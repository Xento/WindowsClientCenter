using WindowsClientCenter.Intune.Services.Models;

namespace WindowsClientCenter.Plugins.DeviceActions.Models;

public sealed record DeviceProcessPresentation(
    string Name,
    int ProcessId,
    int? ParentProcessId,
    string CommandLine,
    long WorkingSetBytes,
    long PrivateMemoryBytes,
    double? CpuPercent,
    DateTimeOffset? StartTimeUtc,
    int ThreadCount,
    int HandleCount)
{
    public string CpuDisplay => CpuPercent.HasValue ? $"{CpuPercent.Value:F1} %" : "0.0 %";
    public string WorkingSetDisplay => FormatBytes(WorkingSetBytes);
    public string PrivateMemoryDisplay => FormatBytes(PrivateMemoryBytes);
    public string StartTimeDisplay => StartTimeUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "-";
    public double SortCpuPercent => CpuPercent ?? 0;

    public static DeviceProcessPresentation FromSnapshotEntry(ProcessSnapshotEntry entry, double? cpuPercent)
    {
        return new DeviceProcessPresentation(
            entry.Name,
            entry.ProcessId,
            entry.ParentProcessId,
            entry.CommandLine,
            entry.WorkingSetBytes,
            entry.PrivateMemoryBytes,
            cpuPercent,
            entry.StartTimeUtc,
            entry.ThreadCount,
            entry.HandleCount);
    }

    private static string FormatBytes(long bytes)
    {
        const double kilo = 1024d;
        const double mega = kilo * 1024d;
        const double giga = mega * 1024d;

        return bytes switch
        {
            >= (long)giga => $"{bytes / giga:F1} GB",
            >= (long)mega => $"{bytes / mega:F1} MB",
            >= (long)kilo => $"{bytes / kilo:F1} KB",
            _ => $"{bytes} B"
        };
    }
}

public sealed class DeviceProcessTreeNode(DeviceProcessPresentation process)
{
    public DeviceProcessPresentation Process { get; } = process;
    public IList<DeviceProcessTreeNode> Children { get; } = new List<DeviceProcessTreeNode>();

    public string Name => Process.Name;
    public int ProcessId => Process.ProcessId;
    public int? ParentProcessId => Process.ParentProcessId;
    public string CpuDisplay => Process.CpuDisplay;
    public string WorkingSetDisplay => Process.WorkingSetDisplay;
    public string PrivateMemoryDisplay => Process.PrivateMemoryDisplay;
    public string CommandLine => Process.CommandLine;
    public string StartTimeDisplay => Process.StartTimeDisplay;
    public int ThreadCount => Process.ThreadCount;
    public int HandleCount => Process.HandleCount;
}
