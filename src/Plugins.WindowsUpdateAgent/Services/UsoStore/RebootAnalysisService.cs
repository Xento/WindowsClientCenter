using WindowsClientCenter.Plugins.WindowsUpdateAgent.Models.UsoStore;

namespace WindowsClientCenter.Plugins.WindowsUpdateAgent.Services.UsoStore;

public sealed class RebootAnalysisService
{
    private static readonly string[] RelevantVariableKeys =
    [
        "UxAutoScheduledRebootTime",
        "UxAutoScheduledRebootTimeByPolicy",
        "UxUserScheduledRebootTime",
        "UxUserConfirmedRebootTime",
        "UxUserInitiatedRebootTime",
        "UxDeadlineTime",
        "UXDevicePastDeadline",
        "UxNextScheduledRunTime",
        "UxNextScheduledWakeTime",
        "UxUpToDateStatus",
        "UxLastRebootNotificationDisplayed",
        "UxLastRunAttention",
        "UxLastSmartSchedulerValidationResult",
        "UxLastSmartSchedulerValidationResultTimestamp",
        "UxReboot_DeadlineEngagement_MultiDay_AutoEnabled-NonIntrusiveDisplayedTime",
        "UxReboot_DeadlineEngagement_MultiDay_AutoEnabled-NonIntrusiveDismissedTime",
        "UxReboot_DeadlineEngagement_MultiDay_AutoEnabled-NonIntrusiveLastAction",
        "UxReboot_PolicyDeadlineRebootImminent-IntrusiveDisplayedTime",
        "UxReboot_PolicyDeadlineRebootImminent-IntrusiveDismissedTime",
        "UxReboot_PolicyDeadlineRebootImminent-IntrusiveLastAction",
        "UXRebootHistoryVersion",
        "UXRebootReasonHistory",
        "UXRebootRecognitionTimeHistory",
        "UXRebootTimeHistory"
    ];

    private readonly TimestampParser _timestampParser;

    public RebootAnalysisService(TimestampParser timestampParser)
    {
        _timestampParser = timestampParser;
    }

    public RebootAnalysisResult Build(
        UsoDatabaseSnapshot snapshot,
        IReadOnlyList<UpdateLifecycleRecord> lifecycleRecords,
        IReadOnlyList<ProviderScanStatus> scanStatuses,
        IReadOnlyList<DowntimeEstimateRecord> downtimeRecords)
    {
        var variables = snapshot.Variables.ToDictionary(record => record.Key, record => record, StringComparer.OrdinalIgnoreCase);
        var lastActualRebootTime = ParseHistoryTimes(variables, "UXRebootTimeHistory").LastOrDefault(time => time.HasValue);
        var latestRebootSensitiveUpdate = lifecycleRecords
            .Where(record => record.WasRebootRequired == true || record.RebootRequiredTimeLocal.HasValue || record.RebootRecognitionTimeLocal.HasValue)
            .OrderByDescending(record => record.RebootRequiredTimeLocal ?? record.RebootRecognitionTimeLocal ?? record.CompletedTimeLocal ?? DateTime.MinValue)
            .FirstOrDefault();

        var rebootPendingLikely = latestRebootSensitiveUpdate is not null &&
                                  (!lastActualRebootTime.HasValue || (latestRebootSensitiveUpdate.RebootRequiredTimeLocal ?? latestRebootSensitiveUpdate.RebootRecognitionTimeLocal) > lastActualRebootTime);

        var summary = new RebootSummary
        {
            RebootPendingLikely = rebootPendingLikely,
            ConfidenceLevel = rebootPendingLikely ? ConfidenceLevel.High : ConfidenceLevel.Medium,
            ConfidenceText = rebootPendingLikely ? ConfidenceLevel.High.ToString() : ConfidenceLevel.Medium.ToString(),
            AttentionSummary = BuildAttentionSummary(rebootPendingLikely, scanStatuses, latestRebootSensitiveUpdate),
            ProviderScanHealthSummary = BuildScanHealthSummary(scanStatuses),
            CurrentUpdateId = latestRebootSensitiveUpdate?.UpdateId,
            CurrentUpdateTitle = latestRebootSensitiveUpdate?.Title,
            CurrentUpdateStateSummary = latestRebootSensitiveUpdate?.StateSummary,
            AutoScheduledRebootTimeLocal = GetDateTime(variables, "UxAutoScheduledRebootTime"),
            PolicyScheduledRebootTimeLocal = GetDateTime(variables, "UxAutoScheduledRebootTimeByPolicy"),
            UserScheduledRebootTimeLocal = GetDateTime(variables, "UxUserScheduledRebootTime"),
            UserConfirmedRebootTimeLocal = GetDateTime(variables, "UxUserConfirmedRebootTime"),
            UserInitiatedRebootTimeLocal = GetDateTime(variables, "UxUserInitiatedRebootTime"),
            DeadlineTimeLocal = GetDateTime(variables, "UxDeadlineTime"),
            DevicePastDeadline = GetBool(variables, "UXDevicePastDeadline"),
            NextScheduledRunTimeLocal = GetDateTime(variables, "UxNextScheduledRunTime"),
            NextScheduledWakeTimeLocal = GetDateTime(variables, "UxNextScheduledWakeTime"),
            UpToDateStatus = GetRaw(variables, "UxUpToDateStatus"),
            LastRebootNotificationDisplayed = GetRaw(variables, "UxLastRebootNotificationDisplayed"),
            LastRunAttention = GetRaw(variables, "UxLastRunAttention"),
            LastSmartSchedulerValidationResult = GetRaw(variables, "UxLastSmartSchedulerValidationResult"),
            LastActualRebootTimeLocal = lastActualRebootTime
        };

        var timeline = BuildTimeline(variables, lifecycleRecords, snapshot.ActionRecords, snapshot.CompletedUpdates, downtimeRecords);
        var rebootHistory = BuildRebootHistory(variables, lifecycleRecords, downtimeRecords);
        var dashboard = new DashboardSummary
        {
            DatabasePath = snapshot.DatabasePath,
            GeneratedAtLocal = DateTime.Now,
            AttentionSummary = summary.AttentionSummary,
            Cards = BuildDashboardCards(summary)
        };

        return new RebootAnalysisResult
        {
            RebootSummary = summary,
            DashboardSummary = dashboard,
            TimelineEvents = timeline,
            RebootHistory = rebootHistory
        };
    }

    private IReadOnlyList<DashboardStatusCard> BuildDashboardCards(RebootSummary summary)
    {
        return
        [
            CreateCard("Reboot pending likely", summary.RebootPendingLikely ? "Yes" : "No", summary.AttentionSummary, summary.RebootPendingLikely ? StatusLevel.Warning : StatusLevel.Healthy),
            CreateCard("Auto scheduled reboot time", _timestampParser.FormatDateTime(summary.AutoScheduledRebootTimeLocal), "Automatic reboot schedule from VARIABLES.", summary.AutoScheduledRebootTimeLocal.HasValue ? StatusLevel.Warning : StatusLevel.Neutral),
            CreateCard("Policy scheduled reboot time", _timestampParser.FormatDateTime(summary.PolicyScheduledRebootTimeLocal), "Policy-derived automatic reboot schedule.", summary.PolicyScheduledRebootTimeLocal.HasValue ? StatusLevel.Warning : StatusLevel.Neutral),
            CreateCard("User scheduled reboot time", _timestampParser.FormatDateTime(summary.UserScheduledRebootTimeLocal), "User-selected reboot target.", summary.UserScheduledRebootTimeLocal.HasValue ? StatusLevel.Warning : StatusLevel.Neutral),
            CreateCard("User confirmed reboot time", _timestampParser.FormatDateTime(summary.UserConfirmedRebootTimeLocal), "User-confirmed reboot plan.", summary.UserConfirmedRebootTimeLocal.HasValue ? StatusLevel.Warning : StatusLevel.Neutral),
            CreateCard("User initiated reboot time", _timestampParser.FormatDateTime(summary.UserInitiatedRebootTimeLocal), "Latest user-initiated reboot marker.", summary.UserInitiatedRebootTimeLocal.HasValue ? StatusLevel.Healthy : StatusLevel.Neutral),
            CreateCard("Deadline time", _timestampParser.FormatDateTime(summary.DeadlineTimeLocal), "Likely restart deadline.", summary.DevicePastDeadline == true ? StatusLevel.Critical : summary.DeadlineTimeLocal.HasValue ? StatusLevel.Warning : StatusLevel.Neutral),
            CreateCard("Device past deadline", summary.DevicePastDeadline == true ? "Yes" : summary.DevicePastDeadline == false ? "No" : "Unknown", "Deadline state derived from UXDevicePastDeadline.", summary.DevicePastDeadline == true ? StatusLevel.Critical : StatusLevel.Healthy),
            CreateCard("Next scheduled run time", _timestampParser.FormatDateTime(summary.NextScheduledRunTimeLocal), "Next Windows Update orchestrator run.", summary.NextScheduledRunTimeLocal.HasValue ? StatusLevel.Healthy : StatusLevel.Neutral),
            CreateCard("Next scheduled wake time", _timestampParser.FormatDateTime(summary.NextScheduledWakeTimeLocal), "Next scheduled wake timer.", summary.NextScheduledWakeTimeLocal.HasValue ? StatusLevel.Healthy : StatusLevel.Neutral),
            CreateCard("Up-to-date status", string.IsNullOrWhiteSpace(summary.UpToDateStatus) ? "Unknown" : summary.UpToDateStatus, "Raw UX up-to-date state.", summary.UpToDateStatus.Contains("Reboot", StringComparison.OrdinalIgnoreCase) ? StatusLevel.Warning : StatusLevel.Healthy),
            CreateCard("Last reboot notification displayed", string.IsNullOrWhiteSpace(summary.LastRebootNotificationDisplayed) ? "Unknown" : summary.LastRebootNotificationDisplayed, "Last reboot-related UX template.", string.IsNullOrWhiteSpace(summary.LastRebootNotificationDisplayed) ? StatusLevel.Neutral : StatusLevel.Warning),
            CreateCard("Last run attention", string.IsNullOrWhiteSpace(summary.LastRunAttention) ? "Unknown" : summary.LastRunAttention, "Scheduler attention summary.", summary.LastRunAttention.Contains("ReadyToReboot", StringComparison.OrdinalIgnoreCase) ? StatusLevel.Critical : StatusLevel.Healthy),
            CreateCard("Last smart scheduler validation result", string.IsNullOrWhiteSpace(summary.LastSmartSchedulerValidationResult) ? "Unknown" : summary.LastSmartSchedulerValidationResult, "Raw internal validation result.", string.IsNullOrWhiteSpace(summary.LastSmartSchedulerValidationResult) ? StatusLevel.Neutral : StatusLevel.Healthy),
            CreateCard("Provider scan health summary", summary.ProviderScanHealthSummary, "Derived from PROVIDERSPROP scan fields.", summary.ProviderScanHealthSummary.Contains("attention", StringComparison.OrdinalIgnoreCase) ? StatusLevel.Critical : StatusLevel.Healthy),
            CreateCard("Current update requiring reboot", string.IsNullOrWhiteSpace(summary.CurrentUpdateTitle) ? "None detected" : summary.CurrentUpdateTitle, string.IsNullOrWhiteSpace(summary.CurrentUpdateStateSummary) ? "No reboot-sensitive update row found." : summary.CurrentUpdateStateSummary, summary.RebootPendingLikely ? StatusLevel.Critical : StatusLevel.Healthy)
        ];
    }

    private IReadOnlyList<RebootTimelineEvent> BuildTimeline(
        IReadOnlyDictionary<string, VariableRecord> variables,
        IReadOnlyList<UpdateLifecycleRecord> lifecycleRecords,
        IReadOnlyList<UsoActionRecord> actionRecords,
        IReadOnlyList<UsoCompletedUpdateRecord> completedUpdates,
        IReadOnlyList<DowntimeEstimateRecord> downtimeRecords)
    {
        var events = new List<RebootTimelineEvent>();

        foreach (var key in RelevantVariableKeys)
        {
            if (!variables.TryGetValue(key, out var variable))
            {
                continue;
            }

            var timestamp = variable.ParsedDateTimeLocal;
            if (!timestamp.HasValue)
            {
                continue;
            }

            events.Add(CreateTimelineEvent(
                timestamp,
                "VARIABLES",
                "Variable",
                $"{variable.Key} = {variable.ParsedValue}",
                $"Raw value preserved: {variable.RawValue}",
                variable.RawValue,
                string.Empty,
                string.Empty,
                key.Contains("History", StringComparison.OrdinalIgnoreCase) ? ConfidenceLevel.Medium : ConfidenceLevel.High));
        }

        foreach (var record in lifecycleRecords)
        {
            AddLifecycleEvent(events, record.DiscoveryTimeLocal, "UpdateLifecycle", "Discovery", $"Update discovered: {record.Title}", record, record.DiscoveryTime);
            AddLifecycleEvent(events, record.ApprovedTimeLocal, "UpdateLifecycle", "Approval", $"Update approved: {record.Title}", record, record.ApprovedTime);
            AddLifecycleEvent(events, record.UpdateAttemptedLocal, "UpdateLifecycle", "Attempt", $"Update attempted: {record.Title}", record, record.UpdateAttempted);
            AddLifecycleEvent(events, record.DownloadActionTimeLocal, "ACTIONRECORDS", "Download", $"Download action: {record.Title}", record, record.DownloadActionResult);
            AddLifecycleEvent(events, record.InstallActionTimeLocal, "ACTIONRECORDS", "Install", $"Install action: {record.Title}", record, record.InstallActionResult);
            AddLifecycleEvent(events, record.RebootRequiredTimeLocal, "UPDATESPROP", "RebootRequired", $"Reboot required: {record.Title}", record, record.RebootRequiredTime);
            AddLifecycleEvent(events, record.RebootRecognitionTimeLocal, "UPDATESPROP", "RebootRecognition", $"Reboot recognized: {record.Title}", record, record.RebootRecognitionTime);
            AddLifecycleEvent(events, record.UpdateBlockTimeLocal, "UPDATESPROP", "UpdateBlock", $"Update block '{record.UpdateBlock}' for {record.Title}", record, record.UpdateBlock);
            AddLifecycleEvent(events, record.LastUpdateBlockTimeLocal, "UPDATESPROP", "LastUpdateBlock", $"Last block '{record.LastUpdateBlock}' for {record.Title}", record, record.LastUpdateBlock);
            AddLifecycleEvent(events, record.CompletedTimeLocal, "COMPLETEDUPDATES", "Completed", $"Update completed: {record.Title}", record, record.StateSummary);
        }

        foreach (var downtime in downtimeRecords)
        {
            events.Add(CreateTimelineEvent(
                downtime.TimestampLocal,
                "DOWNTIMEHISTORY",
                "DowntimeEstimate",
                $"Downtime estimate recorded ({downtime.EstimateVsActualSummary})",
                downtime.LikelyUpdateComposition,
                downtime.RawMetadataJson,
                string.Empty,
                string.Empty,
                downtime.ActualSeconds.HasValue ? ConfidenceLevel.Medium : ConfidenceLevel.Low));
        }

        return events
            .OrderBy(eventRecord => eventRecord.TimestampLocal ?? DateTime.MinValue)
            .ToArray();
    }

    private IReadOnlyList<RebootHistoryRecord> BuildRebootHistory(
        IReadOnlyDictionary<string, VariableRecord> variables,
        IReadOnlyList<UpdateLifecycleRecord> lifecycleRecords,
        IReadOnlyList<DowntimeEstimateRecord> downtimeRecords)
    {
        var reasons = _timestampParser.ParseSerializedStringArray(GetRaw(variables, "UXRebootReasonHistory"));
        var recognitionTimes = ParseHistoryTimes(variables, "UXRebootRecognitionTimeHistory");
        var actualTimes = ParseHistoryTimes(variables, "UXRebootTimeHistory");
        var entryCount = new[] { reasons.Count, recognitionTimes.Count, actualTimes.Count }.Max();

        var records = new List<RebootHistoryRecord>(entryCount);
        for (var index = 0; index < entryCount; index++)
        {
            var reason = GetItemOrDefault(reasons, index) ?? "Unknown";
            var recognition = GetItemOrDefault(recognitionTimes, index);
            var actual = GetItemOrDefault(actualTimes, index);
            var scheduled = ResolveHistoricalScheduledTime(reason, recognition, actual, variables);
            var associatedUpdate = ResolveAssociatedUpdate(recognition ?? actual, lifecycleRecords);
            var confidence = ResolveRebootHistoryConfidence(recognition, actual, scheduled, associatedUpdate);
            var notes = scheduled.HasValue
                ? "Scheduled time is a heuristic derived from the nearest currently stored scheduling variable."
                : "No historical schedule array was found; missing schedule values remain Unknown.";

            var downtimeMatch = ResolveNearestDowntime(recognition ?? actual, downtimeRecords);
            if (!string.IsNullOrWhiteSpace(downtimeMatch))
            {
                notes = $"{notes} Downtime correlation: {downtimeMatch}";
            }

            records.Add(new RebootHistoryRecord
            {
                TimestampLocal = actual ?? recognition ?? scheduled,
                Timestamp = _timestampParser.FormatDateTime(actual ?? recognition ?? scheduled),
                Reason = reason,
                RecognitionTimeLocal = recognition,
                RecognitionTime = _timestampParser.FormatDateTime(recognition),
                ScheduledTimeLocal = scheduled,
                ScheduledTime = _timestampParser.FormatDateTime(scheduled),
                ActualRebootTimeLocal = actual,
                ActualRebootTime = _timestampParser.FormatDateTime(actual),
                AssociatedUpdateTitle = associatedUpdate?.Title ?? "Unknown",
                ConfidenceLevel = confidence,
                ConfidenceText = confidence.ToString(),
                Notes = notes,
                SearchText = string.Join(" ", reason, associatedUpdate?.Title ?? string.Empty, notes)
            });
        }

        return records
            .OrderBy(record => record.TimestampLocal ?? DateTime.MinValue)
            .ToArray();
    }

    private DashboardStatusCard CreateCard(string title, string value, string detail, StatusLevel level)
    {
        return new DashboardStatusCard
        {
            Title = title,
            Value = string.IsNullOrWhiteSpace(value) ? "Unknown" : value,
            Detail = string.IsNullOrWhiteSpace(detail) ? "No additional detail." : detail,
            StatusLevel = level,
            AccentBrush = level switch
            {
                StatusLevel.Healthy => "#1D7D46",
                StatusLevel.Warning => "#A66B00",
                StatusLevel.Critical => "#B42318",
                _ => "#667085"
            }
        };
    }

    private static string BuildAttentionSummary(bool rebootPendingLikely, IReadOnlyList<ProviderScanStatus> scanStatuses, UpdateLifecycleRecord? updateRecord)
    {
        var fragments = new List<string>();
        if (rebootPendingLikely && updateRecord is not null)
        {
            fragments.Add($"Reboot is likely still pending for '{updateRecord.Title}'.");
        }

        var providerIssues = scanStatuses.Where(status => status.AttentionRequired).Select(status => status.ProviderId).ToArray();
        if (providerIssues.Length > 0)
        {
            fragments.Add($"Provider scan attention: {string.Join(", ", providerIssues)}.");
        }

        return fragments.Count == 0
            ? "No immediate reboot or scan attention markers were derived."
            : string.Join(" ", fragments);
    }

    private static string BuildScanHealthSummary(IReadOnlyList<ProviderScanStatus> scanStatuses)
    {
        if (scanStatuses.Count == 0)
        {
            return "No provider scan telemetry found.";
        }

        var issues = scanStatuses.Where(status => status.AttentionRequired).ToArray();
        if (issues.Length == 0)
        {
            return "All provider scan states look healthy.";
        }

        return string.Join("; ", issues.Select(issue => $"{issue.ProviderId}: {issue.HeuristicExplanation}"));
    }

    private void AddLifecycleEvent(
        ICollection<RebootTimelineEvent> events,
        DateTime? timestamp,
        string source,
        string eventType,
        string summary,
        UpdateLifecycleRecord lifecycleRecord,
        string rawValue)
    {
        if (!timestamp.HasValue)
        {
            return;
        }

        events.Add(CreateTimelineEvent(
            timestamp,
            source,
            eventType,
            summary,
            lifecycleRecord.StateSummary,
            rawValue,
            lifecycleRecord.UpdateId,
            lifecycleRecord.Title,
            eventType.Contains("Reboot", StringComparison.OrdinalIgnoreCase) ? ConfidenceLevel.High : ConfidenceLevel.Medium));
    }

    private RebootTimelineEvent CreateTimelineEvent(
        DateTime? timestamp,
        string source,
        string eventType,
        string summary,
        string details,
        string rawValue,
        string associatedUpdateId,
        string associatedUpdateTitle,
        ConfidenceLevel confidence)
    {
        return new RebootTimelineEvent
        {
            TimestampLocal = timestamp,
            Timestamp = _timestampParser.FormatDateTime(timestamp),
            Source = source,
            EventType = eventType,
            Summary = summary,
            Details = details,
            RawValue = rawValue,
            AssociatedUpdateId = associatedUpdateId,
            AssociatedUpdateTitle = associatedUpdateTitle,
            ConfidenceLevel = confidence,
            ConfidenceText = confidence.ToString(),
            SearchText = string.Join(" ", source, eventType, summary, details, rawValue, associatedUpdateTitle, associatedUpdateId)
        };
    }

    private IReadOnlyList<DateTime?> ParseHistoryTimes(IReadOnlyDictionary<string, VariableRecord> variables, string key)
    {
        return _timestampParser.ParseSerializedDateTimeArray(GetRaw(variables, key));
    }

    private DateTime? ResolveHistoricalScheduledTime(
        string reason,
        DateTime? recognition,
        DateTime? actual,
        IReadOnlyDictionary<string, VariableRecord> variables)
    {
        var candidates = reason switch
        {
            "Auto" => new[]
            {
                GetDateTime(variables, "UxAutoScheduledRebootTime"),
                GetDateTime(variables, "UxAutoScheduledRebootTimeByPolicy")
            },
            "UserScheduled" => new[]
            {
                GetDateTime(variables, "UxUserScheduledRebootTime"),
                GetDateTime(variables, "UxUserConfirmedRebootTime")
            },
            "Interactive" => new[]
            {
                GetDateTime(variables, "UxUserInitiatedRebootTime"),
                GetDateTime(variables, "UxUserConfirmedRebootTime")
            },
            _ => new[]
            {
                GetDateTime(variables, "UxUserScheduledRebootTime"),
                GetDateTime(variables, "UxAutoScheduledRebootTime"),
                GetDateTime(variables, "UxAutoScheduledRebootTimeByPolicy")
            }
        };

        var pivot = actual ?? recognition;
        return candidates
            .Where(candidate => candidate.HasValue)
            .Select(candidate => candidate!.Value)
            .OrderBy(candidate => pivot.HasValue ? Math.Abs((candidate - pivot.Value).TotalHours) : double.MaxValue)
            .FirstOrDefault();
    }

    private UpdateLifecycleRecord? ResolveAssociatedUpdate(DateTime? referenceTime, IReadOnlyList<UpdateLifecycleRecord> lifecycleRecords)
    {
        if (!referenceTime.HasValue)
        {
            return lifecycleRecords.FirstOrDefault(record => record.WasRebootRequired == true);
        }

        return lifecycleRecords
            .Where(record => record.RebootRequiredTimeLocal.HasValue || record.RebootRecognitionTimeLocal.HasValue || record.CompletedTimeLocal.HasValue)
            .OrderBy(record => Math.Abs(((record.RebootRecognitionTimeLocal ?? record.RebootRequiredTimeLocal ?? record.CompletedTimeLocal)!.Value - referenceTime.Value).TotalMinutes))
            .FirstOrDefault();
    }

    private string ResolveNearestDowntime(DateTime? referenceTime, IReadOnlyList<DowntimeEstimateRecord> downtimeRecords)
    {
        if (!referenceTime.HasValue)
        {
            return string.Empty;
        }

        var match = downtimeRecords
            .Where(record => record.TimestampLocal.HasValue)
            .OrderBy(record => Math.Abs((record.TimestampLocal!.Value - referenceTime.Value).TotalMinutes))
            .FirstOrDefault();

        if (match is null || !match.TimestampLocal.HasValue || Math.Abs((match.TimestampLocal.Value - referenceTime.Value).TotalHours) > 24)
        {
            return string.Empty;
        }

        return $"{match.Timestamp} {match.EstimateVsActualSummary}";
    }

    private static ConfidenceLevel ResolveRebootHistoryConfidence(DateTime? recognition, DateTime? actual, DateTime? scheduled, UpdateLifecycleRecord? associatedUpdate)
    {
        if (recognition.HasValue && actual.HasValue && associatedUpdate is not null)
        {
            return ConfidenceLevel.High;
        }

        if ((recognition.HasValue || actual.HasValue) && (scheduled.HasValue || associatedUpdate is not null))
        {
            return ConfidenceLevel.Medium;
        }

        if (recognition.HasValue || actual.HasValue)
        {
            return ConfidenceLevel.Low;
        }

        return ConfidenceLevel.Unknown;
    }

    private DateTime? GetDateTime(IReadOnlyDictionary<string, VariableRecord> variables, string key)
    {
        return variables.TryGetValue(key, out var record)
            ? record.ParsedDateTimeLocal
            : null;
    }

    private bool? GetBool(IReadOnlyDictionary<string, VariableRecord> variables, string key)
    {
        return variables.TryGetValue(key, out var record)
            ? record.ParsedBoolean
            : null;
    }

    private static string GetRaw(IReadOnlyDictionary<string, VariableRecord> variables, string key)
    {
        return variables.TryGetValue(key, out var record)
            ? record.RawValue
            : string.Empty;
    }

    private static T? GetItemOrDefault<T>(IReadOnlyList<T> items, int index)
    {
        return index >= 0 && index < items.Count ? items[index] : default;
    }
}
