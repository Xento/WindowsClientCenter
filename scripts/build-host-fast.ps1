param(
    [string]$Configuration = "Debug"
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

& "$PSScriptRoot\dotnet-win.ps1" build "src/Host.Wpf/Host.Wpf.csproj" -c $Configuration
