using WindowsClientCenter.Intune.Services.Contracts;
using WindowsClientCenter.Intune.Services.Models;

namespace WindowsClientCenter.Intune.Services.Runtime;

internal sealed class MockCloudManagedDeviceService : ICloudManagedDeviceService
{
    public ValueTask<CloudManagedDeviceSummary?> FindManagedDeviceByHostAsync(string host, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(host))
        {
            return ValueTask.FromResult<CloudManagedDeviceSummary?>(null);
        }

        var normalizedHost = host.Trim().ToUpperInvariant();
        return ValueTask.FromResult<CloudManagedDeviceSummary?>(new CloudManagedDeviceSummary(
            ManagedDeviceId: $"mock-managed-{normalizedHost.ToLowerInvariant()}",
            DeviceName: normalizedHost,
            AzureAdDeviceId: $"mock-aad-{normalizedHost.ToLowerInvariant()}",
            UserPrincipalName: "admin@example.invalid",
            OperatingSystem: "Windows",
            ComplianceState: "Compliant",
            LastSyncDateTime: DateTimeOffset.UtcNow.AddMinutes(-15),
            IsExactMatch: true,
            Source: "Mock"));
    }

    public ValueTask<CloudSyncResult> SyncManagedDeviceAsync(string managedDeviceId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(CloudSyncResult.Ok(
            $"Mock cloud sync queued for managed device '{managedDeviceId}'.",
            $"mock-sync-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}"));
    }
}
