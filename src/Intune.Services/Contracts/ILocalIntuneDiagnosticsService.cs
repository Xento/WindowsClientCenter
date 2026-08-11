using WindowsClientCenter.Intune.Services.Models;

namespace WindowsClientCenter.Intune.Services.Contracts;

public interface ILocalIntuneDiagnosticsService
{
    ValueTask<LocalIntuneSnapshot> GetSnapshotAsync(string host, CancellationToken cancellationToken);
    ValueTask<LocalIntuneSnapshotDiagnosticsResult> GetSnapshotDiagnosticsAsync(string host, CancellationToken cancellationToken);
    ValueTask<LocalIntuneSnapshot> GetOverviewCoreSnapshotAsync(string host, CancellationToken cancellationToken);
    ValueTask<PlatformSecuritySnapshot?> GetPlatformSecuritySnapshotAsync(string host, CancellationToken cancellationToken);
    ValueTask<SystemRuntimeSnapshot?> GetSystemRuntimeSnapshotAsync(string host, CancellationToken cancellationToken);
    ValueTask<NetworkConnectivitySnapshot?> GetNetworkConnectivitySnapshotAsync(string host, CancellationToken cancellationToken);
    ValueTask<PortAuthenticationSnapshot?> GetPortAuthenticationSnapshotAsync(string host, CancellationToken cancellationToken);
    ValueTask<DeliveryOptimizationSnapshot?> GetDeliveryOptimizationSnapshotAsync(string host, CancellationToken cancellationToken);
    ValueTask<IReadOnlyList<IntuneLogEntry>> GetLogEntriesAsync(string host, string logName, int maxEntries, CancellationToken cancellationToken);
    ValueTask<IReadOnlyList<MdmEventAnalysisEntry>> GetMdmAdminEventsAsync(string host, int maxEntries, CancellationToken cancellationToken);
    ValueTask<string> ExportSnapshotAsync(string host, string outputDirectory, CancellationToken cancellationToken);
    ValueTask<string> ExportMdmDiagnosticsAsync(string host, string outputDirectory, CancellationToken cancellationToken);
}
