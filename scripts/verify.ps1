param([switch]$SkipWeb)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
$results = Join-Path $root 'artifacts/test-results'
Remove-Item $results -Recurse -Force -ErrorAction SilentlyContinue
dotnet test FluxGet.slnx -c Release --logger "trx;LogFileName=engine.trx" --results-directory $results | Out-Host
$testExit = $LASTEXITCODE
$webExit = 0
if (-not $SkipWeb) {
    Push-Location web
    npm ci --no-audit --no-fund | Out-Host
    npm run build | Out-Host
    $webExit = $LASTEXITCODE
    Pop-Location
}
$total = 0; $passed = 0; $failed = 0
foreach ($trx in Get-ChildItem $results -Filter *.trx -Recurse) {
    $c = ([xml](Get-Content $trx.FullName)).TestRun.ResultSummary.Counters
    $total += [int]$c.total; $passed += [int]$c.passed; $failed += [int]$c.failed
}
$commit = (git rev-parse --short HEAD 2>$null)
$status = [ordered]@{
    result = if ($testExit -eq 0 -and $webExit -eq 0) { 'passed' } else { 'failed' }
    at = (Get-Date).ToUniversalTime().ToString('yyyy-MM-dd HH:mm') + ' UTC'
    commit = $commit
    tests = [ordered]@{ total = $total; passed = $passed; failed = $failed }
    webBuild = if ($SkipWeb) { 'skipped' } elseif ($webExit -eq 0) { 'passed' } else { 'failed' }
}
[IO.File]::WriteAllText((Join-Path $root 'docs/build-status.json'), ($status | ConvertTo-Json))
Write-Host "Tests: $passed/$total passed, web build: $($status.webBuild)"
if ($status.result -ne 'passed') { exit 1 }
