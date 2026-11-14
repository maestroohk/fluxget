using System.Net;
using System.Net.Sockets;

namespace FluxGet.Engine.Http;

public static class Connector
{
    private static readonly TimeSpan Stagger = TimeSpan.FromMilliseconds(250);

    public static Func<SocketsHttpConnectionContext, CancellationToken, ValueTask<Stream>> Create(bool blockPrivate) =>
        (context, ct) => ConnectAsync(context.DnsEndPoint.Host, context.DnsEndPoint.Port, blockPrivate, ct);

    public static async ValueTask<Stream> ConnectAsync(string host, int port, bool blockPrivate, CancellationToken ct)
    {
        var addresses = IPAddress.TryParse(host, out var literal) ? [literal] : await Dns.GetHostAddressesAsync(host, ct);
        if (blockPrivate && (addresses.Length == 0 || addresses.Any(UrlGuard.IsPrivate)))
            throw new BlockedAddressException($"Blocked: {host} resolves to a private or reserved address.");
        if (addresses.Length == 0) throw new SocketException((int)SocketError.HostNotFound);

        var ordered = Interleave(addresses);
        using var race = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var attempts = new List<Task<Socket>>();
        Exception? last = null;
        var next = 0;
        while (true)
        {
            if (next < ordered.Count) attempts.Add(TryConnect(ordered[next++], port, race.Token));
            if (attempts.Count == 0) throw last ?? new SocketException((int)SocketError.HostUnreachable);
            var waitFor = Task.Delay(next < ordered.Count ? Stagger : Timeout.InfiniteTimeSpan, race.Token);
            var done = await Task.WhenAny(attempts.Append(waitFor));
            ct.ThrowIfCancellationRequested();
            if (done == waitFor) continue;
            var attempt = (Task<Socket>)done;
            attempts.Remove(attempt);
            if (attempt.IsCompletedSuccessfully)
            {
                race.Cancel();
                foreach (var other in attempts) _ = other.ContinueWith(t => { if (t.IsCompletedSuccessfully) t.Result.Dispose(); }, TaskScheduler.Default);
                return new NetworkStream(attempt.Result, ownsSocket: true);
            }
            last = attempt.Exception?.InnerException ?? last;
            if (next >= ordered.Count && attempts.Count == 0) throw last ?? new SocketException((int)SocketError.ConnectionRefused);
        }
    }

    private static async Task<Socket> TryConnect(IPAddress address, int port, CancellationToken ct)
    {
        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(address, port, ct);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static List<IPAddress> Interleave(IPAddress[] addresses)
    {
        var v6 = new Queue<IPAddress>(addresses.Where(a => a.AddressFamily == AddressFamily.InterNetworkV6));
        var v4 = new Queue<IPAddress>(addresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork));
        var list = new List<IPAddress>(addresses.Length);
        var preferV6 = addresses[0].AddressFamily == AddressFamily.InterNetworkV6;
        while (v6.Count > 0 || v4.Count > 0)
        {
            var (a, b) = preferV6 ? (v6, v4) : (v4, v6);
            if (a.Count > 0) list.Add(a.Dequeue());
            if (b.Count > 0) list.Add(b.Dequeue());
        }
        return list;
    }
}
