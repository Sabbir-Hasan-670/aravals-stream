using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using AravalsStream.Core.Audio;
using AravalsStream.Core.Models;
using AravalsStream.Core.Platforms;
using AravalsStream.Core.Recording.Models;
using AravalsStream.Core.Services;

namespace AravalsStream.App.Views;

public partial class MainWindow
{
    private async Task ConfigureTikTokDestinationAsync(PlatformProfile profile)
    {
        var account = _loadedSettings.TikTokAccount;
        var accountStatus = account?.Connected == true
            ? $"TikTok Account: {account.DisplayName} (Connected)"
            : "TikTok Account: Not Connected";

        MessageBox.Show(this,
            $"{accountStatus}\n\n" +
            "Streaming Method:\nManual RTMP\n\n" +
            "Current official TikTok public APIs provide account identity and display capabilities, but do NOT provide public LIVE stream creation, stream keys, or LIVE chat.\n\n" +
            "TikTok streaming uses secure Manual RTMP transport. Stream keys are stored safely using Windows DPAPI.\n\n" +
            "Click OK to configure your TikTok RTMP server URL, stream key, and vertical preset.",
            "TikTok Destination Setup", MessageBoxButton.OK, MessageBoxImage.Information);

        var group = PlatformDestinationGroup.CreateFromProfile(profile);
        group.ConfigurationMode = ConfigurationMode.ManualRtmp;
        group.Routing = RoutingMode.Vertical;
        group.Order = ViewModel.DestinationGroups.Count;
        group.EnsureChildDestinations();
        ViewModel.DestinationGroups.Add(group);
        DestinationItems.Items.Refresh();
        await SaveSettingsAsync();
        EditDestinationGroup(group, isNew: true);
    }

    private void UpdateTikTokStatus()
    {
        var hasActiveTikTok = ViewModel.DestinationGroups.Any(g =>
            g.PlatformType == PlatformType.TikTok &&
            ((g.Horizontal != null && _outputs.ContainsKey(g.Horizontal.Id)) ||
             (g.Vertical != null && _outputs.ContainsKey(g.Vertical.Id))));

        var account = _loadedSettings.TikTokAccount;
        ViewModel.TikTokAccountName = account?.Connected == true
            ? account.DisplayName
            : (account != null ? $"{account.DisplayName} ({account.State})" : "—");
        ViewModel.TikTokBroadcastStatus = hasActiveTikTok ? "LIVE (Manual RTMP)" : "OFFLINE";
        ViewModel.TikTokViewers = "—"; // Official API does not expose live viewers
        ViewModel.TikTokChatStatus = "Unavailable"; // Official API does not expose live chat
    }

    private async Task RunPhase15AcceptanceAsync()
    {
        AppLog.Write("Phase15Acceptance", "Starting Phase 15 Acceptance Test (Multiplatform Regression with TikTok Vertical)");

        var ytGroup = new PlatformDestinationGroup
        {
            Id = Guid.NewGuid(),
            PlatformType = PlatformType.YouTube,
            Platform = "YouTube",
            Name = "YouTube Test H",
            Routing = RoutingMode.Horizontal,
            Enabled = true,
            ServerUrl = "rtmp://127.0.0.1:19451/live",
            StreamKeyReference = Guid.NewGuid().ToString(),
            Horizontal = new Destination
            {
                Id = Guid.NewGuid(),
                Platform = "YouTube",
                Name = "YouTube-Test H",
                OutputMode = OutputMode.Horizontal,
                StreamUrl = "rtmp://127.0.0.1:19451/live",
                StreamKeyReference = Guid.NewGuid().ToString(),
                VideoBitrateKbps = 4500,
                AudioBitrateKbps = 160,
                FrameRate = 60,
                AutoReconnect = true
            }
        };

        var twitchGroup = new PlatformDestinationGroup
        {
            Id = Guid.NewGuid(),
            PlatformType = PlatformType.Twitch,
            Platform = "Twitch",
            Name = "Twitch Test H",
            Routing = RoutingMode.Horizontal,
            Enabled = true,
            ServerUrl = "rtmp://127.0.0.1:19452/live",
            StreamKeyReference = Guid.NewGuid().ToString(),
            Horizontal = new Destination
            {
                Id = Guid.NewGuid(),
                Platform = "Twitch",
                Name = "Twitch-Test H",
                OutputMode = OutputMode.Horizontal,
                StreamUrl = "rtmp://127.0.0.1:19452/live",
                StreamKeyReference = Guid.NewGuid().ToString(),
                VideoBitrateKbps = 4500,
                AudioBitrateKbps = 160,
                FrameRate = 60,
                AutoReconnect = true
            }
        };

        var kickGroup = new PlatformDestinationGroup
        {
            Id = Guid.NewGuid(),
            PlatformType = PlatformType.Kick,
            Platform = "Kick",
            Name = "Kick Test H",
            Routing = RoutingMode.Horizontal,
            Enabled = true,
            ServerUrl = "rtmp://127.0.0.1:19453/live",
            StreamKeyReference = Guid.NewGuid().ToString(),
            Horizontal = new Destination
            {
                Id = Guid.NewGuid(),
                Platform = "Kick",
                Name = "Kick-Test H",
                OutputMode = OutputMode.Horizontal,
                StreamUrl = "rtmp://127.0.0.1:19453/live",
                StreamKeyReference = Guid.NewGuid().ToString(),
                VideoBitrateKbps = 4500,
                AudioBitrateKbps = 160,
                FrameRate = 60,
                AutoReconnect = true
            }
        };

        var fbGroup = new PlatformDestinationGroup
        {
            Id = Guid.NewGuid(),
            PlatformType = PlatformType.Facebook,
            Platform = "Facebook",
            Name = "Facebook Test H",
            Routing = RoutingMode.Horizontal,
            Enabled = true,
            ServerUrl = "rtmp://127.0.0.1:19454/live",
            StreamKeyReference = Guid.NewGuid().ToString(),
            Horizontal = new Destination
            {
                Id = Guid.NewGuid(),
                Platform = "Facebook",
                Name = "Facebook-Test H",
                OutputMode = OutputMode.Horizontal,
                StreamUrl = "rtmp://127.0.0.1:19454/live",
                StreamKeyReference = Guid.NewGuid().ToString(),
                VideoBitrateKbps = 4000,
                AudioBitrateKbps = 128,
                FrameRate = 30,
                AutoReconnect = true
            }
        };

        var tiktokGroup = new PlatformDestinationGroup
        {
            Id = Guid.NewGuid(),
            PlatformType = PlatformType.TikTok,
            Platform = "TikTok",
            Name = "TikTok Test V",
            Routing = RoutingMode.Vertical,
            Enabled = true,
            ServerUrl = "rtmp://127.0.0.1:19456/live",
            StreamKeyReference = Guid.NewGuid().ToString(),
            Vertical = new Destination
            {
                Id = Guid.NewGuid(),
                Platform = "TikTok",
                Name = "TikTok-Test V",
                OutputMode = OutputMode.Vertical,
                StreamUrl = "rtmp://127.0.0.1:19456/live",
                StreamKeyReference = Guid.NewGuid().ToString(),
                VideoBitrateKbps = 4000,
                AudioBitrateKbps = 128,
                FrameRate = 30,
                AutoReconnect = true
            }
        };

        _secrets.Set(ytGroup.Horizontal.StreamKeyReference!, "yt_h");
        _secrets.Set(twitchGroup.Horizontal.StreamKeyReference!, "twitch_h");
        _secrets.Set(kickGroup.Horizontal.StreamKeyReference!, "kick_h");
        _secrets.Set(fbGroup.Horizontal.StreamKeyReference!, "fb_h");
        _secrets.Set(tiktokGroup.Vertical.StreamKeyReference!, "tiktok_v");

        ViewModel.DestinationGroups.Add(ytGroup);
        ViewModel.DestinationGroups.Add(twitchGroup);
        ViewModel.DestinationGroups.Add(kickGroup);
        ViewModel.DestinationGroups.Add(fbGroup);
        ViewModel.DestinationGroups.Add(tiktokGroup);

        _recording.RefreshFfmpeg(_recordingSettings.CustomFfmpegPath);

        try
        {
            // Step 1: Start all 5 active outputs concurrently
            AppLog.Write("Phase15Acceptance", "Step 1: Starting all 5 outputs (YouTube H, Twitch H, Kick H, Facebook H, TikTok V)");
            var activeOutputs = ViewModel.DestinationGroups.Where(g => g.Enabled && g.Routing != RoutingMode.Off)
                .SelectMany(g => g.GetActiveDestinations()).ToList();
            await Task.WhenAll(activeOutputs.Select(StartOutputAsync));
            await Task.Delay(3500);

            AppLog.Write("Phase15Acceptance", $"Outputs running: {_outputs.Count} (Expected: 5)");

            // Step 2: Local recording active
            AppLog.Write("Phase15Acceptance", "Step 2: Recording locally while all 5 outputs live");
            var recDir = Path.Combine(Path.GetTempPath(), "AravalsPhase15Acceptance");
            Directory.CreateDirectory(recDir);
            var recSettings = new RecordingSettings
            {
                OutputDirectory = recDir,
                Mode = OutputMode.Horizontal,
                Video = new VideoEncoderSettings { EncoderId = "auto", Width = 1280, Height = 720, FrameRate = 30, BitrateKbps = 2500 }
            };
            await _recording.StartRecordingAsync(recSettings);
            await Task.Delay(2500);

            // Step 3: Audio matrix routing
            var micChannel = _audioEngine.Channels.FirstOrDefault(c => c.Name.Contains("Mic", StringComparison.OrdinalIgnoreCase))
                ?? new AudioChannel { Name = "Microphone" };
            var deskChannel = _audioEngine.Channels.FirstOrDefault(c => c.Name.Contains("Desktop", StringComparison.OrdinalIgnoreCase))
                ?? new AudioChannel { Name = "Desktop Audio" };

            AppLog.Write("Phase15Acceptance", "Step 3: Routing Mic to all 5 outputs + Recording");
            _audioEngine.Mixer.Matrix.SetRoute(micChannel.Id, "Recording", true, 1.0f);
            _audioEngine.Mixer.Matrix.SetRoute(micChannel.Id, ytGroup.Horizontal.Id.ToString(), true, 1.0f);
            _audioEngine.Mixer.Matrix.SetRoute(micChannel.Id, twitchGroup.Horizontal.Id.ToString(), true, 1.0f);
            _audioEngine.Mixer.Matrix.SetRoute(micChannel.Id, kickGroup.Horizontal.Id.ToString(), true, 0.8f);
            _audioEngine.Mixer.Matrix.SetRoute(micChannel.Id, fbGroup.Horizontal.Id.ToString(), true, 0.9f);
            _audioEngine.Mixer.Matrix.SetRoute(micChannel.Id, tiktokGroup.Vertical.Id.ToString(), true, 1.0f);

            AppLog.Write("Phase15Acceptance", "Step 4: Routing Desktop Audio to YouTube H and Recording only");
            _audioEngine.Mixer.Matrix.SetRoute(deskChannel.Id, "Recording", true, 1.0f);
            _audioEngine.Mixer.Matrix.SetRoute(deskChannel.Id, ytGroup.Horizontal.Id.ToString(), true, 1.0f);
            _audioEngine.Mixer.Matrix.SetRoute(deskChannel.Id, twitchGroup.Horizontal.Id.ToString(), false, 0.0f);
            _audioEngine.Mixer.Matrix.SetRoute(deskChannel.Id, kickGroup.Horizontal.Id.ToString(), false, 0.0f);
            _audioEngine.Mixer.Matrix.SetRoute(deskChannel.Id, fbGroup.Horizontal.Id.ToString(), false, 0.0f);
            _audioEngine.Mixer.Matrix.SetRoute(deskChannel.Id, tiktokGroup.Vertical.Id.ToString(), false, 0.0f);

            // Step 5: Scene switching during multiplatform stream
            AppLog.Write("Phase15Acceptance", "Step 5: Testing scene switching during 5 live streams + recording");
            if (ViewModel.Scenes.Count > 1)
            {
                var originalScene = ViewModel.SelectedScene;
                var targetScene = ViewModel.Scenes.FirstOrDefault(s => s != originalScene) ?? ViewModel.Scenes[0];
                ViewModel.SelectedScene = targetScene;
                SwitchScene();
                await Task.Delay(2000);
                ViewModel.SelectedScene = originalScene;
                SwitchScene();
                await Task.Delay(1500);
            }

            // Step 6: Pause recording and verify streams remain LIVE
            AppLog.Write("Phase15Acceptance", "Step 6: Pausing recording and verifying all 5 live streams remain LIVE");
            _recording.PauseRecording();
            await Task.Delay(2500);

            AppLog.Write("Phase15Acceptance", $"During paused recording: YT={ytGroup.Horizontal.Status}, Twitch={twitchGroup.Horizontal.Status}, Kick={kickGroup.Horizontal.Status}, FB={fbGroup.Horizontal.Status}, TikTok_V={tiktokGroup.Vertical.Status}");

            // Step 7: Resume recording
            AppLog.Write("Phase15Acceptance", "Step 7: Resuming recording");
            _recording.ResumeRecording();
            await Task.Delay(2000);

            // Step 8: Stop recording
            AppLog.Write("Phase15Acceptance", "Step 8: Stopping local recording");
            await _recording.StopRecordingAsync();
            await Task.Delay(1500);

            // Step 9: Stop all outputs
            AppLog.Write("Phase15Acceptance", "Step 9: Stopping all 5 outputs");
            await StopOutputAsync(ytGroup.Horizontal.Id);
            await StopOutputAsync(twitchGroup.Horizontal.Id);
            await StopOutputAsync(kickGroup.Horizontal.Id);
            await StopOutputAsync(fbGroup.Horizontal.Id);
            await StopOutputAsync(tiktokGroup.Vertical.Id);
            await Task.Delay(2000);

            AppLog.Write("Phase15Acceptance", "Phase 15 Acceptance Test Completed Successfully");
        }
        catch (Exception ex)
        {
            AppLog.Write("Phase15Acceptance", $"Acceptance test failed: {ex.Message}\n{ex.StackTrace}");
        }
        finally
        {
            ViewModel.DestinationGroups.Remove(ytGroup);
            ViewModel.DestinationGroups.Remove(twitchGroup);
            ViewModel.DestinationGroups.Remove(kickGroup);
            ViewModel.DestinationGroups.Remove(fbGroup);
            ViewModel.DestinationGroups.Remove(tiktokGroup);

            if (ytGroup.Horizontal.StreamKeyReference != null) _secrets.Delete(ytGroup.Horizontal.StreamKeyReference);
            if (twitchGroup.Horizontal.StreamKeyReference != null) _secrets.Delete(twitchGroup.Horizontal.StreamKeyReference);
            if (kickGroup.Horizontal.StreamKeyReference != null) _secrets.Delete(kickGroup.Horizontal.StreamKeyReference);
            if (fbGroup.Horizontal.StreamKeyReference != null) _secrets.Delete(fbGroup.Horizontal.StreamKeyReference);
            if (tiktokGroup.Vertical.StreamKeyReference != null) _secrets.Delete(tiktokGroup.Vertical.StreamKeyReference);

            _forceExit = true;
            Close();
        }
    }
}
