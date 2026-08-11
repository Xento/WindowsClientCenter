using WindowsClientCenter.Defender.Contracts;
using WindowsClientCenter.Defender.Contracts.Models;
using WindowsClientCenter.Intune.Services.Contracts;
using WindowsClientCenter.Plugin.Abstractions.Contracts;
using WindowsClientCenter.Plugins.WindowsDefenderAgent.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace WindowsClientCenter.Tests.Plugins.WindowsDefenderAgent;

public sealed class WindowsDefenderAgentViewModelTests
{
    [Theory]
    [InlineData(null, 0)]
    [InlineData("overview", 0)]
    [InlineData("protection-status", 1)]
    [InlineData("versions", 2)]
    [InlineData("scans", 3)]
    [InlineData("settings", 4)]
    [InlineData("detections", 5)]
    [InlineData("device-control", 6)]
    [InlineData("asr-rules", 4)]
    [InlineData("unknown", 0)]
    public void MapNavigationTargetToSectionIndex_ReturnsExpectedValue(string? target, int expected)
    {
        Assert.Equal(expected, WindowsDefenderAgentViewModel.MapNavigationTargetToSectionIndex(target));
    }

    [Fact]
    public void EvaluateHealthPresentation_ReturnsGreen_WhenSnapshotHealthy()
    {
        var snapshot = CreateSnapshot(signatureAgeHours: 4, activeDetections: 0, highCriticalDetections: 0, realtimeEnabled: true, antivirusEnabled: true, tamperEnabled: true);

        var result = WindowsDefenderAgentViewModel.EvaluateHealthPresentation(snapshot);

        Assert.Equal("Green", result.Level);
        Assert.Equal("#1A7F37", result.ColorHex);
    }

    [Fact]
    public void EvaluateHealthPresentation_KeepsGreen_WhenDefinitionsAreStillWithinFreshnessWindow()
    {
        var snapshot = CreateSnapshot(signatureAgeHours: 30, activeDetections: 0, highCriticalDetections: 0, realtimeEnabled: true, antivirusEnabled: true, tamperEnabled: true);

        var result = WindowsDefenderAgentViewModel.EvaluateHealthPresentation(snapshot);

        Assert.Equal("Green", result.Level);
        Assert.Equal("#1A7F37", result.ColorHex);
    }

    [Fact]
    public void EvaluateHealthPresentation_ReturnsRed_WhenSignaturesStale()
    {
        var snapshot = CreateSnapshot(signatureAgeHours: 100, activeDetections: 0, highCriticalDetections: 0, realtimeEnabled: true, antivirusEnabled: true, tamperEnabled: true);

        var result = WindowsDefenderAgentViewModel.EvaluateHealthPresentation(snapshot);

        Assert.Equal("Red", result.Level);
        Assert.Equal("#C62828", result.ColorHex);
    }

    [Fact]
    public void EvaluateVersionBaselinePresentation_ReturnsGreen_WhenAllComponentsMatch()
    {
        var latest = new DefenderLatestVersionInfo(
            SourceUrl: "https://www.microsoft.com/en-us/wdsi/defenderupdates",
            ReleaseNotesUrl: "https://www.microsoft.com/en-us/wdsi/definitions/antimalware-definition-release-notes",
            RetrievedAtUtc: DateTimeOffset.UtcNow,
            SecurityIntelligenceVersion: "1.421.123.0",
            EngineVersion: "1.1.24000.1",
            PlatformVersion: "4.18.24000.6",
            ReleasedAtUtc: DateTimeOffset.UtcNow);
        var snapshot = CreateSnapshot(
            signatureAgeHours: 2,
            activeDetections: 0,
            highCriticalDetections: 0,
            realtimeEnabled: true,
            antivirusEnabled: true,
            tamperEnabled: true,
            latestVersionInfo: latest);

        var result = WindowsDefenderAgentViewModel.EvaluateVersionBaselinePresentation(snapshot);

        Assert.Equal("Current", result.Status);
        Assert.Equal("#1A7F37", result.ColorHex);
    }

    [Fact]
    public void EvaluateVersionBaselinePresentation_ReturnsYellow_WhenPlatformDiffers()
    {
        var latest = new DefenderLatestVersionInfo(
            SourceUrl: "https://www.microsoft.com/en-us/wdsi/defenderupdates",
            ReleaseNotesUrl: "https://www.microsoft.com/en-us/wdsi/definitions/antimalware-definition-release-notes",
            RetrievedAtUtc: DateTimeOffset.UtcNow,
            SecurityIntelligenceVersion: "1.421.123.0",
            EngineVersion: "1.1.24000.1",
            PlatformVersion: "4.18.25000.1",
            ReleasedAtUtc: DateTimeOffset.UtcNow);
        var snapshot = CreateSnapshot(
            signatureAgeHours: 4,
            activeDetections: 0,
            highCriticalDetections: 0,
            realtimeEnabled: true,
            antivirusEnabled: true,
            tamperEnabled: true,
            latestVersionInfo: latest);

        var result = WindowsDefenderAgentViewModel.EvaluateVersionBaselinePresentation(snapshot);

        Assert.Equal("Needs update", result.Status);
        Assert.Equal("#B07D00", result.ColorHex);
    }

    [Fact]
    public void EvaluateVersionBaselinePresentation_ReturnsRed_WhenPlatformDiffersForLongTime()
    {
        var latest = new DefenderLatestVersionInfo(
            SourceUrl: "https://www.microsoft.com/en-us/wdsi/defenderupdates",
            ReleaseNotesUrl: "https://www.microsoft.com/en-us/wdsi/definitions/antimalware-definition-release-notes",
            RetrievedAtUtc: DateTimeOffset.UtcNow,
            SecurityIntelligenceVersion: "1.421.123.0",
            EngineVersion: "1.1.24000.1",
            PlatformVersion: "4.18.25000.1",
            ReleasedAtUtc: DateTimeOffset.UtcNow.AddDays(-45));
        var snapshot = CreateSnapshot(
            signatureAgeHours: 4,
            activeDetections: 0,
            highCriticalDetections: 0,
            realtimeEnabled: true,
            antivirusEnabled: true,
            tamperEnabled: true,
            latestVersionInfo: latest);

        var result = WindowsDefenderAgentViewModel.EvaluateVersionBaselinePresentation(snapshot);

        Assert.Equal("Outdated", result.Status);
        Assert.Equal("#C62828", result.ColorHex);
    }

    [Fact]
    public void EvaluateVersionBaselinePresentation_KeepsSecurityIntelligenceCurrent_WithinFreshnessWindow()
    {
        var latest = new DefenderLatestVersionInfo(
            SourceUrl: "https://www.microsoft.com/en-us/wdsi/defenderupdates",
            ReleaseNotesUrl: "https://www.microsoft.com/en-us/wdsi/definitions/antimalware-definition-release-notes",
            RetrievedAtUtc: DateTimeOffset.UtcNow,
            SecurityIntelligenceVersion: "1.421.999.0",
            EngineVersion: "1.1.24000.1",
            PlatformVersion: "4.18.24000.6",
            ReleasedAtUtc: DateTimeOffset.UtcNow);
        var snapshot = CreateSnapshot(
            signatureAgeHours: 12,
            activeDetections: 0,
            highCriticalDetections: 0,
            realtimeEnabled: true,
            antivirusEnabled: true,
            tamperEnabled: true,
            latestVersionInfo: latest);

        var result = WindowsDefenderAgentViewModel.EvaluateVersionBaselinePresentation(snapshot);

        Assert.Equal("Current", result.Status);
        Assert.Equal("#1A7F37", result.ColorHex);
        Assert.Contains("freshness threshold", result.Details, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EvaluateVersionBaselinePresentation_ReturnsRed_WhenSignaturesStaleAndBehind()
    {
        var latest = new DefenderLatestVersionInfo(
            SourceUrl: "https://www.microsoft.com/en-us/wdsi/defenderupdates",
            ReleaseNotesUrl: "https://www.microsoft.com/en-us/wdsi/definitions/antimalware-definition-release-notes",
            RetrievedAtUtc: DateTimeOffset.UtcNow,
            SecurityIntelligenceVersion: "1.421.999.0",
            EngineVersion: "1.1.24000.1",
            PlatformVersion: "4.18.24000.6",
            ReleasedAtUtc: DateTimeOffset.UtcNow);
        var snapshot = CreateSnapshot(
            signatureAgeHours: 90,
            activeDetections: 0,
            highCriticalDetections: 0,
            realtimeEnabled: true,
            antivirusEnabled: true,
            tamperEnabled: true,
            latestVersionInfo: latest);

        var result = WindowsDefenderAgentViewModel.EvaluateVersionBaselinePresentation(snapshot);

        Assert.Equal("Outdated", result.Status);
        Assert.Equal("#C62828", result.ColorHex);
    }

    [Fact]
    public async Task RefreshOverviewAsync_WhenWinRmFails_UsesConnectionErrorText()
    {
        var hostStatus = new FakeHostStatusLogSink();
        var services = BuildServices(
            new FakeTargetHostService("CLIENT01"),
            new FakeDefenderDiagnosticsService
            {
                SnapshotException = new InvalidOperationException("WinRM cannot connect to destination")
            },
            hostStatus);

        var viewModel = new WindowsDefenderAgentViewModel(new FakePluginContext(services), "overview", (_, _) => true);

        await viewModel.RefreshOverviewAsync();

        Assert.Equal("Connection to 'CLIENT01' failed: WinRM connection failed.", viewModel.Status);
        Assert.Contains(hostStatus.Messages, msg => msg.Contains("Connection to 'CLIENT01' failed", StringComparison.Ordinal));
    }

    [Fact]
    public void ActionCommands_AreDisabled_WhenBusyOrActionRunning()
    {
        var services = BuildServices(
            new FakeTargetHostService("CLIENT01"),
            new FakeDefenderDiagnosticsService(),
            new FakeHostStatusLogSink());
        var viewModel = new WindowsDefenderAgentViewModel(new FakePluginContext(services), "overview", (_, _) => true);

        Assert.True(viewModel.StartQuickScanCommand.CanExecute(null));

        viewModel.IsBusy = true;
        Assert.False(viewModel.StartQuickScanCommand.CanExecute(null));

        viewModel.IsBusy = false;
        viewModel.IsActionBusy = true;
        Assert.False(viewModel.StartQuickScanCommand.CanExecute(null));
    }

    [Fact]
    public async Task RefreshOverviewAsync_UsesGlobalVerboseOperationsSetting()
    {
        var hostStatus = new FakeHostStatusLogSink();
        var services = BuildServices(
            new FakeTargetHostService("CLIENT01"),
            new FakeDefenderDiagnosticsService(),
            hostStatus);

        var viewModel = new WindowsDefenderAgentViewModel(
            new FakePluginContext(
                services,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["VerboseOperations"] = "true"
                }),
            "overview",
            (_, _) => true);

        await viewModel.RefreshOverviewAsync();

        Assert.Contains(hostStatus.Messages, message => message.Contains("[Defender][Verbose]", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RefreshOverviewAsync_PluginSettingOverridesGlobalVerboseOperations()
    {
        var hostStatus = new FakeHostStatusLogSink();
        var services = BuildServices(
            new FakeTargetHostService("CLIENT01"),
            new FakeDefenderDiagnosticsService(),
            hostStatus);

        var viewModel = new WindowsDefenderAgentViewModel(
            new FakePluginContext(
                services,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["VerboseOperations"] = "false",
                    ["verboseOperations"] = "true"
                }),
            "overview",
            (_, _) => true);

        await viewModel.RefreshOverviewAsync();

        Assert.Contains(hostStatus.Messages, message => message.Contains("[Defender][Verbose]", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RefreshDeviceControlAsync_AppliesEventsAndSummaries()
    {
        var blockedAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        var deviceControlSnapshot = new DefenderDeviceControlSnapshot(
            DateTimeOffset.UtcNow,
            "Local Device Control events",
            [],
            [
                new DefenderDeviceControlEventEntry(
                    blockedAt,
                    1123,
                    "Microsoft-Windows-Windows Defender",
                    "Microsoft-Windows-Windows Defender/Operational",
                    "Warning",
                    "Removable storage",
                    "USB Mass Storage",
                    "Contoso USB Drive",
                    "Contoso",
                    "USBSTOR\\DISK",
                    "USBSTOR\\DISK&VEN_CONTOSO&PROD_FASTUSB\\123456",
                    "USBSTOR\\DISK&VEN_CONTOSO&PROD_FASTUSB",
                    "1234",
                    "5678",
                    "123456",
                    "{53f56307-b6bf-11d0-94f2-00a0c91efb8b}",
                    "CONTOSO\\user1",
                    "S-1-5-21",
                    "Block removable storage",
                    "policy-1",
                    "rule-1",
                    "Deny",
                    "Read",
                    "Blocked",
                    true,
                    "Device Control blocked USB device.")
            ],
            [
                new DefenderDeviceControlDeviceSummary(
                    "USBSTOR\\DISK&VEN_CONTOSO&PROD_FASTUSB\\123456",
                    "Removable storage",
                    "Contoso USB Drive",
                    1,
                    blockedAt,
                    blockedAt,
                    "USBSTOR\\DISK",
                    "USBSTOR\\DISK&VEN_CONTOSO&PROD_FASTUSB\\123456",
                    "USBSTOR\\DISK&VEN_CONTOSO&PROD_FASTUSB",
                    "1234",
                    "5678",
                    "123456",
                    "{53f56307-b6bf-11d0-94f2-00a0c91efb8b}",
                    "Block removable storage",
                    "policy-1",
                    "rule-1",
                    "Deny",
                    "Read",
                    "CONTOSO\\user1")
            ]);
        var services = BuildServices(
            new FakeTargetHostService("CLIENT01"),
            new FakeDefenderDiagnosticsService { DeviceControlSnapshot = deviceControlSnapshot },
            new FakeHostStatusLogSink());
        var viewModel = new WindowsDefenderAgentViewModel(new FakePluginContext(services), "device-control", (_, _) => true);

        await viewModel.RefreshDeviceControlAsync();

        Assert.Equal(6, viewModel.SelectedSectionIndex);
        Assert.Single(viewModel.DeviceControlEvents);
        Assert.Single(viewModel.DeviceControlSummaries);
        Assert.Contains("1 Device Control event", viewModel.DeviceControlStatus, StringComparison.Ordinal);
        Assert.Equal("USBSTOR\\DISK&VEN_CONTOSO&PROD_FASTUSB\\123456", viewModel.DeviceControlSummaries[0].DeviceInstanceId);
    }

    private static ServiceProvider BuildServices(
        ITargetHostService targetHostService,
        IDefenderDiagnosticsService defenderService,
        IHostStatusLogSink hostStatusLogSink)
    {
        return new ServiceCollection()
            .AddSingleton(targetHostService)
            .AddSingleton(defenderService)
            .AddSingleton(hostStatusLogSink)
            .BuildServiceProvider();
    }

    private static DefenderSnapshot CreateSnapshot(
        double signatureAgeHours,
        int activeDetections,
        int highCriticalDetections,
        bool realtimeEnabled,
        bool antivirusEnabled,
        bool tamperEnabled,
        DefenderLatestVersionInfo? latestVersionInfo = null)
    {
        const double warningThresholdHours = 36;
        const double criticalThresholdHours = 72;
        return new DefenderSnapshot(
            Host: "CLIENT01",
            MachineName: "CLIENT01",
            CapturedAtUtc: DateTimeOffset.UtcNow,
            IsLocalHost: false,
            IsManaged: true,
            ManagedBy: "MDM (Intune)",
            Protection: new DefenderProtectionStatus(
                AntivirusEnabled: antivirusEnabled,
                RealtimeProtectionEnabled: realtimeEnabled,
                BehaviorMonitorEnabled: true,
                IoavProtectionEnabled: true,
                OnAccessProtectionEnabled: true,
                NisEnabled: true,
                TamperProtectionEnabled: tamperEnabled,
                RunningMode: "Normal"),
            Versions: new DefenderVersionInfo(
                EngineVersion: "1.1.24000.1",
                ProductVersion: "4.18.24000.6",
                AntivirusSignatureVersion: "1.421.123.0",
                AntispywareSignatureVersion: "1.421.123.0",
                NisEngineVersion: "1.1.24000.1",
                NisSignatureVersion: "1.421.123.0",
                SignatureLastUpdatedUtc: DateTimeOffset.UtcNow.AddHours(-signatureAgeHours),
                SignatureAgeHours: signatureAgeHours,
                SignaturesOutdated: signatureAgeHours > warningThresholdHours,
                SignatureWarningThresholdHours: warningThresholdHours,
                SignatureCriticalThresholdHours: criticalThresholdHours),
            Scans: new DefenderScanInfo(
                QuickScanStartUtc: DateTimeOffset.UtcNow.AddHours(-2),
                QuickScanEndUtc: DateTimeOffset.UtcNow.AddHours(-1),
                FullScanStartUtc: DateTimeOffset.UtcNow.AddDays(-7),
                FullScanEndUtc: DateTimeOffset.UtcNow.AddDays(-7).AddHours(1),
                LastScanUtc: DateTimeOffset.UtcNow.AddHours(-1)),
            ActiveDetectionCount: activeDetections,
            ActiveHighOrCriticalDetectionCount: highCriticalDetections,
            HealthLevel: "Green",
            HealthSummary: "ok",
            Notes: [],
            LatestVersionInfo: latestVersionInfo);
    }

    private sealed class FakePluginContext(IServiceProvider services, IReadOnlyDictionary<string, string>? settings = null) : IPluginContext
    {
        public Microsoft.Extensions.Logging.ILogger Logger { get; } = NullLogger.Instance;

        public IServiceProvider Services { get; } = services;

        public string EnvironmentName => "Test";

        public IReadOnlyDictionary<string, string> Settings { get; } = settings ?? new Dictionary<string, string>();
    }

    private sealed class FakeTargetHostService(string host) : ITargetHostService
    {
        private string _currentHost = host;
        private long _version = 1;
        private CancellationTokenSource _selectionCancellationTokenSource = new();

        public string CurrentHost => _currentHost;

        public event EventHandler<string>? HostChanged;

        public HostSelection CaptureSelection() => new(_currentHost, _version, _selectionCancellationTokenSource.Token);

        public bool IsCurrent(HostSelection selection) => selection.Version == _version && string.Equals(selection.Host, _currentHost, StringComparison.OrdinalIgnoreCase);

        public void SetCurrentHost(string host)
        {
            if (!string.Equals(_currentHost, host, StringComparison.OrdinalIgnoreCase))
            {
                _selectionCancellationTokenSource.Cancel();
                _selectionCancellationTokenSource.Dispose();
                _selectionCancellationTokenSource = new CancellationTokenSource();
                _version++;
            }

            _currentHost = host;
            HostChanged?.Invoke(this, host);
        }
    }

    private sealed class FakeHostStatusLogSink : IHostStatusLogSink
    {
        public List<string> Messages { get; } = [];

        public void Append(string message)
        {
            Messages.Add(message);
        }
    }

    private sealed class FakeDefenderDiagnosticsService : IDefenderDiagnosticsService
    {
        public Exception? SnapshotException { get; init; }
        public DefenderDeviceControlSnapshot DeviceControlSnapshot { get; init; } = new(
            DateTimeOffset.UtcNow,
            "Local Device Control events",
            [],
            [],
            []);

        public ValueTask<DefenderSnapshot> GetSnapshotAsync(string host, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (SnapshotException is not null)
            {
                throw SnapshotException;
            }

            return ValueTask.FromResult(CreateSnapshot(2, 0, 0, realtimeEnabled: true, antivirusEnabled: true, tamperEnabled: true));
        }

        public async ValueTask<DefenderSnapshotDiagnosticsResult> GetSnapshotDiagnosticsAsync(string host, CancellationToken cancellationToken)
        {
            var snapshot = await GetSnapshotAsync(host, cancellationToken);
            return new DefenderSnapshotDiagnosticsResult(snapshot, ["PowerShell defender snapshot script completed in 12 ms."]);
        }

        public ValueTask<DefenderSettingsSnapshot> GetSettingsAsync(string host, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new DefenderSettingsSnapshot(DateTimeOffset.UtcNow, "Get-MpPreference", [], []));
        }

        public ValueTask<IReadOnlyList<DefenderDetectionEntry>> GetDetectionsAsync(string host, int daysBack, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IReadOnlyList<DefenderDetectionEntry>>([]);
        }

        public ValueTask<DefenderDeviceControlSnapshot> GetDeviceControlEventsAsync(string host, int daysBack, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(DeviceControlSnapshot);
        }

        public ValueTask<DefenderActionResult> ExecuteActionAsync(string host, DefenderActionRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(DefenderActionResult.Ok("ok"));
        }
    }
}
