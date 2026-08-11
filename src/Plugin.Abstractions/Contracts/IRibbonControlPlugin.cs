using WindowsClientCenter.Plugin.Abstractions.Models;

namespace WindowsClientCenter.Plugin.Abstractions.Contracts;

public interface IRibbonControlPlugin
{
    IReadOnlyList<PluginRibbonGroup> GetRibbonGroups();

    ValueTask<PluginActionResult> ExecuteRibbonControlAsync(string controlId, PluginActionContext context, CancellationToken cancellationToken);
}
