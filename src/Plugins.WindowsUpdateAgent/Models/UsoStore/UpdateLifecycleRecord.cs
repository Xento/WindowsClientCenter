namespace WindowsClientCenter.Plugins.WindowsUpdateAgent.Models.UsoStore;

public sealed class UpdateLifecycleRecord
{
    public required string ProviderId { get; init; }

    public required string UpdateId { get; init; }

    public required string Title { get; init; }

    public required string ResolvedTitleSource { get; init; }

    public required string Description { get; init; }

    public required string HistoryCategory { get; init; }

    public required string CompletedTime { get; init; }

    public DateTime? CompletedTimeLocal { get; init; }

    public bool? WasRebootRequired { get; init; }

    public required string DiscoveryTime { get; init; }

    public DateTime? DiscoveryTimeLocal { get; init; }

    public long? QueueNumber { get; init; }

    public required string QueueNumberDisplay { get; init; }

    public bool? Approved { get; init; }

    public required string ApprovedTime { get; init; }

    public DateTime? ApprovedTimeLocal { get; init; }

    public required string UpdateAttempted { get; init; }

    public DateTime? UpdateAttemptedLocal { get; init; }

    public required string UpdateActionDelayCount { get; init; }

    public required string UpdateActionDelayTime { get; init; }

    public DateTime? UpdateActionDelayTimeLocal { get; init; }

    public required string SchedulingSummary { get; init; }

    public required string SchedulingDetails { get; init; }

    public required string DownloadActionTime { get; init; }

    public DateTime? DownloadActionTimeLocal { get; init; }

    public required string DownloadActionResult { get; init; }

    public required string InstallActionTime { get; init; }

    public DateTime? InstallActionTimeLocal { get; init; }

    public required string InstallActionResult { get; init; }

    public required string RebootRequiredTime { get; init; }

    public DateTime? RebootRequiredTimeLocal { get; init; }

    public required string RebootRecognitionTime { get; init; }

    public DateTime? RebootRecognitionTimeLocal { get; init; }

    public required string ProbableInstallDeadline { get; init; }

    public DateTime? ProbableInstallDeadlineLocal { get; init; }

    public required string ProbableRebootDeadline { get; init; }

    public DateTime? ProbableRebootDeadlineLocal { get; init; }

    public required string DeadlineConfidenceText { get; init; }

    public required string DeadlineExplanation { get; init; }

    public required string UpdateBlock { get; init; }

    public required string LastUpdateBlock { get; init; }

    public required string UpdateBlockSummary { get; init; }

    public required string UpdateBlockTime { get; init; }

    public DateTime? UpdateBlockTimeLocal { get; init; }

    public required string LastUpdateBlockTime { get; init; }

    public DateTime? LastUpdateBlockTimeLocal { get; init; }

    public long? DownloadSizeBytes { get; init; }

    public required string DownloadSizeDisplay { get; init; }

    public bool? IsIpu { get; init; }

    public required string IsIpuDisplay { get; init; }

    public bool? WorkBit { get; init; }

    public required string WorkBitDisplay { get; init; }

    public required string CorrelationVector { get; init; }

    public required string ActionTags { get; init; }

    public required string ActionTagsSummary { get; init; }

    public required string Metadata { get; init; }

    public required string ImportantUpdateProperties { get; init; }

    public required string RawUpdatePropertiesJson { get; init; }

    public required string MoreInfoUrl { get; init; }

    public required string StateSummary { get; init; }

    public required string SearchText { get; init; }
}
