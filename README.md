# FluxGet

A fast, reliable download manager for Windows: segmented HTTP downloads that survive crashes, media downloads from video sites, BitTorrent, and a browser extension — in one calm, modern app.

- **Direct downloads** split across connections when the server allows it, resume safely after pause, restart, crash or network loss, and never mix bytes from two versions of a file.
- **Media** from YouTube and other sites supported by [yt-dlp](https://github.com/yt-dlp/yt-dlp): pick a real resolution, container or audio format; whole playlists and channels; audio extraction and conversion with FFmpeg (stream copy when no re-encode is needed).
- **Torrents** via [MonoTorrent](https://github.com/alanmcgovern/monotorrent): magnet links and .torrent files, file selection, peers, upload limit, seed ratio.
- **Browser extension** (Chrome, Edge, Brave, Opera, Firefox): with "Detect video and audio playing in browsers" on (Settings, on by default) a small FluxGet button appears on any video or audio that plays, with download options and "Play in FluxGet"; it also sends links and captured downloads after a one-time pairing.
- **Built-in player** (LibVLC): plays finished downloads and local files, MP4/MKV/WebM/AVI and MP3/FLAC/M4A/Opus, embedded and external subtitles (SRT, ASS, VTT), audio tracks, speed 0.25–4×, frame stepping, picture controls, fullscreen, an always-on-top mini player, queue, resume and a media library. Partially downloaded files can be previewed when their beginning is on disk.
- **Photos, galleries and page files**: Instagram/X/Reddit/Pinterest/Tumblr/Flickr posts and carousels through [gallery-dl](https://codeberg.org/mikf/gallery-dl); on any other page FluxGet lists the images, videos, audio, documents and archives it links to, lets you pick, and downloads them with the resumable downloader into a folder named after the page.
- **Streaming**: paste a YouTube (or other supported site) link on the Player screen, or press "Stream" on the Media page or "Play in FluxGet" in the browser, and it plays straight from the site without downloading — up to 1080p by default, with a quality menu, fast seeking and an automatic step down when the connection cannot keep up.
- **Live subtitles**: switch on the CC menu's "Live subtitles" (or press C) and FluxGet transcribes the audio a little ahead of playback, for files and streams, entirely on this computer.
- **Offline subtitles**: generates subtitles with whisper.cpp on your own computer (no upload, no account), optionally translated to English; the speech model downloads once (75–466 MB).
- **Queue, schedule and bandwidth control**: concurrency limit, priorities, time windows, global and per-download speed limits, checksum verification.

Architecture, diagrams and decisions: [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md), [docs/DECISIONS.md](docs/DECISIONS.md). Measured performance: [bench/results.md](bench/results.md).

## Run it

Requirements for development: .NET SDK 10, Node.js 22, Windows 10/11 for the desktop app (the engine and headless host also run on Linux).

```bash
cd web && npm ci && npm run build && cd ..
dotnet run --project src/FluxGet.Desktop
```

The desktop app starts its local service on `127.0.0.1:17890`, opens the UI and signs it in automatically. Closing the window keeps downloads running in the tray.

Media downloads need `yt-dlp`, `ffmpeg`/`ffprobe` and a JavaScript runtime (`deno`, or `node`) on `PATH` or in `tools/bin`. `scripts/fetch-tools.ps1` downloads checksum-verified copies into `tools/bin`; the installer bundles them.

Headless (web interface in a browser):

```bash
dotnet run --project src/FluxGet.Host
```

It prints the address and an access key to paste into the login screen. Add `--FluxGet:DevDashboard=true` to serve the development progress dashboard at `/dev/`.

For UI work, run `npm run dev` in `web/` (Vite on port 5173, proxied to the host) and start the host with `--FluxGet:DevDashboard=true` so the dev origin is accepted.

## Windows installer

```bash
powershell -ExecutionPolicy Bypass -File scripts/package.ps1 -Version 0.1.0
```

Produces a per-user Velopack installer (`artifacts/releases/FluxGet-win-Setup.exe`, about 250 MB; the media player's LibVLC engine accounts for most of the increase over 0.3), a portable zip, the update feed and `FluxGet-extension-<version>.zip`. It installs to `%LOCALAPPDATA%\FluxGet` with Start menu and desktop shortcuts; downloads, settings and the database live separately in `%LOCALAPPDATA%\FluxGetData`, so uninstalling or updating never deletes them.

### Code signing

`package.ps1` signs FluxGet's own executables and DLLs plus `Setup.exe` with SHA-256 and an RFC 3161 timestamp. Bundled third-party tools (yt-dlp, FFmpeg, Deno) are left exactly as their publishers ship them.

```bash
powershell -ExecutionPolicy Bypass -File scripts/package.ps1 -SignPfx C:\path	o\certificate.pfx
```

The password comes from `-SignPassword`, `FLUXGET_SIGN_PASSWORD`, or a `.pass` file next to the PFX. For Azure Trusted Signing set `FLUXGET_AZURE_SIGN_FILE` (Velopack's metadata JSON) instead; that mode signs every binary.

Windows only stops showing SmartScreen warnings for a certificate it trusts. Free routes for this project:

- **SignPath Foundation** signs open-source projects for free with its own trusted certificate (requirements: OSI licence, public repository, MFA for all members, a published code signing policy — see [docs/CODE_SIGNING_POLICY.md](docs/CODE_SIGNING_POLICY.md)). Apply at signpath.org.
- **Microsoft Store**: individual developer registration is free and the Store signs MSIX packages itself.
- Until then, `scripts/new-dev-cert.ps1` creates a self-signed test certificate (a PFX file only, never added to a Windows trust store) to exercise the pipeline. Installers signed with it show the signer as untrusted.

## Browser extension

Settings → *Use FluxGet in your browser* walks through each option and has an **Open folder** button for the installed extension.

**Chrome, Edge, Brave, Opera, Vivaldi**

1. Open `chrome://extensions` (or `edge://`, `brave://`, `opera://extensions`), enable Developer mode, choose **Load unpacked** and select the `extension` folder (installed next to FluxGet, or `extension/` in this repository).
2. Click the FluxGet toolbar icon → **Connect**. FluxGet shows a 6-digit code; type it into the extension.

**Firefox 128+**: the same extension loads from `about:debugging` → *Load Temporary Add-on* (until Firefox restarts). A permanent install needs Mozilla's free signing on addons.mozilla.org.

**Any browser, no extension**: drag the *Send to FluxGet* button from Settings to the bookmarks bar. Clicking it on a video page opens FluxGet's quality picker; on other pages it adds a download.

On YouTube and other supported video sites, hover over a video: a small **Download** button appears on the player. Click it to see the real qualities with sizes (4K, 1440p, 1080p, 720p…) and audio options (MP3 192/320 kbps, M4A). Pick one and the download starts in FluxGet, with live progress in the panel and **Show in folder** / **Open** when it finishes. The button can be turned off in the extension settings, and it is never shown on DRM-protected players.

Besides the video sites, the extension only gets the context menu, the toolbar popup and (optional) download capture. Detecting media files on pages, the on-video download button and passing site cookies each ask for extra permission when you turn them on. Cookies are sent only for the URL being downloaded and are kept in memory, never written to disk or logs.

## Remote worker (Docker)

```bash
cp .env.example .env   # set FLUXGET_ACCESS_TOKEN to a long random secret
docker compose up -d --build
```

Serves the web UI on `127.0.0.1:8080` (put a TLS reverse proxy in front for remote access). Hosted mode blocks downloads from private and loopback addresses (SSRF protection: direct downloads are checked on every connection, including after redirects; media links are checked before yt-dlp runs, but redirects inside yt-dlp are not), restricts files to `/downloads`, runs as a non-root user with a read-only root filesystem, and requires the access token. It is single-user by design.

## Supported sites

Verified against live sites on 2026-11-14:

| Source | Status |
|---|---|
| Direct HTTP/HTTPS files | Verified: segmented download from a CDN with SHA-256 check (nodejs.org), resume, adaptive connection limits |
| YouTube videos | Verified: real format list, 360p/720p MP4, H.264 preferred for MP4 compatibility |
| YouTube audio | Verified: MP3 192 kbps with cover art, M4A by stream copy, Opus, FLAC |
| YouTube channels/playlists | Verified: paged listing (136 videos in pages of 100) and per-item jobs |
| Vimeo | Requires signing in: yt-dlp needs browser cookies; use the extension with "Pass my site cookies" |
| Facebook, Instagram, TikTok, X, Reddit, Twitch, others | Supported by yt-dlp; not yet verified in FluxGet. Many posts require a signed-in browser session. |
| BitTorrent | Verified: archive.org public-domain torrent, file selection, completion and ratio stop |

FluxGet never bypasses DRM, paywalls or sign-in. Private media works only when your own browser session is allowed to view it. Watermark-free downloads are only possible when the site itself serves such a file.

## Security model

- The local API listens on loopback only. Every call needs a token; the desktop UI uses an HttpOnly, SameSite=Strict cookie injected by the app.
- Web pages cannot drive it: requests carrying an `http(s)` Origin are rejected, the Host header must be `127.0.0.1`/`localhost` (DNS-rebinding defence), and pairing requires the code shown in the app. Extension tokens are stored hashed and can be revoked in Settings.
- Subprocesses (yt-dlp, FFmpeg) are started with argument lists, never a shell. File names are sanitised and confined to the download folder.
- CI runs NuGet and npm vulnerability scans.

BitTorrent shares your IP address with peers. FluxGet does not provide anonymity.

## Tests

```bash
dotnet test tests/FluxGet.Engine.Tests
cd e2e && npm ci && node extension.test.mjs
```

The engine tests run against a deliberately misbehaving local HTTP server (ignored ranges, wrong `Content-Range`, dropped connections, throttling, connection limits, changed files, expired links). The e2e test loads the real extension into Microsoft Edge and drives pairing, download capture and the popup against a real FluxGet host.

The player is tested through the real Windows UI with `FluxGet.exe --player-selftest <samples folder> <output folder>`, which plays sample files, switches fullscreen and mini player, loads subtitles, generates subtitles and checks screenshots; results are in [bench/player-results.md](bench/player-results.md).

`scripts/verify.ps1` runs everything and records the result in `docs/build-status.json`, which the progress dashboard reads.

## Known limitations

- Expiring or cookie-protected links cannot resume after an app restart, because cookies are deliberately kept in memory only. Use "Refresh address" with a new link.
- Some servers cap connections per IP and temporarily block clients that exceed it. FluxGet backs off, lowers its connection count and remembers the cap per host, but the first attempt still uses the configured count (default 8).
- MonoTorrent 3.0.2 creates deselected files at full length when they share a piece with a selected file; FluxGet deletes deselected files once the torrent finishes.
- Media progress for video+audio downloads grows as yt-dlp starts each stream, because the second stream's size is only known when it starts.
- Firefox is not packaged yet; the extension uses only standard WebExtension APIs.
- Preview during download works only for single-stream direct downloads whose beginning has been downloaded, and not for MP4 files that keep their index at the end. Media (yt-dlp) and torrent downloads play once finished.
- Streams start after the site's video address has been worked out (about 5–7 s for YouTube) plus about 3 s of buffering. Live streams play from the live edge. Sites that need a sign-in stream only when the page was sent from a signed-in browser with "Pass my site cookies" on.
- Chrome only installs extensions that are not in the Chrome Web Store through Developer mode → Load unpacked; the steps are in Settings.
- Rotating the picture, switching hardware decoding or changing subtitle size reopens the file at the same position (a short pause).
- Instagram, X and Reddit refuse anonymous requests for most posts. Turn on "Pass my site cookies" in the extension and use "Download from this page with FluxGet" from the signed-in browser; the session is kept in memory for 30 minutes and never written to disk except as a temporary file while a tool runs.
- The page scan reads the page's HTML and does not run its JavaScript, so images that a site loads only by script are not listed. At most 300 gallery items and 500 page files are shown.
- Offline subtitle generation uses the CPU; the Small model takes several times the clip length on slower machines.

## License

MIT. Third-party components and their licences: [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
