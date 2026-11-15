using System.Collections.Concurrent;
using System.Text.Json;

namespace FluxGet.Engine.Media;

public sealed record GalleryItem(int Index, string Url, string Name, string Ext, int? Width, int? Height, string Kind, string? Preview);

public sealed record GalleryInfo(string Url, string Title, string Source, string? Uploader, IReadOnlyList<GalleryItem> Items, bool HasMore, bool Generic)
{
    public string Type => "gallery";
}

public sealed class GalleryDl(ToolLocator tools, EngineOptions options, SiteSessions sessions)
{
    public const int Limit = 300;
    private static readonly HashSet<string> VideoExts = ["mp4", "webm", "mov", "m4v", "mkv", "avi", "m3u8"];
    private readonly SemaphoreSlim _slots = new(2);
    private readonly ConcurrentDictionary<string, (DateTime At, GalleryInfo Info)> _cache = new();

    public bool Available => tools.Find("gallery-dl") is not null;

    public string Exe => tools.Find("gallery-dl") ?? throw new MediaException("gallery-dl is not installed. Image downloads need it; the FluxGet installer includes it.");

    public SiteSessions Sessions => sessions;

    public List<string> CommonArgs(Uri url, IReadOnlyDictionary<string, string>? headers, out string? cookieFile)
    {
        var args = new List<string> { "--config-ignore", "--no-colors", "--http-timeout", "30" };
        headers ??= sessions.For(url);
        cookieFile = CookieFile.Write(options.DataDirectory, url, headers);
        if (cookieFile is not null) args.AddRange(["--cookies", cookieFile]);
        if (headers is not null && headers.TryGetValue("User-Agent", out var ua)) args.AddRange(["--user-agent", ua]);
        return args;
    }

    public static string Ranges(int[] sorted)
    {
        var parts = new List<string>();
        for (var i = 0; i < sorted.Length; i++)
        {
            var start = sorted[i];
            while (i + 1 < sorted.Length && sorted[i + 1] == sorted[i] + 1) i++;
            parts.Add(start == sorted[i] ? start.ToString(System.Globalization.CultureInfo.InvariantCulture) : $"{start}-{sorted[i]}");
        }
        return string.Join(',', parts);
    }

    public async Task<GalleryInfo?> AnalyzeAsync(string url, CancellationToken ct)
    {
        var uri = UrlGuard.Parse(url);
        var key = uri.ToString();
        if (_cache.TryGetValue(key, out var hit) && DateTime.UtcNow - hit.At < TimeSpan.FromMinutes(10)) return hit.Info;
        await _slots.WaitAsync(ct);
        try
        {
            var info = await ProbeAsync(uri, ct);
            if (info is null) return null;
            if (info.Items.Count == 0) throw new MediaException("This link has no images or files FluxGet can download.");
            _cache[key] = (DateTime.UtcNow, info);
            return info;
        }
        finally
        {
            _slots.Release();
        }
    }

    private async Task<GalleryInfo?> ProbeAsync(Uri uri, CancellationToken ct)
    {
        var args = CommonArgs(uri, null, out var cookieFile);
        args.AddRange(["-j", "--range", $"1-{Limit + 1}", "--", uri.ToString()]);
        try
        {
            var result = await ProcessRunner.RunAsync(Exe, args, null, ct, timeout: TimeSpan.FromMinutes(2));
            if (result.StdErr.Contains("Unsupported URL", StringComparison.Ordinal)) return null;
            if (string.IsNullOrWhiteSpace(result.StdOut)) throw new MediaException(FriendlyError(result.StdErr));
            return Parse(result.StdOut, uri.ToString());
        }
        finally
        {
            CookieFile.Delete(cookieFile);
        }
    }

    internal static GalleryInfo Parse(string json, string url)
    {
        using var doc = JsonDocument.Parse(json);
        static string? S(JsonElement e, params string[] names)
        {
            foreach (var n in names)
                if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString())) return v.GetString();
            return null;
        }
        static int? I(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) && i > 0 ? i : null;

        var items = new List<GalleryItem>();
        string? source = null, error = null;
        string[] titleKeys = ["title", "og_title", "description", "content", "caption", "gallery_title", "page", "post_shortcode", "id"];
        string[] uploaderKeys = ["username", "author", "uploader", "fullname", "user", "owner"];
        var directories = new List<JsonElement>();
        var files = new List<JsonElement>();
        var total = 0;
        foreach (var msg in doc.RootElement.EnumerateArray())
        {
            if (msg.ValueKind != JsonValueKind.Array || msg.GetArrayLength() < 2) continue;
            var type = msg[0].GetInt32();
            if (type == -1) error = S(msg[1], "message") ?? "The site returned an error.";
            if (type is not (2 or 3)) continue;
            var meta = type == 2 ? msg[1] : msg.GetArrayLength() > 2 ? msg[2] : default;
            if (meta.ValueKind != JsonValueKind.Object) continue;
            source ??= S(meta, "category");
            (type == 2 ? directories : files).Add(meta.Clone());
            if (type != 3) continue;
            total++;
            if (items.Count >= Limit) continue;
            var mediaUrl = msg[1].GetString() ?? "";
            var ext = (S(meta, "extension") ?? ExtensionOf(mediaUrl)).ToLowerInvariant();
            var name = S(meta, "filename") is { } f ? $"{f}.{ext}".TrimEnd('.') : $"{items.Count + 1}.{ext}";
            var video = VideoExts.Contains(ext) || mediaUrl.StartsWith("ytdl:", StringComparison.Ordinal);
            var preview = video ? S(meta, "display_url", "thumbnail", "thumbnail_url", "preview", "poster") : mediaUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? mediaUrl : null;
            items.Add(new GalleryItem(total, mediaUrl, name, ext, I(meta, "width"), I(meta, "height"), video ? "video" : "image", preview));
        }
        if (items.Count == 0 && error is not null) throw new MediaException(FriendlyError(error));
        var scope = directories.Count > 0 ? directories : files;
        string? Shared(string[] keys) => keys
            .Select(k => scope.Select(m => S(m, k)).ToList())
            .Where(v => v.Count > 0 && v[0] is not null && v.All(x => x == v[0]))
            .Select(v => v[0])
            .FirstOrDefault();
        var title = Clip(Shared(titleKeys)?.Replace('_', ' '));
        return new GalleryInfo(url, title ?? new Uri(url).Host, Label(source), Shared(uploaderKeys), items, total > Limit, false);
    }

    private static string ExtensionOf(string url) =>
        Uri.TryCreate(url.StartsWith("ytdl:", StringComparison.Ordinal) ? url[5..] : url, UriKind.Absolute, out var u) ? Path.GetExtension(u.AbsolutePath).TrimStart('.') : "";

    private static string? Clip(string? s)
    {
        if (s is null) return null;
        s = string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return s.Length > 120 ? s[..117].TrimEnd() + "…" : s;
    }

    private static string Label(string? category) => category is null or "generic" or "directlink" ? "Web page" : category switch
    {
        "instagram" => "Instagram",
        "twitter" => "X",
        "reddit" => "Reddit",
        "pinterest" => "Pinterest",
        "tumblr" => "Tumblr",
        "flickr" => "Flickr",
        "deviantart" => "DeviantArt",
        "imgur" => "Imgur",
        "wikimediacommons" or "wikipedia" => "Wikimedia",
        "facebook" => "Facebook",
        "tiktok" => "TikTok",
        "bluesky" => "Bluesky",
        "artstation" => "ArtStation",
        _ => char.ToUpperInvariant(category[0]) + category[1..]
    };

    public static string FriendlyError(string stderr)
    {
        var line = stderr.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Contains("[error]", StringComparison.Ordinal)) ?? stderr.Trim().Split('\n').LastOrDefault() ?? "";
        var idx = line.IndexOf("[error] ", StringComparison.Ordinal);
        if (idx >= 0) line = line[(idx + 8)..];
        var lower = line.ToLowerInvariant();
        if (lower.Contains("login") || lower.Contains("log in") || lower.Contains("authorization") || lower.Contains("401") || lower.Contains("403"))
            return "This post needs you to be signed in. In your browser, turn on \"Pass my site cookies\" in the FluxGet extension, then right-click the page and choose \"Download from this page with FluxGet\".";
        if (lower.Contains("blocked") || lower.Contains("429") || lower.Contains("too many requests"))
            return "The site blocked or rate-limited the request. Try again later, or send the page from your signed-in browser with \"Pass my site cookies\" turned on.";
        if (lower.Contains("404") || lower.Contains("not found")) return "Nothing was found at this link. The post may have been deleted.";
        return line.Length > 0 ? line : "The images at this link could not be read.";
    }
}
