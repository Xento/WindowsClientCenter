using WindowsClientCenter.Intune.Services.Contracts;
using WindowsClientCenter.Intune.Services.Models;
using Microsoft.Identity.Client;

namespace WindowsClientCenter.Intune.Services.Runtime;

internal sealed class LiveGraphAuthService : IAuthService, IAccessTokenProvider
{
    private static readonly string[] Scopes =
    [
        "openid",
        "profile",
        "offline_access",
        "https://graph.microsoft.com/DeviceManagementManagedDevices.Read.All",
        "https://graph.microsoft.com/DeviceManagementManagedDevices.PrivilegedOperations.All"
    ];

    private readonly IPublicClientApplication _publicClientApplication;
    private AuthenticationResult? _lastAuthenticationResult;

    public LiveGraphAuthService(IntuneRuntimeOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.ClientId) ||
            options.ClientId.Equals("00000000-0000-0000-0000-000000000000", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Intune:ClientId must be configured for live Microsoft Graph login.");
        }

        var builder = PublicClientApplicationBuilder
            .Create(options.ClientId)
            .WithAuthority(AzureCloudInstance.AzurePublic, options.TenantId);

        if (!string.IsNullOrWhiteSpace(options.RedirectUri))
        {
            builder = builder.WithRedirectUri(options.RedirectUri);
        }
        else
        {
            builder = builder.WithDefaultRedirectUri();
        }

        _publicClientApplication = builder.Build();
    }

    public async ValueTask<AuthSession> LoginAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _lastAuthenticationResult = await AcquireTokenInteractiveAsync(cancellationToken);
        return ToSession(_lastAuthenticationResult);
    }

    public ValueTask<AuthSession?> GetCurrentSessionAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_lastAuthenticationResult is null)
        {
            return ValueTask.FromResult<AuthSession?>(null);
        }

        if (_lastAuthenticationResult.ExpiresOn <= DateTimeOffset.UtcNow.AddMinutes(1))
        {
            _lastAuthenticationResult = null;
            return ValueTask.FromResult<AuthSession?>(null);
        }

        return ValueTask.FromResult<AuthSession?>(ToSession(_lastAuthenticationResult));
    }

    public async ValueTask<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_lastAuthenticationResult is not null && _lastAuthenticationResult.ExpiresOn > DateTimeOffset.UtcNow.AddMinutes(1))
        {
            return _lastAuthenticationResult.AccessToken;
        }

        try
        {
            var accounts = await _publicClientApplication.GetAccountsAsync();
            var firstAccount = accounts.FirstOrDefault();
            if (firstAccount is not null)
            {
                _lastAuthenticationResult = await _publicClientApplication
                    .AcquireTokenSilent(Scopes, firstAccount)
                    .ExecuteAsync(cancellationToken);

                return _lastAuthenticationResult.AccessToken;
            }
        }
        catch (MsalUiRequiredException)
        {
            // Fall back to interactive sign-in.
        }

        _lastAuthenticationResult = await AcquireTokenInteractiveAsync(cancellationToken);
        return _lastAuthenticationResult.AccessToken;
    }

    private async Task<AuthenticationResult> AcquireTokenInteractiveAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _publicClientApplication
                .AcquireTokenInteractive(Scopes)
                .WithUseEmbeddedWebView(false)
                .ExecuteAsync(cancellationToken);
        }
        catch (MsalClientException ex) when (ex.ErrorCode.Equals("authentication_canceled", StringComparison.OrdinalIgnoreCase))
        {
            throw new OperationCanceledException("Interactive login was canceled.", ex, cancellationToken);
        }
    }

    private static AuthSession ToSession(AuthenticationResult result)
    {
        return new AuthSession(
            result.TenantId,
            result.Account?.Username ?? "unknown",
            result.ExpiresOn,
            IsMock: false);
    }
}
