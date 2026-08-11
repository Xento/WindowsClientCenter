using WindowsClientCenter.Intune.Services.Models;
using WindowsClientCenter.Intune.Services.Runtime;

namespace WindowsClientCenter.Tests.IntuneServices;

public sealed class BitLockerRuntimeTests
{
    [Fact]
    public async Task LocalBitLockerService_ParsesSnapshotWithMixedVolumes()
    {
        const string payload = """
        {
          "machineName": "CLIENT01",
          "capturedAtUtc": "2026-03-29T10:15:00Z",
          "capabilities": {
            "isBitLockerCommandAvailable": true,
            "isAdministrator": true,
            "supportsSuspendProtection": true,
            "supportsResumeProtection": true,
            "supportsRecoveryPasswordProtectorOperations": true,
            "supportsBackupToAd": true,
            "supportsBackupToEntra": true,
            "isDomainJoined": true,
            "isEntraJoined": true,
            "warnings": []
          },
          "policies": [
            {
              "settingName": "RequireDeviceEncryption",
              "valueText": "Enabled",
              "source": "MDM (Intune)",
              "category": "Encryption",
              "sourcePath": "HKLM:\\SOFTWARE\\Microsoft\\PolicyManager\\current\\device\\BitLocker"
            },
            {
              "settingName": "EncryptionMethodWithXtsOs",
              "valueText": "7",
              "source": "Group Policy",
              "category": "Operating system drive",
              "sourcePath": "HKLM:\\SOFTWARE\\Policies\\Microsoft\\FVE"
            },
            {
              "settingName": "CCM_BitLockerPolicy.RequireRecoveryPassword",
              "valueText": "True",
              "source": "Configuration Manager",
              "category": "Recovery",
              "sourcePath": "root\\ccm\\policy\\machine\\actualconfig\\CCM_BitLockerPolicy"
            }
          ],
          "hasIntunePolicies": true,
          "hasGpoPolicies": true,
          "hasMecmPolicies": true,
          "volumes": [
            {
              "mountPoint": "C:",
              "volumeType": "OperatingSystem",
              "protectionStatusText": "Protected",
              "volumeStatusText": "FullyEncrypted",
              "lockStatusText": "Unlocked",
              "encryptionPercentage": 100,
              "encryptionMethodText": "XtsAes256",
              "autoUnlockText": "Disabled",
              "suspendRebootCount": null,
              "healthLevel": "Red",
              "complianceStatusText": "Recovery required",
              "complianceDetailsText": "Event 24636 indicates that BitLocker recovery is currently required.",
              "backupEligibilityText": "AD DS: no local evidence | Microsoft Entra: success evidence present",
              "configuredBackupTargetsText": "Configured: AD DS, Microsoft Entra",
              "backupAssessmentText": "AD DS: no local evidence | Microsoft Entra: success evidence present",
              "backupTargetAssessments": [
                {
                  "target": "AD DS",
                  "isConfigured": true,
                  "hasSuccessEvidence": null,
                  "hasFailureEvidence": false,
                  "assessment": "ConfiguredButNoEvidence",
                  "evidenceText": "AD DS is configured by local policy, but no local escrow proof is evaluated."
                },
                {
                  "target": "MBAM",
                  "isConfigured": false,
                  "hasSuccessEvidence": null,
                  "hasFailureEvidence": false,
                  "assessment": "NotConfigured",
                  "evidenceText": "Target is not configured by local policy."
                },
                {
                  "target": "Microsoft Entra",
                  "isConfigured": true,
                  "hasSuccessEvidence": true,
                  "hasFailureEvidence": false,
                  "assessment": "ConfiguredAndSuccessEvidencePresent",
                  "evidenceText": "Microsoft Entra escrow success event 845 found for 'C:' in the last 7 days."
                }
              ],
              "isEncrypted": true,
              "isProtectionOn": true,
              "isProtectionSuspended": false,
              "protectors": [
                {
                  "protectorId": "p-tpm",
                  "protectorType": "Tpm",
                  "friendlyLabel": "TPM",
                  "isRecoveryPassword": false,
                  "isRemovable": false,
                  "backupTargetsText": "Not applicable"
                },
                {
                  "protectorId": "p-rec-1",
                  "protectorType": "RecoveryPassword",
                  "friendlyLabel": "Recovery password",
                  "isRecoveryPassword": true,
                  "isRemovable": true,
                  "backupTargetsText": "Configured: AD DS, Microsoft Entra"
                }
              ]
            },
            {
              "mountPoint": "D:",
              "volumeType": "FixedData",
              "protectionStatusText": "Protection suspended",
              "volumeStatusText": "FullyEncrypted",
              "lockStatusText": "Unlocked",
              "encryptionPercentage": 100,
              "encryptionMethodText": "XtsAes128",
              "autoUnlockText": "Enabled",
              "suspendRebootCount": 2,
              "healthLevel": "Yellow",
              "complianceStatusText": "Recovered",
              "complianceDetailsText": "A later recovery-password event 24652 indicates that the previous recovery state was cleared.",
              "backupEligibilityText": "MBAM: success evidence present",
              "configuredBackupTargetsText": "Configured: MBAM",
              "backupAssessmentText": "MBAM: success evidence present",
              "backupTargetAssessments": [
                {
                  "target": "AD DS",
                  "isConfigured": false,
                  "hasSuccessEvidence": null,
                  "hasFailureEvidence": false,
                  "assessment": "NotConfigured",
                  "evidenceText": "Target is not configured by local policy."
                },
                {
                  "target": "MBAM",
                  "isConfigured": true,
                  "hasSuccessEvidence": true,
                  "hasFailureEvidence": false,
                  "assessment": "ConfiguredAndSuccessEvidencePresent",
                  "evidenceText": "MBAM success event 29 found."
                },
                {
                  "target": "Microsoft Entra",
                  "isConfigured": false,
                  "hasSuccessEvidence": null,
                  "hasFailureEvidence": false,
                  "assessment": "NotConfigured",
                  "evidenceText": "Target is not configured by local policy."
                }
              ],
              "isEncrypted": true,
              "isProtectionOn": false,
              "isProtectionSuspended": true,
              "protectors": [
                {
                  "protectorId": "p-rec-2",
                  "protectorType": "RecoveryPassword",
                  "friendlyLabel": "Recovery password",
                  "isRecoveryPassword": true,
                  "isRemovable": true,
                  "backupTargetsText": "Configured: MBAM"
                }
              ]
            }
          ]
        }
        """;

        var service = new LocalBitLockerService(new FakePowerShellExecutor(new PowershellExecutionResult(0, payload, string.Empty)));

        var snapshot = await service.GetSnapshotAsync("CLIENT01", CancellationToken.None);

        Assert.Equal("CLIENT01", snapshot.Host);
        Assert.Equal("CLIENT01", snapshot.MachineName);
        Assert.Equal(2, snapshot.Volumes.Count);
        Assert.Equal(2, snapshot.EncryptedVolumeCount);
        Assert.Equal(1, snapshot.ProtectedVolumeCount);
        Assert.Equal(1, snapshot.SuspendedVolumeCount);
        Assert.Equal("Red", snapshot.OverallHealthLevel);
        Assert.Equal(3, snapshot.Policies.Count);
        Assert.True(snapshot.HasIntunePolicies);
        Assert.True(snapshot.HasGpoPolicies);
        Assert.True(snapshot.HasMecmPolicies);
        Assert.Equal("Encryption required", snapshot.Policies.Single(policy => policy.SettingName == "RequireDeviceEncryption").ValueMeaningText);
        Assert.Equal("XTS-AES 256-bit", snapshot.Policies.Single(policy => policy.SettingName == "EncryptionMethodWithXtsOs").ValueMeaningText);
        Assert.Equal("Recovery required", snapshot.Volumes[0].ComplianceStatusText);
        Assert.Equal("Recovered", snapshot.Volumes[1].ComplianceStatusText);
        Assert.Equal("Configured: AD DS, Microsoft Entra", snapshot.Volumes[0].ConfiguredBackupTargetsText);
        Assert.Equal("ConfiguredButNoEvidence", snapshot.Volumes[0].BackupTargetAssessments.Single(item => item.Target == "AD DS").Assessment);
        Assert.True(snapshot.Volumes[0].BackupTargetAssessments.Single(item => item.Target == "Microsoft Entra").HasSuccessEvidence ?? false);
        Assert.True(snapshot.Volumes[1].BackupTargetAssessments.Single(item => item.Target == "MBAM").HasSuccessEvidence ?? false);
        Assert.True(snapshot.Capabilities.SupportsBackupToEntra);
        Assert.Equal("D:", snapshot.Volumes[1].MountPoint);
        Assert.Equal(2, snapshot.Volumes[1].SuspendRebootCount);
        Assert.Contains(snapshot.Volumes[0].Protectors, protector => protector.IsRecoveryPassword);
    }

    [Fact]
    public async Task LocalBitLockerService_ReturnsFailureSnapshot_WhenExecutorFails()
    {
        var service = new LocalBitLockerService(new FakePowerShellExecutor(new PowershellExecutionResult(1, string.Empty, "Access denied")));

        var snapshot = await service.GetSnapshotAsync("CLIENT01", CancellationToken.None);

        Assert.Empty(snapshot.Volumes);
        Assert.Equal("Red", snapshot.OverallHealthLevel);
        Assert.Contains(snapshot.Capabilities.Warnings, warning => warning.Contains("Access denied", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LocalBitLockerService_ParsesBackupWarningResult()
    {
        const string payload = """
        {
          "success": false,
          "warning": true,
          "message": "Added a new recovery-password protector, but backup failed. The old protector was kept.",
          "errorCode": "backup_failed",
          "newProtectorId": "new-protector-id",
          "details": [
            "Microsoft Entra backup failed: access denied",
            "The old recovery-password protector was kept because backup of the new protector failed."
          ]
        }
        """;

        var service = new LocalBitLockerService(new FakePowerShellExecutor(new PowershellExecutionResult(0, payload, string.Empty)));

        var result = await service.RotateRecoveryPasswordAsync("CLIENT01", "C:", "old-protector-id", CancellationToken.None);

        Assert.False(result.Success);
        Assert.True(result.Warning);
        Assert.Equal("backup_failed", result.ErrorCode);
        Assert.Equal("new-protector-id", result.NewProtectorId);
        Assert.Equal(2, result.Details?.Count);
    }

    [Fact]
    public async Task LocalBitLockerService_ParsesSuccessfulActionResult()
    {
        const string payload = """
        {
          "success": true,
          "warning": false,
          "message": "Backed up the selected recovery-password protector to Microsoft Entra, AD DS.",
          "details": [
            "Microsoft Entra backup succeeded.",
            "AD DS backup succeeded."
          ]
        }
        """;

        var service = new LocalBitLockerService(new FakePowerShellExecutor(new PowershellExecutionResult(0, payload, string.Empty)));

        var result = await service.BackupRecoveryPasswordAsync("CLIENT01", "C:", "protector-id", CancellationToken.None);

        Assert.True(result.Success);
        Assert.False(result.Warning);
        Assert.Null(result.ErrorCode);
        Assert.Equal(2, result.Details?.Count);
    }

    private sealed class FakePowerShellExecutor(PowershellExecutionResult result) : IPowerShellExecutor
    {
        public ValueTask<PowershellExecutionResult> ExecuteForHostAsync(string host, string scriptBody, CancellationToken cancellationToken)
        {
            _ = host;
            _ = scriptBody;
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(result);
        }
    }
}
