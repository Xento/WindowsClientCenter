using System.Text.Json;
using WindowsClientCenter.Intune.Services.Runtime;
using Xunit;

namespace WindowsClientCenter.Tests.IntuneServices;

public sealed class WindowsProcessManagerTests
{
    [Fact]
    public async Task GetProcessesAsync_MapsPayloadToSnapshot()
    {
        var executor = new RecordingPowerShellExecutor
        {
            Result = new PowershellExecutionResult(
                0,
                JsonSerializer.Serialize(new
                {
                    logicalProcessorCount = 8,
                    capturedAtUtc = "2026-04-20T08:15:00Z",
                    processes = new object[]
                    {
                        new
                        {
                            name = "IntuneManagementExtension",
                            processId = 1544,
                            parentProcessId = 620,
                            commandLine = "agent.exe",
                            workingSetBytes = 1024L,
                            privateMemoryBytes = 2048L,
                            cpuTimeSeconds = 12.5d,
                            startTimeUtc = "2026-04-20T07:00:00Z",
                            threadCount = 12,
                            handleCount = 40
                        },
                        new
                        {
                            name = "explorer",
                            processId = 3120,
                            parentProcessId = 620,
                            commandLine = "explorer.exe",
                            workingSetBytes = 4096L,
                            privateMemoryBytes = 8192L,
                            cpuTimeSeconds = 3.0d,
                            startTimeUtc = (string?)null,
                            threadCount = 40,
                            handleCount = 200
                        }
                    },
                    warnings = Array.Empty<string>()
                }),
                string.Empty)
        };

        var manager = new WindowsProcessManager(executor);

        var snapshot = await manager.GetProcessesAsync("CLIENT01", CancellationToken.None);

        Assert.Equal("CLIENT01", snapshot.Host);
        Assert.Equal(8, snapshot.LogicalProcessorCount);
        Assert.Equal(2, snapshot.Processes.Count);
        var agent = Assert.Single(snapshot.Processes, process => process.ProcessId == 1544);
        Assert.Equal(620, agent.ParentProcessId);
        Assert.Equal(12.5d, agent.CpuTimeSeconds);
        Assert.Equal(12, agent.ThreadCount);
        Assert.Equal(40, agent.HandleCount);
    }

    [Fact]
    public async Task GetProcessesAsync_ReturnsWarningSnapshot_WhenExecutionFails()
    {
        var executor = new RecordingPowerShellExecutor
        {
            Result = new PowershellExecutionResult(1, string.Empty, "WinRM connection failed.")
        };

        var manager = new WindowsProcessManager(executor);

        var snapshot = await manager.GetProcessesAsync("CLIENT01", CancellationToken.None);

        Assert.Empty(snapshot.Processes);
        Assert.Contains("WinRM connection failed.", snapshot.Warnings);
    }

    [Fact]
    public async Task KillProcessAsync_UsesKillScript()
    {
        var executor = new RecordingPowerShellExecutor
        {
            Result = new PowershellExecutionResult(0, "Killed.", string.Empty)
        };

        var manager = new WindowsProcessManager(executor);

        var result = await manager.KillProcessAsync("CLIENT01", 4552, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("$processId=4552;", executor.LastScript, StringComparison.Ordinal);
        Assert.Contains("Stop-Process -Id $processId -Force", executor.LastScript, StringComparison.Ordinal);
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
