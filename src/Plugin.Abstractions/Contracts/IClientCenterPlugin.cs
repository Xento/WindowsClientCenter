namespace WindowsClientCenter.Plugin.Abstractions.Contracts;

public interface IClientCenterPlugin : IAsyncDisposable
{
    IPluginManifest Manifest { get; }
    ValueTask InitializeAsync(IPluginContext context, CancellationToken cancellationToken);
}
