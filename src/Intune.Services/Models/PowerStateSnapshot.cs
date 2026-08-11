namespace WindowsClientCenter.Intune.Services.Models;

public sealed record PowerStateSnapshot(
    string Host,
    bool IsLocalHost,
    string? ActiveSchemeId,
    string? ActiveSchemeName,
    IReadOnlyList<PowerSchemeSnapshot> Schemes,
    IReadOnlyList<string> Warnings);

public sealed record PowerSchemeSnapshot(
    string SchemeId,
    string Name,
    bool IsActive);
