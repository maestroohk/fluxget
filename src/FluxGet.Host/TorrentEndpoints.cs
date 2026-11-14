using System.Text.Json;
using FluxGet.Engine;
using FluxGet.Engine.Torrents;

namespace FluxGet.Host;

public sealed record MagnetBody(string Magnet, string? Directory);
public sealed record TorrentFileBody(int Index, bool Download);

public static class TorrentEndpoints
{
    public static void MapTorrents(this WebApplication app)
    {
        var api = app.MapGroup("/api/torrents");

        api.MapPost("/", (MagnetBody body, JobManager m) =>
        {
            try
            {
                var magnet = TorrentRunner.ValidateMagnet(body.Magnet?.Trim() ?? "");
                var job = m.Add(new AddJobRequest { Kind = JobKind.Torrent, Url = magnet, Directory = body.Directory });
                return Results.Ok(JobDto.From(job, m.Settings));
            }
            catch (DuplicateJobException) { return Results.Problem("This torrent is already in the list.", statusCode: 409); }
            catch (ArgumentException e) { return Results.Problem(e.Message, statusCode: 400); }
        });

        api.MapPost("/file", async (HttpRequest request, JobManager m, TorrentRunner torrents) =>
        {
            if (!request.HasFormContentType) return Results.Problem("Upload a .torrent file.", statusCode: 400);
            var form = await request.ReadFormAsync();
            var file = form.Files.GetFile("file");
            if (file is null || file.Length == 0 || file.Length > 10 * 1024 * 1024) return Results.Problem("Upload a .torrent file up to 10 MB.", statusCode: 400);
            using var ms = new MemoryStream();
            await file.CopyToAsync(ms);
            var bytes = ms.ToArray();
            (string Magnet, string Name, long Size) info;
            try { info = TorrentRunner.Inspect(bytes); }
            catch (Exception) { return Results.Problem("This is not a valid .torrent file.", statusCode: 400); }
            var dir = Path.Combine(torrents.CacheDirectory, "files");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"{Guid.NewGuid():N}.torrent");
            await File.WriteAllBytesAsync(path, bytes);
            try
            {
                var job = m.Add(new AddJobRequest
                {
                    Kind = JobKind.Torrent,
                    Url = info.Magnet,
                    FileName = FileNames.Sanitize(info.Name),
                    Options = JsonSerializer.Serialize(new TorrentOptions { TorrentFile = path }, Json.Options)
                });
                return Results.Ok(JobDto.From(job, m.Settings));
            }
            catch (DuplicateJobException)
            {
                File.Delete(path);
                return Results.Problem("This torrent is already in the list.", statusCode: 409);
            }
        });

        api.MapGet("/{id:long}/files", (long id, JobManager m, TorrentRunner torrents) =>
            m.Find(id) is { Kind: JobKind.Torrent } job ? Results.Ok(torrents.Files(job) ?? []) : Results.NotFound());

        api.MapPut("/{id:long}/files", async (long id, TorrentFileBody body, JobManager m, TorrentRunner torrents) =>
        {
            if (m.Find(id) is not { Kind: JobKind.Torrent } job) return Results.NotFound();
            await torrents.SetFileAsync(job, body.Index, body.Download);
            return Results.NoContent();
        });

        api.MapGet("/{id:long}/peers", async (long id, JobManager m, TorrentRunner torrents) =>
            m.Find(id) is { Kind: JobKind.Torrent } job ? Results.Ok(await torrents.PeersAsync(job)) : Results.NotFound());
    }
}
