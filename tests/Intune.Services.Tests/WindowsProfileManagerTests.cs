using System.Text.Json;
using WindowsClientCenter.Intune.Services.Models;
using WindowsClientCenter.Intune.Services.Runtime;
using Xunit;

namespace WindowsClientCenter.Tests.IntuneServices;

public sealed class WindowsProfileManagerTests
{
    [Fact]
    public async Task GetProfilesAsync_MapsPayloadToSnapshot()
    {
        var executor = new RecordingPowerShellExecutor
        {
            Result = new PowershellExecutionResult(
                0,
                JsonSerializer.Serialize(new
                {
                    profiles = new object[]
                    {
                        new
                        {
                            accountName = @"CONTOSO\alice",
                            sid = "S-1-5-21-1000",
                            localPath = @"C:\Users\alice",
                            lastUseTimeUtc = "2026-04-20T08:15:00Z",
                            serverProfilePath = @"\\profiles\alice",
                            isLoaded = true,
                            isTemporary = false,
                            isRoaming = true,
                            isMandatory = false,
                            isCorrupted = false,
                            isSpecial = false
                        },
                        new
                        {
                            accountName = "Public",
                            sid = "S-1-5-21-1001",
                            localPath = @"C:\Users\Public",
                            lastUseTimeUtc = (string?)null,
                            serverProfilePath = string.Empty,
                            isLoaded = false,
                            isTemporary = false,
                            isRoaming = false,
                            isMandatory = false,
                            isCorrupted = false,
                            isSpecial = true
                        }
                    },
                    policy = new
                    {
                        maxProfileSizeMb = 500,
                        includesRegistryInQuota = true,
                        excludedRelativePaths = new[] { "AppData\\Local\\Temp", "Downloads" },
                        source = @"HKLM:\SOFTWARE\Policies\Microsoft\Windows\System"
                    },
                    warnings = Array.Empty<string>()
                }),
                string.Empty)
        };

        var manager = new WindowsProfileManager(executor);

        var snapshot = await manager.GetProfilesAsync("CLIENT01", CancellationToken.None);

        Assert.Equal("CLIENT01", snapshot.Host);
        Assert.Single(snapshot.Profiles);
        Assert.Equal(@"CONTOSO\alice", snapshot.Profiles[0].AccountName);
        Assert.True(snapshot.Profiles[0].IsRoaming);
        Assert.Equal(500, snapshot.Policy.MaxProfileSizeMb);
        Assert.True(snapshot.Policy.IncludesRegistryInQuota);
        Assert.Equal(2, snapshot.Policy.ExcludedRelativePaths.Count);
    }

    [Fact]
    public async Task GetProfilesAsync_ReturnsNotConfiguredPolicy_WhenMissing()
    {
        var executor = new RecordingPowerShellExecutor
        {
            Result = new PowershellExecutionResult(0, "{}", string.Empty)
        };

        var manager = new WindowsProfileManager(executor);

        var snapshot = await manager.GetProfilesAsync("CLIENT01", CancellationToken.None);

        Assert.Equal("Not configured", snapshot.Policy.Source);
        Assert.False(snapshot.Policy.IsConfigured);
    }

    [Fact]
    public void ParseRobocopyResult_ParsesSummaryValues()
    {
        var output = """
------------------------------------------------------------------------------
               Total    Copied   Skipped  Mismatch    FAILED    Extras
    Dirs :        42         0        42         0         0         0
   Files :       120         0       120         0         0         0
   Bytes : 123,456,789         0 123,456,789         0         0         0
------------------------------------------------------------------------------
""";

        var result = WindowsProfileManager.ParseRobocopyResult(@"C:\Users\alice", ProfileSizeCalculationMode.Raw, output, string.Empty);

        Assert.Equal(42, result.DirectoryCount);
        Assert.Equal(120, result.FileCount);
        Assert.Equal(123456789L, result.SizeBytes);
    }

    [Fact]
    public async Task CalculateProfileSizeAsync_AcceptsRobocopyExitCodeBelowEight()
    {
        var executor = new RecordingPowerShellExecutor
        {
            Result = new PowershellExecutionResult(
                3,
                "Dirs : 1 0 1 0 0 0\nFiles : 2 0 2 0 0 0\nBytes : 2,048 0 2,048 0 0 0",
                string.Empty)
        };

        var manager = new WindowsProfileManager(executor);

        var result = await manager.CalculateProfileSizeAsync("CLIENT01", @"C:\Users\alice", ProfileSizeCalculationMode.PolicyExcluded, CancellationToken.None);

        Assert.Equal(2048L, result.SizeBytes);
        Assert.Contains("$usePolicyExclusions=$true", executor.LastScript, StringComparison.Ordinal);
        Assert.Contains("ExcludeProfileDirs", executor.LastScript, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeleteProfileAsync_UsesRenameAndRegistryRemovalScript()
    {
        var executor = new RecordingPowerShellExecutor
        {
            Result = new PowershellExecutionResult(0, "Deleted.", string.Empty)
        };

        var manager = new WindowsProfileManager(executor);

        var result = await manager.DeleteProfileAsync("CLIENT01", "S-1-5-21-1000", @"C:\Users\alice", CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("Rename-Item -LiteralPath $profilePath", executor.LastScript, StringComparison.Ordinal);
        Assert.Contains("ProfileList", executor.LastScript, StringComparison.Ordinal);
        Assert.Contains("Remove-Item -LiteralPath $registryPath -Recurse -Force", executor.LastScript, StringComparison.Ordinal);
        Assert.Contains("$sid='S-1-5-21-1000';", executor.LastScript, StringComparison.Ordinal);
    }

    private sealed class RecordingPowerShellExecutor : IPowerShellExecutor
    {
        public PowershellExecutionResult Result { get; set; } = new(0, string.Empty, string.Empty);
        public string LastScript { get; private set; } = string.Empty;

        public ValueTask<PowershellExecutionResult> ExecuteForHostAsync(string host, string scriptBody, CancellationToken cancellationToken)
        {
            LastScript = scriptBody;
            return ValueTask.FromResult(Result);
        }
    }
}
