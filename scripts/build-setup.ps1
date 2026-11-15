param(
    [Parameter(Mandatory)] [string]$Version,
    [string]$Releases = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts/releases'),
    [string]$PublishDir
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$build = Join-Path $root 'artifacts/setup-wizard'
$wizard = Join-Path $build 'FluxGet-Setup.exe'

if ($PublishDir) {
    Remove-Item $build -Recurse -Force -ErrorAction SilentlyContinue
    dotnet publish (Join-Path $root 'src/FluxGet.Setup') -c Release -r win-x64 -p:PublishAot=true -p:Version=$Version -o $build
    if ($LASTEXITCODE -ne 0) {
        Write-Warning 'Native AOT needs the Visual Studio C++ build tools; building a .NET-dependent wizard for local testing only.'
        Remove-Item $build -Recurse -Force -ErrorAction SilentlyContinue
        dotnet publish (Join-Path $root 'src/FluxGet.Setup') -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:Version=$Version -o $build
        if ($LASTEXITCODE -ne 0) { throw 'Building the setup wizard failed.' }
        New-Item -ItemType File (Join-Path $build '.not-native') -Force | Out-Null
    }
    New-Item -ItemType Directory -Force (Join-Path $PublishDir 'tools') | Out-Null
    Copy-Item $wizard (Join-Path $PublishDir 'tools/FluxGet-Setup.exe') -Force
    Write-Host 'Update window copied into the app'
    return
}

$payload = Join-Path $Releases 'FluxGet-win-Setup.exe'
if (-not (Test-Path $payload)) { throw "Missing $payload; run vpk pack first." }
if (-not (Test-Path $wizard)) { throw "Missing $wizard; run with -PublishDir first." }
if (Test-Path (Join-Path $build '.not-native')) {
    Write-Warning 'Skipping FluxGet-Setup.exe: the wizard was not built natively (needs the Visual Studio C++ build tools).'
    return
}
$out = Join-Path $Releases 'FluxGet-Setup.exe'
Copy-Item $wizard $out -Force
$length = (Get-Item $payload).Length
$stream = [IO.File]::Open($out, 'Append')
try {
    $source = [IO.File]::OpenRead($payload)
    try { $source.CopyTo($stream) } finally { $source.Dispose() }
    $trailer = [Text.Encoding]::ASCII.GetBytes('FGSETUP1') + [BitConverter]::GetBytes([long]$length)
    $stream.Write($trailer, 0, $trailer.Length)
}
finally { $stream.Dispose() }

if ($env:FLUXGET_SIGN_PFX -or $env:FLUXGET_AZURE_SIGN_FILE) {
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'sign-file.ps1') $out
    if ($LASTEXITCODE -ne 0) { throw 'Signing FluxGet-Setup.exe failed.' }
}
Write-Host "Setup wizard written to $out ($([math]::Round((Get-Item $out).Length / 1MB, 1)) MB)"
