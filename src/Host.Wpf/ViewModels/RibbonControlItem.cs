using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace WindowsClientCenter.Host.ViewModels;

public abstract partial class RibbonControlItem(
    string pluginId,
    string controlId,
    double? width,
    double? minWidth,
    double? height,
    double? fontSize,
    Thickness padding,
    bool requiresConnectedHost) : ObservableObject
{
    public string PluginId { get; } = pluginId;

    public string ControlId { get; } = controlId;

    public double? Width { get; } = width;

    public double? MinWidth { get; } = minWidth;

    public double? Height { get; } = height;

    public double? FontSize { get; } = fontSize;

    public Thickness Padding { get; } = padding;

    public bool RequiresConnectedHost { get; } = requiresConnectedHost;

    public IAsyncRelayCommand? Command { get; set; }

    [ObservableProperty]
    private bool _isEnabled = !requiresConnectedHost;

    public virtual void UpdateIsEnabled(bool hasConnectedHost)
    {
        IsEnabled = !RequiresConnectedHost || hasConnectedHost;
    }
}

public sealed class RibbonButtonItem(
    string pluginId,
    string controlId,
    string text,
    double? width,
    double? minWidth,
    double? height,
    double? fontSize,
    Thickness padding,
    bool requiresConnectedHost = false) : RibbonControlItem(pluginId, controlId, width, minWidth, height, fontSize, padding, requiresConnectedHost)
{
    public string Text { get; } = text;
}

public sealed partial class RibbonCheckBoxItem(
    string pluginId,
    string controlId,
    string text,
    bool isChecked,
    double? width,
    double? minWidth,
    double? height,
    double? fontSize,
    Thickness padding,
    bool requiresConnectedHost = false) : RibbonControlItem(pluginId, controlId, width, minWidth, height, fontSize, padding, requiresConnectedHost)
{
    public string Text { get; } = text;

    [ObservableProperty]
    private bool _isChecked = isChecked;
}

public sealed class RibbonLabelItem(
    string pluginId,
    string controlId,
    string text,
    double? width,
    double? minWidth,
    double? height,
    double? fontSize,
    Thickness padding,
    bool requiresConnectedHost = false) : RibbonControlItem(pluginId, controlId, width, minWidth, height, fontSize, padding, requiresConnectedHost)
{
    public string Text { get; } = text;
}

public sealed class RibbonSeparatorItem(
    string pluginId,
    string controlId,
    double? height) : RibbonControlItem(pluginId, controlId, null, null, height, null, new Thickness(0), false)
{
}

public sealed class RibbonMenuButtonItem(
    string pluginId,
    string controlId,
    string text,
    double? width,
    double? minWidth,
    double? height,
    double? fontSize,
    Thickness padding,
    bool requiresConnectedHost = false) : RibbonControlItem(pluginId, controlId, width, minWidth, height, fontSize, padding, requiresConnectedHost)
{
    public string Text { get; } = text;

    public ObservableCollection<RibbonMenuEntryItem> MenuItems { get; } = [];

    public override void UpdateIsEnabled(bool hasConnectedHost)
    {
        base.UpdateIsEnabled(hasConnectedHost);
        foreach (var item in MenuItems)
        {
            item.UpdateIsEnabled(hasConnectedHost);
        }
    }
}

public sealed partial class RibbonMenuEntryItem(
    string text,
    bool requiresConnectedHost = false) : ObservableObject
{
    public string Text { get; } = text;

    public bool RequiresConnectedHost { get; } = requiresConnectedHost;

    public IAsyncRelayCommand? Command { get; set; }

    public ObservableCollection<RibbonMenuEntryItem> Children { get; } = [];

    [ObservableProperty]
    private bool _isEnabled = !requiresConnectedHost;

    public void UpdateIsEnabled(bool hasConnectedHost)
    {
        IsEnabled = !RequiresConnectedHost || hasConnectedHost;
        foreach (var child in Children)
        {
            child.UpdateIsEnabled(hasConnectedHost);
        }
    }
}
