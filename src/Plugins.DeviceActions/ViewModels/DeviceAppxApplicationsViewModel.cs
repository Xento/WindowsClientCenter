using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WindowsClientCenter.Intune.Services.Contracts;
using WindowsClientCenter.Intune.Services.Models;
using WindowsClientCenter.Plugin.Abstractions.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace WindowsClientCenter.Plugins.DeviceActions.ViewModels;

public partial class DeviceAppxApplicationsViewModel : ObservableObject, IDisposable
{
    private const string DisconnectedStatus = "Client is not connected. Click Connect first.";
    private readonly IAppxPackageManager _packageManager;
    private readonly ITargetHostService _targetHostService;
    private readonly IHostStatusLogSink? _hostStatusLogSink;
    private readonly Func<string, string, bool> _confirmAction;
    private readonly List<AppxPackageEntry> _allPackages = [];
    private readonly List<WingetCatalogEntry> _allWingetResults = [];
    private string _lastForwardedStatusLine = string.Empty;

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = DisconnectedStatus;
    [ObservableProperty] private string _hostText = string.Empty;
    [ObservableProperty] private string _activeUserText = "No active user";
    [ObservableProperty] private string _activeUserSid = string.Empty;
    [ObservableProperty] private string _packageFilter = string.Empty;
    [ObservableProperty] private bool _showFrameworks;
    [ObservableProperty] private bool _showResources;
    [ObservableProperty] private bool _showOptional;
    [ObservableProperty] private bool _showNonRemovable;
    [ObservableProperty] private AppxPackageEntry? _selectedPackage;
    [ObservableProperty] private AppxUserRegistration? _selectedUser;
    [ObservableProperty] private string _wingetQuery = string.Empty;
    [ObservableProperty] private string _wingetSourceFilter = "All";
    [ObservableProperty] private WingetCatalogEntry? _selectedWingetResult;

    public ObservableCollection<AppxPackageEntry> Packages { get; } = [];
    public ObservableCollection<WingetCatalogEntry> WingetResults { get; } = [];
    public IReadOnlyList<string> WingetSources { get; } = ["All", "winget", "msstore"];

    public DeviceAppxApplicationsViewModel(IPluginContext pluginContext, Func<string, string, bool>? confirmAction = null)
    {
        _packageManager = pluginContext.Services.GetRequiredService<IAppxPackageManager>();
        _targetHostService = pluginContext.Services.GetRequiredService<ITargetHostService>();
        _hostStatusLogSink = pluginContext.Services.GetService<IHostStatusLogSink>();
        _confirmAction = confirmAction ?? ConfirmViaMessageBox;
        _targetHostService.HostChanged += OnHostChanged;
    }

    public void Dispose() => _targetHostService.HostChanged -= OnHostChanged;

    partial void OnStatusChanged(string value) => ForwardStatusToHost(value);
    partial void OnIsBusyChanged(bool value) => NotifyCommandStates();
    partial void OnPackageFilterChanged(string value) => ApplyPackageFilter();
    partial void OnShowFrameworksChanged(bool value) => ApplyPackageFilter();
    partial void OnShowResourcesChanged(bool value) => ApplyPackageFilter();
    partial void OnShowOptionalChanged(bool value) => ApplyPackageFilter();
    partial void OnShowNonRemovableChanged(bool value) => ApplyPackageFilter();
    partial void OnWingetSourceFilterChanged(string value) => ApplyWingetSourceFilter();

    partial void OnSelectedPackageChanged(AppxPackageEntry? value)
    {
        SelectedUser = value?.Users.FirstOrDefault(user => user.IsActiveUser) ?? value?.Users.FirstOrDefault();
        NotifyCommandStates();
    }

    partial void OnSelectedUserChanged(AppxUserRegistration? value) => NotifyCommandStates();
    partial void OnSelectedWingetResultChanged(WingetCatalogEntry? value) => NotifyCommandStates();

    [RelayCommand]
    public Task RefreshAsync() => LoadAsync(CancellationToken.None);

    [RelayCommand(CanExecute = nameof(CanSearchWinget))]
    public async Task SearchWingetAsync()
    {
        var selection = _targetHostService.CaptureSelection();
        var host = selection.Host?.Trim() ?? string.Empty;
        if (host.Length == 0)
        {
            Status = DisconnectedStatus;
            return;
        }

        IsBusy = true;
        using var linkedCancellation = CreateHostLinkedCancellation(selection, CancellationToken.None);
        try
        {
            var result = await _packageManager.SearchWingetAsync(host, WingetQuery, linkedCancellation.Token);
            if (!EnsureCurrentSelection(selection))
            {
                return;
            }

            _allWingetResults.Clear();
            _allWingetResults.AddRange(result.Entries);
            ApplyWingetSourceFilter();
            Status = result.Warnings.Count == 0
                ? $"Found {result.Entries.Count} WinGet package(s)."
                : $"Found {result.Entries.Count} WinGet package(s) with warning(s): {string.Join(" ", result.Warnings)}";
        }
        catch (OperationCanceledException) when (selection.CancellationToken.IsCancellationRequested)
        {
            Status = "WinGet search canceled because the target host changed.";
        }
        catch (Exception ex)
        {
            Status = $"WinGet search failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRunWingetAction))]
    public Task InstallMachineAsync() => ExecuteWingetActionAsync("install", WingetInstallScope.Machine);

    [RelayCommand(CanExecute = nameof(CanRunActiveUserWingetAction))]
    public Task InstallForActiveUserAsync() => ExecuteWingetActionAsync("install", WingetInstallScope.ActiveUser);

    [RelayCommand(CanExecute = nameof(CanRunWingetAction))]
    public Task UpgradeMachineAsync() => ExecuteWingetActionAsync("upgrade", WingetInstallScope.Machine);

    [RelayCommand(CanExecute = nameof(CanRunActiveUserWingetAction))]
    public Task UpgradeForActiveUserAsync() => ExecuteWingetActionAsync("upgrade", WingetInstallScope.ActiveUser);

    [RelayCommand(CanExecute = nameof(CanRemoveForSelectedUser))]
    public Task RemoveForSelectedUserAsync() => ExecutePackageActionAsync(
        "Remove AppX registration",
        package => $"Remove '{package.PackageFullName}' for '{SelectedUser?.UserName}'?",
        (host, package, token) => _packageManager.RemoveForUserAsync(host, package.PackageFullName, SelectedUser!.UserSid, token));

    [RelayCommand(CanExecute = nameof(CanRemoveForAllUsers))]
    public Task RemoveForAllUsersAsync() => ExecutePackageActionAsync(
        "Remove AppX for all users",
        package => $"Remove '{package.PackageFullName}' for every registered user on '{HostText}'?",
        (host, package, token) => _packageManager.RemoveForAllUsersAsync(host, package.PackageFullName, token));

    [RelayCommand(CanExecute = nameof(CanRemoveProvisioning))]
    public Task RemoveProvisioningAsync() => ExecutePackageActionAsync(
        "Remove AppX provisioning",
        package => $"Remove provisioning for '{package.ProvisionedPackageName}'? New user profiles will no longer receive this app.",
        (host, package, token) => _packageManager.RemoveProvisioningAsync(host, package.ProvisionedPackageName, token));

    [RelayCommand(CanExecute = nameof(CanRegisterForActiveUser))]
    public Task RegisterForActiveUserAsync() => ExecutePackageActionAsync(
        "Register AppX for active user",
        package => $"Register '{package.PackageFullName}' for active user '{ActiveUserText}'?",
        (host, package, token) => _packageManager.RegisterForActiveUserAsync(host, package.PackageFullName, token));

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        var selection = _targetHostService.CaptureSelection();
        var host = selection.Host?.Trim() ?? string.Empty;
        HostText = host;
        if (host.Length == 0)
        {
            ClearInventory();
            Status = DisconnectedStatus;
            return;
        }

        IsBusy = true;
        using var linkedCancellation = CreateHostLinkedCancellation(selection, cancellationToken);
        var previousPackage = SelectedPackage?.PackageFullName;
        try
        {
            var snapshot = await _packageManager.GetPackagesAsync(host, linkedCancellation.Token);
            if (!EnsureCurrentSelection(selection))
            {
                return;
            }

            ActiveUserSid = snapshot.ActiveUserSid;
            ActiveUserText = string.IsNullOrWhiteSpace(snapshot.ActiveUserName) ? "No active user" : snapshot.ActiveUserName;
            _allPackages.Clear();
            _allPackages.AddRange(snapshot.Packages);
            ApplyPackageFilter(previousPackage);
            Status = snapshot.Warnings.Count == 0
                ? $"Loaded {snapshot.Packages.Count} AppX package(s)."
                : $"Loaded {snapshot.Packages.Count} AppX package(s) with warning(s): {string.Join(" ", snapshot.Warnings)}";
        }
        catch (OperationCanceledException) when (selection.CancellationToken.IsCancellationRequested)
        {
            Status = "AppX inventory canceled because the target host changed.";
        }
        catch (Exception ex)
        {
            ClearInventory();
            Status = $"Failed to load AppX packages: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ExecuteWingetActionAsync(string verb, WingetInstallScope scope)
    {
        var package = SelectedWingetResult;
        if (package is null)
        {
            Status = "Select a WinGet search result first.";
            return;
        }

        var scopeLabel = scope == WingetInstallScope.Machine ? "machine" : $"active user '{ActiveUserText}'";
        if (!_confirmAction($"Confirm WinGet {verb}", $"Run WinGet {verb} for exact ID '{package.Id}' from '{package.Source}' in the {scopeLabel} context?"))
        {
            Status = $"WinGet {verb} cancelled for '{package.Id}'.";
            return;
        }

        await ExecuteActionAsync(async (host, token) => verb == "install"
            ? await _packageManager.InstallWingetAsync(host, package, scope, token)
            : await _packageManager.UpgradeWingetAsync(host, package, scope, token));
    }

    private async Task ExecutePackageActionAsync(
        string title,
        Func<AppxPackageEntry, string> confirmationText,
        Func<string, AppxPackageEntry, CancellationToken, ValueTask<DeviceActionResult>> action)
    {
        var package = SelectedPackage;
        if (package is null)
        {
            Status = "Select an AppX package first.";
            return;
        }

        if (!_confirmAction(title, confirmationText(package)))
        {
            Status = $"Action cancelled for '{package.EffectiveDisplayName}'.";
            return;
        }

        await ExecuteActionAsync((host, token) => action(host, package, token), refreshInventory: true);
    }

    private async Task ExecuteActionAsync(Func<string, CancellationToken, ValueTask<DeviceActionResult>> action, bool refreshInventory = false)
    {
        var selection = _targetHostService.CaptureSelection();
        var host = selection.Host?.Trim() ?? string.Empty;
        if (host.Length == 0)
        {
            Status = DisconnectedStatus;
            return;
        }

        IsBusy = true;
        using var linkedCancellation = CreateHostLinkedCancellation(selection, CancellationToken.None);
        try
        {
            var result = await action(host, linkedCancellation.Token);
            if (!EnsureCurrentSelection(selection))
            {
                Status = "Operation canceled because the target host changed.";
                return;
            }

            Status = result.Message;
            if (result.Success && refreshInventory)
            {
                await LoadAsync(linkedCancellation.Token);
            }
        }
        catch (OperationCanceledException) when (selection.CancellationToken.IsCancellationRequested)
        {
            Status = "Operation canceled because the target host changed.";
        }
        catch (Exception ex)
        {
            Status = $"AppX operation failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanSearchWinget() => !IsBusy && WingetQuery.Trim().Length >= 2 && !string.IsNullOrWhiteSpace(_targetHostService.CurrentHost);
    private bool CanRunWingetAction() => !IsBusy && SelectedWingetResult is not null && !string.IsNullOrWhiteSpace(_targetHostService.CurrentHost);
    private bool CanRunActiveUserWingetAction() => CanRunWingetAction() && !string.IsNullOrWhiteSpace(ActiveUserSid);
    private bool CanRemoveForSelectedUser() => CanRunPackageAction() && SelectedPackage?.NonRemovable == false && SelectedUser is not null;
    private bool CanRemoveForAllUsers() => CanRunPackageAction() && SelectedPackage?.NonRemovable == false && SelectedPackage.Users.Count > 0;
    private bool CanRemoveProvisioning() => CanRunPackageAction() && SelectedPackage?.IsProvisioned == true && !string.IsNullOrWhiteSpace(SelectedPackage.ProvisionedPackageName);
    private bool CanRegisterForActiveUser() => CanRunPackageAction() && !string.IsNullOrWhiteSpace(ActiveUserSid) && !string.IsNullOrWhiteSpace(SelectedPackage?.InstallLocation);
    private bool CanRunPackageAction() => !IsBusy && SelectedPackage is not null && !string.IsNullOrWhiteSpace(_targetHostService.CurrentHost);

    partial void OnWingetQueryChanged(string value) => SearchWingetCommand.NotifyCanExecuteChanged();
    partial void OnActiveUserSidChanged(string value) => NotifyCommandStates();

    private void ApplyPackageFilter(string? preferredPackageFullName = null)
    {
        preferredPackageFullName ??= SelectedPackage?.PackageFullName;
        var filter = PackageFilter.Trim();
        var filtered = _allPackages.Where(package =>
            (ShowFrameworks || !package.IsFramework) &&
            (ShowResources || !package.IsResourcePackage) &&
            (ShowOptional || !package.IsOptional) &&
            (ShowNonRemovable || !package.NonRemovable) &&
            (filter.Length == 0 || package.EffectiveDisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase) || package.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) || package.PackageFullName.Contains(filter, StringComparison.OrdinalIgnoreCase)));

        Packages.Clear();
        foreach (var package in filtered)
        {
            Packages.Add(package);
        }

        SelectedPackage = Packages.FirstOrDefault(package => string.Equals(package.PackageFullName, preferredPackageFullName, StringComparison.OrdinalIgnoreCase)) ?? Packages.FirstOrDefault();
    }

    private void ApplyWingetSourceFilter()
    {
        var preferred = SelectedWingetResult is null ? string.Empty : $"{SelectedWingetResult.Source}|{SelectedWingetResult.Id}";
        WingetResults.Clear();
        foreach (var entry in _allWingetResults.Where(entry => WingetSourceFilter == "All" || string.Equals(entry.Source, WingetSourceFilter, StringComparison.OrdinalIgnoreCase)))
        {
            WingetResults.Add(entry);
        }

        SelectedWingetResult = WingetResults.FirstOrDefault(entry => string.Equals($"{entry.Source}|{entry.Id}", preferred, StringComparison.OrdinalIgnoreCase)) ?? WingetResults.FirstOrDefault();
    }

    private void ClearInventory()
    {
        _allPackages.Clear();
        Packages.Clear();
        SelectedPackage = null;
        ActiveUserSid = string.Empty;
        ActiveUserText = "No active user";
    }

    private void NotifyCommandStates()
    {
        SearchWingetCommand.NotifyCanExecuteChanged();
        InstallMachineCommand.NotifyCanExecuteChanged();
        InstallForActiveUserCommand.NotifyCanExecuteChanged();
        UpgradeMachineCommand.NotifyCanExecuteChanged();
        UpgradeForActiveUserCommand.NotifyCanExecuteChanged();
        RemoveForSelectedUserCommand.NotifyCanExecuteChanged();
        RemoveForAllUsersCommand.NotifyCanExecuteChanged();
        RemoveProvisioningCommand.NotifyCanExecuteChanged();
        RegisterForActiveUserCommand.NotifyCanExecuteChanged();
    }

    private void OnHostChanged(object? sender, string host)
    {
        HostText = host;
        _ = LoadAsync(CancellationToken.None);
    }

    private CancellationTokenSource CreateHostLinkedCancellation(HostSelection selection, CancellationToken cancellationToken) => cancellationToken.CanBeCanceled
        ? CancellationTokenSource.CreateLinkedTokenSource(selection.CancellationToken, cancellationToken)
        : CancellationTokenSource.CreateLinkedTokenSource(selection.CancellationToken);

    private bool EnsureCurrentSelection(HostSelection selection) => _targetHostService.IsCurrent(selection);

    private void ForwardStatusToHost(string message)
    {
        if (_hostStatusLogSink is null || string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        var normalized = message.Trim();
        if (string.Equals(_lastForwardedStatusLine, normalized, StringComparison.Ordinal))
        {
            return;
        }

        _lastForwardedStatusLine = normalized;
        _hostStatusLogSink.Append($"[AppX Applications] {normalized}");
    }

    private static bool ConfirmViaMessageBox(string title, string message) =>
        MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
}
