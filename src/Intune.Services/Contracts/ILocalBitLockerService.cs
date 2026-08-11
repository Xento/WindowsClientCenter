using WindowsClientCenter.Intune.Services.Models;

namespace WindowsClientCenter.Intune.Services.Contracts;

public interface ILocalBitLockerService
{
    ValueTask<BitLockerHostSnapshot> GetSnapshotAsync(string host, CancellationToken cancellationToken, bool verboseDiagnostics = false);

    ValueTask<BitLockerActionResult> SuspendProtectionAsync(
        string host,
        string mountPoint,
        int rebootCount,
        CancellationToken cancellationToken,
        bool verboseDiagnostics = false);

    ValueTask<BitLockerActionResult> ResumeProtectionAsync(
        string host,
        string mountPoint,
        CancellationToken cancellationToken,
        bool verboseDiagnostics = false);

    ValueTask<BitLockerActionResult> AddRecoveryPasswordProtectorAsync(
        string host,
        string mountPoint,
        CancellationToken cancellationToken,
        bool verboseDiagnostics = false);

    ValueTask<BitLockerActionResult> RemoveRecoveryPasswordProtectorAsync(
        string host,
        string mountPoint,
        string protectorId,
        CancellationToken cancellationToken,
        bool verboseDiagnostics = false);

    ValueTask<BitLockerActionResult> BackupRecoveryPasswordAsync(
        string host,
        string mountPoint,
        string protectorId,
        CancellationToken cancellationToken,
        bool verboseDiagnostics = false);

    ValueTask<BitLockerActionResult> RotateRecoveryPasswordAsync(
        string host,
        string mountPoint,
        string protectorId,
        CancellationToken cancellationToken,
        bool verboseDiagnostics = false);
}
