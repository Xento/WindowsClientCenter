using WindowsClientCenter.Intune.Services.Contracts;
using WindowsClientCenter.Intune.Services.Models;
using WindowsClientCenter.Plugin.Abstractions.Contracts;
using WindowsClientCenter.Plugins.MecmAgent.Models;
using WindowsClientCenter.Plugins.MecmAgent.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace WindowsClientCenter.Tests.Plugins.MecmAgent;

public sealed class MecmAgentViewModelTests
{
    [Fact]
    public async Task InitializeAsync_DefaultsToOverview()
    {
        var service = new FakeMecmClientService
        {
            OverviewSnapshots = [CreateOverviewSnapshot()]
        };
        var viewModel = CreateViewModel(service);

        await viewModel.InitializeAsync(CancellationToken.None);

        Assert.True(viewModel.IsOverviewSection);
        Assert.Equal("5.00.9128.1005", viewModel.OverviewClientVersionText);
    }

    [Fact]
    public async Task InitializeAsync_PackagesSectionLoadsPackages()
    {
        var service = new FakeMecmClientService
        {
            PackageSnapshots =
            [
                new MecmPackagesSnapshot(
                    "CLIENT01",
                    [
                        new MecmPackageEntry("ADV1", "PKG1", "Client Repair", "Repair", "Repair Client", "Contoso", "1.0", true, "RerunAlways", "Mandatory", null, null, DateTimeOffset.UtcNow, null, false, "Repair package")
                    ],
                    [])
            ]
        };
        var viewModel = CreateViewModel(service, "packages");

        await viewModel.InitializeAsync(CancellationToken.None);

        Assert.True(viewModel.IsPackagesSection);
        Assert.Single(viewModel.VisiblePackages);
        Assert.Equal("Client Repair", viewModel.VisiblePackages[0].PackageName);
    }

    [Fact]
    public async Task InitializeAsync_BaselinesSectionLoadsDetailsForSelection()
    {
        var service = new FakeMecmClientService
        {
            BaselineSnapshots =
            [
                new MecmBaselinesSnapshot(
                    "CLIENT01",
                    [
                        new MecmBaselineEntry("Baseline-1", "Baseline 1", "1", true, true, 1, 1, DateTimeOffset.UtcNow, "1 item")
                    ],
                    [])
            ],
            BaselineDetailsSnapshots =
            [
                new MecmBaselineDetails(
                    "Baseline-1",
                    "Baseline 1",
                    "1",
                    true,
                    [
                        new MecmBaselineConfigItem("CI-1", "Config Item 1", "Description", "1.0", "Setting", true, true, true, string.Empty)
                    ],
                    [])
            ]
        };
        var viewModel = CreateViewModel(service, "dcm-baselines");

        await viewModel.InitializeAsync(CancellationToken.None);

        Assert.True(viewModel.IsDcmBaselinesSection);
        Assert.Single(viewModel.VisibleBaselines);
        Assert.Single(viewModel.VisibleBaselineConfigItems);
    }

    [Fact]
    public async Task LoadApplicationsAsync_PreservesSelectedApplicationWherePossible()
    {
        var service = new FakeMecmClientService
        {
            ApplicationsSnapshots =
            [
                CreateApplicationsSnapshot(("AppA", true, "Install"), ("AppB", true, "Install")),
                CreateApplicationsSnapshot(("AppA", true, "Install"), ("AppB", true, "Install"))
            ]
        };
        var viewModel = CreateViewModel(service, "applications");

        await viewModel.InitializeAsync(CancellationToken.None);
        viewModel.SelectedApplication = viewModel.VisibleApplications.Single(row => row.Name == "AppB");

        await viewModel.RefreshCurrentSectionAsync();

        Assert.Equal("AppB", viewModel.SelectedApplication?.Name);
    }

    [Fact]
    public async Task HideNonUserUiExperienceApps_FiltersRows()
    {
        var service = new FakeMecmClientService
        {
            ApplicationsSnapshots = [CreateApplicationsSnapshot(("UserVisible", true, "Install"), ("Hidden", false, "Install"))]
        };
        var viewModel = CreateViewModel(service, "applications");
        await viewModel.InitializeAsync(CancellationToken.None);

        viewModel.HideNonUserUiExperienceApps = true;

        Assert.Single(viewModel.VisibleApplications);
        Assert.Equal("UserVisible", viewModel.VisibleApplications[0].Name);
    }

    [Fact]
    public async Task ApplicationCommands_EnableOnlyWhenAllSelectedRowsSupportAction()
    {
        var service = new FakeMecmClientService
        {
            ApplicationsSnapshots = [CreateApplicationsSnapshot(("Repairable", true, "Repair"), ("InstallOnly", true, "Install"))]
        };
        var viewModel = CreateViewModel(service, "applications");
        await viewModel.InitializeAsync(CancellationToken.None);

        viewModel.UpdateSelectedApplications(viewModel.VisibleApplications.ToArray());

        Assert.False(viewModel.RepairApplicationsCommand.CanExecute(null));
        Assert.False(viewModel.InstallApplicationsCommand.CanExecute(null));
    }

    [Fact]
    public async Task InstallCommand_UsesApplicationStateWhenAllowedActionsAreEmpty()
    {
        var snapshot = new MecmApplicationSnapshot(
            "CLIENT01",
            [
                new MecmApplicationEntry(
                    "App-1",
                    "AvailableApp",
                    "AvailableApp",
                    string.Empty,
                    string.Empty,
                    "1.0",
                    "1",
                    true,
                    false,
                    true,
                    [],
                    "NotInstalled",
                    "Applicable",
                    "Available",
                    3,
                    "Application is available for enforcement (install or uninstall based on resolved state). Content may/may not have been downloaded.",
                    0,
                    string.Empty,
                    DateTimeOffset.UtcNow,
                    null,
                    false,
                    false,
                    false)
            ],
            []);

        var service = new FakeMecmClientService
        {
            ApplicationsSnapshots = [snapshot]
        };
        var viewModel = CreateViewModel(service, "applications");
        await viewModel.InitializeAsync(CancellationToken.None);
        viewModel.UpdateSelectedApplications(viewModel.VisibleApplications.ToArray());

        Assert.True(viewModel.InstallApplicationsCommand.CanExecute(null));
        Assert.False(viewModel.UninstallApplicationsCommand.CanExecute(null));
    }

    [Fact]
    public async Task UninstallCommand_UsesInstalledStateWhenAllowedActionsAreEmpty()
    {
        var snapshot = new MecmApplicationSnapshot(
            "CLIENT01",
            [
                new MecmApplicationEntry(
                    "App-1",
                    "InstalledApp",
                    "InstalledApp",
                    string.Empty,
                    string.Empty,
                    "1.0",
                    "1",
                    true,
                    false,
                    true,
                    [],
                    "Installed",
                    "Applicable",
                    "Installed",
                    1,
                    "Application is enforced to desired/resolved state.",
                    0,
                    string.Empty,
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow,
                    false,
                    false,
                    false)
            ],
            []);

        var service = new FakeMecmClientService
        {
            ApplicationsSnapshots = [snapshot]
        };
        var viewModel = CreateViewModel(service, "applications");
        await viewModel.InitializeAsync(CancellationToken.None);
        viewModel.UpdateSelectedApplications(viewModel.VisibleApplications.ToArray());

        Assert.True(viewModel.UninstallApplicationsCommand.CanExecute(null));
    }

    [Fact]
    public async Task UninstallTooltip_ExplainsWhyCommandIsDisabled()
    {
        var snapshot = new MecmApplicationSnapshot(
            "CLIENT01",
            [
                new MecmApplicationEntry(
                    "App-1",
                    "InstallOnly",
                    "InstallOnly",
                    string.Empty,
                    string.Empty,
                    "1.0",
                    "1",
                    true,
                    false,
                    true,
                    ["Install"],
                    "NotInstalled",
                    "Applicable",
                    "Available",
                    3,
                    "Application is available for enforcement (install or uninstall based on resolved state). Content may/may not have been downloaded.",
                    0,
                    string.Empty,
                    DateTimeOffset.UtcNow,
                    null,
                    false,
                    false,
                    false)
            ],
            []);

        var service = new FakeMecmClientService
        {
            ApplicationsSnapshots = [snapshot]
        };
        var viewModel = CreateViewModel(service, "applications");
        await viewModel.InitializeAsync(CancellationToken.None);
        viewModel.UpdateSelectedApplications(viewModel.VisibleApplications.ToArray());

        Assert.False(viewModel.UninstallApplicationsCommand.CanExecute(null));
        Assert.Equal(
            "InstallOnly is not installed.",
            viewModel.UninstallApplicationsCommandTooltip);
    }

    [Fact]
    public async Task UninstallTooltip_DescribesAvailableCommandWhenEnabled()
    {
        var snapshot = new MecmApplicationSnapshot(
            "CLIENT01",
            [
                new MecmApplicationEntry(
                    "App-1",
                    "CommandBacked",
                    "CommandBacked",
                    string.Empty,
                    string.Empty,
                    "1.0",
                    "1",
                    true,
                    false,
                    true,
                    [],
                    "Installed",
                    "Applicable",
                    "Installed",
                    1,
                    "Application is enforced to desired/resolved state.",
                    0,
                    string.Empty,
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow,
                    true,
                    true,
                    false)
            ],
            []);

        var service = new FakeMecmClientService
        {
            ApplicationsSnapshots = [snapshot]
        };
        var viewModel = CreateViewModel(service, "applications");
        await viewModel.InitializeAsync(CancellationToken.None);
        viewModel.UpdateSelectedApplications(viewModel.VisibleApplications.ToArray());

        Assert.True(viewModel.UninstallApplicationsCommand.CanExecute(null));
        Assert.Equal("Uninstall the selected MECM application deployment.", viewModel.UninstallApplicationsCommandTooltip);
    }

    [Fact]
    public async Task InstallApplicationsAsync_DispatchesForAllSelectedRows()
    {
        var service = new FakeMecmClientService
        {
            ApplicationsSnapshots =
            [
                CreateApplicationsSnapshot(("AppA", true, "Install"), ("AppB", true, "Install")),
                CreateApplicationsSnapshot(("AppA", true, "Install"), ("AppB", true, "Install"))
            ]
        };
        var viewModel = CreateViewModel(service, "applications");
        await viewModel.InitializeAsync(CancellationToken.None);
        viewModel.UpdateSelectedApplications(viewModel.VisibleApplications.ToArray());

        await viewModel.InstallApplicationsAsync();

        Assert.Equal(2, service.ApplicationActions.Count);
        Assert.Equal(2, service.ApplicationLoadCalls);
    }

    [Fact]
    public async Task TriggerGlobalApplicationEvaluationAsync_DispatchesExpectedMode()
    {
        var service = new FakeMecmClientService
        {
            ApplicationsSnapshots = [CreateApplicationsSnapshot(("AppA", true, "Install"))]
        };
        var viewModel = CreateViewModel(service, "applications");
        await viewModel.InitializeAsync(CancellationToken.None);

        await viewModel.TriggerGlobalApplicationEvaluationAsync();

        Assert.Equal([MecmApplicationEvaluationMode.GlobalEvaluation], service.ApplicationEvaluations);
    }

    [Fact]
    public async Task InstallSelectedPendingUpdatesAsync_SendsSelectedIds()
    {
        var service = new FakeMecmClientService
        {
            PendingSnapshots =
            [
                new MecmPendingUpdatesSnapshot(
                    "CLIENT01",
                    [
                        new MecmPendingUpdateEntry("ID-1", "Update 1", "Microsoft", string.Empty, "KB1", string.Empty, 1, "ciJobStateAvailable", 0, 0, string.Empty, null),
                        new MecmPendingUpdateEntry("ID-2", "Update 2", "Microsoft", string.Empty, "KB2", string.Empty, 1, "ciJobStateAvailable", 0, 0, string.Empty, null)
                    ],
                    []),
                new MecmPendingUpdatesSnapshot("CLIENT01", [], [])
            ]
        };
        var viewModel = CreateViewModel(service, "updates-pending");
        await viewModel.InitializeAsync(CancellationToken.None);
        viewModel.UpdateSelectedPendingUpdates(viewModel.VisiblePendingUpdates.ToArray());

        await viewModel.InstallSelectedPendingUpdatesAsync();

        var request = Assert.Single(service.UpdateInstallRequests);
        Assert.Equal(MecmUpdateInstallMode.Selected, request.Mode);
        Assert.Equal(["ID-1", "ID-2"], request.SelectedUpdateIds);
    }

    [Fact]
    public async Task AllUpdatesFilter_SupportsMissingOnlyAndHideDuplicates()
    {
        var service = new FakeMecmClientService
        {
            AllUpdatesSnapshots =
            [
                new MecmAllUpdatesSnapshot(
                    "CLIENT01",
                    [
                        new MecmAllUpdateEntry("A-1", "Update A", "KB1", "MS1", "en-US", 1, DateTimeOffset.UtcNow, 1, "Missing", "Windows"),
                        new MecmAllUpdateEntry("A-2", "Update A", "KB1", "MS1", "en-US", 2, DateTimeOffset.UtcNow, 1, "Missing", "Windows"),
                        new MecmAllUpdateEntry("B-1", "Update B", "KB2", "MS2", "en-US", 1, DateTimeOffset.UtcNow, 1, "Installed", "Windows")
                    ],
                    [])
            ]
        };
        var viewModel = CreateViewModel(service, "updates-all");
        await viewModel.InitializeAsync(CancellationToken.None);

        Assert.Single(viewModel.VisibleAllUpdates);

        viewModel.ShowMissingUpdatesOnly = false;
        viewModel.HideDuplicateUpdates = false;

        Assert.Equal(3, viewModel.VisibleAllUpdates.Count);
    }

    [Fact]
    public async Task SuccessfulRefresh_ForwardsStatusToHostLog()
    {
        var logSink = new FakeHostStatusLogSink();
        var service = new FakeMecmClientService
        {
            OverviewSnapshots = [CreateOverviewSnapshot()]
        };
        var viewModel = CreateViewModel(service, null, logSink);

        await viewModel.InitializeAsync(CancellationToken.None);

        Assert.Contains(logSink.Messages, message => message.Contains("[MECM] Loaded MECM overview", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RequestMachinePolicyAsync_DispatchesOverviewActionAndReloadsOverview()
    {
        var service = new FakeMecmClientService
        {
            OverviewSnapshots =
            [
                CreateOverviewSnapshot(),
                CreateOverviewSnapshot()
            ]
        };
        var viewModel = CreateViewModel(service);
        await viewModel.InitializeAsync(CancellationToken.None);

        await viewModel.RequestMachinePolicyAsync();

        Assert.Equal([MecmOverviewAction.RequestMachinePolicy], service.OverviewActions);
        Assert.Equal(2, service.OverviewLoadCalls);
    }

    [Fact]
    public async Task RepairClientAsync_DispatchesOverviewActionWithoutReloadingOverview()
    {
        var service = new FakeMecmClientService
        {
            OverviewSnapshots = [CreateOverviewSnapshot()]
        };
        var viewModel = CreateViewModel(service);
        await viewModel.InitializeAsync(CancellationToken.None);

        await viewModel.RepairClientAsync();

        Assert.Equal([MecmOverviewAction.RepairClient], service.OverviewActions);
        Assert.Equal(1, service.OverviewLoadCalls);
    }

    private static MecmAgentViewModel CreateViewModel(FakeMecmClientService service, string? initialTarget = null, FakeHostStatusLogSink? logSink = null)
    {
        logSink ??= new FakeHostStatusLogSink();
        var services = new ServiceCollection()
            .AddSingleton<ITargetHostService>(new FakeTargetHostService("CLIENT01"))
            .AddSingleton<IMecmClientService>(service)
            .AddSingleton<IHostStatusLogSink>(logSink)
            .BuildServiceProvider();

        return new MecmAgentViewModel(new FakePluginContext(services), initialTarget);
    }

    private static MecmApplicationSnapshot CreateApplicationsSnapshot(params (string name, bool userUi, string action)[] rows)
    {
        return new MecmApplicationSnapshot(
            "CLIENT01",
            rows.Select((row, index) => new MecmApplicationEntry(
                $"App-{index}",
                row.name,
                row.name,
                string.Empty,
                string.Empty,
                "1.0",
                "1",
                row.userUi,
                false,
                true,
                [row.action],
                "Installed",
                "Applicable",
                "Installed",
                1,
                "Application is enforced to desired/resolved state.",
                0,
                string.Empty,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                string.Equals(row.action, "Install", StringComparison.OrdinalIgnoreCase),
                string.Equals(row.action, "Uninstall", StringComparison.OrdinalIgnoreCase),
                false)).ToArray(),
            []);
    }

    private static MecmOverviewSnapshot CreateOverviewSnapshot()
    {
        return new MecmOverviewSnapshot(
            "CLIENT01",
            "5.00.9128.1005",
            "PRI",
            "mp01.contoso.example",
            "No",
            "Active",
            [new MecmOverviewActivityEntry("Heartbeat Discovery", "Reported", "Green", DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddHours(-1), "Discovery data was reported.")],
            [new MecmCoManagementWorkloadEntry("Compliance Policies", "Intune", "Green", "Workload is managed by Intune.")],
            [new MecmClientComponentEntry("Software Updates", "UpdatesAgent", "5.00.9128.1005", true, "Green", "Component is enabled.")],
            [new MecmClientServiceEntry("CcmExec", "SMS Agent Host", "Running", "Auto", "Green", "Core MECM agent service.")],
            [new MecmHealthCheckEntry("WMI", "Healthy", "Green", "SMS_Client is reachable.")],
            []);
    }

    private sealed class FakePluginContext(IServiceProvider services) : IPluginContext
    {
        public Microsoft.Extensions.Logging.ILogger Logger { get; } = NullLogger.Instance;
        public IServiceProvider Services { get; } = services;
        public string EnvironmentName { get; } = "Test";
        public IReadOnlyDictionary<string, string> Settings { get; } = new Dictionary<string, string>();
    }

    private sealed class FakeTargetHostService(string currentHost) : ITargetHostService
    {
        private long _version = 1;
        private CancellationTokenSource _selectionCancellationTokenSource = new();

        public string CurrentHost { get; private set; } = currentHost;
        public event EventHandler<string>? HostChanged;

        public HostSelection CaptureSelection() => new(CurrentHost, _version, _selectionCancellationTokenSource.Token);
        public bool IsCurrent(HostSelection selection) => selection.Version == _version && string.Equals(selection.Host, CurrentHost, StringComparison.OrdinalIgnoreCase);

        public void SetCurrentHost(string host)
        {
            if (!string.Equals(CurrentHost, host, StringComparison.OrdinalIgnoreCase))
            {
                _selectionCancellationTokenSource.Cancel();
                _selectionCancellationTokenSource.Dispose();
                _selectionCancellationTokenSource = new CancellationTokenSource();
                _version++;
            }

            CurrentHost = host;
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

    private sealed class FakeMecmClientService : IMecmClientService
    {
        public List<MecmOverviewSnapshot> OverviewSnapshots { get; set; } = [];
        public List<MecmApplicationSnapshot> ApplicationsSnapshots { get; set; } = [];
        public List<MecmPendingUpdatesSnapshot> PendingSnapshots { get; set; } = [];
        public List<MecmAllUpdatesSnapshot> AllUpdatesSnapshots { get; set; } = [];
        public List<MecmPackagesSnapshot> PackageSnapshots { get; set; } = [];
        public List<MecmBaselinesSnapshot> BaselineSnapshots { get; set; } = [];
        public List<MecmBaselineDetails> BaselineDetailsSnapshots { get; set; } = [];
        public List<MecmOverviewAction> OverviewActions { get; } = [];
        public List<(string ApplicationId, MecmApplicationAction Action)> ApplicationActions { get; } = [];
        public List<MecmApplicationEvaluationMode> ApplicationEvaluations { get; } = [];
        public List<MecmUpdateInstallRequest> UpdateInstallRequests { get; } = [];
        public List<string> PackageActions { get; } = [];
        public List<(string Name, string Version, bool IsMachineTarget, bool Enforce)> BaselineEvaluations { get; } = [];
        public int OverviewLoadCalls { get; private set; }
        public int ApplicationLoadCalls { get; private set; }

        public ValueTask<MecmOverviewSnapshot> GetOverviewAsync(string host, CancellationToken cancellationToken)
        {
            OverviewLoadCalls++;
            return ValueTask.FromResult(OverviewSnapshots.Count > 0
                ? OverviewSnapshots.PopFront()
                : new MecmOverviewSnapshot(host, "Unknown", "Unknown", "Unknown", "Unknown", "Unknown", [], [], [], [], [], []));
        }

        public ValueTask<DeviceActionResult> ExecuteOverviewActionAsync(string host, MecmOverviewAction action, CancellationToken cancellationToken)
        {
            OverviewActions.Add(action);
            return ValueTask.FromResult(DeviceActionResult.Ok($"Queued {action}."));
        }

        public ValueTask<MecmApplicationSnapshot> GetApplicationsAsync(string host, CancellationToken cancellationToken)
        {
            ApplicationLoadCalls++;
            return ValueTask.FromResult(ApplicationsSnapshots.Count > 0 ? ApplicationsSnapshots.PopFront() : new MecmApplicationSnapshot(host, [], []));
        }

        public ValueTask<DeviceActionResult> ExecuteApplicationActionAsync(string host, string applicationId, string revision, bool isMachineTarget, MecmApplicationAction action, CancellationToken cancellationToken)
        {
            ApplicationActions.Add((applicationId, action));
            return ValueTask.FromResult(DeviceActionResult.Ok($"Queued {action} for {applicationId}."));
        }

        public ValueTask<DeviceActionResult> TriggerApplicationEvaluationAsync(string host, MecmApplicationEvaluationMode mode, CancellationToken cancellationToken)
        {
            ApplicationEvaluations.Add(mode);
            return ValueTask.FromResult(DeviceActionResult.Ok($"Queued {mode}."));
        }

        public ValueTask<MecmPendingUpdatesSnapshot> GetPendingUpdatesAsync(string host, CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(PendingSnapshots.Count > 0 ? PendingSnapshots.PopFront() : new MecmPendingUpdatesSnapshot(host, [], []));
        }

        public ValueTask<MecmAllUpdatesSnapshot> GetAllUpdatesAsync(string host, CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(AllUpdatesSnapshots.Count > 0 ? AllUpdatesSnapshots.PopFront() : new MecmAllUpdatesSnapshot(host, [], []));
        }

        public ValueTask<DeviceActionResult> InstallUpdatesAsync(string host, MecmUpdateInstallRequest request, CancellationToken cancellationToken)
        {
            UpdateInstallRequests.Add(request);
            return ValueTask.FromResult(DeviceActionResult.Ok($"Queued {request.Mode}."));
        }

        public ValueTask<MecmPackagesSnapshot> GetPackagesAsync(string host, CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(PackageSnapshots.Count > 0 ? PackageSnapshots.PopFront() : new MecmPackagesSnapshot(host, [], []));
        }

        public ValueTask<DeviceActionResult> ExecutePackageAsync(string host, string advertisementId, CancellationToken cancellationToken)
        {
            PackageActions.Add(advertisementId);
            return ValueTask.FromResult(DeviceActionResult.Ok($"Queued package {advertisementId}."));
        }

        public ValueTask<MecmBaselinesSnapshot> GetBaselinesAsync(string host, CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(BaselineSnapshots.Count > 0 ? BaselineSnapshots.PopFront() : new MecmBaselinesSnapshot(host, [], []));
        }

        public ValueTask<MecmBaselineDetails> GetBaselineDetailsAsync(string host, string baselineName, string version, bool isMachineTarget, CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(BaselineDetailsSnapshots.Count > 0
                ? BaselineDetailsSnapshots.PopFront()
                : new MecmBaselineDetails(baselineName, baselineName, version, isMachineTarget, [], []));
        }

        public ValueTask<DeviceActionResult> TriggerBaselineEvaluationAsync(string host, string baselineName, string version, bool isMachineTarget, bool enforce, CancellationToken cancellationToken)
        {
            BaselineEvaluations.Add((baselineName, version, isMachineTarget, enforce));
            return ValueTask.FromResult(DeviceActionResult.Ok($"Queued baseline {baselineName}."));
        }
    }
}

internal static class ListExtensions
{
    public static T PopFront<T>(this List<T> items)
    {
        var value = items[0];
        items.RemoveAt(0);
        return value;
    }
}
