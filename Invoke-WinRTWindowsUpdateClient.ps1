[CmdletBinding(DefaultParameterSetName='Inventory')]
<#
.SYNOPSIS
WinRT-based Windows Update client for inventory, metadata, scan, administrator, approval, and restart operations.

.DESCRIPTION
Uses the Windows.Management.Update WinRT namespace to inspect available update capabilities on the local or a remote
machine. Depending on OS build and implementation state, it can list applicable updates, completed updates, expose
metadata, trigger scans, inspect administrator registration, approve updates/actions, and request or cancel restarts.

.PARAMETER Action
Legacy operation selector.

Supported values:
- Inventory: full capability and data inventory
- Status: compact manager state summary
- Completed: most recent completed updates
- StartScan: start a scan through WindowsUpdateManager
- AdminInfo: inspect registered administrator state
- RegisterAdmin: register an administrator name
- UnregisterAdmin: unregister an administrator name
- AdminUpdates: list updates visible through the administrator object
- Approve: approve an update with WindowsUpdateApprovalData
- Revoke: revoke an update approval
- ApproveAction: approve a specific action for an update
- RevokeAction: revoke a specific action approval
- Install: try to start installation for a specific update
- RequestRestart: request a restart through the administrator API
- CancelRestart: cancel a previously issued restart request

This parameter is still supported for backward compatibility, but the script now also provides dedicated parameter
sets such as `-Inventory`, `-Completed`, `-Approve`, `-Install`, and `-RequestRestart`.

.PARAMETER Inventory
Select the Inventory parameter set.

.PARAMETER Status
Select the Status parameter set.

.PARAMETER Completed
Select the Completed parameter set.

.PARAMETER StartScan
Select the StartScan parameter set.

.PARAMETER AdminInfo
Select the AdminInfo parameter set.

.PARAMETER RegisterAdmin
Select the RegisterAdmin parameter set.

.PARAMETER UnregisterAdmin
Select the UnregisterAdmin parameter set.

.PARAMETER AdminUpdates
Select the AdminUpdates parameter set.

.PARAMETER Approve
Select the Approve parameter set.

.PARAMETER Revoke
Select the Revoke parameter set.

.PARAMETER ApproveAction
Select the ApproveAction parameter set.

.PARAMETER RevokeAction
Select the RevokeAction parameter set.

.PARAMETER Install
Select the Install parameter set.

.PARAMETER RequestRestart
Select the RequestRestart parameter set.

.PARAMETER CancelRestart
Select the CancelRestart parameter set.

.PARAMETER ClientId
Logical client identifier passed to WindowsUpdateManager.

This is not the computer name. It identifies the caller instance in the WinRT API.

.PARAMETER OrganizationName
Logical administrator/organization identifier used by WindowsUpdateAdministrator APIs.

It is used when registering for administration, querying a registered administrator, approving updates, and
requesting restarts in an administrator context.

.PARAMETER UpdateId
Update identifier used for approval or revoke operations.

Expected format typically looks like:
- `<guid>:<revision>`

Also used by:
- ApproveAction
- RevokeAction
- Install

.PARAMETER UpdateActionName
Action name used with ApproveAction or RevokeAction.

Typical examples are action names such as `Install`, but the valid action set depends on the target update and build.

.PARAMETER RestartRequestToken
Token returned by RequestRestart.

Used later with CancelRestart to revoke the restart request.

.PARAMETER CompletedCount
Number of entries to request from GetMostRecentCompletedUpdates().

.PARAMETER UserInitiatedScan
When used with StartScan, requests a user-initiated scan instead of a background-style scan.

.PARAMETER ApprovalSeeker
Optional value written into WindowsUpdateApprovalData.Seeker during Approve.

This can be used to explicitly mark approval data with seeker semantics when the API/build supports it.

.PARAMETER AllowDownloadOnMetered
Optional value written into WindowsUpdateApprovalData.AllowDownloadOnMetered during Approve.

.PARAMETER ComplianceDeadlineInDays
Optional deadline value in days.

For Approve:
- written into WindowsUpdateApprovalData.ComplianceDeadlineInDays

For RequestRestart:
- written into WindowsUpdateRestartRequestOptions.ComplianceDeadlineInDays

This is restart/compliance-related deadline data, not a generic "install by" timestamp.

.PARAMETER ComplianceGracePeriodInDays
Optional grace-period value in days.

For Approve:
- written into WindowsUpdateApprovalData.ComplianceGracePeriodInDays

For RequestRestart:
- written into WindowsUpdateRestartRequestOptions.ComplianceGracePeriodInDays

.PARAMETER OptOutOfAutoReboot
Optional flag written into approval or restart request data to express opt-out-of-auto-reboot behavior where supported.

.PARAMETER RestartTitle
Title used when building WindowsUpdateRestartRequestOptions for RequestRestart.

.PARAMETER RestartDescription
Description used when building WindowsUpdateRestartRequestOptions for RequestRestart.

.PARAMETER RestartMoreInfoUrl
More-info URL used when building WindowsUpdateRestartRequestOptions for RequestRestart.

.PARAMETER PropertyNames
Additional property names queried through WindowsUpdate.GetPropertyValue().

Useful for testing whether extra metadata such as OptionalInfo-like values are surfaced on a given build.

.PARAMETER AsJson
Returns the result as JSON instead of PowerShell objects.

.PARAMETER ComputerName
Remote computer name for PowerShell Remoting execution.

If specified, the script serializes itself and runs the same logic on the remote machine.

.PARAMETER Credential
Optional credential used for remote execution with -ComputerName.

.PARAMETER NoRemoteDispatch
Internal switch used to prevent recursive remote redispatch.

.EXAMPLE
.\Invoke-WinRTWindowsUpdateClient.ps1 -Inventory

Runs a full local inventory.

.EXAMPLE
.\Invoke-WinRTWindowsUpdateClient.ps1 -Completed -CompletedCount 10 -AsJson

Returns the ten most recent completed updates as JSON.

.EXAMPLE
.\Invoke-WinRTWindowsUpdateClient.ps1 -ComputerName PC01 -Inventory -AsJson

Runs the inventory on a remote machine over PowerShell Remoting.

.EXAMPLE
.\Invoke-WinRTWindowsUpdateClient.ps1 -Approve -OrganizationName Contoso -UpdateId '<guid>:1' -ComplianceDeadlineInDays 2 -ComplianceGracePeriodInDays 1

Builds a WindowsUpdateApprovalData object and attempts to approve the specified update.

.EXAMPLE
.\Invoke-WinRTWindowsUpdateClient.ps1 -Install -UpdateId '<guid>:1' -OrganizationName Contoso

Attempts to start installation for the specified update. The script first tries the direct WindowsSoftwareUpdate path and
then falls back to the administrator action-approval path for `Install`.
#>
param(
    [Parameter(ParameterSetName='LegacyAction')]
    [ValidateSet('Inventory','Status','Completed','StartScan','AdminInfo','RegisterAdmin','UnregisterAdmin','AdminUpdates','Approve','Revoke','ApproveAction','RevokeAction','Install','RequestRestart','CancelRestart')]
    [string]$Action,

    [Parameter(ParameterSetName='Inventory')]
    [switch]$Inventory,

    [Parameter(ParameterSetName='Status', Mandatory)]
    [switch]$Status,

    [Parameter(ParameterSetName='Completed', Mandatory)]
    [switch]$Completed,

    [Parameter(ParameterSetName='StartScan', Mandatory)]
    [switch]$StartScan,

    [Parameter(ParameterSetName='AdminInfo', Mandatory)]
    [switch]$AdminInfo,

    [Parameter(ParameterSetName='RegisterAdmin', Mandatory)]
    [switch]$RegisterAdmin,

    [Parameter(ParameterSetName='UnregisterAdmin', Mandatory)]
    [switch]$UnregisterAdmin,

    [Parameter(ParameterSetName='AdminUpdates', Mandatory)]
    [switch]$AdminUpdates,

    [Parameter(ParameterSetName='Approve', Mandatory)]
    [switch]$Approve,

    [Parameter(ParameterSetName='Revoke', Mandatory)]
    [switch]$Revoke,

    [Parameter(ParameterSetName='ApproveAction', Mandatory)]
    [switch]$ApproveAction,

    [Parameter(ParameterSetName='RevokeAction', Mandatory)]
    [switch]$RevokeAction,

    [Parameter(ParameterSetName='Install', Mandatory)]
    [switch]$Install,

    [Parameter(ParameterSetName='RequestRestart', Mandatory)]
    [switch]$RequestRestart,

    [Parameter(ParameterSetName='CancelRestart', Mandatory)]
    [switch]$CancelRestart,

    [string]$ClientId = 'USOClient',
    [string]$OrganizationName = 'Contoso',
    [Parameter(ParameterSetName='Approve', Mandatory)]
    [Parameter(ParameterSetName='Revoke', Mandatory)]
    [Parameter(ParameterSetName='ApproveAction', Mandatory)]
    [Parameter(ParameterSetName='RevokeAction', Mandatory)]
    [Parameter(ParameterSetName='Install', Mandatory)]
    [Parameter(ParameterSetName='LegacyAction')]
    [string]$UpdateId,
    [Parameter(ParameterSetName='ApproveAction', Mandatory)]
    [Parameter(ParameterSetName='RevokeAction', Mandatory)]
    [Parameter(ParameterSetName='LegacyAction')]
    [string]$UpdateActionName,
    [Parameter(ParameterSetName='CancelRestart', Mandatory)]
    [Parameter(ParameterSetName='LegacyAction')]
    [string]$RestartRequestToken,
    [Parameter(ParameterSetName='Completed')]
    [Parameter(ParameterSetName='LegacyAction')]
    [int]$CompletedCount = 20,
    [Parameter(ParameterSetName='StartScan')]
    [Parameter(ParameterSetName='LegacyAction')]
    [switch]$UserInitiatedScan,
    [Parameter(ParameterSetName='Approve')]
    [Parameter(ParameterSetName='LegacyAction')]
    [Nullable[bool]]$ApprovalSeeker,
    [Parameter(ParameterSetName='Approve')]
    [Parameter(ParameterSetName='LegacyAction')]
    [Nullable[bool]]$AllowDownloadOnMetered,
    [Parameter(ParameterSetName='Approve')]
    [Parameter(ParameterSetName='RequestRestart')]
    [Parameter(ParameterSetName='LegacyAction')]
    [Nullable[int]]$ComplianceDeadlineInDays,
    [Parameter(ParameterSetName='Approve')]
    [Parameter(ParameterSetName='RequestRestart')]
    [Parameter(ParameterSetName='LegacyAction')]
    [Nullable[int]]$ComplianceGracePeriodInDays,
    [Parameter(ParameterSetName='Approve')]
    [Parameter(ParameterSetName='RequestRestart')]
    [Parameter(ParameterSetName='LegacyAction')]
    [Nullable[bool]]$OptOutOfAutoReboot,
    [Parameter(ParameterSetName='RequestRestart')]
    [Parameter(ParameterSetName='LegacyAction')]
    [string]$RestartTitle = 'Windows Update restart required',
    [Parameter(ParameterSetName='RequestRestart')]
    [Parameter(ParameterSetName='LegacyAction')]
    [string]$RestartDescription = 'A Windows Update restart was requested by the WinRT client.',
    [Parameter(ParameterSetName='RequestRestart')]
    [Parameter(ParameterSetName='LegacyAction')]
    [string]$RestartMoreInfoUrl = 'https://learn.microsoft.com/en-us/uwp/api/windows.management.update?view=winrt-22621',
    [string[]]$PropertyNames = @(
        'OptionalInfo',
        'OptionalProperties',
        'ComplianceDeadlineInDays',
        'ComplianceGracePeriodInDays',
        'GatedBlockedStatus',
        'Seeker'
    ),
    [switch]$AsJson,
    [string]$ComputerName,
    [pscredential]$Credential,
    [switch]$NoRemoteDispatch
)

switch ($PSCmdlet.ParameterSetName) {
    'LegacyAction'    { $ResolvedAction = if ($Action) { $Action } else { 'Inventory' } }
    'Inventory'       { $ResolvedAction = 'Inventory' }
    'Status'          { $ResolvedAction = 'Status' }
    'Completed'       { $ResolvedAction = 'Completed' }
    'StartScan'       { $ResolvedAction = 'StartScan' }
    'AdminInfo'       { $ResolvedAction = 'AdminInfo' }
    'RegisterAdmin'   { $ResolvedAction = 'RegisterAdmin' }
    'UnregisterAdmin' { $ResolvedAction = 'UnregisterAdmin' }
    'AdminUpdates'    { $ResolvedAction = 'AdminUpdates' }
    'Approve'         { $ResolvedAction = 'Approve' }
    'Revoke'          { $ResolvedAction = 'Revoke' }
    'ApproveAction'   { $ResolvedAction = 'ApproveAction' }
    'RevokeAction'    { $ResolvedAction = 'RevokeAction' }
    'Install'         { $ResolvedAction = 'Install' }
    'RequestRestart'  { $ResolvedAction = 'RequestRestart' }
    'CancelRestart'   { $ResolvedAction = 'CancelRestart' }
    default           { $ResolvedAction = 'Inventory' }
}

if ($ComputerName -and -not $NoRemoteDispatch) {
    $scriptText = Get-Content -LiteralPath $PSCommandPath -Raw
    $boundParams = [ordered]@{}
    foreach ($key in $PSBoundParameters.Keys) {
        if ($key -in @('ComputerName','Credential','NoRemoteDispatch')) {
            continue
        }
        $boundParams[$key] = $PSBoundParameters[$key]
    }
    $boundParams['NoRemoteDispatch'] = $true

    if ($Credential) {
        Invoke-Command -ComputerName $ComputerName -Credential $Credential -ScriptBlock {
            param($text, $params)
            & ([scriptblock]::Create($text)) @params
        } -ArgumentList $scriptText, $boundParams
    }
    else {
        Invoke-Command -ComputerName $ComputerName -ScriptBlock {
            param($text, $params)
            & ([scriptblock]::Create($text)) @params
        } -ArgumentList $scriptText, $boundParams
    }
    exit $LASTEXITCODE
}

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-WinRTType {
    param([string]$TypeName)
    [type]::GetType("$TypeName, Windows, ContentType=WindowsRuntime", $false)
}

function Convert-WinRTValue {
    param($Value)

    if ($null -eq $Value) { return $null }
    if ($Value -is [string] -or $Value -is [bool] -or $Value -is [int] -or $Value -is [long] -or $Value -is [double]) { return $Value }
    if ($Value -is [DateTimeOffset]) { return $Value.ToString('o') }
    if ($Value -is [Uri]) { return $Value.ToString() }

    if ($Value -is [System.Collections.IEnumerable] -and -not ($Value -is [string])) {
        $items = @()
        foreach ($item in $Value) { $items += (Convert-WinRTValue -Value $item) }
        return $items
    }

    $props = $Value | Get-Member -MemberType Property | Select-Object -ExpandProperty Name
    if (-not $props) { return $Value.ToString() }

    $obj = [ordered]@{}
    foreach ($prop in $props) {
        try { $obj[$prop] = Convert-WinRTValue -Value $Value.$prop }
        catch { $obj[$prop] = "<error: $($_.Exception.Message)>" }
    }
    [pscustomobject]$obj
}

function Try-Invoke {
    param([scriptblock]$Script)
    try {
        [pscustomobject]@{ Success = $true; Result = (& $Script); Error = $null }
    }
    catch {
        [pscustomobject]@{ Success = $false; Result = $null; Error = $_.Exception.Message }
    }
}

function Test-MethodExists {
    param([object]$Object,[string]$MethodName,[int]$ParameterCount = -1)
    if ($null -eq $Object) { return $false }
    $methods = @($Object.GetType().GetMethods() | Where-Object Name -eq $MethodName)
    if ($ParameterCount -ge 0) { $methods = @($methods | Where-Object { $_.GetParameters().Count -eq $ParameterCount }) }
    $methods.Count -gt 0
}

function Get-OsBuildInfo {
    $props = Get-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion'
    [pscustomobject]@{
        ProductName      = $props.ProductName
        DisplayVersion   = $props.DisplayVersion
        ReleaseId        = $props.ReleaseId
        CurrentBuild     = $props.CurrentBuild
        UBR              = $props.UBR
        BuildLabEx       = $props.BuildLabEx
        InstallationType = $props.InstallationType
    }
}

function New-WindowsUpdateManager {
    param([string]$ClientId)
    New-Object 'Windows.Management.Update.WindowsUpdateManager, Windows, ContentType=WindowsRuntime' -ArgumentList @($ClientId)
}

function Get-WinRTInventory {
    $typeNames = @(
        'Windows.Management.Update.WindowsUpdateManager',
        'Windows.Management.Update.WindowsUpdate',
        'Windows.Management.Update.WindowsSoftwareUpdate',
        'Windows.Management.Update.WindowsSoftwareUpdateOptionalInfo',
        'Windows.Management.Update.WindowsUpdateItem',
        'Windows.Management.Update.WindowsUpdateAdministrator',
        'Windows.Management.Update.WindowsUpdateApprovalData',
        'Windows.Management.Update.WindowsUpdateAdministratorOptions',
        'Windows.Management.Update.WindowsUpdateRestartRequestOptions'
    )

    foreach ($name in $typeNames) {
        $type = Get-WinRTType -TypeName $name
        [pscustomobject]@{
            TypeName   = $name
            Present    = $null -ne $type
            Properties = if ($type) { @($type.GetProperties() | Sort-Object Name | Select-Object -ExpandProperty Name) } else { @() }
            Methods    = if ($type) { @($type.GetMethods() | Sort-Object Name | Select-Object -ExpandProperty Name -Unique) } else { @() }
        }
    }
}

function Get-ManagerSnapshot {
    param(
        [object]$Manager,
        [string[]]$PropertyNames,
        [int]$CompletedCount
    )

    $status = [ordered]@{
        IsScanning = $null
        IsWorking = $null
        LastSuccessfulScanTimestamp = $null
        ProviderIds = @()
    }
    try { $status.IsScanning = $Manager.IsScanning } catch {}
    try { $status.IsWorking = $Manager.IsWorking } catch {}
    try { if ($null -ne $Manager.LastSuccessfulScanTimestamp) { $status.LastSuccessfulScanTimestamp = $Manager.LastSuccessfulScanTimestamp.ToString('o') } } catch {}
    try { $status.ProviderIds = @($Manager.ProviderIds) } catch {}

    $applicableUpdatesCall = if (Test-MethodExists -Object $Manager -MethodName 'GetApplicableUpdates' -ParameterCount 0) {
        Try-Invoke { $Manager.GetApplicableUpdates() }
    } else {
        [pscustomobject]@{ Success = $false; Result = $null; Error = 'Method not present: GetApplicableUpdates()' }
    }

    $applicableSoftwareUpdatesCall = if (Test-MethodExists -Object $Manager -MethodName 'GetApplicableSoftwareUpdates' -ParameterCount 0) {
        Try-Invoke { $Manager.GetApplicableSoftwareUpdates() }
    } else {
        [pscustomobject]@{ Success = $false; Result = $null; Error = 'Method not present: GetApplicableSoftwareUpdates()' }
    }

    $providerCall = if (Test-MethodExists -Object $Manager -MethodName 'GetProvider' -ParameterCount 1) {
        Try-Invoke { $Manager.GetProvider('WuProvider') }
    } else {
        [pscustomobject]@{ Success = $false; Result = $null; Error = 'Method not present: GetProvider(string)' }
    }

    $recentCompletedCall = if (Test-MethodExists -Object $Manager -MethodName 'GetMostRecentCompletedUpdates' -ParameterCount 1) {
        Try-Invoke { $Manager.GetMostRecentCompletedUpdates($CompletedCount) }
    } else {
        [pscustomobject]@{ Success = $false; Result = $null; Error = 'Method not present: GetMostRecentCompletedUpdates(int)' }
    }

    $updates = @()
    if ($applicableUpdatesCall.Success -and $applicableUpdatesCall.Result) {
        foreach ($update in $applicableUpdatesCall.Result) {
            $propertyBag = [ordered]@{}
            foreach ($name in $PropertyNames) {
                try { $propertyBag[$name] = Convert-WinRTValue -Value ($update.GetPropertyValue($name)) }
                catch { $propertyBag[$name] = "<error: $($_.Exception.Message)>" }
            }
            $updates += [pscustomobject]@{
                Title               = $update.Title
                UpdateId            = $update.UpdateId
                ProviderId          = $update.ProviderId
                IsSeeker            = $update.IsSeeker
                Deadline            = if ($null -ne $update.Deadline) { $update.Deadline.ToString('o') } else { $null }
                IsDriver            = $update.IsDriver
                IsFeatureUpdate     = $update.IsFeatureUpdate
                IsMandatory         = $update.IsMandatory
                IsSecurity          = $update.IsSecurity
                CurrentAction       = $update.CurrentAction
                Description         = $update.Description
                MoreInfoUrl         = if ($null -ne $update.MoreInfoUrl) { $update.MoreInfoUrl.ToString() } else { $null }
                WinRTPropertyValues = [pscustomobject]$propertyBag
            }
        }
    }

    $softwareUpdates = @()
    if ($applicableSoftwareUpdatesCall.Success -and $applicableSoftwareUpdatesCall.Result) {
        foreach ($update in $applicableSoftwareUpdatesCall.Result) {
            $optionalInfo = $null
            $optionalInfoError = $null
            try { $optionalInfo = Convert-WinRTValue -Value $update.OptionalInfo }
            catch { $optionalInfoError = $_.Exception.Message }
            $softwareUpdates += [pscustomobject]@{
                Title                 = $update.Title
                UpdateId              = $update.UpdateId
                ProviderId            = $update.ProviderId
                OptionalInfo          = $optionalInfo
                OptionalInfoReadError = $optionalInfoError
                Description           = $update.Description
                CurrentAction         = $update.CurrentAction
                MoreInfoUrl           = if ($null -ne $update.MoreInfoUrl) { $update.MoreInfoUrl.ToString() } else { $null }
            }
        }
    }

    $completed = @()
    if ($recentCompletedCall.Success -and $recentCompletedCall.Result) {
        foreach ($item in $recentCompletedCall.Result) {
            $completed += [pscustomobject]@{
                Title       = $item.Title
                UpdateId    = $item.UpdateId
                ProviderId  = $item.ProviderId
                Timestamp   = if ($null -ne $item.Timestamp) { $item.Timestamp.ToString('o') } else { $null }
                Operation   = Convert-WinRTValue -Value $item.Operation
                Category    = Convert-WinRTValue -Value $item.Category
                Description = $item.Description
                MoreInfoUrl = if ($null -ne $item.MoreInfoUrl) { $item.MoreInfoUrl.ToString() } else { $null }
            }
        }
    }

    [pscustomobject]@{
        ManagerStatus                      = [pscustomobject]$status
        ApplicableUpdatesSucceeded         = $applicableUpdatesCall.Success
        ApplicableUpdatesError             = $applicableUpdatesCall.Error
        ApplicableSoftwareUpdatesSucceeded = $applicableSoftwareUpdatesCall.Success
        ApplicableSoftwareUpdatesError     = $applicableSoftwareUpdatesCall.Error
        GetProviderSucceeded               = $providerCall.Success
        GetProviderError                   = $providerCall.Error
        MostRecentCompletedSucceeded       = $recentCompletedCall.Success
        MostRecentCompletedError           = $recentCompletedCall.Error
        ApplicableUpdateCount              = @($updates).Count
        ApplicableSoftwareUpdateCount      = @($softwareUpdates).Count
        MostRecentCompletedCount           = @($completed).Count
        Updates                            = $updates
        SoftwareUpdates                    = $softwareUpdates
        MostRecentCompletedUpdates         = $completed
    }
}

function Get-RegisteredAdministratorInfo {
    param([string]$OrganizationName)

    $adminType = Get-WinRTType -TypeName 'Windows.Management.Update.WindowsUpdateAdministrator'
    if (-not $adminType) {
        return [pscustomobject]@{ Available = $false; Error = 'Type missing: WindowsUpdateAdministrator' }
    }

    $registeredName = $null
    try { $registeredName = $adminType::GetRegisteredAdministratorName() } catch {}

    $result = Try-Invoke { $adminType::GetRegisteredAdministrator($OrganizationName) }
    if (-not $result.Success) {
        return [pscustomobject]@{
            Available = $true
            RegisteredAdministratorName = $registeredName
            GetRegisteredAdministratorSucceeded = $false
            GetRegisteredAdministratorError = $result.Error
            Status = $null
            Administrator = $null
        }
    }

    [pscustomobject]@{
        Available = $true
        RegisteredAdministratorName = $registeredName
        GetRegisteredAdministratorSucceeded = $true
        GetRegisteredAdministratorError = $null
        Status = $result.Result.Status.ToString()
        Administrator = $result.Result.Administrator
    }
}

function New-ApprovalData {
    $approval = New-Object 'Windows.Management.Update.WindowsUpdateApprovalData, Windows, ContentType=WindowsRuntime'
    if ($PSBoundParameters.ContainsKey('ApprovalSeeker')) { $approval.Seeker = $ApprovalSeeker }
    if ($PSBoundParameters.ContainsKey('AllowDownloadOnMetered')) { $approval.AllowDownloadOnMetered = $AllowDownloadOnMetered }
    if ($PSBoundParameters.ContainsKey('ComplianceDeadlineInDays')) { $approval.ComplianceDeadlineInDays = $ComplianceDeadlineInDays }
    if ($PSBoundParameters.ContainsKey('ComplianceGracePeriodInDays')) { $approval.ComplianceGracePeriodInDays = $ComplianceGracePeriodInDays }
    if ($PSBoundParameters.ContainsKey('OptOutOfAutoReboot')) { $approval.OptOutOfAutoReboot = $OptOutOfAutoReboot }
    return $approval
}

function Get-AdministratorObject {
    param([string]$OrganizationName)
    $info = Get-RegisteredAdministratorInfo -OrganizationName $OrganizationName
    if (-not $info.Available) { throw $info.Error }
    if (-not $info.GetRegisteredAdministratorSucceeded) { throw $info.GetRegisteredAdministratorError }
    if ($null -eq $info.Administrator) { throw "No administrator object available for organization '$OrganizationName'. Status=$($info.Status)" }
    $info.Administrator
}

function Convert-ResultObject {
    param($Value)
    if ($null -eq $Value) { return $null }
    Convert-WinRTValue -Value $Value
}

$manager = New-WindowsUpdateManager -ClientId $ClientId
$snapshot = Get-ManagerSnapshot -Manager $manager -PropertyNames $PropertyNames -CompletedCount $CompletedCount
$reportBase = [ordered]@{
    TimestampUtc       = [DateTime]::UtcNow.ToString('o')
    ComputerName       = $env:COMPUTERNAME
    ClientId           = $ClientId
    Action             = $ResolvedAction
    OsBuild            = Get-OsBuildInfo
    WinRTTypeInventory = Get-WinRTInventory
    ManagerSnapshot    = $snapshot
}

switch ($ResolvedAction) {
    'Inventory' {
        $result = [pscustomobject]($reportBase + [ordered]@{})
    }
    'Status' {
        $result = [pscustomobject]($reportBase + [ordered]@{
            ManagerStatus = $snapshot.ManagerStatus
            ProviderIds = $snapshot.ManagerStatus.ProviderIds
            ApplicableUpdateCount = $snapshot.ApplicableUpdateCount
            ApplicableSoftwareUpdateCount = $snapshot.ApplicableSoftwareUpdateCount
            MostRecentCompletedCount = $snapshot.MostRecentCompletedCount
        })
    }
    'Completed' {
        $result = [pscustomobject]($reportBase + [ordered]@{
            MostRecentCompletedUpdates = $snapshot.MostRecentCompletedUpdates
        })
    }
    'StartScan' {
        $scanCall = if (Test-MethodExists -Object $manager -MethodName 'StartScan' -ParameterCount 1) {
            Try-Invoke { $manager.StartScan([bool]$UserInitiatedScan) }
        } else {
            [pscustomobject]@{ Success = $false; Result = $null; Error = 'Method not present: StartScan(bool)' }
        }
        $result = [pscustomobject]($reportBase + [ordered]@{
            StartScanSucceeded = $scanCall.Success
            StartScanError = $scanCall.Error
        })
    }
    'AdminInfo' {
        $adminInfo = Get-RegisteredAdministratorInfo -OrganizationName $OrganizationName
        $adminUpdates = @()
        if ($adminInfo.Available -and $adminInfo.GetRegisteredAdministratorSucceeded -and $null -ne $adminInfo.Administrator -and (Test-MethodExists -Object $adminInfo.Administrator -MethodName 'GetUpdates' -ParameterCount 0)) {
            $call = Try-Invoke { $adminInfo.Administrator.GetUpdates() }
            if ($call.Success -and $call.Result) {
                foreach ($update in $call.Result) {
                    $adminUpdates += [pscustomobject]@{
                        Title = $update.Title
                        UpdateId = $update.UpdateId
                        ProviderId = $update.ProviderId
                        IsSeeker = $update.IsSeeker
                        Deadline = if ($null -ne $update.Deadline) { $update.Deadline.ToString('o') } else { $null }
                    }
                }
            }
            $result = [pscustomobject]($reportBase + [ordered]@{
                OrganizationName = $OrganizationName
                RegisteredAdministratorName = $adminInfo.RegisteredAdministratorName
                GetRegisteredAdministratorSucceeded = $adminInfo.GetRegisteredAdministratorSucceeded
                GetRegisteredAdministratorError = $adminInfo.GetRegisteredAdministratorError
                AdministratorStatus = $adminInfo.Status
                AdminUpdates = $adminUpdates
            })
        } else {
            $result = [pscustomobject]($reportBase + [ordered]@{
                OrganizationName = $OrganizationName
                RegisteredAdministratorName = $adminInfo.RegisteredAdministratorName
                GetRegisteredAdministratorSucceeded = $adminInfo.GetRegisteredAdministratorSucceeded
                GetRegisteredAdministratorError = $adminInfo.GetRegisteredAdministratorError
                AdministratorStatus = $adminInfo.Status
                AdminUpdates = $adminUpdates
            })
        }
    }
    'RegisterAdmin' {
        $adminType = Get-WinRTType -TypeName 'Windows.Management.Update.WindowsUpdateAdministrator'
        $optionsType = Get-WinRTType -TypeName 'Windows.Management.Update.WindowsUpdateAdministratorOptions'
        if (-not $adminType -or -not $optionsType) { throw 'Administrator types are not available.' }
        $opts = $optionsType::None
        $call = Try-Invoke { $adminType::RegisterForAdministration($OrganizationName, $opts) }
        $result = [pscustomobject]($reportBase + [ordered]@{
            OrganizationName = $OrganizationName
            RegisterSucceeded = $call.Success
            RegisterError = $call.Error
            RegisterStatus = if ($call.Success) { $call.Result.ToString() } else { $null }
        })
    }
    'UnregisterAdmin' {
        $adminType = Get-WinRTType -TypeName 'Windows.Management.Update.WindowsUpdateAdministrator'
        if (-not $adminType) { throw 'Administrator type is not available.' }
        $call = Try-Invoke { $adminType::UnregisterForAdministration($OrganizationName) }
        $result = [pscustomobject]($reportBase + [ordered]@{
            OrganizationName = $OrganizationName
            UnregisterSucceeded = $call.Success
            UnregisterError = $call.Error
            UnregisterStatus = if ($call.Success) { $call.Result.ToString() } else { $null }
        })
    }
    'AdminUpdates' {
        $admin = Get-AdministratorObject -OrganizationName $OrganizationName
        $call = Try-Invoke { $admin.GetUpdates() }
        $adminUpdates = @()
        if ($call.Success -and $call.Result) {
            foreach ($update in $call.Result) {
                $adminUpdates += [pscustomobject]@{
                    Title = $update.Title
                    UpdateId = $update.UpdateId
                    ProviderId = $update.ProviderId
                    IsSeeker = $update.IsSeeker
                    Deadline = if ($null -ne $update.Deadline) { $update.Deadline.ToString('o') } else { $null }
                }
            }
        }
        $result = [pscustomobject]($reportBase + [ordered]@{
            OrganizationName = $OrganizationName
            AdminUpdatesSucceeded = $call.Success
            AdminUpdatesError = $call.Error
            AdminUpdates = $adminUpdates
        })
    }
    'Approve' {
        if (-not $UpdateId) { throw 'Approve requires -UpdateId.' }
        $admin = Get-AdministratorObject -OrganizationName $OrganizationName
        $approval = New-ApprovalData
        $call = Try-Invoke { $admin.ApproveWindowsUpdate($UpdateId, $approval) }
        $result = [pscustomobject]($reportBase + [ordered]@{
            OrganizationName = $OrganizationName
            UpdateId = $UpdateId
            ApprovalData = Convert-WinRTValue -Value $approval
            ApproveSucceeded = $call.Success
            ApproveError = $call.Error
        })
    }
    'Revoke' {
        if (-not $UpdateId) { throw 'Revoke requires -UpdateId.' }
        $admin = Get-AdministratorObject -OrganizationName $OrganizationName
        $call = Try-Invoke { $admin.RevokeWindowsUpdateApproval($UpdateId) }
        $result = [pscustomobject]($reportBase + [ordered]@{
            OrganizationName = $OrganizationName
            UpdateId = $UpdateId
            RevokeSucceeded = $call.Success
            RevokeError = $call.Error
        })
    }
    'ApproveAction' {
        if (-not $UpdateId -or -not $UpdateActionName) { throw 'ApproveAction requires -UpdateId and -UpdateActionName.' }
        $admin = Get-AdministratorObject -OrganizationName $OrganizationName
        $call = Try-Invoke { $admin.ApproveWindowsUpdateAction($UpdateId, $UpdateActionName) }
        $result = [pscustomobject]($reportBase + [ordered]@{
            OrganizationName = $OrganizationName
            UpdateId = $UpdateId
            UpdateActionName = $UpdateActionName
            ApproveActionSucceeded = $call.Success
            ApproveActionError = $call.Error
        })
    }
    'RevokeAction' {
        if (-not $UpdateId -or -not $UpdateActionName) { throw 'RevokeAction requires -UpdateId and -UpdateActionName.' }
        $admin = Get-AdministratorObject -OrganizationName $OrganizationName
        $call = Try-Invoke { $admin.RevokeWindowsUpdateActionApproval($UpdateId, $UpdateActionName) }
        $result = [pscustomobject]($reportBase + [ordered]@{
            OrganizationName = $OrganizationName
            UpdateId = $UpdateId
            UpdateActionName = $UpdateActionName
            RevokeActionSucceeded = $call.Success
            RevokeActionError = $call.Error
        })
    }
    'Install' {
        if (-not $UpdateId) { throw 'Install requires -UpdateId.' }

        $directSoftwareInstallAttempted = $false
        $directSoftwareInstallSucceeded = $false
        $directSoftwareInstallError = $null
        $directSoftwareInstallPath = $null
        $directSoftwareInstallResult = $null
        $matchedSoftwareUpdate = $null

        if (Test-MethodExists -Object $manager -MethodName 'GetApplicableSoftwareUpdates' -ParameterCount 0) {
            $softwareCall = Try-Invoke { $manager.GetApplicableSoftwareUpdates() }
            if ($softwareCall.Success -and $softwareCall.Result) {
                $matchedSoftwareUpdate = @($softwareCall.Result | Where-Object UpdateId -eq $UpdateId | Select-Object -First 1)
                if ($matchedSoftwareUpdate.Count -gt 0) {
                    $matchedSoftwareUpdate = $matchedSoftwareUpdate[0]

                    if (($matchedSoftwareUpdate.CurrentAction -eq 'Install') -and (Test-MethodExists -Object $matchedSoftwareUpdate -MethodName 'ApproveCurrentAction' -ParameterCount 1)) {
                        $directSoftwareInstallAttempted = $true
                        $call = Try-Invoke { $matchedSoftwareUpdate.ApproveCurrentAction($true) }
                        $directSoftwareInstallSucceeded = $call.Success
                        $directSoftwareInstallError = $call.Error
                        $directSoftwareInstallPath = 'WindowsSoftwareUpdate.ApproveCurrentAction(true)'
                        $directSoftwareInstallResult = Convert-ResultObject -Value $call.Result
                    }
                    elseif (Test-MethodExists -Object $matchedSoftwareUpdate -MethodName 'Approve' -ParameterCount 1) {
                        $directSoftwareInstallAttempted = $true
                        $approvalInfoType = Get-WinRTType -TypeName 'Windows.Management.Update.WindowsSoftwareUpdateApprovalInfo'
                        if ($approvalInfoType) {
                            try {
                                $approvalInfo = [Activator]::CreateInstance($approvalInfoType, @($false, $false, $false, $true))
                                $call = Try-Invoke { $matchedSoftwareUpdate.Approve($approvalInfo) }
                                $directSoftwareInstallSucceeded = $call.Success
                                $directSoftwareInstallError = $call.Error
                                $directSoftwareInstallPath = 'WindowsSoftwareUpdate.Approve(approvalInfo)'
                                $directSoftwareInstallResult = [pscustomobject]@{
                                    ApprovalInfo = Convert-ResultObject -Value $approvalInfo
                                    Result = Convert-ResultObject -Value $call.Result
                                }
                            }
                            catch {
                                $directSoftwareInstallSucceeded = $false
                                $directSoftwareInstallError = $_.Exception.Message
                                $directSoftwareInstallPath = 'WindowsSoftwareUpdate.Approve(approvalInfo)'
                            }
                        }
                        else {
                            $directSoftwareInstallSucceeded = $false
                            $directSoftwareInstallError = 'Type missing: WindowsSoftwareUpdateApprovalInfo'
                            $directSoftwareInstallPath = 'WindowsSoftwareUpdate.Approve(approvalInfo)'
                        }
                    }
                    else {
                        $directSoftwareInstallError = "Matched WindowsSoftwareUpdate lacks a usable install approval method. CurrentAction=$($matchedSoftwareUpdate.CurrentAction)"
                    }
                }
                else {
                    $directSoftwareInstallError = "GetApplicableSoftwareUpdates() succeeded, but no WindowsSoftwareUpdate matched UpdateId '$UpdateId'."
                }
            }
            else {
                $directSoftwareInstallError = $softwareCall.Error
            }
        }
        else {
            $directSoftwareInstallError = 'Method not present: GetApplicableSoftwareUpdates()'
        }

        $adminFallbackAttempted = $false
        $adminFallbackSucceeded = $false
        $adminFallbackError = $null
        $adminFallbackPath = $null

        if (-not $directSoftwareInstallSucceeded) {
            $adminFallbackAttempted = $true
            try {
                $admin = Get-AdministratorObject -OrganizationName $OrganizationName
                $call = Try-Invoke { $admin.ApproveWindowsUpdateAction($UpdateId, 'Install') }
                $adminFallbackSucceeded = $call.Success
                $adminFallbackError = $call.Error
                $adminFallbackPath = 'WindowsUpdateAdministrator.ApproveWindowsUpdateAction(updateId, ''Install'')'
            }
            catch {
                $adminFallbackSucceeded = $false
                $adminFallbackError = $_.Exception.Message
                $adminFallbackPath = 'WindowsUpdateAdministrator.ApproveWindowsUpdateAction(updateId, ''Install'')'
            }
        }

        $result = [pscustomobject]($reportBase + [ordered]@{
            OrganizationName               = $OrganizationName
            UpdateId                       = $UpdateId
            DirectSoftwareInstallAttempted = $directSoftwareInstallAttempted
            DirectSoftwareInstallSucceeded = $directSoftwareInstallSucceeded
            DirectSoftwareInstallPath      = $directSoftwareInstallPath
            DirectSoftwareInstallError     = $directSoftwareInstallError
            DirectSoftwareInstallResult    = $directSoftwareInstallResult
            MatchedSoftwareUpdate          = if ($matchedSoftwareUpdate) {
                [pscustomobject]@{
                    Title         = $matchedSoftwareUpdate.Title
                    UpdateId      = $matchedSoftwareUpdate.UpdateId
                    ProviderId    = $matchedSoftwareUpdate.ProviderId
                    CurrentAction = $matchedSoftwareUpdate.CurrentAction
                    Description   = $matchedSoftwareUpdate.Description
                }
            } else { $null }
            AdminFallbackAttempted         = $adminFallbackAttempted
            AdminFallbackSucceeded         = $adminFallbackSucceeded
            AdminFallbackPath              = $adminFallbackPath
            AdminFallbackError             = $adminFallbackError
            EffectiveInstallStartSucceeded = ($directSoftwareInstallSucceeded -or $adminFallbackSucceeded)
        })
    }
    'RequestRestart' {
        $adminType = Get-WinRTType -TypeName 'Windows.Management.Update.WindowsUpdateAdministrator'
        if (-not $adminType) { throw 'Administrator type is not available.' }
        $opts = New-Object 'Windows.Management.Update.WindowsUpdateRestartRequestOptions, Windows, ContentType=WindowsRuntime'
        $opts.OrganizationName = $OrganizationName
        $opts.Title = $RestartTitle
        $opts.Description = $RestartDescription
        if ($RestartMoreInfoUrl) { $opts.MoreInfoUrl = [Uri]$RestartMoreInfoUrl }
        if ($PSBoundParameters.ContainsKey('ComplianceDeadlineInDays')) { $opts.ComplianceDeadlineInDays = $ComplianceDeadlineInDays }
        if ($PSBoundParameters.ContainsKey('ComplianceGracePeriodInDays')) { $opts.ComplianceGracePeriodInDays = $ComplianceGracePeriodInDays }
        if ($PSBoundParameters.ContainsKey('OptOutOfAutoReboot')) { $opts.OptOutOfAutoReboot = $OptOutOfAutoReboot }
        $call = Try-Invoke { $adminType::RequestRestart($opts) }
        $result = [pscustomobject]($reportBase + [ordered]@{
            OrganizationName = $OrganizationName
            RestartRequestOptions = Convert-WinRTValue -Value $opts
            RequestRestartSucceeded = $call.Success
            RequestRestartError = $call.Error
            RestartRequestToken = if ($call.Success) { $call.Result } else { $null }
        })
    }
    'CancelRestart' {
        if (-not $RestartRequestToken) { throw 'CancelRestart requires -RestartRequestToken.' }
        $adminType = Get-WinRTType -TypeName 'Windows.Management.Update.WindowsUpdateAdministrator'
        if (-not $adminType) { throw 'Administrator type is not available.' }
        $call = Try-Invoke { $adminType::CancelRestartRequest($RestartRequestToken) }
        $result = [pscustomobject]($reportBase + [ordered]@{
            RestartRequestToken = $RestartRequestToken
            CancelRestartSucceeded = $call.Success
            CancelRestartError = $call.Error
        })
    }
}

if ($AsJson) {
    $result | ConvertTo-Json -Depth 10
}
else {
    $result
}
