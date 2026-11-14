using FluxGet.Engine;
using FluxGet.Engine.Media;

namespace FluxGet.Host;

public sealed record GenerateSubtitlesBody(string Path, string? Model, string? Language, bool Translate);

public static class SubtitleEndpoints
{
    public static void MapSubtitles(this WebApplication app)
    {
        var api = app.MapGroup("/api/subtitles");

        api.MapGet("/models", (Transcriber t) => t.ListModels());

        api.MapGet("/tasks", (Transcriber t) => t.Tasks().Take(20).Select(Dto));

        api.MapGet("/tasks/{id}", (string id, Transcriber t) => t.Get(id) is { } task ? Results.Ok(Dto(task)) : Results.NotFound());

        api.MapPost("/tasks/{id}/cancel", (string id, Transcriber t) =>
        {
            t.Cancel(id);
            return Results.NoContent();
        });

        api.MapPost("/generate", (GenerateSubtitlesBody body, Transcriber t, JobManager m, FluxGetHostOptions o) =>
        {
            if (string.IsNullOrWhiteSpace(body.Path)) return Results.Problem("Choose a media file.", statusCode: 400);
            var full = System.IO.Path.GetFullPath(body.Path);
            if (o.Mode == HostMode.Hosted)
            {
                try { m.ResolveDirectory(System.IO.Path.GetDirectoryName(full)); }
                catch (ArgumentException e) { return Results.Problem(e.Message, statusCode: 400); }
            }
            if (MediaFiles.Kind(full) is null) return Results.Problem("Subtitles can be generated for video and audio files.", statusCode: 400);
            try
            {
                return Results.Ok(Dto(t.Start(full, body.Model ?? "base", body.Language ?? "auto", body.Translate)));
            }
            catch (ArgumentException e)
            {
                return Results.Problem(e.Message, statusCode: 400);
            }
        });
    }

    private static object Dto(TranscriptionTask t) => new
    {
        t.Id, t.MediaPath, t.Model, t.Language, t.Translate, t.Status, progress = Math.Round(t.Progress, 3), t.Detail,
        t.OutputPath, t.DetectedLanguage, t.Segments, t.Error, t.ElapsedMs, t.CreatedAt
    };
}
