using System.Runtime.InteropServices;
using System.Text;
using WindowsClientCenter.Intune.Services.Contracts;
using WindowsClientCenter.Intune.Services.Models;

namespace WindowsClientCenter.Intune.Services.Runtime;

internal sealed class WinRmLocalDeviceActionService(IPowerShellExecutor executor) : ILocalDeviceActionService
{
    public async ValueTask<DeviceActionResult> ExecuteLocalActionAsync(
        string host,
        string action,
        IReadOnlyDictionary<string, string>? parameters,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(host))
        {
            return DeviceActionResult.Fail("No host was provided for local action.", "no_host");
        }

        var normalizedAction = string.IsNullOrWhiteSpace(action) ? "sync-now" : action.Trim();
        if (!normalizedAction.Equals("sync-now", StringComparison.OrdinalIgnoreCase) &&
            !normalizedAction.Equals("enable-winrm", StringComparison.OrdinalIgnoreCase) &&
            !normalizedAction.Equals("shutdown", StringComparison.OrdinalIgnoreCase) &&
            !normalizedAction.Equals("restart", StringComparison.OrdinalIgnoreCase) &&
            !normalizedAction.Equals("logoff", StringComparison.OrdinalIgnoreCase) &&
            !normalizedAction.Equals("lock", StringComparison.OrdinalIgnoreCase) &&
            !normalizedAction.Equals("set-power-scheme", StringComparison.OrdinalIgnoreCase))
        {
            return DeviceActionResult.Fail(
                $"Action '{normalizedAction}' is not supported via local WinRM execution.",
                "local_action_not_supported");
        }

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return DeviceActionResult.Fail(
                "Local WinRM execution is only supported when the host app runs on Windows.",
                "winrm_not_supported");
        }

        var script = normalizedAction.Equals("enable-winrm", StringComparison.OrdinalIgnoreCase)
            ? BuildEnableWinRmScript(host.Trim())
            : normalizedAction.Equals("shutdown", StringComparison.OrdinalIgnoreCase)
                ? BuildShutdownScript()
                : normalizedAction.Equals("restart", StringComparison.OrdinalIgnoreCase)
                    ? BuildRestartScript()
                    : normalizedAction.Equals("logoff", StringComparison.OrdinalIgnoreCase)
                        ? BuildLogoffScript()
                        : normalizedAction.Equals("lock", StringComparison.OrdinalIgnoreCase)
                            ? BuildLockWorkstationScript()
            : normalizedAction.Equals("set-power-scheme", StringComparison.OrdinalIgnoreCase)
                                ? BuildSetPowerSchemeScript(parameters)
                                : BuildSyncScript(host.Trim());
        var execution = await executor.ExecuteForHostAsync(Environment.MachineName, script, cancellationToken);
        var actionLabel = normalizedAction.ToLowerInvariant() switch
        {
            "sync-now" or "sync" => "local sync via WinRM",
            "enable-winrm" => "WinRM bootstrap",
            "shutdown" => "shutdown",
            "restart" => "restart",
            "logoff" => "logoff",
            "lock" => "lock workstation",
            "set-power-scheme" => "power scheme change",
            _ => normalizedAction
        };

        if (execution.ExitCode != 0)
        {
            var error = string.IsNullOrWhiteSpace(execution.StdErr) ? execution.StdOut : execution.StdErr;
            return normalizedAction.Equals("enable-winrm", StringComparison.OrdinalIgnoreCase)
                ? DeviceActionResult.Fail(
                    $"WinRM bootstrap failed on '{host.Trim()}': {error.Trim()}",
                    "winrm_enable_failed")
                : DeviceActionResult.Fail(
                    $"Action '{actionLabel}' failed on '{host.Trim()}': {error.Trim()}",
                    $"{normalizedAction}_failed");
        }

        var message = string.IsNullOrWhiteSpace(execution.StdOut)
            ? normalizedAction.Equals("enable-winrm", StringComparison.OrdinalIgnoreCase)
                ? $"WinRM bootstrap triggered on '{host.Trim()}'."
                : $"Action '{actionLabel}' triggered on '{host.Trim()}'."
            : execution.StdOut.Trim();

        return DeviceActionResult.Ok(message);
    }

    public async ValueTask<PowerStateSnapshot> GetPowerStateAsync(string host, CancellationToken cancellationToken)
    {
        var execution = await executor.ExecuteForHostAsync(host, BuildPowerStateScript(), cancellationToken);
        if (execution.ExitCode != 0)
        {
            return new PowerStateSnapshot(
                host,
                IsLocalHost(host),
                null,
                null,
                [],
                [NormalizeError(execution)]);
        }

        var activeSchemeId = string.Empty;
        var activeSchemeName = string.Empty;
        var warnings = new List<string>();
        var schemes = new List<PowerSchemeSnapshot>();

        foreach (var line in (execution.StdOut ?? string.Empty).Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("ACTIVE|", StringComparison.Ordinal))
            {
                var parts = line.Split('|', 3);
                if (parts.Length == 3)
                {
                    activeSchemeId = DecodePowerStateField(parts[1]);
                    activeSchemeName = DecodePowerStateField(parts[2]);
                }
                continue;
            }

            if (line.StartsWith("SCHEME|", StringComparison.Ordinal))
            {
                var parts = line.Split('|', 4);
                if (parts.Length == 4)
                {
                    schemes.Add(new PowerSchemeSnapshot(
                        DecodePowerStateField(parts[1]),
                        DecodePowerStateField(parts[2]),
                        bool.TryParse(parts[3], out var isActive) && isActive));
                }
                continue;
            }

            if (line.StartsWith("WARN|", StringComparison.Ordinal))
            {
                var parts = line.Split('|', 2);
                if (parts.Length == 2)
                {
                    warnings.Add(DecodePowerStateField(parts[1]));
                }
            }
        }

        if (schemes.Count == 0 && warnings.Count == 0 && string.IsNullOrWhiteSpace(execution.StdOut))
        {
            warnings.Add("Power state query returned no data.");
        }

        return new PowerStateSnapshot(
            host,
            IsLocalHost(host),
            activeSchemeId,
            activeSchemeName,
            schemes,
            warnings);
    }

    public ValueTask<DeviceActionResult> ShutdownAsync(string host, CancellationToken cancellationToken)
    {
        return ExecutePowerActionAsync(host, "shutdown", cancellationToken);
    }

    public ValueTask<DeviceActionResult> RestartAsync(string host, CancellationToken cancellationToken)
    {
        return ExecutePowerActionAsync(host, "restart", cancellationToken);
    }

    public ValueTask<DeviceActionResult> LogoffAsync(string host, CancellationToken cancellationToken)
    {
        return ExecutePowerActionAsync(host, "logoff", cancellationToken);
    }

    public ValueTask<DeviceActionResult> LockWorkstationAsync(string host, CancellationToken cancellationToken)
    {
        return ExecutePowerActionAsync(host, "lock", cancellationToken);
    }

    public ValueTask<DeviceActionResult> SetPowerSchemeAsync(string host, string schemeId, CancellationToken cancellationToken)
    {
        return ExecuteLocalActionAsync(
            host,
            "set-power-scheme",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["schemeId"] = schemeId },
            cancellationToken);
    }

    private async ValueTask<DeviceActionResult> ExecutePowerActionAsync(string host, string action, CancellationToken cancellationToken)
    {
        var execution = await executor.ExecuteForHostAsync(host, BuildPowerActionScript(action), cancellationToken);
        return execution.ExitCode == 0
            ? DeviceActionResult.Ok(string.IsNullOrWhiteSpace(execution.StdOut) ? $"Action '{action}' triggered on '{host.Trim()}'." : execution.StdOut.Trim())
            : DeviceActionResult.Fail(NormalizeError(execution), $"{action}_failed");
    }

    private static string BuildEnableWinRmScript(string host)
    {
        var escapedHost = host.Replace("'", "''", StringComparison.Ordinal);
        return
            "$ErrorActionPreference='Stop';" +
            $"$computerName='{escapedHost}';" +
            "$bootstrapScript = @'\r\n" +
            "$ErrorActionPreference='Stop'\r\n" +
            "try {\r\n" +
            "  Set-Service -Name WinRM -StartupType Automatic -ErrorAction SilentlyContinue | Out-Null\r\n" +
            "  Start-Service -Name WinRM -ErrorAction SilentlyContinue\r\n" +
            "  try {\r\n" +
            "    Enable-PSRemoting -Force -SkipNetworkProfileCheck -ErrorAction Stop | Out-Null\r\n" +
            "  } catch {\r\n" +
            "    & winrm quickconfig -q | Out-Null\r\n" +
            "  }\r\n" +
            "  & netsh advfirewall firewall set rule group=\"Windows Remote Management\" new enable=yes | Out-Null\r\n" +
            "  Write-Output ('WinRM bootstrap completed on ' + $env:COMPUTERNAME + '.')\r\n" +
            "} catch {\r\n" +
            "  Write-Error $_.Exception.Message\r\n" +
            "  exit 1\r\n" +
            "}\r\n" +
            "'@;" +
            "$encodedBootstrap = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($bootstrapScript));" +
            "$commandLine = 'powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand ' + $encodedBootstrap;" +
            "$processClass = [System.Management.ManagementClass]::new('\\\\' + $computerName + '\\root\\cimv2:Win32_Process');" +
            "$createParameters = $processClass.GetMethodParameters('Create');" +
            "$createParameters.CommandLine = $commandLine;" +
            "$result = $processClass.InvokeMethod('Create', $createParameters, $null);" +
            "if ([uint32]$result.ReturnValue -ne 0) { throw ('Failed to start WinRM bootstrap process via WMI. ReturnValue=' + $result.ReturnValue) };" +
            "$deadline = [DateTime]::UtcNow.AddSeconds(90);" +
            "while ([DateTime]::UtcNow -lt $deadline) {" +
            "  [System.Threading.Thread]::Sleep(2000);" +
            "  foreach ($port in @(5985, 5986)) {" +
            "    $client = $null;" +
            "    try {" +
            "      $client = [System.Net.Sockets.TcpClient]::new();" +
            "      $connect = $client.BeginConnect($computerName, $port, $null, $null);" +
            "      if (-not $connect.AsyncWaitHandle.WaitOne(1000)) { continue; }" +
            "      $client.EndConnect($connect);" +
            "      if ($client.Connected) {" +
            "        Write-Output ('WinRM is now reachable on ' + $computerName + ' via TCP ' + $port + '.');" +
            "        exit 0;" +
            "      }" +
            "    } catch {" +
            "    } finally {" +
            "      if ($null -ne $client) { $client.Dispose(); }" +
            "    };" +
            "  };" +
            "};" +
            "throw ('Timed out waiting for WinRM to become reachable on ' + $computerName + '.');";
    }

    private static string BuildSyncScript(string host)
    {
        var escapedHost = host.Replace("'", "''", StringComparison.Ordinal);
        return
            "$ErrorActionPreference='Stop';" +
            $"$computerName='{escapedHost}';" +
            "Test-WSMan -ComputerName $computerName -ErrorAction Stop | Out-Null;" +
            "Invoke-Command -ComputerName $computerName -ErrorAction Stop -ScriptBlock {" +
            "  $task = Get-ScheduledTask | Where-Object { $_.TaskName -eq 'PushLaunch' -and $_.TaskPath -like '\\\\Microsoft\\\\Windows\\\\EnterpriseMgmt\\\\*' } | Select-Object -First 1;" +
            "  if ($null -eq $task) { throw \"Intune sync task 'PushLaunch' was not found.\" };" +
            "  Start-ScheduledTask -TaskName $task.TaskName -TaskPath $task.TaskPath;" +
            "  Write-Output (\"Triggered Intune sync via task \" + $task.TaskPath + $task.TaskName);" +
            "};";
    }

    private static string BuildShutdownScript() =>
        "$ErrorActionPreference='Stop';" +
        "shutdown.exe /s /t 0 /f /d p:4:1 /c 'Windows Client Center shutdown action';" +
        "Write-Output 'Shutdown action triggered.';";

    private static string BuildRestartScript() =>
        "$ErrorActionPreference='Stop';" +
        "shutdown.exe /r /t 0 /f /d p:4:1 /c 'Windows Client Center restart action';" +
        "Write-Output 'Restart action triggered.';";

    private static string BuildLogoffScript() =>
        "$ErrorActionPreference='Stop';" +
        "$sessionLine = @(query user 2>$null | Select-Object -Skip 1 | Where-Object { $_ -match '\\bActive\\b' } | Select-Object -First 1);" +
        "if (-not $sessionLine) { $sessionLine = @(query user 2>$null | Select-Object -Skip 1 | Select-Object -First 1) };" +
        "if (-not $sessionLine) { throw 'No interactive session was found to log off.' };" +
        "$match = [regex]::Match($sessionLine, '^\\s*>?\\s*\\S+(?:\\s+\\S+)?\\s+(\\d+)\\s+');" +
        "if (-not $match.Success) { throw ('Failed to parse session id from: ' + $sessionLine) };" +
        "$sessionId = [int]$match.Groups[1].Value;" +
        "logoff $sessionId /V;" +
        "Write-Output ('Logoff action triggered for session ' + $sessionId + '.');";

    private static string BuildLockWorkstationScript() =>
        "$ErrorActionPreference='Stop';" +
        "rundll32.exe user32.dll,LockWorkStation;" +
        "Write-Output 'Lock workstation action triggered.';";

    private static string BuildSetPowerSchemeScript(IReadOnlyDictionary<string, string>? parameters)
    {
        var schemeId = parameters is not null && parameters.TryGetValue("schemeId", out var value) ? value : string.Empty;
        var escapedSchemeId = schemeId.Replace("'", "''", StringComparison.Ordinal);
        return
            "$ErrorActionPreference='Stop';" +
            $"$schemeId='{escapedSchemeId}';" +
            "if ([string]::IsNullOrWhiteSpace($schemeId)) { throw 'No power scheme id was provided.' };" +
            "powercfg /S $schemeId | Out-Null;" +
            "Write-Output ('Power scheme set to ' + $schemeId + '.');";
    }

    private static string BuildPowerActionScript(string action)
    {
        return action.ToLowerInvariant() switch
        {
            "shutdown" => BuildShutdownScript(),
            "restart" => BuildRestartScript(),
            "logoff" => BuildLogoffScript(),
            "lock" => BuildLockWorkstationScript(),
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, null)
        };
    }

    private static string BuildPowerStateScript()
    {
        return
            "$ErrorActionPreference='Stop';" +
            "$encoding = [System.Text.Encoding]::UTF8;" +
            "function Encode-Field {" +
            "  param([string]$Value);" +
            "  return [Convert]::ToBase64String($encoding.GetBytes([string]$Value));" +
            "};" +
            "$warnings = New-Object System.Collections.Generic.List[string];" +
            "$activeGuid = $null;" +
            "$activeName = $null;" +
            "$schemeCount = 0;" +
            "try {" +
            "  $plans = @(Get-CimInstance -Namespace 'root\\cimv2\\power' -ClassName Win32_PowerPlan -ErrorAction Stop);" +
            "  foreach ($plan in $plans) {" +
            "    $instanceId = [string]$plan.InstanceID;" +
            "    $schemeId = if ($instanceId -match '\\{([0-9A-Fa-f-]{36})\\}') { $matches[1] } else { '' };" +
            "    $schemeName = [string]$plan.ElementName;" +
            "    $isActive = [bool]$plan.IsActive;" +
            "    if ($isActive -and -not $activeGuid) { $activeGuid = $schemeId; $activeName = $schemeName };" +
            "    Write-Output ('SCHEME|' + (Encode-Field $schemeId) + '|' + (Encode-Field $schemeName) + '|' + [string][bool]$isActive);" +
            "    $schemeCount++;" +
            "  }" +
            "  if (-not $activeGuid) { $activePlan = @($plans | Where-Object { $_.IsActive } | Select-Object -First 1); if ($activePlan.Count -gt 0) { $activeGuid = if ($activePlan[0].InstanceID -match '\\{([0-9A-Fa-f-]{36})\\}') { $matches[1] } else { '' }; $activeName = [string]$activePlan[0].ElementName } }" +
            "} catch {" +
            "  $warnings.Add('CIM power plan query failed, falling back to powercfg: ' + $_.Exception.Message);" +
            "  try {" +
            "    $activeOutput = @(powercfg /GETACTIVESCHEME 2>&1);" +
            "    foreach ($line in $activeOutput) {" +
            "      if ($line -match '([0-9A-Fa-f-]{36}).*\\((.+?)\\)') {" +
            "        $activeGuid = $matches[1];" +
            "        $activeName = $matches[2];" +
            "        break;" +
            "      }" +
            "    }" +
            "  } catch { $warnings.Add('Failed to query active power scheme: ' + $_.Exception.Message) };" +
            "  try {" +
            "    $listing = @(powercfg /L 2>&1);" +
            "    foreach ($line in $listing) {" +
            "      if ($line -match '([0-9A-Fa-f-]{36}).*\\((.+?)\\)') {" +
            "        $schemeId = $matches[1];" +
            "        $schemeName = $matches[2];" +
            "        $isActive = $false;" +
            "        if ($line -match '\\*') { $isActive = $true } elseif ($activeGuid -and $schemeId.Equals($activeGuid, [StringComparison]::OrdinalIgnoreCase)) { $isActive = $true };" +
            "        if ($isActive -and -not $activeGuid) { $activeGuid = $schemeId; $activeName = $schemeName };" +
            "        Write-Output ('SCHEME|' + (Encode-Field $schemeId) + '|' + (Encode-Field $schemeName) + '|' + [string][bool]$isActive);" +
            "        $schemeCount++;" +
            "      }" +
            "    }" +
            "  } catch { $warnings.Add('Failed to query power schemes: ' + $_.Exception.Message) };" +
            "};" +
            "if ($schemeCount -eq 0) { $warnings.Add('No power schemes were returned.'); };" +
            "Write-Output ('ACTIVE|' + (Encode-Field $activeGuid) + '|' + (Encode-Field $activeName));" +
            "foreach ($warning in $warnings) { Write-Output ('WARN|' + (Encode-Field $warning)); };";
    }

    private static bool IsLocalHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        return host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
               host.Equals(".", StringComparison.OrdinalIgnoreCase) ||
               host.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeError(PowershellExecutionResult execution)
    {
        var raw = string.IsNullOrWhiteSpace(execution.StdErr) ? execution.StdOut : execution.StdErr;
        return string.IsNullOrWhiteSpace(raw)
            ? $"PowerShell execution failed with exit code {execution.ExitCode}."
            : raw.Trim();
    }

    private static string DecodePowerStateField(string encodedValue)
    {
        if (string.IsNullOrWhiteSpace(encodedValue))
        {
            return string.Empty;
        }

        try
        {
            var bytes = Convert.FromBase64String(encodedValue);
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return encodedValue;
        }
    }

}
