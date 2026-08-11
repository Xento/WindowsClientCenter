namespace WindowsClientCenter.Plugins.WindowsUpdateAgent.Models.UsoStore;

public sealed class RebootHistoryRecord
{
    public DateTime? TimestampLocal { get; init; }

    public required string Timestamp { get; init; }

    public required string Reason { get; init; }

    public DateTime? RecognitionTimeLocal { get; init; }

    public required string RecognitionTime { get; init; }

    public DateTime? ScheduledTimeLocal { get; init; }

    public required string ScheduledTime { get; init; }

    public DateTime? ActualRebootTimeLocal { get; init; }

    public required string ActualRebootTime { get; init; }

    public required string AssociatedUpdateTitle { get; init; }

    public required string ConfidenceText { get; init; }

    public required ConfidenceLevel ConfidenceLevel { get; init; }

    public required string Notes { get; init; }

    public required string SearchText { get; init; }
}
