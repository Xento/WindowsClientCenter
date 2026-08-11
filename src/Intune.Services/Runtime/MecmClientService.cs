using System.Globalization;
using System.Text.Json;
using WindowsClientCenter.Intune.Services.Contracts;
using WindowsClientCenter.Intune.Services.Models;
using WindowsClientCenter.Shared.Diagnostics;

namespace WindowsClientCenter.Intune.Services.Runtime;

internal sealed class MecmClientService(IPowerShellExecutor executor) : IMecmClientService, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };
    private readonly MecmOverviewClient _overviewClient = new(executor);
    private readonly Lazy<SccmClientCenterMecmService> _clientCenterFallback = new(() => new(executor));

    public void Dispose()
    {
        if (_clientCenterFallback.IsValueCreated)
        {
            _clientCenterFallback.Value.Dispose();
        }
    }

    public ValueTask<MecmOverviewSnapshot> GetOverviewAsync(string host, CancellationToken cancellationToken)
    {
        return _overviewClient.GetOverviewAsync(host, cancellationToken);
    }

    public ValueTask<DeviceActionResult> ExecuteOverviewActionAsync(string host, MecmOverviewAction action, CancellationToken cancellationToken)
    {
        return _overviewClient.ExecuteActionAsync(host, action, cancellationToken);
    }

    public async ValueTask<MecmApplicationSnapshot> GetApplicationsAsync(string host, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(host))
        {
            return new MecmApplicationSnapshot(string.Empty, [], ["No host was provided."]);
        }

        var normalizedHost = host.Trim();

        try
        {
            var execution = await executor.ExecuteForHostAsync(normalizedHost, BuildGetApplicationsScriptBody(), cancellationToken);
            if (execution.ExitCode != 0)
            {
                return new MecmApplicationSnapshot(normalizedHost, [], [NormalizeError(execution)]);
            }

            var payload = JsonSerializer.Deserialize<MecmApplicationPayload>(ToJson(execution.StdOut), JsonOptions);
            var entries = (payload?.Entries ?? [])
                .Select(static item => item.ToModel())
                .OrderBy(static item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static item => item.SoftwareVersion, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static item => item.Revision, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return new MecmApplicationSnapshot(
                normalizedHost,
                entries,
                NormalizeWarnings(payload?.Warnings));
        }
        catch (Exception ex)
        {
            return new MecmApplicationSnapshot(normalizedHost, [], [ex.Message]);
        }
    }

    public async ValueTask<DeviceActionResult> ExecuteApplicationActionAsync(string host, string applicationId, string revision, bool isMachineTarget, MecmApplicationAction action, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(host))
        {
            return DeviceActionResult.Fail("No host was provided.", "no_host");
        }

        if (string.IsNullOrWhiteSpace(applicationId))
        {
            return DeviceActionResult.Fail("No application id was provided.", "no_application_id");
        }

        var normalizedHost = host.Trim();
        var execution = await executor.ExecuteForHostAsync(
            normalizedHost,
            BuildApplicationActionScriptBody(applicationId.Trim(), revision?.Trim() ?? string.Empty, isMachineTarget, action),
            cancellationToken);

        return execution.ExitCode == 0
            ? DeviceActionResult.Ok(string.IsNullOrWhiteSpace(execution.StdOut)
                ? $"{action} queued for '{applicationId.Trim()}' on '{normalizedHost}'."
                : execution.StdOut.Trim())
            : DeviceActionResult.Fail(
                $"{action} failed for '{applicationId.Trim()}' on '{normalizedHost}': {NormalizeError(execution)}",
                "mecm_application_action_failed");
    }

    public async ValueTask<DeviceActionResult> TriggerApplicationEvaluationAsync(string host, MecmApplicationEvaluationMode mode, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(host))
        {
            return DeviceActionResult.Fail("No host was provided.", "no_host");
        }

        var normalizedHost = host.Trim();
        var execution = await executor.ExecuteForHostAsync(
            normalizedHost,
            BuildApplicationEvaluationScriptBody(mode),
            cancellationToken);

        return execution.ExitCode == 0
            ? DeviceActionResult.Ok(string.IsNullOrWhiteSpace(execution.StdOut)
                ? $"MECM application evaluation '{mode}' requested on '{normalizedHost}'."
                : execution.StdOut.Trim())
            : DeviceActionResult.Fail(
                $"MECM application evaluation '{mode}' failed on '{normalizedHost}': {NormalizeError(execution)}",
                "mecm_application_evaluation_failed");
    }

    public async ValueTask<MecmPendingUpdatesSnapshot> GetPendingUpdatesAsync(string host, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(host))
        {
            return new MecmPendingUpdatesSnapshot(string.Empty, [], ["No host was provided."]);
        }

        var normalizedHost = host.Trim();

        try
        {
            var execution = await executor.ExecuteForHostAsync(normalizedHost, BuildGetPendingUpdatesScriptBody(), cancellationToken);
            if (execution.ExitCode != 0)
            {
                return new MecmPendingUpdatesSnapshot(normalizedHost, [], [NormalizeError(execution)]);
            }

            var payload = JsonSerializer.Deserialize<MecmPendingUpdatesPayload>(ToJson(execution.StdOut), JsonOptions);
            var entries = (payload?.Entries ?? [])
                .Select(static item => item.ToModel())
                .OrderBy(static item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static item => item.ArticleId, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return new MecmPendingUpdatesSnapshot(
                normalizedHost,
                entries,
                NormalizeWarnings(payload?.Warnings));
        }
        catch (Exception ex)
        {
            return new MecmPendingUpdatesSnapshot(normalizedHost, [], [ex.Message]);
        }
    }

    public async ValueTask<MecmAllUpdatesSnapshot> GetAllUpdatesAsync(string host, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(host))
        {
            return new MecmAllUpdatesSnapshot(string.Empty, [], ["No host was provided."]);
        }

        var normalizedHost = host.Trim();

        try
        {
            var execution = await executor.ExecuteForHostAsync(normalizedHost, BuildGetAllUpdatesScriptBody(), cancellationToken);
            if (execution.ExitCode != 0)
            {
                return new MecmAllUpdatesSnapshot(normalizedHost, [], [NormalizeError(execution)]);
            }

            var payload = JsonSerializer.Deserialize<MecmAllUpdatesPayload>(ToJson(execution.StdOut), JsonOptions);
            var entries = (payload?.Entries ?? [])
                .Select(static item => item.ToModel())
                .OrderBy(static item => item.Title, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static item => item.Article, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static item => item.RevisionNumber)
                .ToArray();

            return new MecmAllUpdatesSnapshot(
                normalizedHost,
                entries,
                NormalizeWarnings(payload?.Warnings));
        }
        catch (Exception ex)
        {
            return new MecmAllUpdatesSnapshot(normalizedHost, [], [ex.Message]);
        }
    }

    public async ValueTask<DeviceActionResult> InstallUpdatesAsync(string host, MecmUpdateInstallRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(host))
        {
            return DeviceActionResult.Fail("No host was provided.", "no_host");
        }

        if (request.Mode == MecmUpdateInstallMode.Selected && request.SelectedUpdateIds.Count == 0)
        {
            return DeviceActionResult.Fail("No MECM updates were selected.", "no_updates_selected");
        }

        var normalizedHost = host.Trim();
        var execution = await executor.ExecuteForHostAsync(normalizedHost, BuildInstallUpdatesScriptBody(request), cancellationToken);

        return execution.ExitCode == 0
            ? DeviceActionResult.Ok(string.IsNullOrWhiteSpace(execution.StdOut)
                ? $"MECM update installation requested on '{normalizedHost}'."
                : execution.StdOut.Trim())
            : DeviceActionResult.Fail(
                $"MECM update installation failed on '{normalizedHost}': {NormalizeError(execution)}",
                "mecm_update_install_failed");
    }

    public ValueTask<MecmPackagesSnapshot> GetPackagesAsync(string host, CancellationToken cancellationToken)
    {
        return _clientCenterFallback.Value.GetPackagesAsync(host, cancellationToken);
    }

    public ValueTask<DeviceActionResult> ExecutePackageAsync(string host, string advertisementId, CancellationToken cancellationToken)
    {
        return _clientCenterFallback.Value.ExecutePackageAsync(host, advertisementId, cancellationToken);
    }

    public ValueTask<MecmBaselinesSnapshot> GetBaselinesAsync(string host, CancellationToken cancellationToken)
    {
        return _clientCenterFallback.Value.GetBaselinesAsync(host, cancellationToken);
    }

    public ValueTask<MecmBaselineDetails> GetBaselineDetailsAsync(string host, string baselineName, string version, bool isMachineTarget, CancellationToken cancellationToken)
    {
        return _clientCenterFallback.Value.GetBaselineDetailsAsync(host, baselineName, version, isMachineTarget, cancellationToken);
    }

    public ValueTask<DeviceActionResult> TriggerBaselineEvaluationAsync(string host, string baselineName, string version, bool isMachineTarget, bool enforce, CancellationToken cancellationToken)
    {
        return _clientCenterFallback.Value.TriggerBaselineEvaluationAsync(host, baselineName, version, isMachineTarget, enforce, cancellationToken);
    }

    internal static string BuildGetApplicationsScriptBody()
    {
        return """
$ErrorActionPreference = 'Stop'
$warnings = New-Object System.Collections.Generic.List[string]

function Test-IccCimClass {
    param([string]$Namespace, [string]$ClassName)

    try {
        Get-CimClass -Namespace $Namespace -ClassName $ClassName -ErrorAction Stop | Out-Null
        return $true
    }
    catch {
        return $false
    }
}

function Convert-IccCimDateToUtc {
    param($Value)

    if ($null -eq $Value) {
        return $null
    }

    if ($Value -is [datetimeoffset]) {
        return ([datetimeoffset]$Value).ToUniversalTime().ToString('o')
    }

    if ($Value -is [datetime]) {
        return ([datetime]$Value).ToUniversalTime().ToString('o')
    }

    $text = [string]$Value
    if ([string]::IsNullOrWhiteSpace($text)) {
        return $null
    }

    try {
        return [datetimeoffset]::Parse($text, [System.Globalization.CultureInfo]::InvariantCulture).ToUniversalTime().ToString('o')
    }
    catch {
    }

    try {
        return [System.Management.ManagementDateTimeConverter]::ToDateTime($text).ToUniversalTime().ToString('o')
    }
    catch {
        return $null
    }
}

function Test-IccAppActionPresent {
    param($Action)

    if ($null -eq $Action) {
        return $false
    }

    try {
        $actionType = [string]$Action.ActionType
        if (-not [string]::IsNullOrWhiteSpace($actionType)) {
            return $true
        }
    }
    catch {
    }

    try {
        $handlerName = [string]$Action.HandlerName
        if (-not [string]::IsNullOrWhiteSpace($handlerName)) {
            return $true
        }
    }
    catch {
    }

    return $false
}

function Add-IccCommandAvailability {
    param(
        [hashtable]$Target,
        [string]$Key,
        [bool]$HasInstallCommand,
        [bool]$HasUninstallCommand)

    if ([string]::IsNullOrWhiteSpace($Key)) {
        return
    }

    if ($Target.ContainsKey($Key)) {
        $existing = $Target[$Key]
        $Target[$Key] = [pscustomobject]@{
            HasInstallCommand = [bool]($existing.HasInstallCommand -or $HasInstallCommand)
            HasUninstallCommand = [bool]($existing.HasUninstallCommand -or $HasUninstallCommand)
        }
        return
    }

    $Target[$Key] = [pscustomobject]@{
        HasInstallCommand = $HasInstallCommand
        HasUninstallCommand = $HasUninstallCommand
    }
}

$commandAvailabilityByAppId = @{}
if (Test-IccCimClass -Namespace 'ROOT\ccm\cimodels' -ClassName 'CCM_AppDeliveryTypeSynclet') {
    try {
        foreach ($synclet in @(Get-CimInstance -Namespace 'ROOT\ccm\cimodels' -ClassName 'CCM_AppDeliveryTypeSynclet' -ErrorAction Stop)) {
            $appId = [string]$synclet.AppId
            $hasInstallCommand = Test-IccAppActionPresent $synclet.InstallAction
            $hasUninstallCommand = Test-IccAppActionPresent $synclet.UninstallAction
            Add-IccCommandAvailability -Target $commandAvailabilityByAppId -Key $appId -HasInstallCommand $hasInstallCommand -HasUninstallCommand $hasUninstallCommand
        }
    }
    catch {
        $warnings.Add('Failed to read MECM application delivery type synclets: ' + $_.Exception.Message) | Out-Null
    }
}

if (-not (Test-IccCimClass -Namespace 'ROOT\ccm\ClientSDK' -ClassName 'CCM_Application')) {
    $warnings.Add('MECM client SDK is not available on the target host.') | Out-Null
    $entries = @()
}
else {
    try {
        $entries = @(
            Get-CimInstance -Namespace 'ROOT\ccm\ClientSDK' -ClassName 'CCM_Application' -ErrorAction Stop |
                ForEach-Object {
                    $allowedActions = @()
                    $hasInstallCommand = $false
                    $hasUninstallCommand = $false
                    try {
                        if ($null -ne $_.AllowedActions) {
                            $allowedActions = @($_.AllowedActions | ForEach-Object { [string]$_ })
                        }
                    }
                    catch {
                    }

                    try {
                        foreach ($appDt in @($_.AppDTs)) {
                            if (-not $hasInstallCommand -and (Test-IccAppActionPresent $appDt.InstallAction)) {
                                $hasInstallCommand = $true
                            }

                            if (-not $hasUninstallCommand -and (Test-IccAppActionPresent $appDt.UninstallAction)) {
                                $hasUninstallCommand = $true
                            }

                            if ($hasInstallCommand -and $hasUninstallCommand) {
                                break
                            }
                        }
                    }
                    catch {
                    }

                    $appId = [string]$_.Id
                    if (-not [string]::IsNullOrWhiteSpace($appId) -and $commandAvailabilityByAppId.ContainsKey($appId)) {
                        $commandInfo = $commandAvailabilityByAppId[$appId]
                        $hasInstallCommand = [bool]($hasInstallCommand -or $commandInfo.HasInstallCommand)
                        $hasUninstallCommand = [bool]($hasUninstallCommand -or $commandInfo.HasUninstallCommand)
                    }

                    [pscustomobject]@{
                        Id = $appId
                        Name = [string]$_.Name
                        FullName = [string]$_.FullName
                        Description = [string]$_.Description
                        Icon = [string]$_.Icon
                        SoftwareVersion = [string]$_.SoftwareVersion
                        Revision = [string]$_.Revision
                        UserUiExperience = [bool]($_.UserUIExperience -eq $true)
                        IsPreflightOnly = [bool]($_.IsPreflightOnly -eq $true)
                        IsMachineTarget = [bool]($_.IsMachineTarget -eq $true)
                        AllowedActions = @($allowedActions)
                        InstallState = [string]$_.InstallState
                        ApplicabilityState = [string]$_.ApplicabilityState
                        ResolvedState = [string]$_.ResolvedState
                        EvaluationState = if ($null -ne $_.EvaluationState) { [int]$_.EvaluationState } else { $null }
                        ErrorCode = if ($null -ne $_.ErrorCode) { [uint32]$_.ErrorCode } else { $null }
                        LastEvalTimeUtc = Convert-IccCimDateToUtc $_.LastEvalTime
                        LastInstallTimeUtc = Convert-IccCimDateToUtc $_.LastInstallTime
                        HasInstallCommand = $hasInstallCommand
                        HasUninstallCommand = $hasUninstallCommand
                        HasIcon = -not [string]::IsNullOrWhiteSpace([string]$_.Icon)
                    }
                }
        )
    }
    catch {
        $warnings.Add($_.Exception.Message) | Out-Null
        $entries = @()
    }
}

[pscustomobject]@{
    Entries = @($entries)
    Warnings = @($warnings)
} | ConvertTo-Json -Depth 6 -Compress
""";
    }

    internal static string BuildApplicationActionScriptBody(string applicationId, string revision, bool isMachineTarget, MecmApplicationAction action)
    {
        var escapedApplicationId = EscapePowerShellSingleQuotedString(applicationId);
        var escapedRevision = EscapePowerShellSingleQuotedString(string.IsNullOrWhiteSpace(revision) ? "1" : revision);
        var actionName = action.ToString();
        var uninstallSetup = action == MecmApplicationAction.Uninstall
            ? $$"""

function Test-IccCimClass {
    param([string]$Namespace, [string]$ClassName)

    try {
        Get-CimClass -Namespace $Namespace -ClassName $ClassName -ErrorAction Stop | Out-Null
        return $true
    }
    catch {
        return $false
    }
}

$overrides = New-Object System.Collections.Generic.List[object]
$assignments = @()
$processed = $false

try {
    if (Test-IccCimClass -Namespace 'ROOT\ccm\Policy\Machine\ActualConfig' -ClassName 'CCM_ApplicationCIAssignment') {
        $applicationIdParts = '{{escapedApplicationId}}' -split '_'
        $assignmentToken = if ($applicationIdParts.Length -gt 2) { $applicationIdParts[2] } else { '{{escapedApplicationId}}' }
        $assignments = @(Get-CimInstance -Namespace 'ROOT\ccm\Policy\Machine\ActualConfig' -ClassName 'CCM_ApplicationCIAssignment' -ErrorAction Stop)

        foreach ($assignment in $assignments) {
            try {
                $assignedCis = @()
                if ($null -ne $assignment.AssignedCIs) {
                    $assignedCis = @($assignment.AssignedCIs | ForEach-Object { [string]$_ })
                }

                if ((@($assignedCis | Where-Object { $_.IndexOf($assignmentToken, [System.StringComparison]::OrdinalIgnoreCase) -ge 0 })).Count -gt 0) {
                    $overrides.Add([pscustomobject]@{
                        RelPath = $assignment.__RELPATH
                        AssignedCis = @($assignedCis)
                        EnforcementDeadline = $assignment.EnforcementDeadline
                    }) | Out-Null

                    Set-CimInstance -InputObject $assignment -Property @{ EnforcementDeadline = $null } -ErrorAction Stop | Out-Null
                    $processed = $true
                }
            }
            catch {
            }
        }

        if ($processed) {
            Start-Sleep -Milliseconds 2000
        }
    }

""".TrimStart()
            : string.Empty;
        var uninstallTeardown = action == MecmApplicationAction.Uninstall
            ? """

    if ($processed) {
        Start-Sleep -Milliseconds 1000
    }
}
finally {
    if ($processed) {
        foreach ($assignment in $assignments) {
            try {
                $assignedCis = @()
                if ($null -ne $assignment.AssignedCIs) {
                    $assignedCis = @($assignment.AssignedCIs | ForEach-Object { [string]$_ })
                }

                foreach ($override in $overrides) {
                    if ($override.EnforcementDeadline -eq $null) {
                        continue
                    }

                    if (@($override.AssignedCis).Length -ne $assignedCis.Length) {
                        continue
                    }

                    $matches = $true
                    for ($index = 0; $index -lt $assignedCis.Length; $index++) {
                        if (-not [string]::Equals($override.AssignedCis[$index], $assignedCis[$index], [System.StringComparison]::OrdinalIgnoreCase)) {
                            $matches = $false
                            break
                        }
                    }

                    if ($matches) {
                        Set-CimInstance -InputObject $assignment -Property @{ EnforcementDeadline = $override.EnforcementDeadline } -ErrorAction Stop | Out-Null
                    }
                }
            }
            catch {
            }
        }
    }
}
""".TrimEnd()
            : string.Empty;
        var invokePrefix = action == MecmApplicationAction.Uninstall ? string.Empty : "\n";
        var invokeSuffix = action == MecmApplicationAction.Uninstall ? string.Empty : "\n";

        return $$"""
$ErrorActionPreference = 'Stop'

try {
    Get-CimClass -Namespace 'ROOT\ccm\ClientSDK' -ClassName 'CCM_Application' -ErrorAction Stop | Out-Null
}
catch {
    throw 'MECM client SDK is not available on the target host.'
}

$arguments = @{
    Id = '{{escapedApplicationId}}'
    Revision = '{{escapedRevision}}'
    IsMachineTarget = ${{isMachineTarget.ToString().ToLowerInvariant()}}
    EnforcePreference = [uint32]0
    Priority = 'Normal'
    IsRebootIfNeeded = $false
}
{{uninstallSetup}}{{invokePrefix}}$result = Invoke-CimMethod -Namespace 'ROOT\ccm\ClientSDK' -ClassName 'CCM_Application' -MethodName '{{actionName}}' -Arguments $arguments -ErrorAction Stop{{invokeSuffix}}{{uninstallTeardown}}

$returnValue = if ($null -ne $result.ReturnValue) { [uint32]$result.ReturnValue } else { [uint32]0 }
if ($returnValue -ne 0) {
    throw ('CCM_Application.{{actionName}} returned 0x{0:X8}.' -f $returnValue)
}

$jobId = [string]$result.JobID
if ([string]::IsNullOrWhiteSpace($jobId)) {
    Write-Output('{{actionName}} queued for application {{escapedApplicationId}}.')
}
else {
    Write-Output('{{actionName}} queued for application {{escapedApplicationId}} (JobID: ' + $jobId + ').')
}
""";
    }

    internal static string BuildApplicationEvaluationScriptBody(MecmApplicationEvaluationMode mode)
    {
        return mode switch
        {
            MecmApplicationEvaluationMode.MachinePolicy => """
$ErrorActionPreference = 'Stop'

$result = Invoke-CimMethod -Namespace 'ROOT\ccm' -ClassName 'SMS_Client' -MethodName 'TriggerSchedule' -Arguments @{
    sScheduleID = '{00000000-0000-0000-0000-000000000121}'
} -ErrorAction Stop

$returnValue = if ($null -ne $result.ReturnValue) { [uint32]$result.ReturnValue } else { [uint32]0 }
if ($returnValue -ne 0) {
    throw ('SMS_Client.TriggerSchedule for application manager machine policy action returned 0x{0:X8}.' -f $returnValue)
}

Write-Output('Triggered MECM application manager machine policy action.')
""",
            MecmApplicationEvaluationMode.UserPolicy => """
$ErrorActionPreference = 'Stop'

function Get-IccLoggedOnUserPolicyNamespaces {
    $namespaces = New-Object System.Collections.Generic.List[string]
    try {
        foreach ($profile in @(Get-CimInstance -ClassName Win32_UserProfile -ErrorAction Stop | Where-Object { $_.Loaded -eq $true -and -not [string]::IsNullOrWhiteSpace($_.SID) })) {
            $candidate = 'ROOT\ccm\Policy\' + $profile.SID.Replace('-', '_') + '\ActualConfig'
            if (-not $namespaces.Contains($candidate)) {
                $namespaces.Add($candidate) | Out-Null
            }
        }
    }
    catch {
    }

    return @($namespaces)
}

$updated = 0
foreach ($namespace in @(Get-IccLoggedOnUserPolicyNamespaces)) {
    try {
        $message = Get-CimInstance -Namespace $namespace -ClassName 'CCM_Scheduler_ScheduledMessage' -Filter "ScheduledMessageID='{00000000-0000-0000-0000-000000000122}'" -ErrorAction Stop
        Set-CimInstance -InputObject $message -Property @{ Triggers = @('SimpleInterval;Minutes=1;MaxRandomDelayMinutes=0') } -ErrorAction Stop | Out-Null
        $updated++
    }
    catch {
    }
}

if ($updated -eq 0) {
    throw 'No logged-on user application policy schedules were found.'
}

Write-Output('Triggered MECM application manager user policy action.')
""",
            _ => """
$ErrorActionPreference = 'Stop'

$manager = New-Object -ComObject 'CPApplet.CPAppletMgr'
$action = @($manager.GetClientActions() | Where-Object { $_.ActionID -eq '{00000000-0000-0000-0000-000000000123}' }) | Select-Object -First 1
if ($null -eq $action) {
    throw 'Application manager global evaluation action was not found.'
}

$action.PerformAction()
Write-Output('Triggered MECM application manager global evaluation action.')
"""
        };
    }

    internal static string BuildGetPendingUpdatesScriptBody()
    {
        return """
$ErrorActionPreference = 'Stop'
$warnings = New-Object System.Collections.Generic.List[string]

function Test-IccCimClass {
    param([string]$Namespace, [string]$ClassName)

    try {
        Get-CimClass -Namespace $Namespace -ClassName $ClassName -ErrorAction Stop | Out-Null
        return $true
    }
    catch {
        return $false
    }
}

function Convert-IccCimDateToUtc {
    param($Value)

    if ($null -eq $Value) {
        return $null
    }

    if ($Value -is [datetimeoffset]) {
        return ([datetimeoffset]$Value).ToUniversalTime().ToString('o')
    }

    if ($Value -is [datetime]) {
        return ([datetime]$Value).ToUniversalTime().ToString('o')
    }

    $text = [string]$Value
    if ([string]::IsNullOrWhiteSpace($text)) {
        return $null
    }

    try {
        return [datetimeoffset]::Parse($text, [System.Globalization.CultureInfo]::InvariantCulture).ToUniversalTime().ToString('o')
    }
    catch {
    }

    try {
        return [System.Management.ManagementDateTimeConverter]::ToDateTime($text).ToUniversalTime().ToString('o')
    }
    catch {
        return $null
    }
}

$targetsById = @{}
if (Test-IccCimClass -Namespace 'root\ccm\SoftwareUpdates\DeploymentAgent' -ClassName 'CCM_TargetedUpdateEx1') {
    try {
        foreach ($target in @(Get-CimInstance -Namespace 'root\ccm\SoftwareUpdates\DeploymentAgent' -ClassName 'CCM_TargetedUpdateEx1' -ErrorAction Stop)) {
            $updateId = [string]$target.UpdateId
            if (-not [string]::IsNullOrWhiteSpace($updateId)) {
                $targetsById[$updateId] = $target
            }
        }
    }
    catch {
        $warnings.Add('Failed to read MECM targeted updates: ' + $_.Exception.Message) | Out-Null
    }
}
else {
    $warnings.Add('MECM deployment agent update metadata is not available on the target host.') | Out-Null
}

if (-not (Test-IccCimClass -Namespace 'ROOT\ccm\ClientSDK' -ClassName 'CCM_SoftwareUpdate')) {
    $warnings.Add('MECM client SDK is not available on the target host.') | Out-Null
    $entries = @()
}
else {
    try {
        $entries = @(
            Get-CimInstance -Namespace 'ROOT\ccm\ClientSDK' -ClassName 'CCM_SoftwareUpdate' -ErrorAction Stop |
                ForEach-Object {
                    $updateId = [string]$_.UpdateID
                    $target = if ($targetsById.ContainsKey($updateId)) { $targetsById[$updateId] } else { $null }

                    [pscustomobject]@{
                        UpdateId = $updateId
                        Name = [string]$_.Name
                        Publisher = [string]$_.Publisher
                        Description = [string]$_.Description
                        ArticleId = [string]$_.ArticleID
                        BulletinId = [string]$_.BulletinID
                        EvaluationState = if ($null -ne $_.EvaluationState) { [int]$_.EvaluationState } else { $null }
                        PercentComplete = if ($null -ne $target -and $null -ne $target.PercentComplete) { [int]$target.PercentComplete } elseif ($null -ne $_.PercentComplete) { [int]$_.PercentComplete } else { $null }
                        ErrorCode = if ($null -ne $_.ErrorCode) { [uint32]$_.ErrorCode } else { $null }
                        DeadlineUtc = if ($null -ne $target) { Convert-IccCimDateToUtc $target.Deadline } else { $null }
                    }
                }
        )
    }
    catch {
        $warnings.Add($_.Exception.Message) | Out-Null
        $entries = @()
    }
}

[pscustomobject]@{
    Entries = @($entries)
    Warnings = @($warnings)
} | ConvertTo-Json -Depth 6 -Compress
""";
    }

    internal static string BuildGetAllUpdatesScriptBody()
    {
        return """
$ErrorActionPreference = 'Stop'
$warnings = New-Object System.Collections.Generic.List[string]

function Test-IccCimClass {
    param([string]$Namespace, [string]$ClassName)

    try {
        Get-CimClass -Namespace $Namespace -ClassName $ClassName -ErrorAction Stop | Out-Null
        return $true
    }
    catch {
        return $false
    }
}

function Convert-IccCimDateToUtc {
    param($Value)

    if ($null -eq $Value) {
        return $null
    }

    if ($Value -is [datetimeoffset]) {
        return ([datetimeoffset]$Value).ToUniversalTime().ToString('o')
    }

    if ($Value -is [datetime]) {
        return ([datetime]$Value).ToUniversalTime().ToString('o')
    }

    $text = [string]$Value
    if ([string]::IsNullOrWhiteSpace($text)) {
        return $null
    }

    try {
        return [datetimeoffset]::Parse($text, [System.Globalization.CultureInfo]::InvariantCulture).ToUniversalTime().ToString('o')
    }
    catch {
    }

    try {
        return [System.Management.ManagementDateTimeConverter]::ToDateTime($text).ToUniversalTime().ToString('o')
    }
    catch {
        return $null
    }
}

if (-not (Test-IccCimClass -Namespace 'root\ccm\SoftwareUpdates\UpdatesStore' -ClassName 'CCM_UpdateStatus')) {
    $warnings.Add('MECM updates store is not available on the target host.') | Out-Null
    $entries = @()
}
else {
    try {
        $entries = @(
            Get-CimInstance -Namespace 'root\ccm\SoftwareUpdates\UpdatesStore' -ClassName 'CCM_UpdateStatus' -ErrorAction Stop |
                ForEach-Object {
                    [pscustomobject]@{
                        UniqueId = [string]$_.UniqueId
                        Title = [string]$_.Title
                        Article = [string]$_.Article
                        Bulletin = [string]$_.Bulletin
                        Language = [string]$_.Language
                        RevisionNumber = if ($null -ne $_.RevisionNumber) { [int]$_.RevisionNumber } else { $null }
                        ScanTimeUtc = Convert-IccCimDateToUtc $_.ScanTime
                        SourceVersion = if ($null -ne $_.SourceVersion) { [int]$_.SourceVersion } else { $null }
                        Status = [string]$_.Status
                        ProductId = [string]$_.ProductID
                    }
                }
        )
    }
    catch {
        $warnings.Add($_.Exception.Message) | Out-Null
        $entries = @()
    }
}

[pscustomobject]@{
    Entries = @($entries)
    Warnings = @($warnings)
} | ConvertTo-Json -Depth 6 -Compress
""";
    }

    internal static string BuildInstallUpdatesScriptBody(MecmUpdateInstallRequest request)
    {
        return request.Mode switch
        {
            MecmUpdateInstallMode.Selected => BuildInstallSelectedUpdatesScriptBody(request.SelectedUpdateIds),
            MecmUpdateInstallMode.AllMandatory => """
$ErrorActionPreference = 'Stop'
try {
    Get-CimClass -Namespace 'ROOT\ccm\ClientSDK' -ClassName 'CCM_SoftwareUpdate' -ErrorAction Stop | Out-Null
}
catch {
    throw 'MECM client SDK is not available on the target host.'
}

([wmiclass]'ROOT\ccm\ClientSDK:CCM_SoftwareUpdatesManager').InstallUpdates() | Out-Null
Write-Output('Installation requested for all mandatory MECM updates.')
""",
            MecmUpdateInstallMode.AllApproved => """
$ErrorActionPreference = 'Stop'
try {
    Get-CimClass -Namespace 'ROOT\ccm\ClientSDK' -ClassName 'CCM_SoftwareUpdate' -ErrorAction Stop | Out-Null
}
catch {
    throw 'MECM client SDK is not available on the target host.'
}

[System.Management.ManagementObject[]]$updates = @(Get-WmiObject -Query 'SELECT * FROM CCM_SoftwareUpdate' -Namespace 'ROOT\ccm\ClientSDK' -ErrorAction Stop)
if ($updates.Count -eq 0) {
    throw 'No approved MECM updates were found.'
}

([wmiclass]'ROOT\ccm\ClientSDK:CCM_SoftwareUpdatesManager').InstallUpdates($updates) | Out-Null
Write-Output('Installation requested for all approved MECM updates.')
""",
            _ => throw new ArgumentOutOfRangeException(nameof(request))
        };
    }

    private static string BuildInstallSelectedUpdatesScriptBody(IReadOnlyList<string> updateIds)
    {
        var literals = string.Join(", ", updateIds
            .Where(static updateId => !string.IsNullOrWhiteSpace(updateId))
            .Select(static updateId => $"'{EscapePowerShellSingleQuotedString(updateId.Trim())}'"));

        return $$"""
$ErrorActionPreference = 'Stop'
try {
    Get-CimClass -Namespace 'ROOT\ccm\ClientSDK' -ClassName 'CCM_SoftwareUpdate' -ErrorAction Stop | Out-Null
}
catch {
    throw 'MECM client SDK is not available on the target host.'
}

$selectedIds = @({{literals}})
if ($selectedIds.Count -eq 0) {
    throw 'No MECM updates were selected.'
}

$updates = New-Object System.Collections.ArrayList
foreach ($selectedId in $selectedIds) {
    $query = "SELECT * FROM CCM_SoftwareUpdate WHERE UpdateID='$selectedId'"
    $match = Get-WmiObject -Query $query -Namespace 'ROOT\ccm\ClientSDK' -ErrorAction SilentlyContinue
    if ($null -ne $match) {
        [void]$updates.Add($match)
    }
}

if ($updates.Count -eq 0) {
    throw 'The selected MECM updates were not found on the target host.'
}

([wmiclass]'ROOT\ccm\ClientSDK:CCM_SoftwareUpdatesManager').InstallUpdates([System.Management.ManagementObject[]]$updates.ToArray()) | Out-Null
Write-Output('Installation requested for ' + $updates.Count + ' selected MECM update(s).')
""";
    }

    private static string ToJson(string? stdOut)
    {
        return string.IsNullOrWhiteSpace(stdOut) ? "{}" : stdOut;
    }

    private static IReadOnlyList<string> NormalizeWarnings(IEnumerable<string>? warnings)
    {
        return warnings?
            .Where(static warning => !string.IsNullOrWhiteSpace(warning))
            .Select(static warning => warning.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray() ?? [];
    }

    private static string NormalizeError(PowershellExecutionResult execution)
    {
        return string.IsNullOrWhiteSpace(execution.StdErr)
            ? string.IsNullOrWhiteSpace(execution.StdOut) ? "Unknown error." : execution.StdOut.Trim()
            : execution.StdErr.Trim();
    }

    private static string EscapePowerShellSingleQuotedString(string value)
    {
        return value.Replace("'", "''", StringComparison.Ordinal);
    }

    private static string ResolveErrorText(uint? errorCode)
    {
        return errorCode.HasValue
            ? ErrorCodeResolver.ResolveDescription(errorCode.Value.ToString(CultureInfo.InvariantCulture))
            : string.Empty;
    }

    private static string ResolveApplicationEvaluationStateText(int? state)
    {
        return state switch
        {
            0 => "No state information is available.",
            1 => "Application is enforced to desired/resolved state.",
            2 => "Application is not required on the client.",
            3 => "Application is available for enforcement (install or uninstall based on resolved state). Content may/may not have been downloaded.",
            4 => "Application last failed to enforce (install/uninstall).",
            5 => "Application is currently waiting for content download to complete.",
            6 => "Application is currently waiting for content download to complete.",
            7 => "Application is currently waiting for its dependencies to download.",
            8 => "Application is currently waiting for a service (maintenance) window.",
            9 => "Application is currently waiting for a previously pending reboot.",
            10 => "Application is currently waiting for serialized enforcement.",
            11 => "Application is currently enforcing dependencies.",
            12 => "Application is currently enforcing.",
            13 => "Application install/uninstall enforced and soft reboot is pending.",
            14 => "Application installed/uninstalled and hard reboot is pending.",
            15 => "Update is available but pending installation.",
            16 => "Application failed to evaluate.",
            17 => "Application is currently waiting for an active user session to enforce.",
            18 => "Application is currently waiting for all users to logoff.",
            19 => "Application is currently waiting for a user logon.",
            20 => "Application in progress, waiting for retry.",
            21 => "Application is waiting for presentation mode to be switched off.",
            22 => "Application is pre-downloading content (downloading outside of install job).",
            23 => "Application is pre-downloading dependent content (downloading outside of install job).",
            24 => "Application download failed (downloading during install job).",
            25 => "Application pre-downloading failed (downloading outside of install job).",
            26 => "Download success (downloading during install job).",
            27 => "Post-enforce evaluation.",
            28 => "Waiting for network connectivity.",
            _ => "Unknown state information."
        };
    }

    private static string ResolveUpdateEvaluationStateText(int? state)
    {
        return state switch
        {
            0 => "ciJobStateNone",
            1 => "ciJobStateAvailable",
            2 => "ciJobStateSubmitted",
            3 => "ciJobStateDetecting",
            4 => "ciJobStatePreDownload",
            5 => "ciJobStateDownloading",
            6 => "ciJobStateWaitInstall",
            7 => "ciJobStateInstalling",
            8 => "ciJobStatePendingSoftReboot",
            9 => "ciJobStatePendingHardReboot",
            10 => "ciJobStateWaitReboot",
            11 => "ciJobStateVerifying",
            12 => "ciJobStateInstallComplete",
            13 => "ciJobStateError",
            14 => "ciJobStateWaitServiceWindow",
            15 => "ciJobStateWaitUserLogon",
            16 => "ciJobStateWaitUserLogoff",
            17 => "ciJobStateWaitJobUserLogon",
            18 => "ciJobStateWaitUserReconnect",
            19 => "ciJobStatePendingUserLogoff",
            20 => "ciJobStatePendingUpdate",
            21 => "ciJobStateWaitingRetry",
            22 => "ciJobStateWaitPresModeOff",
            23 => "ciJobStateWaitForOrchestration",
            _ => "Unknown state information."
        };
    }

    private sealed class MecmApplicationPayload
    {
        public MecmApplicationPayloadItem[]? Entries { get; set; }
        public string[]? Warnings { get; set; }
    }

    private sealed class MecmApplicationPayloadItem
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? FullName { get; set; }
        public string? Description { get; set; }
        public string? Icon { get; set; }
        public string? SoftwareVersion { get; set; }
        public string? Revision { get; set; }
        public bool UserUiExperience { get; set; }
        public bool IsPreflightOnly { get; set; }
        public bool IsMachineTarget { get; set; }
        public string[]? AllowedActions { get; set; }
        public string? InstallState { get; set; }
        public string? ApplicabilityState { get; set; }
        public string? ResolvedState { get; set; }
        public int? EvaluationState { get; set; }
        public uint? ErrorCode { get; set; }
        public string? LastEvalTimeUtc { get; set; }
        public string? LastInstallTimeUtc { get; set; }
        public bool HasInstallCommand { get; set; }
        public bool HasUninstallCommand { get; set; }
        public bool HasIcon { get; set; }

        public MecmApplicationEntry ToModel()
        {
            var actions = (AllowedActions ?? [])
                .Where(static action => !string.IsNullOrWhiteSpace(action))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return new MecmApplicationEntry(
                Id ?? string.Empty,
                Name ?? string.Empty,
                FullName ?? string.Empty,
                Description ?? string.Empty,
                Icon ?? string.Empty,
                SoftwareVersion ?? string.Empty,
                Revision ?? string.Empty,
                UserUiExperience,
                IsPreflightOnly,
                IsMachineTarget,
                actions,
                InstallState ?? string.Empty,
                ApplicabilityState ?? string.Empty,
                ResolvedState ?? string.Empty,
                EvaluationState,
                ResolveApplicationEvaluationStateText(EvaluationState),
                ErrorCode,
                ResolveErrorText(ErrorCode),
                ParseDateTimeOffset(LastEvalTimeUtc),
                ParseDateTimeOffset(LastInstallTimeUtc),
                HasInstallCommand,
                HasUninstallCommand,
                HasIcon);
        }
    }

    private sealed class MecmPendingUpdatesPayload
    {
        public MecmPendingUpdatesPayloadItem[]? Entries { get; set; }
        public string[]? Warnings { get; set; }
    }

    private sealed class MecmPendingUpdatesPayloadItem
    {
        public string? UpdateId { get; set; }
        public string? Name { get; set; }
        public string? Publisher { get; set; }
        public string? Description { get; set; }
        public string? ArticleId { get; set; }
        public string? BulletinId { get; set; }
        public int? EvaluationState { get; set; }
        public int? PercentComplete { get; set; }
        public uint? ErrorCode { get; set; }
        public string? DeadlineUtc { get; set; }

        public MecmPendingUpdateEntry ToModel()
        {
            return new MecmPendingUpdateEntry(
                UpdateId ?? string.Empty,
                Name ?? string.Empty,
                Publisher ?? string.Empty,
                Description ?? string.Empty,
                ArticleId ?? string.Empty,
                BulletinId ?? string.Empty,
                EvaluationState,
                ResolveUpdateEvaluationStateText(EvaluationState),
                PercentComplete,
                ErrorCode,
                ResolveErrorText(ErrorCode),
                ParseDateTimeOffset(DeadlineUtc));
        }
    }

    private sealed class MecmAllUpdatesPayload
    {
        public MecmAllUpdatesPayloadItem[]? Entries { get; set; }
        public string[]? Warnings { get; set; }
    }

    private sealed class MecmAllUpdatesPayloadItem
    {
        public string? UniqueId { get; set; }
        public string? Title { get; set; }
        public string? Article { get; set; }
        public string? Bulletin { get; set; }
        public string? Language { get; set; }
        public int? RevisionNumber { get; set; }
        public string? ScanTimeUtc { get; set; }
        public int? SourceVersion { get; set; }
        public string? Status { get; set; }
        public string? ProductId { get; set; }

        public MecmAllUpdateEntry ToModel()
        {
            return new MecmAllUpdateEntry(
                UniqueId ?? string.Empty,
                Title ?? string.Empty,
                Article ?? string.Empty,
                Bulletin ?? string.Empty,
                Language ?? string.Empty,
                RevisionNumber,
                ParseDateTimeOffset(ScanTimeUtc),
                SourceVersion,
                Status ?? string.Empty,
                ProductId ?? string.Empty);
        }
    }

    private static DateTimeOffset? ParseDateTimeOffset(string? value)
    {
        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : null;
    }
}
