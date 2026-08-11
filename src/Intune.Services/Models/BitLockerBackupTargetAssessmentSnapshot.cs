namespace WindowsClientCenter.Intune.Services.Models;

public sealed record BitLockerBackupTargetAssessmentSnapshot(
    string Target,
    bool IsConfigured,
    bool? HasSuccessEvidence,
    bool HasFailureEvidence,
    string Assessment,
    string EvidenceText)
{
    public string SuccessEvidenceText => HasSuccessEvidence switch
    {
        true => "Present",
        false => "Not present",
        null => "Unknown"
    };

    public string FailureEvidenceText => HasFailureEvidence ? "Present" : "Not present";
}
