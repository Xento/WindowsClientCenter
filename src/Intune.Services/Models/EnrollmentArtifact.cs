namespace WindowsClientCenter.Intune.Services.Models;

public sealed record EnrollmentArtifact(
    string ArtifactType,
    string ArtifactPath,
    string Description,
    string? EnrollmentId = null,
    bool IsRemovable = false);
