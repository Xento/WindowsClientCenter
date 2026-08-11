using System.Text.Json;
using WindowsClientCenter.Intune.Services.Contracts;
using WindowsClientCenter.Intune.Services.Models;
using WindowsClientCenter.Intune.Services.Runtime;
using Xunit;

namespace WindowsClientCenter.Tests.IntuneServices;

public sealed class InstalledSoftwareManagerTests
{
    [Fact]
    public async Task GetInstalledSoftwareAsync_MapsSmsPayloadToEntries()
    {
        var executor = new RecordingPowerShellExecutor
        {
            Result = new PowershellExecutionResult(
                0,
                JsonSerializer.Serialize(new
                {
                    entries = new object[]
                    {
                        new
                        {
                            id = "sms|7zip",
                            name = "7-Zip",
                            version = "24.09",
                            publisher = "Igor Pavlov",
                            installDate = "20260420",
                            installLocation = @"C:\Program Files\7-Zip",
                            installSource = @"C:\Windows\ccmcache\7zip",
                            softwareCode = "{23170F69-40C1-2702-2409-000001000000}",
                            productCode = "{23170F69-40C1-2702-2409-000001000000}",
                            uninstallString = "MsiExec.exe /I{23170F69-40C1-2702-2409-000001000000}",
                            quietUninstallString = "MsiExec.exe /X{23170F69-40C1-2702-2409-000001000000} /qn",
                            source = "SMS_InstalledSoftware",
                            architecture = "x64"
                        }
                    },
                    warnings = Array.Empty<string>()
                }),
                string.Empty)
        };
        var manager = new InstalledSoftwareManager(executor);

        var snapshot = await manager.GetInstalledSoftwareAsync("CLIENT01", CancellationToken.None);

        var entry = Assert.Single(snapshot.Entries);
        Assert.Equal("CLIENT01", snapshot.Host);
        Assert.Equal("7-Zip", entry.Name);
        Assert.Equal("SMS_InstalledSoftware", entry.Source);
        Assert.True(entry.CanRepairMsi);
        Assert.True(entry.CanUninstallMsi);
        Assert.True(entry.CanQuietUninstall);
    }

    [Fact]
    public async Task GetInstalledSoftwareAsync_MapsRegistryFallbackPayloadToEntries()
    {
        var executor = new RecordingPowerShellExecutor
        {
            Result = new PowershellExecutionResult(
                0,
                JsonSerializer.Serialize(new
                {
                    entries = new object[]
                    {
                        new
                        {
                            id = "registry|x64|ContosoVpn",
                            name = "Contoso VPN Client",
                            version = "5.2.1",
                            publisher = "Contoso",
                            quietUninstallString = @"""C:\Program Files\Contoso\VPN\uninstall.exe"" /quiet",
                            source = "Registry",
                            architecture = "x64"
                        }
                    },
                    warnings = new[] { "SMS_InstalledSoftware inventory is unavailable." }
                }),
                string.Empty)
        };
        var manager = new InstalledSoftwareManager(executor);

        var snapshot = await manager.GetInstalledSoftwareAsync("CLIENT01", CancellationToken.None);

        var entry = Assert.Single(snapshot.Entries);
        Assert.Equal("Registry", entry.Source);
        Assert.False(entry.CanRepairMsi);
        Assert.False(entry.CanUninstallMsi);
        Assert.True(entry.CanQuietUninstall);
        Assert.Contains("SMS_InstalledSoftware inventory is unavailable.", snapshot.Warnings);
    }

    [Fact]
    public async Task GetInstalledSoftwareAsync_PropagatesCancellationFromExecutor()
    {
        var executor = new RecordingPowerShellExecutor
        {
            ExceptionToThrow = new OperationCanceledException()
        };
        var manager = new InstalledSoftwareManager(executor);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => manager.GetInstalledSoftwareAsync("CLIENT01", CancellationToken.None).AsTask());
    }

    [Fact]
    public void BuildGetInstalledSoftwareScriptBody_UsesSmsInventoryAndRegistryFallback()
    {
        var script = InstalledSoftwareManager.BuildGetInstalledSoftwareScriptBody();

        Assert.Contains("root\\CIMV2\\sms", script, StringComparison.Ordinal);
        Assert.Contains("SMS_InstalledSoftware", script, StringComparison.Ordinal);
        Assert.Contains("HKLM:\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall", script, StringComparison.Ordinal);
        Assert.Contains("HKLM:\\SOFTWARE\\Wow6432Node\\Microsoft\\Windows\\CurrentVersion\\Uninstall", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Win32_Product", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RepairMsiAsync_UsesExpectedSilentRepairScript()
    {
        var executor = new RecordingPowerShellExecutor
        {
            Result = new PowershellExecutionResult(0, "Repaired.", string.Empty)
        };
        var manager = new InstalledSoftwareManager(executor);

        var result = await manager.RepairMsiAsync("CLIENT01", "{23170F69-40C1-2702-2409-000001000000}", CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("msiexec.exe", executor.LastScript, StringComparison.Ordinal);
        Assert.Contains("/fpecmsu", executor.LastScript, StringComparison.Ordinal);
        Assert.Contains("REBOOT=ReallySuppress", executor.LastScript, StringComparison.Ordinal);
        Assert.Contains("REINSTALL=ALL", executor.LastScript, StringComparison.Ordinal);
        Assert.Contains("/q", executor.LastScript, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UninstallMsiAsync_UsesExpectedSilentUninstallScript()
    {
        var executor = new RecordingPowerShellExecutor
        {
            Result = new PowershellExecutionResult(0, "Uninstalled.", string.Empty)
        };
        var manager = new InstalledSoftwareManager(executor);

        var result = await manager.UninstallMsiAsync("CLIENT01", "{23170F69-40C1-2702-2409-000001000000}", CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("msiexec.exe", executor.LastScript, StringComparison.Ordinal);
        Assert.Contains("/x", executor.LastScript, StringComparison.Ordinal);
        Assert.Contains("REBOOT=ReallySuppress", executor.LastScript, StringComparison.Ordinal);
        Assert.Contains("/q", executor.LastScript, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RepairMsiAsync_RefusesInvalidProductCode()
    {
        var executor = new RecordingPowerShellExecutor();
        var manager = new InstalledSoftwareManager(executor);

        var result = await manager.RepairMsiAsync("CLIENT01", "not-msi", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("invalid_msi_product_code", result.ErrorCode);
        Assert.Equal(string.Empty, executor.LastScript);
    }

    [Fact]
    public async Task UninstallQuietAsync_ExecutesQuietUninstallString()
    {
        var executor = new RecordingPowerShellExecutor
        {
            Result = new PowershellExecutionResult(0, "Quiet uninstall complete.", string.Empty)
        };
        var manager = new InstalledSoftwareManager(executor);

        var result = await manager.UninstallQuietAsync("CLIENT01", @"""C:\App\uninstall.exe"" /quiet", "Contoso App", CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("cmd.exe", executor.LastScript, StringComparison.Ordinal);
        Assert.Contains(@"""C:\App\uninstall.exe"" /quiet", executor.LastScript, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UninstallQuietAsync_RefusesEmptyQuietUninstallString()
    {
        var executor = new RecordingPowerShellExecutor();
        var manager = new InstalledSoftwareManager(executor);

        var result = await manager.UninstallQuietAsync("CLIENT01", "", "Contoso App", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("no_quiet_uninstall", result.ErrorCode);
        Assert.Equal(string.Empty, executor.LastScript);
    }

    [Fact]
    public async Task ForceRemoveRegistryEntryAsync_ForMsiRemovesUninstallAndInstallerKeys()
    {
        var executor = new RecordingPowerShellExecutor
        {
            Result = new PowershellExecutionResult(0, "Removed.", string.Empty)
        };
        var manager = new InstalledSoftwareManager(executor);

        var result = await manager.ForceRemoveRegistryEntryAsync(
            "CLIENT01",
            new InstalledSoftwareEntry(
                "registry|x64|{12345678-1234-ABCD-1234-567890ABCDEF}",
                "Contoso MSI",
                "1.0",
                "Contoso",
                "20260420",
                @"C:\Program Files\Contoso",
                string.Empty,
                "{12345678-1234-ABCD-1234-567890ABCDEF}",
                "{12345678-1234-ABCD-1234-567890ABCDEF}",
                string.Empty,
                string.Empty,
                "Registry",
                "x64"),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains(@"Microsoft\Windows\CurrentVersion\Uninstall", executor.LastScript, StringComparison.Ordinal);
        Assert.Contains(@"SOFTWARE\Classes\Installer\Products", executor.LastScript, StringComparison.Ordinal);
        Assert.Contains(@"HKCR:\Installer\Products", executor.LastScript, StringComparison.Ordinal);
        Assert.Contains(@"Installer\UserData", executor.LastScript, StringComparison.Ordinal);
        Assert.Contains("876543214321DCBA2143658709BADCFE", executor.LastScript, StringComparison.Ordinal);
    }

    [Fact]
    public void ConvertToPackedMsiProductCode_UsesWindowsInstallerRegistryFormat()
    {
        var packedProductCode = InstalledSoftwareManager.ConvertToPackedMsiProductCode("{807b6b52-8124-4c81-a657-dc8df91a8b37}");

        Assert.Equal("25B6B708421818C46A75CDD89FA1B873", packedProductCode);
    }

    [Fact]
    public async Task ForceRemoveRegistryEntryAsync_RefusesRowsWithoutRegistryIdentity()
    {
        var executor = new RecordingPowerShellExecutor();
        var manager = new InstalledSoftwareManager(executor);

        var result = await manager.ForceRemoveRegistryEntryAsync(
            "CLIENT01",
            new InstalledSoftwareEntry(
                string.Empty,
                "Unknown",
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("no_registry_identity", result.ErrorCode);
        Assert.Equal(string.Empty, executor.LastScript);
    }

    [Fact]
    public async Task ForceRemoveRegistryEntryAsync_RefusesSmsRowsWithoutProductIdentity()
    {
        var executor = new RecordingPowerShellExecutor();
        var manager = new InstalledSoftwareManager(executor);
        var software = new InstalledSoftwareEntry(
            "sms||Contoso App",
            "Contoso App",
            "1.0",
            "Contoso",
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            "SMS_InstalledSoftware",
            string.Empty);

        var result = await manager.ForceRemoveRegistryEntryAsync("CLIENT01", software, CancellationToken.None);

        Assert.False(software.CanForceRemoveRegistryEntry);
        Assert.False(result.Success);
        Assert.Equal("no_registry_identity", result.ErrorCode);
        Assert.Equal(string.Empty, executor.LastScript);
    }

    private sealed class RecordingPowerShellExecutor : IPowerShellExecutor
    {
        public PowershellExecutionResult Result { get; set; } = new(0, string.Empty, string.Empty);
        public Exception? ExceptionToThrow { get; set; }
        public string LastScript { get; private set; } = string.Empty;

        public ValueTask<PowershellExecutionResult> ExecuteForHostAsync(string host, string scriptBody, CancellationToken cancellationToken)
        {
            LastScript = scriptBody;
            if (ExceptionToThrow is not null)
            {
                return ValueTask.FromException<PowershellExecutionResult>(ExceptionToThrow);
            }

            return ValueTask.FromResult(Result);
        }
    }
}
