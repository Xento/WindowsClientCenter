using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WindowsClientCenter.Intune.Services.Contracts;
using WindowsClientCenter.Intune.Services.Models;
using WindowsClientCenter.Plugin.Abstractions.Contracts;
using WindowsClientCenter.Plugins.DeviceActions.Models;
using Microsoft.Extensions.DependencyInjection;

namespace WindowsClientCenter.Plugins.DeviceActions.ViewModels;

public partial class DeviceServicesViewModel : ObservableObject, IDisposable
{
    private const string DisconnectedStatus = "Client is not connected. Click Connect first.";
    private readonly IWindowsServiceManager _windowsServiceManager;
    private readonly ITargetHostService _targetHostService;
    private readonly IHostStatusLogSink? _hostStatusLogSink;
    private readonly Func<string, string, bool> _confirmAction;
    private readonly DeviceServicesOptions _options;
    private readonly List<WindowsServiceEntry> _allServices = [];
    private string _lastForwardedStatusLine = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _status = DisconnectedStatus;

    [ObservableProperty]
    private string _hostText = string.Empty;

    [ObservableProperty]
    private string _selectedFilter = string.Empty;

    [ObservableProperty]
    private WindowsServiceEntry? _selectedService;

    [ObservableProperty]
    private WindowsServiceStartMode _selectedStartMode = WindowsServiceStartMode.Automatic;

    [ObservableProperty]
    private StartModeOption? _selectedStartModeOption;

    public ObservableCollection<string> FilterOptions { get; } = [];
    public ObservableCollection<WindowsServiceEntry> Services { get; } = [];
    public ObservableCollection<StartModeOption> StartModeOptions { get; } =
    [
        new(WindowsServiceStartMode.Automatic, FormatStartMode(WindowsServiceStartMode.Automatic)),
        new(WindowsServiceStartMode.AutomaticDelayedStart, FormatStartMode(WindowsServiceStartMode.AutomaticDelayedStart)),
        new(WindowsServiceStartMode.Manual, FormatStartMode(WindowsServiceStartMode.Manual)),
        new(WindowsServiceStartMode.Disabled, FormatStartMode(WindowsServiceStartMode.Disabled))
    ];

    public DeviceServicesViewModel(IPluginContext pluginContext, Func<string, string, bool>? confirmAction = null)
    {
        _windowsServiceManager = pluginContext.Services.GetRequiredService<IWindowsServiceManager>();
        _targetHostService = pluginContext.Services.GetRequiredService<ITargetHostService>();
        _hostStatusLogSink = pluginContext.Services.GetService<IHostStatusLogSink>();
        _confirmAction = confirmAction ?? ConfirmViaMessageBox;
        _options = DeviceServicesOptions.FromSettings(GetPluginSettings(pluginContext.Settings));
        foreach (var category in _options.Categories)
        {
            FilterOptions.Add(category.DisplayName);
        }

        SelectedFilter = _options.DefaultCategoryName;
        SelectedStartModeOption = StartModeOptions.FirstOrDefault();
        _targetHostService.HostChanged += OnHostChanged;
    }

    public void Dispose()
    {
        _targetHostService.HostChanged -= OnHostChanged;
    }

    partial void OnStatusChanged(string value)
    {
        ForwardStatusToHost(value);
    }

    partial void OnSelectedFilterChanged(string value)
    {
        ApplyFilter();
    }

    partial void OnSelectedServiceChanged(WindowsServiceEntry? value)
    {
        if (value is not null)
        {
            SelectedStartMode = value.StartMode;
            SelectedStartModeOption = StartModeOptions.FirstOrDefault(option => option.Value == value.StartMode);
        }

        NotifyCommandStates();
    }

    partial void OnSelectedStartModeChanged(WindowsServiceStartMode value)
    {
        ApplySelectedStartModeCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedStartModeOptionChanged(StartModeOption? value)
    {
        if (value is not null && value.Value != SelectedStartMode)
        {
            SelectedStartMode = value.Value;
        }
    }

    partial void OnIsBusyChanged(bool value)
    {
        NotifyCommandStates();
    }

    [RelayCommand]
    public Task RefreshAsync()
    {
        return LoadAsync(CancellationToken.None);
    }

    [RelayCommand(CanExecute = nameof(CanRunSelectedServiceAction))]
    public Task StartSelectedServiceAsync()
    {
        return ExecuteForSelectedServiceAsync(
            "Starting service",
            (host, serviceName, cancellationToken) => _windowsServiceManager.StartServiceAsync(host, serviceName, cancellationToken));
    }

    [RelayCommand(CanExecute = nameof(CanRunSelectedServiceAction))]
    public Task StopSelectedServiceAsync()
    {
        return ExecuteForSelectedServiceAsync(
            "Stopping service",
            (host, serviceName, cancellationToken) => _windowsServiceManager.StopServiceAsync(host, serviceName, cancellationToken));
    }

    [RelayCommand(CanExecute = nameof(CanRunSelectedServiceAction))]
    public Task RestartSelectedServiceAsync()
    {
        return ExecuteForSelectedServiceAsync(
            "Restarting service",
            (host, serviceName, cancellationToken) => _windowsServiceManager.RestartServiceAsync(host, serviceName, cancellationToken));
    }

    [RelayCommand(CanExecute = nameof(CanRunSelectedServiceAction))]
    public async Task KillSelectedServiceAsync()
    {
        var selection = SelectedService;
        var host = _targetHostService.CurrentHost?.Trim() ?? string.Empty;
        if (selection is null || string.IsNullOrWhiteSpace(host))
        {
            return;
        }

        if (!_confirmAction("Confirm service kill", BuildKillConfirmationMessage(host, selection)))
        {
            Status = $"Kill action cancelled for '{selection.DisplayName}'.";
            return;
        }

        await ExecuteForSelectedServiceAsync(
            "Killing service process",
            (currentHost, serviceName, cancellationToken) => _windowsServiceManager.KillServiceProcessAsync(currentHost, serviceName, cancellationToken));
    }

    [RelayCommand(CanExecute = nameof(CanApplySelectedStartMode))]
    public Task ApplySelectedStartModeAsync()
    {
        return ExecuteForSelectedServiceAsync(
            $"Setting start mode to {SelectedStartModeLabel}",
            (host, serviceName, cancellationToken) => _windowsServiceManager.SetStartModeAsync(host, serviceName, SelectedStartMode, cancellationToken));
    }

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        var selection = _targetHostService.CaptureSelection();
        var host = selection.Host?.Trim() ?? string.Empty;
        HostText = host;

        if (string.IsNullOrWhiteSpace(host))
        {
            _allServices.Clear();
            Services.Clear();
            SelectedService = null;
            Status = DisconnectedStatus;
            return;
        }

        IsBusy = true;
        using var linkedCancellationTokenSource = CreateHostLinkedCancellation(selection, cancellationToken);
        var previousServiceName = SelectedService?.ServiceName;
        try
        {
            var snapshot = await _windowsServiceManager.GetServicesAsync(host, linkedCancellationTokenSource.Token);
            if (!EnsureCurrentSelection(selection))
            {
                return;
            }

            _allServices.Clear();
            _allServices.AddRange(snapshot.Services);
            ApplyFilter(previousServiceName);

            if (snapshot.Warnings.Count > 0 && snapshot.Services.Count == 0)
            {
                Status = $"Failed to load services: {string.Join(" ", snapshot.Warnings)}";
            }
            else if (!ResolveSelectedCategory().IncludeAllServices)
            {
                Status = $"Loaded {Services.Count} filtered service(s) from {snapshot.Services.Count} total.";
            }
            else
            {
                Status = $"Loaded {snapshot.Services.Count} service(s).";
            }
        }
        catch (OperationCanceledException) when (selection.CancellationToken.IsCancellationRequested)
        {
            // Host changed while load was running.
        }
        catch (Exception ex)
        {
            _allServices.Clear();
            Services.Clear();
            SelectedService = null;
            Status = $"Failed to load services: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public static string FormatStartMode(WindowsServiceStartMode startMode)
    {
        return startMode switch
        {
            WindowsServiceStartMode.Automatic => "Automatic",
            WindowsServiceStartMode.AutomaticDelayedStart => "Automatic (Delayed Start)",
            WindowsServiceStartMode.Manual => "Manual",
            WindowsServiceStartMode.Disabled => "Disabled",
            _ => startMode.ToString()
        };
    }

    public string SelectedStartModeLabel => FormatStartMode(SelectedStartMode);

    private async Task ExecuteForSelectedServiceAsync(
        string busyText,
        Func<string, string, CancellationToken, ValueTask<DeviceActionResult>> action)
    {
        var selection = _targetHostService.CaptureSelection();
        var host = selection.Host?.Trim() ?? string.Empty;
        var service = SelectedService;
        HostText = host;

        if (string.IsNullOrWhiteSpace(host))
        {
            Status = DisconnectedStatus;
            return;
        }

        if (service is null)
        {
            Status = "Select a service first.";
            return;
        }

        IsBusy = true;
        using var linkedCancellationTokenSource = CreateHostLinkedCancellation(selection, CancellationToken.None);
        try
        {
            Status = $"{busyText} '{service.DisplayName}'...";
            var result = await action(host, service.ServiceName, linkedCancellationTokenSource.Token);
            if (!EnsureCurrentSelection(selection))
            {
                Status = "Operation canceled because the target host changed.";
                return;
            }

            Status = result.Message;
            if (result.Success)
            {
                await LoadAsync(linkedCancellationTokenSource.Token);
            }
        }
        catch (OperationCanceledException) when (selection.CancellationToken.IsCancellationRequested)
        {
            Status = "Operation canceled because the target host changed.";
        }
        catch (Exception ex)
        {
            Status = $"{busyText} failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanRunSelectedServiceAction()
    {
        return !IsBusy &&
               SelectedService is not null &&
               !string.IsNullOrWhiteSpace(_targetHostService.CurrentHost);
    }

    private bool CanApplySelectedStartMode()
    {
        return CanRunSelectedServiceAction() &&
               SelectedService is not null &&
               SelectedService.StartMode != SelectedStartMode;
    }

    private void ApplyFilter(string? preferredServiceName = null)
    {
        var category = ResolveSelectedCategory();
        var filtered = category.IncludeAllServices
            ? _allServices
            : _allServices.Where(service => category.ServiceNameSet.Contains(service.ServiceName));

        var visibleServices = filtered.ToArray();
        Services.Clear();
        foreach (var service in visibleServices)
        {
            Services.Add(service);
        }

        if (visibleServices.Length == 0)
        {
            SelectedService = null;
            SelectedStartModeOption = StartModeOptions.FirstOrDefault();
            return;
        }

        var effectiveSelectedName = preferredServiceName ?? SelectedService?.ServiceName;
        SelectedService = visibleServices.FirstOrDefault(service =>
                string.Equals(service.ServiceName, effectiveSelectedName, StringComparison.OrdinalIgnoreCase))
            ?? visibleServices[0];
    }

    private void NotifyCommandStates()
    {
        StartSelectedServiceCommand.NotifyCanExecuteChanged();
        StopSelectedServiceCommand.NotifyCanExecuteChanged();
        RestartSelectedServiceCommand.NotifyCanExecuteChanged();
        KillSelectedServiceCommand.NotifyCanExecuteChanged();
        ApplySelectedStartModeCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(SelectedStartModeLabel));
    }

    private void OnHostChanged(object? sender, string host)
    {
        HostText = host;
        _ = LoadAsync(CancellationToken.None);
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
        _hostStatusLogSink.Append($"[Device Services] {normalized}");
    }

    private static string BuildKillConfirmationMessage(string host, WindowsServiceEntry service)
    {
        return
            $"Kill the process behind '{service.DisplayName}' ({service.ServiceName}) on '{host}'?{Environment.NewLine}{Environment.NewLine}" +
            "Warning: services may run inside a shared svchost process. Killing the process can also terminate other services in the same process.";
    }

    private static bool ConfirmViaMessageBox(string title, string message)
    {
        return MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
    }

    private DeviceServicesOptions.ServiceFilterCategory ResolveSelectedCategory()
    {
        return _options.Categories.FirstOrDefault(category =>
                   string.Equals(category.DisplayName, SelectedFilter, StringComparison.Ordinal))
               ?? _options.Categories[0];
    }

    private static IReadOnlyDictionary<string, string> GetPluginSettings(IReadOnlyDictionary<string, string> settings)
    {
        const string prefix = "PluginSettings:device-services-view:";
        return settings
            .Where(pair => pair.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(
                pair => pair.Key[prefix.Length..],
                pair => pair.Value,
                StringComparer.OrdinalIgnoreCase);
    }

    public sealed record StartModeOption(WindowsServiceStartMode Value, string Label);
}
