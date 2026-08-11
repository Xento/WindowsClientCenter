namespace WindowsClientCenter.Defender.Contracts.Models;

public sealed record DefenderDeviceControlDeviceSummary(
    string DeviceKey,
    string DeviceType,
    string DisplayName,
    int BlockedCount,
    DateTimeOffset? FirstBlockedUtc,
    DateTimeOffset? LastBlockedUtc,
    string DeviceId,
    string DeviceInstanceId,
    string HardwareIds,
    string VendorId,
    string ProductId,
    string SerialNumber,
    string ClassGuid,
    string PolicyName,
    string PolicyId,
    string PolicyRuleId,
    string PolicyVerdict,
    string Access,
    string LastUser);
