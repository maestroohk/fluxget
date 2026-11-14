param(
    [string]$Version = '0.1.0',
    [string]$Runtime = 'win-x64',
    [switch]$SkipTools
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
$publish = Join-Path $root "artifacts/publish/$Runtime"
$releases = Join-Path $root 'artifacts/releases'
Remove-Item $publish -Recurse -Force -ErrorAction SilentlyContinue

Push-Location web
npm ci --no-audit --no-fund
npm run build
Pop-Location

if (-not $SkipTools -or -not (Test-Path 'tools/bin/yt-dlp.exe')) { & "$PSScriptRoot/fetch-tools.ps1" }

dotnet publish src/FluxGet.Desktop -c Release -r $Runtime --self-contained true -p:Version=$Version -o $publish
New-Item -ItemType Directory -Force (Join-Path $publish 'tools') | Out-Null
Copy-Item 'tools/bin/*' (Join-Path $publish 'tools') -Recurse -Force
Copy-Item LICENSE, THIRD-PARTY-NOTICES.md $publish -Force

dotnet tool update -g vpk --version 1.2.161 | Out-Host
vpk pack --packId FluxGet --packVersion $Version --packDir $publish --mainExe FluxGet.exe --packTitle FluxGet --icon src/FluxGet.Desktop/fluxget.ico --outputDir $releases
Write-Host "Installer written to $releases"
