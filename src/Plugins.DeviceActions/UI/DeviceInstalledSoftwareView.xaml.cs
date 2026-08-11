using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WindowsClientCenter.Intune.Services.Models;

namespace WindowsClientCenter.Plugins.DeviceActions.UI;

public partial class DeviceInstalledSoftwareView : UserControl
{
    public DeviceInstalledSoftwareView()
    {
        InitializeComponent();
    }

    private void InstalledSoftwareGrid_OnPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not DataGrid dataGrid)
        {
            return;
        }

        var row = FindVisualParent<DataGridRow>(e.OriginalSource as DependencyObject);
        if (row?.Item is InstalledSoftwareEntry software)
        {
            dataGrid.SelectedItem = software;
            row.Focus();
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

            child = VisualTreeHelper.GetParent(child);
        }

        return null;
    }
}
