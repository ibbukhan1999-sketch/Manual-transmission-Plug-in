param(
    [Parameter(Mandatory = $true)]
    [string]$ETS2LASourceRoot
)

$ErrorActionPreference = 'Stop'
$sourceRoot = (Resolve-Path -LiteralPath $ETS2LASourceRoot).Path
$hostProject = Join-Path $sourceRoot 'ETS2LA.Game\ETS2LA.Game.csproj'
if (-not (Test-Path -LiteralPath $hostProject)) {
    throw "ETS2LASourceRoot does not contain ETS2LA.Game: $sourceRoot"
}

$repoRoot = $PSScriptRoot
$libraryProject = Join-Path $repoRoot 'Libraries\Godspeed.Shared\Godspeed.Shared.csproj'
$pluginProject = Join-Path $repoRoot 'Plugins\ManualTransmission\ManualTransmission.csproj'
$buildArgs = @('-c', 'Release', '-p:Platform=x64', "-p:ETS2LASourceRoot=$sourceRoot")

& dotnet build $libraryProject @buildArgs '-p:GodspeedDeploymentInProgress=true'
if ($LASTEXITCODE -ne 0) { throw 'Godspeed.Shared build failed.' }

& dotnet build $pluginProject @buildArgs
if ($LASTEXITCODE -ne 0) { throw 'ManualTransmission build or deployment failed.' }

Write-Output 'ManualTransmission deployed. The radar-free Godspeed.Shared library was installed only if no library was already present. Restart ETS2LA before testing.'
