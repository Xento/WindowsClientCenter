using WindowsClientCenter.Intune.Services.Models;

namespace WindowsClientCenter.Intune.Services.Contracts;

public interface ILocalDeviceActionService
{
    ValueTask<DeviceActionResult> ExecuteLocalActionAsync(
        string host,
        string action,
        IReadOnlyDictionary<string, string>? parameters,
        CancellationToken cancellationToken);

    ValueTask<PowerStateSnapshot> GetPowerStateAsync(string host, CancellationToken cancellationToken);
    ValueTask<DeviceActionResult> ShutdownAsync(string host, CancellationToken cancellationToken);
    ValueTask<DeviceActionResult> RestartAsync(string host, CancellationToken cancellationToken);
    ValueTask<DeviceActionResult> LogoffAsync(string host, CancellationToken cancellationToken);
    ValueTask<DeviceActionResult> LockWorkstationAsync(string host, CancellationToken cancellationToken);
    ValueTask<DeviceActionResult> SetPowerSchemeAsync(string host, string schemeId, CancellationToken cancellationToken);
}
