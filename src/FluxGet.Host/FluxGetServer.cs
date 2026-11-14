using System.Threading.RateLimiting;
using FluxGet.Engine;
using FluxGet.Engine.Http;
using FluxGet.Engine.Media;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;

namespace FluxGet.Host;

public static class FluxGetServer
{
    public static WebApplication Build(string[] args, Action<FluxGetHostOptions>? configure = null)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory
        });
        var options = builder.Configuration.GetSection("FluxGet").Get<FluxGetHostOptions>() ?? new FluxGetHostOptions();
        configure?.Invoke(options);
        if (options.Mode == HostMode.Hosted && string.IsNullOrWhiteSpace(options.AccessToken))
            throw new InvalidOperationException("Hosted mode requires FluxGet__AccessToken (at least 24 characters).");
        var dataDir = options.ResolveDataDirectory();
        Directory.CreateDirectory(dataDir);

        builder.WebHost.ConfigureKestrel(k =>
        {
            if (options.IsLoopbackOnly) k.Listen(System.Net.IPAddress.Loopback, options.Port);
            else k.ListenAnyIP(options.Port);
            k.Limits.MaxRequestBodySize = 20 * 1024 * 1024;
        });
        builder.Services.ConfigureHttpJsonOptions(j =>
        {
            j.SerializerOptions.PropertyNamingPolicy = Json.Options.PropertyNamingPolicy;
            j.SerializerOptions.PropertyNameCaseInsensitive = true;
        });

        var engineOptions = new EngineOptions
        {
            DataDirectory = dataDir,
            BlockPrivateNetworks = options.Mode == HostMode.Hosted,
            RestrictToDownloadDirectory = options.Mode == HostMode.Hosted,
            ToolsDirectory = options.ToolsDirectory
        };
        var store = new JobStore(Path.Combine(dataDir, "fluxget.db"));
        var tools = new ToolLocator(options.ToolsDirectory);
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(engineOptions);
        builder.Services.AddSingleton(store);
        builder.Services.AddSingleton(tools);
        builder.Services.AddSingleton<ToolStatus>();
        builder.Services.AddSingleton<EventHub>();
        builder.Services.AddSingleton<AccessKeys>();
        builder.Services.AddSingleton<PairingService>();
        builder.Services.AddSingleton<IJobRunner>(_ => new HttpRunner(engineOptions));
        builder.Services.AddSingleton(sp => new JobManager(store, engineOptions, sp.GetServices<IJobRunner>(), sp.GetRequiredService<ILogger<JobManager>>()));
        builder.Services.AddHostedService<EngineLifetime>();
        builder.Services.AddRateLimiter(r =>
        {
            r.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            r.AddPolicy("auth", ctx => RateLimitPartition.GetFixedWindowLimiter(ctx.Connection.RemoteIpAddress?.ToString() ?? "-",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1) }));
        });

        var app = builder.Build();
        app.UseMiddleware<RequestGuard>();
        app.UseRateLimiter();

        var webRoot = ResolveWebRoot(options);
        if (webRoot is not null)
        {
            var files = new PhysicalFileProvider(webRoot);
            app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
            app.UseStaticFiles(new StaticFileOptions { FileProvider = files });
        }
        if (options.DevDashboard && FindUp("docs", "progress.json") is { } docs)
        {
            var types = new FileExtensionContentTypeProvider();
            types.Mappings[".md"] = "text/markdown; charset=utf-8";
            var docFiles = new PhysicalFileProvider(docs);
            app.UseDefaultFiles(new DefaultFilesOptions { RequestPath = "/dev", FileProvider = docFiles, DefaultFileNames = ["dashboard.html"] });
            app.UseStaticFiles(new StaticFileOptions { RequestPath = "/dev", FileProvider = docFiles, ContentTypeProvider = types, ServeUnknownFileTypes = false });
        }

        app.MapCore();
        if (webRoot is not null)
            app.MapFallback(async ctx =>
            {
                if (ctx.Request.Path.StartsWithSegments("/api")) { ctx.Response.StatusCode = 404; return; }
                ctx.Response.ContentType = "text/html; charset=utf-8";
                await ctx.Response.SendFileAsync(Path.Combine(webRoot, "index.html"));
            });

        var manager = app.Services.GetRequiredService<JobManager>();
        var hub = app.Services.GetRequiredService<EventHub>();
        manager.JobChanged += j => hub.Broadcast("job", JobDto.From(j, manager.Settings, false));
        manager.Tick += jobs => hub.Broadcast("tick", jobs.Select(j => JobDto.From(j, manager.Settings)));
        manager.SettingsChanged += s => hub.Broadcast("settings", s);
        return app;
    }

    private static string? ResolveWebRoot(FluxGetHostOptions o)
    {
        if (o.WebRoot is { } explicitRoot && File.Exists(Path.Combine(explicitRoot, "index.html"))) return Path.GetFullPath(explicitRoot);
        var bundled = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        if (File.Exists(Path.Combine(bundled, "index.html"))) return bundled;
        return FindUp(Path.Combine("web", "dist"), "index.html");
    }

    internal static string? FindUp(string relative, string marker)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(Path.Combine(candidate, marker))) return candidate;
        }
        return null;
    }
}

public sealed class EngineLifetime(JobManager manager) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        manager.Start();
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken) => await manager.DisposeAsync();
}
