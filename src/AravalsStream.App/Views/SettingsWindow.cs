using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Net.Http;
using System.Text.Json;
using AravalsStream.Capture.Audio;
using AravalsStream.Core.Audio;
using AravalsStream.Core.Models;
using AravalsStream.App.Services;
using AravalsStream.Capture.Display;
using AravalsStream.Core.Accounts;
using AravalsStream.Core.Interfaces;
using AravalsStream.Core.Recording.Models;
using AravalsStream.Core.Services;
using AravalsStream.Core.Settings;
using AravalsStream.Core.Versioning;
using AravalsStream.Core.YouTube;
using AravalsStream.Core.YouTube.Models;
using AravalsStream.Core.Twitch;
using AravalsStream.Core.Kick;
using AravalsStream.Core.Facebook;
using AravalsStream.Core.TikTok;
using AravalsStream.Core.RemoteCapture;

namespace AravalsStream.App.Views;

public sealed class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly PerformanceSettings _performanceDraft;
    private readonly RecordingSettings _recordingDraft;
    private readonly AudioSettings _audioDraft;
    private readonly StreamingSettings _streamingDraft;
    private readonly CanvasSettings _canvasDraft;
    private readonly List<HotkeyBinding> _hotkeyDraft;
    private readonly bool _outputsActive;
    private readonly Dictionary<string, (string Text, int Min, int Max, Action<int> Set)> _numbers = [];
    private readonly Dictionary<Guid, AudioDeviceInfo> _deviceChanges = [];
    private readonly Dictionary<Guid, AudioFilterSettings> _filterChanges = [];
    public IReadOnlyCollection<Guid> ChangedAudioResources => _deviceChanges.Keys.Concat(_filterChanges.Keys).Distinct().ToArray();
    private readonly FfmpegResolution _ffmpeg;
    private readonly IReadOnlyList<EncoderInfo> _encoders;
    private readonly DpapiSecretStorage? _secrets;
    private readonly ContentControl _contentArea;
    private readonly ListBox _categoryList;
    private readonly PerformanceMetricsService? _performanceMetrics;
    private readonly Func<string>? _performanceOutputDetails;
    private readonly PairedDeviceRegistry? _pairedDevices;
    private readonly Func<RelayDiagnosticSnapshot>? _relayDiagnostics;
    private readonly Func<string>? _relayStatus;
    private readonly Func<bool, Task>? _kickSubscriptionsAction;
    private System.Windows.Threading.DispatcherTimer? _performanceTimer;
    private System.Windows.Threading.DispatcherTimer? _relayTimer;

    public bool SettingsSaved { get; private set; }

    public SettingsWindow(
        Window owner,
        AppSettings settings,
        FfmpegResolution ffmpeg,
        IReadOnlyList<EncoderInfo> encoders,
        DpapiSecretStorage? secrets = null, PerformanceMetricsService? performanceMetrics = null,
        Func<string>? performanceOutputDetails = null, PairedDeviceRegistry? pairedDevices = null,
        Func<RelayDiagnosticSnapshot>? relayDiagnostics = null, Func<string>? relayStatus = null,
        Func<bool, Task>? kickSubscriptionsAction = null, bool outputsActive = false)
    {
        Owner = owner;
        _settings = settings;
        _outputsActive = outputsActive;
        _canvasDraft = settings.Canvas with { };
        _hotkeyDraft = JsonSerializer.Deserialize<List<HotkeyBinding>>(JsonSerializer.Serialize(settings.Hotkeys))!;
        foreach (var action in Enum.GetValues<HotkeyAction>())
            if (_hotkeyDraft.All(binding => binding.Action != action))
                _hotkeyDraft.Add(new HotkeyBinding { Action = action, DisplayName = System.Text.RegularExpressions.Regex.Replace(action.ToString(), "([a-z])([A-Z0-9])", "$1 $2"), Enabled = false });
        _recordingDraft = JsonSerializer.Deserialize<RecordingSettings>(JsonSerializer.Serialize(settings.Recording))!;
        _audioDraft = JsonSerializer.Deserialize<AudioSettings>(JsonSerializer.Serialize(settings.Audio))!;
        _audioDraft.DefaultMicrophoneId ??= settings.CaptureResources.FirstOrDefault(r => r.Type == SourceType.AudioInput)?.DeviceId;
        _audioDraft.DefaultDesktopAudioId ??= settings.CaptureResources.FirstOrDefault(r => r.Type == SourceType.AudioOutput)?.DeviceId;
        _audioDraft.MicrophoneFilters ??= (settings.CaptureResources.FirstOrDefault(r => r.Type == SourceType.AudioInput && r.DeviceId == _audioDraft.DefaultMicrophoneId)?.AudioFilters
            ?? new AudioFilterSettings()).Copy();
        _streamingDraft = JsonSerializer.Deserialize<StreamingSettings>(JsonSerializer.Serialize(settings.Streaming))!;
        _performanceDraft = new PerformanceSettings
        {
            Mode = settings.Performance.Mode,
            AutomaticProtection = settings.Performance.AutomaticProtection,
            AllowAutomaticStreamQualityReduction = settings.Performance.AllowAutomaticStreamQualityReduction,
            CustomPreviewFps = settings.Performance.CustomPreviewFps,
            CustomMeterRefreshHz = settings.Performance.CustomMeterRefreshHz
        };
        _ffmpeg = ffmpeg;
        _encoders = encoders;
        _secrets = secrets;
        _performanceMetrics = performanceMetrics;
        _performanceOutputDetails = performanceOutputDetails;
        _pairedDevices = pairedDevices;
        _relayDiagnostics = relayDiagnostics;
        _relayStatus = relayStatus;
        _kickSubscriptionsAction = kickSubscriptionsAction;

        Title = "Aravals Stream - Settings Center";
        Width = 840;
        Height = 680;
        MinWidth = 760;
        MinHeight = 540;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (Brush)FindResource("BackgroundBrush");

        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Left Navigation
        var sidebar = new Border
        {
            Background = (Brush)FindResource("PanelBrush"),
            BorderBrush = (Brush)FindResource("BorderBrushDark"),
            BorderThickness = new Thickness(0, 0, 1, 0)
        };
        _categoryList = new ListBox
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Margin = new Thickness(10, 15, 10, 15),
            Foreground = (Brush)FindResource("TextBrush")
        };
        _categoryList.Items.Add("General");
        _categoryList.Items.Add("Accounts");
        _categoryList.Items.Add("Video");
        _categoryList.Items.Add("Audio");
        _categoryList.Items.Add("Recording");
        _categoryList.Items.Add("Streaming");
        _categoryList.Items.Add("Hotkeys");
        _categoryList.Items.Add("Advanced");
        _categoryList.Items.Add("Remote Capture");
        _categoryList.Items.Add("Webhook Relay");
        _categoryList.Items.Add("About");

        _categoryList.SelectionChanged += (_, _) => ShowCategory(_categoryList.SelectedIndex);
        sidebar.Child = _categoryList;
        Grid.SetColumn(sidebar, 0);
        Grid.SetRow(sidebar, 0);
        root.Children.Add(sidebar);

        // Right Content Area
        _contentArea = new ContentControl { Margin = new Thickness(20) };
        Grid.SetColumn(_contentArea, 1);
        Grid.SetRow(_contentArea, 0);
        root.Children.Add(_contentArea);

        // Footer Buttons
        var footer = new Border
        {
            Background = (Brush)FindResource("PanelBrush"),
            BorderBrush = (Brush)FindResource("BorderBrushDark"),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(15, 10, 15, 10)
        };
        var footerPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };

        var cancelBtn = new Button { Content = "CANCEL", Width = 90, Height = 30, Margin = new Thickness(0, 0, 10, 0), Style = (Style)FindResource("GhostButton") };
        cancelBtn.Click += (_, _) => Close();

        var saveBtn = new Button { Content = "SAVE", Width = 100, Height = 30, Style = (Style)FindResource("AccentButton") };
        saveBtn.Click += (_, _) => SaveEditableSettings();

        footerPanel.Children.Add(cancelBtn);
        footerPanel.Children.Add(saveBtn);
        footer.Child = footerPanel;
        Grid.SetColumnSpan(footer, 2);
        Grid.SetRow(footer, 1);
        root.Children.Add(footer);

        Content = root;
        Closed += (_, _) => { _performanceTimer?.Stop(); _relayTimer?.Stop(); };
        _categoryList.SelectedIndex = 0;
    }

    private void ShowCategory(int index)
    {
        _performanceTimer?.Stop();
        _contentArea.Content = index switch
        {
            0 => BuildGeneralTab(),
            1 => BuildAccountsTab(),
            2 => BuildVideoTab(),
            3 => BuildAudioTab(),
            4 => BuildRecordingTab(),
            5 => BuildStreamingTab(),
            6 => BuildHotkeysTab(),
            7 => BuildAdvancedTab(),
            8 => BuildRemoteCaptureTab(),
            9 => BuildRelayTab(),
            10 => BuildAboutTab(),
            _ => new TextBlock { Text = "Select category", Foreground = (Brush)FindResource("QuietBrush") }
        };
    }

    private UIElement BuildRelayTab()
    {
        var panel = new StackPanel();
        AddSectionHeader(panel, "Optional Webhook Relay");
        panel.Children.Add(new TextBlock { Text = "Receives lightweight Kick and Facebook event notifications. Video, audio, and recordings stay on the normal media path. Ask the Relay administrator for a one-time code scoped to your connected channel IDs.", TextWrapping = TextWrapping.Wrap, Foreground = (Brush)FindResource("QuietBrush"), Margin = new Thickness(0, 0, 0, 12) });
        var enabled = new CheckBox { Content = "Enable relay connection", IsChecked = _settings.Relay.Enabled, Margin = new Thickness(0, 4, 0, 10) };
        panel.Children.Add(enabled);
        var url = new TextBox { Text = _settings.Relay.RelayUrl, Height = 32, Margin = new Thickness(0, 0, 0, 10), ToolTip = "https://relay.example or http://localhost:port in Development" };
        url.TextChanged += (_, _) => { _settings.Relay.RelayUrl = url.Text.Trim().TrimEnd('/'); SettingsSaved = true; };
        panel.Children.Add(new TextBlock { Text = "Relay URL", Foreground = (Brush)FindResource("TextBrush") }); panel.Children.Add(url);
        var id = new TextBlock { Text = string.IsNullOrEmpty(_settings.Relay.InstallationId) ? "Not enrolled" : "Enrolled • " + _settings.Relay.InstallationId[..Math.Min(8, _settings.Relay.InstallationId.Length)], Foreground = (Brush)FindResource("QuietBrush"), Margin = new Thickness(0, 8, 0, 8) };
        panel.Children.Add(id);
        var status = new TextBlock { Text = "Status: " + (_relayStatus?.Invoke() ?? "Disconnected"), Foreground = (Brush)FindResource("TextBrush"), Margin = new Thickness(0, 0, 0, 10) };
        panel.Children.Add(status);
        var manageKick = new CheckBox { Content = "Manage Kick webhook subscriptions through Relay", IsChecked = _settings.Relay.ManageKickSubscriptions, Margin = new Thickness(0, 0, 0, 8) };
        manageKick.Checked += (_, _) => { _settings.Relay.ManageKickSubscriptions = true; SettingsSaved = true; };
        manageKick.Unchecked += (_, _) => { _settings.Relay.ManageKickSubscriptions = false; SettingsSaved = true; };
        panel.Children.Add(manageKick);
        var kickStatus = new TextBlock { Text = KickSubscriptionText(), TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)FindResource("QuietBrush"), Margin = new Thickness(0, 0, 0, 10) };
        panel.Children.Add(kickStatus);
        var kickActions = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (label, remove) in new[] { ("Check Kick subscriptions", false), ("Delete managed subscriptions", true) })
        {
            var button = new Button { Content = label, Height = 32, Margin = new Thickness(0, 0, 8, 8), IsEnabled = _kickSubscriptionsAction is not null };
            button.Click += async (_, _) =>
            {
                button.IsEnabled = false;
                try { await _kickSubscriptionsAction!(remove); manageKick.IsChecked = !remove; SettingsSaved = true; }
                catch { MessageBox.Show(this, "Subscription action failed. Check Relay connectivity and Kick authorization.", "Kick subscriptions"); }
                finally { button.IsEnabled = true; kickStatus.Text = KickSubscriptionText(); }
            };
            kickActions.Children.Add(button);
        }
        panel.Children.Add(kickActions);
        _relayTimer?.Stop(); _relayTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _relayTimer.Tick += (_, _) => { status.Text = "Status: " + (_relayStatus?.Invoke() ?? "Disconnected"); kickStatus.Text = KickSubscriptionText(); };
        _relayTimer.Start();
        var enrollment = new PasswordBox { Height = 32, Margin = new Thickness(0, 0, 0, 10) };
        panel.Children.Add(new TextBlock { Text = "Relay enrollment code", Foreground = (Brush)FindResource("TextBrush") }); panel.Children.Add(enrollment);
        var channels = new List<string>();
        if (_settings.KickAccount?.Connected == true && !string.IsNullOrWhiteSpace(_settings.KickAccount.ChannelId)) channels.Add("kick:" + _settings.KickAccount.ChannelId);
        if (_settings.FacebookAccount?.Connected == true && !string.IsNullOrWhiteSpace(_settings.FacebookAccount.ChannelId)) channels.Add("facebook:" + _settings.FacebookAccount.ChannelId);
        var action = new Button { Content = string.IsNullOrEmpty(_settings.Relay.InstallationId) ? "Enroll and Connect" : "Update and Connect", Height = 34, Style = (Style)FindResource("AccentButton"), Margin = new Thickness(0, 6, 0, 0) };
        action.Click += async (_, _) =>
        {
            if (!Uri.TryCreate(url.Text.Trim(), UriKind.Absolute, out var relayUri) || relayUri.Scheme is not ("https" or "http") ||
                (relayUri.Scheme == "http" && !(relayUri.IsLoopback || relayUri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase))) ||
                !string.IsNullOrEmpty(relayUri.UserInfo) || !string.IsNullOrEmpty(relayUri.Query) || !string.IsNullOrEmpty(relayUri.Fragment))
            { MessageBox.Show(this, "Use HTTPS, or localhost HTTP for development.", "Relay URL", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            var tokenReference = _settings.Relay.RefreshTokenReference;
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                string refresh;
                if (string.IsNullOrEmpty(_settings.Relay.InstallationId))
                {
                    var body = System.Text.Json.JsonSerializer.Serialize(new { code = enrollment.Password, channels = channels.ToArray() });
                    using var response = await client.PostAsync(url.Text.TrimEnd('/') + "/api/v1/enroll", new StringContent(body, System.Text.Encoding.UTF8, "application/json"));
                    response.EnsureSuccessStatusCode();
                    using var doc = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                    _settings.Relay.InstallationId = doc.RootElement.GetProperty("installationId").GetString()!;
                    refresh = doc.RootElement.GetProperty("refreshToken").GetString()!;
                    tokenReference = Guid.NewGuid().ToString("N");
                    _secrets?.Set(tokenReference, refresh);
                    _settings.Relay.RefreshTokenReference = tokenReference;
                }
                else if (_secrets?.Get(tokenReference) is { } saved) refresh = saved;
                else throw new InvalidOperationException("The saved relay credential is unavailable. Disconnect and enroll again.");
                _settings.Relay.RelayUrl = url.Text.Trim().TrimEnd('/'); _settings.Relay.Enabled = true; _settings.Relay.Channels = channels.ToArray();
                enabled.IsChecked = true; enrollment.Clear(); id.Text = "Enrolled • " + _settings.Relay.InstallationId[..Math.Min(8, _settings.Relay.InstallationId.Length)]; SettingsSaved = true;
                MessageBox.Show(this, $"Relay enrollment succeeded. The Desktop will connect after you save settings. Registered channels: {channels.Count}.", "Webhook Relay", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex) { MessageBox.Show(this, $"Relay enrollment failed ({ex.GetType().Name}). Check the URL and enrollment code.", "Webhook Relay", MessageBoxButton.OK, MessageBoxImage.Warning); }
        };
        panel.Children.Add(action);
        var disconnect = new Button { Content = "Disconnect", Height = 32, Margin = new Thickness(0, 8, 0, 0), IsEnabled = !string.IsNullOrEmpty(_settings.Relay.InstallationId) };
        disconnect.Click += async (_, _) =>
        {
            try
            {
                var reference = _settings.Relay.RefreshTokenReference; var secret = _secrets?.Get(reference);
                if (secret is not null && Uri.TryCreate(url.Text, UriKind.Absolute, out var disconnectUri) &&
                    (disconnectUri.Scheme == "https" || (disconnectUri.Scheme == "http" && (disconnectUri.IsLoopback || disconnectUri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)))) )
                {
                    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                    var body = System.Text.Json.JsonSerializer.Serialize(new { installationId = _settings.Relay.InstallationId, refreshToken = secret });
                    await client.PostAsync(url.Text.TrimEnd('/') + "/api/v1/session/revoke", new StringContent(body, System.Text.Encoding.UTF8, "application/json"));
                }
            }
            catch { }
            if (!string.IsNullOrEmpty(_settings.Relay.RefreshTokenReference)) _secrets?.Delete(_settings.Relay.RefreshTokenReference);
            _settings.Relay = new RelaySettings(); SettingsSaved = true; enabled.IsChecked = false; id.Text = "Disconnected";
        };
        panel.Children.Add(disconnect);
        enabled.Checked += (_, _) => { _settings.Relay.Enabled = true; SettingsSaved = true; };
        enabled.Unchecked += (_, _) => { _settings.Relay.Enabled = false; SettingsSaved = true; };
        return panel;
    }

    private string KickSubscriptionText() => $"Kick: {_settings.Relay.KickSubscriptionState}\nLast successful provider check: {_settings.Relay.KickSubscriptionLastCheckedUtc?.ToLocalTime().ToString("g") ?? "never"}" +
        (string.IsNullOrEmpty(_settings.Relay.KickSubscriptionError) ? "" : "\n" + _settings.Relay.KickSubscriptionError);

    private UIElement BuildRemoteCaptureTab()
    {
        var panel = new StackPanel();
        AddSectionHeader(panel, "Paired Remote Devices");
        panel.Children.Add(new TextBlock { Text = "Pairing secrets are protected with Windows DPAPI and are removed when a device is forgotten.", TextWrapping = TextWrapping.Wrap, Foreground = (Brush)FindResource("QuietBrush"), Margin = new Thickness(0, 0, 0, 12) });
        var list = new ListBox { MinHeight = 260 };
        panel.Children.Add(list);
        var forget = new Button { Content = "Forget selected device", HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0), IsEnabled = false };
        panel.Children.Add(forget);
        PairedRemoteDevice? selected = null;
        list.SelectionChanged += (_, _) => { selected = list.SelectedItem as PairedRemoteDevice; forget.IsEnabled = selected is not null; };
        forget.Click += async (_, _) =>
        {
            if (_pairedDevices is null || selected is null) return;
            var remoteRevoked = await _pairedDevices.ForgetAsync(selected.DeviceId);
            list.Items.Remove(selected); selected = null; forget.IsEnabled = false;
            MessageBox.Show(this,
                remoteRevoked
                    ? "The device was forgotten and the Agent revoked its old credential. Pair again to reconnect."
                    : "This Desktop removed its saved credential. The Agent did not confirm remote revocation; if it was offline or needs an update, old copies of that credential may remain valid there.",
                "Remote device forgotten", MessageBoxButton.OK,
                remoteRevoked ? MessageBoxImage.Information : MessageBoxImage.Warning);
        };
        if (_pairedDevices is null)
            list.Items.Add("Remote device registry is unavailable.");
        else
            _ = LoadPairedDevicesAsync(list);
        return panel;
    }

    private async Task LoadPairedDevicesAsync(ListBox list)
    {
        try
        {
            var devices = await _pairedDevices!.ListAsync();
            await Dispatcher.InvokeAsync(() =>
            {
                list.Items.Clear();
                foreach (var device in devices.OrderBy(d => d.DisplayName)) list.Items.Add(device);
                if (devices.Count == 0) list.Items.Add("No paired Remote Agents.");
            });
        }
        catch (Exception ex) { await Dispatcher.InvokeAsync(() => list.Items.Add($"Could not load paired devices: {ex.Message}")); }
    }

    private UIElement BuildGeneralTab()
    {
        var panel = new StackPanel();
        AddSectionHeader(panel, "General Settings");

        var chkExit = new CheckBox
        {
            Content = "Confirm before exiting while live stream or recording is active",
            IsChecked = _settings.General.ConfirmExitWhileLive,
            Margin = new Thickness(0, 10, 0, 10)
        };
        chkExit.Checked += (_, _) => _settings.General.ConfirmExitWhileLive = true;
        chkExit.Unchecked += (_, _) => _settings.General.ConfirmExitWhileLive = false;
        panel.Children.Add(chkExit);

        var chkTray = new CheckBox
        {
            Content = "Minimize to system tray on window close or minimize",
            IsChecked = _settings.General.MinimizeToTray,
            Margin = new Thickness(0, 0, 0, 10)
        };
        chkTray.Checked += (_, _) => _settings.General.MinimizeToTray = true;
        chkTray.Unchecked += (_, _) => _settings.General.MinimizeToTray = false;
        panel.Children.Add(chkTray);

        var chkUpdates = new CheckBox
        {
            Content = "Automatically check for updates",
            IsChecked = _settings.General.CheckForUpdates,
            Margin = new Thickness(0, 0, 0, 15)
        };
        chkUpdates.Checked += (_, _) => _settings.General.CheckForUpdates = true;
        chkUpdates.Unchecked += (_, _) => _settings.General.CheckForUpdates = false;
        panel.Children.Add(chkUpdates);

        return panel;
    }

    private UIElement BuildAccountsTab()
    {
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var panel = new StackPanel();
        scroll.Content = panel;

        AddSectionHeader(panel, "Connected Accounts");

        // YouTube Account Card
        var ytBorder = new Border
        {
            Background = (Brush)FindResource("CardBrush"),
            BorderBrush = (Brush)FindResource("BorderBrushDark"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(14),
            Margin = new Thickness(0, 0, 0, 16)
        };
        var ytContent = new StackPanel();

        var ytHeader = new TextBlock
        {
            Text = "YOUTUBE",
            FontWeight = FontWeights.Bold,
            FontSize = 13,
            Foreground = (Brush)FindResource("TextBrush"),
            Margin = new Thickness(0, 0, 0, 8)
        };
        ytContent.Children.Add(ytHeader);

        var isConnected = _settings.YouTubeAccount?.Connected == true;

        if (isConnected && _settings.YouTubeAccount != null)
        {
            var infoGrid = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            infoGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            infoGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var detailsPanel = new StackPanel();
            var nameBlock = new TextBlock
            {
                Text = _settings.YouTubeAccount.ChannelTitle,
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("TextBrush")
            };
            var idBlock = new TextBlock
            {
                Text = $"Channel ID: {_settings.YouTubeAccount.ChannelId}",
                FontSize = 11,
                Foreground = (Brush)FindResource("QuietBrush"),
                Margin = new Thickness(0, 2, 0, 2)
            };
            var subsBlock = new TextBlock
            {
                Text = $"Subscribers: {_settings.YouTubeAccount.FormattedSubscribers}",
                FontSize = 11,
                Foreground = (Brush)FindResource("QuietBrush")
            };
            detailsPanel.Children.Add(nameBlock);
            detailsPanel.Children.Add(idBlock);
            detailsPanel.Children.Add(subsBlock);
            infoGrid.Children.Add(detailsPanel);

            var statusBlock = new TextBlock
            {
                Text = "● Connected",
                Foreground = (Brush)FindResource("AccentBrush"),
                FontWeight = FontWeights.SemiBold,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Top
            };
            Grid.SetColumn(statusBlock, 1);
            infoGrid.Children.Add(statusBlock);

            ytContent.Children.Add(infoGrid);

            var btnPanel = new StackPanel { Orientation = Orientation.Horizontal };
            var reconnectBtn = new Button
            {
                Content = "RECONNECT",
                Style = (Style)FindResource("GhostButton"),
                Height = 28,
                Padding = new Thickness(12, 0, 12, 0),
                Margin = new Thickness(0, 0, 10, 0)
            };
            var disconnectBtn = new Button
            {
                Content = "DISCONNECT",
                Style = (Style)FindResource("GhostButton"),
                Height = 28,
                Padding = new Thickness(12, 0, 12, 0)
            };

            reconnectBtn.Click += async (_, _) => await ConnectYouTubeAccountAsync();
            disconnectBtn.Click += async (_, _) => await DisconnectYouTubeAccountAsync();

            btnPanel.Children.Add(reconnectBtn);
            btnPanel.Children.Add(disconnectBtn);
            ytContent.Children.Add(btnPanel);
        }
        else
        {
            var notConnBlock = new TextBlock
            {
                Text = "Not connected",
                Foreground = (Brush)FindResource("QuietBrush"),
                FontSize = 12,
                Margin = new Thickness(0, 0, 0, 12)
            };
            ytContent.Children.Add(notConnBlock);

            var connectBtn = new Button
            {
                Content = "CONNECT YOUTUBE",
                Style = (Style)FindResource("AccentButton"),
                Height = 32,
                Width = 160,
                HorizontalAlignment = HorizontalAlignment.Left
            };
            connectBtn.Click += async (_, _) => await ConnectYouTubeAccountAsync();
            ytContent.Children.Add(connectBtn);
        }

        ytBorder.Child = ytContent;
        panel.Children.Add(ytBorder);

        var twitchCard = new Border
        {
            Background = (Brush)FindResource("CardBrush"), BorderBrush = (Brush)FindResource("BorderBrushDark"),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
            Padding = new Thickness(14), Margin = new Thickness(0, 0, 0, 16)
        };
        var twitchContent = new StackPanel();
        twitchContent.Children.Add(new TextBlock { Text = "TWITCH", FontWeight = FontWeights.Bold,
            Foreground = (Brush)FindResource("TextBrush"), Margin = new Thickness(0, 0, 0, 8) });
        var twitch = _settings.TwitchAccount;
        twitchContent.Children.Add(new TextBlock { Text = twitch?.Connected == true
                ? $"{twitch.DisplayName}  @{twitch.Login}  •  {twitch.State}"
                : "Not connected", Foreground = (Brush)FindResource("QuietBrush"), Margin = new Thickness(0, 0, 0, 10) });
        AddLabel(twitchContent, "Twitch application Client ID:");
        var twitchClientId = new TextBox { Text = _settings.TwitchOAuth.ClientId, Height = 28,
            Background = (Brush)FindResource("PanelBrush"), Foreground = (Brush)FindResource("TextBrush"),
            BorderBrush = (Brush)FindResource("BorderBrushDark"), Margin = new Thickness(0, 2, 0, 10) };
        twitchClientId.TextChanged += (_, _) => _settings.TwitchOAuth.ClientId = twitchClientId.Text.Trim();
        twitchContent.Children.Add(twitchClientId);
        var twitchButtons = new StackPanel { Orientation = Orientation.Horizontal };
        var twitchConnect = new Button { Content = twitch?.Connected == true ? "RECONNECT" : "CONNECT TWITCH",
            Style = (Style)FindResource("AccentButton"), Padding = new Thickness(12, 5, 12, 5) };
        twitchConnect.Click += async (_, _) => await ConnectTwitchAccountAsync();
        twitchButtons.Children.Add(twitchConnect);
        if (twitch?.Connected == true)
        {
            var disconnect = new Button { Content = "DISCONNECT", Style = (Style)FindResource("GhostButton"),
                Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(10, 0, 0, 0) };
            disconnect.Click += async (_, _) => await DisconnectTwitchAccountAsync();
            twitchButtons.Children.Add(disconnect);
        }
        twitchContent.Children.Add(twitchButtons);
        twitchCard.Child = twitchContent;
        panel.Children.Add(twitchCard);

        var kickCard = new Border
        {
            Background = (Brush)FindResource("CardBrush"), BorderBrush = (Brush)FindResource("BorderBrushDark"),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
            Padding = new Thickness(14), Margin = new Thickness(0, 0, 0, 16)
        };
        var kickContent = new StackPanel();
        kickContent.Children.Add(new TextBlock { Text = "KICK", FontWeight = FontWeights.Bold,
            Foreground = (Brush)FindResource("TextBrush"), Margin = new Thickness(0, 0, 0, 8) });
        var kick = _settings.KickAccount;
        kickContent.Children.Add(new TextBlock { Text = kick?.Connected == true
                ? $"{kick.DisplayName}  •  User ID: {kick.KickUserId}  •  {kick.State}"
                : "Not connected", Foreground = (Brush)FindResource("QuietBrush"), Margin = new Thickness(0, 0, 0, 10) });
        AddLabel(kickContent, "Kick Client ID:");
        var kickClientId = new TextBox { Text = _settings.KickOAuth.ClientId, Height = 28,
            Background = (Brush)FindResource("PanelBrush"), Foreground = (Brush)FindResource("TextBrush"),
            BorderBrush = (Brush)FindResource("BorderBrushDark"), Margin = new Thickness(0, 2, 0, 8) };
        kickClientId.TextChanged += (_, _) => { _settings.KickOAuth.ClientId = kickClientId.Text.Trim(); SettingsSaved = true; };
        kickContent.Children.Add(kickClientId);
        AddLabel(kickContent, "Kick Client Secret (stored with Windows DPAPI):");
        var kickSecret = new PasswordBox { Height = 28, Background = (Brush)FindResource("PanelBrush"),
            Foreground = (Brush)FindResource("TextBrush"), BorderBrush = (Brush)FindResource("BorderBrushDark"),
            Margin = new Thickness(0, 2, 0, 8) };
        kickContent.Children.Add(kickSecret);
        var saveSecret = new Button { Content = "SAVE SECRET", Style = (Style)FindResource("GhostButton"),
            Padding = new Thickness(10, 5, 10, 5), HorizontalAlignment = HorizontalAlignment.Left };
        saveSecret.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(kickSecret.Password)) return;
            var secrets = _secrets ?? new DpapiSecretStorage();
            _settings.KickOAuth.ClientSecretReference = string.IsNullOrWhiteSpace(_settings.KickOAuth.ClientSecretReference)
                ? Guid.NewGuid().ToString() : _settings.KickOAuth.ClientSecretReference;
            secrets.Set(_settings.KickOAuth.ClientSecretReference, kickSecret.Password);
            kickSecret.Clear();
            SettingsSaved = true;
            MessageBox.Show(this, "Kick client secret saved securely.", "Kick");
        };
        kickContent.Children.Add(saveSecret);
        AddLabel(kickContent, "Registered OAuth redirect URI (exact match required):");
        var kickRedirect = new TextBox { Text = _settings.KickOAuth.RedirectUri, Height = 28,
            Background = (Brush)FindResource("PanelBrush"), Foreground = (Brush)FindResource("TextBrush"),
            BorderBrush = (Brush)FindResource("BorderBrushDark"), Margin = new Thickness(0, 2, 0, 10) };
        kickRedirect.TextChanged += (_, _) => { _settings.KickOAuth.RedirectUri = kickRedirect.Text.Trim(); SettingsSaved = true; };
        kickContent.Children.Add(kickRedirect);
        var kickButtons = new StackPanel { Orientation = Orientation.Horizontal };
        var kickConnect = new Button { Content = kick?.Connected == true ? "RECONNECT" : "CONNECT KICK",
            Style = (Style)FindResource("AccentButton"), Padding = new Thickness(12, 5, 12, 5) };
        kickConnect.Click += async (_, _) => await ConnectKickAccountAsync();
        kickButtons.Children.Add(kickConnect);
        if (kick?.Connected == true)
        {
            var disconnect = new Button { Content = "DISCONNECT", Style = (Style)FindResource("GhostButton"),
                Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(10, 0, 0, 0) };
            disconnect.Click += async (_, _) => await DisconnectKickAccountAsync();
            kickButtons.Children.Add(disconnect);
        }
        kickContent.Children.Add(kickButtons);
        kickContent.Children.Add(new TextBlock { Text = "Kick chat receive requires a public HTTPS webhook; this desktop app cannot receive it directly.",
            Foreground = (Brush)FindResource("QuietBrush"), TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 10, 0, 0) });
        kickCard.Child = kickContent;
        panel.Children.Add(kickCard);

        var facebookCard = new Border
        {
            Background = (Brush)FindResource("CardBrush"), BorderBrush = (Brush)FindResource("BorderBrushDark"),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
            Padding = new Thickness(14), Margin = new Thickness(0, 0, 0, 16)
        };
        var facebookContent = new StackPanel();
        facebookContent.Children.Add(new TextBlock { Text = "FACEBOOK", FontWeight = FontWeights.Bold,
            Foreground = (Brush)FindResource("TextBrush"), Margin = new Thickness(0, 0, 0, 8) });
        var facebook = _settings.FacebookAccount;
        facebookContent.Children.Add(new TextBlock { Text = facebook?.Connected == true
                ? $"{facebook.DisplayName}  •  {facebook.State}"
                : "Not connected", Foreground = (Brush)FindResource("QuietBrush"), Margin = new Thickness(0, 0, 0, 8) });
        facebookContent.Children.Add(new TextBlock
        {
            Text = "Advanced Meta developer access: enter a user access token issued by your authorized Meta app. " +
                   "Meta requires a server-held App Secret for a full Facebook Login flow; this desktop app does not embed one. " +
                   "Page Live API access requires Meta App Review. Manual RTMP remains available.",
            Foreground = (Brush)FindResource("QuietBrush"), TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8)
        });
        AddLabel(facebookContent, "Meta user access token (stored with Windows DPAPI):");
        var facebookToken = new PasswordBox { Height = 28, Background = (Brush)FindResource("PanelBrush"),
            Foreground = (Brush)FindResource("TextBrush"), BorderBrush = (Brush)FindResource("BorderBrushDark"),
            Margin = new Thickness(0, 2, 0, 8) };
        facebookContent.Children.Add(facebookToken);
        var facebookButtons = new StackPanel { Orientation = Orientation.Horizontal };
        var facebookConnect = new Button { Content = facebook?.Connected == true ? "RECONNECT TOKEN" : "CONNECT PAGE TOKEN",
            Style = (Style)FindResource("AccentButton"), Padding = new Thickness(12, 5, 12, 5) };
        facebookConnect.Click += async (_, _) =>
        {
            var token = facebookToken.Password;
            facebookToken.Clear();
            await ConnectFacebookTokenAsync(token);
        };
        facebookButtons.Children.Add(facebookConnect);
        if (facebook?.Connected == true)
        {
            var disconnect = new Button { Content = "DISCONNECT", Style = (Style)FindResource("GhostButton"),
                Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(10, 0, 0, 0) };
            disconnect.Click += async (_, _) => await DisconnectFacebookAsync();
            facebookButtons.Children.Add(disconnect);
        }
        facebookContent.Children.Add(facebookButtons);
        if (facebook?.Connected == true)
        {
            AddLabel(facebookContent, "Authorized Page:");
            var pageChoice = new ComboBox { Height = 30, DisplayMemberPath = "PageName", SelectedValuePath = "PageId",
                ItemsSource = facebook.Pages, SelectedValue = facebook.SelectedPageId,
                Background = (Brush)FindResource("PanelBrush"), Foreground = (Brush)FindResource("TextBrush"),
                Margin = new Thickness(0, 2, 0, 8) };
            pageChoice.SelectionChanged += (_, _) =>
            {
                facebook.SelectedPageId = pageChoice.SelectedValue as string;
                SettingsSaved = true;
            };
            facebookContent.Children.Add(pageChoice);
            foreach (var page in facebook.Pages)
                facebookContent.Children.Add(new TextBlock { Text = $"{page.PageName}  •  ID: {page.PageId}  •  Tasks: {string.Join(", ", page.Tasks)}",
                    Foreground = (Brush)FindResource("QuietBrush"), TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 2, 0, 2) });
            AddLabel(facebookContent, "Meta permission diagnostics:");
            foreach (var diagnostic in FacebookPermissionDiagnostics.For(facebook))
                facebookContent.Children.Add(new TextBlock
                {
                    Text = $"{diagnostic.Label}: {diagnostic.Status}",
                    Foreground = (Brush)FindResource("QuietBrush"), Margin = new Thickness(0, 2, 0, 2)
                });
            facebookContent.Children.Add(new TextBlock
            {
                Text = "Granted token scopes do not prove Meta App Review approval. A Graph API request may still be denied.",
                Foreground = (Brush)FindResource("QuietBrush"), TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 6, 0, 0)
            });
        }
        facebookCard.Child = facebookContent;
        panel.Children.Add(facebookCard);

        var tikTokCard = new Border
        {
            Background = (Brush)FindResource("CardBrush"), BorderBrush = (Brush)FindResource("BorderBrushDark"),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
            Padding = new Thickness(14), Margin = new Thickness(0, 0, 0, 16)
        };
        var tikTokContent = new StackPanel();
        tikTokContent.Children.Add(new TextBlock { Text = "TIKTOK", FontWeight = FontWeights.Bold,
            Foreground = (Brush)FindResource("TextBrush"), Margin = new Thickness(0, 0, 0, 8) });
        var tikTok = _settings.TikTokAccount;
        tikTokContent.Children.Add(new TextBlock { Text = tikTok?.Connected == true
                ? $"{tikTok.DisplayName}  •  OpenID: {tikTok.OpenId}  •  {tikTok.State}"
                : "Not connected", Foreground = (Brush)FindResource("QuietBrush"), Margin = new Thickness(0, 0, 0, 8) });
        tikTokContent.Children.Add(new TextBlock
        {
            Text = "TikTok Login Kit for Desktop uses PKCE and system browser authorization. " +
                   "Current official TikTok public APIs provide account identity and display capabilities, but do NOT provide public LIVE creation, stream key retrieval, or chat APIs. " +
                   "TikTok streaming uses secure Manual RTMP transport. Client Secret is protected using Windows DPAPI.",
            Foreground = (Brush)FindResource("QuietBrush"), TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8)
        });
        AddLabel(tikTokContent, "TikTok Client Key:");
        var tikTokKey = new TextBox { Text = _settings.TikTokOAuth.ClientKey, Height = 28,
            Background = (Brush)FindResource("PanelBrush"), Foreground = (Brush)FindResource("TextBrush"),
            BorderBrush = (Brush)FindResource("BorderBrushDark"), Margin = new Thickness(0, 2, 0, 8) };
        tikTokKey.TextChanged += (_, _) => { _settings.TikTokOAuth.ClientKey = tikTokKey.Text.Trim(); SettingsSaved = true; };
        tikTokContent.Children.Add(tikTokKey);
        AddLabel(tikTokContent, "TikTok Client Secret (stored with Windows DPAPI):");
        var tikTokSecret = new PasswordBox { Height = 28, Background = (Brush)FindResource("PanelBrush"),
            Foreground = (Brush)FindResource("TextBrush"), BorderBrush = (Brush)FindResource("BorderBrushDark"),
            Margin = new Thickness(0, 2, 0, 8) };
        tikTokContent.Children.Add(tikTokSecret);
        var saveTikTokSecret = new Button { Content = "SAVE SECRET", Style = (Style)FindResource("GhostButton"),
            Padding = new Thickness(10, 5, 10, 5), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 8) };
        saveTikTokSecret.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(tikTokSecret.Password)) return;
            var secrets = _secrets ?? new DpapiSecretStorage();
            _settings.TikTokOAuth.ClientSecretReference = string.IsNullOrWhiteSpace(_settings.TikTokOAuth.ClientSecretReference)
                ? Guid.NewGuid().ToString("N") : _settings.TikTokOAuth.ClientSecretReference;
            secrets.Set(_settings.TikTokOAuth.ClientSecretReference, tikTokSecret.Password);
            tikTokSecret.Clear();
            SettingsSaved = true;
            MessageBox.Show(this, "TikTok client secret saved securely.", "TikTok");
        };
        tikTokContent.Children.Add(saveTikTokSecret);
        AddLabel(tikTokContent, "Registered OAuth redirect URI (default http://127.0.0.1:19455/callback/):");
        var tikTokRedirect = new TextBox { Text = _settings.TikTokOAuth.RedirectUri, Height = 28,
            Background = (Brush)FindResource("PanelBrush"), Foreground = (Brush)FindResource("TextBrush"),
            BorderBrush = (Brush)FindResource("BorderBrushDark"), Margin = new Thickness(0, 2, 0, 10) };
        tikTokRedirect.TextChanged += (_, _) => { _settings.TikTokOAuth.RedirectUri = tikTokRedirect.Text.Trim(); SettingsSaved = true; };
        tikTokContent.Children.Add(tikTokRedirect);
        var tikTokButtons = new StackPanel { Orientation = Orientation.Horizontal };
        var tikTokConnect = new Button { Content = tikTok?.Connected == true ? "RECONNECT" : "CONNECT TIKTOK",
            Style = (Style)FindResource("AccentButton"), Padding = new Thickness(12, 5, 12, 5) };
        tikTokConnect.Click += async (_, _) => await ConnectTikTokAccountAsync();
        tikTokButtons.Children.Add(tikTokConnect);
        if (tikTok?.Connected == true)
        {
            var disconnect = new Button { Content = "DISCONNECT", Style = (Style)FindResource("GhostButton"),
                Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(10, 0, 0, 0) };
            disconnect.Click += async (_, _) => await DisconnectTikTokAccountAsync();
            tikTokButtons.Children.Add(disconnect);
        }
        tikTokContent.Children.Add(tikTokButtons);
        tikTokCard.Child = tikTokContent;
        panel.Children.Add(tikTokCard);

        // Google OAuth Configuration Card
        var oauthBorder = new Border
        {
            Background = (Brush)FindResource("CardBrush"),
            BorderBrush = (Brush)FindResource("BorderBrushDark"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(14),
            Margin = new Thickness(0, 0, 0, 16)
        };
        var oauthContent = new StackPanel();

        var oauthHeader = new TextBlock
        {
            Text = "GOOGLE OAUTH 2.0 CONFIGURATION",
            FontWeight = FontWeights.Bold,
            FontSize = 13,
            Foreground = (Brush)FindResource("TextBrush"),
            Margin = new Thickness(0, 0, 0, 6)
        };
        oauthContent.Children.Add(oauthHeader);

        var oauthDesc = new TextBlock
        {
            Text = "Native YouTube integration uses Google OAuth 2.0 authorization code flow with PKCE.\n" +
                   "Set up a Desktop Application OAuth client in Google Cloud Console with YouTube Data API v3 enabled.",
            FontSize = 11,
            Foreground = (Brush)FindResource("QuietBrush"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12)
        };
        oauthContent.Children.Add(oauthDesc);

        AddLabel(oauthContent, "Google Client ID:");
        var clientIdBox = new TextBox
        {
            Height = 28,
            Text = _settings.GoogleOAuth.ClientId,
            Margin = new Thickness(0, 2, 0, 10),
            Background = (Brush)FindResource("PanelBrush"),
            Foreground = (Brush)FindResource("TextBrush"),
            BorderBrush = (Brush)FindResource("BorderBrushDark")
        };
        clientIdBox.TextChanged += (_, _) => _settings.GoogleOAuth.ClientId = clientIdBox.Text.Trim();
        oauthContent.Children.Add(clientIdBox);

        AddLabel(oauthContent, "Google Client Secret (Optional for PKCE desktop apps):");
        var clientSecretBox = new TextBox
        {
            Height = 28,
            Text = _settings.GoogleOAuth.ClientSecret,
            Margin = new Thickness(0, 2, 0, 10),
            Background = (Brush)FindResource("PanelBrush"),
            Foreground = (Brush)FindResource("TextBrush"),
            BorderBrush = (Brush)FindResource("BorderBrushDark")
        };
        clientSecretBox.TextChanged += (_, _) => _settings.GoogleOAuth.ClientSecret = clientSecretBox.Text.Trim();
        oauthContent.Children.Add(clientSecretBox);

        oauthBorder.Child = oauthContent;
        panel.Children.Add(oauthBorder);

        return scroll;
    }

    private async Task ConnectTwitchAccountAsync()
    {
        if (!_settings.TwitchOAuth.IsConfigured)
        {
            MessageBox.Show(this, "Enter the Twitch application Client ID first.", "Twitch");
            return;
        }
        try
        {
            var oauth = new TwitchOAuthClient(_settings.TwitchOAuth, _secrets ?? new DpapiSecretStorage());
            var account = await oauth.AuthorizeAsync(async device =>
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(device.VerificationUri) { UseShellExecute = true });
                await Dispatcher.InvokeAsync(() => MessageBox.Show(this,
                    $"Open {device.VerificationUri} and enter code: {device.UserCode}\n\nAuthorization continues after you close this message.",
                    "Connect Twitch", MessageBoxButton.OK, MessageBoxImage.Information));
            });
            if (_settings.TwitchAccount is { } previous && previous.TokenReference != account.TokenReference)
                await oauth.DisconnectAsync(previous);
            _settings.TwitchAccount = account;
            SettingsSaved = true;
            _contentArea.Content = BuildAccountsTab();
        }
        catch (OperationCanceledException) { MessageBox.Show(this, "Twitch authorization timed out.", "Twitch"); }
        catch (Exception ex) { MessageBox.Show(this, $"Twitch connection failed: {ex.Message}", "Twitch"); }
    }

    private async Task ConnectKickAccountAsync()
    {
        if (!_settings.KickOAuth.IsConfigured)
        {
            MessageBox.Show(this, "Enter Kick Client ID, save Client Secret, and register the exact redirect URI first.", "Kick");
            return;
        }
        try
        {
            var oauth = new KickOAuthClient(_settings.KickOAuth, _secrets ?? new DpapiSecretStorage());
            var account = await oauth.AuthorizeAsync();
            if (_settings.KickAccount is { } old && old.TokenReference != account.TokenReference)
                await oauth.DisconnectAsync(old);
            _settings.KickAccount = account;
            SettingsSaved = true;
            _contentArea.Content = BuildAccountsTab();
        }
        catch (Exception ex) { MessageBox.Show(this, $"Kick connection failed: {ex.Message}", "Kick"); }
    }

    private async Task DisconnectKickAccountAsync()
    {
        if (_settings.KickAccount is not { } account) return;
        if (MessageBox.Show(this, $"Disconnect Kick account {account.DisplayName}?", "Kick",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try { await new KickOAuthClient(_settings.KickOAuth, _secrets ?? new DpapiSecretStorage()).DisconnectAsync(account); }
        catch (Exception ex) { MessageBox.Show(this, $"Kick disconnect failed: {ex.Message}", "Kick"); return; }
        _settings.KickAccount = null;
        SettingsSaved = true;
        _contentArea.Content = BuildAccountsTab();
    }

    private async Task ConnectFacebookTokenAsync(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        { MessageBox.Show(this, "Enter a Meta user access token issued by your authorized app.", "Facebook"); return; }
        try
        {
            var service = new FacebookAccountService(new FacebookGraphClient(), _secrets ?? new DpapiSecretStorage());
            var account = await service.ImportUserTokenAsync(token, _settings.FacebookAccount);
            _settings.FacebookAccount = account;
            SettingsSaved = true;
            _contentArea.Content = BuildAccountsTab();
            if (account.Pages.Count == 0)
                MessageBox.Show(this, "Meta returned no authorized Pages. Check pages_show_list and Page access in your Meta app. Manual RTMP is available.", "Facebook");
        }
        catch (Exception ex) { MessageBox.Show(this, $"Facebook token could not be connected: {ex.Message}", "Facebook"); }
    }

    private async Task DisconnectFacebookAsync()
    {
        if (_settings.FacebookAccount is not { } account) return;
        await new FacebookAccountService(new FacebookGraphClient(), _secrets ?? new DpapiSecretStorage()).DisconnectAsync(account);
        _settings.FacebookAccount = null;
        SettingsSaved = true;
        _contentArea.Content = BuildAccountsTab();
    }

    private async Task ConnectTikTokAccountAsync()
    {
        if (!_settings.TikTokOAuth.IsConfigured)
        {
            MessageBox.Show(this, "Enter TikTok Client Key, save Client Secret, and register the exact redirect URI first.", "TikTok");
            return;
        }
        try
        {
            var service = new TikTokAccountService(new TikTokApiClient(), _secrets ?? new DpapiSecretStorage());
            var account = await service.AuthorizeAsync(_settings.TikTokOAuth);
            if (_settings.TikTokAccount is { } old && old.TokenReference != account.TokenReference)
                await service.DisconnectAsync(old);
            _settings.TikTokAccount = account;
            SettingsSaved = true;
            _contentArea.Content = BuildAccountsTab();
        }
        catch (OperationCanceledException) { MessageBox.Show(this, "TikTok authorization timed out or was canceled.", "TikTok"); }
        catch (Exception ex) { MessageBox.Show(this, $"TikTok connection failed: {ex.Message}", "TikTok"); }
    }

    private async Task DisconnectTikTokAccountAsync()
    {
        if (_settings.TikTokAccount is not { } account) return;
        if (MessageBox.Show(this, $"Disconnect TikTok account {account.DisplayName}?\n\nNote: Configured RTMP stream destinations are preserved.", "TikTok",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try { await new TikTokAccountService(new TikTokApiClient(), _secrets ?? new DpapiSecretStorage()).DisconnectAsync(account); }
        catch (Exception ex) { MessageBox.Show(this, $"TikTok disconnect failed: {ex.Message}", "TikTok"); return; }
        _settings.TikTokAccount = null;
        SettingsSaved = true;
        _contentArea.Content = BuildAccountsTab();
    }

    private async Task DisconnectTwitchAccountAsync()
    {
        if (_settings.TwitchAccount is not { } account) return;
        if (MessageBox.Show(this, $"Disconnect Twitch account @{account.Login}?", "Twitch",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try { await new TwitchOAuthClient(_settings.TwitchOAuth, _secrets ?? new DpapiSecretStorage()).DisconnectAsync(account); }
        catch (Exception ex) { MessageBox.Show(this, $"Could not revoke Twitch access: {ex.Message}", "Twitch"); return; }
        _settings.TwitchAccount = null;
        SettingsSaved = true;
        _contentArea.Content = BuildAccountsTab();
    }

    private async Task ConnectYouTubeAccountAsync()
    {
        if (string.IsNullOrWhiteSpace(_settings.GoogleOAuth.ClientId))
        {
            MessageBox.Show(this,
                "Please configure your Google Client ID before connecting your YouTube account.\n\n" +
                "You can obtain this from the Google Cloud Console (OAuth 2.0 Client IDs -> Desktop application).",
                "Configuration Required",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        try
        {
            ISecretStorage secrets = _secrets != null ? _secrets : new DpapiSecretStorage();
            var oauth = new GoogleOAuthClient(_settings.GoogleOAuth, secrets);
            var account = await oauth.AuthorizeAsync();

            _settings.YouTubeAccount = account;
            _contentArea.Content = BuildAccountsTab();

            MessageBox.Show(this,
                $"Successfully connected YouTube account:\n\n{account.ChannelTitle}\n(ID: {account.ChannelId})",
                "YouTube Connected",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            // User cancelled or timeout
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                $"Failed to connect YouTube account:\n\n{ex.Message}",
                "YouTube Connection Error",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private async Task DisconnectYouTubeAccountAsync()
    {
        if (_settings.YouTubeAccount == null) return;

        var res = MessageBox.Show(this,
            $"Disconnect YouTube account '{_settings.YouTubeAccount.ChannelTitle}'?\n\nThis will remove saved credentials from this application.",
            "Confirm Disconnect",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (res != MessageBoxResult.Yes) return;

        try
        {
            if (_secrets != null && !string.IsNullOrEmpty(_settings.YouTubeAccount.TokenReference))
            {
                var tokenJson = _secrets.Get(_settings.YouTubeAccount.TokenReference);
                if (!string.IsNullOrEmpty(tokenJson))
                {
                    var tokenData = System.Text.Json.JsonSerializer.Deserialize<OAuthTokenData>(tokenJson);
                    if (tokenData?.RefreshToken != null)
                    {
                        var oauth = new GoogleOAuthClient(_settings.GoogleOAuth, _secrets);
                        await oauth.RevokeTokenAsync(tokenData.RefreshToken);
                    }
                }
                _secrets.Delete(_settings.YouTubeAccount.TokenReference);
            }
        }
        catch (Exception ex)
        {
            AppLog.Write("Settings", $"Error revoking YouTube token during disconnect: {ex.Message}");
        }

        _settings.YouTubeAccount.Connected = false;
        _settings.YouTubeAccount.TokenReference = null;
        _contentArea.Content = BuildAccountsTab();
    }

    private UIElement BuildVideoTab()
    {
        var panel = new StackPanel();
        AddSectionHeader(panel, "Video & Canvas Settings");

        AddLabel(panel, "Set custom canvas dimensions. Existing source positions scale with the canvas.");
        if (_outputsActive) AddLabel(panel, "Stop recording and streaming before changing canvas dimensions.");
        var dimensions = new StackPanel { IsEnabled = !_outputsActive };
        AddNumber(dimensions, "Horizontal width (px)", "canvas-h-width", _canvasDraft.HorizontalWidth, 64, 4096, v => _canvasDraft.HorizontalWidth = v);
        AddNumber(dimensions, "Horizontal height (px)", "canvas-h-height", _canvasDraft.HorizontalHeight, 64, 4096, v => _canvasDraft.HorizontalHeight = v);
        AddNumber(dimensions, "Vertical width (px)", "canvas-v-width", _canvasDraft.VerticalWidth, 64, 4096, v => _canvasDraft.VerticalWidth = v);
        AddNumber(dimensions, "Vertical height (px)", "canvas-v-height", _canvasDraft.VerticalHeight, 64, 4096, v => _canvasDraft.VerticalHeight = v);
        panel.Children.Add(dimensions);
        AddLabel(panel, "Use even dimensions, for example 2560 × 1440 or 720 × 1280.");
        AddSectionHeader(panel, "Video bitrate");
        AddNumber(panel, "Recording video bitrate (kbps)", "record-video", _recordingDraft.Video.BitrateKbps, 100, 200000, v => _recordingDraft.Video.BitrateKbps = v);
        AddNumber(panel, "Streaming video bitrate for new destinations (kbps)", "stream-video", _streamingDraft.DefaultVideoBitrateKbps, 500, 100000, v => _streamingDraft.DefaultVideoBitrateKbps = v);
        foreach (var group in _settings.DestinationGroups)
            foreach (var destination in new[] { group.Horizontal, group.Vertical })
                if (destination.Status is DestinationStatus.Offline or DestinationStatus.Disabled or DestinationStatus.Error)
                    AddNumber(panel, $"{group.Name} · {destination.OutputMode} video bitrate (kbps)", $"dest-video-{destination.Id}", destination.VideoBitrateKbps, 500, 100000, v => destination.VideoBitrateKbps = v);
        AddLabel(panel, "Bitrate changes apply when you next start an output.");

        AddLabel(panel, "Default Hardware Encoder:");
        var encCombo = new ComboBox { Height = 28, Margin = new Thickness(0, 4, 0, 12) };
        encCombo.Items.Add(new ComboBoxItem { Content = "Auto (recommended)", Tag = "auto" });
        foreach (var encoder in _encoders.Where(e => e.Available))
            encCombo.Items.Add(new ComboBoxItem { Content = encoder.DisplayName, Tag = encoder.Id });
        encCombo.SelectedItem = encCombo.Items.Cast<ComboBoxItem>().FirstOrDefault(item => Equals(item.Tag, _streamingDraft.DefaultEncoder)) ?? encCombo.Items[0];
        encCombo.SelectionChanged += (_, _) => _streamingDraft.DefaultEncoder = (string)((ComboBoxItem)encCombo.SelectedItem).Tag;
        panel.Children.Add(encCombo);

        return new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    private UIElement BuildAudioTab()
    {
        var panel = new StackPanel();
        AddSectionHeader(panel, "Microphone");
        AddLabel(panel, "Choose the microphone used by your microphone sources.");
        AddDeviceChoice(panel, true, _audioDraft.DefaultMicrophoneId, device =>
        {
            _audioDraft.DefaultMicrophoneId = device.Id;
            foreach (var resource in _settings.CaptureResources.Where(r => r.Type == SourceType.AudioInput))
            {
                _deviceChanges[resource.Id] = device;
                _filterChanges[resource.Id] = _audioDraft.MicrophoneFilters!.Copy();
            }
        });
        var filters = new Button { Content = "Microphone filters…", HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(16, 8, 16, 8), Margin = new Thickness(0, 0, 0, 12) };
        filters.Click += (_, _) =>
        {
            var selectedFilters = _audioDraft.MicrophoneFilters ?? _settings.CaptureResources
                .FirstOrDefault(r => r.Type == SourceType.AudioInput && r.DeviceId == _audioDraft.DefaultMicrophoneId)?.AudioFilters
                ?? _settings.CaptureResources.FirstOrDefault(r => r.Type == SourceType.AudioInput)?.AudioFilters ?? new AudioFilterSettings();
            var dialog = new AudioFiltersDialog(this, "Selected microphone", selectedFilters);
            if (dialog.ShowDialog() == true)
            {
                _audioDraft.MicrophoneFilters = dialog.Result.Copy();
                foreach (var resource in _settings.CaptureResources.Where(r => r.Type == SourceType.AudioInput))
                    _filterChanges[resource.Id] = dialog.Result.Copy();
            }
        };
        panel.Children.Add(filters);
        AddLabel(panel, "Noise suppression, gate, compressor, gain and limiter. Your filters stay selected when you change microphones.");
        AddSectionHeader(panel, "Desktop audio");
        AddDeviceChoice(panel, false, _audioDraft.DefaultDesktopAudioId, device =>
        {
            _audioDraft.DefaultDesktopAudioId = device.Id;
            foreach (var resource in _settings.CaptureResources.Where(r => r.Type == SourceType.AudioOutput))
                _deviceChanges[resource.Id] = device;
        });
        AddLabel(panel, "48 kHz · Stereo. Individual source controls are available in the audio mixer.");
        return new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    private UIElement BuildRecordingTab()
    {
        var panel = new StackPanel();
        AddSectionHeader(panel, "Local Recording Defaults");

        AddLabel(panel, "Recording folder:");
        var folder = new TextBox { Text = _recordingDraft.OutputDirectory, Margin = new Thickness(0, 4, 0, 8) };
        folder.TextChanged += (_, _) => _recordingDraft.OutputDirectory = folder.Text.Trim();
        panel.Children.Add(folder);
        var browse = new Button { Content = "Browse…", HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 0, 0, 12) };
        browse.Click += (_, _) =>
        {
            using var dialog = new System.Windows.Forms.FolderBrowserDialog { Description = "Recording save folder", UseDescriptionForTitle = true, SelectedPath = folder.Text };
            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK) folder.Text = dialog.SelectedPath;
        };
        panel.Children.Add(browse);
        AddLabel(panel, "Container:");
        var container = new ComboBox { ItemsSource = new[] { "mkv", "mp4" }, SelectedItem = _recordingDraft.Container, Margin = new Thickness(0, 4, 0, 12) };
        container.SelectionChanged += (_, _) => _recordingDraft.Container = (string)container.SelectedItem;
        panel.Children.Add(container);
        AddLabel(panel, "Recording encoder:");
        var encoder = new ComboBox { Margin = new Thickness(0, 4, 0, 12) };
        encoder.Items.Add(new ComboBoxItem { Content = "Auto (recommended)", Tag = "auto" });
        foreach (var item in _encoders.Where(e => e.Available)) encoder.Items.Add(new ComboBoxItem { Content = item.DisplayName, Tag = item.Id });
        encoder.SelectedItem = encoder.Items.Cast<ComboBoxItem>().FirstOrDefault(item => Equals(item.Tag, _recordingDraft.Video.EncoderId)) ?? encoder.Items[0];
        encoder.SelectionChanged += (_, _) => _recordingDraft.Video.EncoderId = (string)((ComboBoxItem)encoder.SelectedItem).Tag;
        panel.Children.Add(encoder);
        AddNumber(panel, "Recording video bitrate (kbps)", "record-video", _recordingDraft.Video.BitrateKbps, 100, 200000, v => _recordingDraft.Video.BitrateKbps = v);
        AddNumber(panel, "Recording audio bitrate (kbps)", "record-audio", _recordingDraft.Audio.BitrateKbps, 32, 512, v => _recordingDraft.Audio.BitrateKbps = v);
        AddNumber(panel, "Recording frame rate (FPS)", "record-fps", _recordingDraft.Video.FrameRate, 1, 60, v => _recordingDraft.Video.FrameRate = v);
        AddLabel(panel, "Changes apply to the next recording.");

        return new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    private UIElement BuildStreamingTab()
    {
        var panel = new StackPanel();
        AddSectionHeader(panel, "Streaming & Network Adaptation");
        AddNumber(panel, "Default video bitrate for new destinations (kbps)", "stream-video", _streamingDraft.DefaultVideoBitrateKbps, 500, 100000, v => _streamingDraft.DefaultVideoBitrateKbps = v);
        AddNumber(panel, "Default audio bitrate for new destinations (kbps)", "stream-audio", _streamingDraft.DefaultAudioBitrateKbps, 32, 320, v => _streamingDraft.DefaultAudioBitrateKbps = v);
        foreach (var group in _settings.DestinationGroups)
        {
            AddSectionHeader(panel, group.Name);
            foreach (var destination in new[] { group.Horizontal, group.Vertical })
            {
                if (destination.Status is not (DestinationStatus.Offline or DestinationStatus.Disabled or DestinationStatus.Error))
                { AddLabel(panel, $"{destination.OutputMode}: stop this output before changing bitrate."); continue; }
                AddNumber(panel, $"{destination.OutputMode} video bitrate (kbps)", $"dest-video-{destination.Id}", destination.VideoBitrateKbps, 500, 100000, v => destination.VideoBitrateKbps = v);
                AddNumber(panel, $"{destination.OutputMode} audio bitrate (kbps)", $"dest-audio-{destination.Id}", destination.AudioBitrateKbps, 32, 320, v => destination.AudioBitrateKbps = v);
            }
        }

        var chkAdapt = new CheckBox
        {
            Content = "Enable Adaptive Bitrate (Reduce bitrate during network congestion)",
            IsChecked = _streamingDraft.AdaptiveBitrateDefault,
            Margin = new Thickness(0, 10, 0, 10)
        };
        chkAdapt.Checked += (_, _) => _streamingDraft.AdaptiveBitrateDefault = true;
        chkAdapt.Unchecked += (_, _) => _streamingDraft.AdaptiveBitrateDefault = false;
        panel.Children.Add(chkAdapt);

        var chkRecover = new CheckBox
        {
            Content = "Enable Auto-Recovery (Cautiously increase bitrate when network stabilizes)",
            IsChecked = _streamingDraft.AutoRecoverBitrateDefault,
            Margin = new Thickness(0, 0, 0, 15)
        };
        chkRecover.Checked += (_, _) => _streamingDraft.AutoRecoverBitrateDefault = true;
        chkRecover.Unchecked += (_, _) => _streamingDraft.AutoRecoverBitrateDefault = false;
        panel.Children.Add(chkRecover);

        AddLabel(panel, $"Bandwidth Warning Threshold: {_settings.Streaming.BandwidthWarningThresholdMbps} Mbps");

        return new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    private void AddNumber(StackPanel panel, string label, string key, int value, int min, int max, Action<int> set)
    {
        AddLabel(panel, label);
        var text = _numbers.TryGetValue(key, out var previous) ? previous.Text : value.ToString();
        var input = new TextBox { Text = text, Margin = new Thickness(0, 4, 0, 12) };
        _numbers[key] = (text, min, max, set);
        input.TextChanged += (_, _) => _numbers[key] = (input.Text, min, max, set);
        panel.Children.Add(input);
    }

    private void AddDeviceChoice(StackPanel panel, bool input, string? selectedId, Action<AudioDeviceInfo> set)
    {
        try
        {
            var devices = new WasapiAudioCaptureService().Enumerate(input).ToList();
            selectedId ??= input ? WasapiAudioCaptureService.DefaultInputId : WasapiAudioCaptureService.DefaultOutputId;
            if (devices.All(d => d.Id != selectedId)) devices.Add(new AudioDeviceInfo(selectedId, "Previously selected device (disconnected)", input, false));
            var items = devices.Select(device => new ComboBoxItem { Content = device.Label, Tag = device }).ToArray();
            var combo = new ComboBox { ItemsSource = items, SelectedItem = items.First(item => ((AudioDeviceInfo)item.Tag).Id == selectedId), Margin = new Thickness(0, 4, 0, 12) };
            combo.SelectionChanged += (_, _) => { if (combo.SelectedItem is ComboBoxItem { Tag: AudioDeviceInfo device }) set(device); };
            panel.Children.Add(combo);
        }
        catch (Exception ex) { AddLabel(panel, $"Could not list audio devices: {ex.Message}"); }
    }

    private void SaveEditableSettings()
    {
        var numbers = new List<(Action<int> Set, int Value)>();
        foreach (var (key, field) in _numbers)
        {
            if (!int.TryParse(field.Text, out var value) || value < field.Min || value > field.Max)
            { MessageBox.Show(this, $"{key}: enter a whole number from {field.Min} to {field.Max}.", "Invalid setting"); return; }
            numbers.Add((field.Set, value));
        }
        if (string.IsNullOrWhiteSpace(_recordingDraft.OutputDirectory) || !System.IO.Path.IsPathFullyQualified(_recordingDraft.OutputDirectory) || _recordingDraft.OutputDirectory.IndexOfAny(System.IO.Path.GetInvalidPathChars()) >= 0)
        { MessageBox.Show(this, "Choose a full recording folder path, such as D:\\Recordings.", "Invalid recording folder"); return; }
        foreach (var (key, field) in _numbers.Where(pair => pair.Key.StartsWith("canvas-")))
            if (int.Parse(field.Text) % 2 != 0)
            { MessageBox.Show(this, "Canvas width and height must be even numbers.", "Invalid canvas size"); return; }
        var enabledHotkeys = _hotkeyDraft.Where(h => h.Enabled && h.Key != 0).ToList();
        if (enabledHotkeys.Any(h => enabledHotkeys.Any(other => !ReferenceEquals(h, other) && h.ConflictsWith(other))))
        { MessageBox.Show(this, "Two enabled actions use the same shortcut. Change or disable one of them.", "Shortcut conflict"); return; }
        foreach (var (set, value) in numbers) set(value);
        foreach (var resource in _settings.CaptureResources)
        {
            if (_deviceChanges.TryGetValue(resource.Id, out var device))
            {
                var oldName = resource.Name;
                resource.DeviceId = device.Id;
                resource.FollowSystemDefault = device.Id is WasapiAudioCaptureService.DefaultInputId or WasapiAudioCaptureService.DefaultOutputId;
                resource.Name = $"{(device.IsInput ? "Microphone" : "Desktop Audio")} — {device.Name}";
                foreach (var source in _settings.Scenes.SelectMany(s => s.Sources).Where(s => s.SourceReference == resource.Id && s.Name == oldName)) source.Name = resource.Name;
            }
            if (_filterChanges.TryGetValue(resource.Id, out var filters)) resource.AudioFilters = filters.Copy();
        }
        if (_canvasDraft != _settings.Canvas)
        {
            _recordingDraft.Video.Width = 0;
            _recordingDraft.Video.Height = 0;
        }
        _settings.Canvas = _canvasDraft; _settings.Hotkeys = _hotkeyDraft;
        _settings.Recording = _recordingDraft; _settings.Audio = _audioDraft;
        _settings.Streaming = _streamingDraft; _settings.Performance = _performanceDraft;
        SettingsSaved = true; Close();
    }

    private UIElement BuildHotkeysTab()
    {
        var panel = new StackPanel();
        AddSectionHeader(panel, "Keyboard shortcuts");
        AddLabel(panel, "Click a shortcut and press your key combination. Enable actions you want to use globally.");
        foreach (var group in _hotkeyDraft.GroupBy(h => h.Action.ToString().StartsWith("SwitchScene") ? "Scenes" :
                     h.Action is HotkeyAction.MuteMicrophone or HotkeyAction.MuteDesktopAudio ? "Audio" : "Streaming & recording")
                     .OrderBy(group => group.Key == "Scenes" ? 2 : group.Key == "Audio" ? 1 : 0))
        {
            AddSectionHeader(panel, group.Key);
            foreach (var binding in group)
            {
                var row = new Grid { Margin = new Thickness(0, 0, 0, 8) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var enabled = new CheckBox { Content = binding.DisplayName, IsChecked = binding.Enabled,
                    VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
                enabled.Checked += (_, _) => binding.Enabled = true;
                enabled.Unchecked += (_, _) => binding.Enabled = false;
                row.Children.Add(enabled);
                var shortcut = new TextBox { Text = binding.Key == 0 ? "Click to set shortcut" : binding.ShortcutText,
                    IsReadOnly = true, Padding = new Thickness(8), VerticalContentAlignment = VerticalAlignment.Center };
                shortcut.PreviewKeyDown += (_, e) =>
                {
                    var key = e.Key == System.Windows.Input.Key.System ? e.SystemKey : e.Key;
                    if (key is System.Windows.Input.Key.LeftCtrl or System.Windows.Input.Key.RightCtrl or
                        System.Windows.Input.Key.LeftShift or System.Windows.Input.Key.RightShift or
                        System.Windows.Input.Key.LeftAlt or System.Windows.Input.Key.RightAlt or
                        System.Windows.Input.Key.LWin or System.Windows.Input.Key.RWin) return;
                    if (key is System.Windows.Input.Key.Tab or System.Windows.Input.Key.Escape) return;
                    e.Handled = true;
                    var modifiers = System.Windows.Input.Keyboard.Modifiers;
                    binding.Key = System.Windows.Input.KeyInterop.VirtualKeyFromKey(key);
                    binding.Modifiers = (modifiers.HasFlag(System.Windows.Input.ModifierKeys.Alt) ? 1 : 0) |
                        (modifiers.HasFlag(System.Windows.Input.ModifierKeys.Control) ? 2 : 0) |
                        (modifiers.HasFlag(System.Windows.Input.ModifierKeys.Shift) ? 4 : 0) |
                        (modifiers.HasFlag(System.Windows.Input.ModifierKeys.Windows) ? 8 : 0);
                    binding.ShortcutText = (((binding.Modifiers & 2) != 0) ? "Ctrl + " : "") +
                        (((binding.Modifiers & 1) != 0) ? "Alt + " : "") +
                        (((binding.Modifiers & 4) != 0) ? "Shift + " : "") +
                        (((binding.Modifiers & 8) != 0) ? "Win + " : "") + key;
                    shortcut.Text = binding.ShortcutText;
                    binding.Enabled = true; enabled.IsChecked = true;
                };
                Grid.SetColumn(shortcut, 1); row.Children.Add(shortcut);
                var clear = new Button { Content = "Clear", Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(8, 4, 8, 4) };
                clear.Click += (_, _) => { binding.Key = 0; binding.Modifiers = 0; binding.ShortcutText = "";
                    binding.Enabled = false; enabled.IsChecked = false; shortcut.Text = "Click to set shortcut"; };
                Grid.SetColumn(clear, 2); row.Children.Add(clear);
                panel.Children.Add(new Border { Background = (Brush)FindResource("PanelBrush"), CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(12), Margin = new Thickness(0, 0, 0, 6), Child = row });
            }
        }
        return new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    private UIElement BuildAdvancedTab()
    {
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var panel = new StackPanel();
        scroll.Content = panel;
        AddSectionHeader(panel, "Advanced & Diagnostics");

        AddLabel(panel, "PERFORMANCE");
        var hardware = PerformancePolicy.Classify(Environment.ProcessorCount,
            GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
            _encoders.Any(e => e.HardwareAccelerated && e.Available));
        AddLabel(panel, $"Hardware: {hardware} • {Environment.ProcessorCount} logical cores • " +
            $"{GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1073741824.0:0.0} GiB available • " +
            $"hardware encoder {(_encoders.Any(e => e.HardwareAccelerated && e.Available) ? "available" : "unavailable")}");
        var mode = new ComboBox { Width = 220, HorizontalAlignment = HorizontalAlignment.Left,
            ItemsSource = Enum.GetValues<PerformanceMode>(), SelectedItem = _performanceDraft.Mode,
            Margin = new Thickness(0, 0, 0, 8) };
        mode.SelectionChanged += (_, _) => { if (mode.SelectedItem is PerformanceMode value) _performanceDraft.Mode = value; };
        panel.Children.Add(mode);
        AddLabel(panel, "Eco: lowest resource usage, recommended for older PCs. Auto adapts to available cores, RAM, and encoders.");
        var customRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        var previewFps = new TextBox { Width = 50, Text = _performanceDraft.CustomPreviewFps.ToString(), Margin = new Thickness(6, 0, 16, 0) };
        var meterHz = new TextBox { Width = 50, Text = _performanceDraft.CustomMeterRefreshHz.ToString(), Margin = new Thickness(6, 0, 0, 0) };
        previewFps.TextChanged += (_, _) => { if (int.TryParse(previewFps.Text, out var value)) _performanceDraft.CustomPreviewFps = Math.Clamp(value, 1, 60); };
        meterHz.TextChanged += (_, _) => { if (int.TryParse(meterHz.Text, out var value)) _performanceDraft.CustomMeterRefreshHz = Math.Clamp(value, 1, 20); };
        customRow.Children.Add(new TextBlock { Text = "Custom preview FPS", Foreground = (Brush)FindResource("TextBrush") });
        customRow.Children.Add(previewFps);
        customRow.Children.Add(new TextBlock { Text = "Meter Hz", Foreground = (Brush)FindResource("TextBrush") });
        customRow.Children.Add(meterHz);
        panel.Children.Add(customRow);
        var protection = new CheckBox { Content = "Automatic performance protection", IsChecked = _performanceDraft.AutomaticProtection,
            Margin = new Thickness(0, 8, 0, 4) };
        protection.Checked += (_, _) => _performanceDraft.AutomaticProtection = true;
        protection.Unchecked += (_, _) => _performanceDraft.AutomaticProtection = false;
        panel.Children.Add(protection);
        var qualityReduction = new CheckBox { Content = "Allow automatic stream quality reduction (default off)",
            IsChecked = _performanceDraft.AllowAutomaticStreamQualityReduction, Margin = new Thickness(0, 0, 0, 12) };
        qualityReduction.Checked += (_, _) => _performanceDraft.AllowAutomaticStreamQualityReduction = true;
        qualityReduction.Unchecked += (_, _) => _performanceDraft.AllowAutomaticStreamQualityReduction = false;
        panel.Children.Add(qualityReduction);
        if (_performanceMetrics is not null)
        {
            var live = new TextBlock { Foreground = (Brush)FindResource("TextBrush"),
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 12) };
            panel.Children.Add(live);
            void RefreshMetrics()
            {
                var metrics = _performanceMetrics.Last;
                var gpuMemory = GpuMemoryProbe.Sample();
                var gpuLine = gpuMemory.Count == 0 ? "GPU 3D N/A   GPU Encode N/A   VRAM N/A\n" :
                    string.Join("\n", gpuMemory.Select(g =>
                        $"{g.Adapter}: process VRAM {g.UsedBytes / 1048576.0:0}/{g.BudgetBytes / 1048576.0:0} MB budget " +
                        $"({g.DedicatedBytes / 1048576.0:0} MB dedicated); GPU 3D/Encode N/A")) + "\n";
                live.Text = $"CPU {metrics.CpuPercent:0.0}%   RAM {metrics.WorkingSetMb:0} MB   Private {metrics.PrivateMemoryMb:0} MB\n" +
                    gpuLine +
                    $"Capture {metrics.CaptureFps:0.0} FPS / {metrics.CaptureMilliseconds:0.0} ms   " +
                    $"Composition {metrics.CompositionFps:0.0} FPS / {metrics.CompositionMilliseconds:0.0} ms   " +
                    $"Preview {metrics.PreviewFps:0.0} FPS   Output {metrics.OutputFps:0.0} FPS\n" +
                    $"Drops: capture {metrics.CaptureDrops}, composition deadline {metrics.CompositionDeadlineDrops}, " +
                    $"frame hub {metrics.CompositionDrops}, preview {metrics.PreviewDrops}, output {metrics.OutputDrops}\n" +
                    $"Allocated {metrics.AllocatedMbPerSecond:0.0} MB/s   GC {metrics.Gen0Collections}/{metrics.Gen1Collections}/{metrics.Gen2Collections}\n" +
                    (_performanceOutputDetails?.Invoke() ?? "");
            }
            RefreshMetrics();
            _performanceTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _performanceTimer.Tick += (_, _) => RefreshMetrics();
            _performanceTimer.Start();
        }

        AddLabel(panel, "Process Priority: Normal");
        AddLabel(panel, "Log Retention: 14 Days (Automatic Rolling Clean)");

        var diagBtn = new Button
        {
            Content = "EXPORT DIAGNOSTICS BUNDLE (.ZIP)",
            Style = (Style)FindResource("GhostButton"),
            Width = 260,
            Height = 32,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 15, 0, 0)
        };
        diagBtn.Click += async (_, _) =>
        {
            try
            {
                var zipPath = await DiagnosticsExporter.ExportZipAsync(_settings, _ffmpeg, _encoders, relayDiagnostics: _relayDiagnostics?.Invoke());
                MessageBox.Show(this, $"Diagnostics exported successfully to:\n{zipPath}", "Diagnostics", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Failed to export diagnostics: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        };
        panel.Children.Add(diagBtn);

        return scroll;
    }

    private UIElement BuildAboutTab()
    {
        var panel = new StackPanel();
        panel.Children.Add(new Image
        {
            Source = (ImageSource)Application.Current.FindResource("BrandLogo"),
            Width = 112,
            Height = 92,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 0, 0, 12)
        });
        AddSectionHeader(panel, "About Aravals Stream");

        AddLabel(panel, $"Application: {AppVersion.Name}");
        AddLabel(panel, $"Version: {AppVersion.Version} ({AppVersion.ReleaseChannel})");
        AddLabel(panel, $"Publisher: {AppVersion.Publisher}");
        AddLabel(panel, $"Build Date: {AppVersion.BuildDate:yyyy-MM-dd}");
        AddLabel(panel, $"FFmpeg: {_ffmpeg.Version ?? _ffmpeg.Status.ToString()}");
        AddLabel(panel, $"License: MIT License (Commercial and Personal use permitted)");

        return panel;
    }

    private void AddSectionHeader(StackPanel panel, string title)
    {
        panel.Children.Add(new TextBlock
        {
            Text = title.ToUpperInvariant(),
            FontSize = 16,
            FontWeight = FontWeights.Bold,
            Foreground = (Brush)FindResource("TextBrush"),
            Margin = new Thickness(0, 0, 0, 15)
        });
    }

    private void AddLabel(StackPanel panel, string text)
    {
        panel.Children.Add(new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Foreground = (Brush)FindResource("TextBrush"),
            Margin = new Thickness(0, 0, 0, 8)
        });
    }
}
