using WindowsClientCenter.Plugin.Abstractions.Contracts;
using WindowsClientCenter.Plugin.Abstractions.Models;
using WindowsClientCenter.Plugins.DeviceOverview.UI;
using WindowsClientCenter.Plugins.DeviceOverview.ViewModels;

namespace WindowsClientCenter.Plugins.DeviceOverview;

public sealed class DeviceOverviewPlugin : IViewPlugin, INavigationMenuPlugin, INavigationAwareViewPlugin
{
    private IPluginContext? _context;
    private string? _navigationTarget;
    private DeviceOverviewViewModel? _viewModel;
    private DeviceOverviewView? _view;

    public IPluginManifest Manifest { get; } = new PluginManifest(
        Id: "device-overview",
        DisplayName: "Device Overview",
        Version: "1.0.0",
        Capability: PluginCapability.View,
        MenuPath: "Device",
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

        _viewModel ??= new DeviceOverviewViewModel(_context, _navigationTarget);
        _viewModel.ApplyNavigationTarget(_navigationTarget);

        _view ??= new DeviceOverviewView
        {
            DataContext = _viewModel
        };

        _ = _viewModel.LoadAsync(CancellationToken.None);
        return _view;
    }

    public IReadOnlyList<PluginNavigationEntry> GetNavigationEntries()
    {
        return
        [
            new PluginNavigationEntry(
                MenuPath: "Device/Overview",
                NavigationTarget: "overview",
                IconGlyph: "\uE80F",
                IsExpanded: true),
            new PluginNavigationEntry(
                MenuPath: "Device/Delivery Optimization",
                NavigationTarget: "delivery-optimization",
                IconGlyph: "\uE9D2",
                IsExpanded: true),
            new PluginNavigationEntry(
                MenuPath: "Device/Port Authentication",
                NavigationTarget: "port-authentication",
                IconGlyph: "\uE968",
                IsExpanded: true)
        ];
    }

    public void SetNavigationTarget(string? navigationTarget)
    {
        _navigationTarget = navigationTarget;
        _viewModel?.ApplyNavigationTarget(navigationTarget);
    }

    public ValueTask DisposeAsync()
    {
        _viewModel?.Dispose();
        _viewModel = null;
        _view = null;
        return ValueTask.CompletedTask;
    }
}
