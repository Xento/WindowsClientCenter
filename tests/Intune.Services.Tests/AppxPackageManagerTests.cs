using System.Text.Json;
using WindowsClientCenter.Intune.Services.Models;
using WindowsClientCenter.Intune.Services.Runtime;
using System.Management.Automation.Language;
using Xunit;

namespace WindowsClientCenter.Tests.IntuneServices;

public sealed class AppxPackageManagerTests
{
    [Fact]
    public async Task GetPackagesAsync_MapsProvisioningAndPerUserState()
    {
        var executor = new RecordingPowerShellExecutor(new PowershellExecutionResult(
            0,
            JsonSerializer.Serialize(new
            {
                activeUserName = @"CONTOSO\Ada",
                activeUserSid = "S-1-5-21-1000",
                packages = new[]
                {
                    new
                    {
                        packageFullName = "Contoso.App_1.0.0.0_x64__abc",
                        packageFamilyName = "Contoso.App_abc",
                        name = "Contoso.App",
                        displayName = "Contoso App",
                        version = "1.0.0.0",
                        publisher = "CN=Contoso",
                        architecture = "X64",
                        installLocation = @"C:\Program Files\WindowsApps\Contoso.App",
                        isFramework = false,
                        isResourcePackage = false,
                        isBundle = false,
                        isOptional = false,
                        nonRemovable = false,
                        isProvisioned = true,
                        provisionedPackageName = "Contoso.App_1.0.0.0_neutral_~_abc",
                        users = new[]
                        {
                            new { userSid = "S-1-5-21-1000", userName = @"CONTOSO\Ada", installState = "Installed", isActiveUser = true }
                        }
                    }
                },
                warnings = Array.Empty<string>()
            }),
            string.Empty));
        var manager = new AppxPackageManager(executor);

        var snapshot = await manager.GetPackagesAsync("CLIENT01", CancellationToken.None);

        var package = Assert.Single(snapshot.Packages);
        var user = Assert.Single(package.Users);
        Assert.Equal(@"CONTOSO\Ada", snapshot.ActiveUserName);
        Assert.True(package.IsProvisioned);
        Assert.True(user.IsActiveUser);
        Assert.Equal("Installed", user.InstallState);
    }

    [Fact]
    public async Task SearchWingetAsync_KeepsExactIdAndSource()
    {
        var executor = new RecordingPowerShellExecutor(new PowershellExecutionResult(
            0,
            JsonSerializer.Serialize(new
            {
                entries = new[]
                {
                    new { id = "Microsoft.PowerToys", name = "PowerToys", version = "0.92.1", source = "winget" },
                    new { id = "9ABC", name = "Power Toys", version = "Unknown", source = "msstore" }
                },
                warnings = Array.Empty<string>()
            }),
            string.Empty));
        var manager = new AppxPackageManager(executor);

        var result = await manager.SearchWingetAsync("CLIENT01", "power toys", CancellationToken.None);

        Assert.Equal(2, result.Entries.Count);
        Assert.Contains(result.Entries, entry => entry.Id == "Microsoft.PowerToys" && entry.Source == "winget");
        Assert.Contains(result.Entries, entry => entry.Id == "9ABC" && entry.Source == "msstore");
        Assert.Contains("--source $source", executor.LastScript, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildWingetActionScriptBody_UsesExactSelectionAndMachineScope()
    {
        var script = AppxPackageManager.BuildWingetActionScriptBody(
            "install",
            new WingetCatalogEntry("Contoso.App", "Contoso App", "1.0", "msstore"),
            WingetInstallScope.Machine);

        Assert.Contains("'Contoso.App'", script, StringComparison.Ordinal);
        Assert.Contains("'msstore'", script, StringComparison.Ordinal);
        Assert.Contains("'--exact'", script, StringComparison.Ordinal);
        Assert.Contains("'--scope','machine'", script, StringComparison.Ordinal);
        Assert.Contains("'--disable-interactivity'", script, StringComparison.Ordinal);
        Assert.Contains("'--accept-package-agreements'", script, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildWingetActionScriptBody_UsesInteractiveScheduledTaskForActiveUser()
    {
        var script = AppxPackageManager.BuildWingetActionScriptBody(
            "upgrade",
            new WingetCatalogEntry("Contoso.App", "Contoso App", "1.0", "winget"),
            WingetInstallScope.ActiveUser);

        Assert.Contains("Get-IccWingetPath -ForActiveUser", script, StringComparison.Ordinal);
        Assert.Contains("New-ScheduledTaskPrincipal", script, StringComparison.Ordinal);
        Assert.Contains("-LogonType Interactive", script, StringComparison.Ordinal);
        Assert.Contains("'--scope','user'", script, StringComparison.Ordinal);
    }

    [Fact]
    public void NativeActionScripts_UseExactPackageAndUserIdentities()
    {
        var userScript = AppxPackageManager.BuildRemoveForUserScriptBody("Contoso.App_1.0_x64__abc", "S-1-5-21-1000");
        var allUsersScript = AppxPackageManager.BuildRemoveForAllUsersScriptBody("Contoso.App_1.0_x64__abc");
        var provisionedScript = AppxPackageManager.BuildRemoveProvisioningScriptBody("Contoso.App_1.0_neutral_~_abc");

        Assert.Contains("PackageFullName -eq $name", userScript, StringComparison.Ordinal);
        Assert.Contains("-User 'S-1-5-21-1000'", userScript, StringComparison.Ordinal);
        Assert.Contains("Remove-AppxPackage -AllUsers", allUsersScript, StringComparison.Ordinal);
        Assert.Contains("PackageName -eq $name", provisionedScript, StringComparison.Ordinal);
        Assert.Contains("Remove-AppxProvisionedPackage -Online -AllUsers", provisionedScript, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InventoryCancellation_IsPropagated()
    {
        var executor = new RecordingPowerShellExecutor(new PowershellExecutionResult(0, string.Empty, string.Empty))
        {
            ExceptionToThrow = new OperationCanceledException()
        };
        var manager = new AppxPackageManager(executor);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manager.GetPackagesAsync("CLIENT01", CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task DestructiveActions_RefuseMissingPackageIdentity()
    {
        var executor = new RecordingPowerShellExecutor(new PowershellExecutionResult(0, string.Empty, string.Empty));
        var manager = new AppxPackageManager(executor);

        var result = await manager.RemoveForAllUsersAsync("CLIENT01", string.Empty, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("missing_package_identity", result.ErrorCode);
        Assert.Equal(string.Empty, executor.LastScript);
    }

    [Fact]
    public void GeneratedScripts_ParseAsPowerShell()
    {
        var package = new WingetCatalogEntry("Contoso.App", "Contoso App", "1.0", "winget");
        string[] scripts =
        [
            AppxPackageManager.BuildInventoryScriptBody(),
            AppxPackageManager.BuildWingetSearchScriptBody("contoso"),
            AppxPackageManager.BuildWingetActionScriptBody("install", package, WingetInstallScope.Machine),
            AppxPackageManager.BuildWingetActionScriptBody("upgrade", package, WingetInstallScope.ActiveUser),
            AppxPackageManager.BuildRemoveForUserScriptBody("Contoso.App_1.0_x64__abc", "S-1-5-21-1000"),
            AppxPackageManager.BuildRemoveForAllUsersScriptBody("Contoso.App_1.0_x64__abc"),
            AppxPackageManager.BuildRemoveProvisioningScriptBody("Contoso.App_1.0_neutral_~_abc"),
            AppxPackageManager.BuildRegisterForActiveUserScriptBody("Contoso.App_1.0_x64__abc")
        ];

        foreach (var script in scripts)
        {
            _ = Parser.ParseInput(script, out _, out var errors);
            Assert.True(errors.Length == 0, string.Join(Environment.NewLine, errors.Select(error => error.Message)));
        }
    }

    private sealed class RecordingPowerShellExecutor(PowershellExecutionResult result) : IPowerShellExecutor
    {
        public string LastScript { get; private set; } = string.Empty;
        public Exception? ExceptionToThrow { get; init; }

        public ValueTask<PowershellExecutionResult> ExecuteForHostAsync(string host, string scriptBody, CancellationToken cancellationToken)
        {
            LastScript = scriptBody;
            return ExceptionToThrow is null
                ? ValueTask.FromResult(result)
                : ValueTask.FromException<PowershellExecutionResult>(ExceptionToThrow);
        }
    }
}
