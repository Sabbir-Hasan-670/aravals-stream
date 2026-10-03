using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading.Channels;
using AravalsStream.Core.Models;
using AravalsStream.Core.Recording;
using AravalsStream.Core.Recording.Models;
using AravalsStream.Core.Services;
using AravalsStream.Core.Streaming;

namespace AravalsStream.App.Streaming;

/// <summary>
/// Independent RTMP publisher session attached to an EncodedPacketHub.
/// Publishes already-encoded packets via a lightweight copy-mode FFmpeg process or socket.
/// Provides independent connection lifecycle, error isolation, reconnects, and telemetry.
/// </summary>
public sealed class PublisherSession : IEncodedPacketSubscriber, IAsyncDisposable
{
    private readonly record struct QueuedPacket(ReadOnlyMemory<byte> Data, EncodedPacketKind? Kind, long EnqueuedAt);
    public readonly record struct PublisherDiagnostics(long ReceivedPackets, long EnqueuedPackets,
        long DequeuedPackets, long DiscardedPackets, long BytesEnqueued, long BytesWritten, int QueuePackets, long QueueBytes,
        int MaxQueuePackets, long MaxQueueBytes, double MaxPacketAgeMs, double AverageWriteMs,
        double P95WriteMs, double P99WriteMs, long WriteCount, int Reconnects, string? LastError);
    private static readonly double[] WriteHistogramBoundsMs = [1, 2, 5, 10, 20, 50, 100, 250, 500, 1000, 5000];
    private readonly Destination _destination;
    private readonly string _ffmpegPath;
    private readonly string _streamKey;
    private readonly Guid _sharedEncoderSessionId;
    private readonly EncodedPacketHub _packetHub;
    private readonly Channel<QueuedPacket> _packetChannel;
    private readonly CancellationTokenSource _cts = new();

    private Process? _publisherProcess;
    private NamedPipeServerStream? _publisherPipe;
    private Task? _pipePumpTask;
    private Task? _monitorTask;
    private int _hasStreamedKeyframe;
    private int _backpressureSignaled;
    private int _backpressureCount;
    private int _sharedEncoderFailed;
    private bool _disposed;
    private readonly StringBuilder _publisherDiagnostics = new();
    private readonly object _diagnosticsLock = new();

    private long _bytesPublished;
    private long _packetsPublished;
    private double _uploadBitrateKbps;
    private long _receivedPackets, _enqueuedPackets, _dequeuedPackets, _discardedPackets, _bytesEnqueued, _queueBytes;
    private int _queuePackets, _maxQueuePackets;
    private long _maxQueueBytes, _maxPacketAgeTicks, _writeTicks, _writeCount;
    private int _totalReconnectCount;
    private readonly long[] _writeHistogram = new long[WriteHistogramBoundsMs.Length + 1];

    public string DestinationId => _destination.Id.ToString();
    public Destination Destination => _destination;
    public DestinationStatus Status { get; private set; } = DestinationStatus.Offline;
    public string? LastError { get; private set; }
    public int ReconnectCount { get; private set; }
    public TimeSpan? RetryIn { get; private set; }
    public NetworkHealthReport Health { get; } = new();

    public PublisherDiagnostics Diagnostics
    {
        get
        {
            var writes = Interlocked.Read(ref _writeCount);
            return new PublisherDiagnostics(Interlocked.Read(ref _receivedPackets), Interlocked.Read(ref _enqueuedPackets),
                Interlocked.Read(ref _dequeuedPackets), Interlocked.Read(ref _discardedPackets), Interlocked.Read(ref _bytesEnqueued), Interlocked.Read(ref _bytesPublished),
                Volatile.Read(ref _queuePackets), Interlocked.Read(ref _queueBytes), Volatile.Read(ref _maxQueuePackets),
                Interlocked.Read(ref _maxQueueBytes), StopwatchTicksToMilliseconds(Interlocked.Read(ref _maxPacketAgeTicks)),
                writes == 0 ? 0 : StopwatchTicksToMilliseconds(Interlocked.Read(ref _writeTicks)) / writes,
                WritePercentileMs(0.95, writes), WritePercentileMs(0.99, writes), writes, Volatile.Read(ref _totalReconnectCount), LastError);
        }
    }

    public event Action<DestinationStatus>? StatusChanged;

    public PublisherSession(
        Destination destination,
        string ffmpegPath,
        string streamKey,
        EncodedPacketHub packetHub,
        Guid sharedEncoderSessionId)
    {
        _destination = destination;
        _ffmpegPath = ffmpegPath;
        _streamKey = streamKey;
        _packetHub = packetHub;
        _sharedEncoderSessionId = sharedEncoderSessionId;

        // Bounded channel to prevent memory buildup if network socket stalls
        var channelOptions = new BoundedChannelOptions(120)
        {
            // Encoded FLV tags cannot be dropped independently without breaking
            // inter-frame dependencies or the FLV tag stream. On overflow, isolate
            // this publisher and reconnect it at a fresh keyframe.
            FullMode = BoundedChannelFullMode.Wait,
            SingleWriter = false,
            SingleReader = true
        };
        _packetChannel = Channel.CreateBounded<QueuedPacket>(channelOptions);

        Health.TargetBitrateKbps = destination.VideoBitrateKbps;
        Health.TargetFps = destination.FrameRate;
    }

    private void SetStatus(DestinationStatus status)
    {
        if (Status != status)
            AppLog.Write("PublisherSession", $"Destination {_destination.Platform} session_id={_sharedEncoderSessionId} status={status}.");
        Status = status;
        _destination.Status = status;
        StatusChanged?.Invoke(status);
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        SetStatus(DestinationStatus.Connecting);
        _packetHub.Subscribe(this);

        try
        {
            await LaunchPublisherProcessAsync(ct).ConfigureAwait(false);
            if (Volatile.Read(ref _backpressureSignaled) != 0)
                throw new IOException("Publisher could not keep up with encoded packets.");
            SetStatus(DestinationStatus.Live);
            _monitorTask = Task.Run(() => MonitorProcessAsync(_cts.Token));
        }
        catch (Exception ex)
        {
            AppLog.Write("PublisherSession", $"Initial publish failed for {_destination.Platform}: {ex.Message}");
            if (!_destination.AutoReconnect)
            {
                SetStatus(DestinationStatus.Error);
                LastError = ex.Message;
                throw;
            }
            SetStatus(DestinationStatus.Reconnecting);
            _monitorTask = Task.Run(() => ReconnectLoopAsync(_cts.Token));
        }
    }

    private async Task LaunchPublisherProcessAsync(CancellationToken ct)
    {
        var pipeName = $"aravals_pub_{Guid.NewGuid():N}";
        _publisherPipe = new NamedPipeServerStream(
            pipeName,
            PipeDirection.Out,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            512 * 1024,
            0);

        var rtmpUrl = _destination.StreamUrl.TrimEnd('/') + (string.IsNullOrEmpty(_streamKey) ? "" : "/" + _streamKey);

        // Lightweight copy-mode publisher: 0% encoding CPU, simply transmits encoded FLV
        // The shared stream arrives live through the named pipe already. Adding
        // FFmpeg's -re throttle here double-paces reads and can fill the bounded
        // publisher queue under multiple outputs.
        var args = $"-hide_banner -loglevel warning -f flv -i \\\\.\\pipe\\{pipeName} -c copy -rw_timeout 5000000 -f flv \"{rtmpUrl}\"";

        _publisherProcess = new Process
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

        _publisherProcess.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrEmpty(e.Data)) return;
            lock (_diagnosticsLock)
            {
                _publisherDiagnostics.AppendLine(e.Data);
                if (_publisherDiagnostics.Length > 16 * 1024)
                    _publisherDiagnostics.Remove(0, _publisherDiagnostics.Length - 16 * 1024);
            }
        };

        lock (_diagnosticsLock) _publisherDiagnostics.Clear();
        _publisherProcess.Start();
        _publisherProcess.BeginErrorReadLine();

        // Connect pipe with timeout
        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        connectCts.CancelAfter(TimeSpan.FromSeconds(8));
        await _publisherPipe.WaitForConnectionAsync(connectCts.Token).ConfigureAwait(false);

        _pipePumpTask = Task.Run(() => PumpPacketsToPipeAsync(_cts.Token));
    }

    private async Task PumpPacketsToPipeAsync(CancellationToken ct)
    {
        var reader = _packetChannel.Reader;
        var stopwatch = Stopwatch.StartNew();
        long windowBytes = 0;

        try
        {
            while (!ct.IsCancellationRequested && await reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                while (reader.TryRead(out var packet))
                {
                    Interlocked.Decrement(ref _queuePackets);
                    Interlocked.Add(ref _queueBytes, -packet.Data.Length);
                    Interlocked.Increment(ref _dequeuedPackets);
                    var ageTicks = Stopwatch.GetTimestamp() - packet.EnqueuedAt;
                    UpdateMax(ref _maxPacketAgeTicks, ageTicks);
                    if (_publisherPipe is not { IsConnected: true }) return;

                    var writeStart = Stopwatch.GetTimestamp();
                    await _publisherPipe.WriteAsync(packet.Data, ct).ConfigureAwait(false);
                    var writeTicks = Stopwatch.GetTimestamp() - writeStart;
                    Interlocked.Add(ref _writeTicks, writeTicks);
                    Interlocked.Increment(ref _writeCount);
                    RecordWriteDuration(StopwatchTicksToMilliseconds(writeTicks));
                    Interlocked.Add(ref _bytesPublished, packet.Data.Length);
                    Interlocked.Increment(ref _packetsPublished);
                    windowBytes += packet.Data.Length;

                    if (stopwatch.ElapsedMilliseconds >= 1000)
                    {
                        var kbps = (windowBytes * 8.0) / stopwatch.ElapsedMilliseconds;
                        Volatile.Write(ref _uploadBitrateKbps, kbps);
                        Health.MeasuredBitrateKbps = kbps;
                        windowBytes = 0;
                        stopwatch.Restart();
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            AppLog.Write("PublisherSession", $"Pipe write failed for {_destination.Platform}: {ex.Message}");
        }
    }

    private async Task MonitorProcessAsync(CancellationToken ct)
    {
        try
        {
            if (_publisherProcess is null) return;
            await _publisherProcess.WaitForExitAsync(ct).ConfigureAwait(false);

            if (!ct.IsCancellationRequested)
            {
                var exitCode = _publisherProcess.ExitCode;
                string diagnostics;
                lock (_diagnosticsLock) diagnostics = _publisherDiagnostics.ToString().Trim();
                diagnostics = FfmpegArgumentBuilder.Redact(diagnostics, _destination.StreamUrl.TrimEnd('/') + "/" + _streamKey);
                AppLog.Write("PublisherSession", $"Publisher process for {_destination.Platform} exited (code: {exitCode}). FFmpeg output: {diagnostics}");

                if (Volatile.Read(ref _sharedEncoderFailed) != 0)
                {
                    SetStatus(DestinationStatus.Error);
                    return;
                }

                if (_destination.AutoReconnect)
                {
                    _packetHub.Unsubscribe(this);
                    SetStatus(DestinationStatus.Reconnecting);
                    await ReconnectLoopAsync(ct).ConfigureAwait(false);
                }
                else
                {
                    SetStatus(DestinationStatus.Error);
                    LastError = $"Publisher exited with code {exitCode}";
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task ReconnectLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            Interlocked.Increment(ref _totalReconnectCount);
            ReconnectCount++;
            var delay = RetrySchedule.GetDelay(ReconnectCount);
            RetryIn = delay;
            AppLog.Write("PublisherSession", $"Reconnecting {_destination.Platform} in {delay.TotalSeconds}s (attempt {ReconnectCount})...");

            try
            {
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }

            RetryIn = null;
            SetStatus(DestinationStatus.Connecting);

            try
            {
                CleanupPublisherProcess();
                if (_pipePumpTask is not null)
                {
                    try { await _pipePumpTask.ConfigureAwait(false); } catch { }
                    _pipePumpTask = null;
                }
                while (_packetChannel.Reader.TryRead(out var stalePacket))
                {
                    Interlocked.Decrement(ref _queuePackets);
                    Interlocked.Add(ref _queueBytes, -stalePacket.Data.Length);
                    Interlocked.Increment(ref _discardedPackets);
                }
                Interlocked.Exchange(ref _hasStreamedKeyframe, 0);
                Interlocked.Exchange(ref _backpressureSignaled, 0);
                _packetHub.Subscribe(this); // Re-seed FLV header and codec configuration.
                await LaunchPublisherProcessAsync(ct).ConfigureAwait(false);
                if (Volatile.Read(ref _backpressureSignaled) != 0)
                    throw new IOException("Publisher could not keep up with encoded packets.");
                SetStatus(DestinationStatus.Live);
                LastError = Volatile.Read(ref _backpressureCount) >= 2 ? CapacityWarning : null;
                ReconnectCount = 0;
                await MonitorProcessAsync(ct).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                AppLog.Write("PublisherSession", $"Reconnect attempt failed for {_destination.Platform}: {ex.Message}");
                SetStatus(DestinationStatus.Reconnecting);
            }
        }
    }

    #region IEncodedPacketSubscriber

    public void OnHeader(ReadOnlyMemory<byte> flvHeader)
    {
        if (!TryEnqueue(flvHeader, null))
            RequestBackpressureReconnect();
    }

    public void OnPacket(ReadOnlyMemory<byte> packetData, EncodedPacketKind kind)
    {
        Interlocked.Increment(ref _receivedPackets);
        // Preserve stream metadata and codec headers; wait for an actual video keyframe
        // before forwarding dependent video packets after a new RTMP connection.
        if (kind == EncodedPacketKind.VideoInterframe && Volatile.Read(ref _hasStreamedKeyframe) == 0) return;
        if (kind == EncodedPacketKind.VideoKeyframe)
        {
            Volatile.Write(ref _hasStreamedKeyframe, 1);
        }
        if (!TryEnqueue(packetData, kind))
            RequestBackpressureReconnect();
    }

    private bool TryEnqueue(ReadOnlyMemory<byte> data, EncodedPacketKind? kind)
    {
        var packet = new QueuedPacket(data, kind, Stopwatch.GetTimestamp());
        Interlocked.Increment(ref _queuePackets);
        Interlocked.Add(ref _queueBytes, data.Length);
        if (!_packetChannel.Writer.TryWrite(packet))
        {
            Interlocked.Decrement(ref _queuePackets);
            Interlocked.Add(ref _queueBytes, -data.Length);
            return false;
        }
        Interlocked.Increment(ref _enqueuedPackets);
        Interlocked.Add(ref _bytesEnqueued, data.Length);
        UpdateMax(ref _maxQueuePackets, Volatile.Read(ref _queuePackets));
        UpdateMax(ref _maxQueueBytes, Interlocked.Read(ref _queueBytes));
        return true;
    }

    private void RecordWriteDuration(double milliseconds)
    {
        var bucket = Array.FindIndex(WriteHistogramBoundsMs, bound => milliseconds <= bound);
        if (bucket < 0) bucket = _writeHistogram.Length - 1;
        Interlocked.Increment(ref _writeHistogram[bucket]);
    }

    private double WritePercentileMs(double percentile, long count)
    {
        if (count == 0) return 0;
        var target = (long)Math.Ceiling(count * percentile);
        long cumulative = 0;
        for (var i = 0; i < _writeHistogram.Length; i++)
        {
            cumulative += Interlocked.Read(ref _writeHistogram[i]);
            if (cumulative >= target) return i < WriteHistogramBoundsMs.Length ? WriteHistogramBoundsMs[i] : double.PositiveInfinity;
        }
        return 0;
    }

    private static double StopwatchTicksToMilliseconds(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    private static void UpdateMax(ref int location, int value)
    {
        var current = Volatile.Read(ref location);
        while (value > current)
        {
            var observed = Interlocked.CompareExchange(ref location, value, current);
            if (observed == current) break;
            current = observed;
        }
    }

    private static void UpdateMax(ref long location, long value)
    {
        var current = Interlocked.Read(ref location);
        while (value > current)
        {
            var observed = Interlocked.CompareExchange(ref location, value, current);
            if (observed == current) break;
            current = observed;
        }
    }

    private void RequestBackpressureReconnect()
    {
        if (Interlocked.Exchange(ref _backpressureSignaled, 1) != 0) return;
        var overflows = Interlocked.Increment(ref _backpressureCount);
        LastError = overflows >= 2
            ? CapacityWarning
            : "Publisher is falling behind; reconnecting this destination at the next keyframe.";
        AppLog.Write("PublisherSession", $"Publisher queue filled for {_destination.Platform}; session_id={_sharedEncoderSessionId}; state=FallingBehind; overflow_count={overflows}; isolating this destination.");
        _packetHub.Unsubscribe(this);
        SetStatus(DestinationStatus.FallingBehind);
        SetStatus(DestinationStatus.Reconnecting);
        try
        {
            if (_publisherProcess is { HasExited: false }) _publisherProcess.Kill();
        }
        catch (Exception ex) { AppLog.Write("PublisherSession", $"Could not restart slow publisher: {ex.Message}"); }
    }

    private const string CapacityWarning = "System cannot sustain the current output workload in real time. This destination is reconnecting; try Eco Mode, 30 FPS, or fewer outputs.";

    public void OnEncoderFailed(string reason)
    {
        AppLog.Write("PublisherSession", $"Shared encoder failed for {_destination.Platform}: {reason}");
        LastError = $"Shared encoder error: {reason}";
        Interlocked.Exchange(ref _sharedEncoderFailed, 1);
        _packetHub.Unsubscribe(this);
        SetStatus(DestinationStatus.Error);
        try { if (_publisherProcess is { HasExited: false }) _publisherProcess.Kill(); } catch { }
    }

    #endregion

    private void CleanupPublisherProcess()
    {
        try { _publisherPipe?.Dispose(); } catch { }
        _publisherPipe = null;

        if (_publisherProcess is not null)
        {
            try
            {
                if (!_publisherProcess.HasExited)
                {
                    _publisherProcess.Kill();
                    _publisherProcess.WaitForExit(1000);
                }
                _publisherProcess.Dispose();
            }
            catch { }
            _publisherProcess = null;
        }
    }

    public async Task StopAsync()
    {
        SetStatus(DestinationStatus.Offline);
        _packetHub.Unsubscribe(this);
        _cts.Cancel();

        CleanupPublisherProcess();

        if (_pipePumpTask is not null)
        {
            try { await _pipePumpTask.ConfigureAwait(false); } catch { }
        }
        if (_monitorTask is not null)
        {
            try { await _monitorTask.ConfigureAwait(false); } catch { }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await StopAsync().ConfigureAwait(false);
        _cts.Dispose();
    }
}
