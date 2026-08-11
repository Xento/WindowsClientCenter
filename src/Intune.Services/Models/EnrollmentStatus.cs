namespace WindowsClientCenter.Intune.Services.Models;

public sealed record EnrollmentStatus(
    string Host,
    bool IsLocalHost,
    bool WinRmAvailable,
    bool IsAdminContext,
    bool EnrollmentDetected,
    string LastSyncText,
    string RegistrationSummary,
    IReadOnlyList<string> EnrollmentIds,
    IReadOnlyList<string> Checks,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<EnrollmentArtifact> Artifacts,
    EnrollmentUrlsStatus EnrollmentUrls,
    bool CanTriggerSync,
    bool CanReenroll);
