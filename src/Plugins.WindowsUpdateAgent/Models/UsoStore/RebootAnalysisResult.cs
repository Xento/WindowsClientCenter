namespace WindowsClientCenter.Plugins.WindowsUpdateAgent.Models.UsoStore;

public sealed class RebootAnalysisResult
{
    public required RebootSummary RebootSummary { get; init; }

    public required DashboardSummary DashboardSummary { get; init; }

    public required IReadOnlyList<RebootTimelineEvent> TimelineEvents { get; init; }

    public required IReadOnlyList<RebootHistoryRecord> RebootHistory { get; init; }
}
