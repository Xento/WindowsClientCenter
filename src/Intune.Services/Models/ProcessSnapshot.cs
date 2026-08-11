namespace WindowsClientCenter.Intune.Services.Models;

public sealed record ProcessSnapshot(
    string Host,
    int LogicalProcessorCount,
    DateTimeOffset CapturedAtUtc,
    IReadOnlyList<ProcessSnapshotEntry> Processes,
    IReadOnlyList<string> Warnings);

public sealed record ProcessSnapshotEntry(
    string Name,
    int ProcessId,
    int? ParentProcessId,
    string CommandLine,
    long WorkingSetBytes,
    long PrivateMemoryBytes,
    double CpuTimeSeconds,
    DateTimeOffset? StartTimeUtc,
    int ThreadCount,
    int HandleCount);
