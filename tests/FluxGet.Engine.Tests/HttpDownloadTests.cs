using System.Diagnostics;
using FluxGet.Engine;
using FluxGet.Testing;

namespace FluxGet.Engine.Tests;

public sealed class HttpDownloadTests : IAsyncLifetime
{
    private TestFileServer _server = null!;
    private EngineHarness _h = null!;

    public async Task InitializeAsync()
    {
        _server = await TestFileServer.StartAsync();
        _h = await EngineHarness.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _h.DisposeAsync();
        await _server.DisposeAsync();
    }

    private Job Add(string name, ServedFile f, Func<AddJobRequest, AddJobRequest>? configure = null)
    {
        _server.Add(name, f);
        var req = new AddJobRequest { Url = _server.Url(name) };
        return _h.Manager.Add(configure?.Invoke(req) ?? req);
    }

    [Fact]
    public async Task Small_file_downloads_with_correct_name_and_content()
    {
        var f = new ServedFile { Size = 10_000, Disposition = "attachment; filename=\"report 2026.pdf\"" };
        var job = Add("x", f);
        job = await _h.WaitForStatus(job.Id, JobStatus.Completed);
        Assert.Equal(Path.Combine(_h.Downloads, "report 2026.pdf"), job.FilePath);
        Assert.Equal(TestFileServer.Sha256(f), EngineHarness.Sha256(job.FilePath!));
        Assert.Empty(Directory.GetFiles(_h.Downloads, "*.fgpart"));
    }

    [Fact]
    public async Task Large_file_uses_parallel_ranges_and_matches_source()
    {
        var f = new ServedFile { Size = 24L * 1024 * 1024 + 123, BytesPerSecond = 16L * 1024 * 1024 };
        var job = Add("big.bin", f, r => r with { Connections = 6 });
        job = await _h.WaitForStatus(job.Id, JobStatus.Completed, 30000);
        Assert.True(f.MaxConcurrent >= 4, $"max concurrent {f.MaxConcurrent}");
        Assert.True(job.Resumable);
        Assert.Equal(TestFileServer.Sha256(f), EngineHarness.Sha256(job.FilePath!));
    }

    [Fact]
    public async Task Server_without_ranges_falls_back_to_single_connection()
    {
        var f = new ServedFile { Size = 3 * 1024 * 1024, Ranges = false };
        var job = Add("noranges.bin", f);
        job = await _h.WaitForStatus(job.Id, JobStatus.Completed);
        Assert.False(job.Resumable);
        Assert.Equal(1, f.MaxConcurrent);
        Assert.Equal(1, f.Requests);
        Assert.Equal(TestFileServer.Sha256(f), EngineHarness.Sha256(job.FilePath!));
    }

    [Fact]
    public async Task Wrong_content_range_falls_back_and_never_corrupts()
    {
        var f = new ServedFile { Size = 4 * 1024 * 1024, WrongRangeStart = true };
        var job = Add("badrange.bin", f);
        job = await _h.WaitForStatus(job.Id, JobStatus.Completed);
        Assert.False(job.Resumable);
        Assert.Equal(TestFileServer.Sha256(f), EngineHarness.Sha256(job.FilePath!));
    }

    [Fact]
    public async Task Unknown_length_stream_downloads()
    {
        var f = new ServedFile { Size = 700_000, Ranges = false, NoLength = true };
        var job = Add("stream.bin", f);
        job = await _h.WaitForStatus(job.Id, JobStatus.Completed);
        Assert.Equal(f.Size, job.TotalBytes);
        Assert.Equal(TestFileServer.Sha256(f), EngineHarness.Sha256(job.FilePath!));
    }

    [Fact]
    public async Task Pause_and_resume_continues_from_persisted_offsets()
    {
        var f = new ServedFile { Size = 8L * 1024 * 1024, BytesPerSecond = 2L * 1024 * 1024 };
        var job = Add("pause.bin", f, r => r with { Connections = 4 });
        await _h.WaitFor(job.Id, j => j.ReceivedBytes > 2 * 1024 * 1024);
        _h.Manager.Pause(job.Id);
        job = await _h.WaitForStatus(job.Id, JobStatus.Paused);
        var persisted = _h.Store.LoadUnfinished().Single(j => j.Id == job.Id);
        Assert.True(persisted.ReceivedBytes > 2 * 1024 * 1024);
        Assert.Equal(persisted.ReceivedBytes, persisted.Segments.Sum(s => s.Received));
        var before = f.RangeHeaders.Count;

        f.BytesPerSecond = 0;
        _h.Manager.Resume(job.Id);
        job = await _h.WaitForStatus(job.Id, JobStatus.Completed);
        var resumedRanges = f.RangeHeaders.Skip(before).Select(r => long.Parse(r[6..].Split('-')[0])).ToList();
        Assert.Contains(resumedRanges, start => start > 0 && start % (2 * 1024 * 1024) != 0);
        Assert.Equal(TestFileServer.Sha256(f), EngineHarness.Sha256(job.FilePath!));
    }

    [Fact]
    public async Task Interrupted_download_recovers_after_restart()
    {
        var f = new ServedFile { Size = 8L * 1024 * 1024, BytesPerSecond = 2L * 1024 * 1024 };
        var job = Add("crash.bin", f, r => r with { Connections = 4 });
        await _h.WaitFor(job.Id, j => j.ReceivedBytes > 3 * 1024 * 1024);
        await _h.RestartAsync();
        var recovered = _h.Manager.Find(job.Id)!;
        Assert.True(recovered.ReceivedBytes > 2 * 1024 * 1024, $"recovered {recovered.ReceivedBytes}");
        f.BytesPerSecond = 0;
        job = await _h.WaitForStatus(job.Id, JobStatus.Completed);
        Assert.Equal(TestFileServer.Sha256(f), EngineHarness.Sha256(job.FilePath!));
    }

    [Fact]
    public async Task Changed_remote_file_needs_attention_instead_of_mixing_versions()
    {
        var f = new ServedFile { Size = 6L * 1024 * 1024, BytesPerSecond = 2L * 1024 * 1024 };
        var job = Add("changed.bin", f);
        await _h.WaitFor(job.Id, j => j.ReceivedBytes > 1024 * 1024);
        _h.Manager.Pause(job.Id);
        await _h.WaitForStatus(job.Id, JobStatus.Paused);
        f.ETag = "\"v2\"";
        f.Seed = 2;
        f.BytesPerSecond = 0;
        _h.Manager.Resume(job.Id);
        job = await _h.WaitForStatus(job.Id, JobStatus.NeedsAttention);
        Assert.Contains("changed", job.Error);

        await _h.Manager.RestartAsync(job.Id);
        job = await _h.WaitForStatus(job.Id, JobStatus.Completed);
        Assert.Equal(TestFileServer.Sha256(f), EngineHarness.Sha256(job.FilePath!));
    }

    [Fact]
    public async Task Expired_link_can_be_refreshed_keeping_partial_data()
    {
        var f = new ServedFile { Size = 6L * 1024 * 1024, BytesPerSecond = 2L * 1024 * 1024 };
        var job = Add("signed.bin", f);
        await _h.WaitFor(job.Id, j => j.ReceivedBytes > 1024 * 1024);
        _h.Manager.Pause(job.Id);
        job = await _h.WaitForStatus(job.Id, JobStatus.Paused);
        var kept = job.ReceivedBytes;

        f.ForceStatus = 403;
        _h.Manager.Resume(job.Id);
        job = await _h.WaitForStatus(job.Id, JobStatus.NeedsAttention);
        Assert.Contains("expired", job.Error);

        var fresh = _server.Add("signed-new.bin", new ServedFile { Size = f.Size, ETag = f.ETag, LastModified = f.LastModified });
        await _h.Manager.RefreshUrlAsync(job.Id, _server.Url("signed-new.bin"));
        job = await _h.WaitForStatus(job.Id, JobStatus.Completed);
        var firstStart = long.Parse(fresh.RangeHeaders.Skip(1).Select(r => r[6..].Split('-')[0]).First());
        Assert.True(firstStart > 0);
        Assert.True(fresh.RangeHeaders.Count > 1);
        Assert.Equal(TestFileServer.Sha256(f), EngineHarness.Sha256(job.FilePath!));
        Assert.True(kept > 0);
    }

    [Fact]
    public async Task Dropped_connections_are_retried()
    {
        var f = new ServedFile { Size = 3 * 1024 * 1024, DropAfterBytes = 300_000, DropCount = 3 };
        var job = Add("flaky.bin", f, r => r with { Connections = 2 });
        job = await _h.WaitForStatus(job.Id, JobStatus.Completed);
        Assert.True(f.Requests >= 5);
        Assert.Equal(TestFileServer.Sha256(f), EngineHarness.Sha256(job.FilePath!));
    }

    [Fact]
    public async Task Connection_count_adapts_when_server_limits_concurrency()
    {
        var f = new ServedFile { Size = 12L * 1024 * 1024, ConcurrencyLimit = 3, BytesPerSecond = 8L * 1024 * 1024 };
        var job = Add("limited-conn.bin", f, r => r with { Connections = 8 });
        job = await _h.WaitForStatus(job.Id, JobStatus.Completed, 30000);
        Assert.True(f.Throttled > 0);
        Assert.Equal(TestFileServer.Sha256(f), EngineHarness.Sha256(job.FilePath!));
    }

    [Fact]
    public async Task Persistent_server_errors_fail_after_bounded_attempts()
    {
        var f = new ServedFile { Size = 1000, ForceStatus = 503 };
        var job = Add("down.bin", f);
        job = await _h.WaitForStatus(job.Id, JobStatus.Failed);
        Assert.Contains("Gave up after 4 attempts", job.Error);
        Assert.Equal(4, f.Requests);
    }

    [Fact]
    public async Task Missing_file_fails_immediately()
    {
        var job = _h.Manager.Add(new AddJobRequest { Url = _server.Url("nope") });
        job = await _h.WaitForStatus(job.Id, JobStatus.Failed);
        Assert.Contains("404", job.Error);
    }

    [Fact]
    public async Task Checksum_match_completes_and_mismatch_needs_attention()
    {
        var good = new ServedFile { Size = 500_000 };
        var ok = Add("sum-ok.bin", good, r => r with { Checksum = "sha256:" + TestFileServer.Sha256(good) });
        ok = await _h.WaitForStatus(ok.Id, JobStatus.Completed);
        Assert.NotNull(ok.FilePath);

        var bad = Add("sum-bad.bin", new ServedFile { Size = 500_000 }, r => r with { Checksum = new string('a', 64) });
        bad = await _h.WaitForStatus(bad.Id, JobStatus.NeedsAttention);
        Assert.Contains("Checksum mismatch", bad.Error);
        Assert.False(File.Exists(Path.Combine(_h.Downloads, "sum-bad.bin")));
    }

    [Fact]
    public async Task Per_download_speed_limit_is_enforced()
    {
        var f = new ServedFile { Size = 1536 * 1024 };
        var sw = Stopwatch.StartNew();
        var job = Add("limited.bin", f, r => r with { SpeedLimit = 512 * 1024 });
        await _h.WaitForStatus(job.Id, JobStatus.Completed);
        sw.Stop();
        Assert.InRange(sw.Elapsed.TotalSeconds, 2.5, 4.5);
    }

    [Fact]
    public async Task Global_speed_limit_applies_across_downloads()
    {
        _h.Manager.UpdateSettings(_h.Manager.Settings with { GlobalSpeedLimit = 1024 * 1024 });
        var sw = Stopwatch.StartNew();
        var a = Add("g1.bin", new ServedFile { Size = 1536 * 1024 });
        var b = Add("g2.bin", new ServedFile { Size = 1536 * 1024 });
        await _h.WaitForStatus(a.Id, JobStatus.Completed);
        await _h.WaitForStatus(b.Id, JobStatus.Completed);
        Assert.InRange(sw.Elapsed.TotalSeconds, 2.5, 4.5);
    }

    [Fact]
    public async Task Queue_respects_concurrency_and_priority()
    {
        _h.Manager.UpdateSettings(_h.Manager.Settings with { MaxConcurrentJobs = 1 });
        var slow = new ServedFile { Size = 1024 * 1024, BytesPerSecond = 2 * 1024 * 1024 };
        var first = Add("q1.bin", slow);
        var low = Add("q2.bin", new ServedFile { Size = 1000 });
        var high = Add("q3.bin", new ServedFile { Size = 1000 }, r => r with { Priority = 5 });
        Assert.Equal(JobStatus.Queued, _h.Manager.Find(low.Id)!.Status);
        var running = 0;
        _h.Manager.JobChanged += _ => running = Math.Max(running, _h.Manager.Unfinished().Count(j => j.Status == JobStatus.Running));
        await _h.WaitForStatus(low.Id, JobStatus.Completed);
        DateTime Done(Job j) => _h.Manager.Find(j.Id)!.CompletedAt!.Value;
        Assert.True(Done(first) <= Done(high));
        Assert.True(Done(high) <= Done(low));
        Assert.True(running <= 1);
    }

    [Fact]
    public async Task Duplicate_address_is_rejected_unless_allowed()
    {
        var f = new ServedFile { Size = 1000, BytesPerSecond = 100 };
        Add("dup.bin", f);
        Assert.Throws<DuplicateJobException>(() => _h.Manager.Add(new AddJobRequest { Url = _server.Url("dup.bin") }));
        var again = _h.Manager.Add(new AddJobRequest { Url = _server.Url("dup.bin"), AllowDuplicate = true });
        Assert.NotNull(again);
    }

    [Fact]
    public async Task Existing_file_is_never_overwritten()
    {
        File.WriteAllText(Path.Combine(_h.Downloads, "keep.bin"), "mine");
        var job = Add("keep.bin", new ServedFile { Size = 2000 });
        job = await _h.WaitForStatus(job.Id, JobStatus.Completed);
        Assert.Equal(Path.Combine(_h.Downloads, "keep (1).bin"), job.FilePath);
        Assert.Equal("mine", File.ReadAllText(Path.Combine(_h.Downloads, "keep.bin")));
    }

    [Fact]
    public async Task Scheduled_jobs_wait_for_the_window()
    {
        var now = DateTime.Now;
        var closed = new ScheduleWindow(true, TimeOnly.FromDateTime(now.AddHours(2)), TimeOnly.FromDateTime(now.AddHours(3)), Enum.GetValues<DayOfWeek>());
        _h.Manager.UpdateSettings(_h.Manager.Settings with { Schedule = closed });
        var job = Add("sched.bin", new ServedFile { Size = 1000 }, r => r with { UseSchedule = true });
        await _h.WaitForStatus(job.Id, JobStatus.Scheduled);
        _h.Manager.UpdateSettings(_h.Manager.Settings with { Schedule = closed with { Start = TimeOnly.FromDateTime(now.AddMinutes(-1)) } });
        await _h.WaitForStatus(job.Id, JobStatus.Completed);
    }
}
