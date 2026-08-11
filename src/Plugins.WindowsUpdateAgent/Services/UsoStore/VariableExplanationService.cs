using WindowsClientCenter.Plugins.WindowsUpdateAgent.Models.UsoStore;

namespace WindowsClientCenter.Plugins.WindowsUpdateAgent.Services.UsoStore;

public sealed class VariableExplanationService
{
    public IReadOnlyList<VariableExplanationRecord> Build(
        IReadOnlyList<VariableRecord> variables,
        IReadOnlyList<UpdateLifecycleRecord> lifecycleRecords,
        IReadOnlyList<DowntimeEstimateRecord> downtimeRecords,
        IReadOnlyList<RebootTimelineEvent> timelineEvents)
    {
        return variables
            .Select(variable => BuildExplanation(variable, lifecycleRecords, downtimeRecords, timelineEvents))
            .OrderBy(record => record.Key, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static VariableExplanationRecord BuildExplanation(
        VariableRecord variable,
        IReadOnlyList<UpdateLifecycleRecord> lifecycleRecords,
        IReadOnlyList<DowntimeEstimateRecord> downtimeRecords,
        IReadOnlyList<RebootTimelineEvent> timelineEvents)
    {
        var evidence = new List<string> { "Naming convention" };
        if (variable.Type == 3)
        {
            evidence.Add("Type semantics");
        }

        var category = Classify(variable.Key);
        var (meaning, effect, confidence, notes) = DescribeVariable(variable.Key, variable.RawValue);

        var correlations = FindCorrelations(variable, lifecycleRecords, downtimeRecords, timelineEvents);
        if (!string.IsNullOrWhiteSpace(correlations))
        {
            evidence.Add(variable.Key.Contains("History", StringComparison.OrdinalIgnoreCase)
                ? "Correlation with UX notification history"
                : "Correlation with update lifecycle");
        }

        if (variable.Key.Contains("Downtime", StringComparison.OrdinalIgnoreCase))
        {
            evidence.Add("Correlation with downtime history");
        }

        return new VariableExplanationRecord
        {
            Key = variable.Key,
            RawValue = variable.RawValue,
            ParsedValue = variable.ParsedValue,
            TypeLabel = variable.TypeLabel,
            Category = category,
            SuggestedMeaning = meaning,
            SuggestedEffect = effect,
            EvidenceBasis = string.Join(", ", evidence.Distinct(StringComparer.OrdinalIgnoreCase)),
            ConfidenceLevel = confidence,
            ConfidenceText = confidence.ToString(),
            Notes = notes,
            CorrelatedEvents = string.IsNullOrWhiteSpace(correlations) ? "None" : correlations,
            SearchText = string.Join(" ", variable.Key, variable.RawValue, variable.ParsedValue, category, meaning, effect, notes, correlations)
        };
    }

    private static string FindCorrelations(
        VariableRecord variable,
        IReadOnlyList<UpdateLifecycleRecord> lifecycleRecords,
        IReadOnlyList<DowntimeEstimateRecord> downtimeRecords,
        IReadOnlyList<RebootTimelineEvent> timelineEvents)
    {
        var matches = new List<string>();
        if (variable.ParsedDateTimeLocal.HasValue)
        {
            var referenceTime = variable.ParsedDateTimeLocal.Value;
            var timelineMatch = timelineEvents
                .Where(eventRecord => eventRecord.TimestampLocal.HasValue && Math.Abs((eventRecord.TimestampLocal.Value - referenceTime).TotalMinutes) <= 30)
                .Where(eventRecord => !string.Equals(eventRecord.Source, "VARIABLES", StringComparison.OrdinalIgnoreCase))
                .Take(3)
                .Select(eventRecord => $"{eventRecord.Timestamp} {eventRecord.Summary}");
            matches.AddRange(timelineMatch);
        }

        if (variable.Key.Contains("History", StringComparison.OrdinalIgnoreCase))
        {
            var updateMatch = lifecycleRecords
                .Where(record => record.WasRebootRequired == true)
                .Take(2)
                .Select(record => record.Title);
            matches.AddRange(updateMatch);
        }

        if (variable.Key.Contains("Downtime", StringComparison.OrdinalIgnoreCase))
        {
            matches.AddRange(downtimeRecords.Take(2).Select(record => $"{record.Timestamp} {record.LikelyUpdateComposition}"));
        }

        return string.Join(" | ", matches.Where(match => !string.IsNullOrWhiteSpace(match)).Distinct(StringComparer.OrdinalIgnoreCase).Take(3));
    }

    private static string Classify(string key)
    {
        return key switch
        {
            _ when key.Contains("Deadline", StringComparison.OrdinalIgnoreCase) => "Deadline",
            _ when key.Contains("UserScheduled", StringComparison.OrdinalIgnoreCase) ||
                   key.Contains("UserConfirmed", StringComparison.OrdinalIgnoreCase) ||
                   key.Contains("UserInitiated", StringComparison.OrdinalIgnoreCase) ||
                   key.Contains("UserSchedule", StringComparison.OrdinalIgnoreCase) => "User Scheduling",
            _ when key.Contains("Policy", StringComparison.OrdinalIgnoreCase) => "Policy Scheduling",
            _ when key.Contains("Notification", StringComparison.OrdinalIgnoreCase) ||
                   key.Contains("DisplayedTime", StringComparison.OrdinalIgnoreCase) ||
                   key.Contains("DismissedTime", StringComparison.OrdinalIgnoreCase) => "Notification UX",
            _ when key.Contains("ActiveHours", StringComparison.OrdinalIgnoreCase) => "Active Hours",
            _ when key.Contains("Scheduled", StringComparison.OrdinalIgnoreCase) ||
                   key.Contains("Scheduler", StringComparison.OrdinalIgnoreCase) ||
                   key.Contains("LastRun", StringComparison.OrdinalIgnoreCase) => "Scheduler",
            _ when key.Contains("StorageReserve", StringComparison.OrdinalIgnoreCase) ||
                   key.Contains("Storage", StringComparison.OrdinalIgnoreCase) => "Storage",
            _ when key.Contains("Downtime", StringComparison.OrdinalIgnoreCase) => "Downtime Estimation",
            _ when key.Contains("History", StringComparison.OrdinalIgnoreCase) => "History",
            _ when key.Contains("Reboot", StringComparison.OrdinalIgnoreCase) => "Reboot",
            _ when key.Contains("UpToDate", StringComparison.OrdinalIgnoreCase) ||
                   key.Contains("Validation", StringComparison.OrdinalIgnoreCase) => "Health/Status",
            _ => "Unknown"
        };
    }

    private static (string Meaning, string Effect, ConfidenceLevel Confidence, string Notes) DescribeVariable(string key, string rawValue)
    {
        return key switch
        {
            "UxDeadlineTime" => ("Likely restart deadline timestamp.", "Indicates when the device should stop deferring the pending restart.", ConfidenceLevel.High, "Direct naming match; still treated as a heuristic because Windows does not document every internal key."),
            "UXDevicePastDeadline" => ("Boolean-like deadline state.", "When true, the device appears to be beyond its restart deadline.", ConfidenceLevel.High, "Raw flag preserved."),
            "UxAutoScheduledRebootTime" => ("Automatic reboot schedule.", "Shows the current automatic reboot target time.", ConfidenceLevel.High, "Direct scheduler naming."),
            "UxAutoScheduledRebootTimeByPolicy" => ("Policy-derived automatic reboot schedule.", "Shows when policy appears to have scheduled an automatic reboot.", ConfidenceLevel.High, "Naming and scheduler context align."),
            "UxUserScheduledRebootTime" => ("User-selected reboot time.", "Indicates when the user scheduled a restart.", ConfidenceLevel.High, "Direct naming match."),
            "UxUserConfirmedRebootTime" => ("User-confirmed reboot time.", "Likely records the confirmed restart target after a UX interaction.", ConfidenceLevel.High, "Direct naming match."),
            "UxUserInitiatedRebootTime" => ("User-initiated reboot time.", "Likely records when the restart was started manually.", ConfidenceLevel.High, "Direct naming match."),
            "UxNextScheduledRunTime" => ("Next internal Windows Update scheduler run.", "Helps explain upcoming orchestration work.", ConfidenceLevel.High, "Direct naming match."),
            "UxNextScheduledWakeTime" => ("Wake timer timestamp.", "Likely the next wake-up intended for update activity.", ConfidenceLevel.High, "Direct naming match."),
            "UxLastRebootNotificationDisplayed" => ("Last reboot notification UX identifier.", "Indicates which notification template was last shown.", ConfidenceLevel.High, "String resembles a Windows Update UX scenario identifier."),
            "UxNotificationTimeoutInMins" => ("Notification timeout value.", "Likely affects how long reboot-related UX stays active.", ConfidenceLevel.High, "Naming is explicit."),
            "UXRebootReasonHistory" => ("Serialized reboot reason history.", "Supports historical interpretation of auto, interactive, or user-scheduled restarts.", ConfidenceLevel.High, "Observed JSON array format."),
            "UXRebootRecognitionTimeHistory" => ("Serialized reboot recognition timestamps.", "Shows when reboot need was recognized across historical cycles.", ConfidenceLevel.High, "Observed JSON array of Unix epoch milliseconds."),
            "UXRebootTimeHistory" => ("Serialized reboot execution history.", "Shows historical reboot completion times or reboot timestamps.", ConfidenceLevel.High, "Observed JSON array of Unix epoch milliseconds; actual semantics still treated carefully."),
            "UxLastRunAttention" => ("Internal attention/state summary.", "Useful for surfacing states such as ReadyToReboot.", ConfidenceLevel.Medium, $"Sample raw value '{rawValue}' is preserved as-is."),
            "UxLastSmartSchedulerValidationResult" => ("Internal scheduler validation result.", "May explain whether the scheduler accepted the current reboot plan.", ConfidenceLevel.Medium, "Meaning is inferred from naming only."),
            "RebootDowntimeEstimateOriginalHigh" or "RebootDowntimeEstimateOriginalLow" => ("Initial reboot downtime estimate.", "Helps compare predicted restart duration with actual downtime history.", ConfidenceLevel.High, "Naming aligns with downtime estimation."),
            "StorageReserveClearedOnFirstUse" or "UpdateUsingStorageReserve" => ("Storage reserve signal.", "May indicate whether Windows used reserved storage during update processing.", ConfidenceLevel.Medium, "Naming aligns with official Windows Update concepts."),
            _ when key.StartsWith("UxReboot_", StringComparison.OrdinalIgnoreCase) => ("Reboot UX engagement marker.", "Likely records notification display, dismissal, or last action for a reboot UX template.", ConfidenceLevel.Medium, "The specific template names are internal and not fully documented."),
            _ when key.StartsWith("UxAutoActiveHours", StringComparison.OrdinalIgnoreCase) => ("Automatic active hours value.", "Helps explain when automatic restart should avoid active user hours.", ConfidenceLevel.High, "Naming aligns with Windows Update active hours."),
            _ when key.StartsWith("SBC", StringComparison.OrdinalIgnoreCase) => ("Internal scheduler or servicing signal.", "Raw value preserved because meaning is not established from this sample alone.", ConfidenceLevel.Low, "No strong correlation was found in the sample database."),
            _ => ("Internal Windows Update / USO variable.", "Raw value is preserved and any interpretation should be treated as heuristic.", ConfidenceLevel.Unknown, "The key is not clearly documented in public Windows Update material.")
        };
    }
}
