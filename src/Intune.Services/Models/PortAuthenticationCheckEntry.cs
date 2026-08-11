namespace WindowsClientCenter.Intune.Services.Models;

public sealed record PortAuthenticationCheckEntry(
    string Name,
    string StatusText,
    string StatusLevel,
    string Detail);
