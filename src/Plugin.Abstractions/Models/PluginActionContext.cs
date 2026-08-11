namespace WindowsClientCenter.Plugin.Abstractions.Models;

public sealed record PluginActionContext(
    string? DeviceId,
    string? ActionName,
    IReadOnlyDictionary<string, string>? Arguments = null);
