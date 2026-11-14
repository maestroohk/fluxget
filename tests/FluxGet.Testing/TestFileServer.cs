using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FluxGet.Testing;

public sealed class ServedFile
{
    public long Size { get; set; }
    public int Seed { get; set; } = 1;
    public bool Ranges { get; set; } = true;
    public string? ETag { get; set; } = "\"v1\"";
    public DateTimeOffset? LastModified { get; set; } = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    public bool WrongRangeStart { get; set; }
    public int? ForceStatus { get; set; }
    public long DropAfterBytes { get; set; }
    public int DropCount;
    public long BytesPerSecond { get; set; }
    public bool NoLength { get; set; }
    public string? Disposition { get; set; }
    public int ConcurrencyLimit { get; set; }
    public int Throttled;

    public int Requests;
    public int RangedRequests;
    public int Concurrent;
    public int MaxConcurrent;
    public readonly ConcurrentQueue<string> RangeHeaders = new();
}

public sealed class TestFileServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly ConcurrentDictionary<string, ServedFile> _files = new();

    private TestFileServer(WebApplication app) => _app = app;

    public string BaseUrl { get; private set; } = "";

    public static async Task<TestFileServer> StartAsync(int port = 0)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(System.Net.IPAddress.Loopback, port));
        var app = builder.Build();
        var server = new TestFileServer(app);
        app.MapGet("/f/{name}", (HttpContext ctx, string name) => server.Serve(ctx, name));
        await app.StartAsync();
        server.BaseUrl = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First().TrimEnd('/');
        return server;
    }

    public ServedFile Add(string name, ServedFile file)
    {
        _files[name] = file;
        return file;
    }

    public string Url(string name) => $"{BaseUrl}/f/{name}";

    public static byte ByteAt(long i, int seed) => (byte)((ulong)(i * 2654435761L + seed * 40503L) >> 11);

    public static string Sha256(ServedFile f)
    {
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buf = new byte[1 << 20];
        for (long pos = 0; pos < f.Size; pos += buf.Length)
        {
            var n = (int)Math.Min(buf.Length, f.Size - pos);
            Fill(buf.AsSpan(0, n), pos, f.Seed);
            h.AppendData(buf, 0, n);
        }
        return Convert.ToHexStringLower(h.GetHashAndReset());
    }

    public static void Fill(Span<byte> span, long offset, int seed)
    {
        for (var i = 0; i < span.Length; i++) span[i] = ByteAt(offset + i, seed);
    }

    private async Task Serve(HttpContext ctx, string name)
    {
        if (!_files.TryGetValue(name, out var f))
        {
            ctx.Response.StatusCode = 404;
            return;
        }
        Interlocked.Increment(ref f.Requests);
        var c = Interlocked.Increment(ref f.Concurrent);
        int m;
        while ((m = f.MaxConcurrent) < c && Interlocked.CompareExchange(ref f.MaxConcurrent, c, m) != m) { }
        try
        {
            if (f.ConcurrencyLimit > 0 && c > f.ConcurrencyLimit)
            {
                Interlocked.Increment(ref f.Throttled);
                ctx.Response.Headers.RetryAfter = "1";
                ctx.Response.StatusCode = 429;
                return;
            }
            if (f.ForceStatus is { } st)
            {
                ctx.Response.StatusCode = st;
                return;
            }
            var res = ctx.Response;
            if (f.ETag is not null) res.Headers.ETag = f.ETag;
            if (f.LastModified is { } lm) res.Headers.LastModified = lm.ToString("R");
            if (f.Disposition is not null) res.Headers.ContentDisposition = f.Disposition;
            res.ContentType = "application/octet-stream";

            long from = 0, to = f.Size - 1;
            var range = ctx.Request.Headers.Range.ToString();
            var ifRange = ctx.Request.Headers.IfRange.ToString();
            var rangeValid = f.Ranges && range.StartsWith("bytes=") && (ifRange.Length == 0 || ifRange == f.ETag || ifRange == f.LastModified?.ToString("R"));
            if (rangeValid)
            {
                Interlocked.Increment(ref f.RangedRequests);
                f.RangeHeaders.Enqueue(range);
                var parts = range[6..].Split('-');
                from = long.Parse(parts[0]);
                if (parts[1].Length > 0) to = Math.Min(long.Parse(parts[1]), f.Size - 1);
                if (from >= f.Size)
                {
                    res.StatusCode = 416;
                    res.Headers.ContentRange = $"bytes */{f.Size}";
                    return;
                }
                if (f.WrongRangeStart && from > 0) from = Math.Max(0, from - 100);
                res.StatusCode = 206;
                res.Headers.ContentRange = $"bytes {from}-{to}/{f.Size}";
            }
            else
            {
                res.StatusCode = 200;
                if (f.Ranges) res.Headers.AcceptRanges = "bytes";
            }
            var length = to - from + 1;
            if (!f.NoLength) res.ContentLength = length;
            else ctx.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

            var drop = f.DropAfterBytes > 0 && Interlocked.Decrement(ref f.DropCount) >= 0;
            var buf = new byte[32 * 1024];
            long sent = 0;
            var started = Environment.TickCount64;
            while (sent < length)
            {
                var n = (int)Math.Min(buf.Length, length - sent);
                if (drop && sent + n > f.DropAfterBytes)
                {
                    await res.Body.FlushAsync();
                    ctx.Abort();
                    return;
                }
                Fill(buf.AsSpan(0, n), from + sent, f.Seed);
                await res.Body.WriteAsync(buf.AsMemory(0, n), ctx.RequestAborted);
                sent += n;
                if (f.BytesPerSecond > 0)
                {
                    var due = sent * 1000 / f.BytesPerSecond - (Environment.TickCount64 - started);
                    if (due > 0) await Task.Delay((int)due, ctx.RequestAborted);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        finally
        {
            Interlocked.Decrement(ref f.Concurrent);
        }
    }

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();
}
