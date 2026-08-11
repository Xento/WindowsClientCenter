using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using WindowsClientCenter.Plugins.WindowsUpdateAgent.Models.UsoStore;

namespace WindowsClientCenter.Plugins.WindowsUpdateAgent.Services.UsoStore;

public sealed class UpdateLifecycleService
{
    private static readonly string[] ExplicitInstallDeadlineKeys =
    [
        "InstallDeadline",
        "InstallDeadlineTime",
        "DeploymentDeadline",
        "DeploymentDeadlineTime"
    ];

    private static readonly string[] GenericDeadlineKeys =
    [
        "Deadline",
        "DeadlineTime"
    ];

    private static readonly string[] ExplicitRebootDeadlineKeys =
    [
        "RebootDeadline",
        "RebootDeadlineTime",
        "RebootRequiredDeadline",
        "RebootRequiredDeadlineTime",
        "RestartDeadline",
        "RestartDeadlineTime"
    ];

    private static readonly string[] RebootEvidenceTimeKeys =
    [
        "RebootRequiredTime",
        "RebootRecognitionTime"
    ];

    private readonly TimestampParser _timestampParser;

    public UpdateLifecycleService(TimestampParser timestampParser)
    {
        _timestampParser = timestampParser;
    }

    public IReadOnlyList<UpdateLifecycleRecord> Build(
        IReadOnlyList<UsoCompletedUpdateRecord> completedUpdates,
        IReadOnlyList<UsoUpdatePropertyRecord> updateProperties,
        IReadOnlyList<UsoActionRecord> actionRecords)
    {
        var completedByKey = completedUpdates
            .GroupBy(record => GetUpdateKey(record.ProviderId, record.UpdateId), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(item => _timestampParser.ParseFlexibleDateTime(item.TimeRaw)).First(), StringComparer.OrdinalIgnoreCase);

        var propertiesByKey = updateProperties
            .GroupBy(record => GetUpdateKey(record.ProviderId, record.UpdateId), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToDictionary(item => item.Variable, item => item, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);

        var actionsByKey = actionRecords
            .GroupBy(record => GetUpdateKey(record.ProviderId, record.UpdateId), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);

        var allKeys = completedByKey.Keys
            .Concat(propertiesByKey.Keys)
            .Concat(actionsByKey.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase);

        return allKeys
            .Select(key => BuildRecord(key, completedByKey, propertiesByKey, actionsByKey))
            .OrderByDescending(record => GetSortTime(record) ?? DateTime.MinValue)
            .ToArray();
    }

    private UpdateLifecycleRecord BuildRecord(
        string key,
        IReadOnlyDictionary<string, UsoCompletedUpdateRecord> completedByKey,
        IReadOnlyDictionary<string, Dictionary<string, UsoUpdatePropertyRecord>> propertiesByKey,
        IReadOnlyDictionary<string, UsoActionRecord[]> actionsByKey)
    {
        completedByKey.TryGetValue(key, out var completed);
        propertiesByKey.TryGetValue(key, out var properties);
        actionsByKey.TryGetValue(key, out var actions);

        var providerId = completed?.ProviderId ?? properties?.Values.FirstOrDefault()?.ProviderId ?? actions?.FirstOrDefault()?.ProviderId ?? string.Empty;
        var updateId = completed?.UpdateId ?? properties?.Values.FirstOrDefault()?.UpdateId ?? actions?.FirstOrDefault()?.UpdateId ?? string.Empty;
        var downloadAction = actions?.Where(action => IsAction(action, "Download")).OrderByDescending(action => _timestampParser.ParseFlexibleDateTime(action.TimeRaw)).FirstOrDefault();
        var installAction = actions?.Where(action => IsAction(action, "Install")).OrderByDescending(action => _timestampParser.ParseFlexibleDateTime(action.TimeRaw)).FirstOrDefault();
        var titleResolution = ResolveTitle(completed, updateId);

        var discoveryTime = GetDateTime(properties, "DiscoveryTime");
        var updateAttempted = GetDateTime(properties, "UpdateAttempted");
        var updateActionDelayCount = GetInteger(properties, "UpdateActionDelayCount");
        var updateActionDelayTime = GetDateTime(properties, "UpdateActionDelayTime");
        var rebootRequiredTime = GetDateTime(properties, "RebootRequiredTime");
        var rebootRecognitionTime = GetDateTime(properties, "RebootRecognitionTime");
        var completedTime = _timestampParser.ParseFlexibleDateTime(completed?.TimeRaw);
        var wasRebootRequired = GetBoolean(properties, "WasRebootRequired") ?? completed?.WasRebootRequired;
        var updateBlock = GetValue(properties, "UpdateBlock");
        var lastUpdateBlock = GetValue(properties, "LastUpdateBlock");
        var updateBlockSummary = ResolveUpdateBlockSummary(updateBlock, lastUpdateBlock);
        var actionTags = GetValue(properties, "ActionTags");
        var deadlineAnalysis = ResolveDeadlineAnalysis(properties, updateBlock, lastUpdateBlock);
        var schedulingAnalysis = BuildSchedulingAnalysis(
            discoveryTime,
            updateAttempted,
            updateActionDelayCount,
            updateActionDelayTime,
            updateBlock,
            lastUpdateBlock,
            updateBlockSummary,
            deadlineAnalysis);
        var importantUpdateProperties = BuildImportantUpdateProperties(properties);
        var rawUpdatePropertiesJson = BuildRawUpdatePropertiesJson(properties);
        var stateSummary = ResolveStateSummary(completedTime, wasRebootRequired, rebootRequiredTime, updateBlock, lastUpdateBlock, properties is not null, actions is not null);

        return new UpdateLifecycleRecord
        {
            ProviderId = providerId,
            UpdateId = updateId,
            Title = titleResolution.Title,
            ResolvedTitleSource = titleResolution.Source,
            Description = completed?.Description ?? string.Empty,
            HistoryCategory = completed?.HistoryCategory ?? string.Empty,
            CompletedTime = _timestampParser.FormatDateTime(completedTime),
            CompletedTimeLocal = completedTime,
            WasRebootRequired = wasRebootRequired,
            DiscoveryTime = _timestampParser.FormatDateTime(discoveryTime),
            DiscoveryTimeLocal = discoveryTime,
            QueueNumber = GetInteger(properties, "QueueNumber"),
            QueueNumberDisplay = FormatInteger(GetInteger(properties, "QueueNumber")),
            Approved = GetBoolean(properties, "Approved"),
            ApprovedTime = _timestampParser.FormatDateTime(GetDateTime(properties, "ApprovedTime")),
            ApprovedTimeLocal = GetDateTime(properties, "ApprovedTime"),
            UpdateAttempted = _timestampParser.FormatDateTime(updateAttempted),
            UpdateAttemptedLocal = updateAttempted,
            UpdateActionDelayCount = FormatInteger(updateActionDelayCount),
            UpdateActionDelayTime = _timestampParser.FormatDateTime(updateActionDelayTime),
            UpdateActionDelayTimeLocal = updateActionDelayTime,
            SchedulingSummary = schedulingAnalysis.Summary,
            SchedulingDetails = schedulingAnalysis.Details,
            DownloadActionTime = _timestampParser.FormatDateTime(_timestampParser.ParseFlexibleDateTime(downloadAction?.TimeRaw)),
            DownloadActionTimeLocal = _timestampParser.ParseFlexibleDateTime(downloadAction?.TimeRaw),
            DownloadActionResult = FormatActionResult(downloadAction?.Result),
            InstallActionTime = _timestampParser.FormatDateTime(_timestampParser.ParseFlexibleDateTime(installAction?.TimeRaw)),
            InstallActionTimeLocal = _timestampParser.ParseFlexibleDateTime(installAction?.TimeRaw),
            InstallActionResult = FormatActionResult(installAction?.Result),
            RebootRequiredTime = _timestampParser.FormatDateTime(rebootRequiredTime),
            RebootRequiredTimeLocal = rebootRequiredTime,
            RebootRecognitionTime = _timestampParser.FormatDateTime(rebootRecognitionTime),
            RebootRecognitionTimeLocal = rebootRecognitionTime,
            ProbableInstallDeadline = _timestampParser.FormatDateTime(deadlineAnalysis.InstallDeadline),
            ProbableInstallDeadlineLocal = deadlineAnalysis.InstallDeadline,
            ProbableRebootDeadline = _timestampParser.FormatDateTime(deadlineAnalysis.RebootDeadline),
            ProbableRebootDeadlineLocal = deadlineAnalysis.RebootDeadline,
            DeadlineConfidenceText = deadlineAnalysis.ConfidenceText,
            DeadlineExplanation = deadlineAnalysis.Explanation,
            UpdateBlock = updateBlock,
            LastUpdateBlock = lastUpdateBlock,
            UpdateBlockSummary = updateBlockSummary,
            UpdateBlockTime = _timestampParser.FormatDateTime(GetDateTime(properties, "UpdateBlockTime")),
            UpdateBlockTimeLocal = GetDateTime(properties, "UpdateBlockTime"),
            LastUpdateBlockTime = _timestampParser.FormatDateTime(GetDateTime(properties, "LastUpdateBlockTime")),
            LastUpdateBlockTimeLocal = GetDateTime(properties, "LastUpdateBlockTime"),
            DownloadSizeBytes = GetInteger(properties, "DownloadSize"),
            DownloadSizeDisplay = FormatByteSize(GetInteger(properties, "DownloadSize")),
            IsIpu = GetBoolean(properties, "isIpu"),
            IsIpuDisplay = FormatBoolean(GetBoolean(properties, "isIpu")),
            WorkBit = GetBoolean(properties, "WorkBit"),
            WorkBitDisplay = FormatBoolean(GetBoolean(properties, "WorkBit")),
            CorrelationVector = GetValue(properties, "CorrelationVector"),
            ActionTags = actionTags,
            ActionTagsSummary = FormatActionTagsSummary(actionTags),
            Metadata = completed?.Metadata ?? string.Empty,
            ImportantUpdateProperties = importantUpdateProperties,
            RawUpdatePropertiesJson = rawUpdatePropertiesJson,
            MoreInfoUrl = completed?.MoreInfoUrl ?? string.Empty,
            StateSummary = stateSummary,
            SearchText = string.Join(
                " ",
                providerId,
                updateId,
                titleResolution.Title,
                titleResolution.Source,
                completed?.HistoryCategory,
                stateSummary,
                deadlineAnalysis.ConfidenceText,
                deadlineAnalysis.Explanation,
                schedulingAnalysis.Summary,
                schedulingAnalysis.Details,
                updateBlock,
                lastUpdateBlock,
                actionTags,
                importantUpdateProperties,
                rawUpdatePropertiesJson,
                completed?.Metadata,
                completed?.MoreInfoUrl)
        };
    }

    private static string ResolveStateSummary(
        DateTime? completedTime,
        bool? wasRebootRequired,
        DateTime? rebootRequiredTime,
        string updateBlock,
        string lastUpdateBlock,
        bool hasProperties,
        bool hasActions)
    {
        if (completedTime.HasValue && wasRebootRequired == true)
        {
            return "Completed, reboot required";
        }

        if (completedTime.HasValue)
        {
            return "Completed";
        }

        if (ContainsReadyToReboot(updateBlock) || ContainsReadyToReboot(lastUpdateBlock))
        {
            return "Blocked: ReadyToReboot";
        }

        if (rebootRequiredTime.HasValue || wasRebootRequired == true)
        {
            return "Reboot required, waiting";
        }

        if (hasProperties || hasActions)
        {
            return "Partial telemetry only";
        }

        return "Unknown";
    }

    private DateTime? GetSortTime(UpdateLifecycleRecord record)
    {
        return record.RebootRequiredTimeLocal
            ?? record.ProbableRebootDeadlineLocal
            ?? record.ProbableInstallDeadlineLocal
            ?? record.CompletedTimeLocal
            ?? record.InstallActionTimeLocal
            ?? record.DownloadActionTimeLocal
            ?? record.DiscoveryTimeLocal;
    }

    private TitleResolution ResolveTitle(UsoCompletedUpdateRecord? completed, string updateId)
    {
        if (!string.IsNullOrWhiteSpace(completed?.Title))
        {
            return new TitleResolution(completed.Title.Trim(), "COMPLETEDUPDATES.Title");
        }

        var metadataTitle = ResolveTitleFromMetadata(completed?.Metadata);
        if (!string.IsNullOrWhiteSpace(metadataTitle))
        {
            return new TitleResolution(metadataTitle, "COMPLETEDUPDATES.Metadata");
        }

        return new TitleResolution(updateId, "UpdateId fallback");
    }

    private static string ResolveTitleFromMetadata(string? metadata)
    {
        if (string.IsNullOrWhiteSpace(metadata))
        {
            return string.Empty;
        }

        try
        {
            using var document = JsonDocument.Parse(metadata);
            var title = FindStringProperty(document.RootElement, ["Title", "title", "UpdateTitle", "updateTitle", "Name", "name"]);
            if (!string.IsNullOrWhiteSpace(title))
            {
                return title.Trim();
            }
        }
        catch (JsonException)
        {
            // Some USO metadata blobs are text fragments rather than JSON.
        }

        var match = Regex.Match(metadata, @"(?i)\b(?:Title|UpdateTitle|Name)\b\s*[:=]\s*[""']?(?<title>[^""'\r\n;|}]+)");
        return match.Success ? match.Groups["title"].Value.Trim() : string.Empty;
    }

    private static string FindStringProperty(JsonElement element, IReadOnlyCollection<string> propertyNames)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (propertyNames.Contains(property.Name, StringComparer.OrdinalIgnoreCase) &&
                    property.Value.ValueKind == JsonValueKind.String)
                {
                    return property.Value.GetString() ?? string.Empty;
                }

                var nested = FindStringProperty(property.Value, propertyNames);
                if (!string.IsNullOrWhiteSpace(nested))
                {
                    return nested;
                }
            }
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray())
            {
                var nested = FindStringProperty(child, propertyNames);
                if (!string.IsNullOrWhiteSpace(nested))
                {
                    return nested;
                }
            }
        }

        return string.Empty;
    }

    private DeadlineAnalysis ResolveDeadlineAnalysis(
        IReadOnlyDictionary<string, UsoUpdatePropertyRecord>? properties,
        string updateBlock,
        string lastUpdateBlock)
    {
        if (properties is null || properties.Count == 0)
        {
            return new DeadlineAnalysis(null, null, "No per-update deadline evidence", "No UPDATESPROP rows are available for this update.");
        }

        var readyToReboot = ContainsReadyToReboot(updateBlock) || ContainsReadyToReboot(lastUpdateBlock);
        TryGetFirstDateTime(properties, ExplicitInstallDeadlineKeys, out var explicitInstallKey, out var explicitInstallDeadline);
        TryGetFirstDateTime(properties, GenericDeadlineKeys, out var genericDeadlineKey, out var genericDeadline);
        TryGetFirstDateTime(properties, ExplicitRebootDeadlineKeys, out var explicitRebootKey, out var explicitRebootDeadline);
        TryGetFirstDateTime(properties, RebootEvidenceTimeKeys, out var rebootEvidenceKey, out var rebootEvidenceTime);

        var installDeadline = readyToReboot ? null : explicitInstallDeadline ?? genericDeadline;
        var rebootDeadline = explicitRebootDeadline ?? (readyToReboot ? genericDeadline : null) ?? rebootEvidenceTime;
        var installSource = readyToReboot ? string.Empty : explicitInstallKey ?? genericDeadlineKey;
        var rebootSource = explicitRebootKey ?? (readyToReboot ? genericDeadlineKey : null) ?? rebootEvidenceKey;

        var confidence = ResolveDeadlineConfidence(
            installSource,
            rebootSource,
            explicitInstallDeadline.HasValue,
            explicitRebootDeadline.HasValue,
            genericDeadline.HasValue,
            rebootEvidenceTime.HasValue);
        var explanation = BuildDeadlineExplanation(
            readyToReboot,
            installSource,
            installDeadline,
            rebootSource,
            rebootDeadline);

        return new DeadlineAnalysis(installDeadline, rebootDeadline, confidence, explanation);
    }

    private static string ResolveDeadlineConfidence(
        string? installSource,
        string? rebootSource,
        bool hasExplicitInstallDeadline,
        bool hasExplicitRebootDeadline,
        bool hasGenericDeadline,
        bool hasRebootEvidenceTime)
    {
        if (hasExplicitInstallDeadline || hasExplicitRebootDeadline)
        {
            return "High (explicit per-update deadline)";
        }

        if (!string.IsNullOrWhiteSpace(installSource) || !string.IsNullOrWhiteSpace(rebootSource))
        {
            if (hasGenericDeadline)
            {
                return "Medium (generic deadline interpreted by state)";
            }

            if (hasRebootEvidenceTime)
            {
                return "Medium (reboot evidence time)";
            }
        }

        return "No per-update deadline evidence";
    }

    private string BuildDeadlineExplanation(
        bool readyToReboot,
        string? installSource,
        DateTime? installDeadline,
        string? rebootSource,
        DateTime? rebootDeadline)
    {
        var parts = new List<string>();

        if (readyToReboot)
        {
            parts.Add("UpdateBlock or LastUpdateBlock contains ReadyToReboot, so a generic Deadline value is interpreted as a probable reboot deadline.");
        }

        parts.Add(installDeadline.HasValue
            ? $"Probable install deadline from UPDATESPROP.{installSource}: {_timestampParser.FormatDateTime(installDeadline)}."
            : "No per-update install deadline key was found.");

        parts.Add(rebootDeadline.HasValue
            ? $"Probable reboot deadline from UPDATESPROP.{rebootSource}: {_timestampParser.FormatDateTime(rebootDeadline)}."
            : "No per-update reboot deadline key was found. Global USO reboot deadlines from VARIABLES are intentionally not assigned to this update.");

        return string.Join(Environment.NewLine, parts);
    }

    private SchedulingAnalysis BuildSchedulingAnalysis(
        DateTime? discoveryTime,
        DateTime? updateAttempted,
        long? updateActionDelayCount,
        DateTime? updateActionDelayTime,
        string updateBlock,
        string lastUpdateBlock,
        string updateBlockSummary,
        DeadlineAnalysis deadlineAnalysis)
    {
        var summaryParts = new List<string>();
        var details = new List<string>();

        if (discoveryTime.HasValue)
        {
            details.Add($"Discovered: {_timestampParser.FormatDateTime(discoveryTime)}.");
        }

        if (updateAttempted.HasValue)
        {
            var attemptedText = $"Attempted: {_timestampParser.FormatDateTime(updateAttempted)}";
            if (discoveryTime.HasValue)
            {
                attemptedText += $" ({FormatDuration(updateAttempted.Value - discoveryTime.Value)} after discovery)";
            }

            details.Add(attemptedText + ".");
        }

        if (!string.IsNullOrWhiteSpace(updateBlockSummary))
        {
            summaryParts.Add(updateBlockSummary);
            details.Add($"Current block interpretation: {updateBlockSummary}.");
        }

        if (updateActionDelayCount.GetValueOrDefault() > 0 || updateActionDelayTime.HasValue)
        {
            var countText = updateActionDelayCount.GetValueOrDefault() > 0
                ? $"{updateActionDelayCount.GetValueOrDefault().ToString(CultureInfo.InvariantCulture)} deferral(s)"
                : "Deferral evidence";
            var delayText = updateActionDelayTime.HasValue
                ? $" until {_timestampParser.FormatDateTime(updateActionDelayTime)}"
                : string.Empty;
            summaryParts.Add(countText + delayText);
            details.Add($"Update action delay: {countText}{delayText}.");
        }

        var nearestDeadline = ResolveNearestDeadline(deadlineAnalysis);
        if (nearestDeadline.Time.HasValue)
        {
            details.Add($"Nearest probable {nearestDeadline.Kind} deadline: {_timestampParser.FormatDateTime(nearestDeadline.Time)}.");
            if (updateActionDelayTime.HasValue)
            {
                var delta = nearestDeadline.Time.Value - updateActionDelayTime.Value;
                if (delta < TimeSpan.Zero)
                {
                    summaryParts.Add($"Delay exceeds probable {nearestDeadline.Kind} deadline");
                    details.Add($"The action delay is {FormatDuration(delta.Duration())} after the probable {nearestDeadline.Kind} deadline.");
                }
                else
                {
                    details.Add($"The action delay is {FormatDuration(delta)} before the probable {nearestDeadline.Kind} deadline.");
                }
            }
        }
        else
        {
            details.Add("No per-update install or reboot deadline was found.");
        }

        if (ContainsUserEngaged(updateBlock) || ContainsUserEngaged(lastUpdateBlock))
        {
            details.Add("UserEngaged indicates Windows Update delayed action because the user/session was active or interacting.");
        }

        var summary = summaryParts.Count == 0
            ? "No defer/deadline scheduling evidence"
            : string.Join(" | ", summaryParts.Distinct(StringComparer.OrdinalIgnoreCase));

        return new SchedulingAnalysis(summary, string.Join(Environment.NewLine, details));
    }

    private static (string Kind, DateTime? Time) ResolveNearestDeadline(DeadlineAnalysis deadlineAnalysis)
    {
        if (deadlineAnalysis.InstallDeadline.HasValue && deadlineAnalysis.RebootDeadline.HasValue)
        {
            return deadlineAnalysis.InstallDeadline <= deadlineAnalysis.RebootDeadline
                ? ("install", deadlineAnalysis.InstallDeadline)
                : ("reboot", deadlineAnalysis.RebootDeadline);
        }

        if (deadlineAnalysis.InstallDeadline.HasValue)
        {
            return ("install", deadlineAnalysis.InstallDeadline);
        }

        if (deadlineAnalysis.RebootDeadline.HasValue)
        {
            return ("reboot", deadlineAnalysis.RebootDeadline);
        }

        return ("", null);
    }

    private static string FormatDuration(TimeSpan value)
    {
        if (value.TotalDays >= 1)
        {
            return $"{value.TotalDays:0.#} day(s)";
        }

        if (value.TotalHours >= 1)
        {
            return $"{value.TotalHours:0.#} hour(s)";
        }

        if (value.TotalMinutes >= 1)
        {
            return $"{value.TotalMinutes:0.#} minute(s)";
        }

        return $"{Math.Max(0, value.TotalSeconds):0} second(s)";
    }

    private DateTime? GetPropertyDateTime(UsoUpdatePropertyRecord property)
    {
        return _timestampParser.ParseFlexibleDateTime(property.Value, property.Type);
    }

    private bool TryGetFirstDateTime(
        IReadOnlyDictionary<string, UsoUpdatePropertyRecord> properties,
        IEnumerable<string> keys,
        out string? matchedKey,
        out DateTime? value)
    {
        foreach (var key in keys)
        {
            if (!properties.TryGetValue(key, out var property))
            {
                continue;
            }

            var parsed = GetPropertyDateTime(property);
            if (parsed.HasValue)
            {
                matchedKey = key;
                value = parsed;
                return true;
            }
        }

        matchedKey = null;
        value = null;
        return false;
    }

    private string BuildImportantUpdateProperties(IReadOnlyDictionary<string, UsoUpdatePropertyRecord>? properties)
    {
        if (properties is null || properties.Count == 0)
        {
            return string.Empty;
        }

        var important = properties.Values
            .Where(IsImportantUpdateProperty)
            .OrderBy(record => record.Variable, StringComparer.OrdinalIgnoreCase)
            .Select(record => $"{record.Variable}: {FormatUpdatePropertyValue(record)} (raw: {record.Value}, type: {record.Type} - {_timestampParser.TypeToLabel(record.Type)})")
            .ToArray();

        return string.Join(Environment.NewLine, important);
    }

    private string BuildRawUpdatePropertiesJson(IReadOnlyDictionary<string, UsoUpdatePropertyRecord>? properties)
    {
        if (properties is null || properties.Count == 0)
        {
            return string.Empty;
        }

        var payload = properties.Values
            .OrderBy(record => record.Variable, StringComparer.OrdinalIgnoreCase)
            .Select(record => new
            {
                record.ProviderId,
                record.UpdateId,
                record.Variable,
                record.Value,
                record.Type,
                TypeLabel = _timestampParser.TypeToLabel(record.Type),
                ParsedValue = FormatUpdatePropertyValue(record)
            });

        return JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
    }

    private string FormatUpdatePropertyValue(UsoUpdatePropertyRecord property)
    {
        if (string.Equals(property.Variable, "DownloadSize", StringComparison.OrdinalIgnoreCase))
        {
            return FormatByteSize(_timestampParser.ParseInteger(property.Value));
        }

        if (string.Equals(property.Variable, "QueueNumber", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(property.Variable, "UpdateActionDelayCount", StringComparison.OrdinalIgnoreCase))
        {
            return FormatInteger(_timestampParser.ParseInteger(property.Value));
        }

        if (string.Equals(property.Variable, "isIpu", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(property.Variable, "WorkBit", StringComparison.OrdinalIgnoreCase))
        {
            return FormatBoolean(_timestampParser.ParseBoolean(property.Value));
        }

        if (string.Equals(property.Variable, "ActionTags", StringComparison.OrdinalIgnoreCase))
        {
            return FormatActionTagsSummary(property.Value);
        }

        var parsedTime = GetPropertyDateTime(property);
        return parsedTime.HasValue
            ? _timestampParser.FormatDateTime(parsedTime)
            : _timestampParser.FormatParsedValue(property.Value, property.Type);
    }

    private static bool IsImportantUpdateProperty(UsoUpdatePropertyRecord property)
    {
        var variable = property.Variable;
        return ExplicitInstallDeadlineKeys.Contains(variable, StringComparer.OrdinalIgnoreCase) ||
               GenericDeadlineKeys.Contains(variable, StringComparer.OrdinalIgnoreCase) ||
               ExplicitRebootDeadlineKeys.Contains(variable, StringComparer.OrdinalIgnoreCase) ||
               RebootEvidenceTimeKeys.Contains(variable, StringComparer.OrdinalIgnoreCase) ||
               string.Equals(variable, "QueueNumber", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(variable, "DownloadSize", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(variable, "isIpu", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(variable, "WorkBit", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(variable, "CorrelationVector", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(variable, "ActionTags", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(variable, "UpdateActionDelayCount", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(variable, "UpdateActionDelayTime", StringComparison.OrdinalIgnoreCase) ||
               variable.Contains("Deadline", StringComparison.OrdinalIgnoreCase) ||
               variable.Contains("Reboot", StringComparison.OrdinalIgnoreCase) ||
               variable.Contains("Block", StringComparison.OrdinalIgnoreCase) ||
               variable.Contains("Time", StringComparison.OrdinalIgnoreCase) ||
               variable.Contains("Result", StringComparison.OrdinalIgnoreCase) ||
               variable.Contains("Error", StringComparison.OrdinalIgnoreCase) ||
               variable.Contains("Status", StringComparison.OrdinalIgnoreCase);
    }

    private DateTime? GetDateTime(IReadOnlyDictionary<string, UsoUpdatePropertyRecord>? properties, string key)
    {
        if (properties is null || !properties.TryGetValue(key, out var property))
        {
            return null;
        }

        return _timestampParser.ParseFlexibleDateTime(property.Value, property.Type);
    }

    private long? GetInteger(IReadOnlyDictionary<string, UsoUpdatePropertyRecord>? properties, string key)
    {
        if (properties is null || !properties.TryGetValue(key, out var property))
        {
            return null;
        }

        return _timestampParser.ParseInteger(property.Value);
    }

    private bool? GetBoolean(IReadOnlyDictionary<string, UsoUpdatePropertyRecord>? properties, string key)
    {
        if (properties is null || !properties.TryGetValue(key, out var property))
        {
            return null;
        }

        return _timestampParser.ParseBoolean(property.Value);
    }

    private static string GetValue(IReadOnlyDictionary<string, UsoUpdatePropertyRecord>? properties, string key)
    {
        return properties is not null && properties.TryGetValue(key, out var property)
            ? property.Value
            : string.Empty;
    }

    private static bool IsAction(UsoActionRecord action, string actionName)
    {
        return string.Equals(action.Action, actionName, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(action.ActionClass, actionName, StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsReadyToReboot(string rawText)
    {
        return rawText.Contains("ReadyToReboot", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsUserEngaged(string rawText)
    {
        return rawText.Contains("UserEngaged", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetUpdateKey(string providerId, string updateId)
    {
        return $"{providerId}::{updateId}";
    }

    private static string FormatActionResult(int? result)
    {
        return result switch
        {
            null => string.Empty,
            0 => "0 (Success)",
            _ => result.Value.ToString(CultureInfo.InvariantCulture)
        };
    }

    private static string FormatInteger(long? value)
    {
        return value?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static string FormatBoolean(bool? value)
    {
        return value switch
        {
            true => "Yes",
            false => "No",
            _ => string.Empty
        };
    }

    private static string ResolveUpdateBlockSummary(string updateBlock, string lastUpdateBlock)
    {
        var values = new[] { updateBlock, lastUpdateBlock }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (values.Length == 0)
        {
            return string.Empty;
        }

        if (values.Any(value => value.Contains("ReadyToReboot", StringComparison.OrdinalIgnoreCase)))
        {
            return "Ready to reboot";
        }

        if (values.Any(value => value.Contains("UserEngaged", StringComparison.OrdinalIgnoreCase)))
        {
            return "User engaged, update action delayed";
        }

        return string.Join(" / ", values);
    }

    private static string FormatActionTagsSummary(string rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return string.Empty;
        }

        var groups = rawValue
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(token => token.Split('@', 2)[0].Trim())
            .Where(prefix => !string.IsNullOrWhiteSpace(prefix))
            .GroupBy(prefix => prefix, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => $"{group.Key}: {group.Count().ToString(CultureInfo.InvariantCulture)}")
            .ToArray();

        return groups.Length == 0
            ? rawValue
            : $"{rawValue.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length.ToString(CultureInfo.InvariantCulture)} tag(s), {string.Join(", ", groups)}";
    }

    private static string FormatByteSize(long? bytes)
    {
        if (!bytes.HasValue)
        {
            return string.Empty;
        }

        const double oneKb = 1024d;
        const double oneMb = oneKb * 1024d;
        const double oneGb = oneMb * 1024d;
        var value = bytes.Value;
        return value switch
        {
            >= (long)oneGb => $"{value / oneGb:0.##} GB",
            >= (long)oneMb => $"{value / oneMb:0.##} MB",
            >= (long)oneKb => $"{value / oneKb:0.##} KB",
            _ => $"{value.ToString(CultureInfo.InvariantCulture)} B"
        };
    }

    private sealed record TitleResolution(string Title, string Source);

    private sealed record DeadlineAnalysis(
        DateTime? InstallDeadline,
        DateTime? RebootDeadline,
        string ConfidenceText,
        string Explanation);

    private sealed record SchedulingAnalysis(string Summary, string Details);
}
