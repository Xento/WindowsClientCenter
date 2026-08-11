using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WindowsClientCenter.Intune.Services.Contracts;
using WindowsClientCenter.Intune.Services.Models;
using WindowsClientCenter.Plugin.Abstractions.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace WindowsClientCenter.Plugins.DeviceActions.ViewModels;

public partial class DeviceInstalledSoftwareViewModel : ObservableObject, IDisposable
{
    private const string DisconnectedStatus = "Client is not connected. Click Connect first.";
    private readonly IInstalledSoftwareManager _installedSoftwareManager;
    private readonly ITargetHostService _targetHostService;
    private readonly IHostStatusLogSink? _hostStatusLogSink;
    private readonly Func<string, string, bool> _confirmAction;
    private string _lastForwardedStatusLine = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _status = DisconnectedStatus;

    [ObservableProperty]
    private string _hostText = string.Empty;

    [ObservableProperty]
    private InstalledSoftwareEntry? _selectedSoftware;

    public ObservableCollection<InstalledSoftwareEntry> Software { get; } = [];

    public DeviceInstalledSoftwareViewModel(IPluginContext pluginContext, Func<string, string, bool>? confirmAction = null)
    {
        _installedSoftwareManager = pluginContext.Services.GetRequiredService<IInstalledSoftwareManager>();
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

    partial void OnSelectedSoftwareChanged(InstalledSoftwareEntry? value)
    {
        NotifyCommandStates();
    }

    partial void OnIsBusyChanged(bool value)
    {
        NotifyCommandStates();
    }

    [RelayCommand]
    public Task RefreshAsync()
    {
        return LoadAsync(CancellationToken.None);
    }

    [RelayCommand(CanExecute = nameof(CanRepairSelectedMsi))]
    public Task RepairSelectedMsiAsync()
    {
        return ExecuteForSelectedSoftwareAsync(
            InstalledSoftwareAction.RepairMsi,
            "Repairing MSI",
            software => software.EffectiveProductCode,
            (host, software, cancellationToken) => _installedSoftwareManager.RepairMsiAsync(host, software.EffectiveProductCode, cancellationToken));
    }

    [RelayCommand(CanExecute = nameof(CanUninstallSelectedMsi))]
    public Task UninstallSelectedMsiAsync()
    {
        return ExecuteForSelectedSoftwareAsync(
            InstalledSoftwareAction.UninstallMsi,
            "Uninstalling MSI",
            software => software.EffectiveProductCode,
            (host, software, cancellationToken) => _installedSoftwareManager.UninstallMsiAsync(host, software.EffectiveProductCode, cancellationToken));
    }

    [RelayCommand(CanExecute = nameof(CanQuietUninstallSelected))]
    public Task QuietUninstallSelectedAsync()
    {
        return ExecuteForSelectedSoftwareAsync(
            InstalledSoftwareAction.QuietUninstall,
            "Running quiet uninstall",
            software => software.QuietUninstallString,
            (host, software, cancellationToken) => _installedSoftwareManager.UninstallQuietAsync(
                host,
                software.QuietUninstallString,
                BuildSoftwareIdentity(software),
                cancellationToken));
    }

    [RelayCommand(CanExecute = nameof(CanForceRemoveSelectedRegistryEntry))]
    public Task ForceRemoveSelectedRegistryEntryAsync()
    {
        return ExecuteForSelectedSoftwareAsync(
            InstalledSoftwareAction.ForceRemoveRegistryEntry,
            "Force removing registry entry",
            BuildRegistryRemovalCommandText,
            (host, software, cancellationToken) => _installedSoftwareManager.ForceRemoveRegistryEntryAsync(host, software, cancellationToken));
    }

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        var selection = _targetHostService.CaptureSelection();
        var host = selection.Host?.Trim() ?? string.Empty;
        HostText = host;

        if (string.IsNullOrWhiteSpace(host))
        {
            Software.Clear();
            SelectedSoftware = null;
            Status = DisconnectedStatus;
            return;
        }

        IsBusy = true;
        using var linkedCancellationTokenSource = CreateHostLinkedCancellation(selection, cancellationToken);
        var previousSoftwareId = SelectedSoftware?.Id;
        try
        {
            var snapshot = await _installedSoftwareManager.GetInstalledSoftwareAsync(host, linkedCancellationTokenSource.Token);
            if (!EnsureCurrentSelection(selection))
            {
                return;
            }

            ApplySnapshot(snapshot, previousSoftwareId);
            Status = snapshot.Warnings.Count > 0 && snapshot.Entries.Count == 0
                ? $"Failed to load installed software: {string.Join(" ", snapshot.Warnings)}"
                : snapshot.Warnings.Count > 0
                    ? $"Loaded {snapshot.Entries.Count} installed software item(s) with warning(s): {string.Join(" ", snapshot.Warnings)}"
                    : $"Loaded {snapshot.Entries.Count} installed software item(s).";
        }
        catch (OperationCanceledException) when (selection.CancellationToken.IsCancellationRequested)
        {
            // Host changed while load was running.
        }
        catch (Exception ex)
        {
            Software.Clear();
            SelectedSoftware = null;
            Status = $"Failed to load installed software: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ExecuteForSelectedSoftwareAsync(
        InstalledSoftwareAction action,
        string busyText,
        Func<InstalledSoftwareEntry, string> commandSelector,
        Func<string, InstalledSoftwareEntry, CancellationToken, ValueTask<DeviceActionResult>> execute)
    {
        var selection = _targetHostService.CaptureSelection();
        var host = selection.Host?.Trim() ?? string.Empty;
        var software = SelectedSoftware;
        HostText = host;

        if (string.IsNullOrWhiteSpace(host))
        {
            Status = DisconnectedStatus;
            return;
        }

        if (software is null)
        {
            Status = "Select installed software first.";
            return;
        }

        if (!_confirmAction(BuildConfirmationTitle(action), BuildConfirmationMessage(host, software, action, commandSelector(software))))
        {
            Status = $"{FormatActionName(action)} cancelled for '{software.Name}'.";
            return;
        }

        IsBusy = true;
        using var linkedCancellationTokenSource = CreateHostLinkedCancellation(selection, CancellationToken.None);
        try
        {
            Status = $"{busyText} '{software.Name}'...";
            var result = await execute(host, software, linkedCancellationTokenSource.Token);
            if (!EnsureCurrentSelection(selection))
            {
                Status = "Operation canceled because the target host changed.";
                return;
            }

            Status = result.Message;
            if (result.Success)
            {
                await LoadAsync(linkedCancellationTokenSource.Token);
            }
        }
        catch (OperationCanceledException) when (selection.CancellationToken.IsCancellationRequested)
        {
            Status = "Operation canceled because the target host changed.";
        }
        catch (Exception ex)
        {
            Status = $"{busyText} failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanRepairSelectedMsi()
    {
        return CanRunSelectedSoftwareAction() && SelectedSoftware?.CanRepairMsi == true;
    }

    private bool CanUninstallSelectedMsi()
    {
        return CanRunSelectedSoftwareAction() && SelectedSoftware?.CanUninstallMsi == true;
    }

    private bool CanQuietUninstallSelected()
    {
        return CanRunSelectedSoftwareAction() && SelectedSoftware?.CanQuietUninstall == true;
    }

    private bool CanForceRemoveSelectedRegistryEntry()
    {
        return CanRunSelectedSoftwareAction() && SelectedSoftware?.CanForceRemoveRegistryEntry == true;
    }

    private bool CanRunSelectedSoftwareAction()
    {
        return !IsBusy &&
               SelectedSoftware is not null &&
               !string.IsNullOrWhiteSpace(_targetHostService.CurrentHost);
    }

    private void ApplySnapshot(InstalledSoftwareSnapshot snapshot, string? preferredSoftwareId)
    {
        Software.Clear();
        foreach (var item in snapshot.Entries)
        {
            Software.Add(item);
        }

        if (Software.Count == 0)
        {
            SelectedSoftware = null;
            return;
        }

        SelectedSoftware = Software.FirstOrDefault(item =>
                string.Equals(item.Id, preferredSoftwareId, StringComparison.OrdinalIgnoreCase))
            ?? Software[0];
    }

    private void NotifyCommandStates()
    {
        RepairSelectedMsiCommand.NotifyCanExecuteChanged();
        UninstallSelectedMsiCommand.NotifyCanExecuteChanged();
        QuietUninstallSelectedCommand.NotifyCanExecuteChanged();
        ForceRemoveSelectedRegistryEntryCommand.NotifyCanExecuteChanged();
    }

    private void OnHostChanged(object? sender, string host)
    {
        HostText = host;
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
        _hostStatusLogSink.Append($"[Installed Software] {normalized}");
    }

    private static string BuildConfirmationTitle(InstalledSoftwareAction action)
    {
        return action switch
        {
            InstalledSoftwareAction.RepairMsi => "Confirm MSI repair",
            InstalledSoftwareAction.UninstallMsi => "Confirm MSI uninstall",
            InstalledSoftwareAction.QuietUninstall => "Confirm quiet uninstall",
            InstalledSoftwareAction.ForceRemoveRegistryEntry => "Confirm forced registry removal",
            _ => "Confirm installed software action"
        };
    }

    private static string BuildConfirmationMessage(string host, InstalledSoftwareEntry software, InstalledSoftwareAction action, string commandText)
    {
        return
            $"{FormatActionName(action)} '{software.Name}' on '{host}'?{Environment.NewLine}{Environment.NewLine}" +
            $"Version: {FormatValue(software.Version)}{Environment.NewLine}" +
            $"Publisher: {FormatValue(software.Publisher)}{Environment.NewLine}" +
            $"Action: {FormatActionName(action)}{Environment.NewLine}" +
            $"Command: {FormatValue(commandText)}";
    }

    private static string FormatActionName(InstalledSoftwareAction action)
    {
        return action switch
        {
            InstalledSoftwareAction.RepairMsi => "MSI repair",
            InstalledSoftwareAction.UninstallMsi => "MSI silent uninstall",
            InstalledSoftwareAction.QuietUninstall => "quiet uninstall",
            InstalledSoftwareAction.ForceRemoveRegistryEntry => "forced registry removal",
            _ => action.ToString()
        };
    }

    private static string BuildRegistryRemovalCommandText(InstalledSoftwareEntry software)
    {
        var productCode = software.EffectiveProductCode;
        return software.IsMsi
            ? $"Remove uninstall registry key(s) and Installer product registry key(s) for {productCode}"
            : "Remove uninstall registry key for selected entry";
    }

    private static string BuildSoftwareIdentity(InstalledSoftwareEntry software)
    {
        return string.IsNullOrWhiteSpace(software.Version)
            ? software.Name
            : $"{software.Name} {software.Version}";
    }

    private static string FormatValue(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "-" : value.Trim();
    }

    private static bool ConfirmViaMessageBox(string title, string message)
    {
        return MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
    }
}
