using System.Numerics;

namespace AravalsStream.Core.Audio;

// Streaming spectral subtraction with a Hann overlap/add window, 512 samples
// of latency. Each stereo side has independent history; no callback allocations.
internal sealed class SpectralNoiseSuppressor
{
    private const int Size = 512, Hop = Size / 2;
    private static readonly double[] Windows = Enumerable.Range(0, Size).Select(i => .5 - .5 * Math.Cos(2 * Math.PI * i / Size)).ToArray();
    private readonly float[] _input = new float[Size], _output = new float[Size];
    private readonly double[] _noise = new double[Size];
    private readonly Complex[] _spectrum = new Complex[Size];
    private int _position, _hopPosition;
    private bool _initialized;

    public float Process(float sample, double floorDb)
    {
        var output = _output[_position]; _output[_position] = 0;
        _input[_position] = sample;
        _position = (_position + 1) % Size;
        if (++_hopPosition != Hop) return output;
        _hopPosition = 0;
        double energy = 0;
        for (var i = 0; i < Size; i++)
        {
            var value = _input[(_position + i) % Size];
            energy += value * value;
            _spectrum[i] = value * Window(i);
        }
        Transform(_spectrum, false);
        var floor = Math.Pow(10, Math.Clamp(floorDb, -80, -20) / 20);
        var quiet = Math.Sqrt(energy / Size) < floor * 2;
        var initialPower = floor * floor * Size * .375;
        for (var i = 0; i < Size; i++)
        {
            var power = _spectrum[i].Magnitude * _spectrum[i].Magnitude;
            if (!_initialized) _noise[i] = initialPower;
            if (quiet) _noise[i] = .98 * _noise[i] + .02 * power;
            var reduction = Math.Sqrt(Math.Max(.01, 1 - 1.5 * _noise[i] / Math.Max(1e-20, power)));
            _spectrum[i] *= reduction;
        }
        _initialized = true;
        Transform(_spectrum, true);
        for (var i = 0; i < Size; i++)
        {
            var window = Window(i);
            var other = Window((i + Hop) % Size);
            _output[(_position + i) % Size] += (float)(_spectrum[i].Real * window / (window * window + other * other));
        }
        return output;
    }

    private static double Window(int i) => Windows[i];
    private static void Transform(Complex[] values, bool inverse)
    {
        var n = values.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) (values[i], values[j]) = (values[j], values[i]);
        }
        for (var length = 2; length <= n; length <<= 1)
        {
            var step = Complex.FromPolarCoordinates(1, (inverse ? 2 : -2) * Math.PI / length);
            for (var start = 0; start < n; start += length)
            {
                var phase = Complex.One;
                for (var j = 0; j < length / 2; j++)
                {
                    var a = values[start + j]; var b = values[start + j + length / 2] * phase;
                    values[start + j] = a + b; values[start + j + length / 2] = a - b;
                    phase *= step;
                }
            }
        }
        if (inverse) for (var i = 0; i < n; i++) values[i] /= n;
    }
}
