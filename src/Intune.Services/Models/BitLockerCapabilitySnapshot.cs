namespace WindowsClientCenter.Intune.Services.Models;

public sealed record BitLockerCapabilitySnapshot(
    bool IsBitLockerCommandAvailable,
    bool IsAdministrator,
    bool SupportsSuspendProtection,
    bool SupportsResumeProtection,
    bool SupportsRecoveryPasswordProtectorOperations,
    bool SupportsBackupToAd,
    bool SupportsBackupToEntra,
    bool IsDomainJoined,
    bool IsEntraJoined,
    IReadOnlyList<string> Warnings);
