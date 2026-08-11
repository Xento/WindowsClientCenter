using WindowsClientCenter.Intune.Services.Models;

namespace WindowsClientCenter.Intune.Services.Contracts;

public interface ICloudManagedDeviceService
{
    ValueTask<CloudManagedDeviceSummary?> FindManagedDeviceByHostAsync(string host, CancellationToken cancellationToken);
    ValueTask<CloudSyncResult> SyncManagedDeviceAsync(string managedDeviceId, CancellationToken cancellationToken);
}
