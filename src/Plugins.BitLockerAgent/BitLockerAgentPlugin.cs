using WindowsClientCenter.Plugin.Abstractions.Contracts;
using WindowsClientCenter.Plugin.Abstractions.Models;
using WindowsClientCenter.Plugins.BitLockerAgent.UI;
using WindowsClientCenter.Plugins.BitLockerAgent.ViewModels;

namespace WindowsClientCenter.Plugins.BitLockerAgent;

public sealed class BitLockerAgentPlugin : IViewPlugin, INavigationMenuPlugin, INavigationAwareViewPlugin
{
    private IPluginContext? _context;
    private string? _navigationTarget;
    private BitLockerAgentViewModel? _viewModel;
    private BitLockerAgentView? _view;

    public IPluginManifest Manifest { get; } = new PluginManifest(
        Id: "bitlocker-agent",
        DisplayName: "BitLocker",
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

        _viewModel ??= new BitLockerAgentViewModel(_context, _navigationTarget);
        _viewModel.ApplyNavigationTarget(_navigationTarget);

        _view ??= new BitLockerAgentView
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
                MenuPath: "Device/BitLocker",
                NavigationTarget: "bitlocker",
                IconGlyph: "\uE73E",
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
