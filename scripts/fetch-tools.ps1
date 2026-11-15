param(
    [string]$Destination = (Join-Path (Split-Path -Parent $PSScriptRoot) 'tools/bin'),
    [string]$YtDlpVersion = '2026.10.26',
    [string]$DenoVersion = '2.9.8',
    [string]$GalleryDlVersion = '1.32.20',
    [string]$FfmpegAsset = 'ffmpeg-n9.0-latest-win64-lgpl-shared-9.0.zip'
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
New-Item -ItemType Directory -Force $Destination | Out-Null
$work = Join-Path ([IO.Path]::GetTempPath()) ("fluxget-tools-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $work | Out-Null

function Get-Verified([string]$Url, [string]$SumsUrl, [string]$Name, [string]$OutFile) {
    Invoke-WebRequest $Url -OutFile $OutFile -UseBasicParsing
    $sums = (Invoke-WebRequest $SumsUrl -UseBasicParsing).Content
    if ($sums -is [byte[]]) { $sums = [Text.Encoding]::UTF8.GetString($sums) }
    $line = ($sums -split "`n") | Where-Object { $_ -match [regex]::Escape($Name) -and $_ -match '[0-9a-fA-F]{64}' } | Select-Object -First 1
    $all = [regex]::Matches($sums, '\b[0-9a-fA-F]{64}\b')
    $expected = if ($line) { ([regex]::Match($line, '[0-9a-fA-F]{64}')).Value } elseif ($all.Count -eq 1) { $all[0].Value } else { '' }
    $expected = $expected.ToLowerInvariant()
    $actual = (Get-FileHash $OutFile -Algorithm SHA256).Hash.ToLowerInvariant()
    if (-not $expected -or $expected -ne $actual) { throw "Checksum mismatch for $Name (expected '$expected', got '$actual')" }
    Write-Host "verified $Name $actual"
}

try {
    Get-Verified "https://github.com/yt-dlp/yt-dlp/releases/download/$YtDlpVersion/yt-dlp.exe" `
        "https://github.com/yt-dlp/yt-dlp/releases/download/$YtDlpVersion/SHA2-256SUMS" 'yt-dlp.exe' (Join-Path $Destination 'yt-dlp.exe')

    $gdl = "https://codeberg.org/mikf/gallery-dl/releases/download/v$GalleryDlVersion"
    Get-Verified "$gdl/gallery-dl.exe" "$gdl/SHA256SUMS" 'gallery-dl.exe' (Join-Path $Destination 'gallery-dl.exe')
    Get-Verified "$gdl/gallery_dl-$GalleryDlVersion.tar.gz" "$gdl/SHA256SUMS" "gallery_dl-$GalleryDlVersion.tar.gz" (Join-Path $Destination "gallery-dl-source-$GalleryDlVersion.tar.gz")
    Invoke-WebRequest "https://codeberg.org/mikf/gallery-dl/raw/tag/v$GalleryDlVersion/LICENSE" -OutFile (Join-Path $Destination 'GALLERY-DL-LICENSE.txt') -UseBasicParsing

    $denoZip = Join-Path $work 'deno.zip'
    Get-Verified "https://github.com/denoland/deno/releases/download/v$DenoVersion/deno-x86_64-pc-windows-msvc.zip" `
        "https://github.com/denoland/deno/releases/download/v$DenoVersion/deno-x86_64-pc-windows-msvc.zip.sha256sum" 'deno-x86_64-pc-windows-msvc.zip' $denoZip
    Expand-Archive $denoZip -DestinationPath $Destination -Force

    $ffZip = Join-Path $work 'ffmpeg.zip'
    Get-Verified "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/$FfmpegAsset" `
        "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/checksums.sha256" $FfmpegAsset $ffZip
    Expand-Archive $ffZip -DestinationPath $work -Force
    $ffRoot = Get-ChildItem $work -Directory | Where-Object { Test-Path (Join-Path $_.FullName 'bin/ffmpeg.exe') } | Select-Object -First 1
    Remove-Item (Join-Path $Destination 'ffmpeg.exe'), (Join-Path $Destination 'ffprobe.exe') -ErrorAction SilentlyContinue
    Get-ChildItem (Join-Path $ffRoot.FullName 'bin') | Where-Object { $_.Name -ne 'ffplay.exe' } | Copy-Item -Destination $Destination -Force
    Copy-Item (Join-Path $ffRoot.FullName 'LICENSE.txt') (Join-Path $Destination 'FFMPEG-LICENSE.txt') -Force
    Write-Host "Tools ready in $Destination"
}
finally {
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}

