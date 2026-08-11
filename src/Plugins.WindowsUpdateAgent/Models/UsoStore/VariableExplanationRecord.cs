namespace WindowsClientCenter.Plugins.WindowsUpdateAgent.Models.UsoStore;

public sealed class VariableExplanationRecord
{
    public required string Key { get; init; }

    public required string RawValue { get; init; }

    public required string ParsedValue { get; init; }

    public required string TypeLabel { get; init; }

    public required string Category { get; init; }

    public required string SuggestedMeaning { get; init; }

    public required string SuggestedEffect { get; init; }

    public required string EvidenceBasis { get; init; }

    public required ConfidenceLevel ConfidenceLevel { get; init; }

    public required string ConfidenceText { get; init; }

    public required string Notes { get; init; }

    public required string CorrelatedEvents { get; init; }

    public required string SearchText { get; init; }
}
