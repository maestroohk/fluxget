using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using FluxGet.Host;
using FluxGet.Testing;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace FluxGet.Engine.Tests;

public sealed class HostSecurityTests : IAsyncLifetime
{
    private const string ExtensionOrigin = "chrome-extension://abcdefghijklmnopabcdefghijklmnop";
    private WebApplication _app = null!;
    private string _root = null!;
    private string _base = null!;
    private string _key = null!;

    public async Task InitializeAsync()
    {
        _root = Path.Combine(Path.GetTempPath(), "fluxget-host-tests", Guid.NewGuid().ToString("N"));
        var port = FreePort();
        _app = FluxGetServer.Build([], o =>
        {
            o.Port = port;
            o.DataDirectory = _root;
            o.Mode = HostMode.Local;
        });
        var manager = _app.Services.GetRequiredService<JobManager>();
        manager.UpdateSettings(manager.Settings with { DownloadDirectory = Path.Combine(_root, "dl") });
        await _app.StartAsync();
        _base = $"http://127.0.0.1:{port}";
        _key = _app.Services.GetRequiredService<AccessKeys>().SessionKey;
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private HttpClient Client(string? token = null, string? origin = null)
    {
        var c = new HttpClient(new HttpClientHandler { UseCookies = false }) { BaseAddress = new Uri(_base) };
        if (token is not null) c.DefaultRequestHeaders.Authorization = new("Bearer", token);
        if (origin is not null) c.DefaultRequestHeaders.Add("Origin", origin);
        return c;
    }

    [Fact]
    public async Task Api_requires_a_token()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client().GetAsync("/api/jobs")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client("wrong-token-wrong-token-wrong").GetAsync("/api/jobs")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Client(_key).GetAsync("/api/jobs")).StatusCode);
    }

    [Fact]
    public async Task Websites_are_rejected_even_with_a_token()
    {
        var r = await Client(_key, "https://evil.example").PostAsJsonAsync("/api/downloads", new { urls = new[] { "https://x.test/a" } });
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        var pair = await Client(null, "https://evil.example").PostAsJsonAsync("/api/pair/request", new { name = "x" });
        Assert.Equal(HttpStatusCode.Forbidden, pair.StatusCode);
    }

    [Fact]
    public async Task Dns_rebinding_host_header_is_rejected()
    {
        var c = Client(_key);
        var req = new HttpRequestMessage(HttpMethod.Get, "/api/jobs");
        req.Headers.Host = "attacker.example";
        Assert.Equal(HttpStatusCode.MisdirectedRequest, (await c.SendAsync(req)).StatusCode);
    }

    [Fact]
    public async Task Login_sets_http_only_strict_cookie()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client().PostAsJsonAsync("/api/session", new { key = "nope" })).StatusCode);
        var r = await Client().PostAsJsonAsync("/api/session", new { key = _key });
        var cookie = r.Headers.GetValues("Set-Cookie").Single();
        Assert.Contains("httponly", cookie.ToLowerInvariant());
        Assert.Contains("samesite=strict", cookie.ToLowerInvariant());
        var c = Client();
        c.DefaultRequestHeaders.Add("Cookie", cookie.Split(';')[0]);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/jobs")).StatusCode);
    }

    [Fact]
    public async Task Extension_pairs_downloads_and_can_be_revoked()
    {
        await using var files = await TestFileServer.StartAsync();
        var f = files.Add("ext.bin", new ServedFile { Size = 200_000 });

        var ext = Client(null, ExtensionOrigin);
        var req = await (await ext.PostAsJsonAsync("/api/pair/request", new { name = "Chrome" })).Content.ReadFromJsonAsync<JsonElement>();
        var id = req.GetProperty("id").GetString()!;
        Assert.Equal(HttpStatusCode.BadRequest, (await ext.PostAsJsonAsync("/api/pair/confirm", new { id, code = "000000x" })).StatusCode);
        var code = _app.Services.GetRequiredService<PairingService>().PeekCode(id)!;
        var confirm = await (await ext.PostAsJsonAsync("/api/pair/confirm", new { id, code })).Content.ReadFromJsonAsync<JsonElement>();
        var token = confirm.GetProperty("token").GetString()!;

        var api = Client(token, ExtensionOrigin);
        var add = await api.PostAsJsonAsync("/api/downloads", new { urls = new[] { files.Url("ext.bin") }, headers = new { referer = "https://site.test/page", cookie = "s=1" } });
        add.EnsureSuccessStatusCode();
        var jobId = (await add.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("added")[0].GetProperty("id").GetInt64();
        JsonElement job = default;
        for (var i = 0; i < 200; i++)
        {
            job = await api.GetFromJsonAsync<JsonElement>($"/api/jobs/{jobId}");
            if (job.GetProperty("status").GetString() == "Completed") break;
            await Task.Delay(50);
        }
        Assert.Equal("Completed", job.GetProperty("status").GetString());
        Assert.Equal(TestFileServer.Sha256(f), EngineHarness.Sha256(job.GetProperty("filePath").GetString()!));
        Assert.DoesNotContain("s=1", job.GetRawText());

        var tokens = await Client(_key).GetFromJsonAsync<JsonElement>("/api/tokens");
        var tokenId = tokens[0].GetProperty("id").GetString();
        await Client(_key).DeleteAsync($"/api/tokens/{tokenId}");
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.GetAsync("/api/jobs")).StatusCode);
    }

    [Fact]
    public async Task Events_stream_sends_initial_snapshot()
    {
        var c = Client(_key);
        using var resp = await c.GetAsync("/api/events", HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal("text/event-stream", resp.Content.Headers.ContentType?.MediaType);
        using var reader = new StreamReader(await resp.Content.ReadAsStreamAsync());
        string? line;
        while ((line = await reader.ReadLineAsync()) is not null && !line.StartsWith("event: jobs")) { }
        Assert.Equal("event: jobs", line);
    }
}
