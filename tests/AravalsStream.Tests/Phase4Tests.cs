using System.Text.Json;
using AravalsStream.Core.Audio;
using AravalsStream.Core.Models;
using AravalsStream.Core.Settings;
using Xunit;

namespace AravalsStream.Tests;

public sealed class Phase4Tests
{
    [Fact]
    public void SourceResourcesAndAudioSettingsSurviveSave()
    {
        var window = new CaptureResource { Type = SourceType.WindowCapture, DeviceId = "ABC", WindowTitle = "Editor", Name = "Editor" };
        var microphone = new CaptureResource { Type = SourceType.AudioInput, DeviceId = "MIC", Name = "Mic", Volume = 0.4f, Muted = true };
        var scene = new Scene();
        scene.Sources.Add(new SceneSource { Type = SourceType.WindowCapture, SourceReference = window.Id });
        scene.Sources.Add(new SceneSource { Type = SourceType.AudioInput, SourceReference = microphone.Id, Visible = false });
        var restored = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(new AppSettings
            { Scenes = [scene], CaptureResources = [window, microphone] }))!;
        Assert.Equal(window.Id, restored.Scenes[0].Sources[0].SourceReference);
        Assert.False(restored.Scenes[0].Sources[1].Visible);
        Assert.Equal(0.4f, restored.CaptureResources[1].Volume);
        Assert.True(restored.CaptureResources[1].Muted);
    }

    [Fact]
    public void AudioConverterNormalizesMonoPcmToStereo()
    {
        var converter = new AudioFormatConverter(new RawAudioFormat(48000, 1, RawSampleType.Pcm16));
        var samples = converter.Convert([0, 0, 0, 64]);
        Assert.Equal(4, samples.Length);
        Assert.Equal(samples[0], samples[1]);
        Assert.Equal(samples[2], samples[3]);
        var following = converter.Convert([0, 0]);
        Assert.InRange(following[0], 0.49f, 0.51f);
    }

    [Fact]
    public void MixerAppliesVolumeAndMute()
    {
        var mixer = new AudioMixer();
        var channel = new AudioChannel { Volume = 0.5f };
        float[]? output = null;
        mixer.SamplesReady += (_, samples) => output = samples;
        mixer.Receive(channel, [0.8f, -0.8f]);
        Assert.Equal([0.4f, -0.4f], output!);
        mixer.DecayMeters();
        Assert.InRange(channel.Peak, 0.01f, 0.4f);
        channel.Muted = true;
        mixer.Receive(channel, [0.8f, -0.8f]);
        Assert.Equal([0f, 0f], output!);
    }
}
