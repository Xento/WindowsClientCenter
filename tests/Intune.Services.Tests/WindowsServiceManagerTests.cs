using System.Text.Json;
using WindowsClientCenter.Intune.Services.Models;
using WindowsClientCenter.Intune.Services.Runtime;
using Xunit;

namespace WindowsClientCenter.Tests.IntuneServices;

public sealed class WindowsServiceManagerTests
{
    [Fact]
    public async Task GetServicesAsync_MapsPayloadToServiceEntries()
    {
        var executor = new RecordingPowerShellExecutor
        {
            Result = new PowershellExecutionResult(
                0,
                JsonSerializer.Serialize(new
                {
                    services = new object[]
                    {
                        new
                        {
                            serviceName = "IntuneManagementExtension",
                            displayName = "Microsoft Intune Management Extension",
                            state = "Running",
                            startMode = "AutomaticDelayedStart",
                            description = "Intune agent service",
                            processId = 2440
                        },
                        new
                        {
                            serviceName = "ccmsetup",
                            displayName = "ConfigMgr Setup Service",
                            state = "Stopped",
                            startMode = "Manual",
                            description = "MECM setup",
                            processId = (int?)null
                        }
                    },
                    warnings = Array.Empty<string>()
                }),
                string.Empty)
        };

        var manager = new WindowsServiceManager(executor);

        var snapshot = await manager.GetServicesAsync("CLIENT01", CancellationToken.None);

        Assert.Equal("CLIENT01", snapshot.Host);
        Assert.Empty(snapshot.Warnings);
        Assert.Equal(2, snapshot.Services.Count);
        Assert.Equal("ConfigMgr Setup Service", snapshot.Services[0].DisplayName);
        Assert.Equal(WindowsServiceStartMode.AutomaticDelayedStart, snapshot.Services[1].StartMode);
        Assert.Equal("Automatic (Delayed Start)", snapshot.Services[1].StartModeDisplay);
        Assert.Equal(2440, snapshot.Services[1].ProcessId);
    }

    [Fact]
    public async Task GetServicesAsync_ReturnsWarningSnapshot_WhenExecutionFails()
    {
        var executor = new RecordingPowerShellExecutor
        {
            Result = new PowershellExecutionResult(1, string.Empty, "WinRM connection failed.")
        };

        var manager = new WindowsServiceManager(executor);

        var snapshot = await manager.GetServicesAsync("CLIENT01", CancellationToken.None);

        Assert.Empty(snapshot.Services);
        Assert.Contains("WinRM connection failed.", snapshot.Warnings);
    }

    [Fact]
    public async Task SetStartModeAsync_UsesDelayedAutoScript()
    {
        var executor = new RecordingPowerShellExecutor
        {
            Result = new PowershellExecutionResult(0, "Start mode updated.", string.Empty)
        };

        var manager = new WindowsServiceManager(executor);

        var result = await manager.SetStartModeAsync("CLIENT01", "BITS", WindowsServiceStartMode.AutomaticDelayedStart, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("AutomaticDelayedStart", executor.LastScript, StringComparison.Ordinal);
        Assert.Contains("Set-ServiceDelayedAutoStart -ServiceName $serviceName -Enabled $true", executor.LastScript, StringComparison.Ordinal);
    }

    [Fact]
    public async Task KillServiceProcessAsync_UsesProcessTerminationScript()
    {
        var executor = new RecordingPowerShellExecutor
        {
            Result = new PowershellExecutionResult(0, "Killed.", string.Empty)
        };

        var manager = new WindowsServiceManager(executor);

        var result = await manager.KillServiceProcessAsync("CLIENT01", "wuauserv", CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("Stop-Process -Id $previousPid -Force", executor.LastScript, StringComparison.Ordinal);
        Assert.Contains("$item.ProcessId -le 0", executor.LastScript, StringComparison.Ordinal);
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
