# FluxGet Architecture

FluxGet is a personal download manager: segmented HTTP/HTTPS downloads, media extraction (yt-dlp + FFmpeg), BitTorrent (MonoTorrent), a browser extension, and an optional headless/remote mode. It is a modular monolith written in C# / .NET 10 with a React UI.

## Modules

| Project | Responsibility |
| --- | --- |
| `src/FluxGet.Engine` | Everything that moves bytes. Job store (SQLite), queue + scheduler, segmented HTTP downloader, bandwidth limiter, media jobs (yt-dlp/FFmpeg subprocesses), torrent jobs (MonoTorrent). No ASP.NET dependency. |
| `src/FluxGet.Host` | ASP.NET Core local API: REST + Server-Sent Events, session/pairing auth, serves the built web UI. Runs inside the desktop app or headless (`FluxGet.Host.exe`, Docker). |
| `src/FluxGet.Desktop` | Windows shell: WinForms window hosting WebView2, tray icon, single instance, `fluxget://` protocol, Velopack install/update. Starts the Host in-process. |
| `web/` | React + TypeScript + Vite + Tailwind UI. Same build is used by the desktop window and by a regular browser (web interface). |
| `extension/` | Manifest V3 extension for Chrome/Edge (Firefox later). Sends jobs to the Host over `127.0.0.1` with a paired token. |
| `tests/` | xUnit engine tests against a controllable in-process HTTP server (`FluxGet.Testing`). |
| `bench/` | Throughput / segment-count / recovery benchmarks. |

Engine boundaries that matter:

- `JobManager` owns job lifecycle and the queue. It knows nothing about HTTP, torrents or yt-dlp — it starts an `IJobRunner` for the job kind (`HttpRunner`, `MediaRunner`, `TorrentRunner`).
- `HttpDownloader` is the only code that issues range requests. Media extraction never goes through it: yt-dlp handles its own protocols (DASH, HLS, signatures), FluxGet just supervises the process.
- `JobStore` is the only code that talks to SQLite.

## System context

```mermaid
flowchart LR
  user([User])
  browser[Chrome / Edge<br/>FluxGet extension]
  subgraph pc[User's PC]
    desktop[FluxGet Desktop<br/>WebView2 shell]
    host[FluxGet Host<br/>local API on 127.0.0.1]
    engine[FluxGet Engine]
    db[(SQLite)]
    disk[(Download folder)]
    tools[yt-dlp / FFmpeg / Deno]
  end
  web[(HTTP/HTTPS servers)]
  sites[(Media sites)]
  peers[(BitTorrent peers / trackers / DHT)]
  user --> desktop
  user --> browser
  browser -- paired token --> host
  desktop --> host --> engine
  engine --> db
  engine --> disk
  engine -- subprocess --> tools
  engine --> web
  tools --> sites
  engine --> peers
```

## Component architecture

```mermaid
flowchart TB
  subgraph Host[FluxGet.Host]
    api[REST endpoints]
    sse[SSE /api/events]
    auth[Session cookie + bearer tokens<br/>Origin / Host checks]
    static[Static web UI]
  end
  subgraph Engine[FluxGet.Engine]
    jm[JobManager<br/>queue, priorities, concurrency]
    sched[Scheduler<br/>time windows]
    bw[Bandwidth limiter<br/>global + per job]
    store[JobStore<br/>SQLite WAL]
    http[HttpRunner → HttpDownloader<br/>probe, segments, validators]
    media[MediaRunner<br/>yt-dlp, FFmpeg]
    tor[TorrentRunner<br/>MonoTorrent ClientEngine]
    net[NetworkMonitor]
    guard[UrlGuard<br/>SSRF in hosted mode]
  end
  api --> jm
  sse --> jm
  jm --> store
  jm --> sched
  jm --> http & media & tor
  http --> bw
  http --> guard
  http --> net
  tor --> bw
```

## Download lifecycle

```mermaid
stateDiagram-v2
  [*] --> Queued
  Queued --> Scheduled: outside schedule window
  Scheduled --> Queued: window opens
  Queued --> Running: slot free
  Running --> Paused: user pause
  Paused --> Queued: resume
  Running --> Retrying: transient error
  Retrying --> Running: backoff elapsed
  Running --> WaitingForNetwork: no network
  WaitingForNetwork --> Running: network back
  Retrying --> Failed: attempts exhausted
  Running --> NeedsAttention: remote changed / link expired / checksum mismatch / disk full
  NeedsAttention --> Queued: user fixes (refresh URL, free space, restart)
  Running --> Verifying: all bytes received
  Verifying --> Completed: hash ok, atomic rename
  Running --> Seeding: torrent complete
  Seeding --> Completed: ratio / time reached
  Failed --> Queued: retry
  Completed --> [*]
```

`Running` is shown in the UI as Downloading, Converting or Probing depending on the phase.

## Segmented HTTP download

```mermaid
sequenceDiagram
  participant R as HttpRunner
  participant S as Server
  participant F as .fgpart file
  participant DB as SQLite
  R->>S: GET Range: bytes=0-0
  S-->>R: 206, Content-Range bytes 0-0/N, ETag, Last-Modified
  Note over R: 206 → ranges supported<br/>200 → single connection
  R->>F: create & SetLength(N)
  R->>DB: save validators + segment plan
  par each segment (default 6)
    R->>S: GET Range: bytes=a-b, If-Range: ETag
    S-->>R: 206 Content-Range a-b/N
    R->>F: positional writes (RandomAccess)
  end
  loop every 2 s
    R->>R: snapshot segment offsets
    R->>F: flush to disk
    R->>DB: persist snapshot (never ahead of disk)
  end
  Note over R: finished worker steals half of the<br/>largest remaining segment (≥ 2 MiB)
  R->>F: verify checksum (optional)
  R->>F: atomic rename to final name
```

Rules the downloader enforces:

- A 200 reply to a ranged request, a `Content-Range` that does not start where we asked, a changed total length, or a changed `ETag`/`Last-Modified` aborts that worker. On resume this means the remote file changed: the job goes to *NeedsAttention* instead of mixing bytes from two versions.
- Servers without range support get exactly one connection, and resume restarts from zero (shown to the user as "not resumable").
- Weak ETags (`W/`) are not used for `If-Range`; `Last-Modified` is used instead when available.
- 401/403/410 on resume = likely expired link → *NeedsAttention* with "Refresh address", which swaps the URL and keeps partial data only if size and validators still match.

## Browser extension communication

```mermaid
sequenceDiagram
  participant E as Extension (MV3 service worker)
  participant H as Host 127.0.0.1:17890
  participant U as Desktop UI
  E->>H: POST /api/pair/request (Origin: chrome-extension://…)
  H->>U: SSE pair-request (6-digit code)
  U-->>E: user types code into extension popup
  E->>H: POST /api/pair/confirm {requestId, code}
  H-->>E: extension token (stored hashed by Host)
  E->>H: POST /api/downloads (Bearer token) url, referrer, cookies for that URL only
  H-->>E: job id
  E->>H: GET /api/jobs?active (popup progress)
```

Why localhost + pairing rather than native messaging: no registry manifest per browser, the same API serves the web UI and remote mode, and Firefox works with the same code. Websites cannot use it: requests with an `http(s)` Origin are rejected, the Host header must be `127.0.0.1`/`localhost` (DNS-rebinding defence), and every non-pairing call needs a token. The desktop UI authenticates with an HttpOnly `SameSite=Strict` cookie that the shell injects into WebView2.

## Torrent integration

```mermaid
flowchart LR
  add[Magnet link / .torrent] --> jm[JobManager] --> tr[TorrentRunner]
  tr --> ce[MonoTorrent ClientEngine<br/>one per process]
  ce --> dht[DHT / trackers / peers]
  ce --> fr[(fast-resume + metadata cache<br/>%LOCALAPPDATA%/FluxGetData/torrents)]
  tr -- progress, peers, rates --> jm
  ui[Torrents screen] -- file priorities --> tr
```

The engine-wide bandwidth limits are mapped onto MonoTorrent's own download/upload rate settings; seeding stops at a configurable ratio or time.

## Deployment

```mermaid
flowchart TB
  subgraph Personal[Personal install - default]
    d[FluxGet.Desktop.exe<br/>Velopack installer, per-user] --> h1[Host in-process<br/>127.0.0.1 only]
    h1 --> s1[(SQLite in %LOCALAPPDATA%/FluxGetData)]
    d --> t1[bundled yt-dlp, FFmpeg, Deno]
  end
  subgraph Remote[Remote worker - optional]
    c[Docker container<br/>FluxGet.Host headless] --> s2[(SQLite on volume)]
    c --> v[(downloads volume)]
    rp[Reverse proxy with TLS] --> c
  end
  ext[Browser extension] --> h1
  b[Browser] -. web UI .-> rp
```

Hosted multi-user (PostgreSQL, per-user quotas) is intentionally out of the first release — see DECISIONS.md D-008.

## Failure and recovery

```mermaid
flowchart TD
  start[Host starts] --> load[Load jobs from SQLite]
  load --> wasRunning{status was Running?}
  wasRunning -- yes --> requeue[mark Queued, keep segment offsets]
  wasRunning -- no --> keep[keep status]
  requeue --> probe[re-probe server]
  probe --> same{size + validators match?}
  same -- yes --> resume[resume from persisted offsets]
  same -- no, ranges unsupported --> restart[restart from 0, flag not resumable]
  same -- no, file changed --> attention[NeedsAttention: remote file changed]
  probe -- 401/403/410 --> expired[NeedsAttention: link expired → refresh address]
  probe -- network error --> wait[WaitingForNetwork / Retrying with backoff]
  wait --> probe
  resume --> err{error while running}
  err -- transient --> backoff[segment retry, exponential backoff, max 8 attempts]
  backoff --> resume
  err -- disk full --> attention2[NeedsAttention: free space]
  err -- exhausted --> failed[Failed, user can retry]
```

Persisted offsets are always written *after* the file data they describe has been flushed, so a crash can lose at most the last checkpoint interval of progress, never correctness.

## Data model (SQLite)

- `jobs` — id, kind (http/media/torrent), url, status, file name, directory, total/received bytes, validators (etag, last_modified), resumable flag, priority, per-job speed limit, checksum, schedule flag, error, options JSON, timestamps. Indexed on (status, priority, created_at).
- `segments` — job_id, idx, start, end, received.
- `settings` — key/value JSON.
- `tokens` — hashed extension tokens.

Request headers supplied by the extension (cookies, referrer) are held in memory only and never written to the database or logs.
