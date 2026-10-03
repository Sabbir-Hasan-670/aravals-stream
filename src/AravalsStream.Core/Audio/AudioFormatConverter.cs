using System.Buffers.Binary;

namespace AravalsStream.Core.Audio;

public enum RawSampleType { Pcm16, Pcm24, Pcm32, Float32 }
public sealed record RawAudioFormat(int SampleRate, int Channels, RawSampleType SampleType);

// Converts WASAPI chunks to 48 kHz, stereo, interleaved 32-bit float.
public sealed class AudioFormatConverter(RawAudioFormat input)
{
    public const int OutputSampleRate = 48000;
    private double _position;
    private float _lastLeft, _lastRight;
    private bool _hasLast;

    public float[] Convert(ReadOnlySpan<byte> bytes)
    {
        var sampleBytes = input.SampleType == RawSampleType.Pcm24 ? 3 : input.SampleType == RawSampleType.Pcm16 ? 2 : 4;
        var frameBytes = input.Channels * sampleBytes;
        if (input.SampleRate <= 0 || input.Channels <= 0 || frameBytes <= 0) throw new ArgumentException("Invalid input audio format.");
        var count = bytes.Length / frameBytes;
        if (count == 0) return [];
        var left = new float[count + 1]; var right = new float[count + 1];
        for (var i = 0; i < count; i++)
        {
            var start = i * frameBytes;
            left[i + 1] = ReadSample(bytes.Slice(start, sampleBytes));
            right[i + 1] = input.Channels > 1 ? ReadSample(bytes.Slice(start + sampleBytes, sampleBytes)) : left[i + 1];
        }
        if (!_hasLast) { _lastLeft = left[1]; _lastRight = right[1]; _hasLast = true; }
        left[0] = _lastLeft; right[0] = _lastRight;
        _lastLeft = left[count]; _lastRight = right[count];
        var result = new List<float>((int)(count * 2.1 * OutputSampleRate / input.SampleRate));
        var step = (double)input.SampleRate / OutputSampleRate;
        while (_position < count)
        {
            var i = (int)_position;
            var fraction = (float)(_position - i);
            result.Add(left[i] + (left[i + 1] - left[i]) * fraction);
            result.Add(right[i] + (right[i + 1] - right[i]) * fraction);
            _position += step;
        }
        _position -= count;
        return result.ToArray();
    }

    private float ReadSample(ReadOnlySpan<byte> bytes) => input.SampleType switch
    {
        RawSampleType.Pcm16 => BinaryPrimitives.ReadInt16LittleEndian(bytes) / 32768f,
        RawSampleType.Pcm24 => ((bytes[0] | bytes[1] << 8 | bytes[2] << 16) << 8 >> 8) / 8388608f,
        RawSampleType.Pcm32 => BinaryPrimitives.ReadInt32LittleEndian(bytes) / 2147483648f,
        RawSampleType.Float32 => BitConverter.ToSingle(bytes),
        _ => 0
    };
}
