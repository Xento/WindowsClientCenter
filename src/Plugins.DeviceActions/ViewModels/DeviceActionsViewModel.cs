using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WindowsClientCenter.Intune.Services.Contracts;
using WindowsClientCenter.Intune.Services.Models;
using WindowsClientCenter.Plugin.Abstractions.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace WindowsClientCenter.Plugins.DeviceActions.ViewModels;

public partial class DeviceActionsViewModel : ObservableObject, IDisposable
{
    private const string DisconnectedStatus = "Client is not connected. Click Connect first.";
    private readonly ILocalDeviceActionService _localDeviceActionService;
    private readonly ITargetHostService _targetHostService;
    private readonly IHostStatusLogSink? _hostStatusLogSink;
    private string _lastForwardedStatusLine = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _status = DisconnectedStatus;

    [ObservableProperty]
    private PowerStateSnapshot? _snapshot;

    [ObservableProperty]
    private PowerSchemeSnapshot? _selectedPowerScheme;

    [ObservableProperty]
    private string _hostText = string.Empty;

    [ObservableProperty]
    private string _activeSchemeText = "Unknown";

    [ObservableProperty]
    private string _warningsText = "No warnings.";

    public ObservableCollection<PowerSchemeSnapshot> PowerSchemes { get; } = [];

    public DeviceActionsViewModel(IPluginContext pluginContext)
    {
        _localDeviceActionService = pluginContext.Services.GetRequiredService<ILocalDeviceActionService>();
        _targetHostService = pluginContext.Services.GetRequiredService<ITargetHostService>();
        _hostStatusLogSink = pluginContext.Services.GetService<IHostStatusLogSink>();
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

    [RelayCommand]
    public Task RefreshAsync()
    {
        return LoadAsync(CancellationToken.None);
    }

    [RelayCommand]
    public async Task ShutdownAsync()
    {
        await RunActionAsync("Shutting down device", cancellationToken => _localDeviceActionService.ShutdownAsync(_targetHostService.CurrentHost, cancellationToken));
    }

    [RelayCommand]
    public async Task RestartAsync()
    {
        await RunActionAsync("Restarting device", cancellationToken => _localDeviceActionService.RestartAsync(_targetHostService.CurrentHost, cancellationToken));
    }

    [RelayCommand]
    public async Task LogoffAsync()
    {
        await RunActionAsync("Logging off current session", cancellationToken => _localDeviceActionService.LogoffAsync(_targetHostService.CurrentHost, cancellationToken));
    }

    [RelayCommand]
    public async Task LockWorkstationAsync()
    {
        await RunActionAsync("Locking workstation", cancellationToken => _localDeviceActionService.LockWorkstationAsync(_targetHostService.CurrentHost, cancellationToken));
    }

    [RelayCommand]
    public async Task ApplySelectedPowerSchemeAsync()
    {
        var selectedScheme = SelectedPowerScheme;
        if (selectedScheme is null)
        {
            Status = "Select a power scheme first.";
            return;
        }

        await RunActionAsync(
            $"Setting power scheme to {selectedScheme.Name}",
            cancellationToken => _localDeviceActionService.SetPowerSchemeAsync(_targetHostService.CurrentHost, selectedScheme.SchemeId, cancellationToken),
            refreshAfterSuccess: true);
    }

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        var selection = _targetHostService.CaptureSelection();
        var host = selection.Host?.Trim() ?? string.Empty;
        HostText = host;

        if (string.IsNullOrWhiteSpace(host))
        {
            Snapshot = null;
            PowerSchemes.Clear();
            SelectedPowerScheme = null;
            ActiveSchemeText = "Unknown";
            WarningsText = "No warnings.";
            Status = DisconnectedStatus;
            return;
        }

        IsBusy = true;
        using var linkedCancellationTokenSource = CreateHostLinkedCancellation(selection, cancellationToken);
        try
        {
            var snapshot = await _localDeviceActionService.GetPowerStateAsync(host, linkedCancellationTokenSource.Token);
            if (!EnsureCurrentSelection(selection))
            {
                return;
            }

            Snapshot = snapshot;

            PowerSchemes.Clear();
            foreach (var scheme in snapshot.Schemes)
            {
                PowerSchemes.Add(scheme);
            }

            SelectedPowerScheme = PowerSchemes.FirstOrDefault(scheme => scheme.IsActive) ?? PowerSchemes.FirstOrDefault();
            ActiveSchemeText = string.IsNullOrWhiteSpace(snapshot.ActiveSchemeName)
                ? (string.IsNullOrWhiteSpace(snapshot.ActiveSchemeId) ? "Unknown" : snapshot.ActiveSchemeId)
                : $"{snapshot.ActiveSchemeName} ({snapshot.ActiveSchemeId})";
            WarningsText = snapshot.Warnings.Count == 0
                ? "No warnings."
                : string.Join(Environment.NewLine, snapshot.Warnings);
            Status = snapshot.Schemes.Count == 0
                ? "Power settings loaded, but no schemes were returned."
                : $"Loaded {snapshot.Schemes.Count} power scheme(s).";
        }
        catch (OperationCanceledException) when (selection.CancellationToken.IsCancellationRequested)
        {
            // Host changed while a background load was running.
        }
        catch (Exception ex)
        {
            Snapshot = null;
            PowerSchemes.Clear();
            SelectedPowerScheme = null;
            ActiveSchemeText = "Unknown";
            WarningsText = ex.Message;
            Status = $"Failed to load power settings: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RunActionAsync(
        string busyText,
        Func<CancellationToken, ValueTask<DeviceActionResult>> action,
        bool refreshAfterSuccess = false)
    {
        var selection = _targetHostService.CaptureSelection();
        var host = selection.Host?.Trim() ?? string.Empty;
        HostText = host;

        if (string.IsNullOrWhiteSpace(host))
        {
            Status = DisconnectedStatus;
            return;
        }

        IsBusy = true;
        using var linkedCancellationTokenSource = CreateHostLinkedCancellation(selection, CancellationToken.None);
        try
        {
            Status = busyText + "...";
            var result = await action(linkedCancellationTokenSource.Token);
            if (!EnsureCurrentSelection(selection))
            {
                Status = "Operation canceled because the target host changed.";
                return;
            }

            Status = result.Message;
            if (result.Success && refreshAfterSuccess)
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
        _hostStatusLogSink.Append($"[Device Actions] {normalized}");
    }
}
