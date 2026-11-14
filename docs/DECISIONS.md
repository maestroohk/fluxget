# Decisions

Each entry: context → decision → alternatives → consequences. Dates are when the decision was made.

## D-001 Evolve VidFetch into FluxGet in place (2026-11-14)
The VidFetch folder contained no code yet (only tooling had been installed), so nothing had to be migrated. FluxGet starts in the same folder; the media-downloading scope of VidFetch becomes the Media module.

## D-002 .NET 10 engine, modular monolith
The engine, local API and desktop shell are C#/.NET 10 projects in one solution. Media and torrents are engine modules behind a common `IJobRunner`, not services.
Alternatives: Rust core (no team experience, toolchain not installed), Node/Electron (Motrix-style wrapper around aria2 — lighter engine work but no control over recovery semantics and a ~150 MB runtime).

## D-003 Own segmented HTTP engine instead of driving aria2
aria2 (GPL-2.0) is excellent, but driving it via JSON-RPC means its `.aria2` control files, its state machine and its error model sit outside our database; reporting *why* a resume is unsafe (changed ETag, expired link) would be guesswork. Segmented range downloading with `HttpClient` + `RandomAccess` positional writes is a few hundred lines we can test exhaustively. Design borrowed from public behaviour of IDM/aria2: fixed initial split + dynamic splitting of the largest remaining segment, minimum split size, per-server connection cap.
Consequence: we own correctness; covered by tests against a deliberately misbehaving local server.

## D-004 Desktop shell: WinForms + WebView2
Tauri needs Rust plus a sidecar .NET process; Electron ships Chromium (~100 MB+). WebView2 is preinstalled on Windows 11 (Evergreen runtime), keeps the installer small, and lets the Host run in-process. The same React build runs in a normal browser for the web interface. WinForms is used only for the window/tray chrome.

## D-005 SQLite (WAL) for personal installs
Single-user desktop needs no server database. `Microsoft.Data.Sqlite` with hand-written SQL (no ORM) — the schema is four tables. Checkpoints are small, so WAL mode handles many concurrent jobs.

## D-006 Browser ↔ desktop: localhost API with pairing
Native messaging needs per-browser registry manifests and a separate host executable, and cannot serve the remote mode. A loopback API on `127.0.0.1:17890` is reachable from extensions; it is protected by: pairing code shown in the desktop app, hashed bearer tokens, rejection of `http(s)` Origins, Host-header allow-list (DNS rebinding), and an HttpOnly `SameSite=Strict` session cookie for the desktop UI.
Opening the app from the extension uses a per-user `fluxget://` protocol handler.

## D-007 BitTorrent: MonoTorrent
MIT licensed, pure .NET, maintained (commits in 2026; latest stable 3.0.2, 3.9 alpha). libtorrent via bindings would add native builds per platform and a BSD/C++ toolchain for marginal gain at personal-client scale. Writing a BitTorrent stack ourselves is not justified. Risk: MonoTorrent stable releases are infrequent; pin the version and cover with an integration test against a public legal torrent.

## D-008 Hosted mode is single-tenant first
The Host runs headless in Docker as a personal remote worker (one owner, token auth, SSRF guard on). Multi-tenant hosting (PostgreSQL, per-user quotas, abuse controls) is a separate product decision with legal and bandwidth cost; deferred.

## D-009 Media extraction via yt-dlp subprocess
yt-dlp is the only extractor maintained at the pace sites change. It runs as a subprocess with an argument list (never a shell string), `--newline` progress parsing, and a JS runtime for YouTube (yt-dlp now requires one — Deno preferred, Node accepted). FFmpeg does merging/remuxing/audio extraction; stream copy is used when codecs already fit the container.
We never bypass DRM or access controls; authenticated media relies on the user's own browser cookies passed for that single request.

## D-010 Installer and updates: Velopack
MIT, per-user install without admin, delta updates, update feed from GitHub Releases. Code signing needs a certificate the project does not yet have; unsigned builds trigger SmartScreen. Documented, not hidden.

## D-011 Bundled tools and licences
yt-dlp (Unlicense) and Deno (MIT) are bundled. FFmpeg is bundled as an LGPL build where possible; if a GPL build is shipped it is an unmodified separate executable with its licence and source pointer included. FluxGet itself is MIT.

## D-012 Defaults chosen by measurement
Default segment count and minimum split size come from `bench/` results recorded in the README, not from intuition. More segments are not assumed to be faster.

## D-013 Adaptive connection count and Happy Eyeballs (2026-11-14)
Found while testing against real mirrors: OVH proof servers answer 429 and then stall all traffic from an IP that opens more than ~3 connections; and on this network IPv6 routes to some hosts black-hole, which made .NET's sequential connect hang for the full timeout while curl fell back to IPv4. Decision: a worker that gets 429/503/a stall before its first byte gives its segment back while other connections are healthy, the job's connection cap drops, and the cap is remembered per host for the session. Connections use an RFC 8305-style connector (IPv6 and IPv4 attempts staggered by 250 ms, first success wins), which is also where the SSRF filter lives.

## D-014 Browser e2e runs on installed Edge (2026-11-14)
Playwright's bundled Chromium fails to start on the development machine (Windows side-by-side configuration error). Edge accepts `--load-extension` under Playwright, exercises the real MV3 runtime and is present on every Windows CI image, so the extension e2e test targets the `msedge` channel (override with `E2E_CHANNEL`).

## D-015 Media output names carry the real quality (2026-11-14)
A downloaded 360p test video was named "...4K..." because the 4K is part of the YouTube title. File names now include the downloaded height (`[720p]`) or audio bitrate (`[192k]`), taken from the actual stream, so different quality choices never overwrite each other and the name cannot mislead.

## D-016 User data lives outside the install folder (2026-11-14)
Velopack installs to `%LOCALAPPDATA%\FluxGet` and replaces that folder on uninstall. FluxGet's database, session key and WebView2 profile now live in `%LOCALAPPDATA%\FluxGetData` so updates and uninstalls never remove download history.

## D-017 Sign only our own binaries (2026-11-14)
Velopack signs every binary by default, which would put FluxGet's signature on yt-dlp and FFmpeg. A signing template (`scripts/sign-file.ps1`) skips the bundled `tools` folder so third-party executables keep their publishers' signatures (or none).

## D-018 In-page download panel instead of a separate window (2026-11-14)
Like IDM, the extension adds a Download button on supported video players. Its quality panel renders inside a closed Shadow DOM, so site styles cannot break it, and talks to FluxGet only through the extension's background worker, so web pages never see the API token.

## D-019 Playback engine: LibVLC via LibVLCSharp (2026-11-14)
Compared libmpv, LibVLC, an FFmpeg-based custom player and WebView2's HTML5 video. LibVLC 3.0.24 + LibVLCSharp 3.10.2 (LGPL-2.1, maintained, official NuGet packages with a WinForms VideoView, d3d11 hardware decoding, libass for ASS/SRT/VTT, track and delay APIs, speed, frame stepping, adjust filters). libmpv is excellent but the available Windows builds include GPL components and there is no maintained .NET package; HTML5 video lacks AC3/DTS, ASS styling and audio-track switching; a custom FFmpeg player would mean writing a renderer, A/V sync and subtitle rendering. LibVLC is loaded dynamically from unmodified DLLs; FluxGet stays MIT. The bundle excludes VLC's encoders (including GPL x264/x265), stream output, visualisations, GUI and Lua plugins: 87 MB instead of 109 MB, x64 only.

## D-020 Native video surface over the WebView2 UI (2026-11-14)
The player UI is React like the rest of FluxGet, but video is drawn by LibVLC into a native window placed over the stage rectangle the page reports. Fullscreen uses a separate borderless window plus a small floating WebView2 control bar; the mini player uses native controls. The same video window is reparented between them. Lesson from testing: the destination window's handle must exist before reparenting, otherwise WinForms recreates the video window and LibVLC keeps drawing into the destroyed one (black video).

## D-021 Offline subtitles with whisper.cpp (2026-11-14)
Whisper.net 1.9.1 (MIT) wraps whisper.cpp (MIT); the OpenAI Whisper weights are MIT. Everything runs on the user's CPU; nothing is uploaded. Models download on first use (not bundled, to keep the installer size unchanged) and are checked against Hugging Face's published SHA-256. Measured: Base model, 15 s English clip, 31/33 reference words, about 20 s on this machine including the one-time download.

## D-022 Startup without a blank window (2026-11-14)
The window used to appear empty while the local service and WebView2 started one after the other. Now the service and WebView2 start in parallel, a native splash is painted immediately and stays until the web UI reports it has rendered, and notification/branding setup runs after the window is shown. Measured: window with splash at 0.4–0.5 s, UI ready at about 1.2 s.
