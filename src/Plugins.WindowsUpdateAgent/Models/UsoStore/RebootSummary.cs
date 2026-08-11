namespace WindowsClientCenter.Plugins.WindowsUpdateAgent.Models.UsoStore;

public sealed class RebootSummary
{
    public bool RebootPendingLikely { get; init; }

    public ConfidenceLevel ConfidenceLevel { get; init; }

    public required string ConfidenceText { get; init; }

    public required string AttentionSummary { get; init; }

    public required string ProviderScanHealthSummary { get; init; }

    public string? CurrentUpdateId { get; init; }

    public string? CurrentUpdateTitle { get; init; }

    public string? CurrentUpdateStateSummary { get; init; }

    public DateTime? AutoScheduledRebootTimeLocal { get; init; }

    public DateTime? PolicyScheduledRebootTimeLocal { get; init; }

    public DateTime? UserScheduledRebootTimeLocal { get; init; }

    public DateTime? UserConfirmedRebootTimeLocal { get; init; }

    public DateTime? UserInitiatedRebootTimeLocal { get; init; }

    public DateTime? DeadlineTimeLocal { get; init; }

    public bool? DevicePastDeadline { get; init; }

    public DateTime? NextScheduledRunTimeLocal { get; init; }

    public DateTime? NextScheduledWakeTimeLocal { get; init; }

    public string UpToDateStatus { get; init; } = string.Empty;

    public string LastRebootNotificationDisplayed { get; init; } = string.Empty;

    public string LastRunAttention { get; init; } = string.Empty;

    public string LastSmartSchedulerValidationResult { get; init; } = string.Empty;

    public DateTime? LastActualRebootTimeLocal { get; init; }
}
