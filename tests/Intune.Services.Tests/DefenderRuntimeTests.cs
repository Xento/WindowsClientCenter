using System.Net;
using System.Net.Http;
using WindowsClientCenter.Defender.Contracts.Models;
using WindowsClientCenter.Intune.Services.Contracts;
using WindowsClientCenter.Intune.Services.Runtime;

namespace WindowsClientCenter.Tests.IntuneServices;

public sealed class DefenderRuntimeTests
{
    [Fact]
    public async Task LocalDefenderDiagnosticsService_ParsesSnapshotAndUsesManagedByPriority()
    {
        const string payload = """
        {
          "machineName": "CLIENT01",
          "capturedAtUtc": "2026-03-27T10:00:00Z",
          "isGpoManaged": true,
          "isMdmManaged": true,
          "isMdeManaged": true,
          "managedDefenderProductType": 6,
          "enrollmentStatus": 0,
          "antivirusEnabled": true,
          "realtimeProtectionEnabled": true,
          "behaviorMonitorEnabled": true,
          "ioavProtectionEnabled": true,
          "onAccessProtectionEnabled": true,
          "nisEnabled": true,
          "tamperProtectionEnabled": true,
          "runningMode": "Normal",
          "engineVersion": "1.1.24000.1",
          "productVersion": "4.18.24000.6",
          "antivirusSignatureVersion": "1.421.123.0",
          "antispywareSignatureVersion": "1.421.123.0",
          "nisEngineVersion": "1.1.24000.1",
          "nisSignatureVersion": "1.421.123.0",
          "signatureLastUpdatedUtc": "2026-03-27T08:00:00Z",
          "signatureAgeHours": 2,
          "quickScanStartUtc": "2026-03-27T07:30:00Z",
          "quickScanEndUtc": "2026-03-27T07:35:00Z",
          "fullScanStartUtc": "2026-03-20T07:00:00Z",
          "fullScanEndUtc": "2026-03-20T08:00:00Z",
          "lastScanUtc": "2026-03-27T07:35:00Z",
          "activeDetectionCount": 0,
          "activeHighOrCriticalDetectionCount": 0,
          "notes": []
        }
        """;

        var service = new LocalDefenderDiagnosticsService(new StaticPowerShellExecutor(new PowershellExecutionResult(0, payload, string.Empty)));

        var snapshot = await service.GetSnapshotAsync("CLIENT01", CancellationToken.None);

        Assert.Equal("CLIENT01", snapshot.MachineName);
        Assert.Equal("MDM (Intune)", snapshot.ManagedBy);
        Assert.True(snapshot.IsManaged);
        Assert.Equal("Green", snapshot.HealthLevel);
    }

    [Theory]
    [InlineData(6, 0, "MDM (Intune)")]
    [InlineData(7, 4, "Configuration Manager")]
    [InlineData(7, 3, "Configuration Manager + MDM (Co-managed)")]
    public async Task LocalDefenderDiagnosticsService_UsesMicrosoftRegistryMappingForManagedBy(
        int managedDefenderProductType,
        int enrollmentStatus,
        string expectedManagedBy)
    {
        var payload = $$"""
        {
          "machineName": "CLIENT01",
          "capturedAtUtc": "2026-03-27T10:00:00Z",
          "isGpoManaged": true,
          "isMdmManaged": true,
          "isMdeManaged": false,
          "managedDefenderProductType": {{managedDefenderProductType}},
          "enrollmentStatus": {{enrollmentStatus}},
          "onboardingState": 0,
          "activeDetectionCount": 0,
          "activeHighOrCriticalDetectionCount": 0,
          "notes": []
        }
        """;

        var service = new LocalDefenderDiagnosticsService(new StaticPowerShellExecutor(new PowershellExecutionResult(0, payload, string.Empty)));
        var snapshot = await service.GetSnapshotAsync("CLIENT01", CancellationToken.None);

        Assert.Equal(expectedManagedBy, snapshot.ManagedBy);
    }

    [Fact]
    public async Task LocalDefenderDiagnosticsService_UsesOnboardingStateAsMdeSignal()
    {
        const string payload = """
        {
          "machineName": "CLIENT01",
          "capturedAtUtc": "2026-03-27T10:00:00Z",
          "isGpoManaged": false,
          "isMdmManaged": false,
          "isMdeManaged": false,
          "onboardingState": 1,
          "activeDetectionCount": 0,
          "activeHighOrCriticalDetectionCount": 0,
          "notes": []
        }
        """;

        var service = new LocalDefenderDiagnosticsService(new StaticPowerShellExecutor(new PowershellExecutionResult(0, payload, string.Empty)));
        var snapshot = await service.GetSnapshotAsync("CLIENT01", CancellationToken.None);

        Assert.Equal("Defender for Endpoint", snapshot.ManagedBy);
    }

    [Fact]
    public async Task LocalDefenderDiagnosticsService_LoadsLatestVersionBaselineFromMicrosoftPage()
    {
        const string payload = """
        {
          "machineName": "CLIENT01",
          "capturedAtUtc": "2026-03-27T10:00:00Z",
          "isGpoManaged": false,
          "isMdmManaged": true,
          "isMdeManaged": false,
          "managedDefenderProductType": 6,
          "antivirusEnabled": true,
          "realtimeProtectionEnabled": true,
          "signatureAgeHours": 2,
          "activeDetectionCount": 0,
          "activeHighOrCriticalDetectionCount": 0,
          "notes": []
        }
        """;

        const string html = """
        <ul class="c-list list-bottom-margin">
          <li>Version: <span>1.447.37.0</span></li>
          <li>Engine Version: <span>1.1.26020.3</span></li>
          <li>Platform Version: <span>4.18.26020.6</span></li>
          <li>Released: <span id="dateofrelease">3/27/2026 2:29:26 PM</span></li>
        </ul>
        """;

        var httpClient = new HttpClient(new StaticHttpMessageHandler(HttpStatusCode.OK, html));
        var service = new LocalDefenderDiagnosticsService(
            new StaticPowerShellExecutor(new PowershellExecutionResult(0, payload, string.Empty)),
            httpClient);

        var snapshot = await service.GetSnapshotAsync("CLIENT01", CancellationToken.None);

        Assert.NotNull(snapshot.LatestVersionInfo);
        Assert.Equal("https://www.microsoft.com/en-us/wdsi/defenderupdates", snapshot.LatestVersionInfo!.SourceUrl);
        Assert.Equal("https://www.microsoft.com/en-us/wdsi/definitions/antimalware-definition-release-notes", snapshot.LatestVersionInfo.ReleaseNotesUrl);
        Assert.Equal("1.447.37.0", snapshot.LatestVersionInfo!.SecurityIntelligenceVersion);
        Assert.Equal("1.1.26020.3", snapshot.LatestVersionInfo.EngineVersion);
        Assert.Equal("4.18.26020.6", snapshot.LatestVersionInfo.PlatformVersion);
        Assert.Null(snapshot.LatestVersionInfo.ErrorMessage);
    }

    [Fact]
    public async Task LocalDefenderDiagnosticsService_AllowsPreviousDefinitionVersionAsGreen()
    {
        const string payload = """
        {
          "machineName": "CLIENT01",
          "capturedAtUtc": "2026-03-27T10:00:00Z",
          "isGpoManaged": false,
          "isMdmManaged": true,
          "isMdeManaged": false,
          "managedDefenderProductType": 6,
          "antivirusEnabled": true,
          "realtimeProtectionEnabled": true,
          "antivirusSignatureVersion": "1.447.36.0",
          "signatureAgeHours": 40,
          "activeDetectionCount": 0,
          "activeHighOrCriticalDetectionCount": 0,
          "notes": []
        }
        """;

        const string html = """
        <ul class="c-list list-bottom-margin">
          <li>Version: <span>1.447.37.0</span></li>
          <li>Engine Version: <span>1.1.26020.3</span></li>
          <li>Platform Version: <span>4.18.26020.6</span></li>
          <li>Released: <span id="dateofrelease">3/27/2026 2:29:26 PM</span></li>
        </ul>
        """;

        var httpClient = new HttpClient(new StaticHttpMessageHandler(HttpStatusCode.OK, html));
        var service = new LocalDefenderDiagnosticsService(
            new StaticPowerShellExecutor(new PowershellExecutionResult(0, payload, string.Empty)),
            httpClient);

        var snapshot = await service.GetSnapshotAsync("CLIENT01", CancellationToken.None);

        Assert.Equal("Green", snapshot.HealthLevel);
        Assert.Equal("Defender status is healthy and up to date.", snapshot.HealthSummary);
    }

    [Fact]
    public async Task LocalDefenderDiagnosticsService_KeepsFreshDefinitionsGreen_WhenTheyAreBehindLatestBaseline()
    {
        const string payload = """
        {
          "machineName": "CLIENT01",
          "capturedAtUtc": "2026-03-27T10:00:00Z",
          "isGpoManaged": false,
          "isMdmManaged": true,
          "isMdeManaged": false,
          "managedDefenderProductType": 6,
          "antivirusEnabled": true,
          "realtimeProtectionEnabled": true,
          "antivirusSignatureVersion": "1.447.35.0",
          "signatureAgeHours": 10,
          "activeDetectionCount": 0,
          "activeHighOrCriticalDetectionCount": 0,
          "notes": []
        }
        """;

        const string html = """
        <ul class="c-list list-bottom-margin">
          <li>Version: <span>1.447.37.0</span></li>
          <li>Engine Version: <span>1.1.26020.3</span></li>
          <li>Platform Version: <span>4.18.26020.6</span></li>
          <li>Released: <span id="dateofrelease">3/27/2026 2:29:26 PM</span></li>
        </ul>
        """;

        var httpClient = new HttpClient(new StaticHttpMessageHandler(HttpStatusCode.OK, html));
        var service = new LocalDefenderDiagnosticsService(
            new StaticPowerShellExecutor(new PowershellExecutionResult(0, payload, string.Empty)),
            httpClient);

        var snapshot = await service.GetSnapshotAsync("CLIENT01", CancellationToken.None);

        Assert.Equal("Green", snapshot.HealthLevel);
        Assert.Equal(36, snapshot.Versions.SignatureWarningThresholdHours);
        Assert.False(snapshot.Versions.SignaturesOutdated);
        Assert.Contains("freshness threshold", snapshot.HealthSummary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LocalDefenderDiagnosticsService_FlagsDefinitionsBehindLatestBaseline_AfterFreshnessWindowExpires()
    {
        const string payload = """
        {
          "machineName": "CLIENT01",
          "capturedAtUtc": "2026-03-27T10:00:00Z",
          "isGpoManaged": false,
          "isMdmManaged": true,
          "isMdeManaged": false,
          "managedDefenderProductType": 6,
          "antivirusEnabled": true,
          "realtimeProtectionEnabled": true,
          "antivirusSignatureVersion": "1.447.35.0",
          "signatureAgeHours": 40,
          "activeDetectionCount": 0,
          "activeHighOrCriticalDetectionCount": 0,
          "notes": []
        }
        """;

        const string html = """
        <ul class="c-list list-bottom-margin">
          <li>Version: <span>1.447.37.0</span></li>
          <li>Engine Version: <span>1.1.26020.3</span></li>
          <li>Platform Version: <span>4.18.26020.6</span></li>
          <li>Released: <span id="dateofrelease">3/27/2026 2:29:26 PM</span></li>
        </ul>
        """;

        var httpClient = new HttpClient(new StaticHttpMessageHandler(HttpStatusCode.OK, html));
        var service = new LocalDefenderDiagnosticsService(
            new StaticPowerShellExecutor(new PowershellExecutionResult(0, payload, string.Empty)),
            httpClient);

        var snapshot = await service.GetSnapshotAsync("CLIENT01", CancellationToken.None);

        Assert.Equal("Yellow", snapshot.HealthLevel);
        Assert.Contains("behind latest baseline", snapshot.HealthSummary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LocalDefenderDiagnosticsService_ParsesAsrRulesAndExclusionsFromSettingsPayload()
    {
        const string payload = """
        {
          "capturedAtUtc": "2026-03-27T10:00:00Z",
          "source": "Get-MpPreference + Get-MpComputerStatus",
          "settings": [
            { "name": "ExclusionPath", "value": "C:\\Temp" },
            { "name": "AttackSurfaceReductionOnlyExclusions", "value": "C:\\Tools" }
          ],
          "asrRules": [
            { "ruleId": "56a863a9-875e-4185-98a7-b882c64b5ce5", "action": "1" },
            { "ruleId": "d4f940ab-401b-4efc-aadc-ad5f3c50688a", "action": "2" }
          ],
          "asrPerRuleExclusionsRaw": [
            "56a863a9-875e-4185-98a7-b882c64b5ce5=C:\\\\Drivers\\\\allowed.sys|C:\\\\Drivers\\\\vendor",
            "d4f940ab-401b-4efc-aadc-ad5f3c50688a=C:\\\\OfficeTools"
          ],
          "exclusions": [
            { "type": "Path", "value": "C:\\Temp" },
            { "type": "ASROnly", "value": "C:\\Tools" }
          ],
          "notes": []
        }
        """;

        var service = new LocalDefenderDiagnosticsService(new StaticPowerShellExecutor(new PowershellExecutionResult(0, payload, string.Empty)));

        var settings = await service.GetSettingsAsync("CLIENT01", CancellationToken.None);

        Assert.Equal(2, settings.AsrRules?.Count);
        Assert.Contains(
            settings.AsrRules ?? [],
            rule => rule.RuleId == "56a863a9-875e-4185-98a7-b882c64b5ce5" &&
                    rule.RuleName.Contains("vulnerable signed drivers", StringComparison.OrdinalIgnoreCase) &&
                    rule.Action == "Block (1)" &&
                    rule.RuleSpecificExclusions.Contains("allowed.sys", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(2, settings.Exclusions?.Count);
        Assert.Contains(settings.Exclusions ?? [], exclusion => exclusion.Type == "ASROnly" && exclusion.Value == "C:\\Tools");
    }

    [Fact]
    public async Task LocalDefenderDiagnosticsService_ResolvesKnownNumericSettingsValues()
    {
        const string payload = """
        {
          "capturedAtUtc": "2026-03-27T10:00:00Z",
          "source": "Get-MpPreference + Get-MpComputerStatus",
          "settings": [
            { "name": "PUAProtection", "value": "1" },
            { "name": "MAPSReporting", "value": "2" },
            { "name": "SubmitSamplesConsent", "value": "3" },
            { "name": "CloudBlockLevel", "value": "4" },
            { "name": "ScanScheduleDay", "value": "0" },
            { "name": "SignatureScheduleDay", "value": "8" },
            { "name": "EnableControlledFolderAccess", "value": "2" }
          ],
          "asrRules": [],
          "exclusions": [],
          "notes": []
        }
        """;

        var service = new LocalDefenderDiagnosticsService(new StaticPowerShellExecutor(new PowershellExecutionResult(0, payload, string.Empty)));
        var settings = await service.GetSettingsAsync("CLIENT01", CancellationToken.None);

        Assert.Contains(settings.Settings, entry => entry.Name == "PUAProtection" && entry.Value == "(1) Enabled");
        Assert.Contains(settings.Settings, entry => entry.Name == "MAPSReporting" && entry.Value == "(2) Advanced membership");
        Assert.Contains(settings.Settings, entry => entry.Name == "SubmitSamplesConsent" && entry.Value == "(3) Send all samples automatically");
        Assert.Contains(settings.Settings, entry => entry.Name == "CloudBlockLevel" && entry.Value == "(4) High plus");
        Assert.Contains(settings.Settings, entry => entry.Name == "ScanScheduleDay" && entry.Value == "(0) Every day");
        Assert.Contains(settings.Settings, entry => entry.Name == "SignatureScheduleDay" && entry.Value == "(8) Never");
        Assert.Contains(settings.Settings, entry => entry.Name == "EnableControlledFolderAccess" && entry.Value == "(2) Audit mode");
    }

    [Fact]
    public async Task LocalDefenderDiagnosticsService_FiltersDetectionsByDaysBack()
    {
        var recent = DateTimeOffset.UtcNow.AddDays(-10).ToString("O");
        var old = DateTimeOffset.UtcNow.AddDays(-120).ToString("O");

        var payload = $$"""
        {
          "source": "MpCmdlets",
          "notes": [],
          "entries": [
            {
              "detectedAtUtc": "{{recent}}",
              "lastStatusChangeUtc": "{{recent}}",
              "threatName": "Recent threat",
              "threatId": 1,
              "severity": "High",
              "category": "Test",
              "action": "1",
              "actionSuccess": false,
              "isActive": true,
              "source": "MpCmdlets",
              "details": "recent"
            },
            {
              "detectedAtUtc": "{{old}}",
              "lastStatusChangeUtc": "{{old}}",
              "threatName": "Old threat",
              "threatId": 2,
              "severity": "Low",
              "category": "Test",
              "action": "2",
              "actionSuccess": true,
              "isActive": false,
              "source": "MpCmdlets",
              "details": "old"
            }
          ]
        }
        """;

        var service = new LocalDefenderDiagnosticsService(new StaticPowerShellExecutor(new PowershellExecutionResult(0, payload, string.Empty)));

        var detections = await service.GetDetectionsAsync("CLIENT01", 90, CancellationToken.None);

        var entry = Assert.Single(detections);
        Assert.Equal("Recent threat", entry.ThreatName);
    }

    [Fact]
    public async Task LocalDefenderDiagnosticsService_UsesThreatStatusForDetectionActivity()
    {
        var now = DateTimeOffset.UtcNow.ToString("O");

        var payload = $$"""
        {
          "source": "MpCmdlets",
          "notes": [],
          "entries": [
            {
              "detectedAtUtc": "{{now}}",
              "lastStatusChangeUtc": "{{now}}",
              "threatName": "Cleaned threat",
              "threatId": 1,
              "severity": "High",
              "category": "Test",
              "action": "Cleaned (2)",
              "actionSuccess": null,
              "isActive": false,
              "source": "MpCmdlets",
              "details": "cleaned"
            },
            {
              "detectedAtUtc": "{{now}}",
              "lastStatusChangeUtc": "{{now}}",
              "threatName": "Failed quarantine threat",
              "threatId": 2,
              "severity": "High",
              "category": "Test",
              "action": "Quarantine failed (102)",
              "actionSuccess": null,
              "isActive": true,
              "source": "MpCmdlets",
              "details": "failed"
            }
          ]
        }
        """;

        var service = new LocalDefenderDiagnosticsService(new StaticPowerShellExecutor(new PowershellExecutionResult(0, payload, string.Empty)));

        var detections = await service.GetDetectionsAsync("CLIENT01", 30, CancellationToken.None);

        Assert.Collection(
            detections,
            entry =>
            {
                Assert.Equal("Cleaned threat", entry.ThreatName);
                Assert.False(entry.IsActive);
                Assert.Equal("Cleaned (2)", entry.Action);
            },
            entry =>
            {
                Assert.Equal("Failed quarantine threat", entry.ThreatName);
                Assert.True(entry.IsActive);
                Assert.Equal("Quarantine failed (102)", entry.Action);
	            });
    }

    [Fact]
    public async Task LocalDefenderDiagnosticsService_ParsesDeviceControlUsbBlocks_AndBuildsSummary()
    {
        var now = DateTimeOffset.UtcNow.AddMinutes(-5).ToString("O");
        var payload = $$"""
        {
          "capturedAtUtc": "{{DateTimeOffset.UtcNow:O}}",
          "source": "Local Device Control events",
          "notes": [],
          "entries": [
            {
              "timeCreatedUtc": "{{now}}",
              "eventId": 1123,
              "provider": "Microsoft-Windows-Windows Defender",
              "logName": "Microsoft-Windows-Windows Defender/Operational",
              "level": "Warning",
              "deviceType": "Removable storage",
              "deviceName": "USB Mass Storage",
              "friendlyName": "Contoso USB Drive",
              "manufacturer": "Contoso",
              "deviceId": "USBSTOR\\DISK",
              "deviceInstanceId": "USBSTOR\\DISK&VEN_CONTOSO&PROD_FASTUSB\\123456",
              "hardwareIds": "USBSTOR\\DISK&VEN_CONTOSO&PROD_FASTUSB",
              "vendorId": "1234",
              "productId": "5678",
              "serialNumber": "123456",
              "classGuid": "{53f56307-b6bf-11d0-94f2-00a0c91efb8b}",
              "user": "CONTOSO\\user1",
              "sid": "S-1-5-21",
              "policyName": "Block removable storage",
              "policyId": "policy-1",
              "policyRuleId": "rule-1",
              "policyVerdict": "Deny",
              "access": "Read",
              "action": "Blocked",
              "isBlocked": true,
              "message": "Device Control blocked USB device."
            }
          ]
        }
        """;

        var service = new LocalDefenderDiagnosticsService(new StaticPowerShellExecutor(new PowershellExecutionResult(0, payload, string.Empty)));

        var snapshot = await service.GetDeviceControlEventsAsync("CLIENT01", 30, CancellationToken.None);

        var entry = Assert.Single(snapshot.Events);
        Assert.True(entry.IsBlocked);
        Assert.Equal("USBSTOR\\DISK&VEN_CONTOSO&PROD_FASTUSB\\123456", entry.DeviceInstanceId);
        Assert.Equal("1234", entry.VendorId);
        var summary = Assert.Single(snapshot.DeviceSummaries);
        Assert.Equal("Contoso USB Drive", summary.DisplayName);
        Assert.Equal(1, summary.BlockedCount);
        Assert.Equal("USBSTOR\\DISK&VEN_CONTOSO&PROD_FASTUSB\\123456", summary.DeviceKey);
    }

    [Fact]
    public async Task LocalDefenderDiagnosticsService_GroupsPrinterBlocks_ByDeviceIdFallback()
    {
        var first = DateTimeOffset.UtcNow.AddHours(-2).ToString("O");
        var last = DateTimeOffset.UtcNow.AddMinutes(-15).ToString("O");
        const string deviceId = "SWD\\PRINTENUM\\CONTOSO-PRINTER";
        var deviceIdJson = deviceId.Replace("\\", "\\\\", StringComparison.Ordinal);
        var payload = $$"""
        {
          "capturedAtUtc": "{{DateTimeOffset.UtcNow:O}}",
          "source": "Local Device Control events",
          "notes": [],
          "entries": [
            {
              "timeCreatedUtc": "{{first}}",
              "eventId": 201,
              "provider": "Microsoft-Windows-Sense",
              "logName": "Microsoft-Windows-Sense/Operational",
              "level": "Warning",
              "deviceType": "Printer",
              "deviceName": "Contoso Printer",
              "friendlyName": "",
              "manufacturer": "Contoso",
              "deviceId": "{{deviceIdJson}}",
              "deviceInstanceId": "",
              "hardwareIds": "",
              "vendorId": "",
              "productId": "",
              "serialNumber": "",
              "classGuid": "{4d36e979-e325-11ce-bfc1-08002be10318}",
              "user": "CONTOSO\\user1",
              "sid": "",
              "policyName": "Block printers",
              "policyId": "policy-prn",
              "policyRuleId": "rule-prn",
              "policyVerdict": "Denied",
              "access": "Print",
              "action": "Deny",
              "isBlocked": true,
              "message": "Device Control denied printer access."
            },
            {
              "timeCreatedUtc": "{{last}}",
              "eventId": 202,
              "provider": "Microsoft-Windows-Sense",
              "logName": "Microsoft-Windows-Sense/Operational",
              "level": "Warning",
              "deviceType": "Printer",
              "deviceName": "Contoso Printer",
              "friendlyName": "",
              "manufacturer": "Contoso",
              "deviceId": "{{deviceIdJson}}",
              "deviceInstanceId": "",
              "hardwareIds": "",
              "vendorId": "",
              "productId": "",
              "serialNumber": "",
              "classGuid": "{4d36e979-e325-11ce-bfc1-08002be10318}",
              "user": "CONTOSO\\user2",
              "sid": "",
              "policyName": "Block printers",
              "policyId": "policy-prn",
              "policyRuleId": "rule-prn",
              "policyVerdict": "Blocked",
              "access": "Print",
              "action": "Blocked",
              "isBlocked": true,
              "message": "Device Control blocked printer access."
            }
          ]
        }
        """;

        var service = new LocalDefenderDiagnosticsService(new StaticPowerShellExecutor(new PowershellExecutionResult(0, payload, string.Empty)));

        var snapshot = await service.GetDeviceControlEventsAsync("CLIENT01", 30, CancellationToken.None);

        var summary = Assert.Single(snapshot.DeviceSummaries);
        Assert.Equal(deviceId, summary.DeviceKey);
        Assert.Equal("Printer", summary.DeviceType);
        Assert.Equal(2, summary.BlockedCount);
        Assert.Equal("CONTOSO\\user2", summary.LastUser);
    }

    [Fact]
    public async Task LocalDefenderDiagnosticsService_DeviceControlScript_IncludesMessageFallbackExtraction()
    {
        var executor = new RecordingPowerShellExecutor(new PowershellExecutionResult(0,
            """
            { "capturedAtUtc": "2026-03-27T10:00:00Z", "source": "Local Device Control events", "notes": [], "entries": [] }
            """,
            string.Empty));
        var service = new LocalDefenderDiagnosticsService(executor);

        await service.GetDeviceControlEventsAsync("CLIENT01", 30, CancellationToken.None);

        Assert.Contains("function Get-MessageValue", executor.LastScript, StringComparison.Ordinal);
        Assert.Contains("Device Instance Id", executor.LastScript, StringComparison.Ordinal);
        Assert.Contains("Policy Verdict", executor.LastScript, StringComparison.Ordinal);
        Assert.Contains("Printer", executor.LastScript, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(DefenderActionType.QuickScan, "Start-MpScan -ScanType QuickScan")]
    [InlineData(DefenderActionType.FullScan, "Start-MpScan -ScanType FullScan")]
    [InlineData(DefenderActionType.StopScan, "Stop-MpScan")]
    [InlineData(DefenderActionType.SignatureUpdate, "Update-MpSignature")]
    [InlineData(DefenderActionType.RestartService, "Restart-Service -Name WinDefend")]
    public async Task LocalDefenderDiagnosticsService_MapsActionsToExpectedPowerShell(
        DefenderActionType actionType,
        string expectedSnippet)
    {
        var executor = new RecordingPowerShellExecutor(new PowershellExecutionResult(0,
            """
            { "success": true, "message": "ok", "errorCode": "", "executedAtUtc": "2026-03-27T10:00:00Z" }
            """,
            string.Empty));

        var service = new LocalDefenderDiagnosticsService(executor);

        var result = await service.ExecuteActionAsync("CLIENT01", new DefenderActionRequest(actionType), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains(expectedSnippet, executor.LastScript, StringComparison.Ordinal);
    }

    private sealed class StaticPowerShellExecutor(PowershellExecutionResult result) : IPowerShellExecutor
    {
        public ValueTask<PowershellExecutionResult> ExecuteForHostAsync(string host, string scriptBody, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(result);
        }
    }

    private sealed class RecordingPowerShellExecutor(PowershellExecutionResult result) : IPowerShellExecutor
    {
        public string LastScript { get; private set; } = string.Empty;

        public ValueTask<PowershellExecutionResult> ExecuteForHostAsync(string host, string scriptBody, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastScript = scriptBody;
            return ValueTask.FromResult(result);
        }
    }

    private sealed class StaticHttpMessageHandler(HttpStatusCode statusCode, string content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var response = new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(content)
            };

            return Task.FromResult(response);
        }
    }
}
