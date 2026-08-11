namespace WindowsClientCenter.Plugins.WindowsUpdateAgent.Models.UsoStore;

public sealed class ProviderScanStatus
{
    public required string ProviderId { get; init; }

    public required string ScanAttemptTimeRaw { get; init; }

    public DateTime? ScanAttemptTimeLocal { get; init; }

    public required string ScanAttemptTimeDisplay { get; init; }

    public required string ScanTimeRaw { get; init; }

    public DateTime? ScanTimeLocal { get; init; }

    public required string ScanTimeDisplay { get; init; }

    public required string ScanErrorRaw { get; init; }

    public long? ScanError { get; init; }

    public required string ScanErrorInteractiveRaw { get; init; }

    public bool? ScanErrorInteractive { get; init; }

    public required string ScanErrorTimeRaw { get; init; }

    public DateTime? ScanErrorTimeLocal { get; init; }

    public required string ScanErrorTimeDisplay { get; init; }

    public required string ScanFailuresSinceLastSuccessRaw { get; init; }

    public long? ScanFailuresSinceLastSuccess { get; init; }

    public required string ScanSummaryTimeRaw { get; init; }

    public DateTime? ScanSummaryTimeLocal { get; init; }

    public required string ScanSummaryTimeDisplay { get; init; }

    public required string ScanTags { get; init; }

    public required string ScanCache { get; init; }

    public required string LastScanStatus { get; init; }

    public bool AttentionRequired { get; init; }

    public required string HeuristicExplanation { get; init; }

    public required string SearchText { get; init; }
}
