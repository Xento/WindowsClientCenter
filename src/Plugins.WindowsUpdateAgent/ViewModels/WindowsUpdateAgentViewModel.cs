using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using WindowsClientCenter.Intune.Services.Models;
using WindowsClientCenter.Intune.Services.Runtime;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WindowsClientCenter.Intune.Services.Contracts;
using WindowsClientCenter.Plugin.Abstractions.Contracts;
using WindowsClientCenter.Plugins.WindowsUpdateAgent.Models;
using WindowsClientCenter.Shared.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace WindowsClientCenter.Plugins.WindowsUpdateAgent.ViewModels;

public partial class WindowsUpdateAgentViewModel : ObservableObject, IDisposable
{
    private const string DisconnectedStatus = "Client is not connected. Click Connect first.";
    private const int MinTailLineCount = 20;
    private const int MaxTailLineCount = 5000;
    private const int MaxBufferedRows = 25000;
    private const int StreamPollDelayMilliseconds = 700;
    private const int InstallStatusRefreshIntervalSeconds = 5;
    private const int RemoteInstallRefreshQueuedSeconds = 10;
    private const int RemoteInstallRefreshActiveSeconds = 20;
    private const int RemoteInstallRefreshIdleSeconds = 45;
    private const int RemoteInstallIdleBackoffThresholdSeconds = 60;
    private const int RemoteInstallProgressPollSeconds = 5;
    private const int RemoteInstallProgressIdlePollSeconds = 15;
    private const int RemoteInstallProgressSnapshotLineCount = 200;
    private const string UsoStoreRelativePath = @"ProgramData\USOPrivate\UpdateStore\store.db";
    private const string ReportingEventsLogPath = @"C:\Windows\SoftwareDistribution\ReportingEvents.log";
    private const string InstallTaskName = "WindowsClientCenter-WindowsUpdate-InstallSelected";
    private const string InstallWorkDirectory = @"C:\ProgramData\WindowsClientCenter\WindowsUpdateAgent";
    private const string InstallScriptFileName = "InstallSelectedUpdates.ps1";
    private const string InstallAsyncScriptFileName = "InstallSelectedUpdates.Async.ps1";
    private const string InstallWinRtScriptFileName = "InstallSelectedUpdates.WinRT.ps1";
    private const string InstallLauncherFileName = "InstallSelectedUpdates.Launcher.ps1";
    private const string InstallPayloadFileName = "selected-updates.json";
    private const string InstallStateFileName = "install-status.json";
    private const string InstallProgressLogFileName = "install-progress.log";
    private const string WinRtUpdateClientScriptRelativePath = @"Scripts\Invoke-WinRTWindowsUpdateClient.ps1";
    private const string UpdateServiceKillRequiredMarker = "__ICC_WU_SERVICE_KILL_REQUIRED__";
    private const int WinRtCompletedHistoryCount = 100;
    private const int MaxInstallProgressRows = 2000;
    private const string UpdateApiCom = "COM";
    private const string UpdateApiWinRt = "WinRT";

    private static readonly Regex TokenRegex = new(@"\[[^\]]+\]|\{[^}]+\}|[^\s]+", RegexOptions.Compiled);
    private static readonly Regex CorrelationTokenRegex = new(@"^[A-Za-z0-9+/=-]{6,}\.[0-9.]+$", RegexOptions.Compiled);
    private static readonly Regex DateTimeWithFractionRegex = new(
        @"(?<date>\d{4}-\d{2}-\d{2})\s+(?<time>\d{2}:\d{2}:\d{2})(?:[.,](?<fraction>\d{1,7}))?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly ITargetHostService _targetHostService;
    private readonly ILocalIntuneDiagnosticsService _localIntuneDiagnosticsService;
    private readonly ILocalDeviceActionService? _localDeviceActionService;
    private readonly IPowerShellExecutor? _powerShellExecutor;
    private readonly IntuneRuntimeOptions _intuneRuntimeOptions;
    private readonly DemoDataCatalog _demoDataCatalog;
    private readonly IHostStatusLogSink? _hostStatusLogSink;
    private readonly IHostBusyStateSink? _hostBusyStateSink;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _monitorGate = new(1, 1);
    private readonly SemaphoreSlim _updateGate = new(1, 1);
    private readonly object _sqliteInitSync = new();
    private readonly object _installProgressSync = new();
    private readonly SemaphoreSlim _providerLoadGate = new(1, 1);

    private CancellationTokenSource? _streamCancellationTokenSource;
    private Task? _streamTask;
    private long _streamSessionId;
    private readonly object _streamCheckpointSync = new();
    private string _streamCheckpointHost = string.Empty;
    private string _streamCheckpointPath = string.Empty;
    private long _streamCheckpointPosition = -1;
    private bool _sqliteInitialized;
    private bool _registeredUpdateProvidersLoadedForCurrentHost;
    private CancellationTokenSource? _installStatusCancellationTokenSource;
    private Task? _installStatusTask;
    private CancellationTokenSource? _installProgressCancellationTokenSource;
    private Task? _installProgressTask;
    private string? _activeInstallStatusPath;
    private string? _activeInstallProgressLogPath;
    private string? _activeInstallHost;
    private string? _activeInstallBusyOwnerId;
    private string? _activeBusyOwnerId;
    private bool _installAutoRestartTriggered;
    private string? _trackedInstallProgressPath;
    private long _installProgressPosition = -1;
    private long _remoteInstallProgressCursor = -1;
    private string _lastInstallMonitorFingerprint = string.Empty;
    private DateTimeOffset _lastInstallMonitorActivityUtc = DateTimeOffset.MinValue;
    private bool _disposed;
    private string _lastForwardedStatusLine = string.Empty;
    private string _lastForwardedLogLine = string.Empty;
    private int _busyOperationSequence;
    private bool _demoInstallTaskStarted;
    private readonly SlidingWindowBuffer<ReportingEventsLogEntry> _entryBuffer = new(MaxBufferedRows);
    private readonly SlidingWindowBuffer<string> _installProgressBuffer = new(MaxInstallProgressRows);

    public ObservableCollection<ReportingEventsLogEntry> Entries { get; } = [];
    public ObservableCollection<WindowsUpdateAvailableEntry> AvailableUpdates { get; } = [];
    public ObservableCollection<WindowsUpdateAvailableEntry> VisibleAvailableUpdates { get; } = [];
    public ObservableCollection<WindowsUpdateProviderEntry> RegisteredUpdateProviders { get; } = [];
    public ObservableCollection<WindowsUpdateHistoryEntry> UpdateHistoryEntries { get; } = [];
    public ObservableCollection<InstallProgressEntry> InstallProgressEntries { get; } = [];
    public IReadOnlyList<string> UpdateApiOptions { get; } = [UpdateApiCom, UpdateApiWinRt];

    [ObservableProperty]
    private string _currentHost = string.Empty;

    [ObservableProperty]
    private string _status = "Not connected";

    [ObservableProperty]
    private bool _isMonitoring;

    [ObservableProperty]
    private bool _isMonitoringBusy;

    [ObservableProperty]
    private bool _isReportingEventsLoading;

    [ObservableProperty]
    private string _reportingEventsLoadingText = "Loading ReportingEvents.log...";

    [ObservableProperty]
    private int _tailLineCount = 300;

    [ObservableProperty]
    private string _updateStatus = "No update action executed yet.";

    [ObservableProperty]
    private bool _isUpdateBusy;

    [ObservableProperty]
    private int _selectedSectionIndex;

    [ObservableProperty]
    private string? _highlightedUpdateId;

    [ObservableProperty]
    private bool _isInstallStatusAutoRefreshEnabled = true;

    [ObservableProperty]
    private string _installTaskState = "No install task started.";

    [ObservableProperty]
    private string _installTaskStatusText = "Task: Unknown";

    [ObservableProperty]
    private string _installTaskPhaseText = "Phase: unknown";

    [ObservableProperty]
    private string _installTaskDetail = string.Empty;

    [ObservableProperty]
    private bool _isInstallTaskRunning;

    [ObservableProperty]
    private bool _useCachedAvailableUpdates = true;

    [ObservableProperty]
    private bool _useWinRtUpdateSearch = true;

    [ObservableProperty]
    private string _lastAvailableUpdatesScanInfo = "Last scan: unknown";

    [ObservableProperty]
    private string _registeredUpdateProvidersInfo = "Registered update providers: unknown";

    [ObservableProperty]
    private string _registeredUpdateProvidersHealthText = "Health: unknown";

    [ObservableProperty]
    private string _registeredUpdateProvidersHealthSummaryText = "Microsoft Update default has not been checked yet.";

    [ObservableProperty]
    private string _registeredUpdateProvidersHealthColorHex = "#8A8A8A";

    [ObservableProperty]
    private bool _isRegisteredUpdateProvidersLoading;

    [ObservableProperty]
    private string _selectedUpdateApi = UpdateApiCom;

    [ObservableProperty]
    private string _autopatchRingText = "Unknown";

    [ObservableProperty]
    private bool _useAsyncInstallScript;

    [ObservableProperty]
    private bool _useWinRtInstallScript;

    [ObservableProperty]
    private bool _restartAfterInstallIfRequired;

    [ObservableProperty]
    private int _selectedAvailableUpdatesViewIndex;

    public WindowsUpdateAgentViewModel(IPluginContext pluginContext, string? initialNavigationTarget = null)
    {
        _targetHostService = pluginContext.Services.GetRequiredService<ITargetHostService>();
        _localIntuneDiagnosticsService = pluginContext.Services.GetRequiredService<ILocalIntuneDiagnosticsService>();
        _localDeviceActionService = pluginContext.Services.GetService<ILocalDeviceActionService>();
        _powerShellExecutor = pluginContext.Services.GetService<IPowerShellExecutor>();
        _intuneRuntimeOptions = pluginContext.Services.GetService<IntuneRuntimeOptions>() ?? new IntuneRuntimeOptions();
        _demoDataCatalog = pluginContext.Services.GetService<DemoDataCatalog>() ?? new DemoDataCatalog(_intuneRuntimeOptions);
        _hostStatusLogSink = pluginContext.Services.GetService<IHostStatusLogSink>();
        _hostBusyStateSink = pluginContext.Services.GetService<IHostBusyStateSink>();
        _logger = ResolveLogger(pluginContext.Services);

        CurrentHost = _targetHostService.CurrentHost;
        InitializeUsoDiagnostics();
        ApplyNavigationTarget(initialNavigationTarget);
        _targetHostService.HostChanged += OnHostChanged;

        ForwardStatusToHost(Status);
        ForwardStatusToHost(UpdateStatus);
        if (IsDemoMode && !string.IsNullOrWhiteSpace(CurrentHost))
        {
            ApplyDemoWindowsUpdateSnapshot(CurrentHost, resetInstallState: true);
        }

        if (SelectedSectionIndex == 0)
        {
            _ = EnsureRegisteredUpdateProvidersLoadedAsync(_targetHostService.CaptureSelection());
        }

        _ = EnsureAutopatchRingLoadedAsync(_targetHostService.CaptureSelection());
    }

    private bool IsDemoMode => _intuneRuntimeOptions.Mode == IntuneRuntimeMode.Demo;

    public void ApplyNavigationTarget(string? navigationTarget)
    {
        SelectedSectionIndex = MapNavigationTargetToSectionIndex(navigationTarget);
    }

    private static ILogger ResolveLogger(IServiceProvider services)
    {
        if (services.GetService(typeof(ILoggerFactory)) is ILoggerFactory factory)
        {
            return factory.CreateLogger(nameof(WindowsUpdateAgentViewModel));
        }

        return NullLogger.Instance;
    }

    public Task StartMonitoringAsync(CancellationToken cancellationToken)
    {
        return StartMonitoringInternalAsync(cancellationToken, fullReload: true);
    }

    [RelayCommand]
    public Task RestartMonitoringAsync()
    {
        return StartMonitoringInternalAsync(CancellationToken.None, fullReload: true);
    }

    [RelayCommand]
    public Task ReloadMonitoringAsync()
    {
        return StartMonitoringInternalAsync(CancellationToken.None, fullReload: true);
    }

    [RelayCommand]
    public Task ToggleMonitoringAsync()
    {
        return IsMonitoring
            ? StopMonitoringAsync()
            : StartMonitoringInternalAsync(CancellationToken.None, fullReload: false);
    }

    [RelayCommand]
    public async Task StopMonitoringAsync()
    {
        if (_disposed)
        {
            return;
        }

        await _monitorGate.WaitAsync();
        try
        {
            IsMonitoringBusy = true;
            StopProcessInternal();
            Status = IsDemoMode ? "Demo stream stopped." : "Stream stopped.";
        }
        finally
        {
            IsMonitoringBusy = false;
            _monitorGate.Release();
        }
    }

    private void ClearLog()
    {
        _entryBuffer.Clear();
        Entries.Clear();
    }

    [RelayCommand]
    public async Task StartUpdateScanAsync()
    {
        if (_disposed)
        {
            return;
        }

        var host = _targetHostService.CurrentHost?.Trim() ?? string.Empty;
        CurrentHost = host;

        if (string.IsNullOrWhiteSpace(host))
        {
            UpdateStatus = DisconnectedStatus;
            return;
        }

        if (IsDemoMode)
        {
            ApplyDemoWindowsUpdateSnapshot(host, resetInstallState: false);
            UpdateStatus = "[WU] Demo update scan completed. Available updates were refreshed from the demo catalog.";
            return;
        }

        await _updateGate.WaitAsync();
        try
        {
            if (_disposed)
            {
                return;
            }

            IsUpdateBusy = true;
            var busyOwnerId = BeginBusyState("Scanning updates", ["Search available updates", "Resolve selections", "Refresh list"]);
            var useLocalAccess = IsLocalHost(host);
            var useWinRtUpdateStack = await ShouldUseWinRtUpdateStackAsync(host, useLocalAccess, CancellationToken.None);
            if (!useWinRtUpdateStack)
            {
                UpdateStatus = useLocalAccess
                    ? (UseCachedAvailableUpdates
                        ? "Starting Windows Update scan locally from last scan cache..."
                        : "Starting Windows Update scan locally via legacy WUA inventory...")
                    : (UseCachedAvailableUpdates
                        ? "Starting Windows Update scan via last scan cache..."
                        : "Starting Windows Update scan via legacy WUA inventory...");

                await LoadAvailableUpdatesInternalAsync(
                    host,
                    CancellationToken.None,
                    useLocalAccess
                        ? (UseCachedAvailableUpdates
                            ? "Loading available Windows updates from last scan cache locally..."
                            : "Loading available Windows updates via legacy WUA inventory locally...")
                        : (UseCachedAvailableUpdates
                            ? "Loading available Windows updates from last scan cache via WinRM..."
                            : "Loading available Windows updates via legacy WUA inventory via WinRM..."),
                    UseCachedAvailableUpdates);
                ClearBusyState(busyOwnerId);
                return;
            }

            UpdateStatus = useLocalAccess
                ? "Starting Windows Update scan locally and waiting for completion..."
                : "Starting Windows Update scan via WinRM and waiting for completion...";
            UpdateStatus = useLocalAccess
                ? "[WU] Windows Update scan started locally. Waiting for completion..."
                : "[WU] Windows Update scan started. Waiting for completion...";

            var script = BuildPowerShellScriptForHost(host, useLocalAccess, BuildStartUpdateScanScriptBody());
            var execution = await RunPowershellAsync(script, CancellationToken.None);
            AppendExternalCommandDebug("WinRT scan", new ExternalCommandResult(execution.ExitCode, execution.StdOut, execution.StdErr));

            if (execution.ExitCode != 0)
            {
                var error = string.IsNullOrWhiteSpace(execution.StdErr) ? execution.StdOut : execution.StdErr;
                var message = NormalizePowerShellError(error, execution.ExitCode);
                UpdateStatus = $"Update action failed: {message}";
                UpdateStatus = $"[WU][ERROR] {message}";
                return;
            }

            var scanMessage = string.IsNullOrWhiteSpace(execution.StdOut)
                ? "Windows Update scan completed."
                : execution.StdOut.Trim();
            UpdateStatus = $"{scanMessage} Reloading available updates...";
            UpdateStatus = $"[WU] {scanMessage}";

            await LoadAvailableUpdatesInternalAsync(
                host,
                CancellationToken.None,
                useLocalAccess
                    ? (UseCachedAvailableUpdates
                        ? "Reloading available Windows updates from last scan cache..."
                        : "Reloading available Windows updates after completed scan...")
                    : (UseCachedAvailableUpdates
                        ? "Reloading available Windows updates from last scan cache via WinRM..."
                        : "Reloading available Windows updates after completed scan via WinRM..."),
                UseCachedAvailableUpdates);
            ClearBusyState(busyOwnerId);
        }
        catch (JsonException ex)
        {
            UpdateStatus = $"Failed to parse update response: {ex.Message}";
            UpdateStatus = $"[WU][ERROR] {ex.Message}";
            _logger.LogError(ex, "Failed to parse Windows Update response after scan.");
        }
        catch (Exception ex)
        {
            UpdateStatus = $"Update action failed: {ex.Message}";
            UpdateStatus = $"[WU][ERROR] {ex.Message}";
            _logger.LogError(ex, "Windows Update scan action failed.");
        }
        finally
        {
            ClearBusyState();
            IsUpdateBusy = false;
            _updateGate.Release();
        }
    }

    [RelayCommand]
    public Task RestartUpdateServiceAsync()
    {
        return ExecuteUpdateServiceOperationAsync(
            localStartStatus: "Restarting Windows Update service locally...",
            remoteStartStatus: "Restarting Windows Update service via WinRM...",
            primaryScriptBuilder: BuildRestartUpdateServiceScriptBody,
            killScriptBuilder: BuildKillUpdateServiceProcessScriptBody,
            defaultSuccessMessage: "Windows Update service restarted.",
            defaultKillSuccessMessage: "Windows Update service process was terminated and restarted.",
            exceptionLogMessage: "Windows Update service restart action failed.");
    }

    [RelayCommand]
    public Task ResetUpdateCacheAsync()
    {
        return ExecuteUpdateServiceOperationAsync(
            localStartStatus: "Resetting Windows Update cache locally...",
            remoteStartStatus: "Resetting Windows Update cache via WinRM...",
            primaryScriptBuilder: BuildResetUpdateCacheScriptBody,
            killScriptBuilder: BuildKillUpdateServiceProcessAndResetCacheScriptBody,
            defaultSuccessMessage: "Windows Update cache was reset.",
            defaultKillSuccessMessage: "Windows Update service process was terminated and the update cache was reset.",
            exceptionLogMessage: "Windows Update cache reset action failed.");
    }

    [RelayCommand]
    public async Task LoadAvailableUpdatesAsync()
    {
        if (_disposed)
        {
            return;
        }

        var host = _targetHostService.CurrentHost?.Trim() ?? string.Empty;
        CurrentHost = host;

        if (string.IsNullOrWhiteSpace(host))
        {
            UpdateStatus = DisconnectedStatus;
            return;
        }

        if (IsDemoMode)
        {
            ApplyDemoWindowsUpdateSnapshot(host, resetInstallState: false);
            UpdateStatus = "[WU] Loaded available updates from the demo catalog.";
            return;
        }

        await _updateGate.WaitAsync();
        try
        {
            if (_disposed)
            {
                return;
            }

            IsUpdateBusy = true;
            var busyOwnerId = BeginBusyState("Loading available updates", ["Search updates", "Resolve updates", "Refresh list"]);
            await LoadAvailableUpdatesInternalAsync(host, CancellationToken.None, useCachedAvailableUpdates: UseCachedAvailableUpdates);
            ClearBusyState(busyOwnerId);
        }
        catch (JsonException ex)
        {
            UpdateStatus = $"Failed to parse update response: {ex.Message}";
            UpdateStatus = $"[WU][ERROR] {ex.Message}";
            _logger.LogError(ex, "Failed to parse Windows Update response.");
        }
        catch (Exception ex)
        {
            UpdateStatus = $"Update action failed: {ex.Message}";
            UpdateStatus = $"[WU][ERROR] {ex.Message}";
            _logger.LogError(ex, "Windows Update action failed.");
        }
        finally
        {
            ClearBusyState();
            IsUpdateBusy = false;
            _updateGate.Release();
        }
    }

    private async Task EnsureRegisteredUpdateProvidersLoadedAsync(HostSelection? selectionOverride = null, bool forceReload = false)
    {
        if (_disposed || (_registeredUpdateProvidersLoadedForCurrentHost && !forceReload))
        {
            return;
        }

        var selection = selectionOverride ?? _targetHostService.CaptureSelection();
        using var linkedCancellationTokenSource = CreateHostLinkedCancellation(selection, CancellationToken.None);
        await _providerLoadGate.WaitAsync();
        try
        {
            if (_disposed || (_registeredUpdateProvidersLoadedForCurrentHost && !forceReload))
            {
                return;
            }

            var host = selection.Host?.Trim() ?? string.Empty;
            CurrentHost = host;
            if (string.IsNullOrWhiteSpace(host))
            {
                RegisteredUpdateProvidersInfo = "Registered update providers: client is not connected.";
                ResetRegisteredUpdateProvidersHealth();
                return;
            }

            if (IsDemoMode)
            {
                ApplyDemoRegisteredUpdateProviders(_demoDataCatalog.CreateWindowsUpdateSnapshot(host).Providers);
                _registeredUpdateProvidersLoadedForCurrentHost = true;
                UpdateStatus = "[WU] Loaded registered update providers from the demo catalog.";
                return;
            }

            IsRegisteredUpdateProvidersLoading = true;
            var busyOwnerId = BeginBusyState("Loading update providers", ["Query providers", "Assess Microsoft Update"]);
            RegisteredUpdateProvidersInfo = "Loading registered update providers...";
            _registeredUpdateProvidersLoadedForCurrentHost = true;
            var useLocalAccess = IsLocalHost(host);
            var script = BuildPowerShellScriptForHost(host, useLocalAccess, BuildRegisteredUpdateProvidersScriptBody());
            var execution = await RunPowershellAsync(script, linkedCancellationTokenSource.Token);
            EnsureCurrentSelection(selection);
            if (execution.ExitCode != 0 && string.IsNullOrWhiteSpace(execution.StdOut))
            {
                throw new InvalidOperationException("Failed to load registered update providers.");
            }

            var providers = ParseRegisteredUpdateProvidersPayload(execution.StdOut);
            RegisteredUpdateProviders.Clear();
            foreach (var provider in providers)
            {
                RegisteredUpdateProviders.Add(provider);
            }

            SetRegisteredUpdateProvidersHealth(providers);
            RegisteredUpdateProvidersInfo = BuildRegisteredUpdateProvidersInfo(providers);
            UpdateStatus = $"[WU] Loaded {providers.Count} registered update providers.";
            ClearBusyState(busyOwnerId);
        }
        catch (OperationCanceledException) when (selection.CancellationToken.IsCancellationRequested)
        {
            _registeredUpdateProvidersLoadedForCurrentHost = false;
        }
        catch (Exception ex)
        {
            _registeredUpdateProvidersLoadedForCurrentHost = false;
            _logger.LogDebug(ex, "Failed to load registered update providers.");
            RegisteredUpdateProvidersInfo = "Registered update providers: unavailable.";
            SetRegisteredUpdateProvidersHealth([]);
        }
        finally
        {
            IsRegisteredUpdateProvidersLoading = false;
            ClearBusyState();
            _providerLoadGate.Release();
        }
    }

    private async Task EnsureAutopatchRingLoadedAsync(HostSelection selection)
    {
        if (_disposed)
        {
            return;
        }

        var host = selection.Host;
        if (string.IsNullOrWhiteSpace(host))
        {
            AutopatchRingText = "Unknown";
            return;
        }

        try
        {
            using var linkedCancellationTokenSource = CreateHostLinkedCancellation(selection, CancellationToken.None);
            var snapshot = await _localIntuneDiagnosticsService.GetOverviewCoreSnapshotAsync(host, linkedCancellationTokenSource.Token);
            if (_disposed || !_targetHostService.IsCurrent(selection))
            {
                return;
            }

            AutopatchRingText = string.IsNullOrWhiteSpace(snapshot.UpdateRingText) ? "Unknown" : snapshot.UpdateRingText;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to load Windows Autopatch ring for host {Host}.", host);
            if (!_targetHostService.IsCurrent(selection))
            {
                return;
            }

            AutopatchRingText = "Unknown";
        }
    }

    private async Task LoadAvailableUpdatesInternalAsync(string host, CancellationToken cancellationToken, string? statusOverride = null, bool useCachedAvailableUpdates = true)
    {
        if (IsDemoMode)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ApplyDemoWindowsUpdateSnapshot(host, resetInstallState: false);
            UpdateStatus = statusOverride ?? "[WU] Loaded available updates from the demo catalog.";
            return;
        }

        var useLocalAccess = IsLocalHost(host);
        var useWinRtUpdateStack = await ShouldUseWinRtUpdateStackAsync(host, useLocalAccess, cancellationToken);
        string? scriptPathToDelete = null;
        UpdateStatus = statusOverride ?? (useWinRtUpdateStack
            ? useLocalAccess
                ? "Loading available Windows updates via WinRT inventory locally..."
                : "Loading available Windows updates via WinRT inventory via WinRM..."
            : useLocalAccess
                ? (useCachedAvailableUpdates
                    ? "Loading available Windows updates from last scan cache locally..."
                    : "Loading available Windows updates via legacy WUA inventory locally...")
                : (useCachedAvailableUpdates
                    ? "Loading available Windows updates from last scan cache via WinRM..."
                    : "Loading available Windows updates via legacy WUA inventory via WinRM..."));

        try
        {
            string payloadJson;
            if (useWinRtUpdateStack)
            {
                var scriptPath = ResolveBundledPowerShellScriptExecutionPath(WinRtUpdateClientScriptRelativePath, out var deleteAfterUse);
                if (deleteAfterUse)
                {
                    scriptPathToDelete = scriptPath;
                }

                var arguments = new List<string> { "-Inventory", "-AsJson" };
                if (!useLocalAccess)
                {
                    arguments.Add("-ComputerName");
                    arguments.Add(host);
                }

                AppendLine($"[WU][DEBUG] Running WinRT inventory script for '{host}' with args: {string.Join(" ", arguments)}");
                var execution = await RunPowerShellFileAsync(scriptPath, arguments, cancellationToken);
                AppendExternalCommandDebug("WinRT inventory", execution);
                if (!TryExtractJsonPayload(execution, out payloadJson, out var commandError))
                {
                    throw new InvalidOperationException(commandError);
                }

                AppendWinRtPayloadDebug("inventory", payloadJson);
            }
            else
            {
                AppendLine($"[WU][DEBUG] Running legacy WUA inventory script for '{host}'.");
                var script = BuildPowerShellScriptForHost(host, useLocalAccess, BuildAvailableUpdatesLegacyScriptBody(useCachedAvailableUpdates));
                var execution = await RunPowershellAsync(script, cancellationToken);
                var commandResult = new ExternalCommandResult(execution.ExitCode, execution.StdOut, execution.StdErr);
                AppendExternalCommandDebug("legacy inventory", commandResult);
                if (!TryExtractJsonPayload(commandResult, out payloadJson, out var commandError))
                {
                    throw new InvalidOperationException(commandError);
                }
            }

            var parsed = ParseAvailableUpdatesPayload(
                payloadJson,
                RegisteredUpdateProviders.ToArray());

            AvailableUpdates.Clear();
            foreach (var update in parsed.Updates)
            {
                AvailableUpdates.Add(update);
                AppendLine(
                    $"[WU][DEBUG] Available update found: Title='{update.Title}' UpdateId='{update.UpdateId}' Revision={update.Revision} Downloaded={update.IsDownloaded} Installed={update.IsInstalled} Hidden={update.IsHidden} Status='{update.Status}' Type='{update.Type}' Categories='{update.Categories}'");
            }

            RefreshVisibleAvailableUpdates();
            AppendCollectionDebug("available-updates", parsed.Updates);
            AppendLine($"[WU][DEBUG] Visible available updates after filter: {VisibleAvailableUpdates.Count} (selected view index: {SelectedAvailableUpdatesViewIndex})");

            SetRegisteredUpdateProvidersHealth(parsed.Providers);
            RegisteredUpdateProvidersInfo = BuildRegisteredUpdateProvidersInfo(parsed.Providers);
            UpdateStatus = $"[WU] Loaded {parsed.UpdateCount} software/driver updates.";
            LastAvailableUpdatesScanInfo = BuildLastScanInfo(parsed.LastSearchSuccessDate);
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(scriptPathToDelete))
            {
                TryDeleteFile(scriptPathToDelete);
            }
        }
    }

    [RelayCommand]
    public async Task InstallSelectedUpdatesAsync()
    {
        if (_disposed)
        {
            return;
        }

        var host = _targetHostService.CurrentHost?.Trim() ?? string.Empty;
        CurrentHost = host;

        if (string.IsNullOrWhiteSpace(host))
        {
            UpdateStatus = DisconnectedStatus;
            return;
        }

        var selectedUpdates = AvailableUpdates
            .Where(update => update.IsSelected && !update.IsInstalled)
            .Select(update => new InstallSelectionItem(update.Title, update.UpdateId, update.Revision))
            .ToArray();

        if (selectedUpdates.Length == 0)
        {
            UpdateStatus = "No non-installed updates selected.";
            return;
        }

        if (IsDemoMode)
        {
            _demoInstallTaskStarted = true;
            InstallTaskState = "Demo install task completed.";
            InstallTaskStatusText = "Task: Completed (Demo)";
            InstallTaskPhaseText = "Phase: completed";
            InstallTaskDetail = $"{selectedUpdates.Length} update(s) were simulated successfully. No package was installed on any machine.";
            IsInstallTaskRunning = false;
            InstallProgressEntries.Clear();
            foreach (var line in _demoDataCatalog.CreateWindowsUpdateSnapshot(host).BaseInstallProgressLines)
            {
                InstallProgressEntries.Add(InstallProgressEntry.FromLogLine(line));
            }

            InstallProgressEntries.Add(InstallProgressEntry.FromLogLine($"[2026-04-18 08:05:12] Demo install simulated for {selectedUpdates.Length} selected update(s)."));
            InstallProgressEntries.Add(InstallProgressEntry.FromLogLine("[2026-04-18 08:05:35] Demo install completed without side effects."));

            foreach (var update in AvailableUpdates)
            {
                update.IsSelected = false;
            }

            UpdateStatus = $"[WU] Demo install simulated for {selectedUpdates.Length} update(s).";
            return;
        }

        await _updateGate.WaitAsync();
        try
        {
            IsUpdateBusy = true;
            var busyOwnerId = BeginBusyState("Scheduling update installation", ["Write payload", "Create task", "Start task"]);
            var useLocalAccess = IsLocalHost(host);
            var useAsyncScript = UseAsyncInstallScript;
            var useWinRtUpdateStack = await ShouldUseWinRtUpdateStackAsync(host, useLocalAccess, CancellationToken.None);
            var useWinRtInstallScript = UseWinRtInstallScript && useWinRtUpdateStack;
            var selectedScriptFileName = GetSelectedInstallScriptFileName(useAsyncScript, useWinRtInstallScript);
            var installModeLabel = GetInstallModeLabel(useAsyncScript, useWinRtInstallScript);
            if (UseWinRtInstallScript && !useWinRtUpdateStack)
            {
                installModeLabel += " (legacy COM fallback for Windows 10)";
            }
            UpdateStatus = useLocalAccess
                ? $"Scheduling installation of {selectedUpdates.Length} updates locally ({installModeLabel})..."
                : $"Scheduling installation of {selectedUpdates.Length} updates via Scheduled Task ({installModeLabel})...";

            var localScriptPath = Path.Combine(InstallWorkDirectory, selectedScriptFileName);
            var localLauncherPath = Path.Combine(InstallWorkDirectory, InstallLauncherFileName);
            var localPayloadPath = Path.Combine(InstallWorkDirectory, InstallPayloadFileName);
            var localStatePath = Path.Combine(InstallWorkDirectory, InstallStateFileName);
            var localProgressLogPath = Path.Combine(InstallWorkDirectory, InstallProgressLogFileName);

            var writeScriptPath = useLocalAccess ? localScriptPath : BuildRemoteAdminPath(host, @"ProgramData\WindowsClientCenter\WindowsUpdateAgent\" + selectedScriptFileName);
            var writeLauncherPath = useLocalAccess ? localLauncherPath : BuildRemoteAdminPath(host, @"ProgramData\WindowsClientCenter\WindowsUpdateAgent\" + InstallLauncherFileName);
            var writePayloadPath = useLocalAccess ? localPayloadPath : BuildRemoteAdminPath(host, @"ProgramData\WindowsClientCenter\WindowsUpdateAgent\" + InstallPayloadFileName);
            var writeStatePath = useLocalAccess ? localStatePath : BuildRemoteAdminPath(host, @"ProgramData\WindowsClientCenter\WindowsUpdateAgent\" + InstallStateFileName);
            var writeProgressLogPath = useLocalAccess ? localProgressLogPath : BuildRemoteAdminPath(host, @"ProgramData\WindowsClientCenter\WindowsUpdateAgent\" + InstallProgressLogFileName);

            Directory.CreateDirectory(Path.GetDirectoryName(writeScriptPath) ?? InstallWorkDirectory);

            var scriptBody = useWinRtInstallScript
                ? BuildInstallUpdatesWinRtScriptBody()
                : useAsyncScript
                    ? BuildInstallUpdatesAsyncScriptBody()
                    : BuildInstallUpdatesScriptBody();
            await File.WriteAllTextAsync(writeScriptPath, scriptBody, Encoding.UTF8);
            await File.WriteAllTextAsync(
                writeLauncherPath,
                BuildInstallTaskLauncherScriptBody(localScriptPath, localPayloadPath, localStatePath, localProgressLogPath),
                Encoding.UTF8);
            var payloadJson = JsonSerializer.Serialize(selectedUpdates, JsonOptions);
            await File.WriteAllTextAsync(writePayloadPath, payloadJson, Encoding.UTF8);
            await File.WriteAllTextAsync(
                writeProgressLogPath,
                $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] Queued {selectedUpdates.Length} update(s) for installation ({installModeLabel}).{Environment.NewLine}",
                Encoding.UTF8);

            var initialState = new InstallStatusPayload
            {
                Phase = "queued",
                Message = "Scheduled task queued.",
                CurrentTitle = string.Empty,
                TotalCount = selectedUpdates.Length,
                CompletedCount = 0,
                InstalledCount = 0,
                FailedCount = 0,
                RebootRequired = false,
                FailedTitles = [],
                LastUpdatedUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)
            };
            await File.WriteAllTextAsync(writeStatePath, JsonSerializer.Serialize(initialState, JsonOptions), Encoding.UTF8);

            var scheduledAt = DateTime.Now.AddMinutes(1);
            var remoteSwitch = useLocalAccess ? string.Empty : $" /S \"{host}\"";
            var taskCommand = BuildInstallTaskCommand(localLauncherPath);

            if (useLocalAccess)
            {
                var createArgs =
                    $"/Create{remoteSwitch} /TN \"{InstallTaskName}\" /TR \"{taskCommand}\" /SC ONCE /ST {scheduledAt:HH:mm} /RU SYSTEM /RL HIGHEST /F";
                var createResult = await RunProcessAsync("schtasks.exe", createArgs, CancellationToken.None);
                if (createResult.ExitCode != 0)
                {
                    var createError = NormalizeExternalCommandError(createResult);
                    UpdateStatus = $"[WU][ERROR] Failed to create install task: {createError}";
                    return;
                }

                var runArgs = $"/Run{remoteSwitch} /TN \"{InstallTaskName}\"";
                var runResult = await RunProcessAsync("schtasks.exe", runArgs, CancellationToken.None);
                if (runResult.ExitCode != 0)
                {
                    var runError = NormalizeExternalCommandError(runResult);
                    UpdateStatus = $"[WU][ERROR] Install task created but could not be started: {runError}";
                    return;
                }

                var disableArgs = $"/Change{remoteSwitch} /TN \"{InstallTaskName}\" /DISABLE";
                var disableResult = await RunProcessAsync("schtasks.exe", disableArgs, CancellationToken.None);
                if (disableResult.ExitCode != 0)
                {
                    var disableError = NormalizeExternalCommandError(disableResult);
                    UpdateStatus = $"[WU][ERROR] Install task started but could not be disabled: {disableError}";
                    return;
                }
            }
            else
            {
                var remoteScript = BuildRemoteSchtasksScript(InstallTaskName, taskCommand, scheduledAt);
                var execution = await RunPowershellAsync(BuildPowerShellScriptForHost(host, useLocalAccess: false, remoteScript), CancellationToken.None);
                if (execution.ExitCode != 0)
                {
                    var createError = NormalizePowerShellError(string.IsNullOrWhiteSpace(execution.StdErr) ? execution.StdOut : execution.StdErr, execution.ExitCode);
                    UpdateStatus = $"[WU][ERROR] Failed to create install task: {createError}";
                    return;
                }
            }

            _activeInstallHost = host;
            _activeInstallStatusPath = writeStatePath;
            _activeInstallProgressLogPath = writeProgressLogPath;
            _installAutoRestartTriggered = false;
            ResetInstallProgressTracking(writeProgressLogPath);
            InstallTaskState = "Scheduled task started.";
            InstallTaskStatusText = "Task: Running";
            InstallTaskPhaseText = "Phase: queued";
            InstallTaskDetail = $"{selectedUpdates.Length} update(s) queued for processing ({installModeLabel}).";
            IsInstallTaskRunning = true;
            InstallProgressEntries.Clear();

            foreach (var update in AvailableUpdates)
            {
                update.IsSelected = false;
            }

            UpdateStatus = $"[WU] Install task '{InstallTaskName}' started with {selectedUpdates.Length} selected update(s).";
            EnsureGlobalInstallBusyState(host, "queued", selectedUpdates.Length, 0, string.Empty);

            await RefreshInstallTaskStatusInternalAsync(CancellationToken.None);
            EnsureInstallStatusAutoRefresh();
            ClearBusyState(busyOwnerId);
        }
        catch (Exception ex)
        {
            UpdateStatus = $"[WU][ERROR] Failed to schedule update installation: {ex.Message}";
            _logger.LogError(ex, "Failed to schedule selected update installation.");
        }
        finally
        {
            ClearBusyState();
            IsUpdateBusy = false;
            _updateGate.Release();
        }
    }

    [RelayCommand]
    public Task RefreshInstallTaskStatusAsync()
    {
        return RefreshInstallTaskStatusInternalAsync(CancellationToken.None, forceProgressReload: true);
    }

    [RelayCommand]
    public async Task LoadUpdateHistoryAsync()
    {
        if (_disposed)
        {
            return;
        }

        var host = _targetHostService.CurrentHost?.Trim() ?? string.Empty;
        CurrentHost = host;

        if (string.IsNullOrWhiteSpace(host))
        {
            UpdateStatus = DisconnectedStatus;
            return;
        }

        if (IsDemoMode)
        {
            ApplyDemoHistory(_demoDataCatalog.CreateWindowsUpdateSnapshot(host).HistoryEntries);
            UpdateStatus = "[WU] Loaded update history from the demo catalog.";
            return;
        }

        await _updateGate.WaitAsync();
        try
        {
            IsUpdateBusy = true;
            var busyOwnerId = BeginBusyState("Loading update history", ["Query history", "Fallback to store.db"]);
            var useLocalAccess = IsLocalHost(host);
            var useWinRtUpdateStack = await ShouldUseWinRtUpdateStackAsync(host, useLocalAccess, CancellationToken.None);
            var historySource = useWinRtUpdateStack
                ? "WinRT completed-updates API"
                : "USO UpdateStore (store.db)";
            UpdateStatus = useWinRtUpdateStack
                ? "Loading recent Windows Update history via WinRT completed-updates API..."
                : "Loading recent Windows Update history from store.db...";
            UpdateHistoryPayload parsed;
            if (useWinRtUpdateStack)
            {
                try
                {
                    parsed = await LoadUpdateHistoryFromWinRtAsync(host, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to load recent Windows Update history via WinRT. Falling back to store.db.");
                    EnsureSqliteInitialized();
                    parsed = await LoadUpdateHistoryFromUsoStoreAsync(host, CancellationToken.None);
                    historySource = "USO UpdateStore (store.db)";
                }
            }
            else
            {
                EnsureSqliteInitialized();
                parsed = await LoadUpdateHistoryFromUsoStoreAsync(host, CancellationToken.None);
            }

            UpdateHistoryEntries.Clear();
            foreach (var entry in parsed.Entries)
            {
                UpdateHistoryEntries.Add(entry);
            }

            AppendCollectionDebug("update-history", parsed.Entries);
            AppendLine($"[WU][DEBUG] Update history source='{historySource}' returned={parsed.ReturnedCount} total={parsed.TotalCount} grid={UpdateHistoryEntries.Count}");

            if (string.Equals(historySource, "WinRT completed-updates API", StringComparison.Ordinal))
            {
                UpdateStatus = $"[WU] Loaded {parsed.ReturnedCount} recent completed update(s) via WinRT.";
            }
            else
            {
                UpdateStatus = $"Loaded {parsed.ReturnedCount} history entries (total: {parsed.TotalCount}) from store.db.";
                UpdateStatus = $"[WU] Loaded {parsed.ReturnedCount} update history entries (total: {parsed.TotalCount}) from store.db.";
            }
            _ = PrefetchUsoDiagnosticsForHostAsync(host);
            ClearBusyState(busyOwnerId);
        }
        catch (Exception ex)
        {
            var detailedMessage = FormatExceptionWithInnerMessages(ex);
            UpdateStatus = $"Failed to load update history: {detailedMessage}";
            UpdateStatus = $"[WU][ERROR] Failed to load update history: {detailedMessage}";
            _logger.LogError(ex, "Failed to load update history.");
        }
        finally
        {
            ClearBusyState();
            IsUpdateBusy = false;
            _updateGate.Release();
        }
    }

    [RelayCommand(CanExecute = nameof(CanTryUninstallHistoryEntry))]
    public async Task TryUninstallHistoryEntryAsync(WindowsUpdateHistoryEntry? entry)
    {
        if (_disposed || entry is null)
        {
            return;
        }

        var host = _targetHostService.CurrentHost?.Trim() ?? string.Empty;
        CurrentHost = host;

        if (string.IsNullOrWhiteSpace(host))
        {
            UpdateStatus = DisconnectedStatus;
            return;
        }

        if (IsDemoMode)
        {
            UpdateHistoryEntries.Remove(entry);
            UpdateStatus = $"[WU] Demo uninstall simulated for '{entry.Title}'.";
            return;
        }

        SplitUpdateIdentity(entry.UpdateId, out var updateId, out var parsedRevision);
        var revision = parsedRevision > 0 ? parsedRevision : entry.Revision;
        var packageName = entry.PackageName?.Trim() ?? string.Empty;
        var title = entry.Title?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(packageName) &&
            string.IsNullOrWhiteSpace(title) &&
            string.IsNullOrWhiteSpace(updateId))
        {
            UpdateStatus = "[WU][ERROR] The selected history entry does not expose a usable update or package identity.";
            return;
        }

        var confirmResult = MessageBox.Show(
            $"Attempt to uninstall this update?\n\n{entry.Title}\n\nUpdate ID: {updateId}\nRevision: {(revision > 0 ? revision.ToString(CultureInfo.InvariantCulture) : "n/a")}\nPackage: {(string.IsNullOrWhiteSpace(packageName) ? "n/a" : packageName)}",
            "Uninstall Windows Update",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (confirmResult != MessageBoxResult.Yes)
        {
            return;
        }

        var refreshHistoryAfterUninstall = false;
        await _updateGate.WaitAsync();
        try
        {
            if (_disposed)
            {
                return;
            }

            IsUpdateBusy = true;
            var busyOwnerId = BeginBusyState("Uninstalling update", ["Resolve installed update", "Run Windows Update uninstall"]);
            var useLocalAccess = IsLocalHost(host);
            UpdateStatus = useLocalAccess
                ? $"Attempting to uninstall update '{entry.Title}' locally..."
                : $"Attempting to uninstall update '{entry.Title}' via WinRM...";

            var script = BuildPowerShellScriptForHost(host, useLocalAccess, BuildUninstallHistoryUpdateScriptBody(updateId, revision, entry.Title ?? updateId, packageName));
            var execution = await RunPowershellAsync(script, CancellationToken.None);
            AppendExternalCommandDebug("Win32 uninstall", new ExternalCommandResult(execution.ExitCode, execution.StdOut, execution.StdErr));

            if (execution.ExitCode != 0)
            {
                var error = string.IsNullOrWhiteSpace(execution.StdErr) ? execution.StdOut : execution.StdErr;
                var message = NormalizePowerShellError(error, execution.ExitCode);
                UpdateStatus = $"Update uninstall failed: {message}";
                UpdateStatus = $"[WU][ERROR] {message}";
                return;
            }

            if (!TryParseUninstallHistoryUpdateResult(execution.StdOut, out var uninstallResult, out var parseError))
            {
                UpdateStatus = $"Update uninstall failed: {parseError}";
                UpdateStatus = $"[WU][ERROR] {parseError}";
                return;
            }

            if (!uninstallResult.Success)
            {
                var message = string.IsNullOrWhiteSpace(uninstallResult.Message)
                    ? "The Windows Update uninstall operation failed."
                    : uninstallResult.Message;
                UpdateStatus = $"Update uninstall failed: {message}";
                UpdateStatus = $"[WU][ERROR] {message}";
                return;
            }

            UpdateStatus = uninstallResult.RebootRequired
                ? $"Update uninstall completed. Reboot required. {uninstallResult.Message}"
                : uninstallResult.Message;
            UpdateStatus = $"[WU] {uninstallResult.Message}";
            refreshHistoryAfterUninstall = true;
        }
        catch (Exception ex)
        {
            UpdateStatus = $"Update uninstall failed: {ex.Message}";
            UpdateStatus = $"[WU][ERROR] {ex.Message}";
            _logger.LogError(ex, "Windows Update uninstall action failed.");
        }
        finally
        {
            ClearBusyState();
            IsUpdateBusy = false;
            _updateGate.Release();
        }

        if (refreshHistoryAfterUninstall && !_disposed)
        {
            await LoadUpdateHistoryAsync();
        }
    }

    private bool CanTryUninstallHistoryEntry(WindowsUpdateHistoryEntry? entry)
    {
        return entry is not null &&
               !string.Equals(entry.Operation, "Uninstallation", StringComparison.OrdinalIgnoreCase) &&
               (!string.IsNullOrWhiteSpace(entry.PackageName) ||
                !string.IsNullOrWhiteSpace(entry.UpdateId) ||
                !string.IsNullOrWhiteSpace(entry.Title));
    }

    partial void OnTailLineCountChanged(int value)
    {
        if (value < MinTailLineCount)
        {
            TailLineCount = MinTailLineCount;
        }
        else if (value > MaxTailLineCount)
        {
            TailLineCount = MaxTailLineCount;
        }

        RefreshVisibleEntries();
    }

    partial void OnHighlightedUpdateIdChanged(string? value)
    {
        RefreshVisibleEntries();
    }

    partial void OnStatusChanged(string value)
    {
        ForwardStatusToHost(value);
    }

    partial void OnUpdateStatusChanged(string value)
    {
        ForwardStatusToHost(value);
    }

    partial void OnIsInstallStatusAutoRefreshEnabledChanged(bool value)
    {
        if (value)
        {
            EnsureInstallStatusAutoRefresh();
        }
        else
        {
            StopInstallStatusAutoRefresh();
        }
    }

    partial void OnRestartAfterInstallIfRequiredChanged(bool value)
    {
        if (value)
        {
            EnsureInstallStatusAutoRefresh();
        }
        else if (!IsInstallStatusAutoRefreshEnabled)
        {
            StopInstallStatusAutoRefresh();
        }
    }

    partial void OnSelectedUpdateApiChanged(string value)
    {
        if (string.Equals(value, UpdateApiCom, StringComparison.OrdinalIgnoreCase))
        {
            UseWinRtInstallScript = false;
        }
        else if (string.Equals(value, UpdateApiWinRt, StringComparison.OrdinalIgnoreCase) && !UseWinRtInstallScript)
        {
            UseWinRtInstallScript = true;
        }
    }

    partial void OnUseAsyncInstallScriptChanged(bool value)
    {
        if (value && UseWinRtInstallScript)
        {
            UseWinRtInstallScript = false;
        }
    }

    partial void OnUseWinRtInstallScriptChanged(bool value)
    {
        if (value && UseAsyncInstallScript)
        {
            UseAsyncInstallScript = false;
        }
    }

    public void ToggleUpdateIdHighlight(string? updateId)
    {
        var normalized = string.IsNullOrWhiteSpace(updateId) ? null : updateId.Trim();
        if (normalized is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(HighlightedUpdateId))
        {
            HighlightedUpdateId = normalized;
            return;
        }

        // Clicking again (same or different ID) clears current group highlight.
        HighlightedUpdateId = null;
    }

    partial void OnIsMonitoringChanged(bool value)
    {
        OnPropertyChanged(nameof(MonitoringToggleIconGlyph));
        OnPropertyChanged(nameof(MonitoringToggleToolTip));
    }

    partial void OnIsMonitoringBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(IsMonitoringToggleEnabled));
    }

    partial void OnSelectedAvailableUpdatesViewIndexChanged(int value)
    {
        RefreshVisibleAvailableUpdates();
    }

    partial void OnSelectedSectionIndexChanged(int value)
    {
        if (value == 0)
        {
            _ = EnsureRegisteredUpdateProvidersLoadedAsync();
        }

        if (value == UsoDiagnosticsSectionIndex)
        {
            _ = EnsureUsoDiagnosticsLoadedAsync();
        }
    }

    public string MonitoringToggleIconGlyph => IsMonitoring ? "\uE769" : "\uE768";

    public string MonitoringToggleToolTip => IsMonitoring ? "Stop stream" : "Start stream";

    public bool IsMonitoringToggleEnabled => !IsMonitoringBusy;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _targetHostService.HostChanged -= OnHostChanged;
        ClearBusyState();

        if (_monitorGate.Wait(500))
        {
            try
            {
                StopProcessInternal();
                StopInstallStatusAutoRefresh();
                ClearInstallBusyState();
            }
            finally
            {
                _monitorGate.Release();
            }
        }
        else
        {
            StopProcessInternal();
            StopInstallStatusAutoRefresh();
            ClearInstallBusyState();
        }

        _monitorGate.Dispose();
        _updateGate.Dispose();
        _providerLoadGate.Dispose();
    }

    private async Task ExecuteUpdateActionAsync(
        string localStartStatus,
        string remoteStartStatus,
        Func<string> scriptBuilder,
        Action<WindowsClientCenter.Intune.Services.Runtime.PowershellExecutionResult> onSuccess)
    {
        if (_disposed)
        {
            return;
        }

        var host = _targetHostService.CurrentHost?.Trim() ?? string.Empty;
        CurrentHost = host;

        if (string.IsNullOrWhiteSpace(host))
        {
            UpdateStatus = DisconnectedStatus;
            return;
        }

        await _updateGate.WaitAsync();
        try
        {
            if (_disposed)
            {
                return;
            }

            IsUpdateBusy = true;
            var busyOwnerId = BeginBusyState(string.IsNullOrWhiteSpace(remoteStartStatus) ? "Windows Update action" : remoteStartStatus, [localStartStatus]);
            var useLocalAccess = IsLocalHost(host);
            UpdateStatus = useLocalAccess
                ? localStartStatus
                : remoteStartStatus;

            var scriptBody = scriptBuilder();
            var script = BuildPowerShellScriptForHost(host, useLocalAccess, scriptBody);
            var execution = await RunPowershellAsync(script, CancellationToken.None);

            if (execution.ExitCode != 0)
            {
                var error = string.IsNullOrWhiteSpace(execution.StdErr) ? execution.StdOut : execution.StdErr;
                var message = NormalizePowerShellError(error, execution.ExitCode);
                UpdateStatus = $"Update action failed: {message}";
                UpdateStatus = $"[WU][ERROR] {message}";
                ClearBusyState(busyOwnerId);
                return;
            }

            onSuccess(execution);
            ClearBusyState(busyOwnerId);
        }
        catch (JsonException ex)
        {
            UpdateStatus = $"Failed to parse update response: {ex.Message}";
            UpdateStatus = $"[WU][ERROR] {ex.Message}";
            _logger.LogError(ex, "Failed to parse Windows Update response.");
        }
        catch (Exception ex)
        {
            UpdateStatus = $"Update action failed: {ex.Message}";
            UpdateStatus = $"[WU][ERROR] {ex.Message}";
            _logger.LogError(ex, "Windows Update action failed.");
        }
        finally
        {
            ClearBusyState();
            IsUpdateBusy = false;
            _updateGate.Release();
        }
    }

    private Task<WindowsClientCenter.Intune.Services.Runtime.PowershellExecutionResult> RunUpdateServiceActionAsync(string host, bool useLocalAccess, Func<string> scriptBuilder)
    {
        var script = BuildPowerShellScriptForHost(host, useLocalAccess, scriptBuilder());
        return RunPowershellAsync(script, CancellationToken.None);
    }

    private async Task ExecuteUpdateServiceOperationAsync(
        string localStartStatus,
        string remoteStartStatus,
        Func<string> primaryScriptBuilder,
        Func<int, string> killScriptBuilder,
        string defaultSuccessMessage,
        string defaultKillSuccessMessage,
        string exceptionLogMessage)
    {
        if (_disposed)
        {
            return;
        }

        var host = _targetHostService.CurrentHost?.Trim() ?? string.Empty;
        CurrentHost = host;

        if (string.IsNullOrWhiteSpace(host))
        {
            UpdateStatus = DisconnectedStatus;
            return;
        }

        if (IsDemoMode)
        {
            UpdateStatus = $"[WU] {defaultSuccessMessage} (demo simulation only)";
            return;
        }

        await _updateGate.WaitAsync();
        try
        {
            if (_disposed)
            {
                return;
            }

            IsUpdateBusy = true;
            var busyOwnerId = BeginBusyState(string.IsNullOrWhiteSpace(remoteStartStatus) ? "Windows Update service" : remoteStartStatus, [localStartStatus]);
            var useLocalAccess = IsLocalHost(host);
            UpdateStatus = useLocalAccess
                ? localStartStatus
                : remoteStartStatus;

            var execution = await RunUpdateServiceActionAsync(host, useLocalAccess, primaryScriptBuilder);
            if (TryParseUpdateServiceKillRequired(execution.StdOut, out var processId, out var serviceStatus))
            {
                var prompt = BuildHardKillPrompt(host, processId, serviceStatus);
                if (!ConfirmViaMessageBox("Kill Windows Update Service Process?", prompt))
                {
                    UpdateStatus = "Windows Update service stop timed out. Hard kill was cancelled.";
                    UpdateStatus = "[WU][WARN] Windows Update service stop timed out. Hard kill was cancelled.";
                    ClearBusyState(busyOwnerId);
                    return;
                }

                var killExecution = await RunUpdateServiceActionAsync(
                    host,
                    useLocalAccess,
                    () => killScriptBuilder(processId));

                if (killExecution.ExitCode != 0)
                {
                    var killError = NormalizePowerShellError(
                        string.IsNullOrWhiteSpace(killExecution.StdErr) ? killExecution.StdOut : killExecution.StdErr,
                        killExecution.ExitCode);
                    UpdateStatus = $"Hard kill of Windows Update service failed: {killError}";
                    UpdateStatus = $"[WU][ERROR] Hard kill of Windows Update service failed: {killError}";
                    ClearBusyState(busyOwnerId);
                    return;
                }

                var killMessage = string.IsNullOrWhiteSpace(killExecution.StdOut)
                    ? defaultKillSuccessMessage
                    : killExecution.StdOut.Trim();
                UpdateStatus = killMessage;
                UpdateStatus = $"[WU] {killMessage}";
                ClearBusyState(busyOwnerId);
                return;
            }

            if (execution.ExitCode != 0)
            {
                var error = string.IsNullOrWhiteSpace(execution.StdErr) ? execution.StdOut : execution.StdErr;
                var message = NormalizePowerShellError(error, execution.ExitCode);
                UpdateStatus = $"Update action failed: {message}";
                UpdateStatus = $"[WU][ERROR] {message}";
                ClearBusyState(busyOwnerId);
                return;
            }

            var successMessage = string.IsNullOrWhiteSpace(execution.StdOut)
                ? defaultSuccessMessage
                : execution.StdOut.Trim();
            UpdateStatus = successMessage;
            UpdateStatus = $"[WU] {successMessage}";
            ClearBusyState(busyOwnerId);
        }
        catch (Exception ex)
        {
            UpdateStatus = $"Update action failed: {ex.Message}";
            UpdateStatus = $"[WU][ERROR] {ex.Message}";
            _logger.LogError(ex, exceptionLogMessage);
        }
        finally
        {
            ClearBusyState();
            IsUpdateBusy = false;
            _updateGate.Release();
        }
    }

    private async Task StartMonitoringInternalAsync(CancellationToken cancellationToken, bool fullReload)
    {
        if (_disposed)
        {
            return;
        }

        var selection = _targetHostService.CaptureSelection();
        using var linkedCancellationTokenSource = CreateHostLinkedCancellation(selection, cancellationToken);
        await _monitorGate.WaitAsync(linkedCancellationTokenSource.Token);
        try
        {
            IsMonitoringBusy = true;
            var busyOwnerId = BeginBusyState("Loading ReportingEvents.log", ["Open log", "Stream updates", "Refresh view"]);
            StopProcessInternal();

            var host = selection.Host?.Trim() ?? string.Empty;
            CurrentHost = host;

            if (string.IsNullOrWhiteSpace(host))
            {
                Status = DisconnectedStatus;
                return;
            }

            if (IsDemoMode)
            {
                StopProcessInternal();
                ApplyDemoWindowsUpdateSnapshot(host, resetInstallState: fullReload);
                IsMonitoring = true;
                Status = $@"Streaming C:\Windows\SoftwareDistribution\ReportingEvents.log from '{host}' (demo mode).";
                ClearReportingEventsLoadingOverlay();
                ClearBusyState(busyOwnerId);
                return;
            }

            var clampedTail = Math.Clamp(TailLineCount, MinTailLineCount, MaxTailLineCount);
            var useLocalAccess = IsLocalHost(host);
            var logPath = useLocalAccess ? ReportingEventsLogPath : BuildRemoteReportingEventsUncPath(host);
            var includeTailSnapshot = fullReload;
            long? resumePosition = null;

            if (fullReload)
            {
                ResetStreamCheckpoint();
                ClearLog();
            }
            else if (TryGetStreamCheckpoint(host, logPath, out var checkpointPosition))
            {
                resumePosition = checkpointPosition;
            }

            var sessionId = Interlocked.Increment(ref _streamSessionId);
            var streamTokenSource = CancellationTokenSource.CreateLinkedTokenSource(selection.CancellationToken);

            _streamCancellationTokenSource = streamTokenSource;
            _streamTask = StreamReportingEventsAsync(
                sessionId,
                host,
                useLocalAccess,
                logPath,
                clampedTail,
                includeTailSnapshot,
                resumePosition,
                streamTokenSource.Token);

            IsMonitoring = true;
            Status = useLocalAccess
                ? "Streaming C:\\Windows\\SoftwareDistribution\\ReportingEvents.log locally."
                : $"Streaming C:\\Windows\\SoftwareDistribution\\ReportingEvents.log from '{host}' (SMB preferred, WinRM fallback).";

            if (fullReload)
            {
                SetReportingEventsLoadingOverlay("Loading ReportingEvents.log...");
            }
            else
            {
                SetReportingEventsLoadingOverlay("Resuming ReportingEvents.log...");
            }

            if (useLocalAccess)
            {
                SetReportingEventsLoadingOverlay("Loading ReportingEvents.log locally...");
            }
            else
            {
                SetReportingEventsLoadingOverlay("Loading ReportingEvents.log...");
            }
            ClearBusyState(busyOwnerId);
        }
        catch (OperationCanceledException) when (selection.CancellationToken.IsCancellationRequested)
        {
            ClearReportingEventsLoadingOverlay();
        }
        catch (OperationCanceledException)
        {
            Status = "Start canceled.";
            ClearReportingEventsLoadingOverlay();
        }
        catch (Exception ex)
        {
            IsMonitoring = false;
            Status = $"Log stream failed: {ex.Message}";
            SetReportingEventsLoadingOverlay($"Log stream failed: {ex.Message}");
            _logger.LogError(ex, "Failed to start ReportingEvents.log stream.");
        }
        finally
        {
            ClearBusyState();
            IsMonitoringBusy = false;
            _monitorGate.Release();
        }
    }

    private async Task StreamReportingEventsAsync(
        long sessionId,
        string host,
        bool useLocalAccess,
        string path,
        int tailLineCount,
        bool includeTailSnapshot,
        long? resumePosition,
        CancellationToken cancellationToken)
    {
        try
        {
            if (useLocalAccess)
            {
                if (!File.Exists(path))
                {
                    throw new FileNotFoundException("ReportingEvents.log was not found.", path);
                }

                if (includeTailSnapshot)
                {
                    await AppendTailLinesAsync(path, tailLineCount, cancellationToken);
                }

                ClearReportingEventsLoadingOverlay();

                await FollowFileAsync(
                    path,
                    resumePosition,
                    cancellationToken,
                    position => UpdateStreamCheckpoint(host, path, position));
                return;
            }

            try
            {
                if (!File.Exists(path))
                {
                    throw new FileNotFoundException("ReportingEvents.log was not found via SMB.", path);
                }

                if (includeTailSnapshot)
                {
                    await AppendTailLinesAsync(path, tailLineCount, cancellationToken);
                }

                ClearReportingEventsLoadingOverlay();

                await FollowFileAsync(
                    path,
                    resumePosition,
                    cancellationToken,
                    position => UpdateStreamCheckpoint(host, path, position));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                SetReportingEventsLoadingOverlay($"SMB stream failed for '{host}'. Falling back to WinRM...");
                var fallbackTailCount = includeTailSnapshot ? tailLineCount : 0;
                await StreamViaWinRmAsync(host, fallbackTailCount, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when user stops streaming or host changes.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ReportingEvents.log stream failed.");
            if (IsCurrentStreamSession(sessionId))
            {
                _ = Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    if (!IsCurrentStreamSession(sessionId) || _disposed)
                    {
                        return;
                    }

                    Status = $"Log stream failed: {ex.Message}";
                    SetReportingEventsLoadingOverlay($"Log stream failed: {ex.Message}");
                });
            }
        }
        finally
        {
            ClearReportingEventsLoadingOverlay();
            if (IsCurrentStreamSession(sessionId) && !_disposed)
            {
                _ = Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    if (!IsCurrentStreamSession(sessionId) || _disposed)
                    {
                        return;
                    }

                    IsMonitoring = false;
                });
            }
        }
    }

    private async Task AppendTailLinesAsync(string path, int tailLineCount, CancellationToken cancellationToken)
    {
        var tailLines = await FileTailReader.ReadTailLinesAsync(path, tailLineCount, cancellationToken);
        AppendLines(tailLines.Where(line => !string.IsNullOrWhiteSpace(line)).ToArray());
    }

    private async Task FollowFileAsync(
        string path,
        long? startPosition,
        CancellationToken cancellationToken,
        Action<long> onPositionChanged)
    {
        await FileTailReader.FollowLinesAsync(
            path,
            startPosition,
            AppendLine,
            onPositionChanged,
            StreamPollDelayMilliseconds,
            cancellationToken);
    }

    private async Task StreamViaWinRmAsync(string host, int tailLineCount, CancellationToken cancellationToken)
    {
        var script = BuildWinRmReportingEventsScript(host, tailLineCount);
        using var runspace = RunspaceFactory.CreateRunspace(InitialSessionState.CreateDefault2());
        runspace.Open();
        using var powerShell = PowerShell.Create();
        powerShell.Runspace = runspace;
        powerShell.AddScript(script, useLocalScope: false);

        var output = new PSDataCollection<PSObject>();
        output.DataAdded += (_, eventArgs) =>
        {
            var line = output[eventArgs.Index]?.ToString();
            if (string.IsNullOrWhiteSpace(line))
            {
                return;
            }

            AppendLine(line);
            if (IsMonitoringBusy || IsReportingEventsLoading)
            {
                ClearReportingEventsLoadingOverlay();
            }
        };

        powerShell.Streams.Error.DataAdded += (_, eventArgs) =>
        {
            var error = powerShell.Streams.Error[eventArgs.Index]?.ToString();
            if (!string.IsNullOrWhiteSpace(error))
            {
                AppendLine($"[stderr] {error}");
            }
        };

        using var cancellationRegistration = cancellationToken.Register(() =>
        {
            try
            {
                powerShell.Stop();
            }
            catch
            {
            }
        });

        try
        {
            var asyncResult = powerShell.BeginInvoke<PSObject, PSObject>(input: null, output);
            await Task.Run(() => powerShell.EndInvoke(asyncResult), CancellationToken.None);
        }
        catch (PipelineStoppedException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        if (powerShell.HadErrors)
        {
            throw new InvalidOperationException("WinRM stream failed.");
        }
    }

    private static string BuildWinRmReportingEventsScript(string host, int tailLineCount)
    {
        var escapedHost = host.Replace("'", "''", StringComparison.Ordinal);
        return
            "$ErrorActionPreference='Stop';" +
            $"$computerName='{escapedHost}';" +
            "$tailCount=[int]" + tailLineCount + ";" +
            "Invoke-Command -ComputerName $computerName -ErrorAction Stop -ScriptBlock {" +
            "  param([int]$tail);" +
            "  $ProgressPreference='SilentlyContinue';" +
            "  $path='C:\\Windows\\SoftwareDistribution\\ReportingEvents.log';" +
            "  if (-not (Test-Path -LiteralPath $path)) { throw \"File not found: $path\" };" +
            "  Get-Content -LiteralPath $path -Tail $tail -Wait;" +
            "} -ArgumentList $tailCount;";
    }

    private async Task RefreshInstallTaskStatusInternalAsync(CancellationToken cancellationToken, bool forceProgressReload = false)
    {
        if (IsDemoMode)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_demoInstallTaskStarted)
            {
                var snapshot = _demoDataCatalog.CreateWindowsUpdateSnapshot(_targetHostService.CurrentHost);
                InstallTaskState = snapshot.DefaultInstallTaskState;
                InstallTaskStatusText = snapshot.DefaultInstallTaskStatusText;
                InstallTaskPhaseText = snapshot.DefaultInstallTaskPhaseText;
                InstallTaskDetail = snapshot.DefaultInstallTaskDetail;
                IsInstallTaskRunning = snapshot.IsInstallTaskRunning;
                if (forceProgressReload)
                {
                    InstallProgressEntries.Clear();
                    foreach (var line in snapshot.BaseInstallProgressLines)
                    {
                        InstallProgressEntries.Add(InstallProgressEntry.FromLogLine(line));
                    }
                }
            }

            return;
        }

        var host = _activeInstallHost;
        var statePath = _activeInstallStatusPath;
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(statePath))
        {
            InstallTaskState = "No install task started.";
            InstallTaskStatusText = "Task: Unknown";
            InstallTaskPhaseText = "Phase: unknown";
            InstallTaskDetail = string.Empty;
            IsInstallTaskRunning = false;
            ClearInstallBusyState();
            ResetInstallProgressTracking(null);
            InstallProgressEntries.Clear();
            return;
        }

        EnsureInstallProgressFollowStarted();

        if (!IsLocalHost(host) && _powerShellExecutor is not null)
        {
            await RefreshRemoteInstallTaskStatusAsync(host, cancellationToken, forceProgressReload);
            return;
        }

        await RefreshLocalInstallTaskStatusAsync(host, statePath, cancellationToken, forceProgressReload);
    }

    private async Task RefreshLocalInstallTaskStatusAsync(
        string host,
        string statePath,
        CancellationToken cancellationToken,
        bool forceProgressReload)
    {
        var remoteSwitch = $" /TN \"{InstallTaskName}\" /FO LIST /V";
        var queryResult = await RunProcessAsync("schtasks.exe", "/Query" + remoteSwitch, cancellationToken);

        var taskStatus = "Unknown";
        var taskLastResult = string.Empty;
        if (queryResult.ExitCode == 0)
        {
            taskStatus = ParseSchtasksListValue(queryResult.StdOut, "Status") ?? taskStatus;
            taskLastResult = ParseSchtasksListValue(queryResult.StdOut, "Last Run Result") ?? string.Empty;
        }
        else
        {
            taskStatus = $"Task query failed ({queryResult.ExitCode})";
            taskLastResult = NormalizeExternalCommandError(queryResult);
        }

        InstallStatusPayload? installStatus = null;
        try
        {
            if (File.Exists(statePath))
            {
                var rawStatus = await SharedTextFileReader.ReadAllTextAsync(statePath, cancellationToken);
                installStatus = JsonSerializer.Deserialize<InstallStatusPayload>(rawStatus, JsonOptions);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to read install status file '{StatePath}'.", statePath);
        }

        await EnsureInstallProgressSnapshotLoadedAsync(
            cancellationToken,
            forceReload: forceProgressReload || !IsInstallStatusAutoRefreshEnabled);

        ApplyInstallStatus(
            host,
            taskStatus,
            taskLastResult,
            installStatus?.Phase ?? "unknown",
            installStatus?.Message ?? string.Empty,
            installStatus?.CurrentTitle ?? string.Empty,
            installStatus?.CompletedCount ?? 0,
            installStatus?.TotalCount ?? 0,
            installStatus?.InstalledCount ?? 0,
            installStatus?.FailedCount ?? 0,
            installStatus?.RebootRequired ?? false,
            installStatus?.LastUpdatedUtc ?? string.Empty,
            installStatus);
    }

    private async Task RefreshRemoteInstallTaskStatusAsync(string host, CancellationToken cancellationToken, bool forceProgressReload)
    {
        var progressPath = _activeInstallProgressLogPath ?? string.Empty;
        var useDirectProgressAccess = CanUseDirectInstallProgressAccess(progressPath);
        if (useDirectProgressAccess)
        {
            await EnsureInstallProgressSnapshotLoadedAsync(cancellationToken, forceReload: forceProgressReload);
        }

        var script = BuildRemoteInstallMonitorScript(
            InstallTaskName,
            _activeInstallStatusPath ?? string.Empty,
            useDirectProgressAccess ? string.Empty : progressPath,
            useDirectProgressAccess ? -1 : forceProgressReload ? -1 : _remoteInstallProgressCursor,
            useDirectProgressAccess ? 0 : forceProgressReload ? MaxInstallProgressRows : RemoteInstallProgressSnapshotLineCount);
        var snapshot = await _powerShellExecutor!.ExecuteJsonForHostAsync<WindowsUpdateInstallMonitorSnapshot>(host, script, cancellationToken)
            ?? new WindowsUpdateInstallMonitorSnapshot();

        var installStatus = new InstallStatusPayload
        {
            Phase = snapshot.Phase ?? string.Empty,
            Message = snapshot.Message ?? string.Empty,
            CurrentTitle = snapshot.CurrentTitle ?? string.Empty,
            TotalCount = snapshot.TotalCount,
            CompletedCount = snapshot.CompletedCount,
            InstalledCount = snapshot.InstalledCount,
            FailedCount = snapshot.FailedCount,
            RebootRequired = snapshot.RebootRequired,
            FailedTitles = [],
            LastUpdatedUtc = snapshot.LastUpdatedUtc ?? string.Empty
        };

        if (forceProgressReload)
        {
            InstallProgressEntries.Clear();
            ResetInstallProgressTracking(progressPath);
        }

        foreach (var line in snapshot.ProgressLines.Where(line => !string.IsNullOrWhiteSpace(line)))
        {
            AppendInstallProgressLine(line);
        }

        _remoteInstallProgressCursor = snapshot.ProgressCursor;
        TrackRemoteInstallActivity(snapshot);

        ApplyInstallStatus(
            host,
            snapshot.TaskStatus,
            snapshot.TaskLastResult,
            snapshot.Phase ?? string.Empty,
            snapshot.Message ?? string.Empty,
            snapshot.CurrentTitle ?? string.Empty,
            snapshot.CompletedCount,
            snapshot.TotalCount,
            snapshot.InstalledCount,
            snapshot.FailedCount,
            snapshot.RebootRequired,
            snapshot.LastUpdatedUtc ?? string.Empty,
            installStatus);
    }

    private void ApplyInstallStatus(
        string host,
        string taskStatus,
        string taskLastResult,
        string phase,
        string message,
        string currentTitle,
        int completed,
        int total,
        int installed,
        int failed,
        bool rebootRequired,
        string lastUpdated,
        InstallStatusPayload? installStatus)
    {
        phase = InferInstallPhaseFromProgress(phase);
        IsInstallTaskRunning = IsTaskInProgress(taskStatus, phase);
        SyncGlobalInstallBusyState(host, phase, total, completed, currentTitle);

        if (string.IsNullOrWhiteSpace(taskStatus) || string.Equals(taskStatus, "Unknown", StringComparison.OrdinalIgnoreCase))
        {
            taskStatus = MapPhaseToTaskStatus(phase);
        }

        var detailParts = new List<string>(4);
        if (!string.IsNullOrWhiteSpace(message))
        {
            detailParts.Add(message);
        }

        if (!string.IsNullOrWhiteSpace(currentTitle))
        {
            detailParts.Add($"Current: {currentTitle}");
        }

        if (total > 0)
        {
            detailParts.Add($"Progress: {completed}/{total} | Installed: {installed} | Failed: {failed}");
        }

        if (!string.IsNullOrWhiteSpace(lastUpdated))
        {
            detailParts.Add($"Updated: {FormatDate(lastUpdated)}");
        }

        if (string.Equals(phase, "reboot-pending", StringComparison.OrdinalIgnoreCase))
        {
            detailParts.Add("Reboot: pending");
        }
        else if (rebootRequired && !IsInstallTaskRunning)
        {
            detailParts.Add("Reboot: pending");
        }

        if (!string.IsNullOrWhiteSpace(taskLastResult))
        {
            detailParts.Add($"Last result: {taskLastResult}");
        }

        InstallTaskState = $"Task: {taskStatus} | Phase: {phase}";
        InstallTaskStatusText = $"Task: {taskStatus}";
        InstallTaskPhaseText = $"Phase: {phase}";
        InstallTaskDetail = string.Join(" | ", detailParts.Where(part => !string.IsNullOrWhiteSpace(part)));

        if (IsInstallTaskRunning && !string.IsNullOrWhiteSpace(InstallTaskDetail))
        {
            UpdateStatus = $"[WU] {InstallTaskDetail}";
        }

        if (rebootRequired && !IsInstallTaskRunning)
        {
            _ = TryRestartAfterInstallIfRequiredAsync(host, phase, installStatus);
        }
    }

    private void EnsureInstallStatusAutoRefresh()
    {
        if ((!IsInstallStatusAutoRefreshEnabled && !RestartAfterInstallIfRequired) || string.IsNullOrWhiteSpace(_activeInstallHost) || string.IsNullOrWhiteSpace(_activeInstallStatusPath))
        {
            return;
        }

        StopInstallStatusAutoRefresh(waitForTasks: false);
        var host = _activeInstallHost!;
        var useLocalAccess = IsLocalHost(host);

        var cancellationTokenSource = new CancellationTokenSource();
        _installStatusCancellationTokenSource = cancellationTokenSource;
        _installStatusTask = Task.Run(async () =>
        {
            try
            {
                await RefreshInstallTaskStatusInternalAsync(cancellationTokenSource.Token);
                while (!cancellationTokenSource.IsCancellationRequested)
                {
                    var delay = useLocalAccess
                        ? TimeSpan.FromSeconds(InstallStatusRefreshIntervalSeconds)
                        : GetRemoteInstallRefreshDelay();
                    await Task.Delay(delay, cancellationTokenSource.Token);
                    await RefreshInstallTaskStatusInternalAsync(cancellationTokenSource.Token);
                    if (!IsInstallTaskRunning && IsTerminalInstallPhase(InstallTaskPhaseText))
                    {
                        break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Expected while disposing or switching hosts.
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Install status auto-refresh stopped with error.");
            }
        }, CancellationToken.None);

        EnsureInstallProgressFollowStarted();
    }

    private void StopInstallStatusAutoRefresh(bool waitForTasks = true)
    {
        var cancellationTokenSource = _installStatusCancellationTokenSource;
        var installStatusTask = _installStatusTask;
        var installProgressCancellationTokenSource = _installProgressCancellationTokenSource;
        var installProgressTask = _installProgressTask;

        _installStatusCancellationTokenSource = null;
        _installStatusTask = null;
        _installProgressCancellationTokenSource = null;
        _installProgressTask = null;

        try
        {
            cancellationTokenSource?.Cancel();
            installProgressCancellationTokenSource?.Cancel();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error while stopping install status auto-refresh task.");
        }
        finally
        {
            _ = FinalizeBackgroundStopAsync(installStatusTask, cancellationTokenSource, "install status auto-refresh");
            _ = FinalizeBackgroundStopAsync(installProgressTask, installProgressCancellationTokenSource, "install progress follow");
        }
    }

    private TimeSpan GetRemoteInstallRefreshDelay()
    {
        var phase = InstallTaskPhaseText.StartsWith("Phase: ", StringComparison.OrdinalIgnoreCase)
            ? InstallTaskPhaseText["Phase: ".Length..].Trim()
            : InstallTaskPhaseText.Trim();
        var idleFor = DateTimeOffset.UtcNow - _lastInstallMonitorActivityUtc;
        if (_lastInstallMonitorActivityUtc != DateTimeOffset.MinValue &&
            idleFor >= TimeSpan.FromSeconds(RemoteInstallIdleBackoffThresholdSeconds))
        {
            return TimeSpan.FromSeconds(RemoteInstallRefreshIdleSeconds);
        }

        return phase.ToLowerInvariant() switch
        {
            "queued" => TimeSpan.FromSeconds(RemoteInstallRefreshQueuedSeconds),
            "starting" => TimeSpan.FromSeconds(RemoteInstallRefreshQueuedSeconds),
            _ => TimeSpan.FromSeconds(RemoteInstallRefreshActiveSeconds)
        };
    }

    private void TrackRemoteInstallActivity(WindowsUpdateInstallMonitorSnapshot snapshot)
    {
        var fingerprint = string.Join("|",
            snapshot.TaskStatus ?? string.Empty,
            snapshot.TaskLastResult ?? string.Empty,
            snapshot.Phase ?? string.Empty,
            snapshot.Message ?? string.Empty,
            snapshot.CurrentTitle ?? string.Empty,
            snapshot.CompletedCount,
            snapshot.InstalledCount,
            snapshot.FailedCount,
            snapshot.RebootRequired,
            snapshot.LastUpdatedUtc ?? string.Empty,
            snapshot.ProgressCursor,
            snapshot.ProgressLines.Length);

        if (!string.Equals(_lastInstallMonitorFingerprint, fingerprint, StringComparison.Ordinal))
        {
            _lastInstallMonitorFingerprint = fingerprint;
            _lastInstallMonitorActivityUtc = DateTimeOffset.UtcNow;
        }
    }

    private static bool IsTerminalInstallPhase(string phaseText)
    {
        var phase = phaseText.StartsWith("Phase: ", StringComparison.OrdinalIgnoreCase)
            ? phaseText["Phase: ".Length..].Trim()
            : phaseText.Trim();
        return phase.Equals("completed", StringComparison.OrdinalIgnoreCase) ||
               phase.Equals("completed-with-errors", StringComparison.OrdinalIgnoreCase) ||
               phase.Equals("failed", StringComparison.OrdinalIgnoreCase) ||
               phase.Equals("reboot-pending", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildRemoteInstallMonitorScript(
        string taskName,
        string statePath,
        string progressPath,
        long progressCursor,
        int maxProgressLines)
    {
        var escapedTaskName = EscapePowerShellSingleQuotedString(taskName);
        var escapedStatePath = EscapePowerShellSingleQuotedString(statePath);
        var escapedProgressPath = EscapePowerShellSingleQuotedString(progressPath);
        return
            "$taskName = '" + escapedTaskName + "';" +
            "$statePath = '" + escapedStatePath + "';" +
            "$progressPath = '" + escapedProgressPath + "';" +
            "$cursor = [int64]" + progressCursor.ToString(CultureInfo.InvariantCulture) + ";" +
            "$maxLines = [int]" + maxProgressLines.ToString(CultureInfo.InvariantCulture) + ";" +
            "$result = [ordered]@{" +
            "  TaskStatus = 'Unknown';" +
            "  TaskLastResult = '';" +
            "  Phase = 'unknown';" +
            "  Message = '';" +
            "  CurrentTitle = '';" +
            "  TotalCount = 0;" +
            "  CompletedCount = 0;" +
            "  InstalledCount = 0;" +
            "  FailedCount = 0;" +
            "  RebootRequired = $false;" +
            "  LastUpdatedUtc = '';" +
            "  ProgressLines = @();" +
            "  ProgressCursor = $cursor" +
            "};" +
            "try {" +
            "  if (Get-Command -Name Get-ScheduledTask -ErrorAction SilentlyContinue) {" +
            "    $task = Get-ScheduledTask -TaskName $taskName -ErrorAction Stop;" +
            "    $taskInfo = Get-ScheduledTaskInfo -TaskName $taskName -ErrorAction Stop;" +
            "    $result.TaskStatus = [string]$task.State;" +
            "    $result.TaskLastResult = [string]$taskInfo.LastTaskResult;" +
            "  } else {" +
            "    $query = & schtasks.exe /Query /TN $taskName /FO LIST /V 2>&1;" +
            "    if ($LASTEXITCODE -eq 0) {" +
            "      $text = ($query | Out-String);" +
            "      $statusMatch = [regex]::Match($text, '(?im)^Status:\\s*(.+)$');" +
            "      if ($statusMatch.Success) { $result.TaskStatus = $statusMatch.Groups[1].Value.Trim() };" +
            "      $lastResultMatch = [regex]::Match($text, '(?im)^Last Run Result:\\s*(.+)$');" +
            "      if ($lastResultMatch.Success) { $result.TaskLastResult = $lastResultMatch.Groups[1].Value.Trim() };" +
            "    } else {" +
            "      $result.TaskStatus = 'Task query failed';" +
            "      $result.TaskLastResult = (($query | Out-String).Trim());" +
            "    }" +
            "  }" +
            "} catch {" +
            "  $result.TaskStatus = 'Task query failed';" +
            "  $result.TaskLastResult = $_.Exception.Message;" +
            "}" +
            "if (-not [string]::IsNullOrWhiteSpace($statePath) -and (Test-Path -LiteralPath $statePath)) {" +
            "  try {" +
            "    $state = Get-Content -LiteralPath $statePath -Raw -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop;" +
            "    if ($null -ne $state) {" +
            "      $result.Phase = [string]$state.Phase;" +
            "      $result.Message = [string]$state.Message;" +
            "      $result.CurrentTitle = [string]$state.CurrentTitle;" +
            "      $result.TotalCount = [int]$state.TotalCount;" +
            "      $result.CompletedCount = [int]$state.CompletedCount;" +
            "      $result.InstalledCount = [int]$state.InstalledCount;" +
            "      $result.FailedCount = [int]$state.FailedCount;" +
            "      $result.RebootRequired = [bool]$state.RebootRequired;" +
            "      $result.LastUpdatedUtc = [string]$state.LastUpdatedUtc;" +
            "    }" +
            "  } catch {" +
            "    $result.Message = if ([string]::IsNullOrWhiteSpace($result.Message)) { $_.Exception.Message } else { $result.Message };" +
            "  }" +
            "}" +
            "if (-not [string]::IsNullOrWhiteSpace($progressPath) -and (Test-Path -LiteralPath $progressPath)) {" +
            "  try {" +
            "    $fileInfo = Get-Item -LiteralPath $progressPath -ErrorAction Stop;" +
            "    $fileLength = [int64]$fileInfo.Length;" +
            "    if ($cursor -lt 0 -or $cursor -gt $fileLength) {" +
            "      $result.ProgressLines = @(Get-Content -LiteralPath $progressPath -Tail $maxLines -ErrorAction Stop | Where-Object { -not [string]::IsNullOrWhiteSpace($_) });" +
            "      $result.ProgressCursor = $fileLength;" +
            "    } else {" +
            "      $fileStream = [System.IO.File]::Open($progressPath, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite);" +
            "      try {" +
            "        $fileStream.Seek($cursor, [System.IO.SeekOrigin]::Begin) | Out-Null;" +
            "        $reader = New-Object System.IO.StreamReader($fileStream, [System.Text.Encoding]::UTF8, $true, 4096, $true);" +
            "        try {" +
            "          if ($cursor -gt 0 -and -not $reader.EndOfStream) { $null = $reader.ReadLine() };" +
            "          $lines = New-Object System.Collections.Generic.List[string];" +
            "          while (-not $reader.EndOfStream) {" +
            "            $line = $reader.ReadLine();" +
            "            if (-not [string]::IsNullOrWhiteSpace($line)) { [void]$lines.Add($line) }" +
            "          }" +
            "          if ($lines.Count -gt $maxLines) { $lines = $lines.GetRange($lines.Count - $maxLines, $maxLines) };" +
            "          $result.ProgressLines = @($lines);" +
            "          $result.ProgressCursor = $fileStream.Position;" +
            "        } finally {" +
            "          $reader.Dispose();" +
            "        }" +
            "      } finally {" +
            "        $fileStream.Dispose();" +
            "      }" +
            "    }" +
            "  } catch {" +
            "    $result.ProgressCursor = $cursor;" +
            "  }" +
            "}" +
            "$result | ConvertTo-Json -Depth 6 -Compress;";
    }

    private async Task TryRestartAfterInstallIfRequiredAsync(
        string host,
        string phase,
        InstallStatusPayload? installStatus)
    {
        if (_installAutoRestartTriggered || !RestartAfterInstallIfRequired || _localDeviceActionService is null)
        {
            if (!_installAutoRestartTriggered && RestartAfterInstallIfRequired && _localDeviceActionService is null)
            {
                _installAutoRestartTriggered = true;
                UpdateStatus = "[WU][ERROR] Automatic restart after installation is unavailable because no device action service is registered.";
            }

            return;
        }

        if (!string.Equals(phase, "reboot-pending", StringComparison.OrdinalIgnoreCase) &&
            !(installStatus?.RebootRequired ?? false))
        {
            return;
        }

        if (IsInstallTaskRunning)
        {
            return;
        }

        _installAutoRestartTriggered = true;
        StopInstallStatusAutoRefresh();
        ClearInstallBusyState();

        var busyOwnerId = BeginBusyState("Restarting device after update installation", ["Trigger reboot"]);
        try
        {
            UpdateStatus = $"[WU] Restarting '{host}' because installed updates require a reboot...";
            var restartResult = await _localDeviceActionService.RestartAsync(host, CancellationToken.None);
            UpdateStatus = restartResult.Success
                ? $"[WU] {restartResult.Message}"
                : $"[WU][ERROR] {restartResult.Message}";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to trigger post-install reboot.");
            UpdateStatus = $"[WU][ERROR] Failed to trigger restart after update installation: {ex.Message}";
        }
        finally
        {
            ClearBusyState(busyOwnerId);
        }
    }

    private async Task EnsureInstallProgressSnapshotLoadedAsync(CancellationToken cancellationToken, bool forceReload = false)
    {
        var progressPath = _activeInstallProgressLogPath;
        if (string.IsNullOrWhiteSpace(progressPath) || !File.Exists(progressPath))
        {
            return;
        }

        var shouldLoadSnapshot = false;
        lock (_installProgressSync)
        {
            if (!string.Equals(_trackedInstallProgressPath, progressPath, StringComparison.OrdinalIgnoreCase))
            {
                _trackedInstallProgressPath = progressPath;
                _installProgressPosition = -1;
                _installProgressBuffer.Clear();
                shouldLoadSnapshot = true;
            }
            else if (forceReload)
            {
                shouldLoadSnapshot = true;
            }
            else if (_installProgressPosition < 0 && _installProgressBuffer.Count == 0)
            {
                shouldLoadSnapshot = true;
            }
        }

        if (!shouldLoadSnapshot)
        {
            return;
        }

        TailReadResult snapshot;
        try
        {
            snapshot = await FileTailReader.ReadTailSnapshotAsync(progressPath, MaxInstallProgressRows, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to read install progress log '{ProgressPath}'.", progressPath);
            return;
        }

        var filtered = snapshot.Lines
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToArray();

        lock (_installProgressSync)
        {
            if (!string.Equals(_activeInstallProgressLogPath, progressPath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _trackedInstallProgressPath = progressPath;
            _installProgressPosition = snapshot.EndPosition;
            _installProgressBuffer.Clear();
            _installProgressBuffer.AddRange(filtered);
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            ReplaceInstallProgressEntries(filtered);
            return;
        }

        _ = dispatcher.InvokeAsync(() => ReplaceInstallProgressEntries(filtered));
    }

    private string InferInstallPhaseFromProgress(string currentPhase)
    {
        if (!string.Equals(currentPhase, "queued", StringComparison.OrdinalIgnoreCase))
        {
            return currentPhase;
        }

        string[] lines;
        lock (_installProgressSync)
        {
            lines = _installProgressBuffer
                .GetWindow(64)
                .Select(line => line.Trim())
                .Where(line => line.Length > 0)
                .ToArray();
        }

        if (lines.Length == 0)
        {
            return currentPhase;
        }

        var hasStart = lines.Any(line => line.Contains("Launcher started", StringComparison.OrdinalIgnoreCase));
        var hasFinish = lines.Any(line => line.Contains("Launcher finished", StringComparison.OrdinalIgnoreCase));
        if (hasStart && !hasFinish)
        {
            return "running";
        }

        return currentPhase;
    }

    private static string MapPhaseToTaskStatus(string phase)
    {
        return phase.ToLowerInvariant() switch
        {
            "queued" => "Queued",
            "starting" => "Running",
            "scanning" => "Running",
            "resolving" => "Running",
            "downloading" => "Running",
            "installing" => "Running",
            "running" => "Running",
            "completed" => "Completed",
            "completed-with-errors" => "Completed",
            "reboot-pending" => "Completed",
            "failed" => "Failed",
            _ => "Unknown"
        };
    }

    private static bool IsTaskInProgress(string taskStatus, string phase)
    {
        if (string.Equals(taskStatus, "Running", StringComparison.OrdinalIgnoreCase)
            || string.Equals(taskStatus, "Queued", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return phase.ToLowerInvariant() switch
        {
            "queued" => true,
            "starting" => true,
            "resolving" => true,
            "downloading" => true,
            "installing" => true,
            "running" => true,
            _ => false
        };
    }

    private void ReplaceInstallProgressEntries(IReadOnlyList<string> lines)
    {
        lock (_installProgressSync)
        {
            InstallProgressEntries.Clear();
            foreach (var line in lines)
            {
                InstallProgressEntries.Add(InstallProgressEntry.FromLogLine(line));
            }
        }
    }

    private async Task FollowInstallProgressAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var progressPath = _activeInstallProgressLogPath;
            if (string.IsNullOrWhiteSpace(progressPath))
            {
                return;
            }

            try
            {
                await EnsureInstallProgressSnapshotLoadedAsync(cancellationToken);
                var startPosition = GetTrackedInstallProgressPosition(progressPath);
                await FileTailReader.FollowLinesAsync(
                    progressPath,
                    startPosition,
                    AppendInstallProgressLine,
                    position => UpdateInstallProgressCheckpoint(progressPath, position),
                    StreamPollDelayMilliseconds,
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (IOException ex)
            {
                _logger.LogDebug(ex, "Install progress follow retry for '{ProgressPath}'.", progressPath);
                await Task.Delay(StreamPollDelayMilliseconds, cancellationToken);
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogDebug(ex, "Install progress follow access retry for '{ProgressPath}'.", progressPath);
                await Task.Delay(StreamPollDelayMilliseconds, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Install progress follow stopped unexpectedly for '{ProgressPath}'.", progressPath);
                await Task.Delay(StreamPollDelayMilliseconds, cancellationToken);
            }
        }
    }

    private void AppendInstallProgressLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        lock (_installProgressSync)
        {
            _installProgressBuffer.Add(line);
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            AppendInstallProgressLineCore(line);
            return;
        }

        _ = dispatcher.InvokeAsync(() => AppendInstallProgressLineCore(line));
    }

    private void AppendInstallProgressLineCore(string line)
    {
        lock (_installProgressSync)
        {
            InstallProgressEntries.Add(InstallProgressEntry.FromLogLine(line));
            while (InstallProgressEntries.Count > MaxInstallProgressRows)
            {
                InstallProgressEntries.RemoveAt(0);
            }
        }
    }

    private void ResetInstallProgressTracking(string? progressPath)
    {
        lock (_installProgressSync)
        {
            _trackedInstallProgressPath = progressPath;
            _installProgressPosition = -1;
            _installProgressBuffer.Clear();
            _remoteInstallProgressCursor = -1;
            _lastInstallMonitorFingerprint = string.Empty;
            _lastInstallMonitorActivityUtc = DateTimeOffset.UtcNow;
        }
    }

    private long? GetTrackedInstallProgressPosition(string progressPath)
    {
        lock (_installProgressSync)
        {
            if (!string.Equals(_trackedInstallProgressPath, progressPath, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return _installProgressPosition >= 0 ? _installProgressPosition : null;
        }
    }

    private void UpdateInstallProgressCheckpoint(string progressPath, long position)
    {
        lock (_installProgressSync)
        {
            if (!string.Equals(_trackedInstallProgressPath, progressPath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _installProgressPosition = position;
        }
    }

    private static string GetSelectedInstallScriptFileName(bool useAsyncScript, bool useWinRtInstallScript)
    {
        if (useWinRtInstallScript)
        {
            return InstallWinRtScriptFileName;
        }

        return useAsyncScript ? InstallAsyncScriptFileName : InstallScriptFileName;
    }

    private static string GetInstallModeLabel(bool useAsyncScript, bool useWinRtInstallScript)
    {
        if (useWinRtInstallScript)
        {
            return "winrt approval mode";
        }

        return useAsyncScript ? "async mode" : "sync mode";
    }

    private static string BuildInstallUpdatesScriptBody()
    {
        return
            "param([Parameter(Mandatory=$true)][string]$PayloadPath,[Parameter(Mandatory=$true)][string]$StatePath,[Parameter(Mandatory=$true)][string]$LogPath);" +
            "$ErrorActionPreference='Stop';" +
            "$ProgressPreference='SilentlyContinue';" +
            "function Write-Log {" +
            "  param([string]$Text);" +
            "  $logDir = Split-Path -Parent $LogPath;" +
            "  if (-not [string]::IsNullOrWhiteSpace($logDir)) { New-Item -ItemType Directory -Path $logDir -Force | Out-Null };" +
            "  $line = '[' + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss') + '] ' + $Text;" +
            "  $written = $false;" +
            "  for ($attempt = 0; $attempt -lt 8 -and -not $written; $attempt++) {" +
            "    try {" +
            "      $bytes = [System.Text.Encoding]::UTF8.GetBytes($line + [Environment]::NewLine);" +
            "      $stream = [System.IO.File]::Open($LogPath, [System.IO.FileMode]::Append, [System.IO.FileAccess]::Write, [System.IO.FileShare]::ReadWrite);" +
            "      try {" +
            "        $stream.Write($bytes, 0, $bytes.Length);" +
            "        $stream.Flush();" +
            "      } finally {" +
            "        $stream.Dispose();" +
            "      };" +
            "      $written = $true;" +
            "    } catch [System.IO.IOException] {" +
            "      Start-Sleep -Milliseconds 150;" +
            "    } catch [System.UnauthorizedAccessException] {" +
            "      Start-Sleep -Milliseconds 150;" +
            "    }" +
            "  };" +
            "};" +
            "function Write-State {" +
            "  param(" +
            "    [string]$Phase," +
            "    [string]$Message," +
            "    [string]$CurrentTitle," +
            "    [int]$CompletedCount," +
            "    [int]$InstalledCount," +
            "    [int]$FailedCount," +
            "    [bool]$RebootRequired = $false," +
            "    [string[]]$FailedTitles" +
            "  );" +
            "  $state = [PSCustomObject]@{" +
            "    phase = $Phase;" +
            "    message = $Message;" +
            "    currentTitle = $CurrentTitle;" +
            "    totalCount = $script:TotalCount;" +
            "    completedCount = $CompletedCount;" +
            "    installedCount = $InstalledCount;" +
            "    failedCount = $FailedCount;" +
            "    rebootRequired = $RebootRequired;" +
            "    failedTitles = @($FailedTitles);" +
            "    lastUpdatedUtc = [DateTime]::UtcNow.ToString('o')" +
            "  };" +
            "  $stateDir = Split-Path -Parent $StatePath;" +
            "  if (-not [string]::IsNullOrWhiteSpace($stateDir)) { New-Item -ItemType Directory -Path $stateDir -Force | Out-Null };" +
            "  $stateJson = $state | ConvertTo-Json -Depth 6;" +
            "  $tempPath = $StatePath + '.tmp';" +
            "  for ($attempt = 0; $attempt -lt 8; $attempt++) {" +
            "    try {" +
            "      Set-Content -LiteralPath $tempPath -Value $stateJson -Encoding UTF8 -Force;" +
            "      if (Test-Path -LiteralPath $StatePath) { Remove-Item -LiteralPath $StatePath -Force };" +
            "      Move-Item -LiteralPath $tempPath -Destination $StatePath -Force;" +
            "      break;" +
            "    } catch [System.IO.IOException] {" +
            "      Start-Sleep -Milliseconds 150;" +
            "    } catch [System.UnauthorizedAccessException] {" +
            "      Start-Sleep -Milliseconds 150;" +
            "    }" +
            "  };" +
            "};" +
            "function Get-ResultCodeText {" +
            "  param([int]$Code);" +
            "  switch ($Code) {" +
            "    0 { 'NotStarted' }" +
            "    1 { 'InProgress' }" +
            "    2 { 'Succeeded' }" +
            "    3 { 'SucceededWithErrors' }" +
            "    4 { 'Failed' }" +
            "    5 { 'Aborted' }" +
            "    default { 'Unknown' }" +
            "  }" +
            "};" +
            "function Get-DetailedErrorText {" +
            "  param([object]$ErrorRecord);" +
            "  if ($null -eq $ErrorRecord) { return 'No error record available.' };" +
            "  $parts = New-Object System.Collections.Generic.List[string];" +
            "  if ($ErrorRecord.Exception -and -not [string]::IsNullOrWhiteSpace($ErrorRecord.Exception.Message)) { $parts.Add('Exception=' + $ErrorRecord.Exception.Message) };" +
            "  if (-not [string]::IsNullOrWhiteSpace($ErrorRecord.FullyQualifiedErrorId)) { $parts.Add('ErrorId=' + $ErrorRecord.FullyQualifiedErrorId) };" +
            "  if ($ErrorRecord.InvocationInfo -and -not [string]::IsNullOrWhiteSpace($ErrorRecord.InvocationInfo.PositionMessage)) { $parts.Add('Position=' + ($ErrorRecord.InvocationInfo.PositionMessage -replace '\\r?\\n',' ')) };" +
            "  if (-not [string]::IsNullOrWhiteSpace($ErrorRecord.ScriptStackTrace)) { $parts.Add('Stack=' + ($ErrorRecord.ScriptStackTrace -replace '\\r?\\n',' | ')) };" +
            "  $detail = ($ErrorRecord | Out-String).Trim();" +
            "  if (-not [string]::IsNullOrWhiteSpace($detail)) { $parts.Add('Detail=' + ($detail -replace '\\r?\\n',' | ')) };" +
            "  if ($parts.Count -eq 0) { return 'No additional error details available.' };" +
            "  return [string]::Join(' || ', $parts);" +
            "};" +
            "function Get-UpdateLookupKeys {" +
            "  param([string]$UpdateId,[int]$Revision,[string]$Title);" +
            "  $keys = New-Object System.Collections.Generic.List[string];" +
            "  $normalizedUpdateId = if ([string]::IsNullOrWhiteSpace($UpdateId)) { '' } else { $UpdateId.Trim() };" +
            "  $baseUpdateId = $normalizedUpdateId;" +
            "  if ($baseUpdateId.Contains(':')) {" +
            "    $separatorIndex = $baseUpdateId.LastIndexOf(':');" +
            "    if ($separatorIndex -gt 0 -and $separatorIndex -lt ($baseUpdateId.Length - 1)) { $baseUpdateId = $baseUpdateId.Substring(0, $separatorIndex) }" +
            "  };" +
            "  if (-not [string]::IsNullOrWhiteSpace($baseUpdateId)) {" +
            "    if ($Revision -gt 0) { [void]$keys.Add($baseUpdateId + '|' + [string]$Revision) };" +
            "    [void]$keys.Add($baseUpdateId);" +
            "  };" +
            "  if (-not [string]::IsNullOrWhiteSpace($normalizedUpdateId)) {" +
            "    if ($Revision -gt 0 -and $normalizedUpdateId -ne $baseUpdateId) { [void]$keys.Add($normalizedUpdateId + '|' + [string]$Revision) };" +
            "    [void]$keys.Add($normalizedUpdateId);" +
            "  };" +
            "  if (-not [string]::IsNullOrWhiteSpace($Title)) { [void]$keys.Add('title|' + $Title.Trim()) };" +
            "  return @($keys | Select-Object -Unique);" +
            "};" +
            "function Add-AvailableUpdateIndexEntry {" +
            "  param([hashtable]$Index,[object]$Candidate,[string]$SourceLabel);" +
            "  if ($null -eq $Candidate) { return };" +
            "  $title = '';" +
            "  try { $title = [string]$Candidate.Title } catch { };" +
            "  $updateId = '';" +
            "  $revision = 0;" +
            "  try { $updateId = [string]$Candidate.Identity.UpdateID; $revision = [int]$Candidate.Identity.RevisionNumber } catch { };" +
            "  foreach ($key in Get-UpdateLookupKeys -UpdateId $updateId -Revision $revision -Title $title) {" +
            "    if (-not $Index.ContainsKey($key)) { $Index[$key] = $Candidate };" +
            "  };" +
            "  $candidateType = '';" +
            "  try { $candidateType = [string]$Candidate.Type } catch { };" +
            "  $candidateDownloaded = $false;" +
            "  try { $candidateDownloaded = [bool]$Candidate.IsDownloaded } catch { };" +
            "  Write-Log ('Search candidate [' + $SourceLabel + ']: Title=' + $title + ' UpdateId=' + $updateId + ' Revision=' + $revision + ' Downloaded=' + $candidateDownloaded + ' Type=' + $candidateType);" +
            "};" +
            "function Resolve-AvailableUpdate {" +
            "  param([hashtable]$Index,[string]$UpdateId,[int]$Revision,[string]$Title);" +
            "  foreach ($key in Get-UpdateLookupKeys -UpdateId $UpdateId -Revision $Revision -Title $Title) {" +
            "    if ($Index.ContainsKey($key)) { return $Index[$key] };" +
            "  };" +
            "  return $null;" +
            "};" +
            "function Start-SearchHeartbeatMonitor {" +
            "  param([string]$LogPath,[string]$Label,[int]$IntervalSeconds);" +
            "  $markerPath = $LogPath + '.search-heartbeat';" +
            "  New-Item -ItemType File -Path $markerPath -Force | Out-Null;" +
            "  $job = Start-Job -ArgumentList $LogPath, $markerPath, $Label, $IntervalSeconds -ScriptBlock {" +
            "    param($LogPath,$MarkerPath,$Label,$IntervalSeconds);" +
            "    function Write-Log {" +
            "      param([string]$Text);" +
            "      $line = '[' + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss') + '] ' + $Text;" +
            "      for ($attempt = 0; $attempt -lt 8; $attempt++) {" +
            "        try {" +
            "          $bytes = [System.Text.Encoding]::UTF8.GetBytes($line + [Environment]::NewLine);" +
            "          $stream = [System.IO.File]::Open($LogPath, [System.IO.FileMode]::Append, [System.IO.FileAccess]::Write, [System.IO.FileShare]::ReadWrite);" +
            "          try {" +
            "            $stream.Write($bytes, 0, $bytes.Length);" +
            "            $stream.Flush();" +
            "          } finally {" +
            "            $stream.Dispose();" +
            "          };" +
            "          break;" +
            "        } catch {" +
            "          Start-Sleep -Milliseconds 150;" +
            "        }" +
            "      };" +
            "    };" +
            "    while (Test-Path -LiteralPath $MarkerPath) {" +
            "      Start-Sleep -Seconds $IntervalSeconds;" +
            "      if (Test-Path -LiteralPath $MarkerPath) {" +
            "        Write-Log $Label;" +
            "      }" +
            "    };" +
            "  };" +
            "  return [PSCustomObject]@{ MarkerPath = $markerPath; Job = $job };" +
            "};" +
            "function Stop-SearchHeartbeatMonitor {" +
            "  param([object]$Monitor);" +
            "  if ($null -eq $Monitor) { return };" +
            "  try { if ($Monitor.MarkerPath -and (Test-Path -LiteralPath $Monitor.MarkerPath)) { Remove-Item -LiteralPath $Monitor.MarkerPath -Force } } catch { };" +
            "  try { if ($Monitor.Job) { Wait-Job -Job $Monitor.Job -ErrorAction SilentlyContinue | Out-Null } } catch { };" +
            "  try { if ($Monitor.Job) { Receive-Job -Job $Monitor.Job -ErrorAction SilentlyContinue | Out-Null } } catch { };" +
            "  try { if ($Monitor.Job) { Remove-Job -Job $Monitor.Job -Force -ErrorAction SilentlyContinue } } catch { };" +
            "};" +
            "function Invoke-SearchWithHeartbeat {" +
            "  param([object]$Searcher,[string]$Criteria,[int]$TimeoutSeconds);" +
            "  $searchJob = $null;" +
            "  try {" +
            "    $searchJob = $Searcher.BeginSearch($Criteria, $null, $null);" +
            "  } catch {" +
            "    Write-Log ('BeginSearch unavailable or failed. Falling back to synchronous Search(). Reason=' + $_.Exception.Message);" +
            "    Write-State -Phase 'resolving' -Message 'Searching local Windows Update cache (synchronous fallback)...' -CurrentTitle '' -CompletedCount 0 -InstalledCount 0 -FailedCount $failed.Count -FailedTitles $failed.ToArray();" +
            "    $heartbeat = Start-SearchHeartbeatMonitor -LogPath $LogPath -Label 'Search still running (synchronous fallback).' -IntervalSeconds 30;" +
            "    try {" +
            "      return $Searcher.Search($Criteria);" +
            "    } finally {" +
            "      Stop-SearchHeartbeatMonitor -Monitor $heartbeat;" +
            "    }" +
            "  };" +
            "  $startedAt = Get-Date;" +
            "  $lastHeartbeat = [DateTime]::MinValue;" +
            "  while (-not $searchJob.IsCompleted) {" +
            "    Start-Sleep -Milliseconds 700;" +
            "    $elapsedSeconds = [int][Math]::Floor(((Get-Date) - $startedAt).TotalSeconds);" +
            "    if ($lastHeartbeat -eq [DateTime]::MinValue -or ((Get-Date) - $lastHeartbeat).TotalSeconds -ge 5) {" +
            "      $heartbeat = 'Search still running (' + $elapsedSeconds + 's elapsed).';" +
            "      Write-Log $heartbeat;" +
            "      Write-State -Phase 'resolving' -Message $heartbeat -CurrentTitle '' -CompletedCount 0 -InstalledCount 0 -FailedCount $failed.Count -FailedTitles $failed.ToArray();" +
            "      $lastHeartbeat = Get-Date;" +
            "    };" +
            "    if ($elapsedSeconds -ge $TimeoutSeconds) {" +
            "      try { $searchJob.RequestAbort() } catch { };" +
            "      throw ('Search timed out after ' + $TimeoutSeconds + ' second(s).');" +
            "    };" +
            "  };" +
            "  $result = $Searcher.EndSearch($searchJob);" +
            "  try { $searchJob.CleanUp() } catch { };" +
            "  return $result;" +
            "};" +
            "try {" +
            "if (-not (Test-Path -LiteralPath $PayloadPath)) { throw \"Payload file not found: $PayloadPath\" };" +
            "$items = Get-Content -LiteralPath $PayloadPath -Raw | ConvertFrom-Json;" +
            "if ($items -isnot [System.Collections.IEnumerable]) { $items = @($items) };" +
            "$items = @($items);" +
            "$script:TotalCount = $items.Count;" +
            "$failed = New-Object System.Collections.Generic.List[string];" +
            "$resolved = New-Object -ComObject Microsoft.Update.UpdateColl;" +
            "$processed = 0;" +
            "Write-Log ('Starting install workflow for ' + $script:TotalCount + ' selected update(s).');" +
            "Write-State -Phase 'starting' -Message 'Preparing selected updates...' -CurrentTitle '' -CompletedCount 0 -InstalledCount 0 -FailedCount 0 -FailedTitles @();" +
            "$session = New-Object -ComObject Microsoft.Update.Session;" +
            "$searcher = $session.CreateUpdateSearcher();" +
            "$searcher.Online = $false;" +
            "$criteria = \"(IsInstalled=0 and Type='Software') or (IsInstalled=0 and Type='Driver')\";" +
            "Write-Log ('Search started: loading available updates from local cache (single search pass). Criteria=' + $criteria);" +
            "Write-State -Phase 'resolving' -Message 'Searching local Windows Update cache...' -CurrentTitle '' -CompletedCount 0 -InstalledCount 0 -FailedCount $failed.Count -FailedTitles $failed.ToArray();" +
            "$available = Invoke-SearchWithHeartbeat -Searcher $searcher -Criteria $criteria -TimeoutSeconds 600;" +
            "$availableCode = [int]$available.ResultCode;" +
            "Write-Log ('Search completed. ResultCode=' + $availableCode + ' (' + (Get-ResultCodeText $availableCode) + ') Found=' + $available.Updates.Count + ' update(s).');" +
            "$availableByKey = @{};" +
            "foreach ($candidate in $available.Updates) {" +
            "  Add-AvailableUpdateIndexEntry -Index $availableByKey -Candidate $candidate -SourceLabel 'local cache';" +
            "};" +
            "$unresolvedItems = New-Object System.Collections.Generic.List[object];" +
            "foreach ($item in $items) {" +
            "  $title = [string]$item.Title;" +
            "  $updateId = [string]$item.UpdateId;" +
            "  $revision = [int]$item.Revision;" +
            "  Write-Log ('Resolving: ' + $title);" +
            "  Write-State -Phase 'resolving' -Message ('Resolving update: ' + $title) -CurrentTitle $title -CompletedCount $processed -InstalledCount 0 -FailedCount $failed.Count -FailedTitles $failed.ToArray();" +
            "  $match = Resolve-AvailableUpdate -Index $availableByKey -UpdateId $updateId -Revision $revision -Title $title;" +
            "  if ($null -eq $match) { [void]$unresolvedItems.Add($item); continue };" +
            "  if (-not [bool]$match.EulaAccepted) { $match.AcceptEula() };" +
            "  Write-Log ('Resolved: ' + $title + ' (Downloaded=' + [bool]$match.IsDownloaded + ', AutoSelect=' + [bool]$match.AutoSelectOnWebSites + ')');" +
            "  [void]$resolved.Add($match);" +
            "  $processed++;" +
            "};" +
            "if ($unresolvedItems.Count -gt 0) {" +
            "  Write-Log ('Local cache did not resolve ' + $unresolvedItems.Count + ' selected update(s). Falling back to online search.');" +
            "  $searcher.Online = $true;" +
            "  $onlineResult = Invoke-SearchWithHeartbeat -Searcher $searcher -Criteria $criteria -TimeoutSeconds 600;" +
            "  $onlineCode = [int]$onlineResult.ResultCode;" +
            "  Write-Log ('Online search completed. ResultCode=' + $onlineCode + ' (' + (Get-ResultCodeText $onlineCode) + ') Found=' + $onlineResult.Updates.Count + ' update(s).');" +
            "  foreach ($candidate in $onlineResult.Updates) {" +
            "    Add-AvailableUpdateIndexEntry -Index $availableByKey -Candidate $candidate -SourceLabel 'online catalog';" +
            "  };" +
            "  $stillUnresolved = New-Object System.Collections.Generic.List[object];" +
            "  foreach ($item in $unresolvedItems) {" +
            "    $title = [string]$item.Title;" +
            "    $updateId = [string]$item.UpdateId;" +
            "    $revision = [int]$item.Revision;" +
            "    $match = Resolve-AvailableUpdate -Index $availableByKey -UpdateId $updateId -Revision $revision -Title $title;" +
            "    if ($null -eq $match) {" +
            "      Write-Log ('Resolve failed: not found or already installed: ' + $title + ' (UpdateId=' + $updateId + ', Revision=' + $revision + ')');" +
            "      $failed.Add($title);" +
            "      $processed++;" +
            "      [void]$stillUnresolved.Add($item);" +
            "      continue;" +
            "    };" +
            "    if (-not [bool]$match.EulaAccepted) { $match.AcceptEula() };" +
            "    Write-Log ('Resolved: ' + $title + ' (Downloaded=' + [bool]$match.IsDownloaded + ', AutoSelect=' + [bool]$match.AutoSelectOnWebSites + ')');" +
            "    [void]$resolved.Add($match);" +
            "    $processed++;" +
            "  };" +
            "  $unresolvedItems = $stillUnresolved;" +
            "};" +
            "if ($resolved.Count -eq 0) {" +
            "  Write-Log 'No selected updates could be resolved for installation.';" +
            "  Write-State -Phase 'failed' -Message 'No selected updates could be resolved for installation.' -CurrentTitle '' -CompletedCount $processed -InstalledCount 0 -FailedCount $failed.Count -FailedTitles $failed.ToArray();" +
            "  exit 2;" +
            "};" +
            "Write-Log ('Resolution completed. Resolved=' + $resolved.Count + ' FailedToResolve=' + $failed.Count + '.');" +
            "Write-Log ('Download started for ' + $resolved.Count + ' update(s).');" +
            "Write-State -Phase 'downloading' -Message ('Download started for ' + $resolved.Count + ' update(s).') -CurrentTitle '' -CompletedCount $processed -InstalledCount 0 -FailedCount $failed.Count -FailedTitles $failed.ToArray();" +
            "$downloader = $session.CreateUpdateDownloader();" +
            "$downloader.Updates = $resolved;" +
            "$downloadJob = $null;" +
            "$downloadResult = $null;" +
            "$downloadUsedAsync = $true;" +
            "try {" +
            "  $downloadJob = $downloader.BeginDownload($null, $null, $null);" +
            "} catch {" +
            "  $downloadUsedAsync = $false;" +
            "  Write-Log ('BeginDownload unavailable or failed. Falling back to synchronous Download(). Reason=' + $_.Exception.Message);" +
            "};" +
            "if ($downloadUsedAsync -and $null -eq $downloadJob) {" +
            "  $downloadUsedAsync = $false;" +
            "  Write-Log 'BeginDownload returned no job. Falling back to synchronous Download().';" +
            "};" +
            "$lastDownloadPercent = -1;" +
            "$lastDownloadIndex = -1;" +
            "$lastDownloadLog = [DateTime]::MinValue;" +
            "if ($downloadUsedAsync) {" +
            "  while (-not $downloadJob.IsCompleted) {" +
            "    Start-Sleep -Milliseconds 700;" +
            "    $downloadProgress = $null;" +
            "    try { $downloadProgress = $downloadJob.GetProgress() } catch { };" +
            "    if ($null -eq $downloadProgress) { continue };" +
            "    $downloadPercent = [int]$downloadProgress.PercentComplete;" +
            "    $downloadIndex = [int]$downloadProgress.CurrentUpdateIndex;" +
            "    $downloadUpdatePercent = [int]$downloadProgress.CurrentUpdatePercentComplete;" +
            "    $downloadTitle = '';" +
            "    if ($downloadIndex -ge 0 -and $downloadIndex -lt $resolved.Count) { $downloadTitle = [string]$resolved.Item($downloadIndex).Title };" +
            "    $shouldWriteDownload = $downloadPercent -ne $lastDownloadPercent -or $downloadIndex -ne $lastDownloadIndex -or ((Get-Date) - $lastDownloadLog).TotalSeconds -ge 30;" +
            "    if (-not $shouldWriteDownload) { continue };" +
            "    $downloadMessage = 'Downloading: ' + $downloadPercent + '% (Current=' + $downloadTitle + ' ' + $downloadUpdatePercent + '%)';" +
            "    Write-Log $downloadMessage;" +
            "    Write-State -Phase 'downloading' -Message $downloadMessage -CurrentTitle $downloadTitle -CompletedCount $processed -InstalledCount 0 -FailedCount $failed.Count -FailedTitles $failed.ToArray();" +
            "    $lastDownloadPercent = $downloadPercent;" +
            "    $lastDownloadIndex = $downloadIndex;" +
            "    $lastDownloadLog = Get-Date;" +
            "  };" +
            "  $downloadResult = $downloader.EndDownload($downloadJob);" +
            "  try { $downloadJob.CleanUp() } catch { };" +
            "} else {" +
            "  $downloadResult = $downloader.Download();" +
            "};" +
            "$downloadCode = [int]$downloadResult.ResultCode;" +
            "$downloadSummary = 'Download completed. ResultCode=' + $downloadCode + ' (' + (Get-ResultCodeText $downloadCode) + ') HResult=' + $downloadResult.HResult;" +
            "Write-Log $downloadSummary;" +
            "if ($downloadCode -ne 2 -and $downloadCode -ne 3) {" +
            "  Write-State -Phase 'failed' -Message $downloadSummary -CurrentTitle '' -CompletedCount $processed -InstalledCount 0 -FailedCount $failed.Count -FailedTitles $failed.ToArray();" +
            "  exit 3;" +
            "};" +
            "for ($i = 0; $i -lt $resolved.Count; $i++) {" +
            "  $candidate = $resolved.Item($i);" +
            "  Write-Log ('Download status: ' + [string]$candidate.Title + ' Downloaded=' + [bool]$candidate.IsDownloaded);" +
            "};" +
            "Write-Log ('Installation started for ' + $resolved.Count + ' update(s).');" +
            "Write-State -Phase 'installing' -Message ('Installation started for ' + $resolved.Count + ' update(s).') -CurrentTitle '' -CompletedCount $processed -InstalledCount 0 -FailedCount $failed.Count -FailedTitles $failed.ToArray();" +
            "$installer = $session.CreateUpdateInstaller();" +
            "$installer.Updates = $resolved;" +
            "$installJob = $null;" +
            "$installResult = $null;" +
            "$installUsedAsync = $true;" +
            "try {" +
            "  $installJob = $installer.BeginInstall($null, $null, $null);" +
            "} catch {" +
            "  $installUsedAsync = $false;" +
            "  Write-Log ('BeginInstall unavailable or failed. Falling back to synchronous Install(). Reason=' + $_.Exception.Message);" +
            "};" +
            "if ($installUsedAsync -and $null -eq $installJob) {" +
            "  $installUsedAsync = $false;" +
            "  Write-Log 'BeginInstall returned no job. Falling back to synchronous Install().';" +
            "};" +
            "$lastInstallPercent = -1;" +
            "$lastInstallIndex = -1;" +
            "$lastInstallLog = [DateTime]::MinValue;" +
            "if ($installUsedAsync) {" +
            "  while (-not $installJob.IsCompleted) {" +
            "    Start-Sleep -Milliseconds 700;" +
            "    $installProgress = $null;" +
            "    try { $installProgress = $installJob.GetProgress() } catch { };" +
            "    if ($null -eq $installProgress) { continue };" +
            "    $installPercent = [int]$installProgress.PercentComplete;" +
            "    $installIndex = [int]$installProgress.CurrentUpdateIndex;" +
            "    $installUpdatePercent = [int]$installProgress.CurrentUpdatePercentComplete;" +
            "    $installTitle = '';" +
            "    if ($installIndex -ge 0 -and $installIndex -lt $resolved.Count) { $installTitle = [string]$resolved.Item($installIndex).Title };" +
            "    $shouldWriteInstall = $installPercent -ne $lastInstallPercent -or $installIndex -ne $lastInstallIndex -or ((Get-Date) - $lastInstallLog).TotalSeconds -ge 30;" +
            "    if (-not $shouldWriteInstall) { continue };" +
            "    $installMessage = 'Installing: ' + $installPercent + '% (Current=' + $installTitle + ' ' + $installUpdatePercent + '%)';" +
            "    Write-Log $installMessage;" +
            "    Write-State -Phase 'installing' -Message $installMessage -CurrentTitle $installTitle -CompletedCount $processed -InstalledCount 0 -FailedCount $failed.Count -FailedTitles $failed.ToArray();" +
            "    $lastInstallPercent = $installPercent;" +
            "    $lastInstallIndex = $installIndex;" +
            "    $lastInstallLog = Get-Date;" +
            "  };" +
            "  $installResult = $installer.EndInstall($installJob);" +
            "  try { $installJob.CleanUp() } catch { };" +
            "} else {" +
            "  $installResult = $installer.Install();" +
            "};" +
            "$installCode = [int]$installResult.ResultCode;" +
            "Write-Log ('Installation finished. ResultCode=' + $installCode + ' (' + (Get-ResultCodeText $installCode) + ') HResult=' + $installResult.HResult + ' RebootRequired=' + [bool]$installResult.RebootRequired);" +
            "$installedCount = 0;" +
            "for ($i = 0; $i -lt $resolved.Count; $i++) {" +
            "  $title = [string]$resolved.Item($i).Title;" +
            "  $single = $installResult.GetUpdateResult($i);" +
            "  $resultCode = [int]$single.ResultCode;" +
            "  $hresult = [int]$single.HResult;" +
            "  if ($resultCode -eq 2 -or $resultCode -eq 3) { $installedCount++ } else { $failed.Add($title) };" +
            "  $resultMessage = 'Processed: ' + $title + ' (Result=' + $resultCode + ' ' + (Get-ResultCodeText $resultCode) + ', HResult=' + $hresult + ', RebootRequired=' + [bool]$single.RebootRequired + ')';" +
            "  Write-Log $resultMessage;" +
            "  Write-State -Phase 'installing' -Message $resultMessage -CurrentTitle $title -CompletedCount ($processed + $i + 1) -InstalledCount $installedCount -FailedCount $failed.Count -FailedTitles $failed.ToArray();" +
            "};" +
            "$rebootRequired = [bool]$installResult.RebootRequired;" +
            "$finalPhase = if ($failed.Count -gt 0) { 'completed-with-errors' } elseif ($rebootRequired) { 'reboot-pending' } else { 'completed' };" +
            "$finalMessage = 'Installation finished. Installed=' + $installedCount + ' Failed=' + $failed.Count + ' RebootRequired=' + $rebootRequired;" +
            "if ($rebootRequired) { $finalMessage += ' Reboot pending.' }" +
            "Write-Log $finalMessage;" +
            "Write-State -Phase $finalPhase -Message $finalMessage -CurrentTitle '' -CompletedCount $script:TotalCount -InstalledCount $installedCount -FailedCount $failed.Count -RebootRequired $rebootRequired -FailedTitles $failed.ToArray();" +
            "if ($failed.Count -gt 0) { exit 4 };" +
            "exit 0;" +
            "} catch {" +
            "  $errorMessage = $_.Exception.Message;" +
            "  $errorDetail = Get-DetailedErrorText $_;" +
            "  Write-Log ('Unhandled exception: ' + $errorMessage);" +
            "  Write-Log ('Exception detail: ' + $errorDetail);" +
            "  Write-State -Phase 'failed' -Message ('Unhandled exception: ' + $errorMessage + ' | ' + $errorDetail) -CurrentTitle '' -CompletedCount 0 -InstalledCount 0 -FailedCount 0 -FailedTitles @();" +
            "  exit 1;" +
            "}";
    }

    private static string BuildInstallUpdatesAsyncScriptBody()
    {
        return
            "param([Parameter(Mandatory=$true)][string]$PayloadPath,[Parameter(Mandatory=$true)][string]$StatePath,[Parameter(Mandatory=$true)][string]$LogPath);" +
            "$ErrorActionPreference='Stop';" +
            "$ProgressPreference='SilentlyContinue';" +
            "function Write-Log {" +
            "  param([string]$Text);" +
            "  $logDir = Split-Path -Parent $LogPath;" +
            "  if (-not [string]::IsNullOrWhiteSpace($logDir)) { New-Item -ItemType Directory -Path $logDir -Force | Out-Null };" +
            "  $line = '[' + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss') + '] ' + $Text;" +
            "  $written = $false;" +
            "  for ($attempt = 0; $attempt -lt 8 -and -not $written; $attempt++) {" +
            "    try {" +
            "      $bytes = [System.Text.Encoding]::UTF8.GetBytes($line + [Environment]::NewLine);" +
            "      $stream = [System.IO.File]::Open($LogPath, [System.IO.FileMode]::Append, [System.IO.FileAccess]::Write, [System.IO.FileShare]::ReadWrite);" +
            "      try {" +
            "        $stream.Write($bytes, 0, $bytes.Length);" +
            "        $stream.Flush();" +
            "      } finally {" +
            "        $stream.Dispose();" +
            "      };" +
            "      $written = $true;" +
            "    } catch [System.IO.IOException] {" +
            "      Start-Sleep -Milliseconds 150;" +
            "    } catch [System.UnauthorizedAccessException] {" +
            "      Start-Sleep -Milliseconds 150;" +
            "    }" +
            "  };" +
            "};" +
            "function Write-State {" +
            "  param(" +
            "    [string]$Phase," +
            "    [string]$Message," +
            "    [string]$CurrentTitle," +
            "    [int]$CompletedCount," +
            "    [int]$InstalledCount," +
            "    [int]$FailedCount," +
            "    [bool]$RebootRequired = $false," +
            "    [string[]]$FailedTitles" +
            "  );" +
            "  $state = [PSCustomObject]@{" +
            "    phase = $Phase;" +
            "    message = $Message;" +
            "    currentTitle = $CurrentTitle;" +
            "    totalCount = $script:TotalCount;" +
            "    completedCount = $CompletedCount;" +
            "    installedCount = $InstalledCount;" +
            "    failedCount = $FailedCount;" +
            "    rebootRequired = $RebootRequired;" +
            "    failedTitles = @($FailedTitles);" +
            "    lastUpdatedUtc = [DateTime]::UtcNow.ToString('o')" +
            "  };" +
            "  $stateDir = Split-Path -Parent $StatePath;" +
            "  if (-not [string]::IsNullOrWhiteSpace($stateDir)) { New-Item -ItemType Directory -Path $stateDir -Force | Out-Null };" +
            "  $stateJson = $state | ConvertTo-Json -Depth 6;" +
            "  $tempPath = $StatePath + '.tmp';" +
            "  for ($attempt = 0; $attempt -lt 8; $attempt++) {" +
            "    try {" +
            "      Set-Content -LiteralPath $tempPath -Value $stateJson -Encoding UTF8 -Force;" +
            "      if (Test-Path -LiteralPath $StatePath) { Remove-Item -LiteralPath $StatePath -Force };" +
            "      Move-Item -LiteralPath $tempPath -Destination $StatePath -Force;" +
            "      break;" +
            "    } catch [System.IO.IOException] {" +
            "      Start-Sleep -Milliseconds 150;" +
            "    } catch [System.UnauthorizedAccessException] {" +
            "      Start-Sleep -Milliseconds 150;" +
            "    }" +
            "  };" +
            "};" +
            "function Get-ResultCodeText {" +
            "  param([int]$Code);" +
            "  switch ($Code) {" +
            "    0 { 'NotStarted' }" +
            "    1 { 'InProgress' }" +
            "    2 { 'Succeeded' }" +
            "    3 { 'SucceededWithErrors' }" +
            "    4 { 'Failed' }" +
            "    5 { 'Aborted' }" +
            "    default { 'Unknown' }" +
            "  }" +
            "};" +
            "function Get-DetailedErrorText {" +
            "  param([object]$ErrorRecord);" +
            "  if ($null -eq $ErrorRecord) { return 'No error record available.' };" +
            "  $parts = New-Object System.Collections.Generic.List[string];" +
            "  if ($ErrorRecord.Exception -and -not [string]::IsNullOrWhiteSpace($ErrorRecord.Exception.Message)) { $parts.Add('Exception=' + $ErrorRecord.Exception.Message) };" +
            "  if (-not [string]::IsNullOrWhiteSpace($ErrorRecord.FullyQualifiedErrorId)) { $parts.Add('ErrorId=' + $ErrorRecord.FullyQualifiedErrorId) };" +
            "  if ($ErrorRecord.InvocationInfo -and -not [string]::IsNullOrWhiteSpace($ErrorRecord.InvocationInfo.PositionMessage)) { $parts.Add('Position=' + ($ErrorRecord.InvocationInfo.PositionMessage -replace '\\r?\\n',' ')) };" +
            "  if (-not [string]::IsNullOrWhiteSpace($ErrorRecord.ScriptStackTrace)) { $parts.Add('Stack=' + ($ErrorRecord.ScriptStackTrace -replace '\\r?\\n',' | ')) };" +
            "  $detail = ($ErrorRecord | Out-String).Trim();" +
            "  if (-not [string]::IsNullOrWhiteSpace($detail)) { $parts.Add('Detail=' + ($detail -replace '\\r?\\n',' | ')) };" +
            "  if ($parts.Count -eq 0) { return 'No additional error details available.' };" +
            "  return [string]::Join(' || ', $parts);" +
            "};" +
            "function Get-UpdateLookupKeys {" +
            "  param([string]$UpdateId,[int]$Revision,[string]$Title);" +
            "  $keys = New-Object System.Collections.Generic.List[string];" +
            "  $normalizedUpdateId = if ([string]::IsNullOrWhiteSpace($UpdateId)) { '' } else { $UpdateId.Trim() };" +
            "  $baseUpdateId = $normalizedUpdateId;" +
            "  if ($baseUpdateId.Contains(':')) {" +
            "    $separatorIndex = $baseUpdateId.LastIndexOf(':');" +
            "    if ($separatorIndex -gt 0 -and $separatorIndex -lt ($baseUpdateId.Length - 1)) { $baseUpdateId = $baseUpdateId.Substring(0, $separatorIndex) }" +
            "  };" +
            "  if (-not [string]::IsNullOrWhiteSpace($baseUpdateId)) {" +
            "    if ($Revision -gt 0) { [void]$keys.Add($baseUpdateId + '|' + [string]$Revision) };" +
            "    [void]$keys.Add($baseUpdateId);" +
            "  };" +
            "  if (-not [string]::IsNullOrWhiteSpace($normalizedUpdateId)) {" +
            "    if ($Revision -gt 0 -and $normalizedUpdateId -ne $baseUpdateId) { [void]$keys.Add($normalizedUpdateId + '|' + [string]$Revision) };" +
            "    [void]$keys.Add($normalizedUpdateId);" +
            "  };" +
            "  if (-not [string]::IsNullOrWhiteSpace($Title)) { [void]$keys.Add('title|' + $Title.Trim()) };" +
            "  return @($keys | Select-Object -Unique);" +
            "};" +
            "function Add-AvailableUpdateIndexEntry {" +
            "  param([hashtable]$Index,[object]$Candidate,[string]$SourceLabel);" +
            "  if ($null -eq $Candidate) { return };" +
            "  $title = '';" +
            "  try { $title = [string]$Candidate.Title } catch { };" +
            "  $updateId = '';" +
            "  $revision = 0;" +
            "  try { $updateId = [string]$Candidate.Identity.UpdateID; $revision = [int]$Candidate.Identity.RevisionNumber } catch { };" +
            "  foreach ($key in Get-UpdateLookupKeys -UpdateId $updateId -Revision $revision -Title $title) {" +
            "    if (-not $Index.ContainsKey($key)) { $Index[$key] = $Candidate };" +
            "  };" +
            "  $candidateType = '';" +
            "  try { $candidateType = [string]$Candidate.Type } catch { };" +
            "  $candidateDownloaded = $false;" +
            "  try { $candidateDownloaded = [bool]$Candidate.IsDownloaded } catch { };" +
            "  Write-Log ('Search candidate [' + $SourceLabel + ']: Title=' + $title + ' UpdateId=' + $updateId + ' Revision=' + $revision + ' Downloaded=' + $candidateDownloaded + ' Type=' + $candidateType);" +
            "};" +
            "function Start-SearchHeartbeatMonitor {" +
            "  param([string]$LogPath,[string]$Label,[int]$IntervalSeconds);" +
            "  $markerPath = $LogPath + '.search-heartbeat';" +
            "  New-Item -ItemType File -Path $markerPath -Force | Out-Null;" +
            "  $job = Start-Job -ArgumentList $LogPath, $markerPath, $Label, $IntervalSeconds -ScriptBlock {" +
            "    param($LogPath,$MarkerPath,$Label,$IntervalSeconds);" +
            "    function Write-Log {" +
            "      param([string]$Text);" +
            "      $line = '[' + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss') + '] ' + $Text;" +
            "      for ($attempt = 0; $attempt -lt 8; $attempt++) {" +
            "        try {" +
            "          $bytes = [System.Text.Encoding]::UTF8.GetBytes($line + [Environment]::NewLine);" +
            "          $stream = [System.IO.File]::Open($LogPath, [System.IO.FileMode]::Append, [System.IO.FileAccess]::Write, [System.IO.FileShare]::ReadWrite);" +
            "          try {" +
            "            $stream.Write($bytes, 0, $bytes.Length);" +
            "            $stream.Flush();" +
            "          } finally {" +
            "            $stream.Dispose();" +
            "          };" +
            "          break;" +
            "        } catch {" +
            "          Start-Sleep -Milliseconds 150;" +
            "        }" +
            "      };" +
            "    };" +
            "    while (Test-Path -LiteralPath $MarkerPath) {" +
            "      Start-Sleep -Seconds $IntervalSeconds;" +
            "      if (Test-Path -LiteralPath $MarkerPath) {" +
            "        Write-Log $Label;" +
            "      }" +
            "    };" +
            "  };" +
            "  return [PSCustomObject]@{ MarkerPath = $markerPath; Job = $job };" +
            "};" +
            "function Stop-SearchHeartbeatMonitor {" +
            "  param([object]$Monitor);" +
            "  if ($null -eq $Monitor) { return };" +
            "  try { if ($Monitor.MarkerPath -and (Test-Path -LiteralPath $Monitor.MarkerPath)) { Remove-Item -LiteralPath $Monitor.MarkerPath -Force } } catch { };" +
            "  try { if ($Monitor.Job) { Wait-Job -Job $Monitor.Job -ErrorAction SilentlyContinue | Out-Null } } catch { };" +
            "  try { if ($Monitor.Job) { Receive-Job -Job $Monitor.Job -ErrorAction SilentlyContinue | Out-Null } } catch { };" +
            "  try { if ($Monitor.Job) { Remove-Job -Job $Monitor.Job -Force -ErrorAction SilentlyContinue } } catch { };" +
            "};" +
            "function Invoke-SearchWithHeartbeat {" +
            "  param([object]$Searcher,[string]$Criteria,[int]$TimeoutSeconds);" +
            "  $searchJob = $null;" +
            "  try {" +
            "    $searchJob = $Searcher.BeginSearch($Criteria, $null, $null);" +
            "  } catch {" +
            "    Write-Log ('BeginSearch unavailable or failed. Falling back to synchronous Search(). Reason=' + $_.Exception.Message);" +
            "    Write-State -Phase 'resolving' -Message 'Searching local Windows Update cache (synchronous fallback)...' -CurrentTitle '' -CompletedCount 0 -InstalledCount 0 -FailedCount $failed.Count -FailedTitles $failed.ToArray();" +
            "    $heartbeat = Start-SearchHeartbeatMonitor -LogPath $LogPath -Label 'Search still running (synchronous fallback).' -IntervalSeconds 30;" +
            "    try {" +
            "      return $Searcher.Search($Criteria);" +
            "    } finally {" +
            "      Stop-SearchHeartbeatMonitor -Monitor $heartbeat;" +
            "    }" +
            "  };" +
            "  $startedAt = Get-Date;" +
            "  $lastHeartbeat = [DateTime]::MinValue;" +
            "  while (-not $searchJob.IsCompleted) {" +
            "    Start-Sleep -Milliseconds 700;" +
            "    $elapsedSeconds = [int][Math]::Floor(((Get-Date) - $startedAt).TotalSeconds);" +
            "    if ($lastHeartbeat -eq [DateTime]::MinValue -or ((Get-Date) - $lastHeartbeat).TotalSeconds -ge 5) {" +
            "      $heartbeat = 'Search still running (' + $elapsedSeconds + 's elapsed).';" +
            "      Write-Log $heartbeat;" +
            "      Write-State -Phase 'resolving' -Message $heartbeat -CurrentTitle '' -CompletedCount 0 -InstalledCount 0 -FailedCount $failed.Count -FailedTitles $failed.ToArray();" +
            "      $lastHeartbeat = Get-Date;" +
            "    };" +
            "    if ($elapsedSeconds -ge $TimeoutSeconds) {" +
            "      try { $searchJob.RequestAbort() } catch { };" +
            "      throw ('Search timed out after ' + $TimeoutSeconds + ' second(s).');" +
            "    };" +
            "  };" +
            "  $result = $Searcher.EndSearch($searchJob);" +
            "  try { $searchJob.CleanUp() } catch { };" +
            "  return $result;" +
            "};" +
            "function Get-CurrentTitle {" +
            "  param([object]$Updates,[int]$Index);" +
            "  if ($null -eq $Updates) { return '' };" +
            "  if ($Index -ge 0 -and $Index -lt $Updates.Count) { return [string]$Updates.Item($Index).Title };" +
            "  return '';" +
            "};" +
            "try {" +
            "if (-not (Test-Path -LiteralPath $PayloadPath)) { throw \"Payload file not found: $PayloadPath\" };" +
            "$items = Get-Content -LiteralPath $PayloadPath -Raw | ConvertFrom-Json;" +
            "if ($items -isnot [System.Collections.IEnumerable]) { $items = @($items) };" +
            "$items = @($items);" +
            "$script:TotalCount = $items.Count;" +
            "$failed = New-Object System.Collections.Generic.List[string];" +
            "$resolved = New-Object -ComObject Microsoft.Update.UpdateColl;" +
            "$processed = 0;" +
            "Write-Log ('Starting async install workflow for ' + $script:TotalCount + ' selected update(s).');" +
            "Write-State -Phase 'starting' -Message 'Preparing selected updates (async mode)...' -CurrentTitle '' -CompletedCount 0 -InstalledCount 0 -FailedCount 0 -FailedTitles @();" +
            "$session = New-Object -ComObject Microsoft.Update.Session;" +
            "$searcher = $session.CreateUpdateSearcher();" +
            "$searcher.Online = $false;" +
            "$criteria = \"(IsInstalled=0 and Type='Software') or (IsInstalled=0 and Type='Driver')\";" +
            "Write-Log ('Search started: loading available updates from local cache (single search pass). Criteria=' + $criteria);" +
            "Write-State -Phase 'resolving' -Message 'Searching local Windows Update cache...' -CurrentTitle '' -CompletedCount 0 -InstalledCount 0 -FailedCount $failed.Count -FailedTitles $failed.ToArray();" +
            "$available = Invoke-SearchWithHeartbeat -Searcher $searcher -Criteria $criteria -TimeoutSeconds 600;" +
            "$availableCode = [int]$available.ResultCode;" +
            "Write-Log ('Search completed. ResultCode=' + $availableCode + ' (' + (Get-ResultCodeText $availableCode) + ') Found=' + $available.Updates.Count + ' update(s).');" +
            "$availableByKey = @{};" +
            "foreach ($candidate in $available.Updates) {" +
            "  Add-AvailableUpdateIndexEntry -Index $availableByKey -Candidate $candidate -SourceLabel 'local cache';" +
            "};" +
            "$unresolvedItems = New-Object System.Collections.Generic.List[object];" +
            "foreach ($item in $items) {" +
            "  $title = [string]$item.Title;" +
            "  $updateId = [string]$item.UpdateId;" +
            "  $revision = [int]$item.Revision;" +
            "  Write-Log ('Resolving: ' + $title);" +
            "  Write-State -Phase 'resolving' -Message ('Resolving update: ' + $title) -CurrentTitle $title -CompletedCount $processed -InstalledCount 0 -FailedCount $failed.Count -FailedTitles $failed.ToArray();" +
            "  $match = Resolve-AvailableUpdate -Index $availableByKey -UpdateId $updateId -Revision $revision -Title $title;" +
            "  if ($null -eq $match) { [void]$unresolvedItems.Add($item); continue };" +
            "  if (-not [bool]$match.EulaAccepted) { $match.AcceptEula() };" +
            "  Write-Log ('Resolved: ' + $title + ' (Downloaded=' + [bool]$match.IsDownloaded + ', AutoSelect=' + [bool]$match.AutoSelectOnWebSites + ')');" +
            "  [void]$resolved.Add($match);" +
            "  $processed++;" +
            "};" +
            "if ($unresolvedItems.Count -gt 0) {" +
            "  Write-Log ('Local cache did not resolve ' + $unresolvedItems.Count + ' selected update(s). Falling back to online search.');" +
            "  $searcher.Online = $true;" +
            "  $onlineResult = Invoke-SearchWithHeartbeat -Searcher $searcher -Criteria $criteria -TimeoutSeconds 600;" +
            "  $onlineCode = [int]$onlineResult.ResultCode;" +
            "  Write-Log ('Online search completed. ResultCode=' + $onlineCode + ' (' + (Get-ResultCodeText $onlineCode) + ') Found=' + $onlineResult.Updates.Count + ' update(s).');" +
            "  foreach ($candidate in $onlineResult.Updates) {" +
            "    Add-AvailableUpdateIndexEntry -Index $availableByKey -Candidate $candidate -SourceLabel 'online catalog';" +
            "  };" +
            "  $stillUnresolved = New-Object System.Collections.Generic.List[object];" +
            "  foreach ($item in $unresolvedItems) {" +
            "    $title = [string]$item.Title;" +
            "    $updateId = [string]$item.UpdateId;" +
            "    $revision = [int]$item.Revision;" +
            "    $match = Resolve-AvailableUpdate -Index $availableByKey -UpdateId $updateId -Revision $revision -Title $title;" +
            "    if ($null -eq $match) {" +
            "      Write-Log ('Resolve failed: not found or already installed: ' + $title + ' (UpdateId=' + $updateId + ', Revision=' + $revision + ')');" +
            "      $failed.Add($title);" +
            "      $processed++;" +
            "      [void]$stillUnresolved.Add($item);" +
            "      continue;" +
            "    };" +
            "    if (-not [bool]$match.EulaAccepted) { $match.AcceptEula() };" +
            "    Write-Log ('Resolved: ' + $title + ' (Downloaded=' + [bool]$match.IsDownloaded + ', AutoSelect=' + [bool]$match.AutoSelectOnWebSites + ')');" +
            "    [void]$resolved.Add($match);" +
            "    $processed++;" +
            "  };" +
            "  $unresolvedItems = $stillUnresolved;" +
            "};" +
            "if ($resolved.Count -eq 0) {" +
            "  Write-Log 'No selected updates could be resolved for installation.';" +
            "  Write-State -Phase 'failed' -Message 'No selected updates could be resolved for installation.' -CurrentTitle '' -CompletedCount $processed -InstalledCount 0 -FailedCount $failed.Count -FailedTitles $failed.ToArray();" +
            "  exit 2;" +
            "};" +
            "Write-Log ('Resolution completed. Resolved=' + $resolved.Count + ' FailedToResolve=' + $failed.Count + '.');" +
            "Write-Log ('Download started for ' + $resolved.Count + ' update(s) (async mode).');" +
            "Write-State -Phase 'downloading' -Message ('Download started for ' + $resolved.Count + ' update(s) (async mode).') -CurrentTitle '' -CompletedCount $processed -InstalledCount 0 -FailedCount $failed.Count -FailedTitles $failed.ToArray();" +
            "$downloader = $session.CreateUpdateDownloader();" +
            "$downloader.Updates = $resolved;" +
            "$downloadJob = $null;" +
            "$downloadResult = $null;" +
            "$downloadUsedAsync = $true;" +
            "try {" +
            "  $downloadJob = $downloader.BeginDownload($null, $null, $null);" +
            "} catch {" +
            "  $downloadUsedAsync = $false;" +
            "  Write-Log ('BeginDownload unavailable or failed. Falling back to synchronous Download(). Reason=' + $_.Exception.Message);" +
            "};" +
            "if ($downloadUsedAsync -and $null -eq $downloadJob) {" +
            "  $downloadUsedAsync = $false;" +
            "  Write-Log 'BeginDownload returned no job. Falling back to synchronous Download().';" +
            "};" +
            "$lastDownloadPercent = -1;" +
            "$lastDownloadTitle = '';" +
            "$lastDownloadLog = [DateTime]::MinValue;" +
            "if ($downloadUsedAsync) {" +
            "  while (-not $downloadJob.IsCompleted) {" +
            "    Start-Sleep -Milliseconds 700;" +
            "    $downloadProgress = $null;" +
            "    try { $downloadProgress = $downloadJob.GetProgress() } catch { };" +
            "    if ($null -eq $downloadProgress) { continue };" +
            "    $downloadPercent = [int]$downloadProgress.PercentComplete;" +
            "    $downloadIndex = [int]$downloadProgress.CurrentUpdateIndex;" +
            "    $downloadUpdatePercent = [int]$downloadProgress.CurrentUpdatePercentComplete;" +
            "    $downloadTitle = Get-CurrentTitle -Updates $resolved -Index $downloadIndex;" +
            "    $shouldWriteDownload = $downloadPercent -ne $lastDownloadPercent -or $downloadTitle -ne $lastDownloadTitle -or ((Get-Date) - $lastDownloadLog).TotalSeconds -ge 30;" +
            "    if (-not $shouldWriteDownload) { continue };" +
            "    $downloadMessage = 'Downloading: ' + $downloadPercent + '% (Current=' + $downloadTitle + ' ' + $downloadUpdatePercent + '%)';" +
            "    Write-Log $downloadMessage;" +
            "    Write-State -Phase 'downloading' -Message $downloadMessage -CurrentTitle $downloadTitle -CompletedCount $processed -InstalledCount 0 -FailedCount $failed.Count -FailedTitles $failed.ToArray();" +
            "    $lastDownloadPercent = $downloadPercent;" +
            "    $lastDownloadTitle = $downloadTitle;" +
            "    $lastDownloadLog = Get-Date;" +
            "  };" +
            "  $downloadResult = $downloader.EndDownload($downloadJob);" +
            "  try { $downloadJob.CleanUp() } catch { };" +
            "} else {" +
            "  $downloadResult = $downloader.Download();" +
            "};" +
            "$downloadCode = [int]$downloadResult.ResultCode;" +
            "$downloadSummary = 'Download completed. ResultCode=' + $downloadCode + ' (' + (Get-ResultCodeText $downloadCode) + ') HResult=' + $downloadResult.HResult;" +
            "Write-Log $downloadSummary;" +
            "if ($downloadCode -ne 2 -and $downloadCode -ne 3) {" +
            "  Write-State -Phase 'failed' -Message $downloadSummary -CurrentTitle '' -CompletedCount $processed -InstalledCount 0 -FailedCount $failed.Count -FailedTitles $failed.ToArray();" +
            "  exit 3;" +
            "};" +
            "for ($i = 0; $i -lt $resolved.Count; $i++) {" +
            "  $candidate = $resolved.Item($i);" +
            "  Write-Log ('Download status: ' + [string]$candidate.Title + ' Downloaded=' + [bool]$candidate.IsDownloaded);" +
            "};" +
            "Write-Log ('Installation started for ' + $resolved.Count + ' update(s) (async mode).');" +
            "Write-State -Phase 'installing' -Message ('Installation started for ' + $resolved.Count + ' update(s) (async mode).') -CurrentTitle '' -CompletedCount $processed -InstalledCount 0 -FailedCount $failed.Count -FailedTitles $failed.ToArray();" +
            "$installer = $session.CreateUpdateInstaller();" +
            "$installer.Updates = $resolved;" +
            "$installJob = $null;" +
            "$installResult = $null;" +
            "$installUsedAsync = $true;" +
            "try {" +
            "  $installJob = $installer.BeginInstall($null, $null, $null);" +
            "} catch {" +
            "  $installUsedAsync = $false;" +
            "  Write-Log ('BeginInstall unavailable or failed. Falling back to synchronous Install(). Reason=' + $_.Exception.Message);" +
            "};" +
            "if ($installUsedAsync -and $null -eq $installJob) {" +
            "  $installUsedAsync = $false;" +
            "  Write-Log 'BeginInstall returned no job. Falling back to synchronous Install().';" +
            "};" +
            "$lastInstallPercent = -1;" +
            "$lastInstallIndex = -1;" +
            "$lastInstallLog = [DateTime]::MinValue;" +
            "if ($installUsedAsync) {" +
            "  while (-not $installJob.IsCompleted) {" +
            "    Start-Sleep -Milliseconds 700;" +
            "    $installProgress = $null;" +
            "    try { $installProgress = $installJob.GetProgress() } catch { };" +
            "    if ($null -eq $installProgress) { continue };" +
            "    $installPercent = [int]$installProgress.PercentComplete;" +
            "    $installIndex = [int]$installProgress.CurrentUpdateIndex;" +
            "    $installUpdatePercent = [int]$installProgress.CurrentUpdatePercentComplete;" +
            "    $installTitle = Get-CurrentTitle -Updates $resolved -Index $installIndex;" +
            "    $shouldWriteInstall = $installPercent -ne $lastInstallPercent -or $installIndex -ne $lastInstallIndex -or ((Get-Date) - $lastInstallLog).TotalSeconds -ge 30;" +
            "    if (-not $shouldWriteInstall) { continue };" +
            "    $installMessage = 'Installing: ' + $installPercent + '% (Current=' + $installTitle + ' ' + $installUpdatePercent + '%)';" +
            "    Write-Log $installMessage;" +
            "    Write-State -Phase 'installing' -Message $installMessage -CurrentTitle $installTitle -CompletedCount $processed -InstalledCount 0 -FailedCount $failed.Count -FailedTitles $failed.ToArray();" +
            "    $lastInstallPercent = $installPercent;" +
            "    $lastInstallIndex = $installIndex;" +
            "    $lastInstallLog = Get-Date;" +
            "  };" +
            "  $installResult = $installer.EndInstall($installJob);" +
            "  try { $installJob.CleanUp() } catch { };" +
            "} else {" +
            "  $installResult = $installer.Install();" +
            "};" +
            "$installCode = [int]$installResult.ResultCode;" +
            "Write-Log ('Installation finished. ResultCode=' + $installCode + ' (' + (Get-ResultCodeText $installCode) + ') HResult=' + $installResult.HResult + ' RebootRequired=' + [bool]$installResult.RebootRequired);" +
            "$installedCount = 0;" +
            "for ($i = 0; $i -lt $resolved.Count; $i++) {" +
            "  $title = [string]$resolved.Item($i).Title;" +
            "  $single = $installResult.GetUpdateResult($i);" +
            "  $resultCode = [int]$single.ResultCode;" +
            "  $hresult = [int]$single.HResult;" +
            "  if ($resultCode -eq 2 -or $resultCode -eq 3) { $installedCount++ } else { $failed.Add($title) };" +
            "  $resultMessage = 'Processed: ' + $title + ' (Result=' + $resultCode + ' ' + (Get-ResultCodeText $resultCode) + ', HResult=' + $hresult + ', RebootRequired=' + [bool]$single.RebootRequired + ')';" +
            "  Write-Log $resultMessage;" +
            "  Write-State -Phase 'installing' -Message $resultMessage -CurrentTitle $title -CompletedCount $script:TotalCount -InstalledCount $installedCount -FailedCount $failed.Count -FailedTitles $failed.ToArray();" +
            "};" +
            "$rebootRequired = [bool]$installResult.RebootRequired;" +
            "$finalPhase = if ($failed.Count -gt 0) { 'completed-with-errors' } elseif ($rebootRequired) { 'reboot-pending' } else { 'completed' };" +
            "$finalMessage = 'Installation finished. Installed=' + $installedCount + ' Failed=' + $failed.Count + ' RebootRequired=' + $rebootRequired;" +
            "if ($rebootRequired) { $finalMessage += ' Reboot pending.' }" +
            "Write-Log $finalMessage;" +
            "Write-State -Phase $finalPhase -Message $finalMessage -CurrentTitle '' -CompletedCount $script:TotalCount -InstalledCount $installedCount -FailedCount $failed.Count -RebootRequired $rebootRequired -FailedTitles $failed.ToArray();" +
            "if ($failed.Count -gt 0) { exit 4 };" +
            "exit 0;" +
            "} catch {" +
            "  $errorMessage = $_.Exception.Message;" +
            "  $errorDetail = Get-DetailedErrorText $_;" +
            "  Write-Log ('Unhandled exception: ' + $errorMessage);" +
            "  Write-Log ('Exception detail: ' + $errorDetail);" +
            "  Write-State -Phase 'failed' -Message ('Unhandled exception: ' + $errorMessage + ' | ' + $errorDetail) -CurrentTitle '' -CompletedCount 0 -InstalledCount 0 -FailedCount 0 -FailedTitles @();" +
            "  exit 1;" +
            "}";
    }

    private static string BuildInstallUpdatesWinRtScriptBody()
    {
        return
            "param([Parameter(Mandatory=$true)][string]$PayloadPath,[Parameter(Mandatory=$true)][string]$StatePath,[Parameter(Mandatory=$true)][string]$LogPath);" +
            "$ErrorActionPreference='Stop';" +
            "$ProgressPreference='SilentlyContinue';" +
            "function Write-Log {" +
            "  param([string]$Text);" +
            "  $logDir = Split-Path -Parent $LogPath;" +
            "  if (-not [string]::IsNullOrWhiteSpace($logDir)) { New-Item -ItemType Directory -Path $logDir -Force | Out-Null };" +
            "  $line = '[' + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss') + '] ' + $Text;" +
            "  $written = $false;" +
            "  for ($attempt = 0; $attempt -lt 8 -and -not $written; $attempt++) {" +
            "    try {" +
            "      $bytes = [System.Text.Encoding]::UTF8.GetBytes($line + [Environment]::NewLine);" +
            "      $stream = [System.IO.File]::Open($LogPath, [System.IO.FileMode]::Append, [System.IO.FileAccess]::Write, [System.IO.FileShare]::ReadWrite);" +
            "      try {" +
            "        $stream.Write($bytes, 0, $bytes.Length);" +
            "        $stream.Flush();" +
            "      } finally {" +
            "        $stream.Dispose();" +
            "      };" +
            "      $written = $true;" +
            "    } catch [System.IO.IOException] {" +
            "      Start-Sleep -Milliseconds 150;" +
            "    } catch [System.UnauthorizedAccessException] {" +
            "      Start-Sleep -Milliseconds 150;" +
            "    }" +
            "  };" +
            "};" +
            "function Write-State {" +
            "  param(" +
            "    [string]$Phase," +
            "    [string]$Message," +
            "    [string]$CurrentTitle," +
            "    [int]$CompletedCount," +
            "    [int]$InstalledCount," +
            "    [int]$FailedCount," +
            "    [bool]$RebootRequired = $false," +
            "    [string[]]$FailedTitles" +
            "  );" +
            "  $state = [PSCustomObject]@{" +
            "    phase = $Phase;" +
            "    message = $Message;" +
            "    currentTitle = $CurrentTitle;" +
            "    totalCount = $script:TotalCount;" +
            "    completedCount = $CompletedCount;" +
            "    installedCount = $InstalledCount;" +
            "    failedCount = $FailedCount;" +
            "    rebootRequired = $RebootRequired;" +
            "    failedTitles = @($FailedTitles);" +
            "    lastUpdatedUtc = [DateTime]::UtcNow.ToString('o')" +
            "  };" +
            "  $stateDir = Split-Path -Parent $StatePath;" +
            "  if (-not [string]::IsNullOrWhiteSpace($stateDir)) { New-Item -ItemType Directory -Path $stateDir -Force | Out-Null };" +
            "  $stateJson = $state | ConvertTo-Json -Depth 6;" +
            "  $tempPath = $StatePath + '.tmp';" +
            "  for ($attempt = 0; $attempt -lt 8; $attempt++) {" +
            "    try {" +
            "      Set-Content -LiteralPath $tempPath -Value $stateJson -Encoding UTF8 -Force;" +
            "      if (Test-Path -LiteralPath $StatePath) { Remove-Item -LiteralPath $StatePath -Force };" +
            "      Move-Item -LiteralPath $tempPath -Destination $StatePath -Force;" +
            "      break;" +
            "    } catch [System.IO.IOException] {" +
            "      Start-Sleep -Milliseconds 150;" +
            "    } catch [System.UnauthorizedAccessException] {" +
            "      Start-Sleep -Milliseconds 150;" +
            "    }" +
            "  };" +
            "};" +
            "function Get-DetailedErrorText {" +
            "  param([object]$ErrorRecord);" +
            "  if ($null -eq $ErrorRecord) { return 'No error record available.' };" +
            "  $parts = New-Object System.Collections.Generic.List[string];" +
            "  if ($ErrorRecord.Exception -and -not [string]::IsNullOrWhiteSpace($ErrorRecord.Exception.Message)) { $parts.Add('Exception=' + $ErrorRecord.Exception.Message) };" +
            "  if (-not [string]::IsNullOrWhiteSpace($ErrorRecord.FullyQualifiedErrorId)) { $parts.Add('ErrorId=' + $ErrorRecord.FullyQualifiedErrorId) };" +
            "  if ($ErrorRecord.InvocationInfo -and -not [string]::IsNullOrWhiteSpace($ErrorRecord.InvocationInfo.PositionMessage)) { $parts.Add('Position=' + ($ErrorRecord.InvocationInfo.PositionMessage -replace '\\r?\\n',' ')) };" +
            "  if (-not [string]::IsNullOrWhiteSpace($ErrorRecord.ScriptStackTrace)) { $parts.Add('Stack=' + ($ErrorRecord.ScriptStackTrace -replace '\\r?\\n',' | ')) };" +
            "  $detail = ($ErrorRecord | Out-String).Trim();" +
            "  if (-not [string]::IsNullOrWhiteSpace($detail)) { $parts.Add('Detail=' + ($detail -replace '\\r?\\n',' | ')) };" +
            "  if ($parts.Count -eq 0) { return 'No additional error details available.' };" +
            "  return [string]::Join(' || ', $parts);" +
            "};" +
            "function Get-UpdateLookupKeys {" +
            "  param([string]$UpdateId,[int]$Revision,[string]$Title);" +
            "  $keys = New-Object System.Collections.Generic.List[string];" +
            "  $normalizedUpdateId = if ([string]::IsNullOrWhiteSpace($UpdateId)) { '' } else { $UpdateId.Trim() };" +
            "  $baseUpdateId = $normalizedUpdateId;" +
            "  if ($baseUpdateId.Contains(':')) {" +
            "    $separatorIndex = $baseUpdateId.LastIndexOf(':');" +
            "    if ($separatorIndex -gt 0 -and $separatorIndex -lt ($baseUpdateId.Length - 1)) { $baseUpdateId = $baseUpdateId.Substring(0, $separatorIndex) }" +
            "  };" +
            "  if (-not [string]::IsNullOrWhiteSpace($baseUpdateId)) {" +
            "    if ($Revision -gt 0) { [void]$keys.Add($baseUpdateId + '|' + [string]$Revision) };" +
            "    [void]$keys.Add($baseUpdateId);" +
            "  };" +
            "  if (-not [string]::IsNullOrWhiteSpace($normalizedUpdateId)) {" +
            "    if ($Revision -gt 0 -and $normalizedUpdateId -ne $baseUpdateId) { [void]$keys.Add($normalizedUpdateId + '|' + [string]$Revision) };" +
            "    [void]$keys.Add($normalizedUpdateId);" +
            "  };" +
            "  if (-not [string]::IsNullOrWhiteSpace($Title)) { [void]$keys.Add('title|' + $Title.Trim()) };" +
            "  return @($keys | Select-Object -Unique);" +
            "};" +
            "function Add-AvailableUpdateIndexEntry {" +
            "  param([hashtable]$Index,[object]$Candidate,[string]$SourceLabel);" +
            "  if ($null -eq $Candidate) { return };" +
            "  $title = '';" +
            "  try { $title = [string]$Candidate.Title } catch { };" +
            "  $updateId = '';" +
            "  $revision = 0;" +
            "  try { $updateId = [string]$Candidate.Identity.UpdateID; $revision = [int]$Candidate.Identity.RevisionNumber } catch { };" +
            "  foreach ($key in Get-UpdateLookupKeys -UpdateId $updateId -Revision $revision -Title $title) {" +
            "    if (-not $Index.ContainsKey($key)) { $Index[$key] = $Candidate };" +
            "  };" +
            "  $candidateDownloaded = $false;" +
            "  try { $candidateDownloaded = [bool]$Candidate.IsDownloaded } catch { };" +
            "  Write-Log ('Search candidate [' + $SourceLabel + ']: Title=' + $title + ' UpdateId=' + $updateId + ' Revision=' + $revision + ' Downloaded=' + $candidateDownloaded + ' Action=' + (Get-WinRtActionText -Update $Candidate));" +
            "};" +
            "function Resolve-AvailableUpdate {" +
            "  param([hashtable]$Index,[string]$UpdateId,[int]$Revision,[string]$Title);" +
            "  foreach ($key in Get-UpdateLookupKeys -UpdateId $UpdateId -Revision $Revision -Title $Title) {" +
            "    if ($Index.ContainsKey($key)) { return $Index[$key] };" +
            "  };" +
            "  return $null;" +
            "};" +
            "function Get-WinRtType {" +
            "  param([string]$PrimaryName,[string]$FallbackName);" +
            "  $type = [Type]::GetType($PrimaryName, $false);" +
            "  if ($null -eq $type -and -not [string]::IsNullOrWhiteSpace($FallbackName)) {" +
            "    $type = [Type]::GetType($FallbackName, $false);" +
            "  };" +
            "  return $type;" +
            "};" +
            "function Get-AdministratorObject {" +
            "  param([type]$AdminType,[string]$OrganizationName);" +
            "  $registeredName = $null;" +
            "  try { $registeredName = $AdminType::GetRegisteredAdministratorName() } catch { };" +
            "  $adminResult = $null;" +
            "  try { $adminResult = $AdminType::GetRegisteredAdministrator($OrganizationName) } catch { };" +
            "  if ($null -ne $adminResult -and $null -ne $adminResult.Administrator) {" +
            "    return $adminResult.Administrator;" +
            "  };" +
            "  $optionsType = Get-WinRtType 'Windows.Management.Update.WindowsUpdateAdministratorOptions, Windows.Management.Update, ContentType=WindowsRuntime' 'Windows.Management.Update.WindowsUpdateAdministratorOptions, Windows, ContentType=WindowsRuntime';" +
            "  if ($null -eq $optionsType) { throw 'WindowsUpdateAdministratorOptions type could not be loaded.' };" +
            "  Write-Log ('No registered administrator available for organization ' + $OrganizationName + '. RegisteredName=' + [string]$registeredName + '. Attempting registration.');" +
            "  $opts = $optionsType::None;" +
            "  $null = $AdminType::RegisterForAdministration($OrganizationName, $opts);" +
            "  Start-Sleep -Seconds 1;" +
            "  $registeredResult = $AdminType::GetRegisteredAdministrator($OrganizationName);" +
            "  if ($null -eq $registeredResult -or $null -eq $registeredResult.Administrator) {" +
            "    $statusText = if ($null -ne $registeredResult) { [string]$registeredResult.Status } else { 'unknown' };" +
            "    throw ('No administrator object available for organization ' + $OrganizationName + ' after registration attempt. Status=' + $statusText);" +
            "  };" +
            "  return $registeredResult.Administrator;" +
            "};" +
            "function Get-SelectedUpdateIdentity {" +
            "  param([string]$UpdateId,[int]$Revision);" +
            "  if ([string]::IsNullOrWhiteSpace($UpdateId)) { return '' };" +
            "  if ($UpdateId.Contains(':')) { return $UpdateId };" +
            "  if ($Revision -gt 0) { return $UpdateId + ':' + [string]$Revision };" +
            "  return $UpdateId;" +
            "};" +
            "function Get-WinRtActionText {" +
            "  param([object]$Update);" +
            "  if ($null -eq $Update) { return '' };" +
            "  try { return [string]$Update.CurrentAction } catch { return '' };" +
            "};" +
            "function Get-WinRtActionResultMessage {" +
            "  param([object]$Update);" +
            "  if ($null -eq $Update) { return '' };" +
            "  $result = $null;" +
            "  try { $result = $Update.ActionResult } catch { $result = $null };" +
            "  if ($null -eq $result) { return '' };" +
            "  $action = '';" +
            "  $succeeded = '';" +
            "  $extendedError = '';" +
            "  try { $action = [string]$result.Action } catch { };" +
            "  try { $succeeded = [string][bool]$result.Succeeded } catch { };" +
            "  try { if ($result.ExtendedError) { $extendedError = [string]$result.ExtendedError.Message } } catch { };" +
            "  $message = 'LastAction=' + $action + ' Succeeded=' + $succeeded;" +
            "  if (-not [string]::IsNullOrWhiteSpace($extendedError)) { $message += ' Error=' + $extendedError };" +
            "  return $message;" +
            "};" +
            "function Resolve-WinRtUpdate {" +
            "  param([object[]]$Updates,[string]$RequestedIdentity,[string]$Title);" +
            "  $match = $null;" +
            "  if (-not [string]::IsNullOrWhiteSpace($RequestedIdentity)) {" +
            "    $match = $Updates | Where-Object { [string]$_.UpdateId -eq $RequestedIdentity } | Select-Object -First 1;" +
            "  };" +
            "  if ($null -eq $match -and -not [string]::IsNullOrWhiteSpace($Title)) {" +
            "    $match = $Updates | Where-Object { [string]$_.Title -eq $Title } | Select-Object -First 1;" +
            "  };" +
            "  return $match;" +
            "};" +
            "function Wait-ForWinRtActionTransition {" +
            "  param([object]$Manager,[string]$RequestedIdentity,[string]$Title,[string]$ApprovedAction,[int]$TimeoutSeconds);" +
            "  $startedAt = Get-Date;" +
            "  $lastHeartbeat = [DateTime]::MinValue;" +
            "  while (((Get-Date) - $startedAt).TotalSeconds -lt $TimeoutSeconds) {" +
            "    Start-Sleep -Milliseconds 800;" +
            "    $updates = @($Manager.GetApplicableUpdates());" +
            "    $refreshed = Resolve-WinRtUpdate -Updates $updates -RequestedIdentity $RequestedIdentity -Title $Title;" +
            "    if ($null -eq $refreshed) {" +
            "      return [PSCustomObject]@{ State='Completed'; Update=$null; CurrentAction=''; ResultMessage='' };" +
            "    };" +
            "    $currentAction = Get-WinRtActionText -Update $refreshed;" +
            "    $resultMessage = Get-WinRtActionResultMessage -Update $refreshed;" +
            "    if ([string]::IsNullOrWhiteSpace($currentAction)) {" +
            "      return [PSCustomObject]@{ State='Completed'; Update=$refreshed; CurrentAction=''; ResultMessage=$resultMessage };" +
            "    };" +
            "    if ([string]::Equals($currentAction, 'Reboot', [System.StringComparison]::OrdinalIgnoreCase)) {" +
            "      return [PSCustomObject]@{ State='RebootPending'; Update=$refreshed; CurrentAction=$currentAction; ResultMessage=$resultMessage };" +
            "    };" +
            "    if (-not [string]::Equals($currentAction, $ApprovedAction, [System.StringComparison]::OrdinalIgnoreCase)) {" +
            "      return [PSCustomObject]@{ State='NextAction'; Update=$refreshed; CurrentAction=$currentAction; ResultMessage=$resultMessage };" +
            "    };" +
            "    if ($lastHeartbeat -eq [DateTime]::MinValue -or ((Get-Date) - $lastHeartbeat).TotalSeconds -ge 30) {" +
            "      $heartbeat = 'WinRT action still running: ' + $Title + ' Action=' + $ApprovedAction + ' Elapsed=' + [int][Math]::Floor(((Get-Date) - $startedAt).TotalSeconds) + 's';" +
            "      if (-not [string]::IsNullOrWhiteSpace($resultMessage)) { $heartbeat += ' ' + $resultMessage };" +
            "      Write-Log $heartbeat;" +
            "      $lastHeartbeat = Get-Date;" +
            "    };" +
            "  };" +
            "  $updates = @($Manager.GetApplicableUpdates());" +
            "  $refreshed = Resolve-WinRtUpdate -Updates $updates -RequestedIdentity $RequestedIdentity -Title $Title;" +
            "  $currentAction = Get-WinRtActionText -Update $refreshed;" +
            "  $resultMessage = Get-WinRtActionResultMessage -Update $refreshed;" +
            "  return [PSCustomObject]@{ State='Timeout'; Update=$refreshed; CurrentAction=$currentAction; ResultMessage=$resultMessage };" +
            "};" +
            "try {" +
            "if (-not (Test-Path -LiteralPath $PayloadPath)) { throw \"Payload file not found: $PayloadPath\" };" +
            "$items = Get-Content -LiteralPath $PayloadPath -Raw | ConvertFrom-Json;" +
            "if ($items -isnot [System.Collections.IEnumerable]) { $items = @($items) };" +
            "$items = @($items);" +
            "$script:TotalCount = $items.Count;" +
            "$failed = New-Object System.Collections.Generic.List[string];" +
            "$approvedCount = 0;" +
            "$processed = 0;" +
            "Write-Log ('Starting WinRT install approval workflow for ' + $script:TotalCount + ' selected update(s).');" +
            "Write-State -Phase 'starting' -Message 'Preparing selected updates (WinRT approval mode)...' -CurrentTitle '' -CompletedCount 0 -InstalledCount 0 -FailedCount 0 -FailedTitles @();" +
            "$managerType = Get-WinRtType 'Windows.Management.Update.WindowsUpdateManager, Windows.Management.Update, ContentType=WindowsRuntime' 'Windows.Management.Update.WindowsUpdateManager, Windows, ContentType=WindowsRuntime';" +
            "$adminType = Get-WinRtType 'Windows.Management.Update.WindowsUpdateAdministrator, Windows.Management.Update, ContentType=WindowsRuntime' 'Windows.Management.Update.WindowsUpdateAdministrator, Windows, ContentType=WindowsRuntime';" +
            "if (-not $managerType -or -not $adminType) { throw 'Windows.Management.Update WinRT types could not be loaded on the target system.' };" +
            "$manager = [Activator]::CreateInstance($managerType, @('WindowsClientCenter-WU-Task'));" +
            "$organizationName = 'WindowsClientCenter';" +
            "$admin = Get-AdministratorObject -AdminType $adminType -OrganizationName $organizationName;" +
            "Write-Log ('Using WinRT administrator context for organization ' + $organizationName + '.');" +
            "Write-Log 'WinRT scan started.';" +
            "Write-State -Phase 'scanning' -Message 'Starting Windows Update scan via WinRT...' -CurrentTitle '' -CompletedCount 0 -InstalledCount 0 -FailedCount 0 -FailedTitles @();" +
            "$null = $manager.StartScan($false);" +
            "$scanStartedAt = Get-Date;" +
            "$lastScanHeartbeat = [DateTime]::MinValue;" +
            "while ($manager.IsScanning) {" +
            "  Start-Sleep -Milliseconds 700;" +
            "  $elapsedSeconds = [int][Math]::Floor(((Get-Date) - $scanStartedAt).TotalSeconds);" +
            "  if ($lastScanHeartbeat -eq [DateTime]::MinValue -or ((Get-Date) - $lastScanHeartbeat).TotalSeconds -ge 30) {" +
            "    $heartbeat = 'WinRT scan still running (' + $elapsedSeconds + 's elapsed).';" +
            "    Write-Log $heartbeat;" +
            "    Write-State -Phase 'scanning' -Message $heartbeat -CurrentTitle '' -CompletedCount 0 -InstalledCount 0 -FailedCount 0 -FailedTitles @();" +
            "    $lastScanHeartbeat = Get-Date;" +
            "  };" +
            "  if ($elapsedSeconds -ge 600) { throw 'WinRT scan timed out after 600 second(s).' };" +
            "};" +
            "Write-Log 'WinRT scan completed.';" +
            "$updates = @($manager.GetApplicableUpdates());" +
            "if ($null -eq $updates -or @($updates).Count -eq 0) {" +
            "  Write-Log 'No applicable WinRT updates found after scan.';" +
            "  Write-State -Phase 'failed' -Message 'No applicable WinRT updates found after scan.' -CurrentTitle '' -CompletedCount 0 -InstalledCount 0 -FailedCount 0 -FailedTitles @();" +
            "  exit 2;" +
            "};" +
            "Write-Log ('WinRT applicable updates found=' + @($updates).Count + '.');" +
            "foreach ($candidate in $updates) {" +
            "  $candidateAction = Get-WinRtActionText -Update $candidate;" +
            "  $candidateResult = Get-WinRtActionResultMessage -Update $candidate;" +
            "  $candidateMessage = 'WinRT update found: Title=' + [string]$candidate.Title + ' UpdateId=' + [string]$candidate.UpdateId + ' Action=' + $candidateAction + ' IsDriver=' + [bool]$candidate.IsDriver + ' IsFeatureUpdate=' + [bool]$candidate.IsFeatureUpdate + ' IsSecurity=' + [bool]$candidate.IsSecurity + ' IsMandatory=' + [bool]$candidate.IsMandatory + ' IsSeeker=' + [bool]$candidate.IsSeeker;" +
            "  if (-not [string]::IsNullOrWhiteSpace($candidateResult)) { $candidateMessage += ' ' + $candidateResult };" +
            "  Write-Log $candidateMessage;" +
            "};" +
            "$rebootRequired = $false;" +
            "foreach ($item in $items) {" +
            "  $title = [string]$item.Title;" +
            "  $requestedIdentity = Get-SelectedUpdateIdentity -UpdateId ([string]$item.UpdateId) -Revision ([int]$item.Revision);" +
            "  Write-Log ('Resolving via WinRT: ' + $title + ' (Identity=' + $requestedIdentity + ')');" +
            "  Write-State -Phase 'resolving' -Message ('Resolving update via WinRT: ' + $title) -CurrentTitle $title -CompletedCount $processed -InstalledCount $approvedCount -FailedCount $failed.Count -FailedTitles $failed.ToArray();" +
            "  $match = Resolve-WinRtUpdate -Updates $updates -RequestedIdentity $requestedIdentity -Title $title;" +
            "  if ($null -eq $match) { Write-Log ('Resolve failed via WinRT: not found or no longer applicable: ' + $title + ' (Identity=' + $requestedIdentity + ')'); $failed.Add($title); $processed++; continue };" +
            "  if ($match.IsEulaAccepted -eq $false -and -not [string]::IsNullOrWhiteSpace([string]$match.EulaText)) {" +
            "    try { $match.AcceptEula(); Write-Log ('Accepted EULA: ' + [string]$match.Title) } catch { Write-Log ('AcceptEula failed for ' + [string]$match.Title + ': ' + $_.Exception.Message) };" +
            "  };" +
            "  $completed = $false;" +
            "  for ($actionAttempt = 0; $actionAttempt -lt 6 -and -not $completed; $actionAttempt++) {" +
            "    $currentAction = Get-WinRtActionText -Update $match;" +
            "    if ([string]::IsNullOrWhiteSpace($currentAction)) {" +
            "      $resultMessage = Get-WinRtActionResultMessage -Update $match;" +
            "      Write-Log ('No further WinRT action required: ' + [string]$match.Title + '. ' + $resultMessage);" +
            "      $approvedCount++;" +
            "      $processed++;" +
            "      $completed = $true;" +
            "      break;" +
            "    };" +
            "    if ([string]::Equals($currentAction, 'Reboot', [System.StringComparison]::OrdinalIgnoreCase)) {" +
            "      $resultMessage = Get-WinRtActionResultMessage -Update $match;" +
            "      $rebootRequired = $true;" +
            "      Write-Log ('WinRT update requires reboot: ' + [string]$match.Title + '. ' + $resultMessage);" +
            "      $approvedCount++;" +
            "      $processed++;" +
            "      $completed = $true;" +
            "      break;" +
            "    };" +
            "    Write-Log ('Approving WinRT action: ' + $currentAction + ' for ' + [string]$match.Title + ' (UpdateId=' + [string]$match.UpdateId + ')');" +
            "    Write-State -Phase 'installing' -Message ('Approving WinRT action ' + $currentAction + ': ' + [string]$match.Title) -CurrentTitle ([string]$match.Title) -CompletedCount $processed -InstalledCount $approvedCount -FailedCount $failed.Count -FailedTitles $failed.ToArray();" +
            "    $admin.ApproveWindowsUpdateAction([string]$match.UpdateId, $currentAction);" +
            "    $waitResult = Wait-ForWinRtActionTransition -Manager $manager -RequestedIdentity ([string]$match.UpdateId) -Title ([string]$match.Title) -ApprovedAction $currentAction -TimeoutSeconds 900;" +
            "    if ($waitResult.State -eq 'Completed') {" +
            "      $approvedCount++;" +
            "      $processed++;" +
            "      $completed = $true;" +
            "      $completionMessage = 'WinRT update completed after action ' + $currentAction + ': ' + [string]$match.Title;" +
            "      if (-not [string]::IsNullOrWhiteSpace([string]$waitResult.ResultMessage)) { $completionMessage += ' ' + [string]$waitResult.ResultMessage };" +
            "      Write-Log $completionMessage;" +
            "      break;" +
            "    };" +
            "    if ($waitResult.State -eq 'RebootPending') {" +
            "      $approvedCount++;" +
            "      $processed++;" +
            "      $rebootRequired = $true;" +
            "      $completed = $true;" +
            "      $rebootMessage = 'WinRT update requires reboot after action ' + $currentAction + ': ' + [string]$match.Title;" +
            "      if (-not [string]::IsNullOrWhiteSpace([string]$waitResult.ResultMessage)) { $rebootMessage += ' ' + [string]$waitResult.ResultMessage };" +
            "      Write-Log $rebootMessage;" +
            "      break;" +
            "    };" +
            "    if ($waitResult.State -eq 'NextAction') {" +
            "      $match = $waitResult.Update;" +
            "      $nextAction = [string]$waitResult.CurrentAction;" +
            "      $transitionMessage = 'WinRT action transition detected for ' + [string]$match.Title + ': ' + $currentAction + ' -> ' + $nextAction;" +
            "      if (-not [string]::IsNullOrWhiteSpace([string]$waitResult.ResultMessage)) { $transitionMessage += ' ' + [string]$waitResult.ResultMessage };" +
            "      Write-Log $transitionMessage;" +
            "      continue;" +
            "    };" +
            "    $timeoutMessage = 'Timed out waiting for WinRT action ' + $currentAction + ' to finish for ' + [string]$match.Title + '.';" +
            "    if (-not [string]::IsNullOrWhiteSpace([string]$waitResult.ResultMessage)) { $timeoutMessage += ' ' + [string]$waitResult.ResultMessage };" +
            "    Write-Log $timeoutMessage;" +
            "    $failed.Add($title);" +
            "    $processed++;" +
            "    $completed = $true;" +
            "  };" +
            "  if (-not $completed) {" +
            "    Write-Log ('WinRT action sequence exceeded retry limit for ' + $title + '.');" +
            "    $failed.Add($title);" +
            "    $processed++;" +
            "  };" +
            "};" +
            "if ($approvedCount -eq 0) {" +
            "  Write-Log 'No selected updates could be approved for installation via WinRT.';" +
            "  Write-State -Phase 'failed' -Message 'No selected updates could be approved for installation via WinRT.' -CurrentTitle '' -CompletedCount $processed -InstalledCount 0 -FailedCount $failed.Count -FailedTitles $failed.ToArray();" +
            "  exit 3;" +
            "};" +
            "$finalPhase = if ($failed.Count -gt 0) { 'completed-with-errors' } elseif ($rebootRequired) { 'reboot-pending' } else { 'completed' };" +
            "$finalMessage = 'WinRT install approval finished. Approved=' + $approvedCount + ' Failed=' + $failed.Count + ' RebootRequired=' + $rebootRequired;" +
            "if ($rebootRequired) { $finalMessage += ' Reboot pending.' }" +
            "Write-Log $finalMessage;" +
            "Write-State -Phase $finalPhase -Message $finalMessage -CurrentTitle '' -CompletedCount $script:TotalCount -InstalledCount $approvedCount -FailedCount $failed.Count -RebootRequired $rebootRequired -FailedTitles $failed.ToArray();" +
            "if ($failed.Count -gt 0) { exit 4 };" +
            "exit 0;" +
            "} catch {" +
            "  $errorMessage = $_.Exception.Message;" +
            "  $errorDetail = Get-DetailedErrorText $_;" +
            "  Write-Log ('Unhandled exception: ' + $errorMessage);" +
            "  Write-Log ('Exception detail: ' + $errorDetail);" +
            "  Write-State -Phase 'failed' -Message ('Unhandled exception: ' + $errorMessage + ' | ' + $errorDetail) -CurrentTitle '' -CompletedCount 0 -InstalledCount 0 -FailedCount 0 -FailedTitles @();" +
            "  exit 1;" +
            "}";
    }

    private static string BuildInstallTaskCommand(string launcherPath)
    {
        return
            "powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass " +
            "-File \"" + launcherPath + "\"";
    }

    private static string BuildInstallTaskLauncherScriptBody(string scriptPath, string payloadPath, string statePath, string logPath)
    {
        var escapedScriptPath = EscapePowerShellSingleQuotedString(scriptPath);
        var escapedPayloadPath = EscapePowerShellSingleQuotedString(payloadPath);
        var escapedStatePath = EscapePowerShellSingleQuotedString(statePath);
        var escapedLogPath = EscapePowerShellSingleQuotedString(logPath);

        return
            "$ErrorActionPreference='Stop';" + Environment.NewLine +
            "$scriptPath='" + escapedScriptPath + "';" + Environment.NewLine +
            "$payloadPath='" + escapedPayloadPath + "';" + Environment.NewLine +
            "$statePath='" + escapedStatePath + "';" + Environment.NewLine +
            "$logPath='" + escapedLogPath + "';" + Environment.NewLine +
            "& powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $scriptPath -PayloadPath $payloadPath -StatePath $statePath -LogPath $logPath;" + Environment.NewLine +
            "exit $LASTEXITCODE;" + Environment.NewLine;
    }

    private static string BuildRemoteAdminPath(string host, string relativePathUnderSystemDrive)
    {
        var normalizedHost = host.Trim().TrimStart('\\');
        var normalizedRelativePath = relativePathUnderSystemDrive.TrimStart('\\').Replace(':', '$');
        return $@"\\{normalizedHost}\c$\{normalizedRelativePath}";
    }

    private static async Task<ExternalCommandResult> RunProcessAsync(string fileName, string arguments, CancellationToken cancellationToken)
    {
        var processStartInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using var process = new Process { StartInfo = processStartInfo };
        process.Start();

        var stdOutTask = process.StandardOutput.ReadToEndAsync();
        var stdErrTask = process.StandardError.ReadToEndAsync();

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            TryTerminateProcess(process);
            throw;
        }

        return new ExternalCommandResult(process.ExitCode, await stdOutTask, await stdErrTask);
    }

    private async Task<ExternalCommandResult> RunPowerShellFileAsync(string scriptPath, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var invocation = new StringBuilder()
            .Append("$scriptPath = '")
            .Append(EscapePowerShellSingleQuotedString(scriptPath))
            .AppendLine("';")
            .Append("& $scriptPath");

        foreach (var argument in arguments)
        {
            if (argument.StartsWith("-", StringComparison.Ordinal))
            {
                invocation.Append(' ').Append(argument);
            }
            else
            {
                invocation.Append(" '").Append(EscapePowerShellSingleQuotedString(argument)).Append('\'');
            }
        }

        invocation.Append(';');
        var execution = await RunPowershellAsync(invocation.ToString(), cancellationToken);
        return new ExternalCommandResult(execution.ExitCode, execution.StdOut, execution.StdErr);
    }

    private static string NormalizeExternalCommandError(ExternalCommandResult result)
    {
        var error = string.IsNullOrWhiteSpace(result.StdErr) ? result.StdOut : result.StdErr;
        if (string.IsNullOrWhiteSpace(error))
        {
            return $"Exit code {result.ExitCode}";
        }

        return error.Trim();
    }

    private static bool TryExtractJsonPayload(ExternalCommandResult result, out string payloadJson, out string errorMessage)
    {
        payloadJson = result.StdOut?.Trim() ?? string.Empty;
        errorMessage = string.Empty;

        if (result.ExitCode != 0)
        {
            errorMessage = NormalizeExternalCommandError(result);
            return false;
        }

        if (LooksLikeJson(payloadJson))
        {
            return true;
        }

        var stdErr = result.StdErr?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(stdErr))
        {
            errorMessage = stdErr;
            return false;
        }

        if (!string.IsNullOrWhiteSpace(payloadJson))
        {
            errorMessage = $"Expected JSON output but received: {payloadJson}";
            return false;
        }

        errorMessage = "Command returned no JSON output.";
        return false;
    }

    private static bool LooksLikeJson(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.TrimStart();
        return trimmed.StartsWith("{", StringComparison.Ordinal) || trimmed.StartsWith("[", StringComparison.Ordinal);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best effort cleanup for temporary script extraction.
        }
    }

    private static string? ParseSchtasksListValue(string input, string key)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        foreach (var rawLine in input.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var separatorIndex = line.IndexOf(':');
            if (separatorIndex <= 0)
            {
                continue;
            }

            var currentKey = line[..separatorIndex].Trim();
            if (!currentKey.Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return line[(separatorIndex + 1)..].Trim();
        }

        return null;
    }

    private bool TryGetStreamCheckpoint(string host, string path, out long position)
    {
        lock (_streamCheckpointSync)
        {
            if (_streamCheckpointPosition < 0 ||
                !string.Equals(_streamCheckpointHost, host, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(_streamCheckpointPath, path, StringComparison.OrdinalIgnoreCase))
            {
                position = 0;
                return false;
            }

            position = _streamCheckpointPosition;
            return true;
        }
    }

    private void UpdateStreamCheckpoint(string host, string path, long position)
    {
        lock (_streamCheckpointSync)
        {
            _streamCheckpointHost = host;
            _streamCheckpointPath = path;
            _streamCheckpointPosition = Math.Max(0, position);
        }
    }

    private void ResetStreamCheckpoint()
    {
        lock (_streamCheckpointSync)
        {
            _streamCheckpointHost = string.Empty;
            _streamCheckpointPath = string.Empty;
            _streamCheckpointPosition = -1;
        }
    }

    private bool IsCurrentStreamSession(long sessionId)
    {
        return sessionId == Interlocked.Read(ref _streamSessionId);
    }

    private static string BuildPowerShellScriptForHost(string host, bool useLocalAccess, string scriptBody)
    {
        if (useLocalAccess)
        {
            return "$ErrorActionPreference='Stop';$ProgressPreference='SilentlyContinue';" + scriptBody;
        }

        var escapedHost = host.Replace("'", "''", StringComparison.Ordinal);
        return
            "$ErrorActionPreference='Stop';" +
            $"$computerName='{escapedHost}';" +
            "Invoke-Command -ComputerName $computerName -ErrorAction Stop -ScriptBlock {" +
            "$ProgressPreference='SilentlyContinue';" +
            scriptBody +
            "};";
    }

    private static string BuildRemoteSchtasksScript(string taskName, string taskCommand, DateTime scheduledAt)
    {
        var escapedTaskName = EscapePowerShellSingleQuotedString(taskName);
        var escapedTaskCommand = EscapePowerShellSingleQuotedString(taskCommand);
        var escapedScheduledAt = EscapePowerShellSingleQuotedString(scheduledAt.ToString("HH:mm", CultureInfo.InvariantCulture));

        return
            $"$taskName='{escapedTaskName}';" +
            $"$taskCommand='{escapedTaskCommand}';" +
            $"$scheduledAt='{escapedScheduledAt}';" +
            "$createArgs=@('/Create','/TN',$taskName,'/TR',$taskCommand,'/SC','ONCE','/ST',$scheduledAt,'/RU','SYSTEM','/RL','HIGHEST','/F');" +
            "$createOutput=& schtasks.exe @createArgs 2>&1 | Out-String;" +
            "$createExit=$LASTEXITCODE;" +
            "if ($createExit -ne 0) { throw ('schtasks create failed (' + $createExit + '): ' + $createOutput.Trim()) };" +
            "$runOutput=& schtasks.exe /Run /TN $taskName 2>&1 | Out-String;" +
            "$runExit=$LASTEXITCODE;" +
            "if ($runExit -ne 0) { throw ('schtasks run failed (' + $runExit + '): ' + $runOutput.Trim()) };" +
            "$disableOutput=& schtasks.exe /Change /TN $taskName /DISABLE 2>&1 | Out-String;" +
            "$disableExit=$LASTEXITCODE;" +
            "if ($disableExit -ne 0) { throw ('schtasks disable failed (' + $disableExit + '): ' + $disableOutput.Trim()) };";
    }

    private static string EscapePowerShellSingleQuotedString(string value)
    {
        return value.Replace("'", "''", StringComparison.Ordinal);
    }

    private static string NormalizePowerShellError(string? rawError, int exitCode)
    {
        if (string.IsNullOrWhiteSpace(rawError))
        {
            return $"Exit code {exitCode}";
        }

        var text = rawError.Trim();
        if (text.Contains("<Objs", StringComparison.Ordinal) && text.Contains("schemas.microsoft.com/powershell/2004/04", StringComparison.Ordinal))
        {
            var matches = Regex.Matches(text, "<S S=\"Error\">(.*?)</S>", RegexOptions.Singleline | RegexOptions.CultureInvariant);
            var parts = new List<string>(matches.Count);
            foreach (Match match in matches)
            {
                if (!match.Success)
                {
                    continue;
                }

                var value = match.Groups[1].Value;
                value = DecodeClixmlEscapes(value);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    parts.Add(value.Trim());
                }
            }

            if (parts.Count > 0)
            {
                return string.Join(" ", parts);
            }
        }

        return DecodeClixmlEscapes(text);
    }

    private static bool TryParseUpdateServiceKillRequired(string? output, out int processId, out string serviceStatus)
    {
        processId = 0;
        serviceStatus = string.Empty;

        if (string.IsNullOrWhiteSpace(output))
        {
            return false;
        }

        var lines = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var line in lines)
        {
            if (!line.StartsWith(UpdateServiceKillRequiredMarker, StringComparison.Ordinal))
            {
                continue;
            }

            var parts = line.Split('|', 3, StringSplitOptions.None);
            if (parts.Length >= 2)
            {
                _ = int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out processId);
            }

            serviceStatus = parts.Length >= 3 ? parts[2].Trim() : string.Empty;
            return true;
        }

        return false;
    }

    private static string BuildHardKillPrompt(string host, int processId, string serviceStatus)
    {
        var normalizedHost = string.IsNullOrWhiteSpace(host) ? "the selected host" : $"'{host}'";
        var processText = processId > 0
            ? processId.ToString(CultureInfo.InvariantCulture)
            : "unknown";
        var statusText = string.IsNullOrWhiteSpace(serviceStatus) ? "Unknown" : serviceStatus;

        return
            $"The Windows Update service on {normalizedHost} did not stop within the timeout.{Environment.NewLine}{Environment.NewLine}" +
            $"Current status: {statusText}{Environment.NewLine}" +
            $"Process ID: {processText}{Environment.NewLine}{Environment.NewLine}" +
            "Do you want to forcefully terminate the hosting process and restart the service?" +
            $"{Environment.NewLine}{Environment.NewLine}" +
            "Warning: wuauserv may run inside a shared svchost process. Killing it can also terminate other services in the same process.";
    }

    private static bool ConfirmViaMessageBox(string title, string message)
    {
        return MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
    }

    private static string DecodeClixmlEscapes(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value
            .Replace("_x000D_", " ", StringComparison.OrdinalIgnoreCase)
            .Replace("_x000A_", " ", StringComparison.OrdinalIgnoreCase)
            .Replace("_x0009_", " ", StringComparison.OrdinalIgnoreCase);

        return Regex.Replace(normalized, @"\s+", " ").Trim();
    }

    private static void SplitUpdateIdentity(string? rawUpdateId, out string updateId, out int revision)
    {
        updateId = rawUpdateId ?? string.Empty;
        revision = 0;

        if (string.IsNullOrWhiteSpace(updateId))
        {
            updateId = string.Empty;
            return;
        }

        var separatorIndex = updateId.LastIndexOf(':');
        if (separatorIndex <= 0 || separatorIndex >= updateId.Length - 1)
        {
            return;
        }

        if (!int.TryParse(updateId[(separatorIndex + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out revision))
        {
            revision = 0;
            return;
        }

        updateId = updateId[..separatorIndex];
    }

    private void EnsureSqliteInitialized()
    {
        lock (_sqliteInitSync)
        {
            if (_sqliteInitialized)
            {
                return;
            }

            EnsureSqliteNativeLibraryLoaded();

            _ = ResolveSqliteAssembly("Microsoft.Data.Sqlite");
            _ = ResolveSqliteAssembly("SQLitePCLRaw.core");
            _ = ResolveSqliteAssembly("SQLitePCLRaw.provider.e_sqlite3");
            var batteriesAssembly = ResolveSqliteAssembly("SQLitePCLRaw.batteries_v2");
            var batteriesType = batteriesAssembly.GetType("SQLitePCL.Batteries_V2", throwOnError: true);
            var initMethod = batteriesType?.GetMethod("Init", BindingFlags.Public | BindingFlags.Static);
            if (initMethod is null)
            {
                throw new InvalidOperationException("SQLite initialization method 'SQLitePCL.Batteries_V2.Init' was not found.");
            }

            initMethod.Invoke(null, null);
            _sqliteInitialized = true;
        }
    }

    private Assembly ResolveSqliteAssembly(string assemblyName)
    {
        var pluginAssembly = GetType().Assembly;
        var pluginLoadContext = AssemblyLoadContext.GetLoadContext(pluginAssembly);
        var loadedAssembly = pluginLoadContext?.Assemblies.FirstOrDefault(assembly =>
            string.Equals(assembly.GetName().Name, assemblyName, StringComparison.OrdinalIgnoreCase));
        if (loadedAssembly is not null)
        {
            return loadedAssembly;
        }

        var pluginDirectory = Path.GetDirectoryName(pluginAssembly.Location);
        if (!string.IsNullOrWhiteSpace(pluginDirectory))
        {
            var assemblyPath = Path.Combine(pluginDirectory, assemblyName + ".dll");
            if (File.Exists(assemblyPath))
            {
                return pluginLoadContext is null
                    ? Assembly.LoadFrom(assemblyPath)
                    : pluginLoadContext.LoadFromAssemblyPath(assemblyPath);
            }
        }

        return Assembly.Load(assemblyName);
    }

    private void EnsureSqliteNativeLibraryLoaded()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var pluginDirectory = Path.GetDirectoryName(GetType().Assembly.Location);
        if (string.IsNullOrWhiteSpace(pluginDirectory))
        {
            return;
        }

        var rid = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm => "win-arm",
            Architecture.Arm64 => "win-arm64",
            Architecture.X86 => "win-x86",
            _ => "win-x64"
        };

        var candidatePaths = new[]
        {
            Path.Combine(pluginDirectory, "e_sqlite3.dll"),
            Path.Combine(pluginDirectory, "runtimes", rid, "native", "e_sqlite3.dll"),
            Path.Combine(pluginDirectory, rid, "native", "e_sqlite3.dll")
        };

        foreach (var candidatePath in candidatePaths)
        {
            if (!File.Exists(candidatePath))
            {
                continue;
            }

            if (NativeLibrary.TryLoad(candidatePath, out _))
            {
                return;
            }
        }
    }

    private static string FormatExceptionWithInnerMessages(Exception exception)
    {
        var messages = new List<string>();
        Exception? current = exception;
        while (current is not null)
        {
            if (!string.IsNullOrWhiteSpace(current.Message))
            {
                messages.Add(current.Message.Trim());
            }

            current = current.InnerException;
        }

        if (messages.Count == 0)
        {
            return "Unknown error.";
        }

        return string.Join(" | ", messages.Distinct(StringComparer.Ordinal));
    }

    private static string ResolveBundledPowerShellScriptExecutionPath(string relativePath, out bool deleteAfterUse)
    {
        if (TryGetBundledPowerShellScriptFilePath(relativePath, out var fullPath))
        {
            deleteAfterUse = false;
            return fullPath;
        }

        deleteAfterUse = true;
        return WriteBundledPowerShellScriptToTempFile(relativePath);
    }

    private static bool TryGetBundledPowerShellScriptFilePath(string relativePath, out string fullPath)
    {
        var assemblyPath = Assembly.GetExecutingAssembly().Location;
        var pluginDirectory = Path.GetDirectoryName(assemblyPath);
        if (string.IsNullOrWhiteSpace(pluginDirectory))
        {
            fullPath = string.Empty;
            return false;
        }

        fullPath = Path.Combine(pluginDirectory, relativePath);
        return File.Exists(fullPath);
    }

    private static string LoadBundledPowerShellScriptText(string relativePath)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceSuffix = relativePath
            .Replace('\\', '.')
            .Replace('/', '.');

        var resourceName = assembly
            .GetManifestResourceNames()
            .FirstOrDefault(name => name.EndsWith(resourceSuffix, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(resourceName))
        {
            using var resourceStream = assembly.GetManifestResourceStream(resourceName);
            if (resourceStream is not null)
            {
                using var reader = new StreamReader(resourceStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                return reader.ReadToEnd();
            }
        }

        if (!TryGetBundledPowerShellScriptFilePath(relativePath, out var fullPath))
        {
            throw new FileNotFoundException($"Bundled PowerShell script was not found as embedded resource or file: {relativePath}", relativePath);
        }

        return File.ReadAllText(fullPath, Encoding.UTF8);
    }

    private static string WriteBundledPowerShellScriptToTempFile(string relativePath)
    {
        var scriptText = LoadBundledPowerShellScriptText(relativePath);
        var scriptDirectory = Path.Combine(Path.GetTempPath(), "WindowsClientCenter", "WindowsUpdateAgent");
        Directory.CreateDirectory(scriptDirectory);

        var fileName = Path.GetFileName(relativePath);
        var tempPath = Path.Combine(scriptDirectory, $"{Guid.NewGuid():N}_{fileName}");
        File.WriteAllText(tempPath, scriptText, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return tempPath;
    }

    private static string BuildStartUpdateScanScriptBody()
    {
        return
            "function Get-WinRtType {" +
            "  param([string]$PrimaryName,[string]$FallbackName);" +
            "  $type = [Type]::GetType($PrimaryName, $false);" +
            "  if ($null -eq $type -and -not [string]::IsNullOrWhiteSpace($FallbackName)) {" +
            "    $type = [Type]::GetType($FallbackName, $false);" +
            "  };" +
            "  return $type;" +
            "};" +
            "$managerType = Get-WinRtType 'Windows.Management.Update.WindowsUpdateManager, Windows.Management.Update, ContentType=WindowsRuntime' 'Windows.Management.Update.WindowsUpdateManager, Windows, ContentType=WindowsRuntime';" +
            "if ($null -eq $managerType) { throw 'WindowsUpdateManager type could not be loaded on the target system.' };" +
            "$manager = [Activator]::CreateInstance($managerType, @('WindowsClientCenter-WU-Scan'));" +
            "$scanStartedAt = Get-Date;" +
            "if (-not $manager.IsScanning) {" +
            "  $null = $manager.StartScan($true);" +
            "};" +
            "while ($manager.IsScanning) {" +
            "  Start-Sleep -Milliseconds 700;" +
            "  $elapsedSeconds = [int][Math]::Floor(((Get-Date) - $scanStartedAt).TotalSeconds);" +
            "  if ($elapsedSeconds -ge 600) { throw 'Windows Update scan timed out after 600 second(s).' };" +
            "};" +
            "$completedAfterSeconds = [int][Math]::Floor(((Get-Date) - $scanStartedAt).TotalSeconds);" +
            "$lastScan = '';" +
            "try { $lastScan = [string]$manager.LastSuccessfulScanTimestamp } catch { };" +
            "if ([string]::IsNullOrWhiteSpace($lastScan)) {" +
            "  Write-Output ('Windows Update scan completed after ' + $completedAfterSeconds + ' second(s).');" +
            "} else {" +
            "  Write-Output ('Windows Update scan completed after ' + $completedAfterSeconds + ' second(s). LastSuccessfulScan=' + $lastScan + '.');" +
            "};";
    }

    private static string BuildRestartUpdateServiceScriptBody()
    {
        return
            BuildWaitForServiceStatusFunction() +
            BuildGetWindowsUpdateServiceProcessIdFunction() +
            "$serviceName='wuauserv';" +
            "$stopTimeout=[TimeSpan]::FromSeconds(60);" +
            "$startTimeout=[TimeSpan]::FromSeconds(60);" +
            "$stoppedStatus='Stopped';" +
            "$runningStatus='Running';" +
            "$service=Get-Service -Name $serviceName -ErrorAction Stop;" +
            "if (-not [string]::Equals([string]$service.Status, $stoppedStatus, [System.StringComparison]::OrdinalIgnoreCase)) {" +
            "  Stop-Service -Name $serviceName -Force -ErrorAction Stop;" +
            "  $stopped=$true;" +
            "  try { Wait-ForServiceStatus -ServiceName $serviceName -DesiredStatus $stoppedStatus -Timeout $stopTimeout | Out-Null } catch { $stopped=$false };" +
            "  $service=Get-Service -Name $serviceName -ErrorAction Stop;" +
            "  if (-not [string]::Equals([string]$service.Status, $stoppedStatus, [System.StringComparison]::OrdinalIgnoreCase)) {" +
            "    $processId=Get-WindowsUpdateServiceProcessId -ServiceName $serviceName;" +
            "    Write-Output ('" + UpdateServiceKillRequiredMarker + "|' + $processId + '|' + [string]$service.Status);" +
            "    return;" +
            "  };" +
            "};" +
            "Start-Service -Name $serviceName -ErrorAction Stop;" +
            "$service=Get-Service -Name $serviceName -ErrorAction Stop;" +
            "Wait-ForServiceStatus -ServiceName $serviceName -DesiredStatus $runningStatus -Timeout $startTimeout | Out-Null;" +
            "$service=Get-Service -Name $serviceName -ErrorAction Stop;" +
            "Write-Output ('Windows Update service restarted. Status=' + [string]$service.Status + '.');";
    }

    private static string BuildResetUpdateCacheScriptBody()
    {
        return
            BuildWaitForServiceStatusFunction() +
            BuildGetWindowsUpdateServiceProcessIdFunction() +
            "$serviceName='wuauserv';" +
            "$stopTimeout=[TimeSpan]::FromSeconds(60);" +
            "$startTimeout=[TimeSpan]::FromSeconds(60);" +
            "$stoppedStatus='Stopped';" +
            "$runningStatus='Running';" +
            "$service=Get-Service -Name $serviceName -ErrorAction Stop;" +
            "if (-not [string]::Equals([string]$service.Status, $stoppedStatus, [System.StringComparison]::OrdinalIgnoreCase)) {" +
            "  Stop-Service -Name $serviceName -Force -ErrorAction Stop;" +
            "  $stopped=$true;" +
            "  try { Wait-ForServiceStatus -ServiceName $serviceName -DesiredStatus $stoppedStatus -Timeout $stopTimeout | Out-Null } catch { $stopped=$false };" +
            "  $service=Get-Service -Name $serviceName -ErrorAction Stop;" +
            "  if (-not [string]::Equals([string]$service.Status, $stoppedStatus, [System.StringComparison]::OrdinalIgnoreCase)) {" +
            "    $processId=Get-WindowsUpdateServiceProcessId -ServiceName $serviceName;" +
            "    Write-Output ('" + UpdateServiceKillRequiredMarker + "|' + $processId + '|' + [string]$service.Status);" +
            "    return;" +
            "  };" +
            "};" +
            "$bits = Get-Service -Name 'BITS' -ErrorAction SilentlyContinue;" +
            "if ($null -ne $bits -and -not [string]::Equals([string]$bits.Status, $stoppedStatus, [System.StringComparison]::OrdinalIgnoreCase)) {" +
            "  Stop-Service -Name 'BITS' -Force -ErrorAction Stop;" +
            "  Wait-ForServiceStatus -ServiceName 'BITS' -DesiredStatus $stoppedStatus -Timeout $stopTimeout | Out-Null;" +
            "};" +
            "$downloadPath = Join-Path $env:SystemRoot 'SoftwareDistribution\\Download';" +
            "$removedCount = 0;" +
            "if (Test-Path -LiteralPath $downloadPath) {" +
            "  $items = @(Get-ChildItem -LiteralPath $downloadPath -Force -ErrorAction SilentlyContinue);" +
            "  $removedCount = $items.Count;" +
            "  foreach ($item in $items) { Remove-Item -LiteralPath $item.FullName -Recurse -Force -ErrorAction Stop };" +
            "};" +
            "Start-Service -Name $serviceName -ErrorAction Stop;" +
            "$service=Get-Service -Name $serviceName -ErrorAction Stop;" +
            "Wait-ForServiceStatus -ServiceName $serviceName -DesiredStatus $runningStatus -Timeout $startTimeout | Out-Null;" +
            "if ($null -ne $bits) {" +
            "  Start-Service -Name 'BITS' -ErrorAction Stop;" +
            "  $bits = Get-Service -Name 'BITS' -ErrorAction Stop;" +
            "  Wait-ForServiceStatus -ServiceName 'BITS' -DesiredStatus $runningStatus -Timeout $startTimeout | Out-Null;" +
            "};" +
            "$service=Get-Service -Name $serviceName -ErrorAction Stop;" +
            "$bitsStatus='NotInstalled';" +
            "if ($null -ne $bits) {" +
            "  $bits = Get-Service -Name 'BITS' -ErrorAction Stop;" +
            "  $bitsStatus = [string]$bits.Status;" +
            "};" +
            "Write-Output ('Windows Update cache reset. RemovedEntries=' + $removedCount + ' Wuauserv=' + [string]$service.Status + ' BITS=' + $bitsStatus + '.');";
    }

    private static string BuildGetWindowsUpdateServiceProcessIdFunction()
    {
        return
            "function Get-WindowsUpdateServiceProcessId {" +
            "  param([string]$ServiceName);" +
            "  if ([string]::IsNullOrWhiteSpace($ServiceName)) { $ServiceName = 'wuauserv' };" +
            "  try {" +
            "    $service = Get-CimInstance -ClassName Win32_Service -Filter (\"Name='\" + $ServiceName + \"'\") -ErrorAction Stop;" +
            "    if ($null -ne $service -and [int]$service.ProcessId -gt 0) { return [int]$service.ProcessId }" +
            "  } catch { };" +
            "  try {" +
            "    $taskList = & tasklist.exe /svc /fi ('services eq ' + $ServiceName) /fo csv /nh 2>$null;" +
            "    foreach ($line in @($taskList)) {" +
            "      $text = [string]$line;" +
            "      if ([string]::IsNullOrWhiteSpace($text)) { continue };" +
            "      if ($text.StartsWith('INFO:', [System.StringComparison]::OrdinalIgnoreCase)) { continue };" +
            "      try {" +
            "        $entry = $text | ConvertFrom-Csv -Header 'ImageName', 'PID', 'Services' | Select-Object -First 1;" +
            "        if ($null -ne $entry) {" +
            "          $candidatePid = 0;" +
            "          if ([int]::TryParse([string]$entry.PID, [ref]$candidatePid) -and $candidatePid -gt 0) { return $candidatePid }" +
            "        }" +
            "      } catch { };" +
            "    }" +
            "  } catch { };" +
            "  try {" +
            "    $scOutput = & sc.exe queryex $ServiceName 2>$null;" +
            "    foreach ($line in @($scOutput)) {" +
            "      $text = [string]$line;" +
            "      if ($text -match 'PID\\s*:\\s*(\\d+)') {" +
            "        $candidatePid = [int]$Matches[1];" +
            "        if ($candidatePid -gt 0) { return $candidatePid }" +
            "      }" +
            "    }" +
            "  } catch { };" +
            "  return 0;" +
            "}";
    }

    private static string BuildWaitForServiceStatusFunction()
    {
        return
            "function Wait-ForServiceStatus {" +
            "  param(" +
            "    [Parameter(Mandatory=$true)][string]$ServiceName," +
            "    [Parameter(Mandatory=$true)][string]$DesiredStatus," +
            "    [Parameter(Mandatory=$true)][TimeSpan]$Timeout" +
            "  );" +
            "  $deadline=(Get-Date).Add($Timeout);" +
            "  while ((Get-Date) -lt $deadline) {" +
            "    $service = Get-Service -Name $ServiceName -ErrorAction Stop;" +
            "    if ([string]::Equals([string]$service.Status, $DesiredStatus, [System.StringComparison]::OrdinalIgnoreCase)) {" +
            "      return $service;" +
            "    };" +
            "    Start-Sleep -Milliseconds 500;" +
            "  };" +
            "  $service = Get-Service -Name $ServiceName -ErrorAction Stop;" +
            "  throw ('Timed out waiting for service ' + $ServiceName + ' to reach status ' + $DesiredStatus + '. Current status=' + [string]$service.Status + '.');" +
            "}";
    }

    private static string BuildKillUpdateServiceProcessScriptBody(int processId)
    {
        return
            BuildWaitForServiceStatusFunction() +
            BuildGetWindowsUpdateServiceProcessIdFunction() +
            "$serviceName='wuauserv';" +
            $"$targetProcessId={processId.ToString(CultureInfo.InvariantCulture)};" +
            "if ($targetProcessId -le 0) {" +
            "  $targetProcessId=Get-WindowsUpdateServiceProcessId -ServiceName $serviceName;" +
            "};" +
            "if ($targetProcessId -le 0) { throw 'Could not determine the Windows Update service process id.' };" +
            "$process=Get-Process -Id $targetProcessId -ErrorAction SilentlyContinue;" +
            "if ($null -ne $process) {" +
            "  Stop-Process -Id $targetProcessId -Force -ErrorAction Stop;" +
            "  $deadline=(Get-Date).AddSeconds(20);" +
            "  while ((Get-Date) -lt $deadline) {" +
            "    if (-not (Get-Process -Id $targetProcessId -ErrorAction SilentlyContinue)) { break };" +
            "    Start-Sleep -Milliseconds 500;" +
            "  };" +
            "};" +
            "Start-Service -Name $serviceName -ErrorAction Stop;" +
            "$service=Get-Service -Name $serviceName -ErrorAction Stop;" +
            "Wait-ForServiceStatus -ServiceName $serviceName -DesiredStatus 'Running' -Timeout ([TimeSpan]::FromSeconds(60)) | Out-Null;" +
            "$service=Get-Service -Name $serviceName -ErrorAction Stop;" +
            "Write-Output ('Windows Update service process terminated. PreviousPid=' + $targetProcessId + ' Status=' + [string]$service.Status + '.');";
    }

    private static string BuildKillUpdateServiceProcessAndResetCacheScriptBody(int processId)
    {
        return
            BuildWaitForServiceStatusFunction() +
            BuildGetWindowsUpdateServiceProcessIdFunction() +
            "$serviceName='wuauserv';" +
            "$startTimeout=[TimeSpan]::FromSeconds(60);" +
            $"$targetProcessId={processId.ToString(CultureInfo.InvariantCulture)};" +
            "if ($targetProcessId -le 0) {" +
            "  $targetProcessId=Get-WindowsUpdateServiceProcessId -ServiceName $serviceName;" +
            "};" +
            "if ($targetProcessId -le 0) { throw 'Could not determine the Windows Update service process id.' };" +
            "$process=Get-Process -Id $targetProcessId -ErrorAction SilentlyContinue;" +
            "if ($null -ne $process) {" +
            "  Stop-Process -Id $targetProcessId -Force -ErrorAction Stop;" +
            "  $deadline=(Get-Date).AddSeconds(20);" +
            "  while ((Get-Date) -lt $deadline) {" +
            "    if (-not (Get-Process -Id $targetProcessId -ErrorAction SilentlyContinue)) { break };" +
            "    Start-Sleep -Milliseconds 500;" +
            "  };" +
            "};" +
            "$bits = Get-Service -Name 'BITS' -ErrorAction SilentlyContinue;" +
            "if ($null -ne $bits -and -not [string]::Equals([string]$bits.Status, 'Stopped', [System.StringComparison]::OrdinalIgnoreCase)) {" +
            "  Stop-Service -Name 'BITS' -Force -ErrorAction Stop;" +
            "  Wait-ForServiceStatus -ServiceName 'BITS' -DesiredStatus 'Stopped' -Timeout ([TimeSpan]::FromSeconds(20)) | Out-Null;" +
            "};" +
            "$downloadPath = Join-Path $env:SystemRoot 'SoftwareDistribution\\Download';" +
            "$removedCount = 0;" +
            "if (Test-Path -LiteralPath $downloadPath) {" +
            "  $items = @(Get-ChildItem -LiteralPath $downloadPath -Force -ErrorAction SilentlyContinue);" +
            "  $removedCount = $items.Count;" +
            "  foreach ($item in $items) { Remove-Item -LiteralPath $item.FullName -Recurse -Force -ErrorAction Stop };" +
            "};" +
            "Start-Service -Name $serviceName -ErrorAction Stop;" +
            "$service=Get-Service -Name $serviceName -ErrorAction Stop;" +
            "Wait-ForServiceStatus -ServiceName $serviceName -DesiredStatus 'Running' -Timeout $startTimeout | Out-Null;" +
            "$bitsStatus='NotInstalled';" +
            "if ($null -ne $bits) {" +
            "  Start-Service -Name 'BITS' -ErrorAction Stop;" +
            "  $bits = Get-Service -Name 'BITS' -ErrorAction Stop;" +
            "  Wait-ForServiceStatus -ServiceName 'BITS' -DesiredStatus 'Running' -Timeout $startTimeout | Out-Null;" +
            "  $bitsStatus = [string]$bits.Status;" +
            "};" +
            "$service=Get-Service -Name $serviceName -ErrorAction Stop;" +
            "Write-Output ('Windows Update service process terminated. PreviousPid=' + $targetProcessId + ' RemovedEntries=' + $removedCount + ' Wuauserv=' + [string]$service.Status + ' BITS=' + $bitsStatus + '.');";
    }

    private static string BuildRegisteredUpdateProvidersScriptBody()
    {
        return
            "$providers = @();" +
            "try {" +
            "  $serviceManager = New-Object -ComObject Microsoft.Update.ServiceManager;" +
            "  $services = $null;" +
            "  try { $services = $serviceManager.Services } catch { };" +
            "  if ($null -ne $services) {" +
            "    $serviceCount = 0;" +
            "    try { $serviceCount = [int]$services.Count } catch { $serviceCount = 0 };" +
            "    for ($index = 0; $index -lt $serviceCount; $index++) {" +
            "      $service = $null;" +
            "      try { $service = $services.Item($index) } catch { continue };" +
            "      if ($null -eq $service) { continue };" +
            "      $name = '';" +
            "      $serviceId = '';" +
            "      $isDefault = $false;" +
            "      $isRegisteredWithAU = $false;" +
            "      $offersWindowsUpdates = $false;" +
            "      $isManaged = $false;" +
            "      try { $name = [string]$service.Name } catch { };" +
            "      try { $serviceId = [string]$service.ServiceID } catch { };" +
            "      try { $isDefault = [bool]$service.IsDefaultAUService } catch { };" +
            "      try { $isRegisteredWithAU = [bool]$service.IsRegisteredWithAU } catch { };" +
            "      try { $offersWindowsUpdates = [bool]$service.OffersWindowsUpdates } catch { };" +
            "      try { $isManaged = [bool]$service.IsManaged } catch { };" +
            "      $providers += [PSCustomObject]@{" +
            "        Name = $name;" +
            "        ServiceId = $serviceId;" +
            "        IsDefault = [bool]$isDefault;" +
            "        IsRegisteredWithAU = [bool]$isRegisteredWithAU;" +
            "        OffersWindowsUpdates = [bool]$offersWindowsUpdates;" +
            "        IsManaged = [bool]$isManaged;" +
            "      };" +
            "    };" +
            "  };" +
            "} catch { };" +
            "$payload = [PSCustomObject]@{" +
            "  Providers = @($providers);" +
            "};" +
            "$payload | ConvertTo-Json -Depth 4 -Compress;";
    }

    private async Task<UpdateHistoryPayload> LoadUpdateHistoryFromWinRtAsync(string host, CancellationToken cancellationToken)
    {
        var useLocalAccess = IsLocalHost(host);
        var scriptPath = ResolveBundledPowerShellScriptExecutionPath(WinRtUpdateClientScriptRelativePath, out var deleteAfterUse);

        try
        {
            var arguments = new List<string>
            {
                "-Completed",
                "-CompletedCount",
                WinRtCompletedHistoryCount.ToString(CultureInfo.InvariantCulture),
                "-AsJson"
            };

            if (!useLocalAccess)
            {
                arguments.Add("-ComputerName");
                arguments.Add(host);
            }

            AppendLine($"[WU][DEBUG] Running WinRT completed-updates script for '{host}' with args: {string.Join(" ", arguments)}");
            var execution = await RunPowerShellFileAsync(scriptPath, arguments, cancellationToken);
            AppendExternalCommandDebug("WinRT completed-updates", execution);
            if (!TryExtractJsonPayload(execution, out var payloadJson, out var commandError))
            {
                throw new InvalidOperationException(commandError);
            }

            AppendWinRtPayloadDebug("completed", payloadJson);
            return ParseWinRtCompletedUpdatesPayload(payloadJson);
        }
        finally
        {
            if (deleteAfterUse)
            {
                TryDeleteFile(scriptPath);
            }
        }
    }

    private async Task<UpdateHistoryPayload> LoadUpdateHistoryFromUsoStoreAsync(string host, CancellationToken cancellationToken)
    {
        var prepared = await PrepareUsoDatabaseSourceForHostAsync(host, cancellationToken);
        var snapshotPath = prepared.DatabasePath;
        var snapshotDirectory = Path.GetDirectoryName(snapshotPath) ?? string.Empty;

        try
        {
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = snapshotPath,
                Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Shared
            }.ToString());

            await connection.OpenAsync(cancellationToken);
            await using (var pragmaCommand = connection.CreateCommand())
            {
                pragmaCommand.CommandText = "PRAGMA query_only = ON; PRAGMA busy_timeout = 1000;";
                await pragmaCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            var tableName = await SelectBestHistoryTableAsync(connection, cancellationToken);
            if (string.IsNullOrWhiteSpace(tableName))
            {
                throw new InvalidOperationException("No suitable history table found in store.db.");
            }

            var columns = await GetTableColumnsAsync(connection, tableName, cancellationToken);
            var query = BuildHistoryQuery(tableName, columns);

            await using var command = connection.CreateCommand();
            command.CommandText = query;

            var entries = new List<WindowsUpdateHistoryEntry>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                entries.Add(new WindowsUpdateHistoryEntry(
                    Date: FormatDate(reader["DateValue"]?.ToString()),
                    Operation: MapHistoryOperation(reader["OperationValue"]?.ToString()),
                    Result: MapHistoryResult(reader["ResultValue"]?.ToString()),
                    HResult: FormatHistoryHResult(reader["HResultValue"]?.ToString()),
                    Title: reader["TitleValue"]?.ToString() ?? string.Empty,
                    UpdateId: reader["UpdateIdValue"]?.ToString() ?? string.Empty,
                    Revision: ParseInt(reader["RevisionValue"]),
                    ClientApplicationId: reader["ClientApplicationIdValue"]?.ToString() ?? string.Empty,
                    ServiceId: reader["ServiceIdValue"]?.ToString() ?? string.Empty,
                    PackageName: reader["PackageNameValue"]?.ToString() ?? string.Empty));
            }

            var totalCount = await CountHistoryRowsAsync(connection, tableName, cancellationToken);
            return new UpdateHistoryPayload(totalCount, entries.Count, entries);
        }
        finally
        {
            if (!string.Equals(prepared.CleanupDirectory, _usoWorkingDirectory, StringComparison.OrdinalIgnoreCase))
            {
                TryDeleteDirectory(prepared.CleanupDirectory ?? snapshotDirectory);
            }
        }
    }

    private static string ResolveUsoStorePath(string host)
    {
        if (IsLocalHost(host))
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "USOPrivate", "UpdateStore", "store.db");
        }

        var normalizedHost = host.Trim().TrimStart('\\');
        return $@"\\{normalizedHost}\c$\{UsoStoreRelativePath}";
    }

    private static string BuildRemoteReportingEventsUncPath(string host)
    {
        var normalizedHost = host.Trim().TrimStart('\\');
        return $@"\\{normalizedHost}\admin$\Windows\SoftwareDistribution\ReportingEvents.log";
    }

    private static async Task<string> CreateStoreSnapshotAsync(string sourceDbPath, CancellationToken cancellationToken)
    {
        if (!File.Exists(sourceDbPath))
        {
            throw new FileNotFoundException("USO UpdateStore database was not found.", sourceDbPath);
        }

        var snapshotDirectory = Path.Combine(Path.GetTempPath(), "WindowsClientCenter", "UsoStoreSnapshot", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(snapshotDirectory);

        var snapshotDbPath = Path.Combine(snapshotDirectory, "store.db");
        await CopyReadableAsync(sourceDbPath, snapshotDbPath, cancellationToken);
        await CopyIfExistsAsync(sourceDbPath + "-wal", snapshotDbPath + "-wal", cancellationToken);
        await CopyIfExistsAsync(sourceDbPath + "-shm", snapshotDbPath + "-shm", cancellationToken);
        return snapshotDbPath;
    }

    private static async Task CopyIfExistsAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken)
    {
        if (!File.Exists(sourcePath))
        {
            return;
        }

        await CopyReadableAsync(sourcePath, destinationPath, cancellationToken);
    }

    private static async Task CopyReadableAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken)
    {
        await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        await using var destination = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.Read);
        await source.CopyToAsync(destination, cancellationToken);
    }

    private static async Task<string?> SelectBestHistoryTableAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var tables = new List<string>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%';";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var name = reader.GetString(0);
                if (!string.IsNullOrWhiteSpace(name))
                {
                    tables.Add(name);
                }
            }
        }

        string? bestTable = null;
        var bestScore = int.MinValue;
        foreach (var table in tables)
        {
            var columns = await GetTableColumnsAsync(connection, table, cancellationToken);
            var score = ScoreHistoryTable(table, columns);
            if (score > bestScore)
            {
                bestScore = score;
                bestTable = table;
            }
        }

        return bestScore > 0 ? bestTable : null;
    }

    private static async Task<HashSet<string>> GetTableColumnsAsync(SqliteConnection connection, string tableName, CancellationToken cancellationToken)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({QuoteIdentifier(tableName)});";

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var columnName = reader["name"]?.ToString();
            if (!string.IsNullOrWhiteSpace(columnName))
            {
                columns.Add(columnName);
            }
        }

        return columns;
    }

    private static int ScoreHistoryTable(string tableName, HashSet<string> columns)
    {
        var score = 0;
        var table = tableName.ToLowerInvariant();
        if (table.Contains("history", StringComparison.Ordinal))
        {
            score += 6;
        }

        if (table.Contains("update", StringComparison.Ordinal))
        {
            score += 2;
        }

        if (ContainsAny(columns, "Date", "Timestamp", "Time", "InstallTime", "LastChangeTime"))
        {
            score += 3;
        }

        if (ContainsAny(columns, "UpdateId", "UpdateID", "UpdateGuid", "Guid"))
        {
            score += 2;
        }

        if (ContainsAny(columns, "Title", "UpdateTitle", "Name"))
        {
            score += 2;
        }

        if (ContainsAny(columns, "Result", "ResultCode", "Status"))
        {
            score += 2;
        }

        if (ContainsAny(columns, "Operation", "OperationType", "Action"))
        {
            score += 1;
        }

        return score;
    }

    private static string BuildHistoryQuery(string tableName, HashSet<string> columns)
    {
        var dateColumn = SelectColumn(columns, "Date", "Timestamp", "Time", "InstallTime", "LastChangeTime");
        var operationColumn = SelectColumn(columns, "Operation", "OperationType", "Action");
        var resultColumn = SelectColumn(columns, "ResultCode", "Result", "Status");
        var hResultColumn = SelectColumn(columns, "HResult", "HR", "ErrorCode", "Win32Hresult");
        var titleColumn = SelectColumn(columns, "Title", "UpdateTitle", "Name");
        var updateIdColumn = SelectColumn(columns, "UpdateId", "UpdateID", "UpdateGuid", "Guid");
        var revisionColumn = SelectColumn(columns, "Revision", "RevisionNumber");
        var clientApplicationIdColumn = SelectColumn(columns, "ClientApplicationId", "ClientApplicationID", "CallerApplicationId");
        var serviceIdColumn = SelectColumn(columns, "ServiceId", "ServiceID");
        var packageNameColumn = SelectColumn(columns, "PackageName", "PackageIdentity", "PackageId", "PackageFullName");
        var sortColumn = dateColumn ?? SelectColumn(columns, "RowId", "Id") ?? "rowid";
        var sortExpression = sortColumn.Equals("rowid", StringComparison.OrdinalIgnoreCase)
            ? "rowid"
            : QuoteIdentifier(sortColumn);

        var query =
            $"""
            SELECT
              {SqlText(dateColumn)} AS DateValue,
              {SqlText(operationColumn)} AS OperationValue,
              {SqlText(resultColumn)} AS ResultValue,
              {SqlText(hResultColumn)} AS HResultValue,
              {SqlText(titleColumn)} AS TitleValue,
              {SqlText(updateIdColumn)} AS UpdateIdValue,
              {SqlInt(revisionColumn)} AS RevisionValue,
              {SqlText(clientApplicationIdColumn)} AS ClientApplicationIdValue,
              {SqlText(serviceIdColumn)} AS ServiceIdValue,
              {SqlText(packageNameColumn)} AS PackageNameValue
            FROM {QuoteIdentifier(tableName)}
            ORDER BY {sortExpression} DESC;
            """;

        return query;
    }

    private static async Task<int> CountHistoryRowsAsync(SqliteConnection connection, string tableName, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(1) FROM {QuoteIdentifier(tableName)};";
        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        return ParseInt(scalar);
    }

    private static string? SelectColumn(HashSet<string> columns, params string[] candidates)
    {
        foreach (var candidate in candidates)
        {
            var existing = columns.FirstOrDefault(c => c.Equals(candidate, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                return existing;
            }
        }

        return null;
    }

    private static bool ContainsAny(HashSet<string> columns, params string[] candidates)
    {
        return candidates.Any(candidate => columns.Contains(candidate));
    }

    private static string SqlText(string? columnName)
    {
        return string.IsNullOrWhiteSpace(columnName)
            ? "''"
            : $"CAST({QuoteIdentifier(columnName)} AS TEXT)";
    }

    private static string SqlInt(string? columnName)
    {
        return string.IsNullOrWhiteSpace(columnName)
            ? "0"
            : $"CAST({QuoteIdentifier(columnName)} AS INTEGER)";
    }

    private static string QuoteIdentifier(string identifier)
    {
        return $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }

    private static int ParseInt(object? value)
    {
        return value switch
        {
            null => 0,
            int intValue => intValue,
            long longValue => (int)Math.Clamp(longValue, int.MinValue, int.MaxValue),
            _ when int.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => 0
        };
    }

    private static void TryDeleteDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return;
        }

        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Best effort cleanup.
        }
    }

    private static bool IsLocalHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        var trimmed = host.Trim();
        if (trimmed.Equals(".", StringComparison.Ordinal) ||
            trimmed.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("::1", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var machineName = Environment.MachineName;
        if (trimmed.Equals(machineName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var shortHost = trimmed.Split('.', 2)[0];
        return shortHost.Equals(machineName, StringComparison.OrdinalIgnoreCase);
    }

    private void StopProcessInternal()
    {
        Interlocked.Increment(ref _streamSessionId);

        var streamTokenSource = _streamCancellationTokenSource;
        var streamTask = _streamTask;

        _streamCancellationTokenSource = null;
        _streamTask = null;

        try
        {
            if (streamTokenSource is not null)
            {
                streamTokenSource.Cancel();
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error while stopping ReportingEvents.log stream task.");
        }
        finally
        {
            _ = FinalizeBackgroundStopAsync(streamTask, streamTokenSource, "ReportingEvents.log stream");
            IsMonitoring = false;
        }
    }

    private bool CanUseDirectInstallProgressAccess(string? progressPath)
    {
        return !string.IsNullOrWhiteSpace(progressPath) &&
               FileTailReader.CanFollowDirectly(progressPath);
    }

    private void EnsureInstallProgressFollowStarted()
    {
        if (_installProgressTask is not null)
        {
            return;
        }

        if (!CanUseDirectInstallProgressAccess(_activeInstallProgressLogPath))
        {
            return;
        }

        var progressCancellationTokenSource = new CancellationTokenSource();
        _installProgressCancellationTokenSource = progressCancellationTokenSource;
        _installProgressTask = Task.Run(
            () => FollowInstallProgressAsync(progressCancellationTokenSource.Token),
            CancellationToken.None);
    }

    private async Task FinalizeBackgroundStopAsync(Task? task, CancellationTokenSource? cancellationTokenSource, string operationName)
    {
        try
        {
            if (task is not null)
            {
                await task;
            }
        }
        catch (OperationCanceledException)
        {
            // Expected while stopping monitoring work.
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error while finishing {OperationName}.", operationName);
        }
        finally
        {
            cancellationTokenSource?.Dispose();
        }
    }

    private void OnHostChanged(object? sender, string host)
    {
        if (_disposed)
        {
            return;
        }

        StopInstallStatusAutoRefresh();
        ResetStreamCheckpoint();
        _activeInstallHost = null;
        _activeInstallStatusPath = null;
        _activeInstallProgressLogPath = null;
        ClearInstallBusyState();
        ResetInstallProgressTracking(null);
        _demoInstallTaskStarted = false;
        ClearReportingEventsLoadingOverlay();
        InstallTaskState = "No install task started.";
        InstallTaskStatusText = "Task: Unknown";
        InstallTaskPhaseText = "Phase: unknown";
        InstallTaskDetail = string.Empty;
        InstallProgressEntries.Clear();

        CurrentHost = host;
        _entryBuffer.Clear();
        Entries.Clear();
        AvailableUpdates.Clear();
        VisibleAvailableUpdates.Clear();
        RegisteredUpdateProviders.Clear();
        UpdateHistoryEntries.Clear();
        RegisteredUpdateProvidersInfo = "Registered update providers: unknown";
        ResetRegisteredUpdateProvidersHealth();
        _registeredUpdateProvidersLoadedForCurrentHost = false;
        AutopatchRingText = "Unknown";
        ResetUsoDiagnosticsState();
        UsoDiagnosticsSourceText = string.IsNullOrWhiteSpace(host)
            ? "Current host source: not loaded."
            : $"Current host source: {host} (not loaded yet)";
        UpdateStatus = string.IsNullOrWhiteSpace(host)
            ? DisconnectedStatus
            : $"Host changed to '{host}'.";

        var selection = _targetHostService.CaptureSelection();
        _ = StartMonitoringInternalAsync(CancellationToken.None, fullReload: true);
        _ = EnsureRegisteredUpdateProvidersLoadedAsync(selection);
        _ = EnsureAutopatchRingLoadedAsync(selection);
    }

    private CancellationTokenSource CreateHostLinkedCancellation(HostSelection selection, CancellationToken cancellationToken)
    {
        return cancellationToken.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(selection.CancellationToken, cancellationToken)
            : CancellationTokenSource.CreateLinkedTokenSource(selection.CancellationToken);
    }

    private void EnsureCurrentSelection(HostSelection selection)
    {
        if (!_targetHostService.IsCurrent(selection))
        {
            throw new OperationCanceledException(selection.CancellationToken);
        }
    }

    private void ApplyDemoWindowsUpdateSnapshot(string host, bool resetInstallState)
    {
        var snapshot = _demoDataCatalog.CreateWindowsUpdateSnapshot(host);
        CurrentHost = _demoDataCatalog.NormalizeHost(host);

        ClearLog();
        AppendLines(snapshot.ReportingEventsLines);

        AvailableUpdates.Clear();
        foreach (var update in snapshot.AvailableUpdates)
        {
            AvailableUpdates.Add(new WindowsUpdateAvailableEntry(
                update.Title,
                update.Type,
                update.Status,
                update.IsInstalled,
                update.IsHidden,
                update.KbArticles,
                update.IsDownloaded,
                update.IsMandatory,
                update.EulaAccepted,
                update.Categories,
                update.Deadline,
                update.UpdateId,
                update.Revision));
        }

        RefreshVisibleAvailableUpdates();
        ApplyDemoRegisteredUpdateProviders(snapshot.Providers);
        ApplyDemoHistory(snapshot.HistoryEntries);
        LastAvailableUpdatesScanInfo = snapshot.LastScanInfo;
        if (resetInstallState || !_demoInstallTaskStarted)
        {
            InstallTaskState = snapshot.DefaultInstallTaskState;
            InstallTaskStatusText = snapshot.DefaultInstallTaskStatusText;
            InstallTaskPhaseText = snapshot.DefaultInstallTaskPhaseText;
            InstallTaskDetail = snapshot.DefaultInstallTaskDetail;
            IsInstallTaskRunning = snapshot.IsInstallTaskRunning;
            InstallProgressEntries.Clear();
            foreach (var line in snapshot.BaseInstallProgressLines)
            {
                InstallProgressEntries.Add(InstallProgressEntry.FromLogLine(line));
            }
        }
    }

    private void ApplyDemoRegisteredUpdateProviders(IReadOnlyList<DemoWindowsUpdateProviderItem> providers)
    {
        RegisteredUpdateProviders.Clear();
        foreach (var provider in providers)
        {
            RegisteredUpdateProviders.Add(new WindowsUpdateProviderEntry(
                provider.Name,
                provider.ServiceId,
                provider.IsDefault,
                provider.IsRegisteredWithAutomaticUpdates,
                provider.OffersWindowsUpdates,
                provider.IsManaged));
        }

        SetRegisteredUpdateProvidersHealth(RegisteredUpdateProviders.ToArray());
        RegisteredUpdateProvidersInfo = BuildRegisteredUpdateProvidersInfo(RegisteredUpdateProviders.ToArray());
    }

    private void ApplyDemoHistory(IReadOnlyList<DemoWindowsUpdateHistoryItem> historyEntries)
    {
        UpdateHistoryEntries.Clear();
        foreach (var entry in historyEntries)
        {
            UpdateHistoryEntries.Add(new WindowsUpdateHistoryEntry(
                entry.Date,
                entry.Operation,
                entry.Result,
                entry.HResult,
                entry.Title,
                entry.UpdateId,
                entry.Revision,
                entry.ClientApplicationId,
                entry.ServiceId,
                entry.PackageName));
        }
    }

    private void AppendLine(string line)
    {
        ForwardLogLineToHost(line);

        if (Application.Current.Dispatcher.CheckAccess())
        {
            AppendLineCore(line);
            return;
        }

        _ = Application.Current.Dispatcher.InvokeAsync(() => AppendLineCore(line));
    }

    private void SetReportingEventsLoadingOverlay(string message)
    {
        if (Application.Current.Dispatcher.CheckAccess())
        {
            ReportingEventsLoadingText = message;
            IsReportingEventsLoading = true;
            return;
        }

        _ = Application.Current.Dispatcher.InvokeAsync(() =>
        {
            ReportingEventsLoadingText = message;
            IsReportingEventsLoading = true;
        });
    }

    private void ClearReportingEventsLoadingOverlay()
    {
        if (Application.Current.Dispatcher.CheckAccess())
        {
            IsReportingEventsLoading = false;
            ReportingEventsLoadingText = "Loading ReportingEvents.log...";
            return;
        }

        _ = Application.Current.Dispatcher.InvokeAsync(() =>
        {
            IsReportingEventsLoading = false;
            ReportingEventsLoadingText = "Loading ReportingEvents.log...";
        });
    }

    private void AppendLines(IReadOnlyList<string> lines)
    {
        if (lines.Count == 0)
        {
            return;
        }

        foreach (var line in lines)
        {
            ForwardLogLineToHost(line);
        }

        if (Application.Current.Dispatcher.CheckAccess())
        {
            AppendLinesCore(lines);
            return;
        }

        _ = Application.Current.Dispatcher.InvokeAsync(() => AppendLinesCore(lines));
    }

    private void AppendLineCore(string line)
    {
        if (IsDebugLogLine(line))
        {
            return;
        }

        AppendEntryCore(ParseLine(line));
    }

    private void RefreshVisibleAvailableUpdates()
    {
        if (!Application.Current.Dispatcher.CheckAccess())
        {
            _ = Application.Current.Dispatcher.InvokeAsync(RefreshVisibleAvailableUpdates);
            return;
        }

        IEnumerable<WindowsUpdateAvailableEntry> updates = AvailableUpdates;
        if (SelectedAvailableUpdatesViewIndex == 0)
        {
            updates = updates.Where(update => update.IsAvailable);
        }

        VisibleAvailableUpdates.Clear();
        foreach (var update in updates)
        {
            VisibleAvailableUpdates.Add(update);
        }
    }

    private void AppendLinesCore(IReadOnlyList<string> lines)
    {
        foreach (var line in lines)
        {
            if (IsDebugLogLine(line))
            {
                continue;
            }

            AppendEntryCore(ParseLine(line));
        }
    }

    private void AppendExternalCommandDebug(string operation, ExternalCommandResult result)
    {
        AppendLine($"[WU][DEBUG] {operation}: exit={result.ExitCode} stdout={result.StdOut.Length} chars stderr={result.StdErr.Length} chars");

        var stdoutPreview = BuildDebugPreview(result.StdOut);
        if (!string.IsNullOrWhiteSpace(stdoutPreview))
        {
            AppendLine($"[WU][DEBUG] {operation}: stdout-preview={stdoutPreview}");
        }

        var stderrPreview = BuildDebugPreview(result.StdErr);
        if (!string.IsNullOrWhiteSpace(stderrPreview))
        {
            AppendLine($"[WU][DEBUG] {operation}: stderr-preview={stderrPreview}");
        }
    }

    private void AppendWinRtPayloadDebug(string operation, string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            JsonElement snapshot = default;
            var hasSnapshot = root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("ManagerSnapshot", out snapshot) &&
                snapshot.ValueKind == JsonValueKind.Object;

            var updatesCount = CountArrayProperty(snapshot, "Updates");
            var softwareUpdatesCount = CountArrayProperty(snapshot, "SoftwareUpdates");
            var snapshotCompletedCount = CountArrayProperty(snapshot, "MostRecentCompletedUpdates");
            var rootCompletedCount = CountArrayProperty(root, "MostRecentCompletedUpdates");
            var applicableUpdateCount = GetIntProperty(snapshot, "ApplicableUpdateCount");
            var applicableSoftwareUpdateCount = GetIntProperty(snapshot, "ApplicableSoftwareUpdateCount");
            var mostRecentCompletedCount = GetIntProperty(snapshot, "MostRecentCompletedCount");
            var lastSuccessfulScanTimestamp = GetNestedStringProperty(snapshot, "ManagerStatus", "LastSuccessfulScanTimestamp");

            AppendLine(
                $"[WU][DEBUG] {operation}: hasSnapshot={hasSnapshot} payloadUpdates={updatesCount} payloadSoftwareUpdates={softwareUpdatesCount} payloadCompleted(root={rootCompletedCount},snapshot={snapshotCompletedCount}) summaryUpdates={applicableUpdateCount} summarySoftware={applicableSoftwareUpdateCount} summaryCompleted={mostRecentCompletedCount} lastScan={lastSuccessfulScanTimestamp ?? "<null>"}");
        }
        catch (Exception ex)
        {
            AppendLine($"[WU][DEBUG] {operation}: payload inspection failed: {ex.Message}");
        }
    }

    private void AppendCollectionDebug<T>(string operation, IReadOnlyList<T> entries)
    {
        AppendLine($"[WU][DEBUG] {operation}: parsed entries={entries.Count}");
        foreach (var preview in entries.Take(3).Select(FormatCollectionDebugEntry))
        {
            if (!string.IsNullOrWhiteSpace(preview))
            {
                AppendLine($"[WU][DEBUG] {operation}: item={preview}");
            }
        }
    }

    private static string FormatCollectionDebugEntry<T>(T entry)
    {
        return entry switch
        {
            WindowsUpdateAvailableEntry update => $"Title='{update.Title}' UpdateId='{update.UpdateId}' Revision={update.Revision} Status='{update.Status}' Type='{update.Type}'",
            WindowsUpdateHistoryEntry history => $"Title='{history.Title}' UpdateId='{history.UpdateId}' Revision={history.Revision} Operation='{history.Operation}' Date='{history.Date}'",
            _ => entry?.ToString() ?? string.Empty
        };
    }

    private static int CountArrayProperty(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.Array)
        {
            return 0;
        }

        return property.GetArrayLength();
    }

    private static int GetIntProperty(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.Number ||
            !property.TryGetInt32(out var value))
        {
            return 0;
        }

        return value;
    }

    private static string? GetNestedStringProperty(JsonElement element, string objectPropertyName, string valuePropertyName)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(objectPropertyName, out var nestedElement) ||
            nestedElement.ValueKind != JsonValueKind.Object ||
            !nestedElement.TryGetProperty(valuePropertyName, out var valueElement) ||
            valueElement.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return valueElement.GetString();
    }

    private static string BuildDebugPreview(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var normalized = Regex.Replace(text.Trim(), @"\s+", " ");
        const int maxLength = 320;
        if (normalized.Length <= maxLength)
        {
            return normalized;
        }

        return normalized[..maxLength] + "...";
    }

    private void AppendEntryCore(ReportingEventsLogEntry entry)
    {
        var trimmedCount = _entryBuffer.Add(entry);

        if (ShouldUseSlidingLogWindow())
        {
            Entries.Add(entry);
            TrimVisibleEntriesToWindow();
            return;
        }

        if (trimmedCount > 0)
        {
            RefreshVisibleEntries();
            return;
        }

        Entries.Add(entry);
    }

    private void TrimVisibleEntriesToWindow()
    {
        var maxVisibleRows = GetVisibleLogWindowSize();
        while (Entries.Count > maxVisibleRows)
        {
            Entries.RemoveAt(0);
        }
    }

    private int GetVisibleLogWindowSize()
    {
        if (!ShouldUseSlidingLogWindow())
        {
            return MaxBufferedRows;
        }

        return Math.Clamp(TailLineCount, MinTailLineCount, MaxTailLineCount);
    }

    private bool ShouldUseSlidingLogWindow()
    {
        return string.IsNullOrWhiteSpace(HighlightedUpdateId);
    }

    private void RefreshVisibleEntries()
    {
        if (!Application.Current.Dispatcher.CheckAccess())
        {
            _ = Application.Current.Dispatcher.InvokeAsync(RefreshVisibleEntries);
            return;
        }

        var maxVisibleRows = GetVisibleLogWindowSize();
        Entries.Clear();
        foreach (var entry in _entryBuffer.GetWindow(maxVisibleRows))
        {
            Entries.Add(entry);
        }
    }

    private static ReportingEventsLogEntry ParseLine(string line)
    {
        var tokens = TokenRegex.Matches(line).Select(m => m.Value).ToArray();
        if (tokens.Length < 11)
        {
            return new ReportingEventsLogEntry(
                EventInstanceId: string.Empty,
                Timestamp: string.Empty,
                TimestampDisplay: string.Empty,
                TimestampSortKey: 0,
                NamespaceId: string.Empty,
                EventId: string.Empty,
                AgentEvent: string.Empty,
                SourceId: string.Empty,
                UpdateId: string.Empty,
                Revision: string.Empty,
                Win32Hresult: string.Empty,
                AppName: string.Empty,
                Result: string.Empty,
                Area: string.Empty,
                Operation: string.Empty,
                Message: line,
                CorrelationToken: string.Empty,
                RawLine: line);
        }

        var hasCorrelation = tokens.Length > 14 && CorrelationTokenRegex.IsMatch(tokens[^1]);
        var correlationToken = hasCorrelation ? tokens[^1] : string.Empty;

        var messageStartIndex = 14;
        var messageEndIndex = hasCorrelation ? tokens.Length - 1 : tokens.Length;
        var message = messageStartIndex < messageEndIndex
            ? string.Join(' ', tokens[messageStartIndex..messageEndIndex])
            : string.Empty;
        var rawTimestamp = $"{GetToken(tokens, 1)} {GetToken(tokens, 2)}".Trim();
        var (timestampDisplay, timestampSortKey) = FormatTimestamp(rawTimestamp);

        return new ReportingEventsLogEntry(
            EventInstanceId: GetToken(tokens, 0),
            Timestamp: rawTimestamp,
            TimestampDisplay: timestampDisplay,
            TimestampSortKey: timestampSortKey,
            NamespaceId: GetToken(tokens, 3),
            EventId: GetToken(tokens, 4),
            AgentEvent: GetToken(tokens, 5).Trim('[', ']'),
            SourceId: GetToken(tokens, 6),
            UpdateId: GetToken(tokens, 7),
            Revision: GetToken(tokens, 8),
            Win32Hresult: GetToken(tokens, 9),
            AppName: GetToken(tokens, 10),
            Result: GetToken(tokens, 11),
            Area: GetToken(tokens, 12),
            Operation: GetToken(tokens, 13),
            Message: message,
            CorrelationToken: correlationToken,
            RawLine: line);
    }

    private static (string Display, long SortKey) FormatTimestamp(string rawTimestamp)
    {
        if (string.IsNullOrWhiteSpace(rawTimestamp))
        {
            return (string.Empty, 0);
        }

        if (DateTimeOffset.TryParse(rawTimestamp, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var dateTimeOffset))
        {
            return (dateTimeOffset.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), dateTimeOffset.UtcTicks);
        }

        if (DateTime.TryParse(rawTimestamp, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var dateTime))
        {
            return (dateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), dateTime.Ticks);
        }

        if (TryParseTimestampWithFraction(rawTimestamp, out var preciseTimestamp))
        {
            return (preciseTimestamp.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), preciseTimestamp.Ticks);
        }

        return (TrimTimestampToSeconds(rawTimestamp), ExtractFallbackSortKey(rawTimestamp));
    }

    private static string TrimTimestampToSeconds(string rawTimestamp)
    {
        var trimmed = rawTimestamp.Trim();
        var dateTimeMatch = Regex.Match(trimmed, @"\d{4}-\d{2}-\d{2}\s+\d{2}:\d{2}:\d{2}");
        if (dateTimeMatch.Success)
        {
            return dateTimeMatch.Value;
        }

        var timeMatch = Regex.Match(trimmed, @"\d{2}:\d{2}:\d{2}");
        if (timeMatch.Success)
        {
            return timeMatch.Value;
        }

        return trimmed;
    }

    private static bool TryParseTimestampWithFraction(string rawTimestamp, out DateTime preciseTimestamp)
    {
        var match = DateTimeWithFractionRegex.Match(rawTimestamp.Trim());
        if (!match.Success)
        {
            preciseTimestamp = default;
            return false;
        }

        var datePart = match.Groups["date"].Value;
        var timePart = match.Groups["time"].Value;
        if (!DateTime.TryParseExact(
                $"{datePart} {timePart}",
                "yyyy-MM-dd HH:mm:ss",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var secondResolutionTimestamp))
        {
            preciseTimestamp = default;
            return false;
        }

        var fraction = match.Groups["fraction"].Value;
        if (!string.IsNullOrWhiteSpace(fraction))
        {
            var paddedFraction = fraction.PadRight(7, '0');
            if (long.TryParse(paddedFraction, NumberStyles.Integer, CultureInfo.InvariantCulture, out var fractionTicks))
            {
                preciseTimestamp = secondResolutionTimestamp.AddTicks(fractionTicks);
                return true;
            }
        }

        preciseTimestamp = secondResolutionTimestamp;
        return true;
    }

    private static long ExtractFallbackSortKey(string rawTimestamp)
    {
        if (!TryParseTimestampWithFraction(rawTimestamp, out var preciseTimestamp))
        {
            return 0;
        }

        return preciseTimestamp.Ticks;
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
        _hostStatusLogSink.Append($"[Windows Update Agent] {normalized}");
    }

    private void ForwardLogLineToHost(string line)
    {
        if (_hostStatusLogSink is null || string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        var normalized = line.Trim();
        if (!IsDebugLogLine(normalized))
        {
            return;
        }

        if (string.Equals(_lastForwardedLogLine, normalized, StringComparison.Ordinal))
        {
            return;
        }

        _lastForwardedLogLine = normalized;
        _hostStatusLogSink.Append($"[Windows Update Agent] {normalized}");
    }

    private string? BeginBusyState(string shortStatus, IReadOnlyList<string>? tasks = null)
    {
        if (_hostBusyStateSink is null)
        {
            return null;
        }

        var ownerId = $"windows-update-agent:{GetHashCode():X}:{Interlocked.Increment(ref _busyOperationSequence)}";
        if (!string.IsNullOrWhiteSpace(_activeBusyOwnerId))
        {
            _hostBusyStateSink.ClearBusyState(_activeBusyOwnerId);
        }

        _activeBusyOwnerId = ownerId;
        var normalizedStatus = string.IsNullOrWhiteSpace(shortStatus) ? "Windows Update" : shortStatus.Trim();
        var normalizedTasks = (tasks ?? [])
            .Where(static task => !string.IsNullOrWhiteSpace(task))
            .Select(static task => task.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        _hostBusyStateSink.SetBusyState(ownerId, normalizedStatus, normalizedTasks);
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

    private void EnsureGlobalInstallBusyState(string host, string phase, int total, int completed, string currentTitle)
    {
        if (_hostBusyStateSink is null)
        {
            _activeInstallBusyOwnerId = null;
            return;
        }

        _activeInstallBusyOwnerId ??= $"windows-update-agent-install:{GetHashCode():X}";
        var normalizedHost = string.IsNullOrWhiteSpace(host) ? "current host" : host.Trim();
        var normalizedPhase = string.IsNullOrWhiteSpace(phase) ? "running" : phase.Trim();
        var tasks = new List<string>
        {
            $"Host: {normalizedHost}",
            $"Phase: {normalizedPhase}"
        };

        if (total > 0)
        {
            tasks.Add($"Progress: {completed}/{total}");
        }

        if (!string.IsNullOrWhiteSpace(currentTitle))
        {
            tasks.Add($"Current: {currentTitle.Trim()}");
        }

        _hostBusyStateSink.SetBusyState(
            _activeInstallBusyOwnerId,
            $"Installing Windows updates on '{normalizedHost}'",
            tasks);
    }

    private void SyncGlobalInstallBusyState(string host, string phase, int total, int completed, string currentTitle)
    {
        if (IsInstallTaskRunning)
        {
            EnsureGlobalInstallBusyState(host, phase, total, completed, currentTitle);
        }
        else
        {
            ClearInstallBusyState();
        }
    }

    private void ClearInstallBusyState()
    {
        if (_hostBusyStateSink is null || string.IsNullOrWhiteSpace(_activeInstallBusyOwnerId))
        {
            _activeInstallBusyOwnerId = null;
            return;
        }

        _hostBusyStateSink.ClearBusyState(_activeInstallBusyOwnerId);
        _activeInstallBusyOwnerId = null;
    }

    private static bool IsDebugLogLine(string line)
    {
        return line.Contains("[DEBUG]", StringComparison.Ordinal);
    }

    private static AvailableUpdatesPayload ParseAvailableUpdatesPayload(string json, IReadOnlyList<WindowsUpdateProviderEntry>? providersFallback = null)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new AvailableUpdatesPayload(0, [], providersFallback ?? [], string.Empty, "unknown");
        }

        var payload = JsonSerializer.Deserialize<AvailableUpdatesWirePayload>(json, JsonOptions);
        if (payload is null)
        {
            return new AvailableUpdatesPayload(0, [], providersFallback ?? [], string.Empty, "unknown");
        }

        var updates = payload.Updates?
            .Select(update => new WindowsUpdateAvailableEntry(
                title: update.Title ?? string.Empty,
                type: update.Type ?? string.Empty,
                status: update.Status ?? string.Empty,
                isInstalled: update.IsInstalled,
                isHidden: update.IsHidden,
                kbArticles: update.KbArticles ?? string.Empty,
                isDownloaded: update.IsDownloaded,
                isMandatory: update.IsMandatory,
                eulaAccepted: update.EulaAccepted,
                categories: update.Categories ?? string.Empty,
                deadline: update.Deadline ?? string.Empty,
                updateId: update.UpdateId ?? string.Empty,
                revision: update.Revision))
            .ToArray() ?? [];

        var providers = payload.Providers?
            .Select(provider => new WindowsUpdateProviderEntry(
                provider.Name ?? string.Empty,
                provider.ServiceId ?? string.Empty,
                provider.IsDefault,
                provider.IsRegisteredWithAU,
                provider.OffersWindowsUpdates,
                provider.IsManaged))
            .ToArray() ?? [];

        if (providers.Length == 0 && providersFallback is { Count: > 0 })
        {
            providers = providersFallback.ToArray();
        }

        var count = payload.UpdateCount > 0 ? payload.UpdateCount : updates.Length;
        return new AvailableUpdatesPayload(
            count,
            updates,
            providers,
            payload.LastSearchSuccessDate ?? string.Empty,
            payload.SearchSource ?? "unknown");
    }

    private static string BuildRegisteredUpdateProvidersInfo(IReadOnlyList<WindowsUpdateProviderEntry> providers)
    {
        if (providers.Count == 0)
        {
            return "Registered update providers: none found.";
        }

        var defaultProviders = providers
            .Where(provider => provider.IsDefault)
            .Select(provider => string.IsNullOrWhiteSpace(provider.Name) ? provider.ServiceId : provider.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var defaultText = defaultProviders.Length == 0
            ? "not reported"
            : string.Join(", ", defaultProviders);

        return $"Registered update providers: {providers.Count} (default: {defaultText}).";
    }

    private void SetRegisteredUpdateProvidersHealth(IReadOnlyList<WindowsUpdateProviderEntry> providers)
    {
        if (providers.Count == 0)
        {
            RegisteredUpdateProvidersHealthText = "Health: warning";
            RegisteredUpdateProvidersHealthSummaryText = "No registered update providers were returned. Microsoft Update default is not available.";
            RegisteredUpdateProvidersHealthColorHex = "#B07D00";
            return;
        }

        var microsoftUpdateProviders = providers
            .Where(IsMicrosoftUpdateProvider)
            .ToArray();

        var microsoftUpdateDefaultProviders = microsoftUpdateProviders
            .Where(provider => provider.IsDefault)
            .Select(provider => string.IsNullOrWhiteSpace(provider.Name) ? provider.ServiceId : provider.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (microsoftUpdateDefaultProviders.Length > 0)
        {
            RegisteredUpdateProvidersHealthText = "Health: healthy";
            RegisteredUpdateProvidersHealthSummaryText = $"Microsoft Update default provider detected: {string.Join(", ", microsoftUpdateDefaultProviders)}.";
            RegisteredUpdateProvidersHealthColorHex = "#1A7F37";
            return;
        }

        if (microsoftUpdateProviders.Length > 0)
        {
            var providerNames = microsoftUpdateProviders
                .Select(provider => string.IsNullOrWhiteSpace(provider.Name) ? provider.ServiceId : provider.Name)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            RegisteredUpdateProvidersHealthText = "Health: warning";
            RegisteredUpdateProvidersHealthSummaryText = $"Microsoft Update is registered but not the default provider: {string.Join(", ", providerNames)}.";
            RegisteredUpdateProvidersHealthColorHex = "#B07D00";
            return;
        }

        var defaultProviders = providers
            .Where(provider => provider.IsDefault)
            .Select(provider => string.IsNullOrWhiteSpace(provider.Name) ? provider.ServiceId : provider.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        RegisteredUpdateProvidersHealthText = "Health: warning";
        RegisteredUpdateProvidersHealthSummaryText = defaultProviders.Length == 0
            ? "No default update provider was reported."
            : $"Default update provider is not Microsoft Update: {string.Join(", ", defaultProviders)}.";
        RegisteredUpdateProvidersHealthColorHex = "#B07D00";
    }

    private void ResetRegisteredUpdateProvidersHealth()
    {
        RegisteredUpdateProvidersHealthText = "Health: unknown";
        RegisteredUpdateProvidersHealthSummaryText = "Microsoft Update default has not been checked yet.";
        RegisteredUpdateProvidersHealthColorHex = "#8A8A8A";
    }

    private static bool IsMicrosoftUpdateProvider(WindowsUpdateProviderEntry provider)
    {
        if (string.Equals(provider.ServiceId, "7971f918-a847-4430-9279-4a52d1efe18d", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return provider.Name.Contains("Microsoft Update", StringComparison.OrdinalIgnoreCase) ||
               provider.ServiceId.Contains("Microsoft Update", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<WindowsUpdateProviderEntry> ParseRegisteredUpdateProvidersPayload(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        var payload = JsonSerializer.Deserialize<AvailableUpdatesWirePayload>(json, JsonOptions);
        if (payload?.Providers is null)
        {
            return [];
        }

        return payload.Providers
            .Select(provider => new WindowsUpdateProviderEntry(
                provider.Name ?? string.Empty,
                provider.ServiceId ?? string.Empty,
                provider.IsDefault,
                provider.IsRegisteredWithAU,
                provider.OffersWindowsUpdates,
                provider.IsManaged))
            .ToArray();
    }

    private static AvailableUpdatesPayload ParseWinRtAvailableUpdatesPayload(string json, IReadOnlyList<WindowsUpdateProviderEntry> providers)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new AvailableUpdatesPayload(0, [], providers, string.Empty, "winrt inventory");
        }

        var payload = JsonSerializer.Deserialize<WinRtInventoryWirePayload>(json, JsonOptions);
        var snapshot = payload?.ManagerSnapshot;
        if (snapshot is null)
        {
            return new AvailableUpdatesPayload(0, [], providers, string.Empty, "winrt inventory");
        }

        var detailByUpdateId = snapshot.Updates?
            .Where(update => !string.IsNullOrWhiteSpace(update.UpdateId))
            .GroupBy(update => update.UpdateId!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, WinRtApplicableUpdateWireEntry>(StringComparer.OrdinalIgnoreCase);

        var candidateUpdates = new List<WinRtSoftwareUpdateWireEntry>();
        var seenUpdateIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddCandidate(WinRtSoftwareUpdateWireEntry update)
        {
            var updateId = update.UpdateId ?? string.Empty;
            if (!seenUpdateIds.Add(updateId))
            {
                return;
            }

            candidateUpdates.Add(update);
        }

        if (snapshot.SoftwareUpdates is { Count: > 0 })
        {
            foreach (var update in snapshot.SoftwareUpdates.Where(update => update is not null))
            {
                AddCandidate(update!);
            }
        }

        if (snapshot.Updates is { Count: > 0 })
        {
            foreach (var update in snapshot.Updates.Where(update => update is not null))
            {
                AddCandidate(new WinRtSoftwareUpdateWireEntry
                {
                    Title = update!.Title,
                    UpdateId = update.UpdateId,
                    CurrentAction = update.CurrentAction
                });
            }
        }

        var updates = candidateUpdates
            .Where(update => update is not null)
            .Select(update =>
            {
                var rawUpdateId = update!.UpdateId ?? string.Empty;
                SplitUpdateIdentity(rawUpdateId, out var normalizedUpdateId, out var revision);

                detailByUpdateId.TryGetValue(rawUpdateId, out var detail);
                var type = detail?.IsDriver == true ? "Driver" : "Software";
                var categoryParts = new List<string> { type };
                if (detail?.IsSecurity == true)
                {
                    categoryParts.Add("Security");
                }

                if (detail?.IsFeatureUpdate == true)
                {
                    categoryParts.Add("Feature Update");
                }

                if (detail?.IsMandatory == true)
                {
                    categoryParts.Add("Mandatory");
                }

                var deadline = FormatDate(detail?.Deadline);
                var currentAction = update.CurrentAction ?? string.Empty;
                var isDownloaded = !string.Equals(currentAction, "Download", StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(currentAction);

                return new WindowsUpdateAvailableEntry(
                    title: update.Title ?? string.Empty,
                    type: type,
                    status: string.IsNullOrWhiteSpace(currentAction) ? "Applicable" : currentAction,
                    isInstalled: false,
                    isHidden: false,
                    kbArticles: string.Empty,
                    isDownloaded: isDownloaded,
                    isMandatory: detail?.IsMandatory == true,
                    eulaAccepted: true,
                    categories: string.Join("; ", categoryParts.Distinct(StringComparer.OrdinalIgnoreCase)),
                    deadline: deadline,
                    updateId: normalizedUpdateId,
                    revision: revision);
            })
            .ToArray() ?? [];

        var lastSearchSuccessDate = snapshot.ManagerStatus?.LastSuccessfulScanTimestamp ?? string.Empty;
        return new AvailableUpdatesPayload(
            updates.Length,
            updates,
            providers,
            lastSearchSuccessDate,
            "winrt inventory");
    }

    private static UpdateHistoryPayload ParseWinRtCompletedUpdatesPayload(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new UpdateHistoryPayload(0, 0, []);
        }

        var payload = JsonSerializer.Deserialize<WinRtInventoryWirePayload>(json, JsonOptions);
        var completedUpdates = payload?.MostRecentCompletedUpdates
            ?? payload?.ManagerSnapshot?.MostRecentCompletedUpdates
            ?? [];

        var entries = completedUpdates
            .Where(update => update is not null)
            .Select(update =>
            {
                SplitUpdateIdentity(update!.UpdateId, out var updateId, out var revision);
                return new WindowsUpdateHistoryEntry(
                    Date: FormatDate(update.Timestamp),
                    Operation: update.Operation ?? string.Empty,
                    Result: "Completed",
                    HResult: string.Empty,
                    Title: update.Title ?? string.Empty,
                    UpdateId: updateId,
                    Revision: revision,
                    ClientApplicationId: string.Empty,
                    ServiceId: update.ProviderId ?? string.Empty,
                    PackageName: string.Empty);
            })
            .ToArray();

        return new UpdateHistoryPayload(entries.Length, entries.Length, entries);
    }

    private static string BuildLastScanInfo(string? rawLastScanDate)
    {
        if (string.IsNullOrWhiteSpace(rawLastScanDate))
        {
            return "Last scan: unknown";
        }

        var formatted = FormatDate(rawLastScanDate);
        if (string.IsNullOrWhiteSpace(formatted))
        {
            return "Last scan: unknown";
        }

        return $"Last successful scan: {formatted}";
    }

    private async Task<bool> SupportsWinRtUpdateStackAsync(string host, bool useLocalAccess, CancellationToken cancellationToken)
    {
        try
        {
            var script = BuildPowerShellScriptForHost(host, useLocalAccess, BuildUpdateStackCapabilityQueryScriptBody());
            var execution = await RunPowershellAsync(script, cancellationToken);
            if (execution.ExitCode != 0)
            {
                return false;
            }

            var output = (execution.StdOut ?? string.Empty).Trim();
            return bool.TryParse(output, out var supported) && supported;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to determine whether the WinRT update stack is supported on '{Host}'.", host);
            return false;
        }
    }

    private async Task<bool> ShouldUseWinRtUpdateStackAsync(string host, bool useLocalAccess, CancellationToken cancellationToken)
    {
        if (!string.Equals(SelectedUpdateApi, UpdateApiWinRt, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return await SupportsWinRtUpdateStackAsync(host, useLocalAccess, cancellationToken);
    }

    private static string BuildUpdateStackCapabilityQueryScriptBody()
    {
        return
            "$os = Get-CimInstance -ClassName Win32_OperatingSystem -ErrorAction Stop;" +
            "$buildNumber = 0;" +
            "try { $buildNumber = [int]$os.BuildNumber } catch { $buildNumber = 0 };" +
            "Write-Output ([bool]($buildNumber -ge 22000));";
    }

    private static string BuildAvailableUpdatesLegacyScriptBody(bool useCachedAvailableUpdates)
    {
        var onlineValue = useCachedAvailableUpdates ? "$false" : "$true";
        var searchSource = useCachedAvailableUpdates ? "wua com cache" : "wua com online";
        return
            "$ErrorActionPreference='Stop';" +
            "$session = New-Object -ComObject Microsoft.Update.Session;" +
            "$searcher = $session.CreateUpdateSearcher();" +
            "$searcher.Online = " + onlineValue + ";" +
            "$criteria = \"(IsInstalled=0 and Type='Software') or (IsInstalled=0 and Type='Driver')\";" +
            "$result = $searcher.Search($criteria);" +
            "$updates = @();" +
            "foreach ($candidate in @($result.Updates)) {" +
            "  if ($null -eq $candidate) { continue };" +
            "  $title = '';" +
            "  try { $title = [string]$candidate.Title } catch { };" +
            "  $updateId = '';" +
            "  $revision = 0;" +
            "  try { $updateId = [string]$candidate.Identity.UpdateID; $revision = [int]$candidate.Identity.RevisionNumber } catch { };" +
            "  $normalizedUpdateId = if ([string]::IsNullOrWhiteSpace($updateId)) { '' } elseif ($updateId.Contains(':')) { $updateId } elseif ($revision -gt 0) { $updateId + ':' + [string]$revision } else { $updateId };" +
            "  $typeText = 'Software';" +
            "  try { $typeText = [string]$candidate.Type } catch { };" +
            "  $isDriver = $false;" +
            "  if ($typeText -match 'Driver') { $isDriver = $true };" +
            "  $status = 'Applicable';" +
            "  try { if ([bool]$candidate.IsDownloaded) { $status = 'Downloaded' } } catch { };" +
            "  $isInstalled = $false;" +
            "  try { $isInstalled = [bool]$candidate.IsInstalled } catch { };" +
            "  $isHidden = $false;" +
            "  try { $isHidden = [bool]$candidate.IsHidden } catch { };" +
            "  $isDownloaded = $false;" +
            "  try { $isDownloaded = [bool]$candidate.IsDownloaded } catch { };" +
            "  $isMandatory = $false;" +
            "  try { $isMandatory = [bool]$candidate.IsMandatory } catch { };" +
            "  $eulaAccepted = $true;" +
            "  try { $eulaAccepted = [bool]$candidate.EulaAccepted } catch { };" +
            "  $kbArticles = '';" +
            "  try { $kbArticles = @($candidate.KBArticleIDs) -join ', ' } catch { };" +
            "  $categories = '';" +
            "  try { $categories = @($candidate.Categories | ForEach-Object { $_.Name }) -join '; ' } catch { };" +
            "  $finalType = 'Software';" +
            "  if ($isDriver) { $finalType = 'Driver' };" +
            "  $updates += [PSCustomObject]@{ Title = $title; Type = $finalType; Status = $status; IsInstalled = $isInstalled; IsHidden = $isHidden; KbArticles = $kbArticles; IsDownloaded = $isDownloaded; IsMandatory = $isMandatory; EulaAccepted = $eulaAccepted; Categories = $categories; Deadline = ''; UpdateId = $normalizedUpdateId; Revision = $revision };" +
            "};" +
            "$payload = [PSCustomObject]@{ UpdateCount = $updates.Count; Updates = $updates; Providers = @(); LastSearchSuccessDate = [DateTime]::UtcNow.ToString('o'); SearchSource = '" + searchSource + "' };" +
            "$payload | ConvertTo-Json -Depth 6;";
    }

    private static string BuildUninstallHistoryUpdateScriptBody(string updateId, int revision, string title, string packageName)
    {
        var escapedUpdateId = EscapePowerShellSingleQuotedString(updateId);
        var escapedTitle = EscapePowerShellSingleQuotedString(title);
        var escapedPackageName = EscapePowerShellSingleQuotedString(packageName);
        var revisionSuffix = revision > 0
            ? " Revision=" + revision.ToString(CultureInfo.InvariantCulture)
            : string.Empty;
        return
            "$updateId='" + escapedUpdateId + "';" +
            "$revision=" + revision.ToString(CultureInfo.InvariantCulture) + ";" +
            "$title='" + escapedTitle + "';" +
            "$packageName='" + escapedPackageName + "';" +
            "$result = [ordered]@{ Success=$false; RebootRequired=$false; Message=''; UpdateId=$updateId; Revision=$revision; Title=$title; PackageName=$packageName; HResult='' };" +
            "try {" +
            "  function Get-PackagePatterns {" +
            "    param([string]$PackageName,[string]$Title);" +
            "    $patterns = New-Object System.Collections.Generic.List[string];" +
            "    if (-not [string]::IsNullOrWhiteSpace($PackageName)) { [void]$patterns.Add('^' + [regex]::Escape($PackageName.Trim()) + '$') };" +
            "    if ($Title -match '\\bKB(?<kb>\\d{6,7})\\b') { [void]$patterns.Add('^Package_for_KB' + $matches.kb + '.*$') };" +
            "    if ($Title -match 'RollupFix|Cumulative Update') { [void]$patterns.Add('^Package_for_RollupFix.*$') };" +
            "    if ($Title -match 'DotNetRollup') { [void]$patterns.Add('^Package_for_DotNetRollup.*$') };" +
            "    return $patterns;" +
            "  };" +
            "  function TryUninstallPackage {" +
            "    param([string]$PackageName,[string]$Title);" +
            "    $patterns = Get-PackagePatterns -PackageName $PackageName -Title $Title;" +
            "    if ($patterns.Count -eq 0) { return $false };" +
            "    try { $installedPackages = @(Get-WindowsPackage -Online -ErrorAction Stop | Where-Object { $_.PackageState -eq 'Installed' -and -not [string]::IsNullOrWhiteSpace($_.PackageName) }) } catch { return $false };" +
            "    foreach ($pattern in $patterns) {" +
            "      $matches = @($installedPackages | Where-Object { $_.PackageName -match $pattern });" +
            "      if ($matches.Count -eq 0) { continue };" +
            "      $target = $matches | Sort-Object @{Expression='InstallTime';Descending=$true}, @{Expression='PackageName';Descending=$true} | Select-Object -First 1;" +
            "      if ($null -eq $target) { continue };" +
            "      try {" +
            "        $removeOutput = & dism.exe /Online /English /Remove-Package /PackageName:$($target.PackageName) /NoRestart /Quiet 2>&1;" +
            "        $removeExitCode = [int]$LASTEXITCODE;" +
            "        if ($removeExitCode -eq 0 -or $removeExitCode -eq 3010) {" +
            "          $result.Success = $true;" +
            "          $result.RebootRequired = ($removeExitCode -eq 3010);" +
            "          $result.Message = 'Package uninstall finished for ' + $target.PackageName + '.';" +
            "          if ($result.RebootRequired) { $result.Message += ' Reboot required.' }" +
            "          return $true;" +
            "        }" +
            "        $trimmedOutput = (($removeOutput | Out-String).Trim());" +
            "        if ([string]::IsNullOrWhiteSpace($trimmedOutput)) { $trimmedOutput = 'ExitCode=' + $removeExitCode }" +
            "        $result.Message = ('Package uninstall failed for ' + $target.PackageName + ': ' + $trimmedOutput);" +
            "      } catch {" +
            "        $result.Message = $_.Exception.Message;" +
            "      }" +
            "    };" +
            "    return $false;" +
            "  };" +
            "  if (TryUninstallPackage -PackageName $packageName -Title $title) { }" +
            "  elseif (-not [string]::IsNullOrWhiteSpace($updateId)) {" +
            "    $session = New-Object -ComObject Microsoft.Update.Session;" +
            "    $searcher = $session.CreateUpdateSearcher();" +
            "    $searcher.Online = $false;" +
            "    $criteria = \"IsInstalled=1 and UpdateID='$updateId'\";" +
            "    if ($revision -gt 0) { $criteria += ' and RevisionNumber=' + $revision }" +
            "    $searchResult = $searcher.Search($criteria);" +
            "    $matches = @($searchResult.Updates);" +
            "    if ($matches.Count -eq 0) { throw ('Installed update not found for UpdateID=' + $updateId + '" + EscapePowerShellSingleQuotedString(revisionSuffix) + " + '.') };" +
            "    $target = $matches | Where-Object { $null -ne $_ -and ($_.IsUninstallable -eq $true) } | Select-Object -First 1;" +
            "    if ($null -eq $target) { throw ('The selected update is not uninstallable: ' + $title) };" +
            "    $collection = New-Object -ComObject Microsoft.Update.UpdateColl;" +
            "    [void]$collection.Add($target);" +
            "    $installer = $session.CreateUpdateInstaller();" +
            "    $installer.Updates = $collection;" +
            "    try { $installer.ForceQuiet = $true } catch { };" +
            "    try { $installer.AllowSourcePrompts = $false } catch { };" +
            "    $installResult = $installer.Uninstall();" +
            "    $resultCode = [int]$installResult.ResultCode;" +
            "    $hresult = '';" +
            "    try { $hresult = ('0x{0:X8}' -f ([int]$installResult.HResult)) } catch { };" +
            "    $result.Success = ($resultCode -eq 2 -or $resultCode -eq 3);" +
            "    $result.RebootRequired = [bool]$installResult.RebootRequired;" +
            "    $result.HResult = $hresult;" +
            "    $result.Message = 'Uninstall finished. ResultCode=' + $resultCode + ' HResult=' + $hresult + ' RebootRequired=' + [bool]$installResult.RebootRequired + '.';" +
            "    if ($result.RebootRequired) { $result.Message += ' Reboot required.' }" +
            "  } else {" +
            "    throw 'The selected history entry does not expose a usable update or package identity.'" +
            "  }" +
            "} catch {" +
            "  $result.Message = $_.Exception.Message;" +
            "}" +
            "$result | ConvertTo-Json -Depth 4 -Compress;";
    }

    private static bool TryParseUninstallHistoryUpdateResult(string rawJson, out UninstallHistoryUpdateResult result, out string errorMessage)
    {
        result = new UninstallHistoryUpdateResult(false, false, string.Empty, string.Empty, 0, string.Empty);
        errorMessage = string.Empty;

        if (string.IsNullOrWhiteSpace(rawJson))
        {
            errorMessage = "Windows Update uninstall script returned no output.";
            return false;
        }

        try
        {
            var payload = JsonSerializer.Deserialize<UninstallHistoryUpdateResult>(rawJson, JsonOptions);
            if (payload is null)
            {
                errorMessage = "Windows Update uninstall script returned an empty payload.";
                return false;
            }

            result = payload with
            {
                Message = string.IsNullOrWhiteSpace(payload.Message)
                    ? "Update uninstall completed."
                    : payload.Message.Trim()
            };
            return true;
        }
        catch (JsonException ex)
        {
            errorMessage = $"Failed to parse Windows Update uninstall result: {ex.Message}";
            return false;
        }
    }

    private static string FormatDate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var value = raw.Trim();
        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsedTimestamp))
        {
            return parsedTimestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        }

        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numeric))
        {
            if (TryParseUnixTime(numeric, out var unixTimestamp))
            {
                return unixTimestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
            }

            if (TryParseDotNetTicks(numeric, out var ticksTimestamp))
            {
                return ticksTimestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
            }

            if (TryParseFileTime(numeric, out var fileTimeTimestamp))
            {
                return fileTimeTimestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
            }
        }

        return value;
    }

    private static bool TryParseUnixTime(long numeric, out DateTimeOffset timestamp)
    {
        try
        {
            if (Math.Abs(numeric) is >= 1_000_000_000 and <= 32_503_680_000)
            {
                timestamp = DateTimeOffset.FromUnixTimeSeconds(numeric);
                return true;
            }

            if (Math.Abs(numeric) is >= 1_000_000_000_000 and <= 32_503_680_000_000)
            {
                timestamp = DateTimeOffset.FromUnixTimeMilliseconds(numeric);
                return true;
            }
        }
        catch
        {
            // Ignore and try other timestamp formats.
        }

        timestamp = default;
        return false;
    }

    private static bool TryParseDotNetTicks(long numeric, out DateTimeOffset timestamp)
    {
        if (numeric < DateTime.MinValue.Ticks || numeric > DateTime.MaxValue.Ticks)
        {
            timestamp = default;
            return false;
        }

        try
        {
            timestamp = new DateTimeOffset(new DateTime(numeric, DateTimeKind.Utc));
            return true;
        }
        catch
        {
            timestamp = default;
            return false;
        }
    }

    private static bool TryParseFileTime(long numeric, out DateTimeOffset timestamp)
    {
        try
        {
            timestamp = new DateTimeOffset(DateTime.FromFileTimeUtc(numeric));
            return true;
        }
        catch
        {
            timestamp = default;
            return false;
        }
    }

    private static string MapHistoryOperation(string? rawOperation)
    {
        if (!int.TryParse(rawOperation, NumberStyles.Integer, CultureInfo.InvariantCulture, out var operation))
        {
            return rawOperation ?? string.Empty;
        }

        return operation switch
        {
            1 => "Installation",
            2 => "Uninstallation",
            3 => "Other",
            _ => operation.ToString()
        };
    }

    private static string MapHistoryResult(string? rawResultCode)
    {
        if (!int.TryParse(rawResultCode, NumberStyles.Integer, CultureInfo.InvariantCulture, out var resultCode))
        {
            return rawResultCode ?? string.Empty;
        }

        return resultCode switch
        {
            0 => "NotStarted",
            1 => "InProgress",
            2 => "Succeeded",
            3 => "SucceededWithErrors",
            4 => "Failed",
            5 => "Aborted",
            _ => resultCode.ToString()
        };
    }

    private static string FormatHistoryHResult(string? rawHResult)
    {
        return ErrorCodeResolver.Normalize(rawHResult);
    }

    private static int MapNavigationTargetToSectionIndex(string? navigationTarget)
    {
        if (string.IsNullOrWhiteSpace(navigationTarget))
        {
            return 0;
        }

        return navigationTarget.Trim().ToLowerInvariant() switch
        {
            "overview" => 0,
            "available-updates" => 1,
            "update-history" => 2,
            "reporting-events-log" => 3,
            "uso-diagnostics" => UsoDiagnosticsSectionIndex,
            _ => 0
        };
    }

    private static string GetToken(IReadOnlyList<string> tokens, int index)
    {
        return index >= 0 && index < tokens.Count ? tokens[index] : string.Empty;
    }

    private async Task<WindowsClientCenter.Intune.Services.Runtime.PowershellExecutionResult> RunPowershellAsync(string script, CancellationToken cancellationToken)
    {
        if (_powerShellExecutor is not null)
        {
            return await _powerShellExecutor.ExecuteForHostAsync(Environment.MachineName, script, cancellationToken);
        }

        throw new InvalidOperationException("PowerShell executor is not available.");
    }

    private static void TryTerminateProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best effort cleanup for cancellation path.
        }
    }

    private sealed record ExternalCommandResult(int ExitCode, string StdOut, string StdErr);
    private sealed record InstallSelectionItem(string Title, string UpdateId, int Revision);
    private sealed record UninstallHistoryUpdateResult(bool Success, bool RebootRequired, string Message, string HResult, int Revision, string UpdateId);

    private sealed record AvailableUpdatesPayload(
        int UpdateCount,
        IReadOnlyList<WindowsUpdateAvailableEntry> Updates,
        IReadOnlyList<WindowsUpdateProviderEntry> Providers,
        string LastSearchSuccessDate,
        string SearchSource);
    private sealed record UpdateHistoryPayload(int TotalCount, int ReturnedCount, IReadOnlyList<WindowsUpdateHistoryEntry> Entries);

    private sealed class AvailableUpdatesWirePayload
    {
        public string? SearchSource { get; init; }
        public string? LastSearchSuccessDate { get; init; }
        public int UpdateCount { get; init; }
        public List<AvailableUpdateWireEntry>? Updates { get; init; }
        public List<AvailableUpdateProviderWireEntry>? Providers { get; init; }
    }

    private sealed class AvailableUpdateWireEntry
    {
        public string? Title { get; init; }
        public string? Type { get; init; }
        public string? Status { get; init; }
        public bool IsInstalled { get; init; }
        public bool IsHidden { get; init; }
        public string? KbArticles { get; init; }
        public bool IsDownloaded { get; init; }
        public bool IsMandatory { get; init; }
        public bool EulaAccepted { get; init; }
        public string? Categories { get; init; }
        public string? Deadline { get; init; }
        public string? UpdateId { get; init; }
        public int Revision { get; init; }
    }

    private sealed class AvailableUpdateProviderWireEntry
    {
        public string? Name { get; init; }
        public string? ServiceId { get; init; }
        public bool IsDefault { get; init; }
        public bool IsRegisteredWithAU { get; init; }
        public bool OffersWindowsUpdates { get; init; }
        public bool IsManaged { get; init; }
    }

    private sealed class WinRtInventoryWirePayload
    {
        public WinRtManagerSnapshotWirePayload? ManagerSnapshot { get; init; }
        public List<WinRtCompletedUpdateWireEntry>? MostRecentCompletedUpdates { get; init; }
    }

    private sealed class WinRtManagerSnapshotWirePayload
    {
        public WinRtManagerStatusWirePayload? ManagerStatus { get; init; }
        public List<WinRtApplicableUpdateWireEntry>? Updates { get; init; }
        public List<WinRtSoftwareUpdateWireEntry>? SoftwareUpdates { get; init; }
        public List<WinRtCompletedUpdateWireEntry>? MostRecentCompletedUpdates { get; init; }
    }

    private sealed class WinRtManagerStatusWirePayload
    {
        public string? LastSuccessfulScanTimestamp { get; init; }
        public List<string>? ProviderIds { get; init; }
    }

    private sealed class WinRtApplicableUpdateWireEntry
    {
        public string? Title { get; init; }
        public string? UpdateId { get; init; }
        public string? CurrentAction { get; init; }
        public bool IsDriver { get; init; }
        public bool IsFeatureUpdate { get; init; }
        public bool IsMandatory { get; init; }
        public bool IsSecurity { get; init; }
        public string? Deadline { get; init; }
    }

    private sealed class WinRtSoftwareUpdateWireEntry
    {
        public string? Title { get; init; }
        public string? UpdateId { get; init; }
        public string? CurrentAction { get; init; }
    }

    private sealed class WinRtCompletedUpdateWireEntry
    {
        public string? Title { get; init; }
        public string? UpdateId { get; init; }
        public string? ProviderId { get; init; }
        public string? Timestamp { get; init; }
        public string? Operation { get; init; }
    }

    private sealed class InstallStatusPayload
    {
        public string? Phase { get; init; }
        public string? Message { get; init; }
        public string? CurrentTitle { get; init; }
        public int TotalCount { get; init; }
        public int CompletedCount { get; init; }
        public int InstalledCount { get; init; }
        public int FailedCount { get; init; }
        public bool RebootRequired { get; init; }
        public List<string>? FailedTitles { get; init; }
        public string? LastUpdatedUtc { get; init; }
    }

}
