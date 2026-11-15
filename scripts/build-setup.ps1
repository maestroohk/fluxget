param(
    [Parameter(Mandatory)] [string]$Version,
    [string]$Releases = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts/releases')
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$payload = Join-Path $Releases 'FluxGet-win-Setup.exe'
if (-not (Test-Path $payload)) { throw "Missing $payload; run vpk pack first." }
$build = Join-Path $root 'artifacts/setup-wizard'
Remove-Item $build -Recurse -Force -ErrorAction SilentlyContinue

dotnet publish (Join-Path $root 'src/FluxGet.Setup') -c Release -r win-x64 -p:PublishAot=true -p:Version=$Version -o $build
if ($LASTEXITCODE -ne 0) {
    Write-Warning 'The setup wizard needs the Visual Studio C++ build tools (native AOT). Skipping FluxGet-Setup.exe.'
    return
}

$out = Join-Path $Releases 'FluxGet-Setup.exe'
Copy-Item (Join-Path $build 'FluxGet-Setup.exe') $out -Force
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
