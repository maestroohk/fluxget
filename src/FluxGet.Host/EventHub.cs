using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using FluxGet.Engine;

namespace FluxGet.Host;

public sealed class EventHub
{
    private readonly ConcurrentDictionary<Guid, Channel<string>> _clients = new();

    public void Broadcast(string name, object payload)
    {
        if (_clients.IsEmpty) return;
        var message = $"event: {name}\ndata: {JsonSerializer.Serialize(payload, Json.Options)}\n\n";
        foreach (var c in _clients.Values) c.Writer.TryWrite(message);
    }

    public async Task StreamAsync(HttpContext ctx, Func<IEnumerable<string>> initial)
    {
        ctx.Response.Headers.ContentType = "text/event-stream";
        ctx.Response.Headers.CacheControl = "no-store";
        ctx.Response.Headers["X-Accel-Buffering"] = "no";
        var channel = Channel.CreateBounded<string>(new BoundedChannelOptions(500) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        var id = Guid.NewGuid();
        _clients[id] = channel;
        try
        {
            await ctx.Response.WriteAsync("retry: 2000\n\n", ctx.RequestAborted);
            foreach (var m in initial()) await ctx.Response.WriteAsync(m, ctx.RequestAborted);
            await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
            using var heartbeat = new PeriodicTimer(TimeSpan.FromSeconds(15));
            var reader = channel.Reader;
            var beat = heartbeat.WaitForNextTickAsync(ctx.RequestAborted).AsTask();
            Task<bool>? read = null;
            while (!ctx.RequestAborted.IsCancellationRequested)
            {
                read ??= reader.WaitToReadAsync(ctx.RequestAborted).AsTask();
                if (await Task.WhenAny(read, beat) == beat)
                {
                    await ctx.Response.WriteAsync(": ping\n\n", ctx.RequestAborted);
                    await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
                    beat = heartbeat.WaitForNextTickAsync(ctx.RequestAborted).AsTask();
                    continue;
                }
                if (!await read) break;
                read = null;
                while (reader.TryRead(out var msg)) await ctx.Response.WriteAsync(msg, ctx.RequestAborted);
                await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        finally
        {
            _clients.TryRemove(id, out _);
        }
    }

    public static string Format(string name, object payload) => $"event: {name}\ndata: {JsonSerializer.Serialize(payload, Json.Options)}\n\n";
}
