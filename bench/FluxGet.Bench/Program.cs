using System.Diagnostics;
using System.Globalization;
using System.Text;
using FluxGet.Engine;
using FluxGet.Engine.Http;
using FluxGet.Testing;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
return args.FirstOrDefault() switch
{
    "serve" => await Serve(int.Parse(args[1]), long.Parse(args[2]), long.Parse(args[3])),
    "download" => await DownloadChild(args[1], args[2], int.Parse(args[3])),
    "probe" => await Probe(long.Parse(args[1]) << 20, int.Parse(args[2]), int.Parse(args[3])),
    _ => await RunAll(args.Contains("--quick"))
};

static async Task<int> RunAll(bool quick)
{
    var report = new StringBuilder();
    var machine = $"{Environment.ProcessorCount} logical CPUs, {Environment.OSVersion.VersionString}, .NET {Environment.Version}";
    report.AppendLine($"# FluxGet benchmark results\n\nRun {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC on {machine}. Server and client on the same machine over loopback, so absolute numbers measure FluxGet's own overhead, not internet speed.\n");

    report.AppendLine("## Segment count vs. a server that limits each connection to 4 MiB/s\n");
    report.AppendLine("| Connections | Size | Time | Throughput | Peak working set |\n|---|---|---|---|---|");
    var size = quick ? 64L << 20 : 256L << 20;
    foreach (var c in new[] { 1, 2, 4, 8, 16 })
    {
        var r = await Measure(size, c, perConnection: 4L << 20);
        report.AppendLine($"| {c} | {Mb(size)} | {r.Seconds:0.0} s | {r.MbPerSec:0.0} MiB/s | {r.PeakMb:0} MB |");
        Console.WriteLine($"throttled c={c}: {r.MbPerSec:0.0} MiB/s");
    }

    report.AppendLine("\n## Segment count vs. an unthrottled loopback server\n");
    report.AppendLine("| Connections | Size | Time | Throughput | CPU (avg of all cores) | Peak working set |\n|---|---|---|---|---|---|");
    foreach (var c in new[] { 1, 4, 8, 16 })
    {
        var r = await Measure(quick ? 256L << 20 : 1L << 30, c, 0);
        report.AppendLine($"| {c} | {Mb(r.Size)} | {r.Seconds:0.0} s | {r.MbPerSec:0} MiB/s | {r.Cpu:0}% | {r.PeakMb:0} MB |");
        Console.WriteLine($"unthrottled c={c}: {r.MbPerSec:0} MiB/s");
    }

    if (!quick)
    {
        var big = 4L << 30;
        var r = await Measure(big, 8, 0, verify: true);
        report.AppendLine($"\n## Large file\n\n4 GiB file, 8 connections: {r.Seconds:0.0} s, {r.MbPerSec:0} MiB/s, peak working set {r.PeakMb:0} MB, SHA-256 {(r.Verified ? "matches" : "MISMATCH")}.");
        Console.WriteLine($"4 GiB: {r.MbPerSec:0} MiB/s, peak {r.PeakMb:0} MB, verified={r.Verified}");
    }

    var crash = await CrashRecovery(quick ? 64L << 20 : 256L << 20);
    report.AppendLine($"\n## Crash recovery\n\n{crash}");
    Console.WriteLine(crash);

    var output = Path.Combine(FindRepo(), "bench", "results.md");
    await File.WriteAllTextAsync(output, report.ToString());
    Console.WriteLine($"Wrote {output}");
    return 0;
}

static async Task<int> Probe(long size, int connections, int times)
{
    for (var i = 0; i < times; i++)
    {
        var r = await Measure(size, connections, 0);
        Console.WriteLine($"{Mb(size)} c={connections}: {r.Seconds:0.00} s {r.MbPerSec:0} MiB/s cpu {r.Cpu:0}%");
    }
    return 0;
}

static async Task<Result> Measure(long size, int connections, long perConnection, bool verify = false)
{
    await using var server = await TestFileServer.StartAsync();
    var file = server.Add("f.bin", new ServedFile { Size = size, BytesPerSecond = perConnection });
    var root = Path.Combine(Path.GetTempPath(), "fluxget-bench", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        var store = new JobStore(Path.Combine(root, "db.sqlite"));
        store.SetSetting("engine", new EngineSettings { DownloadDirectory = root, ConnectionsPerDownload = connections });
        var options = new EngineOptions { DataDirectory = root };
        await using var manager = new JobManager(store, options, [new HttpRunner(options)]);
        manager.Start();
        GC.Collect();
        var proc = Process.GetCurrentProcess();
        var cpu0 = proc.TotalProcessorTime;
        long peak = 0;
        var sw = Stopwatch.StartNew();
        var job = manager.Add(new AddJobRequest { Url = server.Url("f.bin"), Connections = connections });
        while (true)
        {
            await Task.Delay(100);
            proc.Refresh();
            peak = Math.Max(peak, proc.WorkingSet64);
            var j = manager.Find(job.Id)!;
            if (j.Status == JobStatus.Completed) break;
            if (j.Status is JobStatus.Failed or JobStatus.NeedsAttention) throw new Exception(j.Error);
        }
        sw.Stop();
        proc.Refresh();
        var cpu = (proc.TotalProcessorTime - cpu0).TotalSeconds / sw.Elapsed.TotalSeconds / Environment.ProcessorCount * 100;
        var done = manager.Find(job.Id)!;
        var verified = !verify || Sha256(done.FilePath!) == TestFileServer.Sha256(file);
        return new Result(size, sw.Elapsed.TotalSeconds, size / 1048576.0 / sw.Elapsed.TotalSeconds, peak / 1048576.0, cpu, verified);
    }
    finally
    {
        try { Directory.Delete(root, true); } catch (IOException) { }
    }
}

static async Task<string> CrashRecovery(long size)
{
    var port = 18900 + Random.Shared.Next(100);
    var root = Path.Combine(Path.GetTempPath(), "fluxget-bench", "crash-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    var self = Environment.ProcessPath!;
    var dll = typeof(Result).Assembly.Location;
    Process Start(params string[] a)
    {
        var psi = new ProcessStartInfo(self) { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
        if (!self.EndsWith("FluxGet.Bench.exe", StringComparison.OrdinalIgnoreCase)) psi.ArgumentList.Add(dll);
        foreach (var x in a) psi.ArgumentList.Add(x);
        return Process.Start(psi)!;
    }
    using var server = Start("serve", port.ToString(), size.ToString(), (2L << 20).ToString());
    await server.StandardOutput.ReadLineAsync();
    var url = $"http://127.0.0.1:{port}/f/f.bin";
    try
    {
        var first = Start("download", url, root, "8");
        long before = 0;
        while (true)
        {
            var line = await first.StandardOutput.ReadLineAsync();
            if (line is null) break;
            if (long.TryParse(line, out var b) && b > size * 4 / 10)
            {
                before = b;
                first.Kill(entireProcessTree: true);
                break;
            }
        }
        await first.WaitForExitAsync();
        var sw = Stopwatch.StartNew();
        var second = Start("download", url, root, "8");
        long resumedFrom = -1;
        double firstProgressMs = 0;
        string? last = null;
        while (await second.StandardOutput.ReadLineAsync() is { } line)
        {
            if (resumedFrom < 0 && long.TryParse(line, out var b) && b > 0)
            {
                resumedFrom = b;
                firstProgressMs = sw.Elapsed.TotalMilliseconds;
            }
            last = line;
        }
        await second.WaitForExitAsync();
        var hash = last?.StartsWith("DONE ") == true ? last[5..] : "";
        var expected = TestFileServer.Sha256(new ServedFile { Size = size });
        return $"{Mb(size)} file at 2 MiB/s per connection, 8 connections, checkpoint every 2 s. Process force-killed (no shutdown) after {Mb(before)}; " +
               $"restarted process resumed with {Mb(resumedFrom)} already on disk after {firstProgressMs:0} ms and finished with SHA-256 {(hash == expected ? "matching the source" : "NOT matching")}.";
    }
    finally
    {
        try { server.Kill(entireProcessTree: true); } catch (Exception) { }
        try { Directory.Delete(root, true); } catch (IOException) { }
    }
}

static async Task<int> Serve(int port, long size, long perConnection)
{
    await using var server = await TestFileServer.StartAsync(port);
    server.Add("f.bin", new ServedFile { Size = size, BytesPerSecond = perConnection });
    Console.WriteLine("ready");
    await Task.Delay(Timeout.Infinite);
    return 0;
}

static async Task<int> DownloadChild(string url, string root, int connections)
{
    var store = new JobStore(Path.Combine(root, "db.sqlite"));
    if (store.GetSetting<EngineSettings>("engine") is null)
        store.SetSetting("engine", new EngineSettings { DownloadDirectory = root, ConnectionsPerDownload = connections });
    var options = new EngineOptions { DataDirectory = root };
    await using var manager = new JobManager(store, options, [new HttpRunner(options)]);
    manager.Start();
    var job = manager.Unfinished().FirstOrDefault() ?? manager.Add(new AddJobRequest { Url = url });
    while (true)
    {
        var j = manager.Find(job.Id)!;
        Console.WriteLine(Interlocked.Read(ref j.ReceivedBytes));
        if (j.Status == JobStatus.Completed)
        {
            Console.WriteLine("DONE " + Sha256(j.FilePath!));
            return 0;
        }
        if (j.Status is JobStatus.Failed or JobStatus.NeedsAttention)
        {
            Console.WriteLine("ERROR " + j.Error);
            return 1;
        }
        await Task.Delay(100);
    }
}

static string Sha256(string path)
{
    using var fs = File.OpenRead(path);
    return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(fs));
}

static string Mb(long b) => b >= 1L << 30 ? $"{b / (double)(1L << 30):0.#} GiB" : $"{b / 1048576.0:0} MiB";

static string FindRepo()
{
    for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        if (File.Exists(Path.Combine(d.FullName, "FluxGet.slnx"))) return d.FullName;
    return Directory.GetCurrentDirectory();
}

record Result(long Size, double Seconds, double MbPerSec, double PeakMb, double Cpu, bool Verified);
