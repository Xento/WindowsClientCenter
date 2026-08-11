using WindowsClientCenter.Intune.Services.Contracts;
using WindowsClientCenter.Intune.Services.Models;

namespace WindowsClientCenter.Intune.Services.Runtime;

public sealed class MockDeviceActionService : IDeviceActionService
{
    public ValueTask<DeviceActionResult> ExecuteActionAsync(DeviceActionRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var trackingId = $"mock-{request.Action}-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";
        return ValueTask.FromResult(DeviceActionResult.Ok(
            $"Mock action '{request.Action}' queued for device '{request.DeviceId}'.",
            trackingId));
    }
}
