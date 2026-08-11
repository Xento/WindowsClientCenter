namespace WindowsClientCenter.Plugin.Abstractions.Models;

public sealed record PluginRibbonGroup(
    string GroupId,
    string Title,
    IReadOnlyList<PluginRibbonControl> Controls,
    double? DefaultControlMinWidth = null,
    double? DefaultControlHeight = null,
    double? DefaultControlFontSize = null,
    double? DefaultControlHorizontalPadding = null,
    double? DefaultControlVerticalPadding = null,
    string? Background = null,
    string? BorderBrush = null,
    string? TitleForeground = null);
