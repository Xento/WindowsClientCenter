namespace WindowsClientCenter.Defender.Contracts.Models;

public sealed record DefenderLatestVersionInfo(
    string SourceUrl,
    string ReleaseNotesUrl,
    DateTimeOffset RetrievedAtUtc,
    string SecurityIntelligenceVersion,
    string EngineVersion,
    string PlatformVersion,
    DateTimeOffset? ReleasedAtUtc,
    string? ErrorMessage = null);
