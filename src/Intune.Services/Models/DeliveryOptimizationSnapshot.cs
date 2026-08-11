namespace WindowsClientCenter.Intune.Services.Models;

public sealed record DeliveryOptimizationSnapshot(
    bool IsAvailable,
    DateTimeOffset CapturedAtUtc,
    IReadOnlyList<DeliveryOptimizationSourceStat> SourceStats,
    IReadOnlyList<DeliveryOptimizationTransferEntry> Transfers,
    IReadOnlyList<string> Notes,
    bool SupportsTimeRangeFiltering = false,
    DateTimeOffset? DataStartUtc = null,
    DateTimeOffset? DataEndUtc = null,
    IReadOnlyList<NameValueItem>? CurrentMetrics = null,
    IReadOnlyList<NameValueItem>? MonthlyMetrics = null,
    IReadOnlyList<NameValueItem>? Configuration = null,
    IReadOnlyList<DeliveryOptimizationPeerStatus>? PeerStatuses = null,
    IReadOnlyList<DeliveryOptimizationJobStatus>? ActiveJobs = null);
