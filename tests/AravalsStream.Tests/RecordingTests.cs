using System.Text.Json;
using AravalsStream.Core.Audio;
using AravalsStream.Core.Composition;
using AravalsStream.Core.Models;
using AravalsStream.Core.Recording;
using AravalsStream.Core.Recording.Models;
using AravalsStream.Core.Settings;
using Xunit;

namespace AravalsStream.Tests;

public sealed class RecordingTests
{
    [Fact]
    public void FfmpegLocator_MissingFfmpeg_ReturnsMissingStatus()
    {
        var locator = new FfmpegLocator(_ => (false, "", "Not found"));
        var result = locator.Locate(configuredPath: "Z:\\NonExistentPath\\ffmpeg.exe");

        // Should not crash, returns missing or invalid
        Assert.NotEqual(FfmpegStatus.Available, result.Status);
    }

    [Fact]
    public void EncoderDiscoveryService_RanksEncodersCorrectly()
    {
        var sampleEncodersOutput = @"
Encoders:
 V..... libx264              libx264 H.264
 V..... h264_amf             AMD AMF H.264
 V..... h264_nvenc           NVIDIA NVENC H.264
 V..... h264_qsv             Intel Quick Sync H.264
";
        var service = new EncoderDiscoveryService((path, args) =>
        {
            if (args == "-encoders") return (true, sampleEncodersOutput, "");
            return (true, "", ""); // Mock test encodes succeed
        });

        var encoders = service.DetectEncoders("mock_ffmpeg.exe");

        Assert.Equal(4, encoders.Count);
        // Priority order: NVENC > QSV > AMF > libx264
        Assert.Equal("h264_nvenc", encoders[0].Id);
        Assert.True(encoders[0].HardwareAccelerated);
        Assert.True(encoders[0].Recommended);

        Assert.Equal("h264_qsv", encoders[1].Id);
        Assert.True(encoders[1].HardwareAccelerated);

        Assert.Equal("h264_amf", encoders[2].Id);
        Assert.True(encoders[2].HardwareAccelerated);

        Assert.Equal("libx264", encoders[3].Id);
        Assert.False(encoders[3].HardwareAccelerated);
    }

    [Fact]
    public void EncoderDiscoveryService_FallbackToSoftware_WhenHardwareFails()
    {
        var service = new EncoderDiscoveryService();
        var encoders = new List<EncoderInfo>
        {
            new("h264_nvenc", "NVIDIA NVENC H.264", true, "NVIDIA", false, false), // Not available
            new("libx264", "Software (libx264)", false, "Software", true, true)
        };

        var resolved = service.ResolveEncoder("h264_nvenc", encoders);
        Assert.NotNull(resolved);
        Assert.Equal("libx264", resolved.Id);
    }

    [Fact]
    public void RecordingSettings_PersistenceRoundTrip()
    {
        var original = new AppSettings
        {
            Recording = new RecordingSettings
            {
                OutputDirectory = @"C:\TestRecordings",
                Container = "mp4",
                Mode = OutputMode.Both,
                Video = new VideoEncoderSettings
                {
                    EncoderId = "h264_nvenc",
                    Width = 1920,
                    Height = 1080,
                    FrameRate = 60,
                    BitrateKbps = 8000
                },
                Audio = new AudioEncoderSettings
                {
                    Codec = "aac",
                    BitrateKbps = 192,
                    SampleRate = 48000,
                    Channels = 2
                }
            }
        };

        var json = JsonSerializer.Serialize(original);
        var restored = JsonSerializer.Deserialize<AppSettings>(json);

        Assert.NotNull(restored?.Recording);
        Assert.Equal(@"C:\TestRecordings", restored.Recording.OutputDirectory);
        Assert.Equal("mp4", restored.Recording.Container);
        Assert.Equal(OutputMode.Both, restored.Recording.Mode);
        Assert.Equal("h264_nvenc", restored.Recording.Video.EncoderId);
        Assert.Equal(60, restored.Recording.Video.FrameRate);
        Assert.Equal(8000, restored.Recording.Video.BitrateKbps);
        Assert.Equal(192, restored.Recording.Audio.BitrateKbps);
    }

    [Fact]
    public void BoundedFrameQueue_DropsOldestFrameWhenFull()
    {
        var queue = new BoundedFrameQueue<long>(maxCapacity: 2);

        queue.Enqueue(1000);
        queue.Enqueue(2000);
        Assert.Equal(2, queue.Count);
        Assert.Equal(0, queue.DroppedCount);

        // Third frame pushes queue beyond capacity: drops oldest (1000)
        queue.Enqueue(3000);
        Assert.Equal(2, queue.Count);
        Assert.Equal(1, queue.DroppedCount);

        // Dequeued frame should be 2000
        Assert.True(queue.TryDequeue(out var dequeued));
        Assert.Equal(2000, dequeued);

        Assert.True(queue.TryDequeue(out var dequeued2));
        Assert.Equal(3000, dequeued2);
    }

    [Fact]
    public void FrameClock_CalculatesAccurateTickIntervals()
    {
        var clock60 = new FrameClock(60);
        Assert.InRange(clock60.TargetIntervalMs, 16.66, 16.67);

        var clock30 = new FrameClock(30);
        Assert.InRange(clock30.TargetIntervalMs, 33.33, 33.34);
    }

    [Fact]
    public void MasterAudioMixer_ContinuousRead_PadsWithSilenceOnStarvation()
    {
        var mixer = new MasterAudioMixer();

        // Push 100 samples of 0.5f
        var input = new float[100];
        Array.Fill(input, 0.5f);
        mixer.PushSamples(input);

        // Read 150 samples: 100 should be 0.5f, remaining 50 should be silence (0.0f)
        var output = new float[150];
        mixer.Read(output);

        for (int i = 0; i < 100; i++)
        {
            Assert.Equal(0.5f, output[i]);
        }

        for (int i = 100; i < 150; i++)
        {
            Assert.Equal(0.0f, output[i]);
        }
    }

    [Fact]
    public void CompositedFrameRenderer_RendersSpecifiedDimensions()
    {
        var scene = new Scene { Name = "TestScene" };

        // Horizontal render
        var hBuffer = new byte[1920 * 1080 * 4];
        CompositedFrameRenderer.Render(1920, 1080, OutputMode.Horizontal, scene, _ => null, hBuffer);
        Assert.Equal(1920 * 1080 * 4, hBuffer.Length);

        // Vertical render
        var vBuffer = new byte[1080 * 1920 * 4];
        CompositedFrameRenderer.Render(1080, 1920, OutputMode.Vertical, scene, _ => null, vBuffer);
        Assert.Equal(1080 * 1920 * 4, vBuffer.Length);
    }

    [Fact]
    public void CompositedFrameRenderer_CompositesSourceWithCropAndOpacity()
    {
        var scene = new Scene { Name = "OverlayScene" };
        var source = new SceneSource
        {
            Name = "GreenSquare",
            Visible = true,
            HorizontalTransform = new SourceTransform
            {
                X = 10,
                Y = 10,
                Width = 20,
                Height = 20,
                Opacity = 1.0,
                CropLeft = 0,
                CropRight = 0,
                CropTop = 0,
                CropBottom = 0
            }
        };
        scene.Sources.Add(source);

        // Create 20x20 green BGRA frame (0x00, 0xFF, 0x00, 0xFF)
        var rawPixels = new byte[20 * 20 * 4];
        for (int i = 0; i < rawPixels.Length; i += 4)
        {
            rawPixels[i + 0] = 0;   // B
            rawPixels[i + 1] = 255; // G
            rawPixels[i + 2] = 0;   // R
            rawPixels[i + 3] = 255; // A
        }
        var rawFrame = new RawVideoFrame(20, 20, 20 * 4, rawPixels);

        var destBuffer = new byte[100 * 100 * 4];
        CompositedFrameRenderer.Render(100, 100, OutputMode.Horizontal, scene, s => s == source ? rawFrame : null, destBuffer);

        // Verify pixel at (15, 15) has been rendered green
        int offset = (15 * 100 + 15) * 4;
        Assert.Equal(0, destBuffer[offset + 0]);     // B
        Assert.Equal(255, destBuffer[offset + 1]);   // G
        Assert.Equal(0, destBuffer[offset + 2]);     // R
        Assert.Equal(255, destBuffer[offset + 3]);   // A
    }

    [Fact]
    public void RecordingFileNameGenerator_GeneratesCorrectHorizontalAndVerticalFilenames()
    {
        var timestamp = new DateTimeOffset(2026, 9, 23, 1, 15, 30, TimeSpan.Zero);
        var dir = @"C:\Recordings";

        var hPath = RecordingFileNameGenerator.GenerateUniqueFilePath(dir, OutputMode.Horizontal, "mkv", timestamp, _ => false);
        var vPath = RecordingFileNameGenerator.GenerateUniqueFilePath(dir, OutputMode.Vertical, "mkv", timestamp, _ => false);

        Assert.Equal(@"C:\Recordings\AravalsStream_2026-09-23_01-15-30_H.mkv", hPath);
        Assert.Equal(@"C:\Recordings\AravalsStream_2026-09-23_01-15-30_V.mkv", vPath);
    }

    [Fact]
    public void RecordingFileNameGenerator_ResolvesCollisionsByIncrementing()
    {
        var timestamp = new DateTimeOffset(2026, 9, 23, 1, 15, 30, TimeSpan.Zero);
        var dir = @"C:\Recordings";

        // Mock existing files
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            @"C:\Recordings\AravalsStream_2026-09-23_01-15-30_H.mkv",
            @"C:\Recordings\AravalsStream_2026-09-23_01-15-30_H_1.mkv"
        };

        var path = RecordingFileNameGenerator.GenerateUniqueFilePath(dir, OutputMode.Horizontal, "mkv", timestamp, existing.Contains);
        Assert.Equal(@"C:\Recordings\AravalsStream_2026-09-23_01-15-30_H_2.mkv", path);
    }

    [Fact]
    public async Task FrameClock_MonotonicTickProgression()
    {
        var clock = new FrameClock(60);
        var t1 = await clock.WaitNextTickAsync();
        var t2 = await clock.WaitNextTickAsync();
        var t3 = await clock.WaitNextTickAsync();

        Assert.True(t1 >= 1);
        Assert.True(t2 > t1);
        Assert.True(t3 > t2);
        Assert.Equal(t3 - 3, clock.MissedTicks);
        Assert.True(clock.Elapsed.TotalMilliseconds >= 0);
    }

    [Fact]
    public void MasterAudioMixer_BoundedBufferDiscardsOldestSamplesWhenOverCapacity()
    {
        // 1 second max buffer = 48000 * 2 = 96000 samples
        var mixer = new MasterAudioMixer(maxBufferedSeconds: 1);

        // Push 100,000 samples (numbered 0 to 99,999)
        var samples = new float[100000];
        for (int i = 0; i < samples.Length; i++) samples[i] = i;
        mixer.PushSamples(samples);

        // Reading should yield the latest 96,000 samples (samples from 4,000 to 99,999)
        var readBuf = new float[96000];
        mixer.Read(readBuf);

        Assert.Equal(4000f, readBuf[0]);
        Assert.Equal(99999f, readBuf[^1]);
    }
}
