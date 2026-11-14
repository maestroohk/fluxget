using FluxGet.Engine;
using FluxGet.Engine.Http;

namespace FluxGet.Engine.Tests;

public sealed class EngineHarness : IAsyncDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "fluxget-tests", Guid.NewGuid().ToString("N"));
    public string Downloads => Path.Combine(Root, "downloads");
    public string DbPath => Path.Combine(Root, "fluxget.db");
    public JobManager Manager { get; private set; } = null!;
    public JobStore Store { get; private set; } = null!;

    public static EngineOptions FastOptions(string root) => new()
    {
        DataDirectory = root,
        CheckpointInterval = TimeSpan.FromMilliseconds(200),
        RetryBaseDelay = TimeSpan.FromMilliseconds(50),
        RetryMaxDelay = TimeSpan.FromMilliseconds(200),
        MaxAttempts = 4,
        StallTimeout = TimeSpan.FromSeconds(5),
        MinSplitSize = 256 * 1024
    };

    public static async Task<EngineHarness> StartAsync(Action<EngineHarness>? beforeStart = null, EngineSettings? settings = null)
    {
        var h = new EngineHarness();
        h.Open(settings, beforeStart);
        return await Task.FromResult(h);
    }

    private void Open(EngineSettings? settings, Action<EngineHarness>? beforeStart)
    {
        Directory.CreateDirectory(Downloads);
        Store = new JobStore(DbPath);
        Store.SetSetting("engine", (settings ?? new EngineSettings()) with { DownloadDirectory = Downloads });
        beforeStart?.Invoke(this);
        Manager = new JobManager(Store, FastOptions(Root), [new HttpRunner(FastOptions(Root))]);
        Manager.Start();
    }

    public async Task RestartAsync(bool graceful = true)
    {
        if (graceful) await Manager.DisposeAsync();
        Open(Store.GetSetting<EngineSettings>("engine"), null);
    }

    public async Task<Job> WaitFor(long id, Func<Job, bool> predicate, int timeoutMs = 20000)
    {
        var until = Environment.TickCount64 + timeoutMs;
        while (true)
        {
            var job = Manager.Find(id) ?? throw new InvalidOperationException("job vanished");
            if (predicate(job)) return job;
            if (Environment.TickCount64 > until)
                throw new TimeoutException($"Job {id} stuck in {job.Status} ({job.Error ?? job.Phase}) with {job.ReceivedBytes}/{job.TotalBytes} bytes");
            await Task.Delay(25);
        }
    }

    public Task<Job> WaitForStatus(long id, JobStatus status, int timeoutMs = 20000) => WaitFor(id, j => j.Status == status, timeoutMs);

    public static string Sha256(string path)
    {
        using var fs = File.OpenRead(path);
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(fs));
    }

    public async ValueTask DisposeAsync()
    {
        await Manager.DisposeAsync();
        try { Directory.Delete(Root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
