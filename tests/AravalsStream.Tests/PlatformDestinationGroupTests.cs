using System.Text.Json;
using AravalsStream.Core.Models;
using AravalsStream.Core.Platforms;
using AravalsStream.Core.Services;
using AravalsStream.Core.Settings;
using Xunit;

namespace AravalsStream.Tests;

public sealed class PlatformDestinationGroupTests
{
    [Fact]
    public void PlatformProfile_LookupByIdNameAndEnum()
    {
        var ytByEnum = PlatformRegistry.Get(PlatformType.YouTube);
        var ytById = PlatformRegistry.Get("youtube");
        var ytByName = PlatformRegistry.Get("YouTube");

        Assert.Equal("youtube", ytByEnum.Id);
        Assert.Equal("YouTube", ytByEnum.DisplayName);
        Assert.Same(ytByEnum, ytById);
        Assert.Same(ytByEnum, ytByName);

        var twitch = PlatformRegistry.Get("Twitch");
        Assert.Equal(PlatformType.Twitch, twitch.PlatformType);
        Assert.True(twitch.SupportsHorizontal);
        Assert.True(twitch.SupportsVertical);

        var tiktok = PlatformRegistry.Get(PlatformType.TikTok);
        Assert.True(tiktok.SupportsHorizontal);
        Assert.True(tiktok.SupportsVertical);
        Assert.False(tiktok.SupportsMultipleOutputs);

        var custom = PlatformRegistry.Get("UnknownPlatform");
        Assert.Equal(PlatformType.Custom, custom.PlatformType);
    }

    [Fact]
    public void GroupCreation_FromProfileSetsAppropriateDefaults()
    {
        var ytProfile = PlatformRegistry.Get(PlatformType.YouTube);
        var group = PlatformDestinationGroup.CreateFromProfile(ytProfile, "My YouTube");

        Assert.Equal("My YouTube", group.Name);
        Assert.Equal(PlatformType.YouTube, group.PlatformType);
        Assert.Equal("YouTube", group.Platform);
        Assert.Equal(RoutingMode.Horizontal, group.Routing);
        Assert.Equal(OutputMode.Horizontal, group.Horizontal.OutputMode);
        Assert.Equal(OutputMode.Vertical, group.Vertical.OutputMode);
        Assert.Equal(6000, group.Horizontal.VideoBitrateKbps);
        Assert.Equal(60, group.Horizontal.FrameRate);
        Assert.Equal(5000, group.Vertical.VideoBitrateKbps);
        Assert.Equal(60, group.Vertical.FrameRate);
    }

    [Fact]
    public void Routing_HorizontalReturnsOnlyHorizontalChild()
    {
        var group = new PlatformDestinationGroup
        {
            Enabled = true,
            Routing = RoutingMode.Horizontal
        };
        group.EnsureChildDestinations();

        var active = group.GetActiveDestinations().ToList();
        Assert.Single(active);
        Assert.Same(group.Horizontal, active[0]);
        Assert.Equal(OutputMode.Horizontal, active[0].OutputMode);
    }

    [Fact]
    public void Routing_VerticalReturnsOnlyVerticalChild()
    {
        var group = new PlatformDestinationGroup
        {
            Enabled = true,
            Routing = RoutingMode.Vertical
        };
        group.EnsureChildDestinations();

        var active = group.GetActiveDestinations().ToList();
        Assert.Single(active);
        Assert.Same(group.Vertical, active[0]);
        Assert.Equal(OutputMode.Vertical, active[0].OutputMode);
    }

    [Fact]
    public void Routing_BothReturnsTwoIndependentChildOutputs()
    {
        var group = new PlatformDestinationGroup
        {
            Enabled = true,
            Routing = RoutingMode.Both
        };
        group.EnsureChildDestinations();

        var active = group.GetActiveDestinations().ToList();
        Assert.Equal(2, active.Count);
        Assert.Contains(group.Horizontal, active);
        Assert.Contains(group.Vertical, active);

        // Verify independent IDs and different formats
        Assert.NotEqual(group.Horizontal.Id, group.Vertical.Id);
        Assert.Equal(OutputMode.Horizontal, group.Horizontal.OutputMode);
        Assert.Equal(OutputMode.Vertical, group.Vertical.OutputMode);
    }

    [Fact]
    public void Routing_OffReturnsNoActiveOutputs()
    {
        var group = new PlatformDestinationGroup
        {
            Enabled = true,
            Routing = RoutingMode.Off
        };
        group.EnsureChildDestinations();

        Assert.Empty(group.GetActiveDestinations());
    }

    [Fact]
    public void IndependentSettings_HVHaveSeparateBitrateFpsAndEncoder()
    {
        var group = new PlatformDestinationGroup
        {
            Name = "YouTube Main",
            Routing = RoutingMode.Both,
            Horizontal = new Destination
            {
                OutputMode = OutputMode.Horizontal,
                VideoBitrateKbps = 8000,
                AudioBitrateKbps = 192,
                FrameRate = 60,
                EncoderId = "h264_nvenc"
            },
            Vertical = new Destination
            {
                OutputMode = OutputMode.Vertical,
                VideoBitrateKbps = 5000,
                AudioBitrateKbps = 128,
                FrameRate = 30,
                EncoderId = "libx264"
            }
        };

        Assert.Equal(8000, group.Horizontal.VideoBitrateKbps);
        Assert.Equal(5000, group.Vertical.VideoBitrateKbps);
        Assert.Equal(60, group.Horizontal.FrameRate);
        Assert.Equal(30, group.Vertical.FrameRate);
        Assert.Equal("h264_nvenc", group.Horizontal.EncoderId);
        Assert.Equal("libx264", group.Vertical.EncoderId);
    }

    [Fact]
    public void GroupStateAggregation_ReflectsLivePartialAndReconnecting()
    {
        var group = new PlatformDestinationGroup
        {
            Enabled = true,
            Routing = RoutingMode.Both
        };
        group.EnsureChildDestinations();

        // 1. Both Live
        group.Horizontal.Status = DestinationStatus.Live;
        group.Vertical.Status = DestinationStatus.Live;
        group.UpdateAggregation();
        Assert.Equal(DestinationGroupStatus.Live, group.Status);
        Assert.Equal("LIVE", group.StatusSummary);

        // 2. H Live, V Reconnecting -> Partial
        group.Vertical.Status = DestinationStatus.Reconnecting;
        group.UpdateAggregation();
        Assert.Equal(DestinationGroupStatus.Partial, group.Status);
        Assert.Contains("H LIVE", group.StatusSummary);
        Assert.Contains("V RECONNECTING", group.StatusSummary);

        // 3. H Live, V Error -> Partial
        group.Vertical.Status = DestinationStatus.Error;
        group.UpdateAggregation();
        Assert.Equal(DestinationGroupStatus.Partial, group.Status);

        // 4. Both Error -> Error
        group.Horizontal.Status = DestinationStatus.Error;
        group.Vertical.Status = DestinationStatus.Error;
        group.UpdateAggregation();
        Assert.Equal(DestinationGroupStatus.Error, group.Status);

        // 5. Disabled group -> Disabled
        group.Enabled = false;
        group.UpdateAggregation();
        Assert.Equal(DestinationGroupStatus.Disabled, group.Status);
    }

    [Fact]
    public void BandwidthCalculation_SumsOnlyActiveChildrenOfEnabledGroups()
    {
        var g1 = new PlatformDestinationGroup
        {
            Enabled = true,
            Routing = RoutingMode.Both,
            Horizontal = new Destination { VideoBitrateKbps = 8000, AudioBitrateKbps = 160 },
            Vertical = new Destination { VideoBitrateKbps = 5000, AudioBitrateKbps = 160 }
        };
        var g2 = new PlatformDestinationGroup
        {
            Enabled = true,
            Routing = RoutingMode.Horizontal,
            Horizontal = new Destination { VideoBitrateKbps = 6000, AudioBitrateKbps = 160 },
            Vertical = new Destination { VideoBitrateKbps = 4000, AudioBitrateKbps = 160 } // Not active
        };
        var g3 = new PlatformDestinationGroup
        {
            Enabled = false, // Disabled
            Routing = RoutingMode.Both,
            Horizontal = new Destination { VideoBitrateKbps = 7000, AudioBitrateKbps = 160 },
            Vertical = new Destination { VideoBitrateKbps = 4000, AudioBitrateKbps = 160 }
        };

        var groups = new[] { g1, g2, g3 };
        var totalKbps = StreamSessionSummary.EstimatedUploadKbps(groups);
        // g1: 8160 + 5160 = 13320
        // g2: 6160
        // g3: 0 (disabled)
        // total: 19480 kbps
        Assert.Equal(19480, totalKbps);

        var totalMbps = StreamSessionSummary.EstimatedUploadMbps(groups);
        Assert.Equal(19.48, totalMbps, 2);
    }

    [Fact]
    public void LegacyDestinationMigration_MigratesOldDestinationsToGroups()
    {
        var legacyH = new Destination
        {
            Name = "Old YouTube H",
            Platform = "YouTube",
            StreamUrl = "rtmp://youtube.test/live",
            StreamKeyReference = "ref_yt_1",
            OutputMode = OutputMode.Horizontal,
            VideoBitrateKbps = 7500,
            AudioBitrateKbps = 160,
            FrameRate = 60
        };

        var legacyV = new Destination
        {
            Name = "Old TikTok V",
            Platform = "TikTok",
            StreamUrl = "rtmp://tiktok.test/live",
            StreamKeyReference = "ref_tt_1",
            OutputMode = OutputMode.Vertical,
            VideoBitrateKbps = 3500,
            AudioBitrateKbps = 128,
            FrameRate = 30
        };

        var settings = new AppSettings
        {
            Destinations = [legacyH, legacyV],
            DestinationGroups = []
        };

        JsonSettingsService.MigrateSettings(settings);

        Assert.Equal(2, settings.DestinationGroups.Count);

        var g1 = settings.DestinationGroups[0];
        Assert.Equal("Old YouTube H", g1.Name);
        Assert.Equal(RoutingMode.Horizontal, g1.Routing);
        Assert.Equal(7500, g1.Horizontal.VideoBitrateKbps);
        Assert.Equal("ref_yt_1", g1.StreamKeyReference);

        var g2 = settings.DestinationGroups[1];
        Assert.Equal("Old TikTok V", g2.Name);
        Assert.Equal(RoutingMode.Vertical, g2.Routing);
        Assert.Equal(3500, g2.Vertical.VideoBitrateKbps);
        Assert.Equal("ref_tt_1", g2.StreamKeyReference);
    }

    [Fact]
    public void DestinationPersistence_RoundTripsAllPropertiesAndSecureSecrets()
    {
        var group = new PlatformDestinationGroup
        {
            Name = "YouTube Pro",
            Platform = "YouTube",
            PlatformType = PlatformType.YouTube,
            Routing = RoutingMode.Both,
            Enabled = true,
            Order = 2,
            LinkSettings = false,
            ServerUrl = "rtmps://a.rtmps.youtube.com/live2",
            StreamKeyReference = "secret_ref_12345",
            Horizontal = new Destination
            {
                Name = "YouTube Pro - H",
                OutputMode = OutputMode.Horizontal,
                StreamUrl = "rtmps://a.rtmps.youtube.com/live2",
                StreamKeyReference = "secret_ref_12345",
                VideoBitrateKbps = 9000,
                AudioBitrateKbps = 192,
                FrameRate = 60,
                EncoderId = "h264_nvenc"
            },
            Vertical = new Destination
            {
                Name = "YouTube Pro - V",
                OutputMode = OutputMode.Vertical,
                StreamUrl = "rtmps://a.rtmps.youtube.com/live2",
                StreamKeyReference = "secret_ref_12345",
                VideoBitrateKbps = 5500,
                AudioBitrateKbps = 160,
                FrameRate = 30,
                EncoderId = "h264_qsv"
            }
        };

        var settings = new AppSettings { DestinationGroups = [group] };
        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
        var loaded = JsonSerializer.Deserialize<AppSettings>(json)!;

        Assert.Single(loaded.DestinationGroups);
        var loadedGroup = loaded.DestinationGroups[0];

        Assert.Equal(group.Id, loadedGroup.Id);
        Assert.Equal("YouTube Pro", loadedGroup.Name);
        Assert.Equal(PlatformType.YouTube, loadedGroup.PlatformType);
        Assert.Equal(RoutingMode.Both, loadedGroup.Routing);
        Assert.Equal(2, loadedGroup.Order);
        Assert.False(loadedGroup.LinkSettings);
        Assert.Equal("secret_ref_12345", loadedGroup.StreamKeyReference);

        Assert.Equal(9000, loadedGroup.Horizontal.VideoBitrateKbps);
        Assert.Equal(5500, loadedGroup.Vertical.VideoBitrateKbps);
        Assert.Equal(60, loadedGroup.Horizontal.FrameRate);
        Assert.Equal(30, loadedGroup.Vertical.FrameRate);
        Assert.Equal("h264_nvenc", loadedGroup.Horizontal.EncoderId);
        Assert.Equal("h264_qsv", loadedGroup.Vertical.EncoderId);

        // Security check: raw secret was never serialized into JSON
        Assert.DoesNotContain("secret-raw-value", json);
    }

    [Fact]
    public void DuplicateDestination_CanExcludeOrIncludeSecrets()
    {
        var group = new PlatformDestinationGroup
        {
            Name = "Twitch Main",
            StreamKeyReference = "secret_twitch_abc",
            Horizontal = new Destination { VideoBitrateKbps = 6000 }
        };

        // Without secret copying
        var dupNoSecret = group.Duplicate(copySecrets: false);
        Assert.NotEqual(group.Id, dupNoSecret.Id);
        Assert.NotEqual(group.Horizontal.Id, dupNoSecret.Horizontal.Id);
        Assert.Equal("Twitch Main Copy", dupNoSecret.Name);
        Assert.Null(dupNoSecret.StreamKeyReference);
        Assert.Null(dupNoSecret.Horizontal.StreamKeyReference);
        Assert.False(dupNoSecret.Enabled);

        // With secret copying
        var dupWithSecret = group.Duplicate(copySecrets: true);
        Assert.Equal("secret_twitch_abc", dupWithSecret.StreamKeyReference);
    }

    [Fact]
    public void DestinationValidation_WarnsWhenBitrateExceedsProfileRecommendedMax()
    {
        var group = new PlatformDestinationGroup
        {
            Name = "Twitch Test",
            PlatformType = PlatformType.Twitch,
            Routing = RoutingMode.Horizontal,
            ServerUrl = "rtmp://live.twitch.tv/app",
            Horizontal = new Destination
            {
                Name = "Twitch Test - H",
                OutputMode = OutputMode.Horizontal,
                StreamUrl = "rtmp://live.twitch.tv/app",
                VideoBitrateKbps = 9500 // Exceeds Twitch max of 8000
            }
        };

        var warnings = DestinationValidation.GetWarnings(group);
        Assert.Single(warnings);
        Assert.Contains("9500", warnings[0]);
        Assert.Contains("Twitch", warnings[0]);
    }
}
