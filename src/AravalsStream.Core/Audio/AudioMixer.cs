using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Diagnostics;

namespace AravalsStream.Core.Audio;

public sealed class AudioChannel : INotifyPropertyChanged
{
    private float _volume = 1;
    private bool _muted, _active;
    private float _peak;
    private AudioMonitoringMode _monitoringMode = AudioMonitoringMode.MonitorOff;
    private readonly AudioSyncBuffer _syncBuffer = new();

    public Guid Id { get; init; } = Guid.NewGuid();
    private string _name = "Audio";
    private AudioFilterSettings _filters = new();
    public string Name { get => _name; set { _name = value; Changed(); } }
    public AudioFilterSettings Filters { get => Volatile.Read(ref _filters); set { Volatile.Write(ref _filters, value.Copy()); Changed(); } }
    public AudioFilterProcessor FilterProcessor { get; } = new();
    public float Volume { get => _volume; set { _volume = Math.Clamp(value, 0, 2); Changed(); } }
    public bool Muted { get => _muted; set { _muted = value; Changed(); } }
    public bool Active { get => _active; set { _active = value; Changed(); } }
    public float Peak { get => _peak; internal set { if (_peak == value) return; _peak = value; Changed(); } }

    public int SyncOffsetMs
    {
        get => _syncBuffer.OffsetMs;
        set { _syncBuffer.OffsetMs = value; Changed(); }
    }

    public AudioSyncBuffer SyncBuffer => _syncBuffer;

    public AudioMonitoringMode MonitoringMode
    {
        get => _monitoringMode;
        set { _monitoringMode = value; Changed(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? property = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}

public interface IAudioMixer
{
    event Action<AudioChannel, float[]>? SamplesReady;
    void Receive(AudioChannel channel, ReadOnlySpan<float> samples);
    void DecayMeters();
}

public sealed class AudioMixer : IAudioMixer
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, float> _latestPeaks = [];
    private readonly Dictionary<Guid, AudioChannel> _channels = [];
    private readonly List<AudioOutputTap> _outputTaps = [];
    private long _lastMeterTick;

    public MasterAudioMixer Master { get; } = new();
    public AudioRoutingMatrix Matrix { get; } = new();
    public event Action<AudioChannel, float[]>? SamplesReady;

    public AudioOutputTap CreateOutputTap(string targetKey = "Default")
    {
        var tap = new AudioOutputTap(targetKey, RemoveTap);
        lock (_gate) _outputTaps.Add(tap);
        return tap;
    }

    private void RemoveTap(AudioOutputTap tap)
    {
        lock (_gate) _outputTaps.Remove(tap);
    }

    public void RemoveChannel(Guid id)
    {
        AudioOutputTap[] taps;
        lock (_gate)
        {
            _channels.Remove(id);
            _latestPeaks.Remove(id);
            taps = _outputTaps.ToArray();
        }
        foreach (var tap in taps) tap.Remove(id);
    }

    public IReadOnlyCollection<AudioChannel> Channels
    {
        get
        {
            lock (_gate) return _channels.Values.ToList();
        }
    }

    public void Receive(AudioChannel channel, ReadOnlySpan<float> samples)
    {
        if (samples.Length == 0) return;

        // 1. Apply sync offset
        var synced = new float[samples.Length];
        channel.SyncBuffer.Process(samples, synced);
        channel.FilterProcessor.Process(synced, channel.Filters);

        // 2. Global gain
        var globalGain = channel.Muted ? 0f : channel.Volume;

        // 3. Peak meter calculation
        float peak = 0;
        for (var i = 0; i < synced.Length; i++)
        {
            var sample = Math.Abs(synced[i] * globalGain);
            if (float.IsFinite(sample)) peak = Math.Max(peak, Math.Min(1f, sample));
        }

        AudioOutputTap[] taps;
        lock (_gate)
        {
            _channels[channel.Id] = channel;
            _latestPeaks[channel.Id] = Math.Max(_latestPeaks.GetValueOrDefault(channel.Id), peak);
            taps = _outputTaps.ToArray();
        }

        // 4. Feed Master mixer if not MonitorOnly
        var scaledSamples = new float[synced.Length];
        for (var i = 0; i < synced.Length; i++)
            scaledSamples[i] = Math.Clamp(synced[i] * globalGain, -1f, 1f);

        if (channel.MonitoringMode != AudioMonitoringMode.MonitorOnly)
        {
            Master.PushSamples(scaledSamples);
        }

        // 5. Fan out to taps according to routing matrix and monitoring modes
        foreach (var tap in taps)
        {
            if (tap.TargetKey == "Monitor")
            {
                // Monitor tap receives if MonitorOnly or MonitorAndOutput
                if (channel.MonitoringMode is AudioMonitoringMode.MonitorOnly or AudioMonitoringMode.MonitorAndOutput)
                {
                    tap.Push(channel.Id, scaledSamples);
                }
            }
            else
            {
                // Output/Recording taps: skip if MonitorOnly
                if (channel.MonitoringMode == AudioMonitoringMode.MonitorOnly) continue;

                // Check routing matrix
                if (!Matrix.IsRouteEnabled(channel.Id, tap.TargetKey)) continue;

                var routeGain = Matrix.GetRouteGain(channel.Id, tap.TargetKey);
                var effectiveGain = globalGain * routeGain;

                var outSamples = new float[synced.Length];
                for (var i = 0; i < synced.Length; i++)
                    outSamples[i] = Math.Clamp(synced[i] * effectiveGain, -1f, 1f);

                tap.Push(channel.Id, outSamples);
            }
        }

        // 6. UI event
        SamplesReady?.Invoke(channel, scaledSamples);
    }

    public void DecayMeters()
    {
        var now = Stopwatch.GetTimestamp();
        lock (_gate)
        {
            var elapsed = _lastMeterTick == 0 ? 1.0 / 30 :
                Math.Clamp(Stopwatch.GetElapsedTime(_lastMeterTick, now).TotalSeconds, 0, 0.25);
            _lastMeterTick = now;
            foreach (var (id, channel) in _channels)
            {
                var target = _latestPeaks.GetValueOrDefault(id);
                if (!float.IsFinite(target)) target = 0;
                target = Math.Clamp(target, 0f, 1f);
                var current = float.IsFinite(channel.Peak) ? Math.Clamp(channel.Peak, 0f, 1f) : 0f;
                var timeConstant = target > current ? 0.035 : 0.35;
                var alpha = 1.0 - Math.Exp(-elapsed / timeConstant);
                var next = (float)(current + (target - current) * alpha);
                channel.Peak = next < 0.0001f ? 0f : next;
                _latestPeaks[id] = 0;
            }
        }
    }
}
