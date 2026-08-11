using System.IO;
using System.Linq;
using WindowsClientCenter.Defender.Contracts;
using WindowsClientCenter.Defender.Contracts.Models;
using WindowsClientCenter.Intune.Services.Contracts;
using WindowsClientCenter.Intune.Services.Models;

namespace WindowsClientCenter.Intune.Services.Runtime;

internal sealed class DemoHostConnectivityService(DemoDataCatalog demoDataCatalog) : IHostConnectivityService
{
    public ValueTask<HostConnectivityStatus> TestConnectivityAsync(string host, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(demoDataCatalog.GetConnectivityStatus(host));
    }
}

internal sealed class DemoPowerShellExecutor : IPowerShellExecutor
{
    public ValueTask<PowershellExecutionResult> ExecuteForHostAsync(string host, string scriptBody, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new PowershellExecutionResult(
            ExitCode: 1,
            StdOut: string.Empty,
            StdErr: "PowerShell execution is disabled in demo mode."));
    }
}

internal sealed class DemoAuthService(DemoDataCatalog demoDataCatalog) : IAuthService, IAccessTokenProvider
{
    private AuthSession? _session;

    public ValueTask<AuthSession> LoginAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _session ??= demoDataCatalog.GetAuthSession();
        return ValueTask.FromResult(_session);
    }

    public ValueTask<AuthSession?> GetCurrentSessionAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_session);
    }

    public ValueTask<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _session ??= demoDataCatalog.GetAuthSession();
        return ValueTask.FromResult("demo-access-token");
    }
}

internal sealed class DemoDeviceQueryService(DemoDataCatalog demoDataCatalog) : IDeviceQueryService
{
    public ValueTask<IReadOnlyList<DeviceRecord>> GetDevicesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(demoDataCatalog.GetDevices());
    }

    public ValueTask<DeviceRecord?> GetDeviceByIdAsync(string deviceId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = demoDataCatalog.GetDevices()
            .FirstOrDefault(device => string.Equals(device.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase));
        return ValueTask.FromResult(result);
    }

    public ValueTask<DeviceRecord?> GetDeviceByHostAsync(string host, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<DeviceRecord?>(demoDataCatalog.CreateDeviceRecord(host));
    }
}

internal sealed class DemoDeviceActionService(DemoDataCatalog demoDataCatalog) : IDeviceActionService
{
    public ValueTask<DeviceActionResult> ExecuteActionAsync(DeviceActionRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalizedHost = demoDataCatalog.NormalizeHost(request.DeviceId);
        return ValueTask.FromResult(DeviceActionResult.Ok(
            $"Demo action '{request.Action}' was simulated for '{normalizedHost}'.",
            $"demo-{request.Action}-{normalizedHost.ToLowerInvariant()}"));
    }
}

internal sealed class DemoCloudManagedDeviceService(DemoDataCatalog demoDataCatalog) : ICloudManagedDeviceService
{
    public ValueTask<CloudManagedDeviceSummary?> FindManagedDeviceByHostAsync(string host, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<CloudManagedDeviceSummary?>(demoDataCatalog.CreateCloudManagedDeviceSummary(host));
    }

    public ValueTask<CloudSyncResult> SyncManagedDeviceAsync(string managedDeviceId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(CloudSyncResult.Ok(
            $"Demo cloud sync queued for managed device '{managedDeviceId}'.",
            $"demo-sync-{managedDeviceId}"));
    }
}

internal sealed class DemoLocalIntuneDiagnosticsService(DemoDataCatalog demoDataCatalog) : ILocalIntuneDiagnosticsService
{
    public ValueTask<LocalIntuneSnapshot> GetSnapshotAsync(string host, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(demoDataCatalog.CreateLocalSnapshot(host));
    }

    public ValueTask<LocalIntuneSnapshotDiagnosticsResult> GetSnapshotDiagnosticsAsync(string host, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new LocalIntuneSnapshotDiagnosticsResult(
            demoDataCatalog.CreateLocalSnapshot(host),
            ["Demo snapshot materialized from the in-memory catalog in 4 ms."]));
    }

    public ValueTask<LocalIntuneSnapshot> GetOverviewCoreSnapshotAsync(string host, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(demoDataCatalog.CreateLocalSnapshot(host));
    }

    public ValueTask<PlatformSecuritySnapshot?> GetPlatformSecuritySnapshotAsync(string host, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<PlatformSecuritySnapshot?>(demoDataCatalog.CreatePlatformSecuritySnapshot());
    }

    public ValueTask<SystemRuntimeSnapshot?> GetSystemRuntimeSnapshotAsync(string host, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<SystemRuntimeSnapshot?>(demoDataCatalog.CreateSystemRuntimeSnapshot());
    }

    public ValueTask<NetworkConnectivitySnapshot?> GetNetworkConnectivitySnapshotAsync(string host, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<NetworkConnectivitySnapshot?>(demoDataCatalog.CreateNetworkConnectivitySnapshot(host));
    }

    public ValueTask<PortAuthenticationSnapshot?> GetPortAuthenticationSnapshotAsync(string host, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<PortAuthenticationSnapshot?>(demoDataCatalog.CreatePortAuthenticationSnapshot(host));
    }

    public ValueTask<DeliveryOptimizationSnapshot?> GetDeliveryOptimizationSnapshotAsync(string host, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<DeliveryOptimizationSnapshot?>(demoDataCatalog.CreateDeliveryOptimizationSnapshot());
    }

    public ValueTask<IReadOnlyList<IntuneLogEntry>> GetLogEntriesAsync(string host, string logName, int maxEntries, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(demoDataCatalog.CreateLogEntries(logName, maxEntries));
    }

    public ValueTask<IReadOnlyList<MdmEventAnalysisEntry>> GetMdmAdminEventsAsync(string host, int maxEntries, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(demoDataCatalog.CreateMdmEvents(maxEntries));
    }

    public ValueTask<string> ExportSnapshotAsync(string host, string outputDirectory, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Path.Combine(outputDirectory, "demo-intune-snapshot.json"));
    }

    public ValueTask<string> ExportMdmDiagnosticsAsync(string host, string outputDirectory, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Path.Combine(outputDirectory, "demo-mdm-diagnostics.cab"));
    }
}

internal sealed class DemoLocalIntuneEnrollmentService(DemoDataCatalog demoDataCatalog) : ILocalIntuneEnrollmentService
{
    public ValueTask<EnrollmentStatus> GetEnrollmentStatusAsync(string host, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(demoDataCatalog.CreateEnrollmentStatus(host));
    }

    public ValueTask<DeviceActionResult> TriggerSyncAsync(string host, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(DeviceActionResult.Ok($"Demo sync triggered for '{demoDataCatalog.NormalizeHost(host)}'."));
    }

    public ValueTask<DeviceActionResult> FixEnrollmentUrlsAsync(string host, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(DeviceActionResult.Ok($"Demo enrollment URL repair simulated on '{demoDataCatalog.NormalizeHost(host)}'."));
    }

    public ValueTask<EnrollmentRepairPreview> PreviewReenrollAsync(string host, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(demoDataCatalog.CreateEnrollmentRepairPreview(host));
    }

    public ValueTask<DeviceActionResult> ExecuteReenrollAsync(string host, bool confirmed, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!confirmed)
        {
            return ValueTask.FromResult(DeviceActionResult.Fail("Demo re-enroll requires confirmation.", "confirmation_required"));
        }

        return ValueTask.FromResult(DeviceActionResult.Ok($"Demo re-enrollment completed for '{demoDataCatalog.NormalizeHost(host)}'."));
    }
}

internal sealed class DemoLocalIntuneActionService(DemoDataCatalog demoDataCatalog) : ILocalIntuneActionService
{
    private bool _imeTestModeEnabled;

    public ValueTask<LocalIntuneActionResult> MdmSyncNowAsync(string host, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Success($"Demo MDM sync simulated for '{demoDataCatalog.NormalizeHost(host)}'."));
    }

    public ValueTask<IReadOnlyList<MdmSyncStatusEntry>> GetMdmSyncStatusAsync(string host, int maxEvents, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<MdmSyncStatusEntry> entries =
        [
            new MdmSyncStatusEntry( DateTimeOffset.Parse("2026-04-18T07:55:00Z"), 208, "Demo sync completed successfully.", "0x00000000"),
            new MdmSyncStatusEntry( DateTimeOffset.Parse("2026-04-18T07:38:00Z"), 209, "Demo device schedule refresh completed.", "0x00000000")
        ];
        return ValueTask.FromResult(entries.Take(Math.Max(1, maxEvents)).ToArray() as IReadOnlyList<MdmSyncStatusEntry>);
    }

    public ValueTask<string> GetImeLogTimelineFingerprintAsync(string host, string logDirectory, string filePattern, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult("demo-ime-fingerprint-v1");
    }

    public ValueTask<ImeLogTimelineSnapshot> GetImeLogTimelineSnapshotAsync(string host, string logDirectory, string filePattern, int maxLines, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(demoDataCatalog.CreateImeLogTimelineSnapshot());
    }

    public ValueTask<IReadOnlyList<ImeLogTimelineEntry>> GetImeLogTimelineAsync(string host, string logDirectory, string filePattern, int maxLines, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(demoDataCatalog.CreateImeTimelineEntries());
    }

    public ValueTask<ImeLogAnalysisResult> GetImeLogAnalysisAsync(string host, string logDirectory, string filePattern, int maxLines, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(demoDataCatalog.CreateImeLogAnalysisResult());
    }

    public ValueTask<IReadOnlyList<ImeApplicationStatusEntry>> GetImeApplicationStatusesAsync(string host, string logDirectory, int maxLines, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(demoDataCatalog.CreateImeApplicationStatuses());
    }

    public ValueTask<MdmReportParseResult> GenerateMdmDiagnosticsReportAsync(string host, string outputDirectory, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new MdmReportParseResult(outputDirectory, Path.Combine(outputDirectory, "MDMDiagReport.xml"), Path.Combine(outputDirectory, "MDMDiagReport.html"), 42, 120));
    }

    public ValueTask<MdmReportParseResult> ParseMdmDiagnosticsReportAsync(string host, string reportDirectory, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new MdmReportParseResult(reportDirectory, Path.Combine(reportDirectory, "MDMDiagReport.xml"), Path.Combine(reportDirectory, "MDMDiagReport.html"), 42, 120));
    }

    public ValueTask<IntunePolicyResultReport> GenerateIntunePolicyResultAsync(string host, string outputDirectory, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(demoDataCatalog.CreatePolicyResultReport(host, outputDirectory));
    }

    public ValueTask<IntunePolicyResultReport> ParseIntunePolicyResultAsync(string host, string reportDirectory, string outputDirectory, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(demoDataCatalog.CreatePolicyResultReport(host, outputDirectory));
    }

    public ValueTask<LocalIntuneActionResult> ImeSyncAppsAsync(string host, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Success($"Demo IME app sync simulated for '{demoDataCatalog.NormalizeHost(host)}'."));
    }

    public ValueTask<LocalIntuneActionResult> ImeSyncComplianceAsync(string host, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Success($"Demo IME compliance sync simulated for '{demoDataCatalog.NormalizeHost(host)}'."));
    }

    public ValueTask<LocalIntuneActionResult> ParseImeAppWorkloadPoliciesAsync(string host, string logDirectory, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Success("Demo IME workload policy parsing completed."));
    }

    public ValueTask<LocalIntuneActionResult> RunImeHealthEvaluationAsync(string host, string taskNameContains, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Success("Demo IME health evaluation completed."));
    }

    public ValueTask<LocalIntuneActionResult> RestartImeServiceAsync(string host, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Success("Demo Intune Management Extension restart simulated."));
    }

    public ValueTask<bool> GetImeTestModeEnabledAsync(string host, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_imeTestModeEnabled);
    }

    public ValueTask<LocalIntuneActionResult> SetImeTestModeEnabledAsync(string host, bool enabled, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _imeTestModeEnabled = enabled;
        return ValueTask.FromResult(Success(enabled ? "Demo IME test mode enabled." : "Demo IME test mode disabled."));
    }

    public ValueTask<LocalIntuneActionResult> RetryWin32AppAsync(string host, Win32RetryRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Success($"Demo retry queued for Win32 app '{request.AppId}'."));
    }

    public ValueTask<LocalIntuneActionResult> RetryAllFailedWin32AppsAsync(string host, Win32RetryAllRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Success("Demo retry queued for all failed Win32 apps."));
    }

    public ValueTask<LocalIntuneActionResult> RestartPortAuthenticationServicesAsync(string host, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Success("Demo restart for Wired AutoConfig and EapHost simulated."));
    }

    public ValueTask<LocalIntuneActionResult> RestartPortAuthenticationAdapterAsync(string host, string interfaceName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Success($"Demo adapter restart simulated for '{interfaceName}'."));
    }

    public ValueTask<LocalIntuneActionResult> SetPortAuthenticationTracingAsync(string host, PortAuthenticationTracingMode mode, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Success($"Demo port authentication tracing switched to '{mode}'."));
    }

    public ValueTask<LocalIntuneActionResult> SetPortAuthenticationAutoconfigAsync(string host, string interfaceName, bool enabled, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Success($"Demo autoconfig set to '{enabled}' for '{interfaceName}'."));
    }

    public ValueTask<LocalIntuneActionResult> ReapplyPortAuthenticationProfileAsync(string host, string profileName, string? interfaceName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Success($"Demo reapply for wired profile '{profileName}' simulated."));
    }

    public ValueTask<LocalIntuneActionResult> ExportSupportEventLogsAsync(string host, string outputDirectory, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Success("Demo support event export simulated."));
    }

    public ValueTask<LocalIntuneActionResult> CreateDiagnosticsBundleAsync(string host, string bundleRoot, string zipPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Success("Demo diagnostics bundle creation simulated."));
    }

    public ValueTask<LocalIntuneActionResult> RunAutopilotDiagnosticsCommunityAsync(string host, bool allSessions, bool showPolicies, string moduleVersion, int maxOutputLines, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new LocalIntuneActionResult(
            true,
            "Demo Autopilot diagnostics collected.",
            [],
            new Dictionary<string, string>
            {
                ["moduleVersionRequested"] = moduleVersion,
                ["outputLineCount"] = "3",
                ["outputText"] = "AUTOPILOT DIAGNOSTICS (DEMO)"
            }));
    }

    public ValueTask<LocalIntuneActionResult> RunImeQuickStatusAsync(string host, int maxOutputLines, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new LocalIntuneActionResult(
            true,
            "Demo IME quick status collected.",
            [],
            new Dictionary<string, string>
            {
                ["outputLineCount"] = "3",
                ["outputText"] = "ServiceName: IntuneManagementExtension\nState: Running\nLastSync: 2026-04-18 08:02:00Z"
            }));
    }

    private static LocalIntuneActionResult Success(string message)
    {
        return new LocalIntuneActionResult(true, message, [], new Dictionary<string, string>());
    }
}

internal sealed class DemoLocalDeviceActionService(DemoDataCatalog demoDataCatalog) : ILocalDeviceActionService
{
    private string _activeSchemeId = "381b4222-f694-41f0-9685-ff5bb260df2e";

    public ValueTask<DeviceActionResult> ExecuteLocalActionAsync(string host, string action, IReadOnlyDictionary<string, string>? parameters, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(DeviceActionResult.Ok(
            $"Demo local action '{action}' simulated on '{demoDataCatalog.NormalizeHost(host)}'.",
            $"demo-local-{action}"));
    }

    public ValueTask<PowerStateSnapshot> GetPowerStateAsync(string host, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(demoDataCatalog.CreatePowerStateSnapshot(host, _activeSchemeId));
    }

    public ValueTask<DeviceActionResult> ShutdownAsync(string host, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(DeviceActionResult.Ok($"Demo shutdown simulated for '{demoDataCatalog.NormalizeHost(host)}'."));
    }

    public ValueTask<DeviceActionResult> RestartAsync(string host, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(DeviceActionResult.Ok($"Demo restart simulated for '{demoDataCatalog.NormalizeHost(host)}'."));
    }

    public ValueTask<DeviceActionResult> LogoffAsync(string host, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(DeviceActionResult.Ok($"Demo logoff simulated for '{demoDataCatalog.NormalizeHost(host)}'."));
    }

    public ValueTask<DeviceActionResult> LockWorkstationAsync(string host, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(DeviceActionResult.Ok($"Demo workstation lock simulated for '{demoDataCatalog.NormalizeHost(host)}'."));
    }

    public ValueTask<DeviceActionResult> SetPowerSchemeAsync(string host, string schemeId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _activeSchemeId = schemeId;
        return ValueTask.FromResult(DeviceActionResult.Ok($"Demo power scheme switched to '{schemeId}'."));
    }
}

internal sealed class DemoWindowsServiceManager(DemoDataCatalog demoDataCatalog) : IWindowsServiceManager
{
    private readonly Dictionary<string, DemoServiceEntry> _servicesByName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["CcmExec"] = new("CcmExec", "SMS Agent Host", "Running", WindowsServiceStartMode.Automatic, "MECM client core service.", 1884),
        ["ccmsetup"] = new("ccmsetup", "ConfigMgr Setup Service", "Stopped", WindowsServiceStartMode.Manual, "MECM client setup and repair service.", null),
        ["IntuneManagementExtension"] = new("IntuneManagementExtension", "Microsoft Intune Management Extension", "Running", WindowsServiceStartMode.AutomaticDelayedStart, "Executes Intune Win32 app and script workloads.", 2440),
        ["BITS"] = new("BITS", "Background Intelligent Transfer Service", "Running", WindowsServiceStartMode.AutomaticDelayedStart, "Transfers background downloads for Windows and management workloads.", 1216),
        ["DoSvc"] = new("DoSvc", "Delivery Optimization", "Running", WindowsServiceStartMode.AutomaticDelayedStart, "Optimizes content distribution for Windows and management traffic.", 1404),
        ["wuauserv"] = new("wuauserv", "Windows Update", "Running", WindowsServiceStartMode.Manual, "Enables the detection, download, and installation of updates.", 1600)
    };

    public ValueTask<WindowsServiceSnapshot> GetServicesAsync(string host, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new WindowsServiceSnapshot(
            demoDataCatalog.NormalizeHost(host),
            false,
            _servicesByName.Values
                .Select(static service => service.ToModel())
                .OrderBy(static service => service.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static service => service.ServiceName, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            []));
    }

    public ValueTask<DeviceActionResult> StartServiceAsync(string host, string serviceName, CancellationToken cancellationToken)
        => UpdateServiceAsync(host, serviceName, cancellationToken, service =>
        {
            service.State = "Running";
            service.ProcessId ??= 3000 + Math.Abs(service.ServiceName.GetHashCode(StringComparison.Ordinal)) % 1000;
            return $"Demo service '{service.DisplayName}' started.";
        });

    public ValueTask<DeviceActionResult> StopServiceAsync(string host, string serviceName, CancellationToken cancellationToken)
        => UpdateServiceAsync(host, serviceName, cancellationToken, service =>
        {
            service.State = "Stopped";
            service.ProcessId = null;
            return $"Demo service '{service.DisplayName}' stopped.";
        });

    public ValueTask<DeviceActionResult> RestartServiceAsync(string host, string serviceName, CancellationToken cancellationToken)
        => UpdateServiceAsync(host, serviceName, cancellationToken, service =>
        {
            service.State = "Running";
            service.ProcessId = 4000 + Math.Abs(service.ServiceName.GetHashCode(StringComparison.Ordinal)) % 1000;
            return $"Demo service '{service.DisplayName}' restarted.";
        });

    public ValueTask<DeviceActionResult> KillServiceProcessAsync(string host, string serviceName, CancellationToken cancellationToken)
        => UpdateServiceAsync(host, serviceName, cancellationToken, service =>
        {
            var previousPid = service.ProcessId;
            service.State = "Stopped";
            service.ProcessId = null;
            return previousPid.HasValue
                ? $"Demo service process killed for '{service.DisplayName}'. PreviousPid={previousPid.Value}."
                : $"Demo service '{service.DisplayName}' had no running process to kill.";
        });

    public ValueTask<DeviceActionResult> SetStartModeAsync(string host, string serviceName, WindowsServiceStartMode startMode, CancellationToken cancellationToken)
        => UpdateServiceAsync(host, serviceName, cancellationToken, service =>
        {
            service.StartMode = startMode;
            return $"Demo start mode set for '{service.DisplayName}' to {service.ToModel().StartModeDisplay}.";
        });

    private ValueTask<DeviceActionResult> UpdateServiceAsync(
        string host,
        string serviceName,
        CancellationToken cancellationToken,
        Func<DemoServiceEntry, string> update)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!_servicesByName.TryGetValue(serviceName, out var service))
        {
            return ValueTask.FromResult(DeviceActionResult.Fail(
                $"Service '{serviceName}' was not found on '{demoDataCatalog.NormalizeHost(host)}'.",
                "demo_service_not_found"));
        }

        return ValueTask.FromResult(DeviceActionResult.Ok(update(service)));
    }

    private sealed class DemoServiceEntry(
        string serviceName,
        string displayName,
        string state,
        WindowsServiceStartMode startMode,
        string description,
        int? processId)
    {
        public string ServiceName { get; } = serviceName;
        public string DisplayName { get; } = displayName;
        public string Description { get; } = description;
        public string State { get; set; } = state;
        public WindowsServiceStartMode StartMode { get; set; } = startMode;
        public int? ProcessId { get; set; } = processId;

        public WindowsServiceEntry ToModel() => new(ServiceName, DisplayName, State, StartMode, Description, ProcessId);
    }
}

internal sealed class DemoInstalledSoftwareManager(DemoDataCatalog demoDataCatalog) : IInstalledSoftwareManager
{
    private readonly List<InstalledSoftwareEntry> _entries =
    [
        new(
            "demo|7zip",
            "7-Zip 24.09 (x64)",
            "24.09",
            "Igor Pavlov",
            DateTime.UtcNow.AddDays(-28).ToString("yyyyMMdd"),
            @"C:\Program Files\7-Zip",
            @"C:\Windows\ccmcache\7zip",
            "{23170F69-40C1-2702-2409-000001000000}",
            "{23170F69-40C1-2702-2409-000001000000}",
            "MsiExec.exe /I{23170F69-40C1-2702-2409-000001000000}",
            "MsiExec.exe /X{23170F69-40C1-2702-2409-000001000000} /qn",
            "SMS_InstalledSoftware",
            "x64"),
        new(
            "demo|contoso-vpn",
            "Contoso VPN Client",
            "5.2.1",
            "Contoso",
            DateTime.UtcNow.AddDays(-11).ToString("yyyyMMdd"),
            @"C:\Program Files\Contoso\VPN",
            @"C:\Installers\ContosoVpn",
            string.Empty,
            string.Empty,
            @"""C:\Program Files\Contoso\VPN\uninstall.exe""",
            @"""C:\Program Files\Contoso\VPN\uninstall.exe"" /quiet /norestart",
            "Registry",
            "x64"),
        new(
            "demo|edge",
            "Microsoft Edge",
            "124.0.2478.80",
            "Microsoft Corporation",
            DateTime.UtcNow.AddDays(-3).ToString("yyyyMMdd"),
            @"C:\Program Files (x86)\Microsoft\Edge\Application",
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            "Registry",
            "x86")
    ];

    public ValueTask<InstalledSoftwareSnapshot> GetInstalledSoftwareAsync(string host, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new InstalledSoftwareSnapshot(
            demoDataCatalog.NormalizeHost(host),
            false,
            _entries
                .OrderBy(static entry => entry.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            []));
    }

    public ValueTask<DeviceActionResult> RepairMsiAsync(string host, string softwareCode, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return InstalledSoftwareEntryHelpers.IsMsiProductCode(softwareCode)
            ? ValueTask.FromResult(DeviceActionResult.Ok($"Demo MSI repair completed for '{softwareCode}' on '{demoDataCatalog.NormalizeHost(host)}'."))
            : ValueTask.FromResult(DeviceActionResult.Fail("The selected software does not have a valid MSI product code.", "invalid_msi_product_code"));
    }

    public ValueTask<DeviceActionResult> UninstallMsiAsync(string host, string softwareCode, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return InstalledSoftwareEntryHelpers.IsMsiProductCode(softwareCode)
            ? ValueTask.FromResult(DeviceActionResult.Ok($"Demo MSI uninstall completed for '{softwareCode}' on '{demoDataCatalog.NormalizeHost(host)}'."))
            : ValueTask.FromResult(DeviceActionResult.Fail("The selected software does not have a valid MSI product code.", "invalid_msi_product_code"));
    }

    public ValueTask<DeviceActionResult> UninstallQuietAsync(string host, string quietUninstallString, string softwareIdentity, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return string.IsNullOrWhiteSpace(quietUninstallString)
            ? ValueTask.FromResult(DeviceActionResult.Fail("No quiet uninstall command was provided.", "no_quiet_uninstall"))
            : ValueTask.FromResult(DeviceActionResult.Ok($"Demo quiet uninstall completed for '{softwareIdentity}' on '{demoDataCatalog.NormalizeHost(host)}'."));
    }

    public ValueTask<DeviceActionResult> ForceRemoveRegistryEntryAsync(string host, InstalledSoftwareEntry software, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return software.CanForceRemoveRegistryEntry
            ? ValueTask.FromResult(DeviceActionResult.Ok($"Demo registry entry removal completed for '{software.Name}' on '{demoDataCatalog.NormalizeHost(host)}'."))
            : ValueTask.FromResult(DeviceActionResult.Fail("The selected software does not expose a removable registry identity.", "no_registry_identity"));
    }
}

internal sealed class DemoWindowsProcessManager(DemoDataCatalog demoDataCatalog) : IWindowsProcessManager
{
    private int _sampleIndex;

    public ValueTask<ProcessSnapshot> GetProcessesAsync(string host, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        _sampleIndex++;
        var normalizedHost = demoDataCatalog.NormalizeHost(host);
        var cpuOffset = (_sampleIndex - 1) * 1.5d;

        IReadOnlyList<ProcessSnapshotEntry> processes =
        [
            new("System", 4, null, string.Empty, 12 * 1024 * 1024, 8 * 1024 * 1024, 38 + cpuOffset, DateTimeOffset.UtcNow.AddDays(-12), 220, 5800),
            new("services", 620, 4, "C:\\Windows\\System32\\services.exe", 22 * 1024 * 1024, 12 * 1024 * 1024, 18 + cpuOffset, DateTimeOffset.UtcNow.AddDays(-9), 18, 960),
            new("svchost", 840, 620, "C:\\Windows\\System32\\svchost.exe -k netsvcs -p", 78 * 1024 * 1024, 28 * 1024 * 1024, 62 + cpuOffset * 1.2d, DateTimeOffset.UtcNow.AddDays(-4), 43, 1500),
            new("CcmExec", 1250, 620, "\"C:\\Windows\\CCM\\CcmExec.exe\"", 95 * 1024 * 1024, 44 * 1024 * 1024, 41 + cpuOffset * 1.1d, DateTimeOffset.UtcNow.AddHours(-16), 34, 620),
            new("IntuneManagementExtension", 1544, 620, "\"C:\\Program Files (x86)\\Microsoft Intune Management Extension\\Microsoft.Management.Services.IntuneWindowsAgent.exe\"", 132 * 1024 * 1024, 60 * 1024 * 1024, 27 + cpuOffset * 1.3d, DateTimeOffset.UtcNow.AddHours(-6), 41, 710),
            new("explorer", 3120, 620, "C:\\Windows\\explorer.exe", 164 * 1024 * 1024, 104 * 1024 * 1024, 12 + cpuOffset * 0.8d, DateTimeOffset.UtcNow.AddHours(-8), 77, 1880),
            new("Teams", 4024, 3120, "\"C:\\Users\\demo\\AppData\\Local\\Microsoft\\Teams\\current\\Teams.exe\" --processStart Teams.exe", 284 * 1024 * 1024, 188 * 1024 * 1024, 49 + cpuOffset * 1.9d, DateTimeOffset.UtcNow.AddHours(-2), 93, 2100),
            new("msedge", 4552, 3120, "\"C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe\" --type=renderer", 225 * 1024 * 1024, 154 * 1024 * 1024, 56 + cpuOffset * 2.2d, DateTimeOffset.UtcNow.AddHours(-1), 55, 1400),
            new("notepad", 4896, 99999, "notepad.exe C:\\Temp\\notes.txt", 28 * 1024 * 1024, 12 * 1024 * 1024, 2 + cpuOffset * 0.2d, DateTimeOffset.UtcNow.AddMinutes(-35), 5, 120)
        ];

        return ValueTask.FromResult(new ProcessSnapshot(
            normalizedHost,
            8,
            DateTimeOffset.UtcNow,
            processes,
            []));
    }

    public ValueTask<DeviceActionResult> KillProcessAsync(string host, int processId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(DeviceActionResult.Ok(
            $"Demo process {processId} terminated on '{demoDataCatalog.NormalizeHost(host)}'."));
    }
}

internal sealed class DemoMecmClientService(DemoDataCatalog demoDataCatalog) : IMecmClientService
{
    public ValueTask<MecmOverviewSnapshot> GetOverviewAsync(string host, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var normalizedHost = demoDataCatalog.NormalizeHost(host);
        var now = DateTimeOffset.UtcNow;

        IReadOnlyList<MecmOverviewActivityEntry> activities =
        [
            new("Heartbeat Discovery", "Reported", "Green", now.AddHours(-8), now.AddHours(-8), "Discovery data was reported successfully."),
            new("Hardware Inventory", "Reported", "Green", now.AddHours(-6), now.AddHours(-6), "Hardware inventory was sent successfully."),
            new("Software Inventory", "Reported", "Green", now.AddDays(-1), now.AddDays(-1), "Software inventory is older than hardware inventory."),
            new("Machine Policy Request", "Observed", "Yellow", now.AddMinutes(-40), null, "PolicyAgent requested machine assignments recently."),
            new("Machine Policy Evaluation", "Observed", "Yellow", now.AddMinutes(-32), null, "Machine policy evaluation completed recently."),
            new("CCMEval", "Observed", "Yellow", now.AddHours(-5), null, "The latest CCMEval report is available."),
            new("Last Reboot", "Observed", "Yellow", now.AddDays(-4), null, "Derived from the last OS boot time.")
        ];

        IReadOnlyList<MecmCoManagementWorkloadEntry> workloads =
        [
            new("Compliance Policies", "Intune", "Green", "Compliance workload is piloted to Intune in the demo dataset."),
            new("Windows Update Policies", "ConfigMgr", "Green", "Windows Update workload stays on ConfigMgr in the demo dataset."),
            new("Resource Access Policies", "Unknown", "Unknown", "No local co-management evidence was recorded."),
            new("Endpoint Protection", "ConfigMgr", "Green", "Endpoint Protection workload remains on ConfigMgr."),
            new("Device Configuration", "Intune", "Green", "Device configuration workload is managed by Intune."),
            new("Office Click-to-Run Apps", "Unknown", "Unknown", "No local co-management evidence was recorded."),
            new("Client Apps", "ConfigMgr", "Green", "Client apps workload remains on ConfigMgr.")
        ];

        IReadOnlyList<MecmClientComponentEntry> components =
        [
            new("Software Updates", "UpdatesAgent", "5.00.9128.1005", true, "Green", "Component is enabled by policy."),
            new("Configuration Management", "DCMAgent", "5.00.9128.1005", true, "Green", "Component is enabled by policy."),
            new("Remote Tools", "RemoteTools", "5.00.9128.1005", false, "Yellow", "Remote tools are installed but disabled in policy.")
        ];

        IReadOnlyList<MecmClientServiceEntry> services =
        [
            new("BITS", "Background Intelligent Transfer Service", "Running", "Auto", "Green", "Required for MECM content transfer."),
            new("CcmExec", "SMS Agent Host", "Running", "Auto", "Green", "Core MECM agent service."),
            new("ccmsetup", "ConfigMgr Setup Service", "Stopped", "Manual", "Yellow", "Setup service is idle."),
            new("lppsvc", "Local Profile Assistant Service", "Running", "Manual", "Green", "Available for policy platform interactions."),
            new("Winmgmt", "Windows Management Instrumentation", "Running", "Auto", "Green", "WMI is available."),
            new("wuauserv", "Windows Update", "Running", "Manual", "Green", "Windows Update service is available.")
        ];

        IReadOnlyList<MecmHealthCheckEntry> healthChecks =
        [
            new("WMI", "Healthy", "Green", "SMS_Client is reachable in the demo dataset."),
            new("SMS Agent Host", "Healthy", "Green", "CcmExec is running."),
            new("Policy Platform", "Healthy", "Green", "ActualConfig is reachable in the demo dataset."),
            new("BITS", "Healthy", "Green", "BITS is running."),
            new("Windows Update Service", "Healthy", "Green", "wuauserv is running."),
            new("Client Registration / MP", "Healthy", "Green", "Client ID and management point are available."),
            new("CCMEval Status", "Issues detected", "Yellow", "One simulated CCMEval warning is present.")
        ];

        return ValueTask.FromResult(new MecmOverviewSnapshot(
            normalizedHost,
            "5.00.9128.1005",
            "PRI",
            "mp01.demo.example",
            "No",
            "Active",
            activities,
            workloads,
            components,
            services,
            healthChecks,
            []));
    }

    public ValueTask<DeviceActionResult> ExecuteOverviewActionAsync(string host, MecmOverviewAction action, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(DeviceActionResult.Ok(
            $"Demo MECM overview action '{action}' queued on '{demoDataCatalog.NormalizeHost(host)}'."));
    }

    public ValueTask<MecmApplicationSnapshot> GetApplicationsAsync(string host, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        IReadOnlyList<MecmApplicationEntry> entries =
        [
            new(
                Id: "ScopeId_000/Application_001",
                Name: "7-Zip",
                FullName: "7-Zip x64",
                Description: "Compression utility deployed by MECM.",
                Icon: string.Empty,
                SoftwareVersion: "24.09",
                Revision: "5",
                UserUiExperience: true,
                IsPreflightOnly: false,
                IsMachineTarget: true,
                AllowedActions: ["Install", "Repair", "Uninstall"],
                InstallState: "Installed",
                ApplicabilityState: "Applicable",
                ResolvedState: "Installed",
                EvaluationState: 1,
                EvaluationStateText: "Application is enforced to desired/resolved state.",
                ErrorCode: 0,
                ErrorCodeText: string.Empty,
                LastEvalTimeUtc: DateTimeOffset.UtcNow.AddMinutes(-18),
                LastInstallTimeUtc: DateTimeOffset.UtcNow.AddDays(-7),
                HasInstallCommand: true,
                HasUninstallCommand: true,
                HasIcon: true),
            new(
                Id: "ScopeId_000/Application_002",
                Name: "Contoso VPN",
                FullName: "Contoso VPN Client",
                Description: "VPN client staged for users.",
                Icon: string.Empty,
                SoftwareVersion: "5.2.1",
                Revision: "3",
                UserUiExperience: true,
                IsPreflightOnly: false,
                IsMachineTarget: false,
                AllowedActions: ["Install"],
                InstallState: "NotInstalled",
                ApplicabilityState: "Applicable",
                ResolvedState: "Available",
                EvaluationState: 3,
                EvaluationStateText: "Application is available for enforcement (install or uninstall based on resolved state). Content may/may not have been downloaded.",
                ErrorCode: 0,
                ErrorCodeText: string.Empty,
                LastEvalTimeUtc: DateTimeOffset.UtcNow.AddMinutes(-7),
                LastInstallTimeUtc: null,
                HasInstallCommand: true,
                HasUninstallCommand: false,
                HasIcon: false),
            new(
                Id: "ScopeId_000/Application_003",
                Name: "Legacy ERP Tools",
                FullName: "Legacy ERP Tools",
                Description: "Repairable line-of-business application.",
                Icon: string.Empty,
                SoftwareVersion: "11.4",
                Revision: "9",
                UserUiExperience: false,
                IsPreflightOnly: true,
                IsMachineTarget: true,
                AllowedActions: ["Repair", "Uninstall"],
                InstallState: "Installed",
                ApplicabilityState: "Applicable",
                ResolvedState: "Installed",
                EvaluationState: 13,
                EvaluationStateText: "Application install/uninstall enforced and soft reboot is pending.",
                ErrorCode: 3010,
                ErrorCodeText: "A restart is required to complete the install.",
                LastEvalTimeUtc: DateTimeOffset.UtcNow.AddMinutes(-3),
                LastInstallTimeUtc: DateTimeOffset.UtcNow.AddHours(-2),
                HasInstallCommand: false,
                HasUninstallCommand: true,
                HasIcon: false)
        ];

        return ValueTask.FromResult(new MecmApplicationSnapshot(
            demoDataCatalog.NormalizeHost(host),
            entries,
            []));
    }

    public ValueTask<DeviceActionResult> ExecuteApplicationActionAsync(string host, string applicationId, string revision, bool isMachineTarget, MecmApplicationAction action, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(DeviceActionResult.Ok(
            $"Demo MECM application action '{action}' queued for '{applicationId}' on '{demoDataCatalog.NormalizeHost(host)}'."));
    }

    public ValueTask<DeviceActionResult> TriggerApplicationEvaluationAsync(string host, MecmApplicationEvaluationMode mode, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(DeviceActionResult.Ok(
            $"Demo MECM application evaluation '{mode}' queued on '{demoDataCatalog.NormalizeHost(host)}'."));
    }

    public ValueTask<MecmPendingUpdatesSnapshot> GetPendingUpdatesAsync(string host, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        IReadOnlyList<MecmPendingUpdateEntry> entries =
        [
            new(
                UpdateId: "Site_001/SUM_501",
                Name: "2026-04 Cumulative Update for Windows 11 24H2 for x64-based Systems",
                Publisher: "Microsoft",
                Description: "Security update for Windows 11.",
                ArticleId: "KB5060001",
                BulletinId: "MS26-041",
                EvaluationState: 5,
                EvaluationStateText: "ciJobStateDownloading",
                PercentComplete: 72,
                ErrorCode: 0,
                ErrorCodeText: string.Empty,
                DeadlineUtc: DateTimeOffset.UtcNow.AddHours(4)),
            new(
                UpdateId: "Site_001/SUM_502",
                Name: "Security Intelligence Update for Microsoft Defender Antivirus",
                Publisher: "Microsoft",
                Description: "Security intelligence update.",
                ArticleId: "2267602",
                BulletinId: string.Empty,
                EvaluationState: 1,
                EvaluationStateText: "ciJobStateAvailable",
                PercentComplete: 0,
                ErrorCode: 0,
                ErrorCodeText: string.Empty,
                DeadlineUtc: DateTimeOffset.UtcNow.AddHours(12))
        ];

        return ValueTask.FromResult(new MecmPendingUpdatesSnapshot(
            demoDataCatalog.NormalizeHost(host),
            entries,
            []));
    }

    public ValueTask<MecmAllUpdatesSnapshot> GetAllUpdatesAsync(string host, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        IReadOnlyList<MecmAllUpdateEntry> entries =
        [
            new(
                UniqueId: "Site_001/SUM_501",
                Title: "2026-04 Cumulative Update for Windows 11 24H2 for x64-based Systems",
                Article: "KB5060001",
                Bulletin: "MS26-041",
                Language: "en-US",
                RevisionNumber: 205,
                ScanTimeUtc: DateTimeOffset.UtcNow.AddHours(-6),
                SourceVersion: 3,
                Status: "Missing",
                ProductId: "Windows 11"),
            new(
                UniqueId: "Site_001/SUM_503",
                Title: ".NET 8.0.15 Security Update",
                Article: "KB5060008",
                Bulletin: "MS26-043",
                Language: "en-US",
                RevisionNumber: 33,
                ScanTimeUtc: DateTimeOffset.UtcNow.AddHours(-6),
                SourceVersion: 1,
                Status: "Installed",
                ProductId: ".NET")
        ];

        return ValueTask.FromResult(new MecmAllUpdatesSnapshot(
            demoDataCatalog.NormalizeHost(host),
            entries,
            []));
    }

    public ValueTask<DeviceActionResult> InstallUpdatesAsync(string host, MecmUpdateInstallRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(DeviceActionResult.Ok(
            $"Demo MECM update action '{request.Mode}' executed on '{demoDataCatalog.NormalizeHost(host)}'."));
    }

    public ValueTask<MecmPackagesSnapshot> GetPackagesAsync(string host, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        IReadOnlyList<MecmPackageEntry> entries =
        [
            new(
                AdvertisementId: "ADV00001",
                PackageId: "PKG00001",
                PackageName: "ConfigMgr Client Repair",
                ProgramId: "Repair",
                ProgramName: "Repair Client",
                Manufacturer: "Contoso",
                Version: "1.0",
                IsMandatory: true,
                RepeatRunBehavior: "RerunAlways",
                LastRunStatus: "Mandatory",
                LastExitCode: null,
                LastRunTimeUtc: null,
                AvailableFromUtc: DateTimeOffset.UtcNow.AddHours(-4),
                ExpiresUtc: null,
                RequiresUserInput: false,
                Comment: "Repairs the MECM client agent."),
            new(
                AdvertisementId: "ADV00002",
                PackageId: "PKG00002",
                PackageName: "Branch Tools",
                ProgramId: "Install",
                ProgramName: "Install Toolkit",
                Manufacturer: "Contoso",
                Version: "4.2",
                IsMandatory: false,
                RepeatRunBehavior: "NeverRerunDeployedProgram",
                LastRunStatus: "Available",
                LastExitCode: null,
                LastRunTimeUtc: null,
                AvailableFromUtc: DateTimeOffset.UtcNow.AddDays(-1),
                ExpiresUtc: DateTimeOffset.UtcNow.AddDays(7),
                RequiresUserInput: false,
                Comment: "Optional software distribution package.")
        ];

        return ValueTask.FromResult(new MecmPackagesSnapshot(
            demoDataCatalog.NormalizeHost(host),
            entries,
            []));
    }

    public ValueTask<DeviceActionResult> ExecutePackageAsync(string host, string advertisementId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(DeviceActionResult.Ok(
            $"Demo MECM package '{advertisementId}' queued on '{demoDataCatalog.NormalizeHost(host)}'."));
    }

    public ValueTask<MecmBaselinesSnapshot> GetBaselinesAsync(string host, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        IReadOnlyList<MecmBaselineEntry> entries =
        [
            new(
                Name: "Demo-Baseline-001",
                DisplayName: "Windows Servicing Baseline",
                Version: "7",
                IsMachineTarget: true,
                IsCompliant: true,
                LastComplianceStatus: 1,
                Status: 1,
                LastEvalTimeUtc: DateTimeOffset.UtcNow.AddMinutes(-25),
                ComplianceDetailsSummary: "2 configuration items evaluated."),
            new(
                Name: "Demo-Baseline-002",
                DisplayName: "Security Hardening Baseline",
                Version: "4",
                IsMachineTarget: true,
                IsCompliant: false,
                LastComplianceStatus: 0,
                Status: 1,
                LastEvalTimeUtc: DateTimeOffset.UtcNow.AddHours(-3),
                ComplianceDetailsSummary: "1 configuration item is non-compliant.")
        ];

        return ValueTask.FromResult(new MecmBaselinesSnapshot(
            demoDataCatalog.NormalizeHost(host),
            entries,
            []));
    }

    public ValueTask<MecmBaselineDetails> GetBaselineDetailsAsync(string host, string baselineName, string version, bool isMachineTarget, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        IReadOnlyList<MecmBaselineConfigItem> items =
        [
            new("CI-001", "Windows Update Ring", "Checks servicing cadence.", "1.0", "Setting", true, true, true, string.Empty),
            new("CI-002", "Defender Platform", "Validates Defender platform currency.", "1.0", "Setting", false, true, true, "Platform is older than policy target.")
        ];

        return ValueTask.FromResult(new MecmBaselineDetails(
            baselineName,
            baselineName,
            version,
            isMachineTarget,
            items,
            []));
    }

    public ValueTask<DeviceActionResult> TriggerBaselineEvaluationAsync(string host, string baselineName, string version, bool isMachineTarget, bool enforce, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(DeviceActionResult.Ok(
            $"Demo MECM baseline '{baselineName}' evaluation queued on '{demoDataCatalog.NormalizeHost(host)}'."));
    }
}

internal sealed class DemoWindowsProfileManager(DemoDataCatalog demoDataCatalog) : IWindowsProfileManager
{
    public ValueTask<WindowsProfileSnapshot> GetProfilesAsync(string host, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        IReadOnlyList<WindowsProfileEntry> profiles =
        [
            new(@"DEMO\alex.wilson", "S-1-5-21-100-200-300-1001", @"C:\Users\alex.wilson", DateTimeOffset.UtcNow.AddHours(-2), @"\\profiles\alex.wilson", true, false, true, false, false, false),
            new(@"DEMO\helpdesk.ops", "S-1-5-21-100-200-300-1002", @"C:\Users\helpdesk.ops", DateTimeOffset.UtcNow.AddDays(-1), string.Empty, false, false, false, false, false, false),
            new(@"DEMO\temp.user", "S-1-5-21-100-200-300-1003", @"C:\Users\TEMP.demo", DateTimeOffset.UtcNow.AddMinutes(-25), string.Empty, false, true, false, false, true, false)
        ];

        var policy = new WindowsProfilePolicyInfo(
            500,
            true,
            ["AppData\\Local\\Temp", "AppData\\Local\\Microsoft\\Teams\\Current\\Cache"],
            @"HKLM:\SOFTWARE\Policies\Microsoft\Windows\System");

        return ValueTask.FromResult(new WindowsProfileSnapshot(
            demoDataCatalog.NormalizeHost(host),
            false,
            profiles,
            policy,
            []));
    }

    public ValueTask<WindowsProfileSizeResult> CalculateProfileSizeAsync(string host, string profileLocalPath, ProfileSizeCalculationMode mode, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var isExcluded = mode == ProfileSizeCalculationMode.PolicyExcluded;
        var sizeBytes = isExcluded ? 8_400_000_000L : 10_900_000_000L;
        var fileCount = isExcluded ? 68420 : 71234;
        var directoryCount = isExcluded ? 4821 : 4975;

        return ValueTask.FromResult(new WindowsProfileSizeResult(
            profileLocalPath,
            mode,
            sizeBytes,
            fileCount,
            directoryCount,
            []));
    }

    public ValueTask<DeviceActionResult> DeleteProfileAsync(string host, string sid, string profileLocalPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(DeviceActionResult.Ok(
            $"Demo profile '{profileLocalPath}' renamed and registry key for '{sid}' removed on '{demoDataCatalog.NormalizeHost(host)}'."));
    }
}

internal sealed class DemoLocalBitLockerService(DemoDataCatalog demoDataCatalog) : ILocalBitLockerService
{
    public ValueTask<BitLockerHostSnapshot> GetSnapshotAsync(string host, CancellationToken cancellationToken, bool verboseDiagnostics = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(demoDataCatalog.CreateBitLockerSnapshot(host));
    }

    public ValueTask<BitLockerActionResult> SuspendProtectionAsync(string host, string mountPoint, int rebootCount, CancellationToken cancellationToken, bool verboseDiagnostics = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(BitLockerActionResult.Ok($"Demo BitLocker suspend simulated on '{mountPoint}'."));
    }

    public ValueTask<BitLockerActionResult> ResumeProtectionAsync(string host, string mountPoint, CancellationToken cancellationToken, bool verboseDiagnostics = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(BitLockerActionResult.Ok($"Demo BitLocker resume simulated on '{mountPoint}'."));
    }

    public ValueTask<BitLockerActionResult> AddRecoveryPasswordProtectorAsync(string host, string mountPoint, CancellationToken cancellationToken, bool verboseDiagnostics = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(BitLockerActionResult.Ok("Demo recovery-password protector added.", "demo-rec-added"));
    }

    public ValueTask<BitLockerActionResult> RemoveRecoveryPasswordProtectorAsync(string host, string mountPoint, string protectorId, CancellationToken cancellationToken, bool verboseDiagnostics = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(BitLockerActionResult.Ok($"Demo recovery-password protector '{protectorId}' removed."));
    }

    public ValueTask<BitLockerActionResult> BackupRecoveryPasswordAsync(string host, string mountPoint, string protectorId, CancellationToken cancellationToken, bool verboseDiagnostics = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(BitLockerActionResult.Ok("Demo recovery-password backup simulated.", details: ["No escrow operation was sent to Microsoft Entra or AD DS."]));
    }

    public ValueTask<BitLockerActionResult> RotateRecoveryPasswordAsync(string host, string mountPoint, string protectorId, CancellationToken cancellationToken, bool verboseDiagnostics = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(BitLockerActionResult.Ok("Demo recovery-password rotation simulated.", "demo-rec-rotated"));
    }
}

internal sealed class DemoDefenderDiagnosticsService(DemoDataCatalog demoDataCatalog) : IDefenderDiagnosticsService
{
    public ValueTask<DefenderSnapshot> GetSnapshotAsync(string host, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(demoDataCatalog.CreateDefenderSnapshot(host));
    }

    public ValueTask<DefenderSnapshotDiagnosticsResult> GetSnapshotDiagnosticsAsync(string host, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new DefenderSnapshotDiagnosticsResult(
            demoDataCatalog.CreateDefenderSnapshot(host),
            ["Demo Defender snapshot materialized from the in-memory catalog in 5 ms."]));
    }

    public ValueTask<DefenderSettingsSnapshot> GetSettingsAsync(string host, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(demoDataCatalog.CreateDefenderSettingsSnapshot());
    }

    public ValueTask<IReadOnlyList<DefenderDetectionEntry>> GetDetectionsAsync(string host, int daysBack, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(demoDataCatalog.CreateDefenderDetections());
    }

    public ValueTask<DefenderDeviceControlSnapshot> GetDeviceControlEventsAsync(string host, int daysBack, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(demoDataCatalog.CreateDefenderDeviceControlSnapshot());
    }

    public ValueTask<DefenderActionResult> ExecuteActionAsync(string host, DefenderActionRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(DefenderActionResult.Ok($"Demo Defender action '{request.ActionType}' simulated."));
    }
}
