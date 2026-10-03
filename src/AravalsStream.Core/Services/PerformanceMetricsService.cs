using System.Diagnostics;

namespace AravalsStream.Core.Services;

public enum PipelineStage
{
    CaptureCopy, CanvasClear, DisplayTransform, WindowTransform, CameraTransform,
    AlertBlend, ChatBlend, YuvConvert, RecordingResize, VideoPipeWait,
    AudioPipeWait, ChatRender, AlertRender
}

public sealed record PipelineStageTiming(PipelineStage Stage, long Samples, double AverageMilliseconds,
    double MaximumMilliseconds);

public sealed record PerformanceSnapshot(double CpuPercent, double WorkingSetMb, double PrivateMemoryMb,
    double AllocatedMbPerSecond, int Gen0Collections, int Gen1Collections, int Gen2Collections,
    double CaptureFps, double CaptureMilliseconds, double CompositionFps, double CompositionMilliseconds,
    double PreviewFps, double OutputFps, long CaptureDrops, long CompositionDrops, long PreviewDrops, long OutputDrops)
{
    public long CompositionDeadlineDrops { get; init; }
}

public sealed class PerformanceMetricsService : IDisposable
{
    private readonly Process _process = Process.GetCurrentProcess();
    private readonly object _gate = new();
    private long _lastTimestamp = Stopwatch.GetTimestamp();
    private TimeSpan _lastCpu;
    private long _lastAllocated = GC.GetTotalAllocatedBytes(false);
    private int _gen0 = GC.CollectionCount(0), _gen1 = GC.CollectionCount(1), _gen2 = GC.CollectionCount(2);
    private long _compositionFrames, _previewFrames, _outputFrames;
    private long _captureFrames, _captureTicks;
    private long _captureDrops, _compositionDrops, _previewDrops, _outputDrops;
    private long _compositionDeadlineDrops;
    private long _compositionTicks;
    private readonly long[] _stageTicks = new long[Enum.GetValues<PipelineStage>().Length];
    private readonly long[] _stageCounts = new long[Enum.GetValues<PipelineStage>().Length];
    private readonly long[] _stageMaxTicks = new long[Enum.GetValues<PipelineStage>().Length];
    public PerformanceSnapshot Last { get; private set; } = new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    public void CaptureFrame(long elapsedStopwatchTicks)
    {
        Interlocked.Increment(ref _captureFrames);
        Interlocked.Add(ref _captureTicks, elapsedStopwatchTicks);
    }
    public void DropCaptureFrame() => Interlocked.Increment(ref _captureDrops);
    public void DropCaptureFrames(long count) { if (count > 0) Interlocked.Add(ref _captureDrops, count); }

    public void CompositionFrame(long elapsedStopwatchTicks)
    {
        Interlocked.Increment(ref _compositionFrames);
        Interlocked.Add(ref _compositionTicks, elapsedStopwatchTicks);
    }
    public void PreviewFrame() => Interlocked.Increment(ref _previewFrames);
    public void OutputFrame() => Interlocked.Increment(ref _outputFrames);
    public void DropCompositionFrame() => Interlocked.Increment(ref _compositionDrops);
    public void DropCompositionDeadlines(long count) => Interlocked.Add(ref _compositionDeadlineDrops, count);
    public void DropPreviewFrame() => Interlocked.Increment(ref _previewDrops);
    public void DropOutputFrame() => Interlocked.Increment(ref _outputDrops);

    public void Stage(PipelineStage stage, long elapsedTicks)
    {
        if (elapsedTicks < 0) return;
        var index = (int)stage;
        Interlocked.Add(ref _stageTicks[index], elapsedTicks);
        Interlocked.Increment(ref _stageCounts[index]);
        var current = Interlocked.Read(ref _stageMaxTicks[index]);
        while (elapsedTicks > current)
        {
            var observed = Interlocked.CompareExchange(ref _stageMaxTicks[index], elapsedTicks, current);
            if (observed == current) break;
            current = observed;
        }
    }

    public IReadOnlyList<PipelineStageTiming> StageTimings() => Enum.GetValues<PipelineStage>()
        .Select(stage =>
        {
            var index = (int)stage;
            var count = Interlocked.Read(ref _stageCounts[index]);
            return new PipelineStageTiming(stage, count,
                count == 0 ? 0 : Interlocked.Read(ref _stageTicks[index]) * 1000.0 / Stopwatch.Frequency / count,
                Interlocked.Read(ref _stageMaxTicks[index]) * 1000.0 / Stopwatch.Frequency);
        }).ToArray();

    public PerformanceSnapshot Sample()
    {
        lock (_gate)
        {
            var now = Stopwatch.GetTimestamp();
            var seconds = (double)(now - _lastTimestamp) / Stopwatch.Frequency;
            if (seconds < 0.5) return Last;
            _process.Refresh();
            var cpu = _process.TotalProcessorTime;
            var allocated = GC.GetTotalAllocatedBytes(false);
            var composition = Interlocked.Exchange(ref _compositionFrames, 0);
            var capture = Interlocked.Exchange(ref _captureFrames, 0);
            var preview = Interlocked.Exchange(ref _previewFrames, 0);
            var output = Interlocked.Exchange(ref _outputFrames, 0);
            var compositionTicks = Interlocked.Exchange(ref _compositionTicks, 0);
            var captureTicks = Interlocked.Exchange(ref _captureTicks, 0);
            var g0 = GC.CollectionCount(0); var g1 = GC.CollectionCount(1); var g2 = GC.CollectionCount(2);
            Last = new PerformanceSnapshot(
                Math.Clamp((cpu - _lastCpu).TotalSeconds / (seconds * Environment.ProcessorCount) * 100, 0, 100),
                _process.WorkingSet64 / 1048576.0, _process.PrivateMemorySize64 / 1048576.0,
                (allocated - _lastAllocated) / 1048576.0 / seconds, g0 - _gen0, g1 - _gen1, g2 - _gen2,
                capture / seconds, capture == 0 ? 0 : captureTicks * 1000.0 / Stopwatch.Frequency / capture,
                composition / seconds, composition == 0 ? 0 : compositionTicks * 1000.0 / Stopwatch.Frequency / composition,
                preview / seconds, output / seconds,
                Interlocked.Read(ref _captureDrops), Interlocked.Read(ref _compositionDrops),
                Interlocked.Read(ref _previewDrops), Interlocked.Read(ref _outputDrops))
            { CompositionDeadlineDrops = Interlocked.Read(ref _compositionDeadlineDrops) };
            _lastTimestamp = now; _lastCpu = cpu; _lastAllocated = allocated;
            _gen0 = g0; _gen1 = g1; _gen2 = g2;
            return Last;
        }
    }

    public void Dispose() => _process.Dispose();
}
