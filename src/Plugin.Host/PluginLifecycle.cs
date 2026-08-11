using WindowsClientCenter.Plugin.Abstractions.Contracts;
using Microsoft.Extensions.Logging;

namespace WindowsClientCenter.Plugin.Host;

public sealed class PluginLifecycle : IPluginLifecycle
{
    public async Task InitializeAsync(LoadedPlugin loadedPlugin, IPluginContext context, CancellationToken cancellationToken)
    {
        await loadedPlugin.Instance.InitializeAsync(CreateScopedContext(loadedPlugin.Manifest.Id, context), cancellationToken);
    }

    public async Task DisposeAsync(LoadedPlugin loadedPlugin)
    {
        await loadedPlugin.Instance.DisposeAsync();
    }

    private static IPluginContext CreateScopedContext(string pluginId, IPluginContext context)
    {
        const string pluginSettingsPrefix = "PluginSettings:";
        var scopedPluginPrefix = $"{pluginSettingsPrefix}{pluginId}:";
        Dictionary<string, string>? effectiveSettings = null;
        var hasScopedSettings = false;
        var hasPrefixedSettings = false;

        foreach (var entry in context.Settings)
        {
            if (entry.Key.StartsWith(pluginSettingsPrefix, StringComparison.OrdinalIgnoreCase))
            {
                hasPrefixedSettings = true;
                continue;
            }

            effectiveSettings ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            effectiveSettings[entry.Key] = entry.Value;
        }

        foreach (var entry in context.Settings)
        {
            if (!entry.Key.StartsWith(scopedPluginPrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            effectiveSettings ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            effectiveSettings[entry.Key[scopedPluginPrefix.Length..]] = entry.Value;
            hasScopedSettings = true;
        }

        if (!hasPrefixedSettings && !hasScopedSettings)
        {
            return context;
        }

        return new ScopedPluginContext(
            context.Logger,
            context.Services,
            context.EnvironmentName,
            effectiveSettings ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
    }

    private sealed class ScopedPluginContext(
        ILogger logger,
        IServiceProvider services,
        string environmentName,
        IReadOnlyDictionary<string, string> settings) : IPluginContext
    {
        public ILogger Logger { get; } = logger;

        public IServiceProvider Services { get; } = services;

        public string EnvironmentName { get; } = environmentName;

        public IReadOnlyDictionary<string, string> Settings { get; } = settings;
    }
}
