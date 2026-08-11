namespace WindowsClientCenter.Intune.Services.Models;

public sealed record IntunePolicyResultSummary(
    int TotalCount,
    int AppliedCount,
    int FailedCount,
    int UnknownCount,
    int DeviceCount,
    int UserCount,
    int UnknownScopeCount,
    int DuplicateCount = 0,
    int ConflictCount = 0);
