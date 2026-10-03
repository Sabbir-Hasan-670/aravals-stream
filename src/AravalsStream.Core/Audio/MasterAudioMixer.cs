namespace AravalsStream.Core.Audio;

public sealed class MasterAudioMixer
{
    public const int SampleRate = 48000;
    public const int Channels = 2;

    private readonly object _gate = new();
    private readonly Queue<float> _buffer = new();
    private readonly int _maxBufferSamples;

    public MasterAudioMixer(int maxBufferedSeconds = 3)
    {
        _maxBufferSamples = Math.Max(SampleRate * Channels, maxBufferedSeconds * SampleRate * Channels);
    }

    public void PushSamples(ReadOnlySpan<float> samples)
    {
        if (samples.Length == 0) return;

        lock (_gate)
        {
            // If the incoming batch alone exceeds capacity, clear old and keep latest
            ReadOnlySpan<float> toAdd = samples;
            if (toAdd.Length > _maxBufferSamples)
            {
                _buffer.Clear();
                toAdd = toAdd.Slice(toAdd.Length - _maxBufferSamples);
            }
            else
            {
                int toDrop = (_buffer.Count + toAdd.Length) - _maxBufferSamples;
                for (int i = 0; i < toDrop && _buffer.Count > 0; i++)
                {
                    _buffer.Dequeue();
                }
            }

            for (int i = 0; i < toAdd.Length; i++)
            {
                _buffer.Enqueue(toAdd[i]);
            }
        }
    }

    public int Read(Span<float> destination)
    {
        int samplesRead = 0;

        lock (_gate)
        {
            int available = Math.Min(destination.Length, _buffer.Count);
            for (int i = 0; i < available; i++)
            {
                destination[i] = _buffer.Dequeue();
            }
            samplesRead = available;
        }

        // Fill remaining destination with silence to guarantee continuous timeline
        if (samplesRead < destination.Length)
        {
            destination.Slice(samplesRead).Clear();
        }

        return destination.Length;
    }

    public int AvailableSamples
    {
        get
        {
            lock (_gate) return _buffer.Count;
        }
    }

    public void Clear()
    {
        lock (_gate) _buffer.Clear();
    }
}
