using WindowsClientCenter.Intune.Services.Contracts;
using WindowsClientCenter.Intune.Services.Models;
using WindowsClientCenter.Plugin.Abstractions.Contracts;
using WindowsClientCenter.Plugins.DeviceActions.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace WindowsClientCenter.Tests.Plugins.DeviceActions;

public sealed class DeviceAppxApplicationsViewModelTests
{
    [Fact]
    public async Task LoadAsync_ShowsPerUserStatusAndHidesFrameworksByDefault()
    {
        var manager = new FakeAppxPackageManager(CreateSnapshot(activeUserSid: "S-1-5-21-1000"));
        var viewModel = CreateViewModel(manager);

        await viewModel.LoadAsync(CancellationToken.None);

        var package = Assert.Single(viewModel.Packages);
        Assert.Equal("Contoso App", package.EffectiveDisplayName);
        Assert.Equal(@"CONTOSO\Ada", viewModel.ActiveUserText);
        Assert.True(viewModel.SelectedUser?.IsActiveUser);

        viewModel.ShowFrameworks = true;
        viewModel.ShowNonRemovable = true;
        Assert.Equal(2, viewModel.Packages.Count);
    }

    [Fact]
    public async Task ActiveUserWingetActions_AreDisabledWithoutActiveUser()
    {
        var manager = new FakeAppxPackageManager(CreateSnapshot(activeUserSid: string.Empty));
        var viewModel = CreateViewModel(manager);
        await viewModel.LoadAsync(CancellationToken.None);
        viewModel.WingetQuery = "PowerToys";
        await viewModel.SearchWingetAsync();

        Assert.True(viewModel.InstallMachineCommand.CanExecute(null));
        Assert.False(viewModel.InstallForActiveUserCommand.CanExecute(null));
        Assert.False(viewModel.UpgradeForActiveUserCommand.CanExecute(null));
    }

    [Fact]
    public async Task RemoveForSelectedUserAsync_UsesSelectedSidAndRefreshes()
    {
        var manager = new FakeAppxPackageManager(CreateSnapshot(activeUserSid: "S-1-5-21-1000"));
        var viewModel = CreateViewModel(manager, (_, _) => true);
        await viewModel.LoadAsync(CancellationToken.None);

        await viewModel.RemoveForSelectedUserAsync();

        Assert.Equal("S-1-5-21-1000", manager.RemovedUserSid);
        Assert.True(manager.InventoryCalls >= 2);
    }

    [Fact]
    public async Task WingetSearch_CanFilterResultsBySource()
    {
        var manager = new FakeAppxPackageManager(CreateSnapshot(activeUserSid: "S-1-5-21-1000"));
        var viewModel = CreateViewModel(manager);
        await viewModel.LoadAsync(CancellationToken.None);
        viewModel.WingetQuery = "power";

        await viewModel.SearchWingetAsync();
        viewModel.WingetSourceFilter = "msstore";

        var result = Assert.Single(viewModel.WingetResults);
        Assert.Equal("9ABC", result.Id);
        Assert.Equal("msstore", result.Source);
    }

    private static DeviceAppxApplicationsViewModel CreateViewModel(FakeAppxPackageManager manager, Func<string, string, bool>? confirm = null)
    {
        var services = new ServiceCollection()
            .AddSingleton<ITargetHostService>(new FakeTargetHostService("CLIENT01"))
            .AddSingleton<IAppxPackageManager>(manager)
            .AddSingleton<IHostStatusLogSink>(new FakeHostStatusLogSink())
            .BuildServiceProvider();
        return new DeviceAppxApplicationsViewModel(new FakePluginContext(services), confirm ?? ((_, _) => true));
    }

    private static AppxPackageSnapshot CreateSnapshot(string activeUserSid) => new(
        "CLIENT01",
        activeUserSid.Length == 0 ? string.Empty : @"CONTOSO\Ada",
        activeUserSid,
        [
            new(
                "Contoso.App_1.0.0.0_x64__abc", "Contoso.App_abc", "Contoso.App", "Contoso App", "1.0.0.0", "CN=Contoso", "X64",
                @"C:\Program Files\WindowsApps\Contoso.App", false, false, false, false, false, true, "Contoso.App_1.0_neutral_~_abc",
                [new("S-1-5-21-1000", @"CONTOSO\Ada", "Installed", activeUserSid.Length > 0)]),
            new(
                "Microsoft.VCLibs_1.0.0.0_x64__abc", "Microsoft.VCLibs_abc", "Microsoft.VCLibs", "Microsoft VCLibs", "1.0.0.0", "CN=Microsoft", "X64",
                @"C:\Program Files\WindowsApps\Microsoft.VCLibs", true, false, false, false, true, false, string.Empty,
                [new("S-1-5-21-1000", @"CONTOSO\Ada", "Installed", activeUserSid.Length > 0)])
        ],
        []);

    private sealed class FakeAppxPackageManager(AppxPackageSnapshot snapshot) : IAppxPackageManager
    {
        public int InventoryCalls { get; private set; }
        public string RemovedUserSid { get; private set; } = string.Empty;

        public ValueTask<AppxPackageSnapshot> GetPackagesAsync(string host, CancellationToken cancellationToken)
        {
            InventoryCalls++;
            return ValueTask.FromResult(snapshot);
        }

        public ValueTask<WingetSearchSnapshot> SearchWingetAsync(string host, string query, CancellationToken cancellationToken) => ValueTask.FromResult(new WingetSearchSnapshot(
            [new("Microsoft.PowerToys", "PowerToys", "0.92.1", "winget"), new("9ABC", "Power Toys", "1.0", "msstore")], []));

        public ValueTask<DeviceActionResult> InstallWingetAsync(string host, WingetCatalogEntry package, WingetInstallScope scope, CancellationToken cancellationToken) => ValueTask.FromResult(DeviceActionResult.Ok("Installed."));
        public ValueTask<DeviceActionResult> UpgradeWingetAsync(string host, WingetCatalogEntry package, WingetInstallScope scope, CancellationToken cancellationToken) => ValueTask.FromResult(DeviceActionResult.Ok("Updated."));

        public ValueTask<DeviceActionResult> RemoveForUserAsync(string host, string packageFullName, string userSid, CancellationToken cancellationToken)
        {
            RemovedUserSid = userSid;
            return ValueTask.FromResult(DeviceActionResult.Ok("Removed."));
        }

        public ValueTask<DeviceActionResult> RemoveForAllUsersAsync(string host, string packageFullName, CancellationToken cancellationToken) => ValueTask.FromResult(DeviceActionResult.Ok("Removed."));
        public ValueTask<DeviceActionResult> RemoveProvisioningAsync(string host, string provisionedPackageName, CancellationToken cancellationToken) => ValueTask.FromResult(DeviceActionResult.Ok("Provisioning removed."));
        public ValueTask<DeviceActionResult> RegisterForActiveUserAsync(string host, string packageFullName, CancellationToken cancellationToken) => ValueTask.FromResult(DeviceActionResult.Ok("Registered."));
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
        private readonly CancellationTokenSource _selectionCancellationTokenSource = new();
        public string CurrentHost { get; private set; } = currentHost;
        public event EventHandler<string>? HostChanged;
        public HostSelection CaptureSelection() => new(CurrentHost, 1, _selectionCancellationTokenSource.Token);
        public bool IsCurrent(HostSelection selection) => string.Equals(selection.Host, CurrentHost, StringComparison.OrdinalIgnoreCase);
        public void SetCurrentHost(string host) { CurrentHost = host; HostChanged?.Invoke(this, host); }
    }

    private sealed class FakeHostStatusLogSink : IHostStatusLogSink
    {
        public void Append(string message) { }
    }
}
