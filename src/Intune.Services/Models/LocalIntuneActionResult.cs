namespace WindowsClientCenter.Intune.Services.Models;

public sealed record LocalIntuneActionResult(
    bool Success,
    string Message,
    IReadOnlyList<string> Warnings,
    IReadOnlyDictionary<string, string> Evidence);
