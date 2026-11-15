using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using FluxGet.Desktop.Player;
using FluxGet.Engine;
using LibVLCSharp.Shared;
using Microsoft.Extensions.DependencyInjection;

namespace FluxGet.Desktop;

internal sealed partial class MainForm
{
    private sealed record Check(string Name, bool Pass, string Detail);

    private async Task RunSelfTestAsync(string samples, string outDir)
    {
        Directory.CreateDirectory(outDir);
        var checks = new List<Check>();
        var metrics = new Dictionary<string, object>();
        string S(string name) => Path.Combine(samples, name);
        void Add(string name, bool pass, string detail = "") => checks.Add(new Check(name, pass, detail));
        async Task<bool> Until(Func<bool> cond, int ms = 10000)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < ms) { if (cond()) return true; await Task.Delay(100); }
            return cond();
        }
        var p = Playback;
        var mp = p.Engine;
        void Open(params string[] files) => p.Open(files.Select(f => new QueueItem(f, Path.GetFileNameWithoutExtension(f), null, false)));
        async Task<bool> Playing() => await Until(() => mp.State == VLCState.Playing && mp.Time > 200, 15000);
        void Cmd(object o) => p.Handle(JsonDocument.Parse(JsonSerializer.Serialize(o)).RootElement);

        var only = Environment.GetEnvironmentVariable("FLUXGET_SELFTEST_ONLY");
        try
        {
            PostToUi(new { type = "navigate", page = "player" });
            await Task.Delay(2500);
            if (only != "stream") await CoreChecksAsync();
            await StreamChecksAsync();
            p.Close();
        }
        catch (Exception e)
        {
            Add("Self-test crashed", false, e.ToString());
        }

        metrics["startupToUiMs"] = Program.StartupMs;
        var report = new { at = DateTime.UtcNow, passed = checks.Count(c => c.Pass), failed = checks.Count(c => !c.Pass), checks, metrics };
        await File.WriteAllTextAsync(Path.Combine(outDir, "results.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        _exiting = true;
        Close();

        async Task StreamChecksAsync()
        {
            p.Close();
            await Task.Delay(1200);
            Shot(this, Path.Combine(outDir, "00-empty-player.png"));
            Open(S("speech-test.mp4"));
            await Playing();
            p.SeekTo(0);
            var sw = Stopwatch.StartNew();
            Cmd(new { cmd = "liveCaptions", on = true, model = "base", language = "auto", translate = false });
            var shown = await Until(() => p.CaptionText is { Length: > 0 }, 90_000);
            metrics["liveCaptionFirstLineMs"] = sw.ElapsedMilliseconds;
            Add("Live subtitles appear on the video while it plays", shown, $"first line after {sw.ElapsedMilliseconds} ms: \"{p.CaptionText}\", status {p.Captions?.Status} {p.Captions?.Error} {p.Captions?.Detail}");
            await Task.Delay(600);
            Shot(this, Path.Combine(outDir, "12-live-subtitles.png"));
            await _web.CoreWebView2.ExecuteScriptAsync("document.querySelector('[aria-label^=\"Subtitles\"]').click()");
            await Task.Delay(800);
            Shot(this, Path.Combine(outDir, "12b-subtitle-menu.png"));
            await _web.CoreWebView2.ExecuteScriptAsync("document.querySelector('[aria-label^=\"Subtitles\"]').click()");
            p.SeekTo(9000);
            var afterSeek = await Until(() => p.CaptionText is { Length: > 0 } && mp.Time > 9000, 30_000);
            Add("Live subtitles follow a seek", afterSeek, $"\"{p.CaptionText}\" at {mp.Time} ms");
            Cmd(new { cmd = "liveCaptions", on = false });
            Add("Live subtitles turn off", await Until(() => p.Captions is null && p.CaptionText is null, 3000));

            var url = Environment.GetEnvironmentVariable("FLUXGET_SELFTEST_STREAM") ?? "https://www.youtube.com/watch?v=aqz-KE-bpKQ";
            var stalls = 0;
            var playingSeen = false;
            mp.Buffering += (_, e) => { if (playingSeen && e.Cache < 100) stalls++; };
            sw.Restart();
            Cmd(new { cmd = "openUrl", url });
            var streaming = await Until(() => mp.State == VLCState.Playing && mp.Time > 500, 60_000);
            playingSeen = streaming;
            metrics["streamStartMs"] = sw.ElapsedMilliseconds;
            metrics["streamResolveMs"] = p.LastResolveMs;
            Add("YouTube link streams without downloading", streaming && mp.VideoTrackCount > 0,
                $"{sw.ElapsedMilliseconds} ms to playing, length {mp.Length / 1000} s, title \"{p.Current?.Title}\", audio tracks {mp.AudioTrackCount}");
            await Task.Delay(3000);
            Shot(this, Path.Combine(outDir, "13-stream.png"));
            Add("Streamed video renders", NotBlack(Path.Combine(outDir, "13-stream.png"), p.View.Bounds));
            Add("Streamed video has sound", mp.AudioTrackCount > 0 && mp.AudioTrack >= 0, $"audio tracks {mp.AudioTrackCount}");

            sw.Restart();
            p.SeekTo(400_000);
            var sought = await Until(() => mp.State == VLCState.Playing && mp.Time > 400_500, 20_000);
            metrics["streamSeekMs"] = sw.ElapsedMilliseconds;
            Add("Seeking far into a stream", sought, $"{sw.ElapsedMilliseconds} ms to resume at {mp.Time / 1000} s");
            var t0 = mp.Time;
            stalls = 0;
            await Task.Delay(15_000);
            var advanced = mp.Time - t0;
            uint aw = 0, ah = 0;
            mp.Size(0, ref aw, ref ah);
            metrics["streamStalls15s"] = stalls;
            metrics["streamAutoHeightAfter20s"] = ah;
            Add("Stream plays 15 s at full quality without rebuffering", stalls == 0 && advanced > 13_500 && ah >= 720, $"stalls {stalls}, advanced {advanced} ms, quality {aw}x{ah}");

            var before = mp.Time;
            Cmd(new { cmd = "quality", value = 720 });
            var switched = await Until(() => mp.State == VLCState.Playing && Math.Abs(mp.Time - before) < 20_000 && mp.Time > 1000, 40_000);
            uint w = 0, h = 0;
            mp.Size(0, ref w, ref h);
            await Task.Delay(6000);
            mp.Size(0, ref w, ref h);
            Add("Choosing 720p keeps the position and plays 720p", switched && h == 720, $"now {w}x{h} at {mp.Time / 1000} s (was {before / 1000} s)");
        }

        async Task CoreChecksAsync()
        {
            Open(S("bbb-720p-long.mp4"));
            Add("MP4 (H.264/AAC 720p60) plays", await Playing(), $"open→playing {p.LastOpenMs} ms");
            metrics["openMs_mp4_720p"] = p.LastOpenMs;
            await Task.Delay(1500);
            Add("Video surface placed in the Player screen", p.View.Visible && p.View.Width > 300 && p.View.Parent == this, $"{p.View.Bounds}");
            Shot(this, Path.Combine(outDir, "01-player-embedded.png"));
            Add("Embedded video renders", NotBlack(Path.Combine(outDir, "01-player-embedded.png"), p.View.Bounds));

            p.SeekTo(300_000);
            var seeked = await Until(() => p.LastSeekMs >= 0 && Math.Abs(mp.Time - 300_000) < 3000, 8000);
            Add("Timeline seek to 5:00", seeked, $"latency {p.LastSeekMs} ms, now {mp.Time} ms");
            metrics["seekMs_720p"] = p.LastSeekMs;
            p.SeekBy(-5000);
            await Task.Delay(800);
            Add("Keyboard-style relative seek (−5 s)", Math.Abs(mp.Time - 295_000) < 3000, $"{mp.Time} ms");

            foreach (var r in new[] { 0.25, 1.5, 2.0, 1.0 })
            {
                Cmd(new { cmd = "rate", value = r });
                await Task.Delay(300);
                Add($"Speed {r}×", Math.Abs(mp.Rate - r) < 0.01, $"rate {mp.Rate}");
            }
            Cmd(new { cmd = "volume", value = 60 });
            Cmd(new { cmd = "mute" });
            await Task.Delay(300);
            Add("Volume and mute", mp.Volume == 60 && mp.Mute, $"volume {mp.Volume}, mute {mp.Mute}");
            Cmd(new { cmd = "mute" });
            Cmd(new { cmd = "adjust", name = "brightness", value = 1.4 });
            Cmd(new { cmd = "aspect", value = "4:3" });
            Cmd(new { cmd = "zoom", value = 1.5 });
            await Task.Delay(600);
            Add("Picture: brightness, aspect 4:3, zoom 150%", Math.Abs(mp.AdjustFloat(VideoAdjustOption.Brightness) - 1.4f) < 0.01 && mp.AspectRatio == "4:3" && Math.Abs(mp.Scale - 1.5f) < 0.01);
            Shot(this, Path.Combine(outDir, "02-picture-adjusted.png"));
            Cmd(new { cmd = "adjust", name = "reset" });
            Cmd(new { cmd = "aspect", value = "" });
            Cmd(new { cmd = "zoom", value = 0 });
            mp.SetPause(true);
            await Task.Delay(300);
            var beforeFrame = mp.Time;
            mp.NextFrame();
            await Task.Delay(400);
            Add("Frame step", mp.State == VLCState.Paused && mp.Time >= beforeFrame, $"{beforeFrame}→{mp.Time} ms");
            mp.SetPause(false);

            var t0 = mp.Time;
            SetPlayerMode(PlayerMode.Fullscreen);
            await Task.Delay(2500);
            var fsForm = Application.OpenForms.OfType<FullscreenForm>().FirstOrDefault();
            Add("Fullscreen keeps playing without restart", fsForm is not null && p.View.Parent == fsForm && mp.Time > t0 && mp.State == VLCState.Playing, $"{t0}→{mp.Time} ms");
            if (fsForm is not null) ShotScreen(fsForm.Bounds, Path.Combine(outDir, "03-fullscreen.png"));
            SetPlayerMode(PlayerMode.Embedded);
            await Task.Delay(1200);
            Add("Back to embedded from fullscreen", p.View.Parent == this && p.View.Visible);

            t0 = mp.Time;
            SetPlayerMode(PlayerMode.Mini);
            await Task.Delay(2500);
            var mini = Application.OpenForms.OfType<MiniForm>().FirstOrDefault();
            Add("Mini player: always on top, keeps playing", mini is not null && mini.TopMost && p.View.Parent == mini && mp.Time > t0 && mp.State == VLCState.Playing, $"{t0}→{mp.Time} ms, size {mini?.Size}");
            if (mini is not null)
            {
                mini.Activate();
                await Task.Delay(500);
                await Task.Delay(2000);
                Shot(mini, Path.Combine(outDir, "04-mini-player.png"));
                Add("Mini player shows video (not black)", NotBlack(Path.Combine(outDir, "04-mini-player.png")));
            }
            SetPlayerMode(PlayerMode.Embedded);
            WindowState = FormWindowState.Normal;
            await Task.Delay(1500);
            Add("Return from mini player", p.View.Parent == this && WindowState == FormWindowState.Normal);
            Shot(this, Path.Combine(outDir, "04b-after-mini.png"));
            Add("Video still renders after leaving the mini player", NotBlack(Path.Combine(outDir, "04b-after-mini.png"), p.View.Bounds));

            Open(S("bbb-multitrack.mkv"));
            Add("MKV plays", await Playing());
            await Until(() => mp.AudioTrackCount >= 2 && mp.SpuCount >= 1, 5000);
            var audio = mp.AudioTrackDescription.Where(t => t.Id >= 0).ToArray();
            Add("MKV exposes 2 audio tracks", audio.Length == 2, string.Join(", ", audio.Select(a => a.Name)));
            if (audio.Length == 2)
            {
                Cmd(new { cmd = "audioTrack", id = audio[1].Id });
                await Task.Delay(800);
                Add("Switch audio track", mp.AudioTrack == audio[1].Id, $"now {mp.AudioTrack}");
            }
            var spu = mp.SpuDescription.Where(t => t.Id >= 0).ToArray();
            Add("Embedded subtitle track detected", spu.Length >= 1, string.Join(", ", spu.Select(s => s.Name)));
            if (spu.Length > 0) Cmd(new { cmd = "subtitleTrack", id = spu[0].Id });
            p.SeekTo(2000);
            await Task.Delay(2000);
            Add("Embedded subtitle selected", spu.Length > 0 && mp.Spu == spu[0].Id);
            Shot(this, Path.Combine(outDir, "05-mkv-embedded-subtitle.png"));
            Cmd(new { cmd = "subtitleDelay", ms = 500 });
            Cmd(new { cmd = "audioDelay", ms = -200 });
            await Task.Delay(300);
            Add("Subtitle delay +0.5 s and audio sync −0.2 s", mp.SpuDelay == 500_000 && mp.AudioDelay == -200_000, $"spu {mp.SpuDelay} µs, audio {mp.AudioDelay} µs");

            foreach (var sub in new[] { "bbb.srt", "bbb.ass", "bbb.vtt" })
            {
                var before = mp.SpuCount;
                Cmd(new { cmd = "addSubtitleFile", path = S(sub) });
                var added = await Until(() => mp.SpuCount > before, 5000);
                Add($"External {Path.GetExtension(sub).ToUpperInvariant().TrimStart('.')} subtitle loads", added, $"tracks {before}→{mp.SpuCount}");
            }
            p.SeekTo(3000);
            await Task.Delay(2500);
            Shot(this, Path.Combine(outDir, "06-external-ass-subtitle.png"));

            foreach (var (file, label) in new[] { ("bbb-480p.webm", "WebM (VP9/Opus)"), ("bbb.mp3", "MP3"), ("bbb.flac", "FLAC") })
            {
                Open(S(file));
                var ok = await Playing();
                Add($"{label} plays", ok, $"open→playing {p.LastOpenMs} ms, video tracks {mp.VideoTrackCount}");
                metrics[$"openMs_{Path.GetExtension(file).TrimStart('.')}"] = p.LastOpenMs;
                if (file == "bbb.mp3") { await Task.Delay(1000); Shot(this, Path.Combine(outDir, "07-audio.png")); }
            }

            foreach (var (file, label) in new[] { ("test-1080p60.mp4", "1080p60"), ("test-2160p30.mp4", "2160p30 (4K)") })
            {
                Open(S(file));
                var ok = await Playing();
                await Task.Delay(1500);
                var proc = Process.GetCurrentProcess();
                var stats0 = p.CurrentMediaStats();
                var cpu0 = proc.TotalProcessorTime;
                var sw = Stopwatch.StartNew();
                await Task.Delay(8000);
                proc.Refresh();
                var cpu = (proc.TotalProcessorTime - cpu0).TotalSeconds / sw.Elapsed.TotalSeconds / Environment.ProcessorCount * 100;
                var end = p.CurrentMediaStats();
                var stats = (Displayed: end.Displayed - stats0.Displayed, Lost: end.Lost - stats0.Lost);
                Add($"{label} plays smoothly", ok && stats.Lost <= Math.Max(3, stats.Displayed / 50), $"cpu {cpu:0.0}% of all cores, displayed {stats.Displayed}, lost {stats.Lost}, working set {proc.WorkingSet64 / 1048576} MB");
                metrics[$"cpu_{label}"] = Math.Round(cpu, 1);
                metrics[$"lostFrames_{label}"] = stats.Lost;
                metrics[$"displayedFrames_{label}"] = stats.Displayed;
                metrics[$"workingSetMB_{label}"] = proc.WorkingSet64 / 1048576;
                metrics[$"openMs_{label}"] = p.LastOpenMs;
            }

            Open(S("bbb-480p.webm"), S("bbb.mp3"), S("bbb.flac"));
            await Playing();
            Cmd(new { cmd = "next" });
            await Task.Delay(1500);
            var afterNext = p.Current?.Title;
            Cmd(new { cmd = "prev" });
            await Task.Delay(1500);
            Add("Queue next / previous", afterNext == "bbb" && p.Current?.Title == "bbb-480p", $"next→{afterNext}, prev→{p.Current?.Title}");
            Cmd(new { cmd = "playIndex", index = 2 });
            await Task.Delay(1200);
            Add("Queue jump to item 3", p.Current?.Source.EndsWith("bbb.flac") == true);

            Open(S("bbb-720p-long.mp4"));
            await Playing();
            p.SeekTo(120_000);
            await Task.Delay(6500);
            p.Close();
            await Task.Delay(500);
            Open(S("bbb-720p-long.mp4"));
            await Playing();
            await Task.Delay(800);
            Add("Resume from last position", mp.Time >= 115_000, $"reopened at {mp.Time} ms");
            Shot(this, Path.Combine(outDir, "08-resumed.png"));

            Open(S("damaged-truncated.mp4"));
            await Task.Delay(6000);
            var s1 = (JsonElement)JsonSerializer.SerializeToElement(p.Snapshot());
            Add("Damaged MP4 reports an error, app stays responsive", s1.GetProperty("state").GetString() is "error" or "ended" or "stopped", $"state {s1.GetProperty("state").GetString()}");
            Open(S("not-really-video.mp4"));
            await Task.Delay(12000);
            var s2 = JsonSerializer.SerializeToElement(p.Snapshot());
            Add("Random bytes named .mp4 report an error", s2.GetProperty("state").GetString() is "error" or "ended" or "stopped", $"state {s2.GetProperty("state").GetString()}");
            Shot(this, Path.Combine(outDir, "09-error.png"));
            p.Open([new QueueItem(S("missing-file.mp4"), "missing-file", null, false)]);
            await Task.Delay(500);
            var s3 = JsonSerializer.SerializeToElement(p.Snapshot());
            Add("Missing file shows a clear message", s3.GetProperty("error").GetString()?.Contains("no longer exists") == true, s3.GetProperty("error").GetString() ?? "");

            var probe = S("bbb-480p.webm");
            Open(probe);
            await Playing();
            await Task.Delay(1500);
            p.Close();
            await Task.Delay(800);
            bool released;
            try { using (File.Open(probe, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { } released = true; }
            catch (IOException) { released = false; }
            Add("Closing the player releases the file handle", released);

            var server = new ThrottledFileServer(S("test-1080p60-faststart.mp4"), 2 * 1024 * 1024);
            Open(S("bbb-multitrack.mkv"));
            await Playing();
            var dl = _manager.Add(new AddJobRequest { Url = server.Url, Directory = Path.Combine(outDir, "downloads"), Connections = 1, AllowDuplicate = true });
            var previewAvailable = await Until(() => Host.PlayerEndpoints.PreviewInfo(_manager.Find(dl.Id)!).Available, 20000);
            var playingDuringDownload = mp.State == VLCState.Playing;
            Add("Playback continues while a download runs", playingDuringDownload && _manager.Find(dl.Id)!.Status is JobStatus.Running or JobStatus.Completed);
            if (previewAvailable)
            {
                Cmd(new { cmd = "openJob", jobId = dl.Id, title = "Preview" });
                var previewPlaying = await Playing();
                var job = _manager.Find(dl.Id)!;
                Add("Preview plays a partially downloaded MP4", previewPlaying, $"downloaded {Interlocked.Read(ref job.ReceivedBytes) / 1048576.0:0.0} of {(job.TotalBytes ?? 0) / 1048576.0:0.0} MB when preview started, status {job.Status}");
                Shot(this, Path.Combine(outDir, "10-preview-during-download.png"));
            }
            else Add("Preview plays a partially downloaded MP4", false, "preview never became available");
            await Until(() => _manager.Find(dl.Id)?.Status == JobStatus.Completed, 30000);
            var finished = _manager.Find(dl.Id)!;
            Add("Download completes intact while being previewed", finished.Status == JobStatus.Completed && new FileInfo(finished.FilePath!).Length == new FileInfo(S("test-1080p60-faststart.mp4")).Length, $"status {finished.Status}");
            server.Dispose();

            Open(S("speech-test.mp4"));
            await Playing();
            var dropBefore = mp.SpuCount;
            PlayFiles([S("bbb.srt")]);
            Add("Dropping a subtitle file adds it to the current video", await Until(() => mp.SpuCount > dropBefore, 5000), $"tracks {dropBefore}→{mp.SpuCount}");

            var transcriber = _server.Services.GetRequiredService<Engine.Media.Transcriber>();
            var task = transcriber.Start(S("speech-test.mp4"), "base", "auto", false);
            await Until(() => task.Status is "done" or "failed" or "cancelled", 600_000);
            var reference = "welcome to fluxget this short clip tests offline subtitles the quick brown fox jumps over the lazy dog downloads continue while you watch videos in the built in player thank you for watching";
            var produced = task.OutputPath is { } srt && File.Exists(srt)
                ? string.Join(' ', File.ReadAllLines(srt).Where(l => l.Length > 0 && !char.IsDigit(l[0]))).ToLowerInvariant()
                : "";
            var words = new string(produced.Select(ch => char.IsLetter(ch) || ch == ' ' ? ch : ' ').ToArray()).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
            var refWords = reference.Split(' ');
            var hit = refWords.Count(w => words.Contains(w));
            metrics["subtitleWordRecall"] = Math.Round((double)hit / refWords.Length, 3);
            metrics["subtitleSeconds"] = Math.Round(task.ElapsedMs / 1000.0, 1);
            Add("Offline subtitles generated (Whisper base)", task.Status == "done" && hit >= refWords.Length * 0.8,
                $"{task.Status}, {task.Segments} lines, {hit}/{refWords.Length} reference words, {task.ElapsedMs / 1000.0:0.0} s incl. model download, lang {task.DetectedLanguage}, file {Path.GetFileName(task.OutputPath ?? "")}");
            if (task.OutputPath is not null)
            {
                var beforeGen = mp.State is VLCState.Playing or VLCState.Paused ? mp.SpuCount : 0;
                Cmd(new { cmd = "addSubtitleFile", path = task.OutputPath });
                if (mp.State is VLCState.Ended or VLCState.Stopped) p.Toggle();
                Add("Generated subtitles load into the player (also after the clip ended)", await Until(() => mp.SpuCount >= Math.Max(beforeGen + 1, 3), 8000), $"tracks {mp.SpuCount}, state {mp.State}");
                p.SeekTo(4000);
                await Task.Delay(2500);
                Shot(this, Path.Combine(outDir, "11-generated-subtitles.png"));
                Add("Video renders at the end of the session", NotBlack(Path.Combine(outDir, "11-generated-subtitles.png"), p.View.Bounds));
                File.Copy(task.OutputPath, Path.Combine(outDir, "generated.srt"), true);
            }
        }
    }

    private static void Shot(Control c, string path)
    {
        using var bmp = new Bitmap(c.Width, c.Height);
        using (var g = Graphics.FromImage(bmp))
        {
            var dc = g.GetHdc();
            PrintWindow(c.Handle, dc, 2);
            g.ReleaseHdc(dc);
        }
        bmp.Save(path);
    }

    private static bool NotBlack(string path, Rectangle? area = null)
    {
        using var bmp = new Bitmap(path);
        var r = area ?? new Rectangle(0, 0, bmp.Width, bmp.Height);
        var bright = 0;
        var total = 0;
        for (var x = r.Left + r.Width / 8; x < r.Right - r.Width / 8; x += Math.Max(1, r.Width / 40))
        for (var y = r.Top + r.Height / 8; y < r.Bottom - r.Height / 8; y += Math.Max(1, r.Height / 30))
        {
            if (x >= bmp.Width || y >= bmp.Height) continue;
            var c = bmp.GetPixel(x, y);
            total++;
            if (c.R + c.G + c.B > 60) bright++;
        }
        return total > 0 && bright > total / 10;
    }

    private static void ShotScreen(Rectangle bounds, string path)
    {
        using var bmp = new Bitmap(bounds.Width, bounds.Height);
        using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
        bmp.Save(path);
    }

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr dc, uint flags);

    private sealed class ThrottledFileServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _cts = new();

        public ThrottledFileServer(string file, long bytesPerSecond)
        {
            _listener.Start();
            Url = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/{Path.GetFileName(file)}";
            _ = Task.Run(async () =>
            {
                while (!_cts.IsCancellationRequested)
                {
                    TcpClient client;
                    try { client = await _listener.AcceptTcpClientAsync(_cts.Token); } catch { return; }
                    _ = Task.Run(async () =>
                    {
                        using var c = client;
                        var stream = c.GetStream();
                        var buf = new byte[8192];
                        var header = new StringBuilder();
                        while (!header.ToString().Contains("\r\n\r\n")) { var n = await stream.ReadAsync(buf); if (n == 0) return; header.Append(Encoding.ASCII.GetString(buf, 0, n)); }
                        var length = new FileInfo(file).Length;
                        await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: video/mp4\r\nContent-Length: {length}\r\nConnection: close\r\n\r\n"));
                        await using var fs = File.OpenRead(file);
                        var chunk = new byte[64 * 1024];
                        var sw = Stopwatch.StartNew();
                        long sent = 0;
                        int read;
                        try
                        {
                            while ((read = await fs.ReadAsync(chunk)) > 0)
                            {
                                await stream.WriteAsync(chunk.AsMemory(0, read));
                                sent += read;
                                var due = sent * 1000 / bytesPerSecond - sw.ElapsedMilliseconds;
                                if (due > 0) await Task.Delay((int)due);
                            }
                        }
                        catch (IOException) { }
                    });
                }
            });
        }

        public string Url { get; }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
        }
    }
}
