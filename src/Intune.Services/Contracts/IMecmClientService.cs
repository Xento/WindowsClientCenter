using WindowsClientCenter.Intune.Services.Models;

namespace WindowsClientCenter.Intune.Services.Contracts;

public interface IMecmClientService
{
    ValueTask<MecmOverviewSnapshot> GetOverviewAsync(string host, CancellationToken cancellationToken);
    ValueTask<DeviceActionResult> ExecuteOverviewActionAsync(string host, MecmOverviewAction action, CancellationToken cancellationToken);
    ValueTask<MecmApplicationSnapshot> GetApplicationsAsync(string host, CancellationToken cancellationToken);
    ValueTask<DeviceActionResult> ExecuteApplicationActionAsync(string host, string applicationId, string revision, bool isMachineTarget, MecmApplicationAction action, CancellationToken cancellationToken);
    ValueTask<DeviceActionResult> TriggerApplicationEvaluationAsync(string host, MecmApplicationEvaluationMode mode, CancellationToken cancellationToken);
    ValueTask<MecmPendingUpdatesSnapshot> GetPendingUpdatesAsync(string host, CancellationToken cancellationToken);
    ValueTask<MecmAllUpdatesSnapshot> GetAllUpdatesAsync(string host, CancellationToken cancellationToken);
    ValueTask<DeviceActionResult> InstallUpdatesAsync(string host, MecmUpdateInstallRequest request, CancellationToken cancellationToken);
    ValueTask<MecmPackagesSnapshot> GetPackagesAsync(string host, CancellationToken cancellationToken);
    ValueTask<DeviceActionResult> ExecutePackageAsync(string host, string advertisementId, CancellationToken cancellationToken);
    ValueTask<MecmBaselinesSnapshot> GetBaselinesAsync(string host, CancellationToken cancellationToken);
    ValueTask<MecmBaselineDetails> GetBaselineDetailsAsync(string host, string baselineName, string version, bool isMachineTarget, CancellationToken cancellationToken);
    ValueTask<DeviceActionResult> TriggerBaselineEvaluationAsync(string host, string baselineName, string version, bool isMachineTarget, bool enforce, CancellationToken cancellationToken);
}
