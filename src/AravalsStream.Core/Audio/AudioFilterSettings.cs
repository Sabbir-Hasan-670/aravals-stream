namespace AravalsStream.Core.Audio;

// Persisted per physical source; disabled filters preserve existing projects.
public sealed record AudioFilterSettings
{
    public bool NoiseSuppression { get; set; }
    public double NoiseFloorDb { get; set; } = -45;
    public bool HighPass { get; set; }
    public double HighPassHz { get; set; } = 80;
    public bool NoiseGate { get; set; }
    public double GateOpenDb { get; set; } = -36;
    public double GateCloseDb { get; set; } = -42;
    public double GateReleaseMs { get; set; } = 150;
    public bool Compressor { get; set; }
    public double CompressorThresholdDb { get; set; } = -18;
    public double CompressorRatio { get; set; } = 3;
    public bool Gain { get; set; }
    public double GainDb { get; set; }
    public bool Limiter { get; set; }
    public double LimiterDb { get; set; } = -1;
    public AudioFilterSettings Copy() => this with { };
    public static AudioFilterSettings VoiceCleanup() => new()
    { NoiseSuppression = true, HighPass = true, NoiseGate = true, Compressor = true, Limiter = true };
}
