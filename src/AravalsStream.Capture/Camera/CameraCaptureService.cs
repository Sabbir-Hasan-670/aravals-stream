using System.Runtime.InteropServices;
using AravalsStream.Capture.Display;
using AravalsStream.Core.Services;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.MediaProperties;
using WinRT;

namespace AravalsStream.Capture.Camera;

public sealed record CameraInfo(string Id, string Name, int Index) { public string Label => Name; }
public sealed record CameraFormat(int Width, int Height, int FramesPerSecond, string Subtype = "")
{
    public override string ToString() => $"{Width}×{Height} @ {FramesPerSecond} fps{(Subtype.Length > 0 ? $" · {Subtype}" : "")}";
}
public interface ICameraCaptureService
{
    Task<IReadOnlyList<CameraInfo>> EnumerateAsync();
    Task<IReadOnlyList<CameraFormat>> EnumerateFormatsAsync(CameraInfo camera);
    ICameraCaptureSession Start(CameraInfo camera, CameraFormat? preferredFormat = null);
}
public interface ICameraCaptureSession : Video.IVideoCaptureSession { CameraFormat? ActualFormat { get; } }

public sealed class CameraCaptureService : ICameraCaptureService
{
    public async Task<IReadOnlyList<CameraInfo>> EnumerateAsync() => (await MediaFrameSourceGroup.FindAllAsync())
        .Where(g => ColorSource(g) is not null).Select((g, i) => new CameraInfo(g.Id, g.DisplayName, i)).ToList();

    public async Task<IReadOnlyList<CameraFormat>> EnumerateFormatsAsync(CameraInfo camera)
    {
        var group = await FindGroup(camera.Id);
        using var capture = await Initialize(group, false);
        var source = capture.FrameSources[ColorSource(group)!.Id];
        return source.SupportedFormats.Where(f => f.VideoFormat is not null).Select(ToFormat)
            .Distinct().OrderBy(f => f.Width * f.Height).ThenBy(f => f.FramesPerSecond).ToList();
    }

    public ICameraCaptureSession Start(CameraInfo camera, CameraFormat? preferredFormat = null) => new NativeCameraSession(camera, preferredFormat);
    internal static MediaFrameSourceInfo? ColorSource(MediaFrameSourceGroup group) => group.SourceInfos
        .FirstOrDefault(i => i.SourceKind == MediaFrameSourceKind.Color &&
            i.MediaStreamType is MediaStreamType.VideoPreview or MediaStreamType.VideoRecord);
    internal static async Task<MediaFrameSourceGroup> FindGroup(string id) =>
        (await MediaFrameSourceGroup.FindAllAsync()).FirstOrDefault(g => g.Id == id && ColorSource(g) is not null)
        ?? throw new InvalidOperationException("Video capture device is disconnected.");
    internal static async Task<MediaCapture> Initialize(MediaFrameSourceGroup group, bool exclusive = true)
    {
        var capture = new MediaCapture();
        try
        {
            await capture.InitializeAsync(new MediaCaptureInitializationSettings
            { SourceGroup = group, StreamingCaptureMode = StreamingCaptureMode.Video,
              MemoryPreference = MediaCaptureMemoryPreference.Cpu,
              SharingMode = exclusive ? MediaCaptureSharingMode.ExclusiveControl : MediaCaptureSharingMode.SharedReadOnly });
            return capture;
        }
        catch { capture.Dispose(); throw; }
    }
    internal static CameraFormat ToFormat(MediaFrameFormat format) => new(
        (int)format.VideoFormat.Width, (int)format.VideoFormat.Height,
        format.FrameRate.Denominator == 0 ? 0 : (int)Math.Round((double)format.FrameRate.Numerator / format.FrameRate.Denominator),
        format.Subtype);
}

internal sealed class NativeCameraSession : ICameraCaptureSession
{
    [ComImport, Guid("5B0D3235-4DBA-4D44-865C-6F1D3F5294D0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMemoryBufferByteAccess { void GetBuffer(out nint buffer, out uint capacity); }
    private MediaCapture? _capture;
    private MediaFrameReader? _reader;
    private readonly CancellationTokenSource _stop = new();
    private bool _disposed;
    private int _failed;
    private long _lastFrameTicks;
    public CameraFormat? ActualFormat { get; private set; }
    public event EventHandler<DisplayFrame>? FrameArrived;
    public event EventHandler<Exception>? CaptureFailed;
    public NativeCameraSession(CameraInfo camera, CameraFormat? preferred) => _ = OpenAsync(camera, preferred);

    private async Task OpenAsync(CameraInfo camera, CameraFormat? preferred)
    {
        try
        {
            var group = await CameraCaptureService.FindGroup(camera.Id);
            if (_stop.IsCancellationRequested) return;
            var capture = await CameraCaptureService.Initialize(group);
            if (_stop.IsCancellationRequested) { capture.Dispose(); return; }
            _capture = capture;
            capture.Failed += OnCaptureFailed;
            var source = capture.FrameSources[CameraCaptureService.ColorSource(group)!.Id];
            if (preferred is not null)
            {
                var format = source.SupportedFormats.FirstOrDefault(f =>
                    CameraCaptureService.ToFormat(f) is { } actual && actual.Width == preferred.Width &&
                    actual.Height == preferred.Height && actual.FramesPerSecond == preferred.FramesPerSecond &&
                    (preferred.Subtype.Length == 0 || actual.Subtype == preferred.Subtype));
                if (format is null) throw new InvalidOperationException($"Camera format unavailable: {preferred}");
                await source.SetFormatAsync(format);
            }
            else
            {
                var recommended = CameraFormatSelector.Recommended(source.SupportedFormats.Select(f =>
                { var c = CameraCaptureService.ToFormat(f); return new VideoFormatChoice(c.Width, c.Height, c.FramesPerSecond, c.Subtype); }));
                var format = source.SupportedFormats.FirstOrDefault(f => recommended is not null &&
                    CameraCaptureService.ToFormat(f) is { } actual && actual.Width == recommended.Width &&
                    actual.Height == recommended.Height && actual.FramesPerSecond == recommended.FramesPerSecond &&
                    actual.Subtype == recommended.Subtype);
                if (format is not null) await source.SetFormatAsync(format);
            }
            ActualFormat = CameraCaptureService.ToFormat(source.CurrentFormat);
            var reader = await capture.CreateFrameReaderAsync(source);
            if (_stop.IsCancellationRequested) { reader.Dispose(); return; }
            _reader = reader;
            reader.FrameArrived += OnFrame;
            var result = await reader.StartAsync();
            if (result != MediaFrameReaderStartStatus.Success) throw new InvalidOperationException($"Video capture device start failed: {result}");
            Interlocked.Exchange(ref _lastFrameTicks, DateTimeOffset.UtcNow.Ticks);
            _ = WatchFramesAsync(_stop.Token);
        }
        catch (Exception ex) when (!_stop.IsCancellationRequested) { ReportFailure(ex); }
    }

    private async Task WatchFramesAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(1000, cancellationToken);
                if (DateTimeOffset.UtcNow.Ticks - Interlocked.Read(ref _lastFrameTicks) > TimeSpan.FromSeconds(5).Ticks)
                { ReportFailure(new InvalidOperationException("Video capture device stopped delivering frames.")); return; }
            }
        }
        catch (OperationCanceledException) { }
    }

    private void OnCaptureFailed(MediaCapture sender, MediaCaptureFailedEventArgs args) =>
        ReportFailure(new InvalidOperationException($"Video capture device failed: {args.Message}"));

    private void ReportFailure(Exception error)
    {
        if (!_disposed && Interlocked.Exchange(ref _failed, 1) == 0) CaptureFailed?.Invoke(this, error);
    }

    private void OnFrame(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
    {
        if (_disposed) return;
        try
        {
            using var frame = sender.TryAcquireLatestFrame();
            var input = frame?.VideoMediaFrame?.SoftwareBitmap;
            if (input is null) return;
            Interlocked.Exchange(ref _lastFrameTicks, DateTimeOffset.UtcNow.Ticks);
            using var converted = input.BitmapPixelFormat == BitmapPixelFormat.Bgra8
                ? SoftwareBitmap.Copy(input) : SoftwareBitmap.Convert(input, BitmapPixelFormat.Bgra8);
            using var locked = converted.LockBuffer(BitmapBufferAccessMode.Read);
            var plane = locked.GetPlaneDescription(0);
            using var reference = locked.CreateReference();
            var access = reference.As<IMemoryBufferByteAccess>();
            access.GetBuffer(out var pointer, out _);
            var stride = converted.PixelWidth * 4;
            var pixels = System.Buffers.ArrayPool<byte>.Shared.Rent(stride * converted.PixelHeight);
            for (var y = 0; y < converted.PixelHeight; y++)
                Marshal.Copy(nint.Add(pointer, plane.StartIndex + y * plane.Stride), pixels, y * stride, stride);
            var result = new DisplayFrame(converted.PixelWidth, converted.PixelHeight, stride, pixels);
            if (FrameArrived is { } consumer) consumer(this, result); else result.Dispose();
        }
        catch (Exception ex) when (!_disposed) { ReportFailure(ex); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _stop.Cancel();
        if (_reader is not null)
        {
            _reader.FrameArrived -= OnFrame;
            try { _reader.StopAsync().AsTask().GetAwaiter().GetResult(); } catch { }
            _reader.Dispose();
        }
        if (_capture is not null) { _capture.Failed -= OnCaptureFailed; _capture.Dispose(); }
        _stop.Dispose();
    }
}
