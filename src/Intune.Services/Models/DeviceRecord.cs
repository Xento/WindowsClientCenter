namespace WindowsClientCenter.Intune.Services.Models;

public sealed record DeviceRecord(
    string DeviceId,
    string DeviceName,
    string Platform,
    DateTimeOffset LastSync,
    string ComplianceState);
