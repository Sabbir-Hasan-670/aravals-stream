using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using AravalsStream.Capture.Audio;
using AravalsStream.Capture.Display;
using AravalsStream.Core.Audio;
using AravalsStream.Core.Models;
using AravalsStream.Core.Recording;
using AravalsStream.Core.RemoteCapture;
using AravalsStream.Core.Services;

namespace AravalsStream.RemoteAgent;

internal sealed class AgentMediaTransmitter : IAsyncDisposable
{
    private readonly string _ffmpegPath;
    private readonly DisplayInfo _display;
    private readonly int _fps;
    private readonly int _outputWidth;
    private readonly int _outputHeight;
    private int _bitrate;
    private readonly RemoteAgentMode _mode;
    private readonly string _passphrase;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _latestGate = new();
    private readonly SemaphoreSlim _videoSignal = new(0, 1);
    private readonly object _rateGate = new();
    private readonly Dictionary<string, (long Ticks, long Count)> _rateWindows = [];
    private readonly ContinuousAudioTimeline _audioTimeline = new();
    private DesktopDuplicationCaptureService? _captureService;
    private IDisplayCaptureSession? _capture;
    private IAudioCaptureSession? _audio;
    private DisplayFrame? _latest;
    private NamedPipeServerStream? _videoPipe, _audioPipe;
    private Process? _ffmpeg;
    private Task? _videoPump, _audioPump;
    private int _disposed;
    private long _capturedFrames;
    private long _submittedVideoFrames, _submittedAudioBlocks, _lastBytesSent;
    private int _firstCapturedAudio, _firstEncodedOutput, _firstBytesSent;
    public string EncoderId { get; private set; } = "libx264";
    public event Action<string>? StatusChanged;


    private AgentMediaTransmitter(string ffmpegPath, DisplayInfo display, int fps, int bitrate, RemoteAgentMode mode, string passphrase)
    {
        _passphrase = passphrase;
        _ffmpegPath = ffmpegPath; _display = display; _fps = fps; _bitrate = bitrate; _mode = mode;
        (_outputWidth, _outputHeight) = mode == RemoteAgentMode.LowImpact
            ? (1280, 720)
            : (display.Width, display.Height);
    }

    public static async Task<AgentMediaTransmitter> StartAsync(string ffmpegPath, DisplayInfo display,
        int fps, int bitrate, RemoteAgentMode mode, string passphrase)
    {
        if (fps is not (30 or 60)) throw new ArgumentOutOfRangeException(nameof(fps));
        if (bitrate is < 4000 or > 50000) throw new ArgumentOutOfRangeException(nameof(bitrate));
        if (!File.Exists(ffmpegPath)) throw new FileNotFoundException("The packaged FFmpeg executable is missing.", ffmpegPath);
        var transmitter = new AgentMediaTransmitter(ffmpegPath, display, fps, bitrate, mode, passphrase);
        try { await transmitter.StartCoreAsync().ConfigureAwait(false); return transmitter; }
        catch { await transmitter.DisposeAsync().ConfigureAwait(false); throw; }
    }

    private async Task StartCoreAsync()
    {
        var detected = await Task.Run(() => new EncoderDiscoveryService().DetectEncoders(_ffmpegPath)).ConfigureAwait(false);
        var encoder = RemoteCaptureProtocol.SelectH264Encoder(detected.Where(e => e.Available).Select(e => e.Id));
        if (_mode == RemoteAgentMode.Quality)
            _bitrate = Math.Min(_bitrate * 3 / 2, 50000);
        EncoderId = encoder;
        var videoPipeName = $"aravals_remote_v_{Guid.NewGuid():N}";
        var audioPipeName = $"aravals_remote_a_{Guid.NewGuid():N}";
        _videoPipe = new NamedPipeServerStream(videoPipeName, PipeDirection.Out, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous, 4 * 1024 * 1024, 0);
        _audioPipe = new NamedPipeServerStream(audioPipeName, PipeDirection.Out, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous, 256 * 1024, 0);
        var videoPath = $@"\\.\pipe\{videoPipeName}";
        var audioPath = $@"\\.\pipe\{audioPipeName}";
        var args = BuildArguments(videoPath, audioPath, encoder, _display.Width, _display.Height,
            _outputWidth, _outputHeight, _fps, _bitrate, _passphrase,
            _mode == RemoteAgentMode.LowImpact);
        var transport = new SrtConnectionOptions("0.0.0.0", RemoteCaptureProtocol.DefaultTransportPort,
            RemoteCaptureProtocol.LatencyFor(RemoteLatencyMode.Balanced), _passphrase);
        var listenerUrl = transport.ToUri().ToString().Replace("mode=caller", "mode=listener", StringComparison.Ordinal);
        _ffmpeg = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = _ffmpegPath, Arguments = args, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardError = true, RedirectStandardOutput = true
            }, EnableRaisingEvents = true
        };
        _ffmpeg.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data)) return;
            // FFmpeg errors can include destination URLs; never write the SRT URL or passphrase to logs/UI.
            var message = RemoteCaptureProtocol.Redact(e.Data);
            if (message.Contains("SRT", StringComparison.OrdinalIgnoreCase) && message.Contains("connect", StringComparison.OrdinalIgnoreCase))
                AppLog.Write("RemoteMedia", $"SrtConnected at {Stopwatch.GetTimestamp()}, pid={_ffmpeg.Id}, detail={message[..Math.Min(300, message.Length)]}");
            if (message.Contains("error", StringComparison.OrdinalIgnoreCase) || message.Contains("failed", StringComparison.OrdinalIgnoreCase))
            {
                AppLog.Write("RemoteMedia", $"SenderDiagnostic at {Stopwatch.GetTimestamp()}, pid={_ffmpeg.Id}, detail={message[..Math.Min(500, message.Length)]}");
                StatusChanged?.Invoke($"Encoder/transport: {message[..Math.Min(180, message.Length)]}");
            }
        };
        _ffmpeg.OutputDataReceived += (_, e) => OnProgress(e.Data);
        _ffmpeg.Exited += (_, _) =>
        {
            if (Volatile.Read(ref _disposed) == 0)
            {
                AppLog.Write("RemoteMedia", $"SrtSenderExited at {Stopwatch.GetTimestamp()}, pid={_ffmpeg.Id}, code={_ffmpeg.ExitCode}, capturedFrames={Interlocked.Read(ref _capturedFrames)}, submittedFrames={Interlocked.Read(ref _submittedVideoFrames)}, submittedAudioBlocks={Interlocked.Read(ref _submittedAudioBlocks)}, totalSize={Interlocked.Read(ref _lastBytesSent)}");
                StatusChanged?.Invoke($"Encoder/transport stopped (code {_ffmpeg.ExitCode}).");
            }
        };
        _videoPump = PumpVideoAsync(_stop.Token);
        _audioPump = PumpAudioAsync(_stop.Token);
        AppLog.Write("RemoteMedia", $"EncoderStartRequested at {Stopwatch.GetTimestamp()}, codec={encoder}, captureSize={_display.Width}x{_display.Height}, outputSize={_outputWidth}x{_outputHeight}, fps={_fps}, bitrateKbps={_bitrate}, mode={_mode}, audio=aac-48k-stereo, mux=mpegts");
        AppLog.Write("RemoteMedia", $"TransportStartRequested at {Stopwatch.GetTimestamp()}, role=listener, host=0.0.0.0, port={transport.Port}, mode=listener, latencyMs={transport.LatencyMilliseconds}, encryption=enabled, codec=h264+aac, mux=mpegts, url={RemoteCaptureProtocol.Redact(listenerUrl)}");
        if (!_ffmpeg.Start()) throw new InvalidOperationException("FFmpeg could not start.");
        _ffmpeg.BeginErrorReadLine();
        _ffmpeg.BeginOutputReadLine();
        AppLog.Write("RemoteMedia", $"EncoderProcessStarted and SrtSenderStarted at {Stopwatch.GetTimestamp()}, pid={_ffmpeg.Id}, listenerPort={transport.Port}");
        AppLog.Write("RemoteMedia", $"CaptureStartRequested at {Stopwatch.GetTimestamp()}, display={_display.Name}, size={_display.Width}x{_display.Height}, fps={_fps}");
        _captureService = new DesktopDuplicationCaptureService(reportTimings: true);
        _capture = _captureService.Start(_display);
        _capture.TargetFps = _fps;
        _capture.FrameArrived += OnFrame;
        _capture.CaptureFailed += (_, ex) => StatusChanged?.Invoke($"Display capture failed: {ex.Message}");
        _audio = new WasapiAudioCaptureService().Start(new AudioDeviceInfo(WasapiAudioCaptureService.DefaultOutputId,
            "System default desktop audio", false, true));
        AppLog.Write("RemoteMedia", $"CaptureStarted at {Stopwatch.GetTimestamp()}, path=D3D11-DesktopDuplication, mode=hybrid-staged, display={_display.Name}, size={_display.Width}x{_display.Height}; audioStarted=true, sampleRate=48000, channels=2");
        _audio.SamplesArrived += samples =>
        {
            if (Interlocked.Exchange(ref _firstCapturedAudio, 1) == 0)
            {
                var peak = 0f; double sumSquares = 0;
                foreach (var sample in samples) { var abs = Math.Abs(sample); peak = Math.Max(peak, abs); sumSquares += sample * sample; }
                var rms = samples.Length == 0 ? 0 : Math.Sqrt(sumSquares / samples.Length);
                AppLog.Write("RemoteMedia", $"FirstAudioSamplesCaptured at {Stopwatch.GetTimestamp()}, samples={samples.Length}, peak={peak:F6}, rms={rms:F6}, sampleRate=48000, channels=2");
            }
            _audioTimeline.Enqueue(samples);
        };
        _audio.CaptureFailed += ex => StatusChanged?.Invoke($"Desktop audio unavailable: {ex.Message}");
        StatusChanged?.Invoke("Media streaming; pairing and discovery remain active.");
        await Task.Yield();
    }

    private void OnFrame(object? sender, DisplayFrame frame)
    {
        var frameNumber = Interlocked.Increment(ref _capturedFrames);
        if (frameNumber == 1) AppLog.Write("RemoteMedia", $"FirstCapturedFrame at {Stopwatch.GetTimestamp()}, size={frame.Width}x{frame.Height}, stride={frame.Stride}");
        else if (frameNumber % 30 == 0) ReportRate("Capture", frameNumber);
        lock (_latestGate)
        {
            _latest?.Dispose();
            _latest = frame;
        }
        if (_videoSignal.CurrentCount == 0) _videoSignal.Release();
    }

    private async Task PumpVideoAsync(CancellationToken cancellationToken)
    {
        var pipe = _videoPipe;
        if (pipe is null) return;
        try
        {
            await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                while (!cancellationToken.IsCancellationRequested)
            {
                await _videoSignal.WaitAsync(cancellationToken).ConfigureAwait(false);
                DisplayFrame? frame;
                lock (_latestGate) { frame = _latest; _latest = null; }
                if (frame is null) continue;
                using (frame)
                {
                    if (!pipe.IsConnected) break;
                    var rowBytes = checked(frame.Width * 4);
                    var frameBytes = checked(rowBytes * frame.Height);
                    if (frame.Stride == rowBytes)
                        await pipe.WriteAsync(frame.Pixels.AsMemory(0, frameBytes), cancellationToken).ConfigureAwait(false);
                    else
                    {
                        var packed = System.Buffers.ArrayPool<byte>.Shared.Rent(frameBytes);
                        try
                        {
                            for (var row = 0; row < frame.Height; row++)
                                Buffer.BlockCopy(frame.Pixels, row * frame.Stride, packed, row * rowBytes, rowBytes);
                            await pipe.WriteAsync(packed.AsMemory(0, frameBytes), cancellationToken).ConfigureAwait(false);
                        }
                        finally { System.Buffers.ArrayPool<byte>.Shared.Return(packed); }
                    }
                    var submitted = Interlocked.Increment(ref _submittedVideoFrames);
                    if (submitted == 1) AppLog.Write("RemoteMedia", $"FirstFrameSubmittedToEncoder at {Stopwatch.GetTimestamp()}, bytes={frameBytes}");
                    else if (submitted % 30 == 0) ReportRate("EncodeInput", submitted);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (IOException ex) { if (!cancellationToken.IsCancellationRequested) StatusChanged?.Invoke($"Video transport closed: {ex.Message}"); }
    }

    private async Task PumpAudioAsync(CancellationToken cancellationToken)
    {
        var pipe = _audioPipe;
        if (pipe is null) return;
        try
        {
            await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
            var samples = new float[ContinuousAudioTimeline.SamplesPerBlock];
            var bytes = new byte[samples.Length * sizeof(float)];
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(ContinuousAudioTimeline.BlockDurationMilliseconds));
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                _audioTimeline.ReadBlock(samples);
                Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
                await pipe.WriteAsync(bytes.AsMemory(), cancellationToken).ConfigureAwait(false);
                var submitted = Interlocked.Increment(ref _submittedAudioBlocks);
                if (submitted == 1) AppLog.Write("RemoteMedia", $"FirstAudioSubmittedToEncoder at {Stopwatch.GetTimestamp()}, bytes={bytes.Length}, blockMs={ContinuousAudioTimeline.BlockDurationMilliseconds}");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (IOException ex) { if (!cancellationToken.IsCancellationRequested) StatusChanged?.Invoke($"Audio transport closed: {ex.Message}"); }
    }

    private void OnProgress(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        if (line.StartsWith("frame=", StringComparison.Ordinal) && long.TryParse(line.AsSpan(6).Trim(), out var frames) && frames > 0)
        {
            if (Interlocked.Exchange(ref _firstEncodedOutput, 1) == 0)
                AppLog.Write("RemoteMedia", $"FirstEncodedVideoOutput at {Stopwatch.GetTimestamp()}, frames={frames}");
            ReportRate("Encoder", frames);
        }
        if (line.StartsWith("total_size=", StringComparison.Ordinal) && long.TryParse(line.AsSpan(11).Trim(), out var bytes))
        {
            var previous = Interlocked.Exchange(ref _lastBytesSent, bytes);
            if (bytes > 0 && Interlocked.Exchange(ref _firstBytesSent, 1) == 0)
                AppLog.Write("RemoteMedia", $"FirstMediaBytesSent at {Stopwatch.GetTimestamp()}, bytes={bytes}");
            else if (bytes > previous)
                ReportRate("Transport", bytes);
        }
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
            var rate = delta / elapsed;
            var metric = stage == "Transport" ? $"intervalMbps={rate * 8 / 1_000_000:F2}" : $"intervalFps={rate:F2}";
            AppLog.Write("RemoteMedia", $"{stage}Rate at {now}, intervalSeconds={elapsed:F2}, {metric}, total={total}");
            _rateWindows[stage] = (now, total);
        }
    }

    private static string BuildArguments(string videoPath, string audioPath, string encoder, int width, int height,
        int outputWidth, int outputHeight, int fps, int bitrate, string passphrase, bool lowImpact)
    {
        var videoSettings = encoder switch
        {
            "h264_nvenc" => "-preset p1 -tune ll -rc cbr -bf 0",
            "h264_qsv" => "-preset veryfast -low_delay 1 -bf 0",
            "h264_amf" => "-quality speed -usage lowlatency -bf 0",
            _ => "-preset ultrafast -tune zerolatency -bf 0"
        };
        var latency = RemoteCaptureProtocol.LatencyFor(RemoteLatencyMode.Balanced);
        var escapedPassphrase = Uri.EscapeDataString(passphrase);
        var srt = $"srt://0.0.0.0:{RemoteCaptureProtocol.DefaultTransportPort}?mode=listener&latency={latency}&passphrase={escapedPassphrase}&pbkeylen=16";
        var scale = lowImpact
            ? $"-vf \"scale={outputWidth}:{outputHeight}:force_original_aspect_ratio=decrease:flags=fast_bilinear,pad={outputWidth}:{outputHeight}:(ow-iw)/2:(oh-ih)/2\""
            : string.Empty;
        return string.Join(' ', new[]
        {
            "-hide_banner -loglevel info -nostats",
            $"-thread_queue_size 2 -f rawvideo -pix_fmt bgra -video_size {width}x{height} -framerate {fps} -i \"{videoPath}\"",
            "-thread_queue_size 16 -f f32le -ar 48000 -ac 2 -i \"" + audioPath + "\"",
            $"-map 0:v:0 -map 1:a:0 -c:v {encoder} {videoSettings} {scale} -b:v {bitrate}k -g {fps * 2} -keyint_min {fps * 2} -pix_fmt yuv420p",
            "-c:a aac -b:a 160k -ar 48000 -ac 2 -f mpegts",
            "-progress pipe:1 -stats_period 1",
            "\"" + srt + "\""
        });
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel();
        if (_capture is not null) { _capture.FrameArrived -= OnFrame; _capture.Dispose(); }
        _audio?.Dispose();
        _videoPipe?.Dispose(); _audioPipe?.Dispose();
        lock (_latestGate) { _latest?.Dispose(); _latest = null; }
        _ffmpeg?.Kill(entireProcessTree: true);
        if (_ffmpeg is not null) { try { await _ffmpeg.WaitForExitAsync().ConfigureAwait(false); } catch { } _ffmpeg.Dispose(); }
        if (_videoPump is not null) try { await _videoPump.ConfigureAwait(false); } catch { }
        if (_audioPump is not null) try { await _audioPump.ConfigureAwait(false); } catch { }
        _captureService?.Dispose();
        _videoSignal.Dispose(); _stop.Dispose();
        StatusChanged?.Invoke("Stopped.");
    }
}
