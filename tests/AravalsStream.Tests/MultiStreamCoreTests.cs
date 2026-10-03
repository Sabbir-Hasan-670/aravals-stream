using AravalsStream.Core.Audio;
using AravalsStream.Core.Composition;
using AravalsStream.Core.Models;
using Xunit;

namespace AravalsStream.Tests;

public sealed class MultiStreamCoreTests
{
    [Fact]
    public void PartialFailureKeepsGlobalStatusLive()
    {
        Assert.Equal("LIVE", StreamSessionSummary.GlobalStatus([DestinationStatus.Live, DestinationStatus.Error, DestinationStatus.Live]));
        Assert.Equal("LIVE", StreamSessionSummary.GlobalStatus([DestinationStatus.Live, DestinationStatus.Stopping]));
        Assert.Equal("LIVE", StreamSessionSummary.GlobalStatus([DestinationStatus.Reconnecting, DestinationStatus.Error]));
        Assert.Equal("LIVE", StreamSessionSummary.GlobalStatus([DestinationStatus.FallingBehind, DestinationStatus.Live]));
        Assert.Equal("LIVE", StreamSessionSummary.GlobalStatus([DestinationStatus.FallingBehind, DestinationStatus.Offline]));
        Assert.Equal("LIVE", StreamSessionSummary.GlobalStatus([DestinationStatus.Reconnecting, DestinationStatus.Offline]));
        Assert.Equal("CONNECTING", StreamSessionSummary.GlobalStatus([DestinationStatus.Connecting, DestinationStatus.Error]));
        Assert.Equal("CONNECTING", StreamSessionSummary.GlobalStatus([DestinationStatus.Restarting, DestinationStatus.Offline]));
        Assert.Equal("STOPPING", StreamSessionSummary.GlobalStatus([DestinationStatus.Stopping]));
        Assert.Equal("STOPPING", StreamSessionSummary.GlobalStatus([DestinationStatus.Stopping, DestinationStatus.Offline]));
        Assert.Equal("ERROR", StreamSessionSummary.GlobalStatus([DestinationStatus.Error]));
        Assert.Equal("ERROR", StreamSessionSummary.GlobalStatus([DestinationStatus.Error, DestinationStatus.Offline]));
        Assert.Equal("OFFLINE", StreamSessionSummary.GlobalStatus([]));
        Assert.Equal("OFFLINE", StreamSessionSummary.GlobalStatus([DestinationStatus.Offline, DestinationStatus.Disabled]));
    }

    [Fact]
    public void ConfiguredBandwidthIncludesOnlyEnabledOutputsAndTheirAudio()
    {
        var destinations = new[]
        {
            new Destination { VideoBitrateKbps = 8000, AudioBitrateKbps = 192 },
            new Destination { VideoBitrateKbps = 6000, AudioBitrateKbps = 160 },
            new Destination { VideoBitrateKbps = 4000, AudioBitrateKbps = 160, Enabled = false }
        };
        Assert.Equal(14352, StreamSessionSummary.EstimatedUploadKbps(destinations));
    }

    [Fact]
    public void ThreeIndependentAudioConsumersAllReceiveMicAndDesktop()
    {
        var mixer = new AudioMixer();
        using var recording = mixer.CreateOutputTap();
        using var horizontal = mixer.CreateOutputTap();
        using var vertical = mixer.CreateOutputTap();
        mixer.Receive(new AudioChannel(), new float[] { 0.2f, 0.2f });
        mixer.Receive(new AudioChannel(), new float[] { 0.3f, -0.1f });
        foreach (var tap in new[] { recording, horizontal, vertical })
        {
            var samples = new float[2];
            tap.Read(samples);
            Assert.Equal(new float[] { 0.5f, 0.1f }, samples);
        }
    }

    [Fact]
    public void FrameSnapshotsFanOutWithoutConsumersStealingThem()
    {
        var horizontal = new FrameSnapshotBus();
        var vertical = new FrameSnapshotBus();
        var hFrame = new byte[] { 1, 2, 3 };
        var vFrame = new byte[] { 4, 5, 6 };
        horizontal.Publish(hFrame);
        vertical.Publish(vFrame);
        Assert.Same(hFrame, horizontal.Latest);
        Assert.Same(hFrame, horizontal.Latest);
        Assert.Same(vFrame, vertical.Latest);
        horizontal.Publish(new byte[] { 7 });
        Assert.Same(vFrame, vertical.Latest);
    }
}
