using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using WindowsClientCenter.Intune.Services.Contracts;
using WindowsClientCenter.Intune.Services.Models;
using WindowsClientCenter.Intune.Services.Runtime;
using Microsoft.Extensions.DependencyInjection;

namespace WindowsClientCenter.Tests.IntuneServices;

public sealed class IntuneRuntimeTests
{
    [Fact]
    public async Task LocalDiagnosticsService_ParsesStructuredSnapshotPayload()
    {
        const string payload = """
        {
          "machineName": "CLIENT01",
          "capturedAtUtc": "2026-03-20T10:15:00Z",
          "lastSyncText": "2026-03-20 09:44:00Z",
          "manufacturerText": "Dell Inc.",
          "modelText": "Latitude 7450",
          "serialNumberText": "ABC1234",
          "adJoinPathText": "OU=Workstations,OU=Berlin,DC=contoso,DC=com",
          "updateRingText": "Ring 3",
          "registrationSummary": "AzureAdJoined : YES; DeviceId : abc",
          "dsregStatusText": "Device State\nAzureAdJoined : YES",
          "dsregHighlights": ["AzureAdJoined : YES", "DeviceId : abc"],
          "enrollmentArtifacts": [
            {
              "artifactType": "Registry",
              "artifactPath": "HKLM:\\SOFTWARE\\Microsoft\\Enrollments\\{GUID}",
              "description": "Enrollment root",
              "enrollmentId": "11111111-1111-1111-1111-111111111111",
              "isRemovable": true
            }
          ],
          "enterpriseMgmtTasks": ["\\Microsoft\\Windows\\EnterpriseMgmt\\11111111-1111-1111-1111-111111111111\\PushLaunch"],
          "certificateSummaries": ["CN=MS-Organization-Access | CN=Issuer | 1234"],
          "serviceValues": [{"name": "MdmServerUrl", "value": "https://enrollment.manage.microsoft.com"}],
          "platformSecurity": {
            "bitLockerStatusText": "Protected",
            "bitLockerDetailText": "C: | FullyEncrypted | 100% encrypted | XtsAes256",
            "tpmStatusText": "Ready",
            "tpmDetailText": "Present: Yes | Ready: Yes | Enabled: Yes | Activated: Yes | Manufacturer: IFX | Spec: 2.0",
            "secureBootStatusText": "Enabled",
            "credentialGuardStatusText": "Running",
            "vbsStatusText": "Running",
            "memoryIntegrityStatusText": "Running"
          },
          "systemRuntime": {
            "uptimeText": "5d 03h 12m",
            "lastBootText": "2026-03-15 06:00:00Z",
            "installDateText": "2025-10-01 08:15:00Z",
            "pendingRebootStatusText": "Restart required",
            "pendingRebootDetailText": "Windows Update requested a restart.",
            "windowsUpdateScheduledRestartStatusText": "Scheduled",
            "windowsUpdateScheduledRestartTimeText": "2026-03-20 22:00:00 +01:00",
            "mecmScheduledRestartTimeText": "2026-03-20 23:30:00 +01:00",
            "sessionLockStatusText": "Locked",
            "sessionLockedSinceText": "2026-03-20 19:45:00 +01:00"
          },
          "networkConnectivity": {
            "primaryConnectionText": "LAN",
            "primaryAdapterText": "Intel(R) Ethernet Connection",
            "wiFiSsidText": "Not connected",
            "vpnStatusText": "Connected",
            "vpnProviderText": "Check Point Endpoint VPN",
            "isCheckpointVpnDetected": true
          },
          "notes": ["Collected successfully"]
        }
        """;

        var service = new LocalIntuneDiagnosticsService(
            new FakePowerShellExecutor(new PowershellExecutionResult(0, payload, string.Empty)),
            new HttpClient(),
            new IntuneRuntimeOptions());

        var snapshot = await service.GetSnapshotAsync("CLIENT01", CancellationToken.None);

        Assert.Equal("CLIENT01", snapshot.Host);
        Assert.Equal("CLIENT01", snapshot.MachineName);
        Assert.Equal("2026-03-20 09:44:00Z", snapshot.LastSyncText);
        Assert.Equal("Dell Inc.", snapshot.ManufacturerText);
        Assert.Equal("Latitude 7450", snapshot.ModelText);
        Assert.Equal("ABC1234", snapshot.SerialNumberText);
        Assert.Equal("OU=Workstations,OU=Berlin,DC=contoso,DC=com", snapshot.AdJoinPathText);
        Assert.Equal("Ring 3", snapshot.UpdateRingText);
        Assert.Equal("AzureAdJoined : YES; DeviceId : abc", snapshot.RegistrationSummary);
        Assert.Single(snapshot.EnrollmentArtifacts);
        Assert.Equal("MdmServerUrl", snapshot.ServiceValues[0].Name);
        Assert.Equal("Collected successfully", snapshot.Notes[0]);
        Assert.Equal("Protected", snapshot.PlatformSecurity?.BitLockerStatusText);
        Assert.Equal("Running", snapshot.PlatformSecurity?.CredentialGuardStatusText);
        Assert.Equal("5d 03h 12m", snapshot.SystemRuntime?.UptimeText);
        Assert.Equal("Restart required", snapshot.SystemRuntime?.PendingRebootStatusText);
        Assert.Equal("Scheduled", snapshot.SystemRuntime?.WindowsUpdateScheduledRestartStatusText);
        Assert.Equal("2026-03-20 22:00:00 +01:00", snapshot.SystemRuntime?.WindowsUpdateScheduledRestartTimeText);
        Assert.Equal("2026-03-20 23:30:00 +01:00", snapshot.SystemRuntime?.MecmScheduledRestartTimeText);
        Assert.Equal("Locked", snapshot.SystemRuntime?.SessionLockStatusText);
        Assert.Equal("2026-03-20 19:45:00 +01:00", snapshot.SystemRuntime?.SessionLockedSinceText);
        Assert.Equal("LAN", snapshot.NetworkConnectivity?.PrimaryConnectionText);
        Assert.True(snapshot.NetworkConnectivity?.IsCheckpointVpnDetected);
    }

    [Fact]
    public async Task LocalDiagnosticsService_ParsesDeliveryOptimizationPayload()
    {
        const string payload = """
        {
          "machineName": "CLIENT01",
          "capturedAtUtc": "2026-03-20T10:15:00Z",
          "lastSyncText": "2026-03-20 09:44:00Z",
          "registrationSummary": "AzureAdJoined : YES; DeviceId : abc",
          "dsregStatusText": "Device State\nAzureAdJoined : YES",
          "deliveryOptimization": {
            "isAvailable": true,
            "capturedAtUtc": "2026-03-20T10:15:00Z",
            "supportsTimeRangeFiltering": true,
            "dataStartUtc": "2026-03-19T00:00:00Z",
            "dataEndUtc": "2026-03-20T10:00:00Z",
            "sourceStats": [
              { "source": "Http", "bytes": 10485760, "transferCount": 3 },
              { "source": "PeerLan", "bytes": 5242880, "transferCount": 2 }
            ],
            "transfers": [
              { "timestampUtc": "2026-03-20T09:30:00Z", "source": "Http", "bytes": 6291456, "description": "update-a" },
              { "timestampUtc": "2026-03-20T09:45:00Z", "source": "PeerLan", "bytes": 2097152, "description": "update-b" }
            ],
            "notes": [ "Delivery Optimization telemetry loaded." ],
            "currentMetrics": [
              { "name": "DownloadMode", "value": "2" },
              { "name": "HttpBytes", "value": "10485760" }
            ],
            "monthlyMetrics": [
              { "name": "DownloadMode", "value": "2" },
              { "name": "PeerBytes", "value": "7340032" }
            ],
            "configuration": [
              { "name": "DODownloadMode", "value": "2" },
              { "name": "DOGroupID", "value": "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee" }
            ],
            "peerStatuses": [
              {
                "content": "update-b",
                "status": "Downloading",
                "candidateCount": 4,
                "connectedPeerCount": 1,
                "bytesFromPeers": 2097152,
                "bytesFromHttp": 0,
                "details": "PeerType=LAN"
              }
            ],
            "activeJobs": [
              {
                "content": "update-c",
                "status": "Downloading",
                "fileSizeBytes": 104857600,
                "downloadedBytes": 52428800,
                "downloadRateBytesPerSecond": 131072,
                "details": "PeerType=LAN"
              }
            ]
          },
          "notes": ["Collected successfully"]
        }
        """;

        var service = new LocalIntuneDiagnosticsService(
            new FakePowerShellExecutor(new PowershellExecutionResult(0, payload, string.Empty)),
            new HttpClient(),
            new IntuneRuntimeOptions());

        var snapshot = await service.GetSnapshotAsync("CLIENT01", CancellationToken.None);

        var deliveryOptimization = Assert.IsType<DeliveryOptimizationSnapshot>(snapshot.DeliveryOptimization);
        Assert.True(deliveryOptimization.IsAvailable);
        Assert.True(deliveryOptimization.SupportsTimeRangeFiltering);
        Assert.Equal(2, deliveryOptimization.SourceStats.Count);
        Assert.Equal("HTTP/CDN", deliveryOptimization.SourceStats[0].Source);
        Assert.Equal(2, deliveryOptimization.Transfers.Count);
        Assert.Equal("Peer (LAN)", deliveryOptimization.Transfers[0].Source);
        Assert.Equal(2097152L, deliveryOptimization.Transfers[0].Bytes);
        Assert.Equal("Delivery Optimization telemetry loaded.", deliveryOptimization.Notes[0]);
        Assert.Equal("DownloadMode", deliveryOptimization.CurrentMetrics![0].Name);
        Assert.Equal("2", deliveryOptimization.CurrentMetrics[0].Value);
        Assert.Equal("DOGroupID", deliveryOptimization.Configuration![1].Name);
        Assert.Equal("update-b", deliveryOptimization.PeerStatuses![0].Content);
        Assert.Equal(1, deliveryOptimization.PeerStatuses[0].ConnectedPeerCount);
        Assert.Equal("update-c", deliveryOptimization.ActiveJobs![0].Content);
        Assert.Equal(131072L, deliveryOptimization.ActiveJobs[0].DownloadRateBytesPerSecond);
    }

    [Fact]
    public async Task LocalDiagnosticsService_LoadsDeliveryOptimizationOnDemand()
    {
        const string payload = """
        {
          "deliveryOptimization": {
            "isAvailable": true,
            "capturedAtUtc": "2026-03-20T10:15:00Z",
            "supportsTimeRangeFiltering": true,
            "dataStartUtc": "2026-03-19T00:00:00Z",
            "dataEndUtc": "2026-03-20T10:00:00Z",
            "sourceStats": [
              { "source": "Http", "bytes": 10485760, "transferCount": 3 }
            ],
            "transfers": [
              { "timestampUtc": "2026-03-20T09:30:00Z", "source": "Http", "bytes": 6291456, "description": "update-a" }
            ],
            "notes": [ "Delivery Optimization telemetry loaded." ]
          }
        }
        """;

        var service = new LocalIntuneDiagnosticsService(
            new FakePowerShellExecutor(new PowershellExecutionResult(0, payload, string.Empty)),
            new HttpClient(),
            new IntuneRuntimeOptions());

        var deliveryOptimization = await service.GetDeliveryOptimizationSnapshotAsync("CLIENT01", CancellationToken.None);

        var snapshot = Assert.IsType<DeliveryOptimizationSnapshot>(deliveryOptimization);
        Assert.True(snapshot.IsAvailable);
        Assert.Single(snapshot.SourceStats);
        Assert.Equal("HTTP/CDN", snapshot.SourceStats[0].Source);
        Assert.Equal("Delivery Optimization telemetry loaded.", snapshot.Notes[0]);
    }

    [Fact]
    public async Task LocalDiagnosticsService_NormalizesWarningPrefixedSnapshotPayload()
    {
        const string payload = """
        WARNING: Delivery Optimization counters are partially unavailable.
        {
          "machineName": "CLIENT01",
          "capturedAtUtc": "2026-03-20T10:15:00Z",
          "lastSyncText": "2026-03-20 09:44:00Z",
          "registrationSummary": "AzureAdJoined : YES; DeviceId : abc",
          "dsregStatusText": "Device State\nAzureAdJoined : YES",
          "deliveryOptimization": {
            "isAvailable": true,
            "capturedAtUtc": "2026-03-20T10:15:00Z",
            "supportsTimeRangeFiltering": false,
            "sourceStats": [
              { "source": "Http", "bytes": 10485760, "transferCount": 3 }
            ],
            "transfers": [
              { "timestampUtc": "2026-03-20T09:30:00Z", "source": "Http", "bytes": 6291456, "description": "update-a" }
            ],
            "notes": [ "Delivery Optimization telemetry loaded." ]
          },
          "notes": ["Collected successfully"]
        }
        """;

        var service = new LocalIntuneDiagnosticsService(
            new FakePowerShellExecutor(new PowershellExecutionResult(0, payload, string.Empty)),
            new HttpClient(),
            new IntuneRuntimeOptions());

        var snapshot = await service.GetSnapshotAsync("localhost", CancellationToken.None);

        var deliveryOptimization = Assert.IsType<DeliveryOptimizationSnapshot>(snapshot.DeliveryOptimization);
        Assert.True(deliveryOptimization.IsAvailable);
        Assert.Equal("HTTP/CDN", deliveryOptimization.SourceStats[0].Source);
        Assert.Contains(snapshot.Notes, item => item.Contains("additional console text", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LocalDiagnosticsService_ParsesLocalizedDsregBooleanValues()
    {
        const string payload = """
        {
          "machineName": "CLIENT01",
          "capturedAtUtc": "2026-03-20T10:15:00Z",
          "lastSyncText": "2026-03-20 09:44:00Z",
          "registrationSummary": "AzureAdJoined : JA; DomainJoined : NEIN; DeviceId : abc",
          "dsregStatusText": "Device State\nAzureAdJoined : JA\nDomainJoined : NEIN\nAzureAdPrt : JA\nTpmProtected : JA\nDeviceAuthStatus : SUCCESS\nMdmUrl : -",
          "notes": ["Collected successfully"]
        }
        """;

        var service = new LocalIntuneDiagnosticsService(
            new FakePowerShellExecutor(new PowershellExecutionResult(0, payload, string.Empty)),
            new HttpClient(),
            new IntuneRuntimeOptions());

        var snapshot = await service.GetSnapshotAsync("CLIENT01", CancellationToken.None);

        Assert.Contains(snapshot.DsregHighlights, item => item.Contains("[OK] AzureAdJoined: JA", StringComparison.Ordinal));
        Assert.Contains(snapshot.DsregHighlights, item => item.Contains("[OK] AzureAdPrt: JA", StringComparison.Ordinal));
        Assert.Contains(snapshot.DsregHighlights, item => item.Contains("[OK] TpmProtected: JA", StringComparison.Ordinal));
        Assert.DoesNotContain(snapshot.DsregHighlights, item => item.Contains("[Error] AzureAdJoined", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LocalEnrollmentService_PreviewParsesArtifactsAndBlockers()
    {
        const string payload = """
        {
          "canExecute": true,
          "confirmationText": "REENROLL CLIENT01",
          "summary": "Preview found 2 removable artifacts.",
          "blockers": [],
          "steps": ["Remove registry keys", "Run deviceenroller"],
          "artifactsToRemove": [
            {
              "artifactType": "Registry",
              "artifactPath": "HKLM:\\SOFTWARE\\Microsoft\\Enrollments\\11111111-1111-1111-1111-111111111111",
              "description": "Remove stale enrollment",
              "enrollmentId": "11111111-1111-1111-1111-111111111111",
              "isRemovable": true
            }
          ]
        }
        """;

        var service = new LocalIntuneEnrollmentService(new FakePowerShellExecutor(new PowershellExecutionResult(0, payload, string.Empty)));

        var preview = await service.PreviewReenrollAsync("CLIENT01", CancellationToken.None);

        Assert.True(preview.CanExecute);
        Assert.Equal("REENROLL CLIENT01", preview.ConfirmationText);
        Assert.Single(preview.ArtifactsToRemove);
        Assert.Contains("deviceenroller", preview.Steps[1], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LocalEnrollmentService_ParsesEnrollmentUrlsStatus()
    {
        const string payload = """
        {
          "winRmAvailable": true,
          "isAdminContext": true,
          "enrollmentDetected": true,
          "lastSyncText": "2026-03-20 08:00:00Z",
          "registrationSummary": "AzureAdJoined : YES",
          "enrollmentIds": ["11111111-1111-1111-1111-111111111111"],
          "checks": ["Administrative context confirmed."],
          "warnings": [],
          "artifacts": [],
          "enrollmentUrls": {
            "tenantInfoDetected": true,
            "areConfigured": true,
            "areExpected": true,
            "summary": "Enrollment URLs are configured correctly.",
            "checks": ["MdmEnrollmentUrl matches expected Intune discovery endpoint."],
            "warnings": [],
            "enrollmentUrl": "https://enrollment.manage.microsoft.com/enrollmentserver/discovery.svc",
            "termsOfUseUrl": "https://portal.manage.microsoft.com/TermsofUse.aspx",
            "complianceUrl": "https://portal.manage.microsoft.com/?portalAction=Compliance",
            "canRepair": true
          },
          "canTriggerSync": true,
          "canReenroll": true
        }
        """;

        var service = new LocalIntuneEnrollmentService(new FakePowerShellExecutor(new PowershellExecutionResult(0, payload, string.Empty)));

        var status = await service.GetEnrollmentStatusAsync("CLIENT01", CancellationToken.None);

        Assert.True(status.EnrollmentUrls.AreExpected);
        Assert.Equal(EnrollmentUrlTargets.EnrollmentUrl, status.EnrollmentUrls.EnrollmentUrl);
        Assert.Equal(EnrollmentUrlTargets.TermsOfUseUrl, status.EnrollmentUrls.TermsOfUseUrl);
        Assert.Equal(EnrollmentUrlTargets.ComplianceUrl, status.EnrollmentUrls.ComplianceUrl);
    }

    [Fact]
    public async Task LocalEnrollmentService_FixEnrollmentUrls_ReturnsSuccessMessage()
    {
        var service = new LocalIntuneEnrollmentService(new FakePowerShellExecutor(new PowershellExecutionResult(0, "Updated enrollment URLs in 1 CloudDomainJoin tenant info key(s).", string.Empty)));

        var result = await service.FixEnrollmentUrlsAsync("CLIENT01", CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("Updated enrollment URLs", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LocalEnrollmentService_RequiresExplicitConfirmationForExecute()
    {
        var service = new LocalIntuneEnrollmentService(new FakePowerShellExecutor(new PowershellExecutionResult(0, "ignored", string.Empty)));

        var result = await service.ExecuteReenrollAsync("CLIENT01", confirmed: false, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("confirmation_required", result.ErrorCode);
    }

    [Fact]
    public async Task LiveGraphCloudService_UsesExactHostFilterAndReturnsOnlyExactMatches()
    {
        var handler = new RecordingHttpMessageHandler(
            """
            {
              "value": [
                {
                  "id": "managed-1",
                  "deviceName": "CLIENT01",
                  "azureADDeviceId": "aad-1",
                  "userPrincipalName": "user@contoso.com",
                  "operatingSystem": "Windows",
                  "complianceState": "compliant",
                  "lastSyncDateTime": "2026-03-20T08:00:00Z"
                },
                {
                  "id": "managed-2",
                  "deviceName": "CLIENT01-OLD",
                  "azureADDeviceId": "aad-2",
                  "userPrincipalName": "user@contoso.com",
                  "operatingSystem": "Windows",
                  "complianceState": "retired",
                  "lastSyncDateTime": "2026-03-19T08:00:00Z"
                }
              ]
            }
            """);
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://graph.microsoft.com/v1.0/") };
        var service = new LiveGraphCloudManagedDeviceService(httpClient, new FakeAccessTokenProvider());

        var result = await service.FindManagedDeviceByHostAsync("CLIENT01", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("managed-1", result!.ManagedDeviceId);
        Assert.Contains("deviceName eq 'CLIENT01'", handler.LastRequestUri, StringComparison.Ordinal);
        Assert.Equal("Bearer", handler.LastAuthorizationScheme);
        Assert.Equal("fake-token", handler.LastAuthorizationParameter);
    }

    [Fact]
    public async Task LiveGraphDeviceQueryService_UsesExactHostFilterAndMapsManagedDevicePayload()
    {
        var handler = new RecordingHttpMessageHandler(
            """
            {
              "value": [
                {
                  "id": "managed-1",
                  "deviceName": "CLIENT01",
                  "operatingSystem": "Windows",
                  "complianceState": "compliant",
                  "lastSyncDateTime": "2026-03-20T08:00:00Z"
                }
              ]
            }
            """);
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://graph.microsoft.com/v1.0/") };
        var service = new LiveGraphDeviceQueryService(httpClient, new FakeAccessTokenProvider());

        var result = await service.GetDeviceByHostAsync("CLIENT01", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("managed-1", result!.DeviceId);
        Assert.Equal("CLIENT01", result.DeviceName);
        Assert.Equal("Windows", result.Platform);
        Assert.Equal("compliant", result.ComplianceState);
        Assert.Contains("deviceName eq 'CLIENT01'", handler.LastRequestUri, StringComparison.Ordinal);
        Assert.Equal(HttpMethod.Get, handler.LastMethod);
    }

    [Fact]
    public async Task LiveGraphDeviceActionService_SyncActionPostsSyncDeviceRequest()
    {
        var handler = new RecordingHttpMessageHandler(string.Empty, HttpStatusCode.NoContent);
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://graph.microsoft.com/v1.0/") };
        var service = new LiveGraphDeviceActionService(httpClient, new FakeAccessTokenProvider());

        var result = await service.ExecuteActionAsync(new DeviceActionRequest("managed-1", "sync-now"), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("sync-now", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("managedDevices/managed-1/syncDevice", handler.LastRequestUri, StringComparison.Ordinal);
        Assert.Equal(HttpMethod.Post, handler.LastMethod);
        Assert.Equal("Bearer", handler.LastAuthorizationScheme);
        Assert.Equal("fake-token", handler.LastAuthorizationParameter);
    }

    [Fact]
    public async Task LiveGraphDeviceActionService_UnsupportedActionReturnsClearFailure()
    {
        var handler = new RecordingHttpMessageHandler(string.Empty);
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://graph.microsoft.com/v1.0/") };
        var service = new LiveGraphDeviceActionService(httpClient, new FakeAccessTokenProvider());

        var result = await service.ExecuteActionAsync(new DeviceActionRequest("managed-1", "wipe"), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("cloud_action_unavailable", result.ErrorCode);
        Assert.Contains("public release", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(string.Empty, handler.LastRequestUri);
    }

    [Fact]
    public async Task WinRmLocalDeviceActionService_EnableWinRm_UsesDotNetWmiBootstrap()
    {
        var executor = new RecordingPowerShellExecutor(new PowershellExecutionResult(0, "ok", string.Empty));
        var service = new WinRmLocalDeviceActionService(executor);

        var result = await service.ExecuteLocalActionAsync("CLIENT01", "enable-winrm", null, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(Environment.MachineName, executor.LastHost);
        Assert.Contains("System.Management.ManagementClass", executor.LastScriptBody, StringComparison.Ordinal);
        Assert.Contains("GetMethodParameters('Create')", executor.LastScriptBody, StringComparison.Ordinal);
        Assert.DoesNotContain("Invoke-WmiMethod", executor.LastScriptBody, StringComparison.Ordinal);
        Assert.DoesNotContain("Get-Date", executor.LastScriptBody, StringComparison.Ordinal);
        Assert.DoesNotContain("Start-Sleep", executor.LastScriptBody, StringComparison.Ordinal);
        Assert.DoesNotContain("Test-WSMan", executor.LastScriptBody, StringComparison.Ordinal);
        Assert.Contains("System.Net.Sockets.TcpClient", executor.LastScriptBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MockAuthService_LoginCreatesReusableSessionAndToken()
    {
        var service = new MockAuthService(new IntuneRuntimeOptions { TenantId = "contoso.onmicrosoft.com" });

        var session = await service.LoginAsync(CancellationToken.None);
        var currentSession = await service.GetCurrentSessionAsync(CancellationToken.None);
        var token = await service.GetAccessTokenAsync(CancellationToken.None);

        Assert.NotNull(currentSession);
        Assert.Equal(session.UserPrincipalName, currentSession!.UserPrincipalName);
        Assert.False(string.IsNullOrWhiteSpace(token));
        Assert.True(session.ExpiresAt > DateTimeOffset.UtcNow);
    }

    [Fact]
    public void AddIntuneRuntime_RegistersDemoServices_WhenDemoModeIsRequested()
    {
        var services = new ServiceCollection();
        services.AddIntuneRuntime(new IntuneRuntimeOptions { Mode = IntuneRuntimeMode.Demo });

        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(IHostConnectivityService) && descriptor.ImplementationType?.Name == "DemoHostConnectivityService");
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(ILocalIntuneDiagnosticsService) && descriptor.ImplementationType?.Name == "DemoLocalIntuneDiagnosticsService");
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(ILocalDeviceActionService) && descriptor.ImplementationType?.Name == "DemoLocalDeviceActionService");
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(ICloudManagedDeviceService) && descriptor.ImplementationType?.Name == "DemoCloudManagedDeviceService");
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(IAuthService) && descriptor.ImplementationFactory is not null);
    }

    [Fact]
    public void AddIntuneRuntime_RegistersDisabledDeviceQueryOnly_WhenLiveModeHasNoClientId()
    {
        var services = new ServiceCollection();
        services.AddIntuneRuntime(new IntuneRuntimeOptions { Mode = IntuneRuntimeMode.Live, ClientId = "" });

        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(IDeviceQueryService) && descriptor.ImplementationType == typeof(DisabledDeviceQueryService));
        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(IAuthService));
        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(IDeviceActionService));
        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(ICloudManagedDeviceService));
    }

    [Fact]
    public void AddIntuneRuntime_UsesClientCenterLibBackendByDefault()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIntuneRuntime(new IntuneRuntimeOptions { Mode = IntuneRuntimeMode.Live, ClientId = "" });

        using var serviceProvider = services.BuildServiceProvider();
        var service = serviceProvider.GetRequiredService<IMecmClientService>();

        Assert.Equal("SccmClientCenterMecmService", service.GetType().Name);
    }

    [Fact]
    public void AddIntuneRuntime_UsesPowerShellBackendWhenConfigured()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIntuneRuntime(new IntuneRuntimeOptions
        {
            Mode = IntuneRuntimeMode.Live,
            ClientId = "",
            MecmBackend = MecmBackendMode.PowerShell
        });

        using var serviceProvider = services.BuildServiceProvider();
        var service = serviceProvider.GetRequiredService<IMecmClientService>();

        Assert.Equal("MecmClientService", service.GetType().Name);
    }

    [Fact]
    public void DemoDataCatalog_UsesConfiguredDemoIdentity()
    {
        var catalog = new DemoDataCatalog(new IntuneRuntimeOptions
        {
            Mode = IntuneRuntimeMode.Demo,
            DemoHostName = "DEMO-WS-01",
            DemoUserPrincipalName = "operator@demo.example",
            DemoConnectedUsersText = @"DEMO\operator, DEMO\analyst"
        });

        Assert.Equal("DEMO-WS-01", catalog.DemoHostName);
        Assert.Equal("operator@demo.example", catalog.GetAuthSession().UserPrincipalName);
        Assert.Equal("DEMO\\operator", catalog.GetConnectedUsers()[0]);
        Assert.Equal("DEMO-WS-01", catalog.CreateLocalSnapshot(null).Host);
    }

    [Fact]
    public async Task LocalDiagnosticsService_AnalyzesMdmAdminEvents_AndResolvesLocalFailure()
    {
        const string payload = """
        [
          {
            "timeCreated": "2026-03-20T10:15:00Z",
            "recordId": 42,
            "id": 404,
            "level": "Fehler",
            "levelValue": 2,
            "provider": "Microsoft-Windows-DeviceManagement-Enterprise-Diagnostics-Provider",
            "message": "MDM PolicyManager: Set policy string, Policy: Homepage, Area: Browser, EnrollmentID requesting merge: (11111111-1111-1111-1111-111111111111), Result:(0x80070002), CSP URI: ./Device/Vendor/MSFT/Policy/Config/Browser/Homepage",
            "xml": "<Event><EventData><Data Name='Policy'>Homepage</Data><Data Name='Area'>Browser</Data><Data Name='EnrollmentId'>11111111-1111-1111-1111-111111111111</Data><Data Name='Result'>0x80070002</Data><Data Name='CspUri'>./Device/Vendor/MSFT/Policy/Config/Browser/Homepage</Data></EventData></Event>"
          }
        ]
        """;

        var service = new LocalIntuneDiagnosticsService(
            new FakePowerShellExecutor(new PowershellExecutionResult(0, payload, string.Empty)),
            new HttpClient(),
            new IntuneRuntimeOptions());

        var entries = await service.GetMdmAdminEventsAsync("CLIENT01", 50, CancellationToken.None);

        var entry = Assert.Single(entries);
        Assert.True(entry.IsFailure);
        Assert.Equal(MdmEventSeverity.Error, entry.Severity);
        Assert.Equal("0x80070002", entry.ResultCode, ignoreCase: true);
        Assert.Equal("Homepage", entry.PolicyName);
        Assert.Equal("Browser", entry.Area);
        Assert.Equal("./Device/Vendor/MSFT/Policy/Config/Browser/Homepage", entry.CspUri);
        Assert.Contains("cannot find the file", entry.ResolvedError, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("referenced file", entry.RecommendedAction, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LocalDiagnosticsService_AcceptsNonIsoEventTimeCreated()
    {
        const string payload = """
        [
          {
            "timeCreated": "03/20/2026 10:15:00",
            "recordId": 43,
            "id": 201,
            "level": "Information",
            "levelValue": 4,
            "provider": "Microsoft-Windows-DeviceManagement-Enterprise-Diagnostics-Provider",
            "message": "MDM PolicyManager: policy applied successfully.",
            "xml": "<Event><EventData><Data Name='Result'>0x00000000</Data></EventData></Event>"
          }
        ]
        """;

        var service = new LocalIntuneDiagnosticsService(
            new FakePowerShellExecutor(new PowershellExecutionResult(0, payload, string.Empty)),
            new HttpClient(),
            new IntuneRuntimeOptions());

        var entries = await service.GetMdmAdminEventsAsync("CLIENT01", 50, CancellationToken.None);

        var entry = Assert.Single(entries);
        Assert.NotNull(entry.TimeCreated);
        Assert.False(entry.IsFailure);
        Assert.Equal(MdmEventSeverity.Information, entry.Severity);
    }

    [Fact]
    public async Task LocalDiagnosticsService_UsesNumericEventLevel_ForSeverity()
    {
        const string payload = """
        [
          {
            "timeCreated": "2026-03-20T10:15:00Z",
            "recordId": 44,
            "id": 305,
            "level": "Warnung",
            "levelValue": 3,
            "provider": "Microsoft-Windows-DeviceManagement-Enterprise-Diagnostics-Provider",
            "message": "A policy warning was logged.",
            "xml": "<Event><System><Level>3</Level></System><EventData><Data Name='Result'>0x00000000</Data></EventData></Event>"
          }
        ]
        """;

        var service = new LocalIntuneDiagnosticsService(
            new FakePowerShellExecutor(new PowershellExecutionResult(0, payload, string.Empty)),
            new HttpClient(),
            new IntuneRuntimeOptions());

        var entries = await service.GetMdmAdminEventsAsync("CLIENT01", 50, CancellationToken.None);

        var entry = Assert.Single(entries);
        Assert.Equal(MdmEventSeverity.Warning, entry.Severity);
    }

    [Fact]
    public async Task LocalDiagnosticsService_KeepsInfoLevelSeverity_ForInfoLevelFailures()
    {
        const string payload = """
        [
          {
            "timeCreated": "2026-03-20T10:15:00Z",
            "recordId": 45,
            "id": 404,
            "level": "Informationen",
            "levelValue": 4,
            "provider": "Microsoft-Windows-DeviceManagement-Enterprise-Diagnostics-Provider",
            "message": "Policy application failed. Result:(0x80070002)",
            "xml": "<Event><System><Level>4</Level></System><EventData><Data Name='HexInt1'>0x80070002</Data></EventData></Event>"
          }
        ]
        """;

        var service = new LocalIntuneDiagnosticsService(
            new FakePowerShellExecutor(new PowershellExecutionResult(0, payload, string.Empty)),
            new HttpClient(),
            new IntuneRuntimeOptions());

        var entries = await service.GetMdmAdminEventsAsync("CLIENT01", 50, CancellationToken.None);

        var entry = Assert.Single(entries);
        Assert.True(entry.IsFailure);
        Assert.Equal(MdmEventSeverity.Information, entry.Severity);
    }

    [Fact]
    public async Task LocalIntuneActionService_MdmSyncNow_Succeeds()
    {
        var service = new LocalIntuneActionService(new FakePowerShellExecutor(new PowershellExecutionResult(0, "ok", string.Empty)));

        var result = await service.MdmSyncNowAsync("CLIENT01", CancellationToken.None);

        Assert.True(result.Success);
    }

    [Fact]
    public async Task LocalIntuneActionService_ParsesMdmSyncStatus()
    {
        const string payload = """
        [
          {
            "timeCreated": "2026-03-20T10:15:00Z",
            "eventId": 209,
            "message": "Session ended with status 0x80072f78"
          }
        ]
        """;

        var service = new LocalIntuneActionService(new FakePowerShellExecutor(new PowershellExecutionResult(0, payload, string.Empty)));

        var entries = await service.GetMdmSyncStatusAsync("CLIENT01", 20, CancellationToken.None);

        var entry = Assert.Single(entries);
        Assert.Equal(209, entry.EventId);
        Assert.Equal("0x80072f78", entry.ResultCode, ignoreCase: true);
    }

    [Fact]
    public async Task LocalIntuneActionService_MdmSyncStatus_HandlesEmptyOutput()
    {
        var service = new LocalIntuneActionService(new FakePowerShellExecutor(new PowershellExecutionResult(0, string.Empty, string.Empty)));

        var entries = await service.GetMdmSyncStatusAsync("CLIENT01", 20, CancellationToken.None);

        Assert.Empty(entries);
    }

    [Fact]
    public async Task LocalIntuneActionService_GenerateMdmDiagnostics_ResolvesHtmlFallback()
    {
        const string generatePayload = """
        {
          "reportDirectory": "C:\\Temp\\Mdm",
          "xmlPath": "C:\\Temp\\Mdm\\MDMDiagReport.xml",
          "htmlPath": "C:\\Temp\\Mdm\\MDMDiagHTMLReport.html"
        }
        """;
        const string parsePayload = """
        {
          "reportDirectory": "C:\\Temp\\Mdm",
          "xmlPath": "C:\\Temp\\Mdm\\MDMDiagReport.xml",
          "htmlPath": "C:\\Temp\\Mdm\\MDMDiagHTMLReport.html",
          "xmlNodeCount": 41,
          "htmlLineCount": 120
        }
        """;

        var service = new LocalIntuneActionService(new QueuedFakePowerShellExecutor(
            new PowershellExecutionResult(0, generatePayload, string.Empty),
            new PowershellExecutionResult(0, parsePayload, string.Empty)));

        var report = await service.GenerateMdmDiagnosticsReportAsync("CLIENT01", "C:\\Temp\\Mdm", CancellationToken.None);

        Assert.Equal("C:\\Temp\\Mdm\\MDMDiagHTMLReport.html", report.HtmlPath);
        Assert.Equal(41, report.XmlNodeCount);
    }

    [Fact]
    public async Task LocalIntuneActionService_GenerateMdmDiagnostics_UsesLocalDriverForRemoteGpResultAndParse()
    {
        const string generatePayload = """
        {
          "reportDirectory": "C:\\Temp\\Mdm",
          "xmlPath": "C:\\Temp\\Mdm\\MDMDiagReport.xml",
          "htmlPath": "C:\\Temp\\Mdm\\MDMDiagReport.html"
        }
        """;
        const string parsePayload = """
        {
          "reportDirectory": "C:\\Temp\\Mdm",
          "xmlPath": "C:\\Temp\\Mdm\\MDMDiagReport.xml",
          "htmlPath": "C:\\Temp\\Mdm\\MDMDiagReport.html",
          "xmlNodeCount": 22,
          "htmlLineCount": 90
        }
        """;

        var executor = new RecordingQueuedPowerShellExecutor(
            new PowershellExecutionResult(0, generatePayload, string.Empty),
            new PowershellExecutionResult(0, parsePayload, string.Empty));
        var service = new LocalIntuneActionService(executor);

        var report = await service.GenerateMdmDiagnosticsReportAsync("CLIENT01", "C:\\Temp\\Mdm", CancellationToken.None);

        Assert.Equal(2, executor.Hosts.Count);
        Assert.Equal("localhost", executor.Hosts[0]);
        Assert.Equal("CLIENT01", executor.Hosts[1]);
        Assert.Contains("New-PSSession -ComputerName $normalizedHost", executor.ScriptBodies[0], StringComparison.Ordinal);
        Assert.Contains("Invoke-IccGpResultPair -HtmlPath $gph -XmlPath $gpx -ComputerName $normalizedHost", executor.ScriptBodies[0], StringComparison.Ordinal);
        Assert.DoesNotContain("Start-Job", executor.ScriptBodies[0], StringComparison.Ordinal);
        Assert.Contains("Invoke-IccGpResult -DestinationPath $HtmlPath -Format 'Html'", executor.ScriptBodies[0], StringComparison.Ordinal);
        Assert.Contains("$arguments += @('/Scope', 'Computer')", executor.ScriptBodies[0], StringComparison.Ordinal);
        Assert.Contains("Remove-IccMdmReportFiles -Directory $outDir", executor.ScriptBodies[0], StringComparison.Ordinal);
        Assert.Contains("LastWriteTimeUtc", executor.ScriptBodies[0], StringComparison.Ordinal);
        Assert.Contains("Invoke-IccRemoteGpResultFallback", executor.ScriptBodies[0], StringComparison.Ordinal);
        Assert.Equal(22, report.XmlNodeCount);
    }

    [Fact]
    public async Task LocalIntuneActionService_GenerateIntunePolicyResult_IncludesTimings()
    {
        const string generatePayload = """
        {
          "reportDirectory": "C:\\Temp\\Mdm",
          "xmlPath": "C:\\Temp\\Mdm\\MDMDiagReport.xml",
          "htmlPath": "C:\\Temp\\Mdm\\MDMDiagReport.html"
        }
        """;
        const string parsePayload = """
        {
          "reportDirectory": "C:\\Temp\\Mdm",
          "xmlPath": "C:\\Temp\\Mdm\\MDMDiagReport.xml",
          "htmlPath": "C:\\Temp\\Mdm\\MDMDiagReport.html",
          "xmlNodeCount": 22,
          "htmlLineCount": 90
        }
        """;
        const string overlayPayload = """
        {
          "entries": [],
          "providers": []
        }
        """;

        var executor = new RecordingQueuedPowerShellExecutor(
            new PowershellExecutionResult(0, generatePayload, string.Empty),
            new PowershellExecutionResult(0, parsePayload, string.Empty),
            new PowershellExecutionResult(0, overlayPayload, string.Empty));
        var service = new LocalIntuneActionService(executor);

        var report = await service.GenerateIntunePolicyResultAsync("CLIENT01", "C:\\Temp\\Policy", CancellationToken.None);

        Assert.NotEmpty(report.Timings);
        Assert.Contains(report.Timings, timing => timing.Contains("Local policy overlay collection", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(report.Timings, timing => timing.Contains("Policy merge", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task LocalIntuneActionService_ParseMdmDiagnostics_UsesLocalHostWhenDirectoryExistsLocally()
    {
        var reportDir = Path.Combine(Path.GetTempPath(), $"icc-mdm-parse-{Guid.NewGuid():N}");
        Directory.CreateDirectory(reportDir);
        try
        {
            var parsePayload = $$"""
                {
                  "reportDirectory": "{{reportDir.Replace("\\", "\\\\", StringComparison.Ordinal)}}",
                  "xmlPath": "{{Path.Combine(reportDir, "MDMDiagReport.xml").Replace("\\", "\\\\", StringComparison.Ordinal)}}",
                  "htmlPath": "{{Path.Combine(reportDir, "MDMDiagReport.html").Replace("\\", "\\\\", StringComparison.Ordinal)}}",
                  "xmlNodeCount": 5,
                  "htmlLineCount": 2
                }
                """;
            var executor = new RecordingPowerShellExecutor(new PowershellExecutionResult(0, parsePayload, string.Empty));
            var service = new LocalIntuneActionService(executor);

            _ = await service.ParseMdmDiagnosticsReportAsync("CLIENT01", reportDir, CancellationToken.None);

            Assert.Equal("localhost", executor.LastHost);
        }
        finally
        {
            Directory.Delete(reportDir, recursive: true);
        }
    }

    [Fact]
    public async Task LocalIntuneActionService_ParseMdmDiagnostics_PrefersNewestHtmlVariant()
    {
        var reportDir = Path.Combine(Path.GetTempPath(), $"icc-mdm-html-order-{Guid.NewGuid():N}");
        Directory.CreateDirectory(reportDir);
        try
        {
            var parsePayload = $$"""
                {
                  "reportDirectory": "{{reportDir.Replace("\\", "\\\\", StringComparison.Ordinal)}}",
                  "xmlPath": "{{Path.Combine(reportDir, "MDMDiagReport.xml").Replace("\\", "\\\\", StringComparison.Ordinal)}}",
                  "htmlPath": "{{Path.Combine(reportDir, "MDMDiagHTMLReport.html").Replace("\\", "\\\\", StringComparison.Ordinal)}}",
                  "xmlNodeCount": 5,
                  "htmlLineCount": 2
                }
                """;
            var executor = new RecordingPowerShellExecutor(new PowershellExecutionResult(0, parsePayload, string.Empty));
            var service = new LocalIntuneActionService(executor);

            _ = await service.ParseMdmDiagnosticsReportAsync("CLIENT01", reportDir, CancellationToken.None);

            Assert.Contains("LastWriteTimeUtc", executor.LastScriptBody, StringComparison.Ordinal);
            Assert.Contains("Sort-Object", executor.LastScriptBody, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(reportDir, recursive: true);
        }
    }

    [Fact]
    public async Task LocalIntuneActionService_ParseMdmDiagnostics_AllowsHtmlFallbackWhenXmlIsMissing()
    {
        var reportDir = Path.Combine(Path.GetTempPath(), $"icc-mdm-htmlonly-{Guid.NewGuid():N}");
        Directory.CreateDirectory(reportDir);
        try
        {
            var htmlPath = Path.Combine(reportDir, "MDMDiagHTMLReport.html");
            var parsePayload = $$"""
                {
                  "reportDirectory": "{{reportDir.Replace("\\", "\\\\", StringComparison.Ordinal)}}",
                  "xmlPath": "",
                  "htmlPath": "{{htmlPath.Replace("\\", "\\\\", StringComparison.Ordinal)}}",
                  "xmlNodeCount": 0,
                  "htmlLineCount": 17
                }
                """;
            var executor = new RecordingPowerShellExecutor(new PowershellExecutionResult(0, parsePayload, string.Empty));
            var service = new LocalIntuneActionService(executor);

            var report = await service.ParseMdmDiagnosticsReportAsync("CLIENT01", reportDir, CancellationToken.None);

            Assert.Equal(string.Empty, report.XmlPath);
            Assert.Equal(htmlPath, report.HtmlPath);
            Assert.Equal(17, report.HtmlLineCount);
            Assert.Equal("localhost", executor.LastHost);
        }
        finally
        {
            Directory.Delete(reportDir, recursive: true);
        }
    }

    [Fact]
    public async Task LocalIntuneActionService_ParseIntunePolicyResult_MapsXmlScopeStatusAndFields()
    {
        var reportDir = Path.Combine(Path.GetTempPath(), $"icc-policy-xml-{Guid.NewGuid():N}");
        var exportDir = Path.Combine(reportDir, "export");
        Directory.CreateDirectory(reportDir);
        Directory.CreateDirectory(exportDir);
        var xmlPath = Path.Combine(reportDir, "MDMDiagReport.xml");
        var htmlPath = Path.Combine(reportDir, "MDMDiagReport.html");
        await File.WriteAllTextAsync(xmlPath, """
            <Report>
              <Policy>
                <Scope>Device</Scope>
                <Area>Defender</Area>
                <SettingName>AllowRealtimeMonitoring</SettingName>
                <OmaUri>./Device/Vendor/MSFT/Policy/Config/Defender/AllowRealtimeMonitoring</OmaUri>
                <CurrentValue>1</CurrentValue>
                <ResultCode>0</ResultCode>
              </Policy>
              <Policy>
                <Scope>User</Scope>
                <Area>Browser</Area>
                <SettingName>Homepage</SettingName>
                <OmaUri>./User/Vendor/MSFT/Policy/Config/Browser/Homepage</OmaUri>
                <CurrentValue>https://contoso</CurrentValue>
                <ResultCode>0x87D1FDE8</ResultCode>
              </Policy>
            </Report>
            """);
        await File.WriteAllTextAsync(htmlPath, "<html><body>fallback</body></html>");

        var parsePayload = $$"""
            {
              "reportDirectory": "{{reportDir.Replace("\\", "\\\\", StringComparison.Ordinal)}}",
              "xmlPath": "{{xmlPath.Replace("\\", "\\\\", StringComparison.Ordinal)}}",
              "htmlPath": "{{htmlPath.Replace("\\", "\\\\", StringComparison.Ordinal)}}",
              "xmlNodeCount": 18,
              "htmlLineCount": 1
            }
            """;

        var service = new LocalIntuneActionService(new FakePowerShellExecutor(new PowershellExecutionResult(0, parsePayload, string.Empty)));
        var report = await service.ParseIntunePolicyResultAsync("CLIENT01", reportDir, exportDir, CancellationToken.None);

        Assert.Equal("Xml", report.Source);
        Assert.Equal(2, report.Summary.TotalCount);
        Assert.Equal(1, report.Summary.AppliedCount);
        Assert.Equal(1, report.Summary.FailedCount);
        Assert.Equal(1, report.Summary.DeviceCount);
        Assert.Equal(1, report.Summary.UserCount);
        Assert.Contains(report.Entries, entry => entry.Scope == "Device" && entry.Status == "Applied");
        Assert.Contains(report.Entries, entry => entry.Scope == "User" && entry.Status == "Failed");
    }

    [Fact]
    public async Task LocalIntuneActionService_ParseIntunePolicyResult_UsesPolicyManagerConfigSourceStructure()
    {
        var reportDir = Path.Combine(Path.GetTempPath(), $"icc-policy-manager-{Guid.NewGuid():N}");
        var exportDir = Path.Combine(reportDir, "export");
        Directory.CreateDirectory(reportDir);
        Directory.CreateDirectory(exportDir);
        var xmlPath = Path.Combine(reportDir, "MDMDiagReport.xml");
        var htmlPath = Path.Combine(reportDir, "MDMDiagReport.html");
        await File.WriteAllTextAsync(xmlPath, """
            <MDMEnterpriseDiagnosticsReport>
              <PolicyManager>
                <ConfigSource>
                  <EnrollmentId>11111111-1111-1111-1111-111111111111</EnrollmentId>
                  <PolicyScope>
                    <PolicyScope>Device</PolicyScope>
                    <Area>
                      <PolicyAreaName>Defender</PolicyAreaName>
                      <AllowRealtimeMonitoring>1</AllowRealtimeMonitoring>
                      <AllowRealtimeMonitoring_LastWrite>2026-03-27T19:00:00Z</AllowRealtimeMonitoring_LastWrite>
                    </Area>
                  </PolicyScope>
                  <PolicyScope>
                    <PolicyScope>S-1-5-21-111-222-333-1001</PolicyScope>
                    <Area>
                      <PolicyAreaName>Browser</PolicyAreaName>
                      <Homepage>https://contoso</Homepage>
                    </Area>
                  </PolicyScope>
                </ConfigSource>
                <currentPolicies>
                  <PolicyScope>Device</PolicyScope>
                  <CurrentPolicyValues>
                    <PolicyAreaName>Defender</PolicyAreaName>
                    <AllowRealtimeMonitoring_WinningProvider>aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa</AllowRealtimeMonitoring_WinningProvider>
                  </CurrentPolicyValues>
                </currentPolicies>
                <currentPolicies>
                  <PolicyScope>S-1-5-21-111-222-333-1001</PolicyScope>
                  <CurrentPolicyValues>
                    <PolicyAreaName>Browser</PolicyAreaName>
                    <Homepage_WinningProvider>aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa</Homepage_WinningProvider>
                  </CurrentPolicyValues>
                </currentPolicies>
              </PolicyManager>
              <PolicyManagerMeta>
                <AreaMetadata>
                  <PolicyAreaName>Defender</PolicyAreaName>
                  <PolicyMetadata>
                    <PolicyName>AllowRealtimeMonitoring</PolicyName>
                    <RegKeyPathRedirect>Software\Policies\Microsoft\Windows Defender</RegKeyPathRedirect>
                  </PolicyMetadata>
                </AreaMetadata>
              </PolicyManagerMeta>
            </MDMEnterpriseDiagnosticsReport>
            """);
        await File.WriteAllTextAsync(htmlPath, "<html><body>fallback</body></html>");

        var parsePayload = $$"""
            {
              "reportDirectory": "{{reportDir.Replace("\\", "\\\\", StringComparison.Ordinal)}}",
              "xmlPath": "{{xmlPath.Replace("\\", "\\\\", StringComparison.Ordinal)}}",
              "htmlPath": "{{htmlPath.Replace("\\", "\\\\", StringComparison.Ordinal)}}",
              "xmlNodeCount": 42,
              "htmlLineCount": 1
            }
            """;

        var service = new LocalIntuneActionService(new FakePowerShellExecutor(new PowershellExecutionResult(0, parsePayload, string.Empty)));
        var report = await service.ParseIntunePolicyResultAsync("CLIENT01", reportDir, exportDir, CancellationToken.None);

        Assert.Equal("Xml", report.Source);
        Assert.Equal(2, report.Summary.TotalCount);
        Assert.Equal(2, report.Summary.AppliedCount);
        Assert.Equal(1, report.Summary.DeviceCount);
        Assert.Equal(1, report.Summary.UserCount);
        Assert.Equal(0, report.Summary.UnknownScopeCount);
        Assert.Contains(report.Entries, entry => entry.Scope == "Device" && entry.Area == "Defender" && entry.SettingName == "AllowRealtimeMonitoring" && entry.Status == "Applied");
        Assert.Contains(report.Entries, entry => entry.Scope == "User" && entry.Area == "Browser" && entry.SettingName == "Homepage" && entry.Status == "Applied");
        Assert.Contains(report.Entries, entry => entry.SettingName == "AllowRealtimeMonitoring" && entry.OmaUri.Contains(@"Software\Policies", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task LocalIntuneActionService_ParseIntunePolicyResult_DoesNotTreatMdmMetadataPathAsHybridSource()
    {
        var reportDir = Path.Combine(Path.GetTempPath(), $"icc-policy-manager-filter-{Guid.NewGuid():N}");
        var exportDir = Path.Combine(reportDir, "export");
        Directory.CreateDirectory(reportDir);
        Directory.CreateDirectory(exportDir);
        var xmlPath = Path.Combine(reportDir, "MDMDiagReport.xml");
        var htmlPath = Path.Combine(reportDir, "MDMDiagReport.html");
        await File.WriteAllTextAsync(xmlPath, """
            <MDMEnterpriseDiagnosticsReport>
              <PolicyManager>
                <ConfigSource>
                  <EnrollmentId>11111111-1111-1111-1111-111111111111</EnrollmentId>
                  <PolicyScope>
                    <PolicyScope>Device</PolicyScope>
                    <Area>
                      <PolicyAreaName>Defender</PolicyAreaName>
                      <AllowRealtimeMonitoring>1</AllowRealtimeMonitoring>
                    </Area>
                  </PolicyScope>
                </ConfigSource>
                <currentPolicies>
                  <PolicyScope>Device</PolicyScope>
                  <CurrentPolicyValues>
                    <PolicyAreaName>Defender</PolicyAreaName>
                    <AllowRealtimeMonitoring_WinningProvider>aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa</AllowRealtimeMonitoring_WinningProvider>
                  </CurrentPolicyValues>
                </currentPolicies>
              </PolicyManager>
              <PolicyManagerMeta>
                <AreaMetadata>
                  <PolicyAreaName>Defender</PolicyAreaName>
                  <PolicyMetadata>
                    <PolicyName>AllowRealtimeMonitoring</PolicyName>
                    <RegKeyPathRedirect>Software\Policies\Microsoft\Windows Defender</RegKeyPathRedirect>
                  </PolicyMetadata>
                </AreaMetadata>
              </PolicyManagerMeta>
            </MDMEnterpriseDiagnosticsReport>
            """);
        await File.WriteAllTextAsync(htmlPath, "<html><body>fallback</body></html>");

        var parsePayload = $$"""
            {
              "reportDirectory": "{{reportDir.Replace("\\", "\\\\", StringComparison.Ordinal)}}",
              "xmlPath": "{{xmlPath.Replace("\\", "\\\\", StringComparison.Ordinal)}}",
              "htmlPath": "{{htmlPath.Replace("\\", "\\\\", StringComparison.Ordinal)}}",
              "xmlNodeCount": 24,
              "htmlLineCount": 1
            }
            """;

        var service = new LocalIntuneActionService(new FakePowerShellExecutor(new PowershellExecutionResult(0, parsePayload, string.Empty)));
        var report = await service.ParseIntunePolicyResultAsync("CLIENT01", reportDir, exportDir, CancellationToken.None);

        var html = await File.ReadAllTextAsync(report.ExportHtmlPath);
        Assert.Contains("data-entry-row=\"true\" data-kind=\"mdm-only\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("data-entry-row=\"true\" data-kind=\"hybrid\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LocalIntuneActionService_ParseIntunePolicyResult_MarksDuplicateAndWinningSourceForGpoOverlap()
    {
        var reportDir = Path.Combine(Path.GetTempPath(), $"icc-policy-conflict-{Guid.NewGuid():N}");
        var exportDir = Path.Combine(reportDir, "export");
        Directory.CreateDirectory(reportDir);
        Directory.CreateDirectory(exportDir);
        var xmlPath = Path.Combine(reportDir, "MDMDiagReport.xml");
        var htmlPath = Path.Combine(reportDir, "MDMDiagReport.html");
        await File.WriteAllTextAsync(xmlPath, """
            <MDMEnterpriseDiagnosticsReport>
              <PolicyManager>
                <ConfigSource>
                  <PolicyScope>
                    <PolicyScope>Device</PolicyScope>
                    <Area>
                      <PolicyAreaName>ADMX_ControlPanelDisplay</PolicyAreaName>
                      <CPL_Personalization_NoChangingLockScreen>&lt;enabled/&gt;</CPL_Personalization_NoChangingLockScreen>
                    </Area>
                  </PolicyScope>
                </ConfigSource>
                <currentPolicies>
                  <PolicyScope>Device</PolicyScope>
                  <CurrentPolicyValues>
                    <PolicyAreaName>ADMX_ControlPanelDisplay</PolicyAreaName>
                    <CPL_Personalization_NoChangingLockScreen_WinningProvider>aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa</CPL_Personalization_NoChangingLockScreen_WinningProvider>
                  </CurrentPolicyValues>
                </currentPolicies>
              </PolicyManager>
              <PolicyManagerMeta>
                <AreaMetadata>
                  <PolicyAreaName>ADMX_ControlPanelDisplay</PolicyAreaName>
                  <PolicyMetadata>
                    <PolicyName>CPL_Personalization_NoChangingLockScreen</PolicyName>
                    <RegKeyPathRedirect>Software\Policies\Microsoft\Windows\Personalization</RegKeyPathRedirect>
                  </PolicyMetadata>
                </AreaMetadata>
              </PolicyManagerMeta>
            </MDMEnterpriseDiagnosticsReport>
            """);
        await File.WriteAllTextAsync(htmlPath, "<html><body>fallback</body></html>");

        var parsePayload = $$"""
            {
              "reportDirectory": "{{reportDir.Replace("\\", "\\\\", StringComparison.Ordinal)}}",
              "xmlPath": "{{xmlPath.Replace("\\", "\\\\", StringComparison.Ordinal)}}",
              "htmlPath": "{{htmlPath.Replace("\\", "\\\\", StringComparison.Ordinal)}}",
              "xmlNodeCount": 32,
              "htmlLineCount": 1
            }
            """;
        var overlayPayload = """
            [
              {
                "scope": "Device",
                "area": "Microsoft",
                "settingName": "CPL_Personalization_NoChangingLockScreen",
                "omaUri": "HKLM\\Software\\Policies\\Microsoft\\Windows\\Personalization",
                "currentValue": "1",
                "status": "Applied",
                "resultCode": "",
                "source": "GroupPolicy",
                "winningSource": "GroupPolicy"
              }
            ]
            """;

        var service = new LocalIntuneActionService(new QueuedFakePowerShellExecutor(
            new PowershellExecutionResult(0, parsePayload, string.Empty),
            new PowershellExecutionResult(0, overlayPayload, string.Empty)));
        var report = await service.ParseIntunePolicyResultAsync("CLIENT01", reportDir, exportDir, CancellationToken.None);

        Assert.Contains("LocalPolicyOverlay", report.Source, StringComparison.Ordinal);
        Assert.Equal(2, report.Summary.TotalCount);
        Assert.Equal(2, report.Summary.DuplicateCount);
        Assert.Equal(1, report.Summary.ConflictCount);
        Assert.Contains(report.Entries, entry =>
            entry.SettingName == "CPL_Personalization_NoChangingLockScreen" &&
            entry.Source == "Mdm" &&
            entry.IsDuplicate &&
            entry.WinningSource == "Mdm" &&
            entry.MdmPath.Contains("./Device/Vendor/MSFT/Policy/Config/ADMX_ControlPanelDisplay/CPL_Personalization_NoChangingLockScreen", StringComparison.OrdinalIgnoreCase) &&
            entry.GpoPath.Contains(@"Software\Policies\Microsoft\Windows\Personalization", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(report.Entries, entry =>
            entry.SettingName == "CPL_Personalization_NoChangingLockScreen" &&
            entry.Source == "GroupPolicy" &&
            entry.IsDuplicate &&
            entry.WinningSource == "Mdm" &&
            entry.GpoCategoryPath.StartsWith(@"Administrative Templates\", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task LocalIntuneActionService_ParseIntunePolicyResult_ShowsConflictBadgeWhenMdmAndGpoValuesDiffer()
    {
        var reportDir = Path.Combine(Path.GetTempPath(), $"icc-policy-valueconflict-{Guid.NewGuid():N}");
        var exportDir = Path.Combine(reportDir, "export");
        Directory.CreateDirectory(reportDir);
        Directory.CreateDirectory(exportDir);
        var xmlPath = Path.Combine(reportDir, "MDMDiagReport.xml");
        var htmlPath = Path.Combine(reportDir, "MDMDiagReport.html");
        await File.WriteAllTextAsync(xmlPath, """
            <MDMEnterpriseDiagnosticsReport>
              <PolicyManager>
                <ConfigSource>
                  <EnrollmentId>11111111-1111-1111-1111-111111111111</EnrollmentId>
                  <PolicyScope>
                    <PolicyScope>Device</PolicyScope>
                    <Area>
                      <PolicyAreaName>Defender</PolicyAreaName>
                      <AllowRealtimeMonitoring>Enabled</AllowRealtimeMonitoring>
                    </Area>
                  </PolicyScope>
                </ConfigSource>
                <currentPolicies>
                  <PolicyScope>Device</PolicyScope>
                  <CurrentPolicyValues>
                    <PolicyAreaName>Defender</PolicyAreaName>
                    <AllowRealtimeMonitoring_WinningProvider>11111111-1111-1111-1111-111111111111</AllowRealtimeMonitoring_WinningProvider>
                  </CurrentPolicyValues>
                </currentPolicies>
              </PolicyManager>
              <PolicyManagerMeta>
                <AreaMetadata>
                  <PolicyAreaName>Defender</PolicyAreaName>
                  <PolicyMetadata>
                    <PolicyName>AllowRealtimeMonitoring</PolicyName>
                    <RegKeyPathRedirect>Software\Policies\Microsoft\Windows Defender</RegKeyPathRedirect>
                  </PolicyMetadata>
                </AreaMetadata>
              </PolicyManagerMeta>
            </MDMEnterpriseDiagnosticsReport>
            """);
        await File.WriteAllTextAsync(htmlPath, "<html><body>fallback</body></html>");

        var parsePayload = $$"""
            {
              "reportDirectory": "{{reportDir.Replace("\\", "\\\\", StringComparison.Ordinal)}}",
              "xmlPath": "{{xmlPath.Replace("\\", "\\\\", StringComparison.Ordinal)}}",
              "htmlPath": "{{htmlPath.Replace("\\", "\\\\", StringComparison.Ordinal)}}",
              "xmlNodeCount": 24,
              "htmlLineCount": 1
            }
            """;
        const string overlayPayload = """
            [
              {
                "scope": "Device",
                "area": "Defender",
                "settingName": "AllowRealtimeMonitoring",
                "omaUri": "HKLM\\Software\\Policies\\Microsoft\\Windows Defender",
                "currentValue": "Disabled",
                "status": "Applied",
                "resultCode": "",
                "source": "GroupPolicy",
                "winningSource": "GroupPolicy"
              }
            ]
            """;

        var service = new LocalIntuneActionService(new QueuedFakePowerShellExecutor(
            new PowershellExecutionResult(0, parsePayload, string.Empty),
            new PowershellExecutionResult(0, overlayPayload, string.Empty)));
        var report = await service.ParseIntunePolicyResultAsync("CLIENT01", reportDir, exportDir, CancellationToken.None);

        Assert.Contains(report.Entries, entry =>
            entry.SettingName == "AllowRealtimeMonitoring" &&
            entry.Source == "Mdm" &&
            entry.WinningSource == "Mdm" &&
            entry.IsDuplicate);
        Assert.Contains(report.Entries, entry =>
            entry.SettingName == "AllowRealtimeMonitoring" &&
            entry.Source == "GroupPolicy" &&
            entry.WinningSource == "Mdm" &&
            entry.IsDuplicate);

        var html = await File.ReadAllTextAsync(report.ExportHtmlPath);
        Assert.Contains("source-conflict", html, StringComparison.Ordinal);
        Assert.Contains("Conflict</span>", html, StringComparison.Ordinal);
        Assert.Contains("MDM: Mdm = Enabled", html, StringComparison.Ordinal);
        Assert.Contains("GPO/Local: GroupPolicy = Disabled", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LocalIntuneActionService_ParseIntunePolicyResult_ResolvesWinningProviderFromPolicyProviderLookup()
    {
        var reportDir = Path.Combine(Path.GetTempPath(), $"icc-policy-providerlookup-{Guid.NewGuid():N}");
        var exportDir = Path.Combine(reportDir, "export");
        Directory.CreateDirectory(reportDir);
        Directory.CreateDirectory(exportDir);
        var xmlPath = Path.Combine(reportDir, "MDMDiagReport.xml");
        var htmlPath = Path.Combine(reportDir, "MDMDiagReport.html");
        await File.WriteAllTextAsync(xmlPath, """
            <MDMEnterpriseDiagnosticsReport>
              <PolicyManager>
                <ConfigSource>
                  <EnrollmentId>11111111-1111-1111-1111-111111111111</EnrollmentId>
                  <PolicyScope>
                    <PolicyScope>Device</PolicyScope>
                    <Area>
                      <PolicyAreaName>Defender</PolicyAreaName>
                      <AllowRealtimeMonitoring>1</AllowRealtimeMonitoring>
                    </Area>
                  </PolicyScope>
                </ConfigSource>
                <currentPolicies>
                  <PolicyScope>Device</PolicyScope>
                  <CurrentPolicyValues>
                    <PolicyAreaName>Defender</PolicyAreaName>
                    <AllowRealtimeMonitoring_WinningProvider>aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa</AllowRealtimeMonitoring_WinningProvider>
                  </CurrentPolicyValues>
                </currentPolicies>
              </PolicyManager>
              <PolicyManagerMeta>
                <AreaMetadata>
                  <PolicyAreaName>Defender</PolicyAreaName>
                  <PolicyMetadata>
                    <PolicyName>AllowRealtimeMonitoring</PolicyName>
                    <RegKeyPathRedirect>Software\Policies\Microsoft\Windows Defender</RegKeyPathRedirect>
                  </PolicyMetadata>
                </AreaMetadata>
              </PolicyManagerMeta>
            </MDMEnterpriseDiagnosticsReport>
            """);
        await File.WriteAllTextAsync(htmlPath, "<html><body>fallback</body></html>");

        var parsePayload = $$"""
            {
              "reportDirectory": "{{reportDir.Replace("\\", "\\\\", StringComparison.Ordinal)}}",
              "xmlPath": "{{xmlPath.Replace("\\", "\\\\", StringComparison.Ordinal)}}",
              "htmlPath": "{{htmlPath.Replace("\\", "\\\\", StringComparison.Ordinal)}}",
              "xmlNodeCount": 24,
              "htmlLineCount": 1
            }
            """;
        var overlayPayload = """
            {
              "entries": [],
              "providers": [
                {
                  "providerId": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                  "name": "Local Group Policy",
                  "source": "GroupPolicy"
                }
              ]
            }
            """;

        var service = new LocalIntuneActionService(new QueuedFakePowerShellExecutor(
            new PowershellExecutionResult(0, parsePayload, string.Empty),
            new PowershellExecutionResult(0, overlayPayload, string.Empty)));
        var report = await service.ParseIntunePolicyResultAsync("CLIENT01", reportDir, exportDir, CancellationToken.None);

        var entry = Assert.Single(report.Entries);
        Assert.Equal("Defender", entry.Area);
        Assert.Equal("AllowRealtimeMonitoring", entry.SettingName);
        Assert.Equal("GroupPolicy", entry.WinningSource);
    }

    [Fact]
    public async Task LocalIntuneActionService_ParseIntunePolicyResult_UsesGpResultXmlOverlayScriptStructure()
    {
        var reportDir = Path.Combine(Path.GetTempPath(), $"icc-policy-gpresultxml-{Guid.NewGuid():N}");
        var exportDir = Path.Combine(reportDir, "export");
        Directory.CreateDirectory(reportDir);
        Directory.CreateDirectory(exportDir);
        var xmlPath = Path.Combine(reportDir, "MDMDiagReport.xml");
        var htmlPath = Path.Combine(reportDir, "MDMDiagReport.html");
        await File.WriteAllTextAsync(xmlPath, """
            <MDMEnterpriseDiagnosticsReport>
              <PolicyManager>
                <ConfigSource>
                  <EnrollmentId>11111111-1111-1111-1111-111111111111</EnrollmentId>
                  <PolicyScope>
                    <PolicyScope>Device</PolicyScope>
                    <Area>
                      <PolicyAreaName>Defender</PolicyAreaName>
                      <AllowRealtimeMonitoring>1</AllowRealtimeMonitoring>
                    </Area>
                  </PolicyScope>
                </ConfigSource>
              </PolicyManager>
            </MDMEnterpriseDiagnosticsReport>
            """);
        await File.WriteAllTextAsync(htmlPath, "<html><body>fallback</body></html>");

        var parsePayload = $$"""
            {
              "reportDirectory": "{{reportDir.Replace("\\", "\\\\", StringComparison.Ordinal)}}",
              "xmlPath": "{{xmlPath.Replace("\\", "\\\\", StringComparison.Ordinal)}}",
              "htmlPath": "{{htmlPath.Replace("\\", "\\\\", StringComparison.Ordinal)}}",
              "xmlNodeCount": 16,
              "htmlLineCount": 1
            }
            """;
        const string overlayPayload = """
            {
              "entries": [],
              "providers": []
            }
            """;

        var executor = new RecordingQueuedPowerShellExecutor(
            new PowershellExecutionResult(0, parsePayload, string.Empty),
            new PowershellExecutionResult(0, overlayPayload, string.Empty));
        var service = new LocalIntuneActionService(executor);
        _ = await service.ParseIntunePolicyResultAsync("CLIENT01", reportDir, exportDir, CancellationToken.None);

        Assert.True(executor.ScriptBodies.Count >= 2);
        var overlayScript = executor.ScriptBodies[1];
        Assert.Contains("RegistryRsopSetting", overlayScript, StringComparison.Ordinal);
        Assert.Contains("RegistrySetting", overlayScript, StringComparison.Ordinal);
        Assert.Contains("Normalize-GpResultKeyPathByScope", overlayScript, StringComparison.Ordinal);
        Assert.Contains("Resolve-PolicyAreaFromPath", overlayScript, StringComparison.Ordinal);
        Assert.Contains("KeyPath", overlayScript, StringComparison.Ordinal);
        Assert.Contains("Add-PolicyEntriesFromGpResultXml -Scope 'User'", overlayScript, StringComparison.Ordinal);
        Assert.Contains("Registry::HKEY_CURRENT_USER\\SOFTWARE\\Policies", overlayScript, StringComparison.Ordinal);
        Assert.Contains("Get-ChildItem -LiteralPath $HivePath -Recurse -ErrorAction SilentlyContinue", overlayScript, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LocalIntuneActionService_ParseIntunePolicyResult_UsesGpResultXmlFallbackWhenOverlayEntriesAreEmpty()
    {
        var reportDir = Path.Combine(Path.GetTempPath(), $"icc-policy-gpresult-fallback-{Guid.NewGuid():N}");
        var exportDir = Path.Combine(reportDir, "export");
        Directory.CreateDirectory(reportDir);
        Directory.CreateDirectory(exportDir);

        var xmlPath = Path.Combine(reportDir, "MDMDiagReport.xml");
        var htmlPath = Path.Combine(reportDir, "MDMDiagReport.html");
        var gpResultPath = Path.Combine(reportDir, "gpresult.xml");

        await File.WriteAllTextAsync(xmlPath, "<MDMEnterpriseDiagnosticsReport />");
        await File.WriteAllTextAsync(htmlPath, "<html><body>fallback</body></html>");
        await File.WriteAllTextAsync(gpResultPath, """
            <Rsop xmlns="http://www.microsoft.com/GroupPolicy/Rsop"
                  xmlns:q25="http://www.microsoft.com/GroupPolicy/Settings/Registry">
              <ComputerResults>
                <GPO>
                  <Identifier xmlns="http://www.microsoft.com/GroupPolicy/Types">{10000000-0000-0000-0000-000000000001}</Identifier>
                  <Name xmlns="http://www.microsoft.com/GroupPolicy/Types">Demo Browser Baseline</Name>
                </GPO>
                <ExtensionData>
                  <Extension>
                    <q25:RegistrySetting>
                      <GPO xmlns="http://www.microsoft.com/GroupPolicy/Settings/Base">
                        <Identifier xmlns="http://www.microsoft.com/GroupPolicy/Types">{10000000-0000-0000-0000-000000000001}</Identifier>
                        <Domain xmlns="http://www.microsoft.com/GroupPolicy/Types">example.invalid</Domain>
                      </GPO>
                      <q25:KeyPath>Software\Policies\Microsoft\Edge</q25:KeyPath>
                      <q25:AdmSetting>false</q25:AdmSetting>
                      <q25:Value>
                        <q25:Name>MetricsReportingEnabled</q25:Name>
                        <q25:Number>0</q25:Number>
                      </q25:Value>
                    </q25:RegistrySetting>
                  </Extension>
                </ExtensionData>
              </ComputerResults>
            </Rsop>
            """);

        var parsePayload = $$"""
            {
              "reportDirectory": "{{reportDir.Replace("\\", "\\\\", StringComparison.Ordinal)}}",
              "xmlPath": "{{xmlPath.Replace("\\", "\\\\", StringComparison.Ordinal)}}",
              "htmlPath": "{{htmlPath.Replace("\\", "\\\\", StringComparison.Ordinal)}}",
              "xmlNodeCount": 1,
              "htmlLineCount": 1
            }
            """;
        const string overlayPayload = """
            {
              "entries": [],
              "providers": []
            }
            """;

        var service = new LocalIntuneActionService(new QueuedFakePowerShellExecutor(
            new PowershellExecutionResult(0, parsePayload, string.Empty),
            new PowershellExecutionResult(0, overlayPayload, string.Empty)));
        var report = await service.ParseIntunePolicyResultAsync("CLIENT01", reportDir, exportDir, CancellationToken.None);

        Assert.Contains("LocalPolicyOverlay", report.Source, StringComparison.Ordinal);
        Assert.Contains(report.Warnings, warning => warning.Contains("gpresult.xml", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(report.Warnings, warning => warning.Contains("No local policy overlay entries were extracted", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(report.Entries, entry =>
            entry.Source == "GroupPolicy" &&
            entry.WinningSource == "GroupPolicy" &&
            entry.SettingName == "MetricsReportingEnabled" &&
            entry.Area == "Edge" &&
            entry.OmaUri.Contains(@"Software\Policies\Microsoft\Edge", StringComparison.OrdinalIgnoreCase));

        var html = await File.ReadAllTextAsync(report.ExportHtmlPath);
        Assert.Contains("GPO only", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LocalIntuneActionService_ParseIntunePolicyResult_MergesGpResultXmlEntriesWithMdmEntries()
    {
        var reportDir = Path.Combine(Path.GetTempPath(), $"icc-policy-gpresult-merge-{Guid.NewGuid():N}");
        var exportDir = Path.Combine(reportDir, "export");
        Directory.CreateDirectory(reportDir);
        Directory.CreateDirectory(exportDir);

        var xmlPath = Path.Combine(reportDir, "MDMDiagReport.xml");
        var htmlPath = Path.Combine(reportDir, "MDMDiagReport.html");
        var gpResultPath = Path.Combine(reportDir, "gpresult.xml");

        await File.WriteAllTextAsync(xmlPath, """
            <MDMEnterpriseDiagnosticsReport>
              <PolicyManager>
                <ConfigSource>
                  <EnrollmentId>11111111-1111-1111-1111-111111111111</EnrollmentId>
                  <PolicyScope>
                    <PolicyScope>Device</PolicyScope>
                    <Area>
                      <PolicyAreaName>Defender</PolicyAreaName>
                      <AllowRealtimeMonitoring>1</AllowRealtimeMonitoring>
                    </Area>
                  </PolicyScope>
                </ConfigSource>
              </PolicyManager>
            </MDMEnterpriseDiagnosticsReport>
            """);
        await File.WriteAllTextAsync(htmlPath, "<html><body>fallback</body></html>");
        await File.WriteAllTextAsync(gpResultPath, """
            <Rsop xmlns="http://www.microsoft.com/GroupPolicy/Rsop"
                  xmlns:q4="http://www.microsoft.com/GroupPolicy/Settings/Windows/Registry">
              <UserResults>
                <GPO>
                  <Identifier xmlns="http://www.microsoft.com/GroupPolicy/Types">{10000000-0000-0000-0000-000000000002}</Identifier>
                  <Name xmlns="http://www.microsoft.com/GroupPolicy/Types">Demo Office Macro User Policy</Name>
                </GPO>
                <ExtensionData>
                  <Extension xmlns="http://www.microsoft.com/GroupPolicy/Settings">
                    <q4:RegistryRsopSetting>
                      <GPO xmlns="http://www.microsoft.com/GroupPolicy/Settings/Base">
                        <Identifier xmlns="http://www.microsoft.com/GroupPolicy/Types">{10000000-0000-0000-0000-000000000002}</Identifier>
                      </GPO>
                      <q4:BaseInstanceXml CLASSNAME="RSOP_PolmkrRegistrySetting">
                        <INSTANCE CLASSNAME="RSOP_PolmkrRegistrySetting">
                          <PROPERTY NAME="polmkrBaseGpoDisplayName">
                            <VALUE>Demo Office Macro User Policy</VALUE>
                          </PROPERTY>
                          <PROPERTY NAME="polmkrValueResolved">
                            <VALUE>1</VALUE>
                          </PROPERTY>
                          <PROPERTY NAME="polmkrHiveResolved">
                            <VALUE>HKEY_CURRENT_USER</VALUE>
                          </PROPERTY>
                          <PROPERTY NAME="polmkrKeyResolved">
                            <VALUE>Software\Policies\Microsoft\Office\16.0\Common\General</VALUE>
                          </PROPERTY>
                          <PROPERTY NAME="polmkrNameResolved">
                            <VALUE>PreferCloudSaveLocations</VALUE>
                          </PROPERTY>
                          <PROPERTY NAME="polmkrClassResultCode">
                            <VALUE>0x00000000</VALUE>
                          </PROPERTY>
                          <PROPERTY NAME="polmkrClassResultCodeValue">
                            <VALUE>0</VALUE>
                          </PROPERTY>
                        </INSTANCE>
                      </q4:BaseInstanceXml>
                    </q4:RegistryRsopSetting>
                  </Extension>
                </ExtensionData>
              </UserResults>
            </Rsop>
            """);

        var parsePayload = $$"""
            {
              "reportDirectory": "{{reportDir.Replace("\\", "\\\\", StringComparison.Ordinal)}}",
              "xmlPath": "{{xmlPath.Replace("\\", "\\\\", StringComparison.Ordinal)}}",
              "htmlPath": "{{htmlPath.Replace("\\", "\\\\", StringComparison.Ordinal)}}",
              "xmlNodeCount": 9,
              "htmlLineCount": 1
            }
            """;
        const string overlayPayload = """
            {
              "entries": [],
              "providers": []
            }
            """;

        var service = new LocalIntuneActionService(new QueuedFakePowerShellExecutor(
            new PowershellExecutionResult(0, parsePayload, string.Empty),
            new PowershellExecutionResult(0, overlayPayload, string.Empty)));
        var report = await service.ParseIntunePolicyResultAsync("CLIENT01", reportDir, exportDir, CancellationToken.None);

        Assert.Contains(report.Entries, entry =>
            entry.Source == "Mdm" &&
            entry.SettingName == "AllowRealtimeMonitoring" &&
            entry.Area == "Defender");
        Assert.Contains(report.Entries, entry =>
            entry.Source == "GroupPolicy" &&
            entry.WinningSource == "GroupPolicy" &&
            entry.Scope == "User" &&
            entry.Area == "Office" &&
            entry.SettingName == "PreferCloudSaveLocations" &&
            entry.OmaUri.Contains(@"Software\Policies\Microsoft\Office\16.0\Common\General", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task LocalIntuneActionService_ParseIntunePolicyResult_MatchesAdmxDisplayPolicyFromGpResultXmlWithMdmEntry()
    {
        var previousRoot = Environment.GetEnvironmentVariable("ICC_POLICY_DEFINITIONS_ROOT");
        var policyDefinitionsRoot = CreatePolicyDefinitionsFixture();
        Environment.SetEnvironmentVariable("ICC_POLICY_DEFINITIONS_ROOT", policyDefinitionsRoot);

        var reportDir = Path.Combine(Path.GetTempPath(), $"icc-policy-lockscreen-{Guid.NewGuid():N}");
        var exportDir = Path.Combine(reportDir, "export");
        Directory.CreateDirectory(reportDir);
        Directory.CreateDirectory(exportDir);

        try
        {
            var xmlPath = Path.Combine(reportDir, "MDMDiagReport.xml");
            var htmlPath = Path.Combine(reportDir, "MDMDiagReport.html");
            var gpResultXmlPath = Path.Combine(reportDir, "gpresult.xml");
            var gpResultHtmlPath = Path.Combine(reportDir, "gpresult.html");

            await File.WriteAllTextAsync(xmlPath, """
                <MDMEnterpriseDiagnosticsReport>
                  <PolicyManager>
                    <ConfigSource>
                      <EnrollmentId>11111111-1111-1111-1111-111111111111</EnrollmentId>
                      <PolicyScope>
                        <PolicyScope>Device</PolicyScope>
                        <Area>
                          <PolicyAreaName>ADMX_ControlPanelDisplay</PolicyAreaName>
                          <CPL_Personalization_ForceDefaultLockScreen>&lt;enabled/&gt;&lt;data id="LockScreenImage" value="\\server\share\corp.jpg" /&gt;&lt;data id="LockScreenOverlaysDisabled" value="true" /&gt;</CPL_Personalization_ForceDefaultLockScreen>
                        </Area>
                      </PolicyScope>
                    </ConfigSource>
                    <currentPolicies>
                      <PolicyScope>Device</PolicyScope>
                      <CurrentPolicyValues>
                        <PolicyAreaName>ADMX_ControlPanelDisplay</PolicyAreaName>
                        <CPL_Personalization_ForceDefaultLockScreen_WinningProvider>aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa</CPL_Personalization_ForceDefaultLockScreen_WinningProvider>
                      </CurrentPolicyValues>
                    </currentPolicies>
                  </PolicyManager>
                  <PolicyManagerMeta>
                    <AreaMetadata>
                      <PolicyAreaName>ADMX_ControlPanelDisplay</PolicyAreaName>
                      <PolicyMetadata>
                        <PolicyName>CPL_Personalization_ForceDefaultLockScreen</PolicyName>
                        <RegKeyPathRedirect>Software\Policies\Microsoft\Windows\Personalization</RegKeyPathRedirect>
                      </PolicyMetadata>
                    </AreaMetadata>
                  </PolicyManagerMeta>
                </MDMEnterpriseDiagnosticsReport>
                """);
            await File.WriteAllTextAsync(htmlPath, "<html><body>fallback</body></html>");
            await File.WriteAllTextAsync(gpResultHtmlPath, """
                <html>
                  <body>
                    <div class="rsopsettings">
                      <div class="he0_expanded"><span class="sectionTitle">Computerdetails</span></div>
                      <div class="container">
                        <div class="he0h_expanded"><span class="sectionTitle">Einstellungen</span></div>
                        <div class="container">
                          <div class="he1h_expanded"><span class="sectionTitle">Richtlinien</span></div>
                          <div class="container">
                            <div class="he1"><span class="sectionTitle">Administrative Vorlagen</span></div>
                            <div class="container">
                              <div class="he3"><span class="sectionTitle">Systemsteuerung/Anpassung</span></div>
                              <div class="container">
                                <div class="he4i">
                                  <table class="info3">
                                    <tr><th>Richtlinie</th><th>Einstellung</th><th>Ausschlaggebendes Gruppenrichtlinienobjekt</th></tr>
                                    <tr>
                                      <td><span class="explainlink" gpmc_settingName="Ein bestimmtes Standardbild für den Sperr- und Anmeldebildschirm erzwingen" gpmc_settingPath="Computerkonfiguration/Administrative Vorlagen/Systemsteuerung/Anpassung">Ein bestimmtes Standardbild für den Sperr- und Anmeldebildschirm erzwingen</span></td>
                                      <td>Aktiviert<table><tr><td>Pfad zum Sperrbildschirmbild:</td><td>\\\\server\\share\\corp.jpg</td></tr></table></td>
                                      <td>Demo Printer Computer Policy</td>
                                    </tr>
                                  </table>
                                </div>
                              </div>
                            </div>
                          </div>
                        </div>
                      </div>
                    </div>
                  </body>
                </html>
                """, Encoding.Unicode);
            await File.WriteAllTextAsync(gpResultXmlPath, """
                <Rsop xmlns="http://www.microsoft.com/GroupPolicy/Rsop"
                      xmlns:q26="http://www.microsoft.com/GroupPolicy/Settings/Registry">
                  <ComputerResults>
                    <GPO>
                      <Identifier xmlns="http://www.microsoft.com/GroupPolicy/Types">{10000000-0000-0000-0000-000000000003}</Identifier>
                      <Name xmlns="http://www.microsoft.com/GroupPolicy/Types">Demo Printer Computer Policy</Name>
                    </GPO>
                    <ExtensionData>
                      <Extension>
                        <q26:Policy>
                          <GPO xmlns="http://www.microsoft.com/GroupPolicy/Settings/Base">
                            <Identifier xmlns="http://www.microsoft.com/GroupPolicy/Types">{10000000-0000-0000-0000-000000000003}</Identifier>
                          </GPO>
                          <q26:Name>Ein bestimmtes Standardbild für den Sperr- und Anmeldebildschirm erzwingen</q26:Name>
                          <q26:State>Enabled</q26:State>
                          <q26:Category>Systemsteuerung/Anpassung</q26:Category>
                          <q26:EditText>
                            <q26:Name>Pfad zum Sperrbildschirmbild:</q26:Name>
                            <q26:Value>\\server\share\corp.jpg</q26:Value>
                          </q26:EditText>
                        </q26:Policy>
                      </Extension>
                    </ExtensionData>
                  </ComputerResults>
                </Rsop>
                """);

            var parsePayload = $$"""
                {
                  "reportDirectory": "{{reportDir.Replace("\\", "\\\\", StringComparison.Ordinal)}}",
                  "xmlPath": "{{xmlPath.Replace("\\", "\\\\", StringComparison.Ordinal)}}",
                  "htmlPath": "{{htmlPath.Replace("\\", "\\\\", StringComparison.Ordinal)}}",
                  "xmlNodeCount": 24,
                  "htmlLineCount": 1
                }
                """;
            const string overlayPayload = """
                {
                  "entries": [],
                  "providers": []
                }
                """;

            var service = new LocalIntuneActionService(new QueuedFakePowerShellExecutor(
                new PowershellExecutionResult(0, parsePayload, string.Empty),
                new PowershellExecutionResult(0, overlayPayload, string.Empty)));
            var report = await service.ParseIntunePolicyResultAsync("CLIENT01", reportDir, exportDir, CancellationToken.None);

            Assert.Contains(report.Entries, entry =>
                entry.SettingName == "CPL_Personalization_ForceDefaultLockScreen" &&
                entry.Source == "Mdm" &&
                entry.IsDuplicate &&
                entry.WinningSource == "Mdm");
            Assert.Contains(report.Entries, entry =>
                entry.SettingName == "Ein bestimmtes Standardbild für den Sperr- und Anmeldebildschirm erzwingen" &&
                entry.Source == "GroupPolicy" &&
                entry.IsDuplicate &&
                entry.WinningSource == "Mdm");

            var html = await File.ReadAllTextAsync(report.ExportHtmlPath);
            Assert.Contains("MDM + GPO / Local", html, StringComparison.Ordinal);
            Assert.Contains("data-entry-row=\"true\" data-kind=\"hybrid\"", html, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ICC_POLICY_DEFINITIONS_ROOT", previousRoot);
            try
            {
                if (Directory.Exists(policyDefinitionsRoot))
                {
                    Directory.Delete(policyDefinitionsRoot, recursive: true);
                }
            }
            catch
            {
                // Best effort cleanup only.
            }

            try
            {
                if (Directory.Exists(reportDir))
                {
                    Directory.Delete(reportDir, recursive: true);
                }
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
    }

    [Fact]
    public async Task LocalIntuneActionService_ParseIntunePolicyResult_PrefersMdmWinnerWhenWinningProviderGuidIsUnresolved()
    {
        var previousRoot = Environment.GetEnvironmentVariable("ICC_POLICY_DEFINITIONS_ROOT");
        var policyDefinitionsRoot = CreatePolicyDefinitionsFixture();
        Environment.SetEnvironmentVariable("ICC_POLICY_DEFINITIONS_ROOT", policyDefinitionsRoot);

        var reportDir = Path.Combine(Path.GetTempPath(), $"icc-policy-allowbuildpreview-{Guid.NewGuid():N}");
        var exportDir = Path.Combine(reportDir, "export");
        Directory.CreateDirectory(reportDir);
        Directory.CreateDirectory(exportDir);

        try
        {
            var xmlPath = Path.Combine(reportDir, "MDMDiagReport.xml");
            var htmlPath = Path.Combine(reportDir, "MDMDiagReport.html");
            var gpResultXmlPath = Path.Combine(reportDir, "gpresult.xml");
            var gpResultHtmlPath = Path.Combine(reportDir, "gpresult.html");

            await File.WriteAllTextAsync(xmlPath, """
                <MDMEnterpriseDiagnosticsReport>
                  <PolicyManager>
                    <ConfigSource>
                      <EnrollmentId>11111111-1111-1111-1111-111111111111</EnrollmentId>
                      <PolicyScope>
                        <PolicyScope>Device</PolicyScope>
                        <Area>
                          <PolicyAreaName>System</PolicyAreaName>
                          <AllowBuildPreview>2</AllowBuildPreview>
                        </Area>
                      </PolicyScope>
                    </ConfigSource>
                    <currentPolicies>
                      <PolicyScope>Device</PolicyScope>
                      <CurrentPolicyValues>
                        <PolicyAreaName>System</PolicyAreaName>
                        <AllowBuildPreview_WinningProvider>10000000-0000-0000-0000-000000000004</AllowBuildPreview_WinningProvider>
                      </CurrentPolicyValues>
                    </currentPolicies>
                  </PolicyManager>
                  <PolicyManagerMeta>
                    <AreaMetadata>
                      <PolicyAreaName>System</PolicyAreaName>
                      <PolicyMetadata>
                        <PolicyName>AllowBuildPreview</PolicyName>
                        <RegKeyPathRedirect>Software\Policies\Microsoft\Windows\PreviewBuilds</RegKeyPathRedirect>
                      </PolicyMetadata>
                    </AreaMetadata>
                  </PolicyManagerMeta>
                </MDMEnterpriseDiagnosticsReport>
                """);
            await File.WriteAllTextAsync(htmlPath, "<html><body>fallback</body></html>");
            await File.WriteAllTextAsync(gpResultHtmlPath, """
                <html>
                  <body>
                    <div class="rsopsettings">
                      <div class="he0_expanded"><span class="sectionTitle">Computerdetails</span></div>
                      <div class="container">
                        <div class="he0h_expanded"><span class="sectionTitle">Einstellungen</span></div>
                        <div class="container">
                          <div class="he1h_expanded"><span class="sectionTitle">Richtlinien</span></div>
                          <div class="container">
                            <div class="he1"><span class="sectionTitle">Administrative Vorlagen</span></div>
                            <div class="container">
                              <div class="he3"><span class="sectionTitle">Windows-Komponenten/Datensammlung und Vorabversionen</span></div>
                              <div class="container">
                                <div class="he4i">
                                  <table class="info3">
                                    <tr><th>Richtlinie</th><th>Einstellung</th><th>Ausschlaggebendes Gruppenrichtlinienobjekt</th></tr>
                                    <tr>
                                      <td><span class="explainlink" gpmc_settingName="Benutzersteuerung für Insider-Builds ein-/ausschalten" gpmc_settingPath="Computerkonfiguration/Administrative Vorlagen/Windows-Komponenten/Datensammlung und Vorabversionen">Benutzersteuerung für Insider-Builds ein-/ausschalten</span></td>
                                      <td>Deaktiviert</td>
                                      <td>Preview Builds Policy</td>
                                    </tr>
                                  </table>
                                </div>
                              </div>
                            </div>
                          </div>
                        </div>
                      </div>
                    </div>
                  </body>
                </html>
                """, Encoding.Unicode);
            await File.WriteAllTextAsync(gpResultXmlPath, """
                <Rsop xmlns="http://www.microsoft.com/GroupPolicy/Rsop"
                      xmlns:q26="http://www.microsoft.com/GroupPolicy/Settings/Registry">
                  <ComputerResults>
                    <ExtensionData>
                      <Extension>
                        <q26:Policy>
                          <q26:Name>Benutzersteuerung für Insider-Builds ein-/ausschalten</q26:Name>
                          <q26:State>Disabled</q26:State>
                          <q26:Category>Windows-Komponenten/Datensammlung und Vorabversionen</q26:Category>
                          <q26:Supported>Mindestens Windows 10</q26:Supported>
                        </q26:Policy>
                      </Extension>
                    </ExtensionData>
                  </ComputerResults>
                </Rsop>
                """);

            var parsePayload = $$"""
                {
                  "reportDirectory": "{{reportDir.Replace("\\", "\\\\", StringComparison.Ordinal)}}",
                  "xmlPath": "{{xmlPath.Replace("\\", "\\\\", StringComparison.Ordinal)}}",
                  "htmlPath": "{{htmlPath.Replace("\\", "\\\\", StringComparison.Ordinal)}}",
                  "xmlNodeCount": 17,
                  "htmlLineCount": 1
                }
                """;
            const string overlayPayload = """
                {
                  "entries": [],
                  "providers": []
                }
                """;

            var service = new LocalIntuneActionService(new QueuedFakePowerShellExecutor(
                new PowershellExecutionResult(0, parsePayload, string.Empty),
                new PowershellExecutionResult(0, overlayPayload, string.Empty)));
            var report = await service.ParseIntunePolicyResultAsync("CLIENT01", reportDir, exportDir, CancellationToken.None);

            Assert.Contains(report.Entries, entry =>
                entry.SettingName == "AllowBuildPreview" &&
                entry.Source == "Mdm" &&
                entry.WinningSource == "Mdm" &&
                entry.IsDuplicate &&
                entry.GpoPath.Contains(@"Software\Policies\Microsoft\Windows\PreviewBuilds", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(report.Entries, entry =>
                entry.SettingName == "Benutzersteuerung für Insider-Builds ein-/ausschalten" &&
                entry.Source == "GroupPolicy" &&
                entry.WinningSource == "Mdm" &&
                entry.IsDuplicate);

            var html = await File.ReadAllTextAsync(report.ExportHtmlPath);
            Assert.Contains("AllowBuildPreview", html, StringComparison.Ordinal);
            Assert.Contains("GroupPolicy", html, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ICC_POLICY_DEFINITIONS_ROOT", previousRoot);
            try
            {
                Directory.Delete(reportDir, recursive: true);
            }
            catch
            {
            }

            try
            {
                Directory.Delete(policyDefinitionsRoot, recursive: true);
            }
            catch
            {
            }
        }
    }

    [Fact]
    public async Task LocalIntuneActionService_ParseIntunePolicyResult_UsesGpResultHtmlStructureForAdmxPathAndDetails()
    {
        var reportDir = Path.Combine(Path.GetTempPath(), $"icc-policy-gpresult-html-{Guid.NewGuid():N}");
        var exportDir = Path.Combine(reportDir, "export");
        Directory.CreateDirectory(reportDir);
        Directory.CreateDirectory(exportDir);

        var xmlPath = Path.Combine(reportDir, "MDMDiagReport.xml");
        var htmlPath = Path.Combine(reportDir, "MDMDiagReport.html");
        var gpResultHtmlPath = Path.Combine(reportDir, "gpresult.html");

        await File.WriteAllTextAsync(xmlPath, "<MDMEnterpriseDiagnosticsReport />");
        await File.WriteAllTextAsync(htmlPath, "<html><body>fallback</body></html>");
        await File.WriteAllTextAsync(gpResultHtmlPath, """
            <html>
              <body>
                <div class="rsopsettings">
                  <div class="he0_expanded"><span class="sectionTitle">Computerdetails</span><a class="expando" href="#"></a></div>
                  <div class="container">
                    <div class="he0h_expanded"><span class="sectionTitle">Einstellungen</span><a class="expando" href="#"></a></div>
                    <div class="container">
                      <div class="he1h_expanded"><span class="sectionTitle">Richtlinien</span><a class="expando" href="#"></a></div>
                      <div class="container">
                        <div class="he1"><span class="sectionTitle">Administrative Vorlagen</span><a class="expando" href="#"></a></div>
                        <div class="container">
                          <div class="he3"><span class="sectionTitle">Drucker</span><a class="expando" href="#"></a></div>
                          <div class="container">
                            <div class="he4i">
                              <table class="info3">
                                <tr><th>Richtlinie</th><th>Einstellung</th><th>Ausschlaggebendes Gruppenrichtlinienobjekt</th></tr>
                                <tr>
                                  <td><span class="explainlink" gpmc_settingName="Annahme von Clientverbindungen zum Druckspooler zulassen" gpmc_settingPath="Computerkonfiguration/Administrative Vorlagen/Drucker">Annahme von Clientverbindungen zum Druckspooler zulassen</span></td>
                                  <td>Deaktiviert<table class="subtable"><tr><td>Legacy clients</td><td>Blockiert</td></tr><tr><td>Remote spooler</td><td>Blockiert</td></tr></table></td>
                                  <td>Demo Printer Computer Policy</td>
                                </tr>
                              </table>
                            </div>
                          </div>
                        </div>
                      </div>
                    </div>
                  </div>
                </div>
              </body>
            </html>
            """, Encoding.Unicode);

        var parsePayload = $$"""
            {
              "reportDirectory": "{{reportDir.Replace("\\", "\\\\", StringComparison.Ordinal)}}",
              "xmlPath": "{{xmlPath.Replace("\\", "\\\\", StringComparison.Ordinal)}}",
              "htmlPath": "{{htmlPath.Replace("\\", "\\\\", StringComparison.Ordinal)}}",
              "xmlNodeCount": 1,
              "htmlLineCount": 1
            }
            """;
        const string overlayPayload = """
            {
              "entries": [],
              "providers": []
            }
            """;

        var service = new LocalIntuneActionService(new QueuedFakePowerShellExecutor(
            new PowershellExecutionResult(0, parsePayload, string.Empty),
            new PowershellExecutionResult(0, overlayPayload, string.Empty)));
        var report = await service.ParseIntunePolicyResultAsync("CLIENT01", reportDir, exportDir, CancellationToken.None);

        var entry = Assert.Single(report.Entries);
        Assert.Equal("GroupPolicy", entry.Source);
        Assert.Equal("Drucker", entry.Area);
        Assert.Equal("Annahme von Clientverbindungen zum Druckspooler zulassen", entry.SettingName);
        Assert.Equal(@"Computerkonfiguration\Administrative Vorlagen\Drucker", entry.GpoPath);
        Assert.Equal(@"Computerkonfiguration\Administrative Vorlagen\Drucker", entry.GpoCategoryPath);
        Assert.Contains("Legacy clients = Blockiert", entry.AdditionalDetails, StringComparison.Ordinal);
        Assert.Contains("Remote spooler = Blockiert", entry.AdditionalDetails, StringComparison.Ordinal);
        Assert.Contains(report.Warnings, warning => warning.Contains("gpresult.html", StringComparison.OrdinalIgnoreCase));

        var exportedHtml = await File.ReadAllTextAsync(report.ExportHtmlPath);
        Assert.Contains("class=\"detail-row\"", exportedHtml, StringComparison.Ordinal);
        Assert.Contains("class=\"detail-table\"", exportedHtml, StringComparison.Ordinal);
        Assert.Contains("<td>Legacy clients</td>", exportedHtml, StringComparison.Ordinal);
        Assert.Contains("<td>Blockiert</td>", exportedHtml, StringComparison.Ordinal);
        Assert.Contains(@"Computerkonfiguration\Administrative Vorlagen\Drucker", exportedHtml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LocalIntuneActionService_ParseIntunePolicyResult_NormalizesOverlayJsonWithConsolePrefixAndDerivesEdgeArea()
    {
        var reportDir = Path.Combine(Path.GetTempPath(), $"icc-policy-overlayprefix-{Guid.NewGuid():N}");
        var exportDir = Path.Combine(reportDir, "export");
        Directory.CreateDirectory(reportDir);
        Directory.CreateDirectory(exportDir);
        var xmlPath = Path.Combine(reportDir, "MDMDiagReport.xml");
        var htmlPath = Path.Combine(reportDir, "MDMDiagReport.html");
        await File.WriteAllTextAsync(xmlPath, """
            <MDMEnterpriseDiagnosticsReport>
              <PolicyManager>
                <ConfigSource>
                  <PolicyScope>
                    <PolicyScope>Device</PolicyScope>
                    <Area>
                      <PolicyAreaName>Defender</PolicyAreaName>
                      <AllowRealtimeMonitoring>1</AllowRealtimeMonitoring>
                    </Area>
                  </PolicyScope>
                </ConfigSource>
              </PolicyManager>
            </MDMEnterpriseDiagnosticsReport>
            """);
        await File.WriteAllTextAsync(htmlPath, "<html><body>fallback</body></html>");

        var parsePayload = $$"""
            {
              "reportDirectory": "{{reportDir.Replace("\\", "\\\\", StringComparison.Ordinal)}}",
              "xmlPath": "{{xmlPath.Replace("\\", "\\\\", StringComparison.Ordinal)}}",
              "htmlPath": "{{htmlPath.Replace("\\", "\\\\", StringComparison.Ordinal)}}",
              "xmlNodeCount": 18,
              "htmlLineCount": 1
            }
            """;

        const string overlayJson = """
            {
              "entries": [
                {
                  "scope": "Device",
                  "area": "",
                  "settingName": "MetricsReportingEnabled",
                  "omaUri": "HKLM\\Software\\Policies\\Microsoft\\Edge",
                  "currentValue": "0",
                  "status": "Applied",
                  "resultCode": "",
                  "source": "GroupPolicy",
                  "winningSource": "GroupPolicy"
                }
              ],
              "providers": []
            }
            """;
        var overlayPayloadWithPrefix = "gpresult info line\r\n" + overlayJson;

        var service = new LocalIntuneActionService(new QueuedFakePowerShellExecutor(
            new PowershellExecutionResult(0, parsePayload, string.Empty),
            new PowershellExecutionResult(0, overlayPayloadWithPrefix, string.Empty)));
        var report = await service.ParseIntunePolicyResultAsync("CLIENT01", reportDir, exportDir, CancellationToken.None);

        Assert.Contains("LocalPolicyOverlay", report.Source, StringComparison.Ordinal);
        Assert.Contains(report.Warnings, warning => warning.Contains("normalized", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(report.Entries, entry =>
            entry.Source == "GroupPolicy" &&
            entry.SettingName == "MetricsReportingEnabled" &&
            entry.Area == "Edge");
    }

    [Fact]
    public async Task LocalIntuneActionService_ParseIntunePolicyResult_UsesHtmlFallbackWhenXmlEmpty()
    {
        var reportDir = Path.Combine(Path.GetTempPath(), $"icc-policy-html-{Guid.NewGuid():N}");
        var exportDir = Path.Combine(reportDir, "export");
        Directory.CreateDirectory(reportDir);
        Directory.CreateDirectory(exportDir);
        var xmlPath = Path.Combine(reportDir, "MDMDiagReport.xml");
        var htmlPath = Path.Combine(reportDir, "MDMDiagHTMLReport.html");
        await File.WriteAllTextAsync(xmlPath, "<Report><Metadata><Item>none</Item></Metadata></Report>");
        await File.WriteAllTextAsync(htmlPath, """
            <html>
              <body>
                <table>
                  <tr>
                    <th>Scope</th><th>Area</th><th>Setting Name</th><th>OMA-URI</th><th>Current Value</th><th>Status</th><th>Result Code</th>
                  </tr>
                  <tr>
                    <td>Device</td><td>Defender</td><td>DefinitionUpdate</td><td>./Device/Vendor/MSFT/Policy/Config/Defender/DefinitionUpdate</td><td>Enabled</td><td>Failed</td><td>0x87D1FDE8</td>
                  </tr>
                </table>
              </body>
            </html>
            """);

        var parsePayload = $$"""
            {
              "reportDirectory": "{{reportDir.Replace("\\", "\\\\", StringComparison.Ordinal)}}",
              "xmlPath": "{{xmlPath.Replace("\\", "\\\\", StringComparison.Ordinal)}}",
              "htmlPath": "{{htmlPath.Replace("\\", "\\\\", StringComparison.Ordinal)}}",
              "xmlNodeCount": 4,
              "htmlLineCount": 14
            }
            """;

        var service = new LocalIntuneActionService(new FakePowerShellExecutor(new PowershellExecutionResult(0, parsePayload, string.Empty)));
        var report = await service.ParseIntunePolicyResultAsync("CLIENT01", reportDir, exportDir, CancellationToken.None);

        Assert.Equal("HtmlFallback", report.Source);
        var entry = Assert.Single(report.Entries);
        Assert.Equal("Device", entry.Scope);
        Assert.Equal("Defender", entry.Area);
        Assert.Equal("Failed", entry.Status);
    }

    [Fact]
    public async Task LocalIntuneActionService_ParseIntunePolicyResult_ClassifiesAppliedAndFailedFromResultCode()
    {
        var reportDir = Path.Combine(Path.GetTempPath(), $"icc-policy-status-{Guid.NewGuid():N}");
        var exportDir = Path.Combine(reportDir, "export");
        Directory.CreateDirectory(reportDir);
        Directory.CreateDirectory(exportDir);
        var xmlPath = Path.Combine(reportDir, "MDMDiagReport.xml");
        var htmlPath = Path.Combine(reportDir, "MDMDiagReport.html");
        await File.WriteAllTextAsync(xmlPath, """
            <Report>
              <Policy>
                <Area>Defender</Area>
                <SettingName>AllowRealtimeMonitoring</SettingName>
                <OmaUri>./Device/Vendor/MSFT/Policy/Config/Defender/AllowRealtimeMonitoring</OmaUri>
                <ResultCode>0</ResultCode>
              </Policy>
              <Policy>
                <Area>Defender</Area>
                <SettingName>SubmitSamplesConsent</SettingName>
                <OmaUri>./Device/Vendor/MSFT/Policy/Config/Defender/SubmitSamplesConsent</OmaUri>
                <ErrorCode>87</ErrorCode>
              </Policy>
            </Report>
            """);
        await File.WriteAllTextAsync(htmlPath, "<html><body>fallback</body></html>");

        var parsePayload = $$"""
            {
              "reportDirectory": "{{reportDir.Replace("\\", "\\\\", StringComparison.Ordinal)}}",
              "xmlPath": "{{xmlPath.Replace("\\", "\\\\", StringComparison.Ordinal)}}",
              "htmlPath": "{{htmlPath.Replace("\\", "\\\\", StringComparison.Ordinal)}}",
              "xmlNodeCount": 14,
              "htmlLineCount": 1
            }
            """;

        var service = new LocalIntuneActionService(new FakePowerShellExecutor(new PowershellExecutionResult(0, parsePayload, string.Empty)));
        var report = await service.ParseIntunePolicyResultAsync("CLIENT01", reportDir, exportDir, CancellationToken.None);

        Assert.Contains(report.Entries, entry => entry.SettingName == "AllowRealtimeMonitoring" && entry.Status == "Applied");
        Assert.Contains(report.Entries, entry => entry.SettingName == "SubmitSamplesConsent" && entry.Status == "Failed");
    }

    [Fact]
    public async Task LocalIntuneActionService_ParseIntunePolicyResult_WritesHtmlAndJsonExportsWithMatchingCounts()
    {
        var reportDir = Path.Combine(Path.GetTempPath(), $"icc-policy-export-{Guid.NewGuid():N}");
        var exportDir = Path.Combine(reportDir, "export");
        Directory.CreateDirectory(reportDir);
        Directory.CreateDirectory(exportDir);
        var xmlPath = Path.Combine(reportDir, "MDMDiagReport.xml");
        var htmlPath = Path.Combine(reportDir, "MDMDiagReport.html");
        await File.WriteAllTextAsync(xmlPath, """
            <Report>
              <Policy>
                <Scope>Device</Scope>
                <Area>Defender</Area>
                <SettingName>AllowRealtimeMonitoring</SettingName>
                <OmaUri>./Device/Vendor/MSFT/Policy/Config/Defender/AllowRealtimeMonitoring</OmaUri>
                <ResultCode>0</ResultCode>
              </Policy>
            </Report>
            """);
        await File.WriteAllTextAsync(htmlPath, "<html><body>fallback</body></html>");

        var parsePayload = $$"""
            {
              "reportDirectory": "{{reportDir.Replace("\\", "\\\\", StringComparison.Ordinal)}}",
              "xmlPath": "{{xmlPath.Replace("\\", "\\\\", StringComparison.Ordinal)}}",
              "htmlPath": "{{htmlPath.Replace("\\", "\\\\", StringComparison.Ordinal)}}",
              "xmlNodeCount": 8,
              "htmlLineCount": 1
            }
            """;

        var service = new LocalIntuneActionService(new FakePowerShellExecutor(new PowershellExecutionResult(0, parsePayload, string.Empty)));
        var report = await service.ParseIntunePolicyResultAsync("CLIENT01", reportDir, exportDir, CancellationToken.None);

        Assert.True(File.Exists(report.ExportHtmlPath));
        Assert.True(File.Exists(report.ExportJsonPath));

        using var jsonDoc = JsonDocument.Parse(await File.ReadAllTextAsync(report.ExportJsonPath));
        Assert.Equal(report.Summary.TotalCount, jsonDoc.RootElement.GetProperty("summary").GetProperty("totalCount").GetInt32());
        Assert.Equal(report.Summary.AppliedCount, jsonDoc.RootElement.GetProperty("summary").GetProperty("appliedCount").GetInt32());
    }

    [Fact]
    public async Task LocalIntuneActionService_ParseIntunePolicyResult_PrintsFirstSectionWithoutScriptExecution()
    {
        var reportDir = Path.Combine(Path.GetTempPath(), $"icc-policy-html-fallback-{Guid.NewGuid():N}");
        var exportDir = Path.Combine(reportDir, "export");
        Directory.CreateDirectory(reportDir);
        Directory.CreateDirectory(exportDir);
        var xmlPath = Path.Combine(reportDir, "MDMDiagReport.xml");
        var htmlPath = Path.Combine(reportDir, "MDMDiagReport.html");
        await File.WriteAllTextAsync(xmlPath, """
            <Report>
              <Policy>
                <Scope>Device</Scope>
                <Area>Defender</Area>
                <SettingName>AllowRealtimeMonitoring</SettingName>
                <OmaUri>./Device/Vendor/MSFT/Policy/Config/Defender/AllowRealtimeMonitoring</OmaUri>
                <CurrentValue>1</CurrentValue>
                <ResultCode>0</ResultCode>
              </Policy>
            </Report>
            """);
        await File.WriteAllTextAsync(htmlPath, "<html><body>fallback</body></html>");

        var parsePayload = $$"""
            {
              "reportDirectory": "{{reportDir.Replace("\\", "\\\\", StringComparison.Ordinal)}}",
              "xmlPath": "{{xmlPath.Replace("\\", "\\\\", StringComparison.Ordinal)}}",
              "htmlPath": "{{htmlPath.Replace("\\", "\\\\", StringComparison.Ordinal)}}",
              "xmlNodeCount": 8,
              "htmlLineCount": 1
            }
            """;

        var service = new LocalIntuneActionService(new FakePowerShellExecutor(new PowershellExecutionResult(0, parsePayload, string.Empty)));
        var report = await service.ParseIntunePolicyResultAsync("CLIENT01", reportDir, exportDir, CancellationToken.None);

        var html = await File.ReadAllTextAsync(report.ExportHtmlPath);
        Assert.Contains("Device Policies", html, StringComparison.Ordinal);
        Assert.Contains("<span class=\"policy-node-label\">Defender (1)</span>", html, StringComparison.Ordinal);
        Assert.Contains("AllowRealtimeMonitoring", html, StringComparison.Ordinal);
        Assert.Contains("class=\"nav-item\" data-section-id=\"active-section-1\" href=\"#active-section-1\">Defender (1)</a>", html, StringComparison.Ordinal);
        Assert.Contains("Expand All Nodes", html, StringComparison.Ordinal);
        Assert.Contains("Collapse All Nodes", html, StringComparison.Ordinal);
        Assert.Contains("report-search", html, StringComparison.Ordinal);
        Assert.Contains("source-filter", html, StringComparison.Ordinal);
        Assert.Contains("path-mode", html, StringComparison.Ordinal);
        Assert.Contains("Group Policy Only", html, StringComparison.Ordinal);
        Assert.Contains("Local Policy Only", html, StringComparison.Ordinal);
        Assert.Contains("Compare MDM vs. GPO", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<th>Scope</th>", html, StringComparison.Ordinal);
        Assert.Contains(".policy-node-toggle { display:flex; align-items:center; width:100%;", html, StringComparison.Ordinal);
        Assert.Contains(".detail-table { width:calc(100% - 16px);", html, StringComparison.Ordinal);
        Assert.Contains("matchesFilter(kind)", html, StringComparison.Ordinal);
        Assert.Contains("if (directSections !== 0 || directNodes.length !== 1) { break; }", html, StringComparison.Ordinal);
        Assert.Contains("document.onclick = function (event) {", html, StringComparison.Ordinal);
        Assert.DoesNotContain(".policy-node.depth-2 > summary { background:#86a8c2; color:#000; margin-left:24px; }", html, StringComparison.Ordinal);
        Assert.Contains("<script", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LocalIntuneActionService_RunAutopilotDiagnosticsCommunity_ParsesTailJsonAndInjectsScriptArguments()
    {
        const string payload = """
        noise line before json
        {"message":"Autopilot diagnostics collected with community script.","scriptPath":"C:\\Users\\user\\Documents\\PowerShell\\Scripts\\Get-AutopilotDiagnosticsCommunity.ps1","installedVersion":"6.3","outputText":"AUTOPILOT DIAGNOSTICS\\nline2","outputLineCount":2,"truncated":false,"warnings":["Using cached community script."]}
        """;
        var executor = new RecordingPowerShellExecutor(new PowershellExecutionResult(0, payload, string.Empty));
        var service = new LocalIntuneActionService(executor);

        var result = await service.RunAutopilotDiagnosticsCommunityAsync(
            "CLIENT01",
            allSessions: true,
            showPolicies: false,
            moduleVersion: "6.3",
            maxOutputLines: 25,
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("Autopilot diagnostics collected with community script.", result.Message);
        Assert.Single(result.Warnings);
        Assert.Equal("Using cached community script.", result.Warnings[0]);
        Assert.Equal("6.3", result.Evidence["moduleVersionRequested"]);
        Assert.Equal("6.3", result.Evidence["moduleVersionInstalled"]);
        Assert.Equal("2", result.Evidence["outputLineCount"]);
        Assert.Equal("False", result.Evidence["outputTruncated"]);
        Assert.Contains("AUTOPILOT DIAGNOSTICS", result.Evidence["outputText"], StringComparison.Ordinal);
        Assert.Contains("$requestedVersion = '6.3'", executor.LastScriptBody, StringComparison.Ordinal);
        Assert.Contains("$includeAllSessions = $true", executor.LastScriptBody, StringComparison.Ordinal);
        Assert.Contains("$includePolicies = $false", executor.LastScriptBody, StringComparison.Ordinal);
        Assert.Contains("$maxOutputLines = 100", executor.LastScriptBody, StringComparison.Ordinal);
        Assert.DoesNotContain("__MODULE_VERSION__", executor.LastScriptBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LocalIntuneActionService_RunImeQuickStatus_InjectsMaxLinesAndParsesPayload()
    {
        const string payload = """
        {"message":"IME quick status collected.","outputText":"ServiceName: IntuneManagementExtension\nStatus: Running","outputLineCount":2,"truncated":false,"warnings":[]}
        """;
        var executor = new RecordingPowerShellExecutor(new PowershellExecutionResult(0, payload, string.Empty));
        var service = new LocalIntuneActionService(executor);

        var result = await service.RunImeQuickStatusAsync("CLIENT01", 10, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("IME quick status collected.", result.Message);
        Assert.Equal("2", result.Evidence["outputLineCount"]);
        Assert.Equal("False", result.Evidence["outputTruncated"]);
        Assert.Contains("ServiceName: IntuneManagementExtension", result.Evidence["outputText"], StringComparison.Ordinal);
        Assert.Contains("$maxOutputLines = 50", executor.LastScriptBody, StringComparison.Ordinal);
        Assert.DoesNotContain("__MAX_OUTPUT_LINES__", executor.LastScriptBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LocalIntuneActionService_ParsesAppWorkloadPolicyPayload()
    {
        const string payload = """
        {
          "message": "Latest AppWorkload policy payload extracted.",
          "policyJson": "[{\"Id\":\"abc\"}]"
        }
        """;
        var service = new LocalIntuneActionService(new FakePowerShellExecutor(new PowershellExecutionResult(0, payload, string.Empty)));

        var result = await service.ParseImeAppWorkloadPoliciesAsync("CLIENT01", "C:\\ProgramData\\Microsoft\\IntuneManagementExtension\\Logs", CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("[{\"Id\":\"abc\"}]", result.Evidence["policyJson"]);
    }

    [Fact]
    public async Task LocalIntuneActionService_ParsesImeLogTimelinePayload()
    {
        const string payload = """
        [
          {
            "timeCreated": "2026-03-20T10:15:00Z",
            "severity": "Warning",
            "component": "AppWorkload",
            "message": "App with id 11111111-1111-1111-1111-111111111111 enforcement failed with error code 0x87D300C9.",
            "sourceFile": "AppWorkload.log",
            "lineNumber": 120,
            "rawLine": "<![LOG[App with id 11111111-1111-1111-1111-111111111111 enforcement failed with error code 0x87D300C9.]LOG]!>",
            "isPolicyPayload": false,
            "policyJson": ""
          },
          {
            "timeCreated": "2026-03-20T10:14:00Z",
            "severity": "Information",
            "component": "AppWorkload",
            "message": "Get policies = [{\"Id\":\"abc\"}]",
            "sourceFile": "AppWorkload.log",
            "lineNumber": 118,
            "rawLine": "<![LOG[Get policies = [{\"Id\":\"abc\"}]]LOG]!>",
            "isPolicyPayload": true,
            "policyJson": "[{\"Id\":\"abc\"}]"
          }
        ]
        """;
        var service = new LocalIntuneActionService(new FakePowerShellExecutor(new PowershellExecutionResult(0, payload, string.Empty)));

        var entries = await service.GetImeLogTimelineAsync("CLIENT01", "C:\\ProgramData\\Microsoft\\IntuneManagementExtension\\Logs", "AppWorkload*.log", 400, CancellationToken.None);

        Assert.Equal(2, entries.Count);
        Assert.Equal("Warning", entries[0].Severity);
        Assert.Equal("AppWorkload.log", entries[0].SourceFile);
        Assert.Equal("App", entries[0].EntityType);
        Assert.Equal("11111111-1111-1111-1111-111111111111", entries[0].EntityId);
        Assert.Equal("0x87D300C9", entries[0].ResultCode);
        Assert.Equal("Policy Sync", entries[1].Flow);
        Assert.Equal("policy_sync", entries[1].Phase);
        Assert.True(entries[1].IsPolicyPayload);
        Assert.Equal("[{\"Id\":\"abc\"}]", entries[1].PolicyJson);
    }

    [Fact]
    public async Task LocalIntuneActionService_ParsesImeApplicationStatusPayload()
    {
        const string payload = """
        [
          {
            "appId": "11111111-1111-1111-1111-111111111111",
            "appName": "Contoso App",
            "installStatus": "Failed",
            "lastUpdated": "2026-03-20T10:15:00Z",
            "resultCode": "0x87D300C9",
            "sourceFile": "AppWorkload.log",
            "lastMessage": "Installation failed.",
            "isInstalledForAnyIdentity": true,
            "identityStatuses": [
              {
                "identityId": "00000000-0000-0000-0000-000000000000",
                "scope": "System",
                "installStatus": "Installed",
                "lastUpdated": "2026-03-20T10:14:00Z",
                "resultCode": "0x00000000",
                "source": "Registry Win32Apps",
                "details": "InstallState=Installed"
              }
            ]
          }
        ]
        """;
        var service = new LocalIntuneActionService(new FakePowerShellExecutor(new PowershellExecutionResult(0, payload, string.Empty)));

        var entries = await service.GetImeApplicationStatusesAsync("CLIENT01", "C:\\ProgramData\\Microsoft\\IntuneManagementExtension\\Logs", 400, CancellationToken.None);

        var entry = Assert.Single(entries);
        Assert.Equal("11111111-1111-1111-1111-111111111111", entry.AppId);
        Assert.Equal("Contoso App", entry.AppName);
        Assert.Equal("Failed", entry.InstallStatus);
        Assert.Equal("0x87D300C9", entry.ResultCode);
        Assert.True(entry.IsInstalledForAnyIdentity);
        var identity = Assert.Single(entry.IdentityStatuses);
        Assert.Equal("System", identity.Scope);
        Assert.Equal("Installed", identity.InstallStatus);
    }

    [Fact]
    public void LocalIntuneActionService_ClassifiesRegistryComplianceStateAsInstalled()
    {
        var status = InvokeRegistryStatusClassifier(
            "ComplianceState=1 | EnforcementState=1000 | InstallContext=2",
            resultCode: string.Empty,
            hasGrs: false,
            hasAppKey: true);

        Assert.Equal("Installed", status);
    }

    [Fact]
    public void LocalIntuneActionService_PrefersInstalledSignalsOverTransientText()
    {
        var status = InvokeRegistryStatusClassifier(
            "State=processing | pending | ApplicationDetected=true | DetectionState=3",
            resultCode: string.Empty,
            hasGrs: false,
            hasAppKey: true);

        Assert.Equal("Installed", status);
    }

    [Fact]
    public void LocalIntuneActionService_ClassifiesNotDetectedSignalsAsNotInstalled()
    {
        var status = InvokeRegistryStatusClassifier(
            "ApplicationDetected=false | ComplianceState=0 | DetectionState=0",
            resultCode: string.Empty,
            hasGrs: false,
            hasAppKey: true);

        Assert.Equal("NotInstalled", status);
    }

    [Fact]
    public void LocalIntuneActionService_ClassifiesImeDetectionStateNotInstalled()
    {
        var status = InvokeRegistryStatusClassifier(
            "DesiredState=2 | ApplicabilityState=0 | DetectionState=2",
            resultCode: string.Empty,
            hasGrs: false,
            hasAppKey: true);

        Assert.Equal("NotInstalled", status);
    }

    [Fact]
    public void LocalIntuneActionService_ClassifiesLegacyComplianceStateMessageJsonAsInstalled()
    {
        var status = InvokeRegistryStatusClassifier(
            "ComplianceStateMessage.ComplianceStateMessage={\"ComplianceState\":1,\"Applicability\":0,\"DesiredState\":2}",
            resultCode: string.Empty,
            hasGrs: false,
            hasAppKey: true);

        Assert.Equal("Installed", status);
    }

    [Fact]
    public void LocalIntuneActionService_ClassifiesReportingStateJsonAsInstalled()
    {
        var status = InvokeRegistryStatusClassifier(
            "ReportingState={\"DesiredState\":4,\"DetectionState\":1,\"ApplicabilityState\":0,\"Intent\":2}",
            resultCode: string.Empty,
            hasGrs: false,
            hasAppKey: true);

        Assert.Equal("Installed", status);
    }

    [Fact]
    public void LocalIntuneActionService_PrefersInstalledOverInProgressEnforcement()
    {
        var status = InvokeRegistryStatusClassifier(
            "ComplianceState=1 | DesiredState=2 | EnforcementState=2008",
            resultCode: string.Empty,
            hasGrs: false,
            hasAppKey: true);

        Assert.Equal("Installed", status);
    }

    [Fact]
    public void LocalIntuneActionService_PrefersInstalledReportingStateOverInProgressEnforcement()
    {
        var status = InvokeRegistryStatusClassifier(
            "ReportingState={\"DesiredState\":4,\"DetectionState\":1,\"ApplicabilityState\":0,\"EnforcementState\":2000}",
            resultCode: string.Empty,
            hasGrs: false,
            hasAppKey: true);

        Assert.Equal("Installed", status);
    }

    [Fact]
    public void LocalIntuneActionService_ClassifiesImeEnforcementErrorAsFailed()
    {
        var status = InvokeRegistryStatusClassifier(
            "ComplianceState=1 | DesiredState=2 | EnforcementState=5000",
            resultCode: string.Empty,
            hasGrs: false,
            hasAppKey: true);

        Assert.Equal("Failed", status);
    }

    [Fact]
    public void LocalIntuneActionService_ClassifiesStatus2UninstallingAsInProgress()
    {
        var status = InvokeRegistryStatusClassifier(
            "Intent=4 | Status2=Uninstalling | ErrorCode=0",
            resultCode: string.Empty,
            hasGrs: false,
            hasAppKey: true);

        Assert.Equal("InProgress", status);
    }

    [Fact]
    public void LocalIntuneActionService_IgnoresSubgraphProcessingLogAsInstallStatus()
    {
        var status = InvokeImeLogStatusClassifier(
            "[Win32App][V3Processor] Processing subgraph with app ids: 032937f7-c5a4-48a3-bcf6-ad78a2b0373b");

        Assert.Null(status);
    }

    [Fact]
    public void LocalIntuneActionService_ClassifiesInstallingLogAsInProgress()
    {
        var status = InvokeImeLogStatusClassifier(
            "[Win32App] Installing app 032937f7-c5a4-48a3-bcf6-ad78a2b0373b");

        Assert.Equal("InProgress", status);
    }

    [Fact]
    public void LocalIntuneActionService_ClassifiesStatus2UninstalledAsNotInstalled()
    {
        var status = InvokeRegistryStatusClassifier(
            "Intent=4 | Status2=UninstalledByGateway | ErrorCode=0",
            resultCode: string.Empty,
            hasGrs: false,
            hasAppKey: true);

        Assert.Equal("NotInstalled", status);
    }

    [Fact]
    public void LocalIntuneActionService_ClassifiesComplianceStateNotInstalled()
    {
        var status = InvokeRegistryStatusClassifier(
            "ComplianceState=2 | Required=true | Status=1000",
            resultCode: string.Empty,
            hasGrs: false,
            hasAppKey: true);

        Assert.Equal("NotInstalled", status);
    }

    [Fact]
    public void LocalIntuneActionService_ClassifiesComplianceStateErrorAsFailed()
    {
        var status = InvokeRegistryStatusClassifier(
            "ComplianceState=4 | DetectionState=1",
            resultCode: string.Empty,
            hasGrs: false,
            hasAppKey: true);

        Assert.Equal("Failed", status);
    }

    [Fact]
    public void LocalIntuneActionService_ClassifiesComplianceStateCleanupAsInProgress()
    {
        var status = InvokeRegistryStatusClassifier(
            "ComplianceState=100 | DetectionState=0",
            resultCode: string.Empty,
            hasGrs: false,
            hasAppKey: true);

        Assert.Equal("InProgress", status);
    }

    [Fact]
    public void LocalIntuneActionService_DoesNotTreatDesiredStateAvailableAsInstalled()
    {
        var status = InvokeRegistryStatusClassifier(
            "DesiredState=4 | Intent=2 | ApplicabilityCode=0",
            resultCode: string.Empty,
            hasGrs: false,
            hasAppKey: true);

        Assert.Equal("Detected", status);
    }

    [Fact]
    public void LocalIntuneActionService_ClassifiesCompanyPortalReportingSignalsAsInstalled()
    {
        const string companyPortalSignals =
            "{\"Applicability\":0,\"ComplianceState\":1,\"DesiredState\":2,\"ErrorCode\":null,\"TargetingMethod\":0,\"InstallContext\":2,\"TargetType\":2,\"ProductVersion\":\"11.2.1753.0\",\"AssignmentFilterIds\":null}" +
            " | " +
            "{\"DesiredState\":2,\"DetectionState\":1,\"DetectionErrorOccurred\":false,\"DetectionErrorCode\":null,\"ApplicabilityState\":0,\"ApplicabilityErrorOccurred\":false,\"ApplicabilityErrorCode\":null,\"EnforcementState\":null,\"EnforcementErrorCode\":null,\"TargetingMethod\":0,\"TargetingType\":2,\"InstallContext\":2,\"Intent\":3,\"InternalVersion\":1,\"DetectedIdentityVersion\":\"11.2.1753.0\",\"RemovalReason\":null}" +
            " | " +
            "{\"AppId\":\"032937f7-c5a4-48a3-bcf6-ad78a2b0373b\",\"Required\":true,\"Status\":1000,\"Status2\":1000,\"ApplicabilityCode\":0,\"ApplicabilityCode2\":0,\"ErrorCode\":0,\"CustomError\":true}";

        var status = InvokeRegistryStatusClassifier(
            companyPortalSignals,
            resultCode: string.Empty,
            hasGrs: false,
            hasAppKey: true);

        Assert.Equal("Installed", status);
    }

    [Fact]
    public void LocalIntuneActionService_PrefersInstalledReportingStateOverCleanupComplianceState()
    {
        const string companyPortalCleanupAndInstalledSignals =
            "ComplianceStateMessage={\"ComplianceState\":100,\"DesiredState\":2,\"InstallContext\":2}" +
            " | " +
            "ReportingState={\"DesiredState\":2,\"DetectionState\":1,\"ApplicabilityState\":0,\"InstallContext\":2,\"Intent\":3,\"DetectedIdentityVersion\":\"11.2.1753.0\"}" +
            " | " +
            "StatusServiceReport={\"AppId\":\"032937f7-c5a4-48a3-bcf6-ad78a2b0373b\",\"Required\":true,\"Status\":1000,\"Status2\":1000,\"ErrorCode\":0}";

        var status = InvokeRegistryStatusClassifier(
            companyPortalCleanupAndInstalledSignals,
            resultCode: string.Empty,
            hasGrs: false,
            hasAppKey: true);

        Assert.Equal("Installed", status);
    }

    [Fact]
    public void LocalIntuneActionService_DoesNotFailOnNonZeroResultCode_WhenInstalledSignalsExist()
    {
        var status = InvokeRegistryStatusClassifier(
            "ComplianceState=1 | DesiredState=2 | DetectionState=1 | Status=1000",
            resultCode: "0x87D1041C",
            hasGrs: false,
            hasAppKey: true);

        Assert.Equal("Installed", status);
    }

    [Fact]
    public void LocalIntuneActionService_ClassifiesCompanyPortalFromRegistryAsInstalled_WhenKeysAvailable()
    {
        var registrySignals = TryLoadCompanyPortalRegistrySignals();
        if (registrySignals is null)
        {
            return;
        }

        var (signalText, resultCode) = registrySignals.Value;
        Assert.False(string.IsNullOrWhiteSpace(signalText));

        var status = InvokeRegistryStatusClassifier(
            signalText,
            resultCode,
            hasGrs: false,
            hasAppKey: true);

        Assert.Equal("Installed", status);
    }

    [Fact]
    public void ImeApplicationIdentityStatusEntry_ResolvesApplicabilityAndDependencySignals()
    {
        var identity = new ImeApplicationIdentityStatusEntry(
            "00000000-0000-0000-0000-000000000000",
            "System",
            "InProgress",
            DateTimeOffset.UtcNow,
            string.Empty,
            "StatusServiceReports",
            "ApplicabilityCode=0 | DesiredState=2 | DetectionState=1 | Required=true | Status=1000 | ErrorCode=0");

        Assert.Equal("Applicable", identity.ApplicabilityStatus);
        Assert.Equal("Unknown", identity.DependencyStatus);
    }

    [Fact]
    public void ImeApplicationIdentityStatusEntry_MapsDetailedApplicabilityCode()
    {
        var identity = new ImeApplicationIdentityStatusEntry(
            "user-1",
            "User",
            "NotInstalled",
            DateTimeOffset.UtcNow,
            string.Empty,
            "StatusServiceReports",
            "ApplicabilityCode=1002 | ComplianceState=2");

        Assert.Equal("MinimumOSVersionNotMet", identity.ApplicabilityStatus);
    }

    [Fact]
    public void ImeApplicationStatusEntry_AggregatesDetailedApplicabilityStatuses()
    {
        var entry = new ImeApplicationStatusEntry(
            "11111111-1111-1111-1111-111111111111",
            "Contoso App",
            "Required",
            "System",
            "NotInstalled",
            DateTimeOffset.UtcNow,
            string.Empty,
            "StatusServiceReports",
            "synthetic",
            false,
            [
                new ImeApplicationIdentityStatusEntry(
                    "user-1",
                    "User",
                    "NotInstalled",
                    DateTimeOffset.UtcNow,
                    string.Empty,
                    "StatusServiceReports",
                    "ApplicabilityCode=1002"),
                new ImeApplicationIdentityStatusEntry(
                    "user-2",
                    "User",
                    "NotInstalled",
                    DateTimeOffset.UtcNow,
                    string.Empty,
                    "StatusServiceReports",
                    "ApplicabilityCode=1002")
            ]);

        Assert.Equal("MinimumOSVersionNotMet", entry.ApplicabilitySummary);
    }

    [Fact]
    public void ImeApplicationIdentityStatusEntry_ResolvesDependencyBlockedSignal()
    {
        var identity = new ImeApplicationIdentityStatusEntry(
            "user-1",
            "User",
            "Failed",
            DateTimeOffset.UtcNow,
            "0x87D1041C",
            "AppWorkload.log",
            "Dependency app missing, prerequisite not found.");

        Assert.Equal("Blocked", identity.DependencyStatus);
    }

    private static string InvokeRegistryStatusClassifier(string signalText, string resultCode, bool hasGrs, bool hasAppKey)
    {
        var method = typeof(LocalIntuneActionService).GetMethod(
            "ClassifyInstallStatusFromRegistry",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);

        var raw = method!.Invoke(null, [signalText, resultCode, hasGrs, hasAppKey]);
        var status = Assert.IsType<string>(raw);
        return status;
    }

    private static string? InvokeImeLogStatusClassifier(string message)
    {
        var method = typeof(LocalIntuneActionService).GetMethod(
            "ClassifyInstallStatus",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);

        var raw = method!.Invoke(null, [message]);
        return raw as string;
    }

    private static (string SignalText, string ResultCode)? TryLoadCompanyPortalRegistrySignals()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        const string compliancePath = @"SOFTWARE\Microsoft\IntuneManagementExtension\Win32Apps\10000000-0000-0000-0000-000000000005\032937f7-c5a4-48a3-bcf6-ad78a2b0373b_1\ComplianceStateMessage";
        const string reportingPath = @"SOFTWARE\Microsoft\IntuneManagementExtension\Win32Apps\Reporting\00000000-0000-0000-0000-000000000000\032937f7-c5a4-48a3-bcf6-ad78a2b0373b";

        try
        {
            using var complianceKey = Registry.LocalMachine.OpenSubKey(compliancePath, writable: false);
            using var reportingKey = Registry.LocalMachine.OpenSubKey(reportingPath, writable: false);
            if (complianceKey is null || reportingKey is null)
            {
                return null;
            }

            var signals = new List<string>();
            var resultCode = string.Empty;
            CollectRegistrySignals(complianceKey, "ComplianceStateMessage", signals, ref resultCode, depth: 0);
            CollectRegistrySignals(reportingKey, "Reporting", signals, ref resultCode, depth: 0);

            if (signals.Count == 0)
            {
                return null;
            }

            return (string.Join(" | ", signals), resultCode);
        }
        catch
        {
            return null;
        }
    }

    private static void CollectRegistrySignals(
        RegistryKey key,
        string pathPrefix,
        ICollection<string> signals,
        ref string resultCode,
        int depth)
    {
        if (depth > 2)
        {
            return;
        }

        foreach (var valueName in key.GetValueNames())
        {
            var value = key.GetValue(valueName);
            var serialized = SerializeRegistryTestValue(value);
            if (string.IsNullOrWhiteSpace(serialized))
            {
                continue;
            }

            var qualifiedName = string.IsNullOrWhiteSpace(pathPrefix)
                ? valueName
                : $"{pathPrefix}.{valueName}";
            signals.Add($"{qualifiedName}={serialized}");
            signals.Add(serialized);

            if (string.IsNullOrWhiteSpace(resultCode))
            {
                resultCode = ExtractResultCodeFromTestSignal(valueName, value, serialized);
            }
        }

        foreach (var childName in key.GetSubKeyNames())
        {
            using var child = key.OpenSubKey(childName, writable: false);
            if (child is null)
            {
                continue;
            }

            var childPrefix = string.IsNullOrWhiteSpace(pathPrefix)
                ? childName
                : $"{pathPrefix}.{childName}";
            CollectRegistrySignals(child, childPrefix, signals, ref resultCode, depth + 1);
        }
    }

    private static string SerializeRegistryTestValue(object? value)
    {
        if (value is null)
        {
            return string.Empty;
        }

        if (value is byte[] bytes)
        {
            if (bytes.Length == 0)
            {
                return string.Empty;
            }

            var utf8 = Encoding.UTF8.GetString(bytes).Replace("\0", string.Empty, StringComparison.Ordinal).Trim();
            if (!string.IsNullOrWhiteSpace(utf8) &&
                (utf8.StartsWith("{", StringComparison.Ordinal) || utf8.StartsWith("[", StringComparison.Ordinal)))
            {
                return utf8;
            }

            var utf16 = Encoding.Unicode.GetString(bytes).Replace("\0", string.Empty, StringComparison.Ordinal).Trim();
            if (!string.IsNullOrWhiteSpace(utf16) &&
                (utf16.StartsWith("{", StringComparison.Ordinal) || utf16.StartsWith("[", StringComparison.Ordinal)))
            {
                return utf16;
            }

            return Convert.ToHexString(bytes);
        }

        if (value is string[] values)
        {
            return string.Join(", ", values.Where(item => !string.IsNullOrWhiteSpace(item)));
        }

        return Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static string ExtractResultCodeFromTestSignal(string valueName, object? rawValue, string serializedValue)
    {
        var hint = valueName.ToLowerInvariant();
        var hasCodeHint =
            hint.Contains("error", StringComparison.Ordinal) ||
            hint.Contains("result", StringComparison.Ordinal) ||
            hint.Contains("return", StringComparison.Ordinal) ||
            hint.Contains("exit", StringComparison.Ordinal) ||
            hint.Contains("code", StringComparison.Ordinal);

        if (!hasCodeHint)
        {
            return string.Empty;
        }

        var hex = System.Text.RegularExpressions.Regex.Match(serializedValue, @"0x[0-9A-Fa-f]{8}");
        if (hex.Success)
        {
            return hex.Value.ToUpperInvariant();
        }

        if (rawValue is int intValue)
        {
            return $"0x{unchecked((uint)intValue):X8}";
        }

        if (rawValue is uint uintValue)
        {
            return $"0x{uintValue:X8}";
        }

        if (int.TryParse(serializedValue, out var numeric))
        {
            return $"0x{unchecked((uint)numeric):X8}";
        }

        return string.Empty;
    }

    private static string CreatePolicyDefinitionsFixture()
    {
        var root = Path.Combine(Path.GetTempPath(), $"icc-policy-definitions-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, "de-DE"));

        File.WriteAllText(Path.Combine(root, "ControlPanelDisplay.admx"), """
            <?xml version="1.0" encoding="utf-8"?>
            <policyDefinitions revision="1.0" schemaVersion="1.0">
              <policies>
                <policy name="CPL_Personalization_ForceDefaultLockScreen"
                        class="Machine"
                        displayName="$(string.CPL_Personalization_ForceDefaultLockScreen)"
                        key="Software\Policies\Microsoft\Windows\Personalization">
                  <elements>
                    <text id="LockScreenImage" valueName="LockScreenImage" required="true" />
                    <boolean id="LockScreenOverlaysDisabled" valueName="LockScreenOverlaysDisabled" />
                  </elements>
                </policy>
              </policies>
            </policyDefinitions>
            """);

        File.WriteAllText(Path.Combine(root, "de-DE", "ControlPanelDisplay.adml"), """
            <?xml version="1.0" encoding="utf-8"?>
            <policyDefinitionResources revision="1.0" schemaVersion="1.0">
              <resources>
                <stringTable>
                  <string id="CPL_Personalization_ForceDefaultLockScreen">Ein bestimmtes Standardbild für den Sperr- und Anmeldebildschirm erzwingen</string>
                </stringTable>
              </resources>
            </policyDefinitionResources>
            """);

        File.WriteAllText(Path.Combine(root, "AllowBuildPreview.admx"), """
            <?xml version="1.0" encoding="utf-8"?>
            <policyDefinitions revision="1.0" schemaVersion="1.0">
              <policies>
                <policy name="AllowBuildPreview"
                        class="Machine"
                        displayName="$(string.AllowBuildPreview)"
                        key="Software\Policies\Microsoft\Windows\PreviewBuilds"
                        valueName="AllowBuildPreview" />
              </policies>
            </policyDefinitions>
            """);

        File.WriteAllText(Path.Combine(root, "de-DE", "AllowBuildPreview.adml"), """
            <?xml version="1.0" encoding="utf-8"?>
            <policyDefinitionResources revision="1.0" schemaVersion="1.0">
              <resources>
                <stringTable>
                  <string id="AllowBuildPreview">Benutzersteuerung für Insider-Builds ein-/ausschalten</string>
                </stringTable>
              </resources>
            </policyDefinitionResources>
            """);

        return root;
    }

    private sealed class FakePowerShellExecutor(PowershellExecutionResult result) : IPowerShellExecutor
    {
        public ValueTask<PowershellExecutionResult> ExecuteForHostAsync(string host, string scriptBody, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(result);
        }
    }

    private sealed class RecordingPowerShellExecutor(PowershellExecutionResult result) : IPowerShellExecutor
    {
        public string LastHost { get; private set; } = string.Empty;
        public string LastScriptBody { get; private set; } = string.Empty;

        public ValueTask<PowershellExecutionResult> ExecuteForHostAsync(string host, string scriptBody, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastHost = host;
            LastScriptBody = scriptBody;
            return ValueTask.FromResult(result);
        }
    }

    private sealed class FakeAccessTokenProvider : IAccessTokenProvider
    {
        public ValueTask<string> GetAccessTokenAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult("fake-token");
        }
    }

    private sealed class QueuedFakePowerShellExecutor(params PowershellExecutionResult[] results) : IPowerShellExecutor
    {
        private readonly Queue<PowershellExecutionResult> _results = new(results);

        public ValueTask<PowershellExecutionResult> ExecuteForHostAsync(string host, string scriptBody, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_results.Count == 0)
            {
                return ValueTask.FromResult(new PowershellExecutionResult(1, string.Empty, "No fake result queued."));
            }

            return ValueTask.FromResult(_results.Dequeue());
        }
    }

    private sealed class RecordingQueuedPowerShellExecutor(params PowershellExecutionResult[] results) : IPowerShellExecutor
    {
        private readonly Queue<PowershellExecutionResult> _results = new(results);

        public List<string> Hosts { get; } = [];
        public List<string> ScriptBodies { get; } = [];

        public ValueTask<PowershellExecutionResult> ExecuteForHostAsync(string host, string scriptBody, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Hosts.Add(host);
            ScriptBodies.Add(scriptBody);
            if (_results.Count == 0)
            {
                return ValueTask.FromResult(new PowershellExecutionResult(1, string.Empty, "No fake result queued."));
            }

            return ValueTask.FromResult(_results.Dequeue());
        }
    }

    private sealed class RecordingHttpMessageHandler(string responseBody, HttpStatusCode statusCode = HttpStatusCode.OK) : HttpMessageHandler
    {
        public string LastRequestUri { get; private set; } = string.Empty;
        public HttpMethod? LastMethod { get; private set; }
        public string? LastAuthorizationScheme { get; private set; }
        public string? LastAuthorizationParameter { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri?.ToString() ?? string.Empty;
            LastMethod = request.Method;
            LastAuthorizationScheme = request.Headers.Authorization?.Scheme;
            LastAuthorizationParameter = request.Headers.Authorization?.Parameter;

            var response = new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
            };

            return Task.FromResult(response);
        }
    }
}
