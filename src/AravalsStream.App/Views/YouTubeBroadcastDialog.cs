using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AravalsStream.Core.Accounts;
using AravalsStream.Core.Services;
using AravalsStream.Core.YouTube;
using AravalsStream.Core.YouTube.Models;

namespace AravalsStream.App.Views;

public sealed class YouTubeBroadcastDialog : Window
{
    private readonly YouTubeAccount _account;
    private readonly GoogleOAuthClient _oauthClient;
    private readonly YouTubeApiClient _apiClient;

    // Create New Controls
    private readonly TextBox _titleBox = new() { Height = 32 };
    private readonly TextBox _descBox = new() { Height = 64, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true };
    private readonly ComboBox _privacyCombo = new() { Height = 32 };
    private readonly RadioButton _startNowRadio = new() { Content = "Start immediately when streaming begins", IsChecked = true, Margin = new Thickness(0, 4, 0, 4) };
    private readonly RadioButton _scheduleRadio = new() { Content = "Schedule for later date/time", Margin = new Thickness(0, 4, 0, 8) };
    private readonly DatePicker _datePicker = new() { Height = 32, SelectedDate = DateTime.Today, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 8) };
    private readonly RadioButton _notForKidsRadio = new() { Content = "No, it's not made for kids (General Audience)", IsChecked = true, Margin = new Thickness(0, 4, 0, 4) };
    private readonly RadioButton _forKidsRadio = new() { Content = "Yes, it's made for kids", Margin = new Thickness(0, 4, 0, 8) };
    private readonly CheckBox _dvrCheck = new() { Content = "Enable DVR (viewers can rewind)", IsChecked = true, Margin = new Thickness(0, 2, 0, 4) };
    private readonly CheckBox _autoStartCheck = new() { Content = "Auto-start broadcast when ingest receives video", IsChecked = true, Margin = new Thickness(0, 2, 0, 4) };
    private readonly CheckBox _autoStopCheck = new() { Content = "Auto-stop broadcast when video ingest stops", IsChecked = false, Margin = new Thickness(0, 2, 0, 8) };
    private readonly ComboBox _resCombo = new() { Height = 32 };

    // Select Existing Controls
    private readonly ListBox _broadcastList = new() { MinHeight = 220 };
    private readonly TextBlock _statusText = new() { Foreground = Brushes.Orange, Margin = new Thickness(0, 8, 0, 8), TextWrapping = TextWrapping.Wrap };
    private readonly Button _actionBtn = new() { Height = 36, Padding = new Thickness(16, 0, 16, 0), FontWeight = FontWeights.SemiBold };

    private IReadOnlyList<YouTubeBroadcastItem> _existingBroadcasts = Array.Empty<YouTubeBroadcastItem>();

    public YouTubeBroadcastItem? ResultBroadcast { get; private set; }
    public YouTubeLiveStreamItem? ResultStream { get; private set; }
    public string? IngestionAddress { get; private set; }
    public string? StreamKey { get; private set; }

    public YouTubeBroadcastDialog(
        Window owner,
        YouTubeAccount account,
        GoogleOAuthClient oauthClient,
        YouTubeApiClient? apiClient = null)
    {
        Owner = owner;
        _account = account;
        _oauthClient = oauthClient;
        _apiClient = apiClient ?? new YouTubeApiClient();

        Title = $"YouTube Live Manager - {account.ChannelTitle}";
        Width = 560;
        Height = 680;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(27, 29, 36));
        Foreground = Brushes.White;

        var root = new Grid { Margin = new Thickness(20) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Header
        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };
        var title = new TextBlock
        {
            Text = "YouTube Live Broadcast",
            FontSize = 18,
            FontWeight = FontWeights.Bold,
            Foreground = Brushes.White
        };
        var sub = new TextBlock
        {
            Text = $"Connected Channel: {account.ChannelTitle} ({account.FormattedSubscribers} subscribers)",
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(156, 163, 175)),
            Margin = new Thickness(0, 2, 0, 0)
        };
        header.Children.Add(title);
        header.Children.Add(sub);
        Grid.SetRow(header, 0);
        root.Children.Add(header);

        // Tab Control
        var tabs = new TabControl { Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
        Grid.SetRow(tabs, 1);

        // Tab 1: Create New
        var createTab = new TabItem { Header = "Create New Broadcast" };
        var createPanel = new StackPanel { Margin = new Thickness(12) };
        var scroll = new ScrollViewer { Content = createPanel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

        _titleBox.Text = $"Aravals Stream Live - {DateTime.Now:yyyy-MM-dd HH:mm}";
        _descBox.Text = "Live streamed via Aravals Stream.";
        _privacyCombo.ItemsSource = new[] { "Unlisted", "Private", "Public" };
        _privacyCombo.SelectedIndex = 0;

        _resCombo.ItemsSource = new[] { "1080p 60fps", "1080p 30fps", "720p 60fps", "720p 30fps" };
        _resCombo.SelectedIndex = 0;

        _startNowRadio.Checked += (_, _) => _datePicker.Visibility = Visibility.Collapsed;
        _scheduleRadio.Checked += (_, _) => _datePicker.Visibility = Visibility.Visible;

        AddLabel(createPanel, "Broadcast Title");
        createPanel.Children.Add(_titleBox);

        AddLabel(createPanel, "Description");
        createPanel.Children.Add(_descBox);

        AddLabel(createPanel, "Privacy");
        createPanel.Children.Add(_privacyCombo);

        AddLabel(createPanel, "Schedule");
        createPanel.Children.Add(_startNowRadio);
        createPanel.Children.Add(_scheduleRadio);
        createPanel.Children.Add(_datePicker);

        AddLabel(createPanel, "Audience (Required by YouTube)");
        createPanel.Children.Add(_notForKidsRadio);
        createPanel.Children.Add(_forKidsRadio);

        AddLabel(createPanel, "Ingest Resolution");
        createPanel.Children.Add(_resCombo);

        AddLabel(createPanel, "Options");
        createPanel.Children.Add(_dvrCheck);
        createPanel.Children.Add(_autoStartCheck);
        createPanel.Children.Add(_autoStopCheck);

        createTab.Content = scroll;
        tabs.Items.Add(createTab);

        // Tab 2: Existing Broadcasts
        var existingTab = new TabItem { Header = "Select Existing Broadcast" };
        var existingPanel = new StackPanel { Margin = new Thickness(12) };

        var topBar = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        topBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        topBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var listHelp = new TextBlock
        {
            Text = "Upcoming & Active Broadcasts:",
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(156, 163, 175)),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(listHelp, 0);
        topBar.Children.Add(listHelp);

        var refreshBtn = new Button
        {
            Content = "Refresh List",
            Height = 28,
            Padding = new Thickness(12, 0, 12, 0),
            Background = new SolidColorBrush(Color.FromRgb(45, 48, 58)),
            Foreground = Brushes.White,
            Cursor = System.Windows.Input.Cursors.Hand
        };
        refreshBtn.Click += async (_, _) => await LoadExistingBroadcastsAsync();
        Grid.SetColumn(refreshBtn, 1);
        topBar.Children.Add(refreshBtn);

        existingPanel.Children.Add(topBar);

        _broadcastList.Background = new SolidColorBrush(Color.FromRgb(20, 22, 28));
        _broadcastList.BorderBrush = new SolidColorBrush(Color.FromRgb(45, 48, 58));
        _broadcastList.Foreground = Brushes.White;
        existingPanel.Children.Add(_broadcastList);

        existingTab.Content = existingPanel;
        tabs.Items.Add(existingTab);

        root.Children.Add(tabs);

        // Footer
        var footer = new Grid { Margin = new Thickness(0, 16, 0, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        Grid.SetColumn(_statusText, 0);
        footer.Children.Add(_statusText);

        var cancelBtn = new Button
        {
            Content = "Cancel",
            Width = 80,
            Height = 36,
            Background = Brushes.Transparent,
            Foreground = new SolidColorBrush(Color.FromRgb(156, 163, 175)),
            Cursor = System.Windows.Input.Cursors.Hand,
            Margin = new Thickness(0, 0, 10, 0)
        };
        cancelBtn.Click += (_, _) => { DialogResult = false; Close(); };
        Grid.SetColumn(cancelBtn, 1);
        footer.Children.Add(cancelBtn);

        _actionBtn.Content = "Create & Bind Broadcast";
        _actionBtn.Background = new SolidColorBrush(Color.FromRgb(239, 68, 68)); // YouTube Red
        _actionBtn.Foreground = Brushes.White;
        _actionBtn.Cursor = System.Windows.Input.Cursors.Hand;
        _actionBtn.Click += async (_, _) =>
        {
            if (tabs.SelectedIndex == 0)
                await ExecuteCreateAsync();
            else
                await ExecuteSelectExistingAsync();
        };
        Grid.SetColumn(_actionBtn, 2);
        footer.Children.Add(_actionBtn);

        tabs.SelectionChanged += (_, _) =>
        {
            _actionBtn.Content = tabs.SelectedIndex == 0 ? "Create & Bind Broadcast" : "Select & Bind Broadcast";
        };

        Grid.SetRow(footer, 2);
        root.Children.Add(footer);

        Content = root;
        Loaded += async (_, _) => await LoadExistingBroadcastsAsync();
    }

    private static void AddLabel(StackPanel panel, string text)
    {
        panel.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(209, 213, 219)),
            Margin = new Thickness(0, 10, 0, 4)
        });
    }

    private async Task LoadExistingBroadcastsAsync()
    {
        _statusText.Text = "Loading existing broadcasts...";
        _broadcastList.Items.Clear();

        try
        {
            var token = await _oauthClient.GetValidAccessTokenAsync(_account);
            _existingBroadcasts = await _apiClient.ListBroadcastsAsync(token, "all");

            if (_existingBroadcasts.Count == 0)
            {
                _statusText.Text = "No upcoming or active broadcasts found for this channel.";
                return;
            }

            foreach (var b in _existingBroadcasts)
            {
                var time = b.Snippet?.ScheduledStartTime?.ToLocalTime().ToString("g") ?? "No date";
                var status = b.Status?.LifeCycleStatus?.ToUpperInvariant() ?? "UNKNOWN";
                var privacy = b.Status?.PrivacyStatus?.ToUpperInvariant() ?? "PRIVATE";
                _broadcastList.Items.Add($"{b.Snippet?.Title} [{status} • {privacy}] (Scheduled: {time})");
            }
            _broadcastList.SelectedIndex = 0;
            _statusText.Text = $"Found {_existingBroadcasts.Count} broadcast(s).";
        }
        catch (Exception ex)
        {
            _statusText.Text = $"Failed to load broadcasts: {ex.Message}";
        }
    }

    private async Task ExecuteCreateAsync()
    {
        var title = _titleBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(title))
        {
            MessageBox.Show(this, "Please enter a broadcast title.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _actionBtn.IsEnabled = false;
        _statusText.Text = "Creating broadcast and stream on YouTube...";

        try
        {
            var token = await _oauthClient.GetValidAccessTokenAsync(_account);
            var privacy = _privacyCombo.SelectedItem?.ToString() ?? "unlisted";
            var startTime = _startNowRadio.IsChecked == true
                ? DateTimeOffset.UtcNow
                : (_datePicker.SelectedDate ?? DateTime.Today).ToUniversalTime();

            var madeForKids = _forKidsRadio.IsChecked == true;
            var dvr = _dvrCheck.IsChecked == true;
            var autoStart = _autoStartCheck.IsChecked == true;
            var autoStop = _autoStopCheck.IsChecked == true;

            // 1. Create Broadcast
            var broadcast = await _apiClient.CreateBroadcastAsync(
                token, title, _descBox.Text.Trim(), privacy, startTime,
                madeForKids, autoStart, autoStop, dvr);

            _statusText.Text = "Creating YouTube live stream resource...";

            // 2. Create Live Stream
            var resChoice = _resCombo.SelectedItem?.ToString() ?? "1080p 60fps";
            var resolution = resChoice.Contains("720p") ? "720p" : "1080p";
            var frameRate = resChoice.Contains("30fps") ? "30fps" : "60fps";

            var stream = await _apiClient.CreateLiveStreamAsync(token, $"{title} Stream", resolution, frameRate);

            _statusText.Text = "Binding broadcast to stream...";

            // 3. Bind Broadcast to Stream
            var boundBroadcast = await _apiClient.BindBroadcastAsync(token, broadcast.Id, stream.Id);

            ResultBroadcast = boundBroadcast;
            ResultStream = stream;
            IngestionAddress = stream.Cdn?.IngestionInfo?.RtmpsIngestionAddress
                ?? stream.Cdn?.IngestionInfo?.IngestionAddress
                ?? "rtmp://a.rtmp.youtube.com/live2";
            StreamKey = stream.Cdn?.IngestionInfo?.StreamName;

            AppLog.Write("YouTube", $"Created & bound YouTube broadcast '{title}' (ID: {broadcast.Id}, Stream: {stream.Id})");

            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            _statusText.Text = $"Creation error: {ex.Message}";
            _actionBtn.IsEnabled = true;
        }
    }

    private async Task ExecuteSelectExistingAsync()
    {
        if (_broadcastList.SelectedIndex < 0 || _broadcastList.SelectedIndex >= _existingBroadcasts.Count)
        {
            MessageBox.Show(this, "Please select a broadcast from the list.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var selected = _existingBroadcasts[_broadcastList.SelectedIndex];
        _actionBtn.IsEnabled = false;
        _statusText.Text = "Retrieving stream configuration for selected broadcast...";

        try
        {
            var token = await _oauthClient.GetValidAccessTokenAsync(_account);

            string? streamId = selected.ContentDetails?.BoundStreamId;
            YouTubeLiveStreamItem? stream = null;

            if (!string.IsNullOrEmpty(streamId))
            {
                var streams = await _apiClient.ListStreamsAsync(token);
                stream = streams.FirstOrDefault(s => s.Id == streamId);
            }

            if (stream == null)
            {
                _statusText.Text = "Creating and binding a new live stream for this broadcast...";
                stream = await _apiClient.CreateLiveStreamAsync(token, $"{selected.Snippet?.Title} Stream");
                selected = await _apiClient.BindBroadcastAsync(token, selected.Id, stream.Id);
            }

            ResultBroadcast = selected;
            ResultStream = stream;
            IngestionAddress = stream.Cdn?.IngestionInfo?.RtmpsIngestionAddress
                ?? stream.Cdn?.IngestionInfo?.IngestionAddress
                ?? "rtmp://a.rtmp.youtube.com/live2";
            StreamKey = stream.Cdn?.IngestionInfo?.StreamName;

            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            _statusText.Text = $"Failed to bind broadcast: {ex.Message}";
            _actionBtn.IsEnabled = true;
        }
    }
}
