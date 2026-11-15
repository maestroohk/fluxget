using FluxGet.Engine.Media;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FluxGet.Engine.Tests;

public sealed class GalleryTests
{
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    [Fact]
    public void Parses_gallery_dl_json_into_items()
    {
        const string json = """
        [
          [2, {"category": "instagram", "description": "Sunset over the lake", "username": "someone"}],
          [3, "https://cdn.example/a.jpg", {"category": "instagram", "filename": "a", "extension": "jpg", "width": 1080, "height": 1350}],
          [3, "https://cdn.example/b.mp4", {"category": "instagram", "filename": "b", "extension": "mp4", "display_url": "https://cdn.example/b.jpg"}],
          [6, "https://example.com/other", {}]
        ]
        """;
        var info = GalleryDl.Parse(json, "https://www.instagram.com/p/x/");
        Assert.Equal("Instagram", info.Source);
        Assert.Equal("Sunset over the lake", info.Title);
        Assert.Equal("someone", info.Uploader);
        Assert.Collection(info.Items,
            a => { Assert.Equal(1, a.Index); Assert.Equal("image", a.Kind); Assert.Equal("a.jpg", a.Name); Assert.Equal(1080, a.Width); Assert.Equal("https://cdn.example/a.jpg", a.Preview); },
            b => { Assert.Equal(2, b.Index); Assert.Equal("video", b.Kind); Assert.Equal("https://cdn.example/b.jpg", b.Preview); });
    }

    [Fact]
    public void Sign_in_errors_become_actionable_messages()
    {
        var e = Assert.Throws<MediaException>(() => GalleryDl.Parse("""[[-1, {"error": "AbortExtraction", "message": "HTTP redirect to login page (https://www.instagram.com/accounts/login/)"}]]""", "https://www.instagram.com/p/x/"));
        Assert.Contains("Pass my site cookies", e.Message);
    }

    [Theory]
    [InlineData("Index of /gnu/hello", "Index of -gnu-hello")]
    [InlineData("a:b*c?", "a_b_c_")]
    public void Page_titles_become_folder_names(string title, string expected) => Assert.Equal(expected, FileNames.FolderName(title));

    [Theory]
    [InlineData(new[] { 1 }, "1")]
    [InlineData(new[] { 1, 2, 3, 5, 7, 8 }, "1-3,5,7-8")]
    public void Selected_indices_become_compact_ranges(int[] indices, string expected) => Assert.Equal(expected, GalleryDl.Ranges(indices));

    [Fact]
    public void Page_scan_finds_images_media_and_linked_files()
    {
        const string html = """
            <html><head><title>Trip &amp; photos</title><base href="/assets/">
            <meta property="og:image" content="https://cdn.example/cover.jpg"></head><body>
            <img src="small.png" srcset="small.png 1x, large.png 2x" width="640">
            <img data-src="lazy.webp" src="data:image/gif;base64,R0lGOD">
            <img src="small.png">
            <video src="/v/clip.mp4" poster="poster.jpg"></video>
            <audio><source src="song.mp3"></audio>
            <a href="report.pdf">Report</a><a href="/about">About</a><a href="https://other.example/pack.zip#x">Pack</a>
            </body></html>
            """;
        var info = PageScanner.Parse(html, new Uri("https://site.example/trip/index.html"));
        Assert.Equal("Trip & photos", info.Title);
        Assert.True(info.Generic);
        Assert.Equal(new[]
        {
            "https://cdn.example/cover.jpg", "https://site.example/assets/large.png", "https://site.example/assets/lazy.webp", "https://site.example/assets/small.png",
            "https://site.example/v/clip.mp4", "https://site.example/assets/poster.jpg", "https://site.example/assets/song.mp3",
            "https://site.example/assets/report.pdf", "https://other.example/pack.zip"
        }, info.Items.Select(i => i.Url));
        Assert.Equal(["image", "image", "image", "image", "video", "image", "audio", "file", "file"], info.Items.Select(i => i.Kind));
        Assert.Equal(640, info.Items[1].Width);
    }

    [Fact]
    public async Task Gallery_dl_downloads_the_selected_file_into_a_named_folder()
    {
        var tools = ToolsDirectory();
        if (tools is null) return;

        await using var server = await StartServerAsync();
        var root = Path.Combine(Path.GetTempPath(), "fluxget-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var options = EngineHarness.FastOptions(root);
            var locator = new ToolLocator(tools);
            var gallery = new GalleryDl(locator, options, new SiteSessions());
            var url = server.Base + "/img/one.png";
            var info = await gallery.AnalyzeAsync(url, CancellationToken.None);
            Assert.NotNull(info);
            Assert.Single(info.Items);

            var store = new JobStore(Path.Combine(root, "fluxget.db"));
            store.SetSetting("engine", new EngineSettings { DownloadDirectory = Path.Combine(root, "downloads") });
            await using var manager = new JobManager(store, options, [new MediaRunner(new YtDlp(locator), gallery)]);
            manager.Start();
            var job = manager.Add(new AddJobRequest
            {
                Kind = JobKind.Media,
                Url = url,
                FileName = "Holiday photos",
                Options = System.Text.Json.JsonSerializer.Serialize(new MediaOptions { Mode = "gallery", Range = GalleryDl.Ranges([1]), Count = 1 }, Json.Options)
            });
            var until = Environment.TickCount64 + 60_000;
            while (manager.Find(job.Id) is { Status: not (JobStatus.Completed or JobStatus.Failed) } && Environment.TickCount64 < until) await Task.Delay(100);
            job = manager.Find(job.Id)!;
            Assert.True(job.Status == JobStatus.Completed, job.Error);
            var folder = Path.Combine(root, "downloads", "Holiday photos");
            var file = Assert.Single(Directory.GetFiles(folder));
            Assert.Equal(file, job.FilePath);
            Assert.Equal([.. Png, .. "one.png"u8.ToArray()], File.ReadAllBytes(file));
            Assert.Equal(Png.Length + 7, job.ReceivedBytes);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public async Task Page_scan_reads_a_live_page_and_direct_files()
    {
        await using var server = await StartServerAsync();
        var scanner = new PageScanner(new SiteSessions(), EngineHarness.FastOptions(Path.GetTempPath()));
        var info = await scanner.ScanAsync(server.Base + "/page.html", CancellationToken.None);
        Assert.Equal("Holiday photos", info.Title);
        Assert.Equal(["one.png", "two.png", "notes.pdf"], info.Items.Select(i => i.Name));
        var direct = await scanner.ScanAsync(server.Base + "/img/two.png", CancellationToken.None);
        Assert.Equal("two.png", Assert.Single(direct.Items).Name);
    }

    private sealed class Server(WebApplication app, string baseUrl) : IAsyncDisposable
    {
        public string Base => baseUrl;
        public ValueTask DisposeAsync() => app.DisposeAsync();
    }

    private static async Task<Server> StartServerAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(k => k.Listen(System.Net.IPAddress.Loopback, 0));
        builder.Logging.ClearProviders();
        var app = builder.Build();
        app.MapGet("/page.html", () => Results.Content("""
            <html><head><title>Holiday photos</title></head><body>
            <img src="/img/one.png"><img src="img/two.png"><a href="/files/notes.pdf">Notes</a>
            </body></html>
            """, "text/html"));
        app.MapGet("/img/{name}", (string name) => Results.Bytes([.. Png, .. System.Text.Encoding.ASCII.GetBytes(name)], "image/png"));
        await app.StartAsync();
        return new Server(app, app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First().TrimEnd('/'));
    }

    private static string? ToolsDirectory()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "tools", "bin");
            if (File.Exists(Path.Combine(candidate, OperatingSystem.IsWindows() ? "gallery-dl.exe" : "gallery-dl"))) return candidate;
        }
        return null;
    }
}
