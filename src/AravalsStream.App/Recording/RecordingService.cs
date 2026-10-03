using System.IO;
using AravalsStream.App.Audio;
using AravalsStream.App.Composition;
using AravalsStream.Core.Models;
using AravalsStream.Core.Recording;
using AravalsStream.Core.Recording.Models;
using AravalsStream.Core.Services;
using AravalsStream.Core.Settings;

namespace AravalsStream.App.Recording;

public interface IRecordingService : IAsyncDisposable, IDisposable
{
    RecordingState State { get; }
    RecordingTelemetry Telemetry { get; }
    IReadOnlyList<string> LastOutputFiles { get; }
    FfmpegResolution FfmpegStatus { get; }
    IReadOnlyList<EncoderInfo> AvailableEncoders { get; }

    event Action<RecordingState>? StateChanged;
    event Action<string>? ErrorOccurred;
    event Action<RecordingTelemetry>? TelemetryUpdated;

    void RefreshFfmpeg(string? customPath = null);
    Task StartRecordingAsync(RecordingSettings settings, CancellationToken cancellationToken = default);
    void PauseRecording();
    void ResumeRecording();
    Task StopRecordingAsync(CancellationToken cancellationToken = default);
}

public sealed class RecordingService : IRecordingService
{
    private readonly IFfmpegLocator _locator;
    private readonly IEncoderDiscoveryService _encoderDiscovery;
    private readonly ISceneCompositor _compositor;
    private readonly SceneAudioEngine _audioEngine;
    private readonly ComposedFrameHub? _frameHub;
    private string? _discoveryCustomPath;
    private string? _discoveryFfmpegPath;

    private readonly List<IMediaSession> _activeSessions = [];
    private readonly List<string> _lastOutputFiles = [];
    private RecordingState _state = RecordingState.Idle;
    private System.Windows.Threading.DispatcherTimer? _telemetryTimer;
    public PerformanceMode PerformanceMode { get; set; } = PerformanceMode.Auto;

    public RecordingState State => _state;
    public IReadOnlyList<string> LastOutputFiles => _lastOutputFiles;
    public FfmpegResolution FfmpegStatus { get; private set; } = new(AravalsStream.Core.Recording.Models.FfmpegStatus.Missing, null, null, null, "Not initialized");
    public IReadOnlyList<EncoderInfo> AvailableEncoders { get; private set; } = [];

    private RecordingTelemetry? _lastTelemetry;

    private RecordingTelemetry ComputeTelemetry()
    {
        if (_activeSessions.Count == 0)
        {
            return _lastTelemetry ?? new RecordingTelemetry(0, 0, 0, TimeSpan.Zero, 0, "None");
        }

        double totalFps = _activeSessions.Average(s => s.Telemetry.CurrentFps);
        long totalFrames = _activeSessions.Sum(s => s.Telemetry.FramesEncoded);
        long totalDropped = _activeSessions.Sum(s => s.Telemetry.FramesDropped);
        var maxElapsed = _activeSessions.Max(s => s.Telemetry.ElapsedTime);
        int bitrate = _activeSessions.Sum(s => s.Telemetry.BitrateKbps);
        string encoder = _activeSessions[0].Telemetry.ActiveEncoder;

        return new RecordingTelemetry(totalFps, totalFrames, totalDropped, maxElapsed, bitrate, encoder)
        {
            ScheduledFrames = _activeSessions.Sum(s => s.Telemetry.ScheduledFrames),
            UniqueFrames = _activeSessions.Sum(s => s.Telemetry.UniqueFrames),
            RepeatedFrames = _activeSessions.Sum(s => s.Telemetry.RepeatedFrames),
            RecordingSchedulerDrops = _activeSessions.Sum(s => s.Telemetry.RecordingSchedulerDrops),
            EncoderBackpressureDrops = _activeSessions.Sum(s => s.Telemetry.EncoderBackpressureDrops),
            EncoderDrops = _activeSessions.Sum(s => s.Telemetry.EncoderDrops),
            AverageVideoPipeWriteMs = _activeSessions.Average(s => s.Telemetry.AverageVideoPipeWriteMs),
            MaximumVideoPipeWriteMs = _activeSessions.Max(s => s.Telemetry.MaximumVideoPipeWriteMs),
            VideoPipeDeadlineMisses = _activeSessions.Sum(s => s.Telemetry.VideoPipeDeadlineMisses),
            RealtimeRatio = _activeSessions.Count == 1 ? _activeSessions[0].Telemetry.RealtimeRatio : Math.Round(_activeSessions.Average(s => s.Telemetry.RealtimeRatio), 4),
            CadenceState = _activeSessions[0].Telemetry.CadenceState,
            PublishWallDurationSeconds = _activeSessions.Max(s => s.Telemetry.PublishWallDurationSeconds),
            FirstMediaSubmissionUtc = _activeSessions.Min(s => s.Telemetry.FirstMediaSubmissionUtc),
            LastMediaSubmissionUtc = _activeSessions.Max(s => s.Telemetry.LastMediaSubmissionUtc),
            StopRequestedUtc = _activeSessions[0].Telemetry.StopRequestedUtc,
            PipesClosedUtc = _activeSessions[0].Telemetry.PipesClosedUtc,
            PostStopFrames = _activeSessions.Sum(s => s.Telemetry.PostStopFrames)
        };
    }

    public RecordingTelemetry Telemetry => ComputeTelemetry();

    public event Action<RecordingState>? StateChanged;
    public event Action<string>? ErrorOccurred;
    public event Action<RecordingTelemetry>? TelemetryUpdated;

    public RecordingService(
        ISceneCompositor compositor,
        SceneAudioEngine audioEngine,
        IFfmpegLocator? locator = null,
        IEncoderDiscoveryService? encoderDiscovery = null,
        ComposedFrameHub? frameHub = null)
    {
        _compositor = compositor;
        _audioEngine = audioEngine;
        _frameHub = frameHub;
        _locator = locator ?? new FfmpegLocator();
        _encoderDiscovery = encoderDiscovery ?? new EncoderDiscoveryService();

        RefreshFfmpeg();

        _telemetryTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _telemetryTimer.Tick += (_, _) =>
        {
            if (_state == RecordingState.Recording || _state == RecordingState.Paused)
            {
                TelemetryUpdated?.Invoke(Telemetry);
            }
        };
    }

    public void RefreshFfmpeg(string? customPath = null)
    {
        FfmpegStatus = _locator.Locate(customPath);
        if (FfmpegStatus.Status == Core.Recording.Models.FfmpegStatus.Available && !string.IsNullOrEmpty(FfmpegStatus.FfmpegPath))
        {
            AvailableEncoders = _encoderDiscovery.DetectEncoders(FfmpegStatus.FfmpegPath);
            _discoveryCustomPath = NormalizePath(customPath);
            _discoveryFfmpegPath = NormalizePath(FfmpegStatus.FfmpegPath);
        }
        else
        {
            AvailableEncoders = [];
            _discoveryCustomPath = NormalizePath(customPath);
            _discoveryFfmpegPath = null;
        }
    }

    private void EnsureFfmpegDiscovery(string? customPath)
    {
        var normalizedCustomPath = NormalizePath(customPath);
        var needsRefresh = !string.Equals(normalizedCustomPath, _discoveryCustomPath, StringComparison.OrdinalIgnoreCase);
        if (!needsRefresh && FfmpegStatus.Status == Core.Recording.Models.FfmpegStatus.Available)
        {
            needsRefresh = !string.Equals(NormalizePath(FfmpegStatus.FfmpegPath), _discoveryFfmpegPath, StringComparison.OrdinalIgnoreCase);
        }
        if (needsRefresh || (FfmpegStatus.Status != Core.Recording.Models.FfmpegStatus.Available && _discoveryCustomPath is null))
        {
            RefreshFfmpeg(customPath);
        }
        else
        {
            AppLog.Write("RecordingStartup", "FFmpeg encoder discovery cache hit.");
        }
    }

    private static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try { return Path.GetFullPath(path.Trim().Trim('"')); }
        catch { return path.Trim().Trim('"'); }
    }

    public static string GenerateUniqueFilePath(string directory, OutputMode mode, string container, DateTimeOffset timestamp)
    {
        return RecordingFileNameGenerator.GenerateUniqueFilePath(directory, mode, container, timestamp);
    }

    public async Task StartRecordingAsync(RecordingSettings settings, CancellationToken cancellationToken = default)
    {
        if (_state != RecordingState.Idle)
        {
            throw new InvalidOperationException($"Cannot start recording when state is '{_state}'");
        }

        // 1. Verify FFmpeg availability
        EnsureFfmpegDiscovery(settings.CustomFfmpegPath);
        if (FfmpegStatus.Status != Core.Recording.Models.FfmpegStatus.Available || string.IsNullOrEmpty(FfmpegStatus.FfmpegPath))
        {
            var msg = FfmpegStatus.ErrorMessage ?? "FFmpeg is not installed or available on this system.";
            AppLog.Write("Recording", $"Start failed: {msg}");
            ErrorOccurred?.Invoke(msg);
            throw new InvalidOperationException(msg);
        }

        // 2. Resolve encoder (with priority selection and fallback)
        var encoderInfo = _encoderDiscovery.ResolveEncoder(settings.Video.EncoderId, AvailableEncoders);
        AppLog.Write("Recording", $"Selected encoder for session: {encoderInfo.DisplayName} ({encoderInfo.Id})");
        var effectiveSettings = settings;
        if (encoderInfo.Id == "libx264" && PerformanceMode == PerformanceMode.Eco)
        {
            effectiveSettings = new RecordingSettings
            {
                OutputDirectory = settings.OutputDirectory, Container = settings.Container, Mode = settings.Mode,
                CustomFfmpegPath = settings.CustomFfmpegPath, Audio = settings.Audio,
                Video = new VideoEncoderSettings
                {
                    EncoderId = settings.Video.EncoderId, Width = settings.Video.Width, Height = settings.Video.Height,
                    FrameRate = settings.Video.FrameRate, BitrateKbps = settings.Video.BitrateKbps,
                    KeyframeIntervalSeconds = settings.Video.KeyframeIntervalSeconds, RateControl = settings.Video.RateControl,
                    Preset = "ultrafast"
                }
            };
        }

        _state = RecordingState.Starting;
        StateChanged?.Invoke(_state);
        _lastOutputFiles.Clear();
        _lastTelemetry = null;
        _activeSessions.Clear();

        var now = DateTimeOffset.Now;
        var modesToRecord = settings.Mode == OutputMode.Both
            ? new[] { OutputMode.Horizontal, OutputMode.Vertical }
            : new[] { settings.Mode };

        try
        {
            foreach (var mode in modesToRecord)
            {
                var filePath = GenerateUniqueFilePath(settings.OutputDirectory, mode, settings.Container, now);
                var session = new FfmpegMediaSession(
                    FfmpegStatus.FfmpegPath,
                    effectiveSettings,
                    _compositor,
                    _audioEngine.Mixer,
                    mode,
                    filePath,
                    encoderInfo.Id, frameHub: _frameHub);

                session.StateChanged += sessionState =>
                {
                    if (sessionState == RecordingState.Error && _state != RecordingState.Error)
                    {
                        _state = RecordingState.Error;
                        StateChanged?.Invoke(_state);
                    }
                };

                session.ErrorOccurred += err => ErrorOccurred?.Invoke(err);

                _activeSessions.Add(session);
                _lastOutputFiles.Add(filePath);
            }

            // Start all sessions
            foreach (var session in _activeSessions)
            {
                await session.StartAsync(cancellationToken).ConfigureAwait(false);
            }

            _state = RecordingState.Recording;
            StateChanged?.Invoke(_state);
            _telemetryTimer?.Start();
        }
        catch (Exception ex)
        {
            AppLog.Write("Recording", $"Recording startup failed: {ex.Message}");
            _state = RecordingState.Error;
            StateChanged?.Invoke(_state);
            ErrorOccurred?.Invoke(ex.Message);

            // Cleanup partial sessions
            foreach (var session in _activeSessions)
            {
                try { await session.StopAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
            }
            _activeSessions.Clear();
            throw;
        }
    }

    public async Task StopRecordingAsync(CancellationToken cancellationToken = default)
    {
        if (_state != RecordingState.Recording && _state != RecordingState.Paused && _state != RecordingState.Error) return;

        _state = RecordingState.Stopping;
        StateChanged?.Invoke(_state);
        _telemetryTimer?.Stop();

        try
        {
            foreach (var session in _activeSessions)
            {
                try
                {
                    await session.StopAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    AppLog.Write("Recording", $"Error stopping session ({session.Mode}): {ex.Message}");
                }
            }
        }
        finally
        {
            _lastTelemetry = ComputeTelemetry();
            _activeSessions.Clear();
            _state = RecordingState.Idle;
            StateChanged?.Invoke(_state);
        }
    }

    public void PauseRecording()
    {
        if (_state != RecordingState.Recording) throw new InvalidOperationException($"Cannot pause recording in state '{_state}'.");
        foreach (var session in _activeSessions) session.Pause();
        _state = RecordingState.Paused;
        StateChanged?.Invoke(_state);
        TelemetryUpdated?.Invoke(Telemetry);
    }

    public void ResumeRecording()
    {
        if (_state != RecordingState.Paused) throw new InvalidOperationException($"Cannot resume recording in state '{_state}'.");
        foreach (var session in _activeSessions) session.Resume();
        _state = RecordingState.Recording;
        StateChanged?.Invoke(_state);
    }

    public async ValueTask DisposeAsync()
    {
        _telemetryTimer?.Stop();
        _telemetryTimer = null;

        if (_state == RecordingState.Recording || _state == RecordingState.Paused || _state == RecordingState.Error)
        {
            await StopRecordingAsync(CancellationToken.None).ConfigureAwait(false);
        }

        foreach (var session in _activeSessions)
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
        _activeSessions.Clear();
    }

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
