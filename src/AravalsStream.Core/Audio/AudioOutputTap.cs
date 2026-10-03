namespace AravalsStream.Core.Audio;

// A consumer-specific view of the mixer. Recording and streaming can read the
// same live audio without draining each other's samples.
public sealed class AudioOutputTap : IDisposable
{
    private const int MaxSamplesPerChannel = MasterAudioMixer.SampleRate * MasterAudioMixer.Channels * 3;
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Queue<float>> _channels = [];
    private readonly Action<AudioOutputTap> _onDispose;
    private bool _disposed;

    public string TargetKey { get; }
    public int QueuedSamples { get { lock (_gate) return _channels.Values.Sum(queue => queue.Count); } }

    internal AudioOutputTap(string targetKey, Action<AudioOutputTap> onDispose)
    {
        TargetKey = targetKey;
        _onDispose = onDispose;
    }

    internal void Push(Guid channelId, ReadOnlySpan<float> samples)
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (!_channels.TryGetValue(channelId, out var queue))
                _channels[channelId] = queue = new Queue<float>();
            foreach (var sample in samples) queue.Enqueue(sample);
            while (queue.Count > MaxSamplesPerChannel) queue.Dequeue();
        }
    }

    internal void Remove(Guid channelId)
    {
        lock (_gate) _channels.Remove(channelId);
    }

    public void Read(Span<float> destination)
    {
        destination.Clear();
        lock (_gate)
        {
            foreach (var queue in _channels.Values)
            {
                for (var i = 0; i < destination.Length && queue.Count > 0; i++)
                    destination[i] = Math.Clamp(destination[i] + queue.Dequeue(), -1f, 1f);
            }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            foreach (var queue in _channels.Values) queue.Clear();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _channels.Clear();
        }
        _onDispose(this);
    }
}
