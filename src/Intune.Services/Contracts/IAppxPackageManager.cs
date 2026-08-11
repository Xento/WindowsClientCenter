using WindowsClientCenter.Intune.Services.Models;

namespace WindowsClientCenter.Intune.Services.Contracts;

public interface IAppxPackageManager
{
    ValueTask<AppxPackageSnapshot> GetPackagesAsync(string host, CancellationToken cancellationToken);
    ValueTask<WingetSearchSnapshot> SearchWingetAsync(string host, string query, CancellationToken cancellationToken);
    ValueTask<DeviceActionResult> InstallWingetAsync(string host, WingetCatalogEntry package, WingetInstallScope scope, CancellationToken cancellationToken);
    ValueTask<DeviceActionResult> UpgradeWingetAsync(string host, WingetCatalogEntry package, WingetInstallScope scope, CancellationToken cancellationToken);
    ValueTask<DeviceActionResult> RemoveForUserAsync(string host, string packageFullName, string userSid, CancellationToken cancellationToken);
    ValueTask<DeviceActionResult> RemoveForAllUsersAsync(string host, string packageFullName, CancellationToken cancellationToken);
    ValueTask<DeviceActionResult> RemoveProvisioningAsync(string host, string provisionedPackageName, CancellationToken cancellationToken);
    ValueTask<DeviceActionResult> RegisterForActiveUserAsync(string host, string packageFullName, CancellationToken cancellationToken);
}
