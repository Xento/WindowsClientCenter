param(
    [string]$Configuration = "Release",
    [string]$OutputRoot = "$PSScriptRoot/../artifacts/package",
    [string]$NativeArtifacts = "$PSScriptRoot/../artifacts/native",
    [string]$RuntimeIdentifier = "win-x64",
    [string]$RuntimeFrameworkVersion = "8.0.29"
)

$ErrorActionPreference = "Stop"

$solutionRoot = Resolve-Path "$PSScriptRoot/.."
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
$NativeArtifacts = [IO.Path]::GetFullPath($NativeArtifacts)
$nativeStageScript = "$solutionRoot/scripts/stage-native-plugins.ps1"
$packageNoticeScript = "$solutionRoot/scripts/generate-third-party-package-notices.ps1"

function Invoke-DotNetOrThrow {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)

    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

function Copy-SourceTree {
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Destination
    )

    foreach ($file in Get-ChildItem -LiteralPath $Source -Recurse -File) {
        $relativePath = [IO.Path]::GetRelativePath($Source, $file.FullName)
        $segments = $relativePath -split '[\\/]'
        if ($segments -contains "bin" -or $segments -contains "obj") {
            continue
        }

        $destinationPath = Join-Path $Destination $relativePath
        New-Item -ItemType Directory -Path (Split-Path -Parent $destinationPath) -Force | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $destinationPath -Force
    }
}

if (Test-Path $OutputRoot) {
    Remove-Item -Path $OutputRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $OutputRoot | Out-Null

$hostOut = "$OutputRoot/host"

Write-Host "Staging native plugin artifacts"
& $nativeStageScript -Configuration $Configuration -OutputRoot $NativeArtifacts

Write-Host "Publishing self-contained Host.Wpf"
Invoke-DotNetOrThrow @(
    "publish",
    "$solutionRoot/src/Host.Wpf/Host.Wpf.csproj",
    "-c", $Configuration,
    "-r", $RuntimeIdentifier,
    "--self-contained", "true",
    "-p:PublishSingleFile=false",
    "-p:PublishTrimmed=false",
    "-p:RuntimeFrameworkVersion=$RuntimeFrameworkVersion",
    "-p:StageNativePluginsOnBuild=false",
    "-o", $hostOut
)

Write-Host "Copying plugin artifacts"
New-Item -ItemType Directory -Path "$hostOut/plugins/native" -Force | Out-Null
New-Item -ItemType Directory -Path "$hostOut/PSScripts" -Force | Out-Null

$nativeSource = "$NativeArtifacts/plugins/native"
$powerShellScriptsSource = "$NativeArtifacts/PSScripts"
if (-not (Test-Path $nativeSource)) {
    throw "Native artifacts missing: $nativeSource"
}
if (-not (Test-Path $powerShellScriptsSource)) {
    throw "PowerShell scripts missing: $powerShellScriptsSource"
}

Copy-Item "$nativeSource/*" "$hostOut/plugins/native" -Recurse -Force
Copy-Item "$powerShellScriptsSource/*" "$hostOut/PSScripts" -Recurse -Force

Write-Host "Copying license and attribution material"
$requiredRepositoryFiles = @(
    "$solutionRoot/LICENSE",
    "$solutionRoot/THIRD-PARTY-NOTICES.md",
    "$solutionRoot/SOURCE-CODE.md",
    "$solutionRoot/LICENSES",
    "$solutionRoot/docs/attribution",
    "$solutionRoot/src/SccmCliCtr.Automation/LICENSE.md",
    "$solutionRoot/src/SccmCliCtr.Automation/MODIFICATIONS.md"
)

foreach ($requiredFile in $requiredRepositoryFiles) {
    if (-not (Test-Path -LiteralPath $requiredFile)) {
        throw "Required release license material is missing: $requiredFile"
    }
}

Copy-Item -LiteralPath "$solutionRoot/LICENSE" -Destination "$hostOut/LICENSE" -Force
Copy-Item -LiteralPath "$solutionRoot/THIRD-PARTY-NOTICES.md" -Destination "$hostOut/THIRD-PARTY-NOTICES.md" -Force
Copy-Item -LiteralPath "$solutionRoot/SOURCE-CODE.md" -Destination "$hostOut/SOURCE-CODE.md" -Force
Copy-Item -LiteralPath "$solutionRoot/LICENSES" -Destination "$hostOut/LICENSES" -Recurse -Force
Copy-Item -LiteralPath "$solutionRoot/docs/attribution" -Destination "$hostOut/attribution" -Recurse -Force
Copy-Item -LiteralPath "$solutionRoot/src/SccmCliCtr.Automation/LICENSE.md" -Destination "$hostOut/LICENSES/SccmCliCtr.Automation-LGPL.md" -Force
Copy-Item -LiteralPath "$solutionRoot/src/SccmCliCtr.Automation/MODIFICATIONS.md" -Destination "$hostOut/LICENSES/SccmCliCtr.Automation-MODIFICATIONS.md" -Force

& $packageNoticeScript `
    -RepositoryRoot $solutionRoot `
    -OutputPath "$hostOut/LICENSES/NUGET-PACKAGES.md" `
    -LicenseOutputDirectory "$hostOut/LICENSES/NUGET"

$hostExecutable = "$hostOut/WindowsClientCenter.exe"
if (-not (Test-Path -LiteralPath $hostExecutable)) {
    throw "Published Windows executable is missing: $hostExecutable"
}

$lgplAssemblies = @(Get-ChildItem -LiteralPath $hostOut -Filter "sccmclictr.automation.dll" -Recurse -File)
if ($lgplAssemblies.Count -eq 0) {
    throw "The LGPL assembly was not found as a separate replaceable DLL in the package."
}

Write-Host "Creating corresponding LGPL source artifact"
$lgplSourceOut = "$OutputRoot/WindowsClientCenter-LGPL-source"
New-Item -ItemType Directory -Path "$lgplSourceOut/src/SccmCliCtr.Automation" -Force | Out-Null
Copy-SourceTree -Source "$solutionRoot/src/SccmCliCtr.Automation" -Destination "$lgplSourceOut/src/SccmCliCtr.Automation"
Copy-Item -LiteralPath "$solutionRoot/Directory.Build.props" -Destination "$lgplSourceOut/Directory.Build.props" -Force
Copy-Item -LiteralPath "$solutionRoot/NuGet.Config" -Destination "$lgplSourceOut/NuGet.Config" -Force
Copy-Item -LiteralPath "$solutionRoot/SOURCE-CODE.md" -Destination "$lgplSourceOut/README.md" -Force
Copy-Item -LiteralPath "$solutionRoot/LICENSES" -Destination "$lgplSourceOut/LICENSES" -Recurse -Force

$sourceZipPath = "$OutputRoot/WindowsClientCenter-LGPL-source.zip"
Compress-Archive -Path "$lgplSourceOut/*" -DestinationPath $sourceZipPath

$zipPath = "$OutputRoot/WindowsClientCenter-$RuntimeIdentifier.zip"
Compress-Archive -Path "$hostOut/*" -DestinationPath $zipPath

Write-Host "Package layout ready: $OutputRoot"
Write-Host "Binary ZIP: $zipPath"
Write-Host "LGPL source ZIP: $sourceZipPath"
