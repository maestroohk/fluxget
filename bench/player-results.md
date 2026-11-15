# Player measurements

Self-test (`FluxGet.exe --player-selftest <samples> <out>`) driving the real Windows UI: real window, real LibVLC video surface, fullscreen and mini-player windows. Samples: CC-licensed Big Buck Bunny (Blender Foundation) clips and FFmpeg-generated test patterns. Machine: 20 logical CPUs, Windows 11, hardware decoding on.

Result on the installed 0.4.0 build (after in-app update): 46 of 46 checks passed.

| Measurement | Value |
|---|---|
| Open → playing, 720p60 H.264 MP4 | 17 ms |
| Seek to 5:00 in a 10-minute file | 48 ms |
| Open → playing, WebM / MP3 / FLAC | 5 / 27 / 29 ms |
| 1080p60 H.264: CPU (all cores) / frames shown / dropped in 8 s | 1.2% / 495 / 0 |
| 2160p30 H.264: CPU / shown / dropped in 8 s | 0.3% / 241 / 0 |
| Process working set while playing 1080p / 4K | 451 / 971 MB |
| Offline subtitles, Base model, 15 s speech clip (incl. one-time 142 MB model download) | 39.9 s, 94% of reference words |

Startup (three runs, `FLUXGET_STARTUP_PROBE`): window with splash visible at 0.41–0.52 s, interface ready at 1.22–1.29 s.

Installer: 248.6 MB (0.3.x: 200 MB). The increase is LibVLC (87 MB uncompressed, x64 only, encoders excluded) and Whisper.net's native runtime (2 MB); speech models are downloaded on demand and not bundled.

## Streaming and live subtitles (2026-11-15)

Self-test on the development build, `FLUXGET_SELFTEST_ONLY=stream`, YouTube "Big Buck Bunny 60fps 4K" (10:34), home connection:

| Measurement | Value |
|---|---|
| Link to playing (including 7289 ms for yt-dlp to resolve the link) | 12745 ms |
| Seek to 6:40 in the stream | 1847 ms |
| Rebuffering stalls in 15 s of playback | 0 |
| Quality reached by default | 1080p |
| Live subtitles: first line after switching on (Base model present) | 2700 ms |

The full player self-test (55 checks including these) passed.
