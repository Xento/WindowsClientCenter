using WindowsClientCenter.Intune.Services.Contracts;
using WindowsClientCenter.Intune.Services.Runtime;
using WindowsClientCenter.Plugin.Abstractions.Contracts;
using WindowsClientCenter.Plugin.Abstractions.Models;
using WindowsClientCenter.Plugins.PowerShellScripts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace WindowsClientCenter.Tests.Plugins.PowerShellScripts;

public sealed class PowerShellScriptsPluginTests
{
    [Fact]
    public async Task InitializeAsync_BuildsHierarchicalSortedMenu()
    {
        var metadataProvider = new FakeMetadataProvider(
        [
            new PowerShellScriptCatalogEntry("zeta.ps1", "zeta", "zeta.ps1", "zeta.ps1", PowerShellScriptExecutionMode.RemotingWindow, []),
            new PowerShellScriptCatalogEntry("Folder/beta.ps1", "beta", "Folder/beta.ps1", "Folder/beta.ps1", PowerShellScriptExecutionMode.RemotingWindow, []),
            new PowerShellScriptCatalogEntry("Folder/Alpha.ps1", "Alpha", "Folder/Alpha.ps1", "Folder/Alpha.ps1", PowerShellScriptExecutionMode.RemotingWindow, []),
            new PowerShellScriptCatalogEntry("Another/Inner/Tool.ps1", "Tool", "Another/Inner/Tool.ps1", "Another/Inner/Tool.ps1", PowerShellScriptExecutionMode.RemotingWindow, []),
            new PowerShellScriptCatalogEntry("Root.ps1", "Root", "Root.ps1", "Root.ps1", PowerShellScriptExecutionMode.DirectComputerName, [])
        ]);
        var plugin = new PowerShellScriptsPlugin(metadataProvider, new FakeLauncher());

        await plugin.InitializeAsync(CreateContext(), CancellationToken.None);

        var group = Assert.Single(plugin.GetRibbonGroups());
        var control = Assert.Single(group.Controls);
        Assert.NotNull(control.MenuItems);
        var topLevelMenu = control.MenuItems!;
        Assert.Equal(["Another", "Folder", "Root", "zeta"], topLevelMenu.Select(static item => item.Text).ToArray());

        var folderMenu = Assert.Single(topLevelMenu, static item => item.Text == "Folder");
        Assert.Equal(["Alpha", "beta"], folderMenu.Children!.Select(static item => item.Text).ToArray());

        var anotherMenu = Assert.Single(topLevelMenu, static item => item.Text == "Another");
        var innerMenu = Assert.Single(anotherMenu.Children!);
        Assert.Equal("Inner", innerMenu.Text);
        Assert.Equal("Tool", Assert.Single(innerMenu.Children!).Text);
    }

    [Fact]
    public async Task InitializeAsync_UsesConfiguredScriptDirectory()
    {
        var metadataProvider = new FakeMetadataProvider([]);
        var plugin = new PowerShellScriptsPlugin(metadataProvider, new FakeLauncher());
        var tempDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);

        await plugin.InitializeAsync(CreateContext(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["scriptDirectory"] = tempDirectory
        }), CancellationToken.None);

        Assert.Equal(tempDirectory, metadataProvider.LastDirectory);

        Directory.Delete(tempDirectory, recursive: true);
    }

    [Fact]
    public async Task ExecuteRibbonControlAsync_LaunchesDirectScriptWithComputerNameLiteral()
    {
        var metadataProvider = new FakeMetadataProvider(
        [
            new PowerShellScriptCatalogEntry(
                "Folder/Test.ps1",
                "Test",
                "Folder/Test.ps1",
                "Folder/Test.ps1",
                PowerShellScriptExecutionMode.DirectComputerName,
                [])
        ]);
        var launcher = new FakeLauncher();
        var plugin = new PowerShellScriptsPlugin(metadataProvider, launcher);

        await plugin.InitializeAsync(CreateContext(), CancellationToken.None);

        var result = await plugin.ExecuteRibbonControlAsync(
            "scripts-menu",
            new PluginActionContext(null, "Folder/Test.ps1", new Dictionary<string, string>
            {
                ["menuItemId"] = "Folder/Test.ps1",
                ["host"] = "CLIENT01"
            }),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("CLIENT01", launcher.LastHost);
        Assert.Equal("'CLIENT01'", launcher.LastParameters["ComputerName"]);
        Assert.Equal(PowerShellScriptExecutionMode.DirectComputerName, launcher.LastScript!.ExecutionMode);
    }

    [Fact]
    public async Task ExecuteRibbonControlAsync_ReturnsUnsupportedFailureForUnsupportedScript()
    {
        var metadataProvider = new FakeMetadataProvider(
        [
            new PowerShellScriptCatalogEntry(
                "Test.ps1",
                "Test",
                "Test.ps1",
                "Test.ps1",
                PowerShellScriptExecutionMode.Unsupported,
                [],
                "Unsupported parameter type.")
        ]);
        var plugin = new PowerShellScriptsPlugin(metadataProvider, new FakeLauncher());

        await plugin.InitializeAsync(CreateContext(), CancellationToken.None);

        var result = await plugin.ExecuteRibbonControlAsync(
            "scripts-menu",
            new PluginActionContext(null, "Test.ps1", new Dictionary<string, string>
            {
                ["menuItemId"] = "Test.ps1",
                ["host"] = "CLIENT01"
            }),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("Unsupported parameter type.", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InitializeAsync_LoadsScriptsInBackground_WhenMetadataIsNotImmediatelyAvailable()
    {
        var metadataProvider = new DeferredMetadataProvider();
        var refreshSink = new FakeHostRibbonRefreshSink();
        var plugin = new PowerShellScriptsPlugin(metadataProvider, new FakeLauncher());

        await plugin.InitializeAsync(CreateContext(configureServices: services =>
        {
            services.AddSingleton<IHostRibbonRefreshSink>(refreshSink);
            services.AddSingleton<IHostStatusLogSink>(new FakeHostStatusLogSink());
        }), CancellationToken.None);

        var loadingGroup = Assert.Single(plugin.GetRibbonGroups());
        var loadingControl = Assert.Single(loadingGroup.Controls);
        var loadingItem = Assert.Single(loadingControl.MenuItems!);
        Assert.Equal("Loading scripts...", loadingItem.Text);

        metadataProvider.SetResult(
        [
            new PowerShellScriptCatalogEntry(
                "Folder/Test.ps1",
                "Test",
                "Folder/Test.ps1",
                "Folder/Test.ps1",
                PowerShellScriptExecutionMode.DirectComputerName,
                [])
        ]);

        await refreshSink.WaitForRefreshAsync();

        var loadedGroup = Assert.Single(plugin.GetRibbonGroups());
        var loadedControl = Assert.Single(loadedGroup.Controls);
        var loadedItem = Assert.Single(loadedControl.MenuItems!);
        Assert.Equal("Folder", loadedItem.Text);
    }

    private static IPluginContext CreateContext(
        IReadOnlyDictionary<string, string>? settings = null,
        Action<ServiceCollection>? configureServices = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ITargetHostService>(new FakeTargetHostService("CLIENT01"));
        configureServices?.Invoke(services);
        return new FakePluginContext(services, settings);
    }

    private sealed class FakePluginContext(ServiceCollection services, IReadOnlyDictionary<string, string>? settings) : IPluginContext
    {
        public Microsoft.Extensions.Logging.ILogger Logger { get; } = NullLogger.Instance;

        public IServiceProvider Services { get; } = services.BuildServiceProvider();

        public string EnvironmentName { get; } = "test";

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

    private sealed class FakeMetadataProvider(IReadOnlyList<PowerShellScriptCatalogEntry> entries) : IPowerShellScriptMetadataProvider
    {
        public string? LastDirectory { get; private set; }

        public ValueTask<IReadOnlyList<PowerShellScriptCatalogEntry>> LoadAsync(string scriptDirectory, CancellationToken cancellationToken)
        {
            LastDirectory = scriptDirectory;
            return ValueTask.FromResult(entries);
        }
    }

    private sealed class DeferredMetadataProvider : IPowerShellScriptMetadataProvider
    {
        private readonly TaskCompletionSource<IReadOnlyList<PowerShellScriptCatalogEntry>> _completionSource =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<IReadOnlyList<PowerShellScriptCatalogEntry>> LoadAsync(string scriptDirectory, CancellationToken cancellationToken)
        {
            cancellationToken.Register(() => _completionSource.TrySetCanceled(cancellationToken));
            return new ValueTask<IReadOnlyList<PowerShellScriptCatalogEntry>>(_completionSource.Task);
        }

        public void SetResult(IReadOnlyList<PowerShellScriptCatalogEntry> entries)
        {
            _completionSource.TrySetResult(entries);
        }
    }

    private sealed class FakeHostRibbonRefreshSink : IHostRibbonRefreshSink
    {
        private readonly TaskCompletionSource<string> _refreshSource = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void RequestRibbonRefresh(string pluginId)
        {
            _refreshSource.TrySetResult(pluginId);
        }

        public async Task WaitForRefreshAsync()
        {
            await _refreshSource.Task;
        }
    }

    private sealed class FakeHostStatusLogSink : IHostStatusLogSink
    {
        public void Append(string message)
        {
        }
    }

    private sealed class FakeLauncher : IPowerShellScriptLauncher
    {
        public string? LastHost { get; private set; }

        public PowerShellScriptCatalogEntry? LastScript { get; private set; }

        public IReadOnlyDictionary<string, string> LastParameters { get; private set; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public ValueTask<PluginActionResult> LaunchAsync(
            string host,
            PowerShellScriptCatalogEntry script,
            IReadOnlyDictionary<string, string> parameterLiterals,
            IPowerShellExecutor? executor,
            CancellationToken cancellationToken)
        {
            LastHost = host;
            LastScript = script;
            LastParameters = new Dictionary<string, string>(parameterLiterals, StringComparer.OrdinalIgnoreCase);
            return ValueTask.FromResult(PluginActionResult.Ok("started"));
        }
    }
}
