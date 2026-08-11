using WindowsClientCenter.Intune.Services.Models;

namespace WindowsClientCenter.Intune.Services.Contracts;

public interface IHostConnectivityService
{
    ValueTask<HostConnectivityStatus> TestConnectivityAsync(string host, CancellationToken cancellationToken);
}
