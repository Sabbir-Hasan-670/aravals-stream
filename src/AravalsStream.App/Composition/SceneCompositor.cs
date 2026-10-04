using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Diagnostics;
using System.IO;
using System.Globalization;
using AravalsStream.Capture.Camera;
using AravalsStream.Capture.Display;
using AravalsStream.Capture.Video;
using AravalsStream.Capture.Window;
using AravalsStream.Core.Composition;
using AravalsStream.Core.Models;
using AravalsStream.Core.Services;
using AravalsStream.Core.RemoteCapture;
using AravalsStream.App.RemoteCapture;

namespace AravalsStream.App.Composition;

// One feed per physical visual resource; two WPF canvases render that feed independently.
public sealed class SceneCompositor : ISceneCompositor
{
    private sealed class Feed(IVideoCaptureSession session, RemoteSourceStateMachine? remoteState)
    {
        public IVideoCaptureSession? Session { get; set; } = session;
        public RemoteSourceStateMachine? RemoteState { get; } = remoteState;
        public DateTimeOffset LastFrameAt = DateTimeOffset.UtcNow;
        public CancellationTokenSource? ReconnectCancellation;
        public bool ReconnectScheduled;
        public WriteableBitmap? Bitmap { get; set; }
        public int Pending;
        public PooledFrameStore? RawFrames { get; set; }
        public int RawWidth, RawHeight, RawStride, RawBytes;
        public readonly object FrameLock = new();
        public long LastPreviewTicks;
        public long Version;
        public long FramesSubmitted;
        public long FramesReceived;
        public long PreviewUpdates;
    }

    private sealed class OverlayFeed(int width, int height)
    {
        public int Width { get; } = width;
        public int Height { get; } = height;
        public PooledFrameStore Frames { get; } = new(checked(width * height * 4), 3);
        public long Version;
    }

    private readonly IDisplayCaptureService _displays = new DesktopDuplicationCaptureService();
    private readonly IWindowCaptureService _windows = new WindowCaptureService();
    private readonly ICameraCaptureService _cameras = new CameraCaptureService();
    private readonly RemoteCaptureSessionFactory? _remoteCapture;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _remoteHealthTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Dictionary<string, Feed> _feeds = [];
    private readonly Dictionary<Guid, CaptureResource> _resources = [];
    private readonly Dictionary<(SourceType, OutputMode), OverlayFeed> _overlays = [];
    private readonly Dictionary<(SourceType, OutputMode), WriteableBitmap> _overlayBitmaps = [];
    private readonly object _overlayLock = new();
    private readonly object _rateGate = new();
    private readonly Dictionary<string, (long Ticks, long Count)> _rateWindows = [];
    private bool _disposed;
    private bool _captureSuspended;
    private bool _previewVisible = true;
    public bool PreviewVisible
    {
        get => _previewVisible;
        set
        {
            var restoring = value && !_previewVisible;
            _previewVisible = value;
            if (restoring) RestoreLatestPreviews();
        }
    }
    public int PreviewTargetFps { get; set; } = 30;
    public int CaptureTargetFps
    {
        get => _captureTargetFps;
        set
        {
            _captureTargetFps = value;
            foreach (var feed in _feeds.Values)
            {
                if (feed.Session is IDisplayCaptureSession displaySession)
                {
                    displaySession.TargetFps = value;
                }
                if (feed.Session is IWindowCaptureSession windowSession)
                {
                    windowSession.TargetFps = value;
                }
            }
        }
    }
    private int _captureTargetFps = 60;
    public PerformanceMetricsService? Metrics { get; set; }
    public Scene? ActiveScene { get; private set; }
    public event Action? CompositionChanged;
    public event Action<string>? FrameReady;
    public event Action<string, string>? SourceFailed;

    public SceneCompositor(Dispatcher dispatcher, RemoteCaptureSessionFactory? remoteCapture = null)
    {
        _dispatcher = dispatcher;
        _remoteCapture = remoteCapture;
        _remoteHealthTimer.Tick += (_, _) => CheckRemoteFeedHealth();
        _remoteHealthTimer.Start();
    }

    public void SetResources(IEnumerable<CaptureResource> resources)
    {
        _resources.Clear();
        foreach (var resource in resources) _resources[resource.Id] = resource;
    }

    public string? KeyFor(SceneSource source)
    {
        if (_resources.TryGetValue(source.SourceReference, out var resource))
            return resource.Id.ToString();
        return source.Type == SourceType.DisplayCapture && source.DisplayId is not null
            ? $"{SourceType.DisplayCapture}:{source.DisplayId}" : null;
    }

    public void SetScene(Scene? scene, IEnumerable<Scene> allScenes)
    {
        ActiveScene = scene;
        RefreshSources(allScenes);
        CompositionChanged?.Invoke();
    }

    public void RefreshSources(IEnumerable<Scene> allScenes)
    {
        if (_disposed) return;
        if (_captureSuspended)
        {
            foreach (var key in _feeds.Keys.ToList()) StopFeed(key);
            return;
        }
        // Only the active scene needs physical capture. Switching away must release
        // an otherwise idle display/camera even if another saved scene references it.
        var referenced = CaptureDemand.ActiveResourceKeys(ActiveScene, KeyFor);
        foreach (var stale in _feeds.Keys.Where(k => !referenced.Contains(k)).ToList()) StopFeed(stale);
        if (ActiveScene is null) return;

        foreach (var source in ActiveScene.Sources.Where(s => s.Visible && s.HasVideo && IsVisual(s.Type)))
        {
            var key = KeyFor(source);
            if (key is null || _feeds.ContainsKey(key)) continue;
            try
            {
                var session = Start(source);
                var remoteState = source.Type == SourceType.RemotePc ? new RemoteSourceStateMachine() : null;
                remoteState?.Transition(RemoteSourceState.Connecting);
                var feed = new Feed(session, remoteState);
                _feeds.Add(key, feed);
                AppLog.Write("Capture", $"Capture started: {key}");
                AttachSession(key, feed, session);
                if (remoteState is not null) LogRemoteState(key, feed, "Session started.");
            }
            catch (Exception ex)
            {
                AppLog.Write("Capture", $"Capture start failed: {key}: {ex.Message}");
                SourceFailed?.Invoke(key, ex.Message);
            }
        }
        CompositionChanged?.Invoke();
    }

    public void SetCaptureSuspended(bool suspended, IEnumerable<Scene> scenes)
    {
        if (_captureSuspended == suspended) return;
        _captureSuspended = suspended;
        RefreshSources(scenes);
    }

    public void RestartResource(Guid resourceId, IEnumerable<Scene> allScenes)
    {
        StopFeed(resourceId.ToString());
        RefreshSources(allScenes);
    }

    private static bool IsVisual(SourceType type) => type is SourceType.DisplayCapture or SourceType.WindowCapture or SourceType.Camera or SourceType.CaptureDevice or SourceType.RemotePc;

    private IVideoCaptureSession Start(SceneSource source)
    {
        if (source.DisplayId == "performance_test_pattern" || source.Name.Contains("Test Pattern", StringComparison.OrdinalIgnoreCase))
        {
            return new PerformanceTestPatternSession(1920, 1080, 60);
        }
        _resources.TryGetValue(source.SourceReference, out var resource);
        return source.Type switch
        {
            SourceType.DisplayCapture => StartDisplay(resource?.DeviceId ?? source.DisplayId ?? ""),
            SourceType.WindowCapture when resource is not null => StartWindow(resource),
            SourceType.Camera when resource is not null => _cameras.Start(
                new CameraInfo(resource.DeviceId, resource.Name, resource.CameraIndex),
                resource.FormatWidth > 0 && resource.FormatHeight > 0 && resource.FormatFrameRate > 0
                    ? new CameraFormat(resource.FormatWidth, resource.FormatHeight, resource.FormatFrameRate, resource.FormatSubtype) : null),
            SourceType.CaptureDevice when resource is not null => _cameras.Start(
                new CameraInfo(resource.DeviceId, resource.Name, resource.CameraIndex),
                resource.FormatWidth > 0 && resource.FormatHeight > 0 && resource.FormatFrameRate > 0
                    ? new CameraFormat(resource.FormatWidth, resource.FormatHeight, resource.FormatFrameRate, resource.FormatSubtype) : null),
            SourceType.RemotePc when resource is not null && _remoteCapture is not null => _remoteCapture.Start(resource),
            _ => throw new InvalidOperationException("Source configuration is missing.")
        };
    }

    private IVideoCaptureSession StartDisplay(string id)
    {
        var display = _displays.EnumerateDisplays().FirstOrDefault(d => d.Id == id)
            ?? throw new InvalidOperationException("Display is disconnected.");
        var session = _displays.Start(display);
        session.TargetFps = _captureTargetFps;
        return session;
    }

    private IVideoCaptureSession StartWindow(CaptureResource resource)
    {
        if (!long.TryParse(resource.DeviceId, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var handleValue))
            throw new InvalidOperationException("Window handle is invalid. Re-select the window capture source.");
        var window = _windows.GetWindowInfo((nint)handleValue, allowMinimized: true, expectedProcessName: resource.ProcessName)
            ?? throw new InvalidOperationException("Window is closed or no longer available.");
        resource.DeviceId = window.Id;
        var session = _windows.Start(window);
        session.TargetFps = _captureTargetFps;
        return session;
    }

    public BitmapSource? FrameFor(SceneSource source) => source.HasVideo && KeyFor(source) is { } key && _feeds.TryGetValue(key, out var feed)
        ? feed.Bitmap : null;

    private void RestoreLatestPreviews()
    {
        if (!_dispatcher.CheckAccess()) { _dispatcher.BeginInvoke(RestoreLatestPreviews); return; }
        foreach (var (key, feed) in _feeds)
        {
            lock (feed.FrameLock)
            {
                using var reader = feed.RawFrames?.AcquireLatest();
                if (reader is null) continue;
                if (feed.Bitmap is null || feed.Bitmap.PixelWidth != feed.RawWidth || feed.Bitmap.PixelHeight != feed.RawHeight)
                    feed.Bitmap = new WriteableBitmap(feed.RawWidth, feed.RawHeight, 96, 96, PixelFormats.Bgra32, null);
                feed.Bitmap.WritePixels(new Int32Rect(0, 0, feed.RawWidth, feed.RawHeight), reader.Buffer, feed.RawStride, 0);
            }
            FrameReady?.Invoke(key);
        }
    }

    public BitmapSource? OverlayFrameFor(SourceType type, OutputMode mode) =>
        _overlayBitmaps.TryGetValue((type, mode), out var bitmap) ? bitmap : null;

    public void SetOverlay(SourceType type, OutputMode mode, RawVideoFrame frame)
    {
        SetOverlayRendered(type, mode, frame.Width, frame.Height,
            pixels =>
            {
                for (var row = 0; row < frame.Height; row++)
                    Buffer.BlockCopy(frame.Pixels, row * frame.Stride, pixels, row * frame.Width * 4, frame.Width * 4);
            });
    }

    public void SetOverlayRendered(SourceType type, OutputMode mode, int width, int height, Action<byte[]> render)
    {
        OverlayFeed feed;
        lock (_overlayLock)
        {
            if (_overlays.TryGetValue((type, mode), out var existing) &&
                existing.Width == width && existing.Height == height) feed = existing;
            else
                _overlays[(type, mode)] = feed = new OverlayFeed(width, height);
        }
        using var writer = feed.Frames.TryBeginWrite();
        if (writer is null) return;
        render(writer.Buffer);
        if (_dispatcher.CheckAccess() && PreviewVisible)
        {
            if (!_overlayBitmaps.TryGetValue((type, mode), out var bitmap) ||
                bitmap.PixelWidth != width || bitmap.PixelHeight != height)
                _overlayBitmaps[(type, mode)] = bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
            bitmap.WritePixels(new Int32Rect(0, 0, width, height), writer.Buffer, width * 4, 0);
        }
        writer.Publish();
        feed.Version++;
    }

    public void CopyOverlay(SourceType type, OutputMode fromMode, OutputMode toMode)
    {
        OverlayFeed? source;
        lock (_overlayLock) _overlays.TryGetValue((type, fromMode), out source);
        if (source is null) return;
        using var reader = source.Frames.AcquireLatest();
        if (reader is null) return;
        SetOverlayRendered(type, toMode, source.Width, source.Height,
            target => Buffer.BlockCopy(reader.Buffer, 0, target, 0, checked(source.Width * source.Height * 4)));
    }

    public RawVideoFrame? RawFrameFor(SceneSource source)
    {
        if (KeyFor(source) is { } key && _feeds.TryGetValue(key, out var feed))
        {
            lock (feed.FrameLock)
            {
                using var snapshot = feed.RawFrames?.AcquireLatest();
                if (snapshot is null) return null;
                return new RawVideoFrame(feed.RawWidth, feed.RawHeight, feed.RawStride, (byte[])snapshot.Buffer.Clone());
            }
        }
        return null;
    }

    public void RenderComposedFrame(OutputMode mode, int targetWidth, int targetHeight, byte[] destinationBuffer,
        CanvasRenderCache? renderCache = null)
    {
        var readers = new List<PooledFrameStore.Reader>();
        try
        {
            CompositedFrameRenderer.Render(
                targetWidth, targetHeight, mode, ActiveScene,
                source => source.Type is SourceType.Alerts or SourceType.ChatOverlay
                    ? AcquireOverlay(source, mode, readers) : AcquireRawFrame(source, readers),
                destinationBuffer,
                mode == OutputMode.Vertical ? 1080 : 1920,
                mode == OutputMode.Vertical ? 1920 : 1080,
                (type, ticks) => Metrics?.Stage(type switch
                {
                    SourceType.DisplayCapture => PipelineStage.DisplayTransform,
                    SourceType.WindowCapture or SourceType.GameCapture => PipelineStage.WindowTransform,
                    SourceType.Camera => PipelineStage.CameraTransform,
                    SourceType.CaptureDevice => PipelineStage.CameraTransform,
                    SourceType.RemotePc => PipelineStage.CameraTransform,
                    SourceType.Alerts => PipelineStage.AlertBlend,
                    SourceType.ChatOverlay => PipelineStage.ChatBlend,
                    _ => PipelineStage.DisplayTransform
                }, ticks),
                ticks => Metrics?.Stage(PipelineStage.CanvasClear, ticks),
                renderCache);
        }
        finally { foreach (var reader in readers) reader.Dispose(); }
    }

    private RawVideoFrame? AcquireRawFrame(SceneSource source, List<PooledFrameStore.Reader> readers)
    {
        if (KeyFor(source) is not { } key || !_feeds.TryGetValue(key, out var feed)) return null;
        lock (feed.FrameLock)
        {
            var reader = feed.RawFrames?.AcquireLatest();
            if (reader is null) return null;
            readers.Add(reader);
            return new RawVideoFrame(feed.RawWidth, feed.RawHeight, feed.RawStride, reader.Buffer);
        }
    }

    private RawVideoFrame? AcquireOverlay(SceneSource source, OutputMode mode, List<PooledFrameStore.Reader> readers)
    {
        lock (_overlayLock)
        {
            if (!_overlays.TryGetValue((source.Type, mode), out var feed)) return null;
            var reader = feed.Frames.AcquireLatest();
            if (reader is null) return null;
            readers.Add(reader);
            return new RawVideoFrame(feed.Width, feed.Height, feed.Width * 4, reader.Buffer);
        }
    }

    public SceneSourceFrameLease? AcquireSourceFrame(SceneSource source, OutputMode mode)
    {
        if (!source.HasVideo) return null;
        if (source.Type is SourceType.Alerts or SourceType.ChatOverlay)
        {
            lock (_overlayLock)
            {
                if (!_overlays.TryGetValue((source.Type, mode), out var feed)) return null;
                var reader = feed.Frames.AcquireLatest();
                if (reader is null) return null;
                return new SceneSourceFrameLease(
                    new RawVideoFrame(feed.Width, feed.Height, feed.Width * 4, reader.Buffer), feed.Version, reader);
            }
        }
        else
        {
            if (KeyFor(source) is not { } key || !_feeds.TryGetValue(key, out var feed)) return null;
            lock (feed.FrameLock)
            {
                var reader = feed.RawFrames?.AcquireLatest();
                if (reader is null) return null;
                return new SceneSourceFrameLease(
                    new RawVideoFrame(feed.RawWidth, feed.RawHeight, feed.RawStride, reader.Buffer), feed.Version, reader);
            }
        }
    }

    private void OnFrame(string key, Feed feed, IVideoCaptureSession session, DisplayFrame frame)
    {
        if (!ReferenceEquals(feed.Session, session)) return;
        feed.LastFrameAt = DateTimeOffset.UtcNow;
        if (feed.RemoteState is { State: not RemoteSourceState.Live } remoteState)
        {
            remoteState.Transition(RemoteSourceState.Live);
            feed.ReconnectScheduled = false;
            feed.ReconnectCancellation?.Cancel();
            LogRemoteState(key, feed, "Video frames received.");
        }
        var submitted = Interlocked.Increment(ref feed.FramesSubmitted);
        ReportRate($"CompositorSubmitted:{key}", submitted);
        var captureStart = Stopwatch.GetTimestamp();
        Metrics?.DropCaptureFrames(frame.MissedFrames);
        // Retain latest raw frame for background media pipeline
        try
        {
            PooledFrameStore store;
            lock (feed.FrameLock)
            {
                var validBytes = checked(frame.Stride * frame.Height);
                if (frame.Pixels.Length < validBytes) throw new InvalidDataException("Capture frame buffer is truncated.");
                if (feed.RawFrames is null || feed.RawBytes != validBytes ||
                    feed.RawWidth != frame.Width || feed.RawHeight != frame.Height || feed.RawStride != frame.Stride)
                    feed.RawFrames = new PooledFrameStore(validBytes);
                feed.RawWidth = frame.Width; feed.RawHeight = frame.Height;
                feed.RawStride = frame.Stride; feed.RawBytes = validBytes;
                store = feed.RawFrames;
            }
            using var writer = store.TryBeginWrite();
            if (writer is not null)
            {
                var copyStart = Stopwatch.GetTimestamp();
                Buffer.BlockCopy(frame.Pixels, 0, writer.Buffer, 0, feed.RawBytes);
                Metrics?.Stage(PipelineStage.CaptureCopy, Stopwatch.GetTimestamp() - copyStart);
                writer.Publish();
                var version = Interlocked.Increment(ref feed.Version);
                if (version == 1 && _resources.TryGetValue(Guid.TryParse(key, out var resourceId) ? resourceId : Guid.Empty, out var resource) && resource.Type == SourceType.RemotePc)
                    AppLog.Write("RemoteMedia", $"FirstFrameConsumedByCompositor at {Stopwatch.GetTimestamp()}, source={key}");
                var consumed = Interlocked.Increment(ref feed.FramesReceived);
                ReportRate($"CompositorSource:{key}", consumed);
                Metrics?.CaptureFrame(Stopwatch.GetTimestamp() - captureStart);
            }
            else Metrics?.DropCaptureFrame();
        }
        catch { }

        var nowTicks = Stopwatch.GetTimestamp();
        var previewInterval = Stopwatch.Frequency / Math.Max(1, PreviewTargetFps);
        if (!PreviewVisible || PreviewTargetFps <= 0 || nowTicks - Interlocked.Read(ref feed.LastPreviewTicks) < previewInterval)
        { Metrics?.DropPreviewFrame(); frame.Dispose(); return; }
        if (Interlocked.Exchange(ref feed.Pending, 1) != 0) { Metrics?.DropPreviewFrame(); frame.Dispose(); return; }
        Interlocked.Exchange(ref feed.LastPreviewTicks, nowTicks);
        try { _dispatcher.BeginInvoke(() =>
        {
            try
            {
                if (!_feeds.TryGetValue(key, out var current) || !ReferenceEquals(current, feed)) return;
                if (feed.Bitmap is null || feed.Bitmap.PixelWidth != frame.Width || feed.Bitmap.PixelHeight != frame.Height)
                    feed.Bitmap = new WriteableBitmap(frame.Width, frame.Height, 96, 96, PixelFormats.Bgra32, null);
                feed.Bitmap.WritePixels(new Int32Rect(0, 0, frame.Width, frame.Height), frame.Pixels, frame.Stride, 0);
                Metrics?.PreviewFrame();
                ReportRate($"Preview:{key}", Interlocked.Increment(ref feed.PreviewUpdates));
                FrameReady?.Invoke(key);
            }
            catch (Exception ex) { SourceFailed?.Invoke(key, ex.Message); }
            finally { frame.Dispose(); Interlocked.Exchange(ref feed.Pending, 0); }
        }, DispatcherPriority.Render); }
        catch { frame.Dispose(); Interlocked.Exchange(ref feed.Pending, 0); }
    }

    private void ReportRate(string stage, long total)
    {
        var now = Stopwatch.GetTimestamp();
        lock (_rateGate)
        {
            if (!_rateWindows.TryGetValue(stage, out var previous))
            {
                _rateWindows[stage] = (now, total);
                return;
            }
            var elapsedTicks = now - previous.Ticks;
            if (elapsedTicks < Stopwatch.Frequency * 5) return;
            var elapsed = elapsedTicks / (double)Stopwatch.Frequency;
            var delta = Math.Max(0, total - previous.Count);
            var stageName = stage[..stage.IndexOf(':')];
            AppLog.Write("RemoteMedia", $"{stageName}Rate at {now}, intervalSeconds={elapsed:F2}, intervalFps={delta / elapsed:F2}, total={total}");
            _rateWindows[stage] = (now, total);
        }
    }

    private void FailFeed(string key, string message)
    {
        if (!_feeds.ContainsKey(key)) return;
        if (_feeds[key].RemoteState is not null)
        {
            ScheduleRemoteReconnect(key, _feeds[key], message);
            return;
        }
        AppLog.Write("Capture", $"Capture failed: {key}: {message}");
        StopFeed(key);
        SourceFailed?.Invoke(key, message);
        CompositionChanged?.Invoke();
    }

    private void StopFeed(string key)
    {
        if (!_feeds.Remove(key, out var feed)) return;
        feed.ReconnectCancellation?.Cancel();
        feed.ReconnectCancellation?.Dispose();
        feed.ReconnectCancellation = null;
        if (feed.Session is RemoteSrtCaptureSession)
            AppLog.Write("RemoteMedia", $"RemotePcSourceStopped at {Stopwatch.GetTimestamp()}, source={key}, compositorFrames={Interlocked.Read(ref feed.FramesReceived)}");
        var failedSession = feed.Session;
        feed.Session = null;
        failedSession?.Dispose();
        AppLog.Write("Capture", $"Capture stopped: {key}");
    }

    private void AttachSession(string key, Feed feed, IVideoCaptureSession session)
    {
        session.FrameArrived += (_, frame) => OnFrame(key, feed, session, frame);
        session.CaptureFailed += (_, ex) =>
        {
            if (!ReferenceEquals(feed.Session, session)) return;
            _dispatcher.BeginInvoke(() =>
            {
                if (ReferenceEquals(feed.Session, session)) FailFeed(key, ex.Message);
            });
        };
    }

    private void CheckRemoteFeedHealth()
    {
        if (_disposed) return;
        var now = DateTimeOffset.UtcNow;
        foreach (var (key, feed) in _feeds.ToArray())
        {
            if (feed.RemoteState is null || feed.ReconnectScheduled || now - feed.LastFrameAt < TimeSpan.FromSeconds(15)) continue;
            ScheduleRemoteReconnect(key, feed, "No remote video frames received for 15 seconds.");
        }
    }

    private void ScheduleRemoteReconnect(string key, Feed feed, string reason)
    {
        if (_disposed || feed.ReconnectScheduled || !_feeds.TryGetValue(key, out var current) || !ReferenceEquals(current, feed)) return;
        var state = feed.RemoteState;
        if (state is null) return;
        if (state.State != RemoteSourceState.Reconnecting) state.Transition(RemoteSourceState.Reconnecting);
        feed.ReconnectScheduled = true;
        feed.LastFrameAt = DateTimeOffset.UtcNow;
        feed.ReconnectCancellation?.Cancel();
        feed.ReconnectCancellation?.Dispose();
        var cancellation = feed.ReconnectCancellation = new CancellationTokenSource();
        LogRemoteState(key, feed, reason);
        // Stop the failed receiver before the retry delay. Buffered frames from
        // this session must not mark the source Live or cancel its retry.
        var failedSession = feed.Session;
        feed.Session = null;
        failedSession?.Dispose();
        _ = ReconnectRemoteAsync(key, feed, cancellation.Token);
    }

    private async Task ReconnectRemoteAsync(string key, Feed feed, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var retryAt = feed.RemoteState?.RetryAt ?? DateTimeOffset.UtcNow.AddSeconds(1);
                var delay = retryAt - DateTimeOffset.UtcNow;
                if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                await _dispatcher.InvokeAsync(() =>
                {
                    if (_disposed || cancellationToken.IsCancellationRequested ||
                        !_feeds.TryGetValue(key, out var current) || !ReferenceEquals(current, feed)) return;
                    try
                    {
                        var state = feed.RemoteState!;
                        if (state.State == RemoteSourceState.Reconnecting) state.Transition(RemoteSourceState.Connecting);
                        feed.ReconnectScheduled = false;
                        feed.LastFrameAt = DateTimeOffset.UtcNow;
                        AppLog.Write("RemoteMedia", $"RemoteReconnectAttemptStarting at {Stopwatch.GetTimestamp()}, source={key}, reconnectCount={state.ReconnectCount}");
                        if (_resources.TryGetValue(Guid.Parse(key), out var resource) && _remoteCapture is not null)
                        {
                            var replacement = _remoteCapture.Restart(resource);
                            feed.Session = replacement;
                            AttachSession(key, feed, replacement);
                            AppLog.Write("RemoteMedia", $"RemoteReconnectReceiverCreated at {Stopwatch.GetTimestamp()}, source={key}");
                            LogRemoteState(key, feed, "Receiver restarted; waiting for video.");
                        }
                        else
                        {
                            ScheduleRemoteReconnect(key, feed, "Remote source configuration is unavailable.");
                        }
                    }
                    catch (Exception ex)
                    {
                        feed.ReconnectScheduled = false;
                        AppLog.Write("RemoteMedia", $"RemoteReconnectAttemptFailed at {Stopwatch.GetTimestamp()}, source={key}, error={ex.GetType().Name}: {ex.Message}");
                        ScheduleRemoteReconnect(key, feed, $"Reconnect attempt failed ({ex.GetType().Name}): {ex.Message}");
                    }
                }).Task.ConfigureAwait(false);
                return;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private static void LogRemoteState(string key, Feed feed, string detail)
    {
        var state = feed.RemoteState;
        if (state is null) return;
        AppLog.Write("RemoteMedia", $"RemoteSourceStateChanged at {Stopwatch.GetTimestamp()}, source={key}, state={state.State}, reconnectCount={state.ReconnectCount}, retryAt={state.RetryAt:O}, detail={detail}");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _remoteHealthTimer.Stop();
        foreach (var key in _feeds.Keys.ToList()) StopFeed(key);
        _displays.Dispose();
    }
}


