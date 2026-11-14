using FluxGet.Engine;

namespace FluxGet.Host;

public sealed record SegmentDto(long Start, long End, long Received, bool Active);

public sealed record JobDto(
    long Id, JobKind Kind, string Url, string? FileName, string Directory, string? FilePath, string? Group,
    long? TotalBytes, long ReceivedBytes, double Speed, double UploadSpeed, double? Eta, int Connections, int MaxConnections,
    JobStatus Status, string? Phase, string? Error, bool Resumable, int Priority, long SpeedLimit, string? Checksum,
    bool UseSchedule, DateTime CreatedAt, DateTime? CompletedAt, IReadOnlyList<SegmentDto>? Segments,
    IReadOnlyDictionary<string, object?>? Extra, string? Options)
{
    public static JobDto From(Job j, EngineSettings settings, bool withSegments = true)
    {
        var received = Interlocked.Read(ref j.ReceivedBytes);
        double? eta = j.TotalBytes is { } t && j.Speed > 1 && t > received ? (t - received) / j.Speed : null;
        List<SegmentDto>? segments = null;
        if (withSegments && j.Segments.Count > 0)
            lock (j.Gate) segments = j.Segments.OrderBy(s => s.Start).Select(s => new SegmentDto(s.Start, s.End, Interlocked.Read(ref s.Received), s.Active)).ToList();
        return new JobDto(j.Id, j.Kind, j.Url, j.FileName, j.Directory, j.FilePath, j.Group, j.TotalBytes, received,
            Math.Round(j.Speed), Math.Round(j.UploadSpeed), eta is null ? null : Math.Round(eta.Value), j.ActiveConnections,
            j.Connections > 0 ? j.Connections : settings.ConnectionsPerDownload, j.Status, j.Phase, j.Error, j.Resumable,
            j.Priority, j.SpeedLimit, j.Checksum, j.UseSchedule, j.CreatedAt, j.CompletedAt, segments, j.Extra, j.Options);
    }
}

public sealed record HeadersBody(string? Cookie, string? Referer, string? UserAgent);

public sealed record AddDownloadsBody(
    string[] Urls, string? FileName, string? Directory, int? Connections, long SpeedLimit, string? Checksum,
    bool UseSchedule, int Priority, bool StartPaused, bool AllowDuplicate, HeadersBody? Headers);

public sealed record AddResult(IReadOnlyList<JobDto> Added, IReadOnlyList<DuplicateDto> Duplicates, IReadOnlyList<RejectedDto> Rejected);

public sealed record DuplicateDto(string Url, long ExistingId);

public sealed record RejectedDto(string Url, string Reason);

public sealed record UpdateJobBody(int? Priority, long? SpeedLimit, bool? UseSchedule);

public sealed record AddressBody(string Url);

public sealed record LoginBody(string Key);

public sealed record PairRequestBody(string? Name);

public sealed record PairConfirmBody(string Id, string Code);
