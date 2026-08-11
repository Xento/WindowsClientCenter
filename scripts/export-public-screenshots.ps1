param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",
    [string]$OutputDirectory = "docs/images",
    [string]$Profile = "readme",
    [ValidateSet("Mock", "Demo", "Live")]
    [string]$IntuneMode = "Demo"
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repoRoot

try {
    if ($IsWindows) {
        dotnet run --project src/Host.Wpf/Host.Wpf.csproj -c $Configuration -- --capture-screenshots --capture-output $OutputDirectory --capture-profile $Profile --capture-intune-mode $IntuneMode --host DEMO-CLIENT-01
    }
    else {
        $windowsRepoRoot = wslpath -w $repoRoot
        $quotedRepoRoot = $windowsRepoRoot.Replace("'", "''")
        $quotedConfiguration = $Configuration.Replace("'", "''")
        $quotedOutputDirectory = $OutputDirectory.Replace("'", "''")
        $quotedProfile = $Profile.Replace("'", "''")
        $quotedIntuneMode = $IntuneMode.Replace("'", "''")
        $command = "Set-Location '$quotedRepoRoot'; dotnet run --project 'src/Host.Wpf/Host.Wpf.csproj' -c '$quotedConfiguration' -- --capture-screenshots --capture-output '$quotedOutputDirectory' --capture-profile '$quotedProfile' --capture-intune-mode '$quotedIntuneMode' --host DEMO-CLIENT-01"
        powershell.exe -NoProfile -ExecutionPolicy Bypass -Command $command
    }
}
finally {
    Pop-Location
}
