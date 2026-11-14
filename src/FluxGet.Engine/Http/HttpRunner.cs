using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FluxGet.Engine.Http;

public sealed class HttpRunner : IJobRunner, IDisposable
{
    private const int BufferSize = 128 * 1024;
    private static readonly HashSet<string> ForwardedHeaders = new(StringComparer.OrdinalIgnoreCase) { "Cookie", "Referer", "User-Agent", "Accept-Language" };
    private readonly HttpClient _client;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _hostLimits = new(StringComparer.OrdinalIgnoreCase);

    public HttpRunner(EngineOptions options)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 10,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(20),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
            MaxConnectionsPerServer = 64,
            UseCookies = false
        };
        handler.ConnectCallback = Connector.Create(options.BlockPrivateNetworks);
        _client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan, DefaultRequestVersion = HttpVersion.Version11, DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("FluxGet/0.1");
    }

    public JobKind Kind => JobKind.Http;

    public static string PartPath(Job job) => Path.Combine(job.Directory, $"{job.FileName}.{job.Id}{FileNames.PartSuffix}");

    public async Task RunAsync(JobContext ctx, CancellationToken ct)
    {
        var job = ctx.Job;
        var probe = await WithRetry(ctx, () => ProbeAsync(job, ct), ct);
        try
        {
            ctx.SetStatus(JobStatus.Running);
            Prepare(ctx, probe);
            await DownloadAsync(ctx, probe, ct);
        }
        finally
        {
            probe.Response?.Dispose();
        }
        await FinalizeAsync(ctx, ct);
    }

    private void Prepare(JobContext ctx, Probe probe)
    {
        var job = ctx.Job;
        job.FileName ??= probe.FileName;
        var part = PartPath(job);
        if (job.Segments.Count > 0 && !File.Exists(part)) ResetProgress(job);

        if (job.Segments.Count > 0)
        {
            if (job.TotalBytes != probe.Total)
                throw new JobAttentionException($"The file on the server changed (size {Fmt(job.TotalBytes)} → {Fmt(probe.Total)}). Restart to download the new version.");
            if (ValidatorsDiffer(job.ETag, job.LastModified, probe.ETag, probe.LastModified))
                throw new JobAttentionException("The file on the server changed since this download started. Restart to download the new version.");
            if (!probe.AcceptsRanges)
            {
                ResetProgress(job);
                job.Resumable = false;
                job.Phase = "Server cannot resume — restarted from the beginning";
            }
        }

        if (job.Segments.Count == 0)
        {
            job.TotalBytes = probe.Total;
            job.ETag = probe.ETag;
            job.LastModified = probe.LastModified;
            job.Resumable = probe.AcceptsRanges && probe.Total is not null;
            job.Segments = Plan(probe.Total, job.Resumable ? Connections(ctx) : 1, ctx.Options.MinSplitSize);
        }
        ctx.Save();

        var remaining = (job.TotalBytes ?? 0) - Interlocked.Read(ref job.ReceivedBytes);
        if (remaining > 0 && FreeSpace(job.Directory) is { } free && free < remaining + 32L * 1024 * 1024)
            throw new JobAttentionException($"Not enough disk space: {Fmt(remaining)} needed, {Fmt(free)} free.");
    }

    private static void ResetProgress(Job job)
    {
        job.Segments.Clear();
        Interlocked.Exchange(ref job.ReceivedBytes, 0);
    }

    private static int Connections(JobContext ctx) => ctx.Job.Connections > 0 ? ctx.Job.Connections : ctx.Settings.ConnectionsPerDownload;

    internal static List<Segment> Plan(long? total, int connections, long minSplit)
    {
        if (total is not { } t) return [new Segment { Index = 0, Start = 0, End = -1 }];
        if (t == 0) return [];
        var n = (int)Math.Clamp(t / Math.Max(1, minSplit), 1, Math.Max(1, connections));
        var size = t / n;
        var list = new List<Segment>(n);
        for (var i = 0; i < n; i++)
        {
            var start = i * size;
            var end = i == n - 1 ? t - 1 : start + size - 1;
            list.Add(new Segment { Index = i, Start = start, End = end, Next = start });
        }
        return list;
    }

    private async Task DownloadAsync(JobContext ctx, Probe probe, CancellationToken ct)
    {
        var job = ctx.Job;
        var part = PartPath(job);
        var isNew = !File.Exists(part);
        await using var fs = OpenPart(part, job.TotalBytes, isNew);
        var run = new RunState(ctx, fs, probe);
        lock (job.Gate)
        {
            foreach (var seg in job.Segments)
            {
                seg.Next = seg.Start + Interlocked.Read(ref seg.Received);
                seg.Active = false;
            }
            Interlocked.Exchange(ref job.ReceivedBytes, job.Segments.Sum(x => Interlocked.Read(ref x.Received)));
        }

        using var checkpointCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var checkpoints = CheckpointLoop(run, checkpointCts.Token);
        try
        {
            try
            {
                await RunWorkers(run, ct);
            }
            catch (RangeUnsupportedException) when (!ct.IsCancellationRequested)
            {
                lock (job.Gate)
                {
                    ResetProgress(job);
                    job.Resumable = false;
                    job.Segments = Plan(job.TotalBytes, 1, ctx.Options.MinSplitSize);
                }
                ctx.SetStatus(JobStatus.Running, "Server ignored range requests — using one connection");
                await RunWorkers(run, ct);
            }
        }
        finally
        {
            checkpointCts.Cancel();
            try { await checkpoints; } catch (OperationCanceledException) { }
            job.ActiveConnections = 0;
            Checkpoint(run);
        }

        if (job.TotalBytes is { } total)
        {
            var got = Interlocked.Read(ref job.ReceivedBytes);
            if (got != total || job.Segments.Any(s => !s.IsComplete))
                throw new JobFailedException($"Download ended early ({Fmt(got)} of {Fmt(total)}).");
        }
    }

    private static FileStream OpenPart(string path, long? total, bool isNew)
    {
        try
        {
            var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, 0, FileOptions.Asynchronous | FileOptions.RandomAccess);
            if (isNew && OperatingSystem.IsWindows()) MakeSparse(fs.SafeFileHandle);
            if (total is { } t && fs.Length != t) fs.SetLength(t);
            return fs;
        }
        catch (IOException e) when (IsDiskFull(e))
        {
            throw new JobAttentionException("Not enough disk space to allocate the file.");
        }
    }

    private sealed class RunState(JobContext ctx, FileStream file, Probe probe)
    {
        public JobContext Ctx { get; } = ctx;
        public Job Job => Ctx.Job;
        public FileStream File { get; } = file;
        public Uri Uri { get; } = probe.FinalUri;
        public HttpResponseMessage? InitialResponse = probe.Response;
        public int Limit;
        public int Busy;
    }

    private async Task RunWorkers(RunState run, CancellationToken ct)
    {
        var job = run.Job;
        var workers = job.Resumable ? Connections(run.Ctx) : 1;
        if (_hostLimits.TryGetValue(run.Uri.Host, out var learned)) workers = Math.Min(workers, learned);
        run.Limit = workers;
        run.Busy = 0;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Exception? failure = null;
        var tasks = Enumerable.Range(0, workers).Select(_ => Task.Run(async () =>
        {
            try
            {
                while (!linked.IsCancellationRequested && TakeSegment(run) is { } seg)
                {
                    var released = false;
                    try { released = await DownloadSegment(run, seg, linked.Token); }
                    finally
                    {
                        lock (job.Gate)
                        {
                            if (!released)
                            {
                                seg.Active = false;
                                run.Busy--;
                            }
                            job.ActiveConnections = job.Segments.Count(s => s.Active);
                        }
                    }
                }
            }
            catch (Exception e)
            {
                if (e is OperationCanceledException && linked.IsCancellationRequested) return;
                Interlocked.CompareExchange(ref failure, e, null);
                linked.Cancel();
            }
        }, CancellationToken.None)).ToArray();
        await Task.WhenAll(tasks);
        ct.ThrowIfCancellationRequested();
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(failure);
    }

    private Segment? TakeSegment(RunState run)
    {
        var job = run.Job;
        var min = run.Ctx.Options.MinSplitSize;
        lock (job.Gate)
        {
            if (run.Busy >= run.Limit) return null;
            var free = job.Segments.FirstOrDefault(s => !s.Active && !s.IsComplete);
            if (free is null && job.Resumable && job.TotalBytes is not null)
            {
                var victim = job.Segments.Where(s => s.Active && s.Remaining >= 2 * min).MaxBy(s => s.Remaining);
                if (victim is not null)
                {
                    var mid = victim.Next + victim.Remaining / 2;
                    free = new Segment { Index = job.Segments.Max(s => s.Index) + 1, Start = mid, End = victim.End, Next = mid };
                    victim.End = mid - 1;
                    job.Segments.Add(free);
                }
            }
            if (free is null) return null;
            free.Active = true;
            run.Busy++;
            job.ActiveConnections = job.Segments.Count(s => s.Active);
            return free;
        }
    }

    private async Task<bool> DownloadSegment(RunState run, Segment seg, CancellationToken ct)
    {
        var job = run.Job;
        var ctx = run.Ctx;
        var attempt = 0;
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            while (true)
            {
                long from;
                var gotBytes = false;
                lock (job.Gate)
                {
                    if (seg.End >= 0 && seg.Next > seg.End) return false;
                    from = seg.Next;
                }
                try
                {
                    using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    stall.CancelAfter(ctx.Options.StallTimeout);
                    var initial = from == 0 ? Interlocked.Exchange(ref run.InitialResponse, null) : null;
                    using var response = initial ?? await SendRangeAsync(run, from, seg.End, stall.Token);
                    ValidateSegmentResponse(job, response, from, ranged: initial is null && job.Resumable);
                    await using var stream = await response.Content.ReadAsStreamAsync(stall.Token);
                    while (true)
                    {
                        var chunk = Math.Min(ctx.GlobalLimiter.SuggestedChunk(BufferSize), ctx.JobLimiter.SuggestedChunk(BufferSize));
                        var n = await stream.ReadAsync(buffer.AsMemory(0, chunk), stall.Token);
                        stall.CancelAfter(ctx.Options.StallTimeout);
                        if (n == 0) break;
                        await ctx.GlobalLimiter.WaitAsync(n, ct);
                        await ctx.JobLimiter.WaitAsync(n, ct);
                        long pos;
                        int take;
                        lock (job.Gate)
                        {
                            pos = seg.Next;
                            take = seg.End < 0 ? n : (int)Math.Min(n, seg.End - pos + 1);
                            if (take > 0) seg.Next += take;
                        }
                        if (take > 0)
                        {
                            await Write(run.File.SafeFileHandle, buffer.AsMemory(0, take), pos, ct);
                            Interlocked.Add(ref seg.Received, take);
                            Interlocked.Add(ref job.ReceivedBytes, take);
                            gotBytes = true;
                            if (attempt > 0)
                            {
                                attempt = 0;
                                ctx.SetStatus(JobStatus.Running);
                            }
                        }
                        if (take < n || (seg.End >= 0 && seg.Next > seg.End)) break;
                    }
                    if (seg.End < 0)
                    {
                        lock (job.Gate)
                        {
                            seg.End = seg.Next - 1;
                            job.TotalBytes = seg.Next;
                        }
                        return false;
                    }
                    if (seg.Next <= seg.End) throw new IOException("The connection closed before the segment finished.");
                    return false;
                }
                catch (Exception e) when (IsTransient(e, ct))
                {
                    if (!NetworkInterface.GetIsNetworkAvailable())
                    {
                        await WaitForNetwork(ctx, ct);
                        continue;
                    }
                    if (!gotBytes && IsThrottle(e))
                    {
                        int? reduced = null;
                        lock (job.Gate)
                        {
                            if (run.Busy > 1)
                            {
                                run.Busy--;
                                seg.Active = false;
                                run.Limit = Math.Max(1, run.Busy);
                                reduced = run.Limit;
                            }
                        }
                        if (reduced is { } n)
                        {
                            _hostLimits[run.Uri.Host] = n;
                            ctx.SetStatus(JobStatus.Running, $"Server limits connections — using {n}");
                            return true;
                        }
                    }
                    attempt++;
                    if (attempt > ctx.Options.MaxAttempts)
                        throw new JobFailedException($"Gave up after {ctx.Options.MaxAttempts} attempts: {JobManager.Describe(Unwrap(e))}");
                    if (!job.Resumable)
                    {
                        lock (job.Gate)
                        {
                            seg.Next = seg.Start;
                            Interlocked.Exchange(ref seg.Received, 0);
                            Interlocked.Exchange(ref job.ReceivedBytes, 0);
                        }
                    }
                    var delay = BackoffDelay(ctx.Options, attempt, e);
                    ctx.SetStatus(JobStatus.Retrying, $"Retrying in {Math.Ceiling(delay.TotalSeconds)} s (attempt {attempt}/{ctx.Options.MaxAttempts}): {JobManager.Describe(Unwrap(e))}");
                    await Task.Delay(delay, ct);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task<HttpResponseMessage> SendRangeAsync(RunState run, long from, long to, CancellationToken ct)
    {
        var job = run.Job;
        var req = CreateRequest(job, run.Uri);
        if (job.Resumable)
        {
            req.Headers.Range = new RangeHeaderValue(from, to >= 0 ? to : null);
            if (job.ETag is { } etag && !etag.StartsWith("W/", StringComparison.Ordinal))
                req.Headers.IfRange = new RangeConditionHeaderValue(EntityTagHeaderValue.Parse(etag));
            else if (job.LastModified is { } lm && DateTimeOffset.TryParse(lm, out var date))
                req.Headers.IfRange = new RangeConditionHeaderValue(date);
        }
        var response = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if ((int)response.StatusCode is 200 or 206) return response;
        response.Dispose();
        throw StatusError(response, job);
    }

    private static void ValidateSegmentResponse(Job job, HttpResponseMessage r, long from, bool ranged)
    {
        var etag = r.Headers.ETag?.ToString();
        var lm = r.Content.Headers.LastModified?.ToString("R");
        if (ValidatorsDiffer(job.ETag, job.LastModified, etag, lm))
            throw new JobAttentionException("The file on the server changed during the download. Restart to download the new version.");
        if (!ranged)
        {
            if (job.TotalBytes is { } t && r.Content.Headers.ContentLength is { } len && len != t)
                throw new JobAttentionException("The file on the server changed size during the download. Restart to download it again.");
            return;
        }
        if (r.StatusCode != HttpStatusCode.PartialContent) throw new RangeUnsupportedException();
        var cr = r.Content.Headers.ContentRange;
        if (cr?.From is null || cr.From != from) throw new RangeUnsupportedException();
        if (cr.Length is { } total && job.TotalBytes is { } expected && total != expected)
            throw new JobAttentionException("The file on the server changed size during the download. Restart to download it again.");
    }

    private static bool ValidatorsDiffer(string? etagA, string? lmA, string? etagB, string? lmB)
    {
        if (etagA is not null && etagB is not null) return Strip(etagA) != Strip(etagB);
        if (lmA is not null && lmB is not null) return lmA != lmB;
        return false;
        static string Strip(string e) => e.StartsWith("W/", StringComparison.Ordinal) ? e[2..] : e;
    }

    private async Task<Probe> ProbeAsync(Job job, CancellationToken ct)
    {
        var uri = new Uri(job.Url);
        var req = CreateRequest(job, uri);
        req.Headers.Range = new RangeHeaderValue(0, 0);
        var r = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        var finalUri = r.RequestMessage?.RequestUri ?? uri;
        var name = FileNames.FromResponse(r.Content.Headers.ContentDisposition, finalUri, r.Content.Headers.ContentType?.MediaType);
        var etag = r.Headers.ETag?.ToString();
        var lm = r.Content.Headers.LastModified?.ToString("R");
        switch (r.StatusCode)
        {
            case HttpStatusCode.PartialContent when r.Content.Headers.ContentRange is { From: 0, Length: { } len }:
                r.Dispose();
                return new Probe(finalUri, name, len, true, etag, lm, null);
            case HttpStatusCode.OK:
                return new Probe(finalUri, name, r.Content.Headers.ContentLength, false, etag, lm, r);
            case HttpStatusCode.RequestedRangeNotSatisfiable when r.Content.Headers.ContentRange is { Length: 0 }:
                r.Dispose();
                return new Probe(finalUri, name, 0, true, etag, lm, null);
            case HttpStatusCode.PartialContent:
                r.Dispose();
                return new Probe(finalUri, name, null, false, etag, lm, null);
            default:
                r.Dispose();
                throw StatusError(r, job);
        }
    }

    private HttpRequestMessage CreateRequest(Job job, Uri uri)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, uri);
        req.Headers.AcceptEncoding.ParseAdd("identity");
        if (job.RequestHeaders is { } headers)
        {
            foreach (var (k, v) in headers)
            {
                if (!ForwardedHeaders.Contains(k)) continue;
                if (k.Equals("User-Agent", StringComparison.OrdinalIgnoreCase)) req.Headers.UserAgent.Clear();
                req.Headers.TryAddWithoutValidation(k, v);
            }
        }
        return req;
    }

    private static Exception StatusError(HttpResponseMessage r, Job job)
    {
        var code = (int)r.StatusCode;
        var retryAfter = r.Headers.RetryAfter?.Delta ?? (r.Headers.RetryAfter?.Date is { } d ? d - DateTimeOffset.UtcNow : null);
        if (code is 408 or 425 or 429 or >= 500) return new HttpStatusError(code, retryAfter);
        var hasProgress = Interlocked.Read(ref job.ReceivedBytes) > 0;
        return code switch
        {
            401 or 403 or 410 when hasProgress => new JobAttentionException($"The link no longer works (HTTP {code}). It has probably expired — refresh the address from the original page to continue."),
            401 or 403 => new JobFailedException($"Access denied (HTTP {code}). The file may require signing in — try sending it from the browser extension."),
            404 => new JobFailedException("The file was not found on the server (HTTP 404)."),
            416 => new RangeUnsupportedException(),
            _ => new JobFailedException($"The server refused the request (HTTP {code}).")
        };
    }

    private async Task<T> WithRetry<T>(JobContext ctx, Func<Task<T>> action, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await action();
            }
            catch (Exception e) when (IsTransient(e, ct))
            {
                if (!NetworkInterface.GetIsNetworkAvailable())
                {
                    await WaitForNetwork(ctx, ct);
                    attempt--;
                    continue;
                }
                if (attempt >= ctx.Options.MaxAttempts)
                    throw new JobFailedException($"Gave up after {attempt} attempts: {JobManager.Describe(Unwrap(e))}");
                var delay = BackoffDelay(ctx.Options, attempt, e);
                ctx.SetStatus(JobStatus.Retrying, $"Retrying in {Math.Ceiling(delay.TotalSeconds)} s (attempt {attempt}/{ctx.Options.MaxAttempts}): {JobManager.Describe(Unwrap(e))}");
                await Task.Delay(delay, ct);
            }
        }
    }

    private static async Task WaitForNetwork(JobContext ctx, CancellationToken ct)
    {
        ctx.SetStatus(JobStatus.WaitingForNetwork, "Waiting for a network connection");
        while (!NetworkInterface.GetIsNetworkAvailable()) await Task.Delay(TimeSpan.FromSeconds(2), ct);
        ctx.SetStatus(JobStatus.Running);
    }

    private static TimeSpan BackoffDelay(EngineOptions o, int attempt, Exception e)
    {
        var ms = Math.Min(o.RetryMaxDelay.TotalMilliseconds, o.RetryBaseDelay.TotalMilliseconds * Math.Pow(2, attempt - 1));
        ms *= 0.8 + Random.Shared.NextDouble() * 0.4;
        if (e is HttpStatusError { RetryAfter: { } ra } && ra > TimeSpan.Zero) ms = Math.Max(ms, Math.Min(ra.TotalMilliseconds, 120_000));
        return TimeSpan.FromMilliseconds(ms);
    }

    private static bool IsTransient(Exception e, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return false;
        return e switch
        {
            HttpStatusError => true,
            OperationCanceledException => true,
            HttpRequestException h => h.InnerException is not BlockedAddressException,
            DiskException or JobAttentionException or JobFailedException or RangeUnsupportedException => false,
            IOException => true,
            _ => false
        };
    }

    private static bool IsThrottle(Exception e) => e switch
    {
        HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable } => true,
        HttpRequestException { StatusCode: null } => true,
        OperationCanceledException => true,
        IOException => true,
        _ => false
    };

    private static Exception Unwrap(Exception e) => e is OperationCanceledException ? new TimeoutException() : e;

    private static async ValueTask Write(SafeFileHandle handle, ReadOnlyMemory<byte> data, long offset, CancellationToken ct)
    {
        try
        {
            await RandomAccess.WriteAsync(handle, data, offset, ct);
        }
        catch (IOException e) when (IsDiskFull(e))
        {
            throw new JobAttentionException("The disk is full. Free some space and resume — downloaded data is kept.");
        }
        catch (IOException e)
        {
            throw new DiskException(e.Message, e);
        }
    }

    private static bool IsDiskFull(IOException e) => (e.HResult & 0xFFFF) is 112 or 39;

    private async Task CheckpointLoop(RunState run, CancellationToken ct)
    {
        while (true)
        {
            await Task.Delay(run.Ctx.Options.CheckpointInterval, ct);
            Checkpoint(run);
        }
    }

    private static void Checkpoint(RunState run)
    {
        var job = run.Job;
        List<(int, long, long, long)> snapshot;
        lock (job.Gate)
            snapshot = job.Segments.Select(s => (s.Index, s.Start, s.End, Interlocked.Read(ref s.Received))).ToList();
        var received = snapshot.Sum(s => s.Item4);
        try { run.File.Flush(flushToDisk: true); }
        catch (ObjectDisposedException) { return; }
        run.Ctx.Store.SaveProgress(job, snapshot, received);
    }

    private static async Task FinalizeAsync(JobContext ctx, CancellationToken ct)
    {
        var job = ctx.Job;
        var part = PartPath(job);
        if (!File.Exists(part))
            await using (File.Create(part)) { }
        if (Checksums.Parse(job.Checksum) is { } sum)
        {
            ctx.SetStatus(JobStatus.Verifying, $"Verifying {sum.Algorithm.ToUpperInvariant()}");
            var actual = await Checksums.ComputeAsync(part, sum.Algorithm, null, ct);
            if (actual != sum.Hex)
                throw new JobAttentionException($"Checksum mismatch. Expected {sum.Hex}, got {actual}. The file was not saved; restart to download it again.");
        }
        var final = FileNames.UniquePath(job.Directory, job.FileName!);
        File.Move(part, final);
        job.FilePath = final;
        job.TotalBytes ??= Interlocked.Read(ref job.ReceivedBytes);
        ctx.Store.SaveProgress(job, [], Interlocked.Read(ref job.ReceivedBytes));
        job.Segments.Clear();
    }

    public Task CleanupAsync(Job job, bool deleteFiles)
    {
        if (job.FileName is not null && job.Status != JobStatus.Completed)
        {
            var part = PartPath(job);
            try { if (File.Exists(part)) File.Delete(part); } catch (IOException) { }
        }
        return Task.CompletedTask;
    }

    private static long? FreeSpace(string directory)
    {
        try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(directory))!).AvailableFreeSpace; }
        catch (Exception) { return null; }
    }

    private static string Fmt(long? bytes) => bytes is { } b ? $"{b / 1048576.0:0.##} MiB" : "unknown";

    private static void MakeSparse(SafeFileHandle handle)
    {
        const uint FsctlSetSparse = 0x000900C4;
        DeviceIoControl(handle, FsctlSetSparse, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, IntPtr inBuffer, uint inSize, IntPtr outBuffer, uint outSize, out uint returned, IntPtr overlapped);

    public void Dispose() => _client.Dispose();

    private sealed record Probe(Uri FinalUri, string FileName, long? Total, bool AcceptsRanges, string? ETag, string? LastModified, HttpResponseMessage? Response);

    private sealed class RangeUnsupportedException() : Exception("The server does not honour range requests.");

    private sealed class DiskException(string message, Exception inner) : Exception("Could not write the file: " + message, inner);

    private sealed class HttpStatusError(int status, TimeSpan? retryAfter) : HttpRequestException($"Server returned {status}.", null, (HttpStatusCode)status)
    {
        public TimeSpan? RetryAfter { get; } = retryAfter;
    }
}
