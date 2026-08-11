namespace WindowsClientCenter.Intune.Services.Models;

public sealed record EnrollmentRepairPreview(
    string Host,
    bool CanExecute,
    string ConfirmationText,
    string Summary,
    IReadOnlyList<string> Blockers,
    IReadOnlyList<string> Steps,
    IReadOnlyList<EnrollmentArtifact> ArtifactsToRemove);
