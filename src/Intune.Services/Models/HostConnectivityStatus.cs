namespace WindowsClientCenter.Intune.Services.Models;

public sealed record HostConnectivityStatus(
    bool PingSucceeded,
    long? PingRoundtripTimeMs,
    string PingDetail,
    string? ResolvedIp,
    bool SmbReachable,
    bool WinRmHttpReachable,
    bool WinRmHttpsReachable)
{
    public bool IsWinRmReachable => WinRmHttpReachable || WinRmHttpsReachable;
}
