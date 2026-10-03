namespace AravalsStream.Core.Audio;

public sealed class AudioSyncBuffer
{
    public const int SampleRate = 48000;
    public const int Channels = 2;
    private const int SamplesPerMs = (SampleRate * Channels) / 1000; // 96 samples/ms

    private readonly object _gate = new();
    private readonly Queue<float> _buffer = new();
    private int _offsetMs;
    private int _samplesToDrop;

    public int OffsetMs
    {
        get => _offsetMs;
        set
        {
            lock (_gate)
            {
                var clamped = Math.Clamp(value, -500, 5000);
                if (_offsetMs == clamped) return;
                _offsetMs = clamped;
                _buffer.Clear();
                _samplesToDrop = clamped < 0 ? Math.Abs(clamped) * SamplesPerMs : 0;
            }
        }
    }

    public void Process(ReadOnlySpan<float> input, Span<float> output)
    {
        if (input.Length != output.Length)
            throw new ArgumentException("Input and output spans must have identical length.");

        lock (_gate)
        {
            if (_offsetMs == 0)
            {
                input.CopyTo(output);
                return;
            }

            if (_offsetMs < 0)
            {
                int inputIdx = 0;
                while (_samplesToDrop > 0 && inputIdx < input.Length)
                {
                    _samplesToDrop--;
                    inputIdx++;
                }

                int remaining = input.Length - inputIdx;
                if (remaining > 0)
                {
                    input.Slice(inputIdx, remaining).CopyTo(output.Slice(0, remaining));
                    output.Slice(remaining).Clear();
                }
                else
                {
                    output.Clear();
                }
                return;
            }

            // OffsetMs > 0: Delay line
            int targetDelaySamples = _offsetMs * SamplesPerMs;

            for (int i = 0; i < input.Length; i++)
            {
                _buffer.Enqueue(input[i]);
            }

            for (int i = 0; i < output.Length; i++)
            {
                if (_buffer.Count > targetDelaySamples)
                {
                    output[i] = _buffer.Dequeue();
                }
                else
                {
                    output[i] = 0f;
                }
            }
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _buffer.Clear();
            _samplesToDrop = _offsetMs < 0 ? Math.Abs(_offsetMs) * SamplesPerMs : 0;
        }
    }
}
