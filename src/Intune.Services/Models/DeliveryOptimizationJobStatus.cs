namespace WindowsClientCenter.Intune.Services.Models;

public sealed record DeliveryOptimizationJobStatus(
    string Content,
    string Status,
    long FileSizeBytes,
    long DownloadedBytes,
    long DownloadRateBytesPerSecond,
    string Details);
