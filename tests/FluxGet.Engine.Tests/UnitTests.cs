using System.Net;
using System.Net.Http.Headers;
using FluxGet.Engine;
using FluxGet.Engine.Http;

namespace FluxGet.Engine.Tests;

public sealed class UnitTests
{
    [Theory]
    [InlineData("22:00", "06:00", "2026-11-16 23:30", true)]
    [InlineData("22:00", "06:00", "2026-11-17 05:59", true)]
    [InlineData("22:00", "06:00", "2026-11-17 06:00", false)]
    [InlineData("22:00", "06:00", "2026-11-17 12:00", false)]
    [InlineData("01:00", "07:00", "2026-11-17 03:00", true)]
    [InlineData("01:00", "07:00", "2026-11-17 08:00", false)]
    public void Schedule_window_handles_overnight_ranges(string start, string end, string at, bool open)
    {
        var w = new ScheduleWindow(true, TimeOnly.Parse(start), TimeOnly.Parse(end), Enum.GetValues<DayOfWeek>());
        Assert.Equal(open, w.IsOpen(DateTime.Parse(at)));
    }

    [Fact]
    public void Overnight_window_uses_the_starting_day()
    {
        var w = new ScheduleWindow(true, new TimeOnly(22, 0), new TimeOnly(6, 0), [DayOfWeek.Monday]);
        Assert.True(w.IsOpen(new DateTime(2026, 11, 16, 23, 0, 0)));
        Assert.True(w.IsOpen(new DateTime(2026, 11, 17, 2, 0, 0)));
        Assert.False(w.IsOpen(new DateTime(2026, 11, 17, 23, 0, 0)));
    }

    [Theory]
    [InlineData("..\\..\\Windows\\evil.exe", "evil.exe")]
    [InlineData("a/b/c.zip", "c.zip")]
    [InlineData("CON.txt", "_CON.txt")]
    [InlineData("what?.iso", "what_.iso")]
    [InlineData("...", "download")]
    [InlineData("trailing. ", "trailing")]
    public void File_names_are_sanitised(string input, string expected) => Assert.Equal(expected, FileNames.Sanitize(input));

    [Fact]
    public void File_name_prefers_content_disposition_star()
    {
        var cd = ContentDispositionHeaderValue.Parse("attachment; filename=\"fallback.bin\"; filename*=UTF-8''r%C3%A9sum%C3%A9.pdf");
        Assert.Equal("résumé.pdf", FileNames.FromResponse(cd, new Uri("https://x.test/dl?id=1"), null));
        Assert.Equal("ubuntu.iso", FileNames.FromResponse(null, new Uri("https://x.test/path/ubuntu.iso?sig=abc"), null));
    }

    [Fact]
    public void Ensure_inside_rejects_escape()
    {
        var root = Path.Combine(Path.GetTempPath(), "root");
        Assert.StartsWith(root, FileNames.EnsureInside(root, "..\\x.txt"));
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("10.1.2.3", true)]
    [InlineData("172.20.0.1", true)]
    [InlineData("192.168.1.1", true)]
    [InlineData("169.254.169.254", true)]
    [InlineData("100.64.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("fd00::1", true)]
    [InlineData("fe80::1", true)]
    [InlineData("::ffff:10.0.0.1", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("2606:4700::1111", false)]
    public void Private_addresses_are_detected(string ip, bool isPrivate) => Assert.Equal(isPrivate, UrlGuard.IsPrivate(IPAddress.Parse(ip)));

    [Theory]
    [InlineData("ftp://x/y")]
    [InlineData("javascript:alert(1)")]
    [InlineData("https://user:pw@host/x")]
    [InlineData("not a url")]
    public void Invalid_urls_are_rejected(string url) => Assert.Throws<ArgumentException>(() => UrlGuard.Parse(url));

    [Fact]
    public void Checksums_parse_by_prefix_or_length()
    {
        Assert.Equal("md5", Checksums.Parse(new string('a', 32))!.Value.Algorithm);
        Assert.Equal("sha256", Checksums.Parse("SHA-256: " + new string('B', 64))!.Value.Algorithm);
        Assert.Null(Checksums.Parse(" "));
        Assert.Throws<ArgumentException>(() => Checksums.Parse("sha256:abc"));
    }

    [Fact]
    public void Segment_plan_respects_minimum_split_and_covers_file()
    {
        var plan = HttpRunner.Plan(10_000_001, 8, 1_000_000);
        Assert.Equal(8, plan.Count);
        Assert.Equal(0, plan[0].Start);
        Assert.Equal(10_000_000, plan[^1].End);
        for (var i = 1; i < plan.Count; i++) Assert.Equal(plan[i - 1].End + 1, plan[i].Start);
        Assert.Single(HttpRunner.Plan(1_500_000, 8, 1_000_000));
        Assert.Equal(-1, HttpRunner.Plan(null, 8, 1).Single().End);
    }

    [Fact]
    public async Task Bandwidth_limiter_throttles_to_rate()
    {
        var limiter = new BandwidthLimiter(1_000_000);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 30; i++) await limiter.WaitAsync(100_000, CancellationToken.None);
        Assert.InRange(sw.Elapsed.TotalSeconds, 2.5, 3.6);
    }
}

public sealed class ConnectorTests
{
    [Fact]
    public async Task Private_targets_are_blocked_in_hosted_mode()
    {
        await Assert.ThrowsAsync<FluxGet.Engine.BlockedAddressException>(async () => await FluxGet.Engine.Http.Connector.ConnectAsync("127.0.0.1", 80, true, CancellationToken.None));
        await Assert.ThrowsAsync<FluxGet.Engine.BlockedAddressException>(async () => await FluxGet.Engine.Http.Connector.ConnectAsync("localhost", 80, true, CancellationToken.None));
    }

    [Fact]
    public async Task Connects_to_loopback_listener()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        await using var stream = await FluxGet.Engine.Http.Connector.ConnectAsync("localhost", port, false, CancellationToken.None);
        Assert.True(stream.CanWrite);
        listener.Stop();
    }
}
