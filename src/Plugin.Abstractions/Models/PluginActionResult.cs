namespace WindowsClientCenter.Plugin.Abstractions.Models;

public sealed record PluginActionResult(
    bool Success,
    string Message,
    string? ErrorCode = null)
{
    public static PluginActionResult Ok(string message) => new(true, message);

    public static PluginActionResult Fail(string message, string? errorCode = null) => new(false, message, errorCode);
}
