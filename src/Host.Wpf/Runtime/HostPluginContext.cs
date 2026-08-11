using WindowsClientCenter.Plugin.Abstractions.Contracts;
using Microsoft.Extensions.Logging;

namespace WindowsClientCenter.Host.Runtime;

public sealed class HostPluginContext(
    ILogger logger,
    IServiceProvider services,
    string environmentName,
    IReadOnlyDictionary<string, string> settings) : IPluginContext
{
    public ILogger Logger { get; } = logger;

    public IServiceProvider Services { get; } = services;

    public string EnvironmentName { get; } = environmentName;

    public IReadOnlyDictionary<string, string> Settings { get; } = settings;
}
