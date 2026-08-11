namespace WindowsClientCenter.Intune.Services.Models;

public sealed record PlatformSecuritySnapshot(
    string BitLockerStatusText,
    string BitLockerDetailText,
    string TpmStatusText,
    string TpmVersionText,
    string TpmDetailText,
    string SecureBootStatusText,
    string CredentialGuardStatusText,
    string VbsStatusText,
    string MemoryIntegrityStatusText);
