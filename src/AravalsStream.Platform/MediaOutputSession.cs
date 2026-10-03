namespace AravalsStream.Platform;

// Owns native audio resources together with their encoder, including failed startup cleanup.
public sealed class MediaOutputSession : IAsyncDisposable
{
    private readonly FfmpegProcess _encoder;
    private readonly List<WindowsLoopback> _loopbacks = [];
    public bool HasExited => _encoder.HasExited;
    private int _disposed;
    public string? Error
    {
        get
        {
            if (_encoder.Error is { } error) return error;
            if (OperatingSystem.IsWindows()) foreach (var bridge in _loopbacks) if (bridge.Error is { } captureError) return captureError;
            return null;
        }
    }

    public MediaOutputSession(string ffmpeg, MediaPlan plan, string destination, bool recording)
    {
        try
        {
            var audio = new List<CaptureInput>();
            foreach (var input in plan.Audio)
            {
                if (input.Kind == CaptureKind.DesktopAudio && plan.Platform == DesktopPlatform.Windows && OperatingSystem.IsWindows())
                {
                    var bridge = new WindowsLoopback(input.Device); _loopbacks.Add(bridge);
                    audio.Add(input with { Kind = CaptureKind.PcmAudio, Device = bridge.PipePath });
                }
                else audio.Add(input);
            }
            _encoder = new(ffmpeg, MediaArguments.Output(plan with { Audio = audio }, destination, recording));
        }
        catch
        {
            if (OperatingSystem.IsWindows()) foreach (var bridge in _loopbacks) bridge.DisposeAsync().AsTask().GetAwaiter().GetResult();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { await _encoder.DisposeAsync(); }
        finally { if (OperatingSystem.IsWindows()) foreach (var bridge in _loopbacks) await bridge.DisposeAsync(); }
    }
}
