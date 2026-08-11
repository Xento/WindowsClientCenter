namespace WindowsClientCenter.Intune.Services.Models;

public sealed record AuthSession(
    string TenantId,
    string UserPrincipalName,
    DateTimeOffset ExpiresAt,
    bool IsMock);
