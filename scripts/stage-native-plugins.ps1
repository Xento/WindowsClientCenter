param(
    [string]$Configuration = "Release",
    [string]$OutputRoot = "$PSScriptRoot/../artifacts/native"
)

$ErrorActionPreference = "Stop"

$solutionRoot = Resolve-Path "$PSScriptRoot/.."
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
$nativeOut = "$OutputRoot/plugins/native"
$powerShellScriptsOut = "$OutputRoot/PSScripts"

function Invoke-DotNetOrThrow {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments
    )

    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

function Remove-DirectoryRobust {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    if (-not (Test-Path $Path)) {
        return
    }

    for ($attempt = 1; $attempt -le 5; $attempt++) {
        try {
            Remove-Item $Path -Recurse -Force -ErrorAction Stop
            return
        }
        catch {
            if ($attempt -eq 5 -or -not (Test-Path $Path)) {
                throw
            }

            Start-Sleep -Milliseconds 250
        }
    }
}

if (Test-Path $nativeOut) {
    Remove-DirectoryRobust $nativeOut
}

if (Test-Path $powerShellScriptsOut) {
    Remove-DirectoryRobust $powerShellScriptsOut
}

New-Item -ItemType Directory -Path $nativeOut -Force | Out-Null
New-Item -ItemType Directory -Path $powerShellScriptsOut -Force | Out-Null

Invoke-DotNetOrThrow @(
    "restore",
    "$solutionRoot/WindowsClientCenter.sln"
)

$projects = @(
    @{ Name = "Plugins.BitLockerAgent"; Tfm = "net8.0-windows" },
    @{ Name = "Plugins.DeviceOverview"; Tfm = "net8.0-windows" },
    @{ Name = "Plugins.DeviceActions"; Tfm = "net8.0-windows" },
    @{ Name = "Plugins.IntuneAgent"; Tfm = "net8.0-windows" },
    @{ Name = "Plugins.MecmAgent"; Tfm = "net8.0-windows" },
    @{ Name = "Plugins.PowerShellScripts"; Tfm = "net8.0-windows" },
    @{ Name = "Plugins.WindowsDefenderAgent"; Tfm = "net8.0-windows" },
    @{ Name = "Plugins.WindowsUpdateAgent"; Tfm = "net8.0-windows" }
)

foreach ($project in $projects) {
    $projectPath = "$solutionRoot/src/$($project.Name)/$($project.Name).csproj"
    Invoke-DotNetOrThrow @(
        "build",
        $projectPath,
        "-c",
        $Configuration,
        "--no-restore"
    )

    $binPath = "$solutionRoot/src/$($project.Name)/bin/$Configuration/$($project.Tfm)"
    if (-not (Test-Path $binPath)) {
        throw "Build output not found: $binPath"
    }

    Copy-Item "$binPath/*.dll" $nativeOut -Force
    Copy-Item "$binPath/*.json" $nativeOut -Force -ErrorAction SilentlyContinue
    if (Test-Path "$binPath/runtimes") {
        Copy-Item "$binPath/runtimes" "$nativeOut/runtimes" -Recurse -Force
    }
    if (Test-Path "$binPath/PSScripts") {
        Copy-Item "$binPath/PSScripts/*" $powerShellScriptsOut -Recurse -Force
    }
    if (Test-Path "$binPath/Scripts") {
        Copy-Item "$binPath/Scripts" "$nativeOut/Scripts" -Recurse -Force
    }

    $manifestPath = "$solutionRoot/src/$($project.Name)/$($project.Name).plugin.json"
    if (Test-Path $manifestPath) {
        Copy-Item $manifestPath $nativeOut -Force
    }
}

$sharedAssemblies = @(
    "$solutionRoot/src/Intune.Services/bin/$Configuration/net8.0/Intune.Services.dll",
    "$solutionRoot/src/Plugin.Abstractions/bin/$Configuration/net8.0/Plugin.Abstractions.dll"
)

foreach ($assemblyPath in $sharedAssemblies) {
    if (-not (Test-Path $assemblyPath)) {
        throw "Shared assembly not found: $assemblyPath"
    }

    Copy-Item $assemblyPath $nativeOut -Force
}

Write-Host "Native plugin staging completed. Output: $nativeOut"
Write-Host "PowerShell scripts staged to: $powerShellScriptsOut"
