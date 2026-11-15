using System.Diagnostics;
using System.Text.Json;
using FluxGet.Engine;
using FluxGet.Engine.Media;
using FluxGet.Host;
using LibVLCSharp.Shared;
using LibVLCSharp.WinForms;

namespace FluxGet.Desktop.Player;

internal enum PlayerMode { Embedded, Fullscreen, Mini }

internal sealed record QueueItem(string Source, string Title, long? JobId, bool IsStream);

internal sealed record CaptionSettings(string Model, string Language, bool Translate);

internal delegate LiveCaptions CaptionFactory(string source, IReadOnlyDictionary<string, string>? headers, CaptionSettings settings, long startMs, long durationMs);

internal sealed class PlayerController : IDisposable
{
    private static readonly float[] Speeds = [0.25f, 0.5f, 0.75f, 1f, 1.25f, 1.5f, 1.75f, 2f];
    private readonly LibVLC _vlc;
    private readonly MediaPlayer _mp;
    private readonly JobStore _store;
    private readonly Control _ui;
    private readonly List<Action<string>> _sinks = [];
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 250 };
    private readonly List<QueueItem> _queue = [];
    private Media? _media;
    private int _index = -1;
    private string? _error;
    private long _resumedFrom;
    private long _lastSave;
    private bool _hardwareDecoding = true;
    private int _subtitleScale = 100;
    private int _rotation;
    private readonly Stopwatch _openClock = new();
    private long? _seekTarget;
    private readonly Stopwatch _seekClock = new();
    private bool _reachedPlaying;
    private readonly Dictionary<string, List<string>> _extraSubtitles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<string, int?, bool, CancellationToken, Task<StreamPlan>>? _resolver;
    private readonly Dictionary<string, StreamPlan> _plans = new(StringComparer.Ordinal);
    private int _streamHeight = YtDlp.DefaultStreamHeight;
    private int _stalls;
    private volatile bool _stalling;
    private long _stallWindow;
    private long _quietUntil;
    private string? _notice;
    private long _noticeUntil;
    private bool _retried;
    private CancellationTokenSource? _resolving;
    private string? _status;
    private readonly CaptionFactory? _captionFactory;
    private CaptionSettings? _captionSettings;
    private LiveCaptions? _captions;
    private string? _shownCaption;
    private bool _marquee;
    private int _marqueeSize;
    private string? _captionsSaved;

    public PlayerController(Control ui, JobStore store, Func<string, int?, bool, CancellationToken, Task<StreamPlan>>? resolver = null, CaptionFactory? captionFactory = null)
    {
        _ui = ui;
        _store = store;
        _resolver = resolver;
        _captionFactory = captionFactory;
        Core.Initialize();
        _vlc = new LibVLC("--no-video-title-show", "--no-snapshot-preview", "--quiet");
        _mp = new MediaPlayer(_vlc) { EnableMouseInput = false, EnableKeyInput = false };
        View = new VideoView { MediaPlayer = _mp, BackColor = Color.Black, Visible = false, AllowDrop = true };
        _mp.Playing += (_, _) => _ui.BeginInvoke(OnPlaying);
        _mp.EndReached += (_, _) => _ui.BeginInvoke(() => OnEnded());
        _mp.EncounteredError += (_, _) => _ui.BeginInvoke(() => { if (!RetryStream()) SetError(Current?.IsStream == true ? "The stream stopped. Check your connection and try again." : "This file can't be played. It may be damaged or use an unsupported format."); });
        _mp.Buffering += (_, e) =>
        {
            if (!_reachedPlaying || Environment.TickCount64 < Interlocked.Read(ref _quietUntil)) return;
            if (e.Cache < 100f) { if (!_stalling) { _stalling = true; Interlocked.Increment(ref _stalls); } }
            else _stalling = false;
        };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    public VideoView View { get; }
    public PlayerMode Mode { get; set; } = PlayerMode.Embedded;
    public bool HasMedia => _index >= 0 && _index < _queue.Count;
    public long LastOpenMs { get; private set; } = -1;
    public long LastSeekMs { get; private set; } = -1;
    public MediaPlayer Engine => _mp;
    public QueueItem? Current => HasMedia ? _queue[_index] : null;
    public string? CaptionText => _shownCaption;
    public long LastResolveMs { get; private set; } = -1;
    public LiveCaptions? Captions => _captions;
    private StreamPlan? CurrentPlan => Current is { } c ? _plans.GetValueOrDefault(c.Source) : null;

    public event Action<PlayerMode>? ModeRequested;

    public void AddSink(Action<string> sink) => _sinks.Add(sink);
    public void RemoveSink(Action<string> sink) => _sinks.Remove(sink);

    public void Open(IEnumerable<QueueItem> items, bool append = false)
    {
        var list = items.ToList();
        if (list.Count == 0) return;
        if (append && HasMedia)
        {
            _queue.AddRange(list);
            Push();
            return;
        }
        SaveResume();
        _index = -1;
        _queue.Clear();
        _queue.AddRange(list);
        PlayIndex(0);
    }

    public void PlayIndex(int index, long? startAtMs = null)
    {
        if (index < 0 || index >= _queue.Count) return;
        SaveResume();
        _index = index;
        _error = null;
        _resumedFrom = 0;
        var item = _queue[index];
        if (!item.IsStream && !File.Exists(item.Source))
        {
            StopEngine();
            SetError("This file no longer exists. It may have been moved or deleted.");
            return;
        }
        var start = startAtMs ?? ResumePoint(item);
        _resumedFrom = startAtMs is null ? start : 0;
        _retried = false;
        if (NeedsResolve(item) && !_plans.ContainsKey(item.Source)) Resolve(item, index, start);
        else StartMedia(item, start);
    }

    private bool NeedsResolve(QueueItem item) =>
        _resolver is not null && item.IsStream && !item.Source.Contains("/preview/", StringComparison.Ordinal)
        && Uri.TryCreate(item.Source, UriKind.Absolute, out var u) && !YtDlp.IsDirectMedia(u);

    private bool RetryStream()
    {
        if (_retried || Current is not { } item || !NeedsResolve(item)) return false;
        _retried = true;
        var at = Math.Max(0, _mp.Time);
        _plans.Remove(item.Source);
        Resolve(item, _index, at, fresh: true);
        return true;
    }

    private void Resolve(QueueItem item, int index, long start, bool fresh = false)
    {
        _resolving?.Cancel();
        var cts = _resolving = new CancellationTokenSource();
        StopEngine();
        _status = "Getting the stream…";
        Push();
        var height = _streamHeight;
        var clock = Stopwatch.StartNew();
        _ = Task.Run(async () =>
        {
            try
            {
                var plan = await _resolver!(item.Source, height > 0 ? height : null, fresh, cts.Token);
                LastResolveMs = clock.ElapsedMilliseconds;
                _ui.BeginInvoke(() =>
                {
                    if (cts.IsCancellationRequested || _index != index || index >= _queue.Count) return;
                    _status = null;
                    _plans[item.Source] = plan;
                    var titled = item with { Title = plan.Title };
                    _queue[index] = titled;
                    StartMedia(titled, plan.Live ? 0 : start);
                });
            }
            catch (Exception e) when (!cts.IsCancellationRequested)
            {
                _ui.BeginInvoke(() =>
                {
                    if (_index != index) return;
                    _status = null;
                    SetError(e is MediaException ? e.Message : "The stream could not be opened. Check the address or your connection.");
                });
            }
            catch (OperationCanceledException) { }
        });
    }

    private long ResumePoint(QueueItem item)
    {
        if (item.IsStream && !item.Source.Contains("/preview/", StringComparison.Ordinal) && !NeedsResolve(item)) return 0;
        var p = _store.GetPlayback(Key(item));
        if (p is null || p.PositionMs < 10_000) return 0;
        if (p.DurationMs > 0 && p.DurationMs - p.PositionMs < 15_000) return 0;
        return p.PositionMs;
    }

    private void StartMedia(QueueItem item, long startMs)
    {
        StopEngine();
        var plan = item.IsStream && NeedsResolve(item) ? _plans.GetValueOrDefault(item.Source) : null;
        var media = plan is not null ? new Media(_vlc, new Uri(plan.VideoUrl))
            : item.IsStream ? new Media(_vlc, new Uri(item.Source))
            : new Media(_vlc, item.Source, FromType.FromPath);
        if (item.IsStream)
        {
            media.AddOption(":network-caching=1000");
            media.AddOption(":http-reconnect");
        }
        if (plan is not null)
        {
            if (plan.Headers.TryGetValue("User-Agent", out var ua)) media.AddOption(":http-user-agent=" + ua);
            if (plan.Headers.TryGetValue("Referer", out var referer)) media.AddOption(":http-referrer=" + referer);
            if (plan.AudioUrl is { } audio) media.AddSlave(MediaSlaveType.Audio, 4, audio);
            if (plan.Adaptive)
            {
                media.AddOption($":adaptive-maxheight={(_streamHeight > 0 ? _streamHeight : YtDlp.DefaultStreamHeight)}");
                if (_streamHeight > 0) media.AddOption(":adaptive-logic=highest");
            }
        }
        if (!_hardwareDecoding) media.AddOption(":avcodec-hw=none");
        if (_subtitleScale != 100) media.AddOption($":sub-text-scale={_subtitleScale}");
        if (_rotation != 0)
        {
            media.AddOption(":video-filter=transform");
            media.AddOption($":transform-type={_rotation}");
        }
        if (_extraSubtitles.TryGetValue(item.Source, out var subs))
            foreach (var sub in subs.Where(File.Exists)) media.AddSlave(MediaSlaveType.Subtitle, 4, new Uri(sub).AbsoluteUri);
        if (startMs > 0) media.AddOption($":start-time={startMs / 1000.0:0.###}".Replace(',', '.'));
        _media = media;
        _stalls = 0;
        _stalling = false;
        _stallWindow = Environment.TickCount64;
        Interlocked.Exchange(ref _quietUntil, Environment.TickCount64 + 5000);
        SyncCaptions(item, plan, startMs);
        _openClock.Restart();
        _reachedPlaying = false;
        _mp.Play(media);
        Push();
    }

    private void StopEngine()
    {
        if (_mp.IsPlaying || _mp.State is VLCState.Paused or VLCState.Opening or VLCState.Buffering) _mp.Stop();
        _media?.Dispose();
        _media = null;
    }

    private void OnPlaying()
    {
        _reachedPlaying = true;
        if (_openClock.IsRunning)
        {
            LastOpenMs = _openClock.ElapsedMilliseconds;
            _openClock.Stop();
        }
        Push();
    }

    private void OnEnded()
    {
        if (Current is { } item) _store.SavePlayback(Key(item), item.Title, 0, _mp.Length);
        if (_index + 1 < _queue.Count) PlayIndex(_index + 1, 0);
        else Push();
    }

    private void SetError(string message)
    {
        _error = message;
        Push();
    }

    private static string Key(QueueItem item) => item.Source.Contains("/preview/", StringComparison.Ordinal) && item.JobId is { } id ? $"job:{id}" : item.Source;

    private void SaveResume()
    {
        if (Current is not { } item || _mp.Length <= 0) return;
        _store.SavePlayback(Key(item), item.Title, _mp.Time, _mp.Length);
    }

    private void Tick()
    {
        if (!_reachedPlaying && _openClock.IsRunning && _error is null && Current is { } opening
            && _mp.State is VLCState.Opening or VLCState.Buffering or VLCState.NothingSpecial
            && _openClock.ElapsedMilliseconds > (opening.IsStream ? 25_000 : 10_000))
        {
            _openClock.Stop();
            if (!RetryStream())
            {
                StopEngine();
                SetError(opening.IsStream ? "The stream did not start. Check the address or your connection." : "This file can't be played. It may be damaged or not a media file.");
            }
        }
        AdaptToStalls();
        if (_seekTarget is { } target && Math.Abs(_mp.Time - target) < 1500)
        {
            LastSeekMs = _seekClock.ElapsedMilliseconds;
            _seekTarget = null;
        }
        if (_mp.IsPlaying && Environment.TickCount64 - _lastSave > 5000)
        {
            _lastSave = Environment.TickCount64;
            SaveResume();
        }
        UpdateCaptions();
        if (_sinks.Count > 0 && (HasMedia || _error is not null)) Push();
    }

    public void Push()
    {
        var json = JsonSerializer.Serialize(new { type = "playerState", state = Snapshot() }, Options);
        foreach (var sink in _sinks.ToArray()) sink(json);
    }

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public object Snapshot()
    {
        var item = Current;
        return new
        {
            loaded = item is not null,
            title = item?.Title,
            source = item?.Source,
            isStream = item?.IsStream ?? false,
            state = _error is not null ? "error" : _status is not null ? "opening" : _mp.State.ToString().ToLowerInvariant(),
            error = _error,
            status = _status ?? (Environment.TickCount64 < _noticeUntil ? _notice : null),
            live = CurrentPlan?.Live ?? false,
            quality = CurrentPlan is { Adaptive: true } ? _streamHeight : CurrentPlan?.Height,
            adaptive = CurrentPlan?.Adaptive ?? false,
            qualities = CurrentPlan?.Heights.Where(h => h <= 2160) ?? [],
            time = _mp.Time,
            length = _mp.Length,
            volume = _mp.Volume,
            muted = _mp.Mute,
            rate = Math.Round(_mp.Rate, 2),
            seekable = _mp.IsSeekable,
            hasVideo = _mp.VideoTrackCount > 0,
            audioTracks = (_mp.AudioTrackDescription ?? []).Select(t => new { id = t.Id, name = t.Name }),
            audioTrack = _mp.AudioTrack,
            subtitleTracks = (_mp.SpuDescription ?? []).Select(t => new { id = t.Id, name = t.Name }),
            subtitleTrack = _mp.Spu,
            subtitleDelayMs = _mp.SpuDelay / 1000,
            audioDelayMs = _mp.AudioDelay / 1000,
            aspect = _mp.AspectRatio,
            crop = _mp.CropGeometry,
            zoom = _mp.Scale,
            rotation = _rotation,
            hardwareDecoding = _hardwareDecoding,
            subtitleScale = _subtitleScale,
            brightness = _mp.AdjustFloat(VideoAdjustOption.Brightness),
            contrast = _mp.AdjustFloat(VideoAdjustOption.Contrast),
            saturation = _mp.AdjustFloat(VideoAdjustOption.Saturation),
            mode = Mode.ToString().ToLowerInvariant(),
            resumedFrom = _resumedFrom,
            index = _index,
            queue = _queue.Select(q => new { q.Title, q.Source, q.JobId, q.IsStream }),
            speeds = Speeds,
            captions = _captions is null ? null : new
            {
                status = _captions.Status,
                error = _captions.Error,
                model = _captions.Model,
                language = _captions.Language,
                translate = _captions.Translate,
                count = _captions.Count,
                ready = _captions.Ready(_mp.Time),
                saved = _captionsSaved
            },
            captionsAvailable = _captionFactory is not null
        };
    }

    public void Handle(JsonElement msg)
    {
        string S(string n) => msg.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
        double D(string n) => msg.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
        bool B(string n) => msg.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.True;

        switch (S("cmd"))
        {
            case "toggle": Toggle(); break;
            case "play": if (HasMedia && _error is null) _mp.Play(); else if (HasMedia) PlayIndex(_index); break;
            case "pause": _mp.SetPause(true); break;
            case "stop": Stop(); break;
            case "seek": SeekTo((long)D("ms")); break;
            case "seekBy": SeekBy((long)D("ms")); break;
            case "frame": _mp.NextFrame(); break;
            case "volume": _mp.Volume = (int)Math.Clamp(D("value"), 0, 200); _mp.Mute = false; break;
            case "volumeBy": _mp.Volume = Math.Clamp(_mp.Volume + (int)D("value"), 0, 200); break;
            case "mute": _mp.Mute = !_mp.Mute; break;
            case "rate": _mp.SetRate((float)Math.Clamp(D("value"), 0.25, 4)); break;
            case "rateStep": StepRate((int)D("value")); break;
            case "audioTrack": _mp.SetAudioTrack((int)D("id")); break;
            case "subtitleTrack":
                if ((int)D("id") >= 0 && _captions is not null) DisableCaptions();
                _mp.SetSpu((int)D("id"));
                break;
            case "subtitleDelay": _mp.SetSpuDelay((long)(D("ms") * 1000)); break;
            case "audioDelay": _mp.SetAudioDelay((long)(D("ms") * 1000)); break;
            case "aspect": _mp.AspectRatio = string.IsNullOrEmpty(S("value")) ? null : S("value"); break;
            case "crop": _mp.CropGeometry = string.IsNullOrEmpty(S("value")) ? null : S("value"); break;
            case "zoom": _mp.Scale = (float)Math.Clamp(D("value"), 0, 4); break;
            case "adjust": Adjust(S("name"), (float)D("value")); break;
            case "rotate": _rotation = ((int)D("value") % 360 + 360) % 360; Reopen(); break;
            case "hardwareDecoding": _hardwareDecoding = B("value"); Reopen(); break;
            case "subtitleScale": _subtitleScale = (int)Math.Clamp(D("value"), 50, 300); Reopen(); break;
            case "next": if (_index + 1 < _queue.Count) PlayIndex(_index + 1); break;
            case "prev": if (_mp.Time > 5000 || _index == 0) SeekTo(0); else PlayIndex(_index - 1); break;
            case "playIndex": PlayIndex((int)D("index")); break;
            case "startOver": SeekTo(0); _resumedFrom = 0; break;
            case "removeIndex": RemoveIndex((int)D("index")); break;
            case "openPaths":
                var paths = msg.GetProperty("paths").EnumerateArray().Select(p => p.GetString()!).Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
                Open(paths.Select(FromPath), B("append"));
                break;
            case "openJob": OpenJob((long)D("jobId"), S("path"), S("title"), B("append")); break;
            case "openUrl": OpenUrl(S("url"), B("append")); break;
            case "quality": ChangeQuality((int)D("value")); break;
            case "liveCaptions":
                if (B("on")) EnableCaptions(new CaptionSettings(S("model") is { Length: > 0 } model && Transcriber.IsKnownModel(model) ? model : "base", S("language") is { Length: > 0 } lang ? lang : "auto", B("translate")));
                else DisableCaptions();
                break;
            case "addSubtitleFile": AddSubtitle(S("path")); break;
            case "mode": ModeRequested?.Invoke(Enum.TryParse<PlayerMode>(S("value"), true, out var m) ? m : PlayerMode.Embedded); break;
        }
        Push();
    }

    public void Toggle()
    {
        if (_error is not null && HasMedia) { PlayIndex(_index); return; }
        if (_mp.State == VLCState.Ended) { PlayIndex(_index, 0); return; }
        if (_mp.State is VLCState.Stopped or VLCState.NothingSpecial && HasMedia) { PlayIndex(_index); return; }
        _mp.Pause();
    }

    public void SeekTo(long ms)
    {
        if (!_mp.IsSeekable) return;
        var target = Math.Clamp(ms, 0, Math.Max(0, _mp.Length - 500));
        _seekTarget = target;
        Interlocked.Exchange(ref _quietUntil, Environment.TickCount64 + 5000);
        _seekClock.Restart();
        _mp.Time = target;
    }

    public void SeekBy(long ms) => SeekTo(_mp.Time + ms);

    private void StepRate(int direction)
    {
        var i = Array.FindIndex(Speeds, s => Math.Abs(s - _mp.Rate) < 0.01f);
        if (i < 0) i = Array.IndexOf(Speeds, 1f);
        _mp.SetRate(Speeds[Math.Clamp(i + direction, 0, Speeds.Length - 1)]);
    }

    private void Adjust(string name, float value)
    {
        _mp.SetAdjustInt(VideoAdjustOption.Enable, 1);
        var option = name switch
        {
            "brightness" => VideoAdjustOption.Brightness,
            "contrast" => VideoAdjustOption.Contrast,
            "saturation" => VideoAdjustOption.Saturation,
            _ => (VideoAdjustOption?)null
        };
        if (option is { } o) _mp.SetAdjustFloat(o, Math.Clamp(value, 0, 2));
        if (name == "reset")
        {
            _mp.SetAdjustFloat(VideoAdjustOption.Brightness, 1);
            _mp.SetAdjustFloat(VideoAdjustOption.Contrast, 1);
            _mp.SetAdjustFloat(VideoAdjustOption.Saturation, 1);
            _mp.SetAdjustInt(VideoAdjustOption.Enable, 0);
        }
    }

    private void Reopen()
    {
        if (Current is not { } item) return;
        var at = _mp.Time;
        var paused = _mp.State == VLCState.Paused;
        StartMedia(item, at);
        if (paused) _ui.BeginInvoke(async () => { await Task.Delay(400); _mp.SetPause(true); });
    }

    private void RemoveIndex(int index)
    {
        if (index < 0 || index >= _queue.Count) return;
        if (index == _index)
        {
            Stop();
            _queue.RemoveAt(index);
            _index = -1;
            if (_queue.Count > 0) PlayIndex(Math.Min(index, _queue.Count - 1));
            return;
        }
        _queue.RemoveAt(index);
        if (index < _index) _index--;
    }

    private void EnableCaptions(CaptionSettings settings)
    {
        _captionSettings = settings;
        _captions?.Dispose();
        _captions = null;
        if (Current is { } item) SyncCaptions(item, CurrentPlan, _mp.Time);
    }

    private void DisableCaptions()
    {
        _captionSettings = null;
        _captions?.Dispose();
        _captions = null;
        ShowCaption(null);
    }

    private void SyncCaptions(QueueItem item, StreamPlan? plan, long startMs)
    {
        if (_captionSettings is null || _captionFactory is null) return;
        if (_mp.Spu >= 0) _mp.SetSpu(-1);
        var source = plan is not null ? plan.AudioUrl ?? plan.VideoUrl : item.Source;
        if (_captions is { } existing && existing.Source == source && existing.Model == _captionSettings.Model && existing.Translate == _captionSettings.Translate) return;
        _captions?.Dispose();
        _captionsSaved = null;
        ShowCaption(null);
        var duration = plan?.Duration is { } d ? (long)(d * 1000) : _mp.Length;
        _captions = _captionFactory(source, plan?.Headers, _captionSettings, startMs, plan?.Live == true ? 0 : duration);
    }

    private void UpdateCaptions()
    {
        if (_captions is null) return;
        if (_mp.Spu >= 0) _mp.SetSpu(-1);
        var time = _mp.Time;
        _captions.Update(time, CurrentPlan?.Live == true ? 0 : _mp.Length);
        ShowCaption(_mp.State is VLCState.Playing or VLCState.Paused ? _captions.TextAt(time) : null);
        if (_captionsSaved is null && Current is { IsStream: false } item && _captions.Complete && _captions.Count > 0)
        {
            try { _captionsSaved = _captions.SaveSrt(item.Source); }
            catch (IOException) { _captionsSaved = ""; }
            catch (UnauthorizedAccessException) { _captionsSaved = ""; }
        }
    }

    private void ShowCaption(string? text)
    {
        if (text == _shownCaption) return;
        _shownCaption = text;
        if (string.IsNullOrEmpty(text))
        {
            _mp.SetMarqueeString(VideoMarqueeOption.Text, " ");
            return;
        }
        uint w = 0, h = 0;
        try { _mp.Size(0, ref w, ref h); } catch (Exception) { }
        var size = h > 0 ? Math.Clamp((int)(h / 17), 16, 72) : 28;
        if (!_marquee)
        {
            _marquee = true;
            _mp.SetMarqueeInt(VideoMarqueeOption.Position, 8);
            _mp.SetMarqueeInt(VideoMarqueeOption.Color, 0xFFFFFF);
            _mp.SetMarqueeInt(VideoMarqueeOption.Opacity, 255);
            _mp.SetMarqueeInt(VideoMarqueeOption.Timeout, 0);
            _mp.SetMarqueeInt(VideoMarqueeOption.Enable, 1);
        }
        if (size != _marqueeSize)
        {
            _marqueeSize = size;
            _mp.SetMarqueeInt(VideoMarqueeOption.Size, size);
            _mp.SetMarqueeInt(VideoMarqueeOption.Y, Math.Max(12, size));
        }
        _mp.SetMarqueeString(VideoMarqueeOption.Text, Wrap(text.Replace("$", "$$").Replace("%", "%%"), 46));
    }

    private static string Wrap(string text, int width)
    {
        if (text.Length <= width) return text;
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var lines = new List<string>();
        var line = "";
        foreach (var w in words)
        {
            if (line.Length > 0 && line.Length + 1 + w.Length > width) { lines.Add(line); line = w; }
            else line = line.Length == 0 ? w : line + " " + w;
        }
        if (line.Length > 0) lines.Add(line);
        return string.Join('\n', lines.TakeLast(3));
    }

    private void AdaptToStalls()
    {
        var now = Environment.TickCount64;
        if (now - _stallWindow > 30_000) { _stallWindow = now; Interlocked.Exchange(ref _stalls, 0); }
        if (_stalls < 2 || CurrentPlan is not { Adaptive: true } plan || Current is not { } item) return;
        var lower = plan.Heights.Where(h => h < _streamHeight && h >= 240).DefaultIfEmpty(0).Max();
        if (lower == 0) return;
        _streamHeight = lower;
        _notice = $"Lowered to {lower}p to keep playback smooth";
        _noticeUntil = now + 6000;
        StartMedia(item, Math.Max(0, _mp.Time));
    }

    private void ChangeQuality(int height)
    {
        _streamHeight = height <= 0 ? 0 : Math.Clamp(height, 144, 2160);
        if (Current is not { } item || !NeedsResolve(item)) return;
        var at = _mp.Time;
        if (CurrentPlan is { Adaptive: true }) { StartMedia(item, at); return; }
        _plans.Remove(item.Source);
        Resolve(item, _index, at);
    }

    public void Stop()
    {
        _resolving?.Cancel();
        _status = null;
        SaveResume();
        StopEngine();
    }

    public void Close()
    {
        _captions?.Dispose();
        _captions = null;
        ShowCaption(null);
        Stop();
        _queue.Clear();
        _index = -1;
        _error = null;
        Push();
    }

    private static QueueItem FromPath(string path)
    {
        if (Uri.TryCreate(path, UriKind.Absolute, out var u) && !u.IsFile) return new QueueItem(path, StreamTitle(u), null, true);
        var local = u is { IsFile: true } ? u.LocalPath : path;
        return new QueueItem(local, Path.GetFileNameWithoutExtension(local), null, false);
    }

    private static string StreamTitle(Uri u) => Uri.UnescapeDataString(u.Segments.LastOrDefault()?.Trim('/') is { Length: > 0 } s ? s : u.Host);

    public Func<long, string?>? PreviewUrlFactory { get; set; }

    private void OpenJob(long jobId, string path, string title, bool append)
    {
        if (!string.IsNullOrEmpty(path) && File.Exists(path))
        {
            Open([new QueueItem(path, string.IsNullOrEmpty(title) ? Path.GetFileNameWithoutExtension(path) : title, jobId, false)], append);
            return;
        }
        if (PreviewUrlFactory?.Invoke(jobId) is { } url)
            Open([new QueueItem(url, string.IsNullOrEmpty(title) ? "Preview" : title, jobId, true)], append);
        else
            SetError("This download can be played once it has finished.");
    }

    private void OpenUrl(string url, bool append)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https" or "rtsp" or "rtmp" or "mms"))
        {
            SetError("Enter an http, https, rtsp or rtmp address.");
            return;
        }
        Open([new QueueItem(u.ToString(), YtDlp.IsDirectMedia(u) ? StreamTitle(u) : u.Host.Replace("www.", ""), null, true)], append);
    }

    private void AddSubtitle(string path)
    {
        if (!File.Exists(path) || Current is not { } item) return;
        if (!_extraSubtitles.TryGetValue(item.Source, out var list)) _extraSubtitles[item.Source] = list = [];
        if (!list.Contains(path, StringComparer.OrdinalIgnoreCase)) list.Add(path);
        if (_mp.State is VLCState.Playing or VLCState.Paused or VLCState.Buffering) _mp.AddSlave(MediaSlaveType.Subtitle, new Uri(path).AbsoluteUri, true);
    }

    public void RefreshVideoOutput()
    {
        if (_mp.Hwnd == View.Handle) return;
        _mp.Hwnd = View.Handle;
        var track = _mp.VideoTrack;
        if (track < 0 || !HasMedia) return;
        _mp.SetVideoTrack(-1);
        _mp.SetVideoTrack(track);
    }

    public (int Displayed, int Lost) CurrentMediaStats()
    {
        if (_media is null) return (0, 0);
        var st = _media.Statistics;
        return (st.DisplayedPictures, st.LostPictures);
    }

    public void Dispose()
    {
        _captions?.Dispose();
        SaveResume();
        _timer.Dispose();
        StopEngine();
        _mp.Dispose();
        View.Dispose();
        _vlc.Dispose();
    }
}
