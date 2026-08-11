namespace WindowsClientCenter.Intune.Services.Models;

public sealed record Win32RetryRequest(
    string IdentityId,
    Guid AppId,
    string BackupDirectory,
    bool WhatIf);
