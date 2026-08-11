using WindowsClientCenter.Plugin.Abstractions.Contracts;
using WindowsClientCenter.Plugin.Abstractions.Models;
using WindowsClientCenter.Plugins.MecmAgent.UI;
using WindowsClientCenter.Plugins.MecmAgent.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace WindowsClientCenter.Plugins.MecmAgent;

public sealed class MecmAgentPlugin : IViewPlugin, INavigationMenuPlugin, INavigationAwareViewPlugin
{
    private IPluginContext? _context;
    private string? _navigationTarget;
    private MecmAgentViewModel? _viewModel;
    private MecmAgentView? _view;
    private ILogger<MecmAgentPlugin>? _logger;
    private IHostStatusLogSink? _hostStatusLogSink;

    public IPluginManifest Manifest { get; } = new PluginManifest(
        Id: "mecm-agent",
        DisplayName: "MECM",
        Version: "1.0.0",
        Capability: PluginCapability.View,
        MenuPath: "MECM",
        MinHostVersion: "1.0.0");

    public ValueTask InitializeAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        _context = context;
        _logger = context.Services.GetService<ILogger<MecmAgentPlugin>>();
        _hostStatusLogSink = context.Services.GetService<IHostStatusLogSink>();
        return ValueTask.CompletedTask;
    }

    public object CreateView()
    {
        if (_context is null)
        {
            throw new InvalidOperationException("Plugin has not been initialized.");
        }

        _viewModel ??= new MecmAgentViewModel(_context, _navigationTarget);
        _viewModel.ApplyNavigationTarget(_navigationTarget);

        _view ??= new MecmAgentView
        {
            DataContext = _viewModel
        };

        StartBackgroundOperation(_viewModel.InitializeAsync(CancellationToken.None), "initializing MECM view");
        return _view;
    }

    public IReadOnlyList<PluginNavigationEntry> GetNavigationEntries()
    {
        return
        [
            new PluginNavigationEntry("MECM/Overview", "overview", "\uE946", true),
            new PluginNavigationEntry("MECM/Applications", "applications", "\uE7B8", true),
            new PluginNavigationEntry("MECM/Updates/Pending", "updates-pending", "\uE823", true),
            new PluginNavigationEntry("MECM/Updates/All", "updates-all", "\uE81C", true),
            new PluginNavigationEntry("MECM/Packages", "packages", "\uE8B7", true),
            new PluginNavigationEntry("MECM/DCM Baselines", "dcm-baselines", "\uE9D2", true)
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

    private async void StartBackgroundOperation(Task task, string operationName)
    {
        try
        {
            await task;
        }
        catch (Exception ex)
        {
            _hostStatusLogSink?.Append($"[MECM] Background operation failed while {operationName}: {ex.Message}");
            _logger?.LogError(ex, "MECM plugin background operation failed while {OperationName}.", operationName);
        }
    }
}
