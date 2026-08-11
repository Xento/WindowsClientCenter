using Microsoft.Extensions.Logging;

namespace WindowsClientCenter.Plugin.Abstractions.Contracts;

public interface IPluginContext
{
    ILogger Logger { get; }
    IServiceProvider Services { get; }
    string EnvironmentName { get; }
    IReadOnlyDictionary<string, string> Settings { get; }
}
