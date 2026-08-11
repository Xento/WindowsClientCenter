using System.Text.Json;
using WindowsClientCenter.Intune.Services.Contracts;
using WindowsClientCenter.Intune.Services.Models;

namespace WindowsClientCenter.Intune.Services.Runtime;

internal sealed class InstalledSoftwareManager(IPowerShellExecutor executor) : IInstalledSoftwareManager
{
    public async ValueTask<InstalledSoftwareSnapshot> GetInstalledSoftwareAsync(string host, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(host))
        {
            return new InstalledSoftwareSnapshot(string.Empty, false, [], ["No host was provided."]);
        }

        var normalizedHost = host.Trim();
        try
        {
            var execution = await executor.ExecuteForHostAsync(normalizedHost, BuildGetInstalledSoftwareScriptBody(), cancellationToken);
            if (execution.ExitCode != 0)
            {
                return new InstalledSoftwareSnapshot(
                    normalizedHost,
                    LocalPowerShellExecutor.IsLocalHost(normalizedHost),
                    [],
                    [NormalizeError(execution)]);
            }

            var payload = JsonSerializer.Deserialize<InstalledSoftwarePayload>(
                string.IsNullOrWhiteSpace(execution.StdOut) ? "{}" : execution.StdOut,
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

            var entries = (payload?.Entries ?? [])
                .Select(MapEntry)
                .Where(static entry => !string.IsNullOrWhiteSpace(entry.Name))
                .OrderBy(static entry => entry.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static entry => entry.Version, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static entry => entry.Publisher, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return new InstalledSoftwareSnapshot(
                normalizedHost,
                LocalPowerShellExecutor.IsLocalHost(normalizedHost),
                entries,
                payload?.Warnings?.Where(static warning => !string.IsNullOrWhiteSpace(warning)).ToArray() ?? []);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new InstalledSoftwareSnapshot(
                normalizedHost,
                LocalPowerShellExecutor.IsLocalHost(normalizedHost),
                [],
                [ex.Message]);
        }
    }

    public ValueTask<DeviceActionResult> RepairMsiAsync(string host, string softwareCode, CancellationToken cancellationToken)
    {
        return ExecuteMsiActionAsync(host, softwareCode, BuildRepairMsiScriptBody(softwareCode), "repair", cancellationToken);
    }

    public ValueTask<DeviceActionResult> UninstallMsiAsync(string host, string softwareCode, CancellationToken cancellationToken)
    {
        return ExecuteMsiActionAsync(host, softwareCode, BuildUninstallMsiScriptBody(softwareCode), "uninstall", cancellationToken);
    }

    public async ValueTask<DeviceActionResult> UninstallQuietAsync(
        string host,
        string quietUninstallString,
        string softwareIdentity,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(host))
        {
            return DeviceActionResult.Fail("No host was provided.", "no_host");
        }

        if (string.IsNullOrWhiteSpace(quietUninstallString))
        {
            return DeviceActionResult.Fail("No quiet uninstall command was provided.", "no_quiet_uninstall");
        }

        var normalizedHost = host.Trim();
        var normalizedIdentity = string.IsNullOrWhiteSpace(softwareIdentity) ? "selected software" : softwareIdentity.Trim();
        var execution = await executor.ExecuteForHostAsync(normalizedHost, BuildQuietUninstallScriptBody(quietUninstallString), cancellationToken);
        return execution.ExitCode == 0
            ? DeviceActionResult.Ok(string.IsNullOrWhiteSpace(execution.StdOut)
                ? $"Quiet uninstall completed for '{normalizedIdentity}' on '{normalizedHost}'."
                : execution.StdOut.Trim())
            : DeviceActionResult.Fail(
                $"Quiet uninstall failed for '{normalizedIdentity}' on '{normalizedHost}': {NormalizeError(execution)}",
                "quiet_uninstall_failed");
    }

    public async ValueTask<DeviceActionResult> ForceRemoveRegistryEntryAsync(
        string host,
        InstalledSoftwareEntry software,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(host))
        {
            return DeviceActionResult.Fail("No host was provided.", "no_host");
        }

        if (!software.CanForceRemoveRegistryEntry)
        {
            return DeviceActionResult.Fail("The selected software does not expose a removable registry identity.", "no_registry_identity");
        }

        var normalizedHost = host.Trim();
        var execution = await executor.ExecuteForHostAsync(
            normalizedHost,
            BuildForceRemoveRegistryEntryScriptBody(software),
            cancellationToken);
        var identity = string.IsNullOrWhiteSpace(software.Name) ? software.Id : software.Name;
        return execution.ExitCode == 0
            ? DeviceActionResult.Ok(string.IsNullOrWhiteSpace(execution.StdOut)
                ? $"Registry entry removal completed for '{identity}' on '{normalizedHost}'."
                : execution.StdOut.Trim())
            : DeviceActionResult.Fail(
                $"Registry entry removal failed for '{identity}' on '{normalizedHost}': {NormalizeError(execution)}",
                "registry_entry_remove_failed");
    }

    private async ValueTask<DeviceActionResult> ExecuteMsiActionAsync(
        string host,
        string softwareCode,
        string script,
        string actionName,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(host))
        {
            return DeviceActionResult.Fail("No host was provided.", "no_host");
        }

        if (!InstalledSoftwareEntryHelpers.IsMsiProductCode(softwareCode))
        {
            return DeviceActionResult.Fail("The selected software does not have a valid MSI product code.", "invalid_msi_product_code");
        }

        var normalizedHost = host.Trim();
        var normalizedSoftwareCode = softwareCode.Trim();
        var execution = await executor.ExecuteForHostAsync(normalizedHost, script, cancellationToken);
        return execution.ExitCode == 0
            ? DeviceActionResult.Ok(string.IsNullOrWhiteSpace(execution.StdOut)
                ? $"MSI {actionName} completed for '{normalizedSoftwareCode}' on '{normalizedHost}'."
                : execution.StdOut.Trim())
            : DeviceActionResult.Fail(
                $"MSI {actionName} failed for '{normalizedSoftwareCode}' on '{normalizedHost}': {NormalizeError(execution)}",
                $"msi_{actionName}_failed");
    }

    internal static string BuildGetInstalledSoftwareScriptBody()
    {
        return
            BuildInstalledSoftwareInventoryFunctions() +
            "$warnings = New-Object System.Collections.Generic.List[string];" +
            "$entries = @();" +
            "try {" +
            "  $smsClass = Get-CimClass -Namespace 'root\\CIMV2\\sms' -ClassName 'SMS_InstalledSoftware' -ErrorAction Stop;" +
            "  $entries = @(Get-CimInstance -Namespace 'root\\CIMV2\\sms' -ClassName 'SMS_InstalledSoftware' -ErrorAction Stop | ForEach-Object { ConvertFrom-SmsInstalledSoftware -Item $_ });" +
            "} catch {" +
            "  $warnings.Add('SMS_InstalledSoftware inventory is unavailable: ' + $_.Exception.Message) | Out-Null;" +
            "  $entries = @();" +
            "};" +
            "if ($entries.Count -eq 0) {" +
            "  try {" +
            "    $entries = @(Get-RegistryInstalledSoftware);" +
            "  } catch {" +
            "    $warnings.Add('Registry uninstall inventory is unavailable: ' + $_.Exception.Message) | Out-Null;" +
            "    $entries = @();" +
            "  };" +
            "};" +
            "$payload = [pscustomobject]@{ Entries = @($entries | Sort-Object Name, Version, Publisher); Warnings = @($warnings) };" +
            "$payload | ConvertTo-Json -Depth 6 -Compress;";
    }

    internal static string BuildRepairMsiScriptBody(string softwareCode)
    {
        return BuildMsiActionScriptBody(
            softwareCode,
            "repair",
            "@('/fpecmsu', $productCode, 'REBOOT=ReallySuppress', 'REINSTALL=ALL', '/q')");
    }

    internal static string BuildUninstallMsiScriptBody(string softwareCode)
    {
        return BuildMsiActionScriptBody(
            softwareCode,
            "uninstall",
            "@('/x', $productCode, 'REBOOT=ReallySuppress', '/q')");
    }

    internal static string BuildQuietUninstallScriptBody(string quietUninstallString)
    {
        return
            $"$commandLine='{EscapePowerShellSingleQuotedString(quietUninstallString)}';" +
            "if ([string]::IsNullOrWhiteSpace($commandLine)) { throw 'Quiet uninstall command is empty.' };" +
            "$process = Start-Process -FilePath 'cmd.exe' -ArgumentList @('/d','/s','/c',$commandLine) -WindowStyle Hidden -Wait -PassThru;" +
            "if ($process.ExitCode -ne 0) { throw ('Quiet uninstall command failed. ExitCode=' + $process.ExitCode) };" +
            "Write-Output ('Quiet uninstall command completed. ExitCode=' + $process.ExitCode + '.');";
    }

    internal static string BuildForceRemoveRegistryEntryScriptBody(InstalledSoftwareEntry software)
    {
        var packedProductCode = InstalledSoftwareEntryHelpers.IsMsiProductCode(software.EffectiveProductCode)
            ? ConvertToPackedMsiProductCode(software.EffectiveProductCode)
            : string.Empty;

        return
            BuildRegistryRemovalFunctions() +
            $"$softwareId='{EscapePowerShellSingleQuotedString(software.Id)}';" +
            $"$softwareCode='{EscapePowerShellSingleQuotedString(software.SoftwareCode)}';" +
            $"$productCode='{EscapePowerShellSingleQuotedString(software.EffectiveProductCode)}';" +
            $"$packedProductCode='{EscapePowerShellSingleQuotedString(packedProductCode)}';" +
            $"$displayName='{EscapePowerShellSingleQuotedString(software.Name)}';" +
            "$removed = New-Object System.Collections.Generic.List[string];" +
            "$candidates = New-Object System.Collections.Generic.List[string];" +
            "foreach ($candidate in @($softwareCode, $productCode)) {" +
            "  if (-not [string]::IsNullOrWhiteSpace($candidate) -and -not $candidates.Contains($candidate)) { $candidates.Add($candidate) | Out-Null };" +
            "};" +
            "if ($softwareId -like 'registry|*') {" +
            "  $parts = $softwareId -split '\\|', 3;" +
            "  if ($parts.Length -eq 3 -and -not [string]::IsNullOrWhiteSpace($parts[2]) -and -not $candidates.Contains($parts[2])) { $candidates.Add($parts[2]) | Out-Null };" +
            "};" +
            "$uninstallRoots = @('HKLM:\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall','HKLM:\\SOFTWARE\\Wow6432Node\\Microsoft\\Windows\\CurrentVersion\\Uninstall');" +
            "foreach ($root in $uninstallRoots) {" +
            "  foreach ($candidate in $candidates) {" +
            "    Remove-IccRegistryKeyIfExists -Path ($root + '\\' + $candidate) -Removed $removed;" +
            "  };" +
            "};" +
            "if (-not [string]::IsNullOrWhiteSpace($packedProductCode)) {" +
            "  $installerProductRoots = @(" +
            "    'HKLM:\\SOFTWARE\\Classes\\Installer\\Products'," +
            "    'HKCR:\\Installer\\Products'" +
            "  );" +
            "  foreach ($root in $installerProductRoots) {" +
            "    Remove-IccRegistryKeyIfExists -Path ($root + '\\' + $packedProductCode) -Removed $removed;" +
            "  };" +
            "  $userDataRoot = 'HKLM:\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Installer\\UserData';" +
            "  if (Test-Path -LiteralPath $userDataRoot) {" +
            "    foreach ($sidKey in @(Get-ChildItem -LiteralPath $userDataRoot -ErrorAction SilentlyContinue)) {" +
            "      Remove-IccRegistryKeyIfExists -Path ($sidKey.PSPath + '\\Products\\' + $packedProductCode) -Removed $removed;" +
            "    };" +
            "  };" +
            "};" +
            "if ($removed.Count -eq 0) { throw ('No matching registry keys were found for ' + $displayName + '.') };" +
            "Write-Output ('Force removed ' + $removed.Count + ' registry key(s) for ' + $displayName + ': ' + ($removed -join '; '));";
    }

    private static string BuildMsiActionScriptBody(string softwareCode, string actionName, string argumentsExpression)
    {
        return
            $"$productCode='{EscapePowerShellSingleQuotedString(softwareCode)}';" +
            "if ($productCode -notmatch '^\\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\\}$') { throw 'Invalid MSI product code.' };" +
            $"$arguments = {argumentsExpression};" +
            "$process = Start-Process -FilePath 'msiexec.exe' -ArgumentList $arguments -WindowStyle Hidden -Wait -PassThru;" +
            "if ($process.ExitCode -ne 0 -and $process.ExitCode -ne 3010) { throw ('msiexec failed. ExitCode=' + $process.ExitCode) };" +
            $"Write-Output ('MSI {actionName} completed for ' + $productCode + '. ExitCode=' + $process.ExitCode + '.');";
    }

    private static string BuildInstalledSoftwareInventoryFunctions()
    {
        return
            "function Get-IccPropertyValue {" +
            "  param($Item, [string]$Name);" +
            "  if ($null -eq $Item) { return '' };" +
            "  $property = $Item.PSObject.Properties[$Name];" +
            "  if ($null -eq $property -or $null -eq $property.Value) { return '' };" +
            "  return [string]$property.Value;" +
            "};" +
            "function Get-IccMsiProductCode {" +
            "  param([string]$SoftwareCode, [string]$ProductCode, [string]$UninstallString, [string]$KeyName);" +
            "  foreach ($candidate in @($ProductCode, $SoftwareCode, $KeyName)) {" +
            "    if (-not [string]::IsNullOrWhiteSpace($candidate) -and $candidate -match '^\\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\\}$') { return $candidate };" +
            "  };" +
            "  if (-not [string]::IsNullOrWhiteSpace($UninstallString) -and $UninstallString -match '\\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\\}') { return $Matches[0] };" +
            "  return '';" +
            "};" +
            "function ConvertFrom-SmsInstalledSoftware {" +
            "  param($Item);" +
            "  $name = Get-IccPropertyValue $Item 'ARPDisplayName';" +
            "  if ([string]::IsNullOrWhiteSpace($name)) { $name = Get-IccPropertyValue $Item 'ProductName' };" +
            "  $softwareCode = Get-IccPropertyValue $Item 'SoftwareCode';" +
            "  $productCode = Get-IccMsiProductCode $softwareCode (Get-IccPropertyValue $Item 'ProductID') (Get-IccPropertyValue $Item 'UninstallString') '';" +
            "  [pscustomobject]@{" +
            "    Id = 'sms|' + $softwareCode + '|' + $name;" +
            "    Name = $name;" +
            "    Version = Get-IccPropertyValue $Item 'ProductVersion';" +
            "    Publisher = Get-IccPropertyValue $Item 'Publisher';" +
            "    InstallDate = Get-IccPropertyValue $Item 'InstallDate';" +
            "    InstallLocation = Get-IccPropertyValue $Item 'InstalledLocation';" +
            "    InstallSource = Get-IccPropertyValue $Item 'InstallSource';" +
            "    SoftwareCode = $softwareCode;" +
            "    ProductCode = $productCode;" +
            "    UninstallString = Get-IccPropertyValue $Item 'UninstallString';" +
            "    QuietUninstallString = Get-IccPropertyValue $Item 'QuietUninstallString';" +
            "    Source = 'SMS_InstalledSoftware';" +
            "    Architecture = Get-IccPropertyValue $Item 'InstallType'" +
            "  };" +
            "};" +
            "function Get-RegistryInstalledSoftware {" +
            "  $roots = @(" +
            "    [pscustomobject]@{ Path='HKLM:\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall'; Architecture='x64' }," +
            "    [pscustomobject]@{ Path='HKLM:\\SOFTWARE\\Wow6432Node\\Microsoft\\Windows\\CurrentVersion\\Uninstall'; Architecture='x86' }" +
            "  );" +
            "  foreach ($root in $roots) {" +
            "    if (-not (Test-Path -LiteralPath $root.Path)) { continue };" +
            "    foreach ($key in @(Get-ChildItem -LiteralPath $root.Path -ErrorAction Stop)) {" +
            "      $item = Get-ItemProperty -LiteralPath $key.PSPath -ErrorAction SilentlyContinue;" +
            "      if ($null -eq $item -or [string]::IsNullOrWhiteSpace([string]$item.DisplayName)) { continue };" +
            "      $softwareCode = [string]$key.PSChildName;" +
            "      $productCode = Get-IccMsiProductCode $softwareCode '' ([string]$item.UninstallString) ([string]$key.PSChildName);" +
            "      [pscustomobject]@{" +
            "        Id = 'registry|' + $root.Architecture + '|' + [string]$key.PSChildName;" +
            "        Name = [string]$item.DisplayName;" +
            "        Version = [string]$item.DisplayVersion;" +
            "        Publisher = [string]$item.Publisher;" +
            "        InstallDate = [string]$item.InstallDate;" +
            "        InstallLocation = [string]$item.InstallLocation;" +
            "        InstallSource = [string]$item.InstallSource;" +
            "        SoftwareCode = $softwareCode;" +
            "        ProductCode = $productCode;" +
            "        UninstallString = [string]$item.UninstallString;" +
            "        QuietUninstallString = [string]$item.QuietUninstallString;" +
            "        Source = 'Registry';" +
            "        Architecture = $root.Architecture" +
            "      };" +
            "    };" +
            "  };" +
            "};";
    }

    private static string BuildRegistryRemovalFunctions()
    {
        return
            "function Remove-IccRegistryKeyIfExists {" +
            "  param([Parameter(Mandatory=$true)][string]$Path, [Parameter(Mandatory=$true)]$Removed);" +
            "  if (Test-Path -LiteralPath $Path) {" +
            "    Remove-Item -LiteralPath $Path -Recurse -Force -ErrorAction Stop;" +
            "    $Removed.Add($Path) | Out-Null;" +
            "  };" +
            "};";
    }

    internal static string ConvertToPackedMsiProductCode(string productCode)
    {
        if (!InstalledSoftwareEntryHelpers.IsMsiProductCode(productCode))
        {
            return string.Empty;
        }

        var raw = productCode.Trim()[1..^1].Replace("-", string.Empty, StringComparison.Ordinal).ToUpperInvariant();
        return Reverse(raw[..8]) +
               Reverse(raw.Substring(8, 4)) +
               Reverse(raw.Substring(12, 4)) +
               SwapPairs(raw.Substring(16, 16));
    }

    private static string Reverse(string value)
    {
        return new string(value.Reverse().ToArray());
    }

    private static string SwapPairs(string value)
    {
        var result = new char[value.Length];
        for (var index = 0; index < value.Length; index += 2)
        {
            result[index] = value[index + 1];
            result[index + 1] = value[index];
        }

        return new string(result);
    }

    private static InstalledSoftwareEntry MapEntry(InstalledSoftwarePayloadItem item)
    {
        var softwareCode = item.SoftwareCode ?? string.Empty;
        var productCode = item.ProductCode ?? string.Empty;
        var effectiveProductCode = InstalledSoftwareEntryHelpers.IsMsiProductCode(productCode)
            ? productCode
            : InstalledSoftwareEntryHelpers.IsMsiProductCode(softwareCode)
                ? softwareCode
                : string.Empty;

        return new InstalledSoftwareEntry(
            item.Id ?? BuildFallbackId(item),
            item.Name ?? string.Empty,
            item.Version ?? string.Empty,
            item.Publisher ?? string.Empty,
            item.InstallDate ?? string.Empty,
            item.InstallLocation ?? string.Empty,
            item.InstallSource ?? string.Empty,
            softwareCode,
            effectiveProductCode,
            item.UninstallString ?? string.Empty,
            item.QuietUninstallString ?? string.Empty,
            item.Source ?? string.Empty,
            item.Architecture ?? string.Empty);
    }

    private static string BuildFallbackId(InstalledSoftwarePayloadItem item)
    {
        return string.Join("|", item.Source, item.Name, item.Version, item.Publisher);
    }

    private static string EscapePowerShellSingleQuotedString(string value)
    {
        return string.IsNullOrEmpty(value)
            ? string.Empty
            : value.Replace("'", "''", StringComparison.Ordinal);
    }

    private static string NormalizeError(PowershellExecutionResult execution)
    {
        return string.IsNullOrWhiteSpace(execution.StdErr)
            ? string.IsNullOrWhiteSpace(execution.StdOut) ? "Unknown error." : execution.StdOut.Trim()
            : execution.StdErr.Trim();
    }

    private sealed class InstalledSoftwarePayload
    {
        public InstalledSoftwarePayloadItem[]? Entries { get; set; }
        public string[]? Warnings { get; set; }
    }

    private sealed class InstalledSoftwarePayloadItem
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? Version { get; set; }
        public string? Publisher { get; set; }
        public string? InstallDate { get; set; }
        public string? InstallLocation { get; set; }
        public string? InstallSource { get; set; }
        public string? SoftwareCode { get; set; }
        public string? ProductCode { get; set; }
        public string? UninstallString { get; set; }
        public string? QuietUninstallString { get; set; }
        public string? Source { get; set; }
        public string? Architecture { get; set; }
    }
}
