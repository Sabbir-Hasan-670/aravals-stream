using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using AravalsStream.App.Composition;
using AravalsStream.Core.Audio;
using AravalsStream.Core.Composition;
using AravalsStream.Core.Models;
using AravalsStream.Core.Recording;
using AravalsStream.Core.Recording.Models;
using AravalsStream.Core.Services;
using AravalsStream.Core.Streaming;

namespace AravalsStream.App.Streaming;

/// <summary>
/// Encodes a single video and audio stream ONCE for a group of compatible destinations.
/// Pipes the resulting FLV stream into an EncodedPacketHub for multi-destination distribution.
/// </summary>
public sealed class SharedEncoderSession : IAsyncDisposable
{
    public Guid SessionId { get; } = Guid.NewGuid();
    private readonly EncoderCompatibilityKey _key;
    private readonly string _ffmpegPath;
    private readonly ComposedFrameHub.Lease _frameLease;
    private readonly AudioMixer _audioMixer;
    private readonly AudioOutputTap _audioTap;
    private readonly EncodedPacketHub _packetHub;
    private readonly CancellationTokenSource _cts = new();

    private Process? _encoderProcess;
    private NamedPipeServerStream? _videoPipe;
    private NamedPipeServerStream? _audioPipe;
    private NamedPipeServerStream? _encodedPipe;
    private Task? _videoPumpTask;
    private Task? _audioPumpTask;
    private Task? _hubIngestTask;
    private Task? _monitorTask;
    private bool _disposed;
    private readonly StringBuilder _ffmpegDiagnostics = new();
    private readonly object _diagnosticsLock = new();

    private long _framesEncoded;
    private long _framesDropped;
    private long _scheduledFrames;
    private long _uniqueFrames;
    private long _repeatedFrames;
    private double _outputFps;
    private double _measuredBitrateKbps;
    private long _lastOutTimeMicroseconds;
    private readonly Stopwatch _sessionStopwatch = new();
    private DateTimeOffset? _firstSubmissionUtc;
    private DateTimeOffset? _lastSubmissionUtc;

    public EncoderCompatibilityKey Key => _key;
    public EncodedPacketHub PacketHub => _packetHub;
    public bool IsActive => _encoderProcess is not null && !_encoderProcess.HasExited;

    public RecordingTelemetry Telemetry => new(
        Volatile.Read(ref _outputFps) > 0 ? Volatile.Read(ref _outputFps) : _key.FrameRate,
        Interlocked.Read(ref _framesEncoded),
        Interlocked.Read(ref _framesDropped),
        _sessionStopwatch.Elapsed,
        _key.VideoBitrateKbps,
        _key.EncoderBackend)
    {
        CadenceState = CalculateCadenceState(),
        RealtimeRatio = CalculateRealtimeRatio(),
        MediaDuration = TimeSpan.FromMicroseconds(Math.Max(0, Interlocked.Read(ref _lastOutTimeMicroseconds))),
        ScheduledFrames = Interlocked.Read(ref _scheduledFrames),
        UniqueFrames = Interlocked.Read(ref _uniqueFrames),
        RepeatedFrames = Interlocked.Read(ref _repeatedFrames),
        RecordingSchedulerDrops = Interlocked.Read(ref _framesDropped),
        MeasuredBitrateKbps = Volatile.Read(ref _measuredBitrateKbps),
        PublishWallDurationSeconds = Math.Max(0, (_lastSubmissionUtc - _firstSubmissionUtc)?.TotalSeconds ?? 0),
        FirstMediaSubmissionUtc = _firstSubmissionUtc,
        LastMediaSubmissionUtc = _lastSubmissionUtc
    };

    public SharedEncoderSession(
        EncoderCompatibilityKey key,
        string ffmpegPath,
        ComposedFrameHub.Lease frameLease,
        AudioMixer audioMixer,
        string audioTargetKey)
    {
        _key = key;
        _ffmpegPath = ffmpegPath;
        _frameLease = frameLease;
        _audioMixer = audioMixer;
        _audioTap = audioMixer.CreateOutputTap(audioTargetKey);
        _packetHub = new EncodedPacketHub();
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        AppLog.Write("SharedEncoderSession", $"Starting shared encoder session_id={SessionId} mode={_key.CanvasMode} fps={_key.FrameRate}.");
        var token = _cts.Token;
        var videoPipeName = $"aravals_shared_v_{Guid.NewGuid():N}";
        var audioPipeName = $"aravals_shared_a_{Guid.NewGuid():N}";
        var encodedPipeName = $"aravals_shared_enc_{Guid.NewGuid():N}";

        _videoPipe = new NamedPipeServerStream(
            videoPipeName,
            PipeDirection.Out,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            8 * 1024 * 1024, // 8 MB buffer to absorb pipeline jitter
            0);

        _audioPipe = new NamedPipeServerStream(
            audioPipeName,
            PipeDirection.Out,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            256 * 1024,
            0);

        _encodedPipe = new NamedPipeServerStream(
            encodedPipeName,
            PipeDirection.In,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            4 * 1024 * 1024,
            0);

        // Build single encoder command line
        var dummySettings = new RecordingSettings
        {
            Mode = _key.CanvasMode,
            Video = new VideoEncoderSettings
            {
                EncoderId = _key.EncoderBackend,
                FrameRate = _key.FrameRate,
                BitrateKbps = _key.VideoBitrateKbps,
                KeyframeIntervalSeconds = _key.KeyframeIntervalSeconds,
                Preset = _key.Preset
            },
            Audio = new AudioEncoderSettings
            {
                Codec = _key.AudioCodec,
                BitrateKbps = _key.AudioBitrateKbps
            }
        };

        var args = FfmpegArgumentBuilder.Build(
            dummySettings,
            _key.EncoderBackend,
            _key.Width,
            _key.Height,
            videoPipeName,
            audioPipeName,
            $"\\\\.\\pipe\\{encodedPipeName}",
            streaming: true,
            inputPixelFormat: _key.PixelFormat);

        _encoderProcess = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = _ffmpegPath,
                Arguments = args,
                RedirectStandardError = true,
                RedirectStandardOutput = false,
                UseShellExecute = false,
                CreateNoWindow = true
            },
            EnableRaisingEvents = true
        };

        _encoderProcess.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                lock (_diagnosticsLock)
                {
                    _ffmpegDiagnostics.AppendLine(e.Data);
                    if (_ffmpegDiagnostics.Length > 32 * 1024)
                        _ffmpegDiagnostics.Remove(0, _ffmpegDiagnostics.Length - 32 * 1024);
                }
                ParseProgress(e.Data);
            }
        };

        _encoderProcess.Start();
        _encoderProcess.BeginErrorReadLine();
        _sessionStopwatch.Restart();

        var videoConnectedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var audioConnectedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var encConnectedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        _videoPumpTask = Task.Run(async () =>
        {
            try
            {
                await _videoPipe.WaitForConnectionAsync(token).ConfigureAwait(false);
                videoConnectedTcs.TrySetResult();
                await VideoPumpAsync(token).ConfigureAwait(false);
            }
            catch (Exception ex) { videoConnectedTcs.TrySetException(ex); }
        }, token);

        _audioPumpTask = Task.Run(async () =>
        {
            try
            {
                await _audioPipe.WaitForConnectionAsync(token).ConfigureAwait(false);
                audioConnectedTcs.TrySetResult();
                await AudioPumpAsync(token).ConfigureAwait(false);
            }
            catch (Exception ex) { audioConnectedTcs.TrySetException(ex); }
        }, token);

        _hubIngestTask = Task.Run(async () =>
        {
            try
            {
                await _encodedPipe.WaitForConnectionAsync(token).ConfigureAwait(false);
                encConnectedTcs.TrySetResult();
                await _packetHub.ProcessEncoderStreamAsync(_encodedPipe, token).ConfigureAwait(false);
            }
            catch (Exception ex) { encConnectedTcs.TrySetException(ex); }
        }, token);

        // Wait for all 3 pipes to connect with 10s timeout
        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct, token);
        connectCts.CancelAfter(TimeSpan.FromSeconds(10));

        await Task.WhenAll(
            videoConnectedTcs.Task.WaitAsync(connectCts.Token),
            audioConnectedTcs.Task.WaitAsync(connectCts.Token),
            encConnectedTcs.Task.WaitAsync(connectCts.Token)
        ).ConfigureAwait(false);

        _monitorTask = Task.Run(() => MonitorProcessAsync(token), token);
    }

    private async Task VideoPumpAsync(CancellationToken ct)
    {
        var clock = new FrameClock(_key.FrameRate);
        var blankFrame = new byte[_key.Width * _key.Height * 3 / 2]; // NV12 blank frame
        long lastTick = 0;
        long lastVersion = -1;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var tick = await clock.WaitNextTickAsync(ct).ConfigureAwait(false);
                if (lastTick > 0 && tick > lastTick + 1)
                {
                    var missed = tick - lastTick - 1;
                    Interlocked.Add(ref _framesDropped, missed);
                    Interlocked.Add(ref _scheduledFrames, missed);
                }
                lastTick = tick;

                using var snapshot = _frameLease.AcquireLatest();
                var frame = snapshot?.Buffer ?? blankFrame;
                Interlocked.Increment(ref _scheduledFrames);
                if (snapshot is not null && snapshot.Version != lastVersion)
                    Interlocked.Increment(ref _uniqueFrames);
                else
                    Interlocked.Increment(ref _repeatedFrames);
                if (snapshot is not null) lastVersion = snapshot.Version;

                var submitTime = DateTimeOffset.UtcNow;
                _firstSubmissionUtc ??= submitTime;
                await _videoPipe!.WriteAsync(frame, ct).ConfigureAwait(false);
                _lastSubmissionUtc = submitTime;
                Interlocked.Increment(ref _framesEncoded);
                _frameLease.ReportOutputFrame();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            AppLog.Write("SharedEncoderSession", $"Video pump error: {ex.Message}");
        }
    }

    private async Task AudioPumpAsync(CancellationToken ct)
    {
        const int floatCount = 960 * 2;
        const int byteCount = floatCount * sizeof(float);
        var pcmBuffer = new float[floatCount];
        var byteBuffer = new byte[byteCount];
        var clock = new FrameClock(50);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                await clock.WaitNextTickAsync(ct).ConfigureAwait(false);
                // Read clears the destination first, so an empty/quiet mixer produces
                // timed silence instead of starving FFmpeg's second input and stalling mux.
                _audioTap.Read(pcmBuffer);
                Buffer.BlockCopy(pcmBuffer, 0, byteBuffer, 0, byteCount);
                if (_audioPipe is { IsConnected: true })
                    await _audioPipe.WriteAsync(byteBuffer.AsMemory(0, byteCount), ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            AppLog.Write("SharedEncoderSession", $"Audio pump error: {ex.Message}");
        }
    }

    private void ParseProgress(string line)
    {
        if (line.StartsWith("fps=", StringComparison.Ordinal) &&
            double.TryParse(line[4..].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var fps))
        {
            Volatile.Write(ref _outputFps, fps);
        }
        else if (line.StartsWith("bitrate=", StringComparison.Ordinal))
        {
            var value = line[8..].Trim().Replace("kbits/s", "", StringComparison.OrdinalIgnoreCase).Trim();
            if (double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var kbps))
            {
                Volatile.Write(ref _measuredBitrateKbps, kbps);
            }
        }
        else if (line.StartsWith("out_time_us=", StringComparison.Ordinal) &&
            long.TryParse(line[12..].Trim(), System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var outTimeUs))
        {
            Interlocked.Exchange(ref _lastOutTimeMicroseconds, Math.Max(0, outTimeUs));
        }
    }

    private double CalculateRealtimeRatio()
    {
        var wall = _sessionStopwatch.Elapsed.TotalSeconds;
        if (wall < 0.5) return 1.0;
        var mediaSeconds = Interlocked.Read(ref _lastOutTimeMicroseconds) / 1_000_000.0;
        if (mediaSeconds <= 0)
            mediaSeconds = Interlocked.Read(ref _framesEncoded) / (double)Math.Max(1, _key.FrameRate);
        return Math.Round(mediaSeconds / wall, 4);
    }

    private MediaCadenceState CalculateCadenceState() => CalculateRealtimeRatio() switch
    {
        >= 0.95 => MediaCadenceState.Healthy,
        >= 0.90 => MediaCadenceState.SlightlyBehind,
        >= 0.75 => MediaCadenceState.Degraded,
        _ => MediaCadenceState.Critical
    };

    private async Task MonitorProcessAsync(CancellationToken ct)
    {
        try
        {
            if (_encoderProcess is null) return;
            await _encoderProcess.WaitForExitAsync(ct).ConfigureAwait(false);

            if (!ct.IsCancellationRequested)
            {
                var exitCode = _encoderProcess.ExitCode;
                string diagnostics;
                lock (_diagnosticsLock) diagnostics = _ffmpegDiagnostics.ToString().Trim();
                AppLog.Write("SharedEncoderSession", $"Shared encoder exited unexpectedly (code: {exitCode}). FFmpeg output: {diagnostics}");
                _packetHub.NotifyEncoderFailed(string.IsNullOrEmpty(diagnostics)
                    ? $"Process exited with code {exitCode}"
                    : $"Process exited with code {exitCode}: {diagnostics}");
            }
        }
        catch (OperationCanceledException) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _cts.Cancel();

        try { _videoPipe?.Dispose(); } catch { }
        try { _audioPipe?.Dispose(); } catch { }
        try { _encodedPipe?.Dispose(); } catch { }

        if (_encoderProcess is not null)
        {
            try
            {
                if (!_encoderProcess.HasExited)
                {
                    _encoderProcess.Kill();
                    _encoderProcess.WaitForExit(1000);
                }
                _encoderProcess.Dispose();
            }
            catch { }
        }

        _packetHub.Dispose();
        _audioTap.Dispose();
        _cts.Dispose();
        AppLog.Write("SharedEncoderSession", $"Disposed shared encoder session_id={SessionId}.");

        if (_videoPumpTask is not null) try { await _videoPumpTask.ConfigureAwait(false); } catch { }
        if (_audioPumpTask is not null) try { await _audioPumpTask.ConfigureAwait(false); } catch { }
        if (_hubIngestTask is not null) try { await _hubIngestTask.ConfigureAwait(false); } catch { }
        if (_monitorTask is not null) try { await _monitorTask.ConfigureAwait(false); } catch { }
    }
}
