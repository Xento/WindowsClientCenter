using WindowsClientCenter.Intune.Services.Models;

namespace WindowsClientCenter.Intune.Services.Contracts;

public interface IAuthService
{
    ValueTask<AuthSession> LoginAsync(CancellationToken cancellationToken);
    ValueTask<AuthSession?> GetCurrentSessionAsync(CancellationToken cancellationToken);
}
