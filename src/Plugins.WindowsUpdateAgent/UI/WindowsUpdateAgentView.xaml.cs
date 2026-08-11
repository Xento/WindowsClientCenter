using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using WindowsClientCenter.Plugins.WindowsUpdateAgent.Models;
using WindowsClientCenter.Plugins.WindowsUpdateAgent.ViewModels;

namespace WindowsClientCenter.Plugins.WindowsUpdateAgent.UI;

public partial class WindowsUpdateAgentView : UserControl
{
    private static readonly Brush HighlightRowBrush = new SolidColorBrush(Color.FromRgb(255, 247, 209));

    private INotifyCollectionChanged? _entriesCollection;
    private INotifyPropertyChanged? _viewModelNotifications;

    public WindowsUpdateAgentView()
    {
        InitializeComponent();

        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_entriesCollection is not null)
        {
            _entriesCollection.CollectionChanged -= OnEntriesCollectionChanged;
            _entriesCollection = null;
        }

        if (_viewModelNotifications is not null)
        {
            _viewModelNotifications.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModelNotifications = null;
        }

        if (e.NewValue is WindowsUpdateAgentViewModel vm)
        {
            _entriesCollection = vm.Entries;
            _entriesCollection.CollectionChanged += OnEntriesCollectionChanged;
            _viewModelNotifications = vm;
            _viewModelNotifications.PropertyChanged += OnViewModelPropertyChanged;
            RefreshVisibleRowHighlights();
        }
    }

    private void OnEntriesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action is not NotifyCollectionChangedAction.Add)
        {
            return;
        }

        if (EventsGrid.Items.Count == 0)
        {
            return;
        }

        if (!IsLoaded)
        {
            return;
        }

        if (DataContext is not WindowsUpdateAgentViewModel vm ||
            !string.IsNullOrWhiteSpace(vm.HighlightedUpdateId))
        {
            return;
        }

        _ = Dispatcher.InvokeAsync(() =>
        {
            var lastItem = EventsGrid.Items[EventsGrid.Items.Count - 1];
            EventsGrid.ScrollIntoView(lastItem);
        });
    }

    private void EventsGrid_OnLoadingRow(object sender, DataGridRowEventArgs e)
    {
        ApplyRowHighlight(e.Row);
    }

    private void EventsGrid_OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not WindowsUpdateAgentViewModel vm)
        {
            return;
        }

        var clickedCell = FindParent<DataGridCell>(e.OriginalSource as DependencyObject);
        if (clickedCell is null || !IsUpdateIdColumn(clickedCell.Column))
        {
            return;
        }

        if (clickedCell.DataContext is not ReportingEventsLogEntry entry)
        {
            return;
        }

        vm.ToggleUpdateIdHighlight(entry.UpdateId);
        RefreshVisibleRowHighlights();
        e.Handled = true;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!string.Equals(e.PropertyName, nameof(WindowsUpdateAgentViewModel.HighlightedUpdateId), StringComparison.Ordinal))
        {
            return;
        }

        _ = Dispatcher.InvokeAsync(RefreshVisibleRowHighlights);
    }

    private void RefreshVisibleRowHighlights()
    {
        foreach (var item in EventsGrid.Items)
        {
            if (EventsGrid.ItemContainerGenerator.ContainerFromItem(item) is DataGridRow row)
            {
                ApplyRowHighlight(row);
            }
        }
    }

    private void ApplyRowHighlight(DataGridRow row)
    {
        if (DataContext is not WindowsUpdateAgentViewModel vm ||
            row.Item is not ReportingEventsLogEntry entry ||
            string.IsNullOrWhiteSpace(vm.HighlightedUpdateId) ||
            string.IsNullOrWhiteSpace(entry.UpdateId) ||
            !string.Equals(vm.HighlightedUpdateId, entry.UpdateId, StringComparison.OrdinalIgnoreCase))
        {
            row.ClearValue(BackgroundProperty);
            row.ClearValue(FontWeightProperty);
            return;
        }

        row.Background = HighlightRowBrush;
        row.FontWeight = FontWeights.SemiBold;
    }

    private static bool IsUpdateIdColumn(DataGridColumn? column)
    {
        if (column is not DataGridBoundColumn boundColumn || boundColumn.Binding is not Binding binding)
        {
            return false;
        }

        return string.Equals(binding.Path?.Path, nameof(ReportingEventsLogEntry.UpdateId), StringComparison.Ordinal);
    }

    private static T? FindParent<T>(DependencyObject? child) where T : DependencyObject
    {
        var current = child;
        while (current is not null)
        {
            if (current is T target)
            {
                return target;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private void InstallProgressList_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) != ModifierKeys.Control || e.Key != Key.C)
        {
            return;
        }

        if (sender is not ListBox listBox)
        {
            return;
        }

        var selectedLines = listBox.SelectedItems
            .Cast<object>()
            .Select(item => item?.ToString() ?? string.Empty)
            .ToArray();

        if (selectedLines.Length == 0 && listBox.SelectedItem is not null)
        {
            selectedLines = [(listBox.SelectedItem.ToString() ?? string.Empty)];
        }

        if (selectedLines.Length == 0)
        {
            return;
        }

        Clipboard.SetText(string.Join(Environment.NewLine, selectedLines));
        e.Handled = true;
    }
}
