using System.Diagnostics;
using AravalsStream.App.Composition.D3D11;
using AravalsStream.App.Composition.Pipelines;
using AravalsStream.Core.Composition;
using AravalsStream.Core.Models;
using AravalsStream.Core.Recording;
using AravalsStream.Core.Services;

namespace AravalsStream.App.Composition;

/// <summary>
/// Central frame hub delivering composed video surfaces to encoders and publishers.
/// Supports high-performance D3D11 GPU rendering with NV12 output, and seamless CPU fallback.
/// </summary>
public sealed class ComposedFrameHub : IDisposable
{
    private readonly ISceneCompositor _compositor;
    private readonly PerformanceMetricsService? _metrics;
    private readonly D3D11DeviceManager _deviceManager;
    private readonly D3D11FrameCompositor _gpuCompositor;
    private readonly GpuNv12Converter _gpuConverter;
    private readonly object _gate = new();
    private readonly Dictionary<OutputMode, Feed> _feeds = [];
    private D3D11Shaders? _shaders;
    private int _shaderGen = -1;
    private bool _disposed;

    public VideoPipelineMode PipelineMode { get; set; } = VideoPipelineMode.Auto;
    public bool IsHardwareAccelerated => PipelineMode != VideoPipelineMode.Cpu && _deviceManager.IsInitialized && _deviceManager.IsHardwareDevice;
    public string ActivePixelFormat => IsHardwareAccelerated ? "nv12" : "yuv420p";

    public ComposedFrameHub(
        ISceneCompositor compositor,
        PerformanceMetricsService? metrics = null,
        D3D11DeviceManager? deviceManager = null)
    {
        _compositor = compositor;
        _metrics = metrics;
        _deviceManager = deviceManager ?? D3D11DeviceManager.Instance;
        _gpuCompositor = new D3D11FrameCompositor(_deviceManager);
        _gpuConverter = new GpuNv12Converter(_deviceManager);
    }

    private D3D11Shaders EnsureShaders()
    {
        if (_shaders is null || _shaderGen != _deviceManager.DeviceGeneration)
        {
            _shaders?.Dispose();
            _shaders = _deviceManager.WithContext((dev, _) => new D3D11Shaders(dev));
            _shaderGen = _deviceManager.DeviceGeneration;
        }
        return _shaders;
    }

    public (double RenderMs, double ConvertMs, long DroppedFrames) Diagnostics(OutputMode mode)
    {
        lock (_gate)
        {
            return _feeds.TryGetValue(mode, out var feed)
                ? (feed.RenderMilliseconds, feed.ConvertMilliseconds, feed.Frames.DroppedWrites)
                : (0, 0, 0);
        }
    }

    public Lease Acquire(OutputMode mode, int requestedFps = 60)
    {
        requestedFps = Math.Clamp(requestedFps, 1, 60);
        lock (_gate)
        {
            if (!_feeds.TryGetValue(mode, out var feed))
            {
                var (width, height) = CanvasLayout.Size(mode);
                feed = new Feed(this, _compositor, _metrics, mode, width, height, requestedFps);
                _feeds.Add(mode, feed);
            }
            feed.Users++;
            feed.RequestedRates.Add(requestedFps);
            Volatile.Write(ref feed.TargetFps, feed.RequestedRates.Max());
            return new Lease(this, feed, requestedFps);
        }
    }

    private void Release(Feed feed, int requestedFps)
    {
        lock (_gate)
        {
            feed.RequestedRates.Remove(requestedFps);
            if (feed.RequestedRates.Count > 0)
                Volatile.Write(ref feed.TargetFps, feed.RequestedRates.Max());
            if (--feed.Users != 0) return;
            _feeds.Remove(feed.Mode);
            feed.Stop();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        lock (_gate)
        {
            foreach (var feed in _feeds.Values) feed.Stop();
            _feeds.Clear();
        }

        _gpuCompositor.Dispose();
        _gpuConverter.Dispose();
        _shaders?.Dispose();
    }

    public sealed class Lease : IDisposable
    {
        private ComposedFrameHub? _owner;
        private readonly Feed _feed;
        private readonly int _requestedFps;

        internal Lease(ComposedFrameHub owner, Feed feed, int requestedFps)
        {
            _owner = owner;
            _feed = feed;
            _requestedFps = requestedFps;
        }

        public PooledFrameStore.Reader? AcquireLatest() => _feed.Frames.AcquireLatest();
        public long DroppedCompositionFrames => _feed.Frames.DroppedWrites;
        public double CompositionMilliseconds => _feed.CompositionMilliseconds;
        public string PixelFormat => _feed.PixelFormat;
        public void ReportOutputFrame() => _owner?._metrics?.OutputFrame();
        public void ReportStage(PipelineStage stage, long elapsedTicks) => _owner?._metrics?.Stage(stage, elapsedTicks);
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release(_feed, _requestedFps);
    }

    internal sealed class Feed
    {
        private readonly ComposedFrameHub _owner;
        private readonly ISceneCompositor _compositor;
        private readonly PerformanceMetricsService? _metrics;
        private readonly int _width;
        private readonly int _height;
        private readonly byte[] _bgraScratch;
        private readonly byte[]? _yuv420Scratch;
        private readonly CanvasRenderCache _renderCache = new();
        private readonly CancellationTokenSource _stop = new();
        private bool _gpuFailed;

        public OutputMode Mode { get; }
        public int Users;
        public readonly List<int> RequestedRates = [];
        public int TargetFps;
        public PooledFrameStore Frames { get; }
        public string PixelFormat { get; }
        public double CompositionMilliseconds { get; private set; }
        public double RenderMilliseconds { get; private set; }
        public double ConvertMilliseconds { get; private set; }

        public Feed(
            ComposedFrameHub owner,
            ISceneCompositor compositor,
            PerformanceMetricsService? metrics,
            OutputMode mode,
            int width,
            int height,
            int fps)
        {
            _owner = owner;
            _compositor = compositor;
            Mode = mode;
            _width = width;
            _height = height;
            _metrics = metrics;
            TargetFps = fps;
            PixelFormat = owner.ActivePixelFormat;
            _bgraScratch = new byte[checked(width * height * 4)];
            _yuv420Scratch = PixelFormat == "nv12" ? new byte[Yuv420FrameConverter.BufferSize(width, height)] : null;
            Frames = new PooledFrameStore(Yuv420FrameConverter.BufferSize(width, height), capacity: 5);
            _ = Task.Run(RenderAsync);
        }

        private async Task RenderAsync()
        {
            var currentFps = Volatile.Read(ref TargetFps);
            var clock = new FrameClock(currentFps);
            long lastTick = 0;

            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var wantedFps = Volatile.Read(ref TargetFps);
                    if (wantedFps != currentFps)
                    {
                        currentFps = wantedFps;
                        clock = new FrameClock(currentFps);
                        lastTick = 0;
                    }

                    var tick = await clock.WaitNextTickAsync(_stop.Token).ConfigureAwait(false);
                    if (lastTick > 0 && tick > lastTick + 1)
                        _metrics?.DropCompositionDeadlines(tick - lastTick - 1);
                    lastTick = tick;

                    using var writer = Frames.TryBeginWrite();
                    if (writer is null)
                    {
                        _metrics?.DropCompositionFrame();
                        continue;
                    }

                    bool useGpu = PixelFormat == "nv12" && !_gpuFailed &&
                        _owner.PipelineMode != VideoPipelineMode.Cpu &&
                        _owner._deviceManager.IsInitialized && _owner._deviceManager.IsHardwareDevice;
                    bool gpuSucceeded = false;

                    // 1. Direct3D 11 GPU Pipeline Path
                    if (useGpu)
                    {
                        try
                        {
                            var start = Stopwatch.GetTimestamp();

                            using var gpuLease = _owner._gpuCompositor.RenderScene(
                                Mode,
                                _compositor.ActiveScene,
                                source => _compositor.AcquireSourceFrame(source, Mode),
                                _width,
                                _height);

                            var afterRender = Stopwatch.GetTimestamp();

                            _owner._gpuConverter.ConvertBgraToNv12Gpu(
                                gpuLease.Frame.Texture,
                                _width,
                                _height,
                                writer.Buffer,
                                _owner.EnsureShaders());

                            var afterConvert = Stopwatch.GetTimestamp();

                            _metrics?.Stage(PipelineStage.YuvConvert, afterConvert - afterRender);
                            RenderMilliseconds = Stopwatch.GetElapsedTime(start, afterRender).TotalMilliseconds;
                            ConvertMilliseconds = Stopwatch.GetElapsedTime(afterRender, afterConvert).TotalMilliseconds;
                            CompositionMilliseconds = Stopwatch.GetElapsedTime(start, afterConvert).TotalMilliseconds;
                            _metrics?.CompositionFrame(afterConvert - start);
                            writer.Publish();
                            gpuSucceeded = true;
                        }
                        catch (Exception ex)
                        {
                            AppLog.Write("ComposedFrameHub", $"GPU render failed for {Mode}: {ex.Message}; engaging CPU fallback.");
                            _gpuFailed = true;
                            gpuSucceeded = false;
                        }
                    }

                    // 2. CPU Fallback Pipeline Path
                    if (!gpuSucceeded)
                    {
                        var start = Stopwatch.GetTimestamp();
                        _compositor.RenderComposedFrame(Mode, _width, _height, _bgraScratch, _renderCache);
                        var afterRender = Stopwatch.GetTimestamp();

                        if (PixelFormat == "nv12")
                        {
                            Yuv420FrameConverter.Convert(_bgraScratch, _yuv420Scratch!, _width, _height);
                            Yuv420FrameConverter.PlanarToNv12(_yuv420Scratch!, writer.Buffer, _width, _height);
                        }
                        else
                        {
                            Yuv420FrameConverter.Convert(_bgraScratch, writer.Buffer, _width, _height);
                        }
                        var afterConvert = Stopwatch.GetTimestamp();

                        _metrics?.Stage(PipelineStage.YuvConvert, afterConvert - afterRender);
                        RenderMilliseconds = Stopwatch.GetElapsedTime(start, afterRender).TotalMilliseconds;
                        ConvertMilliseconds = Stopwatch.GetElapsedTime(afterRender, afterConvert).TotalMilliseconds;
                        CompositionMilliseconds = Stopwatch.GetElapsedTime(start, afterConvert).TotalMilliseconds;
                        _metrics?.CompositionFrame(afterConvert - start);
                        writer.Publish();
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                AppLog.Write("Composition", $"Frame feed failed for {Mode}: {ex.Message}");
            }
        }

        public void Stop() => _stop.Cancel();
    }
}
