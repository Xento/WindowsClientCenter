namespace WindowsClientCenter.Intune.Services.Models;

public sealed record PortAuthenticationSnapshot(
    DateTimeOffset CapturedAtUtc,
    string OverallStatusText,
    string OverallStatusLevel,
    string OverallDetailText,
    string ApplicabilityText,
    string Fqdn,
    string ActiveInterfaceName,
    string ActiveInterfaceDescription,
    string AuthenticationStateText,
    string TracingModeText,
    string LastSuccessfulAuthenticationText,
    IReadOnlyList<PortAuthenticationCheckEntry> Checks,
    IReadOnlyList<PortAuthenticationProfileEntry> Profiles,
    IReadOnlyList<PortAuthenticationCertificateEntry> Certificates,
    IReadOnlyList<PortAuthenticationEventEntry> Events);
