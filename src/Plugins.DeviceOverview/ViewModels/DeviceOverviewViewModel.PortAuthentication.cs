using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WindowsClientCenter.Intune.Services.Contracts;
using WindowsClientCenter.Intune.Services.Models;

namespace WindowsClientCenter.Plugins.DeviceOverview.ViewModels;

public partial class DeviceOverviewViewModel
{
    private Task? _portAuthenticationLoadTask;
    private bool _portAuthenticationLoadAttempted;

    [ObservableProperty]
    private PortAuthenticationSnapshot? _portAuthenticationSnapshot;

    [ObservableProperty]
    private bool _isPortAuthenticationLoading;

    [ObservableProperty]
    private bool _isPortAuthenticationActionRunning;

    [ObservableProperty]
    private string _portAuthenticationSectionStatusText = "Open the Port Authentication view or refresh it to load health details.";

    [ObservableProperty]
    private string _portAuthenticationActionStatusText = "No remediation action has been executed yet.";

    [ObservableProperty]
    private bool _confirmPortAuthenticationDisruptiveActions;

    [ObservableProperty]
    private PortAuthenticationProfileEntry? _selectedPortAuthenticationProfile;

    public bool IsPortAuthenticationVisible => _options.PortAuthentication.Enabled &&
                                               (ShowPortAuthenticationSummary ||
                                                ShowPortAuthenticationChecks ||
                                                ShowPortAuthenticationProfiles ||
                                                ShowPortAuthenticationCertificates ||
                                                ShowPortAuthenticationEvents ||
                                                ShowPortAuthenticationRemediation);

    public bool ShowPortAuthenticationSummary => _options.PortAuthentication.Enabled && _options.PortAuthentication.ShowSummary;

    public bool ShowPortAuthenticationChecks => _options.PortAuthentication.Enabled && _options.PortAuthentication.ShowChecks;

    public bool ShowPortAuthenticationProfiles => _options.PortAuthentication.Enabled && _options.PortAuthentication.ShowProfiles;

    public bool ShowPortAuthenticationCertificates => _options.PortAuthentication.Enabled && _options.PortAuthentication.ShowCertificates;

    public bool ShowPortAuthenticationEvents => _options.PortAuthentication.Enabled && _options.PortAuthentication.ShowEvents;

    public bool ShowPortAuthenticationRemediation => _options.PortAuthentication.Enabled && _options.PortAuthentication.ShowRemediation;

    public string PortAuthenticationOverallStatusText => PortAuthenticationSnapshot?.OverallStatusText ?? "Not loaded";

    public string PortAuthenticationOverallStatusLevel => PortAuthenticationSnapshot?.OverallStatusLevel ?? "Unknown";

    public string PortAuthenticationOverallDetailText => PortAuthenticationSnapshot?.OverallDetailText ?? "Port authentication data has not been loaded.";

    public string PortAuthenticationApplicabilityText => PortAuthenticationSnapshot?.ApplicabilityText ?? "Unknown";

    public string PortAuthenticationFqdnText => string.IsNullOrWhiteSpace(PortAuthenticationSnapshot?.Fqdn) ? "-" : PortAuthenticationSnapshot.Fqdn;

    public string PortAuthenticationActiveInterfaceName => string.IsNullOrWhiteSpace(PortAuthenticationSnapshot?.ActiveInterfaceName)
        ? "-"
        : PortAuthenticationSnapshot.ActiveInterfaceName;

    public string PortAuthenticationActiveInterfaceDescription => string.IsNullOrWhiteSpace(PortAuthenticationSnapshot?.ActiveInterfaceDescription)
        ? "-"
        : PortAuthenticationSnapshot.ActiveInterfaceDescription;

    public string PortAuthenticationAuthenticationStateText => PortAuthenticationSnapshot?.AuthenticationStateText ?? "Unknown";

    public string PortAuthenticationTracingModeText => PortAuthenticationSnapshot?.TracingModeText ?? "Unknown";

    public string PortAuthenticationLastSuccessfulAuthenticationText => PortAuthenticationSnapshot?.LastSuccessfulAuthenticationText
        ?? "No successful wired authentication event found.";

    public bool HasPortAuthenticationActiveInterface => !string.IsNullOrWhiteSpace(PortAuthenticationSnapshot?.ActiveInterfaceName);

    public bool HasPortAuthenticationProfiles => PortAuthenticationSnapshot?.Profiles.Count > 0;

    public bool CanRefreshPortAuthentication => !IsPortAuthenticationLoading && !IsPortAuthenticationActionRunning;

    public bool CanRestartPortAuthenticationServices => !IsPortAuthenticationLoading && !IsPortAuthenticationActionRunning;

    public bool CanEnablePortAuthenticationTracing => !IsPortAuthenticationLoading && !IsPortAuthenticationActionRunning;

    public bool CanDisablePortAuthenticationTracing => !IsPortAuthenticationLoading && !IsPortAuthenticationActionRunning;

    public bool CanEnablePortAuthenticationAutoconfig => !IsPortAuthenticationLoading && !IsPortAuthenticationActionRunning && HasPortAuthenticationActiveInterface;

    public bool CanRestartPortAuthenticationAdapter => !IsPortAuthenticationLoading &&
                                                       !IsPortAuthenticationActionRunning &&
                                                       HasPortAuthenticationActiveInterface &&
                                                       ConfirmPortAuthenticationDisruptiveActions;

    public bool CanReapplyPortAuthenticationProfile => !IsPortAuthenticationLoading &&
                                                       !IsPortAuthenticationActionRunning &&
                                                       SelectedPortAuthenticationProfile is not null &&
                                                       ConfirmPortAuthenticationDisruptiveActions;

    partial void OnPortAuthenticationSnapshotChanged(PortAuthenticationSnapshot? value)
    {
        if (value is null)
        {
            SelectedPortAuthenticationProfile = null;
        }
        else if (SelectedPortAuthenticationProfile is null ||
                 !value.Profiles.Any(profile => string.Equals(profile.Name, SelectedPortAuthenticationProfile.Name, StringComparison.OrdinalIgnoreCase)))
        {
            SelectedPortAuthenticationProfile = value.Profiles.FirstOrDefault();
        }

        RaisePortAuthenticationStateChanged();
    }

    partial void OnIsPortAuthenticationLoadingChanged(bool value)
    {
        RaisePortAuthenticationStateChanged();
    }

    partial void OnIsPortAuthenticationActionRunningChanged(bool value)
    {
        RaisePortAuthenticationStateChanged();
    }

    partial void OnConfirmPortAuthenticationDisruptiveActionsChanged(bool value)
    {
        RaisePortAuthenticationStateChanged();
    }

    partial void OnSelectedPortAuthenticationProfileChanged(PortAuthenticationProfileEntry? value)
    {
        OnPropertyChanged(nameof(CanReapplyPortAuthenticationProfile));
    }

    [RelayCommand]
    private Task RefreshPortAuthenticationAsync()
    {
        return EnsurePortAuthenticationLoadedAsync(_targetHostService.CaptureSelection(), CancellationToken.None, forceRefresh: true);
    }

    [RelayCommand]
    private Task RestartPortAuthenticationServicesAsync()
    {
        return ExecutePortAuthenticationActionAsync(
            "Restarting 802.1X services",
            (host, cancellationToken) => _localIntuneActionService.RestartPortAuthenticationServicesAsync(host, cancellationToken),
            refreshAfterAction: true);
    }

    [RelayCommand]
    private Task RestartPortAuthenticationAdapterAsync()
    {
        if (!CanRestartPortAuthenticationAdapter)
        {
            return Task.CompletedTask;
        }

        return ExecutePortAuthenticationActionAsync(
            "Restarting wired adapter",
            (host, cancellationToken) => _localIntuneActionService.RestartPortAuthenticationAdapterAsync(host, PortAuthenticationSnapshot!.ActiveInterfaceName, cancellationToken),
            refreshAfterAction: true);
    }

    [RelayCommand]
    private Task EnablePortAuthenticationTracingAsync()
    {
        return ExecutePortAuthenticationActionAsync(
            "Enabling port authentication tracing",
            (host, cancellationToken) => _localIntuneActionService.SetPortAuthenticationTracingAsync(host, PortAuthenticationTracingMode.Enabled, cancellationToken),
            refreshAfterAction: true);
    }

    [RelayCommand]
    private Task DisablePortAuthenticationTracingAsync()
    {
        return ExecutePortAuthenticationActionAsync(
            "Disabling port authentication tracing",
            (host, cancellationToken) => _localIntuneActionService.SetPortAuthenticationTracingAsync(host, PortAuthenticationTracingMode.Disabled, cancellationToken),
            refreshAfterAction: true);
    }

    [RelayCommand]
    private Task EnablePortAuthenticationAutoconfigAsync()
    {
        if (!CanEnablePortAuthenticationAutoconfig)
        {
            return Task.CompletedTask;
        }

        return ExecutePortAuthenticationActionAsync(
            "Enabling wired autoconfig",
            (host, cancellationToken) => _localIntuneActionService.SetPortAuthenticationAutoconfigAsync(host, PortAuthenticationSnapshot!.ActiveInterfaceName, true, cancellationToken),
            refreshAfterAction: true);
    }

    [RelayCommand]
    private Task ReapplyPortAuthenticationProfileAsync()
    {
        if (!CanReapplyPortAuthenticationProfile || SelectedPortAuthenticationProfile is null)
        {
            return Task.CompletedTask;
        }

        return ExecutePortAuthenticationActionAsync(
            "Reapplying wired profile",
            (host, cancellationToken) => _localIntuneActionService.ReapplyPortAuthenticationProfileAsync(
                host,
                SelectedPortAuthenticationProfile.Name,
                string.IsNullOrWhiteSpace(SelectedPortAuthenticationProfile.InterfaceName) ? null : SelectedPortAuthenticationProfile.InterfaceName,
                cancellationToken),
            refreshAfterAction: true);
    }

    private async Task EnsurePortAuthenticationLoadedAsync(HostSelection selection, CancellationToken cancellationToken, bool forceRefresh)
    {
        if (!IsPortAuthenticationVisible || string.IsNullOrWhiteSpace(selection.Host))
        {
            return;
        }

        if (!forceRefresh)
        {
            if (_portAuthenticationLoadTask is not null)
            {
                await _portAuthenticationLoadTask;
                return;
            }

            if (_portAuthenticationLoadAttempted && PortAuthenticationSnapshot is not null)
            {
                return;
            }
        }

        var host = selection.Host;
        using var linkedCancellationTokenSource = CreateHostLinkedCancellation(selection, cancellationToken);
        var loadTask = LoadPortAuthenticationAsync(host, linkedCancellationTokenSource.Token);
        _portAuthenticationLoadTask = loadTask;

        try
        {
            await loadTask;
            EnsureCurrentSelection(selection);
        }
        catch (OperationCanceledException) when (selection.CancellationToken.IsCancellationRequested)
        {
            // Host changed while the port authentication view was loading.
        }
        finally
        {
            if (ReferenceEquals(_portAuthenticationLoadTask, loadTask))
            {
                _portAuthenticationLoadTask = null;
            }
        }
    }

    private async Task LoadPortAuthenticationAsync(string host, CancellationToken cancellationToken)
    {
        _portAuthenticationLoadAttempted = true;
        IsPortAuthenticationLoading = true;
        PortAuthenticationSectionStatusText = $"Loading port authentication health for '{host}'...";
        var busyOwnerId = BeginBusyState(host);
        UpdateBusyState(host, busyOwnerId, ["Port authentication"]);

        try
        {
            var snapshot = await _localIntuneDiagnosticsService.GetPortAuthenticationSnapshotAsync(host, cancellationToken);
            PortAuthenticationSnapshot = snapshot;
            PortAuthenticationSectionStatusText = snapshot is null
                ? $"Port authentication data is not available for '{host}'."
                : $"Loaded port authentication health for '{host}'.";
            ForwardStatusToHost(PortAuthenticationSectionStatusText);
        }
        catch (Exception ex)
        {
            PortAuthenticationSnapshot = null;
            var diagnostics = ex.Message?.Trim() ?? "Unknown error.";
            var firstLine = diagnostics
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault() ?? diagnostics;
            PortAuthenticationSectionStatusText = $"Port authentication health check failed for '{host}': {firstLine}";
            ForwardStatusToHost(PortAuthenticationSectionStatusText);
            if (!string.Equals(firstLine, diagnostics, StringComparison.Ordinal))
            {
                ForwardStatusToHost($"Port authentication diagnostics for '{host}': {diagnostics}");
            }
        }
        finally
        {
            IsPortAuthenticationLoading = false;
            ClearBusyState(busyOwnerId);
        }
    }

    private void ResetPortAuthenticationState()
    {
        _portAuthenticationLoadAttempted = false;
        _portAuthenticationLoadTask = null;
        PortAuthenticationSnapshot = null;
        IsPortAuthenticationLoading = false;
        IsPortAuthenticationActionRunning = false;
        ConfirmPortAuthenticationDisruptiveActions = false;
        SelectedPortAuthenticationProfile = null;
        PortAuthenticationSectionStatusText = "Open the Port Authentication view or refresh it to load health details.";
        PortAuthenticationActionStatusText = "No remediation action has been executed yet.";
    }

    private async Task ExecutePortAuthenticationActionAsync(
        string operationText,
        Func<string, CancellationToken, ValueTask<LocalIntuneActionResult>> action,
        bool refreshAfterAction)
    {
        if (IsPortAuthenticationActionRunning)
        {
            return;
        }

        var selection = _targetHostService.CaptureSelection();
        var host = selection.Host;
        if (string.IsNullOrWhiteSpace(host))
        {
            PortAuthenticationActionStatusText = DisconnectedStatus;
            return;
        }

        using var linkedCancellationTokenSource = CreateHostLinkedCancellation(selection, CancellationToken.None);
        var busyOwnerId = BeginBusyState(host);
        UpdateBusyState(host, busyOwnerId, [operationText]);
        IsPortAuthenticationActionRunning = true;
        PortAuthenticationActionStatusText = $"{operationText} on '{host}'...";

        try
        {
            var result = await action(host, linkedCancellationTokenSource.Token);
            EnsureCurrentSelection(selection);
            PortAuthenticationActionStatusText = result.Success
                ? result.Message
                : $"{operationText} failed: {result.Message}";
            ForwardStatusToHost($"Port authentication action for '{host}': {PortAuthenticationActionStatusText}");

            if (result.Success && refreshAfterAction)
            {
                await EnsurePortAuthenticationLoadedAsync(selection, linkedCancellationTokenSource.Token, forceRefresh: true);
            }
        }
        catch (OperationCanceledException) when (selection.CancellationToken.IsCancellationRequested)
        {
            PortAuthenticationActionStatusText = $"Port authentication action for '{host}' was canceled because the host selection changed.";
        }
        catch (Exception ex)
        {
            PortAuthenticationActionStatusText = $"{operationText} failed: {ex.Message}";
            ForwardStatusToHost($"Port authentication action failed for '{host}': {ex.Message}");
        }
        finally
        {
            IsPortAuthenticationActionRunning = false;
            ClearBusyState(busyOwnerId);
        }
    }

    private void RaisePortAuthenticationStateChanged()
    {
        OnPropertyChanged(nameof(PortAuthenticationOverallStatusText));
        OnPropertyChanged(nameof(PortAuthenticationOverallStatusLevel));
        OnPropertyChanged(nameof(PortAuthenticationOverallDetailText));
        OnPropertyChanged(nameof(PortAuthenticationApplicabilityText));
        OnPropertyChanged(nameof(PortAuthenticationFqdnText));
        OnPropertyChanged(nameof(PortAuthenticationActiveInterfaceName));
        OnPropertyChanged(nameof(PortAuthenticationActiveInterfaceDescription));
        OnPropertyChanged(nameof(PortAuthenticationAuthenticationStateText));
        OnPropertyChanged(nameof(PortAuthenticationTracingModeText));
        OnPropertyChanged(nameof(PortAuthenticationLastSuccessfulAuthenticationText));
        OnPropertyChanged(nameof(HasPortAuthenticationActiveInterface));
        OnPropertyChanged(nameof(HasPortAuthenticationProfiles));
        OnPropertyChanged(nameof(CanRefreshPortAuthentication));
        OnPropertyChanged(nameof(CanRestartPortAuthenticationServices));
        OnPropertyChanged(nameof(CanEnablePortAuthenticationTracing));
        OnPropertyChanged(nameof(CanDisablePortAuthenticationTracing));
        OnPropertyChanged(nameof(CanEnablePortAuthenticationAutoconfig));
        OnPropertyChanged(nameof(CanRestartPortAuthenticationAdapter));
        OnPropertyChanged(nameof(CanReapplyPortAuthenticationProfile));
    }
}
