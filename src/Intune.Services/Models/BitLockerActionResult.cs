namespace WindowsClientCenter.Intune.Services.Models;

public sealed record BitLockerActionResult(
    bool Success,
    bool Warning,
    string Message,
    string? ErrorCode = null,
    string? NewProtectorId = null,
    IReadOnlyList<string>? Details = null)
{
    public static BitLockerActionResult Ok(string message, string? newProtectorId = null, IReadOnlyList<string>? details = null) =>
        new(true, false, message, null, newProtectorId, details);

    public static BitLockerActionResult Warn(string message, string? errorCode = null, string? newProtectorId = null, IReadOnlyList<string>? details = null) =>
        new(false, true, message, errorCode, newProtectorId, details);

    public static BitLockerActionResult Fail(string message, string? errorCode = null, IReadOnlyList<string>? details = null) =>
        new(false, false, message, errorCode, null, details);
}
