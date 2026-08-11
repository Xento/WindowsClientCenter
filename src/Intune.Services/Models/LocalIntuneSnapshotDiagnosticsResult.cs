namespace WindowsClientCenter.Intune.Services.Models;

public sealed record LocalIntuneSnapshotDiagnosticsResult(
    LocalIntuneSnapshot Snapshot,
    IReadOnlyList<string> Timings);
