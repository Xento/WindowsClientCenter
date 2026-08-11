using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WindowsClientCenter.Intune.Services.Contracts;
using WindowsClientCenter.Intune.Services.Models;
using WindowsClientCenter.Plugin.Abstractions.Contracts;
using WindowsClientCenter.Plugins.DeviceActions.Models;
using Microsoft.Extensions.DependencyInjection;

namespace WindowsClientCenter.Plugins.DeviceActions.ViewModels;

public partial class DeviceProcessesViewModel : ObservableObject, IDisposable
{
    private const string DisconnectedStatus = "Client is not connected. Click Connect first.";
    private readonly IWindowsProcessManager _windowsProcessManager;
    private readonly ITargetHostService _targetHostService;
    private readonly IHostStatusLogSink? _hostStatusLogSink;
    private readonly Func<string, string, bool> _confirmAction;
    private readonly DeviceProcessesOptions _options;
    private readonly DispatcherTimer _refreshTimer;
    private ProcessSnapshot? _previousSnapshot;
    private string _lastForwardedStatusLine = string.Empty;
    private bool _autoRefreshInProgress;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _status = DisconnectedStatus;

    [ObservableProperty]
    private string _hostText = string.Empty;

    [ObservableProperty]
    private DeviceProcessPresentation? _selectedProcess;

    [ObservableProperty]
    private ProcessViewMode _selectedViewMode = ProcessViewMode.List;

    [ObservableProperty]
    private ViewModeOption? _selectedViewModeOption;

    [ObservableProperty]
    private RefreshIntervalOption? _selectedRefreshIntervalOption;

    public ObservableCollection<DeviceProcessPresentation> Processes { get; } = [];
    public ObservableCollection<DeviceProcessTreeNode> ProcessTreeRoots { get; } = [];
    public ObservableCollection<ViewModeOption> ViewModeOptions { get; } =
    [
        new(ProcessViewMode.List, "List"),
        new(ProcessViewMode.Tree, "Tree")
    ];
    public ObservableCollection<RefreshIntervalOption> RefreshIntervalOptions { get; } = [];

    public DeviceProcessesViewModel(IPluginContext pluginContext, Func<string, string, bool>? confirmAction = null)
    {
        _windowsProcessManager = pluginContext.Services.GetRequiredService<IWindowsProcessManager>();
        _targetHostService = pluginContext.Services.GetRequiredService<ITargetHostService>();
        _hostStatusLogSink = pluginContext.Services.GetService<IHostStatusLogSink>();
        _confirmAction = confirmAction ?? ConfirmViaMessageBox;
        _options = DeviceProcessesOptions.FromSettings(GetPluginSettings(pluginContext.Settings));

        foreach (var interval in _options.RefreshIntervalsSeconds)
        {
            RefreshIntervalOptions.Add(new RefreshIntervalOption(interval, interval == 0 ? "Off" : $"{interval}s"));
        }

        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _refreshTimer.Tick += RefreshTimer_OnTick;

        SelectedViewMode = _options.DefaultViewMode;
        SelectedViewModeOption = ViewModeOptions.FirstOrDefault(option => option.Mode == _options.DefaultViewMode)
            ?? ViewModeOptions.FirstOrDefault();
        SelectedRefreshIntervalOption = RefreshIntervalOptions.FirstOrDefault(option => option.Seconds == _options.DefaultRefreshIntervalSeconds)
            ?? RefreshIntervalOptions.FirstOrDefault();
        ApplyRefreshTimerState();
        _targetHostService.HostChanged += OnHostChanged;
    }

    public bool IsListMode => SelectedViewMode == ProcessViewMode.List;
    public bool IsTreeMode => SelectedViewMode == ProcessViewMode.Tree;

    public void Dispose()
    {
        _targetHostService.HostChanged -= OnHostChanged;
        _refreshTimer.Stop();
        _refreshTimer.Tick -= RefreshTimer_OnTick;
    }

    partial void OnStatusChanged(string value)
    {
        ForwardStatusToHost(value);
    }

    partial void OnSelectedViewModeChanged(ProcessViewMode value)
    {
        OnPropertyChanged(nameof(IsListMode));
        OnPropertyChanged(nameof(IsTreeMode));
    }

    partial void OnSelectedViewModeOptionChanged(ViewModeOption? value)
    {
        if (value is not null && value.Mode != SelectedViewMode)
        {
            SelectedViewMode = value.Mode;
        }
    }

    partial void OnSelectedRefreshIntervalOptionChanged(RefreshIntervalOption? value)
    {
        ApplyRefreshTimerState();
        KillSelectedProcessCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedProcessChanged(DeviceProcessPresentation? value)
    {
        KillSelectedProcessCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsBusyChanged(bool value)
    {
        KillSelectedProcessCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    public Task RefreshAsync()
    {
        return LoadAsync(CancellationToken.None, isAutoRefresh: false);
    }

    [RelayCommand(CanExecute = nameof(CanKillSelectedProcess))]
    public async Task KillSelectedProcessAsync()
    {
        var selection = SelectedProcess;
        var host = _targetHostService.CurrentHost?.Trim() ?? string.Empty;
        if (selection is null || string.IsNullOrWhiteSpace(host))
        {
            return;
        }

        if (!_confirmAction("Confirm process kill", BuildKillConfirmationMessage(host, selection)))
        {
            Status = $"Kill action cancelled for process {selection.Name} ({selection.ProcessId}).";
            return;
        }

        var targetSelection = _targetHostService.CaptureSelection();
        IsBusy = true;
        using var linkedCancellationTokenSource = CreateHostLinkedCancellation(targetSelection, CancellationToken.None);
        try
        {
            Status = $"Killing process {selection.Name} ({selection.ProcessId})...";
            var result = await _windowsProcessManager.KillProcessAsync(host, selection.ProcessId, linkedCancellationTokenSource.Token);
            if (!EnsureCurrentSelection(targetSelection))
            {
                Status = "Operation canceled because the target host changed.";
                return;
            }

            Status = result.Message;
            if (result.Success)
            {
                await LoadAsync(linkedCancellationTokenSource.Token, isAutoRefresh: false);
            }
        }
        catch (OperationCanceledException) when (targetSelection.CancellationToken.IsCancellationRequested)
        {
            Status = "Operation canceled because the target host changed.";
        }
        catch (Exception ex)
        {
            Status = $"Killing process failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task LoadAsync(CancellationToken cancellationToken, bool isAutoRefresh = false)
    {
        var selection = _targetHostService.CaptureSelection();
        var host = selection.Host?.Trim() ?? string.Empty;
        HostText = host;

        if (string.IsNullOrWhiteSpace(host))
        {
            _previousSnapshot = null;
            Processes.Clear();
            ProcessTreeRoots.Clear();
            SelectedProcess = null;
            Status = DisconnectedStatus;
            return;
        }

        if (!isAutoRefresh)
        {
            IsBusy = true;
        }

        using var linkedCancellationTokenSource = CreateHostLinkedCancellation(selection, cancellationToken);
        var previousProcessId = SelectedProcess?.ProcessId;
        try
        {
            var snapshot = await _windowsProcessManager.GetProcessesAsync(host, linkedCancellationTokenSource.Token);
            if (!EnsureCurrentSelection(selection))
            {
                return;
            }

            ApplySnapshot(snapshot, previousProcessId);
            Status = snapshot.Warnings.Count > 0 && snapshot.Processes.Count == 0
                ? $"Failed to load processes: {string.Join(" ", snapshot.Warnings)}"
                : isAutoRefresh
                    ? $"Auto-refreshed {snapshot.Processes.Count} process(es)."
                    : $"Loaded {snapshot.Processes.Count} process(es).";
        }
        catch (OperationCanceledException) when (selection.CancellationToken.IsCancellationRequested)
        {
            Status = "Operation canceled because the target host changed.";
        }
        catch (Exception ex)
        {
            Processes.Clear();
            ProcessTreeRoots.Clear();
            SelectedProcess = null;
            Status = $"Failed to load processes: {ex.Message}";
        }
        finally
        {
            _autoRefreshInProgress = false;
            if (!isAutoRefresh)
            {
                IsBusy = false;
            }
        }
    }

    public void SelectProcessFromTreeNode(DeviceProcessTreeNode? node)
    {
        SelectedProcess = node?.Process;
    }

    private bool CanKillSelectedProcess()
    {
        return !IsBusy &&
               SelectedProcess is not null &&
               !string.IsNullOrWhiteSpace(_targetHostService.CurrentHost);
    }

    private void ApplySnapshot(ProcessSnapshot snapshot, int? preferredProcessId)
    {
        var presentations = snapshot.Processes
            .Select(process => DeviceProcessPresentation.FromSnapshotEntry(process, CalculateCpuPercent(snapshot, process)))
            .OrderByDescending(static process => process.SortCpuPercent)
            .ThenBy(static process => process.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static process => process.ProcessId)
            .ToArray();

        Processes.Clear();
        foreach (var process in presentations)
        {
            Processes.Add(process);
        }

        var treeRoots = BuildTree(presentations);
        ProcessTreeRoots.Clear();
        foreach (var root in treeRoots)
        {
            ProcessTreeRoots.Add(root);
        }

        SelectedProcess = presentations.FirstOrDefault(process => process.ProcessId == preferredProcessId)
            ?? presentations.FirstOrDefault();
        _previousSnapshot = snapshot;
    }

    private double? CalculateCpuPercent(ProcessSnapshot currentSnapshot, ProcessSnapshotEntry currentProcess)
    {
        var previousSnapshot = _previousSnapshot;
        if (previousSnapshot is null)
        {
            return null;
        }

        var elapsedSeconds = (currentSnapshot.CapturedAtUtc - previousSnapshot.CapturedAtUtc).TotalSeconds;
        if (elapsedSeconds <= 0)
        {
            return null;
        }

        var previousProcess = previousSnapshot.Processes.FirstOrDefault(process =>
            process.ProcessId == currentProcess.ProcessId &&
            Nullable.Equals(process.StartTimeUtc, currentProcess.StartTimeUtc));
        if (previousProcess is null)
        {
            return null;
        }

        var logicalProcessorCount = currentSnapshot.LogicalProcessorCount > 0 ? currentSnapshot.LogicalProcessorCount : 1;
        var cpuDelta = currentProcess.CpuTimeSeconds - previousProcess.CpuTimeSeconds;
        if (cpuDelta <= 0)
        {
            return 0;
        }

        return Math.Max(0, Math.Min(100 * logicalProcessorCount, cpuDelta / elapsedSeconds / logicalProcessorCount * 100));
    }

    private static IReadOnlyList<DeviceProcessTreeNode> BuildTree(IReadOnlyList<DeviceProcessPresentation> processes)
    {
        var nodesByProcessId = processes.ToDictionary(process => process.ProcessId, process => new DeviceProcessTreeNode(process));
        var roots = new List<DeviceProcessTreeNode>();

        foreach (var node in nodesByProcessId.Values)
        {
            if (node.ParentProcessId.HasValue &&
                nodesByProcessId.TryGetValue(node.ParentProcessId.Value, out var parent))
            {
                parent.Children.Add(node);
            }
            else
            {
                roots.Add(node);
            }
        }

        SortTreeNodes(roots);
        return roots;
    }

    private static void SortTreeNodes(IList<DeviceProcessTreeNode> nodes)
    {
        var sorted = nodes
            .OrderByDescending(static node => node.Process.SortCpuPercent)
            .ThenBy(static node => node.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static node => node.ProcessId)
            .ToArray();

        nodes.Clear();
        foreach (var node in sorted)
        {
            SortTreeNodes(node.Children);
            nodes.Add(node);
        }
    }

    private void ApplyRefreshTimerState()
    {
        var selectedInterval = SelectedRefreshIntervalOption?.Seconds ?? 0;
        _refreshTimer.Stop();
        _refreshTimer.Interval = TimeSpan.FromSeconds(Math.Max(1, selectedInterval));
        if (selectedInterval > 0)
        {
            _refreshTimer.Start();
        }
    }

    private async Task RefreshFromTimerAsync()
    {
        if (_autoRefreshInProgress || IsBusy || (SelectedRefreshIntervalOption?.Seconds ?? 0) <= 0)
        {
            return;
        }

        _autoRefreshInProgress = true;
        await LoadAsync(CancellationToken.None, isAutoRefresh: true);
    }

    private void RefreshTimer_OnTick(object? sender, EventArgs e)
    {
        _ = RefreshFromTimerAsync();
    }

    private void OnHostChanged(object? sender, string host)
    {
        HostText = host;
        _ = LoadAsync(CancellationToken.None, isAutoRefresh: false);
    }

    private CancellationTokenSource CreateHostLinkedCancellation(HostSelection selection, CancellationToken cancellationToken)
    {
        return cancellationToken.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(selection.CancellationToken, cancellationToken)
            : CancellationTokenSource.CreateLinkedTokenSource(selection.CancellationToken);
    }

    private bool EnsureCurrentSelection(HostSelection selection)
    {
        return _targetHostService.IsCurrent(selection);
    }

    private void ForwardStatusToHost(string message)
    {
        if (_hostStatusLogSink is null || string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        var normalized = message.Trim();
        if (string.Equals(_lastForwardedStatusLine, normalized, StringComparison.Ordinal))
        {
            return;
        }

        _lastForwardedStatusLine = normalized;
        _hostStatusLogSink.Append($"[Device Processes] {normalized}");
    }

    private static string BuildKillConfirmationMessage(string host, DeviceProcessPresentation process)
    {
        return
            $"Kill process '{process.Name}' ({process.ProcessId}) on '{host}'?{Environment.NewLine}{Environment.NewLine}" +
            "This forcefully terminates the selected process and any unsaved work in that process will be lost.";
    }

    private static bool ConfirmViaMessageBox(string title, string message)
    {
        return MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
    }

    private static IReadOnlyDictionary<string, string> GetPluginSettings(IReadOnlyDictionary<string, string> settings)
    {
        const string prefix = "PluginSettings:device-processes-view:";
        return settings
            .Where(pair => pair.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(
                pair => pair.Key[prefix.Length..],
                pair => pair.Value,
                StringComparer.OrdinalIgnoreCase);
    }

    public sealed record RefreshIntervalOption(int Seconds, string Label);
    public sealed record ViewModeOption(ProcessViewMode Mode, string Label);
}
