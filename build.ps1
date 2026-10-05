param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$Lidarr = Join-Path $Root "ext\Lidarr"
$Project = Join-Path $Root "src\Lidarr.Plugin.YandexMusic.Metadata\Lidarr.Plugin.YandexMusic.Metadata.csproj"
$Dist = Join-Path $Root "dist"
$Publish = Join-Path $Dist "YandexMusicMetadata"

function Assert-Exit([string]$Action) {
    if ($LASTEXITCODE -ne 0) { throw "$Action failed with exit code $LASTEXITCODE" }
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw ".NET SDK is not available in PATH."
}

if (-not (Test-Path (Join-Path $Lidarr "src\NzbDrone.Core\Lidarr.Core.csproj"))) {
    if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
        throw "Git is required to fetch the Lidarr plugins branch."
    }
    if (Test-Path $Lidarr) { Remove-Item -Recurse -Force $Lidarr }
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Lidarr) | Out-Null
    Write-Host "Cloning Lidarr plugins branch..." -ForegroundColor Cyan
    & git clone --depth 1 --branch plugins https://github.com/Lidarr/Lidarr.git $Lidarr
    Assert-Exit "git clone Lidarr@plugins"
}

if (Test-Path $Dist) { Remove-Item -Recurse -Force $Dist }
New-Item -ItemType Directory -Force -Path $Publish | Out-Null

Write-Host "Building Yandex Music Metadata against Lidarr@plugins..." -ForegroundColor Cyan
& dotnet publish $Project `
    --configuration $Configuration `
    --output $Publish `
    --property:NuGetAudit=false `
    --property:AssemblyVersion=1.0.0 `
    --property:FileVersion=1.0.0 `
    --property:Deterministic=true `
    --property:EnableAnalyzers=false `
    --property:TreatWarningsAsErrors=false
Assert-Exit "dotnet publish"

$dll = Join-Path $Publish "Lidarr.Plugin.YandexMusic.Metadata.dll"
if (-not (Test-Path $dll)) { throw "Plugin DLL was not produced: $dll" }

# Host assemblies must not be deployed in the plugin folder.
$forbidden = Get-ChildItem $Publish -File | Where-Object {
    $_.Name -eq "Lidarr.Core.dll" -or
    $_.Name -eq "Lidarr.Common.dll" -or
    $_.Name -eq "NLog.dll"
}
if ($forbidden) {
    $forbidden | Remove-Item -Force
}

$zip = Join-Path $Dist "Lidarr.Plugin.YandexMusic.Metadata-v0.2.7.net8.0.zip"
Compress-Archive -Path (Join-Path $Publish "*") -DestinationPath $zip -Force

Write-Host ""
Write-Host "Build completed." -ForegroundColor Green
Write-Host "DLL: $dll"
Write-Host "ZIP: $zip"
Write-Host "Install to a separate plugin folder, e.g. /config/plugins/Community/YandexMusicMetadata/."
