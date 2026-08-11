using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Globalization;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using WindowsClientCenter.Plugins.IntuneAgent.ViewModels;
using Microsoft.Win32;

namespace WindowsClientCenter.Plugins.IntuneAgent.UI;

public partial class IntuneAgentView : System.Windows.Controls.UserControl
{
    private static bool _browserFeatureConfigured;
    private readonly DispatcherTimer _imeTimelineRefreshTimer;
    private INotifyPropertyChanged? _propertyChangedSource;
    private bool _imeTimelineRefreshInProgress;
    private bool _followImeTimelineTail;

    public IntuneAgentView()
    {
        EnsureBrowserFeatureMode();
        InitializeComponent();
        ImeTimelineDataGrid.CommandBindings.Add(new CommandBinding(ApplicationCommands.Copy, ImeTimelineDataGrid_OnCopyExecuted));
        _imeTimelineRefreshTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(3)
        };
        _imeTimelineRefreshTimer.Tick += ImeTimelineRefreshTimer_OnTick;
        PolicyResultBrowser.LoadCompleted += (_, _) => SuppressBrowserScriptErrors();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        DataContextChanged += OnDataContextChanged;
    }

    private async void MdmEventsDataGrid_OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.VerticalChange <= 0 || e.ExtentHeightChange != 0)
        {
            return;
        }

        if (DataContext is not IntuneAgentViewModel viewModel)
        {
            return;
        }

        if (e.VerticalOffset + e.ViewportHeight < e.ExtentHeight - 12)
        {
            return;
        }

        await viewModel.LoadMoreMdmEventsAsync();
    }

    private void OnLoaded(object sender, System.Windows.RoutedEventArgs e)
    {
        SuppressBrowserScriptErrors();
        _imeTimelineRefreshTimer.Start();
        _followImeTimelineTail = IsLastImeTimelineRowSelected();
        AttachViewModelNotifications();
        NavigatePolicyResultReport();
    }

    private void OnUnloaded(object sender, System.Windows.RoutedEventArgs e)
    {
        _imeTimelineRefreshTimer.Stop();
        DetachViewModelNotifications();
    }

    private void OnDataContextChanged(object sender, System.Windows.DependencyPropertyChangedEventArgs e)
    {
        DetachViewModelNotifications();
        AttachViewModelNotifications();
        NavigatePolicyResultReport();
    }

    private void AttachViewModelNotifications()
    {
        if (DataContext is not INotifyPropertyChanged notify)
        {
            return;
        }

        _propertyChangedSource = notify;
        _propertyChangedSource.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void DetachViewModelNotifications()
    {
        if (_propertyChangedSource is null)
        {
            return;
        }

        _propertyChangedSource.PropertyChanged -= OnViewModelPropertyChanged;
        _propertyChangedSource = null;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!string.Equals(e.PropertyName, nameof(IntuneAgentViewModel.PolicyReportHtmlPath), StringComparison.Ordinal) &&
            !string.Equals(e.PropertyName, nameof(IntuneAgentViewModel.PolicyReportHtmlContent), StringComparison.Ordinal))
        {
            return;
        }

        Dispatcher.Invoke(NavigatePolicyResultReport);
    }

    private void NavigatePolicyResultReport()
    {
        if (PolicyResultBrowser is null)
        {
            return;
        }

        if (DataContext is not IntuneAgentViewModel viewModel)
        {
            PolicyResultBrowser.NavigateToString("<html><body style='font-family:Segoe UI; color:#445468;'>No policy result context available.</body></html>");
            return;
        }

        if (!string.IsNullOrWhiteSpace(viewModel.PolicyReportHtmlContent))
        {
            PolicyResultBrowser.NavigateToString(viewModel.PolicyReportHtmlContent);
            return;
        }

        if (string.IsNullOrWhiteSpace(viewModel.PolicyReportHtmlPath) || !File.Exists(viewModel.PolicyReportHtmlPath))
        {
            PolicyResultBrowser.NavigateToString("<html><body style='font-family:Segoe UI; color:#445468;'>No policy result report generated yet.</body></html>");
            return;
        }

        PolicyResultBrowser.Source = new Uri(viewModel.PolicyReportHtmlPath, UriKind.Absolute);
    }

    private static void EnsureBrowserFeatureMode()
    {
        if (_browserFeatureConfigured)
        {
            return;
        }

        _browserFeatureConfigured = true;
        try
        {
            var appName = $"{Process.GetCurrentProcess().ProcessName}.exe";
            const int ie11EdgeMode = 11001;
            using var emulationKey = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Internet Explorer\Main\FeatureControl\FEATURE_BROWSER_EMULATION");
            emulationKey?.SetValue(appName, ie11EdgeMode, RegistryValueKind.DWord);
        }
        catch
        {
            // Best effort only.
        }
    }

    private void SuppressBrowserScriptErrors()
    {
        try
        {
            var activeX = PolicyResultBrowser.GetType().InvokeMember(
                "ActiveXInstance",
                BindingFlags.GetProperty | BindingFlags.Instance | BindingFlags.NonPublic,
                null,
                PolicyResultBrowser,
                null);
            activeX?.GetType().InvokeMember(
                "Silent",
                BindingFlags.SetProperty,
                null,
                activeX,
                [true]);
        }
        catch
        {
            // Best effort only.
        }
    }

    private async void ImeTimelineRefreshTimer_OnTick(object? sender, EventArgs e)
    {
        if (_imeTimelineRefreshInProgress)
        {
            return;
        }

        if (DataContext is not IntuneAgentViewModel viewModel)
        {
            return;
        }

        if (!ImeTimelineDataGrid.IsVisible || viewModel.IsLocalBusy || viewModel.ImeTimelineEntries.Count == 0)
        {
            return;
        }

        _imeTimelineRefreshInProgress = true;
        var shouldFollowTail = _followImeTimelineTail && IsImeTimelineNearBottom() && IsLastImeTimelineRowSelected();
        var previousLastEntryKey = GetImeTimelineLastEntryKey();
        var previousItemCount = ImeTimelineDataGrid.Items.Count;
        try
        {
            await viewModel.RefreshImeLogTimelineInBackgroundAsync();
            if (shouldFollowTail && HasImeTimelineTailAdvanced(previousItemCount, previousLastEntryKey))
            {
                SelectAndScrollImeTimelineToLastRow();
            }
        }
        finally
        {
            _imeTimelineRefreshInProgress = false;
        }
    }

    private void ImeTimelineDataGrid_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateImeTimelineTailFollowState();
    }

    private void ImeTimelineDataGrid_OnCopyExecuted(object sender, ExecutedRoutedEventArgs e)
    {
        if (!ReferenceEquals(ImeTimelineDataGrid.CurrentColumn, ImeTimelineEntryColumn) ||
            ImeTimelineDataGrid.SelectedCells.Count != 1 ||
            ImeTimelineDataGrid.SelectedItem is not WindowsClientCenter.Intune.Services.Models.ImeLogTimelineEntry entry)
        {
            return;
        }

        Clipboard.SetText(FormatImeTimelineClipboardContent(entry));
        e.Handled = true;
    }

    private void ImeTimelineDataGrid_OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        UpdateImeTimelineTailFollowState();
    }

    private void ImeTimelineDataGrid_OnSizeChanged(object sender, System.Windows.SizeChangedEventArgs e)
    {
        if (ImeTimelineEntryColumn is null)
        {
            return;
        }

        var targetWidth = Math.Max(240d, ImeTimelineDataGrid.ActualWidth * 0.8d);
        ImeTimelineEntryColumn.Width = new DataGridLength(targetWidth, DataGridLengthUnitType.Pixel);
    }

    private void ImeTimelineDataGrid_OnMouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (DataContext is not IntuneAgentViewModel viewModel || viewModel.SelectedImeLogEntry is null)
        {
            return;
        }

        viewModel.ToggleImeRelatedHighlightForSelectedEntry();
    }

    private bool IsLastImeTimelineRowSelected()
    {
        return ImeTimelineDataGrid.Items.Count > 0 &&
               ImeTimelineDataGrid.SelectedIndex == ImeTimelineDataGrid.Items.Count - 1;
    }

    private void UpdateImeTimelineTailFollowState()
    {
        _followImeTimelineTail = IsLastImeTimelineRowSelected() && IsImeTimelineNearBottom();
    }

    private bool IsImeTimelineNearBottom()
    {
        var scrollViewer = FindDescendant<ScrollViewer>(ImeTimelineDataGrid);
        if (scrollViewer is null)
        {
            return false;
        }

        return scrollViewer.VerticalOffset + scrollViewer.ViewportHeight >= scrollViewer.ExtentHeight - 12d;
    }

    private string? GetImeTimelineLastEntryKey()
    {
        if (ImeTimelineDataGrid.Items.Count == 0)
        {
            return null;
        }

        return BuildImeTimelineEntryKey(ImeTimelineDataGrid.Items[ImeTimelineDataGrid.Items.Count - 1]);
    }

    private bool HasImeTimelineTailAdvanced(int previousItemCount, string? previousLastEntryKey)
    {
        if (ImeTimelineDataGrid.Items.Count == 0)
        {
            return false;
        }

        if (ImeTimelineDataGrid.Items.Count != previousItemCount)
        {
            return true;
        }

        var currentLastEntryKey = GetImeTimelineLastEntryKey();
        return !string.Equals(previousLastEntryKey, currentLastEntryKey, StringComparison.Ordinal);
    }

    private static string? BuildImeTimelineEntryKey(object? item)
    {
        if (item is not Intune.Services.Models.ImeLogTimelineEntry entry)
        {
            return null;
        }

        return $"{entry.SourceFile}|{entry.LineNumber}|{entry.RawLine}";
    }

    private static string FormatImeTimelineClipboardContent(WindowsClientCenter.Intune.Services.Models.ImeLogTimelineEntry entry)
    {
        var timestamp = entry.TimeCreated?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? string.Empty;
        return string.Join(
            " ",
            new[] { timestamp, entry.Severity, entry.Message }
                .Where(static part => !string.IsNullOrWhiteSpace(part)));
    }

    private static T? FindDescendant<T>(DependencyObject? root)
        where T : DependencyObject
    {
        if (root is null)
        {
            return null;
        }

        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                return match;
            }

            var nested = FindDescendant<T>(child);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    private void SelectAndScrollImeTimelineToLastRow()
    {
        if (ImeTimelineDataGrid.Items.Count == 0)
        {
            return;
        }

        var lastIndex = ImeTimelineDataGrid.Items.Count - 1;
        var lastItem = ImeTimelineDataGrid.Items[lastIndex];
        ImeTimelineDataGrid.SelectedItem = lastItem;
        ImeTimelineDataGrid.UpdateLayout();
        ImeTimelineDataGrid.ScrollIntoView(lastItem);
        UpdateImeTimelineTailFollowState();
    }
}
