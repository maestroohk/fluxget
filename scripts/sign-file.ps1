param([Parameter(Mandatory)][string]$File)
$ErrorActionPreference = 'Stop'
$segments = $File -split '[\\/]'
if ($segments -contains 'tools') { Write-Host "skip third-party $File"; exit 0 }
& $env:FLUXGET_SIGNTOOL sign /fd sha256 /f $env:FLUXGET_SIGN_PFX /p $env:FLUXGET_SIGN_PASSWORD /tr $env:FLUXGET_TIMESTAMP_URL /td sha256 $File
exit $LASTEXITCODE
