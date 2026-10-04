using AravalsStream.Core.Audio;
using AravalsStream.Core.Composition;
using AravalsStream.Core.Models;
using Xunit;
using System.Text.Json;

namespace AravalsStream.Tests;

public sealed class AudioVisualRegressionTests
{
    [Theory]
    [InlineData(SourceType.AudioInput)]
    [InlineData(SourceType.AudioOutput)]
    public void AudioOnlySourcesHaveNoVisualCapability(SourceType type)
    {
        var source = new SceneSource { Type = type };
        Assert.True(source.HasAudio);
        Assert.False(source.HasVideo);
        Assert.False(source.CanTransform);
    }

    [Theory]
    [InlineData(OutputMode.Horizontal)]
    [InlineData(OutputMode.Vertical)]
    public void CompositorNeverRequestsAudioFrames(OutputMode mode)
    {
        var scene = new Scene();
        scene.Sources.Add(new SceneSource { Type = SourceType.AudioInput });
        scene.Sources.Add(new SceneSource { Type = SourceType.AudioOutput });
        var requests = 0;
        CompositedFrameRenderer.Render(4, 4, mode, scene, _ => { requests++; return null; }, new byte[4 * 4 * 4]);
        Assert.Equal(0, requests);
    }

    [Fact]
    public void VideoSourceRetainsVisualCapability()
    {
        var source = new SceneSource { Type = SourceType.DisplayCapture };
        Assert.True(source.HasVideo);
        Assert.True(source.CanTransform);
    }

    [Fact]
    public void LegacyAudioTransformLoadsButRemainsNonVisual()
    {
        var source = new SceneSource { Type = SourceType.AudioInput };
        source.HorizontalTransform.Width = 300;
        source.VerticalTransform.Height = 500;
        var restored = JsonSerializer.Deserialize<SceneSource>(JsonSerializer.Serialize(source))!;
        Assert.Equal(300, restored.HorizontalTransform.Width);
        Assert.Equal(500, restored.VerticalTransform.Height);
        Assert.False(restored.HasVideo);
        Assert.False(restored.CanTransform);
    }

    [Fact]
    public void AudioOnlyChannelStaysInMixerAndCanRouteToOutput()
    {
        var mixer = new AudioMixer();
        var channel = new AudioChannel { Name = "Microphone" };
        using var tap = mixer.CreateOutputTap("Horizontal");
        mixer.Matrix.SetRoute(channel.Id, "Horizontal", true);
        mixer.Receive(channel, new float[] { 0.5f, -0.5f });
        Assert.Contains(mixer.Channels, item => item.Id == channel.Id);
        Span<float> output = stackalloc float[2];
        tap.Read(output);
        Assert.Equal(0.5f, output[0]);
        Assert.Equal(-0.5f, output[1]);
    }

    [Fact]
    public void MeterRisesAndFallsWithoutDroppingSilence()
    {
        var mixer = new AudioMixer();
        var channel = new AudioChannel();
        mixer.Receive(channel, new float[] { 1, 1, 1, 1 });
        mixer.DecayMeters();
        Assert.InRange(channel.Peak, 0.01f, 1f);
        var raised = channel.Peak;
        mixer.DecayMeters();
        Assert.InRange(channel.Peak, 0f, raised);
    }

    [Fact]
    public void InvalidMeterSamplesDoNotPoisonDisplay()
    {
        var mixer = new AudioMixer();
        var channel = new AudioChannel();
        mixer.Receive(channel, new float[] { float.NaN, float.PositiveInfinity, -1 });
        mixer.DecayMeters();
        Assert.True(float.IsFinite(channel.Peak));
        Assert.InRange(channel.Peak, 0f, 1f);
    }
}
