namespace WindowsClientCenter.Intune.Services.Models;

public sealed record AppxPackageSnapshot(
    string Host,
    string ActiveUserName,
    string ActiveUserSid,
    IReadOnlyList<AppxPackageEntry> Packages,
    IReadOnlyList<string> Warnings);

public sealed record AppxPackageEntry(
    string PackageFullName,
    string PackageFamilyName,
    string Name,
    string DisplayName,
    string Version,
    string Publisher,
    string Architecture,
    string InstallLocation,
    bool IsFramework,
    bool IsResourcePackage,
    bool IsBundle,
    bool IsOptional,
    bool NonRemovable,
    bool IsProvisioned,
    string ProvisionedPackageName,
    IReadOnlyList<AppxUserRegistration> Users)
{
    public string EffectiveDisplayName => string.IsNullOrWhiteSpace(DisplayName) ? Name : DisplayName;
    public string PackageType => IsBundle ? "Bundle" : IsFramework ? "Framework" : IsResourcePackage ? "Resource" : IsOptional ? "Optional" : "Main";
    public string ProvisionedDisplay => IsProvisioned ? "Yes" : "No";
    public string RemovableDisplay => NonRemovable ? "No" : "Yes";
}

public sealed record AppxUserRegistration(
    string UserSid,
    string UserName,
    string InstallState,
    bool IsActiveUser)
{
    public string ActiveUserDisplay => IsActiveUser ? "Yes" : string.Empty;
}

public sealed record WingetSearchSnapshot(
    IReadOnlyList<WingetCatalogEntry> Entries,
    IReadOnlyList<string> Warnings);

public sealed record WingetCatalogEntry(
    string Id,
    string Name,
    string Version,
    string Source);

public enum WingetInstallScope
{
    Machine,
    ActiveUser
}
