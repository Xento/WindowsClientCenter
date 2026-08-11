using System.Text.Json;
using WindowsClientCenter.Intune.Services.Contracts;
using WindowsClientCenter.Intune.Services.Models;

namespace WindowsClientCenter.Intune.Services.Runtime;

internal sealed class LocalIntuneEnrollmentService(IPowerShellExecutor executor) : ILocalIntuneEnrollmentService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public async ValueTask<EnrollmentStatus> GetEnrollmentStatusAsync(string host, CancellationToken cancellationToken)
    {
        var execution = await executor.ExecuteForHostAsync(host, BuildEnrollmentStatusScript(), cancellationToken);
        if (execution.ExitCode != 0)
        {
            return new EnrollmentStatus(
                host,
                LocalPowerShellExecutor.IsLocalHost(host),
                false,
                false,
                false,
                "Unknown",
                "Failed to query enrollment status.",
                [],
                [],
                [NormalizeError(execution)],
                [],
                CreateUnavailableEnrollmentUrlsStatus("Failed to query enrollment status."),
                false,
                false);
        }

        try
        {
            var payload = JsonSerializer.Deserialize<EnrollmentStatusPayload>(execution.StdOut, JsonOptions)
                          ?? throw new InvalidOperationException("Enrollment payload was empty.");

            return new EnrollmentStatus(
                host,
                LocalPowerShellExecutor.IsLocalHost(host),
                payload.WinRmAvailable,
                payload.IsAdminContext,
                payload.EnrollmentDetected,
                payload.LastSyncText ?? "Unknown",
                payload.RegistrationSummary ?? "Unknown",
                payload.EnrollmentIds ?? [],
                payload.Checks ?? [],
                payload.Warnings ?? [],
                (payload.Artifacts ?? []).Select(ToArtifact).ToArray(),
                ToEnrollmentUrlsStatus(payload.EnrollmentUrls),
                payload.CanTriggerSync,
                payload.CanReenroll);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return new EnrollmentStatus(
                host,
                LocalPowerShellExecutor.IsLocalHost(host),
                false,
                false,
                false,
                "Unknown",
                "Failed to parse enrollment status.",
                [],
                [],
                [$"Enrollment parsing failed: {ex.Message}"],
                [],
                CreateUnavailableEnrollmentUrlsStatus("Failed to parse enrollment URL status."),
                false,
                false);
        }
    }

    public async ValueTask<DeviceActionResult> TriggerSyncAsync(string host, CancellationToken cancellationToken)
    {
        var execution = await executor.ExecuteForHostAsync(host, BuildTriggerSyncScript(), cancellationToken);
        return execution.ExitCode == 0
            ? DeviceActionResult.Ok(string.IsNullOrWhiteSpace(execution.StdOut) ? $"Triggered Intune sync on '{host}'." : execution.StdOut.Trim())
            : DeviceActionResult.Fail(NormalizeError(execution), "sync_failed");
    }

    public async ValueTask<DeviceActionResult> FixEnrollmentUrlsAsync(string host, CancellationToken cancellationToken)
    {
        var execution = await executor.ExecuteForHostAsync(host, BuildFixEnrollmentUrlsScript(), cancellationToken);
        return execution.ExitCode == 0
            ? DeviceActionResult.Ok(string.IsNullOrWhiteSpace(execution.StdOut) ? $"Updated enrollment URLs on '{host}'." : execution.StdOut.Trim())
            : DeviceActionResult.Fail(NormalizeError(execution), "fix_enrollment_urls_failed");
    }

    public async ValueTask<EnrollmentRepairPreview> PreviewReenrollAsync(string host, CancellationToken cancellationToken)
    {
        var execution = await executor.ExecuteForHostAsync(host, BuildPreviewReenrollScript(), cancellationToken);
        if (execution.ExitCode != 0)
        {
            return new EnrollmentRepairPreview(
                host,
                false,
                $"REENROLL {host.ToUpperInvariant()}",
                "Re-enroll preview failed.",
                [NormalizeError(execution)],
                [],
                []);
        }

        try
        {
            var payload = JsonSerializer.Deserialize<PreviewPayload>(execution.StdOut, JsonOptions)
                          ?? throw new InvalidOperationException("Preview payload was empty.");

            return new EnrollmentRepairPreview(
                host,
                payload.CanExecute,
                payload.ConfirmationText ?? $"REENROLL {host.ToUpperInvariant()}",
                payload.Summary ?? string.Empty,
                payload.Blockers ?? [],
                payload.Steps ?? [],
                (payload.ArtifactsToRemove ?? []).Select(ToArtifact).ToArray());
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return new EnrollmentRepairPreview(
                host,
                false,
                $"REENROLL {host.ToUpperInvariant()}",
                "Failed to parse re-enroll preview.",
                [$"Preview parsing failed: {ex.Message}"],
                [],
                []);
        }
    }

    public async ValueTask<DeviceActionResult> ExecuteReenrollAsync(string host, bool confirmed, CancellationToken cancellationToken)
    {
        if (!confirmed)
        {
            return DeviceActionResult.Fail("Re-enroll execution requires explicit confirmation.", "confirmation_required");
        }

        var execution = await executor.ExecuteForHostAsync(host, BuildExecuteReenrollScript(), cancellationToken);
        return execution.ExitCode == 0
            ? DeviceActionResult.Ok(string.IsNullOrWhiteSpace(execution.StdOut) ? $"Re-enroll flow started on '{host}'." : execution.StdOut.Trim())
            : DeviceActionResult.Fail(NormalizeError(execution), "reenroll_failed");
    }

    private static EnrollmentArtifact ToArtifact(ArtifactPayload payload) =>
        new(
            payload.ArtifactType ?? string.Empty,
            payload.ArtifactPath ?? string.Empty,
            payload.Description ?? string.Empty,
            payload.EnrollmentId,
            payload.IsRemovable);

    private static string NormalizeError(PowershellExecutionResult execution)
    {
        var raw = string.IsNullOrWhiteSpace(execution.StdErr) ? execution.StdOut : execution.StdErr;
        return string.IsNullOrWhiteSpace(raw)
            ? $"PowerShell execution failed with exit code {execution.ExitCode}."
            : raw.Trim();
    }

    private static string BuildTriggerSyncScript() =>
        "$task = Get-ScheduledTask | Where-Object { $_.TaskName -eq 'PushLaunch' -and $_.TaskPath -like '\\\\Microsoft\\\\Windows\\\\EnterpriseMgmt\\\\*' } | Select-Object -First 1;" +
        "if ($null -eq $task) { throw \"Intune sync task 'PushLaunch' was not found.\" };" +
        "Start-ScheduledTask -TaskName $task.TaskName -TaskPath $task.TaskPath;" +
        "Write-Output ('Triggered Intune sync via task ' + $task.TaskPath + $task.TaskName);";

    private static string BuildEnrollmentStatusScript() =>
        "$currentIdentity=[Security.Principal.WindowsIdentity]::GetCurrent();" +
        "$principal=New-Object Security.Principal.WindowsPrincipal($currentIdentity);" +
        "$isAdmin=$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator);" +
        $"$expectedEnrollmentUrl='{EnrollmentUrlTargets.EnrollmentUrl}';" +
        $"$expectedTermsOfUseUrl='{EnrollmentUrlTargets.TermsOfUseUrl}';" +
        $"$expectedComplianceUrl='{EnrollmentUrlTargets.ComplianceUrl}';" +
        "$dsreg = (cmd /c 'dsregcmd /status') | Out-String;" +
        "$artifacts = New-Object System.Collections.Generic.List[object];" +
        "$checks = New-Object System.Collections.Generic.List[string];" +
        "$warnings = New-Object System.Collections.Generic.List[string];" +
        "$urlChecks = New-Object System.Collections.Generic.List[string];" +
        "$urlWarnings = New-Object System.Collections.Generic.List[string];" +
        "$enrollmentIds = New-Object System.Collections.Generic.List[string];" +
        "$enrollmentRoot = 'HKLM:\\SOFTWARE\\Microsoft\\Enrollments';" +
        "$tenantInfoRoot = 'HKLM:\\SYSTEM\\CurrentControlSet\\Control\\CloudDomainJoin\\TenantInfo';" +
        "$tenantKeys = @(Get-ChildItem -LiteralPath $tenantInfoRoot -ErrorAction SilentlyContinue);" +
        "$mdmEnrollmentUrl = $null;" +
        "$mdmTermsOfUseUrl = $null;" +
        "$mdmComplianceUrl = $null;" +
        "if (Test-Path -LiteralPath $enrollmentRoot) {" +
        "  foreach ($key in (Get-ChildItem -LiteralPath $enrollmentRoot | Where-Object { $_.PSChildName -match '^[0-9A-Fa-f-]{36}$' })) {" +
        "    $enrollmentIds.Add($key.PSChildName);" +
        "    $artifacts.Add([ordered]@{ ArtifactType='Registry'; ArtifactPath=$key.Name; Description='Enrollment root'; EnrollmentId=$key.PSChildName; IsRemovable=$true });" +
        "  }" +
        "}" +
        "if ($tenantKeys.Count -gt 0) {" +
        "  $urlChecks.Add('CloudDomainJoin tenant info detected.');" +
        "  foreach ($tenantKey in $tenantKeys) {" +
        "    try {" +
        "      $props = Get-ItemProperty -LiteralPath $tenantKey.PSPath -ErrorAction Stop;" +
        "      if ([string]::IsNullOrWhiteSpace($mdmEnrollmentUrl) -and -not [string]::IsNullOrWhiteSpace($props.MdmEnrollmentUrl)) { $mdmEnrollmentUrl = [string]$props.MdmEnrollmentUrl };" +
        "      if ([string]::IsNullOrWhiteSpace($mdmTermsOfUseUrl) -and -not [string]::IsNullOrWhiteSpace($props.MdmTermsOfUseUrl)) { $mdmTermsOfUseUrl = [string]$props.MdmTermsOfUseUrl };" +
        "      if ([string]::IsNullOrWhiteSpace($mdmComplianceUrl) -and -not [string]::IsNullOrWhiteSpace($props.MdmComplianceUrl)) { $mdmComplianceUrl = [string]$props.MdmComplianceUrl };" +
        "      if ([string]::Equals([string]$props.MdmEnrollmentUrl, $expectedEnrollmentUrl, [System.StringComparison]::OrdinalIgnoreCase)) { $urlChecks.Add('MdmEnrollmentUrl matches expected Intune discovery endpoint.') } else { $urlWarnings.Add('MdmEnrollmentUrl is missing or differs from the expected Intune discovery endpoint.') };" +
        "      if ([string]::Equals([string]$props.MdmTermsOfUseUrl, $expectedTermsOfUseUrl, [System.StringComparison]::OrdinalIgnoreCase)) { $urlChecks.Add('MdmTermsOfUseUrl matches expected Intune terms endpoint.') } else { $urlWarnings.Add('MdmTermsOfUseUrl is missing or differs from the expected Intune terms endpoint.') };" +
        "      if ([string]::Equals([string]$props.MdmComplianceUrl, $expectedComplianceUrl, [System.StringComparison]::OrdinalIgnoreCase)) { $urlChecks.Add('MdmComplianceUrl matches expected Intune compliance endpoint.') } else { $urlWarnings.Add('MdmComplianceUrl is missing or differs from the expected Intune compliance endpoint.') };" +
        "    } catch { $urlWarnings.Add('Failed to read CloudDomainJoin tenant info from ' + $tenantKey.Name + ': ' + $_.Exception.Message) }" +
        "  }" +
        "} else { $urlWarnings.Add('No CloudDomainJoin tenant info registry key was found.') }" +
        "$urlsConfigured = -not [string]::IsNullOrWhiteSpace($mdmEnrollmentUrl) -and -not [string]::IsNullOrWhiteSpace($mdmTermsOfUseUrl) -and -not [string]::IsNullOrWhiteSpace($mdmComplianceUrl);" +
        "$urlsExpected = ($tenantKeys.Count -gt 0) -and $urlsConfigured -and ($urlWarnings.Count -eq 0);" +
        "$urlSummary = if ($urlsExpected) { 'Enrollment URLs are configured correctly.' } elseif ($tenantKeys.Count -eq 0) { 'CloudDomainJoin tenant info was not found.' } elseif (-not $urlsConfigured) { 'Enrollment URLs are missing or incomplete.' } else { 'Enrollment URLs differ from the expected Microsoft Intune values.' };" +
        "$task = Get-ScheduledTask -ErrorAction SilentlyContinue | Where-Object { $_.TaskName -eq 'PushLaunch' -and $_.TaskPath -like '\\\\Microsoft\\\\Windows\\\\EnterpriseMgmt\\\\*' } | Select-Object -First 1;" +
        "$lastSyncText='Unknown';" +
        "if ($null -ne $task) {" +
        "  $checks.Add('EnterpriseMgmt PushLaunch task present.');" +
        "  try { $info = Get-ScheduledTaskInfo -TaskName $task.TaskName -TaskPath $task.TaskPath -ErrorAction Stop; if ($info.LastRunTime.Year -gt 2000) { $lastSyncText = $info.LastRunTime.ToString('u') } } catch { $warnings.Add($_.Exception.Message) }" +
        "} else { $warnings.Add('EnterpriseMgmt PushLaunch task was not found.') }" +
        "if ($isAdmin) { $checks.Add('Administrative context confirmed.') } else { $warnings.Add('Administrative context is required for re-enrollment repairs.') }" +
        "if ($enrollmentIds.Count -gt 0) { $checks.Add('Enrollment registry entries detected: ' + ($enrollmentIds -join ', ')) } else { $warnings.Add('No enrollment registry GUIDs were detected.') }" +
        "$registrationSummary = (($dsreg -split [Environment]::NewLine | Where-Object { $_ -match 'AzureAdJoined|DomainJoined|DeviceId|TenantId|MdmUrl|WorkplaceJoined' } | Select-Object -First 5) -join '; ');" +
        "if ([string]::IsNullOrWhiteSpace($registrationSummary)) { $registrationSummary = 'No dsreg summary detected.' }" +
        "$result = [ordered]@{" +
        "  WinRmAvailable=$true;" +
        "  IsAdminContext=$isAdmin;" +
        "  EnrollmentDetected=($enrollmentIds.Count -gt 0);" +
        "  LastSyncText=$lastSyncText;" +
        "  RegistrationSummary=$registrationSummary;" +
        "  EnrollmentIds=$enrollmentIds;" +
        "  Checks=$checks;" +
        "  Warnings=$warnings;" +
        "  Artifacts=$artifacts;" +
        "  EnrollmentUrls=[ordered]@{" +
        "    TenantInfoDetected=($tenantKeys.Count -gt 0);" +
        "    AreConfigured=$urlsConfigured;" +
        "    AreExpected=$urlsExpected;" +
        "    Summary=$urlSummary;" +
        "    Checks=$urlChecks;" +
        "    Warnings=$urlWarnings;" +
        "    EnrollmentUrl=$mdmEnrollmentUrl;" +
        "    TermsOfUseUrl=$mdmTermsOfUseUrl;" +
        "    ComplianceUrl=$mdmComplianceUrl;" +
        "    CanRepair=($isAdmin -and $tenantKeys.Count -gt 0);" +
        "  };" +
        "  CanTriggerSync=($null -ne $task);" +
        "  CanReenroll=($isAdmin -and $enrollmentIds.Count -gt 0);" +
        "};" +
        "$result | ConvertTo-Json -Depth 8 -Compress;";

    private static string BuildFixEnrollmentUrlsScript() =>
        "$currentIdentity=[Security.Principal.WindowsIdentity]::GetCurrent();" +
        "$principal=New-Object Security.Principal.WindowsPrincipal($currentIdentity);" +
        "if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Administrative context is required.' };" +
        "$tenantInfoRoot = 'HKLM:\\SYSTEM\\CurrentControlSet\\Control\\CloudDomainJoin\\TenantInfo';" +
        "$tenantKeys = @(Get-ChildItem -LiteralPath $tenantInfoRoot -ErrorAction SilentlyContinue);" +
        "if ($tenantKeys.Count -eq 0) { throw 'No CloudDomainJoin tenant info registry key was found.' };" +
        $"$expectedEnrollmentUrl='{EnrollmentUrlTargets.EnrollmentUrl}';" +
        $"$expectedTermsOfUseUrl='{EnrollmentUrlTargets.TermsOfUseUrl}';" +
        $"$expectedComplianceUrl='{EnrollmentUrlTargets.ComplianceUrl}';" +
        "$updated = 0;" +
        "foreach ($tenantKey in $tenantKeys) {" +
        "  Set-ItemProperty -LiteralPath $tenantKey.PSPath -Name 'MdmEnrollmentUrl' -Value $expectedEnrollmentUrl -Type String -Force;" +
        "  Set-ItemProperty -LiteralPath $tenantKey.PSPath -Name 'MdmTermsOfUseUrl' -Value $expectedTermsOfUseUrl -Type String -Force;" +
        "  Set-ItemProperty -LiteralPath $tenantKey.PSPath -Name 'MdmComplianceUrl' -Value $expectedComplianceUrl -Type String -Force;" +
        "  $updated++;" +
        "}" +
        "Write-Output ('Updated enrollment URLs in ' + $updated + ' CloudDomainJoin tenant info key(s).');";

    private static EnrollmentUrlsStatus ToEnrollmentUrlsStatus(EnrollmentUrlsStatusPayload? payload)
    {
        if (payload is null)
        {
            return CreateUnavailableEnrollmentUrlsStatus("Enrollment URL status is not available.");
        }

        return new EnrollmentUrlsStatus(
            payload.TenantInfoDetected,
            payload.AreConfigured,
            payload.AreExpected,
            payload.Summary ?? "Enrollment URL status is not available.",
            payload.Checks ?? [],
            payload.Warnings ?? [],
            payload.EnrollmentUrl ?? string.Empty,
            payload.TermsOfUseUrl ?? string.Empty,
            payload.ComplianceUrl ?? string.Empty,
            payload.CanRepair);
    }

    private static EnrollmentUrlsStatus CreateUnavailableEnrollmentUrlsStatus(string summary) =>
        new(
            false,
            false,
            false,
            summary,
            [],
            [],
            string.Empty,
            string.Empty,
            string.Empty,
            false);

    private static string BuildPreviewReenrollScript() =>
        "$currentIdentity=[Security.Principal.WindowsIdentity]::GetCurrent();" +
        "$principal=New-Object Security.Principal.WindowsPrincipal($currentIdentity);" +
        "$isAdmin=$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator);" +
        "$blockers = New-Object System.Collections.Generic.List[string];" +
        "$steps = New-Object System.Collections.Generic.List[string];" +
        "$artifacts = New-Object System.Collections.Generic.List[object];" +
        "$ids = @(Get-ChildItem -LiteralPath 'HKLM:\\SOFTWARE\\Microsoft\\Enrollments' -ErrorAction SilentlyContinue | Where-Object { $_.PSChildName -match '^[0-9A-Fa-f-]{36}$' } | Select-Object -ExpandProperty PSChildName);" +
        "if (-not $isAdmin) { $blockers.Add('Administrative context is required.') }" +
        "if ($ids.Count -eq 0) { $blockers.Add('No enrollment GUID-backed artifacts were detected.') }" +
        "foreach ($id in $ids) {" +
        "  foreach ($root in 'HKLM:\\SOFTWARE\\Microsoft\\Enrollments','HKLM:\\SOFTWARE\\Microsoft\\Enrollments\\Status','HKLM:\\SOFTWARE\\Microsoft\\EnterpriseResourceManager\\Tracked','HKLM:\\SOFTWARE\\Microsoft\\Provisioning\\OMADM\\Accounts','HKLM:\\SOFTWARE\\Microsoft\\Provisioning\\OMADM\\Logger','HKLM:\\SOFTWARE\\Microsoft\\Provisioning\\OMADM\\Sessions') {" +
        "    $path = Join-Path $root $id;" +
        "    if (Test-Path -LiteralPath $path) { $artifacts.Add([ordered]@{ ArtifactType='Registry'; ArtifactPath=$path; Description='Remove stale MDM enrollment registry key'; EnrollmentId=$id; IsRemovable=$true }) }" +
        "  }" +
        "  $taskPath = '\\\\Microsoft\\\\Windows\\\\EnterpriseMgmt\\\\' + $id + '\\\\';" +
        "  $artifacts.Add([ordered]@{ ArtifactType='ScheduledTaskFolder'; ArtifactPath=$taskPath; Description='Remove stale EnterpriseMgmt task folder'; EnrollmentId=$id; IsRemovable=$true });" +
        "}" +
        "$steps.Add('Validate admin context and enrollment GUIDs.');" +
        "$steps.Add('Remove only GUID-scoped MDM enrollment registry keys.');" +
        "$steps.Add('Unregister GUID-scoped EnterpriseMgmt scheduled tasks.');" +
        "$steps.Add('Re-trigger built-in MDM auto-enrollment using deviceenroller.exe.');" +
        "$summary = if ($artifacts.Count -gt 0) { 'Preview found ' + $artifacts.Count + ' removable artifacts.' } else { 'No removable artifacts found.' };" +
        "$result = [ordered]@{" +
        "  CanExecute=($blockers.Count -eq 0 -and $artifacts.Count -gt 0);" +
        "  ConfirmationText=('REENROLL ' + $env:COMPUTERNAME.ToUpperInvariant());" +
        "  Summary=$summary;" +
        "  Blockers=$blockers;" +
        "  Steps=$steps;" +
        "  ArtifactsToRemove=$artifacts;" +
        "};" +
        "$result | ConvertTo-Json -Depth 8 -Compress;";

    private static string BuildExecuteReenrollScript() =>
        "$currentIdentity=[Security.Principal.WindowsIdentity]::GetCurrent();" +
        "$principal=New-Object Security.Principal.WindowsPrincipal($currentIdentity);" +
        "if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Administrative context is required.' };" +
        "$ids = @(Get-ChildItem -LiteralPath 'HKLM:\\SOFTWARE\\Microsoft\\Enrollments' -ErrorAction SilentlyContinue | Where-Object { $_.PSChildName -match '^[0-9A-Fa-f-]{36}$' } | Select-Object -ExpandProperty PSChildName);" +
        "if ($ids.Count -eq 0) { throw 'No enrollment GUID-backed artifacts were detected.' };" +
        "$removed = New-Object System.Collections.Generic.List[string];" +
        "foreach ($id in $ids) {" +
        "  foreach ($root in 'HKLM:\\SOFTWARE\\Microsoft\\Enrollments','HKLM:\\SOFTWARE\\Microsoft\\Enrollments\\Status','HKLM:\\SOFTWARE\\Microsoft\\EnterpriseResourceManager\\Tracked','HKLM:\\SOFTWARE\\Microsoft\\Provisioning\\OMADM\\Accounts','HKLM:\\SOFTWARE\\Microsoft\\Provisioning\\OMADM\\Logger','HKLM:\\SOFTWARE\\Microsoft\\Provisioning\\OMADM\\Sessions') {" +
        "    $path = Join-Path $root $id;" +
        "    if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force -ErrorAction Stop; $removed.Add($path) }" +
        "  }" +
        "  Get-ScheduledTask -ErrorAction SilentlyContinue | Where-Object { $_.TaskPath -eq ('\\\\Microsoft\\\\Windows\\\\EnterpriseMgmt\\\\' + $id + '\\\\') } | ForEach-Object { Unregister-ScheduledTask -TaskName $_.TaskName -TaskPath $_.TaskPath -Confirm:$false; $removed.Add($_.TaskPath + $_.TaskName) };" +
        "}" +
        "$deviceEnroller = Join-Path $env:SystemRoot 'System32\\deviceenroller.exe';" +
        "if (-not (Test-Path -LiteralPath $deviceEnroller)) { throw 'deviceenroller.exe was not found.' };" +
        "$process = Start-Process -FilePath $deviceEnroller -ArgumentList '/c','/AutoEnrollMDM' -WindowStyle Hidden -Wait -PassThru;" +
        "if ($process.ExitCode -ne 0) { throw ('deviceenroller.exe exited with code ' + $process.ExitCode) };" +
        "Write-Output ('Removed ' + $removed.Count + ' artifact(s) and triggered deviceenroller.exe /c /AutoEnrollMDM.');";

    private sealed class EnrollmentStatusPayload
    {
        public bool WinRmAvailable { get; init; }
        public bool IsAdminContext { get; init; }
        public bool EnrollmentDetected { get; init; }
        public string? LastSyncText { get; init; }
        public string? RegistrationSummary { get; init; }
        public List<string>? EnrollmentIds { get; init; }
        public List<string>? Checks { get; init; }
        public List<string>? Warnings { get; init; }
        public List<ArtifactPayload>? Artifacts { get; init; }
        public EnrollmentUrlsStatusPayload? EnrollmentUrls { get; init; }
        public bool CanTriggerSync { get; init; }
        public bool CanReenroll { get; init; }
    }

    private sealed class EnrollmentUrlsStatusPayload
    {
        public bool TenantInfoDetected { get; init; }
        public bool AreConfigured { get; init; }
        public bool AreExpected { get; init; }
        public string? Summary { get; init; }
        public List<string>? Checks { get; init; }
        public List<string>? Warnings { get; init; }
        public string? EnrollmentUrl { get; init; }
        public string? TermsOfUseUrl { get; init; }
        public string? ComplianceUrl { get; init; }
        public bool CanRepair { get; init; }
    }

    private sealed class PreviewPayload
    {
        public bool CanExecute { get; init; }
        public string? ConfirmationText { get; init; }
        public string? Summary { get; init; }
        public List<string>? Blockers { get; init; }
        public List<string>? Steps { get; init; }
        public List<ArtifactPayload>? ArtifactsToRemove { get; init; }
    }

    private sealed class ArtifactPayload
    {
        public string? ArtifactType { get; init; }
        public string? ArtifactPath { get; init; }
        public string? Description { get; init; }
        public string? EnrollmentId { get; init; }
        public bool IsRemovable { get; init; }
    }
}
