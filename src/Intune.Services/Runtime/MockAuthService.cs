using WindowsClientCenter.Intune.Services.Contracts;
using WindowsClientCenter.Intune.Services.Models;

namespace WindowsClientCenter.Intune.Services.Runtime;

internal sealed class MockAuthService(IntuneRuntimeOptions options) : IAuthService, IAccessTokenProvider
{
    private AuthSession? _session;
    private string? _accessToken;

    public ValueTask<AuthSession> LoginAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        _session ??= new AuthSession(
            string.IsNullOrWhiteSpace(options.TenantId) ? "demo.example" : options.TenantId,
            "admin@example.invalid",
            DateTimeOffset.UtcNow.AddHours(8),
            IsMock: true);

        return ValueTask.FromResult(_session);
    }

    public ValueTask<AuthSession?> GetCurrentSessionAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_session);
    }

    public ValueTask<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = LoginAsync(cancellationToken);
        _accessToken ??= $"mock-token-{Guid.NewGuid():N}";
        return ValueTask.FromResult(_accessToken);
    }
}
