using AravalsStream.App.Recording;
using AravalsStream.App.Composition;
using AravalsStream.Core.Audio;
using AravalsStream.Core.Models;
using AravalsStream.Core.Recording;
using AravalsStream.Core.Recording.Models;
using AravalsStream.Core.Services;
using AravalsStream.Core.Streaming;

namespace AravalsStream.App.Streaming;

public interface IStreamingOutput : IAsyncDisposable
{
    Destination Destination { get; }
    DestinationStatus Status { get; }
    RecordingTelemetry? Telemetry { get; }
    NetworkHealthReport Health { get; }
    string? LastError { get; }
    int ReconnectCount { get; }
    PublisherSession.PublisherDiagnostics? PublisherDiagnostics { get; }
    TimeSpan? RetryIn { get; }
    Task StartAsync(CancellationToken ct = default);
    Task StopAsync();
}

public sealed class StreamingOutput : IStreamingOutput
{
    private readonly string _ffmpeg;
    private readonly string _encoder;
    private readonly string _key;
    private readonly ISceneCompositor _compositor;
    private readonly ComposedFrameHub _frameHub;
    private readonly AudioMixer _mixer;
    private readonly StreamingOutputGroupManager? _groupManager;
    private CancellationTokenSource? _cts;
    private FfmpegMediaSession? _session;
    private PublisherSession? _sharedPublisher;
    private PublisherSession.PublisherDiagnostics? _lastPublisherDiagnostics;
    private SharedEncoderSession? _sharedEncoder;
    private Task? _monitor;
    private RecordingTelemetry? _lastTelemetry;
    private DestinationStatus _status = DestinationStatus.Offline;
    private NetworkHealthReport _health = new();
    private string? _lastError;
    private int _reconnectCount;
    private TimeSpan? _retryIn;

    public Destination Destination { get; }
    public DestinationStatus Status => _status;
    public RecordingTelemetry? Telemetry => _sharedEncoder?.Telemetry ?? _session?.Telemetry ?? _lastTelemetry;
    public NetworkHealthReport Health => _sharedPublisher?.Health ?? _health;
    public AdaptiveBitrateController? AdaptiveController { get; set; }
    public string? LastError => _sharedPublisher?.LastError ?? _lastError;
    public int ReconnectCount => _sharedPublisher?.ReconnectCount ?? _reconnectCount;
    public PublisherSession.PublisherDiagnostics? PublisherDiagnostics => _sharedPublisher?.Diagnostics ?? _lastPublisherDiagnostics;
    public TimeSpan? RetryIn => _sharedPublisher?.RetryIn ?? _retryIn;
    public event Action<DestinationStatus>? StatusChanged;

    public StreamingOutput(Destination destination, string ffmpeg, string encoder, string key,
        ISceneCompositor compositor, AudioMixer mixer, ComposedFrameHub frameHub,
        StreamingOutputGroupManager? groupManager = null)
    {
        Destination = destination; _ffmpeg = ffmpeg; _encoder = encoder; _key = key;
        _compositor = compositor; _mixer = mixer; _frameHub = frameHub; _groupManager = groupManager;
        _health.TargetBitrateKbps = destination.VideoBitrateKbps;
        _health.TargetFps = destination.FrameRate;
    }

    private void SetStatus(DestinationStatus status)
    {
        _status = status; Destination.Status = status; StatusChanged?.Invoke(status);
    }

    private string IngestUrl => Destination.StreamUrl.TrimEnd('/') + (string.IsNullOrEmpty(_key) ? "" : "/" + _key);

    private RecordingSettings MediaSettings => new()
    {
        Mode = Destination.OutputMode,
        Video = new VideoEncoderSettings { EncoderId = _encoder, FrameRate = Destination.FrameRate,
            BitrateKbps = Destination.VideoBitrateKbps, KeyframeIntervalSeconds = Destination.KeyframeIntervalSeconds },
        Audio = new AudioEncoderSettings { Codec = "aac", BitrateKbps = Destination.AudioBitrateKbps }
    };

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (_cts != null) throw new InvalidOperationException("Output already started.");
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _cts.Token;
        if (_groupManager is not null)
        {
            await StartSharedAsync(token).ConfigureAwait(false);
            _monitor = Task.Run(() => MonitorSharedAsync(token), token);
            return;
        }

        try
        {
            await ConnectAsync(token);
        }
        catch (Exception ex)
        {
            if (!Destination.AutoReconnect || token.IsCancellationRequested)
            {
                SetStatus(DestinationStatus.Error);
                _cts.Dispose();
                _cts = null;
                throw;
            }
            AppLog.Write("Streaming", $"Initial connection failed for {Destination.Id}: {ex.GetType().Name}; starting reconnect loop.");
            SetStatus(DestinationStatus.Reconnecting);
        }
        _monitor = Task.Run(() => MonitorAsync(token));
    }

    private async Task StartSharedAsync(CancellationToken ct)
    {
        SetStatus(DestinationStatus.Connecting);
        try
        {
            var (publisher, encoder) = await _groupManager!.AcquireAsync(Destination, _encoder, ct).ConfigureAwait(false);
            _sharedPublisher = publisher;
            _lastPublisherDiagnostics = null;
            _sharedEncoder = encoder;
            publisher.StatusChanged += SharedPublisher_StatusChanged;
            _lastError = null;
            SetStatus(publisher.Status);
        }
        catch (Exception ex)
        {
            _lastError = FfmpegArgumentBuilder.Redact(ex.Message, IngestUrl);
            SetStatus(DestinationStatus.Error);
            _cts?.Dispose();
            _cts = null;
            throw;
        }
    }

    private void SharedPublisher_StatusChanged(DestinationStatus status) => SetStatus(status);

    private async Task MonitorSharedAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(500, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            var publisher = _sharedPublisher;
            var telemetry = _sharedEncoder?.Telemetry;
            if (publisher is null) continue;
            _health = NetworkHealthEvaluator.Evaluate(
                Destination.VideoBitrateKbps,
                publisher.Health.MeasuredBitrateKbps,
                telemetry?.FramesDropped ?? 0,
                telemetry?.FramesEncoded ?? 0,
                telemetry?.CurrentFps ?? Destination.FrameRate,
                Destination.FrameRate,
                publisher.ReconnectCount,
                publisher.Status == DestinationStatus.Reconnecting,
                telemetry?.RealtimeRatio ?? 1.0,
                telemetry?.CadenceState ?? MediaCadenceState.Healthy);
        }
    }

    private async Task ConnectAsync(CancellationToken ct)
    {
        SetStatus(DestinationStatus.Connecting);
        _lastError = null; _retryIn = null;
        _session = new FfmpegMediaSession(_ffmpeg, MediaSettings, _compositor, _mixer,
            Destination.OutputMode, IngestUrl, _encoder, streaming: true, frameHub: _frameHub,
            audioTargetKey: Destination.Id.ToString());
        try
        {
            await _session.StartAsync(ct);
            SetStatus(DestinationStatus.Live);
        }
        catch (Exception ex)
        {
            var msg = ex.Message;
            if (msg.Contains("capacity", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("NVENC", StringComparison.OrdinalIgnoreCase) && msg.Contains("fail", StringComparison.OrdinalIgnoreCase))
            {
                _lastError = "ENCODER CAPACITY ERROR: Concurrent hardware sessions reached. Use x264 or lower output count.";
            }
            else
            {
                _lastError = FfmpegArgumentBuilder.Redact(ex.Message, IngestUrl);
            }

            await _session.DisposeAsync();
            _session = null;
            throw;
        }
    }

    private async Task MonitorAsync(CancellationToken ct)
    {
        var retry = 0;
        while (!ct.IsCancellationRequested)
        {
            if (Status == DestinationStatus.Reconnecting)
            {
                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        _retryIn = RetrySchedule.GetDelay(retry++);
                        _reconnectCount++;
                        StatusChanged?.Invoke(Status);
                        await Task.Delay(_retryIn.Value, ct);
                        _retryIn = null;
                        await ConnectAsync(ct);
                        retry = 0;
                        break;
                    }
                    catch (OperationCanceledException) { return; }
                    catch (Exception ex)
                    {
                        AppLog.Write("Streaming", $"Reconnect attempt failed for {Destination.Id}: {ex.GetType().Name}");
                        SetStatus(DestinationStatus.Reconnecting);
                    }
                }
                continue;
            }

            try { await Task.Delay(500, ct); } catch (OperationCanceledException) { break; }

            // Update network health telemetry
            var tel = Telemetry;
            _health = NetworkHealthEvaluator.Evaluate(
                Destination.VideoBitrateKbps,
                tel?.MeasuredBitrateKbps,
                tel?.FramesDropped ?? 0,
                tel?.FramesEncoded ?? 0,
                tel?.CurrentFps ?? 0,
                Destination.FrameRate,
                _reconnectCount,
                Status == DestinationStatus.Reconnecting,
                tel?.RealtimeRatio ?? 1.0,
                tel?.CadenceState ?? MediaCadenceState.Healthy);

            // Optional Adaptive Bitrate
            if (AdaptiveController != null && AdaptiveController.Evaluate(_health, DateTime.UtcNow, out int newBitrate))
            {
                AppLog.Write("AdaptiveBitrate", $"Adapting bitrate for destination '{Destination.Name}': {Destination.VideoBitrateKbps} -> {newBitrate} kbps");
                Destination.VideoBitrateKbps = newBitrate;
            }

            if (_session?.State != RecordingState.Error) continue;
            _lastError = "Encoder or RTMP connection ended unexpectedly.";
            if (_session != null) { await _session.DisposeAsync(); _session = null; }
            if (!Destination.AutoReconnect) { SetStatus(DestinationStatus.Error); break; }
            SetStatus(DestinationStatus.Reconnecting);
        }
    }

    public async Task StopAsync()
    {
        if (_cts == null) return;
        SetStatus(DestinationStatus.Stopping);
        _cts.Cancel();
        if (_monitor != null) { try { await _monitor; } catch (OperationCanceledException) { } }
        if (_sharedPublisher is not null)
        {
            _sharedPublisher.StatusChanged -= SharedPublisher_StatusChanged;
            _lastPublisherDiagnostics = _sharedPublisher.Diagnostics;
            _lastTelemetry = _sharedEncoder?.Telemetry;
            await _groupManager!.ReleaseAsync(Destination.Id).ConfigureAwait(false);
            _sharedPublisher = null;
            _sharedEncoder = null;
            _cts.Dispose(); _cts = null; _monitor = null;
            SetStatus(Destination.Enabled ? DestinationStatus.Offline : DestinationStatus.Disabled);
            return;
        }
        if (_session != null)
        {
            _lastTelemetry = _session.Telemetry;
            await _session.DisposeAsync();
            _session = null;
        }
        _cts.Dispose(); _cts = null; _monitor = null;
        SetStatus(Destination.Enabled ? DestinationStatus.Offline : DestinationStatus.Disabled);
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
