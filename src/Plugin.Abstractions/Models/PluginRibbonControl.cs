namespace WindowsClientCenter.Plugin.Abstractions.Models;

public sealed record PluginRibbonControl(
    string ControlId,
    PluginRibbonControlKind Kind,
    string? Text = null,
    bool? IsChecked = null,
    double? Width = null,
    double? MinWidth = null,
    double? Height = null,
    double? FontSize = null,
    double? HorizontalPadding = null,
    double? VerticalPadding = null,
    IReadOnlyList<PluginRibbonMenuItem>? MenuItems = null,
    bool RequiresConnectedHost = false);
