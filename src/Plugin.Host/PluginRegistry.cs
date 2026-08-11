using System.Runtime.Loader;
using WindowsClientCenter.Plugin.Abstractions.Contracts;
using WindowsClientCenter.Plugin.Abstractions.Models;
using Microsoft.Extensions.Logging;

namespace WindowsClientCenter.Plugin.Host;

public sealed class PluginRegistry(ILogger<PluginRegistry> logger, IPluginLoader pluginLoader, IPluginLifecycle lifecycle, IHostStatusLogSink hostStatusLogSink) : IPluginRegistry
{
    private readonly List<LoadedPlugin> _all = [];
    private readonly List<PluginLoadResult> _lastLoadResults = [];

    public IReadOnlyList<LoadedPlugin> All => _all;
    public IReadOnlyList<PluginLoadResult> LastLoadResults => _lastLoadResults;
    public IReadOnlyList<string> LastLoadErrors => _lastLoadResults
        .Where(static result => !result.Succeeded && !string.IsNullOrWhiteSpace(result.ErrorMessage))
        .Select(static result => $"{result.ManifestFileName}: {result.ErrorMessage}")
        .ToArray();

    public IReadOnlyList<LoadedPlugin> ViewPlugins => _all.Where(p => p.Capability == PluginCapability.View).ToList();

    public IReadOnlyList<LoadedPlugin> ActionPlugins => _all.Where(p => p.Capability == PluginCapability.Action).ToList();

    public IReadOnlyList<LoadedPlugin> BackgroundPlugins => _all.Where(p => p.Capability == PluginCapability.BackgroundTask).ToList();

    public IReadOnlyList<LoadedPlugin> RibbonPlugins => _all.Where(p => p.Capability == PluginCapability.Ribbon).ToList();

    public async Task LoadAsync(string pluginDirectory, IPluginContext context, CancellationToken cancellationToken)
    {
        foreach (var existing in _all.ToArray())
        {
            try
            {
                await lifecycle.DisposeAsync(existing);
                if (existing.LoadContext.RawContext is AssemblyLoadContext alc)
                {
                    alc.Unload();
                }
            }
            catch (Exception ex)
            {
                hostStatusLogSink.Append($"[Exception][Plugin Unload:{existing.Manifest.Id}] {ex.GetType().Name}: {ex.Message}");
                logger.LogWarning(ex, "Error unloading plugin {PluginId}", existing.Manifest.Id);
            }
        }

        _all.Clear();
        _lastLoadResults.Clear();
        _all.AddRange(await pluginLoader.DiscoverAsync(pluginDirectory, context, cancellationToken));
        _lastLoadResults.AddRange(pluginLoader.LastLoadResults);
    }
}
