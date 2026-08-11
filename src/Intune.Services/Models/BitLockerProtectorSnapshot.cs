namespace WindowsClientCenter.Intune.Services.Models;

public sealed record BitLockerProtectorSnapshot(
    string ProtectorId,
    string ProtectorType,
    string FriendlyLabel,
    bool IsRecoveryPassword,
    bool IsRemovable,
    string BackupTargetsText,
    string LastActionStatusText = "");
