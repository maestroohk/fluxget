using System.Globalization;
using Whisper.net;

namespace FluxGet.Engine.Media;

public sealed record Caption(long StartMs, long EndMs, string Text);

public enum CaptionSpeed { Quiet, Balanced, Fast }

public sealed class LiveCaptions : IDisposable
{
    public const int ChunkMs = 20_000;
    public const int AheadMs = 45_000;

    private readonly Transcriber _transcriber;
    private readonly ToolLocator _tools;
    private readonly string _workDir;
    private readonly string _source;
    private readonly IReadOnlyDictionary<string, string>? _headers;
    private readonly string _model;
    private readonly bool _translate;
    private readonly bool _whole;
    private readonly CaptionSpeed _speed;
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource _languageKnown = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _gate = new();
    private readonly List<Caption> _captions = [];
    private readonly HashSet<int> _done = [];
    private readonly HashSet<int> _busy = [];
    private string _language;
    private long _position;
    private long _durationMs;

    public LiveCaptions(Transcriber transcriber, ToolLocator tools, string workDir, string source, IReadOnlyDictionary<string, string>? headers,
        string model, string language, bool translate, long startMs, long durationMs, bool whole = false, CaptionSpeed speed = CaptionSpeed.Balanced)
    {
        _transcriber = transcriber;
        _tools = tools;
        _workDir = workDir;
        _source = source;
        _headers = headers;
        _model = model;
        _language = language;
        _translate = translate;
        _position = startMs;
        _durationMs = durationMs;
        _whole = whole && durationMs > 0;
        _speed = speed;
        if (language != "auto") _languageKnown.TrySetResult();
        Status = "Starting";
        _ = Task.Run(RunAsync);
    }

    public string Status { get; private set; }
    public string? Error { get; private set; }
    public string? Detail { get; private set; }
    public string Model => _model;
    public string Language => _language;
    public bool Translate => _translate;
    public bool Whole => _whole;
    public CaptionSpeed Speed => _speed;
    public string Source => _source;
    public int Count { get { lock (_gate) return _captions.Count; } }

    private int TotalChunks
    {
        get
        {
            var total = Interlocked.Read(ref _durationMs);
            return total <= 0 ? 0 : (int)((total + ChunkMs - 1) / ChunkMs);
        }
    }

    public double Progress
    {
        get
        {
            var chunks = TotalChunks;
            if (chunks == 0) return 0;
            lock (_gate) return Math.Min(1, _done.Count / (double)chunks);
        }
    }

    public bool Complete
    {
        get
        {
            var chunks = TotalChunks;
            if (chunks == 0) return false;
            lock (_gate) return Enumerable.Range(0, chunks).All(_done.Contains);
        }
    }

    public void Update(long positionMs, long durationMs)
    {
        Interlocked.Exchange(ref _position, Math.Max(0, positionMs));
        if (durationMs > 0) Interlocked.Exchange(ref _durationMs, durationMs);
    }

    public string? TextAt(long ms)
    {
        lock (_gate)
        {
            for (var i = _captions.Count - 1; i >= 0; i--)
            {
                var c = _captions[i];
                if (c.StartMs <= ms && ms < c.EndMs) return c.Text;
            }
        }
        return null;
    }

    public IReadOnlyList<Caption> Captions() { lock (_gate) return _captions.OrderBy(c => c.StartMs).ToList(); }

    public bool Ready(long ms)
    {
        lock (_gate) return _done.Contains((int)(ms / ChunkMs));
    }

    private int? Claim()
    {
        var pos = Interlocked.Read(ref _position);
        var chunks = TotalChunks;
        var first = (int)(pos / ChunkMs);
        var last = (int)((pos + AheadMs) / ChunkMs);
        lock (_gate)
        {
            bool Free(int k) => !_done.Contains(k) && !_busy.Contains(k) && (chunks == 0 || k < chunks);
            int? pick = null;
            for (var k = first; k <= last && pick is null; k++) if (Free(k)) pick = k;
            if (pick is null && _whole)
            {
                for (var k = last + 1; k < chunks && pick is null; k++) if (Free(k)) pick = k;
                for (var k = 0; k < first && pick is null; k++) if (Free(k)) pick = k;
            }
            if (pick is { } p) _busy.Add(p);
            return pick;
        }
    }

    private (int Workers, int Threads) Budget()
    {
        var cores = Environment.ProcessorCount;
        return _speed switch
        {
            CaptionSpeed.Quiet => (1, Math.Clamp(cores / 4, 2, 4)),
            CaptionSpeed.Fast => (Math.Clamp(cores / 8, 1, 3), Math.Clamp(cores / 2, 2, 8)),
            _ => (1, Math.Clamp(cores / 2, 2, 8)),
        };
    }

    private async Task RunAsync()
    {
        var ct = _stop.Token;
        try
        {
            var ffmpeg = _tools.Find("ffmpeg") ?? throw new MediaException("FFmpeg is not available.");
            if (!_transcriber.IsInstalled(_model))
            {
                Status = "Downloading speech model";
                await _transcriber.EnsureModelAsync(_model, p => Status = $"Downloading speech model {p:P0}", ct);
            }
            Directory.CreateDirectory(_workDir);
            using var factory = WhisperFactory.FromPath(_transcriber.ModelFile(_model));
            Status = "Listening";
            var (workers, threads) = Budget();
            await Task.WhenAll(Enumerable.Range(0, workers).Select(i => WorkerAsync(factory, ffmpeg, i, threads, ct)));
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            Error = e is MediaException or JobFailedException ? e.Message : "Subtitles stopped: " + e.Message;
            Status = "Failed";
        }
    }

    private async Task WorkerAsync(WhisperFactory factory, string ffmpeg, int index, int threads, CancellationToken ct)
    {
        if (index > 0) await _languageKnown.Task.WaitAsync(ct);
        while (!ct.IsCancellationRequested)
        {
            if (Claim() is not { } k)
            {
                if (Complete) Status = "Done";
                await Task.Delay(300, ct);
                continue;
            }
            try
            {
                await ProcessChunkAsync(factory, ffmpeg, k, threads, ct);
                lock (_gate) _done.Add(k);
                _languageKnown.TrySetResult();
                Status = Complete ? "Done" : "Listening";
            }
            finally
            {
                lock (_gate) _busy.Remove(k);
            }
        }
    }

    private async Task ProcessChunkAsync(WhisperFactory factory, string ffmpeg, int k, int threads, CancellationToken ct)
    {
        var startMs = (long)k * ChunkMs;
        var wav = Path.Combine(_workDir, $"live-{Guid.NewGuid():N}.wav");
        try
        {
            var args = new List<string> { "-hide_banner", "-nostdin", "-y", "-loglevel", "error" };
            if (_headers is not null)
            {
                if (_headers.TryGetValue("User-Agent", out var ua)) args.AddRange(["-user_agent", ua]);
                if (_headers.TryGetValue("Referer", out var referer)) args.AddRange(["-headers", $"Referer: {referer}\r\n"]);
            }
            args.AddRange(["-ss", (startMs / 1000.0).ToString("0.###", CultureInfo.InvariantCulture), "-t", (ChunkMs / 1000).ToString(CultureInfo.InvariantCulture),
                "-i", _source, "-vn", "-ac", "1", "-ar", "16000", "-c:a", "pcm_s16le", wav]);
            var result = await ProcessRunner.RunAsync(ffmpeg, args, null, ct, lowPriority: true, timeout: TimeSpan.FromSeconds(60));
            if (result.ExitCode != 0 || !File.Exists(wav) || new FileInfo(wav).Length < 3200)
            {
                Detail = $"chunk {k}: no audio (ffmpeg {result.ExitCode}) {result.StdErr.Trim().Split('\n').LastOrDefault()}";
                if (Interlocked.Read(ref _durationMs) <= 0) Interlocked.Exchange(ref _durationMs, startMs);
                return;
            }
            var builder = factory.CreateBuilder().WithLanguage(_language).WithThreads(threads);
            if (_translate) builder = builder.WithTranslate();
            await using var processor = builder.Build();
            await using var audio = File.OpenRead(wav);
            var found = new List<Caption>();
            await foreach (var s in processor.ProcessAsync(audio, ct))
            {
                if (_language == "auto" && !string.IsNullOrEmpty(s.Language) && s.Language != "auto") _language = s.Language;
                var text = s.Text.Trim();
                if (text.Length == 0 || text.StartsWith('[') || text.StartsWith('(')) continue;
                found.Add(new Caption(startMs + (long)s.Start.TotalMilliseconds, Math.Min(startMs + (long)s.End.TotalMilliseconds, startMs + ChunkMs), text));
            }
            lock (_gate) _captions.AddRange(found);
            Detail = $"chunk {k}: {found.Count} lines";
        }
        finally
        {
            try { File.Delete(wav); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    public string? SaveSrt(string mediaPath)
    {
        var list = Captions();
        if (list.Count == 0) return null;
        var lang = _translate ? "en" : _language == "auto" ? "und" : _language;
        return Transcriber.WriteSrt(mediaPath, lang, list.Select(c => (TimeSpan.FromMilliseconds(c.StartMs), TimeSpan.FromMilliseconds(c.EndMs), c.Text)), "fluxget");
    }

    public void Dispose() => _stop.Cancel();
}
