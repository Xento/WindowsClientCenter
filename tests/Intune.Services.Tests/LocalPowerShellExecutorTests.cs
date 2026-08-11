using WindowsClientCenter.Intune.Services.Contracts;
using WindowsClientCenter.Intune.Services.Models;
using WindowsClientCenter.Intune.Services.Runtime;
using WindowsClientCenter.Plugin.Abstractions.Contracts;
using Xunit;

namespace WindowsClientCenter.Tests.IntuneServices;

public sealed class LocalPowerShellExecutorTests
{
    [Fact]
    public async Task ExecuteForHostAsync_LocalHost_ReusesRunspaceAcrossCalls()
    {
        using var executor = new LocalPowerShellExecutor(new FakeHostConnectivityService());

        var first = await executor.ExecuteForHostAsync(Environment.MachineName, "'first'", CancellationToken.None);
        var second = await executor.ExecuteForHostAsync(Environment.MachineName, "'second'", CancellationToken.None);

        Assert.Equal(0, first.ExitCode);
        Assert.Equal("first", first.StdOut.Trim());
        Assert.Equal(0, second.ExitCode);
        Assert.Equal("second", second.StdOut.Trim());
        Assert.Equal(1, executor.ActiveSessionCount);
    }

    [Fact]
    public async Task ExecuteJsonForHostAsync_LocalHost_DeserializesJsonPayload()
    {
        using var executor = new LocalPowerShellExecutor(new FakeHostConnectivityService());

        var payload = await ((IPowerShellExecutor)executor).ExecuteJsonForHostAsync<TestPayload>(
            Environment.MachineName,
            "'{\"Value\":\"ok\",\"Count\":2}'",
            CancellationToken.None);

        Assert.NotNull(payload);
        Assert.Equal("ok", payload!.Value);
        Assert.Equal(2, payload.Count);
    }

    [Fact]
    public async Task ExecuteForHostAsync_LocalHost_RemainsUsableAfterScriptFailure()
    {
        using var executor = new LocalPowerShellExecutor(new FakeHostConnectivityService());

        var failure = await executor.ExecuteForHostAsync(Environment.MachineName, "throw 'boom'", CancellationToken.None);
        var success = await executor.ExecuteForHostAsync(Environment.MachineName, "'still-ok'", CancellationToken.None);

        Assert.NotEqual(0, failure.ExitCode);
        Assert.Contains("boom", failure.StdErr, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, success.ExitCode);
        Assert.Equal("still-ok", success.StdOut.Trim());
        Assert.Equal(1, executor.ActiveSessionCount);
    }

    [Fact]
    public async Task ExecuteForHostAsync_LocalHost_AllowsParallelCallsUpToPoolSize()
    {
        using var executor = new LocalPowerShellExecutor(
            new FakeHostConnectivityService(),
            options: new IntuneRuntimeOptions { PowerShellSessionPoolSize = 5 });
        var gateName = $"WindowsClientCenter-{Guid.NewGuid():N}";
        using var releaseGate = new EventWaitHandle(false, EventResetMode.ManualReset, gateName);

        var tasks = Enumerable.Range(0, 5)
            .Select(index => executor.ExecuteForHostAsync(
                Environment.MachineName,
                $"$null = [System.Threading.EventWaitHandle]::OpenExisting('{gateName}').WaitOne(); 'job-{index}'",
                CancellationToken.None).AsTask())
            .ToArray();

        try
        {
            var timeoutAt = DateTime.UtcNow.AddSeconds(10);
            while (executor.ActiveSessionCount < 5 && DateTime.UtcNow < timeoutAt)
            {
                await Task.Delay(20);
            }

            Assert.Equal(5, executor.ActiveSessionCount);
        }
        finally
        {
            releaseGate.Set();
        }

        var results = await Task.WhenAll(tasks);

        Assert.All(results, result => Assert.Equal(0, result.ExitCode));
        Assert.Equal(5, results.Select(static result => result.StdOut.Trim()).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(5, executor.ActiveSessionCount);
    }

    [Fact]
    public async Task ExecuteForHostAsync_LocalHost_ReportsPoolSaturationToHostBusyState()
    {
        var hostBusyStateSink = new FakeHostBusyStateSink();

        using var executor = new LocalPowerShellExecutor(
            new FakeHostConnectivityService(),
            options: new IntuneRuntimeOptions { PowerShellSessionPoolSize = 1 },
            hostBusyStateSinkAccessor: () => hostBusyStateSink);
        var gateName = $"WindowsClientCenter-{Guid.NewGuid():N}";
        using var releaseGate = new EventWaitHandle(false, EventResetMode.ManualReset, gateName);

        var blocker = executor.ExecuteForHostAsync(
            Environment.MachineName,
            $"$null = [System.Threading.EventWaitHandle]::OpenExisting('{gateName}').WaitOne(); 'first'",
            CancellationToken.None).AsTask();

        var waiter = executor.ExecuteForHostAsync(
            Environment.MachineName,
            "'second'",
            CancellationToken.None).AsTask();

        try
        {
            var timeoutAt = DateTime.UtcNow.AddSeconds(10);
            while (!hostBusyStateSink.History.Any(static entry => entry.Operation == "set" && entry.Status.Contains("waiting", StringComparison.Ordinal)) &&
                   DateTime.UtcNow < timeoutAt)
            {
                await Task.Delay(20);
            }

            Assert.Contains(hostBusyStateSink.History, static entry => entry.Operation == "set" && entry.Status.Contains("waiting", StringComparison.Ordinal));
        }
        finally
        {
            releaseGate.Set();
        }

        await Task.WhenAll(blocker, waiter);

        Assert.Contains(hostBusyStateSink.History, static entry => entry.Operation == "set" && entry.Tasks.Any(task => task.Contains("sessions in use", StringComparison.Ordinal)));
        Assert.Contains(hostBusyStateSink.History, static entry => entry.Operation == "set" && entry.Tasks.Any(task => task.Contains("waiting request", StringComparison.Ordinal)));
        Assert.Contains(hostBusyStateSink.History, static entry => entry.Operation == "clear");
    }

    [Fact]
    public async Task ExecuteForHostAsync_HostChanged_InvalidatesPreviousHostPool()
    {
        var initialHost = Environment.MachineName;
        var targetHostService = new FakeTargetHostService(initialHost);
        using var executor = new LocalPowerShellExecutor(new FakeHostConnectivityService(), targetHostService);

        var first = await executor.ExecuteForHostAsync(initialHost, "'first'", CancellationToken.None);
        Assert.Equal(1, executor.ActiveSessionCount);

        targetHostService.SetCurrentHost("localhost");

        var second = await executor.ExecuteForHostAsync("localhost", "'second'", CancellationToken.None);

        Assert.Equal(0, first.ExitCode);
        Assert.Equal(0, second.ExitCode);
        Assert.Equal(1, executor.ActiveSessionCount);
    }

    private sealed class FakeHostConnectivityService : IHostConnectivityService
    {
        public ValueTask<HostConnectivityStatus> TestConnectivityAsync(string host, CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(new HostConnectivityStatus(
                PingSucceeded: true,
                PingRoundtripTimeMs: 1,
                PingDetail: "ok",
                ResolvedIp: "127.0.0.1",
                SmbReachable: true,
                WinRmHttpReachable: true,
                WinRmHttpsReachable: false));
        }
    }

    private sealed class FakeTargetHostService(string currentHost) : ITargetHostService
    {
        private long _version = 1;
        private CancellationTokenSource _selectionCancellationTokenSource = new();

        public string CurrentHost { get; private set; } = currentHost;

        public event EventHandler<string>? HostChanged;

        public HostSelection CaptureSelection() => new(CurrentHost, _version, _selectionCancellationTokenSource.Token);

        public bool IsCurrent(HostSelection selection) => selection.Version == _version && string.Equals(selection.Host, CurrentHost, StringComparison.OrdinalIgnoreCase);

        public void SetCurrentHost(string host)
        {
            if (!string.Equals(CurrentHost, host, StringComparison.OrdinalIgnoreCase))
            {
                _selectionCancellationTokenSource.Cancel();
                _selectionCancellationTokenSource.Dispose();
                _selectionCancellationTokenSource = new CancellationTokenSource();
                _version++;
            }

            CurrentHost = host;
            HostChanged?.Invoke(this, host);
        }
    }

    private sealed class FakeHostBusyStateSink : IHostBusyStateSink
    {
        private readonly List<BusyStateHistoryEntry> _history = [];

        public IReadOnlyList<BusyStateHistoryEntry> History => _history;

        public void SetBusyState(string ownerId, string shortStatus, IReadOnlyList<string>? tasks = null)
        {
            lock (_history)
            {
                _history.Add(new BusyStateHistoryEntry("set", ownerId, shortStatus, tasks ?? []));
            }
        }

        public void ClearBusyState(string ownerId)
        {
            lock (_history)
            {
                _history.Add(new BusyStateHistoryEntry("clear", ownerId, string.Empty, []));
            }
        }
    }

    private sealed record BusyStateHistoryEntry(string Operation, string OwnerId, string Status, IReadOnlyList<string> Tasks);

    private sealed class TestPayload
    {
        public string Value { get; set; } = string.Empty;
        public int Count { get; set; }
    }
}
