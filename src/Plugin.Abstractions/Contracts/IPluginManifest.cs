using WindowsClientCenter.Plugin.Abstractions.Models;

namespace WindowsClientCenter.Plugin.Abstractions.Contracts;

public interface IPluginManifest
{
    string Id { get; }
    string DisplayName { get; }
    string Version { get; }
    PluginCapability Capability { get; }
    string MenuPath { get; }
    string MinHostVersion { get; }
}
