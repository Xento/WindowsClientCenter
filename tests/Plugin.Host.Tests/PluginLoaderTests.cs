using System.Text.Json;
using WindowsClientCenter.Plugin.Abstractions.Contracts;
using WindowsClientCenter.Plugin.Abstractions.Models;
using WindowsClientCenter.Plugin.Host;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace WindowsClientCenter.Tests.PluginHost;

public sealed class PluginLoaderTests
{
    [Fact]
    public async Task DiscoverAsync_ReturnsEmpty_WhenDirectoryMissing()
    {
        var loggerFactory = NullLoggerFactory.Instance;
        var lifecycle = new PluginLifecycle();
        var loader = new PluginLoader(loggerFactory.CreateLogger<PluginLoader>(), lifecycle, new FakeHostStatusLogSink());

        var result = await loader.DiscoverAsync(
            pluginDirectory: Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")),
            context: new TestPluginContext(loggerFactory.CreateLogger("test")),
            cancellationToken: CancellationToken.None);

        Assert.Empty(result);
        var failure = Assert.Single(loader.LastLoadResults);
        Assert.False(failure.Succeeded);
        Assert.Single(loader.LastLoadErrors);
    }

    [Fact]
    public async Task Registry_LoadAsync_KeepsCapabilitySpecificViews()
    {
        var loggerFactory = NullLoggerFactory.Instance;
        var loader = new FakeLoader();
        var registry = new PluginRegistry(loggerFactory.CreateLogger<PluginRegistry>(), loader, new PluginLifecycle(), new FakeHostStatusLogSink());

        await registry.LoadAsync("unused", new TestPluginContext(loggerFactory.CreateLogger("test")), CancellationToken.None);

        Assert.Single(registry.ViewPlugins);
        Assert.Single(registry.ActionPlugins);
        Assert.Empty(registry.BackgroundPlugins);
        Assert.Single(registry.RibbonPlugins);
        Assert.Equal(4, registry.LastLoadResults.Count);
        Assert.Single(registry.LastLoadErrors);
    }

    [Fact]
    public async Task DiscoverAsync_ContinuesAfterInvalidManifest()
    {
        var loggerFactory = NullLoggerFactory.Instance;
        var sink = new FakeHostStatusLogSink();
        var loader = new PluginLoader(loggerFactory.CreateLogger<PluginLoader>(), new PluginLifecycle(), sink);
        var pluginDirectory = CreatePluginDirectory();

        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(pluginDirectory, "01-invalid.plugin.json"),
                "{ invalid json",
                CancellationToken.None);

            await File.WriteAllTextAsync(
                Path.Combine(pluginDirectory, "02-valid.plugin.json"),
                CreateManifestJson(
                    "loader-valid",
                    "Loader Valid",
                    "View",
                    typeof(LoaderTestViewPlugin).Assembly.Location,
                    typeof(LoaderTestViewPlugin).FullName!),
                CancellationToken.None);

            var result = await loader.DiscoverAsync(
                pluginDirectory,
                new TestPluginContext(loggerFactory.CreateLogger("test")),
                CancellationToken.None);

            Assert.Single(result);
            Assert.Equal(2, loader.LastLoadResults.Count);
            Assert.False(loader.LastLoadResults[0].Succeeded);
            Assert.Equal("01-invalid.plugin.json", loader.LastLoadResults[0].ManifestFileName);
            Assert.True(loader.LastLoadResults[1].Succeeded);
            Assert.Equal("loader-valid", loader.LastLoadResults[1].PluginId);
            Assert.Contains(sink.Messages, message => message.Contains("01-invalid.plugin.json", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(pluginDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task DiscoverAsync_RecordsInitializationFailure_AndContinuesLoadingLaterPlugins()
    {
        var loggerFactory = NullLoggerFactory.Instance;
        var sink = new FakeHostStatusLogSink();
        var loader = new PluginLoader(loggerFactory.CreateLogger<PluginLoader>(), new PluginLifecycle(), sink);
        var pluginDirectory = CreatePluginDirectory();

        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(pluginDirectory, "01-failing.plugin.json"),
                CreateManifestJson(
                    "loader-failing",
                    "Loader Failing",
                    "View",
                    typeof(LoaderFailingViewPlugin).Assembly.Location,
                    typeof(LoaderFailingViewPlugin).FullName!),
                CancellationToken.None);

            await File.WriteAllTextAsync(
                Path.Combine(pluginDirectory, "02-valid.plugin.json"),
                CreateManifestJson(
                    "loader-valid",
                    "Loader Valid",
                    "View",
                    typeof(LoaderTestViewPlugin).Assembly.Location,
                    typeof(LoaderTestViewPlugin).FullName!),
                CancellationToken.None);

            var result = await loader.DiscoverAsync(
                pluginDirectory,
                new TestPluginContext(loggerFactory.CreateLogger("test")),
                CancellationToken.None);

            var loadedPlugin = Assert.Single(result);
            Assert.Equal("loader-valid", loadedPlugin.Manifest.Id);
            Assert.Equal(2, loader.LastLoadResults.Count);
            Assert.False(loader.LastLoadResults[0].Succeeded);
            Assert.Equal("loader-failing", loader.LastLoadResults[0].PluginId);
            Assert.Contains("Simulated init failure", loader.LastLoadErrors[0], StringComparison.Ordinal);
            Assert.True(loader.LastLoadResults[1].Succeeded);
            Assert.Contains(sink.Messages, message => message.Contains("01-failing.plugin.json", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(pluginDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task InitializeAsync_AppliesPluginScopedSettings()
    {
        var loggerFactory = NullLoggerFactory.Instance;
        var lifecycle = new PluginLifecycle();
        var plugin = new CapturingViewPlugin();
        var loadedPlugin = new LoadedPlugin(
            new PluginManifest("bitlocker-agent", "BitLocker", "1", PluginCapability.View, "Device", "1"),
            plugin,
            PluginCapability.View,
            "bitlocker.dll",
            new AssemblyLoadContextHandle(null));

        var context = new TestPluginContext(
            loggerFactory.CreateLogger("test"),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["TargetHost"] = "localhost",
                ["PluginSettings:bitlocker-agent:verboseTimings"] = "true",
                ["PluginSettings:other-plugin:verboseTimings"] = "false"
            });

        await lifecycle.InitializeAsync(loadedPlugin, context, CancellationToken.None);

        Assert.NotNull(plugin.Context);
        Assert.Equal("localhost", plugin.Context!.Settings["TargetHost"]);
        Assert.Equal("true", plugin.Context.Settings["verboseTimings"]);
        Assert.DoesNotContain("PluginSettings:other-plugin:verboseTimings", plugin.Context.Settings.Keys);
    }

    [Fact]
    public async Task DiscoverAsync_LoadsViewPluginsIntoDefaultContext()
    {
        var loggerFactory = NullLoggerFactory.Instance;
        var loader = new PluginLoader(loggerFactory.CreateLogger<PluginLoader>(), new PluginLifecycle(), new FakeHostStatusLogSink());
        var pluginDirectory = CreatePluginDirectory();

        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(pluginDirectory, "01-view.plugin.json"),
                CreateManifestJson(
                    "loader-valid",
                    "Loader Valid",
                    "View",
                    typeof(LoaderTestViewPlugin).Assembly.Location,
                    typeof(LoaderTestViewPlugin).FullName!),
                CancellationToken.None);

            var result = await loader.DiscoverAsync(
                pluginDirectory,
                new TestPluginContext(loggerFactory.CreateLogger("test")),
                CancellationToken.None);

            var loadedPlugin = Assert.Single(result);
            Assert.Null(loadedPlugin.LoadContext.RawContext);
        }
        finally
        {
            Directory.Delete(pluginDirectory, recursive: true);
        }
    }

    private sealed class FakeLoader : IPluginLoader
    {
        public IReadOnlyList<PluginLoadResult> LastLoadResults { get; } =
        [
            new("view.plugin.json", "v", "View", 12, true, null),
            new("action.plugin.json", "a", "Action", 18, true, null),
            new("ribbon.plugin.json", "r", "Ribbon", 24, true, null),
            new("broken.plugin.json", null, null, 7, false, "Simulated load failure")
        ];

        public IReadOnlyList<string> LastLoadErrors => ["broken.plugin.json: Simulated load failure"];

        public Task<IReadOnlyList<LoadedPlugin>> DiscoverAsync(string pluginDirectory, IPluginContext context, CancellationToken cancellationToken)
        {
            IReadOnlyList<LoadedPlugin> plugins =
            [
                new LoadedPlugin(
                    new PluginManifest("v", "View", "1", PluginCapability.View, "A/B", "1"),
                    new TestViewPlugin(),
                    PluginCapability.View,
                    "view.dll",
                    new AssemblyLoadContextHandle(null)),
                new LoadedPlugin(
                    new PluginManifest("a", "Action", "1", PluginCapability.Action, "A/C", "1"),
                    new TestActionPlugin(),
                    PluginCapability.Action,
                    "action.dll",
                    new AssemblyLoadContextHandle(null)),
                new LoadedPlugin(
                    new PluginManifest("r", "Ribbon", "1", PluginCapability.Ribbon, "Ribbon", "1"),
                    new TestRibbonPlugin(),
                    PluginCapability.Ribbon,
                    "ribbon.dll",
                    new AssemblyLoadContextHandle(null))
            ];

            return Task.FromResult(plugins);
        }
    }

    private sealed class TestPluginContext(ILogger logger, IReadOnlyDictionary<string, string>? settings = null) : IPluginContext
    {
        public ILogger Logger { get; } = logger;

        public IServiceProvider Services { get; } = new ServiceCollection().BuildServiceProvider();

        public string EnvironmentName { get; } = "test";

        public IReadOnlyDictionary<string, string> Settings { get; } = settings ?? new Dictionary<string, string>();
    }

    private sealed class FakeHostStatusLogSink : IHostStatusLogSink
    {
        public List<string> Messages { get; } = [];

        public void Append(string message)
        {
            Messages.Add(message);
        }
    }

    private sealed class TestViewPlugin : IViewPlugin
    {
        public IPluginManifest Manifest { get; } = new PluginManifest("v", "View", "1", PluginCapability.View, "A/B", "1");

        public object CreateView() => new object();

        public ValueTask InitializeAsync(IPluginContext context, CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class CapturingViewPlugin : IViewPlugin
    {
        public IPluginManifest Manifest { get; } = new PluginManifest("bitlocker-agent", "BitLocker", "1", PluginCapability.View, "Device", "1");

        public IPluginContext? Context { get; private set; }

        public object CreateView() => new object();

        public ValueTask InitializeAsync(IPluginContext context, CancellationToken cancellationToken)
        {
            Context = context;
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TestActionPlugin : IActionPlugin
    {
        public IPluginManifest Manifest { get; } = new PluginManifest("a", "Action", "1", PluginCapability.Action, "A/C", "1");

        public ValueTask<PluginActionResult> ExecuteAsync(PluginActionContext context, CancellationToken cancellationToken)
            => ValueTask.FromResult(PluginActionResult.Ok("ok"));

        public ValueTask InitializeAsync(IPluginContext context, CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TestRibbonPlugin : IClientCenterPlugin, IRibbonControlPlugin
    {
        public IPluginManifest Manifest { get; } = new PluginManifest("r", "Ribbon", "1", PluginCapability.Ribbon, "Ribbon", "1");

        public IReadOnlyList<PluginRibbonGroup> GetRibbonGroups() => [];

        public ValueTask<PluginActionResult> ExecuteRibbonControlAsync(string controlId, PluginActionContext context, CancellationToken cancellationToken)
            => ValueTask.FromResult(PluginActionResult.Ok("ok"));

        public ValueTask InitializeAsync(IPluginContext context, CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static string CreatePluginDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static string CreateManifestJson(
        string id,
        string displayName,
        string capability,
        string assemblyPath,
        string typeName)
    {
        return JsonSerializer.Serialize(new
        {
            id,
            displayName,
            version = "1.0.0",
            capability,
            menuPath = "Tests/Loader",
            minHostVersion = "1.0.0",
            assembly = assemblyPath,
            type = typeName
        });
    }
}

public sealed class LoaderTestViewPlugin : IViewPlugin
{
    public IPluginManifest Manifest { get; } = new PluginManifest("loader-valid", "Loader Valid", "1", PluginCapability.View, "Tests/Loader", "1");

    public object CreateView() => new object();

    public ValueTask InitializeAsync(IPluginContext context, CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed class LoaderFailingViewPlugin : IViewPlugin
{
    public IPluginManifest Manifest { get; } = new PluginManifest("loader-failing", "Loader Failing", "1", PluginCapability.View, "Tests/Loader", "1");

    public object CreateView() => new object();

    public ValueTask InitializeAsync(IPluginContext context, CancellationToken cancellationToken)
        => ValueTask.FromException(new InvalidOperationException("Simulated init failure"));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
