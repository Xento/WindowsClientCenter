param(
    [string]$RepositoryRoot = "$PSScriptRoot/..",
    [string]$OutputPath = "$PSScriptRoot/../docs/third-party-nuget-packages.md",
    [string]$LicenseOutputDirectory = ""
)

$ErrorActionPreference = "Stop"

$RepositoryRoot = [IO.Path]::GetFullPath($RepositoryRoot)
$OutputPath = [IO.Path]::GetFullPath($OutputPath)
$sourceRoot = Join-Path $RepositoryRoot "src"

function Get-MetadataNodeText {
    param(
        [Parameter(Mandatory = $true)]
        [xml]$Nuspec,
        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    $node = $Nuspec.SelectSingleNode("//*[local-name()='metadata']/*[local-name()='$Name']")
    if ($null -eq $node) {
        return ""
    }

    return $node.InnerText.Trim()
}

function Convert-LegacyLicenseUrl {
    param([string]$License)

    if ([string]::IsNullOrWhiteSpace($License)) {
        return ""
    }

    if ($License -match "(?i)(dotnet/corefx.*/LICENSE|aka\.ms/deprecateLicenseUrl|LinkId=329770)") {
        return "MIT"
    }

    return $License
}

function Escape-MarkdownCell {
    param([string]$Value)

    return ($Value -replace "[\r\n]+", " " -replace "\|", "\|").Trim()
}

$assetFiles = Get-ChildItem -Path $sourceRoot -Filter "project.assets.json" -Recurse -File |
    Where-Object { $_.FullName -match "[\\/]obj[\\/]project\.assets\.json$" }

if ($assetFiles.Count -eq 0) {
    throw "No restored project.assets.json files were found below '$sourceRoot'. Run dotnet restore first."
}

$packages = @{}

foreach ($assetFile in $assetFiles) {
    $assets = Get-Content -LiteralPath $assetFile.FullName -Raw | ConvertFrom-Json
    $packageFolders = @($assets.packageFolders.PSObject.Properties.Name)

    foreach ($libraryProperty in $assets.libraries.PSObject.Properties) {
        if ($libraryProperty.Value.type -ne "package") {
            continue
        }

        $separator = $libraryProperty.Name.LastIndexOf('/')
        if ($separator -le 0) {
            throw "Unexpected package key '$($libraryProperty.Name)' in '$($assetFile.FullName)'."
        }

        $id = $libraryProperty.Name.Substring(0, $separator)
        $version = $libraryProperty.Name.Substring($separator + 1)
        $packageKey = "$id/$version"
        if ($packages.ContainsKey($packageKey)) {
            continue
        }

        $packageDirectory = $null
        foreach ($packageFolder in $packageFolders) {
            $candidate = Join-Path $packageFolder (Join-Path $id.ToLowerInvariant() $version)
            if (Test-Path -LiteralPath $candidate) {
                $packageDirectory = $candidate
                break
            }
        }

        if ($null -eq $packageDirectory) {
            throw "Restored package directory not found for '$packageKey'."
        }

        $nuspecFile = Get-ChildItem -LiteralPath $packageDirectory -Filter "*.nuspec" -File |
            Select-Object -First 1
        if ($null -eq $nuspecFile) {
            throw "NuSpec metadata not found for '$packageKey'."
        }

        [xml]$nuspec = Get-Content -LiteralPath $nuspecFile.FullName -Raw
        $licenseNode = $nuspec.SelectSingleNode("//*[local-name()='metadata']/*[local-name()='license']")
        $license = if ($null -eq $licenseNode) { "" } else { $licenseNode.InnerText.Trim() }
        $declaredLicenseFile = ""
        if ($null -ne $licenseNode -and $licenseNode.GetAttribute("type") -eq "file") {
            $declaredLicenseFile = $license
            $declaredLicensePath = Join-Path $packageDirectory $declaredLicenseFile
            if (-not (Test-Path -LiteralPath $declaredLicensePath)) {
                throw "Package '$packageKey' declares missing license file '$declaredLicenseFile'."
            }
            $license = "Package license file ($declaredLicenseFile)"
        }
        elseif ([string]::IsNullOrWhiteSpace($license)) {
            $license = Get-MetadataNodeText -Nuspec $nuspec -Name "licenseUrl"
        }
        $license = Convert-LegacyLicenseUrl -License $license

        if ([string]::IsNullOrWhiteSpace($license)) {
            throw "Package '$packageKey' does not declare license metadata. Review it before release."
        }

        $packages[$packageKey] = [pscustomobject]@{
            Id = $id
            Version = $version
            License = $license
            Authors = Get-MetadataNodeText -Nuspec $nuspec -Name "authors"
            ProjectUrl = Get-MetadataNodeText -Nuspec $nuspec -Name "projectUrl"
            PackageDirectory = $packageDirectory
            DeclaredLicenseFile = $declaredLicenseFile
        }
    }
}

$lines = [Collections.Generic.List[string]]::new()
$lines.Add("# Production NuGet Package License Inventory")
$lines.Add("")
$lines.Add("This file is generated from the restored ``src/**/obj/project.assets.json`` files by")
$lines.Add("``scripts/generate-third-party-package-notices.ps1``. Project references and test-only")
$lines.Add("packages are excluded. Regenerate and review this file whenever dependencies change.")
$lines.Add("")
$lines.Add("| Package | Version | License | Authors | Project |")
$lines.Add("|---|---:|---|---|---|")

foreach ($package in $packages.Values | Sort-Object Id, Version) {
    $id = Escape-MarkdownCell $package.Id
    $version = Escape-MarkdownCell $package.Version
    $license = Escape-MarkdownCell $package.License
    $authors = Escape-MarkdownCell $package.Authors
    $projectUrl = Escape-MarkdownCell $package.ProjectUrl
    $project = if ([string]::IsNullOrWhiteSpace($projectUrl)) { "" } else { "[upstream]($projectUrl)" }
    $lines.Add("| $id | $version | $license | $authors | $project |")
}

$outputDirectory = Split-Path -Parent $OutputPath
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
[IO.File]::WriteAllLines($OutputPath, $lines, [Text.UTF8Encoding]::new($false))

if (-not [string]::IsNullOrWhiteSpace($LicenseOutputDirectory)) {
    $LicenseOutputDirectory = [IO.Path]::GetFullPath($LicenseOutputDirectory)
    New-Item -ItemType Directory -Path $LicenseOutputDirectory -Force | Out-Null

    foreach ($package in $packages.Values) {
        $noticeFiles = @(Get-ChildItem -LiteralPath $package.PackageDirectory -Recurse -File |
            Where-Object {
                $_.Extension -ne ".nupkg" -and
                $_.Name -match "(?i)^(license|copying|notice|third[-_]?party[-_]?notices?)(\..*)?$"
            })

        if (-not [string]::IsNullOrWhiteSpace($package.DeclaredLicenseFile)) {
            $declaredPath = Join-Path $package.PackageDirectory $package.DeclaredLicenseFile
            if ($noticeFiles.FullName -notcontains $declaredPath) {
                $noticeFiles += Get-Item -LiteralPath $declaredPath
            }
        }

        if ($noticeFiles.Count -eq 0) {
            continue
        }

        $packageNoticeDirectory = Join-Path $LicenseOutputDirectory (Join-Path $package.Id $package.Version)
        New-Item -ItemType Directory -Path $packageNoticeDirectory -Force | Out-Null
        foreach ($noticeFile in $noticeFiles) {
            $relativeNoticePath = [IO.Path]::GetRelativePath($package.PackageDirectory, $noticeFile.FullName)
            $noticeDestination = Join-Path $packageNoticeDirectory $relativeNoticePath
            New-Item -ItemType Directory -Path (Split-Path -Parent $noticeDestination) -Force | Out-Null
            Copy-Item -LiteralPath $noticeFile.FullName -Destination $noticeDestination -Force
        }
    }
}

Write-Host "Third-party package inventory written to: $OutputPath"
