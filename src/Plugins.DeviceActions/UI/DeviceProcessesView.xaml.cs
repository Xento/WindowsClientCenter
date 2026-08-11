using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using WindowsClientCenter.Plugins.DeviceActions.Models;
using WindowsClientCenter.Plugins.DeviceActions.ViewModels;

namespace WindowsClientCenter.Plugins.DeviceActions.UI;

public partial class DeviceProcessesView : UserControl
{
    public DeviceProcessesView()
    {
        InitializeComponent();
    }

    private void ProcessesGrid_OnPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not DataGrid dataGrid)
        {
            return;
        }

        var row = FindVisualParent<DataGridRow>(e.OriginalSource as DependencyObject);
        if (row?.Item is DeviceProcessPresentation process)
        {
            dataGrid.SelectedItem = process;
            row.Focus();
        }
    }

    private void ProcessTreeView_OnSelectedItemChanged(object sender, System.Windows.RoutedPropertyChangedEventArgs<object> e)
    {
        if (DataContext is not DeviceProcessesViewModel viewModel)
        {
            return;
        }

        viewModel.SelectProcessFromTreeNode(e.NewValue as DeviceProcessTreeNode);
    }

    private void ProcessTreeView_OnPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not DeviceProcessesViewModel viewModel)
        {
            return;
        }

        var treeViewItem = FindVisualParent<TreeViewItem>(e.OriginalSource as DependencyObject);
        if (treeViewItem?.DataContext is DeviceProcessTreeNode node)
        {
            treeViewItem.IsSelected = true;
            treeViewItem.Focus();
            viewModel.SelectProcessFromTreeNode(node);
        }
    }

    private static T? FindVisualParent<T>(DependencyObject? child)
        where T : DependencyObject
    {
        while (child is not null)
        {
            if (child is T match)
            {
                return match;
            }

            child = child switch
            {
                Visual or Visual3D => VisualTreeHelper.GetParent(child),
                FrameworkContentElement frameworkContentElement => frameworkContentElement.Parent,
                _ => null
            };
        }

        return null;
    }
}
