using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WindowsClientCenter.Host.Runtime;
using WindowsClientCenter.Intune.Services.Contracts;
using WindowsClientCenter.Intune.Services.Models;
using WindowsClientCenter.Intune.Services.Runtime;
using WindowsClientCenter.Plugin.Abstractions.Contracts;
using WindowsClientCenter.Plugin.Abstractions.Models;
using WindowsClientCenter.Plugin.Host;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace WindowsClientCenter.Host.ViewModels;

public partial class MainWindowViewModel : ObservableObject, IHostStatusLogSink, IHostBusyStateSink, IHostRibbonRefreshSink, IDisposable
{
    private const int BusyOverlayFrameCount = 24;
    private const int BusyOverlayFrameIntervalMilliseconds = 140;
    private const int MaxRecentHosts = 10;
    private const int HostPingTimeoutMs = 1500;
    private const int HostConnectionTimeoutMs = 1500;
    private const int HostConnectionOverallTimeoutMs = 4500;
    private const int HostBackgroundPingIntervalMs = 15000;
    private const int ConnectedUsersLookupTimeoutSeconds = 8;

    private readonly IServiceProvider _serviceProvider;
    private readonly IPluginRegistry _pluginRegistry;
    private readonly ITargetHostService _targetHostService;
    private readonly IHostConnectivityService _hostConnectivityService;
    private readonly ILocalDeviceActionService _localDeviceActionService;
    private readonly IHostUserSettingsStore _userSettingsStore;
    private readonly IntuneRuntimeOptions _intuneRuntimeOptions;
    private readonly DemoDataCatalog _demoDataCatalog;
    private readonly HostRuntimeOptions _runtimeOptions;
    private readonly HostPluginOptions _pluginOptions;
    private readonly HostExplorerOptions _explorerOptions;
    private readonly IConfiguration _configuration;
    private readonly HostStatusLogDispatcher _hostStatusLogDispatcher;
    private readonly ILogger<MainWindowViewModel> _logger;
    private readonly Dictionary<string, LoadedPlugin> _pluginById = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, object> _viewByPluginId = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HostBusyState> _busyStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> _navigationExpansionStateByPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly StringBuilder _logBuilder = new();
    private readonly object _busyStateSync = new();
    private readonly object _hostPingSync = new();
    private readonly SemaphoreSlim _userSettingsSaveGate = new(1, 1);
    private readonly DispatcherTimer _busyOverlayTimer;
    private readonly ImageSource[] _busyOverlayFrames;

    private string? _startupHost;
    private CancellationTokenSource? _hostPingCancellationTokenSource;
    private string? _connectedHostForPing;
    private string? _selectedNavigationNodePath;
    private bool _connectedUsersLookupCompleted;
    private bool _isManualPingRunning;
    private DateTimeOffset? _hostPingOfflineSince;
    private int _busyOverlayFrameIndex;
    private int _busyOverlayPauseTicksRemaining;

    public MainWindowViewModel(
        IServiceProvider serviceProvider,
        IPluginRegistry pluginRegistry,
        ITargetHostService targetHostService,
        IHostConnectivityService hostConnectivityService,
        ILocalDeviceActionService localDeviceActionService,
        IHostUserSettingsStore userSettingsStore,
        IntuneRuntimeOptions intuneRuntimeOptions,
        DemoDataCatalog demoDataCatalog,
        HostRuntimeOptions runtimeOptions,
        HostPluginOptions pluginOptions,
        HostExplorerOptions explorerOptions,
        IConfiguration configuration,
        HostStatusLogDispatcher hostStatusLogDispatcher,
        ILogger<MainWindowViewModel> logger)
    {
        _serviceProvider = serviceProvider;
        _pluginRegistry = pluginRegistry;
        _targetHostService = targetHostService;
        _hostConnectivityService = hostConnectivityService;
        _localDeviceActionService = localDeviceActionService;
        _userSettingsStore = userSettingsStore;
        _intuneRuntimeOptions = intuneRuntimeOptions;
        _demoDataCatalog = demoDataCatalog;
        _runtimeOptions = runtimeOptions;
        _pluginOptions = pluginOptions;
        _explorerOptions = explorerOptions;
        _configuration = configuration;
        _hostStatusLogDispatcher = hostStatusLogDispatcher;
        _logger = logger;
        _hostStatusLogDispatcher.ReplayTo(AppendLog);
        _hostStatusLogDispatcher.MessageAppended += AppendLog;
        _targetHostService.HostChanged += OnTargetHostChanged;
        _busyOverlayFrames = CreateBusyOverlayFrames();
        _busyOverlayTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(BusyOverlayFrameIntervalMilliseconds)
        };
        _busyOverlayTimer.Tick += OnBusyOverlayTimerTick;
    }

    public ObservableCollection<NavigationNode> Navigation { get; } = [];

    public ObservableCollection<RibbonGroupItem> RibbonGroups { get; } = [];

    public ObservableCollection<RibbonControlItem> PowerShellRibbonControls { get; } = [];

    public ObservableCollection<string> RecentHosts { get; } = [];

    public ObservableCollection<ExplorerTargetItem> ExplorerTargets { get; } = [];

    [ObservableProperty]
    private object? _currentContent;

    [ObservableProperty]
    private string _environmentName = string.Empty;

    [ObservableProperty]
    private string _currentHost = string.Empty;

    [ObservableProperty]
    private string? _connectedHost;

    [ObservableProperty]
    private string _connectedUsersText = string.Empty;

    [ObservableProperty]
    private string _hostInputText = string.Empty;

    [ObservableProperty]
    private string _hostStatus = "No host selected";

    [ObservableProperty]
    private string _logText = string.Empty;

    [ObservableProperty]
    private bool _isConnecting;

    [ObservableProperty]
    private string _hostPingIndicatorBrush = "#8A8A8A";

    [ObservableProperty]
    private string _hostPingIndicatorToolTip = "Ping status unknown. Connect first.";

    [ObservableProperty]
    private bool _isHostBusyVisible;

    [ObservableProperty]
    private string _hostBusyShortStatus = string.Empty;

    [ObservableProperty]
    private string _hostBusyToolTip = string.Empty;

    [ObservableProperty]
    private ImageSource? _taskbarOverlayImage;

    public string WindowTitle => string.IsNullOrWhiteSpace(ConnectedHost)
        ? "Windows Client Center"
        : !_connectedUsersLookupCompleted
            ? $"{ConnectedHost} - Windows Client Center"
            : string.IsNullOrWhiteSpace(ConnectedUsersText)
            ? $"{ConnectedHost} (No user logged on) - Windows Client Center"
            : $"{ConnectedHost} ({ConnectedUsersText}) - Windows Client Center";

    public bool CanOpenRemoteHostTools =>
        !string.IsNullOrWhiteSpace(_targetHostService.CurrentHost);

    public bool CanOpenExplorerTargets =>
        ResolveDefaultExplorerTarget() is { IsEnabled: true };

    public bool HasExplorerTargets =>
        ExplorerTargets.Count > 0;

    private void OnTargetHostChanged(object? sender, string host)
    {
        RefreshRemoteToolAvailability();
        RefreshExplorerTargetAvailability();
    }

    private void RefreshRemoteToolAvailability()
    {
        OnPropertyChanged(nameof(CanOpenRemoteHostTools));
        OpenRemotePowerShellCommand.NotifyCanExecuteChanged();
        OpenRemotePowerShellNoProfileCommand.NotifyCanExecuteChanged();
        OpenRemotePsExecCommand.NotifyCanExecuteChanged();
        UpdateRibbonControlAvailability();
    }

    private void RefreshExplorerTargetAvailability()
    {
        RebuildExplorerTargets();
        OnPropertyChanged(nameof(HasExplorerTargets));
        OnPropertyChanged(nameof(CanOpenExplorerTargets));
        OpenDefaultExplorerTargetCommand.NotifyCanExecuteChanged();
    }

    public void SetStartupHost(string? host)
    {
        _startupHost = string.IsNullOrWhiteSpace(host) ? null : host.Trim();
    }

    partial void OnCurrentHostChanged(string value)
    {
        RefreshRemoteToolAvailability();
        RefreshExplorerTargetAvailability();
    }

    public void Dispose()
    {
        StopHostPingMonitor();
        _hostStatusLogDispatcher.MessageAppended -= AppendLog;
        _targetHostService.HostChanged -= OnTargetHostChanged;
        DisposeCachedViews();
        _busyOverlayTimer.Stop();
        _busyOverlayTimer.Tick -= OnBusyOverlayTimerTick;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        EnvironmentName = _runtimeOptions.Environment;

        await LoadUserSettingsAsync(cancellationToken);

        if (IsDemoMode)
        {
            CurrentHost = _demoDataCatalog.DemoHostName;
        }
        else if (!string.IsNullOrWhiteSpace(_startupHost))
        {
            CurrentHost = _startupHost;
        }
        else if (RecentHosts.Count > 0)
        {
            CurrentHost = RecentHosts[0];
        }
        else
        {
            CurrentHost = "localhost";
        }

        HostInputText = CurrentHost;
        if (IsDemoMode)
        {
            ApplyDemoConnectionState(CurrentHost, appendLogEntry: true);
        }
        else
        {
            HostStatus = $"Host '{CurrentHost}' vorbereitet. Connect startet erst manuell.";
        }

        RefreshExplorerTargetAvailability();
        await LoadPluginsAsync(cancellationToken);
        if (IsDemoMode)
        {
            RestoreSelectedNavigationAfterConnect();
        }
    }

    [RelayCommand(CanExecute = nameof(CanConnectHost))]
    public Task ConnectHostAsync()
    {
        return ConnectHostAsync(CancellationToken.None, persistSettings: true);
    }

    private async Task ConnectHostAsync(CancellationToken cancellationToken, bool persistSettings)
    {
        if (IsDemoMode)
        {
            await ConnectDemoHostAsync(cancellationToken, persistSettings);
            return;
        }

        var normalizedHost = HostInputText.Trim();
        if (string.IsNullOrWhiteSpace(normalizedHost))
        {
            HostStatus = "Hostname is empty";
            AppendLog("Connect failed: hostname is empty.");
            return;
        }

        try
        {
            IsConnecting = true;
            HostInputText = normalizedHost;
            CurrentHost = normalizedHost;
            ResetConnectedHostStateForNewAttempt(normalizedHost);
            HostStatus = $"Connecting to '{normalizedHost}'...";
            AppendLog($"Connecting to host '{normalizedHost}'...");

            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(HostConnectionOverallTimeoutMs);

            var connectivity = await _hostConnectivityService.TestConnectivityAsync(normalizedHost, timeoutSource.Token);

            if (!connectivity.IsWinRmReachable &&
                (connectivity.SmbReachable || IsLocalHost(normalizedHost)))
            {
                HostStatus = $"WinRM is not reachable on '{normalizedHost}'. Attempting to enable it...";
                AppendLog($"WinRM is not reachable on '{normalizedHost}'. Attempting automatic enablement...");

                using var bootstrapTimeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                bootstrapTimeoutSource.CancelAfter(TimeSpan.FromSeconds(90));

                var enableResult = await _localDeviceActionService.ExecuteLocalActionAsync(
                    normalizedHost,
                    "enable-winrm",
                    null,
                    bootstrapTimeoutSource.Token);

                AppendLog(enableResult.Success
                    ? enableResult.Message
                    : $"Automatic WinRM enable failed on '{normalizedHost}': {enableResult.Message}");

                if (enableResult.Success)
                {
                    HostStatus = $"WinRM bootstrap completed on '{normalizedHost}'. Rechecking connectivity...";
                    using var refreshTimeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    refreshTimeoutSource.CancelAfter(HostConnectionOverallTimeoutMs);
                    connectivity = await _hostConnectivityService.TestConnectivityAsync(normalizedHost, refreshTimeoutSource.Token);
                }
            }

            if (!connectivity.IsWinRmReachable)
            {
                var failureReason = BuildHostConnectionFailureReason(connectivity);
                var failureStatus = $"Connection to '{normalizedHost}' failed: {failureReason}";
                ShowHostConnectionFailure(
                    normalizedHost,
                    failureStatus,
                    failureReason,
                    new PingConnectivityResult(connectivity.PingSucceeded, connectivity.PingRoundtripTimeMs, connectivity.PingDetail, connectivity.ResolvedIp),
                    IsHostOffline(connectivity));
                HostStatus = failureStatus;
                AppendLog(
                    $"Connection to '{normalizedHost}' failed: {failureReason} " +
                    $"SMB={connectivity.SmbReachable}, WinRM5985={connectivity.WinRmHttpReachable}, WinRM5986={connectivity.WinRmHttpsReachable}.");
                HostInputText = normalizedHost;
                return;
            }

            _targetHostService.SetCurrentHost(normalizedHost);
            ConnectedHost = normalizedHost;
            SetConnectedUsersLookupState(null, lookupCompleted: false);
            HostStatus = BuildHostStatus(normalizedHost, connectivity);
            AppendLog(BuildHostLogEntry(normalizedHost, connectivity));
            ApplyPingIndicator(normalizedHost, new PingConnectivityResult(connectivity.PingSucceeded, connectivity.PingRoundtripTimeMs, connectivity.PingDetail, connectivity.ResolvedIp));
            StartHostPingMonitor(normalizedHost);
            RestoreSelectedNavigationAfterConnect();
            IsConnecting = false;
            await RefreshConnectedUsersAsync(normalizedHost, cancellationToken);

            AddRecentHost(normalizedHost);

            if (persistSettings)
            {
                await SaveUserSettingsAsync(cancellationToken);
            }

            HostInputText = normalizedHost;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            HostStatus = $"Connection test for '{normalizedHost}' timed out.";
            AppendLog($"Connection test for '{normalizedHost}' timed out after {HostConnectionOverallTimeoutMs} ms.");
            ShowHostConnectionFailure(
                normalizedHost,
                HostStatus,
                "The device did not respond in time. No host data is currently loaded.",
                new PingConnectivityResult(false, null, "Connection test timed out", null),
                isOffline: true);
            HostInputText = normalizedHost;
        }
        catch (Exception ex)
        {
            HostStatus = $"Connection test for '{normalizedHost}' failed.";
            AppendLog($"Connection test for '{normalizedHost}' failed: {ex.Message}");
            ShowHostConnectionFailure(
                normalizedHost,
                HostStatus,
                $"The device is unavailable or returned an error: {ex.Message}",
                new PingConnectivityResult(false, null, ex.Message, null),
                isOffline: true);
            _logger.LogError(ex, "Host connection test failed for {Host}.", normalizedHost);
            HostInputText = normalizedHost;
        }
        finally
        {
            if (IsConnecting)
            {
                IsConnecting = false;
            }
        }
    }

    [RelayCommand(CanExecute = nameof(CanPingHost))]
    public async Task PingHostAsync()
    {
        var host = _connectedHostForPing;
        if (string.IsNullOrWhiteSpace(host))
        {
            return;
        }

        _isManualPingRunning = true;
        PingHostCommand.NotifyCanExecuteChanged();
        try
        {
            if (IsDemoMode)
            {
                var demoConnectivity = _demoDataCatalog.GetConnectivityStatus(host);
                var demoPingResult = new PingConnectivityResult(
                    demoConnectivity.PingSucceeded,
                    demoConnectivity.PingRoundtripTimeMs,
                    demoConnectivity.PingDetail,
                    demoConnectivity.ResolvedIp);
                ApplyPingIndicator(host, demoPingResult);
                AppendLog($"Manual demo ping to '{host}' succeeded ({demoPingResult.PingRoundtripTimeMs ?? 0} ms).");
                return;
            }

            var pingResult = await SendPingAsync(host);
            ApplyPingIndicator(host, pingResult);

            var resolvedIpSuffix = string.IsNullOrWhiteSpace(pingResult.ResolvedIp)
                ? string.Empty
                : $" [{pingResult.ResolvedIp}]";
            var logMessage = pingResult.PingSucceeded
                ? $"Manual ping to '{host}'{resolvedIpSuffix} succeeded ({pingResult.PingRoundtripTimeMs ?? 0} ms)."
                : $"Manual ping to '{host}'{resolvedIpSuffix} failed ({pingResult.PingDetail}).";
            AppendLog(logMessage);
        }
        finally
        {
            _isManualPingRunning = false;
            PingHostCommand.NotifyCanExecuteChanged();
        }
    }

    private static async Task<PingConnectivityResult> SendPingAsync(string host)
    {
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(host, HostPingTimeoutMs);
            var resolvedIp = reply.Address is null || IPAddress.None.Equals(reply.Address)
                ? await TryResolveHostIpAsync(host)
                : reply.Address.ToString();
            return new PingConnectivityResult(
                PingSucceeded: reply.Status == IPStatus.Success,
                PingRoundtripTimeMs: reply.Status == IPStatus.Success ? reply.RoundtripTime : null,
                PingDetail: reply.Status == IPStatus.Success ? "ok" : reply.Status.ToString(),
                ResolvedIp: resolvedIp);
        }
        catch (PingException ex)
        {
            return new PingConnectivityResult(false, null, ex.InnerException?.Message ?? ex.Message, await TryResolveHostIpAsync(host));
        }
        catch (SocketException ex)
        {
            return new PingConnectivityResult(false, null, ex.Message, await TryResolveHostIpAsync(host));
        }
    }

    private static async Task<string?> TryResolveHostIpAsync(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return null;
        }

        if (IPAddress.TryParse(host, out var directIp))
        {
            return directIp.ToString();
        }

        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host);
            if (addresses.Length == 0)
            {
                return null;
            }

            var ipv4 = addresses.FirstOrDefault(static address => address.AddressFamily == AddressFamily.InterNetwork);
            return (ipv4 ?? addresses[0]).ToString();
        }
        catch
        {
            return null;
        }
    }

    private async Task RefreshConnectedUsersAsync(string host, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            SetConnectedUsersLookupState(null, lookupCompleted: false);
            return;
        }

        if (IsDemoMode)
        {
            var users = _demoDataCatalog.GetConnectedUsers();
            SetConnectedUsersLookupState(FormatConnectedUsersTitle(users), lookupCompleted: true);
            AppendLog($"Connected users on '{host}': {string.Join(", ", users)}.");
            return;
        }

        try
        {
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(TimeSpan.FromSeconds(ConnectedUsersLookupTimeoutSeconds));

            var users = await GetConnectedUsersAsync(host, timeoutSource.Token);
            SetConnectedUsersLookupState(FormatConnectedUsersTitle(users), lookupCompleted: true);

            if (users.Count == 0)
            {
                AppendLog($"Connected users on '{host}': none found.");
            }
            else
            {
                AppendLog($"Connected users on '{host}': {string.Join(", ", users)}.");
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            SetConnectedUsersLookupState(null, lookupCompleted: false);
            AppendLog($"Connected user lookup for '{host}' timed out.");
        }
        catch (Exception ex)
        {
            SetConnectedUsersLookupState(null, lookupCompleted: false);
            AppendLog($"Connected user lookup for '{host}' failed: {ex.Message}");
            _logger.LogDebug(ex, "Connected user lookup failed for {Host}.", host);
        }
    }

    private static async Task<IReadOnlyList<string>> GetConnectedUsersAsync(string host, CancellationToken cancellationToken)
    {
        var escapedHost = EscapeSingleQuotedPowerShellLiteral(host.Trim());
        var script =
            "$ErrorActionPreference='Stop';" +
            $"$computerName='{escapedHost}';" +
            "function Add-UserNames {" +
            "  param($Source, [System.Collections.Generic.HashSet[string]]$Users);" +
            "  foreach ($entry in @($Source)) {" +
            "    if ($null -eq $entry) { continue };" +
            "    $candidate = [string]$entry;" +
            "    if ([string]::IsNullOrWhiteSpace($candidate)) { continue };" +
            "    $candidate = $candidate.Trim();" +
            "    if ([string]::IsNullOrWhiteSpace($candidate)) { continue };" +
            "    if ($candidate -match '^>') { $candidate = $candidate.Substring(1).Trim() };" +
            "    if ($candidate -match '^(?<user>\\S+)\\s+\\S+\\s+\\d+\\s+') { $candidate = $matches['user'] };" +
            "    if ($candidate -match '^(BENUTZERNAME|USERNAME)$') { continue };" +
            "    [void]$Users.Add($candidate);" +
            "  };" +
            "};" +
            "function Get-UserNamesFromExplorerProcesses {" +
            "  param([string]$ComputerName);" +
            "  $users = New-Object System.Collections.Generic.HashSet[string] ([System.StringComparer]::OrdinalIgnoreCase);" +
            "  try {" +
            "    $processes = if ($ComputerName -eq 'localhost' -or $ComputerName -eq '.' -or $ComputerName -eq $env:COMPUTERNAME) {" +
            "      @(Get-Process explorer -IncludeUserName -ErrorAction Stop)" +
            "    } else {" +
            "      @(Invoke-Command -ComputerName $ComputerName -ErrorAction Stop -ScriptBlock { @(Get-Process explorer -IncludeUserName -ErrorAction Stop) })" +
            "    };" +
            "    foreach ($process in @($processes)) {" +
            "      if ($null -eq $process) { continue };" +
            "      $userName = [string]$process.UserName;" +
            "      if ([string]::IsNullOrWhiteSpace($userName)) { continue };" +
            "      [void]$users.Add($userName);" +
            "    };" +
            "  } catch {" +
            "    $queryOutput = if ($ComputerName -eq 'localhost' -or $ComputerName -eq '.' -or $ComputerName -eq $env:COMPUTERNAME) {" +
            "      @(query user 2>$null | Select-Object -Skip 1)" +
            "    } else {" +
            "      @(Invoke-Command -ComputerName $ComputerName -ErrorAction Stop -ScriptBlock { @(query user 2>$null | Select-Object -Skip 1) })" +
            "    };" +
            "    Add-UserNames -Source $queryOutput -Users $users;" +
            "  };" +
            "  return $users | Sort-Object;" +
            "};" +
            "$output = Get-UserNamesFromExplorerProcesses -ComputerName $computerName;" +
            "$output | ForEach-Object { $_ }";

        var execution = await RunPowerShellEncodedCommandAsync(script, cancellationToken);
        if (execution.ExitCode != 0)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(execution.StdErr) ? "Connected user lookup failed." : execution.StdErr.Trim());
        }

        return execution.StdOut
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static line => !string.IsNullOrWhiteSpace(line))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string FormatConnectedUsersTitle(IReadOnlyList<string> users)
    {
        var distinctUsers = users
            .Where(static user => !string.IsNullOrWhiteSpace(user))
            .Select(static user => user.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (distinctUsers.Length == 0)
        {
            return string.Empty;
        }

        const int maxUsersInTitle = 3;
        var visibleUsers = distinctUsers.Take(maxUsersInTitle).ToArray();
        if (distinctUsers.Length <= maxUsersInTitle)
        {
            return string.Join(", ", visibleUsers);
        }

        return $"{string.Join(", ", visibleUsers)} +{distinctUsers.Length - maxUsersInTitle}";
    }

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunPowerShellEncodedCommandAsync(string script, CancellationToken cancellationToken)
    {
        var encodedCommand = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encodedCommand}",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start PowerShell.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch
        {
            TryTerminateProcess(process);
            await ObserveTaskExceptionAsync(stdoutTask);
            await ObserveTaskExceptionAsync(stderrTask);
            throw;
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        return (process.ExitCode, stdout, stderr);
    }

    private static async Task ObserveTaskExceptionAsync(Task task)
    {
        try
        {
            await task;
        }
        catch
        {
            // Best effort observation for canceled process stream readers.
        }
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

    private static bool IsLocalHost(string host)
    {
        return HostConnectivityService.IsLocalHost(host);
    }

    private static string BuildHostStatus(string host, HostConnectivityStatus connectivity)
    {
        var resolvedIpSuffix = string.IsNullOrWhiteSpace(connectivity.ResolvedIp)
            ? string.Empty
            : $" [{connectivity.ResolvedIp}]";
        var pingText = connectivity.PingSucceeded
            ? $"Ping ok{resolvedIpSuffix} ({connectivity.PingRoundtripTimeMs ?? 0} ms)"
            : $"Ping failed{resolvedIpSuffix} ({connectivity.PingDetail})";

        var channels = new List<string>();
        if (connectivity.SmbReachable)
        {
            channels.Add("SMB");
        }

        if (connectivity.WinRmHttpReachable)
        {
            channels.Add("WinRM HTTP");
        }

        if (connectivity.WinRmHttpsReachable)
        {
            channels.Add("WinRM HTTPS");
        }

        var connectionText = channels.Count > 0
            ? $"Connection ok via {string.Join(", ", channels)}"
            : "No SMB/WinRM port reachable";

        return $"{host}: {pingText}, {connectionText}";
    }

    private static string BuildHostLogEntry(string host, HostConnectivityStatus connectivity)
    {
        var resolvedIpSuffix = string.IsNullOrWhiteSpace(connectivity.ResolvedIp)
            ? string.Empty
            : $" [{connectivity.ResolvedIp}]";
        var pingText = connectivity.PingSucceeded
            ? $"Ping ok{resolvedIpSuffix} ({connectivity.PingRoundtripTimeMs ?? 0} ms)"
            : $"Ping failed{resolvedIpSuffix} ({connectivity.PingDetail})";

        return
            $"Connected host '{host}'. {pingText}. " +
            $"SMB={connectivity.SmbReachable}, WinRM5985={connectivity.WinRmHttpReachable}, WinRM5986={connectivity.WinRmHttpsReachable}.";
    }

    private static string BuildHostConnectionFailureReason(HostConnectivityStatus connectivity)
    {
        if (connectivity.IsWinRmReachable)
        {
            return "Unknown connection failure.";
        }

        if (connectivity.PingSucceeded || connectivity.SmbReachable)
        {
            return "WinRM is not reachable.";
        }

        return string.IsNullOrWhiteSpace(connectivity.PingDetail)
            ? "Host is not reachable."
            : $"Host is not reachable ({connectivity.PingDetail}).";
    }

    private static bool IsHostOffline(HostConnectivityStatus connectivity)
    {
        return !connectivity.PingSucceeded && !connectivity.SmbReachable;
    }

    private static TextBlock BuildHostMessageContent(string title, string detail)
    {
        return new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(detail)
                ? title
                : $"{title}{Environment.NewLine}{Environment.NewLine}{detail}",
            Margin = new Thickness(16),
            TextWrapping = TextWrapping.Wrap
        };
    }

    private bool CanConnectHost()
    {
        return !IsConnecting;
    }

    private bool IsDemoMode => _intuneRuntimeOptions.Mode == IntuneRuntimeMode.Demo;

    private bool CanPingHost()
    {
        return !IsConnecting && !_isManualPingRunning && !string.IsNullOrWhiteSpace(_connectedHostForPing);
    }

    partial void OnIsConnectingChanged(bool value)
    {
        ConnectHostCommand.NotifyCanExecuteChanged();
        PingHostCommand.NotifyCanExecuteChanged();
    }

    partial void OnConnectedHostChanged(string? value)
    {
        OnPropertyChanged(nameof(WindowTitle));
        RefreshRemoteToolAvailability();
    }

    partial void OnConnectedUsersTextChanged(string value)
    {
        OnPropertyChanged(nameof(WindowTitle));
    }

    private void SetConnectedUsersLookupState(string? usersText, bool lookupCompleted)
    {
        ConnectedUsersText = usersText ?? string.Empty;
        if (_connectedUsersLookupCompleted != lookupCompleted)
        {
            _connectedUsersLookupCompleted = lookupCompleted;
            OnPropertyChanged(nameof(WindowTitle));
        }
    }

    private void StartHostPingMonitor(string host)
    {
        if (IsDemoMode)
        {
            StopHostPingMonitor();
            _connectedHostForPing = host;
            _hostPingOfflineSince = null;
            PingHostCommand.NotifyCanExecuteChanged();
            return;
        }

        StopHostPingMonitor();

        _connectedHostForPing = host;
        _hostPingOfflineSince = null;
        var cts = new CancellationTokenSource();
        _hostPingCancellationTokenSource = cts;
        _ = RunHostPingMonitorAsync(host, cts.Token);
        PingHostCommand.NotifyCanExecuteChanged();
    }

    private void StopHostPingMonitor()
    {
        lock (_hostPingSync)
        {
            if (_hostPingCancellationTokenSource is not null)
            {
                try
                {
                    _hostPingCancellationTokenSource.Cancel();
                }
                catch
                {
                    // Ignore cancellation race.
                }
                _hostPingCancellationTokenSource.Dispose();
                _hostPingCancellationTokenSource = null;
            }
        }
    }

    private void ResetConnectedHostStateForNewAttempt(string host)
    {
        StopHostPingMonitor();
        _connectedHostForPing = null;
        _hostPingOfflineSince = null;
        PingHostCommand.NotifyCanExecuteChanged();
        _targetHostService.SetCurrentHost(string.Empty);
        ConnectedHost = null;
        SetConnectedUsersLookupState(null, lookupCompleted: false);
        CurrentContent = BuildHostMessageContent(
            $"Connecting to '{host}'...",
            "Previously loaded host data was cleared. Fresh data will appear only after a successful connection.");

        lock (_busyStateSync)
        {
            _busyStates.Clear();
        }

        RefreshBusyIndicator();
        ResetPingIndicator(host);
    }

    private async Task ConnectDemoHostAsync(CancellationToken cancellationToken, bool persistSettings)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IsConnecting = true;

        try
        {
            var demoHost = _demoDataCatalog.DemoHostName;
            HostInputText = demoHost;
            CurrentHost = demoHost;
            ResetConnectedHostStateForNewAttempt(demoHost);
            ApplyDemoConnectionState(demoHost, appendLogEntry: true);
            RestoreSelectedNavigationAfterConnect();
            AddRecentHost(demoHost);

            if (persistSettings)
            {
                await SaveUserSettingsAsync(cancellationToken);
            }
        }
        finally
        {
            IsConnecting = false;
        }
    }

    private void ApplyDemoConnectionState(string host, bool appendLogEntry)
    {
        var normalizedHost = _demoDataCatalog.NormalizeHost(host);
        var connectivity = _demoDataCatalog.GetConnectivityStatus(normalizedHost);
        _targetHostService.SetCurrentHost(normalizedHost);
        ConnectedHost = normalizedHost;
        _connectedHostForPing = normalizedHost;
        SetConnectedUsersLookupState(FormatConnectedUsersTitle(_demoDataCatalog.GetConnectedUsers()), lookupCompleted: true);
        HostStatus = $"{BuildHostStatus(normalizedHost, connectivity)} [Demo]";
        ApplyPingIndicator(
            normalizedHost,
            new PingConnectivityResult(
                connectivity.PingSucceeded,
                connectivity.PingRoundtripTimeMs,
                connectivity.PingDetail,
                connectivity.ResolvedIp));
        if (appendLogEntry)
        {
            AppendLog($"Demo mode active. Connected to simulated host '{normalizedHost}'.");
        }
    }

    private async Task RunHostPingMonitorAsync(string host, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(HostBackgroundPingIntervalMs, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                if (cancellationToken.IsCancellationRequested ||
                    !string.Equals(_connectedHostForPing, host, StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                var pingResult = await SendPingAsync(host);

                var application = System.Windows.Application.Current;
                if (application is null)
                {
                    break;
                }

                if (application.Dispatcher.CheckAccess())
                {
                    ApplyPingIndicator(host, pingResult);
                }
                else
                {
                    await application.Dispatcher.InvokeAsync(() => ApplyPingIndicator(host, pingResult));
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when the connection monitor is stopped or the app is shutting down.
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Background host ping monitor stopped for {Host}.", host);
        }
    }

    private void ApplyPingIndicator(string host, PingConnectivityResult pingResult)
    {
        var resolvedIpText = string.IsNullOrWhiteSpace(pingResult.ResolvedIp)
            ? string.Empty
            : $" ({pingResult.ResolvedIp})";
        HostPingIndicatorBrush = pingResult.PingSucceeded ? "#1A7F37" : "#C62828";
        if (pingResult.PingSucceeded)
        {
            _hostPingOfflineSince = null;
            HostPingIndicatorToolTip = $"Host '{host}' online{resolvedIpText} ({pingResult.PingRoundtripTimeMs ?? 0} ms). Click to ping now.";
            return;
        }

        _hostPingOfflineSince ??= DateTimeOffset.Now;
        var offlineSinceText = _hostPingOfflineSince.Value.ToString("yyyy-MM-dd HH:mm:ss");
        HostPingIndicatorToolTip = $"Host '{host}' offline since {offlineSinceText}{resolvedIpText} ({pingResult.PingDetail}). Click to ping now.";
    }

    private void ResetPingIndicator(string? host)
    {
        HostPingIndicatorBrush = "#8A8A8A";
        HostPingIndicatorToolTip = string.IsNullOrWhiteSpace(host)
            ? "Ping status unknown. Connect first."
            : $"Ping status for '{host}' is unknown until a connection succeeds.";
    }

    private void ApplyDisconnectedPingIndicator(string host, PingConnectivityResult pingResult)
    {
        var resolvedIpText = string.IsNullOrWhiteSpace(pingResult.ResolvedIp)
            ? string.Empty
            : $" ({pingResult.ResolvedIp})";
        HostPingIndicatorBrush = pingResult.PingSucceeded ? "#1A7F37" : "#C62828";
        if (pingResult.PingSucceeded)
        {
            _hostPingOfflineSince = null;
            HostPingIndicatorToolTip = $"Host '{host}' responded to ping{resolvedIpText} ({pingResult.PingRoundtripTimeMs ?? 0} ms), but no active connection is established.";
            return;
        }

        _hostPingOfflineSince ??= DateTimeOffset.Now;
        var offlineSinceText = _hostPingOfflineSince.Value.ToString("yyyy-MM-dd HH:mm:ss");
        HostPingIndicatorToolTip = $"Host '{host}' appears offline since {offlineSinceText}{resolvedIpText} ({pingResult.PingDetail}).";
    }

    private void ShowHostConnectionFailure(string host, string status, string detail, PingConnectivityResult pingResult, bool isOffline)
    {
        ConnectedHost = null;
        SetConnectedUsersLookupState(null, lookupCompleted: false);
        CurrentContent = BuildHostMessageContent(
            isOffline ? $"Device '{host}' is offline." : $"Connection to '{host}' failed.",
            detail);
        HostStatus = status;
        ApplyDisconnectedPingIndicator(host, pingResult);
    }

    private async Task LoadPluginsAsync(CancellationToken cancellationToken)
    {
        try
        {
            DisposeCachedViews();
            CurrentContent = null;
            var loadStopwatch = Stopwatch.StartNew();

            var nativePluginDirectory = ResolveRelativePath(_pluginOptions.NativeDirectory);
            HostStatus = $"Loading plugins from '{nativePluginDirectory}'...";
            AppendLog($"Loading plugins from '{nativePluginDirectory}'...");
            var context = new HostPluginContext(
                _logger,
                _serviceProvider,
                _runtimeOptions.Environment,
                BuildPluginContextSettings(nativePluginDirectory));

            await _pluginRegistry.LoadAsync(nativePluginDirectory, context, cancellationToken);
            RebuildNavigation();
            RebuildRibbonGroups();
            loadStopwatch.Stop();
            AppendPluginLoadSummary(nativePluginDirectory, loadStopwatch.ElapsedMilliseconds);
            if (IsVerbosePluginLoadDiagnosticsEnabled())
            {
                AppendPluginLoadDiagnostics(nativePluginDirectory);
            }
            ShowPluginLoadErrorsIfAny();
        }
        catch (Exception ex)
        {
            HostStatus = "Plugin loading failed.";
            AppendLog($"Plugin loading failed: {ex.Message}");
            if (CurrentContent is null)
            {
                CurrentContent = BuildHostMessageContent("Plugin loading failed.", ex.Message);
            }

            RunOnUiThread(() =>
            {
                var owner = System.Windows.Application.Current?.MainWindow;
                System.Windows.MessageBox.Show(
                    owner,
                    ex.Message,
                    "Plugin Load Failed",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            });
            _hostStatusLogDispatcher.Append($"[Exception][Plugin Load] {ex.GetType().Name}: {ex.Message}");
            _logger.LogError(ex, "Plugin loading failed.");
        }
    }

    private bool IsVerbosePluginLoadDiagnosticsEnabled()
    {
        return bool.TryParse(_configuration["Diagnostics:VerboseOperations"], out var enabled) && enabled;
    }

    private void AppendPluginLoadSummary(string nativePluginDirectory, long elapsedMilliseconds)
    {
        var failedCount = _pluginRegistry.LastLoadResults.Count(static result => !result.Succeeded);
        var loadedCount = _pluginRegistry.All.Count;
        AppendLog(
            $"Plugin load completed in {elapsedMilliseconds} ms: {loadedCount} loaded, {failedCount} failed from {nativePluginDirectory}.");

        foreach (var result in _pluginRegistry.LastLoadResults)
        {
            if (result.Succeeded)
            {
                var label = !string.IsNullOrWhiteSpace(result.DisplayName)
                    ? $"{result.DisplayName} ({result.PluginId})"
                    : result.ManifestFileName;
                AppendLog($"Plugin loaded in {result.ElapsedMilliseconds} ms: {label} via {result.ManifestFileName}.");
            }
            else
            {
                AppendLog($"Plugin failed in {result.ElapsedMilliseconds} ms: {result.ManifestFileName}.");
            }
        }
    }

    private Dictionary<string, string> BuildPluginContextSettings(string nativePluginDirectory)
    {
        var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["NativePluginDirectory"] = nativePluginDirectory,
            ["TargetHost"] = _targetHostService.CurrentHost
        };

        if (!string.IsNullOrWhiteSpace(_configuration["Diagnostics:VerboseOperations"]))
        {
            settings["VerboseOperations"] = _configuration["Diagnostics:VerboseOperations"]!;
        }

        foreach (var pluginSection in _configuration.GetSection("Plugins:Settings").GetChildren())
        {
            AppendPluginSettings(settings, pluginSection.Key, null, pluginSection);
        }

        return settings;
    }

    private static void AppendPluginSettings(
        IDictionary<string, string> settings,
        string pluginId,
        string? currentPath,
        IConfigurationSection section)
    {
        foreach (var child in section.GetChildren())
        {
            var childPath = string.IsNullOrWhiteSpace(currentPath)
                ? child.Key
                : $"{currentPath}:{child.Key}";

            if (child.Value is not null)
            {
                settings[$"PluginSettings:{pluginId}:{childPath}"] = child.Value;
            }

            AppendPluginSettings(settings, pluginId, childPath, child);
        }
    }

    public async Task ExecuteRibbonControlAsync(RibbonControlItem? item, string? menuItemId = null, string? menuItemText = null)
    {
        if (item is null)
        {
            return;
        }

        if (!_pluginById.TryGetValue(item.PluginId, out var loadedPlugin))
        {
            AppendLog($"Ribbon control '{item.ControlId}' is not associated with a loaded plugin.");
            return;
        }

        if (loadedPlugin.Instance is IRibbonControlPlugin ribbonControlPlugin)
        {
            try
            {
                var arguments = new Dictionary<string, string>
                {
                    ["host"] = _targetHostService.CurrentHost,
                    ["event"] = menuItemId is not null
                        ? "menu-click"
                        : item switch
                    {
                        RibbonCheckBoxItem => "toggle-changed",
                        _ => "click"
                    }
                };

                if (item is RibbonCheckBoxItem checkBoxItem)
                {
                    arguments["isChecked"] = checkBoxItem.IsChecked.ToString();
                }

                if (!string.IsNullOrWhiteSpace(menuItemId))
                {
                    arguments["menuItemId"] = menuItemId;
                }

                var result = await ribbonControlPlugin.ExecuteRibbonControlAsync(
                    item.ControlId,
                    new PluginActionContext(
                        DeviceId: null,
                        ActionName: menuItemId ?? item.ControlId,
                        Arguments: arguments),
                    CancellationToken.None);

                AppendLog(result.Success
                    ? $"Ribbon control '{DescribeRibbonControl(item, menuItemText)}' succeeded: {result.Message}"
                    : $"Ribbon control '{DescribeRibbonControl(item, menuItemText)}' failed: {result.Message}");
            }
            catch (Exception ex)
            {
                AppendLog($"Ribbon control '{DescribeRibbonControl(item, menuItemText)}' crashed: {ex.Message}");
                _logger.LogError(ex, "Ribbon control execution failed: {PluginId}/{ControlId}", item.PluginId, item.ControlId);
            }

            return;
        }

        if (item is not RibbonButtonItem buttonItem)
        {
            AppendLog($"Ribbon control '{item.ControlId}' is not supported by plugin '{item.PluginId}'.");
            return;
        }

        if (loadedPlugin.Instance is not IActionPlugin actionPlugin)
        {
            AppendLog($"Plugin '{buttonItem.Text}' is not an action plugin.");
            return;
        }

        try
        {
            var result = await actionPlugin.ExecuteAsync(
                new PluginActionContext(
                    DeviceId: null,
                    ActionName: "execute",
                    Arguments: new Dictionary<string, string>
                    {
                        ["host"] = _targetHostService.CurrentHost
                    }),
                CancellationToken.None);

            AppendLog(result.Success
                ? $"Action '{buttonItem.Text}' succeeded: {result.Message}"
                : $"Action '{buttonItem.Text}' failed: {result.Message}");
        }
        catch (Exception ex)
        {
            AppendLog($"Action '{buttonItem.Text}' crashed: {ex.Message}");
            _logger.LogError(ex, "Action plugin execution failed: {PluginId}", buttonItem.PluginId);
        }
    }

    [RelayCommand(CanExecute = nameof(CanOpenExplorerTargets))]
    public Task OpenDefaultExplorerTargetAsync()
    {
        return OpenExplorerTargetAsync(ResolveDefaultExplorerTarget());
    }

    [RelayCommand]
    public Task OpenExplorerTargetAsync(ExplorerTargetItem? target)
    {
        if (target is null)
        {
            AppendLog("Explorer launch skipped: no target selected.");
            return Task.CompletedTask;
        }

        if (!target.IsEnabled)
        {
            AppendLog($"Explorer launch skipped for '{target.Name}': target is not available for the current host.");
            return Task.CompletedTask;
        }

        if (string.IsNullOrWhiteSpace(target.ResolvedPath))
        {
            AppendLog($"Explorer launch skipped for '{target.Name}': resolved path is empty.");
            return Task.CompletedTask;
        }

        try
        {
            var startInfo = ExplorerTargeting.BuildStartInfo(target.ResolvedPath);
            _ = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start Explorer.");
            AppendLog($"Opened Explorer target '{target.Name}' -> '{target.ResolvedPath}'.");
        }
        catch (Exception ex)
        {
            AppendLog($"Explorer launch failed for '{target.Name}': {ex.Message}");
            _hostStatusLogDispatcher.Append($"[Exception][Explorer] {ex.GetType().Name}: {ex.Message}");
            _logger.LogError(ex, "Explorer launch failed for {TargetName}.", target.Name);
        }

        return Task.CompletedTask;
    }

    [RelayCommand(CanExecute = nameof(CanOpenRemoteHostTools))]
    public Task OpenRemotePowerShellAsync()
    {
        return LaunchRemoteHostToolAsync(RemoteHostToolKind.PowerShellWithProfile);
    }

    [RelayCommand(CanExecute = nameof(CanOpenRemoteHostTools))]
    public Task OpenRemotePowerShellNoProfileAsync()
    {
        return LaunchRemoteHostToolAsync(RemoteHostToolKind.PowerShellNoProfile);
    }

    [RelayCommand(CanExecute = nameof(CanOpenRemoteHostTools))]
    public Task OpenRemotePsExecAsync()
    {
        return LaunchRemoteHostToolAsync(RemoteHostToolKind.PsExec);
    }

    public void OnNavigationSelected(NavigationNode? node)
    {
        if (node?.PluginId is null)
        {
            return;
        }

        _selectedNavigationNodePath = node.NodePath;

        if (!_pluginById.TryGetValue(node.PluginId, out var loadedPlugin))
        {
            return;
        }

        if (loadedPlugin.Instance is not IViewPlugin viewPlugin)
        {
            return;
        }

        try
        {
            if (viewPlugin is INavigationAwareViewPlugin navigationAwareViewPlugin)
            {
                navigationAwareViewPlugin.SetNavigationTarget(node.NavigationTarget);
            }

            if (!_viewByPluginId.TryGetValue(node.PluginId, out var view))
            {
                view = viewPlugin.CreateView();
                _viewByPluginId[node.PluginId] = view;
            }

            CurrentContent = view;
        }
        catch (Exception ex)
        {
            CurrentContent = new TextBlock
            {
                Text = $"Plugin failed: {ex.Message}",
                Margin = new Thickness(12)
            };
            AppendLog($"View plugin failed: {ex.Message}");
            _hostStatusLogDispatcher.Append($"[Exception][View Plugin:{loadedPlugin.Manifest.Id}] {ex.GetType().Name}: {ex.Message}");
            _logger.LogError(ex, "View plugin selection failed for {PluginId}", loadedPlugin.Manifest.Id);
        }
    }

    public bool TrySelectNavigationPath(string menuPath)
    {
        if (string.IsNullOrWhiteSpace(menuPath))
        {
            return false;
        }

        var node = FindNavigationNodeByMenuPath(menuPath);
        if (node?.PluginId is null)
        {
            return false;
        }

        ExpandNavigationPath(node);
        node.IsSelected = true;
        OnNavigationSelected(node);
        return true;
    }

    private void RestoreSelectedNavigationAfterConnect()
    {
        var selectedNode = ResolveSelectedNavigationNode();
        if (selectedNode is null)
        {
            return;
        }

        OnNavigationSelected(selectedNode);
    }

    private NavigationNode? ResolveSelectedNavigationNode()
    {
        if (!string.IsNullOrWhiteSpace(_selectedNavigationNodePath))
        {
            var selectedNode = EnumerateNavigationNodes(Navigation)
                .FirstOrDefault(node =>
                    string.Equals(node.NodePath, _selectedNavigationNodePath, StringComparison.OrdinalIgnoreCase) &&
                    node.PluginId is not null);
            if (selectedNode is not null)
            {
                return selectedNode;
            }
        }

        return Navigation.Count == 0
            ? null
            : FindFirstLeaf(Navigation[0]);
    }

    private NavigationNode? FindNavigationNodeByMenuPath(string menuPath)
    {
        var parts = menuPath
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return null;
        }

        var level = Navigation.AsEnumerable();
        NavigationNode? current = null;
        foreach (var part in parts)
        {
            current = level.FirstOrDefault(node => node.Title.Equals(part, StringComparison.OrdinalIgnoreCase));
            if (current is null)
            {
                return null;
            }

            level = current.Children;
        }

        return current;
    }

    private static void ExpandNavigationPath(NavigationNode node)
    {
        node.IsExpanded = true;
    }

    private async Task LoadUserSettingsAsync(CancellationToken cancellationToken)
    {
        var settings = await _userSettingsStore.LoadAsync(cancellationToken);
        _navigationExpansionStateByPath.Clear();
        foreach (var state in settings.NavigationStates ?? [])
        {
            if (!string.IsNullOrWhiteSpace(state.NodePath))
            {
                _navigationExpansionStateByPath[state.NodePath.Trim()] = state.IsExpanded;
            }
        }

        RecentHosts.Clear();

        foreach (var host in (settings.RecentHosts ?? []).Where(h => !string.IsNullOrWhiteSpace(h)).Take(MaxRecentHosts))
        {
            RecentHosts.Add(host);
        }
    }

    private async Task SaveUserSettingsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _userSettingsSaveGate.WaitAsync(cancellationToken);
            try
            {
                var settings = new HostUserSettings(
                    RecentHosts.ToList(),
                    CaptureNavigationStates().ToList());
                await _userSettingsStore.SaveAsync(settings, cancellationToken);
                _navigationExpansionStateByPath.Clear();
                foreach (var state in settings.NavigationStates ?? [])
                {
                    if (!string.IsNullOrWhiteSpace(state.NodePath))
                    {
                        _navigationExpansionStateByPath[state.NodePath.Trim()] = state.IsExpanded;
                    }
                }
            }
            finally
            {
                _userSettingsSaveGate.Release();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save host user settings.");
        }
    }

    private void AddRecentHost(string host)
    {
        var existing = RecentHosts.FirstOrDefault(h => h.Equals(host, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            RecentHosts.Remove(existing);
        }

        RecentHosts.Insert(0, host);

        while (RecentHosts.Count > MaxRecentHosts)
        {
            RecentHosts.RemoveAt(RecentHosts.Count - 1);
        }
    }

    private void RebuildNavigation()
    {
        Navigation.Clear();
        _pluginById.Clear();

        foreach (var plugin in _pluginRegistry.ViewPlugins)
        {
            _pluginById[plugin.Manifest.Id] = plugin;
            if (plugin.Instance is INavigationMenuPlugin navigationMenuPlugin)
            {
                var navigationEntries = navigationMenuPlugin.GetNavigationEntries()
                    .Where(static entry => !string.IsNullOrWhiteSpace(entry.MenuPath))
                    .ToArray();
                var rootNavigationTarget = ResolveRootNavigationTarget(navigationEntries);

                AddNavigationNode(
                    plugin.Manifest.MenuPath,
                    plugin.Manifest.Id,
                    navigationTarget: rootNavigationTarget,
                    iconGlyph: null,
                    isExpanded: true);

                foreach (var entry in navigationEntries)
                {
                    AddNavigationNode(
                        entry.MenuPath,
                        plugin.Manifest.Id,
                        entry.NavigationTarget,
                        entry.IconGlyph,
                        entry.IsExpanded,
                        entry.IsContainerOnly);
                }
            }
            else
            {
                AddNavigationNode(plugin.Manifest.MenuPath, plugin.Manifest.Id, navigationTarget: null, iconGlyph: null, isExpanded: true);
            }
        }

        if (Navigation.Count > 0)
        {
            SubscribeNavigationStateTracking();
            var firstLeaf = FindFirstLeaf(Navigation[0]);
            OnNavigationSelected(firstLeaf);
        }
    }

    private static string? ResolveRootNavigationTarget(IReadOnlyList<PluginNavigationEntry> entries)
    {
        var overviewEntry = entries.FirstOrDefault(static entry =>
            !string.IsNullOrWhiteSpace(entry.NavigationTarget) &&
            entry.NavigationTarget.Equals("overview", StringComparison.OrdinalIgnoreCase));
        if (overviewEntry is not null)
        {
            return overviewEntry.NavigationTarget;
        }

        return entries
            .Select(static entry => entry.NavigationTarget)
            .FirstOrDefault(static target => !string.IsNullOrWhiteSpace(target));
    }

    private void RebuildRibbonGroups()
    {
        RibbonGroups.Clear();
        PowerShellRibbonControls.Clear();

        foreach (var plugin in _pluginRegistry.All)
        {
            if (plugin.Instance is not IRibbonControlPlugin ribbonControlPlugin)
            {
                continue;
            }

            _pluginById[plugin.Manifest.Id] = plugin;
            var groups = ribbonControlPlugin.GetRibbonGroups();
            foreach (var group in groups)
            {
                var usePowerShellHostSlot = string.Equals(plugin.Manifest.Id, "powershell-scripts", StringComparison.OrdinalIgnoreCase);
                var groupItem = usePowerShellHostSlot
                    ? null
                    : GetOrCreateRibbonGroup(
                        $"{plugin.Manifest.Id}:{group.GroupId}",
                        group.Title,
                        ParseBrushOrDefault(group.Background),
                        ParseBrushOrDefault(group.BorderBrush),
                        ParseBrushOrDefault(group.TitleForeground));

                foreach (var control in group.Controls)
                {
                    var controlItem = CreateRibbonControlItem(plugin.Manifest.Id, group, control);

                    if (controlItem is not null)
                    {
                        if (usePowerShellHostSlot)
                        {
                            PowerShellRibbonControls.Add(controlItem);
                        }
                        else
                        {
                            groupItem!.Controls.Add(controlItem);
                        }
                    }
                }
            }
        }

        foreach (var plugin in _pluginRegistry.ActionPlugins)
        {
            _pluginById[plugin.Manifest.Id] = plugin;
            var groupTitle = plugin.Manifest.MenuPath.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "Actions";
            var group = RibbonGroups.FirstOrDefault(item => string.Equals(item.Title, groupTitle, StringComparison.OrdinalIgnoreCase));
            if (group is null)
            {
                group = new RibbonGroupItem($"action:{groupTitle}", groupTitle, null, null, null);
                RibbonGroups.Add(group);
            }

            var buttonItem = new RibbonButtonItem(plugin.Manifest.Id, "execute", plugin.Manifest.DisplayName, null, 150, 30, null, new Thickness(10, 2, 10, 2));
            buttonItem.Command = new AsyncRelayCommand(() => ExecuteRibbonControlAsync(buttonItem));
            group.Controls.Add(buttonItem);
        }

        UpdateRibbonControlAvailability();
    }

    private static System.Windows.Media.Brush? ParseBrushOrDefault(string? brushValue)
    {
        if (string.IsNullOrWhiteSpace(brushValue))
        {
            return null;
        }

        try
        {
            return (System.Windows.Media.Brush?)new BrushConverter().ConvertFromString(brushValue);
        }
        catch
        {
            return null;
        }
    }

    private RibbonGroupItem GetOrCreateRibbonGroup(
        string groupId,
        string title,
        System.Windows.Media.Brush? background,
        System.Windows.Media.Brush? borderBrush,
        System.Windows.Media.Brush? titleForeground)
    {
        var existing = RibbonGroups.FirstOrDefault(item => string.Equals(item.Title, title, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            return existing;
        }

        var group = new RibbonGroupItem(groupId, title, background, borderBrush, titleForeground);
        RibbonGroups.Add(group);
        return group;
    }

    private RibbonControlItem? CreateRibbonControlItem(
        string pluginId,
        PluginRibbonGroup group,
        PluginRibbonControl control)
    {
        var padding = new Thickness(
            control.HorizontalPadding ?? group.DefaultControlHorizontalPadding ?? 10,
            control.VerticalPadding ?? group.DefaultControlVerticalPadding ?? 2,
            control.HorizontalPadding ?? group.DefaultControlHorizontalPadding ?? 10,
            control.VerticalPadding ?? group.DefaultControlVerticalPadding ?? 2);

        RibbonControlItem? controlItem = control.Kind switch
        {
            PluginRibbonControlKind.Button => new RibbonButtonItem(
                pluginId,
                control.ControlId,
                control.Text ?? control.ControlId,
                control.Width,
                control.MinWidth ?? group.DefaultControlMinWidth,
                control.Height ?? group.DefaultControlHeight,
                control.FontSize ?? group.DefaultControlFontSize,
                padding,
                control.RequiresConnectedHost),
            PluginRibbonControlKind.CheckBox => new RibbonCheckBoxItem(
                pluginId,
                control.ControlId,
                control.Text ?? control.ControlId,
                control.IsChecked ?? false,
                control.Width,
                control.MinWidth ?? group.DefaultControlMinWidth,
                control.Height ?? group.DefaultControlHeight,
                control.FontSize ?? group.DefaultControlFontSize,
                padding,
                control.RequiresConnectedHost),
            PluginRibbonControlKind.Label => new RibbonLabelItem(
                pluginId,
                control.ControlId,
                control.Text ?? string.Empty,
                control.Width,
                control.MinWidth ?? group.DefaultControlMinWidth,
                control.Height ?? group.DefaultControlHeight,
                control.FontSize ?? group.DefaultControlFontSize,
                padding,
                control.RequiresConnectedHost),
            PluginRibbonControlKind.Separator => new RibbonSeparatorItem(
                pluginId,
                control.ControlId,
                control.Height ?? group.DefaultControlHeight ?? 24),
            PluginRibbonControlKind.MenuButton => new RibbonMenuButtonItem(
                pluginId,
                control.ControlId,
                control.Text ?? control.ControlId,
                control.Width,
                control.MinWidth ?? group.DefaultControlMinWidth,
                control.Height ?? group.DefaultControlHeight,
                control.FontSize ?? group.DefaultControlFontSize,
                padding,
                control.RequiresConnectedHost),
            _ => null
        };

        if (controlItem is null)
        {
            return null;
        }

        switch (controlItem)
        {
            case RibbonButtonItem buttonItem:
                buttonItem.Command = new AsyncRelayCommand(() => ExecuteRibbonControlAsync(buttonItem));
                break;
            case RibbonCheckBoxItem checkBoxItem:
                checkBoxItem.Command = new AsyncRelayCommand(() => ExecuteRibbonControlAsync(checkBoxItem));
                break;
            case RibbonMenuButtonItem menuButtonItem:
                foreach (var menuItem in control.MenuItems ?? [])
                {
                    menuButtonItem.MenuItems.Add(CreateRibbonMenuEntryItem(menuButtonItem, menuItem));
                }
                break;
        }

        return controlItem;
    }

    private RibbonMenuEntryItem CreateRibbonMenuEntryItem(RibbonControlItem controlItem, PluginRibbonMenuItem menuItem)
    {
        var entryItem = new RibbonMenuEntryItem(menuItem.Text, controlItem.RequiresConnectedHost);
        if (menuItem.Children is { Count: > 0 })
        {
            foreach (var child in menuItem.Children)
            {
                entryItem.Children.Add(CreateRibbonMenuEntryItem(controlItem, child));
            }
        }
        else
        {
            entryItem.Command = new AsyncRelayCommand(() => ExecuteRibbonControlAsync(controlItem, menuItem.ItemId, menuItem.Text));
        }

        return entryItem;
    }

    private void UpdateRibbonControlAvailability()
    {
        var hasConnectedHost = CanOpenRemoteHostTools;
        foreach (var control in PowerShellRibbonControls)
        {
            control.UpdateIsEnabled(hasConnectedHost);
        }

        foreach (var group in RibbonGroups)
        {
            foreach (var control in group.Controls)
            {
                control.UpdateIsEnabled(hasConnectedHost);
            }
        }
    }

    private static string DescribeRibbonControl(RibbonControlItem item, string? menuItemText = null)
    {
        if (!string.IsNullOrWhiteSpace(menuItemText))
        {
            return menuItemText;
        }

        return item switch
        {
            RibbonButtonItem button => button.Text,
            RibbonCheckBoxItem checkBox => checkBox.Text,
            RibbonLabelItem label => label.Text,
            RibbonMenuButtonItem menuButton => menuButton.Text,
            _ => item.ControlId
        };
    }

    private void AddNavigationNode(
        string menuPath,
        string pluginId,
        string? navigationTarget,
        string? iconGlyph,
        bool? isExpanded,
        bool isContainerOnly = false)
    {
        var parts = menuPath.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return;
        }

        ObservableCollection<NavigationNode> level = Navigation;
        var pathSegments = new List<string>(parts.Length);

        for (var i = 0; i < parts.Length; i++)
        {
            var title = parts[i];
            pathSegments.Add(title);
            var nodePath = $"{pluginId}::{string.Join('/', pathSegments)}";
            var existing = level.FirstOrDefault(n => n.Title.Equals(title, StringComparison.OrdinalIgnoreCase));
            var isLeaf = i == parts.Length - 1;
            if (existing is null)
            {
                var defaultIcon = ResolveDefaultIconGlyph(title, isLeaf);
                existing = new NavigationNode(
                    title,
                    nodePath,
                    isLeaf && !isContainerOnly ? pluginId : null,
                    isLeaf && !isContainerOnly ? navigationTarget : null,
                    isLeaf ? iconGlyph ?? defaultIcon : defaultIcon);
                level.Add(existing);
            }
            else if (isLeaf && !isContainerOnly)
            {
                existing.AssignPlugin(pluginId, navigationTarget, iconGlyph ?? ResolveDefaultIconGlyph(title, isLeaf: true));
            }

            if (isLeaf && !string.IsNullOrWhiteSpace(iconGlyph))
            {
                existing.AssignIcon(iconGlyph);
            }

            level = existing.Children;

            var resolvedExpanded = ResolveNavigationExpansionState(nodePath, isExpanded);
            if (existing.IsExpanded != resolvedExpanded)
            {
                existing.IsExpanded = resolvedExpanded;
            }
        }
    }

    private bool ResolveNavigationExpansionState(string nodePath, bool? defaultState)
    {
        if (_navigationExpansionStateByPath.TryGetValue(nodePath, out var savedState))
        {
            return savedState;
        }

        return defaultState ?? true;
    }

    private void SubscribeNavigationStateTracking()
    {
        foreach (var node in EnumerateNavigationNodes(Navigation))
        {
            node.PropertyChanged += NavigationNodeOnPropertyChanged;
        }
    }

    private void NavigationNodeOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not NavigationNode node ||
            !string.Equals(e.PropertyName, nameof(NavigationNode.IsExpanded), StringComparison.Ordinal))
        {
            return;
        }

        _ = SaveUserSettingsAsync(CancellationToken.None);
    }

    private IEnumerable<NavigationNodeState> CaptureNavigationStates()
    {
        foreach (var node in EnumerateNavigationNodes(Navigation))
        {
            yield return new NavigationNodeState(node.NodePath, node.IsExpanded);
        }
    }

    private static IEnumerable<NavigationNode> EnumerateNavigationNodes(IEnumerable<NavigationNode> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node;
            foreach (var child in EnumerateNavigationNodes(node.Children))
            {
                yield return child;
            }
        }
    }

    private static string ResolveDefaultIconGlyph(string title, bool isLeaf)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return isLeaf ? "\uE8A5" : "\uE8B7";
        }

        return title.Trim().ToLowerInvariant() switch
        {
            "devices" => "\uE7F4",
            "intune agent" => "\uE8D4",
            "windows update agent" => "\uE895",
            "overview" => "\uE80F",
            "local diagnostics" => "\uE9D9",
            "mdm events" => "\uE7BA",
            "enrollment" => "\uE8B7",
            "available updates" => "\uE823",
            "update history" => "\uE81C",
            "reportingevents.log" => "\uE9D9",
            "logs" => "\uE9D9",
            "cloud" => "\uE753",
            "actions" => "\uE7C3",
            _ => isLeaf ? "\uE8A5" : "\uE8B7"
        };
    }

    private static NavigationNode? FindFirstLeaf(NavigationNode root)
    {
        if (root.PluginId is not null)
        {
            return root;
        }

        foreach (var child in root.Children)
        {
            var leaf = FindFirstLeaf(child);
            if (leaf is not null)
            {
                return leaf;
            }
        }

        return null;
    }

    private string ResolveRelativePath(string path)
    {
        if (Path.IsPathRooted(path))
        {
            return path;
        }

        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, path));
    }

    public void Append(string message)
    {
        AppendLog(message);
    }

    public void RequestRibbonRefresh(string pluginId)
    {
        if (string.IsNullOrWhiteSpace(pluginId))
        {
            return;
        }

        RunOnUiThread(() =>
        {
            RebuildRibbonGroups();
            AppendLog($"Ribbon controls refreshed for plugin '{pluginId}'.");
        });
    }

    private Task LaunchRemoteHostToolAsync(RemoteHostToolKind toolKind)
    {
        var host = _targetHostService.CurrentHost?.Trim();
        if (string.IsNullOrWhiteSpace(host))
        {
            AppendLog("Remote host tool launch skipped: no host selected.");
            return Task.CompletedTask;
        }

        try
        {
            var startInfo = toolKind switch
            {
                RemoteHostToolKind.PowerShellWithProfile => BuildPowerShellSessionStartInfo(host, noProfile: false),
                RemoteHostToolKind.PowerShellNoProfile => BuildPowerShellSessionStartInfo(host, noProfile: true),
                RemoteHostToolKind.PsExec => BuildPsExecStartInfo(host),
                _ => throw new InvalidEnumArgumentException(nameof(toolKind), (int)toolKind, typeof(RemoteHostToolKind))
            };

            _ = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start remote host tool process.");
            AppendLog(toolKind switch
            {
                RemoteHostToolKind.PowerShellWithProfile => $"Opened remote PowerShell session launcher for '{host}'.",
                RemoteHostToolKind.PowerShellNoProfile => $"Opened remote PowerShell (NoProfile) session launcher for '{host}'.",
                RemoteHostToolKind.PsExec => $"Opened PsExec launcher for '{host}'.",
                _ => $"Opened remote tool for '{host}'."
            });
        }
        catch (Exception ex)
        {
            AppendLog($"Remote host tool launch failed for '{host}': {ex.Message}");
            _hostStatusLogDispatcher.Append($"[Exception][Remote Host Tool] {ex.GetType().Name}: {ex.Message}");
            _logger.LogError(ex, "Remote host tool launch failed for {Host}.", host);
        }

        return Task.CompletedTask;
    }

    private static ProcessStartInfo BuildPowerShellSessionStartInfo(string host, bool noProfile)
    {
        var noProfileArgument = noProfile ? "-NoProfile " : string.Empty;
        var script = $"Enter-PSSession -ComputerName '{EscapeSingleQuotedPowerShellLiteral(host)}'";
        return new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"{noProfileArgument}-NoExit -Command \"{script}\"",
            UseShellExecute = true
        };
    }

    private static ProcessStartInfo BuildPsExecStartInfo(string host)
    {
        return new ProcessStartInfo
        {
            FileName = "psexec.exe",
            Arguments = $"\\\\{host} cmd.exe",
            UseShellExecute = true
        };
    }

    private static string EscapeSingleQuotedPowerShellLiteral(string value)
    {
        return value.Replace("'", "''", StringComparison.Ordinal);
    }

    private void RebuildExplorerTargets()
    {
        ExplorerTargets.Clear();

        foreach (var target in ExplorerTargeting.BuildTargets(_explorerOptions.Targets, CurrentHost))
        {
            ExplorerTargets.Add(target);
        }
    }

    private ExplorerTargetItem? ResolveDefaultExplorerTarget()
    {
        return ExplorerTargeting.ResolveDefaultTarget(ExplorerTargets);
    }

    private enum RemoteHostToolKind
    {
        PowerShellWithProfile,
        PowerShellNoProfile,
        PsExec
    }

    public void SetBusyState(string ownerId, string shortStatus, IReadOnlyList<string>? tasks = null)
    {
        if (string.IsNullOrWhiteSpace(ownerId))
        {
            return;
        }

        var normalizedOwnerId = ownerId.Trim();
        var normalizedStatus = string.IsNullOrWhiteSpace(shortStatus) ? normalizedOwnerId : shortStatus.Trim();
        var normalizedTasks = (tasks ?? [])
            .Where(static task => !string.IsNullOrWhiteSpace(task))
            .Select(static task => task.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        lock (_busyStateSync)
        {
            _busyStates[normalizedOwnerId] = new HostBusyState(
                normalizedOwnerId,
                normalizedStatus,
                normalizedTasks,
                DateTimeOffset.UtcNow);
        }

        RefreshBusyIndicator();
    }

    public void ClearBusyState(string ownerId)
    {
        if (string.IsNullOrWhiteSpace(ownerId))
        {
            return;
        }

        lock (_busyStateSync)
        {
            _busyStates.Remove(ownerId.Trim());
        }

        RefreshBusyIndicator();
    }

    private void DisposeCachedViews()
    {
        foreach (var view in _viewByPluginId.Values)
        {
            if (view is FrameworkElement { DataContext: IDisposable disposableDataContext })
            {
                disposableDataContext.Dispose();
            }
            else if (view is IDisposable disposableView)
            {
                disposableView.Dispose();
            }
        }

        _viewByPluginId.Clear();
    }

    private void AppendLog(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        if (System.Windows.Application.Current.Dispatcher.CheckAccess())
        {
            _logBuilder.AppendLine(line);
            LogText = _logBuilder.ToString();
            return;
        }

        _ = System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
        {
            _logBuilder.AppendLine(line);
            LogText = _logBuilder.ToString();
        });
    }

    private void RefreshBusyIndicator()
    {
        HostBusyState[] activeStates;
        lock (_busyStateSync)
        {
            activeStates = _busyStates.Values
                .OrderByDescending(static state => state.UpdatedAtUtc)
                .ToArray();
        }

        RunOnUiThread(() =>
        {
            if (activeStates.Length == 0)
            {
                IsHostBusyVisible = false;
                HostBusyShortStatus = string.Empty;
                HostBusyToolTip = string.Empty;
                return;
            }

            var primaryState = activeStates[0];
            IsHostBusyVisible = true;
            HostBusyShortStatus = activeStates.Length == 1
                ? primaryState.ShortStatus
                : $"{activeStates.Length} tasks running";

            var toolTipLines = new List<string>(capacity: activeStates.Length * 4);
            foreach (var state in activeStates)
            {
                toolTipLines.Add(activeStates.Length == 1
                    ? state.ShortStatus
                    : $"{state.OwnerId}: {state.ShortStatus}");

                foreach (var task in state.Tasks)
                {
                    toolTipLines.Add($"- {task}");
                }
            }

            HostBusyToolTip = string.Join(Environment.NewLine, toolTipLines);
        });
    }

    partial void OnIsHostBusyVisibleChanged(bool value)
    {
        UpdateTaskbarBusyAnimation(value);
    }

    private void UpdateTaskbarBusyAnimation(bool isVisible)
    {
        if (!isVisible || _busyOverlayFrames.Length == 0)
        {
            _busyOverlayTimer.Stop();
            _busyOverlayFrameIndex = 0;
            _busyOverlayPauseTicksRemaining = 0;
            TaskbarOverlayImage = null;
            return;
        }

        _busyOverlayFrameIndex = 0;
        _busyOverlayPauseTicksRemaining = 0;
        TaskbarOverlayImage = _busyOverlayFrames[0];
        if (!_busyOverlayTimer.IsEnabled)
        {
            _busyOverlayTimer.Start();
        }
    }

    private void OnBusyOverlayTimerTick(object? sender, EventArgs e)
    {
        if (!IsHostBusyVisible || _busyOverlayFrames.Length == 0)
        {
            return;
        }

        if (_busyOverlayPauseTicksRemaining > 0)
        {
            _busyOverlayPauseTicksRemaining--;
            return;
        }

        _busyOverlayFrameIndex = (_busyOverlayFrameIndex + 1) % _busyOverlayFrames.Length;
        TaskbarOverlayImage = _busyOverlayFrames[_busyOverlayFrameIndex];

        if (_busyOverlayFrameIndex == _busyOverlayFrames.Length / 2)
        {
            _busyOverlayPauseTicksRemaining = 8;
        }
    }

    private static ImageSource[] CreateBusyOverlayFrames()
    {
        var frames = new ImageSource[BusyOverlayFrameCount];
        for (var frameIndex = 0; frameIndex < frames.Length; frameIndex++)
        {
            frames[frameIndex] = CreateBusyOverlayFrame(frameIndex, frames.Length);
        }

        return frames;
    }

    private static ImageSource CreateBusyOverlayFrame(int frameIndex, int frameCount)
    {
        const double size = 20.0;
        const double center = size / 2.0;
        const double baseMargin = 1.2;

        var progress = frameCount <= 1 ? 0.0 : (double)frameIndex / (frameCount - 1);
        var rotation = 360.0 * progress;

        var badgeBrush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(225, 16, 24, 36));
        badgeBrush.Freeze();

        var badgeEdgeBrush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(165, 255, 255, 255));
        badgeEdgeBrush.Freeze();

        var frameBrush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(225, 248, 248, 248));
        frameBrush.Freeze();
        var framePen = new System.Windows.Media.Pen(frameBrush, 1.35);
        framePen.Freeze();

        var sandBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 198, 0));
        sandBrush.Freeze();
        var sandGlowBrush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(125, 255, 198, 0));
        sandGlowBrush.Freeze();
        var sandHighlightBrush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(175, 255, 242, 180));
        sandHighlightBrush.Freeze();

        var outerHighlightPen = new System.Windows.Media.Pen(new SolidColorBrush(System.Windows.Media.Color.FromArgb(95, 255, 255, 255)), 0.9);
        outerHighlightPen.Freeze();

        static StreamGeometry CreateTriangleGeometry(System.Windows.Point topLeft, System.Windows.Point topRight, System.Windows.Point apex)
        {
            var geometry = new StreamGeometry();
            using (var context = geometry.Open())
            {
                context.BeginFigure(topLeft, true, true);
                context.LineTo(topRight, true, false);
                context.LineTo(apex, true, false);
            }

            geometry.Freeze();
            return geometry;
        }

        var drawingGroup = new DrawingGroup();
        drawingGroup.Children.Add(new GeometryDrawing(badgeBrush, null, new RectangleGeometry(new Rect(baseMargin, baseMargin, size - (baseMargin * 2.0), size - (baseMargin * 2.0)), 4.0, 4.0)));
        drawingGroup.Children.Add(new GeometryDrawing(null, new System.Windows.Media.Pen(badgeEdgeBrush, 1.0), new RectangleGeometry(new Rect(baseMargin, baseMargin, size - (baseMargin * 2.0), size - (baseMargin * 2.0)), 4.0, 4.0)));

        var rotatingGroup = new DrawingGroup
        {
            Transform = new RotateTransform(rotation, center, center)
        };

        var topTriangleOutline = CreateTriangleGeometry(
            new System.Windows.Point(6.1, 4.6),
            new System.Windows.Point(13.9, 4.6),
            new System.Windows.Point(center, 10.0));
        var bottomTriangleOutline = CreateTriangleGeometry(
            new System.Windows.Point(6.1, 15.4),
            new System.Windows.Point(13.9, 15.4),
            new System.Windows.Point(center, 10.0));

        var topSandApexY = 6.1 + (2.1 * (1.0 - progress));
        var bottomSandApexY = 13.9 - (2.4 * progress);
        var particleY = 7.3 + (4.8 * progress);

        var topSandGeometry = CreateTriangleGeometry(
            new System.Windows.Point(6.8, 5.2),
            new System.Windows.Point(13.2, 5.2),
            new System.Windows.Point(center, topSandApexY));
        var bottomSandGeometry = CreateTriangleGeometry(
            new System.Windows.Point(6.8, 14.8),
            new System.Windows.Point(13.2, 14.8),
            new System.Windows.Point(center, bottomSandApexY));
        var particleGeometry = new EllipseGeometry(new System.Windows.Point(center, particleY), 0.75, 0.75);

        rotatingGroup.Children.Add(new GeometryDrawing(null, framePen, topTriangleOutline));
        rotatingGroup.Children.Add(new GeometryDrawing(null, framePen, bottomTriangleOutline));
        rotatingGroup.Children.Add(new GeometryDrawing(sandGlowBrush, null, topSandGeometry));
        rotatingGroup.Children.Add(new GeometryDrawing(sandBrush, null, topSandGeometry));
        rotatingGroup.Children.Add(new GeometryDrawing(sandGlowBrush, null, bottomSandGeometry));
        rotatingGroup.Children.Add(new GeometryDrawing(sandBrush, null, bottomSandGeometry));
        rotatingGroup.Children.Add(new GeometryDrawing(sandHighlightBrush, null, particleGeometry));
        rotatingGroup.Children.Add(new GeometryDrawing(null, outerHighlightPen, new LineGeometry(new System.Windows.Point(7.2, 5.7), new System.Windows.Point(12.8, 5.7))));
        rotatingGroup.Children.Add(new GeometryDrawing(null, outerHighlightPen, new LineGeometry(new System.Windows.Point(7.2, 14.3), new System.Windows.Point(12.8, 14.3))));

        drawingGroup.Children.Add(rotatingGroup);
        drawingGroup.Freeze();

        var image = new DrawingImage(drawingGroup);
        image.Freeze();
        return image;
    }

    private static void RunOnUiThread(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return;
        }

        _ = dispatcher.InvokeAsync(action);
    }

    private void AppendPluginLoadDiagnostics(string nativePluginDirectory)
    {
        var pluginAssemblyPaths = new HashSet<string>(
            _pluginRegistry.All.Select(p => Path.GetFullPath(p.AssemblyPath)),
            StringComparer.OrdinalIgnoreCase);

        foreach (var plugin in _pluginRegistry.All.OrderBy(p => p.Manifest.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            var metadata = ReadAssemblyMetadata(plugin.AssemblyPath);
            AppendLog(
                $"Plugin '{plugin.Manifest.DisplayName}' ({plugin.Manifest.Id}) " +
                $"manifest={plugin.Manifest.Version}, asm={metadata.AssemblyVersion}, " +
                $"file={metadata.FileVersion}, sha256={metadata.ShortHash}");
        }

        IEnumerable<string> dllPaths;
        try
        {
            dllPaths = Directory.EnumerateFiles(nativePluginDirectory, "*.dll", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFullPath)
                .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception ex)
        {
            AppendLog($"DLL inventory failed for '{nativePluginDirectory}': {ex.Message}");
            return;
        }

        foreach (var dllPath in dllPaths)
        {
            if (pluginAssemblyPaths.Contains(dllPath))
            {
                continue;
            }

            if (!IsRelevantPluginDll(dllPath))
            {
                continue;
            }

            var metadata = ReadAssemblyMetadata(dllPath);
            AppendLog(
                $"DLL '{Path.GetFileName(dllPath)}' asm={metadata.AssemblyVersion}, " +
                $"file={metadata.FileVersion}, sha256={metadata.ShortHash}");
        }
    }

    private void ShowPluginLoadErrorsIfAny()
    {
        var failedCount = _pluginRegistry.LastLoadResults.Count(static result => !result.Succeeded);
        if (failedCount == 0)
        {
            return;
        }

        HostStatus = $"{_pluginRegistry.All.Count} plugins loaded, {failedCount} failed. See status log.";
    }

    private static bool IsRelevantPluginDll(string dllPath)
    {
        var name = Path.GetFileName(dllPath);
        return name.StartsWith("Plugins.", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("Intune.", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("Plugin.", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("WindowsClientCenter.", StringComparison.OrdinalIgnoreCase);
    }

    private static (string AssemblyVersion, string FileVersion, string ShortHash) ReadAssemblyMetadata(string assemblyPath)
    {
        var assemblyVersion = "n/a";
        var fileVersion = "n/a";
        var shortHash = "n/a";

        try
        {
            assemblyVersion = AssemblyName.GetAssemblyName(assemblyPath).Version?.ToString() ?? "n/a";
        }
        catch
        {
            // Keep n/a if assembly metadata cannot be read.
        }

        try
        {
            var versionInfo = FileVersionInfo.GetVersionInfo(assemblyPath);
            fileVersion = string.IsNullOrWhiteSpace(versionInfo.FileVersion) ? "n/a" : versionInfo.FileVersion;
        }
        catch
        {
            // Keep n/a if file version metadata cannot be read.
        }

        try
        {
            using var stream = File.OpenRead(assemblyPath);
            var hashBytes = SHA256.HashData(stream);
            shortHash = Convert.ToHexString(hashBytes)[..12];
        }
        catch
        {
            // Keep n/a if hash cannot be computed.
        }

        return (assemblyVersion, fileVersion, shortHash);
    }

    private sealed record PingConnectivityResult(
        bool PingSucceeded,
        long? PingRoundtripTimeMs,
        string PingDetail,
        string? ResolvedIp);

    private sealed record HostBusyState(
        string OwnerId,
        string ShortStatus,
        IReadOnlyList<string> Tasks,
        DateTimeOffset UpdatedAtUtc);
}
