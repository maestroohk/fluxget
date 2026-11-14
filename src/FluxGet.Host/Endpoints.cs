using System.Diagnostics;
using FluxGet.Engine;
using FluxGet.Engine.Media;

namespace FluxGet.Host;

public static class Endpoints
{
    public static void MapCore(this WebApplication app)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/health", () => Results.Ok(new { status = "ok" }));

        api.MapGet("/session", (HttpContext ctx, AccessKeys keys) => Results.Ok(new { authenticated = keys.IsValid(RequestGuard.TokenFrom(ctx)) }));
        api.MapPost("/session", (LoginBody body, HttpContext ctx, AccessKeys keys, FluxGetHostOptions o) =>
        {
            if (!AccessKeys.FixedEquals(body.Key?.Trim() ?? "", keys.SessionKey)) return Results.Unauthorized();
            SetSessionCookie(ctx, keys.SessionKey, o);
            return Results.Ok(new { authenticated = true });
        }).RequireRateLimiting("auth");
        api.MapDelete("/session", (HttpContext ctx) =>
        {
            ctx.Response.Cookies.Delete(AccessKeys.CookieName);
            return Results.NoContent();
        });

        api.MapPost("/pair/request", (PairRequestBody body, HttpContext ctx, PairingService pairing) =>
        {
            if (!RequestGuard.IsExtensionOrigin(ctx.Request.Headers.Origin.ToString()))
                return Results.Problem("Pairing is only available to browser extensions.", statusCode: 403);
            try
            {
                var (id, expires) = pairing.Request(body.Name ?? "");
                return Results.Ok(new { id, expires });
            }
            catch (InvalidOperationException e)
            {
                return Results.Problem(e.Message, statusCode: 429);
            }
        }).RequireRateLimiting("auth");
        api.MapPost("/pair/confirm", (PairConfirmBody body, PairingService pairing, AccessKeys keys) =>
        {
            var name = pairing.Confirm(body.Id, body.Code);
            return name is null ? Results.Problem("The code is wrong or has expired.", statusCode: 400) : Results.Ok(new { token = keys.IssueExtensionToken(name) });
        }).RequireRateLimiting("auth");
        api.MapDelete("/pair/{id}", (string id, PairingService pairing) =>
        {
            pairing.Dismiss(id);
            return Results.NoContent();
        });
        api.MapGet("/tokens", (JobStore store) => store.ListTokens().Select(t => new { id = t.Hash, t.Name, t.CreatedAt }));
        api.MapDelete("/tokens/{id}", (string id, AccessKeys keys) =>
        {
            keys.Revoke(id);
            return Results.NoContent();
        });

        api.MapGet("/events", (HttpContext ctx, EventHub hub, JobManager m) =>
            hub.StreamAsync(ctx, () => [EventHub.Format("jobs", m.Unfinished().Select(j => JobDto.From(j, m.Settings)))]));

        api.MapGet("/jobs", (JobManager m) => m.Unfinished().Select(j => JobDto.From(j, m.Settings)));
        api.MapGet("/jobs/completed", (JobManager m, string? search, JobKind? kind, int offset = 0, int limit = 100) =>
        {
            var (items, total) = m.Store.QueryCompleted(search, kind, Math.Max(0, offset), Math.Clamp(limit, 1, 500));
            return new { total, items = items.Select(j => JobDto.From(j, m.Settings, false)) };
        });
        api.MapGet("/jobs/{id:long}", (long id, JobManager m) => m.Find(id) is { } j ? Results.Ok(JobDto.From(j, m.Settings)) : Results.NotFound());

        api.MapPost("/downloads", (AddDownloadsBody body, JobManager m) =>
        {
            var urls = (body.Urls ?? []).Select(u => u?.Trim()).Where(u => !string.IsNullOrEmpty(u)).Distinct().Take(5000).ToList();
            if (urls.Count == 0) return Results.Problem("Add at least one address.", statusCode: 400);
            var headers = ToHeaders(body.Headers);
            var added = new List<JobDto>();
            var dups = new List<DuplicateDto>();
            var rejected = new List<RejectedDto>();
            foreach (var url in urls)
            {
                try
                {
                    var job = m.Add(new AddJobRequest
                    {
                        Url = url!,
                        FileName = urls.Count == 1 ? body.FileName : null,
                        Directory = body.Directory,
                        Connections = body.Connections,
                        SpeedLimit = body.SpeedLimit,
                        Checksum = urls.Count == 1 ? body.Checksum : null,
                        UseSchedule = body.UseSchedule,
                        Priority = body.Priority,
                        StartPaused = body.StartPaused,
                        AllowDuplicate = body.AllowDuplicate,
                        Headers = headers
                    });
                    added.Add(JobDto.From(job, m.Settings));
                }
                catch (DuplicateJobException d) { dups.Add(new DuplicateDto(url!, d.Existing.Id)); }
                catch (ArgumentException e) { rejected.Add(new RejectedDto(url!, e.Message)); }
            }
            return Results.Ok(new AddResult(added, dups, rejected));
        });

        api.MapPost("/jobs/{id:long}/pause", (long id, JobManager m) => Act(() => m.Pause(id)));
        api.MapPost("/jobs/{id:long}/resume", (long id, JobManager m) => Act(() => m.Resume(id)));
        api.MapPost("/jobs/{id:long}/restart", (long id, JobManager m) => ActAsync(() => m.RestartAsync(id)));
        api.MapPost("/jobs/{id:long}/address", (long id, AddressBody body, JobManager m) => ActAsync(() => m.RefreshUrlAsync(id, body.Url)));
        api.MapPatch("/jobs/{id:long}", (long id, UpdateJobBody body, JobManager m) => Act(() => m.Update(id, body.Priority, body.SpeedLimit, body.UseSchedule)));
        api.MapDelete("/jobs/{id:long}", (long id, bool? deleteFile, JobManager m) => ActAsync(() => m.RemoveAsync(id, deleteFile == true)));
        api.MapPost("/jobs/pause-all", (JobManager m) =>
        {
            foreach (var j in m.Unfinished()) m.Pause(j.Id);
            return Results.NoContent();
        });
        api.MapPost("/jobs/resume-all", (JobManager m) =>
        {
            foreach (var j in m.Unfinished().Where(j => j.Status == JobStatus.Paused)) m.Resume(j.Id);
            return Results.NoContent();
        });

        api.MapPost("/jobs/{id:long}/open", (long id, bool? reveal, JobManager m, FluxGetHostOptions o) =>
        {
            if (o.Mode == HostMode.Hosted || !OperatingSystem.IsWindows()) return Results.Problem("Not available in this mode.", statusCode: 400);
            var job = m.Find(id);
            var path = job?.FilePath;
            if (path is null || (!File.Exists(path) && !Directory.Exists(path))) return Results.NotFound();
            if (reveal == true) Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { "/select,", path } });
            else Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            return Results.NoContent();
        });

        api.MapPost("/app/activate", (ActivateBody body, AppSignals signals) =>
        {
            signals.Activate(body.Argument);
            return Results.NoContent();
        });

        api.MapGet("/settings", (JobManager m) => m.Settings);
        api.MapPut("/settings", (EngineSettings s, JobManager m, FluxGetHostOptions o) =>
        {
            if (o.Mode == HostMode.Hosted) s = s with { DownloadDirectory = m.Settings.DownloadDirectory };
            try
            {
                m.UpdateSettings(s);
                return Results.Ok(m.Settings);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return Results.Problem("The download folder cannot be used: " + e.Message, statusCode: 400);
            }
        });

        api.MapGet("/system", (JobManager m, FluxGetHostOptions o) =>
        {
            long? free = null;
            try { free = new DriveInfo(Path.GetPathRoot(m.Settings.DownloadDirectory)!).AvailableFreeSpace; } catch (Exception) { }
            var (completed, bytes) = m.Store.CompletedTotals();
            return new
            {
                version = typeof(Endpoints).Assembly.GetName().Version?.ToString(3),
                mode = o.Mode.ToString(),
                downloadDirectory = m.Settings.DownloadDirectory,
                freeBytes = free,
                completedCount = completed,
                completedBytes = bytes,
                tools = app.Services.GetService<ToolStatus>()?.Snapshot()
            };
        });
    }

    public static void SetSessionCookie(HttpContext ctx, string key, FluxGetHostOptions o) =>
        ctx.Response.Cookies.Append(AccessKeys.CookieName, key, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Strict,
            Secure = ctx.Request.IsHttps,
            MaxAge = TimeSpan.FromDays(365),
            Path = "/"
        });

    internal static IReadOnlyDictionary<string, string>? ToHeaders(HeadersBody? h)
    {
        if (h is null) return null;
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(h.Cookie)) d["Cookie"] = h.Cookie.Trim();
        if (!string.IsNullOrWhiteSpace(h.Referer) && Uri.TryCreate(h.Referer, UriKind.Absolute, out var r) && r.Scheme.StartsWith("http")) d["Referer"] = r.ToString();
        if (!string.IsNullOrWhiteSpace(h.UserAgent) && h.UserAgent.Length < 512) d["User-Agent"] = h.UserAgent.Trim();
        return d.Count == 0 ? null : d;
    }

    private static IResult Act(Action action)
    {
        try
        {
            action();
            return Results.NoContent();
        }
        catch (KeyNotFoundException) { return Results.NotFound(); }
        catch (ArgumentException e) { return Results.Problem(e.Message, statusCode: 400); }
    }

    private static async Task<IResult> ActAsync(Func<Task> action)
    {
        try
        {
            await action();
            return Results.NoContent();
        }
        catch (KeyNotFoundException) { return Results.NotFound(); }
        catch (ArgumentException e) { return Results.Problem(e.Message, statusCode: 400); }
    }
}
