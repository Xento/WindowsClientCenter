namespace WindowsClientCenter.Intune.Services.Models;

public sealed record WindowsProfileEntry(
    string AccountName,
    string Sid,
    string LocalPath,
    DateTimeOffset? LastUseTimeUtc,
    string ServerProfilePath,
    bool IsLoaded,
    bool IsTemporary,
    bool IsRoaming,
    bool IsMandatory,
    bool IsCorrupted,
    bool IsSpecial);

public sealed record WindowsProfilePolicyInfo(
    int? MaxProfileSizeMb,
    bool? IncludesRegistryInQuota,
    IReadOnlyList<string> ExcludedRelativePaths,
    string Source)
{
    public bool IsConfigured => MaxProfileSizeMb.HasValue || IncludesRegistryInQuota.HasValue || ExcludedRelativePaths.Count > 0;
}

public sealed record WindowsProfileSnapshot(
    string Host,
    bool IsLocalHost,
    IReadOnlyList<WindowsProfileEntry> Profiles,
    WindowsProfilePolicyInfo Policy,
    IReadOnlyList<string> Warnings);

public enum ProfileSizeCalculationMode
{
    Raw,
    PolicyExcluded
}

public sealed record WindowsProfileSizeResult(
    string ProfileLocalPath,
    ProfileSizeCalculationMode Mode,
    long SizeBytes,
    int FileCount,
    int DirectoryCount,
    IReadOnlyList<string> Warnings);
