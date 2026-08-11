namespace WindowsClientCenter.Intune.Services.Models;

public sealed record ImeFlowSummaryEntry(
    string Key,
    string Flow,
    string EntityType,
    string EntityId,
    string PolicyId,
    string SessionId,
    string UserId,
    DateTimeOffset? StartedAt,
    DateTimeOffset? LastSeenAt,
    string LastPhase,
    string Result,
    string ResultCode,
    int EventCount,
    int AttemptCount,
    bool IsComplete,
    bool IsFailed,
    string Summary,
    string LastMessage)
{
    public string FlowDisplay => string.IsNullOrWhiteSpace(Flow) ? "Informational" : Flow;
    public string EntityDisplay => string.IsNullOrWhiteSpace(EntityType) || string.IsNullOrWhiteSpace(EntityId) ? "-" : $"{EntityType} {EntityId}";
    public string RunDisplay => !string.IsNullOrWhiteSpace(SessionId)
        ? $"Session {SessionId}"
        : !string.IsNullOrWhiteSpace(PolicyId)
            ? $"Policy {PolicyId}"
            : "-";

    public string StateDisplay => string.IsNullOrWhiteSpace(LastPhase) ? Result : $"{Result} @ {LastPhase}";
    public string ResultCodeDisplay => string.IsNullOrWhiteSpace(ResultCode) ? "-" : ResultCode;
    public string AttemptDisplay => AttemptCount <= 1 ? "1" : AttemptCount.ToString();
    public string DurationDisplay => StartedAt.HasValue && LastSeenAt.HasValue
        ? (LastSeenAt.Value - StartedAt.Value).ToString(@"hh\:mm\:ss")
        : "-";
}
