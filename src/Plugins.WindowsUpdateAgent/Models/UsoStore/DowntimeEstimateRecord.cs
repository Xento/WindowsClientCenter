namespace WindowsClientCenter.Plugins.WindowsUpdateAgent.Models.UsoStore;

public sealed class DowntimeEstimateRecord
{
    public required string UniqueId { get; init; }

    public required string Timestamp { get; init; }

    public DateTime? TimestampLocal { get; init; }

    public long? EstimatedTimeMinutes { get; init; }

    public long? EstimatedTimeHighMinutes { get; init; }

    public long? ActualLabel { get; init; }

    public long? ActualSeconds { get; init; }

    public long DriverCount { get; init; }

    public long DriverSizeBytes { get; init; }

    public long FeatureUpdateCount { get; init; }

    public long FeatureUpdateSizeBytes { get; init; }

    public long OtherCount { get; init; }

    public long OtherSizeBytes { get; init; }

    public long QualityUpdateCount { get; init; }

    public long QualityUpdateSizeBytes { get; init; }

    public required string LikelyUpdateComposition { get; init; }

    public required string EstimateVsActualSummary { get; init; }

    public required string RawMetadataJson { get; init; }

    public required string SearchText { get; init; }
}
