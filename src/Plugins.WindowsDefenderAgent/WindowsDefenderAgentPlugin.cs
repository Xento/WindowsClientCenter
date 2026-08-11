using WindowsClientCenter.Plugin.Abstractions.Contracts;
using WindowsClientCenter.Plugin.Abstractions.Models;
using WindowsClientCenter.Plugins.WindowsDefenderAgent.UI;
using WindowsClientCenter.Plugins.WindowsDefenderAgent.ViewModels;

namespace WindowsClientCenter.Plugins.WindowsDefenderAgent;

public sealed class WindowsDefenderAgentPlugin : IViewPlugin, INavigationMenuPlugin, INavigationAwareViewPlugin
{
    private IPluginContext? _context;
    private string? _navigationTarget;
    private WindowsDefenderAgentViewModel? _viewModel;
    private WindowsDefenderAgentView? _view;

    public IPluginManifest Manifest { get; } = new PluginManifest(
        Id: "windows-defender-agent",
        DisplayName: "Windows Defender",
        Version: "1.0.0",
        Capability: PluginCapability.View,
        MenuPath: "Defender",
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

        _viewModel ??= new WindowsDefenderAgentViewModel(_context, _navigationTarget);
        _viewModel.ApplyNavigationTarget(_navigationTarget);

        _view ??= new WindowsDefenderAgentView
        {
            DataContext = _viewModel
        };

        _ = _viewModel.InitializeAsync(CancellationToken.None);
        return _view;
    }

    public IReadOnlyList<PluginNavigationEntry> GetNavigationEntries()
    {
        return
        [
            new PluginNavigationEntry("Defender/Overview", "overview", "\uE8F1", true),
            new PluginNavigationEntry("Defender/Protection Status", "protection-status", "\uE73E", true),
            new PluginNavigationEntry("Defender/Versions", "versions", "\uE946", true),
            new PluginNavigationEntry("Defender/Scans", "scans", "\uE721", true),
            new PluginNavigationEntry("Defender/Settings", "settings", "\uE713", true),
            new PluginNavigationEntry("Defender/Detections", "detections", "\uE814", true),
            new PluginNavigationEntry("Defender/Device Control", "device-control", "\uE772", true)
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
