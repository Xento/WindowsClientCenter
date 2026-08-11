using WindowsClientCenter.Intune.Services.Contracts;
using WindowsClientCenter.Intune.Services.Models;
using WindowsClientCenter.Plugin.Abstractions.Contracts;
using WindowsClientCenter.Plugins.DeviceActions.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace WindowsClientCenter.Tests.Plugins.DeviceActions;

public sealed class DeviceInstalledSoftwareViewModelTests
{
    [Fact]
    public async Task LoadAsync_DisplaysInstalledSoftwareForConnectedHost()
    {
        var hostStatus = new FakeHostStatusLogSink();
        var manager = new FakeInstalledSoftwareManager(new InstalledSoftwareSnapshot(
            "CLIENT01",
            false,
            [CreateMsiEntry()],
            []));
        var viewModel = new DeviceInstalledSoftwareViewModel(
            new FakePluginContext(BuildServices(new FakeTargetHostService("CLIENT01"), manager, hostStatus)),
            (_, _) => true);

        await viewModel.LoadAsync(CancellationToken.None);

        Assert.Single(viewModel.Software);
        Assert.Equal("7-Zip", viewModel.SelectedSoftware?.Name);
        Assert.Equal("Loaded 1 installed software item(s).", viewModel.Status);
        Assert.Contains(hostStatus.Messages, message => message.Contains("[Installed Software] Loaded 1 installed software item(s).", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LoadAsync_ShowsDisconnectedStatusWithoutHost()
    {
        var manager = new FakeInstalledSoftwareManager(new InstalledSoftwareSnapshot(string.Empty, false, [], []));
        var viewModel = new DeviceInstalledSoftwareViewModel(
            new FakePluginContext(BuildServices(new FakeTargetHostService(string.Empty), manager, new FakeHostStatusLogSink())),
            (_, _) => true);

        await viewModel.LoadAsync(CancellationToken.None);

        Assert.Empty(viewModel.Software);
        Assert.Null(viewModel.SelectedSoftware);
        Assert.Equal("Client is not connected. Click Connect first.", viewModel.Status);
    }

    [Fact]
    public async Task CommandsAreDisabledForUnsupportedRows()
    {
        var manager = new FakeInstalledSoftwareManager(new InstalledSoftwareSnapshot(
            "CLIENT01",
            false,
            [CreateEntryWithoutRegistryIdentity()],
            []));
        var viewModel = new DeviceInstalledSoftwareViewModel(
            new FakePluginContext(BuildServices(new FakeTargetHostService("CLIENT01"), manager, new FakeHostStatusLogSink())),
            (_, _) => true);

        await viewModel.LoadAsync(CancellationToken.None);

        Assert.False(viewModel.RepairSelectedMsiCommand.CanExecute(null));
        Assert.False(viewModel.UninstallSelectedMsiCommand.CanExecute(null));
        Assert.False(viewModel.QuietUninstallSelectedCommand.CanExecute(null));
        Assert.False(viewModel.ForceRemoveSelectedRegistryEntryCommand.CanExecute(null));
    }

    [Fact]
    public async Task RepairSelectedMsiAsync_DoesNotCallManager_WhenConfirmationDeclined()
    {
        var manager = new FakeInstalledSoftwareManager(new InstalledSoftwareSnapshot(
            "CLIENT01",
            false,
            [CreateMsiEntry()],
            []));
        var viewModel = new DeviceInstalledSoftwareViewModel(
            new FakePluginContext(BuildServices(new FakeTargetHostService("CLIENT01"), manager, new FakeHostStatusLogSink())),
            (_, _) => false);
        await viewModel.LoadAsync(CancellationToken.None);

        await viewModel.RepairSelectedMsiAsync();

        Assert.Equal(0, manager.RepairCalls);
        Assert.Contains("cancelled", viewModel.Status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task QuietUninstallSelectedAsync_RefreshesAfterSuccess()
    {
        var manager = new FakeInstalledSoftwareManager(new InstalledSoftwareSnapshot(
            "CLIENT01",
            false,
            [CreateQuietEntry()],
            []));
        var viewModel = new DeviceInstalledSoftwareViewModel(
            new FakePluginContext(BuildServices(new FakeTargetHostService("CLIENT01"), manager, new FakeHostStatusLogSink())),
            (_, _) => true);
        await viewModel.LoadAsync(CancellationToken.None);

        await viewModel.QuietUninstallSelectedAsync();

        Assert.Equal(1, manager.QuietUninstallCalls);
        Assert.True(manager.LoadCalls >= 2);
    }

    [Fact]
    public async Task ForceRemoveSelectedRegistryEntryAsync_DoesNotCallManager_WhenConfirmationDeclined()
    {
        var manager = new FakeInstalledSoftwareManager(new InstalledSoftwareSnapshot(
            "CLIENT01",
            false,
            [CreateMsiEntry()],
            []));
        var viewModel = new DeviceInstalledSoftwareViewModel(
            new FakePluginContext(BuildServices(new FakeTargetHostService("CLIENT01"), manager, new FakeHostStatusLogSink())),
            (_, _) => false);
        await viewModel.LoadAsync(CancellationToken.None);

        await viewModel.ForceRemoveSelectedRegistryEntryAsync();

        Assert.Equal(0, manager.ForceRemoveCalls);
        Assert.Contains("cancelled", viewModel.Status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ForceRemoveSelectedRegistryEntryAsync_RefreshesAfterSuccess()
    {
        var manager = new FakeInstalledSoftwareManager(new InstalledSoftwareSnapshot(
            "CLIENT01",
            false,
            [CreateMsiEntry()],
            []));
        var viewModel = new DeviceInstalledSoftwareViewModel(
            new FakePluginContext(BuildServices(new FakeTargetHostService("CLIENT01"), manager, new FakeHostStatusLogSink())),
            (_, _) => true);
        await viewModel.LoadAsync(CancellationToken.None);

        await viewModel.ForceRemoveSelectedRegistryEntryAsync();

        Assert.Equal(1, manager.ForceRemoveCalls);
        Assert.True(manager.LoadCalls >= 2);
    }

    private static InstalledSoftwareEntry CreateMsiEntry() => new(
        "sms|7zip",
        "7-Zip",
        "24.09",
        "Igor Pavlov",
        "20260420",
        @"C:\Program Files\7-Zip",
        @"C:\Windows\ccmcache\7zip",
        "{23170F69-40C1-2702-2409-000001000000}",
        "{23170F69-40C1-2702-2409-000001000000}",
        "MsiExec.exe /I{23170F69-40C1-2702-2409-000001000000}",
        "MsiExec.exe /X{23170F69-40C1-2702-2409-000001000000} /qn",
        "SMS_InstalledSoftware",
        "x64");

    private static InstalledSoftwareEntry CreateQuietEntry() => new(
        "registry|vpn",
        "Contoso VPN",
        "5.2.1",
        "Contoso",
        "20260421",
        @"C:\Program Files\Contoso\VPN",
        string.Empty,
        string.Empty,
        string.Empty,
        @"""C:\Program Files\Contoso\VPN\uninstall.exe""",
        @"""C:\Program Files\Contoso\VPN\uninstall.exe"" /quiet",
        "Registry",
        "x64");

    private static InstalledSoftwareEntry CreateReadOnlyEntry() => new(
        "registry|edge",
        "Microsoft Edge",
        "124.0",
        "Microsoft Corporation",
        "20260421",
        @"C:\Program Files (x86)\Microsoft\Edge\Application",
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty,
        "Registry",
        "x86");

    private static InstalledSoftwareEntry CreateEntryWithoutRegistryIdentity() => new(
        "sms||Unknown",
        "Unknown",
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty);

    private static IServiceProvider BuildServices(
        ITargetHostService targetHostService,
        IInstalledSoftwareManager installedSoftwareManager,
        IHostStatusLogSink hostStatusLogSink)
    {
        return new ServiceCollection()
            .AddSingleton(targetHostService)
            .AddSingleton(installedSoftwareManager)
            .AddSingleton(hostStatusLogSink)
            .BuildServiceProvider();
    }

    private sealed class FakePluginContext(IServiceProvider services) : IPluginContext
    {
        public Microsoft.Extensions.Logging.ILogger Logger { get; } = NullLogger.Instance;
        public IServiceProvider Services { get; } = services;
        public string EnvironmentName { get; } = "Test";
        public IReadOnlyDictionary<string, string> Settings { get; } = new Dictionary<string, string>();
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

    private sealed class FakeInstalledSoftwareManager(InstalledSoftwareSnapshot snapshot) : IInstalledSoftwareManager
    {
        public int LoadCalls { get; private set; }
        public int RepairCalls { get; private set; }
        public int QuietUninstallCalls { get; private set; }
        public int ForceRemoveCalls { get; private set; }

        public ValueTask<InstalledSoftwareSnapshot> GetInstalledSoftwareAsync(string host, CancellationToken cancellationToken)
        {
            LoadCalls++;
            return ValueTask.FromResult(snapshot);
        }

        public ValueTask<DeviceActionResult> RepairMsiAsync(string host, string softwareCode, CancellationToken cancellationToken)
        {
            RepairCalls++;
            return ValueTask.FromResult(DeviceActionResult.Ok($"Repaired {softwareCode}."));
        }

        public ValueTask<DeviceActionResult> UninstallMsiAsync(string host, string softwareCode, CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(DeviceActionResult.Ok($"Uninstalled {softwareCode}."));
        }

        public ValueTask<DeviceActionResult> UninstallQuietAsync(string host, string quietUninstallString, string softwareIdentity, CancellationToken cancellationToken)
        {
            QuietUninstallCalls++;
            return ValueTask.FromResult(DeviceActionResult.Ok($"Quiet uninstall {softwareIdentity}."));
        }

        public ValueTask<DeviceActionResult> ForceRemoveRegistryEntryAsync(string host, InstalledSoftwareEntry software, CancellationToken cancellationToken)
        {
            ForceRemoveCalls++;
            return ValueTask.FromResult(DeviceActionResult.Ok($"Force removed {software.Name}."));
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
