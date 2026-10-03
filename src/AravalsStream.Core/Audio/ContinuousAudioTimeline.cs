namespace AravalsStream.Core.Audio;

/// <summary>
/// Produces fixed-size stereo blocks on the caller's single output clock.
/// Captured samples are consumed in order; missing samples remain zero-filled.
/// </summary>
public sealed class ContinuousAudioTimeline
{
    public const int SampleRate = 48_000;
    public const int Channels = 2;
    public const int BlockDurationMilliseconds = 10;
    public const int SamplesPerBlock = SampleRate * Channels * BlockDurationMilliseconds / 1000;

    private readonly object _gate = new();
    private readonly Queue<float[]> _pending = new();
    private int _headOffset;

    /// <summary>Adds interleaved 48 kHz stereo samples captured from WASAPI.</summary>
    public void Enqueue(ReadOnlySpan<float> samples)
    {
        if ((samples.Length & 1) != 0)
            throw new ArgumentException("Interleaved stereo audio must contain an even number of samples.", nameof(samples));
        if (samples.IsEmpty) return;

        lock (_gate) _pending.Enqueue(samples.ToArray());
    }

    /// <summary>
    /// Fills one 10 ms block. If capture has not supplied enough samples,
    /// the remainder is silence; later blocks continue the same sample timeline.
    /// </summary>
    public void ReadBlock(Span<float> destination)
    {
        if (destination.Length != SamplesPerBlock)
            throw new ArgumentException($"A timeline block must contain exactly {SamplesPerBlock} samples.", nameof(destination));

        destination.Clear();
        lock (_gate)
        {
            var written = 0;
            while (written < destination.Length && _pending.TryPeek(out var chunk))
            {
                var count = Math.Min(destination.Length - written, chunk.Length - _headOffset);
                chunk.AsSpan(_headOffset, count).CopyTo(destination[written..]);
                written += count;
                _headOffset += count;
                if (_headOffset == chunk.Length)
                {
                    _pending.Dequeue();
                    _headOffset = 0;
                }
            }
        }
    }
}
