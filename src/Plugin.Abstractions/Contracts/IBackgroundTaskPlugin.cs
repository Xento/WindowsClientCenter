using WindowsClientCenter.Plugin.Abstractions.Models;

namespace WindowsClientCenter.Plugin.Abstractions.Contracts;

public interface IBackgroundTaskPlugin : IClientCenterPlugin
{
    ValueTask<PluginActionResult> RunAsync(CancellationToken cancellationToken);
}
