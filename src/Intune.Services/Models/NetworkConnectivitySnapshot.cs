namespace WindowsClientCenter.Intune.Services.Models;

public sealed record NetworkConnectivitySnapshot(
    string PrimaryConnectionText,
    string PrimaryAdapterText,
    string WiFiSsidText,
    string VpnStatusText,
    string VpnProviderText,
    bool IsCheckpointVpnDetected,
    string PortAuthenticationStatusText = "Unknown",
    string PortAuthenticationDetailText = "Port authentication status is not available.");
