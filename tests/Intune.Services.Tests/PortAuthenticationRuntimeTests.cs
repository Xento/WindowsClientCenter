using System.Net.Http;
using WindowsClientCenter.Intune.Services.Models;
using WindowsClientCenter.Intune.Services.Runtime;
using Xunit;

namespace WindowsClientCenter.Tests.IntuneServices;

public sealed class PortAuthenticationRuntimeTests
{
    [Fact]
    public async Task GetPortAuthenticationSnapshotAsync_BuildsExpectedTroubleshootingScript()
    {
        var executor = new RecordingPowerShellExecutor(
            new PowershellExecutionResult(0, """
            {
              "portAuthentication": {
                "capturedAtUtc": "2026-04-24T08:00:00Z",
                "overallStatusText": "Healthy",
                "overallStatusLevel": "Green",
                "overallDetailText": "Healthy.",
                "applicabilityText": "Applicable",
                "fqdn": "client01.contoso.com",
                "activeInterfaceName": "Ethernet",
                "activeInterfaceDescription": "Intel Ethernet",
                "authenticationStateText": "Authenticated",
                "tracingModeText": "Disabled",
                "lastSuccessfulAuthenticationText": "2026-04-24 08:00:00Z | Success",
                "checks": [],
                "profiles": [],
                "certificates": [],
                "events": []
              }
            }
            """, string.Empty));

        var service = new LocalIntuneDiagnosticsService(executor, new HttpClient(), new IntuneRuntimeOptions());

        var snapshot = await service.GetPortAuthenticationSnapshotAsync("CLIENT01", CancellationToken.None);

        Assert.NotNull(snapshot);
        Assert.Contains("dot3svc", executor.LastScript, StringComparison.Ordinal);
        Assert.Contains("EapHost", executor.LastScript, StringComparison.Ordinal);
        Assert.Contains("netsh lan show profiles", executor.LastScript, StringComparison.Ordinal);
        Assert.Contains("netsh lan export profile", executor.LastScript, StringComparison.Ordinal);
        Assert.Contains("Microsoft-Windows-Wired-AutoConfig/Operational", executor.LastScript, StringComparison.Ordinal);
        Assert.Contains("Microsoft-Windows-EapHost/Operational", executor.LastScript, StringComparison.Ordinal);
        Assert.Contains("Microsoft-Windows-CAPI2/Operational", executor.LastScript, StringComparison.Ordinal);
        Assert.Contains("([DateTime]$event.TimeCreated).ToUniversalTime()", executor.LastScript, StringComparison.Ordinal);
        Assert.Contains("function Get-IccPortAuthErrorDetails", executor.LastScript, StringComparison.Ordinal);
        Assert.Contains("function Invoke-IccPortAuthStep", executor.LastScript, StringComparison.Ordinal);
        Assert.Contains("Write-Error -Message $details", executor.LastScript, StringComparison.Ordinal);
        Assert.Contains("Invoke-IccPortAuthStep 'Get-IccPortAuthEvents'", executor.LastScript, StringComparison.Ordinal);
        Assert.Contains("Entries = [object[]]$entries", executor.LastScript, StringComparison.Ordinal);
        Assert.Contains("function Get-IccPortAuthLanInterfaces", executor.LastScript, StringComparison.Ordinal);
        Assert.Contains("check\\s*point|virtual\\s+network\\s+adapter|\\bvpn\\b", executor.LastScript, StringComparison.Ordinal);
        Assert.Contains("/*[local-name()=\"LANProfile\"]/*[local-name()=\"name\"]", executor.LastScript, StringComparison.Ordinal);
        Assert.Contains("Profile detected, but export could not be matched to XML", executor.LastScript, StringComparison.Ordinal);
        Assert.Contains("New-IccPortAuthCheck 'Applicability' $applicabilityText (Get-IccPortAuthStatusLevel $applicabilityText) $(if", executor.LastScript, StringComparison.Ordinal);
        Assert.Contains("New-IccPortAuthCheck 'Services' $(if ($servicesHealthy)", executor.LastScript, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetPortAuthenticationSnapshotAsync_ParsesEventEntries()
    {
        var executor = new RecordingPowerShellExecutor(
            new PowershellExecutionResult(0, """
            {
              "portAuthentication": {
                "capturedAtUtc": "2026-04-24T08:00:00Z",
                "overallStatusText": "Degraded",
                "overallStatusLevel": "Yellow",
                "overallDetailText": "Warnings found.",
                "applicabilityText": "Applicable",
                "fqdn": "client01.contoso.com",
                "activeInterfaceName": "Ethernet",
                "activeInterfaceDescription": "Intel Ethernet",
                "authenticationStateText": "Authenticated",
                "tracingModeText": "Disabled",
                "lastSuccessfulAuthenticationText": "2026-04-24 08:00:00Z | Success",
                "checks": [],
                "profiles": [],
                "certificates": [],
                "events": [
                  {
                    "timeCreatedUtc": "2026-04-24T07:55:00Z",
                    "logName": "Microsoft-Windows-Wired-AutoConfig/Operational",
                    "id": 11004,
                    "level": "Information",
                    "statusLevel": "Green",
                    "summary": "802.1X authenticated successfully.",
                    "recommendedAction": "None.",
                    "message": "Authentication completed."
                  }
                ]
              }
            }
            """, string.Empty));

        var service = new LocalIntuneDiagnosticsService(executor, new HttpClient(), new IntuneRuntimeOptions());

        var snapshot = await service.GetPortAuthenticationSnapshotAsync("CLIENT01", CancellationToken.None);

        Assert.NotNull(snapshot);
        var result = snapshot!;
        var entry = Assert.Single(result.Events);
        Assert.Equal(new DateTimeOffset(2026, 4, 24, 7, 55, 0, TimeSpan.Zero), entry.TimeCreated);
        Assert.Equal(11004, entry.Id);
        Assert.Equal("Green", entry.StatusLevel);
    }

    [Fact]
    public async Task GetNetworkConnectivitySnapshotAsync_UsesPortAuthenticationSnapshotForSummary()
    {
        var executor = new QueueingPowerShellExecutor(
        [
            new PowershellExecutionResult(0, """
            {
              "networkConnectivity": {
                "primaryConnectionText": "LAN",
                "primaryAdapterText": "Intel Ethernet",
                "wiFiSsidText": "Not connected",
                "vpnStatusText": "Not detected",
                "vpnProviderText": "-",
                "isCheckpointVpnDetected": false,
                "portAuthenticationStatusText": "Skipped",
                "portAuthenticationDetailText": "Old summary"
              }
            }
            """, string.Empty),
            new PowershellExecutionResult(0, """
            {
              "portAuthentication": {
                "capturedAtUtc": "2026-04-24T08:00:00Z",
                "overallStatusText": "Healthy",
                "overallStatusLevel": "Green",
                "overallDetailText": "Shared summary from port authentication.",
                "applicabilityText": "Applicable",
                "fqdn": "client01.contoso.com",
                "activeInterfaceName": "Ethernet",
                "activeInterfaceDescription": "Intel Ethernet",
                "authenticationStateText": "Authenticated",
                "tracingModeText": "Disabled",
                "lastSuccessfulAuthenticationText": "2026-04-24 08:00:00Z | Success",
                "checks": [],
                "profiles": [],
                "certificates": [],
                "events": []
              }
            }
            """, string.Empty)
        ]);

        var service = new LocalIntuneDiagnosticsService(executor, new HttpClient(), new IntuneRuntimeOptions());

        var snapshot = await service.GetNetworkConnectivitySnapshotAsync("CLIENT01", CancellationToken.None);

        var result = Assert.IsType<NetworkConnectivitySnapshot>(snapshot);
        Assert.Equal("Healthy", result.PortAuthenticationStatusText);
        Assert.Equal("Shared summary from port authentication.", result.PortAuthenticationDetailText);
        Assert.Equal(2, executor.Scripts.Count);
    }

    [Fact]
    public async Task RestartPortAuthenticationServicesAsync_BuildsExpectedScript()
    {
        var executor = new RecordingPowerShellExecutor(new PowershellExecutionResult(0, "ok", string.Empty));
        var service = new LocalIntuneActionService(executor);

        var result = await service.RestartPortAuthenticationServicesAsync("CLIENT01", CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("Restart-Service -Name 'dot3svc'", executor.LastScript, StringComparison.Ordinal);
        Assert.Contains("Restart-Service -Name 'EapHost'", executor.LastScript, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RestartPortAuthenticationAdapterAsync_BuildsExpectedScript()
    {
        var executor = new RecordingPowerShellExecutor(new PowershellExecutionResult(0, "ok", string.Empty));
        var service = new LocalIntuneActionService(executor);

        var result = await service.RestartPortAuthenticationAdapterAsync("CLIENT01", "Ethernet", CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("Disable-NetAdapter -Name $name", executor.LastScript, StringComparison.Ordinal);
        Assert.Contains("Enable-NetAdapter -Name $name", executor.LastScript, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(PortAuthenticationTracingMode.Enabled, "mode=yes")]
    [InlineData(PortAuthenticationTracingMode.Disabled, "mode=no")]
    [InlineData(PortAuthenticationTracingMode.Persistent, "mode=persistent")]
    public async Task SetPortAuthenticationTracingAsync_BuildsExpectedScript(PortAuthenticationTracingMode mode, string expectedSnippet)
    {
        var executor = new RecordingPowerShellExecutor(new PowershellExecutionResult(0, "ok", string.Empty));
        var service = new LocalIntuneActionService(executor);

        var result = await service.SetPortAuthenticationTracingAsync("CLIENT01", mode, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains(expectedSnippet, executor.LastScript, StringComparison.Ordinal);
        Assert.Contains("netsh lan show tracing", executor.LastScript, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SetPortAuthenticationAutoconfigAsync_BuildsExpectedScript()
    {
        var executor = new RecordingPowerShellExecutor(new PowershellExecutionResult(0, "ok", string.Empty));
        var service = new LocalIntuneActionService(executor);

        var result = await service.SetPortAuthenticationAutoconfigAsync("CLIENT01", "Ethernet", true, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("netsh lan set autoconfig enabled=yes interface=", executor.LastScript, StringComparison.Ordinal);
        Assert.Contains("$name='Ethernet';", executor.LastScript, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReapplyPortAuthenticationProfileAsync_BuildsExpectedScript()
    {
        var executor = new RecordingPowerShellExecutor(new PowershellExecutionResult(0, "ok", string.Empty));
        var service = new LocalIntuneActionService(executor);

        var result = await service.ReapplyPortAuthenticationProfileAsync("CLIENT01", "Corp Wired 802.1X", "Ethernet", CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("netsh lan export profile", executor.LastScript, StringComparison.Ordinal);
        Assert.Contains("netsh @addArgs", executor.LastScript, StringComparison.Ordinal);
        Assert.Contains("$profileName='Corp Wired 802.1X';", executor.LastScript, StringComparison.Ordinal);
    }

    private sealed class RecordingPowerShellExecutor(PowershellExecutionResult result) : IPowerShellExecutor
    {
        public string LastScript { get; private set; } = string.Empty;

        public ValueTask<PowershellExecutionResult> ExecuteForHostAsync(string host, string scriptBody, CancellationToken cancellationToken)
        {
            LastScript = scriptBody;
            return ValueTask.FromResult(result);
        }
    }

    private sealed class QueueingPowerShellExecutor(IReadOnlyList<PowershellExecutionResult> results) : IPowerShellExecutor
    {
        private int _index;

        public List<string> Scripts { get; } = [];

        public ValueTask<PowershellExecutionResult> ExecuteForHostAsync(string host, string scriptBody, CancellationToken cancellationToken)
        {
            Scripts.Add(scriptBody);
            var result = _index < results.Count ? results[_index] : results[^1];
            _index++;
            return ValueTask.FromResult(result);
        }
    }
}
