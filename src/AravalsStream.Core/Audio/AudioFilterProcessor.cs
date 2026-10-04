namespace AravalsStream.Core.Audio;

// Canonical 48 kHz stereo PCM. Linked dynamics keep stereo channels balanced.
public sealed class AudioFilterProcessor
{
    private readonly object _gate = new();
    private readonly double[] _previousInput = new double[2], _previousOutput = new double[2];
    private double _envelope, _gateGain, _compressorGain = 1;
    private bool _gateOpen;
    private AudioFilterSettings? _previousSettings;
    private SpectralNoiseSuppressor[] _suppressors = [new(), new()];

    public void Process(Span<float> samples, AudioFilterSettings settings)
    {
        if (!settings.NoiseSuppression && !settings.HighPass && !settings.NoiseGate && !settings.Compressor && !settings.Gain && !settings.Limiter) return;
        lock (_gate)
        {
            if (_previousSettings != settings)
            {
                Array.Clear(_previousInput); Array.Clear(_previousOutput);
                _envelope = 0; _gateOpen = false; _gateGain = 0; _compressorGain = 1;
                _previousSettings = settings.Copy();
                _suppressors = [new(), new()];
            }
            var hp = Math.Exp(-2 * Math.PI * Math.Clamp(settings.HighPassHz, 20, 1000) / 48000);
            double gateOpen = Db(settings.GateOpenDb), gateClose = Db(Math.Min(settings.GateCloseDb, settings.GateOpenDb));
            var release = Math.Exp(-1 / (48000 * Math.Clamp(settings.GateReleaseMs, 10, 2000) / 1000));
            var threshold = Db(settings.CompressorThresholdDb);
            var gain = settings.Gain ? Db(Math.Clamp(settings.GainDb, -60, 30)) : 1;
            var ceiling = Db(Math.Clamp(settings.LimiterDb, -30, 0));
            var envelopeRelease = Math.Exp(-1d / (48000 * .05));
            var gateAttack = Math.Exp(-1d / (48000 * .005));
            var compressorAttack = Math.Exp(-1d / (48000 * .01));
            var compressorRelease = Math.Exp(-1d / (48000 * .15));
            for (var i = 0; i < samples.Length; i += 2)
            {
                double peak = 0;
                for (var ch = 0; ch < 2 && i + ch < samples.Length; ch++)
                {
                    var value = float.IsFinite(samples[i + ch]) ? samples[i + ch] : 0;
                    if (settings.NoiseSuppression) value = _suppressors[ch].Process(value, settings.NoiseFloorDb);
                    if (settings.HighPass)
                    {
                        var filtered = hp * (_previousOutput[ch] + value - _previousInput[ch]);
                        _previousInput[ch] = value; _previousOutput[ch] = filtered;
                        value = (float)filtered;
                    }
                    samples[i + ch] = value;
                    peak = Math.Max(peak, Math.Abs(value));
                }
                _envelope = peak > _envelope ? peak : _envelope * envelopeRelease;
                if (_envelope >= gateOpen) _gateOpen = true;
                else if (_envelope < gateClose) _gateOpen = false;
                var gateTarget = _gateOpen ? 1d : 0;
                var gateCoefficient = _gateOpen ? gateAttack : release;
                _gateGain = gateTarget + gateCoefficient * (_gateGain - gateTarget);
                var compressorTarget = settings.Compressor && _envelope > threshold
                    ? Math.Pow(_envelope / threshold, 1 / Math.Clamp(settings.CompressorRatio, 1, 20) - 1) : 1;
                var compressorCoefficient = compressorTarget < _compressorGain ? compressorAttack : compressorRelease;
                _compressorGain = compressorTarget + compressorCoefficient * (_compressorGain - compressorTarget);
                var multiplier = gain * (settings.NoiseGate ? _gateGain : 1) * (settings.Compressor ? _compressorGain : 1);
                if (settings.Limiter && peak * multiplier > ceiling) multiplier = ceiling / peak;
                for (var ch = 0; ch < 2 && i + ch < samples.Length; ch++) samples[i + ch] *= (float)multiplier;
            }
        }
    }
    private static double Db(double db) => Math.Pow(10, Math.Clamp(db, -120, 60) / 20);
}
