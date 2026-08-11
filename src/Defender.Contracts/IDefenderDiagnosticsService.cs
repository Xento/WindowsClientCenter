using WindowsClientCenter.Defender.Contracts.Models;

namespace WindowsClientCenter.Defender.Contracts;

public interface IDefenderDiagnosticsService
{
    ValueTask<DefenderSnapshot> GetSnapshotAsync(string host, CancellationToken cancellationToken);
    ValueTask<DefenderSnapshotDiagnosticsResult> GetSnapshotDiagnosticsAsync(string host, CancellationToken cancellationToken);
    ValueTask<DefenderSettingsSnapshot> GetSettingsAsync(string host, CancellationToken cancellationToken);
    ValueTask<IReadOnlyList<DefenderDetectionEntry>> GetDetectionsAsync(string host, int daysBack, CancellationToken cancellationToken);
    ValueTask<DefenderDeviceControlSnapshot> GetDeviceControlEventsAsync(string host, int daysBack, CancellationToken cancellationToken);
    ValueTask<DefenderActionResult> ExecuteActionAsync(string host, DefenderActionRequest request, CancellationToken cancellationToken);
}
