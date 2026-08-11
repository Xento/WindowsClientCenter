namespace WindowsClientCenter.Intune.Services.Models;

public sealed record EnrollmentUrlsStatus(
    bool TenantInfoDetected,
    bool AreConfigured,
    bool AreExpected,
    string Summary,
    IReadOnlyList<string> Checks,
    IReadOnlyList<string> Warnings,
    string EnrollmentUrl,
    string TermsOfUseUrl,
    string ComplianceUrl,
    bool CanRepair);
