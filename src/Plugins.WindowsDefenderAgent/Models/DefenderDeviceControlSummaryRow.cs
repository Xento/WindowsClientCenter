using WindowsClientCenter.Defender.Contracts.Models;

namespace WindowsClientCenter.Plugins.WindowsDefenderAgent.Models;

public sealed class DefenderDeviceControlSummaryRow
{
    public DefenderDeviceControlSummaryRow(DefenderDeviceControlDeviceSummary summary)
    {
        Summary = summary;
        FirstBlockedText = summary.FirstBlockedUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "Unknown";
        LastBlockedText = summary.LastBlockedUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "Unknown";
    }

    public DefenderDeviceControlDeviceSummary Summary { get; }

    public string DeviceType => Summary.DeviceType;

    public string DisplayName => Summary.DisplayName;

    public int BlockedCount => Summary.BlockedCount;

    public string FirstBlockedText { get; }

    public string LastBlockedText { get; }

    public string DeviceId => Summary.DeviceId;

    public string DeviceInstanceId => Summary.DeviceInstanceId;

    public string HardwareIds => Summary.HardwareIds;

    public string VendorId => Summary.VendorId;

    public string ProductId => Summary.ProductId;

    public string SerialNumber => Summary.SerialNumber;

    public string ClassGuid => Summary.ClassGuid;

    public string PolicyName => Summary.PolicyName;

    public string PolicyId => Summary.PolicyId;

    public string PolicyRuleId => Summary.PolicyRuleId;

    public string PolicyVerdict => Summary.PolicyVerdict;

    public string Access => Summary.Access;

    public string LastUser => Summary.LastUser;
}
