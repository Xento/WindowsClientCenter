namespace WindowsClientCenter.Intune.Services.Models;

public sealed record InstalledSoftwareSnapshot(
    string Host,
    bool IsLocalHost,
    IReadOnlyList<InstalledSoftwareEntry> Entries,
    IReadOnlyList<string> Warnings);

public sealed record InstalledSoftwareEntry(
    string Id,
    string Name,
    string Version,
    string Publisher,
    string InstallDate,
    string InstallLocation,
    string InstallSource,
    string SoftwareCode,
    string ProductCode,
    string UninstallString,
    string QuietUninstallString,
    string Source,
    string Architecture)
{
    public bool IsMsi => InstalledSoftwareEntryHelpers.IsMsiProductCode(EffectiveProductCode);
    public bool CanRepairMsi => IsMsi;
    public bool CanUninstallMsi => IsMsi;
    public bool CanQuietUninstall => !string.IsNullOrWhiteSpace(QuietUninstallString);
    public bool CanForceRemoveRegistryEntry => InstalledSoftwareEntryHelpers.IsRegistryEntryId(Id) ||
                                               !string.IsNullOrWhiteSpace(SoftwareCode) ||
                                               !string.IsNullOrWhiteSpace(ProductCode);
    public string EffectiveProductCode => !string.IsNullOrWhiteSpace(ProductCode) ? ProductCode : SoftwareCode;
    public string MsiDisplay => IsMsi ? "Yes" : "No";
    public string QuietUninstallDisplay => CanQuietUninstall ? "Yes" : "No";
}

public enum InstalledSoftwareAction
{
    RepairMsi,
    UninstallMsi,
    QuietUninstall,
    ForceRemoveRegistryEntry
}

public static class InstalledSoftwareEntryHelpers
{
    public static bool IsRegistryEntryId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var parts = value.Split('|', 3);
        return parts.Length == 3 &&
               string.Equals(parts[0], "registry", StringComparison.OrdinalIgnoreCase) &&
               !string.IsNullOrWhiteSpace(parts[2]);
    }

    public static bool IsMsiProductCode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var text = value.Trim();
        return text.Length == 38 &&
               text[0] == '{' &&
               text[37] == '}' &&
               Guid.TryParse(text[1..^1], out _);
    }
}
