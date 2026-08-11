namespace WindowsClientCenter.Plugins.WindowsUpdateAgent.Models.UsoStore;

public sealed class DashboardStatusCard
{
    public required string Title { get; init; }

    public required string Value { get; init; }

    public required string Detail { get; init; }

    public required StatusLevel StatusLevel { get; init; }

    public required string AccentBrush { get; init; }
}
