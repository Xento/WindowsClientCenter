using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Diagnostics;
using WindowsClientCenter.Defender.Contracts;
using WindowsClientCenter.Defender.Contracts.Models;
using WindowsClientCenter.Intune.Services.Contracts;

namespace WindowsClientCenter.Intune.Services.Runtime;

internal sealed class LocalDefenderDiagnosticsService(IPowerShellExecutor executor, HttpClient? httpClient = null, IntuneRuntimeOptions? options = null) : IDefenderDiagnosticsService
{
    private const string DefenderOperationalLog = "Microsoft-Windows-Windows Defender/Operational";
    private const string DefenderUpdatesUrl = "https://www.microsoft.com/en-us/wdsi/defenderupdates";
    private const string DefenderReleaseNotesUrl = "https://www.microsoft.com/en-us/wdsi/definitions/antimalware-definition-release-notes";
    private const double DefaultSignatureWarningThresholdHours = 36;
    private const double DefaultSignatureCriticalThresholdHours = 72;
    private const int AllowedDefinitionVersionLag = 1;
    private static readonly TimeSpan LatestVersionsCacheDuration = TimeSpan.FromHours(2);
    private static readonly Regex SecurityIntelligenceVersionRegex = new(
        @"<li>\s*Version:\s*<span>(?<value>[^<]+)</span>\s*</li>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex EngineVersionRegex = new(
        @"<li>\s*Engine Version:\s*<span>(?<value>[^<]+)</span>\s*</li>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex PlatformVersionRegex = new(
        @"<li>\s*Platform Version:\s*<span>(?<value>[^<]+)</span>\s*</li>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex ReleasedRegex = new(
        @"<li>\s*Released:\s*<span[^>]*>(?<value>[^<]+)</span>\s*</li>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };
    private static readonly IReadOnlyDictionary<string, string> AsrRuleNameById = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["be9ba2d9-53ea-4cdc-84e5-9b1eeee46550"] = "Block executable content from email client and webmail",
        ["d4f940ab-401b-4efc-aadc-ad5f3c50688a"] = "Block all Office applications from creating child processes",
        ["3b576869-a4ec-4529-8536-b80a7769e899"] = "Block Office applications from creating executable content",
        ["75668c1f-73b5-4cf0-bb93-3ecf5cb7cc84"] = "Block Office applications from injecting code into other processes",
        ["d3e037e1-3eb8-44c8-a917-57927947596d"] = "Block JavaScript or VBScript from launching downloaded executable content",
        ["5beb7efe-fd9a-4556-801d-275e5ffc04cc"] = "Block execution of potentially obfuscated scripts",
        ["92e97fa1-2edf-4476-bdd6-9dd0b4dddc7b"] = "Block Win32 API calls from Office macros",
        ["01443614-cd74-433a-b99e-2ecdc07bfc25"] = "Block executable files from running unless they meet prevalence, age, or trusted list criteria",
        ["c1db55ab-c21a-4637-bb3f-a12568109d35"] = "Use advanced protection against ransomware",
        ["9e6c4e1f-7d60-472f-ba1a-a39ef669e4b2"] = "Block credential stealing from LSASS",
        ["b2b3f03d-6a65-4f7b-a9c7-1c7ef74a9ba4"] = "Block untrusted and unsigned processes that run from USB",
        ["26190899-1602-49e8-8b27-eb1d0a1ce869"] = "Block Office communication applications from creating child processes",
        ["7674ba52-37eb-4a4f-a9a1-f0f9a1619a2c"] = "Block Adobe Reader from creating child processes",
        ["e6db77e5-3df2-4cf1-b95a-636979351e5b"] = "Block persistence through WMI event subscription",
        ["d1e49aac-8f56-4280-b9ba-993a6d77406c"] = "Block process creations originating from PSExec and WMI commands",
        ["33ddedf1-c6e0-47cb-833e-de6133960387"] = "Block rebooting machine in Safe Mode",
        ["c0033c00-d16d-4114-a5a0-dc9b3a7d2ceb"] = "Block use of copied or impersonated system tools",
        ["a8f5898e-1dc8-49a9-9878-85004b8a61e6"] = "Block webshell creation for servers",
        ["56a863a9-875e-4185-98a7-b882c64b5ce5"] = "Block abuse of exploited vulnerable signed drivers"
    };
    private static readonly IReadOnlyDictionary<string, string> PuaProtectionValueMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["0"] = "Disabled",
        ["1"] = "Enabled",
        ["2"] = "Audit"
    };
    private static readonly IReadOnlyDictionary<string, string> MapsReportingValueMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["0"] = "Disabled",
        ["1"] = "Basic membership",
        ["2"] = "Advanced membership"
    };
    private static readonly IReadOnlyDictionary<string, string> SubmitSamplesConsentValueMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["0"] = "Always prompt",
        ["1"] = "Send safe samples automatically",
        ["2"] = "Never send",
        ["3"] = "Send all samples automatically"
    };
    private static readonly IReadOnlyDictionary<string, string> CloudBlockLevelValueMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["0"] = "Default",
        ["1"] = "High",
        ["2"] = "High",
        ["4"] = "High plus",
        ["6"] = "Zero tolerance"
    };
    private static readonly IReadOnlyDictionary<string, string> ScheduleDayValueMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["0"] = "Every day",
        ["1"] = "Sunday",
        ["2"] = "Monday",
        ["3"] = "Tuesday",
        ["4"] = "Wednesday",
        ["5"] = "Thursday",
        ["6"] = "Friday",
        ["7"] = "Saturday",
        ["8"] = "Never"
    };
    private static readonly IReadOnlyDictionary<string, string> ControlledFolderAccessValueMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["0"] = "Disabled",
        ["1"] = "Enabled",
        ["2"] = "Audit mode"
    };
    private readonly SemaphoreSlim _latestVersionsCacheLock = new(1, 1);
    private readonly double _signatureWarningThresholdHours = ResolveThresholdHours(
        options?.DefenderSecurityIntelligenceWarningThresholdHours,
        DefaultSignatureWarningThresholdHours);
    private readonly double _signatureCriticalThresholdHours = ResolveThresholdHours(
        options?.DefenderSecurityIntelligenceCriticalThresholdHours,
        DefaultSignatureCriticalThresholdHours);
    private DefenderLatestVersionInfo? _latestVersionsCache;
    private DateTimeOffset _latestVersionsCacheUpdatedUtc = DateTimeOffset.MinValue;

    public async ValueTask<DefenderSnapshot> GetSnapshotAsync(string host, CancellationToken cancellationToken)
    {
        var result = await GetSnapshotDiagnosticsAsync(host, cancellationToken);
        return result.Snapshot;
    }

    public async ValueTask<DefenderSnapshotDiagnosticsResult> GetSnapshotDiagnosticsAsync(string host, CancellationToken cancellationToken)
    {
        var timings = new List<string>();
        var totalTimer = Stopwatch.StartNew();

        var baselineTask = MeasureAsync(
            "Microsoft version baseline lookup",
            timings,
            () => TryGetLatestDefenderVersionsAsync(cancellationToken).AsTask());

        var executionTimer = Stopwatch.StartNew();
        var execution = await executor.ExecuteForHostAsync(host, BuildSnapshotScript(), cancellationToken);
        timings.Add($"PowerShell defender snapshot script completed in {executionTimer.ElapsedMilliseconds} ms.");
        if (execution.ExitCode != 0)
        {
            throw new InvalidOperationException(NormalizeError(execution));
        }

        SnapshotPayload payload;
        var parseTimer = Stopwatch.StartNew();
        try
        {
            payload = JsonSerializer.Deserialize<SnapshotPayload>(execution.StdOut, JsonOptions)
                      ?? throw new InvalidOperationException("Defender snapshot payload was empty.");
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            throw new InvalidOperationException($"Defender snapshot parsing failed: {ex.Message}", ex);
        }
        timings.Add($"Defender snapshot payload parsing completed in {parseTimer.ElapsedMilliseconds} ms.");

        var evaluationTimer = Stopwatch.StartNew();
        var managedBy = ResolveManagedBy(
            payload.IsGpoManaged,
            payload.IsMdmManaged,
            payload.IsMdeManaged,
            payload.ManagedDefenderProductType,
            payload.EnrollmentStatus,
            payload.OnboardingState);
        var isManaged = !string.Equals(managedBy, "Local", StringComparison.Ordinal) &&
                        !string.Equals(managedBy, "Unknown", StringComparison.Ordinal);

        var latestVersions = await baselineTask;
        var signatureVersionLag = ResolveSignatureVersionLag(payload.AntivirusSignatureVersion, latestVersions);

        var versions = new DefenderVersionInfo(
            payload.EngineVersion ?? "Unknown",
            payload.ProductVersion ?? "Unknown",
            payload.AntivirusSignatureVersion ?? "Unknown",
            payload.AntispywareSignatureVersion ?? "Unknown",
            payload.NisEngineVersion ?? "Unknown",
            payload.NisSignatureVersion ?? "Unknown",
            ParseTimestamp(payload.SignatureLastUpdatedUtc),
            payload.SignatureAgeHours,
            IsSignatureOutdated(payload.SignatureAgeHours, signatureVersionLag),
            _signatureWarningThresholdHours,
            _signatureCriticalThresholdHours);

        var protection = new DefenderProtectionStatus(
            payload.AntivirusEnabled,
            payload.RealtimeProtectionEnabled,
            payload.BehaviorMonitorEnabled,
            payload.IoavProtectionEnabled,
            payload.OnAccessProtectionEnabled,
            payload.NisEnabled,
            payload.TamperProtectionEnabled,
            payload.RunningMode ?? "Unknown");

        var scans = new DefenderScanInfo(
            ParseTimestamp(payload.QuickScanStartUtc),
            ParseTimestamp(payload.QuickScanEndUtc),
            ParseTimestamp(payload.FullScanStartUtc),
            ParseTimestamp(payload.FullScanEndUtc),
            ParseTimestamp(payload.LastScanUtc));

        var healthLevel = ResolveHealthLevel(
            protection,
            versions,
            signatureVersionLag,
            payload.ActiveDetectionCount,
            payload.ActiveHighOrCriticalDetectionCount,
            payload.Notes ?? []);

        var healthSummary = ResolveHealthSummary(
            healthLevel,
            versions,
            signatureVersionLag,
            payload.ActiveDetectionCount,
            payload.ActiveHighOrCriticalDetectionCount,
            protection,
            latestVersions);

        var notes = payload.Notes ?? [];
        if (latestVersions is not null && !string.IsNullOrWhiteSpace(latestVersions.ErrorMessage))
        {
            notes = [.. notes, $"Microsoft version baseline lookup failed: {latestVersions.ErrorMessage}"];
        }

        var snapshot = new DefenderSnapshot(
            host,
            payload.MachineName ?? host,
            ParseTimestamp(payload.CapturedAtUtc) ?? DateTimeOffset.UtcNow,
            LocalPowerShellExecutor.IsLocalHost(host),
            isManaged,
            managedBy,
            protection,
            versions,
            scans,
            payload.ActiveDetectionCount,
            payload.ActiveHighOrCriticalDetectionCount,
            healthLevel,
            healthSummary,
            notes,
            latestVersions);
        timings.Add($"Defender snapshot evaluation completed in {evaluationTimer.ElapsedMilliseconds} ms.");
        timings.Add($"Defender snapshot total completed in {totalTimer.ElapsedMilliseconds} ms.");
        return new DefenderSnapshotDiagnosticsResult(snapshot, timings);
    }

    private static async Task<T> MeasureAsync<T>(string operationName, ICollection<string> timings, Func<Task<T>> action)
    {
        var timer = Stopwatch.StartNew();
        try
        {
            var result = await action();
            timings.Add($"{operationName} completed in {timer.ElapsedMilliseconds} ms.");
            return result;
        }
        catch (Exception ex)
        {
            timings.Add($"{operationName} failed after {timer.ElapsedMilliseconds} ms: {ex.Message}");
            throw;
        }
    }

    public async ValueTask<DefenderSettingsSnapshot> GetSettingsAsync(string host, CancellationToken cancellationToken)
    {
        var execution = await executor.ExecuteForHostAsync(host, BuildSettingsScript(), cancellationToken);
        if (execution.ExitCode != 0)
        {
            throw new InvalidOperationException(NormalizeError(execution));
        }

        SettingsPayload payload;
        try
        {
            payload = JsonSerializer.Deserialize<SettingsPayload>(execution.StdOut, JsonOptions)
                      ?? throw new InvalidOperationException("Defender settings payload was empty.");
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            throw new InvalidOperationException($"Defender settings parsing failed: {ex.Message}", ex);
        }

        var settings = (payload.Settings ?? [])
            .Select(item =>
            {
                var name = item.Name ?? string.Empty;
                var value = item.Value ?? string.Empty;
                return new DefenderSettingItem(name, ResolveSettingDisplayValue(name, value));
            })
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var asrPerRuleExclusionsByRuleId = ParseAsrPerRuleExclusions(payload.AsrPerRuleExclusionsRaw ?? []);
        var asrRulePayloadByRuleId = new Dictionary<string, AsrRulePayload>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in payload.AsrRules ?? [])
        {
            var normalizedRuleId = NormalizeAsrRuleId(item.RuleId);
            if (string.IsNullOrWhiteSpace(normalizedRuleId))
            {
                continue;
            }

            asrRulePayloadByRuleId[normalizedRuleId] = item;
        }

        foreach (var ruleId in asrPerRuleExclusionsByRuleId.Keys)
        {
            if (!asrRulePayloadByRuleId.ContainsKey(ruleId))
            {
                asrRulePayloadByRuleId[ruleId] = new AsrRulePayload
                {
                    RuleId = ruleId
                };
            }
        }

        var asrRules = asrRulePayloadByRuleId.Values
            .Select(item =>
            {
                var normalizedRuleId = NormalizeAsrRuleId(item.RuleId);
                return new DefenderAsrRuleItem(
                    normalizedRuleId,
                    ResolveAsrRuleName(normalizedRuleId),
                    ResolveAsrAction(item.Action),
                    ResolveAsrPerRuleExclusionsText(asrPerRuleExclusionsByRuleId, normalizedRuleId));
            })
            .Where(item => !string.IsNullOrWhiteSpace(item.RuleId))
            .OrderBy(item => item.RuleName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.RuleId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var exclusions = (payload.Exclusions ?? [])
            .Where(item => !string.IsNullOrWhiteSpace(item.Value))
            .Select(item => new DefenderExclusionItem(
                string.IsNullOrWhiteSpace(item.Type) ? "Unknown" : item.Type.Trim(),
                item.Value!.Trim()))
            .ToArray();

        return new DefenderSettingsSnapshot(
            ParseTimestamp(payload.CapturedAtUtc) ?? DateTimeOffset.UtcNow,
            payload.Source ?? "Get-MpPreference",
            settings,
            payload.Notes ?? [],
            asrRules,
            exclusions);
    }

    public async ValueTask<IReadOnlyList<DefenderDetectionEntry>> GetDetectionsAsync(string host, int daysBack, CancellationToken cancellationToken)
    {
        var clampedDays = Math.Clamp(daysBack, 1, 365);
        var execution = await executor.ExecuteForHostAsync(host, BuildDetectionsScript(clampedDays), cancellationToken);
        if (execution.ExitCode != 0)
        {
            throw new InvalidOperationException(NormalizeError(execution));
        }

        DetectionsPayload payload;
        try
        {
            payload = JsonSerializer.Deserialize<DetectionsPayload>(execution.StdOut, JsonOptions)
                      ?? throw new InvalidOperationException("Defender detections payload was empty.");
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            throw new InvalidOperationException($"Defender detections parsing failed: {ex.Message}", ex);
        }

        var sinceUtc = DateTimeOffset.UtcNow.AddDays(-clampedDays);
        return (payload.Entries ?? [])
            .Select(entry => new DefenderDetectionEntry(
                ParseTimestamp(entry.DetectedAtUtc),
                ParseTimestamp(entry.LastStatusChangeUtc),
                entry.ThreatName ?? "Unknown threat",
                entry.ThreatId,
                entry.Severity ?? "Unknown",
                entry.Category ?? string.Empty,
                entry.Action ?? string.Empty,
                entry.ActionSuccess,
                entry.IsActive,
                entry.Source ?? "Unknown",
                entry.Details ?? string.Empty))
            .Where(entry =>
                (!entry.DetectedAtUtc.HasValue && !entry.LastStatusChangeUtc.HasValue) ||
                (entry.DetectedAtUtc.HasValue && entry.DetectedAtUtc.Value >= sinceUtc) ||
                (entry.LastStatusChangeUtc.HasValue && entry.LastStatusChangeUtc.Value >= sinceUtc))
            .OrderByDescending(entry => entry.DetectedAtUtc ?? entry.LastStatusChangeUtc ?? DateTimeOffset.MinValue)
            .ToArray();
    }

    public async ValueTask<DefenderDeviceControlSnapshot> GetDeviceControlEventsAsync(string host, int daysBack, CancellationToken cancellationToken)
    {
        var clampedDays = Math.Clamp(daysBack, 1, 365);
        var execution = await executor.ExecuteForHostAsync(host, BuildDeviceControlEventsScript(clampedDays), cancellationToken);
        if (execution.ExitCode != 0)
        {
            throw new InvalidOperationException(NormalizeError(execution));
        }

        DeviceControlPayload payload;
        try
        {
            payload = JsonSerializer.Deserialize<DeviceControlPayload>(execution.StdOut, JsonOptions)
                      ?? throw new InvalidOperationException("Defender Device Control payload was empty.");
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            throw new InvalidOperationException($"Defender Device Control parsing failed: {ex.Message}", ex);
        }

        var sinceUtc = DateTimeOffset.UtcNow.AddDays(-clampedDays);
        var events = (payload.Entries ?? [])
            .Select(entry => new DefenderDeviceControlEventEntry(
                ParseTimestamp(entry.TimeCreatedUtc),
                entry.EventId,
                NormalizeDeviceControlText(entry.Provider),
                NormalizeDeviceControlText(entry.LogName),
                NormalizeDeviceControlText(entry.Level),
                NormalizeDeviceControlText(entry.DeviceType),
                NormalizeDeviceControlText(entry.DeviceName),
                NormalizeDeviceControlText(entry.FriendlyName),
                NormalizeDeviceControlText(entry.Manufacturer),
                NormalizeDeviceControlText(entry.DeviceId),
                NormalizeDeviceControlText(entry.DeviceInstanceId),
                NormalizeDeviceControlText(entry.HardwareIds),
                NormalizeDeviceControlText(entry.VendorId),
                NormalizeDeviceControlText(entry.ProductId),
                NormalizeDeviceControlText(entry.SerialNumber),
                NormalizeDeviceControlText(entry.ClassGuid),
                NormalizeDeviceControlText(entry.User),
                NormalizeDeviceControlText(entry.Sid),
                NormalizeDeviceControlText(entry.PolicyName),
                NormalizeDeviceControlText(entry.PolicyId),
                NormalizeDeviceControlText(entry.PolicyRuleId),
                NormalizeDeviceControlText(entry.PolicyVerdict),
                NormalizeDeviceControlText(entry.Access),
                NormalizeDeviceControlText(entry.Action),
                entry.IsBlocked,
                NormalizeDeviceControlText(entry.Message)))
            .Where(entry => !entry.TimeCreatedUtc.HasValue || entry.TimeCreatedUtc.Value >= sinceUtc)
            .OrderByDescending(entry => entry.TimeCreatedUtc ?? DateTimeOffset.MinValue)
            .ToArray();

        return new DefenderDeviceControlSnapshot(
            ParseTimestamp(payload.CapturedAtUtc) ?? DateTimeOffset.UtcNow,
            string.IsNullOrWhiteSpace(payload.Source) ? "Local Device Control events" : payload.Source.Trim(),
            payload.Notes ?? [],
            events,
            BuildDeviceControlSummaries(events));
    }

    public async ValueTask<DefenderActionResult> ExecuteActionAsync(string host, DefenderActionRequest request, CancellationToken cancellationToken)
    {
        var actionId = ToActionId(request.ActionType);
        var execution = await executor.ExecuteForHostAsync(host, BuildActionScript(actionId), cancellationToken);
        if (execution.ExitCode != 0)
        {
            return DefenderActionResult.Fail(NormalizeError(execution), "execution_failed");
        }

        try
        {
            var payload = JsonSerializer.Deserialize<ActionPayload>(execution.StdOut, JsonOptions)
                          ?? throw new InvalidOperationException("Defender action payload was empty.");
            return new DefenderActionResult(
                payload.Success,
                payload.Message ?? string.Empty,
                payload.ErrorCode ?? string.Empty,
                ParseTimestamp(payload.ExecutedAtUtc) ?? DateTimeOffset.UtcNow);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return DefenderActionResult.Fail($"Defender action parsing failed: {ex.Message}", "parse_failed");
        }
    }

    private static string ToActionId(DefenderActionType actionType)
    {
        return actionType switch
        {
            DefenderActionType.QuickScan => "quick-scan",
            DefenderActionType.FullScan => "full-scan",
            DefenderActionType.StopScan => "stop-scan",
            DefenderActionType.SignatureUpdate => "signature-update",
            DefenderActionType.RestartService => "restart-service",
            _ => "unknown"
        };
    }

    private static string ResolveManagedBy(
        bool isGpoManaged,
        bool isMdmManaged,
        bool isMdeManaged,
        int? managedDefenderProductType,
        int? enrollmentStatus,
        int? onboardingState)
    {
        var microsoftMapping = ResolveManagedByFromMicrosoftMapping(managedDefenderProductType, enrollmentStatus);
        if (microsoftMapping is not null)
        {
            return microsoftMapping;
        }

        // EnrollmentStatus from SenseCM can still help when ManagedDefenderProductType is missing.
        if (enrollmentStatus == 3)
        {
            return "Configuration Manager + MDM (Co-managed)";
        }

        if (enrollmentStatus == 4)
        {
            return "Configuration Manager";
        }

        if (isMdmManaged && isGpoManaged)
        {
            return "MDM (Intune) + Group Policy";
        }

        if (isGpoManaged)
        {
            return "Group Policy";
        }

        if (isMdmManaged)
        {
            return "MDM (Intune)";
        }

        if (onboardingState == 1 || isMdeManaged)
        {
            return "Defender for Endpoint";
        }

        return "Local";
    }

    private static string? ResolveManagedByFromMicrosoftMapping(int? managedDefenderProductType, int? enrollmentStatus)
    {
        // Microsoft Learn mapping for tamper protection management scope:
        // ManagedDefenderProductType=6 => Intune managed
        // ManagedDefenderProductType=7 + EnrollmentStatus=4 => Configuration Manager managed
        // ManagedDefenderProductType=7 + EnrollmentStatus=3 => Co-managed (ConfigMgr + Intune)
        if (managedDefenderProductType == 6)
        {
            return "MDM (Intune)";
        }

        if (managedDefenderProductType != 7)
        {
            return null;
        }

        return enrollmentStatus switch
        {
            3 => "Configuration Manager + MDM (Co-managed)",
            4 => "Configuration Manager",
            _ => null
        };
    }

    private string ResolveHealthLevel(
        DefenderProtectionStatus protection,
        DefenderVersionInfo versions,
        int? signatureVersionLag,
        int activeDetections,
        int activeHighOrCriticalDetections,
        IReadOnlyList<string> notes)
    {
        if (protection.AntivirusEnabled == false || protection.RealtimeProtectionEnabled == false)
        {
            return "Red";
        }

        if (activeHighOrCriticalDetections > 0)
        {
            return "Red";
        }

        if (versions.SignatureAgeHours > _signatureCriticalThresholdHours && !IsLatestOrPreviousDefinition(signatureVersionLag))
        {
            return "Red";
        }

        if (versions.SignaturesOutdated || activeDetections > 0 || protection.TamperProtectionEnabled == false || notes.Count > 0)
        {
            return "Yellow";
        }

        return "Green";
    }

    private string ResolveHealthSummary(
        string healthLevel,
        DefenderVersionInfo versions,
        int? signatureVersionLag,
        int activeDetections,
        int activeHighOrCriticalDetections,
        DefenderProtectionStatus protection,
        DefenderLatestVersionInfo? latestVersions)
    {
        return healthLevel switch
        {
            "Green" when versions.SignatureAgeHours >= 0 &&
                           versions.SignatureAgeHours <= _signatureWarningThresholdHours &&
                           signatureVersionLag is > AllowedDefinitionVersionLag &&
                           latestVersions is { ErrorMessage: null } latestGreen =>
                $"Definitions differ from the latest baseline but are still within the {_signatureWarningThresholdHours:N0}h freshness threshold (current {versions.AntivirusSignatureVersion}, latest {latestGreen.SecurityIntelligenceVersion}).",
            "Green" => "Defender status is healthy and up to date.",
            "Red" when protection.AntivirusEnabled == false || protection.RealtimeProtectionEnabled == false =>
                "Critical protection components are disabled.",
            "Red" when activeHighOrCriticalDetections > 0 =>
                $"{activeHighOrCriticalDetections} active high/critical detection(s) require immediate action.",
            "Red" when versions.SignatureAgeHours > _signatureCriticalThresholdHours && signatureVersionLag is > AllowedDefinitionVersionLag &&
                       latestVersions is { ErrorMessage: null } latestRed =>
                $"Signatures are stale ({versions.SignatureAgeHours:N1}h old, current {versions.AntivirusSignatureVersion}, latest {latestRed.SecurityIntelligenceVersion}).",
            "Red" when versions.SignatureAgeHours > _signatureCriticalThresholdHours =>
                $"Signatures are stale ({versions.SignatureAgeHours:N1}h old).",
            "Yellow" when activeDetections > 0 =>
                $"{activeDetections} active detection(s) need review.",
            "Yellow" when versions.SignatureAgeHours > _signatureWarningThresholdHours &&
                          signatureVersionLag is > AllowedDefinitionVersionLag &&
                          latestVersions is { ErrorMessage: null } latestYellow =>
                $"Definitions are behind latest baseline (current {versions.AntivirusSignatureVersion}, latest {latestYellow.SecurityIntelligenceVersion}).",
            "Yellow" when versions.SignaturesOutdated =>
                $"Signatures should be refreshed ({versions.SignatureAgeHours:N1}h old, warning after {_signatureWarningThresholdHours:N0}h).",
            "Yellow" when protection.TamperProtectionEnabled == false =>
                "Tamper protection is disabled.",
            _ => "Defender reported partial data."
        };
    }

    private bool IsSignatureOutdated(double signatureAgeHours, int? signatureVersionLag)
    {
        if (signatureAgeHours <= _signatureWarningThresholdHours)
        {
            return false;
        }

        return !IsLatestOrPreviousDefinition(signatureVersionLag);
    }

    private static bool IsLatestOrPreviousDefinition(int? signatureVersionLag)
    {
        return signatureVersionLag is >= 0 and <= AllowedDefinitionVersionLag;
    }

    private static double ResolveThresholdHours(double? configuredValue, double fallbackValue)
    {
        if (!configuredValue.HasValue)
        {
            return fallbackValue;
        }

        return configuredValue.Value > 0 ? configuredValue.Value : fallbackValue;
    }

    private static int? ResolveSignatureVersionLag(string? currentDefinitionVersion, DefenderLatestVersionInfo? latestVersions)
    {
        if (latestVersions is null || !string.IsNullOrWhiteSpace(latestVersions.ErrorMessage))
        {
            return null;
        }

        if (!TryParseVersion(currentDefinitionVersion, out var currentVersion) ||
            !TryParseVersion(latestVersions.SecurityIntelligenceVersion, out var latestVersion))
        {
            return null;
        }

        if (currentVersion >= latestVersion)
        {
            return 0;
        }

        if (currentVersion.Major == latestVersion.Major &&
            currentVersion.Minor == latestVersion.Minor &&
            currentVersion.Build < latestVersion.Build)
        {
            var buildLag = latestVersion.Build - currentVersion.Build;
            return buildLag <= 0 ? 0 : buildLag;
        }

        if (currentVersion.Major == latestVersion.Major &&
            currentVersion.Minor == latestVersion.Minor &&
            currentVersion.Build == latestVersion.Build)
        {
            var revisionLag = latestVersion.Revision - currentVersion.Revision;
            return revisionLag <= 0 ? 0 : revisionLag;
        }

        return AllowedDefinitionVersionLag + 1;
    }

    private static bool TryParseVersion(string? value, out Version version)
    {
        if (Version.TryParse(value?.Trim(), out var parsed))
        {
            version = parsed;
            return true;
        }

        version = new Version(0, 0);
        return false;
    }

    private static string ResolveSettingDisplayValue(string name, string value)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        var key = name.Trim();
        var raw = value.Trim();
        return key switch
        {
            "PUAProtection" => MapNumericSettingValue(raw, PuaProtectionValueMap),
            "MAPSReporting" => MapNumericSettingValue(raw, MapsReportingValueMap),
            "SubmitSamplesConsent" => MapNumericSettingValue(raw, SubmitSamplesConsentValueMap),
            "CloudBlockLevel" => MapNumericSettingValue(raw, CloudBlockLevelValueMap),
            "ScanScheduleDay" => MapNumericSettingValue(raw, ScheduleDayValueMap),
            "SignatureScheduleDay" => MapNumericSettingValue(raw, ScheduleDayValueMap),
            "EnableControlledFolderAccess" => MapNumericSettingValue(raw, ControlledFolderAccessValueMap),
            _ => value
        };
    }

    private static string MapNumericSettingValue(string rawValue, IReadOnlyDictionary<string, string> valueMap)
    {
        if (!valueMap.TryGetValue(rawValue, out var text))
        {
            return rawValue;
        }

        return $"({rawValue}) {text}";
    }

    private static string NormalizeAsrRuleId(string? ruleId)
    {
        if (string.IsNullOrWhiteSpace(ruleId))
        {
            return string.Empty;
        }

        return Guid.TryParse(ruleId.Trim(), out var guid)
            ? guid.ToString("D").ToLowerInvariant()
            : ruleId.Trim();
    }

    private static string ResolveAsrRuleName(string ruleId)
    {
        if (string.IsNullOrWhiteSpace(ruleId))
        {
            return "Unknown rule";
        }

        return AsrRuleNameById.TryGetValue(ruleId, out var name)
            ? name
            : "Unknown rule";
    }

    private static string ResolveAsrAction(string? rawAction)
    {
        if (string.IsNullOrWhiteSpace(rawAction))
        {
            return "Not configured";
        }

        var trimmed = rawAction.Trim();
        return trimmed switch
        {
            "-1" => "Not configured (-1)",
            "0" => "Disabled (0)",
            "1" => "Block (1)",
            "2" => "Audit (2)",
            "6" => "Warn (6)",
            _ => trimmed.ToLowerInvariant() switch
            {
                "not configured" => "Not configured",
                "notconfigured" => "Not configured",
                "disabled" => "Disabled",
                "block" => "Block",
                "audit" => "Audit",
                "warn" => "Warn",
                _ => $"Unknown ({trimmed})"
            }
        };
    }

    private static string ResolveAsrPerRuleExclusionsText(
        IReadOnlyDictionary<string, List<string>> exclusionsByRuleId,
        string normalizedRuleId)
    {
        if (string.IsNullOrWhiteSpace(normalizedRuleId) ||
            !exclusionsByRuleId.TryGetValue(normalizedRuleId, out var values) ||
            values.Count == 0)
        {
            return "None";
        }

        return string.Join("; ", values);
    }

    private static Dictionary<string, List<string>> ParseAsrPerRuleExclusions(IReadOnlyList<string> rawEntries)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in rawEntries)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            var entries = raw.Split(new[] { '\r', '\n', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var entry in entries)
            {
                ParseAsrPerRuleEntry(entry, result);
            }
        }

        return result;
    }

    private static void ParseAsrPerRuleEntry(string rawEntry, IDictionary<string, List<string>> output)
    {
        if (string.IsNullOrWhiteSpace(rawEntry))
        {
            return;
        }

        var trimmed = rawEntry.Trim();
        var equalsIndex = trimmed.IndexOf('=');
        string ruleIdPart;
        string valuesPart;
        if (equalsIndex > 0)
        {
            ruleIdPart = trimmed[..equalsIndex];
            valuesPart = trimmed[(equalsIndex + 1)..];
        }
        else
        {
            var legacySplit = trimmed.Split('|', 2, StringSplitOptions.TrimEntries);
            if (legacySplit.Length != 2)
            {
                return;
            }

            ruleIdPart = legacySplit[0];
            valuesPart = legacySplit[1];
        }

        var normalizedRuleId = NormalizeAsrRuleId(ruleIdPart);
        if (!Guid.TryParse(normalizedRuleId, out _) || string.IsNullOrWhiteSpace(valuesPart))
        {
            return;
        }

        if (!output.TryGetValue(normalizedRuleId, out var values))
        {
            values = [];
            output[normalizedRuleId] = values;
        }

        foreach (var exclusion in valuesPart.Split(new[] { '|', '>' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (string.IsNullOrWhiteSpace(exclusion) ||
                values.Contains(exclusion, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            values.Add(exclusion);
        }
    }

    private static IReadOnlyList<DefenderDeviceControlDeviceSummary> BuildDeviceControlSummaries(IReadOnlyList<DefenderDeviceControlEventEntry> events)
    {
        return events
            .Where(static entry => entry.IsBlocked)
            .GroupBy(BuildDeviceControlDeviceKey, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var ordered = group
                    .OrderByDescending(static entry => entry.TimeCreatedUtc ?? DateTimeOffset.MinValue)
                    .ToArray();
                var latest = ordered.First();
                var firstBlockedUtc = ordered
                    .Where(static entry => entry.TimeCreatedUtc.HasValue)
                    .Select(static entry => entry.TimeCreatedUtc!.Value)
                    .DefaultIfEmpty()
                    .Min();
                var lastBlockedUtc = ordered
                    .Where(static entry => entry.TimeCreatedUtc.HasValue)
                    .Select(static entry => entry.TimeCreatedUtc!.Value)
                    .DefaultIfEmpty()
                    .Max();

                return new DefenderDeviceControlDeviceSummary(
                    group.Key,
                    FirstNonEmpty(ordered.Select(static entry => entry.DeviceType)),
                    FirstNonEmpty(
                        ordered.Select(static entry => entry.FriendlyName)
                            .Concat(ordered.Select(static entry => entry.DeviceName))
                            .Concat(ordered.Select(static entry => entry.DeviceInstanceId))),
                    ordered.Length,
                    firstBlockedUtc == default ? null : firstBlockedUtc,
                    lastBlockedUtc == default ? null : lastBlockedUtc,
                    FirstNonEmpty(ordered.Select(static entry => entry.DeviceId)),
                    FirstNonEmpty(ordered.Select(static entry => entry.DeviceInstanceId)),
                    FirstNonEmpty(ordered.Select(static entry => entry.HardwareIds)),
                    FirstNonEmpty(ordered.Select(static entry => entry.VendorId)),
                    FirstNonEmpty(ordered.Select(static entry => entry.ProductId)),
                    FirstNonEmpty(ordered.Select(static entry => entry.SerialNumber)),
                    FirstNonEmpty(ordered.Select(static entry => entry.ClassGuid)),
                    latest.PolicyName,
                    latest.PolicyId,
                    latest.PolicyRuleId,
                    latest.PolicyVerdict,
                    latest.Access,
                    latest.User);
            })
            .OrderByDescending(static summary => summary.LastBlockedUtc ?? DateTimeOffset.MinValue)
            .ToArray();
    }

    private static string BuildDeviceControlDeviceKey(DefenderDeviceControlEventEntry entry)
    {
        var serialVendorProduct = string.Join(
            "|",
            new[] { entry.SerialNumber, entry.VendorId, entry.ProductId }
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Select(static value => value.Trim()));

        return FirstNonEmpty(
            [
                entry.DeviceInstanceId,
                entry.DeviceId,
                serialVendorProduct,
                entry.HardwareIds,
                entry.DeviceName,
                "Unknown device"
            ]);
    }

    private static string FirstNonEmpty(IEnumerable<string?> values)
    {
        return values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;
    }

    private static string NormalizeDeviceControlText(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
    }

    private static DateTimeOffset? ParseTimestamp(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : null;
    }

    private static string NormalizeError(PowershellExecutionResult execution)
    {
        var raw = string.IsNullOrWhiteSpace(execution.StdErr) ? execution.StdOut : execution.StdErr;
        return string.IsNullOrWhiteSpace(raw)
            ? $"PowerShell execution failed with exit code {execution.ExitCode}."
            : raw.Trim();
    }

    private async ValueTask<DefenderLatestVersionInfo?> TryGetLatestDefenderVersionsAsync(CancellationToken cancellationToken)
    {
        if (httpClient is null)
        {
            return null;
        }

        var nowUtc = DateTimeOffset.UtcNow;
        if (_latestVersionsCache is not null && (nowUtc - _latestVersionsCacheUpdatedUtc) <= LatestVersionsCacheDuration)
        {
            return _latestVersionsCache;
        }

        await _latestVersionsCacheLock.WaitAsync(cancellationToken);
        try
        {
            nowUtc = DateTimeOffset.UtcNow;
            if (_latestVersionsCache is not null && (nowUtc - _latestVersionsCacheUpdatedUtc) <= LatestVersionsCacheDuration)
            {
                return _latestVersionsCache;
            }

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, DefenderUpdatesUrl);
                using var response = await httpClient.SendAsync(request, cancellationToken);
                var html = await response.Content.ReadAsStringAsync(cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    var failed = new DefenderLatestVersionInfo(
                        DefenderUpdatesUrl,
                        DefenderReleaseNotesUrl,
                        DateTimeOffset.UtcNow,
                        "Unknown",
                        "Unknown",
                        "Unknown",
                        null,
                        $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}".Trim());
                    _latestVersionsCache = failed;
                    _latestVersionsCacheUpdatedUtc = DateTimeOffset.UtcNow;
                    return failed;
                }

                var securityIntelligenceVersion = ExtractHtmlValue(SecurityIntelligenceVersionRegex, html);
                var engineVersion = ExtractHtmlValue(EngineVersionRegex, html);
                var platformVersion = ExtractHtmlValue(PlatformVersionRegex, html);
                var releasedText = ExtractHtmlValue(ReleasedRegex, html);
                var releasedUtc = ParseUsReleaseDate(releasedText);

                if (string.IsNullOrWhiteSpace(securityIntelligenceVersion) ||
                    string.IsNullOrWhiteSpace(engineVersion) ||
                    string.IsNullOrWhiteSpace(platformVersion))
                {
                    var malformed = new DefenderLatestVersionInfo(
                        DefenderUpdatesUrl,
                        DefenderReleaseNotesUrl,
                        DateTimeOffset.UtcNow,
                        "Unknown",
                        "Unknown",
                        "Unknown",
                        releasedUtc,
                        "Defender updates page format changed.");
                    _latestVersionsCache = malformed;
                    _latestVersionsCacheUpdatedUtc = DateTimeOffset.UtcNow;
                    return malformed;
                }

                var result = new DefenderLatestVersionInfo(
                    DefenderUpdatesUrl,
                    DefenderReleaseNotesUrl,
                    DateTimeOffset.UtcNow,
                    securityIntelligenceVersion,
                    engineVersion,
                    platformVersion,
                    releasedUtc,
                    null);
                _latestVersionsCache = result;
                _latestVersionsCacheUpdatedUtc = DateTimeOffset.UtcNow;
                return result;
            }
            catch (Exception ex)
            {
                var failed = new DefenderLatestVersionInfo(
                    DefenderUpdatesUrl,
                    DefenderReleaseNotesUrl,
                    DateTimeOffset.UtcNow,
                    "Unknown",
                    "Unknown",
                    "Unknown",
                    null,
                    ex.Message);
                _latestVersionsCache = failed;
                _latestVersionsCacheUpdatedUtc = DateTimeOffset.UtcNow;
                return failed;
            }
        }
        finally
        {
            _latestVersionsCacheLock.Release();
        }
    }

    private static string ExtractHtmlValue(Regex regex, string html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return string.Empty;
        }

        var match = regex.Match(html);
        if (!match.Success)
        {
            return string.Empty;
        }

        var raw = match.Groups["value"].Value;
        return WebUtility.HtmlDecode(raw).Trim();
    }

    private static DateTimeOffset? ParseUsReleaseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (DateTimeOffset.TryParse(
            value,
            CultureInfo.GetCultureInfo("en-US"),
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed))
        {
            return parsed;
        }

        return DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out parsed)
            ? parsed
            : null;
    }

    private static string BuildSnapshotScript() =>
        """
        $notes = New-Object System.Collections.Generic.List[string]

        function To-IsoUtc([object]$value) {
          if ($null -eq $value) { return $null }
          try { return ([DateTime]$value).ToUniversalTime().ToString('o') } catch { return $null }
        }

        function Has-ConfiguredPolicyValue([string]$rootPath, [string[]]$excludedLeafNames) {
          if (-not (Test-Path -LiteralPath $rootPath)) { return $false }

          $stack = New-Object System.Collections.Generic.Stack[string]
          $stack.Push($rootPath)

          while ($stack.Count -gt 0) {
            $current = $stack.Pop()
            $leaf = Split-Path -Path $current -Leaf
            if ($excludedLeafNames -contains $leaf) {
              continue
            }

            try {
              $props = (Get-ItemProperty -LiteralPath $current -ErrorAction SilentlyContinue).PSObject.Properties |
                Where-Object { $_.Name -notlike 'PS*' -and $_.Name -ne '(default)' }
              foreach ($prop in $props) {
                $valueText = if ($null -eq $prop.Value) { '' } else { [string]$prop.Value }
                if (-not [string]::IsNullOrWhiteSpace($valueText)) {
                  return $true
                }
              }
            } catch { }

            foreach ($child in @(Get-ChildItem -LiteralPath $current -Directory -ErrorAction SilentlyContinue)) {
              $stack.Push($child.PSPath)
            }
          }

          return $false
        }

        try { $status = Get-MpComputerStatus -ErrorAction Stop }
        catch { throw "Get-MpComputerStatus failed: $($_.Exception.Message)" }

        $isGpoManaged = $false
        try {
          $isGpoManaged = Has-ConfiguredPolicyValue 'HKLM:\SOFTWARE\Policies\Microsoft\Windows Defender' @('Policy Manager')
        } catch { $notes.Add("GPO probe failed: $($_.Exception.Message)") }

        $isMdmManaged = $false
        try {
          $isMdmManaged = Has-ConfiguredPolicyValue 'HKLM:\SOFTWARE\Microsoft\PolicyManager\current\device\Defender' @()
          $providersRoot = 'HKLM:\SOFTWARE\Microsoft\PolicyManager\providers'
          if (-not $isMdmManaged -and (Test-Path -LiteralPath $providersRoot)) {
            foreach ($provider in @(Get-ChildItem -LiteralPath $providersRoot -Directory -ErrorAction SilentlyContinue)) {
              if (Has-ConfiguredPolicyValue "$($provider.PSPath)\default\Device\Defender" @()) {
                $isMdmManaged = $true
                break
              }

              if (Has-ConfiguredPolicyValue "$($provider.PSPath)\current\device\Defender" @()) {
                $isMdmManaged = $true
                break
              }
            }
          }
        }
        catch { $notes.Add("MDM probe failed: $($_.Exception.Message)") }

        $isMdeManaged = $false
        try {
          $sense = Get-Service -Name 'Sense' -ErrorAction SilentlyContinue
          $isMdeManaged = $null -ne $sense -and $sense.Status -eq 'Running'
        } catch { $notes.Add("MDE probe failed: $($_.Exception.Message)") }

        $managedDefenderProductType = $null
        try {
          $managedDefenderProductType = [int](Get-ItemPropertyValue -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows Defender' -Name 'ManagedDefenderProductType' -ErrorAction Stop)
        } catch { }

        $senseCmEnrollmentStatus = $null
        try {
          $senseCmEnrollmentStatus = [int](Get-ItemPropertyValue -LiteralPath 'HKLM:\SOFTWARE\Microsoft\SenseCM' -Name 'EnrollmentStatus' -ErrorAction Stop)
        } catch { }

        $onboardingState = $null
        try {
          $onboardingState = [int](Get-ItemPropertyValue -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows Advanced Threat Protection\Status' -Name 'OnboardingState' -ErrorAction Stop)
        } catch { }

        $enrollmentStatusMeaning = switch ($senseCmEnrollmentStatus) {
          3 { 'Co-managed (Configuration Manager + Intune)' }
          4 { 'Configuration Manager managed' }
          $null { 'Unavailable' }
          default { "Unknown ($senseCmEnrollmentStatus)" }
        }

        $onboardingStateMeaning = switch ($onboardingState) {
          1 { 'Onboarded' }
          0 { 'Not onboarded/offboarded' }
          $null { 'Unavailable' }
          default { "Unknown ($onboardingState)" }
        }

        if ($onboardingState -eq 1) {
          $isMdeManaged = $true
        }

        $notes.Add("Management probe: GPO=$isGpoManaged; MDM=$isMdmManaged; MDE=$isMdeManaged; ProductType=$managedDefenderProductType; EnrollmentStatus=$senseCmEnrollmentStatus; OnboardingState=$onboardingState")
        $notes.Add("EnrollmentStatus meaning: $enrollmentStatusMeaning")
        $notes.Add("OnboardingState meaning: $onboardingStateMeaning")

        $activeDetectionCount = 0
        $activeHighOrCriticalDetectionCount = 0
        try {
          if (Get-Command -Name Get-MpThreat -ErrorAction SilentlyContinue) {
            $activeThreats = @(Get-MpThreat -ErrorAction Stop)
            $activeDetectionCount = $activeThreats.Count
            $activeHighOrCriticalDetectionCount = @($activeThreats | Where-Object { ([int]($_.SeverityID -as [int])) -ge 4 }).Count
          } else {
            $notes.Add('Get-MpThreat cmdlet is not available.')
          }
        } catch { $notes.Add("Active threat query failed: $($_.Exception.Message)") }

        $signatureLastUpdatedUtc = To-IsoUtc $status.AntivirusSignatureLastUpdated
        $signatureAgeHours = -1.0
        if ($null -ne $status.AntivirusSignatureLastUpdated) {
          try {
            $signatureAgeHours = ((Get-Date).ToUniversalTime() - ([DateTime]$status.AntivirusSignatureLastUpdated).ToUniversalTime()).TotalHours
          } catch {
            $notes.Add("Signature age calculation failed: $($_.Exception.Message)")
          }
        }

        $lastScanUtc = $null
        $scanCandidates = @($status.QuickScanEndTime, $status.FullScanEndTime) | Where-Object { $null -ne $_ }
        if ($scanCandidates.Count -gt 0) {
          $lastScanUtc = To-IsoUtc (($scanCandidates | Sort-Object -Descending | Select-Object -First 1))
        }

        $result = [ordered]@{
          machineName = $env:COMPUTERNAME
          capturedAtUtc = (Get-Date).ToUniversalTime().ToString('o')
          isGpoManaged = $isGpoManaged
          isMdmManaged = $isMdmManaged
          isMdeManaged = $isMdeManaged
          managedDefenderProductType = $managedDefenderProductType
          enrollmentStatus = $senseCmEnrollmentStatus
          onboardingState = $onboardingState
          antivirusEnabled = if ($null -ne $status.AntivirusEnabled) { [bool]$status.AntivirusEnabled } else { $null }
          realtimeProtectionEnabled = if ($null -ne $status.RealTimeProtectionEnabled) { [bool]$status.RealTimeProtectionEnabled } else { $null }
          behaviorMonitorEnabled = if ($null -ne $status.BehaviorMonitorEnabled) { [bool]$status.BehaviorMonitorEnabled } else { $null }
          ioavProtectionEnabled = if ($null -ne $status.IoavProtectionEnabled) { [bool]$status.IoavProtectionEnabled } else { $null }
          onAccessProtectionEnabled = if ($null -ne $status.OnAccessProtectionEnabled) { [bool]$status.OnAccessProtectionEnabled } else { $null }
          nisEnabled = if ($null -ne $status.NISEnabled) { [bool]$status.NISEnabled } else { $null }
          tamperProtectionEnabled = if ($null -ne $status.IsTamperProtected) { [bool]$status.IsTamperProtected } else { $null }
          runningMode = [string]$status.AMRunningMode
          engineVersion = [string]$status.AMEngineVersion
          productVersion = [string]$status.AMProductVersion
          antivirusSignatureVersion = [string]$status.AntivirusSignatureVersion
          antispywareSignatureVersion = [string]$status.AntispywareSignatureVersion
          nisEngineVersion = [string]$status.NISEngineVersion
          nisSignatureVersion = [string]$status.NISSignatureVersion
          signatureLastUpdatedUtc = $signatureLastUpdatedUtc
          signatureAgeHours = $signatureAgeHours
          quickScanStartUtc = To-IsoUtc $status.QuickScanStartTime
          quickScanEndUtc = To-IsoUtc $status.QuickScanEndTime
          fullScanStartUtc = To-IsoUtc $status.FullScanStartTime
          fullScanEndUtc = To-IsoUtc $status.FullScanEndTime
          lastScanUtc = $lastScanUtc
          activeDetectionCount = $activeDetectionCount
          activeHighOrCriticalDetectionCount = $activeHighOrCriticalDetectionCount
          notes = $notes
        }

        $result | ConvertTo-Json -Depth 8 -Compress
        """;

    private static string BuildSettingsScript() =>
        """
        $notes = New-Object System.Collections.Generic.List[string]
        $settings = New-Object System.Collections.Generic.List[object]
        $asrRules = New-Object System.Collections.Generic.List[object]
        $exclusions = New-Object System.Collections.Generic.List[object]
        $asrPerRuleExclusionsRaw = New-Object System.Collections.Generic.List[string]

        function To-DisplayValue([object]$value) {
          if ($null -eq $value) { return '' }
          if ($value -is [Array]) { return ((@($value) | ForEach-Object { [string]$_ }) -join '; ') }
          if ($value -is [hashtable] -or $value -is [System.Collections.IDictionary]) { return ($value | ConvertTo-Json -Compress -Depth 6) }
          return [string]$value
        }

        function Add-Setting([string]$name, [object]$value) {
          $settings.Add([ordered]@{ Name = $name; Value = (To-DisplayValue $value) })
        }

        function Add-Exclusion([string]$type, [object]$values) {
          foreach ($entry in @($values)) {
            if ($null -eq $entry) { continue }
            $text = [string]$entry
            if ([string]::IsNullOrWhiteSpace($text)) { continue }
            $exclusions.Add([ordered]@{ type = $type; value = $text })
          }
        }

        function Add-AsrPerRuleRawEntry([object]$value) {
          if ($null -eq $value) { return }
          $text = [string]$value
          if ([string]::IsNullOrWhiteSpace($text)) { return }
          $trimmed = $text.Trim()
          if (-not $asrPerRuleExclusionsRaw.Contains($trimmed)) {
            $asrPerRuleExclusionsRaw.Add($trimmed)
          }
        }

        function Add-AsrPerRuleRawEntries([object]$values) {
          foreach ($entry in @($values)) {
            Add-AsrPerRuleRawEntry $entry
          }
        }

        function Get-RegistryValueSafe([string]$path, [string]$name) {
          try {
            $item = Get-ItemProperty -Path $path -Name $name -ErrorAction Stop
            return $item.$name
          }
          catch {
            return $null
          }
        }

        try { $pref = Get-MpPreference -ErrorAction Stop }
        catch { throw "Get-MpPreference failed: $($_.Exception.Message)" }

        $status = $null
        try { $status = Get-MpComputerStatus -ErrorAction Stop }
        catch { $notes.Add("Get-MpComputerStatus failed in settings view: $($_.Exception.Message)") }

        foreach ($name in @(
          'DisableRealtimeMonitoring','DisableBehaviorMonitoring','DisableIOAVProtection','DisableScriptScanning',
          'DisableArchiveScanning','DisableEmailScanning','DisableRemovableDriveScanning','PUAProtection',
          'CloudBlockLevel','MAPSReporting','SubmitSamplesConsent','ScanScheduleDay','ScanScheduleTime',
          'SignatureScheduleDay','SignatureScheduleTime','CheckForSignaturesBeforeRunningScan',
          'EnableControlledFolderAccess','UILockdown')) {
          try { Add-Setting $name $pref.$name }
          catch { $notes.Add("Failed to read setting '$name': $($_.Exception.Message)") }
        }

        Add-Setting 'ExclusionPath' $pref.ExclusionPath
        Add-Setting 'ExclusionProcess' $pref.ExclusionProcess
        Add-Setting 'ExclusionExtension' $pref.ExclusionExtension
        Add-Setting 'ExclusionIpAddress' $pref.ExclusionIpAddress
        Add-Setting 'AttackSurfaceReductionOnlyExclusions' $pref.AttackSurfaceReductionOnlyExclusions
        Add-Setting 'AttackSurfaceReductionRules_Ids' $pref.AttackSurfaceReductionRules_Ids
        Add-Setting 'AttackSurfaceReductionRules_Actions' $pref.AttackSurfaceReductionRules_Actions

        try { Add-AsrPerRuleRawEntries $pref.ASROnlyPerRuleExclusions }
        catch { $notes.Add("ASROnlyPerRuleExclusions not exposed by Get-MpPreference: $($_.Exception.Message)") }

        Add-AsrPerRuleRawEntries (Get-RegistryValueSafe 'HKLM:\SOFTWARE\Policies\Microsoft\Windows Defender\Windows Defender Exploit Guard\ASR' 'ASROnlyPerRuleExclusions')
        Add-AsrPerRuleRawEntries (Get-RegistryValueSafe 'HKLM:\SOFTWARE\Policies\Microsoft\Windows Defender\Windows Defender Exploit Guard\ASR' 'ExploitGuard_ASR_ASROnlyPerRuleExclusions')
        Add-AsrPerRuleRawEntries (Get-RegistryValueSafe 'HKLM:\SOFTWARE\Microsoft\PolicyManager\current\device\Defender' 'ASROnlyPerRuleExclusions')
        Add-AsrPerRuleRawEntries (Get-RegistryValueSafe 'HKLM:\SOFTWARE\Microsoft\PolicyManager\current\device\Defender\ASROnlyPerRuleExclusions' 'value')
        Add-AsrPerRuleRawEntries (Get-RegistryValueSafe 'HKLM:\SOFTWARE\Microsoft\PolicyManager\current\device\Defender\ASROnlyPerRuleExclusions' 'Value')

        Add-Setting 'ASROnlyPerRuleExclusions' $asrPerRuleExclusionsRaw

        $asrIds = @($pref.AttackSurfaceReductionRules_Ids)
        $asrActions = @($pref.AttackSurfaceReductionRules_Actions)
        $asrCount = [Math]::Max($asrIds.Count, $asrActions.Count)
        for ($i = 0; $i -lt $asrCount; $i++) {
          $ruleId = if ($i -lt $asrIds.Count) { [string]$asrIds[$i] } else { '' }
          $actionValue = if ($i -lt $asrActions.Count) { $asrActions[$i] } else { $null }
          if ([string]::IsNullOrWhiteSpace($ruleId) -and $null -eq $actionValue) { continue }
          $actionText = if ($null -eq $actionValue) { '' } else { [string]$actionValue }
          $asrRules.Add([ordered]@{
            ruleId = $ruleId
            action = $actionText
          })
        }

        Add-Exclusion 'Path' $pref.ExclusionPath
        Add-Exclusion 'Process' $pref.ExclusionProcess
        Add-Exclusion 'Extension' $pref.ExclusionExtension
        Add-Exclusion 'IpAddress' $pref.ExclusionIpAddress
        Add-Exclusion 'ASROnly' $pref.AttackSurfaceReductionOnlyExclusions

        if ($null -ne $status) {
          foreach ($name in @(
            'AMRunningMode','AMServiceEnabled','AntispywareEnabled','AntivirusEnabled',
            'BehaviorMonitorEnabled','IoavProtectionEnabled','OnAccessProtectionEnabled',
            'RealTimeProtectionEnabled','NISEnabled','IsTamperProtected','AntivirusSignatureVersion',
            'AntispywareSignatureVersion','NISSignatureVersion','AntivirusSignatureAge',
            'AntivirusSignatureLastUpdated','QuickScanStartTime','QuickScanEndTime',
            'FullScanStartTime','FullScanEndTime','LastQuickScanSource','LastFullScanSource',
            'ComputerState'
          )) {
            try { Add-Setting "Status.$name" $status.$name }
            catch { $notes.Add("Failed to read status setting '$name': $($_.Exception.Message)") }
          }
        }

        [ordered]@{
          capturedAtUtc = (Get-Date).ToUniversalTime().ToString('o')
          source = 'Get-MpPreference + Get-MpComputerStatus'
          settings = $settings
          asrRules = $asrRules
          asrPerRuleExclusionsRaw = $asrPerRuleExclusionsRaw
          exclusions = $exclusions
          notes = $notes
        } | ConvertTo-Json -Depth 8 -Compress
        """;

    private static string BuildDetectionsScript(int daysBack)
    {
        return $$"""
        $notes = New-Object System.Collections.Generic.List[string]
        $entries = New-Object System.Collections.Generic.List[object]
        $source = 'MpCmdlets'
        $sinceUtc = (Get-Date).ToUniversalTime().AddDays(-{{daysBack}})

        function To-IsoUtc([object]$value) {
          if ($null -eq $value) { return $null }
          try { return ([DateTime]$value).ToUniversalTime().ToString('o') } catch { return $null }
        }

        function To-Severity([int]$severityId) {
          switch ($severityId) {
            { $_ -ge 5 } { return 'Critical' }
            4 { return 'High' }
            3 { return 'Moderate' }
            2 { return 'Low' }
            default { return 'Unknown' }
          }
        }

        function To-ThreatStatusText([object]$value) {
          $statusId = 0
          if ($null -eq $value -or -not [int]::TryParse([string]$value, [ref]$statusId)) {
            return ''
          }

          switch ($statusId) {
            1 { return 'Detected' }
            2 { return 'Cleaned' }
            3 { return 'Quarantined' }
            4 { return 'Removed' }
            5 { return 'Allowed' }
            6 { return 'Blocked' }
            101 { return 'Clean failed' }
            102 { return 'Quarantine failed' }
            103 { return 'Remove failed' }
            104 { return 'Allow failed' }
            105 { return 'Abandoned' }
            107 { return 'Block failed' }
            default { return '' }
          }
        }

        function Resolve-IsActive([object]$threatStatusId, [object]$actionSuccess) {
          $statusId = 0
          if ($null -ne $threatStatusId -and [int]::TryParse([string]$threatStatusId, [ref]$statusId)) {
            switch ($statusId) {
              2 { return $false }
              3 { return $false }
              4 { return $false }
              1 { return $true }
              5 { return $true }
              6 { return $true }
              101 { return $true }
              102 { return $true }
              103 { return $true }
              104 { return $true }
              105 { return $true }
              107 { return $true }
            }
          }

          if ($null -ne $actionSuccess) {
            return (-not [bool]$actionSuccess)
          }

          return $true
        }

        $threatMap = @{}
        try {
          if (Get-Command -Name Get-MpThreat -ErrorAction SilentlyContinue) {
            foreach ($threat in @(Get-MpThreat -ErrorAction SilentlyContinue)) {
              if ($null -ne $threat.ThreatID) { $threatMap[[string]$threat.ThreatID] = $threat }
            }
          } else {
            $notes.Add('Get-MpThreat cmdlet is not available.')
          }
        } catch { $notes.Add("Get-MpThreat failed: $($_.Exception.Message)") }

        $detectionsLoaded = $false
        if (Get-Command -Name Get-MpThreatDetection -ErrorAction SilentlyContinue) {
          try {
            foreach ($detection in @(Get-MpThreatDetection -ErrorAction Stop)) {
              $detectedAtUtc = To-IsoUtc $detection.InitialDetectionTime
              $statusChangedUtc = To-IsoUtc $detection.LastThreatStatusChangeTime

              $detectedAtValue = if ($detectedAtUtc) { [DateTimeOffset]$detectedAtUtc } else { $null }
              $statusChangedValue = if ($statusChangedUtc) { [DateTimeOffset]$statusChangedUtc } else { $null }
              if (($null -ne $detectedAtValue -and $detectedAtValue -lt $sinceUtc) -and ($null -ne $statusChangedValue -and $statusChangedValue -lt $sinceUtc)) {
                continue
              }

              $threatId = if ($null -ne $detection.ThreatID) { [int]$detection.ThreatID } else { $null }
              $threatName = [string]$detection.ThreatName
              $severity = 'Unknown'
              $category = ''

              if ($null -ne $threatId -and $threatMap.ContainsKey([string]$threatId)) {
                $threat = $threatMap[[string]$threatId]
                $severity = To-Severity ([int]($threat.SeverityID -as [int]))
                $category = [string]$threat.CategoryID
                if ([string]::IsNullOrWhiteSpace($threatName)) { $threatName = [string]$threat.ThreatName }
              }

              if ([string]::IsNullOrWhiteSpace($threatName)) { $threatName = 'Unknown threat' }

              $actionSuccess = $null
              if ($detection.PSObject.Properties.Name -contains 'ActionSuccess' -and $null -ne $detection.ActionSuccess) {
                $actionSuccess = [bool]$detection.ActionSuccess
              }

              $threatStatusId = if ($detection.PSObject.Properties.Name -contains 'ThreatStatusID' -and $null -ne $detection.ThreatStatusID) {
                [int]$detection.ThreatStatusID
              } else {
                $null
              }
              $threatStatusText = To-ThreatStatusText $threatStatusId
              $isActive = Resolve-IsActive $threatStatusId $actionSuccess
              $actionText = if ([string]::IsNullOrWhiteSpace($threatStatusText)) {
                [string]$threatStatusId
              } elseif ($null -eq $threatStatusId) {
                $threatStatusText
              } else {
                $threatStatusText + ' (' + [string]$threatStatusId + ')'
              }

              $entries.Add([ordered]@{
                detectedAtUtc = $detectedAtUtc
                lastStatusChangeUtc = $statusChangedUtc
                threatName = $threatName
                threatId = $threatId
                severity = $severity
                category = $category
                action = $actionText
                actionSuccess = $actionSuccess
                isActive = $isActive
                source = 'MpCmdlets'
                details = ($detection | ConvertTo-Json -Compress -Depth 6)
              })
            }

            $detectionsLoaded = $true
          } catch { $notes.Add("Get-MpThreatDetection failed: $($_.Exception.Message)") }
        } else {
          $notes.Add('Get-MpThreatDetection cmdlet is not available.')
        }

        if (-not $detectionsLoaded -or $entries.Count -eq 0) {
          $source = 'DefenderOperationalEvent'
          try {
            foreach ($event in @(Get-WinEvent -FilterHashtable @{ LogName = '{{DefenderOperationalLog}}'; Id = 1116, 1117, 1118; StartTime = $sinceUtc } -MaxEvents 500 -ErrorAction Stop)) {
              $message = [string]$event.Message
              $threatName = 'Unknown threat'
              $severity = 'Unknown'

              if ($message -match '(?im)Name:\s*(.+)$') { $threatName = $Matches[1].Trim() }
              if ($message -match '(?i)severe|critical') { $severity = 'Critical' }
              elseif ($message -match '(?i)high') { $severity = 'High' }
              elseif ($message -match '(?i)moderate') { $severity = 'Moderate' }
              elseif ($message -match '(?i)low') { $severity = 'Low' }

              $entries.Add([ordered]@{
                detectedAtUtc = To-IsoUtc $event.TimeCreated
                lastStatusChangeUtc = To-IsoUtc $event.TimeCreated
                threatName = $threatName
                threatId = $null
                severity = $severity
                category = ''
                action = [string]$event.Id
                actionSuccess = if ($event.Id -eq 1117) { $true } elseif ($event.Id -eq 1118) { $true } else { $null }
                isActive = ($event.Id -eq 1116)
                source = 'DefenderOperationalEvent'
                details = $message
              })
            }
          } catch { $notes.Add("Defender event fallback failed: $($_.Exception.Message)") }
        }

        [ordered]@{
          source = $source
          notes = $notes
          entries = $entries
        } | ConvertTo-Json -Depth 8 -Compress
        """;
    }

    private static string BuildDeviceControlEventsScript(int daysBack)
    {
        return $$"""
        $notes = New-Object System.Collections.Generic.List[string]
        $entries = New-Object System.Collections.Generic.List[object]
        $logs = @(
          'Microsoft-Windows-Windows Defender/Operational',
          'Microsoft-Windows-Sense/Operational'
        )
        $sinceUtc = (Get-Date).ToUniversalTime().AddDays(-{{daysBack}})

        function To-IsoUtc([object]$value) {
          if ($null -eq $value) { return $null }
          try { return ([DateTime]$value).ToUniversalTime().ToString('o') } catch { return $null }
        }

        function Normalize-Text([object]$value) {
          if ($null -eq $value) { return '' }
          return ([string]$value).Trim()
        }

        function Get-EventDataMap([string]$xmlText) {
          $map = [System.Collections.Generic.Dictionary[string,string]]::new([System.StringComparer]::OrdinalIgnoreCase)
          if ([string]::IsNullOrWhiteSpace($xmlText)) { return $map }

          try {
            [xml]$xml = $xmlText
            foreach ($node in @($xml.SelectNodes('//*[local-name()="Data"]'))) {
              $name = $null
              if ($null -ne $node.Attributes -and $null -ne $node.Attributes['Name']) {
                $name = [string]$node.Attributes['Name'].Value
              }
              if ([string]::IsNullOrWhiteSpace($name)) { continue }
              $value = Normalize-Text $node.InnerText
              if (-not [string]::IsNullOrWhiteSpace($value) -and -not $map.ContainsKey($name)) {
                $map.Add($name, $value)
              }
            }
          } catch { }

          return $map
        }

        function Get-FirstDataValue($map, [string[]]$names) {
          foreach ($name in $names) {
            if ($map.ContainsKey($name)) {
              $value = Normalize-Text $map[$name]
              if (-not [string]::IsNullOrWhiteSpace($value)) { return $value }
            }
          }

          return ''
        }

        function Get-MessageValue([string]$message, [string[]]$labels) {
          if ([string]::IsNullOrWhiteSpace($message)) { return '' }

          foreach ($label in $labels) {
            $escaped = [regex]::Escape($label)
            if ($message -match "(?im)^\s*$escaped\s*[:=]\s*(?<value>.+?)\s*$") {
              return $Matches['value'].Trim()
            }
          }

          return ''
        }

        function Get-Value($map, [string]$message, [string[]]$names) {
          $value = Get-FirstDataValue $map $names
          if (-not [string]::IsNullOrWhiteSpace($value)) { return $value }
          return Get-MessageValue $message $names
        }

        function Test-Blocked([string]$verdict, [string]$action, [string]$message) {
          $combined = (($verdict, $action, $message) -join ' ')
          return ($combined -match '(?i)\b(deny|denied|block|blocked|disallowed|not allowed)\b')
        }

        function Resolve-DeviceType([string]$deviceType, [string]$deviceName, [string]$classGuid, [string]$message) {
          $combined = (($deviceType, $deviceName, $classGuid, $message) -join ' ')
          if ($combined -match '(?i)printer|print') { return 'Printer' }
          if ($combined -match '(?i)usb|removable|storage|disk') { return 'Removable storage' }
          if (-not [string]::IsNullOrWhiteSpace($deviceType)) { return $deviceType }
          return 'Unknown'
        }

        function Test-CandidateDeviceControlEvent([string]$message, $map, [bool]$isBlocked) {
          $dataText = (($map.Keys | ForEach-Object { $_ + '=' + $map[$_] }) -join ' ')
          $combined = (($message, $dataText) -join ' ')
          if ($isBlocked) { return $true }
          return ($combined -match '(?i)(Device Control|DeviceControl|RemovableStoragePolicyTriggered|RemovableStorage|Printer|USB|blocked|denied)')
        }

        foreach ($log in $logs) {
          try {
            if ($null -eq (Get-WinEvent -ListLog $log -ErrorAction SilentlyContinue)) {
              $notes.Add("Event log '$log' is not available.") | Out-Null
              continue
            }

            $events = @(Get-WinEvent -FilterHashtable @{ LogName = $log; StartTime = $sinceUtc } -MaxEvents 800 -ErrorAction Stop)
            foreach ($event in $events) {
              $xmlText = ''
              try { $xmlText = [string]$event.ToXml() } catch { }

              $message = Normalize-Text $event.Message
              $map = Get-EventDataMap $xmlText
              $deviceId = Get-Value $map $message @('DeviceId', 'Device ID', 'DeviceIdString')
              $deviceInstanceId = Get-Value $map $message @('DeviceInstanceId', 'Device Instance Id', 'DeviceInstancePath', 'InstanceId', 'DeviceInstanceID')
              $hardwareIds = Get-Value $map $message @('HardwareIds', 'Hardware IDs', 'HardwareId')
              $vendorId = Get-Value $map $message @('VendorId', 'Vendor ID', 'VID')
              $productId = Get-Value $map $message @('ProductId', 'Product ID', 'PID')
              $serialNumber = Get-Value $map $message @('SerialNumber', 'Serial Number', 'Serial')
              $classGuid = Get-Value $map $message @('ClassGuid', 'Class Guid', 'DeviceClassGuid')
              $deviceName = Get-Value $map $message @('DeviceName', 'Device Name', 'Name')
              $friendlyName = Get-Value $map $message @('FriendlyName', 'Friendly Name')
              $manufacturer = Get-Value $map $message @('Manufacturer', 'Vendor')
              $policyName = Get-Value $map $message @('PolicyName', 'Policy Name')
              $policyId = Get-Value $map $message @('PolicyId', 'Policy ID')
              $policyRuleId = Get-Value $map $message @('PolicyRuleId', 'RuleId', 'Policy Rule ID')
              $policyVerdict = Get-Value $map $message @('PolicyVerdict', 'Verdict', 'Policy Verdict')
              $access = Get-Value $map $message @('Access', 'AccessMask', 'Access Request')
              $action = Get-Value $map $message @('Action', 'Enforcement', 'Decision')
              $user = Get-Value $map $message @('User', 'UserName', 'AccountName')
              $sid = Get-Value $map $message @('Sid', 'UserSid', 'User SID')
              $deviceType = Resolve-DeviceType (Get-Value $map $message @('DeviceType', 'Device Type', 'ClassName')) $deviceName $classGuid $message
              $isBlocked = Test-Blocked $policyVerdict $action $message

              if (-not (Test-CandidateDeviceControlEvent $message $map $isBlocked)) {
                continue
              }

              $entries.Add([ordered]@{
                timeCreatedUtc = To-IsoUtc $event.TimeCreated
                eventId = [int]$event.Id
                provider = [string]$event.ProviderName
                logName = $log
                level = [string]$event.LevelDisplayName
                deviceType = $deviceType
                deviceName = $deviceName
                friendlyName = $friendlyName
                manufacturer = $manufacturer
                deviceId = $deviceId
                deviceInstanceId = $deviceInstanceId
                hardwareIds = $hardwareIds
                vendorId = $vendorId
                productId = $productId
                serialNumber = $serialNumber
                classGuid = $classGuid
                user = $user
                sid = $sid
                policyName = $policyName
                policyId = $policyId
                policyRuleId = $policyRuleId
                policyVerdict = $policyVerdict
                access = $access
                action = $action
                isBlocked = [bool]$isBlocked
                message = $message
              }) | Out-Null
            }
          } catch {
            $notes.Add("Device Control event query failed for '$log': $($_.Exception.Message)") | Out-Null
          }
        }

        [ordered]@{
          capturedAtUtc = (Get-Date).ToUniversalTime().ToString('o')
          source = 'Local Device Control events'
          notes = $notes
          entries = $entries
        } | ConvertTo-Json -Depth 8 -Compress
        """;
    }

    private static string BuildActionScript(string actionId)
    {
        var escaped = actionId.Replace("'", "''", StringComparison.Ordinal);
        return $$"""
        $actionId = '{{escaped}}'
        $result = [ordered]@{
          success = $true
          message = ''
          errorCode = ''
          executedAtUtc = (Get-Date).ToUniversalTime().ToString('o')
        }

        try {
          switch ($actionId) {
            'quick-scan' { Start-MpScan -ScanType QuickScan -ErrorAction Stop; $result.message = 'Quick scan started.' }
            'full-scan' { Start-MpScan -ScanType FullScan -ErrorAction Stop; $result.message = 'Full scan started.' }
            'stop-scan' { Stop-MpScan -ErrorAction Stop; $result.message = 'Scan stop requested.' }
            'signature-update' { Update-MpSignature -ErrorAction Stop | Out-Null; $result.message = 'Signature update triggered.' }
            'restart-service' { Restart-Service -Name WinDefend -Force -ErrorAction Stop; $result.message = 'WinDefend service restarted.' }
            default { throw "Unknown defender action '$actionId'." }
          }
        } catch {
          $result.success = $false
          $result.message = $_.Exception.Message
          $result.errorCode = 'defender_action_failed'
        }

        $result | ConvertTo-Json -Depth 5 -Compress
        """;
    }

    private sealed class SnapshotPayload
    {
        public string? MachineName { get; init; }
        public string? CapturedAtUtc { get; init; }
        public bool IsGpoManaged { get; init; }
        public bool IsMdmManaged { get; init; }
        public bool IsMdeManaged { get; init; }
        public int? ManagedDefenderProductType { get; init; }
        public int? EnrollmentStatus { get; init; }
        public int? OnboardingState { get; init; }
        public bool? AntivirusEnabled { get; init; }
        public bool? RealtimeProtectionEnabled { get; init; }
        public bool? BehaviorMonitorEnabled { get; init; }
        public bool? IoavProtectionEnabled { get; init; }
        public bool? OnAccessProtectionEnabled { get; init; }
        public bool? NisEnabled { get; init; }
        public bool? TamperProtectionEnabled { get; init; }
        public string? RunningMode { get; init; }
        public string? EngineVersion { get; init; }
        public string? ProductVersion { get; init; }
        public string? AntivirusSignatureVersion { get; init; }
        public string? AntispywareSignatureVersion { get; init; }
        public string? NisEngineVersion { get; init; }
        public string? NisSignatureVersion { get; init; }
        public string? SignatureLastUpdatedUtc { get; init; }
        public double SignatureAgeHours { get; init; }
        public string? QuickScanStartUtc { get; init; }
        public string? QuickScanEndUtc { get; init; }
        public string? FullScanStartUtc { get; init; }
        public string? FullScanEndUtc { get; init; }
        public string? LastScanUtc { get; init; }
        public int ActiveDetectionCount { get; init; }
        public int ActiveHighOrCriticalDetectionCount { get; init; }
        public List<string>? Notes { get; init; }
    }

    private sealed class SettingsPayload
    {
        public string? CapturedAtUtc { get; init; }
        public string? Source { get; init; }
        public List<NameValuePayload>? Settings { get; init; }
        public List<AsrRulePayload>? AsrRules { get; init; }
        public List<string>? AsrPerRuleExclusionsRaw { get; init; }
        public List<ExclusionPayload>? Exclusions { get; init; }
        public List<string>? Notes { get; init; }
    }

    private sealed class NameValuePayload
    {
        public string? Name { get; init; }
        public string? Value { get; init; }
    }

    private sealed class AsrRulePayload
    {
        public string? RuleId { get; init; }
        public string? Action { get; init; }
    }

    private sealed class ExclusionPayload
    {
        public string? Type { get; init; }
        public string? Value { get; init; }
    }

    private sealed class DetectionsPayload
    {
        public string? Source { get; init; }
        public List<string>? Notes { get; init; }
        public List<DetectionPayload>? Entries { get; init; }
    }

    private sealed class DetectionPayload
    {
        public string? DetectedAtUtc { get; init; }
        public string? LastStatusChangeUtc { get; init; }
        public string? ThreatName { get; init; }
        public int? ThreatId { get; init; }
        public string? Severity { get; init; }
        public string? Category { get; init; }
        public string? Action { get; init; }
        public bool? ActionSuccess { get; init; }
        public bool IsActive { get; init; }
        public string? Source { get; init; }
        public string? Details { get; init; }
    }

    private sealed class DeviceControlPayload
    {
        public string? CapturedAtUtc { get; init; }
        public string? Source { get; init; }
        public List<string>? Notes { get; init; }
        public List<DeviceControlEventPayload>? Entries { get; init; }
    }

    private sealed class DeviceControlEventPayload
    {
        public string? TimeCreatedUtc { get; init; }
        public int EventId { get; init; }
        public string? Provider { get; init; }
        public string? LogName { get; init; }
        public string? Level { get; init; }
        public string? DeviceType { get; init; }
        public string? DeviceName { get; init; }
        public string? FriendlyName { get; init; }
        public string? Manufacturer { get; init; }
        public string? DeviceId { get; init; }
        public string? DeviceInstanceId { get; init; }
        public string? HardwareIds { get; init; }
        public string? VendorId { get; init; }
        public string? ProductId { get; init; }
        public string? SerialNumber { get; init; }
        public string? ClassGuid { get; init; }
        public string? User { get; init; }
        public string? Sid { get; init; }
        public string? PolicyName { get; init; }
        public string? PolicyId { get; init; }
        public string? PolicyRuleId { get; init; }
        public string? PolicyVerdict { get; init; }
        public string? Access { get; init; }
        public string? Action { get; init; }
        public bool IsBlocked { get; init; }
        public string? Message { get; init; }
    }

    private sealed class ActionPayload
    {
        public bool Success { get; init; }
        public string? Message { get; init; }
        public string? ErrorCode { get; init; }
        public string? ExecutedAtUtc { get; init; }
    }
}
