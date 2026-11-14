using System.Collections.Concurrent;
using System.Text.Json;
using MonoTorrent;
using MonoTorrent.Client;

namespace FluxGet.Engine.Torrents;

public sealed record TorrentOptions
{
    public string? TorrentFile { get; init; }
    public int[] Skip { get; init; } = [];
}

public sealed record TorrentFileDto(int Index, string Path, long Length, long Downloaded, bool Download);
public sealed record PeerDto(string Address, string Client, double DownloadRate, double UploadRate, bool Seeder);

public sealed class TorrentRunner : IJobRunner, IAsyncDisposable
{
    private readonly EngineOptions _options;
    private readonly JobStore _store;
    private readonly ConcurrentDictionary<long, TorrentManager> _managers = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ClientEngine? _engine;
    private (long Down, long Up) _limits;

    public TorrentRunner(EngineOptions options, JobStore store)
    {
        _options = options;
        _store = store;
    }

    public JobKind Kind => JobKind.Torrent;

    public string CacheDirectory => Path.Combine(_options.DataDirectory, "torrents");

    public static string ValidateMagnet(string url)
    {
        if (!url.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Enter a magnet link (magnet:?xt=urn:btih:…).");
        try { return MagnetLink.Parse(url).ToV1String(); }
        catch (Exception) { throw new ArgumentException("This magnet link is not valid."); }
    }

    public static (string Magnet, string Name, long Size) Inspect(byte[] torrentFile)
    {
        var torrent = Torrent.Load(torrentFile);
        var hash = torrent.InfoHashes.V1OrV2.ToHex();
        return ($"magnet:?xt=urn:btih:{hash}&dn={Uri.EscapeDataString(torrent.Name)}", torrent.Name, torrent.Size);
    }

    private async Task<ClientEngine> Engine(EngineSettings settings)
    {
        await _gate.WaitAsync();
        try
        {
            var limits = (settings.GlobalSpeedLimit, settings.TorrentUploadLimit);
            if (_engine is null)
            {
                Directory.CreateDirectory(CacheDirectory);
                _engine = new ClientEngine(Build(limits));
                _limits = limits;
            }
            else if (limits != _limits)
            {
                await _engine.UpdateSettingsAsync(Build(limits));
                _limits = limits;
            }
            return _engine;
        }
        finally
        {
            _gate.Release();
        }
    }

    private MonoTorrent.Client.EngineSettings Build((long Down, long Up) limits) => new EngineSettingsBuilder
    {
        CacheDirectory = CacheDirectory,
        AutoSaveLoadFastResume = true,
        AutoSaveLoadMagnetLinkMetadata = true,
        AllowPortForwarding = true,
        MaximumDownloadRate = (int)Math.Min(int.MaxValue, limits.Down),
        MaximumUploadRate = (int)Math.Min(int.MaxValue, limits.Up)
    }.ToSettings();

    public async Task RunAsync(JobContext ctx, CancellationToken ct)
    {
        var job = ctx.Job;
        var engine = await Engine(ctx.Settings);
        var options = job.GetOptions<TorrentOptions>() ?? new TorrentOptions();
        Directory.CreateDirectory(job.Directory);

        if (!_managers.TryGetValue(job.Id, out var manager))
        {
            var existing = engine.Torrents.FirstOrDefault(t => t.SavePath == job.Directory && t.InfoHashes.V1OrV2.ToHex() == MagnetLink.Parse(job.Url).InfoHashes.V1OrV2.ToHex());
            manager = existing ?? (options.TorrentFile is { } file && File.Exists(file)
                ? await engine.AddAsync(await Torrent.LoadAsync(file), job.Directory)
                : await engine.AddAsync(MagnetLink.Parse(job.Url), job.Directory));
            _managers[job.Id] = manager;
        }

        var appliedSkip = false;
        var completed = false;
        try
        {
            await manager.StartAsync();
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (manager.State == TorrentState.Error)
                    throw new JobFailedException("Torrent error: " + (manager.Error?.Exception?.Message ?? "unknown"));

                if (manager.HasMetadata && !appliedSkip)
                {
                    appliedSkip = true;
                    await ApplyPriorities(manager, options.Skip);
                    job.FileName ??= manager.Torrent!.Name;
                    if (job.FileName != manager.Torrent!.Name)
                    {
                        job.FileName = manager.Torrent.Name;
                        ctx.Save();
                    }
                }

                var selected = manager.HasMetadata ? manager.Files.Where(f => f.Priority != Priority.DoNotDownload).ToList() : [];
                var total = selected.Sum(f => f.Length);
                var done = selected.Sum(f => f.BytesDownloaded());
                job.TotalBytes = manager.HasMetadata ? total : null;
                Interlocked.Exchange(ref job.ReceivedBytes, done);
                job.UploadSpeed = manager.Monitor.UploadRate;
                job.ActiveConnections = manager.OpenConnections;
                var uploaded = manager.Monitor.DataBytesSent;
                var ratio = total > 0 ? (double)uploaded / total : 0;
                job.Extra = new Dictionary<string, object?>
                {
                    ["metadata"] = manager.HasMetadata,
                    ["peers"] = manager.Peers.Available + manager.OpenConnections,
                    ["seeds"] = manager.Peers.Seeds,
                    ["leechs"] = manager.Peers.Leechs,
                    ["uploaded"] = uploaded,
                    ["ratio"] = ratio,
                    ["state"] = manager.State.ToString()
                };

                var finished = manager.HasMetadata && total > 0 && done >= total;
                if (finished)
                {
                    job.FilePath = Path.Combine(job.Directory, manager.Torrent!.Name);
                    if (ctx.Settings.SeedRatio <= 0 || ratio >= ctx.Settings.SeedRatio)
                    {
                        completed = true;
                        break;
                    }
                    ctx.SetStatus(JobStatus.Seeding, $"Seeding until ratio {ctx.Settings.SeedRatio:0.##}");
                }
                else
                {
                    var phase = manager.State switch
                    {
                        TorrentState.Metadata => "Fetching metadata from peers",
                        TorrentState.Hashing => "Checking existing data",
                        TorrentState.Downloading when manager.OpenConnections == 0 => "Looking for peers",
                        _ => null
                    };
                    ctx.SetStatus(manager.State == TorrentState.Hashing ? JobStatus.Verifying : JobStatus.Running, phase);
                }
                await Task.Delay(500, ct);
            }
        }
        finally
        {
            try { await manager.StopAsync(TimeSpan.FromSeconds(10)); } catch (Exception) { }
        }
        if (completed) RemoveSkippedFiles(manager);
    }

    private static void RemoveSkippedFiles(TorrentManager manager)
    {
        foreach (var f in manager.Files.Where(f => f.Priority == Priority.DoNotDownload))
        {
            try { if (File.Exists(f.FullPath)) File.Delete(f.FullPath); } catch (Exception) { }
        }
    }

    private static async Task ApplyPriorities(TorrentManager manager, int[] skip)
    {
        for (var i = 0; i < manager.Files.Count; i++)
            await manager.SetFilePriorityAsync(manager.Files[i], skip.Contains(i) ? Priority.DoNotDownload : Priority.Normal);
    }

    public IReadOnlyList<TorrentFileDto>? Files(Job job)
    {
        if (!_managers.TryGetValue(job.Id, out var m) || !m.HasMetadata) return null;
        return m.Files.Select((f, i) => new TorrentFileDto(i, f.Path, f.Length, f.BytesDownloaded(), f.Priority != Priority.DoNotDownload)).ToList();
    }

    public async Task SetFileAsync(Job job, int index, bool download)
    {
        var options = job.GetOptions<TorrentOptions>() ?? new TorrentOptions();
        var skip = options.Skip.Where(i => i != index).ToList();
        if (!download) skip.Add(index);
        job.Options = JsonSerializer.Serialize(options with { Skip = skip.Distinct().Order().ToArray() }, Json.Options);
        _store.Update(job);
        if (_managers.TryGetValue(job.Id, out var m) && m.HasMetadata && index >= 0 && index < m.Files.Count)
            await m.SetFilePriorityAsync(m.Files[index], download ? Priority.Normal : Priority.DoNotDownload);
    }

    public async Task<IReadOnlyList<PeerDto>> PeersAsync(Job job)
    {
        if (!_managers.TryGetValue(job.Id, out var m)) return [];
        var peers = await m.GetPeersAsync();
        return peers.Select(p => new PeerDto(p.Uri.Host + ":" + p.Uri.Port, p.ClientApp.Client.ToString(), p.Monitor.DownloadRate, p.Monitor.UploadRate, p.IsSeeder)).ToList();
    }

    public async Task CleanupAsync(Job job, bool deleteFiles)
    {
        if (_managers.TryRemove(job.Id, out var m) && _engine is not null)
        {
            try { await m.StopAsync(TimeSpan.FromSeconds(10)); } catch (Exception) { }
            await _engine.RemoveAsync(m, deleteFiles && job.Status != JobStatus.Completed ? RemoveMode.CacheDataAndDownloadedData : RemoveMode.CacheDataOnly);
        }
        if (job.GetOptions<TorrentOptions>()?.TorrentFile is { } file)
        {
            try { File.Delete(file); } catch (Exception) { }
        }
    }

    public void Reset(Job job) => _managers.TryRemove(job.Id, out _);

    public async ValueTask DisposeAsync()
    {
        var engine = Interlocked.Exchange(ref _engine, null);
        if (engine is null) return;
        try { await engine.StopAllAsync(); } catch (Exception) { }
        engine.Dispose();
    }
}
