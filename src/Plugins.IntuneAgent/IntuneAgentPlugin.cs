using WindowsClientCenter.Intune.Services.Contracts;
using WindowsClientCenter.Intune.Services.Models;
using WindowsClientCenter.Plugin.Abstractions.Contracts;
using WindowsClientCenter.Plugin.Abstractions.Models;
using WindowsClientCenter.Plugins.IntuneAgent.UI;
using WindowsClientCenter.Plugins.IntuneAgent.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace WindowsClientCenter.Plugins.IntuneAgent;

public sealed class IntuneAgentPlugin : IViewPlugin, INavigationMenuPlugin, INavigationAwareViewPlugin, IRibbonControlPlugin
{
    private IPluginContext? _context;
    private string? _navigationTarget;
    private IntuneAgentViewModel? _viewModel;
    private IntuneAgentView? _view;

    public IPluginManifest Manifest { get; } = new PluginManifest(
        Id: "intune-agent",
        DisplayName: "Intune Agent",
        Version: "1.0.0",
        Capability: PluginCapability.View,
        MenuPath: "Intune Agent",
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

        _viewModel ??= new IntuneAgentViewModel(_context, _navigationTarget);
        _viewModel.ApplyNavigationTarget(_navigationTarget);

        _view ??= new IntuneAgentView
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
            new PluginNavigationEntry("Intune Agent/Overview", "overview", "\uE80F", true),
            new PluginNavigationEntry("Intune Agent/Local Diagnostics", "local-diagnostics", "\uE9D9", true),
            new PluginNavigationEntry("Intune Agent/Enrollment", "enrollment", "\uE8B7", true),
            new PluginNavigationEntry("Intune Agent/MDM Events", "mdm-events", "\uE7BA", true),
            new PluginNavigationEntry("Intune Agent/IME Logs", "ime-logs", "\uE9D9", true),
            new PluginNavigationEntry("Intune Agent/IME Applications", "ime-applications", "\uE8FD", true),
            new PluginNavigationEntry("Intune Agent/Local Actions", "local-actions", "\uE945", true),
            new PluginNavigationEntry("Intune Agent/Policy Result", "policy-result", "\uE8A5", true),
            new PluginNavigationEntry("Intune Agent/Cloud", "cloud", "\uE753", true)
        ];
    }

    public IReadOnlyList<PluginRibbonGroup> GetRibbonGroups()
    {
        return
        [
            new PluginRibbonGroup(
                GroupId: "intune",
                Title: "Intune",
                Controls:
                [
                    new PluginRibbonControl("mdm-sync", PluginRibbonControlKind.Button, Text: "MDM Sync", Width: 82),
                    new PluginRibbonControl("ime-sync", PluginRibbonControlKind.Button, Text: "IME Sync", Width: 82)
                ],
                DefaultControlHeight: 26,
                DefaultControlFontSize: 12,
                DefaultControlHorizontalPadding: 5,
                DefaultControlVerticalPadding: 1)
        ];
    }

    public async ValueTask<PluginActionResult> ExecuteRibbonControlAsync(string controlId, PluginActionContext context, CancellationToken cancellationToken)
    {
        if (_context is null)
        {
            return PluginActionResult.Fail("Plugin has not been initialized.", "not_initialized");
        }

        var targetHostService = _context.Services.GetRequiredService<ITargetHostService>();
        var localIntuneActionService = _context.Services.GetRequiredService<ILocalIntuneActionService>();

        var host = ResolveHost(context, targetHostService);
        if (host is null)
        {
            return PluginActionResult.Fail("Client is not connected. Click Connect first.", "no_host");
        }

        return controlId switch
        {
            "mdm-sync" => await ToPluginActionResultAsync(localIntuneActionService.MdmSyncNowAsync(host, cancellationToken)),
            "ime-sync" => await ToPluginActionResultAsync(localIntuneActionService.ImeSyncAppsAsync(host, cancellationToken)),
            _ => PluginActionResult.Fail($"Unknown ribbon control '{controlId}'.", "unknown_control")
        };
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

    private static string? ResolveHost(PluginActionContext context, ITargetHostService targetHostService)
    {
        if (context.Arguments is not null &&
            context.Arguments.TryGetValue("host", out var hostFromArgs) &&
            !string.IsNullOrWhiteSpace(hostFromArgs))
        {
            return hostFromArgs.Trim();
        }

        return string.IsNullOrWhiteSpace(targetHostService.CurrentHost)
            ? null
            : targetHostService.CurrentHost.Trim();
    }

    private static async ValueTask<PluginActionResult> ToPluginActionResultAsync(ValueTask<LocalIntuneActionResult> actionTask)
    {
        var result = await actionTask;
        return result.Success
            ? PluginActionResult.Ok(result.Message)
            : PluginActionResult.Fail(result.Message);
    }
}
