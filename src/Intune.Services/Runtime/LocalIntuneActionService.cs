using System.Globalization;
using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Win32;
using WindowsClientCenter.Intune.Services.Contracts;
using WindowsClientCenter.Intune.Services.Models;

namespace WindowsClientCenter.Intune.Services.Runtime;

internal sealed partial class LocalIntuneActionService(IPowerShellExecutor executor) : ILocalIntuneActionService
{
    private const string MdmAdminLogName = "Microsoft-Windows-DeviceManagement-Enterprise-Diagnostics-Provider/Admin";
    private const string SystemIdentityId = "00000000-0000-0000-0000-000000000000";
    private const string PolicyDefinitionsRootOverrideEnvironmentVariable = "ICC_POLICY_DEFINITIONS_ROOT";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };
    private static readonly HashSet<string> PolicyIdPropertyNames = ["Id", "ID", "AppId", "AppID", "ApplicationId", "Win32AppId"];
    private static readonly HashSet<string> PolicyNamePropertyNames = ["Name", "DisplayName", "AppName", "Title", "ApplicationName"];
    private static readonly HashSet<string> PolicyIntentPropertyNames = ["Intent", "InstallIntent", "AssignmentIntent"];
    private static readonly HashSet<string> PolicyTargetTypePropertyNames = ["TargetType", "TargetingType"];
    private static readonly string[] PolicyScopeTokens = ["scope", "context", "targetscope", "userscope"];
    private static readonly string[] PolicyAreaTokens = ["area", "category", "family", "policyarea"];
    private static readonly string[] PolicyNameTokens = ["settingname", "name", "setting", "policyname", "policy", "displayname"];
    private static readonly string[] PolicyUriTokens = ["omauri", "uri", "cspuri", "path", "policypath", "settingpath"];
    private static readonly string[] PolicyValueTokens = ["currentvalue", "value", "data", "configuredvalue", "effectivevalue"];
    private static readonly string[] PolicyStatusTokens = ["status", "state", "resultstatus"];
    private static readonly string[] PolicyResultTokens = ["resultcode", "errorcode", "hresult", "result"];
    private static readonly object AdmxCatalogLock = new();
    private static readonly Dictionary<string, AdmxPolicyCatalog> AdmxCatalogCache = new(StringComparer.OrdinalIgnoreCase);

    [GeneratedRegex(@"0x[0-9A-Fa-f]{8}", RegexOptions.Compiled)]
    private static partial Regex HexRegex();
    [GeneratedRegex(@"(?<id>[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})", RegexOptions.Compiled)]
    private static partial Regex GuidRegex();
    [GeneratedRegex(@"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$", RegexOptions.Compiled)]
    private static partial Regex GuidStrictRegex();
    [GeneratedRegex(@"^(?<id>[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})(?:_.*)?$", RegexOptions.Compiled)]
    private static partial Regex GuidWithOptionalSuffixRegex();
    [GeneratedRegex(@"(?i)(?:app(?:lication)?|win32\s*app)(?:\s+with)?\s*(?:id)?[^0-9a-fA-F]{0,20}(?<id>[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})", RegexOptions.Compiled)]
    private static partial Regex AppIdHintRegex();
    [GeneratedRegex(@"(?i)(?:display\s*name|app\s*name|application\s*name|name)\s*[:=]\s*[""']?(?<name>[^;,\r\n]{2,200})", RegexOptions.Compiled)]
    private static partial Regex NameHintRegex();
    [GeneratedRegex(@"Get policies = (?<json>\[\{.*\}\])", RegexOptions.Compiled | RegexOptions.Singleline)]
    private static partial Regex PolicyPayloadRegex();
    [GeneratedRegex(@"(?i)""(?:displayname|name|appname|title|applicationname)""\s*:\s*""(?<name>[^""\\]{2,200})""", RegexOptions.Compiled)]
    private static partial Regex JsonNameRegex();
    [GeneratedRegex(@"^<!\[LOG\[(?<msg>.*)\]LOG\]!><(?<meta>.+)>$", RegexOptions.Compiled | RegexOptions.Singleline)]
    private static partial Regex CmTraceLineRegex();
    [GeneratedRegex(@"(?<key>[A-Za-z]+)=""(?<value>[^""]*)""", RegexOptions.Compiled)]
    private static partial Regex CmTraceAttrRegex();
    [GeneratedRegex(@"0x[0-9A-Fa-f]{8}|(?<![A-Fa-f0-9])-?\d{3,}(?![A-Fa-f0-9])", RegexOptions.Compiled)]
    private static partial Regex ResultCodeRegex();
    [GeneratedRegex(@"(?i)""(?:errorcode|exitcode)""\s*:\s*(?:""(?<hex>0x[0-9A-Fa-f]+)""|(?<num>-?\d+))", RegexOptions.Compiled)]
    private static partial Regex JsonErrorCodeRegex();
    [GeneratedRegex(@"(?i)\bsession\s*id\b[^0-9a-fA-F-]*(?<id>[0-9a-fA-F-]{8,})", RegexOptions.Compiled)]
    private static partial Regex SessionIdRegex();
    [GeneratedRegex(@"(?i)\bpolicy\s*id\b[^0-9a-fA-F-]*(?<id>[0-9a-fA-F-]{8,})", RegexOptions.Compiled)]
    private static partial Regex PolicyIdRegex();
    [GeneratedRegex(@"(?i)\buser\s*id\b[^0-9a-fA-F-]*(?<id>[0-9a-fA-F-]{8,})", RegexOptions.Compiled)]
    private static partial Regex UserIdRegex();
    [GeneratedRegex(@"(?i)\b(?:error\s*code|hresult)\b[^0-9A-Fa-f-]*(?<id>0x[0-9A-Fa-f]+|-?\d+)", RegexOptions.Compiled)]
    private static partial Regex ErrorCodeHintRegex();
    [GeneratedRegex(@"Request(ing)? (required|available|selected|mock) apps|Started app sync now|Got result with session id|Got .* Win32App\(s\)", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex TimelinePolicySyncRegex();
    [GeneratedRegex(@"download|downloaded|hash validation|unzip|decrypt|content", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex TimelineDownloadRegex();
    [GeneratedRegex(@"detect|detection|detected as installed|not detected|detected version", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex TimelineDetectionRegex();
    [GeneratedRegex(@"requirement|applicability|applicable|requirementsnotmet", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex TimelineRequirementsRegex();
    [GeneratedRegex(@"dependenc|dependent app|child app|parent app", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex TimelineDependenciesRegex();
    [GeneratedRegex(@"install command|installation|installer|msi|exit code|return code", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex TimelineInstallationRegex();
    [GeneratedRegex(@"install|uninstall|enforc|execut|^\[Win32App\]\[ApplicabilityActionHandler\]", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex TimelineExecutionRegex();
    [GeneratedRegex(@"Sending status to company portal based on report|save .* app results|StatusServiceReport|sent successfully", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex TimelineReportingRegex();
    [GeneratedRegex(@"^\[(ServiceBase|Proxy Poller|Discovery Service|Location Service|StatusService)\]|on.?demand|check.?in", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex TimelineStatusServiceRegex();
    [GeneratedRegex(@"<[^>]+>", RegexOptions.Compiled)]
    private static partial Regex HtmlTagRegex();
    [GeneratedRegex(@"(?is)<tr[^>]*>(?<row>.*?)</tr>", RegexOptions.Compiled)]
    private static partial Regex HtmlRowRegex();
    [GeneratedRegex(@"(?is)<t[dh][^>]*>(?<cell>.*?)</t[dh]>", RegexOptions.Compiled)]
    private static partial Regex HtmlCellRegex();
    [GeneratedRegex(@"(?i)\./(?:Device|User)/Vendor/MSFT/[A-Za-z0-9_\-./]+", RegexOptions.Compiled)]
    private static partial Regex OmaUriHintRegex();

    private sealed record AdmxPolicyDefinition(
        string PolicyName,
        string KeyPath,
        IReadOnlyList<string> DisplayNames);

    private sealed record AdmxPolicyCatalog(
        IReadOnlyDictionary<string, AdmxPolicyDefinition> ByPolicyName,
        IReadOnlyDictionary<string, IReadOnlyList<AdmxPolicyDefinition>> ByDisplayName);

    public async ValueTask<LocalIntuneActionResult> MdmSyncNowAsync(string host, CancellationToken cancellationToken)
    {
        const string script =
            "[Windows.Management.MdmSessionManager,Windows.Management,ContentType=WindowsRuntime] | Out-Null;" +
            "$session=[Windows.Management.MdmSessionManager]::TryCreateSession();" +
            "if ($null -eq $session) { throw 'No MDM session available.' };" +
            "$session.StartAsync() | Out-Null;" +
            "Write-Output 'MDM sync started via WinRT session.';";

        return await ExecuteSimpleActionAsync(host, script, cancellationToken, "A01_MDM_SYNC_NOW");
    }

    public async ValueTask<IReadOnlyList<MdmSyncStatusEntry>> GetMdmSyncStatusAsync(string host, int maxEvents, CancellationToken cancellationToken)
    {
        var clamped = Math.Clamp(maxEvents, 1, 500);
        var script =
            "$entries = @(Get-WinEvent -FilterHashtable @{ LogName = '" + MdmAdminLogName + "'; Id = 208, 209 } -MaxEvents " + clamped + " -ErrorAction Stop | " +
            "Select-Object @{Name='TimeCreated';Expression={ if ($_.TimeCreated) { $_.TimeCreated.ToString('o') } else { $null } }}, @{Name='EventId';Expression={$_.Id}}, @{Name='Message';Expression={$_.Message}});" +
            "$entries | ConvertTo-Json -Depth 4 -Compress;";

        var execution = await executor.ExecuteForHostAsync(host, script, cancellationToken);
        if (execution.ExitCode != 0)
        {
            return [new MdmSyncStatusEntry(DateTimeOffset.UtcNow, 0, NormalizeError(execution), string.Empty)];
        }

        if (string.IsNullOrWhiteSpace(execution.StdOut))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(execution.StdOut);
            var items = new List<MdmStatusPayload>();
            if (document.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var element in document.RootElement.EnumerateArray())
                {
                    var payload = element.Deserialize<MdmStatusPayload>(JsonOptions);
                    if (payload is not null)
                    {
                        items.Add(payload);
                    }
                }
            }
            else if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                var payload = document.RootElement.Deserialize<MdmStatusPayload>(JsonOptions);
                if (payload is not null)
                {
                    items.Add(payload);
                }
            }

            return items.Select(item => new MdmSyncStatusEntry(
                    ParseTimestamp(item.TimeCreated),
                    item.EventId,
                    item.Message ?? string.Empty,
                    ResolveHex(item.Message)))
                .ToArray();
        }
        catch (JsonException ex)
        {
            return [new MdmSyncStatusEntry(DateTimeOffset.UtcNow, 0, $"Unable to parse MDM sync status payload: {ex.Message}", string.Empty)];
        }
    }

    public async ValueTask<IReadOnlyList<ImeLogTimelineEntry>> GetImeLogTimelineAsync(
        string host,
        string logDirectory,
        string filePattern,
        int maxLines,
        CancellationToken cancellationToken)
    {
        var snapshot = await GetImeLogTimelineSnapshotAsync(host, logDirectory, filePattern, maxLines, cancellationToken);
        return snapshot.Entries;
    }

    public async ValueTask<ImeLogTimelineSnapshot> GetImeLogTimelineSnapshotAsync(
        string host,
        string logDirectory,
        string filePattern,
        int maxLines,
        CancellationToken cancellationToken)
    {
        var clamped = Math.Clamp(maxLines, 50, 2000);
        var fastTimelineResult = await Task.Run(
            () => TryGetImeLogTimelineSnapshotFast(host, logDirectory, filePattern, clamped, cancellationToken),
            cancellationToken);
        if (fastTimelineResult.Success)
        {
            return new ImeLogTimelineSnapshot(fastTimelineResult.Fingerprint, fastTimelineResult.Entries);
        }

        var safeDir = logDirectory.Replace("'", "''", StringComparison.Ordinal);
        var safePattern = string.IsNullOrWhiteSpace(filePattern)
            ? "AppWorkload*.log"
            : filePattern.Replace("'", "''", StringComparison.Ordinal);

        var script =
            "$logDir='" + safeDir + "';" +
            "$pattern='" + safePattern + "';" +
            "$maxLines=" + clamped + ";" +
            "if (-not (Test-Path -LiteralPath $logDir)) { throw ('IME log directory not found: ' + $logDir) };" +
            "$files=Get-ChildItem -LiteralPath $logDir -Filter $pattern -File -ErrorAction SilentlyContinue | Sort-Object LastWriteTimeUtc -Descending;" +
            "$fingerprintParts=@();" +
            "foreach ($file in ($files | Select-Object -First 8)) {" +
            "  $fingerprintParts += ($file.Name + '|' + $file.LastWriteTimeUtc.Ticks + '|' + $file.Length)" +
            "};" +
            "$entries=New-Object System.Collections.Generic.List[object];" +
            "$lineRx='^<!\\[LOG\\[(?<msg>.*)\\]LOG\\]!><(?<meta>.+)>$';" +
            "$attrRx='(?<key>[A-Za-z]+)=\"(?<value>[^\"]*)\"';" +
            "foreach ($file in $files) {" +
            "  $lines=Get-Content -LiteralPath $file.FullName -ErrorAction SilentlyContinue;" +
            "  if ($null -eq $lines) { continue };" +
            "  $start=[Math]::Max(0, $lines.Count - $maxLines);" +
            "  for ($i=$start; $i -lt $lines.Count; $i++) {" +
            "    $raw=[string]$lines[$i];" +
            "    if ([string]::IsNullOrWhiteSpace($raw)) { continue };" +
            "    $message=$raw; $component=''; $severity='Information'; $timestamp=''; $policyJson='';" +
            "    $lineMatch=[regex]::Match($raw,$lineRx);" +
            "    if ($lineMatch.Success) {" +
            "      $message=$lineMatch.Groups['msg'].Value;" +
            "      $meta=$lineMatch.Groups['meta'].Value;" +
            "      $type='1'; $datePart=''; $timePart='';" +
            "      foreach ($attr in [regex]::Matches($meta,$attrRx)) {" +
            "        $key=$attr.Groups['key'].Value.ToLowerInvariant();" +
            "        $value=$attr.Groups['value'].Value;" +
            "        switch ($key) {" +
            "          'component' { $component=$value; continue }" +
            "          'type' { $type=$value; continue }" +
            "          'date' { $datePart=$value; continue }" +
            "          'time' { $timePart=$value; continue }" +
            "        }" +
            "      }" +
            "      switch ($type) {" +
            "        '3' { $severity='Error'; break }" +
            "        '2' { $severity='Warning'; break }" +
            "        '4' { $severity='Verbose'; break }" +
            "        '0' { $severity='Verbose'; break }" +
            "        default { $severity='Information'; break }" +
            "      }" +
            "      if (-not [string]::IsNullOrWhiteSpace($datePart) -and -not [string]::IsNullOrWhiteSpace($timePart)) {" +
            "        $candidate=($datePart + ' ' + $timePart);" +
            "        try { $timestamp=([datetimeoffset]$candidate).ToString('o') } catch { $timestamp=$candidate }" +
            "      }" +
            "      $pol=[regex]::Match($message,'Get policies = (?<json>\\[\\{.*\\}\\])');" +
            "      if ($pol.Success) { $policyJson=$pol.Groups['json'].Value }" +
            "    } else {" +
            "      $pol=[regex]::Match($raw,'Get policies = (?<json>\\[\\{.*\\}\\])');" +
            "      if ($pol.Success) { $policyJson=$pol.Groups['json'].Value }" +
            "    }" +
            "    $entries.Add([pscustomobject]@{" +
            "      TimeCreated=$timestamp;" +
            "      Severity=$severity;" +
            "      Component=$component;" +
            "      Message=$message;" +
            "      SourceFile=$file.Name;" +
            "      LineNumber=($i + 1);" +
            "      RawLine=$raw;" +
            "      IsPolicyPayload=(-not [string]::IsNullOrWhiteSpace($policyJson));" +
            "      PolicyJson=$policyJson;" +
            "      FileLastWriteUtc=$file.LastWriteTimeUtc.ToString('o')" +
            "    });" +
            "  }" +
            "};" +
            "[pscustomobject]@{" +
            "  Fingerprint=[string]::Join(';',$fingerprintParts);" +
            "  Entries=($entries | Sort-Object @{Expression='FileLastWriteUtc';Descending=$true}, @{Expression='LineNumber';Descending=$true} | Select-Object -First $maxLines)" +
            "} | ConvertTo-Json -Depth 6 -Compress;";

        var execution = await executor.ExecuteForHostAsync(host, script, cancellationToken);
        if (execution.ExitCode != 0)
        {
            throw new InvalidOperationException(NormalizeError(execution));
        }

        if (string.IsNullOrWhiteSpace(execution.StdOut))
        {
            return new ImeLogTimelineSnapshot(string.Empty, []);
        }

        try
        {
            using var document = JsonDocument.Parse(execution.StdOut);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Array)
            {
                var legacyPayloads = new List<ImeTimelinePayload>();
                foreach (var element in root.EnumerateArray())
                {
                    var payload = element.Deserialize<ImeTimelinePayload>(JsonOptions);
                    if (payload is not null)
                    {
                        legacyPayloads.Add(payload);
                    }
                }

                return new ImeLogTimelineSnapshot(string.Empty, BuildImeTimelineEntries(legacyPayloads));
            }

            var fingerprint = root.TryGetProperty("Fingerprint", out var fingerprintElement)
                ? fingerprintElement.GetString() ?? string.Empty
                : string.Empty;
            var payloads = new List<ImeTimelinePayload>();
            if (root.TryGetProperty("Entries", out var entriesElement))
            {
                if (entriesElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var element in entriesElement.EnumerateArray())
                    {
                        var payload = element.Deserialize<ImeTimelinePayload>(JsonOptions);
                        if (payload is not null)
                        {
                            payloads.Add(payload);
                        }
                    }
                }
                else if (entriesElement.ValueKind == JsonValueKind.Object)
                {
                    var payload = entriesElement.Deserialize<ImeTimelinePayload>(JsonOptions);
                    if (payload is not null)
                    {
                        payloads.Add(payload);
                    }
                }
            }

            return new ImeLogTimelineSnapshot(fingerprint, BuildImeTimelineEntries(payloads));
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Unable to parse IME log timeline payload: {ex.Message}", ex);
        }
    }

    public async ValueTask<string> GetImeLogTimelineFingerprintAsync(
        string host,
        string logDirectory,
        string filePattern,
        CancellationToken cancellationToken)
    {
        var fastFingerprint = await Task.Run(
            () => TryGetImeLogTimelineFingerprintFast(host, logDirectory, filePattern),
            cancellationToken);
        if (fastFingerprint is not null)
        {
            return fastFingerprint;
        }

        var safeDir = logDirectory.Replace("'", "''", StringComparison.Ordinal);
        var safePattern = string.IsNullOrWhiteSpace(filePattern)
            ? "AppWorkload*.log"
            : filePattern.Replace("'", "''", StringComparison.Ordinal);

        var script =
            "$logDir='" + safeDir + "';" +
            "$pattern='" + safePattern + "';" +
            "if (-not (Test-Path -LiteralPath $logDir)) { '' ; return };" +
            "$files=Get-ChildItem -LiteralPath $logDir -Filter $pattern -File -ErrorAction SilentlyContinue | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 8;" +
            "$parts=@();" +
            "foreach ($file in $files) {" +
            "  $parts += ($file.Name + '|' + $file.LastWriteTimeUtc.Ticks + '|' + $file.Length)" +
            "};" +
            "[string]::Join(';',$parts);";

        var execution = await executor.ExecuteForHostAsync(host, script, cancellationToken);
        if (execution.ExitCode != 0)
        {
            throw new InvalidOperationException(NormalizeError(execution));
        }

        return execution.StdOut?.Trim() ?? string.Empty;
    }

    public async ValueTask<ImeLogAnalysisResult> GetImeLogAnalysisAsync(
        string host,
        string logDirectory,
        string filePattern,
        int maxLines,
        CancellationToken cancellationToken)
    {
        var clampedTimelineLines = Math.Clamp(maxLines, 50, 2000);
        var fastAnalysis = await Task.Run(
            () => TryGetImeLogAnalysisFast(host, logDirectory, filePattern, clampedTimelineLines, cancellationToken),
            cancellationToken);
        if (fastAnalysis.Success)
        {
            return new ImeLogAnalysisResult(
                fastAnalysis.Fingerprint,
                fastAnalysis.TimelineEntries,
                fastAnalysis.ApplicationStatuses);
        }

        var timelineTask = GetImeLogTimelineSnapshotAsync(host, logDirectory, filePattern, maxLines, cancellationToken).AsTask();
        var applicationsTask = GetImeApplicationStatusesAsync(host, logDirectory, maxLines, cancellationToken).AsTask();
        await Task.WhenAll(timelineTask, applicationsTask);

        var snapshot = await timelineTask;
        return new ImeLogAnalysisResult(
            snapshot.Fingerprint,
            snapshot.Entries,
            await applicationsTask);
    }

    public async ValueTask<IReadOnlyList<ImeApplicationStatusEntry>> GetImeApplicationStatusesAsync(
        string host,
        string logDirectory,
        int maxLines,
        CancellationToken cancellationToken)
    {
        var clamped = Math.Clamp(maxLines, 100, 4000);
        var safeDir = logDirectory.Replace("'", "''", StringComparison.Ordinal);

        var fastResult = await Task.Run(
            () => TryGetImeApplicationStatusesFast(host, logDirectory, clamped, cancellationToken),
            cancellationToken);
        var runPowerShellFallback = IsLocalHost(host) && ShouldRunPowerShellFallback(fastResult.Entries);
        if (fastResult.Success && !runPowerShellFallback)
        {
            return fastResult.Entries;
        }

        var scriptTemplate = """
$logDir='__LOG_DIR__'
$maxLines=__MAX_LINES__
if (-not (Test-Path -LiteralPath $logDir)) { throw ('IME log directory not found: ' + $logDir) }

$lineRx='^<!\[LOG\[(?<msg>.*)\]LOG\]!><(?<meta>.+)>$'
$attrRx='(?<key>[A-Za-z]+)="(?<value>[^"]*)"'
$policyRx='Get policies = (?<json>\[\{.*\}\])'
$guidRx='(?<id>[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})'
$guidStrictRx='^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$'
$appIdHintRx='(?i)(?:app(?:lication)?\s*(?:id)?|win32\s*app)[^0-9a-fA-F]{0,20}(?<id>[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})'
$nameHintRx='(?i)(?:display\s*name|app\s*name|application\s*name|name)\s*[:=]\s*["'']?(?<name>[^;,\r\n]{2,200})'
$jsonNameRx='(?i)"(?:displayname|name|appname|title|applicationname)"\s*:\s*"(?<name>[^"\\]{2,200})"'
$resultRx='0x[0-9A-Fa-f]{8}|(?<![A-Fa-f0-9])-?\d{3,}(?![A-Fa-f0-9])'

$policyNameMap=@{}
$policyTimeMap=@{}
$policyIntentMap=@{}
$policyTargetContextMap=@{}
$statusMap=@{}
$registryStateMap=@{}
$knownAppIds = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
$knownWin32AppIds = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)

function Add-KnownAppId([string]$rawId, [string]$name, [string]$timestamp) {
  if ([string]::IsNullOrWhiteSpace($rawId)) { return '' }
  $id = $rawId.Trim('{}').ToLowerInvariant()
  if ([string]::IsNullOrWhiteSpace($id)) { return '' }
  $null = $knownAppIds.Add($id)
  if (-not [string]::IsNullOrWhiteSpace($name)) {
    if (-not $policyNameMap.ContainsKey($id) -or [string]::IsNullOrWhiteSpace([string]$policyNameMap[$id]) -or [string]::Equals([string]$policyNameMap[$id], $id, [System.StringComparison]::OrdinalIgnoreCase)) {
      $policyNameMap[$id] = $name
    }

  }
  if (-not [string]::IsNullOrWhiteSpace($timestamp)) {
    $policyTimeMap[$id] = $timestamp
  }
  return $id
}

function Add-KnownWin32AppId([string]$rawId, [string]$name, [string]$timestamp) {
  $id = Add-KnownAppId $rawId $name $timestamp
  if (-not [string]::IsNullOrWhiteSpace($id)) { $null = $knownWin32AppIds.Add($id) }
  return $id
}

function Parse-Line([string]$raw) {
  $message = $raw
  $timestamp = ''
  $lineMatch = [regex]::Match($raw, $lineRx)
  if ($lineMatch.Success) {
    $message = $lineMatch.Groups['msg'].Value
    $meta = $lineMatch.Groups['meta'].Value
    $datePart=''; $timePart=''
    foreach ($attr in [regex]::Matches($meta, $attrRx)) {
      $key=$attr.Groups['key'].Value.ToLowerInvariant()
      $value=$attr.Groups['value'].Value
      switch ($key) { 'date' { $datePart=$value; continue } 'time' { $timePart=$value; continue } }
    }
    if (-not [string]::IsNullOrWhiteSpace($datePart) -and -not [string]::IsNullOrWhiteSpace($timePart)) {
      $candidate=($datePart + ' ' + $timePart)
      try { $timestamp=([datetimeoffset]$candidate).ToString('o') } catch { $timestamp=$candidate }
    }
  }
  return [pscustomobject]@{ Message=$message; Timestamp=$timestamp }
}

$workloadFiles = Get-ChildItem -LiteralPath $logDir -Filter 'AppWorkload*.log' -File -ErrorAction SilentlyContinue | Sort-Object LastWriteTimeUtc -Descending
foreach ($file in $workloadFiles) {
  $lines=Get-Content -LiteralPath $file.FullName -ErrorAction SilentlyContinue
  if ($null -eq $lines) { continue }
  $start=[Math]::Max(0, $lines.Count - $maxLines)
  for ($i=$start; $i -lt $lines.Count; $i++) {
    $raw=[string]$lines[$i]
    if ([string]::IsNullOrWhiteSpace($raw)) { continue }
    $parsed = Parse-Line $raw
    $message = [string]$parsed.Message
    $timestamp = [string]$parsed.Timestamp

    $policy=[regex]::Match($message,$policyRx)
    if (-not $policy.Success) { continue }
    $policyJson=$policy.Groups['json'].Value

    try {
      $items = @(ConvertFrom-Json $policyJson -Depth 60)
      foreach ($item in $items) {
        if ($null -eq $item) { continue }
        $appId=''
        foreach ($idKey in @('Id','ID','AppId','AppID','ApplicationId','Win32AppId')) {
          if ($item.PSObject.Properties.Name -contains $idKey) {
            $appId=[string]$item.$idKey
            if (-not [string]::IsNullOrWhiteSpace($appId)) { break }
          }
        }
        if ([string]::IsNullOrWhiteSpace($appId)) { continue }
        $name=''
        foreach ($nameKey in @('Name','DisplayName','AppName','Title','ApplicationName')) {
          if ($item.PSObject.Properties.Name -contains $nameKey) {
            $name=[string]$item.$nameKey
            if (-not [string]::IsNullOrWhiteSpace($name)) { break }
          }
        }
        $id = Add-KnownAppId $appId $name $timestamp
        if (-not [string]::IsNullOrWhiteSpace($id)) {
          foreach ($intentKey in @('Intent','InstallIntent','AssignmentIntent')) {
            if ($item.PSObject.Properties.Name -contains $intentKey) {
              $value=[string]$item.$intentKey
              if (-not [string]::IsNullOrWhiteSpace($value)) { $policyIntentMap[$id]=$value; break }
            }
          }
          foreach ($targetKey in @('TargetType','TargetingType')) {
            if ($item.PSObject.Properties.Name -contains $targetKey) {
              $value=[string]$item.$targetKey
              if (-not [string]::IsNullOrWhiteSpace($value)) { $policyTargetContextMap[$id]=$value; break }
            }
          }
        }
      }
    } catch { }

    foreach ($idHit in [regex]::Matches($policyJson,$guidRx)) {
      $idCandidate=$idHit.Groups['id'].Value
      if ([string]::IsNullOrWhiteSpace($idCandidate)) { continue }
      $startIdx=[Math]::Max(0, $idHit.Index - 350)
      $sliceLen=[Math]::Min(1400, $policyJson.Length - $startIdx)
      if ($sliceLen -le 0) { continue }
      $slice=$policyJson.Substring($startIdx, $sliceLen)
      $nameHit=[regex]::Match($slice,$jsonNameRx)
      $candidateName=if ($nameHit.Success) { $nameHit.Groups['name'].Value.Trim() } else { '' }
      $null = Add-KnownAppId $idCandidate $candidateName $timestamp
    }
  }
}

$win32Root='HKLM:\SOFTWARE\Microsoft\IntuneManagementExtension\Win32Apps'
if (Test-Path -LiteralPath $win32Root) {
  foreach ($identity in (Get-ChildItem -LiteralPath $win32Root -ErrorAction SilentlyContinue)) {
    if ($identity.PSChildName -eq 'OperationalState') { continue }
    foreach ($child in (Get-ChildItem -LiteralPath $identity.PSPath -ErrorAction SilentlyContinue)) {
      if ($child.PSChildName -eq 'GRS') {
        foreach ($grs in (Get-ChildItem -LiteralPath $child.PSPath -ErrorAction SilentlyContinue)) {
          foreach ($hit in [regex]::Matches($grs.PSChildName, $guidRx)) {
            $id = Add-KnownWin32AppId $hit.Groups['id'].Value '' ''
            if ([string]::IsNullOrWhiteSpace($id)) { continue }
            if (-not $registryStateMap.ContainsKey($id)) {
              $registryStateMap[$id]=[ordered]@{ HasAppKey=$false; HasGrs=$false; IdentityIds=(New-Object System.Collections.Generic.List[string]) }
            }
            $registryStateMap[$id].HasGrs = $true
            if (-not $registryStateMap[$id].IdentityIds.Contains($identity.PSChildName)) { $registryStateMap[$id].IdentityIds.Add($identity.PSChildName) }
          }
        }
        continue
      }

      if ([regex]::IsMatch($child.PSChildName, $guidStrictRx)) {
        $id = Add-KnownWin32AppId $child.PSChildName '' ''
        if ([string]::IsNullOrWhiteSpace($id)) { continue }
        if (-not $registryStateMap.ContainsKey($id)) {
          $registryStateMap[$id]=[ordered]@{ HasAppKey=$false; HasGrs=$false; IdentityIds=(New-Object System.Collections.Generic.List[string]) }
        }
        $registryStateMap[$id].HasAppKey = $true
        if (-not $registryStateMap[$id].IdentityIds.Contains($identity.PSChildName)) { $registryStateMap[$id].IdentityIds.Add($identity.PSChildName) }
      }
    }
  }
}

$statusFiles=@()
$statusFiles += Get-ChildItem -LiteralPath $logDir -Filter 'AppWorkload*.log' -File -ErrorAction SilentlyContinue
$statusFiles += Get-ChildItem -LiteralPath $logDir -Filter 'IntuneManagementExtension*.log' -File -ErrorAction SilentlyContinue
$statusFiles = $statusFiles | Sort-Object LastWriteTimeUtc -Descending

foreach ($file in $statusFiles) {
  $lines=Get-Content -LiteralPath $file.FullName -ErrorAction SilentlyContinue
  if ($null -eq $lines) { continue }
  $start=[Math]::Max(0, $lines.Count - $maxLines)
  for ($i=$start; $i -lt $lines.Count; $i++) {
    $raw=[string]$lines[$i]
    if ([string]::IsNullOrWhiteSpace($raw)) { continue }
    $parsed = Parse-Line $raw
    $message = [string]$parsed.Message
    $timestamp = [string]$parsed.Timestamp

    $id=''
    $idMatch=[regex]::Match($message,$appIdHintRx)
    if ($idMatch.Success) {
      $candidateId = $idMatch.Groups['id'].Value.Trim('{}').ToLowerInvariant()
      if ($knownWin32AppIds.Contains($candidateId)) {
        $id = Add-KnownAppId $candidateId '' ''
      }
    } else {
      $fallback=[regex]::Match($message,$guidRx)
      if ($fallback.Success) {
        $candidateId = $fallback.Groups['id'].Value.Trim('{}').ToLowerInvariant()
        if ($knownWin32AppIds.Contains($candidateId)) {
          $id = $candidateId
        }
      }
    }
    if ([string]::IsNullOrWhiteSpace($id)) { continue }

    $msgLower=$message.ToLowerInvariant()
    $status='Unknown'
    if ($msgLower -match 'failed|error|0x8|exit code|return code|timeout|timed out') { $status='Failed' }
    elseif ($msgLower -match 'installed|succeeded|completed successfully|detected as installed') { $status='Installed' }
    elseif ($msgLower -match 'not installed|not detected') { $status='NotInstalled' }
    elseif ($msgLower -match 'installing|downloading|enforcing|processing|queued|retry') { $status='InProgress' }
    else { continue }

    $result=''; $resultMatch=[regex]::Match($message,$resultRx); if ($resultMatch.Success) { $result=$resultMatch.Value }
    $name=''; $nameMatch=[regex]::Match($message,$nameHintRx)
    if ($nameMatch.Success) { $name=$nameMatch.Groups['name'].Value.Trim() }
    if ([string]::IsNullOrWhiteSpace($name) -and $policyNameMap.ContainsKey($id)) { $name=[string]$policyNameMap[$id] }
    if ([string]::IsNullOrWhiteSpace($name)) { $name=$id }

    $registryHint=''
    if ($registryStateMap.ContainsKey($id)) {
      $reg = $registryStateMap[$id]
      if ($reg.HasGrs) { $registryHint='GRS entry present' }
      elseif ($reg.HasAppKey) { $registryHint='Registry app key present' }
    }
    $finalMessage = $message
    if (-not [string]::IsNullOrWhiteSpace($registryHint) -and -not ($finalMessage -like ("*" + $registryHint + "*"))) {
      $finalMessage = $finalMessage + ' | ' + $registryHint
    }

    $intent = if ($policyIntentMap.ContainsKey($id)) { [string]$policyIntentMap[$id] } else { '' }
    $targetContext = if ($policyTargetContextMap.ContainsKey($id)) { [string]$policyTargetContextMap[$id] } else { '' }
    $candidate=[pscustomobject]@{ AppId=$id; AppName=$name; Intent=$intent; TargetInstallContext=$targetContext; InstallStatus=$status; LastUpdated=$timestamp; ResultCode=$result; SourceFile=$file.Name; LastMessage=$finalMessage; IsInstalledForAnyIdentity=$false; IdentityStatuses=@() }
    if (-not $statusMap.ContainsKey($id)) { $statusMap[$id]=$candidate; continue }
    $existing=$statusMap[$id]; $currentTs=[datetimeoffset]::MinValue; $newTs=[datetimeoffset]::MinValue
    try { $currentTs=[datetimeoffset][string]$existing.LastUpdated } catch { }
    try { $newTs=[datetimeoffset][string]$candidate.LastUpdated } catch { }
    if ($newTs -ge $currentTs) { $statusMap[$id]=$candidate }
  }
}

foreach ($appId in $knownWin32AppIds) {
  if ($statusMap.ContainsKey($appId)) { continue }
  $name = if ($policyNameMap.ContainsKey($appId)) { [string]$policyNameMap[$appId] } else { $appId }
  $status='Unknown'; $source='AppWorkload policy payload'; $message='App available in policy payload, no matching status line found in scanned logs.'
  if ($registryStateMap.ContainsKey($appId)) {
    $reg = $registryStateMap[$appId]
    if ($reg.HasGrs) {
      $status='RetryPending'
      $source='Registry GRS'
      $message='Registry indicates pending reevaluation (GRS entry present).'
    } elseif ($reg.HasAppKey) {
      $status='Detected'
      $source='Registry Win32Apps'
      $message='AppId found in Win32Apps registry state.'
    }
  }
  $statusMap[$appId]=[pscustomobject]@{
    AppId=$appId
    AppName=$name
    Intent=(if ($policyIntentMap.ContainsKey($appId)) { [string]$policyIntentMap[$appId] } else { '' })
    TargetInstallContext=(if ($policyTargetContextMap.ContainsKey($appId)) { [string]$policyTargetContextMap[$appId] } else { '' })
    InstallStatus=$status
    LastUpdated=(if ($policyTimeMap.ContainsKey($appId)) { [string]$policyTimeMap[$appId] } else { '' })
    ResultCode=''
    SourceFile=$source
    LastMessage=$message
    IsInstalledForAnyIdentity=$false
    IdentityStatuses=@()
  }
}

$statusMap.Values | Sort-Object AppName, AppId | ConvertTo-Json -Depth 6 -Compress
""";
        var script = scriptTemplate
            .Replace("__LOG_DIR__", safeDir, StringComparison.Ordinal)
            .Replace("__MAX_LINES__", clamped.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);

        var execution = await executor.ExecuteForHostAsync(host, script, cancellationToken);
        if (execution.ExitCode != 0)
        {
            if (fastResult.Success)
            {
                return fastResult.Entries;
            }

            throw new InvalidOperationException(NormalizeError(execution));
        }

        if (string.IsNullOrWhiteSpace(execution.StdOut))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(execution.StdOut);
            var payloads = new List<ImeApplicationStatusPayload>();
            if (document.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var element in document.RootElement.EnumerateArray())
                {
                    var payload = element.Deserialize<ImeApplicationStatusPayload>(JsonOptions);
                    if (payload is not null)
                    {
                        payloads.Add(payload);
                    }
                }
            }
            else if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                var payload = document.RootElement.Deserialize<ImeApplicationStatusPayload>(JsonOptions);
                if (payload is not null)
                {
                    payloads.Add(payload);
                }
            }

            return payloads
                .Select(payload =>
                {
                    var identityStatuses = (payload.IdentityStatuses ?? [])
                        .Select(identity => new ImeApplicationIdentityStatusEntry(
                            identity.IdentityId ?? string.Empty,
                            string.IsNullOrWhiteSpace(identity.Scope) ? "User" : identity.Scope!,
                            string.IsNullOrWhiteSpace(identity.InstallStatus) ? "Unknown" : identity.InstallStatus!,
                            ParseTimestampFlexible(identity.LastUpdated),
                            identity.ResultCode ?? string.Empty,
                            identity.Source ?? string.Empty,
                            identity.Details ?? string.Empty))
                        .Where(identity => !string.IsNullOrWhiteSpace(identity.IdentityId))
                        .ToArray();

                    return new ImeApplicationStatusEntry(
                        payload.AppId ?? string.Empty,
                        payload.AppName ?? payload.AppId ?? string.Empty,
                        string.IsNullOrWhiteSpace(payload.Intent) ? "Unknown" : NormalizePolicyIntent(payload.Intent),
                        string.IsNullOrWhiteSpace(payload.TargetInstallContext) ? "Unknown" : NormalizeTargetType(payload.TargetInstallContext),
                        string.IsNullOrWhiteSpace(payload.InstallStatus) ? "Unknown" : payload.InstallStatus!,
                        ParseTimestampFlexible(payload.LastUpdated),
                        payload.ResultCode ?? string.Empty,
                        payload.SourceFile ?? string.Empty,
                        payload.LastMessage ?? string.Empty,
                        payload.IsInstalledForAnyIdentity ?? identityStatuses.Any(entry => IsInstalledStatus(entry.InstallStatus)),
                        identityStatuses);
                })
                .Where(entry => !string.IsNullOrWhiteSpace(entry.AppId))
                .ToArray();
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Unable to parse IME application status payload: {ex.Message}", ex);
        }
    }

    private static bool ShouldRunPowerShellFallback(IReadOnlyList<ImeApplicationStatusEntry> entries)
    {
        if (entries.Count == 0)
        {
            return true;
        }

        if (entries.Any(entry => IsTerminalStatus(entry.InstallStatus)))
        {
            return false;
        }

        // Only trigger corrective fallback for clearly unresolved states.
        return entries.Any(entry =>
            string.Equals(entry.InstallStatus, "InProgress", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(entry.InstallStatus, "Unknown", StringComparison.OrdinalIgnoreCase));
    }

    public async ValueTask<MdmReportParseResult> GenerateMdmDiagnosticsReportAsync(string host, string outputDirectory, CancellationToken cancellationToken)
    {
        var script = BuildGenerateMdmDiagnosticsReportScript(host, outputDirectory);
        var executionHost = string.IsNullOrWhiteSpace(host) || !LocalPowerShellExecutor.IsLocalHost(host)
            ? "localhost"
            : host;

        var execution = await executor.ExecuteForHostAsync(executionHost, script, cancellationToken);
        if (execution.ExitCode != 0)
        {
            throw new InvalidOperationException(NormalizeError(execution));
        }

        var payload = JsonSerializer.Deserialize<MdmReportPayload>(execution.StdOut, JsonOptions)
                      ?? throw new InvalidOperationException("MDM diagnostics report generation returned no payload.");
        return await ParseMdmDiagnosticsReportAsync(host, payload.ReportDirectory ?? outputDirectory, cancellationToken);
    }

    public async ValueTask<MdmReportParseResult> ParseMdmDiagnosticsReportAsync(string host, string reportDirectory, CancellationToken cancellationToken)
    {
        var safeDir = reportDirectory.Replace("'", "''", StringComparison.Ordinal);
        var script =
            "$dir='" + safeDir + "';" +
            "$xml=[System.IO.Path]::Combine($dir, 'MDMDiagReport.xml');" +
            "$html1=[System.IO.Path]::Combine($dir, 'MDMDiagReport.html');" +
            "$html2=[System.IO.Path]::Combine($dir, 'MDMDiagHTMLReport.html');" +
            "$htmlCandidates=@($html1,$html2) | Where-Object { Test-Path -LiteralPath $_ };" +
            "$html=if ($htmlCandidates.Count -eq 0) { '' } else { $htmlCandidates | Sort-Object { (Get-Item -LiteralPath $_ -ErrorAction SilentlyContinue).LastWriteTimeUtc } -Descending | Select-Object -First 1 };" +
            "$xmlPath=if (Test-Path -LiteralPath $xml) { $xml } else { '' };" +
            "$xmlNodeCount=0; if (-not [string]::IsNullOrWhiteSpace($xmlPath)) { try { [xml]$doc=Get-Content -LiteralPath $xmlPath -ErrorAction Stop; $xmlNodeCount=$doc.SelectNodes('//*').Count } catch { $xmlNodeCount=0 } };" +
            "$htmlLineCount=if ([string]::IsNullOrWhiteSpace($html)) { 0 } else { (Get-Content -LiteralPath $html -ErrorAction SilentlyContinue | Measure-Object -Line).Lines };" +
            "$result=[ordered]@{ ReportDirectory=$dir; XmlPath=$xmlPath; HtmlPath=$html; XmlNodeCount=$xmlNodeCount; HtmlLineCount=$htmlLineCount };" +
            "$result | ConvertTo-Json -Depth 4 -Compress;";

        var executionHost = Directory.Exists(reportDirectory) ? "localhost" : host;
        var execution = await executor.ExecuteForHostAsync(executionHost, script, cancellationToken);
        if (execution.ExitCode != 0)
        {
            throw new InvalidOperationException(NormalizeError(execution));
        }

        var payload = JsonSerializer.Deserialize<MdmReportParsePayload>(execution.StdOut, JsonOptions)
                      ?? throw new InvalidOperationException("MDM report parse returned no payload.");

        return new MdmReportParseResult(
            payload.ReportDirectory ?? reportDirectory,
            payload.XmlPath ?? string.Empty,
            payload.HtmlPath ?? string.Empty,
            payload.XmlNodeCount,
            payload.HtmlLineCount);
    }

    private static string BuildGenerateMdmDiagnosticsReportScript(string host, string outputDirectory)
    {
        var safeOut = outputDirectory.Replace("'", "''", StringComparison.Ordinal);
        var safeHost = (host ?? string.Empty).Replace("'", "''", StringComparison.Ordinal);
        return $$"""
$outDir='{{safeOut}}';
$targetHost='{{safeHost}}';
$normalizedHost=if ([string]::IsNullOrWhiteSpace($targetHost)) { $env:COMPUTERNAME } else { $targetHost.Trim() };
$isLocalTarget=$normalizedHost -eq '.' -or $normalizedHost -eq 'localhost' -or $normalizedHost -eq $env:COMPUTERNAME;
$xml=[System.IO.Path]::Combine($outDir, 'MDMDiagReport.xml');
$html1=[System.IO.Path]::Combine($outDir, 'MDMDiagReport.html');
$html2=[System.IO.Path]::Combine($outDir, 'MDMDiagHTMLReport.html');
$gph=[System.IO.Path]::Combine($outDir, 'gpresult.html');
$gpx=[System.IO.Path]::Combine($outDir, 'gpresult.xml');
$session=$null;
$remoteReportDir='';
$remoteGpPath='';
$remoteGpXmlPath='';

function Invoke-IccMdmExport {
    param([string]$DestinationDirectory)
    New-Item -ItemType Directory -Path $DestinationDirectory -Force | Out-Null;
    $candidates=@(
        [System.IO.Path]::Combine($env:SystemRoot, 'System32\MdmDiagnosticsTool.exe'),
        [System.IO.Path]::Combine($env:SystemRoot, 'System32\mdmdiagnosticstool.exe'));
    $tool=$candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1;
    if (-not $tool) { throw 'MDM diagnostics tool not found in System32.' }
    & $tool -out $DestinationDirectory | Out-Null;
}

function Resolve-IccMdmHtmlPath {
    param([string]$Directory)
    $candidates=@(
        ([System.IO.Path]::Combine($Directory, 'MDMDiagReport.html')),
        ([System.IO.Path]::Combine($Directory, 'MDMDiagHTMLReport.html'))
    ) | Where-Object { Test-Path -LiteralPath $_ };
    if ($candidates.Count -eq 0) { return '' }
    return $candidates |
        Sort-Object { (Get-Item -LiteralPath $_ -ErrorAction SilentlyContinue).LastWriteTimeUtc } -Descending |
        Select-Object -First 1;
}

function Remove-IccMdmReportFiles {
    param([string]$Directory)
    foreach ($path in @(
        ([System.IO.Path]::Combine($Directory, 'MDMDiagReport.xml')),
        ([System.IO.Path]::Combine($Directory, 'MDMDiagReport.html')),
        ([System.IO.Path]::Combine($Directory, 'MDMDiagHTMLReport.html'))
    )) {
        if (Test-Path -LiteralPath $path) {
            Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue;
        }
    }
}

function Test-IccFileReady {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { return $false }
    try { return (Get-Item -LiteralPath $Path -ErrorAction Stop).Length -gt 0 } catch { return $false }
}

function Invoke-IccGpResultPair {
    param(
        [string]$HtmlPath,
        [string]$XmlPath,
        [string]$ComputerName = ''
    )

    function Invoke-IccGpResult {
        param(
            [string]$DestinationPath,
            [ValidateSet('Html', 'Xml')]
            [string]$Format
        )

        if ([string]::IsNullOrWhiteSpace($DestinationPath)) {
            return $false
        }

        try {
            $arguments = @()
            if (-not [string]::IsNullOrWhiteSpace($ComputerName)) {
                $arguments += @('/S', $ComputerName)
            }

            $arguments += @('/Scope', 'Computer')
            if ($Format -eq 'Html') {
                $arguments += @('/H', $DestinationPath, '/F')
            }
            else {
                $arguments += @('/X', $DestinationPath, '/F')
            }

            & gpresult.exe @arguments | Out-Null
        }
        catch { }

        return Test-IccFileReady -Path $DestinationPath
    }

    # Background PowerShell jobs require a separate pwsh.exe, which is not
    # available to the embedded host. Run both native calls in-process.
    $htmlReady = Invoke-IccGpResult -DestinationPath $HtmlPath -Format 'Html'
    $xmlReady = Invoke-IccGpResult -DestinationPath $XmlPath -Format 'Xml'

    return [ordered]@{
        HtmlReady = $htmlReady
        XmlReady = $xmlReady
    }
}

function Invoke-IccRemoteGpResultFallback {
    param(
        [System.Management.Automation.Runspaces.PSSession]$Session,
        [string]$DestinationPath
    )

    $remotePath = Invoke-Command -Session $Session -ErrorAction Stop -ScriptBlock {
        $path=Join-Path $env:TEMP ('icc-gpresult-' + [guid]::NewGuid().ToString('N') + '.html');
        if (Test-Path -LiteralPath $path) {
            Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue;
        }
        & gpresult.exe /Scope Computer /H $path /F | Out-Null;
        if ((Test-Path -LiteralPath $path) -and ((Get-Item -LiteralPath $path -ErrorAction SilentlyContinue).Length -gt 0)) { return $path }
        return ''
    };

    if ([string]::IsNullOrWhiteSpace($remotePath)) {
        return '';
    }

    Copy-Item -FromSession $Session -LiteralPath $remotePath -Destination $DestinationPath -Force;
    return $remotePath;
}

function Invoke-IccRemoteGpResultXmlFallback {
    param(
        [System.Management.Automation.Runspaces.PSSession]$Session,
        [string]$DestinationPath
    )

    $remotePath = Invoke-Command -Session $Session -ErrorAction Stop -ScriptBlock {
        $path=Join-Path $env:TEMP ('icc-gpresult-' + [guid]::NewGuid().ToString('N') + '.xml');
        if (Test-Path -LiteralPath $path) {
            Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue;
        }
        & gpresult.exe /Scope Computer /X $path /F | Out-Null;
        if ((Test-Path -LiteralPath $path) -and ((Get-Item -LiteralPath $path -ErrorAction SilentlyContinue).Length -gt 0)) { return $path }
        return ''
    };

    if ([string]::IsNullOrWhiteSpace($remotePath)) {
        return '';
    }

    Copy-Item -FromSession $Session -LiteralPath $remotePath -Destination $DestinationPath -Force;
    return $remotePath;
}

New-Item -ItemType Directory -Path $outDir -Force | Out-Null;
Remove-IccMdmReportFiles -Directory $outDir;
if (Test-Path -LiteralPath $gph) {
    Remove-Item -LiteralPath $gph -Force -ErrorAction SilentlyContinue;
}
if (Test-Path -LiteralPath $gpx) {
    Remove-Item -LiteralPath $gpx -Force -ErrorAction SilentlyContinue;
}

try {
    if ($isLocalTarget) {
        Invoke-IccMdmExport -DestinationDirectory $outDir;
        $gpResultStatus = Invoke-IccGpResultPair -HtmlPath $gph -XmlPath $gpx;
    }
    else {
        $session=New-PSSession -ComputerName $normalizedHost -ErrorAction Stop;
        $remoteReport=Invoke-Command -Session $session -ErrorAction Stop -ScriptBlock {
            $dir=[System.IO.Path]::Combine($env:TEMP, 'WindowsClientCenter\policy-result\' + [guid]::NewGuid().ToString('N'));
            New-Item -ItemType Directory -Path $dir -Force | Out-Null;
            $candidates=@(
                [System.IO.Path]::Combine($env:SystemRoot, 'System32\MdmDiagnosticsTool.exe'),
                [System.IO.Path]::Combine($env:SystemRoot, 'System32\mdmdiagnosticstool.exe'));
            $tool=$candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1;
            if (-not $tool) { throw 'MDM diagnostics tool not found in System32.' }
            & $tool -out $dir | Out-Null;
            $xmlPath=[System.IO.Path]::Combine($dir, 'MDMDiagReport.xml');
            $htmlPath='';
            foreach ($candidate in @(
                [System.IO.Path]::Combine($dir, 'MDMDiagReport.html'),
                [System.IO.Path]::Combine($dir, 'MDMDiagHTMLReport.html'))) {
                if (Test-Path -LiteralPath $candidate) {
                    $htmlPath=$candidate;
                    break;
                }
            }
            return [ordered]@{
                ReportDirectory = $dir
                XmlPath = if (Test-Path -LiteralPath $xmlPath) { $xmlPath } else { '' }
                HtmlPath = $htmlPath
            };
        };
        $remoteReportDir=if ($null -ne $remoteReport) { [string]$remoteReport.ReportDirectory } else { '' };

        $remoteXmlPath=if ($null -ne $remoteReport) { [string]$remoteReport.XmlPath } else { '' };
        if (-not [string]::IsNullOrWhiteSpace($remoteXmlPath)) {
            Copy-Item -FromSession $session -LiteralPath $remoteXmlPath -Destination $xml -Force;
        }

        $remoteHtmlPath=if ($null -ne $remoteReport) { [string]$remoteReport.HtmlPath } else { '' };
        if (-not [string]::IsNullOrWhiteSpace($remoteHtmlPath)) {
            $localHtmlPath=[System.IO.Path]::Combine($outDir, [System.IO.Path]::GetFileName($remoteHtmlPath));
            Copy-Item -FromSession $session -LiteralPath $remoteHtmlPath -Destination $localHtmlPath -Force;
        }

        $gpResultStatus = Invoke-IccGpResultPair -HtmlPath $gph -XmlPath $gpx -ComputerName $normalizedHost;
        if (-not $gpResultStatus.HtmlReady) {
            $remoteGpPath=Invoke-IccRemoteGpResultFallback -Session $session -DestinationPath $gph;
        }
        if (-not $gpResultStatus.XmlReady) {
            $remoteGpXmlPath=Invoke-IccRemoteGpResultXmlFallback -Session $session -DestinationPath $gpx;
        }
    }
}
finally {
    if ($session) {
        try {
            Invoke-Command -Session $session -ArgumentList $remoteReportDir, $remoteGpPath, $remoteGpXmlPath -ErrorAction SilentlyContinue -ScriptBlock {
                param($reportDir, $gpPath, $gpXmlPath)
                if (-not [string]::IsNullOrWhiteSpace($gpPath) -and (Test-Path -LiteralPath $gpPath)) {
                    Remove-Item -LiteralPath $gpPath -Force -ErrorAction SilentlyContinue;
                }
                if (-not [string]::IsNullOrWhiteSpace($gpXmlPath) -and (Test-Path -LiteralPath $gpXmlPath)) {
                    Remove-Item -LiteralPath $gpXmlPath -Force -ErrorAction SilentlyContinue;
                }
                if (-not [string]::IsNullOrWhiteSpace($reportDir) -and (Test-Path -LiteralPath $reportDir)) {
                    Remove-Item -LiteralPath $reportDir -Recurse -Force -ErrorAction SilentlyContinue;
                }
            } | Out-Null;
        }
        catch { }

        Remove-PSSession -Session $session -ErrorAction SilentlyContinue;
    }
}

$html=Resolve-IccMdmHtmlPath -Directory $outDir;
$result=[ordered]@{
    ReportDirectory=$outDir;
    XmlPath=if (Test-Path -LiteralPath $xml) { $xml } else { '' };
    HtmlPath=$html
};
$result | ConvertTo-Json -Depth 4 -Compress;
""";
    }

    public async ValueTask<IntunePolicyResultReport> GenerateIntunePolicyResultAsync(string host, string outputDirectory, CancellationToken cancellationToken)
    {
        var report = await GenerateMdmDiagnosticsReportAsync(host, outputDirectory, cancellationToken);
        return await BuildIntunePolicyResultReportAsync(host, report, outputDirectory, cancellationToken);
    }

    public async ValueTask<IntunePolicyResultReport> ParseIntunePolicyResultAsync(string host, string reportDirectory, string outputDirectory, CancellationToken cancellationToken)
    {
        var report = await ParseMdmDiagnosticsReportAsync(host, reportDirectory, cancellationToken);
        return await BuildIntunePolicyResultReportAsync(host, report, outputDirectory, cancellationToken);
    }

    public async ValueTask<LocalIntuneActionResult> ImeSyncAppsAsync(string host, CancellationToken cancellationToken)
    {
        const string script =
            "(New-Object -ComObject Shell.Application).Open('intunemanagementextension://syncapp');" +
            "Write-Output 'IME syncapp signal sent.';";
        return await ExecuteSimpleActionAsync(host, script, cancellationToken, "A05_IME_SYNC_APP");
    }

    public async ValueTask<LocalIntuneActionResult> ImeSyncComplianceAsync(string host, CancellationToken cancellationToken)
    {
        const string script =
            "(New-Object -ComObject Shell.Application).Open('intunemanagementextension://synccompliance');" +
            "Write-Output 'IME synccompliance signal sent.';";
        return await ExecuteSimpleActionAsync(host, script, cancellationToken, "A06_IME_SYNC_COMPLIANCE");
    }

    public async ValueTask<LocalIntuneActionResult> ParseImeAppWorkloadPoliciesAsync(string host, string logDirectory, CancellationToken cancellationToken)
    {
        var safeDir = logDirectory.Replace("'", "''", StringComparison.Ordinal);
        var script =
            "$logDir='" + safeDir + "';" +
            "if (-not (Test-Path -LiteralPath $logDir)) { throw ('IME log directory not found: ' + $logDir) };" +
            "$rx='<!\\[LOG\\[Get policies = (?<json>\\[\\{.*?\\}\\])\\]LOG\\]!>';" +
            "$latest=''; $sourceFile=''; $sourceLine=0;" +
            "Get-ChildItem -LiteralPath $logDir -Filter 'AppWorkload*.log' -ErrorAction SilentlyContinue | Sort-Object LastWriteTimeUtc -Descending | ForEach-Object {" +
            "  $lines=Get-Content -LiteralPath $_.FullName -ErrorAction SilentlyContinue;" +
            "  if ($null -eq $lines) { continue };" +
            "  for ($idx=$lines.Count-1; $idx -ge 0; $idx--) {" +
            "    $m=[regex]::Match([string]$lines[$idx],$rx);" +
            "    if ($m.Success) {" +
            "      $latest=$m.Groups['json'].Value;" +
            "      $sourceFile=$_.Name;" +
            "      $sourceLine=($idx + 1);" +
            "      break;" +
            "    }" +
            "  }" +
            "  if (-not [string]::IsNullOrWhiteSpace($latest)) { break }" +
            "};" +
            "if ([string]::IsNullOrWhiteSpace($latest)) { throw 'No AppWorkload policy payload found.' };" +
            "$result=[ordered]@{ Message='Latest AppWorkload policy payload extracted from AppWorkload logs.'; PolicyJson=$latest; SourceFile=$sourceFile; SourceLine=$sourceLine };" +
            "$result | ConvertTo-Json -Depth 4 -Compress;";

        var execution = await executor.ExecuteForHostAsync(host, script, cancellationToken);
        if (execution.ExitCode != 0)
        {
            return Failed("A07_IME_PARSE_APPWORKLOAD", NormalizeError(execution));
        }

        var payload = JsonSerializer.Deserialize<PolicyParsePayload>(execution.StdOut, JsonOptions);
        return new LocalIntuneActionResult(
            true,
            payload?.Message ?? "AppWorkload policy payload extracted from local logs.",
            [],
            new Dictionary<string, string>
            {
                ["policyJson"] = payload?.PolicyJson ?? string.Empty,
                ["sourceFile"] = payload?.SourceFile ?? string.Empty,
                ["sourceLine"] = payload is null ? "0" : payload.SourceLine.ToString(CultureInfo.InvariantCulture)
            });
    }

    public async ValueTask<LocalIntuneActionResult> RunImeHealthEvaluationAsync(string host, string taskNameContains, CancellationToken cancellationToken)
    {
        var contains = string.IsNullOrWhiteSpace(taskNameContains) ? "Health Evaluation" : taskNameContains.Trim();
        var safeContains = contains.Replace("'", "''", StringComparison.Ordinal);
        var script =
            "$needle='" + safeContains + "';" +
            "$task=Get-ScheduledTask -ErrorAction SilentlyContinue | Where-Object { $_.TaskName -like ('*' + $needle + '*') -or $_.TaskPath -like '*Intune*' } | Select-Object -First 1;" +
            "if ($null -eq $task) { throw ('IME health evaluation task not found using filter: ' + $needle) };" +
            "Start-ScheduledTask -TaskName $task.TaskName -TaskPath $task.TaskPath;" +
            "$log='C:\\ProgramData\\Microsoft\\IntuneManagementExtension\\Logs\\ClientHealth.log';" +
            "$tail=if (Test-Path -LiteralPath $log) { (Get-Content -LiteralPath $log -Tail 80 -ErrorAction SilentlyContinue) -join [Environment]::NewLine } else { '' };" +
            "$result=[ordered]@{ Message=('Started task ' + $task.TaskPath + $task.TaskName); ClientHealthTail=$tail };" +
            "$result | ConvertTo-Json -Depth 4 -Compress;";

        var execution = await executor.ExecuteForHostAsync(host, script, cancellationToken);
        if (execution.ExitCode != 0)
        {
            return Failed("A08_IME_HEALTH_EVAL", NormalizeError(execution));
        }

        var payload = JsonSerializer.Deserialize<HealthEvalPayload>(execution.StdOut, JsonOptions);
        return new LocalIntuneActionResult(
            true,
            payload?.Message ?? "IME health evaluation started.",
            [],
            new Dictionary<string, string> { ["clientHealthTail"] = payload?.ClientHealthTail ?? string.Empty });
    }

    public async ValueTask<LocalIntuneActionResult> RestartImeServiceAsync(string host, CancellationToken cancellationToken)
    {
        const string script =
            "$svc=Get-Service -Name 'IntuneManagementExtension' -ErrorAction SilentlyContinue;" +
            "if ($null -eq $svc) { throw 'Service IntuneManagementExtension not found.' };" +
            "Restart-Service -Name 'IntuneManagementExtension' -Force -ErrorAction Stop;" +
            "Start-Sleep -Seconds 2;" +
            "$svc=Get-Service -Name 'IntuneManagementExtension' -ErrorAction SilentlyContinue;" +
            "$result=[ordered]@{ Message=('IME service restarted. Current status: ' + $svc.Status); Status=[string]$svc.Status };" +
            "$result | ConvertTo-Json -Depth 4 -Compress;";

        var execution = await executor.ExecuteForHostAsync(host, script, cancellationToken);
        if (execution.ExitCode != 0)
        {
            return Failed("A08B_IME_RESTART", NormalizeError(execution));
        }

        var payload = JsonSerializer.Deserialize<ImeRestartPayload>(execution.StdOut, JsonOptions);
        return new LocalIntuneActionResult(
            true,
            payload?.Message ?? "IME service restart completed.",
            [],
            new Dictionary<string, string> { ["serviceStatus"] = payload?.Status ?? string.Empty });
    }

    public async ValueTask<bool> GetImeTestModeEnabledAsync(string host, CancellationToken cancellationToken)
    {
        const string script =
            "$path='HKLM:\\Software\\Microsoft\\IntuneManagementExtension\\Settings';" +
            "$name='TestMode';" +
            "$raw=$null;" +
            "if (Test-Path -LiteralPath $path) { $raw=Get-ItemPropertyValue -LiteralPath $path -Name $name -ErrorAction SilentlyContinue };" +
            "$parsed=$false;" +
            "if (-not [string]::IsNullOrWhiteSpace([string]$raw)) { [void][bool]::TryParse([string]$raw, [ref]$parsed) };" +
            "[string]$parsed;";

        var execution = await executor.ExecuteForHostAsync(host, script, cancellationToken);
        if (execution.ExitCode != 0)
        {
            return false;
        }

        return bool.TryParse(execution.StdOut.Trim(), out var enabled) && enabled;
    }

    public async ValueTask<LocalIntuneActionResult> SetImeTestModeEnabledAsync(string host, bool enabled, CancellationToken cancellationToken)
    {
        var enableFlag = enabled ? "$true" : "$false";
        var script =
            "$path='HKLM:\\Software\\Microsoft\\IntuneManagementExtension\\Settings';" +
            "$name='TestMode';" +
            "$enable=" + enableFlag + ";" +
            "if ($enable) {" +
            "  New-Item -Path $path -Force | Out-Null;" +
            "  Set-ItemProperty -LiteralPath $path -Name $name -Value 'true' -Type String -Force;" +
            "  $message='IME TestMode enabled (TestMode=true).';" +
            "} else {" +
            "  if (Test-Path -LiteralPath $path) { Remove-ItemProperty -LiteralPath $path -Name $name -ErrorAction SilentlyContinue };" +
            "  $message='IME TestMode disabled (TestMode value removed).';" +
            "}" +
            "$raw=$null;" +
            "if (Test-Path -LiteralPath $path) { $raw=Get-ItemPropertyValue -LiteralPath $path -Name $name -ErrorAction SilentlyContinue };" +
            "$parsed=$false;" +
            "if (-not [string]::IsNullOrWhiteSpace([string]$raw)) { [void][bool]::TryParse([string]$raw, [ref]$parsed) };" +
            "$result=[ordered]@{ Message=$message; IsEnabled=$parsed; RawValue=([string]$raw) };" +
            "$result | ConvertTo-Json -Depth 4 -Compress;";

        var execution = await executor.ExecuteForHostAsync(host, script, cancellationToken);
        if (execution.ExitCode != 0)
        {
            return Failed("A08C_IME_TESTMODE", NormalizeError(execution));
        }

        var payload = JsonSerializer.Deserialize<ImeTestModePayload>(execution.StdOut, JsonOptions);
        return new LocalIntuneActionResult(
            true,
            payload?.Message ?? "IME TestMode updated.",
            [],
            new Dictionary<string, string>
            {
                ["isEnabled"] = payload?.IsEnabled.ToString(CultureInfo.InvariantCulture) ?? bool.FalseString,
                ["rawValue"] = payload?.RawValue ?? string.Empty
            });
    }

    public async ValueTask<LocalIntuneActionResult> RetryWin32AppAsync(string host, Win32RetryRequest request, CancellationToken cancellationToken)
    {
        var safeIdentity = request.IdentityId.Replace("'", "''", StringComparison.Ordinal);
        var safeBackup = request.BackupDirectory.Replace("'", "''", StringComparison.Ordinal);
        var appId = request.AppId.ToString("D");
        var whatIf = request.WhatIf ? "$true" : "$false";

        var script =
            "$identity='" + safeIdentity + "';" +
            "$appId='" + appId + "';" +
            "$backupDir='" + safeBackup + "';" +
            "$whatIf=" + whatIf + ";" +
            "New-Item -ItemType Directory -Path $backupDir -Force | Out-Null;" +
            "$base='HKLM:\\SOFTWARE\\Microsoft\\IntuneManagementExtension\\Win32Apps\\' + $identity;" +
            "$app=Join-Path $base $appId;" +
            "if (-not (Test-Path -LiteralPath $app)) { throw ('App key not found: ' + $app) };" +
            "$regExport=Join-Path $backupDir ('Win32Apps_' + $identity + '.reg');" +
            "reg.exe export ('HKLM\\SOFTWARE\\Microsoft\\IntuneManagementExtension\\Win32Apps\\' + $identity) $regExport /y | Out-Null;" +
            "if (-not $whatIf) {" +
            "  Remove-Item -LiteralPath $app -Recurse -Force -ErrorAction Stop;" +
            "  $grs=Join-Path $base 'GRS';" +
            "  if (Test-Path -LiteralPath $grs) {" +
            "    Get-ChildItem -LiteralPath $grs -ErrorAction SilentlyContinue | Where-Object { $_.PSChildName -like ('*' + $appId + '*') } | ForEach-Object { Remove-Item -LiteralPath $_.PSPath -Recurse -Force -ErrorAction SilentlyContinue }" +
            "  }" +
            "}" +
            "$msg=if ($whatIf) { 'Preview complete. No registry keys were deleted.' } else { 'Win32 retry state deleted.' };" +
            "$result=[ordered]@{ Message=$msg; BackupPath=$regExport };" +
            "$result | ConvertTo-Json -Depth 4 -Compress;";

        var execution = await executor.ExecuteForHostAsync(host, script, cancellationToken);
        if (execution.ExitCode != 0)
        {
            return Failed("A09_WIN32_RETRY_SINGLE", NormalizeError(execution));
        }

        var payload = JsonSerializer.Deserialize<RetryPayload>(execution.StdOut, JsonOptions);
        return new LocalIntuneActionResult(
            true,
            payload?.Message ?? "Win32 retry action finished.",
            [],
            new Dictionary<string, string> { ["backupReg"] = payload?.BackupPath ?? string.Empty });
    }

    public async ValueTask<LocalIntuneActionResult> RetryAllFailedWin32AppsAsync(string host, Win32RetryAllRequest request, CancellationToken cancellationToken)
    {
        var maxApps = Math.Clamp(request.MaxAppsPerRun, 1, 500);
        var cooldown = Math.Clamp(request.CooldownSeconds, 0, 120);
        var safeBackupRoot = request.BackupRoot.Replace("'", "''", StringComparison.Ordinal);
        var whatIf = request.WhatIf ? "$true" : "$false";
        var removeGrs = request.RemoveGrsEntriesForFailedApps ? "$true" : "$false";
        var restartIme = request.RestartImeService ? "$true" : "$false";

        var script =
            "$maxApps=" + maxApps + ";" +
            "$cooldown=" + cooldown + ";" +
            "$backupRoot='" + safeBackupRoot + "';" +
            "$whatIf=" + whatIf + ";" +
            "$removeGrs=" + removeGrs + ";" +
            "$restartIme=" + restartIme + ";" +
            "New-Item -ItemType Directory -Path $backupRoot -Force | Out-Null;" +
            "$root='HKLM:\\SOFTWARE\\Microsoft\\IntuneManagementExtension\\Win32Apps';" +
            "if (-not (Test-Path -LiteralPath $root)) { throw 'Win32Apps registry root not found.' };" +
            "$guidRx='(?i)^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$';" +
            "$failedRx='(?i)(?:fail(?:ed|ure)?|error|exception|retry|0x87d|0x800)';" +
            "$skipRx='(?i)0x0+\\b|success|succeeded|compliant|installed';" +
            "$deleted=0;" +
            "$backups=New-Object System.Collections.Generic.List[string];" +
            "foreach ($identity in (Get-ChildItem -LiteralPath $root -ErrorAction SilentlyContinue)) {" +
            "  if ($deleted -ge $maxApps) { break }" +
            "  if ($identity.PSChildName -eq 'OperationalState') { continue }" +
            "  $base=$identity.PSPath;" +
            "  $backup=Join-Path $backupRoot ('Win32Apps_' + $identity.PSChildName + '.reg');" +
            "  reg.exe export ('HKLM\\SOFTWARE\\Microsoft\\IntuneManagementExtension\\Win32Apps\\' + $identity.PSChildName) $backup /y | Out-Null;" +
            "  $backups.Add($backup);" +
            "  foreach ($app in (Get-ChildItem -LiteralPath $base -ErrorAction SilentlyContinue)) {" +
            "    if ($deleted -ge $maxApps) { break }" +
            "    if ($app.PSChildName -eq 'GRS') { continue }" +
            "    if ($app.PSChildName -notmatch $guidRx) { continue }" +
            "    $isFailed=$false;" +
            "    foreach ($name in @('ComplianceStateMessage','EnforcementStateMessage','EnforcementState','LastError','ErrorCode')) {" +
            "      $raw=(Get-ItemPropertyValue -LiteralPath $app.PSPath -Name $name -ErrorAction SilentlyContinue);" +
            "      if ($null -eq $raw) { continue }" +
            "      $text=[string]$raw;" +
            "      if ($text -match $failedRx -and $text -notmatch $skipRx) { $isFailed=$true; break }" +
            "    }" +
            "    if (-not $isFailed) { continue }" +
            "    if (-not $whatIf) {" +
            "      Remove-Item -LiteralPath $app.PSPath -Recurse -Force -ErrorAction SilentlyContinue;" +
            "      if ($removeGrs) {" +
            "        $grs=Join-Path $base 'GRS';" +
            "        if (Test-Path -LiteralPath $grs) {" +
            "          Get-ChildItem -LiteralPath $grs -ErrorAction SilentlyContinue | Where-Object { $_.PSChildName -like ('*' + $app.PSChildName + '*') } | ForEach-Object { Remove-Item -LiteralPath $_.PSPath -Recurse -Force -ErrorAction SilentlyContinue }" +
            "        }" +
            "      }" +
            "    }" +
            "    $deleted++;" +
            "    if ($cooldown -gt 0) { Start-Sleep -Seconds $cooldown }" +
            "  }" +
            "}" +
            "$restartMessage='IME service restart not requested.';" +
            "if ($restartIme) {" +
            "  if ($whatIf) { $restartMessage='Preview complete. IME service restart skipped (WhatIf).' }" +
            "  else {" +
            "    $svc=Get-Service -Name 'IntuneManagementExtension' -ErrorAction SilentlyContinue;" +
            "    if ($null -eq $svc) { $restartMessage='Service IntuneManagementExtension not found.' }" +
            "    else { Restart-Service -Name 'IntuneManagementExtension' -Force -ErrorAction Stop; Start-Sleep -Seconds 2; $svc=Get-Service -Name 'IntuneManagementExtension' -ErrorAction SilentlyContinue; $restartMessage=('IME service restarted. Current status: ' + $svc.Status) }" +
            "  }" +
            "}" +
            "$msg=if ($whatIf) { 'Preview complete for failed-app batch retry.' } else { 'Failed-app batch retry registry cleanup done.' };" +
            "$result=[ordered]@{ Message=($msg + ' Affected apps: ' + $deleted); BackupRoot=$backupRoot; RestartMessage=$restartMessage };" +
            "$result | ConvertTo-Json -Depth 4 -Compress;";

        var execution = await executor.ExecuteForHostAsync(host, script, cancellationToken);
        if (execution.ExitCode != 0)
        {
            return Failed("A10_WIN32_RETRY_ALL_FAILED", NormalizeError(execution));
        }

        var payload = JsonSerializer.Deserialize<RetryAllPayload>(execution.StdOut, JsonOptions);
        return new LocalIntuneActionResult(
            true,
            payload?.Message ?? "Batch Win32 retry action finished.",
            [],
            new Dictionary<string, string>
            {
                ["backupRoot"] = payload?.BackupRoot ?? request.BackupRoot,
                ["imeRestart"] = payload?.RestartMessage ?? string.Empty
            });
    }

    public async ValueTask<LocalIntuneActionResult> RestartPortAuthenticationServicesAsync(string host, CancellationToken cancellationToken)
    {
        const string script =
            "Restart-Service -Name 'dot3svc' -Force -ErrorAction Stop;" +
            "Restart-Service -Name 'EapHost' -Force -ErrorAction Stop;" +
            "$services=@(Get-Service -Name 'dot3svc','EapHost' -ErrorAction Stop | Select-Object Name,Status);" +
            "Write-Output ('Restarted port authentication services: ' + (($services | ForEach-Object { $_.Name + '=' + $_.Status }) -join ', '));";

        return await ExecuteSimpleActionAsync(host, script, cancellationToken, "A12_PORTAUTH_RESTART_SERVICES");
    }

    public async ValueTask<LocalIntuneActionResult> RestartPortAuthenticationAdapterAsync(string host, string interfaceName, CancellationToken cancellationToken)
    {
        const string actionId = "A13_PORTAUTH_RESTART_ADAPTER";
        if (string.IsNullOrWhiteSpace(interfaceName))
        {
            return Failed(actionId, "No network interface was provided.");
        }

        var safeInterface = interfaceName.Replace("'", "''", StringComparison.Ordinal);
        var script =
            "$name='" + safeInterface + "';" +
            "Disable-NetAdapter -Name $name -Confirm:$false -ErrorAction Stop | Out-Null;" +
            "Start-Sleep -Seconds 2;" +
            "Enable-NetAdapter -Name $name -Confirm:$false -ErrorAction Stop | Out-Null;" +
            "Write-Output ('Restarted network adapter ' + $name + '.');";

        return await ExecuteSimpleActionAsync(host, script, cancellationToken, actionId);
    }

    public async ValueTask<LocalIntuneActionResult> SetPortAuthenticationTracingAsync(string host, PortAuthenticationTracingMode mode, CancellationToken cancellationToken)
    {
        var modeArgument = mode switch
        {
            PortAuthenticationTracingMode.Disabled => "no",
            PortAuthenticationTracingMode.Enabled => "yes",
            PortAuthenticationTracingMode.Persistent => "persistent",
            _ => "no"
        };

        var script =
            "netsh lan set tracing mode=" + modeArgument + " | Out-Null;" +
            "$tracing=(netsh lan show tracing 2>&1 | Out-String).Trim();" +
            "if ([string]::IsNullOrWhiteSpace($tracing)) { $tracing='Tracing state updated.' };" +
            "Write-Output $tracing;";

        return await ExecuteSimpleActionAsync(host, script, cancellationToken, "A14_PORTAUTH_SET_TRACING");
    }

    public async ValueTask<LocalIntuneActionResult> SetPortAuthenticationAutoconfigAsync(string host, string interfaceName, bool enabled, CancellationToken cancellationToken)
    {
        const string actionId = "A15_PORTAUTH_SET_AUTOCONFIG";
        if (string.IsNullOrWhiteSpace(interfaceName))
        {
            return Failed(actionId, "No network interface was provided.");
        }

        var safeInterface = interfaceName.Replace("'", "''", StringComparison.Ordinal);
        var enabledValue = enabled ? "yes" : "no";
        var script =
            "$name='" + safeInterface + "';" +
            "netsh lan set autoconfig enabled=" + enabledValue + " interface=\"$name\" | Out-Null;" +
            "Write-Output ('Set wired autoconfig to " + enabledValue + " on interface ' + $name + '.');";

        return await ExecuteSimpleActionAsync(host, script, cancellationToken, actionId);
    }

    public async ValueTask<LocalIntuneActionResult> ReapplyPortAuthenticationProfileAsync(string host, string profileName, string? interfaceName, CancellationToken cancellationToken)
    {
        const string actionId = "A16_PORTAUTH_REAPPLY_PROFILE";
        if (string.IsNullOrWhiteSpace(profileName))
        {
            return Failed(actionId, "No wired profile name was provided.");
        }

        var safeProfile = profileName.Replace("'", "''", StringComparison.Ordinal);
        var safeInterface = interfaceName?.Replace("'", "''", StringComparison.Ordinal) ?? string.Empty;
        var interfaceClause = string.IsNullOrWhiteSpace(safeInterface)
            ? string.Empty
            : ";$addArgs += @('interface=\"' + $interfaceName + '\"')";
        var script =
            "$profileName='" + safeProfile + "';" +
            "$interfaceName='" + safeInterface + "';" +
            "$tempFolder=Join-Path ([System.IO.Path]::GetTempPath()) ('ICC-portauth-' + [Guid]::NewGuid().ToString('N'));" +
            "New-Item -ItemType Directory -Path $tempFolder -Force | Out-Null;" +
            "try {" +
            "  netsh lan export profile folder=\"$tempFolder\" | Out-Null;" +
            "  $profileFile=$null;" +
            "  foreach ($candidate in @(Get-ChildItem -Path $tempFolder -Filter '*.xml' -ErrorAction SilentlyContinue)) {" +
            "    try { [xml]$xml=Get-Content -LiteralPath $candidate.FullName -Raw -ErrorAction Stop } catch { continue };" +
            "    $nameNode=$xml.SelectSingleNode('//*[local-name()=\"name\"]');" +
            "    if ($null -ne $nameNode -and [string]::Equals($nameNode.InnerText.Trim(), $profileName, [System.StringComparison]::OrdinalIgnoreCase)) { $profileFile=$candidate.FullName; break }" +
            "  }" +
            "  if ([string]::IsNullOrWhiteSpace($profileFile)) { throw ('Wired profile not found in exported profiles: ' + $profileName) };" +
            "  $addArgs=@('lan','add','profile','filename=\"' + $profileFile + '\"')" +
            interfaceClause +
            ";" +
            "  netsh @addArgs | Out-Null;" +
            "  Write-Output ('Reapplied wired profile ' + $profileName + $(if ([string]::IsNullOrWhiteSpace($interfaceName)) { '.' } else { ' on interface ' + $interfaceName + '.' }));" +
            "} finally { Remove-Item -Path $tempFolder -Recurse -Force -ErrorAction SilentlyContinue; }";

        return await ExecuteSimpleActionAsync(host, script, cancellationToken, actionId);
    }

    public async ValueTask<LocalIntuneActionResult> ExportSupportEventLogsAsync(string host, string outputDirectory, CancellationToken cancellationToken)
    {
        var safeOutput = outputDirectory.Replace("'", "''", StringComparison.Ordinal);
        var script =
            "$out='" + safeOutput + "';" +
            "New-Item -ItemType Directory -Path $out -Force | Out-Null;" +
            "$mdm=Join-Path $out 'MDM_Admin.evtx';" +
            "$udr=Join-Path $out 'UserDeviceRegistration_Admin.evtx';" +
            "$prov=Join-Path $out 'Provisioning_Admin.evtx';" +
            "wevtutil epl '" + MdmAdminLogName + "' $mdm;" +
            "wevtutil epl 'Microsoft-Windows-User Device Registration/Admin' $udr;" +
            "wevtutil epl 'Microsoft-Windows-Provisioning-Diagnostics-Provider/Admin' $prov;" +
            "$result=[ordered]@{ Message='Event logs exported.'; Mdm=$mdm; Udr=$udr; Provisioning=$prov };" +
            "$result | ConvertTo-Json -Depth 4 -Compress;";

        var execution = await executor.ExecuteForHostAsync(host, script, cancellationToken);
        if (execution.ExitCode != 0)
        {
            return Failed("A11_SUPPORT_EXPORT_EVENTLOGS", NormalizeError(execution));
        }

        var payload = JsonSerializer.Deserialize<EventLogExportPayload>(execution.StdOut, JsonOptions);
        return new LocalIntuneActionResult(
            true,
            payload?.Message ?? "Support event logs exported.",
            [],
            new Dictionary<string, string>
            {
                ["mdmEvtx"] = payload?.Mdm ?? string.Empty,
                ["udrEvtx"] = payload?.Udr ?? string.Empty,
                ["provisioningEvtx"] = payload?.Provisioning ?? string.Empty
            });
    }

    public async ValueTask<LocalIntuneActionResult> CreateDiagnosticsBundleAsync(string host, string bundleRoot, string zipPath, CancellationToken cancellationToken)
    {
        var safeBundle = bundleRoot.Replace("'", "''", StringComparison.Ordinal);
        var safeZip = zipPath.Replace("'", "''", StringComparison.Ordinal);
        var script =
            "$bundle='" + safeBundle + "';" +
            "$zip='" + safeZip + "';" +
            "New-Item -ItemType Directory -Path $bundle -Force | Out-Null;" +
            "$imeSrc='C:\\ProgramData\\Microsoft\\IntuneManagementExtension\\Logs';" +
            "$imeDst=Join-Path $bundle 'IME_Logs';" +
            "if (Test-Path -LiteralPath $imeSrc) { Copy-Item -LiteralPath $imeSrc -Destination $imeDst -Recurse -Force -ErrorAction SilentlyContinue };" +
            "$evtxDir=Join-Path $bundle 'EVTX'; New-Item -ItemType Directory -Path $evtxDir -Force | Out-Null;" +
            "$mdmEvtx=Join-Path $evtxDir 'MDM_Admin.evtx'; wevtutil epl '" + MdmAdminLogName + "' $mdmEvtx;" +
            "$mdmDir=Join-Path $bundle 'MDM'; New-Item -ItemType Directory -Path $mdmDir -Force | Out-Null;" +
            "$candidates=@((Join-Path $env:SystemRoot 'System32\\MdmDiagnosticsTool.exe'),(Join-Path $env:SystemRoot 'System32\\mdmdiagnosticstool.exe'));" +
            "$tool=$candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1;" +
            "if ($tool) { & $tool -out $mdmDir | Out-Null };" +
            "if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force };" +
            "Compress-Archive -Path (Join-Path $bundle '*') -DestinationPath $zip -CompressionLevel Fastest;" +
            "$result=[ordered]@{ Message='Diagnostics bundle created.'; ZipPath=$zip; BundleRoot=$bundle };" +
            "$result | ConvertTo-Json -Depth 4 -Compress;";

        var execution = await executor.ExecuteForHostAsync(host, script, cancellationToken);
        if (execution.ExitCode != 0)
        {
            return Failed("A12_SUPPORT_CREATE_BUNDLE", NormalizeError(execution));
        }

        var payload = JsonSerializer.Deserialize<BundlePayload>(execution.StdOut, JsonOptions);
        return new LocalIntuneActionResult(
            true,
            payload?.Message ?? "Diagnostics bundle created.",
            [],
            new Dictionary<string, string>
            {
                ["zipPath"] = payload?.ZipPath ?? zipPath,
                ["bundleRoot"] = payload?.BundleRoot ?? bundleRoot
            });
    }

    public async ValueTask<LocalIntuneActionResult> RunAutopilotDiagnosticsCommunityAsync(
        string host,
        bool allSessions,
        bool showPolicies,
        string moduleVersion,
        int maxOutputLines,
        CancellationToken cancellationToken)
    {
        var normalizedVersion = string.IsNullOrWhiteSpace(moduleVersion) ? "6.3" : moduleVersion.Trim();
        var clampedMaxOutputLines = Math.Clamp(maxOutputLines, 100, 10000);

        var scriptBody = LoadEmbeddedHelperScript("Invoke-AutopilotDiagnosticsCommunity.ps1")
            .Replace("__MODULE_VERSION__", normalizedVersion.Replace("'", "''", StringComparison.Ordinal), StringComparison.Ordinal)
            .Replace("__ALL_SESSIONS__", allSessions ? "$true" : "$false", StringComparison.Ordinal)
            .Replace("__SHOW_POLICIES__", showPolicies ? "$true" : "$false", StringComparison.Ordinal)
            .Replace("__MAX_OUTPUT_LINES__", clampedMaxOutputLines.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);

        var execution = await executor.ExecuteForHostAsync(host, scriptBody, cancellationToken);
        if (execution.ExitCode != 0)
        {
            return Failed("A13_AUTOPILOT_DIAGNOSTICS_COMMUNITY", NormalizeError(execution));
        }

        return BuildHelperScriptResult(
            actionId: "A13_AUTOPILOT_DIAGNOSTICS_COMMUNITY",
            defaultMessage: "Autopilot diagnostics collected with community script.",
            stdOut: execution.StdOut,
            extraEvidence: new Dictionary<string, string>
            {
                ["moduleVersionRequested"] = normalizedVersion
            });
    }

    public async ValueTask<LocalIntuneActionResult> RunImeQuickStatusAsync(string host, int maxOutputLines, CancellationToken cancellationToken)
    {
        var clampedMaxOutputLines = Math.Clamp(maxOutputLines, 50, 5000);
        var scriptBody = LoadEmbeddedHelperScript("Invoke-ImeQuickStatus.ps1")
            .Replace("__MAX_OUTPUT_LINES__", clampedMaxOutputLines.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);

        var execution = await executor.ExecuteForHostAsync(host, scriptBody, cancellationToken);
        if (execution.ExitCode != 0)
        {
            return Failed("A14_IME_QUICK_STATUS", NormalizeError(execution));
        }

        return BuildHelperScriptResult(
            actionId: "A14_IME_QUICK_STATUS",
            defaultMessage: "IME quick status collected.",
            stdOut: execution.StdOut);
    }

    private static FastImeTimelineSnapshotResult TryGetImeLogTimelineSnapshotFast(
        string host,
        string logDirectory,
        string filePattern,
        int maxLines,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            return default;
        }

        var resolvedLogDirectory = ResolveLogDirectoryForHost(host, logDirectory);
        if (string.IsNullOrWhiteSpace(resolvedLogDirectory) || !Directory.Exists(resolvedLogDirectory))
        {
            return default;
        }

        var pattern = string.IsNullOrWhiteSpace(filePattern) ? "AppWorkload*.log" : filePattern.Trim();
        var files = GetLatestFiles(resolvedLogDirectory, pattern, 8);
        if (files.Count == 0)
        {
            return new FastImeTimelineSnapshotResult(true, string.Empty, []);
        }

        var linesPerFile = Math.Clamp(maxLines, 50, 2000);
        IReadOnlyList<TailLogEntry> GetTailEntries(string path) => ReadTailLogEntries(path, linesPerFile, cancellationToken);
        var timeline = BuildFastImeTimelineEntries(files, maxLines, GetTailEntries, cancellationToken);
        return new FastImeTimelineSnapshotResult(true, BuildImeLogFingerprint(files), timeline);
    }

    private static FastImeLogAnalysisResult TryGetImeLogAnalysisFast(
        string host,
        string logDirectory,
        string filePattern,
        int maxLines,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            return default;
        }

        var resolvedLogDirectory = ResolveLogDirectoryForHost(host, logDirectory);
        if (string.IsNullOrWhiteSpace(resolvedLogDirectory) || !Directory.Exists(resolvedLogDirectory))
        {
            return default;
        }

        var pattern = string.IsNullOrWhiteSpace(filePattern) ? "AppWorkload*.log" : filePattern.Trim();
        var files = GetLatestFiles(resolvedLogDirectory, pattern, 8);
        if (files.Count == 0)
        {
            return new FastImeLogAnalysisResult(true, string.Empty, [], []);
        }

        var tailEntryLimit = Math.Max(Math.Clamp(maxLines, 50, 2000), Math.Clamp(maxLines, 100, 4000));
        var tailEntryCache = new Dictionary<string, IReadOnlyList<TailLogEntry>>(StringComparer.OrdinalIgnoreCase);

        IReadOnlyList<TailLogEntry> GetTailEntriesCached(string path)
        {
            if (tailEntryCache.TryGetValue(path, out var cached))
            {
                return cached;
            }

            var loaded = ReadTailLogEntries(path, tailEntryLimit, cancellationToken);
            tailEntryCache[path] = loaded;
            return loaded;
        }

        var timeline = BuildFastImeTimelineEntries(files, maxLines, GetTailEntriesCached, cancellationToken);
        var applications = BuildFastImeApplicationStatuses(host, resolvedLogDirectory, GetTailEntriesCached, cancellationToken);
        return new FastImeLogAnalysisResult(true, BuildImeLogFingerprint(files), timeline, applications);
    }

    private static string? TryGetImeLogTimelineFingerprintFast(string host, string logDirectory, string filePattern)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var resolvedLogDirectory = ResolveLogDirectoryForHost(host, logDirectory);
        if (string.IsNullOrWhiteSpace(resolvedLogDirectory) || !Directory.Exists(resolvedLogDirectory))
        {
            return string.Empty;
        }

        var pattern = string.IsNullOrWhiteSpace(filePattern) ? "AppWorkload*.log" : filePattern.Trim();
        var files = GetLatestFiles(resolvedLogDirectory, pattern, 8);
        if (files.Count == 0)
        {
            return string.Empty;
        }

        return string.Join(
            ';',
            files.Select(path =>
            {
                var info = new FileInfo(path);
                return $"{info.Name}|{info.LastWriteTimeUtc.Ticks}|{info.Length}";
            }));
    }

    private static FastImeAppStatusResult TryGetImeApplicationStatusesFast(
        string host,
        string logDirectory,
        int maxLines,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            return default;
        }

        var resolvedLogDirectory = ResolveLogDirectoryForHost(host, logDirectory);
        if (string.IsNullOrWhiteSpace(resolvedLogDirectory) || !Directory.Exists(resolvedLogDirectory))
        {
            return default;
        }

        var tailEntryCache = new Dictionary<string, IReadOnlyList<TailLogEntry>>(StringComparer.OrdinalIgnoreCase);

        IReadOnlyList<TailLogEntry> GetTailEntriesCached(string path)
        {
            if (tailEntryCache.TryGetValue(path, out var cached))
            {
                return cached;
            }

            var loaded = ReadTailLogEntries(path, maxLines, cancellationToken);
            tailEntryCache[path] = loaded;
            return loaded;
        }

        var entries = BuildFastImeApplicationStatuses(host, resolvedLogDirectory, GetTailEntriesCached, cancellationToken);
        return new FastImeAppStatusResult(true, entries);
    }

    private static IReadOnlyList<ImeLogTimelineEntry> BuildFastImeTimelineEntries(
        IReadOnlyList<string> files,
        int maxLines,
        Func<string, IReadOnlyList<TailLogEntry>> getTailEntries,
        CancellationToken cancellationToken)
    {
        var timelineRows = new List<(DateTimeOffset LastWriteUtc, int LineNumber, ImeLogTimelineEntry Entry)>();
        foreach (var filePath in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var sourceFile = Path.GetFileName(filePath);
            var fileLastWrite = File.GetLastWriteTimeUtc(filePath);
            foreach (var row in getTailEntries(filePath))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(row.RawText))
                {
                    continue;
                }

                var parsed = ParseImeLogLine(row.RawText);
                var policyMatch = PolicyPayloadRegex().Match(parsed.Message);
                var policyJson = policyMatch.Success ? policyMatch.Groups["json"].Value : string.Empty;
                var classification = ClassifyImeTimelineEntry(parsed.Component, parsed.Message, !string.IsNullOrWhiteSpace(policyJson));
                var entry = new ImeLogTimelineEntry(
                    parsed.Timestamp,
                    parsed.Severity,
                    classification.DisplayComponent,
                    parsed.Message,
                    sourceFile,
                    row.LineNumber,
                    row.RawText,
                    !string.IsNullOrWhiteSpace(policyJson),
                    policyJson,
                    classification.Flow,
                    classification.Phase,
                    classification.Effect,
                    classification.CorrelationSummary,
                    classification.EntityType,
                    classification.EntityId,
                    classification.PolicyId,
                    classification.SessionId,
                    classification.UserId,
                    classification.ResultCode);
                timelineRows.Add((fileLastWrite, row.LineNumber, entry));
            }
        }

        return timelineRows
            .OrderByDescending(item => item.LastWriteUtc)
            .ThenByDescending(item => item.LineNumber)
            .Select(item => item.Entry)
            .Take(maxLines)
            .ToArray();
    }

    private static IReadOnlyList<ImeApplicationStatusEntry> BuildFastImeApplicationStatuses(
        string host,
        string resolvedLogDirectory,
        Func<string, IReadOnlyList<TailLogEntry>> getTailEntries,
        CancellationToken cancellationToken)
    {
        var knownAppIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var knownWin32AppIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var policyNameMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var policyTimeMap = new Dictionary<string, DateTimeOffset?>(StringComparer.OrdinalIgnoreCase);
        var policyIntentMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var policyTargetContextMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var registryStateMap = new Dictionary<string, RegistryAppState>(StringComparer.OrdinalIgnoreCase);
        var statusMap = new Dictionary<string, ImeApplicationStatusEntry>(StringComparer.OrdinalIgnoreCase);

        var policyFiles = GetLatestFiles(resolvedLogDirectory, "AppWorkload*.log", 3);
        foreach (var filePath in policyFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var row in getTailEntries(filePath))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var rawLine = row.RawText;
                if (string.IsNullOrWhiteSpace(rawLine))
                {
                    continue;
                }

                var parsed = ParseImeLogLine(rawLine);
                var message = parsed.Message;
                var timestamp = parsed.Timestamp;
                var policyMatch = PolicyPayloadRegex().Match(message);
                if (!policyMatch.Success)
                {
                    continue;
                }

                var policyJson = policyMatch.Groups["json"].Value;
                if (string.IsNullOrWhiteSpace(policyJson))
                {
                    continue;
                }

                try
                {
                    using var document = JsonDocument.Parse(policyJson);
                    CollectPolicyAppIds(
                        document.RootElement,
                        knownAppIds,
                        policyNameMap,
                        policyTimeMap,
                        policyIntentMap,
                        policyTargetContextMap,
                        timestamp);
                }
                catch (JsonException)
                {
                    // Keep processing other lines; malformed payloads are ignored.
                }
            }
        }

        TryPopulateRegistryStates(host, knownAppIds, knownWin32AppIds, policyNameMap, policyTimeMap, registryStateMap);
        TryPopulateStatusServiceReportStates(host, knownAppIds, knownWin32AppIds, policyNameMap, policyTimeMap, registryStateMap);
        TryPopulateReportingRegistryStates(host, knownAppIds, knownWin32AppIds, policyNameMap, policyTimeMap, registryStateMap);

        var statusFileCandidates = GetLatestFiles(resolvedLogDirectory, "AppWorkload*.log", 3)
            .Concat(GetLatestFiles(resolvedLogDirectory, "IntuneManagementExtension*.log", 4))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var filePath in statusFileCandidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourceFile = Path.GetFileName(filePath);

            foreach (var row in getTailEntries(filePath))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var rawLine = row.RawText;
                if (string.IsNullOrWhiteSpace(rawLine))
                {
                    continue;
                }

                var parsed = ParseImeLogLine(rawLine);
                var message = parsed.Message;
                var timestamp = parsed.Timestamp;

                var appId = ExtractStatusAppId(message, knownWin32AppIds);
                if (string.IsNullOrWhiteSpace(appId))
                {
                    continue;
                }

                var status = ClassifyInstallStatus(message);
                if (status is null)
                {
                    continue;
                }

                var resultCode = string.Empty;
                var resultMatch = ResultCodeRegex().Match(message);
                if (resultMatch.Success)
                {
                    resultCode = resultMatch.Value;
                }

                var appName = ExtractNameHint(message);
                if (string.IsNullOrWhiteSpace(appName) && policyNameMap.TryGetValue(appId, out var mappedName))
                {
                    appName = mappedName;
                }

                if (string.IsNullOrWhiteSpace(appName))
                {
                    appName = appId;
                }

                var finalMessage = message;
                if (registryStateMap.TryGetValue(appId, out var regState))
                {
                    var registryHint = regState.HasGrs
                        ? "GRS entry present"
                        : (regState.HasAppKey ? "Registry app key present" : string.Empty);
                    if (!string.IsNullOrWhiteSpace(registryHint) &&
                        !finalMessage.Contains(registryHint, StringComparison.OrdinalIgnoreCase))
                    {
                        finalMessage = $"{finalMessage} | {registryHint}";
                    }
                }

                var candidate = new ImeApplicationStatusEntry(
                    appId,
                    appName,
                    ResolvePolicyIntent(appId, policyIntentMap),
                    ResolveTargetInstallContext(appId, policyTargetContextMap),
                    status,
                    timestamp,
                    resultCode,
                    sourceFile,
                    finalMessage,
                    false,
                    []);

                if (!statusMap.TryGetValue(appId, out var current))
                {
                    statusMap[appId] = candidate;
                    continue;
                }

                var currentTs = current.LastUpdated ?? DateTimeOffset.MinValue;
                var candidateTs = candidate.LastUpdated ?? DateTimeOffset.MinValue;
                if (candidateTs >= currentTs)
                {
                    statusMap[appId] = candidate;
                }
            }
        }

        foreach (var appId in knownWin32AppIds)
        {
            if (statusMap.ContainsKey(appId))
            {
                continue;
            }

            var appName = policyNameMap.TryGetValue(appId, out var name) && !string.IsNullOrWhiteSpace(name)
                ? name
                : appId;

            var status = "Unknown";
            var source = "AppWorkload policy payload";
            var message = "App available in policy payload, no matching status line found in scanned logs.";
            if (registryStateMap.TryGetValue(appId, out var regState))
            {
                if (regState.HasGrs)
                {
                    status = "RetryPending";
                    source = "Registry GRS";
                    message = "Registry indicates pending reevaluation (GRS entry present).";
                }
                else if (regState.HasAppKey)
                {
                    status = "Detected";
                    source = "Registry Win32Apps";
                    message = "AppId found in Win32Apps registry state.";
                }
            }

            policyTimeMap.TryGetValue(appId, out var lastUpdated);
            statusMap[appId] = new ImeApplicationStatusEntry(
                appId,
                appName,
                ResolvePolicyIntent(appId, policyIntentMap),
                ResolveTargetInstallContext(appId, policyTargetContextMap),
                status,
                lastUpdated,
                string.Empty,
                source,
                message,
                false,
                []);
        }

        return statusMap.Values
            .Select(entry => MergeWithRegistryState(entry, registryStateMap.TryGetValue(entry.AppId, out var state) ? state : null))
            .OrderBy(entry => entry.AppName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.AppId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string ResolveLogDirectoryForHost(string host, string logDirectory)
    {
        if (IsLocalHost(host))
        {
            return logDirectory;
        }

        if (string.IsNullOrWhiteSpace(logDirectory))
        {
            return string.Empty;
        }

        if (logDirectory.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return logDirectory;
        }

        if (logDirectory.Length < 3 || logDirectory[1] != ':')
        {
            return string.Empty;
        }

        var driveLetter = char.ToUpperInvariant(logDirectory[0]);
        var remainder = logDirectory[2..].TrimStart('\\');
        return $@"\\{host}\{driveLetter}$\{remainder}";
    }

    private static bool IsLocalHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return true;
        }

        var normalized = host.Trim();
        if (normalized is "." or "localhost")
        {
            return true;
        }

        if (normalized.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var dot = normalized.IndexOf('.');
        if (dot > 0 && normalized[..dot].Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static IReadOnlyList<string> GetLatestFiles(string directoryPath, string pattern, int maxFiles)
    {
        try
        {
            return Directory.EnumerateFiles(directoryPath, pattern, SearchOption.TopDirectoryOnly)
                .Select(path => new FileInfo(path))
                .OrderByDescending(info => info.LastWriteTimeUtc)
                .Take(Math.Max(1, maxFiles))
                .Select(info => info.FullName)
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    private static IReadOnlyList<TailLogEntry> ReadTailLogEntries(string filePath, int maxEntries, CancellationToken cancellationToken)
    {
        var targetLines = Math.Max(1, maxEntries);
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length <= 0)
        {
            return [];
        }

        const int minWindowBytes = 128 * 1024;
        const int maxWindowBytes = 8 * 1024 * 1024;
        long windowBytes = Math.Clamp((long)targetLines * 512L, minWindowBytes, maxWindowBytes);
        var fileLength = stream.Length;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var offset = Math.Max(0, fileLength - windowBytes);
            stream.Seek(offset, SeekOrigin.Begin);

            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 8192, leaveOpen: true);
            var chunk = reader.ReadToEnd();
            stream.Position = 0;

            if (string.IsNullOrEmpty(chunk))
            {
                return [];
            }

            var lines = chunk.Split(["\r\n", "\n"], StringSplitOptions.None);
            var startIndex = offset > 0 ? 1 : 0; // Skip possibly truncated first line when reading from middle.
            if (startIndex >= lines.Length)
            {
                return [];
            }

            var queue = new Queue<TailLogEntry>(targetLines);
            StringBuilder? pendingCmTrace = null;
            var pendingStartLine = 0;
            for (var i = startIndex; i < lines.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var line = lines[i];
                if (i == lines.Length - 1 && line.Length == 0)
                {
                    continue;
                }

                var relativeLine = i + 1;
                if (pendingCmTrace is not null)
                {
                    pendingCmTrace.Append('\n').Append(line);
                    if (CmTraceLineRegex().IsMatch(pendingCmTrace.ToString()))
                    {
                        if (queue.Count == targetLines)
                        {
                            queue.Dequeue();
                        }

                        queue.Enqueue(new TailLogEntry(pendingStartLine, pendingCmTrace.ToString()));
                        pendingCmTrace = null;
                        pendingStartLine = 0;
                    }

                    continue;
                }

                var trimmed = line.TrimStart();
                if (trimmed.StartsWith("<![LOG[", StringComparison.Ordinal))
                {
                    pendingCmTrace = new StringBuilder(line);
                    pendingStartLine = relativeLine;

                    if (CmTraceLineRegex().IsMatch(pendingCmTrace.ToString()))
                    {
                        if (queue.Count == targetLines)
                        {
                            queue.Dequeue();
                        }

                        queue.Enqueue(new TailLogEntry(pendingStartLine, pendingCmTrace.ToString()));
                        pendingCmTrace = null;
                        pendingStartLine = 0;
                    }

                    continue;
                }

                if (queue.Count == targetLines)
                {
                    queue.Dequeue();
                }

                queue.Enqueue(new TailLogEntry(relativeLine, line));
            }

            if (pendingCmTrace is not null)
            {
                if (queue.Count == targetLines)
                {
                    queue.Dequeue();
                }

                queue.Enqueue(new TailLogEntry(pendingStartLine <= 0 ? 1 : pendingStartLine, pendingCmTrace.ToString()));
            }

            if (queue.Count >= targetLines || offset == 0 || windowBytes >= fileLength)
            {
                return queue.ToArray();
            }

            windowBytes = Math.Min(fileLength, windowBytes * 2);
        }
    }

    private static ParsedImeLogLine ParseImeLogLine(string rawLine)
    {
        var message = rawLine;
        DateTimeOffset? timestamp = null;
        var component = string.Empty;
        var severity = "Information";

        var lineMatch = CmTraceLineRegex().Match(rawLine);
        if (!lineMatch.Success)
        {
            return new ParsedImeLogLine(timestamp, message, component, severity);
        }

        message = lineMatch.Groups["msg"].Value;
        var meta = lineMatch.Groups["meta"].Value;
        var datePart = string.Empty;
        var timePart = string.Empty;
        var type = "1";
        foreach (Match attr in CmTraceAttrRegex().Matches(meta))
        {
            var key = attr.Groups["key"].Value.ToLowerInvariant();
            var value = attr.Groups["value"].Value;
            switch (key)
            {
                case "date":
                    datePart = value;
                    break;
                case "time":
                    timePart = value;
                    break;
                case "component":
                    component = value;
                    break;
                case "type":
                    type = value;
                    break;
            }
        }

        if (!string.IsNullOrWhiteSpace(datePart) && !string.IsNullOrWhiteSpace(timePart))
        {
            timestamp = ParseTimestampFlexible($"{datePart} {timePart}");
            if (timestamp is null)
            {
                var split = timePart.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (split.Length > 0)
                {
                    timestamp = ParseTimestampFlexible($"{datePart} {split[0]}");
                }
            }
        }

        severity = type switch
        {
            "3" => "Error",
            "2" => "Warning",
            "4" => "Verbose",
            "0" => "Verbose",
            _ => "Information"
        };

        return new ParsedImeLogLine(timestamp, message, component, severity);
    }

    private static ImeTimelineClassification ClassifyImeTimelineEntry(string component, string message, bool isPolicyPayload)
    {
        var displayComponent = ResolveImeTimelineComponent(component, message);
        var flow = string.Empty;
        var phase = string.Empty;
        var effect = string.Empty;

        if (isPolicyPayload || TimelinePolicySyncRegex().IsMatch(message))
        {
            flow = "Policy Sync";
            phase = "policy_sync";
            effect = "Fetch/refresh assignment policy";
        }
        else if (TimelineDownloadRegex().IsMatch(message))
        {
            flow = "Download";
            phase = "download_prepare";
            effect = "Prepare content for enforcement";
        }
        else if (TimelineDetectionRegex().IsMatch(message))
        {
            flow = "Execution";
            phase = "detection";
            effect = "Evaluate detection state";
        }
        else if (TimelineRequirementsRegex().IsMatch(message))
        {
            flow = "Execution";
            phase = "requirements";
            effect = "Evaluate requirements/applicability";
        }
        else if (TimelineDependenciesRegex().IsMatch(message))
        {
            flow = "Execution";
            phase = "dependencies";
            effect = "Resolve dependency chain";
        }
        else if (TimelineInstallationRegex().IsMatch(message))
        {
            flow = "Execution";
            phase = "installation";
            effect = "Execute installation";
        }
        else if (TimelineExecutionRegex().IsMatch(message))
        {
            flow = "Execution";
            phase = "execution";
            effect = "Applicability/enforcement execution";
        }
        else if (TimelineReportingRegex().IsMatch(message))
        {
            flow = "Reporting";
            phase = "reporting";
            effect = "Persist/report end state";
        }
        else if (TimelineStatusServiceRegex().IsMatch(message))
        {
            flow = "Status Service";
            phase = "policy_sync";
            effect = "Gateway/session processing";
        }

        var appId = ResolveTimelineAppId(message);
        var sessionId = ResolveTimelineSessionId(message);
        var policyId = ResolveTimelinePolicyId(message);
        var userId = ResolveTimelineUserId(message);
        var resultCode = ResolveTimelineResultCode(message);
        var entityType = !string.IsNullOrWhiteSpace(appId)
            ? "App"
            : !string.IsNullOrWhiteSpace(policyId)
                ? "Policy"
                : !string.IsNullOrWhiteSpace(sessionId)
                    ? "Session"
                    : string.Empty;
        var entityId = !string.IsNullOrWhiteSpace(appId)
            ? appId
            : !string.IsNullOrWhiteSpace(policyId)
                ? policyId
                : sessionId;

        return new ImeTimelineClassification(
            flow,
            phase,
            effect,
            BuildImeTimelineCorrelationSummary(message),
            displayComponent,
            entityType,
            entityId,
            policyId,
            sessionId,
            userId,
            resultCode);
    }

    private static string ResolveImeTimelineComponent(string component, string message)
    {
        var tags = ExtractLeadingBracketTags(message);
        if (tags.Count >= 2)
        {
            return $"{tags[0]}/{tags[1]}";
        }

        if (tags.Count == 1)
        {
            return tags[0];
        }

        return component;
    }

    private static string BuildImeTimelineCorrelationSummary(string message)
    {
        var appId = ResolveTimelineAppId(message);
        if (!string.IsNullOrWhiteSpace(appId))
        {
            return $"App {appId}";
        }

        var sessionId = ResolveTimelineSessionId(message);
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            return $"Session {sessionId}";
        }

        var policyId = ResolveTimelinePolicyId(message);
        if (!string.IsNullOrWhiteSpace(policyId))
        {
            return $"Policy {policyId}";
        }

        var resultCode = ResolveTimelineResultCode(message);
        if (!string.IsNullOrWhiteSpace(resultCode))
        {
            return $"Error {resultCode}";
        }

        var guidMatch = GuidRegex().Match(message);
        return guidMatch.Success ? $"Id {NormalizeGuidId(guidMatch.Groups["id"].Value)}" : string.Empty;
    }

    private static string ResolveTimelineAppId(string message)
    {
        var appIdMatch = AppIdHintRegex().Match(message);
        return appIdMatch.Success ? NormalizeGuidId(appIdMatch.Groups["id"].Value) : string.Empty;
    }

    private static string ResolveTimelineSessionId(string message)
    {
        var sessionMatch = SessionIdRegex().Match(message);
        return sessionMatch.Success ? sessionMatch.Groups["id"].Value.Trim() : string.Empty;
    }

    private static string ResolveTimelinePolicyId(string message)
    {
        var policyMatch = PolicyIdRegex().Match(message);
        return policyMatch.Success ? policyMatch.Groups["id"].Value.Trim() : string.Empty;
    }

    private static string ResolveTimelineUserId(string message)
    {
        var userMatch = UserIdRegex().Match(message);
        return userMatch.Success ? userMatch.Groups["id"].Value.Trim() : string.Empty;
    }

    private static string ResolveTimelineResultCode(string message)
    {
        var errorMatch = ErrorCodeHintRegex().Match(message);
        if (errorMatch.Success)
        {
            return errorMatch.Groups["id"].Value.Trim();
        }

        var hex = ResolveHex(message);
        return string.IsNullOrWhiteSpace(hex) ? string.Empty : hex;
    }

    private static IReadOnlyList<string> ExtractLeadingBracketTags(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return [];
        }

        var tags = new List<string>(2);
        var index = 0;
        while (index < message.Length && message[index] == '[')
        {
            var endIndex = message.IndexOf(']', index + 1);
            if (endIndex <= index + 1)
            {
                break;
            }

            tags.Add(message.Substring(index + 1, endIndex - index - 1).Trim());
            index = endIndex + 1;
        }

        return tags;
    }

    private static void CollectPolicyAppIds(
        JsonElement element,
        HashSet<string> knownAppIds,
        IDictionary<string, string> policyNameMap,
        IDictionary<string, DateTimeOffset?> policyTimeMap,
        IDictionary<string, string> policyIntentMap,
        IDictionary<string, string> policyTargetContextMap,
        DateTimeOffset? timestamp)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
            {
                string? id = null;
                string? name = null;
                string? intent = null;
                string? targetType = null;
                foreach (var property in element.EnumerateObject())
                {
                    if (id is null &&
                        PolicyIdPropertyNames.Contains(property.Name) &&
                        property.Value.ValueKind == JsonValueKind.String)
                    {
                        id = NormalizeGuidId(property.Value.GetString());
                    }

                    if (name is null &&
                        PolicyNamePropertyNames.Contains(property.Name) &&
                        property.Value.ValueKind == JsonValueKind.String)
                    {
                        name = property.Value.GetString();
                    }

                    if (intent is null && PolicyIntentPropertyNames.Contains(property.Name))
                    {
                        intent = NormalizePolicyIntent(ExtractJsonScalar(property.Value));
                    }

                    if (targetType is null && PolicyTargetTypePropertyNames.Contains(property.Name))
                    {
                        targetType = NormalizeTargetType(ExtractJsonScalar(property.Value));
                    }
                }

                if (!string.IsNullOrWhiteSpace(id))
                {
                    var normalizedId = AddKnownAppId(knownAppIds, policyNameMap, policyTimeMap, id, name, timestamp);
                    if (!string.IsNullOrWhiteSpace(normalizedId))
                    {
                        if (!string.IsNullOrWhiteSpace(intent))
                        {
                            policyIntentMap[normalizedId] = intent;
                        }

                        if (!string.IsNullOrWhiteSpace(targetType))
                        {
                            policyTargetContextMap[normalizedId] = targetType;
                        }
                    }
                }

                foreach (var property in element.EnumerateObject())
                {
                    CollectPolicyAppIds(
                        property.Value,
                        knownAppIds,
                        policyNameMap,
                        policyTimeMap,
                        policyIntentMap,
                        policyTargetContextMap,
                        timestamp);
                }

                break;
            }
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    CollectPolicyAppIds(
                        item,
                        knownAppIds,
                        policyNameMap,
                        policyTimeMap,
                        policyIntentMap,
                        policyTargetContextMap,
                        timestamp);
                }

                break;
        }
    }

    private static string AddKnownAppId(
        HashSet<string> knownAppIds,
        IDictionary<string, string> policyNameMap,
        IDictionary<string, DateTimeOffset?> policyTimeMap,
        string rawId,
        string? name,
        DateTimeOffset? timestamp)
    {
        var normalizedId = NormalizeGuidId(rawId);
        if (string.IsNullOrWhiteSpace(normalizedId))
        {
            return string.Empty;
        }

        knownAppIds.Add(normalizedId);

        if (!string.IsNullOrWhiteSpace(name))
        {
            if (!policyNameMap.TryGetValue(normalizedId, out var existingName) ||
                string.IsNullOrWhiteSpace(existingName) ||
                string.Equals(existingName, normalizedId, StringComparison.OrdinalIgnoreCase))
            {
                policyNameMap[normalizedId] = name.Trim();
            }
        }

        if (timestamp.HasValue)
        {
            policyTimeMap[normalizedId] = timestamp;
        }

        return normalizedId;
    }

    private static string NormalizeGuidId(string? rawId)
    {
        if (string.IsNullOrWhiteSpace(rawId))
        {
            return string.Empty;
        }

        return Guid.TryParse(rawId.Trim(), out var parsed)
            ? parsed.ToString("D")
            : string.Empty;
    }

    private static string ExtractJsonScalar(JsonElement value) =>
        value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => string.Empty
        };

    private static string NormalizePolicyIntent(string? rawIntent)
    {
        if (string.IsNullOrWhiteSpace(rawIntent))
        {
            return string.Empty;
        }

        var normalized = rawIntent.Trim();
        if (int.TryParse(normalized, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numericIntent))
        {
            return numericIntent switch
            {
                4 => "Uninstall",
                3 => "Required",
                2 => "Available",
                1 => "Available",
                _ => normalized
            };
        }

        var lower = normalized.ToLowerInvariant();
        if (lower.Contains("required", StringComparison.Ordinal))
        {
            return "Required";
        }

        if (lower.Contains("available", StringComparison.Ordinal))
        {
            return "Available";
        }

        if (lower.Contains("uninstall", StringComparison.Ordinal) ||
            lower.Contains("remove", StringComparison.Ordinal))
        {
            return "Uninstall";
        }

        return normalized;
    }

    private static string NormalizeTargetType(string? rawTargetType)
    {
        if (string.IsNullOrWhiteSpace(rawTargetType))
        {
            return string.Empty;
        }

        var normalized = rawTargetType.Trim();
        if (int.TryParse(normalized, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numericType))
        {
            return numericType switch
            {
                0 => "None",
                1 => "User",
                2 => "Device",
                3 => "Both",
                _ => normalized
            };
        }

        return normalized;
    }

    private static string ResolvePolicyIntent(string appId, IReadOnlyDictionary<string, string> policyIntentMap) =>
        policyIntentMap.TryGetValue(appId, out var intent) && !string.IsNullOrWhiteSpace(intent)
            ? intent
            : "Unknown";

    private static string ResolveTargetInstallContext(string appId, IReadOnlyDictionary<string, string> policyTargetContextMap) =>
        policyTargetContextMap.TryGetValue(appId, out var targetContext) && !string.IsNullOrWhiteSpace(targetContext)
            ? targetContext
            : "Unknown";

    private static string ExtractStatusAppId(string message, HashSet<string> knownAppIds)
    {
        var hintMatch = AppIdHintRegex().Match(message);
        if (hintMatch.Success)
        {
            return NormalizeGuidId(hintMatch.Groups["id"].Value);
        }

        foreach (Match match in GuidRegex().Matches(message))
        {
            var candidate = NormalizeGuidId(match.Groups["id"].Value);
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            if (knownAppIds.Contains(candidate))
            {
                return candidate;
            }
        }

        return string.Empty;
    }

    private static string ExtractNameHint(string message)
    {
        var nameMatch = NameHintRegex().Match(message);
        if (nameMatch.Success)
        {
            return nameMatch.Groups["name"].Value.Trim();
        }

        return string.Empty;
    }

    private static string? ClassifyInstallStatus(string message)
    {
        var normalized = message.ToLowerInvariant();
        if (normalized.Contains("processing subgraph with app ids", StringComparison.Ordinal) ||
            normalized.Contains("processing subgraph with app id", StringComparison.Ordinal))
        {
            // V3 processor orchestration line; does not represent an install state.
            return null;
        }

        if (normalized.Contains("failed", StringComparison.Ordinal) ||
            normalized.Contains("error", StringComparison.Ordinal) ||
            normalized.Contains("0x8", StringComparison.Ordinal) ||
            normalized.Contains("exit code", StringComparison.Ordinal) ||
            normalized.Contains("return code", StringComparison.Ordinal) ||
            normalized.Contains("timeout", StringComparison.Ordinal) ||
            normalized.Contains("timed out", StringComparison.Ordinal))
        {
            return "Failed";
        }

        if (normalized.Contains("installed", StringComparison.Ordinal) ||
            normalized.Contains("succeeded", StringComparison.Ordinal) ||
            normalized.Contains("completed successfully", StringComparison.Ordinal) ||
            normalized.Contains("detected as installed", StringComparison.Ordinal))
        {
            return "Installed";
        }

        if (normalized.Contains("not installed", StringComparison.Ordinal) ||
            normalized.Contains("not detected", StringComparison.Ordinal))
        {
            return "NotInstalled";
        }

        var hasProcessingInstallSignal =
            normalized.Contains("processing install", StringComparison.Ordinal) ||
            normalized.Contains("processing app install", StringComparison.Ordinal) ||
            normalized.Contains("processing enforcement", StringComparison.Ordinal);

        if (normalized.Contains("installing", StringComparison.Ordinal) ||
            normalized.Contains("downloading", StringComparison.Ordinal) ||
            normalized.Contains("enforcing", StringComparison.Ordinal) ||
            hasProcessingInstallSignal ||
            normalized.Contains("queued", StringComparison.Ordinal) ||
            normalized.Contains("retry", StringComparison.Ordinal))
        {
            return "InProgress";
        }

        return null;
    }

    private static ImeApplicationStatusEntry MergeWithRegistryState(ImeApplicationStatusEntry baseEntry, RegistryAppState? registryState)
    {
        if (registryState is null || registryState.IdentityStates.Count == 0)
        {
            return baseEntry with
            {
                IsInstalledForAnyIdentity = false,
                IdentityStatuses = []
            };
        }

        IEnumerable<RegistryIdentityState> selectedIdentityStates = registryState.IdentityStates.Values;
        if (registryState.IsV3Managed)
        {
            var v3States = selectedIdentityStates
                .Where(identity => identity.Source.Contains("Win32Apps Reporting", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (v3States.Length > 0)
            {
                selectedIdentityStates = v3States;
            }
        }

        var identityStatuses = selectedIdentityStates
            .Select(identity => new ImeApplicationIdentityStatusEntry(
                identity.IdentityId,
                IsSystemIdentity(identity.IdentityId) ? "System" : "User",
                identity.InstallStatus,
                identity.LastUpdated,
                identity.ResultCode,
                identity.Source,
                identity.Details))
            .OrderBy(entry => string.Equals(entry.Scope, "System", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(entry => entry.IdentityId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var mergedStatus = ResolveAggregatedInstallStatus(baseEntry.InstallStatus, identityStatuses);
        var mergedLastUpdated = ResolveAggregatedTimestamp(baseEntry.LastUpdated, identityStatuses);
        var mergedResultCode = ResolveAggregatedResultCode(baseEntry.ResultCode, identityStatuses, mergedStatus);
        var mergedSource = ResolveAggregatedSource(baseEntry.SourceFile, identityStatuses);
        var mergedMessage = BuildAggregatedMessage(baseEntry.LastMessage, identityStatuses);
        var installedForAny = identityStatuses.Any(identity => IsInstalledStatus(identity.InstallStatus));
        var targetInstallContext = ResolveTargetInstallContext(baseEntry.TargetInstallContext);
        var mergedIntent = ResolveIntent(baseEntry.Intent, registryState.Intent);

        return baseEntry with
        {
            Intent = mergedIntent,
            InstallStatus = mergedStatus,
            LastUpdated = mergedLastUpdated,
            ResultCode = mergedResultCode,
            SourceFile = mergedSource,
            LastMessage = mergedMessage,
            TargetInstallContext = targetInstallContext,
            IsInstalledForAnyIdentity = installedForAny,
            IdentityStatuses = identityStatuses
        };
    }

    private static string ResolveAggregatedInstallStatus(
        string fallbackStatus,
        IReadOnlyList<ImeApplicationIdentityStatusEntry> identityStatuses)
    {
        if (identityStatuses.Count == 0)
        {
            return string.IsNullOrWhiteSpace(fallbackStatus) ? "Unknown" : fallbackStatus;
        }

        var hasFailed = identityStatuses.Any(entry => string.Equals(entry.InstallStatus, "Failed", StringComparison.OrdinalIgnoreCase));
        if (hasFailed)
        {
            return "Failed";
        }

        var installedCount = identityStatuses.Count(entry => IsInstalledStatus(entry.InstallStatus));
        var notInstalledCount = identityStatuses.Count(entry => string.Equals(entry.InstallStatus, "NotInstalled", StringComparison.OrdinalIgnoreCase));

        if (installedCount > 0)
        {
            if (notInstalledCount > 0)
            {
                return "PartiallyInstalled";
            }

            // Installed wins over additional transient states like RetryPending.
            return "Installed";
        }

        if (notInstalledCount > 0)
        {
            return "NotInstalled";
        }

        if (IsTerminalStatus(fallbackStatus))
        {
            return fallbackStatus;
        }

        var hasInProgress = identityStatuses.Any(entry =>
            string.Equals(entry.InstallStatus, "InProgress", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(entry.InstallStatus, "RetryPending", StringComparison.OrdinalIgnoreCase));
        if (hasInProgress)
        {
            return "InProgress";
        }

        if (identityStatuses.Any(entry => string.Equals(entry.InstallStatus, "Detected", StringComparison.OrdinalIgnoreCase)))
        {
            return "Detected";
        }

        if (identityStatuses.Any(entry => string.Equals(entry.InstallStatus, "RetryPending", StringComparison.OrdinalIgnoreCase)))
        {
            return "RetryPending";
        }

        return string.IsNullOrWhiteSpace(fallbackStatus) ? "Unknown" : fallbackStatus;
    }

    private static DateTimeOffset? ResolveAggregatedTimestamp(
        DateTimeOffset? fallback,
        IReadOnlyList<ImeApplicationIdentityStatusEntry> identityStatuses)
    {
        var latest = fallback;
        foreach (var identity in identityStatuses)
        {
            if (!identity.LastUpdated.HasValue)
            {
                continue;
            }

            if (!latest.HasValue || identity.LastUpdated.Value > latest.Value)
            {
                latest = identity.LastUpdated;
            }
        }

        return latest;
    }

    private static string ResolveAggregatedResultCode(
        string fallback,
        IReadOnlyList<ImeApplicationIdentityStatusEntry> identityStatuses,
        string aggregatedStatus)
    {
        if (string.Equals(aggregatedStatus, "Failed", StringComparison.OrdinalIgnoreCase))
        {
            var failedCode = identityStatuses
                .Where(entry => string.Equals(entry.InstallStatus, "Failed", StringComparison.OrdinalIgnoreCase))
                .Select(entry => entry.ResultCode)
                .FirstOrDefault(code => !string.IsNullOrWhiteSpace(code));
            if (!string.IsNullOrWhiteSpace(failedCode))
            {
                return failedCode;
            }
        }

        var first = identityStatuses
            .Select(entry => entry.ResultCode)
            .FirstOrDefault(code => !string.IsNullOrWhiteSpace(code));
        return string.IsNullOrWhiteSpace(first) ? fallback : first;
    }

    private static string ResolveAggregatedSource(
        string fallback,
        IReadOnlyList<ImeApplicationIdentityStatusEntry> identityStatuses)
    {
        if (identityStatuses.Count == 0)
        {
            return fallback;
        }

        var sources = identityStatuses
            .Select(entry => entry.Source)
            .Where(source => !string.IsNullOrWhiteSpace(source))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (sources.Length == 0)
        {
            return string.IsNullOrWhiteSpace(fallback) ? "Registry Win32Apps" : fallback;
        }

        return string.Join(" + ", sources);
    }

    private static string BuildAggregatedMessage(
        string fallbackMessage,
        IReadOnlyList<ImeApplicationIdentityStatusEntry> identityStatuses)
    {
        if (identityStatuses.Count == 0)
        {
            return fallbackMessage;
        }

        var summary = string.Join("; ", identityStatuses.Select(identity =>
            $"{identity.Scope}:{identity.IdentityId}={identity.InstallStatus}{(string.IsNullOrWhiteSpace(identity.ResultCode) ? string.Empty : $" ({identity.ResultCode})")}"));
        return string.IsNullOrWhiteSpace(fallbackMessage)
            ? $"Registry states: {summary}"
            : $"Registry states: {summary} | Last log hint: {fallbackMessage}";
    }

    private static bool IsInstalledStatus(string status) =>
        string.Equals(status, "Installed", StringComparison.OrdinalIgnoreCase);

    private static string ResolveTargetInstallContext(string currentValue)
    {
        var normalized = currentValue?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(normalized) &&
            !string.Equals(normalized, "Unknown", StringComparison.OrdinalIgnoreCase))
        {
            return normalized;
        }

        return "Unknown";
    }

    private static string ResolveIntent(string fallback, string registryIntent)
    {
        if (IsMeaningfulIntent(fallback))
        {
            return fallback;
        }

        return IsMeaningfulIntent(registryIntent) ? registryIntent : fallback;
    }

    private static bool IsMeaningfulIntent(string? value)
    {
        return !string.IsNullOrWhiteSpace(value) &&
               !string.Equals(value.Trim(), "Unknown", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTerminalStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            return false;
        }

        return string.Equals(status, "Installed", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(status, "NotInstalled", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(status, "Failed", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(status, "PartiallyInstalled", StringComparison.OrdinalIgnoreCase);
    }

    private static void TryPopulateRegistryStates(
        string host,
        HashSet<string> knownAppIds,
        HashSet<string> knownWin32AppIds,
        IDictionary<string, string> policyNameMap,
        IDictionary<string, DateTimeOffset?> policyTimeMap,
        IDictionary<string, RegistryAppState> registryStateMap)
    {
        try
        {
            using var root = OpenWin32AppsRoot(host);
            if (root is null)
            {
                return;
            }

            foreach (var identityName in root.GetSubKeyNames())
            {
                if (string.Equals(identityName, "OperationalState", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(identityName, "Reporting", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                using var identityKey = root.OpenSubKey(identityName, writable: false);
                if (identityKey is null)
                {
                    continue;
                }

                var normalizedIdentity = NormalizeIdentityId(identityName);
                if (string.IsNullOrWhiteSpace(normalizedIdentity))
                {
                    continue;
                }

                foreach (var childName in identityKey.GetSubKeyNames())
                {
                    if (string.Equals(childName, "GRS", StringComparison.OrdinalIgnoreCase))
                    {
                        using var grsKey = identityKey.OpenSubKey(childName, writable: false);
                        if (grsKey is null)
                        {
                            continue;
                        }

                        foreach (var grsName in grsKey.GetSubKeyNames())
                        {
                            foreach (Match match in GuidRegex().Matches(grsName))
                            {
                                var id = AddKnownAppId(
                                    knownAppIds,
                                    policyNameMap,
                                    policyTimeMap,
                                    match.Groups["id"].Value,
                                    string.Empty,
                                    null);
                                if (string.IsNullOrWhiteSpace(id))
                                {
                                    continue;
                                }

                                knownWin32AppIds.Add(id);
                                var state = GetOrAddRegistryState(registryStateMap, id);
                                var grsIdentityState = GetOrAddIdentityState(state, normalizedIdentity);
                                grsIdentityState.HasGrs = true;
                                if (!grsIdentityState.HasAppKey)
                                {
                                    grsIdentityState.InstallStatus = "RetryPending";
                                    grsIdentityState.Source = "Registry GRS";
                                    grsIdentityState.Details = "GRS entry present.";
                                }
                            }
                        }

                        continue;
                    }

                    var normalizedChildAppId = ExtractAppIdFromWin32AppKeyName(childName);
                    if (string.IsNullOrWhiteSpace(normalizedChildAppId))
                    {
                        continue;
                    }

                    var appId = AddKnownAppId(knownAppIds, policyNameMap, policyTimeMap, normalizedChildAppId, string.Empty, null);
                    if (string.IsNullOrWhiteSpace(appId))
                    {
                        continue;
                    }

                    knownWin32AppIds.Add(appId);
                    var appState = GetOrAddRegistryState(registryStateMap, appId);
                    var identityState = GetOrAddIdentityState(appState, normalizedIdentity);
                    identityState.HasAppKey = true;

                    using var appKey = identityKey.OpenSubKey(childName, writable: false);
                    ApplyRegistryIdentityStateFromKey(identityState, appKey);
                }
            }
        }
        catch
        {
            // Registry access is best-effort and should not break the full operation.
        }
    }

    private static void TryPopulateStatusServiceReportStates(
        string host,
        HashSet<string> knownAppIds,
        ISet<string> knownWin32AppIds,
        IDictionary<string, string> policyNameMap,
        IDictionary<string, DateTimeOffset?> policyTimeMap,
        IDictionary<string, RegistryAppState> registryStateMap)
    {
        try
        {
            using var root = OpenStatusServiceReportsRoot(host);
            if (root is null)
            {
                return;
            }

            Traverse(root, "StatusServiceReports", depth: 0);

            void Traverse(RegistryKey key, string path, int depth)
            {
                if (depth > 8)
                {
                    return;
                }

                var pathGuids = ExtractNormalizedGuids(path);
                if (pathGuids.Count > 0)
                {
                    var appCandidates = pathGuids
                        .Where(candidate => knownWin32AppIds.Contains(candidate))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    foreach (var appCandidate in appCandidates)
                    {
                        var appId = AddKnownAppId(knownAppIds, policyNameMap, policyTimeMap, appCandidate, string.Empty, null);
                        if (string.IsNullOrWhiteSpace(appId))
                        {
                            continue;
                        }

                        var identityId = ResolveIdentityIdForReportPath(pathGuids, appId, knownWin32AppIds);
                        ApplyStatusServiceReportKeyValues(key, appId, identityId, registryStateMap);
                    }
                }

                foreach (var childName in key.GetSubKeyNames())
                {
                    using var child = key.OpenSubKey(childName, writable: false);
                    if (child is null)
                    {
                        continue;
                    }

                    Traverse(child, $"{path}\\{childName}", depth + 1);
                }
            }
        }
        catch
        {
            // Best-effort only. Missing/inaccessible report keys should never break status loading.
        }
    }

    private static void TryPopulateReportingRegistryStates(
        string host,
        HashSet<string> knownAppIds,
        HashSet<string> knownWin32AppIds,
        IDictionary<string, string> policyNameMap,
        IDictionary<string, DateTimeOffset?> policyTimeMap,
        IDictionary<string, RegistryAppState> registryStateMap)
    {
        try
        {
            using var root = OpenWin32AppsReportingRoot(host);
            if (root is null)
            {
                return;
            }

            var appAuthorityMap = ReadV3AppAuthorityMap(root);
            foreach (var (appId, _) in appAuthorityMap)
            {
                var normalizedAppId = AddKnownAppId(knownAppIds, policyNameMap, policyTimeMap, appId, string.Empty, null);
                if (string.IsNullOrWhiteSpace(normalizedAppId))
                {
                    continue;
                }

                knownWin32AppIds.Add(normalizedAppId);
                GetOrAddRegistryState(registryStateMap, normalizedAppId).IsV3Managed = true;
            }

            Traverse(root, "Reporting", depth: 0);

            void Traverse(RegistryKey key, string path, int depth)
            {
                if (depth > 8)
                {
                    return;
                }

                var pathGuids = ExtractNormalizedGuids(path);
                if (key.GetValueNames().Length > 0)
                {
                    ApplyReportingKeyValues(
                        key,
                        pathGuids,
                        knownAppIds,
                        knownWin32AppIds,
                        policyNameMap,
                        policyTimeMap,
                        appAuthorityMap,
                        registryStateMap);
                }

                foreach (var childName in key.GetSubKeyNames())
                {
                    using var child = key.OpenSubKey(childName, writable: false);
                    if (child is null)
                    {
                        continue;
                    }

                    Traverse(child, $"{path}\\{childName}", depth + 1);
                }
            }
        }
        catch
        {
            // Best-effort only. Missing/inaccessible reporting keys should never break status loading.
        }
    }

    private static Dictionary<string, int> ReadV3AppAuthorityMap(RegistryKey reportingRoot)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        using var authorityKey = reportingRoot.OpenSubKey("AppAuthority", writable: false);
        if (authorityKey is null)
        {
            return map;
        }

        foreach (var valueName in authorityKey.GetValueNames())
        {
            var appId = NormalizeGuidId(valueName);
            if (string.IsNullOrWhiteSpace(appId))
            {
                continue;
            }

            if (TryParseInstallContextValue(authorityKey.GetValue(valueName), out var installContext))
            {
                map[appId] = installContext;
            }
        }

        foreach (var subKeyName in authorityKey.GetSubKeyNames())
        {
            var appId = NormalizeGuidId(subKeyName);
            if (string.IsNullOrWhiteSpace(appId))
            {
                continue;
            }

            using var appKey = authorityKey.OpenSubKey(subKeyName, writable: false);
            if (appKey is null)
            {
                continue;
            }

            foreach (var valueName in appKey.GetValueNames())
            {
                if (TryParseInstallContextValue(appKey.GetValue(valueName), out var installContext))
                {
                    map[appId] = installContext;
                    break;
                }
            }
        }

        return map;
    }

    private static bool TryParseInstallContextValue(object? rawValue, out int installContext)
    {
        installContext = 0;
        var parsed = rawValue switch
        {
            int intValue => intValue,
            uint uintValue when uintValue <= int.MaxValue => (int)uintValue,
            long longValue when longValue is >= int.MinValue and <= int.MaxValue => (int)longValue,
            string text when int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var fromText) => fromText,
            _ => 0
        };

        if (parsed is 1 or 2)
        {
            installContext = parsed;
            return true;
        }

        return false;
    }

    private static void ApplyReportingKeyValues(
        RegistryKey key,
        IReadOnlyList<string> pathGuids,
        HashSet<string> knownAppIds,
        ISet<string> knownWin32AppIds,
        IDictionary<string, string> policyNameMap,
        IDictionary<string, DateTimeOffset?> policyTimeMap,
        IReadOnlyDictionary<string, int> appAuthorityMap,
        IDictionary<string, RegistryAppState> registryStateMap)
    {
        var details = new List<string>();
        var statusSignals = new List<string>();
        var resultCode = string.Empty;
        var discoveredIntent = string.Empty;
        DateTimeOffset? lastUpdated = null;
        string explicitAppId = string.Empty;
        var hasReportingStateValue = false;
        var hasStatusServiceReportValue = false;

        foreach (var valueName in key.GetValueNames())
        {
            var rawValue = key.GetValue(valueName);
            var serializedValue = SerializeRegistryValue(rawValue);
            if (string.IsNullOrWhiteSpace(serializedValue))
            {
                continue;
            }

            details.Add($"{valueName}={serializedValue}");
            statusSignals.Add($"{valueName}={serializedValue}");
            statusSignals.Add(serializedValue);

            if (string.IsNullOrWhiteSpace(resultCode))
            {
                resultCode = ExtractRegistryResultCode(valueName, rawValue, serializedValue);
            }

            if (string.IsNullOrWhiteSpace(discoveredIntent))
            {
                discoveredIntent = ExtractRegistryIntent(valueName, rawValue, serializedValue);
            }

            var candidateTime = ExtractRegistryTimestamp(valueName, rawValue, serializedValue);
            if (candidateTime.HasValue && (!lastUpdated.HasValue || candidateTime.Value > lastUpdated.Value))
            {
                lastUpdated = candidateTime;
            }

            if (string.Equals(valueName, "AppId", StringComparison.OrdinalIgnoreCase))
            {
                explicitAppId = NormalizeGuidId(serializedValue);
            }
            else if (string.Equals(valueName, "ReportingState", StringComparison.OrdinalIgnoreCase))
            {
                hasReportingStateValue = true;
            }
            else if (string.Equals(valueName, "StatusServiceReport", StringComparison.OrdinalIgnoreCase))
            {
                hasStatusServiceReportValue = true;
            }
        }

        if (details.Count == 0)
        {
            return;
        }

        var appId = string.IsNullOrWhiteSpace(explicitAppId)
            ? ResolveAppIdForReportingPath(pathGuids, knownAppIds, appAuthorityMap)
            : explicitAppId;
        if (string.IsNullOrWhiteSpace(appId))
        {
            return;
        }

        appId = AddKnownAppId(knownAppIds, policyNameMap, policyTimeMap, appId, string.Empty, null);
        if (string.IsNullOrWhiteSpace(appId))
        {
            return;
        }
        knownWin32AppIds.Add(appId);

        int? installContext = null;
        if (appAuthorityMap.TryGetValue(appId, out var mappedContext))
        {
            installContext = mappedContext;
        }

        var identityId = ResolveIdentityIdForReportingPath(pathGuids, appId, installContext);
        var detailText = BuildRegistryDetailText(details, hasGrs: false);
        var signalText = string.Join(" | ", statusSignals);
        var status = ClassifyInstallStatusFromRegistry(signalText, resultCode, hasGrs: false, hasAppKey: true);

        var source = hasStatusServiceReportValue
            ? "Registry Win32Apps Reporting (StatusServiceReport)"
            : hasReportingStateValue
                ? "Registry Win32Apps Reporting (ReportingState)"
                : "Registry Win32Apps Reporting";

        var appState = GetOrAddRegistryState(registryStateMap, appId);
        if (installContext.HasValue)
        {
            appState.IsV3Managed = true;
        }

        if (IsMeaningfulIntent(discoveredIntent))
        {
            appState.Intent = discoveredIntent;
        }

        var identityState = GetOrAddIdentityState(appState, identityId);
        identityState.HasAppKey = true;

        var currentRank = GetStatusRank(identityState.InstallStatus);
        var candidateRank = GetStatusRank(status);
        var currentIsReporting = identityState.Source.Contains("Win32Apps Reporting", StringComparison.OrdinalIgnoreCase);
        var shouldReplace = candidateRank > currentRank ||
                            (candidateRank == currentRank && !currentIsReporting);
        if (shouldReplace)
        {
            identityState.InstallStatus = status;
            identityState.Source = source;
            identityState.Details = detailText;
        }
        else if (!identityState.Source.Contains("Win32Apps Reporting", StringComparison.OrdinalIgnoreCase))
        {
            identityState.Source = $"{identityState.Source} + {source}";
        }

        if (!string.IsNullOrWhiteSpace(resultCode))
        {
            identityState.ResultCode = resultCode;
        }

        if (lastUpdated.HasValue && (!identityState.LastUpdated.HasValue || lastUpdated.Value > identityState.LastUpdated.Value))
        {
            identityState.LastUpdated = lastUpdated;
        }
    }

    private static string ResolveAppIdForReportingPath(
        IReadOnlyList<string> pathGuids,
        ISet<string> knownAppIds,
        IReadOnlyDictionary<string, int> appAuthorityMap)
    {
        if (pathGuids.Count == 0)
        {
            return string.Empty;
        }

        for (var i = pathGuids.Count - 1; i >= 0; i--)
        {
            var candidate = pathGuids[i];
            if (appAuthorityMap.ContainsKey(candidate))
            {
                return candidate;
            }
        }

        for (var i = pathGuids.Count - 1; i >= 0; i--)
        {
            var candidate = pathGuids[i];
            if (knownAppIds.Contains(candidate))
            {
                return candidate;
            }
        }

        for (var i = pathGuids.Count - 1; i >= 0; i--)
        {
            var candidate = pathGuids[i];
            if (!string.Equals(candidate, SystemIdentityId, StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        return string.Empty;
    }

    private static string ResolveIdentityIdForReportingPath(
        IReadOnlyList<string> pathGuids,
        string appId,
        int? installContext)
    {
        if (installContext == 2)
        {
            return SystemIdentityId;
        }

        if (installContext == 1)
        {
            var userIdentity = pathGuids.FirstOrDefault(candidate =>
                !string.Equals(candidate, appId, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(candidate, SystemIdentityId, StringComparison.OrdinalIgnoreCase));
            return string.IsNullOrWhiteSpace(userIdentity) ? "user-context" : userIdentity;
        }

        var systemIdentity = pathGuids.FirstOrDefault(candidate =>
            string.Equals(candidate, SystemIdentityId, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(systemIdentity) &&
            !string.Equals(appId, SystemIdentityId, StringComparison.OrdinalIgnoreCase))
        {
            return systemIdentity;
        }

        var fallbackIdentity = pathGuids.FirstOrDefault(candidate =>
            !string.Equals(candidate, appId, StringComparison.OrdinalIgnoreCase));
        return string.IsNullOrWhiteSpace(fallbackIdentity) ? SystemIdentityId : fallbackIdentity;
    }

    private static List<string> ExtractNormalizedGuids(string input)
    {
        var output = new List<string>();
        foreach (Match match in GuidRegex().Matches(input))
        {
            var normalized = NormalizeGuidId(match.Groups["id"].Value);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                continue;
            }

            if (!output.Contains(normalized, StringComparer.OrdinalIgnoreCase))
            {
                output.Add(normalized);
            }
        }

        return output;
    }

    private static string ResolveIdentityIdForReportPath(
        IReadOnlyList<string> pathGuids,
        string appId,
        ISet<string> knownAppIds)
    {
        var systemCandidate = pathGuids.FirstOrDefault(candidate =>
            string.Equals(candidate, SystemIdentityId, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(systemCandidate) &&
            !string.Equals(appId, SystemIdentityId, StringComparison.OrdinalIgnoreCase))
        {
            return systemCandidate;
        }

        var identityCandidate = pathGuids.FirstOrDefault(candidate =>
            !string.Equals(candidate, appId, StringComparison.OrdinalIgnoreCase) &&
            !knownAppIds.Contains(candidate));
        if (!string.IsNullOrWhiteSpace(identityCandidate))
        {
            return identityCandidate;
        }

        return SystemIdentityId;
    }

    private static void ApplyStatusServiceReportKeyValues(
        RegistryKey key,
        string appId,
        string identityId,
        IDictionary<string, RegistryAppState> registryStateMap)
    {
        var details = new List<string>();
        var statusSignals = new List<string>();
        var resultCode = string.Empty;
        var discoveredIntent = string.Empty;
        DateTimeOffset? lastUpdated = null;

        foreach (var valueName in key.GetValueNames())
        {
            var rawValue = key.GetValue(valueName);
            var serializedValue = SerializeRegistryValue(rawValue);
            if (!string.IsNullOrWhiteSpace(serializedValue))
            {
                details.Add($"{valueName}={serializedValue}");
                statusSignals.Add($"{valueName}={serializedValue}");
                statusSignals.Add(serializedValue);
            }

            if (string.IsNullOrWhiteSpace(resultCode))
            {
                resultCode = ExtractRegistryResultCode(valueName, rawValue, serializedValue);
            }

            if (string.IsNullOrWhiteSpace(discoveredIntent))
            {
                discoveredIntent = ExtractRegistryIntent(valueName, rawValue, serializedValue);
            }

            var candidateTime = ExtractRegistryTimestamp(valueName, rawValue, serializedValue);
            if (candidateTime.HasValue && (!lastUpdated.HasValue || candidateTime.Value > lastUpdated.Value))
            {
                lastUpdated = candidateTime;
            }
        }

        if (details.Count == 0 && string.IsNullOrWhiteSpace(resultCode))
        {
            return;
        }

        var detailText = BuildRegistryDetailText(details, hasGrs: false);
        var signalText = string.Join(" | ", statusSignals);
        var status = ClassifyInstallStatusFromRegistry(signalText, resultCode, hasGrs: false, hasAppKey: true);

        var appState = GetOrAddRegistryState(registryStateMap, appId);
        if (IsMeaningfulIntent(discoveredIntent))
        {
            appState.Intent = discoveredIntent;
        }

        var identityState = GetOrAddIdentityState(appState, identityId);
        identityState.HasAppKey = true;

        var currentRank = GetStatusRank(identityState.InstallStatus);
        var candidateRank = GetStatusRank(status);
        var shouldReplace = candidateRank > currentRank ||
                            (candidateRank == currentRank &&
                             !string.IsNullOrWhiteSpace(resultCode) &&
                             string.IsNullOrWhiteSpace(identityState.ResultCode));

        if (shouldReplace)
        {
            identityState.InstallStatus = status;
            identityState.Source = "StatusServiceReports";
            identityState.Details = detailText;
        }
        else if (identityState.Source.Equals("Registry Win32Apps", StringComparison.OrdinalIgnoreCase))
        {
            identityState.Source = "Registry Win32Apps + StatusServiceReports";
        }

        if (!string.IsNullOrWhiteSpace(resultCode))
        {
            identityState.ResultCode = resultCode;
        }

        if (lastUpdated.HasValue && (!identityState.LastUpdated.HasValue || lastUpdated.Value > identityState.LastUpdated.Value))
        {
            identityState.LastUpdated = lastUpdated;
        }
    }

    private static int GetStatusRank(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            return 0;
        }

        return status.Trim().ToLowerInvariant() switch
        {
            "failed" => 60,
            "installed" => 50,
            "partiallyinstalled" => 45,
            "notinstalled" => 40,
            "inprogress" => 30,
            "retrypending" => 20,
            "detected" => 10,
            _ => 0
        };
    }

    private static RegistryAppState GetOrAddRegistryState(IDictionary<string, RegistryAppState> states, string appId)
    {
        if (states.TryGetValue(appId, out var existing))
        {
            return existing;
        }

        var created = new RegistryAppState();
        states[appId] = created;
        return created;
    }

    private static RegistryIdentityState GetOrAddIdentityState(RegistryAppState appState, string identityId)
    {
        if (appState.IdentityStates.TryGetValue(identityId, out var existing))
        {
            return existing;
        }

        var created = new RegistryIdentityState(identityId);
        appState.IdentityStates[identityId] = created;
        return created;
    }

    private static void ApplyRegistryIdentityStateFromKey(RegistryIdentityState identityState, RegistryKey? appKey)
    {
        if (appKey is null)
        {
            identityState.InstallStatus = identityState.HasGrs ? "RetryPending" : "Detected";
            identityState.Source = identityState.HasGrs ? "Registry GRS" : "Registry Win32Apps";
            identityState.Details = identityState.HasGrs ? "GRS entry present." : "App key found in Win32Apps.";
            return;
        }

        var details = new List<string>();
        var statusSignals = new List<string>();
        var resultCode = identityState.ResultCode;
        DateTimeOffset? lastUpdated = identityState.LastUpdated;

        CollectRegistryValuesRecursive(appKey, pathPrefix: string.Empty, details, statusSignals, ref resultCode, ref lastUpdated, depth: 0);

        var detailText = BuildRegistryDetailText(details, identityState.HasGrs);
        var statusSignalText = string.Join(" | ", statusSignals);
        var status = ClassifyInstallStatusFromRegistry(statusSignalText, resultCode, identityState.HasGrs, identityState.HasAppKey);
        identityState.InstallStatus = status;
        identityState.LastUpdated = lastUpdated;
        identityState.ResultCode = resultCode;
        identityState.Source = identityState.HasGrs ? "Registry Win32Apps + GRS" : "Registry Win32Apps";
        identityState.Details = detailText;

        static void CollectRegistryValuesRecursive(
            RegistryKey key,
            string pathPrefix,
            ICollection<string> details,
            ICollection<string> statusSignals,
            ref string resultCode,
            ref DateTimeOffset? lastUpdated,
            int depth)
        {
            if (depth > 2)
            {
                return;
            }

            foreach (var valueName in key.GetValueNames())
            {
                var rawValue = key.GetValue(valueName);
                var serializedValue = SerializeRegistryValue(rawValue);
                if (!string.IsNullOrWhiteSpace(serializedValue))
                {
                    var qualifiedValueName = string.IsNullOrWhiteSpace(pathPrefix)
                        ? valueName
                        : $"{pathPrefix}.{valueName}";
                    details.Add($"{qualifiedValueName}={serializedValue}");
                    statusSignals.Add($"{qualifiedValueName}={serializedValue}");
                    statusSignals.Add(serializedValue);
                }

                if (string.IsNullOrWhiteSpace(resultCode))
                {
                    var resultHintName = string.IsNullOrWhiteSpace(pathPrefix)
                        ? valueName
                        : $"{pathPrefix}.{valueName}";
                    resultCode = ExtractRegistryResultCode(resultHintName, rawValue, serializedValue);
                }

                var candidateTime = ExtractRegistryTimestamp(valueName, rawValue, serializedValue);
                if (candidateTime.HasValue && (!lastUpdated.HasValue || candidateTime.Value > lastUpdated.Value))
                {
                    lastUpdated = candidateTime;
                }
            }

            foreach (var childName in key.GetSubKeyNames())
            {
                using var child = key.OpenSubKey(childName, writable: false);
                if (child is null)
                {
                    continue;
                }

                var childPathPrefix = string.IsNullOrWhiteSpace(pathPrefix)
                    ? childName
                    : $"{pathPrefix}.{childName}";
                CollectRegistryValuesRecursive(child, childPathPrefix, details, statusSignals, ref resultCode, ref lastUpdated, depth + 1);
            }
        }
    }

    private static string SerializeRegistryValue(object? value)
    {
        return value switch
        {
            null => string.Empty,
            byte[] bytes => SerializeRegistryBinary(bytes),
            string[] values => string.Join(", ", values.Where(item => !string.IsNullOrWhiteSpace(item))),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty
        };
    }

    private static string ExtractRegistryIntent(string valueName, object? rawValue, string serializedValue)
    {
        if (!string.IsNullOrWhiteSpace(serializedValue) &&
            TryExtractIntentFromJson(serializedValue, out var fromJson))
        {
            return fromJson;
        }

        if (!string.IsNullOrWhiteSpace(valueName) &&
            valueName.Contains("intent", StringComparison.OrdinalIgnoreCase))
        {
            return NormalizePolicyIntent(serializedValue);
        }

        if (rawValue is int intValue)
        {
            return NormalizePolicyIntent(intValue.ToString(CultureInfo.InvariantCulture));
        }

        if (rawValue is uint uintValue && uintValue <= int.MaxValue)
        {
            return NormalizePolicyIntent(((int)uintValue).ToString(CultureInfo.InvariantCulture));
        }

        return string.Empty;
    }

    private static bool TryExtractIntentFromJson(string input, out string intent)
    {
        intent = string.Empty;
        var trimmed = input.Trim();
        if ((!trimmed.StartsWith("{", StringComparison.Ordinal) && !trimmed.StartsWith("[", StringComparison.Ordinal)) ||
            (!trimmed.EndsWith("}", StringComparison.Ordinal) && !trimmed.EndsWith("]", StringComparison.Ordinal)))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(trimmed);
            return TryFindIntentInJson(document.RootElement, out intent);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryFindIntentInJson(JsonElement element, out string intent)
    {
        intent = string.Empty;
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (PolicyIntentPropertyNames.Contains(property.Name))
                    {
                        var normalized = NormalizePolicyIntent(ExtractJsonScalar(property.Value));
                        if (IsMeaningfulIntent(normalized))
                        {
                            intent = normalized;
                            return true;
                        }
                    }

                    if (TryFindIntentInJson(property.Value, out intent))
                    {
                        return true;
                    }
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (TryFindIntentInJson(item, out intent))
                    {
                        return true;
                    }
                }

                break;
        }

        return false;
    }

    private static string SerializeRegistryBinary(byte[] bytes)
    {
        if (bytes.Length == 0)
        {
            return string.Empty;
        }

        if (TryDecodeRegistryText(bytes, Encoding.UTF8, out var utf8) && LooksLikeStructuredRegistrySignal(utf8))
        {
            return utf8;
        }

        if (TryDecodeRegistryText(bytes, Encoding.Unicode, out var utf16Le) && LooksLikeStructuredRegistrySignal(utf16Le))
        {
            return utf16Le;
        }

        if (TryDecodeRegistryText(bytes, Encoding.BigEndianUnicode, out var utf16Be) && LooksLikeStructuredRegistrySignal(utf16Be))
        {
            return utf16Be;
        }

        return Convert.ToHexString(bytes);
    }

    private static bool TryDecodeRegistryText(byte[] bytes, Encoding encoding, out string decoded)
    {
        decoded = string.Empty;

        try
        {
            var text = encoding.GetString(bytes);
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            text = text.Replace("\0", string.Empty, StringComparison.Ordinal).Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            decoded = text;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool LooksLikeStructuredRegistrySignal(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.Trim();
        return trimmed.StartsWith("{", StringComparison.Ordinal) ||
               trimmed.StartsWith("[", StringComparison.Ordinal) ||
               trimmed.Contains("=", StringComparison.Ordinal) ||
               trimmed.Contains(":", StringComparison.Ordinal);
    }

    private static string ExtractRegistryResultCode(string valueName, object? rawValue, string serializedValue)
    {
        var hintName = valueName?.ToLowerInvariant() ?? string.Empty;
        var hasHint = hintName.Contains("error", StringComparison.Ordinal) ||
                      hintName.Contains("result", StringComparison.Ordinal) ||
                      hintName.Contains("return", StringComparison.Ordinal) ||
                      hintName.Contains("exit", StringComparison.Ordinal) ||
                      hintName.Contains("code", StringComparison.Ordinal);

        var resolvedHex = ResolveHex(serializedValue);
        if (!string.IsNullOrWhiteSpace(resolvedHex))
        {
            return resolvedHex;
        }

        var jsonMatch = JsonErrorCodeRegex().Match(serializedValue);
        if (jsonMatch.Success)
        {
            var jsonHex = jsonMatch.Groups["hex"].Value;
            if (!string.IsNullOrWhiteSpace(jsonHex))
            {
                return NormalizeHexLikeCode(jsonHex);
            }

            if (int.TryParse(jsonMatch.Groups["num"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var jsonNum))
            {
                return $"0x{unchecked((uint)jsonNum):X8}";
            }
        }

        if (rawValue is int intValue && hasHint)
        {
            return $"0x{unchecked((uint)intValue):X8}";
        }

        if (rawValue is uint uintValue && hasHint)
        {
            return $"0x{uintValue:X8}";
        }

        if (rawValue is long longValue && hasHint)
        {
            return $"0x{unchecked((uint)longValue):X8}";
        }

        if (hasHint && int.TryParse(serializedValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return $"0x{unchecked((uint)parsed):X8}";
        }

        return string.Empty;
    }

    private static string NormalizeHexLikeCode(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var trimmed = value.Trim();
        if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[2..];
        }

        if (!uint.TryParse(trimmed, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var parsed))
        {
            return string.Empty;
        }

        return $"0x{parsed:X8}";
    }

    private static DateTimeOffset? ExtractRegistryTimestamp(string valueName, object? rawValue, string serializedValue)
    {
        var lowerName = valueName?.ToLowerInvariant() ?? string.Empty;
        if (!lowerName.Contains("time", StringComparison.Ordinal) &&
            !lowerName.Contains("date", StringComparison.Ordinal) &&
            !lowerName.Contains("updated", StringComparison.Ordinal) &&
            !lowerName.Contains("modified", StringComparison.Ordinal))
        {
            return null;
        }

        if (rawValue is long longValue)
        {
            var fromNumber = ParseRegistryTimestampFromNumber(longValue);
            if (fromNumber.HasValue)
            {
                return fromNumber;
            }
        }

        if (rawValue is int intValue)
        {
            var fromNumber = ParseRegistryTimestampFromNumber(intValue);
            if (fromNumber.HasValue)
            {
                return fromNumber;
            }
        }

        return ParseTimestampFlexible(serializedValue);
    }

    private static DateTimeOffset? ParseRegistryTimestampFromNumber(long value)
    {
        try
        {
            if (value > 100_000_000_000_000)
            {
                return DateTimeOffset.FromFileTime(value);
            }
        }
        catch
        {
            // Ignore non-filetime values.
        }

        if (value > 10_000_000_000)
        {
            try
            {
                return DateTimeOffset.FromUnixTimeMilliseconds(value);
            }
            catch
            {
                return null;
            }
        }

        if (value > 1_000_000_000)
        {
            try
            {
                return DateTimeOffset.FromUnixTimeSeconds(value);
            }
            catch
            {
                return null;
            }
        }

        return null;
    }

    private static string BuildRegistryDetailText(IReadOnlyList<string> details, bool hasGrs)
    {
        if (details.Count == 0)
        {
            return hasGrs ? "GRS entry present." : "No status values found in app key.";
        }

        var visible = details.Take(8).ToArray();
        var text = string.Join(" | ", visible);
        if (details.Count > visible.Length)
        {
            text = $"{text} | ...";
        }

        if (hasGrs && !text.Contains("GRS", StringComparison.OrdinalIgnoreCase))
        {
            text = $"{text} | GRS entry present.";
        }

        return text;
    }

    private static string ClassifyInstallStatusFromRegistry(string signalText, string resultCode, bool hasGrs, bool hasAppKey)
    {
        var normalized = signalText.ToLowerInvariant();
        var compact = normalized
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace("\t", string.Empty, StringComparison.Ordinal)
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Replace("\n", string.Empty, StringComparison.Ordinal)
            .Replace("\\\"", "\"", StringComparison.Ordinal);
        var unquoted = compact.Replace("\"", string.Empty, StringComparison.Ordinal);
        static bool HasJsonNumericState(string source, string key, int value) =>
            source.Contains($"\"{key}\":{value},", StringComparison.Ordinal) ||
            source.Contains($"\"{key}\":{value}}}", StringComparison.Ordinal) ||
            source.Contains($"\"{key}\":{value}]", StringComparison.Ordinal);
        static bool HasKeyValueNumericState(string source, string key, int value) =>
            Regex.IsMatch(source, $@"(?<![a-z0-9]){Regex.Escape(key)}={value}(?!\d)", RegexOptions.IgnoreCase);
        var hasExplicitNotInstalledSignal =
            normalized.Contains("not installed", StringComparison.Ordinal) ||
            normalized.Contains("not detected", StringComparison.Ordinal) ||
            normalized.Contains("uninstall", StringComparison.Ordinal);
        var hasExplicitFailureSignal =
            normalized.Contains("failed", StringComparison.Ordinal) ||
            normalized.Contains("failure", StringComparison.Ordinal) ||
            normalized.Contains("fatal", StringComparison.Ordinal) ||
            normalized.Contains("0x8", StringComparison.Ordinal) ||
            normalized.Contains("noncompliant", StringComparison.Ordinal);
        var hasJsonDetectedTrue =
            compact.Contains("\"applicationdetected\":true", StringComparison.Ordinal) ||
            compact.Contains("\"applicationdetected\":\"true\"", StringComparison.Ordinal) ||
            compact.Contains("\"applicationdetected\":\"installed\"", StringComparison.Ordinal) ||
            compact.Contains("\"applicationdetected\":\"detected\"", StringComparison.Ordinal) ||
            compact.Contains("\"isdetected\":true", StringComparison.Ordinal) ||
            compact.Contains("\"isdetected\":\"true\"", StringComparison.Ordinal) ||
            compact.Contains("\"detected\":\"true\"", StringComparison.Ordinal) ||
            compact.Contains("\"detected\":true", StringComparison.Ordinal);
        var hasJsonDetectedFalse =
            compact.Contains("\"applicationdetected\":false", StringComparison.Ordinal) ||
            compact.Contains("\"applicationdetected\":\"false\"", StringComparison.Ordinal) ||
            compact.Contains("\"applicationdetected\":\"notdetected\"", StringComparison.Ordinal) ||
            compact.Contains("\"applicationdetected\":\"notinstalled\"", StringComparison.Ordinal) ||
            compact.Contains("\"isdetected\":false", StringComparison.Ordinal) ||
            compact.Contains("\"isdetected\":\"false\"", StringComparison.Ordinal) ||
            compact.Contains("\"detected\":\"false\"", StringComparison.Ordinal) ||
            compact.Contains("\"detected\":false", StringComparison.Ordinal);
        var hasJsonComplianceInstalled =
            compact.Contains("\"iscompliant\":true", StringComparison.Ordinal) ||
            compact.Contains("\"compliant\":true", StringComparison.Ordinal) ||
            HasJsonNumericState(compact, "compliancestate", 1) ||
            compact.Contains("\"compliancestate\":\"compliant\"", StringComparison.Ordinal);
        var hasJsonComplianceNotInstalled =
            compact.Contains("\"iscompliant\":false", StringComparison.Ordinal) ||
            compact.Contains("\"compliant\":false", StringComparison.Ordinal) ||
            HasJsonNumericState(compact, "compliancestate", 2) ||
            HasJsonNumericState(compact, "compliancestate", 0) ||
            compact.Contains("\"compliancestate\":\"noncompliant\"", StringComparison.Ordinal);
        var hasJsonComplianceError =
            HasJsonNumericState(compact, "compliancestate", 4) ||
            compact.Contains("\"compliancestate\":\"error\"", StringComparison.Ordinal);
        var hasJsonComplianceCleanup =
            HasJsonNumericState(compact, "compliancestate", 100) ||
            compact.Contains("\"compliancestate\":\"cleanup\"", StringComparison.Ordinal);
        var hasJsonInstallStateInstalled =
            compact.Contains("\"installstate\":\"installed\"", StringComparison.Ordinal) ||
            compact.Contains("\"installstate\":1", StringComparison.Ordinal);
        var hasJsonDetectionStateInstalled =
            HasJsonNumericState(compact, "detectionstate", 1) ||
            compact.Contains("\"detectionstate\":\"detected\"", StringComparison.Ordinal) ||
            compact.Contains("\"detectionstate\":\"installed\"", StringComparison.Ordinal);
        var hasJsonDetectionStateNotInstalled =
            HasJsonNumericState(compact, "detectionstate", 0) ||
            HasJsonNumericState(compact, "detectionstate", 2) ||
            compact.Contains("\"detectionstate\":\"notdetected\"", StringComparison.Ordinal) ||
            compact.Contains("\"detectionstate\":\"notinstalled\"", StringComparison.Ordinal);
        var hasKvDetectedTrue =
            unquoted.Contains("applicationdetected=true", StringComparison.Ordinal) ||
            unquoted.Contains("applicationdetected=1", StringComparison.Ordinal) ||
            unquoted.Contains("applicationdetected=installed", StringComparison.Ordinal) ||
            unquoted.Contains("applicationdetected=detected", StringComparison.Ordinal) ||
            unquoted.Contains("isdetected=true", StringComparison.Ordinal) ||
            unquoted.Contains("isdetected=1", StringComparison.Ordinal) ||
            unquoted.Contains("detected=true", StringComparison.Ordinal) ||
            unquoted.Contains("detected=1", StringComparison.Ordinal);
        var hasKvDetectedFalse =
            unquoted.Contains("applicationdetected=false", StringComparison.Ordinal) ||
            unquoted.Contains("applicationdetected=0", StringComparison.Ordinal) ||
            unquoted.Contains("applicationdetected=notdetected", StringComparison.Ordinal) ||
            unquoted.Contains("applicationdetected=notinstalled", StringComparison.Ordinal) ||
            unquoted.Contains("isdetected=false", StringComparison.Ordinal) ||
            unquoted.Contains("isdetected=0", StringComparison.Ordinal) ||
            unquoted.Contains("detected=false", StringComparison.Ordinal) ||
            unquoted.Contains("detected=0", StringComparison.Ordinal);
        var hasKvDetectionStateInstalled =
            HasKeyValueNumericState(unquoted, "detectionstate", 1) ||
            unquoted.Contains("detectionstate=installed", StringComparison.Ordinal) ||
            unquoted.Contains("detectionstate=detected", StringComparison.Ordinal);
        var hasKvDetectionStateNotInstalled =
            HasKeyValueNumericState(unquoted, "detectionstate", 0) ||
            HasKeyValueNumericState(unquoted, "detectionstate", 2) ||
            unquoted.Contains("detectionstate=notinstalled", StringComparison.Ordinal) ||
            unquoted.Contains("detectionstate=notdetected", StringComparison.Ordinal);
        var hasKvComplianceInstalled =
            HasKeyValueNumericState(unquoted, "compliancestate", 1) ||
            unquoted.Contains("compliancestate=compliant", StringComparison.Ordinal) ||
            unquoted.Contains("iscompliant=true", StringComparison.Ordinal);
        var hasKvComplianceNotInstalled =
            HasKeyValueNumericState(unquoted, "compliancestate", 2) ||
            HasKeyValueNumericState(unquoted, "compliancestate", 0) ||
            unquoted.Contains("compliancestate=noncompliant", StringComparison.Ordinal) ||
            unquoted.Contains("iscompliant=false", StringComparison.Ordinal);
        var hasKvComplianceError =
            HasKeyValueNumericState(unquoted, "compliancestate", 4) ||
            unquoted.Contains("compliancestate=error", StringComparison.Ordinal);
        var hasKvComplianceCleanup =
            HasKeyValueNumericState(unquoted, "compliancestate", 100) ||
            unquoted.Contains("compliancestate=cleanup", StringComparison.Ordinal);
        var hasKvInstallStateInstalled =
            unquoted.Contains("installstate=installed", StringComparison.Ordinal) ||
            HasKeyValueNumericState(unquoted, "installstate", 1);
        var hasKvInstallStateNotInstalled =
            unquoted.Contains("installstate=notinstalled", StringComparison.Ordinal) ||
            HasKeyValueNumericState(unquoted, "installstate", 0) ||
            HasKeyValueNumericState(unquoted, "status", 60) ||
            HasKeyValueNumericState(unquoted, "enforcementstate", 60);
        var hasStatus2Installed =
            unquoted.Contains("status2=installed", StringComparison.Ordinal) ||
            unquoted.Contains("status2=installedpendingreboot", StringComparison.Ordinal) ||
            unquoted.Contains("status2=installedbutdependenciesnotpresent", StringComparison.Ordinal);
        var hasStatus2NotInstalled =
            unquoted.Contains("status2=notinstalled", StringComparison.Ordinal) ||
            unquoted.Contains("status2=notapplicable", StringComparison.Ordinal) ||
            unquoted.Contains("status2=uninstalledbygateway", StringComparison.Ordinal);
        var hasStatus2InProgress =
            unquoted.Contains("status2=installing", StringComparison.Ordinal) ||
            unquoted.Contains("status2=installingpendingreboot", StringComparison.Ordinal) ||
            unquoted.Contains("status2=uninstalling", StringComparison.Ordinal);
        var hasStatus2Failed =
            unquoted.Contains("status2=failed", StringComparison.Ordinal) ||
            unquoted.Contains("status2=uninstallfailed", StringComparison.Ordinal);
        var hasEnforcementInProgress =
            HasKeyValueNumericState(unquoted, "enforcementstate", 2000) ||
            HasKeyValueNumericState(unquoted, "enforcementstate", 2007) ||
            HasKeyValueNumericState(unquoted, "enforcementstate", 2008) ||
            HasKeyValueNumericState(unquoted, "enforcementstate", 2009) ||
            HasKeyValueNumericState(unquoted, "enforcementstate", 2010) ||
            HasKeyValueNumericState(unquoted, "enforcementstate", 2011) ||
            HasKeyValueNumericState(unquoted, "enforcementstate", 2012) ||
            HasKeyValueNumericState(unquoted, "enforcementstate", 5999) ||
            HasKeyValueNumericState(unquoted, "enforcementstate", 6002) ||
            HasKeyValueNumericState(unquoted, "enforcementstate", 6010) ||
            unquoted.Contains("enforcementstate=inprogress", StringComparison.Ordinal);
        var hasEnforcementSuccess =
            HasKeyValueNumericState(unquoted, "enforcementstate", 1000) ||
            HasKeyValueNumericState(unquoted, "enforcementstate", 1004) ||
            HasKeyValueNumericState(unquoted, "enforcementstate", 1005) ||
            HasKeyValueNumericState(unquoted, "enforcementstate", 1006) ||
            HasKeyValueNumericState(unquoted, "enforcementstate", 1007) ||
            HasKeyValueNumericState(unquoted, "enforcementstate", 1016) ||
            HasKeyValueNumericState(unquoted, "enforcementstate", 1017) ||
            unquoted.Contains("enforcementstate=success", StringComparison.Ordinal);
        var hasEnforcementFailed =
            HasKeyValueNumericState(unquoted, "enforcementstate", 5000) ||
            HasKeyValueNumericState(unquoted, "enforcementstate", 5003) ||
            HasKeyValueNumericState(unquoted, "enforcementstate", 5006) ||
            HasKeyValueNumericState(unquoted, "enforcementstate", 5015) ||
            HasKeyValueNumericState(unquoted, "enforcementstate", 6000) ||
            HasKeyValueNumericState(unquoted, "enforcementstate", 6001) ||
            HasKeyValueNumericState(unquoted, "enforcementstate", 6004) ||
            HasKeyValueNumericState(unquoted, "enforcementstate", 6005) ||
            HasKeyValueNumericState(unquoted, "enforcementstate", 6007) ||
            HasKeyValueNumericState(unquoted, "enforcementstate", 6008) ||
            HasKeyValueNumericState(unquoted, "enforcementstate", 6009) ||
            HasKeyValueNumericState(unquoted, "enforcementstate", 6012) ||
            HasKeyValueNumericState(unquoted, "enforcementstate", 6013) ||
            unquoted.Contains("enforcementstate=error", StringComparison.Ordinal);
        var hasEnforcementNotInstalled =
            HasKeyValueNumericState(unquoted, "enforcementstate", 3000) ||
            HasKeyValueNumericState(unquoted, "enforcementstate", 6003) ||
            HasKeyValueNumericState(unquoted, "enforcementstate", 6006) ||
            HasKeyValueNumericState(unquoted, "enforcementstate", 6011);
        // StateMessageDesiredState: None=0, NotPresent=1, Present=2, Unknown=3, Available=4
        // Only "Present" is a reliable install-desired signal.
        var hasKvDesiredInstallSignal =
            HasKeyValueNumericState(unquoted, "desiredstate", 2) ||
            unquoted.Contains("desiredstate=present", StringComparison.Ordinal) ||
            unquoted.Contains("required=true", StringComparison.Ordinal) ||
            HasKeyValueNumericState(unquoted, "intent", 3);
        var hasKvDetectedVersionSignal =
            unquoted.Contains("detectedidentityversion=", StringComparison.Ordinal) &&
            !unquoted.Contains("detectedidentityversion=null", StringComparison.Ordinal) &&
            !unquoted.Contains("detectedidentityversion=\"\"", StringComparison.Ordinal);
        var hasStatusServiceSuccess =
            (HasKeyValueNumericState(unquoted, "status", 1000) ||
             HasKeyValueNumericState(unquoted, "status2", 1000) ||
             hasStatus2Installed) &&
            (HasKeyValueNumericState(unquoted, "errorcode", 0) ||
             unquoted.Contains("errorcode=null", StringComparison.Ordinal));
        var hasReportingStateInstalled =
            hasKvDesiredInstallSignal &&
            (hasJsonDetectionStateInstalled || hasKvDetectionStateInstalled || hasKvDetectedTrue || hasKvDetectedVersionSignal || hasStatusServiceSuccess || hasKvInstallStateInstalled || hasEnforcementSuccess);
        var hasComplianceError = hasJsonComplianceError || hasKvComplianceError;
        var hasComplianceCleanup = hasJsonComplianceCleanup || hasKvComplianceCleanup;
        var hasAnyInstalledSignal =
            hasJsonDetectedTrue || hasKvDetectedTrue ||
            hasJsonComplianceInstalled || hasKvComplianceInstalled ||
            hasJsonInstallStateInstalled || hasKvInstallStateInstalled ||
            hasJsonDetectionStateInstalled || hasKvDetectionStateInstalled ||
            hasReportingStateInstalled ||
            hasKvDetectedVersionSignal ||
            hasStatusServiceSuccess ||
            hasStatus2Installed ||
            hasEnforcementSuccess;
        var hasAnyNotInstalledSignal =
            hasJsonDetectedFalse || hasKvDetectedFalse ||
            hasJsonComplianceNotInstalled || hasKvComplianceNotInstalled ||
            hasKvInstallStateNotInstalled || hasJsonDetectionStateNotInstalled || hasKvDetectionStateNotInstalled ||
            hasStatus2NotInstalled ||
            hasEnforcementNotInstalled;

        var hasNonZeroResultCode =
            !string.IsNullOrWhiteSpace(resultCode) &&
            !string.Equals(resultCode, "0x00000000", StringComparison.OrdinalIgnoreCase);

        if (hasExplicitFailureSignal)
        {
            return "Failed";
        }

        if (hasComplianceError)
        {
            return "Failed";
        }

        if (hasStatus2Failed || hasEnforcementFailed)
        {
            return "Failed";
        }

        if (hasAnyInstalledSignal && !hasAnyNotInstalledSignal)
        {
            return "Installed";
        }

        if (hasComplianceCleanup)
        {
            return "InProgress";
        }

        if (hasAnyNotInstalledSignal && !hasAnyInstalledSignal)
        {
            return "NotInstalled";
        }

        if (hasStatus2InProgress || hasEnforcementInProgress)
        {
            return "InProgress";
        }

        if (hasNonZeroResultCode)
        {
            return "Failed";
        }

        // Exit/Error code 0 is a strong success signal in Win32 app reporting paths.
        if (hasAppKey &&
            string.Equals(resultCode, "0x00000000", StringComparison.OrdinalIgnoreCase) &&
            !hasExplicitNotInstalledSignal)
        {
            return "Installed";
        }

        if (normalized.Contains("installed", StringComparison.Ordinal) ||
            normalized.Contains("succeeded", StringComparison.Ordinal) ||
            normalized.Contains("success", StringComparison.Ordinal) ||
            normalized.Contains("compliant", StringComparison.Ordinal))
        {
            return "Installed";
        }

        if (hasExplicitNotInstalledSignal)
        {
            return "NotInstalled";
        }

        if (normalized.Contains("installing", StringComparison.Ordinal) ||
            normalized.Contains("processing", StringComparison.Ordinal) ||
            normalized.Contains("download", StringComparison.Ordinal) ||
            normalized.Contains("pending", StringComparison.Ordinal) ||
            normalized.Contains("retry", StringComparison.Ordinal) ||
            normalized.Contains("queued", StringComparison.Ordinal))
        {
            return "InProgress";
        }

        if (hasGrs)
        {
            return hasAppKey ? "Detected" : "RetryPending";
        }

        return "Detected";
    }

    private static string NormalizeIdentityId(string identityName)
    {
        if (Guid.TryParse(identityName, out var guid))
        {
            return guid.ToString("D");
        }

        return identityName?.Trim() ?? string.Empty;
    }

    private static bool IsSystemIdentity(string identityId) =>
        string.Equals(identityId, SystemIdentityId, StringComparison.OrdinalIgnoreCase);

    private static string ExtractAppIdFromWin32AppKeyName(string keyName)
    {
        if (string.IsNullOrWhiteSpace(keyName))
        {
            return string.Empty;
        }

        if (GuidStrictRegex().IsMatch(keyName))
        {
            return NormalizeGuidId(keyName);
        }

        var match = GuidWithOptionalSuffixRegex().Match(keyName);
        return match.Success ? NormalizeGuidId(match.Groups["id"].Value) : string.Empty;
    }

    private static RegistryKey? OpenStatusServiceReportsRoot(string host)
    {
        const string subKeyPath = @"SOFTWARE\Microsoft\IntuneManagementExtension\SideCarPolicies\StatusServiceReports";
        return OpenIntuneSubKey(host, subKeyPath);
    }

    private static RegistryKey? OpenWin32AppsReportingRoot(string host)
    {
        const string subKeyPath = @"SOFTWARE\Microsoft\IntuneManagementExtension\Win32Apps\Reporting";
        return OpenIntuneSubKey(host, subKeyPath);
    }

    private static RegistryKey? OpenWin32AppsRoot(string host)
    {
        const string subKeyPath = @"SOFTWARE\Microsoft\IntuneManagementExtension\Win32Apps";
        return OpenIntuneSubKey(host, subKeyPath);
    }

    private static RegistryKey? OpenIntuneSubKey(string host, string subKeyPath)
    {
        if (IsLocalHost(host))
        {
            try
            {
                var localDefault = Registry.LocalMachine.OpenSubKey(subKeyPath, writable: false);
                if (localDefault is not null)
                {
                    return localDefault;
                }
            }
            catch
            {
                // Fall through to explicit view probes.
            }

            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    var localBase = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                    var key = localBase.OpenSubKey(subKeyPath, writable: false);
                    if (key is not null)
                    {
                        return key;
                    }
                }
                catch
                {
                    // Best effort only.
                }
            }

            return null;
        }

        try
        {
            var remoteDefault = RegistryKey.OpenRemoteBaseKey(RegistryHive.LocalMachine, host);
            var defaultKey = remoteDefault.OpenSubKey(subKeyPath, writable: false);
            if (defaultKey is not null)
            {
                return defaultKey;
            }
        }
        catch
        {
            // Fall through to explicit views.
        }

        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                var remoteBase = RegistryKey.OpenRemoteBaseKey(RegistryHive.LocalMachine, host, view);
                var key = remoteBase.OpenSubKey(subKeyPath, writable: false);
                if (key is not null)
                {
                    return key;
                }
            }
            catch
            {
                // Best effort only.
            }
        }

        return null;
    }

    private async ValueTask<IntunePolicyResultReport> BuildIntunePolicyResultReportAsync(
        string host,
        MdmReportParseResult report,
        string outputDirectory,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var totalTimer = Stopwatch.StartNew();
        var gpResultHtmlPath = FindGpResultHtmlPath(report.ReportDirectory);
        var gpResultXmlPath = FindGpResultXmlPath(report.ReportDirectory);
        var overlayWarnings = new List<string>();
        var gpResultXmlWarnings = new List<string>();
        var gpResultHtmlWarnings = new List<string>();
        var mdmWarnings = new List<string>();
        var warnings = new List<string>();
        var timings = new List<string>();

        async Task<(T Value, string Timing)> MeasureAsync<T>(string operationName, Func<Task<T>> action)
        {
            var timer = Stopwatch.StartNew();
            var value = await action();
            return (value, $"{operationName} completed in {timer.ElapsedMilliseconds} ms.");
        }

        var overlayTask = MeasureAsync(
            "Local policy overlay collection",
            () => CollectLocalPolicyOverlayEntriesAsync(host, overlayWarnings, cancellationToken).AsTask());
        var gpResultXmlTask = MeasureAsync(
            "gpresult XML extraction",
            () => Task.Run(
                () => string.IsNullOrWhiteSpace(gpResultXmlPath)
                    ? []
                    : TryExtractPolicyEntriesFromGpResultXml(gpResultXmlPath, gpResultXmlWarnings),
                cancellationToken));
        var gpResultHtmlTask = MeasureAsync(
            "gpresult HTML extraction",
            () => Task.Run(
                () => string.IsNullOrWhiteSpace(gpResultHtmlPath)
                    ? []
                    : TryExtractPolicyEntriesFromGpResultHtml(gpResultHtmlPath, gpResultHtmlWarnings),
                cancellationToken));

        var (overlayResult, overlayTiming) = await overlayTask;
        timings.Add(overlayTiming);
        var (overlayEntries, providerLookup) = overlayResult;
        var overlayEntryCountBeforeGpResultMerge = overlayEntries.Count;

        var mdmTask = MeasureAsync(
            "MDM policy extraction",
            () => Task.Run(
                () => ExtractPolicyEntries(report.XmlPath, report.HtmlPath, mdmWarnings, providerLookup),
                cancellationToken));

        var (gpResultXmlResult, gpResultXmlTiming) = await gpResultXmlTask;
        timings.Add(gpResultXmlTiming);
        var gpResultEntries = gpResultXmlResult;

        var (gpResultHtmlResult, gpResultHtmlTiming) = await gpResultHtmlTask;
        timings.Add(gpResultHtmlTiming);
        var gpResultHtmlEntries = gpResultHtmlResult;

        var (mdmResult, mdmTiming) = await mdmTask;
        timings.Add(mdmTiming);
        var (mdmEntries, source) = mdmResult;

        warnings.AddRange(overlayWarnings);
        warnings.AddRange(mdmWarnings);
        warnings.AddRange(gpResultXmlWarnings);
        warnings.AddRange(gpResultHtmlWarnings);

        var gpResultEntryCount = gpResultEntries.Count;
        if (!string.IsNullOrWhiteSpace(gpResultXmlPath))
        {
            if (gpResultEntries.Count > 0)
            {
                overlayEntries = DeduplicatePolicyEntries(overlayEntries.Concat(gpResultEntries));
                warnings.Add($"Merged {gpResultEntries.Count} gpresult XML entr{(gpResultEntries.Count == 1 ? "y" : "ies")} from '{gpResultXmlPath}'.");
            }
            else
            {
                warnings.Add($"Found gpresult.xml at '{gpResultXmlPath}', but no mergeable GPO entries were extracted.");
            }
        }
        else
        {
            warnings.Add($"No gpresult.xml found in report directory '{report.ReportDirectory}'.");
        }

        var gpResultHtmlEntryCount = gpResultHtmlEntries.Count;
        if (!string.IsNullOrWhiteSpace(gpResultHtmlPath))
        {
            if (gpResultHtmlEntries.Count > 0)
            {
                overlayEntries = MergeGpResultHtmlEntries(overlayEntries, gpResultHtmlEntries);
                warnings.Add($"Merged {gpResultHtmlEntries.Count} gpresult HTML entr{(gpResultHtmlEntries.Count == 1 ? "y" : "ies")} from '{gpResultHtmlPath}'.");
            }
            else
            {
                warnings.Add($"Found gpresult.html at '{gpResultHtmlPath}', but no mergeable GPO entries were extracted.");
            }
        }
        else
        {
            warnings.Add($"No gpresult.html found in report directory '{report.ReportDirectory}'.");
        }

        var mergeTimer = Stopwatch.StartNew();
        var entries = MergePolicyEntriesWithConflictAnalysis(mdmEntries, overlayEntries);
        timings.Add($"Policy merge completed in {mergeTimer.ElapsedMilliseconds} ms.");
        warnings.Add($"Extraction summary: MDM={mdmEntries.Count}, LocalOverlay={overlayEntryCountBeforeGpResultMerge}, GpResultXml={gpResultEntryCount}, GpResultHtml={gpResultHtmlEntryCount}, CombinedOverlay={overlayEntries.Count}.");
        warnings.Add("Entry sources: " + BuildPolicySourceSummary(entries) + ".");
        if (overlayEntries.Count > 0)
        {
            source = $"{source}+LocalPolicyOverlay";
        }
        else
        {
            warnings.Add("No local policy overlay entries were extracted. If GPO settings are expected, run elevated and verify gpresult access on the target host.");
        }

        timings.Add($"Policy report total completed in {totalTimer.ElapsedMilliseconds} ms.");
        var summary = BuildPolicyResultSummary(entries);
        var generatedAt = DateTimeOffset.UtcNow;
        var targetDirectory = string.IsNullOrWhiteSpace(outputDirectory)
            ? Path.Combine(AppContext.BaseDirectory, "logs", "intune-agent", "policy-result")
            : outputDirectory;

        Directory.CreateDirectory(targetDirectory);
        var stamp = generatedAt.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
        var exportHtmlPath = Path.Combine(targetDirectory, $"intune-policy-result-{stamp}.html");
        var exportJsonPath = Path.Combine(targetDirectory, $"intune-policy-result-{stamp}.json");

        var reportResult = new IntunePolicyResultReport(
            host,
            generatedAt,
            report.ReportDirectory,
            report.XmlPath,
            report.HtmlPath,
            source,
            summary,
            entries,
            exportHtmlPath,
            exportJsonPath,
            warnings,
            timings);

        var htmlContent = BuildPolicyResultHtml(reportResult);
        var jsonOptions = new JsonSerializerOptions(JsonOptions) { WriteIndented = true };
        var jsonContent = JsonSerializer.Serialize(reportResult, jsonOptions);
        await Task.WhenAll(
            File.WriteAllTextAsync(exportHtmlPath, htmlContent, new UTF8Encoding(false), cancellationToken),
            File.WriteAllTextAsync(exportJsonPath, jsonContent, new UTF8Encoding(false), cancellationToken));
        CleanupStalePolicyResultArtifacts(targetDirectory, exportHtmlPath, exportJsonPath);
        return reportResult;
    }

    private static string BuildImeLogFingerprint(IReadOnlyList<string> files)
    {
        return string.Join(
            ';',
            files.Select(path =>
            {
                var info = new FileInfo(path);
                return $"{info.Name}|{info.LastWriteTimeUtc.Ticks}|{info.Length}";
            }));
    }

    private static IReadOnlyList<ImeLogTimelineEntry> BuildImeTimelineEntries(IReadOnlyList<ImeTimelinePayload> payloads)
    {
        return payloads
            .Select(payload =>
            {
                var message = payload.Message ?? string.Empty;
                var classification = ClassifyImeTimelineEntry(payload.Component ?? string.Empty, message, payload.IsPolicyPayload);
                return new ImeLogTimelineEntry(
                    ParseTimestampFlexible(payload.TimeCreated),
                    string.IsNullOrWhiteSpace(payload.Severity) ? "Information" : payload.Severity!,
                    classification.DisplayComponent,
                    message,
                    payload.SourceFile ?? string.Empty,
                    payload.LineNumber,
                    payload.RawLine ?? string.Empty,
                    payload.IsPolicyPayload,
                    payload.PolicyJson ?? string.Empty,
                    classification.Flow,
                    classification.Phase,
                    classification.Effect,
                    classification.CorrelationSummary,
                    classification.EntityType,
                    classification.EntityId,
                    classification.PolicyId,
                    classification.SessionId,
                    classification.UserId,
                    classification.ResultCode);
            })
            .ToArray();
    }

    private static string BuildPolicySourceSummary(IReadOnlyList<IntunePolicyResultEntry> entries)
    {
        if (entries.Count == 0)
        {
            return "none";
        }

        return string.Join(
            ", ",
            entries
                .GroupBy(
                    entry => NormalizePolicySource(string.IsNullOrWhiteSpace(entry.WinningSource) ? entry.Source : entry.WinningSource),
                    StringComparer.OrdinalIgnoreCase)
                .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
                .Select(group => $"{group.Key}={group.Count()}"));
    }

    private static void CleanupStalePolicyResultArtifacts(string directory, string latestHtmlPath, string latestJsonPath)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return;
        }

        foreach (var pattern in new[] { "intune-policy-result-*.html", "intune-policy-result-*.json" })
        {
            foreach (var path in Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly))
            {
                if (path.Equals(latestHtmlPath, StringComparison.OrdinalIgnoreCase) ||
                    path.Equals(latestJsonPath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    File.Delete(path);
                }
                catch
                {
                    // Best effort cleanup only.
                }
            }
        }
    }

    private static string? FindGpResultXmlPath(string reportDirectory)
    {
        if (string.IsNullOrWhiteSpace(reportDirectory))
        {
            return null;
        }

        var candidatePaths = new[]
        {
            Path.Combine(reportDirectory, "gpresult.xml"),
            Path.Combine(reportDirectory, "GPResult.xml")
        };

        return candidatePaths.FirstOrDefault(File.Exists);
    }

    private static string? FindGpResultHtmlPath(string reportDirectory)
    {
        if (string.IsNullOrWhiteSpace(reportDirectory))
        {
            return null;
        }

        var candidatePaths = new[]
        {
            Path.Combine(reportDirectory, "gpresult.html"),
            Path.Combine(reportDirectory, "GPResult.html")
        };

        return candidatePaths.FirstOrDefault(File.Exists);
    }

    private async ValueTask<(IReadOnlyList<IntunePolicyResultEntry> Entries, IReadOnlyDictionary<string, string> ProviderLookup)> CollectLocalPolicyOverlayEntriesAsync(
        string host,
        ICollection<string> warnings,
        CancellationToken cancellationToken)
    {
        const string script = """
$entries = New-Object System.Collections.Generic.List[object];
$providers = New-Object System.Collections.Generic.List[object];
$rsopGpoSourceLookup = @{};
$script:gpResultXmlDocument = $null;
$script:gpResultXmlNamespaces = $null;
$skipNames = @('PSPath','PSParentPath','PSChildName','PSDrive','PSProvider');

function Convert-PolicyValue {
  param([object]$Value)
  if ($null -eq $Value) { return '' }
  if ($Value -is [byte[]]) { return ([BitConverter]::ToString($Value) -replace '-', '') }
  if ($Value -is [array]) {
    $parts = @();
    foreach ($item in $Value) {
      if ($null -eq $item) { continue }
      $parts += [string]$item;
    }
    return ($parts -join ', ');
  }

  return [string]$Value;
}

function Resolve-PolicyAreaFromPath {
  param([string]$Path)

  if ([string]::IsNullOrWhiteSpace($Path)) { return 'General' }
  $segments = @($Path -split '\\' | Where-Object { -not [string]::IsNullOrWhiteSpace($_) });
  if ($segments.Count -eq 0) { return 'General' }

  $policiesIndex = -1;
  for ($i = 0; $i -lt $segments.Count; $i++) {
    if ($segments[$i].Equals('Policies', [System.StringComparison]::OrdinalIgnoreCase)) {
      $policiesIndex = $i;
      break;
    }
  }

  if ($policiesIndex -ge 0) {
    $candidateIndex = $policiesIndex + 1;
    if ($candidateIndex -lt $segments.Count) {
      $candidate = $segments[$candidateIndex];
      if ($candidate.Equals('Microsoft', [System.StringComparison]::OrdinalIgnoreCase) -and ($candidateIndex + 1) -lt $segments.Count) {
        return $segments[$candidateIndex + 1];
      }

      return $candidate;
    }
  }

  return $segments[$segments.Count - 1];
}

function Resolve-ScopePolicySource {
  param([string]$Scope)
  $namespace = if ($Scope -eq 'Device') { 'root\rsop\computer' } else { 'root\rsop\user' };
  try {
    $gpos = @(Get-CimInstance -Namespace $namespace -ClassName RSOP_GPO -ErrorAction Stop);
    if ($gpos.Count -eq 0) { return 'RegistryPolicy' }

    $hasLocal = $false;
    $hasDomain = $false;
    foreach ($gpo in $gpos) {
      $name = [string]$gpo.Name;
      if ([string]::IsNullOrWhiteSpace($name)) { continue }
      $normalized = $name.ToLowerInvariant();
      if ($normalized.Contains('local group policy') -or $normalized.Contains('lokale gruppenrichtlinie')) {
        $hasLocal = $true;
      } else {
        $hasDomain = $true;
      }
    }

    if ($hasDomain) { return 'GroupPolicy' }
    if ($hasLocal) { return 'LocalPolicy' }
  } catch {
    # Best effort only.
  }

  return 'RegistryPolicy';
}

function Resolve-ProviderSource {
  param(
    [string]$ProviderId,
    [string]$ProviderName,
    [string]$RawHint
  )

  $hint = (($ProviderId + ' ' + $ProviderName + ' ' + $RawHint).Trim()).ToLowerInvariant();
  if ([string]::IsNullOrWhiteSpace($hint)) { return 'Unknown' }
  if ($hint -match 'group policy|gpo') { return 'GroupPolicy' }
  if ($hint -match 'local policy') { return 'LocalPolicy' }
  if ($hint -match 'mdm|omadm|intune|enrollment|csp') { return 'Mdm' }
  if ($hint -match 'registry') { return 'RegistryPolicy' }
  return 'Unknown'
}

function Normalize-ProviderId {
  param([string]$ProviderId)
  if ([string]::IsNullOrWhiteSpace($ProviderId)) { return '' }
  $trimmed = $ProviderId.Trim().Trim('{','}');
  $guid = [guid]::Empty;
  if ([guid]::TryParse($trimmed, [ref]$guid)) {
    return $guid.ToString('D').ToUpperInvariant();
  }

  return $trimmed.ToUpperInvariant();
}

function Get-RsopPropertyValue {
  param(
    [object]$Item,
    [string[]]$PropertyNames
  )

  if ($null -eq $Item -or $null -eq $PropertyNames) { return $null }
  foreach ($propertyName in $PropertyNames) {
    if ([string]::IsNullOrWhiteSpace($propertyName)) { continue }
    $property = $Item.PSObject.Properties[$propertyName];
    if ($null -eq $property -or $null -eq $property.Value) { continue }
    return $property.Value;
  }

  return $null;
}

function Convert-RsopByteValue {
  param(
    [byte[]]$Data,
    [int]$ValueType
  )

  if ($null -eq $Data -or $Data.Length -eq 0) { return '' }
  switch ($ValueType) {
    1 { return ([Text.Encoding]::Unicode.GetString($Data)).TrimEnd([char]0) } # REG_SZ
    2 { return ([Text.Encoding]::Unicode.GetString($Data)).TrimEnd([char]0) } # REG_EXPAND_SZ
    4 {
      if ($Data.Length -lt 4) { return '' }
      return ([BitConverter]::ToUInt32($Data, 0)).ToString([System.Globalization.CultureInfo]::InvariantCulture);
    }
    7 {
      $text = ([Text.Encoding]::Unicode.GetString($Data)).TrimEnd([char]0);
      if ([string]::IsNullOrWhiteSpace($text)) { return '' }
      return (($text -split [char]0) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }) -join ', ';
    }
    11 {
      if ($Data.Length -lt 8) { return '' }
      return ([BitConverter]::ToUInt64($Data, 0)).ToString([System.Globalization.CultureInfo]::InvariantCulture);
    }
    default { return ([BitConverter]::ToString($Data) -replace '-', '') }
  }
}

function Normalize-RsopRegistryPath {
  param(
    [string]$Scope,
    [string]$Path
  )

  if ([string]::IsNullOrWhiteSpace($Path)) { return '' }
  $trimmed = $Path.Trim();
  if ($trimmed.StartsWith('HKEY_LOCAL_MACHINE\\', [System.StringComparison]::OrdinalIgnoreCase)) {
    return 'HKLM\\' + $trimmed.Substring('HKEY_LOCAL_MACHINE\\'.Length);
  }
  if ($trimmed.StartsWith('HKEY_CURRENT_USER\\', [System.StringComparison]::OrdinalIgnoreCase)) {
    return 'HKCU\\' + $trimmed.Substring('HKEY_CURRENT_USER\\'.Length);
  }
  if ($trimmed.StartsWith('HKLM\\', [System.StringComparison]::OrdinalIgnoreCase) -or
      $trimmed.StartsWith('HKCU\\', [System.StringComparison]::OrdinalIgnoreCase)) {
    return $trimmed;
  }

  if ($Scope -eq 'Device') {
    if ($trimmed.StartsWith('SYSTEM\\', [System.StringComparison]::OrdinalIgnoreCase) -or
        $trimmed.StartsWith('SOFTWARE\\', [System.StringComparison]::OrdinalIgnoreCase)) {
      return 'HKLM\\' + $trimmed;
    }
  } elseif ($Scope -eq 'User') {
    if ($trimmed.StartsWith('SOFTWARE\\', [System.StringComparison]::OrdinalIgnoreCase)) {
      return 'HKCU\\' + $trimmed;
    }
  }

  return $trimmed;
}

function Get-GpResultXmlContext {
  if ($null -ne $script:gpResultXmlDocument -and $null -ne $script:gpResultXmlNamespaces) {
    return @{
      Document = $script:gpResultXmlDocument;
      Namespaces = $script:gpResultXmlNamespaces
    };
  }

  $tmpPath = Join-Path $env:TEMP ('icc-gpresult-' + [Guid]::NewGuid().ToString('N') + '.xml');
  try {
    & gpresult.exe /Scope Computer /X $tmpPath /F | Out-Null;
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $tmpPath)) {
      return $null;
    }

    $doc = New-Object System.Xml.XmlDocument;
    $doc.Load($tmpPath);
    $ns = New-Object System.Xml.XmlNamespaceManager($doc.NameTable);
    $ns.AddNamespace('r', 'http://www.microsoft.com/GroupPolicy/Rsop');
    $ns.AddNamespace('s', 'http://www.microsoft.com/GroupPolicy/Settings');

    $script:gpResultXmlDocument = $doc;
    $script:gpResultXmlNamespaces = $ns;
    return @{
      Document = $doc;
      Namespaces = $ns
    };
  } catch {
    return $null;
  } finally {
    try {
      if (Test-Path -LiteralPath $tmpPath) {
        Remove-Item -LiteralPath $tmpPath -Force -ErrorAction SilentlyContinue;
      }
    } catch {
      # Best effort cleanup only.
    }
  }
}

function Get-GpResultPropertyValue {
  param(
    [System.Xml.XmlNode]$Node,
    [string[]]$PropertyNames
  )

  if ($null -eq $Node -or $null -eq $PropertyNames) { return '' }
  foreach ($propertyName in $PropertyNames) {
    if ([string]::IsNullOrWhiteSpace($propertyName)) { continue }
    $valueNode = $Node.SelectSingleNode("*[local-name()='PROPERTY' and @NAME='$propertyName']/*[local-name()='VALUE']");
    if ($null -eq $valueNode) { continue }
    $valueText = [string]$valueNode.InnerText;
    if ([string]::IsNullOrWhiteSpace($valueText)) { continue }
    return $valueText.Trim();
  }

  return '';
}

function Get-GpResultNodeText {
  param(
    [System.Xml.XmlNode]$Node,
    [string]$XPath,
    [System.Xml.XmlNamespaceManager]$Namespaces
  )

  if ($null -eq $Node -or [string]::IsNullOrWhiteSpace($XPath)) {
    return '';
  }

  $selected = $Node.SelectSingleNode($XPath, $Namespaces);
  if ($null -eq $selected) {
    return '';
  }

  $value = [string]$selected.InnerText;
  if ([string]::IsNullOrWhiteSpace($value)) {
    return '';
  }

  return $value.Trim();
}

function Resolve-GpoDisplayNameSource {
  param([string]$DisplayName)
  $name = [string]$DisplayName;
  if ([string]::IsNullOrWhiteSpace($name)) { return 'GroupPolicy' }
  $lower = $name.ToLowerInvariant();
  if ($lower.Contains('local group policy') -or
      $lower.Contains('lokale gruppenrichtlinie') -or
      $lower.Contains('richtlinien der lokalen gruppe')) {
    return 'LocalPolicy';
  }

  return 'GroupPolicy';
}

function Normalize-GpResultRegistryPath {
  param(
    [string]$Hive,
    [string]$Key
  )

  $normalizedHive = if ([string]::IsNullOrWhiteSpace($Hive)) {
    ''
  } elseif ($Hive.StartsWith('HKEY_LOCAL_MACHINE', [System.StringComparison]::OrdinalIgnoreCase)) {
    'HKLM'
  } elseif ($Hive.StartsWith('HKEY_CURRENT_USER', [System.StringComparison]::OrdinalIgnoreCase)) {
    'HKCU'
  } elseif ($Hive.StartsWith('HKEY_USERS', [System.StringComparison]::OrdinalIgnoreCase)) {
    'HKU'
  } elseif ($Hive.StartsWith('HKEY_CLASSES_ROOT', [System.StringComparison]::OrdinalIgnoreCase)) {
    'HKCR'
  } else {
    $Hive.Trim();
  };

  $normalizedKey = [string]$Key;
  if (-not [string]::IsNullOrWhiteSpace($normalizedKey)) {
    $normalizedKey = $normalizedKey.Trim().TrimStart('\');
  }

  if ([string]::IsNullOrWhiteSpace($normalizedHive)) { return $normalizedKey }
  if ([string]::IsNullOrWhiteSpace($normalizedKey)) { return $normalizedHive }
  return ($normalizedHive + '\' + $normalizedKey);
}

function Normalize-GpResultKeyPathByScope {
  param(
    [string]$Scope,
    [string]$KeyPath
  )

  if ([string]::IsNullOrWhiteSpace($KeyPath)) { return '' }
  $trimmed = $KeyPath.Trim().TrimStart('\');

  if ($trimmed.StartsWith('HKEY_LOCAL_MACHINE\', [System.StringComparison]::OrdinalIgnoreCase)) {
    return Normalize-GpResultRegistryPath -Hive 'HKEY_LOCAL_MACHINE' -Key $trimmed.Substring('HKEY_LOCAL_MACHINE\'.Length);
  }

  if ($trimmed.StartsWith('HKEY_CURRENT_USER\', [System.StringComparison]::OrdinalIgnoreCase)) {
    return Normalize-GpResultRegistryPath -Hive 'HKEY_CURRENT_USER' -Key $trimmed.Substring('HKEY_CURRENT_USER\'.Length);
  }

  if ($trimmed.StartsWith('HKLM\', [System.StringComparison]::OrdinalIgnoreCase)) {
    return Normalize-GpResultRegistryPath -Hive 'HKLM' -Key $trimmed.Substring('HKLM\'.Length);
  }

  if ($trimmed.StartsWith('HKCU\', [System.StringComparison]::OrdinalIgnoreCase)) {
    return Normalize-GpResultRegistryPath -Hive 'HKCU' -Key $trimmed.Substring('HKCU\'.Length);
  }

  $scopeHive = if ($Scope -eq 'Device') { 'HKLM' } else { 'HKCU' };
  return Normalize-GpResultRegistryPath -Hive $scopeHive -Key $trimmed;
}

function Get-GpResultRegistryValueText {
  param([System.Xml.XmlNode]$ValueNode)

  if ($null -eq $ValueNode) { return '' }
  foreach ($child in $ValueNode.SelectNodes("*[local-name()!='Name']")) {
    if ($null -eq $child) { continue }
    $localName = [string]$child.LocalName;
    if ($localName.Equals('MultiText', [System.StringComparison]::OrdinalIgnoreCase)) {
      $multiParts = @();
      foreach ($textNode in $child.SelectNodes("*[local-name()='Text']")) {
        if ($null -eq $textNode) { continue }
        $text = [string]$textNode.InnerText;
        if ([string]::IsNullOrWhiteSpace($text)) { continue }
        $multiParts += $text.Trim();
      }

      if ($multiParts.Count -gt 0) {
        return ($multiParts -join ', ');
      }
    }

    $value = [string]$child.InnerText;
    if (-not [string]::IsNullOrWhiteSpace($value)) {
      return $value.Trim();
    }
  }

  return '';
}

function Add-PolicyEntriesFromGpResultXml {
  param(
    [string]$Scope,
    [string]$FallbackSource
  )

  $context = Get-GpResultXmlContext;
  if ($null -eq $context) {
    return 0;
  }

  $scopeNodeName = if ($Scope -eq 'Device') { 'ComputerResults' } else { 'UserResults' };
  $scopeNode = $context.Document.SelectSingleNode('/r:Rsop/r:' + $scopeNodeName, $context.Namespaces);
  if ($null -eq $scopeNode) {
    return 0;
  }

  $gpoNameLookup = @{};
  foreach ($gpo in $scopeNode.SelectNodes('r:GPO', $context.Namespaces)) {
    if ($null -eq $gpo) { continue }
    $gpoId = Normalize-ProviderId (Get-GpResultNodeText -Node $gpo -XPath "*[local-name()='Identifier']" -Namespaces $context.Namespaces);
    if ([string]::IsNullOrWhiteSpace($gpoId)) { continue }

    $gpoName = Get-GpResultNodeText -Node $gpo -XPath "*[local-name()='Name']" -Namespaces $context.Namespaces;
    if ([string]::IsNullOrWhiteSpace($gpoName)) { $gpoName = $gpoId }
    $gpoName = $gpoName.Trim();
    $gpoNameLookup[$gpoId] = $gpoName;

    $source = Resolve-GpoDisplayNameSource -DisplayName $gpoName;
    $script:rsopGpoSourceLookup[$gpoId] = $source;
    $providers.Add([pscustomobject]@{
      ProviderId = $gpoId;
      Name = $gpoName;
      Source = $source
    }) | Out-Null;
  }

  $addedCount = 0;
  foreach ($setting in $scopeNode.SelectNodes(".//*[local-name()='RegistryRsopSetting']", $context.Namespaces)) {
    try {
      if ($null -eq $setting) { continue }

      $gpoId = Normalize-ProviderId (Get-GpResultNodeText -Node $setting -XPath "*[local-name()='GPO']/*[local-name()='Identifier']" -Namespaces $context.Namespaces);
      $baseXml = $setting.SelectSingleNode("*[local-name()='BaseInstanceXml']", $context.Namespaces);
      if ($null -eq $baseXml) { continue }

      $instance = $baseXml.SelectSingleNode("*[local-name()='INSTANCE']", $context.Namespaces);
      $propertyRoot = if ($null -ne $instance) { $instance } else { $baseXml };
      $fallbackRoot = if ($null -ne $instance) { $baseXml } else { $null };

      $settingName = Get-GpResultPropertyValue -Node $propertyRoot -PropertyNames @('polmkrNameResolved', 'polmkrName', 'name');
      if ([string]::IsNullOrWhiteSpace($settingName) -and $null -ne $fallbackRoot) {
        $settingName = Get-GpResultPropertyValue -Node $fallbackRoot -PropertyNames @('polmkrNameResolved', 'polmkrName', 'name');
      }

      $hive = Get-GpResultPropertyValue -Node $propertyRoot -PropertyNames @('polmkrHiveResolved', 'polmkrHive');
      if ([string]::IsNullOrWhiteSpace($hive) -and $null -ne $fallbackRoot) {
        $hive = Get-GpResultPropertyValue -Node $fallbackRoot -PropertyNames @('polmkrHiveResolved', 'polmkrHive');
      }

      $key = Get-GpResultPropertyValue -Node $propertyRoot -PropertyNames @('polmkrKeyResolved', 'polmkrKey');
      if ([string]::IsNullOrWhiteSpace($key) -and $null -ne $fallbackRoot) {
        $key = Get-GpResultPropertyValue -Node $fallbackRoot -PropertyNames @('polmkrKeyResolved', 'polmkrKey');
      }

      $valueText = Get-GpResultPropertyValue -Node $propertyRoot -PropertyNames @('polmkrValueResolved', 'polmkrValue', 'value');
      if ([string]::IsNullOrWhiteSpace($valueText) -and $null -ne $fallbackRoot) {
        $valueText = Get-GpResultPropertyValue -Node $fallbackRoot -PropertyNames @('polmkrValueResolved', 'polmkrValue', 'value');
      }

      $displayPath = Normalize-GpResultRegistryPath -Hive $hive -Key $key;
      if ([string]::IsNullOrWhiteSpace($displayPath)) { continue }
      if ([string]::IsNullOrWhiteSpace($settingName)) { $settingName = '(Default)' }
      if ([string]::IsNullOrWhiteSpace($valueText)) { continue }

      $gpoName = Get-GpResultPropertyValue -Node $baseXml -PropertyNames @('polmkrBaseGpoDisplayName', 'polmkrBaseGpoName');
      if ([string]::IsNullOrWhiteSpace($gpoName) -and -not [string]::IsNullOrWhiteSpace($gpoId) -and $gpoNameLookup.ContainsKey($gpoId)) {
        $gpoName = [string]$gpoNameLookup[$gpoId];
      }

      $source = if (-not [string]::IsNullOrWhiteSpace($gpoName)) {
        Resolve-GpoDisplayNameSource -DisplayName $gpoName
      } elseif (-not [string]::IsNullOrWhiteSpace($gpoId) -and $script:rsopGpoSourceLookup.ContainsKey($gpoId)) {
        [string]$script:rsopGpoSourceLookup[$gpoId]
      } else {
        $FallbackSource
      };

      if (-not [string]::IsNullOrWhiteSpace($gpoId)) {
        if ([string]::IsNullOrWhiteSpace($gpoName) -and $gpoNameLookup.ContainsKey($gpoId)) {
          $gpoName = [string]$gpoNameLookup[$gpoId];
        }

        if (-not [string]::IsNullOrWhiteSpace($gpoName)) {
          $providers.Add([pscustomobject]@{
            ProviderId = $gpoId;
            Name = $gpoName;
            Source = $source
          }) | Out-Null;
        }
      }

      $resultCode = Get-GpResultPropertyValue -Node $propertyRoot -PropertyNames @('polmkrClassResultCode');
      if ([string]::IsNullOrWhiteSpace($resultCode) -and $null -ne $fallbackRoot) {
        $resultCode = Get-GpResultPropertyValue -Node $fallbackRoot -PropertyNames @('polmkrClassResultCode');
      }
      $resultCodeValue = Get-GpResultPropertyValue -Node $propertyRoot -PropertyNames @('polmkrClassResultCodeValue');
      if ([string]::IsNullOrWhiteSpace($resultCodeValue) -and $null -ne $fallbackRoot) {
        $resultCodeValue = Get-GpResultPropertyValue -Node $fallbackRoot -PropertyNames @('polmkrClassResultCodeValue');
      }

      $status = 'Applied';
      if ((-not [string]::IsNullOrWhiteSpace($resultCode) -and
          -not $resultCode.Equals('0x00000000', [System.StringComparison]::OrdinalIgnoreCase) -and
          -not $resultCode.Equals('0', [System.StringComparison]::OrdinalIgnoreCase)) -or
          (-not [string]::IsNullOrWhiteSpace($resultCodeValue) -and
          -not $resultCodeValue.Equals('0', [System.StringComparison]::OrdinalIgnoreCase))) {
        $status = 'Failed';
      }

      if ($status -eq 'Applied') {
        $resultCode = '';
      }

      $area = Resolve-PolicyAreaFromPath -Path $displayPath;
      $entries.Add([pscustomobject]@{
        Scope = $Scope;
        Area = $area;
        SettingName = $settingName;
        OmaUri = $displayPath;
        CurrentValue = $valueText;
        Status = $status;
        ResultCode = $resultCode;
        Source = $source;
        WinningSource = $source
      }) | Out-Null;
      $addedCount++;
    } catch {
      # Best effort only.
    }
  }

  foreach ($setting in $scopeNode.SelectNodes(".//*[local-name()='RegistrySetting']", $context.Namespaces)) {
    try {
      if ($null -eq $setting) { continue }

      $gpoId = Normalize-ProviderId (Get-GpResultNodeText -Node $setting -XPath "*[local-name()='GPO']/*[local-name()='Identifier']" -Namespaces $context.Namespaces);
      $keyPath = Get-GpResultNodeText -Node $setting -XPath "*[local-name()='KeyPath']" -Namespaces $context.Namespaces;
      $displayPath = Normalize-GpResultKeyPathByScope -Scope $Scope -KeyPath $keyPath;
      if ([string]::IsNullOrWhiteSpace($displayPath)) { continue }

      $gpoName = if (-not [string]::IsNullOrWhiteSpace($gpoId) -and $gpoNameLookup.ContainsKey($gpoId)) {
        [string]$gpoNameLookup[$gpoId]
      } else {
        ''
      };

      $source = if (-not [string]::IsNullOrWhiteSpace($gpoName)) {
        Resolve-GpoDisplayNameSource -DisplayName $gpoName
      } elseif (-not [string]::IsNullOrWhiteSpace($gpoId) -and $script:rsopGpoSourceLookup.ContainsKey($gpoId)) {
        [string]$script:rsopGpoSourceLookup[$gpoId]
      } else {
        $FallbackSource
      };

      if (-not [string]::IsNullOrWhiteSpace($gpoId) -and -not [string]::IsNullOrWhiteSpace($gpoName)) {
        $providers.Add([pscustomobject]@{
          ProviderId = $gpoId;
          Name = $gpoName;
          Source = $source
        }) | Out-Null;
      }

      $valueNodes = @($setting.SelectNodes("*[local-name()='Value']", $context.Namespaces));
      foreach ($valueNode in $valueNodes) {
        if ($null -eq $valueNode) { continue }

        $settingName = Get-GpResultNodeText -Node $valueNode -XPath "*[local-name()='Name']" -Namespaces $context.Namespaces;
        if ([string]::IsNullOrWhiteSpace($settingName)) { $settingName = '(Default)' }

        $valueText = Get-GpResultRegistryValueText -ValueNode $valueNode;
        if ([string]::IsNullOrWhiteSpace($valueText)) { continue }

        $area = Resolve-PolicyAreaFromPath -Path $displayPath;
        $entries.Add([pscustomobject]@{
          Scope = $Scope;
          Area = $area;
          SettingName = $settingName;
          OmaUri = $displayPath;
          CurrentValue = $valueText;
          Status = 'Applied';
          ResultCode = '';
          Source = $source;
          WinningSource = $source
        }) | Out-Null;
        $addedCount++;
      }
    } catch {
      # Best effort only.
    }
  }

  return $addedCount;
}

function Add-RsopGpoProviderEntries {
  param([string]$Scope)

  $namespace = if ($Scope -eq 'Device') { 'root\rsop\computer' } else { 'root\rsop\user' };
  try {
    foreach ($gpo in (Get-CimInstance -Namespace $namespace -ClassName RSOP_GPO -ErrorAction Stop)) {
      $gpoIdRaw = [string](Get-RsopPropertyValue -Item $gpo -PropertyNames @('id', 'ID'));
      $gpoId = Normalize-ProviderId $gpoIdRaw;
      if ([string]::IsNullOrWhiteSpace($gpoId)) { continue }

      $name = [string](Get-RsopPropertyValue -Item $gpo -PropertyNames @('name', 'Name'));
      $nameLower = $name.ToLowerInvariant();
      $source = if ($nameLower.Contains('local group policy') -or $nameLower.Contains('lokale gruppenrichtlinie')) {
        'LocalPolicy'
      } else {
        'GroupPolicy'
      };

      $script:rsopGpoSourceLookup[$gpoId] = $source;
      $providers.Add([pscustomobject]@{
        ProviderId = $gpoId;
        Name = $name;
        Source = $source
      }) | Out-Null;
    }
  } catch {
    # Best effort only.
  }
}

function Add-PolicyProviderEntries {
  $providerRoot = 'Registry::HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\PolicyManager\Providers';
  if (-not (Test-Path -LiteralPath $providerRoot)) { return }

  try {
    foreach ($providerKey in (Get-ChildItem -LiteralPath $providerRoot -ErrorAction Stop)) {
      if ($null -eq $providerKey -or [string]::IsNullOrWhiteSpace($providerKey.PSChildName)) { continue }
      $providerId = [string]$providerKey.PSChildName;
      $providerName = '';
      $hintParts = New-Object System.Collections.Generic.List[string];
      $hintParts.Add($providerId) | Out-Null;

      try {
        $props = Get-ItemProperty -LiteralPath $providerKey.PSPath -ErrorAction Stop;
        foreach ($prop in $props.PSObject.Properties) {
          if ($null -eq $prop -or [string]::IsNullOrWhiteSpace($prop.Name)) { continue }
          if ($skipNames -contains $prop.Name) { continue }
          if ($prop.Value -eq $null) { continue }
          $valueText = [string]$prop.Value;
          if ([string]::IsNullOrWhiteSpace($valueText)) { continue }
          if ([string]::IsNullOrWhiteSpace($providerName) -and $prop.Name -match 'name|provider') {
            $providerName = $valueText;
          }
          $hintParts.Add($valueText) | Out-Null;
        }
      } catch {
        # Best effort only.
      }

      $hint = [string]::Join(' ', $hintParts);
      $source = Resolve-ProviderSource -ProviderId $providerId -ProviderName $providerName -RawHint $hint;
      $providers.Add([pscustomobject]@{
        ProviderId = $providerId;
        Name = $providerName;
        Source = $source
      }) | Out-Null;
    }
  } catch {
    # Best effort only.
  }
}

function Add-PolicyEntriesFromRsopRegistry {
  param(
    [string]$Scope,
    [string]$FallbackSource
  )

  $namespace = if ($Scope -eq 'Device') { 'root\rsop\computer' } else { 'root\rsop\user' };
  try {
    $rsopSettings = @(Get-CimInstance -Namespace $namespace -ClassName RSOP_RegistryPolicySetting -ErrorAction Stop);
  } catch {
    return;
  }

  foreach ($setting in $rsopSettings) {
    try {
      $keyPathRaw = [string](Get-RsopPropertyValue -Item $setting -PropertyNames @('keyName', 'KeyName', 'registryKey', 'RegistryKey', 'path', 'Path'));
      $displayPath = Normalize-RsopRegistryPath -Scope $Scope -Path $keyPathRaw;
      if ([string]::IsNullOrWhiteSpace($displayPath)) { continue }

      $settingName = [string](Get-RsopPropertyValue -Item $setting -PropertyNames @('valueName', 'ValueName', 'name', 'Name'));
      if ([string]::IsNullOrWhiteSpace($settingName)) { $settingName = '(Default)' }

      $rawValue = Get-RsopPropertyValue -Item $setting -PropertyNames @('valueData', 'ValueData', 'settingValue', 'SettingValue', 'Data');
      $valueTypeRaw = Get-RsopPropertyValue -Item $setting -PropertyNames @('valueType', 'ValueType', 'type', 'Type');
      $valueType = 0;
      if ($null -ne $valueTypeRaw) { [void][int]::TryParse([string]$valueTypeRaw, [ref]$valueType) }

      $valueText = '';
      if ($rawValue -is [byte[]]) {
        $valueText = Convert-RsopByteValue -Data $rawValue -ValueType $valueType;
      } else {
        $valueText = Convert-PolicyValue -Value $rawValue;
      }

      if ([string]::IsNullOrWhiteSpace($valueText)) {
        foreach ($candidateName in @('StringValue','ExpandedStringValue','DwordValue','QwordValue','MultiStringValue','BinaryValue')) {
          $candidate = Get-RsopPropertyValue -Item $setting -PropertyNames @($candidateName);
          if ($null -eq $candidate) { continue }
          $valueText = Convert-PolicyValue -Value $candidate;
          if (-not [string]::IsNullOrWhiteSpace($valueText)) { break }
        }
      }

      if ([string]::IsNullOrWhiteSpace($valueText)) { continue }

      $source = $FallbackSource;
      $gpoId = Normalize-ProviderId ([string](Get-RsopPropertyValue -Item $setting -PropertyNames @('gpoId', 'GPOID', 'GpoID')));
      if (-not [string]::IsNullOrWhiteSpace($gpoId) -and $script:rsopGpoSourceLookup.ContainsKey($gpoId)) {
        $source = [string]$script:rsopGpoSourceLookup[$gpoId];
      }

      $area = Resolve-PolicyAreaFromPath -Path $displayPath;

      $entries.Add([pscustomobject]@{
        Scope = $Scope;
        Area = $area;
        SettingName = $settingName;
        OmaUri = $displayPath;
        CurrentValue = $valueText;
        Status = 'Applied';
        ResultCode = '';
        Source = $source;
        WinningSource = $source
      }) | Out-Null;
    } catch {
      # Best effort only.
    }
  }
}

function Add-PolicyEntriesFromHive {
  param(
    [string]$Scope,
    [string]$HivePath,
    [string]$Source
  )

  if (-not (Test-Path -LiteralPath $HivePath)) {
    return;
  }

  $keyPaths = New-Object System.Collections.Generic.List[string];
  $keyPaths.Add($HivePath) | Out-Null;
  foreach ($subKey in (Get-ChildItem -LiteralPath $HivePath -Recurse -ErrorAction SilentlyContinue)) {
    if ($null -eq $subKey -or [string]::IsNullOrWhiteSpace($subKey.PSPath)) { continue }
    $keyPaths.Add([string]$subKey.PSPath) | Out-Null;
  }

  foreach ($keyPath in $keyPaths) {
    try {
      $item = Get-Item -LiteralPath $keyPath -ErrorAction Stop;
      $props = $item.Property;
      if ($null -eq $props -or $props.Count -eq 0) { continue }

      $rawName = [string]$item.Name;
      if ([string]::IsNullOrWhiteSpace($rawName)) { continue }

      $displayPath = $rawName;
      if ($displayPath.StartsWith('HKEY_LOCAL_MACHINE\', [System.StringComparison]::OrdinalIgnoreCase)) {
        $displayPath = 'HKLM\' + $displayPath.Substring('HKEY_LOCAL_MACHINE\'.Length);
      } elseif ($displayPath.StartsWith('HKEY_CURRENT_USER\', [System.StringComparison]::OrdinalIgnoreCase)) {
        $displayPath = 'HKCU\' + $displayPath.Substring('HKEY_CURRENT_USER\'.Length);
      }

      $area = Resolve-PolicyAreaFromPath -Path $displayPath;

      foreach ($name in $props) {
        if ($skipNames -contains $name) { continue }
        try {
          $value = Convert-PolicyValue -Value ($item.GetValue($name, $null, 'DoNotExpandEnvironmentNames'));
        } catch {
          continue;
        }

        if ([string]::IsNullOrWhiteSpace($value)) { continue }

        $entries.Add([pscustomobject]@{
          Scope = $Scope;
          Area = $area;
          SettingName = [string]$name;
          OmaUri = $displayPath;
          CurrentValue = $value;
          Status = 'Applied';
          ResultCode = '';
          Source = $Source;
          WinningSource = $Source
        }) | Out-Null;
      }
    } catch {
      # Best effort only.
    }
  }
}

$deviceSource = Resolve-ScopePolicySource -Scope 'Device';
$userSource = Resolve-ScopePolicySource -Scope 'User';

    $deviceGpResultEntries = Add-PolicyEntriesFromGpResultXml -Scope 'Device' -FallbackSource $deviceSource;
    $userGpResultEntries = Add-PolicyEntriesFromGpResultXml -Scope 'User' -FallbackSource $userSource;

    if ($deviceGpResultEntries -eq 0) {
        Add-RsopGpoProviderEntries -Scope 'Device';
        Add-PolicyEntriesFromRsopRegistry -Scope 'Device' -FallbackSource $deviceSource;
        Add-PolicyEntriesFromHive -Scope 'Device' -HivePath 'Registry::HKEY_LOCAL_MACHINE\SOFTWARE\Policies' -Source $deviceSource;
        Add-PolicyEntriesFromHive -Scope 'Device' -HivePath 'Registry::HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Policies' -Source $deviceSource;
    }

    if ($userGpResultEntries -eq 0) {
        Add-RsopGpoProviderEntries -Scope 'User';
        Add-PolicyEntriesFromRsopRegistry -Scope 'User' -FallbackSource $userSource;
        Add-PolicyEntriesFromHive -Scope 'User' -HivePath 'Registry::HKEY_CURRENT_USER\SOFTWARE\Policies' -Source $userSource;
    }

Add-PolicyProviderEntries;

$result = [ordered]@{
  Entries = $entries;
  Providers = $providers
};
$result | ConvertTo-Json -Depth 8 -Compress;
""";

        var execution = await executor.ExecuteForHostAsync(host, script, cancellationToken);
        if (execution.ExitCode != 0)
        {
            warnings.Add($"Local policy overlay collection failed: {NormalizeError(execution)}");
            return ([], new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        }

        if (string.IsNullOrWhiteSpace(execution.StdOut))
        {
            return ([], new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        }

        if (!TryParsePowerShellJsonDocument(execution.StdOut, out var document, out var parseWarning, out var parseError))
        {
            warnings.Add($"Local policy overlay parse failed: {parseError}");
            return ([], new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(parseWarning))
        {
            warnings.Add(parseWarning);
        }

        try
        {
            using (document)
            {
                var payloads = new List<PolicyOverlayPayload>();
                var providerPayloads = new List<PolicyProviderPayload>();
                if (document.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var element in document.RootElement.EnumerateArray())
                    {
                        var payload = element.Deserialize<PolicyOverlayPayload>(JsonOptions);
                        if (payload is not null)
                        {
                            payloads.Add(payload);
                        }
                    }
                }
                else if (document.RootElement.ValueKind == JsonValueKind.Object)
                {
                    if (document.RootElement.TryGetProperty("entries", out var entriesElement))
                    {
                        if (entriesElement.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var element in entriesElement.EnumerateArray())
                            {
                                var payload = element.Deserialize<PolicyOverlayPayload>(JsonOptions);
                                if (payload is not null)
                                {
                                    payloads.Add(payload);
                                }
                            }
                        }
                        else if (entriesElement.ValueKind == JsonValueKind.Object)
                        {
                            var payload = entriesElement.Deserialize<PolicyOverlayPayload>(JsonOptions);
                            if (payload is not null)
                            {
                                payloads.Add(payload);
                            }
                        }
                    }

                    if (document.RootElement.TryGetProperty("providers", out var providersElement) &&
                        providersElement.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var element in providersElement.EnumerateArray())
                        {
                            var provider = element.Deserialize<PolicyProviderPayload>(JsonOptions);
                            if (provider is not null)
                            {
                                providerPayloads.Add(provider);
                            }
                        }
                    }

                    if (payloads.Count == 0 && providerPayloads.Count == 0)
                    {
                        var payload = document.RootElement.Deserialize<PolicyOverlayPayload>(JsonOptions);
                        if (payload is not null)
                        {
                            payloads.Add(payload);
                        }
                    }
                }

                var entries = payloads
                    .Select(CreatePolicyOverlayEntry)
                    .Where(entry => entry is not null)
                    .Select(entry => entry!)
                    .ToArray();
                var providerLookup = BuildPolicyProviderSourceLookup(providerPayloads);
                return (DeduplicatePolicyEntries(entries), providerLookup);
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            warnings.Add($"Local policy overlay parse failed: {ex.Message}");
            return ([], new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        }
    }

    private static bool TryParsePowerShellJsonDocument(
        string rawOutput,
        out JsonDocument document,
        out string warning,
        out string error)
    {
        warning = string.Empty;
        error = "PowerShell output was empty.";
        document = null!;

        if (string.IsNullOrWhiteSpace(rawOutput))
        {
            return false;
        }

        var trimmed = rawOutput.Trim();
        if (TryParseJsonDocument(trimmed, out document))
        {
            error = string.Empty;
            return true;
        }

        var startIndex = -1;
        for (var i = 0; i < trimmed.Length; i++)
        {
            if (trimmed[i] == '{' || trimmed[i] == '[')
            {
                startIndex = i;
                break;
            }
        }

        if (startIndex < 0 ||
            !TryExtractBalancedJsonBlock(trimmed, startIndex, out var jsonText, out var prefixLength, out var suffixLength) ||
            !TryParseJsonDocument(jsonText, out document))
        {
            error = "No valid JSON payload found in PowerShell output.";
            return false;
        }

        error = string.Empty;
        if (prefixLength > 0 || suffixLength > 0)
        {
            warning = $"Local policy overlay output contained additional console text and was normalized (prefix chars: {prefixLength}, suffix chars: {suffixLength}).";
        }

        return true;

        static bool TryParseJsonDocument(string candidate, out JsonDocument parsed)
        {
            try
            {
                parsed = JsonDocument.Parse(candidate);
                return true;
            }
            catch (JsonException)
            {
                parsed = null!;
                return false;
            }
        }
    }

    private static bool TryExtractBalancedJsonBlock(
        string text,
        int startIndex,
        out string json,
        out int prefixLength,
        out int suffixLength)
    {
        json = string.Empty;
        prefixLength = 0;
        suffixLength = 0;

        if (string.IsNullOrWhiteSpace(text) || startIndex < 0 || startIndex >= text.Length)
        {
            return false;
        }

        var open = text[startIndex];
        var close = open == '{' ? '}' : open == '[' ? ']' : '\0';
        if (close == '\0')
        {
            return false;
        }

        var depth = 0;
        var inString = false;
        var escaped = false;

        for (var i = startIndex; i < text.Length; i++)
        {
            var ch = text[i];
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                    continue;
                }

                if (ch == '\\')
                {
                    escaped = true;
                    continue;
                }

                if (ch == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (ch == '"')
            {
                inString = true;
                continue;
            }

            if (ch == open)
            {
                depth++;
                continue;
            }

            if (ch != close)
            {
                continue;
            }

            depth--;
            if (depth != 0)
            {
                continue;
            }

            json = text[startIndex..(i + 1)];
            prefixLength = startIndex;
            suffixLength = text.Length - i - 1;
            return true;
        }

        return false;
    }

    private static IntunePolicyResultEntry? CreatePolicyOverlayEntry(PolicyOverlayPayload payload)
    {
        var settingName = NormalizePolicyFieldValue(payload.SettingName);
        var path = NormalizePolicyFieldValue(payload.OmaUri);
        if (string.IsNullOrWhiteSpace(settingName) && string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var source = NormalizePolicySource(payload.Source);
        var winningSource = NormalizePolicySource(payload.WinningSource);
        return new IntunePolicyResultEntry(
            NormalizeScope(payload.Scope, path),
            string.IsNullOrWhiteSpace(payload.Area) ? DeriveAreaFromOmaUri(path) : NormalizePolicyFieldValue(payload.Area),
            string.IsNullOrWhiteSpace(settingName) ? DeriveSettingNameFromOmaUri(path) : settingName,
            path,
            NormalizePolicyFieldValue(payload.CurrentValue),
            string.IsNullOrWhiteSpace(payload.Status) ? "Applied" : NormalizePolicyFieldValue(payload.Status),
            NormalizeResultCode(payload.ResultCode),
            source,
            string.IsNullOrWhiteSpace(winningSource) ? source : winningSource,
            false,
            string.Empty,
            string.Empty,
            path,
            DeriveGpoCategoryPath(path, source));
    }

    private static IReadOnlyList<IntunePolicyResultEntry> MergePolicyEntriesWithConflictAnalysis(
        IReadOnlyList<IntunePolicyResultEntry> mdmEntries,
        IReadOnlyList<IntunePolicyResultEntry> overlayEntries)
    {
        if (mdmEntries.Count == 0 && overlayEntries.Count == 0)
        {
            return [];
        }

        var combined = mdmEntries
            .Concat(overlayEntries)
            .ToArray();
        var analyzed = new List<IntunePolicyResultEntry>(combined.Length);

        foreach (var groupEntries in BuildConflictGroups(combined))
        {
            var sourceSet = groupEntries
                .Select(entry => NormalizePolicySource(entry.Source))
                .Where(source => !string.IsNullOrWhiteSpace(source))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(source => source, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var isDuplicate = sourceSet.Length > 1;
            var duplicateSources = isDuplicate ? string.Join(", ", sourceSet) : string.Empty;
            var groupWinningSource = ResolveConflictWinningSource(groupEntries, sourceSet);

            foreach (var entry in groupEntries)
            {
                var entrySource = NormalizePolicySource(entry.Source);
                var entryWinning = NormalizePolicySource(entry.WinningSource);
                if (!string.IsNullOrWhiteSpace(groupWinningSource) &&
                    !string.Equals(groupWinningSource, "Unknown", StringComparison.OrdinalIgnoreCase))
                {
                    entryWinning = groupWinningSource;
                }
                else if (string.IsNullOrWhiteSpace(entryWinning) ||
                         string.Equals(entryWinning, "Unknown", StringComparison.OrdinalIgnoreCase))
                {
                    entryWinning = groupWinningSource;
                }

                analyzed.Add(entry with
                {
                    Source = string.IsNullOrWhiteSpace(entrySource) ? "Unknown" : entrySource,
                    WinningSource = string.IsNullOrWhiteSpace(entryWinning) ? "Unknown" : entryWinning,
                    IsDuplicate = isDuplicate,
                    DuplicateSources = duplicateSources
                });
            }
        }

        return DeduplicatePolicyEntries(analyzed);
    }

    private static string CreateConflictKey(IntunePolicyResultEntry entry)
    {
        var scope = NormalizeScope(entry.Scope, entry.OmaUri);
        var path = NormalizePolicyPathForComparison(entry.OmaUri);
        var settingName = NormalizePolicyFieldValue(entry.SettingName);
        if (string.IsNullOrWhiteSpace(path))
        {
            path = $"{NormalizePolicyFieldValue(entry.Area)}\\{settingName}";
        }

        return $"{scope}|{path}|{settingName}";
    }

    private static string ResolveConflictWinningSource(
        IReadOnlyList<IntunePolicyResultEntry> entries,
        IReadOnlyList<string> sourceSet)
    {
        var explicitWinner = entries
            .Select(entry => NormalizePolicySource(entry.WinningSource))
            .FirstOrDefault(candidate =>
                !string.IsNullOrWhiteSpace(candidate) &&
                !string.Equals(candidate, "Unknown", StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(explicitWinner))
        {
            return explicitWinner;
        }

        if (sourceSet.Any(source => string.Equals(source, "GroupPolicy", StringComparison.OrdinalIgnoreCase)))
        {
            return "GroupPolicy";
        }

        if (sourceSet.Any(source => string.Equals(source, "LocalPolicy", StringComparison.OrdinalIgnoreCase)))
        {
            return "LocalPolicy";
        }

        if (sourceSet.Any(source => string.Equals(source, "Mdm", StringComparison.OrdinalIgnoreCase)))
        {
            return "Mdm";
        }

        return sourceSet.FirstOrDefault() ?? "Unknown";
    }

    private static string NormalizePolicyPathForComparison(string? path)
    {
        var normalized = NormalizePolicyFieldValue(path);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.Empty;
        }

        normalized = normalized.Replace("/", "\\", StringComparison.Ordinal).TrimStart('.', '\\');
        if (normalized.StartsWith(@"Registry::HKEY_LOCAL_MACHINE\", StringComparison.OrdinalIgnoreCase))
        {
            normalized = "HKLM\\" + normalized[@"Registry::HKEY_LOCAL_MACHINE\".Length..];
        }
        else if (normalized.StartsWith(@"Registry::HKEY_CURRENT_USER\", StringComparison.OrdinalIgnoreCase))
        {
            normalized = "HKCU\\" + normalized[@"Registry::HKEY_CURRENT_USER\".Length..];
        }
        else if (normalized.StartsWith(@"HKEY_LOCAL_MACHINE\", StringComparison.OrdinalIgnoreCase))
        {
            normalized = "HKLM\\" + normalized[@"HKEY_LOCAL_MACHINE\".Length..];
        }
        else if (normalized.StartsWith(@"HKEY_CURRENT_USER\", StringComparison.OrdinalIgnoreCase))
        {
            normalized = "HKCU\\" + normalized[@"HKEY_CURRENT_USER\".Length..];
        }

        if (normalized.StartsWith(@"HKLM\", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith(@"HKCU\", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[5..];
        }

        return normalized;
    }

    private static IEnumerable<string> EnumerateConflictKeys(IntunePolicyResultEntry entry)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            CreateConflictKey(entry)
        };

        var scope = NormalizeScope(entry.Scope, entry.OmaUri);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddConflictPath(paths, entry.OmaUri);
        AddConflictPath(paths, entry.GpoPath);
        AddConflictPath(paths, entry.MdmPath);

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            NormalizePolicyFieldValue(entry.SettingName)
        };

        var admxPolicy = TryResolveAdmxPolicyDefinition(entry);
        if (admxPolicy is not null)
        {
            AddConflictPath(paths, admxPolicy.KeyPath);
            names.Add(NormalizePolicyFieldValue(admxPolicy.PolicyName));
            foreach (var displayName in admxPolicy.DisplayNames)
            {
                names.Add(NormalizePolicyFieldValue(displayName));
            }
        }

        foreach (var path in paths.Where(static value => !string.IsNullOrWhiteSpace(value)))
        {
            foreach (var name in names.Where(static value => !string.IsNullOrWhiteSpace(value)))
            {
                keys.Add($"{scope}|{path}|{name}");
            }
        }

        return keys;
    }

    private static void AddConflictPath(ISet<string> paths, string? path)
    {
        var normalized = NormalizePolicyPathForComparison(path);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return;
        }

        paths.Add(normalized);
    }

    private static AdmxPolicyDefinition? TryResolveAdmxPolicyDefinition(IntunePolicyResultEntry entry)
    {
        var catalog = GetAdmxPolicyCatalog();
        if (catalog.ByPolicyName.Count == 0 && catalog.ByDisplayName.Count == 0)
        {
            return null;
        }

        var settingName = NormalizePolicyFieldValue(entry.SettingName);
        if (!string.IsNullOrWhiteSpace(settingName) &&
            catalog.ByPolicyName.TryGetValue(settingName, out var directMatch))
        {
            return directMatch;
        }

        if (string.IsNullOrWhiteSpace(settingName))
        {
            return null;
        }

        if (!catalog.ByDisplayName.TryGetValue(NormalizePolicyDisplayLookupKey(settingName), out var displayMatches) ||
            displayMatches.Count == 0)
        {
            return null;
        }

        if (displayMatches.Count == 1)
        {
            return displayMatches[0];
        }

        var knownPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddConflictPath(knownPaths, entry.OmaUri);
        AddConflictPath(knownPaths, entry.GpoPath);
        AddConflictPath(knownPaths, entry.MdmPath);

        var pathFiltered = displayMatches
            .Where(match => knownPaths.Contains(NormalizePolicyPathForComparison(match.KeyPath)))
            .ToArray();
        if (pathFiltered.Length == 1)
        {
            return pathFiltered[0];
        }

        return null;
    }

    private static AdmxPolicyCatalog GetAdmxPolicyCatalog()
    {
        var root = ResolvePolicyDefinitionsRoot();
        if (string.IsNullOrWhiteSpace(root))
        {
            return new AdmxPolicyCatalog(
                new Dictionary<string, AdmxPolicyDefinition>(StringComparer.OrdinalIgnoreCase),
                new Dictionary<string, IReadOnlyList<AdmxPolicyDefinition>>(StringComparer.OrdinalIgnoreCase));
        }

        lock (AdmxCatalogLock)
        {
            if (AdmxCatalogCache.TryGetValue(root, out var cached))
            {
                return cached;
            }

            var catalog = BuildAdmxPolicyCatalog(root);
            AdmxCatalogCache[root] = catalog;
            return catalog;
        }
    }

    private static string ResolvePolicyDefinitionsRoot()
    {
        static string NormalizeRoot(string? value)
            => string.IsNullOrWhiteSpace(value) ? string.Empty : Path.GetFullPath(value.Trim());

        var overrideRoot = NormalizeRoot(Environment.GetEnvironmentVariable(PolicyDefinitionsRootOverrideEnvironmentVariable));
        if (!string.IsNullOrWhiteSpace(overrideRoot) && Directory.Exists(overrideRoot))
        {
            return overrideRoot;
        }

        var candidates = new[]
        {
            Environment.GetEnvironmentVariable("windir"),
            Environment.GetEnvironmentVariable("WINDIR"),
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Path.Combine(Path.GetPathRoot(Environment.CurrentDirectory) ?? string.Empty, "Windows")
        };

        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            var root = NormalizeRoot(Path.Combine(candidate, "PolicyDefinitions"));
            if (Directory.Exists(root))
            {
                return root;
            }
        }

        return string.Empty;
    }

    private static AdmxPolicyCatalog BuildAdmxPolicyCatalog(string root)
    {
        var byPolicyName = new Dictionary<string, AdmxPolicyDefinition>(StringComparer.OrdinalIgnoreCase);
        var byDisplayName = new Dictionary<string, List<AdmxPolicyDefinition>>(StringComparer.OrdinalIgnoreCase);
        foreach (var admxPath in Directory.EnumerateFiles(root, "*.admx", SearchOption.TopDirectoryOnly))
        {
            IReadOnlyDictionary<string, string> stringLookup;
            try
            {
                stringLookup = BuildAdmlStringLookup(root, Path.GetFileNameWithoutExtension(admxPath));
                var document = XDocument.Load(admxPath, LoadOptions.PreserveWhitespace);
                foreach (var policy in document.Descendants().Where(static element => element.Name.LocalName.Equals("policy", StringComparison.OrdinalIgnoreCase)))
                {
                    var policyName = NormalizePolicyFieldValue(policy.Attribute("name")?.Value);
                    var keyPath = NormalizePolicyFieldValue(policy.Attribute("key")?.Value);
                    if (string.IsNullOrWhiteSpace(policyName) || string.IsNullOrWhiteSpace(keyPath))
                    {
                        continue;
                    }

                    var displayNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    AddAdmxDisplayName(displayNames, policy.Attribute("displayName")?.Value, stringLookup);
                    displayNames.Add(policyName);
                    var definition = new AdmxPolicyDefinition(policyName, keyPath, displayNames.ToArray());
                    byPolicyName[policyName] = definition;

                    foreach (var displayName in displayNames)
                    {
                        var lookupKey = NormalizePolicyDisplayLookupKey(displayName);
                        if (string.IsNullOrWhiteSpace(lookupKey))
                        {
                            continue;
                        }

                        if (!byDisplayName.TryGetValue(lookupKey, out var list))
                        {
                            list = [];
                            byDisplayName[lookupKey] = list;
                        }

                        if (list.All(existing => !string.Equals(existing.PolicyName, definition.PolicyName, StringComparison.OrdinalIgnoreCase)))
                        {
                            list.Add(definition);
                        }
                    }
                }
            }
            catch
            {
                // Ignore malformed or inaccessible ADMX files.
            }
        }

        return new AdmxPolicyCatalog(
            byPolicyName,
            byDisplayName.ToDictionary(static pair => pair.Key, static pair => (IReadOnlyList<AdmxPolicyDefinition>)pair.Value, StringComparer.OrdinalIgnoreCase));
    }

    private static IReadOnlyDictionary<string, string> BuildAdmlStringLookup(string root, string baseName)
    {
        var lookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var cultureName in GetAdmlCandidateCultures())
        {
            var admlPath = Path.Combine(root, cultureName, baseName + ".adml");
            if (!File.Exists(admlPath))
            {
                continue;
            }

            try
            {
                var document = XDocument.Load(admlPath, LoadOptions.PreserveWhitespace);
                foreach (var stringNode in document.Descendants().Where(static element => element.Name.LocalName.Equals("string", StringComparison.OrdinalIgnoreCase)))
                {
                    var id = NormalizePolicyFieldValue(stringNode.Attribute("id")?.Value);
                    var value = NormalizePolicyFieldValue(stringNode.Value);
                    if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(value))
                    {
                        continue;
                    }

                    lookup[id] = value;
                }
            }
            catch
            {
                // Ignore malformed or inaccessible ADML files.
            }
        }

        return lookup;
    }

    private static IEnumerable<string> GetAdmlCandidateCultures()
    {
        var candidates = new[]
        {
            CultureInfo.CurrentUICulture.Name,
            CultureInfo.CurrentUICulture.Parent?.Name,
            CultureInfo.InstalledUICulture.Name,
            "de-DE",
            "en-US"
        };

        return candidates
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)!;
    }

    private static void AddAdmxDisplayName(ISet<string> target, string? rawDisplayName, IReadOnlyDictionary<string, string> stringLookup)
    {
        var normalized = NormalizePolicyFieldValue(rawDisplayName);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return;
        }

        var resolved = ResolveAdmxStringReference(normalized, stringLookup);
        if (!string.IsNullOrWhiteSpace(resolved))
        {
            target.Add(resolved);
        }
    }

    private static string ResolveAdmxStringReference(string rawValue, IReadOnlyDictionary<string, string> stringLookup)
    {
        var normalized = NormalizePolicyFieldValue(rawValue);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.Empty;
        }

        if (!normalized.StartsWith("$(string.", StringComparison.OrdinalIgnoreCase) ||
            !normalized.EndsWith(')'))
        {
            return normalized;
        }

        var stringId = normalized["$(string.".Length..^1];
        return stringLookup.TryGetValue(stringId, out var resolved) && !string.IsNullOrWhiteSpace(resolved)
            ? resolved
            : stringId;
    }

    private static string NormalizePolicyDisplayLookupKey(string? value)
        => NormalizePolicyFieldValue(value).ToLowerInvariant();

    private static string NormalizePolicySource(string? source)
    {
        var normalized = NormalizePolicyFieldValue(source);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return "Unknown";
        }

        var lower = normalized.ToLowerInvariant();
        if (lower.Contains("group", StringComparison.Ordinal))
        {
            return "GroupPolicy";
        }

        if (lower.Contains("local", StringComparison.Ordinal))
        {
            return "LocalPolicy";
        }

        if (lower.Contains("mdm", StringComparison.Ordinal) ||
            lower.Contains("intune", StringComparison.Ordinal) ||
            lower.Contains("csp", StringComparison.Ordinal))
        {
            return "Mdm";
        }

        if (lower.Contains("registry", StringComparison.Ordinal))
        {
            return "RegistryPolicy";
        }

        return normalized;
    }

    private static bool IsGpoLikeSource(string? source)
    {
        var normalized = NormalizePolicySource(source);
        return string.Equals(normalized, "GroupPolicy", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(normalized, "LocalPolicy", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(normalized, "RegistryPolicy", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLinkedGpoSource(string? source)
        => string.Equals(NormalizePolicySource(source), "GroupPolicy", StringComparison.OrdinalIgnoreCase);

    private static bool IsLocalGpoSource(string? source)
    {
        var normalized = NormalizePolicySource(source);
        return string.Equals(normalized, "LocalPolicy", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(normalized, "RegistryPolicy", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsMdmLikeSource(string? source)
        => string.Equals(NormalizePolicySource(source), "Mdm", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeRegistryPolicyPath(string? path)
    {
        var normalized = NormalizePolicyFieldValue(path);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        return normalized.StartsWith("HKLM\\", StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith("HKCU\\", StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith("HKEY_", StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith("Registry::", StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith(@"Software\Policies\", StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith(@"SYSTEM\CurrentControlSet\Policies\", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains(@"\Software\Policies\", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains(@"\SYSTEM\CurrentControlSet\Policies\", StringComparison.OrdinalIgnoreCase);
    }

    private static string DeriveGpoCategoryPath(string? path, string? source)
    {
        if (!IsGpoLikeSource(source))
        {
            return string.Empty;
        }

        var normalizedPath = NormalizePolicyPathForComparison(path);
        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            return string.Empty;
        }

        var policySegment = ExtractPolicyCategoryTail(normalizedPath, @"Software\Policies\");
        if (!string.IsNullOrWhiteSpace(policySegment))
        {
            return @"Administrative Templates\" + policySegment;
        }

        policySegment = ExtractPolicyCategoryTail(normalizedPath, @"SYSTEM\CurrentControlSet\Policies\");
        if (!string.IsNullOrWhiteSpace(policySegment))
        {
            return @"Administrative Templates\System\" + policySegment;
        }

        return @"Registry\" + normalizedPath;
    }

    private static string ExtractPolicyCategoryTail(string normalizedPath, string marker)
    {
        var markerIndex = normalizedPath.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
        {
            return string.Empty;
        }

        return NormalizePolicyFieldValue(normalizedPath[(markerIndex + marker.Length)..].Trim('\\'));
    }

    private static (IReadOnlyList<IntunePolicyResultEntry> Entries, string Source) ExtractPolicyEntries(
        string xmlPath,
        string htmlPath,
        ICollection<string> warnings,
        IReadOnlyDictionary<string, string> providerSourceLookup)
    {
        var xmlEntries = TryExtractPolicyEntriesFromXml(xmlPath, warnings, providerSourceLookup);
        if (xmlEntries.Count > 2)
        {
            return (xmlEntries, "Xml");
        }

        var htmlEntries = TryExtractPolicyEntriesFromHtml(htmlPath, warnings);
        if (htmlEntries.Count > xmlEntries.Count)
        {
            if (xmlEntries.Count > 0)
            {
                warnings.Add($"XML extraction yielded {xmlEntries.Count} entries; switched to HTML fallback with {htmlEntries.Count} entries.");
            }

            return (htmlEntries, "HtmlFallback");
        }

        return xmlEntries.Count > 0
            ? (xmlEntries, "Xml")
            : (htmlEntries, "HtmlFallback");
    }

    private static IReadOnlyList<IntunePolicyResultEntry> MergeGpResultHtmlEntries(
        IReadOnlyList<IntunePolicyResultEntry> existingEntries,
        IReadOnlyList<IntunePolicyResultEntry> htmlEntries)
    {
        if (htmlEntries.Count == 0)
        {
            return existingEntries;
        }

        var merged = existingEntries.ToList();
        var existingLookup = merged
            .Select((entry, index) => new { entry, index })
            .GroupBy(item => CreateGpResultHtmlMatchKey(item.entry), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Select(item => item.index).ToArray(), StringComparer.OrdinalIgnoreCase);

        foreach (var htmlEntry in htmlEntries)
        {
            var key = CreateGpResultHtmlMatchKey(htmlEntry);
            if (!existingLookup.TryGetValue(key, out var candidateIndexes) || candidateIndexes.Length == 0)
            {
                merged.Add(htmlEntry);
                continue;
            }

            var bestIndex = candidateIndexes
                .OrderByDescending(index =>
                    IsGpoLikeSource(merged[index].Source) ||
                    IsGpoLikeSource(merged[index].WinningSource) ||
                    !string.IsNullOrWhiteSpace(merged[index].GpoPath))
                .ThenBy(index => string.IsNullOrWhiteSpace(merged[index].OmaUri))
                .First();

            var current = merged[bestIndex];
            merged[bestIndex] = current with
            {
                OmaUri = string.IsNullOrWhiteSpace(current.OmaUri) ? htmlEntry.OmaUri : current.OmaUri,
                Area = string.IsNullOrWhiteSpace(current.Area) || string.Equals(current.Area, "General", StringComparison.OrdinalIgnoreCase)
                    ? htmlEntry.Area
                    : current.Area,
                CurrentValue = string.IsNullOrWhiteSpace(current.CurrentValue) ? htmlEntry.CurrentValue : current.CurrentValue,
                GpoPath = string.IsNullOrWhiteSpace(htmlEntry.GpoPath) ? current.GpoPath : htmlEntry.GpoPath,
                GpoCategoryPath = string.IsNullOrWhiteSpace(htmlEntry.GpoCategoryPath) ? current.GpoCategoryPath : htmlEntry.GpoCategoryPath,
                AdditionalDetails = MergePolicyDetailText(current.AdditionalDetails, htmlEntry.AdditionalDetails)
            };
        }

        return DeduplicatePolicyEntries(merged);
    }

    private static string CreateGpResultHtmlMatchKey(IntunePolicyResultEntry entry)
    {
        var scope = NormalizeScope(entry.Scope, entry.OmaUri);
        var settingName = NormalizePolicyFieldValue(entry.SettingName);
        var categoryPath = NormalizePolicyFieldValue(
            string.IsNullOrWhiteSpace(entry.GpoCategoryPath)
                ? string.IsNullOrWhiteSpace(entry.GpoPath)
                    ? entry.Area
                    : entry.GpoPath
                : entry.GpoCategoryPath).Replace('/', '\\');
        return $"{scope}|{categoryPath}|{settingName}";
    }

    private static string MergePolicyDetailText(string? existingText, string? newText)
    {
        var combined = new List<string>();
        AddLines(combined, existingText);
        AddLines(combined, newText);
        return string.Join("\n", combined);

        static void AddLines(List<string> target, string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return;
            }

            foreach (var line in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (target.Any(existing => string.Equals(existing, line, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                target.Add(line);
            }
        }
    }

    private static IReadOnlyList<IntunePolicyResultEntry> TryExtractPolicyEntriesFromPolicyManagerConfigSource(
        XDocument document,
        IReadOnlyDictionary<string, string> providerSourceLookup)
    {
        var winningProviderLookup = BuildCurrentPolicyWinningProviderLookup(document);
        var policyPathLookup = BuildPolicyMetadataPathLookup(document);
        var entries = new List<IntunePolicyResultEntry>();

        foreach (var configSource in document.Descendants().Where(element => element.Name.LocalName.Equals("ConfigSource", StringComparison.OrdinalIgnoreCase)))
        {
            var enrollmentId = NormalizePolicyProviderId(
                configSource.Elements().FirstOrDefault(element => element.Name.LocalName.Equals("EnrollmentId", StringComparison.OrdinalIgnoreCase))?.Value);
            var configSourceName = ResolvePolicySourceFromProviderId(enrollmentId, providerSourceLookup, fallback: "Mdm");

            foreach (var policyScope in configSource.Elements().Where(element => element.Name.LocalName.Equals("PolicyScope", StringComparison.OrdinalIgnoreCase)))
            {
                var scopeHint = policyScope.Elements().FirstOrDefault(element => element.Name.LocalName.Equals("PolicyScope", StringComparison.OrdinalIgnoreCase))?.Value;
                var normalizedScope = NormalizeScope(scopeHint, string.Empty);

                foreach (var areaNode in policyScope.Elements().Where(element => element.Name.LocalName.Equals("Area", StringComparison.OrdinalIgnoreCase)))
                {
                    var area = NormalizePolicyFieldValue(
                        areaNode.Elements().FirstOrDefault(element => element.Name.LocalName.Equals("PolicyAreaName", StringComparison.OrdinalIgnoreCase))?.Value);
                    if (string.IsNullOrWhiteSpace(area))
                    {
                        area = "General";
                    }

                    foreach (var settingNode in areaNode.Elements())
                    {
                        var settingName = settingNode.Name.LocalName;
                        if (settingName.Equals("PolicyAreaName", StringComparison.OrdinalIgnoreCase) ||
                            settingName.EndsWith("_LastWrite", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        var currentValue = NormalizePolicyFieldValue(settingNode.Value);
                        if (string.IsNullOrWhiteSpace(currentValue))
                        {
                            continue;
                        }

                        var winningProvider = ResolveWinningProvider(winningProviderLookup, scopeHint, normalizedScope, area, settingName);
                        var winningSource = ResolveWinningSourceFromProvider(
                            winningProvider,
                            enrollmentId,
                            configSourceName,
                            providerSourceLookup);
                        var status = ResolvePolicyManagerStatus(currentValue, winningProvider);
                        var mdmPath = BuildMdmPolicyPath(normalizedScope, area, settingName);
                        var path = ResolvePolicyPath(policyPathLookup, normalizedScope, area, settingName);

                        entries.Add(new IntunePolicyResultEntry(
                            normalizedScope,
                            area,
                            settingName,
                            path,
                            currentValue,
                            status,
                            string.Empty,
                            configSourceName,
                            winningSource,
                            false,
                            string.Empty,
                            mdmPath,
                            LooksLikeRegistryPolicyPath(path) ? path : string.Empty,
                            LooksLikeRegistryPolicyPath(path) ? DeriveGpoCategoryPath(path, winningSource) : string.Empty));
                    }
                }
            }
        }

        return DeduplicatePolicyEntries(entries);
    }

    private static string ResolveWinningProvider(
        IReadOnlyDictionary<string, string> winningProviderLookup,
        string? scopeHint,
        string normalizedScope,
        string area,
        string settingName)
    {
        if (winningProviderLookup.TryGetValue(CreatePolicyLookupKey(scopeHint, area, settingName), out var provider))
        {
            return provider;
        }

        return winningProviderLookup.TryGetValue(CreatePolicyLookupKey(normalizedScope, area, settingName), out provider)
            ? provider
            : string.Empty;
    }

    private static Dictionary<string, string> BuildPolicyProviderSourceLookup(IEnumerable<PolicyProviderPayload> providers)
    {
        var lookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in providers)
        {
            var providerId = NormalizePolicyProviderId(provider.ProviderId);
            if (string.IsNullOrWhiteSpace(providerId))
            {
                continue;
            }

            var source = NormalizePolicySource(provider.Source);
            if (string.IsNullOrWhiteSpace(source) ||
                string.Equals(source, "Unknown", StringComparison.OrdinalIgnoreCase))
            {
                source = NormalizePolicySource(provider.Name);
            }

            if (string.IsNullOrWhiteSpace(source))
            {
                continue;
            }

            lookup[providerId] = source;
        }

        return lookup;
    }

    private static string ResolvePolicySourceFromProviderId(
        string providerId,
        IReadOnlyDictionary<string, string> providerSourceLookup,
        string fallback = "Unknown")
    {
        if (!string.IsNullOrWhiteSpace(providerId) &&
            providerSourceLookup.TryGetValue(providerId, out var mapped) &&
            !string.IsNullOrWhiteSpace(mapped) &&
            !string.Equals(mapped, "Unknown", StringComparison.OrdinalIgnoreCase))
        {
            return mapped;
        }

        return fallback;
    }

    private static string NormalizePolicyProviderId(string? providerId)
    {
        var normalized = NormalizePolicyFieldValue(providerId)
            .Trim('{', '}');
        if (Guid.TryParse(normalized, out var guid))
        {
            return guid.ToString("D").ToUpperInvariant();
        }

        return string.Empty;
    }

    private static string ResolveWinningSourceFromProvider(
        string winningProvider,
        string enrollmentId,
        string configSourceName,
        IReadOnlyDictionary<string, string> providerSourceLookup)
    {
        var normalized = NormalizePolicyFieldValue(winningProvider);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return "Unknown";
        }

        var providerId = NormalizePolicyProviderId(normalized);
        if (!string.IsNullOrWhiteSpace(providerId))
        {
            if (providerSourceLookup.TryGetValue(providerId, out var mapped) && !string.IsNullOrWhiteSpace(mapped))
            {
                return mapped;
            }

            if (!string.IsNullOrWhiteSpace(enrollmentId) &&
                string.Equals(providerId, enrollmentId, StringComparison.OrdinalIgnoreCase))
            {
                return configSourceName;
            }
        }

        var lower = normalized.ToLowerInvariant();
        if (lower.Contains("group", StringComparison.Ordinal) ||
            lower.Contains("gpo", StringComparison.Ordinal))
        {
            return "GroupPolicy";
        }

        if (lower.Contains("local", StringComparison.Ordinal))
        {
            return "LocalPolicy";
        }

        if (lower.Contains("mdm", StringComparison.Ordinal) ||
            lower.Contains("csp", StringComparison.Ordinal) ||
            lower.Contains("intune", StringComparison.Ordinal))
        {
            return "Mdm";
        }

        if (lower.Contains("registry", StringComparison.Ordinal))
        {
            return "RegistryPolicy";
        }

        var normalizedConfigSource = NormalizePolicySource(configSourceName);
        if (!string.IsNullOrWhiteSpace(normalizedConfigSource) &&
            !string.Equals(normalizedConfigSource, "Unknown", StringComparison.OrdinalIgnoreCase))
        {
            return normalizedConfigSource;
        }

        return "Unknown";
    }

    private static string ResolvePolicyManagerStatus(string currentValue, string winningProvider)
    {
        if (currentValue.Contains("not configured", StringComparison.OrdinalIgnoreCase))
        {
            return "Unknown";
        }

        return string.IsNullOrWhiteSpace(winningProvider)
            ? "Unknown"
            : "Applied";
    }

    private static string BuildMdmPolicyPath(string normalizedScope, string area, string settingName)
    {
        if (string.Equals(normalizedScope, "Device", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalizedScope, "User", StringComparison.OrdinalIgnoreCase))
        {
            return $"./{normalizedScope}/Vendor/MSFT/Policy/Config/{area}/{settingName}";
        }

        return string.Empty;
    }

    private static string ResolvePolicyPath(
        IReadOnlyDictionary<string, string> policyPathLookup,
        string normalizedScope,
        string area,
        string settingName)
    {
        if (policyPathLookup.TryGetValue(CreatePolicyLookupKey(normalizedScope, area, settingName), out var path))
        {
            return path;
        }

        if (string.Equals(normalizedScope, "Device", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalizedScope, "User", StringComparison.OrdinalIgnoreCase))
        {
            return $"./{normalizedScope}/Vendor/MSFT/Policy/Config/{area}/{settingName}";
        }

        return string.Empty;
    }

    private static Dictionary<string, string> BuildCurrentPolicyWinningProviderLookup(XDocument document)
    {
        var lookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var scopeNode in document.Descendants().Where(element => element.Name.LocalName.Equals("currentPolicies", StringComparison.OrdinalIgnoreCase)))
        {
            var scope = NormalizePolicyFieldValue(
                scopeNode.Elements().FirstOrDefault(element => element.Name.LocalName.Equals("PolicyScope", StringComparison.OrdinalIgnoreCase))?.Value);
            var normalizedScope = NormalizeScope(scope, string.Empty);

            foreach (var currentValues in scopeNode.Elements().Where(element => element.Name.LocalName.Equals("CurrentPolicyValues", StringComparison.OrdinalIgnoreCase)))
            {
                var area = NormalizePolicyFieldValue(
                    currentValues.Elements().FirstOrDefault(element => element.Name.LocalName.Equals("PolicyAreaName", StringComparison.OrdinalIgnoreCase))?.Value);
                if (string.IsNullOrWhiteSpace(area))
                {
                    continue;
                }

                foreach (var child in currentValues.Elements())
                {
                    var keyName = child.Name.LocalName;
                    if (!keyName.EndsWith("_WinningProvider", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var settingName = keyName[..^"_WinningProvider".Length];
                    var provider = NormalizePolicyFieldValue(child.Value);
                    if (string.IsNullOrWhiteSpace(settingName) || string.IsNullOrWhiteSpace(provider))
                    {
                        continue;
                    }

                    lookup[CreatePolicyLookupKey(scope, area, settingName)] = provider;
                    lookup[CreatePolicyLookupKey(normalizedScope, area, settingName)] = provider;
                }
            }
        }

        return lookup;
    }

    private static Dictionary<string, string> BuildPolicyMetadataPathLookup(XDocument document)
    {
        var lookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var scopeHints = new[] { "Device", "User" };

        foreach (var areaNode in document.Descendants().Where(element => element.Name.LocalName.Equals("AreaMetadata", StringComparison.OrdinalIgnoreCase)))
        {
            var area = NormalizePolicyFieldValue(
                areaNode.Elements().FirstOrDefault(element => element.Name.LocalName.Equals("PolicyAreaName", StringComparison.OrdinalIgnoreCase))?.Value);
            if (string.IsNullOrWhiteSpace(area))
            {
                continue;
            }

            foreach (var metadata in areaNode.Elements().Where(element => element.Name.LocalName.Equals("PolicyMetadata", StringComparison.OrdinalIgnoreCase)))
            {
                var settingName = NormalizePolicyFieldValue(
                    metadata.Elements().FirstOrDefault(element => element.Name.LocalName.Equals("PolicyName", StringComparison.OrdinalIgnoreCase))?.Value);
                if (string.IsNullOrWhiteSpace(settingName))
                {
                    continue;
                }

                var path = ResolvePolicyMetadataPath(metadata);
                if (string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }

                foreach (var scope in scopeHints)
                {
                    lookup[CreatePolicyLookupKey(scope, area, settingName)] = path;
                }
            }
        }

        return lookup;
    }

    private static string ResolvePolicyMetadataPath(XElement metadata)
    {
        foreach (var child in metadata.Elements())
        {
            var name = child.Name.LocalName;
            var value = NormalizePolicyFieldValue(child.Value);
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            if (name.Contains("uri", StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
        }

        foreach (var candidate in new[] { "RegKeyPathRedirect", "grouppolicypath", "PolicyPath", "SettingPath" })
        {
            var value = NormalizePolicyFieldValue(
                metadata.Elements().FirstOrDefault(element => element.Name.LocalName.Equals(candidate, StringComparison.OrdinalIgnoreCase))?.Value);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return string.Empty;
    }

    private static string CreatePolicyLookupKey(string? scope, string area, string settingName)
        => $"{NormalizePolicyFieldValue(scope)}|{NormalizePolicyFieldValue(area)}|{NormalizePolicyFieldValue(settingName)}";

    private static IReadOnlyList<IntunePolicyResultEntry> TryExtractPolicyEntriesFromXml(
        string xmlPath,
        ICollection<string> warnings,
        IReadOnlyDictionary<string, string> providerSourceLookup)
    {
        if (string.IsNullOrWhiteSpace(xmlPath) || !File.Exists(xmlPath))
        {
            warnings.Add("MDMDiagReport.xml was not found for policy extraction.");
            return [];
        }

        try
        {
            var document = XDocument.Load(xmlPath, LoadOptions.PreserveWhitespace);
            var policyManagerEntries = TryExtractPolicyEntriesFromPolicyManagerConfigSource(document, providerSourceLookup);
            if (policyManagerEntries.Count > 0)
            {
                return policyManagerEntries;
            }

            var entries = new List<IntunePolicyResultEntry>();
            foreach (var node in document.Descendants().Where(element => element.HasElements))
            {
                var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var child in node.Elements().Where(element => !element.HasElements))
                {
                    var key = child.Name.LocalName;
                    var value = NormalizePolicyFieldValue(child.Value);
                    if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
                    {
                        continue;
                    }

                    if (map.TryGetValue(key, out var existing))
                    {
                        if (!existing.Contains(value, StringComparison.OrdinalIgnoreCase))
                        {
                            map[key] = $"{existing} | {value}";
                        }
                    }
                    else
                    {
                        map[key] = value;
                    }
                }

                var entry = NormalizePolicyEntry(map);
                if (entry is not null)
                {
                    entries.Add(entry);
                }
            }

            if (entries.Count == 0)
            {
                var xmlText = document.ToString(SaveOptions.DisableFormatting);
                foreach (Match match in OmaUriHintRegex().Matches(xmlText))
                {
                    var uri = match.Value.Trim();
                    if (string.IsNullOrWhiteSpace(uri))
                    {
                        continue;
                    }

                    entries.Add(new IntunePolicyResultEntry(
                        NormalizeScope(string.Empty, uri),
                        DeriveAreaFromOmaUri(uri),
                        DeriveSettingNameFromOmaUri(uri),
                        uri,
                        string.Empty,
                        "Unknown",
                        string.Empty,
                        "Mdm",
                        string.Empty,
                        false,
                        string.Empty,
                        uri));
                }
            }

            return DeduplicatePolicyEntries(entries);
        }
        catch (Exception ex)
        {
            warnings.Add($"XML policy extraction failed: {ex.Message}");
            return [];
        }
    }

    private static IReadOnlyList<IntunePolicyResultEntry> TryExtractPolicyEntriesFromHtml(string htmlPath, ICollection<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(htmlPath) || !File.Exists(htmlPath))
        {
            warnings.Add("MDM HTML report was not found for policy fallback extraction.");
            return [];
        }

        try
        {
            var html = File.ReadAllText(htmlPath, Encoding.UTF8);
            var entries = new List<IntunePolicyResultEntry>();
            List<string>? headers = null;
            foreach (Match rowMatch in HtmlRowRegex().Matches(html))
            {
                var rowHtml = rowMatch.Groups["row"].Value;
                if (string.IsNullOrWhiteSpace(rowHtml))
                {
                    continue;
                }

                var cells = HtmlCellRegex()
                    .Matches(rowHtml)
                    .Select(match => NormalizePolicyFieldValue(WebUtility.HtmlDecode(HtmlTagRegex().Replace(match.Groups["cell"].Value, " "))))
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .ToList();
                if (cells.Count == 0)
                {
                    continue;
                }

                if (headers is null && LooksLikeHeaderRow(cells))
                {
                    headers = cells.Select(NormalizeHeaderKey).ToList();
                    continue;
                }

                var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (headers is not null && headers.Count >= 2)
                {
                    var len = Math.Min(headers.Count, cells.Count);
                    for (var i = 0; i < len; i++)
                    {
                        if (!string.IsNullOrWhiteSpace(headers[i]))
                        {
                            map[headers[i]] = cells[i];
                        }
                    }
                }
                else
                {
                    if (cells.Count >= 1) map["SettingName"] = cells[0];
                    if (cells.Count >= 2) map["OmaUri"] = cells[1];
                    if (cells.Count >= 3) map["CurrentValue"] = cells[2];
                    if (cells.Count >= 4) map["Status"] = cells[3];
                    if (cells.Count >= 5) map["ResultCode"] = cells[4];
                    if (cells.Count >= 6) map["Scope"] = cells[5];
                }

                var entry = NormalizePolicyEntry(map);
                if (entry is not null)
                {
                    entries.Add(entry);
                }
            }

            if (entries.Count == 0)
            {
                foreach (Match match in OmaUriHintRegex().Matches(html))
                {
                    var uri = match.Value.Trim();
                    if (string.IsNullOrWhiteSpace(uri))
                    {
                        continue;
                    }

                    entries.Add(new IntunePolicyResultEntry(
                        NormalizeScope(string.Empty, uri),
                        DeriveAreaFromOmaUri(uri),
                        DeriveSettingNameFromOmaUri(uri),
                        uri,
                        string.Empty,
                        "Unknown",
                        string.Empty,
                        "Mdm",
                        string.Empty,
                        false,
                        string.Empty,
                        uri));
                }
            }

            return DeduplicatePolicyEntries(entries);
        }
        catch (Exception ex)
        {
            warnings.Add($"HTML policy extraction failed: {ex.Message}");
            return [];
        }
    }

    private static IReadOnlyList<IntunePolicyResultEntry> TryExtractPolicyEntriesFromGpResultHtml(string gpResultHtmlPath, ICollection<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(gpResultHtmlPath) || !File.Exists(gpResultHtmlPath))
        {
            return [];
        }

        try
        {
            var html = ReadTextWithDetectedEncoding(gpResultHtmlPath);
            if (string.IsNullOrWhiteSpace(html))
            {
                return [];
            }

            var sanitizedHtml = NormalizeGpResultHtmlForXmlParsing(html);
            var document = XDocument.Parse(sanitizedHtml, LoadOptions.PreserveWhitespace);
            var settingsRoot = document.Descendants()
                .FirstOrDefault(element =>
                    element.Name.LocalName.Equals("div", StringComparison.OrdinalIgnoreCase) &&
                    HasCssClass(element, "rsopsettings"));
            if (settingsRoot is null)
            {
                return [];
            }

            var entries = new List<IntunePolicyResultEntry>();
            ParseGpResultHtmlElements(settingsRoot.Elements().ToList(), scope: null, [], entries);
            return DeduplicatePolicyEntries(entries);
        }
        catch (Exception ex)
        {
            warnings.Add($"gpresult HTML extraction failed: {ex.Message}");
            return [];
        }
    }

    private static void ParseGpResultHtmlElements(
        IReadOnlyList<XElement> elements,
        string? scope,
        List<string> path,
        List<IntunePolicyResultEntry> entries)
    {
        for (var index = 0; index < elements.Count; index++)
        {
            var element = elements[index];
            if (TryGetGpResultSectionTitle(element, out var title))
            {
                var normalizedTitle = NormalizeGpResultHtmlText(title);
                var nextElement = index + 1 < elements.Count ? elements[index + 1] : null;
                if (TryResolveGpResultScope(normalizedTitle, out var resolvedScope))
                {
                    if (nextElement is not null && IsGpResultContainer(nextElement))
                    {
                        ParseGpResultHtmlElements(nextElement.Elements().ToList(), resolvedScope, [], entries);
                        index++;
                    }

                    continue;
                }

                if (string.IsNullOrWhiteSpace(scope))
                {
                    continue;
                }

                path.Add(normalizedTitle);
                if (nextElement is not null && IsGpResultContainer(nextElement))
                {
                    ParseGpResultHtmlElements(nextElement.Elements().ToList(), scope, path, entries);
                    index++;
                }
                else
                {
                    ParseGpResultHtmlElement(element, scope, path, entries);
                }

                path.RemoveAt(path.Count - 1);
                continue;
            }

            ParseGpResultHtmlElement(element, scope, path, entries);
        }
    }

    private static void ParseGpResultHtmlElement(
        XElement element,
        string? scope,
        IReadOnlyList<string> path,
        List<IntunePolicyResultEntry> entries)
    {
        if (string.IsNullOrWhiteSpace(scope))
        {
            return;
        }

        if (element.Name.LocalName.Equals("table", StringComparison.OrdinalIgnoreCase))
        {
            ParseGpResultHtmlTable(element, scope, path, entries);
            return;
        }

        foreach (var child in element.Elements())
        {
            if (TryGetGpResultSectionTitle(child, out _))
            {
                continue;
            }

            ParseGpResultHtmlElement(child, scope, path, entries);
        }
    }

    private static void ParseGpResultHtmlTable(
        XElement table,
        string scope,
        IReadOnlyList<string> path,
        List<IntunePolicyResultEntry> entries)
    {
        if (!ShouldParseGpResultPolicyTable(path, table))
        {
            return;
        }

        var rows = GetDirectTableRows(table);
        if (rows.Count == 0)
        {
            return;
        }

        var headerRow = rows
            .FirstOrDefault(row => row.Elements().Any(cell => cell.Name.LocalName.Equals("th", StringComparison.OrdinalIgnoreCase)));
        var headers = headerRow is null
            ? []
            : headerRow.Elements()
                .Where(cell =>
                    cell.Name.LocalName.Equals("th", StringComparison.OrdinalIgnoreCase) ||
                    cell.Name.LocalName.Equals("td", StringComparison.OrdinalIgnoreCase))
                .Select(ExtractGpResultHtmlCellText)
                .ToArray();

        var gpoColumnIndex = FindGpResultHeaderIndex(headers, "ausschlaggebendes gruppenrichtlinienobjekt", "group policy object", "gpo");
        var nameColumnIndex = FindGpResultHeaderIndex(headers, "richtlinie", "policy", "name", "dienst", "gruppe");
        var valueColumnIndex = FindGpResultHeaderIndex(headers, "einstellung", "value", "parameter", "mitglieder", "mitglied von", "startmodus");

        IntunePolicyResultEntry? lastEntry = null;
        foreach (var row in rows)
        {
            if (ReferenceEquals(row, headerRow))
            {
                continue;
            }

            var cells = row.Elements()
                .Where(cell =>
                    cell.Name.LocalName.Equals("td", StringComparison.OrdinalIgnoreCase) ||
                    cell.Name.LocalName.Equals("th", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (cells.Length == 0)
            {
                continue;
            }

            if (cells.Length == 1)
            {
                var detailText = BuildGpResultHtmlDetailText(cells[0].Descendants().Where(element => element.Name.LocalName.Equals("table", StringComparison.OrdinalIgnoreCase)));
                if (lastEntry is not null && !string.IsNullOrWhiteSpace(detailText))
                {
                    var updatedEntry = lastEntry with
                    {
                        AdditionalDetails = MergePolicyDetailText(lastEntry.AdditionalDetails, detailText)
                    };
                    entries[^1] = updatedEntry;
                    lastEntry = updatedEntry;
                }

                continue;
            }

            var sourceName = gpoColumnIndex >= 0 && gpoColumnIndex < cells.Length
                ? ExtractGpResultHtmlCellText(cells[gpoColumnIndex])
                : string.Empty;
            var explicitPath = cells
                .SelectMany(cell => cell.DescendantsAndSelf().Where(element => element.Name.LocalName.Equals("span", StringComparison.OrdinalIgnoreCase)))
                .Select(span => NormalizeGpResultHtmlPath(span.Attribute("gpmc_settingPath")?.Value))
                .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value))
                ?? string.Empty;
            var settingName = cells
                .SelectMany(cell => cell.DescendantsAndSelf().Where(element => element.Name.LocalName.Equals("span", StringComparison.OrdinalIgnoreCase)))
                .Select(span => NormalizeGpResultHtmlText(span.Attribute("gpmc_settingName")?.Value))
                .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value))
                ?? string.Empty;
            if (string.IsNullOrWhiteSpace(settingName))
            {
                var fallbackIndex = nameColumnIndex >= 0 && nameColumnIndex < cells.Length ? nameColumnIndex : 0;
                settingName = ExtractGpResultHtmlCellText(cells[fallbackIndex]);
            }

            if (string.IsNullOrWhiteSpace(settingName))
            {
                continue;
            }

            var currentValue = ExtractGpResultHtmlPrimaryValue(cells, headers, nameColumnIndex, valueColumnIndex, gpoColumnIndex);
            var detailsText = BuildGpResultHtmlDetailText(cells.SelectMany(cell => cell.Descendants().Where(element => element.Name.LocalName.Equals("table", StringComparison.OrdinalIgnoreCase))));
            var gpoPath = BuildGpResultHtmlCategoryPath(scope, path, explicitPath);
            var area = DeriveAreaFromGpResultHtmlCategoryPath(gpoPath);
            var source = ResolveGpResultSourceFromGpoName(sourceName);

            var entry = new IntunePolicyResultEntry(
                scope,
                area,
                settingName,
                string.IsNullOrWhiteSpace(explicitPath) ? gpoPath : explicitPath,
                currentValue,
                "Applied",
                string.Empty,
                source,
                source,
                false,
                string.Empty,
                string.Empty,
                gpoPath,
                gpoPath,
                detailsText);

            entries.Add(entry);
            lastEntry = entry;
        }
    }

    private static bool ShouldParseGpResultPolicyTable(IReadOnlyList<string> path, XElement table)
    {
        if (table.Ancestors().Any(ancestor =>
            ancestor.Name.LocalName.Equals("table", StringComparison.OrdinalIgnoreCase) &&
            !ReferenceEquals(ancestor, table)))
        {
            return false;
        }

        if (path.Count == 0)
        {
            return false;
        }

        return path.Any(segment =>
                   segment.Contains("Richtlinien", StringComparison.OrdinalIgnoreCase) ||
                   segment.Contains("Administrative Vorlagen", StringComparison.OrdinalIgnoreCase) ||
                   segment.Contains("Windows-Einstellungen", StringComparison.OrdinalIgnoreCase) ||
                   segment.Contains("Sicherheitseinstellungen", StringComparison.OrdinalIgnoreCase))
               || table.Descendants().Any(element => !string.IsNullOrWhiteSpace(element.Attribute("gpmc_settingPath")?.Value));
    }

    private static List<XElement> GetDirectTableRows(XElement table)
    {
        var rows = new List<XElement>();
        foreach (var child in table.Elements())
        {
            if (child.Name.LocalName.Equals("tr", StringComparison.OrdinalIgnoreCase))
            {
                rows.Add(child);
                continue;
            }

            if (child.Name.LocalName.Equals("thead", StringComparison.OrdinalIgnoreCase) ||
                child.Name.LocalName.Equals("tbody", StringComparison.OrdinalIgnoreCase) ||
                child.Name.LocalName.Equals("tfoot", StringComparison.OrdinalIgnoreCase))
            {
                rows.AddRange(child.Elements().Where(element => element.Name.LocalName.Equals("tr", StringComparison.OrdinalIgnoreCase)));
            }
        }

        return rows;
    }

    private static int FindGpResultHeaderIndex(IReadOnlyList<string> headers, params string[] candidates)
    {
        for (var index = 0; index < headers.Count; index++)
        {
            var header = headers[index];
            if (candidates.Any(candidate => header.Contains(candidate, StringComparison.OrdinalIgnoreCase)))
            {
                return index;
            }
        }

        return -1;
    }

    private static string ExtractGpResultHtmlPrimaryValue(
        IReadOnlyList<XElement> cells,
        IReadOnlyList<string> headers,
        int nameColumnIndex,
        int valueColumnIndex,
        int gpoColumnIndex)
    {
        if (valueColumnIndex >= 0 && valueColumnIndex < cells.Count)
        {
            return ExtractGpResultHtmlCellText(cells[valueColumnIndex]);
        }

        var values = new List<string>();
        for (var index = 0; index < cells.Count; index++)
        {
            if (index == nameColumnIndex || index == gpoColumnIndex)
            {
                continue;
            }

            var text = ExtractGpResultHtmlCellText(cells[index]);
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            var header = index < headers.Count ? headers[index] : string.Empty;
            values.Add(string.IsNullOrWhiteSpace(header) ? text : $"{header}: {text}");
        }

        return values.Count == 0 ? string.Empty : string.Join(" | ", values);
    }

    private static string BuildGpResultHtmlDetailText(IEnumerable<XElement> tables)
    {
        var lines = new List<string>();
        foreach (var table in tables)
        {
            var rows = GetDirectTableRows(table);
            if (rows.Count == 0)
            {
                continue;
            }

            var headerRow = rows
                .FirstOrDefault(row => row.Elements().Any(cell => cell.Name.LocalName.Equals("th", StringComparison.OrdinalIgnoreCase)));
            var headers = headerRow is null
                ? []
                : headerRow.Elements()
                    .Where(cell =>
                        cell.Name.LocalName.Equals("th", StringComparison.OrdinalIgnoreCase) ||
                        cell.Name.LocalName.Equals("td", StringComparison.OrdinalIgnoreCase))
                    .Select(ExtractGpResultHtmlCellText)
                    .ToArray();

            foreach (var row in rows)
            {
                if (ReferenceEquals(row, headerRow))
                {
                    continue;
                }

                var values = row.Elements()
                    .Where(cell =>
                        cell.Name.LocalName.Equals("td", StringComparison.OrdinalIgnoreCase) ||
                        cell.Name.LocalName.Equals("th", StringComparison.OrdinalIgnoreCase))
                    .Select(ExtractGpResultHtmlCellText)
                    .Where(static value => !string.IsNullOrWhiteSpace(value))
                    .ToArray();
                if (values.Length == 0)
                {
                    continue;
                }

                string line;
                if (headers.Length == values.Length && headers.Length > 0 && headers.Any(static header => !string.IsNullOrWhiteSpace(header)))
                {
                    line = string.Join(" | ", headers.Zip(values, (header, value) => $"{header}: {value}"));
                }
                else if (values.Length == 2)
                {
                    line = $"{values[0]} = {values[1]}";
                }
                else
                {
                    line = string.Join(" | ", values);
                }

                if (lines.Any(existing => string.Equals(existing, line, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                lines.Add(line);
            }
        }

        return string.Join("\n", lines);
    }

    private static string ExtractGpResultHtmlCellText(XElement cell)
    {
        var builder = new StringBuilder();
        AppendGpResultHtmlVisibleText(cell.Nodes(), builder, skipNestedTables: true);
        return NormalizeGpResultHtmlText(builder.ToString());
    }

    private static void AppendGpResultHtmlVisibleText(IEnumerable<XNode> nodes, StringBuilder builder, bool skipNestedTables)
    {
        foreach (var node in nodes)
        {
            if (node is XText text)
            {
                builder.Append(text.Value);
                continue;
            }

            if (node is not XElement element)
            {
                continue;
            }

            if (skipNestedTables && element.Name.LocalName.Equals("table", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (element.Name.LocalName.Equals("br", StringComparison.OrdinalIgnoreCase))
            {
                builder.Append('\n');
                continue;
            }

            AppendGpResultHtmlVisibleText(element.Nodes(), builder, skipNestedTables);
            if (element.Name.LocalName.Equals("td", StringComparison.OrdinalIgnoreCase) ||
                element.Name.LocalName.Equals("th", StringComparison.OrdinalIgnoreCase) ||
                element.Name.LocalName.Equals("div", StringComparison.OrdinalIgnoreCase))
            {
                builder.Append(' ');
            }
        }
    }

    private static string BuildGpResultHtmlCategoryPath(string scope, IReadOnlyList<string> path, string explicitPath)
    {
        var normalizedExplicitPath = NormalizeGpResultHtmlPath(explicitPath);
        if (!string.IsNullOrWhiteSpace(normalizedExplicitPath))
        {
            return normalizedExplicitPath;
        }

        var prefix = string.Equals(scope, "User", StringComparison.OrdinalIgnoreCase)
            ? "Benutzerkonfiguration"
            : "Computerkonfiguration";
        var normalizedPath = path
            .Select(NormalizeGpResultHtmlText)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .ToList();
        var settingsIndex = normalizedPath.FindIndex(segment => segment.Equals("Einstellungen", StringComparison.OrdinalIgnoreCase));
        if (settingsIndex >= 0)
        {
            normalizedPath = normalizedPath[(settingsIndex + 1)..];
        }

        return normalizedPath.Count == 0
            ? prefix
            : prefix + "\\" + string.Join("\\", normalizedPath);
    }

    private static string DeriveAreaFromGpResultHtmlCategoryPath(string categoryPath)
    {
        var normalized = NormalizeGpResultHtmlPath(categoryPath);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return "General";
        }

        var parts = normalized.Split('\\', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length == 0 ? "General" : parts[^1];
    }

    private static bool TryGetGpResultSectionTitle(XElement element, out string title)
    {
        title = string.Empty;
        if (!element.Name.LocalName.Equals("div", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var className = element.Attribute("class")?.Value;
        if (string.IsNullOrWhiteSpace(className) ||
            !className.StartsWith("he", StringComparison.OrdinalIgnoreCase) ||
            className.EndsWith("i", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var titleSpan = element.Descendants()
            .FirstOrDefault(descendant =>
                descendant.Name.LocalName.Equals("span", StringComparison.OrdinalIgnoreCase) &&
                HasCssClass(descendant, "sectionTitle"));
        if (titleSpan is null)
        {
            return false;
        }

        title = NormalizeGpResultHtmlText(titleSpan.Value);
        return !string.IsNullOrWhiteSpace(title);
    }

    private static bool TryResolveGpResultScope(string title, out string scope)
    {
        if (title.Contains("Computerdetails", StringComparison.OrdinalIgnoreCase))
        {
            scope = "Device";
            return true;
        }

        if (title.Contains("Benutzerdetails", StringComparison.OrdinalIgnoreCase))
        {
            scope = "User";
            return true;
        }

        scope = string.Empty;
        return false;
    }

    private static bool IsGpResultContainer(XElement element)
        => element.Name.LocalName.Equals("div", StringComparison.OrdinalIgnoreCase) && HasCssClass(element, "container");

    private static bool HasCssClass(XElement element, string className)
    {
        var value = element.Attribute("class")?.Value;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(candidate => string.Equals(candidate, className, StringComparison.OrdinalIgnoreCase));
    }

    private static string ReadTextWithDetectedEncoding(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static string NormalizeGpResultHtmlForXmlParsing(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return string.Empty;
        }

        var normalized = html.TrimStart('\uFEFF');
        return Regex.Replace(
            normalized,
            @"&(?<name>[A-Za-z][A-Za-z0-9]+);",
            static match =>
            {
                var entityName = match.Groups["name"].Value;
                if (entityName.Equals("amp", StringComparison.OrdinalIgnoreCase) ||
                    entityName.Equals("lt", StringComparison.OrdinalIgnoreCase) ||
                    entityName.Equals("gt", StringComparison.OrdinalIgnoreCase) ||
                    entityName.Equals("quot", StringComparison.OrdinalIgnoreCase) ||
                    entityName.Equals("apos", StringComparison.OrdinalIgnoreCase))
                {
                    return match.Value;
                }

                var decoded = WebUtility.HtmlDecode(match.Value);
                if (string.Equals(decoded, match.Value, StringComparison.Ordinal))
                {
                    return "&amp;" + entityName + ";";
                }

                var builder = new StringBuilder();
                foreach (var ch in decoded)
                {
                    builder.Append("&#");
                    builder.Append((int)ch);
                    builder.Append(';');
                }

                return builder.ToString();
            },
            RegexOptions.CultureInvariant);
    }

    private static string NormalizeGpResultHtmlText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var decoded = WebUtility.HtmlDecode(value);
        decoded = decoded.Replace('\u00A0', ' ');
        decoded = Regex.Replace(decoded, @"\s+", " ");
        return decoded.Trim();
    }

    private static string NormalizeGpResultHtmlPath(string? value)
    {
        var normalized = NormalizeGpResultHtmlText(value).Replace('/', '\\');
        return normalized.Trim('\\');
    }

    private static IReadOnlyList<IntunePolicyResultEntry> TryExtractPolicyEntriesFromGpResultXml(
        string gpResultXmlPath,
        ICollection<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(gpResultXmlPath) || !File.Exists(gpResultXmlPath))
        {
            return [];
        }

        try
        {
            var document = XDocument.Load(gpResultXmlPath, LoadOptions.PreserveWhitespace);
            var entries = new List<IntunePolicyResultEntry>();
            var root = document.Root;
            if (root is null)
            {
                return [];
            }

            foreach (var scopeNode in root.Elements())
            {
                var scopeName = scopeNode.Name.LocalName;
                string scope;
                if (scopeName.Equals("ComputerResults", StringComparison.OrdinalIgnoreCase))
                {
                    scope = "Device";
                }
                else if (scopeName.Equals("UserResults", StringComparison.OrdinalIgnoreCase))
                {
                    scope = "User";
                }
                else
                {
                    continue;
                }

                var gpoSourceLookup = BuildGpResultGpoSourceLookup(scopeNode);
                foreach (var setting in scopeNode.Descendants().Where(static element => element.Name.LocalName.Equals("RegistryRsopSetting", StringComparison.OrdinalIgnoreCase)))
                {
                    var gpoId = NormalizeGpResultProviderIdentifier(
                        setting.Elements()
                            .FirstOrDefault(static element => element.Name.LocalName.Equals("GPO", StringComparison.OrdinalIgnoreCase))?
                            .Descendants()
                            .FirstOrDefault(static element => element.Name.LocalName.Equals("Identifier", StringComparison.OrdinalIgnoreCase))?
                            .Value);

                    var baseInstanceXml = setting.Elements()
                        .FirstOrDefault(static element => element.Name.LocalName.Equals("BaseInstanceXml", StringComparison.OrdinalIgnoreCase));
                    if (baseInstanceXml is null)
                    {
                        continue;
                    }

                    var propertyRoot = baseInstanceXml.Descendants()
                        .FirstOrDefault(static element => element.Name.LocalName.Equals("INSTANCE", StringComparison.OrdinalIgnoreCase))
                        ?? baseInstanceXml;
                    var settingName = GetGpResultPropertyValue(propertyRoot, "polmkrNameResolved", "polmkrName", "name");
                    if (string.IsNullOrWhiteSpace(settingName) && !ReferenceEquals(propertyRoot, baseInstanceXml))
                    {
                        settingName = GetGpResultPropertyValue(baseInstanceXml, "polmkrNameResolved", "polmkrName", "name");
                    }

                    var hive = GetGpResultPropertyValue(propertyRoot, "polmkrHiveResolved", "polmkrHive");
                    if (string.IsNullOrWhiteSpace(hive) && !ReferenceEquals(propertyRoot, baseInstanceXml))
                    {
                        hive = GetGpResultPropertyValue(baseInstanceXml, "polmkrHiveResolved", "polmkrHive");
                    }

                    var key = GetGpResultPropertyValue(propertyRoot, "polmkrKeyResolved", "polmkrKey");
                    if (string.IsNullOrWhiteSpace(key) && !ReferenceEquals(propertyRoot, baseInstanceXml))
                    {
                        key = GetGpResultPropertyValue(baseInstanceXml, "polmkrKeyResolved", "polmkrKey");
                    }

                    var value = GetGpResultPropertyValue(propertyRoot, "polmkrValueResolved", "polmkrValue", "value");
                    if (string.IsNullOrWhiteSpace(value) && !ReferenceEquals(propertyRoot, baseInstanceXml))
                    {
                        value = GetGpResultPropertyValue(baseInstanceXml, "polmkrValueResolved", "polmkrValue", "value");
                    }

                    var normalizedPath = NormalizeGpResultRegistryPath(hive, key);
                    if (string.IsNullOrWhiteSpace(normalizedPath) || string.IsNullOrWhiteSpace(value))
                    {
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(settingName))
                    {
                        settingName = "(Default)";
                    }

                    var gpoDisplayName = GetGpResultPropertyValue(baseInstanceXml, "polmkrBaseGpoDisplayName", "polmkrBaseGpoName");
                    var source = !string.IsNullOrWhiteSpace(gpoDisplayName)
                        ? ResolveGpResultSourceFromGpoName(gpoDisplayName)
                        : (!string.IsNullOrWhiteSpace(gpoId) && gpoSourceLookup.TryGetValue(gpoId, out var mappedSource)
                            ? mappedSource
                            : "GroupPolicy");

                    var resultCode = GetGpResultPropertyValue(propertyRoot, "polmkrClassResultCode");
                    if (string.IsNullOrWhiteSpace(resultCode) && !ReferenceEquals(propertyRoot, baseInstanceXml))
                    {
                        resultCode = GetGpResultPropertyValue(baseInstanceXml, "polmkrClassResultCode");
                    }

                    var resultCodeValue = GetGpResultPropertyValue(propertyRoot, "polmkrClassResultCodeValue");
                    if (string.IsNullOrWhiteSpace(resultCodeValue) && !ReferenceEquals(propertyRoot, baseInstanceXml))
                    {
                        resultCodeValue = GetGpResultPropertyValue(baseInstanceXml, "polmkrClassResultCodeValue");
                    }

                    var normalizedResultCode = NormalizeResultCode(resultCode);
                    var status = IsGpResultSuccessCode(normalizedResultCode) && IsGpResultSuccessCode(resultCodeValue)
                        ? "Applied"
                        : "Failed";
                    if (string.Equals(status, "Applied", StringComparison.OrdinalIgnoreCase))
                    {
                        normalizedResultCode = string.Empty;
                    }
                    else if (string.IsNullOrWhiteSpace(normalizedResultCode))
                    {
                        normalizedResultCode = NormalizeResultCode(resultCodeValue);
                    }

                    entries.Add(new IntunePolicyResultEntry(
                        scope,
                        DeriveAreaFromOmaUri(normalizedPath),
                        settingName,
                        normalizedPath,
                        value,
                        status,
                        normalizedResultCode,
                        source,
                        source,
                        false,
                        string.Empty,
                        string.Empty,
                        normalizedPath,
                        DeriveGpoCategoryPath(normalizedPath, source)));
                }

                foreach (var setting in scopeNode.Descendants().Where(static element => element.Name.LocalName.Equals("RegistrySetting", StringComparison.OrdinalIgnoreCase)))
                {
                    var keyPath = NormalizePolicyFieldValue(
                        setting.Elements().FirstOrDefault(static element => element.Name.LocalName.Equals("KeyPath", StringComparison.OrdinalIgnoreCase))?.Value);
                    var normalizedPath = NormalizeGpResultRegistryPathByScope(scope, keyPath);
                    if (string.IsNullOrWhiteSpace(normalizedPath))
                    {
                        continue;
                    }

                    var source = ResolveGpResultSourceForSetting(setting, gpoSourceLookup);
                    var area = DeriveAreaFromOmaUri(normalizedPath);
                    foreach (var valueNode in setting.Elements().Where(static element => element.Name.LocalName.Equals("Value", StringComparison.OrdinalIgnoreCase)))
                    {
                        var settingName = NormalizePolicyFieldValue(
                            valueNode.Elements().FirstOrDefault(static element => element.Name.LocalName.Equals("Name", StringComparison.OrdinalIgnoreCase))?.Value);
                        if (string.IsNullOrWhiteSpace(settingName))
                        {
                            settingName = "(Default)";
                        }

                        var value = ResolveGpResultRegistryValueText(valueNode);
                        if (string.IsNullOrWhiteSpace(value))
                        {
                            continue;
                        }

                        entries.Add(new IntunePolicyResultEntry(
                            scope,
                            area,
                            settingName,
                            normalizedPath,
                            value,
                            "Applied",
                            string.Empty,
                            source,
                            source,
                            false,
                            string.Empty,
                            string.Empty,
                            normalizedPath,
                        DeriveGpoCategoryPath(normalizedPath, source)));
                    }
                }

                foreach (var policy in scopeNode.Descendants().Where(static element => element.Name.LocalName.Equals("Policy", StringComparison.OrdinalIgnoreCase)))
                {
                    var settingName = NormalizePolicyFieldValue(
                        policy.Elements().FirstOrDefault(static element => element.Name.LocalName.Equals("Name", StringComparison.OrdinalIgnoreCase))?.Value);
                    if (string.IsNullOrWhiteSpace(settingName))
                    {
                        continue;
                    }

                    var category = NormalizePolicyFieldValue(
                        policy.Elements().FirstOrDefault(static element => element.Name.LocalName.Equals("Category", StringComparison.OrdinalIgnoreCase))?.Value);
                    var categoryPath = BuildGpResultXmlCategoryPath(scope, category);
                    var source = ResolveGpResultSourceForSetting(policy, gpoSourceLookup);
                    var state = NormalizePolicyFieldValue(
                        policy.Elements().FirstOrDefault(static element => element.Name.LocalName.Equals("State", StringComparison.OrdinalIgnoreCase))?.Value);
                    var detailsText = BuildGpResultXmlPolicyDetailText(policy);
                    var resolvedPolicy = TryResolveAdmxPolicyDefinition(new IntunePolicyResultEntry(
                        scope,
                        DeriveAreaFromGpResultHtmlCategoryPath(categoryPath),
                        settingName,
                        categoryPath,
                        state,
                        "Applied",
                        string.Empty,
                        source,
                        source,
                        false,
                        string.Empty,
                        string.Empty,
                        categoryPath,
                        categoryPath,
                        detailsText));
                    var comparisonPath = resolvedPolicy is null
                        ? categoryPath
                        : NormalizeGpResultRegistryPathByScope(scope, resolvedPolicy.KeyPath);

                    entries.Add(new IntunePolicyResultEntry(
                        scope,
                        DeriveAreaFromGpResultHtmlCategoryPath(categoryPath),
                        settingName,
                        string.IsNullOrWhiteSpace(comparisonPath) ? categoryPath : comparisonPath,
                        state,
                        "Applied",
                        string.Empty,
                        source,
                        source,
                        false,
                        string.Empty,
                        string.Empty,
                        categoryPath,
                        categoryPath,
                        detailsText));
                }
            }

            return DeduplicatePolicyEntries(entries);
        }
        catch (Exception ex)
        {
            warnings.Add($"gpresult XML fallback extraction failed: {ex.Message}");
            return [];
        }
    }

    private static string GetGpResultPropertyValue(XElement? node, params string[] propertyNames)
    {
        if (node is null || propertyNames.Length == 0)
        {
            return string.Empty;
        }

        foreach (var propertyName in propertyNames)
        {
            if (string.IsNullOrWhiteSpace(propertyName))
            {
                continue;
            }

            var value = node.Descendants()
                .FirstOrDefault(element =>
                    element.Name.LocalName.Equals("PROPERTY", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(element.Attribute("NAME")?.Value, propertyName, StringComparison.OrdinalIgnoreCase))
                ?.Descendants()
                .FirstOrDefault(element => element.Name.LocalName.Equals("VALUE", StringComparison.OrdinalIgnoreCase))
                ?.Value;
            value = NormalizePolicyFieldValue(value);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return string.Empty;
    }

    private static string BuildGpResultXmlCategoryPath(string scope, string category)
    {
        var normalizedCategory = NormalizePolicyFieldValue(category).Replace('/', '\\');
        var prefix = string.Equals(scope, "User", StringComparison.OrdinalIgnoreCase)
            ? @"Benutzerkonfiguration\Administrative Vorlagen"
            : @"Computerkonfiguration\Administrative Vorlagen";
        return string.IsNullOrWhiteSpace(normalizedCategory)
            ? prefix
            : prefix + "\\" + normalizedCategory.Trim('\\');
    }

    private static string BuildGpResultXmlPolicyDetailText(XElement policy)
    {
        var lines = new List<string>();
        foreach (var child in policy.Elements())
        {
            var localName = child.Name.LocalName;
            if (localName.Equals("GPO", StringComparison.OrdinalIgnoreCase) ||
                localName.Equals("Precedence", StringComparison.OrdinalIgnoreCase) ||
                localName.Equals("Name", StringComparison.OrdinalIgnoreCase) ||
                localName.Equals("State", StringComparison.OrdinalIgnoreCase) ||
                localName.Equals("Explain", StringComparison.OrdinalIgnoreCase) ||
                localName.Equals("Supported", StringComparison.OrdinalIgnoreCase) ||
                localName.Equals("Category", StringComparison.OrdinalIgnoreCase) ||
                localName.Equals("Text", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var label = NormalizePolicyFieldValue(
                child.Elements().FirstOrDefault(static element => element.Name.LocalName.Equals("Name", StringComparison.OrdinalIgnoreCase))?.Value);
            var value = NormalizePolicyFieldValue(
                child.Elements().FirstOrDefault(static element => element.Name.LocalName.Equals("Value", StringComparison.OrdinalIgnoreCase))?.Value);
            if (string.IsNullOrWhiteSpace(value))
            {
                value = NormalizePolicyFieldValue(
                    child.Elements().FirstOrDefault(static element => element.Name.LocalName.Equals("State", StringComparison.OrdinalIgnoreCase))?.Value);
            }

            string line;
            if (!string.IsNullOrWhiteSpace(label) && !string.IsNullOrWhiteSpace(value))
            {
                line = $"{label} = {value}";
            }
            else if (!string.IsNullOrWhiteSpace(label))
            {
                line = label;
            }
            else
            {
                line = NormalizePolicyFieldValue(child.Value);
            }

            if (string.IsNullOrWhiteSpace(line) ||
                lines.Any(existing => string.Equals(existing, line, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            lines.Add(line);
        }

        return string.Join("\n", lines);
    }

    private static Dictionary<string, string> BuildGpResultGpoSourceLookup(XElement scopeNode)
    {
        var lookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var gpoNode in scopeNode.Elements().Where(static element => element.Name.LocalName.Equals("GPO", StringComparison.OrdinalIgnoreCase)))
        {
            var identifier = NormalizeGpResultProviderIdentifier(
                gpoNode.Descendants().FirstOrDefault(static element => element.Name.LocalName.Equals("Identifier", StringComparison.OrdinalIgnoreCase))?.Value);
            if (string.IsNullOrWhiteSpace(identifier))
            {
                continue;
            }

            var displayName = NormalizePolicyFieldValue(
                gpoNode.Descendants().FirstOrDefault(static element => element.Name.LocalName.Equals("Name", StringComparison.OrdinalIgnoreCase))?.Value);
            lookup[identifier] = ResolveGpResultSourceFromGpoName(displayName);
        }

        return lookup;
    }

    private static string ResolveGpResultSourceForSetting(XElement setting, IReadOnlyDictionary<string, string> gpoSourceLookup)
    {
        var gpoId = NormalizeGpResultProviderIdentifier(
            setting.Elements()
                .FirstOrDefault(static element => element.Name.LocalName.Equals("GPO", StringComparison.OrdinalIgnoreCase))?
                .Descendants()
                .FirstOrDefault(static element => element.Name.LocalName.Equals("Identifier", StringComparison.OrdinalIgnoreCase))?
                .Value);
        if (!string.IsNullOrWhiteSpace(gpoId) && gpoSourceLookup.TryGetValue(gpoId, out var mappedSource))
        {
            return mappedSource;
        }

        return "GroupPolicy";
    }

    private static string NormalizeGpResultProviderIdentifier(string? rawValue)
    {
        var value = NormalizePolicyFieldValue(rawValue);
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return value.Trim('{', '}');
    }

    private static string ResolveGpResultSourceFromGpoName(string? gpoName)
    {
        var normalized = NormalizePolicyFieldValue(gpoName);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return "GroupPolicy";
        }

        var lower = normalized.ToLowerInvariant();
        if (lower.Contains("local group policy", StringComparison.Ordinal) ||
            lower.Contains("lokale gruppenrichtlinie", StringComparison.Ordinal) ||
            lower.Contains("richtlinien der lokalen gruppe", StringComparison.Ordinal))
        {
            return "LocalPolicy";
        }

        return "GroupPolicy";
    }

    private static string NormalizeGpResultRegistryPath(string hive, string key)
    {
        var normalizedHive = NormalizePolicyFieldValue(hive);
        if (normalizedHive.StartsWith("HKEY_LOCAL_MACHINE", StringComparison.OrdinalIgnoreCase))
        {
            normalizedHive = "HKLM";
        }
        else if (normalizedHive.StartsWith("HKEY_CURRENT_USER", StringComparison.OrdinalIgnoreCase))
        {
            normalizedHive = "HKCU";
        }
        else if (normalizedHive.StartsWith("HKEY_USERS", StringComparison.OrdinalIgnoreCase))
        {
            normalizedHive = "HKU";
        }
        else if (normalizedHive.StartsWith("HKEY_CLASSES_ROOT", StringComparison.OrdinalIgnoreCase))
        {
            normalizedHive = "HKCR";
        }

        var normalizedKey = NormalizePolicyFieldValue(key).TrimStart('\\');
        if (string.IsNullOrWhiteSpace(normalizedHive))
        {
            return normalizedKey;
        }

        return string.IsNullOrWhiteSpace(normalizedKey)
            ? normalizedHive
            : $"{normalizedHive}\\{normalizedKey}";
    }

    private static string NormalizeGpResultRegistryPathByScope(string scope, string keyPath)
    {
        var normalized = NormalizePolicyFieldValue(keyPath);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.Empty;
        }

        normalized = normalized.TrimStart('\\');
        if (normalized.StartsWith("HKEY_LOCAL_MACHINE\\", StringComparison.OrdinalIgnoreCase))
        {
            return "HKLM\\" + normalized["HKEY_LOCAL_MACHINE\\".Length..];
        }

        if (normalized.StartsWith("HKEY_CURRENT_USER\\", StringComparison.OrdinalIgnoreCase))
        {
            return "HKCU\\" + normalized["HKEY_CURRENT_USER\\".Length..];
        }

        if (normalized.StartsWith("HKLM\\", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("HKCU\\", StringComparison.OrdinalIgnoreCase))
        {
            return normalized;
        }

        var prefix = string.Equals(scope, "Device", StringComparison.OrdinalIgnoreCase) ? "HKLM\\" : "HKCU\\";
        return prefix + normalized;
    }

    private static string ResolveGpResultRegistryValueText(XElement valueNode)
    {
        foreach (var child in valueNode.Elements().Where(static element => !element.Name.LocalName.Equals("Name", StringComparison.OrdinalIgnoreCase)))
        {
            if (child.Name.LocalName.Equals("MultiText", StringComparison.OrdinalIgnoreCase))
            {
                var values = child.Elements()
                    .Where(static element => element.Name.LocalName.Equals("Text", StringComparison.OrdinalIgnoreCase))
                    .Select(element => NormalizePolicyFieldValue(element.Value))
                    .Where(static value => !string.IsNullOrWhiteSpace(value))
                    .ToArray();
                if (values.Length > 0)
                {
                    return string.Join(", ", values);
                }
            }

            var text = NormalizePolicyFieldValue(child.Value);
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text;
            }
        }

        return string.Empty;
    }

    private static bool IsGpResultSuccessCode(string? rawValue)
    {
        var normalized = NormalizePolicyFieldValue(rawValue);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return true;
        }

        if (string.Equals(normalized, "0", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "0x00000000", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static bool LooksLikeHeaderRow(IReadOnlyList<string> cells)
    {
        if (cells.Count == 0)
        {
            return false;
        }

        var joined = string.Join(" ", cells).ToLowerInvariant();
        return joined.Contains("setting", StringComparison.Ordinal) ||
               joined.Contains("policy", StringComparison.Ordinal) ||
               joined.Contains("oma", StringComparison.Ordinal) ||
               joined.Contains("status", StringComparison.Ordinal) ||
               joined.Contains("result", StringComparison.Ordinal);
    }

    private static string NormalizeHeaderKey(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return value
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Trim();
    }

    private static IReadOnlyList<IntunePolicyResultEntry> DeduplicatePolicyEntries(IEnumerable<IntunePolicyResultEntry> entries)
    {
        return entries
            .GroupBy(
                entry => string.Join(
                    "|",
                    entry.Scope,
                    entry.Area,
                    entry.SettingName,
                    entry.OmaUri,
                    entry.CurrentValue,
                    entry.Status,
                    entry.ResultCode,
                    entry.Source,
                    entry.WinningSource,
                    entry.IsDuplicate.ToString(CultureInfo.InvariantCulture),
                    entry.DuplicateSources,
                    entry.MdmPath,
                    entry.GpoPath,
                    entry.GpoCategoryPath,
                    entry.AdditionalDetails),
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(entry => ScopeOrder(entry.Scope))
            .ThenBy(entry => entry.Area, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.SettingName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.OmaUri, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.Source, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IntunePolicyResultEntry? NormalizePolicyEntry(IReadOnlyDictionary<string, string> map)
    {
        if (map.Count == 0)
        {
            return null;
        }

        var policyHint = string.Join(" ", map.Keys).ToLowerInvariant();
        var valueHint = string.Join(" ", map.Values).ToLowerInvariant();
        var hasHint =
            policyHint.Contains("policy", StringComparison.Ordinal) ||
            policyHint.Contains("oma", StringComparison.Ordinal) ||
            policyHint.Contains("uri", StringComparison.Ordinal) ||
            valueHint.Contains("vendor/msft", StringComparison.Ordinal) ||
            valueHint.Contains("policy", StringComparison.Ordinal);
        if (!hasHint)
        {
            return null;
        }

        var omaUri = FindPolicyField(map, PolicyUriTokens);
        if (string.IsNullOrWhiteSpace(omaUri))
        {
            omaUri = OmaUriHintRegex().Match(valueHint).Value;
        }

        var settingName = FindPolicyField(map, PolicyNameTokens);
        if (string.IsNullOrWhiteSpace(settingName))
        {
            settingName = DeriveSettingNameFromOmaUri(omaUri);
        }

        var area = FindPolicyField(map, PolicyAreaTokens);
        if (string.IsNullOrWhiteSpace(area))
        {
            area = DeriveAreaFromOmaUri(omaUri);
        }

        var scopeRaw = FindPolicyField(map, PolicyScopeTokens);
        var currentValue = FindPolicyField(map, PolicyValueTokens);
        var statusRaw = FindPolicyField(map, PolicyStatusTokens);
        var resultRaw = FindPolicyField(map, PolicyResultTokens);
        var resultCode = NormalizeResultCode(resultRaw);
        var status = NormalizePolicyStatus(statusRaw, resultCode);
        var scope = NormalizeScope(scopeRaw, omaUri);

        if (string.IsNullOrWhiteSpace(settingName) && string.IsNullOrWhiteSpace(omaUri))
        {
            return null;
        }

        return new IntunePolicyResultEntry(
            scope,
            string.IsNullOrWhiteSpace(area) ? "General" : area,
            string.IsNullOrWhiteSpace(settingName) ? "Unknown Setting" : settingName,
            omaUri,
            currentValue,
            status,
            resultCode,
            "Mdm",
            string.Empty,
            false,
            string.Empty,
            omaUri);
    }

    private static string FindPolicyField(IReadOnlyDictionary<string, string> map, IReadOnlyList<string> tokens)
    {
        foreach (var key in map.Keys)
        {
            if (tokens.Any(token => key.Contains(token, StringComparison.OrdinalIgnoreCase)) &&
                map.TryGetValue(key, out var value) &&
                !string.IsNullOrWhiteSpace(value))
            {
                return NormalizePolicyFieldValue(value);
            }
        }

        return string.Empty;
    }

    private static string NormalizePolicyFieldValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Trim();
        return normalized.Length <= 400
            ? normalized
            : normalized[..400];
    }

    private static string NormalizeScope(string? scopeHint, string? omaUri)
    {
        var trimmedScopeHint = NormalizePolicyFieldValue(scopeHint);
        if (trimmedScopeHint.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase))
        {
            return "User";
        }

        var combined = $"{scopeHint} {omaUri}".ToLowerInvariant();
        if (combined.Contains("/device/", StringComparison.Ordinal) ||
            combined.Contains("./device/", StringComparison.Ordinal) ||
            combined.Contains("device", StringComparison.Ordinal))
        {
            return "Device";
        }

        if (combined.Contains("/user/", StringComparison.Ordinal) ||
            combined.Contains("./user/", StringComparison.Ordinal) ||
            combined.Contains("user", StringComparison.Ordinal))
        {
            return "User";
        }

        return "Unknown";
    }

    private static string DeriveAreaFromOmaUri(string? omaUri)
    {
        if (string.IsNullOrWhiteSpace(omaUri))
        {
            return "General";
        }

        var normalized = omaUri.Replace('\\', '/');
        var configMarker = "/Policy/Config/";
        var markerIndex = normalized.IndexOf(configMarker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex >= 0)
        {
            var rest = normalized[(markerIndex + configMarker.Length)..];
            var segment = rest.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(segment))
            {
                return segment.Trim();
            }
        }

        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var policiesIndex = Array.FindIndex(parts, static segment => string.Equals(segment, "Policies", StringComparison.OrdinalIgnoreCase));
        if (policiesIndex >= 0)
        {
            var candidateIndex = policiesIndex + 1;
            if (candidateIndex < parts.Length)
            {
                var candidate = parts[candidateIndex].Trim();
                if (candidate.Equals("Microsoft", StringComparison.OrdinalIgnoreCase) && candidateIndex + 1 < parts.Length)
                {
                    return parts[candidateIndex + 1].Trim();
                }

                return candidate;
            }
        }

        if (parts.Length >= 2)
        {
            return parts[^1].Trim();
        }

        return "General";
    }

    private static string DeriveSettingNameFromOmaUri(string? omaUri)
    {
        if (string.IsNullOrWhiteSpace(omaUri))
        {
            return string.Empty;
        }

        var normalized = omaUri.Replace('\\', '/');
        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 0 ? string.Empty : parts[^1].Trim();
    }

    private static string NormalizeResultCode(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var match = Regex.Match(raw, @"0x[0-9A-Fa-f]+");
        if (match.Success)
        {
            return match.Value.ToUpperInvariant();
        }

        if (long.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var numeric))
        {
            return $"0x{unchecked((uint)numeric):X8}";
        }

        return raw.Trim();
    }

    private static string NormalizePolicyStatus(string? statusHint, string resultCode)
    {
        if (!string.IsNullOrWhiteSpace(statusHint))
        {
            var normalized = statusHint.Trim();
            if (normalized.Contains("appl", StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains("success", StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains("succeed", StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains("ok", StringComparison.OrdinalIgnoreCase))
            {
                return "Applied";
            }

            if (normalized.Contains("fail", StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains("denied", StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains("blocked", StringComparison.OrdinalIgnoreCase))
            {
                return "Failed";
            }
        }

        if (string.IsNullOrWhiteSpace(resultCode))
        {
            return "Unknown";
        }

        if (resultCode.Equals("0x00000000", StringComparison.OrdinalIgnoreCase) ||
            resultCode.Equals("0", StringComparison.OrdinalIgnoreCase))
        {
            return "Applied";
        }

        return "Failed";
    }

    private static int ScopeOrder(string scope)
    {
        if (string.Equals(scope, "Device", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (string.Equals(scope, "User", StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        return 2;
    }

    private static IntunePolicyResultSummary BuildPolicyResultSummary(IReadOnlyList<IntunePolicyResultEntry> entries)
    {
        var applied = entries.Count(entry => string.Equals(entry.Status, "Applied", StringComparison.OrdinalIgnoreCase));
        var failed = entries.Count(entry => string.Equals(entry.Status, "Failed", StringComparison.OrdinalIgnoreCase));
        var unknown = entries.Count - applied - failed;
        var device = entries.Count(entry => string.Equals(entry.Scope, "Device", StringComparison.OrdinalIgnoreCase));
        var user = entries.Count(entry => string.Equals(entry.Scope, "User", StringComparison.OrdinalIgnoreCase));
        var unknownScope = entries.Count - device - user;
        var duplicates = entries.Count(entry => entry.IsDuplicate);
        var conflicts = entries.Count(entry =>
            entry.IsDuplicate &&
            !string.IsNullOrWhiteSpace(entry.WinningSource) &&
            !string.Equals(
                NormalizePolicySource(entry.Source),
                NormalizePolicySource(entry.WinningSource),
                StringComparison.OrdinalIgnoreCase));

        return new IntunePolicyResultSummary(
            entries.Count,
            applied,
            failed,
            unknown,
            device,
            user,
            unknownScope,
            duplicates,
            conflicts);
    }

    private static string BuildPolicyResultHtml(IntunePolicyResultReport report)
    {
        var comparisonLookup = BuildPolicyComparisonLookup(report.Entries);
        var visibleTreeModes = new[] { "active", "mdm", "gpo" };
        var sectionModes = new[] { "active", "mdm", "gpo", "compare" };
        var sectionsByMode = sectionModes.ToDictionary(
            mode => mode,
            mode => BuildPolicyReportSections(report.Entries, comparisonLookup, mode),
            StringComparer.OrdinalIgnoreCase);
        var scopeGroupsByMode = sectionModes.ToDictionary(
            mode => mode,
            mode => sectionsByMode[mode]
                .GroupBy(section => section.Scope)
                .OrderBy(group => ScopeOrder(group.Key))
                .ToArray(),
            StringComparer.OrdinalIgnoreCase);
        var scopeTreesByMode = sectionModes.ToDictionary(
            mode => mode,
            mode => scopeGroupsByMode[mode]
                .ToDictionary(group => group.Key, group => BuildPolicyTree(group.Key, group.ToArray()), StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);
        var columnWidths = BuildPolicyResultColumnWidths(report.Entries);

        var summary = report.Summary;
        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html lang=\"en\">");
        sb.AppendLine("<head>");
        sb.AppendLine("  <meta charset=\"utf-8\" />");
        sb.AppendLine("  <meta http-equiv=\"X-UA-Compatible\" content=\"IE=edge\" />");
        sb.AppendLine("  <meta name=\"viewport\" content=\"width=device-width, initial-scale=1\" />");
        sb.AppendLine("  <title>Intune Policy Result</title>");
        sb.AppendLine("  <style>");
        sb.AppendLine($"    :root {{ --line:#d4d9e2; --bg:#f4f6fa; --panel:#ffffff; --ok:#0b6e4f; --fail:#8f1e2d; --unknown:#6a7079; --accent:#154f91; --w-setting:{columnWidths[0].ToString("0.##", CultureInfo.InvariantCulture)}%; --w-path:{columnWidths[1].ToString("0.##", CultureInfo.InvariantCulture)}%; --w-value:{columnWidths[2].ToString("0.##", CultureInfo.InvariantCulture)}%; --w-sourceinfo:{columnWidths[3].ToString("0.##", CultureInfo.InvariantCulture)}%; --w-status:{columnWidths[4].ToString("0.##", CultureInfo.InvariantCulture)}%; --w-code:{columnWidths[5].ToString("0.##", CultureInfo.InvariantCulture)}%; }}");
        sb.AppendLine("    * { box-sizing:border-box; }");
        sb.AppendLine("    body { margin:0; font-family:'Segoe UI', Tahoma, sans-serif; color:#1e2a35; background:var(--bg); }");
        sb.AppendLine("    header { border-bottom:1px solid var(--line); background:linear-gradient(180deg,#f9fbff,#ecf2fb); padding:14px 18px; }");
        sb.AppendLine("    h1 { margin:0 0 8px; font-size:20px; }");
        sb.AppendLine("    h2 { margin:14px 0 8px; font-size:17px; color:#1f2f42; }");
        sb.AppendLine("    h3 { margin:0; font-size:15px; color:#1f2f42; }");
        sb.AppendLine("    .meta { font-size:12px; color:#3f4f63; line-height:1.5; }");
        sb.AppendLine("    .summary { margin-top:10px; font-size:0; }");
        sb.AppendLine("    .card { display:inline-block; width:24.2%; margin:0 1% 8px 0; vertical-align:top; background:var(--panel); border:1px solid var(--line); border-radius:6px; padding:6px 8px; font-size:12px; }");
        sb.AppendLine("    .card:nth-child(4n) { margin-right:0; }");
        sb.AppendLine("    .card .label { font-size:11px; text-transform:uppercase; color:#5a6777; }");
        sb.AppendLine("    .card .value { margin-top:2px; font-size:18px; font-weight:700; }");
        sb.AppendLine("    .layout { display:block; min-height:calc(100vh - 150px); }");
        sb.AppendLine("    nav { display:none; }");
        sb.AppendLine("    .tree-mode { display:none; }");
        sb.AppendLine("    body[data-tree-mode='active'] .tree-mode[data-tree-mode='active'] { display:block; }");
        sb.AppendLine("    body[data-tree-mode='mdm'] .tree-mode[data-tree-mode='mdm'] { display:block; }");
        sb.AppendLine("    body[data-tree-mode='gpo'] .tree-mode[data-tree-mode='gpo'] { display:block; }");
        sb.AppendLine("    .nav-tree, .nav-tree ul { list-style:none; margin:0; padding:0; }");
        sb.AppendLine("    .nav-scope { margin:0 0 10px; }");
        sb.AppendLine("    .nav-tree details { margin:0 0 4px; }");
        sb.AppendLine("    .nav-tree summary { list-style:none; cursor:pointer; padding:6px 8px; border-radius:4px; color:#213143; font-size:13px; background:#f8fbff; border:1px solid #e3ebf8; }");
        sb.AppendLine("    .nav-tree summary::-webkit-details-marker { display:none; }");
        sb.AppendLine("    .nav-tree summary::before { content:'▸ '; color:#355271; }");
        sb.AppendLine("    .nav-tree details[open] > summary::before { content:'▾ '; }");
        sb.AppendLine("    .nav-tree ul { margin-left:14px; padding-top:4px; }");
        sb.AppendLine("    .nav-item { display:block; padding:6px 8px; border-radius:4px; color:#213143; font-size:13px; background:#f8fbff; border:1px solid #e3ebf8; text-decoration:none; }");
        sb.AppendLine("    main { padding:12px; overflow:auto; }");
        sb.AppendLine("    .toolbar { display:flex; gap:8px; margin:0 0 12px; flex-wrap:wrap; align-items:center; }");
        sb.AppendLine("    .toolbar-btn { display:inline-block; border:1px solid #c6d4e8; border-radius:4px; background:#f2f7ff; color:#1b3f6a; font-size:12px; font-weight:600; padding:6px 10px; cursor:pointer; user-select:none; }");
        sb.AppendLine("    .toolbar-btn:hover { background:#e7f0ff; }");
        sb.AppendLine("    .filter-label { font-size:12px; font-weight:600; color:#30465f; }");
        sb.AppendLine("    .filter-select { border:1px solid #c6d4e8; border-radius:4px; background:#fff; color:#203447; font-size:12px; padding:6px 8px; min-width:170px; }");
        sb.AppendLine("    .filter-search { border:1px solid #c6d4e8; border-radius:4px; background:#fff; color:#203447; font-size:12px; padding:6px 8px; min-width:220px; }");
        sb.AppendLine("    .section { margin:0 0 8px; border:1px solid #bfc9d3; background:#fff; overflow:visible; width:100%; }");
        sb.AppendLine("    .policy-node { margin:0; border:none; background:transparent; overflow:visible; min-width:0; width:100%; }");
        sb.AppendLine("    .policy-node-toggle { display:flex; align-items:center; width:100%; border:none; cursor:pointer; padding:6px 10px; font-size:14px; font-weight:600; color:#000; border-top:1px solid #c1ccd4; border-bottom:1px solid #c1ccd4; user-select:none; text-align:left; background:transparent; }");
        sb.AppendLine("    .policy-node-toggle::-moz-focus-inner { border:0; }");
        sb.AppendLine("    .policy-node-marker { display:inline-block; width:14px; margin-right:4px; color:#355271; flex:0 0 14px; }");
        sb.AppendLine("    .policy-node.is-expanded > .policy-node-toggle .policy-node-marker::before { content:'▾'; }");
        sb.AppendLine("    .policy-node.is-collapsed > .policy-node-toggle .policy-node-marker::before { content:'▸'; }");
        sb.AppendLine("    .policy-node-label { display:inline-block; min-width:0; }");
        sb.AppendLine("    .policy-node.is-collapsed > .policy-node-body { display:none; }");
        sb.AppendLine("    .policy-node-body { margin:0; padding:0 0 0 18px; border-left:1px solid #d3d9df; width:100%; min-width:0; }");
        sb.AppendLine("    .policy-node .policy-node { margin:0; width:100%; }");
        sb.AppendLine("    .policy-node.depth-0 > .policy-node-toggle { background:#f8efc8; margin-top:6px; }");
        sb.AppendLine("    .policy-node.depth-1 > .policy-node-toggle { background:#f6e9c8; }");
        sb.AppendLine("    .policy-node.depth-2 > .policy-node-toggle { background:#86a8c2; color:#000; }");
        sb.AppendLine("    .policy-node.depth-3 > .policy-node-toggle { background:#b8ccd9; color:#000; }");
        sb.AppendLine("    .policy-node.depth-4 > .policy-node-toggle, .policy-node.depth-5 > .policy-node-toggle { background:#cfd9e2; color:#000; }");
        sb.AppendLine("    .policy-leaf > .policy-node-body { padding:0 0 0 18px; }");
        sb.AppendLine("    .policy-leaf .section { margin:0 0 8px; border-top:1px solid #d3d9df; }");
        sb.AppendLine("    table { width:100%; border-collapse:collapse; table-layout:auto; }");
        sb.AppendLine("    col.col-setting { width:var(--w-setting); }");
        sb.AppendLine("    col.col-path { width:var(--w-path); }");
        sb.AppendLine("    col.col-value { width:var(--w-value); }");
        sb.AppendLine("    col.col-sourceinfo { width:var(--w-sourceinfo); }");
        sb.AppendLine("    col.col-status { width:var(--w-status); }");
        sb.AppendLine("    col.col-code { width:var(--w-code); }");
        sb.AppendLine("    th, td { border-bottom:1px solid var(--line); padding:5px 8px; text-align:left; vertical-align:top; font-size:12px; overflow-wrap:anywhere; word-wrap:break-word; word-break:break-word; white-space:normal; }");
        sb.AppendLine("    th { background:#f4f4f4; font-weight:600; color:#000; }");
        sb.AppendLine("    .winner-source { font-weight:700; color:#1f2f42; }");
        sb.AppendLine("    .source-hint { display:inline-block; margin-left:6px; color:#52647b; font-size:11px; border-bottom:1px dotted #7f90a8; cursor:help; max-width:100%; white-space:nowrap; overflow:hidden; text-overflow:ellipsis; vertical-align:bottom; }");
        sb.AppendLine("    .source-conflict { display:inline-block; margin-left:6px; padding:1px 6px; border-radius:999px; background:#f9d7d9; color:#8f1e2d; font-size:11px; font-weight:700; cursor:help; vertical-align:bottom; }");
        sb.AppendLine("    .status { font-weight:600; }");
        sb.AppendLine("    .status-applied { color:var(--ok); }");
        sb.AppendLine("    .status-failed { color:var(--fail); }");
        sb.AppendLine("    .status-unknown { color:var(--unknown); }");
        sb.AppendLine("    .hint { padding:6px 8px; color:#556477; font-size:11px; }");
        sb.AppendLine("    .path-view { display:none; white-space:normal; }");
        sb.AppendLine("    body[data-path-mode='active'] .path-view-active { display:block; }");
        sb.AppendLine("    body[data-path-mode='mdm'] .path-view-mdm { display:block; }");
        sb.AppendLine("    body[data-path-mode='gpo'] .path-view-gpo { display:block; }");
        sb.AppendLine("    body[data-path-mode='compare'] .path-view-compare { display:block; }");
        sb.AppendLine("    .path-line { display:block; margin:0 0 4px; }");
        sb.AppendLine("    .path-line:last-child { margin-bottom:0; }");
        sb.AppendLine("    .path-label { display:inline-block; min-width:76px; color:#556477; font-weight:600; }");
        sb.AppendLine("    .detail-row td { padding:0; border-bottom:1px solid var(--line); }");
        sb.AppendLine("    .detail-host { padding:0 0 8px 28px; }");
        sb.AppendLine("    .detail-table { width:calc(100% - 16px); margin:0 0 0 18px; border-collapse:collapse; background:#f8fbff; border:1px solid #d7e0ee; }");
        sb.AppendLine("    .detail-table td { padding:5px 8px; font-size:11px; color:#415264; border-bottom:1px solid #d7e0ee; vertical-align:top; }");
        sb.AppendLine("    .detail-table tr:last-child td { border-bottom:none; }");
        sb.AppendLine("  </style>");
        sb.AppendLine("</head>");
        sb.AppendLine("<body data-path-mode=\"active\" data-tree-mode=\"active\">");
        sb.AppendLine("  <header>");
        sb.AppendLine("    <h1>Intune Policy Result (gpresult-style)</h1>");
        sb.AppendLine($"    <div class=\"meta\">Host: {WebUtility.HtmlEncode(report.Host)} | Source: {WebUtility.HtmlEncode(report.Source)} | Generated (UTC): {report.GeneratedAtUtc:yyyy-MM-dd HH:mm:ss}</div>");
        sb.AppendLine($"    <div class=\"meta\">Report directory: {WebUtility.HtmlEncode(report.ReportDirectory)}</div>");
        sb.AppendLine("    <div class=\"summary\">");
        sb.AppendLine($"      <div class=\"card\"><div class=\"label\">Total</div><div class=\"value\">{summary.TotalCount}</div></div>");
        sb.AppendLine($"      <div class=\"card\"><div class=\"label\">Applied</div><div class=\"value\" style=\"color:var(--ok)\">{summary.AppliedCount}</div></div>");
        sb.AppendLine($"      <div class=\"card\"><div class=\"label\">Failed</div><div class=\"value\" style=\"color:var(--fail)\">{summary.FailedCount}</div></div>");
        sb.AppendLine($"      <div class=\"card\"><div class=\"label\">Unknown</div><div class=\"value\" style=\"color:var(--unknown)\">{summary.UnknownCount}</div></div>");
        sb.AppendLine($"      <div class=\"card\"><div class=\"label\">Device</div><div class=\"value\">{summary.DeviceCount}</div></div>");
        sb.AppendLine($"      <div class=\"card\"><div class=\"label\">User</div><div class=\"value\">{summary.UserCount}</div></div>");
        sb.AppendLine($"      <div class=\"card\"><div class=\"label\">Duplicate</div><div class=\"value\">{summary.DuplicateCount}</div></div>");
        sb.AppendLine($"      <div class=\"card\"><div class=\"label\">Conflict</div><div class=\"value\">{summary.ConflictCount}</div></div>");
        sb.AppendLine("    </div>");
        sb.AppendLine("  </header>");
        sb.AppendLine("  <div class=\"layout\">");
        sb.AppendLine("    <nav>");
        sb.AppendLine("      <h2>Policy Areas</h2>");
        if (report.Entries.Count == 0)
        {
            sb.AppendLine("      <div class=\"hint\">No policy entries were extracted.</div>");
        }
        else
        {
            foreach (var treeMode in visibleTreeModes)
            {
                var scopeGroups = scopeGroupsByMode[treeMode];
                sb.AppendLine($"      <div class=\"tree-mode\" data-tree-mode=\"{WebUtility.HtmlEncode(treeMode)}\">");
                for (var scopeIndex = 0; scopeIndex < scopeGroups.Length; scopeIndex++)
                {
                    var scopeGroup = scopeGroups[scopeIndex];
                    sb.AppendLine($"      <div class=\"nav-scope\"><h3>{WebUtility.HtmlEncode(scopeGroup.Key)} ({scopeGroup.Sum(section => section.Entries.Length)})</h3>");
                    sb.AppendLine("      <ul class=\"nav-tree\">");
                    AppendPolicyTreeNavHtml(sb, scopeTreesByMode[treeMode][scopeGroup.Key]);
                    sb.AppendLine("      </ul></div>");
                }

                sb.AppendLine("      </div>");
            }
        }
        sb.AppendLine("    </nav>");
        sb.AppendLine("    <main>");
        if (report.Entries.Count == 0)
        {
            sb.AppendLine("      <h2>No policy entries were extracted.</h2>");
            sb.AppendLine("      <div class=\"hint\">Run Generate or Parse again after MDM diagnostics are available.</div>");
        }
        else
        {
            sb.AppendLine("      <div class=\"toolbar\">");
            sb.AppendLine("        <button type=\"button\" id=\"expand-all-btn\" class=\"toolbar-btn\">Expand All Nodes</button>");
            sb.AppendLine("        <button type=\"button\" id=\"collapse-all-btn\" class=\"toolbar-btn\">Collapse All Nodes</button>");
            sb.AppendLine("        <span class=\"filter-label\">Search</span>");
            sb.AppendLine("        <input id=\"report-search\" class=\"filter-search\" type=\"search\" placeholder=\"Search policies, values, paths\" />");
            sb.AppendLine("        <span class=\"filter-label\">Filter</span>");
            sb.AppendLine("        <select id=\"source-filter\" class=\"filter-select\">");
            sb.AppendLine("          <option value=\"all\">All Policies</option>");
            sb.AppendLine("          <option value=\"mdm-only\">MDM Only</option>");
            sb.AppendLine("          <option value=\"hybrid\">MDM + GPO / Local</option>");
            sb.AppendLine("          <option value=\"gpo-only\">Group Policy Only</option>");
            sb.AppendLine("          <option value=\"local-only\">Local Policy Only</option>");
            sb.AppendLine("          <option value=\"gpo-or-local-only\">Any GPO / Local Only</option>");
            sb.AppendLine("        </select>");
            sb.AppendLine("        <span class=\"filter-label\">Path View</span>");
            sb.AppendLine("        <select id=\"path-mode\" class=\"filter-select\">");
            sb.AppendLine("          <option value=\"active\">Active Match Path</option>");
            sb.AppendLine("          <option value=\"mdm\">MDM Path</option>");
            sb.AppendLine("          <option value=\"gpo\">GPO / ADMX Path</option>");
            sb.AppendLine("          <option value=\"compare\">Compare MDM vs. GPO</option>");
            sb.AppendLine("        </select>");
            sb.AppendLine("      </div>");
            foreach (var treeMode in sectionModes)
            {
                var effectiveTreeMode = string.Equals(treeMode, "compare", StringComparison.OrdinalIgnoreCase) ? "active" : treeMode;
                sb.AppendLine($"      <div class=\"sections tree-mode\" data-tree-mode=\"{WebUtility.HtmlEncode(effectiveTreeMode)}\" data-section-mode=\"{WebUtility.HtmlEncode(treeMode)}\">");
                foreach (var scopeGroup in scopeGroupsByMode[treeMode])
                {
                    sb.AppendLine($"        <h2 data-scope-heading=\"{WebUtility.HtmlEncode(scopeGroup.Key)}\">{WebUtility.HtmlEncode(scopeGroup.Key)} Policies</h2>");
                    AppendPolicyTreeHtml(sb, scopeTreesByMode[treeMode][scopeGroup.Key], comparisonLookup, scopeGroup.Key, depth: 0);
                }

                sb.AppendLine("      </div>");
            }
        }

        sb.AppendLine("    </main>");
        sb.AppendLine("  </div>");
        sb.AppendLine("  <script>");
        sb.AppendLine("    (function () {");
        sb.AppendLine("      var sourceFilter = document.getElementById('source-filter');");
        sb.AppendLine("      var pathMode = document.getElementById('path-mode');");
        sb.AppendLine("      var reportSearch = document.getElementById('report-search');");
        sb.AppendLine("      var expandAllBtn = document.getElementById('expand-all-btn');");
        sb.AppendLine("      var collapseAllBtn = document.getElementById('collapse-all-btn');");
        sb.AppendLine("      function updateReportView() {");
        sb.AppendLine("        var filter = sourceFilter ? sourceFilter.value : 'all';");
        sb.AppendLine("        var mode = pathMode ? pathMode.value : 'active';");
        sb.AppendLine("        var search = reportSearch && reportSearch.value ? reportSearch.value.toLowerCase() : '';");
        sb.AppendLine("        document.body.setAttribute('data-path-mode', mode);");
        sb.AppendLine("        document.body.setAttribute('data-tree-mode', mode === 'gpo' ? 'gpo' : (mode === 'mdm' ? 'mdm' : 'active'));");
        sb.AppendLine("        var sectionContainers = document.querySelectorAll('.sections[data-section-mode]');");
        sb.AppendLine("        for (var s = 0; s < sectionContainers.length; s++) {");
        sb.AppendLine("          var container = sectionContainers[s];");
        sb.AppendLine("          var containerMode = container.getAttribute('data-section-mode') || 'active';");
        sb.AppendLine("          var showContainer = containerMode === mode || (mode !== 'compare' && containerMode === 'compare' ? false : false);");
        sb.AppendLine("          container.style.display = showContainer ? '' : 'none';");
        sb.AppendLine("        }");
        sb.AppendLine("        function matchesFilter(kind) {");
        sb.AppendLine("          if (filter === 'all') { return true; }");
        sb.AppendLine("          if (filter === 'gpo-or-local-only') { return kind === 'gpo-only' || kind === 'local-only' || kind === 'gpo-or-local-only'; }");
        sb.AppendLine("          return kind === filter;");
        sb.AppendLine("        }");
        sb.AppendLine("        var sections = document.querySelectorAll('.sections[data-section-mode=\"' + mode + '\"] .section');");
        sb.AppendLine("        for (var i = 0; i < sections.length; i++) {");
        sb.AppendLine("          var section = sections[i];");
        sb.AppendLine("          var rows = section.querySelectorAll('tbody tr[data-entry-row=\"true\"]');");
        sb.AppendLine("          var visibleRows = 0;");
        sb.AppendLine("          for (var j = 0; j < rows.length; j++) {");
        sb.AppendLine("            var row = rows[j];");
        sb.AppendLine("            var kind = row.getAttribute('data-kind') || 'all';");
        sb.AppendLine("            var detailRow = row.nextElementSibling && row.nextElementSibling.getAttribute('data-detail-row') === 'true' ? row.nextElementSibling : null;");
        sb.AppendLine("            var text = (row.textContent || '') + (detailRow ? (' ' + (detailRow.textContent || '')) : '');");
        sb.AppendLine("            var show = matchesFilter(kind) && (!search || text.toLowerCase().indexOf(search) >= 0);");
        sb.AppendLine("            row.style.display = show ? '' : 'none';");
        sb.AppendLine("            if (detailRow) { detailRow.style.display = show ? '' : 'none'; }");
        sb.AppendLine("            if (show) { visibleRows++; }");
        sb.AppendLine("          }");
        sb.AppendLine("          section.style.display = visibleRows > 0 ? '' : 'none';");
        sb.AppendLine("        }");
        sb.AppendLine("        var navItems = document.querySelectorAll('[data-section-id]');");
        sb.AppendLine("        for (var k = 0; k < navItems.length; k++) {");
        sb.AppendLine("          var navItem = navItems[k];");
        sb.AppendLine("          var sectionId = navItem.getAttribute('data-section-id');");
        sb.AppendLine("          var target = sectionId ? document.getElementById(sectionId) : null;");
        sb.AppendLine("          navItem.style.display = target && target.style.display !== 'none' ? '' : 'none';");
        sb.AppendLine("        }");
        sb.AppendLine("        var treeNodes = document.querySelectorAll('.sections[data-section-mode=\"' + mode + '\"] .policy-node');");
        sb.AppendLine("        for (var p = treeNodes.length - 1; p >= 0; p--) {");
        sb.AppendLine("          var node = treeNodes[p];");
        sb.AppendLine("          var body = null;");
        sb.AppendLine("          for (var c = 0; c < node.children.length; c++) {");
        sb.AppendLine("            var child = node.children[c];");
        sb.AppendLine("            if (child.classList && child.classList.contains('policy-node-body')) { body = child; break; }");
        sb.AppendLine("          }");
        sb.AppendLine("          var visibleNode = false;");
        sb.AppendLine("          if (body) {");
        sb.AppendLine("            for (var d = 0; d < body.children.length; d++) {");
        sb.AppendLine("              var item = body.children[d];");
        sb.AppendLine("              if (!item.classList) { continue; }");
        sb.AppendLine("              if (item.classList.contains('section') && item.style.display !== 'none') { visibleNode = true; break; }");
        sb.AppendLine("              if (item.classList.contains('policy-node') && item.style.display !== 'none') { visibleNode = true; break; }");
        sb.AppendLine("            }");
        sb.AppendLine("          }");
        sb.AppendLine("          node.style.display = visibleNode ? '' : 'none';");
        sb.AppendLine("          if (search) { setNodeExpanded(node, visibleNode); }");
        sb.AppendLine("        }");
        sb.AppendLine("        var headings = document.querySelectorAll('.sections[data-section-mode=\"' + mode + '\"] [data-scope-heading]');");
        sb.AppendLine("        for (var m = 0; m < headings.length; m++) {");
        sb.AppendLine("          var heading = headings[m];");
        sb.AppendLine("          var scopeName = heading.getAttribute('data-scope-heading');");
        sb.AppendLine("          var visibleSection = null;");
        sb.AppendLine("          var scopeNodes = document.querySelectorAll('.sections[data-section-mode=\"' + mode + '\"] .policy-node[data-scope=\"' + scopeName + '\"]');");
        sb.AppendLine("          for (var n = 0; n < scopeNodes.length; n++) {");
        sb.AppendLine("            if (scopeNodes[n].style.display !== 'none') { visibleSection = scopeNodes[n]; break; }");
        sb.AppendLine("          }");
        sb.AppendLine("          heading.style.display = visibleSection ? '' : 'none';");
        sb.AppendLine("        }");
        sb.AppendLine("      }");
        sb.AppendLine("      function getNodeBody(node) {");
        sb.AppendLine("        if (!node) { return null; }");
        sb.AppendLine("        for (var i = 0; i < node.children.length; i++) {");
        sb.AppendLine("          var child = node.children[i];");
        sb.AppendLine("          if (child.classList && child.classList.contains('policy-node-body')) { return child; }");
        sb.AppendLine("        }");
        sb.AppendLine("        return null;");
        sb.AppendLine("      }");
        sb.AppendLine("      function getDirectVisiblePolicyNodes(node) {");
        sb.AppendLine("        var body = getNodeBody(node);");
        sb.AppendLine("        var result = [];");
        sb.AppendLine("        if (!body) { return result; }");
        sb.AppendLine("        for (var i = 0; i < body.children.length; i++) {");
        sb.AppendLine("          var child = body.children[i];");
        sb.AppendLine("          if (child.classList && child.classList.contains('policy-node') && child.style.display !== 'none') { result.push(child); }");
        sb.AppendLine("        }");
        sb.AppendLine("        return result;");
        sb.AppendLine("      }");
        sb.AppendLine("      function getDirectVisibleSections(node) {");
        sb.AppendLine("        var body = getNodeBody(node);");
        sb.AppendLine("        var count = 0;");
        sb.AppendLine("        if (!body) { return count; }");
        sb.AppendLine("        for (var i = 0; i < body.children.length; i++) {");
        sb.AppendLine("          var child = body.children[i];");
        sb.AppendLine("          if (child.classList && child.classList.contains('section') && child.style.display !== 'none') { count++; }");
        sb.AppendLine("        }");
        sb.AppendLine("        return count;");
        sb.AppendLine("      }");
        sb.AppendLine("      function applyNodeExpandedState(node, expanded) {");
        sb.AppendLine("        if (!node) { return; }");
        sb.AppendLine("        node.className = node.className.replace(/\\bis-expanded\\b/g, '').replace(/\\bis-collapsed\\b/g, '').replace(/\\s{2,}/g, ' ').replace(/^\\s+|\\s+$/g, '');");
        sb.AppendLine("        node.className += (node.className ? ' ' : '') + (expanded ? 'is-expanded' : 'is-collapsed');");
        sb.AppendLine("        var toggle = null;");
        sb.AppendLine("        for (var i = 0; i < node.children.length; i++) {");
        sb.AppendLine("          var child = node.children[i];");
        sb.AppendLine("          if (child.classList && child.classList.contains('policy-node-toggle')) { toggle = child; break; }");
        sb.AppendLine("        }");
        sb.AppendLine("        if (toggle) { toggle.setAttribute('aria-expanded', expanded ? 'true' : 'false'); }");
        sb.AppendLine("      }");
        sb.AppendLine("      function setNodeExpanded(node, expanded) {");
        sb.AppendLine("        var current = node;");
        sb.AppendLine("        while (current) {");
        sb.AppendLine("          applyNodeExpandedState(current, expanded);");
        sb.AppendLine("          var directSections = getDirectVisibleSections(current);");
        sb.AppendLine("          var directNodes = getDirectVisiblePolicyNodes(current);");
        sb.AppendLine("          if (directSections !== 0 || directNodes.length !== 1) { break; }");
        sb.AppendLine("          current = directNodes[0];");
        sb.AppendLine("        }");
        sb.AppendLine("      }");
        sb.AppendLine("      document.onclick = function (event) {");
        sb.AppendLine("        var target = event.target || event.srcElement;");
        sb.AppendLine("        while (target && target.nodeType === 1) {");
          sb.AppendLine("          if (target.classList && target.classList.contains('policy-node-toggle')) {");
          sb.AppendLine("            var node = target.parentNode;");
          sb.AppendLine("            setNodeExpanded(node, !node.classList.contains('is-expanded'));");
          sb.AppendLine("            return false;");
          sb.AppendLine("          }");
        sb.AppendLine("          target = target.parentNode;");
        sb.AppendLine("        }");
        sb.AppendLine("      };");
        sb.AppendLine("      if (sourceFilter) { sourceFilter.onchange = updateReportView; }");
        sb.AppendLine("      if (pathMode) { pathMode.onchange = updateReportView; }");
        sb.AppendLine("      if (reportSearch) { reportSearch.oninput = updateReportView; }");
        sb.AppendLine("      if (expandAllBtn) { expandAllBtn.onclick = function () { var mode = pathMode ? pathMode.value : 'active'; var nodes = document.querySelectorAll('.sections[data-section-mode=\"' + mode + '\"] .policy-node'); for (var i = 0; i < nodes.length; i++) { setNodeExpanded(nodes[i], true); } }; }");
        sb.AppendLine("      if (collapseAllBtn) { collapseAllBtn.onclick = function () { var mode = pathMode ? pathMode.value : 'active'; var nodes = document.querySelectorAll('.sections[data-section-mode=\"' + mode + '\"] .policy-node'); for (var i = 0; i < nodes.length; i++) { setNodeExpanded(nodes[i], false); } }; }");
        sb.AppendLine("      updateReportView();");
        sb.AppendLine("    })();");
        sb.AppendLine("  </script>");
        sb.AppendLine("</body>");
        sb.AppendLine("</html>");
        return sb.ToString();
    }

    private static PolicyTreeNode[] BuildPolicyTree(string scope, IReadOnlyList<PolicyReportSection> sections)
    {
        var roots = new List<PolicyTreeNode>();
        foreach (var section in sections)
        {
            var segments = BuildPolicyTreeSegments(scope, section.Area);
            if (segments.Length == 0)
            {
                segments = [section.Area];
            }

            var currentNodes = roots;
            PolicyTreeNode? currentNode = null;
            for (var index = 0; index < segments.Length; index++)
            {
                var segment = string.IsNullOrWhiteSpace(segments[index]) ? section.Area : segments[index];
                var existingNode = currentNodes.FirstOrDefault(node => string.Equals(node.Label, segment, StringComparison.OrdinalIgnoreCase));
                if (existingNode is null)
                {
                    existingNode = new PolicyTreeNode(segment);
                    currentNodes.Add(existingNode);
                }

                currentNode = existingNode;
                currentNodes = existingNode.Children;
            }

            if (currentNode is null)
            {
                currentNode = new PolicyTreeNode(section.Area);
                roots.Add(currentNode);
            }

            currentNode.Sections.Add(section);
        }

        SortPolicyTreeNodes(roots);
        return roots.ToArray();
    }

    private static PolicyReportSection[] BuildPolicyReportSections(
        IReadOnlyList<IntunePolicyResultEntry> entries,
        IReadOnlyDictionary<string, PolicyComparisonInfo> comparisonLookup,
        string treeMode)
    {
        var displayEntries = SelectPolicyEntriesForTreeMode(entries, treeMode);
        return displayEntries
            .GroupBy(entry => new
            {
                entry.Scope,
                Area = ResolvePolicyTreeSectionLabel(
                    treeMode,
                    entry,
                    comparisonLookup.TryGetValue(CreateConflictKey(entry), out var comparison)
                        ? comparison
                        : BuildPolicyComparisonInfo([entry]))
            })
            .OrderBy(group => ScopeOrder(group.Key.Scope))
            .ThenBy(group => group.Key.Area, StringComparer.OrdinalIgnoreCase)
            .Select((group, index) => new PolicyReportSection(
                $"{treeMode}-section-{index + 1}",
                group.Key.Scope,
                group.Key.Area,
                group
                    .OrderBy(entry => entry.SettingName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(entry => entry.OmaUri, StringComparer.OrdinalIgnoreCase)
                    .ToArray()))
            .ToArray();
    }

    private static IReadOnlyList<IntunePolicyResultEntry> SelectPolicyEntriesForTreeMode(
        IReadOnlyList<IntunePolicyResultEntry> entries,
        string treeMode)
    {
        if (entries.Count == 0)
        {
            return [];
        }

        return BuildConflictGroups(entries)
            .Select(group => SelectPolicyEntryForTreeMode(group, treeMode))
            .OrderBy(entry => ScopeOrder(entry.Scope))
            .ThenBy(entry => entry.Area, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.SettingName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.OmaUri, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IntunePolicyResultEntry SelectPolicyEntryForTreeMode(
        IReadOnlyList<IntunePolicyResultEntry> entries,
        string treeMode)
    {
        if (entries.Count == 1)
        {
            return entries[0];
        }

        static IntunePolicyResultEntry? PickBySource(IEnumerable<IntunePolicyResultEntry> candidates, Func<IntunePolicyResultEntry, bool> predicate)
            => candidates.FirstOrDefault(predicate);

        if (string.Equals(treeMode, "gpo", StringComparison.OrdinalIgnoreCase))
        {
            return PickBySource(entries, entry => IsGpoLikeSource(entry.Source) || IsGpoLikeSource(entry.WinningSource))
                   ?? entries[0];
        }

        if (string.Equals(treeMode, "mdm", StringComparison.OrdinalIgnoreCase))
        {
            return PickBySource(entries, entry => IsMdmLikeSource(entry.Source) || IsMdmLikeSource(entry.WinningSource))
                   ?? entries[0];
        }

        var winnerSource = ResolveConflictWinningSource(
            entries,
            entries.Select(entry => NormalizePolicySource(entry.Source))
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray());

        return entries.FirstOrDefault(entry =>
                   string.Equals(NormalizePolicySource(entry.Source), winnerSource, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(NormalizePolicySource(entry.WinningSource), winnerSource, StringComparison.OrdinalIgnoreCase))
               ?? entries[0];
    }

    private static string ResolvePolicyTreeSectionLabel(
        string treeMode,
        IntunePolicyResultEntry entry,
        PolicyComparisonInfo comparison)
    {
        if (string.Equals(treeMode, "mdm", StringComparison.OrdinalIgnoreCase))
        {
            var path = comparison.MdmPaths.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));
            if (!string.IsNullOrWhiteSpace(path))
            {
                return DeriveTreeCategoryPathFromMdmPath(path);
            }

            return string.Equals(entry.Scope, "User", StringComparison.OrdinalIgnoreCase)
                ? @"User\No MDM Path"
                : @"Device\No MDM Path";
        }

        if (string.Equals(treeMode, "gpo", StringComparison.OrdinalIgnoreCase))
        {
            var path = comparison.GpoCategoryPaths.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path))
                ?? comparison.GpoPaths.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));
            if (!string.IsNullOrWhiteSpace(path))
            {
                return NormalizePolicyFieldValue(path).Replace('/', '\\');
            }

            return string.Equals(entry.Scope, "User", StringComparison.OrdinalIgnoreCase)
                ? @"User\No GPO Path"
                : @"Device\No GPO Path";
        }

        return ResolvePolicySectionLabel(entry, comparison);
    }

    private static string DeriveTreeCategoryPathFromMdmPath(string path)
    {
        var normalized = NormalizePolicyFieldValue(path).Replace('\\', '/').Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.Empty;
        }

        if (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }

        var segments = normalized
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        if (segments.Count > 1)
        {
            segments.RemoveAt(segments.Count - 1);
        }

        return string.Join("\\", segments);
    }

    private static string[] BuildPolicyTreeSegments(string scope, string area)
    {
        var normalizedArea = NormalizePolicyFieldValue(area);
        if (string.IsNullOrWhiteSpace(normalizedArea))
        {
            return [];
        }

        if (string.Equals(scope, "Device", StringComparison.OrdinalIgnoreCase) &&
            normalizedArea.StartsWith(@"Computerkonfiguration\", StringComparison.OrdinalIgnoreCase))
        {
            normalizedArea = normalizedArea[@"Computerkonfiguration\".Length..];
        }
        else if (string.Equals(scope, "User", StringComparison.OrdinalIgnoreCase) &&
                 normalizedArea.StartsWith(@"Benutzerkonfiguration\", StringComparison.OrdinalIgnoreCase))
        {
            normalizedArea = normalizedArea[@"Benutzerkonfiguration\".Length..];
        }

        return normalizedArea
            .Split('\\', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();
    }

    private static void SortPolicyTreeNodes(List<PolicyTreeNode> nodes)
    {
        nodes.Sort((left, right) => StringComparer.OrdinalIgnoreCase.Compare(left.Label, right.Label));
        foreach (var node in nodes)
        {
            node.Sections.Sort((left, right) => StringComparer.OrdinalIgnoreCase.Compare(left.Area, right.Area));
            SortPolicyTreeNodes(node.Children);
        }
    }

    private static void AppendPolicyTreeNavHtml(StringBuilder sb, IReadOnlyList<PolicyTreeNode> nodes)
    {
        foreach (var node in nodes)
        {
            var totalEntries = CountPolicyTreeEntries(node);
            sb.AppendLine("        <li>");
            if (node.Children.Count > 0)
            {
                sb.AppendLine($"          <details open><summary>{WebUtility.HtmlEncode(node.Label)} ({totalEntries})</summary>");
                sb.AppendLine("            <ul>");
                AppendPolicyTreeNavHtml(sb, node.Children);
                foreach (var section in node.Sections)
                {
                    sb.AppendLine($"              <li data-section-id=\"{WebUtility.HtmlEncode(section.Id)}\"><a class=\"nav-item\" href=\"#{WebUtility.HtmlEncode(section.Id)}\">{WebUtility.HtmlEncode(section.Area)} ({section.Entries.Length})</a></li>");
                }

                sb.AppendLine("            </ul>");
                sb.AppendLine("          </details>");
            }
            else
            {
                foreach (var section in node.Sections)
                {
                    sb.AppendLine($"          <a class=\"nav-item\" data-section-id=\"{WebUtility.HtmlEncode(section.Id)}\" href=\"#{WebUtility.HtmlEncode(section.Id)}\">{WebUtility.HtmlEncode(node.Label)} ({section.Entries.Length})</a>");
                }
            }

            sb.AppendLine("        </li>");
        }
    }

    private static void AppendPolicyTreeHtml(
        StringBuilder sb,
        IReadOnlyList<PolicyTreeNode> nodes,
        IReadOnlyDictionary<string, PolicyComparisonInfo> comparisonLookup,
        string scope,
        int depth)
    {
        foreach (var node in nodes)
        {
            var cssClass = node.Children.Count == 0
                ? $"policy-node policy-leaf depth-{Math.Min(depth, 5)} is-expanded"
                : $"policy-node depth-{Math.Min(depth, 5)} is-expanded";
            sb.AppendLine($"        <div class=\"{cssClass}\" data-scope=\"{WebUtility.HtmlEncode(scope)}\">");
            sb.AppendLine("          <button type=\"button\" class=\"policy-node-toggle\" aria-expanded=\"true\">");
            sb.AppendLine("            <span class=\"policy-node-marker\" aria-hidden=\"true\"></span>");
            sb.AppendLine($"            <span class=\"policy-node-label\">{WebUtility.HtmlEncode(node.Label)} ({CountPolicyTreeEntries(node)})</span>");
            sb.AppendLine("          </button>");
            sb.AppendLine("          <div class=\"policy-node-body\">");
            foreach (var section in node.Sections)
            {
                AppendPolicySectionHtml(sb, section, comparisonLookup, scope);
            }

            if (node.Children.Count > 0)
            {
                AppendPolicyTreeHtml(sb, node.Children, comparisonLookup, scope, depth + 1);
            }

            sb.AppendLine("          </div>");
            sb.AppendLine("        </div>");
        }
    }

    private static void AppendPolicySectionHtml(
        StringBuilder sb,
        PolicyReportSection section,
        IReadOnlyDictionary<string, PolicyComparisonInfo> comparisonLookup,
        string scope)
    {
        sb.AppendLine($"            <article id=\"{WebUtility.HtmlEncode(section.Id)}\" class=\"section\" data-scope=\"{WebUtility.HtmlEncode(scope)}\">");
        sb.AppendLine("              <table>");
        sb.AppendLine("                <colgroup>");
        sb.AppendLine("                  <col class=\"col-setting\" />");
        sb.AppendLine("                  <col class=\"col-path\" />");
        sb.AppendLine("                  <col class=\"col-value\" />");
        sb.AppendLine("                  <col class=\"col-sourceinfo\" />");
        sb.AppendLine("                  <col class=\"col-status\" />");
        sb.AppendLine("                  <col class=\"col-code\" />");
        sb.AppendLine("                </colgroup>");
        sb.AppendLine("                <thead>");
        sb.AppendLine("                  <tr>");
        sb.AppendLine("                    <th>Setting</th>");
        sb.AppendLine("                    <th>OMA-URI / Path</th>");
        sb.AppendLine("                    <th>Current Value</th>");
        sb.AppendLine("                    <th>Policy Source</th>");
        sb.AppendLine("                    <th>Status</th>");
        sb.AppendLine("                    <th>Result Code</th>");
        sb.AppendLine("                  </tr>");
        sb.AppendLine("                </thead>");
        sb.AppendLine("                <tbody>");
        if (section.Entries.Length == 0)
        {
            sb.AppendLine("                  <tr><td colspan=\"6\">No entries in this section.</td></tr>");
        }
        else
        {
            foreach (var entry in section.Entries)
            {
                var conflictKey = CreateConflictKey(entry);
                var comparison = comparisonLookup.TryGetValue(conflictKey, out var existingComparison)
                    ? existingComparison
                    : BuildPolicyComparisonInfo([entry]);
                var statusClass = string.Equals(entry.Status, "Applied", StringComparison.OrdinalIgnoreCase)
                    ? "status status-applied"
                    : string.Equals(entry.Status, "Failed", StringComparison.OrdinalIgnoreCase)
                        ? "status status-failed"
                        : "status status-unknown";

                sb.AppendLine($"                  <tr data-entry-row=\"true\" data-kind=\"{WebUtility.HtmlEncode(comparison.GroupKind)}\">");
                sb.AppendLine($"                    <td>{WebUtility.HtmlEncode(entry.SettingName)}</td>");
                sb.AppendLine($"                    <td>{BuildPolicyPathCellHtml(entry, comparison)}</td>");
                sb.AppendLine($"                    <td>{BuildPolicyValueCellHtml(entry)}</td>");
                var sourceInfo = BuildPolicySourcePresentation(entry, comparison);
                var sourceCell = new StringBuilder();
                sourceCell.Append("<span class=\"winner-source\">");
                sourceCell.Append(WebUtility.HtmlEncode(sourceInfo.WinnerSource));
                sourceCell.Append("</span>");
                if (!string.IsNullOrWhiteSpace(sourceInfo.HintText))
                {
                    sourceCell.Append("<span class=\"source-hint\" title=\"");
                    sourceCell.Append(WebUtility.HtmlEncode(sourceInfo.HintText));
                    sourceCell.Append("\">(");
                    sourceCell.Append(WebUtility.HtmlEncode(sourceInfo.HintLabel));
                    sourceCell.Append(")</span>");
                }

                if (sourceInfo.HasValueConflict)
                {
                    sourceCell.Append("<span class=\"source-conflict\" title=\"");
                    sourceCell.Append(WebUtility.HtmlEncode(sourceInfo.ConflictText));
                    sourceCell.Append("\">Conflict</span>");
                }

                sb.AppendLine($"                    <td>{sourceCell}</td>");
                sb.AppendLine($"                    <td class=\"{statusClass}\">{WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(entry.Status) ? "Unknown" : entry.Status)}</td>");
                sb.AppendLine($"                    <td>{WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(entry.ResultCode) ? "-" : entry.ResultCode)}</td>");
                sb.AppendLine("                  </tr>");
                AppendPolicyDetailRowsHtml(sb, entry, comparison.GroupKind);
            }
        }

        sb.AppendLine("                </tbody>");
        sb.AppendLine("              </table>");
        sb.AppendLine($"              <div class=\"hint\">{section.Entries.Length} entr{(section.Entries.Length == 1 ? "y" : "ies")} in this section.</div>");
        sb.AppendLine("            </article>");
    }

    private static int CountPolicyTreeEntries(PolicyTreeNode node)
        => node.Sections.Sum(section => section.Entries.Length) + node.Children.Sum(CountPolicyTreeEntries);

    private static Dictionary<string, PolicyComparisonInfo> BuildPolicyComparisonLookup(IReadOnlyList<IntunePolicyResultEntry> entries)
    {
        var lookup = new Dictionary<string, PolicyComparisonInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var groupEntries in BuildConflictGroups(entries))
        {
            var comparison = BuildPolicyComparisonInfo(groupEntries);
            foreach (var entry in groupEntries)
            {
                foreach (var key in EnumerateConflictKeys(entry))
                {
                    lookup[key] = comparison;
                }
            }
        }

        return lookup;
    }

    private static IReadOnlyList<IntunePolicyResultEntry[]> BuildConflictGroups(IReadOnlyList<IntunePolicyResultEntry> entries)
    {
        if (entries.Count == 0)
        {
            return [];
        }

        var parents = new int[entries.Count];
        for (var index = 0; index < parents.Length; index++)
        {
            parents[index] = index;
        }

        var keyLookup = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < entries.Count; index++)
        {
            foreach (var key in EnumerateConflictKeys(entries[index]))
            {
                if (keyLookup.TryGetValue(key, out var existingIndex))
                {
                    Union(parents, index, existingIndex);
                }
                else
                {
                    keyLookup[key] = index;
                }
            }
        }

        return entries
            .Select((entry, index) => new { entry, index, root = FindRoot(parents, index) })
            .GroupBy(item => item.root)
            .OrderBy(group => group.Min(item => item.index))
            .Select(group => group.Select(item => item.entry).ToArray())
            .ToArray();

        static int FindRoot(int[] parents, int index)
        {
            while (parents[index] != index)
            {
                parents[index] = parents[parents[index]];
                index = parents[index];
            }

            return index;
        }

        static void Union(int[] parents, int left, int right)
        {
            var leftRoot = FindRoot(parents, left);
            var rightRoot = FindRoot(parents, right);
            if (leftRoot == rightRoot)
            {
                return;
            }

            parents[rightRoot] = leftRoot;
        }
    }

    private static PolicyComparisonInfo BuildPolicyComparisonInfo(IEnumerable<IntunePolicyResultEntry> entries)
    {
        var entryArray = entries.ToArray();
        var mdmPaths = entryArray
            .SelectMany(entry =>
            {
                var values = new List<string>(2);
                if (!string.IsNullOrWhiteSpace(entry.MdmPath))
                {
                    values.Add(entry.MdmPath);
                }
                else if (IsMdmLikeSource(entry.Source) && !string.IsNullOrWhiteSpace(entry.OmaUri))
                {
                    values.Add(entry.OmaUri);
                }

                return values;
            })
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var gpoPaths = entryArray
            .SelectMany(entry =>
            {
                var values = new List<string>(2);
                if (!string.IsNullOrWhiteSpace(entry.GpoPath))
                {
                    values.Add(entry.GpoPath);
                }
                else if (IsGpoLikeSource(entry.Source) && !string.IsNullOrWhiteSpace(entry.OmaUri))
                {
                    values.Add(entry.OmaUri);
                }

                return values;
            })
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var gpoCategoryPaths = entryArray
            .Select(entry => entry.GpoCategoryPath)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var sourceSummaries = entryArray
            .Select(BuildPolicySourceSummaryLine)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var valueConflictHint = BuildPolicyValueConflictHint(entryArray);

        return new PolicyComparisonInfo(
            DeterminePolicyGroupKind(entryArray),
            mdmPaths,
            gpoPaths,
            gpoCategoryPaths,
            sourceSummaries,
            !string.IsNullOrWhiteSpace(valueConflictHint),
            valueConflictHint);
    }

    private static string BuildPolicyValueConflictHint(IReadOnlyList<IntunePolicyResultEntry> entries)
    {
        var families = new List<(string Label, string[] Tokens, string[] DisplaySummaries)>(2);
        AddPolicyValueFamily(
            families,
            "MDM",
            entries,
            static entry => IsMdmLikeSource(ResolvePolicyComparisonSource(entry)));
        AddPolicyValueFamily(
            families,
            "GPO/Local",
            entries,
            static entry => IsGpoLikeSource(ResolvePolicyComparisonSource(entry)));

        if (families.Count < 2)
        {
            return string.Empty;
        }

        var first = families[0];
        var second = families[1];
        if (first.Tokens.Length == 0 || second.Tokens.Length == 0)
        {
            return string.Empty;
        }

        if (first.Tokens.SequenceEqual(second.Tokens, StringComparer.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        return $"{first.Label}: {string.Join(" || ", first.DisplaySummaries)} | {second.Label}: {string.Join(" || ", second.DisplaySummaries)}";
    }

    private static void AddPolicyValueFamily(
        ICollection<(string Label, string[] Tokens, string[] DisplaySummaries)> families,
        string label,
        IReadOnlyList<IntunePolicyResultEntry> entries,
        Func<IntunePolicyResultEntry, bool> predicate)
    {
        var familyEntries = entries
            .Where(predicate)
            .ToArray();
        if (familyEntries.Length == 0)
        {
            return;
        }

        var tokens = familyEntries
            .SelectMany(BuildPolicyValueTokensForComparison)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var displaySummaries = familyEntries
            .Select(BuildPolicySourceSummaryLine)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (displaySummaries.Length == 0)
        {
            return;
        }

        families.Add((label, tokens, displaySummaries));
    }

    private static string ResolvePolicyComparisonSource(IntunePolicyResultEntry entry)
    {
        var source = NormalizePolicySource(entry.Source);
        if (!string.IsNullOrWhiteSpace(source) &&
            !string.Equals(source, "Unknown", StringComparison.OrdinalIgnoreCase))
        {
            return source;
        }

        return NormalizePolicySource(entry.WinningSource);
    }

    private static IEnumerable<string> BuildPolicyValueTokensForComparison(IntunePolicyResultEntry entry)
    {
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var valuePart in FormatPolicyValueForDisplay(entry).Split(" | ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            AddPolicyValueComparisonToken(tokens, valuePart);
        }

        if (!string.IsNullOrWhiteSpace(entry.AdditionalDetails))
        {
            foreach (var line in entry.AdditionalDetails.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                foreach (var part in line.Split(" | ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    AddPolicyValueComparisonToken(tokens, part);
                }
            }
        }

        return tokens;
    }

    private static void AddPolicyValueComparisonToken(ISet<string> tokens, string? rawValue)
    {
        var normalized = NormalizePolicyValueTokenForComparison(rawValue);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return;
        }

        tokens.Add(normalized);
    }

    private static string NormalizePolicyValueTokenForComparison(string? rawValue)
    {
        var normalized = NormalizePolicyFieldValue(rawValue)
            .Trim('"');
        if (string.IsNullOrWhiteSpace(normalized) ||
            string.Equals(normalized, "-", StringComparison.Ordinal))
        {
            return string.Empty;
        }

        var extracted = normalized;
        var explicitSeparatorIndex = extracted.LastIndexOf(" = ", StringComparison.Ordinal);
        if (explicitSeparatorIndex >= 0 && explicitSeparatorIndex + 3 < extracted.Length)
        {
            extracted = extracted[(explicitSeparatorIndex + 3)..];
        }
        else
        {
            var equalsIndex = extracted.LastIndexOf('=');
            if (equalsIndex >= 0 && equalsIndex + 1 < extracted.Length)
            {
                extracted = extracted[(equalsIndex + 1)..];
            }
            else
            {
                var colonIndex = extracted.LastIndexOf(": ", StringComparison.Ordinal);
                if (colonIndex >= 0 && colonIndex + 2 < extracted.Length)
                {
                    extracted = extracted[(colonIndex + 2)..];
                }
            }
        }

        normalized = string.Join(
            " ",
            extracted
                .Replace('/', '\\')
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.Empty;
        }

        var lower = normalized.ToLowerInvariant();
        return lower switch
        {
            "enabled" or "aktiviert" or "true" => "enabled",
            "disabled" or "deaktiviert" or "false" => "disabled",
            _ when int.TryParse(lower, NumberStyles.Integer, CultureInfo.InvariantCulture, out _) => string.Empty,
            _ => lower
        };
    }

    private static string DeterminePolicyGroupKind(IEnumerable<IntunePolicyResultEntry> entries)
    {
        var entryArray = entries as IntunePolicyResultEntry[] ?? entries.ToArray();
        var sourceSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entryArray)
        {
            AddPolicySource(sourceSet, entry.Source);
            AddPolicySource(sourceSet, entry.WinningSource);
            if (!string.IsNullOrWhiteSpace(entry.DuplicateSources))
            {
                foreach (var candidate in entry.DuplicateSources.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    AddPolicySource(sourceSet, candidate);
                }
            }
        }

        var hasMdm = sourceSet.Contains("Mdm");
        var hasLinkedGpo = sourceSet.Contains("GroupPolicy");
        var hasLocalGpo = sourceSet.Contains("LocalPolicy") || sourceSet.Contains("RegistryPolicy");
        var hasAnyGpo = hasLinkedGpo || hasLocalGpo;

        if (hasMdm && hasAnyGpo)
        {
            return "hybrid";
        }

        if (hasMdm)
        {
            return "mdm-only";
        }

        if (hasLinkedGpo && hasLocalGpo)
        {
            return "gpo-or-local-only";
        }

        if (hasLinkedGpo)
        {
            return "gpo-only";
        }

        if (hasLocalGpo || hasAnyGpo)
        {
            return "local-only";
        }

        return "other";
    }

    private static void AddPolicySource(ISet<string> sourceSet, string? source)
    {
        var normalized = NormalizePolicySource(source);
        if (string.IsNullOrWhiteSpace(normalized) ||
            string.Equals(normalized, "Unknown", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        sourceSet.Add(normalized);
    }

    private static string ResolvePolicySectionLabel(IntunePolicyResultEntry entry, PolicyComparisonInfo comparison)
    {
        if (string.Equals(comparison.GroupKind, "gpo-only", StringComparison.OrdinalIgnoreCase) &&
            comparison.GpoCategoryPaths.Length > 0)
        {
            return comparison.GpoCategoryPaths[0];
        }

        return entry.Area;
    }

    private static string BuildPolicyPathCellHtml(IntunePolicyResultEntry entry, PolicyComparisonInfo comparison)
    {
        static string EncodeMany(IEnumerable<string> values)
            => string.Join("<br />", values.Select(value => WebUtility.HtmlEncode(value)));

        var activePath = WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(entry.OmaUri) ? "-" : entry.OmaUri);
        var mdmPathText = comparison.MdmPaths.Length == 0 ? "-" : EncodeMany(comparison.MdmPaths);
        var gpoPathText = comparison.GpoPaths.Length == 0 ? "-" : EncodeMany(comparison.GpoPaths);

        var sb = new StringBuilder();
        sb.Append("<div class=\"path-view path-view-active\">");
        sb.Append(activePath);
        sb.Append("</div>");
        sb.Append("<div class=\"path-view path-view-mdm\">");
        sb.Append(mdmPathText);
        sb.Append("</div>");
        sb.Append("<div class=\"path-view path-view-gpo\">");
        sb.Append(gpoPathText);
        sb.Append("</div>");
        sb.Append("<div class=\"path-view path-view-compare\">");
        sb.Append("<span class=\"path-line\"><span class=\"path-label\">MDM</span>");
        sb.Append(mdmPathText);
        sb.Append("</span>");
        sb.Append("<span class=\"path-line\"><span class=\"path-label\">GPO/ADMX</span>");
        sb.Append(gpoPathText);
        sb.Append("</span>");
        sb.Append("</div>");
        return sb.ToString();
    }

    private static string BuildPolicyValueCellHtml(IntunePolicyResultEntry entry)
    {
        return WebUtility.HtmlEncode(FormatPolicyValueForDisplay(entry));
    }

    private static void AppendPolicyDetailRowsHtml(StringBuilder sb, IntunePolicyResultEntry entry, string groupKind)
    {
        var detailRows = BuildPolicyDetailRows(entry.AdditionalDetails);
        if (detailRows.Length == 0)
        {
            return;
        }

        sb.AppendLine($"                  <tr class=\"detail-row\" data-detail-row=\"true\" data-kind=\"{WebUtility.HtmlEncode(groupKind)}\">");
        sb.AppendLine("                    <td colspan=\"6\">");
        sb.AppendLine("                      <div class=\"detail-host\">");
        sb.AppendLine("                        <table class=\"detail-table\">");
        sb.AppendLine("                          <tbody>");
        foreach (var row in detailRows)
        {
            sb.AppendLine("                            <tr>");
            foreach (var cell in row.Cells)
            {
                sb.Append("                              <td>");
                sb.Append(WebUtility.HtmlEncode(cell));
                sb.AppendLine("</td>");
            }

            sb.AppendLine("                            </tr>");
        }

        sb.AppendLine("                          </tbody>");
        sb.AppendLine("                        </table>");
        sb.AppendLine("                      </div>");
        sb.AppendLine("                    </td>");
        sb.AppendLine("                  </tr>");
    }

    private static PolicyDetailRow[] BuildPolicyDetailRows(string detailsText)
    {
        if (string.IsNullOrWhiteSpace(detailsText))
        {
            return [];
        }

        return detailsText
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(ParsePolicyDetailRow)
            .Where(static row => row.Cells.Length > 0)
            .ToArray();
    }

    private static PolicyDetailRow ParsePolicyDetailRow(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return new PolicyDetailRow([]);
        }

        var cells = line.Contains(" | ", StringComparison.Ordinal)
            ? line.Split(" | ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : line.Contains(" = ", StringComparison.Ordinal)
                ? line.Split(" = ", 2, StringSplitOptions.TrimEntries)
                : [line.Trim()];

        return new PolicyDetailRow(cells.Where(static value => !string.IsNullOrWhiteSpace(value)).ToArray());
    }

    private static double[] BuildPolicyResultColumnWidths(IReadOnlyList<IntunePolicyResultEntry> entries)
    {
        const int columnCount = 6;
        var min = new[] { 20d, 42d, 29d, 3d, 3d, 1.5d };
        var max = new[] { 38d, 72d, 52d, 4.5d, 4d, 2.5d };
        var observed = new[] { 16, 36, 20, 4, 3, 2 };

        foreach (var entry in entries)
        {
            observed[0] = Math.Max(observed[0], EstimatePolicyColumnLength(entry.SettingName, "-"));
            observed[1] = Math.Max(observed[1], EstimatePolicyColumnLength(BuildPolicyPathWidthText(entry), "-"));
            observed[2] = Math.Max(observed[2], EstimatePolicyColumnLength(entry.CurrentValue, "-"));
            observed[3] = Math.Max(observed[3], EstimatePolicyColumnLength(BuildPolicySourceWidthText(entry), "-"));
            observed[4] = Math.Max(observed[4], EstimatePolicyColumnLength(entry.Status, "Unknown"));
            observed[5] = Math.Max(observed[5], EstimatePolicyColumnLength(entry.ResultCode, "-"));
        }

        var widths = min.ToArray();
        var remaining = 100d - widths.Sum();
        if (remaining <= 0d)
        {
            return widths;
        }

        var weights = observed.Select(value => Math.Max(1d, Math.Sqrt(Math.Min(value, 160)))).ToArray();
        while (remaining > 0.0001d)
        {
            var eligibleWeight = 0d;
            for (var i = 0; i < columnCount; i++)
            {
                if (widths[i] + 0.0001d < max[i])
                {
                    eligibleWeight += weights[i];
                }
            }

            if (eligibleWeight <= 0d)
            {
                break;
            }

            var distributed = 0d;
            for (var i = 0; i < columnCount; i++)
            {
                var capacity = max[i] - widths[i];
                if (capacity <= 0.0001d)
                {
                    continue;
                }

                var share = remaining * (weights[i] / eligibleWeight);
                var add = Math.Min(capacity, share);
                widths[i] += add;
                distributed += add;
            }

            if (distributed <= 0.0001d)
            {
                break;
            }

            remaining -= distributed;
        }

        var rounded = widths.Select(value => Math.Round(value, 2, MidpointRounding.AwayFromZero)).ToArray();
        var correction = 100d - rounded.Sum();
        rounded[^1] += correction;
        return rounded;
    }

    private static string BuildPolicySourceWidthText(IntunePolicyResultEntry entry)
    {
        var sourceInfo = BuildPolicySourcePresentation(entry, BuildPolicyComparisonInfo([entry]));
        return sourceInfo.HasValueConflict
            ? sourceInfo.WinnerSource + " Conflict"
            : sourceInfo.WinnerSource;
    }

    private static string BuildPolicyPathWidthText(IntunePolicyResultEntry entry)
    {
        return string.Join(
            " | ",
            new[] { entry.OmaUri, entry.MdmPath, entry.GpoPath, entry.GpoCategoryPath }
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private static string BuildPolicySourceSummaryLine(IntunePolicyResultEntry entry)
    {
        var source = string.IsNullOrWhiteSpace(entry.Source) ? NormalizePolicySource(entry.WinningSource) : NormalizePolicySource(entry.Source);
        if (string.IsNullOrWhiteSpace(source) || string.Equals(source, "Unknown", StringComparison.OrdinalIgnoreCase))
        {
            source = string.IsNullOrWhiteSpace(entry.Source) ? "-" : entry.Source.Trim();
        }

        var value = BuildPolicySourceValueSummary(entry);
        return string.IsNullOrWhiteSpace(value)
            ? source
            : $"{source} = {value}";
    }

    private static string BuildPolicySourceValueSummary(IntunePolicyResultEntry entry)
    {
        var parts = new List<string>();
        var formattedValue = FormatPolicyValueForDisplay(entry);
        if (!string.IsNullOrWhiteSpace(formattedValue) && !string.Equals(formattedValue, "-", StringComparison.Ordinal))
        {
            parts.Add(formattedValue);
        }

        if (!string.IsNullOrWhiteSpace(entry.AdditionalDetails))
        {
            parts.AddRange(entry.AdditionalDetails
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(static value => !string.IsNullOrWhiteSpace(value)));
        }

        return string.Join(" | ", parts.Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private static string FormatPolicyValueForDisplay(IntunePolicyResultEntry entry)
    {
        var currentValue = NormalizePolicyFieldValue(entry.CurrentValue);
        if (string.IsNullOrWhiteSpace(currentValue))
        {
            return "-";
        }

        if (!currentValue.Contains('<', StringComparison.Ordinal))
        {
            return currentValue;
        }

        try
        {
            var document = XDocument.Parse("<root>" + currentValue + "</root>", LoadOptions.PreserveWhitespace);
            var parts = new List<string>();
            if (document.Root?.Elements().Any(static element => element.Name.LocalName.Equals("enabled", StringComparison.OrdinalIgnoreCase)) == true)
            {
                parts.Add("Enabled");
            }
            else if (document.Root?.Elements().Any(static element => element.Name.LocalName.Equals("disabled", StringComparison.OrdinalIgnoreCase)) == true)
            {
                parts.Add("Disabled");
            }

            foreach (var dataElement in document.Root?.Elements().Where(static element => element.Name.LocalName.Equals("data", StringComparison.OrdinalIgnoreCase)) ?? [])
            {
                var id = NormalizePolicyFieldValue(dataElement.Attribute("id")?.Value);
                var value = NormalizePolicyFieldValue(dataElement.Attribute("value")?.Value);
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                parts.Add($"{id} = {value}");
            }

            return parts.Count == 0 ? currentValue : string.Join(" | ", parts);
        }
        catch
        {
            return currentValue;
        }
    }

    private static PolicySourcePresentation BuildPolicySourcePresentation(IntunePolicyResultEntry entry, PolicyComparisonInfo comparison)
    {
        var winnerSource = string.IsNullOrWhiteSpace(entry.WinningSource)
            ? (string.IsNullOrWhiteSpace(entry.Source) ? "-" : entry.Source.Trim())
            : entry.WinningSource.Trim();
        if (string.IsNullOrWhiteSpace(winnerSource))
        {
            winnerSource = "-";
        }

        var sourceValues = new List<string>(4);
        AddSourceValue(sourceValues, entry.Source);
        AddSourceValue(sourceValues, entry.WinningSource);
        if (!string.IsNullOrWhiteSpace(entry.DuplicateSources))
        {
            foreach (var candidate in entry.DuplicateSources.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                AddSourceValue(sourceValues, candidate);
            }
        }

        if (sourceValues.Count == 0 && !string.Equals(winnerSource, "-", StringComparison.Ordinal))
        {
            sourceValues.Add(winnerSource);
        }

        var hasMdmSource = sourceValues.Any(value => string.Equals(value, "Mdm", StringComparison.OrdinalIgnoreCase));
        var isLinkedGpoWinner = string.Equals(winnerSource, "GroupPolicy", StringComparison.OrdinalIgnoreCase);
        var isLocalWinner =
            string.Equals(winnerSource, "LocalPolicy", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(winnerSource, "RegistryPolicy", StringComparison.OrdinalIgnoreCase);
        if ((isLinkedGpoWinner || isLocalWinner) && !hasMdmSource)
        {
            return new PolicySourcePresentation(
                winnerSource,
                isLocalWinner ? "Local only" : "GPO only",
                isLocalWinner
                    ? "No matching MDM source for this local policy setting in the current report."
                    : "No matching MDM source for this GPO setting in the current report.",
                comparison.HasValueConflict,
                comparison.ValueConflictHint);
        }

        var alternativeValues = sourceValues
            .Where(value => !string.Equals(value, winnerSource, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (alternativeValues.Length == 0)
        {
            return new PolicySourcePresentation(
                winnerSource,
                string.Empty,
                string.Empty,
                comparison.HasValueConflict,
                comparison.ValueConflictHint);
        }

        var hintParts = new List<string>(3);
        if (!string.IsNullOrWhiteSpace(entry.Source))
        {
            hintParts.Add("Source: " + entry.Source.Trim());
        }

        var alternativeSummaries = comparison.SourceSummaries
            .Where(summary => alternativeValues.Any(value => summary.StartsWith(value + " = ", StringComparison.OrdinalIgnoreCase) || string.Equals(summary, value, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        if (alternativeSummaries.Length > 0)
        {
            hintParts.Add("Other values: " + string.Join(" || ", alternativeSummaries));
        }
        else
        {
            hintParts.Add("Other values: " + string.Join(", ", alternativeValues));
        }

        if (entry.IsDuplicate)
        {
            hintParts.Add("Duplicate setting detected");
        }

        var hintText = string.Join(" | ", hintParts.Where(static value => !string.IsNullOrWhiteSpace(value)));
        var hintLabel = alternativeValues.Length == 1 ? "+1 alt" : $"+{alternativeValues.Length} alt";
        return new PolicySourcePresentation(
            winnerSource,
            hintLabel,
            hintText,
            comparison.HasValueConflict,
            comparison.ValueConflictHint);

        static void AddSourceValue(List<string> values, string? rawValue)
        {
            if (string.IsNullOrWhiteSpace(rawValue))
            {
                return;
            }

            var normalized = rawValue.Trim();
            if (values.Any(existing => string.Equals(existing, normalized, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            values.Add(normalized);
        }
    }

    private static int EstimatePolicyColumnLength(string? value, string fallback)
    {
        var text = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        return Math.Min(text.Length, 160);
    }

    private sealed record PolicyReportSection(string Id, string Scope, string Area, IntunePolicyResultEntry[] Entries);

    private sealed class PolicyTreeNode(string label)
    {
        public string Label { get; } = label;
        public List<PolicyTreeNode> Children { get; } = [];
        public List<PolicyReportSection> Sections { get; } = [];
    }

    private readonly record struct PolicyDetailRow(string[] Cells);
    private readonly record struct PolicySourcePresentation(string WinnerSource, string HintLabel, string HintText, bool HasValueConflict, string ConflictText);
    private readonly record struct PolicyComparisonInfo(string GroupKind, string[] MdmPaths, string[] GpoPaths, string[] GpoCategoryPaths, string[] SourceSummaries, bool HasValueConflict, string ValueConflictHint);

    private async ValueTask<LocalIntuneActionResult> ExecuteSimpleActionAsync(string host, string scriptBody, CancellationToken cancellationToken, string actionId)
    {
        var execution = await executor.ExecuteForHostAsync(host, scriptBody, cancellationToken);
        return execution.ExitCode == 0
            ? new LocalIntuneActionResult(true, execution.StdOut.Trim(), [], new Dictionary<string, string> { ["actionId"] = actionId })
            : Failed(actionId, NormalizeError(execution));
    }

    private static string LoadEmbeddedHelperScript(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            throw new InvalidOperationException("Embedded helper script file name must be provided.");
        }

        var assembly = typeof(LocalIntuneActionService).Assembly;
        var resourceName = assembly
            .GetManifestResourceNames()
            .FirstOrDefault(name => name.EndsWith($".{fileName}", StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(resourceName))
        {
            throw new InvalidOperationException($"Embedded helper script '{fileName}' was not found.");
        }

        using var stream = assembly.GetManifestResourceStream(resourceName)
                            ?? throw new InvalidOperationException($"Embedded helper script '{fileName}' could not be opened.");
        using var reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static LocalIntuneActionResult BuildHelperScriptResult(
        string actionId,
        string defaultMessage,
        string stdOut,
        IReadOnlyDictionary<string, string>? extraEvidence = null)
    {
        var payload = TryDeserializeHelperScriptPayload(stdOut);

        var message = payload?.Message;
        if (string.IsNullOrWhiteSpace(message))
        {
            message = defaultMessage;
        }

        var warnings = payload?.Warnings?
            .Where(static warning => !string.IsNullOrWhiteSpace(warning))
            .ToArray() ?? [];

        var outputText = payload?.OutputText;
        if (string.IsNullOrWhiteSpace(outputText))
        {
            outputText = stdOut.Trim();
        }

        var outputLineCount = payload?.OutputLineCount ?? CountNonEmptyLines(outputText);
        var evidence = new Dictionary<string, string>
        {
            ["actionId"] = actionId,
            ["moduleVersionInstalled"] = payload?.InstalledVersion ?? string.Empty,
            ["scriptPath"] = payload?.ScriptPath ?? string.Empty,
            ["outputLineCount"] = outputLineCount.ToString(CultureInfo.InvariantCulture),
            ["outputTruncated"] = (payload?.Truncated ?? false).ToString(CultureInfo.InvariantCulture),
            ["outputText"] = outputText ?? string.Empty
        };

        if (extraEvidence is not null)
        {
            foreach (var pair in extraEvidence)
            {
                evidence[pair.Key] = pair.Value;
            }
        }

        return new LocalIntuneActionResult(true, message, warnings, evidence);
    }

    private static HelperScriptPayload? TryDeserializeHelperScriptPayload(string stdOut)
    {
        if (TryDeserialize(stdOut, out var payload))
        {
            return payload;
        }

        var lines = stdOut.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var index = lines.Length - 1; index >= 0; index--)
        {
            var candidate = lines[index];
            if (!candidate.StartsWith('{') || !candidate.EndsWith('}'))
            {
                continue;
            }

            if (TryDeserialize(candidate, out payload))
            {
                return payload;
            }
        }

        return null;

        static bool TryDeserialize(string json, out HelperScriptPayload? parsed)
        {
            parsed = null;
            if (string.IsNullOrWhiteSpace(json))
            {
                return false;
            }

            try
            {
                parsed = JsonSerializer.Deserialize<HelperScriptPayload>(json, JsonOptions);
                return parsed is not null;
            }
            catch (JsonException)
            {
                return false;
            }
        }
    }

    private static int CountNonEmptyLines(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        return text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;
    }

    private static LocalIntuneActionResult Failed(string actionId, string error) =>
        new(false, error, [], new Dictionary<string, string> { ["actionId"] = actionId });

    private static string NormalizeError(PowershellExecutionResult execution)
    {
        var raw = string.IsNullOrWhiteSpace(execution.StdErr) ? execution.StdOut : execution.StdErr;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return $"PowerShell execution failed with exit code {execution.ExitCode}.";
        }

        var normalized = raw.Trim();
        if (normalized.Contains("CLIXML", StringComparison.OrdinalIgnoreCase))
        {
            var matches = Regex.Matches(normalized, "<S S=\"Error\">(?<msg>.*?)</S>", RegexOptions.Singleline);
            if (matches.Count > 0)
            {
                var parts = new List<string>(matches.Count);
                foreach (Match match in matches)
                {
                    var value = match.Groups["msg"].Value;
                    if (string.IsNullOrWhiteSpace(value))
                    {
                        continue;
                    }

                    var decoded = WebUtility.HtmlDecode(value)
                        .Replace("_x000D__x000A_", Environment.NewLine, StringComparison.Ordinal)
                        .Replace("_x000D_", string.Empty, StringComparison.Ordinal)
                        .Replace("_x000A_", Environment.NewLine, StringComparison.Ordinal)
                        .Trim();
                    if (!string.IsNullOrWhiteSpace(decoded))
                    {
                        parts.Add(decoded);
                    }
                }

                if (parts.Count > 0)
                {
                    return string.Join(Environment.NewLine, parts.Distinct(StringComparer.Ordinal));
                }
            }
        }

        return normalized;
    }

    private static DateTimeOffset? ParseTimestamp(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return DateTimeOffset.TryParse(value, out var parsed) ? parsed : null;
    }

    private static DateTimeOffset? ParseTimestampFlexible(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var dto))
        {
            return dto;
        }

        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var dt))
        {
            return new DateTimeOffset(dt);
        }

        return ParseTimestamp(value);
    }

    private static string ResolveHex(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return string.Empty;
        }

        var match = HexRegex().Match(input);
        return match.Success ? match.Value : string.Empty;
    }

    private readonly record struct FastImeTimelineSnapshotResult(bool Success, string Fingerprint, IReadOnlyList<ImeLogTimelineEntry> Entries);
    private readonly record struct FastImeLogAnalysisResult(
        bool Success,
        string Fingerprint,
        IReadOnlyList<ImeLogTimelineEntry> TimelineEntries,
        IReadOnlyList<ImeApplicationStatusEntry> ApplicationStatuses);
    private readonly record struct FastImeAppStatusResult(bool Success, IReadOnlyList<ImeApplicationStatusEntry> Entries);
    private readonly record struct TailLogEntry(int LineNumber, string RawText);
    private readonly record struct ParsedImeLogLine(DateTimeOffset? Timestamp, string Message, string Component, string Severity);
    private readonly record struct ImeTimelineClassification(
        string Flow,
        string Phase,
        string Effect,
        string CorrelationSummary,
        string DisplayComponent,
        string EntityType,
        string EntityId,
        string PolicyId,
        string SessionId,
        string UserId,
        string ResultCode);

    private sealed class RegistryAppState
    {
        public Dictionary<string, RegistryIdentityState> IdentityStates { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool IsV3Managed { get; set; }
        public string Intent { get; set; } = string.Empty;

        public bool HasAppKey => IdentityStates.Values.Any(state => state.HasAppKey);
        public bool HasGrs => IdentityStates.Values.Any(state => state.HasGrs);
    }

    private sealed class RegistryIdentityState(string identityId)
    {
        public string IdentityId { get; } = identityId;
        public bool HasAppKey { get; set; }
        public bool HasGrs { get; set; }
        public string InstallStatus { get; set; } = "Unknown";
        public DateTimeOffset? LastUpdated { get; set; }
        public string ResultCode { get; set; } = string.Empty;
        public string Source { get; set; } = "Registry Win32Apps";
        public string Details { get; set; } = string.Empty;
    }

    private sealed class MdmStatusPayload
    {
        public string? TimeCreated { get; init; }
        public int EventId { get; init; }
        public string? Message { get; init; }
    }

    private sealed class MdmReportPayload
    {
        public string? ReportDirectory { get; init; }
        public string? XmlPath { get; init; }
        public string? HtmlPath { get; init; }
    }

    private sealed class MdmReportParsePayload
    {
        public string? ReportDirectory { get; init; }
        public string? XmlPath { get; init; }
        public string? HtmlPath { get; init; }
        public int XmlNodeCount { get; init; }
        public int HtmlLineCount { get; init; }
    }

    private sealed class PolicyOverlayPayload
    {
        public string? Scope { get; init; }
        public string? Area { get; init; }
        public string? SettingName { get; init; }
        public string? OmaUri { get; init; }
        public string? CurrentValue { get; init; }
        public string? Status { get; init; }
        public string? ResultCode { get; init; }
        public string? Source { get; init; }
        public string? WinningSource { get; init; }
    }

    private sealed class PolicyProviderPayload
    {
        public string? ProviderId { get; init; }
        public string? Name { get; init; }
        public string? Source { get; init; }
    }

    private sealed class PolicyParsePayload
    {
        public string? Message { get; init; }
        public string? PolicyJson { get; init; }
        public string? SourceFile { get; init; }
        public int SourceLine { get; init; }
    }

    private sealed class ImeTimelinePayload
    {
        public string? TimeCreated { get; init; }
        public string? Severity { get; init; }
        public string? Component { get; init; }
        public string? Message { get; init; }
        public string? SourceFile { get; init; }
        public int LineNumber { get; init; }
        public string? RawLine { get; init; }
        public bool IsPolicyPayload { get; init; }
        public string? PolicyJson { get; init; }
    }

    private sealed class ImeApplicationStatusPayload
    {
        public string? AppId { get; init; }
        public string? AppName { get; init; }
        public string? Intent { get; init; }
        public string? TargetInstallContext { get; init; }
        public string? InstallStatus { get; init; }
        public string? LastUpdated { get; init; }
        public string? ResultCode { get; init; }
        public string? SourceFile { get; init; }
        public string? LastMessage { get; init; }
        public bool? IsInstalledForAnyIdentity { get; init; }
        public List<ImeApplicationIdentityStatusPayload>? IdentityStatuses { get; init; }
    }

    private sealed class ImeApplicationIdentityStatusPayload
    {
        public string? IdentityId { get; init; }
        public string? Scope { get; init; }
        public string? InstallStatus { get; init; }
        public string? LastUpdated { get; init; }
        public string? ResultCode { get; init; }
        public string? Source { get; init; }
        public string? Details { get; init; }
    }

    private sealed class HealthEvalPayload
    {
        public string? Message { get; init; }
        public string? ClientHealthTail { get; init; }
    }

    private sealed class ImeRestartPayload
    {
        public string? Message { get; init; }
        public string? Status { get; init; }
    }

    private sealed class ImeTestModePayload
    {
        public string? Message { get; init; }
        public bool IsEnabled { get; init; }
        public string? RawValue { get; init; }
    }

    private sealed class RetryPayload
    {
        public string? Message { get; init; }
        public string? BackupPath { get; init; }
    }

    private sealed class RetryAllPayload
    {
        public string? Message { get; init; }
        public string? BackupRoot { get; init; }
        public string? RestartMessage { get; init; }
    }

    private sealed class EventLogExportPayload
    {
        public string? Message { get; init; }
        public string? Mdm { get; init; }
        public string? Udr { get; init; }
        public string? Provisioning { get; init; }
    }

    private sealed class BundlePayload
    {
        public string? Message { get; init; }
        public string? ZipPath { get; init; }
        public string? BundleRoot { get; init; }
    }

    private sealed class HelperScriptPayload
    {
        public string? Message { get; init; }
        public string? ScriptPath { get; init; }
        public string? InstalledVersion { get; init; }
        public string? OutputText { get; init; }
        public int OutputLineCount { get; init; }
        public bool Truncated { get; init; }
        public List<string>? Warnings { get; init; }
    }
}
