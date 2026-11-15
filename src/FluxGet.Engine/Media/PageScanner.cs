using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace FluxGet.Engine.Media;

public sealed partial class PageScanner(SiteSessions sessions, EngineOptions options)
{
    public const int Limit = 500;
    private const int MaxHtmlBytes = 6 * 1024 * 1024;
    private const string BrowserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36";

    private static readonly HashSet<string> ImageExts = ["jpg", "jpeg", "png", "gif", "webp", "avif", "bmp", "svg", "heic", "jxl", "tif", "tiff", "ico"];
    private static readonly HashSet<string> VideoExts = ["mp4", "webm", "mov", "m4v", "mkv", "avi", "wmv", "flv", "mpg", "mpeg", "ts"];
    private static readonly HashSet<string> AudioExts = ["mp3", "m4a", "aac", "ogg", "opus", "flac", "wav", "wma"];
    private static readonly HashSet<string> FileExts = [
        "pdf", "epub", "mobi", "doc", "docx", "xls", "xlsx", "ppt", "pptx", "odt", "ods", "odp", "rtf", "txt", "csv", "json", "xml",
        "zip", "rar", "7z", "tar", "gz", "tgz", "bz2", "xz", "zst", "iso", "img", "exe", "msi", "msix", "dmg", "pkg", "deb", "rpm", "apk", "appimage", "torrent", "srt", "vtt", "ass"];

    private readonly HttpClient _http = new(new SocketsHttpHandler
    {
        ConnectCallback = Http.Connector.Create(options.BlockPrivateNetworks),
        AutomaticDecompression = DecompressionMethods.All,
        AllowAutoRedirect = true,
        MaxAutomaticRedirections = 8,
        UseCookies = false,
        ConnectTimeout = TimeSpan.FromSeconds(15)
    }) { Timeout = TimeSpan.FromSeconds(40) };
    private readonly ConcurrentDictionary<string, (DateTime At, GalleryInfo Info)> _cache = new();

    public async Task<GalleryInfo> ScanAsync(string url, CancellationToken ct)
    {
        var uri = UrlGuard.Parse(url);
        if (_cache.TryGetValue(uri.ToString(), out var hit) && DateTime.UtcNow - hit.At < TimeSpan.FromMinutes(10)) return hit.Info;
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        var session = sessions.For(uri);
        request.Headers.TryAddWithoutValidation("User-Agent", session is not null && session.TryGetValue("User-Agent", out var ua) ? ua : BrowserAgent);
        request.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml;q=0.9,*/*;q=0.8");
        if (session is not null && session.TryGetValue("Cookie", out var cookie)) request.Headers.TryAddWithoutValidation("Cookie", cookie);

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new MediaException("The page could not be reached. Check the address and your connection.");
        }
        using (response)
        {
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new MediaException("The site refused access to this page. If you need to be signed in, send the page from your browser with \"Pass my site cookies\" turned on in the FluxGet extension.");
            if (!response.IsSuccessStatusCode) throw new MediaException($"The page returned an error ({(int)response.StatusCode}).");
            var final = response.RequestMessage?.RequestUri ?? uri;
            var type = response.Content.Headers.ContentType?.MediaType ?? "";
            if (!type.Contains("html", StringComparison.OrdinalIgnoreCase))
            {
                var single = Item(1, final, null, null);
                var direct = new GalleryInfo(final.ToString(), single.Name, "Direct link", null, [single], false, true);
                _cache[uri.ToString()] = (DateTime.UtcNow, direct);
                return direct;
            }
            var html = await ReadLimitedAsync(response, ct);
            var info = Parse(html, final);
            if (info.Items.Count == 0) throw new MediaException("No images, videos or files were found on this page.");
            _cache[uri.ToString()] = (DateTime.UtcNow, info);
            return info;
        }
    }

    private static async Task<string> ReadLimitedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0 && buffer.Length < MaxHtmlBytes) buffer.Write(chunk, 0, read);
        var charset = response.Content.Headers.ContentType?.CharSet;
        Encoding encoding;
        try { encoding = charset is null ? Encoding.UTF8 : Encoding.GetEncoding(charset.Trim('"')); }
        catch (ArgumentException) { encoding = Encoding.UTF8; }
        return encoding.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    internal static GalleryInfo Parse(string html, Uri page)
    {
        var baseUri = BaseHref().Match(html) is { Success: true } b && Uri.TryCreate(page, Decode(b.Groups["v"].Value), out var bu) ? bu : page;
        var title = Meta(html, "og:title") ?? (TitleTag().Match(html) is { Success: true } t ? Clean(t.Groups[1].Value) : null) ?? page.Host;
        var items = new List<GalleryItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void Add(string? raw, int? width = null, int? height = null, bool fromLink = false)
        {
            if (items.Count >= Limit || string.IsNullOrWhiteSpace(raw)) return;
            raw = Decode(raw.Trim());
            if (raw.StartsWith("data:", StringComparison.OrdinalIgnoreCase) || raw.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) || raw.StartsWith('#')) return;
            if (!Uri.TryCreate(baseUri, raw, out var u) || (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps)) return;
            u = new UriBuilder(u) { Fragment = "" }.Uri;
            var ext = Extension(u);
            if (fromLink && !(ImageExts.Contains(ext) || VideoExts.Contains(ext) || AudioExts.Contains(ext) || FileExts.Contains(ext))) return;
            if (!seen.Add(u.ToString())) return;
            items.Add(Item(items.Count + 1, u, width, height));
        }

        Add(Meta(html, "og:image"));
        Add(Meta(html, "og:video") ?? Meta(html, "og:video:url"));
        foreach (Match m in Tag().Matches(html))
        {
            var name = m.Groups["name"].Value.ToLowerInvariant();
            var attrs = Attributes(m.Groups["attrs"].Value);
            switch (name)
            {
                case "img":
                    var src = Largest(attrs.GetValueOrDefault("srcset")) ?? attrs.GetValueOrDefault("data-src") ?? attrs.GetValueOrDefault("data-original") ?? attrs.GetValueOrDefault("data-lazy-src") ?? attrs.GetValueOrDefault("src");
                    Add(src, Int(attrs.GetValueOrDefault("width")), Int(attrs.GetValueOrDefault("height")));
                    break;
                case "source":
                    Add(Largest(attrs.GetValueOrDefault("srcset")) ?? attrs.GetValueOrDefault("src"));
                    break;
                case "video" or "audio":
                    Add(attrs.GetValueOrDefault("src"));
                    Add(attrs.GetValueOrDefault("poster"));
                    break;
                case "a":
                    Add(attrs.GetValueOrDefault("href"), fromLink: true);
                    break;
            }
        }
        return new GalleryInfo(page.ToString(), title, "Web page", page.Host, items, items.Count >= Limit, true);
    }

    private static GalleryItem Item(int index, Uri u, int? width, int? height)
    {
        var ext = Extension(u);
        var kind = ImageExts.Contains(ext) ? "image" : VideoExts.Contains(ext) ? "video" : AudioExts.Contains(ext) ? "audio" : "file";
        var name = Uri.UnescapeDataString(u.Segments.LastOrDefault()?.Trim('/') ?? "");
        if (name.Length == 0) name = u.Host;
        return new GalleryItem(index, u.ToString(), FileNames.Sanitize(name), ext, width, height, kind, kind == "image" ? u.ToString() : null);
    }

    private static string Extension(Uri u)
    {
        var ext = Path.GetExtension(u.AbsolutePath).TrimStart('.').ToLowerInvariant();
        if (ext.Length > 0) return ext;
        var query = u.Query.ToLowerInvariant();
        foreach (var known in new[] { "jpg", "jpeg", "png", "webp", "gif" })
            if (query.Contains("format=" + known) || query.Contains("fm=" + known)) return known;
        return "";
    }

    private static string? Largest(string? srcset)
    {
        if (string.IsNullOrWhiteSpace(srcset)) return null;
        return srcset.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(c => c.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(p => p.Length > 0)
            .Select(p => (Url: p[0], Size: p.Length > 1 && double.TryParse(p[1].TrimEnd('w', 'x', 'W', 'X'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 1))
            .OrderByDescending(p => p.Size)
            .Select(p => p.Url)
            .FirstOrDefault();
    }

    private static Dictionary<string, string> Attributes(string text)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match a in Attr().Matches(text))
            d.TryAdd(a.Groups["k"].Value, a.Groups["v1"].Success ? a.Groups["v1"].Value : a.Groups["v2"].Success ? a.Groups["v2"].Value : a.Groups["v3"].Value);
        return d;
    }

    private static string? Meta(string html, string property)
    {
        foreach (Match m in Tag().Matches(html))
        {
            if (!m.Groups["name"].Value.Equals("meta", StringComparison.OrdinalIgnoreCase)) continue;
            var attrs = Attributes(m.Groups["attrs"].Value);
            var key = attrs.GetValueOrDefault("property") ?? attrs.GetValueOrDefault("name");
            if (string.Equals(key, property, StringComparison.OrdinalIgnoreCase) && attrs.TryGetValue("content", out var c) && c.Length > 0) return Clean(c);
        }
        return null;
    }

    private static int? Int(string? s) => int.TryParse(s, out var i) && i > 0 ? i : null;
    private static string Decode(string s) => WebUtility.HtmlDecode(s);
    private static string Clean(string s)
    {
        s = string.Join(' ', Decode(s).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return s.Length > 120 ? s[..117].TrimEnd() + "…" : s;
    }

    [GeneratedRegex(@"<(?<name>img|source|video|audio|a|meta)\b(?<attrs>[^>]*)>", RegexOptions.IgnoreCase)]
    private static partial Regex Tag();

    [GeneratedRegex(@"(?<k>[a-zA-Z_:][-a-zA-Z0-9_:.]*)\s*=\s*(?:""(?<v1>[^""]*)""|'(?<v2>[^']*)'|(?<v3>[^\s""'>]+))")]
    private static partial Regex Attr();

    [GeneratedRegex(@"<title[^>]*>([^<]*)</title>", RegexOptions.IgnoreCase)]
    private static partial Regex TitleTag();

    [GeneratedRegex(@"<base\b[^>]*\bhref\s*=\s*[""']?(?<v>[^""'\s>]+)", RegexOptions.IgnoreCase)]
    private static partial Regex BaseHref();
}
