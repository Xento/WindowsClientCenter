namespace WindowsClientCenter.Intune.Services.Models;

public sealed record CloudManagedDeviceSummary(
    string ManagedDeviceId,
    string DeviceName,
    string? AzureAdDeviceId,
    string? UserPrincipalName,
    string? OperatingSystem,
    string? ComplianceState,
    DateTimeOffset? LastSyncDateTime,
    bool IsExactMatch,
    string Source);
