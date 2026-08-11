using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using WindowsClientCenter.Intune.Services.Models;
using WindowsClientCenter.Plugins.MecmAgent.Models;

namespace WindowsClientCenter.Plugins.MecmAgent.ViewModels;

public partial class MecmAgentViewModel
{
    private MecmOverviewSnapshot? _overviewSnapshot;
    private bool _overviewLoaded;

    public ObservableCollection<MecmOverviewActivityRow> VisibleOverviewActivities { get; } = [];
    public ObservableCollection<MecmCoManagementWorkloadRow> VisibleOverviewWorkloads { get; } = [];
    public ObservableCollection<MecmClientComponentRow> VisibleOverviewComponents { get; } = [];
    public ObservableCollection<MecmClientServiceRow> VisibleOverviewServices { get; } = [];
    public ObservableCollection<MecmHealthCheckRow> VisibleOverviewHealthChecks { get; } = [];

    public bool CanRunOverviewActions => !IsBusy && !string.IsNullOrWhiteSpace(_targetHostService.CurrentHost);
    public bool HasOverviewWarnings => _overviewSnapshot?.Warnings.Count > 0;
    public string OverviewWarningsText => HasOverviewWarnings ? string.Join(Environment.NewLine, _overviewSnapshot!.Warnings) : string.Empty;
    public string OverviewClientVersionText => GetOverviewValue(_overviewSnapshot?.ClientVersion);
    public string OverviewAssignedSiteText => GetOverviewValue(_overviewSnapshot?.AssignedSite);
    public string OverviewManagementPointText => GetOverviewValue(_overviewSnapshot?.ManagementPoint);
    public string OverviewRebootPendingText => GetOverviewValue(_overviewSnapshot?.RebootPendingText);
    public string OverviewCoManagementStateText => GetOverviewValue(_overviewSnapshot?.CoManagementStateText);

    partial void OnCurrentHostChanged(string value)
    {
        OnPropertyChanged(nameof(CanRunOverviewActions));
    }

    public Task RequestMachinePolicyAsync()
    {
        return ExecuteOverviewActionCoreAsync(MecmOverviewAction.RequestMachinePolicy, reloadAfterSuccess: true);
    }

    public Task EvaluateMachinePolicyAsync()
    {
        return ExecuteOverviewActionCoreAsync(MecmOverviewAction.EvaluateMachinePolicy, reloadAfterSuccess: true);
    }

    public Task TriggerHeartbeatDiscoveryAsync()
    {
        return ExecuteOverviewActionCoreAsync(MecmOverviewAction.TriggerHeartbeatDiscovery, reloadAfterSuccess: true);
    }

    public Task TriggerHardwareInventoryAsync()
    {
        return ExecuteOverviewActionCoreAsync(MecmOverviewAction.TriggerHardwareInventory, reloadAfterSuccess: true);
    }

    public Task TriggerSoftwareInventoryAsync()
    {
        return ExecuteOverviewActionCoreAsync(MecmOverviewAction.TriggerSoftwareInventory, reloadAfterSuccess: true);
    }

    public Task RunCcmevalAsync()
    {
        return ExecuteOverviewActionCoreAsync(MecmOverviewAction.RunCcmeval, reloadAfterSuccess: true);
    }

    public Task RestartCcmExecAsync()
    {
        return ExecuteOverviewActionCoreAsync(MecmOverviewAction.RestartCcmExec, reloadAfterSuccess: true);
    }

    public Task ResetPolicySoftAsync()
    {
        return ExecuteOverviewActionCoreAsync(MecmOverviewAction.ResetPolicySoft, reloadAfterSuccess: true);
    }

    public Task ResetPolicyHardAsync()
    {
        return ExecuteOverviewActionCoreAsync(MecmOverviewAction.ResetPolicyHard, reloadAfterSuccess: true);
    }

    public Task RepairClientAsync()
    {
        return ExecuteOverviewActionCoreAsync(MecmOverviewAction.RepairClient, reloadAfterSuccess: false);
    }

    [RelayCommand]
    public void OpenPolicyAgentLog()
    {
        OpenRemoteLog(@"C$\Windows\CCM\Logs\PolicyAgent.log", "PolicyAgent.log");
    }

    [RelayCommand]
    public void OpenInventoryAgentLog()
    {
        OpenRemoteLog(@"C$\Windows\CCM\Logs\InventoryAgent.log", "InventoryAgent.log");
    }

    [RelayCommand]
    public void OpenCoManagementHandlerLog()
    {
        OpenRemoteLog(@"C$\Windows\CCM\Logs\CoManagementHandler.log", "CoManagementHandler.log");
    }

    [RelayCommand]
    public void OpenLocationServicesLog()
    {
        OpenRemoteLog(@"C$\Windows\CCM\Logs\LocationServices.log", "LocationServices.log");
    }

    [RelayCommand]
    public void OpenClientIdManagerStartupLog()
    {
        OpenRemoteLog(@"C$\Windows\CCM\Logs\ClientIDManagerStartup.log", "ClientIDManagerStartup.log");
    }

    [RelayCommand]
    public void OpenCcmEvalLog()
    {
        OpenRemoteLog(@"C$\Windows\CCM\Logs\CcmEval.log", "CcmEval.log");
    }

    private async Task LoadOverviewAsync(string host, CancellationToken cancellationToken)
    {
        var snapshot = await _mecmClientService.GetOverviewAsync(host, cancellationToken);
        _overviewSnapshot = snapshot;
        _overviewLoaded = true;

        ReplaceRows(VisibleOverviewActivities, snapshot.Activities.Select(static item => new MecmOverviewActivityRow(item)));
        ReplaceRows(VisibleOverviewWorkloads, snapshot.Workloads.Select(static item => new MecmCoManagementWorkloadRow(item)));
        ReplaceRows(VisibleOverviewComponents, snapshot.Components.Select(static item => new MecmClientComponentRow(item)));
        ReplaceRows(VisibleOverviewServices, snapshot.Services.Select(static item => new MecmClientServiceRow(item)));
        ReplaceRows(VisibleOverviewHealthChecks, snapshot.HealthChecks.Select(static item => new MecmHealthCheckRow(item)));

        NotifyOverviewStateProperties();
        Status = BuildLoadStatus(
            $"Loaded MECM overview with {VisibleOverviewActivities.Count} activity row(s), {VisibleOverviewComponents.Count} component(s) and {VisibleOverviewHealthChecks.Count} health check(s).",
            snapshot.Warnings);
    }

    private async Task ExecuteOverviewActionCoreAsync(MecmOverviewAction action, bool reloadAfterSuccess)
    {
        var host = _targetHostService.CurrentHost?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(host))
        {
            Status = DisconnectedStatus;
            return;
        }

        var selection = _targetHostService.CaptureSelection();
        using var linkedCancellationTokenSource = CreateHostLinkedCancellation(selection, CancellationToken.None);

        try
        {
            IsBusy = true;
            var result = await _mecmClientService.ExecuteOverviewActionAsync(host, action, linkedCancellationTokenSource.Token);
            Status = result.Message;
            if (!result.Success)
            {
                return;
            }

            if (reloadAfterSuccess)
            {
                _overviewLoaded = false;
                await LoadOverviewAsync(host, linkedCancellationTokenSource.Token);
            }
        }
        catch (OperationCanceledException) when (selection.CancellationToken.IsCancellationRequested)
        {
            Status = "Operation canceled because the target host changed.";
        }
        catch (Exception ex)
        {
            Status = $"{GetOverviewActionDisplayName(action)} failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ClearOverviewState()
    {
        _overviewLoaded = false;
        _overviewSnapshot = null;
        VisibleOverviewActivities.Clear();
        VisibleOverviewWorkloads.Clear();
        VisibleOverviewComponents.Clear();
        VisibleOverviewServices.Clear();
        VisibleOverviewHealthChecks.Clear();
        NotifyOverviewStateProperties();
    }

    private void NotifyOverviewStateProperties()
    {
        OnPropertyChanged(nameof(CanRunOverviewActions));
        OnPropertyChanged(nameof(HasOverviewWarnings));
        OnPropertyChanged(nameof(OverviewWarningsText));
        OnPropertyChanged(nameof(OverviewClientVersionText));
        OnPropertyChanged(nameof(OverviewAssignedSiteText));
        OnPropertyChanged(nameof(OverviewManagementPointText));
        OnPropertyChanged(nameof(OverviewRebootPendingText));
        OnPropertyChanged(nameof(OverviewCoManagementStateText));
    }

    private static void ReplaceRows<T>(ObservableCollection<T> target, IEnumerable<T> rows)
    {
        target.Clear();
        foreach (var row in rows)
        {
            target.Add(row);
        }
    }

    private static string GetOverviewValue(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "Unknown" : value;
    }

    private static string GetOverviewActionDisplayName(MecmOverviewAction action)
    {
        return action switch
        {
            MecmOverviewAction.RequestMachinePolicy => "Request policy",
            MecmOverviewAction.EvaluateMachinePolicy => "Evaluate policy",
            MecmOverviewAction.TriggerHeartbeatDiscovery => "Heartbeat discovery",
            MecmOverviewAction.TriggerHardwareInventory => "Hardware inventory",
            MecmOverviewAction.TriggerSoftwareInventory => "Software inventory",
            MecmOverviewAction.RunCcmeval => "CCMEval",
            MecmOverviewAction.RestartCcmExec => "SMS Agent Host restart",
            MecmOverviewAction.ResetPolicySoft => "Policy reset",
            MecmOverviewAction.ResetPolicyHard => "Hard policy reset",
            MecmOverviewAction.RepairClient => "Client repair",
            _ => "MECM overview action"
        };
    }
}
