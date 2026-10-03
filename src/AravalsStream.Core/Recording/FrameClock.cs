using System.Diagnostics;

namespace AravalsStream.Core.Recording;

public sealed class FrameClock
{
    private readonly double _intervalTicks;
    private long _startTicks;
    private long _frameIndex;
    private long _missedTicks;

    public int TargetFps { get; }
    public double TargetIntervalMs => 1000.0 / TargetFps;
    public TimeSpan Elapsed => TimeSpan.FromSeconds((double)(Stopwatch.GetTimestamp() - _startTicks) / Stopwatch.Frequency);
    public long MissedTicks => Interlocked.Read(ref _missedTicks);

    public FrameClock(int targetFps = 60)
    {
        TargetFps = Math.Clamp(targetFps, 1, 120);
        _intervalTicks = (double)Stopwatch.Frequency / TargetFps;
        _startTicks = Stopwatch.GetTimestamp();
        _frameIndex = 0;
    }

    public async Task<long> WaitNextTickAsync(CancellationToken cancellationToken = default)
    {
        var next = _frameIndex + 1;
        var targetTimestamp = _startTicks + (long)(next * _intervalTicks);
        var current = Stopwatch.GetTimestamp();
        var remainingTicks = targetTimestamp - current;

        if (remainingTicks > 0)
        {
            await Task.Delay(TimeSpan.FromSeconds((double)remainingTicks / Stopwatch.Frequency), cancellationToken)
                .ConfigureAwait(false);
            _frameIndex = next;
            return _frameIndex;
        }

        var overdueTicks = -remainingTicks;
        var missed = (long)(overdueTicks / _intervalTicks);
        if (missed > 0)
        {
            Interlocked.Add(ref _missedTicks, missed);
            _frameIndex = next + missed;
        }
        else
        {
            _frameIndex = next;
        }

        return _frameIndex;
    }

    public void Reset()
    {
        _startTicks = Stopwatch.GetTimestamp();
        _frameIndex = 0;
        Interlocked.Exchange(ref _missedTicks, 0);
    }
}
