using System.Diagnostics;
using System.IO;
using System.Text;
using WindowsClientCenter.Intune.Services.Runtime;
using WindowsClientCenter.Plugin.Abstractions.Models;

namespace WindowsClientCenter.Plugins.PowerShellScripts;

public sealed class PowerShellScriptLauncher : IPowerShellScriptLauncher
{
    private readonly Func<ProcessStartInfo, Process?> _processStarter;

    public PowerShellScriptLauncher()
        : this(static startInfo => Process.Start(startInfo))
    {
    }

    public PowerShellScriptLauncher(Func<ProcessStartInfo, Process?> processStarter)
    {
        _processStarter = processStarter ?? throw new ArgumentNullException(nameof(processStarter));
    }

    public ValueTask<PluginActionResult> LaunchAsync(
        string host,
        PowerShellScriptCatalogEntry script,
        IReadOnlyDictionary<string, string> parameterLiterals,
        IPowerShellExecutor? executor,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            return ValueTask.FromResult(PluginActionResult.Fail(
                "PowerShell script launch is only supported on Windows hosts.",
                "unsupported_os"));
        }

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var startInfo = BuildStartInfo(host, script, parameterLiterals);
            var process = _processStarter(startInfo);
            if (process is null)
            {
                return ValueTask.FromResult(PluginActionResult.Fail(
                    $"Failed to execute script '{script.DisplayName}': PowerShell process could not be started.",
                    "launch_failed"));
            }

            return ValueTask.FromResult(PluginActionResult.Ok(
                $"Started script '{script.DisplayName}' for host '{host}' in a PowerShell window."));
        }
        catch (Exception ex)
        {
            return ValueTask.FromResult(PluginActionResult.Fail(
                $"Failed to execute script '{script.DisplayName}': {ex.Message}",
                "launch_failed"));
        }
    }

    private static ProcessStartInfo BuildStartInfo(
        string host,
        PowerShellScriptCatalogEntry script,
        IReadOnlyDictionary<string, string> parameterLiterals)
    {
        var launchScript = BuildLaunchScript(host, script, parameterLiterals);
        var encodedCommand = Convert.ToBase64String(Encoding.Unicode.GetBytes(launchScript));

        return new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoLogo -NoExit -ExecutionPolicy Bypass -EncodedCommand {encodedCommand}",
            WorkingDirectory = Path.GetDirectoryName(script.FullPath) ?? Environment.CurrentDirectory,
            UseShellExecute = true
        };
    }

    private static string BuildLaunchScript(
        string host,
        PowerShellScriptCatalogEntry script,
        IReadOnlyDictionary<string, string> parameterLiterals)
    {
        var builder = new StringBuilder()
            .AppendLine("$ErrorActionPreference = 'Stop'")
            .Append("$scriptPath = ")
            .Append(PowerShellScriptLiteralBuilder.CreateStringLiteral(script.FullPath))
            .AppendLine()
            .Append("$targetHost = ")
            .Append(PowerShellScriptLiteralBuilder.CreateStringLiteral(host))
            .AppendLine()
            .Append("$isLocalHost = ")
            .Append(IsLocalHost(host) ? "$true" : "$false")
            .AppendLine()
            .AppendLine("$params = [ordered]@{}");

        foreach (var parameter in parameterLiterals.OrderBy(static entry => entry.Key, StringComparer.OrdinalIgnoreCase))
        {
            builder.Append("$params[")
                .Append(PowerShellScriptLiteralBuilder.CreateStringLiteral(parameter.Key))
                .Append("] = ")
                .Append(parameter.Value)
                .AppendLine();
        }

        builder
            .AppendLine()
            .AppendLine("Write-Host (\"Starting script '{0}' for host '{1}'.\" -f [System.IO.Path]::GetFileNameWithoutExtension($scriptPath), $targetHost) -ForegroundColor Cyan")
            .AppendLine("Write-Host (\"Script path: {0}\" -f $scriptPath) -ForegroundColor DarkGray")
            .AppendLine()
            .AppendLine("try {");

        if (script.ExecutionMode == PowerShellScriptExecutionMode.RemotingWindow)
        {
            builder
                .AppendLine("  if ($isLocalHost) {")
                .AppendLine("    & $scriptPath *>&1 | Out-Host")
                .AppendLine("  } else {")
                .AppendLine("    Invoke-Command -ComputerName $targetHost -FilePath $scriptPath -ErrorAction Stop *>&1 | Out-Host")
                .AppendLine("  }");
        }
        else
        {
            builder.AppendLine("  & $scriptPath @params *>&1 | Out-Host");
        }

        builder
            .AppendLine("  Write-Host ''")
            .AppendLine("  Write-Host 'Script finished.' -ForegroundColor Green")
            .AppendLine("} catch {")
            .AppendLine("  Write-Host ''")
            .AppendLine("  Write-Host ('Script failed: ' + $_.Exception.Message) -ForegroundColor Red")
            .AppendLine("  Write-Host $_ -ForegroundColor Red")
            .AppendLine("}");

        return builder.ToString();
    }

    private static bool IsLocalHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        return host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
               host.Equals(".", StringComparison.OrdinalIgnoreCase) ||
               host.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase);
    }
}
