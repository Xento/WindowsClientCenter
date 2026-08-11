using System.Collections.ObjectModel;
using System.DirectoryServices;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WindowsClientCenter.Defender.Contracts;
using WindowsClientCenter.Defender.Contracts.Models;
using WindowsClientCenter.Intune.Services.Contracts;
using WindowsClientCenter.Intune.Services.Models;
using WindowsClientCenter.Plugins.DeviceOverview.Models;
using WindowsClientCenter.Plugin.Abstractions.Contracts;
using Microsoft.Extensions.DependencyInjection;
using System.Windows.Threading;

namespace WindowsClientCenter.Plugins.DeviceOverview.ViewModels;

public partial class DeviceOverviewViewModel : ObservableObject, IDisposable
{
    private const string DisconnectedStatus = "Client is not connected. Click Connect first.";
    private const int OverviewSectionIndex = 0;
    private const int DeliveryOptimizationSectionIndex = 1;
    private const int PortAuthenticationSectionIndex = 2;
    private const string RangeLast24Hours = "Last 24 hours";
    private const string RangeLast7Days = "Last 7 days";
    private const string RangeLast30Days = "Last 30 days";
    private const string RangeAllAvailable = "All available";

    private readonly IDeviceQueryService _deviceQueryService;
    private readonly ILocalIntuneDiagnosticsService _localIntuneDiagnosticsService;
    private readonly ILocalIntuneEnrollmentService _localIntuneEnrollmentService;
    private readonly ILocalIntuneActionService _localIntuneActionService;
    private readonly IDefenderDiagnosticsService? _defenderDiagnosticsService;
    private readonly ITargetHostService _targetHostService;
    private readonly IHostStatusLogSink? _hostStatusLogSink;
    private readonly IHostBusyStateSink? _hostBusyStateSink;
    private readonly IPluginContext _pluginContext;
    private readonly DeviceOverviewOptions _options;
    private readonly bool _verboseOperationsEnabled;
    private readonly double _freeDiskSpaceWarningThresholdGb;
    private readonly double _freeDiskSpaceCriticalThresholdGb;
    private readonly double _uptimeWarningThresholdDays;
    private readonly double _uptimeCriticalThresholdDays;
    private readonly double _defenderSignatureWarningThresholdHours;
    private readonly double _defenderSignatureCriticalThresholdHours;
    private readonly double _defenderScanWarningThresholdDays;
    private readonly DispatcherTimer _uptimeRefreshTimer;
    private string _lastForwardedStatusLine = string.Empty;
    private string? _activeBusyOwnerId;
    private Task? _deliveryOptimizationLoadTask;
    private bool _deliveryOptimizationLoadAttempted;
    private int _busyOperationSequence;
    private static readonly Regex DsregFieldRegex = new(@"^\s*([^\r\n:=][^\r\n:=]*?)\s*[:=]\s*([^\r\n]*)\s*$", RegexOptions.Compiled | RegexOptions.Multiline);

    public ObservableCollection<DeliveryOptimizationSourceRow> DeliveryOptimizationSourceRows { get; } = [];
    public ObservableCollection<DeliveryOptimizationTransferRow> DeliveryOptimizationTransfers { get; } = [];
    public ObservableCollection<NameValueItem> DeliveryOptimizationCurrentMetrics { get; } = [];
    public ObservableCollection<NameValueItem> DeliveryOptimizationMonthlyMetrics { get; } = [];
    public ObservableCollection<NameValueItem> DeliveryOptimizationConfigurationRows { get; } = [];
    public ObservableCollection<DeliveryOptimizationPeerStatusRow> DeliveryOptimizationPeerStatuses { get; } = [];
    public ObservableCollection<DeliveryOptimizationActiveJobRow> DeliveryOptimizationActiveJobs { get; } = [];
    public IReadOnlyList<string> DeliveryOptimizationRangeOptions { get; } =
    [
        RangeLast24Hours,
        RangeLast7Days,
        RangeLast30Days,
        RangeAllAvailable
    ];

    [ObservableProperty]
    private string _status = "Loading device...";

    [ObservableProperty]
    private DeviceRecord? _device;

    [ObservableProperty]
    private LocalIntuneSnapshot? _localSnapshot;

    [ObservableProperty]
    private string _clientHealthStatusText = "Unknown";

    [ObservableProperty]
    private string _clientHealthSummaryText = "Health check not available.";

    [ObservableProperty]
    private string _clientHealthColorHex = "#8A8A8A";

    [ObservableProperty]
    private string _defenderHealthStatusText = "Unknown";

    [ObservableProperty]
    private string _defenderHealthSummaryText = "Defender health not loaded.";

    [ObservableProperty]
    private string _defenderHealthColorHex = "#8A8A8A";

    [ObservableProperty]
    private string _defenderHealthDetailText = "Defender health details are not available.";

    [ObservableProperty]
    private string _defenderDefinitionAgeText = "Unknown";

    [ObservableProperty]
    private string _entraJoinStatusText = "Unknown";

    [ObservableProperty]
    private string _entraJoinColorHex = "#8A8A8A";

    [ObservableProperty]
    private string _entraJoinDetailText = "Entra join state is not available.";

    [ObservableProperty]
    private string _adJoinStatusText = "Unknown";

    [ObservableProperty]
    private string _adJoinColorHex = "#8A8A8A";

    [ObservableProperty]
    private string _adJoinDetailText = "AD join state is not available.";

    [ObservableProperty]
    private string _intuneEnrollmentStatusText = "Unknown";

    [ObservableProperty]
    private string _intuneEnrollmentColorHex = "#8A8A8A";

    [ObservableProperty]
    private string _intuneEnrollmentDetailText = "Intune enrollment state is not available.";

    [ObservableProperty]
    private string _enrollmentUrlsStatusText = "Unknown";

    [ObservableProperty]
    private string _enrollmentUrlsColorHex = "#8A8A8A";

    [ObservableProperty]
    private string _enrollmentUrlsDetailText = "Enrollment URL status is not available.";

    [ObservableProperty]
    private int _selectedSectionIndex;

    [ObservableProperty]
    private bool _isDeliveryOptimizationAvailable;

    [ObservableProperty]
    private bool _isDeliveryOptimizationRangeFilterEnabled;

    [ObservableProperty]
    private bool _isCloudDeviceLoading;

    [ObservableProperty]
    private bool _isLocalSystemLoading;

    [ObservableProperty]
    private bool _isPlatformSecurityLoading;

    [ObservableProperty]
    private bool _isSystemRuntimeLoading;

    [ObservableProperty]
    private bool _isNetworkLoading;

    [ObservableProperty]
    private bool _isClientHealthLoading;

    [ObservableProperty]
    private string _selectedDeliveryOptimizationRange = RangeLast7Days;

    [ObservableProperty]
    private string _deliveryOptimizationSummaryText = "Delivery Optimization data has not been loaded for this host yet.";

    [ObservableProperty]
    private string _deliveryOptimizationWindowText = "-";

    [ObservableProperty]
    private string _deliveryOptimizationRangeHint = "Open the Delivery Optimization view or refresh it there to load transfers.";

    [ObservableProperty]
    private string _deliveryOptimizationNotesText = "-";

    [ObservableProperty]
    private string _deliveryOptimizationCurrentMetricsSummaryText = "Current Delivery Optimization perf snapshot has not been loaded yet.";

    [ObservableProperty]
    private string _deliveryOptimizationMonthlyMetricsSummaryText = "Month-to-date Delivery Optimization perf snapshot has not been loaded yet.";

    [ObservableProperty]
    private string _deliveryOptimizationPeerSummaryText = "Live peer snapshot has not been loaded yet.";

    [ObservableProperty]
    private string _deliveryOptimizationConfigurationSummaryText = "Delivery Optimization configuration snapshot has not been loaded yet.";

    [ObservableProperty]
    private string _deliveryOptimizationActiveJobsSummaryText = "Delivery Optimization active jobs have not been loaded yet.";

    [ObservableProperty]
    private string _freeDiskSpaceStatusLevel = "Unknown";

    [ObservableProperty]
    private string _freeDiskSpaceDetailText = "Free disk space status is not available.";

    [ObservableProperty]
    private string _uptimeStatusLevel = "Unknown";

    [ObservableProperty]
    private string _uptimeDisplayText = "Unknown";

    [ObservableProperty]
    private string _uptimeDetailText = "Uptime status is not available.";

    public bool IsCloudDeviceVisible => _options.CloudDevice.Enabled &&
                                        (ShowCloudDeviceName || ShowCloudDevicePlatform || ShowCloudDeviceCompliance ||
                                         ShowCloudDeviceCloudLastSync || ShowCloudDeviceMdmLastSync ||
                                         ShowCloudDeviceImeLastSync || ShowCloudDeviceIntuneStatus);

    public bool ShowCloudDeviceName => _options.CloudDevice.Enabled && _options.CloudDevice.ShowDevice;

    public bool ShowCloudDevicePlatform => _options.CloudDevice.Enabled && _options.CloudDevice.ShowPlatform;

    public bool ShowCloudDeviceCompliance => _options.CloudDevice.Enabled && _options.CloudDevice.ShowCompliance;

    public bool ShowCloudDeviceCloudLastSync => _options.CloudDevice.Enabled && _options.CloudDevice.ShowCloudLastSync;

    public bool ShowCloudDeviceMdmLastSync => _options.CloudDevice.Enabled && _options.CloudDevice.ShowMdmLastSync;

    public bool ShowCloudDeviceImeLastSync => _options.CloudDevice.Enabled && _options.CloudDevice.ShowImeLastSync;

    public bool ShowCloudDeviceIntuneStatus => _options.CloudDevice.Enabled && _options.CloudDevice.ShowIntuneStatus;

    public bool IsLocalSystemVisible => _options.LocalSystem.Enabled &&
                                        (ShowLocalSystemManufacturer || ShowLocalSystemModel || ShowLocalSystemSerialNumber ||
                                         ShowLocalSystemWindowsVersion || ShowLocalSystemWindowsBuild ||
                                         ShowLocalSystemUpdateRing || ShowLocalSystemPatchStatus ||
                                         ShowLocalSystemFreeDiskSpace);

    public bool ShowLocalSystemManufacturer => _options.LocalSystem.Enabled && _options.LocalSystem.ShowManufacturer;

    public bool ShowLocalSystemModel => _options.LocalSystem.Enabled && _options.LocalSystem.ShowModel;

    public bool ShowLocalSystemSerialNumber => _options.LocalSystem.Enabled && _options.LocalSystem.ShowSerialNumber;

    public bool ShowLocalSystemWindowsVersion => _options.LocalSystem.Enabled && _options.LocalSystem.ShowWindowsVersion;

    public bool ShowLocalSystemWindowsBuild => _options.LocalSystem.Enabled && _options.LocalSystem.ShowWindowsBuild;

    public bool ShowLocalSystemUpdateRing => _options.LocalSystem.Enabled && _options.LocalSystem.ShowUpdateRing;

    public bool ShowLocalSystemPatchStatus => _options.LocalSystem.Enabled && _options.LocalSystem.ShowPatchStatus;

    public bool ShowLocalSystemFreeDiskSpace => _options.LocalSystem.Enabled && _options.LocalSystem.ShowFreeDiskSpace;

    public bool IsPlatformSecurityVisible => _options.PlatformSecurity.Enabled &&
                                             (ShowPlatformSecurityBitLocker || ShowPlatformSecurityBitLockerDetail ||
                                              ShowPlatformSecurityTpm || ShowPlatformSecurityTpmVersion ||
                                              ShowPlatformSecurityTpmDetail || ShowPlatformSecuritySecureBoot ||
                                              ShowPlatformSecurityCredentialGuard || ShowPlatformSecurityVbs ||
                                              ShowPlatformSecurityMemoryIntegrity);

    public bool ShowPlatformSecurityBitLocker => _options.PlatformSecurity.Enabled && _options.PlatformSecurity.ShowBitLocker;

    public bool ShowPlatformSecurityBitLockerDetail => _options.PlatformSecurity.Enabled && _options.PlatformSecurity.ShowBitLockerDetail;

    public bool ShowPlatformSecurityTpm => _options.PlatformSecurity.Enabled && _options.PlatformSecurity.ShowTpm;

    public bool ShowPlatformSecurityTpmVersion => _options.PlatformSecurity.Enabled && _options.PlatformSecurity.ShowTpmVersion;

    public bool ShowPlatformSecurityTpmDetail => _options.PlatformSecurity.Enabled && _options.PlatformSecurity.ShowTpmDetail;

    public bool ShowPlatformSecuritySecureBoot => _options.PlatformSecurity.Enabled && _options.PlatformSecurity.ShowSecureBoot;

    public bool ShowPlatformSecurityCredentialGuard => _options.PlatformSecurity.Enabled && _options.PlatformSecurity.ShowCredentialGuard;

    public bool ShowPlatformSecurityVbs => _options.PlatformSecurity.Enabled && _options.PlatformSecurity.ShowVbs;

    public bool ShowPlatformSecurityMemoryIntegrity => _options.PlatformSecurity.Enabled && _options.PlatformSecurity.ShowMemoryIntegrity;

    public bool IsSystemRuntimeVisible => _options.SystemRuntime.Enabled &&
                                          (ShowSystemRuntimeUptime || ShowSystemRuntimeLastReboot ||
                                           ShowSystemRuntimeInstallDate || ShowSystemRuntimePendingReboot ||
                                           ShowSystemRuntimePendingRebootDetail ||
                                           ShowSystemRuntimeWindowsUpdateRestart ||
                                           ShowSystemRuntimeScheduledRestartTime ||
                                           ShowSystemRuntimeSessionLock || ShowSystemRuntimeLockedSince);

    public bool ShowSystemRuntimeUptime => _options.SystemRuntime.Enabled && _options.SystemRuntime.ShowUptime;

    public bool ShowSystemRuntimeLastReboot => _options.SystemRuntime.Enabled && _options.SystemRuntime.ShowLastReboot;

    public bool ShowSystemRuntimeInstallDate => _options.SystemRuntime.Enabled && _options.SystemRuntime.ShowInstallDate;

    public bool ShowSystemRuntimePendingReboot => _options.SystemRuntime.Enabled && _options.SystemRuntime.ShowPendingReboot;

    public bool ShowSystemRuntimePendingRebootDetail => _options.SystemRuntime.Enabled && _options.SystemRuntime.ShowPendingRebootDetail;

    public bool ShowSystemRuntimeWindowsUpdateRestart => _options.SystemRuntime.Enabled && _options.SystemRuntime.ShowWindowsUpdateRestart;

    public bool ShowSystemRuntimeScheduledRestartTime => _options.SystemRuntime.Enabled && _options.SystemRuntime.ShowScheduledRestartTime;

    public bool ShowSystemRuntimeSessionLock => _options.SystemRuntime.Enabled && _options.SystemRuntime.ShowSessionLock;

    public bool ShowSystemRuntimeLockedSince => _options.SystemRuntime.Enabled && _options.SystemRuntime.ShowLockedSince;

    public bool IsNetworkVisible => _options.Network.Enabled &&
                                    (ShowNetworkConnectionType || ShowNetworkActiveAdapter ||
                                     ShowNetworkWifiSsid || ShowNetworkVpn || ShowNetworkVpnProvider ||
                                     ShowNetworkPortAuthenticationSummary);

    public bool ShowNetworkConnectionType => _options.Network.Enabled && _options.Network.ShowConnectionType;

    public bool ShowNetworkActiveAdapter => _options.Network.Enabled && _options.Network.ShowActiveAdapter;

    public bool ShowNetworkWifiSsid => _options.Network.Enabled && _options.Network.ShowWifiSsid;

    public bool ShowNetworkVpn => _options.Network.Enabled && _options.Network.ShowVpn;

    public bool ShowNetworkVpnProvider => _options.Network.Enabled && _options.Network.ShowVpnProvider;

    public bool ShowNetworkPortAuthenticationSummary => _options.Network.Enabled && _options.Network.ShowPortAuthenticationSummary;

    public bool IsClientHealthVisible => _options.ClientHealth.Enabled &&
                                         (ShowClientHealthOverall || ShowClientHealthSummary ||
                                          ShowDefenderHealth || ShowDefenderHealthDetail ||
                                          ShowDefenderDefinitionAge || ShowEntraJoinHealth ||
                                          ShowAdJoinHealth || ShowIntuneEnrollmentHealth);

    public bool ShowClientHealthOverall => _options.ClientHealth.Enabled && _options.ClientHealth.ShowOverallHealth;

    public bool ShowClientHealthSummary => _options.ClientHealth.Enabled && _options.ClientHealth.ShowSummary;

    public bool IsDefenderHealthEnabled => _options.ClientHealth.Enabled && _options.ClientHealth.Checks.Defender.Enabled;

    public bool ShowDefenderHealth => IsDefenderHealthEnabled && _options.ClientHealth.Checks.Defender.ShowStatus;

    public bool ShowDefenderHealthDetail => IsDefenderHealthEnabled && _options.ClientHealth.Checks.Defender.ShowDetail;

    public bool ShowDefenderDefinitionAge => IsDefenderHealthEnabled && _options.ClientHealth.Checks.Defender.ShowDefinitionAge;

    public bool IsEntraJoinHealthEnabled => _options.ClientHealth.Enabled && _options.ClientHealth.Checks.EntraJoin.Enabled;

    public bool ShowEntraJoinHealth => IsEntraJoinHealthEnabled && _options.ClientHealth.Checks.EntraJoin.ShowStatus;

    public bool IsAdJoinHealthEnabled => _options.ClientHealth.Enabled && _options.ClientHealth.Checks.AdJoin.Enabled;

    public bool ShowAdJoinHealth => IsAdJoinHealthEnabled && _options.ClientHealth.Checks.AdJoin.ShowStatus;

    public bool IsIntuneEnrollmentHealthEnabled => _options.ClientHealth.Enabled && _options.ClientHealth.Checks.IntuneEnrollment.Enabled;

    public bool ShowIntuneEnrollmentHealth => IsIntuneEnrollmentHealthEnabled && _options.ClientHealth.Checks.IntuneEnrollment.ShowStatus;

    public bool IsEnrollmentUrlsHealthEnabled => _options.ClientHealth.Enabled && _options.ClientHealth.Checks.EnrollmentUrls.Enabled;

    public bool IsFreeDiskSpaceHealthEnabled => _options.ClientHealth.Enabled && _options.ClientHealth.Checks.FreeDiskSpace.Enabled;

    public bool IsUptimeHealthEnabled => _options.ClientHealth.Enabled && _options.ClientHealth.Checks.Uptime.Enabled;

    public bool IsDeliveryOptimizationVisible => _options.DeliveryOptimization.Enabled &&
                                                 (ShowDeliveryOptimizationSummary ||
                                                  ShowDeliveryOptimizationActiveJobs ||
                                                  ShowDeliveryOptimizationCurrentMetrics ||
                                                  ShowDeliveryOptimizationMonthlyMetrics ||
                                                  ShowDeliveryOptimizationPeerSnapshot ||
                                                  ShowDeliveryOptimizationConfiguration ||
                                                  ShowDeliveryOptimizationSourceDistribution ||
                                                  ShowDeliveryOptimizationTransferTimeline ||
                                                  ShowDeliveryOptimizationNotes);

    public bool ShowDeliveryOptimizationSummary => _options.DeliveryOptimization.Enabled && _options.DeliveryOptimization.ShowSummary;

    public bool ShowDeliveryOptimizationActiveJobs => _options.DeliveryOptimization.Enabled && _options.DeliveryOptimization.ShowActiveJobs;

    public bool ShowDeliveryOptimizationCurrentMetrics => _options.DeliveryOptimization.Enabled && _options.DeliveryOptimization.ShowCurrentMetrics;

    public bool ShowDeliveryOptimizationMonthlyMetrics => _options.DeliveryOptimization.Enabled && _options.DeliveryOptimization.ShowMonthlyMetrics;

    public bool ShowDeliveryOptimizationPeerSnapshot => _options.DeliveryOptimization.Enabled && _options.DeliveryOptimization.ShowPeerSnapshot;

    public bool ShowDeliveryOptimizationConfiguration => _options.DeliveryOptimization.Enabled && _options.DeliveryOptimization.ShowConfiguration;

    public bool ShowDeliveryOptimizationSourceDistribution => _options.DeliveryOptimization.Enabled && _options.DeliveryOptimization.ShowSourceDistribution;

    public bool ShowDeliveryOptimizationTransferTimeline => _options.DeliveryOptimization.Enabled && _options.DeliveryOptimization.ShowTransferTimeline;

    public bool ShowDeliveryOptimizationNotes => _options.DeliveryOptimization.Enabled && _options.DeliveryOptimization.ShowNotes;

    public DeviceOverviewViewModel(IPluginContext pluginContext, string? initialNavigationTarget = null)
    {
        _pluginContext = pluginContext;
        _deviceQueryService = pluginContext.Services.GetRequiredService<IDeviceQueryService>();
        _localIntuneDiagnosticsService = pluginContext.Services.GetRequiredService<ILocalIntuneDiagnosticsService>();
        _localIntuneEnrollmentService = pluginContext.Services.GetRequiredService<ILocalIntuneEnrollmentService>();
        _localIntuneActionService = pluginContext.Services.GetRequiredService<ILocalIntuneActionService>();
        _defenderDiagnosticsService = pluginContext.Services.GetService<IDefenderDiagnosticsService>();
        _targetHostService = pluginContext.Services.GetRequiredService<ITargetHostService>();
        _hostStatusLogSink = pluginContext.Services.GetService<IHostStatusLogSink>();
        _hostBusyStateSink = pluginContext.Services.GetService<IHostBusyStateSink>();
        _options = DeviceOverviewOptions.FromSettings(pluginContext.Settings);
        _verboseOperationsEnabled = ResolveVerboseOperationsEnabled(pluginContext);
        _freeDiskSpaceWarningThresholdGb = _options.LocalSystem.FreeDiskSpaceWarningGb;
        _freeDiskSpaceCriticalThresholdGb = _options.LocalSystem.FreeDiskSpaceCriticalGb;
        _uptimeWarningThresholdDays = _options.SystemRuntime.UptimeWarningDays;
        _uptimeCriticalThresholdDays = _options.SystemRuntime.UptimeCriticalDays;
        _defenderSignatureWarningThresholdHours = _options.ClientHealth.Checks.Defender.SignatureWarningHours;
        _defenderSignatureCriticalThresholdHours = _options.ClientHealth.Checks.Defender.SignatureCriticalHours;
        _defenderScanWarningThresholdDays = _options.ClientHealth.Checks.Defender.ScanWarningDays;
        _uptimeRefreshTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMinutes(1)
        };
        _uptimeRefreshTimer.Tick += OnUptimeRefreshTimerTick;
        ApplyNavigationTarget(initialNavigationTarget);
        _targetHostService.HostChanged += OnHostChanged;
    }

    public void ApplyNavigationTarget(string? navigationTarget)
    {
        var sectionIndex = MapNavigationTargetToSectionIndex(navigationTarget);
        SelectedSectionIndex =
            sectionIndex == DeliveryOptimizationSectionIndex && !IsDeliveryOptimizationVisible ? OverviewSectionIndex :
            sectionIndex == PortAuthenticationSectionIndex && !IsPortAuthenticationVisible ? OverviewSectionIndex :
            sectionIndex;
    }

    [RelayCommand]
    public Task RefreshAsync()
    {
        if (IsDeliveryOptimizationVisible &&
            SelectedSectionIndex == DeliveryOptimizationSectionIndex &&
            LocalSnapshot is not null)
        {
            return EnsureDeliveryOptimizationLoadedAsync(_targetHostService.CaptureSelection(), CancellationToken.None, forceRefresh: true);
        }

        if (IsPortAuthenticationVisible && SelectedSectionIndex == PortAuthenticationSectionIndex)
        {
            return EnsurePortAuthenticationLoadedAsync(_targetHostService.CaptureSelection(), CancellationToken.None, forceRefresh: true);
        }

        return LoadAsync(CancellationToken.None);
    }

    partial void OnSelectedDeliveryOptimizationRangeChanged(string value)
    {
        ApplyDeliveryOptimization(LocalSnapshot);
    }

    partial void OnSelectedSectionIndexChanged(int value)
    {
        if (value == DeliveryOptimizationSectionIndex && IsDeliveryOptimizationVisible)
        {
            _ = EnsureDeliveryOptimizationLoadedAsync(_targetHostService.CaptureSelection(), CancellationToken.None, forceRefresh: false);
            return;
        }

        if (value == PortAuthenticationSectionIndex && IsPortAuthenticationVisible)
        {
            _ = EnsurePortAuthenticationLoadedAsync(_targetHostService.CaptureSelection(), CancellationToken.None, forceRefresh: false);
        }
    }

    partial void OnLocalSnapshotChanged(LocalIntuneSnapshot? value)
    {
        ApplyFreeDiskSpaceStatus(value);
        UpdateUptimeRefreshTimer(value);
    }

    private bool ShouldLoadCloudDevice()
    {
        return ShowCloudDeviceName || ShowCloudDevicePlatform || ShowCloudDeviceCompliance || ShowCloudDeviceCloudLastSync;
    }

    private bool ShouldLoadEnrollmentStatus()
    {
        return ShowCloudDeviceIntuneStatus || IsEnrollmentUrlsHealthEnabled;
    }

    private bool ShouldLoadLocalCore()
    {
        return IsLocalSystemVisible ||
               ShowCloudDeviceMdmLastSync ||
               ShowCloudDeviceImeLastSync ||
               IsEntraJoinHealthEnabled ||
               IsAdJoinHealthEnabled ||
               IsIntuneEnrollmentHealthEnabled ||
               IsFreeDiskSpaceHealthEnabled ||
               (IsDeliveryOptimizationVisible && SelectedSectionIndex == DeliveryOptimizationSectionIndex);
    }

    private bool ShouldLoadPlatformSecurity()
    {
        return IsPlatformSecurityVisible;
    }

    private bool ShouldLoadSystemRuntime()
    {
        return IsSystemRuntimeVisible || IsUptimeHealthEnabled;
    }

    private bool ShouldLoadNetwork()
    {
        return IsNetworkVisible;
    }

    private bool ShouldLoadDefender()
    {
        return IsDefenderHealthEnabled;
    }

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        var selection = _targetHostService.CaptureSelection();
        var host = selection.Host;
        if (string.IsNullOrWhiteSpace(host))
        {
            ClearBusyState();
            Device = null;
            LocalSnapshot = null;
            ResetClientHealth();
            ResetDeliveryOptimizationState();
            ResetPortAuthenticationState();
            ResetOverviewLoadingState();
            Status = DisconnectedStatus;
            return;
        }

        Status = $"Loading device for host '{host}'...";
        LocalSnapshot = null;
        Device = null;
        ResetClientHealth();
        ResetDeliveryOptimizationState();
        ResetPortAuthenticationState();
        BeginOverviewLoadingState();
        var busyOwnerId = BeginBusyState(host);
        var totalTimer = StartVerboseTimer();
        var shouldLoadCloud = ShouldLoadCloudDevice();
        var shouldLoadEnrollment = ShouldLoadEnrollmentStatus();
        var shouldLoadCore = ShouldLoadLocalCore();
        var shouldLoadPlatform = ShouldLoadPlatformSecurity();
        var shouldLoadRuntime = ShouldLoadSystemRuntime();
        var shouldLoadNetwork = ShouldLoadNetwork();
        var shouldLoadDefender = ShouldLoadDefender();
        using var linkedCancellationTokenSource = CreateHostLinkedCancellation(selection, cancellationToken);

        try
        {
            var cloudTask = shouldLoadCloud
                ? MeasureOperationAsync($"Cloud lookup for '{host}'", () => LoadCloudDeviceAsync(host, linkedCancellationTokenSource.Token))
                : Task.FromResult(new CloudLookupResult(null, null, false));
            var localCoreTask = shouldLoadCore
                ? MeasureOperationAsync($"Local system for '{host}'", () => LoadOverviewCoreSnapshotAsync(host, linkedCancellationTokenSource.Token))
                : Task.FromResult(new LocalSnapshotResult(null, null));
            var enrollmentTask = shouldLoadEnrollment
                ? MeasureOperationAsync($"Enrollment status for '{host}'", () => LoadEnrollmentStatusAsync(host, linkedCancellationTokenSource.Token))
                : Task.FromResult(CreateSkippedEnrollmentStatus(host));
            var platformTask = shouldLoadPlatform
                ? MeasureOperationAsync($"Platform security for '{host}'", () => LoadPlatformSecurityAsync(host, linkedCancellationTokenSource.Token))
                : Task.FromResult(new LocalSectionResult<PlatformSecuritySnapshot>(null, null));
            var runtimeTask = shouldLoadRuntime
                ? MeasureOperationAsync($"System runtime for '{host}'", () => LoadSystemRuntimeAsync(host, linkedCancellationTokenSource.Token))
                : Task.FromResult(new LocalSectionResult<SystemRuntimeSnapshot>(null, null));
            var networkTask = shouldLoadNetwork
                ? MeasureOperationAsync($"Network for '{host}'", () => LoadNetworkConnectivityAsync(host, linkedCancellationTokenSource.Token))
                : Task.FromResult(new LocalSectionResult<NetworkConnectivitySnapshot>(null, null));
            var defenderTask = shouldLoadDefender
                ? MeasureOperationAsync($"Defender lookup for '{host}'", () => LoadDefenderSnapshotAsync(host, linkedCancellationTokenSource.Token))
                : Task.FromResult(new DefenderLookupResult(null, null));

            var operations = new List<(string Name, Task Task)>();
            if (shouldLoadCloud) operations.Add(("Cloud lookup", cloudTask));
            if (shouldLoadEnrollment) operations.Add(("Enrollment status", enrollmentTask));
            if (shouldLoadCore) operations.Add(("Local system", localCoreTask));
            if (shouldLoadPlatform) operations.Add(("Platform security", platformTask));
            if (shouldLoadRuntime) operations.Add(("System runtime", runtimeTask));
            if (shouldLoadNetwork) operations.Add(("Network", networkTask));
            if (shouldLoadDefender) operations.Add(("Defender", defenderTask));
            var monitorTask = MonitorLoadProgressAsync(host, busyOwnerId, linkedCancellationTokenSource.Token, operations.ToArray());
            var pendingTasks = new HashSet<Task> { cloudTask, enrollmentTask, localCoreTask, platformTask, runtimeTask, networkTask, defenderTask };
            CloudLookupResult? cloudResult = null;
            EnrollmentStatus? enrollmentStatus = null;
            LocalSnapshotResult? localCoreResult = null;
            LocalSectionResult<PlatformSecuritySnapshot>? platformResult = null;
            LocalSectionResult<SystemRuntimeSnapshot>? runtimeResult = null;
            LocalSectionResult<NetworkConnectivitySnapshot>? networkResult = null;
            DefenderLookupResult? defenderResult = null;
            var enrollmentCompleted = !shouldLoadEnrollment;
            var coreCompleted = !shouldLoadCore;
            var defenderCompleted = !shouldLoadDefender;

            while (pendingTasks.Count > 0)
            {
                var completedTask = await Task.WhenAny(pendingTasks);
                pendingTasks.Remove(completedTask);

                if (completedTask == cloudTask)
                {
                    cloudResult = await cloudTask;
                    EnsureCurrentSelection(selection);
                    if (shouldLoadCloud)
                    {
                        Device = cloudResult.Device;
                    }

                    IsCloudDeviceLoading = IsCloudDeviceVisible &&
                                           ((shouldLoadEnrollment && !enrollmentCompleted) ||
                                            (shouldLoadCloud && cloudResult is null));
                    await Task.Yield();
                    continue;
                }

                if (completedTask == enrollmentTask)
                {
                    enrollmentStatus = await enrollmentTask;
                    EnsureCurrentSelection(selection);
                    enrollmentCompleted = true;
                    if (shouldLoadEnrollment)
                    {
                        ApplyEnrollmentUrlsStatus(enrollmentStatus.EnrollmentUrls);
                    }

                    IsCloudDeviceLoading = IsCloudDeviceVisible &&
                                           shouldLoadCloud &&
                                           cloudResult is null;
                    await Task.Yield();
                    continue;
                }

                if (completedTask == localCoreTask)
                {
                    localCoreResult = await localCoreTask;
                    EnsureCurrentSelection(selection);
                    coreCompleted = true;
                    IsLocalSystemLoading = false;
                    if (shouldLoadCore)
                    {
                        ApplyOverviewCoreSnapshot(host, localCoreResult.Snapshot);
                    }

                    if (IsClientHealthVisible)
                    {
                        ApplyClientHealth(LocalSnapshot, defenderResult?.Snapshot, defenderResult?.Error);
                    }

                    IsClientHealthLoading = IsClientHealthVisible && !(coreCompleted && defenderCompleted);

                    if (IsDeliveryOptimizationVisible &&
                        SelectedSectionIndex == DeliveryOptimizationSectionIndex &&
                        localCoreResult.Snapshot is not null)
                    {
                        _ = EnsureDeliveryOptimizationLoadedAsync(selection, linkedCancellationTokenSource.Token, forceRefresh: false);
                    }

                    await Task.Yield();
                    continue;
                }

                if (completedTask == platformTask)
                {
                    platformResult = await platformTask;
                    EnsureCurrentSelection(selection);
                    IsPlatformSecurityLoading = false;
                    if (shouldLoadPlatform)
                    {
                        ApplyPlatformSecuritySnapshot(host, platformResult.Snapshot);
                    }
                    await Task.Yield();
                    continue;
                }

                if (completedTask == runtimeTask)
                {
                    runtimeResult = await runtimeTask;
                    EnsureCurrentSelection(selection);
                    IsSystemRuntimeLoading = false;
                    if (shouldLoadRuntime)
                    {
                        ApplySystemRuntimeSnapshot(host, runtimeResult.Snapshot);
                    }
                    await Task.Yield();
                    continue;
                }

                if (completedTask == networkTask)
                {
                    networkResult = await networkTask;
                    EnsureCurrentSelection(selection);
                    IsNetworkLoading = false;
                    if (shouldLoadNetwork)
                    {
                        ApplyNetworkConnectivitySnapshot(host, networkResult.Snapshot);
                    }
                    await Task.Yield();
                    continue;
                }

                defenderResult = await defenderTask;
                EnsureCurrentSelection(selection);
                defenderCompleted = true;
                if (IsClientHealthVisible)
                {
                    ApplyClientHealth(LocalSnapshot, defenderResult.Snapshot, defenderResult.Error);
                }

                IsClientHealthLoading = IsClientHealthVisible && !(coreCompleted && defenderCompleted);
                await Task.Yield();
            }

            await monitorTask;
            EnsureCurrentSelection(selection);

            cloudResult ??= await cloudTask;
            enrollmentStatus ??= await enrollmentTask;
            localCoreResult ??= await localCoreTask;
            platformResult ??= await platformTask;
            runtimeResult ??= await runtimeTask;
            networkResult ??= await networkTask;
            defenderResult ??= await defenderTask;
            EnsureCurrentSelection(selection);

            if (shouldLoadCloud && cloudResult.CloudLookupDisabled)
            {
                ForwardStatusToHost("Cloud device lookup is disabled.");
            }
            else if (shouldLoadCloud && !string.IsNullOrWhiteSpace(cloudResult.Error))
            {
                ForwardStatusToHost(cloudResult.Error);
            }

            if (shouldLoadDefender && !string.IsNullOrWhiteSpace(defenderResult.Error))
            {
                ForwardStatusToHost(
                    _defenderDiagnosticsService is null
                        ? defenderResult.Error
                        : $"Defender health lookup failed for '{host}': {defenderResult.Error}");
            }

            var cloudLookupError = cloudResult.Error;
            var cloudLookupDisabled = cloudResult.CloudLookupDisabled;
            var defenderLookupError = defenderResult.Error;
            var defenderSnapshot = defenderResult.Snapshot;

            if (shouldLoadCore && localCoreResult.Snapshot is null)
            {
                var localFailure = localCoreResult.Error ?? "Local diagnostics did not return a snapshot.";
                if (TryBuildConnectionFailureStatus(host, localFailure, out var connectionFailure))
                {
                    Status = cloudLookupError is null
                        ? connectionFailure
                        : $"{connectionFailure} {cloudLookupError}";
                }
                else
                {
                    Status = cloudLookupError is null
                        ? $"Local diagnostics failed for '{host}': {localFailure}"
                        : $"{cloudLookupError} Local diagnostics also failed: {localFailure}";
                }

                ForwardStatusToHost(Status);
                return;
            }

            if (IsClientHealthVisible)
            {
                ApplyClientHealth(LocalSnapshot, defenderSnapshot, defenderLookupError);
            }

            if (IsPortAuthenticationVisible && SelectedSectionIndex == PortAuthenticationSectionIndex)
            {
                _ = EnsurePortAuthenticationLoadedAsync(selection, linkedCancellationTokenSource.Token, forceRefresh: false);
            }

            if (shouldLoadEnrollment)
            {
                ApplyEnrollmentUrlsStatus(enrollmentStatus.EnrollmentUrls);
            }

            var localSnapshotFailure = TryGetLocalSnapshotFailure(LocalSnapshot);
            if (shouldLoadCore && !string.IsNullOrWhiteSpace(localSnapshotFailure))
            {
                if (TryBuildConnectionFailureStatus(host, localSnapshotFailure, out var connectionFailure))
                {
                    Status = cloudLookupError is null
                        ? connectionFailure
                        : $"{connectionFailure} {cloudLookupError}";
                }
                else
                {
                    Status = cloudLookupError is null
                        ? $"Local diagnostics failed for '{host}': {localSnapshotFailure}"
                        : $"{cloudLookupError} Local diagnostics failed: {localSnapshotFailure}";
                }

                ForwardStatusToHost(Status);
                foreach (var note in (LocalSnapshot?.Notes ?? []).Where(static note => !string.IsNullOrWhiteSpace(note)).Take(5))
                {
                    ForwardStatusToHost($"Local diagnostics detail: {note}");
                }
                return;
            }

            if (shouldLoadCloud && cloudLookupError is not null)
            {
                Status = $"{cloudLookupError} Local diagnostics loaded.";
                ForwardStatusToHost(Status);
                return;
            }

            if (shouldLoadCloud && cloudLookupDisabled)
            {
                Status = $"Cloud lookup disabled. Local diagnostics loaded for '{host}'.";
                ForwardStatusToHost(Status);
                return;
            }

            if (shouldLoadCloud && Device is null)
            {
                Status = $"No cloud device found for host '{host}', local diagnostics loaded.";
                ForwardStatusToHost(Status);
                return;
            }

            Status = Device is null
                ? $"Loaded device overview for '{host}' in {_pluginContext.EnvironmentName} mode."
                : $"Loaded device '{Device.DeviceName}' in {_pluginContext.EnvironmentName} mode.";
            ForwardStatusToHost(Status);
        }
        catch (OperationCanceledException) when (selection.CancellationToken.IsCancellationRequested)
        {
            // Host changed while a background load was running.
        }
        finally
        {
            ResetOverviewLoadingState();
            LogVerboseDuration($"Device overview load for '{host}'", totalTimer);
            ClearBusyState(busyOwnerId);
        }
    }

    private async Task MonitorLoadProgressAsync(
        string host,
        string? busyOwnerId,
        CancellationToken cancellationToken,
        params (string Name, Task Task)[] operations)
    {
        var progressTick = 0;
        while (operations.Any(static operation => !operation.Task.IsCompleted))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(350), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            progressTick = (progressTick + 1) % 4;
            var suffix = new string('.', progressTick);
            var pending = operations
                .Where(operation => !operation.Task.IsCompleted)
                .Select(operation => operation.Name)
                .ToList();

            Status = pending.Count == 0
                ? $"Finalizing device data for '{host}'{suffix}"
                : $"Loading device for '{host}': {string.Join(", ", pending)}{suffix}";
            UpdateBusyState(host, busyOwnerId, pending);
        }
    }

    private async Task<CloudLookupResult> LoadCloudDeviceAsync(string host, CancellationToken cancellationToken)
    {
        try
        {
            var device = await _deviceQueryService.GetDeviceByHostAsync(host, cancellationToken);
            return new CloudLookupResult(device, null, false);
        }
        catch (Exception ex)
        {
            return new CloudLookupResult(null, $"Cloud device lookup failed for '{host}': {ex.Message}", false);
        }
    }

    private async Task<LocalSnapshotResult> LoadOverviewCoreSnapshotAsync(string host, CancellationToken cancellationToken)
    {
        try
        {
            var snapshot = await _localIntuneDiagnosticsService.GetOverviewCoreSnapshotAsync(host, cancellationToken);
            snapshot = await EnrichAdJoinPathAsync(snapshot, host, cancellationToken);
            return new LocalSnapshotResult(snapshot, null);
        }
        catch (Exception ex)
        {
            return new LocalSnapshotResult(null, ex.Message);
        }
    }

    private async Task<LocalSectionResult<PlatformSecuritySnapshot>> LoadPlatformSecurityAsync(string host, CancellationToken cancellationToken)
    {
        try
        {
            var snapshot = await _localIntuneDiagnosticsService.GetPlatformSecuritySnapshotAsync(host, cancellationToken);
            return new LocalSectionResult<PlatformSecuritySnapshot>(snapshot, null);
        }
        catch (Exception ex)
        {
            ForwardStatusToHost($"Platform security lookup failed for '{host}': {ex.Message}");
            return new LocalSectionResult<PlatformSecuritySnapshot>(null, ex.Message);
        }
    }

    private async Task<LocalSectionResult<SystemRuntimeSnapshot>> LoadSystemRuntimeAsync(string host, CancellationToken cancellationToken)
    {
        try
        {
            var snapshot = await _localIntuneDiagnosticsService.GetSystemRuntimeSnapshotAsync(host, cancellationToken);
            return new LocalSectionResult<SystemRuntimeSnapshot>(snapshot, null);
        }
        catch (Exception ex)
        {
            ForwardStatusToHost($"System runtime lookup failed for '{host}': {ex.Message}");
            return new LocalSectionResult<SystemRuntimeSnapshot>(null, ex.Message);
        }
    }

    private async Task<LocalSectionResult<NetworkConnectivitySnapshot>> LoadNetworkConnectivityAsync(string host, CancellationToken cancellationToken)
    {
        try
        {
            var snapshot = await _localIntuneDiagnosticsService.GetNetworkConnectivitySnapshotAsync(host, cancellationToken);
            return new LocalSectionResult<NetworkConnectivitySnapshot>(snapshot, null);
        }
        catch (Exception ex)
        {
            ForwardStatusToHost($"Network lookup failed for '{host}': {ex.Message}");
            return new LocalSectionResult<NetworkConnectivitySnapshot>(null, ex.Message);
        }
    }

    private async Task<DefenderLookupResult> LoadDefenderSnapshotAsync(string host, CancellationToken cancellationToken)
    {
        if (_defenderDiagnosticsService is null)
        {
            return new DefenderLookupResult(null, "Defender diagnostics service is not registered.");
        }

        try
        {
            if (_verboseOperationsEnabled)
            {
                var result = await _defenderDiagnosticsService.GetSnapshotDiagnosticsAsync(host, cancellationToken);
                foreach (var timing in result.Timings)
                {
                    LogVerboseOperation($"Defender: {timing}");
                }

                return new DefenderLookupResult(result.Snapshot, null);
            }

            var snapshot = await _defenderDiagnosticsService.GetSnapshotAsync(host, cancellationToken);
            return new DefenderLookupResult(snapshot, null);
        }
        catch (Exception ex)
        {
            return new DefenderLookupResult(null, ex.Message);
        }
    }

    private void ResetClientHealth()
    {
        ClientHealthStatusText = "Unknown";
        ClientHealthSummaryText = "Health check not available.";
        ClientHealthColorHex = "#8A8A8A";
        DefenderHealthStatusText = "Unknown";
        DefenderHealthSummaryText = "Defender health not loaded.";
        DefenderHealthColorHex = "#8A8A8A";
        DefenderHealthDetailText = "Defender health details are not available.";
        DefenderDefinitionAgeText = "Unknown";
        EntraJoinStatusText = "Unknown";
        EntraJoinColorHex = "#8A8A8A";
        EntraJoinDetailText = "Entra join state is not available.";
        AdJoinStatusText = "Unknown";
        AdJoinColorHex = "#8A8A8A";
        AdJoinDetailText = "AD join state is not available.";
        IntuneEnrollmentStatusText = "Unknown";
        IntuneEnrollmentColorHex = "#8A8A8A";
        IntuneEnrollmentDetailText = "Intune enrollment state is not available.";
        EnrollmentUrlsStatusText = "Unknown";
        EnrollmentUrlsColorHex = "#8A8A8A";
        EnrollmentUrlsDetailText = "Enrollment URL status is not available.";
        FreeDiskSpaceStatusLevel = "Unknown";
        FreeDiskSpaceDetailText = "Free disk space status is not available.";
        UptimeStatusLevel = "Unknown";
        UptimeDisplayText = "Unknown";
        UptimeDetailText = "Uptime status is not available.";
    }

    private void BeginOverviewLoadingState()
    {
        IsCloudDeviceLoading = IsCloudDeviceVisible && (ShouldLoadCloudDevice() || ShouldLoadEnrollmentStatus());
        IsLocalSystemLoading = IsLocalSystemVisible && ShouldLoadLocalCore();
        IsPlatformSecurityLoading = IsPlatformSecurityVisible && ShouldLoadPlatformSecurity();
        IsSystemRuntimeLoading = IsSystemRuntimeVisible && ShouldLoadSystemRuntime();
        IsNetworkLoading = IsNetworkVisible && ShouldLoadNetwork();
        IsClientHealthLoading = IsClientHealthVisible && (ShouldLoadLocalCore() || ShouldLoadDefender());
    }

    private void ResetOverviewLoadingState()
    {
        IsCloudDeviceLoading = false;
        IsLocalSystemLoading = false;
        IsPlatformSecurityLoading = false;
        IsSystemRuntimeLoading = false;
        IsNetworkLoading = false;
        IsClientHealthLoading = false;
    }

    private void ApplyOverviewCoreSnapshot(string host, LocalIntuneSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            return;
        }

        var existing = LocalSnapshot;
        LocalSnapshot = snapshot with
        {
            PlatformSecurity = existing?.PlatformSecurity,
            SystemRuntime = existing?.SystemRuntime,
            NetworkConnectivity = existing?.NetworkConnectivity,
            DeliveryOptimization = existing?.DeliveryOptimization
        };
    }

    private void ApplyPlatformSecuritySnapshot(string host, PlatformSecuritySnapshot? snapshot)
    {
        if (snapshot is null)
        {
            return;
        }

        var current = LocalSnapshot ?? CreatePlaceholderLocalSnapshot(host);
        LocalSnapshot = current with { PlatformSecurity = snapshot };
    }

    private void ApplySystemRuntimeSnapshot(string host, SystemRuntimeSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            return;
        }

        var current = LocalSnapshot ?? CreatePlaceholderLocalSnapshot(host);
        LocalSnapshot = current with { SystemRuntime = snapshot };
    }

    private void ApplyNetworkConnectivitySnapshot(string host, NetworkConnectivitySnapshot? snapshot)
    {
        if (snapshot is null)
        {
            return;
        }

        var current = LocalSnapshot ?? CreatePlaceholderLocalSnapshot(host);
        LocalSnapshot = current with { NetworkConnectivity = snapshot };
    }

    private async Task<EnrollmentStatus> LoadEnrollmentStatusAsync(string host, CancellationToken cancellationToken)
    {
        return await _localIntuneEnrollmentService.GetEnrollmentStatusAsync(host, cancellationToken);
    }

    private static EnrollmentStatus CreateSkippedEnrollmentStatus(string host)
    {
        return new EnrollmentStatus(
            host,
            false,
            false,
            false,
            false,
            "Unknown",
            "Enrollment status lookup is disabled by configuration.",
            [],
            [],
            [],
            [],
            new EnrollmentUrlsStatus(
                false,
                false,
                false,
                "Enrollment URL check is disabled by configuration.",
                [],
                [],
                string.Empty,
                string.Empty,
                string.Empty,
                false),
            false,
            false);
    }

    private static LocalIntuneSnapshot CreatePlaceholderLocalSnapshot(string host)
    {
        return new LocalIntuneSnapshot(
            host,
            host,
            DateTimeOffset.UtcNow,
            false,
            "Unknown",
            "Unknown",
            string.Empty,
            [],
            [],
            [],
            [],
            [],
            []);
    }

    private void ResetDeliveryOptimizationState()
    {
        _deliveryOptimizationLoadAttempted = false;
        IsDeliveryOptimizationAvailable = false;
        IsDeliveryOptimizationRangeFilterEnabled = false;
        DeliveryOptimizationSummaryText = "Delivery Optimization data has not been loaded for this host yet.";
        DeliveryOptimizationWindowText = "-";
        DeliveryOptimizationRangeHint = "Open the Delivery Optimization view or refresh it there to load transfers.";
        DeliveryOptimizationNotesText = "-";
        DeliveryOptimizationCurrentMetricsSummaryText = "Current Delivery Optimization perf snapshot has not been loaded yet.";
        DeliveryOptimizationMonthlyMetricsSummaryText = "Month-to-date Delivery Optimization perf snapshot has not been loaded yet.";
        DeliveryOptimizationPeerSummaryText = "Live peer snapshot has not been loaded yet.";
        DeliveryOptimizationConfigurationSummaryText = "Delivery Optimization configuration snapshot has not been loaded yet.";
        DeliveryOptimizationActiveJobsSummaryText = "Delivery Optimization active jobs have not been loaded yet.";
        DeliveryOptimizationSourceRows.Clear();
        DeliveryOptimizationTransfers.Clear();
        DeliveryOptimizationCurrentMetrics.Clear();
        DeliveryOptimizationMonthlyMetrics.Clear();
        DeliveryOptimizationConfigurationRows.Clear();
        DeliveryOptimizationPeerStatuses.Clear();
        DeliveryOptimizationActiveJobs.Clear();
    }

    private void ApplyDeliveryOptimization(LocalIntuneSnapshot? snapshot)
    {
        if (snapshot?.DeliveryOptimization is not { } deliveryOptimization)
        {
            if (!_deliveryOptimizationLoadAttempted)
            {
                ResetDeliveryOptimizationState();
                return;
            }

            IsDeliveryOptimizationAvailable = false;
            IsDeliveryOptimizationRangeFilterEnabled = false;
            DeliveryOptimizationSummaryText = "Delivery Optimization data is not available in this snapshot.";
            DeliveryOptimizationWindowText = "-";
            DeliveryOptimizationRangeHint = "No Delivery Optimization payload was returned.";
            DeliveryOptimizationNotesText = "-";
            DeliveryOptimizationCurrentMetricsSummaryText = "Current Delivery Optimization perf snapshot not available.";
            DeliveryOptimizationMonthlyMetricsSummaryText = "Month-to-date Delivery Optimization perf snapshot not available.";
            DeliveryOptimizationPeerSummaryText = "Live peer snapshot not available.";
            DeliveryOptimizationConfigurationSummaryText = "Delivery Optimization configuration snapshot not available.";
            DeliveryOptimizationActiveJobsSummaryText = "Delivery Optimization active jobs not available.";
            DeliveryOptimizationSourceRows.Clear();
            DeliveryOptimizationTransfers.Clear();
            DeliveryOptimizationCurrentMetrics.Clear();
            DeliveryOptimizationMonthlyMetrics.Clear();
            DeliveryOptimizationConfigurationRows.Clear();
            DeliveryOptimizationPeerStatuses.Clear();
            DeliveryOptimizationActiveJobs.Clear();
            return;
        }

        IsDeliveryOptimizationAvailable = deliveryOptimization.IsAvailable;
        IsDeliveryOptimizationRangeFilterEnabled = deliveryOptimization.SupportsTimeRangeFiltering;
        DeliveryOptimizationRangeHint = deliveryOptimization.SupportsTimeRangeFiltering
            ? "Range filter applies to transfer timeline and source totals."
            : "Range filter is disabled because no usable transfer timestamps were returned.";
        DeliveryOptimizationNotesText = deliveryOptimization.Notes.Count == 0
            ? "-"
            : string.Join(" | ", deliveryOptimization.Notes.Where(static note => !string.IsNullOrWhiteSpace(note)).Take(6));
        DeliveryOptimizationCurrentMetricsSummaryText = BuildMetricSummary(
            deliveryOptimization.CurrentMetrics ?? [],
            "Current Delivery Optimization perf snapshot not available.");
        DeliveryOptimizationMonthlyMetricsSummaryText = BuildMonthlyMetricSummary(
            deliveryOptimization.MonthlyMetrics ?? [],
            "Month-to-date Delivery Optimization perf snapshot not available.");
        DeliveryOptimizationPeerSummaryText = BuildPeerSummary(deliveryOptimization.PeerStatuses ?? []);
        DeliveryOptimizationConfigurationSummaryText = BuildConfigurationSummary(deliveryOptimization.Configuration ?? []);
        var activeJobs = deliveryOptimization.ActiveJobs ?? [];
        DeliveryOptimizationActiveJobsSummaryText = activeJobs.Count == 0
            ? "No running Delivery Optimization jobs were returned."
            : $"{activeJobs.Count} running Delivery Optimization job{(activeJobs.Count == 1 ? string.Empty : "s")} returned.";

        var filteredTransfers = FilterTransfersByRange(deliveryOptimization).ToArray();
        DeliveryOptimizationTransfers.Clear();
        foreach (var transfer in filteredTransfers
                     .OrderByDescending(static item => item.TimestampUtc)
                     .Take(300))
        {
            DeliveryOptimizationTransfers.Add(new DeliveryOptimizationTransferRow(
                transfer.TimestampUtc,
                transfer.Source,
                transfer.Bytes,
                transfer.Description));
        }

        var sourceStats = BuildSourceRows(deliveryOptimization, filteredTransfers);
        DeliveryOptimizationSourceRows.Clear();
        foreach (var row in sourceStats)
        {
            DeliveryOptimizationSourceRows.Add(row);
        }

        DeliveryOptimizationCurrentMetrics.Clear();
        foreach (var row in deliveryOptimization.CurrentMetrics ?? [])
        {
            DeliveryOptimizationCurrentMetrics.Add(CreateDisplayMetricRow(row));
        }

        DeliveryOptimizationMonthlyMetrics.Clear();
        foreach (var row in deliveryOptimization.MonthlyMetrics ?? [])
        {
            DeliveryOptimizationMonthlyMetrics.Add(CreateDisplayMetricRow(row));
        }

        DeliveryOptimizationConfigurationRows.Clear();
        foreach (var row in deliveryOptimization.Configuration ?? [])
        {
            DeliveryOptimizationConfigurationRows.Add(CreateDisplayMetricRow(row));
        }

        DeliveryOptimizationPeerStatuses.Clear();
        foreach (var row in deliveryOptimization.PeerStatuses ?? [])
        {
            DeliveryOptimizationPeerStatuses.Add(new DeliveryOptimizationPeerStatusRow(
                row.Content,
                row.Status,
                row.CandidateCount,
                row.ConnectedPeerCount,
                row.BytesFromPeers,
                row.BytesFromHttp,
                row.Details));
        }

        DeliveryOptimizationActiveJobs.Clear();
        foreach (var row in activeJobs)
        {
            DeliveryOptimizationActiveJobs.Add(new DeliveryOptimizationActiveJobRow(
                row.Content,
                row.Status,
                row.FileSizeBytes,
                row.DownloadedBytes,
                row.DownloadRateBytesPerSecond,
                row.Details));
        }

        var totalBytes = sourceStats.Sum(static row => row.Bytes);
        DeliveryOptimizationSummaryText = sourceStats.Count == 0
            ? "No Delivery Optimization transfer data available for the selected range."
            : $"{FormatMegabytes(totalBytes)} from {sourceStats.Count} source(s), {filteredTransfers.Length} transfer entr{(filteredTransfers.Length == 1 ? "y" : "ies")}.";

        var windowStart = filteredTransfers.Length > 0
            ? filteredTransfers.Min(static item => item.TimestampUtc)
            : deliveryOptimization.DataStartUtc;
        var windowEnd = filteredTransfers.Length > 0
            ? filteredTransfers.Max(static item => item.TimestampUtc)
            : deliveryOptimization.DataEndUtc;

        DeliveryOptimizationWindowText = windowStart.HasValue && windowEnd.HasValue
            ? $"{windowStart:yyyy-MM-dd HH:mm} - {windowEnd:yyyy-MM-dd HH:mm} UTC"
            : "-";
    }

    private async Task EnsureDeliveryOptimizationLoadedAsync(HostSelection selection, CancellationToken cancellationToken, bool forceRefresh)
    {
        var host = selection.Host;
        if (string.IsNullOrWhiteSpace(host) || LocalSnapshot is null)
        {
            return;
        }

        if (!forceRefresh && LocalSnapshot.DeliveryOptimization is not null)
        {
            ApplyDeliveryOptimization(LocalSnapshot);
            return;
        }

        if (!forceRefresh && _deliveryOptimizationLoadTask is { IsCompleted: false })
        {
            await _deliveryOptimizationLoadTask;
            return;
        }

        using var linkedCancellationTokenSource = CreateHostLinkedCancellation(selection, cancellationToken);
        var loadTask = LoadDeliveryOptimizationAsync(host, linkedCancellationTokenSource.Token);
        _deliveryOptimizationLoadTask = loadTask;
        try
        {
            await loadTask;
            EnsureCurrentSelection(selection);
        }
        finally
        {
            if (ReferenceEquals(_deliveryOptimizationLoadTask, loadTask))
            {
                _deliveryOptimizationLoadTask = null;
            }
        }
    }

    private Task LoadDeliveryOptimizationAsync(string host, CancellationToken cancellationToken)
    {
        return MeasureOperationAsync($"Delivery Optimization load for '{host}'", async () =>
        {
            try
            {
                _deliveryOptimizationLoadAttempted = true;
                var snapshot = await _localIntuneDiagnosticsService.GetDeliveryOptimizationSnapshotAsync(host, cancellationToken);
                var currentSnapshot = LocalSnapshot;
                if (currentSnapshot is null || !string.Equals(currentSnapshot.Host, host, StringComparison.OrdinalIgnoreCase))
                {
                    return 0;
                }

                LocalSnapshot = currentSnapshot with { DeliveryOptimization = snapshot };
                ApplyDeliveryOptimization(LocalSnapshot);
                ForwardStatusToHost($"Delivery Optimization data loaded for '{host}'.");
            }
            catch (Exception ex)
            {
                _deliveryOptimizationLoadAttempted = true;
                var currentSnapshot = LocalSnapshot;
                if (currentSnapshot is null || !string.Equals(currentSnapshot.Host, host, StringComparison.OrdinalIgnoreCase))
                {
                    return 0;
                }

                var failedSnapshot = new DeliveryOptimizationSnapshot(
                    false,
                    DateTimeOffset.UtcNow,
                    [],
                    [],
                    [$"Delivery Optimization load failed: {ex.Message}"],
                    false,
                    null,
                    null,
                    [],
                    [],
                    [],
                    []);
                LocalSnapshot = currentSnapshot with { DeliveryOptimization = failedSnapshot };
                ApplyDeliveryOptimization(LocalSnapshot);
                ForwardStatusToHost($"Delivery Optimization load failed for '{host}': {ex.Message}");
            }

            return 0;
        });
    }

    private static string BuildMetricSummary(IReadOnlyList<NameValueItem> metrics, string emptyText)
    {
        if (metrics.Count == 0)
        {
            return emptyText;
        }

        var parts = new List<string>();
        AddMetricSummaryPart(parts, metrics, "(?i)^dodownloadmode$|^downloadmode$", "Mode", static value => TranslateDownloadMode(value));
        AddMetricSummaryPart(parts, metrics, "(?i)numberofpeers|peercount|numpeers", "Peers");
        AddMetricSummaryPart(parts, metrics, "(?i)files.*download|download.*files", "Files");
        AddMetricSummaryPart(parts, metrics, "(?i)(http|cdn).*bytes|bytes.*(http|cdn)", "HTTP", TryFormatMegabyteValue);
        AddMetricSummaryPart(parts, metrics, "(?i)(peer|p2p).*bytes|bytes.*(peer|p2p)", "Peer", TryFormatMegabyteValue);
        AddMetricSummaryPart(parts, metrics, "(?i)(cache|mcc).*bytes|bytes.*(cache|mcc)", "Cache", TryFormatMegabyteValue);

        return parts.Count > 0
            ? string.Join(" | ", parts.Distinct(StringComparer.OrdinalIgnoreCase))
            : $"{metrics.Count} metric(s) captured.";
    }

    private static string BuildMonthlyMetricSummary(IReadOnlyList<NameValueItem> metrics, string emptyText)
    {
        if (metrics.Count == 0)
        {
            return emptyText;
        }

        var categories = new List<(string Label, long Bytes)>();
        AddMonthlyCategory(categories, metrics, "Internet", "(?i)internetpeerbytes|bytesfrominternetpeers");
        AddMonthlyCategory(categories, metrics, "LAN", "(?i)lanpeerbytes|bytesfromlanpeers");
        AddMonthlyCategory(categories, metrics, "Group", "(?i)grouppeerbytes|bytesfromgrouppeers");
        AddMonthlyCategory(categories, metrics, "HTTP", "(?i)(http|cdn)bytes|bytesfromhttp|bytesdownloadedfromhttp");
        AddMonthlyCategory(categories, metrics, "Cache", "(?i)cache(host)?bytes|bytesfromcacheserver|bytesfromcachehost");

        var totalBytes = categories.Sum(static item => Math.Max(0L, item.Bytes));
        if (totalBytes <= 0)
        {
            return BuildMetricSummary(metrics, emptyText);
        }

        var parts = categories
            .Where(static item => item.Bytes > 0)
            .Select(item =>
            {
                var percent = (double)item.Bytes * 100d / totalBytes;
                return $"{item.Label}: {FormatMegabytes(item.Bytes)} ({percent:N1}%)";
            })
            .ToList();

        parts.Add($"Total: {FormatMegabytes(totalBytes)}");
        return string.Join(" | ", parts);
    }

    private static string BuildPeerSummary(IReadOnlyList<DeliveryOptimizationPeerStatus> peers)
    {
        if (peers.Count == 0)
        {
            return "No live peer snapshot rows returned.";
        }

        var connectedPeers = peers.Sum(static row => Math.Max(0, row.ConnectedPeerCount));
        var candidatePeers = peers.Sum(static row => Math.Max(0, row.CandidateCount));
        var peerBytes = peers.Sum(static row => Math.Max(0L, row.BytesFromPeers));
        return $"{peers.Count} row(s), {connectedPeers} connected peer(s), {candidatePeers} candidate(s), {FormatMegabytes(peerBytes)} from peers.";
    }

    private static string BuildConfigurationSummary(IReadOnlyList<NameValueItem> configuration)
    {
        if (configuration.Count == 0)
        {
            return "Delivery Optimization configuration snapshot not available.";
        }

        var parts = new List<string>();
        AddMetricSummaryPart(parts, configuration, "(?i)^dodownloadmode$|^downloadmode$", "Mode", static value => TranslateDownloadMode(value));
        AddMetricSummaryPart(parts, configuration, "(?i)dogroupid|dogroupid|groupid", "Group");
        AddMetricSummaryPart(parts, configuration, "(?i)mcc|cachehost", "MCC");
        AddMetricSummaryPart(parts, configuration, "(?i)vpn", "VPN");
        AddMetricSummaryPart(parts, configuration, "(?i)mindisksizeallowedtopeer|minramallowedtopeer|minfilesizetocache", "Thresholds");

        return parts.Count > 0
            ? string.Join(" | ", parts.Distinct(StringComparer.OrdinalIgnoreCase))
            : $"{configuration.Count} configuration value(s) captured.";
    }

    private static void AddMetricSummaryPart(
        ICollection<string> parts,
        IReadOnlyList<NameValueItem> values,
        string namePattern,
        string label,
        Func<string, string?>? transform = null)
    {
        var entry = values.FirstOrDefault(item => Regex.IsMatch(item.Name, namePattern, RegexOptions.IgnoreCase));
        if (entry is null || string.IsNullOrWhiteSpace(entry.Value))
        {
            return;
        }

        var formatted = transform is null ? entry.Value.Trim() : transform(entry.Value);
        if (string.IsNullOrWhiteSpace(formatted))
        {
            return;
        }

        parts.Add($"{label}: {formatted}");
    }

    private static void AddMonthlyCategory(
        ICollection<(string Label, long Bytes)> categories,
        IReadOnlyList<NameValueItem> metrics,
        string label,
        string namePattern)
    {
        var bytes = TryGetMetricBytes(metrics, namePattern);
        if (!bytes.HasValue)
        {
            return;
        }

        categories.Add((label, Math.Max(0L, bytes.Value)));
    }

    private static long? TryGetMetricBytes(IReadOnlyList<NameValueItem> metrics, string namePattern)
    {
        var entry = metrics.FirstOrDefault(item => Regex.IsMatch(item.Name, namePattern, RegexOptions.IgnoreCase));
        if (entry is null || string.IsNullOrWhiteSpace(entry.Value))
        {
            return null;
        }

        return TryParseDataSizeToBytes(entry.Value, out var bytes) ? bytes : null;
    }

    private static NameValueItem CreateDisplayMetricRow(NameValueItem item)
    {
        return new NameValueItem(item.Name, FormatDeliveryOptimizationValue(item.Name, item.Value));
    }

    private static string FormatDeliveryOptimizationValue(string name, string rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return rawValue;
        }

        if (ShouldFormatAsMegabytes(name, rawValue))
        {
            return TryFormatMegabyteValue(rawValue) ?? rawValue.Trim();
        }

        if (ShouldFormatAsKilobytesPerSecond(name, rawValue))
        {
            return TryFormatKilobytesPerSecondValue(rawValue) ?? rawValue.Trim();
        }

        return rawValue.Trim();
    }

    private static bool ShouldFormatAsMegabytes(string name, string rawValue)
    {
        if (IsRateValue(rawValue))
        {
            return false;
        }

        return Regex.IsMatch(name, "(?i)bytes|size|cache", RegexOptions.IgnoreCase) ||
               Regex.IsMatch(rawValue, @"(?i)\b(bytes?|kb|mb|gb|tb)\b", RegexOptions.IgnoreCase);
    }

    private static string? TryFormatMegabyteValue(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        if (!TryParseDataSizeToBytes(raw, out var bytes))
        {
            return raw.Trim();
        }

        return FormatMegabytes(bytes);
    }

    private static bool ShouldFormatAsKilobytesPerSecond(string name, string rawValue)
    {
        return Regex.IsMatch(name, "(?i)(bytes?|data|download).*(per.?second|/s)|rate|speed|bandwidth|throughput|bps", RegexOptions.IgnoreCase) ||
               IsRateValue(rawValue);
    }

    private static string? TryFormatKilobytesPerSecondValue(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        if (!TryParseRateToBytesPerSecond(raw, out var bytesPerSecond))
        {
            return raw.Trim();
        }

        return FormatKilobytesPerSecond(bytesPerSecond);
    }

    private static bool TryParseDataSizeToBytes(string raw, out long bytes)
    {
        bytes = 0L;

        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        if (IsRateValue(raw))
        {
            return false;
        }

        var normalized = raw.Replace(",", string.Empty, StringComparison.Ordinal).Trim();
        if (long.TryParse(normalized, NumberStyles.Integer, CultureInfo.InvariantCulture, out bytes))
        {
            return true;
        }

        var match = Regex.Match(raw.Trim(), @"(?i)^(?<value>-?\d+(?:[.,]\d+)?)\s*(?<unit>bytes?|kb|mb|gb|tb)$");
        if (!match.Success)
        {
            return false;
        }

        var valueText = match.Groups["value"].Value.Replace(',', '.');
        if (!double.TryParse(valueText, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            return false;
        }

        var factor = match.Groups["unit"].Value.ToUpperInvariant() switch
        {
            "BYTE" or "BYTES" => 1d,
            "KB" => 1024d,
            "MB" => 1024d * 1024d,
            "GB" => 1024d * 1024d * 1024d,
            "TB" => 1024d * 1024d * 1024d * 1024d,
            _ => 0d
        };

        if (factor <= 0d)
        {
            return false;
        }

        bytes = (long)Math.Round(value * factor, MidpointRounding.AwayFromZero);
        return true;
    }

    private static bool IsRateValue(string raw)
    {
        return !string.IsNullOrWhiteSpace(raw) &&
               Regex.IsMatch(raw, @"(?i)\b(bytes?|kb|mb|gb|tb)\s*/\s*s\b|\b(bytes?|kb|mb|gb|tb)ps\b");
    }

    private static bool TryParseRateToBytesPerSecond(string raw, out long bytesPerSecond)
    {
        bytesPerSecond = 0L;

        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var normalized = raw.Trim()
            .Replace("/sec", "/s", StringComparison.OrdinalIgnoreCase)
            .Replace("/second", "/s", StringComparison.OrdinalIgnoreCase)
            .Replace("ps", "/s", StringComparison.OrdinalIgnoreCase);

        var match = Regex.Match(normalized, @"(?i)^(?<value>-?\d+(?:[.,]\d+)?)\s*(?<unit>bytes?|kb|mb|gb|tb)(?:\s*/\s*s)?$");
        if (!match.Success)
        {
            return false;
        }

        var size = $"{match.Groups["value"].Value} {match.Groups["unit"].Value}";
        return TryParseDataSizeToBytes(size, out bytesPerSecond);
    }

    private static string TranslateDownloadMode(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var trimmed = raw.Trim();
        return trimmed switch
        {
            "0" => "HTTP only (0)",
            "1" => "LAN (1)",
            "2" => "Group (2)",
            "3" => "Internet (3)",
            "99" => "Simple (99)",
            _ => trimmed
        };
    }

    private IReadOnlyList<DeliveryOptimizationSourceRow> BuildSourceRows(
        DeliveryOptimizationSnapshot snapshot,
        IReadOnlyList<DeliveryOptimizationTransferEntry> filteredTransfers)
    {
        var totals = new Dictionary<string, (long Bytes, int TransferCount)>(StringComparer.OrdinalIgnoreCase);

        foreach (var transfer in filteredTransfers)
        {
            if (!totals.TryGetValue(transfer.Source, out var current))
            {
                current = (0L, 0);
            }

            totals[transfer.Source] = (current.Bytes + Math.Max(0L, transfer.Bytes), current.TransferCount + 1);
        }

        if (totals.Count == 0)
        {
            foreach (var stat in snapshot.SourceStats)
            {
                var source = string.IsNullOrWhiteSpace(stat.Source) ? "Unknown" : stat.Source.Trim();
                totals[source] = (Math.Max(0L, stat.Bytes), Math.Max(0, stat.TransferCount));
            }
        }

        var rows = totals
            .Select(static pair => new DeliveryOptimizationSourceRow(
                pair.Key,
                pair.Value.Bytes,
                pair.Value.TransferCount,
                0d))
            .OrderByDescending(static row => row.Bytes)
            .ToArray();

        var totalBytes = rows.Sum(static row => row.Bytes);
        if (totalBytes <= 0)
        {
            return rows;
        }

        return rows
            .Select(row =>
            {
                var share = (double)row.Bytes * 100d / totalBytes;
                return row with { SharePercent = Math.Clamp(share, 0d, 100d) };
            })
            .ToArray();
    }

    private IReadOnlyList<DeliveryOptimizationTransferEntry> FilterTransfersByRange(DeliveryOptimizationSnapshot snapshot)
    {
        if (snapshot.Transfers.Count == 0)
        {
            return [];
        }

        if (!snapshot.SupportsTimeRangeFiltering || string.Equals(SelectedDeliveryOptimizationRange, RangeAllAvailable, StringComparison.Ordinal))
        {
            return snapshot.Transfers;
        }

        var endUtc = snapshot.DataEndUtc
                     ?? snapshot.Transfers.Max(static item => item.TimestampUtc);
        var range = SelectedDeliveryOptimizationRange switch
        {
            RangeLast24Hours => TimeSpan.FromHours(24),
            RangeLast7Days => TimeSpan.FromDays(7),
            RangeLast30Days => TimeSpan.FromDays(30),
            _ => TimeSpan.MaxValue
        };

        if (range == TimeSpan.MaxValue)
        {
            return snapshot.Transfers;
        }

        var startUtc = endUtc - range;
        return snapshot.Transfers
            .Where(item => item.TimestampUtc >= startUtc && item.TimestampUtc <= endUtc)
            .ToArray();
    }

    private static string FormatMegabytes(long bytes)
    {
        if (bytes < 0)
        {
            bytes = 0;
        }

        var value = bytes / (1024d * 1024d);
        return string.Format(CultureInfo.CurrentCulture, "{0:N2} MB", value);
    }

    private static string FormatKilobytesPerSecond(long bytesPerSecond)
    {
        if (bytesPerSecond < 0)
        {
            bytesPerSecond = 0;
        }

        var value = bytesPerSecond / 1024d;
        return string.Format(CultureInfo.CurrentCulture, "{0:N2} KB/s", value);
    }

    private void ApplyClientHealth(LocalIntuneSnapshot? snapshot, DefenderSnapshot? defenderSnapshot, string? defenderLookupError)
    {
        if (snapshot is null)
        {
            ResetClientHealth();
            DefenderHealthStatusText = "Unknown";
            DefenderHealthSummaryText = string.IsNullOrWhiteSpace(defenderLookupError)
                ? "Defender health not available."
                : defenderLookupError;
            DefenderHealthColorHex = "#B07D00";
            DefenderHealthDetailText = DefenderHealthSummaryText;
            DefenderDefinitionAgeText = "Unknown";
            return;
        }

        var dsregFields = ParseDsregFields(snapshot.DsregStatusText ?? string.Empty);
        var entraJoined = IsDsregYes(dsregFields, "AzureAdJoined");
        var domainJoined = IsDsregYes(dsregFields, "DomainJoined");
        var deviceAuthStatus = GetDsregField(dsregFields, "DeviceAuthStatus");
        var hasDeviceAuthError = entraJoined is true && IsSuccessfulDeviceAuthStatus(deviceAuthStatus) is false;
        var mdmUrl = GetDsregField(dsregFields, "MdmUrl");
        var adJoinPathText = snapshot.AdJoinPathText?.Trim() ?? string.Empty;
        var enrollmentArtifacts = snapshot.EnrollmentArtifacts ?? Array.Empty<EnrollmentArtifact>();
        var serviceValues = snapshot.ServiceValues ?? Array.Empty<NameValueItem>();

        var hasEnrollmentArtifacts = enrollmentArtifacts.Count > 0;
        var hasEnrollmentServiceValues = serviceValues.Any(static value =>
            value.Name.Equals("ProviderID", StringComparison.OrdinalIgnoreCase) ||
            value.Name.Equals("MdmServerUrl", StringComparison.OrdinalIgnoreCase) ||
            value.Name.Equals("TenantID", StringComparison.OrdinalIgnoreCase));
        var hasMdmUrl = !string.IsNullOrWhiteSpace(mdmUrl) && !mdmUrl.Equals("-", StringComparison.OrdinalIgnoreCase);
        var intuneEnrolled = hasEnrollmentArtifacts || hasEnrollmentServiceValues || hasMdmUrl;

        EntraJoinStatusText = entraJoined switch
        {
            true when hasDeviceAuthError => "Joined (auth error)",
            true => "Joined",
            false => "Not joined",
            null => "Unknown"
        };
        EntraJoinColorHex = hasDeviceAuthError
            ? "#C62828"
            : entraJoined switch
        {
            true => "#1A7F37",
            false => "#C62828",
            _ => "#B07D00"
        };

        AdJoinStatusText = domainJoined switch
        {
            true => string.IsNullOrWhiteSpace(adJoinPathText) || string.Equals(adJoinPathText, "Unknown", StringComparison.OrdinalIgnoreCase)
                ? "Joined"
                : $"Joined ({adJoinPathText})",
            false => "Not joined",
            null => "Unknown"
        };
        AdJoinColorHex = domainJoined switch
        {
            true => "#1A7F37",
            false => "#B07D00",
            _ => "#B07D00"
        };

        IntuneEnrollmentStatusText = intuneEnrolled ? "Enrolled" : "Not enrolled";
        IntuneEnrollmentColorHex = intuneEnrolled ? "#1A7F37" : "#C62828";
        EntraJoinDetailText = entraJoined switch
        {
            true when hasDeviceAuthError => BuildDeviceAuthStatusDetail(deviceAuthStatus),
            true => "AzureAdJoined was reported as YES.",
            false => "AzureAdJoined was reported as NO.",
            _ => "AzureAdJoined value could not be parsed from dsreg output."
        };
        AdJoinDetailText = domainJoined switch
        {
            true => string.IsNullOrWhiteSpace(adJoinPathText) || string.Equals(adJoinPathText, "Unknown", StringComparison.OrdinalIgnoreCase)
                ? "DomainJoined was reported as YES."
                : $"DomainJoined was reported as YES. Current AD location: {adJoinPathText}",
            false => "DomainJoined was reported as NO.",
            _ => "DomainJoined value could not be parsed from dsreg output."
        };
        IntuneEnrollmentDetailText = intuneEnrolled
            ? BuildEnrollmentEvidenceSummary(hasEnrollmentArtifacts, hasEnrollmentServiceValues, hasMdmUrl)
            : "No Intune enrollment evidence was found (no enrollment artifacts, service values, or MDM URL).";

        if (!IsDefenderHealthEnabled)
        {
            DefenderHealthStatusText = "Unknown";
            DefenderHealthSummaryText = "Defender health check is disabled.";
            DefenderHealthColorHex = "#8A8A8A";
            DefenderHealthDetailText = DefenderHealthSummaryText;
            DefenderDefinitionAgeText = "Unknown";
        }
        else if (defenderSnapshot is null)
        {
            DefenderHealthStatusText = "Unknown";
            DefenderHealthSummaryText = string.IsNullOrWhiteSpace(defenderLookupError)
                ? "Defender health not available."
                : defenderLookupError;
            DefenderHealthColorHex = "#B07D00";
            DefenderHealthDetailText = DefenderHealthSummaryText;
            DefenderDefinitionAgeText = "Unknown";
        }
        else
        {
            var defenderHealth = EvaluateDefenderHealthPresentation(defenderSnapshot);
            DefenderHealthStatusText = defenderHealth.Level;
            DefenderHealthSummaryText = defenderHealth.Summary;
            DefenderHealthColorHex = defenderHealth.ColorHex;
            DefenderHealthDetailText = BuildDefenderHealthDetail(defenderSnapshot);
            DefenderDefinitionAgeText = defenderSnapshot.Versions.SignatureAgeHours < 0
                ? "Unknown"
                : $"{defenderSnapshot.Versions.SignatureAgeHours:N1}h (warning after {_defenderSignatureWarningThresholdHours:N0}h)";
        }

        var issues = new List<string>();
        var warnings = new List<string>();

        if (IsEntraJoinHealthEnabled && entraJoined == false)
        {
            issues.Add("Device is not Entra joined");
        }
        else if (IsEntraJoinHealthEnabled && !entraJoined.HasValue)
        {
            warnings.Add("Entra join state is unknown");
        }

        if (IsIntuneEnrollmentHealthEnabled && !intuneEnrolled)
        {
            issues.Add("Device is not Intune enrolled");
        }

        if (IsEnrollmentUrlsHealthEnabled && EnrollmentUrlsColorHex == "#C62828")
        {
            warnings.Add("Intune enrollment URLs need attention");
        }
        else if (IsEnrollmentUrlsHealthEnabled && EnrollmentUrlsColorHex == "#B07D00")
        {
            warnings.Add("Intune-side configuration could not be fully verified");
        }

        if (IsAdJoinHealthEnabled && domainJoined == false)
        {
            warnings.Add("Device is not AD joined");
        }
        else if (IsAdJoinHealthEnabled && !domainJoined.HasValue)
        {
            warnings.Add("AD join state is unknown");
        }

        if (IsDefenderHealthEnabled && defenderSnapshot is null)
        {
            warnings.Add("Defender health is unavailable");
        }
        else if (IsDefenderHealthEnabled)
        {
            if (string.Equals(DefenderHealthStatusText, "Red", StringComparison.OrdinalIgnoreCase))
            {
                issues.Add($"Defender issue: {DefenderHealthSummaryText}");
            }
            else if (!string.Equals(DefenderHealthStatusText, "Green", StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add($"Defender warning: {DefenderHealthSummaryText}");
            }
        }

        (string Level, string DetailText) freeDiskSpaceEvaluation = IsFreeDiskSpaceHealthEnabled
            ? EvaluateFreeDiskSpace(snapshot?.FreeDiskSpaceText)
            : (Level: "Disabled", DetailText: string.Empty);
        if (IsFreeDiskSpaceHealthEnabled && freeDiskSpaceEvaluation.Level == "Red")
        {
            issues.Add($"Low free disk space: {freeDiskSpaceEvaluation.DetailText}");
        }
        else if (IsFreeDiskSpaceHealthEnabled && freeDiskSpaceEvaluation.Level == "Yellow")
        {
            warnings.Add($"Free disk space warning: {freeDiskSpaceEvaluation.DetailText}");
        }

        (string Level, string DisplayText, string DetailText) uptimeEvaluation = IsUptimeHealthEnabled
            ? EvaluateUptime(snapshot?.SystemRuntime?.UptimeText, snapshot?.SystemRuntime?.LastBootText)
            : (Level: "Disabled", DisplayText: string.Empty, DetailText: string.Empty);
        if (IsUptimeHealthEnabled && uptimeEvaluation.Level == "Red")
        {
            issues.Add($"High uptime: {uptimeEvaluation.DetailText}");
        }
        else if (IsUptimeHealthEnabled && uptimeEvaluation.Level == "Yellow")
        {
            warnings.Add($"Uptime warning: {uptimeEvaluation.DetailText}");
        }

        if (issues.Count > 0)
        {
            ClientHealthStatusText = "Unhealthy";
            ClientHealthSummaryText = string.Join("; ", issues);
            ClientHealthColorHex = "#C62828";
            return;
        }

        if (warnings.Count > 0)
        {
            ClientHealthStatusText = "Warning";
            ClientHealthSummaryText = string.Join("; ", warnings);
            ClientHealthColorHex = "#B07D00";
            return;
        }

        ClientHealthStatusText = "Healthy";
        ClientHealthSummaryText = BuildHealthyClientHealthSummary();
        ClientHealthColorHex = "#1A7F37";
    }

    private void ApplyEnrollmentUrlsStatus(EnrollmentUrlsStatus status)
    {
        if (status.AreExpected)
        {
            EnrollmentUrlsStatusText = "OK";
            EnrollmentUrlsColorHex = "#1A7F37";
        }
        else if (status.TenantInfoDetected)
        {
            EnrollmentUrlsStatusText = "Needs attention";
            EnrollmentUrlsColorHex = "#C62828";
        }
        else
        {
            EnrollmentUrlsStatusText = "Unknown";
            EnrollmentUrlsColorHex = "#B07D00";
        }

        EnrollmentUrlsDetailText = string.IsNullOrWhiteSpace(status.Summary)
            ? "Enrollment URL status is not available."
            : status.Summary;
    }

    private void ApplyFreeDiskSpaceStatus(LocalIntuneSnapshot? snapshot)
    {
        var evaluation = EvaluateFreeDiskSpace(snapshot?.FreeDiskSpaceText);
        FreeDiskSpaceStatusLevel = evaluation.Level;
        FreeDiskSpaceDetailText = evaluation.DetailText;
        RefreshUptimeStatus(snapshot);
    }

    private string BuildHealthyClientHealthSummary()
    {
        var activeChecks = new List<string>();
        if (IsDefenderHealthEnabled) activeChecks.Add("Defender");
        if (IsEntraJoinHealthEnabled) activeChecks.Add("Entra join");
        if (IsAdJoinHealthEnabled) activeChecks.Add("AD join");
        if (IsIntuneEnrollmentHealthEnabled) activeChecks.Add("Intune enrollment");
        if (IsEnrollmentUrlsHealthEnabled) activeChecks.Add("Enrollment URLs");
        if (IsFreeDiskSpaceHealthEnabled) activeChecks.Add("Free disk space");
        if (IsUptimeHealthEnabled) activeChecks.Add("Uptime");

        return activeChecks.Count == 0
            ? "No client health checks are enabled."
            : $"Active checks are healthy ({string.Join(", ", activeChecks)}).";
    }

    private void RefreshUptimeStatus(LocalIntuneSnapshot? snapshot)
    {
        var uptimeEvaluation = EvaluateUptime(snapshot?.SystemRuntime?.UptimeText, snapshot?.SystemRuntime?.LastBootText);
        UptimeStatusLevel = uptimeEvaluation.Level;
        UptimeDisplayText = uptimeEvaluation.DisplayText;
        UptimeDetailText = uptimeEvaluation.DetailText;
    }

    private (string Level, string Summary, string ColorHex) EvaluateDefenderHealthPresentation(DefenderSnapshot snapshot)
    {
        if (snapshot.Protection.AntivirusEnabled == false || snapshot.Protection.RealtimeProtectionEnabled == false)
        {
            return ("Red", "Critical protection components are disabled.", "#C62828");
        }

        if (snapshot.ActiveDetectionCount > 0 ||
            snapshot.Versions.SignatureAgeHours > _defenderSignatureCriticalThresholdHours)
        {
            return ("Red", snapshot.HealthSummary, "#C62828");
        }

        var scanReferenceUtc = snapshot.Scans.QuickScanEndUtc
                               ?? snapshot.Scans.FullScanEndUtc
                               ?? snapshot.Scans.LastScanUtc;
        var scanIsOld = scanReferenceUtc.HasValue && scanReferenceUtc.Value < DateTimeOffset.UtcNow.AddDays(-_defenderScanWarningThresholdDays);

        if (snapshot.Versions.SignatureAgeHours > _defenderSignatureWarningThresholdHours ||
            snapshot.Protection.TamperProtectionEnabled == false ||
            scanIsOld)
        {
            return ("Yellow", snapshot.HealthSummary, "#B07D00");
        }

        return ("Green", "Defender status is healthy and current.", "#1A7F37");
    }

    private (string Level, string DetailText) EvaluateFreeDiskSpace(string? freeDiskSpaceText)
    {
        if (string.IsNullOrWhiteSpace(freeDiskSpaceText) ||
            string.Equals(freeDiskSpaceText, "Unknown", StringComparison.OrdinalIgnoreCase))
        {
            return ("Unknown", "Free disk space is not available.");
        }

        var parsedFreeDiskSpaceGb = TryParseFreeDiskSpaceGb(freeDiskSpaceText);
        if (!parsedFreeDiskSpaceGb.HasValue)
        {
            return ("Unknown", $"Free disk space could not be evaluated: {freeDiskSpaceText.Trim()}");
        }

        if (parsedFreeDiskSpaceGb.Value <= _freeDiskSpaceCriticalThresholdGb)
        {
            return ("Red", $"{freeDiskSpaceText.Trim()} (critical at <= {_freeDiskSpaceCriticalThresholdGb:N1} GB)");
        }

        if (parsedFreeDiskSpaceGb.Value <= _freeDiskSpaceWarningThresholdGb)
        {
            return ("Yellow", $"{freeDiskSpaceText.Trim()} (warning at <= {_freeDiskSpaceWarningThresholdGb:N1} GB)");
        }

        return ("Green", $"{freeDiskSpaceText.Trim()}");
    }

    private static double? TryParseFreeDiskSpaceGb(string? freeDiskSpaceText)
    {
        if (string.IsNullOrWhiteSpace(freeDiskSpaceText))
        {
            return null;
        }

        var match = Regex.Match(
            freeDiskSpaceText,
            @"(?<value>\d+(?:[.,]\d+)?)\s*GB",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            return null;
        }

        var normalizedValue = match.Groups["value"].Value.Replace(',', '.');
        return double.TryParse(normalizedValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private (string Level, string DisplayText, string DetailText) EvaluateUptime(string? uptimeText, string? lastBootText)
    {
        TimeSpan? uptime;
        var lastBootUtc = TryParseLastBootUtc(lastBootText);
        if (lastBootUtc.HasValue)
        {
            uptime = DateTimeOffset.UtcNow - lastBootUtc.Value;
        }
        else
        {
            uptime = TryParseUptime(uptimeText);
        }

        if (!uptime.HasValue)
        {
            return ("Unknown", "Unknown", "Uptime is not available.");
        }

        var displayText = FormatUptimeDisplay(uptime.Value);
        var detailText = string.IsNullOrWhiteSpace(lastBootText) || string.Equals(lastBootText, "Unknown", StringComparison.OrdinalIgnoreCase)
            ? displayText
            : $"{displayText} since {lastBootText.Trim()}";

        if (uptime.Value.TotalDays >= _uptimeCriticalThresholdDays)
        {
            return ("Red", displayText, $"{detailText} (critical at >= {_uptimeCriticalThresholdDays:N0} days)");
        }

        if (uptime.Value.TotalDays >= _uptimeWarningThresholdDays)
        {
            return ("Yellow", displayText, $"{detailText} (warning at >= {_uptimeWarningThresholdDays:N0} days)");
        }

        return ("Green", displayText, detailText);
    }

    private static DateTimeOffset? TryParseLastBootUtc(string? lastBootText)
    {
        if (string.IsNullOrWhiteSpace(lastBootText) ||
            string.Equals(lastBootText, "Unknown", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return DateTimeOffset.TryParse(
            lastBootText,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeLocal | DateTimeStyles.AllowWhiteSpaces,
            out var parsed)
            ? parsed.ToUniversalTime()
            : null;
    }

    private static TimeSpan? TryParseUptime(string? uptimeText)
    {
        if (string.IsNullOrWhiteSpace(uptimeText))
        {
            return null;
        }

        var matches = Regex.Matches(
            uptimeText,
            @"(?<value>\d+)\s*(?<unit>d|h|m|s)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (matches.Count == 0)
        {
            return null;
        }

        double totalSeconds = 0;
        foreach (Match match in matches)
        {
            if (!match.Success ||
                !double.TryParse(match.Groups["value"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            {
                continue;
            }

            totalSeconds += match.Groups["unit"].Value.ToLowerInvariant() switch
            {
                "d" => value * 24 * 60 * 60,
                "h" => value * 60 * 60,
                "m" => value * 60,
                "s" => value,
                _ => 0
            };
        }

        return totalSeconds > 0 ? TimeSpan.FromSeconds(totalSeconds) : TimeSpan.Zero;
    }

    private static string FormatUptimeDisplay(TimeSpan uptime)
    {
        if (uptime.TotalSeconds < 0)
        {
            return "Unknown";
        }

        var parts = new List<string>(capacity: 3);
        if (uptime.Days > 0)
        {
            parts.Add($"{uptime.Days}d");
        }

        if (uptime.Hours > 0 || parts.Count > 0)
        {
            parts.Add($"{uptime.Hours:00}h");
        }

        if (uptime.Minutes > 0 || parts.Count > 0)
        {
            parts.Add($"{uptime.Minutes:00}m");
        }

        if (parts.Count == 0)
        {
            parts.Add($"{Math.Max(0, (int)Math.Floor(uptime.TotalSeconds))}s");
        }

        return string.Join(' ', parts);
    }

    private static string BuildEnrollmentEvidenceSummary(bool hasEnrollmentArtifacts, bool hasEnrollmentServiceValues, bool hasMdmUrl)
    {
        var evidence = new List<string>(capacity: 3);
        if (hasEnrollmentArtifacts)
        {
            evidence.Add("enrollment artifacts");
        }

        if (hasEnrollmentServiceValues)
        {
            evidence.Add("MDM service values");
        }

        if (hasMdmUrl)
        {
            evidence.Add("MdmUrl");
        }

        return evidence.Count == 0
            ? "Enrollment status could not be derived from local diagnostics."
            : $"Enrollment evidence found: {string.Join(", ", evidence)}.";
    }

    private static string BuildDefenderHealthDetail(DefenderSnapshot snapshot)
    {
        if (snapshot is null)
        {
            return "Defender health details are not available.";
        }

        var details = new List<string>(capacity: 4);
        if (!string.IsNullOrWhiteSpace(snapshot.HealthSummary))
        {
            details.Add(snapshot.HealthSummary);
        }

        if (snapshot.Versions is { } versions && versions.SignatureAgeHours >= 0)
        {
            details.Add($"Signature age: {versions.SignatureAgeHours:N1}h");
        }

        if (snapshot.LatestVersionInfo is { ErrorMessage: null } latest &&
            snapshot.Versions is { } currentVersions &&
            !string.IsNullOrWhiteSpace(currentVersions.AntivirusSignatureVersion))
        {
            details.Add(
                $"Definitions: current {currentVersions.AntivirusSignatureVersion}, latest {latest.SecurityIntelligenceVersion}");
        }

        if (snapshot.ActiveDetectionCount > 0)
        {
            details.Add($"Active detections: {snapshot.ActiveDetectionCount}");
        }

        return details.Count == 0
            ? "Defender health details are unavailable."
            : string.Join(". ", details);
    }

    private void OnHostChanged(object? sender, string host)
    {
        _ = LoadAsync(CancellationToken.None);
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

    private void OnUptimeRefreshTimerTick(object? sender, EventArgs e)
    {
        if (LocalSnapshot?.SystemRuntime is null)
        {
            StopUptimeRefreshTimer();
            return;
        }

        RefreshUptimeStatus(LocalSnapshot);
    }

    private void UpdateUptimeRefreshTimer(LocalIntuneSnapshot? snapshot)
    {
        if (snapshot?.SystemRuntime is null)
        {
            StopUptimeRefreshTimer();
            return;
        }

        if (!_uptimeRefreshTimer.IsEnabled)
        {
            _uptimeRefreshTimer.Start();
        }

        RefreshUptimeStatus(snapshot);
    }

    private void StopUptimeRefreshTimer()
    {
        if (_uptimeRefreshTimer.IsEnabled)
        {
            _uptimeRefreshTimer.Stop();
        }
    }

    public void Dispose()
    {
        _targetHostService.HostChanged -= OnHostChanged;
        _uptimeRefreshTimer.Tick -= OnUptimeRefreshTimerTick;
        StopUptimeRefreshTimer();
        ClearBusyState();
    }

    private static string? TryGetLocalSnapshotFailure(LocalIntuneSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            return "No snapshot payload was returned.";
        }

        if (snapshot.RegistrationSummary.StartsWith("Failed", StringComparison.OrdinalIgnoreCase))
        {
            return snapshot.Notes.FirstOrDefault(static note => !string.IsNullOrWhiteSpace(note))
                   ?? snapshot.RegistrationSummary;
        }

        var coreFieldsUnknown =
            string.Equals(snapshot.MdmLastSyncText, "Unknown", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(snapshot.ImeLastSyncText, "Unknown", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(snapshot.WindowsVersionText, "Unknown", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(snapshot.WindowsBuildText, "Unknown", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(snapshot.FreeDiskSpaceText, "Unknown", StringComparison.OrdinalIgnoreCase);

        if (!coreFieldsUnknown)
        {
            return null;
        }

        return snapshot.Notes.FirstOrDefault(static note => !string.IsNullOrWhiteSpace(note));
    }

    private async Task<LocalIntuneSnapshot> EnrichAdJoinPathAsync(LocalIntuneSnapshot snapshot, string host, CancellationToken cancellationToken)
    {
        if (!ShouldResolveAdJoinPath(snapshot))
        {
            return snapshot;
        }

        try
        {
            var adJoinPath = await TryResolveAdJoinPathFromDirectoryAsync(host, cancellationToken);
            if (string.IsNullOrWhiteSpace(adJoinPath))
            {
                return snapshot;
            }

            return snapshot with { AdJoinPathText = adJoinPath };
        }
        catch (Exception ex)
        {
            _hostStatusLogSink?.Append($"[DeviceOverview] AD OU lookup failed for '{host}': {ex.Message}");
            return snapshot;
        }
    }

    private static bool ShouldResolveAdJoinPath(LocalIntuneSnapshot snapshot)
    {
        if (!string.IsNullOrWhiteSpace(snapshot.AdJoinPathText) &&
            !string.Equals(snapshot.AdJoinPathText, "Unknown", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var dsregFields = ParseDsregFields(snapshot.DsregStatusText);
        return IsDsregYes(dsregFields, "DomainJoined") is true;
    }

    private static Task<string?> TryResolveAdJoinPathFromDirectoryAsync(string host, CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            var normalizedHost = NormalizeComputerAccountName(host);
            if (string.IsNullOrWhiteSpace(normalizedHost))
            {
                return (string?)null;
            }

            using var rootDse = new DirectoryEntry("LDAP://RootDSE");
            var defaultNamingContext = rootDse.Properties["defaultNamingContext"]?.Value?.ToString();
            if (string.IsNullOrWhiteSpace(defaultNamingContext))
            {
                return null;
            }

            using var searchRoot = new DirectoryEntry($"LDAP://{defaultNamingContext}");
            using var searcher = new DirectorySearcher(searchRoot)
            {
                Filter = BuildComputerSearchFilter(normalizedHost),
                SearchScope = SearchScope.Subtree,
                PageSize = 1
            };
            searcher.PropertiesToLoad.Add("distinguishedName");

            var searchResult = searcher.FindOne();
            var distinguishedName = searchResult?.Properties["distinguishedname"]?[0]?.ToString();
            return ExtractOuPath(distinguishedName);
        }, cancellationToken);
    }

    private static string NormalizeComputerAccountName(string host)
    {
        var normalized = host.Trim();
        if (normalized.StartsWith(@"\\", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }

        var dotIndex = normalized.IndexOf('.');
        if (dotIndex > 0)
        {
            normalized = normalized[..dotIndex];
        }

        return normalized.Trim();
    }

    private static string BuildComputerSearchFilter(string host)
    {
        var escapedHost = EscapeLdapFilterValue(host);
        var escapedSamAccountName = EscapeLdapFilterValue(host + "$");
        return $"(&(objectCategory=computer)(|(sAMAccountName={escapedSamAccountName})(name={escapedHost})(cn={escapedHost})(dNSHostName={escapedHost})))";
    }

    private static string EscapeLdapFilterValue(string value)
    {
        return value
            .Replace(@"\", @"\5c", StringComparison.Ordinal)
            .Replace("*", @"\2a", StringComparison.Ordinal)
            .Replace("(", @"\28", StringComparison.Ordinal)
            .Replace(")", @"\29", StringComparison.Ordinal)
            .Replace("\0", @"\00", StringComparison.Ordinal);
    }

    private static string? ExtractOuPath(string? distinguishedName)
    {
        if (string.IsNullOrWhiteSpace(distinguishedName))
        {
            return null;
        }

        var commaIndex = distinguishedName.IndexOf(',');
        if (commaIndex <= 0 || commaIndex >= distinguishedName.Length - 1)
        {
            return distinguishedName;
        }

        return distinguishedName[(commaIndex + 1)..];
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
        _hostStatusLogSink.Append($"[Device Overview] {normalized}");
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

        _hostStatusLogSink.Append($"[Device Overview][Verbose] {message.Trim()}");
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

    private string? BeginBusyState(string host)
    {
        if (_hostBusyStateSink is null)
        {
            return null;
        }

        var ownerId = $"device-overview:{GetHashCode():X}:{Interlocked.Increment(ref _busyOperationSequence)}";
        if (!string.IsNullOrWhiteSpace(_activeBusyOwnerId))
        {
            _hostBusyStateSink.ClearBusyState(_activeBusyOwnerId);
        }

        _activeBusyOwnerId = ownerId;
        UpdateBusyState(
            host,
            ownerId,
            ["Cloud lookup", "Local system", "Platform security", "System runtime", "Network", "Defender"]);

        return ownerId;
    }

    private void UpdateBusyState(string host, string? ownerId, IReadOnlyList<string> tasks)
    {
        if (_hostBusyStateSink is null || string.IsNullOrWhiteSpace(ownerId))
        {
            return;
        }

        var normalizedTasks = tasks
            .Where(static task => !string.IsNullOrWhiteSpace(task))
            .Select(static task => task.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var shortStatus = normalizedTasks.Length == 0
            ? $"Finalizing '{host}'"
            : $"Loading '{host}'";

        _hostBusyStateSink.SetBusyState(ownerId, shortStatus, normalizedTasks);
    }

    private void ClearBusyState(string? ownerId = null)
    {
        if (_hostBusyStateSink is null)
        {
            _activeBusyOwnerId = null;
            return;
        }

        var resolvedOwnerId = ownerId ?? _activeBusyOwnerId;
        if (string.IsNullOrWhiteSpace(resolvedOwnerId))
        {
            _activeBusyOwnerId = null;
            return;
        }

        if (string.Equals(_activeBusyOwnerId, resolvedOwnerId, StringComparison.Ordinal))
        {
            _activeBusyOwnerId = null;
        }

        _hostBusyStateSink.ClearBusyState(resolvedOwnerId);
    }

    private static bool TryBuildConnectionFailureStatus(string host, string? message, out string status)
    {
        status = string.Empty;
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        var normalized = message.Trim();
        var indicatesConnectionFailure =
            normalized.Contains("test-wsman", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("winrm", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("cannot connect", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("destination specified in the request", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("rpc server is unavailable", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("name resolution", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("no such host is known", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("timed out", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("verbindung", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("zugriff verweigert", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("rpc-server ist nicht verfügbar", StringComparison.OrdinalIgnoreCase);

        if (!indicatesConnectionFailure)
        {
            return false;
        }

        var reason = normalized.Contains("access is denied", StringComparison.OrdinalIgnoreCase) ||
                     normalized.Contains("zugriff verweigert", StringComparison.OrdinalIgnoreCase)
            ? "Access denied."
            : normalized.Contains("timed out", StringComparison.OrdinalIgnoreCase)
                ? "Timeout while connecting to remote host."
                : normalized.Contains("no such host is known", StringComparison.OrdinalIgnoreCase) ||
                  normalized.Contains("name resolution", StringComparison.OrdinalIgnoreCase)
                    ? "Host name could not be resolved."
                    : "WinRM connection failed.";

        status = $"Connection to '{host}' failed: {reason}";
        return true;
    }

    private static int MapNavigationTargetToSectionIndex(string? navigationTarget)
    {
        if (string.IsNullOrWhiteSpace(navigationTarget))
        {
            return 0;
        }

        return navigationTarget.Trim().ToLowerInvariant() switch
        {
            "overview" => OverviewSectionIndex,
            "delivery-optimization" => DeliveryOptimizationSectionIndex,
            "port-authentication" => PortAuthenticationSectionIndex,
            _ => OverviewSectionIndex
        };
    }

    private static Dictionary<string, string> ParseDsregFields(string? dsregStatusText)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(dsregStatusText))
        {
            return fields;
        }

        foreach (Match match in DsregFieldRegex.Matches(dsregStatusText))
        {
            if (!match.Success)
            {
                continue;
            }

            var key = NormalizeDsregKey(match.Groups[1].Value);
            var value = match.Groups[2].Value.Trim();
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            fields[key] = value;
        }

        return fields;
    }

    private static bool? IsDsregYes(IReadOnlyDictionary<string, string> fields, string fieldName)
    {
        var value = GetDsregField(fields, fieldName);
        return ParseDsregBooleanValue(value);
    }

    private static bool? ParseDsregBooleanValue(string? rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return null;
        }

        var span = rawValue.AsSpan().Trim();
        var tokenBuffer = new char[32];
        var tokenLength = 0;
        foreach (var ch in span)
        {
            if (char.IsLetterOrDigit(ch))
            {
                if (tokenLength < tokenBuffer.Length)
                {
                    tokenBuffer[tokenLength++] = char.ToUpperInvariant(ch);
                }

                continue;
            }

            if (tokenLength > 0)
            {
                break;
            }
        }

        if (tokenLength == 0)
        {
            return null;
        }

        var token = new string(tokenBuffer, 0, tokenLength);
        return token switch
        {
            "YES" or "Y" or "TRUE" or "1" or "JA" or "WAHR" or "AKTIV" => true,
            "NO" or "N" or "FALSE" or "0" or "NEIN" or "FALSCH" or "INAKTIV" => false,
            _ => null
        };
    }

    private static string GetDsregField(IReadOnlyDictionary<string, string> fields, string fieldName)
    {
        return fields.TryGetValue(NormalizeDsregKey(fieldName), out var value)
            ? value
            : string.Empty;
    }

    private static bool IsSuccessfulDeviceAuthStatus(string? deviceAuthStatus)
    {
        return !string.IsNullOrWhiteSpace(deviceAuthStatus) &&
               deviceAuthStatus.Trim().Equals("SUCCESS", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildDeviceAuthStatusDetail(string? deviceAuthStatus)
    {
        if (string.IsNullOrWhiteSpace(deviceAuthStatus))
        {
            return "AzureAdJoined was reported as YES, but DeviceAuthStatus indicates a Microsoft Entra device authentication problem.";
        }

        return $"AzureAdJoined was reported as YES, but DeviceAuthStatus is '{deviceAuthStatus.Trim()}'. Device authentication with Entra ID is failing or incomplete.";
    }

    private static string NormalizeDsregKey(string key)
    {
        return string.Concat(key.Where(char.IsLetterOrDigit)).ToLowerInvariant();
    }

    public sealed record DeliveryOptimizationSourceRow(
        string Source,
        long Bytes,
        int TransferCount,
        double SharePercent)
    {
        public string BytesText => FormatMegabytes(Bytes);
        public string ShareText => string.Format(CultureInfo.CurrentCulture, "{0:N1}%", SharePercent);
    }

    public sealed record DeliveryOptimizationTransferRow(
        DateTimeOffset TimestampUtc,
        string Source,
        long Bytes,
        string Description)
    {
        public string BytesText => FormatMegabytes(Bytes);
    }

    public sealed record DeliveryOptimizationPeerStatusRow(
        string Content,
        string Status,
        int CandidateCount,
        int ConnectedPeerCount,
        long BytesFromPeers,
        long BytesFromHttp,
        string Details)
    {
        public string BytesFromPeersText => FormatMegabytes(BytesFromPeers);
        public string BytesFromHttpText => FormatMegabytes(BytesFromHttp);
    }

    public sealed record DeliveryOptimizationActiveJobRow(
        string Content,
        string Status,
        long FileSizeBytes,
        long DownloadedBytes,
        long DownloadRateBytesPerSecond,
        string Details)
    {
        public string FileSizeText => FormatMegabytes(FileSizeBytes);
        public string DownloadedText => FormatMegabytes(DownloadedBytes);
        public string DownloadRateText => FormatKilobytesPerSecond(DownloadRateBytesPerSecond);
        public string ProgressText => FileSizeBytes <= 0
            ? "-"
            : string.Format(
                CultureInfo.CurrentCulture,
                "{0:N1}%",
                Math.Clamp((double)DownloadedBytes * 100d / FileSizeBytes, 0d, 100d));
    }

    private sealed record CloudLookupResult(DeviceRecord? Device, string? Error, bool CloudLookupDisabled);
    private sealed record LocalSnapshotResult(LocalIntuneSnapshot? Snapshot, string? Error);
    private sealed record LocalSectionResult<T>(T? Snapshot, string? Error) where T : class;
    private sealed record DefenderLookupResult(DefenderSnapshot? Snapshot, string? Error);
}
