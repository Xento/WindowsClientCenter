using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Serialization;
using WindowsClientCenter.Plugin.Abstractions.Contracts;
using WindowsClientCenter.Plugin.Abstractions.Models;
using WindowsClientCenter.Plugin.Host.Internal;
using Microsoft.Extensions.Logging;

namespace WindowsClientCenter.Plugin.Host;

public sealed class PluginLoader(ILogger<PluginLoader> logger, IPluginLifecycle lifecycle, IHostStatusLogSink hostStatusLogSink) : IPluginLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };
    private readonly List<PluginLoadResult> _lastLoadResults = [];

    public IReadOnlyList<PluginLoadResult> LastLoadResults => _lastLoadResults;
    public IReadOnlyList<string> LastLoadErrors => _lastLoadResults
        .Where(static result => !result.Succeeded && !string.IsNullOrWhiteSpace(result.ErrorMessage))
        .Select(static result => $"{result.ManifestFileName}: {result.ErrorMessage}")
        .ToArray();

    static PluginLoader()
    {
        JsonOptions.Converters.Add(new JsonStringEnumConverter());
    }

    public async Task<IReadOnlyList<LoadedPlugin>> DiscoverAsync(
        string pluginDirectory,
        IPluginContext context,
        CancellationToken cancellationToken)
    {
        _lastLoadResults.Clear();

        if (!Directory.Exists(pluginDirectory))
        {
            RecordFailure("<plugin-directory>", null, null, 0, $"Plugin directory does not exist: {pluginDirectory}");
            logger.LogWarning("Plugin directory {PluginDirectory} does not exist.", pluginDirectory);
            return [];
        }

        var loaded = new List<LoadedPlugin>();
        var manifestPaths = Directory
            .EnumerateFiles(pluginDirectory, "*.plugin.json", SearchOption.TopDirectoryOnly)
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var manifestPath in manifestPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var manifestFileName = Path.GetFileName(manifestPath);
            var stopwatch = Stopwatch.StartNew();
            string? pluginId = null;
            string? displayName = null;
            CollectiblePluginLoadContext? alc = null;
            IClientCenterPlugin? instance = null;
            LoadedPlugin? plugin = null;

            try
            {
                hostStatusLogSink.Append($"Loading plugin manifest: {manifestFileName}...");
                var manifestText = await File.ReadAllTextAsync(manifestPath, cancellationToken);
                var manifestFile = JsonSerializer.Deserialize<PluginManifestFile>(manifestText, JsonOptions);
                if (manifestFile is null)
                {
                    throw new InvalidOperationException("Manifest is empty or invalid.");
                }

                pluginId = manifestFile.Id;
                displayName = manifestFile.DisplayName;
                hostStatusLogSink.Append($"Loading plugin: {displayName} ({pluginId})...");

                var assemblyPath = Path.GetFullPath(Path.Combine(pluginDirectory, manifestFile.Assembly));
                if (!File.Exists(assemblyPath))
                {
                    throw new FileNotFoundException(
                        $"Assembly '{manifestFile.Assembly}' from manifest '{manifestFileName}' was not found.",
                        assemblyPath);
                }

                var assembly = LoadPluginAssembly(manifestFile.Capability, assemblyPath, out alc);
                var type = assembly.GetType(manifestFile.Type, throwOnError: true, ignoreCase: false);
                if (type is null)
                {
                    throw new InvalidOperationException($"Type '{manifestFile.Type}' was not found in {assemblyPath}.");
                }

                instance = Activator.CreateInstance(type) as IClientCenterPlugin;
                if (instance is null)
                {
                    throw new InvalidOperationException($"Type '{manifestFile.Type}' does not implement IClientCenterPlugin.");
                }

                var manifest = new PluginManifest(
                    manifestFile.Id,
                    manifestFile.DisplayName,
                    manifestFile.Version,
                    manifestFile.Capability,
                    manifestFile.MenuPath,
                    manifestFile.MinHostVersion);

                plugin = new LoadedPlugin(
                    manifest,
                    instance,
                    manifest.Capability,
                    assemblyPath,
                    new AssemblyLoadContextHandle(alc));

                await lifecycle.InitializeAsync(plugin, context, cancellationToken);
                loaded.Add(plugin);
                stopwatch.Stop();
                _lastLoadResults.Add(new PluginLoadResult(
                    manifestFileName,
                    manifest.Id,
                    manifest.DisplayName,
                    stopwatch.ElapsedMilliseconds,
                    true,
                    null));

                hostStatusLogSink.Append($"Plugin loaded: {manifest.DisplayName} ({manifest.Id}) in {stopwatch.ElapsedMilliseconds} ms.");
                logger.LogInformation("Loaded plugin {PluginId} ({DisplayName}) from {AssemblyPath}", manifest.Id, manifest.DisplayName, assemblyPath);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                await CleanupFailedLoadAsync(plugin, instance, alc, manifestFileName);
                RecordFailure(manifestFileName, pluginId, displayName, stopwatch.ElapsedMilliseconds, ex.Message);
                logger.LogError(ex, "Failed loading plugin manifest {ManifestPath}", manifestPath);
            }
        }

        return loaded;
    }

    private void RecordFailure(
        string manifestFileName,
        string? pluginId,
        string? displayName,
        long elapsedMilliseconds,
        string errorMessage)
    {
        _lastLoadResults.Add(new PluginLoadResult(
            manifestFileName,
            pluginId,
            displayName,
            elapsedMilliseconds,
            false,
            errorMessage));

        hostStatusLogSink.Append($"Plugin load failed: {manifestFileName}: {errorMessage}");
    }

    private static Assembly LoadPluginAssembly(
        PluginCapability capability,
        string assemblyPath,
        out CollectiblePluginLoadContext? alc)
    {
        // WPF compiled XAML resources do not resolve reliably from a collectible ALC.
        // Load view plugins into the default context so InitializeComponent can find BAML resources.
        if (capability == PluginCapability.View)
        {
            alc = null;
            return AssemblyLoadContext.Default.LoadFromAssemblyPath(assemblyPath);
        }

        alc = new CollectiblePluginLoadContext(assemblyPath);
        return alc.LoadFromAssemblyPath(assemblyPath);
    }

    private async Task CleanupFailedLoadAsync(
        LoadedPlugin? plugin,
        IClientCenterPlugin? instance,
        CollectiblePluginLoadContext? alc,
        string manifestFileName)
    {
        try
        {
            if (plugin is not null)
            {
                await lifecycle.DisposeAsync(plugin);
            }
            else if (instance is not null)
            {
                await instance.DisposeAsync();
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Cleanup failed for plugin manifest {ManifestFileName}", manifestFileName);
        }
        finally
        {
            try
            {
                alc?.Unload();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Unload failed for plugin manifest {ManifestFileName}", manifestFileName);
            }
        }
    }
}
