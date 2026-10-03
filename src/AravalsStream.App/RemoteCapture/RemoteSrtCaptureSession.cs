using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Buffers;
using AravalsStream.Capture.Display;
using AravalsStream.Capture.Video;
using AravalsStream.Core.Models;
using AravalsStream.Core.RemoteCapture;
using AravalsStream.Core.Services;

namespace AravalsStream.App.RemoteCapture;

public sealed class RemoteSrtCaptureSession : IVideoCaptureSession
{
    private sealed class PendingRemoteFrame(DisplayFrame frame, long enqueuedAt) : IDisposable
    {
        private DisplayFrame? _frame = frame;
        public long EnqueuedAt { get; } = enqueuedAt;
        public DisplayFrame? Detach() => Interlocked.Exchange(ref _frame, null);
        public void Dispose() => Detach()?.Dispose();
    }

    private readonly string _ffmpegPath;
    private readonly CancellationTokenSource _stop = new();
    private readonly NamedPipeServerStream _videoPipe;
    private readonly NamedPipeServerStream _audioPipe;
    private readonly Process _process;
    private readonly Task _videoReader;
    private readonly Task _audioReader;
    private readonly Task _framePublisher;
    private readonly LatestFrameSlot<PendingRemoteFrame> _pendingFrames = new(frame => frame.Dispose());
    private readonly SemaphoreSlim _frameSignal = new(0, 1);
    private readonly object _rateGate = new();
    private readonly Dictionary<string, (long Ticks, long Count, long Bytes)> _rateWindows = [];
    private int _disposed;
    private long _videoFrames;
    private long _audioBlocks;
    private long _videoBytes;
    private long _audioBytes;
    private long _publishedFrames;
    private long _latestFrameQueueDrops;
    private long _progressFrames;
    private long _progressBytes;
    private long _lastProgressTicks;
    private long _lastProgressFrames;
    private long _lastProgressBytes;
    private long _srtQueueWarnings;
    private long _queueAgeWindowStart = Stopwatch.GetTimestamp();
    private long _queueAgeWindowCount;
    private long _queueAgeWindowSum;
    private long _queueAgeWindowMax;

    public event EventHandler<DisplayFrame>? FrameArrived;
    public event EventHandler<Exception>? CaptureFailed;
    public event Action<float[]>? AudioSamplesArrived;
    public event Action<string>? AudioFailed;
    public event Action? Disposed;

    public RemoteSrtCaptureSession(CaptureResource resource, string ffmpegPath, string passphrase)
    {
        _ffmpegPath = ffmpegPath;
        var width = resource.FormatWidth;
        var height = resource.FormatHeight;
        if (string.IsNullOrWhiteSpace(resource.RemoteHost) || width is < 16 or > 8192 || height is < 16 or > 8192)
            throw new InvalidOperationException("Remote source has no valid host or display format.");
        if (!File.Exists(ffmpegPath)) throw new FileNotFoundException("Bundled FFmpeg is missing.", ffmpegPath);
        var videoName = $"aravals_rx_v_{Guid.NewGuid():N}";
        var audioName = $"aravals_rx_a_{Guid.NewGuid():N}";
        _videoPipe = new NamedPipeServerStream(videoName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 4 * 1024 * 1024);
        _audioPipe = new NamedPipeServerStream(audioName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 256 * 1024);
        _framePublisher = PublishLatestFramesAsync(_stop.Token);
        var pixelFormat = (Environment.GetEnvironmentVariable("ARAVALS_REMOTE_PIXFMT") ?? "bgra").Trim().ToLowerInvariant();
        if (pixelFormat is not ("bgra" or "nv12" or "bgr24")) pixelFormat = "bgra";
        _videoReader = ReadVideoAsync(width, height, pixelFormat, _stop.Token);
        _audioReader = ReadAudioAsync(_stop.Token);
        var start = new ProcessStartInfo { FileName = _ffmpegPath, UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        start.RedirectStandardOutput = true;
        var decoder = (Environment.GetEnvironmentVariable("ARAVALS_REMOTE_DECODER") ?? "software").Trim().ToLowerInvariant();
        if (decoder is not ("auto" or "d3d11va" or "cuda" or "software")) decoder = "software";
        var threads = int.TryParse(Environment.GetEnvironmentVariable("ARAVALS_REMOTE_THREADS"), out var requestedThreads) && requestedThreads is >= 1 and <= 16
            ? requestedThreads : 0;
        Add(start, "-y", "-hide_banner", "-loglevel", "info", "-nostats", "-fflags", "nobuffer", "-flags", "low_delay");
        if (decoder is "auto" or "d3d11va" or "cuda") Add(start, "-hwaccel", decoder);
        if (threads > 0) Add(start, "-threads", threads.ToString(System.Globalization.CultureInfo.InvariantCulture), "-filter_threads", threads.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Add(start, "-probesize", "2000000", "-analyzeduration", "1000000", "-progress", "pipe:1", "-stats_period", "5");
        var connection = new SrtConnectionOptions(resource.RemoteHost, RemoteCaptureProtocol.DefaultTransportPort,
            Math.Clamp(resource.RemoteLatencyMs, 20, 4000), passphrase);
        var url = connection.ToUri().ToString();
        var videoFilter = $"scale={width}:{height}:force_original_aspect_ratio=decrease,pad={width}:{height}:(ow-iw)/2:(oh-ih)/2";
        Add(start, "-i", url,
            "-map", "0:v:0", "-vf", videoFilter, "-fps_mode", "passthrough", "-f", "rawvideo", "-pix_fmt", pixelFormat, $@"\\.\pipe\{videoName}",
            "-map", "0:a:0?", "-f", "f32le", "-ar", "48000", "-ac", "2", $@"\\.\pipe\{audioName}");
        _process = new Process { StartInfo = start, EnableRaisingEvents = true };
        _process.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data) || Volatile.Read(ref _disposed) != 0) return;
            var safe = RemoteCaptureProtocol.Redact(e.Data);
            if (safe.Contains("No room to store incoming packet", StringComparison.OrdinalIgnoreCase))
            {
                var warnings = Interlocked.Increment(ref _srtQueueWarnings);
                if (warnings % 500 == 0) AppLog.Write("RemoteMedia", $"SrtReceiveQueueFull at {Stopwatch.GetTimestamp()}, repeatedWarnings={warnings}, likelyReceiverBackpressure=true");
                return;
            }
            AppLog.Write("RemoteMedia", $"ReceiverFFmpeg at {Stopwatch.GetTimestamp()}: {safe[..Math.Min(500, safe.Length)]}");
            if (safe.Contains("Input #0", StringComparison.OrdinalIgnoreCase))
                AppLog.Write("RemoteMedia", $"SrtConnected and DemuxStarted at {Stopwatch.GetTimestamp()}: {safe}");
            if (safe.Contains("Stream #0:", StringComparison.OrdinalIgnoreCase) && safe.Contains("Video:", StringComparison.OrdinalIgnoreCase))
                AppLog.Write("RemoteMedia", $"DemuxVideoStreamDetected at {Stopwatch.GetTimestamp()}: {safe[..Math.Min(300, safe.Length)]}");
            if (safe.Contains("Stream #0:", StringComparison.OrdinalIgnoreCase) && safe.Contains("Audio:", StringComparison.OrdinalIgnoreCase))
                AppLog.Write("RemoteMedia", $"DemuxAudioStreamDetected at {Stopwatch.GetTimestamp()}: {safe[..Math.Min(300, safe.Length)]}");
            if (safe.Contains("error", StringComparison.OrdinalIgnoreCase) || safe.Contains("failed", StringComparison.OrdinalIgnoreCase))
            {
                if (safe.Contains("audio", StringComparison.OrdinalIgnoreCase)) AudioFailed?.Invoke(safe[..Math.Min(220, safe.Length)]);
                AppLog.Write("RemoteMedia", $"Receiver diagnostic at {Stopwatch.GetTimestamp()}: {safe[..Math.Min(500, safe.Length)]}");
            }
        };
        _process.OutputDataReceived += (_, e) => OnProgress(e.Data);
        _process.Exited += (_, _) =>
        {
            if (Volatile.Read(ref _disposed) == 0)
            {
                AppLog.Write("RemoteMedia", $"SrtReceiverExited at {Stopwatch.GetTimestamp()}, pid={_process.Id}, code={_process.ExitCode}, videoFrames={Interlocked.Read(ref _videoFrames)}, audioBlocks={Interlocked.Read(ref _audioBlocks)}, videoBytes={Interlocked.Read(ref _videoBytes)}, audioBytes={Interlocked.Read(ref _audioBytes)}");
                CaptureFailed?.Invoke(this, new IOException($"Remote SRT decoder exited (code {_process.ExitCode})."));
            }
        };
        AppLog.Write("RemoteMedia", $"RemoteConnectRequested at {Stopwatch.GetTimestamp()}, resource={resource.Id:N}");
        AppLog.Write("RemoteMedia", $"SrtReceiverStartRequested at {Stopwatch.GetTimestamp()}, role=caller, host={connection.Host}, port={connection.Port}, mode=caller, latencyMs={connection.LatencyMilliseconds}, encryption=enabled, input=mpegts, decode=h264+aac, decoder={decoder}, decoderThreads={(threads == 0 ? "auto" : threads)}, url={RemoteCaptureProtocol.Redact(url)}");
        try
        {
            if (!_process.Start()) throw new InvalidOperationException("FFmpeg receiver failed to start.");
            AppLog.Write("RemoteMedia", $"SrtReceiverProcessStarted at {Stopwatch.GetTimestamp()}, pid={_process.Id}; local UDP source port is OS-assigned");
            _process.BeginErrorReadLine();
            _process.BeginOutputReadLine();
        }
        catch { Dispose(); throw; }
    }

    private static void Add(ProcessStartInfo start, params string[] args)
    { foreach (var arg in args) start.ArgumentList.Add(arg); }

    private void OnProgress(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        if (line.StartsWith("frame=", StringComparison.Ordinal) && long.TryParse(line.AsSpan(6).Trim(), out var frames))
            Interlocked.Exchange(ref _progressFrames, frames);
        else if (line.StartsWith("total_size=", StringComparison.Ordinal) && long.TryParse(line.AsSpan(11).Trim(), out var bytes))
            Interlocked.Exchange(ref _progressBytes, bytes);
        else if (line == "progress=continue" || line == "progress=end")
        {
            var now = Stopwatch.GetTimestamp();
            var previousTicks = Interlocked.Exchange(ref _lastProgressTicks, now);
            var progressFrameCount = Interlocked.Read(ref _progressFrames);
            var progressOutputBytes = Interlocked.Read(ref _progressBytes);
            var previousFrames = Interlocked.Exchange(ref _lastProgressFrames, progressFrameCount);
            var previousBytes = Interlocked.Exchange(ref _lastProgressBytes, progressOutputBytes);
            var elapsed = previousTicks == 0 ? 0 : (now - previousTicks) / (double)Stopwatch.Frequency;
            var fps = elapsed > 0 ? Math.Max(0, progressFrameCount - previousFrames) / elapsed : 0;
            var mbps = elapsed > 0 ? Math.Max(0, progressOutputBytes - previousBytes) / elapsed / 1_000_000 : 0;
            AppLog.Write("RemoteMedia", $"FFmpegOutputRate at {now}, intervalSeconds={elapsed:F2}, intervalFps={fps:F2}, frame={progressFrameCount}, rawBytes={progressOutputBytes}, rawMBps={mbps:F2}");
        }
    }

    private async Task ReadVideoAsync(int width, int height, string pixelFormat, CancellationToken cancellationToken)
    {
        try
        {
            await _videoPipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
            var frameBytes = checked(width * height * 4);
            var pipeFrameBytes = pixelFormat switch
            {
                "nv12" => checked(width * height * 3 / 2),
                "bgr24" => checked(width * height * 3),
                _ => frameBytes
            };
            while (!cancellationToken.IsCancellationRequested)
            {
                var pipePixels = ArrayPool<byte>.Shared.Rent(pipeFrameBytes);
                byte[]? bgraPixels = null;
                try
                {
                    await _videoPipe.ReadExactlyAsync(pipePixels.AsMemory(0, pipeFrameBytes), cancellationToken).ConfigureAwait(false);
                    var frameNumber = Interlocked.Increment(ref _videoFrames);
                    var totalVideoBytes = Interlocked.Add(ref _videoBytes, pipeFrameBytes);
                    if (frameNumber == 1) AppLog.Write("RemoteMedia", $"FirstTransportVideoBytesReceived and FirstDecodedFrame at {Stopwatch.GetTimestamp()}, pid={_process.Id}, bytes={pipeFrameBytes}, decoder=selected, size={width}x{height}, pixelFormat={pixelFormat}");
                    ReportRate("FFmpegPipeReader", frameNumber, totalVideoBytes);
                    if (pixelFormat is "nv12" or "bgr24")
                    {
                        bgraPixels = ArrayPool<byte>.Shared.Rent(frameBytes);
                        if (pixelFormat == "nv12")
                            Nv12BgraConverter.Convert(pipePixels.AsSpan(0, pipeFrameBytes), bgraPixels.AsSpan(0, frameBytes), width, height);
                        else
                            Bgr24BgraConverter.Convert(pipePixels.AsSpan(0, pipeFrameBytes), bgraPixels.AsSpan(0, frameBytes), width, height);
                    }
                    var framePixels = bgraPixels ?? pipePixels;
                    var frame = new DisplayFrame(width, height, width * 4, framePixels);
                    if (ReferenceEquals(framePixels, pipePixels)) pipePixels = [];
                    else bgraPixels = null;
                    try { OfferLatestFrame(frame); }
                    catch { frame.Dispose(); throw; }
                }
                finally
                {
                    if (pipePixels.Length > 0) ArrayPool<byte>.Shared.Return(pipePixels);
                    if (bgraPixels is { Length: > 0 }) ArrayPool<byte>.Shared.Return(bgraPixels);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex) when (Volatile.Read(ref _disposed) == 0)
        { CaptureFailed?.Invoke(this, new IOException("Remote video transport ended.", ex)); }
    }

    private void OfferLatestFrame(DisplayFrame frame)
    {
        var replacementsBefore = _pendingFrames.ReplacedCount;
        _pendingFrames.Publish(new PendingRemoteFrame(frame, Stopwatch.GetTimestamp()));
        if (_pendingFrames.ReplacedCount > replacementsBefore)
        {
            var drops = Interlocked.Increment(ref _latestFrameQueueDrops);
            if (drops % 30 == 0) AppLog.Write("RemoteMedia", $"LatestFrameQueueDrops at {Stopwatch.GetTimestamp()}, total={drops}");
        }
        if (_frameSignal.CurrentCount == 0)
        {
            try { _frameSignal.Release(); } catch (SemaphoreFullException) { }
        }
    }

    private async Task PublishLatestFramesAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await _frameSignal.WaitAsync(cancellationToken).ConfigureAwait(false);
                using var pending = _pendingFrames.Take();
                if (pending is null) continue;
                RecordQueueAge(Stopwatch.GetTimestamp() - pending.EnqueuedAt);
                var frame = pending.Detach();
                if (frame is null) continue;
                var published = Interlocked.Increment(ref _publishedFrames);
                try
                {
                    if (FrameArrived is { } handlers)
                    {
                        handlers.Invoke(this, frame);
                        if (published == 1) AppLog.Write("RemoteMedia", $"FirstFramePublishedToRemotePcSource at {Stopwatch.GetTimestamp()}");
                    }
                    else frame.Dispose();
                }
                catch { frame.Dispose(); throw; }
                ReportRate("FramePublish", published, Interlocked.Read(ref _videoBytes));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private void RecordQueueAge(long ageTicks)
    {
        Interlocked.Increment(ref _queueAgeWindowCount);
        Interlocked.Add(ref _queueAgeWindowSum, ageTicks);
        long observed;
        do
        {
            observed = Interlocked.Read(ref _queueAgeWindowMax);
            if (ageTicks <= observed) break;
        } while (Interlocked.CompareExchange(ref _queueAgeWindowMax, ageTicks, observed) != observed);

        var now = Stopwatch.GetTimestamp();
        var start = Interlocked.Read(ref _queueAgeWindowStart);
        if (now - start < Stopwatch.Frequency * 5) return;
        var count = Interlocked.Exchange(ref _queueAgeWindowCount, 0);
        var sum = Interlocked.Exchange(ref _queueAgeWindowSum, 0);
        var max = Interlocked.Exchange(ref _queueAgeWindowMax, 0);
        if (Interlocked.CompareExchange(ref _queueAgeWindowStart, now, start) != start) return;
        var averageMs = count > 0 ? sum * 1000d / Stopwatch.Frequency / count : 0;
        var maxMs = max * 1000d / Stopwatch.Frequency;
        AppLog.Write("RemoteMedia", $"LatestFrameAge at {now}, samples={count}, averageMs={averageMs:F2}, maxMs={maxMs:F2}");
    }

    private async Task ReadAudioAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _audioPipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
            var bytes = ArrayPool<byte>.Shared.Rent(48_000 / 50 * 2 * sizeof(float));
            try
            {
                var carry = 0;
                while (!cancellationToken.IsCancellationRequested)
                {
                    var read = await _audioPipe.ReadAsync(bytes.AsMemory(carry, bytes.Length - carry), cancellationToken).ConfigureAwait(false);
                    if (read == 0) throw new EndOfStreamException("Remote audio transport ended.");
                    var alignedBytes = (carry + read) & ~3;
                    var floats = new float[alignedBytes / sizeof(float)];
                    Buffer.BlockCopy(bytes, 0, floats, 0, alignedBytes);
                    var blockNumber = Interlocked.Increment(ref _audioBlocks);
                    var totalAudioBytes = Interlocked.Add(ref _audioBytes, alignedBytes);
                    carry = carry + read - alignedBytes;
                    if (carry > 0) Buffer.BlockCopy(bytes, alignedBytes, bytes, 0, carry);
                    if (floats.Length > 0)
                    {
                        if (blockNumber == 1) AppLog.Write("RemoteMedia", $"FirstDecodedAudioBlock at {Stopwatch.GetTimestamp()}, pid={_process.Id}, bytes={alignedBytes}, floatSamples={floats.Length}, sampleRate=48000, channels=2");
                        else if (blockNumber % 100 == 0) AppLog.Write("RemoteMedia", $"DecodedAudioBlocksTotal at {Stopwatch.GetTimestamp()}, pid={_process.Id}, blocks={blockNumber}, bytes={totalAudioBytes}, sampleRate=48000, channels=2");
                        AudioSamplesArrived?.Invoke(floats);
                    }
                }
            }
            finally { ArrayPool<byte>.Shared.Return(bytes); }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex) when (Volatile.Read(ref _disposed) == 0) { AudioFailed?.Invoke(ex.Message); }
    }

    private void ReportRate(string stage, long total, long bytes = 0)
    {
        var now = Stopwatch.GetTimestamp();
        lock (_rateGate)
        {
            if (!_rateWindows.TryGetValue(stage, out var previous))
            {
                _rateWindows[stage] = (now, total, bytes);
                return;
            }
            var elapsedTicks = now - previous.Ticks;
            if (elapsedTicks < Stopwatch.Frequency * 5) return;
            var elapsed = elapsedTicks / (double)Stopwatch.Frequency;
            var delta = Math.Max(0, total - previous.Count);
            var rate = delta / elapsed;
            var bytesPerSecond = Math.Max(0, bytes - previous.Bytes) / elapsed;
            var mbps = stage == "FFmpegPipeReader" ? $", rawMBps={bytesPerSecond / 1_000_000:F2}" : string.Empty;
            AppLog.Write("RemoteMedia", $"{stage}Rate at {now}, intervalSeconds={elapsed:F2}, intervalFps={rate:F2}, total={total}{mbps}");
            _rateWindows[stage] = (now, total, bytes);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel();
        _videoPipe.Dispose(); _audioPipe.Dispose();
        _pendingFrames.Clear();
        try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); } catch { }
        try { _process.WaitForExit(1000); } catch { }
        _process.Dispose();
        try { Task.WaitAll([_videoReader, _audioReader, _framePublisher], 1000); } catch { }
        _frameSignal.Dispose();
        _stop.Dispose();
        Disposed?.Invoke();
    }
}

public sealed class RemoteCaptureSessionFactory : IDisposable
{
    private readonly Dictionary<Guid, RemoteSrtCaptureSession> _sessions = [];
    private readonly PairedDeviceRegistry _registry;
    private readonly string _ffmpegPath;
    public event Action<Guid, RemoteSrtCaptureSession>? SessionReplaced;
    public RemoteCaptureSessionFactory(string ffmpegPath, PairedDeviceRegistry registry) { _ffmpegPath = ffmpegPath; _registry = registry; }

    public RemoteSrtCaptureSession Start(CaptureResource resource)
    {
        if (_sessions.TryGetValue(resource.Id, out var existing)) return existing;
        if (!Guid.TryParse(resource.RemoteDeviceId, out var deviceId)) throw new InvalidOperationException("Remote device identity is missing.");
        var secret = _registry.GetPassphraseAsync(deviceId).GetAwaiter().GetResult();
        if (string.IsNullOrWhiteSpace(secret)) throw new InvalidOperationException("Pair with the Remote Agent before adding this source.");
        var session = new RemoteSrtCaptureSession(resource, _ffmpegPath, secret);
        _sessions.Add(resource.Id, session);
        session.Disposed += () => _sessions.Remove(resource.Id);
        return session;
    }

    public RemoteSrtCaptureSession Restart(CaptureResource resource)
    {
        if (_sessions.Remove(resource.Id, out var old)) old.Dispose();
        var session = Start(resource);
        SessionReplaced?.Invoke(resource.Id, session);
        return session;
    }

    public RemoteSrtCaptureSession? Get(Guid resourceId) => _sessions.GetValueOrDefault(resourceId);
    public void Dispose() { foreach (var session in _sessions.Values.ToArray()) session.Dispose(); _sessions.Clear(); }
}
