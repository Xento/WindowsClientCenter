using System.Text.Json;
using WindowsClientCenter.Intune.Services.Contracts;
using WindowsClientCenter.Intune.Services.Models;

namespace WindowsClientCenter.Intune.Services.Runtime;

internal sealed class WindowsServiceManager(IPowerShellExecutor executor) : IWindowsServiceManager
{
    public async ValueTask<WindowsServiceSnapshot> GetServicesAsync(string host, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(host))
        {
            return new WindowsServiceSnapshot(string.Empty, false, [], ["No host was provided."]);
        }

        try
        {
            var execution = await executor.ExecuteForHostAsync(host.Trim(), BuildGetServicesScriptBody(), cancellationToken);
            if (execution.ExitCode != 0)
            {
                return new WindowsServiceSnapshot(
                    host.Trim(),
                    LocalPowerShellExecutor.IsLocalHost(host.Trim()),
                    [],
                    [NormalizeError(execution)]);
            }

            var payload = JsonSerializer.Deserialize<ServiceInventoryPayload>(
                string.IsNullOrWhiteSpace(execution.StdOut) ? "{}" : execution.StdOut,
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

            var services = (payload?.Services ?? [])
                .Select(static item => new WindowsServiceEntry(
                    item.ServiceName ?? string.Empty,
                    item.DisplayName ?? string.Empty,
                    string.IsNullOrWhiteSpace(item.State) ? "Unknown" : item.State.Trim(),
                    ParseStartMode(item.StartMode),
                    item.Description ?? string.Empty,
                    item.ProcessId > 0 ? item.ProcessId : null))
                .OrderBy(static service => service.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static service => service.ServiceName, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return new WindowsServiceSnapshot(
                host.Trim(),
                LocalPowerShellExecutor.IsLocalHost(host.Trim()),
                services,
                payload?.Warnings?.Where(static warning => !string.IsNullOrWhiteSpace(warning)).ToArray() ?? []);
        }
        catch (Exception ex)
        {
            return new WindowsServiceSnapshot(
                host.Trim(),
                LocalPowerShellExecutor.IsLocalHost(host.Trim()),
                [],
                [ex.Message]);
        }
    }

    public ValueTask<DeviceActionResult> StartServiceAsync(string host, string serviceName, CancellationToken cancellationToken)
        => ExecuteServiceActionAsync(host, serviceName, BuildStartServiceScriptBody(serviceName), "start", cancellationToken);

    public ValueTask<DeviceActionResult> StopServiceAsync(string host, string serviceName, CancellationToken cancellationToken)
        => ExecuteServiceActionAsync(host, serviceName, BuildStopServiceScriptBody(serviceName), "stop", cancellationToken);

    public ValueTask<DeviceActionResult> RestartServiceAsync(string host, string serviceName, CancellationToken cancellationToken)
        => ExecuteServiceActionAsync(host, serviceName, BuildRestartServiceScriptBody(serviceName), "restart", cancellationToken);

    public ValueTask<DeviceActionResult> KillServiceProcessAsync(string host, string serviceName, CancellationToken cancellationToken)
        => ExecuteServiceActionAsync(host, serviceName, BuildKillServiceProcessScriptBody(serviceName), "kill", cancellationToken);

    public ValueTask<DeviceActionResult> SetStartModeAsync(string host, string serviceName, WindowsServiceStartMode startMode, CancellationToken cancellationToken)
        => ExecuteServiceActionAsync(host, serviceName, BuildSetStartModeScriptBody(serviceName, startMode), "set-start-mode", cancellationToken);

    private async ValueTask<DeviceActionResult> ExecuteServiceActionAsync(
        string host,
        string serviceName,
        string script,
        string actionName,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(host))
        {
            return DeviceActionResult.Fail("No host was provided.", "no_host");
        }

        if (string.IsNullOrWhiteSpace(serviceName))
        {
            return DeviceActionResult.Fail("No service name was provided.", "no_service");
        }

        var normalizedHost = host.Trim();
        var normalizedServiceName = serviceName.Trim();
        var execution = await executor.ExecuteForHostAsync(normalizedHost, script, cancellationToken);
        return execution.ExitCode == 0
            ? DeviceActionResult.Ok(string.IsNullOrWhiteSpace(execution.StdOut)
                ? $"Service action '{actionName}' completed for '{normalizedServiceName}' on '{normalizedHost}'."
                : execution.StdOut.Trim())
            : DeviceActionResult.Fail(
                $"Service action '{actionName}' failed for '{normalizedServiceName}' on '{normalizedHost}': {NormalizeError(execution)}",
                $"service_{actionName}_failed");
    }

    private static WindowsServiceStartMode ParseStartMode(string? value)
    {
        return value?.Trim() switch
        {
            "AutomaticDelayedStart" => WindowsServiceStartMode.AutomaticDelayedStart,
            "Automatic" => WindowsServiceStartMode.Automatic,
            "Disabled" => WindowsServiceStartMode.Disabled,
            _ => WindowsServiceStartMode.Manual
        };
    }

    internal static string BuildGetServicesScriptBody()
    {
        return
            BuildGetServiceInventoryFunctions() +
            "$warnings = New-Object System.Collections.Generic.List[string];" +
            "try {" +
            "  $services = @(Get-CimInstance -ClassName Win32_Service -ErrorAction Stop | ForEach-Object { Get-ServiceInventoryItem -Service $_ });" +
            "} catch {" +
            "  $warnings.Add($_.Exception.Message) | Out-Null;" +
            "  $services = @();" +
            "};" +
            "$payload = [pscustomobject]@{ Services = @($services | Sort-Object DisplayName, ServiceName); Warnings = @($warnings) };" +
            "$payload | ConvertTo-Json -Depth 5 -Compress;";
    }

    internal static string BuildStartServiceScriptBody(string serviceName)
    {
        return
            BuildGetServiceInventoryFunctions() +
            BuildWaitForServiceStatusFunction() +
            $"$serviceName='{EscapePowerShellSingleQuotedString(serviceName)}';" +
            "$service = Get-Service -Name $serviceName -ErrorAction Stop;" +
            "if (-not [string]::Equals([string]$service.Status, 'Running', [System.StringComparison]::OrdinalIgnoreCase)) {" +
            "  Start-Service -Name $serviceName -ErrorAction Stop;" +
            "  Wait-ForServiceStatus -ServiceName $serviceName -DesiredStatus 'Running' -Timeout ([TimeSpan]::FromSeconds(45)) | Out-Null;" +
            "};" +
            "$item = Get-ServiceInventoryItem -ServiceName $serviceName;" +
            "Write-Output ('Service ' + $item.DisplayName + ' (' + $item.ServiceName + ') started. State=' + $item.State + '.');";
    }

    internal static string BuildStopServiceScriptBody(string serviceName)
    {
        return
            BuildGetServiceInventoryFunctions() +
            BuildWaitForServiceStatusFunction() +
            $"$serviceName='{EscapePowerShellSingleQuotedString(serviceName)}';" +
            "$service = Get-Service -Name $serviceName -ErrorAction Stop;" +
            "if (-not [string]::Equals([string]$service.Status, 'Stopped', [System.StringComparison]::OrdinalIgnoreCase)) {" +
            "  Stop-Service -Name $serviceName -Force -ErrorAction Stop;" +
            "  Wait-ForServiceStatus -ServiceName $serviceName -DesiredStatus 'Stopped' -Timeout ([TimeSpan]::FromSeconds(45)) | Out-Null;" +
            "};" +
            "$item = Get-ServiceInventoryItem -ServiceName $serviceName;" +
            "Write-Output ('Service ' + $item.DisplayName + ' (' + $item.ServiceName + ') stopped. State=' + $item.State + '.');";
    }

    internal static string BuildRestartServiceScriptBody(string serviceName)
    {
        return
            BuildGetServiceInventoryFunctions() +
            BuildWaitForServiceStatusFunction() +
            $"$serviceName='{EscapePowerShellSingleQuotedString(serviceName)}';" +
            "$service = Get-Service -Name $serviceName -ErrorAction Stop;" +
            "if (-not [string]::Equals([string]$service.Status, 'Stopped', [System.StringComparison]::OrdinalIgnoreCase)) {" +
            "  Stop-Service -Name $serviceName -Force -ErrorAction Stop;" +
            "  Wait-ForServiceStatus -ServiceName $serviceName -DesiredStatus 'Stopped' -Timeout ([TimeSpan]::FromSeconds(45)) | Out-Null;" +
            "};" +
            "Start-Service -Name $serviceName -ErrorAction Stop;" +
            "Wait-ForServiceStatus -ServiceName $serviceName -DesiredStatus 'Running' -Timeout ([TimeSpan]::FromSeconds(45)) | Out-Null;" +
            "$item = Get-ServiceInventoryItem -ServiceName $serviceName;" +
            "Write-Output ('Service ' + $item.DisplayName + ' (' + $item.ServiceName + ') restarted. State=' + $item.State + '.');";
    }

    internal static string BuildKillServiceProcessScriptBody(string serviceName)
    {
        return
            BuildGetServiceInventoryFunctions() +
            $"$serviceName='{EscapePowerShellSingleQuotedString(serviceName)}';" +
            "$item = Get-ServiceInventoryItem -ServiceName $serviceName;" +
            "if ($null -eq $item) { throw ('Service ' + $serviceName + ' was not found.') };" +
            "if ($item.ProcessId -le 0) { throw ('Could not determine a process id for service ' + $serviceName + '.') };" +
            "$process = Get-Process -Id $item.ProcessId -ErrorAction SilentlyContinue;" +
            "if ($null -eq $process) { throw ('Process ' + $item.ProcessId + ' for service ' + $serviceName + ' is no longer running.') };" +
            "$previousPid = $item.ProcessId;" +
            "Stop-Process -Id $previousPid -Force -ErrorAction Stop;" +
            "$deadline = (Get-Date).AddSeconds(20);" +
            "while ((Get-Date) -lt $deadline) {" +
            "  if (-not (Get-Process -Id $previousPid -ErrorAction SilentlyContinue)) { break };" +
            "  Start-Sleep -Milliseconds 400;" +
            "};" +
            "$refreshed = Get-ServiceInventoryItem -ServiceName $serviceName;" +
            "$state = if ($null -eq $refreshed) { 'Unknown' } else { $refreshed.State };" +
            "Write-Output ('Service process killed for ' + $item.DisplayName + ' (' + $item.ServiceName + '). PreviousPid=' + $previousPid + ' State=' + $state + '.');";
    }

    internal static string BuildSetStartModeScriptBody(string serviceName, WindowsServiceStartMode startMode)
    {
        var startModeToken = startMode switch
        {
            WindowsServiceStartMode.Automatic => "Automatic",
            WindowsServiceStartMode.AutomaticDelayedStart => "AutomaticDelayedStart",
            WindowsServiceStartMode.Disabled => "Disabled",
            _ => "Manual"
        };

        return
            BuildGetServiceInventoryFunctions() +
            $"$serviceName='{EscapePowerShellSingleQuotedString(serviceName)}';" +
            $"$startMode='{startModeToken}';" +
            "$service = Get-CimInstance -ClassName Win32_Service -Filter (\"Name='\" + $serviceName.Replace(\"'\", \"''\") + \"'\") -ErrorAction Stop;" +
            "if ($startMode -eq 'AutomaticDelayedStart') {" +
            "  $result = Invoke-CimMethod -InputObject $service -MethodName ChangeStartMode -Arguments @{ StartMode = 'Automatic' } -ErrorAction Stop;" +
            "  if ($null -ne $result.ReturnValue -and [uint32]$result.ReturnValue -ne 0) { throw ('Failed to change start mode. ReturnValue=' + $result.ReturnValue) };" +
            "  Set-ServiceDelayedAutoStart -ServiceName $serviceName -Enabled $true;" +
            "} elseif ($startMode -eq 'Automatic') {" +
            "  $result = Invoke-CimMethod -InputObject $service -MethodName ChangeStartMode -Arguments @{ StartMode = 'Automatic' } -ErrorAction Stop;" +
            "  if ($null -ne $result.ReturnValue -and [uint32]$result.ReturnValue -ne 0) { throw ('Failed to change start mode. ReturnValue=' + $result.ReturnValue) };" +
            "  Set-ServiceDelayedAutoStart -ServiceName $serviceName -Enabled $false;" +
            "} elseif ($startMode -eq 'Disabled') {" +
            "  $result = Invoke-CimMethod -InputObject $service -MethodName ChangeStartMode -Arguments @{ StartMode = 'Disabled' } -ErrorAction Stop;" +
            "  if ($null -ne $result.ReturnValue -and [uint32]$result.ReturnValue -ne 0) { throw ('Failed to change start mode. ReturnValue=' + $result.ReturnValue) };" +
            "  Set-ServiceDelayedAutoStart -ServiceName $serviceName -Enabled $false;" +
            "} else {" +
            "  $result = Invoke-CimMethod -InputObject $service -MethodName ChangeStartMode -Arguments @{ StartMode = 'Manual' } -ErrorAction Stop;" +
            "  if ($null -ne $result.ReturnValue -and [uint32]$result.ReturnValue -ne 0) { throw ('Failed to change start mode. ReturnValue=' + $result.ReturnValue) };" +
            "  Set-ServiceDelayedAutoStart -ServiceName $serviceName -Enabled $false;" +
            "};" +
            "$item = Get-ServiceInventoryItem -ServiceName $serviceName;" +
            "Write-Output ('Start mode set for ' + $item.DisplayName + ' (' + $item.ServiceName + ') to ' + $item.StartMode + '.');";
    }

    private static string BuildGetServiceInventoryFunctions()
    {
        return
            "function Get-ServiceStartModeValue {" +
            "  param([Parameter(Mandatory=$true)]$Service);" +
            "  $baseMode = [string]$Service.StartMode;" +
            "  if ([string]::Equals($baseMode, 'Auto', [System.StringComparison]::OrdinalIgnoreCase)) {" +
            "    $regPath = 'HKLM:\\SYSTEM\\CurrentControlSet\\Services\\' + [string]$Service.Name;" +
            "    $delayed = 0;" +
            "    try { $delayed = [int](Get-ItemProperty -LiteralPath $regPath -Name 'DelayedAutostart' -ErrorAction SilentlyContinue).DelayedAutostart } catch { $delayed = 0 };" +
            "    if ($delayed -eq 1) { return 'AutomaticDelayedStart' };" +
            "    return 'Automatic';" +
            "  };" +
            "  if ([string]::Equals($baseMode, 'Disabled', [System.StringComparison]::OrdinalIgnoreCase)) { return 'Disabled' };" +
            "  return 'Manual';" +
            "};" +
            "function Get-ServiceInventoryItem {" +
            "  param([string]$ServiceName, $Service);" +
            "  if ($null -eq $Service) {" +
            "    if ([string]::IsNullOrWhiteSpace($ServiceName)) { return $null };" +
            "    $escapedName = $ServiceName.Replace(\"'\", \"''\");" +
            "    $Service = Get-CimInstance -ClassName Win32_Service -Filter (\"Name='\" + $escapedName + \"'\") -ErrorAction Stop;" +
            "  };" +
            "  $processId = 0;" +
            "  try { $processId = [int]$Service.ProcessId } catch { $processId = 0 };" +
            "  [pscustomobject]@{" +
            "    ServiceName = [string]$Service.Name;" +
            "    DisplayName = [string]$Service.DisplayName;" +
            "    State = [string]$Service.State;" +
            "    StartMode = Get-ServiceStartModeValue -Service $Service;" +
            "    Description = [string]$Service.Description;" +
            "    ProcessId = if ($processId -gt 0) { $processId } else { $null }" +
            "  };" +
            "};" +
            "function Set-ServiceDelayedAutoStart {" +
            "  param([Parameter(Mandatory=$true)][string]$ServiceName, [Parameter(Mandatory=$true)][bool]$Enabled);" +
            "  $regPath = 'HKLM:\\SYSTEM\\CurrentControlSet\\Services\\' + $ServiceName;" +
            "  if (-not (Test-Path -LiteralPath $regPath)) { return };" +
            "  if ($Enabled) {" +
            "    New-ItemProperty -LiteralPath $regPath -Name 'DelayedAutostart' -PropertyType DWord -Value 1 -Force | Out-Null;" +
            "  } else {" +
            "    New-ItemProperty -LiteralPath $regPath -Name 'DelayedAutostart' -PropertyType DWord -Value 0 -Force | Out-Null;" +
            "  };" +
            "};";
    }

    private static string BuildWaitForServiceStatusFunction()
    {
        return
            "function Wait-ForServiceStatus {" +
            "  param(" +
            "    [Parameter(Mandatory=$true)][string]$ServiceName," +
            "    [Parameter(Mandatory=$true)][string]$DesiredStatus," +
            "    [Parameter(Mandatory=$true)][TimeSpan]$Timeout" +
            "  );" +
            "  $deadline = (Get-Date).Add($Timeout);" +
            "  while ((Get-Date) -lt $deadline) {" +
            "    $service = Get-Service -Name $ServiceName -ErrorAction Stop;" +
            "    if ([string]::Equals([string]$service.Status, $DesiredStatus, [System.StringComparison]::OrdinalIgnoreCase)) { return $service };" +
            "    Start-Sleep -Milliseconds 400;" +
            "  };" +
            "  $service = Get-Service -Name $ServiceName -ErrorAction Stop;" +
            "  throw ('Timed out waiting for service ' + $ServiceName + ' to reach status ' + $DesiredStatus + '. Current status=' + [string]$service.Status + '.');" +
            "};";
    }

    private static string EscapePowerShellSingleQuotedString(string value)
    {
        return string.IsNullOrEmpty(value)
            ? string.Empty
            : value.Replace("'", "''", StringComparison.Ordinal);
    }

    private static string NormalizeError(PowershellExecutionResult execution)
    {
        return string.IsNullOrWhiteSpace(execution.StdErr)
            ? string.IsNullOrWhiteSpace(execution.StdOut) ? "Unknown error." : execution.StdOut.Trim()
            : execution.StdErr.Trim();
    }

    private sealed class ServiceInventoryPayload
    {
        public ServiceInventoryPayloadItem[]? Services { get; set; }
        public string[]? Warnings { get; set; }
    }

    private sealed class ServiceInventoryPayloadItem
    {
        public string? ServiceName { get; set; }
        public string? DisplayName { get; set; }
        public string? State { get; set; }
        public string? StartMode { get; set; }
        public string? Description { get; set; }
        public int? ProcessId { get; set; }
    }
}
