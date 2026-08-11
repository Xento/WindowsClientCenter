namespace WindowsClientCenter.Intune.Services.Models;

public sealed record DeliveryOptimizationSourceStat(
    string Source,
    long Bytes,
    int TransferCount = 0);
