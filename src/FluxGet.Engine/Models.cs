using System.Text.Json;
using System.Text.Json.Serialization;

namespace FluxGet.Engine;

[JsonConverter(typeof(JsonStringEnumConverter<JobKind>))]
public enum JobKind { Http, Media, Torrent }

[JsonConverter(typeof(JsonStringEnumConverter<JobStatus>))]
public enum JobStatus
{
    Queued,
    Scheduled,
    Running,
    Retrying,
    WaitingForNetwork,
    Verifying,
    Paused,
    Seeding,
    NeedsAttention,
    Failed,
    Completed
}

public static class JobStatusExtensions
{
    public static bool IsActive(this JobStatus s) =>
        s is JobStatus.Running or JobStatus.Retrying or JobStatus.WaitingForNetwork or JobStatus.Verifying;

    public static bool IsFinished(this JobStatus s) => s is JobStatus.Completed;
}

public sealed class Segment
{
    public int Index { get; set; }
    public long Start { get; init; }
    public long End { get; set; }
    public long Received;
    internal long Next;
    public bool Active { get; internal set; }

    public long Length => End < 0 ? -1 : End - Start + 1;
    public bool IsComplete => End >= 0 && Received >= End - Start + 1;
    internal long Remaining => End < 0 ? long.MaxValue : End - Next + 1;
}

public sealed class Job
{
    public long Id { get; set; }
    public JobKind Kind { get; init; }
    public string Url { get; set; } = "";
    public string? FileName { get; set; }
    public string Directory { get; set; } = "";
    public string? FilePath { get; set; }
    public string? Group { get; set; }
    public long? TotalBytes { get; set; }
    public long ReceivedBytes;
    public string? ETag { get; set; }
    public string? LastModified { get; set; }
    public bool Resumable { get; set; } = true;
    public int Priority { get; set; }
    public long SpeedLimit { get; set; }
    public int Connections { get; set; }
    public string? Checksum { get; set; }
    public bool UseSchedule { get; set; }
    public JobStatus Status { get; set; } = JobStatus.Queued;
    public string? Error { get; set; }
    public string? Options { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }

    public List<Segment> Segments { get; set; } = [];
    internal IReadOnlyDictionary<string, string>? RequestHeaders { get; set; }
    public readonly object Gate = new();

    public double Speed { get; internal set; }
    public double UploadSpeed { get; internal set; }
    public int ActiveConnections { get; internal set; }
    public string? Phase { get; internal set; }
    public Dictionary<string, object?>? Extra { get; internal set; }

    public T? GetOptions<T>() where T : class =>
        string.IsNullOrEmpty(Options) ? null : JsonSerializer.Deserialize<T>(Options, Json.Options);
}

public sealed record ScheduleWindow(bool Enabled, TimeOnly Start, TimeOnly End, DayOfWeek[] Days)
{
    public static ScheduleWindow Default { get; } = new(false, new TimeOnly(1, 0), new TimeOnly(7, 0), Enum.GetValues<DayOfWeek>());

    public bool IsOpen(DateTime local)
    {
        if (!Enabled) return true;
        var t = TimeOnly.FromDateTime(local);
        if (Start == End) return Days.Contains(local.DayOfWeek);
        if (Start < End) return Days.Contains(local.DayOfWeek) && t >= Start && t < End;
        if (t >= Start) return Days.Contains(local.DayOfWeek);
        if (t < End) return Days.Contains(local.AddDays(-1).DayOfWeek);
        return false;
    }
}

public sealed record EngineSettings
{
    public string DownloadDirectory { get; init; } = DefaultDownloadDirectory();
    public int MaxConcurrentJobs { get; init; } = 3;
    public int ConnectionsPerDownload { get; init; } = 8;
    public long GlobalSpeedLimit { get; init; }
    public long TorrentUploadLimit { get; init; }
    public double SeedRatio { get; init; } = 1.0;
    public ScheduleWindow Schedule { get; init; } = ScheduleWindow.Default;
    public bool NotifyOnComplete { get; init; } = true;
    public JsonElement? Ui { get; init; }

    public static string DefaultDownloadDirectory()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(string.IsNullOrEmpty(home) ? AppContext.BaseDirectory : home, "Downloads", "FluxGet");
    }

    public EngineSettings Normalize() => this with
    {
        MaxConcurrentJobs = Math.Clamp(MaxConcurrentJobs, 1, 20),
        ConnectionsPerDownload = Math.Clamp(ConnectionsPerDownload, 1, 32),
        GlobalSpeedLimit = Math.Max(0, GlobalSpeedLimit),
        TorrentUploadLimit = Math.Max(0, TorrentUploadLimit),
        SeedRatio = Math.Clamp(SeedRatio, 0, 100),
        DownloadDirectory = string.IsNullOrWhiteSpace(DownloadDirectory) ? DefaultDownloadDirectory() : Path.GetFullPath(DownloadDirectory)
    };
}

public sealed class EngineOptions
{
    public required string DataDirectory { get; init; }
    public TimeSpan CheckpointInterval { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan RetryBaseDelay { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan RetryMaxDelay { get; init; } = TimeSpan.FromSeconds(30);
    public int MaxAttempts { get; init; } = 8;
    public TimeSpan StallTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public long MinSplitSize { get; init; } = 1024 * 1024;
    public bool BlockPrivateNetworks { get; init; }
    public bool RestrictToDownloadDirectory { get; init; }
    public string? ToolsDirectory { get; init; }
}

public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}

public class JobAttentionException(string message) : Exception(message);

public class JobFailedException(string message) : Exception(message);
