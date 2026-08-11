namespace WindowsClientCenter.Intune.Services.Models;

public sealed record DeliveryOptimizationTransferEntry(
    DateTimeOffset TimestampUtc,
    string Source,
    long Bytes,
    string Description);
