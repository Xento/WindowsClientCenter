using WindowsClientCenter.Defender.Contracts.Models;

namespace WindowsClientCenter.Plugins.WindowsDefenderAgent.Models;

public sealed class DefenderDeviceControlEventRow
{
    public DefenderDeviceControlEventRow(DefenderDeviceControlEventEntry entry)
    {
        Entry = entry;
        TimestampText = entry.TimeCreatedUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "Unknown";
    }

    public DefenderDeviceControlEventEntry Entry { get; }

    public string TimestampText { get; }

    public int EventId => Entry.EventId;

    public string DeviceType => Entry.DeviceType;

    public string DeviceName => string.IsNullOrWhiteSpace(Entry.FriendlyName) ? Entry.DeviceName : Entry.FriendlyName;

    public string DeviceInstanceId => Entry.DeviceInstanceId;

    public string HardwareIds => Entry.HardwareIds;

    public string PolicyVerdict => Entry.PolicyVerdict;

    public string Action => Entry.Action;

    public string User => Entry.User;

    public string Source => string.IsNullOrWhiteSpace(Entry.Provider) ? Entry.LogName : Entry.Provider;

    public string Message => Entry.Message;
}
