using WindowsClientCenter.Intune.Services.Contracts;
using WindowsClientCenter.Intune.Services.Models;

namespace WindowsClientCenter.Intune.Services.Runtime;

internal sealed class DisabledDeviceQueryService : IDeviceQueryService
{
    public ValueTask<IReadOnlyList<DeviceRecord>> GetDevicesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IReadOnlyList<DeviceRecord>>([]);
    }

    public ValueTask<DeviceRecord?> GetDeviceByIdAsync(string deviceId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<DeviceRecord?>(null);
    }

    public ValueTask<DeviceRecord?> GetDeviceByHostAsync(string host, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<DeviceRecord?>(null);
    }
}
