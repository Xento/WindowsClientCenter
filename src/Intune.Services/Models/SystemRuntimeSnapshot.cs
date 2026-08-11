namespace WindowsClientCenter.Intune.Services.Models;

public sealed record SystemRuntimeSnapshot(
    string UptimeText,
    string LastBootText,
    string InstallDateText,
    string PendingRebootStatusText,
    string PendingRebootDetailText,
    string WindowsUpdateScheduledRestartStatusText,
    string WindowsUpdateScheduledRestartTimeText,
    string MecmScheduledRestartTimeText,
    string SessionLockStatusText,
    string SessionLockedSinceText);
