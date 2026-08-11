namespace WindowsClientCenter.Plugins.WindowsUpdateAgent.Models.UsoStore;

public sealed class RawTableInfo
{
    public required string Name { get; init; }

    public long RowCount { get; init; }

    public bool IsEmpty => RowCount == 0;

    public string DisplayName => $"{Name} ({RowCount})";
}
