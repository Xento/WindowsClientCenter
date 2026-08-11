using System.Reflection;
using System.Runtime.Loader;

namespace WindowsClientCenter.Plugin.Host.Internal;

internal sealed class CollectiblePluginLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;
    private static readonly string[] SharedAssemblyNames =
    [
        "WindowsClientCenter.Plugin.Abstractions",
        "Plugin.Abstractions",
        "WindowsClientCenter.Intune.Services",
        "Intune.Services",
        "WindowsClientCenter.Defender.Contracts",
        "Defender.Contracts",
        "Microsoft.Extensions.Logging.Abstractions",
        "Microsoft.Extensions.Logging",
        "Microsoft.Extensions.DependencyInjection.Abstractions",
        "Microsoft.Extensions.DependencyInjection"
    ];

    public CollectiblePluginLoadContext(string pluginAssemblyPath)
        : base($"Plugin::{Path.GetFileNameWithoutExtension(pluginAssemblyPath)}", isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(pluginAssemblyPath);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (SharedAssemblyNames.Contains(assemblyName.Name, StringComparer.Ordinal))
        {
            var loadedAssembly = Default.Assemblies.FirstOrDefault(a =>
                string.Equals(a.GetName().Name, assemblyName.Name, StringComparison.Ordinal));

            if (loadedAssembly is not null)
            {
                return loadedAssembly;
            }

            try
            {
                return Assembly.Load(assemblyName);
            }
            catch
            {
                return null;
            }
        }

        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        if (path is null)
        {
            return null;
        }

        return LoadFromAssemblyPath(path);
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        if (path is null)
        {
            return IntPtr.Zero;
        }

        return LoadUnmanagedDllFromPath(path);
    }
}
