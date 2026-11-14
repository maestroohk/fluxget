# FluxGet benchmark results

Run 2026-11-14 14:08 UTC on 20 logical CPUs, Microsoft Windows NT 10.0.26200.0, .NET 10.0.10. Server and client on the same machine over loopback, so absolute numbers measure FluxGet's own overhead, not internet speed.

## Segment count vs. a server that limits each connection to 4 MiB/s

| Connections | Size | Time | Throughput | Peak working set |
|---|---|---|---|---|
| 1 | 256 MiB | 64.1 s | 4.0 MiB/s | 73 MB |
| 2 | 256 MiB | 32.1 s | 8.0 MiB/s | 80 MB |
| 4 | 256 MiB | 16.1 s | 15.9 MiB/s | 85 MB |
| 8 | 256 MiB | 8.1 s | 31.7 MiB/s | 91 MB |
| 16 | 256 MiB | 4.2 s | 60.6 MiB/s | 97 MB |

## Segment count vs. an unthrottled loopback server

| Connections | Size | Time | Throughput | CPU (avg of all cores) | Peak working set |
|---|---|---|---|---|---|
| 1 | 1 GiB | 2.3 s | 440 MiB/s | 12% | 94 MB |
| 4 | 1 GiB | 1.6 s | 660 MiB/s | 14% | 95 MB |
| 8 | 1 GiB | 6.3 s | 163 MiB/s | 3% | 96 MB |
| 16 | 1 GiB | 1.1 s | 907 MiB/s | 17% | 99 MB |

## Large file

4 GiB file, 8 connections: 70.6 s, 58 MiB/s, peak working set 100 MB, SHA-256 matches. This run overlapped other disk-heavy work on the machine (a parallel 720p media download and a 370 MB tool download).

Re-run on an idle machine (`FluxGet.Bench probe 4096 <connections> 1`): 4 GiB with 8 connections in 19.0 s (216 MiB/s, CPU 4%); with 1 connection in 32.1 s (128 MiB/s, CPU 3%).

## Disk is the ceiling on this machine

Sequential write + fsync of 4 GiB with Python measured 158 MiB/s sustained. Unthrottled loopback runs are bimodal: 650-900 MiB/s while the OS write cache absorbs data, 100-140 MiB/s with ~3% CPU once it has to flush to disk. The 163 MiB/s "8 connections" row above is one of those disk-bound runs, not a property of the connection count. Repeated 1 GiB / 8-connection runs: 659, 884, 130, 103 MiB/s.

## What this means for defaults

- Splitting a download only helps when the server limits each connection; then throughput scales almost linearly with connections (4.0 → 60.6 MiB/s from 1 to 16).
- Against a fast server, connection count makes no measurable difference; the disk decides.
- Some real servers cap connections per IP (observed: OVH proof servers block clients above about 3). FluxGet starts at 8, backs off when the server answers 429/503 or stalls, and remembers the cap per host.
- Default kept at 8 connections. Memory stays flat at about 100 MB including the in-process test server, independent of file size.

## Crash recovery

256 MiB file at 2 MiB/s per connection, 8 connections, checkpoint every 2 s. Process force-killed (no shutdown) after 103 MiB; restarted process resumed with 97 MiB already on disk after 168 ms and finished with SHA-256 matching the source.
