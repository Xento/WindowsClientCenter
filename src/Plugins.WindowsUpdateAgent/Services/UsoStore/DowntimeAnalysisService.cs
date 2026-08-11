using System.Text.Json;
using WindowsClientCenter.Plugins.WindowsUpdateAgent.Models.UsoStore;

namespace WindowsClientCenter.Plugins.WindowsUpdateAgent.Services.UsoStore;

public sealed class DowntimeAnalysisService
{
    private readonly TimestampParser _timestampParser;

    public DowntimeAnalysisService(TimestampParser timestampParser)
    {
        _timestampParser = timestampParser;
    }

    public IReadOnlyList<DowntimeEstimateRecord> Build(IReadOnlyList<UsoDowntimeHistoryRecord> downtimeHistory)
    {
        return downtimeHistory
            .Select(BuildRecord)
            .OrderByDescending(record => record.TimestampLocal ?? DateTime.MinValue)
            .ToArray();
    }

    private DowntimeEstimateRecord BuildRecord(UsoDowntimeHistoryRecord rawRecord)
    {
        var metadata = ParseMetadata(rawRecord.UpdateMetadata);
        var timestamp = _timestampParser.ParseFlexibleDateTime(rawRecord.TimestampRaw);
        var composition = $"Driver {metadata.DriverCount}, FU {metadata.FeatureCount}, Other {metadata.OtherCount}, Quality {metadata.QualityCount}";

        return new DowntimeEstimateRecord
        {
            UniqueId = rawRecord.UniqueId,
            Timestamp = _timestampParser.FormatDateTime(timestamp),
            TimestampLocal = timestamp,
            EstimatedTimeMinutes = rawRecord.EstimatedTime,
            EstimatedTimeHighMinutes = rawRecord.EstimatedTimeHigh,
            ActualLabel = rawRecord.RealLabel,
            ActualSeconds = rawRecord.RealLabelSeconds,
            DriverCount = metadata.DriverCount,
            DriverSizeBytes = metadata.DriverSizeBytes,
            FeatureUpdateCount = metadata.FeatureCount,
            FeatureUpdateSizeBytes = metadata.FeatureSizeBytes,
            OtherCount = metadata.OtherCount,
            OtherSizeBytes = metadata.OtherSizeBytes,
            QualityUpdateCount = metadata.QualityCount,
            QualityUpdateSizeBytes = metadata.QualitySizeBytes,
            LikelyUpdateComposition = composition,
            EstimateVsActualSummary = BuildEstimateVsActualSummary(rawRecord.EstimatedTime, rawRecord.EstimatedTimeHigh, rawRecord.RealLabelSeconds),
            RawMetadataJson = rawRecord.UpdateMetadata,
            SearchText = string.Join(" ", rawRecord.UniqueId, composition, rawRecord.UpdateMetadata)
        };
    }

    private static string BuildEstimateVsActualSummary(long? lowMinutes, long? highMinutes, long? actualSeconds)
    {
        if (!lowMinutes.HasValue && !highMinutes.HasValue)
        {
            return actualSeconds.HasValue
                ? $"Actual downtime recorded: {actualSeconds.Value} seconds."
                : "No estimate or actual downtime label available.";
        }

        if (!actualSeconds.HasValue)
        {
            return $"Estimated downtime range: {lowMinutes?.ToString() ?? "?"}-{highMinutes?.ToString() ?? "?"} minute(s); no actual downtime label recorded.";
        }

        var actualMinutes = actualSeconds.Value / 60d;
        if (lowMinutes.HasValue && highMinutes.HasValue && actualMinutes >= lowMinutes.Value && actualMinutes <= highMinutes.Value)
        {
            return $"Actual downtime {actualSeconds.Value} seconds fell within the estimated range.";
        }

        return $"Actual downtime {actualSeconds.Value} seconds differed from the estimated {lowMinutes?.ToString() ?? "?"}-{highMinutes?.ToString() ?? "?"} minute range.";
    }

    private static ParsedDowntimeMetadata ParseMetadata(string? rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return ParsedDowntimeMetadata.Empty;
        }

        try
        {
            using var document = JsonDocument.Parse(rawJson);
            return new ParsedDowntimeMetadata(
                DriverCount: GetLong(document.RootElement, "DriverInfo", "Num"),
                DriverSizeBytes: GetLong(document.RootElement, "DriverInfo", "Size"),
                FeatureCount: GetLong(document.RootElement, "FUInfo", "Num"),
                FeatureSizeBytes: GetLong(document.RootElement, "FUInfo", "Size"),
                OtherCount: GetLong(document.RootElement, "OtherInfo", "Num"),
                OtherSizeBytes: GetLong(document.RootElement, "OtherInfo", "Size"),
                QualityCount: GetLong(document.RootElement, "QUInfo", "Num"),
                QualitySizeBytes: GetLong(document.RootElement, "QUInfo", "Size"));
        }
        catch (JsonException)
        {
            return ParsedDowntimeMetadata.Empty;
        }
    }

    private static long GetLong(JsonElement root, string sectionName, string propertyName)
    {
        if (!root.TryGetProperty(sectionName, out var section) ||
            !section.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.Number)
        {
            return 0;
        }

        return property.TryGetInt64(out var value) ? value : 0;
    }

    private sealed record ParsedDowntimeMetadata(
        long DriverCount,
        long DriverSizeBytes,
        long FeatureCount,
        long FeatureSizeBytes,
        long OtherCount,
        long OtherSizeBytes,
        long QualityCount,
        long QualitySizeBytes)
    {
        public static ParsedDowntimeMetadata Empty { get; } = new(0, 0, 0, 0, 0, 0, 0, 0);
    }
}
