namespace WindowsClientCenter.Intune.Services.Models;

public sealed record BitLockerHostSnapshot(
    string Host,
    string MachineName,
    DateTimeOffset CapturedAt,
    BitLockerCapabilitySnapshot Capabilities,
    IReadOnlyList<BitLockerPolicySettingSnapshot> Policies,
    bool HasIntunePolicies,
    bool HasGpoPolicies,
    bool HasMecmPolicies,
    IReadOnlyList<BitLockerVolumeSnapshot> Volumes,
    int EncryptedVolumeCount,
    int ProtectedVolumeCount,
    int SuspendedVolumeCount,
    int WarningVolumeCount,
    int ErrorVolumeCount,
    string OverallHealthLevel);
