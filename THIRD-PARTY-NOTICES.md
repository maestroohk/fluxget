# Third-party components

FluxGet is MIT licensed. It uses or ships the following components.

| Component | Use | Licence | Shipped in installer |
|---|---|---|---|
| .NET / ASP.NET Core | runtime | MIT | yes (self-contained) |
| Microsoft.Data.Sqlite, SQLitePCLRaw, SQLite | job store | MIT / Apache-2.0 / public domain | yes |
| MonoTorrent | BitTorrent engine | MIT | yes |
| Microsoft.Web.WebView2 SDK | desktop window | Microsoft BSD-style licence (redistributable) | yes |
| Velopack | installer and updates | MIT | yes |
| React, React DOM | UI | MIT | yes (bundled JS) |
| Tailwind CSS | UI styles | MIT | yes (compiled CSS) |
| lucide-react | icons | ISC | yes (bundled JS) |
| @tanstack/react-virtual | list virtualisation | MIT | yes (bundled JS) |
| Geist, Geist Mono (via Fontsource) | typefaces | SIL Open Font License 1.1 | yes |
| LibVLC 3.0.24 (VideoLAN.LibVLC.Windows) | media playback engine (unmodified DLLs and plugins, x64) | LGPL-2.1-or-later; encoders and other GPL plugins are excluded from the bundle | yes |
| LibVLCSharp, LibVLCSharp.WinForms | .NET bindings for LibVLC | LGPL-2.1-or-later | yes (unmodified assemblies) |
| Whisper.net, whisper.cpp / ggml | offline speech recognition for subtitles | MIT | yes |
| Whisper speech models (ggml tiny/base/small) | offline subtitles | MIT (OpenAI Whisper) | no — downloaded on first use from huggingface.co/ggerganov/whisper.cpp and SHA-256 verified |
| Microsoft.Toolkit.Uwp.Notifications | Windows notifications | MIT | yes |
| gallery-dl 1.32.20 | photo and gallery downloads (separate executable, run as a process) | GPL-2.0-only | yes, unmodified official build from codeberg.org/mikf/gallery-dl; its licence and the matching source archive are installed next to it in `tools/` |
| yt-dlp | media extraction (separate executable) | Unlicense | yes, unmodified `yt-dlp.exe` |
| Deno | JavaScript runtime required by yt-dlp for YouTube (separate executable) | MIT | yes, unmodified `deno.exe` |
| FFmpeg / FFprobe | merging, remuxing, audio conversion (separate executables) | LGPL-2.1-or-later build | yes, unmodified LGPL build from github.com/BtbN/FFmpeg-Builds; source: https://ffmpeg.org/download.html |

The Microsoft Edge WebView2 Runtime is part of Windows 11 and is not redistributed.

FFmpeg is shipped as unmodified separate executables invoked as processes, not linked into FluxGet. The LGPL build is used so that no GPL obligations extend to the distribution. Its licence text is installed next to the binaries in `tools/`.

LibVLC and LibVLCSharp are used as separate, unmodified dynamic libraries, so they can be replaced by the user. Their source code is available from https://code.videolan.org/videolan/vlc (tag 3.0.24) and https://code.videolan.org/videolan/LibVLCSharp.
