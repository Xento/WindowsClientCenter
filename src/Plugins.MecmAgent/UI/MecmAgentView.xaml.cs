using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using WindowsClientCenter.Intune.Services.Models;
using WindowsClientCenter.Plugins.MecmAgent.Models;
using WindowsClientCenter.Plugins.MecmAgent.ViewModels;

namespace WindowsClientCenter.Plugins.MecmAgent.UI;

public partial class MecmAgentView : UserControl
{
    public MecmAgentView()
    {
        InitializeComponent();
    }

    private void ApplicationsGrid_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not MecmAgentViewModel viewModel)
        {
            return;
        }

        viewModel.UpdateSelectedApplications(ApplicationsGrid.SelectedItems.Cast<MecmApplicationRow>().ToArray());
    }

    private void ApplicationsGrid_OnPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var row = FindVisualParent<DataGridRow>(e.OriginalSource as DependencyObject);
        if (row?.Item is not MecmApplicationRow applicationRow)
        {
            return;
        }

        ApplicationsGrid.SelectedItems.Clear();
        row.IsSelected = true;
        ApplicationsGrid.SelectedItem = applicationRow;

        if (DataContext is MecmAgentViewModel viewModel)
        {
            viewModel.UpdateSelectedApplications([applicationRow]);
        }
    }

    private void PendingUpdatesGrid_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not MecmAgentViewModel viewModel)
        {
            return;
        }

        viewModel.UpdateSelectedPendingUpdates(PendingUpdatesGrid.SelectedItems.Cast<MecmPendingUpdateRow>().ToArray());
    }

    private void PackagesGrid_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not MecmAgentViewModel viewModel)
        {
            return;
        }

        viewModel.UpdateSelectedPackages(PackagesGrid.SelectedItems.Cast<MecmPackageRow>().ToArray());
    }

    private void EvaluateApplicationsButton_OnClick(object sender, System.Windows.RoutedEventArgs e)
    {
        if (sender is not Button button || button.ContextMenu is null)
        {
            return;
        }

        button.ContextMenu.DataContext = DataContext;
        button.ContextMenu.PlacementTarget = button;
        button.ContextMenu.Placement = PlacementMode.Bottom;
        button.ContextMenu.IsOpen = true;
    }

    private async void OverviewActionButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MecmAgentViewModel viewModel ||
            sender is not Button button ||
            !TryGetOverviewAction(button, out var action))
        {
            return;
        }

        await ExecuteOverviewActionAsync(viewModel, action);
    }

    private async void DisruptiveOverviewActionButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MecmAgentViewModel viewModel ||
            sender is not Button button ||
            !TryGetOverviewAction(button, out var action))
        {
            return;
        }

        var (title, message) = GetConfirmationText(action);
        var confirmation = MessageBox.Show(
            message,
            title,
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);

        if (confirmation != MessageBoxResult.OK)
        {
            return;
        }

        await ExecuteOverviewActionAsync(viewModel, action);
    }

    private static T? FindVisualParent<T>(DependencyObject? dependencyObject)
        where T : DependencyObject
    {
        while (dependencyObject is not null)
        {
            if (dependencyObject is T typedObject)
            {
                return typedObject;
            }

            dependencyObject = VisualTreeHelper.GetParent(dependencyObject);
        }

        return null;
    }

    private static bool TryGetOverviewAction(Button button, out MecmOverviewAction action)
    {
        action = default;
        return button.CommandParameter is string rawAction &&
               Enum.TryParse(rawAction, ignoreCase: true, out action);
    }

    private static (string Title, string Message) GetConfirmationText(MecmOverviewAction action)
    {
        return action switch
        {
            MecmOverviewAction.RestartCcmExec => (
                "Restart SMS Agent Host",
                "Restarting SMS Agent Host interrupts active MECM work on the target device.\n\nContinue?"),
            MecmOverviewAction.ResetPolicySoft => (
                "Reset MECM Policy",
                "A MECM policy reset clears local policy state and requests fresh machine policy afterwards.\n\nContinue?"),
            MecmOverviewAction.ResetPolicyHard => (
                "Hard Reset MECM Policy",
                "A hard MECM policy reset is disruptive and rebuilds local policy state before requesting fresh machine policy.\n\nContinue?"),
            MecmOverviewAction.RepairClient => (
                "Repair MECM Client",
                "This starts an MSI repair of the MECM client on the target device. The client can be unstable while repair is running.\n\nContinue?"),
            _ => ("Confirm MECM Action", "Continue?")
        };
    }

    private static Task ExecuteOverviewActionAsync(MecmAgentViewModel viewModel, MecmOverviewAction action)
    {
        return action switch
        {
            MecmOverviewAction.RequestMachinePolicy => viewModel.RequestMachinePolicyAsync(),
            MecmOverviewAction.EvaluateMachinePolicy => viewModel.EvaluateMachinePolicyAsync(),
            MecmOverviewAction.TriggerHeartbeatDiscovery => viewModel.TriggerHeartbeatDiscoveryAsync(),
            MecmOverviewAction.TriggerHardwareInventory => viewModel.TriggerHardwareInventoryAsync(),
            MecmOverviewAction.TriggerSoftwareInventory => viewModel.TriggerSoftwareInventoryAsync(),
            MecmOverviewAction.RunCcmeval => viewModel.RunCcmevalAsync(),
            MecmOverviewAction.RestartCcmExec => viewModel.RestartCcmExecAsync(),
            MecmOverviewAction.ResetPolicySoft => viewModel.ResetPolicySoftAsync(),
            MecmOverviewAction.ResetPolicyHard => viewModel.ResetPolicyHardAsync(),
            MecmOverviewAction.RepairClient => viewModel.RepairClientAsync(),
            _ => Task.CompletedTask
        };
    }
}
