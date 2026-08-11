using System.Diagnostics;
using System.Text;
using WindowsClientCenter.Plugins.PowerShellScripts;
using Xunit;

namespace WindowsClientCenter.Tests.Plugins.PowerShellScripts;

public sealed class PowerShellScriptLauncherTests
{
    [Fact]
    public async Task LaunchAsync_StartsPowerShellWindowForDirectScript()
    {
        ProcessStartInfo? capturedStartInfo = null;
        var launcher = new PowerShellScriptLauncher(startInfo =>
        {
            capturedStartInfo = startInfo;
            return Process.GetCurrentProcess();
        });

        var script = new PowerShellScriptCatalogEntry(
            "Inventory/Test.ps1",
            "Test",
            "Inventory/Test.ps1",
            @"C:\Scripts\Test.ps1",
            PowerShellScriptExecutionMode.DirectComputerName,
            []);

        var result = await launcher.LaunchAsync(
            "CLIENT01",
            script,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["ComputerName"] = "'CLIENT01'"
            },
            executor: null,
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.NotNull(capturedStartInfo);
        Assert.Equal("powershell.exe", capturedStartInfo!.FileName);
        Assert.Contains("-NoExit", capturedStartInfo.Arguments, StringComparison.Ordinal);
        Assert.True(capturedStartInfo.UseShellExecute);
        Assert.Equal(@"C:\Scripts", capturedStartInfo.WorkingDirectory);

        var launchScript = DecodeLaunchScript(capturedStartInfo.Arguments);
        Assert.Contains("& $scriptPath @params *>&1 | Out-Host", launchScript, StringComparison.Ordinal);
        Assert.Contains("$params['ComputerName'] = 'CLIENT01'", launchScript, StringComparison.Ordinal);
        Assert.Contains("$targetHost = 'CLIENT01'", launchScript, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LaunchAsync_UsesInvokeCommandForRemotingWindowScript()
    {
        ProcessStartInfo? capturedStartInfo = null;
        var launcher = new PowerShellScriptLauncher(startInfo =>
        {
            capturedStartInfo = startInfo;
            return Process.GetCurrentProcess();
        });

        var script = new PowerShellScriptCatalogEntry(
            "Inventory/Remote.ps1",
            "Remote",
            "Inventory/Remote.ps1",
            @"C:\Scripts\Remote.ps1",
            PowerShellScriptExecutionMode.RemotingWindow,
            []);

        var result = await launcher.LaunchAsync(
            "RQXD002",
            script,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            executor: null,
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.NotNull(capturedStartInfo);

        var launchScript = DecodeLaunchScript(capturedStartInfo!.Arguments);
        Assert.Contains("Invoke-Command -ComputerName $targetHost -FilePath $scriptPath -ErrorAction Stop *>&1 | Out-Host", launchScript, StringComparison.Ordinal);
        Assert.Contains("$isLocalHost = $false", launchScript, StringComparison.Ordinal);
        Assert.Contains("$targetHost = 'RQXD002'", launchScript, StringComparison.Ordinal);
    }

    private static string DecodeLaunchScript(string arguments)
    {
        const string marker = "-EncodedCommand ";
        var index = arguments.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(index >= 0, "Expected -EncodedCommand in PowerShell arguments.");

        var encodedCommand = arguments[(index + marker.Length)..].Trim();
        return Encoding.Unicode.GetString(Convert.FromBase64String(encodedCommand));
    }
}
