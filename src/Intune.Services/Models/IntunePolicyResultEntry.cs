namespace WindowsClientCenter.Intune.Services.Models;

public sealed record IntunePolicyResultEntry(
    string Scope,
    string Area,
    string SettingName,
    string OmaUri,
    string CurrentValue,
    string Status,
    string ResultCode,
    string Source = "Mdm",
    string WinningSource = "",
    bool IsDuplicate = false,
    string DuplicateSources = "",
    string MdmPath = "",
    string GpoPath = "",
    string GpoCategoryPath = "",
    string AdditionalDetails = "");
