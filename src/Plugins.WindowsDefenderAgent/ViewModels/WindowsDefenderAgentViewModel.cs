using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WindowsClientCenter.Defender.Contracts;
using WindowsClientCenter.Defender.Contracts.Models;
using WindowsClientCenter.Intune.Services.Contracts;
using WindowsClientCenter.Plugin.Abstractions.Contracts;
using WindowsClientCenter.Plugins.WindowsDefenderAgent.Models;
using Microsoft.Extensions.DependencyInjection;

namespace WindowsClientCenter.Plugins.WindowsDefenderAgent.ViewModels;

public partial class WindowsDefenderAgentViewModel : ObservableObject, IDisposable
{
    private const string DefenderLearnUpdatesUrl = "https://learn.microsoft.com/en-us/defender-endpoint/microsoft-defender-antivirus-updates";
    private const string DefenderUpdatesUrl = "https://www.microsoft.com/en-us/wdsi/defenderupdates";
    private const string DefenderReleaseNotesUrl = "https://www.microsoft.com/en-us/wdsi/definitions/antimalware-definition-release-notes";
    private readonly ITargetHostService _targetHostService;
    private readonly IDefenderDiagnosticsService _defenderDiagnosticsService;
    private readonly IHostStatusLogSink? _hostStatusLogSink;
    private readonly Func<string, string, bool> _confirmAction;
    private readonly bool _verboseOperationsEnabled;
    private string _lastForwardedStatusLine = string.Empty;
    private bool _disposed;

    public ObservableCollection<DefenderSettingItem> SettingsEntries { get; } = [];
    public ObservableCollection<DefenderAsrRuleItem> AsrRuleEntries { get; } = [];
    public ObservableCollection<DefenderExclusionItem> ExclusionEntries { get; } = [];
    public ObservableCollection<DefenderDetectionRow> DetectionEntries { get; } = [];
    public ObservableCollection<DefenderDeviceControlSummaryRow> DeviceControlSummaries { get; } = [];
    public ObservableCollection<DefenderDeviceControlEventRow> DeviceControlEvents { get; } = [];

    [ObservableProperty]
    private string _currentHost = string.Empty;

    [ObservableProperty]
    private string _status = "Not connected";

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isActionBusy;

    [ObservableProperty]
    private int _selectedSectionIndex;

    [ObservableProperty]
    private int _daysBack = 90;

    [ObservableProperty]
    private DefenderSnapshot? _snapshot;

    [ObservableProperty]
    private string _healthLevel = "Unknown";

    [ObservableProperty]
    private string _healthSummary = "No data loaded.";

    [ObservableProperty]
    private string _healthColorHex = "#8A8A8A";

    [ObservableProperty]
    private string _managedByText = "Unknown";

    [ObservableProperty]
    private string _isManagedText = "Unknown";

    [ObservableProperty]
    private string _runningModeText = "Unknown";

    [ObservableProperty]
    private string _antivirusEnabledText = "Unknown";

    [ObservableProperty]
    private string _realtimeProtectionEnabledText = "Unknown";

    [ObservableProperty]
    private string _behaviorMonitorEnabledText = "Unknown";

    [ObservableProperty]
    private string _ioavProtectionEnabledText = "Unknown";

    [ObservableProperty]
    private string _onAccessProtectionEnabledText = "Unknown";

    [ObservableProperty]
    private string _nisEnabledText = "Unknown";

    [ObservableProperty]
    private string _tamperProtectionEnabledText = "Unknown";

    [ObservableProperty]
    private string _engineVersionText = "Unknown";

    [ObservableProperty]
    private string _productVersionText = "Unknown";

    [ObservableProperty]
    private string _antivirusSignatureVersionText = "Unknown";

    [ObservableProperty]
    private string _antispywareSignatureVersionText = "Unknown";

    [ObservableProperty]
    private string _nisEngineVersionText = "Unknown";

    [ObservableProperty]
    private string _nisSignatureVersionText = "Unknown";

    [ObservableProperty]
    private string _signatureLastUpdatedText = "Unknown";

    [ObservableProperty]
    private string _signatureAgeText = "Unknown";

    [ObservableProperty]
    private string _microsoftVersionStatusText = "Unknown";

    [ObservableProperty]
    private string _microsoftVersionStatusDetailsText = "Microsoft comparison not loaded.";

    [ObservableProperty]
    private string _microsoftVersionStatusColorHex = "#8A8A8A";

    [ObservableProperty]
    private string _latestSecurityIntelligenceVersionText = "Unknown";

    [ObservableProperty]
    private string _latestEngineVersionText = "Unknown";

    [ObservableProperty]
    private string _latestPlatformVersionText = "Unknown";

    [ObservableProperty]
    private string _latestReleasedText = "Unknown";

    [ObservableProperty]
    private string _productVersionStatusText = "Unknown";

    [ObservableProperty]
    private string _productVersionStatusDetailsText = "Not evaluated.";

    [ObservableProperty]
    private string _productVersionStatusColorHex = "#8A8A8A";

    [ObservableProperty]
    private string _productVersionTooltipText = string.Empty;

    [ObservableProperty]
    private string _engineVersionStatusText = "Unknown";

    [ObservableProperty]
    private string _engineVersionStatusDetailsText = "Not evaluated.";

    [ObservableProperty]
    private string _engineVersionStatusColorHex = "#8A8A8A";

    [ObservableProperty]
    private string _engineVersionTooltipText = string.Empty;

    [ObservableProperty]
    private string _securityIntelligenceStatusText = "Unknown";

    [ObservableProperty]
    private string _securityIntelligenceStatusDetailsText = "Not evaluated.";

    [ObservableProperty]
    private string _securityIntelligenceStatusColorHex = "#8A8A8A";

    [ObservableProperty]
    private string _securityIntelligenceTooltipText = string.Empty;

    [ObservableProperty]
    private string _microsoftBaselineSourceUrlText = DefenderUpdatesUrl;

    [ObservableProperty]
    private string _microsoftReleaseNotesUrlText = DefenderReleaseNotesUrl;

    [ObservableProperty]
    private string _microsoftLearnUpdatesUrlText = DefenderLearnUpdatesUrl;

    [ObservableProperty]
    private string _quickScanStartText = "Unknown";

    [ObservableProperty]
    private string _quickScanEndText = "Unknown";

    [ObservableProperty]
    private string _fullScanStartText = "Unknown";

    [ObservableProperty]
    private string _fullScanEndText = "Unknown";

    [ObservableProperty]
    private string _lastScanText = "Unknown";

    [ObservableProperty]
    private string _activeDetectionsText = "0";

    [ObservableProperty]
    private string _activeHighCriticalDetectionsText = "0";

    [ObservableProperty]
    private string _settingsStatus = "Settings not loaded.";

    [ObservableProperty]
    private string _asrRulesStatus = "ASR rules not loaded.";

    [ObservableProperty]
    private string _exclusionsStatus = "Global exclusions not loaded.";

    [ObservableProperty]
    private string _detectionsStatus = "Detections not loaded.";

    [ObservableProperty]
    private string _deviceControlStatus = "Device Control events not loaded.";

    [ObservableProperty]
    private string _lastRefreshText = "Never";

    public WindowsDefenderAgentViewModel(
        IPluginContext pluginContext,
        string? initialNavigationTarget = null,
        Func<string, string, bool>? confirmAction = null)
    {
        _targetHostService = pluginContext.Services.GetRequiredService<ITargetHostService>();
        _defenderDiagnosticsService = pluginContext.Services.GetRequiredService<IDefenderDiagnosticsService>();
        _hostStatusLogSink = pluginContext.Services.GetService<IHostStatusLogSink>();
        _confirmAction = confirmAction ?? ConfirmViaMessageBox;
        _verboseOperationsEnabled = ResolveVerboseOperationsEnabled(pluginContext);

        CurrentHost = _targetHostService.CurrentHost;
        ApplyNavigationTarget(initialNavigationTarget);

        _targetHostService.HostChanged += OnHostChanged;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(CurrentHost))
        {
            Status = "Client is not connected. Click Connect first.";
            ForwardStatusToHost(Status);
            return;
        }

        await RefreshForSelectedSectionAsync(cancellationToken);
    }

    public void ApplyNavigationTarget(string? navigationTarget)
    {
        SelectedSectionIndex = MapNavigationTargetToSectionIndex(navigationTarget);
    }

    public static int MapNavigationTargetToSectionIndex(string? navigationTarget)
    {
        if (string.IsNullOrWhiteSpace(navigationTarget))
        {
            return 0;
        }

        return navigationTarget.Trim().ToLowerInvariant() switch
        {
            "overview" => 0,
            "protection-status" => 1,
            "versions" => 2,
            "scans" => 3,
            "settings" => 4,
            "detections" => 5,
            "device-control" => 6,
            "asr-rules" => 4,
            _ => 0
        };
    }

    public static DefenderHealthPresentation EvaluateHealthPresentation(DefenderSnapshot snapshot)
    {
        if (snapshot.Protection.AntivirusEnabled == false || snapshot.Protection.RealtimeProtectionEnabled == false)
        {
            return new DefenderHealthPresentation("Red", "Critical protection components are disabled.", "#C62828");
        }

        if (snapshot.ActiveHighOrCriticalDetectionCount > 0 ||
            snapshot.Versions.SignatureAgeHours > snapshot.Versions.SignatureCriticalThresholdHours)
        {
            return new DefenderHealthPresentation("Red", snapshot.HealthSummary, "#C62828");
        }

        var scanReferenceUtc = snapshot.Scans.QuickScanEndUtc
                               ?? snapshot.Scans.FullScanEndUtc
                               ?? snapshot.Scans.LastScanUtc;
        var scanIsOld = scanReferenceUtc.HasValue && scanReferenceUtc.Value < DateTimeOffset.UtcNow.AddDays(-14);

        if (snapshot.Versions.SignatureAgeHours > snapshot.Versions.SignatureWarningThresholdHours ||
            snapshot.ActiveDetectionCount > 0 ||
            snapshot.Protection.TamperProtectionEnabled == false ||
            scanIsOld)
        {
            return new DefenderHealthPresentation("Yellow", snapshot.HealthSummary, "#B07D00");
        }

        return new DefenderHealthPresentation("Green", "Defender status is healthy and current.", "#1A7F37");
    }

    public static DefenderVersionBaselinePresentation EvaluateVersionBaselinePresentation(DefenderSnapshot snapshot)
    {
        var securityIntelligenceMatchesLatest = string.Equals(
            Normalize(snapshot.Versions.AntivirusSignatureVersion),
            Normalize(snapshot.LatestVersionInfo?.SecurityIntelligenceVersion),
            StringComparison.OrdinalIgnoreCase);
        var productStatus = EvaluateComponentVersionStatus(
            "Platform",
            snapshot.Versions.ProductVersion,
            snapshot.LatestVersionInfo?.PlatformVersion,
            snapshot.LatestVersionInfo?.ReleasedAtUtc,
            null);
        var engineStatus = EvaluateComponentVersionStatus(
            "Engine",
            snapshot.Versions.EngineVersion,
            snapshot.LatestVersionInfo?.EngineVersion,
            snapshot.LatestVersionInfo?.ReleasedAtUtc,
            null);
        var intelligenceStatus = EvaluateComponentVersionStatus(
            "Security intelligence",
            snapshot.Versions.AntivirusSignatureVersion,
            snapshot.LatestVersionInfo?.SecurityIntelligenceVersion,
            snapshot.LatestVersionInfo?.ReleasedAtUtc,
            snapshot.Versions.SignatureAgeHours,
            snapshot.Versions.SignatureWarningThresholdHours,
            snapshot.Versions.SignatureCriticalThresholdHours);

        var statuses = new[] { productStatus, engineStatus, intelligenceStatus };
        if (statuses.Any(static status => string.Equals(status.Status, "Outdated", StringComparison.Ordinal)))
        {
            return new DefenderVersionBaselinePresentation(
                "Outdated",
                "At least one Defender component is significantly behind Microsoft baseline.",
                "#C62828");
        }

        if (statuses.Any(static status => string.Equals(status.Status, "Needs update", StringComparison.Ordinal)))
        {
            return new DefenderVersionBaselinePresentation(
                "Needs update",
                "Defender components differ from the latest Microsoft baseline.",
                "#B07D00");
        }

        if (statuses.Any(static status => string.Equals(status.Status, "Unknown", StringComparison.Ordinal)))
        {
            var details = snapshot.LatestVersionInfo is null
                ? "Microsoft baseline lookup not configured."
                : string.IsNullOrWhiteSpace(snapshot.LatestVersionInfo.ErrorMessage)
                    ? "Component version data is incomplete."
                    : $"Microsoft baseline lookup failed: {snapshot.LatestVersionInfo.ErrorMessage}";
            return new DefenderVersionBaselinePresentation(
                "Unknown",
                details,
                "#8A8A8A");
        }

        return new DefenderVersionBaselinePresentation(
            "Current",
            securityIntelligenceMatchesLatest
                ? "Security intelligence, engine, and platform match the latest Microsoft baseline."
                : $"Security intelligence is still within the {snapshot.Versions.SignatureWarningThresholdHours:N0}h freshness threshold.",
            "#1A7F37");
    }

    public static DefenderVersionBaselinePresentation EvaluateComponentVersionStatus(
        string componentName,
        string? localVersion,
        string? latestVersion,
        DateTimeOffset? latestReleasedUtc,
        double? localAgeHours,
        double warningThresholdHours = 36,
        double criticalThresholdHours = 72)
    {
        var local = Normalize(localVersion);
        var latest = Normalize(latestVersion);
        var releaseAgeDays = latestReleasedUtc.HasValue
            ? Math.Max(0, (DateTimeOffset.UtcNow - latestReleasedUtc.Value).TotalDays)
            : -1;

        if (string.Equals(latest, "Unknown", StringComparison.Ordinal))
        {
            return new DefenderVersionBaselinePresentation(
                "Unknown",
                $"{componentName}: Latest Microsoft baseline is unavailable.",
                "#8A8A8A");
        }

        if (string.Equals(local, "Unknown", StringComparison.Ordinal))
        {
            return new DefenderVersionBaselinePresentation(
                "Unknown",
                $"{componentName}: Local version is unavailable.",
                "#8A8A8A");
        }

        if (string.Equals(local, latest, StringComparison.OrdinalIgnoreCase))
        {
            return new DefenderVersionBaselinePresentation(
                "Current",
                $"{componentName}: {local} (matches latest baseline).",
                "#1A7F37");
        }

        var ageHint = releaseAgeDays >= 0
            ? $" Latest baseline is {releaseAgeDays:N0} day(s) old."
            : string.Empty;
        var details = $"{componentName}: local {local}, latest {latest}.{ageHint}";

        if (string.Equals(componentName, "Security intelligence", StringComparison.OrdinalIgnoreCase))
        {
            var ageHours = localAgeHours.GetValueOrDefault(-1);
            if (ageHours > criticalThresholdHours)
            {
                return new DefenderVersionBaselinePresentation("Outdated", details, "#C62828");
            }

            if (ageHours > warningThresholdHours)
            {
                return new DefenderVersionBaselinePresentation("Needs update", details, "#B07D00");
            }

            if (ageHours >= 0)
            {
                var withinThresholdDetails =
                    $"{details} Definitions are {ageHours:N1}h old and still within the {warningThresholdHours:N0}h freshness threshold.";
                return new DefenderVersionBaselinePresentation("Current", withinThresholdDetails, "#1A7F37");
            }

            return new DefenderVersionBaselinePresentation("Needs update", details, "#B07D00");
        }

        if (releaseAgeDays >= 30)
        {
            return new DefenderVersionBaselinePresentation("Outdated", details, "#C62828");
        }

        return new DefenderVersionBaselinePresentation("Needs update", details, "#B07D00");
    }

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    public Task RefreshCurrentSectionAsync()
    {
        return RefreshForSelectedSectionAsync(CancellationToken.None);
    }

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    public async Task RefreshAllAsync()
    {
        if (!CanRefresh())
        {
            return;
        }

        var selection = _targetHostService.CaptureSelection();
        using var linkedCancellationTokenSource = CreateHostLinkedCancellation(selection, CancellationToken.None);
        IsBusy = true;
        var totalTimer = StartVerboseTimer();
        try
        {
            var currentHost = selection.Host;
            var clampedDays = Math.Clamp(DaysBack, 1, 365);
            if (clampedDays != DaysBack)
            {
                DaysBack = clampedDays;
            }

            var snapshotTask = MeasureOperationAsync(
                $"Defender overview for '{currentHost}'",
                () => _defenderDiagnosticsService.GetSnapshotAsync(currentHost, linkedCancellationTokenSource.Token).AsTask());
            var settingsTask = MeasureOperationAsync(
                $"Defender settings for '{currentHost}'",
                () => _defenderDiagnosticsService.GetSettingsAsync(currentHost, linkedCancellationTokenSource.Token).AsTask());
            var detectionsTask = MeasureOperationAsync(
                $"Defender detections ({clampedDays} days) for '{currentHost}'",
                () => _defenderDiagnosticsService.GetDetectionsAsync(currentHost, clampedDays, linkedCancellationTokenSource.Token).AsTask());
            var deviceControlTask = MeasureOperationAsync(
                $"Defender Device Control events ({clampedDays} days) for '{currentHost}'",
                () => _defenderDiagnosticsService.GetDeviceControlEventsAsync(currentHost, clampedDays, linkedCancellationTokenSource.Token).AsTask());

            await Task.WhenAll(snapshotTask, settingsTask, detectionsTask, deviceControlTask);
            EnsureCurrentSelection(selection);

            ApplySnapshot(await snapshotTask);
            ApplySettings(await settingsTask);
            ApplyDetections(await detectionsTask, clampedDays);
            ApplyDeviceControl(await deviceControlTask, clampedDays);

            Status = $"Defender data refreshed for '{CurrentHost}'.";
            LastRefreshText = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            ForwardStatusToHost(Status);
        }
        catch (OperationCanceledException) when (selection.CancellationToken.IsCancellationRequested)
        {
            // Host changed while a background refresh was running.
        }
        finally
        {
            LogVerboseDuration($"Defender refresh for '{CurrentHost}'", totalTimer);
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    public async Task RefreshOverviewAsync()
    {
        await RefreshSnapshotAsync("Refreshing Defender overview...", CancellationToken.None);
    }

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    public async Task RefreshSettingsAsync()
    {
        await RefreshSettingsAsync("Refreshing Defender settings...", CancellationToken.None);
    }

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    public async Task RefreshDetectionsAsync()
    {
        await RefreshDetectionsAsync($"Refreshing Defender detections (last {DaysBack} days)...", CancellationToken.None);
    }

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    public async Task RefreshDeviceControlAsync()
    {
        await RefreshDeviceControlAsync($"Refreshing Defender Device Control events (last {DaysBack} days)...", CancellationToken.None);
    }

    [RelayCommand(CanExecute = nameof(CanExecuteAction))]
    public Task StartQuickScanAsync()
    {
        return ExecuteActionAsync(DefenderActionType.QuickScan, "Quick Scan");
    }

    [RelayCommand(CanExecute = nameof(CanExecuteAction))]
    public Task StartFullScanAsync()
    {
        return ExecuteActionAsync(DefenderActionType.FullScan, "Full Scan");
    }

    [RelayCommand(CanExecute = nameof(CanExecuteAction))]
    public Task StopScanAsync()
    {
        return ExecuteActionAsync(DefenderActionType.StopScan, "Stop Scan");
    }

    [RelayCommand(CanExecute = nameof(CanExecuteAction))]
    public Task UpdateSignaturesAsync()
    {
        return ExecuteActionAsync(DefenderActionType.SignatureUpdate, "Signature Update");
    }

    [RelayCommand(CanExecute = nameof(CanExecuteAction))]
    public Task RestartDefenderServiceAsync()
    {
        return ExecuteActionAsync(DefenderActionType.RestartService, "Restart WinDefend");
    }

    private async Task RefreshForSelectedSectionAsync(CancellationToken cancellationToken)
    {
        var selection = _targetHostService.CaptureSelection();
        switch (SelectedSectionIndex)
        {
            case 4:
                await RefreshSettingsAsync("Refreshing Defender settings...", cancellationToken, selection);
                break;
            case 5:
                await RefreshDetectionsAsync($"Refreshing Defender detections (last {DaysBack} days)...", cancellationToken, selection);
                break;
            case 6:
                await RefreshDeviceControlAsync($"Refreshing Defender Device Control events (last {DaysBack} days)...", cancellationToken, selection);
                break;
            default:
                await RefreshSnapshotAsync("Refreshing Defender overview...", cancellationToken, selection);
                break;
        }
    }

    private async Task RefreshSnapshotAsync(string startStatus, CancellationToken cancellationToken, HostSelection? selectionOverride = null)
    {
        if (!CanRefresh())
        {
            return;
        }

        var selection = selectionOverride ?? _targetHostService.CaptureSelection();
        using var linkedCancellationTokenSource = CreateHostLinkedCancellation(selection, cancellationToken);
        IsBusy = true;
        var totalTimer = StartVerboseTimer();
        try
        {
            Status = startStatus;
            ForwardStatusToHost(Status);

            await RefreshSnapshotCoreAsync(linkedCancellationTokenSource.Token, selection);
            EnsureCurrentSelection(selection);

            Status = $"Defender overview loaded for '{CurrentHost}'.";
            LastRefreshText = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            ForwardStatusToHost(Status);
        }
        catch (OperationCanceledException) when (selection.CancellationToken.IsCancellationRequested)
        {
            // Host changed while a background refresh was running.
        }
        catch (Exception ex)
        {
            Status = BuildFriendlyError(CurrentHost, ex.Message);
            ForwardStatusToHost(Status);
        }
        finally
        {
            LogVerboseDuration($"Defender overview refresh for '{CurrentHost}'", totalTimer);
            IsBusy = false;
        }
    }

    private async Task RefreshSettingsAsync(string startStatus, CancellationToken cancellationToken, HostSelection? selectionOverride = null)
    {
        if (!CanRefresh())
        {
            return;
        }

        var selection = selectionOverride ?? _targetHostService.CaptureSelection();
        using var linkedCancellationTokenSource = CreateHostLinkedCancellation(selection, cancellationToken);
        IsBusy = true;
        var totalTimer = StartVerboseTimer();
        try
        {
            Status = startStatus;
            ForwardStatusToHost(Status);
            await RefreshSettingsCoreAsync(linkedCancellationTokenSource.Token, selection);
            EnsureCurrentSelection(selection);
            LastRefreshText = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        }
        catch (OperationCanceledException) when (selection.CancellationToken.IsCancellationRequested)
        {
            // Host changed while a background refresh was running.
        }
        catch (Exception ex)
        {
            Status = BuildFriendlyError(CurrentHost, ex.Message);
            SettingsStatus = Status;
            ForwardStatusToHost(Status);
        }
        finally
        {
            LogVerboseDuration($"Defender settings refresh for '{CurrentHost}'", totalTimer);
            IsBusy = false;
        }
    }

    private async Task RefreshDetectionsAsync(string startStatus, CancellationToken cancellationToken, HostSelection? selectionOverride = null)
    {
        if (!CanRefresh())
        {
            return;
        }

        var selection = selectionOverride ?? _targetHostService.CaptureSelection();
        using var linkedCancellationTokenSource = CreateHostLinkedCancellation(selection, cancellationToken);
        IsBusy = true;
        var totalTimer = StartVerboseTimer();
        try
        {
            Status = startStatus;
            ForwardStatusToHost(Status);
            await RefreshDetectionsCoreAsync(linkedCancellationTokenSource.Token, selection);
            EnsureCurrentSelection(selection);
            LastRefreshText = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        }
        catch (OperationCanceledException) when (selection.CancellationToken.IsCancellationRequested)
        {
            // Host changed while a background refresh was running.
        }
        catch (Exception ex)
        {
            Status = BuildFriendlyError(CurrentHost, ex.Message);
            DetectionsStatus = Status;
            ForwardStatusToHost(Status);
        }
        finally
        {
            LogVerboseDuration($"Defender detections refresh for '{CurrentHost}'", totalTimer);
            IsBusy = false;
        }
    }

    private async Task RefreshDeviceControlAsync(string startStatus, CancellationToken cancellationToken, HostSelection? selectionOverride = null)
    {
        if (!CanRefresh())
        {
            return;
        }

        var selection = selectionOverride ?? _targetHostService.CaptureSelection();
        using var linkedCancellationTokenSource = CreateHostLinkedCancellation(selection, cancellationToken);
        IsBusy = true;
        var totalTimer = StartVerboseTimer();
        try
        {
            Status = startStatus;
            ForwardStatusToHost(Status);
            await RefreshDeviceControlCoreAsync(linkedCancellationTokenSource.Token, selection);
            EnsureCurrentSelection(selection);
            LastRefreshText = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        }
        catch (OperationCanceledException) when (selection.CancellationToken.IsCancellationRequested)
        {
            // Host changed while a background refresh was running.
        }
        catch (Exception ex)
        {
            Status = BuildFriendlyError(CurrentHost, ex.Message);
            DeviceControlStatus = Status;
            ForwardStatusToHost(Status);
        }
        finally
        {
            LogVerboseDuration($"Defender Device Control refresh for '{CurrentHost}'", totalTimer);
            IsBusy = false;
        }
    }

    private async Task RefreshSnapshotCoreAsync(CancellationToken cancellationToken, HostSelection selection)
    {
        var snapshot = await MeasureOperationAsync(
            $"Defender overview for '{selection.Host}'",
            () => _defenderDiagnosticsService.GetSnapshotAsync(selection.Host, cancellationToken).AsTask());
        EnsureCurrentSelection(selection);
        ApplySnapshot(snapshot);
    }

    private async Task RefreshSettingsCoreAsync(CancellationToken cancellationToken, HostSelection selection)
    {
        var settings = await MeasureOperationAsync(
            $"Defender settings for '{selection.Host}'",
            () => _defenderDiagnosticsService.GetSettingsAsync(selection.Host, cancellationToken).AsTask());
        EnsureCurrentSelection(selection);
        ApplySettings(settings);
    }

    private async Task RefreshDetectionsCoreAsync(CancellationToken cancellationToken, HostSelection selection)
    {
        var clampedDays = Math.Clamp(DaysBack, 1, 365);
        if (clampedDays != DaysBack)
        {
            DaysBack = clampedDays;
        }

        var detections = await MeasureOperationAsync(
            $"Defender detections ({clampedDays} days) for '{selection.Host}'",
            () => _defenderDiagnosticsService.GetDetectionsAsync(selection.Host, clampedDays, cancellationToken).AsTask());
        EnsureCurrentSelection(selection);
        ApplyDetections(detections, clampedDays);
    }

    private async Task RefreshDeviceControlCoreAsync(CancellationToken cancellationToken, HostSelection selection)
    {
        var clampedDays = Math.Clamp(DaysBack, 1, 365);
        if (clampedDays != DaysBack)
        {
            DaysBack = clampedDays;
        }

        var snapshot = await MeasureOperationAsync(
            $"Defender Device Control events ({clampedDays} days) for '{selection.Host}'",
            () => _defenderDiagnosticsService.GetDeviceControlEventsAsync(selection.Host, clampedDays, cancellationToken).AsTask());
        EnsureCurrentSelection(selection);
        ApplyDeviceControl(snapshot, clampedDays);
    }

    private void ApplySnapshot(DefenderSnapshot snapshot)
    {
        Snapshot = snapshot;

        var health = EvaluateHealthPresentation(snapshot);
        HealthLevel = health.Level;
        HealthSummary = health.Summary;
        HealthColorHex = health.ColorHex;

        ManagedByText = string.IsNullOrWhiteSpace(snapshot.ManagedBy) ? "Unknown" : snapshot.ManagedBy;
        IsManagedText = snapshot.IsManaged ? "Yes" : "No";

        RunningModeText = Normalize(snapshot.Protection.RunningMode);
        AntivirusEnabledText = ToStatus(snapshot.Protection.AntivirusEnabled);
        RealtimeProtectionEnabledText = ToStatus(snapshot.Protection.RealtimeProtectionEnabled);
        BehaviorMonitorEnabledText = ToStatus(snapshot.Protection.BehaviorMonitorEnabled);
        IoavProtectionEnabledText = ToStatus(snapshot.Protection.IoavProtectionEnabled);
        OnAccessProtectionEnabledText = ToStatus(snapshot.Protection.OnAccessProtectionEnabled);
        NisEnabledText = ToStatus(snapshot.Protection.NisEnabled);
        TamperProtectionEnabledText = ToStatus(snapshot.Protection.TamperProtectionEnabled);

        EngineVersionText = Normalize(snapshot.Versions.EngineVersion);
        ProductVersionText = Normalize(snapshot.Versions.ProductVersion);
        AntivirusSignatureVersionText = Normalize(snapshot.Versions.AntivirusSignatureVersion);
        AntispywareSignatureVersionText = Normalize(snapshot.Versions.AntispywareSignatureVersion);
        NisEngineVersionText = Normalize(snapshot.Versions.NisEngineVersion);
        NisSignatureVersionText = Normalize(snapshot.Versions.NisSignatureVersion);
        SignatureLastUpdatedText = FormatDateTime(snapshot.Versions.SignatureLastUpdatedUtc);
        SignatureAgeText = snapshot.Versions.SignatureAgeHours < 0
            ? "Unknown"
            : $"{snapshot.Versions.SignatureAgeHours:N1}h";

        LatestSecurityIntelligenceVersionText = Normalize(snapshot.LatestVersionInfo?.SecurityIntelligenceVersion);
        LatestEngineVersionText = Normalize(snapshot.LatestVersionInfo?.EngineVersion);
        LatestPlatformVersionText = Normalize(snapshot.LatestVersionInfo?.PlatformVersion);
        LatestReleasedText = FormatDateTime(snapshot.LatestVersionInfo?.ReleasedAtUtc);
        MicrosoftBaselineSourceUrlText = string.IsNullOrWhiteSpace(snapshot.LatestVersionInfo?.SourceUrl)
            ? DefenderUpdatesUrl
            : snapshot.LatestVersionInfo.SourceUrl.Trim();
        MicrosoftReleaseNotesUrlText = string.IsNullOrWhiteSpace(snapshot.LatestVersionInfo?.ReleaseNotesUrl)
            ? DefenderReleaseNotesUrl
            : snapshot.LatestVersionInfo.ReleaseNotesUrl.Trim();
        MicrosoftLearnUpdatesUrlText = DefenderLearnUpdatesUrl;

        var versionBaseline = EvaluateVersionBaselinePresentation(snapshot);
        MicrosoftVersionStatusText = versionBaseline.Status;
        MicrosoftVersionStatusDetailsText = versionBaseline.Details;
        MicrosoftVersionStatusColorHex = versionBaseline.ColorHex;

        var productVersionStatus = EvaluateComponentVersionStatus(
            "Platform",
            snapshot.Versions.ProductVersion,
            snapshot.LatestVersionInfo?.PlatformVersion,
            snapshot.LatestVersionInfo?.ReleasedAtUtc,
            null);
        ProductVersionStatusText = productVersionStatus.Status;
        ProductVersionStatusDetailsText = productVersionStatus.Details;
        ProductVersionStatusColorHex = productVersionStatus.ColorHex;
        ProductVersionTooltipText = BuildOutdatedVersionTooltip(
            "Platform",
            snapshot.Versions.ProductVersion,
            snapshot.LatestVersionInfo?.PlatformVersion,
            productVersionStatus.Status);

        var engineVersionStatus = EvaluateComponentVersionStatus(
            "Engine",
            snapshot.Versions.EngineVersion,
            snapshot.LatestVersionInfo?.EngineVersion,
            snapshot.LatestVersionInfo?.ReleasedAtUtc,
            null);
        EngineVersionStatusText = engineVersionStatus.Status;
        EngineVersionStatusDetailsText = engineVersionStatus.Details;
        EngineVersionStatusColorHex = engineVersionStatus.ColorHex;
        EngineVersionTooltipText = BuildOutdatedVersionTooltip(
            "Engine",
            snapshot.Versions.EngineVersion,
            snapshot.LatestVersionInfo?.EngineVersion,
            engineVersionStatus.Status);

        var securityIntelligenceStatus = EvaluateComponentVersionStatus(
            "Security intelligence",
            snapshot.Versions.AntivirusSignatureVersion,
            snapshot.LatestVersionInfo?.SecurityIntelligenceVersion,
            snapshot.LatestVersionInfo?.ReleasedAtUtc,
            snapshot.Versions.SignatureAgeHours,
            snapshot.Versions.SignatureWarningThresholdHours,
            snapshot.Versions.SignatureCriticalThresholdHours);
        SecurityIntelligenceStatusText = securityIntelligenceStatus.Status;
        SecurityIntelligenceStatusDetailsText = securityIntelligenceStatus.Details;
        SecurityIntelligenceStatusColorHex = securityIntelligenceStatus.ColorHex;
        SecurityIntelligenceTooltipText = BuildOutdatedVersionTooltip(
            "Security intelligence",
            snapshot.Versions.AntivirusSignatureVersion,
            snapshot.LatestVersionInfo?.SecurityIntelligenceVersion,
            securityIntelligenceStatus.Status);

        QuickScanStartText = FormatDateTime(snapshot.Scans.QuickScanStartUtc);
        QuickScanEndText = FormatDateTime(snapshot.Scans.QuickScanEndUtc);
        FullScanStartText = FormatDateTime(snapshot.Scans.FullScanStartUtc);
        FullScanEndText = FormatDateTime(snapshot.Scans.FullScanEndUtc);
        LastScanText = FormatDateTime(snapshot.Scans.LastScanUtc);

        ActiveDetectionsText = snapshot.ActiveDetectionCount.ToString();
        ActiveHighCriticalDetectionsText = snapshot.ActiveHighOrCriticalDetectionCount.ToString();
    }

    private void ApplySettings(DefenderSettingsSnapshot settings)
    {
        SettingsEntries.Clear();
        foreach (var setting in settings.Settings.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
        {
            SettingsEntries.Add(setting);
        }

        AsrRuleEntries.Clear();
        foreach (var asrRule in (settings.AsrRules ?? [])
                     .OrderBy(item => item.RuleName, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(item => item.RuleId, StringComparer.OrdinalIgnoreCase))
        {
            AsrRuleEntries.Add(asrRule);
        }

        ExclusionEntries.Clear();
        foreach (var exclusion in settings.Exclusions ?? [])
        {
            ExclusionEntries.Add(exclusion);
        }

        SettingsStatus = $"Loaded {SettingsEntries.Count} settings from {settings.Source}.";
        AsrRulesStatus = AsrRuleEntries.Count == 0
            ? "No ASR rules reported."
            : $"Loaded {AsrRuleEntries.Count} ASR rule(s) with resolved names/actions.";
        ExclusionsStatus = ExclusionEntries.Count == 0
            ? "No global exclusions reported."
            : $"Loaded {ExclusionEntries.Count} global exclusion(s).";
        Status = SettingsStatus;
        ForwardStatusToHost(SettingsStatus);
    }

    private void ApplyDetections(IReadOnlyList<DefenderDetectionEntry> detections, int clampedDays)
    {
        DetectionEntries.Clear();
        foreach (var detection in detections)
        {
            DetectionEntries.Add(new DefenderDetectionRow(detection));
        }

        DetectionsStatus = $"Loaded {DetectionEntries.Count} detection entries (last {clampedDays} days).";
        Status = DetectionsStatus;
        ForwardStatusToHost(DetectionsStatus);
    }

    private void ApplyDeviceControl(DefenderDeviceControlSnapshot snapshot, int clampedDays)
    {
        DeviceControlSummaries.Clear();
        foreach (var summary in snapshot.DeviceSummaries)
        {
            DeviceControlSummaries.Add(new DefenderDeviceControlSummaryRow(summary));
        }

        DeviceControlEvents.Clear();
        foreach (var entry in snapshot.Events)
        {
            DeviceControlEvents.Add(new DefenderDeviceControlEventRow(entry));
        }

        DeviceControlStatus = $"Loaded {DeviceControlEvents.Count} Device Control event(s), {DeviceControlSummaries.Count} blocked device summary row(s) (last {clampedDays} days).";
        Status = DeviceControlStatus;
        ForwardStatusToHost(DeviceControlStatus);
    }

    private async Task ExecuteActionAsync(DefenderActionType actionType, string actionDisplayName)
    {
        if (!CanExecuteAction())
        {
            return;
        }

        var host = CurrentHost;
        if (!_confirmAction("Confirm Defender action", $"Execute '{actionDisplayName}' on '{host}'?"))
        {
            Status = $"Action '{actionDisplayName}' cancelled.";
            ForwardStatusToHost(Status);
            return;
        }

        IsActionBusy = true;
        var selection = _targetHostService.CaptureSelection();
        using var linkedCancellationTokenSource = CreateHostLinkedCancellation(selection, CancellationToken.None);
        var totalTimer = StartVerboseTimer();
        try
        {
            Status = $"Executing '{actionDisplayName}' on '{host}'...";
            ForwardStatusToHost(Status);

            var result = await MeasureOperationAsync(
                $"Defender action '{actionDisplayName}' on '{host}'",
                () => _defenderDiagnosticsService.ExecuteActionAsync(host, new DefenderActionRequest(actionType), linkedCancellationTokenSource.Token).AsTask());
            EnsureCurrentSelection(selection);
            if (!result.Success)
            {
                Status = BuildFriendlyError(host, result.Message);
                ForwardStatusToHost(Status);
                return;
            }

            Status = result.Message;
            ForwardStatusToHost(Status);

            await RefreshSnapshotCoreAsync(linkedCancellationTokenSource.Token, selection);
            LastRefreshText = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        }
        catch (OperationCanceledException) when (selection.CancellationToken.IsCancellationRequested)
        {
            Status = "Operation canceled because the target host changed.";
            ForwardStatusToHost(Status);
        }
        catch (Exception ex)
        {
            Status = BuildFriendlyError(host, ex.Message);
            ForwardStatusToHost(Status);
        }
        finally
        {
            LogVerboseDuration($"Defender action '{actionDisplayName}' for '{host}'", totalTimer);
            IsActionBusy = false;
        }
    }

    private bool CanRefresh()
    {
        return !_disposed && !IsBusy && !string.IsNullOrWhiteSpace(CurrentHost);
    }

    private bool CanExecuteAction()
    {
        return !_disposed && !IsBusy && !IsActionBusy && !string.IsNullOrWhiteSpace(CurrentHost);
    }

    partial void OnIsBusyChanged(bool value)
    {
        NotifyCommandStates();
    }

    partial void OnIsActionBusyChanged(bool value)
    {
        NotifyCommandStates();
    }

    partial void OnCurrentHostChanged(string value)
    {
        NotifyCommandStates();
    }

    private void NotifyCommandStates()
    {
        RefreshCurrentSectionCommand.NotifyCanExecuteChanged();
        RefreshAllCommand.NotifyCanExecuteChanged();
        RefreshOverviewCommand.NotifyCanExecuteChanged();
        RefreshSettingsCommand.NotifyCanExecuteChanged();
        RefreshDetectionsCommand.NotifyCanExecuteChanged();
        RefreshDeviceControlCommand.NotifyCanExecuteChanged();
        StartQuickScanCommand.NotifyCanExecuteChanged();
        StartFullScanCommand.NotifyCanExecuteChanged();
        StopScanCommand.NotifyCanExecuteChanged();
        UpdateSignaturesCommand.NotifyCanExecuteChanged();
        RestartDefenderServiceCommand.NotifyCanExecuteChanged();
    }

    private void OnHostChanged(object? sender, string host)
    {
        CurrentHost = host;
        ClearTransientData();
        if (string.IsNullOrWhiteSpace(host))
        {
            Status = "Client is not connected. Click Connect first.";
            ForwardStatusToHost(Status);
            return;
        }

        _ = RefreshSnapshotAsync($"Refreshing Defender overview for '{host}'...", CancellationToken.None, _targetHostService.CaptureSelection());
    }

    private CancellationTokenSource CreateHostLinkedCancellation(HostSelection selection, CancellationToken cancellationToken)
    {
        return cancellationToken.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(selection.CancellationToken, cancellationToken)
            : CancellationTokenSource.CreateLinkedTokenSource(selection.CancellationToken);
    }

    private void EnsureCurrentSelection(HostSelection selection)
    {
        if (!_targetHostService.IsCurrent(selection))
        {
            throw new OperationCanceledException(selection.CancellationToken);
        }
    }

    private void ClearTransientData()
    {
        SettingsEntries.Clear();
        AsrRuleEntries.Clear();
        ExclusionEntries.Clear();
        DetectionEntries.Clear();
        DeviceControlSummaries.Clear();
        DeviceControlEvents.Clear();
        SettingsStatus = "Settings not loaded.";
        AsrRulesStatus = "ASR rules not loaded.";
        ExclusionsStatus = "Global exclusions not loaded.";
        DetectionsStatus = "Detections not loaded.";
        DeviceControlStatus = "Device Control events not loaded.";
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
        _hostStatusLogSink.Append($"[Defender] {normalized}");
    }

    private async Task<T> MeasureOperationAsync<T>(string operationName, Func<Task<T>> operation)
    {
        if (!_verboseOperationsEnabled)
        {
            return await operation();
        }

        var timer = Stopwatch.StartNew();
        try
        {
            var result = await operation();
            LogVerboseOperation($"{operationName} completed in {timer.ElapsedMilliseconds} ms.");
            return result;
        }
        catch (Exception ex)
        {
            LogVerboseOperation($"{operationName} failed after {timer.ElapsedMilliseconds} ms: {ex.Message}");
            throw;
        }
    }

    private void LogVerboseDuration(string operationName, Stopwatch? timer)
    {
        if (timer is null)
        {
            return;
        }

        LogVerboseOperation($"{operationName} completed in {timer.ElapsedMilliseconds} ms.");
    }

    private void LogVerboseOperation(string message)
    {
        if (!_verboseOperationsEnabled || _hostStatusLogSink is null || string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        _hostStatusLogSink.Append($"[Defender][Verbose] {message.Trim()}");
    }

    private Stopwatch? StartVerboseTimer()
    {
        return _verboseOperationsEnabled ? Stopwatch.StartNew() : null;
    }

    private static bool ResolveVerboseOperationsEnabled(IPluginContext pluginContext)
    {
        var enabled = false;
        if (pluginContext.Settings.TryGetValue("VerboseOperations", out var globalSetting) &&
            bool.TryParse(globalSetting, out var globalEnabled))
        {
            enabled = globalEnabled;
        }

        if (pluginContext.Settings.TryGetValue("verboseOperations", out var pluginSetting) &&
            bool.TryParse(pluginSetting, out var pluginEnabled))
        {
            enabled = pluginEnabled;
        }

        return enabled;
    }

    private static string BuildFriendlyError(string host, string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return $"Defender operation failed for '{host}'.";
        }

        var normalized = message.Trim();
        var isConnectionIssue =
            normalized.Contains("test-wsman", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("winrm", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("cannot connect", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("rpc server is unavailable", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("timed out", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("no such host is known", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("name resolution", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("zugriff verweigert", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("verbindung", StringComparison.OrdinalIgnoreCase);

        if (!isConnectionIssue)
        {
            return normalized;
        }

        if (normalized.Contains("access is denied", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("zugriff verweigert", StringComparison.OrdinalIgnoreCase))
        {
            return $"Connection to '{host}' failed: Access denied.";
        }

        if (normalized.Contains("timed out", StringComparison.OrdinalIgnoreCase))
        {
            return $"Connection to '{host}' failed: Timeout while connecting to remote host.";
        }

        if (normalized.Contains("no such host is known", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("name resolution", StringComparison.OrdinalIgnoreCase))
        {
            return $"Connection to '{host}' failed: Host name could not be resolved.";
        }

        return $"Connection to '{host}' failed: WinRM connection failed.";
    }

    private static bool ConfirmViaMessageBox(string title, string message)
    {
        return MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
    }

    private static string FormatDateTime(DateTimeOffset? value)
    {
        return value?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "Unknown";
    }

    private static string ToStatus(bool? value)
    {
        return value switch
        {
            true => "Enabled",
            false => "Disabled",
            _ => "Unknown"
        };
    }

    private static string Normalize(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "Unknown" : value.Trim();
    }

    private static string BuildOutdatedVersionTooltip(string componentName, string? localVersion, string? latestVersion, string? componentStatus)
    {
        if (!string.Equals(componentStatus, "Needs update", StringComparison.Ordinal) &&
            !string.Equals(componentStatus, "Outdated", StringComparison.Ordinal))
        {
            return string.Empty;
        }

        var local = Normalize(localVersion);
        var latest = Normalize(latestVersion);
        return $"Latest {componentName} baseline: {latest} (local: {local})";
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _targetHostService.HostChanged -= OnHostChanged;
    }
}
