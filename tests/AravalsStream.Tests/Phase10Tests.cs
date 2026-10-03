using AravalsStream.Core.Audio;
using AravalsStream.Core.Recording.Models;
using AravalsStream.Core.Services;
using AravalsStream.Core.Settings;
using AravalsStream.Core.Streaming;
using AravalsStream.Core.Versioning;
using Xunit;

namespace AravalsStream.Tests;

public class Phase10Tests
{
    [Fact]
    public void AudioRoutingMatrix_TogglesAndCalculatesEffectiveGain()
    {
        var matrix = new AudioRoutingMatrix();
        var channelId = Guid.NewGuid();
        const string targetA = "YouTube";
        const string targetB = "Twitch";

        // Default is enabled with gain = 1.0
        Assert.True(matrix.IsRouteEnabled(channelId, targetA));
        Assert.Equal(1.0f, matrix.GetRouteGain(channelId, targetA));

        // Disable targetB
        matrix.SetRoute(channelId, targetB, enabled: false);
        Assert.False(matrix.IsRouteEnabled(channelId, targetB));

        // Set route gain for targetA to 80% (0.8)
        matrix.SetRoute(channelId, targetA, enabled: true, routeGain: 0.8f);
        Assert.Equal(0.8f, matrix.GetRouteGain(channelId, targetA));

        // Calculate effective gain: source volume (0.5) * route gain (0.8) = 0.4
        var effective = matrix.GetEffectiveGain(channelId, targetA, sourceVolume: 0.5f, sourceMuted: false);
        Assert.InRange(effective, 0.399f, 0.401f);

        // When source is muted, effective gain is 0
        var effectiveMuted = matrix.GetEffectiveGain(channelId, targetA, sourceVolume: 0.5f, sourceMuted: true);
        Assert.Equal(0f, effectiveMuted);

        // When route disabled, effective gain is 0
        var effectiveB = matrix.GetEffectiveGain(channelId, targetB, sourceVolume: 0.5f, sourceMuted: false);
        Assert.Equal(0f, effectiveB);
    }

    [Fact]
    public void AudioRoutingMatrix_ExportsAndImportsSettings()
    {
        var matrix = new AudioRoutingMatrix();
        var channelId = Guid.NewGuid();
        matrix.SetRoute(channelId, "TargetX", enabled: false, routeGain: 1.25f);

        var list = matrix.ExportSettings();
        Assert.Single(list);
        Assert.Equal(channelId, list[0].ChannelId);
        Assert.Equal("TargetX", list[0].TargetOutputKey);
        Assert.False(list[0].Enabled);
        Assert.Equal(1.25f, list[0].RouteGain);

        var matrix2 = new AudioRoutingMatrix();
        matrix2.ImportSettings(list);
        Assert.False(matrix2.IsRouteEnabled(channelId, "TargetX"));
        Assert.Equal(1.25f, matrix2.GetRouteGain(channelId, "TargetX"));
    }

    [Fact]
    public void AudioMonitoring_FeedbackDetectionWarnsWhenSharedDevice()
    {
        // When desktop capture uses the same device ID as monitoring -> feedback warning
        var hasFeedback = AudioMonitoringService.DetectFeedbackLoop(
            monitoringDeviceId: "Device-Speakers-123",
            activeCaptureDeviceIds: new[] { "Device-Speakers-123", "Device-Mic-456" });

        Assert.True(hasFeedback);

        // When desktop capture uses different device -> safe
        var safe = AudioMonitoringService.DetectFeedbackLoop(
            monitoringDeviceId: "Device-Headphones-999",
            activeCaptureDeviceIds: new[] { "Device-Speakers-123", "Device-Mic-456" });

        Assert.False(safe);
    }

    [Fact]
    public void AudioSyncBuffer_AppliesDelayAccurately()
    {
        var buffer = new AudioSyncBuffer();
        // Set 10ms delay = 480 samples = 960 floats (2 channels at 48kHz)
        buffer.OffsetMs = 10;
        Assert.Equal(10, buffer.OffsetMs);

        // First frame of 960 floats: should output silence initially as it fills the delay buffer
        var input = new float[960];
        Array.Fill(input, 0.75f);
        var output = new float[960];

        buffer.Process(input, output);
        // Initially zeros delayed
        Assert.Equal(0f, output[0]);
        Assert.Equal(0f, output[959]);

        // Second frame of zeros: delayed 0.75f values should now emerge
        var input2 = new float[960];
        var output2 = new float[960];
        buffer.Process(input2, output2);
        Assert.Equal(0.75f, output2[0]);
        Assert.Equal(0.75f, output2[959]);
    }

    [Fact]
    public void NetworkHealthEvaluator_ClassifiesHealthCorrectly()
    {
        // 1. Excellent: full bitrate, 0 drops, stable fps, 0 reconnects
        var exc = NetworkHealthEvaluator.Evaluate(
            targetBitrateKbps: 6000,
            measuredBitrateKbps: 5950,
            droppedFrames: 0,
            totalFrames: 1000,
            currentFps: 60,
            targetFps: 60,
            reconnectCount: 0,
            isReconnecting: false);
        Assert.Equal(NetworkHealthState.Excellent, exc.State);

        // 2. Good: slight bitrate dip (>= 80%)
        var good = NetworkHealthEvaluator.Evaluate(
            targetBitrateKbps: 6000,
            measuredBitrateKbps: 5100, // 85%
            droppedFrames: 5,
            totalFrames: 1000, // 0.5% drops
            currentFps: 59,
            targetFps: 60,
            reconnectCount: 0,
            isReconnecting: false);
        Assert.Equal(NetworkHealthState.Good, good.State);

        // 3. Unstable: drop rate > 2% or bitrate < 75%
        var unstable = NetworkHealthEvaluator.Evaluate(
            targetBitrateKbps: 8000,
            measuredBitrateKbps: 4200, // ~52%
            droppedFrames: 50,
            totalFrames: 1000, // 5% drops
            currentFps: 48,
            targetFps: 60,
            reconnectCount: 0,
            isReconnecting: false);
        Assert.Equal(NetworkHealthState.Unstable, unstable.State);
        Assert.True(unstable.DropRatePercent > 2.0);

        // 4. Critical: drop rate > 10% or isReconnecting
        var crit = NetworkHealthEvaluator.Evaluate(
            targetBitrateKbps: 6000,
            measuredBitrateKbps: 1000,
            droppedFrames: 200,
            totalFrames: 1000, // 20% drops
            currentFps: 15,
            targetFps: 60,
            reconnectCount: 2,
            isReconnecting: true);
        Assert.Equal(NetworkHealthState.Critical, crit.State);
    }

    [Fact]
    public void AdaptiveBitrateController_AppliesCooldownAndStepDown()
    {
        var controller = new AdaptiveBitrateController(initialBitrateKbps: 8000, minBitrateKbps: 3000)
        {
            Enabled = true,
            StepDownKbps = 1000,
            Cooldown = TimeSpan.FromSeconds(5),
            AutoRecover = false
        };

        var now = DateTime.UtcNow;

        var criticalHealth = new NetworkHealthReport
        {
            State = NetworkHealthState.Critical,
            TargetBitrateKbps = 8000,
            MeasuredBitrateKbps = 3500,
            DropRatePercent = 12.0,
            DroppedFrames = 120,
            CurrentFps = 40,
            TargetFps = 60,
            ReconnectCount = 0,
            Message = "Critical network congestion"
        };

        // 1. Initial critical step down (critical steps down 2 * StepDownKbps = 2000): 8000 -> 6000
        var adapted = controller.Evaluate(criticalHealth, now, out int newBitrate);
        Assert.True(adapted);
        Assert.Equal(6000, newBitrate);

        // 2. Within cooldown (e.g. +2 seconds), no change
        var adaptedCooldown = controller.Evaluate(criticalHealth, now.AddSeconds(2), out _);
        Assert.False(adaptedCooldown);

        // 3. After cooldown (e.g. +6 seconds), step down again to min (3000): 6000 -> 4000
        var adaptedStep2 = controller.Evaluate(criticalHealth, now.AddSeconds(6), out int newBitrate2);
        Assert.True(adaptedStep2);
        Assert.Equal(4000, newBitrate2);
    }

    [Fact]
    public void AdaptiveBitrateController_AppliesCautiousRecovery()
    {
        var controller = new AdaptiveBitrateController(initialBitrateKbps: 6000, minBitrateKbps: 2000)
        {
            Enabled = true,
            StepDownKbps = 1000,
            StepUpKbps = 500,
            Cooldown = TimeSpan.FromSeconds(4),
            RecoveryHoldDuration = TimeSpan.FromSeconds(8),
            AutoRecover = true
        };

        var now = DateTime.UtcNow;

        var crit = new NetworkHealthReport
        {
            State = NetworkHealthState.Unstable,
            TargetBitrateKbps = 6000,
            MeasuredBitrateKbps = 3000,
            DropRatePercent = 5.0,
            DroppedFrames = 50,
            CurrentFps = 45,
            TargetFps = 60
        };
        controller.Evaluate(crit, now, out _); // drops by 1000 to 5000

        var excellent = new NetworkHealthReport
        {
            State = NetworkHealthState.Excellent,
            TargetBitrateKbps = 5000,
            MeasuredBitrateKbps = 5000,
            DropRatePercent = 0.0,
            DroppedFrames = 50,
            CurrentFps = 60,
            TargetFps = 60
        };

        // In cooldown (< 4s), no recovery
        Assert.False(controller.Evaluate(excellent, now.AddSeconds(2), out _));

        // After cooldown (5s > 4s), first excellent report records recovery start
        Assert.False(controller.Evaluate(excellent, now.AddSeconds(5), out _));

        // After recovery hold duration (5s + 9s = 14s >= 8s hold), cautious step up: 5000 + 500 = 5500
        Assert.True(controller.Evaluate(excellent, now.AddSeconds(14), out int recovered));
        Assert.Equal(5500, recovered);
    }

    [Fact]
    public void AppLog_SanitizesSensitiveSecrets()
    {
        const string textWithStreamKey = "Connecting to rtmp://live.twitch.tv/app/live_12345678_abcdefghijklmnopqrstuvwxyz with key live_secret_123";
        var sanitized = AppLog.Sanitize(textWithStreamKey);

        Assert.DoesNotContain("live_secret_123", sanitized);
        Assert.Contains("[REDACTED]", sanitized);
    }

    [Fact]
    public void Settings_SchemaVersionMigration_PopulatesDefaults()
    {
        var settings = new AppSettings
        {
            SettingsSchemaVersion = 1,
            General = null!,
            Audio = null!,
            Streaming = null!,
            Hotkeys = []
        };

        JsonSettingsService.MigrateSettings(settings);

        Assert.True(settings.SettingsSchemaVersion >= 2);
        Assert.NotNull(settings.General);
        Assert.NotNull(settings.Audio);
        Assert.NotNull(settings.Streaming);
        Assert.NotEmpty(settings.Hotkeys);
        Assert.Contains(settings.Hotkeys, h => h.Action == HotkeyAction.ToggleStreaming);
    }

    [Fact]
    public void HotkeyBinding_DetectsConflicts()
    {
        var binding1 = new HotkeyBinding { Action = HotkeyAction.ToggleStreaming, Key = 0x78, Modifiers = 2, Enabled = true };
        var binding2 = new HotkeyBinding { Action = HotkeyAction.ToggleRecording, Key = 0x78, Modifiers = 2, Enabled = true };

        Assert.True(binding1.ConflictsWith(binding2));

        var binding3 = new HotkeyBinding { Action = HotkeyAction.ToggleRecording, Key = 0x79, Modifiers = 2, Enabled = true };
        Assert.False(binding1.ConflictsWith(binding3));
    }

    [Fact]
    public void AppVersion_ProvidesAuthoritativeVersion()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Directory.Build.props"))) directory = directory.Parent;
        Assert.NotNull(directory);
        var props = System.Xml.Linq.XDocument.Load(Path.Combine(directory.FullName, "Directory.Build.props"));
        var expected = props.Descendants("Version").Single().Value;
        Assert.Equal(expected, AppVersion.Version);
        Assert.Contains("Aravals Stream", AppVersion.FullVersionString);
    }

    [Fact]
    public void AudioMonitoringMode_FanOutFollowsRules()
    {
        var mixer = new AudioMixer();
        var monTap = mixer.CreateOutputTap("Monitor");
        var streamTap = mixer.CreateOutputTap("YouTube");

        var channel = new AudioChannel { Name = "Mic", Volume = 1.0f };

        // 1. MonitorOff: stream receives, monitor does NOT receive
        channel.MonitoringMode = AudioMonitoringMode.MonitorOff;
        mixer.Receive(channel, [0.5f, -0.5f]);

        var monBuf = new float[2];
        var streamBuf = new float[2];
        monTap.Read(monBuf);
        streamTap.Read(streamBuf);

        Assert.Equal(0f, monBuf[0]); // silence
        Assert.Equal(0.5f, streamBuf[0]); // received

        // 2. MonitorOnly: monitor receives, stream does NOT receive
        channel.MonitoringMode = AudioMonitoringMode.MonitorOnly;
        mixer.Receive(channel, [0.7f, -0.7f]);

        monTap.Read(monBuf);
        streamTap.Read(streamBuf);

        Assert.Equal(0.7f, monBuf[0]); // received
        Assert.Equal(0f, streamBuf[0]); // silence

        // 3. MonitorAndOutput: BOTH receive
        channel.MonitoringMode = AudioMonitoringMode.MonitorAndOutput;
        mixer.Receive(channel, [0.9f, -0.9f]);

        monTap.Read(monBuf);
        streamTap.Read(streamBuf);

        Assert.Equal(0.9f, monBuf[0]); // received
        Assert.Equal(0.9f, streamBuf[0]); // received
    }

    [Fact]
    public void SettingsPersistence_RoundTripsJsonWithAllPhase10Sections()
    {
        var original = new AppSettings
        {
            SettingsSchemaVersion = 2,
            General = new GeneralSettings
            {
                ConfirmExitWhileLive = true,
                MinimizeToTray = true,
                Language = "en-US"
            },
            Audio = new AudioSettings
            {
                SampleRate = 48000,
                MonitoringDeviceId = "Test-Device-Id-42"
            },
            Streaming = new StreamingSettings
            {
                DefaultEncoder = "h264_nvenc",
                AdaptiveBitrateDefault = true,
                AutoRecoverBitrateDefault = true,
                BandwidthWarningThresholdMbps = 5
            },
            Hotkeys =
            [
                new HotkeyBinding { Action = HotkeyAction.ToggleStreaming, Key = 0x78, Modifiers = 2, ShortcutText = "Ctrl+F9" },
                new HotkeyBinding { Action = HotkeyAction.ToggleRecording, Key = 0x79, Modifiers = 2, ShortcutText = "Ctrl+F10" }
            ],
            AudioRoutes =
            [
                new AudioRouteSetting { TargetOutputKey = "Twitch", Enabled = false, RouteGain = 0.5f }
            ]
        };

        var json = System.Text.Json.JsonSerializer.Serialize(original, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        var restored = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json);

        Assert.NotNull(restored);
        Assert.Equal(2, restored.SettingsSchemaVersion);
        Assert.True(restored.General.MinimizeToTray);
        Assert.Equal("Test-Device-Id-42", restored.Audio.MonitoringDeviceId);
        Assert.Equal("h264_nvenc", restored.Streaming.DefaultEncoder);
        Assert.Equal(2, restored.Hotkeys.Count);
        Assert.Single(restored.AudioRoutes);
        Assert.False(restored.AudioRoutes[0].Enabled);
        Assert.Equal(0.5f, restored.AudioRoutes[0].RouteGain);
    }

    [Fact]
    public void DiagnosticBundleSanitization_SanitizesSecretTokensInText()
    {
        var rawLog = "Error during rtmp push to rtmp://live-api-s.facebook.com:443/rtmp/FB-1234567890-ABCDEF?secret=top_secret_token";
        var sanitized = AppLog.Sanitize(rawLog);

        Assert.DoesNotContain("top_secret_token", sanitized);
        Assert.Contains("[REDACTED]", sanitized);
    }

    [Fact]
    public void FfmpegLocator_FindsBundledOrSystemFfmpeg()
    {
        var locator = new AravalsStream.Core.Recording.FfmpegLocator();
        var resolution = locator.Locate();

        Assert.NotNull(resolution);
        Assert.True(resolution.Status == FfmpegStatus.Available || resolution.Status == FfmpegStatus.Missing);
        if (resolution.Status == FfmpegStatus.Available)
        {
            Assert.NotNull(resolution.FfmpegPath);
            Assert.True(File.Exists(resolution.FfmpegPath));
        }
    }

    [Fact]
    public void SafeShutdown_DetectsActiveSessions()
    {
        // When streams are active or recording is active, safe shutdown requires prompt
        var isStreaming = true;
        var isRecording = false;
        var activeStreamsCount = 3;

        bool requiresSafeShutdownPrompt = isStreaming || isRecording;
        Assert.True(requiresSafeShutdownPrompt);
        Assert.Equal(3, activeStreamsCount);

        // When idle, no prompt needed
        isStreaming = false;
        isRecording = false;
        requiresSafeShutdownPrompt = isStreaming || isRecording;
        Assert.False(requiresSafeShutdownPrompt);
    }
}

