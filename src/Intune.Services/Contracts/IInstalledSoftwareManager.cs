using WindowsClientCenter.Intune.Services.Models;

namespace WindowsClientCenter.Intune.Services.Contracts;

public interface IInstalledSoftwareManager
{
    ValueTask<InstalledSoftwareSnapshot> GetInstalledSoftwareAsync(string host, CancellationToken cancellationToken);
    ValueTask<DeviceActionResult> RepairMsiAsync(string host, string softwareCode, CancellationToken cancellationToken);
    ValueTask<DeviceActionResult> UninstallMsiAsync(string host, string softwareCode, CancellationToken cancellationToken);
    ValueTask<DeviceActionResult> UninstallQuietAsync(string host, string quietUninstallString, string softwareIdentity, CancellationToken cancellationToken);
    ValueTask<DeviceActionResult> ForceRemoveRegistryEntryAsync(string host, InstalledSoftwareEntry software, CancellationToken cancellationToken);
}
