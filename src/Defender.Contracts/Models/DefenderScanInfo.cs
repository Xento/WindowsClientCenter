namespace WindowsClientCenter.Defender.Contracts.Models;

public sealed record DefenderScanInfo(
    DateTimeOffset? QuickScanStartUtc,
    DateTimeOffset? QuickScanEndUtc,
    DateTimeOffset? FullScanStartUtc,
    DateTimeOffset? FullScanEndUtc,
    DateTimeOffset? LastScanUtc);
