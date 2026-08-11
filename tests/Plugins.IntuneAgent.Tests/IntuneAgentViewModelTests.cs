using System.Collections.Generic;
using System.ComponentModel;
using System.Reflection;
using WindowsClientCenter.Intune.Services.Contracts;
using WindowsClientCenter.Intune.Services.Models;
using WindowsClientCenter.Plugin.Abstractions.Contracts;
using WindowsClientCenter.Plugins.IntuneAgent.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace WindowsClientCenter.Tests.Plugins.IntuneAgent;

public sealed class IntuneAgentViewModelTests
{
    [Theory]
    [InlineData(null, 0)]
    [InlineData("overview", 0)]
    [InlineData("local-diagnostics", 1)]
    [InlineData("enrollment", 2)]
    [InlineData("mdm-events", 3)]
    [InlineData("logs", 3)]
    [InlineData("ime-applications", 4)]
    [InlineData("ime-logs", 5)]
    [InlineData("local-actions", 6)]
    [InlineData("policy-result", 7)]
    [InlineData("cloud", 8)]
    public void MapNavigationTargetToSectionIndex_ReturnsExpectedValue(string? target, int expected)
    {
        Assert.Equal(expected, IntuneAgentViewModel.MapNavigationTargetToSectionIndex(target));
    }

    [Fact]
    public async Task RefreshCloudAsync_WhenSignedOut_KeepsCloudSyncDisabled()
    {
        var targetHostService = new FakeTargetHostService("CLIENT01");
        var cloudService = new FakeCloudManagedDeviceService();
        var services = BuildServices(
            targetHostService,
            new FakeDiagnosticsService(),
            new FakeEnrollmentService(),
            new FakeLocalIntuneActionService(),
            new NullAuthService(),
            cloudService,
            new FakeHostStatusLogSink());
        var viewModel = new IntuneAgentViewModel(new FakePluginContext(services), "cloud");

        await viewModel.RefreshCloudAsync();

        Assert.Null(viewModel.AuthSession);
        Assert.Null(viewModel.CloudDevice);
        Assert.False(viewModel.CanTriggerCloudSync);
        Assert.Equal("Cloud sign-in required.", viewModel.CloudStatus);
        Assert.Equal(0, cloudService.LookupCalls);
    }

    [Fact]
    public async Task RefreshCloudAsync_WhenCloudServicesMissing_ShowsWarningAndDisablesControls()
    {
        var services = new ServiceCollection()
            .AddSingleton<ITargetHostService>(new FakeTargetHostService("CLIENT01"))
            .AddSingleton<ILocalIntuneDiagnosticsService>(new FakeDiagnosticsService())
            .AddSingleton<ILocalIntuneEnrollmentService>(new FakeEnrollmentService())
            .AddSingleton<ILocalIntuneActionService>(new FakeLocalIntuneActionService())
            .AddSingleton<IHostStatusLogSink>(new FakeHostStatusLogSink())
            .AddSingleton<Microsoft.Extensions.Logging.ILoggerFactory>(NullLoggerFactory.Instance)
            .BuildServiceProvider();

        var viewModel = new IntuneAgentViewModel(new FakePluginContext(services), "cloud");
        await viewModel.RefreshCloudAsync();

        Assert.False(viewModel.IsCloudConfigured);
        Assert.False(viewModel.IsCloudControlsEnabled);
        Assert.Contains("Cloud features are disabled", viewModel.CloudConfigurationWarning, StringComparison.Ordinal);
        Assert.Equal(viewModel.CloudConfigurationWarning, viewModel.CloudStatus);
    }

    [Fact]
    public async Task RefreshCloudAsync_TreatsExpiredSessionAsSignedOut()
    {
        var targetHostService = new FakeTargetHostService("CLIENT01");
        var cloudService = new FakeCloudManagedDeviceService();
        var services = BuildServices(
            targetHostService,
            new FakeDiagnosticsService(),
            new FakeEnrollmentService(),
            new FakeLocalIntuneActionService(),
            new ExpiredAuthService(),
            cloudService,
            new FakeHostStatusLogSink());
        var viewModel = new IntuneAgentViewModel(new FakePluginContext(services), "cloud");

        await viewModel.RefreshCloudAsync();

        Assert.Null(viewModel.AuthSession);
        Assert.Null(viewModel.CloudDevice);
        Assert.False(viewModel.CanTriggerCloudSync);
        Assert.Equal(0, cloudService.LookupCalls);
    }

    [Fact]
    public async Task HostChange_RefreshesOverviewAndPreservesAuthenticatedSession()
    {
        var targetHostService = new FakeTargetHostService("CLIENT01");
        var diagnosticsService = new FakeDiagnosticsService();
        var enrollmentService = new FakeEnrollmentService();
        var authService = new StableAuthService();
        var cloudService = new FakeCloudManagedDeviceService();
        var services = BuildServices(
            targetHostService,
            diagnosticsService,
            enrollmentService,
            new FakeLocalIntuneActionService(),
            authService,
            cloudService,
            new FakeHostStatusLogSink());
        var viewModel = new IntuneAgentViewModel(new FakePluginContext(services), "overview");

        await viewModel.SignInAsync();
        await viewModel.InitializeAsync(CancellationToken.None);

        targetHostService.SetCurrentHost("CLIENT02");
        await WaitUntilAsync(() => diagnosticsService.SnapshotCalls >= 2 && cloudService.LookupCalls >= 2);

        Assert.NotNull(viewModel.AuthSession);
        Assert.Equal("tester@contoso.com", viewModel.AuthSession!.UserPrincipalName);
        Assert.Equal("CLIENT02", viewModel.CurrentHost);
        Assert.Equal("CLIENT02", viewModel.CloudDevice!.DeviceName);
        Assert.True(enrollmentService.StatusCalls >= 2);
    }

    [Fact]
    public async Task FixEnrollmentUrlsAsync_RepairsUrlsAndRefreshesEnrollmentStatus()
    {
        var targetHostService = new FakeTargetHostService("CLIENT01");
        var enrollmentService = new FakeEnrollmentService();
        var services = BuildServices(
            targetHostService,
            new FakeDiagnosticsService(),
            enrollmentService,
            new FakeLocalIntuneActionService(),
            new NullAuthService(),
            new FakeCloudManagedDeviceService(),
            new FakeHostStatusLogSink());
        var viewModel = new IntuneAgentViewModel(new FakePluginContext(services), "enrollment");

        await viewModel.InitializeAsync(CancellationToken.None);
        Assert.True(viewModel.CanFixEnrollmentUrls);

        await viewModel.FixEnrollmentUrlsAsync();

        Assert.Equal(1, enrollmentService.FixEnrollmentUrlsCalls);
        Assert.False(viewModel.CanFixEnrollmentUrls);
        Assert.True(viewModel.EnrollmentStatus?.EnrollmentUrls.AreExpected);
    }

    [Fact]
    public async Task ReenrollConfirmationInput_RaisesCanExecutePropertyChanged_AndEnablesExecution()
    {
        var services = BuildServices(
            new FakeTargetHostService("CLIENT01"),
            new FakeDiagnosticsService(),
            new FakeEnrollmentService(),
            new FakeLocalIntuneActionService(),
            new NullAuthService(),
            new FakeCloudManagedDeviceService(),
            new FakeHostStatusLogSink());
        var viewModel = new IntuneAgentViewModel(new FakePluginContext(services), "enrollment");
        var changedProperties = new List<string>();
        viewModel.PropertyChanged += (_, args) =>
        {
            if (!string.IsNullOrWhiteSpace(args.PropertyName))
            {
                changedProperties.Add(args.PropertyName);
            }
        };

        await viewModel.PreviewReenrollAsync();
        changedProperties.Clear();

        viewModel.ConfirmReenrollInput = "REENROLL CLIENT01";

        Assert.True(viewModel.CanExecuteReenroll);
        Assert.True(viewModel.ExecuteReenrollCommand.CanExecute(null));
        Assert.Contains(nameof(IntuneAgentViewModel.CanExecuteReenroll), changedProperties);
    }

    [Fact]
    public void BuildExternalHelperLauncherScript_KeepsWindowOpen_AndLeavesRemoteSessionAvailable()
    {
        var method = typeof(IntuneAgentViewModel).GetMethod(
            "BuildExternalHelperLauncherScript",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("BuildExternalHelperLauncherScript not found.");

        var script = Assert.IsType<string>(method.Invoke(null, ["CLIENT01", @"C:\Temp\helper.ps1", "Autopilot Diagnostics (Community)"]));

        Assert.Contains("powershell window remains open", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("New-PSSession -ComputerName $hostName", script, StringComparison.Ordinal);
        Assert.Contains("Invoke-Command -Session $session -FilePath $helperScriptPath", script, StringComparison.Ordinal);
        Assert.Contains("Enter-PSSession -Session $session", script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoadMdmEventsAsync_DefaultFilters_ShowWarningsAndErrorsOnly()
    {
        var services = BuildServices(
            new FakeTargetHostService("CLIENT01"),
            new FakeDiagnosticsService(),
            new FakeEnrollmentService(),
            new FakeLocalIntuneActionService(),
            new NullAuthService(),
            new FakeCloudManagedDeviceService(),
            new FakeHostStatusLogSink());
        var viewModel = new IntuneAgentViewModel(new FakePluginContext(services), "mdm-events");

        await viewModel.LoadMdmEventsAsync();

        Assert.Equal(149, viewModel.MdmEvents.Count);
        Assert.DoesNotContain(viewModel.MdmEvents, item => item.Severity == MdmEventSeverity.Information);
    }

    [Fact]
    public async Task LoadMdmEventsAsync_EventIdFilter_ReducesVisibleEntries()
    {
        var services = BuildServices(
            new FakeTargetHostService("CLIENT01"),
            new FakeDiagnosticsService(),
            new FakeEnrollmentService(),
            new FakeLocalIntuneActionService(),
            new NullAuthService(),
            new FakeCloudManagedDeviceService(),
            new FakeHostStatusLogSink());
        var viewModel = new IntuneAgentViewModel(new FakePluginContext(services), "mdm-events");

        await viewModel.LoadMdmEventsAsync();
        viewModel.MdmEventIdFilter = "404";

        var entry = Assert.Single(viewModel.MdmEvents);
        Assert.Equal(404, entry.Id);
    }

    [Fact]
    public async Task LoadMdmEventsAsync_UsesConfiguredLoadCount()
    {
        var diagnosticsService = new FakeDiagnosticsService();
        var services = BuildServices(
            new FakeTargetHostService("CLIENT01"),
            diagnosticsService,
            new FakeEnrollmentService(),
            new FakeLocalIntuneActionService(),
            new NullAuthService(),
            new FakeCloudManagedDeviceService(),
            new FakeHostStatusLogSink());
        var viewModel = new IntuneAgentViewModel(new FakePluginContext(services), "mdm-events")
        {
            MdmEventLoadCount = 300
        };

        await viewModel.LoadMdmEventsAsync();

        Assert.Equal(300, diagnosticsService.LastMdmEventRequestCount);
    }

    [Fact]
    public async Task LoadMoreMdmEventsAsync_IncreasesRequestedEventCount()
    {
        var diagnosticsService = new FakeDiagnosticsService();
        var services = BuildServices(
            new FakeTargetHostService("CLIENT01"),
            diagnosticsService,
            new FakeEnrollmentService(),
            new FakeLocalIntuneActionService(),
            new NullAuthService(),
            new FakeCloudManagedDeviceService(),
            new FakeHostStatusLogSink());
        var viewModel = new IntuneAgentViewModel(new FakePluginContext(services), "mdm-events")
        {
            MdmEventLoadCount = 100
        };

        await viewModel.LoadMdmEventsAsync();
        await viewModel.LoadMoreMdmEventsAsync();

        Assert.Equal(200, diagnosticsService.LastMdmEventRequestCount);
    }

    [Fact]
    public async Task LoadMoreMdmEventsAsync_StopsWhenNoMoreEntriesExist()
    {
        var diagnosticsService = new FakeDiagnosticsService { TotalMdmEvents = 180 };
        var services = BuildServices(
            new FakeTargetHostService("CLIENT01"),
            diagnosticsService,
            new FakeEnrollmentService(),
            new FakeLocalIntuneActionService(),
            new NullAuthService(),
            new FakeCloudManagedDeviceService(),
            new FakeHostStatusLogSink());
        var viewModel = new IntuneAgentViewModel(new FakePluginContext(services), "mdm-events")
        {
            MdmEventLoadCount = 100
        };

        await viewModel.LoadMdmEventsAsync();
        await viewModel.LoadMoreMdmEventsAsync();
        await viewModel.LoadMoreMdmEventsAsync();

        Assert.False(viewModel.CanLoadMoreMdmEvents);
        Assert.Equal(179, viewModel.MdmEvents.Count);
    }

    [Fact]
    public async Task LoadImeLogTimelineAsync_PopulatesTimelineEntries()
    {
        var services = BuildServices(
            new FakeTargetHostService("CLIENT01"),
            new FakeDiagnosticsService(),
            new FakeEnrollmentService(),
            new FakeLocalIntuneActionService(),
            new NullAuthService(),
            new FakeCloudManagedDeviceService(),
            new FakeHostStatusLogSink());
        var viewModel = new IntuneAgentViewModel(new FakePluginContext(services), "ime-logs");

        await viewModel.LoadImeLogTimelineAsync();

        var entry = Assert.Single(viewModel.ImeTimelineEntries);
        Assert.True(entry.IsPolicyPayload);
        Assert.Equal("AppWorkload.log", entry.SourceFile);
        Assert.Equal("Policy Sync", entry.Flow);
        var summary = Assert.Single(viewModel.ImeFlowSummaries);
        Assert.Equal("Policy Sync", summary.FlowDisplay);
        Assert.Equal("App 11111111-1111-1111-1111-111111111111", summary.EntityDisplay);
        Assert.Equal("In Progress", summary.Result);
    }

    [Fact]
    public async Task RefreshDiagnosticsAsync_UsesGlobalVerboseOperationsSetting()
    {
        var hostStatus = new FakeHostStatusLogSink();
        var services = BuildServices(
            new FakeTargetHostService("CLIENT01"),
            new FakeDiagnosticsService(),
            new FakeEnrollmentService(),
            new FakeLocalIntuneActionService(),
            new NullAuthService(),
            new FakeCloudManagedDeviceService(),
            hostStatus);
        var viewModel = new IntuneAgentViewModel(
            new FakePluginContext(
                services,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["VerboseOperations"] = "true"
                }),
            "overview");

        await viewModel.RefreshDiagnosticsAsync();

        Assert.Contains(hostStatus.Entries, message => message.Contains("[Intune Agent][Verbose]", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RefreshImeLogTimelineInBackgroundAsync_SkipsReloadWhenFingerprintIsUnchanged()
    {
        var localActionService = new FakeLocalIntuneActionService
        {
            TimelineFingerprint = "same-fingerprint"
        };
        var services = BuildServices(
            new FakeTargetHostService("CLIENT01"),
            new FakeDiagnosticsService(),
            new FakeEnrollmentService(),
            localActionService,
            new NullAuthService(),
            new FakeCloudManagedDeviceService(),
            new FakeHostStatusLogSink());
        var viewModel = new IntuneAgentViewModel(new FakePluginContext(services), "ime-logs");

        await viewModel.LoadImeLogTimelineAsync();
        await viewModel.RefreshImeLogTimelineInBackgroundAsync();

        Assert.Equal(1, localActionService.GetImeLogTimelineCalls);
        Assert.Equal(1, localActionService.GetImeLogTimelineFingerprintCalls);
    }

    [Fact]
    public async Task ToggleImeRelatedHighlightForSelectedEntry_HighlightsMatchingTimelineRows()
    {
        var localActionService = new FakeLocalIntuneActionService();
        localActionService.TimelineEntries =
        [
            new ImeLogTimelineEntry(
                DateTimeOffset.UtcNow.AddMinutes(-1),
                "Information",
                "AppWorkload",
                "Get policies = [{\"Id\":\"sample\"}]",
                "AppWorkload.log",
                42,
                "<![LOG[Get policies = [{\"Id\":\"sample\"}]]LOG]!>",
                true,
                "[{\"Id\":\"sample\"}]",
                "Policy Sync",
                "policy_sync",
                "Fetch/refresh assignment policy",
                "App 11111111-1111-1111-1111-111111111111",
                "App",
                "11111111-1111-1111-1111-111111111111",
                "policy-01",
                "session-01",
                string.Empty,
                string.Empty),
            new ImeLogTimelineEntry(
                DateTimeOffset.UtcNow,
                "Warning",
                "AppWorkload",
                "App with id 11111111-1111-1111-1111-111111111111 enforcement failed with error code 0x87D300C9.",
                "AppWorkload.log",
                43,
                "<![LOG[App with id 11111111-1111-1111-1111-111111111111 enforcement failed with error code 0x87D300C9.]LOG]!>",
                false,
                string.Empty,
                "Execution",
                "execution",
                "Applicability/enforcement execution",
                "App 11111111-1111-1111-1111-111111111111",
                "App",
                "11111111-1111-1111-1111-111111111111",
                "policy-01",
                "session-01",
                string.Empty,
                "0x87D300C9")
        ];

        var services = BuildServices(
            new FakeTargetHostService("CLIENT01"),
            new FakeDiagnosticsService(),
            new FakeEnrollmentService(),
            localActionService,
            new NullAuthService(),
            new FakeCloudManagedDeviceService(),
            new FakeHostStatusLogSink());
        var viewModel = new IntuneAgentViewModel(new FakePluginContext(services), "ime-logs");

        await viewModel.LoadImeLogTimelineAsync();
        viewModel.SelectedImeLogEntry = viewModel.ImeTimelineEntries.Last();

        viewModel.ToggleImeRelatedHighlightForSelectedEntry();

        Assert.False(viewModel.ImeTimelineEntries[0].IsRelatedHighlight);
        Assert.True(viewModel.ImeTimelineEntries[1].IsRelatedHighlight);

        viewModel.ToggleImeRelatedHighlightForSelectedEntry();

        Assert.All(viewModel.ImeTimelineEntries, entry => Assert.False(entry.IsRelatedHighlight));
    }

    [Fact]
    public async Task ImeTimelineFilters_FilterByFlowAndComponentDirectly()
    {
        var localActionService = new FakeLocalIntuneActionService();
        localActionService.TimelineEntries =
        [
            new ImeLogTimelineEntry(
                DateTimeOffset.UtcNow.AddMinutes(-2),
                "Information",
                "AppWorkload",
                "Content download started.",
                "AppWorkload.log",
                40,
                "<![LOG[Content download started.]LOG]!>",
                false,
                string.Empty,
                "Download",
                "download_prepare",
                "Prepare content for enforcement",
                "App 11111111-1111-1111-1111-111111111111",
                "App",
                "11111111-1111-1111-1111-111111111111",
                "policy-01",
                "session-01",
                string.Empty,
                string.Empty),
            new ImeLogTimelineEntry(
                DateTimeOffset.UtcNow.AddMinutes(-1),
                "Information",
                "StatusService",
                "Status service check-in sent successfully.",
                "IntuneManagementExtension.log",
                41,
                "<![LOG[Status service check-in sent successfully.]LOG]!>",
                false,
                string.Empty,
                "Status Service",
                "policy_sync",
                "Gateway/session processing",
                "Session session-01",
                "Session",
                "session-01",
                string.Empty,
                "session-01",
                string.Empty,
                string.Empty)
        ];

        var services = BuildServices(
            new FakeTargetHostService("CLIENT01"),
            new FakeDiagnosticsService(),
            new FakeEnrollmentService(),
            localActionService,
            new NullAuthService(),
            new FakeCloudManagedDeviceService(),
            new FakeHostStatusLogSink());
        var viewModel = new IntuneAgentViewModel(new FakePluginContext(services), "ime-logs");

        await viewModel.LoadImeLogTimelineAsync();

        Assert.Contains("AppWorkload", viewModel.ImeTimelineComponentOptions);
        Assert.Contains("StatusService", viewModel.ImeTimelineComponentOptions);

        viewModel.ImeFlowTypeFilter = "Download";
        Assert.Single(viewModel.ImeTimelineEntries);
        Assert.Equal("Download", viewModel.ImeTimelineEntries[0].FlowDisplay);

        viewModel.ImeFlowTypeFilter = "All";
        viewModel.ImeTimelineComponentFilter = "StatusService";
        Assert.Single(viewModel.ImeTimelineEntries);
        Assert.Equal("StatusService", viewModel.ImeTimelineEntries[0].Component);
    }

    [Fact]
    public async Task ImeApplicationFlowFilter_UsesLoadedTimelineFlows()
    {
        var localActionService = new FakeLocalIntuneActionService();
        localActionService.TimelineEntries =
        [
            new ImeLogTimelineEntry(
                DateTimeOffset.UtcNow.AddMinutes(-2),
                "Information",
                "AppWorkload",
                "Content download started.",
                "AppWorkload.log",
                40,
                "<![LOG[Content download started.]LOG]!>",
                false,
                string.Empty,
                "Download",
                "download_prepare",
                "Prepare content for enforcement",
                "App 11111111-1111-1111-1111-111111111111",
                "App",
                "11111111-1111-1111-1111-111111111111",
                "policy-01",
                "session-01",
                string.Empty,
                string.Empty),
            new ImeLogTimelineEntry(
                DateTimeOffset.UtcNow.AddMinutes(-1),
                "Information",
                "AppWorkload",
                "Installation started.",
                "AppWorkload.log",
                41,
                "<![LOG[Installation started.]LOG]!>",
                false,
                string.Empty,
                "Execution",
                "installation",
                "Execute installation",
                "App 11111111-1111-1111-1111-111111111111",
                "App",
                "11111111-1111-1111-1111-111111111111",
                "policy-01",
                "session-01",
                string.Empty,
                string.Empty)
        ];

        var services = BuildServices(
            new FakeTargetHostService("CLIENT01"),
            new FakeDiagnosticsService(),
            new FakeEnrollmentService(),
            localActionService,
            new NullAuthService(),
            new FakeCloudManagedDeviceService(),
            new FakeHostStatusLogSink());
        var viewModel = new IntuneAgentViewModel(new FakePluginContext(services), "ime-applications");

        await viewModel.LoadImeLogTimelineAsync();
        await viewModel.LoadImeApplicationsAsync();

        Assert.Contains("Download", viewModel.ImeApplicationFlowOptions);
        Assert.Contains("Execution", viewModel.ImeApplicationFlowOptions);

        viewModel.ImeApplicationFlowFilter = "Download";

        var entry = Assert.Single(viewModel.ImeApplications);
        Assert.Equal("11111111-1111-1111-1111-111111111111", entry.AppId);
    }

    [Fact]
    public async Task LoadImeApplicationsAsync_PopulatesApplicationEntries()
    {
        var services = BuildServices(
            new FakeTargetHostService("CLIENT01"),
            new FakeDiagnosticsService(),
            new FakeEnrollmentService(),
            new FakeLocalIntuneActionService(),
            new NullAuthService(),
            new FakeCloudManagedDeviceService(),
            new FakeHostStatusLogSink());
        var viewModel = new IntuneAgentViewModel(new FakePluginContext(services), "ime-logs");

        await viewModel.LoadImeApplicationsAsync();

        var entry = Assert.Single(viewModel.ImeApplications);
        Assert.Equal("Contoso App", entry.AppName);
        Assert.Equal("Failed", entry.InstallStatus);
        Assert.False(entry.IsInstalledForAnyIdentity);
        var identity = Assert.Single(viewModel.SelectedImeApplicationIdentityStatuses);
        Assert.Equal("System", identity.Scope);
    }

    [Fact]
    public async Task LoadImeApplicationsAsync_SetsLoadingStateForImeListOnly()
    {
        var localActionService = new FakeLocalIntuneActionService
        {
            PendingImeApplicationStatuses = new TaskCompletionSource<IReadOnlyList<ImeApplicationStatusEntry>>(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        var services = BuildServices(
            new FakeTargetHostService("CLIENT01"),
            new FakeDiagnosticsService(),
            new FakeEnrollmentService(),
            localActionService,
            new NullAuthService(),
            new FakeCloudManagedDeviceService(),
            new FakeHostStatusLogSink());
        var viewModel = new IntuneAgentViewModel(new FakePluginContext(services), "ime-applications");

        var loadTask = viewModel.LoadImeApplicationsAsync();

        await Task.Delay(20);

        Assert.True(viewModel.IsImeApplicationsLoading);
        Assert.True(viewModel.IsLocalBusy);

        localActionService.PendingImeApplicationStatuses.SetResult(FakeLocalIntuneActionService.BuildImeApplicationStatuses());
        await loadTask;

        Assert.False(viewModel.IsImeApplicationsLoading);
        Assert.False(viewModel.IsLocalBusy);
    }

    [Fact]
    public void ApplyNavigationTarget_ImeApplications_SelectsApplicationsSection()
    {
        var services = BuildServices(
            new FakeTargetHostService("CLIENT01"),
            new FakeDiagnosticsService(),
            new FakeEnrollmentService(),
            new FakeLocalIntuneActionService(),
            new NullAuthService(),
            new FakeCloudManagedDeviceService(),
            new FakeHostStatusLogSink());
        var viewModel = new IntuneAgentViewModel(new FakePluginContext(services), "ime-applications");

        Assert.Equal(4, viewModel.SelectedSectionIndex);
    }

    [Fact]
    public async Task GeneratePolicyResultAsync_UpdatesStatusCountersAndHtmlPath()
    {
        var outputDir = Path.Combine(Path.GetTempPath(), $"icc-policy-generate-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDir);
        var htmlPath = Path.Combine(outputDir, "intune-policy-result-sample.html");
        var jsonPath = Path.Combine(outputDir, "intune-policy-result-sample.json");
        await File.WriteAllTextAsync(htmlPath, "<html><body>sample</body></html>");
        await File.WriteAllTextAsync(jsonPath, "{\"sample\":true}");

        var localActionService = new FakeLocalIntuneActionService
        {
            PolicyResultReport = BuildPolicyResultReport(outputDir, htmlPath, jsonPath)
        };
        var services = BuildServices(
            new FakeTargetHostService("CLIENT01"),
            new FakeDiagnosticsService(),
            new FakeEnrollmentService(),
            localActionService,
            new NullAuthService(),
            new FakeCloudManagedDeviceService(),
            new FakeHostStatusLogSink());
        var viewModel = new IntuneAgentViewModel(new FakePluginContext(services), "policy-result")
        {
            PolicyResultExportDirectory = outputDir
        };

        await viewModel.GeneratePolicyResultAsync();

        Assert.Equal(2, viewModel.PolicyResultTotalCount);
        Assert.Equal(1, viewModel.PolicyResultAppliedCount);
        Assert.Equal(1, viewModel.PolicyResultFailedCount);
        Assert.Equal(htmlPath, viewModel.PolicyReportHtmlPath);
        Assert.True(File.Exists(htmlPath));
        Assert.True(File.Exists(jsonPath));
        Assert.Contains("sample", viewModel.PolicyReportHtmlContent, StringComparison.Ordinal);
        Assert.Contains("\"sample\":true", viewModel.PolicyReportJsonContent, StringComparison.Ordinal);
        Assert.Contains("Generated policy result", viewModel.PolicyResultStatus, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GeneratePolicyResultAsync_ShowsLongRunningMarkerDuringExecution()
    {
        var outputDir = Path.Combine(Path.GetTempPath(), $"icc-policy-loading-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDir);
        var htmlPath = Path.Combine(outputDir, "intune-policy-result-sample.html");
        var jsonPath = Path.Combine(outputDir, "intune-policy-result-sample.json");
        await File.WriteAllTextAsync(htmlPath, "<html><body>sample</body></html>");
        await File.WriteAllTextAsync(jsonPath, "{\"sample\":true}");

        var localActionService = new FakeLocalIntuneActionService
        {
            PolicyResultReport = BuildPolicyResultReport(outputDir, htmlPath, jsonPath),
            PendingGeneratePolicyResult = new TaskCompletionSource<IntunePolicyResultReport>(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        var services = BuildServices(
            new FakeTargetHostService("CLIENT01"),
            new FakeDiagnosticsService(),
            new FakeEnrollmentService(),
            localActionService,
            new NullAuthService(),
            new FakeCloudManagedDeviceService(),
            new FakeHostStatusLogSink());
        var viewModel = new IntuneAgentViewModel(new FakePluginContext(services), "policy-result")
        {
            PolicyResultExportDirectory = outputDir
        };

        var commandTask = viewModel.GeneratePolicyResultAsync();
        await Task.Delay(25);

        Assert.True(viewModel.IsLongRunningLocalAction);
        Assert.Equal("Generating policy result...", viewModel.LongRunningLocalActionLabel);

        localActionService.PendingGeneratePolicyResult.SetResult(localActionService.PolicyResultReport);
        await commandTask;

        Assert.False(viewModel.IsLongRunningLocalAction);
        Assert.Equal(string.Empty, viewModel.LongRunningLocalActionLabel);
    }

    [Fact]
    public async Task GeneratePolicyResultAsync_LogsTimingsWhenVerboseOperationsAreEnabled()
    {
        var outputDir = Path.Combine(Path.GetTempPath(), $"icc-policy-verbose-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDir);
        var htmlPath = Path.Combine(outputDir, "intune-policy-result-sample.html");
        var jsonPath = Path.Combine(outputDir, "intune-policy-result-sample.json");
        await File.WriteAllTextAsync(htmlPath, "<html><body>sample</body></html>");
        await File.WriteAllTextAsync(jsonPath, "{\"sample\":true}");

        var hostStatus = new FakeHostStatusLogSink();
        var localActionService = new FakeLocalIntuneActionService
        {
            PolicyResultReport = BuildPolicyResultReport(
                outputDir,
                htmlPath,
                jsonPath,
                ["Local policy overlay collection completed in 7 ms.", "Policy merge completed in 2 ms."])
        };
        var services = BuildServices(
            new FakeTargetHostService("CLIENT01"),
            new FakeDiagnosticsService(),
            new FakeEnrollmentService(),
            localActionService,
            new NullAuthService(),
            new FakeCloudManagedDeviceService(),
            hostStatus);
        var viewModel = new IntuneAgentViewModel(
            new FakePluginContext(
                services,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["VerboseOperations"] = "true"
                }),
            "policy-result")
        {
            PolicyResultExportDirectory = outputDir
        };

        await viewModel.GeneratePolicyResultAsync();

        Assert.Contains(hostStatus.Entries, message => message.Contains("[Intune Agent][Verbose] Policy result:", StringComparison.Ordinal));
        Assert.Contains(hostStatus.Entries, message => message.Contains("Local policy overlay collection completed in 7 ms.", StringComparison.Ordinal));
        Assert.Contains(hostStatus.Entries, message => message.Contains("Policy merge completed in 2 ms.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ParsePolicyResultAsync_UpdatesStatusCountersAndHtmlPath()
    {
        var outputDir = Path.Combine(Path.GetTempPath(), $"icc-policy-parse-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDir);
        var htmlPath = Path.Combine(outputDir, "intune-policy-result-sample.html");
        var jsonPath = Path.Combine(outputDir, "intune-policy-result-sample.json");
        await File.WriteAllTextAsync(htmlPath, "<html><body>sample</body></html>");
        await File.WriteAllTextAsync(jsonPath, "{\"sample\":true}");

        var localActionService = new FakeLocalIntuneActionService
        {
            PolicyResultReport = BuildPolicyResultReport(outputDir, htmlPath, jsonPath)
        };
        var services = BuildServices(
            new FakeTargetHostService("CLIENT01"),
            new FakeDiagnosticsService(),
            new FakeEnrollmentService(),
            localActionService,
            new NullAuthService(),
            new FakeCloudManagedDeviceService(),
            new FakeHostStatusLogSink());
        var viewModel = new IntuneAgentViewModel(new FakePluginContext(services), "policy-result")
        {
            PolicyResultReportDirectory = outputDir,
            PolicyResultExportDirectory = outputDir
        };

        await viewModel.ParsePolicyResultAsync();

        Assert.Equal(2, viewModel.PolicyResultTotalCount);
        Assert.Equal(1, viewModel.PolicyResultDeviceCount);
        Assert.Equal(1, viewModel.PolicyResultUserCount);
        Assert.Equal(htmlPath, viewModel.PolicyReportHtmlPath);
        Assert.True(File.Exists(htmlPath));
        Assert.True(File.Exists(jsonPath));
        Assert.Contains("sample", viewModel.PolicyReportHtmlContent, StringComparison.Ordinal);
        Assert.Contains("\"sample\":true", viewModel.PolicyReportJsonContent, StringComparison.Ordinal);
        Assert.Contains("Parsed policy result", viewModel.PolicyResultStatus, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExportPolicyResultAsync_AddsExportEvidence()
    {
        var sourceDir = Path.Combine(Path.GetTempPath(), $"icc-policy-export-src-{Guid.NewGuid():N}");
        var targetDir = Path.Combine(Path.GetTempPath(), $"icc-policy-export-dst-{Guid.NewGuid():N}");
        Directory.CreateDirectory(sourceDir);
        Directory.CreateDirectory(targetDir);

        var sourceHtml = Path.Combine(sourceDir, "source-report.html");
        var sourceJson = Path.Combine(sourceDir, "source-report.json");
        await File.WriteAllTextAsync(sourceHtml, "<html><body>source</body></html>");
        await File.WriteAllTextAsync(sourceJson, "{\"source\":true}");

        var services = BuildServices(
            new FakeTargetHostService("CLIENT01"),
            new FakeDiagnosticsService(),
            new FakeEnrollmentService(),
            new FakeLocalIntuneActionService(),
            new NullAuthService(),
            new FakeCloudManagedDeviceService(),
            new FakeHostStatusLogSink());
        var viewModel = new IntuneAgentViewModel(new FakePluginContext(services), "policy-result")
        {
            PolicyReportHtmlPath = sourceHtml,
            PolicyReportJsonPath = sourceJson,
            PolicyResultExportDirectory = targetDir
        };

        await viewModel.ExportPolicyResultAsync();

        Assert.Contains(viewModel.LocalActionEvidence, item => item.Name == "ExportHtmlPath" && File.Exists(item.Value));
        Assert.Contains(viewModel.LocalActionEvidence, item => item.Name == "ExportJsonPath" && File.Exists(item.Value));
    }

    [Fact]
    public async Task ExportPolicyResultAsync_WritesArtifactsFromInMemoryPolicyReportWhenSourceFilesAreGone()
    {
        var sourceDir = Path.Combine(Path.GetTempPath(), $"icc-policy-export-mem-src-{Guid.NewGuid():N}");
        var targetDir = Path.Combine(Path.GetTempPath(), $"icc-policy-export-mem-dst-{Guid.NewGuid():N}");
        Directory.CreateDirectory(sourceDir);
        Directory.CreateDirectory(targetDir);

        var sourceHtml = Path.Combine(sourceDir, "source-report.html");
        var sourceJson = Path.Combine(sourceDir, "source-report.json");

        var services = BuildServices(
            new FakeTargetHostService("CLIENT01"),
            new FakeDiagnosticsService(),
            new FakeEnrollmentService(),
            new FakeLocalIntuneActionService(),
            new NullAuthService(),
            new FakeCloudManagedDeviceService(),
            new FakeHostStatusLogSink());
        var viewModel = new IntuneAgentViewModel(new FakePluginContext(services), "policy-result")
        {
            PolicyReportHtmlPath = sourceHtml,
            PolicyReportJsonPath = sourceJson,
            PolicyReportHtmlContent = "<html><body>in-memory</body></html>",
            PolicyReportJsonContent = "{\"inMemory\":true}",
            PolicyResultExportDirectory = targetDir
        };

        await viewModel.ExportPolicyResultAsync();

        var htmlTarget = Assert.Single(viewModel.LocalActionEvidence, item => item.Name == "ExportHtmlPath").Value;
        var jsonTarget = Assert.Single(viewModel.LocalActionEvidence, item => item.Name == "ExportJsonPath").Value;
        Assert.True(File.Exists(htmlTarget));
        Assert.True(File.Exists(jsonTarget));
        Assert.Contains("in-memory", await File.ReadAllTextAsync(htmlTarget), StringComparison.Ordinal);
        Assert.Contains("\"inMemory\":true", await File.ReadAllTextAsync(jsonTarget), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ActionRetryWin32AllAsync_DoesNotRequestImeRestart()
    {
        var localActionService = new FakeLocalIntuneActionService();
        var services = BuildServices(
            new FakeTargetHostService("CLIENT01"),
            new FakeDiagnosticsService(),
            new FakeEnrollmentService(),
            localActionService,
            new NullAuthService(),
            new FakeCloudManagedDeviceService(),
            new FakeHostStatusLogSink());
        var viewModel = new IntuneAgentViewModel(new FakePluginContext(services), "ime-applications");

        await viewModel.ActionRetryWin32AllAsync();

        Assert.NotNull(localActionService.LastRetryAllRequest);
        Assert.False(localActionService.LastRetryAllRequest!.RestartImeService);
    }

    [Fact]
    public async Task ActionRestartImeAsync_InvokesLocalActionService()
    {
        var localActionService = new FakeLocalIntuneActionService();
        var services = BuildServices(
            new FakeTargetHostService("CLIENT01"),
            new FakeDiagnosticsService(),
            new FakeEnrollmentService(),
            localActionService,
            new NullAuthService(),
            new FakeCloudManagedDeviceService(),
            new FakeHostStatusLogSink());
        var viewModel = new IntuneAgentViewModel(new FakePluginContext(services), "ime-applications");

        await viewModel.ActionRestartImeAsync();

        Assert.Equal(1, localActionService.RestartImeServiceCalls);
    }

    [Fact]
    public async Task SetImeTestModeAsync_TogglesRegistryStateInLocalActionService()
    {
        var localActionService = new FakeLocalIntuneActionService();
        var services = BuildServices(
            new FakeTargetHostService("CLIENT01"),
            new FakeDiagnosticsService(),
            new FakeEnrollmentService(),
            localActionService,
            new NullAuthService(),
            new FakeCloudManagedDeviceService(),
            new FakeHostStatusLogSink());
        var viewModel = new IntuneAgentViewModel(new FakePluginContext(services), "ime-applications");

        await viewModel.SetImeTestModeAsync(true);
        Assert.True(viewModel.IsImeTestModeEnabled);
        Assert.Equal(1, localActionService.SetImeTestModeCalls);

        await viewModel.SetImeTestModeAsync(false);
        Assert.False(viewModel.IsImeTestModeEnabled);
        Assert.Equal(2, localActionService.SetImeTestModeCalls);
    }

    private static ServiceProvider BuildServices(
        ITargetHostService targetHostService,
        ILocalIntuneDiagnosticsService diagnosticsService,
        ILocalIntuneEnrollmentService enrollmentService,
        ILocalIntuneActionService localIntuneActionService,
        IAuthService authService,
        ICloudManagedDeviceService cloudService,
        IHostStatusLogSink logSink)
    {
        var services = new ServiceCollection();
        services.AddSingleton(targetHostService);
        services.AddSingleton(diagnosticsService);
        services.AddSingleton(enrollmentService);
        services.AddSingleton(localIntuneActionService);
        services.AddSingleton(authService);
        services.AddSingleton(cloudService);
        services.AddSingleton(logSink);
        services.AddSingleton<Microsoft.Extensions.Logging.ILoggerFactory>(NullLoggerFactory.Instance);
        return services.BuildServiceProvider();
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        var timeout = DateTimeOffset.UtcNow.AddSeconds(3);
        while (DateTimeOffset.UtcNow < timeout)
        {
            if (predicate())
            {
                return;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException("Condition was not reached in time.");
    }

    private static IntunePolicyResultReport BuildPolicyResultReport(
        string reportDirectory,
        string htmlPath,
        string jsonPath,
        IReadOnlyList<string>? timings = null)
    {
        return new IntunePolicyResultReport(
            "CLIENT01",
            DateTimeOffset.UtcNow,
            reportDirectory,
            Path.Combine(reportDirectory, "MDMDiagReport.xml"),
            Path.Combine(reportDirectory, "MDMDiagReport.html"),
            "Xml",
            new IntunePolicyResultSummary(
                TotalCount: 2,
                AppliedCount: 1,
                FailedCount: 1,
                UnknownCount: 0,
                DeviceCount: 1,
                UserCount: 1,
                UnknownScopeCount: 0),
            [
                new IntunePolicyResultEntry(
                    "Device",
                    "Defender",
                    "AllowRealTimeMonitoring",
                    "./Device/Vendor/MSFT/Policy/Config/Defender/AllowRealTimeMonitoring",
                    "1",
                    "Applied",
                    "0x00000000"),
                new IntunePolicyResultEntry(
                    "User",
                    "Browser",
                    "Homepage",
                    "./User/Vendor/MSFT/Policy/Config/Browser/Homepage",
                    "https://contoso",
                    "Failed",
                    "0x87D1FDE8")
            ],
            htmlPath,
            jsonPath,
            [],
            timings ?? []);
    }

    private sealed class FakePluginContext(IServiceProvider services, IReadOnlyDictionary<string, string>? settings = null) : IPluginContext
    {
        public Microsoft.Extensions.Logging.ILogger Logger => NullLogger.Instance;
        public IServiceProvider Services { get; } = services;
        public string EnvironmentName => "test";
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

    private sealed class FakeDiagnosticsService : ILocalIntuneDiagnosticsService
    {
        public int SnapshotCalls { get; private set; }
        public int LastMdmEventRequestCount { get; private set; }
        public int TotalMdmEvents { get; set; } = 320;

        public ValueTask<LocalIntuneSnapshot> GetSnapshotAsync(string host, CancellationToken cancellationToken)
        {
            SnapshotCalls++;
            return ValueTask.FromResult(new LocalIntuneSnapshot(
                host,
                host,
                DateTimeOffset.UtcNow,
                false,
                "2026-03-20 08:00:00Z",
                "AzureAdJoined : YES",
                "raw dsreg",
                ["AzureAdJoined : YES"],
                [],
                [],
                [],
                [],
                []));
        }

        public async ValueTask<LocalIntuneSnapshotDiagnosticsResult> GetSnapshotDiagnosticsAsync(string host, CancellationToken cancellationToken)
        {
            var snapshot = await GetSnapshotAsync(host, cancellationToken);
            return new LocalIntuneSnapshotDiagnosticsResult(snapshot, ["PowerShell snapshot script completed in 10 ms."]);
        }

        public ValueTask<LocalIntuneSnapshot> GetOverviewCoreSnapshotAsync(string host, CancellationToken cancellationToken)
        {
            return GetSnapshotAsync(host, cancellationToken);
        }

        public ValueTask<PlatformSecuritySnapshot?> GetPlatformSecuritySnapshotAsync(string host, CancellationToken cancellationToken)
        {
            return ValueTask.FromResult<PlatformSecuritySnapshot?>(null);
        }

        public ValueTask<SystemRuntimeSnapshot?> GetSystemRuntimeSnapshotAsync(string host, CancellationToken cancellationToken)
        {
            return ValueTask.FromResult<SystemRuntimeSnapshot?>(null);
        }

        public ValueTask<NetworkConnectivitySnapshot?> GetNetworkConnectivitySnapshotAsync(string host, CancellationToken cancellationToken)
        {
            return ValueTask.FromResult<NetworkConnectivitySnapshot?>(null);
        }

        public ValueTask<PortAuthenticationSnapshot?> GetPortAuthenticationSnapshotAsync(string host, CancellationToken cancellationToken)
        {
            return ValueTask.FromResult<PortAuthenticationSnapshot?>(null);
        }

        public ValueTask<DeliveryOptimizationSnapshot?> GetDeliveryOptimizationSnapshotAsync(string host, CancellationToken cancellationToken)
        {
            return ValueTask.FromResult<DeliveryOptimizationSnapshot?>(new DeliveryOptimizationSnapshot(
                false,
                DateTimeOffset.UtcNow,
                [],
                [],
                [],
                false,
                null,
                null,
                [],
                [],
                [],
                [],
                []));
        }

        public ValueTask<IReadOnlyList<IntuneLogEntry>> GetLogEntriesAsync(string host, string logName, int maxEntries, CancellationToken cancellationToken)
        {
            IReadOnlyList<IntuneLogEntry> entries = [new IntuneLogEntry(logName, DateTimeOffset.UtcNow, 1, "Information", "Test", "Message")];
            return ValueTask.FromResult(entries);
        }

        public ValueTask<IReadOnlyList<MdmEventAnalysisEntry>> GetMdmAdminEventsAsync(string host, int maxEntries, CancellationToken cancellationToken)
        {
            LastMdmEventRequestCount = maxEntries;
            var count = Math.Min(maxEntries, TotalMdmEvents);
            IReadOnlyList<MdmEventAnalysisEntry> entries = Enumerable.Range(0, count)
                .Select(index =>
                {
                    var severity = index switch
                    {
                        0 => MdmEventSeverity.Information,
                        1 => MdmEventSeverity.Error,
                        2 => MdmEventSeverity.Critical,
                        _ => MdmEventSeverity.Warning
                    };

                    var id = index switch
                    {
                        0 => 200,
                        1 => 404,
                        2 => 409,
                        _ => 500 + index
                    };

                    var resultCode = severity switch
                    {
                        MdmEventSeverity.Information => "0x00000000",
                        MdmEventSeverity.Critical => "0x80070005",
                        _ => "0x80070002"
                    };

                    return new MdmEventAnalysisEntry(
                        "Microsoft-Windows-DeviceManagement-Enterprise-Diagnostics-Provider/Admin",
                        DateTimeOffset.UtcNow.AddMinutes(-index),
                        index,
                        id,
                        severity == MdmEventSeverity.Information ? "Information" : "Error",
                        "DeviceManagement-Enterprise-Diagnostics-Provider",
                        severity,
                        severity != MdmEventSeverity.Information,
                        $"Synthetic event {index}",
                        resultCode,
                        severity == MdmEventSeverity.Information ? "Success." : "Synthetic failure.",
                        severity == MdmEventSeverity.Information ? "AllowTelemetry" : $"Policy{index}",
                        severity == MdmEventSeverity.Information ? "System" : $"Area{index}",
                        $"./Device/Vendor/MSFT/Policy/Config/Test/Policy{index}",
                        "11111111-1111-1111-1111-111111111111",
                        "Synthetic recommendation.",
                        $"Synthetic raw message {index}");
                })
                .ToArray();

            return ValueTask.FromResult(entries);
        }

        public ValueTask<string> ExportSnapshotAsync(string host, string outputDirectory, CancellationToken cancellationToken) =>
            ValueTask.FromResult("snapshot.json");

        public ValueTask<string> ExportMdmDiagnosticsAsync(string host, string outputDirectory, CancellationToken cancellationToken) =>
            ValueTask.FromResult("bundle.cab");
    }

    private sealed class FakeEnrollmentService : ILocalIntuneEnrollmentService
    {
        public int StatusCalls { get; private set; }
        public int FixEnrollmentUrlsCalls { get; private set; }
        public bool EnrollmentUrlsFixed { get; private set; }

        public ValueTask<EnrollmentStatus> GetEnrollmentStatusAsync(string host, CancellationToken cancellationToken)
        {
            StatusCalls++;
            return ValueTask.FromResult(new EnrollmentStatus(
                host,
                false,
                true,
                true,
                true,
                "2026-03-20 08:00:00Z",
                "AzureAdJoined : YES",
                ["11111111-1111-1111-1111-111111111111"],
                ["Administrative context confirmed."],
                [],
                [],
                new EnrollmentUrlsStatus(
                    true,
                    true,
                    EnrollmentUrlsFixed,
                    EnrollmentUrlsFixed ? "Enrollment URLs are configured correctly." : "Enrollment URLs differ from the expected Microsoft Intune values.",
                    EnrollmentUrlsFixed ? ["Enrollment URLs match expected values."] : [],
                    EnrollmentUrlsFixed ? [] : ["MdmEnrollmentUrl is missing or differs from the expected Intune discovery endpoint."],
                    EnrollmentUrlsFixed ? EnrollmentUrlTargets.EnrollmentUrl : "https://old.invalid/discovery.svc",
                    EnrollmentUrlsFixed ? EnrollmentUrlTargets.TermsOfUseUrl : "https://old.invalid/tou",
                    EnrollmentUrlsFixed ? EnrollmentUrlTargets.ComplianceUrl : "https://old.invalid/compliance",
                    true),
                true,
                true));
        }

        public ValueTask<DeviceActionResult> TriggerSyncAsync(string host, CancellationToken cancellationToken) =>
            ValueTask.FromResult(DeviceActionResult.Ok($"Triggered sync for {host}."));

        public ValueTask<DeviceActionResult> FixEnrollmentUrlsAsync(string host, CancellationToken cancellationToken)
        {
            FixEnrollmentUrlsCalls++;
            EnrollmentUrlsFixed = true;
            return ValueTask.FromResult(DeviceActionResult.Ok($"Updated enrollment URLs on '{host}'."));
        }

        public ValueTask<EnrollmentRepairPreview> PreviewReenrollAsync(string host, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new EnrollmentRepairPreview(host, true, $"REENROLL {host}", "preview", [], [], []));

        public ValueTask<DeviceActionResult> ExecuteReenrollAsync(string host, bool confirmed, CancellationToken cancellationToken) =>
            ValueTask.FromResult(DeviceActionResult.Ok($"Re-enroll executed for {host}."));
    }

    private sealed class FakeLocalIntuneActionService : ILocalIntuneActionService
    {
        public int GetImeLogTimelineCalls { get; private set; }
        public int GetImeLogTimelineFingerprintCalls { get; private set; }
        public int GetImeLogAnalysisCalls { get; private set; }
        public int RestartImeServiceCalls { get; private set; }
        public int SetImeTestModeCalls { get; private set; }
        public bool ImeTestModeEnabled { get; private set; }
        public TaskCompletionSource<IntunePolicyResultReport>? PendingGeneratePolicyResult { get; set; }
        public TaskCompletionSource<IntunePolicyResultReport>? PendingParsePolicyResult { get; set; }
        public TaskCompletionSource<IReadOnlyList<ImeApplicationStatusEntry>>? PendingImeApplicationStatuses { get; set; }
        public string TimelineFingerprint { get; set; } = "default-fingerprint";
        public Win32RetryAllRequest? LastRetryAllRequest { get; private set; }
        public IntunePolicyResultReport PolicyResultReport { get; set; } = new(
            "CLIENT01",
            DateTimeOffset.UtcNow,
            "C:\\Temp\\Mdm",
            "C:\\Temp\\Mdm\\MDMDiagReport.xml",
            "C:\\Temp\\Mdm\\MDMDiagReport.html",
            "Xml",
            new IntunePolicyResultSummary(0, 0, 0, 0, 0, 0, 0),
            [],
            "C:\\Temp\\Mdm\\intune-policy-result.html",
            "C:\\Temp\\Mdm\\intune-policy-result.json",
            [],
            []);
        public IReadOnlyList<ImeLogTimelineEntry> TimelineEntries { get; set; } =
        [
            new ImeLogTimelineEntry(
                DateTimeOffset.UtcNow,
                "Information",
                "AppWorkload",
                "Get policies = [{\"Id\":\"sample\"}]",
                "AppWorkload.log",
                42,
                "<![LOG[Get policies = [{\"Id\":\"sample\"}]]LOG]!>",
                true,
                "[{\"Id\":\"sample\"}]",
                "Policy Sync",
                "policy_sync",
                "Fetch/refresh assignment policy",
                "App 11111111-1111-1111-1111-111111111111",
                "App",
                "11111111-1111-1111-1111-111111111111",
                "policy-01",
                "session-01",
                string.Empty,
                string.Empty)
        ];

        public ValueTask<LocalIntuneActionResult> MdmSyncNowAsync(string host, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new LocalIntuneActionResult(true, "ok", [], new Dictionary<string, string>()));

        public ValueTask<IReadOnlyList<MdmSyncStatusEntry>> GetMdmSyncStatusAsync(string host, int maxEvents, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<MdmSyncStatusEntry>>([]);

        public ValueTask<string> GetImeLogTimelineFingerprintAsync(string host, string logDirectory, string filePattern, CancellationToken cancellationToken)
        {
            GetImeLogTimelineFingerprintCalls++;
            return ValueTask.FromResult(TimelineFingerprint);
        }

        public ValueTask<ImeLogTimelineSnapshot> GetImeLogTimelineSnapshotAsync(string host, string logDirectory, string filePattern, int maxLines, CancellationToken cancellationToken)
        {
            GetImeLogTimelineCalls++;
            return ValueTask.FromResult(new ImeLogTimelineSnapshot(TimelineFingerprint, TimelineEntries));
        }

        public ValueTask<IReadOnlyList<ImeLogTimelineEntry>> GetImeLogTimelineAsync(string host, string logDirectory, string filePattern, int maxLines, CancellationToken cancellationToken)
        {
            GetImeLogTimelineCalls++;
            return ValueTask.FromResult(TimelineEntries);
        }

        public ValueTask<ImeLogAnalysisResult> GetImeLogAnalysisAsync(string host, string logDirectory, string filePattern, int maxLines, CancellationToken cancellationToken)
        {
            GetImeLogAnalysisCalls++;
            return ValueTask.FromResult(new ImeLogAnalysisResult(TimelineFingerprint, TimelineEntries, BuildImeApplicationStatuses()));
        }

        public ValueTask<IReadOnlyList<ImeApplicationStatusEntry>> GetImeApplicationStatusesAsync(string host, string logDirectory, int maxLines, CancellationToken cancellationToken) =>
            PendingImeApplicationStatuses is null
                ? ValueTask.FromResult(BuildImeApplicationStatuses())
                : new ValueTask<IReadOnlyList<ImeApplicationStatusEntry>>(PendingImeApplicationStatuses.Task);

        public ValueTask<MdmReportParseResult> GenerateMdmDiagnosticsReportAsync(string host, string outputDirectory, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new MdmReportParseResult(outputDirectory, "x.xml", "x.html", 0, 0));

        public ValueTask<MdmReportParseResult> ParseMdmDiagnosticsReportAsync(string host, string reportDirectory, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new MdmReportParseResult(reportDirectory, "x.xml", "x.html", 0, 0));

        public ValueTask<IntunePolicyResultReport> GenerateIntunePolicyResultAsync(string host, string outputDirectory, CancellationToken cancellationToken) =>
            PendingGeneratePolicyResult is null
                ? ValueTask.FromResult(PolicyResultReport)
                : new ValueTask<IntunePolicyResultReport>(PendingGeneratePolicyResult.Task);

        public ValueTask<IntunePolicyResultReport> ParseIntunePolicyResultAsync(string host, string reportDirectory, string outputDirectory, CancellationToken cancellationToken) =>
            PendingParsePolicyResult is null
                ? ValueTask.FromResult(PolicyResultReport)
                : new ValueTask<IntunePolicyResultReport>(PendingParsePolicyResult.Task);

        public ValueTask<LocalIntuneActionResult> ImeSyncAppsAsync(string host, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new LocalIntuneActionResult(true, "ok", [], new Dictionary<string, string>()));

        public ValueTask<LocalIntuneActionResult> ImeSyncComplianceAsync(string host, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new LocalIntuneActionResult(true, "ok", [], new Dictionary<string, string>()));

        public ValueTask<LocalIntuneActionResult> ParseImeAppWorkloadPoliciesAsync(string host, string logDirectory, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new LocalIntuneActionResult(true, "ok", [], new Dictionary<string, string>()));

        public ValueTask<LocalIntuneActionResult> RunImeHealthEvaluationAsync(string host, string taskNameContains, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new LocalIntuneActionResult(true, "ok", [], new Dictionary<string, string>()));

        public ValueTask<LocalIntuneActionResult> RestartImeServiceAsync(string host, CancellationToken cancellationToken)
        {
            RestartImeServiceCalls++;
            return ValueTask.FromResult(new LocalIntuneActionResult(true, "ok", [], new Dictionary<string, string>()));
        }

        public ValueTask<bool> GetImeTestModeEnabledAsync(string host, CancellationToken cancellationToken) =>
            ValueTask.FromResult(ImeTestModeEnabled);

        public ValueTask<LocalIntuneActionResult> SetImeTestModeEnabledAsync(string host, bool enabled, CancellationToken cancellationToken)
        {
            SetImeTestModeCalls++;
            ImeTestModeEnabled = enabled;
            return ValueTask.FromResult(new LocalIntuneActionResult(true, "ok", [], new Dictionary<string, string>()));
        }

        public ValueTask<LocalIntuneActionResult> RetryWin32AppAsync(string host, Win32RetryRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new LocalIntuneActionResult(true, "ok", [], new Dictionary<string, string>()));

        public ValueTask<LocalIntuneActionResult> RetryAllFailedWin32AppsAsync(string host, Win32RetryAllRequest request, CancellationToken cancellationToken)
        {
            LastRetryAllRequest = request;
            return ValueTask.FromResult(new LocalIntuneActionResult(true, "ok", [], new Dictionary<string, string>()));
        }

        public ValueTask<LocalIntuneActionResult> RestartPortAuthenticationServicesAsync(string host, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new LocalIntuneActionResult(true, "ok", [], new Dictionary<string, string>()));

        public ValueTask<LocalIntuneActionResult> RestartPortAuthenticationAdapterAsync(string host, string interfaceName, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new LocalIntuneActionResult(true, "ok", [], new Dictionary<string, string>()));

        public ValueTask<LocalIntuneActionResult> SetPortAuthenticationTracingAsync(string host, PortAuthenticationTracingMode mode, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new LocalIntuneActionResult(true, "ok", [], new Dictionary<string, string>()));

        public ValueTask<LocalIntuneActionResult> SetPortAuthenticationAutoconfigAsync(string host, string interfaceName, bool enabled, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new LocalIntuneActionResult(true, "ok", [], new Dictionary<string, string>()));

        public ValueTask<LocalIntuneActionResult> ReapplyPortAuthenticationProfileAsync(string host, string profileName, string? interfaceName, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new LocalIntuneActionResult(true, "ok", [], new Dictionary<string, string>()));

        public ValueTask<LocalIntuneActionResult> ExportSupportEventLogsAsync(string host, string outputDirectory, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new LocalIntuneActionResult(true, "ok", [], new Dictionary<string, string>()));

        public ValueTask<LocalIntuneActionResult> CreateDiagnosticsBundleAsync(string host, string bundleRoot, string zipPath, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new LocalIntuneActionResult(true, "ok", [], new Dictionary<string, string>()));

        public ValueTask<LocalIntuneActionResult> RunAutopilotDiagnosticsCommunityAsync(
            string host,
            bool allSessions,
            bool showPolicies,
            string moduleVersion,
            int maxOutputLines,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new LocalIntuneActionResult(
                true,
                "Autopilot diagnostics collected.",
                [],
                new Dictionary<string, string>
                {
                    ["moduleVersionRequested"] = moduleVersion,
                    ["outputLineCount"] = "1",
                    ["outputText"] = "AUTOPILOT DIAGNOSTICS"
                }));

        public ValueTask<LocalIntuneActionResult> RunImeQuickStatusAsync(string host, int maxOutputLines, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new LocalIntuneActionResult(
                true,
                "IME quick status collected.",
                [],
                new Dictionary<string, string>
                {
                    ["outputLineCount"] = "3",
                    ["outputText"] = "ServiceName: IntuneManagementExtension"
                }));

        public static IReadOnlyList<ImeApplicationStatusEntry> BuildImeApplicationStatuses()
        {
            return
            [
                new ImeApplicationStatusEntry(
                    "11111111-1111-1111-1111-111111111111",
                    "Contoso App",
                    "Required",
                    "System",
                    "Failed",
                    DateTimeOffset.UtcNow,
                    "0x87D300C9",
                    "AppWorkload.log",
                    "Install failed with synthetic error.",
                    false,
                    [
                        new ImeApplicationIdentityStatusEntry(
                            "00000000-0000-0000-0000-000000000000",
                            "System",
                            "Failed",
                            DateTimeOffset.UtcNow,
                            "0x87D300C9",
                            "Registry Win32Apps",
                            "Synthetic registry detail")
                    ])
            ];
        }
    }

    private sealed class FakeCloudManagedDeviceService : ICloudManagedDeviceService
    {
        public int LookupCalls { get; private set; }

        public ValueTask<CloudManagedDeviceSummary?> FindManagedDeviceByHostAsync(string host, CancellationToken cancellationToken)
        {
            LookupCalls++;
            return ValueTask.FromResult<CloudManagedDeviceSummary?>(new CloudManagedDeviceSummary(
                $"managed-{host}",
                host,
                $"aad-{host}",
                "tester@contoso.com",
                "Windows",
                "Compliant",
                DateTimeOffset.UtcNow,
                true,
                "Test"));
        }

        public ValueTask<CloudSyncResult> SyncManagedDeviceAsync(string managedDeviceId, CancellationToken cancellationToken) =>
            ValueTask.FromResult(CloudSyncResult.Ok($"Synced {managedDeviceId}."));
    }

    private sealed class NullAuthService : IAuthService
    {
        public ValueTask<AuthSession> LoginAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<AuthSession?> GetCurrentSessionAsync(CancellationToken cancellationToken) => ValueTask.FromResult<AuthSession?>(null);
    }

    private sealed class ExpiredAuthService : IAuthService
    {
        public ValueTask<AuthSession> LoginAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(new AuthSession("contoso.onmicrosoft.com", "tester@contoso.com", DateTimeOffset.UtcNow.AddMinutes(-2), false));

        public ValueTask<AuthSession?> GetCurrentSessionAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<AuthSession?>(new AuthSession("contoso.onmicrosoft.com", "tester@contoso.com", DateTimeOffset.UtcNow.AddMinutes(-2), false));
    }

    private sealed class StableAuthService : IAuthService
    {
        private readonly AuthSession _session = new("contoso.onmicrosoft.com", "tester@contoso.com", DateTimeOffset.UtcNow.AddHours(1), false);

        public ValueTask<AuthSession> LoginAsync(CancellationToken cancellationToken) => ValueTask.FromResult(_session);
        public ValueTask<AuthSession?> GetCurrentSessionAsync(CancellationToken cancellationToken) => ValueTask.FromResult<AuthSession?>(_session);
    }

    private sealed class FakeHostStatusLogSink : IHostStatusLogSink
    {
        public List<string> Entries { get; } = [];
        public void Append(string message) => Entries.Add(message);
    }
}
