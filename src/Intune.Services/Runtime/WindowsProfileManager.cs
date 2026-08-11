using System.Text.Json;
using System.Text.RegularExpressions;
using WindowsClientCenter.Intune.Services.Contracts;
using WindowsClientCenter.Intune.Services.Models;

namespace WindowsClientCenter.Intune.Services.Runtime;

internal sealed partial class WindowsProfileManager(IPowerShellExecutor executor) : IWindowsProfileManager
{
    public async ValueTask<WindowsProfileSnapshot> GetProfilesAsync(string host, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(host))
        {
            return new WindowsProfileSnapshot(
                string.Empty,
                false,
                [],
                new WindowsProfilePolicyInfo(null, null, [], "Not configured"),
                ["No host was provided."]);
        }

        var normalizedHost = host.Trim();
        try
        {
            var execution = await executor.ExecuteForHostAsync(normalizedHost, BuildGetProfilesScriptBody(), cancellationToken);
            if (execution.ExitCode != 0)
            {
                return new WindowsProfileSnapshot(
                    normalizedHost,
                    LocalPowerShellExecutor.IsLocalHost(normalizedHost),
                    [],
                    new WindowsProfilePolicyInfo(null, null, [], "Not configured"),
                    [NormalizeError(execution)]);
            }

            var payload = JsonSerializer.Deserialize<ProfileInventoryPayload>(
                string.IsNullOrWhiteSpace(execution.StdOut) ? "{}" : execution.StdOut,
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

            var profiles = (payload?.Profiles ?? [])
                .Select(static item => new WindowsProfileEntry(
                    item.AccountName ?? string.Empty,
                    item.Sid ?? string.Empty,
                    item.LocalPath ?? string.Empty,
                    DateTimeOffset.TryParse(item.LastUseTimeUtc, out var lastUseTimeUtc) ? lastUseTimeUtc : null,
                    item.ServerProfilePath ?? string.Empty,
                    item.IsLoaded,
                    item.IsTemporary,
                    item.IsRoaming,
                    item.IsMandatory,
                    item.IsCorrupted,
                    item.IsSpecial))
                .Where(static item => !item.IsSpecial)
                .OrderBy(static item => item.AccountName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static item => item.LocalPath, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var policy = new WindowsProfilePolicyInfo(
                payload?.Policy?.MaxProfileSizeMb,
                payload?.Policy?.IncludesRegistryInQuota,
                ParseExcludedPaths(payload?.Policy?.ExcludedRelativePaths),
                string.IsNullOrWhiteSpace(payload?.Policy?.Source) ? "Not configured" : payload!.Policy!.Source!.Trim());

            return new WindowsProfileSnapshot(
                normalizedHost,
                LocalPowerShellExecutor.IsLocalHost(normalizedHost),
                profiles,
                policy,
                payload?.Warnings?.Where(static warning => !string.IsNullOrWhiteSpace(warning)).ToArray() ?? []);
        }
        catch (Exception ex)
        {
            return new WindowsProfileSnapshot(
                normalizedHost,
                LocalPowerShellExecutor.IsLocalHost(normalizedHost),
                [],
                new WindowsProfilePolicyInfo(null, null, [], "Not configured"),
                [ex.Message]);
        }
    }

    public async ValueTask<WindowsProfileSizeResult> CalculateProfileSizeAsync(
        string host,
        string profileLocalPath,
        ProfileSizeCalculationMode mode,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(host))
        {
            return new WindowsProfileSizeResult(string.Empty, mode, 0, 0, 0, ["No host was provided."]);
        }

        if (string.IsNullOrWhiteSpace(profileLocalPath))
        {
            return new WindowsProfileSizeResult(string.Empty, mode, 0, 0, 0, ["No profile path was provided."]);
        }

        var normalizedHost = host.Trim();
        var normalizedProfilePath = profileLocalPath.Trim();
        var script = BuildCalculateProfileSizeScriptBody(normalizedProfilePath, mode);
        var execution = await executor.ExecuteForHostAsync(normalizedHost, script, cancellationToken);
        if (execution.ExitCode >= 8)
        {
            return new WindowsProfileSizeResult(normalizedProfilePath, mode, 0, 0, 0, [NormalizeError(execution)]);
        }

        return ParseRobocopyResult(normalizedProfilePath, mode, execution.StdOut, execution.StdErr);
    }

    public async ValueTask<DeviceActionResult> DeleteProfileAsync(string host, string sid, string profileLocalPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(host))
        {
            return DeviceActionResult.Fail("No host was provided.", "no_host");
        }

        if (string.IsNullOrWhiteSpace(sid))
        {
            return DeviceActionResult.Fail("No profile SID was provided.", "no_profile_sid");
        }

        if (string.IsNullOrWhiteSpace(profileLocalPath))
        {
            return DeviceActionResult.Fail("No profile path was provided.", "no_profile_path");
        }

        var normalizedHost = host.Trim();
        var execution = await executor.ExecuteForHostAsync(
            normalizedHost,
            BuildDeleteProfileScriptBody(sid.Trim(), profileLocalPath.Trim()),
            cancellationToken);

        return execution.ExitCode == 0
            ? DeviceActionResult.Ok(string.IsNullOrWhiteSpace(execution.StdOut)
                ? $"Profile '{profileLocalPath.Trim()}' removed on '{normalizedHost}'."
                : execution.StdOut.Trim())
            : DeviceActionResult.Fail(
                $"Deleting profile '{profileLocalPath.Trim()}' failed on '{normalizedHost}': {NormalizeError(execution)}",
                "delete_profile_failed");
    }

    internal static string BuildGetProfilesScriptBody()
    {
        return
            "$warnings = New-Object System.Collections.Generic.List[string];" +
            "function Resolve-AccountName([string]$sid) {" +
            "  if ([string]::IsNullOrWhiteSpace($sid)) { return '' };" +
            "  try { return ([System.Security.Principal.SecurityIdentifier]::new($sid).Translate([System.Security.Principal.NTAccount])).Value } catch { return $sid };" +
            "};" +
            "function Convert-ProfileTime([string]$value) {" +
            "  if ([string]::IsNullOrWhiteSpace($value)) { return $null };" +
            "  try { return [System.Management.ManagementDateTimeConverter]::ToDateTime($value).ToUniversalTime().ToString('o') } catch { return $null };" +
            "};" +
            "$policy = [pscustomobject]@{ MaxProfileSizeMb = $null; IncludesRegistryInQuota = $null; ExcludedRelativePaths = @(); Source = 'Not configured' };" +
            "$policyPaths = @('HKLM:\\SOFTWARE\\Policies\\Microsoft\\Windows\\System','HKLM:\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Winlogon');" +
            "foreach ($policyPath in $policyPaths) {" +
            "  try {" +
            "    if (-not (Test-Path -LiteralPath $policyPath)) { continue };" +
            "    $props = Get-ItemProperty -LiteralPath $policyPath -ErrorAction Stop;" +
            "    $hasAny = $false;" +
            "    if ($null -ne $props.MaxProfileSize) { $policy.MaxProfileSizeMb = [int][Math]::Ceiling(([double]$props.MaxProfileSize) / 1024.0); $hasAny = $true };" +
            "    if ($null -ne $props.IncludeRegInProQuota) { $policy.IncludesRegistryInQuota = [int]$props.IncludeRegInProQuota -ne 0; $hasAny = $true };" +
            "    if (-not [string]::IsNullOrWhiteSpace([string]$props.ExcludeProfileDirs)) {" +
            "      $policy.ExcludedRelativePaths = @([string]$props.ExcludeProfileDirs -split '[;,\\r\\n]+' | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | ForEach-Object { $_.Trim() } | Select-Object -Unique);" +
            "      $hasAny = $true;" +
            "    };" +
            "    if ($hasAny) { $policy.Source = $policyPath; break };" +
            "  } catch {" +
            "    $warnings.Add('Could not read profile policy from ' + $policyPath + ': ' + $_.Exception.Message) | Out-Null;" +
            "  }" +
            "};" +
            "try {" +
            "  $profiles = @(Get-CimInstance -ClassName Win32_UserProfile -ErrorAction Stop | ForEach-Object {" +
            "    $status = 0;" +
            "    try { $status = [int]$_.Status } catch { $status = 0 };" +
            "    [pscustomobject]@{" +
            "      AccountName = Resolve-AccountName ([string]$_.SID);" +
            "      Sid = [string]$_.SID;" +
            "      LocalPath = [string]$_.LocalPath;" +
            "      LastUseTimeUtc = Convert-ProfileTime ([string]$_.LastUseTime);" +
            "      ServerProfilePath = [string]($_.RoamingPath ?? '');" +
            "      IsLoaded = [bool]$_.Loaded;" +
            "      IsTemporary = ($status -band 1) -ne 0;" +
            "      IsRoaming = ($status -band 2) -ne 0 -or [bool]$_.RoamingConfigured -or -not [string]::IsNullOrWhiteSpace([string]$_.RoamingPath);" +
            "      IsMandatory = ($status -band 4) -ne 0;" +
            "      IsCorrupted = ($status -band 8) -ne 0;" +
            "      IsSpecial = [bool]$_.Special" +
            "    };" +
            "  });" +
            "} catch {" +
            "  $warnings.Add($_.Exception.Message) | Out-Null;" +
            "  $profiles = @();" +
            "};" +
            "$payload = [pscustomobject]@{ Profiles = @($profiles); Policy = $policy; Warnings = @($warnings) };" +
            "$payload | ConvertTo-Json -Depth 6 -Compress;";
    }

    internal static string BuildCalculateProfileSizeScriptBody(string profileLocalPath, ProfileSizeCalculationMode mode)
    {
        var escapedPath = EscapePowerShellSingleQuotedString(profileLocalPath);
        var usePolicyExclusions = mode == ProfileSizeCalculationMode.PolicyExcluded ? "$true" : "$false";
        return
            "$ErrorActionPreference='Stop';" +
            $"$profilePath='{escapedPath}';" +
            $"$usePolicyExclusions={usePolicyExclusions};" +
            "if (-not (Test-Path -LiteralPath $profilePath)) { throw ('Profile path not found: ' + $profilePath) };" +
            "$excludePaths = @();" +
            "if ($usePolicyExclusions) {" +
            "  $policyPaths = @('HKLM:\\SOFTWARE\\Policies\\Microsoft\\Windows\\System','HKLM:\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Winlogon');" +
            "  foreach ($policyPath in $policyPaths) {" +
            "    try {" +
            "      if (-not (Test-Path -LiteralPath $policyPath)) { continue };" +
            "      $excluded = [string](Get-ItemProperty -LiteralPath $policyPath -Name 'ExcludeProfileDirs' -ErrorAction SilentlyContinue).ExcludeProfileDirs;" +
            "      if ([string]::IsNullOrWhiteSpace($excluded)) { continue };" +
            "      $excludePaths = @($excluded -split '[;,\\r\\n]+' | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | ForEach-Object { Join-Path -Path $profilePath -ChildPath $_.Trim() } | Select-Object -Unique);" +
            "      if ($excludePaths.Count -gt 0) { break };" +
            "    } catch { }" +
            "  }" +
            "};" +
            "$destination = Join-Path -Path ([System.IO.Path]::GetTempPath()) -ChildPath ('ICC-ProfileSize-' + [guid]::NewGuid().ToString('N'));" +
            "New-Item -ItemType Directory -Path $destination -Force | Out-Null;" +
            "try {" +
            "  $arguments = @($profilePath, $destination, '*', '/L', '/E', '/R:0', '/W:0', '/XJ', '/BYTES', '/FP', '/TS', '/NP', '/NFL', '/NDL');" +
            "  if ($excludePaths.Count -gt 0) { $arguments += '/XD'; $arguments += $excludePaths };" +
            "  $output = & robocopy @arguments 2>&1 | Out-String;" +
            "  Write-Output $output;" +
            "  exit $LASTEXITCODE;" +
            "} finally {" +
            "  Remove-Item -LiteralPath $destination -Recurse -Force -ErrorAction SilentlyContinue;" +
            "};";
    }

    internal static string BuildDeleteProfileScriptBody(string sid, string profileLocalPath)
    {
        return
            "$ErrorActionPreference='Stop';" +
            $"$sid='{EscapePowerShellSingleQuotedString(sid)}';" +
            $"$profilePath='{EscapePowerShellSingleQuotedString(profileLocalPath)}';" +
            "$profile = Get-CimInstance -ClassName Win32_UserProfile -Filter (\"SID='\" + $sid.Replace(\"'\", \"''\") + \"'\") -ErrorAction Stop;" +
            "if ($null -eq $profile) { throw ('Profile with SID ' + $sid + ' was not found.') };" +
            "if ([bool]$profile.Loaded) { throw ('Profile ' + $profilePath + ' is currently loaded and cannot be removed.') };" +
            "$registryPath = 'HKLM:\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\ProfileList\\' + $sid;" +
            "$timestamp = Get-Date -Format 'yyyyMMddHHmmss';" +
            "$renamedPath = $profilePath + '.deleted-' + $timestamp;" +
            "if (-not (Test-Path -LiteralPath $profilePath)) { throw ('Profile path not found: ' + $profilePath) };" +
            "if (Test-Path -LiteralPath $renamedPath) { throw ('Target rename path already exists: ' + $renamedPath) };" +
            "Rename-Item -LiteralPath $profilePath -NewName ([System.IO.Path]::GetFileName($renamedPath)) -ErrorAction Stop;" +
            "if (Test-Path -LiteralPath $registryPath) {" +
            "  Remove-Item -LiteralPath $registryPath -Recurse -Force -ErrorAction Stop;" +
            "};" +
            "Write-Output ('Profile ' + $profilePath + ' renamed to ' + $renamedPath + ' and registry key removed.');";
    }

    internal static WindowsProfileSizeResult ParseRobocopyResult(
        string profileLocalPath,
        ProfileSizeCalculationMode mode,
        string? standardOutput,
        string? standardError)
    {
        var combinedOutput = string.Join(
            Environment.NewLine,
            new[] { standardOutput, standardError }.Where(static value => !string.IsNullOrWhiteSpace(value)));
        var lines = combinedOutput.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return new WindowsProfileSizeResult(
            profileLocalPath,
            mode,
            ParseSummaryValue(lines, "Bytes"),
            (int)ParseSummaryValue(lines, "Files"),
            (int)ParseSummaryValue(lines, "Dirs"),
            []);
    }

    private static IReadOnlyList<string> ParseExcludedPaths(string[]? paths)
    {
        return (paths ?? [])
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(static path => path.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static long ParseSummaryValue(IEnumerable<string> lines, string label)
    {
        foreach (var line in lines)
        {
            if (!line.StartsWith(label, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var match = SummaryNumberRegex().Match(line);
            if (match.Success && long.TryParse(match.Groups["value"].Value.Replace(",", string.Empty), out var value))
            {
                return value;
            }
        }

        return 0;
    }

    private static string NormalizeError(PowershellExecutionResult execution)
    {
        return string.IsNullOrWhiteSpace(execution.StdErr)
            ? string.IsNullOrWhiteSpace(execution.StdOut) ? "Unknown error." : execution.StdOut.Trim()
            : execution.StdErr.Trim();
    }

    private static string EscapePowerShellSingleQuotedString(string value)
    {
        return value.Replace("'", "''", StringComparison.Ordinal);
    }

    [GeneratedRegex(@":\s*(?<value>[\d,]+)")]
    private static partial Regex SummaryNumberRegex();

    private sealed class ProfileInventoryPayload
    {
        public ProfileInventoryPayloadItem[]? Profiles { get; set; }
        public ProfilePolicyPayload? Policy { get; set; }
        public string[]? Warnings { get; set; }
    }

    private sealed class ProfileInventoryPayloadItem
    {
        public string? AccountName { get; set; }
        public string? Sid { get; set; }
        public string? LocalPath { get; set; }
        public string? LastUseTimeUtc { get; set; }
        public string? ServerProfilePath { get; set; }
        public bool IsLoaded { get; set; }
        public bool IsTemporary { get; set; }
        public bool IsRoaming { get; set; }
        public bool IsMandatory { get; set; }
        public bool IsCorrupted { get; set; }
        public bool IsSpecial { get; set; }
    }

    private sealed class ProfilePolicyPayload
    {
        public int? MaxProfileSizeMb { get; set; }
        public bool? IncludesRegistryInQuota { get; set; }
        public string[]? ExcludedRelativePaths { get; set; }
        public string? Source { get; set; }
    }
}
