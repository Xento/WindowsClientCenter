using WindowsClientCenter.Defender.Contracts.Models;

namespace WindowsClientCenter.Plugins.WindowsDefenderAgent.Models;

public sealed class DefenderDetectionRow
{
    public DefenderDetectionRow(DefenderDetectionEntry entry)
    {
        Entry = entry;
        var timestamp = entry.DetectedAtUtc ?? entry.LastStatusChangeUtc;
        TimestampText = timestamp?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "Unknown";
        Severity = string.IsNullOrWhiteSpace(entry.Severity) ? "Unknown" : entry.Severity;
    }

    public DefenderDetectionEntry Entry { get; }

    public string TimestampText { get; }

    public string Severity { get; }

    public string ThreatName => Entry.ThreatName;

    public bool IsActive => Entry.IsActive;

    public string Source => Entry.Source;

    public string Action => Entry.Action;

    public string Details => Entry.Details;
}
