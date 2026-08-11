using WindowsClientCenter.Intune.Services.Contracts;
using WindowsClientCenter.Intune.Services.Models;
using WindowsClientCenter.Plugin.Abstractions.Contracts;
using WindowsClientCenter.Plugins.BitLockerAgent.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace WindowsClientCenter.Tests.Plugins.BitLockerAgent;

public sealed class BitLockerAgentViewModelTests
{
    [Fact]
    public async Task LoadAsync_PopulatesVolumesAndProtectors()
    {
        var service = new FakeLocalBitLockerService();
        var viewModel = new BitLockerAgentViewModel(new FakePluginContext(BuildServices(service)), "bitlocker");

        await viewModel.LoadAsync(CancellationToken.None);

        Assert.Equal(2, viewModel.Volumes.Count);
        Assert.Contains("MDM (Intune)", viewModel.PolicySourceSummaryText, StringComparison.Ordinal);
        Assert.NotNull(viewModel.SelectedVolume);
        Assert.Equal("C:", viewModel.SelectedVolume!.MountPoint);
        Assert.Equal(3, viewModel.Protectors.Count);
        Assert.NotNull(viewModel.SelectedProtector);
    }

    [Fact]
    public async Task SelectedVolumeChanged_UpdatesProtectorGrid()
    {
        var service = new FakeLocalBitLockerService();
        var viewModel = new BitLockerAgentViewModel(new FakePluginContext(BuildServices(service)), "bitlocker");
        await viewModel.LoadAsync(CancellationToken.None);

        viewModel.SelectedVolume = viewModel.Volumes.Single(volume => volume.MountPoint == "D:");

        Assert.Single(viewModel.Protectors);
        Assert.Equal("rec-d-1", viewModel.Protectors[0].ProtectorId);
    }

    [Fact]
    public async Task RemoveRecoveryPasswordCommand_Disabled_ForLastRecoveryProtector()
    {
        var service = new FakeLocalBitLockerService();
        var viewModel = new BitLockerAgentViewModel(new FakePluginContext(BuildServices(service)), "bitlocker");
        await viewModel.LoadAsync(CancellationToken.None);

        viewModel.SelectedVolume = viewModel.Volumes.Single(volume => volume.MountPoint == "D:");
        viewModel.SelectedProtector = viewModel.Protectors[0];

        Assert.False(viewModel.RemoveRecoveryPasswordProtectorCommand.CanExecute(null));
    }

    [Fact]
    public async Task BackupRecoveryPasswordAsync_AddsOperationLogAndRefreshes()
    {
        var service = new FakeLocalBitLockerService
        {
            BackupResult = BitLockerActionResult.Ok(
                "Backed up the selected recovery-password protector to Microsoft Entra.",
                details: ["Microsoft Entra backup succeeded."])
        };
        var viewModel = new BitLockerAgentViewModel(new FakePluginContext(BuildServices(service)), "bitlocker");
        await viewModel.LoadAsync(CancellationToken.None);

        viewModel.SelectedVolume = viewModel.Volumes.Single(volume => volume.MountPoint == "C:");
        viewModel.SelectedProtector = viewModel.Protectors.Single(protector => protector.ProtectorId == "rec-c-1");

        await viewModel.BackupRecoveryPasswordCommand.ExecuteAsync(null);

        Assert.Single(viewModel.OperationLogEntries);
        Assert.Equal("Success", viewModel.OperationLogEntries[0].Level);
        Assert.Contains("Backed up the selected recovery-password protector", viewModel.OperationLogEntries[0].Message, StringComparison.Ordinal);
        Assert.True(service.BackupCalled);
    }

    private static ServiceProvider BuildServices(ILocalBitLockerService bitLockerService)
    {
        return new ServiceCollection()
            .AddSingleton<ITargetHostService>(new FakeTargetHostService("CLIENT01"))
            .AddSingleton(bitLockerService)
            .AddSingleton<IHostStatusLogSink>(new FakeHostStatusLogSink())
            .AddSingleton<IHostBusyStateSink>(new FakeHostBusyStateSink())
            .BuildServiceProvider();
    }

    private static BitLockerHostSnapshot CreateSnapshot() =>
        new(
            "CLIENT01",
            "CLIENT01",
            DateTimeOffset.UtcNow,
            new BitLockerCapabilitySnapshot(
                true,
                true,
                true,
                true,
                true,
                true,
                true,
                true,
                true,
                []),
            [
                new BitLockerPolicySettingSnapshot(
                    "RequireDeviceEncryption",
                    "Enabled",
                    "MDM (Intune)",
                    "Encryption",
                    @"HKLM:\SOFTWARE\Microsoft\PolicyManager\current\device\BitLocker",
                    "Encryption required"),
                new BitLockerPolicySettingSnapshot(
                    "EncryptionMethodWithXtsOs",
                    "7",
                    "Group Policy",
                    "Operating system drive",
                    @"HKLM:\SOFTWARE\Policies\Microsoft\FVE",
                    "XTS-AES 256-bit")
            ],
            true,
            true,
            false,
            [
                new BitLockerVolumeSnapshot(
                    "C:",
                    "OperatingSystem",
                    "Protected",
                    "FullyEncrypted",
                    "Unlocked",
                    100,
                    "XtsAes256",
                    "Disabled",
                    null,
                    "Green",
                    "Compliant",
                    "No unresolved BitLocker recovery event was detected.",
                    "AD DS: no local evidence | Microsoft Entra: no local evidence",
                    "Configured: AD DS, Microsoft Entra",
                    "AD DS: no local evidence | Microsoft Entra: no local evidence",
                    [
                        new BitLockerBackupTargetAssessmentSnapshot("AD DS", true, null, false, "ConfiguredButNoEvidence", "AD DS is configured by local policy, but no local escrow proof is evaluated."),
                        new BitLockerBackupTargetAssessmentSnapshot("MBAM", false, null, false, "NotConfigured", "Target is not configured by local policy."),
                        new BitLockerBackupTargetAssessmentSnapshot("Microsoft Entra", true, null, false, "ConfiguredButNoEvidence", "Microsoft Entra is configured by local MDM recovery policy, but no local escrow proof was found.")
                    ],
                    true,
                    true,
                    false,
                    [
                        new BitLockerProtectorSnapshot("tpm-c", "Tpm", "TPM", false, false, "Not applicable"),
                        new BitLockerProtectorSnapshot("rec-c-1", "RecoveryPassword", "Recovery password", true, true, "Configured: AD DS, Microsoft Entra"),
                        new BitLockerProtectorSnapshot("rec-c-2", "RecoveryPassword", "Recovery password", true, true, "Configured: AD DS, Microsoft Entra")
                    ]),
                new BitLockerVolumeSnapshot(
                    "D:",
                    "FixedData",
                    "Protection suspended",
                    "FullyEncrypted",
                    "Unlocked",
                    100,
                    "XtsAes128",
                    "Enabled",
                    2,
                    "Yellow",
                    "Recovered",
                    "A later recovery-password event 24652 indicates that the previous recovery state was cleared.",
                    "MBAM: success evidence present",
                    "Configured: MBAM",
                    "MBAM: success evidence present",
                    [
                        new BitLockerBackupTargetAssessmentSnapshot("AD DS", false, null, false, "NotConfigured", "Target is not configured by local policy."),
                        new BitLockerBackupTargetAssessmentSnapshot("MBAM", true, true, false, "ConfiguredAndSuccessEvidencePresent", "MBAM success event 29 found."),
                        new BitLockerBackupTargetAssessmentSnapshot("Microsoft Entra", false, null, false, "NotConfigured", "Target is not configured by local policy.")
                    ],
                    true,
                    false,
                    true,
                    [
                        new BitLockerProtectorSnapshot("rec-d-1", "RecoveryPassword", "Recovery password", true, true, "Configured: MBAM")
                    ])
            ],
            2,
            1,
            1,
            1,
            0,
            "Yellow");

    private sealed class FakeLocalBitLockerService : ILocalBitLockerService
    {
        public bool BackupCalled { get; private set; }
        public BitLockerActionResult BackupResult { get; set; } = BitLockerActionResult.Ok("Backed up the selected recovery-password protector.");

        public ValueTask<BitLockerHostSnapshot> GetSnapshotAsync(string host, CancellationToken cancellationToken, bool verboseDiagnostics = false)
        {
            _ = host;
            _ = verboseDiagnostics;
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(CreateSnapshot());
        }

        public ValueTask<BitLockerActionResult> SuspendProtectionAsync(string host, string mountPoint, int rebootCount, CancellationToken cancellationToken, bool verboseDiagnostics = false)
        {
            _ = host;
            _ = mountPoint;
            _ = rebootCount;
            _ = verboseDiagnostics;
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(BitLockerActionResult.Ok("BitLocker protection was suspended."));
        }

        public ValueTask<BitLockerActionResult> ResumeProtectionAsync(string host, string mountPoint, CancellationToken cancellationToken, bool verboseDiagnostics = false)
        {
            _ = host;
            _ = mountPoint;
            _ = verboseDiagnostics;
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(BitLockerActionResult.Ok("BitLocker protection was resumed."));
        }

        public ValueTask<BitLockerActionResult> AddRecoveryPasswordProtectorAsync(string host, string mountPoint, CancellationToken cancellationToken, bool verboseDiagnostics = false)
        {
            _ = host;
            _ = mountPoint;
            _ = verboseDiagnostics;
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(BitLockerActionResult.Ok("Added a new recovery-password protector.", "new-rec-id"));
        }

        public ValueTask<BitLockerActionResult> RemoveRecoveryPasswordProtectorAsync(string host, string mountPoint, string protectorId, CancellationToken cancellationToken, bool verboseDiagnostics = false)
        {
            _ = host;
            _ = mountPoint;
            _ = protectorId;
            _ = verboseDiagnostics;
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(BitLockerActionResult.Ok("Removed the selected recovery-password protector."));
        }

        public ValueTask<BitLockerActionResult> BackupRecoveryPasswordAsync(string host, string mountPoint, string protectorId, CancellationToken cancellationToken, bool verboseDiagnostics = false)
        {
            _ = host;
            _ = mountPoint;
            _ = protectorId;
            _ = verboseDiagnostics;
            cancellationToken.ThrowIfCancellationRequested();
            BackupCalled = true;
            return ValueTask.FromResult(BackupResult);
        }

        public ValueTask<BitLockerActionResult> RotateRecoveryPasswordAsync(string host, string mountPoint, string protectorId, CancellationToken cancellationToken, bool verboseDiagnostics = false)
        {
            _ = host;
            _ = mountPoint;
            _ = protectorId;
            _ = verboseDiagnostics;
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(BitLockerActionResult.Ok("Rotated the recovery-password protector.", "new-rec-id"));
        }
    }

    private sealed class FakePluginContext(IServiceProvider services, IReadOnlyDictionary<string, string>? settings = null) : IPluginContext
    {
        public Microsoft.Extensions.Logging.ILogger Logger => NullLogger.Instance;
        public IServiceProvider Services { get; } = services;
        public string EnvironmentName => "test";
        public IReadOnlyDictionary<string, string> Settings { get; } = settings ?? new Dictionary<string, string>();
    }

    private sealed class FakeTargetHostService(string host) : ITargetHostService
    {
        private string _currentHost = host;
        private long _version = 1;
        private CancellationTokenSource _selectionCancellationTokenSource = new();

        public string CurrentHost => _currentHost;

        public event EventHandler<string>? HostChanged;

        public HostSelection CaptureSelection() => new(_currentHost, _version, _selectionCancellationTokenSource.Token);

        public bool IsCurrent(HostSelection selection) => selection.Version == _version && string.Equals(selection.Host, _currentHost, StringComparison.OrdinalIgnoreCase);

        public void SetCurrentHost(string host)
        {
            if (!string.Equals(_currentHost, host, StringComparison.OrdinalIgnoreCase))
            {
                _selectionCancellationTokenSource.Cancel();
                _selectionCancellationTokenSource.Dispose();
                _selectionCancellationTokenSource = new CancellationTokenSource();
                _version++;
            }

            _currentHost = host;
            HostChanged?.Invoke(this, host);
        }
    }

    private sealed class FakeHostStatusLogSink : IHostStatusLogSink
    {
        public List<string> Messages { get; } = [];

        public void Append(string message)
        {
            Messages.Add(message);
        }
    }

    private sealed class FakeHostBusyStateSink : IHostBusyStateSink
    {
        public void ClearBusyState(string ownerId)
        {
            _ = ownerId;
        }

        public void SetBusyState(string ownerId, string shortStatus, IReadOnlyList<string>? tasks = null)
        {
            _ = ownerId;
            _ = shortStatus;
            _ = tasks;
        }
    }
}
