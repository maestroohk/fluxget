using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FluxGet.Engine;

public interface IJobRunner
{
    JobKind Kind { get; }
    Task RunAsync(JobContext context, CancellationToken ct);
    Task CleanupAsync(Job job, bool deleteFiles);
    void Reset(Job job) { }
}

public sealed class JobContext(JobManager manager, Job job, BandwidthLimiter jobLimiter)
{
    public Job Job { get; } = job;
    public JobManager Manager { get; } = manager;
    public EngineOptions Options => Manager.Options;
    public EngineSettings Settings => Manager.Settings;
    public BandwidthLimiter GlobalLimiter => Manager.GlobalLimiter;
    public BandwidthLimiter JobLimiter { get; } = jobLimiter;
    public JobStore Store => Manager.Store;

    public void SetStatus(JobStatus status, string? phase = null) => Manager.SetRunState(Job, status, phase);
    public void Save() => Manager.Store.Update(Job);
}

public sealed record AddJobRequest
{
    public JobKind Kind { get; init; } = JobKind.Http;
    public required string Url { get; init; }
    public string? FileName { get; init; }
    public string? Directory { get; init; }
    public string? Group { get; init; }
    public int? Connections { get; init; }
    public long SpeedLimit { get; init; }
    public string? Checksum { get; init; }
    public bool UseSchedule { get; init; }
    public int Priority { get; init; }
    public bool StartPaused { get; init; }
    public bool AllowDuplicate { get; init; }
    public string? Options { get; init; }
    public IReadOnlyDictionary<string, string>? Headers { get; init; }
}

public sealed class DuplicateJobException(Job existing) : Exception("This address is already in the list.")
{
    public Job Existing { get; } = existing;
}

public sealed class JobManager : IAsyncDisposable
{
    private sealed class Run
    {
        public required CancellationTokenSource Cts { get; init; }
        public required JobContext Context { get; init; }
        public Task Task { get; set; } = Task.CompletedTask;
        public JobStatus? StopIntent { get; set; }
    }

    private readonly object _gate = new();
    private readonly Dictionary<long, Job> _jobs = new();
    private readonly Dictionary<long, Run> _runs = new();
    private readonly Dictionary<long, Queue<(long Ticks, long Bytes)>> _samples = new();
    private readonly Dictionary<JobKind, IJobRunner> _runners;
    private readonly ILogger _log;
    private Timer? _timer;
    private EngineSettings _settings;
    private bool _stopping;

    public JobManager(JobStore store, EngineOptions options, IEnumerable<IJobRunner> runners, ILogger<JobManager>? log = null)
    {
        Store = store;
        Options = options;
        _log = log ?? NullLogger<JobManager>.Instance;
        _runners = runners.ToDictionary(r => r.Kind);
        _settings = (store.GetSetting<EngineSettings>("engine") ?? new EngineSettings()).Normalize();
        GlobalLimiter = new BandwidthLimiter(_settings.GlobalSpeedLimit);
    }

    public JobStore Store { get; }
    public EngineOptions Options { get; }
    public BandwidthLimiter GlobalLimiter { get; }
    public EngineSettings Settings => _settings;

    public event Action<Job>? JobChanged;
    public event Action<IReadOnlyList<Job>>? Tick;
    public event Action<EngineSettings>? SettingsChanged;

    public void Start()
    {
        foreach (var job in Store.LoadUnfinished())
        {
            if (job.Status.IsActive() || job.Status == JobStatus.Seeding)
            {
                job.Status = JobStatus.Queued;
                Store.Update(job);
            }
            foreach (var s in job.Segments) s.Next = s.Start + s.Received;
            lock (_gate) _jobs[job.Id] = job;
        }
        _timer = new Timer(_ => OnTimer(), null, TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(500));
        Pump();
    }

    public IReadOnlyList<Job> Unfinished()
    {
        lock (_gate) return _jobs.Values.OrderBy(j => j.Id).ToList();
    }

    public Job? Find(long id)
    {
        lock (_gate)
            if (_jobs.TryGetValue(id, out var j)) return j;
        return Store.Get(id);
    }

    public Job Add(AddJobRequest req)
    {
        if (!_runners.ContainsKey(req.Kind)) throw new ArgumentException($"{req.Kind} downloads are not available.");
        var url = req.Kind == JobKind.Torrent ? req.Url.Trim() : UrlGuard.Parse(req.Url).ToString();
        var checksum = Checksums.Parse(req.Checksum);
        if (!req.AllowDuplicate)
        {
            lock (_gate)
            {
                var dup = _jobs.Values.FirstOrDefault(j => j.Kind == req.Kind && j.Url == url && j.Options == req.Options);
                if (dup is not null) throw new DuplicateJobException(dup);
            }
        }
        var directory = ResolveDirectory(req.Directory);
        System.IO.Directory.CreateDirectory(directory);
        var job = new Job
        {
            Kind = req.Kind,
            Url = url,
            FileName = string.IsNullOrWhiteSpace(req.FileName) ? null : FileNames.Sanitize(req.FileName),
            Directory = directory,
            Group = req.Group,
            Connections = Math.Clamp(req.Connections ?? 0, 0, 32),
            SpeedLimit = Math.Max(0, req.SpeedLimit),
            Checksum = checksum is { } c ? $"{c.Algorithm}:{c.Hex}" : null,
            UseSchedule = req.UseSchedule,
            Priority = Math.Clamp(req.Priority, -10, 10),
            Options = req.Options,
            Status = req.StartPaused ? JobStatus.Paused : JobStatus.Queued,
            RequestHeaders = req.Headers
        };
        Store.Insert(job);
        lock (_gate) _jobs[job.Id] = job;
        Raise(job);
        Pump();
        return job;
    }

    public string ResolveDirectory(string? requested)
    {
        var root = _settings.DownloadDirectory;
        if (string.IsNullOrWhiteSpace(requested)) return root;
        var full = Path.GetFullPath(requested);
        if (Options.RestrictToDownloadDirectory)
        {
            var r = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            if (!full.Equals(r, StringComparison.OrdinalIgnoreCase) && !full.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Downloads must be saved inside the configured download folder.");
        }
        return full;
    }

    public void Pause(long id)
    {
        lock (_gate)
        {
            if (!_jobs.TryGetValue(id, out var job)) throw new KeyNotFoundException();
            if (_runs.TryGetValue(id, out var run))
            {
                run.StopIntent = JobStatus.Paused;
                run.Cts.Cancel();
                return;
            }
            if (job.Status is JobStatus.Queued or JobStatus.Scheduled)
                SetStatus(job, JobStatus.Paused, null);
        }
    }

    public void Resume(long id)
    {
        lock (_gate)
        {
            if (!_jobs.TryGetValue(id, out var job)) throw new KeyNotFoundException();
            if (_runs.ContainsKey(id)) return;
            if (job.Status is JobStatus.Paused or JobStatus.Failed or JobStatus.NeedsAttention)
                SetStatus(job, JobStatus.Queued, null);
        }
        Pump();
    }

    public async Task RestartAsync(long id)
    {
        var job = await StopAsync(id, JobStatus.Paused) ?? throw new KeyNotFoundException();
        if (_runners.TryGetValue(job.Kind, out var runner))
        {
            await runner.CleanupAsync(job, deleteFiles: true);
            runner.Reset(job);
        }
        lock (_gate)
        {
            job.Segments.Clear();
            Interlocked.Exchange(ref job.ReceivedBytes, 0);
            job.ETag = job.LastModified = null;
            job.TotalBytes = null;
            job.Resumable = true;
            Store.SaveProgress(job, [], 0);
            SetStatus(job, JobStatus.Queued, null);
        }
        Pump();
    }

    public async Task RefreshUrlAsync(long id, string url)
    {
        var job = await StopAsync(id, JobStatus.Paused) ?? throw new KeyNotFoundException();
        if (job.Kind != JobKind.Http) throw new ArgumentException("Only direct downloads can change their address.");
        lock (_gate)
        {
            job.Url = UrlGuard.Parse(url).ToString();
            SetStatus(job, JobStatus.Queued, null);
        }
        Pump();
    }

    public void Update(long id, int? priority, long? speedLimit, bool? useSchedule)
    {
        lock (_gate)
        {
            if (!_jobs.TryGetValue(id, out var job)) throw new KeyNotFoundException();
            if (priority is { } p) job.Priority = Math.Clamp(p, -10, 10);
            if (speedLimit is { } l)
            {
                job.SpeedLimit = Math.Max(0, l);
                if (_runs.TryGetValue(id, out var run)) run.Context.JobLimiter.Rate = job.SpeedLimit;
            }
            if (useSchedule is { } u) job.UseSchedule = u;
            Store.Update(job);
            Raise(job);
        }
        Pump();
    }

    public async Task RemoveAsync(long id, bool deleteFiles)
    {
        var job = await StopAsync(id, JobStatus.Paused) ?? Store.Get(id) ?? throw new KeyNotFoundException();
        lock (_gate) _jobs.Remove(id);
        if (_runners.TryGetValue(job.Kind, out var runner)) await runner.CleanupAsync(job, deleteFiles);
        if (deleteFiles && job.Status == JobStatus.Completed && job.FilePath is { } path && File.Exists(path))
        {
            try { File.Delete(path); } catch (IOException e) { _log.LogWarning(e, "Could not delete {Path}", path); }
        }
        Store.Delete(id);
        job.Status = JobStatus.Completed;
        JobChanged?.Invoke(job);
    }

    private async Task<Job?> StopAsync(long id, JobStatus intent)
    {
        Run? run;
        Job? job;
        lock (_gate)
        {
            _jobs.TryGetValue(id, out job);
            if (_runs.TryGetValue(id, out run))
            {
                run.StopIntent = intent;
                run.Cts.Cancel();
            }
        }
        if (run is not null) await run.Task;
        return job;
    }

    public void UpdateSettings(EngineSettings settings)
    {
        settings = settings.Normalize();
        System.IO.Directory.CreateDirectory(settings.DownloadDirectory);
        Store.SetSetting("engine", settings);
        _settings = settings;
        GlobalLimiter.Rate = settings.GlobalSpeedLimit;
        SettingsChanged?.Invoke(settings);
        Pump();
    }

    internal void SetRunState(Job job, JobStatus status, string? phase)
    {
        lock (_gate)
        {
            if (!_runs.ContainsKey(job.Id)) return;
            var changed = job.Status != status || job.Phase != phase;
            job.Status = status;
            job.Phase = phase;
            if (changed) Raise(job);
        }
        if (status == JobStatus.Seeding) Pump();
    }

    private void SetStatus(Job job, JobStatus status, string? error)
    {
        job.Status = status;
        job.Error = error;
        job.Phase = null;
        Store.Update(job);
        Raise(job);
    }

    private void Raise(Job job)
    {
        try { JobChanged?.Invoke(job); }
        catch (Exception e) { _log.LogError(e, "JobChanged handler failed"); }
    }

    public void Pump()
    {
        lock (_gate)
        {
            if (_stopping) return;
            var open = _settings.Schedule.IsOpen(DateTime.Now);
            foreach (var job in _jobs.Values)
            {
                if (job.Status == JobStatus.Queued && job.UseSchedule && !open) SetStatus(job, JobStatus.Scheduled, null);
                else if (job.Status == JobStatus.Scheduled && (open || !job.UseSchedule)) SetStatus(job, JobStatus.Queued, null);
            }
            foreach (var (id, run) in _runs)
            {
                var job = _jobs[id];
                if (job.UseSchedule && !open && run.StopIntent is null && job.Status != JobStatus.Seeding)
                {
                    run.StopIntent = JobStatus.Scheduled;
                    run.Cts.Cancel();
                }
            }
            var busy = _runs.Count(r => _jobs[r.Key].Status != JobStatus.Seeding);
            foreach (var job in _jobs.Values.Where(j => j.Status == JobStatus.Queued).OrderByDescending(j => j.Priority).ThenBy(j => j.Id).ToList())
            {
                if (busy >= _settings.MaxConcurrentJobs) break;
                StartRun(job);
                busy++;
            }
        }
    }

    private void StartRun(Job job)
    {
        var runner = _runners[job.Kind];
        var run = new Run { Cts = new CancellationTokenSource(), Context = new JobContext(this, job, new BandwidthLimiter(job.SpeedLimit)) };
        _runs[job.Id] = run;
        job.Error = null;
        job.Phase = null;
        job.Status = JobStatus.Running;
        Store.Update(job);
        Raise(job);
        run.Task = Task.Run(async () =>
        {
            JobStatus final;
            string? error = null;
            try
            {
                await runner.RunAsync(run.Context, run.Cts.Token);
                final = JobStatus.Completed;
            }
            catch (OperationCanceledException) when (run.Cts.IsCancellationRequested)
            {
                final = run.StopIntent ?? JobStatus.Paused;
            }
            catch (JobAttentionException e)
            {
                final = JobStatus.NeedsAttention;
                error = e.Message;
            }
            catch (Exception e)
            {
                final = JobStatus.Failed;
                error = Describe(e);
                _log.LogWarning("Job {Id} failed: {Error}", job.Id, error);
            }
            Finish(job, run, final, error);
        });
    }

    private void Finish(Job job, Run run, JobStatus final, string? error)
    {
        lock (_gate)
        {
            _runs.Remove(job.Id);
            _samples.Remove(job.Id);
            run.Cts.Dispose();
            job.Speed = 0;
            job.UploadSpeed = 0;
            job.ActiveConnections = 0;
            if (final == JobStatus.Completed) job.CompletedAt = DateTime.UtcNow;
            if (!_jobs.ContainsKey(job.Id)) return;
            SetStatus(job, final, error);
            if (final == JobStatus.Completed) _jobs.Remove(job.Id);
        }
        Pump();
    }

    internal static string Describe(Exception e) => e switch
    {
        JobFailedException or ArgumentException => e.Message,
        HttpRequestException { StatusCode: { } code } => $"Server returned {(int)code} {code}.",
        HttpRequestException h when h.InnerException is BlockedAddressException b => b.Message,
        HttpRequestException h => "Network error: " + (h.InnerException?.Message ?? h.Message),
        UnauthorizedAccessException => "Access to the download folder was denied.",
        IOException io => "File error: " + io.Message,
        TimeoutException => "The server stopped responding.",
        _ => e.Message
    };

    private void OnTimer()
    {
        List<Job> active;
        lock (_gate)
        {
            var now = Environment.TickCount64;
            foreach (var id in _runs.Keys)
            {
                var job = _jobs[id];
                var bytes = Interlocked.Read(ref job.ReceivedBytes);
                if (!_samples.TryGetValue(id, out var q)) _samples[id] = q = new Queue<(long, long)>();
                q.Enqueue((now, bytes));
                while (q.Count > 1 && now - q.Peek().Ticks > 3000) q.Dequeue();
                var (t0, b0) = q.Peek();
                job.Speed = now > t0 ? Math.Max(0, (bytes - b0) * 1000.0 / (now - t0)) : 0;
            }
            active = _runs.Keys.Select(id => _jobs[id]).ToList();
        }
        if (DateTime.Now.Second % 15 == 0) Pump();
        if (active.Count > 0)
        {
            try { Tick?.Invoke(active); }
            catch (Exception e) { _log.LogError(e, "Tick handler failed"); }
        }
    }

    public async ValueTask DisposeAsync()
    {
        List<Task> tasks;
        lock (_gate)
        {
            _stopping = true;
            foreach (var run in _runs.Values)
            {
                run.StopIntent = JobStatus.Queued;
                run.Cts.Cancel();
            }
            tasks = _runs.Values.Select(r => r.Task).ToList();
        }
        if (_timer is not null) await _timer.DisposeAsync();
        await Task.WhenAny(Task.WhenAll(tasks), Task.Delay(TimeSpan.FromSeconds(15)));
        foreach (var r in _runners.Values)
            if (r is IAsyncDisposable d) await d.DisposeAsync();
    }
}
