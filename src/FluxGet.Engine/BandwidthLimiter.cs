using System.Diagnostics;

namespace FluxGet.Engine;

public sealed class BandwidthLimiter
{
    private readonly object _gate = new();
    private long _rate;
    private double _tokens;
    private long _lastTicks = Stopwatch.GetTimestamp();

    public BandwidthLimiter(long bytesPerSecond = 0) => _rate = Math.Max(0, bytesPerSecond);

    public long Rate
    {
        get => Interlocked.Read(ref _rate);
        set
        {
            lock (_gate)
            {
                _rate = Math.Max(0, value);
                _tokens = Math.Min(_tokens, Capacity);
            }
        }
    }

    private double Capacity => Math.Max(_rate / 5.0, 16 * 1024);

    public ValueTask WaitAsync(int bytes, CancellationToken ct)
    {
        TimeSpan delay;
        lock (_gate)
        {
            if (_rate <= 0) return ValueTask.CompletedTask;
            var now = Stopwatch.GetTimestamp();
            _tokens = Math.Min(Capacity, _tokens + Stopwatch.GetElapsedTime(_lastTicks, now).TotalSeconds * _rate);
            _lastTicks = now;
            _tokens -= bytes;
            if (_tokens >= 0) return ValueTask.CompletedTask;
            delay = TimeSpan.FromSeconds(-_tokens / _rate);
        }
        return new ValueTask(Task.Delay(delay, ct));
    }

    public int SuggestedChunk(int max)
    {
        var r = Rate;
        return r <= 0 ? max : (int)Math.Clamp(r / 20, 4 * 1024, max);
    }
}
