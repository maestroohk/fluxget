param(
    [string]$Version = '0.1.0',
    [string]$Runtime = 'win-x64',
    [switch]$SkipTools,
    [string]$SignPfx = $env:FLUXGET_SIGN_PFX,
    [string]$SignPassword = $env:FLUXGET_SIGN_PASSWORD,
    [string]$AzureTrustedSignFile = $env:FLUXGET_AZURE_SIGN_FILE,
    [string]$TimestampUrl = 'http://timestamp.digicert.com'
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

if (-not $SkipTools -or -not (Test-Path 'tools/bin/yt-dlp.exe') -or -not (Test-Path 'tools/bin/gallery-dl.exe')) { & "$PSScriptRoot/fetch-tools.ps1" }

dotnet publish src/FluxGet.Desktop -c Release -r $Runtime --self-contained true -p:Version=$Version -o $publish
New-Item -ItemType Directory -Force (Join-Path $publish 'tools') | Out-Null
Copy-Item 'tools/bin/*' (Join-Path $publish 'tools') -Recurse -Force
Copy-Item LICENSE, THIRD-PARTY-NOTICES.md $publish -Force
Copy-Item extension (Join-Path $publish 'extension') -Recurse -Force
Get-ChildItem (Join-Path $publish 'runtimes') -Directory -ErrorAction SilentlyContinue | Where-Object { $_.Name -ne 'win-x64' } | Remove-Item -Recurse -Force

dotnet tool update -g vpk --version 1.2.161 | Out-Host
$signArgs = @()
if ($AzureTrustedSignFile) {
    $signArgs = @('--azureTrustedSignFile', $AzureTrustedSignFile)
    Write-Host 'Signing with Azure Trusted Signing'
}
elseif ($SignPfx) {
    if (-not $SignPassword -and (Test-Path ([IO.Path]::ChangeExtension($SignPfx, '.pass')))) { $SignPassword = (Get-Content ([IO.Path]::ChangeExtension($SignPfx, '.pass')) -Raw).Trim() }
    $env:FLUXGET_SIGN_PFX = (Resolve-Path $SignPfx).Path
    $env:FLUXGET_SIGN_PASSWORD = $SignPassword
    $env:FLUXGET_TIMESTAMP_URL = $TimestampUrl
    $env:FLUXGET_SIGNTOOL = Get-ChildItem (Join-Path $env:USERPROFILE '.dotnet/tools/.store/vpk') -Recurse -Filter signtool.exe | Select-Object -First 1 -ExpandProperty FullName
    $signer = Join-Path $PSScriptRoot 'sign-file.ps1'
    $signArgs = @('--signTemplate', "powershell -NoProfile -ExecutionPolicy Bypass -File `"$signer`" `"{{file}}`"")
    Write-Host "Signing with certificate $SignPfx"
}
else {
    Write-Warning 'No signing certificate configured: the installer will be unsigned.'
}

& (Join-Path $PSScriptRoot 'build-setup.ps1') -Version $Version -PublishDir $publish

vpk pack --packId FluxGet --packVersion $Version --packDir $publish --mainExe FluxGet.exe --packTitle FluxGet --icon src/FluxGet.Desktop/fluxget.ico --splashImage src/FluxGet.Desktop/installer-splash.png --packAuthors FluxGet --outputDir $releases @signArgs
if ($LASTEXITCODE -ne 0) { throw "vpk pack failed with exit code $LASTEXITCODE" }

& (Join-Path $PSScriptRoot 'build-setup.ps1') -Version $Version -Releases $releases

$extensionZip = Join-Path $releases "FluxGet-extension-$Version.zip"
Remove-Item $extensionZip -ErrorAction SilentlyContinue
Compress-Archive -Path (Join-Path $root 'extension/*') -DestinationPath $extensionZip
Write-Host "Installer and extension written to $releases"
