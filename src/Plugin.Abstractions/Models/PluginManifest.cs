using WindowsClientCenter.Plugin.Abstractions.Contracts;

namespace WindowsClientCenter.Plugin.Abstractions.Models;

public sealed record PluginManifest(
    string Id,
    string DisplayName,
    string Version,
    PluginCapability Capability,
    string MenuPath,
    string MinHostVersion) : IPluginManifest;
