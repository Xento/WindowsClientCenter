namespace WindowsClientCenter.Plugins.WindowsUpdateAgent.Models.UsoStore;

public sealed class UsoProviderPropertyRecord
{
    public required string ProviderId { get; init; }

    public required string Variable { get; init; }

    public required string Value { get; init; }

    public int Type { get; init; }
}

public sealed class UsoUpdatePropertyRecord
{
    public required string ProviderId { get; init; }

    public required string UpdateId { get; init; }

    public required string Variable { get; init; }

    public required string Value { get; init; }

    public int Type { get; init; }
}

public sealed class UsoCompletedUpdateRecord
{
    public required string ProviderId { get; init; }

    public required string UpdateId { get; init; }

    public required string TimeRaw { get; init; }

    public required string Title { get; init; }

    public required string Description { get; init; }

    public required string MoreInfoUrl { get; init; }

    public required string HistoryCategory { get; init; }

    public int? Uninstall { get; init; }

    public bool? WasRebootRequired { get; init; }

    public int? ForOs { get; init; }

    public required string Metadata { get; init; }
}

public sealed class UsoActionRecord
{
    public required string ProviderId { get; init; }

    public required string UpdateId { get; init; }

    public required string TimeRaw { get; init; }

    public required string Action { get; init; }

    public required string ActionClass { get; init; }

    public int? Result { get; init; }
}

public sealed class UsoDowntimeHistoryRecord
{
    public required string UniqueId { get; init; }

    public long? EstimatedTime { get; init; }

    public long? EstimatedTimeHigh { get; init; }

    public required string TimestampRaw { get; init; }

    public long? RealLabel { get; init; }

    public long? RealLabelSeconds { get; init; }

    public required string UpdateMetadata { get; init; }
}
