using System.Collections.Concurrent;
using System.Text.Json;

namespace FluxGet.Engine.Media;

public sealed record StreamPlan(
    string PageUrl, string Title, string VideoUrl, string? AudioUrl, IReadOnlyDictionary<string, string> Headers,
    double? Duration, bool Live, int? Height, IReadOnlyList<int> Heights, string? Thumbnail, DateTime ResolvedAt, bool Adaptive = false);

public sealed partial class YtDlp
{
    public const int DefaultStreamHeight = 1080;
    private static readonly string[] DirectExtensions = [".mp4", ".m4v", ".mkv", ".webm", ".mov", ".avi", ".ts", ".m3u8", ".mpd", ".mp3", ".m4a", ".aac", ".ogg", ".opus", ".flac", ".wav"];
    private readonly ConcurrentDictionary<string, StreamPlan> _streams = new();

    public static bool IsDirectMedia(Uri uri) =>
        uri.Scheme is not ("http" or "https") || DirectExtensions.Contains(Path.GetExtension(uri.AbsolutePath), StringComparer.OrdinalIgnoreCase);

    public static string StreamSelector(int height) =>
        $"bv*[height<={height}][vcodec^=avc1][protocol^=http]+ba[ext=m4a][protocol^=http]/" +
        $"bv*[height<={height}][protocol^=http]+ba[protocol^=http]/" +
        $"b[height<={height}]/bv*[height<={height}]+ba/b";

    public async Task<StreamPlan> ResolveStreamAsync(string url, int? height, bool fresh, CancellationToken ct)
    {
        var uri = UrlGuard.Parse(url);
        var h = Math.Clamp(height ?? DefaultStreamHeight, 144, 4320);
        var key = $"{uri}|{h}";
        if (!fresh && _streams.TryGetValue(key, out var hit) && DateTime.UtcNow - hit.ResolvedAt < TimeSpan.FromMinutes(45)) return hit;

        var args = CommonArgs();
        args.AddRange(["-J", "--no-playlist", "-f", StreamSelector(h)]);
        var session = sessions?.For(uri);
        var cookieFile = options is null ? null : CookieFile.Write(options.DataDirectory, uri, session);
        if (cookieFile is not null) args.AddRange(["--cookies", cookieFile]);
        if (session is not null && session.TryGetValue("User-Agent", out var ua)) args.AddRange(["--user-agent", ua]);
        args.Add("--");
        args.Add(uri.ToString());
        ProcessResult result;
        await _analyzeSlots.WaitAsync(ct);
        try
        {
            result = await ProcessRunner.RunAsync(Exe, args, null, ct, timeout: TimeSpan.FromSeconds(90));
        }
        finally
        {
            _analyzeSlots.Release();
            CookieFile.Delete(cookieFile);
        }
        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StdOut)) throw new MediaException(FriendlyError(result.StdErr));
        var plan = ParseStream(result.StdOut, uri.ToString());
        _streams[key] = plan;
        return plan;
    }

    internal static StreamPlan ParseStream(string json, string pageUrl)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        static string? S(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        static int? I(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? (int)v.GetDouble() : null;
        static Dictionary<string, string> Headers(JsonElement e)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (e.TryGetProperty("http_headers", out var h) && h.ValueKind == JsonValueKind.Object)
                foreach (var p in h.EnumerateObject())
                    if (p.Value.ValueKind == JsonValueKind.String && p.Name is "User-Agent" or "Referer" or "Origin") d[p.Name] = p.Value.GetString()!;
            return d;
        }

        if (S(root, "_type") is "playlist" or "multi_video") throw new MediaException("This link is a playlist. Open a single video to stream it, or download the playlist from the Media page.");
        string? video = null, audio = null;
        int? height = null;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("requested_formats", out var parts) && parts.ValueKind == JsonValueKind.Array && parts.GetArrayLength() > 0)
        {
            foreach (var part in parts.EnumerateArray())
            {
                var isVideo = S(part, "vcodec") is { } vc && vc != "none";
                if (isVideo && video is null) { video = S(part, "url"); height = I(part, "height"); headers = Headers(part); }
                else if (!isVideo && audio is null) audio = S(part, "url");
            }
            if (video is null && audio is not null) { video = audio; audio = null; }
        }
        else
        {
            video = S(root, "url");
            height = I(root, "height");
            headers = Headers(root);
        }
        var all = root.TryGetProperty("formats", out var formats) && formats.ValueKind == JsonValueKind.Array ? formats.EnumerateArray().ToList() : [];
        var hls = all.Where(f => S(f, "protocol") is { } p && p.StartsWith("m3u8", StringComparison.Ordinal) && S(f, "manifest_url") is not null).ToList();
        var master = hls.Select(f => S(f, "manifest_url")!).Distinct().ToList();
        var adaptive = master.Count == 1 && hls.Any(f => S(f, "vcodec") is { } vc && vc != "none");
        if (adaptive)
        {
            video = master[0];
            audio = null;
            headers = Headers(hls[0]);
        }
        if (string.IsNullOrEmpty(video)) throw new MediaException("No playable stream was found at this link.");

        var heights = (adaptive ? hls : all).Where(f => S(f, "vcodec") is { } vc && vc != "none").Select(f => I(f, "height") ?? 0).Where(x => x > 0).Distinct().OrderDescending().ToList();
        var duration = root.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetDouble() : (double?)null;
        return new StreamPlan(S(root, "webpage_url") ?? pageUrl, S(root, "title") ?? pageUrl, video, audio, headers, duration,
            root.TryGetProperty("is_live", out var live) && live.ValueKind == JsonValueKind.True, adaptive ? null : height, heights, S(root, "thumbnail"), DateTime.UtcNow, adaptive);
    }
}
