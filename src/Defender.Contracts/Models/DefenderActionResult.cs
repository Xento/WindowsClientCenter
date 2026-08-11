namespace WindowsClientCenter.Defender.Contracts.Models;

public sealed record DefenderActionResult(
    bool Success,
    string Message,
    string ErrorCode,
    DateTimeOffset ExecutedAtUtc)
{
    public static DefenderActionResult Ok(string message)
    {
        return new DefenderActionResult(true, message, string.Empty, DateTimeOffset.UtcNow);
    }

    public static DefenderActionResult Fail(string message, string errorCode = "defender_action_failed")
    {
        return new DefenderActionResult(false, message, errorCode, DateTimeOffset.UtcNow);
    }
}
