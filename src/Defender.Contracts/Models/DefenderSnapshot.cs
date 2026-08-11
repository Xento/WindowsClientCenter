namespace WindowsClientCenter.Defender.Contracts.Models;

public sealed record DefenderSnapshot(
    string Host,
    string MachineName,
    DateTimeOffset CapturedAtUtc,
    bool IsLocalHost,
    bool IsManaged,
    string ManagedBy,
    DefenderProtectionStatus Protection,
    DefenderVersionInfo Versions,
    DefenderScanInfo Scans,
    int ActiveDetectionCount,
    int ActiveHighOrCriticalDetectionCount,
    string HealthLevel,
    string HealthSummary,
    IReadOnlyList<string> Notes,
    DefenderLatestVersionInfo? LatestVersionInfo = null);
