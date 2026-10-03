using System.Text.Json;
using AravalsStream.Core.Models;
using AravalsStream.Core.Settings;
using AravalsStream.Core.Services;
using Xunit;

namespace AravalsStream.Tests;

public sealed class DestinationTests
{
    [Fact]
    public void DestinationSettings_RoundTripIndependentOutputsAndSecretReferences()
    {
        var a = new Destination { Name = "Main", StreamUrl = "rtmps://example.test/live", StreamKeyReference = Guid.NewGuid().ToString(), VideoBitrateKbps = 8000, AudioBitrateKbps = 192, FrameRate = 60, EncoderId = "h264_nvenc", OutputMode = OutputMode.Horizontal };
        var b = new Destination { Name = "Vertical", StreamUrl = "rtmp://example.test/live", VideoBitrateKbps = 5000, AudioBitrateKbps = 160, FrameRate = 30, EncoderId = "libx264", OutputMode = OutputMode.Vertical };
        var json = JsonSerializer.Serialize(new AppSettings { Destinations = [a, b] });
        var loaded = JsonSerializer.Deserialize<AppSettings>(json)!;
        Assert.Equal(2, loaded.Destinations.Count);
        Assert.Equal(8000, loaded.Destinations[0].VideoBitrateKbps);
        Assert.Equal(5000, loaded.Destinations[1].VideoBitrateKbps);
        Assert.Equal(192, loaded.Destinations[0].AudioBitrateKbps);
        Assert.Equal(30, loaded.Destinations[1].FrameRate);
        Assert.Equal("h264_nvenc", loaded.Destinations[0].EncoderId);
        Assert.Equal(OutputMode.Vertical, loaded.Destinations[1].OutputMode);
        Assert.Equal(a.StreamKeyReference, loaded.Destinations[0].StreamKeyReference);
        Assert.DoesNotContain("stream-secret", json);
    }

    [Fact]
    public void EditingAndDeletingAffectsOnlySelectedDestination()
    {
        var a = new Destination { VideoBitrateKbps = 4500 };
        var b = new Destination { VideoBitrateKbps = 6000 };
        var settings = new AppSettings { Destinations = [a, b] };
        var edited = a.Copy(); edited.VideoBitrateKbps = 8000;
        settings.Destinations[0] = edited;
        Assert.Equal(6000, settings.Destinations[1].VideoBitrateKbps);
        settings.Destinations.Remove(edited);
        Assert.Single(settings.Destinations);
        Assert.Same(b, settings.Destinations[0]);
    }
    [Theory]
    [InlineData("", true, "Server URL")]
    [InlineData("https://example.test/live", true, "Server URL")]
    [InlineData("rtmp://example.test/live", false, "stream key")]
    public void DestinationValidation_RejectsInvalidConfiguration(string url, bool keyAvailable, string expected)
    {
        var destination = new Destination { StreamUrl = url };
        Assert.Contains(expected, DestinationValidation.Validate(destination, keyAvailable)!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DestinationValidation_AcceptsCustomBitrateAndIndependentFps()
    {
        var destination = new Destination { StreamUrl = "rtmps://example.test/live", Protocol = "RTMPS", VideoBitrateKbps = 4500,
            AudioBitrateKbps = 192, FrameRate = 30, OutputMode = OutputMode.Vertical };
        Assert.Null(DestinationValidation.Validate(destination, true));
        destination.VideoBitrateKbps = 8000;
        Assert.Null(DestinationValidation.Validate(destination, true));
    }    [Fact]
    public async Task JsonSettingsService_PersistsEditAndDeletionAcrossReload()
    {
        var directory = Path.Combine(Path.GetTempPath(), "aravals-destination-test-" + Guid.NewGuid().ToString("N"));
        var file = Path.Combine(directory, "settings.json");
        try
        {
            var service = new JsonSettingsService(file);
            var a = new Destination { Name = "A", VideoBitrateKbps = 4500 };
            var b = new Destination { Name = "B", VideoBitrateKbps = 6000 };
            await service.SaveAsync(new AppSettings { Destinations = [a, b] });
            var loaded = await service.LoadAsync();
            loaded.Destinations[0].VideoBitrateKbps = 8000;
            loaded.Destinations.RemoveAt(1);
            await service.SaveAsync(loaded);
            var restarted = await service.LoadAsync();
            Assert.Single(restarted.Destinations);
            Assert.Equal(8000, restarted.Destinations[0].VideoBitrateKbps);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }    [Fact]
    public void DuplicateGetsNewIdAndNoSecretButCopiesOutputSettings()
    {
        var source = new Destination { StreamKeyReference = Guid.NewGuid().ToString(),
            OutputMode = OutputMode.Vertical, FrameRate = 30, VideoBitrateKbps = 3500,
            AudioBitrateKbps = 192, EncoderId = "h264_nvenc", AutoReconnect = false,
            KeyframeIntervalSeconds = 3 };
        var copy = source.Duplicate();
        Assert.NotEqual(source.Id, copy.Id);
        Assert.Null(copy.StreamKeyReference);
        Assert.False(copy.Enabled);
        Assert.Equal(source.OutputMode, copy.OutputMode);
        Assert.Equal(source.FrameRate, copy.FrameRate);
        Assert.Equal(source.VideoBitrateKbps, copy.VideoBitrateKbps);
        Assert.Equal(source.AudioBitrateKbps, copy.AudioBitrateKbps);
        Assert.Equal(source.EncoderId, copy.EncoderId);
        Assert.Equal(source.AutoReconnect, copy.AutoReconnect);
        Assert.Equal(source.KeyframeIntervalSeconds, copy.KeyframeIntervalSeconds);
    }}




