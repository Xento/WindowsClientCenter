$maxOutputLines = __MAX_OUTPUT_LINES__
$warnings = New-Object System.Collections.Generic.List[string]
$lines = New-Object System.Collections.Generic.List[string]

function Add-Line {
    param([string]$Text)
    if (-not [string]::IsNullOrWhiteSpace($Text)) {
        $lines.Add($Text) | Out-Null
    }
}

try {
    $service = Get-Service -Name 'IntuneManagementExtension' -ErrorAction Stop
    Add-Line ('ServiceName: ' + $service.Name)
    Add-Line ('DisplayName: ' + $service.DisplayName)
    Add-Line ('Status: ' + $service.Status.ToString())

    try {
        $cim = Get-CimInstance -ClassName Win32_Service -Filter "Name='IntuneManagementExtension'" -ErrorAction SilentlyContinue
        if ($null -ne $cim) {
            if (-not [string]::IsNullOrWhiteSpace($cim.StartMode)) {
                Add-Line ('StartMode: ' + [string]$cim.StartMode)
            }

            $binaryPath = [string]$cim.PathName
            if (-not [string]::IsNullOrWhiteSpace($binaryPath)) {
                $binaryPath = $binaryPath.Trim('"')
                Add-Line ('BinaryPath: ' + $binaryPath)
                if (Test-Path -LiteralPath $binaryPath) {
                    try {
                        $item = Get-Item -LiteralPath $binaryPath -ErrorAction Stop
                        if ($null -ne $item.VersionInfo -and -not [string]::IsNullOrWhiteSpace($item.VersionInfo.FileVersion)) {
                            Add-Line ('BinaryVersion: ' + [string]$item.VersionInfo.FileVersion)
                        }
                    } catch {
                        $warnings.Add('Could not resolve IME binary version: ' + $_.Exception.Message) | Out-Null
                    }
                }
            }
        }
    } catch {
        $warnings.Add('Could not query Win32_Service for IME: ' + $_.Exception.Message) | Out-Null
    }
} catch {
    $warnings.Add('IntuneManagementExtension service not found: ' + $_.Exception.Message) | Out-Null
}

try {
    $regPath = 'Registry::HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\IntuneManagementExtension'
    if (Test-Path -LiteralPath $regPath) {
        $reg = Get-ItemProperty -LiteralPath $regPath -ErrorAction SilentlyContinue
        if ($null -ne $reg) {
            foreach ($name in @('AgentVersion', 'Version', 'DeviceId', 'TenantId')) {
                $property = $reg.PSObject.Properties[$name]
                if ($null -eq $property -or $null -eq $property.Value) {
                    continue
                }

                $value = [string]$property.Value
                if (-not [string]::IsNullOrWhiteSpace($value)) {
                    Add-Line ("Registry.$name: $value")
                }
            }
        }
    }
} catch {
    $warnings.Add('Could not read IME registry values: ' + $_.Exception.Message) | Out-Null
}

$lineCount = $lines.Count
$truncated = $false
if ($maxOutputLines -gt 0 -and $lineCount -gt $maxOutputLines) {
    $lines = @($lines | Select-Object -First $maxOutputLines)
    $truncated = $true
    $warnings.Add("Output truncated to $maxOutputLines lines.") | Out-Null
}

$outputText = if ($lines.Count -eq 0) {
    'No IME status details were collected.'
} else {
    $lines -join [Environment]::NewLine
}

$result = [ordered]@{
    Message = 'IME quick status collected.'
    OutputText = $outputText
    OutputLineCount = $lineCount
    Truncated = $truncated
    Warnings = $warnings
}

$result | ConvertTo-Json -Depth 6 -Compress
