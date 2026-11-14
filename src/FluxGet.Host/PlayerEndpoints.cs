using System.Collections.Concurrent;
using FluxGet.Engine;
using FluxGet.Engine.Http;

namespace FluxGet.Host;

public static class MediaFiles
{
    public static readonly HashSet<string> Video = new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".m4v", ".mkv", ".webm", ".mov", ".avi", ".wmv", ".flv", ".ts", ".m2ts", ".mpg", ".mpeg", ".3gp", ".ogv" };
    public static readonly HashSet<string> Audio = new(StringComparer.OrdinalIgnoreCase) { ".mp3", ".m4a", ".aac", ".opus", ".ogg", ".oga", ".flac", ".wav", ".wma", ".alac", ".aiff", ".mka" };

    public static string? Kind(string? path)
    {
        var ext = Path.GetExtension(path ?? "");
        return Video.Contains(ext) ? "video" : Audio.Contains(ext) ? "audio" : null;
    }
}

public sealed record LibraryItem(long JobId, string Title, string Path, string Kind, long Size, DateTime? CompletedAt, long PositionMs, long DurationMs, DateTime? LastPlayed);

public sealed class PreviewTokens
{
    private readonly ConcurrentDictionary<string, (long JobId, DateTime Expires)> _tokens = new();

    public string Issue(long jobId)
    {
        foreach (var t in _tokens.Where(t => t.Value.Expires < DateTime.UtcNow)) _tokens.TryRemove(t.Key, out _);
        var token = AccessKeys.NewSecret();
        _tokens[token] = (jobId, DateTime.UtcNow.AddHours(6));
        return token;
    }

    public bool Valid(string? token, long jobId) =>
        token is not null && _tokens.TryGetValue(token, out var t) && t.JobId == jobId && t.Expires > DateTime.UtcNow;
}

public static class PlayerEndpoints
{
    public static void MapPlayer(this WebApplication app)
    {
        var api = app.MapGroup("/api/player");

        api.MapGet("/library", (JobManager m, string? search, string? kind, string? sort) =>
        {
            var (items, _) = m.Store.QueryCompleted(search, null, 0, 2000);
            var media = items
                .Where(j => j.FilePath is not null && MediaFiles.Kind(j.FilePath) is not null && File.Exists(j.FilePath))
                .Where(j => kind is not ("video" or "audio") || MediaFiles.Kind(j.FilePath) == kind)
                .ToList();
            var playback = m.Store.PlaybackFor(media.Select(j => j.FilePath!));
            var list = media.Select(j =>
            {
                playback.TryGetValue(j.FilePath!, out var p);
                return new LibraryItem(j.Id, p?.Title ?? Path.GetFileNameWithoutExtension(j.FilePath!), j.FilePath!, MediaFiles.Kind(j.FilePath)!,
                    j.TotalBytes ?? 0, j.CompletedAt, p?.PositionMs ?? 0, p?.DurationMs ?? 0, p?.LastPlayed);
            });
            list = sort switch
            {
                "size" => list.OrderByDescending(i => i.Size),
                "duration" => list.OrderByDescending(i => i.DurationMs),
                "played" => list.OrderByDescending(i => i.LastPlayed ?? DateTime.MinValue),
                "title" => list.OrderBy(i => i.Title, StringComparer.CurrentCultureIgnoreCase),
                _ => list.OrderByDescending(i => i.CompletedAt ?? DateTime.MinValue)
            };
            return list.ToList();
        });

        api.MapGet("/recent", (JobStore store) => store.RecentPlayback(12)
            .Where(p => p.Path.StartsWith("http", StringComparison.OrdinalIgnoreCase) || File.Exists(p.Path))
            .Select(p => new { p.Path, title = p.Title ?? Path.GetFileNameWithoutExtension(p.Path), p.PositionMs, p.DurationMs, p.LastPlayed, kind = MediaFiles.Kind(p.Path) ?? "stream" }));

        api.MapDelete("/recent", (string path, JobStore store) =>
        {
            store.RemovePlayback(path);
            return Results.NoContent();
        });

        api.MapGet("/preview/{id:long}", (long id, JobManager m) =>
        {
            var job = m.Find(id);
            if (job is null) return Results.NotFound();
            return Results.Ok(PreviewInfo(job));
        });

        app.MapGet("/preview/{id:long}", (long id, string? t, JobManager m, PreviewTokens tokens, HttpContext ctx) =>
        {
            if (!tokens.Valid(t, id)) return Results.Unauthorized();
            var job = m.Find(id);
            if (job is null) return Results.NotFound();
            string path;
            long length;
            if (job.Status == JobStatus.Completed && job.FilePath is { } done && File.Exists(done))
            {
                path = done;
                length = new FileInfo(done).Length;
            }
            else
            {
                var info = PreviewInfo(job);
                if (!info.Available) return Results.Problem(info.Reason, statusCode: 409);
                path = HttpRunner.PartPath(job);
                length = info.ReadableBytes;
            }
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.Asynchronous);
            return Results.Stream(new PrefixStream(stream, length), "application/octet-stream", enableRangeProcessing: true);
        });
    }

    public sealed record Preview(bool Available, long ReadableBytes, long? TotalBytes, string? Reason);

    public static Preview PreviewInfo(Job job)
    {
        if (job.Status == JobStatus.Completed) return new Preview(true, job.TotalBytes ?? 0, job.TotalBytes, null);
        if (job.Kind != JobKind.Http || job.FileName is null)
            return new Preview(false, 0, job.TotalBytes, "This download can be played once it has finished.");
        if (MediaFiles.Kind(job.FileName) is null)
            return new Preview(false, 0, job.TotalBytes, "This file type can't be previewed.");
        long prefix = 0;
        lock (job.Gate)
        {
            foreach (var s in job.Segments.OrderBy(s => s.Start))
            {
                if (s.Start != prefix) break;
                var received = Interlocked.Read(ref s.Received);
                prefix = s.Start + received;
                if (!s.IsComplete) break;
            }
        }
        if (job.Segments.Count == 0 || !File.Exists(HttpRunner.PartPath(job)))
            return new Preview(false, 0, job.TotalBytes, "Nothing has been downloaded yet.");
        const long minimum = 2L * 1024 * 1024;
        return prefix >= minimum
            ? new Preview(true, prefix, job.TotalBytes, null)
            : new Preview(false, prefix, job.TotalBytes, "Preview starts once the beginning of the file has downloaded.");
    }
}

public sealed class PrefixStream(Stream inner, long length) : Stream
{
    private long _position;

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => length;

    public override long Position
    {
        get => _position;
        set => Seek(value, SeekOrigin.Begin);
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        var allowed = (int)Math.Min(buffer.Length, length - _position);
        if (allowed <= 0) return 0;
        inner.Position = _position;
        var n = inner.Read(buffer[..allowed]);
        _position += n;
        return n;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        var allowed = (int)Math.Min(buffer.Length, length - _position);
        if (allowed <= 0) return 0;
        inner.Position = _position;
        var n = await inner.ReadAsync(buffer[..allowed], ct);
        _position += n;
        return n;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        _position = Math.Clamp(origin switch { SeekOrigin.Current => _position + offset, SeekOrigin.End => length + offset, _ => offset }, 0, length);
        return _position;
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) inner.Dispose();
        base.Dispose(disposing);
    }
}
