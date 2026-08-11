using WindowsClientCenter.Intune.Services.Models;

namespace WindowsClientCenter.Intune.Services.Contracts;

public interface IDeviceQueryService
{
    ValueTask<IReadOnlyList<DeviceRecord>> GetDevicesAsync(CancellationToken cancellationToken);
    ValueTask<DeviceRecord?> GetDeviceByIdAsync(string deviceId, CancellationToken cancellationToken);
    ValueTask<DeviceRecord?> GetDeviceByHostAsync(string host, CancellationToken cancellationToken);
}
