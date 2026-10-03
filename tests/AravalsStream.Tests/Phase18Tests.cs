using System.Text;
using AravalsStream.Core.Audio;
using AravalsStream.Core.Composition;
using AravalsStream.Core.Models;
using AravalsStream.Core.Recording;
using AravalsStream.Core.Recording.Models;
using AravalsStream.Core.Streaming;
using Xunit;

namespace AravalsStream.Tests;

public sealed class Phase18Tests
{
    private sealed class DisposableTestResource : IDisposable
    {
        public bool IsDisposed { get; private set; }
        public void Dispose() => IsDisposed = true;
    }

    private sealed class TestSubscriber : IEncodedPacketSubscriber
    {
        public string DestinationId { get; } = Guid.NewGuid().ToString("N");
        public byte[]? Header { get; private set; }
        public List<byte[]> Packets { get; } = [];
        public List<EncodedPacketKind> PacketKinds { get; } = [];
        public string? FailureReason { get; private set; }

        public void OnHeader(ReadOnlyMemory<byte> flvHeader) => Header = flvHeader.ToArray();
        public void OnPacket(ReadOnlyMemory<byte> packetData, EncodedPacketKind kind)
        {
            Packets.Add(packetData.ToArray());
            PacketKinds.Add(kind);
        }
        public void OnEncoderFailed(string reason) => FailureReason = reason;
    }

    [Fact]
    public void GpuFrameLeaseLifecycle_AcquireAndDispose_ReclaimsResourceWhenLastLeaseDisposed()
    {
        var reclaimed = false;
        var resource = new DisposableTestResource();
        var frame = new ReferenceCountedResource<DisposableTestResource>(resource, r =>
        {
            reclaimed = true;
            r.Dispose();
        });

        Assert.Equal(1, frame.ReferenceCount);
        Assert.False(reclaimed);

        var lease1 = frame.AcquireLease();
        Assert.Equal(2, frame.ReferenceCount);

        var lease2 = frame.AcquireLease();
        Assert.Equal(3, frame.ReferenceCount);

        // Disposing initial frame reference drops count to 2, resource still alive
        frame.Dispose();
        Assert.Equal(2, frame.ReferenceCount);
        Assert.False(reclaimed);
        frame.Dispose();
        Assert.Equal(2, frame.ReferenceCount);

        // Disposing lease1 drops count to 1, resource still alive
        lease1.Dispose();
        Assert.Equal(1, frame.ReferenceCount);
        Assert.False(reclaimed);

        // Disposing final lease drops count to 0 and reclaims resource
        lease2.Dispose();
        Assert.Equal(0, frame.ReferenceCount);
        Assert.True(reclaimed);
        Assert.True(resource.IsDisposed);
    }

    [Fact]
    public void GpuFrameLeaseLifecycle_ThrowsIfAcquiredAfterDisposal()
    {
        var resource = new DisposableTestResource();
        var frame = new ReferenceCountedResource<DisposableTestResource>(resource, r => r.Dispose());
        frame.Dispose();

        Assert.Throws<ObjectDisposedException>(() => frame.AcquireLease());
    }

    [Fact]
    public void TexturePool_BoundsAndReusesItems_DropsWhenCapacityExceeded()
    {
        using var pool = new BoundedResourcePool<string, DisposableTestResource>(maxCapacityPerKey: 2);

        var res1 = new DisposableTestResource();
        var res2 = new DisposableTestResource();
        var res3 = new DisposableTestResource();

        Assert.True(pool.Return("1080p_BGRA", res1));
        Assert.True(pool.Return("1080p_BGRA", res2));
        // Exceeds max capacity of 2 -> rejected and disposed immediately
        Assert.False(pool.Return("1080p_BGRA", res3));
        Assert.True(res3.IsDisposed);
        Assert.Equal(2, pool.GetPooledCount("1080p_BGRA"));

        // Renting retrieves pooled items
        var rented1 = pool.TryRent("1080p_BGRA");
        Assert.NotNull(rented1);
        Assert.Same(res1, rented1);
        Assert.Equal(1, pool.GetPooledCount("1080p_BGRA"));

        var rented2 = pool.TryRent("1080p_BGRA");
        Assert.NotNull(rented2);
        Assert.Same(res2, rented2);
        Assert.Equal(0, pool.GetPooledCount("1080p_BGRA"));

        // Empty pool returns null
        Assert.Null(pool.TryRent("1080p_BGRA"));
    }

    [Fact]
    public void EncoderCompatibilityKey_IdenticalDestinations_AreEqualAndSameHashCode()
    {
        var destA = new Destination
        {
            Platform = "YouTube",
            OutputMode = OutputMode.Horizontal,
            FrameRate = 60,
            VideoBitrateKbps = 6000,
            AudioBitrateKbps = 160,
            KeyframeIntervalSeconds = 2
        };

        var destB = new Destination
        {
            Platform = "Twitch",
            OutputMode = OutputMode.Horizontal,
            FrameRate = 60,
            VideoBitrateKbps = 6000,
            AudioBitrateKbps = 160,
            KeyframeIntervalSeconds = 2
        };

        var keyA = EncoderCompatibilityKey.For(destA, "h264_nvenc", "DefaultMix");
        var keyB = EncoderCompatibilityKey.For(destB, "h264_nvenc", "DefaultMix");

        Assert.Equal(keyA, keyB);
        Assert.Equal(keyA.GetHashCode(), keyB.GetHashCode());
    }

    [Fact]
    public void EncoderCompatibilityKey_DifferingBitrate_AreNotEqual()
    {
        var destA = new Destination { OutputMode = OutputMode.Horizontal, VideoBitrateKbps = 6000, AudioBitrateKbps = 160 };
        var destB = new Destination { OutputMode = OutputMode.Horizontal, VideoBitrateKbps = 4500, AudioBitrateKbps = 160 };

        var keyA = EncoderCompatibilityKey.For(destA, "h264_nvenc", "DefaultMix");
        var keyB = EncoderCompatibilityKey.For(destB, "h264_nvenc", "DefaultMix");

        Assert.NotEqual(keyA, keyB);
    }

    [Fact]
    public void EncoderCompatibilityKey_DifferingFps_AreNotEqual()
    {
        var destA = new Destination { OutputMode = OutputMode.Horizontal, FrameRate = 60 };
        var destB = new Destination { OutputMode = OutputMode.Horizontal, FrameRate = 30 };

        var keyA = EncoderCompatibilityKey.For(destA, "h264_nvenc", "DefaultMix");
        var keyB = EncoderCompatibilityKey.For(destB, "h264_nvenc", "DefaultMix");

        Assert.NotEqual(keyA, keyB);
    }

    [Fact]
    public void EncoderCompatibilityKey_DifferingCanvasMode_AreNotEqual()
    {
        var destA = new Destination { OutputMode = OutputMode.Horizontal };
        var destB = new Destination { OutputMode = OutputMode.Vertical };

        var keyA = EncoderCompatibilityKey.For(destA, "h264_nvenc", "DefaultMix");
        var keyB = EncoderCompatibilityKey.For(destB, "h264_nvenc", "DefaultMix");

        Assert.NotEqual(keyA, keyB);
    }

    [Fact]
    public void EncoderCompatibilityKey_DifferingAudioMix_AreNotEqual()
    {
        var dest = new Destination { OutputMode = OutputMode.Horizontal };

        var keyA = EncoderCompatibilityKey.For(dest, "h264_nvenc", "MixTrack1");
        var keyB = EncoderCompatibilityKey.For(dest, "h264_nvenc", "MixTrack2");

        Assert.NotEqual(keyA, keyB);
    }

    [Fact]
    public void EncoderCompatibilityKey_DifferingBackend_AreNotEqual()
    {
        var dest = new Destination { OutputMode = OutputMode.Horizontal };

        var keyA = EncoderCompatibilityKey.For(dest, "h264_nvenc", "DefaultMix");
        var keyB = EncoderCompatibilityKey.For(dest, "libx264", "DefaultMix");

        Assert.NotEqual(keyA, keyB);
    }

    [Fact]
    public void SharedEncoderGrouping_ThreeIdenticalHDestinations_FormSingleGroup()
    {
        var destinations = new[]
        {
            new Destination { Platform = "YouTube", OutputMode = OutputMode.Horizontal, FrameRate = 60, VideoBitrateKbps = 6000 },
            new Destination { Platform = "Twitch", OutputMode = OutputMode.Horizontal, FrameRate = 60, VideoBitrateKbps = 6000 },
            new Destination { Platform = "Kick", OutputMode = OutputMode.Horizontal, FrameRate = 60, VideoBitrateKbps = 6000 }
        };

        var groups = destinations
            .GroupBy(d => EncoderCompatibilityKey.For(d, "h264_nvenc", "MasterMix"))
            .ToList();

        Assert.Single(groups);
        Assert.Equal(3, groups[0].Count());
    }

    [Fact]
    public void SharedEncoderGrouping_DifferentBitrates_SeparateGroups()
    {
        var destinations = new[]
        {
            new Destination { Platform = "YouTube", OutputMode = OutputMode.Horizontal, VideoBitrateKbps = 6000 },
            new Destination { Platform = "Twitch", OutputMode = OutputMode.Horizontal, VideoBitrateKbps = 6000 },
            new Destination { Platform = "Kick", OutputMode = OutputMode.Horizontal, VideoBitrateKbps = 4500 }
        };

        var groups = destinations
            .GroupBy(d => EncoderCompatibilityKey.For(d, "h264_nvenc", "MasterMix"))
            .ToList();

        Assert.Equal(2, groups.Count);
        Assert.Contains(groups, g => g.Count() == 2);
        Assert.Contains(groups, g => g.Count() == 1);
    }

    [Fact]
    public void SharedEncoderGrouping_HorizontalAndVertical_SeparateGroups()
    {
        var destinations = new[]
        {
            new Destination { Platform = "YouTube", OutputMode = OutputMode.Horizontal, FrameRate = 60 },
            new Destination { Platform = "Twitch", OutputMode = OutputMode.Horizontal, FrameRate = 60 },
            new Destination { Platform = "Kick", OutputMode = OutputMode.Horizontal, FrameRate = 60 },
            new Destination { Platform = "TikTok", OutputMode = OutputMode.Vertical, FrameRate = 30 }
        };

        var groups = destinations
            .GroupBy(d => EncoderCompatibilityKey.For(d, "h264_nvenc", "MasterMix"))
            .ToList();

        Assert.Equal(2, groups.Count);
        var hGroup = groups.Single(g => g.Key.CanvasMode == OutputMode.Horizontal);
        var vGroup = groups.Single(g => g.Key.CanvasMode == OutputMode.Vertical);

        Assert.Equal(3, hGroup.Count());
        Assert.Single(vGroup);
    }

    [Fact]
    public async Task EncodedPacketHub_FansOutPacketsToMultipleSubscribers()
    {
        using var hub = new EncodedPacketHub();
        var sub1 = new TestSubscriber();
        var sub2 = new TestSubscriber();

        hub.Subscribe(sub1);
        hub.Subscribe(sub2);
        Assert.Equal(2, hub.SubscriberCount);

        var dummyData = new byte[] { 1, 2, 3, 4, 5 };
        var ms = new MemoryStream();
        ms.Write(new byte[] { 0x46, 0x4C, 0x56, 0x01, 0x05, 0x00, 0x00, 0x00, 0x09 });
        ms.Write(new byte[] { 0, 0, 0, 0 });

        ms.WriteByte(0x09);
        ms.Write(new byte[] { 0x00, 0x00, 0x05 });
        ms.Write(new byte[] { 0x00, 0x00, 0x00, 0x00 });
        ms.Write(new byte[] { 0x00, 0x00, 0x00 });
        ms.Write(dummyData);
        ms.Write(new byte[] { 0x00, 0x00, 0x00, 0x10 });

        ms.Position = 0;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await hub.ProcessEncoderStreamAsync(ms, cts.Token);

        Assert.NotNull(sub1.Header);
        Assert.NotNull(sub2.Header);
        Assert.Single(sub1.Packets);
        Assert.Single(sub2.Packets);
        Assert.Equal(20, sub1.Packets[0].Length);
        Assert.Equal(20, sub2.Packets[0].Length);
    }

    [Fact]
    public async Task EncodedPacketHub_CachesHeadersAndDeliversToLateSubscriber()
    {
        using var hub = new EncodedPacketHub();

        var ms = new MemoryStream();
        ms.Write(new byte[] { 0x46, 0x4C, 0x56, 0x01, 0x05, 0x00, 0x00, 0x00, 0x09 });
        ms.Write(new byte[] { 0, 0, 0, 0 });

        ms.WriteByte(0x09);
        ms.Write(new byte[] { 0x00, 0x00, 0x05 });
        ms.Write(new byte[] { 0x00, 0x00, 0x00, 0x00 });
        ms.Write(new byte[] { 0x00, 0x00, 0x00 });
        ms.Write(new byte[] { 0x17, 0x00, 0x00, 0x00, 0x00 });
        ms.Write(new byte[] { 0x00, 0x00, 0x00, 0x10 });

        ms.Position = 0;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await hub.ProcessEncoderStreamAsync(ms, cts.Token);

        var lateSubscriber = new TestSubscriber();
        hub.Subscribe(lateSubscriber);

        Assert.NotNull(lateSubscriber.Header);
        Assert.Single(lateSubscriber.Packets);
        Assert.Equal(EncodedPacketKind.VideoSequenceHeader, lateSubscriber.PacketKinds[0]);
    }

    [Fact]
    public async Task EncodedPacketHub_SubscriberIsolation_CancellationDoesNotAffectOthers()
    {
        using var hub = new EncodedPacketHub();
        var healthySub = new TestSubscriber();
        var failingSub = new TestSubscriber();

        hub.Subscribe(healthySub);
        hub.Subscribe(failingSub);

        hub.Unsubscribe(failingSub);
        Assert.Equal(1, hub.SubscriberCount);

        var ms = new MemoryStream();
        ms.Write(new byte[] { 0x46, 0x4C, 0x56, 0x01, 0x05, 0x00, 0x00, 0x00, 0x09 });
        ms.Write(new byte[] { 0, 0, 0, 0 });
        ms.Position = 0;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await hub.ProcessEncoderStreamAsync(ms, cts.Token);

        Assert.NotNull(healthySub.Header);
        Assert.Null(failingSub.Header);
    }

    [Fact]
    public void AudioMixFingerprint_CalculatesDeterministicString()
    {
        var mixer = new AudioMixer();
        var ch1 = new AudioChannel { Name = "Microphone", Volume = 1.0f, Muted = false };
        var ch2 = new AudioChannel { Name = "Desktop", Volume = 0.8f, Muted = false };

        var print1 = AudioMixFingerprint.For(mixer, [ch1, ch2]);
        var print2 = AudioMixFingerprint.For(mixer, [ch1, ch2]);

        Assert.Equal(print1, print2);

        // Modifying mute state modifies fingerprint
        ch1.Muted = true;
        var print3 = AudioMixFingerprint.For(mixer, [ch1, ch2]);
        Assert.NotEqual(print1, print3);
    }

    [Fact]
    public void AudioMixFingerprint_IncludesDestinationRoutingAndGain()
    {
        var mixer = new AudioMixer();
        var channel = new AudioChannel { Name = "Desktop", Volume = 1, Muted = false };
        mixer.Receive(channel, new float[] { 0.25f, -0.25f });
        var before = AudioMixFingerprint.For(mixer, "destination-a");

        mixer.Matrix.SetRoute(channel.Id, "destination-a", enabled: false, routeGain: 0);
        var after = AudioMixFingerprint.For(mixer, "destination-a");

        Assert.NotEqual(before, after);
        Assert.Equal(before, AudioMixFingerprint.For(mixer, "destination-b"));
    }

    [Fact]
    public void Yuv420FrameConverter_ResizeNv12_CorrectlyDownscalesLumaAndInterleavedChroma()
    {
        const int srcW = 4;
        const int srcH = 4;
        var srcNv12 = new byte[srcW * srcH + (srcW * srcH / 2)];
        Array.Fill(srcNv12, (byte)128, 0, srcW * srcH);
        for (var i = srcW * srcH; i < srcNv12.Length; i += 2)
        {
            srcNv12[i] = 64;   // U
            srcNv12[i + 1] = 192; // V
        }

        const int dstW = 2;
        const int dstH = 2;
        var dstNv12 = new byte[dstW * dstH + (dstW * dstH / 2)];

        Yuv420FrameConverter.ResizeNv12(srcNv12, srcW, srcH, dstNv12, dstW, dstH);

        Assert.Equal(128, dstNv12[0]);
        Assert.Equal(128, dstNv12[1]);
        Assert.Equal(128, dstNv12[2]);
        Assert.Equal(128, dstNv12[3]);

        Assert.Equal(64, dstNv12[4]);   // U
        Assert.Equal(192, dstNv12[5]);  // V
    }

    [Fact]
    public void Yuv420FrameConverter_PlanarToNv12_PreservesLumaAndInterleavesChroma()
    {
        const int width = 4;
        const int height = 2;
        var planar = new byte[]
        {
            16, 32, 48, 64, 80, 96, 112, 128, // Y
            10, 20,                            // U
            30, 40                             // V
        };
        var nv12 = new byte[planar.Length];

        Yuv420FrameConverter.PlanarToNv12(planar, nv12, width, height);

        Assert.Equal(planar[..8], nv12[..8]);
        Assert.Equal(new byte[] { 10, 30, 20, 40 }, nv12[8..]);
    }

    [Fact]
    public void FfmpegArgumentBuilder_Nv12PixelFormat_EmitsCorrectArguments()
    {
        var settings = new RecordingSettings();
        settings.Video.Width = 1920;
        settings.Video.Height = 1080;
        settings.Video.FrameRate = 60;
        settings.Video.BitrateKbps = 6000;

        var args = FfmpegArgumentBuilder.Build(
            settings,
            "h264_nvenc",
            1920,
            1080,
            "test_video",
            "test_audio",
            @"C:\test\output.mp4",
            streaming: false,
            inputPixelFormat: "nv12");

        Assert.Contains("-pix_fmt nv12", args);
        Assert.Contains("-s 1920x1080", args);
        Assert.Contains("-r 60", args);
        Assert.Contains("-c:v h264_nvenc", args);
    }
}
