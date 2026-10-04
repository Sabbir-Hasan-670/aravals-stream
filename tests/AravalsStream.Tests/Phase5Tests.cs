using System.Text.Json;
using AravalsStream.Core.Models;
using AravalsStream.Core.Services;
using AravalsStream.Core.Settings;
using AravalsStream.Core.Audio;
using Xunit;

namespace AravalsStream.Tests;

public sealed class Phase5Tests
{
    [Fact]
    public void RecoveryBackoffIsBoundedAndDeviceNotificationAdvancesRetry()
    {
        var recovery = new CaptureRecovery();
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        recovery.Failed(id, now);
        Assert.Empty(recovery.Due(now));
        Assert.Equal(id, Assert.Single(recovery.Due(now.AddSeconds(1))));
        recovery.Failed(id, now.AddSeconds(1));
        Assert.Empty(recovery.Due(now.AddSeconds(2)));
        recovery.DeviceChanged(now.AddSeconds(2));
        Assert.Equal(id, Assert.Single(recovery.Due(now.AddSeconds(2))));
        recovery.Recovered(id);
        Assert.Empty(recovery.Due(now.AddMinutes(1)));
    }

    [Fact]
    public void WindowCaptureRecoveryRetriesQuicklyWithoutDoublingBackoff()
    {
        var recovery = new CaptureRecovery();
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        recovery.Failed(id, now, retrySoon: true);
        Assert.Empty(recovery.Due(now.AddMilliseconds(1999)));
        Assert.Equal(id, Assert.Single(recovery.Due(now.AddSeconds(2))));

        recovery.Defer(id, now.AddSeconds(2));
        Assert.Empty(recovery.Due(now.AddMilliseconds(3999)));
        Assert.Equal(id, Assert.Single(recovery.Due(now.AddSeconds(4))));
    }

    [Fact]
    public void RecommendedFormatUsesSupported720p30OverUnsupportedAssumptions()
    {
        var selected = CameraFormatSelector.Recommended([
            new(1920, 1080, 60, "MJPG"), new(1280, 720, 30, "NV12"), new(640, 480, 30, "YUY2")]);
        Assert.Equal(new VideoFormatChoice(1280, 720, 30, "NV12"), selected);
    }

    [Fact]
    public void MissingDeviceAndSharedSceneItemsPersist()
    {
        var resource = new CaptureResource { Type = SourceType.Camera, DeviceId = "stable-device-link",
            Name = "Camera", FormatWidth = 1280, FormatHeight = 720, FormatFrameRate = 30,
            FormatSubtype = "NV12" };
        var one = new Scene(); var two = new Scene();
        one.Sources.Add(new SceneSource { Type = SourceType.Camera, SourceReference = resource.Id });
        two.Sources.Add(new SceneSource { Type = SourceType.Camera, SourceReference = resource.Id });
        var restored = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(new AppSettings
            { CaptureResources = [resource], Scenes = [one, two] }))!;
        Assert.Equal("stable-device-link", restored.CaptureResources[0].DeviceId);
        Assert.Equal("NV12", restored.CaptureResources[0].FormatSubtype);
        Assert.Equal(restored.Scenes[0].Sources[0].SourceReference, restored.Scenes[1].Sources[0].SourceReference);
    }

    [Fact]
    public void DefaultAudioModePersistsSeparatelyFromSpecificDevice()
    {
        var resource = new CaptureResource { Type = SourceType.AudioInput, DeviceId = "system-default-input",
            FollowSystemDefault = true, Volume = 0.6f, Muted = true };
        var restored = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(new AppSettings
            { CaptureResources = [resource] }))!;
        Assert.True(restored.CaptureResources[0].FollowSystemDefault);
        Assert.Equal("system-default-input", restored.CaptureResources[0].DeviceId);
        Assert.Equal(0.6f, restored.CaptureResources[0].Volume);
    }

    [Fact]
    public void Mono44100AudioConvertsToStereo48000AcrossChunks()
    {
        var converter = new AudioFormatConverter(new RawAudioFormat(44100, 1, RawSampleType.Pcm16));
        var raw = new byte[441 * 2];
        for (var i = 0; i < raw.Length; i += 2) { raw[i] = 0; raw[i + 1] = 64; }
        var first = converter.Convert(raw);
        var second = converter.Convert(raw);
        Assert.InRange(first.Length, 958, 962);
        Assert.InRange(second.Length, 958, 962);
        Assert.Equal(first[0], first[1]);
        Assert.InRange(first[0], 0.49f, 0.51f);
    }
}
