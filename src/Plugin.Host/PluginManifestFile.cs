using WindowsClientCenter.Plugin.Abstractions.Models;

namespace WindowsClientCenter.Plugin.Host;

public sealed record PluginManifestFile(
    string Id,
    string DisplayName,
    string Version,
    PluginCapability Capability,
    string MenuPath,
    string MinHostVersion,
    string Assembly,
    string Type);
