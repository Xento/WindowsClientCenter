using WindowsClientCenter.Plugin.Abstractions.Contracts;
using WindowsClientCenter.Plugin.Abstractions.Models;

namespace WindowsClientCenter.Plugin.Host;

public interface IPluginLoader
{
    IReadOnlyList<PluginLoadResult> LastLoadResults { get; }
    IReadOnlyList<string> LastLoadErrors { get; }

    Task<IReadOnlyList<LoadedPlugin>> DiscoverAsync(
        string pluginDirectory,
        IPluginContext context,
        CancellationToken cancellationToken);
}

public interface IPluginRegistry
{
    IReadOnlyList<LoadedPlugin> All { get; }
    IReadOnlyList<LoadedPlugin> ViewPlugins { get; }
    IReadOnlyList<LoadedPlugin> ActionPlugins { get; }
    IReadOnlyList<LoadedPlugin> BackgroundPlugins { get; }
    IReadOnlyList<LoadedPlugin> RibbonPlugins { get; }
    IReadOnlyList<PluginLoadResult> LastLoadResults { get; }
    IReadOnlyList<string> LastLoadErrors { get; }

    Task LoadAsync(string pluginDirectory, IPluginContext context, CancellationToken cancellationToken);
}

public interface IPluginLifecycle
{
    Task InitializeAsync(LoadedPlugin loadedPlugin, IPluginContext context, CancellationToken cancellationToken);
    Task DisposeAsync(LoadedPlugin loadedPlugin);
}

public sealed record LoadedPlugin(
    IPluginManifest Manifest,
    IClientCenterPlugin Instance,
    PluginCapability Capability,
    string AssemblyPath,
    AssemblyLoadContextHandle LoadContext);

public sealed record PluginLoadResult(
    string ManifestFileName,
    string? PluginId,
    string? DisplayName,
    long ElapsedMilliseconds,
    bool Succeeded,
    string? ErrorMessage);

public sealed record AssemblyLoadContextHandle(object? RawContext);
