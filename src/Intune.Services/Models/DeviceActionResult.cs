namespace WindowsClientCenter.Intune.Services.Models;

public sealed record DeviceActionResult(
    bool Success,
    string Message,
    string? TrackingId = null,
    string? ErrorCode = null)
{
    public static DeviceActionResult Ok(string message, string? trackingId = null) =>
        new(true, message, trackingId);

    public static DeviceActionResult Fail(string message, string? errorCode = null) =>
        new(false, message, null, errorCode);
}
