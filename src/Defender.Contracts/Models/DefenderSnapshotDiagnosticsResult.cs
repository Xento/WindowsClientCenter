namespace WindowsClientCenter.Defender.Contracts.Models;

public sealed record DefenderSnapshotDiagnosticsResult(
    DefenderSnapshot Snapshot,
    IReadOnlyList<string> Timings);
