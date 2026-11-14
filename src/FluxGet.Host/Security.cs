using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using FluxGet.Engine;

namespace FluxGet.Host;

public sealed class AccessKeys
{
    public const string CookieName = "fg_session";
    private readonly JobStore _store;
    private readonly ConcurrentDictionary<string, bool> _known = new();

    public AccessKeys(FluxGetHostOptions options, JobStore store)
    {
        _store = store;
        SessionKey = options.AccessToken is { Length: >= 24 } fixedKey ? fixedKey : LoadOrCreate(Path.Combine(options.ResolveDataDirectory(), "session.key"));
    }

    public string SessionKey { get; }

    public bool IsValid(string? token)
    {
        if (string.IsNullOrEmpty(token)) return false;
        if (FixedEquals(token, SessionKey)) return true;
        var hash = JobStore.HashToken(token);
        if (_known.TryGetValue(hash, out var ok)) return ok;
        ok = _store.HasToken(token);
        if (ok) _known[hash] = true;
        return ok;
    }

    public string IssueExtensionToken(string name)
    {
        var token = NewSecret();
        _store.AddToken(token, name);
        return token;
    }

    public void Revoke(string hash)
    {
        _store.RevokeToken(hash);
        _known.TryRemove(hash, out _);
    }

    public static string NewSecret() => Base64Url(RandomNumberGenerator.GetBytes(32));

    public static bool FixedEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    private static string Base64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string LoadOrCreate(string path)
    {
        if (File.Exists(path))
        {
            var existing = File.ReadAllText(path).Trim();
            if (existing.Length >= 24) return existing;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var key = NewSecret();
        File.WriteAllText(path, key);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return key;
    }
}

public sealed class PairingService(EventHub hub)
{
    private sealed record Pending(string Id, string Code, string Name, DateTime Expires)
    {
        public int Attempts { get; set; }
    }

    private readonly ConcurrentDictionary<string, Pending> _pending = new();

    public (string Id, DateTime Expires) Request(string name)
    {
        foreach (var p in _pending.Values.Where(p => p.Expires < DateTime.UtcNow)) _pending.TryRemove(p.Id, out _);
        if (_pending.Count >= 5) throw new InvalidOperationException("Too many pairing requests. Try again in a minute.");
        var id = AccessKeys.NewSecret()[..16];
        var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
        var pending = new Pending(id, code, string.IsNullOrWhiteSpace(name) ? "Browser extension" : name.Trim()[..Math.Min(name.Trim().Length, 60)], DateTime.UtcNow.AddMinutes(2));
        _pending[id] = pending;
        hub.Broadcast("pair", new { id, code, name = pending.Name, expires = pending.Expires });
        return (id, pending.Expires);
    }

    public string? Confirm(string id, string code)
    {
        if (!_pending.TryGetValue(id, out var p) || p.Expires < DateTime.UtcNow) return null;
        if (++p.Attempts > 5)
        {
            _pending.TryRemove(id, out _);
            return null;
        }
        if (!AccessKeys.FixedEquals(p.Code, code?.Trim() ?? "")) return null;
        _pending.TryRemove(id, out _);
        hub.Broadcast("pair-done", new { id });
        return p.Name;
    }

    internal string? PeekCode(string id) => _pending.TryGetValue(id, out var p) ? p.Code : null;

    public void Dismiss(string id)
    {
        _pending.TryRemove(id, out _);
        hub.Broadcast("pair-done", new { id });
    }
}

public sealed class RequestGuard(RequestDelegate next, FluxGetHostOptions options, AccessKeys keys)
{
    private static readonly string[] Anonymous = ["/api/session", "/api/pair/request", "/api/pair/confirm", "/api/health"];

    public async Task InvokeAsync(HttpContext ctx)
    {
        if (!HostAllowed(ctx.Request.Host))
        {
            ctx.Response.StatusCode = StatusCodes.Status421MisdirectedRequest;
            return;
        }
        var origin = ctx.Request.Headers.Origin.ToString();
        if (origin.Length > 0 && !OriginAllowed(ctx, origin))
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
        var path = ctx.Request.Path;
        if (path.StartsWithSegments("/api"))
        {
            ctx.Response.Headers.CacheControl = "no-store";
            var anonymous = Anonymous.Any(a => path.Equals(a, StringComparison.OrdinalIgnoreCase));
            if (!anonymous && !keys.IsValid(TokenFrom(ctx)))
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
        }
        ctx.Response.Headers.XContentTypeOptions = "nosniff";
        ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
        ctx.Response.Headers.XFrameOptions = "DENY";
        await next(ctx);
    }

    public static string? TokenFrom(HttpContext ctx)
    {
        var auth = ctx.Request.Headers.Authorization.ToString();
        if (auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return auth[7..].Trim();
        return ctx.Request.Cookies[AccessKeys.CookieName];
    }

    public static bool IsExtensionOrigin(string origin) =>
        origin.StartsWith("chrome-extension://", StringComparison.Ordinal) || origin.StartsWith("moz-extension://", StringComparison.Ordinal);

    private bool HostAllowed(HostString host)
    {
        var h = host.Host;
        if (options.IsLoopbackOnly) return h is "127.0.0.1" or "localhost" or "[::1]" or "::1";
        return options.AllowedHosts.Length == 0 || options.AllowedHosts.Contains(h, StringComparer.OrdinalIgnoreCase);
    }

    private bool OriginAllowed(HttpContext ctx, string origin)
    {
        if (IsExtensionOrigin(origin)) return true;
        if (origin.Equals($"{ctx.Request.Scheme}://{ctx.Request.Host}", StringComparison.OrdinalIgnoreCase)) return true;
        return options.DevOrigins.Contains(origin, StringComparer.OrdinalIgnoreCase) && options.DevDashboard;
    }
}
