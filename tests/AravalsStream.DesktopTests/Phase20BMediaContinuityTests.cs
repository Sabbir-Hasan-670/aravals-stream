using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using AravalsStream.App.Audio;
using AravalsStream.App.Composition;
using AravalsStream.App.Recording;
using AravalsStream.App.RemoteCapture;
using AravalsStream.App.Streaming;
using AravalsStream.App.Views;
using AravalsStream.Core.Alerts;
using AravalsStream.Core.Chat;
using AravalsStream.Core.Models;
using AravalsStream.Core.Recording;
using AravalsStream.Core.Recording.Models;
using AravalsStream.Core.RemoteCapture;
using AravalsStream.Core.Services;
using AravalsStream.Core.Settings;
using Xunit;
using Xunit.Abstractions;

namespace AravalsStream.DesktopTests;

public sealed class Phase20BMediaContinuityTests(ITestOutputHelper output)
{
    [Fact]
    public async Task DesktopStartsOfflineAndMediaSurvivesRelayFailureAndRecovery()
    {
        // The test host concurrently owns the GUI, sender, receiver and Relay; avoid host-only worker starvation.
        ThreadPool.GetMinThreads(out var minWorkers, out var minIo); ThreadPool.SetMinThreads(Math.Max(32, minWorkers), minIo);
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AravalsStream.sln"))) directory = directory.Parent;
        Assert.NotNull(directory); var repo = directory!.FullName;
        var ffmpeg = Path.Combine(repo, "ffmpeg", "bin", "ffmpeg.exe");
        var ffprobe = Path.Combine(repo, "ffmpeg", "bin", "ffprobe.exe"); Assert.True(File.Exists(ffmpeg)); Assert.True(File.Exists(ffprobe));
        var root = Path.Combine(repo, "artifacts", AravalsStream.Core.Versioning.AppVersion.Version, "acceptance", "media-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")); Directory.CreateDirectory(root);
        using var rsa = RSA.Create(2048); var signing = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var relayUrl = "http://127.0.0.1:" + FreePort(); var rtmpUrl = "rtmp://127.0.0.1:" + FreePort() + "/live";
        var relayState = Path.Combine(root, "relay-state.json"); var relayDll = Path.Combine(AppContext.BaseDirectory, "AravalsStream.Relay.dll");
        var enrollCode = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var srtSecret = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        Process? relay = null, sender = null, receiver = null; StreamingOutput? stream = null; MainWindow? window = null;
        Task? windowThread = null; IRecordingService? recording = null;
        var states = new ConcurrentQueue<RelayConnectionState>();
        var streamFile = Path.Combine(root, "local-stream.mkv");
        Process StartRelay()
        {
            var info = Info("dotnet", [relayDll]); info.WorkingDirectory = AppContext.BaseDirectory;
            info.Environment["ASPNETCORE_ENVIRONMENT"] = "Development"; info.Environment["ASPNETCORE_URLS"] = relayUrl;
            info.Environment["RELAY_TOKEN_SIGNING_KEY"] = signing; info.Environment["RELAY_STORAGE_PATH"] = relayState;
            info.Environment["RELAY_ENROLLMENT_CREDENTIALS_JSON"] = JsonSerializer.Serialize(new[] { new { code = enrollCode, channels = new[] { "kick:101" } } });
            info.Environment["KICK_WEBHOOK_PUBLIC_KEY_PEM"] = rsa.ExportSubjectPublicKeyInfoPem();
            info.Environment["KICK_WEBHOOK_PUBLIC_KEY_FILE"] = ""; info.Environment["KICK_SUBSCRIPTIONS_ENABLED"] = "false";
            info.Environment["FACEBOOK_WEBHOOKS_ENABLED"] = "false";
            return Start(info);
        }
        async Task Healthy()
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(400) };
            await Wait(async () => { try { return (await http.GetAsync(relayUrl + "/health")).IsSuccessStatusCode; } catch { return false; } }, 15);
        }
        try
        {
            relay = StartRelay(); await Healthy();
            using var http = new HttpClient(); using var enrolledResponse = await http.PostAsJsonAsync(relayUrl + "/api/v1/enroll", new { code = enrollCode, channels = new[] { "kick:101" } });
            enrolledResponse.EnsureSuccessStatusCode(); using var enrolled = JsonDocument.Parse(await enrolledResponse.Content.ReadAsStringAsync());
            var id = enrolled.RootElement.GetProperty("installationId").GetString()!; var refresh = enrolled.RootElement.GetProperty("refreshToken").GetString()!;
            await Kill(relay); relay = null;
            // Encrypted LAN SRT fixture uses the production Remote PC receiver, compositor and audio mixer.
            using (var availability = new UdpClient(new IPEndPoint(IPAddress.Loopback, RemoteCaptureProtocol.DefaultTransportPort))) { }
            sender = Start(Info(ffmpeg, ["-hide_banner", "-loglevel", "error", "-re", "-f", "lavfi", "-i", "testsrc2=size=640x360:rate=15",
                "-re", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000", "-c:v", "libx264", "-preset", "ultrafast", "-tune", "zerolatency", "-g", "15",
                "-pix_fmt", "yuv420p", "-c:a", "aac", "-ar", "48000", "-ac", "2", "-f", "mpegts",
                $"srt://127.0.0.1:{RemoteCaptureProtocol.DefaultTransportPort}?mode=listener&latency=250&passphrase={srtSecret}&pbkeylen=16"]));
            await Task.Delay(600);
            var secrets = new DpapiSecretStorage(Path.Combine(root, "client", "secrets")); secrets.Set("relay-refresh", refresh);
            var registry = new PairedDeviceRegistry(secrets, Path.Combine(root, "client", "paired.json")); var deviceId = Guid.NewGuid();
            await registry.SaveAsync(new PairedRemoteDevice(deviceId, "Local acceptance sender", "127.0.0.1", 45999, 1,
                AravalsStream.Core.Versioning.AppVersion.Version, "test-only", "srt-test", DateTimeOffset.UtcNow), srtSecret);
            var resource = new CaptureResource { Type = SourceType.RemotePc, RemoteDeviceId = deviceId.ToString(), RemoteHost = "127.0.0.1",
                FormatWidth = 640, FormatHeight = 360, FormatFrameRate = 15, RemoteLatencyMs = 250, Name = "Local encrypted SRT fixture" };
            var scene = new Scene { Name = "Relay outage acceptance" }; var source = new SceneSource { Type = SourceType.RemotePc, SourceReference = resource.Id, Name = resource.Name };
            CanvasLayout.Fit(source.HorizontalTransform, 640, 360, OutputMode.Horizontal); scene.Sources.Add(source);
            var settings = new AppSettings { FirstRunCompleted = true }; settings.Scenes = [scene]; settings.CaptureResources = [resource];
            settings.General.ConfirmExitWhileLive = false; settings.Hotkeys = []; settings.Relay = new RelaySettings { Enabled = true, RelayUrl = relayUrl,
                InstallationId = id, RefreshTokenReference = "relay-refresh", Channels = ["kick:101"] };
            settings.Recording.CustomFfmpegPath = ffmpeg;
            var settingsStore = new JsonSettingsService(Path.Combine(root, "client", "settings.json")); await settingsStore.SaveAsync(settings);
            var ready = new TaskCompletionSource<MainWindow>(TaskCreationOptions.RunContinuationsAsynchronously);
            var threadDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); windowThread = threadDone.Task;
            var thread = new Thread(() =>
            {
                try
                {
                    var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                    foreach (var theme in new[] { "Colors", "Typography", "Buttons", "ToggleSwitch", "Controls", "Cards" })
                        application.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/AravalsStream.App;component/Themes/" + theme + ".xaml") });
                    application.Resources["BrandLogo"] = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/AravalsStream.App;component/Assets/Brand/Aravals%20Stream.png"));
                    var main = new MainWindow(settingsStore, secrets, registry, new SessionRecoveryService(Path.Combine(root, "client", "session.active")))
                    { ShowInTaskbar = false, Left = -10000, Top = -10000 };
                    main.Show(); ready.TrySetResult(main); Dispatcher.Run(); threadDone.TrySetResult();
                }
                catch (Exception error) { ready.TrySetException(error); threadDone.TrySetException(error); }
            }); thread.SetApartmentState(ApartmentState.STA); thread.IsBackground = true; thread.Start();
            window = await ready.Task.WaitAsync(TimeSpan.FromSeconds(45));
            var desktop = Field<WebhookRelayClient>(window, "_relayClient"); desktop.StateChanged += states.Enqueue;
            await Wait(() => Task.FromResult(desktop.State == RelayConnectionState.Reconnecting), 30);
            var factory = Field<RemoteCaptureSessionFactory>(window, "_remoteCapture");
            await Wait(() => Task.FromResult(factory.Get(resource.Id) is not null), 20);
            var remote = factory.Get(resource.Id)!; await Wait(() => Task.FromResult(Counter(remote, "_videoFrames") > 5 && Counter(remote, "_audioBlocks") > 5), 25);
            output.WriteLine("Desktop startup while Relay offline: PASS; production Remote PC encrypted SRT video/audio flowing.");
            var compositor = Field<ISceneCompositor>(window, "_compositor"); var hub = Field<ComposedFrameHub>(window, "_frameHub"); var audio = Field<SceneAudioEngine>(window, "_audioEngine");
            recording = Field<IRecordingService>(window, "_recording");
            var encoder = recording.AvailableEncoders.FirstOrDefault(e => e.Id == "h264_nvenc" && e.Available)?.Id ?? "libx264";
            receiver = Start(Info(ffmpeg, ["-y", "-hide_banner", "-loglevel", "error", "-analyzeduration", "0", "-probesize", "32768", "-listen", "1", "-i", rtmpUrl + "/acceptance", "-map", "0", "-c", "copy", streamFile]));
            await Task.Delay(500);
            var destination = new Destination { Name = "Local custom RTMP", Platform = "Custom RTMP", StreamUrl = rtmpUrl, FrameRate = 15,
                VideoBitrateKbps = 2000, AudioBitrateKbps = 128, AutoReconnect = false, OutputMode = OutputMode.Horizontal };
            stream = new StreamingOutput(destination, ffmpeg, encoder, "acceptance", compositor, audio.Mixer, hub); await stream.StartAsync();
            await window.Dispatcher.InvokeAsync(() => { Field<Dictionary<Guid, StreamingOutput>>(window, "_outputs")[destination.Id] = stream; FieldInfoFor(window, "_streaming").SetValue(window, true); });
            await recording.StartRecordingAsync(new RecordingSettings { CustomFfmpegPath = ffmpeg, OutputDirectory = Path.Combine(root, "recordings"), Container = "mkv", Mode = OutputMode.Horizontal,
                Video = new VideoEncoderSettings { EncoderId = encoder, Width = 640, Height = 360, FrameRate = 15, BitrateKbps = 1000, Preset = "ultrafast" } });
            var media = Field<FfmpegMediaSession>(stream, "_session"); var recordSession = ((IEnumerable)Field<object>(recording, "_activeSessions")).Cast<FfmpegMediaSession>().Single();
            var mediaPid = Field<Process>(media, "_ffmpegProcess").Id; var recordPid = Field<Process>(recordSession, "_ffmpegProcess").Id;
            var windows = new List<(string Name, double Start, double End, long Video, long Audio)>();
            async Task Sample(string name, int seconds = 4)
            {
                var first = media.Telemetry; var chunks = Counter(media, "_audioChunksWritten"); var recordFrames = recordSession.Telemetry.FramesEncoded;
                await Task.Delay(TimeSpan.FromSeconds(seconds)); var last = media.Telemetry;
                Assert.Equal(DestinationStatus.Live, stream.Status); Assert.Equal(0, stream.ReconnectCount); Assert.Equal(mediaPid, Field<Process>(media, "_ffmpegProcess").Id);
                Assert.Equal(recordPid, Field<Process>(recordSession, "_ffmpegProcess").Id); Assert.True(recordSession.Telemetry.FramesEncoded > recordFrames);
                var video = last.FramesEncoded - first.FramesEncoded; var audioChunks = Counter(media, "_audioChunksWritten") - chunks;
                output.WriteLine($"{name} raw counters: video={video}, audio={audioChunks}, source={Counter(remote, "_videoFrames")}, media duration={last.MediaDuration.TotalSeconds:0.00}s.");
                Assert.True(video > 5, name + " video stopped"); Assert.True(audioChunks > 10, name + " audio stopped");
                windows.Add((name, first.MediaDuration.TotalSeconds, last.MediaDuration.TotalSeconds, video, audioChunks));
                output.WriteLine($"{name}: submitted video={video}, audio chunks={audioChunks}, output PID={mediaPid}, recording PID={recordPid}, reconnects=0.");
            }
            await Sample("Relay offline at startup");
            relay = StartRelay(); await Healthy(); await Wait(() => Task.FromResult(desktop.State == RelayConnectionState.Connected), 75); await Sample("Before outage");
            await Kill(relay); relay = null; await Wait(() => Task.FromResult(desktop.State == RelayConnectionState.Reconnecting), 10); await Sample("During outage");
            relay = StartRelay(); await Healthy(); await Wait(() => Task.FromResult(desktop.State == RelayConnectionState.Connected), 75); await Sample("After recovery");
            var unified = Field<UnifiedChatService>(window, "_unifiedChat"); var alerts = Field<AlertEngine>(window, "_alertEngine");
            var count = 0; var alertCount = 0;
            const string mappedEventId = "Kick:after-media-recovery:event";
            unified.EventReceived += evt => { if (evt.Id == mappedEventId) Interlocked.Increment(ref count); };
            alerts.Activated += alert => { if (alert.Event.Id == mappedEventId) Interlocked.Increment(ref alertCount); };
            var body = Encoding.UTF8.GetBytes("{\"broadcaster\":{\"user_id\":101},\"follower\":{\"user_id\":88,\"username\":\"recovered-viewer\"}}");
            async Task Post()
            {
                var stamp = DateTimeOffset.UtcNow.ToString("O"); var signed = Encoding.UTF8.GetBytes("after-media-recovery." + stamp + ".").Concat(body).ToArray();
                using var request = new HttpRequestMessage(HttpMethod.Post, relayUrl + "/webhooks/kick") { Content = new ByteArrayContent(body) };
                request.Headers.Add("Kick-Event-Message-Id", "after-media-recovery"); request.Headers.Add("Kick-Event-Message-Timestamp", stamp);
                request.Headers.Add("Kick-Event-Type", "channel.followed"); request.Headers.Add("Kick-Event-Signature", Convert.ToBase64String(rsa.SignData(signed, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)));
                using var response = await http.SendAsync(request); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }
            await Post(); await Wait(() => Task.FromResult(Volatile.Read(ref count) == 1), 5); await Post(); await Task.Delay(300); Assert.Equal(1, count);
            await Wait(() => Task.FromResult(Volatile.Read(ref alertCount) == 1), 5); Assert.Equal(1, alertCount);
            output.WriteLine("Signed Kick event after recovery: actual MainWindow mapper → UnifiedChat → existing AlertEngine exactly once: PASS.");
            Assert.Contains(RelayConnectionState.Reconnecting, states); Assert.Contains(RelayConnectionState.Connected, states);
            await stream.StopAsync(); await recording.StopRecordingAsync(); await receiver.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            using var packets = JsonDocument.Parse(await Capture(ffprobe, ["-v", "error", "-show_packets", "-show_entries", "packet=codec_type,pts_time", "-of", "json", streamFile]));
            var receivedStages = new List<object>();
            foreach (var phase in windows)
            {
                var matching = packets.RootElement.GetProperty("packets").EnumerateArray().Where(p => p.TryGetProperty("pts_time", out var timestamp) &&
                    double.TryParse(timestamp.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var time) && time >= phase.Start && time <= phase.End).ToArray();
                var video = matching.Count(p => p.GetProperty("codec_type").GetString() == "video"); var audioPackets = matching.Count(p => p.GetProperty("codec_type").GetString() == "audio");
                Assert.True(video > 5, phase.Name + " receiver video packets missing"); Assert.True(audioPackets > 10, phase.Name + " receiver audio packets missing");
                output.WriteLine($"{phase.Name}: received video packets={video}, audio packets={audioPackets}.");
                var bytes = await CaptureBytes(ffmpeg, ["-v", "error", "-ss", (phase.Start + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), "-i", streamFile,
                    "-t", "0.25", "-vn", "-f", "f32le", "-ar", "8000", "-ac", "1", "pipe:1"]);
                Assert.True(bytes.Length > 100); var peak = Enumerable.Range(0, bytes.Length / 4).Max(i => Math.Abs(BitConverter.ToSingle(bytes, i * 4))); Assert.True(peak > 0.01, "Decoded receiver audio is silent");
                var pixels = await CaptureBytes(ffmpeg, ["-v", "error", "-ss", (phase.Start + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), "-i", streamFile,
                    "-frames:v", "1", "-vf", "scale=16:16", "-pix_fmt", "rgb24", "-f", "rawvideo", "pipe:1"]);
                Assert.True(pixels.Length >= 768 && pixels.Max() > 30, "Decoded receiver video is black");
                receivedStages.Add(new { phase.Name, videoPackets = video, audioPackets, decodedAudioPeak = peak,
                    decodedVideoSha256 = Convert.ToHexString(SHA256.HashData(pixels)) });
            }
            using (var recorded = JsonDocument.Parse(await Capture(ffprobe, ["-v", "error", "-show_entries", "stream=codec_type", "-of", "json", recording.LastOutputFiles.Single()])))
            {
                var types = recorded.RootElement.GetProperty("streams").EnumerateArray().Select(s => s.GetProperty("codec_type").GetString()).ToArray();
                Assert.Contains("video", types); Assert.Contains("audio", types);
            }
            var report = new { version = AravalsStream.Core.Versioning.AppVersion.Version, mediaPid, recordPid, receiverPid = receiver.Id, streamReconnects = stream.ReconnectCount,
                stages = windows.Select(w => new { w.Name, w.Start, w.End, w.Video, w.Audio }), receivedStages,
                postRecoveryUnifiedEvents = count, postRecoveryAlerts = alertCount, result = "PASS" };
            await File.WriteAllTextAsync(Path.Combine(root, "results.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            output.WriteLine("Acceptance evidence: " + root);
        }
        finally
        {
            try
            {
            if (stream is not null) await stream.DisposeAsync();
            if (recording is not null) await recording.StopRecordingAsync();
            if (window is not null)
            {
                var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                await window.Dispatcher.InvokeAsync(() => { window.Closed += (_, _) => { closed.TrySetResult(); window.Dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }; FieldInfoFor(window, "_forceExit").SetValue(window, true); window.Close(); });
                await closed.Task.WaitAsync(TimeSpan.FromSeconds(15));
            }
            if (windowThread is not null) await windowThread.WaitAsync(TimeSpan.FromSeconds(15));
            }
            finally
            {
            foreach (var process in new[] { relay, sender, receiver }) if (process is not null) { await Kill(process); process.Dispose(); }
            // Keep only secret-free media/results; remove disposable test sessions and DPAPI credentials.
            if (File.Exists(relayState)) File.Delete(relayState);
            var client = Path.Combine(root, "client"); if (Directory.Exists(client)) Directory.Delete(client, true);
            ThreadPool.SetMinThreads(minWorkers, minIo);
            }
        }
    }
    [Theory]
    [InlineData(1234567L)] [InlineData(6133333L)] [InlineData(15000000L)]
    public void LegacyFfmpegProgressTimeIsAlwaysMicroseconds(long value)
    {
        var session = (FfmpegMediaSession)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(FfmpegMediaSession));
        typeof(FfmpegMediaSession).GetMethod("ParseProgress", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(session, ["out_time_ms=" + value]);
        Assert.Equal(value, Counter(session, "_lastOutTimeMicroseconds"));
    }
    private static FieldInfo FieldInfoFor(object value, string field) => value.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static T Field<T>(object value, string field) => (T)FieldInfoFor(value, field).GetValue(value)!;
    private static long Counter(object value, string field) => Field<long>(value, field);
    private static int FreePort() { var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port; }
    private static ProcessStartInfo Info(string executable, IEnumerable<string> arguments)
    { var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true }; foreach (var argument in arguments) info.ArgumentList.Add(argument); return info; }
    private static Process Start(ProcessStartInfo info) { var process = Process.Start(info)!; _ = process.StandardOutput.ReadToEndAsync(); _ = process.StandardError.ReadToEndAsync(); return process; }
    private static async Task Kill(Process process) { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
    private static async Task Wait(Func<Task<bool>> predicate, int seconds)
    { var clock = Stopwatch.StartNew(); while (clock.Elapsed.TotalSeconds < seconds) { if (await predicate()) return; await Task.Delay(100); } throw new TimeoutException("Acceptance condition did not become true."); }
    private static async Task<byte[]> CaptureBytes(string executable, string[] arguments)
    { using var process = Process.Start(Info(executable, arguments))!; var error = process.StandardError.ReadToEndAsync(); using var bytes = new MemoryStream(); await process.StandardOutput.BaseStream.CopyToAsync(bytes); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20)); Assert.True(process.ExitCode == 0, await error); return bytes.ToArray(); }
    private static async Task<string> Capture(string executable, string[] arguments) => Encoding.UTF8.GetString(await CaptureBytes(executable, arguments));
}
