using WindowsClientCenter.Intune.Services.Models;

namespace WindowsClientCenter.Intune.Services.Contracts;

public interface IWindowsServiceManager
{
    ValueTask<WindowsServiceSnapshot> GetServicesAsync(string host, CancellationToken cancellationToken);
    ValueTask<DeviceActionResult> StartServiceAsync(string host, string serviceName, CancellationToken cancellationToken);
    ValueTask<DeviceActionResult> StopServiceAsync(string host, string serviceName, CancellationToken cancellationToken);
    ValueTask<DeviceActionResult> RestartServiceAsync(string host, string serviceName, CancellationToken cancellationToken);
    ValueTask<DeviceActionResult> KillServiceProcessAsync(string host, string serviceName, CancellationToken cancellationToken);
    ValueTask<DeviceActionResult> SetStartModeAsync(string host, string serviceName, WindowsServiceStartMode startMode, CancellationToken cancellationToken);
}
