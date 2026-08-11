namespace WindowsClientCenter.Intune.Services.Models;

public sealed record BitLockerVolumeSnapshot(
    string MountPoint,
    string VolumeType,
    string ProtectionStatusText,
    string VolumeStatusText,
    string LockStatusText,
    int EncryptionPercentage,
    string EncryptionMethodText,
    string AutoUnlockText,
    int? SuspendRebootCount,
    string HealthLevel,
    string ComplianceStatusText,
    string ComplianceDetailsText,
    string BackupEligibilityText,
    string ConfiguredBackupTargetsText,
    string BackupAssessmentText,
    IReadOnlyList<BitLockerBackupTargetAssessmentSnapshot> BackupTargetAssessments,
    bool IsEncrypted,
    bool IsProtectionOn,
    bool IsProtectionSuspended,
    IReadOnlyList<BitLockerProtectorSnapshot> Protectors);
