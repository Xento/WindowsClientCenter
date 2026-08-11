namespace WindowsClientCenter.Plugin.Abstractions.Models;

public sealed record PluginNavigationEntry(
    string MenuPath,
    string? NavigationTarget = null,
    string? IconGlyph = null,
    bool? IsExpanded = null,
    bool IsContainerOnly = false);
