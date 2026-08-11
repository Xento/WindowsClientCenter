using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WindowsClientCenter.Intune.Services.Contracts;
using WindowsClientCenter.Intune.Services.Models;
using WindowsClientCenter.Plugin.Abstractions.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;

namespace WindowsClientCenter.Plugins.IntuneAgent.ViewModels;

public partial class IntuneAgentViewModel : ObservableObject, IDisposable
{
    private readonly IServiceProvider _services;
    private readonly ITargetHostService _targetHostService;
    private readonly ILocalIntuneDiagnosticsService _localIntuneDiagnosticsService;
    private readonly ILocalIntuneEnrollmentService _localIntuneEnrollmentService;
    private readonly ILocalIntuneActionService _localIntuneActionService;
    private ICloudManagedDeviceService? _cloudManagedDeviceService;
    private IAuthService? _authService;
    private readonly IHostStatusLogSink? _hostStatusLogSink;
    private readonly ILogger _logger;
    private readonly bool _verboseOperationsEnabled;
    private readonly SemaphoreSlim _localGate = new(1, 1);
    private readonly SemaphoreSlim _cloudGate = new(1, 1);
    private readonly List<MdmEventAnalysisEntry> _allMdmEvents = [];
    private readonly List<ImeLogTimelineEntry> _allImeTimelineEntries = [];
    private readonly List<ImeApplicationStatusEntry> _allImeApplications = [];
    private int _requestedMdmEventCount;
    private const string DisconnectedStatus = "Client is not connected. Click Connect first.";
    private const string CompanyPortalAppId = "032937f7-c5a4-48a3-bcf6-ad78a2b0373b";
    private const string SystemIdentityId = "00000000-0000-0000-0000-000000000000";
    private const string CommunityScriptAutopilotDiagnostics = "Autopilot Diagnostics (Community)";
    private const string CommunityScriptImeQuickStatus = "IME Quick Status";
    private const string IgnoredAppIdPrefix = "000000";

    private bool _disposed;
    private bool _initialized;
    private bool _isRefreshingImeTimelineComponentOptions;
    private bool _isRefreshingImeApplicationFlowOptions;
    private string _imeTimelineFingerprint = string.Empty;
    private string _imeHighlightedFlowKey = string.Empty;
    private string _lastForwardedMessage = string.Empty;
    private string _lastRegistryProbeHost = string.Empty;
    private DateTimeOffset _lastRegistryProbeAt = DateTimeOffset.MinValue;
    private int _suppressStatusLogDepth;
    private bool _cloudAvailabilityChecked;
    private int _longRunningLocalActionDepth;

    public IntuneAgentViewModel(IPluginContext pluginContext, string? initialNavigationTarget = null)
    {
        _services = pluginContext.Services;
        _targetHostService = pluginContext.Services.GetRequiredService<ITargetHostService>();
        _localIntuneDiagnosticsService = pluginContext.Services.GetRequiredService<ILocalIntuneDiagnosticsService>();
        _localIntuneEnrollmentService = pluginContext.Services.GetRequiredService<ILocalIntuneEnrollmentService>();
        _localIntuneActionService = pluginContext.Services.GetRequiredService<ILocalIntuneActionService>();
        _hostStatusLogSink = pluginContext.Services.GetService<IHostStatusLogSink>();
        _logger = ResolveLogger(pluginContext.Services);
        _verboseOperationsEnabled = ResolveVerboseOperationsEnabled(pluginContext);

        CurrentHost = _targetHostService.CurrentHost;
        MdmReportDirectory = Path.Combine(GetExportDirectory(), "mdm-report");
        PolicyResultReportDirectory = MdmReportDirectory;
        PolicyResultExportDirectory = Path.Combine(GetExportDirectory(), "policy-result");
        SupportOutputDirectory = Path.Combine(GetExportDirectory(), "support-logs");
        BundleRootDirectory = Path.Combine(GetExportDirectory(), "bundle");
        BundleZipPath = Path.Combine(GetExportDirectory(), "bundle.zip");
        LocalActionEvidence.CollectionChanged += (_, _) => RefreshLocalActionOutputText();
        ApplyNavigationTarget(initialNavigationTarget);
        _targetHostService.HostChanged += OnHostChanged;
    }
    
    public ObservableCollection<MdmEventAnalysisEntry> MdmEvents { get; } = [];
    public ObservableCollection<ImeLogTimelineEntry> ImeTimelineEntries { get; } = [];
    public ObservableCollection<ImeFlowSummaryEntry> ImeFlowSummaries { get; } = [];
    public ObservableCollection<string> ImeTimelineComponentOptions { get; } = new(["All"]);
    public ObservableCollection<string> ImeApplicationFlowOptions { get; } = new(["All"]);
    public ObservableCollection<ImeApplicationStatusEntry> ImeApplications { get; } = [];
    public ObservableCollection<ImeApplicationIdentityStatusEntry> SelectedImeApplicationIdentityStatuses { get; } = [];
    public ObservableCollection<NameValueItem> LocalActionEvidence { get; } = [];
    public IReadOnlyList<string> CommunityScriptOptions { get; } = [CommunityScriptAutopilotDiagnostics, CommunityScriptImeQuickStatus];
    public IReadOnlyList<int> MdmEventLoadOptions { get; } = [50, 100, 150, 200, 300, 400];
    public IReadOnlyList<int> ImeTimelineLoadOptions { get; } = [200, 400, 800, 1200, 2000];
    public IReadOnlyList<string> ImeFlowTypeOptions { get; } = ["All", "Policy Sync", "Download", "Execution", "Reporting", "Status Service", "Informational"];
    public IReadOnlyList<string> ImeAppStatusOptions { get; } = ["All", "Failed", "PartiallyInstalled", "Installed", "NotInstalled", "InProgress", "RetryPending", "Detected", "Unknown"];

    [ObservableProperty]
    private string _currentHost = string.Empty;

    [ObservableProperty]
    private int _selectedSectionIndex;

    [ObservableProperty]
    private string _overallStatus = "Ready.";

    [ObservableProperty]
    private string _diagnosticsStatus = "No diagnostics loaded.";

    [ObservableProperty]
    private string _enrollmentStatusText = "No enrollment status loaded.";

    [ObservableProperty]
    private string _mdmEventsStatus = "No MDM events loaded.";

    [ObservableProperty]
    private string _imeLogsStatus = "No IME log timeline loaded.";

    [ObservableProperty]
    private string _imeAppsStatus = "No IME application list loaded.";

    [ObservableProperty]
    private string _cloudStatus = "Cloud sign-in required.";

    [ObservableProperty]
    private bool _isCloudConfigured = true;

    [ObservableProperty]
    private string _cloudConfigurationWarning = string.Empty;

    [ObservableProperty]
    private string _confirmReenrollInput = string.Empty;

    [ObservableProperty]
    private LocalIntuneSnapshot? _snapshot;

    [ObservableProperty]
    private EnrollmentStatus? _enrollmentStatus;

    [ObservableProperty]
    private EnrollmentRepairPreview? _reenrollPreview;

    [ObservableProperty]
    private AuthSession? _authSession;

    [ObservableProperty]
    private CloudManagedDeviceSummary? _cloudDevice;

    [ObservableProperty]
    private MdmEventAnalysisEntry? _selectedMdmEvent;

    [ObservableProperty]
    private ImeLogTimelineEntry? _selectedImeLogEntry;

    [ObservableProperty]
    private ImeFlowSummaryEntry? _selectedImeFlowSummary;

    [ObservableProperty]
    private ImeApplicationStatusEntry? _selectedImeApplication;

    [ObservableProperty]
    private bool _isLocalBusy;

    [ObservableProperty]
    private bool _isCloudBusy;

    [ObservableProperty]
    private bool _isLongRunningLocalAction;

    [ObservableProperty]
    private string _longRunningLocalActionLabel = string.Empty;

    [ObservableProperty]
    private string _mdmEventIdFilter = string.Empty;

    [ObservableProperty]
    private bool _showMdmCriticalEvents = true;

    [ObservableProperty]
    private bool _showMdmErrorEvents = true;

    [ObservableProperty]
    private bool _showMdmWarningEvents = true;

    [ObservableProperty]
    private bool _showMdmInfoEvents;

    [ObservableProperty]
    private int _mdmEventLoadCount = 150;

    [ObservableProperty]
    private string _imeLogFilePattern = "AppWorkload*.log";

    [ObservableProperty]
    private int _imeTimelineMaxLines = 400;

    [ObservableProperty]
    private string _imeLogSearchText = string.Empty;

    [ObservableProperty]
    private string _imeFlowSearchText = string.Empty;

    [ObservableProperty]
    private string _imeFlowTypeFilter = "All";

    [ObservableProperty]
    private string _imeTimelineComponentFilter = "All";

    [ObservableProperty]
    private bool _showImeFailedFlowsOnly;

    [ObservableProperty]
    private bool _showImeIncompleteFlowsOnly;

    [ObservableProperty]
    private bool _showImeLatestFlowRunsOnly;

    [ObservableProperty]
    private bool _focusSelectedImeFlow;

    [ObservableProperty]
    private string _imeAppSearchText = string.Empty;

    [ObservableProperty]
    private string _imeAppStatusFilter = "All";

    [ObservableProperty]
    private string _imeApplicationFlowFilter = "All";

    [ObservableProperty]
    private bool _showImeAppsWithoutIntent;

    [ObservableProperty]
    private bool _showImeSystemPlaceholderApps;

    [ObservableProperty]
    private bool _isImeAppDetailsExpanded;

    [ObservableProperty]
    private bool _showImeErrorEntries = true;

    [ObservableProperty]
    private bool _showImeWarningEntries = true;

    [ObservableProperty]
    private bool _showImeInformationEntries = true;

    [ObservableProperty]
    private bool _showImeVerboseEntries;

    [ObservableProperty]
    private bool _isLoadingMoreMdmEvents;

    [ObservableProperty]
    private bool _canLoadMoreMdmEvents = true;

    [ObservableProperty]
    private bool _isImeApplicationsLoading;

    [ObservableProperty]
    private string _localActionStatus = "No local action executed.";

    [ObservableProperty]
    private string _localActionWarnings = string.Empty;

    [ObservableProperty]
    private string _localActionOutputText = string.Empty;

    [ObservableProperty]
    private int _syncStatusMaxEvents = 50;

    [ObservableProperty]
    private string _mdmReportDirectory = string.Empty;

    [ObservableProperty]
    private string _policyResultReportDirectory = string.Empty;

    [ObservableProperty]
    private string _policyResultExportDirectory = string.Empty;

    [ObservableProperty]
    private string _policyResultStatus = "No policy result generated.";

    [ObservableProperty]
    private string _policyResultWarnings = string.Empty;

    [ObservableProperty]
    private string _policyReportHtmlPath = string.Empty;

    [ObservableProperty]
    private string _policyReportJsonPath = string.Empty;

    [ObservableProperty]
    private string _policyReportHtmlContent = string.Empty;

    [ObservableProperty]
    private string _policyReportJsonContent = string.Empty;

    [ObservableProperty]
    private int _policyResultTotalCount;

    [ObservableProperty]
    private int _policyResultAppliedCount;

    [ObservableProperty]
    private int _policyResultFailedCount;

    [ObservableProperty]
    private int _policyResultUnknownCount;

    [ObservableProperty]
    private int _policyResultDeviceCount;

    [ObservableProperty]
    private int _policyResultUserCount;

    [ObservableProperty]
    private int _policyResultUnknownScopeCount;

    [ObservableProperty]
    private string _imeLogDirectory = @"C:\ProgramData\Microsoft\IntuneManagementExtension\Logs";

    [ObservableProperty]
    private string _imeTaskNameContains = "Health Evaluation";

    [ObservableProperty]
    private bool _isImeTestModeEnabled;

    [ObservableProperty]
    private string _supportOutputDirectory = string.Empty;

    [ObservableProperty]
    private string _bundleRootDirectory = string.Empty;

    [ObservableProperty]
    private string _bundleZipPath = string.Empty;

    [ObservableProperty]
    private string _autopilotDiagnosticsModuleVersion = "6.3";

    [ObservableProperty]
    private bool _autopilotDiagnosticsAllSessions = true;

    [ObservableProperty]
    private bool _autopilotDiagnosticsShowPolicies;

    [ObservableProperty]
    private int _autopilotDiagnosticsMaxOutputLines = 1200;

    [ObservableProperty]
    private string _selectedCommunityScript = CommunityScriptAutopilotDiagnostics;

    public string AuthenticationSummary => AuthSession is null
        ? "Signed out"
        : $"{AuthSession.UserPrincipalName} | {AuthSession.TenantId} | expires {AuthSession.ExpiresAt:yyyy-MM-dd HH:mm}";

    public bool IsAutopilotCommunityScriptSelected =>
        string.Equals(SelectedCommunityScript, CommunityScriptAutopilotDiagnostics, StringComparison.Ordinal);

    public string SelectedCommunityScriptDescription =>
        string.Equals(SelectedCommunityScript, CommunityScriptImeQuickStatus, StringComparison.Ordinal)
            ? "Collects IME service/runtime status from the target host and returns a compact diagnostics snapshot."
            : "Installs/runs 'Get-AutopilotDiagnosticsCommunity' on the target host and captures the textual report.";

    public bool CanExecuteReenroll =>
        ReenrollPreview is not null &&
        ReenrollPreview.CanExecute &&
        string.Equals(ConfirmReenrollInput?.Trim(), ReenrollPreview.ConfirmationText, StringComparison.Ordinal) &&
        !IsLocalBusy;

    public bool CanFixEnrollmentUrls =>
        EnrollmentStatus?.EnrollmentUrls.CanRepair == true &&
        !EnrollmentStatus.EnrollmentUrls.AreExpected &&
        !IsLocalBusy;

    public bool CanTriggerCloudSync => IsCloudConfigured && AuthSession is not null && CloudDevice is not null && !IsCloudBusy;

    public bool IsCloudControlsEnabled => IsCloudConfigured && !IsCloudBusy;

    public string CloudDeviceSummary => CloudDevice is null
        ? "No cloud device resolved."
        : $"{CloudDevice.DeviceName} | {CloudDevice.ManagedDeviceId} | {CloudDevice.ComplianceState ?? "Unknown"}";

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (_initialized || _disposed)
        {
            return;
        }

        _initialized = true;
        if (!TryGetConnectedHost(out _))
        {
            ApplyDisconnectedState();
            return;
        }

        await RefreshOverviewAsync(cancellationToken);
    }

    public void ApplyNavigationTarget(string? navigationTarget)
    {
        var normalized = navigationTarget?.Trim().ToLowerInvariant();
        SelectedSectionIndex = MapNavigationTargetToSectionIndex(normalized);
    }

    public static int MapNavigationTargetToSectionIndex(string? navigationTarget)
    {
        return navigationTarget?.Trim().ToLowerInvariant() switch
        {
            "overview" => (int)IntuneAgentSection.Overview,
            "local-diagnostics" => (int)IntuneAgentSection.LocalDiagnostics,
            "enrollment" => (int)IntuneAgentSection.Enrollment,
            "mdm-events" => (int)IntuneAgentSection.MdmEvents,
            "logs" => (int)IntuneAgentSection.MdmEvents,
            "ime-logs" => (int)IntuneAgentSection.ImeLogs,
            "ime-applications" => (int)IntuneAgentSection.ImeApplications,
            "local-actions" => (int)IntuneAgentSection.LocalActions,
            "policy-result" => (int)IntuneAgentSection.PolicyResult,
            "cloud" => (int)IntuneAgentSection.Cloud,
            _ => (int)IntuneAgentSection.Overview
        };
    }

    [RelayCommand]
    public Task RefreshOverviewAsync()
    {
        return RefreshOverviewAsync(CancellationToken.None);
    }

    public async Task RefreshOverviewAsync(CancellationToken cancellationToken)
    {
        var selection = _targetHostService.CaptureSelection();
        var host = selection.Host;
        var totalTimer = StartVerboseTimer();
        using var linkedCancellationTokenSource = CreateHostLinkedCancellation(selection, cancellationToken);
        await _localGate.WaitAsync(linkedCancellationTokenSource.Token);
        try
        {
            if (_disposed)
            {
                return;
            }

            IsLocalBusy = true;
            CurrentHost = host;
            if (string.IsNullOrWhiteSpace(CurrentHost))
            {
                ApplyDisconnectedState();
                return;
            }

            var snapshotTask = MeasureOperationAsync(
                $"Overview diagnostics for '{CurrentHost}'",
                () => _localIntuneDiagnosticsService.GetSnapshotAsync(CurrentHost, linkedCancellationTokenSource.Token).AsTask());
            var enrollmentTask = MeasureOperationAsync(
                $"Enrollment status for '{CurrentHost}'",
                () => _localIntuneEnrollmentService.GetEnrollmentStatusAsync(CurrentHost, linkedCancellationTokenSource.Token).AsTask());
            await Task.WhenAll(snapshotTask, enrollmentTask);
            EnsureCurrentSelection(selection);

            Snapshot = await snapshotTask;
            EnrollmentStatus = await enrollmentTask;
            DiagnosticsStatus = $"Diagnostics loaded for '{CurrentHost}' at {Snapshot.CapturedAt:yyyy-MM-dd HH:mm:ss}.";
            EnrollmentStatusText = BuildEnrollmentSummary(EnrollmentStatus);
            SetStatus($"Overview refreshed for '{CurrentHost}'.");
        }
        catch (OperationCanceledException) when (selection.CancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            DiagnosticsStatus = $"Failed to refresh overview: {ex.Message}";
            SetStatus(DiagnosticsStatus, ex);
        }
        finally
        {
            IsLocalBusy = false;
            _localGate.Release();
        }

        await MeasureOperationAsync(
            $"Cloud refresh for '{host}'",
            () => RefreshCloudAsync(linkedCancellationTokenSource.Token, logNoSession: false, selection));
        LogVerboseDuration($"Overview refresh for '{host}'", totalTimer);
    }

    [RelayCommand]
    public async Task RefreshDiagnosticsAsync()
    {
        var totalTimer = StartVerboseTimer();
        await _localGate.WaitAsync();
        try
        {
            IsLocalBusy = true;
            if (!TryGetConnectedHost(out _))
            {
                DiagnosticsStatus = DisconnectedStatus;
                SetStatus(DiagnosticsStatus);
                return;
            }

            Snapshot = await MeasureOperationAsync(
                $"Diagnostics snapshot for '{CurrentHost}'",
                () => _localIntuneDiagnosticsService.GetSnapshotAsync(CurrentHost, CancellationToken.None).AsTask());
            DiagnosticsStatus = $"Diagnostics snapshot refreshed for '{CurrentHost}'.";
            SetStatus(DiagnosticsStatus);
        }
        catch (Exception ex)
        {
            DiagnosticsStatus = $"Diagnostics refresh failed: {ex.Message}";
            SetStatus(DiagnosticsStatus, ex);
        }
        finally
        {
            LogVerboseDuration($"Diagnostics refresh for '{CurrentHost}'", totalTimer);
            IsLocalBusy = false;
            _localGate.Release();
        }
    }

    [RelayCommand]
    public async Task ExportSnapshotAsync()
    {
        var totalTimer = StartVerboseTimer();
        await _localGate.WaitAsync();
        try
        {
            IsLocalBusy = true;
            if (!TryGetConnectedHost(out _))
            {
                DiagnosticsStatus = DisconnectedStatus;
                SetStatus(DiagnosticsStatus);
                return;
            }

            var path = await MeasureOperationAsync(
                $"Snapshot export for '{CurrentHost}'",
                () => _localIntuneDiagnosticsService.ExportSnapshotAsync(CurrentHost, GetExportDirectory(), CancellationToken.None).AsTask());
            DiagnosticsStatus = $"Snapshot exported to {path}";
            SetStatus(DiagnosticsStatus);
        }
        catch (Exception ex)
        {
            DiagnosticsStatus = $"Snapshot export failed: {ex.Message}";
            SetStatus(DiagnosticsStatus, ex);
        }
        finally
        {
            LogVerboseDuration($"Snapshot export for '{CurrentHost}'", totalTimer);
            IsLocalBusy = false;
            _localGate.Release();
        }
    }

    [RelayCommand]
    public async Task ExportMdmDiagnosticsAsync()
    {
        var totalTimer = StartVerboseTimer();
        await _localGate.WaitAsync();
        try
        {
            IsLocalBusy = true;
            if (!TryGetConnectedHost(out _))
            {
                DiagnosticsStatus = DisconnectedStatus;
                SetStatus(DiagnosticsStatus);
                return;
            }

            var path = await MeasureOperationAsync(
                $"MDM diagnostics export for '{CurrentHost}'",
                () => _localIntuneDiagnosticsService.ExportMdmDiagnosticsAsync(CurrentHost, GetExportDirectory(), CancellationToken.None).AsTask());
            DiagnosticsStatus = $"MDM diagnostics exported to {path}";
            SetStatus(DiagnosticsStatus);
        }
        catch (Exception ex)
        {
            DiagnosticsStatus = $"MDM diagnostics export failed: {ex.Message}";
            SetStatus(DiagnosticsStatus, ex);
        }
        finally
        {
            LogVerboseDuration($"MDM diagnostics export for '{CurrentHost}'", totalTimer);
            IsLocalBusy = false;
            _localGate.Release();
        }
    }

    [RelayCommand]
    public async Task LoadMdmEventsAsync()
    {
        _requestedMdmEventCount = MdmEventLoadCount;
        await ReloadMdmEventsAsync(logMessage: $"MDM events loaded for '{CurrentHost}'.");
    }

    public async Task LoadMoreMdmEventsAsync()
    {
        if (!CanLoadMoreMdmEvents || IsLoadingMoreMdmEvents || IsLocalBusy)
        {
            return;
        }

        _requestedMdmEventCount = Math.Min(_requestedMdmEventCount + MdmEventLoadCount, 400);
        await ReloadMdmEventsAsync(logMessage: $"Additional MDM events loaded for '{CurrentHost}'.", isLoadMore: true);
    }

    [RelayCommand]
    public async Task LoadImeLogTimelineAsync()
    {
        await LoadImeLogTimelineCoreAsync(emitSuccessStatus: true, emitFailureStatus: true);
    }

    [RelayCommand]
    public void ClearImeFlowSelection()
    {
        SelectedImeFlowSummary = null;
    }

    public void ToggleImeRelatedHighlightForSelectedEntry()
    {
        if (SelectedImeLogEntry is null)
        {
            return;
        }

        var highlightKey = BuildImeHighlightKey(SelectedImeLogEntry);
        _imeHighlightedFlowKey = string.Equals(_imeHighlightedFlowKey, highlightKey, StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : highlightKey;
        ApplyImeLogFilters(updateStatus: false);
    }

    public async Task RefreshImeLogTimelineInBackgroundAsync()
    {
        if (!TryGetConnectedHost(out var currentHost))
        {
            return;
        }

        var currentFingerprint = await _localIntuneActionService.GetImeLogTimelineFingerprintAsync(
            currentHost,
            ImeLogDirectory,
            ImeLogFilePattern,
            CancellationToken.None);
        if (string.Equals(_imeTimelineFingerprint, currentFingerprint, StringComparison.Ordinal))
        {
            return;
        }

        await LoadImeLogTimelineCoreAsync(emitSuccessStatus: false, emitFailureStatus: false);
    }

    private async Task LoadImeLogTimelineCoreAsync(bool emitSuccessStatus, bool emitFailureStatus)
    {
        var totalTimer = StartVerboseTimer();
        await _localGate.WaitAsync();
        try
        {
            IsLocalBusy = true;
            if (!TryGetConnectedHost(out _))
            {
                ImeLogsStatus = DisconnectedStatus;
                if (emitFailureStatus)
                {
                    SetStatus(ImeLogsStatus);
                }

                return;
            }

            var timelineSnapshot = await MeasureOperationAsync(
                $"IME timeline load for '{CurrentHost}'",
                () => _localIntuneActionService.GetImeLogTimelineSnapshotAsync(
                    CurrentHost,
                    ImeLogDirectory,
                    ImeLogFilePattern,
                    ImeTimelineMaxLines,
                    CancellationToken.None).AsTask());
            _imeTimelineFingerprint = timelineSnapshot.Fingerprint;

            _allImeTimelineEntries.Clear();
            _allImeTimelineEntries.AddRange(timelineSnapshot.Entries);
            ApplyImeLogFilters(updateStatus: true);
            ApplyImeApplicationFilters(updateStatus: _allImeApplications.Count > 0);
            if (emitSuccessStatus)
            {
                SetStatus($"IME log timeline loaded for '{CurrentHost}'.");
            }
        }
        catch (Exception ex)
        {
            ImeLogsStatus = $"IME log timeline loading failed: {ex.Message}";
            if (emitFailureStatus)
            {
                SetStatus(ImeLogsStatus, ex);
            }
            else
            {
                _logger.LogDebug(ex, "Background IME timeline refresh failed for host '{Host}'.", CurrentHost);
            }
        }
        finally
        {
            LogVerboseDuration($"IME timeline refresh for '{CurrentHost}'", totalTimer);
            IsLocalBusy = false;
            _localGate.Release();
        }
    }

    [RelayCommand]
    public async Task LoadImeApplicationsAsync()
    {
        var totalTimer = StartVerboseTimer();
        await _localGate.WaitAsync();
        try
        {
            IsLocalBusy = true;
            IsImeApplicationsLoading = true;
            if (!TryGetConnectedHost(out _))
            {
                ImeAppsStatus = DisconnectedStatus;
                SetStatus(ImeAppsStatus);
                return;
            }

            var previousByAppId = _allImeApplications
                .GroupBy(static entry => entry.AppId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    static group => group.Key,
                    static group => group.First(),
                    StringComparer.OrdinalIgnoreCase);

            var items = await MeasureOperationAsync(
                $"IME application status load for '{CurrentHost}'",
                () => _localIntuneActionService.GetImeApplicationStatusesAsync(
                    CurrentHost,
                    ImeLogDirectory,
                    ImeTimelineMaxLines,
                    CancellationToken.None).AsTask());
            var mergedItems = MergeImeDisplayMetadata(items, previousByAppId);

            _allImeApplications.Clear();
            _allImeApplications.AddRange(mergedItems);
            LogImeApplicationDiagnostics(mergedItems);
            await RefreshImeTestModeAsync(CurrentHost);
            ApplyImeApplicationFilters(updateStatus: true);
            SetStatus($"IME application status list loaded for '{CurrentHost}'.");
        }
        catch (Exception ex)
        {
            ImeAppsStatus = $"IME application list loading failed: {ex.Message}";
            SetStatus(ImeAppsStatus, ex);
        }
        finally
        {
            LogVerboseDuration($"IME application refresh for '{CurrentHost}'", totalTimer);
            IsImeApplicationsLoading = false;
            IsLocalBusy = false;
            _localGate.Release();
        }
    }

    [RelayCommand]
    public async Task RunEnrollmentCheckAsync()
    {
        var totalTimer = StartVerboseTimer();
        await _localGate.WaitAsync();
        try
        {
            IsLocalBusy = true;
            if (!TryGetConnectedHost(out _))
            {
                EnrollmentStatusText = DisconnectedStatus;
                SetStatus(EnrollmentStatusText);
                return;
            }

            EnrollmentStatus = await MeasureOperationAsync(
                $"Enrollment check for '{CurrentHost}'",
                () => _localIntuneEnrollmentService.GetEnrollmentStatusAsync(CurrentHost, CancellationToken.None).AsTask());
            EnrollmentStatusText = BuildEnrollmentSummary(EnrollmentStatus);
            SetStatus($"Enrollment check completed for '{CurrentHost}'.");
        }
        catch (Exception ex)
        {
            EnrollmentStatusText = $"Enrollment check failed: {ex.Message}";
            SetStatus(EnrollmentStatusText, ex);
        }
        finally
        {
            LogVerboseDuration($"Enrollment check for '{CurrentHost}'", totalTimer);
            IsLocalBusy = false;
            _localGate.Release();
        }
    }

    [RelayCommand]
    public async Task TriggerSyncAsync()
    {
        await _localGate.WaitAsync();
        try
        {
            IsLocalBusy = true;
            var result = await _localIntuneEnrollmentService.TriggerSyncAsync(CurrentHost, CancellationToken.None);
            EnrollmentStatusText = result.Message;
            SetStatus(result.Message);
        }
        catch (Exception ex)
        {
            EnrollmentStatusText = $"Local sync failed: {ex.Message}";
            SetStatus(EnrollmentStatusText, ex);
        }
        finally
        {
            IsLocalBusy = false;
            _localGate.Release();
        }

        await RefreshOverviewAsync(CancellationToken.None);
    }

    [RelayCommand(CanExecute = nameof(CanFixEnrollmentUrls))]
    public async Task FixEnrollmentUrlsAsync()
    {
        await _localGate.WaitAsync();
        try
        {
            IsLocalBusy = true;
            var result = await _localIntuneEnrollmentService.FixEnrollmentUrlsAsync(CurrentHost, CancellationToken.None);
            EnrollmentStatusText = result.Message;
            SetStatus(result.Message);
        }
        catch (Exception ex)
        {
            EnrollmentStatusText = $"Enrollment URL repair failed: {ex.Message}";
            SetStatus(EnrollmentStatusText, ex);
        }
        finally
        {
            IsLocalBusy = false;
            _localGate.Release();
        }

        await RunEnrollmentCheckAsync();
    }

    [RelayCommand]
    public async Task PreviewReenrollAsync()
    {
        await _localGate.WaitAsync();
        try
        {
            IsLocalBusy = true;
            ReenrollPreview = await _localIntuneEnrollmentService.PreviewReenrollAsync(CurrentHost, CancellationToken.None);
            ConfirmReenrollInput = string.Empty;
            EnrollmentStatusText = ReenrollPreview.Summary;
            SetStatus($"Re-enroll preview generated for '{CurrentHost}'.");
        }
        catch (Exception ex)
        {
            EnrollmentStatusText = $"Re-enroll preview failed: {ex.Message}";
            SetStatus(EnrollmentStatusText, ex);
        }
        finally
        {
            IsLocalBusy = false;
            _localGate.Release();
        }
    }

    [RelayCommand(CanExecute = nameof(CanExecuteReenroll))]
    public async Task ExecuteReenrollAsync()
    {
        await _localGate.WaitAsync();
        try
        {
            IsLocalBusy = true;
            var result = await _localIntuneEnrollmentService.ExecuteReenrollAsync(CurrentHost, confirmed: true, CancellationToken.None);
            EnrollmentStatusText = result.Message;
            SetStatus(result.Message);
            ReenrollPreview = null;
            ConfirmReenrollInput = string.Empty;
        }
        catch (Exception ex)
        {
            EnrollmentStatusText = $"Re-enroll execution failed: {ex.Message}";
            SetStatus(EnrollmentStatusText, ex);
        }
        finally
        {
            IsLocalBusy = false;
            _localGate.Release();
        }

        await RefreshOverviewAsync(CancellationToken.None);
    }

    [RelayCommand]
    public Task ActionMdmSyncNowAsync()
    {
        return ExecuteLocalActionAsync(
            () => _localIntuneActionService.MdmSyncNowAsync(CurrentHost, CancellationToken.None),
            $"MDM sync executed for '{CurrentHost}'.");
    }

    [RelayCommand]
    public async Task ActionMdmSyncStatusAsync()
    {
        await _localGate.WaitAsync();
        try
        {
            IsLocalBusy = true;
            var items = await _localIntuneActionService.GetMdmSyncStatusAsync(CurrentHost, SyncStatusMaxEvents, CancellationToken.None);
            var latest = items.FirstOrDefault();
            LocalActionStatus = latest is null
                ? "No MDM sync status events found."
                : $"Returned {items.Count} MDM sync status event(s). Last: ID {latest.EventId}, Result {latest.ResultCode}.";
            LocalActionWarnings = string.Empty;
            LocalActionEvidence.Clear();
            var idx = 1;
            foreach (var item in items.Take(10))
            {
                LocalActionEvidence.Add(new NameValueItem(
                    $"Event {idx}",
                    $"{item.TimeCreated:yyyy-MM-dd HH:mm:ss} | ID {item.EventId} | {item.ResultCode}"));
                idx++;
            }

            SetStatus(LocalActionStatus);
        }
        catch (Exception ex)
        {
            LocalActionStatus = $"MDM sync status failed: {ex.Message}";
            LocalActionWarnings = string.Empty;
            LocalActionEvidence.Clear();
            SetStatus(LocalActionStatus, ex);
        }
        finally
        {
            IsLocalBusy = false;
            _localGate.Release();
        }
    }

    [RelayCommand]
    public async Task ActionGenerateMdmReportAsync()
    {
        await _localGate.WaitAsync();
        IDisposable? longRunningScope = null;
        try
        {
            IsLocalBusy = true;
            longRunningScope = BeginLongRunningLocalAction("Generating MDM report...");
            var report = await _localIntuneActionService.GenerateMdmDiagnosticsReportAsync(CurrentHost, MdmReportDirectory, CancellationToken.None);
            MdmReportDirectory = report.ReportDirectory;
            LocalActionStatus = $"Generated and parsed XML/HTML from '{report.ReportDirectory}'.";
            LocalActionWarnings = string.Empty;
            LocalActionEvidence.Clear();
            LocalActionEvidence.Add(new NameValueItem("XmlPath", report.XmlPath));
            LocalActionEvidence.Add(new NameValueItem("HtmlPath", report.HtmlPath));
            LocalActionEvidence.Add(new NameValueItem("XmlNodeCount", report.XmlNodeCount.ToString()));
            LocalActionEvidence.Add(new NameValueItem("HtmlLineCount", report.HtmlLineCount.ToString()));
            SetStatus(LocalActionStatus);
        }
        catch (Exception ex)
        {
            LocalActionStatus = $"Generate MDM report failed: {ex.Message}";
            LocalActionWarnings = string.Empty;
            LocalActionEvidence.Clear();
            SetStatus(LocalActionStatus, ex);
        }
        finally
        {
            longRunningScope?.Dispose();
            IsLocalBusy = false;
            _localGate.Release();
        }
    }

    [RelayCommand]
    public async Task ActionParseMdmReportAsync()
    {
        await _localGate.WaitAsync();
        IDisposable? longRunningScope = null;
        try
        {
            IsLocalBusy = true;
            longRunningScope = BeginLongRunningLocalAction("Parsing MDM report...");
            var report = await _localIntuneActionService.ParseMdmDiagnosticsReportAsync(CurrentHost, MdmReportDirectory, CancellationToken.None);
            LocalActionStatus = $"Parsed report from '{report.ReportDirectory}'.";
            LocalActionWarnings = string.Empty;
            LocalActionEvidence.Clear();
            LocalActionEvidence.Add(new NameValueItem("XmlPath", report.XmlPath));
            LocalActionEvidence.Add(new NameValueItem("HtmlPath", report.HtmlPath));
            LocalActionEvidence.Add(new NameValueItem("XmlNodeCount", report.XmlNodeCount.ToString()));
            LocalActionEvidence.Add(new NameValueItem("HtmlLineCount", report.HtmlLineCount.ToString()));
            SetStatus(LocalActionStatus);
        }
        catch (Exception ex)
        {
            LocalActionStatus = $"Parse MDM report failed: {ex.Message}";
            LocalActionWarnings = string.Empty;
            LocalActionEvidence.Clear();
            SetStatus(LocalActionStatus, ex);
        }
        finally
        {
            longRunningScope?.Dispose();
            IsLocalBusy = false;
            _localGate.Release();
        }
    }

    [RelayCommand]
    public async Task GeneratePolicyResultAsync()
    {
        await _localGate.WaitAsync();
        IDisposable? longRunningScope = null;
        var workingDirectory = GetPolicyResultWorkingDirectory(CurrentHost);
        try
        {
            IsLocalBusy = true;
            longRunningScope = BeginLongRunningLocalAction("Generating policy result...");
            var report = await _localIntuneActionService.GenerateIntunePolicyResultAsync(
                CurrentHost,
                workingDirectory,
                CancellationToken.None);
            PolicyResultReportDirectory = report.ReportDirectory;
            ApplyPolicyResultReport(report);
            LogPolicyResultTimings(report.Timings);
            PolicyResultStatus = $"Generated policy result from '{report.ReportDirectory}' with {report.Summary.TotalCount} entries.";
            SetStatus(PolicyResultStatus);
        }
        catch (Exception ex)
        {
            PolicyResultStatus = $"Generate policy result failed: {ex.Message}";
            PolicyResultWarnings = string.Empty;
            SetStatus(PolicyResultStatus, ex);
        }
        finally
        {
            longRunningScope?.Dispose();
            IsLocalBusy = false;
            _localGate.Release();
        }
    }

    [RelayCommand]
    public async Task ParsePolicyResultAsync()
    {
        await _localGate.WaitAsync();
        IDisposable? longRunningScope = null;
        try
        {
            IsLocalBusy = true;
            longRunningScope = BeginLongRunningLocalAction("Parsing policy result...");
            var report = await _localIntuneActionService.ParseIntunePolicyResultAsync(
                CurrentHost,
                PolicyResultReportDirectory,
                GetPolicyResultWorkingDirectory(CurrentHost),
                CancellationToken.None);
            ApplyPolicyResultReport(report);
            LogPolicyResultTimings(report.Timings);
            PolicyResultStatus = $"Parsed policy result from '{report.ReportDirectory}' with {report.Summary.TotalCount} entries.";
            SetStatus(PolicyResultStatus);
        }
        catch (Exception ex)
        {
            PolicyResultStatus = $"Parse policy result failed: {ex.Message}";
            PolicyResultWarnings = string.Empty;
            SetStatus(PolicyResultStatus, ex);
        }
        finally
        {
            longRunningScope?.Dispose();
            IsLocalBusy = false;
            _localGate.Release();
        }
    }

    [RelayCommand]
    public async Task ExportPolicyResultAsync()
    {
        await _localGate.WaitAsync();
        try
        {
            IsLocalBusy = true;
            var hasHtmlFile = !string.IsNullOrWhiteSpace(PolicyReportHtmlPath) && File.Exists(PolicyReportHtmlPath);
            var hasJsonFile = !string.IsNullOrWhiteSpace(PolicyReportJsonPath) && File.Exists(PolicyReportJsonPath);
            var hasHtmlContent = !string.IsNullOrWhiteSpace(PolicyReportHtmlContent);
            var hasJsonContent = !string.IsNullOrWhiteSpace(PolicyReportJsonContent);
            if ((!hasHtmlFile && !hasHtmlContent) || (!hasJsonFile && !hasJsonContent))
            {
                throw new InvalidOperationException("No policy result export source available. Generate or parse a report first.");
            }

            var destination = string.IsNullOrWhiteSpace(PolicyResultExportDirectory)
                ? Path.Combine(GetExportDirectory(), "policy-result")
                : PolicyResultExportDirectory;
            Directory.CreateDirectory(destination);

            var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            var htmlTarget = Path.Combine(destination, $"intune-policy-result-{stamp}.html");
            var jsonTarget = Path.Combine(destination, $"intune-policy-result-{stamp}.json");

            await Task.WhenAll(
                CopyOrWritePolicyArtifactAsync(PolicyReportHtmlPath, htmlTarget, PolicyReportHtmlContent, hasHtmlFile),
                CopyOrWritePolicyArtifactAsync(PolicyReportJsonPath, jsonTarget, PolicyReportJsonContent, hasJsonFile));

            LocalActionStatus = $"Policy result exported to '{destination}'.";
            LocalActionWarnings = string.Empty;
            LocalActionEvidence.Clear();
            LocalActionEvidence.Add(new NameValueItem("ExportHtmlPath", htmlTarget));
            LocalActionEvidence.Add(new NameValueItem("ExportJsonPath", jsonTarget));
            SetStatus(LocalActionStatus);
        }
        catch (Exception ex)
        {
            LocalActionStatus = $"Export policy result failed: {ex.Message}";
            LocalActionWarnings = string.Empty;
            LocalActionEvidence.Clear();
            SetStatus(LocalActionStatus, ex);
        }
        finally
        {
            IsLocalBusy = false;
            _localGate.Release();
        }
    }

    [RelayCommand]
    public Task ActionImeSyncAppsAsync()
    {
        return ExecuteLocalActionAsync(
            () => _localIntuneActionService.ImeSyncAppsAsync(CurrentHost, CancellationToken.None),
            $"IME app sync signal sent for '{CurrentHost}'.");
    }

    [RelayCommand]
    public Task ActionImeSyncComplianceAsync()
    {
        return ExecuteLocalActionAsync(
            () => _localIntuneActionService.ImeSyncComplianceAsync(CurrentHost, CancellationToken.None),
            $"IME compliance sync signal sent for '{CurrentHost}'.");
    }

    [RelayCommand]
    public async Task ActionParseAppWorkloadAsync()
    {
        await ExecuteLocalActionAsync(
            () => _localIntuneActionService.ParseImeAppWorkloadPoliciesAsync(CurrentHost, ImeLogDirectory, CancellationToken.None),
            $"Parsed AppWorkload policies from local AppWorkload logs in '{ImeLogDirectory}'.");

        await ReloadImeAnalysisAsync();
    }

    [RelayCommand]
    public Task ActionRunImeHealthEvalAsync()
    {
        return ExecuteLocalActionAsync(
            () => _localIntuneActionService.RunImeHealthEvaluationAsync(CurrentHost, ImeTaskNameContains, CancellationToken.None),
            $"IME health evaluation started for '{CurrentHost}'.");
    }

    [RelayCommand]
    public async Task ActionRestartImeAsync()
    {
        await ExecuteLocalActionAsync(
            () => _localIntuneActionService.RestartImeServiceAsync(CurrentHost, CancellationToken.None),
            $"IME service restart requested for '{CurrentHost}'.");

        await LoadImeApplicationsAsync();
    }

    private async Task ReloadImeAnalysisAsync()
    {
        var totalTimer = StartVerboseTimer();
        await _localGate.WaitAsync();
        try
        {
            IsLocalBusy = true;
            CurrentHost = _targetHostService.CurrentHost;

            var analysis = await MeasureOperationAsync(
                $"IME combined analysis for '{CurrentHost}'",
                () => _localIntuneActionService.GetImeLogAnalysisAsync(
                    CurrentHost,
                    ImeLogDirectory,
                    ImeLogFilePattern,
                    ImeTimelineMaxLines,
                    CancellationToken.None).AsTask());

            _imeTimelineFingerprint = analysis.Fingerprint;
            _allImeTimelineEntries.Clear();
            _allImeTimelineEntries.AddRange(analysis.TimelineEntries);
            ApplyImeLogFilters(updateStatus: true);

            var previousByAppId = _allImeApplications
                .GroupBy(static entry => entry.AppId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    static group => group.Key,
                    static group => group.First(),
                    StringComparer.OrdinalIgnoreCase);
            var mergedItems = MergeImeDisplayMetadata(analysis.ApplicationStatuses, previousByAppId);

            _allImeApplications.Clear();
            _allImeApplications.AddRange(mergedItems);
            LogImeApplicationDiagnostics(mergedItems);
            await RefreshImeTestModeAsync(CurrentHost);
            ApplyImeApplicationFilters(updateStatus: true);
            SetStatus($"IME log timeline and application status list loaded for '{CurrentHost}'.");
        }
        catch (Exception ex)
        {
            ImeLogsStatus = $"IME log timeline loading failed: {ex.Message}";
            ImeAppsStatus = $"IME application list loading failed: {ex.Message}";
            SetStatus(ImeAppsStatus, ex);
        }
        finally
        {
            LogVerboseDuration($"IME combined analysis refresh for '{CurrentHost}'", totalTimer);
            IsLocalBusy = false;
            _localGate.Release();
        }
    }

    [RelayCommand]
    public async Task SetImeTestModeAsync(bool? enabled)
    {
        if (enabled is null)
        {
            return;
        }

        var requestedState = enabled.Value;
        await ExecuteLocalActionAsync(
            () => _localIntuneActionService.SetImeTestModeEnabledAsync(CurrentHost, requestedState, CancellationToken.None),
            requestedState
                ? $"Fast first check-in mode enabled for '{CurrentHost}' (skips extra non-ESP startup delays)."
                : $"Fast first check-in mode disabled for '{CurrentHost}' (default non-ESP startup delays restored).");

        await RefreshImeTestModeAsync(CurrentHost);
    }

    [RelayCommand]
    public async Task ActionRetryWin32AllAsync()
    {
        var failedCandidates = _allImeApplications.Count(static entry => IsFailedLike(entry.InstallStatus, entry.ResultCode));
        var request = new Win32RetryAllRequest(
            Math.Clamp(failedCandidates > 0 ? failedCandidates : 500, 1, 500),
            0,
            Path.Combine(GetExportDirectory(), "win32-retry-all"),
            false,
            true,
            false);

        await ExecuteLocalActionAsync(
            () => _localIntuneActionService.RetryAllFailedWin32AppsAsync(CurrentHost, request, CancellationToken.None),
            $"Failed Win32 app state cleared for '{CurrentHost}'.");

        await LoadImeApplicationsAsync();
    }

    [RelayCommand]
    public async Task ClearImeAppStateAsync(ImeApplicationStatusEntry? app)
    {
        if (app is null)
        {
            LocalActionStatus = "No IME application selected.";
            LocalActionWarnings = string.Empty;
            LocalActionEvidence.Clear();
            SetStatus(LocalActionStatus);
            return;
        }

        if (!Guid.TryParse(app.AppId, out var appId))
        {
            LocalActionStatus = $"Clear app state failed: AppId '{app.AppId}' is not a valid GUID.";
            LocalActionWarnings = string.Empty;
            LocalActionEvidence.Clear();
            SetStatus(LocalActionStatus);
            return;
        }

        var identityIds = app.IdentityStatuses?
            .Where(static status => IsFailedLike(status.InstallStatus, status.ResultCode))
            .Select(static status => status.IdentityId?.Trim())
            .Where(static identityId => !string.IsNullOrWhiteSpace(identityId))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Cast<string>()
            .ToArray() ?? [];

        if (identityIds.Length == 0 && IsFailedLike(app.InstallStatus, app.ResultCode))
        {
            identityIds = [SystemIdentityId];
        }

        if (identityIds.Length == 0)
        {
            LocalActionStatus = $"No failed state found for '{app.AppName}' ({app.AppId}).";
            LocalActionWarnings = string.Empty;
            LocalActionEvidence.Clear();
            SetStatus(LocalActionStatus);
            return;
        }

        await _localGate.WaitAsync();
        try
        {
            IsLocalBusy = true;
            LocalActionWarnings = string.Empty;
            LocalActionEvidence.Clear();
            var failures = new List<string>();

            foreach (var identityId in identityIds)
            {
                var request = new Win32RetryRequest(
                    identityId,
                    appId,
                    Path.Combine(GetExportDirectory(), "win32-retry-single"),
                    false);

                var result = await _localIntuneActionService.RetryWin32AppAsync(CurrentHost, request, CancellationToken.None);
                LocalActionEvidence.Add(new NameValueItem($"Identity {identityId}", result.Message));
                if (!result.Success)
                {
                    failures.Add($"{identityId}: {result.Message}");
                }
            }

            LocalActionStatus = failures.Count == 0
                ? $"Cleared app state for '{app.AppName}' on {identityIds.Length} identity scope(s)."
                : $"Clear app state completed with {failures.Count} error(s).";
            LocalActionWarnings = failures.Count == 0 ? string.Empty : string.Join(Environment.NewLine, failures);
            SetStatus(LocalActionStatus);
        }
        catch (Exception ex)
        {
            LocalActionStatus = $"Clear app state failed: {ex.Message}";
            LocalActionWarnings = string.Empty;
            LocalActionEvidence.Clear();
            SetStatus(LocalActionStatus, ex);
        }
        finally
        {
            IsLocalBusy = false;
            _localGate.Release();
        }

        await LoadImeApplicationsAsync();
    }

    [RelayCommand]
    public Task ActionExportSupportEventLogsAsync()
    {
        return ExecuteLocalActionAsync(
            () => _localIntuneActionService.ExportSupportEventLogsAsync(CurrentHost, SupportOutputDirectory, CancellationToken.None),
            $"Support event logs exported for '{CurrentHost}'.");
    }

    [RelayCommand]
    public Task ActionCreateBundleAsync()
    {
        return ExecuteLocalActionAsync(
            () => _localIntuneActionService.CreateDiagnosticsBundleAsync(CurrentHost, BundleRootDirectory, BundleZipPath, CancellationToken.None),
            $"Diagnostics bundle created for '{CurrentHost}'.",
            markAsLongRunning: true,
            longRunningLabel: "Creating diagnostics bundle...");
    }

    [RelayCommand]
    public Task ActionAutopilotDiagnosticsCommunityAsync()
    {
        var normalizedVersion = string.IsNullOrWhiteSpace(AutopilotDiagnosticsModuleVersion) ? "6.3" : AutopilotDiagnosticsModuleVersion.Trim();
        var clampedMaxOutputLines = Math.Clamp(AutopilotDiagnosticsMaxOutputLines, 100, 10000);
        var helperScript = LoadEmbeddedIntuneHelperScript("Invoke-AutopilotDiagnosticsCommunity.ps1")
            .Replace("__MODULE_VERSION__", normalizedVersion.Replace("'", "''", StringComparison.Ordinal), StringComparison.Ordinal)
            .Replace("__ALL_SESSIONS__", AutopilotDiagnosticsAllSessions ? "$true" : "$false", StringComparison.Ordinal)
            .Replace("__SHOW_POLICIES__", AutopilotDiagnosticsShowPolicies ? "$true" : "$false", StringComparison.Ordinal)
            .Replace("__MAX_OUTPUT_LINES__", clampedMaxOutputLines.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);

        return LaunchExternalHelperScriptAsync(
            helperScript,
            "autopilot-diagnostics-community",
            "Autopilot Diagnostics (Community)");
    }

    [RelayCommand]
    public Task ActionRunSelectedCommunityScriptAsync()
    {
        if (string.Equals(SelectedCommunityScript, CommunityScriptImeQuickStatus, StringComparison.Ordinal))
        {
            var clampedMaxOutputLines = Math.Clamp(AutopilotDiagnosticsMaxOutputLines, 50, 5000);
            var helperScript = LoadEmbeddedIntuneHelperScript("Invoke-ImeQuickStatus.ps1")
                .Replace("__MAX_OUTPUT_LINES__", clampedMaxOutputLines.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);

            return LaunchExternalHelperScriptAsync(
                helperScript,
                "ime-quick-status",
                "IME Quick Status");
        }

        return ActionAutopilotDiagnosticsCommunityAsync();
    }

    [RelayCommand]
    public async Task SignInAsync()
    {
        await _cloudGate.WaitAsync();
        try
        {
            IsCloudBusy = true;
            if (!EnsureCloudServicesAvailable())
            {
                return;
            }

            AuthSession = await _authService!.LoginAsync(CancellationToken.None);
            CloudStatus = $"Signed in as {AuthSession.UserPrincipalName}.";
            SetStatus(CloudStatus);
        }
        catch (OperationCanceledException)
        {
            CloudStatus = "Cloud sign-in canceled.";
            SetStatus(CloudStatus);
        }
        catch (Exception ex)
        {
            CloudStatus = $"Cloud sign-in failed: {ex.Message}";
            SetStatus(CloudStatus, ex);
        }
        finally
        {
            IsCloudBusy = false;
            _cloudGate.Release();
        }

        await RefreshCloudAsync(CancellationToken.None, logNoSession: false);
    }

    [RelayCommand]
    public Task RefreshCloudAsync()
    {
        return RefreshCloudAsync(CancellationToken.None, logNoSession: true);
    }

    private async Task RefreshCloudAsync(CancellationToken cancellationToken, bool logNoSession)
    {
        await RefreshCloudAsync(cancellationToken, logNoSession, _targetHostService.CaptureSelection());
    }

    private async Task RefreshCloudAsync(CancellationToken cancellationToken, bool logNoSession, HostSelection selection)
    {
        var totalTimer = StartVerboseTimer();
        using var linkedCancellationTokenSource = CreateHostLinkedCancellation(selection, cancellationToken);
        await _cloudGate.WaitAsync(linkedCancellationTokenSource.Token);
        try
        {
            if (_disposed)
            {
                return;
            }

            IsCloudBusy = true;
            if (!EnsureCloudServicesAvailable())
            {
                return;
            }

            if (!TryGetConnectedHost(out _))
            {
                CloudDevice = null;
                CloudStatus = DisconnectedStatus;
                if (logNoSession)
                {
                    SetStatus(CloudStatus);
                }

                return;
            }

            AuthSession = await MeasureOperationAsync(
                "Cloud session lookup",
                () => _authService!.GetCurrentSessionAsync(linkedCancellationTokenSource.Token).AsTask());
            if (AuthSession is null || AuthSession.ExpiresAt <= DateTimeOffset.UtcNow.AddMinutes(1))
            {
                AuthSession = null;
                CloudDevice = null;
                CloudStatus = "Cloud sign-in required.";
                if (logNoSession)
                {
                    SetStatus(CloudStatus);
                }
                return;
            }

            CloudDevice = await MeasureOperationAsync(
                $"Cloud device lookup for '{CurrentHost}'",
                () => _cloudManagedDeviceService!.FindManagedDeviceByHostAsync(CurrentHost, linkedCancellationTokenSource.Token).AsTask());
            EnsureCurrentSelection(selection);
            CloudStatus = CloudDevice is null
                ? $"No exact Intune managed device match for '{CurrentHost}'."
                : $"Resolved cloud device '{CloudDevice.DeviceName}' from {CloudDevice.Source}.";
            SetStatus(CloudStatus);
        }
        catch (OperationCanceledException) when (selection.CancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            CloudStatus = $"Cloud lookup failed: {ex.Message}";
            SetStatus(CloudStatus, ex);
        }
        finally
        {
            LogVerboseDuration($"Cloud refresh for '{CurrentHost}'", totalTimer);
            IsCloudBusy = false;
            _cloudGate.Release();
        }
    }

    [RelayCommand(CanExecute = nameof(CanTriggerCloudSync))]
    public async Task TriggerCloudSyncAsync()
    {
        await _cloudGate.WaitAsync();
        try
        {
            IsCloudBusy = true;
            if (!EnsureCloudServicesAvailable())
            {
                return;
            }

            if (CloudDevice is null)
            {
                CloudStatus = "No cloud device resolved.";
                SetStatus(CloudStatus);
                return;
            }

            var result = await _cloudManagedDeviceService!.SyncManagedDeviceAsync(CloudDevice.ManagedDeviceId, CancellationToken.None);
            CloudStatus = result.Message;
            SetStatus(result.Message);
        }
        catch (Exception ex)
        {
            CloudStatus = $"Cloud sync failed: {ex.Message}";
            SetStatus(CloudStatus, ex);
        }
        finally
        {
            IsCloudBusy = false;
            _cloudGate.Release();
        }
    }

    partial void OnAuthSessionChanged(AuthSession? value)
    {
        OnPropertyChanged(nameof(AuthenticationSummary));
        TriggerCloudSyncCommand.NotifyCanExecuteChanged();
    }

    partial void OnCloudDeviceChanged(CloudManagedDeviceSummary? value)
    {
        OnPropertyChanged(nameof(CloudDeviceSummary));
        TriggerCloudSyncCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsCloudConfiguredChanged(bool value)
    {
        OnPropertyChanged(nameof(IsCloudControlsEnabled));
        TriggerCloudSyncCommand.NotifyCanExecuteChanged();
    }

    partial void OnConfirmReenrollInputChanged(string value)
    {
        OnPropertyChanged(nameof(CanExecuteReenroll));
        ExecuteReenrollCommand.NotifyCanExecuteChanged();
    }

    partial void OnReenrollPreviewChanged(EnrollmentRepairPreview? value)
    {
        OnPropertyChanged(nameof(CanExecuteReenroll));
        ExecuteReenrollCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsLocalBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanExecuteReenroll));
        ExecuteReenrollCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanFixEnrollmentUrls));
        FixEnrollmentUrlsCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsCloudBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(IsCloudControlsEnabled));
        TriggerCloudSyncCommand.NotifyCanExecuteChanged();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _targetHostService.HostChanged -= OnHostChanged;
        _localGate.Dispose();
        _cloudGate.Dispose();
    }

    private void OnHostChanged(object? sender, string host)
    {
        if (_disposed)
        {
            return;
        }

        CurrentHost = host;
        Snapshot = null;
        EnrollmentStatus = null;
        ReenrollPreview = null;
        CloudDevice = null;
        _allMdmEvents.Clear();
        MdmEvents.Clear();
        SelectedMdmEvent = null;
        _allImeTimelineEntries.Clear();
        ImeTimelineEntries.Clear();
        ImeFlowSummaries.Clear();
        SelectedImeLogEntry = null;
        SelectedImeFlowSummary = null;
        ImeLogsStatus = "No IME log timeline loaded.";
        _allImeApplications.Clear();
        ImeApplications.Clear();
        SelectedImeApplication = null;
        ImeAppsStatus = "No IME application list loaded.";
        LocalActionStatus = "No local action executed.";
        LocalActionWarnings = string.Empty;
        LocalActionEvidence.Clear();
        PolicyResultStatus = "No policy result generated.";
        PolicyResultWarnings = string.Empty;
        PolicyReportHtmlPath = string.Empty;
        PolicyReportJsonPath = string.Empty;
        PolicyReportHtmlContent = string.Empty;
        PolicyReportJsonContent = string.Empty;
        PolicyResultTotalCount = 0;
        PolicyResultAppliedCount = 0;
        PolicyResultFailedCount = 0;
        PolicyResultUnknownCount = 0;
        PolicyResultDeviceCount = 0;
        PolicyResultUserCount = 0;
        PolicyResultUnknownScopeCount = 0;
        _requestedMdmEventCount = 0;
        CanLoadMoreMdmEvents = true;
        ConfirmReenrollInput = string.Empty;
        if (string.IsNullOrWhiteSpace(host))
        {
            ApplyDisconnectedState();
            return;
        }

        _ = RefreshActiveSectionAsync(_targetHostService.CaptureSelection());
    }

    private async Task RefreshActiveSectionAsync(HostSelection selection)
    {
        Interlocked.Increment(ref _suppressStatusLogDepth);
        try
        {
            switch ((IntuneAgentSection)SelectedSectionIndex)
            {
                case IntuneAgentSection.LocalDiagnostics:
                    await RefreshDiagnosticsAsync();
                    break;
                case IntuneAgentSection.Enrollment:
                    await RunEnrollmentCheckAsync();
                    break;
                case IntuneAgentSection.MdmEvents:
                    await LoadMdmEventsAsync();
                    break;
                case IntuneAgentSection.ImeApplications:
                    await LoadImeApplicationsAsync();
                    break;
                case IntuneAgentSection.ImeLogs:
                    await LoadImeLogTimelineAsync();
                    break;
                case IntuneAgentSection.LocalActions:
                    SetStatus($"Local actions ready for '{CurrentHost}'.");
                    break;
                case IntuneAgentSection.PolicyResult:
                    SetStatus($"Policy result ready for '{CurrentHost}'.");
                    break;
                case IntuneAgentSection.Cloud:
                    await RefreshCloudAsync(CancellationToken.None, logNoSession: false, selection);
                    break;
                default:
                    await RefreshOverviewAsync(selection.CancellationToken);
                    break;
            }
        }
        finally
        {
            Interlocked.Decrement(ref _suppressStatusLogDepth);
        }
    }

    private static ILogger ResolveLogger(IServiceProvider services)
    {
        if (services.GetService(typeof(ILoggerFactory)) is ILoggerFactory factory)
        {
            return factory.CreateLogger(nameof(IntuneAgentViewModel));
        }

        return NullLogger.Instance;
    }

    private static Task CopyOrWritePolicyArtifactAsync(string sourcePath, string targetPath, string content, bool hasSourceFile)
    {
        if (hasSourceFile)
        {
            return Task.Run(() => File.Copy(sourcePath, targetPath, overwrite: true));
        }

        return File.WriteAllTextAsync(targetPath, content, CancellationToken.None);
    }

    private bool EnsureCloudServicesAvailable()
    {
        if (_cloudAvailabilityChecked)
        {
            return IsCloudConfigured;
        }

        _cloudAvailabilityChecked = true;

        try
        {
            _authService = _services.GetRequiredService<IAuthService>();
            _cloudManagedDeviceService = _services.GetRequiredService<ICloudManagedDeviceService>();
            IsCloudConfigured = true;
            CloudConfigurationWarning = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            IsCloudConfigured = false;
            CloudDevice = null;
            AuthSession = null;
            CloudConfigurationWarning = BuildCloudConfigurationWarning(ex.Message);
            CloudStatus = CloudConfigurationWarning;
            _logger.LogWarning(ex, "Cloud services unavailable for Intune Agent.");
            return false;
        }
    }

    private static string BuildCloudConfigurationWarning(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return "Cloud features are unavailable. Configure Microsoft Graph sign-in to enable this page.";
        }

        var normalized = message.Trim();
        if (normalized.Contains("Intune:ClientId must be configured", StringComparison.OrdinalIgnoreCase))
        {
            return "Cloud features are disabled: 'Intune:ClientId' is not configured for Microsoft Graph login.";
        }

        if (normalized.Contains("No service for type", StringComparison.OrdinalIgnoreCase))
        {
            return "Cloud features are disabled: required cloud services are not registered in this runtime.";
        }

        return $"Cloud features are unavailable: {normalized}";
    }

    private string BuildEnrollmentSummary(EnrollmentStatus? status)
    {
        if (status is null)
        {
            return "No enrollment status loaded.";
        }

        var detected = status.EnrollmentDetected ? "Enrollment detected" : "Enrollment not detected";
        var urlState = status.EnrollmentUrls.AreExpected
            ? "URLs OK"
            : "URLs need attention";
        return $"{detected}; {urlState}; last sync: {status.LastSyncText}; checks: {status.Checks.Count}; warnings: {status.Warnings.Count}.";
    }

    partial void OnEnrollmentStatusChanged(EnrollmentStatus? value)
    {
        OnPropertyChanged(nameof(CanFixEnrollmentUrls));
        FixEnrollmentUrlsCommand.NotifyCanExecuteChanged();
    }

    private string GetExportDirectory()
    {
        return Path.Combine(AppContext.BaseDirectory, "logs", "intune-agent");
    }

    private async Task LaunchExternalHelperScriptAsync(string helperScriptBody, string scriptKey, string displayName)
    {
        if (!OperatingSystem.IsWindows())
        {
            LocalActionStatus = $"External script launch is only supported on Windows. '{displayName}' was not started.";
            LocalActionWarnings = string.Empty;
            LocalActionEvidence.Clear();
            SetStatus(LocalActionStatus);
            return;
        }

        var host = CurrentHost?.Trim();
        if (string.IsNullOrWhiteSpace(host))
        {
            LocalActionStatus = DisconnectedStatus;
            LocalActionWarnings = string.Empty;
            LocalActionEvidence.Clear();
            SetStatus(LocalActionStatus);
            return;
        }

        try
        {
            var workingDirectory = GetExternalScriptWorkingDirectory(host, scriptKey);
            Directory.CreateDirectory(workingDirectory);

            var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            var helperScriptPath = Path.Combine(workingDirectory, $"{scriptKey}.{stamp}.ps1");
            var launcherScriptPath = Path.Combine(workingDirectory, $"{scriptKey}.{stamp}.launcher.ps1");

            await File.WriteAllTextAsync(helperScriptPath, helperScriptBody, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            await File.WriteAllTextAsync(
                launcherScriptPath,
                BuildExternalHelperLauncherScript(host, helperScriptPath, displayName),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoExit -ExecutionPolicy Bypass -File \"{launcherScriptPath}\"",
                WorkingDirectory = workingDirectory,
                UseShellExecute = true
            };

            _ = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start external PowerShell window.");

            LocalActionStatus = $"Opened external PowerShell window for '{displayName}' on '{host}'.";
            LocalActionWarnings = "The script runs in a separate PowerShell window and remains open after execution.";
            LocalActionEvidence.Clear();
            LocalActionEvidence.Add(new NameValueItem("LauncherScript", launcherScriptPath));
            LocalActionEvidence.Add(new NameValueItem("HelperScript", helperScriptPath));
            SetStatus(LocalActionStatus);
        }
        catch (Exception ex)
        {
            LocalActionStatus = $"Launching external script '{displayName}' failed: {ex.Message}";
            LocalActionWarnings = string.Empty;
            LocalActionEvidence.Clear();
            SetStatus(LocalActionStatus, ex);
        }
    }

    private static string BuildExternalHelperLauncherScript(string host, string helperScriptPath, string displayName)
    {
        var escapedHost = EscapePowerShellSingleQuotedString(host);
        var escapedHelperScriptPath = EscapePowerShellSingleQuotedString(helperScriptPath);
        var escapedDisplayName = EscapePowerShellSingleQuotedString(displayName);

        return
            "$ErrorActionPreference='Stop'" + Environment.NewLine +
            "$hostName = '" + escapedHost + "'" + Environment.NewLine +
            "$helperScriptPath = '" + escapedHelperScriptPath + "'" + Environment.NewLine +
            "$displayName = '" + escapedDisplayName + "'" + Environment.NewLine +
            "function Show-HelperResult {" + Environment.NewLine +
            "  param([object]$Payload)" + Environment.NewLine +
            "  if ($null -eq $Payload) { return }" + Environment.NewLine +
            "  if (-not [string]::IsNullOrWhiteSpace([string]$Payload.Message)) {" + Environment.NewLine +
            "    Write-Host $Payload.Message -ForegroundColor Green" + Environment.NewLine +
            "  }" + Environment.NewLine +
            "  if (-not [string]::IsNullOrWhiteSpace([string]$Payload.InstalledVersion)) {" + Environment.NewLine +
            "    Write-Host ('InstalledVersion: ' + [string]$Payload.InstalledVersion) -ForegroundColor DarkGray" + Environment.NewLine +
            "  }" + Environment.NewLine +
            "  if (-not [string]::IsNullOrWhiteSpace([string]$Payload.ScriptPath)) {" + Environment.NewLine +
            "    Write-Host ('ScriptPath: ' + [string]$Payload.ScriptPath) -ForegroundColor DarkGray" + Environment.NewLine +
            "  }" + Environment.NewLine +
            "  if ($Payload.Warnings) {" + Environment.NewLine +
            "    foreach ($warning in @($Payload.Warnings)) {" + Environment.NewLine +
            "      if (-not [string]::IsNullOrWhiteSpace([string]$warning)) { Write-Warning ([string]$warning) }" + Environment.NewLine +
            "    }" + Environment.NewLine +
            "  }" + Environment.NewLine +
            "  $outputText = [string]$Payload.OutputText" + Environment.NewLine +
            "  if (-not [string]::IsNullOrWhiteSpace($outputText)) {" + Environment.NewLine +
            "    Write-Host ''" + Environment.NewLine +
            "    Write-Host $outputText" + Environment.NewLine +
            "  }" + Environment.NewLine +
            "}" + Environment.NewLine +
            "function Invoke-HelperScript {" + Environment.NewLine +
            "  param([string]$Path)" + Environment.NewLine +
            "  $rawResult = & $Path | Out-String" + Environment.NewLine +
            "  if ([string]::IsNullOrWhiteSpace($rawResult)) { return $null }" + Environment.NewLine +
            "  try { return $rawResult | ConvertFrom-Json -ErrorAction Stop } catch { return [PSCustomObject]@{ Message = 'Raw script output'; OutputText = $rawResult.Trim() } }" + Environment.NewLine +
            "}" + Environment.NewLine +
            "$localNames = @('localhost', '.', $env:COMPUTERNAME)" + Environment.NewLine +
            "$isLocal = $localNames -contains $hostName" + Environment.NewLine +
            "Write-Host ('Launching ' + $displayName + ' for host ' + $hostName + '...') -ForegroundColor Cyan" + Environment.NewLine +
            "if ($isLocal) {" + Environment.NewLine +
            "  $payload = Invoke-HelperScript -Path $helperScriptPath" + Environment.NewLine +
            "  Show-HelperResult -Payload $payload" + Environment.NewLine +
            "  Write-Host ''" + Environment.NewLine +
            "  Write-Host 'The PowerShell window remains open.' -ForegroundColor Yellow" + Environment.NewLine +
            "  return" + Environment.NewLine +
            "}" + Environment.NewLine +
            "$session = $null" + Environment.NewLine +
            "try {" + Environment.NewLine +
            "  $session = New-PSSession -ComputerName $hostName -ErrorAction Stop" + Environment.NewLine +
            "  Write-Host ('Connected to ' + $hostName + '. Running helper script...') -ForegroundColor Cyan" + Environment.NewLine +
            "  $payload = Invoke-Command -Session $session -FilePath $helperScriptPath -ErrorAction Stop | Out-String | ConvertFrom-Json -ErrorAction Stop" + Environment.NewLine +
            "  Show-HelperResult -Payload $payload" + Environment.NewLine +
            "  Write-Host ''" + Environment.NewLine +
            "  Write-Host ('Remote PowerShell session to ' + $hostName + ' remains open. Type Exit-PSSession when finished.') -ForegroundColor Yellow" + Environment.NewLine +
            "  Enter-PSSession -Session $session" + Environment.NewLine +
            "} catch {" + Environment.NewLine +
            "  Write-Error $_.Exception.Message" + Environment.NewLine +
            "} finally {" + Environment.NewLine +
            "  if ($null -ne $session) { try { Remove-PSSession -Session $session -ErrorAction SilentlyContinue } catch { } }" + Environment.NewLine +
            "}" + Environment.NewLine +
            "Write-Host ''" + Environment.NewLine +
            "Write-Host 'The PowerShell window remains open.' -ForegroundColor Yellow" + Environment.NewLine;
    }

    private static string LoadEmbeddedIntuneHelperScript(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            throw new InvalidOperationException("Embedded helper script file name must be provided.");
        }

        var assembly = typeof(LocalIntuneActionResult).Assembly;
        var resourceName = assembly
            .GetManifestResourceNames()
            .FirstOrDefault(name => name.EndsWith($".{fileName}", StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(resourceName))
        {
            throw new InvalidOperationException($"Embedded helper script '{fileName}' was not found.");
        }

        using var stream = assembly.GetManifestResourceStream(resourceName)
                            ?? throw new InvalidOperationException($"Embedded helper script '{fileName}' could not be opened.");
        using var reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static string GetExternalScriptWorkingDirectory(string host, string scriptKey)
    {
        var safeHost = Regex.Replace(host.Trim(), @"[^\w\.-]", "_", RegexOptions.CultureInvariant);
        var safeScriptKey = Regex.Replace(scriptKey.Trim(), @"[^\w\.-]", "_", RegexOptions.CultureInvariant);
        return Path.Combine(AppContext.BaseDirectory, "logs", "intune-agent", "external-scripts", safeHost, safeScriptKey);
    }

    private static string EscapePowerShellSingleQuotedString(string value)
    {
        return string.IsNullOrEmpty(value)
            ? string.Empty
            : value.Replace("'", "''", StringComparison.Ordinal);
    }

    private static string GetPolicyResultWorkingDirectory(string host)
    {
        var safeHost = Regex.Replace(
            string.IsNullOrWhiteSpace(host) ? "localhost" : host.Trim(),
            @"[^\w\.-]",
            "_",
            RegexOptions.CultureInvariant);
        if (string.IsNullOrWhiteSpace(safeHost))
        {
            safeHost = "localhost";
        }

        return Path.Combine(Path.GetTempPath(), "WindowsClientCenter", "policy-result", safeHost);
    }

    private static void TryCleanupPolicyWorkingDirectory(string? workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory))
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), "WindowsClientCenter", "policy-result");
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedCandidate = Path.GetFullPath(workingDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var prefix = normalizedRoot + Path.DirectorySeparatorChar;
        if (!normalizedCandidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            Directory.Delete(normalizedCandidate, recursive: true);
        }
        catch
        {
            // Best effort cleanup only.
        }
    }

    private void ApplyPolicyResultReport(IntunePolicyResultReport report)
    {
        PolicyReportHtmlPath = report.ExportHtmlPath;
        PolicyReportJsonPath = report.ExportJsonPath;
        PolicyReportHtmlContent = TryReadText(report.ExportHtmlPath);
        PolicyReportJsonContent = TryReadText(report.ExportJsonPath);
        PolicyResultWarnings = report.Warnings.Count == 0
            ? string.Empty
            : string.Join(Environment.NewLine, report.Warnings);
        PolicyResultTotalCount = report.Summary.TotalCount;
        PolicyResultAppliedCount = report.Summary.AppliedCount;
        PolicyResultFailedCount = report.Summary.FailedCount;
        PolicyResultUnknownCount = report.Summary.UnknownCount;
        PolicyResultDeviceCount = report.Summary.DeviceCount;
        PolicyResultUserCount = report.Summary.UserCount;
        PolicyResultUnknownScopeCount = report.Summary.UnknownScopeCount;
    }

    private static string TryReadText(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return string.Empty;
        }

        try
        {
            return File.ReadAllText(path);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static void TryDeleteFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch
        {
            // Best effort cleanup only.
        }
    }

    private IDisposable BeginLongRunningLocalAction(string label)
    {
        _longRunningLocalActionDepth++;
        IsLongRunningLocalAction = true;
        LongRunningLocalActionLabel = string.IsNullOrWhiteSpace(label)
            ? "Running long operation..."
            : label.Trim();

        return new LongRunningLocalActionScope(this);
    }

    private void EndLongRunningLocalAction()
    {
        if (_longRunningLocalActionDepth > 0)
        {
            _longRunningLocalActionDepth--;
        }

        if (_longRunningLocalActionDepth != 0)
        {
            return;
        }

        IsLongRunningLocalAction = false;
        LongRunningLocalActionLabel = string.Empty;
    }

    private async Task ExecuteLocalActionAsync(
        Func<ValueTask<LocalIntuneActionResult>> execute,
        string successFallbackStatus,
        bool markAsLongRunning = false,
        string? longRunningLabel = null)
    {
        var totalTimer = StartVerboseTimer();
        await _localGate.WaitAsync();
        IDisposable? longRunningScope = null;
        try
        {
            IsLocalBusy = true;
            if (!TryGetConnectedHost(out _))
            {
                LocalActionStatus = DisconnectedStatus;
                LocalActionWarnings = string.Empty;
                LocalActionEvidence.Clear();
                SetStatus(LocalActionStatus);
                return;
            }

            if (markAsLongRunning)
            {
                longRunningScope = BeginLongRunningLocalAction(longRunningLabel ?? successFallbackStatus);
            }

            var result = await MeasureOperationAsync(successFallbackStatus, () => execute().AsTask());
            LocalActionStatus = string.IsNullOrWhiteSpace(result.Message) ? successFallbackStatus : result.Message;
            LocalActionWarnings = result.Warnings.Count == 0 ? string.Empty : string.Join(Environment.NewLine, result.Warnings);
            LocalActionEvidence.Clear();
            foreach (var pair in result.Evidence)
            {
                LocalActionEvidence.Add(new NameValueItem(pair.Key, pair.Value));
            }

            if (result.Success)
            {
                SetStatus(LocalActionStatus);
            }
            else
            {
                SetStatus(LocalActionStatus, new InvalidOperationException(LocalActionStatus));
            }
        }
        catch (Exception ex)
        {
            LocalActionStatus = ex.Message;
            LocalActionWarnings = string.Empty;
            LocalActionEvidence.Clear();
            SetStatus(LocalActionStatus, ex);
        }
        finally
        {
            LogVerboseDuration(successFallbackStatus, totalTimer);
            longRunningScope?.Dispose();
            IsLocalBusy = false;
            _localGate.Release();
        }
    }

    private sealed class LongRunningLocalActionScope(IntuneAgentViewModel owner) : IDisposable
    {
        private IntuneAgentViewModel? _owner = owner;

        public void Dispose()
        {
            var instance = Interlocked.Exchange(ref _owner, null);
            if (instance is null)
            {
                return;
            }

            instance.EndLongRunningLocalAction();
        }
    }

    private void RefreshLocalActionOutputText()
    {
        var outputText = LocalActionEvidence
            .FirstOrDefault(static item => string.Equals(item.Name, "outputText", StringComparison.OrdinalIgnoreCase))
            ?.Value ?? string.Empty;

        if (!string.Equals(LocalActionOutputText, outputText, StringComparison.Ordinal))
        {
            LocalActionOutputText = outputText;
        }
    }

    partial void OnSelectedCommunityScriptChanged(string value)
    {
        OnPropertyChanged(nameof(IsAutopilotCommunityScriptSelected));
        OnPropertyChanged(nameof(SelectedCommunityScriptDescription));
    }

    private void SetStatus(string message, Exception? ex = null)
    {
        OverallStatus = message;

        if (Volatile.Read(ref _suppressStatusLogDepth) == 0 &&
            !string.Equals(_lastForwardedMessage, message, StringComparison.Ordinal))
        {
            _hostStatusLogSink?.Append($"[Intune Agent] {message}");
            _lastForwardedMessage = message;
        }

        if (ex is not null)
        {
            _logger.LogError(ex, "Intune Agent operation failed: {Message}", message);
        }
        else
        {
            _logger.LogInformation("Intune Agent: {Message}", message);
        }
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

    private async Task MeasureOperationAsync(string operationName, Func<Task> operation)
    {
        if (!_verboseOperationsEnabled)
        {
            await operation();
            return;
        }

        var timer = Stopwatch.StartNew();
        try
        {
            await operation();
            LogVerboseOperation($"{operationName} completed in {timer.ElapsedMilliseconds} ms.");
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

        _hostStatusLogSink.Append($"[Intune Agent][Verbose] {message.Trim()}");
    }

    private void LogPolicyResultTimings(IReadOnlyList<string> timings)
    {
        if (timings.Count == 0)
        {
            return;
        }

        foreach (var timing in timings)
        {
            LogVerboseOperation($"Policy result: {timing}");
        }
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

    private void LogImeApplicationDiagnostics(IReadOnlyList<ImeApplicationStatusEntry> items)
    {
        if (_hostStatusLogSink is null)
        {
            return;
        }

        var inProgressCount = items.Count(entry => string.Equals(entry.InstallStatus, "InProgress", StringComparison.OrdinalIgnoreCase));
        var installedCount = items.Count(entry => string.Equals(entry.InstallStatus, "Installed", StringComparison.OrdinalIgnoreCase));
        var failedCount = items.Count(entry => string.Equals(entry.InstallStatus, "Failed", StringComparison.OrdinalIgnoreCase));

        _hostStatusLogSink.Append(
            $"[Intune Agent][Diag] IME apps loaded={items.Count}; installed={installedCount}; inProgress={inProgressCount}; failed={failedCount}; host={CurrentHost}");

        var hasUnknown = items.Any(entry => string.Equals(entry.InstallStatus, "Unknown", StringComparison.OrdinalIgnoreCase));
        var shouldLogDeepDiagnostics = installedCount == 0 && (inProgressCount > 0 || hasUnknown);
        if (!shouldLogDeepDiagnostics)
        {
            return;
        }

        LogCompanyPortalRegistryProbe();

        var focusEntries = items
            .Where(entry => string.Equals(entry.AppId, CompanyPortalAppId, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (focusEntries.Length == 0)
        {
            focusEntries = items
                .Where(entry =>
                    string.Equals(entry.InstallStatus, "InProgress", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(entry.InstallStatus, "Unknown", StringComparison.OrdinalIgnoreCase))
                .Take(5)
                .ToArray();
        }

        if (focusEntries.Length == 0)
        {
            _hostStatusLogSink.Append("[Intune Agent][Diag] No focus app found (Company Portal missing; no InProgress/Unknown apps).");
            return;
        }

        foreach (var entry in focusEntries)
        {
            _hostStatusLogSink.Append(
                $"[Intune Agent][Diag] App '{entry.AppName}' ({entry.AppId}) status={entry.InstallStatus}; installedAny={entry.IsInstalledForAnyIdentity}; intent={entry.Intent}; installContext={entry.InstallContextSummary}; target={entry.TargetInstallContext}; result={SafeValue(entry.ResultCode)}; source={SafeValue(entry.SourceFile)}");
            _hostStatusLogSink.Append(
                $"[Intune Agent][Diag] App message={TruncateForLog(SafeValue(entry.LastMessage), 800)}");

            if (entry.IdentityStatuses.Count == 0)
            {
                _hostStatusLogSink.Append("[Intune Agent][Diag] Identity statuses: <none>");
                continue;
            }

            foreach (var identity in entry.IdentityStatuses.Take(8))
            {
                _hostStatusLogSink.Append(
                    $"[Intune Agent][Diag] Identity {identity.IdentityId} ({identity.Scope}) status={identity.InstallStatus}; app={identity.ApplicabilityStatus}; dep={identity.DependencyStatus}; result={SafeValue(identity.ResultCode)}; source={SafeValue(identity.Source)}");
                _hostStatusLogSink.Append(
                    $"[Intune Agent][Diag] Identity details={TruncateForLog(SafeValue(identity.Details), 1200)}");
            }
        }
    }

    private static string SafeValue(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "<empty>" : value.Trim();

    private static string TruncateForLog(string value, int maxLength)
    {
        if (value.Length <= maxLength)
        {
            return value;
        }

        return $"{value[..maxLength]}...[truncated]";
    }

    private void LogCompanyPortalRegistryProbe()
    {
        if (_hostStatusLogSink is null || !OperatingSystem.IsWindows())
        {
            return;
        }

        var normalizedHost = CurrentHost?.Trim() ?? string.Empty;
        if (normalizedHost.Equals(_lastRegistryProbeHost, StringComparison.OrdinalIgnoreCase) &&
            DateTimeOffset.UtcNow - _lastRegistryProbeAt < TimeSpan.FromMinutes(5))
        {
            return;
        }

        _lastRegistryProbeHost = normalizedHost;
        _lastRegistryProbeAt = DateTimeOffset.UtcNow;

        const string reportingRelativePath = @"SOFTWARE\Microsoft\IntuneManagementExtension\Win32Apps\Reporting\00000000-0000-0000-0000-000000000000\032937f7-c5a4-48a3-bcf6-ad78a2b0373b";
        const string win32AppsRootPath = @"SOFTWARE\Microsoft\IntuneManagementExtension\Win32Apps";

        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var reportingKey = baseKey.OpenSubKey(reportingRelativePath, writable: false);
                var reportingFound = reportingKey is not null;
                var reportingValueCount = reportingKey?.GetValueNames().Length ?? 0;

                var appKeyMatches = 0;
                using var win32Root = baseKey.OpenSubKey(win32AppsRootPath, writable: false);
                if (win32Root is not null)
                {
                    foreach (var identityName in win32Root.GetSubKeyNames())
                    {
                        if (string.Equals(identityName, "Reporting", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(identityName, "OperationalState", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        using var identityKey = win32Root.OpenSubKey(identityName, writable: false);
                        if (identityKey is null)
                        {
                            continue;
                        }

                        foreach (var childName in identityKey.GetSubKeyNames())
                        {
                            if (childName.Contains(CompanyPortalAppId, StringComparison.OrdinalIgnoreCase))
                            {
                                appKeyMatches++;
                            }
                        }
                    }
                }

                _hostStatusLogSink.Append(
                    $"[Intune Agent][Diag] Registry probe view={view}: reportingFound={reportingFound}; reportingValueCount={reportingValueCount}; win32AppKeyMatches={appKeyMatches}; systemIdentity={SystemIdentityId}");
            }
            catch (Exception ex)
            {
                _hostStatusLogSink.Append($"[Intune Agent][Diag] Registry probe view={view} failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    partial void OnMdmEventIdFilterChanged(string value)
    {
        ApplyMdmEventFilters(updateStatus: false);
    }

    partial void OnShowMdmCriticalEventsChanged(bool value)
    {
        ApplyMdmEventFilters(updateStatus: false);
    }

    partial void OnShowMdmErrorEventsChanged(bool value)
    {
        ApplyMdmEventFilters(updateStatus: false);
    }

    partial void OnShowMdmWarningEventsChanged(bool value)
    {
        ApplyMdmEventFilters(updateStatus: false);
    }

    partial void OnShowMdmInfoEventsChanged(bool value)
    {
        ApplyMdmEventFilters(updateStatus: false);
    }

    partial void OnMdmEventLoadCountChanged(int value)
    {
        if (value <= 0)
        {
            MdmEventLoadCount = 150;
        }
    }

    partial void OnImeLogFilePatternChanged(string value)
    {
        ApplyImeLogFilters(updateStatus: false);
    }

    partial void OnImeLogSearchTextChanged(string value)
    {
        ApplyImeLogFilters(updateStatus: false);
    }

    partial void OnImeFlowSearchTextChanged(string value)
    {
        ApplyImeLogFilters(updateStatus: true);
    }

    partial void OnImeFlowTypeFilterChanged(string value)
    {
        ApplyImeLogFilters(updateStatus: true);
    }

    partial void OnImeTimelineComponentFilterChanged(string value)
    {
        if (_isRefreshingImeTimelineComponentOptions)
        {
            return;
        }

        ApplyImeLogFilters(updateStatus: true);
    }

    partial void OnShowImeFailedFlowsOnlyChanged(bool value)
    {
        ApplyImeLogFilters(updateStatus: true);
    }

    partial void OnShowImeIncompleteFlowsOnlyChanged(bool value)
    {
        ApplyImeLogFilters(updateStatus: true);
    }

    partial void OnShowImeLatestFlowRunsOnlyChanged(bool value)
    {
        ApplyImeLogFilters(updateStatus: true);
    }

    partial void OnFocusSelectedImeFlowChanged(bool value)
    {
        ApplyImeLogFilters(updateStatus: true);
    }

    partial void OnSelectedImeFlowSummaryChanged(ImeFlowSummaryEntry? value)
    {
        ApplyImeLogFilters(updateStatus: true);
    }

    partial void OnShowImeErrorEntriesChanged(bool value)
    {
        ApplyImeLogFilters(updateStatus: false);
    }

    partial void OnShowImeWarningEntriesChanged(bool value)
    {
        ApplyImeLogFilters(updateStatus: false);
    }

    partial void OnShowImeInformationEntriesChanged(bool value)
    {
        ApplyImeLogFilters(updateStatus: false);
    }

    partial void OnShowImeVerboseEntriesChanged(bool value)
    {
        ApplyImeLogFilters(updateStatus: false);
    }

    partial void OnImeTimelineMaxLinesChanged(int value)
    {
        if (value <= 0)
        {
            ImeTimelineMaxLines = 400;
        }
    }

    partial void OnImeAppSearchTextChanged(string value)
    {
        ApplyImeApplicationFilters(updateStatus: false);
    }

    partial void OnImeAppStatusFilterChanged(string value)
    {
        ApplyImeApplicationFilters(updateStatus: false);
    }

    partial void OnImeApplicationFlowFilterChanged(string value)
    {
        if (_isRefreshingImeApplicationFlowOptions)
        {
            return;
        }

        ApplyImeApplicationFilters(updateStatus: false);
    }

    partial void OnShowImeAppsWithoutIntentChanged(bool value)
    {
        ApplyImeApplicationFilters(updateStatus: false);
    }

    partial void OnShowImeSystemPlaceholderAppsChanged(bool value)
    {
        ApplyImeApplicationFilters(updateStatus: false);
    }

    partial void OnSelectedImeApplicationChanged(ImeApplicationStatusEntry? value)
    {
        SelectedImeApplicationIdentityStatuses.Clear();
        if (value?.IdentityStatuses is null)
        {
            return;
        }

        if (_hostStatusLogSink is not null && !string.IsNullOrWhiteSpace(value.LastMessage))
        {
            _hostStatusLogSink.Append(
                $"[Intune Agent] {value.AppName} ({value.AppId}) message: {TruncateForLog(value.LastMessage, 1200)}");
        }

        foreach (var identityStatus in value.IdentityStatuses)
        {
            SelectedImeApplicationIdentityStatuses.Add(identityStatus);
        }
    }

    private void ApplyMdmEventFilters(bool updateStatus)
    {
        IEnumerable<MdmEventAnalysisEntry> filtered = _allMdmEvents.Where(MatchesMdmEventFilters);
        filtered = filtered.OrderByDescending(entry => entry.TimeCreated ?? DateTimeOffset.MinValue);

        var selectedRecordId = SelectedMdmEvent?.RecordId;
        var selectedId = SelectedMdmEvent?.Id;

        MdmEvents.Clear();
        foreach (var entry in filtered)
        {
            MdmEvents.Add(entry);
        }

        SelectedMdmEvent = MdmEvents.FirstOrDefault(entry =>
                               selectedRecordId.HasValue && entry.RecordId == selectedRecordId) ??
                           MdmEvents.FirstOrDefault(entry => entry.Id == selectedId) ??
                           MdmEvents.FirstOrDefault();

        if (!updateStatus)
        {
            return;
        }

        if (_allMdmEvents.Count == 0)
        {
            MdmEventsStatus = "No MDM events loaded.";
            return;
        }

        MdmEventsStatus = $"{MdmEvents.Count} / {_allMdmEvents.Count} events visible";
        if (!CanLoadMoreMdmEvents && _allMdmEvents.Count > 0)
        {
            MdmEventsStatus += " - all available events loaded";
        }
    }

    private bool MatchesMdmEventFilters(MdmEventAnalysisEntry entry)
    {
        var severityVisible = entry.Severity switch
        {
            MdmEventSeverity.Critical => ShowMdmCriticalEvents,
            MdmEventSeverity.Error => ShowMdmErrorEvents,
            MdmEventSeverity.Warning => ShowMdmWarningEvents,
            _ => ShowMdmInfoEvents
        };

        if (!severityVisible)
        {
            return false;
        }

        var filter = MdmEventIdFilter?.Trim();
        if (string.IsNullOrWhiteSpace(filter))
        {
            return true;
        }

        var tokens = filter
            .Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(token => int.TryParse(token, out var parsed) ? parsed : (int?)null)
            .Where(value => value.HasValue)
            .Select(value => value!.Value)
            .ToHashSet();

        return tokens.Count == 0 || tokens.Contains(entry.Id);
    }

    private void ApplyImeLogFilters(bool updateStatus)
    {
        RefreshImeTimelineComponentOptions();

        var baseFilteredEntries = _allImeTimelineEntries
            .Where(MatchesImeLogEntryFilters)
            .ToArray();
        ApplyImeHighlightState();

        var visibleSummaries = BuildImeFlowSummaries(baseFilteredEntries);
        ImeFlowSummaries.Clear();
        foreach (var summary in visibleSummaries)
        {
            ImeFlowSummaries.Add(summary);
        }

        var filtered = baseFilteredEntries
            .OrderBy(entry => entry.TimeCreated ?? DateTimeOffset.MinValue)
            .ThenBy(entry => entry.SourceFile, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.LineNumber);

        var selectedFile = SelectedImeLogEntry?.SourceFile;
        var selectedLine = SelectedImeLogEntry?.LineNumber;

        ImeTimelineEntries.Clear();
        foreach (var entry in filtered)
        {
            ImeTimelineEntries.Add(entry);
        }

        SelectedImeLogEntry = ImeTimelineEntries.FirstOrDefault(entry =>
                                   string.Equals(entry.SourceFile, selectedFile, StringComparison.OrdinalIgnoreCase) &&
                                   entry.LineNumber == selectedLine) ??
                               ImeTimelineEntries.LastOrDefault();

        if (!updateStatus)
        {
            return;
        }

        if (_allImeTimelineEntries.Count == 0)
        {
            ImeLogsStatus = "No IME log timeline loaded.";
            return;
        }

        var policyRows = ImeTimelineEntries.Count(item => item.IsPolicyPayload);
        var structuredRows = ImeTimelineEntries.Count(item => !string.IsNullOrWhiteSpace(item.Flow));
        ImeLogsStatus = $"{ImeTimelineEntries.Count} / {_allImeTimelineEntries.Count} lines visible; components: {ImeTimelineComponentOptions.Count - 1}; structured flow entries: {structuredRows}; policy payload entries: {policyRows}";
    }

    private bool MatchesImeLogEntryFilters(ImeLogTimelineEntry entry)
    {
        var severity = entry.Severity?.Trim() ?? string.Empty;
        var severityVisible = severity.ToLowerInvariant() switch
        {
            "error" => ShowImeErrorEntries,
            "warning" => ShowImeWarningEntries,
            "verbose" => ShowImeVerboseEntries,
            _ => ShowImeInformationEntries
        };

        if (!severityVisible)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(ImeFlowTypeFilter) &&
            !string.Equals(ImeFlowTypeFilter, "All", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(entry.FlowDisplay, ImeFlowTypeFilter, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(ImeTimelineComponentFilter) &&
            !string.Equals(ImeTimelineComponentFilter, "All", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(entry.Component, ImeTimelineComponentFilter, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var filter = ImeLogSearchText?.Trim();
        if (string.IsNullOrWhiteSpace(filter))
        {
            return true;
        }

        return entry.Message.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
               entry.Flow.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
               entry.Phase.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
               entry.Effect.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
               entry.CorrelationSummary.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
               entry.Component.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
               entry.SourceFile.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
               entry.RawLine.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
               entry.PolicyJson.Contains(filter, StringComparison.OrdinalIgnoreCase);
    }

    private void RefreshImeTimelineComponentOptions()
    {
        var selected = ImeTimelineComponentFilter;
        var options = _allImeTimelineEntries
            .Select(entry => entry.Component?.Trim())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .Prepend("All")
            .ToArray();

        _isRefreshingImeTimelineComponentOptions = true;
        try
        {
            ImeTimelineComponentOptions.Clear();
            foreach (var option in options)
            {
                ImeTimelineComponentOptions.Add(option);
            }

            var effectiveSelection = ImeTimelineComponentOptions.Any(option => string.Equals(option, selected, StringComparison.OrdinalIgnoreCase))
                ? selected
                : "All";

            if (!string.Equals(ImeTimelineComponentFilter, effectiveSelection, StringComparison.OrdinalIgnoreCase))
            {
                ImeTimelineComponentFilter = effectiveSelection;
            }
        }
        finally
        {
            _isRefreshingImeTimelineComponentOptions = false;
        }
    }

    private bool MatchesImeFlowSummaryFilters(ImeFlowSummaryEntry entry)
    {
        if (ShowImeFailedFlowsOnly && !entry.IsFailed)
        {
            return false;
        }

        if (ShowImeIncompleteFlowsOnly && entry.IsComplete)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(ImeFlowTypeFilter) &&
            !string.Equals(ImeFlowTypeFilter, "All", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(entry.FlowDisplay, ImeFlowTypeFilter, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var filter = ImeFlowSearchText?.Trim();
        if (string.IsNullOrWhiteSpace(filter))
        {
            return true;
        }

        return entry.FlowDisplay.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
               entry.EntityDisplay.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
               entry.PolicyId.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
               entry.SessionId.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
               entry.Result.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
               entry.ResultCode.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
               entry.Summary.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
               entry.LastMessage.Contains(filter, StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<ImeFlowSummaryEntry> BuildImeFlowSummaries(IEnumerable<ImeLogTimelineEntry> entries)
    {
        return entries
            .Select(entry => new
            {
                Entry = entry,
                Key = BuildImeFlowSummaryKey(entry)
            })
            .Where(item => !string.IsNullOrWhiteSpace(item.Key))
            .GroupBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => CreateImeFlowSummary(group.Key, group.Select(item => item.Entry).ToArray()))
            .OrderByDescending(summary => summary.LastSeenAt ?? DateTimeOffset.MinValue)
            .ThenBy(summary => summary.EntityDisplay, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static ImeFlowSummaryEntry CreateImeFlowSummary(string key, IReadOnlyList<ImeLogTimelineEntry> entries)
    {
        var ordered = entries
            .OrderBy(entry => entry.TimeCreated ?? DateTimeOffset.MinValue)
            .ThenBy(entry => entry.SourceFile, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.LineNumber)
            .ToArray();
        var first = ordered[0];
        var last = ordered[^1];
        var lastPhase = ordered
            .Select(entry => entry.PhaseDisplay)
            .LastOrDefault(phase => !string.Equals(phase, "-", StringComparison.Ordinal)) ?? "-";
        var resultCode = ordered
            .Select(entry => entry.ResultCode)
            .LastOrDefault(code => !string.IsNullOrWhiteSpace(code)) ?? string.Empty;
        var isFailed = ordered.Any(IsImeFailureEntry);
        var isComplete = isFailed || ordered.Any(IsImeCompletionEntry);
        var result = isFailed ? "Failed" : isComplete ? "Succeeded" : "In Progress";
        var attemptCount = Math.Max(1, ordered.Count(IsImeAttemptBoundary));
        var entityType = !string.IsNullOrWhiteSpace(last.EntityType)
            ? last.EntityType
            : !string.IsNullOrWhiteSpace(last.CorrelationSummary)
                ? "Correlation"
                : "Source";
        var entityId = !string.IsNullOrWhiteSpace(last.EntityId)
            ? last.EntityId
            : !string.IsNullOrWhiteSpace(last.CorrelationSummary)
                ? last.CorrelationSummary
                : last.SourceFile;

        return new ImeFlowSummaryEntry(
            key,
            last.FlowDisplay,
            entityType,
            entityId,
            ordered.Select(entry => entry.PolicyId).LastOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty,
            ordered.Select(entry => entry.SessionId).LastOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty,
            ordered.Select(entry => entry.UserId).LastOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty,
            first.TimeCreated,
            last.TimeCreated,
            lastPhase,
            result,
            resultCode,
            ordered.Length,
            attemptCount,
            isComplete,
            isFailed,
            BuildImeFlowSummaryText(ordered, lastPhase, result, resultCode),
            TrimImeMessage(last.Message));
    }

    private static string BuildImeFlowSummaryKey(ImeLogTimelineEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.Flow) &&
            string.IsNullOrWhiteSpace(entry.EntityId) &&
            string.IsNullOrWhiteSpace(entry.CorrelationSummary) &&
            string.IsNullOrWhiteSpace(entry.ResultCode))
        {
            return string.Empty;
        }

        var entityType = !string.IsNullOrWhiteSpace(entry.EntityType)
            ? entry.EntityType
            : !string.IsNullOrWhiteSpace(entry.CorrelationSummary)
                ? "Correlation"
                : "Source";
        var entityId = !string.IsNullOrWhiteSpace(entry.EntityId)
            ? entry.EntityId
            : !string.IsNullOrWhiteSpace(entry.CorrelationSummary)
                ? entry.CorrelationSummary
                : entry.SourceFile;
        var runKey = !string.IsNullOrWhiteSpace(entry.SessionId)
            ? $"session:{entry.SessionId}"
            : !string.IsNullOrWhiteSpace(entry.PolicyId)
                ? $"policy:{entry.PolicyId}"
                : "run:default";

        return $"{entry.FlowDisplay}|{entityType}|{entityId}|{runKey}";
    }

    private static string BuildImeLatestFlowKey(ImeFlowSummaryEntry entry)
    {
        return $"{entry.FlowDisplay}|{entry.EntityType}|{entry.EntityId}";
    }

    private static string BuildImeHighlightKey(ImeLogTimelineEntry entry)
    {
        var summaryKey = BuildImeFlowSummaryKey(entry);
        if (!string.IsNullOrWhiteSpace(summaryKey))
        {
            return summaryKey;
        }

        if (!string.IsNullOrWhiteSpace(entry.CorrelationSummary))
        {
            return $"correlation|{entry.CorrelationSummary}";
        }

        return $"{entry.SourceFile}|{entry.LineNumber}";
    }

    private void ApplyImeHighlightState()
    {
        foreach (var entry in _allImeTimelineEntries)
        {
            entry.IsRelatedHighlight = false;
        }

        if (string.IsNullOrWhiteSpace(_imeHighlightedFlowKey))
        {
            return;
        }

        var matchingEntries = _allImeTimelineEntries
            .Where(entry => string.Equals(BuildImeHighlightKey(entry), _imeHighlightedFlowKey, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (matchingEntries.Length == 0)
        {
            _imeHighlightedFlowKey = string.Empty;
            return;
        }

        foreach (var entry in matchingEntries)
        {
            entry.IsRelatedHighlight = true;
        }
    }

    private static bool IsImeFailureEntry(ImeLogTimelineEntry entry)
    {
        if (IsFailedLike(entry.Severity, entry.ResultCode))
        {
            return true;
        }

        return Regex.IsMatch(entry.Message, @"(?i)\b(fail(?:ed|ure)?|error|exception|timeout|abort(?:ed)?|denied)\b");
    }

    private static bool IsImeCompletionEntry(ImeLogTimelineEntry entry)
    {
        if (Regex.IsMatch(entry.Message, @"(?i)\b(success|succeeded|completed|finished|sent successfully|reported successfully|saved .* results)\b"))
        {
            return true;
        }

        return string.Equals(entry.Phase, "reporting", StringComparison.OrdinalIgnoreCase) &&
               !IsImeFailureEntry(entry);
    }

    private static bool IsImeAttemptBoundary(ImeLogTimelineEntry entry)
    {
        return Regex.IsMatch(entry.Message, @"(?i)\b(start(?:ed|ing)?|request(?:ed|ing)?|retry|attempt|check-?in|sync now)\b");
    }

    private static string BuildImeFlowSummaryText(IReadOnlyList<ImeLogTimelineEntry> entries, string lastPhase, string result, string resultCode)
    {
        var last = entries[^1];
        var trimmedMessage = TrimImeMessage(last.Message);
        if (string.Equals(result, "Failed", StringComparison.OrdinalIgnoreCase))
        {
            return string.IsNullOrWhiteSpace(resultCode)
                ? $"Failed in {lastPhase}. {trimmedMessage}"
                : $"Failed in {lastPhase} with {resultCode}. {trimmedMessage}";
        }

        if (string.Equals(result, "Succeeded", StringComparison.OrdinalIgnoreCase))
        {
            return $"Completed in {lastPhase}. {trimmedMessage}";
        }

        return $"Last observed phase {lastPhase}. {trimmedMessage}";
    }

    private static string TrimImeMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return "-";
        }

        var normalized = Regex.Replace(message.Trim(), @"\s+", " ");
        return normalized.Length <= 160 ? normalized : normalized[..157] + "...";
    }

    private void ApplyImeApplicationFilters(bool updateStatus)
    {
        RefreshImeApplicationFlowOptions();

        IEnumerable<ImeApplicationStatusEntry> filtered = _allImeApplications.Where(MatchesImeApplicationFilters);
        filtered = filtered
            .OrderBy(entry => entry.AppName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.AppId, StringComparer.OrdinalIgnoreCase);

        var selectedAppId = SelectedImeApplication?.AppId;
        ImeApplications.Clear();
        foreach (var entry in filtered)
        {
            ImeApplications.Add(entry);
        }

        SelectedImeApplication = ImeApplications.FirstOrDefault(entry => string.Equals(entry.AppId, selectedAppId, StringComparison.OrdinalIgnoreCase))
                                 ?? ImeApplications.FirstOrDefault();

        if (!updateStatus)
        {
            return;
        }

        if (_allImeApplications.Count == 0)
        {
            ImeAppsStatus = "No IME application list loaded.";
            return;
        }

        var failedCount = ImeApplications.Count(entry => string.Equals(entry.InstallStatus, "Failed", StringComparison.OrdinalIgnoreCase));
        var installedAnyCount = ImeApplications.Count(entry => entry.IsInstalledForAnyIdentity);
        ImeAppsStatus = $"{ImeApplications.Count} / {_allImeApplications.Count} apps visible; failed: {failedCount}; installed (any user/system): {installedAnyCount}";
    }

    private bool MatchesImeApplicationFilters(ImeApplicationStatusEntry entry)
    {
        if (!ShowImeSystemPlaceholderApps &&
            entry.AppId.StartsWith(IgnoredAppIdPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!ShowImeAppsWithoutIntent &&
            (string.IsNullOrWhiteSpace(entry.Intent) ||
             string.Equals(entry.Intent, "Unknown", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        var statusFilter = ImeAppStatusFilter?.Trim();
        if (!string.IsNullOrWhiteSpace(statusFilter) &&
            !string.Equals(statusFilter, "All", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(entry.InstallStatus, statusFilter, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var flowFilter = ImeApplicationFlowFilter?.Trim();
        if (!string.IsNullOrWhiteSpace(flowFilter) &&
            !string.Equals(flowFilter, "All", StringComparison.OrdinalIgnoreCase))
        {
            var hasMatchingFlow = _allImeTimelineEntries.Any(logEntry =>
                string.Equals(logEntry.EntityType, "App", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(logEntry.EntityId, entry.AppId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(logEntry.FlowDisplay, flowFilter, StringComparison.OrdinalIgnoreCase));
            if (!hasMatchingFlow)
            {
                return false;
            }
        }

        var search = ImeAppSearchText?.Trim();
        if (string.IsNullOrWhiteSpace(search))
        {
            return true;
        }

        return entry.AppName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
               entry.AppId.Contains(search, StringComparison.OrdinalIgnoreCase) ||
               entry.Intent.Contains(search, StringComparison.OrdinalIgnoreCase) ||
               entry.InstallContextSummary.Contains(search, StringComparison.OrdinalIgnoreCase) ||
               entry.TargetInstallContext.Contains(search, StringComparison.OrdinalIgnoreCase) ||
               entry.ApplicabilitySummary.Contains(search, StringComparison.OrdinalIgnoreCase) ||
               entry.DependencySummary.Contains(search, StringComparison.OrdinalIgnoreCase) ||
               entry.InstallStatus.Contains(search, StringComparison.OrdinalIgnoreCase) ||
               entry.ResultCode.Contains(search, StringComparison.OrdinalIgnoreCase) ||
               entry.LastMessage.Contains(search, StringComparison.OrdinalIgnoreCase) ||
               entry.IdentityStatuses.Any(identity =>
                   identity.IdentityId.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                   identity.ApplicabilityStatus.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                   identity.DependencyStatus.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                   identity.InstallStatus.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                   identity.Details.Contains(search, StringComparison.OrdinalIgnoreCase));
    }

    private void RefreshImeApplicationFlowOptions()
    {
        var selected = ImeApplicationFlowFilter;
        var options = ImeFlowTypeOptions
            .Where(option => !string.IsNullOrWhiteSpace(option))
            .Select(option => option.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        _isRefreshingImeApplicationFlowOptions = true;
        try
        {
            ImeApplicationFlowOptions.Clear();
            foreach (var option in options)
            {
                ImeApplicationFlowOptions.Add(option);
            }

            var effectiveSelection = ImeApplicationFlowOptions.Any(option => string.Equals(option, selected, StringComparison.OrdinalIgnoreCase))
                ? selected
                : "All";

            if (!string.Equals(ImeApplicationFlowFilter, effectiveSelection, StringComparison.OrdinalIgnoreCase))
            {
                ImeApplicationFlowFilter = effectiveSelection;
            }
        }
        finally
        {
            _isRefreshingImeApplicationFlowOptions = false;
        }
    }

    private static bool IsFailedLike(string? status, string? resultCode)
    {
        if (!string.IsNullOrWhiteSpace(status))
        {
            var normalized = status.Trim();
            if (normalized.Contains("fail", StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains("retry", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        if (!string.IsNullOrWhiteSpace(resultCode))
        {
            var code = resultCode.Trim();
            if (code.StartsWith("0x8", StringComparison.OrdinalIgnoreCase) ||
                code.StartsWith("-214", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private async Task RefreshImeTestModeAsync(string host)
    {
        try
        {
            IsImeTestModeEnabled = await _localIntuneActionService.GetImeTestModeEnabledAsync(host, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Unable to read IME TestMode registry state for host '{Host}'.", host);
        }
    }

    private static IReadOnlyList<ImeApplicationStatusEntry> MergeImeDisplayMetadata(
        IReadOnlyList<ImeApplicationStatusEntry> current,
        IReadOnlyDictionary<string, ImeApplicationStatusEntry> previousByAppId)
    {
        if (current.Count == 0 || previousByAppId.Count == 0)
        {
            return current;
        }

        var merged = new ImeApplicationStatusEntry[current.Count];
        for (var i = 0; i < current.Count; i++)
        {
            var entry = current[i];
            if (!previousByAppId.TryGetValue(entry.AppId, out var previous))
            {
                merged[i] = entry;
                continue;
            }

            var appName = SelectPreferredValue(entry.AppName, previous.AppName, treatAppIdAsUnknown: true, entry.AppId);
            var intent = SelectPreferredValue(entry.Intent, previous.Intent, treatAppIdAsUnknown: false, entry.AppId);
            var targetContext = SelectPreferredValue(entry.TargetInstallContext, previous.TargetInstallContext, treatAppIdAsUnknown: false, entry.AppId);
            merged[i] = entry with
            {
                AppName = appName,
                Intent = intent,
                TargetInstallContext = targetContext
            };
        }

        return merged;
    }

    private static string SelectPreferredValue(string current, string previous, bool treatAppIdAsUnknown, string appId)
    {
        if (IsMeaningfulMetadata(current, treatAppIdAsUnknown, appId))
        {
            return current;
        }

        return IsMeaningfulMetadata(previous, treatAppIdAsUnknown, appId) ? previous : current;
    }

    private static bool IsMeaningfulMetadata(string? value, bool treatAppIdAsUnknown, string appId)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        if (string.Equals(trimmed, "Unknown", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (treatAppIdAsUnknown && string.Equals(trimmed, appId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private async Task ReloadMdmEventsAsync(string logMessage, bool isLoadMore = false)
    {
        var selection = _targetHostService.CaptureSelection();
        using var linkedCancellationTokenSource = CreateHostLinkedCancellation(selection, CancellationToken.None);
        await _localGate.WaitAsync(linkedCancellationTokenSource.Token);
        try
        {
            IsLocalBusy = !isLoadMore;
            IsLoadingMoreMdmEvents = isLoadMore;
            if (!TryGetConnectedHost(out _))
            {
                MdmEventsStatus = DisconnectedStatus;
                SetStatus(MdmEventsStatus);
                return;
            }

            var requestedCount = Math.Clamp(_requestedMdmEventCount <= 0 ? MdmEventLoadCount : _requestedMdmEventCount, 20, 400);
            var previousCount = _allMdmEvents.Count;
            var entries = await _localIntuneDiagnosticsService.GetMdmAdminEventsAsync(CurrentHost, requestedCount, linkedCancellationTokenSource.Token);
            EnsureCurrentSelection(selection);

            _requestedMdmEventCount = requestedCount;
            _allMdmEvents.Clear();
            _allMdmEvents.AddRange(entries);
            CanLoadMoreMdmEvents = entries.Count >= requestedCount && entries.Count > previousCount;
            ApplyMdmEventFilters(updateStatus: true);
            SetStatus(logMessage);
        }
        catch (OperationCanceledException) when (selection.CancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            MdmEventsStatus = $"MDM event loading failed: {ex.Message}";
            SetStatus(MdmEventsStatus, ex);
        }
        finally
        {
            IsLoadingMoreMdmEvents = false;
            IsLocalBusy = false;
            _localGate.Release();
        }
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

    private bool TryGetConnectedHost(out string host)
    {
        host = _targetHostService.CurrentHost?.Trim() ?? string.Empty;
        CurrentHost = host;
        return !string.IsNullOrWhiteSpace(host);
    }

    private void ApplyDisconnectedState()
    {
        CurrentHost = string.Empty;
        DiagnosticsStatus = DisconnectedStatus;
        EnrollmentStatusText = DisconnectedStatus;
        MdmEventsStatus = DisconnectedStatus;
        ImeLogsStatus = DisconnectedStatus;
        ImeAppsStatus = DisconnectedStatus;
        LocalActionStatus = DisconnectedStatus;
        LocalActionWarnings = string.Empty;
        PolicyResultStatus = DisconnectedStatus;
        PolicyResultWarnings = string.Empty;
        CloudStatus = DisconnectedStatus;
        SetStatus(DisconnectedStatus);
    }
}
