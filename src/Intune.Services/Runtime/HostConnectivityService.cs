using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using WindowsClientCenter.Intune.Services.Contracts;
using WindowsClientCenter.Intune.Services.Models;

namespace WindowsClientCenter.Intune.Services.Runtime;

public sealed class HostConnectivityService : IHostConnectivityService
{
    private const int HostPingTimeoutMs = 1000;
    private const int HostConnectionTimeoutMs = 1500;

    public async ValueTask<HostConnectivityStatus> TestConnectivityAsync(string host, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(host))
        {
            return new HostConnectivityStatus(
                PingSucceeded: false,
                PingRoundtripTimeMs: null,
                PingDetail: "no host provided",
                ResolvedIp: null,
                SmbReachable: false,
                WinRmHttpReachable: false,
                WinRmHttpsReachable: false);
        }

        var normalizedHost = host.Trim();
        if (IsLocalHost(normalizedHost))
        {
            return new HostConnectivityStatus(
                PingSucceeded: true,
                PingRoundtripTimeMs: 0,
                PingDetail: "local machine",
                ResolvedIp: IPAddress.Loopback.ToString(),
                SmbReachable: true,
                WinRmHttpReachable: true,
                WinRmHttpsReachable: false);
        }

        var pingResult = await SendPingAsync(normalizedHost);

        if (!pingResult.PingSucceeded)
        {
            return new HostConnectivityStatus(
                PingSucceeded: false,
                PingRoundtripTimeMs: pingResult.PingRoundtripTimeMs,
                PingDetail: pingResult.PingDetail,
                ResolvedIp: pingResult.ResolvedIp,
                SmbReachable: false,
                WinRmHttpReachable: false,
                WinRmHttpsReachable: false);
        }

        var smbTask = TryConnectTcpAsync(normalizedHost, 445, cancellationToken);
        var winRmHttpTask = TryConnectTcpAsync(normalizedHost, 5985, cancellationToken);
        var winRmHttpsTask = TryConnectTcpAsync(normalizedHost, 5986, cancellationToken);

        await Task.WhenAll(smbTask, winRmHttpTask, winRmHttpsTask);

        return new HostConnectivityStatus(
            PingSucceeded: pingResult.PingSucceeded,
            PingRoundtripTimeMs: pingResult.PingRoundtripTimeMs,
            PingDetail: pingResult.PingDetail,
            ResolvedIp: pingResult.ResolvedIp,
            SmbReachable: await smbTask,
            WinRmHttpReachable: await winRmHttpTask,
            WinRmHttpsReachable: await winRmHttpsTask);
    }

    public static bool IsLocalHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        var normalizedHost = host.Trim();
        if (normalizedHost.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            normalizedHost.Equals(".", StringComparison.OrdinalIgnoreCase) ||
            normalizedHost.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!IPAddress.TryParse(normalizedHost, out var address))
        {
            return false;
        }

        return IPAddress.IsLoopback(address);
    }

    private static async Task<PingConnectivityResult> SendPingAsync(string host)
    {
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(host, HostPingTimeoutMs);
            var resolvedIp = reply.Address is null || IPAddress.None.Equals(reply.Address)
                ? await TryResolveHostIpAsync(host)
                : reply.Address.ToString();
            return new PingConnectivityResult(
                PingSucceeded: reply.Status == IPStatus.Success,
                PingRoundtripTimeMs: reply.Status == IPStatus.Success ? reply.RoundtripTime : null,
                PingDetail: reply.Status == IPStatus.Success ? "ok" : reply.Status.ToString(),
                ResolvedIp: resolvedIp);
        }
        catch (PingException ex)
        {
            return new PingConnectivityResult(false, null, ex.InnerException?.Message ?? ex.Message, await TryResolveHostIpAsync(host));
        }
        catch (SocketException ex)
        {
            return new PingConnectivityResult(false, null, ex.Message, await TryResolveHostIpAsync(host));
        }
    }

    private static async Task<string?> TryResolveHostIpAsync(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return null;
        }

        if (IPAddress.TryParse(host, out var directIp))
        {
            return directIp.ToString();
        }

        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host);
            if (addresses.Length == 0)
            {
                return null;
            }

            var ipv4 = addresses.FirstOrDefault(static address => address.AddressFamily == AddressFamily.InterNetwork);
            return (ipv4 ?? addresses[0]).ToString();
        }
        catch
        {
            return null;
        }
    }

    private static async Task<bool> TryConnectTcpAsync(string host, int port, CancellationToken cancellationToken)
    {
        try
        {
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(HostConnectionTimeoutMs);

            using var tcpClient = new TcpClient();
            await tcpClient.ConnectAsync(host, port, timeoutSource.Token);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private sealed record PingConnectivityResult(
        bool PingSucceeded,
        long? PingRoundtripTimeMs,
        string PingDetail,
        string? ResolvedIp);
}
