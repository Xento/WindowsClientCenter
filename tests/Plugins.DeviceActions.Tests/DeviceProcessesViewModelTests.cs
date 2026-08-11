using WindowsClientCenter.Intune.Services.Contracts;
using WindowsClientCenter.Intune.Services.Models;
using WindowsClientCenter.Plugin.Abstractions.Contracts;
using WindowsClientCenter.Plugins.DeviceActions.Models;
using WindowsClientCenter.Plugins.DeviceActions.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace WindowsClientCenter.Tests.Plugins.DeviceActions;

public sealed class DeviceProcessesViewModelTests
{
    [Fact]
    public async Task LoadAsync_DefaultsToListView_AndAutoRefreshOff()
    {
        var viewModel = CreateViewModel(
            new FakeWindowsProcessManager([CreateSnapshot("CLIENT01", DateTimeOffset.UtcNow, 2d)]),
            new Dictionary<string, string>());

        await viewModel.LoadAsync(CancellationToken.None);

        Assert.True(viewModel.IsListMode);
        Assert.False(viewModel.IsTreeMode);
        Assert.Equal("Off", viewModel.SelectedRefreshIntervalOption?.Label);
        Assert.Equal("Loaded 1 process(es).", viewModel.Status);
    }

    [Fact]
    public async Task LoadAsync_SecondSample_ComputesCpuDelta()
    {
        var baseTime = DateTimeOffset.UtcNow;
        var manager = new FakeWindowsProcessManager(
        [
            CreateSnapshot("CLIENT01", baseTime, 10d),
            CreateSnapshot("CLIENT01", baseTime.AddSeconds(10), 18d)
        ]);
        var viewModel = CreateViewModel(manager, new Dictionary<string, string>());

        await viewModel.LoadAsync(CancellationToken.None);
        await viewModel.LoadAsync(CancellationToken.None);

        Assert.StartsWith("10", viewModel.Processes.Single().CpuDisplay, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoadAsync_AutoRefreshSample_ComputesCpuDelta()
    {
        var baseTime = DateTimeOffset.UtcNow;
        var manager = new FakeWindowsProcessManager(
        [
            CreateSnapshot("CLIENT01", baseTime, 12d),
            CreateSnapshot("CLIENT01", baseTime.AddSeconds(10), 20d)
        ]);
        var viewModel = CreateViewModel(manager, new Dictionary<string, string>());

        await viewModel.LoadAsync(CancellationToken.None);
        await viewModel.LoadAsync(CancellationToken.None, isAutoRefresh: true);

        Assert.Equal("Auto-refreshed 1 process(es).", viewModel.Status);
        Assert.StartsWith("10", viewModel.Processes.Single().CpuDisplay, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TreeView_BuildsRoots_ForMissingParents()
    {
        var snapshot = new ProcessSnapshot(
            "CLIENT01",
            8,
            DateTimeOffset.UtcNow,
            [
                new ProcessSnapshotEntry("root", 100, null, string.Empty, 0, 0, 1, DateTimeOffset.UtcNow, 1, 1),
                new ProcessSnapshotEntry("child", 101, 100, string.Empty, 0, 0, 1, DateTimeOffset.UtcNow, 1, 1),
                new ProcessSnapshotEntry("orphan", 102, 9999, string.Empty, 0, 0, 1, DateTimeOffset.UtcNow, 1, 1)
            ],
            []);
        var viewModel = CreateViewModel(new FakeWindowsProcessManager([snapshot]), new Dictionary<string, string>());

        await viewModel.LoadAsync(CancellationToken.None);

        Assert.Equal(2, viewModel.ProcessTreeRoots.Count);
        Assert.Contains(viewModel.ProcessTreeRoots, node => node.ProcessId == 102);
        Assert.Single(viewModel.ProcessTreeRoots.Single(node => node.ProcessId == 100).Children);
    }

    [Fact]
    public async Task KillSelectedProcessAsync_DoesNotCallManager_WhenConfirmationDeclined()
    {
        var manager = new FakeWindowsProcessManager([CreateSnapshot("CLIENT01", DateTimeOffset.UtcNow, 2d)]);
        var viewModel = CreateViewModel(manager, new Dictionary<string, string>(), (_, _) => false);
        await viewModel.LoadAsync(CancellationToken.None);

        await viewModel.KillSelectedProcessAsync();

        Assert.Equal(0, manager.KillCalls);
        Assert.Contains("cancelled", viewModel.Status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task KillSelectedProcessAsync_RefreshesAfterSuccess()
    {
        var baseTime = DateTimeOffset.UtcNow;
        var manager = new FakeWindowsProcessManager(
        [
            CreateSnapshot("CLIENT01", baseTime, 2d),
            CreateSnapshot("CLIENT01", baseTime.AddSeconds(5), 4d)
        ]);
        var viewModel = CreateViewModel(manager, new Dictionary<string, string>(), (_, _) => true);
        await viewModel.LoadAsync(CancellationToken.None);

        await viewModel.KillSelectedProcessAsync();

        Assert.Equal(1, manager.KillCalls);
        Assert.True(manager.LoadCalls >= 2);
    }

    [Fact]
    public async Task Constructor_AppliesConfiguredDefaultTreeView_AndRefreshIntervals()
    {
        var viewModel = CreateViewModel(
            new FakeWindowsProcessManager([CreateSnapshot("CLIENT01", DateTimeOffset.UtcNow, 2d)]),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["PluginSettings:device-processes-view:defaultViewMode"] = "tree",
                ["PluginSettings:device-processes-view:refreshIntervals"] = "0,10,30",
                ["PluginSettings:device-processes-view:defaultRefreshIntervalSeconds"] = "10"
            });

        await viewModel.LoadAsync(CancellationToken.None);

        Assert.True(viewModel.IsTreeMode);
        Assert.Equal("10s", viewModel.SelectedRefreshIntervalOption?.Label);
        Assert.Equal(3, viewModel.RefreshIntervalOptions.Count);
    }

    private static DeviceProcessesViewModel CreateViewModel(
        FakeWindowsProcessManager manager,
        IReadOnlyDictionary<string, string> settings,
        Func<string, string, bool>? confirmAction = null)
    {
        var services = new ServiceCollection()
            .AddSingleton<ITargetHostService>(new FakeTargetHostService("CLIENT01"))
            .AddSingleton<IWindowsProcessManager>(manager)
            .AddSingleton<IHostStatusLogSink>(new FakeHostStatusLogSink())
            .BuildServiceProvider();
        return new DeviceProcessesViewModel(new FakePluginContext(services, settings), confirmAction);
    }

    private static ProcessSnapshot CreateSnapshot(string host, DateTimeOffset capturedAtUtc, double cpuSeconds)
    {
        var startTimeUtc = new DateTimeOffset(2026, 4, 20, 7, 0, 0, TimeSpan.Zero);
        return new ProcessSnapshot(
            host,
            8,
            capturedAtUtc,
            [new ProcessSnapshotEntry("proc", 1000, null, "proc.exe", 2000, 1000, cpuSeconds, startTimeUtc, 4, 40)],
            []);
    }

    private sealed class FakePluginContext(IServiceProvider services, IReadOnlyDictionary<string, string> settings) : IPluginContext
    {
        public Microsoft.Extensions.Logging.ILogger Logger { get; } = NullLogger.Instance;
        public IServiceProvider Services { get; } = services;
        public string EnvironmentName { get; } = "Test";
        public IReadOnlyDictionary<string, string> Settings { get; } = settings;
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

    private sealed class FakeWindowsProcessManager(IEnumerable<ProcessSnapshot> snapshots) : IWindowsProcessManager
    {
        private readonly Queue<ProcessSnapshot> _snapshots = new(snapshots);
        private readonly ProcessSnapshot _lastSnapshot = snapshots.Last();
        public int LoadCalls { get; private set; }
        public int KillCalls { get; private set; }

        public ValueTask<ProcessSnapshot> GetProcessesAsync(string host, CancellationToken cancellationToken)
        {
            LoadCalls++;
            return ValueTask.FromResult(_snapshots.Count > 0 ? _snapshots.Dequeue() : _lastSnapshot);
        }

        public ValueTask<DeviceActionResult> KillProcessAsync(string host, int processId, CancellationToken cancellationToken)
        {
            KillCalls++;
            return ValueTask.FromResult(DeviceActionResult.Ok($"Process {processId} terminated."));
        }
    }

    private sealed class FakeHostStatusLogSink : IHostStatusLogSink
    {
        public void Append(string message)
        {
        }
    }
}
