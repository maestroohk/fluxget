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

public static class MediaEndpoints
{
    public static void MapMedia(this WebApplication app)
    {
        var api = app.MapGroup("/api/media");

        api.MapPost("/analyze", async (AnalyzeBody body, YtDlp yt, FluxGetHostOptions o, CancellationToken ct) =>
        {
            try
            {
                if (o.Mode == HostMode.Hosted) await UrlGuard.EnsurePublicAsync(UrlGuard.Parse(body.Url), ct);
                return Results.Ok(await yt.AnalyzeAsync(body.Url, body.Start ?? 1, ct));
            }
            catch (Exception e) when (e is MediaException or ArgumentException or TimeoutException)
            {
                return Results.Problem(e.Message, statusCode: 422);
            }
        });

        api.MapPost("/download", async (MediaDownloadBody body, JobManager m, FluxGetHostOptions o, CancellationToken ct) =>
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
                        Headers = headers
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
}
