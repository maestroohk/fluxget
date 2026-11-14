using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace FluxGet.Engine.Media;

public sealed record MediaOptions
{
    public string Mode { get; init; } = "video";
    public int? Height { get; init; }
    public string Container { get; init; } = "mp4";
    public string AudioFormat { get; init; } = "mp3";
    public string? AudioQuality { get; init; }
    public bool Subtitles { get; init; }
    public string? MediaId { get; init; }
    public string? Source { get; init; }

    public static readonly string[] Containers = ["mp4", "mkv", "webm"];
    public static readonly string[] AudioFormats = ["mp3", "m4a", "aac", "opus", "flac", "wav"];
    public static readonly string[] Qualities = ["64", "96", "128", "160", "192", "256", "320"];
}

public sealed class MediaRunner(YtDlp ytDlp) : IJobRunner
{
    public JobKind Kind => JobKind.Media;

    public Task RunAsync(JobContext ctx, CancellationToken ct)
    {
        var o = ctx.Job.GetOptions<MediaOptions>() ?? new MediaOptions();
        return o.Mode == "convert" ? ConvertAsync(ctx, o, ct) : DownloadAsync(ctx, o, ct);
    }

    private async Task DownloadAsync(JobContext ctx, MediaOptions o, CancellationToken ct)
    {
        var job = ctx.Job;
        Directory.CreateDirectory(job.Directory);
        job.Extra = new Dictionary<string, object?> { ["title"] = job.FileName, ["mode"] = o.Mode };
        var args = ytDlp.CommonArgs();
        args.AddRange(["--newline", "--progress", "--no-simulate", "--no-mtime", "--continue", "--retries", "10", "--fragment-retries", "10",
            "--progress-template", "download:FGP %(progress.status)s|%(progress.downloaded_bytes)s|%(progress.total_bytes)s|%(progress.total_bytes_estimate)s|%(info.format_id)s",
            "--print", "after_move:FGFILE %(filepath)s",
            "-o", Path.Combine(job.Directory, "%(title).150B [%(id)s].%(ext)s")]);
        if (OperatingSystem.IsWindows()) args.Add("--windows-filenames");
        var rate = new[] { job.SpeedLimit, ctx.GlobalLimiter.Rate }.Where(r => r > 0).DefaultIfEmpty(0).Min();
        if (rate > 0) args.AddRange(["--limit-rate", rate.ToString(CultureInfo.InvariantCulture)]);

        if (o.Mode == "audio")
        {
            args.AddRange(["-f", "ba/b", "-x", "--audio-format", o.AudioFormat, "--embed-metadata"]);
            if (o.AudioQuality is { } q && o.AudioFormat is not ("flac" or "wav")) args.AddRange(["--audio-quality", q + "K"]);
            if (o.AudioFormat is "mp3" or "m4a" or "flac") args.Add("--embed-thumbnail");
        }
        else
        {
            var sort = o.Height is { } h ? $"res:{h}" : "res";
            if (o.Container == "mp4") sort += ",vcodec:h264,ext:mp4:m4a";
            args.AddRange(["-f", "bv*+ba/b", "-S", sort, "--merge-output-format", o.Container, "--embed-metadata"]);
            if (o.Container != "webm") args.Add("--embed-thumbnail");
            if (o.Subtitles) args.AddRange(["--write-subs", "--sub-langs", "all,-live_chat", "--embed-subs"]);
        }

        string? cookieFile = null;
        if (job.RequestHeaders is { } headers)
        {
            if (headers.TryGetValue("Cookie", out var cookie)) cookieFile = WriteCookieFile(ctx.Options.DataDirectory, new Uri(job.Url), cookie);
            if (cookieFile is not null) args.AddRange(["--cookies", cookieFile]);
            if (headers.TryGetValue("Referer", out var referer)) args.AddRange(["--referer", referer]);
            if (headers.TryGetValue("User-Agent", out var ua)) args.AddRange(["--user-agent", ua]);
        }
        args.Add("--");
        args.Add(job.Url);

        var streams = new Dictionary<string, (long Done, long? Total)>();
        string? final = null;
        ctx.SetStatus(JobStatus.Running, "Reading media information");
        try
        {
            var result = await ProcessRunner.RunAsync(ytDlp.Exe, args, line =>
            {
                if (line.StartsWith("FGP ", StringComparison.Ordinal)) OnProgress(ctx, streams, line[4..]);
                else if (line.StartsWith("FGFILE ", StringComparison.Ordinal)) final = line[7..].Trim();
                else if (line.StartsWith("[Merger]", StringComparison.Ordinal)) ctx.SetStatus(JobStatus.Running, "Merging video and audio");
                else if (line.StartsWith("[ExtractAudio]", StringComparison.Ordinal)) ctx.SetStatus(JobStatus.Running, $"Converting to {o.AudioFormat.ToUpperInvariant()}");
                else if (line.StartsWith("[EmbedThumbnail]", StringComparison.Ordinal) || line.StartsWith("[Metadata]", StringComparison.Ordinal)) ctx.SetStatus(JobStatus.Running, "Writing metadata");
            }, ct);
            if (result.ExitCode != 0) throw new JobFailedException(YtDlp.FriendlyError(result.StdErr));
        }
        finally
        {
            if (cookieFile is not null) TryDelete(cookieFile);
        }
        if (final is null || !File.Exists(final)) throw new JobFailedException("yt-dlp finished but the output file was not found.");
        var size = new FileInfo(final).Length;
        job.FilePath = final;
        job.FileName = Path.GetFileName(final);
        job.TotalBytes = size;
        Interlocked.Exchange(ref job.ReceivedBytes, size);
        ctx.Save();
    }

    private static void OnProgress(JobContext ctx, Dictionary<string, (long Done, long? Total)> streams, string payload)
    {
        var p = payload.Split('|');
        if (p.Length < 5) return;
        static long? N(string s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? (long)v : null;
        var done = N(p[1]) ?? 0;
        var total = N(p[2]) ?? N(p[3]);
        lock (streams)
        {
            streams[p[4]] = (done, total);
            var job = ctx.Job;
            Interlocked.Exchange(ref job.ReceivedBytes, streams.Values.Sum(s => s.Done));
            job.TotalBytes = streams.Values.All(s => s.Total is not null) ? streams.Values.Sum(s => s.Total!.Value) : null;
            var phase = streams.Count > 1 ? $"Downloading stream {streams.Count}" : null;
            if (job.Phase != phase) ctx.SetStatus(JobStatus.Running, phase);
        }
    }

    private static string? WriteCookieFile(string dataDir, Uri url, string cookieHeader)
    {
        var dir = Path.Combine(dataDir, "tmp");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"c-{Guid.NewGuid():N}.txt");
        var domain = "." + string.Join('.', url.Host.Split('.').TakeLast(2));
        var sb = new StringBuilder("# Netscape HTTP Cookie File\n");
        foreach (var part in cookieHeader.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0) continue;
            sb.Append($"{domain}\tTRUE\t/\tTRUE\t0\t{part[..eq]}\t{part[(eq + 1)..]}\n");
        }
        File.WriteAllText(path, sb.ToString());
        return path;
    }

    private async Task ConvertAsync(JobContext ctx, MediaOptions o, CancellationToken ct)
    {
        var job = ctx.Job;
        var source = o.Source;
        if (source is null || !File.Exists(source)) throw new JobFailedException("The source file no longer exists.");
        var ffmpeg = ytDlp.Tools.Find("ffmpeg") ?? throw new JobFailedException("FFmpeg is not installed.");
        var ffprobe = ytDlp.Tools.Find("ffprobe") ?? throw new JobFailedException("FFprobe is not installed.");
        var (codec, duration) = await ProbeAudio(ffprobe, source, ct);
        if (codec is null) throw new JobFailedException("This file has no audio track.");

        var fmt = o.AudioFormat;
        var copy = (fmt, codec) is ("m4a", "aac") or ("aac", "aac") or ("mp3", "mp3") or ("opus", "opus") or ("flac", "flac");
        var ext = fmt switch { "aac" => ".aac", _ => "." + fmt };
        var output = FileNames.UniquePath(job.Directory, Path.GetFileNameWithoutExtension(source) + ext);
        var temp = output + ".fgtmp" + ext;
        var q = (o.AudioQuality ?? "192") + "k";
        var args = new List<string> { "-hide_banner", "-nostdin", "-y", "-i", source, "-vn", "-sn", "-dn", "-map", "0:a:0", "-map_metadata", "0" };
        args.AddRange(copy ? ["-c:a", "copy"] : fmt switch
        {
            "mp3" => ["-c:a", "libmp3lame", "-b:a", q],
            "m4a" or "aac" => ["-c:a", "aac", "-b:a", q],
            "opus" => ["-c:a", "libopus", "-b:a", q],
            "flac" => ["-c:a", "flac"],
            _ => ["-c:a", "pcm_s16le"]
        });
        if (fmt == "aac") args.AddRange(["-f", "adts"]);
        args.AddRange(["-progress", "pipe:1", "-nostats", temp]);

        job.Extra = new Dictionary<string, object?> { ["mode"] = "convert", ["progress"] = 0.0, ["copied"] = copy };
        job.TotalBytes = null;
        ctx.SetStatus(JobStatus.Running, copy ? $"Copying audio to {fmt.ToUpperInvariant()} (no re-encode)" : $"Converting to {fmt.ToUpperInvariant()}");
        var sw = Stopwatch.StartNew();
        try
        {
            var result = await ProcessRunner.RunAsync(ffmpeg, args, line =>
            {
                if (duration > 0 && line.StartsWith("out_time_us=", StringComparison.Ordinal) && long.TryParse(line[12..], out var us))
                    job.Extra = new Dictionary<string, object?>(job.Extra!) { ["progress"] = Math.Clamp(us / 1e6 / duration, 0, 1) };
            }, ct, lowPriority: true);
            if (result.ExitCode != 0)
                throw new JobFailedException("FFmpeg could not convert this file: " + result.StdErr.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0));
            File.Move(temp, output);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
        var size = new FileInfo(output).Length;
        job.Extra = new Dictionary<string, object?>(job.Extra!) { ["progress"] = 1.0, ["elapsedMs"] = sw.ElapsedMilliseconds };
        job.FilePath = output;
        job.FileName = Path.GetFileName(output);
        job.TotalBytes = size;
        Interlocked.Exchange(ref job.ReceivedBytes, size);
        ctx.Save();
    }

    private static async Task<(string? Codec, double Duration)> ProbeAudio(string ffprobe, string path, CancellationToken ct)
    {
        var r = await ProcessRunner.RunAsync(ffprobe, ["-v", "error", "-select_streams", "a:0", "-show_entries", "stream=codec_name:format=duration", "-of", "json", path], null, ct, timeout: TimeSpan.FromMinutes(1));
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(r.StdOut) ? "{}" : r.StdOut);
        string? codec = null;
        if (doc.RootElement.TryGetProperty("streams", out var s) && s.GetArrayLength() > 0) codec = s[0].GetProperty("codec_name").GetString();
        var duration = doc.RootElement.TryGetProperty("format", out var f) && f.TryGetProperty("duration", out var d) && double.TryParse(d.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
        return (codec, duration);
    }

    public Task CleanupAsync(Job job, bool deleteFiles)
    {
        if (job.Status == JobStatus.Completed) return Task.CompletedTask;
        var id = job.GetOptions<MediaOptions>()?.MediaId;
        if (string.IsNullOrEmpty(id) || !Directory.Exists(job.Directory)) return Task.CompletedTask;
        foreach (var f in Directory.EnumerateFiles(job.Directory, $"*{id}*"))
        {
            if (!f.Contains($"[{id}]", StringComparison.Ordinal)) continue;
            if (f.EndsWith(".part", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".ytdl", StringComparison.OrdinalIgnoreCase) || f.Contains(".part-Frag", StringComparison.OrdinalIgnoreCase))
                TryDelete(f);
        }
        return Task.CompletedTask;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (Exception) { }
    }
}
