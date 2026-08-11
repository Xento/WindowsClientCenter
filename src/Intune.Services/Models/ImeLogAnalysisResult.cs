namespace WindowsClientCenter.Intune.Services.Models;

public sealed record ImeLogAnalysisResult(
    string Fingerprint,
    IReadOnlyList<ImeLogTimelineEntry> TimelineEntries,
    IReadOnlyList<ImeApplicationStatusEntry> ApplicationStatuses);
