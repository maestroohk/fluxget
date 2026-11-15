using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Whisper.net;

namespace FluxGet.Engine.Media;

public sealed record WhisperModelInfo(string Id, string Label, string Description, long ApproxSize, bool Installed);

public sealed class TranscriptionTask
{
    public required string Id { get; init; }
    public required string MediaPath { get; init; }
    public required string Model { get; init; }
    public required string Language { get; init; }
    public bool Translate { get; init; }
    public string Status { get; set; } = "queued";
    public double Progress { get; set; }
    public string? Detail { get; set; }
    public string? OutputPath { get; set; }
    public string? DetectedLanguage { get; set; }
    public int Segments { get; set; }
    public string? Error { get; set; }
    public DateTime CreatedAt { get; } = DateTime.UtcNow;
    public long ElapsedMs { get; set; }
    internal CancellationTokenSource Cts { get; } = new();
}

public sealed class Transcriber(EngineOptions options, ToolLocator tools)
{
    private static readonly (string Id, string Label, string Description, long Size)[] Models =
    [
        ("tiny", "Tiny", "Fastest, rough accuracy", 77_691_713),
        ("base", "Base", "Balanced — recommended", 147_951_465),
        ("small", "Small", "Most accurate, slower", 487_601_967)
    ];

    private readonly ConcurrentDictionary<string, TranscriptionTask> _tasks = new();
    private readonly SemaphoreSlim _slot = new(1, 1);
    private readonly SemaphoreSlim _download = new(1, 1);
    private static readonly HttpClient Http = new(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };

    private string ModelsDirectory => Path.Combine(options.DataDirectory, "models");
    private string ModelPath(string id) => Path.Combine(ModelsDirectory, $"ggml-{id}.bin");
    public string ModelFile(string id) => ModelPath(id);
    public bool IsInstalled(string id) => File.Exists(ModelPath(id));
    public static bool IsKnownModel(string id) => Models.Any(m => m.Id == id);

    public IReadOnlyList<WhisperModelInfo> ListModels() =>
        Models.Select(m => new WhisperModelInfo(m.Id, m.Label, m.Description, m.Size, File.Exists(ModelPath(m.Id)))).ToList();

    public IReadOnlyList<TranscriptionTask> Tasks() => _tasks.Values.OrderByDescending(t => t.CreatedAt).ToList();

    public TranscriptionTask? Get(string id) => _tasks.TryGetValue(id, out var t) ? t : null;

    public TranscriptionTask Start(string mediaPath, string model, string language, bool translate)
    {
        if (!File.Exists(mediaPath)) throw new ArgumentException("That media file no longer exists.");
        if (Models.All(m => m.Id != model)) throw new ArgumentException("Unknown speech model.");
        if (language != "auto" && (language.Length is < 2 or > 3 || !language.All(char.IsLetter))) throw new ArgumentException("Unknown language.");
        var running = _tasks.Values.FirstOrDefault(t => t.MediaPath == mediaPath && t.Status is "queued" or "model" or "audio" or "transcribing");
        if (running is not null) return running;
        var task = new TranscriptionTask { Id = Guid.NewGuid().ToString("N")[..12], MediaPath = mediaPath, Model = model, Language = language, Translate = translate };
        _tasks[task.Id] = task;
        _ = Task.Run(() => RunAsync(task));
        return task;
    }

    public void Cancel(string id)
    {
        if (_tasks.TryGetValue(id, out var t)) t.Cts.Cancel();
    }

    private async Task RunAsync(TranscriptionTask task)
    {
        var ct = task.Cts.Token;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        string? wav = null;
        try
        {
            await _slot.WaitAsync(ct);
            try
            {
                task.Status = "model";
                await EnsureModelAsync(task.Model, p => { task.Progress = p; task.Detail = $"Downloading the {task.Model} speech model ({p:P0})"; }, ct);

                task.Status = "audio";
                task.Progress = 0;
                task.Detail = "Extracting audio";
                wav = Path.Combine(Path.GetTempPath(), $"fluxget-{task.Id}.wav");
                var ffmpeg = tools.Find("ffmpeg") ?? throw new JobFailedException("FFmpeg is not available.");
                var extract = await ProcessRunner.RunAsync(ffmpeg, ["-hide_banner", "-nostdin", "-y", "-i", task.MediaPath, "-vn", "-ac", "1", "-ar", "16000", "-c:a", "pcm_s16le", wav], null, ct, lowPriority: true);
                if (extract.ExitCode != 0 || !File.Exists(wav)) throw new JobFailedException("This file has no audio track FFmpeg can read.");

                task.Status = "transcribing";
                task.Detail = "Listening";
                var segments = new List<SegmentData>();
                using (var factory = WhisperFactory.FromPath(ModelPath(task.Model)))
                {
                    var builder = factory.CreateBuilder()
                        .WithLanguage(task.Language)
                        .WithThreads(Math.Clamp(Environment.ProcessorCount / 2, 2, 8))
                        .WithProgressHandler(p => { task.Progress = p / 100.0; task.Detail = $"Transcribing ({p}%)"; });
                    if (task.Translate) builder = builder.WithTranslate();
                    await using var processor = builder.Build();
                    await using var audio = File.OpenRead(wav);
                    await foreach (var segment in processor.ProcessAsync(audio, ct))
                    {
                        segments.Add(segment);
                        task.Segments = segments.Count;
                        task.DetectedLanguage ??= segment.Language;
                    }
                }
                if (segments.Count == 0) throw new JobFailedException("No speech was found in this file.");

                var lang = task.Translate ? "en" : task.Language != "auto" ? task.Language : task.DetectedLanguage ?? "und";
                task.OutputPath = WriteSrt(task.MediaPath, lang, segments.Select(x => (x.Start, x.End, x.Text)));
                task.Status = "done";
                task.Progress = 1;
                task.Detail = $"{segments.Count} subtitles";
            }
            finally
            {
                _slot.Release();
            }
        }
        catch (OperationCanceledException)
        {
            task.Status = "cancelled";
            task.Detail = "Cancelled";
        }
        catch (Exception e)
        {
            task.Status = "failed";
            task.Error = e is JobFailedException ? e.Message : "Subtitle generation failed: " + e.Message;
        }
        finally
        {
            task.ElapsedMs = clock.ElapsedMilliseconds;
            if (wav is not null) try { File.Delete(wav); } catch (Exception) { }
        }
    }

    public async Task EnsureModelAsync(string id, Action<double>? progress, CancellationToken ct)
    {
        var path = ModelPath(id);
        if (File.Exists(path)) return;
        await _download.WaitAsync(ct);
        try
        {
            if (File.Exists(path)) return;
            Directory.CreateDirectory(ModelsDirectory);
            var url = $"https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-{id}.bin";
            using var head = await Http.SendAsync(new HttpRequestMessage(HttpMethod.Head, url), ct);
            var expected = head.Headers.TryGetValues("X-Linked-ETag", out var etags) ? etags.First().Trim('"').ToLowerInvariant() : null;
            var size = head.Headers.TryGetValues("X-Linked-Size", out var sizes) && long.TryParse(sizes.First(), out var s) ? s : 0;
            var target = head.StatusCode is HttpStatusCode.Found or HttpStatusCode.Redirect or HttpStatusCode.TemporaryRedirect ? head.Headers.Location : new Uri(url);
            if (target is null || expected is null || expected.Length != 64) throw new JobFailedException("The speech model could not be located.");
            if (!target.IsAbsoluteUri) target = new Uri(new Uri(url), target);

            var part = path + ".part";
            using (var response = await Http.GetAsync(target, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength ?? size;
                await using var input = await response.Content.ReadAsStreamAsync(ct);
                await using var output = File.Create(part);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[1 << 20];
                long done = 0;
                int n;
                while ((n = await input.ReadAsync(buffer, ct)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, n), ct);
                    hash.AppendData(buffer, 0, n);
                    done += n;
                    if (total > 0) progress?.Invoke((double)done / total);
                }
                if (Convert.ToHexStringLower(hash.GetHashAndReset()) != expected)
                {
                    output.Close();
                    File.Delete(part);
                    throw new JobFailedException("The downloaded speech model failed its integrity check.");
                }
            }
            File.Move(part, path, true);
        }
        finally
        {
            _download.Release();
        }
    }

    public static string WriteSrt(string mediaPath, string language, IEnumerable<(TimeSpan Start, TimeSpan End, string Text)> segments, string? tag = null)
    {
        var sb = new StringBuilder();
        var i = 1;
        foreach (var s in segments)
        {
            var text = s.Text.Trim();
            if (text.Length == 0 || text is "[BLANK_AUDIO]" or "[MUSIC]") continue;
            sb.Append(i++).Append('\n')
              .Append(Stamp(s.Start)).Append(" --> ").Append(Stamp(s.End)).Append('\n')
              .Append(text).Append("\n\n");
        }
        var dir = Path.GetDirectoryName(mediaPath)!;
        var stem = Path.GetFileNameWithoutExtension(mediaPath);
        var path = Path.Combine(dir, tag is null ? $"{stem}.{language}.srt" : $"{stem}.{language}.{tag}.srt");
        for (var n = 2; tag is null && File.Exists(path); n++) path = Path.Combine(dir, $"{stem}.{language}.{n}.srt");
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        return path;
    }

    private static string Stamp(TimeSpan t) => t.ToString(@"hh\:mm\:ss\,fff", CultureInfo.InvariantCulture);
}
