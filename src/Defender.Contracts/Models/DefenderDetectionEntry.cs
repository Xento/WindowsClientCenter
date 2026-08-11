namespace WindowsClientCenter.Defender.Contracts.Models;

public sealed record DefenderDetectionEntry(
    DateTimeOffset? DetectedAtUtc,
    DateTimeOffset? LastStatusChangeUtc,
    string ThreatName,
    int? ThreatId,
    string Severity,
    string Category,
    string Action,
    bool? ActionSuccess,
    bool IsActive,
    string Source,
    string Details);
