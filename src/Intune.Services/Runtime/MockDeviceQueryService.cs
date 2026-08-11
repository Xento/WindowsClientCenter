using WindowsClientCenter.Intune.Services.Contracts;
using WindowsClientCenter.Intune.Services.Models;

namespace WindowsClientCenter.Intune.Services.Runtime;

public sealed class MockDeviceQueryService : IDeviceQueryService
{
    private static readonly IReadOnlyList<DeviceRecord> Devices =
    [
        new("device-001", "LAPTOP-001", "Windows", DateTimeOffset.UtcNow.AddMinutes(-42), "Compliant"),
        new("device-002", "LAPTOP-002", "Windows", DateTimeOffset.UtcNow.AddHours(-5), "InGracePeriod"),
        new("device-003", "SURFACE-OPS", "Windows", DateTimeOffset.UtcNow.AddDays(-1), "NonCompliant")
    ];

    public ValueTask<IReadOnlyList<DeviceRecord>> GetDevicesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Devices);
    }

    public ValueTask<DeviceRecord?> GetDeviceByIdAsync(string deviceId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Devices.FirstOrDefault(d => d.DeviceId.Equals(deviceId, StringComparison.OrdinalIgnoreCase)));
    }

    public ValueTask<DeviceRecord?> GetDeviceByHostAsync(string host, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(host))
        {
            return ValueTask.FromResult<DeviceRecord?>(null);
        }

        var normalizedHost = host.Trim().ToUpperInvariant();
        var existing = Devices.FirstOrDefault(d => d.DeviceName.Equals(normalizedHost, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            return ValueTask.FromResult<DeviceRecord?>(existing);
        }

        var seed = Math.Abs(normalizedHost.GetHashCode());
        var compliance = (seed % 3) switch
        {
            0 => "Compliant",
            1 => "InGracePeriod",
            _ => "NonCompliant"
        };

        var synthetic = new DeviceRecord(
            DeviceId: $"host-{normalizedHost.ToLowerInvariant()}",
            DeviceName: normalizedHost,
            Platform: "Windows",
            LastSync: DateTimeOffset.UtcNow.AddMinutes(-(seed % 180)),
            ComplianceState: compliance);

        return ValueTask.FromResult<DeviceRecord?>(synthetic);
    }
}
