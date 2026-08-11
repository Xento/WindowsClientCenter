namespace WindowsClientCenter.Plugin.Abstractions.Models;

public sealed record PluginRibbonMenuItem(
    string ItemId,
    string Text,
    IReadOnlyList<PluginRibbonMenuItem>? Children = null);
