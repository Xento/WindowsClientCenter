using WindowsClientCenter.Intune.Services.Models;

namespace WindowsClientCenter.Intune.Services.Contracts;

public interface ILocalIntuneEnrollmentService
{
    ValueTask<EnrollmentStatus> GetEnrollmentStatusAsync(string host, CancellationToken cancellationToken);
    ValueTask<DeviceActionResult> TriggerSyncAsync(string host, CancellationToken cancellationToken);
    ValueTask<DeviceActionResult> FixEnrollmentUrlsAsync(string host, CancellationToken cancellationToken);
    ValueTask<EnrollmentRepairPreview> PreviewReenrollAsync(string host, CancellationToken cancellationToken);
    ValueTask<DeviceActionResult> ExecuteReenrollAsync(string host, bool confirmed, CancellationToken cancellationToken);
}
