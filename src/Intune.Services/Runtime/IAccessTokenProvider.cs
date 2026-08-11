namespace WindowsClientCenter.Intune.Services.Runtime;

internal interface IAccessTokenProvider
{
    ValueTask<string> GetAccessTokenAsync(CancellationToken cancellationToken);
}
