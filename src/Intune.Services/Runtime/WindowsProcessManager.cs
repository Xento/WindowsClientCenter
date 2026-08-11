using System.Text.Json;
using WindowsClientCenter.Intune.Services.Contracts;
using WindowsClientCenter.Intune.Services.Models;

namespace WindowsClientCenter.Intune.Services.Runtime;

internal sealed class WindowsProcessManager(IPowerShellExecutor executor) : IWindowsProcessManager
{
    public async ValueTask<ProcessSnapshot> GetProcessesAsync(string host, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(host))
        {
            return new ProcessSnapshot(string.Empty, 1, DateTimeOffset.UtcNow, [], ["No host was provided."]);
        }

        try
        {
            var normalizedHost = host.Trim();
            var execution = await executor.ExecuteForHostAsync(normalizedHost, BuildGetProcessesScriptBody(), cancellationToken);
            if (execution.ExitCode != 0)
            {
                return new ProcessSnapshot(
                    normalizedHost,
                    1,
                    DateTimeOffset.UtcNow,
                    [],
                    [NormalizeError(execution)]);
            }

            var payload = JsonSerializer.Deserialize<ProcessInventoryPayload>(
                string.IsNullOrWhiteSpace(execution.StdOut) ? "{}" : execution.StdOut,
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

            var capturedAtUtc = DateTimeOffset.TryParse(payload?.CapturedAtUtc, out var parsedCapturedAtUtc)
                ? parsedCapturedAtUtc
                : DateTimeOffset.UtcNow;

            var processes = (payload?.Processes ?? [])
                .Select(static item => new ProcessSnapshotEntry(
                    item.Name ?? string.Empty,
                    item.ProcessId,
                    item.ParentProcessId > 0 ? item.ParentProcessId : null,
                    item.CommandLine ?? string.Empty,
                    item.WorkingSetBytes < 0 ? 0 : item.WorkingSetBytes,
                    item.PrivateMemoryBytes < 0 ? 0 : item.PrivateMemoryBytes,
                    item.CpuTimeSeconds < 0 ? 0 : item.CpuTimeSeconds,
                    DateTimeOffset.TryParse(item.StartTimeUtc, out var startTimeUtc) ? startTimeUtc : null,
                    item.ThreadCount < 0 ? 0 : item.ThreadCount,
                    item.HandleCount < 0 ? 0 : item.HandleCount))
                .OrderBy(static item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static item => item.ProcessId)
                .ToArray();

            return new ProcessSnapshot(
                normalizedHost,
                payload?.LogicalProcessorCount > 0 ? payload.LogicalProcessorCount : 1,
                capturedAtUtc,
                processes,
                payload?.Warnings?.Where(static warning => !string.IsNullOrWhiteSpace(warning)).ToArray() ?? []);
        }
        catch (Exception ex)
        {
            return new ProcessSnapshot(host.Trim(), 1, DateTimeOffset.UtcNow, [], [ex.Message]);
        }
    }

    public async ValueTask<DeviceActionResult> KillProcessAsync(string host, int processId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(host))
        {
            return DeviceActionResult.Fail("No host was provided.", "no_host");
        }

        if (processId <= 0)
        {
            return DeviceActionResult.Fail("No process id was provided.", "no_process_id");
        }

        var normalizedHost = host.Trim();
        var execution = await executor.ExecuteForHostAsync(normalizedHost, BuildKillProcessScriptBody(processId), cancellationToken);
        return execution.ExitCode == 0
            ? DeviceActionResult.Ok(string.IsNullOrWhiteSpace(execution.StdOut)
                ? $"Process {processId} terminated on '{normalizedHost}'."
                : execution.StdOut.Trim())
            : DeviceActionResult.Fail(
                $"Killing process {processId} failed on '{normalizedHost}': {NormalizeError(execution)}",
                "kill_process_failed");
    }

    internal static string BuildGetProcessesScriptBody()
    {
        return
            "$ErrorActionPreference='Stop';" +
            "$warnings = New-Object System.Collections.Generic.List[string];" +
            "$processById = @{};" +
            "foreach ($runtimeProcess in @(Get-Process -ErrorAction SilentlyContinue)) {" +
            "  try { $processById[[int]$runtimeProcess.Id] = $runtimeProcess } catch { };" +
            "};" +
            "$logicalProcessorCount = 1;" +
            "try {" +
            "  $logicalProcessorCount = [int](Get-CimInstance -ClassName Win32_ComputerSystem -ErrorAction Stop | Select-Object -ExpandProperty NumberOfLogicalProcessors);" +
            "} catch {" +
            "  $warnings.Add('Could not determine logical processor count: ' + $_.Exception.Message) | Out-Null;" +
            "  try { $logicalProcessorCount = [int][Environment]::ProcessorCount } catch { $logicalProcessorCount = 1 };" +
            "};" +
            "try {" +
            "  $processes = @(Get-CimInstance -ClassName Win32_Process -ErrorAction Stop | ForEach-Object {" +
            "    $runtime = $processById[[int]$_.ProcessId];" +
            "    $cpuTimeSeconds = 0.0;" +
            "    $workingSetBytes = 0L;" +
            "    $privateMemoryBytes = 0L;" +
            "    $threadCount = 0;" +
            "    $handleCount = 0;" +
            "    $name = [string]$_.Name;" +
            "    if ($null -ne $runtime) {" +
            "      try { if ($null -ne $runtime.CPU) { $cpuTimeSeconds = [double]$runtime.CPU } } catch { };" +
            "      try { $workingSetBytes = [int64]$runtime.WorkingSet64 } catch { };" +
            "      try { $privateMemoryBytes = [int64]$runtime.PrivateMemorySize64 } catch { };" +
            "      try { $threadCount = [int]$runtime.Threads.Count } catch { };" +
            "      try { $handleCount = [int]$runtime.HandleCount } catch { };" +
            "      try { if (-not [string]::IsNullOrWhiteSpace([string]$runtime.ProcessName)) { $name = [string]$runtime.ProcessName } } catch { };" +
            "    };" +
            "    $startTimeUtc = $null;" +
            "    try {" +
            "      if (-not [string]::IsNullOrWhiteSpace([string]$_.CreationDate)) {" +
            "        $startTimeUtc = [System.Management.ManagementDateTimeConverter]::ToDateTime([string]$_.CreationDate).ToUniversalTime().ToString('o');" +
            "      }" +
            "    } catch { };" +
            "    [pscustomobject]@{" +
            "      Name = $name;" +
            "      ProcessId = [int]$_.ProcessId;" +
            "      ParentProcessId = if ([int]$_.ParentProcessId -gt 0) { [int]$_.ParentProcessId } else { $null };" +
            "      CommandLine = if ($null -ne $_.CommandLine) { [string]$_.CommandLine } else { '' };" +
            "      WorkingSetBytes = $workingSetBytes;" +
            "      PrivateMemoryBytes = $privateMemoryBytes;" +
            "      CpuTimeSeconds = $cpuTimeSeconds;" +
            "      StartTimeUtc = $startTimeUtc;" +
            "      ThreadCount = $threadCount;" +
            "      HandleCount = $handleCount" +
            "    };" +
            "  });" +
            "} catch {" +
            "  $warnings.Add($_.Exception.Message) | Out-Null;" +
            "  $processes = @();" +
            "};" +
            "$payload = [pscustomobject]@{" +
            "  LogicalProcessorCount = if ($logicalProcessorCount -gt 0) { $logicalProcessorCount } else { 1 };" +
            "  CapturedAtUtc = [DateTime]::UtcNow.ToString('o');" +
            "  Processes = @($processes);" +
            "  Warnings = @($warnings)" +
            "};" +
            "$payload | ConvertTo-Json -Depth 6 -Compress;";
    }

    internal static string BuildKillProcessScriptBody(int processId)
    {
        return
            "$ErrorActionPreference='Stop';" +
            $"$processId={processId};" +
            "if ($processId -le 0) { throw 'Invalid process id.' };" +
            "$process = Get-Process -Id $processId -ErrorAction SilentlyContinue;" +
            "if ($null -eq $process) { throw ('Process ' + $processId + ' was not found.') };" +
            "$processName = [string]$process.ProcessName;" +
            "Stop-Process -Id $processId -Force -ErrorAction Stop;" +
            "$deadline = (Get-Date).AddSeconds(20);" +
            "while ((Get-Date) -lt $deadline) {" +
            "  if (-not (Get-Process -Id $processId -ErrorAction SilentlyContinue)) { break };" +
            "  Start-Sleep -Milliseconds 400;" +
            "};" +
            "if (Get-Process -Id $processId -ErrorAction SilentlyContinue) { throw ('Process ' + $processId + ' is still running.') };" +
            "Write-Output ('Process ' + $processName + ' (' + $processId + ') terminated.');";
    }

    private static string NormalizeError(PowershellExecutionResult execution)
    {
        return string.IsNullOrWhiteSpace(execution.StdErr)
            ? string.IsNullOrWhiteSpace(execution.StdOut) ? "Unknown error." : execution.StdOut.Trim()
            : execution.StdErr.Trim();
    }

    private sealed class ProcessInventoryPayload
    {
        public int LogicalProcessorCount { get; set; }
        public string? CapturedAtUtc { get; set; }
        public ProcessInventoryPayloadItem[]? Processes { get; set; }
        public string[]? Warnings { get; set; }
    }

    private sealed class ProcessInventoryPayloadItem
    {
        public string? Name { get; set; }
        public int ProcessId { get; set; }
        public int? ParentProcessId { get; set; }
        public string? CommandLine { get; set; }
        public long WorkingSetBytes { get; set; }
        public long PrivateMemoryBytes { get; set; }
        public double CpuTimeSeconds { get; set; }
        public string? StartTimeUtc { get; set; }
        public int ThreadCount { get; set; }
        public int HandleCount { get; set; }
    }
}
