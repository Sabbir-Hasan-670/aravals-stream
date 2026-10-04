using System.Text.Json;
using AravalsStream.Core.Audio;
using AravalsStream.Core.Models;
using AravalsStream.Core.Settings;
using Xunit;

namespace AravalsStream.Tests;

public class AudioFilterTests
{
    [Fact]
    public void DisabledFiltersAreTransparent()
    {
        var samples = Signal(.25f); var expected = samples.ToArray();
        new AudioFilterProcessor().Process(samples, new());
        Assert.Equal(expected, samples);
    }

    [Fact]
    public void GateClosesOnQuietNoiseAndOpensForSpeech()
    {
        var processor = new AudioFilterProcessor();
        var settings = new AudioFilterSettings { NoiseGate = true };
        var quiet = Signal(.001f); processor.Process(quiet, settings);
        Assert.True(Rms(quiet) < .00001);
        var speech = Signal(.2f); processor.Process(speech, settings);
        Assert.True(Rms(speech.Skip(4800).ToArray()) > .1);
        var silence = Signal(.001f); processor.Process(silence, settings);
        Assert.True(Rms(silence.TakeLast(4800).ToArray()) < .00005);
    }

    [Fact]
    public void SpectralSuppressionReducesBroadbandNoiseAndPreservesVoiceTone()
    {
        var random = new Random(18);
        var noise = Enumerable.Range(0, 96000).Select(_ => (float)((random.NextDouble() * 2 - 1) * .005)).ToArray();
        var originalRms = Rms(noise);
        new AudioFilterProcessor().Process(noise, new() { NoiseSuppression = true, NoiseFloorDb = -45 });
        Assert.True(Rms(noise.Skip(4096).ToArray()) < originalRms * .75);
        var tone = Signal(.2f);
        new AudioFilterProcessor().Process(tone, new() { NoiseSuppression = true });
        Assert.InRange(Rms(tone.Skip(4096).ToArray()), .13, .15);
    }

    [Fact]
    public void LimiterPreservesStereoBalanceAndBoundsPeaks()
    {
        var signal = Enumerable.Range(0, 1000).SelectMany(_ => new[] { .9f, .45f }).ToArray();
        new AudioFilterProcessor().Process(signal, new() { Gain = true, GainDb = 12, Limiter = true, LimiterDb = -3 });
        var ceiling = Math.Pow(10, -3d / 20);
        Assert.All(signal, value => Assert.True(Math.Abs(value) <= ceiling + .00001));
        Assert.Equal(signal[0] / 2, signal[1], 5);
    }

    [Fact]
    public void ChunkBoundariesDoNotChangeFilteredAudio()
    {
        var all = Signal(.2f); var chunks = all.ToArray();
        var settings = AudioFilterSettings.VoiceCleanup();
        new AudioFilterProcessor().Process(all, settings);
        var processor = new AudioFilterProcessor();
        for (var i = 0; i < chunks.Length; i += 274)
            processor.Process(chunks.AsSpan(i, Math.Min(274, chunks.Length - i)), settings);
        Assert.Equal(all, chunks);
    }

    [Fact]
    public void MixerPublishesProcessedAudioAndSettingsRoundTripCustomValues()
    {
        var channel = new AudioChannel { Filters = new() { Gain = true, GainDb = -6 } };
        float[]? output = null;
        var mixer = new AudioMixer(); mixer.SamplesReady += (_, samples) => output = samples;
        using var recording = mixer.CreateOutputTap("Recording");
        using var streaming = mixer.CreateOutputTap("TestStream");
        mixer.Receive(channel, new float[] { .5f, .5f });
        Assert.InRange(output![0], .2505f, .2507f);
        var recorded = new float[2]; var streamed = new float[2];
        recording.Read(recorded); streaming.Read(streamed);
        Assert.Equal(output, recorded); Assert.Equal(output, streamed);
        var settings = new AppSettings();
        settings.Recording.Video.BitrateKbps = 12345;
        settings.Recording.OutputDirectory = "D:\\My Recordings";
        settings.CaptureResources.Add(new CaptureResource { AudioFilters = AudioFilterSettings.VoiceCleanup(), DeviceId = "chosen-microphone" });
        var restored = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;
        Assert.Equal(12345, restored.Recording.Video.BitrateKbps);
        Assert.Equal("D:\\My Recordings", restored.Recording.OutputDirectory);
        Assert.True(restored.CaptureResources[0].AudioFilters.NoiseSuppression);
        Assert.Equal("chosen-microphone", restored.CaptureResources[0].DeviceId);
    }
    private static float[] Signal(float amplitude) => Enumerable.Range(0, 48000).SelectMany(i =>
        new[] { amplitude * (float)Math.Sin(2 * Math.PI * 440 * i / 48000), amplitude * (float)Math.Sin(2 * Math.PI * 440 * i / 48000) }).ToArray();
    private static double Rms(float[] values) => Math.Sqrt(values.Average(value => (double)value * value));
}
