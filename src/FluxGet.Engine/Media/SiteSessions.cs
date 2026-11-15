using System.Collections.Concurrent;
using System.Text;

namespace FluxGet.Engine.Media;

public sealed class SiteSessions
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);
    private readonly ConcurrentDictionary<string, (DateTime At, IReadOnlyDictionary<string, string> Headers)> _items = new();

    public void Remember(Uri url, IReadOnlyDictionary<string, string> headers) => _items[Site(url)] = (DateTime.UtcNow, headers);

    public IReadOnlyDictionary<string, string>? For(Uri url)
    {
        var site = Site(url);
        if (!_items.TryGetValue(site, out var entry)) return null;
        if (DateTime.UtcNow - entry.At < Lifetime) return entry.Headers;
        _items.TryRemove(site, out _);
        return null;
    }

    public static string Site(Uri url) => string.Join('.', url.Host.Split('.').TakeLast(2)).ToLowerInvariant();
}

public static class CookieFile
{
    public static string? Write(string dataDir, Uri url, IReadOnlyDictionary<string, string>? headers)
    {
        if (headers is null || !headers.TryGetValue("Cookie", out var cookieHeader) || string.IsNullOrWhiteSpace(cookieHeader)) return null;
        var dir = Path.Combine(dataDir, "tmp");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"c-{Guid.NewGuid():N}.txt");
        var domain = "." + SiteSessions.Site(url);
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

    public static void Delete(string? path)
    {
        if (path is null) return;
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
