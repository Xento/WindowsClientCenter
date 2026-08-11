using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace WindowsClientCenter.Host.ViewModels;

public partial class NavigationNode : ObservableObject
{
    private string? _pluginId;
    private string? _navigationTarget;
    private string _iconGlyph;

    public NavigationNode(string title, string nodePath, string? pluginId = null, string? navigationTarget = null, string? iconGlyph = null)
    {
        Title = title;
        NodePath = nodePath;
        _pluginId = pluginId;
        _navigationTarget = navigationTarget;
        _iconGlyph = string.IsNullOrWhiteSpace(iconGlyph) ? "\uE8B7" : iconGlyph;
    }

    public string Title { get; }

    public string NodePath { get; }

    public string? PluginId
    {
        get => _pluginId;
        private set => SetProperty(ref _pluginId, value);
    }

    public string? NavigationTarget
    {
        get => _navigationTarget;
        private set => SetProperty(ref _navigationTarget, value);
    }

    public string IconGlyph
    {
        get => _iconGlyph;
        private set => SetProperty(ref _iconGlyph, value);
    }

    public ObservableCollection<NavigationNode> Children { get; } = [];

    [ObservableProperty]
    private bool _isExpanded = true;

    [ObservableProperty]
    private bool _isSelected;

    public void AssignPlugin(string pluginId, string? navigationTarget, string? iconGlyph = null)
    {
        PluginId = pluginId;
        NavigationTarget = navigationTarget;
        if (!string.IsNullOrWhiteSpace(iconGlyph))
        {
            IconGlyph = iconGlyph;
        }
    }

    public void AssignIcon(string? iconGlyph)
    {
        if (!string.IsNullOrWhiteSpace(iconGlyph))
        {
            IconGlyph = iconGlyph;
        }
    }
}
