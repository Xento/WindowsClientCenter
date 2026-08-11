namespace WindowsClientCenter.Intune.Services.Models;

public sealed record ImeLogTimelineSnapshot(
    string Fingerprint,
    IReadOnlyList<ImeLogTimelineEntry> Entries);
