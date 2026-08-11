namespace WindowsClientCenter.Plugins.WindowsUpdateAgent.Models.UsoStore;

public sealed class DashboardSummary
{
    public required string DatabasePath { get; init; }

    public DateTime GeneratedAtLocal { get; init; }

    public required IReadOnlyList<DashboardStatusCard> Cards { get; init; }

    public required string AttentionSummary { get; init; }
}
