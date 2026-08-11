using WindowsClientCenter.Intune.Services.Models;

namespace WindowsClientCenter.Intune.Services.Contracts;

public interface IWindowsProcessManager
{
    ValueTask<ProcessSnapshot> GetProcessesAsync(string host, CancellationToken cancellationToken);
    ValueTask<DeviceActionResult> KillProcessAsync(string host, int processId, CancellationToken cancellationToken);
}
