namespace WindowsClientCenter.Intune.Services.Models;

public sealed record Win32RetryAllRequest(
    int MaxAppsPerRun,
    int CooldownSeconds,
    string BackupRoot,
    bool WhatIf,
    bool RemoveGrsEntriesForFailedApps,
    bool RestartImeService);
