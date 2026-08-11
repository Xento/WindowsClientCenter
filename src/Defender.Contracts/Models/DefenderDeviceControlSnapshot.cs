namespace WindowsClientCenter.Defender.Contracts.Models;

public sealed record DefenderDeviceControlSnapshot(
    DateTimeOffset CapturedAtUtc,
    string Source,
    IReadOnlyList<string> Notes,
    IReadOnlyList<DefenderDeviceControlEventEntry> Events,
    IReadOnlyList<DefenderDeviceControlDeviceSummary> DeviceSummaries);
