using WindowsClientCenter.Intune.Services.Contracts;
using WindowsClientCenter.Intune.Services.Models;
using WindowsClientCenter.Plugin.Abstractions.Contracts;
using WindowsClientCenter.Plugins.DeviceActions.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace WindowsClientCenter.Tests.Plugins.DeviceActions;

public sealed class DeviceServicesViewModelTests
{
    [Fact]
    public async Task LoadAsync_DefaultsToAllServices()
    {
        var hostStatus = new FakeHostStatusLogSink();
        var services = BuildServices(
            new FakeTargetHostService("CLIENT01"),
            new FakeWindowsServiceManager(
                new WindowsServiceSnapshot(
                    "CLIENT01",
                    false,
                    [
                        new WindowsServiceEntry("IntuneManagementExtension", "Microsoft Intune Management Extension", "Running", WindowsServiceStartMode.AutomaticDelayedStart, "Intune", 1000),
                        new WindowsServiceEntry("wuauserv", "Windows Update", "Running", WindowsServiceStartMode.Manual, "WU", 2000)
                    ],
                    [])),
            hostStatus);

        var viewModel = new DeviceServicesViewModel(new FakePluginContext(services), (_, _) => true);

        await viewModel.LoadAsync(CancellationToken.None);

        Assert.Equal(2, viewModel.Services.Count);
        Assert.Equal("All services", viewModel.SelectedFilter);
        Assert.Equal("Loaded 2 service(s).", viewModel.Status);
        Assert.Contains(hostStatus.Messages, message => message.Contains("Loaded 2 service(s).", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SelectingManagedFilter_ReducesVisibleServices()
    {
        var services = BuildServices(
            new FakeTargetHostService("CLIENT01"),
            new FakeWindowsServiceManager(
                new WindowsServiceSnapshot(
                    "CLIENT01",
                    false,
                    [
                        new WindowsServiceEntry("IntuneManagementExtension", "Microsoft Intune Management Extension", "Running", WindowsServiceStartMode.AutomaticDelayedStart, "Intune", 1000),
                        new WindowsServiceEntry("Spooler", "Print Spooler", "Running", WindowsServiceStartMode.Automatic, "Printer", 2000)
                    ],
                    [])),
            new FakeHostStatusLogSink());

        var viewModel = new DeviceServicesViewModel(
            new FakePluginContext(
                services,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["PluginSettings:device-services-view:filters:0:displayName"] = "All services",
                    ["PluginSettings:device-services-view:filters:0:includeAllServices"] = "true",
                    ["PluginSettings:device-services-view:filters:1:displayName"] = "Management",
                    ["PluginSettings:device-services-view:filters:1:serviceNames"] = "IntuneManagementExtension"
                }),
            (_, _) => true);
        await viewModel.LoadAsync(CancellationToken.None);

        viewModel.SelectedFilter = "Management";

        Assert.Single(viewModel.Services);
        Assert.Equal("IntuneManagementExtension", viewModel.SelectedService?.ServiceName);
    }

    [Fact]
    public async Task KillSelectedServiceAsync_DoesNotCallManager_WhenConfirmationDeclined()
    {
        var manager = new FakeWindowsServiceManager(
            new WindowsServiceSnapshot(
                "CLIENT01",
                false,
                [new WindowsServiceEntry("BITS", "Background Intelligent Transfer Service", "Running", WindowsServiceStartMode.AutomaticDelayedStart, "BITS", 1000)],
                []));
        var viewModel = new DeviceServicesViewModel(
            new FakePluginContext(BuildServices(new FakeTargetHostService("CLIENT01"), manager, new FakeHostStatusLogSink())),
            (_, _) => false);
        await viewModel.LoadAsync(CancellationToken.None);

        await viewModel.KillSelectedServiceAsync();

        Assert.Equal(0, manager.KillCalls);
        Assert.Contains("cancelled", viewModel.Status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ApplySelectedStartModeAsync_RefreshesAfterSuccess()
    {
        var manager = new FakeWindowsServiceManager(
            new WindowsServiceSnapshot(
                "CLIENT01",
                false,
                [new WindowsServiceEntry("BITS", "Background Intelligent Transfer Service", "Running", WindowsServiceStartMode.AutomaticDelayedStart, "BITS", 1000)],
                []));
        var viewModel = new DeviceServicesViewModel(
            new FakePluginContext(BuildServices(new FakeTargetHostService("CLIENT01"), manager, new FakeHostStatusLogSink())),
            (_, _) => true);
        await viewModel.LoadAsync(CancellationToken.None);

        viewModel.SelectedStartModeOption = viewModel.StartModeOptions.Single(option => option.Value == WindowsServiceStartMode.Manual);
        await viewModel.ApplySelectedStartModeAsync();

        Assert.Equal(1, manager.SetStartModeCalls);
        Assert.True(manager.LoadCalls >= 2);
    }

    private static IServiceProvider BuildServices(ITargetHostService targetHostService, IWindowsServiceManager windowsServiceManager, IHostStatusLogSink hostStatusLogSink)
    {
        return new ServiceCollection()
            .AddSingleton(targetHostService)
            .AddSingleton(windowsServiceManager)
            .AddSingleton(hostStatusLogSink)
            .BuildServiceProvider();
    }

    private sealed class FakePluginContext(IServiceProvider services, IReadOnlyDictionary<string, string>? settings = null) : IPluginContext
    {
        public Microsoft.Extensions.Logging.ILogger Logger { get; } = NullLogger.Instance;
        public IServiceProvider Services { get; } = services;
        public string EnvironmentName { get; } = "Test";
        public IReadOnlyDictionary<string, string> Settings { get; } = settings ?? new Dictionary<string, string>();
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

    private sealed class FakeWindowsServiceManager(WindowsServiceSnapshot snapshot) : IWindowsServiceManager
    {
        public int LoadCalls { get; private set; }
        public int KillCalls { get; private set; }
        public int SetStartModeCalls { get; private set; }

        public ValueTask<WindowsServiceSnapshot> GetServicesAsync(string host, CancellationToken cancellationToken)
        {
            LoadCalls++;
            return ValueTask.FromResult(snapshot);
        }

        public ValueTask<DeviceActionResult> StartServiceAsync(string host, string serviceName, CancellationToken cancellationToken)
            => ValueTask.FromResult(DeviceActionResult.Ok($"Started {serviceName}."));

        public ValueTask<DeviceActionResult> StopServiceAsync(string host, string serviceName, CancellationToken cancellationToken)
            => ValueTask.FromResult(DeviceActionResult.Ok($"Stopped {serviceName}."));

        public ValueTask<DeviceActionResult> RestartServiceAsync(string host, string serviceName, CancellationToken cancellationToken)
            => ValueTask.FromResult(DeviceActionResult.Ok($"Restarted {serviceName}."));

        public ValueTask<DeviceActionResult> KillServiceProcessAsync(string host, string serviceName, CancellationToken cancellationToken)
        {
            KillCalls++;
            return ValueTask.FromResult(DeviceActionResult.Ok($"Killed {serviceName}."));
        }

        public ValueTask<DeviceActionResult> SetStartModeAsync(string host, string serviceName, WindowsServiceStartMode startMode, CancellationToken cancellationToken)
        {
            SetStartModeCalls++;
            return ValueTask.FromResult(DeviceActionResult.Ok($"Start mode for {serviceName} set to {startMode}."));
        }
    }

    private sealed class FakeHostStatusLogSink : IHostStatusLogSink
    {
        public List<string> Messages { get; } = [];

        public void Append(string message)
        {
            Messages.Add(message);
        }
    }
}
