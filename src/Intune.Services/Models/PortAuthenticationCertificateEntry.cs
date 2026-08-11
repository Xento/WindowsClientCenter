namespace WindowsClientCenter.Intune.Services.Models;

public sealed record PortAuthenticationCertificateEntry(
    string Subject,
    string SanDns,
    string Thumbprint,
    string Issuer,
    string StoreName,
    string HasPrivateKeyText,
    string ValidityText,
    string ChainStatusText,
    string FqdnMatchText,
    string StatusLevel);
