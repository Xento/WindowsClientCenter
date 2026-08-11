namespace WindowsClientCenter.Plugins.WindowsUpdateAgent.Models.UsoStore;

public sealed class UsoDatabaseSnapshot
{
    public required string DatabasePath { get; init; }

    public required IReadOnlyList<RawTableInfo> Tables { get; init; }

    public required IReadOnlyList<VariableRecord> Variables { get; init; }

    public required IReadOnlyList<UsoProviderPropertyRecord> ProviderProperties { get; init; }

    public required IReadOnlyList<UsoUpdatePropertyRecord> UpdateProperties { get; init; }

    public required IReadOnlyList<UsoCompletedUpdateRecord> CompletedUpdates { get; init; }

    public required IReadOnlyList<UsoActionRecord> ActionRecords { get; init; }

    public required IReadOnlyList<UsoDowntimeHistoryRecord> DowntimeHistory { get; init; }
}
