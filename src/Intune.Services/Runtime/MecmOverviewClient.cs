using System.Globalization;
using System.Text.Json;
using WindowsClientCenter.Intune.Services.Models;

namespace WindowsClientCenter.Intune.Services.Runtime;

internal sealed class MecmOverviewClient(IPowerShellExecutor executor)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly string[] WorkloadOrder =
    [
        "Compliance Policies",
        "Windows Update Policies",
        "Resource Access Policies",
        "Endpoint Protection",
        "Device Configuration",
        "Office Click-to-Run Apps",
        "Client Apps"
    ];

    public async ValueTask<MecmOverviewSnapshot> GetOverviewAsync(string host, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(host))
        {
            return CreateEmptySnapshot(string.Empty, ["No host was provided."]);
        }

        var normalizedHost = host.Trim();

        try
        {
            var execution = await executor.ExecuteForHostAsync(normalizedHost, BuildGetOverviewScriptBody(), cancellationToken);
            if (execution.ExitCode != 0)
            {
                return CreateEmptySnapshot(normalizedHost, [$"MECM overview script failed: {NormalizeError(execution)}"]);
            }

            MecmOverviewPayload? payload;
            try
            {
                payload = JsonSerializer.Deserialize<MecmOverviewPayload>(ToJson(execution.StdOut), JsonOptions);
            }
            catch (Exception ex)
            {
                return CreateEmptySnapshot(
                    normalizedHost,
                    [
                        $"Failed to parse MECM overview payload: {FormatException(ex)}",
                        GetPayloadPreviewWarning(execution.StdOut)
                    ]);
            }

            if (payload is null)
            {
                return CreateEmptySnapshot(normalizedHost, ["MECM overview payload was empty."]);
            }

            var warnings = new List<string>(NormalizeWarnings(payload.Warnings));

            return new MecmOverviewSnapshot(
                normalizedHost,
                payload.ClientVersion ?? "Unknown",
                payload.AssignedSite ?? "Unknown",
                payload.ManagementPoint ?? "Unknown",
                payload.RebootPendingText ?? "Unknown",
                payload.CoManagementStateText ?? "Unknown",
                TryMapSection(() => BuildActivities(payload.Activities), warnings, "activities"),
                TryMapSection(() => BuildWorkloads(payload.Workloads), warnings, "workloads"),
                TryMapSection(() => BuildComponents(payload.Components), warnings, "components"),
                TryMapSection(() => BuildServices(payload.Services), warnings, "services"),
                TryMapSection(() => BuildHealthChecks(payload.HealthChecks), warnings, "health checks"),
                NormalizeWarnings(warnings));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return CreateEmptySnapshot(normalizedHost, [$"MECM overview mapping failed: {FormatException(ex)}"]);
        }
    }

    public async ValueTask<DeviceActionResult> ExecuteActionAsync(string host, MecmOverviewAction action, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(host))
        {
            return DeviceActionResult.Fail("No host was provided.", "no_host");
        }

        var normalizedHost = host.Trim();
        var execution = await executor.ExecuteForHostAsync(normalizedHost, BuildOverviewActionScriptBody(action), cancellationToken);

        return execution.ExitCode == 0
            ? DeviceActionResult.Ok(string.IsNullOrWhiteSpace(execution.StdOut)
                ? $"MECM overview action '{action}' requested on '{normalizedHost}'."
                : execution.StdOut.Trim())
            : DeviceActionResult.Fail(
                $"MECM overview action '{action}' failed on '{normalizedHost}': {NormalizeError(execution)}",
                "mecm_overview_action_failed");
    }

    private static MecmOverviewSnapshot CreateEmptySnapshot(string host, IReadOnlyList<string> warnings)
    {
        return new MecmOverviewSnapshot(
            host,
            "Unknown",
            "Unknown",
            "Unknown",
            "Unknown",
            "Unknown",
            [],
            WorkloadOrder.Select(static name => new MecmCoManagementWorkloadEntry(name, "Unknown", "Unknown", "No local co-management evidence was available.")).ToArray(),
            [],
            [],
            [],
            NormalizeWarnings(warnings));
    }

    private static IReadOnlyList<MecmOverviewActivityEntry> BuildActivities(MecmOverviewActivityPayloadItem[]? items)
    {
        return (items ?? [])
            .Select(static item => new MecmOverviewActivityEntry(
                item.Name ?? string.Empty,
                item.StatusText ?? "Unknown",
                item.StatusLevel ?? "Unknown",
                ParseDateTimeOffset(item.StartedUtc),
                ParseDateTimeOffset(item.ReportedUtc),
                item.Detail ?? string.Empty))
            .ToArray();
    }

    private static IReadOnlyList<MecmCoManagementWorkloadEntry> BuildWorkloads(MecmCoManagementWorkloadPayloadItem[]? items)
    {
        var mapped = (items ?? [])
            .Where(static item => !string.IsNullOrWhiteSpace(item.Name))
            .GroupBy(static item => item.Name!.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                static group => group.Key,
                static group =>
                {
                    var item = group.First();
                    return new MecmCoManagementWorkloadEntry(
                        item.Name ?? string.Empty,
                        item.Authority ?? "Unknown",
                        item.StatusLevel ?? "Unknown",
                        item.Detail ?? string.Empty);
                },
                StringComparer.OrdinalIgnoreCase);

        var ordered = new List<MecmCoManagementWorkloadEntry>(WorkloadOrder.Length);
        foreach (var workloadName in WorkloadOrder)
        {
            ordered.Add(mapped.TryGetValue(workloadName, out var entry)
                ? entry
                : new MecmCoManagementWorkloadEntry(workloadName, "Unknown", "Unknown", "No local co-management evidence was available."));
        }

        return ordered;
    }

    private static IReadOnlyList<MecmClientComponentEntry> BuildComponents(MecmClientComponentPayloadItem[]? items)
    {
        return (items ?? [])
            .Select(static item => new MecmClientComponentEntry(
                item.DisplayName ?? string.Empty,
                item.Name ?? string.Empty,
                item.Version ?? string.Empty,
                item.IsEnabled,
                item.StatusLevel ?? "Unknown",
                item.Detail ?? string.Empty))
            .OrderBy(static item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<MecmClientServiceEntry> BuildServices(MecmClientServicePayloadItem[]? items)
    {
        return (items ?? [])
            .Select(static item => new MecmClientServiceEntry(
                item.Name ?? string.Empty,
                item.DisplayName ?? string.Empty,
                item.Status ?? string.Empty,
                item.StartMode ?? string.Empty,
                item.StatusLevel ?? "Unknown",
                item.Detail ?? string.Empty))
            .OrderBy(static item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<MecmHealthCheckEntry> BuildHealthChecks(MecmHealthCheckPayloadItem[]? items)
    {
        return (items ?? [])
            .Select(static item => new MecmHealthCheckEntry(
                item.Name ?? string.Empty,
                item.StatusText ?? "Unknown",
                item.StatusLevel ?? "Unknown",
                item.Detail ?? string.Empty))
            .ToArray();
    }

    private static string BuildGetOverviewScriptBody()
    {
        return """
$ErrorActionPreference = 'Stop'
$warnings = New-Object System.Collections.Generic.List[string]

function Add-IccWarning([string]$message) {
  if ([string]::IsNullOrWhiteSpace($message)) { return }
  if (-not $warnings.Contains($message)) { $warnings.Add($message) | Out-Null }
}

function Get-IccErrorDetails([string]$stepName, $errorRecord) {
  $parts = New-Object System.Collections.ArrayList
  $message = if ($null -eq $errorRecord -or $null -eq $errorRecord.Exception) {
    $stepName + ' failed.'
  } else {
    $stepName + ' failed: ' + $errorRecord.Exception.Message
  }

  $null = $parts.Add($message)

  if ($null -ne $errorRecord) {
    if ($null -ne $errorRecord.Exception) {
      $null = $parts.Add('ExceptionType: ' + $errorRecord.Exception.GetType().FullName)
    }

    if (-not [string]::IsNullOrWhiteSpace($errorRecord.FullyQualifiedErrorId)) {
      $null = $parts.Add('FullyQualifiedErrorId: ' + $errorRecord.FullyQualifiedErrorId)
    }

    if ($null -ne $errorRecord.InvocationInfo) {
      if (-not [string]::IsNullOrWhiteSpace($errorRecord.InvocationInfo.Line)) {
        $null = $parts.Add('InvocationLine: ' + $errorRecord.InvocationInfo.Line.Trim())
      }

      if (-not [string]::IsNullOrWhiteSpace($errorRecord.InvocationInfo.PositionMessage)) {
        $position = ($errorRecord.InvocationInfo.PositionMessage -replace '\s+', ' ').Trim()
        $null = $parts.Add('Position: ' + $position)
      }
    }

    if (-not [string]::IsNullOrWhiteSpace($errorRecord.ScriptStackTrace)) {
      $stack = ($errorRecord.ScriptStackTrace -replace '\s+', ' ').Trim()
      $null = $parts.Add('ScriptStackTrace: ' + $stack)
    }
  }

  return ($parts -join ' | ')
}

function Test-IccCimClass([string]$namespace, [string]$className) {
  try {
    Get-CimClass -Namespace $namespace -ClassName $className -ErrorAction Stop | Out-Null
    return $true
  } catch {
    return $false
  }
}

function Convert-IccToDisplayString($value) {
  if ($null -eq $value) { return '' }
  if ($value -is [string]) { return $value.Trim() }
  return [string]$value
}

function Convert-IccToUtcString($value) {
  if ($null -eq $value) { return $null }
  try {
    if ($value -is [DateTimeOffset]) { return $value.ToUniversalTime().ToString('o') }
    if ($value -is [DateTime]) { return ([DateTimeOffset]$value.ToUniversalTime()).ToString('o') }
    if ($value -is [string] -and -not [string]::IsNullOrWhiteSpace($value)) {
      return ([DateTimeOffset]::Parse($value, [System.Globalization.CultureInfo]::InvariantCulture)).ToUniversalTime().ToString('o')
    }
  } catch {
  }
  return $null
}

function Convert-IccWmiDateToUtcString($value) {
  if ([string]::IsNullOrWhiteSpace($value)) { return $null }
  try {
    return ([System.Management.ManagementDateTimeConverter]::ToDateTime($value)).ToUniversalTime().ToString('o')
  } catch {
    return $null
  }
}

function Get-IccStatusLevelFromBoolean([Nullable[bool]]$value) {
  if ($null -eq $value) { return 'Unknown' }
  if ($value) { return 'Green' }
  return 'Red'
}

function Get-IccActivityEntry([string]$name, [string]$detail, $startedUtc, $reportedUtc) {
  $statusText = 'Unknown'
  $statusLevel = 'Unknown'
  if (-not [string]::IsNullOrWhiteSpace($reportedUtc)) {
    $statusText = 'Reported'
    $statusLevel = 'Green'
  } elseif (-not [string]::IsNullOrWhiteSpace($startedUtc)) {
    $statusText = 'Observed'
    $statusLevel = 'Yellow'
  }

  return [pscustomobject]@{
    Name = $name
    StatusText = $statusText
    StatusLevel = $statusLevel
    StartedUtc = $startedUtc
    ReportedUtc = $reportedUtc
    Detail = $detail
  }
}

function Get-IccServiceSnapshot([string]$name, [bool]$critical, [bool]$optional) {
  try {
    $service = Get-CimInstance -ClassName Win32_Service -Filter ("Name='" + $name + "'") -ErrorAction Stop | Select-Object -First 1
    if ($null -eq $service) {
      if (-not $optional) { Add-IccWarning("Required MECM-related service '" + $name + "' was not found.") }
      return [pscustomobject]@{
        Name = $name
        DisplayName = $name
        Status = 'Missing'
        StartMode = 'Unknown'
        StatusLevel = if ($optional) { 'Unknown' } else { 'Red' }
        Detail = if ($optional) { 'Optional service is not installed on this device.' } else { 'Required service is not installed on this device.' }
      }
    }

    $status = Convert-IccToDisplayString $service.State
    $startMode = Convert-IccToDisplayString $service.StartMode
    $statusLevel = 'Yellow'
    if ($status -eq 'Running') {
      $statusLevel = 'Green'
    } elseif ($critical) {
      $statusLevel = 'Red'
    }

    return [pscustomobject]@{
      Name = $name
      DisplayName = Convert-IccToDisplayString $service.DisplayName
      Status = if ([string]::IsNullOrWhiteSpace($status)) { 'Unknown' } else { $status }
      StartMode = if ([string]::IsNullOrWhiteSpace($startMode)) { 'Unknown' } else { $startMode }
      StatusLevel = $statusLevel
      Detail = 'Service state reported by Win32_Service.'
    }
  } catch {
    if (-not $optional) { Add-IccWarning((Get-IccErrorDetails ("Reading service state for '" + $name + "'") $_)) }
    return [pscustomobject]@{
      Name = $name
      DisplayName = $name
      Status = 'Unknown'
      StartMode = 'Unknown'
      StatusLevel = 'Unknown'
      Detail = if ($optional) { 'Optional service state could not be determined.' } else { 'Service state could not be determined.' }
    }
  }
}

function Get-IccCmTraceLines([string]$path) {
  if (-not (Test-Path -LiteralPath $path)) { return @() }
  try {
    return @(Get-Content -LiteralPath $path -Tail 4000 -ErrorAction Stop)
  } catch {
    Add-IccWarning((Get-IccErrorDetails ("Reading '" + [System.IO.Path]::GetFileName($path) + "'") $_))
    return @()
  }
}

function Convert-IccCmTraceLineToUtcString([string]$line) {
  if ([string]::IsNullOrWhiteSpace($line)) { return $null }
  $match = [regex]::Match($line, 'time="(?<time>[^"]+)"\s+date="(?<date>[^"]+)"')
  if (-not $match.Success) { return $null }

  $timeText = $match.Groups['time'].Value -replace '([+-]\d{3})$'
  $dateText = $match.Groups['date'].Value
  $formats = @(
    'MM-dd-yyyy HH:mm:ss.fff',
    'M-d-yyyy HH:mm:ss.fff',
    'MM-dd-yyyy HH:mm:ss',
    'M-d-yyyy HH:mm:ss'
  )

  foreach ($format in $formats) {
    try {
      $parsed = [DateTime]::ParseExact($dateText + ' ' + $timeText, $format, [System.Globalization.CultureInfo]::InvariantCulture)
      return ([DateTimeOffset]$parsed.ToUniversalTime()).ToString('o')
    } catch {
    }
  }

  return $null
}

function Get-IccLastLogEventUtc([string]$path, [string[]]$patterns) {
  $lines = Get-IccCmTraceLines $path
  if ($lines.Count -eq 0) { return $null }

  for ($index = $lines.Count - 1; $index -ge 0; $index--) {
    $line = [string]$lines[$index]
    foreach ($pattern in $patterns) {
      if ($line -match $pattern) {
        $timestamp = Convert-IccCmTraceLineToUtcString $line
        if (-not [string]::IsNullOrWhiteSpace($timestamp)) {
          return $timestamp
        }
      }
    }
  }

  return $null
}

function Get-IccLastRegexMatch([string[]]$lines, [string]$pattern) {
  if ($null -eq $lines -or $lines.Count -eq 0) { return $null }

  for ($index = $lines.Count - 1; $index -ge 0; $index--) {
    $line = [string]$lines[$index]
    $match = [regex]::Match($line, $pattern)
    if ($match.Success) {
      return [pscustomobject]@{
        Line = $line
        Match = $match
      }
    }
  }

  return $null
}

function Get-IccRegistryString([string]$path, [string]$name) {
  try {
    $value = (Get-ItemProperty -LiteralPath $path -Name $name -ErrorAction Stop).$name
    return Convert-IccToDisplayString $value
  } catch {
    return ''
  }
}

function Get-IccRegistryInt64([string]$path, [string]$name) {
  try {
    $value = (Get-ItemProperty -LiteralPath $path -Name $name -ErrorAction Stop).$name
    if ($null -eq $value) { return $null }
    return [long]$value
  } catch {
    return $null
  }
}

function Get-IccAssignedSite([string]$locationLogPath) {
  try {
    $assignedSiteResult = ([wmiclass]'ROOT\ccm:SMS_Client').GetAssignedSite()
    if ($null -ne $assignedSiteResult -and -not [string]::IsNullOrWhiteSpace($assignedSiteResult.sSiteCode)) {
      return Convert-IccToDisplayString $assignedSiteResult.sSiteCode
    }
  } catch {
    Add-IccWarning((Get-IccErrorDetails 'Invoking SMS_Client.GetAssignedSite()' $_))
  }

  $registryAssignedSite = Get-IccRegistryString 'HKLM:\SOFTWARE\Microsoft\SMS\Mobile Client' 'AssignedSiteCode'
  if (-not [string]::IsNullOrWhiteSpace($registryAssignedSite)) {
    return $registryAssignedSite
  }

  $registryGpAssignedSite = Get-IccRegistryString 'HKLM:\SOFTWARE\Microsoft\SMS\Mobile Client' 'GPRequestedSiteAssignmentCode'
  if (-not [string]::IsNullOrWhiteSpace($registryGpAssignedSite)) {
    return $registryGpAssignedSite
  }

  $lines = Get-IccCmTraceLines $locationLogPath
  foreach ($pattern in @(
    "(?i)Assigning to site '(?<site>[^']+)'",
    '(?i)Found Assigned Site Code <(?<site>[^>]+)>',
    '(?i)assigned site code <(?<site>[^>]+)>',
    '(?i)Successfully assigned to site (?<site>[A-Z0-9]{3})'
  )) {
    $matchResult = Get-IccLastRegexMatch $lines $pattern
    if ($null -ne $matchResult) {
      $site = Convert-IccToDisplayString $matchResult.Match.Groups['site'].Value
      if (-not [string]::IsNullOrWhiteSpace($site)) {
        return $site
      }
    }
  }

  return ''
}

function Get-IccManagementPoint([string]$assignedSite, [string]$locationLogPath) {
  if (-not [string]::IsNullOrWhiteSpace($assignedSite) -and $assignedSite -ne 'Unknown' -and (Test-IccCimClass 'ROOT\ccm' 'SMS_Authority')) {
    try {
      $escapedSite = $assignedSite.Replace("'", "''")
      $authority = Get-CimInstance -Namespace 'ROOT\ccm' -Query ("SELECT * FROM SMS_Authority WHERE Name='SMS:" + $escapedSite + "'") -ErrorAction Stop | Select-Object -First 1
      if ($null -ne $authority) {
        $currentManagementPoint = Convert-IccToDisplayString $authority.CurrentManagementPoint
        if (-not [string]::IsNullOrWhiteSpace($currentManagementPoint)) {
          return $currentManagementPoint
        }
      }
    } catch {
      Add-IccWarning((Get-IccErrorDetails 'Reading SMS_Authority' $_))
    }
  }

  $lines = Get-IccCmTraceLines $locationLogPath
  foreach ($pattern in @(
    '(?i)Current Management Point is\s+<?(?<mp>[A-Z0-9._-]+)>?',
    "(?i)Current management point.*?'(?<mp>[^']+)'",
    '(?i)Retrieved MP \[(?<mp>[^\]]+)\]',
    '(?i)management point.*?<(?<mp>[A-Z0-9._-]+)>'
  )) {
    $matchResult = Get-IccLastRegexMatch $lines $pattern
    if ($null -ne $matchResult) {
      $managementPoint = Convert-IccToDisplayString $matchResult.Match.Groups['mp'].Value
      if (-not [string]::IsNullOrWhiteSpace($managementPoint)) {
        return $managementPoint
      }
    }
  }

  return ''
}

function Get-IccCoManagementFlags([string]$logPath) {
  $lines = Get-IccCmTraceLines $logPath
  $registryFlags = Get-IccRegistryInt64 'HKLM:\SOFTWARE\Microsoft\CCM' 'CoManagementFlags'
  if ($null -ne $registryFlags) {
    $detail = 'Registry HKLM:\SOFTWARE\Microsoft\CCM\CoManagementFlags = ' + $registryFlags + '.'
    $mergedFlagsMatch = Get-IccLastRegexMatch $lines "(?i)New merged workloadflags value with co-management max capabilities '(?<cap>\d+)' is '(?<flags>-?\d+)'"
    if ($null -ne $mergedFlagsMatch) {
      $rawFlags = [long]$mergedFlagsMatch.Match.Groups['flags'].Value
      $maxCapabilities = [long]$mergedFlagsMatch.Match.Groups['cap'].Value
      $detail += ' CoManagementHandler.log last merged value was ' + ($rawFlags -band $maxCapabilities) + ' (raw ' + $rawFlags + ', max capabilities ' + $maxCapabilities + ').'
    }

    return [pscustomobject]@{
      EffectiveFlags = $registryFlags
      RawFlags = $registryFlags
      MaxCapabilities = $null
      Source = 'Registry'
      Detail = $detail
    }
  }

  if ($lines.Count -eq 0) {
    return [pscustomobject]@{
      EffectiveFlags = $null
      RawFlags = $null
      MaxCapabilities = $null
      Source = 'None'
      Detail = 'No local co-management evidence was available.'
    }
  }

  $mergedFlagsMatch = Get-IccLastRegexMatch $lines "(?i)New merged workloadflags value with co-management max capabilities '(?<cap>\d+)' is '(?<flags>-?\d+)'"
  if ($null -ne $mergedFlagsMatch) {
    $rawFlags = [long]$mergedFlagsMatch.Match.Groups['flags'].Value
    $maxCapabilities = [long]$mergedFlagsMatch.Match.Groups['cap'].Value
    return [pscustomobject]@{
      EffectiveFlags = ($rawFlags -band $maxCapabilities)
      RawFlags = $rawFlags
      MaxCapabilities = $maxCapabilities
      Source = 'MergedFlags'
      Detail = 'CoManagementHandler.log merged workloadFlags to ' + ($rawFlags -band $maxCapabilities) + ' (raw ' + $rawFlags + ', max capabilities ' + $maxCapabilities + ').'
    }
  }

  $capabilitiesMatch = Get-IccLastRegexMatch $lines "(?i)Merged value for setting 'CoManagementSettings_Capabilities' is '(?<flags>-?\d+)'"
  if ($null -ne $capabilitiesMatch) {
    $flags = [long]$capabilitiesMatch.Match.Groups['flags'].Value
    return [pscustomobject]@{
      EffectiveFlags = $flags
      RawFlags = $flags
      MaxCapabilities = $null
      Source = 'Capabilities'
      Detail = "CoManagementHandler.log reported CoManagementSettings_Capabilities = $flags."
    }
  }

  $retrievedMatch = Get-IccLastRegexMatch $lines "(?i)Workloads flag retrieved (?<flags>-?\d+)"
  if ($null -ne $retrievedMatch) {
    $flags = [long]$retrievedMatch.Match.Groups['flags'].Value
    return [pscustomobject]@{
      EffectiveFlags = if ($flags -ge 0 -and $flags -le 4095) { $flags } else { $null }
      RawFlags = $flags
      MaxCapabilities = $null
      Source = 'RetrievedFlags'
      Detail = 'CoManagementHandler.log reported raw workloadFlags = ' + $flags + '.'
    }
  }

  return [pscustomobject]@{
    EffectiveFlags = $null
    RawFlags = $null
    MaxCapabilities = $null
    Source = 'Unknown'
    Detail = 'CoManagementHandler.log did not contain a recognizable workloadFlags value.'
  }
}

function Get-IccCoManagementWorkloads($coManagementFlags) {
  $definitions = @(
    [pscustomobject]@{ Name = 'Compliance Policies'; Bit = 2 },
    [pscustomobject]@{ Name = 'Windows Update Policies'; Bit = 16 },
    [pscustomobject]@{ Name = 'Resource Access Policies'; Bit = 4 },
    [pscustomobject]@{ Name = 'Endpoint Protection'; Bit = 32 },
    [pscustomobject]@{ Name = 'Device Configuration'; Bit = 8 },
    [pscustomobject]@{ Name = 'Office Click-to-Run Apps'; Bit = 128 },
    [pscustomobject]@{ Name = 'Client Apps'; Bit = 64 }
  )

  $entries = New-Object System.Collections.ArrayList
  foreach ($definition in @($definitions | Sort-Object Name)) {
    $authority = 'Unknown'
    $statusLevel = 'Unknown'
    $detail = $coManagementFlags.Detail

    if ($null -ne $coManagementFlags.EffectiveFlags) {
      $isIntuneManaged = (($coManagementFlags.EffectiveFlags -band [long]$definition.Bit) -eq [long]$definition.Bit)
      $bitState = if ($isIntuneManaged) { 'enabled' } else { 'disabled' }
      $authority = if ($isIntuneManaged) { 'Intune' } else { 'ConfigMgr' }
      $statusLevel = 'Green'
      $detail = $coManagementFlags.Detail + ' Capability bit ' + $definition.Bit + ' for "' + $definition.Name + '" is ' + $bitState + '.'
    }

    $null = $entries.Add([pscustomobject]@{
      Name = $definition.Name
      Authority = $authority
      StatusLevel = $statusLevel
      Detail = $detail
    })
  }

  return @($entries)
}

$services = @(
  (Get-IccServiceSnapshot 'CcmExec' $true $false),
  (Get-IccServiceSnapshot 'ccmsetup' $false $true),
  (Get-IccServiceSnapshot 'BITS' $false $false),
  (Get-IccServiceSnapshot 'wuauserv' $false $false),
  (Get-IccServiceSnapshot 'lppsvc' $false $false),
  (Get-IccServiceSnapshot 'Winmgmt' $true $false)
)

$cmRcService = Get-IccServiceSnapshot 'CmRcService' $false $true
if ($cmRcService.Status -ne 'Missing') {
  $services += $cmRcService
}

$clientVersion = 'Unknown'
$assignedSite = 'Unknown'
$managementPoint = 'Unknown'
$clientId = ''
$actualConfigAvailable = $false
$inventoryById = @{}

if (Test-IccCimClass 'ROOT\ccm' 'SMS_Client') {
  try {
    $smsClient = Get-CimInstance -Namespace 'ROOT\ccm' -ClassName 'SMS_Client' -ErrorAction Stop
    $clientVersion = Convert-IccToDisplayString $smsClient.ClientVersion
    if (-not [string]::IsNullOrWhiteSpace($smsClient.AssignedSiteCode)) {
      $assignedSite = Convert-IccToDisplayString $smsClient.AssignedSiteCode
    } elseif ($smsClient.PSObject.Methods.Name -contains 'GetAssignedSite') {
      $assignedSiteResult = Invoke-CimMethod -InputObject $smsClient -MethodName 'GetAssignedSite' -ErrorAction Stop
      if (-not [string]::IsNullOrWhiteSpace($assignedSiteResult.sSiteCode)) {
        $assignedSite = Convert-IccToDisplayString $assignedSiteResult.sSiteCode
      }
    }
  } catch {
    Add-IccWarning((Get-IccErrorDetails 'Reading SMS_Client' $_))
  }
} else {
  Add-IccWarning('MECM client WMI class SMS_Client is not available.')
}

if (Test-IccCimClass 'ROOT\ccm' 'CCM_Client') {
  try {
    $ccmClient = Get-CimInstance -Namespace 'ROOT\ccm' -ClassName 'CCM_Client' -ErrorAction Stop
    $clientId = Convert-IccToDisplayString $ccmClient.ClientId
  } catch {
    Add-IccWarning((Get-IccErrorDetails 'Reading CCM_Client' $_))
  }
}

if (-not [string]::IsNullOrWhiteSpace($assignedSite) -and $assignedSite -ne 'Unknown' -and (Test-IccCimClass 'ROOT\ccm' 'SMS_Authority')) {
  try {
    $escapedSite = $assignedSite.Replace("'", "''")
    $authority = Get-CimInstance -Namespace 'ROOT\ccm' -Query ("SELECT * FROM SMS_Authority WHERE Name='SMS:" + $escapedSite + "'") -ErrorAction Stop | Select-Object -First 1
    if ($null -ne $authority) {
      $managementPoint = Convert-IccToDisplayString $authority.CurrentManagementPoint
    }
  } catch {
    Add-IccWarning((Get-IccErrorDetails 'Reading SMS_Authority' $_))
  }
}

if (Test-IccCimClass 'ROOT\ccm\Policy\Machine\ActualConfig' 'CCM_ComponentClientConfig') {
  $actualConfigAvailable = $true
}

if (Test-IccCimClass 'ROOT\ccm\invagt' 'InventoryActionStatus') {
  try {
    foreach ($item in @(Get-CimInstance -Namespace 'ROOT\ccm\invagt' -ClassName 'InventoryActionStatus' -ErrorAction Stop)) {
      $inventoryById[[string]$item.InventoryActionID] = [pscustomobject]@{
        StartedUtc = Convert-IccWmiDateToUtcString $item.LastCycleStartedDate
        ReportedUtc = Convert-IccWmiDateToUtcString $item.LastReportDate
      }
    }
  } catch {
    Add-IccWarning((Get-IccErrorDetails 'Reading InventoryActionStatus' $_))
  }
} else {
  Add-IccWarning('InventoryActionStatus is not available in ROOT\\ccm\\invagt.')
}

$agentPath = Join-Path $env:windir 'CCM'
$policyLog = Join-Path $agentPath 'Logs\PolicyAgent.log'
$inventoryLog = Join-Path $agentPath 'Logs\InventoryAgent.log'
$coManagementLog = Join-Path $agentPath 'Logs\CoManagementHandler.log'
$locationServicesLog = Join-Path $agentPath 'Logs\LocationServices.log'
$ccmEvalReportPath = Join-Path $agentPath 'CcmEvalReport.xml'

if ([string]::IsNullOrWhiteSpace($assignedSite) -or $assignedSite -eq 'Unknown') {
  $resolvedAssignedSite = Get-IccAssignedSite $locationServicesLog
  if (-not [string]::IsNullOrWhiteSpace($resolvedAssignedSite)) {
    $assignedSite = $resolvedAssignedSite
  }
}

if ([string]::IsNullOrWhiteSpace($managementPoint) -or $managementPoint -eq 'Unknown') {
  $resolvedManagementPoint = Get-IccManagementPoint $assignedSite $locationServicesLog
  if (-not [string]::IsNullOrWhiteSpace($resolvedManagementPoint)) {
    $managementPoint = $resolvedManagementPoint
  }
}

$machinePolicyRequestUtc = Get-IccLastLogEventUtc $policyLog @(
  '(?i)requesting machine assignments',
  '(?i)machine policy.*request',
  '(?i)retriev(e|ing).*machine policy'
)

$machinePolicyEvaluationUtc = Get-IccLastLogEventUtc $policyLog @(
  '(?i)evaluat(e|ing).*machine policy',
  '(?i)machine policy.*evaluation',
  '(?i)completed.*machine policy'
)

if ([string]::IsNullOrWhiteSpace($machinePolicyRequestUtc)) {
  Add-IccWarning('The last machine policy request could not be determined locally.')
}

if ([string]::IsNullOrWhiteSpace($machinePolicyEvaluationUtc)) {
  Add-IccWarning('The last machine policy evaluation could not be determined locally.')
}

$lastRebootUtc = $null
try {
  $operatingSystem = Get-CimInstance -ClassName Win32_OperatingSystem -ErrorAction Stop
  $lastRebootUtc = Convert-IccToUtcString $operatingSystem.LastBootUpTime
} catch {
  Add-IccWarning((Get-IccErrorDetails 'Reading last reboot time' $_))
}

$rebootPendingText = 'Unknown'
$rebootPendingLevel = 'Unknown'
try {
  $rebootResult = Invoke-CimMethod -Namespace 'ROOT\ccm\ClientSDK' -ClassName 'CCM_ClientUtilities' -MethodName 'DetermineIfRebootPending' -ErrorAction Stop
  $rebootPending = [bool]$rebootResult.RebootPending -or [bool]$rebootResult.IsHardRebootPending
  $rebootPendingText = if ($rebootPending) { 'Pending' } else { 'No' }
  $rebootPendingLevel = if ($rebootPending) { 'Yellow' } else { 'Green' }
} catch {
  Add-IccWarning((Get-IccErrorDetails 'Reading MECM reboot state' $_))
}

$components = New-Object System.Collections.ArrayList
$componentEnabledByName = @{}
if ($actualConfigAvailable) {
  try {
    foreach ($config in @(Get-CimInstance -Namespace 'ROOT\ccm\Policy\Machine\ActualConfig' -ClassName 'CCM_ComponentClientConfig' -ErrorAction Stop)) {
      $componentEnabledByName[[string]$config.ComponentName] = $config.Enabled
    }
  } catch {
    Add-IccWarning((Get-IccErrorDetails 'Reading CCM_ComponentClientConfig' $_))
  }
}

if (Test-IccCimClass 'ROOT\ccm' 'CCM_InstalledComponent') {
  try {
    foreach ($component in @(Get-CimInstance -Namespace 'ROOT\ccm' -ClassName 'CCM_InstalledComponent' -ErrorAction Stop)) {
      $name = Convert-IccToDisplayString $component.Name
      $displayName = Convert-IccToDisplayString $component.DisplayName
      $enabled = $null
      if ($componentEnabledByName.ContainsKey($name)) {
        $enabled = [bool]$componentEnabledByName[$name]
      }

      $null = $components.Add([pscustomobject]@{
        DisplayName = if ([string]::IsNullOrWhiteSpace($displayName)) { $name } else { $displayName }
        Name = $name
        Version = Convert-IccToDisplayString $component.Version
        IsEnabled = $enabled
        StatusLevel = if ($null -eq $enabled) { 'Unknown' } elseif ($enabled) { 'Green' } else { 'Yellow' }
        Detail = if ($null -eq $enabled) { 'Enabled state is not available in ActualConfig.' } elseif ($enabled) { 'Component is enabled by MECM policy.' } else { 'Component is present but disabled by MECM policy.' }
      })
    }
  } catch {
    Add-IccWarning((Get-IccErrorDetails 'Reading CCM_InstalledComponent' $_))
  }
}

$ccmEvalUtc = $null
$ccmEvalStatusText = 'Unknown'
$ccmEvalStatusLevel = 'Unknown'
$ccmEvalDetail = 'CCMEval report is not available.'
if (Test-Path -LiteralPath $ccmEvalReportPath) {
  try {
    [xml]$ccmEvalReport = Get-Content -LiteralPath $ccmEvalReportPath -Raw -ErrorAction Stop
    $ccmEvalUtc = Convert-IccToUtcString $ccmEvalReport.ClientHealthReport.Summary.EvaluationTime
    $failedChecks = @($ccmEvalReport.ClientHealthReport.HealthChecks.HealthCheck | Where-Object {
      (Convert-IccToDisplayString $_.ResultType) -match '(?i)fail|error' -or
      (Convert-IccToDisplayString $_.ResultCode) -notin @('', '0')
    })
    if ($failedChecks.Count -eq 0) {
      $ccmEvalStatusText = 'Healthy'
      $ccmEvalStatusLevel = 'Green'
      $ccmEvalDetail = 'The latest CCMEval report does not contain failed checks.'
    } else {
      $ccmEvalStatusText = 'Issues detected'
      $ccmEvalStatusLevel = 'Yellow'
      $ccmEvalDetail = 'The latest CCMEval report contains ' + $failedChecks.Count + ' failed or non-zero health check(s).'
    }
  } catch {
    Add-IccWarning((Get-IccErrorDetails 'Reading CcmEvalReport.xml' $_))
  }
} else {
  Add-IccWarning('CcmEvalReport.xml was not available on the target client.')
}

$coManagementFlags = Get-IccCoManagementFlags $coManagementLog
$workloads = Get-IccCoManagementWorkloads $coManagementFlags
$coManagementStateText = if ($null -ne $coManagementFlags.EffectiveFlags -or (Test-Path -LiteralPath $coManagementLog)) { 'Active' } else { 'Unknown' }

$activities = @(
  (Get-IccActivityEntry 'Heartbeat Discovery' 'Derived from InventoryActionStatus for discovery data.' $inventoryById['{00000000-0000-0000-0000-000000000003}'].StartedUtc $inventoryById['{00000000-0000-0000-0000-000000000003}'].ReportedUtc),
  (Get-IccActivityEntry 'Hardware Inventory' 'Derived from InventoryActionStatus for hardware inventory.' $inventoryById['{00000000-0000-0000-0000-000000000001}'].StartedUtc $inventoryById['{00000000-0000-0000-0000-000000000001}'].ReportedUtc),
  (Get-IccActivityEntry 'Software Inventory' 'Derived from InventoryActionStatus for software inventory.' $inventoryById['{00000000-0000-0000-0000-000000000002}'].StartedUtc $inventoryById['{00000000-0000-0000-0000-000000000002}'].ReportedUtc),
  (Get-IccActivityEntry 'Machine Policy Request' 'Best effort from PolicyAgent.log.' $machinePolicyRequestUtc $null),
  (Get-IccActivityEntry 'Machine Policy Evaluation' 'Best effort from PolicyAgent.log.' $machinePolicyEvaluationUtc $null),
  (Get-IccActivityEntry 'CCMEval' 'Latest evaluation timestamp from CcmEvalReport.xml.' $ccmEvalUtc $null),
  (Get-IccActivityEntry 'Last Reboot' 'Last boot time from Win32_OperatingSystem.' $lastRebootUtc $null)
)

$ccmExecService = $services | Where-Object Name -eq 'CcmExec' | Select-Object -First 1
$bitsService = $services | Where-Object Name -eq 'BITS' | Select-Object -First 1
$wuaService = $services | Where-Object Name -eq 'wuauserv' | Select-Object -First 1

$healthChecks = @(
  [pscustomobject]@{
    Name = 'WMI'
    StatusText = if ($clientVersion -ne 'Unknown') { 'Healthy' } else { 'Unavailable' }
    StatusLevel = if ($clientVersion -ne 'Unknown') { 'Green' } else { 'Red' }
    Detail = if ($clientVersion -ne 'Unknown') { 'SMS_Client is reachable in ROOT\\ccm.' } else { 'The MECM client WMI namespace could not be queried.' }
  },
  [pscustomobject]@{
    Name = 'SMS Agent Host'
    StatusText = if ($ccmExecService.Status -eq 'Running') { 'Healthy' } else { $ccmExecService.Status }
    StatusLevel = if ($ccmExecService.Status -eq 'Running') { 'Green' } else { 'Red' }
    Detail = 'Status derived from the CcmExec service.'
  },
  [pscustomobject]@{
    Name = 'Policy Platform'
    StatusText = if ($actualConfigAvailable) { 'Healthy' } else { 'Unknown' }
    StatusLevel = if ($actualConfigAvailable) { 'Green' } else { 'Unknown' }
    Detail = if ($actualConfigAvailable) { 'ROOT\\ccm\\Policy\\Machine\\ActualConfig is reachable.' } else { 'The local policy platform could not be verified.' }
  },
  [pscustomobject]@{
    Name = 'BITS'
    StatusText = if ($bitsService.Status -eq 'Running') { 'Healthy' } else { $bitsService.Status }
    StatusLevel = if ($bitsService.Status -eq 'Running') { 'Green' } elseif ($bitsService.Status -eq 'Missing') { 'Red' } else { 'Yellow' }
    Detail = 'BITS service state for MECM download activity.'
  },
  [pscustomobject]@{
    Name = 'Windows Update Service'
    StatusText = if ($wuaService.Status -eq 'Running') { 'Healthy' } else { $wuaService.Status }
    StatusLevel = if ($wuaService.Status -eq 'Running') { 'Green' } elseif ($wuaService.Status -eq 'Missing') { 'Red' } else { 'Yellow' }
    Detail = 'wuauserv state used for MECM software update interactions.'
  },
  [pscustomobject]@{
    Name = 'Client Registration / MP'
    StatusText = if (-not [string]::IsNullOrWhiteSpace($clientId) -and -not [string]::IsNullOrWhiteSpace($managementPoint) -and $managementPoint -ne 'Unknown') { 'Healthy' } else { 'Degraded' }
    StatusLevel = if (-not [string]::IsNullOrWhiteSpace($clientId) -and -not [string]::IsNullOrWhiteSpace($managementPoint) -and $managementPoint -ne 'Unknown') { 'Green' } else { 'Yellow' }
    Detail = if (-not [string]::IsNullOrWhiteSpace($clientId) -and -not [string]::IsNullOrWhiteSpace($managementPoint) -and $managementPoint -ne 'Unknown') { 'Client ID and current management point are available.' } else { 'Client ID or management point could not be fully resolved locally.' }
  },
  [pscustomobject]@{
    Name = 'CCMEval Status'
    StatusText = $ccmEvalStatusText
    StatusLevel = $ccmEvalStatusLevel
    Detail = $ccmEvalDetail
  }
)

[pscustomobject]@{
  ClientVersion = if ([string]::IsNullOrWhiteSpace($clientVersion)) { 'Unknown' } else { $clientVersion }
  AssignedSite = if ([string]::IsNullOrWhiteSpace($assignedSite)) { 'Unknown' } else { $assignedSite }
  ManagementPoint = if ([string]::IsNullOrWhiteSpace($managementPoint)) { 'Unknown' } else { $managementPoint }
  RebootPendingText = $rebootPendingText
  CoManagementStateText = $coManagementStateText
  Activities = @($activities)
  Workloads = @($workloads)
  Components = @($components)
  Services = @($services)
  HealthChecks = @($healthChecks)
  Warnings = @($warnings)
} | ConvertTo-Json -Depth 8 -Compress
""";
    }

    private static string BuildOverviewActionScriptBody(MecmOverviewAction action)
    {
        var actionBody = action switch
        {
            MecmOverviewAction.RequestMachinePolicy => """
Invoke-IccTriggerSchedule '{00000000-0000-0000-0000-000000000021}'
Write-Output 'Requested MECM machine policy assignments.'
""",
            MecmOverviewAction.EvaluateMachinePolicy => """
Invoke-IccTriggerSchedule '{00000000-0000-0000-0000-000000000022}'
Write-Output 'Triggered MECM machine policy evaluation.'
""",
            MecmOverviewAction.TriggerHeartbeatDiscovery => """
Invoke-IccTriggerSchedule '{00000000-0000-0000-0000-000000000003}'
Write-Output 'Triggered MECM heartbeat discovery / discovery data cycle.'
""",
            MecmOverviewAction.TriggerHardwareInventory => """
Invoke-IccTriggerSchedule '{00000000-0000-0000-0000-000000000001}'
Write-Output 'Triggered MECM hardware inventory.'
""",
            MecmOverviewAction.TriggerSoftwareInventory => """
Invoke-IccTriggerSchedule '{00000000-0000-0000-0000-000000000002}'
Write-Output 'Triggered MECM software inventory.'
""",
            MecmOverviewAction.RunCcmeval => """
$ccmEvalPath = Join-Path (Join-Path $env:windir 'CCM') 'ccmeval.exe'
if (-not (Test-Path -LiteralPath $ccmEvalPath)) {
  throw 'ccmeval.exe was not found under %WINDIR%\CCM.'
}
Start-Process -FilePath $ccmEvalPath -WindowStyle Hidden | Out-Null
Write-Output 'Started CCMEval.'
""",
            MecmOverviewAction.RestartCcmExec => """
Restart-Service -Name 'CcmExec' -Force -ErrorAction Stop
Write-Output 'Restarted SMS Agent Host (CcmExec).'
""",
            MecmOverviewAction.ResetPolicySoft => """
Invoke-IccResetPolicy 0
Invoke-IccTriggerSchedule '{00000000-0000-0000-0000-000000000040}'
Invoke-IccTriggerSchedule '{00000000-0000-0000-0000-000000000021}'
Write-Output 'Triggered soft MECM policy reset and requested fresh machine policy.'
""",
            MecmOverviewAction.ResetPolicyHard => """
Invoke-IccResetPolicy 1
Invoke-IccTriggerSchedule '{00000000-0000-0000-0000-000000000040}'
Invoke-IccTriggerSchedule '{00000000-0000-0000-0000-000000000021}'
Write-Output 'Triggered hard MECM policy reset and requested fresh machine policy.'
""",
            MecmOverviewAction.RepairClient => """
$productCode = (Get-WmiObject -Namespace 'root\ccm' -Class 'CCM_InstalledProduct' -ErrorAction Stop | Select-Object -First 1 -ExpandProperty ProductCode)
if ([string]::IsNullOrWhiteSpace($productCode)) {
  throw 'The MECM client product code could not be determined.'
}
Start-Process -FilePath 'msiexec.exe' -ArgumentList @('/fpecms', $productCode) -WindowStyle Hidden | Out-Null
Write-Output ('Started MECM client MSI repair for product code ' + $productCode + '.')
""",
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Unsupported MECM overview action.")
        };

        return $$"""
$ErrorActionPreference = 'Stop'

function Invoke-IccTriggerSchedule([string]$scheduleId) {
  $result = ([wmiclass]'ROOT\ccm:SMS_Client').TriggerSchedule($scheduleId)
  if ($null -ne $result -and $null -ne $result.ReturnValue -and [uint32]$result.ReturnValue -ne 0) {
    throw ('TriggerSchedule for ' + $scheduleId + ' failed with 0x{0:X8}.' -f [uint32]$result.ReturnValue)
  }
}

function Invoke-IccResetPolicy([int]$mode) {
  $result = ([wmiclass]'ROOT\ccm:SMS_Client').ResetPolicy($mode)
  if ($null -ne $result -and $null -ne $result.ReturnValue -and [uint32]$result.ReturnValue -ne 0) {
    throw ('ResetPolicy(' + $mode + ') failed with 0x{0:X8}.' -f [uint32]$result.ReturnValue)
  }
}

{{actionBody}}
""";
    }

    private static string ToJson(string? stdOut)
    {
        return string.IsNullOrWhiteSpace(stdOut) ? "{}" : stdOut;
    }

    private static string NormalizeError(PowershellExecutionResult execution)
    {
        return string.IsNullOrWhiteSpace(execution.StdErr)
            ? string.IsNullOrWhiteSpace(execution.StdOut) ? "Unknown error." : execution.StdOut.Trim()
            : execution.StdErr.Trim();
    }

    private static IReadOnlyList<string> NormalizeWarnings(IEnumerable<string>? warnings)
    {
        return warnings?
            .Where(static warning => !string.IsNullOrWhiteSpace(warning))
            .Select(static warning => warning.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray() ?? [];
    }

    private static DateTimeOffset? ParseDateTimeOffset(string? value)
    {
        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : null;
    }

    private static IReadOnlyList<T> TryMapSection<T>(Func<IReadOnlyList<T>> map, ICollection<string> warnings, string sectionName)
    {
        try
        {
            return map();
        }
        catch (Exception ex)
        {
            warnings.Add($"MECM overview {sectionName} mapping failed: {FormatException(ex)}");
            return [];
        }
    }

    private static string FormatException(Exception ex)
    {
        var parts = new List<string> { $"{ex.GetType().Name}: {ex.Message}" };
        if (ex.InnerException is not null)
        {
            parts.Add($"InnerException: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
        }

        return string.Join(" | ", parts);
    }

    private static string GetPayloadPreviewWarning(string? stdOut)
    {
        if (string.IsNullOrWhiteSpace(stdOut))
        {
            return "The MECM overview payload was empty.";
        }

        var normalized = stdOut.ReplaceLineEndings(" ").Trim();
        if (normalized.Length > 400)
        {
            normalized = normalized[..400] + "...";
        }

        return $"Payload preview: {normalized}";
    }

    private sealed class MecmOverviewPayload
    {
        public string? ClientVersion { get; set; }
        public string? AssignedSite { get; set; }
        public string? ManagementPoint { get; set; }
        public string? RebootPendingText { get; set; }
        public string? CoManagementStateText { get; set; }
        public MecmOverviewActivityPayloadItem[]? Activities { get; set; }
        public MecmCoManagementWorkloadPayloadItem[]? Workloads { get; set; }
        public MecmClientComponentPayloadItem[]? Components { get; set; }
        public MecmClientServicePayloadItem[]? Services { get; set; }
        public MecmHealthCheckPayloadItem[]? HealthChecks { get; set; }
        public string[]? Warnings { get; set; }
    }

    private sealed class MecmOverviewActivityPayloadItem
    {
        public string? Name { get; set; }
        public string? StatusText { get; set; }
        public string? StatusLevel { get; set; }
        public string? StartedUtc { get; set; }
        public string? ReportedUtc { get; set; }
        public string? Detail { get; set; }
    }

    private sealed class MecmCoManagementWorkloadPayloadItem
    {
        public string? Name { get; set; }
        public string? Authority { get; set; }
        public string? StatusLevel { get; set; }
        public string? Detail { get; set; }
    }

    private sealed class MecmClientComponentPayloadItem
    {
        public string? DisplayName { get; set; }
        public string? Name { get; set; }
        public string? Version { get; set; }
        public bool? IsEnabled { get; set; }
        public string? StatusLevel { get; set; }
        public string? Detail { get; set; }
    }

    private sealed class MecmClientServicePayloadItem
    {
        public string? Name { get; set; }
        public string? DisplayName { get; set; }
        public string? Status { get; set; }
        public string? StartMode { get; set; }
        public string? StatusLevel { get; set; }
        public string? Detail { get; set; }
    }

    private sealed class MecmHealthCheckPayloadItem
    {
        public string? Name { get; set; }
        public string? StatusText { get; set; }
        public string? StatusLevel { get; set; }
        public string? Detail { get; set; }
    }
}
