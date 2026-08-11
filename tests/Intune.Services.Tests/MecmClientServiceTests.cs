using System.Text.Json;
using WindowsClientCenter.Intune.Services.Models;
using WindowsClientCenter.Intune.Services.Runtime;
using Xunit;

namespace WindowsClientCenter.Tests.IntuneServices;

public sealed class MecmClientServiceTests
{
    [Fact]
    public async Task GetOverviewAsync_MapsPayloadToSnapshot()
    {
        var executor = new RecordingPowerShellExecutor
        {
            Result = new PowershellExecutionResult(
                0,
                JsonSerializer.Serialize(new
                {
                    clientVersion = "5.00.9128.1005",
                    assignedSite = "PRI",
                    managementPoint = "mp01.contoso.example",
                    rebootPendingText = "No",
                    coManagementStateText = "Active",
                    activities = new object[]
                    {
                        new
                        {
                            name = "Heartbeat Discovery",
                            statusText = "Reported",
                            statusLevel = "Green",
                            startedUtc = "2026-04-24T09:00:00Z",
                            reportedUtc = "2026-04-24T09:05:00Z",
                            detail = "Discovery data was reported."
                        }
                    },
                    workloads = new object[]
                    {
                        new
                        {
                            name = "Compliance Policies",
                            authority = "Intune",
                            statusLevel = "Green",
                            detail = "Managed by Intune."
                        }
                    },
                    components = new object[]
                    {
                        new
                        {
                            displayName = "Software Updates",
                            name = "UpdatesAgent",
                            version = "5.00.9128.1005",
                            isEnabled = true,
                            statusLevel = "Green",
                            detail = "Enabled."
                        }
                    },
                    services = new object[]
                    {
                        new
                        {
                            name = "CcmExec",
                            displayName = "SMS Agent Host",
                            status = "Running",
                            startMode = "Auto",
                            statusLevel = "Green",
                            detail = "Core service."
                        }
                    },
                    healthChecks = new object[]
                    {
                        new
                        {
                            name = "WMI",
                            statusText = "Healthy",
                            statusLevel = "Green",
                            detail = "Reachable."
                        }
                    },
                    warnings = new[] { "PolicyAgent.log was not available." }
                }),
                string.Empty)
        };

        var service = new MecmClientService(executor);

        var snapshot = await service.GetOverviewAsync("CLIENT01", CancellationToken.None);

        Assert.Equal("CLIENT01", snapshot.Host);
        Assert.Equal("5.00.9128.1005", snapshot.ClientVersion);
        Assert.Equal("PRI", snapshot.AssignedSite);
        Assert.Equal("mp01.contoso.example", snapshot.ManagementPoint);
        Assert.Equal("No", snapshot.RebootPendingText);
        Assert.Equal("Active", snapshot.CoManagementStateText);
        Assert.Contains("PolicyAgent.log was not available.", snapshot.Warnings);
        Assert.Single(snapshot.Activities);
        Assert.Equal("Heartbeat Discovery", snapshot.Activities[0].Name);
        Assert.Equal("Compliance Policies", snapshot.Workloads[0].Name);
        Assert.Single(snapshot.Components);
        Assert.Single(snapshot.Services);
        Assert.Single(snapshot.HealthChecks);
    }

    [Fact]
    public async Task GetOverviewAsync_UsesPowerShellCompatibleObjectCollectors()
    {
        var executor = new RecordingPowerShellExecutor
        {
            Result = new PowershellExecutionResult(0, "{}", string.Empty)
        };

        var service = new MecmClientService(executor);

        _ = await service.GetOverviewAsync("CLIENT01", CancellationToken.None);

        Assert.Contains("System.Collections.ArrayList", executor.LastScript, StringComparison.Ordinal);
        Assert.DoesNotContain("System.Collections.Generic.List[object]", executor.LastScript, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetOverviewAsync_UsesAssignedSiteAndManagementPointFallbacks()
    {
        var executor = new RecordingPowerShellExecutor
        {
            Result = new PowershellExecutionResult(0, "{}", string.Empty)
        };

        var service = new MecmClientService(executor);

        _ = await service.GetOverviewAsync("CLIENT01", CancellationToken.None);

        Assert.Contains("([wmiclass]'ROOT\\ccm:SMS_Client').GetAssignedSite()", executor.LastScript, StringComparison.Ordinal);
        Assert.Contains("LocationServices.log", executor.LastScript, StringComparison.Ordinal);
        Assert.Contains("AssignedSiteCode", executor.LastScript, StringComparison.Ordinal);
        Assert.Contains("Current Management Point is", executor.LastScript, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetOverviewAsync_UsesRegistryBackedCoManagementFlagsAndCurrentBitMappings()
    {
        var executor = new RecordingPowerShellExecutor
        {
            Result = new PowershellExecutionResult(0, "{}", string.Empty)
        };

        var service = new MecmClientService(executor);

        _ = await service.GetOverviewAsync("CLIENT01", CancellationToken.None);

        Assert.Contains("HKLM:\\SOFTWARE\\Microsoft\\CCM", executor.LastScript, StringComparison.Ordinal);
        Assert.Contains("CoManagementFlags", executor.LastScript, StringComparison.Ordinal);
        Assert.Contains("Name = 'Device Configuration'; Bit = 8", executor.LastScript, StringComparison.Ordinal);
        Assert.Contains("Name = 'Endpoint Protection'; Bit = 32", executor.LastScript, StringComparison.Ordinal);
        Assert.DoesNotContain("(if ($isIntuneManaged)", executor.LastScript, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetOverviewAsync_WhenPayloadParsingFails_ReturnsDetailedWarning()
    {
        var executor = new RecordingPowerShellExecutor
        {
            Result = new PowershellExecutionResult(0, "Argument types do not match", string.Empty)
        };

        var service = new MecmClientService(executor);

        var snapshot = await service.GetOverviewAsync("CLIENT01", CancellationToken.None);

        Assert.Empty(snapshot.Activities);
        Assert.Contains(snapshot.Warnings, warning => warning.Contains("Failed to parse MECM overview payload", StringComparison.Ordinal));
        Assert.Contains(snapshot.Warnings, warning => warning.Contains("Payload preview: Argument types do not match", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(MecmOverviewAction.RequestMachinePolicy, "Invoke-IccTriggerSchedule '{00000000-0000-0000-0000-000000000021}'")]
    [InlineData(MecmOverviewAction.ResetPolicyHard, "Invoke-IccResetPolicy 1")]
    [InlineData(MecmOverviewAction.RepairClient, "CCM_InstalledProduct")]
    public async Task ExecuteOverviewActionAsync_UsesExpectedMechanism(MecmOverviewAction action, string expectedSnippet)
    {
        var executor = new RecordingPowerShellExecutor
        {
            Result = new PowershellExecutionResult(0, "Queued.", string.Empty)
        };

        var service = new MecmClientService(executor);
        var result = await service.ExecuteOverviewActionAsync("CLIENT01", action, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains(expectedSnippet, executor.LastScript, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetApplicationsAsync_MapsPayloadToSnapshot()
    {
        var executor = new RecordingPowerShellExecutor
        {
            Result = new PowershellExecutionResult(
                0,
                JsonSerializer.Serialize(new
                {
                    entries = new object[]
                    {
                        new
                        {
                            id = "ScopeId_001/App_001",
                            name = "7-Zip",
                            fullName = "7-Zip x64",
                            description = "Compression tool",
                            icon = "AQID",
                            softwareVersion = "24.09",
                            revision = "5",
                            userUiExperience = true,
                            isPreflightOnly = false,
                            isMachineTarget = true,
                            allowedActions = new[] { "Install", "Repair", "Uninstall" },
                            installState = "Installed",
                            applicabilityState = "Applicable",
                            resolvedState = "Installed",
                            evaluationState = 1,
                            errorCode = 3010,
                            lastEvalTimeUtc = "2026-04-20T08:10:00Z",
                            lastInstallTimeUtc = "2026-04-20T08:12:00Z",
                            hasInstallCommand = true,
                            hasUninstallCommand = true,
                            hasIcon = true
                        }
                    },
                    warnings = Array.Empty<string>()
                }),
                string.Empty)
        };

        var service = new MecmClientService(executor);

        var snapshot = await service.GetApplicationsAsync("CLIENT01", CancellationToken.None);

        var app = Assert.Single(snapshot.Entries);
        Assert.Equal("CLIENT01", snapshot.Host);
        Assert.Equal("7-Zip", app.Name);
        Assert.Contains("desired/resolved", app.EvaluationStateText, StringComparison.Ordinal);
        Assert.Equal((uint)3010, app.ErrorCode);
        Assert.True(app.HasIcon);
        Assert.True(app.HasInstallCommand);
        Assert.True(app.HasUninstallCommand);
        Assert.Contains("Install", app.AllowedActions);
    }

    [Fact]
    public async Task GetPendingUpdatesAsync_MapsPayloadToSnapshot()
    {
        var executor = new RecordingPowerShellExecutor
        {
            Result = new PowershellExecutionResult(
                0,
                JsonSerializer.Serialize(new
                {
                    entries = new object[]
                    {
                        new
                        {
                            updateId = "Site_001/SUM_501",
                            name = "2026-04 Cumulative Update",
                            publisher = "Microsoft",
                            description = "Security update",
                            articleId = "KB5060001",
                            bulletinId = "MS26-041",
                            evaluationState = 5,
                            percentComplete = 72,
                            errorCode = 0,
                            deadlineUtc = "2026-04-20T10:00:00Z"
                        }
                    },
                    warnings = Array.Empty<string>()
                }),
                string.Empty)
        };

        var service = new MecmClientService(executor);

        var snapshot = await service.GetPendingUpdatesAsync("CLIENT01", CancellationToken.None);

        var update = Assert.Single(snapshot.Entries);
        Assert.Equal("Site_001/SUM_501", update.UpdateId);
        Assert.Equal(72, update.PercentComplete);
        Assert.Equal("ciJobStateDownloading", update.EvaluationStateText);
    }

    [Fact]
    public async Task GetAllUpdatesAsync_ReturnsWarningsWithoutThrowing()
    {
        var executor = new RecordingPowerShellExecutor
        {
            Result = new PowershellExecutionResult(
                0,
                JsonSerializer.Serialize(new
                {
                    entries = Array.Empty<object>(),
                    warnings = new[] { "MECM client SDK is not available on the target host." }
                }),
                string.Empty)
        };

        var service = new MecmClientService(executor);

        var snapshot = await service.GetAllUpdatesAsync("CLIENT01", CancellationToken.None);

        Assert.Empty(snapshot.Entries);
        Assert.Contains("MECM client SDK is not available on the target host.", snapshot.Warnings);
    }

    [Theory]
    [InlineData(MecmApplicationAction.Install, "MethodName 'Install'")]
    [InlineData(MecmApplicationAction.Repair, "MethodName 'Repair'")]
    [InlineData(MecmApplicationAction.Uninstall, "MethodName 'Uninstall'")]
    public async Task ExecuteApplicationActionAsync_UsesExpectedScript(MecmApplicationAction action, string expectedSnippet)
    {
        var executor = new RecordingPowerShellExecutor
        {
            Result = new PowershellExecutionResult(0, "Queued.", string.Empty)
        };

        var service = new MecmClientService(executor);
        var result = await service.ExecuteApplicationActionAsync("CLIENT01", "App_01", "5", true, action, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains(expectedSnippet, executor.LastScript, StringComparison.Ordinal);
        Assert.Contains("Invoke-CimMethod", executor.LastScript, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildApplicationActionScriptBody_UninstallMatchesSccmClientCenterWorkflow()
    {
        var script = MecmClientService.BuildApplicationActionScriptBody(
            "ScopeId_E35569AA-7FB2-4093-85F3-F379A1AA528F/Application_7f771728-397c-47a5-8e1a-5019a5eeaf3b",
            "5",
            true,
            MecmApplicationAction.Uninstall);

        Assert.Contains("ROOT\\ccm\\Policy\\Machine\\ActualConfig", script, StringComparison.Ordinal);
        Assert.Contains("$applicationIdParts = 'ScopeId_E35569AA-7FB2-4093-85F3-F379A1AA528F/Application_7f771728-397c-47a5-8e1a-5019a5eeaf3b' -split '_'", script, StringComparison.Ordinal);
        Assert.Contains("Start-Sleep -Milliseconds 2000", script, StringComparison.Ordinal);
        Assert.Contains("Start-Sleep -Milliseconds 1000", script, StringComparison.Ordinal);
        Assert.Contains("Set-CimInstance -InputObject $assignment -Property @{ EnforcementDeadline = $null }", script, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildApplicationActionScriptBody_InstallDoesNotUseDeadlineOverrideWorkflow()
    {
        var script = MecmClientService.BuildApplicationActionScriptBody("App_01", "5", true, MecmApplicationAction.Install);

        Assert.DoesNotContain("CCM_ApplicationCIAssignment", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Start-Sleep -Milliseconds 2000", script, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(MecmApplicationEvaluationMode.UserPolicy, "000000000122")]
    [InlineData(MecmApplicationEvaluationMode.MachinePolicy, "000000000121")]
    [InlineData(MecmApplicationEvaluationMode.GlobalEvaluation, "000000000123")]
    public async Task TriggerApplicationEvaluationAsync_UsesExpectedMechanism(MecmApplicationEvaluationMode mode, string expectedSnippet)
    {
        var executor = new RecordingPowerShellExecutor
        {
            Result = new PowershellExecutionResult(0, "Triggered.", string.Empty)
        };

        var service = new MecmClientService(executor);
        var result = await service.TriggerApplicationEvaluationAsync("CLIENT01", mode, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains(expectedSnippet, executor.LastScript, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildGetApplicationsScriptBody_UsesDeliveryTypeSyncletsForCommandDetection()
    {
        var script = MecmClientService.BuildGetApplicationsScriptBody();

        Assert.Contains("CCM_AppDeliveryTypeSynclet", script, StringComparison.Ordinal);
        Assert.Contains("InstallAction", script, StringComparison.Ordinal);
        Assert.Contains("UninstallAction", script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InstallUpdatesAsync_SelectedMode_UsesSelectedIds()
    {
        var executor = new RecordingPowerShellExecutor
        {
            Result = new PowershellExecutionResult(0, "Queued.", string.Empty)
        };

        var service = new MecmClientService(executor);
        var result = await service.InstallUpdatesAsync(
            "CLIENT01",
            new MecmUpdateInstallRequest(MecmUpdateInstallMode.Selected, ["SUM_1", "SUM_2"]),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("'SUM_1'", executor.LastScript, StringComparison.Ordinal);
        Assert.Contains("CCM_SoftwareUpdatesManager", executor.LastScript, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(MecmUpdateInstallMode.AllMandatory, "InstallUpdates()")]
    [InlineData(MecmUpdateInstallMode.AllApproved, "SELECT * FROM CCM_SoftwareUpdate")]
    public async Task InstallUpdatesAsync_BuildsExpectedBulkScript(MecmUpdateInstallMode mode, string expectedSnippet)
    {
        var executor = new RecordingPowerShellExecutor
        {
            Result = new PowershellExecutionResult(0, "Queued.", string.Empty)
        };

        var service = new MecmClientService(executor);
        var result = await service.InstallUpdatesAsync(
            "CLIENT01",
            new MecmUpdateInstallRequest(mode, []),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains(expectedSnippet, executor.LastScript, StringComparison.Ordinal);
    }

    private sealed class RecordingPowerShellExecutor : IPowerShellExecutor
    {
        public PowershellExecutionResult Result { get; set; } = new(0, string.Empty, string.Empty);
        public string LastScript { get; private set; } = string.Empty;

        public ValueTask<PowershellExecutionResult> ExecuteForHostAsync(string host, string scriptBody, CancellationToken cancellationToken)
        {
            LastScript = scriptBody;
            return ValueTask.FromResult(Result);
        }
    }
}
