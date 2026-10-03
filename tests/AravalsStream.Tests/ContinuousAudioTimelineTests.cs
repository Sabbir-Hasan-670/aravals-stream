using AravalsStream.Core.Audio;
using Xunit;

namespace AravalsStream.Tests;

public sealed class ContinuousAudioTimelineTests
{
    [Fact]
    public void EmitsTimedStereoSilenceWhenCaptureHasNoSamples()
    {
        var timeline = new ContinuousAudioTimeline();
        var block = new float[ContinuousAudioTimeline.SamplesPerBlock];

        timeline.ReadBlock(block);

        Assert.Equal(960, block.Length);
        Assert.All(block, sample => Assert.Equal(0f, sample));
    }

    [Fact]
    public void PreservesRealAudioAndPadsTheUnfilledPartWithSilence()
    {
        var timeline = new ContinuousAudioTimeline();
        var captured = Enumerable.Repeat(0.25f, 480).ToArray();
        var block = new float[ContinuousAudioTimeline.SamplesPerBlock];
        timeline.Enqueue(captured);

        timeline.ReadBlock(block);

        Assert.All(block[..captured.Length], sample => Assert.Equal(0.25f, sample));
        Assert.All(block[captured.Length..], sample => Assert.Equal(0f, sample));
    }

    [Fact]
    public void ResumesRealAudioOnTheSameTimelineAfterSilence()
    {
        var timeline = new ContinuousAudioTimeline();
        var block = new float[ContinuousAudioTimeline.SamplesPerBlock];
        timeline.ReadBlock(block);
        Assert.All(block, sample => Assert.Equal(0f, sample));

        timeline.Enqueue(Enumerable.Repeat(0.75f, block.Length).ToArray());
        timeline.ReadBlock(block);

        Assert.All(block, sample => Assert.Equal(0.75f, sample));
    }

    [Fact]
    public void SilenceAfterRealAudioDoesNotResetOrShortenTheSampleTimeline()
    {
        var timeline = new ContinuousAudioTimeline();
        var block = new float[ContinuousAudioTimeline.SamplesPerBlock];
        var deliveredSamples = 0;
        timeline.Enqueue(Enumerable.Repeat(-0.5f, block.Length).ToArray());

        timeline.ReadBlock(block);
        deliveredSamples += block.Length;
        Assert.All(block, sample => Assert.Equal(-0.5f, sample));
        timeline.ReadBlock(block);
        deliveredSamples += block.Length;
        Assert.All(block, sample => Assert.Equal(0f, sample));

        Assert.Equal(2 * ContinuousAudioTimeline.SamplesPerBlock, deliveredSamples);
    }

    [Fact]
    public void FifteenSecondsOfSilentAndResumedCaptureAlwaysProducesContinuousBlocks()
    {
        var timeline = new ContinuousAudioTimeline();
        var block = new float[ContinuousAudioTimeline.SamplesPerBlock];
        var deliveredSamples = 0;
        var nonSilentBlocks = 0;

        for (var tick = 0; tick < 150; tick++)
        {
            if (tick is >= 50 and < 100)
                timeline.Enqueue(Enumerable.Repeat(0.4f, block.Length).ToArray());

            timeline.ReadBlock(block);
            deliveredSamples += block.Length;
            if (block.Any(sample => sample != 0f)) nonSilentBlocks++;
        }

        Assert.Equal(150 * ContinuousAudioTimeline.SamplesPerBlock, deliveredSamples);
        Assert.Equal(50, nonSilentBlocks);
    }
}
