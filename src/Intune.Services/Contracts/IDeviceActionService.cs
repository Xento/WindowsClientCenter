using WindowsClientCenter.Intune.Services.Models;

namespace WindowsClientCenter.Intune.Services.Contracts;

public interface IDeviceActionService
{
    ValueTask<DeviceActionResult> ExecuteActionAsync(DeviceActionRequest request, CancellationToken cancellationToken);
}
