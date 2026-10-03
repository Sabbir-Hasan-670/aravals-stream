using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using AravalsStream.Core.Models;
using AravalsStream.Core.Platforms;
using AravalsStream.Core.Recording.Models;
using AravalsStream.Core.Services;
using AravalsStream.Capture.Display;
using NAudio.Wave;

namespace AravalsStream.App.Views;

public partial class MainWindow
{
    private async Task RunPhase16AcceptanceAsync(bool phase17 = false, bool single = false, bool capture = false,
        bool constrained = false, bool phase17b = false, bool soak = false, bool cpuFallback = false, bool threeH = false, bool threeHV = false, bool threeHVR = false, bool recordingFirst = false, string? startupTopology = null)
    {
        var isolatedProfile = threeH || threeHV || threeHVR || startupTopology is not null;
        var topologyName = startupTopology?.Replace("--phase18-startup-", "", StringComparison.OrdinalIgnoreCase);
        var report = Path.Combine(Path.GetTempPath(), startupTopology is not null ? $"aravals_phase18_startup_{topologyName}.txt" : threeHVR ? "aravals_phase18_3hvr.txt" : threeHV ? "aravals_phase18_3hv.txt" : threeH ? "aravals_phase18_3h.txt" : soak ? "aravals_phase17b_soak.txt" : phase17b ? "aravals_phase17b_acceptance.txt" : cpuFallback ? "aravals_phase18_cpu_fallback.txt" : constrained ? "aravals_phase17_constrained.txt" : capture ? "aravals_phase17_capture.txt" : single ? "aravals_phase17_single.txt" : phase17 ? "aravals_phase17_acceptance.txt" : "aravals_phase16_acceptance.txt");
        var outputDir = Path.Combine(Path.GetTempPath(), startupTopology is not null ? $"AravalsPhase18Startup{topologyName}" : threeHVR ? "AravalsPhase18ThreeHVR" : threeHV ? "AravalsPhase18ThreeHV" : threeH ? "AravalsPhase18ThreeH" : soak ? "AravalsPhase17BSoak" : phase17b ? "AravalsPhase17BAcceptance" : cpuFallback ? "AravalsPhase18CpuFallback" : constrained ? "AravalsPhase17Constrained" : capture ? "AravalsPhase17Capture" : single ? "AravalsPhase17Single" : phase17 ? "AravalsPhase17Acceptance" : "AravalsPhase16Acceptance");
        Directory.CreateDirectory(outputDir);
        _frameHub.PipelineMode = cpuFallback
            ? AravalsStream.App.Composition.Pipelines.VideoPipelineMode.Cpu
            : AravalsStream.App.Composition.Pipelines.VideoPipelineMode.Auto;
        var savedAlertSettings = JsonSerializer.Deserialize<AravalsStream.Core.Alerts.AlertSettings>(
            JsonSerializer.Serialize(_loadedSettings.Alerts)) ?? new();
        var original = ViewModel.SelectedScene;
        var scene = new Scene { Name = "Phase 16 Local Acceptance" };
        var captureAdded = false;
        var usePattern = Environment.GetCommandLineArgs().Contains("--pattern", StringComparer.OrdinalIgnoreCase) ||
                         Environment.GetCommandLineArgs().Contains("--test-pattern", StringComparer.OrdinalIgnoreCase);
        var visualSource = usePattern ? null : (phase17b || soak || cpuFallback
            ? ViewModel.Scenes.SelectMany(s => s.Sources) : original?.Sources ?? [])
            .FirstOrDefault(s => s.Visible && s.Type is (SourceType.DisplayCapture or SourceType.WindowCapture or SourceType.Camera));
        if (usePattern || ((capture || constrained || phase17b || soak || cpuFallback || isolatedProfile) && visualSource is null))
        {
            scene.Sources.Add(new SceneSource
            {
                Name = "Performance Test Pattern",
                Type = SourceType.DisplayCapture,
                DisplayId = "performance_test_pattern",
                HorizontalTransform = new SourceTransform { X = 0, Y = 0, Width = 1920, Height = 1080 },
                VerticalTransform = new SourceTransform { X = 0, Y = 0, Width = 1080, Height = 1920 }
            });
            captureAdded = true;
        }
        else if ((capture || constrained || phase17b || soak || cpuFallback) && visualSource is { } visual)
        {
            var copy = JsonSerializer.Deserialize<SceneSource>(JsonSerializer.Serialize(visual));
            if (copy is not null) { scene.Sources.Add(copy); captureAdded = true; }
        }
        if (!isolatedProfile) scene.Sources.Add(new SceneSource
        {
            Name = "Alerts", Type = SourceType.Alerts,
            HorizontalTransform = new SourceTransform { X = 610, Y = 80, Width = 700, Height = 180 },
            VerticalTransform = new SourceTransform { X = 45, Y = 250, Width = 900, Height = 240 }
        });
        if (!isolatedProfile) scene.Sources.Add(new SceneSource
        {
            Name = "Chat Overlay", Type = SourceType.ChatOverlay,
            HorizontalTransform = new SourceTransform { X = 40, Y = 620, Width = 650, Height = 420 },
            VerticalTransform = new SourceTransform { X = 60, Y = 960, Width = 800, Height = 540 }
        });
        var specs = new[]
        {
            (PlatformType.YouTube, "YouTube", OutputMode.Horizontal, 19451, "yt_h"),
            (PlatformType.Twitch, "Twitch", OutputMode.Horizontal, 19452, "twitch_h"),
            (PlatformType.TikTok, "TikTok", OutputMode.Vertical, 19456, "tiktok_v")
        };
        var verticalSpec = specs[2];
        if (phase17b && !soak)
            specs = [specs[0], specs[1], (PlatformType.Kick, "Kick", OutputMode.Horizontal, 19453, "kick_h"), specs[2]];
        if (isolatedProfile) specs = [specs[0], specs[1], (PlatformType.Kick, "Kick", OutputMode.Horizontal, 19453, "kick_h")];
        if (threeHV) specs = [specs[0], specs[1], (PlatformType.Kick, "Kick", OutputMode.Horizontal, 19453, "kick_h"), verticalSpec];
        if (startupTopology == "--phase18-startup-1h") specs = [specs[0]];
        if (startupTopology == "--phase18-startup-hv") specs = [specs[0], verticalSpec];
        if (startupTopology == "--phase18-startup-3h") specs = [specs[0], specs[1], (PlatformType.Kick, "Kick", OutputMode.Horizontal, 19453, "kick_h")];
        if (single || constrained || cpuFallback) specs = specs.Take(1).ToArray();
        var groups = new List<PlatformDestinationGroup>();
        var result = new List<string>();
        if (capture || constrained || cpuFallback) result.Add("capture_source_added=" + captureAdded);
        if (cpuFallback) result.Add("video_pipeline=CPU");
        try
        {
            if ((phase17b || soak) && !captureAdded)
                throw new InvalidOperationException("Phase 17B controlled profile requires a visible saved display/window/camera source.");
            ViewModel.Scenes.Add(scene);
            ViewModel.SelectedScene = scene;
            SwitchScene();
            foreach (var (platformType, platform, mode, port, key) in specs)
            {
                var d = new Destination { Platform = platform, Name = $"{platform} local test", OutputMode = mode,
                    StreamUrl = $"rtmp://127.0.0.1:{port}/live", StreamKeyReference = Guid.NewGuid().ToString("N"),
                    VideoBitrateKbps = 2500, AudioBitrateKbps = 128,
                    FrameRate = single ? 60 : 30,
                    EncoderId = constrained ? "libx264" : "auto" };
                _secrets.Set(d.StreamKeyReference!, key);
                var g = new PlatformDestinationGroup { PlatformType = platformType, Platform = platform,
                    Name = platform + " local", Routing = mode == OutputMode.Vertical ? RoutingMode.Vertical : RoutingMode.Horizontal,
                    Enabled = true, Horizontal = mode == OutputMode.Horizontal ? d : new Destination(),
                    Vertical = mode == OutputMode.Vertical ? d : new Destination() };
                groups.Add(g); ViewModel.DestinationGroups.Add(g);
            }
            _recording.RefreshFfmpeg(_recordingSettings.CustomFfmpegPath);
            var rec = new RecordingSettings { OutputDirectory = outputDir, Mode = OutputMode.Horizontal,
                Video = new VideoEncoderSettings { EncoderId = constrained || threeHVR || startupTopology is not null ? "libx264" : "auto", Preset = "ultrafast",
                    Width = 1280, Height = 720, FrameRate = 30, BitrateKbps = 2000 } };
            if (threeHVR) result.Add("recording_encoder=libx264;diagnostic_only=true");
            var includeRecording = startupTopology is not null || (!single && (threeHVR || !threeH && !threeHV));
            if (recordingFirst && includeRecording) await _recording.StartRecordingAsync(rec);
            await Task.WhenAll(groups.SelectMany(g => g.GetActiveDestinations()).Select(StartOutputAsync));
            if (!recordingFirst && includeRecording) await _recording.StartRecordingAsync(rec);

            result.Add("outputs=" + string.Join(",", groups.SelectMany(g => g.GetActiveDestinations())
                .Select(d => d.Platform + ":" + d.Status)));
            if (_streamingOutputGroups is { } outputGroups)
            {
                var counts = outputGroups.SnapshotCounts;
                result.Add($"shared_groups={counts.Encoders};publishers={counts.Publishers};compositors={outputGroups.ActiveCompositorCount}");
            }

            result.Add("recording=" + _recording.State);
            var benchmarkWallStart = DateTimeOffset.UtcNow;
            AppLog.Write("Phase18Acceptance", $"profile={(startupTopology ?? (threeHVR ? "3HVR" : threeHV ? "3HV" : threeH ? "3H" : "other"))} stage=benchmark_start utc={benchmarkWallStart:O}");

            if (!isolatedProfile)
            {
            var alertDefinition = _loadedSettings.Alerts.Definitions.First(d => d.EventType == StreamEventType.PaidMessage);
            alertDefinition.Enabled = true;
            alertDefinition.DisplayMilliseconds = 6000;
            alertDefinition.EnterMilliseconds = 100;
            alertDefinition.ExitMilliseconds = 100;
            var tone = Path.Combine(outputDir, "alert-test.wav");
            CreateTestTone(tone);
            alertDefinition.SoundPath = tone;
            alertDefinition.RecordSound = true;
            alertDefinition.SoundPlatforms = ["YouTube", "Twitch", "TikTok"];
            _loadedSettings.Alerts.ChatOverlay.Enabled = true;

            var now = DateTimeOffset.UtcNow;
            _unifiedChat.Publish(new ChatMessage { Id = "yt-normal", Platform = "YouTube", AuthorId = "u1",
                AuthorName = "Viewer", Text = "Hello from YouTube", Timestamp = now });
            _unifiedChat.Publish(new ChatMessage { Id = "twitch-normal", Platform = "Twitch", AuthorId = "u2",
                AuthorName = "Viewer", Text = "Hello from Twitch", Timestamp = now.AddMilliseconds(50) });
            _unifiedChat.Publish(new ChatMessage { Id = "yt-paid", Platform = "YouTube", AuthorId = "u3",
                AuthorName = "Sabbir", Text = "Test Super Chat", SuperChatAmount = "$5", MessageType = ChatMessageType.SuperChat,
                Timestamp = now.AddMilliseconds(100) });
            _unifiedChat.Publish(new ChatMessage { Id = "twitch-sub", Platform = "Twitch", AuthorId = "u4",
                AuthorName = "Alex", Text = "Subscribed", MessageType = ChatMessageType.Subscription,
                Timestamp = now.AddMilliseconds(150) });
            _unifiedChat.Publish(new ChatMessage { Id = "twitch-bits", Platform = "Twitch", AuthorId = "u5",
                AuthorName = "Cheerer", Text = "Cheered", Bits = 100, MessageType = ChatMessageType.Bits,
                Timestamp = now.AddMilliseconds(200) });
            _unifiedChat.Publish(new ChatMessage { Id = "twitch-raid", Platform = "Twitch", AuthorId = "u6",
                AuthorName = "Raider", Text = "Raided", EventQuantity = 45, MessageType = ChatMessageType.Raid,
                Timestamp = now.AddMilliseconds(250) });
            }
            if (single)
            {
                for (var segment = 0; segment < 6; segment++)
                {
                    await Task.Delay(10000);
                    var metrics = _performanceMetrics.Last;
                    var pipeline = _frameHub.Diagnostics(OutputMode.Horizontal);
                    result.Add($"single_{segment}=cpu:{metrics.CpuPercent:0.0};ram:{metrics.WorkingSetMb:0};" +
                        $"composition_fps:{metrics.CompositionFps:0.0};composition_ms:{metrics.CompositionMilliseconds:0.0};" +
                        $"render_ms:{pipeline.RenderMs:0.0};convert_ms:{pipeline.ConvertMs:0.0};" +
                        $"output_fps:{metrics.OutputFps:0.0};drops:{metrics.CompositionDrops}");
                }
            }
            else if (phase17 || capture || constrained || phase17b || soak || cpuFallback || isolatedProfile)
            {
                var segments = soak ? 180 : capture || constrained || cpuFallback ? 3 : isolatedProfile ? 1 : 6;
                for (var segment = 0; segment < segments; segment++)
                {
                    await Task.Delay(10000);
                    var metrics = _performanceMetrics.Last;
                    if (!soak || segment == 0 || (segment + 1) % 60 == 0)
                        result.Add($"sample_{segment}=window:{WindowState};cpu:{metrics.CpuPercent:0.0};ram:{metrics.WorkingSetMb:0};" +
                        $"composition_fps:{metrics.CompositionFps:0.0};composition_ms:{metrics.CompositionMilliseconds:0.0};" +
                        $"preview_fps:{metrics.PreviewFps:0.0};output_fps:{metrics.OutputFps:0.0};" +
                        $"composition_drops:{metrics.CompositionDeadlineDrops};framehub_drops:{metrics.CompositionDrops};" +
                        $"capture_drops:{metrics.CaptureDrops};preview_drops:{metrics.PreviewDrops};output_drops:{metrics.OutputDrops};" +
                        $"gc:{metrics.Gen0Collections},{metrics.Gen1Collections},{metrics.Gen2Collections};" +
                        $"handles:{System.Diagnostics.Process.GetCurrentProcess().HandleCount};threads:{System.Diagnostics.Process.GetCurrentProcess().Threads.Count}");
                    if (!isolatedProfile && !constrained && !soak && segment == (capture ? 0 : 1)) WindowState = WindowState.Minimized;
                    if (!isolatedProfile && !constrained && !soak && segment == (capture ? 1 : 3)) WindowState = WindowState.Normal;
                    if (!isolatedProfile) _unifiedChat.Publish(new ChatMessage { Id = $"phase17-paid-{segment}", Platform = "YouTube",
                        AuthorId = "phase17", AuthorName = "Local test", Text = "Performance alert",
                        SuperChatAmount = "$1", MessageType = ChatMessageType.SuperChat,
                        Timestamp = DateTimeOffset.UtcNow });
                }
            }
            else await Task.Delay(9000);
            result.Add($"chat_all={ViewModel.AllChatMessages.Count};youtube={ViewModel.YouTubeChatMessages.Count};twitch={ViewModel.TwitchChatMessages.Count}");
            result.Add($"activity={_activity.Count};alert_queue={_alertEngine?.QueuedCount}");
            if (phase17 || capture || constrained || phase17b || soak || single || cpuFallback)
                foreach (var stage in _performanceMetrics.StageTimings().Where(s => s.Samples > 0))
                    result.Add($"stage_{stage.Stage}=samples:{stage.Samples};avg_ms:{stage.AverageMilliseconds:0.000};max_ms:{stage.MaximumMilliseconds:0.000}");
            var benchmarkWallEnd = DateTimeOffset.UtcNow;
            var benchmarkWallSeconds = (benchmarkWallEnd - benchmarkWallStart).TotalSeconds;
            result.Add($"benchmark_wall_seconds={benchmarkWallSeconds:0.000}");

            var activeOutputs = groups.SelectMany(g => g.GetActiveDestinations())
                .Select(d => (Destination: d, Output: _outputs.TryGetValue(d.Id, out var o) ? o : null))
                .Where(x => x.Output is not null).ToList();

            // Concurrently stop all streaming outputs and recording so media timelines match benchmark window
            var stopTasks = activeOutputs.Select(x => StopOutputAsync(x.Destination.Id)).ToList();
            if (includeRecording) stopTasks.Add(_recording.StopRecordingAsync());
            AppLog.Write("Phase18Acceptance", $"profile={(startupTopology ?? (threeHVR ? "3HVR" : threeHV ? "3HV" : threeH ? "3H" : "other"))} stage=benchmark_stop utc={DateTimeOffset.UtcNow:O}");
            await Task.WhenAll(stopTasks);

            result.Add("recording_stopped=" + _recording.State);
            if (!single)
            {
                var recordingTelemetry = _recording.Telemetry;
                result.Add($"recording_dir={outputDir}");
                result.Add($"recording_wall_seconds={recordingTelemetry.PublishWallDurationSeconds:0.000}");
                result.Add($"recording_frames={recordingTelemetry.FramesEncoded};dropped={recordingTelemetry.FramesDropped}");
                result.Add($"recording_schedule={recordingTelemetry.ScheduledFrames};unique={recordingTelemetry.UniqueFrames};" +
                    $"repeated={recordingTelemetry.RepeatedFrames};scheduler_drops={recordingTelemetry.RecordingSchedulerDrops};" +
                    $"pipe_backpressure_drops={recordingTelemetry.EncoderBackpressureDrops};" +
                    $"encoder_drops={recordingTelemetry.EncoderDrops};pipe_avg_ms={recordingTelemetry.AverageVideoPipeWriteMs:0.00};" +
                    $"pipe_max_ms={recordingTelemetry.MaximumVideoPipeWriteMs:0.00};" +
                    $"pipe_deadline_misses={recordingTelemetry.VideoPipeDeadlineMisses};" +
                    $"realtime_ratio:{recordingTelemetry.RealtimeRatio:0.0000};cadence_state:{recordingTelemetry.CadenceState};" +
                    $"media_duration_s:{recordingTelemetry.MediaDuration.TotalSeconds:0.000};" +
                    $"publish_wall_s:{recordingTelemetry.PublishWallDurationSeconds:0.000};" +
                    $"post_stop_frames:{recordingTelemetry.PostStopFrames}");
            }

            foreach (var (destination, output) in activeOutputs)
            {
                if (isolatedProfile && output?.PublisherDiagnostics is { } publisher)
                    result.Add($"publisher_{destination.Platform}=received:{publisher.ReceivedPackets};enqueued:{publisher.EnqueuedPackets};" +
                        $"dequeued:{publisher.DequeuedPackets};discarded:{publisher.DiscardedPackets};bytes_enqueued:{publisher.BytesEnqueued};bytes_written:{publisher.BytesWritten};" +
                        $"queue_packets:{publisher.QueuePackets};queue_bytes:{publisher.QueueBytes};" +
                        $"max_queue_packets:{publisher.MaxQueuePackets};max_queue_bytes:{publisher.MaxQueueBytes};" +
                        $"max_packet_age_ms:{publisher.MaxPacketAgeMs:0.00};write_avg_ms:{publisher.AverageWriteMs:0.00};" +
                        $"write_p95_ms:{publisher.P95WriteMs:0.00};write_p99_ms:{publisher.P99WriteMs:0.00};" +
                        $"write_count:{publisher.WriteCount};reconnects:{publisher.Reconnects};last_error:{publisher.LastError}");
                if (output?.Telemetry is not { } telemetry) continue;
                result.Add($"output_{destination.Platform}_{destination.OutputMode}=scheduled:{telemetry.ScheduledFrames};" +
                    $"unique:{telemetry.UniqueFrames};repeated:{telemetry.RepeatedFrames};" +
                    $"scheduler_drops:{telemetry.RecordingSchedulerDrops};pipe_backpressure_drops:{telemetry.EncoderBackpressureDrops};" +
                    $"encoder_drops:{telemetry.EncoderDrops};" +
                    $"pipe_avg_ms:{telemetry.AverageVideoPipeWriteMs:0.00};pipe_max_ms:{telemetry.MaximumVideoPipeWriteMs:0.00};" +
                    $"pipe_deadline_misses:{telemetry.VideoPipeDeadlineMisses};" +
                    $"realtime_ratio:{telemetry.RealtimeRatio:0.0000};cadence_state:{telemetry.CadenceState};" +
                    $"media_duration_s:{telemetry.MediaDuration.TotalSeconds:0.000};" +
                    $"publish_wall_s:{telemetry.PublishWallDurationSeconds:0.000};" +
                    $"first_submission_utc:{telemetry.FirstMediaSubmissionUtc:O};" +
                    $"last_submission_utc:{telemetry.LastMediaSubmissionUtc:O};" +
                    $"stop_requested_utc:{telemetry.StopRequestedUtc:O};" +
                    $"pipes_closed_utc:{telemetry.PipesClosedUtc:O};" +
                    $"post_stop_frames:{telemetry.PostStopFrames}");
            }

            // Exercise the installed WPF controls as well as the media pipeline.
            var picker = new SourcePicker { Owner = this };
            picker.Show();
            await Task.Delay(200);
            picker.TypeList.SelectedItem = picker.TypeList.Items.Cast<SourcePicker.SourceChoice>()
                .First(x => x.Type == SourceType.Alerts);
            await Task.Delay(100);
            var alertsAvailable = picker.AddButton.IsEnabled;
            picker.TypeList.SelectedItem = picker.TypeList.Items.Cast<SourcePicker.SourceChoice>()
                .First(x => x.Type == SourceType.ChatOverlay);
            await Task.Delay(100);
            var chatOverlayAvailable = picker.AddButton.IsEnabled;
            picker.Close();
            result.Add($"source_picker_alerts={alertsAvailable};chat_overlay={chatOverlayAvailable}");

            var editor = new AlertSettingsWindow(_loadedSettings.Alerts, item => _alertEngine?.Enqueue(item))
            { Owner = this };
            editor.Show();
            await Task.Delay(200);
            var testButton = FindTestButton(editor);
            if (testButton == null) throw new InvalidOperationException("Test Alert button missing in installed editor.");
            testButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            editor.Close();
            result.Add("alert_settings_editor=test_button_clicked");
            if (phase17 || capture || single || constrained || phase17b || soak || cpuFallback)
            {
                var performanceSettings = new SettingsWindow(this, _loadedSettings, _recording.FfmpegStatus,
                    _recording.AvailableEncoders, _secrets, _performanceMetrics, PerformanceOutputDetails);
                performanceSettings.Show();
                await Task.Delay(150);
                var categories = FindVisual<ListBox>(performanceSettings)
                    .FirstOrDefault(box => box.Items.Contains("Advanced"));
                if (categories is null) throw new InvalidOperationException("Performance settings category missing.");
                categories.SelectedItem = "Advanced";
                await Task.Delay(150);
                var modes = FindVisual<ComboBox>(performanceSettings)
                    .FirstOrDefault(box => box.ItemsSource is IEnumerable<AravalsStream.Core.Settings.PerformanceMode>);
                result.Add("performance_modes=" + (modes?.Items.Count ?? 0));
                if (phase17b || soak)
                    result.Add("gpu_memory=" + string.Join(";", GpuMemoryProbe.Sample().Select(g =>
                        $"{g.Adapter}:{g.UsedBytes / 1048576.0:0}/{g.BudgetBytes / 1048576.0:0}MB")));
                performanceSettings.Close();
            }
        }
        catch (Exception ex)
        {
            result.Add("ERROR=" + ex);
            AppLog.Write("Phase16Acceptance", ex.ToString());
        }
        finally
        {
            if (_recording.State != RecordingState.Idle)
                try { await _recording.StopRecordingAsync(); } catch { }
            foreach (var group in groups)
            {
                foreach (var destination in group.GetActiveDestinations())
                {
                    try { await StopOutputAsync(destination.Id); } catch { }
                    if (destination.StreamKeyReference != null) _secrets.Delete(destination.StreamKeyReference);
                }
                ViewModel.DestinationGroups.Remove(group);
            }
            ViewModel.SelectedScene = original;
            ViewModel.Scenes.Remove(scene);
            _loadedSettings.Alerts = savedAlertSettings;
            await File.WriteAllLinesAsync(report, result);
            _forceExit = true;
            Close();
        }
    }

    private static Button? FindTestButton(DependencyObject root)
    {
        if (root is Button button && button.Content?.ToString() == "TEST ALERT") return button;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindTestButton(VisualTreeHelper.GetChild(root, i)) is { } found) return found;
        return null;
    }

    private static IEnumerable<T> FindVisual<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) yield return match;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in FindVisual<T>(VisualTreeHelper.GetChild(root, i))) yield return child;
    }

    private static void CreateTestTone(string path)
    {
        using var writer = new WaveFileWriter(path, new WaveFormat(48000, 16, 2));
        for (int i = 0; i < 48000; i++)
        {
            short sample = (short)(Math.Sin(2 * Math.PI * 880 * i / 48000) * 9000);
            writer.WriteByte((byte)sample); writer.WriteByte((byte)(sample >> 8));
            writer.WriteByte((byte)sample); writer.WriteByte((byte)(sample >> 8));
        }
    }
}
