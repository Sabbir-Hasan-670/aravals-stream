using AravalsStream.Core.Composition;
using AravalsStream.Core.Recording;
using AravalsStream.Core.Recording.Models;
using AravalsStream.Core.Settings;
using AravalsStream.Core.Models;
using AravalsStream.Core.Services;
using AravalsStream.Core.Streaming;
using Xunit;

namespace AravalsStream.Tests;

public sealed class Phase17PerformanceTests
{
    [Fact]
    public void PooledFrames_AreBoundedAndNotOverwrittenWhileRead()
    {
        var store = new PooledFrameStore(8, 2);
        using (var writer = store.TryBeginWrite()!) { writer.Buffer[0] = 42; writer.Publish(); }
        using var firstReader = store.AcquireLatest()!;
        Assert.Equal(1, firstReader.Version);
        using (var writer = store.TryBeginWrite()!) { writer.Buffer[0] = 91; writer.Publish(); }
        using var secondReader = store.AcquireLatest()!;
        Assert.Equal(2, secondReader.Version);
        Assert.Null(store.TryBeginWrite());
        Assert.Equal(1, store.DroppedWrites);
        Assert.Equal(42, firstReader.Buffer[0]);
        Assert.Equal(91, secondReader.Buffer[0]);
        firstReader.Dispose();
        using var reused = store.TryBeginWrite();
        Assert.NotNull(reused);
        Assert.Same(firstReader.Buffer, reused.Buffer);
    }

    [Fact]
    public void FullCanvasDisplay_OverwritesPreviousFrameAndMissingCaptureClears()
    {
        var scene = new Scene();
        var source = new SceneSource { Type = SourceType.DisplayCapture };
        source.HorizontalTransform.X = 0;
        source.HorizontalTransform.Y = 0;
        source.HorizontalTransform.Width = 2;
        source.HorizontalTransform.Height = 2;
        scene.Sources.Add(source);
        var pixels = new byte[16];
        for (var i = 0; i < pixels.Length; i += 4) { pixels[i] = 71; pixels[i + 3] = 255; }
        var destination = Enumerable.Repeat((byte)19, 16).ToArray();
        CompositedFrameRenderer.Render(2, 2, OutputMode.Horizontal, scene,
            _ => new RawVideoFrame(2, 2, 8, pixels), destination);
        Assert.Equal(pixels, destination);
        CompositedFrameRenderer.Render(2, 2, OutputMode.Horizontal, scene, _ => null, destination);
        Assert.Equal(new byte[] { 0, 0, 0, 255, 0, 0, 0, 255,
            0, 0, 0, 255, 0, 0, 0, 255 }, destination);
    }

    [Fact]
    public void DirtyCanvasCache_ClearsMovedAndRemovedOverlayPixels()
    {
        var scene = new Scene();
        var overlay = new SceneSource { Type = SourceType.ChatOverlay };
        overlay.HorizontalTransform.X = 0;
        overlay.HorizontalTransform.Y = 0;
        overlay.HorizontalTransform.Width = 2;
        overlay.HorizontalTransform.Height = 2;
        scene.Sources.Add(overlay);
        var pixels = new byte[16];
        for (var i = 0; i < pixels.Length; i += 4) { pixels[i + 2] = 200; pixels[i + 3] = 255; }
        var frame = new RawVideoFrame(2, 2, 8, pixels);
        var canvas = new byte[4 * 4 * 4];
        var cache = new CanvasRenderCache();
        CompositedFrameRenderer.Render(4, 4, OutputMode.Horizontal, scene, _ => frame, canvas,
            renderCache: cache);
        Assert.Equal(200, canvas[2]);
        overlay.HorizontalTransform.X = 2;
        CompositedFrameRenderer.Render(4, 4, OutputMode.Horizontal, scene, _ => frame, canvas,
            renderCache: cache);
        Assert.Equal(0, canvas[2]);
        Assert.Equal(200, canvas[2 * 4 + 2]);
        overlay.Visible = false;
        CompositedFrameRenderer.Render(4, 4, OutputMode.Horizontal, scene, _ => frame, canvas,
            renderCache: cache);
        Assert.Equal(0, canvas[2 * 4 + 2]);
    }

    [Fact]
    public void PerformanceModes_ProtectOutputBeforePreview()
    {
        var settings = new PerformanceSettings();
        Assert.Equal(PerformanceMode.Auto, settings.Mode);
        Assert.False(settings.AllowAutomaticStreamQualityReduction);
        Assert.Equal(30, PerformancePolicy.Resolve(settings, HardwareClass.Low, false, false).PreviewFps);
        Assert.Equal(60, PerformancePolicy.Resolve(settings, HardwareClass.High, false, false).PreviewFps);
        Assert.Equal(0, PerformancePolicy.Resolve(settings, HardwareClass.High, true, false).PreviewFps);
        Assert.Equal(10, PerformancePolicy.Resolve(settings, HardwareClass.High, false, true).PreviewFps);
        Assert.Equal(30, PerformancePolicy.Resolve(settings, HardwareClass.Moderate, false, false, hasActiveOutputs: false).PreviewFps);
        Assert.Equal(30, PerformancePolicy.Resolve(settings, HardwareClass.High, false, false, hasActiveOutputs: false).PreviewFps);
        Assert.True(PerformancePolicy.ShouldWarnBeforeStarting(HardwareClass.Low, 2, 2));
        Assert.False(PerformancePolicy.ShouldWarnBeforeStarting(HardwareClass.High, 1, 1));
        Assert.True(PerformancePolicy.ShouldWarnBeforeStarting(HardwareClass.Moderate, 4, 3.0));

        var healthy = NetworkHealthEvaluator.Evaluate(4000, 4000, 0, 600, 60, 60, 0, false, 1.0, MediaCadenceState.Healthy);
        Assert.Equal(NetworkHealthState.Excellent, healthy.State);
        Assert.Equal(MediaCadenceState.Healthy, healthy.CadenceState);

        var degraded = NetworkHealthEvaluator.Evaluate(4000, 4000, 0, 600, 60, 60, 0, false, 0.85, MediaCadenceState.Degraded);
        Assert.Equal(NetworkHealthState.Unstable, degraded.State);

        var critical = NetworkHealthEvaluator.Evaluate(4000, 4000, 0, 600, 60, 60, 0, false, 0.70, MediaCadenceState.Critical);
        Assert.Equal(NetworkHealthState.Critical, critical.State);
    }

    [Fact]
    public void MediaArguments_UseWallClockAndScaleSharedComposition()
    {
        var settings = new RecordingSettings { Video = new VideoEncoderSettings { FrameRate = 30 } };
        var args = FfmpegArgumentBuilder.Build(settings, "libx264", 1920, 1080,
            "video", "audio", "test.mkv", false, 1280, 720);
        Assert.Contains("-use_wallclock_as_timestamps 1", args);
        Assert.Contains("-fps_mode vfr", args);
        Assert.Contains("-vf scale=1280:720:flags=fast_bilinear,setpts=", args);
        Assert.Contains("aresample=async=1000", args);
    }

    [Fact]
    public void YuvConversion_UsesCallerBufferAndProducesNeutralBlack()
    {
        var source = new byte[16];
        source[3] = source[7] = source[11] = source[15] = 255;
        var yuv = new byte[Yuv420FrameConverter.BufferSize(2, 2)];
        Yuv420FrameConverter.Convert(source, yuv, 2, 2);
        Assert.Equal(new byte[] { 16, 16, 16, 16, 128, 128 }, yuv);
        var smaller = new byte[Yuv420FrameConverter.BufferSize(2, 2)];
        Yuv420FrameConverter.ResizeNearest(yuv, 2, 2, smaller, 2, 2);
        Assert.Equal(yuv, smaller);
    }

    [Fact]
    public void YuvResize_MapsEachPlaneWithoutCrossingChromaBoundaries()
    {
        var source = Enumerable.Range(0, Yuv420FrameConverter.BufferSize(4, 4))
            .Select(i => (byte)i).ToArray();
        var target = new byte[Yuv420FrameConverter.BufferSize(2, 2)];
        Yuv420FrameConverter.ResizeNearest(source, 4, 4, target, 2, 2);
        Assert.Equal(new byte[] { 0, 2, 8, 10, 16, 20 }, target);
    }

    [Fact]
    public void CaptureDemand_UsesOnlyVisibleSourcesInActiveSceneAndDeduplicates()
    {
        var active = new Scene();
        active.Sources.Add(new SceneSource { Name = "Display A", Type = SourceType.DisplayCapture, Visible = true });
        active.Sources.Add(new SceneSource { Name = "Display B", Type = SourceType.DisplayCapture, Visible = true });
        active.Sources.Add(new SceneSource { Name = "Camera hidden", Type = SourceType.Camera, Visible = false });
        active.Sources.Add(new SceneSource { Name = "Alert", Type = SourceType.Alerts, Visible = true });
        var keys = CaptureDemand.ActiveResourceKeys(active, source =>
            source.Type == SourceType.DisplayCapture ? "shared-display" : source.Name);
        Assert.Single(keys);
        Assert.Contains("shared-display", keys);
        Assert.Empty(CaptureDemand.ActiveResourceKeys(null, source => source.Name));
    }

    [Fact]
    public void EncoderCompatibility_RequiresActualAudioMixAndEveryOutputSetting()
    {
        var first = new Destination { OutputMode = OutputMode.Horizontal, FrameRate = 60,
            VideoBitrateKbps = 4000, AudioBitrateKbps = 128 };
        var second = new Destination { OutputMode = OutputMode.Horizontal, FrameRate = 60,
            VideoBitrateKbps = 4000, AudioBitrateKbps = 128 };
        var common = OutputCompatibilityKey.For(first, "h264_nvenc", "same-mix");
        Assert.Equal(common, OutputCompatibilityKey.For(second, "h264_nvenc", "same-mix"));
        Assert.NotEqual(common, OutputCompatibilityKey.For(second, "h264_nvenc", "different-mix"));
        second.VideoBitrateKbps++;
        Assert.NotEqual(common, OutputCompatibilityKey.For(second, "h264_nvenc", "same-mix"));
        Assert.Throws<ArgumentException>(() => OutputCompatibilityKey.For(first, "h264_nvenc", ""));
        var groups = OutputCompatibilityKey.CompatibleGroups(new[] { first, second },
            d => OutputCompatibilityKey.For(d, "h264_nvenc", "same-mix"));
        Assert.Equal(2, groups.Count);
    }
}
