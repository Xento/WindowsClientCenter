using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WindowsClientCenter.Intune.Services.Contracts;
using WindowsClientCenter.Intune.Services.Models;
using WindowsClientCenter.Plugin.Abstractions.Contracts;
using WindowsClientCenter.Plugins.MecmAgent.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace WindowsClientCenter.Plugins.MecmAgent.ViewModels;

public partial class MecmAgentViewModel : ObservableObject, IDisposable
{
    private const string DisconnectedStatus = "Client is not connected. Click Connect first.";

    private readonly IMecmClientService _mecmClientService;
    private readonly ITargetHostService _targetHostService;
    private readonly IHostStatusLogSink? _hostStatusLogSink;
    private readonly ILogger<MecmAgentViewModel>? _logger;

    private IReadOnlyList<MecmApplicationRow> _allApplications = [];
    private IReadOnlyList<MecmPendingUpdateRow> _allPendingUpdates = [];
    private IReadOnlyList<MecmAllUpdateRow> _allUpdates = [];
    private IReadOnlyList<MecmPackageRow> _allPackages = [];
    private IReadOnlyList<MecmBaselineRow> _allBaselines = [];
    private List<MecmApplicationRow> _selectedApplications = [];
    private List<MecmPendingUpdateRow> _selectedPendingUpdates = [];
    private List<MecmPackageRow> _selectedPackages = [];
    private bool _applicationsLoaded;
    private bool _pendingUpdatesLoaded;
    private bool _allUpdatesLoaded;
    private bool _packagesLoaded;
    private bool _baselinesLoaded;
    private string _lastForwardedStatusLine = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _currentHost = string.Empty;

    [ObservableProperty]
    private string _status = DisconnectedStatus;

    [ObservableProperty]
    private MecmSection _currentSection;

    [ObservableProperty]
    private MecmApplicationRow? _selectedApplication;

    [ObservableProperty]
    private MecmPendingUpdateRow? _selectedPendingUpdate;

    [ObservableProperty]
    private MecmPackageRow? _selectedPackage;

    [ObservableProperty]
    private MecmBaselineRow? _selectedBaseline;

    [ObservableProperty]
    private bool _hideNonUserUiExperienceApps;

    [ObservableProperty]
    private bool _showMissingUpdatesOnly = true;

    [ObservableProperty]
    private bool _hideDuplicateUpdates = true;

    public ObservableCollection<MecmApplicationRow> VisibleApplications { get; } = [];
    public ObservableCollection<MecmPendingUpdateRow> VisiblePendingUpdates { get; } = [];
    public ObservableCollection<MecmAllUpdateRow> VisibleAllUpdates { get; } = [];
    public ObservableCollection<MecmPackageRow> VisiblePackages { get; } = [];
    public ObservableCollection<MecmBaselineRow> VisibleBaselines { get; } = [];
    public ObservableCollection<MecmBaselineConfigItemRow> VisibleBaselineConfigItems { get; } = [];

    public MecmAgentViewModel(IPluginContext pluginContext, string? initialNavigationTarget = null)
    {
        _mecmClientService = pluginContext.Services.GetRequiredService<IMecmClientService>();
        _targetHostService = pluginContext.Services.GetRequiredService<ITargetHostService>();
        _hostStatusLogSink = pluginContext.Services.GetService<IHostStatusLogSink>();
        _logger = pluginContext.Services.GetService<ILogger<MecmAgentViewModel>>();
        CurrentSection = MapNavigationTarget(initialNavigationTarget);
        _targetHostService.HostChanged += OnHostChanged;
    }

    public bool IsApplicationsSection => CurrentSection == MecmSection.Applications;
    public bool IsOverviewSection => CurrentSection == MecmSection.Overview;
    public bool IsPendingUpdatesSection => CurrentSection == MecmSection.PendingUpdates;
    public bool IsAllUpdatesSection => CurrentSection == MecmSection.AllUpdates;
    public bool IsPackagesSection => CurrentSection == MecmSection.Packages;
    public bool IsDcmBaselinesSection => CurrentSection == MecmSection.DcmBaselines;
    public string? InstallApplicationsCommandTooltip => GetApplicationActionTooltip("Install");
    public string? RepairApplicationsCommandTooltip => GetApplicationActionTooltip("Repair");
    public string? UninstallApplicationsCommandTooltip => GetApplicationActionTooltip("Uninstall");

    public void Dispose()
    {
        _targetHostService.HostChanged -= OnHostChanged;
    }

    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        return LoadCurrentSectionAsync(forceReload: false, cancellationToken);
    }

    public void ApplyNavigationTarget(string? navigationTarget)
    {
        CurrentSection = MapNavigationTarget(navigationTarget);
        StartBackgroundLoad(LoadCurrentSectionAsync(forceReload: false, CancellationToken.None), "applying navigation target");
    }

    public static MecmSection MapNavigationTarget(string? navigationTarget)
    {
        return navigationTarget?.Trim().ToLowerInvariant() switch
        {
            "overview" => MecmSection.Overview,
            "updates-pending" => MecmSection.PendingUpdates,
            "updates-all" => MecmSection.AllUpdates,
            "packages" => MecmSection.Packages,
            "dcm-baselines" => MecmSection.DcmBaselines,
            "applications" => MecmSection.Applications,
            _ => MecmSection.Overview
        };
    }

    partial void OnStatusChanged(string value)
    {
        if (_hostStatusLogSink is null || string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var normalized = value.Trim();
        if (string.Equals(_lastForwardedStatusLine, normalized, StringComparison.Ordinal))
        {
            return;
        }

        _lastForwardedStatusLine = normalized;
        _hostStatusLogSink.Append($"[MECM] {normalized}");
    }

    partial void OnCurrentSectionChanged(MecmSection value)
    {
        OnPropertyChanged(nameof(IsOverviewSection));
        OnPropertyChanged(nameof(IsApplicationsSection));
        OnPropertyChanged(nameof(IsPendingUpdatesSection));
        OnPropertyChanged(nameof(IsAllUpdatesSection));
        OnPropertyChanged(nameof(IsPackagesSection));
        OnPropertyChanged(nameof(IsDcmBaselinesSection));
    }

    partial void OnSelectedApplicationChanged(MecmApplicationRow? value)
    {
        NotifyApplicationCommandStates();
    }

    partial void OnSelectedPendingUpdateChanged(MecmPendingUpdateRow? value)
    {
        NotifyPendingUpdateCommandStates();
    }

    partial void OnSelectedPackageChanged(MecmPackageRow? value)
    {
        NotifyPackageCommandStates();
    }

    partial void OnSelectedBaselineChanged(MecmBaselineRow? value)
    {
        NotifyBaselineCommandStates();
        if (!_baselinesLoaded)
        {
            return;
        }

        if (value is null)
        {
            VisibleBaselineConfigItems.Clear();
            return;
        }

        var host = _targetHostService.CurrentHost?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(host) || IsBusy || CurrentSection != MecmSection.DcmBaselines)
        {
            return;
        }

        StartBackgroundLoad(LoadBaselineDetailsAsync(host, value, CancellationToken.None), "loading MECM baseline details");
    }

    partial void OnHideNonUserUiExperienceAppsChanged(bool value)
    {
        ApplyApplicationsFilter();
    }

    partial void OnShowMissingUpdatesOnlyChanged(bool value)
    {
        ApplyAllUpdatesFilter();
    }

    partial void OnHideDuplicateUpdatesChanged(bool value)
    {
        ApplyAllUpdatesFilter();
    }

    partial void OnIsBusyChanged(bool value)
    {
        NotifyApplicationCommandStates();
        NotifyPendingUpdateCommandStates();
        NotifyPackageCommandStates();
        NotifyBaselineCommandStates();
        OnPropertyChanged(nameof(CanRunOverviewActions));
        TriggerUserApplicationPolicyCommand.NotifyCanExecuteChanged();
        TriggerMachineApplicationPolicyCommand.NotifyCanExecuteChanged();
        TriggerGlobalApplicationEvaluationCommand.NotifyCanExecuteChanged();
        InstallAllMandatoryUpdatesCommand.NotifyCanExecuteChanged();
        InstallAllApprovedUpdatesCommand.NotifyCanExecuteChanged();
    }

    public void UpdateSelectedApplications(IReadOnlyList<MecmApplicationRow> rows)
    {
        _selectedApplications = rows.Where(static row => row is not null).Distinct().ToList();
        SelectedApplication = _selectedApplications.FirstOrDefault() ?? SelectedApplication;
        NotifyApplicationCommandStates();
    }

    public void UpdateSelectedPendingUpdates(IReadOnlyList<MecmPendingUpdateRow> rows)
    {
        _selectedPendingUpdates = rows.Where(static row => row is not null).Distinct().ToList();
        SelectedPendingUpdate = _selectedPendingUpdates.FirstOrDefault() ?? SelectedPendingUpdate;
        NotifyPendingUpdateCommandStates();
    }

    public void UpdateSelectedPackages(IReadOnlyList<MecmPackageRow> rows)
    {
        _selectedPackages = rows.Where(static row => row is not null).Distinct().ToList();
        SelectedPackage = _selectedPackages.FirstOrDefault() ?? SelectedPackage;
        NotifyPackageCommandStates();
    }

    [RelayCommand]
    public Task RefreshCurrentSectionAsync()
    {
        return LoadCurrentSectionAsync(forceReload: true, CancellationToken.None);
    }

    [RelayCommand(CanExecute = nameof(CanInstallSelectedApplications))]
    public Task InstallApplicationsAsync()
    {
        return ExecuteApplicationActionAsync(MecmApplicationAction.Install);
    }

    [RelayCommand(CanExecute = nameof(CanRepairSelectedApplications))]
    public Task RepairApplicationsAsync()
    {
        return ExecuteApplicationActionAsync(MecmApplicationAction.Repair);
    }

    [RelayCommand(CanExecute = nameof(CanUninstallSelectedApplications))]
    public Task UninstallApplicationsAsync()
    {
        return ExecuteApplicationActionAsync(MecmApplicationAction.Uninstall);
    }

    [RelayCommand(CanExecute = nameof(CanTriggerApplicationEvaluation))]
    public Task TriggerUserApplicationPolicyAsync()
    {
        return ExecuteApplicationEvaluationAsync(MecmApplicationEvaluationMode.UserPolicy);
    }

    [RelayCommand(CanExecute = nameof(CanTriggerApplicationEvaluation))]
    public Task TriggerMachineApplicationPolicyAsync()
    {
        return ExecuteApplicationEvaluationAsync(MecmApplicationEvaluationMode.MachinePolicy);
    }

    [RelayCommand(CanExecute = nameof(CanTriggerApplicationEvaluation))]
    public Task TriggerGlobalApplicationEvaluationAsync()
    {
        return ExecuteApplicationEvaluationAsync(MecmApplicationEvaluationMode.GlobalEvaluation);
    }

    [RelayCommand(CanExecute = nameof(CanInstallSelectedPendingUpdates))]
    public Task InstallSelectedPendingUpdatesAsync()
    {
        return ExecuteUpdateInstallAsync(new MecmUpdateInstallRequest(
            MecmUpdateInstallMode.Selected,
            GetSelectedPendingUpdates().Select(static row => row.UpdateId).ToArray()));
    }

    [RelayCommand(CanExecute = nameof(CanInstallAllMandatoryUpdates))]
    public Task InstallAllMandatoryUpdatesAsync()
    {
        return ExecuteUpdateInstallAsync(new MecmUpdateInstallRequest(MecmUpdateInstallMode.AllMandatory, []));
    }

    [RelayCommand(CanExecute = nameof(CanInstallAllApprovedUpdates))]
    public Task InstallAllApprovedUpdatesAsync()
    {
        return ExecuteUpdateInstallAsync(new MecmUpdateInstallRequest(MecmUpdateInstallMode.AllApproved, []));
    }

    [RelayCommand(CanExecute = nameof(CanExecuteSelectedPackages))]
    public Task ExecuteSelectedPackagesAsync()
    {
        return ExecutePackageActionAsync();
    }

    [RelayCommand(CanExecute = nameof(CanEvaluateSelectedBaseline))]
    public Task EvaluateSelectedBaselineAsync()
    {
        return ExecuteBaselineEvaluationAsync();
    }

    [RelayCommand]
    public void OpenAppEnforceLog()
    {
        OpenRemoteLog(@"C$\Windows\CCM\Logs\AppEnforce.log", "AppEnforce.log");
    }

    [RelayCommand]
    public void OpenAppDiscoveryLog()
    {
        OpenRemoteLog(@"C$\Windows\CCM\Logs\AppDiscovery.log", "AppDiscovery.log");
    }

    [RelayCommand]
    public void OpenAppIntentEvalLog()
    {
        OpenRemoteLog(@"C$\Windows\CCM\Logs\AppIntentEval.log", "AppIntentEval.log");
    }

    [RelayCommand]
    public void OpenUpdatesDeploymentLog()
    {
        OpenRemoteLog(@"C$\Windows\CCM\Logs\UpdatesDeployment.log", "UpdatesDeployment.log");
    }

    [RelayCommand]
    public void OpenWuaHandlerLog()
    {
        OpenRemoteLog(@"C$\Windows\CCM\Logs\WUAHandler.log", "WUAHandler.log");
    }

    private async Task LoadCurrentSectionAsync(bool forceReload, CancellationToken cancellationToken)
    {
        var selection = _targetHostService.CaptureSelection();
        var host = selection.Host?.Trim() ?? string.Empty;
        CurrentHost = host;

        if (string.IsNullOrWhiteSpace(host))
        {
            ClearLoadedState();
            Status = DisconnectedStatus;
            return;
        }

        using var linkedCancellationTokenSource = CreateHostLinkedCancellation(selection, cancellationToken);

        try
        {
            IsBusy = true;
            switch (CurrentSection)
            {
                case MecmSection.Overview:
                    if (forceReload || !_overviewLoaded)
                    {
                        await LoadOverviewAsync(host, linkedCancellationTokenSource.Token);
                    }
                    break;
                case MecmSection.Applications:
                    if (forceReload || !_applicationsLoaded)
                    {
                        await LoadApplicationsAsync(host, linkedCancellationTokenSource.Token);
                    }
                    break;
                case MecmSection.PendingUpdates:
                    if (forceReload || !_pendingUpdatesLoaded)
                    {
                        await LoadPendingUpdatesAsync(host, linkedCancellationTokenSource.Token);
                    }
                    break;
                case MecmSection.AllUpdates:
                    if (forceReload || !_allUpdatesLoaded)
                    {
                        await LoadAllUpdatesAsync(host, linkedCancellationTokenSource.Token);
                    }
                    break;
                case MecmSection.Packages:
                    if (forceReload || !_packagesLoaded)
                    {
                        await LoadPackagesAsync(host, linkedCancellationTokenSource.Token);
                    }
                    break;
                case MecmSection.DcmBaselines:
                    if (forceReload || !_baselinesLoaded)
                    {
                        await LoadBaselinesAsync(host, linkedCancellationTokenSource.Token);
                    }
                    else if (SelectedBaseline is not null)
                    {
                        await LoadBaselineDetailsAsync(host, SelectedBaseline, linkedCancellationTokenSource.Token);
                    }
                    break;
            }
        }
        catch (OperationCanceledException) when (selection.CancellationToken.IsCancellationRequested)
        {
            Status = "Operation canceled because the target host changed.";
        }
        catch (Exception ex)
        {
            Status = $"MECM view load failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task LoadApplicationsAsync(string host, CancellationToken cancellationToken)
    {
        var preferredId = SelectedApplication?.Id;
        var snapshot = await _mecmClientService.GetApplicationsAsync(host, cancellationToken);
        _allApplications = snapshot.Entries.Select(static entry => new MecmApplicationRow(entry)).ToArray();
        _applicationsLoaded = true;
        ApplyApplicationsFilter();
        SelectedApplication = VisibleApplications.FirstOrDefault(row => string.Equals(row.Id, preferredId, StringComparison.OrdinalIgnoreCase))
            ?? VisibleApplications.FirstOrDefault();
        Status = BuildLoadStatus($"Loaded {_allApplications.Count} MECM application(s).", snapshot.Warnings);
    }

    private async Task LoadPendingUpdatesAsync(string host, CancellationToken cancellationToken)
    {
        var preferredId = SelectedPendingUpdate?.UpdateId;
        var snapshot = await _mecmClientService.GetPendingUpdatesAsync(host, cancellationToken);
        _allPendingUpdates = snapshot.Entries.Select(static entry => new MecmPendingUpdateRow(entry)).ToArray();
        _pendingUpdatesLoaded = true;
        VisiblePendingUpdates.Clear();
        foreach (var row in _allPendingUpdates)
        {
            VisiblePendingUpdates.Add(row);
        }

        SelectedPendingUpdate = VisiblePendingUpdates.FirstOrDefault(row => string.Equals(row.UpdateId, preferredId, StringComparison.OrdinalIgnoreCase))
            ?? VisiblePendingUpdates.FirstOrDefault();
        Status = BuildLoadStatus($"Loaded {_allPendingUpdates.Count} pending MECM update(s).", snapshot.Warnings);
    }

    private async Task LoadAllUpdatesAsync(string host, CancellationToken cancellationToken)
    {
        var snapshot = await _mecmClientService.GetAllUpdatesAsync(host, cancellationToken);
        _allUpdates = snapshot.Entries.Select(static entry => new MecmAllUpdateRow(entry)).ToArray();
        _allUpdatesLoaded = true;
        ApplyAllUpdatesFilter();
        Status = BuildLoadStatus($"Loaded {_allUpdates.Count} MECM update catalog entries.", snapshot.Warnings);
    }

    private async Task LoadPackagesAsync(string host, CancellationToken cancellationToken)
    {
        var preferredAdvertisementId = SelectedPackage?.AdvertisementId;
        var snapshot = await _mecmClientService.GetPackagesAsync(host, cancellationToken);
        _allPackages = snapshot.Entries.Select(static entry => new MecmPackageRow(entry)).ToArray();
        _packagesLoaded = true;

        VisiblePackages.Clear();
        foreach (var row in _allPackages)
        {
            VisiblePackages.Add(row);
        }

        SelectedPackage = VisiblePackages.FirstOrDefault(row => string.Equals(row.AdvertisementId, preferredAdvertisementId, StringComparison.OrdinalIgnoreCase))
            ?? VisiblePackages.FirstOrDefault();
        Status = BuildLoadStatus($"Loaded {_allPackages.Count} MECM package deployment(s).", snapshot.Warnings);
    }

    private async Task LoadBaselinesAsync(string host, CancellationToken cancellationToken)
    {
        (string Name, string Version, bool IsMachineTarget)? preferredBaseline = SelectedBaseline is null
            ? null
            : (SelectedBaseline.Name, SelectedBaseline.Version, SelectedBaseline.IsMachineTarget);

        var snapshot = await _mecmClientService.GetBaselinesAsync(host, cancellationToken);
        _allBaselines = snapshot.Entries.Select(static entry => new MecmBaselineRow(entry)).ToArray();
        _baselinesLoaded = true;

        VisibleBaselines.Clear();
        foreach (var row in _allBaselines)
        {
            VisibleBaselines.Add(row);
        }

        SelectedBaseline = preferredBaseline.HasValue
            ? VisibleBaselines.FirstOrDefault(row =>
                string.Equals(row.Name, preferredBaseline.Value.Name, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(row.Version, preferredBaseline.Value.Version, StringComparison.OrdinalIgnoreCase) &&
                row.IsMachineTarget == preferredBaseline.Value.IsMachineTarget)
            : VisibleBaselines.FirstOrDefault();

        if (SelectedBaseline is null)
        {
            VisibleBaselineConfigItems.Clear();
        }
        else
        {
            await LoadBaselineDetailsAsync(host, SelectedBaseline, cancellationToken);
        }

        Status = BuildLoadStatus($"Loaded {_allBaselines.Count} MECM baseline(s).", snapshot.Warnings);
    }

    private async Task LoadBaselineDetailsAsync(string host, MecmBaselineRow baseline, CancellationToken cancellationToken)
    {
        var details = await _mecmClientService.GetBaselineDetailsAsync(
            host,
            baseline.Name,
            baseline.Version,
            baseline.IsMachineTarget,
            cancellationToken);

        VisibleBaselineConfigItems.Clear();
        foreach (var row in details.ConfigItems.Select(static item => new MecmBaselineConfigItemRow(item)))
        {
            VisibleBaselineConfigItems.Add(row);
        }

        var baseMessage = VisibleBaselineConfigItems.Count == 0
            ? $"No config items were returned for baseline '{baseline.DisplayName}'."
            : $"Loaded {VisibleBaselineConfigItems.Count} config item(s) for baseline '{baseline.DisplayName}'.";
        Status = BuildLoadStatus(baseMessage, details.Warnings);
    }

    private void ApplyApplicationsFilter()
    {
        var rows = HideNonUserUiExperienceApps
            ? _allApplications.Where(static row => row.Entry.UserUiExperience)
            : _allApplications;

        VisibleApplications.Clear();
        foreach (var row in rows)
        {
            VisibleApplications.Add(row);
        }
    }

    private void ApplyAllUpdatesFilter()
    {
        IEnumerable<MecmAllUpdateRow> rows = _allUpdates;

        if (ShowMissingUpdatesOnly)
        {
            rows = rows.Where(static row =>
                row.Status.Contains("missing", StringComparison.OrdinalIgnoreCase) ||
                row.Status.Contains("required", StringComparison.OrdinalIgnoreCase));
        }

        if (HideDuplicateUpdates)
        {
            rows = rows
                .GroupBy(static row => $"{row.Article}|{row.Bulletin}|{row.Title}|{row.Language}", StringComparer.OrdinalIgnoreCase)
                .Select(static group => group
                    .OrderByDescending(row => row.RevisionNumber ?? 0)
                    .ThenBy(row => row.UniqueId, StringComparer.OrdinalIgnoreCase)
                    .First());
        }

        rows = rows
            .OrderBy(static row => row.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static row => row.Article, StringComparer.OrdinalIgnoreCase);

        VisibleAllUpdates.Clear();
        foreach (var row in rows)
        {
            VisibleAllUpdates.Add(row);
        }
    }

    private async Task ExecuteApplicationActionAsync(MecmApplicationAction action)
    {
        var host = _targetHostService.CurrentHost?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(host))
        {
            Status = DisconnectedStatus;
            return;
        }

        var rows = GetSelectedApplications();
        if (rows.Count == 0)
        {
            return;
        }

        var selection = _targetHostService.CaptureSelection();
        using var linkedCancellationTokenSource = CreateHostLinkedCancellation(selection, CancellationToken.None);

        try
        {
            IsBusy = true;
            foreach (var row in rows)
            {
                var result = await _mecmClientService.ExecuteApplicationActionAsync(
                    host,
                    row.Entry.Id,
                    row.Entry.Revision,
                    row.Entry.IsMachineTarget,
                    action,
                    linkedCancellationTokenSource.Token);

                Status = result.Message;
                if (!result.Success)
                {
                    return;
                }
            }

            await LoadApplicationsAsync(host, linkedCancellationTokenSource.Token);
        }
        catch (OperationCanceledException) when (selection.CancellationToken.IsCancellationRequested)
        {
            Status = "Operation canceled because the target host changed.";
        }
        catch (Exception ex)
        {
            Status = $"{action} failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ExecuteApplicationEvaluationAsync(MecmApplicationEvaluationMode mode)
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
            var result = await _mecmClientService.TriggerApplicationEvaluationAsync(host, mode, linkedCancellationTokenSource.Token);
            Status = result.Message;
        }
        catch (OperationCanceledException) when (selection.CancellationToken.IsCancellationRequested)
        {
            Status = "Operation canceled because the target host changed.";
        }
        catch (Exception ex)
        {
            Status = $"MECM application evaluation failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ExecuteUpdateInstallAsync(MecmUpdateInstallRequest request)
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
            var result = await _mecmClientService.InstallUpdatesAsync(host, request, linkedCancellationTokenSource.Token);
            Status = result.Message;
            if (!result.Success)
            {
                return;
            }

            switch (CurrentSection)
            {
                case MecmSection.PendingUpdates:
                    await LoadPendingUpdatesAsync(host, linkedCancellationTokenSource.Token);
                    break;
                case MecmSection.AllUpdates:
                    await LoadAllUpdatesAsync(host, linkedCancellationTokenSource.Token);
                    break;
                default:
                    await LoadCurrentSectionAsync(forceReload: true, linkedCancellationTokenSource.Token);
                    break;
            }
        }
        catch (OperationCanceledException) when (selection.CancellationToken.IsCancellationRequested)
        {
            Status = "Operation canceled because the target host changed.";
        }
        catch (Exception ex)
        {
            Status = $"MECM update installation failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanInstallSelectedApplications() => CanExecuteApplicationAction("Install");
    private bool CanRepairSelectedApplications() => CanExecuteApplicationAction("Repair");
    private bool CanUninstallSelectedApplications() => CanExecuteApplicationAction("Uninstall");

    private string? GetApplicationActionTooltip(string actionName)
    {
        if (IsBusy)
        {
            return "Another MECM action is still running.";
        }

        if (string.IsNullOrWhiteSpace(_targetHostService.CurrentHost))
        {
            return "Select a device first.";
        }

        var rows = GetSelectedApplications();
        if (rows.Count == 0)
        {
            return "Select at least one application first.";
        }

        var unsupportedRows = rows
            .Where(row => !SupportsApplicationAction(row, actionName))
            .ToArray();

        if (unsupportedRows.Length == 0)
        {
            return $"{actionName} the selected MECM application deployment.";
        }

        return string.Join(
            " ",
            unsupportedRows
                .Select(row => DescribeUnsupportedApplicationAction(row, actionName))
                .Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private bool CanExecuteApplicationAction(string actionName)
    {
        var rows = GetSelectedApplications();
        return !IsBusy &&
               rows.Count > 0 &&
               rows.All(row => SupportsApplicationAction(row, actionName)) &&
               !string.IsNullOrWhiteSpace(_targetHostService.CurrentHost);
    }

    private bool CanTriggerApplicationEvaluation()
    {
        return !IsBusy && !string.IsNullOrWhiteSpace(_targetHostService.CurrentHost);
    }

    private static bool SupportsApplicationAction(MecmApplicationRow row, string actionName)
    {
        return actionName switch
        {
            "Install" => row.AllowedActionSet.Contains(actionName) || IsInstallableByState(row),
            "Uninstall" => row.AllowedActionSet.Contains(actionName) || IsInstalledByState(row),
            _ => row.AllowedActionSet.Contains(actionName)
        };
    }

    private static string DescribeUnsupportedApplicationAction(MecmApplicationRow row, string actionName)
    {
        return actionName switch
        {
            "Install" when IsInstalledByState(row) => $"{row.Name} is already installed.",
            "Install" => $"{row.Name} is not in an installable MECM state.",
            "Uninstall" when !IsInstalledByState(row) => $"{row.Name} is not installed.",
            "Uninstall" => $"{row.Name} is not in an uninstallable MECM state.",
            "Repair" when !IsInstalledByState(row) => $"{row.Name} is not installed.",
            "Repair" => $"{row.Name} does not advertise repair.",
            _ => $"{row.Name} does not allow '{actionName}'."
        };
    }

    private static bool IsInstallableByState(MecmApplicationRow row)
    {
        if (IsInstalledByState(row))
        {
            return false;
        }

        return IsNormalizedState(row.Entry.InstallState, "notinstalled") ||
               IsNormalizedState(row.Entry.ResolvedState, "available") ||
               row.Entry.ApplicabilityState.Equals("Applicable", StringComparison.OrdinalIgnoreCase) ||
               row.Entry.EvaluationStateText.Contains("available for enforcement", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsInstalledByState(MecmApplicationRow row)
    {
        return IsNormalizedState(row.Entry.InstallState, "installed") ||
               IsNormalizedState(row.Entry.ResolvedState, "installed") ||
               row.Entry.EvaluationStateText.Contains("desired/resolved state", StringComparison.OrdinalIgnoreCase) ||
               row.Entry.EvaluationStateText.Contains("install/uninstall enforced", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsNormalizedState(string? value, string expected)
    {
        return string.Equals(NormalizeState(value), expected, StringComparison.Ordinal);
    }

    private static string NormalizeState(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return new string(value.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
    }

    private bool CanInstallSelectedPendingUpdates()
    {
        return !IsBusy &&
               GetSelectedPendingUpdates().Count > 0 &&
               !string.IsNullOrWhiteSpace(_targetHostService.CurrentHost);
    }

    private bool CanInstallAllMandatoryUpdates()
    {
        return !IsBusy && !string.IsNullOrWhiteSpace(_targetHostService.CurrentHost);
    }

    private bool CanInstallAllApprovedUpdates()
    {
        return !IsBusy && !string.IsNullOrWhiteSpace(_targetHostService.CurrentHost);
    }

    private bool CanExecuteSelectedPackages()
    {
        return !IsBusy &&
               GetSelectedPackages().Count > 0 &&
               !string.IsNullOrWhiteSpace(_targetHostService.CurrentHost);
    }

    private bool CanEvaluateSelectedBaseline()
    {
        return !IsBusy &&
               SelectedBaseline is not null &&
               !string.IsNullOrWhiteSpace(_targetHostService.CurrentHost);
    }

    private IReadOnlyList<MecmApplicationRow> GetSelectedApplications()
    {
        return _selectedApplications.Count > 0
            ? _selectedApplications
            : SelectedApplication is not null ? [SelectedApplication] : [];
    }

    private IReadOnlyList<MecmPendingUpdateRow> GetSelectedPendingUpdates()
    {
        return _selectedPendingUpdates.Count > 0
            ? _selectedPendingUpdates
            : SelectedPendingUpdate is not null ? [SelectedPendingUpdate] : [];
    }

    private IReadOnlyList<MecmPackageRow> GetSelectedPackages()
    {
        return _selectedPackages.Count > 0
            ? _selectedPackages
            : SelectedPackage is not null ? [SelectedPackage] : [];
    }

    private async Task ExecutePackageActionAsync()
    {
        var host = _targetHostService.CurrentHost?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(host))
        {
            Status = DisconnectedStatus;
            return;
        }

        var rows = GetSelectedPackages();
        if (rows.Count == 0)
        {
            return;
        }

        var selection = _targetHostService.CaptureSelection();
        using var linkedCancellationTokenSource = CreateHostLinkedCancellation(selection, CancellationToken.None);

        try
        {
            IsBusy = true;
            foreach (var row in rows)
            {
                var result = await _mecmClientService.ExecutePackageAsync(host, row.AdvertisementId, linkedCancellationTokenSource.Token);
                Status = result.Message;
                if (!result.Success)
                {
                    return;
                }
            }

            await LoadPackagesAsync(host, linkedCancellationTokenSource.Token);
        }
        catch (OperationCanceledException) when (selection.CancellationToken.IsCancellationRequested)
        {
            Status = "Operation canceled because the target host changed.";
        }
        catch (Exception ex)
        {
            Status = $"MECM package execution failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ExecuteBaselineEvaluationAsync()
    {
        var host = _targetHostService.CurrentHost?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(host))
        {
            Status = DisconnectedStatus;
            return;
        }

        if (SelectedBaseline is null)
        {
            return;
        }

        var selection = _targetHostService.CaptureSelection();
        using var linkedCancellationTokenSource = CreateHostLinkedCancellation(selection, CancellationToken.None);

        try
        {
            IsBusy = true;
            var result = await _mecmClientService.TriggerBaselineEvaluationAsync(
                host,
                SelectedBaseline.Name,
                SelectedBaseline.Version,
                SelectedBaseline.IsMachineTarget,
                enforce: true,
                linkedCancellationTokenSource.Token);
            Status = result.Message;
            if (!result.Success)
            {
                return;
            }

            await LoadBaselinesAsync(host, linkedCancellationTokenSource.Token);
        }
        catch (OperationCanceledException) when (selection.CancellationToken.IsCancellationRequested)
        {
            Status = "Operation canceled because the target host changed.";
        }
        catch (Exception ex)
        {
            Status = $"MECM baseline evaluation failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void OpenRemoteLog(string relativePath, string label)
    {
        var host = _targetHostService.CurrentHost?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(host))
        {
            Status = DisconnectedStatus;
            return;
        }

        var resolvedPath = $@"\\{host}\{relativePath}";
        try
        {
            _ = Process.Start(new ProcessStartInfo
            {
                FileName = resolvedPath,
                UseShellExecute = true
            }) ?? throw new InvalidOperationException("Failed to start Explorer.");
            Status = $"Opened {label} on '{host}'.";
        }
        catch (Exception ex)
        {
            Status = $"Opening {label} failed: {ex.Message}";
        }
    }

    private void OnHostChanged(object? sender, string host)
    {
        ClearLoadedState();
        CurrentHost = host;
        StartBackgroundLoad(LoadCurrentSectionAsync(forceReload: true, CancellationToken.None), "reacting to host change");
    }

    private async void StartBackgroundLoad(Task task, string operationName)
    {
        try
        {
            await task;
        }
        catch (Exception ex)
        {
            Status = $"MECM background operation failed: {ex.Message}";
            _logger?.LogError(ex, "MECM background operation failed while {OperationName}.", operationName);
        }
    }

    private void ClearLoadedState()
    {
        ClearOverviewState();
        _applicationsLoaded = false;
        _pendingUpdatesLoaded = false;
        _allUpdatesLoaded = false;
        _packagesLoaded = false;
        _baselinesLoaded = false;
        _allApplications = [];
        _allPendingUpdates = [];
        _allUpdates = [];
        _allPackages = [];
        _allBaselines = [];
        _selectedApplications.Clear();
        _selectedPendingUpdates.Clear();
        _selectedPackages.Clear();
        VisibleApplications.Clear();
        VisiblePendingUpdates.Clear();
        VisibleAllUpdates.Clear();
        VisiblePackages.Clear();
        VisibleBaselines.Clear();
        VisibleBaselineConfigItems.Clear();
        SelectedApplication = null;
        SelectedPendingUpdate = null;
        SelectedPackage = null;
        SelectedBaseline = null;
    }

    private void NotifyApplicationCommandStates()
    {
        InstallApplicationsCommand.NotifyCanExecuteChanged();
        RepairApplicationsCommand.NotifyCanExecuteChanged();
        UninstallApplicationsCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(InstallApplicationsCommandTooltip));
        OnPropertyChanged(nameof(RepairApplicationsCommandTooltip));
        OnPropertyChanged(nameof(UninstallApplicationsCommandTooltip));
    }

    private void NotifyPendingUpdateCommandStates()
    {
        InstallSelectedPendingUpdatesCommand.NotifyCanExecuteChanged();
    }

    private void NotifyPackageCommandStates()
    {
        ExecuteSelectedPackagesCommand.NotifyCanExecuteChanged();
    }

    private void NotifyBaselineCommandStates()
    {
        EvaluateSelectedBaselineCommand.NotifyCanExecuteChanged();
    }

    private CancellationTokenSource CreateHostLinkedCancellation(HostSelection selection, CancellationToken cancellationToken)
    {
        return cancellationToken.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(selection.CancellationToken, cancellationToken)
            : CancellationTokenSource.CreateLinkedTokenSource(selection.CancellationToken);
    }

    private static string BuildLoadStatus(string baseMessage, IReadOnlyList<string> warnings)
    {
        return warnings.Count == 0
            ? baseMessage
            : $"{baseMessage} Warnings: {string.Join(" ", warnings)}";
    }
}
