param(
    [Parameter(Position = 0)]
    [string]$Command = "build",

    [Parameter(Position = 1)]
    [string]$Target = "src/Host.Wpf/Host.Wpf.csproj",

    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$RemainingArgs
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

$arguments = @($Command, $Target)

if (-not ($RemainingArgs -contains "-nologo")) {
    $arguments += "-nologo"
}

if (-not ($RemainingArgs -contains "-clp:ErrorsOnly")) {
    $arguments += "-clp:ErrorsOnly"
}

$normalizedTarget = $Target.Replace('\', '/')
if ($Command -eq "build" -and
    $normalizedTarget -eq "src/Host.Wpf/Host.Wpf.csproj" -and
    -not ($RemainingArgs | Where-Object { $_ -like "/p:StageNativePluginsOnBuild=*" })) {
    $arguments += "/p:StageNativePluginsOnBuild=false"
}

$arguments += $RemainingArgs

& dotnet @arguments
exit $LASTEXITCODE
