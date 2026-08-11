using WindowsClientCenter.Plugin.Abstractions.Contracts;
using WindowsClientCenter.Plugin.Abstractions.Models;
using WindowsClientCenter.Plugins.WindowsUpdateAgent.UI;
using WindowsClientCenter.Plugins.WindowsUpdateAgent.ViewModels;

namespace WindowsClientCenter.Plugins.WindowsUpdateAgent;

public sealed class WindowsUpdateAgentPlugin : IViewPlugin, INavigationMenuPlugin, INavigationAwareViewPlugin
{
    private IPluginContext? _context;
    private string? _navigationTarget;
    private WindowsUpdateAgentViewModel? _viewModel;
    private WindowsUpdateAgentView? _view;

    public IPluginManifest Manifest { get; } = new PluginManifest(
        Id: "windows-update-agent",
        DisplayName: "Windows Update Agent",
        Version: "1.0.0",
        Capability: PluginCapability.View,
        MenuPath: "Windows Update Agent",
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

        _viewModel ??= new WindowsUpdateAgentViewModel(_context, _navigationTarget);
        _viewModel.ApplyNavigationTarget(_navigationTarget);

        _view ??= new WindowsUpdateAgentView
        {
            DataContext = _viewModel
        };

        _ = _viewModel.StartMonitoringAsync(CancellationToken.None);
        return _view;
    }

    public IReadOnlyList<PluginNavigationEntry> GetNavigationEntries()
    {
        return
        [
            new PluginNavigationEntry(
                MenuPath: "Windows Update Agent/Overview",
                NavigationTarget: "overview",
                IconGlyph: "\uE80F",
                IsExpanded: true),
            new PluginNavigationEntry(
                MenuPath: "Windows Update Agent/Available updates",
                NavigationTarget: "available-updates",
                IconGlyph: "\uE823",
                IsExpanded: true),
            new PluginNavigationEntry(
                MenuPath: "Windows Update Agent/Update history",
                NavigationTarget: "update-history",
                IconGlyph: "\uE81C",
                IsExpanded: true),
            new PluginNavigationEntry(
                MenuPath: "Windows Update Agent/ReportingEvents.log",
                NavigationTarget: "reporting-events-log",
                IconGlyph: "\uE9D9",
                IsExpanded: true),
            new PluginNavigationEntry(
                MenuPath: "Windows Update Agent/USO diagnostics",
                NavigationTarget: "uso-diagnostics",
                IconGlyph: "\uE9D2",
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
