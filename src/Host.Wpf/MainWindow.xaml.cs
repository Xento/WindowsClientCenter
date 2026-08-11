using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WindowsClientCenter.Host.ViewModels;

namespace WindowsClientCenter.Host;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel;
    private readonly TaskCompletionSource _initializationCompletionSource = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public MainWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = _viewModel;

        Loaded += OnLoaded;
    }

    public Task WaitForInitializationAsync()
    {
        return _initializationCompletionSource.Task;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        try
        {
            await _viewModel.InitializeAsync(CancellationToken.None);
            _initializationCompletionSource.TrySetResult();
        }
        catch (Exception ex)
        {
            _initializationCompletionSource.TrySetException(ex);
            if (System.Windows.Application.Current.Properties.Contains("ScreenshotCaptureMode"))
            {
                return;
            }

            System.Windows.MessageBox.Show(
                this,
                ex.Message,
                "Startup Failed",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
        }
    }

    private void NavigationTree_OnSelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        _viewModel.OnNavigationSelected(e.NewValue as NavigationNode);
    }

    private void StatusLogTextBox_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        StatusLogTextBox.ScrollToEnd();
    }

    private void HostComboBox_OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.Enter || !_viewModel.ConnectHostCommand.CanExecute(null))
        {
            return;
        }

        _viewModel.ConnectHostCommand.Execute(null);
        e.Handled = true;
    }

    private void RibbonMenuButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button button || button.ContextMenu is null)
        {
            return;
        }

        button.ContextMenu.PlacementTarget = button;
        button.ContextMenu.IsOpen = true;
        e.Handled = true;
    }
}
