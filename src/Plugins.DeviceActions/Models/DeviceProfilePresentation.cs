using CommunityToolkit.Mvvm.ComponentModel;
using WindowsClientCenter.Intune.Services.Models;

namespace WindowsClientCenter.Plugins.DeviceActions.Models;

public partial class DeviceProfilePresentation : ObservableObject
{
    public DeviceProfilePresentation(WindowsProfileEntry profile)
    {
        AccountName = profile.AccountName;
        Sid = profile.Sid;
        LocalPath = profile.LocalPath;
        LastUseTimeUtc = profile.LastUseTimeUtc;
        ServerProfilePath = profile.ServerProfilePath;
        IsLoaded = profile.IsLoaded;
        IsTemporary = profile.IsTemporary;
        IsRoaming = profile.IsRoaming;
        IsMandatory = profile.IsMandatory;
        IsCorrupted = profile.IsCorrupted;
        IsSpecial = profile.IsSpecial;
    }

    public string AccountName { get; }
    public string Sid { get; }
    public string LocalPath { get; }
    public DateTimeOffset? LastUseTimeUtc { get; }
    public string ServerProfilePath { get; }
    public bool IsLoaded { get; }
    public bool IsTemporary { get; }
    public bool IsRoaming { get; }
    public bool IsMandatory { get; }
    public bool IsCorrupted { get; }
    public bool IsSpecial { get; }

    [ObservableProperty]
    private string _rawSizeDisplay = "Not calculated";

    [ObservableProperty]
    private string _policySizeDisplay = "Not calculated";

    [ObservableProperty]
    private string _lastCalculationSummary = string.Empty;

    public string LastUseDisplay => LastUseTimeUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? string.Empty;

    public void ApplySizeResult(WindowsProfileSizeResult result)
    {
        var summary = $"Files={result.FileCount:N0}, Dirs={result.DirectoryCount:N0}";
        if (result.Mode == ProfileSizeCalculationMode.PolicyExcluded)
        {
            PolicySizeDisplay = FormatBytes(result.SizeBytes);
        }
        else
        {
            RawSizeDisplay = FormatBytes(result.SizeBytes);
        }

        LastCalculationSummary = summary;
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unitIndex = 0;
        while (value >= 1024d && unitIndex < units.Length - 1)
        {
            value /= 1024d;
            unitIndex++;
        }

        return $"{value:0.##} {units[unitIndex]}";
    }
}
