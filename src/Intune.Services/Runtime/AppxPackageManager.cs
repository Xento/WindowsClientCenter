using System.Text.Json;
using WindowsClientCenter.Intune.Services.Contracts;
using WindowsClientCenter.Intune.Services.Models;

namespace WindowsClientCenter.Intune.Services.Runtime;

public sealed class AppxPackageManager(IPowerShellExecutor executor) : IAppxPackageManager
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public async ValueTask<AppxPackageSnapshot> GetPackagesAsync(string host, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalizedHost = NormalizeHost(host);
        if (normalizedHost.Length == 0)
        {
            return new AppxPackageSnapshot(string.Empty, string.Empty, string.Empty, [], ["No host was provided."]);
        }

        try
        {
            var execution = await executor.ExecuteForHostAsync(normalizedHost, BuildInventoryScriptBody(), cancellationToken);
            if (execution.ExitCode != 0)
            {
                return new AppxPackageSnapshot(normalizedHost, string.Empty, string.Empty, [], [NormalizeError(execution)]);
            }

            var payload = JsonSerializer.Deserialize<AppxInventoryPayload>(EmptyJsonFallback(execution.StdOut), JsonOptions);
            var packages = (payload?.Packages ?? [])
                .Select(MapPackage)
                .Where(static package => !string.IsNullOrWhiteSpace(package.PackageFullName))
                .OrderBy(static package => package.EffectiveDisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static package => package.Version, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return new AppxPackageSnapshot(
                normalizedHost,
                payload?.ActiveUserName?.Trim() ?? string.Empty,
                payload?.ActiveUserSid?.Trim() ?? string.Empty,
                packages,
                NormalizeWarnings(payload?.Warnings));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new AppxPackageSnapshot(normalizedHost, string.Empty, string.Empty, [], [ex.Message]);
        }
    }

    public async ValueTask<WingetSearchSnapshot> SearchWingetAsync(string host, string query, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalizedHost = NormalizeHost(host);
        var normalizedQuery = query?.Trim() ?? string.Empty;
        if (normalizedHost.Length == 0 || normalizedQuery.Length < 2)
        {
            return new WingetSearchSnapshot([], [normalizedHost.Length == 0 ? "No host was provided." : "Enter at least two characters to search WinGet."]);
        }

        try
        {
            var execution = await executor.ExecuteForHostAsync(normalizedHost, BuildWingetSearchScriptBody(normalizedQuery), cancellationToken);
            if (execution.ExitCode != 0)
            {
                return new WingetSearchSnapshot([], [NormalizeError(execution)]);
            }

            var payload = JsonSerializer.Deserialize<WingetSearchPayload>(EmptyJsonFallback(execution.StdOut), JsonOptions);
            var entries = (payload?.Entries ?? [])
                .Where(static entry => !string.IsNullOrWhiteSpace(entry.Id) && !string.IsNullOrWhiteSpace(entry.Source))
                .Select(static entry => new WingetCatalogEntry(
                    entry.Id.Trim(),
                    entry.Name?.Trim() ?? string.Empty,
                    entry.Version?.Trim() ?? string.Empty,
                    entry.Source.Trim()))
                .GroupBy(static entry => $"{entry.Source}|{entry.Id}", StringComparer.OrdinalIgnoreCase)
                .Select(static group => group.First())
                .OrderBy(static entry => entry.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static entry => entry.Id, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return new WingetSearchSnapshot(entries, NormalizeWarnings(payload?.Warnings));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new WingetSearchSnapshot([], [ex.Message]);
        }
    }

    public ValueTask<DeviceActionResult> InstallWingetAsync(string host, WingetCatalogEntry package, WingetInstallScope scope, CancellationToken cancellationToken) =>
        ExecuteWingetAsync("install", host, package, scope, cancellationToken);

    public ValueTask<DeviceActionResult> UpgradeWingetAsync(string host, WingetCatalogEntry package, WingetInstallScope scope, CancellationToken cancellationToken) =>
        ExecuteWingetAsync("upgrade", host, package, scope, cancellationToken);

    public ValueTask<DeviceActionResult> RemoveForUserAsync(string host, string packageFullName, string userSid, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(packageFullName))
        {
            return MissingPackageIdentity();
        }

        if (string.IsNullOrWhiteSpace(userSid) || !userSid.Trim().StartsWith("S-", StringComparison.OrdinalIgnoreCase))
        {
            return ValueTask.FromResult(DeviceActionResult.Fail("The selected user does not expose a valid SID.", "invalid_user_sid"));
        }

        return ExecuteActionAsync(
            host,
            BuildRemoveForUserScriptBody(packageFullName, userSid),
            $"Removed '{packageFullName}' for user '{userSid}'.",
            cancellationToken);
    }

    public ValueTask<DeviceActionResult> RemoveForAllUsersAsync(string host, string packageFullName, CancellationToken cancellationToken) =>
        string.IsNullOrWhiteSpace(packageFullName)
            ? MissingPackageIdentity()
            : ExecuteActionAsync(host, BuildRemoveForAllUsersScriptBody(packageFullName), $"Removed '{packageFullName}' for all users.", cancellationToken);

    public ValueTask<DeviceActionResult> RemoveProvisioningAsync(string host, string provisionedPackageName, CancellationToken cancellationToken) =>
        string.IsNullOrWhiteSpace(provisionedPackageName)
            ? MissingPackageIdentity()
            : ExecuteActionAsync(host, BuildRemoveProvisioningScriptBody(provisionedPackageName), $"Removed provisioning for '{provisionedPackageName}'.", cancellationToken);

    public ValueTask<DeviceActionResult> RegisterForActiveUserAsync(string host, string packageFullName, CancellationToken cancellationToken) =>
        string.IsNullOrWhiteSpace(packageFullName)
            ? MissingPackageIdentity()
            : ExecuteActionAsync(host, BuildRegisterForActiveUserScriptBody(packageFullName), $"Registered '{packageFullName}' for the active user.", cancellationToken);

    internal static string BuildInventoryScriptBody() =>
        "Set-StrictMode -Version Latest; $ErrorActionPreference='Stop';" +
        "$warnings=[System.Collections.Generic.List[string]]::new();" +
        "$activeUserName=''; $activeUserSid='';" +
        "try { $activeUserName=[string](Get-CimInstance Win32_ComputerSystem -ErrorAction Stop).UserName;" +
        " if (-not [string]::IsNullOrWhiteSpace($activeUserName)) { $activeUserSid=([System.Security.Principal.NTAccount]$activeUserName).Translate([System.Security.Principal.SecurityIdentifier]).Value }" +
        "} catch { $warnings.Add('Active user could not be resolved: ' + $_.Exception.Message) };" +
        "$provisioned=@{}; try { foreach($item in @(Get-AppxProvisionedPackage -Online -ErrorAction Stop)) { if(-not [string]::IsNullOrWhiteSpace([string]$item.DisplayName)) { $provisioned[[string]$item.DisplayName]=$item } } } catch { $warnings.Add('Provisioned packages could not be read: ' + $_.Exception.Message) };" +
        "$packages=@(); foreach($package in @(Get-AppxPackage -AllUsers -PackageTypeFilter Main,Framework,Resource,Bundle,Optional -ErrorAction Stop)) {" +
        " $users=@(); foreach($registration in @($package.PackageUserInformation)) { $sid=[string]$registration.UserSecurityId; $userName=$sid; try { if(-not [string]::IsNullOrWhiteSpace($sid)) { $userName=([System.Security.Principal.SecurityIdentifier]$sid).Translate([System.Security.Principal.NTAccount]).Value } } catch {};" +
        "  $users += [pscustomobject]@{ UserSid=$sid; UserName=$userName; InstallState=[string]$registration.InstallState; IsActiveUser=($sid -eq $activeUserSid) } };" +
        " $provisionedItem=$null; if($provisioned.ContainsKey([string]$package.Name)) { $provisionedItem=$provisioned[[string]$package.Name] };" +
        " $packages += [pscustomobject]@{ PackageFullName=[string]$package.PackageFullName; PackageFamilyName=[string]$package.PackageFamilyName; Name=[string]$package.Name; DisplayName=[string]$package.Name; Version=[string]$package.Version; Publisher=[string]$package.Publisher; Architecture=[string]$package.Architecture; InstallLocation=[string]$package.InstallLocation; IsFramework=[bool]$package.IsFramework; IsResourcePackage=[bool]$package.IsResourcePackage; IsBundle=([string]$package.PackageFullName -match '_neutral_~_'); IsOptional=[bool]$package.IsOptional; NonRemovable=[bool]$package.NonRemovable; IsProvisioned=($null -ne $provisionedItem); ProvisionedPackageName=$(if($null -ne $provisionedItem){[string]$provisionedItem.PackageName}else{''}); Users=$users } };" +
        "$knownNames=@($packages | ForEach-Object Name); foreach($item in $provisioned.Values) { if($knownNames -notcontains [string]$item.DisplayName) { $packages += [pscustomobject]@{ PackageFullName=[string]$item.PackageName; PackageFamilyName=''; Name=[string]$item.DisplayName; DisplayName=[string]$item.DisplayName; Version=[string]$item.Version; Publisher=''; Architecture=[string]$item.Architecture; InstallLocation=''; IsFramework=$false; IsResourcePackage=$false; IsBundle=([string]$item.PackageName -match '_neutral_~_'); IsOptional=$false; NonRemovable=$false; IsProvisioned=$true; ProvisionedPackageName=[string]$item.PackageName; Users=@() } } };" +
        "[pscustomobject]@{ ActiveUserName=$activeUserName; ActiveUserSid=$activeUserSid; Packages=$packages; Warnings=$warnings } | ConvertTo-Json -Depth 7 -Compress";

    internal static string BuildWingetSearchScriptBody(string query) =>
        "Set-StrictMode -Version Latest; $ErrorActionPreference='Stop'; $query=" + ToPowerShellLiteral(query) + ";" +
        GetWingetPathFunction() +
        "$winget=Get-IccWingetPath; $entries=[System.Collections.Generic.List[object]]::new(); $warnings=[System.Collections.Generic.List[string]]::new();" +
        "foreach($source in @('winget','msstore')) { try { $lines=@(& $winget search --query $query --source $source --count 50 --accept-source-agreements --disable-interactivity 2>&1); if($LASTEXITCODE -ne 0){throw ($lines -join ' ')};" +
        " $separator=-1; for($i=0;$i -lt $lines.Count;$i++){if([string]$lines[$i] -match '^-{2,}\\s+-{2,}'){ $separator=$i; break }}; if($separator -lt 0){continue};" +
        " $spans=@([regex]::Matches([string]$lines[$separator],'-+') | ForEach-Object { [pscustomobject]@{Start=$_.Index;Length=$_.Length} }); if($spans.Count -lt 2){continue};" +
        " for($i=$separator+1;$i -lt $lines.Count;$i++){ $line=[string]$lines[$i]; if([string]::IsNullOrWhiteSpace($line)){continue}; $values=@(); foreach($span in $spans){ if($line.Length -le $span.Start){$values += ''}else{$length=[Math]::Min($span.Length,$line.Length-$span.Start);$values += $line.Substring($span.Start,$length).Trim()} }; if($values.Count -ge 2 -and -not [string]::IsNullOrWhiteSpace($values[1])){ $entries.Add([pscustomobject]@{Name=$values[0];Id=$values[1];Version=$(if($values.Count -ge 3){$values[2]}else{''});Source=$source}) } }" +
        " } catch { $warnings.Add($source + ' search failed: ' + $_.Exception.Message) } };" +
        "[pscustomobject]@{Entries=$entries;Warnings=$warnings} | ConvertTo-Json -Depth 5 -Compress";

    internal static string BuildRemoveForUserScriptBody(string packageFullName, string userSid) =>
        BuildExactPackageLookup(packageFullName) + "$package | Remove-AppxPackage -User " + ToPowerShellLiteral(userSid) + " -ErrorAction Stop; 'Removed.'";

    internal static string BuildRemoveForAllUsersScriptBody(string packageFullName) =>
        BuildExactPackageLookup(packageFullName) + "$package | Remove-AppxPackage -AllUsers -ErrorAction Stop; 'Removed.'";

    internal static string BuildRemoveProvisioningScriptBody(string provisionedPackageName) =>
        "Set-StrictMode -Version Latest; $ErrorActionPreference='Stop'; $name=" + ToPowerShellLiteral(provisionedPackageName) + ";" +
        "$package=Get-AppxProvisionedPackage -Online | Where-Object PackageName -eq $name | Select-Object -First 1; if($null -eq $package){throw 'The exact provisioned package was not found.'};" +
        "$package | Remove-AppxProvisionedPackage -Online -AllUsers -ErrorAction Stop | Out-Null; 'Provisioning removed.'";

    internal static string BuildRegisterForActiveUserScriptBody(string packageFullName) =>
        BuildExactPackageLookup(packageFullName) +
        "$manifest=Join-Path ([string]$package.InstallLocation) 'AppxManifest.xml'; if(-not (Test-Path -LiteralPath $manifest)){throw 'AppxManifest.xml was not found.'};" +
        "$inner=\"Add-AppxPackage -DisableDevelopmentMode -Register '$($manifest.Replace(\"'\",\"''\"))' -ErrorAction Stop\";" +
        BuildInteractiveTaskInvocation("$inner") + "'Registered.'";

    internal static string BuildWingetActionScriptBody(string verb, WingetCatalogEntry package, WingetInstallScope scope)
    {
        var arguments = "$arguments=@(" + string.Join(",", BuildWingetArguments(verb, package, scope).Select(ToPowerShellLiteral)) + ");";
        if (scope == WingetInstallScope.Machine)
        {
            return "Set-StrictMode -Version Latest; $ErrorActionPreference='Stop';" + GetWingetPathFunction() + "$winget=Get-IccWingetPath;" + arguments +
                   "$output=@(& $winget @arguments 2>&1); if($LASTEXITCODE -ne 0){throw ('WinGet failed with exit code ' + $LASTEXITCODE + ': ' + ($output -join ' '))}; $output -join [Environment]::NewLine";
        }

        return "Set-StrictMode -Version Latest; $ErrorActionPreference='Stop';" + GetWingetPathFunction() + "$winget=Get-IccWingetPath -ForActiveUser;" + arguments +
               "$quoted=@($arguments | ForEach-Object { \"'\" + ([string]$_).Replace(\"'\",\"''\") + \"'\" }); $inner=\"& '$($winget.Replace(\"'\",\"''\"))' $($quoted -join ' '); exit `$LASTEXITCODE\";" +
               BuildInteractiveTaskInvocation("$inner") + "'WinGet completed.'";
    }

    private async ValueTask<DeviceActionResult> ExecuteWingetAsync(string verb, string host, WingetCatalogEntry package, WingetInstallScope scope, CancellationToken cancellationToken)
    {
        if (package is null || string.IsNullOrWhiteSpace(package.Id) || string.IsNullOrWhiteSpace(package.Source))
        {
            return DeviceActionResult.Fail("Select a WinGet result with an exact ID and source.", "invalid_winget_package");
        }

        return await ExecuteActionAsync(host, BuildWingetActionScriptBody(verb, package, scope), $"WinGet {verb} completed for '{package.Id}' from '{package.Source}' ({scope}).", cancellationToken);
    }

    private async ValueTask<DeviceActionResult> ExecuteActionAsync(string host, string scriptBody, string successMessage, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalizedHost = NormalizeHost(host);
        if (normalizedHost.Length == 0)
        {
            return DeviceActionResult.Fail("No host was provided.", "missing_host");
        }

        if (string.IsNullOrWhiteSpace(scriptBody))
        {
            return DeviceActionResult.Fail("The requested action is missing a package identity.", "missing_package_identity");
        }

        try
        {
            var execution = await executor.ExecuteForHostAsync(normalizedHost, scriptBody, cancellationToken);
            return execution.ExitCode == 0
                ? DeviceActionResult.Ok(successMessage)
                : DeviceActionResult.Fail(NormalizeError(execution), "appx_action_failed");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return DeviceActionResult.Fail(ex.Message, "appx_action_failed");
        }
    }

    private static AppxPackageEntry MapPackage(AppxPackagePayload package) => new(
        package.PackageFullName?.Trim() ?? string.Empty,
        package.PackageFamilyName?.Trim() ?? string.Empty,
        package.Name?.Trim() ?? string.Empty,
        package.DisplayName?.Trim() ?? string.Empty,
        package.Version?.Trim() ?? string.Empty,
        package.Publisher?.Trim() ?? string.Empty,
        package.Architecture?.Trim() ?? string.Empty,
        package.InstallLocation?.Trim() ?? string.Empty,
        package.IsFramework,
        package.IsResourcePackage,
        package.IsBundle,
        package.IsOptional,
        package.NonRemovable,
        package.IsProvisioned,
        package.ProvisionedPackageName?.Trim() ?? string.Empty,
        (package.Users ?? []).Select(static user => new AppxUserRegistration(
            user.UserSid?.Trim() ?? string.Empty,
            user.UserName?.Trim() ?? string.Empty,
            user.InstallState?.Trim() ?? string.Empty,
            user.IsActiveUser)).ToArray());

    private static string[] BuildWingetArguments(string verb, WingetCatalogEntry package, WingetInstallScope scope) =>
    [
        verb,
        "--id", package.Id.Trim(),
        "--exact",
        "--source", package.Source.Trim(),
        "--scope", scope == WingetInstallScope.Machine ? "machine" : "user",
        "--silent",
        "--disable-interactivity",
        "--accept-package-agreements",
        "--accept-source-agreements"
    ];

    private static string BuildExactPackageLookup(string packageFullName)
    {
        if (string.IsNullOrWhiteSpace(packageFullName))
        {
            return string.Empty;
        }

        return "Set-StrictMode -Version Latest; $ErrorActionPreference='Stop'; $name=" + ToPowerShellLiteral(packageFullName) + ";" +
               "$package=Get-AppxPackage -AllUsers | Where-Object PackageFullName -eq $name | Select-Object -First 1; if($null -eq $package){throw 'The exact AppX package was not found.'};";
    }

    private static string BuildInteractiveTaskInvocation(string innerExpression) =>
        "$activeUser=[string](Get-CimInstance Win32_ComputerSystem -ErrorAction Stop).UserName; if([string]::IsNullOrWhiteSpace($activeUser)){throw 'No active interactive user is logged on.'};" +
        "$encoded=[Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes(" + innerExpression + ")); $taskName='ICC-AppX-' + [Guid]::NewGuid().ToString('N');" +
        "$action=New-ScheduledTaskAction -Execute 'powershell.exe' -Argument ('-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand ' + $encoded);" +
        "$principal=New-ScheduledTaskPrincipal -UserId $activeUser -LogonType Interactive -RunLevel Limited;" +
        "try { Register-ScheduledTask -TaskName $taskName -Action $action -Principal $principal -Force | Out-Null; Start-ScheduledTask -TaskName $taskName;" +
        " $deadline=[DateTime]::UtcNow.AddSeconds(110); do { Start-Sleep -Milliseconds 500; $task=Get-ScheduledTask -TaskName $taskName; $info=Get-ScheduledTaskInfo -TaskName $taskName } while($task.State -eq 'Running' -and [DateTime]::UtcNow -lt $deadline);" +
        " if($task.State -eq 'Running'){Stop-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue; throw 'The interactive user action exceeded 110 seconds.'}; if([int64]$info.LastTaskResult -ne 0){throw ('The interactive user action failed with exit code ' + $info.LastTaskResult)}" +
        " } finally { Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue };";

    private static string GetWingetPathFunction() =>
        "function Get-IccWingetPath { param([switch]$ForActiveUser); $package=$null; if($ForActiveUser){ $activeUser=[string](Get-CimInstance Win32_ComputerSystem -ErrorAction Stop).UserName; if([string]::IsNullOrWhiteSpace($activeUser)){throw 'No active interactive user is logged on.'}; $sid=([System.Security.Principal.NTAccount]$activeUser).Translate([System.Security.Principal.SecurityIdentifier]).Value; $package=Get-AppxPackage -User $sid -Name Microsoft.DesktopAppInstaller -ErrorAction SilentlyContinue | Sort-Object Version -Descending | Select-Object -First 1 } else { $command=Get-Command winget.exe -ErrorAction SilentlyContinue; if($null -ne $command){return $command.Source}; $package=Get-AppxPackage -AllUsers -Name Microsoft.DesktopAppInstaller -ErrorAction SilentlyContinue | Sort-Object Version -Descending | Select-Object -First 1 }; if($null -eq $package){throw 'WinGet (App Installer) is not installed for the requested context.'}; $path=Join-Path ([string]$package.InstallLocation) 'winget.exe'; if(-not (Test-Path -LiteralPath $path)){throw 'winget.exe was not found.'}; return $path };";

    private static string ToPowerShellLiteral(string? value) => "'" + (value ?? string.Empty).Replace("'", "''", StringComparison.Ordinal) + "'";
    private static ValueTask<DeviceActionResult> MissingPackageIdentity() => ValueTask.FromResult(DeviceActionResult.Fail("The requested action is missing a package identity.", "missing_package_identity"));
    private static string NormalizeHost(string? host) => host?.Trim() ?? string.Empty;
    private static string EmptyJsonFallback(string? value) => string.IsNullOrWhiteSpace(value) ? "{}" : value;
    private static IReadOnlyList<string> NormalizeWarnings(IEnumerable<string?>? warnings) => warnings?.Where(static warning => !string.IsNullOrWhiteSpace(warning)).Select(static warning => warning!.Trim()).ToArray() ?? [];
    private static string NormalizeError(PowershellExecutionResult execution) => string.IsNullOrWhiteSpace(execution.StdErr) ? $"PowerShell execution failed with exit code {execution.ExitCode}." : execution.StdErr.Trim();

    private sealed record AppxInventoryPayload(string? ActiveUserName, string? ActiveUserSid, IReadOnlyList<AppxPackagePayload>? Packages, IReadOnlyList<string?>? Warnings);
    private sealed record AppxPackagePayload(string? PackageFullName, string? PackageFamilyName, string? Name, string? DisplayName, string? Version, string? Publisher, string? Architecture, string? InstallLocation, bool IsFramework, bool IsResourcePackage, bool IsBundle, bool IsOptional, bool NonRemovable, bool IsProvisioned, string? ProvisionedPackageName, IReadOnlyList<AppxUserPayload>? Users);
    private sealed record AppxUserPayload(string? UserSid, string? UserName, string? InstallState, bool IsActiveUser);
    private sealed record WingetSearchPayload(IReadOnlyList<WingetSearchEntryPayload>? Entries, IReadOnlyList<string?>? Warnings);
    private sealed record WingetSearchEntryPayload(string Id, string? Name, string? Version, string Source);
}
