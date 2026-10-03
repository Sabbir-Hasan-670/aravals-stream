using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Globalization;
using AravalsStream.App.Composition;
using AravalsStream.Core.Composition;
using AravalsStream.Core.Audio;
using AravalsStream.Core.Models;
using AravalsStream.Core.Recording;
using AravalsStream.Core.Recording.Models;
using AravalsStream.Core.Services;

namespace AravalsStream.App.Recording;

public sealed class FfmpegMediaSession : IMediaSession
{
    private readonly string _ffmpegPath;
    private readonly RecordingSettings _settings;
    private readonly ISceneCompositor _compositor;
    private readonly AudioMixer _audioMixer;
    private readonly AudioOutputTap _audioTap;
    private readonly ComposedFrameHub.Lease? _frameLease;
    private readonly OutputMode _mode;
    private readonly string _outputPath;
    private readonly string _encoderId;
    private readonly int _width;
    private readonly int _height;
    private readonly int _outputWidth;
    private readonly int _outputHeight;
    private readonly int _fps;
    private readonly bool _streaming;

    private Process? _ffmpegProcess;
    private NamedPipeServerStream? _videoPipe;
    private NamedPipeServerStream? _audioPipe;
    private CancellationTokenSource? _cts;
    private Task? _videoPumpTask;
    private Task? _renderTask;
    private Task? _audioPumpTask;
    private byte[]? _latestFrame;
    private RecordingState _state = RecordingState.Idle;

    private long _framesEncoded;
    private long _framesDropped;
    private long _ffmpegFramesDropped;
    private long _audioChunksWritten;
    private long _scheduledFrames, _uniqueFrames, _repeatedFrames;
    private long _videoPipeWriteTicks, _videoPipeWriteMaxTicks, _videoPipeDeadlineMisses;
    private long _backpressureDrops;
    private long _lastOutTimeMicroseconds;
    private double _currentFps;
    private double _outputFps;
    private double _measuredBitrateKbps;
    private double _videoPipeWriteMs;
    private double _audioPipeWriteMs;
    private int _videoPipeBusy;
    private int _audioPipeBusy;
    private long _firstSubmissionTicks;
    private long _lastSubmissionTicks;
    private long _stopRequestedTicks;
    private long _postStopFrames;
    private DateTimeOffset? _firstSubmissionUtc;
    private DateTimeOffset? _lastSubmissionUtc;
    private DateTimeOffset? _stopRequestedUtc;
    private DateTimeOffset? _pipesClosedUtc;
    private readonly Stopwatch _sessionStopwatch = new();
    private readonly Stopwatch _startupStopwatch = new();
    private string _startupSessionId = string.Empty;
    private int _startupProgressReported;
    private int _startupVideoWriteReported;
    private int _startupAudioWriteReported;
    private int _startupVideoTickReported;
    private int _startupFrameAcquireReported;
    private int _startupResizeReported;
    private int _startupVideoWriteBeginReported;
    private int _startupAudioTickReported;
    private int _startupAudioWriteBeginReported;
    private int _startupFirstStderrReported;
    private int _startupFirstInputReported;
    private int _startupFirstPipePathReported;
    private Task? _startupDiagnosticsTask;
    private readonly object _pauseLock = new();
    private TaskCompletionSource _resumeSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly StringBuilder _stderrCapture = new();
    private readonly StringBuilder _verboseStderrCapture = new();
    private readonly StringBuilder _startupTrace = new();
    private readonly object _startupTraceLock = new();
    private int _startupTraceFlushed;
    private TaskCompletionSource _published = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public OutputMode Mode => _mode;
    public string OutputPath => _outputPath;
    public RecordingState State => _state;
    public double PublishWallDurationSeconds
    {
        get
        {
            var first = Interlocked.Read(ref _firstSubmissionTicks);
            var last = Interlocked.Read(ref _lastSubmissionTicks);
            if (first > 0 && last >= first)
                return (double)(last - first) / Stopwatch.Frequency;
            return _sessionStopwatch.Elapsed.TotalSeconds;
        }
    }

    public RecordingTelemetry Telemetry => new(
        _streaming && Volatile.Read(ref _outputFps) > 0 ? Volatile.Read(ref _outputFps) : _currentFps,
        Interlocked.Read(ref _framesEncoded),
        Interlocked.Read(ref _framesDropped) + Interlocked.Read(ref _ffmpegFramesDropped),
        _sessionStopwatch.Elapsed,
        _settings.Video.BitrateKbps,
        _encoderId)
    {
        MeasuredBitrateKbps = _streaming && Volatile.Read(ref _measuredBitrateKbps) > 0 ? Volatile.Read(ref _measuredBitrateKbps) : null,
        VideoPipeWriteMs = Volatile.Read(ref _videoPipeWriteMs), AudioPipeWriteMs = Volatile.Read(ref _audioPipeWriteMs),
        VideoQueueDepth = Volatile.Read(ref _videoPipeBusy),
        AudioQueueDepth = _audioTap.QueuedSamples / 1920 + Volatile.Read(ref _audioPipeBusy),
        ScheduledFrames = Interlocked.Read(ref _scheduledFrames),
        UniqueFrames = Interlocked.Read(ref _uniqueFrames),
        RepeatedFrames = Interlocked.Read(ref _repeatedFrames),
        RecordingSchedulerDrops = Interlocked.Read(ref _framesDropped) - Interlocked.Read(ref _backpressureDrops),
        EncoderBackpressureDrops = Interlocked.Read(ref _backpressureDrops),
        EncoderDrops = Interlocked.Read(ref _ffmpegFramesDropped),
        AverageVideoPipeWriteMs = Interlocked.Read(ref _framesEncoded) == 0 ? 0 :
            Interlocked.Read(ref _videoPipeWriteTicks) * 1000.0 / Stopwatch.Frequency / Interlocked.Read(ref _framesEncoded),
        MaximumVideoPipeWriteMs = Interlocked.Read(ref _videoPipeWriteMaxTicks) * 1000.0 / Stopwatch.Frequency,
        VideoPipeDeadlineMisses = Interlocked.Read(ref _videoPipeDeadlineMisses),
        RealtimeRatio = CalculateRealtimeRatio(),
        CadenceState = CalculateCadenceState(),
        MediaDuration = TimeSpan.FromMicroseconds(Math.Max(0, Interlocked.Read(ref _lastOutTimeMicroseconds))),
        PublishWallDurationSeconds = PublishWallDurationSeconds,
        FirstMediaSubmissionUtc = _firstSubmissionUtc,
        LastMediaSubmissionUtc = _lastSubmissionUtc,
        StopRequestedUtc = _stopRequestedUtc,
        PipesClosedUtc = _pipesClosedUtc,
        PostStopFrames = Interlocked.Read(ref _postStopFrames)
    };

    private double CalculateRealtimeRatio()
    {
        var wall = PublishWallDurationSeconds;
        if (wall < 0.5) return 1.0;
        var outUs = Interlocked.Read(ref _lastOutTimeMicroseconds);
        if (outUs > 0) return Math.Round(outUs / 1_000_000.0 / wall, 4);
        var encoded = Interlocked.Read(ref _framesEncoded);
        if (encoded > 0 && _fps > 0) return Math.Round((double)encoded / _fps / wall, 4);
        return 1.0;
    }

    private MediaCadenceState CalculateCadenceState()
    {
        var ratio = CalculateRealtimeRatio();
        return ratio switch
        {
            >= 0.95 => MediaCadenceState.Healthy,
            >= 0.90 => MediaCadenceState.SlightlyBehind,
            >= 0.75 => MediaCadenceState.Degraded,
            _ => MediaCadenceState.Critical
        };
    }

    public event Action<RecordingState>? StateChanged;
    public event Action<string>? ErrorOccurred;
    public long AudioChunksWritten => Interlocked.Read(ref _audioChunksWritten);

    public void Pause()
    {
        if (_streaming || _state != RecordingState.Recording)
            throw new InvalidOperationException($"Cannot pause output in state '{_state}'.");
        lock (_pauseLock)
        {
            _audioTap.Clear();
            _resumeSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _sessionStopwatch.Stop();
            SetState(RecordingState.Paused);
        }
    }

    public void Resume()
    {
        if (_streaming || _state != RecordingState.Paused)
            throw new InvalidOperationException($"Cannot resume output in state '{_state}'.");
        lock (_pauseLock)
        {
            _audioTap.Clear();
            _sessionStopwatch.Start();
            SetState(RecordingState.Recording);
            _resumeSignal.TrySetResult();
        }
    }

    private async Task WaitUntilResumedAsync(CancellationToken ct)
    {
        Task wait;
        lock (_pauseLock) wait = _state == RecordingState.Paused ? _resumeSignal.Task : Task.CompletedTask;
        await wait.WaitAsync(ct).ConfigureAwait(false);
    }

    public FfmpegMediaSession(
        string ffmpegPath,
        RecordingSettings settings,
        ISceneCompositor compositor,
        AudioMixer audioMixer,
        OutputMode mode,
        string outputPath,
        string encoderId, bool streaming = false, ComposedFrameHub? frameHub = null,
        string? audioTargetKey = null)
    {
        _ffmpegPath = ffmpegPath;
        _settings = settings;
        _compositor = compositor;
        _audioMixer = audioMixer;
        var audioSubscribe = Stopwatch.StartNew();
        _audioTap = _audioMixer.CreateOutputTap(audioTargetKey ?? (streaming ? "Streaming" : "Recording"));
        audioSubscribe.Stop();
        var frameSubscribe = Stopwatch.StartNew();
        _frameLease = frameHub?.Acquire(mode, settings.Video.FrameRate);
        frameSubscribe.Stop();
        AppLog.Write(streaming ? "StreamingStartup" : "RecordingStartup",
            $"mode={mode} stage=session_resources_registered audio_tap_ms={audioSubscribe.Elapsed.TotalMilliseconds:0.0} frame_hub_subscribe_ms={frameSubscribe.Elapsed.TotalMilliseconds:0.0}");
        _mode = mode;
        _outputPath = outputPath;
        _encoderId = encoderId;
        _streaming = streaming;

        var (w, h) = CanvasLayout.Size(mode);
        _outputWidth = !_streaming && settings.Video.Width > 0 ? settings.Video.Width : w;
        _outputHeight = !_streaming && settings.Video.Height > 0 ? settings.Video.Height : h;
        _width = _outputWidth;
        _height = _outputHeight;
        _fps = settings.Video.FrameRate > 0 ? settings.Video.FrameRate : 60;
    }

    private void SetState(RecordingState newState)
    {
        _state = newState;
        StateChanged?.Invoke(newState);
    }

    private void LogStartupStage(string stage)
    {
        var category = _streaming ? "StreamingStartup" : "RecordingStartup";
        var line = $"{DateTimeOffset.Now:O} [{category}] session={_startupSessionId} elapsed_ms={_startupStopwatch.Elapsed.TotalMilliseconds:0.0} stage={stage}";
        lock (_startupTraceLock)
        {
            if (Volatile.Read(ref _startupTraceFlushed) == 0)
            {
                _startupTrace.AppendLine(line);
                return;
            }
        }
        AppLog.Write(category, line);
    }

    private void FlushStartupTrace()
    {
        string trace;
        lock (_startupTraceLock)
        {
            if (Interlocked.Exchange(ref _startupTraceFlushed, 1) != 0) return;
            trace = _startupTrace.ToString();
            _startupTrace.Clear();
        }
        if (!string.IsNullOrWhiteSpace(trace))
            AppLog.Write(_streaming ? "StreamingStartup" : "RecordingStartup", trace.TrimEnd());
    }

    private static bool StartupVerboseDiagnostics =>
        string.Equals(Environment.GetEnvironmentVariable("ARAVALS_FFMPEG_STARTUP_DIAGNOSTICS"), "1", StringComparison.Ordinal);

    private void FlushVerboseStderrDiagnostics()
    {
        if (!StartupVerboseDiagnostics) return;
        try
        {
            string captured;
            lock (_stderrCapture) captured = _verboseStderrCapture.ToString();
            if (captured.Length == 0) return;
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AravalsStream", "logs");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"recording-startup-{_startupSessionId}.stderr.log");
            File.WriteAllText(path, captured, Encoding.UTF8);
            AppLog.Write("RecordingStartup", $"session={_startupSessionId} verbose_stderr_file={path} bytes={Encoding.UTF8.GetByteCount(captured)}");
        }
        catch (Exception ex) { AppLog.Write("RecordingStartup", $"session={_startupSessionId} verbose_stderr_flush_error={ex.Message}"); }
    }

    private Task ConnectPipeAndPumpAsync(
        NamedPipeServerStream? pipe,
        TaskCompletionSource connected,
        Func<CancellationToken, Task> pump,
        string connectedStage,
        CancellationToken ct)
    {
        if (pipe is null) throw new InvalidOperationException("Recording input pipe was not created.");
        return ConnectAndPumpAsync();

        async Task ConnectAndPumpAsync()
        {
            try
            {
                LogStartupStage(connectedStage == "video_pipe_connected" ? "video_wait_for_connection_started" : "audio_wait_for_connection_started");
                await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
                connected.TrySetResult();
                LogStartupStage(connectedStage);
                await pump(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                connected.TrySetException(ex);
            }
        }
    }

    private async Task LogStartupDiagnosticsAsync(CancellationToken ct)
    {
        try
        {
            TimeSpan? previousCpu = null;
            while (!ct.IsCancellationRequested && _state == RecordingState.Starting)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), ct).ConfigureAwait(false);
                if (_state != RecordingState.Starting) break;
                ThreadPool.GetAvailableThreads(out var availableWorkers, out var availableIo);
                ThreadPool.GetMaxThreads(out var maxWorkers, out var maxIo);
                ThreadPool.GetMinThreads(out var minWorkers, out var minIo);
                var process = _ffmpegProcess;
                TimeSpan? cpu = null;
                double cpuDeltaMs = 0;
                long workingSet = 0;
                int threads = 0, handles = 0;
                bool exited = true;
                try
                {
                    if (process is not null)
                    {
                        process.Refresh();
                        exited = process.HasExited;
                        if (!exited)
                        {
                            cpu = process.TotalProcessorTime;
                            if (previousCpu is { } oldCpu) cpuDeltaMs = Math.Max(0, (cpu.Value - oldCpu).TotalMilliseconds);
                            previousCpu = cpu;
                            workingSet = process.WorkingSet64;
                            threads = process.Threads.Count;
                            handles = process.HandleCount;
                        }
                    }
                }
                catch (Exception ex) { LogStartupStage($"startup_sample_error {ex.Message}"); }
                LogStartupStage($"startup_sample " +
                    $"pid={(process is { HasExited: false } ? process.Id : -1)} exited={exited} " +
                    $"process_cpu_total_ms={(cpu?.TotalMilliseconds ?? 0):0.0} process_cpu_delta_ms={cpuDeltaMs:0.0} working_set_bytes={workingSet} process_threads={threads} handles={handles} " +
                    $"pool_workers_available={availableWorkers};max={maxWorkers};min={minWorkers};pending={ThreadPool.PendingWorkItemCount} " +
                    $"pool_io_available={availableIo};max={maxIo};min={minIo}");
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { LogStartupStage($"startup_sample_error {ex.Message}"); }
    }
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_state != RecordingState.Idle)
        {
            throw new InvalidOperationException($"Cannot start recording in state '{_state}'");
        }

        SetState(RecordingState.Starting);
        _startupSessionId = Guid.NewGuid().ToString("N")[..8];
        _startupStopwatch.Restart();
        Interlocked.Exchange(ref _startupProgressReported, 0);
        Interlocked.Exchange(ref _startupVideoWriteReported, 0);
        Interlocked.Exchange(ref _startupAudioWriteReported, 0);
        Interlocked.Exchange(ref _startupVideoTickReported, 0);
        Interlocked.Exchange(ref _startupFrameAcquireReported, 0);
        Interlocked.Exchange(ref _startupResizeReported, 0);
        Interlocked.Exchange(ref _startupVideoWriteBeginReported, 0);
        Interlocked.Exchange(ref _startupAudioTickReported, 0);
        Interlocked.Exchange(ref _startupAudioWriteBeginReported, 0);
        Interlocked.Exchange(ref _startupFirstStderrReported, 0);
        Interlocked.Exchange(ref _startupFirstInputReported, 0);
        Interlocked.Exchange(ref _startupFirstPipePathReported, 0);
        LogStartupStage("start_requested");
        _published = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        var videoPipeName = $"aravals_v_{_startupSessionId}";
        var audioPipeName = $"aravals_a_{_startupSessionId}";

        // Ensure target directory exists
        var outputDir = _streaming ? null : Path.GetDirectoryName(_outputPath);
        if (!_streaming && !string.IsNullOrEmpty(outputDir))
        {
            Directory.CreateDirectory(outputDir);
        }

        try
        {
            // 1. Create named pipes for asynchronous raw streaming
            _videoPipe = new NamedPipeServerStream(
                videoPipeName,
                PipeDirection.Out,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous,
                8 * 1024 * 1024,
                0);

            _audioPipe = new NamedPipeServerStream(
                audioPipeName,
                PipeDirection.Out,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous,
                256 * 1024,
                0);
            LogStartupStage("video_and_audio_pipes_created");
            AppLog.Write("RecordingStartup", $"session={_startupSessionId} video_pipe_id={HashPipeName(videoPipeName)} audio_pipe_id={HashPipeName(audioPipeName)} both_servers_ready_before_process_start=true");

            // 2. Build FFmpeg command-line arguments
            var args = FfmpegArgumentBuilder.Build(_settings, _encoderId, _width, _height, videoPipeName, audioPipeName, _outputPath, _streaming,
                inputPixelFormat: _frameLease?.PixelFormat ?? "bgra");
            if (StartupVerboseDiagnostics) args = args.Replace("-loglevel warning", "-loglevel verbose", StringComparison.Ordinal);
            AppLog.Write(_streaming ? "Streaming" : "Recording", $"Starting FFmpeg ({_encoderId}) for {_mode}");

            _ffmpegProcess = new Process
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
            LogStartupStage("ffmpeg_process_created");
            AppLog.Write("RecordingStartup", $"session={_startupSessionId} sanitized_ffmpeg_args={FfmpegArgumentBuilder.Redact(args, _outputPath)}");

            _stderrCapture.Clear();
            _ffmpegProcess.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    if (Interlocked.Exchange(ref _startupFirstStderrReported, 1) == 0)
                        LogStartupStage("first_ffmpeg_stderr_line");
                    if (e.Data.Contains("Input #0", StringComparison.OrdinalIgnoreCase) && Interlocked.Exchange(ref _startupFirstInputReported, 1) == 0)
                        LogStartupStage("first_ffmpeg_input_0_log");
                    if ((e.Data.Contains(videoPipeName, StringComparison.OrdinalIgnoreCase) || e.Data.Contains("\\\\.\\pipe\\" + videoPipeName, StringComparison.OrdinalIgnoreCase)) &&
                        Interlocked.Exchange(ref _startupFirstPipePathReported, 1) == 0)
                        LogStartupStage("first_ffmpeg_video_pipe_path_log");
                    if (StartupVerboseDiagnostics)
                    {
                        var capturedLine = $"{DateTimeOffset.UtcNow:O} elapsed_ms={_startupStopwatch.Elapsed.TotalMilliseconds:0.0} {FfmpegArgumentBuilder.Redact(e.Data, _outputPath)}{Environment.NewLine}";
                        lock (_stderrCapture)
                        {
                            if (_verboseStderrCapture.Length < 1_000_000) _verboseStderrCapture.Append(capturedLine);
                        }
                    }
                    ParseProgress(e.Data);
                    lock (_stderrCapture)
                    {
                        if (_stderrCapture.Length < 16384)
                        {
                            _stderrCapture.AppendLine(_streaming ? FfmpegArgumentBuilder.Redact(e.Data, _outputPath) : e.Data);
                        }
                    }
                }
            };

            _ffmpegProcess.Exited += (_, _) =>
            {
                if (_state == RecordingState.Starting)
                    _published.TrySetException(new InvalidOperationException("Encoder exited before publishing media."));
                if (_state == RecordingState.Recording || _state == RecordingState.Paused)
                {
                    var exitCode = _ffmpegProcess?.ExitCode ?? -1;
                    string err;
                    lock (_stderrCapture) err = _stderrCapture.ToString();
                    AppLog.Write(_streaming ? "Streaming" : "Recording", $"FFmpeg exited unexpectedly with code {exitCode}. Stderr: {err}");
                    SetState(RecordingState.Error);
                    ErrorOccurred?.Invoke($"Encoder exited with code {exitCode}: {err}");
                }
            };

            var spawnStart = Stopwatch.GetTimestamp();
            LogStartupStage("ffmpeg_spawn_begin");
            LogStartupStage("process_start_called");
            _ffmpegProcess.Start();
            LogStartupStage("process_start_returned");
            LogStartupStage($"ffmpeg_spawn_complete_spawn_ms={Stopwatch.GetElapsedTime(spawnStart).TotalMilliseconds:0.0} pid={_ffmpegProcess.Id} args={FfmpegArgumentBuilder.Redact(args, _outputPath)}");
            _startupDiagnosticsTask = LogStartupDiagnosticsAsync(ct);
            LogStartupStage("stderr_handler_attached_begin_read_line_pending");
            _ffmpegProcess.BeginErrorReadLine();
            LogStartupStage("stderr_async_drain_attached");

            // 3. Connect named pipes and start pumps asynchronously
            // In Windows, FFmpeg opens input 0 first and reads probe data before opening input 1.
            // Starting both pump tasks concurrently ensures video data is written immediately when input 0 connects,
            // allowing FFmpeg to proceed to connect to input 1 without deadlock.
            var videoConnectedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var audioConnectedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            _sessionStopwatch.Restart();
            Interlocked.Exchange(ref _framesEncoded, 0);
            Interlocked.Exchange(ref _framesDropped, 0);
            Interlocked.Exchange(ref _ffmpegFramesDropped, 0);
            Interlocked.Exchange(ref _scheduledFrames, 0);
            Interlocked.Exchange(ref _uniqueFrames, 0);
            Interlocked.Exchange(ref _repeatedFrames, 0);
            Interlocked.Exchange(ref _videoPipeWriteTicks, 0);
            Interlocked.Exchange(ref _videoPipeWriteMaxTicks, 0);
            Interlocked.Exchange(ref _videoPipeDeadlineMisses, 0);
            Interlocked.Exchange(ref _backpressureDrops, 0);
            _currentFps = _fps;
            Volatile.Write(ref _outputFps, 0);
            Volatile.Write(ref _measuredBitrateKbps, 0);

            _videoPumpTask = ConnectPipeAndPumpAsync(_videoPipe, videoConnectedTcs, VideoPumpAsync,
                "video_pipe_connected", ct);

            if (_frameLease is null) _renderTask = Task.Run(() => RenderLoopAsync(ct), ct);

            _audioPumpTask = ConnectPipeAndPumpAsync(_audioPipe, audioConnectedTcs, AudioPumpAsync,
                "audio_pipe_connected", ct);

            // Wait for both independent pipe accepts concurrently. FFmpeg opens raw
            // video first, so the video pump starts as soon as its own pipe connects.
            var pipeConnectTimeoutSeconds = _streaming ? 8 : 30;
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(pipeConnectTimeoutSeconds));

            try
            {
                await Task.WhenAll(
                    videoConnectedTcs.Task.WaitAsync(timeoutCts.Token),
                    audioConnectedTcs.Task.WaitAsync(timeoutCts.Token)
                ).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                string stderr;
                lock (_stderrCapture) stderr = _stderrCapture.ToString();
                if (_streaming) stderr = FfmpegArgumentBuilder.Redact(stderr, _outputPath);
                LogStartupStage($"pipe_connect_timeout video={videoConnectedTcs.Task.IsCompletedSuccessfully} " +
                    $"audio={audioConnectedTcs.Task.IsCompletedSuccessfully} ffmpeg_exited={_ffmpegProcess.HasExited} stderr={stderr}");
                throw new TimeoutException($"FFmpeg did not connect both {(_streaming ? "streaming" : "recording")} input pipes within {pipeConnectTimeoutSeconds} seconds.");
            }

            if (_streaming)
            {
                using var publishTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                publishTimeout.CancelAfter(TimeSpan.FromSeconds(15));
                try { await _published.Task.WaitAsync(publishTimeout.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                { throw new TimeoutException("RTMP output did not confirm published media within 15 seconds."); }
                if (_ffmpegProcess.HasExited) throw new InvalidOperationException("RTMP output exited during connection.");
            }
            SetState(RecordingState.Recording);
            LogStartupStage("recording_state_active");
            FlushStartupTrace();
            FlushVerboseStderrDiagnostics();
            AppLog.Write(_streaming ? "Streaming" : "Recording", $"Output started for {_mode}");
        }
        catch (Exception ex)
        {
            FlushStartupTrace();
            FlushVerboseStderrDiagnostics();
            AppLog.Write(_streaming ? "Streaming" : "Recording", $"Failed to start output: {ex}");
            SetState(RecordingState.Error);
            await CleanupAsync();
            throw;
        }
    }

    private static string HashPipeName(string name)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(name)))[..12];
    private async Task RenderLoopAsync(CancellationToken ct)
    {
        var clock = new FrameClock(Math.Min(_fps, 60));
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await clock.WaitNextTickAsync(ct).ConfigureAwait(false);
                var frame = new byte[_width * _height * 4];
                _compositor.RenderComposedFrame(_mode, _width, _height, frame);
                Volatile.Write(ref _latestFrame, frame);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { AppLog.Write(_streaming ? "Streaming" : "Recording", $"Render error ({_mode}): {ex.Message}"); }
    }

    private void ParseProgress(string line)
    {
        var reportsOutput =
            line.StartsWith("out_time_us=", StringComparison.Ordinal) && long.TryParse(line[12..].Trim(), out var progressUs) && progressUs > 0 ||
            line.StartsWith("frame=", StringComparison.Ordinal) && long.TryParse(line[6..].Trim(), out var progressFrames) && progressFrames > 0;
        if (reportsOutput && Interlocked.Exchange(ref _startupProgressReported, 1) == 0)
            LogStartupStage("first_encoder_progress");

        if (line.StartsWith("out_time_us=", StringComparison.Ordinal) && long.TryParse(line[12..].Trim(), out var us) && us > 0)
        {
            Interlocked.Exchange(ref _lastOutTimeMicroseconds, us);
            if (_state == RecordingState.Starting) _published.TrySetResult();
        }
        else if (line.StartsWith("out_time_ms=", StringComparison.Ordinal) && long.TryParse(line[12..].Trim(), out var ms) && ms > 0)
        {
            // FFmpeg -progress historically outputs out_time_ms in microseconds (AV_TIME_BASE units).
            Interlocked.Exchange(ref _lastOutTimeMicroseconds, ms);
            if (_state == RecordingState.Starting) _published.TrySetResult();
        }
        else if (_state == RecordingState.Starting &&
            ((line.StartsWith("frame=", StringComparison.Ordinal) && long.TryParse(line[6..].Trim(), out var fr) && fr > 0) ||
             (line.StartsWith("total_size=", StringComparison.Ordinal) && long.TryParse(line[11..].Trim(), out var ts) && ts > 0)))
        {
            _published.TrySetResult();
        }
        if (line.StartsWith("fps=", StringComparison.Ordinal) &&
            double.TryParse(line[4..].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var fps))
            Volatile.Write(ref _outputFps, fps);
        else if (line.StartsWith("bitrate=", StringComparison.Ordinal))
        {
            var value = line[8..].Trim().Replace("kbits/s", "", StringComparison.OrdinalIgnoreCase).Trim();
            if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var kbps))
                Volatile.Write(ref _measuredBitrateKbps, kbps);
        }
        else if (line.StartsWith("drop_frames=", StringComparison.Ordinal) &&
            long.TryParse(line[12..].Trim(), out var dropped))
            Interlocked.Exchange(ref _ffmpegFramesDropped, dropped);
    }
    private async Task VideoPumpAsync(CancellationToken ct)
    {
        var clock = new FrameClock(_fps);
        var blankFrame = new byte[_frameLease is null ? _width * _height * 4 : Yuv420FrameConverter.BufferSize(_width, _height)];
        if (_frameLease is not null) Yuv420FrameConverter.FillBlack(blankFrame, _width, _height);
        byte[]? scaledFrame = null;
        var (canvasWidth, canvasHeight) = CanvasLayout.Size(_mode);
        if (_frameLease is not null && (canvasWidth != _width || canvasHeight != _height))
            scaledFrame = new byte[Yuv420FrameConverter.BufferSize(_width, _height)];
        var fpsTimer = Stopwatch.StartNew();
        long fpsFrames = 0;
        long lastTick = 0;
        long lastPipeWriteTicks = 0;
        long lastVersion = 0;
        byte[]? lastFrame = null;

        try
        {
            if (_videoPipe?.IsConnected == true)
            {
                LogStartupStage("first_composed_frame_requested");
                using var initialSnapshot = _frameLease?.AcquireLatest();
                LogStartupStage(initialSnapshot is null ? "first_composed_frame_unavailable_use_black" : "first_composed_frame_acquired");
                var initialFrame = initialSnapshot?.Buffer ?? Volatile.Read(ref _latestFrame) ?? blankFrame;
                if (initialSnapshot is not null && scaledFrame is not null)
                {
                    Yuv420FrameConverter.ResizeNearest(initialFrame, canvasWidth, canvasHeight, scaledFrame, _width, _height,
                        _frameLease?.PixelFormat ?? "yuv420p");
                    initialFrame = scaledFrame;
                }
                var initialWriteStart = Stopwatch.GetTimestamp();
                LogStartupStage("first_video_pipe_write_begin");
                Volatile.Write(ref _videoPipeBusy, 1);
                try { await _videoPipe.WriteAsync(initialFrame, 0, initialFrame.Length, ct).ConfigureAwait(false); }
                finally { Volatile.Write(ref _videoPipeBusy, 0); }
                LogStartupStage("first_video_bytes_written");
                Interlocked.Exchange(ref _scheduledFrames, 1);
                Interlocked.Exchange(ref _uniqueFrames, 1);
                Interlocked.Exchange(ref _firstSubmissionTicks, initialWriteStart);
                _firstSubmissionUtc = DateTimeOffset.UtcNow;
                Interlocked.Exchange(ref _lastSubmissionTicks, Stopwatch.GetTimestamp());
                _lastSubmissionUtc = DateTimeOffset.UtcNow;
                var initialWriteTicks = Stopwatch.GetTimestamp() - initialWriteStart;
                lastPipeWriteTicks = initialWriteTicks;
                Interlocked.Add(ref _videoPipeWriteTicks, initialWriteTicks);
                Max(ref _videoPipeWriteMaxTicks, initialWriteTicks);
                Volatile.Write(ref _videoPipeWriteMs, initialWriteTicks * 1000.0 / Stopwatch.Frequency);
                Interlocked.Increment(ref _framesEncoded);
                _frameLease?.ReportOutputFrame();
                lastVersion = initialSnapshot?.Version ?? 0;
                lastFrame = initialFrame;
            }

            while (!ct.IsCancellationRequested && _videoPipe?.IsConnected == true)
            {
                if (_state == RecordingState.Paused)
                {
                    await WaitUntilResumedAsync(ct).ConfigureAwait(false);
                    clock.Reset();
                    lastTick = 0;
                    lastPipeWriteTicks = 0;
                    fpsTimer.Restart(); fpsFrames = 0;
                }
                var tick = await clock.WaitNextTickAsync(ct).ConfigureAwait(false);
                if (Interlocked.Exchange(ref _startupVideoTickReported, 1) == 0)
                    LogStartupStage("first_video_frame_tick");
                if (lastTick > 0 && tick > lastTick + 1)
                {
                    var missed = tick - lastTick - 1;
                    Interlocked.Add(ref _framesDropped, missed);
                    var intervalTicks = Stopwatch.Frequency / _fps;
                    var pipeAttributed = Math.Min(missed, lastPipeWriteTicks / Math.Max(1, intervalTicks));
                    Interlocked.Add(ref _backpressureDrops, pipeAttributed);
                }
                Interlocked.Add(ref _scheduledFrames, lastTick > 0 ? tick - lastTick : 1);
                lastTick = tick;
                if (_state == RecordingState.Paused) continue;

                // Asynchronously write to video pipe
                using var snapshot = _frameLease?.AcquireLatest();
                if (Interlocked.Exchange(ref _startupFrameAcquireReported, 1) == 0)
                    LogStartupStage(snapshot is null ? "first_frame_acquired_empty" : "first_frame_acquired");
                var frame = snapshot?.Buffer ?? Volatile.Read(ref _latestFrame) ?? blankFrame;
                var version = snapshot?.Version ?? 0;
                if (snapshot is not null ? version != lastVersion : !ReferenceEquals(frame, lastFrame))
                    Interlocked.Increment(ref _uniqueFrames);
                else Interlocked.Increment(ref _repeatedFrames);
                lastVersion = version;
                lastFrame = frame;
                if (snapshot is not null && scaledFrame is not null)
                {
                    var resizeStart = Stopwatch.GetTimestamp();
                    Yuv420FrameConverter.ResizeNearest(frame, canvasWidth, canvasHeight, scaledFrame, _width, _height,
                        _frameLease?.PixelFormat ?? "yuv420p");
                    _frameLease?.ReportStage(PipelineStage.RecordingResize, Stopwatch.GetTimestamp() - resizeStart);
                    if (Interlocked.Exchange(ref _startupResizeReported, 1) == 0)
                        LogStartupStage("first_recording_resize_complete");
                    frame = scaledFrame;
                }
                var writeStart = Stopwatch.GetTimestamp();
                if (Interlocked.Exchange(ref _startupVideoWriteBeginReported, 1) == 0)
                    LogStartupStage("first_video_pipe_write_begin");
                Volatile.Write(ref _videoPipeBusy, 1);
                try { await _videoPipe.WriteAsync(frame, 0, frame.Length, ct).ConfigureAwait(false); }
                finally { Volatile.Write(ref _videoPipeBusy, 0); }
                if (Interlocked.Exchange(ref _startupVideoWriteReported, 1) == 0)
                    LogStartupStage("first_video_write_complete");
                var writeTicks = Stopwatch.GetTimestamp() - writeStart;
                if (Interlocked.Read(ref _firstSubmissionTicks) == 0)
                {
                    Interlocked.CompareExchange(ref _firstSubmissionTicks, writeStart, 0);
                    _firstSubmissionUtc ??= DateTimeOffset.UtcNow;
                }
                Interlocked.Exchange(ref _lastSubmissionTicks, Stopwatch.GetTimestamp());
                _lastSubmissionUtc = DateTimeOffset.UtcNow;
                if (Interlocked.Read(ref _stopRequestedTicks) > 0)
                {
                    Interlocked.Increment(ref _postStopFrames);
                }
                lastPipeWriteTicks = writeTicks;
                _frameLease?.ReportStage(PipelineStage.VideoPipeWait, writeTicks);
                Volatile.Write(ref _videoPipeWriteMs, writeTicks * 1000.0 / Stopwatch.Frequency);
                Interlocked.Add(ref _videoPipeWriteTicks, writeTicks);
                Max(ref _videoPipeWriteMaxTicks, writeTicks);
                if (writeTicks > Stopwatch.Frequency / _fps) Interlocked.Increment(ref _videoPipeDeadlineMisses);
                Interlocked.Increment(ref _framesEncoded);
                _frameLease?.ReportOutputFrame();
                fpsFrames++;

                if (fpsTimer.ElapsedMilliseconds >= 1000)
                {
                    _currentFps = Math.Round((double)fpsFrames * 1000 / fpsTimer.ElapsedMilliseconds, 1);
                    fpsFrames = 0;
                    fpsTimer.Restart();
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            AppLog.Write(_streaming ? "Streaming" : "Recording", $"Video pump error ({_mode}): {(_streaming ? ex.GetType().Name : ex.Message)}");
        }
    }

    private static void Max(ref long target, long value)
    {
        var current = Interlocked.Read(ref target);
        while (value > current)
        {
            var observed = Interlocked.CompareExchange(ref target, value, current);
            if (observed == current) return;
            current = observed;
        }
    }

    private async Task AudioPumpAsync(CancellationToken ct)
    {
        // 20ms chunk = 960 frames = 1920 floats = 7680 bytes
        const int samplesPerChunk = 960 * 2;
        const int bytesPerChunk = samplesPerChunk * 4;

        var floatBuffer = new float[samplesPerChunk];
        var byteBuffer = new byte[bytesPerChunk];
        var clock = new FrameClock(50);
        long lastTick = 0;

        try
        {
            if (_audioPipe?.IsConnected == true)
            {
                LogStartupStage("first_audio_mixer_block_requested");
                Array.Clear(byteBuffer);
                var primeAudioStart = Stopwatch.GetTimestamp();
                Volatile.Write(ref _audioPipeBusy, 1);
                try { await _audioPipe.WriteAsync(byteBuffer, 0, bytesPerChunk, ct).ConfigureAwait(false); }
                finally { Volatile.Write(ref _audioPipeBusy, 0); }
                LogStartupStage("first_audio_silence_bytes_written");
                Interlocked.Increment(ref _audioChunksWritten);
                Volatile.Write(ref _audioPipeWriteMs, (Stopwatch.GetTimestamp() - primeAudioStart) * 1000.0 / Stopwatch.Frequency);
            }
            while (!ct.IsCancellationRequested && _audioPipe?.IsConnected == true)
            {
                if (_state == RecordingState.Paused)
                {
                    await WaitUntilResumedAsync(ct).ConfigureAwait(false);
                    clock.Reset();
                    lastTick = 0;
                }
                var tick = await clock.WaitNextTickAsync(ct).ConfigureAwait(false);
                if (Interlocked.Exchange(ref _startupAudioTickReported, 1) == 0)
                    LogStartupStage("first_audio_chunk_tick");
                if (_state == RecordingState.Paused) continue;
                await WaitUntilResumedAsync(ct).ConfigureAwait(false);

                var elapsedTicks = lastTick > 0 ? (int)Math.Min(20, Math.Max(1, tick - lastTick)) : 1;
                lastTick = tick;

                for (var i = 0; i < elapsedTicks; i++)
                {
                    // Read from master audio mixer (fills with silence if inputs are quiet/disconnected)
                    _audioTap.Read(floatBuffer);
                    if (Interlocked.Exchange(ref _startupAudioTickReported, 1) == 0) LogStartupStage("first_audio_mixer_block_acquired");

                    Buffer.BlockCopy(floatBuffer, 0, byteBuffer, 0, bytesPerChunk);

                    var writeStart = Stopwatch.GetTimestamp();
                    Volatile.Write(ref _audioPipeBusy, 1);
                    if (Interlocked.Exchange(ref _startupAudioWriteBeginReported, 1) == 0)
                        LogStartupStage("first_audio_pipe_write_begin");
                    try { await _audioPipe.WriteAsync(byteBuffer, 0, bytesPerChunk, ct).ConfigureAwait(false); }
                    finally { Volatile.Write(ref _audioPipeBusy, 0); }
                    if (Interlocked.Exchange(ref _startupAudioWriteReported, 1) == 0)
                        LogStartupStage("first_audio_write_complete");
                    var audioWriteTicks = Stopwatch.GetTimestamp() - writeStart;
                    _frameLease?.ReportStage(PipelineStage.AudioPipeWait, audioWriteTicks);
                    Volatile.Write(ref _audioPipeWriteMs, audioWriteTicks * 1000.0 / Stopwatch.Frequency);
                    Interlocked.Increment(ref _audioChunksWritten);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            AppLog.Write(_streaming ? "Streaming" : "Recording", $"Audio pump error ({_mode}): {(_streaming ? ex.GetType().Name : ex.Message)}");
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_state != RecordingState.Recording && _state != RecordingState.Paused && _state != RecordingState.Error) return;

        SetState(RecordingState.Stopping);
        _stopRequestedUtc ??= DateTimeOffset.UtcNow;
        Interlocked.CompareExchange(ref _stopRequestedTicks, Stopwatch.GetTimestamp(), 0);
        AppLog.Write(_streaming ? "Streaming" : "Recording", $"Stopping output for {_mode}...");

        try
        {
            // Signal cancellation to stop pumps
        _cts?.Cancel();
            _resumeSignal.TrySetResult();

            if (_videoPumpTask is not null)
            {
                try { await _videoPumpTask.ConfigureAwait(false); } catch { }
            }
            if (_renderTask is not null)
            {
                try { await _renderTask.ConfigureAwait(false); } catch { }
            }
            if (_audioPumpTask is not null)
            {
                try { await _audioPumpTask.ConfigureAwait(false); } catch { }
            }

            // Closing pipes signals EOF to FFmpeg
            ClosePipes();

            // Wait for FFmpeg to finish muxing container
            if (_ffmpegProcess is not null && !_ffmpegProcess.HasExited)
            {
                using var waitCts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                try
                {
                    await _ffmpegProcess.WaitForExitAsync(waitCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    AppLog.Write("Recording", "FFmpeg did not exit within 8s; terminating process.");
                    try { _ffmpegProcess.Kill(); } catch { }
                }
            }

            _sessionStopwatch.Stop();
            AppLog.Write(_streaming ? "Streaming" : "Recording",
                $"Timing {_mode}: wall={_sessionStopwatch.Elapsed.TotalSeconds:0.000}s publish_wall={PublishWallDurationSeconds:0.000}s " +
                $"video_frames={Interlocked.Read(ref _framesEncoded)} audio_chunks={Interlocked.Read(ref _audioChunksWritten)} " +
                $"scheduled={Interlocked.Read(ref _scheduledFrames)} unique={Interlocked.Read(ref _uniqueFrames)} " +
                $"repeated={Interlocked.Read(ref _repeatedFrames)} " +
                $"scheduled_drops={Interlocked.Read(ref _framesDropped) - Interlocked.Read(ref _backpressureDrops)} " +
                $"pipe_backpressure_drops={Interlocked.Read(ref _backpressureDrops)} encoder_drops={Interlocked.Read(ref _ffmpegFramesDropped)} " +
                $"post_stop_frames={Interlocked.Read(ref _postStopFrames)} " +
                $"pipe_avg_ms={Telemetry.AverageVideoPipeWriteMs:0.00} pipe_max_ms={Telemetry.MaximumVideoPipeWriteMs:0.00} " +
                $"pipe_deadline_misses={Interlocked.Read(ref _videoPipeDeadlineMisses)} " +
                $"composition_drops={_frameLease?.DroppedCompositionFrames ?? 0} composition_ms={_frameLease?.CompositionMilliseconds ?? 0:0.00}");
            SetState(RecordingState.Idle);
            AppLog.Write(_streaming ? "Streaming" : "Recording", $"Output stopped for {_mode}");
        }
        finally
        {
            await CleanupAsync();
            _audioTap.Dispose();
            _frameLease?.Dispose();
        }
    }

    private void ClosePipes()
    {
        _pipesClosedUtc ??= DateTimeOffset.UtcNow;
        try { _videoPipe?.Flush(); } catch { }
        try { _videoPipe?.Dispose(); } catch { }
        _videoPipe = null;

        try { _audioPipe?.Flush(); } catch { }
        try { _audioPipe?.Dispose(); } catch { }
        _audioPipe = null;
    }

    private Task CleanupAsync()
    {
        ClosePipes();

        if (_ffmpegProcess is not null)
        {
            try
            {
                if (!_ffmpegProcess.HasExited)
                {
                    _ffmpegProcess.Kill();
                }
                _ffmpegProcess.Dispose();
            }
            catch { }
            _ffmpegProcess = null;
        }

        _cts?.Dispose();
        _cts = null;
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_state == RecordingState.Recording || _state == RecordingState.Paused || _state == RecordingState.Error)
        {
            await StopAsync();
        }
        else
        {
            await CleanupAsync();
        }
        _audioTap.Dispose();
        _frameLease?.Dispose();
    }
}


