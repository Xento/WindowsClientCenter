using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WindowsClientCenter.Intune.Services.Contracts;
using WindowsClientCenter.Intune.Services.Models;
using WindowsClientCenter.Plugin.Abstractions.Contracts;
using WindowsClientCenter.Plugins.DeviceActions.Models;
using Microsoft.Extensions.DependencyInjection;

namespace WindowsClientCenter.Plugins.DeviceActions.ViewModels;

public partial class DeviceProfilesViewModel : ObservableObject, IDisposable
{
    private const string DisconnectedStatus = "Client is not connected. Click Connect first.";
    private readonly IWindowsProfileManager _windowsProfileManager;
    private readonly ITargetHostService _targetHostService;
    private readonly IHostStatusLogSink? _hostStatusLogSink;
    private readonly Func<string, string, bool> _confirmAction;
    private readonly Dictionary<string, WindowsProfileSizeResult> _rawSizeCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, WindowsProfileSizeResult> _policySizeCache = new(StringComparer.OrdinalIgnoreCase);
    private string _lastForwardedStatusLine = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _status = DisconnectedStatus;

    [ObservableProperty]
    private string _hostText = string.Empty;

    [ObservableProperty]
    private DeviceProfilePresentation? _selectedProfile;

    [ObservableProperty]
    private string _policySource = "Not configured";

    [ObservableProperty]
    private string _maxProfileSizeText = "Not configured";

    [ObservableProperty]
    private string _includesRegistryInQuotaText = "Not configured";

    [ObservableProperty]
    private string _excludedPathsText = "None";

    public ObservableCollection<DeviceProfilePresentation> Profiles { get; } = [];

    public DeviceProfilesViewModel(IPluginContext pluginContext, Func<string, string, bool>? confirmAction = null)
    {
        _windowsProfileManager = pluginContext.Services.GetRequiredService<IWindowsProfileManager>();
        _targetHostService = pluginContext.Services.GetRequiredService<ITargetHostService>();
        _hostStatusLogSink = pluginContext.Services.GetService<IHostStatusLogSink>();
        _confirmAction = confirmAction ?? ConfirmViaMessageBox;
        _targetHostService.HostChanged += OnHostChanged;
    }

    public void Dispose()
    {
        _targetHostService.HostChanged -= OnHostChanged;
    }

    partial void OnStatusChanged(string value)
    {
        ForwardStatusToHost(value);
    }

    partial void OnSelectedProfileChanged(DeviceProfilePresentation? value)
    {
        CalculateRawSizeCommand.NotifyCanExecuteChanged();
        CalculatePolicySizeCommand.NotifyCanExecuteChanged();
        DeleteSelectedProfileCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsBusyChanged(bool value)
    {
        RefreshCommand.NotifyCanExecuteChanged();
        CalculateRawSizeCommand.NotifyCanExecuteChanged();
        CalculatePolicySizeCommand.NotifyCanExecuteChanged();
        DeleteSelectedProfileCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    public Task RefreshAsync()
    {
        return LoadAsync(CancellationToken.None);
    }

    [RelayCommand(CanExecute = nameof(CanCalculateSelectedProfileSize))]
    public Task CalculateRawSizeAsync()
    {
        return CalculateSelectedProfileSizeAsync(ProfileSizeCalculationMode.Raw);
    }

    [RelayCommand(CanExecute = nameof(CanCalculateSelectedProfileSize))]
    public Task CalculatePolicySizeAsync()
    {
        return CalculateSelectedProfileSizeAsync(ProfileSizeCalculationMode.PolicyExcluded);
    }

    [RelayCommand(CanExecute = nameof(CanDeleteSelectedProfile))]
    public async Task DeleteSelectedProfileAsync()
    {
        var selection = _targetHostService.CaptureSelection();
        var host = selection.Host?.Trim() ?? string.Empty;
        var profile = SelectedProfile;
        HostText = host;

        if (string.IsNullOrWhiteSpace(host))
        {
            Status = DisconnectedStatus;
            return;
        }

        if (profile is null)
        {
            return;
        }

        if (!_confirmAction("Confirm profile delete", BuildDeleteConfirmationMessage(profile)))
        {
            Status = $"Delete action cancelled for profile {profile.AccountName}.";
            return;
        }

        IsBusy = true;
        using var linkedCancellationTokenSource = CreateHostLinkedCancellation(selection, CancellationToken.None);
        try
        {
            Status = $"Deleting profile {profile.AccountName}...";
            var result = await _windowsProfileManager.DeleteProfileAsync(host, profile.Sid, profile.LocalPath, linkedCancellationTokenSource.Token);
            if (!EnsureCurrentSelection(selection))
            {
                Status = "Operation canceled because the target host changed.";
                return;
            }

            Status = result.Message;
            if (result.Success)
            {
                _rawSizeCache.Remove(profile.LocalPath);
                _policySizeCache.Remove(profile.LocalPath);
                await LoadAsync(linkedCancellationTokenSource.Token);
            }
        }
        catch (OperationCanceledException) when (selection.CancellationToken.IsCancellationRequested)
        {
            Status = "Operation canceled because the target host changed.";
        }
        catch (Exception ex)
        {
            Status = $"Profile deletion failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        var selection = _targetHostService.CaptureSelection();
        var host = selection.Host?.Trim() ?? string.Empty;
        HostText = host;

        if (string.IsNullOrWhiteSpace(host))
        {
            ClearProfiles(clearCaches: true);
            Status = DisconnectedStatus;
            return;
        }

        IsBusy = true;
        using var linkedCancellationTokenSource = CreateHostLinkedCancellation(selection, cancellationToken);
        var previousPath = SelectedProfile?.LocalPath;
        try
        {
            var snapshot = await _windowsProfileManager.GetProfilesAsync(host, linkedCancellationTokenSource.Token);
            if (!EnsureCurrentSelection(selection))
            {
                return;
            }

            ApplySnapshot(snapshot, previousPath);
            Status = snapshot.Warnings.Count > 0 && snapshot.Profiles.Count == 0
                ? $"Failed to load profiles: {string.Join(" ", snapshot.Warnings)}"
                : $"Loaded {snapshot.Profiles.Count} profile(s).";
        }
        catch (OperationCanceledException) when (selection.CancellationToken.IsCancellationRequested)
        {
            Status = "Operation canceled because the target host changed.";
        }
        catch (Exception ex)
        {
            ClearProfiles(clearCaches: false);
            Status = $"Failed to load profiles: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanRefresh() => !IsBusy;

    private bool CanCalculateSelectedProfileSize()
    {
        return !IsBusy &&
               SelectedProfile is not null &&
               !string.IsNullOrWhiteSpace(_targetHostService.CurrentHost);
    }

    private bool CanDeleteSelectedProfile()
    {
        return !IsBusy &&
               SelectedProfile is not null &&
               !string.IsNullOrWhiteSpace(_targetHostService.CurrentHost);
    }

    private async Task CalculateSelectedProfileSizeAsync(ProfileSizeCalculationMode mode)
    {
        var selection = _targetHostService.CaptureSelection();
        var host = selection.Host?.Trim() ?? string.Empty;
        var profile = SelectedProfile;
        HostText = host;

        if (string.IsNullOrWhiteSpace(host))
        {
            Status = DisconnectedStatus;
            return;
        }

        if (profile is null)
        {
            return;
        }

        IsBusy = true;
        using var linkedCancellationTokenSource = CreateHostLinkedCancellation(selection, CancellationToken.None);
        try
        {
            Status = mode == ProfileSizeCalculationMode.PolicyExcluded
                ? $"Calculating policy size for {profile.AccountName}..."
                : $"Calculating raw size for {profile.AccountName}...";
            var result = await _windowsProfileManager.CalculateProfileSizeAsync(host, profile.LocalPath, mode, linkedCancellationTokenSource.Token);
            if (!EnsureCurrentSelection(selection))
            {
                Status = "Operation canceled because the target host changed.";
                return;
            }

            profile.ApplySizeResult(result);
            CacheResult(result);
            var warningsSuffix = result.Warnings.Count == 0 ? string.Empty : $" Warnings: {string.Join(" ", result.Warnings)}";
            Status = $"{(mode == ProfileSizeCalculationMode.PolicyExcluded ? "Policy" : "Raw")} size calculated for {profile.AccountName}: {GetDisplayForMode(profile, mode)}.{warningsSuffix}";
        }
        catch (OperationCanceledException) when (selection.CancellationToken.IsCancellationRequested)
        {
            Status = "Operation canceled because the target host changed.";
        }
        catch (Exception ex)
        {
            Status = $"Profile size calculation failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ApplySnapshot(WindowsProfileSnapshot snapshot, string? previousPath)
    {
        Profiles.Clear();
        foreach (var profile in snapshot.Profiles)
        {
            var presentation = new DeviceProfilePresentation(profile);
            if (_rawSizeCache.TryGetValue(profile.LocalPath, out var rawSize))
            {
                presentation.ApplySizeResult(rawSize);
            }

            if (_policySizeCache.TryGetValue(profile.LocalPath, out var policySize))
            {
                presentation.ApplySizeResult(policySize);
            }

            Profiles.Add(presentation);
        }

        ApplyPolicy(snapshot.Policy);
        SelectedProfile = Profiles.FirstOrDefault(profile => string.Equals(profile.LocalPath, previousPath, StringComparison.OrdinalIgnoreCase))
            ?? Profiles.FirstOrDefault();
    }

    private void ApplyPolicy(WindowsProfilePolicyInfo policy)
    {
        PolicySource = string.IsNullOrWhiteSpace(policy.Source) ? "Not configured" : policy.Source;
        MaxProfileSizeText = policy.MaxProfileSizeMb.HasValue ? $"{policy.MaxProfileSizeMb.Value:N0} MB" : "Not configured";
        IncludesRegistryInQuotaText = policy.IncludesRegistryInQuota.HasValue ? (policy.IncludesRegistryInQuota.Value ? "Yes" : "No") : "Not configured";
        ExcludedPathsText = policy.ExcludedRelativePaths.Count == 0 ? "None" : string.Join(", ", policy.ExcludedRelativePaths);
    }

    private void CacheResult(WindowsProfileSizeResult result)
    {
        var target = result.Mode == ProfileSizeCalculationMode.PolicyExcluded ? _policySizeCache : _rawSizeCache;
        target[result.ProfileLocalPath] = result;
    }

    private string GetDisplayForMode(DeviceProfilePresentation profile, ProfileSizeCalculationMode mode)
    {
        return mode == ProfileSizeCalculationMode.PolicyExcluded ? profile.PolicySizeDisplay : profile.RawSizeDisplay;
    }

    private void ClearProfiles(bool clearCaches)
    {
        Profiles.Clear();
        SelectedProfile = null;
        ApplyPolicy(new WindowsProfilePolicyInfo(null, null, [], "Not configured"));
        if (clearCaches)
        {
            _rawSizeCache.Clear();
            _policySizeCache.Clear();
        }
    }

    private void OnHostChanged(object? sender, string host)
    {
        _rawSizeCache.Clear();
        _policySizeCache.Clear();
        _ = LoadAsync(CancellationToken.None);
    }

    private bool EnsureCurrentSelection(HostSelection selection)
    {
        return _targetHostService.IsCurrent(selection);
    }

    private CancellationTokenSource CreateHostLinkedCancellation(HostSelection selection, CancellationToken cancellationToken)
    {
        return CancellationTokenSource.CreateLinkedTokenSource(selection.CancellationToken, cancellationToken);
    }

    private void ForwardStatusToHost(string value)
    {
        if (_hostStatusLogSink is null || string.IsNullOrWhiteSpace(value) || string.Equals(value, _lastForwardedStatusLine, StringComparison.Ordinal))
        {
            return;
        }

        _lastForwardedStatusLine = value;
        _hostStatusLogSink.Append($"[Device Profiles] {value}");
    }

    private static bool ConfirmViaMessageBox(string title, string message)
    {
        return MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
    }

    private static string BuildDeleteConfirmationMessage(DeviceProfilePresentation profile)
    {
        return
            $"Delete profile '{profile.AccountName}'?" + Environment.NewLine + Environment.NewLine +
            $"Local path: {profile.LocalPath}" + Environment.NewLine +
            $"SID: {profile.Sid}" + Environment.NewLine + Environment.NewLine +
            "This renames the profile directory and removes the ProfileList registry key on the target device.";
    }
}
