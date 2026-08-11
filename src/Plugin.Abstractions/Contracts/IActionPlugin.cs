using WindowsClientCenter.Plugin.Abstractions.Models;

namespace WindowsClientCenter.Plugin.Abstractions.Contracts;

public interface IActionPlugin : IClientCenterPlugin
{
    ValueTask<PluginActionResult> ExecuteAsync(PluginActionContext context, CancellationToken cancellationToken);
}
