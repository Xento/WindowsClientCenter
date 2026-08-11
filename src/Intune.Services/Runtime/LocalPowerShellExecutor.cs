using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Management.Automation;
using System.Management.Automation.Remoting;
using System.Management.Automation.Runspaces;
using System.Runtime.InteropServices;
using System.Text;
using WindowsClientCenter.Intune.Services.Contracts;
using WindowsClientCenter.Plugin.Abstractions.Contracts;

namespace WindowsClientCenter.Intune.Services.Runtime;

internal sealed class LocalPowerShellExecutor : IPowerShellExecutor, IDisposable
{
    private const string LocalSessionKey = "__local__";
    private const string PoolBusyOwnerId = "runtime.powershell.sessions";
    private readonly ConcurrentDictionary<string, PowerShellRunspacePool> _pools = new(StringComparer.OrdinalIgnoreCase);
    private readonly IHostConnectivityService _hostConnectivityService;
    private readonly ITargetHostService? _targetHostService;
    private readonly ConcurrentDictionary<string, PoolPressureSnapshot> _poolPressure = new(StringComparer.OrdinalIgnoreCase);
    private readonly int _maxSessionsPerHost;
    private readonly Func<IHostBusyStateSink?> _hostBusyStateSinkAccessor;
    private IHostBusyStateSink? _hostBusyStateSink;
    private string _lastKnownHost;
    private bool _disposed;

    public int ActiveSessionCount => _pools.Values.Sum(static pool => pool.TotalSessionCount);

    public LocalPowerShellExecutor(
        IHostConnectivityService hostConnectivityService,
        ITargetHostService? targetHostService = null,
        IntuneRuntimeOptions? options = null,
        IHostStatusLogSink? hostStatusLogSink = null,
        Func<IHostBusyStateSink?>? hostBusyStateSinkAccessor = null)
    {
        _hostConnectivityService = hostConnectivityService;
        _targetHostService = targetHostService;
        _maxSessionsPerHost = Math.Max(1, options?.PowerShellSessionPoolSize ?? 5);
        _hostBusyStateSinkAccessor = hostBusyStateSinkAccessor ?? (() => null);
        _lastKnownHost = targetHostService?.CurrentHost?.Trim() ?? string.Empty;
        if (_targetHostService is not null)
        {
            _targetHostService.HostChanged += OnTargetHostChanged;
        }
    }

    public async ValueTask<PowershellExecutionResult> ExecuteForHostAsync(string host, string scriptBody, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_disposed)
        {
            return new PowershellExecutionResult(1, string.Empty, "PowerShell executor is already disposed.");
        }

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return new PowershellExecutionResult(1, string.Empty, "PowerShell execution is only supported on Windows hosts.");
        }

        if (string.IsNullOrWhiteSpace(host))
        {
            return new PowershellExecutionResult(1, string.Empty, "Client is not connected. Click Connect first.");
        }

        var normalizedHost = host.Trim();
        var isLocal = IsLocalHost(normalizedHost);
        if (!isLocal)
        {
            var connectivity = await _hostConnectivityService.TestConnectivityAsync(normalizedHost, cancellationToken);
            if (!connectivity.IsWinRmReachable)
            {
                var reason = connectivity.PingSucceeded
                    ? "WinRM is not reachable."
                    : $"Host is not reachable ({connectivity.PingDetail}).";
                return new PowershellExecutionResult(1, string.Empty, $"Connection to '{normalizedHost}' failed: {reason}");
            }
        }

        var poolKey = isLocal ? LocalSessionKey : normalizedHost;
        var script = WrapScript(scriptBody);
        var pool = _pools.GetOrAdd(poolKey, _ => new PowerShellRunspacePool(normalizedHost, isLocal, _maxSessionsPerHost, UpdatePoolPressure));
        PowerShellRunspaceSession? session = null;
        var invalidateSession = false;

        try
        {
            session = await pool.RentAsync(cancellationToken);
            var result = await session.InvokeAsync(script, CreateRunspace, cancellationToken);
            if (isLocal && ShouldFallbackToWindowsPowerShell(result))
            {
                invalidateSession = true;
                return await ExecuteLocallyViaWindowsPowerShellAsync(script, cancellationToken);
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (PSRemotingTransportException ex)
        {
            invalidateSession = true;
            return new PowershellExecutionResult(1, string.Empty, ex.Message);
        }
        catch (PSInvalidOperationException ex)
        {
            invalidateSession = true;
            return new PowershellExecutionResult(1, string.Empty, ex.Message);
        }
        catch (InvalidRunspaceStateException ex)
        {
            invalidateSession = true;
            return new PowershellExecutionResult(1, string.Empty, ex.Message);
        }
        catch (Exception ex)
        {
            invalidateSession = true;
            return new PowershellExecutionResult(1, string.Empty, ex.Message);
        }
        finally
        {
            if (session is not null)
            {
                pool.Return(session, invalidateSession);
            }
        }
    }

    public static bool IsLocalHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        return host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
               host.Equals(".", StringComparison.OrdinalIgnoreCase) ||
               host.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_targetHostService is not null)
        {
            _targetHostService.HostChanged -= OnTargetHostChanged;
        }

        foreach (var pool in _pools.Values)
        {
            pool.Dispose();
        }

        _pools.Clear();
        _poolPressure.Clear();
        _hostBusyStateSink?.ClearBusyState(PoolBusyOwnerId);
    }

    private void OnTargetHostChanged(object? sender, string host)
    {
        var normalizedHost = host.Trim();
        var previousHost = Interlocked.Exchange(ref _lastKnownHost, normalizedHost);
        if (string.IsNullOrWhiteSpace(previousHost) ||
            string.Equals(previousHost, normalizedHost, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        InvalidateHost(previousHost);
    }

    private void InvalidateHost(string host)
    {
        var poolKey = IsLocalHost(host) ? LocalSessionKey : host;
        if (_pools.TryRemove(poolKey, out var pool))
        {
            pool.Invalidate();
            _poolPressure.TryRemove(pool.Host, out _);
            UpdatePoolPressure(new PoolPressureSnapshot(pool.Host, 0, 0, pool.MaxSessions));
        }
    }

    private static Runspace CreateRunspace(string host, bool isLocal)
    {
        if (isLocal)
        {
            return RunspaceFactory.CreateRunspace(InitialSessionState.CreateDefault2());
        }

        var connectionInfo = new WSManConnectionInfo
        {
            ComputerName = host,
            ShellUri = "http://schemas.microsoft.com/powershell/Microsoft.PowerShell",
            AuthenticationMechanism = AuthenticationMechanism.Default,
            OpenTimeout = 30000,
            OperationTimeout = 120000,
            CancelTimeout = 5000,
            IdleTimeout = 240000,
            MaxConnectionRetryCount = 1
        };

        return RunspaceFactory.CreateRunspace(connectionInfo);
    }

    private static string WrapScript(string scriptBody)
    {
        return "$ErrorActionPreference='Stop';$ProgressPreference='SilentlyContinue';" + scriptBody;
    }

    private static bool ShouldFallbackToWindowsPowerShell(PowershellExecutionResult result)
    {
        if (result.ExitCode == 0)
        {
            return false;
        }

        return result.StdErr.Contains("Cannot find the built-in module 'Microsoft.PowerShell.Utility' that is compatible with the 'Core' edition", StringComparison.OrdinalIgnoreCase) ||
               result.StdErr.Contains("Please make sure the PowerShell built-in modules are available", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<PowershellExecutionResult> ExecuteLocallyViaWindowsPowerShellAsync(string script, CancellationToken cancellationToken)
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

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            return new PowershellExecutionResult(1, string.Empty, "Failed to start powershell.exe for local execution.");
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(cancellationToken);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        return new PowershellExecutionResult(process.ExitCode, stdout, stderr);
    }

    private void UpdatePoolPressure(PoolPressureSnapshot snapshot)
    {
        if (_disposed)
        {
            return;
        }

        _poolPressure.AddOrUpdate(snapshot.Host, snapshot, (_, _) => snapshot);

        var saturatedPools = _poolPressure.Values
            .Where(static entry => entry.IsSaturated)
            .OrderByDescending(static entry => entry.WaitingRequests)
            .ThenBy(static entry => entry.Host, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (saturatedPools.Length == 0)
        {
            GetHostBusyStateSink()?.ClearBusyState(PoolBusyOwnerId);
            return;
        }

        var taskLines = saturatedPools
            .SelectMany(static pool => pool.FormatLines())
            .ToArray();

        GetHostBusyStateSink()?.SetBusyState(
            PoolBusyOwnerId,
            saturatedPools.Length == 1
                ? saturatedPools[0].FormatSummary()
                : $"PowerShell pools busy: {saturatedPools.Sum(static pool => pool.ActiveRentals)}/{saturatedPools.Sum(static pool => pool.MaxSessions)} in use, {saturatedPools.Sum(static pool => pool.WaitingRequests)} waiting",
            taskLines);
    }

    private IHostBusyStateSink? GetHostBusyStateSink()
    {
        if (_hostBusyStateSink is not null || _disposed)
        {
            return _hostBusyStateSink;
        }

        try
        {
            _hostBusyStateSink = _hostBusyStateSinkAccessor();
        }
        catch (ObjectDisposedException)
        {
            return null;
        }

        return _hostBusyStateSink;
    }

    private sealed class PowerShellRunspacePool : IDisposable
    {
        private readonly ConcurrentBag<PowerShellRunspaceSession> _idleSessions = [];
        private readonly SemaphoreSlim _rentalGate;
        private readonly Action<PoolPressureSnapshot> _pressureChanged;
        private readonly string _displayHost;
        private readonly bool _isLocal;
        private readonly int _maxSessions;
        private int _totalSessionCount;
        private int _activeRentals;
        private int _waitingRequests;
        private int _invalidateRequested;
        private bool _disposed;

        public PowerShellRunspacePool(string host, bool isLocal, int maxSessions, Action<PoolPressureSnapshot> pressureChanged)
        {
            _rentalGate = new SemaphoreSlim(maxSessions, maxSessions);
            _pressureChanged = pressureChanged;
            _displayHost = host;
            _isLocal = isLocal;
            _maxSessions = maxSessions;
        }

        public int TotalSessionCount => Volatile.Read(ref _totalSessionCount);
        public int MaxSessions => _maxSessions;
        public string Host => _displayHost;

        public async Task<PowerShellRunspaceSession> RentAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _waitingRequests);
            NotifyPressureChanged();

            try
            {
                await _rentalGate.WaitAsync(cancellationToken);
            }
            finally
            {
                Interlocked.Decrement(ref _waitingRequests);
            }

            if (_disposed)
            {
                NotifyPressureChanged();
                _rentalGate.Release();
                throw new ObjectDisposedException(nameof(PowerShellRunspacePool));
            }

            Interlocked.Increment(ref _activeRentals);
            NotifyPressureChanged();

            if (_idleSessions.TryTake(out var session))
            {
                return session;
            }

            Interlocked.Increment(ref _totalSessionCount);
            return new PowerShellRunspaceSession(_displayHost, _isLocal);
        }

        public void Return(PowerShellRunspaceSession session, bool invalidateSession)
        {
            try
            {
                if (_disposed || invalidateSession || Volatile.Read(ref _invalidateRequested) == 1)
                {
                    session.Dispose();
                    Interlocked.Decrement(ref _totalSessionCount);
                    return;
                }

                _idleSessions.Add(session);
            }
            finally
            {
                Interlocked.Decrement(ref _activeRentals);
                _rentalGate.Release();
                NotifyPressureChanged();
            }
        }

        public void Invalidate()
        {
            if (_disposed)
            {
                return;
            }

            Interlocked.Exchange(ref _invalidateRequested, 1);
            while (_idleSessions.TryTake(out var session))
            {
                session.Dispose();
                Interlocked.Decrement(ref _totalSessionCount);
            }

            NotifyPressureChanged();
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Interlocked.Exchange(ref _invalidateRequested, 1);
            while (_idleSessions.TryTake(out var session))
            {
                session.Dispose();
                Interlocked.Decrement(ref _totalSessionCount);
            }

            NotifyPressureChanged();
            _rentalGate.Dispose();
        }

        private void NotifyPressureChanged()
        {
            _pressureChanged(new PoolPressureSnapshot(
                _displayHost,
                Volatile.Read(ref _activeRentals),
                Volatile.Read(ref _waitingRequests),
                _maxSessions));
        }
    }

    private sealed class PowerShellRunspaceSession : IDisposable
    {
        private readonly string _host;
        private readonly bool _isLocal;
        private Runspace? _runspace;
        private bool _disposed;

        public PowerShellRunspaceSession(string host, bool isLocal)
        {
            _host = host;
            _isLocal = isLocal;
        }

        public async Task<PowershellExecutionResult> InvokeAsync(
            string script,
            Func<string, bool, Runspace> runspaceFactory,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(PowerShellRunspaceSession));
            }

            var runspace = _runspace ??= runspaceFactory(_host, _isLocal);
            if (runspace.RunspaceStateInfo.State == RunspaceState.BeforeOpen)
            {
                runspace.Open();
            }

            using var powerShell = PowerShell.Create();
            powerShell.Runspace = runspace;
            powerShell.AddScript(script, useLocalScope: false);

            using var cancellationRegistration = cancellationToken.Register(() => TryStop(powerShell));
            Collection<PSObject> output;
            try
            {
                output = await Task.Run(powerShell.Invoke, CancellationToken.None);
            }
            catch (AggregateException ex) when (cancellationToken.IsCancellationRequested || IsPipelineStop(ex))
            {
                throw new OperationCanceledException(cancellationToken);
            }
            catch (RuntimeException) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }
            catch (PipelineStoppedException) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            var stdOut = JoinOutput(output);
            var stdErr = JoinErrors(powerShell.Streams.Error);
            var exitCode = powerShell.HadErrors ? 1 : 0;
            return new PowershellExecutionResult(exitCode, stdOut, stdErr);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            try
            {
                _runspace?.Dispose();
            }
            catch
            {
                // Best effort cleanup only.
            }
            finally
            {
                _runspace = null;
            }
        }

        private static string JoinOutput(IEnumerable<PSObject> output)
        {
            var builder = new StringBuilder();
            foreach (var entry in output)
            {
                if (builder.Length > 0)
                {
                    builder.AppendLine();
                }

                builder.Append(entry?.BaseObject switch
                {
                    null => string.Empty,
                    string value => value,
                    _ => entry.ToString()
                });
            }

            return builder.ToString();
        }

        private static string JoinErrors(PSDataCollection<ErrorRecord> errors)
        {
            var builder = new StringBuilder();
            foreach (var error in errors)
            {
                if (builder.Length > 0)
                {
                    builder.AppendLine();
                }

                builder.Append(error.ToString());
            }

            return builder.ToString();
        }

        private static void TryStop(PowerShell powerShell)
        {
            try
            {
                powerShell.Stop();
            }
            catch
            {
                // Best effort cancellation only.
            }
        }

        private static bool IsPipelineStop(AggregateException exception)
        {
            return exception.Flatten().InnerExceptions.All(static inner =>
                inner is PipelineStoppedException ||
                inner is RuntimeException ||
                inner is OperationCanceledException ||
                inner is IOException ||
                inner is ObjectDisposedException);
        }
    }

    private sealed record PoolPressureSnapshot(string Host, int ActiveRentals, int WaitingRequests, int MaxSessions)
    {
        public bool IsSaturated => WaitingRequests > 0;

        public string FormatSummary()
        {
            var scope = IsLocalHost(Host) ? "local" : Host;
            return $"PowerShell {scope}: {ActiveRentals}/{MaxSessions} in use, {WaitingRequests} waiting";
        }

        public IReadOnlyList<string> FormatLines()
        {
            var scope = IsLocalHost(Host) ? "local" : Host;
            return
            [
                $"{scope}: {ActiveRentals}/{MaxSessions} sessions in use",
                $"{scope}: {WaitingRequests} waiting request(s)"
            ];
        }
    }
}
