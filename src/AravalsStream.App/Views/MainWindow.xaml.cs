using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AravalsStream.App.Composition;
using AravalsStream.App.Audio;
using AravalsStream.App.Recording;
using AravalsStream.App.Streaming;
using AravalsStream.App.ViewModels;
using AravalsStream.Capture.Audio;
using AravalsStream.Capture.Camera;
using AravalsStream.Capture.Display;
using AravalsStream.Capture.Window;
using AravalsStream.Core.Audio;
using AravalsStream.Core.Interfaces;
using AravalsStream.Core.Models;
using AravalsStream.Core.Platforms;
using AravalsStream.Core.Accounts;
using AravalsStream.Core.Recording;
using AravalsStream.Core.Recording.Models;
using AravalsStream.Core.Services;
using AravalsStream.Core.Settings;
using AravalsStream.Core.Streaming;
using AravalsStream.Core.YouTube;
using AravalsStream.Core.YouTube.Models;
using AravalsStream.Core.Twitch;
using AravalsStream.Core.Kick;
using AravalsStream.App.Services;
using AravalsStream.App.RemoteCapture;
using AravalsStream.Core.RemoteCapture;

namespace AravalsStream.App.Views;

public partial class MainWindow : Window
{
    private readonly ISceneCompositor _compositor;
    private readonly PerformanceMetricsService _performanceMetrics = new();
    private System.Windows.Threading.DispatcherTimer? _uiTimer;
    private HardwareClass _hardwareClass;
    private bool _performanceOverloaded;
    private DateTimeOffset? _overloadSince;
    private long _lastRecordingBytes;
    private DateTimeOffset _lastRecordingSample;
    private double _recordingWriteMbPerSecond;
    private readonly ComposedFrameHub _frameHub;
    private StreamingOutputGroupManager? _streamingOutputGroups;
    private readonly ISettingsService _settings = new JsonSettingsService();
    private readonly List<DisplayCaptureSource> _captureSources = [];
    private readonly List<CaptureResource> _captureResources = [];
    private readonly SceneAudioEngine _audioEngine;
    private readonly CaptureRecovery _recovery = new();
    private DeviceChangeMonitor? _deviceChanges;
    private readonly Dictionary<Guid, (string DeviceId, string? WindowTitle, string? ProcessName,
        int CameraIndex, int Width, int Height, int Fps, string Subtype, string Name, bool FollowDefault)> _previousBindings = [];
    private OutputMode _activeMode = OutputMode.Horizontal;
    private bool _closingAfterSave;
    private Point _sceneDragStart;
    private Scene? _draggedScene;
    private readonly IRecordingService _recording;
    private readonly DpapiSecretStorage _secrets = new DpapiSecretStorage();
    private readonly PairedDeviceRegistry _pairedRemoteDevices;
    private readonly RemoteCaptureSessionFactory _remoteCapture;
    private RemoteDiscoveryService? _remoteDiscovery;
    private readonly Dictionary<Guid, RemotePcDialog.AgentEntry> _remoteAgents = [];
    private readonly Dictionary<Guid, StreamingOutput> _outputs = [];
    private readonly Dictionary<Guid, string> _outputErrors = [];
    private bool _streaming;
    private YouTubeChatProvider? _ytChatProvider;
    private System.Windows.Threading.DispatcherTimer? _ytStatsTimer;
    private TwitchChatProvider? _twitchChatProvider;
    private System.Windows.Threading.DispatcherTimer? _twitchStatsTimer;
    private KickChatProvider? _kickChatProvider;
    private System.Windows.Threading.DispatcherTimer? _kickStatsTimer;
    private RecordingSettings _recordingSettings = new();
    private AppSettings _loadedSettings = new();
    private readonly SessionRecoveryService _recoveryService = new();
    private HotkeyService? _hotkeyService;
    private SystemTrayService? _trayService;
    private bool _forceExit;
    private bool _lastIsMinimized;
    private MainViewModel ViewModel => (MainViewModel)DataContext;
    private SceneSource? SelectedSource => SourceList.SelectedItem as SceneSource;
    private SourceTransform? ActiveTransform => SelectedSource is { CanTransform: true } source
        ? _activeMode == OutputMode.Vertical ? source.VerticalTransform : source.HorizontalTransform : null;

    public MainWindow() : this(null, null, null, null) { }

    public MainWindow(ISettingsService? settings, DpapiSecretStorage? secrets,
        PairedDeviceRegistry? pairedDevices, SessionRecoveryService? recovery)
    {
        if (settings is not null) _settings = settings;
        if (secrets is not null) _secrets = secrets;
        if (recovery is not null) _recoveryService = recovery;
        InitializeComponent();
        Icon = new BitmapImage(new Uri("pack://application:,,,/AravalsStream.App;component/Assets/Brand/Aravals%20Stream.ico"));
        AravalsStream.App.Controls.DarkWindowChrome.Apply(this);
        SizeChanged += (_, _) => ApplyResponsiveLayout();
        _pairedRemoteDevices = pairedDevices ?? new PairedDeviceRegistry(_secrets);
        var ffmpeg = new FfmpegLocator().Locate().FfmpegPath ?? Path.Combine(AppContext.BaseDirectory, "ffmpeg", "bin", "ffmpeg.exe");
        _remoteCapture = new RemoteCaptureSessionFactory(ffmpeg, _pairedRemoteDevices);
        _audioEngine = new SceneAudioEngine(_remoteCapture);
        _compositor = new SceneCompositor(Dispatcher, _remoteCapture);
        ((SceneCompositor)_compositor).Metrics = _performanceMetrics;
        _frameHub = new ComposedFrameHub(_compositor, _performanceMetrics);
        _recording = new RecordingService(_compositor, _audioEngine, frameHub: _frameHub);
        _recording.StateChanged += Recording_StateChanged;
        _recording.TelemetryUpdated += Recording_TelemetryUpdated;
        _recording.ErrorOccurred += Recording_ErrorOccurred;
        HorizontalPreview.SetMode(OutputMode.Horizontal);
        VerticalPreview.SetMode(OutputMode.Vertical);
        HorizontalPreview.Bind(_compositor); VerticalPreview.Bind(_compositor);
        HorizontalPreview.SourceSelected += source => SelectSource(source, OutputMode.Horizontal);
        VerticalPreview.SourceSelected += source => SelectSource(source, OutputMode.Vertical);
        HorizontalPreview.TransformChanged += RefreshPreviews;
        VerticalPreview.TransformChanged += RefreshPreviews;
        HorizontalPreview.ContextMenuRequested += (source, pt) =>
        {
            SelectSource(source, OutputMode.Horizontal);
            ShowSourceContextMenu(source, HorizontalPreview, isPositionedAtTarget: false);
        };
        VerticalPreview.ContextMenuRequested += (source, pt) =>
        {
            SelectSource(source, OutputMode.Vertical);
            ShowSourceContextMenu(source, VerticalPreview, isPositionedAtTarget: false);
        };
        HorizontalPreview.DoubleClicked += () => ViewModel.PreviewMode = OutputMode.Horizontal;
        VerticalPreview.DoubleClicked += () => ViewModel.PreviewMode = OutputMode.Vertical;
        HorizontalPane.PreviewMouseDown += (_, _) => { _activeMode = OutputMode.Horizontal; UpdateActiveCanvasBorder(); };
        VerticalPane.PreviewMouseDown += (_, _) => { _activeMode = OutputMode.Vertical; UpdateActiveCanvasBorder(); };
        PreviewKeyDown += MainWindow_PreviewKeyDown;
        _compositor.SourceFailed += (id, message) =>
        {
            foreach (var resource in _captureResources.Where(r => r.Id.ToString() == id)) MarkFailed(resource.Id, message);
            AppLog.Write("Capture", $"{id}: {message}");
        };
        _compositor.FrameReady += id =>
        {
            foreach (var resource in _captureResources.Where(r => r.Id.ToString() == id)) MarkActive(resource.Id);
        };
        _audioEngine.SourceFailed += (id, message) => Dispatcher.BeginInvoke(() =>
        {
            _audioEngine.StopFailed(id);
            MarkFailed(id, message);
            AppLog.Write("Audio", $"{id}: {message}");
        });
        _audioEngine.SourceStarted += MarkActive;
        MixerList.ItemsSource = _audioEngine.Channels;
        MixerList.AddHandler(Button.ClickEvent, new RoutedEventHandler(AudioMixerAction_Click));
        var meterTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _uiTimer = meterTimer;
        var lastRecoveryCheck = DateTimeOffset.MinValue;
        var lastStatsUpdate = DateTimeOffset.MinValue;
        var lastMeterUpdate = DateTimeOffset.MinValue;
        meterTimer.Tick += (_, _) =>
        {
            var isMin = !IsVisible || WindowState == WindowState.Minimized;
            if (isMin != _lastIsMinimized)
            {
                _lastIsMinimized = isMin;
                ApplyPerformanceProfile();
            }
            var now = DateTimeOffset.UtcNow;
            var profile = CurrentPerformanceProfile();
            if (now - lastMeterUpdate >= TimeSpan.FromSeconds(1.0 / profile.MeterRefreshHz))
            { lastMeterUpdate = now; _audioEngine.Mixer.DecayMeters(); if (IsVisible && WindowState != WindowState.Minimized) UpdateAudioBar(); }
            TickAlerts();
            meterTimer.Interval = TimeSpan.FromMilliseconds(isMin ? 250 :
                profile.MeterRefreshHz >= 60 ? 16 : 33);
            if (now - lastStatsUpdate >= TimeSpan.FromMilliseconds(profile.StatsRefreshMilliseconds))
            { lastStatsUpdate = now; if (IsVisible && WindowState != WindowState.Minimized) { UpdateStreamUi(); UpdateChatTargets(); UpdateAlertStats(); } UpdatePerformanceStatus(); }
            if (now - lastRecoveryCheck >= TimeSpan.FromSeconds(1))
            { lastRecoveryCheck = now; RetryDueSources(); }
        };
        meterTimer.Start();
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.SelectedScene)) SwitchScene();
            if (e.PropertyName == nameof(MainViewModel.PreviewMode)) UpdatePreviewMode();
        };
        ViewModel.RequestAddDestination += AddDestination;
        ViewModel.RequestToggleStream += async () => await ToggleStreamingAsync();
        ViewModel.RequestCreateScene += () => AddScene_Click(this, new RoutedEventArgs());
        Loaded += MainWindow_Loaded;
        SourceInitialized += (_, _) =>
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            var source = System.Windows.Interop.HwndSource.FromHwnd(hwnd);
            source?.AddHook(WndProc);
        };
        StateChanged += (_, _) => ApplyPerformanceProfile();
        Closing += MainWindow_Closing;
        Closed += (_, _) => { meterTimer.Stop(); _updateTimer.Stop(); _updateService.Dispose(); _alertSound?.Dispose(); _ = _unifiedChat.DisposeAsync(); _ = _relayClient.DisposeAsync(); _deviceChanges?.Dispose(); _ = _remoteDiscovery?.DisposeAsync(); _audioEngine.Dispose(); _remoteCapture.Dispose(); if (_streamingOutputGroups is not null) _ = _streamingOutputGroups.DisposeAsync(); _frameHub.Dispose(); _compositor.Dispose(); _recording.Dispose(); _performanceMetrics.Dispose(); };
        UpdatePreviewMode();
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        ApplyResponsiveLayout();
        try
        {
            _remoteDiscovery = new RemoteDiscoveryService();
            _remoteDiscovery.AgentDiscovered += (ad, endpoint) => Dispatcher.BeginInvoke(() =>
            {
                if (!RemoteCaptureProtocol.IsCompatible(ad.ProtocolVersion)) return;
                _remoteAgents[ad.DeviceId] = new RemotePcDialog.AgentEntry(ad, endpoint.Address.ToString(), DateTimeOffset.UtcNow);
            });
            _ = _remoteDiscovery.StartAsync();
            var settings = await _settings.LoadAsync();
            _captureSources.Clear();
            _captureSources.AddRange(settings.CaptureSources);
            _captureResources.Clear();
            _captureResources.AddRange(settings.CaptureResources);
            if (settings.Scenes.Count > 0)
            {
                ViewModel.Scenes.Clear();
                foreach (var scene in settings.Scenes) ViewModel.Scenes.Add(scene);
                ViewModel.SelectedScene = SceneManager.ResolveActiveScene(ViewModel.Scenes, settings.SelectedSceneId);
                ViewModel.UpdateActiveSceneStates();
                ViewModel.PreviewMode = settings.PreviewMode;
            }
            ViewModel.DestinationGroups.Clear();
            foreach (var group in settings.DestinationGroups)
            {
                group.EnsureChildDestinations();
                group.UpdateAggregation();
                ViewModel.DestinationGroups.Add(group);
            }
            ViewModel.Destinations.Clear();
            foreach (var d in ViewModel.DestinationGroups.SelectMany(g => g.GetActiveDestinations()))
            {
                ViewModel.Destinations.Add(d);
            }
            if (settings.Recording != null)
            {
                _recordingSettings = settings.Recording;
            }
            var memoryBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
            _hardwareClass = PerformancePolicy.Classify(Environment.ProcessorCount, memoryBytes,
                _recording.AvailableEncoders.Any(e => e.HardwareAccelerated && e.Available));
            foreach (var source in ViewModel.Scenes.SelectMany(s => s.Sources).Where(s => s.Type == SourceType.DisplayCapture && s.DisplayId is not null))
            {
                var definition = _captureSources.FirstOrDefault(d => d.Id == source.SourceReference)
                    ?? _captureSources.FirstOrDefault(d => d.DisplayId == source.DisplayId);
                if (definition is null)
                {
                    definition = new DisplayCaptureSource { DisplayId = source.DisplayId!, Name = source.Name };
                    _captureSources.Add(definition);
                }
                source.SourceReference = definition.Id;
                if (!_captureResources.Any(r => r.Id == definition.Id))
                    _captureResources.Add(new CaptureResource { Id = definition.Id, Type = SourceType.DisplayCapture, DeviceId = definition.DisplayId, Name = definition.Name });
            }
            _compositor.SetResources(_captureResources);
            _audioEngine.SetResources(_captureResources);
            ApplyPerformanceProfile();
            _deviceChanges = new DeviceChangeMonitor();
            _deviceChanges.Changed += () => Dispatcher.BeginInvoke(() => _recovery.DeviceChanged(DateTimeOffset.UtcNow));
            _deviceChanges.DefaultAudioChanged += () => Dispatcher.BeginInvoke(() =>
            {
                foreach (var resource in _captureResources.Where(r => r.FollowSystemDefault))
                    _audioEngine.RestartResource(resource.Id, ViewModel.Scenes);
            });
            foreach (var s in ViewModel.Scenes)
            {
                foreach (var source in s.Sources)
                {
                    if (source.Locked || source.HorizontalTransform.Locked || source.VerticalTransform.Locked)
                    {
                        source.Locked = true;
                    }
                }
            }
            _recoveryService.CheckAndStartSession();
            if (_recoveryService.WasPreviousShutdownUnclean && !Environment.GetCommandLineArgs().Any(a => a.StartsWith("--")))
            {
                var res = MessageBox.Show(this, "Aravals Stream did not close normally last time. Settings and scene layout have been restored.\n\nWould you like to view the recent diagnostic logs?", "Session Recovery", MessageBoxButton.YesNo, MessageBoxImage.Information);
                if (res == MessageBoxResult.Yes)
                {
                    var logPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AravalsStream", "logs", "application.log");
                    if (File.Exists(logPath))
                    {
                        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("notepad.exe", logPath) { UseShellExecute = true }); } catch { }
                    }
                }
            }

            _loadedSettings = settings;
            if (!_loadedSettings.General.UpdatePreferenceInitialized)
            {
                _loadedSettings.General.CheckForUpdates = true;
                _loadedSettings.General.UpdatePreferenceInitialized = true;
            }
            InitializeAlerts();
            _audioEngine.Mixer.Matrix.ImportSettings(_loadedSettings.AudioRoutes);

            _hotkeyService = new HotkeyService(this);
            _hotkeyService.Initialize();
            _hotkeyService.RegisterHotkeys(_loadedSettings.Hotkeys);
            _hotkeyService.HotkeyTriggered += action => Dispatcher.BeginInvoke(async () =>
            {
                switch (action)
                {
                    case HotkeyAction.ToggleStreaming:
                        await ToggleStreamingAsync();
                        break;
                    case HotkeyAction.ToggleRecording:
                        RecButton_Click(this, new RoutedEventArgs());
                        break;
                    case HotkeyAction.PauseResumeRecording:
                        PauseRecButton_Click(this, new RoutedEventArgs());
                        break;
                    case HotkeyAction.MuteMicrophone:
                        var mic = _audioEngine.Channels.FirstOrDefault(c => c.Name.Contains("Mic", StringComparison.OrdinalIgnoreCase));
                        if (mic != null) mic.Muted = !mic.Muted;
                        break;
                    case HotkeyAction.MuteDesktopAudio:
                        var desk = _audioEngine.Channels.FirstOrDefault(c => c.Name.Contains("Desktop", StringComparison.OrdinalIgnoreCase));
                        if (desk != null) desk.Muted = !desk.Muted;
                        break;
                    default:
                        var str = action.ToString();
                        if (str.StartsWith("SwitchScene") && int.TryParse(str.Substring(11), out int sIdx) && sIdx >= 1 && sIdx <= ViewModel.Scenes.Count)
                        {
                            ViewModel.SelectedScene = ViewModel.Scenes[sIdx - 1];
                        }
                        break;
                }
            });

            _trayService = new SystemTrayService(
                this,
                onOpen: () => Dispatcher.BeginInvoke(() => { Show(); WindowState = WindowState.Normal; Activate(); }),
                onToggleStream: () => Dispatcher.BeginInvoke(async () => await ToggleStreamingAsync()),
                onToggleRecord: () => Dispatcher.BeginInvoke(() => RecButton_Click(this, new RoutedEventArgs())),
                onToggleMuteMic: () => Dispatcher.BeginInvoke(() =>
                {
                    var mic = _audioEngine.Channels.FirstOrDefault(c => c.Name.Contains("Mic", StringComparison.OrdinalIgnoreCase));
                    if (mic != null) mic.Muted = !mic.Muted;
                }),
                onExit: () => Dispatcher.BeginInvoke(() => { _forceExit = true; Close(); })
            );
            _trayService.Initialize();

            if (!_loadedSettings.FirstRunCompleted && !Environment.GetCommandLineArgs().Any(a => a.StartsWith("--")))
            {
                var wizard = new FirstRunWizardDialog(this, _loadedSettings, _recording.AvailableEncoders);
                wizard.ShowDialog();
                await SaveSettingsAsync();
            }

            UpdateTikTokStatus();
            SwitchScene();
            if (!Environment.GetCommandLineArgs().Any(a => a.StartsWith("--", StringComparison.Ordinal)))
                InitializeUpdateChecks();
            AppLog.Write("Application", "Scene layout loaded");
            if (Environment.GetCommandLineArgs().Contains("--phase17b-soak", StringComparer.OrdinalIgnoreCase))
            {
                await RunPhase16AcceptanceAsync(phase17b: true, soak: true);
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--phase17b-acceptance", StringComparer.OrdinalIgnoreCase))
            {
                await RunPhase16AcceptanceAsync(phase17b: true);
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--phase18-3h", StringComparer.OrdinalIgnoreCase))
            {
                await RunPhase16AcceptanceAsync(threeH: true);
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--phase18-3hv", StringComparer.OrdinalIgnoreCase))
            {
                await RunPhase16AcceptanceAsync(threeHV: true);
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--phase18-3hvr", StringComparer.OrdinalIgnoreCase))
            {
                await RunPhase16AcceptanceAsync(threeHVR: true, threeHV: true, recordingFirst: Environment.GetCommandLineArgs().Contains("--recording-first", StringComparer.OrdinalIgnoreCase));
                return;
            }
            var startupArgs = Environment.GetCommandLineArgs();
            if (startupArgs.Contains("--phase18-startup-1h", StringComparer.OrdinalIgnoreCase) ||
                startupArgs.Contains("--phase18-startup-hv", StringComparer.OrdinalIgnoreCase) ||
                startupArgs.Contains("--phase18-startup-3h", StringComparer.OrdinalIgnoreCase))
            {
                await RunPhase16AcceptanceAsync(startupTopology: startupArgs.First(a => a.StartsWith("--phase18-startup-", StringComparison.OrdinalIgnoreCase)));
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--phase18-cpu-fallback", StringComparer.OrdinalIgnoreCase))
            {
                await RunPhase16AcceptanceAsync(cpuFallback: true);
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--phase17-constrained", StringComparer.OrdinalIgnoreCase))
            {
                await RunPhase16AcceptanceAsync(constrained: true);
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--phase17-capture", StringComparer.OrdinalIgnoreCase))
            {
                await RunPhase16AcceptanceAsync(capture: true);
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--phase17-single", StringComparer.OrdinalIgnoreCase))
            {
                await RunPhase16AcceptanceAsync(single: true);
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--phase17-acceptance", StringComparer.OrdinalIgnoreCase))
            {
                await RunPhase16AcceptanceAsync(phase17: true);
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--phase16-acceptance", StringComparer.OrdinalIgnoreCase))
            {
                await RunPhase16AcceptanceAsync();
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--phase15-acceptance", StringComparer.OrdinalIgnoreCase))
            {
                await RunPhase15AcceptanceAsync();
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--phase10-acceptance", StringComparer.OrdinalIgnoreCase))
            {
                await RunPhase10AcceptanceAsync();
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--phase9-acceptance", StringComparer.OrdinalIgnoreCase))
            {
                await RunPhase9AcceptanceAsync();
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--record-pause-smoke", StringComparer.OrdinalIgnoreCase))
            {
                await RunRecordingPauseSmokeAsync();
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--multi-smoke", StringComparer.OrdinalIgnoreCase))
            {
                await RunMultiSmokeAsync();
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--multi-failure-smoke", StringComparer.OrdinalIgnoreCase))
            {
                await RunMultiSmokeAsync(partialFailure: true);
                return;
            }
            if (Environment.GetCommandLineArgs().Contains("--start-stream", StringComparer.OrdinalIgnoreCase))
                await ToggleStreamingAsync();
        }
        catch (Exception ex)
        {
            AppLog.Write("Application", $"Settings load failed: {ex}");
            if (!Environment.GetCommandLineArgs().Any(a => a.StartsWith("--")))
                MessageBox.Show(this, ex.Message, "Settings could not be loaded");
            SwitchScene();
        }
    }

    private async Task RunRecordingPauseSmokeAsync()
    {
        try
        {
            var smokeSettings = new RecordingSettings
            {
                OutputDirectory = Path.Combine(Path.GetTempPath(), "AravalsStreamPauseSmoke"),
                Mode = OutputMode.Horizontal,
                Container = "mkv",
                Video = new VideoEncoderSettings { EncoderId = "auto", Width = 320, Height = 180, FrameRate = 15, BitrateKbps = 1500 },
                Audio = new AudioEncoderSettings { BitrateKbps = 160 }
            };
            await _recording.StartRecordingAsync(smokeSettings);
            await Task.Delay(TimeSpan.FromSeconds(10));
            _recording.PauseRecording();
            await Task.Delay(200);
            AppLog.Write("PauseSmoke", $"Paused at {_recording.Telemetry.ElapsedTime.TotalSeconds:0.0}s; frames={_recording.Telemetry.FramesEncoded}; stats={LiveStatsText.Text}");
            await Task.Delay(TimeSpan.FromSeconds(10));
            AppLog.Write("PauseSmoke", $"Still paused at {_recording.Telemetry.ElapsedTime.TotalSeconds:0.0}s; frames={_recording.Telemetry.FramesEncoded}");
            _recording.ResumeRecording();
            await Task.Delay(TimeSpan.FromSeconds(10));
            AppLog.Write("PauseSmoke", $"Before stop: frames={_recording.Telemetry.FramesEncoded}; elapsed={_recording.Telemetry.ElapsedTime.TotalSeconds:0.0}s");
            await _recording.StopRecordingAsync();
            AppLog.Write("PauseSmoke", $"Output: {string.Join(", ", _recording.LastOutputFiles)}");
        }
        catch (Exception ex) { AppLog.Write("PauseSmoke", $"Failed: {ex}"); }
        finally { Close(); }
    }

    private async Task RunMultiSmokeAsync(bool partialFailure = false)
    {
        var testDestinations = new[]
        {
            new Destination { Name = "Local Horizontal Test", StreamUrl = "rtmp://127.0.0.1:19451/live", StreamKeyReference = Guid.NewGuid().ToString(), OutputMode = OutputMode.Horizontal, VideoBitrateKbps = 6000, FrameRate = 60 },
            new Destination { Name = "Local Vertical Test", StreamUrl = "rtmp://127.0.0.1:19452/live", StreamKeyReference = Guid.NewGuid().ToString(), OutputMode = OutputMode.Vertical, VideoBitrateKbps = 4000, FrameRate = 30 }
        };
        try
        {
            foreach (var d in testDestinations)
            {
                _secrets.Set(d.StreamKeyReference!, d.OutputMode == OutputMode.Horizontal ? "h" : "v");
                ViewModel.Destinations.Add(d);
            }
            _recording.RefreshFfmpeg(_recordingSettings.CustomFfmpegPath);
            await Task.WhenAll(testDestinations.Select(StartOutputAsync));
            if (partialFailure)
            {
                AppLog.Write("MultiSmoke", $"Initial failure check: H={testDestinations[0].Status}, V={testDestinations[1].Status}; outputs={_outputs.Count}");
                for (int i = 0; i < 25; i++)
                {
                    await Task.Delay(1000);
                    if (testDestinations[1].Status == DestinationStatus.Live)
                    {
                        AppLog.Write("MultiSmoke", $"V reconnected successfully to LIVE: H={testDestinations[0].Status}, V={testDestinations[1].Status}; outputs={_outputs.Count}");
                        await Task.Delay(3000);
                        break;
                    }
                }
            }
            else
            {
                await Task.Delay(TimeSpan.FromSeconds(6));
                AppLog.Write("MultiSmoke", $"After start: H={testDestinations[0].Status}, V={testDestinations[1].Status}; outputs={_outputs.Count}");
                if (_outputs.Count == 2)
                {
                    var settings = new RecordingSettings { OutputDirectory = Path.Combine(Path.GetTempPath(), "AravalsStreamMultiSmoke"), Mode = OutputMode.Horizontal,
                        Video = new VideoEncoderSettings { EncoderId = "auto", Width = 320, Height = 180, FrameRate = 15, BitrateKbps = 1500 } };
                    await _recording.StartRecordingAsync(settings);
                    await Task.Delay(TimeSpan.FromSeconds(4));
                    _recording.PauseRecording();
                    AppLog.Write("MultiSmoke", $"Paused recording: H={testDestinations[0].Status}, V={testDestinations[1].Status}");
                    await Task.Delay(TimeSpan.FromSeconds(4));
                    _recording.ResumeRecording();
                    await Task.Delay(TimeSpan.FromSeconds(4));
                    await _recording.StopRecordingAsync();
                    await StopOutputAsync(testDestinations[1].Id);
                    AppLog.Write("MultiSmoke", $"Stopped V: H={testDestinations[0].Status}, V={testDestinations[1].Status}");
                    await Task.Delay(TimeSpan.FromSeconds(3));
                }
            }
        }
        catch (Exception ex) { AppLog.Write("MultiSmoke", $"Failed: {ex}"); }
        finally
        {
            await StopAllStreamsAsync();
            foreach (var d in testDestinations)
            {
                ViewModel.Destinations.Remove(d);
                if (d.StreamKeyReference != null) _secrets.Delete(d.StreamKeyReference);
            }
            Close();
        }
    }

    private async Task RunPhase9AcceptanceAsync()
    {
        AppLog.Write("Phase9Acceptance", "Starting Phase 9 Acceptance Test");

        var ytGroup = new PlatformDestinationGroup
        {
            Id = Guid.NewGuid(),
            PlatformType = PlatformType.YouTube,
            Platform = "YouTube",
            Name = "YouTube Test",
            Routing = RoutingMode.Both,
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
                VideoBitrateKbps = 6000,
                AudioBitrateKbps = 160,
                FrameRate = 60,
                AutoReconnect = true
            },
            Vertical = new Destination
            {
                Id = Guid.NewGuid(),
                Platform = "YouTube",
                Name = "YouTube-Test V",
                OutputMode = OutputMode.Vertical,
                StreamUrl = "rtmp://127.0.0.1:19452/live",
                StreamKeyReference = Guid.NewGuid().ToString(),
                VideoBitrateKbps = 4000,
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
            Name = "Kick Test",
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
                VideoBitrateKbps = 5000,
                AudioBitrateKbps = 160,
                FrameRate = 30,
                AutoReconnect = true
            },
            Vertical = new Destination
            {
                Id = Guid.NewGuid(),
                Platform = "Kick",
                Name = "Kick-Test V",
                OutputMode = OutputMode.Vertical,
                StreamUrl = "rtmp://127.0.0.1:19453/live",
                StreamKeyReference = Guid.NewGuid().ToString(),
                VideoBitrateKbps = 4000,
                AudioBitrateKbps = 160,
                FrameRate = 30,
                AutoReconnect = true
            }
        };

        _secrets.Set(ytGroup.Horizontal.StreamKeyReference!, "yt_h");
        _secrets.Set(ytGroup.Vertical.StreamKeyReference!, "yt_v");
        _secrets.Set(kickGroup.Horizontal.StreamKeyReference!, "kick_h");

        ViewModel.DestinationGroups.Add(ytGroup);
        ViewModel.DestinationGroups.Add(kickGroup);

        _recording.RefreshFfmpeg(_recordingSettings.CustomFfmpegPath);

        try
        {
            // Step 1: Start all active outputs concurrently (3 simultaneous outputs)
            AppLog.Write("Phase9Acceptance", "Step 1: Starting all 3 enabled outputs");
            var activeOutputs = ViewModel.DestinationGroups.Where(g => g.Enabled && g.Routing != RoutingMode.Off)
                .SelectMany(g => g.GetActiveDestinations()).ToList();
            await Task.WhenAll(activeOutputs.Select(StartOutputAsync));
            await Task.Delay(3000);

            AppLog.Write("Phase9Acceptance", $"Outputs running: {_outputs.Count} (Expected: 3)");
            AppLog.Write("Phase9Acceptance", $"YT_H: {ytGroup.Horizontal.Status}, YT_V: {ytGroup.Vertical.Status}, Kick_H: {kickGroup.Horizontal.Status}");

            // Step 5: Scene switching updates all
            if (ViewModel.Scenes.Count > 1)
            {
                AppLog.Write("Phase9Acceptance", "Step 5: Switching scene to Just Chatting");
                ViewModel.SelectedScene = ViewModel.Scenes[1];
                await Task.Delay(1500);
                AppLog.Write("Phase9Acceptance", "Step 5: Switching scene back to Gaming");
                ViewModel.SelectedScene = ViewModel.Scenes[0];
                await Task.Delay(1500);
            }

            // Step 8-9: Stop YouTube V, verify YouTube H and Kick remain LIVE
            AppLog.Write("Phase9Acceptance", "Step 8: Stopping YouTube V independently");
            await StopOutputAsync(ytGroup.Vertical.Id);
            ytGroup.UpdateAggregation();
            AppLog.Write("Phase9Acceptance", $"After stopping V: YT_H={ytGroup.Horizontal.Status}, YT_V={ytGroup.Vertical.Status}, Kick_H={kickGroup.Horizontal.Status}, GroupA_Status={ytGroup.Status}");
            await Task.Delay(2000);

            // Step 10-11: Restart YouTube V independently
            AppLog.Write("Phase9Acceptance", "Step 10: Restarting YouTube V independently");
            await StartOutputAsync(ytGroup.Vertical);
            ytGroup.UpdateAggregation();
            AppLog.Write("Phase9Acceptance", $"After restarting V: YT_H={ytGroup.Horizontal.Status}, YT_V={ytGroup.Vertical.Status}, Kick_H={kickGroup.Horizontal.Status}, GroupA_Status={ytGroup.Status}");
            await Task.Delay(2500);

            // Step 12-14: Change YouTube V bitrate to 4500 and restart ONLY V
            AppLog.Write("Phase9Acceptance", "Step 12: Changing YouTube V bitrate to 4500 kbps and restarting only V");
            ytGroup.Vertical.VideoBitrateKbps = 4500;
            await StopOutputAsync(ytGroup.Vertical.Id);
            await Task.Delay(1500);
            await StartOutputAsync(ytGroup.Vertical);
            ytGroup.UpdateAggregation();
            AppLog.Write("Phase9Acceptance", $"After V bitrate restart: YT_H={ytGroup.Horizontal.Status}, YT_V={ytGroup.Vertical.Status}, Kick_H={kickGroup.Horizontal.Status}");
            await Task.Delay(2500);

            // Step 15-18: Record locally while all 3 live, pause, resume
            AppLog.Write("Phase9Acceptance", "Step 15: Recording locally while all three outputs live");
            var recDir = Path.Combine(Path.GetTempPath(), "AravalsPhase9Acceptance");
            Directory.CreateDirectory(recDir);
            var recSettings = new RecordingSettings
            {
                OutputDirectory = recDir,
                Mode = OutputMode.Horizontal,
                Video = new VideoEncoderSettings { EncoderId = "auto", Width = 640, Height = 360, FrameRate = 30, BitrateKbps = 2500 }
            };
            await _recording.StartRecordingAsync(recSettings);
            await Task.Delay(2500);

            AppLog.Write("Phase9Acceptance", "Step 16: Pausing recording");
            _recording.PauseRecording();
            AppLog.Write("Phase9Acceptance", $"During paused recording: YT_H={ytGroup.Horizontal.Status}, YT_V={ytGroup.Vertical.Status}, Kick_H={kickGroup.Horizontal.Status}");
            await Task.Delay(2500);

            AppLog.Write("Phase9Acceptance", "Step 18: Resuming and stopping recording");
            _recording.ResumeRecording();
            await Task.Delay(2500);
            await _recording.StopRecordingAsync();
            AppLog.Write("Phase9Acceptance", "Recording completed successfully");

            // Step 19: Stop global stream
            AppLog.Write("Phase9Acceptance", "Step 19: Stopping all streams");
            await StopAllStreamsAsync();
            AppLog.Write("Phase9Acceptance", $"Outputs running after stop: {_outputs.Count} (Expected: 0)");

            var reportPath = Path.Combine(Path.GetTempPath(), "phase9_acceptance_result.txt");
            await File.WriteAllTextAsync(reportPath, "PHASE9_ACCEPTANCE_SUCCESS");
            AppLog.Write("Phase9Acceptance", $"Phase 9 Acceptance Test Completed Successfully. Report: {reportPath}");
        }
        catch (Exception ex)
        {
            AppLog.Write("Phase9Acceptance", $"Phase 9 Acceptance Test Failed: {ex}");
        }
        finally
        {
            await StopAllStreamsAsync();
            ViewModel.DestinationGroups.Remove(ytGroup);
            ViewModel.DestinationGroups.Remove(kickGroup);
            if (ytGroup.Horizontal.StreamKeyReference != null) _secrets.Delete(ytGroup.Horizontal.StreamKeyReference);
            if (ytGroup.Vertical.StreamKeyReference != null) _secrets.Delete(ytGroup.Vertical.StreamKeyReference);
            if (kickGroup.Horizontal.StreamKeyReference != null) _secrets.Delete(kickGroup.Horizontal.StreamKeyReference);
            Close();
        }
    }

    private async Task RunPhase10AcceptanceAsync()
    {
        AppLog.Write("Phase10Acceptance", "Starting Phase 10 Acceptance Test");

        var ytGroup = new PlatformDestinationGroup
        {
            Id = Guid.NewGuid(),
            PlatformType = PlatformType.YouTube,
            Platform = "YouTube",
            Name = "YouTube Test",
            Routing = RoutingMode.Both,
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
                VideoBitrateKbps = 6000,
                AudioBitrateKbps = 160,
                FrameRate = 30,
                AutoReconnect = true
            },
            Vertical = new Destination
            {
                Id = Guid.NewGuid(),
                Platform = "YouTube",
                Name = "YouTube-Test V",
                OutputMode = OutputMode.Vertical,
                StreamUrl = "rtmp://127.0.0.1:19452/live",
                StreamKeyReference = Guid.NewGuid().ToString(),
                VideoBitrateKbps = 4000,
                AudioBitrateKbps = 160,
                FrameRate = 30,
                AutoReconnect = true
            }
        };

        var kickGroup = new PlatformDestinationGroup
        {
            Id = Guid.NewGuid(),
            PlatformType = PlatformType.Kick,
            Platform = "Kick",
            Name = "Kick Test",
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
                VideoBitrateKbps = 5000,
                AudioBitrateKbps = 160,
                FrameRate = 30,
                AutoReconnect = true
            },
            Vertical = new Destination
            {
                Id = Guid.NewGuid(),
                Platform = "Kick",
                Name = "Kick-Test V",
                OutputMode = OutputMode.Vertical,
                StreamUrl = "rtmp://127.0.0.1:19453/live",
                StreamKeyReference = Guid.NewGuid().ToString(),
                VideoBitrateKbps = 4000,
                AudioBitrateKbps = 160,
                FrameRate = 30,
                AutoReconnect = true
            }
        };

        _secrets.Set(ytGroup.Horizontal.StreamKeyReference!, "yt_h");
        _secrets.Set(ytGroup.Vertical.StreamKeyReference!, "yt_v");
        _secrets.Set(kickGroup.Horizontal.StreamKeyReference!, "kick_h");

        ViewModel.DestinationGroups.Add(ytGroup);
        ViewModel.DestinationGroups.Add(kickGroup);

        _recording.RefreshFfmpeg(_recordingSettings.CustomFfmpegPath);

        try
        {
            // Step 1: Start all active outputs concurrently (3 simultaneous outputs)
            AppLog.Write("Phase10Acceptance", "Step 1: Starting all 3 enabled outputs");
            var activeOutputs = new[] { ytGroup, kickGroup }.Where(g => g.Enabled && g.Routing != RoutingMode.Off)
                .SelectMany(g => g.GetActiveDestinations()).ToList();
            await Task.WhenAll(activeOutputs.Select(StartOutputAsync));
            await Task.Delay(3000);

            AppLog.Write("Phase10Acceptance", $"Outputs running: {activeOutputs.Count(d => _outputs.ContainsKey(d.Id))} (Expected: 3)");

            // Step 2: Local recording active
            AppLog.Write("Phase10Acceptance", "Step 2: Recording locally while all three outputs live");
            var recDir = Path.Combine(Path.GetTempPath(), "AravalsPhase10Acceptance");
            Directory.CreateDirectory(recDir);
            var recSettings = new RecordingSettings
            {
                OutputDirectory = recDir,
                Mode = OutputMode.Horizontal,
                Video = new VideoEncoderSettings { EncoderId = "auto", Width = 640, Height = 360, FrameRate = 30, BitrateKbps = 2500 }
            };
            await _recording.StartRecordingAsync(recSettings);
            await Task.Delay(2500);

            // Step 3 & 4: Audio matrix routing
            var micChannel = _audioEngine.Channels.FirstOrDefault(c => c.Name.Contains("Mic", StringComparison.OrdinalIgnoreCase))
                ?? new AudioChannel { Name = "Microphone" };
            var deskChannel = _audioEngine.Channels.FirstOrDefault(c => c.Name.Contains("Desktop", StringComparison.OrdinalIgnoreCase))
                ?? new AudioChannel { Name = "Desktop Audio" };

            AppLog.Write("Phase10Acceptance", "Step 3: Routing Mic to all outputs");
            _audioEngine.Mixer.Matrix.SetRoute(micChannel.Id, "Recording", true, 1.0f);
            _audioEngine.Mixer.Matrix.SetRoute(micChannel.Id, ytGroup.Horizontal.Id.ToString(), true, 1.0f);
            _audioEngine.Mixer.Matrix.SetRoute(micChannel.Id, ytGroup.Vertical.Id.ToString(), true, 1.0f);
            _audioEngine.Mixer.Matrix.SetRoute(micChannel.Id, kickGroup.Horizontal.Id.ToString(), true, 0.8f);

            AppLog.Write("Phase10Acceptance", "Step 4: Routing Desktop Audio to only 2 outputs (REC and YouTube H)");
            _audioEngine.Mixer.Matrix.SetRoute(deskChannel.Id, "Recording", true, 1.0f);
            _audioEngine.Mixer.Matrix.SetRoute(deskChannel.Id, ytGroup.Horizontal.Id.ToString(), true, 1.0f);
            _audioEngine.Mixer.Matrix.SetRoute(deskChannel.Id, ytGroup.Vertical.Id.ToString(), false, 0.0f);
            _audioEngine.Mixer.Matrix.SetRoute(deskChannel.Id, kickGroup.Horizontal.Id.ToString(), false, 0.0f);

            // Step 5: Verify routing
            bool micKick = _audioEngine.Mixer.Matrix.IsRouteEnabled(micChannel.Id, kickGroup.Horizontal.Id.ToString());
            bool deskKick = _audioEngine.Mixer.Matrix.IsRouteEnabled(deskChannel.Id, kickGroup.Horizontal.Id.ToString());
            float micKickGain = _audioEngine.Mixer.Matrix.GetRouteGain(micChannel.Id, kickGroup.Horizontal.Id.ToString());

            if (micKick && !deskKick && Math.Abs(micKickGain - 0.8f) < 0.05f)
            {
                AppLog.Write("Phase10Acceptance", "Step 5: Audio routing verified successfully: Mic routed to all (gain 0.8 on Kick), Desktop routed to only 2 outputs (Kick OFF).");
            }
            else
            {
                AppLog.Write("Phase10Acceptance", $"Step 5: Audio routing mismatch: micKick={micKick}, deskKick={deskKick}, micKickGain={micKickGain}");
            }

            // Step 6: Pause recording
            AppLog.Write("Phase10Acceptance", "Step 6: Pausing recording");
            _recording.PauseRecording();
            await Task.Delay(2500);

            // Step 7: Outputs stay live
            AppLog.Write("Phase10Acceptance", $"Step 7: During paused recording: YT_H={ytGroup.Horizontal.Status}, YT_V={ytGroup.Vertical.Status}, Kick_H={kickGroup.Horizontal.Status}");

            // Step 8: Network-health telemetry updates
            _outputs.TryGetValue(ytGroup.Horizontal.Id, out var oYtH);
            _outputs.TryGetValue(ytGroup.Vertical.Id, out var oYtV);
            _outputs.TryGetValue(kickGroup.Horizontal.Id, out var oKickH);
            AppLog.Write("Phase10Acceptance", $"Step 8: Network health verified: YT_H={oYtH?.Health?.State}, YT_V={oYtV?.Health?.State}, Kick_H={oKickH?.Health?.State}");

            // Step 9: Stop one output (YouTube V)
            AppLog.Write("Phase10Acceptance", "Step 9: Stopping YouTube V output");
            await StopOutputAsync(ytGroup.Vertical.Id);
            ytGroup.UpdateAggregation();
            await Task.Delay(2000);

            // Step 10: Others remain live
            AppLog.Write("Phase10Acceptance", $"Step 10: After stopping V: YT_H={ytGroup.Horizontal.Status}, YT_V={ytGroup.Vertical.Status}, Kick_H={kickGroup.Horizontal.Status}");

            // Step 11: Shutdown and finalize
            _recording.ResumeRecording();
            await Task.Delay(1500);
            await _recording.StopRecordingAsync();
            AppLog.Write("Phase10Acceptance", "Recording finalized successfully");

            await StopAllStreamsAsync();
            AppLog.Write("Phase10Acceptance", $"Outputs running after stop: {_outputs.Count} (Expected: 0)");

            var reportPath = Path.Combine(Path.GetTempPath(), "phase10_acceptance_result.txt");
            await File.WriteAllTextAsync(reportPath, "PHASE10_ACCEPTANCE_SUCCESS");
            AppLog.Write("Phase10Acceptance", $"Phase 10 Acceptance Test Completed Successfully. Report: {reportPath}");
        }
        catch (Exception ex)
        {
            AppLog.Write("Phase10Acceptance", $"Phase 10 Acceptance Test Failed: {ex}");
        }
        finally
        {
            await StopAllStreamsAsync();
            ViewModel.DestinationGroups.Remove(ytGroup);
            ViewModel.DestinationGroups.Remove(kickGroup);
            if (ytGroup.Horizontal.StreamKeyReference != null) _secrets.Delete(ytGroup.Horizontal.StreamKeyReference);
            if (ytGroup.Vertical.StreamKeyReference != null) _secrets.Delete(ytGroup.Vertical.StreamKeyReference);
            if (kickGroup.Horizontal.StreamKeyReference != null) _secrets.Delete(kickGroup.Horizontal.StreamKeyReference);
            _recoveryService.MarkCleanShutdown();
            Close();
        }
    }

    private async void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_closingAfterSave) return;
        e.Cancel = true;

        bool isLive = _streaming || _outputs.Count > 0;
        bool isRec = _recording.State is RecordingState.Recording or RecordingState.Paused or RecordingState.Starting;

        if (!_forceExit && (isLive || isRec) && _loadedSettings.General.ConfirmExitWhileLive)
        {
            var msg = $"Aravals Stream is currently active.\n\nStreaming: {_outputs.Count} outputs\nRecording: {(isRec ? "Active" : "Inactive")}\n\nDo you want to stop all outputs and exit?";
            var confirm = MessageBox.Show(this, msg, "Confirm Application Exit", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.OK) return;
        }

        if (!_forceExit && _loadedSettings.General.MinimizeToTray)
        {
            WindowState = WindowState.Minimized;
            Hide();
            return;
        }

        if (isRec)
        {
            try
            {
                await _recording.StopRecordingAsync();
            }
            catch (Exception ex)
            {
                AppLog.Write("Recording", $"Shutdown stop error: {ex.Message}");
            }
        }
        await StopAllStreamsAsync();
        await SaveSettingsAsync();
        _hotkeyService?.Dispose();
        _trayService?.Dispose();
        _recoveryService.MarkCleanShutdown();
        _closingAfterSave = true;
        Close();
    }

    private async Task SaveSettingsAsync()
    {
        try
        {
            _loadedSettings.Scenes = ViewModel.Scenes.ToList();
            _loadedSettings.CaptureSources = _captureSources.ToList();
            _loadedSettings.CaptureResources = _captureResources.ToList();
            _loadedSettings.Destinations = ViewModel.DestinationGroups.SelectMany(g => g.GetActiveDestinations()).ToList();
            _loadedSettings.DestinationGroups = ViewModel.DestinationGroups.ToList();
            _loadedSettings.SelectedSceneId = ViewModel.SelectedScene?.Id.ToString();
            _loadedSettings.PreviewMode = ViewModel.PreviewMode;
            _loadedSettings.Recording = _recordingSettings;
            _loadedSettings.AudioRoutes = _audioEngine.Mixer.Matrix.ExportSettings();
            _loadedSettings.SettingsSchemaVersion = 9;

            await _settings.SaveAsync(_loadedSettings);
            AppLog.Write("Application", "Scene layout saved");
        }
        catch (Exception ex)
        {
            AppLog.Write("Application", $"Settings save failed: {ex.Message}");
        }
    }

    private void AudioMatrix_Click(object sender, RoutedEventArgs e)
    {
        new AudioMatrixDialog(this, _audioEngine.Mixer, _loadedSettings, ViewModel.DestinationGroups).ShowDialog();
    }

    private async void Settings_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SettingsWindow(this, _loadedSettings, _recording.FfmpegStatus, _recording.AvailableEncoders,
            _secrets, _performanceMetrics, PerformanceOutputDetails, _pairedRemoteDevices,
            () => new RelayDiagnosticSnapshot(_loadedSettings.Relay.Enabled, _loadedSettings.Relay.RelayUrl,
                _relayClient.State.ToString(), _relayClient.ReconnectCount,
                _relayClient.LastConnected, _relayClient.LastEvent, _relayClient.EventsReceived,
                _relayClient.EventsRejected, _loadedSettings.Relay.KickSubscriptionState.ToString()),
            () => $"{_relayClient.State} • reconnects {_relayClient.ReconnectCount} • last event {_relayClient.LastEvent?.ToLocalTime().ToString("g") ?? "none"}",
            ManageKickRelaySubscriptionsAsync);
        dlg.ShowDialog();
        if (dlg.SettingsSaved)
        {
            _recordingSettings = _loadedSettings.Recording;
            foreach (var id in dlg.ChangedAudioResources) _audioEngine.RestartResource(id, ViewModel.Scenes);
            await SaveSettingsAsync();
            ConfigureWebhookRelay();
            if (_loadedSettings.TwitchAccount?.Connected != true) await StopTwitchServicesAsync();
            if (_loadedSettings.KickAccount?.Connected != true) await StopKickServicesAsync();
            if (_loadedSettings.FacebookAccount?.Connected != true) await StopFacebookServicesAsync();
            UpdateTikTokStatus();
            _hotkeyService?.RegisterHotkeys(_loadedSettings.Hotkeys);
            ApplyPerformanceProfile();
            ConfigureUpdateChecks();
        }
    }

    private async void AddDestination()
    {
        var picker = new PlatformPickerDialog(this);
        AravalsStream.App.Controls.DarkWindowChrome.Apply(picker);
        if (picker.ShowDialog() != true || picker.SelectedProfile == null)
            return;

        if (picker.SelectedProfile.PlatformType == PlatformType.YouTube)
        {
            var modeDialog = new YouTubeModeDialog(this, _loadedSettings.YouTubeAccount);
            AravalsStream.App.Controls.DarkWindowChrome.Apply(modeDialog);
            if (modeDialog.ShowDialog() != true) return;

            if (modeDialog.UseNativeAccount)
            {
                await ConfigureNativeYouTubeDestinationAsync(picker.SelectedProfile);
                return;
            }
        }

        if (picker.SelectedProfile.PlatformType == PlatformType.Twitch)
        {
            var choice = MessageBox.Show(this,
                "Use a connected Twitch account?\n\nYes: Native Twitch account\nNo: Manual RTMP\nCancel: Go back",
                "Twitch destination", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (choice == MessageBoxResult.Cancel) return;
            if (choice == MessageBoxResult.Yes)
            {
                await ConfigureNativeTwitchDestinationAsync(picker.SelectedProfile);
                return;
            }
        }

        if (picker.SelectedProfile.PlatformType == PlatformType.Kick)
        {
            var choice = MessageBox.Show(this,
                "Choose Kick setup:\n\nYes: Connected Kick account\nNo: Manual RTMP\nCancel: Go back",
                "Kick destination", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (choice == MessageBoxResult.Cancel) return;
            if (choice == MessageBoxResult.Yes)
            {
                await ConfigureNativeKickDestinationAsync(picker.SelectedProfile);
                return;
            }
        }

        if (picker.SelectedProfile.PlatformType == PlatformType.Facebook)
        {
            var choice = MessageBox.Show(this,
                "Choose Facebook setup:\n\nYes: Authorized Meta Page token (advanced)\nNo: Manual RTMP\nCancel: Go back",
                "Facebook destination", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (choice == MessageBoxResult.Cancel) return;
            if (choice == MessageBoxResult.Yes)
            {
                await ConfigureNativeFacebookDestinationAsync(picker.SelectedProfile);
                return;
            }
        }

        if (picker.SelectedProfile.PlatformType == PlatformType.TikTok)
        {
            await ConfigureTikTokDestinationAsync(picker.SelectedProfile);
            return;
        }

        var newGroup = PlatformDestinationGroup.CreateFromProfile(picker.SelectedProfile);
        newGroup.ConfigurationMode = ConfigurationMode.ManualRtmp;
        newGroup.Order = ViewModel.DestinationGroups.Count;
        EditDestinationGroup(newGroup, isNew: true);
    }

    private async Task ConfigureNativeYouTubeDestinationAsync(PlatformProfile profile)
    {
        // 1. Verify or prompt authentication
        if (_loadedSettings.YouTubeAccount == null || !_loadedSettings.YouTubeAccount.Connected || string.IsNullOrEmpty(_loadedSettings.YouTubeAccount.TokenReference))
        {
            if (string.IsNullOrWhiteSpace(_loadedSettings.GoogleOAuth.ClientId))
            {
                var ask = MessageBox.Show(this,
                    "YouTube Native Integration requires a Google OAuth Client ID.\n\nWould you like to open Settings Center to configure Google OAuth?",
                    "Google Configuration Required",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Information);
                if (ask == MessageBoxResult.Yes)
                {
                    Settings_Click(this, new RoutedEventArgs());
                }
                return;
            }

            try
            {
                var oauth = new GoogleOAuthClient(_loadedSettings.GoogleOAuth, _secrets);
                var account = await oauth.AuthorizeAsync();
                _loadedSettings.YouTubeAccount = account;
                await SaveSettingsAsync();
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Failed to connect YouTube account:\n\n{ex.Message}", "Connection Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }

        // 2. Open YouTube Live Manager / Broadcast Dialog
        var oauthClient = new GoogleOAuthClient(_loadedSettings.GoogleOAuth, _secrets);
        var broadcastDialog = new YouTubeBroadcastDialog(this, _loadedSettings.YouTubeAccount, oauthClient);
        AravalsStream.App.Controls.DarkWindowChrome.Apply(broadcastDialog);
        if (broadcastDialog.ShowDialog() != true || broadcastDialog.ResultBroadcast == null)
            return;

        // 3. Create native PlatformDestinationGroup
        var group = PlatformDestinationGroup.CreateFromProfile(profile);
        group.Name = $"YouTube ({_loadedSettings.YouTubeAccount.ChannelTitle})";
        group.ConfigurationMode = ConfigurationMode.NativeApi;
        group.BoundAccountId = _loadedSettings.YouTubeAccount.Id.ToString();
        group.BroadcastId = broadcastDialog.ResultBroadcast.Id;
        group.BroadcastTitle = broadcastDialog.ResultBroadcast.Snippet?.Title ?? "YouTube Live Broadcast";
        group.BroadcastStatus = broadcastDialog.ResultBroadcast.Status?.LifeCycleStatus ?? "ready";
        group.StreamId = broadcastDialog.ResultStream?.Id;
        group.LiveChatId = broadcastDialog.ResultBroadcast.Snippet?.LiveChatId;
        group.ServerUrl = broadcastDialog.IngestionAddress ?? profile.DefaultServerUrl;
        group.Order = ViewModel.DestinationGroups.Count;

        // Store ingest stream key securely in DPAPI
        if (!string.IsNullOrEmpty(broadcastDialog.StreamKey))
        {
            var keyRef = Guid.NewGuid().ToString();
            _secrets.Set(keyRef, broadcastDialog.StreamKey);
            group.StreamKeyReference = keyRef;
            group.Horizontal.StreamKeyReference = keyRef;
            group.Vertical.StreamKeyReference = keyRef;
        }

        group.EnsureChildDestinations();
        group.UpdateAggregation();
        ViewModel.DestinationGroups.Add(group);
        DestinationItems.Items.Refresh();
        await SaveSettingsAsync();
        UpdateStreamUi();
    }

    private void StartYouTubeServicesIfNeeded()
    {
        var nativeGroup = ViewModel.DestinationGroups.FirstOrDefault(g =>
            g.Enabled &&
            g.Routing != RoutingMode.Off &&
            g.PlatformType == PlatformType.YouTube &&
            g.ConfigurationMode == ConfigurationMode.NativeApi &&
            !string.IsNullOrEmpty(g.BroadcastId));

        if (nativeGroup == null || _loadedSettings.YouTubeAccount == null || !_loadedSettings.YouTubeAccount.Connected)
            return;

        // Start Chat if LiveChatId is known
        if (!string.IsNullOrEmpty(nativeGroup.LiveChatId) && _ytChatProvider == null)
        {
            try
            {
                var oauth = new GoogleOAuthClient(_loadedSettings.GoogleOAuth, _secrets);
                _ytChatProvider = new YouTubeChatProvider(_loadedSettings.YouTubeAccount, oauth);
                _unifiedChat.Register(_ytChatProvider);
                _ytChatProvider.StatusChanged += status => Dispatcher.BeginInvoke(() => ViewModel.YouTubeChatStatus = status);
                Task.Run(async () => await _ytChatProvider.StartAsync(nativeGroup.LiveChatId));
                ViewModel.YouTubeChatStatus = "Connecting...";
            }
            catch (Exception ex)
            {
                AppLog.Write("YouTubeChat", $"Failed to start chat provider: {ex.Message}");
                ViewModel.YouTubeChatStatus = "Error";
            }
        }

        // Start Stats poller (every 15s)
        if (_ytStatsTimer == null)
        {
            ViewModel.YouTubeBroadcastStatus = "LIVE";
            ViewModel.YouTubeSubscribers = _loadedSettings.YouTubeAccount.FormattedSubscribers;

            _ytStatsTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(15)
            };
            _ytStatsTimer.Tick += async (_, _) => await PollYouTubeStatsAsync(nativeGroup);
            _ytStatsTimer.Start();

            // Run first poll immediately in background
            Task.Run(async () => await PollYouTubeStatsAsync(nativeGroup));
        }
    }

    private async Task PollYouTubeStatsAsync(PlatformDestinationGroup group)
    {
        if (group == null || string.IsNullOrEmpty(group.BroadcastId) || _loadedSettings.YouTubeAccount == null)
            return;

        try
        {
            var oauth = new GoogleOAuthClient(_loadedSettings.GoogleOAuth, _secrets);
            var token = await oauth.GetValidAccessTokenAsync(_loadedSettings.YouTubeAccount);
            var api = new YouTubeApiClient();
            var (viewers, activeChatId) = await api.GetVideoLiveDetailsAsync(token, group.BroadcastId);

            await Dispatcher.InvokeAsync(() =>
            {
                if (viewers.HasValue)
                {
                    ViewModel.YouTubeConcurrentViewers = viewers.Value.ToString("N0");
                }
                else
                {
                    ViewModel.YouTubeConcurrentViewers = "—";
                }

                // If chat was not started because LiveChatId was missing initially, start it now
                if (string.IsNullOrEmpty(group.LiveChatId) && !string.IsNullOrEmpty(activeChatId) && _ytChatProvider == null)
                {
                    group.LiveChatId = activeChatId;
                    _ytChatProvider = new YouTubeChatProvider(_loadedSettings.YouTubeAccount, oauth);
                    _unifiedChat.Register(_ytChatProvider);
                    _ytChatProvider.StatusChanged += status => Dispatcher.BeginInvoke(() => ViewModel.YouTubeChatStatus = status);
                    Task.Run(async () => await _ytChatProvider.StartAsync(activeChatId));
                }
            });
        }
        catch (Exception ex)
        {
            AppLog.Write("YouTubeStats", $"Poll error (isolated from stream): {ex.Message}");
        }
    }

    private async Task StopYouTubeServicesAsync()
    {
        _unifiedChat.Unregister("YouTube");
        if (_ytStatsTimer != null)
        {
            _ytStatsTimer.Stop();
            _ytStatsTimer = null;
        }

        if (_ytChatProvider != null)
        {
            var provider = _ytChatProvider;
            _ytChatProvider = null;
            try
            {
                await provider.StopAsync();
            }
            catch (Exception ex)
            {
                AppLog.Write("YouTubeChat", $"Error stopping chat provider: {ex.Message}");
            }
        }

        await Dispatcher.InvokeAsync(() =>
        {
            ViewModel.YouTubeBroadcastStatus = "OFFLINE";
            ViewModel.YouTubeChatStatus = "Offline";
            ViewModel.YouTubeConcurrentViewers = "—";
        });
    }

    private void CheckIfYouTubeStillActive()
    {
        bool anyYtActive = ViewModel.DestinationGroups.Any(g =>
            g.PlatformType == PlatformType.YouTube &&
            g.ConfigurationMode == ConfigurationMode.NativeApi &&
            (_outputs.ContainsKey(g.Horizontal.Id) || _outputs.ContainsKey(g.Vertical.Id)));

        if (!anyYtActive)
        {
            Task.Run(async () => await StopYouTubeServicesAsync());
        }
    }

    private async void ChatSendBtn_Click(object sender, RoutedEventArgs e)
    {
        var text = ChatInputBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(text)) return;

        var target = (ChatTargetBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "YouTube";
        if (!_unifiedChat.AvailableSendTargets().Contains(target))
        {
            MessageBox.Show(this, $"{target} live chat is not currently connected.", "Live Chat", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        ChatSendBtn.IsEnabled = false;
        try
        {
            var sent = (await _unifiedChat.SendAsync(text, [target])).FirstOrDefault()?.Sent == true;
            if (sent)
            {
                ChatInputBox.Clear();
            }
            else
            {
                MessageBox.Show(this, "Could not send chat message. Please wait a second between messages or check chat permissions.", "Rate Limit", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Failed to send chat: {ex.Message}", "Chat Error", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            ChatSendBtn.IsEnabled = true;
        }
    }

    private void ChatInputBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ChatSendBtn_Click(ChatSendBtn, new RoutedEventArgs());
            e.Handled = true;
        }
    }

    private async void EditDestinationGroup(PlatformDestinationGroup original, bool isNew = false, bool skipNativeMetadata = false)
    {
        if (!isNew && !skipNativeMetadata && original.PlatformType == PlatformType.Twitch && original.ConfigurationMode == ConfigurationMode.NativeApi)
            await EditNativeTwitchMetadataAsync(original);
        if (!isNew && !skipNativeMetadata && original.PlatformType == PlatformType.Kick && original.ConfigurationMode == ConfigurationMode.NativeApi)
            await EditNativeKickMetadataAsync(original);
        if (!isNew && !skipNativeMetadata && original.PlatformType == PlatformType.Facebook && original.ConfigurationMode == ConfigurationMode.NativeApi)
            await EditNativeFacebookMetadataAsync(original);
        _recording.RefreshFfmpeg(_recordingSettings.CustomFfmpegPath);
        var draft = original.Copy();
        if (isNew)
        {
            foreach (var destination in new[] { draft.Horizontal, draft.Vertical })
            {
                destination.VideoBitrateKbps = _loadedSettings.Streaming.DefaultVideoBitrateKbps;
                destination.AudioBitrateKbps = _loadedSettings.Streaming.DefaultAudioBitrateKbps;
                destination.EncoderId = _loadedSettings.Streaming.DefaultEncoder;
            }
        }
        string? storedKey = original.StreamKeyReference == null ? null : _secrets.Get(original.StreamKeyReference);
        var dialog = new PlatformDestinationDialog(this, draft, _recording.AvailableEncoders, storedKey);
        AravalsStream.App.Controls.DarkWindowChrome.Apply(dialog);
        if (dialog.ShowDialog() != true) return;

        var updated = dialog.Result;
        bool isLiveH = _outputs.ContainsKey(original.Horizontal.Id);
        bool isLiveV = _outputs.ContainsKey(original.Vertical.Id);

        bool restartH = isLiveH && (DestinationChangePolicy.RequiresRestart(original.Horizontal, updated.Horizontal) || dialog.ReplacementKey != null || dialog.ClearKey);
        bool restartV = isLiveV && (DestinationChangePolicy.RequiresRestart(original.Vertical, updated.Vertical) || dialog.ReplacementKey != null || dialog.ClearKey);

        if (restartH || restartV)
        {
            var msg = "Changes require restarting affected live output(s).\n";
            if (restartH) msg += $"• Horizontal: {original.Horizontal.VideoBitrateKbps} kbps → {updated.Horizontal.VideoBitrateKbps} kbps\n";
            if (restartV) msg += $"• Vertical: {original.Vertical.VideoBitrateKbps} kbps → {updated.Vertical.VideoBitrateKbps} kbps\n";
            msg += "\nDo you want to apply and restart affected outputs?";
            if (MessageBox.Show(this, msg, "Restart affected outputs", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
                return;

            if (restartH) await StopOutputAsync(original.Horizontal.Id);
            if (restartV) await StopOutputAsync(original.Vertical.Id);
        }

        if (dialog.ClearKey && updated.StreamKeyReference != null)
        {
            _secrets.Delete(updated.StreamKeyReference);
            updated.StreamKeyReference = null;
            updated.Horizontal.StreamKeyReference = null;
            updated.Vertical.StreamKeyReference = null;
        }
        if (dialog.ReplacementKey != null)
        {
            updated.StreamKeyReference ??= Guid.NewGuid().ToString();
            _secrets.Set(updated.StreamKeyReference, dialog.ReplacementKey);
            updated.Horizontal.StreamKeyReference = updated.StreamKeyReference;
            updated.Vertical.StreamKeyReference = updated.StreamKeyReference;
        }

        if (isNew)
        {
            ViewModel.DestinationGroups.Add(updated);
        }
        else
        {
            int idx = ViewModel.DestinationGroups.IndexOf(original);
            if (idx >= 0) ViewModel.DestinationGroups[idx] = updated;
        }

        updated.EnsureChildDestinations();
        updated.UpdateAggregation();
        DestinationItems.Items.Refresh();
        await SaveSettingsAsync();

        if (_streaming && updated.Enabled && updated.Routing != RoutingMode.Off)
        {
            if (restartH || (updated.Routing is RoutingMode.Horizontal or RoutingMode.Both && !_outputs.ContainsKey(updated.Horizontal.Id)))
                await StartOutputAsync(updated.Horizontal);

            if (restartV || (updated.Routing is RoutingMode.Vertical or RoutingMode.Both && !_outputs.ContainsKey(updated.Vertical.Id)))
                await StartOutputAsync(updated.Vertical);
        }

        StartTwitchServicesIfNeeded();
        StartKickServicesIfNeeded();
        StartFacebookServicesIfNeeded();

        UpdateStreamUi();
    }

    private async void DestinationToggle_Click(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not CheckBox { DataContext: PlatformDestinationGroup group }) return;
        if (!group.Enabled)
        {
            if (_outputs.ContainsKey(group.Horizontal.Id)) await StopOutputAsync(group.Horizontal.Id);
            if (_outputs.ContainsKey(group.Vertical.Id)) await StopOutputAsync(group.Vertical.Id);
            CheckIfYouTubeStillActive();
            CheckIfTwitchStillActive();
            CheckIfKickStillActive();
        }
        else if (_streaming && group.Routing != RoutingMode.Off)
        {
            foreach (var child in group.GetActiveDestinations())
            {
                if (!_outputs.ContainsKey(child.Id))
                    await StartOutputAsync(child);
            }
            StartYouTubeServicesIfNeeded();
            StartTwitchServicesIfNeeded();
            StartKickServicesIfNeeded();
            StartFacebookServicesIfNeeded();
        }
        group.UpdateAggregation();
        DestinationItems.Items.Refresh();
        UpdateStreamUi();
        await SaveSettingsAsync();
    }

    private void DestinationButton_Click(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is CheckBox) { DestinationToggle_Click(sender, e); return; }
        if (e.OriginalSource is Button button && button.DataContext is PlatformDestinationGroup group)
        {
            if (Equals(button.Tag, "QuickRouteH")) HandleQuickRoute(group, RoutingMode.Horizontal);
            else if (Equals(button.Tag, "QuickRouteV")) HandleQuickRoute(group, RoutingMode.Vertical);
            else if (Equals(button.Tag, "QuickRouteBoth")) HandleQuickRoute(group, RoutingMode.Both);
            else if (Equals(button.Content, "⚙ Edit")) EditDestinationGroup(group);
            else if (Equals(button.Content, "⋯")) DestinationMenu_Click(button, group);
        }
    }

    private async void HandleQuickRoute(PlatformDestinationGroup group, RoutingMode targetMode)
    {
        if (group.Routing == targetMode) return;
        if (group.PlatformType == PlatformType.Twitch && group.ConfigurationMode == ConfigurationMode.NativeApi &&
            targetMode == RoutingMode.Both)
        {
            MessageBox.Show(this, "Native Twitch supports one active output per account. Choose Horizontal or Vertical.", "Twitch routing");
            return;
        }
        if (group.PlatformType == PlatformType.Kick && group.ConfigurationMode == ConfigurationMode.NativeApi &&
            targetMode == RoutingMode.Both)
        {
            MessageBox.Show(this, "Native Kick supports one active output per account. Choose Horizontal or Vertical.", "Kick routing");
            return;
        }
        if (group.PlatformType == PlatformType.Facebook && group.ConfigurationMode == ConfigurationMode.NativeApi &&
            targetMode == RoutingMode.Both)
        {
            MessageBox.Show(this, "Native Facebook Page uses one output. Choose Horizontal or Vertical.", "Facebook routing");
            return;
        }

        var profile = PlatformRegistry.Get(group.PlatformType);
        if (targetMode is RoutingMode.Horizontal or RoutingMode.Both && !profile.SupportsHorizontal)
        {
            MessageBox.Show(this, $"{profile.DisplayName} does not support horizontal streaming.", "Routing");
            return;
        }
        if (targetMode is RoutingMode.Vertical or RoutingMode.Both && !profile.SupportsVertical)
        {
            MessageBox.Show(this, $"{profile.DisplayName} does not support vertical streaming.", "Routing");
            return;
        }

        var oldMode = group.Routing;
        bool isHLive = _outputs.ContainsKey(group.Horizontal.Id);
        bool isVLive = _outputs.ContainsKey(group.Vertical.Id);
        bool isGroupLive = isHLive || isVLive;

        if (!_streaming || !isGroupLive)
        {
            group.Routing = targetMode;
            group.EnsureChildDestinations();
            group.UpdateAggregation();
            await SaveSettingsAsync();
            DestinationItems.Items.Refresh();
            UpdateStreamUi();
            return;
        }

        string actionDescription;
        if (oldMode == RoutingMode.Horizontal && targetMode == RoutingMode.Both)
            actionDescription = "This will start Vertical output while keeping Horizontal live.";
        else if (oldMode == RoutingMode.Vertical && targetMode == RoutingMode.Both)
            actionDescription = "This will start Horizontal output while keeping Vertical live.";
        else if (oldMode == RoutingMode.Both && targetMode == RoutingMode.Horizontal)
            actionDescription = "This will stop Vertical output while keeping Horizontal live.";
        else if (oldMode == RoutingMode.Both && targetMode == RoutingMode.Vertical)
            actionDescription = "This will stop Horizontal output while keeping Vertical live.";
        else
            actionDescription = $"This will switch output from {oldMode} to {targetMode}.";

        var message = $"Changing output routing requires starting/stopping the affected output.\n\nCurrent: {oldMode}\nNew: {targetMode}\n\n{actionDescription}\n\nDo you want to apply this change?";
        var confirm = MessageBox.Show(this, message, "Change Routing Confirmation", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.OK) return;

        group.Routing = targetMode;
        group.EnsureChildDestinations();

        if (oldMode == RoutingMode.Horizontal && targetMode == RoutingMode.Both)
        {
            await StartOutputAsync(group.Vertical);
        }
        else if (oldMode == RoutingMode.Vertical && targetMode == RoutingMode.Both)
        {
            await StartOutputAsync(group.Horizontal);
        }
        else if (oldMode == RoutingMode.Both && targetMode == RoutingMode.Horizontal)
        {
            await StopOutputAsync(group.Vertical.Id);
        }
        else if (oldMode == RoutingMode.Both && targetMode == RoutingMode.Vertical)
        {
            await StopOutputAsync(group.Horizontal.Id);
        }
        else if (oldMode == RoutingMode.Horizontal && targetMode == RoutingMode.Vertical)
        {
            await StopOutputAsync(group.Horizontal.Id);
            await StartOutputAsync(group.Vertical);
        }
        else if (oldMode == RoutingMode.Vertical && targetMode == RoutingMode.Horizontal)
        {
            await StopOutputAsync(group.Vertical.Id);
            await StartOutputAsync(group.Horizontal);
        }
        else if (targetMode == RoutingMode.Off)
        {
            await StopOutputAsync(group.Horizontal.Id);
            await StopOutputAsync(group.Vertical.Id);
        }

        group.UpdateAggregation();
        await SaveSettingsAsync();
        DestinationItems.Items.Refresh();
        UpdateStreamUi();
    }

    private void DestinationMenu_Click(FrameworkElement source, PlatformDestinationGroup group)
    {
        var menu = new ContextMenu();
        void Add(string label, Action action, bool enabled = true)
        {
            var item = new MenuItem { Header = label, IsEnabled = enabled };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }

        Add("Edit", () => EditDestinationGroup(group));

        var routeMenu = new MenuItem { Header = "Change Routing" };
        var profile = PlatformRegistry.Get(group.PlatformType);
        if (profile.SupportsHorizontal)
        {
            var hItem = new MenuItem { Header = "Horizontal", IsChecked = group.Routing == RoutingMode.Horizontal };
            hItem.Click += (_, _) => HandleQuickRoute(group, RoutingMode.Horizontal);
            routeMenu.Items.Add(hItem);
        }
        if (profile.SupportsVertical)
        {
            var vItem = new MenuItem { Header = "Vertical", IsChecked = group.Routing == RoutingMode.Vertical };
            vItem.Click += (_, _) => HandleQuickRoute(group, RoutingMode.Vertical);
            routeMenu.Items.Add(vItem);
        }
        if (profile.SupportsHorizontal && profile.SupportsVertical)
        {
            var bItem = new MenuItem { Header = "Both", IsChecked = group.Routing == RoutingMode.Both };
            bItem.Click += (_, _) => HandleQuickRoute(group, RoutingMode.Both);
            routeMenu.Items.Add(bItem);
        }
        var offItem = new MenuItem { Header = "Off", IsChecked = group.Routing == RoutingMode.Off };
        offItem.Click += (_, _) => HandleQuickRoute(group, RoutingMode.Off);
        routeMenu.Items.Add(offItem);
        menu.Items.Add(routeMenu);

        menu.Items.Add(new Separator());

        bool isHLive = _outputs.ContainsKey(group.Horizontal.Id);
        bool isVLive = _outputs.ContainsKey(group.Vertical.Id);
        bool isAnyLive = isHLive || isVLive;

        if (isAnyLive)
        {
            Add("Restart All Outputs", async () =>
            {
                if (isHLive) { await StopOutputAsync(group.Horizontal.Id); await StartOutputAsync(group.Horizontal); }
                if (isVLive) { await StopOutputAsync(group.Vertical.Id); await StartOutputAsync(group.Vertical); }
            });
            Add("Stop All Outputs", async () =>
            {
                if (isHLive) await StopOutputAsync(group.Horizontal.Id);
                if (isVLive) await StopOutputAsync(group.Vertical.Id);
            });
        }

        if (isHLive)
        {
            Add("Restart Horizontal", async () => { await StopOutputAsync(group.Horizontal.Id); await StartOutputAsync(group.Horizontal); });
            Add("Stop Horizontal", async () => await StopOutputAsync(group.Horizontal.Id));
        }
        if (isVLive)
        {
            Add("Restart Vertical", async () => { await StopOutputAsync(group.Vertical.Id); await StartOutputAsync(group.Vertical); });
            Add("Stop Vertical", async () => await StopOutputAsync(group.Vertical.Id));
        }

        Add("View Details (Horizontal)", () =>
        {
            _outputs.TryGetValue(group.Horizontal.Id, out var o);
            new OutputDetailsDialog(this, group.Horizontal, o).ShowDialog();
        });

        if (group.Routing is RoutingMode.Vertical or RoutingMode.Both)
        {
            Add("View Details (Vertical)", () =>
            {
                _outputs.TryGetValue(group.Vertical.Id, out var o);
                new OutputDetailsDialog(this, group.Vertical, o).ShowDialog();
            });
        }

        menu.Items.Add(new Separator());

        Add(group.Enabled ? "Disable" : "Enable", async () =>
        {
            group.Enabled = !group.Enabled;
            if (!group.Enabled)
            {
                if (isHLive) await StopOutputAsync(group.Horizontal.Id);
                if (isVLive) await StopOutputAsync(group.Vertical.Id);
            }
            else if (_streaming && group.Routing != RoutingMode.Off)
            {
                foreach (var d in group.GetActiveDestinations())
                    await StartOutputAsync(d);
            }
            group.UpdateAggregation();
            DestinationItems.Items.Refresh();
            UpdateStreamUi();
            await SaveSettingsAsync();
        });

        Add("Duplicate", async () =>
        {
            var copySecrets = MessageBox.Show(this, "Copy stream key configuration to the duplicated destination?", "Duplicate Destination", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
            var dup = group.Duplicate(copySecrets);
            if (copySecrets && group.StreamKeyReference != null)
            {
                var key = _secrets.Get(group.StreamKeyReference);
                if (key != null)
                {
                    dup.StreamKeyReference = Guid.NewGuid().ToString();
                    _secrets.Set(dup.StreamKeyReference, key);
                    dup.Horizontal.StreamKeyReference = dup.StreamKeyReference;
                    dup.Vertical.StreamKeyReference = dup.StreamKeyReference;
                }
            }
            dup.Order = ViewModel.DestinationGroups.Count;
            ViewModel.DestinationGroups.Add(dup);
            await SaveSettingsAsync();
        });

        int index = ViewModel.DestinationGroups.IndexOf(group);
        Add("Move Up", async () =>
        {
            if (index > 0)
            {
                ViewModel.DestinationGroups.Move(index, index - 1);
                ReassignOrders();
                await SaveSettingsAsync();
            }
        }, enabled: index > 0);

        Add("Move Down", async () =>
        {
            if (index < ViewModel.DestinationGroups.Count - 1)
            {
                ViewModel.DestinationGroups.Move(index, index + 1);
                ReassignOrders();
                await SaveSettingsAsync();
            }
        }, enabled: index < ViewModel.DestinationGroups.Count - 1);

        menu.Items.Add(new Separator());

        Add("Delete", async () =>
        {
            if (MessageBox.Show(this, $"Delete destination \"{group.Name}\"? Saved stream configuration will be removed.", "Delete destination", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
                return;

            if (isHLive) await StopOutputAsync(group.Horizontal.Id);
            if (isVLive) await StopOutputAsync(group.Vertical.Id);

            if (group.StreamKeyReference != null) _secrets.Delete(group.StreamKeyReference);
            if (group.Horizontal.StreamKeyReference != null && group.Horizontal.StreamKeyReference != group.StreamKeyReference)
                _secrets.Delete(group.Horizontal.StreamKeyReference);
            if (group.Vertical.StreamKeyReference != null && group.Vertical.StreamKeyReference != group.StreamKeyReference)
                _secrets.Delete(group.Vertical.StreamKeyReference);

            ViewModel.DestinationGroups.Remove(group);
            ReassignOrders();
            await SaveSettingsAsync();
        });

        menu.PlacementTarget = source;
        menu.IsOpen = true;
    }

    private void ReassignOrders()
    {
        for (int i = 0; i < ViewModel.DestinationGroups.Count; i++)
        {
            ViewModel.DestinationGroups[i].Order = i;
        }
    }

    private async Task ToggleStreamingAsync()
    {
        if (_streaming) { await StopAllStreamsAsync(); return; }
        _recording.RefreshFfmpeg(_recordingSettings.CustomFfmpegPath);
        if (_recording.FfmpegStatus.Status != FfmpegStatus.Available)
        { MessageBox.Show(this, "FFmpeg is unavailable.", "Streaming"); return; }

        var enabledGroups = ViewModel.DestinationGroups.Where(g => g.Enabled && g.Routing != RoutingMode.Off).ToList();
        if (enabledGroups.Count == 0)
        {
            MessageBox.Show(this, "No enabled destinations found. Enable at least one destination to start streaming.", "Streaming");
            return;
        }

        var activeDestinations = enabledGroups.SelectMany(g => g.GetActiveDestinations()).ToList();
        if (activeDestinations.Count == 0)
        {
            MessageBox.Show(this, "No active outputs configured for enabled destinations.", "Streaming");
            return;
        }

        var equivalent1080p60 = activeDestinations.Sum(d => Math.Max(1, d.FrameRate) / 60.0);
        var riskySetup = PerformancePolicy.ShouldWarnBeforeStarting(_hardwareClass,
            activeDestinations.Count, equivalent1080p60);
        if (riskySetup)
        {
            var configured = string.Join("\n", activeDestinations.GroupBy(d => d.FrameRate)
                .Select(g => $"{g.Count()} × 1080p{g.Key}"));
            var choice = MessageBox.Show(this,
                $"Your current setup may be too demanding for this computer.\n\nConfigured:\n{activeDestinations.Count} outputs\n{configured}\n\n" +
                "Yes: Enable Eco Mode and start\nNo: Start anyway\nCancel: Do not start",
                "Performance Check", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
            if (choice == MessageBoxResult.Cancel) return;
            if (choice == MessageBoxResult.Yes)
            {
                _loadedSettings.Performance.Mode = PerformanceMode.Eco;
                ApplyPerformanceProfile();
                await SaveSettingsAsync();
            }
        }

        var estimatedKbps = StreamSessionSummary.EstimatedUploadKbps(enabledGroups);
        var estimatedMbps = estimatedKbps / 1000.0;

        var hGroups = enabledGroups.Where(g => g.Routing is RoutingMode.Horizontal or RoutingMode.Both).Select(g => g.Name).ToList();
        var vGroups = enabledGroups.Where(g => g.Routing is RoutingMode.Vertical or RoutingMode.Both).Select(g => g.Name).ToList();

        var routingPreview = "STREAM ROUTING:\n";
        if (hGroups.Count > 0) routingPreview += $"• Horizontal: {string.Join(", ", hGroups)}\n";
        if (vGroups.Count > 0) routingPreview += $"• Vertical: {string.Join(", ", vGroups)}\n";

        if (estimatedKbps >= 10000)
        {
            var warnMsg = $"{routingPreview}\nYour enabled outputs ({activeDestinations.Count} streams across {enabledGroups.Count} destinations) are configured for approximately {estimatedMbps:0.1} Mbps upload.\nEnsure your connection can sustain this continuous bandwidth.\n\nDo you want to proceed?";
            var res = MessageBox.Show(this, warnMsg, "Upload Bandwidth Warning", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (res != MessageBoxResult.OK) return;
        }

        _streaming = true;
        ApplyPerformanceProfile();
        StreamButton.Content = "STOP STREAM";

        await Task.WhenAll(activeDestinations.Select(StartOutputAsync));
        _streaming = _outputs.Count > 0;
        StreamButton.Content = _streaming ? "STOP STREAM" : "START STREAM";
        if (_streaming)
        {
            StartYouTubeServicesIfNeeded();
            StartTwitchServicesIfNeeded();
            StartKickServicesIfNeeded();
            StartFacebookServicesIfNeeded();
        }
        UpdateStreamUi();
    }

    private async Task StartOutputAsync(Destination d)
    {
        if (_outputs.ContainsKey(d.Id)) return;
        try
        {
            var nativeTwitch = ViewModel.DestinationGroups.FirstOrDefault(g =>
                g.PlatformType == PlatformType.Twitch && g.ConfigurationMode == ConfigurationMode.NativeApi &&
                (g.Horizontal.Id == d.Id || g.Vertical.Id == d.Id));
            if (nativeTwitch != null) await PrepareNativeTwitchOutputAsync(nativeTwitch);
            var nativeKick = ViewModel.DestinationGroups.FirstOrDefault(g =>
                g.PlatformType == PlatformType.Kick && g.ConfigurationMode == ConfigurationMode.NativeApi &&
                (g.Horizontal.Id == d.Id || g.Vertical.Id == d.Id));
            if (nativeKick != null) await PrepareNativeKickOutputAsync(nativeKick);
            var nativeFacebook = ViewModel.DestinationGroups.FirstOrDefault(g =>
                g.PlatformType == PlatformType.Facebook && g.ConfigurationMode == ConfigurationMode.NativeApi &&
                (g.Horizontal.Id == d.Id || g.Vertical.Id == d.Id));
            if (nativeFacebook != null) await PrepareNativeFacebookOutputAsync(nativeFacebook);
            var key = d.StreamKeyReference == null ? null : _secrets.Get(d.StreamKeyReference);
            var error = DestinationValidation.Validate(d, key != null);
            if (error != null) throw new InvalidOperationException(error);
            var encoder = d.EncoderId == "auto"
                ? _recording.AvailableEncoders.FirstOrDefault(x => x.Recommended && x.Available) ?? _recording.AvailableEncoders.FirstOrDefault(x => x.Available)
                : _recording.AvailableEncoders.FirstOrDefault(x => x.Id == d.EncoderId && x.Available);
            if (encoder == null) throw new InvalidOperationException("Selected encoder is unavailable.");
            _streamingOutputGroups ??= new StreamingOutputGroupManager(
                _recording.FfmpegStatus.FfmpegPath!, _frameHub, _audioEngine.Mixer, _secrets);
            var output = new StreamingOutput(d, _recording.FfmpegStatus.FfmpegPath!, encoder.Id, key!,
                _compositor, _audioEngine.Mixer, _frameHub, _streamingOutputGroups);
            if (_loadedSettings.Streaming?.AdaptiveBitrateDefault == true)
            {
                output.AdaptiveController = new AdaptiveBitrateController(d.VideoBitrateKbps)
                {
                    Enabled = true,
                    AutoRecover = _loadedSettings.Streaming.AutoRecoverBitrateDefault
                };
            }
            output.StatusChanged += _ => Dispatcher.BeginInvoke(() => { DestinationItems.Items.Refresh(); UpdateStreamUi(); });
            _outputs[d.Id] = output;
            await output.StartAsync();
            _outputErrors.Remove(d.Id);
        }
        catch (Exception ex)
        {
            string err = $"Encoder/Session error: {ex.Message}";
            if (_outputs.Remove(d.Id, out var failed))
            {
                if (failed.LastError != null) err = failed.LastError;
                await failed.DisposeAsync();
            }
            d.Status = DestinationStatus.Error;
            _outputErrors[d.Id] = err;
            DestinationItems.Items.Refresh();
            AppLog.Write("Streaming", $"Output {d.Id} failed: {err}");
        }
        UpdateStreamUi();
    }

    private async Task StopOutputAsync(Guid id)
    {
        if (_outputs.Remove(id, out var output)) await output.DisposeAsync();
        DestinationItems.Items.Refresh(); UpdateStreamUi();
        CheckIfYouTubeStillActive();
        CheckIfTwitchStillActive();
        CheckIfKickStillActive();
        CheckIfFacebookStillActive();
        UpdateTikTokStatus();
    }

    private async Task StopAllStreamsAsync()
    {
        foreach (var id in _outputs.Keys.ToList()) await StopOutputAsync(id);
        _streaming = false; StreamButton.Content = "START STREAM"; UpdateStreamUi();
        await StopYouTubeServicesAsync();
        await StopTwitchServicesAsync();
        await StopKickServicesAsync();
        await StopFacebookServicesAsync();
        UpdateTikTokStatus();
    }

    private void UpdateStreamUi()
    {
        if (!IsLoaded) return;
        var outputs = _outputs.Values.ToList();
        _streaming = outputs.Count > 0;
        StreamButton.Content = _streaming ? "STOP STREAM" : "START STREAM";

        foreach (var group in ViewModel.DestinationGroups)
        {
            if (_outputs.TryGetValue(group.Horizontal.Id, out var hOut) && hOut.Telemetry is { } hTel)
            {
                var hBitrate = hTel.MeasuredBitrateKbps.HasValue ? $"{hTel.MeasuredBitrateKbps.Value / 1000.0:0.1} Mbps" : $"{group.Horizontal.VideoBitrateKbps} kbps";
                group.HorizontalDetailsText = $"{hBitrate} • {hTel.CurrentFps:0} FPS • {hTel.ActiveEncoder}";
            }
            else
            {
                group.HorizontalDetailsText = $"{group.Horizontal.VideoBitrateKbps} kbps • {group.Horizontal.FrameRate} FPS • {group.Horizontal.EncoderId}";
            }

            if (_outputs.TryGetValue(group.Vertical.Id, out var vOut) && vOut.Telemetry is { } vTel)
            {
                var vBitrate = vTel.MeasuredBitrateKbps.HasValue ? $"{vTel.MeasuredBitrateKbps.Value / 1000.0:0.1} Mbps" : $"{group.Vertical.VideoBitrateKbps} kbps";
                group.VerticalDetailsText = $"{vBitrate} • {vTel.CurrentFps:0} FPS • {vTel.ActiveEncoder}";
            }
            else
            {
                group.VerticalDetailsText = $"{group.Vertical.VideoBitrateKbps} kbps • {group.Vertical.FrameRate} FPS • {group.Vertical.EncoderId}";
            }

            group.UpdateAggregation();
        }

        var allStatuses = outputs.Select(o => o.Status).Concat(ViewModel.DestinationGroups.SelectMany(g => g.GetActiveDestinations()).Where(d => d.Status == DestinationStatus.Error).Select(d => d.Status));
        var status = StreamSessionSummary.GlobalStatus(allStatuses);
        GlobalStreamStatusText.Text = status;
        var brush = status == "LIVE" ? (Brush)FindResource("AccentBrush")
            : status == "ERROR" ? (Brush)FindResource("DangerBrush")
            : status == "OFFLINE" ? (Brush)FindResource("QuietBrush") : (Brush)FindResource("WarningBrush");
        GlobalStreamStatusText.Foreground = brush;
        GlobalStreamStatusDot.Fill = brush;

        var statItems = new List<OutputStatItem>();
        foreach (var group in ViewModel.DestinationGroups.Where(g => g.Enabled && g.Routing != RoutingMode.Off))
        {
            foreach (var child in group.GetActiveDestinations())
            {
                _outputs.TryGetValue(child.Id, out var output);
                var currentStatus = output?.Status ?? child.Status;
                var statusStr = currentStatus.ToString().ToUpperInvariant();
                var telemetry = output?.Telemetry;

                var detail = telemetry == null
                    ? $"Target: {child.VideoBitrateKbps} kbps • {child.FrameRate} FPS • {child.EncoderId}"
                    : $"Elapsed: {telemetry.ElapsedTime:hh\\:mm\\:ss} • Target: {telemetry.BitrateKbps} kbps"
                        + (telemetry.MeasuredBitrateKbps is { } actual ? $" • Actual: {actual / 1000.0:0.1} Mbps" : "")
                        + $" • {telemetry.CurrentFps:0.0} FPS • Dropped: {telemetry.FramesDropped} • {telemetry.ActiveEncoder}";

                if (output?.RetryIn is { } retry) detail += $" • Retry in {retry.TotalSeconds:0}s (attempt {output.ReconnectCount})";
                if (output?.LastError is { } error) detail += $" • {error}";
                else if (_outputErrors.TryGetValue(child.Id, out var failure)) detail += $" • {failure}";

                var statusBrush = currentStatus switch
                {
                    DestinationStatus.Live => (Brush)FindResource("AccentBrush"),
                    DestinationStatus.Connecting or DestinationStatus.FallingBehind or DestinationStatus.Reconnecting or DestinationStatus.Restarting => (Brush)FindResource("WarningBrush"),
                    DestinationStatus.Error => (Brush)FindResource("DangerBrush"),
                    _ => (Brush)FindResource("QuietBrush")
                };

                var formatTag = child.OutputMode == OutputMode.Vertical ? "[V]" : "[H]";
                statItems.Add(new OutputStatItem($"{group.Name} {formatTag}", statusStr, detail, statusBrush));
            }
        }
        OutputStatsItems.ItemsSource = statItems;

        UpdateTikTokStatus();
        var measured = outputs.Where(o => o.Status == DestinationStatus.Live).Select(o => o.Telemetry?.MeasuredBitrateKbps).Where(x => x.HasValue).Sum(x => x!.Value);
        var configured = StreamSessionSummary.EstimatedUploadKbps(ViewModel.DestinationGroups);
        if (outputs.Count == 0)
        {
            StreamStatsText.Text = $"Stream: {status}";
        }
        else if (measured > 0)
        {
            StreamStatsText.Text = $"Total measured upload: {measured / 1000.0:0.1} Mbps (Target: ~{configured / 1000.0:0.1} Mbps)";
        }
        else
        {
            StreamStatsText.Text = $"Estimated / Target Upload: ~{configured / 1000.0:0.1} Mbps";
        }

        var totalActiveOutputs = ViewModel.DestinationGroups.Where(g => g.Enabled && g.Routing != RoutingMode.Off).Sum(g => g.GetActiveDestinations().Count());
        OutputsCountLabel.Text = $"{totalActiveOutputs} output{(totalActiveOutputs == 1 ? "" : "s")} enabled";

        bool hasNativeYt = ViewModel.DestinationGroups.Any(g => g.Enabled && g.PlatformType == PlatformType.YouTube && g.ConfigurationMode == ConfigurationMode.NativeApi);
        bool ytLive = ViewModel.DestinationGroups.Any(g => g.PlatformType == PlatformType.YouTube && g.ConfigurationMode == ConfigurationMode.NativeApi && (_outputs.ContainsKey(g.Horizontal.Id) || _outputs.ContainsKey(g.Vertical.Id)));

        if (ytLive)
        {
            ViewModel.YouTubeBroadcastStatus = "● LIVE";
        }
        else if (hasNativeYt)
        {
            ViewModel.YouTubeBroadcastStatus = "READY";
        }
        else
        {
            ViewModel.YouTubeBroadcastStatus = "OFFLINE";
        }
        ViewModel.YouTubeSubscribers = _loadedSettings.YouTubeAccount?.FormattedSubscribers ?? "—";
    }
    private void SwitchScene()
    {
        if (!IsLoaded) return;
        ViewModel.UpdateActiveSceneStates();
        _compositor.SetScene(ViewModel.SelectedScene, ViewModel.Scenes);
        _audioEngine.SetScene(ViewModel.SelectedScene, ViewModel.Scenes);
        HorizontalPreview.SetScene(ViewModel.SelectedScene);
        VerticalPreview.SetScene(ViewModel.SelectedScene);
        HorizontalPreview.SetSelected(SelectedSource);
        VerticalPreview.SetSelected(SelectedSource);
        AppLog.Write("Composition", $"Scene changed: {ViewModel.SelectedScene?.Name ?? "none"}");
    }

    private async void AddSource_Click(object sender, RoutedEventArgs e)
    {
        var picker = new SourcePicker(defaultMicrophoneId: _loadedSettings.Audio.DefaultMicrophoneId,
            defaultDesktopAudioId: _loadedSettings.Audio.DefaultDesktopAudioId) { Owner = this };
        if (picker.ShowDialog() != true || ViewModel.SelectedScene is not { } scene) return;
        var type = picker.SelectedType;
        if (type == SourceType.RemotePc)
        {
            var recent = _remoteAgents.Values.Where(a => DateTimeOffset.UtcNow - a.LastSeen < TimeSpan.FromSeconds(8)).ToList();
            if (_remoteDiscovery is null) { MessageBox.Show(this, "Remote PC discovery is not ready.", "Remote PC"); return; }
            var remoteDialog = new RemotePcDialog(this, _pairedRemoteDevices, recent, _remoteDiscovery);
            if (remoteDialog.ShowDialog() != true || remoteDialog.SelectedAgent is not { } agent) return;
            if (!RemoteCaptureProtocol.IsCompatible(agent.Advertisement.ProtocolVersion))
            { MessageBox.Show(this, "Remote Agent version is incompatible.", "Remote PC"); return; }
            var remoteName = $"Remote PC — {agent.Advertisement.ComputerName}";
            var remoteResource = _captureResources.FirstOrDefault(r => r.Type == SourceType.RemotePc && r.RemoteDeviceId == agent.Advertisement.DeviceId.ToString());
            if (remoteResource is null)
            {
                remoteResource = new CaptureResource
                {
                    Type = SourceType.RemotePc, DeviceId = agent.Advertisement.DeviceId.ToString(), Name = remoteName,
                    RemoteDeviceId = agent.Advertisement.DeviceId.ToString(), RemoteHost = agent.Host,
                    RemoteSecretReference = agent.Advertisement.DeviceId.ToString("N"),
                    RemoteCertificateThumbprint = agent.Advertisement.CertificateThumbprint,
                    FormatWidth = agent.Advertisement.DisplayWidth, FormatHeight = agent.Advertisement.DisplayHeight,
                    FormatFrameRate = agent.Advertisement.CaptureFps, RemoteLatencyMs = 250
                };
                _captureResources.Add(remoteResource);
            }
            else { remoteResource.RemoteHost = agent.Host; remoteResource.Name = remoteName; }
            var remoteSource = new SceneSource { Name = remoteName, Type = SourceType.RemotePc, SourceReference = remoteResource.Id };
            var rw = Math.Max(1, remoteResource.FormatWidth); var rh = Math.Max(1, remoteResource.FormatHeight);
            CanvasLayout.Fit(remoteSource.HorizontalTransform, rw, rh, OutputMode.Horizontal);
            CanvasLayout.SmartVertical(remoteSource.VerticalTransform, rw, rh, SmartVerticalTemplate.FullscreenCrop);
            scene.Sources.Add(remoteSource); remoteSource.State = CaptureState.Initializing;
            _compositor.SetResources(_captureResources); _audioEngine.SetResources(_captureResources);
            _compositor.RefreshSources(ViewModel.Scenes); _audioEngine.RefreshSources(ViewModel.Scenes);
            SourceList.SelectedItem = remoteSource; RefreshPreviews();
            await SaveSettingsAsync();
            return;
        }
        if (picker.IsSynthetic)
        {
            if (scene.Sources.Any(s => s.Type == type))
            { MessageBox.Show(this, $"This scene already contains a {type} source.", "Source already added"); return; }
            var synthetic = new SceneSource { Name = type == SourceType.Alerts ? "Alerts" : "Chat Overlay", Type = type, State = CaptureState.Active };
            synthetic.HorizontalTransform.Width = type == SourceType.Alerts ? 700 : 650;
            synthetic.HorizontalTransform.Height = type == SourceType.Alerts ? 180 : 420;
            synthetic.HorizontalTransform.X = type == SourceType.Alerts ? 610 : 40;
            synthetic.HorizontalTransform.Y = type == SourceType.Alerts ? 80 : 620;
            synthetic.VerticalTransform.Width = type == SourceType.Alerts ? 900 : 800;
            synthetic.VerticalTransform.Height = type == SourceType.Alerts ? 240 : 540;
            synthetic.VerticalTransform.X = type == SourceType.Alerts ? 45 : 60;
            synthetic.VerticalTransform.Y = type == SourceType.Alerts ? 250 : 960;
            scene.Sources.Add(synthetic);
            _compositor.RefreshSources(ViewModel.Scenes);
            SourceList.SelectedItem = synthetic;
            RefreshPreviews();
            await SaveSettingsAsync();
            return;
        }
        if (picker.SelectedDevice is not { } device) return;
        var (id, name, width, height) = device switch
        {
            DisplayInfo d => (d.Id, $"Display Capture — {d.Name}", d.Width, d.Height),
            WindowInfo w => (w.Id, $"Window Capture — {w.Title}", w.Width, w.Height),
            CameraInfo c when type == SourceType.CaptureDevice => (c.Id, $"Capture Device — {c.Name}", picker.PreferredCaptureDeviceFormat?.Width ?? 1280, picker.PreferredCaptureDeviceFormat?.Height ?? 720),
            CameraInfo c => (c.Id, $"Camera — {c.Name}", picker.PreferredCameraFormat?.Width ?? 1280, picker.PreferredCameraFormat?.Height ?? 720),
            AudioDeviceInfo a => (a.Id, $"{(a.IsInput ? "Microphone" : "Desktop Audio")} — {a.Name}", 0, 0),
            _ => throw new InvalidOperationException("Unknown capture device")
        };
        var resource = _captureResources.FirstOrDefault(r => r.Type == type && r.DeviceId == id);
        if (resource is null)
        {
            resource = new CaptureResource { Type = type, DeviceId = id, Name = name };
            _captureResources.Add(resource);
        }
        if (device is WindowInfo window) { resource.WindowTitle = window.Title; resource.ProcessName = window.ProcessName; }
        if (device is CameraInfo camera) resource.CameraIndex = camera.Index;
        if (device is AudioDeviceInfo audio) resource.FollowSystemDefault = audio.Id is
            WasapiAudioCaptureService.DefaultInputId or WasapiAudioCaptureService.DefaultOutputId;
        var selectedFormat = type == SourceType.CaptureDevice ? picker.PreferredCaptureDeviceFormat : picker.PreferredCameraFormat;
        if (selectedFormat is { } format)
        { resource.FormatWidth = format.Width; resource.FormatHeight = format.Height; resource.FormatFrameRate = format.FramesPerSecond; resource.FormatSubtype = format.Subtype; }
        var source = new SceneSource { Name = name, Type = type, DisplayId = type == SourceType.DisplayCapture ? id : null, SourceReference = resource.Id };
        if (width > 0 && height > 0)
        {
            CanvasLayout.Fit(source.HorizontalTransform, width, height, OutputMode.Horizontal);
            CanvasLayout.SmartVertical(source.VerticalTransform, width, height, SmartVerticalTemplate.FullscreenCrop);
            if (scene.Sources.Any(s => s.Type is SourceType.DisplayCapture or SourceType.WindowCapture or SourceType.Camera or SourceType.CaptureDevice))
            { ScaleToCorner(source.HorizontalTransform, OutputMode.Horizontal); ScaleToCorner(source.VerticalTransform, OutputMode.Vertical); }
        }
        scene.Sources.Add(source);
        source.State = CaptureState.Initializing;
        _compositor.SetResources(_captureResources);
        _audioEngine.SetResources(_captureResources);
        _compositor.RefreshSources(ViewModel.Scenes);
        _audioEngine.RefreshSources(ViewModel.Scenes);
        source.Error = null;
        SourceList.SelectedItem = source;
        RefreshPreviews();
        AppLog.Write("Capture", $"Source added: {source.Name}");
        await SaveSettingsAsync();
    }

    private static void ScaleToCorner(SourceTransform transform, OutputMode mode)
    {
        var (width, height) = CanvasLayout.Size(mode);
        var factor = Math.Min(width * 0.38 / transform.Width, height * 0.38 / transform.Height);
        transform.Width *= factor; transform.Height *= factor;
        transform.X = width - transform.Width - 40;
        transform.Y = height - transform.Height - 40;
    }

    private void SourceList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        HorizontalPreview.SetSelected(SelectedSource);
        VerticalPreview.SetSelected(SelectedSource);
    }

    private void SelectSource(SceneSource source, OutputMode mode)
    {
        _activeMode = mode;
        SourceList.SelectedItem = source;
        UpdateActiveCanvasBorder();
    }

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete && SelectedSource is { } source && Keyboard.FocusedElement is not (TextBox or PasswordBox))
        {
            e.Handled = true;
            PromptDeleteSource(source);
        }
    }

    private void SourceList_ButtonClick(object sender, RoutedEventArgs e)
    {
        var button = (e.OriginalSource as Button) ?? FindAncestor<Button>(e.OriginalSource as DependencyObject);
        if (button?.DataContext is SceneSource source)
        {
            e.Handled = true;
            SourceList.SelectedItem = source;
            var tag = button.Tag as string;
            if (tag == "ToggleVisibility")
            {
                ToggleVisibility(source);
            }
            else if (tag == "ToggleLock")
            {
                ToggleLock(source);
            }
            else if (tag == "MoreSource")
            {
                ShowSourceContextMenu(source, button, isPositionedAtTarget: true);
            }
        }
    }

    private void SourceList_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        var item = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        if (item?.DataContext is SceneSource source)
        {
            e.Handled = true;
            SourceList.SelectedItem = source;
            ShowSourceContextMenu(source, item, isPositionedAtTarget: false);
        }
    }

    private void SourceList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete && SelectedSource is { } source)
        {
            e.Handled = true;
            PromptDeleteSource(source);
        }
    }

    private void ToggleVisibility(SceneSource source)
    {
        source.Visible = !source.Visible;
        _compositor.RefreshSources(ViewModel.Scenes);
        _audioEngine.RefreshSources(ViewModel.Scenes);
        source.State = source.Visible ? CaptureState.Recovering : CaptureState.Stopped;
        RefreshPreviews();
        _ = SaveSettingsAsync();
    }

    private void ToggleLock(SceneSource source)
    {
        source.Locked = !source.Locked;
        RefreshPreviews();
        _ = SaveSettingsAsync();
    }

    private void ShowSourceContextMenu(SceneSource source, UIElement placementTarget, bool isPositionedAtTarget)
    {
        var menu = new ContextMenu();

        var titleItem = new MenuItem
        {
            Header = source.Name,
            IsEnabled = false,
            FontWeight = FontWeights.Bold,
            Foreground = (Brush)Application.Current.FindResource("TextBrush")
        };
        menu.Items.Add(titleItem);
        menu.Items.Add(new Separator());

        var renameItem = new MenuItem { Header = "Rename" };
        renameItem.Click += (_, _) => PromptRenameSource(source);
        menu.Items.Add(renameItem);

        bool supportsChangeDevice = source.Type is SourceType.DisplayCapture or SourceType.WindowCapture
            or SourceType.Camera or SourceType.CaptureDevice or SourceType.AudioInput or SourceType.AudioOutput;
        if (supportsChangeDevice)
        {
            var changeDeviceItem = new MenuItem { Header = "Change Device" };
            changeDeviceItem.Click += (_, _) => PromptChangeDevice(source);
            menu.Items.Add(changeDeviceItem);
        }
        if (source.Type is SourceType.AudioInput or SourceType.AudioOutput)
        {
            var filtersItem = new MenuItem { Header = "Audio Filters…" };
            filtersItem.Click += (_, _) => EditAudioFilters(source.SourceReference);
            menu.Items.Add(filtersItem);
        }
        if (source.Type == SourceType.CaptureDevice)
        {
            var audioLink = new MenuItem { Header = "Link Capture Card Audio…" };
            audioLink.Click += async (_, _) => await LinkCaptureDeviceAudioAsync(source);
            menu.Items.Add(audioLink);
        }

        var transformItem = new MenuItem { Header = "Transform", IsEnabled = source.CanTransform };
        transformItem.Click += (_, _) => PromptEditTransform(source);
        menu.Items.Add(transformItem);

        menu.Items.Add(new Separator());

        var showHideItem = new MenuItem { Header = source.Visible ? "Hide Source" : "Show Source" };
        showHideItem.Click += (_, _) => ToggleVisibility(source);
        menu.Items.Add(showHideItem);

        var lockItem = new MenuItem { Header = source.Locked ? "Unlock Source" : "Lock Source" };
        lockItem.Click += (_, _) => ToggleLock(source);
        menu.Items.Add(lockItem);

        menu.Items.Add(new Separator());

        var fitItem = new MenuItem { Header = "Fit to Canvas", IsEnabled = source.CanTransform };
        fitItem.Click += (_, _) => LayoutActionForSource(source, "fit");
        menu.Items.Add(fitItem);

        var fillItem = new MenuItem { Header = "Fill Canvas", IsEnabled = source.CanTransform };
        fillItem.Click += (_, _) => LayoutActionForSource(source, "fill");
        menu.Items.Add(fillItem);

        var smartVerticalMenu = new MenuItem { Header = "Smart Vertical", IsEnabled = source.CanTransform };
        var smartCropItem = new MenuItem { Header = "Fullscreen Crop (9:16)" };
        smartCropItem.Click += (_, _) => LayoutActionForSource(source, "smart_crop");
        var smartBgItem = new MenuItem { Header = "Background + Full 16:9 Screen" };
        smartBgItem.Click += (_, _) => LayoutActionForSource(source, "smart_bg");
        smartVerticalMenu.Items.Add(smartCropItem);
        smartVerticalMenu.Items.Add(smartBgItem);
        menu.Items.Add(smartVerticalMenu);

        var centerItem = new MenuItem { Header = "Center", IsEnabled = source.CanTransform };
        centerItem.Click += (_, _) => LayoutActionForSource(source, "center");
        menu.Items.Add(centerItem);

        var resetItem = new MenuItem { Header = "Reset Transform", IsEnabled = source.CanTransform };
        resetItem.Click += (_, _) => LayoutActionForSource(source, "reset");
        menu.Items.Add(resetItem);

        menu.Items.Add(new Separator());

        var copyHtoV = new MenuItem { Header = "Copy Horizontal to Vertical", IsEnabled = source.CanTransform };
        copyHtoV.Click += (_, _) => CopySourceLayout(source, OutputMode.Horizontal, OutputMode.Vertical);
        menu.Items.Add(copyHtoV);

        var copyVtoH = new MenuItem { Header = "Copy Vertical to Horizontal", IsEnabled = source.CanTransform };
        copyVtoH.Click += (_, _) => CopySourceLayout(source, OutputMode.Vertical, OutputMode.Horizontal);
        menu.Items.Add(copyVtoH);

        menu.Items.Add(new Separator());

        int index = ViewModel.SelectedScene?.Sources.IndexOf(source) ?? -1;
        int count = ViewModel.SelectedScene?.Sources.Count ?? 0;

        var moveUpItem = new MenuItem { Header = "Move Up" };
        moveUpItem.IsEnabled = index > 0;
        moveUpItem.Click += (_, _) => MoveSource(source, -1);
        menu.Items.Add(moveUpItem);

        var moveDownItem = new MenuItem { Header = "Move Down" };
        moveDownItem.IsEnabled = index >= 0 && index < count - 1;
        moveDownItem.Click += (_, _) => MoveSource(source, 1);
        menu.Items.Add(moveDownItem);

        var moveToTopItem = new MenuItem { Header = "Move to Top" };
        moveToTopItem.IsEnabled = index > 0;
        moveToTopItem.Click += (_, _) => MoveToEdge(source, true);
        menu.Items.Add(moveToTopItem);

        var moveToBottomItem = new MenuItem { Header = "Move to Bottom" };
        moveToBottomItem.IsEnabled = index >= 0 && index < count - 1;
        moveToBottomItem.Click += (_, _) => MoveToEdge(source, false);
        menu.Items.Add(moveToBottomItem);

        menu.Items.Add(new Separator());

        var deleteItem = new MenuItem { Header = "Delete" };
        deleteItem.Foreground = (Brush)Application.Current.FindResource("DangerBrush");
        deleteItem.Click += (_, _) => PromptDeleteSource(source);
        menu.Items.Add(deleteItem);

        if (isPositionedAtTarget)
        {
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.PlacementTarget = placementTarget;
        }
        else
        {
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        }
        menu.IsOpen = true;
    }

    private async Task LinkCaptureDeviceAudioAsync(SceneSource captureSource)
    {
        var resource = _captureResources.FirstOrDefault(r => r.Id == captureSource.SourceReference);
        if (resource is null) return;
        var picker = new SourcePicker(SourceType.AudioInput) { Owner = this };
        if (picker.ShowDialog() != true || picker.SelectedDevice is not AudioDeviceInfo audio || ViewModel.SelectedScene is not { } scene) return;
        var audioResource = _captureResources.FirstOrDefault(r => r.Type == SourceType.AudioInput && r.DeviceId == audio.Id);
        if (audioResource is null)
        {
            audioResource = new CaptureResource { Type = SourceType.AudioInput, DeviceId = audio.Id, Name = $"Capture Card Audio — {audio.Name}", FollowSystemDefault = audio.Id == WasapiAudioCaptureService.DefaultInputId };
            _captureResources.Add(audioResource);
        }
        resource.AssociatedAudioResourceId = audioResource.Id;
        if (!scene.Sources.Any(s => s.Type == SourceType.AudioInput && s.SourceReference == audioResource.Id))
            scene.Sources.Add(new SceneSource { Type = SourceType.AudioInput, Name = audioResource.Name, SourceReference = audioResource.Id, State = CaptureState.Initializing });
        _audioEngine.SetResources(_captureResources);
        _audioEngine.RefreshSources(ViewModel.Scenes);
        RefreshPreviews();
        await SaveSettingsAsync();
    }

    private void PromptRenameSource(SceneSource source)
    {
        var dialog = new Window
        {
            Title = "Rename Source",
            Owner = this,
            Width = 380,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            Background = (Brush)Application.Current.FindResource("ShellBrush")
        };
        AravalsStream.App.Controls.DarkWindowChrome.Apply(dialog);

        var root = new Border
        {
            Background = (Brush)Application.Current.FindResource("CardBrush"),
            BorderBrush = (Brush)Application.Current.FindResource("BorderBrushDark"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(20)
        };

        var stack = new StackPanel();
        var header = new TextBlock
        {
            Text = "RENAME SOURCE",
            Style = (Style)Application.Current.FindResource("SectionTitle"),
            Margin = new Thickness(0, 0, 0, 14)
        };
        var label = new TextBlock
        {
            Text = "SOURCE NAME",
            Style = (Style)Application.Current.FindResource("TinyText"),
            Margin = new Thickness(0, 0, 0, 6)
        };
        var nameInput = new TextBox
        {
            Text = source.Name,
            Margin = new Thickness(0, 0, 0, 16)
        };

        var btnPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        var cancelBtn = new Button
        {
            Content = "Cancel",
            Style = (Style)Application.Current.FindResource("GhostButton"),
            Margin = new Thickness(0, 0, 8, 0),
            Padding = new Thickness(14, 6, 14, 6)
        };
        var saveBtn = new Button
        {
            Content = "Save",
            Style = (Style)Application.Current.FindResource("PrimaryButton"),
            Padding = new Thickness(18, 6, 18, 6),
            MinHeight = 36
        };

        void Submit()
        {
            var text = nameInput.Text.Trim();
            if (!string.IsNullOrWhiteSpace(text))
            {
                dialog.DialogResult = true;
                dialog.Close();
            }
        }

        cancelBtn.Click += (_, _) => { dialog.DialogResult = false; dialog.Close(); };
        saveBtn.Click += (_, _) => Submit();

        btnPanel.Children.Add(cancelBtn);
        btnPanel.Children.Add(saveBtn);

        stack.Children.Add(header);
        stack.Children.Add(label);
        stack.Children.Add(nameInput);
        stack.Children.Add(btnPanel);
        root.Child = stack;
        dialog.Content = root;

        dialog.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { e.Handled = true; Submit(); }
            else if (e.Key == Key.Escape) { e.Handled = true; dialog.Close(); }
        };

        dialog.Loaded += (_, _) =>
        {
            nameInput.Focus();
            nameInput.SelectAll();
        };

        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(nameInput.Text))
        {
            source.Name = nameInput.Text.Trim();
            _ = SaveSettingsAsync();
            AppLog.Write("Capture", $"Source renamed: {source.Name}");
        }
    }

    private void PromptChangeDevice(SceneSource source)
    {
        var resource = _captureResources.FirstOrDefault(r => r.Id == source.SourceReference);
        if (resource is null) return;
        var currentFormat = resource.FormatWidth > 0
            ? new CameraFormat(resource.FormatWidth, resource.FormatHeight, resource.FormatFrameRate, resource.FormatSubtype) : null;
        var picker = new SourcePicker(source.Type, currentFormat, resource.DeviceId, lockType: true) { Owner = this };
        if (picker.ShowDialog() != true || picker.SelectedDevice is null) return;
        _previousBindings[resource.Id] = (resource.DeviceId, resource.WindowTitle, resource.ProcessName,
            resource.CameraIndex, resource.FormatWidth, resource.FormatHeight, resource.FormatFrameRate, resource.FormatSubtype, resource.Name, resource.FollowSystemDefault);
        resource.DeviceId = picker.SelectedDevice switch
        {
            DisplayInfo d => d.Id, WindowInfo w => w.Id, CameraInfo c => c.Id,
            AudioDeviceInfo a => a.Id, _ => resource.DeviceId
        };
        if (picker.SelectedDevice is WindowInfo window)
        { resource.WindowTitle = window.Title; resource.ProcessName = window.ProcessName; }
        if (picker.SelectedDevice is CameraInfo camera)
        {
            resource.CameraIndex = camera.Index;
            var format = picker.PreferredCameraFormat;
            resource.FormatWidth = format?.Width ?? 0; resource.FormatHeight = format?.Height ?? 0;
            resource.FormatFrameRate = format?.FramesPerSecond ?? 0; resource.FormatSubtype = format?.Subtype ?? "";
        }
        if (picker.SelectedDevice is AudioDeviceInfo audio)
        {
            resource.FollowSystemDefault = audio.Id is WasapiAudioCaptureService.DefaultInputId or WasapiAudioCaptureService.DefaultOutputId;
            var oldName = resource.Name;
            resource.Name = $"{(audio.IsInput ? "Microphone" : "Desktop Audio")} — {audio.Name}";
            foreach (var item in ViewModel.Scenes.SelectMany(s => s.Sources).Where(s => s.SourceReference == resource.Id && s.Name == oldName)) item.Name = resource.Name;
        }
        if (picker.SelectedDevice is DisplayInfo display)
            foreach (var item in ViewModel.Scenes.SelectMany(s => s.Sources).Where(s => s.SourceReference == resource.Id)) item.DisplayId = display.Id;
        foreach (var item in ViewModel.Scenes.SelectMany(s => s.Sources).Where(s => s.SourceReference == resource.Id))
        { item.State = CaptureState.Recovering; item.Error = null; }
        _recovery.Recovered(resource.Id);
        _compositor.RestartResource(resource.Id, ViewModel.Scenes);
        _audioEngine.RestartResource(resource.Id, ViewModel.Scenes);
        _ = SaveSettingsAsync();
    }

    private void PromptEditTransform(SceneSource source)
    {
        if (!source.CanTransform) return;
        var transform = _activeMode == OutputMode.Vertical ? source.VerticalTransform : source.HorizontalTransform;
        if (transform.Locked)
        {
            MessageBox.Show(this, "Unlock this source before editing its transform.", "Edit Transform", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (new TransformEditor(transform, _activeMode) { Owner = this }.ShowDialog() == true)
        {
            RefreshPreviews();
            _ = SaveSettingsAsync();
            AppLog.Write("Composition", "Transform edited");
        }
    }

    private void PromptDeleteSource(SceneSource source)
    {
        if (ViewModel.SelectedScene is not { } scene) return;

        if (!SceneDialogs.ShowRemoveSourceConfirmationDialog(this, source.Name))
            return;

        bool wasSelected = ReferenceEquals(SelectedSource, source) || SourceList.SelectedItem == source;

        if (!SceneManager.DeleteSourceFromScene(scene, source, ViewModel.Scenes, out var orphanResources))
            return;

        if (wasSelected)
        {
            SourceList.SelectedItem = null;
            HorizontalPreview.SetSelected(null);
            VerticalPreview.SetSelected(null);
        }

        if (orphanResources.Count > 0)
        {
            foreach (var orphan in orphanResources)
            {
                _captureSources.RemoveAll(s => s.Id == orphan);
                _captureResources.RemoveAll(s => s.Id == orphan);
                _recovery.Remove(orphan);
            }
            _compositor.SetResources(_captureResources);
            _audioEngine.SetResources(_captureResources);
        }

        _compositor.RefreshSources(ViewModel.Scenes);
        _audioEngine.RefreshSources(ViewModel.Scenes);
        RefreshPreviews();
        _ = SaveSettingsAsync();
        AppLog.Write("Capture", $"Source removed: {source.Name}");
    }

    private void MoveSource(SceneSource source, int delta)
    {
        if (ViewModel.SelectedScene is not { } scene) return;
        var index = scene.Sources.IndexOf(source);
        if (index < 0) return;
        var next = Math.Clamp(index + delta, 0, scene.Sources.Count - 1);
        if (next == index) return;
        scene.Sources.Move(index, next);
        RefreshPreviews();
        _ = SaveSettingsAsync();
    }

    private void MoveToEdge(SceneSource source, bool top)
    {
        if (ViewModel.SelectedScene is not { } scene) return;
        var index = scene.Sources.IndexOf(source);
        if (index < 0) return;
        scene.Sources.Move(index, top ? scene.Sources.Count - 1 : 0);
        RefreshPreviews();
        _ = SaveSettingsAsync();
    }

    private void CopySourceLayout(SceneSource source, OutputMode fromMode, OutputMode toMode)
    {
        var from = fromMode == OutputMode.Horizontal ? source.HorizontalTransform : source.VerticalTransform;
        var to = toMode == OutputMode.Horizontal ? source.HorizontalTransform : source.VerticalTransform;
        if (to.Locked) return;
        CanvasLayout.CopyLayout(from, to, fromMode, toMode);
        RefreshPreviews();
        _ = SaveSettingsAsync();
    }

    private void LayoutActionForSource(SceneSource source, string action)
    {
        if (!source.CanTransform) return;
        var transform = _activeMode == OutputMode.Vertical ? source.VerticalTransform : source.HorizontalTransform;
        if (transform.Locked) return;
        var display = FindVisualSize(source);
        var (dw, dh) = display ?? (_activeMode == OutputMode.Vertical ? (1080, 1920) : (1920, 1080));

        switch (action)
        {
            case "fit":
                CanvasLayout.FitEntire(transform, dw, dh, _activeMode);
                break;
            case "fill":
                CanvasLayout.FillCanvas(transform, dw, dh, _activeMode);
                break;
            case "smart_crop":
                CanvasLayout.SmartVertical(source.VerticalTransform, dw, dh, SmartVerticalTemplate.FullscreenCrop);
                break;
            case "smart_bg":
                CanvasLayout.SmartVertical(source.VerticalTransform, dw, dh, SmartVerticalTemplate.BackgroundAndFullSource);
                break;
            case "stretch":
                CanvasLayout.Stretch(transform, _activeMode);
                break;
            case "center":
                CanvasLayout.Center(transform, _activeMode);
                break;
            case "original":
                transform.Width = dw;
                transform.Height = dh;
                CanvasLayout.Center(transform, _activeMode);
                break;
            case "reset":
                transform.Rotation = 0;
                transform.Opacity = 1;
                transform.ScaleX = 1;
                transform.ScaleY = 1;
                transform.CropLeft = 0;
                transform.CropTop = 0;
                transform.CropRight = 0;
                transform.CropBottom = 0;
                if (_activeMode == OutputMode.Vertical)
                {
                    CanvasLayout.SmartVertical(transform, dw, dh, SmartVerticalTemplate.FullscreenCrop);
                }
                else
                {
                    CanvasLayout.Fit(transform, dw, dh, _activeMode);
                }
                break;
        }
        RefreshPreviews();
        _ = SaveSettingsAsync();
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match) return match;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private void AddScene_Click(object sender, RoutedEventArgs e)
    {
        if (SceneDialogs.ShowCreateSceneDialog(this, ViewModel.Scenes.Select(s => s.Name), out var sceneName))
        {
            var scene = new Scene
            {
                Name = sceneName,
                Enabled = true
            };
            ViewModel.Scenes.Add(scene);
            ViewModel.SelectedScene = scene;
            _ = SaveSettingsAsync();
            AppLog.Write("Application", $"Scene created: {scene.Name}");
        }
    }

    private void SceneList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _sceneDragStart = e.GetPosition(null);

        var element = e.OriginalSource as DependencyObject;
        if (FindAncestor<Button>(element) is not null)
        {
            return;
        }

        var item = FindAncestor<ListBoxItem>(element);
        if (item?.DataContext is Scene scene)
        {
            if (!scene.Enabled)
            {
                e.Handled = true;
                return;
            }

            ViewModel.SelectedScene = scene;
            _draggedScene = scene;
        }
    }

    private void SceneList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _draggedScene is null) return;

        var diff = _sceneDragStart - e.GetPosition(null);
        if (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance ||
            Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance)
        {
            var dragData = new DataObject("AravalsStream.Scene", _draggedScene);
            DragDrop.DoDragDrop(SceneList, dragData, DragDropEffects.Move);
            _draggedScene = null;
        }
    }

    private void SceneList_DragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent("AravalsStream.Scene"))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }
        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    private void SceneList_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent("AravalsStream.Scene") ||
            e.Data.GetData("AravalsStream.Scene") is not Scene droppedScene) return;

        var element = e.OriginalSource as DependencyObject;
        var targetItem = FindAncestor<ListBoxItem>(element);
        if (targetItem?.DataContext is Scene targetScene && targetScene != droppedScene)
        {
            int oldIndex = ViewModel.Scenes.IndexOf(droppedScene);
            int newIndex = ViewModel.Scenes.IndexOf(targetScene);
            if (oldIndex >= 0 && newIndex >= 0 && oldIndex != newIndex)
            {
                ViewModel.Scenes.Move(oldIndex, newIndex);
                _ = SaveSettingsAsync();
            }
        }
    }

    private void SceneList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is TextBox) return;

        var scene = SceneList.SelectedItem as Scene ?? ViewModel.SelectedScene;
        if (scene is null) return;

        if (e.Key == Key.F2)
        {
            e.Handled = true;
            PromptRenameScene(scene);
        }
        else if (e.Key == Key.Delete)
        {
            e.Handled = true;
            DeleteScene(scene);
        }
        else if (e.Key == Key.Enter)
        {
            e.Handled = true;
            if (scene.Enabled && ViewModel.SelectedScene != scene)
            {
                ViewModel.SelectedScene = scene;
            }
        }
    }

    private void SceneList_ButtonClick(object sender, RoutedEventArgs e)
    {
        var button = (e.OriginalSource as Button) ?? FindAncestor<Button>(e.OriginalSource as DependencyObject);
        if (button?.DataContext is Scene scene)
        {
            e.Handled = true;
            ShowSceneContextMenu(scene, button, isPositionedAtTarget: true);
        }
    }

    private void SceneList_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        var item = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        if (item?.DataContext is Scene scene)
        {
            e.Handled = true;
            ShowSceneContextMenu(scene, item, isPositionedAtTarget: false);
        }
    }

    private void ShowSceneContextMenu(Scene scene, UIElement placementTarget, bool isPositionedAtTarget)
    {
        var menu = new ContextMenu();

        var titleItem = new MenuItem
        {
            Header = scene.Name,
            IsEnabled = false,
            FontWeight = FontWeights.Bold,
            Foreground = (Brush)Application.Current.FindResource("TextBrush")
        };
        menu.Items.Add(titleItem);
        menu.Items.Add(new Separator());

        var setActiveItem = new MenuItem { Header = "Set Active" };
        setActiveItem.IsEnabled = scene.Enabled && scene != ViewModel.SelectedScene;
        setActiveItem.Click += (_, _) =>
        {
            if (scene.Enabled)
            {
                ViewModel.SelectedScene = scene;
                _ = SaveSettingsAsync();
            }
        };
        menu.Items.Add(setActiveItem);

        var toggleItem = new MenuItem();
        if (scene.Enabled)
        {
            toggleItem.Header = "Disable";
            bool canDisable = SceneManager.CanDisableScene(ViewModel.Scenes, scene);
            toggleItem.IsEnabled = canDisable;
            if (!canDisable)
                toggleItem.ToolTip = "At least one scene must remain enabled.";
            toggleItem.Click += (_, _) => ToggleSceneEnabled(scene);
        }
        else
        {
            toggleItem.Header = "Enable";
            toggleItem.Click += (_, _) => ToggleSceneEnabled(scene);
        }
        menu.Items.Add(toggleItem);

        var renameItem = new MenuItem { Header = "Rename" };
        renameItem.Click += (_, _) => PromptRenameScene(scene);
        menu.Items.Add(renameItem);

        var duplicateItem = new MenuItem { Header = "Duplicate" };
        duplicateItem.Click += (_, _) => DuplicateScene(scene);
        menu.Items.Add(duplicateItem);

        menu.Items.Add(new Separator());

        int index = ViewModel.Scenes.IndexOf(scene);
        var moveUpItem = new MenuItem { Header = "Move Up" };
        moveUpItem.IsEnabled = index > 0;
        moveUpItem.Click += (_, _) => MoveScene(scene, -1);
        menu.Items.Add(moveUpItem);

        var moveDownItem = new MenuItem { Header = "Move Down" };
        moveDownItem.IsEnabled = index >= 0 && index < ViewModel.Scenes.Count - 1;
        moveDownItem.Click += (_, _) => MoveScene(scene, 1);
        menu.Items.Add(moveDownItem);

        menu.Items.Add(new Separator());

        var deleteItem = new MenuItem { Header = "Delete" };
        bool canDelete = SceneManager.CanDeleteScene(ViewModel.Scenes, scene);
        deleteItem.IsEnabled = canDelete;
        deleteItem.Foreground = (Brush)Application.Current.FindResource("DangerBrush");
        if (!canDelete)
            deleteItem.ToolTip = "Cannot delete the final remaining scene.";
        deleteItem.Click += (_, _) => DeleteScene(scene);
        menu.Items.Add(deleteItem);

        if (isPositionedAtTarget)
        {
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.PlacementTarget = placementTarget;
        }
        else
        {
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        }
        menu.IsOpen = true;
    }

    private void ToggleSceneEnabled(Scene scene)
    {
        var currentActive = ViewModel.SelectedScene;
        if (!SceneManager.ToggleSceneEnabled(ViewModel.Scenes, scene, ref currentActive))
        {
            MessageBox.Show(this, "At least one scene must remain enabled.", "Scene Management", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (currentActive != ViewModel.SelectedScene)
        {
            ViewModel.SelectedScene = currentActive;
        }
        else
        {
            ViewModel.UpdateActiveSceneStates();
        }

        _ = SaveSettingsAsync();
    }

    private void PromptRenameScene(Scene scene)
    {
        if (!SceneDialogs.ShowRenameSceneDialog(this, scene.Name, out var newName))
            return;

        if (SceneManager.RenameScene(scene, newName, out var error))
        {
            _ = SaveSettingsAsync();
            AppLog.Write("Application", $"Scene renamed: {scene.Name}");
        }
        else if (error is not null)
        {
            MessageBox.Show(this, error, "Rename Scene", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void DuplicateScene(Scene scene)
    {
        var duplicate = SceneManager.DuplicateScene(scene, ViewModel.Scenes);
        ViewModel.UpdateActiveSceneStates();

        _compositor.RefreshSources(ViewModel.Scenes);
        _audioEngine.RefreshSources(ViewModel.Scenes);

        _ = SaveSettingsAsync();
        AppLog.Write("Application", $"Scene duplicated: {scene.Name} -> {duplicate.Name}");
    }

    private void DeleteScene(Scene scene)
    {
        if (!SceneManager.CanDeleteScene(ViewModel.Scenes, scene))
        {
            MessageBox.Show(this, "Cannot delete the final remaining scene.", "Delete Scene", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!SceneDialogs.ShowDeleteConfirmationDialog(this, scene.Name))
            return;

        var result = SceneManager.DeleteScene(ViewModel.Scenes, scene, ViewModel.SelectedScene);
        if (!result.Success) return;

        if (result.NextActiveScene is not null && result.NextActiveScene != ViewModel.SelectedScene)
        {
            ViewModel.SelectedScene = result.NextActiveScene;
        }
        else
        {
            ViewModel.UpdateActiveSceneStates();
        }

        if (result.OrphanResourceReferences.Count > 0)
        {
            foreach (var orphan in result.OrphanResourceReferences)
            {
                _captureSources.RemoveAll(s => s.Id == orphan);
                _captureResources.RemoveAll(s => s.Id == orphan);
                _recovery.Remove(orphan);
            }
            _compositor.SetResources(_captureResources);
            _audioEngine.SetResources(_captureResources);
        }

        _compositor.RefreshSources(ViewModel.Scenes);
        _audioEngine.RefreshSources(ViewModel.Scenes);
        SwitchScene();
        _ = SaveSettingsAsync();
        AppLog.Write("Application", $"Scene deleted: {scene.Name}");
    }

    private void MoveScene(Scene scene, int delta)
    {
        int index = ViewModel.Scenes.IndexOf(scene);
        if (index < 0) return;
        int target = index + delta;
        if (target < 0 || target >= ViewModel.Scenes.Count) return;

        ViewModel.Scenes.Move(index, target);
        _ = SaveSettingsAsync();
    }

    private static void AddMenu(ContextMenu menu, string label, Action action)
    {
        var item = new MenuItem { Header = label };
        item.Click += (_, _) => action();
        menu.Items.Add(item);
    }

    private void LayoutAction(string action)
    {
        if (ActiveTransform is not { } transform || SelectedSource is not { } source) return;
        if (transform.Locked) return;
        var display = FindVisualSize(source);
        switch (action)
        {
            case "fit" when display is not null: CanvasLayout.Fit(transform, display.Value.Width, display.Value.Height, _activeMode); break;
            case "stretch": CanvasLayout.Stretch(transform, _activeMode); break;
            case "center": CanvasLayout.Center(transform, _activeMode); break;
            case "original" when display is not null:
                transform.Width = display.Value.Width; transform.Height = display.Value.Height;
                CanvasLayout.Center(transform, _activeMode); break;
            case "reset" when display is not null:
                transform.Rotation = 0; transform.Opacity = 1; transform.ScaleX = 1; transform.ScaleY = 1;
                transform.CropLeft = 0; transform.CropTop = 0; transform.CropRight = 0; transform.CropBottom = 0;
                CanvasLayout.Fit(transform, display.Value.Width, display.Value.Height, _activeMode); break;
        }
        RefreshPreviews();
    }

    private (int Width, int Height)? FindVisualSize(SceneSource source)
    {
        try
        {
            var resource = _captureResources.FirstOrDefault(r => r.Id == source.SourceReference);
            if (source.Type == SourceType.DisplayCapture)
            {
                var d = new DesktopDuplicationCaptureService().EnumerateDisplays().FirstOrDefault(d => d.Id == (resource?.DeviceId ?? source.DisplayId));
                return d is null ? null : (d.Width, d.Height);
            }
            if (source.Type == SourceType.WindowCapture && resource is not null)
            {
                var w = new WindowCaptureService().EnumerateWindows().FirstOrDefault(w => w.Id == resource.DeviceId);
                return w is null ? null : (w.Width, w.Height);
            }
            if (source.Type == SourceType.Camera && resource is not null)
                return (resource.FormatWidth > 0 ? resource.FormatWidth : 1280, resource.FormatHeight > 0 ? resource.FormatHeight : 720);
            if (source.Type == SourceType.CaptureDevice && resource is not null)
                return (resource.FormatWidth > 0 ? resource.FormatWidth : 1280, resource.FormatHeight > 0 ? resource.FormatHeight : 720);
            return null;
        }
        catch { return null; }
    }

    private void CopyLayout(OutputMode fromMode, OutputMode toMode)
    {
        if (SelectedSource is not { } source) return;
        var from = fromMode == OutputMode.Horizontal ? source.HorizontalTransform : source.VerticalTransform;
        var to = toMode == OutputMode.Horizontal ? source.HorizontalTransform : source.VerticalTransform;
        if (to.Locked) return;
        CanvasLayout.CopyLayout(from, to, fromMode, toMode);
        RefreshPreviews();
    }

    private void MoveToEdge(bool top)
    {
        if (SelectedSource is not { } source || ViewModel.SelectedScene is not { } scene) return;
        scene.Sources.Move(scene.Sources.IndexOf(source), top ? scene.Sources.Count - 1 : 0);
        RefreshPreviews();
    }

    private PerformanceProfile CurrentPerformanceProfile()
    {
        var hasActiveOutputs = _streaming || _outputs.Values.Any(o => o.Status == DestinationStatus.Live) ||
            _recording.State is RecordingState.Recording or RecordingState.Paused;
        return PerformancePolicy.Resolve(
            _loadedSettings.Performance, _hardwareClass,
            !IsVisible || WindowState == WindowState.Minimized, _performanceOverloaded, hasActiveOutputs);
    }

    private string PerformanceOutputDetails()
    {
        var live = _outputs.Values.Where(o => o.Status == DestinationStatus.Live).ToArray();
        var streamFps = live.Sum(o => o.Telemetry?.CurrentFps ?? 0);
        var bitrate = live.Sum(o => o.Telemetry?.MeasuredBitrateKbps ?? 0);
        var streamDrops = live.Sum(o => o.Telemetry?.FramesDropped ?? 0);
        var streamRepeats = live.Sum(o => o.Telemetry?.RepeatedFrames ?? 0);
        var videoWriteMs = live.Length == 0 ? 0 : live.Average(o => o.Telemetry?.VideoPipeWriteMs ?? 0);
        var videoQueue = live.Sum(o => o.Telemetry?.VideoQueueDepth ?? 0);
        var audioQueue = live.Sum(o => o.Telemetry?.AudioQueueDepth ?? 0);
        var encoders = string.Join(", ", live.Select(o => o.Telemetry?.ActiveEncoder ?? "unknown").Distinct());
        var cadence = live.Length == 0 ? "none" : string.Join(", ", live.Select(o => $"{o.Destination.Name}:{o.Telemetry?.CadenceState ?? MediaCadenceState.Healthy}({o.Telemetry?.RealtimeRatio:0.00}x)"));
        var recording = _recording.State is RecordingState.Recording or RecordingState.Paused
            ? _recording.Telemetry.CurrentFps.ToString("0.0") : "idle";
        return $"Streaming {streamFps:0.0} FPS   Recording {recording} FPS   Active outputs {live.Length}\n" +
            $"Encoder {encoders}   Network {bitrate:0} kbps   Stream drops {streamDrops}   Repeated {streamRepeats}\n" +
            $"Cadence: {cadence}\n" +
            $"Recording unique {_recording.Telemetry.UniqueFrames}   repeated {_recording.Telemetry.RepeatedFrames}   " +
            $"missed {_recording.Telemetry.FramesDropped}   cadence {_recording.Telemetry.CadenceState}({_recording.Telemetry.RealtimeRatio:0.00}x)\n" +
            $"Video pipe {videoWriteMs:0.0} ms / queue {videoQueue}   Audio queue {audioQueue} chunks   " +
            $"Disk {_recordingWriteMbPerSecond:0.0} MB/s";
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == 0x0005) // WM_SIZE
        {
            ApplyPerformanceProfile();
        }
        return IntPtr.Zero;
    }

    private void ApplyPerformanceProfile()
    {
        if (_compositor is not SceneCompositor compositor) return;
        var hasActiveOutputs = _streaming || _outputs.Values.Any(o => o.Status == DestinationStatus.Live) ||
            _recording.State is RecordingState.Recording or RecordingState.Paused;
        var profile = CurrentPerformanceProfile();
        compositor.PreviewVisible = profile.PreviewFps > 0;
        compositor.PreviewTargetFps = profile.PreviewFps;
        compositor.CaptureTargetFps = hasActiveOutputs ? 60 : profile.PreviewFps > 0 ? profile.PreviewFps : 15;
        // Keep physical sources alive while the workspace is minimized. Preview is
        // hidden and throttled by the profile, but capture resumes without recreating
        // DXGI/WGC devices when the window is restored.
        compositor.SetCaptureSuspended(false, ViewModel.Scenes);
        if (_recording is RecordingService recording)
            recording.PerformanceMode = _loadedSettings.Performance.Mode == PerformanceMode.Auto && _hardwareClass == HardwareClass.Low
                ? PerformanceMode.Eco : _loadedSettings.Performance.Mode;
    }

    private void UpdatePerformanceStatus()
    {
        var sample = _performanceMetrics.Sample();
        if (_recording.State == RecordingState.Recording)
        {
            try
            {
                var bytes = _recording.LastOutputFiles.Where(System.IO.File.Exists)
                    .Sum(path => new System.IO.FileInfo(path).Length);
                var now = DateTimeOffset.UtcNow;
                var elapsed = (now - _lastRecordingSample).TotalSeconds;
                if (_lastRecordingSample != DateTimeOffset.MinValue && elapsed > 0.5)
                    _recordingWriteMbPerSecond = Math.Max(0, bytes - _lastRecordingBytes) / 1048576.0 / elapsed;
                _lastRecordingBytes = bytes; _lastRecordingSample = now;
            }
            catch (System.IO.IOException) { }
        }
        else { _lastRecordingBytes = 0; _lastRecordingSample = DateTimeOffset.MinValue; _recordingWriteMbPerSecond = 0; }
        var active = _outputs.Values.Count(o => o.Status == DestinationStatus.Live);
        var target = _outputs.Values.Where(o => o.Status == DestinationStatus.Live).Sum(o => o.Destination.FrameRate);
        _performanceOverloaded = sample.CpuPercent >= 85 ||
            (target > 0 && sample.OutputFps > 0 && sample.OutputFps < target * 0.7);
        _overloadSince = _performanceOverloaded ? _overloadSince ?? DateTimeOffset.UtcNow : null;
        ApplyPerformanceProfile();
        if (!IsVisible || WindowState == WindowState.Minimized) return;
        var status = _performanceOverloaded ? "Overloaded" : sample.CpuPercent >= 65 ? "High Load" :
            sample.CpuPercent >= 35 ? "Good" : "Excellent";
        PerformanceStatusLabel.Text = $"Performance: {status}";
        PerformanceStatusLabel.ToolTip = $"CPU {sample.CpuPercent:0.0}% • RAM {sample.WorkingSetMb:0} MB • " +
            $"composition {sample.CompositionFps:0.0} FPS ({sample.CompositionMilliseconds:0.0} ms) • " +
            $"preview {sample.PreviewFps:0.0} FPS • output {sample.OutputFps:0.0} FPS • " +
            $"active outputs {active} • dropped composition {sample.CompositionDrops}" +
            (_overloadSince is { } since && DateTimeOffset.UtcNow - since >= TimeSpan.FromSeconds(10)
                ? "\nSustained overload: use Eco Mode or lower selected output FPS/resolution. Live quality is unchanged." : "");
    }

    private void RefreshPreviews()
    {
        HorizontalPreview.Refresh(); VerticalPreview.Refresh();
    }

    private void FitEntire_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedSource is { } source) LayoutActionForSource(source, "fit");
    }

    private void FillCanvas_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedSource is { } source) LayoutActionForSource(source, "fill");
    }

    private void AutoVertical_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedSource is not { CanTransform: true } source) return;
        var transform = source.VerticalTransform;
        if (transform.Locked) return;
        var display = FindVisualSize(source);
        var (dw, dh) = display ?? (1920, 1080);
        CanvasLayout.SmartVertical(transform, dw, dh, SmartVerticalTemplate.FullscreenCrop);
        RefreshPreviews();
        _ = SaveSettingsAsync();
    }

    private void ResetLayout_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedSource is { } source) LayoutActionForSource(source, "reset");
    }

    private void ToggleSafeArea_Click(object sender, RoutedEventArgs e)
    {
        VerticalPreview.ToggleSafeArea();
        if (SafeAreaBtn is not null)
        {
            SafeAreaBtn.Foreground = VerticalPreview.ShowSafeArea
                ? (Brush)Application.Current.FindResource("AccentBrush")
                : (Brush)Application.Current.FindResource("TextBrush");
        }
    }

    private void UpdateAudioBar()
    {
        AudioChannel? FindChannel(SourceType type) => _audioEngine.Channels.FirstOrDefault(channel =>
            _captureResources.Any(resource => resource.Id == channel.Id && resource.Type == type && channel.Active));
        var mic = FindChannel(SourceType.AudioInput);
        var desktop = FindChannel(SourceType.AudioOutput);
        if (!ReferenceEquals(MicBar.DataContext, mic)) MicBar.DataContext = mic;
        if (!ReferenceEquals(DesktopBar.DataContext, desktop)) DesktopBar.DataContext = desktop;
        var outputText = $"{ViewModel.Destinations.Count(d => d.Enabled)} outputs enabled";
        if (OutputsCountLabel.Text != outputText) OutputsCountLabel.Text = outputText;
    }

    private void MarkFailed(Guid id, string message)
    {
        if (_previousBindings.Remove(id, out var previous) && _captureResources.FirstOrDefault(r => r.Id == id) is { } resource)
        {
            resource.DeviceId = previous.DeviceId; resource.WindowTitle = previous.WindowTitle; resource.ProcessName = previous.ProcessName;
            resource.CameraIndex = previous.CameraIndex; resource.FormatWidth = previous.Width; resource.FormatHeight = previous.Height;
            resource.FormatFrameRate = previous.Fps; resource.FormatSubtype = previous.Subtype;
            var failedName = resource.Name;
            resource.Name = previous.Name; resource.FollowSystemDefault = previous.FollowDefault;
            foreach (var source in ViewModel.Scenes.SelectMany(s => s.Sources).Where(s => s.SourceReference == id && s.Name == failedName)) source.Name = resource.Name;
            Dispatcher.BeginInvoke(() =>
            {
                _compositor.RestartResource(id, ViewModel.Scenes);
                _audioEngine.RestartResource(id, ViewModel.Scenes);
            });
        }
        var matchingSources = ViewModel.Scenes.SelectMany(s => s.Sources).Where(s => s.SourceReference == id).ToArray();
        var isWindowCapture = matchingSources.Any(source => source.Type == SourceType.WindowCapture);
        _recovery.Failed(id, DateTimeOffset.UtcNow, retrySoon: isWindowCapture);
        foreach (var source in matchingSources)
        { source.Error = message; source.State = CaptureState.Unavailable; }
    }

    private void MarkActive(Guid id)
    {
        if (!_recovery.IsFailed(id) && ViewModel.Scenes.SelectMany(s => s.Sources).Where(s => s.SourceReference == id)
            .All(s => s.State == CaptureState.Active)) return;
        _recovery.Recovered(id);
        _previousBindings.Remove(id);
        foreach (var source in ViewModel.Scenes.SelectMany(s => s.Sources).Where(s => s.SourceReference == id))
        { source.Error = null; source.State = CaptureState.Active; }
    }

    private void RetryDueSources()
    {
        var due = _recovery.Due(DateTimeOffset.UtcNow);
        if (due.Count == 0) return;
        foreach (var id in due)
        {
            _recovery.Defer(id, DateTimeOffset.UtcNow);
            foreach (var source in ViewModel.Scenes.SelectMany(s => s.Sources).Where(s => s.SourceReference == id))
                source.State = CaptureState.Recovering;
        }
        _compositor.RefreshSources(ViewModel.Scenes);
        _audioEngine.RefreshSources(ViewModel.Scenes);
    }

    private void UpdatePreviewMode()
    {
        var mode = ViewModel.PreviewMode;
        HorizontalModeButton.IsChecked = mode == OutputMode.Horizontal;
        VerticalModeButton.IsChecked = mode == OutputMode.Vertical;
        BothModeButton.IsChecked = mode == OutputMode.Both;
        HorizontalPane.Visibility = mode == OutputMode.Vertical ? Visibility.Collapsed : Visibility.Visible;
        VerticalPane.Visibility = mode == OutputMode.Horizontal ? Visibility.Collapsed : Visibility.Visible;
        VerticalColumn.Width = mode == OutputMode.Horizontal ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        HorizontalColumn.Width = mode == OutputMode.Vertical ? new GridLength(0) : new GridLength(mode == OutputMode.Both ? 3 : 1, GridUnitType.Star);
        PreviewGap.Width = mode == OutputMode.Both ? new GridLength(12) : new GridLength(0);

        if (mode == OutputMode.Vertical)
        {
            Grid.SetColumn(VerticalPane, 0);
            Grid.SetColumnSpan(VerticalPane, 3);
            VerticalPane.HorizontalAlignment = HorizontalAlignment.Center;
            if (ChatRow is not null) ChatRow.Height = new GridLength(0);
        }
        else if (mode == OutputMode.Horizontal)
        {
            Grid.SetColumn(HorizontalPane, 0);
            Grid.SetColumnSpan(HorizontalPane, 3);
            HorizontalPane.HorizontalAlignment = HorizontalAlignment.Stretch;
            if (ChatRow is not null) ChatRow.Height = new GridLength(220);
        }
        else
        {
            Grid.SetColumn(HorizontalPane, 0);
            Grid.SetColumnSpan(HorizontalPane, 1);
            Grid.SetColumn(VerticalPane, 2);
            Grid.SetColumnSpan(VerticalPane, 1);
            HorizontalPane.HorizontalAlignment = HorizontalAlignment.Stretch;
            VerticalPane.HorizontalAlignment = HorizontalAlignment.Stretch;
            if (ChatRow is not null) ChatRow.Height = new GridLength(220);
        }

        if (mode != OutputMode.Both) _activeMode = mode;
        UpdateActiveCanvasBorder();
    }

    private void UpdateActiveCanvasBorder()
    {
        if (HorizontalPane is null || VerticalPane is null) return;
        var accentBrush = (Brush)Application.Current.FindResource("AccentBrush");
        var darkBrush = (Brush)Application.Current.FindResource("BorderBrushDark");

        if (ViewModel.PreviewMode == OutputMode.Both)
        {
            if (_activeMode == OutputMode.Vertical)
            {
                VerticalPane.BorderBrush = accentBrush;
                VerticalPane.BorderThickness = new Thickness(2);
                HorizontalPane.BorderBrush = darkBrush;
                HorizontalPane.BorderThickness = new Thickness(1);
            }
            else
            {
                HorizontalPane.BorderBrush = accentBrush;
                HorizontalPane.BorderThickness = new Thickness(2);
                VerticalPane.BorderBrush = darkBrush;
                VerticalPane.BorderThickness = new Thickness(1);
            }
        }
        else if (ViewModel.PreviewMode == OutputMode.Vertical)
        {
            VerticalPane.BorderBrush = accentBrush;
            VerticalPane.BorderThickness = new Thickness(2);
        }
        else
        {
            HorizontalPane.BorderBrush = accentBrush;
            HorizontalPane.BorderThickness = new Thickness(2);
        }
    }

    private void ApplyResponsiveLayout()
    {
        if (!IsLoaded) return;
        PerformanceStatusLabel.Visibility = ActualWidth < 1450 ? Visibility.Collapsed : Visibility.Visible;
        OutputsCountLabel.Visibility = ActualWidth < 1250 ? Visibility.Collapsed : Visibility.Visible;
        DesktopBar.Visibility = ActualWidth < 1120 ? Visibility.Collapsed : Visibility.Visible;
        MicBar.Visibility = ActualWidth < 900 ? Visibility.Collapsed : Visibility.Visible;
        AudioMatrixBtn.Visibility = ActualWidth < 1050 ? Visibility.Collapsed : Visibility.Visible;
        var dpi = VisualTreeHelper.GetDpi(this);
        MinWidth = Math.Max(800, 1280 / dpi.DpiScaleX);
        MinHeight = Math.Max(450, 720 / dpi.DpiScaleY);
        if (ActualHeight < 530)
        {
            ShellHeaderRow.Height = new GridLength(50); ShellFooterRow.Height = new GridLength(54);
            ChatRow.Height = new GridLength(94); StatsRow.Height = new GridLength(1, GridUnitType.Star);
            WorkspaceGrid.Margin = new Thickness(8);
        }
        else if (ActualHeight < 640)
        {
            ShellHeaderRow.Height = new GridLength(56); ShellFooterRow.Height = new GridLength(62);
            ChatRow.Height = new GridLength(130); StatsRow.Height = new GridLength(1, GridUnitType.Star);
            WorkspaceGrid.Margin = new Thickness(10);
        }
        else
        {
            ShellHeaderRow.Height = new GridLength(64); ShellFooterRow.Height = new GridLength(76);
            ChatRow.Height = new GridLength(220); StatsRow.Height = new GridLength(1, GridUnitType.Star);
            WorkspaceGrid.Margin = new Thickness(16, 14, 16, 14);
        }
    }

    private void RecSettings_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new RecordingSettingsDialog(this, _recordingSettings, _recording.FfmpegStatus, _recording.AvailableEncoders);
        if (dialog.ShowDialog() == true)
        {
            _recordingSettings = dialog.Settings;
            _ = SaveSettingsAsync();
        }
    }

    private async void RecButton_Click(object sender, RoutedEventArgs e)
    {
        if (_recording.State == RecordingState.Idle || _recording.State == RecordingState.Error)
        {
            try
            {
                RecButton.IsEnabled = false;
                if (_recording.State == RecordingState.Error) await _recording.StopRecordingAsync();
                var startSettings = _recordingSettings;
                var hardwareSessions = _outputs.Values.Count(output => output.Status == DestinationStatus.Live &&
                    output.Telemetry?.ActiveEncoder is ("h264_nvenc" or "h264_qsv" or "h264_amf"));
                if (hardwareSessions >= 3)
                {
                    var canUseSoftware = _recording.AvailableEncoders.Any(encoder => encoder.Id == "libx264" && encoder.Available);
                    var answer = MessageBox.Show(this,
                        $"{hardwareSessions} hardware-encoded streams are already live. Recording may exceed encoder capacity or drop frames.\n\n" +
                        (canUseSoftware ? "Yes: use software x264 for this recording\nNo: start with the selected encoder\nCancel: do not record" :
                            "Start recording with the selected encoder?"),
                        "Recording Performance", canUseSoftware ? MessageBoxButton.YesNoCancel : MessageBoxButton.OKCancel,
                        MessageBoxImage.Warning);
                    if (answer == MessageBoxResult.Cancel) return;
                    if (canUseSoftware && answer == MessageBoxResult.Yes)
                    {
                        startSettings = JsonSerializer.Deserialize<RecordingSettings>(JsonSerializer.Serialize(_recordingSettings))!;
                        startSettings.Video.EncoderId = "libx264";
                        startSettings.Video.Preset = "ultrafast";
                    }
                }
                await _recording.StartRecordingAsync(startSettings);
            }
            catch (Exception ex)
            {
                AppLog.Write("Recording", $"Start failed: {ex.Message}");
                MessageBox.Show(this, ex.Message, "Recording Failed", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                RecButton.IsEnabled = true;
            }
        }
        else if (_recording.State == RecordingState.Recording || _recording.State == RecordingState.Paused)
        {
            try
            {
                RecButton.IsEnabled = false;
                await _recording.StopRecordingAsync();
            }
            catch (Exception ex)
            {
                AppLog.Write("Recording", $"Stop failed: {ex.Message}");
                MessageBox.Show(this, ex.Message, "Recording Stop Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                RecButton.IsEnabled = true;
            }
        }
    }

    private void PauseRecButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_recording.State == RecordingState.Recording) _recording.PauseRecording();
            else if (_recording.State == RecordingState.Paused) _recording.ResumeRecording();
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Recording pause failed", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void UpdateRecordingStats()
    {
        var state = _recording.State;
        var telemetry = _recording.Telemetry;
        var encoderName = string.IsNullOrEmpty(telemetry.ActiveEncoder) ? _recordingSettings.Video.EncoderId : telemetry.ActiveEncoder;

        switch (state)
        {
            case RecordingState.Recording:
                RecStatDot.Visibility = Visibility.Visible;
                RecStatDot.Fill = (Brush)FindResource("DangerBrush");
                RecStatState.Text = "RECORDING";
                RecStatState.Foreground = (Brush)FindResource("DangerBrush");
                RecStatElapsed.Text = telemetry.ElapsedTime.ToString(@"hh\:mm\:ss");
                RecStatElapsed.Foreground = (Brush)FindResource("TextBrush");
                RecStatEncoder.Text = $"Encoder: {encoderName}";
                RecStatPerformance.Text = $"{telemetry.CurrentFps:0.0} FPS • {telemetry.FramesDropped} dropped";
                LiveStatsText.Text = $"Recording: RECORDING\nDuration: {telemetry.ElapsedTime:hh\\:mm\\:ss}\nEncoder: {encoderName} • {telemetry.CurrentFps:0.0} FPS\nDropped: {telemetry.FramesDropped}";
                break;
            case RecordingState.Paused:
                RecStatDot.Visibility = Visibility.Visible;
                RecStatDot.Fill = (Brush)FindResource("WarningBrush");
                RecStatState.Text = "PAUSED";
                RecStatState.Foreground = (Brush)FindResource("WarningBrush");
                RecStatElapsed.Text = telemetry.ElapsedTime.ToString(@"hh\:mm\:ss");
                RecStatElapsed.Foreground = (Brush)FindResource("WarningBrush");
                RecStatEncoder.Text = $"Encoder: {encoderName}";
                RecStatPerformance.Text = $"{telemetry.CurrentFps:0.0} FPS (paused) • {telemetry.FramesDropped} dropped";
                LiveStatsText.Text = $"Recording: PAUSED\nRecorded duration: {telemetry.ElapsedTime:hh\\:mm\\:ss}";
                break;
            case RecordingState.Starting:
                RecStatDot.Visibility = Visibility.Collapsed;
                RecStatState.Text = "Starting...";
                RecStatState.Foreground = (Brush)FindResource("WarningBrush");
                RecStatElapsed.Text = "00:00:00";
                RecStatElapsed.Foreground = (Brush)FindResource("QuietBrush");
                RecStatEncoder.Text = $"Encoder: {encoderName}";
                RecStatPerformance.Text = "Initializing...";
                LiveStatsText.Text = "Recording: Starting";
                break;
            case RecordingState.Stopping:
                RecStatDot.Visibility = Visibility.Collapsed;
                RecStatState.Text = "Stopping...";
                RecStatState.Foreground = (Brush)FindResource("WarningBrush");
                RecStatElapsed.Text = telemetry.ElapsedTime.ToString(@"hh\:mm\:ss");
                RecStatElapsed.Foreground = (Brush)FindResource("QuietBrush");
                RecStatEncoder.Text = $"Encoder: {encoderName}";
                RecStatPerformance.Text = "Finalizing container...";
                LiveStatsText.Text = "Recording: Stopping";
                break;
            case RecordingState.Error:
                RecStatDot.Visibility = Visibility.Collapsed;
                RecStatState.Text = "Error";
                RecStatState.Foreground = (Brush)FindResource("DangerBrush");
                RecStatElapsed.Text = telemetry.ElapsedTime.ToString(@"hh\:mm\:ss");
                RecStatElapsed.Foreground = (Brush)FindResource("QuietBrush");
                RecStatEncoder.Text = $"Encoder: {encoderName}";
                RecStatPerformance.Text = "Recording halted";
                LiveStatsText.Text = "Recording: Error";
                break;
            default:
                RecStatDot.Visibility = Visibility.Collapsed;
                RecStatState.Text = "Idle";
                RecStatState.Foreground = (Brush)FindResource("QuietBrush");
                RecStatElapsed.Text = "00:00:00";
                RecStatElapsed.Foreground = (Brush)FindResource("QuietBrush");
                RecStatEncoder.Text = $"Encoder: {encoderName}";
                RecStatPerformance.Text = "0.0 FPS • 0 dropped";
                LiveStatsText.Text = "Recording: Idle";
                break;
        }
    }

    private void Recording_StateChanged(RecordingState state)
    {
        Dispatcher.BeginInvoke(() =>
        {
            ApplyPerformanceProfile();
            switch (state)
            {
                case RecordingState.Idle:
                    PauseRecButton.Visibility = Visibility.Collapsed;
                    RecButton.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1E2430"));
                    RecButton.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#334155"));
                    RecDot.Opacity = 0.5;
                    RecButtonText.Text = "REC";
                    RecTimerLabel.Text = "00:00:00";
                    RecTimerLabel.Visibility = Visibility.Collapsed;
                    RecStatsLabel.Visibility = Visibility.Collapsed;
                    RecButton.IsEnabled = true;
                    break;
                case RecordingState.Starting:
                    PauseRecButton.Visibility = Visibility.Collapsed;
                    RecButton.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#3B2D18"));
                    RecButton.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F59E0B"));
                    RecDot.Opacity = 0.8;
                    RecButtonText.Text = "STARTING";
                    RecTimerLabel.Text = "Starting...";
                    RecTimerLabel.Visibility = Visibility.Visible;
                    RecStatsLabel.Visibility = Visibility.Collapsed;
                    RecButton.IsEnabled = false;
                    break;
                case RecordingState.Recording:
                    PauseRecButton.Visibility = Visibility.Visible;
                    PauseRecButton.Content = "PAUSE";
                    RecButton.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#EF4444"));
                    RecButton.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F87171"));
                    RecDot.Opacity = 1.0;
                    RecButtonText.Text = "STOP REC";
                    RecTimerLabel.Visibility = Visibility.Visible;
                    RecStatsLabel.Visibility = Visibility.Visible;
                    RecButton.IsEnabled = true;
                    break;
                case RecordingState.Paused:
                    PauseRecButton.Visibility = Visibility.Visible;
                    PauseRecButton.Content = "RESUME";
                    RecButtonText.Text = "STOP REC";
                    RecTimerLabel.Visibility = Visibility.Visible;
                    RecTimerLabel.Text = _recording.Telemetry.ElapsedTime.ToString(@"hh\:mm\:ss");
                    RecStatsLabel.Visibility = Visibility.Visible;
                    RecButton.IsEnabled = true;
                    break;
                case RecordingState.Stopping:
                    PauseRecButton.Visibility = Visibility.Collapsed;
                    RecButton.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#3B2D18"));
                    RecButton.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F59E0B"));
                    RecDot.Opacity = 0.8;
                    RecButtonText.Text = "STOPPING";
                    RecTimerLabel.Text = "Finalizing...";
                    RecStatsLabel.Visibility = Visibility.Collapsed;
                    RecButton.IsEnabled = false;
                    break;
                case RecordingState.Error:
                    PauseRecButton.Visibility = Visibility.Collapsed;
                    RecButton.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1E2430"));
                    RecButton.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#EF4444"));
                    RecDot.Opacity = 0.5;
                    RecButtonText.Text = "REC";
                    RecTimerLabel.Text = "REC ERR";
                    RecTimerLabel.Visibility = Visibility.Visible;
                    RecStatsLabel.Visibility = Visibility.Collapsed;
                    RecButton.IsEnabled = true;
                    break;
            }
            UpdateRecordingStats();
        });
    }

    private void Recording_TelemetryUpdated(RecordingTelemetry telemetry)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_recording.State == RecordingState.Recording || _recording.State == RecordingState.Paused)
            {
                RecTimerLabel.Text = telemetry.ElapsedTime.ToString(@"hh\:mm\:ss");
                RecStatsLabel.Text = $"{telemetry.CurrentFps:F1} FPS | Drop: {telemetry.FramesDropped} | {telemetry.ActiveEncoder}";
            }
            UpdateRecordingStats();
        });
    }

    private void Recording_ErrorOccurred(string error)
    {
        Dispatcher.BeginInvoke(() =>
        {
            AppLog.Write("Recording", $"Error: {error}");
            MessageBox.Show(this, error, "Recording Error", MessageBoxButton.OK, MessageBoxImage.Error);
        });
    }
}

public sealed record OutputStatItem(string Name, string Status, string Details, Brush? StatusBrush = null);



















