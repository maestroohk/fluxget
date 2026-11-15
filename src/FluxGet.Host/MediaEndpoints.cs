using System.Text.Json;
using FluxGet.Engine;
using FluxGet.Engine.Media;

namespace FluxGet.Host;

public sealed record AnalyzeBody(string Url, int? Start);
public sealed record MediaItem(string Url, string? Title, string? Id);
public sealed record MediaDownloadBody(
    MediaItem[] Items, string? Group, string Mode, int? Height, string? Container, string? AudioFormat, string? AudioQuality,
    bool Subtitles, string? Directory, HeadersBody? Headers);
public sealed record ConvertBody(long JobId, string AudioFormat, string? AudioQuality);
public sealed record GalleryBody(string Url);
public sealed record GalleryFile(string Url, string? Name);
public sealed record GalleryDownloadBody(string Url, string? Title, int[]? Indices, GalleryFile[]? Files, string? Directory);
public sealed record SessionBody(string Url, HeadersBody? Headers);

public static class MediaEndpoints
{
    public static void MapMedia(this WebApplication app)
    {
        var api = app.MapGroup("/api/media");

        api.MapPost("/analyze", async (AnalyzeBody body, YtDlp yt, GalleryDl gallery, PageScanner pages, FluxGetHostOptions o, CancellationToken ct) =>
        {
            try
            {
                if (o.Mode == HostMode.Hosted) await UrlGuard.EnsurePublicAsync(UrlGuard.Parse(body.Url), ct);
                return Results.Ok<object>(await yt.AnalyzeAsync(body.Url, body.Start ?? 1, ct));
            }
            catch (MediaException video) when ((body.Start ?? 1) == 1)
            {
                try
                {
                    return Results.Ok<object>(await FindFilesAsync(body.Url, gallery, pages, ct));
                }
                catch (Exception e) when (e is MediaException or TimeoutException)
                {
                    var noVideo = video.Message.StartsWith("No video was found", StringComparison.Ordinal) || video.Message.Contains("isn't supported", StringComparison.Ordinal);
                    return Results.Problem(noVideo ? e.Message : video.Message, statusCode: 422);
                }
            }
            catch (Exception e) when (e is MediaException or ArgumentException or TimeoutException)
            {
                return Results.Problem(e.Message, statusCode: 422);
            }
        });

        api.MapPost("/gallery", async (GalleryBody body, GalleryDl gallery, PageScanner pages, FluxGetHostOptions o, CancellationToken ct) =>
        {
            try
            {
                if (o.Mode == HostMode.Hosted) await UrlGuard.EnsurePublicAsync(UrlGuard.Parse(body.Url), ct);
                return Results.Ok(await FindFilesAsync(body.Url, gallery, pages, ct));
            }
            catch (Exception e) when (e is MediaException or ArgumentException or TimeoutException)
            {
                return Results.Problem(e.Message, statusCode: 422);
            }
        });

        api.MapPost("/gallery/download", async (GalleryDownloadBody body, JobManager m, SiteSessions sessions, FluxGetHostOptions o, CancellationToken ct) =>
        {
            Uri uri;
            try
            {
                uri = UrlGuard.Parse(body.Url);
                if (o.Mode == HostMode.Hosted)
                {
                    await UrlGuard.EnsurePublicAsync(uri, ct);
                    foreach (var f in body.Files ?? []) await UrlGuard.EnsurePublicAsync(UrlGuard.Parse(f.Url), ct);
                }
            }
            catch (Exception e) when (e is ArgumentException or System.Net.Sockets.SocketException)
            {
                return Results.Problem(e.Message, statusCode: 400);
            }
            var title = string.IsNullOrWhiteSpace(body.Title) ? uri.Host : body.Title.Trim();
            var folderName = FileNames.FolderName(title);
            var session = sessions.For(uri);

            if (body.Files is { Length: > 0 } files)
            {
                var headers = new Dictionary<string, string>(session ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase) { ["Referer"] = uri.ToString() };
                var ids = new List<long>();
                try
                {
                    var directory = m.ResolveDirectory(Path.Combine(body.Directory ?? m.Settings.DownloadDirectory, folderName));
                    foreach (var f in files.Take(GalleryDl.Limit + PageScanner.Limit))
                    {
                        try
                        {
                            var fileHeaders = new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase);
                            if (SiteSessions.Site(new Uri(f.Url)) != SiteSessions.Site(uri)) fileHeaders.Remove("Cookie");
                            ids.Add(m.Add(new AddJobRequest { Url = f.Url, FileName = f.Name, Directory = directory, Group = title, Headers = fileHeaders }).Id);
                        }
                        catch (DuplicateJobException d) { ids.Add(d.Existing.Id); }
                    }
                }
                catch (ArgumentException e)
                {
                    return Results.Problem(e.Message, statusCode: 400);
                }
                return Results.Ok(new { added = ids.Count, ids });
            }

            var indices = (body.Indices ?? []).Where(i => i is > 0 and <= 100_000).Distinct().Order().ToArray();
            if (indices.Length == 0) return Results.Problem("Choose at least one file.", statusCode: 400);
            var options = JsonSerializer.Serialize(new MediaOptions { Mode = "gallery", Range = GalleryDl.Ranges(indices), Count = indices.Length }, Json.Options);
            try
            {
                var job = m.Add(new AddJobRequest
                {
                    Kind = JobKind.Media,
                    Url = uri.ToString(),
                    FileName = folderName,
                    Directory = body.Directory,
                    Options = options,
                    Headers = session
                });
                return Results.Ok(new { added = 1, ids = new[] { job.Id } });
            }
            catch (DuplicateJobException d)
            {
                return Results.Ok(new { added = 0, ids = new[] { d.Existing.Id } });
            }
            catch (ArgumentException e)
            {
                return Results.Problem(e.Message, statusCode: 400);
            }
        });

        api.MapPost("/session", (SessionBody body, SiteSessions sessions) =>
        {
            if (Endpoints.ToHeaders(body.Headers) is not { } headers) return Results.NoContent();
            try
            {
                sessions.Remember(UrlGuard.Parse(body.Url), headers);
                return Results.NoContent();
            }
            catch (ArgumentException e)
            {
                return Results.Problem(e.Message, statusCode: 400);
            }
        });

        api.MapPost("/download", async (MediaDownloadBody body, JobManager m, SiteSessions sessions, FluxGetHostOptions o, CancellationToken ct) =>
        {
            if (o.Mode == HostMode.Hosted)
            {
                try
                {
                    foreach (var item in body.Items ?? []) await UrlGuard.EnsurePublicAsync(UrlGuard.Parse(item.Url), ct);
                }
                catch (Exception e) when (e is ArgumentException or System.Net.Sockets.SocketException)
                {
                    return Results.Problem(e.Message, statusCode: 400);
                }
            }
            if (body.Items is not { Length: > 0 }) return Results.Problem("Choose at least one item.", statusCode: 400);
            if (body.Mode is not ("video" or "audio")) return Results.Problem("Mode must be video or audio.", statusCode: 400);
            var container = body.Container ?? "mp4";
            var audioFormat = body.AudioFormat ?? "mp3";
            if (!MediaOptions.Containers.Contains(container) || !MediaOptions.AudioFormats.Contains(audioFormat))
                return Results.Problem("Unsupported output format.", statusCode: 400);
            if (body.AudioQuality is { } q && !MediaOptions.Qualities.Contains(q))
                return Results.Problem("Unsupported audio quality.", statusCode: 400);
            var headers = Endpoints.ToHeaders(body.Headers);
            var added = new List<JobDto>();
            var ids = new List<long>();
            var skipped = 0;
            foreach (var item in body.Items.Take(5000))
            {
                var options = JsonSerializer.Serialize(new MediaOptions
                {
                    Mode = body.Mode,
                    Height = body.Height is > 0 and <= 8640 ? body.Height : null,
                    Container = container,
                    AudioFormat = audioFormat,
                    AudioQuality = body.AudioQuality,
                    Subtitles = body.Subtitles,
                    MediaId = item.Id
                }, Json.Options);
                try
                {
                    var job = m.Add(new AddJobRequest
                    {
                        Kind = JobKind.Media,
                        Url = item.Url,
                        FileName = item.Title,
                        Group = body.Group,
                        Directory = body.Directory,
                        Options = options,
                        Headers = headers ?? (Uri.TryCreate(item.Url, UriKind.Absolute, out var itemUri) ? sessions.For(itemUri) : null)
                    });
                    added.Add(JobDto.From(job, m.Settings));
                    ids.Add(job.Id);
                }
                catch (DuplicateJobException d)
                {
                    skipped++;
                    ids.Add(d.Existing.Id);
                }
                catch (ArgumentException) { skipped++; }
            }
            return Results.Ok(new { added = added.Count, skipped, ids });
        });

        api.MapPost("/convert", (ConvertBody body, JobManager m) =>
        {
            var source = m.Find(body.JobId);
            if (source is not { Status: JobStatus.Completed, FilePath: { } path } || !File.Exists(path))
                return Results.Problem("That file is not available any more.", statusCode: 404);
            if (!MediaOptions.AudioFormats.Contains(body.AudioFormat)) return Results.Problem("Unsupported audio format.", statusCode: 400);
            if (body.AudioQuality is { } q && !MediaOptions.Qualities.Contains(q)) return Results.Problem("Unsupported audio quality.", statusCode: 400);
            var url = source.Url.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? source.Url : "https://localhost/convert";
            var job = m.Add(new AddJobRequest
            {
                Kind = JobKind.Media,
                Url = url,
                FileName = Path.GetFileNameWithoutExtension(path) + "." + (body.AudioFormat == "aac" ? "aac" : body.AudioFormat),
                Directory = Path.GetDirectoryName(path),
                AllowDuplicate = true,
                Options = JsonSerializer.Serialize(new MediaOptions { Mode = "convert", AudioFormat = body.AudioFormat, AudioQuality = body.AudioQuality, Source = path }, Json.Options)
            });
            return Results.Ok(JobDto.From(job, m.Settings));
        });
    }

    private static async Task<GalleryInfo> FindFilesAsync(string url, GalleryDl gallery, PageScanner pages, CancellationToken ct) =>
        (gallery.Available ? await gallery.AnalyzeAsync(url, ct) : null) ?? await pages.ScanAsync(url, ct);
}
