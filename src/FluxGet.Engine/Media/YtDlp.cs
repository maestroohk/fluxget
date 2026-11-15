using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FluxGet.Engine.Media;

public sealed record VideoOption(int Height, double? Fps, string Ext, string Vcodec, long? Size, bool Estimated);
public sealed record AudioSource(double? Abr, string Ext, string Acodec, long? Size);
public sealed record MediaEntry(string Id, string Url, string Title, double? Duration, string? Thumbnail);

public sealed record MediaInfo(
    string Type, string Url, string Id, string Title, string? Uploader, double? Duration, string? Thumbnail, string Extractor,
    IReadOnlyList<VideoOption> Video, IReadOnlyList<AudioSource> Audio, IReadOnlyList<string> Subtitles,
    IReadOnlyList<MediaEntry> Entries, int? EntryCount, bool HasMore, bool Live);

public sealed class MediaException(string message) : Exception(message);

public sealed partial class YtDlp(ToolLocator tools, EngineOptions? options = null, SiteSessions? sessions = null)
{
    public const int PageSize = 100;
    private readonly SemaphoreSlim _analyzeSlots = new(3);
    private readonly ConcurrentDictionary<string, (DateTime At, MediaInfo Info)> _cache = new();

    public ToolLocator Tools => tools;

    public string Exe => tools.Find("yt-dlp") ?? throw new MediaException("yt-dlp is not installed. Media downloads need it; the FluxGet installer includes it.");

    public List<string> CommonArgs()
    {
        var args = new List<string> { "--no-colors", "--encoding", "utf-8", "--no-warnings", "--socket-timeout", "30" };
        if (tools.JsRuntime(out var kind) is { } js) args.AddRange(["--js-runtimes", $"{kind}:{js}"]);
        if (tools.Find("ffmpeg") is { } ffmpeg) args.AddRange(["--ffmpeg-location", ffmpeg]);
        return args;
    }

    public async Task<MediaInfo> AnalyzeAsync(string url, int start, CancellationToken ct)
    {
        var uri = UrlGuard.Parse(url);
        var key = $"{uri}|{start}";
        if (_cache.TryGetValue(key, out var hit) && DateTime.UtcNow - hit.At < TimeSpan.FromMinutes(10)) return hit.Info;

        await _analyzeSlots.WaitAsync(ct);
        try
        {
            var args = CommonArgs();
            args.AddRange(["-J", "--flat-playlist", "--playlist-items", $"{Math.Max(1, start)}:{Math.Max(1, start) + PageSize - 1}"]);
            if (IsSingleVideoUrl(uri)) args.Add("--no-playlist");
            var session = sessions?.For(uri);
            var cookieFile = options is null ? null : CookieFile.Write(options.DataDirectory, uri, session);
            if (cookieFile is not null) args.AddRange(["--cookies", cookieFile]);
            if (session is not null && session.TryGetValue("User-Agent", out var ua)) args.AddRange(["--user-agent", ua]);
            args.Add("--");
            args.Add(uri.ToString());
            ProcessResult result;
            try
            {
                result = await ProcessRunner.RunAsync(Exe, args, null, ct, timeout: TimeSpan.FromMinutes(2));
            }
            finally
            {
                CookieFile.Delete(cookieFile);
            }
            if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StdOut))
                throw new MediaException(FriendlyError(result.StdErr));
            var info = Parse(result.StdOut, uri.ToString(), start);
            _cache[key] = (DateTime.UtcNow, info);
            return info;
        }
        finally
        {
            _analyzeSlots.Release();
        }
    }

    private static bool IsSingleVideoUrl(Uri uri) =>
        uri.Host.Contains("youtube.com", StringComparison.OrdinalIgnoreCase) && uri.AbsolutePath == "/watch" && uri.Query.Contains("v=");

    internal static MediaInfo Parse(string json, string url, int start)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        string? S(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        double? D(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
        long? L(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? (long)v.GetDouble() : null;

        var extractor = S(root, "extractor_key") ?? S(root, "extractor") ?? "Web";
        var title = S(root, "title") ?? S(root, "id") ?? url;
        var thumb = S(root, "thumbnail") ?? BestThumbnail(root);

        if (S(root, "_type") is "playlist" or "multi_video")
        {
            var entries = new List<MediaEntry>();
            if (root.TryGetProperty("entries", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in list.EnumerateArray())
                {
                    var eurl = S(e, "url") ?? S(e, "webpage_url");
                    if (eurl is null || !eurl.StartsWith("http", StringComparison.OrdinalIgnoreCase)) continue;
                    entries.Add(new MediaEntry(S(e, "id") ?? eurl, eurl, S(e, "title") ?? eurl, D(e, "duration"), S(e, "thumbnail") ?? BestThumbnail(e)));
                }
            }
            var count = (int?)L(root, "playlist_count");
            var hasMore = entries.Count >= PageSize && (count is null || count > start - 1 + entries.Count);
            return new MediaInfo("playlist", url, S(root, "id") ?? url, title, S(root, "uploader") ?? S(root, "channel"), null, thumb, extractor,
                [], [], [], entries, count, hasMore, false);
        }

        var formats = root.TryGetProperty("formats", out var f) && f.ValueKind == JsonValueKind.Array ? f.EnumerateArray().ToList() : [];
        var audio = formats
            .Where(x => S(x, "vcodec") == "none" && S(x, "acodec") is { } ac && ac != "none" && S(x, "protocol") is "https" or "http" or null)
            .Select(x => new AudioSource(D(x, "abr") ?? D(x, "tbr"), S(x, "ext") ?? "", S(x, "acodec")!, L(x, "filesize") ?? L(x, "filesize_approx")))
            .OrderByDescending(a => a.Abr ?? 0)
            .ToList();
        var bestAudio = audio.FirstOrDefault();
        var video = formats
            .Where(x => S(x, "vcodec") is { } vc && vc != "none" && L(x, "height") is > 0)
            .GroupBy(x => (int)L(x, "height")!.Value)
            .Select(g =>
            {
                var best = g
                    .OrderByDescending(x => S(x, "protocol") is "https" or "http")
                    .ThenByDescending(x => L(x, "filesize") is not null || L(x, "filesize_approx") is not null)
                    .ThenByDescending(x => D(x, "tbr") ?? 0)
                    .First();
                var exact = L(best, "filesize");
                var size = exact ?? L(best, "filesize_approx");
                var separateAudio = S(best, "acodec") is null or "none";
                if (size is not null && separateAudio) size = bestAudio?.Size is { } a ? size + a : null;
                return new VideoOption(g.Key, D(best, "fps"), S(best, "ext") ?? "", S(best, "vcodec") ?? "", size, exact is null || (separateAudio && bestAudio?.Size is null));
            })
            .OrderByDescending(v => v.Height)
            .ToList();
        var subs = root.TryGetProperty("subtitles", out var st) && st.ValueKind == JsonValueKind.Object
            ? st.EnumerateObject().Select(p => p.Name).Where(n => n != "live_chat").ToList()
            : [];
        return new MediaInfo("video", S(root, "webpage_url") ?? url, S(root, "id") ?? "", title, S(root, "uploader") ?? S(root, "channel") ?? S(root, "uploader_id"),
            D(root, "duration"), thumb, extractor, video, audio.Take(3).ToList(), subs, [], null, false,
            root.TryGetProperty("is_live", out var live) && live.ValueKind == JsonValueKind.True);
    }

    private static string? BestThumbnail(JsonElement e)
    {
        if (!e.TryGetProperty("thumbnails", out var t) || t.ValueKind != JsonValueKind.Array) return null;
        return t.EnumerateArray()
            .Select(x => (Url: x.TryGetProperty("url", out var u) ? u.GetString() : null, W: x.TryGetProperty("width", out var w) && w.ValueKind == JsonValueKind.Number ? w.GetInt32() : 0))
            .Where(x => x.Url is not null && x.W <= 1280)
            .OrderByDescending(x => x.W)
            .Select(x => x.Url)
            .FirstOrDefault();
    }

    public static string FriendlyError(string stderr)
    {
        var line = stderr.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith("ERROR:", StringComparison.Ordinal)) ?? stderr.Trim().Split('\n').LastOrDefault() ?? "";
        line = AnsiRegex().Replace(line, "").Replace("ERROR: ", "");
        var lower = line.ToLowerInvariant();
        if (lower.Contains("no video formats found") || lower.Contains("no media found")) return "No video was found at this link.";
        if (lower.Contains("unsupported url")) return "This site or page isn't supported for media downloads. If it is a direct file link, add it as a normal download.";
        if (lower.Contains("private video") || lower.Contains("this video is private")) return "This video is private. Only media you can access can be downloaded.";
        if (lower.Contains("sign in to confirm your age")) return "This video is age-restricted. Enable \"Pass my site cookies\" in the browser extension and send it from a signed-in browser.";
        if (lower.Contains("sign in to confirm") || lower.Contains("not a bot")) return "The site is asking for a sign-in check. Try again later or send the page from a signed-in browser with cookies enabled.";
        if (lower.Contains("login required") || lower.Contains("log in") || lower.Contains("logged-in") || lower.Contains("logged in") || lower.Contains("requires authentication") || lower.Contains("account credentials")) return "This media requires signing in. Send it from the browser extension with cookies enabled.";
        if (lower.Contains("drm")) return "This media is DRM-protected and cannot be downloaded.";
        if (lower.Contains("video unavailable") || lower.Contains("has been removed") || lower.Contains("does not exist") || lower.Contains("404")) return "This media is unavailable — it may have been deleted.";
        if (lower.Contains("429") || lower.Contains("too many requests")) return "The site is rate-limiting requests. Wait a few minutes and try again.";
        if (lower.Contains("geo") && lower.Contains("restrict")) return "This media is not available in your region.";
        if (lower.Contains("requested format is not available")) return "The selected quality is no longer offered for this media. Pick another quality.";
        return string.IsNullOrWhiteSpace(line) ? "The media could not be read." : line.Length > 300 ? line[..300] : line;
    }

    [GeneratedRegex(@"\x1B\[[0-9;]*m")]
    private static partial Regex AnsiRegex();
}
