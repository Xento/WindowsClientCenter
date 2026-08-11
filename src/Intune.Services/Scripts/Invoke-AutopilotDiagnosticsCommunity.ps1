$requestedVersion = '__MODULE_VERSION__'
$includeAllSessions = __ALL_SESSIONS__
$includePolicies = __SHOW_POLICIES__
$maxOutputLines = __MAX_OUTPUT_LINES__

$moduleName = 'Get-AutopilotDiagnosticsCommunity'
$scriptCommandName = 'Get-AutopilotDiagnosticsCommunity.ps1'
$warnings = New-Object System.Collections.Generic.List[string]

function Get-InstalledScriptPath {
    try {
        $installedScript = Get-InstalledScript -Name $moduleName -ErrorAction SilentlyContinue |
            Sort-Object Version -Descending |
            Select-Object -First 1
        if ($null -ne $installedScript -and -not [string]::IsNullOrWhiteSpace($installedScript.InstalledLocation)) {
            $candidatePath = Join-Path -Path ([string]$installedScript.InstalledLocation) -ChildPath $scriptCommandName
            if (Test-Path -LiteralPath $candidatePath) {
                return [string]$candidatePath
            }
        }
    } catch {
        # Best effort lookup.
    }

    $command = Get-Command -Name $scriptCommandName -CommandType ExternalScript -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -ne $command -and -not [string]::IsNullOrWhiteSpace($command.Path)) {
        return [string]$command.Path
    }

    return ''
}

function Ensure-NuGetProvider {
    if ($null -ne (Get-PackageProvider -Name NuGet -ListAvailable -ErrorAction SilentlyContinue)) {
        return
    }

    Install-PackageProvider -Name NuGet -Scope CurrentUser -Force -ErrorAction Stop | Out-Null
}

function Ensure-CommunityScript {
    $existingPath = Get-InstalledScriptPath
    if (-not [string]::IsNullOrWhiteSpace($existingPath)) {
        return $existingPath
    }

    if ($null -eq (Get-Command -Name Install-Script -ErrorAction SilentlyContinue)) {
        throw 'Install-Script is not available on the target host.'
    }

    Ensure-NuGetProvider

    try {
        Set-PSRepository -Name PSGallery -InstallationPolicy Trusted -ErrorAction SilentlyContinue | Out-Null
    } catch {
        $warnings.Add('Unable to set PSGallery to Trusted: ' + $_.Exception.Message) | Out-Null
    }

    $installParams = @{
        Name = $moduleName
        Scope = 'CurrentUser'
        Force = $true
        ErrorAction = 'Stop'
        AllowClobber = $true
        AcceptLicense = $true
    }

    if (-not [string]::IsNullOrWhiteSpace($requestedVersion)) {
        $installParams['RequiredVersion'] = $requestedVersion
    }

    Install-Script @installParams | Out-Null

    $installedPath = Get-InstalledScriptPath
    if ([string]::IsNullOrWhiteSpace($installedPath)) {
        throw 'Install-Script completed but script path could not be resolved.'
    }

    return $installedPath
}

$scriptPath = Ensure-CommunityScript
$installedVersion = ''
try {
    $installed = Get-InstalledScript -Name $moduleName -ErrorAction SilentlyContinue | Sort-Object Version -Descending | Select-Object -First 1
    if ($null -ne $installed -and $null -ne $installed.Version) {
        $installedVersion = [string]$installed.Version
    }
} catch {
    $warnings.Add('Could not resolve installed script version: ' + $_.Exception.Message) | Out-Null
}

$transcriptPath = Join-Path -Path ([System.IO.Path]::GetTempPath()) -ChildPath ('icc-autopilot-diag-' + [guid]::NewGuid().ToString('N') + '.txt')
try {
    Start-Transcript -Path $transcriptPath -Force -ErrorAction Stop | Out-Null
    try {
        $invokeParams = @{}
        if ($includeAllSessions) { $invokeParams['AllSessions'] = $true }
        if ($includePolicies) { $invokeParams['ShowPolicies'] = $true }
        & $scriptPath @invokeParams
    }
    finally {
        try {
            Stop-Transcript | Out-Null
        }
        catch {
            $warnings.Add('Stop-Transcript failed: ' + $_.Exception.Message) | Out-Null
        }
    }

    $outputText = ''
    if (Test-Path -LiteralPath $transcriptPath) {
        $outputText = Get-Content -LiteralPath $transcriptPath -Raw -ErrorAction SilentlyContinue
    }

    $lines = @()
    if (-not [string]::IsNullOrWhiteSpace($outputText)) {
        $lines = $outputText -split "`r?`n"
    }

    $nonEmptyLines = @($lines | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    $lineCount = $nonEmptyLines.Count
    $truncated = $false

    if ($maxOutputLines -gt 0 -and $lineCount -gt $maxOutputLines) {
        $nonEmptyLines = $nonEmptyLines | Select-Object -First $maxOutputLines
        $truncated = $true
        $warnings.Add("Output truncated to $maxOutputLines lines.") | Out-Null
    }

    $result = [ordered]@{
        Message = 'Autopilot diagnostics collected with community script.'
        ScriptPath = $scriptPath
        InstalledVersion = $installedVersion
        OutputText = ($nonEmptyLines -join [Environment]::NewLine)
        OutputLineCount = $lineCount
        Truncated = $truncated
        Warnings = $warnings
    }

    $result | ConvertTo-Json -Depth 6 -Compress
}
finally {
    if (Test-Path -LiteralPath $transcriptPath) {
        Remove-Item -LiteralPath $transcriptPath -Force -ErrorAction SilentlyContinue
    }
}
