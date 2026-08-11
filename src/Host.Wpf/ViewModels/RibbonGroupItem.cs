using System.Collections.ObjectModel;

namespace WindowsClientCenter.Host.ViewModels;

public sealed class RibbonGroupItem(
    string groupId,
    string title,
    System.Windows.Media.Brush? background,
    System.Windows.Media.Brush? borderBrush,
    System.Windows.Media.Brush? titleForeground)
{
    public string GroupId { get; } = groupId;

    public string Title { get; } = title;

    public System.Windows.Media.Brush? Background { get; } = background;

    public System.Windows.Media.Brush? BorderBrush { get; } = borderBrush;

    public System.Windows.Media.Brush? TitleForeground { get; } = titleForeground;

    public ObservableCollection<RibbonControlItem> Controls { get; } = [];
}
