using AravalsStream.Core.Audio;
using Xunit;

namespace AravalsStream.Tests;

public sealed class StreamingAudioFanoutTests
{
    [Fact]
    public void RecordingAndStreamingTapsReceiveSameMixedAudio()
    {
        var mixer = new AudioMixer();
        using var recordingTap = mixer.CreateOutputTap();
        using var streamingTap = mixer.CreateOutputTap();
        var mic = new AudioChannel();
        var desktop = new AudioChannel();
        mixer.Receive(mic, new float[] { 0.25f, -0.5f, 0.25f, 0 });
        mixer.Receive(desktop, new float[] { 0.25f, 0.25f, -0.5f, 0.5f });
        var recording = new float[4]; var streaming = new float[4];
        recordingTap.Read(recording); streamingTap.Read(streaming);
        Assert.Equal(new float[] { 0.5f, -0.25f, -0.25f, 0.5f }, recording);
        Assert.Equal(recording, streaming);
    }

    [Fact]
    public void MutingMicLeavesDesktopAudio()
    {
        var mixer = new AudioMixer();
        using var tap = mixer.CreateOutputTap();
        var mic = new AudioChannel { Muted = true };
        var desktop = new AudioChannel();
        mixer.Receive(mic, new float[] { 1, 1 });
        mixer.Receive(desktop, new float[] { 0.2f, -0.2f });
        var output = new float[2]; tap.Read(output);
        Assert.Equal(new float[] { 0.2f, -0.2f }, output);
    }

    [Fact]
    public void ClearingPausedRecordingTapDoesNotDrainLiveStreamTap()
    {
        var mixer = new AudioMixer();
        using var recording = mixer.CreateOutputTap();
        using var stream = mixer.CreateOutputTap();
        var mic = new AudioChannel();
        mixer.Receive(mic, new float[] { 0.25f, 0.25f });
        recording.Clear();
        var streamSamples = new float[2];
        stream.Read(streamSamples);
        Assert.Equal(new float[] { 0.25f, 0.25f }, streamSamples);
        var recordedSamples = new float[2];
        recording.Read(recordedSamples);
        Assert.Equal(new float[] { 0, 0 }, recordedSamples);
        mixer.Receive(mic, new float[] { 0.5f, 0.5f });
        recording.Read(recordedSamples);
        Assert.Equal(new float[] { 0.5f, 0.5f }, recordedSamples);
    }
}
