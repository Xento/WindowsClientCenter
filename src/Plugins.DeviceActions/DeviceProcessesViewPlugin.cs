using WindowsClientCenter.Plugin.Abstractions.Contracts;
using WindowsClientCenter.Plugin.Abstractions.Models;
using WindowsClientCenter.Plugins.DeviceActions.UI;
using WindowsClientCenter.Plugins.DeviceActions.ViewModels;

namespace WindowsClientCenter.Plugins.DeviceActions;

public sealed class DeviceProcessesViewPlugin : IViewPlugin, INavigationAwareViewPlugin
{
    private IPluginContext? _context;
    private DeviceProcessesViewModel? _viewModel;
    private DeviceProcessesView? _view;

    public IPluginManifest Manifest { get; } = new PluginManifest(
        Id: "device-processes-view",
        DisplayName: "Device Processes",
        Version: "1.0.0",
        Capability: PluginCapability.View,
        MenuPath: "Device/Processes",
        MinHostVersion: "1.0.0");

    public ValueTask InitializeAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        _context = context;
        return ValueTask.CompletedTask;
    }

    public object CreateView()
    {
        if (_context is null)
        {
            throw new InvalidOperationException("Plugin has not been initialized.");
        }

        _viewModel ??= new DeviceProcessesViewModel(_context);
        _view ??= new DeviceProcessesView
        {
            DataContext = _viewModel
        };

        _ = _viewModel.LoadAsync(CancellationToken.None);
        return _view;
    }

    public void SetNavigationTarget(string? navigationTarget)
    {
        _ = navigationTarget;
    }

    public ValueTask DisposeAsync()
    {
        _viewModel?.Dispose();
        _viewModel = null;
        _view = null;
        return ValueTask.CompletedTask;
    }
}
