namespace WindowsClientCenter.Intune.Services.Models;

public sealed record CloudSyncResult(
    bool Success,
    string Message,
    string? TrackingId = null,
    string? ErrorCode = null)
{
    public static CloudSyncResult Ok(string message, string? trackingId = null) =>
        new(true, message, trackingId);

    public static CloudSyncResult Fail(string message, string? errorCode = null) =>
        new(false, message, null, errorCode);
}
