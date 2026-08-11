namespace WindowsClientCenter.Plugins.WindowsUpdateAgent.Models.UsoStore;

public sealed class RebootTimelineEvent
{
    public DateTime? TimestampLocal { get; init; }

    public required string Timestamp { get; init; }

    public required string Source { get; init; }

    public required string EventType { get; init; }

    public required string Summary { get; init; }

    public required string Details { get; init; }

    public required string RawValue { get; init; }

    public required string AssociatedUpdateTitle { get; init; }

    public required string AssociatedUpdateId { get; init; }

    public required ConfidenceLevel ConfidenceLevel { get; init; }

    public required string ConfidenceText { get; init; }

    public required string SearchText { get; init; }
}
