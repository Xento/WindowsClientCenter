namespace WindowsClientCenter.Intune.Services.Models;

public sealed record WindowsServiceSnapshot(
    string Host,
    bool IsLocalHost,
    IReadOnlyList<WindowsServiceEntry> Services,
    IReadOnlyList<string> Warnings);

public sealed record WindowsServiceEntry(
    string ServiceName,
    string DisplayName,
    string State,
    WindowsServiceStartMode StartMode,
    string Description,
    int? ProcessId)
{
    public string StartModeDisplay => StartMode switch
    {
        WindowsServiceStartMode.Automatic => "Automatic",
        WindowsServiceStartMode.AutomaticDelayedStart => "Automatic (Delayed Start)",
        WindowsServiceStartMode.Manual => "Manual",
        WindowsServiceStartMode.Disabled => "Disabled",
        _ => StartMode.ToString()
    };
}

public enum WindowsServiceStartMode
{
    Automatic,
    AutomaticDelayedStart,
    Manual,
    Disabled
}
