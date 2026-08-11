namespace WindowsClientCenter.Intune.Services.Models;

public sealed record DeviceActionRequest(
    string DeviceId,
    string Action,
    IReadOnlyDictionary<string, string>? Parameters = null);
