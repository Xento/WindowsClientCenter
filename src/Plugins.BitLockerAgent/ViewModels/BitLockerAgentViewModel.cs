using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WindowsClientCenter.Intune.Services.Contracts;
using WindowsClientCenter.Intune.Services.Models;
using WindowsClientCenter.Plugin.Abstractions.Contracts;
using WindowsClientCenter.Plugins.BitLockerAgent.Models;
using Microsoft.Extensions.DependencyInjection;

namespace WindowsClientCenter.Plugins.BitLockerAgent.ViewModels;

public partial class BitLockerAgentViewModel : ObservableObject, IDisposable
{
    private const string DisconnectedStatus = "Client is not connected. Click Connect first.";
    private readonly ILocalBitLockerService _localBitLockerService;
    private readonly ITargetHostService _targetHostService;
    private readonly IHostStatusLogSink? _hostStatusLogSink;
    private readonly IHostBusyStateSink? _hostBusyStateSink;
    private readonly Dictionary<string, string> _protectorActionStatuses = new(StringComparer.OrdinalIgnoreCase);
    private readonly bool _verboseOperationsEnabled;
    private string? _activeBusyOwnerId;
    private int _busyOperationSequence;
    private string _lastForwardedStatusLine = string.Empty;

    public ObservableCollection<BitLockerVolumeSnapshot> Volumes { get; } = [];
    public ObservableCollection<BitLockerProtectorSnapshot> Protectors { get; } = [];
    public ObservableCollection<BitLockerOperationLogEntry> OperationLogEntries { get; } = [];
    public IReadOnlyList<int> SuspendRebootCountOptions { get; } = Enumerable.Range(0, 16).ToArray();

    [ObservableProperty]
    private string _status = DisconnectedStatus;

    [ObservableProperty]
    private BitLockerHostSnapshot? _snapshot;

    [ObservableProperty]
    private BitLockerVolumeSnapshot? _selectedVolume;

    [ObservableProperty]
    private BitLockerProtectorSnapshot? _selectedProtector;

    [ObservableProperty]
    private int _selectedSuspendRebootCount = 1;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _capabilityWarningsText = "No capability warnings.";

    [ObservableProperty]
    private string _overallHealthText = "Unknown";

    [ObservableProperty]
    private string _overallHealthColorHex = "#8A8A8A";

    [ObservableProperty]
    private string _policySourceSummaryText = "No BitLocker policy sources detected.";

    public BitLockerAgentViewModel(IPluginContext pluginContext, string? initialNavigationTarget = null)
    {
        _localBitLockerService = pluginContext.Services.GetRequiredService<ILocalBitLockerService>();
        _targetHostService = pluginContext.Services.GetRequiredService<ITargetHostService>();
        _hostStatusLogSink = pluginContext.Services.GetService<IHostStatusLogSink>();
        _hostBusyStateSink = pluginContext.Services.GetService<IHostBusyStateSink>();
        if (pluginContext.Settings.TryGetValue("VerboseOperations", out var globalVerboseSetting) &&
            bool.TryParse(globalVerboseSetting, out var globalVerboseEnabled))
        {
            _verboseOperationsEnabled = globalVerboseEnabled;
        }

        ApplyNavigationTarget(initialNavigationTarget);
        _targetHostService.HostChanged += OnHostChanged;
    }

    public void ApplyNavigationTarget(string? navigationTarget)
    {
        _ = navigationTarget;
    }

    [RelayCommand]
    public Task RefreshAsync()
    {
        return LoadAsync(CancellationToken.None);
    }

    [RelayCommand(CanExecute = nameof(CanSuspendProtection))]
    private async Task SuspendProtectionAsync()
    {
        if (SelectedVolume is null)
        {
            return;
        }

        await ExecuteActionAsync(
            $"Suspend protection on {SelectedVolume.MountPoint}",
            cancellationToken => _localBitLockerService.SuspendProtectionAsync(
                _targetHostService.CurrentHost,
                SelectedVolume.MountPoint,
                SelectedSuspendRebootCount,
                cancellationToken,
                _verboseOperationsEnabled),
            SelectedVolume.MountPoint,
            null);
    }

    [RelayCommand(CanExecute = nameof(CanResumeProtection))]
    private async Task ResumeProtectionAsync()
    {
        if (SelectedVolume is null)
        {
            return;
        }

        await ExecuteActionAsync(
            $"Resume protection on {SelectedVolume.MountPoint}",
            cancellationToken => _localBitLockerService.ResumeProtectionAsync(
                _targetHostService.CurrentHost,
                SelectedVolume.MountPoint,
                cancellationToken,
                _verboseOperationsEnabled),
            SelectedVolume.MountPoint,
            null);
    }

    [RelayCommand(CanExecute = nameof(CanAddRecoveryPasswordProtector))]
    private async Task AddRecoveryPasswordProtectorAsync()
    {
        if (SelectedVolume is null)
        {
            return;
        }

        await ExecuteActionAsync(
            $"Add recovery-password protector on {SelectedVolume.MountPoint}",
            cancellationToken => _localBitLockerService.AddRecoveryPasswordProtectorAsync(
                _targetHostService.CurrentHost,
                SelectedVolume.MountPoint,
                cancellationToken,
                _verboseOperationsEnabled),
            SelectedVolume.MountPoint,
            null);
    }

    [RelayCommand(CanExecute = nameof(CanBackupRecoveryPassword))]
    private async Task BackupRecoveryPasswordAsync()
    {
        if (SelectedVolume is null || SelectedProtector is null)
        {
            return;
        }

        await ExecuteActionAsync(
            $"Back up recovery-password protector on {SelectedVolume.MountPoint}",
            cancellationToken => _localBitLockerService.BackupRecoveryPasswordAsync(
                _targetHostService.CurrentHost,
                SelectedVolume.MountPoint,
                SelectedProtector.ProtectorId,
                cancellationToken,
                _verboseOperationsEnabled),
            SelectedVolume.MountPoint,
            SelectedProtector.ProtectorId);
    }

    [RelayCommand(CanExecute = nameof(CanRotateRecoveryPassword))]
    private async Task RotateRecoveryPasswordAsync()
    {
        if (SelectedVolume is null || SelectedProtector is null)
        {
            return;
        }

        await ExecuteActionAsync(
            $"Rotate recovery-password protector on {SelectedVolume.MountPoint}",
            cancellationToken => _localBitLockerService.RotateRecoveryPasswordAsync(
                _targetHostService.CurrentHost,
                SelectedVolume.MountPoint,
                SelectedProtector.ProtectorId,
                cancellationToken,
                _verboseOperationsEnabled),
            SelectedVolume.MountPoint,
            SelectedProtector.ProtectorId);
    }

    [RelayCommand(CanExecute = nameof(CanRemoveRecoveryPasswordProtector))]
    private async Task RemoveRecoveryPasswordProtectorAsync()
    {
        if (SelectedVolume is null || SelectedProtector is null)
        {
            return;
        }

        await ExecuteActionAsync(
            $"Remove recovery-password protector from {SelectedVolume.MountPoint}",
            cancellationToken => _localBitLockerService.RemoveRecoveryPasswordProtectorAsync(
                _targetHostService.CurrentHost,
                SelectedVolume.MountPoint,
                SelectedProtector.ProtectorId,
                cancellationToken,
                _verboseOperationsEnabled),
            SelectedVolume.MountPoint,
            SelectedProtector.ProtectorId);
    }

    public async Task LoadAsync(CancellationToken cancellationToken, string? preferredMountPoint = null, string? preferredProtectorId = null)
    {
        var selection = _targetHostService.CaptureSelection();
        var host = selection.Host;
        if (string.IsNullOrWhiteSpace(host))
        {
            Snapshot = null;
            Volumes.Clear();
            Protectors.Clear();
            SelectedVolume = null;
            SelectedProtector = null;
            CapabilityWarningsText = DisconnectedStatus;
            PolicySourceSummaryText = "No BitLocker policy sources detected.";
            OverallHealthText = "Unknown";
            OverallHealthColorHex = "#8A8A8A";
            Status = DisconnectedStatus;
            ClearBusyState();
            return;
        }

        var busyOwnerId = BeginBusyState(host, "BitLocker");
        IsBusy = true;
        NotifyActionStates();
        Status = $"Loading BitLocker data for '{host}'...";
        using var linkedCancellationTokenSource = CreateHostLinkedCancellation(selection, cancellationToken);

        try
        {
            var snapshot = await _localBitLockerService.GetSnapshotAsync(host, linkedCancellationTokenSource.Token, _verboseOperationsEnabled);
            if (!EnsureCurrentSelection(selection))
            {
                return;
            }

            Snapshot = snapshot;
            PopulateVolumes(snapshot.Volumes, preferredMountPoint, preferredProtectorId);
            CapabilityWarningsText = snapshot.Capabilities.Warnings.Count == 0
                ? "No capability warnings."
                : string.Join(Environment.NewLine, snapshot.Capabilities.Warnings);
            PolicySourceSummaryText = BuildPolicySourceSummary(snapshot);
            OverallHealthText = snapshot.OverallHealthLevel;
            OverallHealthColorHex = snapshot.OverallHealthLevel switch
            {
                "Green" => "#1A7F37",
                "Yellow" => "#B07D00",
                "Red" => "#C62828",
                _ => "#8A8A8A"
            };
            Status = $"Loaded BitLocker data for '{host}'.";
            ForwardStatusToHost(Status);
        }
        catch (OperationCanceledException) when (selection.CancellationToken.IsCancellationRequested)
        {
            // Host changed while a background load was running.
        }
        catch (Exception ex)
        {
            Snapshot = null;
            Volumes.Clear();
            Protectors.Clear();
            SelectedVolume = null;
            SelectedProtector = null;
            CapabilityWarningsText = ex.Message;
            PolicySourceSummaryText = "No BitLocker policy sources detected.";
            OverallHealthText = "Red";
            OverallHealthColorHex = "#C62828";
            Status = TryBuildConnectionFailureStatus(host, ex.Message, out var connectionFailure)
                ? connectionFailure
                : $"BitLocker data load failed for '{host}': {ex.Message}";
            ForwardStatusToHost(Status);
        }
        finally
        {
            IsBusy = false;
            NotifyActionStates();
            ClearBusyState(busyOwnerId);
        }
    }

    partial void OnSelectedVolumeChanged(BitLockerVolumeSnapshot? value)
    {
        PopulateProtectors(value, SelectedProtector?.ProtectorId);
        NotifyActionStates();
    }

    partial void OnSelectedProtectorChanged(BitLockerProtectorSnapshot? value)
    {
        _ = value;
        NotifyActionStates();
    }

    partial void OnIsBusyChanged(bool value)
    {
        _ = value;
        NotifyActionStates();
    }

    public void Dispose()
    {
        _targetHostService.HostChanged -= OnHostChanged;
        ClearBusyState();
    }

    public bool CanSuspendProtection() =>
        !IsBusy &&
        SelectedVolume is not null &&
        !SelectedVolume.IsProtectionSuspended &&
        !string.IsNullOrWhiteSpace(_targetHostService.CurrentHost);

    public bool CanResumeProtection() =>
        !IsBusy &&
        SelectedVolume is { IsProtectionSuspended: true } &&
        !string.IsNullOrWhiteSpace(_targetHostService.CurrentHost);

    public bool CanAddRecoveryPasswordProtector() =>
        !IsBusy &&
        SelectedVolume is not null &&
        !string.IsNullOrWhiteSpace(_targetHostService.CurrentHost);

    public bool CanBackupRecoveryPassword() =>
        !IsBusy &&
        SelectedVolume is not null &&
        SelectedProtector is { IsRecoveryPassword: true } &&
        !string.IsNullOrWhiteSpace(_targetHostService.CurrentHost);

    public bool CanRotateRecoveryPassword() =>
        !IsBusy &&
        SelectedVolume is not null &&
        SelectedProtector is { IsRecoveryPassword: true } &&
        !string.IsNullOrWhiteSpace(_targetHostService.CurrentHost);

    public bool CanRemoveRecoveryPasswordProtector()
    {
        if (IsBusy || SelectedVolume is null || SelectedProtector is not { IsRecoveryPassword: true, IsRemovable: true })
        {
            return false;
        }

        return SelectedVolume.Protectors.Count(static protector => protector.IsRecoveryPassword) > 1 &&
               !string.IsNullOrWhiteSpace(_targetHostService.CurrentHost);
    }

    private async Task ExecuteActionAsync(
        string actionName,
        Func<CancellationToken, ValueTask<BitLockerActionResult>> action,
        string? preferredMountPoint,
        string? preferredProtectorId)
    {
        var selection = _targetHostService.CaptureSelection();
        var host = selection.Host;
        if (string.IsNullOrWhiteSpace(host))
        {
            Status = DisconnectedStatus;
            return;
        }

        var busyOwnerId = BeginBusyState(host, actionName);
        IsBusy = true;
        NotifyActionStates();
        Status = $"{actionName}...";
        using var linkedCancellationTokenSource = CreateHostLinkedCancellation(selection, CancellationToken.None);

        try
        {
            var result = await action(linkedCancellationTokenSource.Token);
            if (!EnsureCurrentSelection(selection))
            {
                Status = "Operation canceled because the target host changed.";
                return;
            }

            var level = result.Success ? "Success" : result.Warning ? "Warning" : "Error";
            var selectedProtectorId = result.NewProtectorId ?? preferredProtectorId;
            if (!string.IsNullOrWhiteSpace(selectedProtectorId))
            {
                _protectorActionStatuses[selectedProtectorId] = result.Message;
            }

            AppendOperationLog(level, preferredMountPoint ?? "-", result.Message, result.Details);
            Status = result.Message;
            ForwardStatusToHost(Status);

            if (result.Success || result.Warning)
            {
                await LoadAsync(linkedCancellationTokenSource.Token, preferredMountPoint, selectedProtectorId);
            }
        }
        catch (OperationCanceledException) when (selection.CancellationToken.IsCancellationRequested)
        {
            Status = "Operation canceled because the target host changed.";
            ForwardStatusToHost(Status);
        }
        catch (Exception ex)
        {
            Status = $"{actionName} failed: {ex.Message}";
            AppendOperationLog("Error", preferredMountPoint ?? "-", Status, []);
            ForwardStatusToHost(Status);
        }
        finally
        {
            IsBusy = false;
            NotifyActionStates();
            ClearBusyState(busyOwnerId);
        }
    }

    private void PopulateVolumes(IReadOnlyList<BitLockerVolumeSnapshot> volumes, string? preferredMountPoint, string? preferredProtectorId)
    {
        Volumes.Clear();
        foreach (var volume in volumes)
        {
            Volumes.Add(volume);
        }

        var resolvedVolume = Volumes.FirstOrDefault(volume =>
                                 volume.MountPoint.Equals(preferredMountPoint ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                             ?? Volumes.FirstOrDefault();
        SelectedVolume = resolvedVolume;
        PopulateProtectors(resolvedVolume, preferredProtectorId);
    }

    private void PopulateProtectors(BitLockerVolumeSnapshot? volume, string? preferredProtectorId)
    {
        Protectors.Clear();
        if (volume is null)
        {
            SelectedProtector = null;
            return;
        }

        foreach (var protector in volume.Protectors)
        {
            var resolved = protector;
            if (_protectorActionStatuses.TryGetValue(protector.ProtectorId, out var actionStatus) &&
                !string.IsNullOrWhiteSpace(actionStatus))
            {
                resolved = protector with { LastActionStatusText = actionStatus };
            }

            Protectors.Add(resolved);
        }

        SelectedProtector = Protectors.FirstOrDefault(protector =>
                               protector.ProtectorId.Equals(preferredProtectorId ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                           ?? Protectors.FirstOrDefault();
    }

    private void AppendOperationLog(string level, string target, string message, IReadOnlyList<string>? details)
    {
        var detailsText = details is { Count: > 0 }
            ? string.Join(Environment.NewLine, details.Where(static detail => !string.IsNullOrWhiteSpace(detail)))
            : "-";
        OperationLogEntries.Insert(0, new BitLockerOperationLogEntry(DateTimeOffset.UtcNow, level, target, message, detailsText));
        while (OperationLogEntries.Count > 40)
        {
            OperationLogEntries.RemoveAt(OperationLogEntries.Count - 1);
        }
    }

    private void NotifyActionStates()
    {
        SuspendProtectionCommand.NotifyCanExecuteChanged();
        ResumeProtectionCommand.NotifyCanExecuteChanged();
        AddRecoveryPasswordProtectorCommand.NotifyCanExecuteChanged();
        BackupRecoveryPasswordCommand.NotifyCanExecuteChanged();
        RotateRecoveryPasswordCommand.NotifyCanExecuteChanged();
        RemoveRecoveryPasswordProtectorCommand.NotifyCanExecuteChanged();
    }

    private void OnHostChanged(object? sender, string host)
    {
        _ = LoadAsync(CancellationToken.None);
    }

    private CancellationTokenSource CreateHostLinkedCancellation(HostSelection selection, CancellationToken cancellationToken)
    {
        return cancellationToken.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(selection.CancellationToken, cancellationToken)
            : CancellationTokenSource.CreateLinkedTokenSource(selection.CancellationToken);
    }

    private bool EnsureCurrentSelection(HostSelection selection)
    {
        return _targetHostService.IsCurrent(selection);
    }

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
        _hostStatusLogSink.Append($"[BitLocker] {normalized}");
    }

    private string? BeginBusyState(string host, string taskName)
    {
        if (_hostBusyStateSink is null)
        {
            return null;
        }

        var ownerId = $"bitlocker-agent:{GetHashCode():X}:{Interlocked.Increment(ref _busyOperationSequence)}";
        if (!string.IsNullOrWhiteSpace(_activeBusyOwnerId))
        {
            _hostBusyStateSink.ClearBusyState(_activeBusyOwnerId);
        }

        _activeBusyOwnerId = ownerId;
        _hostBusyStateSink.SetBusyState(ownerId, $"BitLocker '{host}'", [taskName]);
        return ownerId;
    }

    private void ClearBusyState(string? ownerId = null)
    {
        if (_hostBusyStateSink is null)
        {
            _activeBusyOwnerId = null;
            return;
        }

        var resolvedOwnerId = ownerId ?? _activeBusyOwnerId;
        if (string.IsNullOrWhiteSpace(resolvedOwnerId))
        {
            _activeBusyOwnerId = null;
            return;
        }

        if (string.Equals(_activeBusyOwnerId, resolvedOwnerId, StringComparison.Ordinal))
        {
            _activeBusyOwnerId = null;
        }

        _hostBusyStateSink.ClearBusyState(resolvedOwnerId);
    }

    private static bool TryBuildConnectionFailureStatus(string host, string? message, out string status)
    {
        status = string.Empty;
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        var normalized = message.Trim();
        var indicatesConnectionFailure =
            normalized.Contains("test-wsman", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("winrm", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("cannot connect", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("destination specified in the request", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("rpc server is unavailable", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("name resolution", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("no such host is known", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("timed out", StringComparison.OrdinalIgnoreCase);

        if (!indicatesConnectionFailure)
        {
            return false;
        }

        var reason = normalized.Contains("access is denied", StringComparison.OrdinalIgnoreCase)
            ? "Access denied."
            : normalized.Contains("timed out", StringComparison.OrdinalIgnoreCase)
                ? "Timeout while connecting to remote host."
                : normalized.Contains("no such host is known", StringComparison.OrdinalIgnoreCase) ||
                  normalized.Contains("name resolution", StringComparison.OrdinalIgnoreCase)
                    ? "Host name could not be resolved."
                    : "WinRM connection failed.";

        status = $"Connection to '{host}' failed: {reason}";
        return true;
    }

    private static string BuildPolicySourceSummary(BitLockerHostSnapshot snapshot)
    {
        var sources = new List<string>();
        if (snapshot.HasIntunePolicies)
        {
            sources.Add("MDM (Intune)");
        }

        if (snapshot.HasGpoPolicies)
        {
            sources.Add("Group Policy");
        }

        if (snapshot.HasMecmPolicies)
        {
            sources.Add("Configuration Manager");
        }

        if (sources.Count == 0)
        {
            return snapshot.Policies.Count == 0
                ? "No BitLocker policy sources detected."
                : "BitLocker policies were collected, but the source could not be classified.";
        }

        return $"Detected BitLocker policy sources: {string.Join(", ", sources)}.";
    }
}
