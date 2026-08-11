namespace WindowsClientCenter.Intune.Services.Models;

public sealed record PortAuthenticationProfileEntry(
    string Name,
    string InterfaceName,
    string AuthMode,
    string SsoMode,
    string OneXEnabledText,
    string OneXEnforcedText,
    string EapType,
    string ParseStatusText,
    string StatusLevel);
