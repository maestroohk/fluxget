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
