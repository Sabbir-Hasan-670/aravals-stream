using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using AravalsStream.Core.Models;
using AravalsStream.Core.Platforms;
using AravalsStream.Core.Recording.Models;

namespace AravalsStream.App.Streaming;

public sealed class PlatformDestinationDialog : Window
{
    private readonly TextBox _name = new();
    private readonly TextBox _server = new();
    private readonly PasswordBox _key = new();
    private readonly TextBox _visibleKey = new() { Visibility = Visibility.Collapsed };
    private readonly string? _initialKey;
    private readonly ComboBox _protocol = new();
    private readonly ComboBox _routing = new();
    private readonly CheckBox _linkSettings = new() { Content = "Link settings between Horizontal and Vertical" };
    private readonly CheckBox _reconnect = new() { Content = "Auto reconnect" };

    // Horizontal controls
    private readonly Border _hPanelBorder = new();
    private readonly TextBox _hVideo = new();
    private readonly ComboBox _hFps = new();
    private readonly ComboBox _hEncoder = new();
    private readonly TextBox _hKeyframe = new();

    // Vertical controls
    private readonly Border _vPanelBorder = new();
    private readonly TextBox _vVideo = new();
    private readonly ComboBox _vFps = new();
    private readonly ComboBox _vEncoder = new();
    private readonly TextBox _vKeyframe = new();

    private readonly TextBlock _warning = new() { Foreground = Brushes.Orange, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };

    private readonly PlatformProfile _profile;
    private bool _isUpdatingLinked;

    public PlatformDestinationGroup Result { get; }
    public string? ReplacementKey { get; private set; }
    public bool ClearKey { get; private set; }

    public PlatformDestinationDialog(Window owner, PlatformDestinationGroup group, IReadOnlyList<EncoderInfo> encoders, string? storedKey)
    {
        Owner = owner;
        Title = $"{group.Platform} Destination Settings";
        Width = 540;
        Height = 780;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(27, 29, 36));
        Foreground = Brushes.White;

        Result = group.Copy();
        Result.EnsureChildDestinations();
        _initialKey = storedKey;
        _profile = PlatformRegistry.Get(Result.PlatformType);

        var rootScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var panel = new StackPanel { Margin = new Thickness(24) };
        rootScroll.Content = panel;
        Content = rootScroll;

        // Platform Header with Icon and Brand Accent
        var header = CreateHeader();
        panel.Children.Add(header);

        // Name
        Add(panel, "Destination Name", _name);
        _name.Text = Result.Name;

        // Protocol
        Add(panel, "Protocol", _protocol);
        _protocol.ItemsSource = new[] { "RTMP", "RTMPS" };
        _protocol.SelectedItem = Result.Horizontal.Protocol ?? _profile.DefaultProtocol;

        // Server URL
        Add(panel, "Server URL", _server);
        _server.Text = !string.IsNullOrWhiteSpace(Result.ServerUrl) ? Result.ServerUrl :
            (Result.PlatformType == PlatformType.Kick || Result.PlatformType == PlatformType.Facebook) && Result.ConfigurationMode == ConfigurationMode.NativeApi
                ? "" : _profile.DefaultServerUrl;

        // Native Twitch credentials are resolved from the account at stream start.
        if (Result.ConfigurationMode == ConfigurationMode.NativeApi && Result.PlatformType == PlatformType.Twitch)
            panel.Children.Add(new TextBlock { Text = "Stream key: managed securely by the connected Twitch account",
                Foreground = Brushes.LightGray, Margin = new Thickness(0, 4, 0, 8) });
        else Add(panel, Result.PlatformType == PlatformType.Kick && Result.ConfigurationMode == ConfigurationMode.NativeApi
            ? "Stream Key (Kick API or secure manual entry)" :
            Result.PlatformType == PlatformType.Facebook && Result.ConfigurationMode == ConfigurationMode.NativeApi
                ? "Stream Key (created securely by Meta at Start)" : "Stream Key", _key);
        _key.Password = storedKey ?? "";
        _visibleKey.MinHeight = 27;
        if (Result.ConfigurationMode != ConfigurationMode.NativeApi || Result.PlatformType != PlatformType.Twitch)
            panel.Children.Add(_visibleKey);

        var keyActionGrid = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        keyActionGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        keyActionGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var showKey = new Button { Content = "Show", Padding = new Thickness(10, 2, 10, 2) };
        showKey.Click += (_, _) =>
        {
            if (_visibleKey.Visibility == Visibility.Collapsed)
            {
                _visibleKey.Text = _key.Password;
                _key.Visibility = Visibility.Collapsed;
                _visibleKey.Visibility = Visibility.Visible;
                showKey.Content = "Hide";
            }
            else
            {
                _key.Password = _visibleKey.Text;
                _key.Visibility = Visibility.Visible;
                _visibleKey.Visibility = Visibility.Collapsed;
                showKey.Content = "Show";
            }
        };
        Grid.SetColumn(showKey, 0);
        keyActionGrid.Children.Add(showKey);

        var clear = new CheckBox
        {
            Content = "Clear saved key",
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(8, 0, 0, 0)
        };
        clear.Checked += (_, _) => ClearKey = true;
        clear.Unchecked += (_, _) => ClearKey = false;
        Grid.SetColumn(clear, 1);
        keyActionGrid.Children.Add(clear);
        if (Result.ConfigurationMode != ConfigurationMode.NativeApi || Result.PlatformType != PlatformType.Twitch)
            panel.Children.Add(keyActionGrid);

        // Routing Mode
        Add(panel, "Routing Mode", _routing);
        var routingOptions = new List<RoutingMode>();
        if (_profile.SupportsHorizontal) routingOptions.Add(RoutingMode.Horizontal);
        if (_profile.SupportsVertical) routingOptions.Add(RoutingMode.Vertical);
        if (_profile.SupportsHorizontal && _profile.SupportsVertical &&
            !((Result.PlatformType == PlatformType.Twitch || Result.PlatformType == PlatformType.Kick || Result.PlatformType == PlatformType.Facebook || Result.PlatformType == PlatformType.TikTok) &&
                Result.ConfigurationMode == ConfigurationMode.NativeApi))
            routingOptions.Add(RoutingMode.Both);
        routingOptions.Add(RoutingMode.Off);
        _routing.ItemsSource = routingOptions;
        _routing.SelectedItem = routingOptions.Contains(Result.Routing) ? Result.Routing : routingOptions[0];
        _routing.SelectionChanged += (_, _) => UpdateRoutingPanels();

        // Link Settings
        _linkSettings.IsChecked = Result.LinkSettings;
        _linkSettings.Margin = new Thickness(0, 10, 0, 6);
        _linkSettings.Checked += (_, _) => SyncLinkedValues();
        panel.Children.Add(_linkSettings);

        // Available encoders
        var encoderChoices = new List<EncoderInfo> { new("auto", "Auto (Hardware preferred)", false, "", true, false) };
        encoderChoices.AddRange(encoders.Where(e => e.Available));

        // Horizontal Settings Panel
        ConfigureFormatPanel(_hPanelBorder, $"HORIZONTAL OUTPUT ({AravalsStream.Core.Services.CanvasLayout.Size(OutputMode.Horizontal).Width}×{AravalsStream.Core.Services.CanvasLayout.Size(OutputMode.Horizontal).Height})", _hVideo, _hFps, _hEncoder, _hKeyframe,
            Result.Horizontal, encoderChoices, isHorizontal: true);
        panel.Children.Add(_hPanelBorder);

        // Vertical Settings Panel
        ConfigureFormatPanel(_vPanelBorder, $"VERTICAL OUTPUT ({AravalsStream.Core.Services.CanvasLayout.Size(OutputMode.Vertical).Width}×{AravalsStream.Core.Services.CanvasLayout.Size(OutputMode.Vertical).Height})", _vVideo, _vFps, _vEncoder, _vKeyframe,
            Result.Vertical, encoderChoices, isHorizontal: false);
        panel.Children.Add(_vPanelBorder);

        // Wiring linked updates
        WireLinkEvents();

        // Warning block
        panel.Children.Add(_warning);
        UpdateWarnings();

        // Reconnect
        _reconnect.IsChecked = Result.Horizontal.AutoReconnect;
        _reconnect.Margin = new Thickness(0, 10, 0, 14);
        panel.Children.Add(_reconnect);

        // Buttons
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Cancel", Padding = new Thickness(14, 7, 14, 7), Margin = new Thickness(0, 0, 8, 0) };
        cancel.Click += (_, _) => DialogResult = false;
        var save = new Button { Content = "Save Destination", Padding = new Thickness(14, 7, 14, 7) };
        save.Click += (_, _) => Save();
        buttons.Children.Add(cancel);
        buttons.Children.Add(save);
        panel.Children.Add(buttons);

        UpdateRoutingPanels();
    }

    private Grid CreateHeader()
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 16) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var brandColor = (Color)ColorConverter.ConvertFromString(_profile.BrandColor);
        var iconBox = new Viewbox { Width = 26, Height = 26, VerticalAlignment = VerticalAlignment.Center };
        iconBox.Child = new Path { Data = Geometry.Parse(_profile.IconData), Fill = new SolidColorBrush(brandColor) };
        Grid.SetColumn(iconBox, 0);
        grid.Children.Add(iconBox);

        var titleStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        titleStack.Children.Add(new TextBlock { Text = _profile.DisplayName, FontSize = 16, FontWeight = FontWeights.Bold, Foreground = Brushes.White });
        titleStack.Children.Add(new TextBlock { Text = _profile.Description, FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184)) });
        Grid.SetColumn(titleStack, 1);
        grid.Children.Add(titleStack);

        if (!string.IsNullOrEmpty(_profile.DocumentationUrl))
        {
            var docsBtn = new Button
            {
                Content = "Docs ↗",
                Padding = new Thickness(8, 3, 8, 3),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center
            };
            docsBtn.Click += (_, _) =>
            {
                try { Process.Start(new ProcessStartInfo(_profile.DocumentationUrl) { UseShellExecute = true }); } catch { }
            };
            Grid.SetColumn(docsBtn, 2);
            grid.Children.Add(docsBtn);
        }

        return grid;
    }

    private void ConfigureFormatPanel(Border border, string title, TextBox video, ComboBox fps, ComboBox encoder,
        TextBox keyframe, Destination child, List<EncoderInfo> encoders, bool isHorizontal)
    {
        border.Background = new SolidColorBrush(Color.FromRgb(35, 39, 48));
        border.BorderBrush = new SolidColorBrush(Color.FromRgb(55, 61, 75));
        border.BorderThickness = new Thickness(1);
        border.CornerRadius = new CornerRadius(6);
        border.Padding = new Thickness(14, 10, 14, 12);
        border.Margin = new Thickness(0, 8, 0, 8);

        var stack = new StackPanel();
        border.Child = stack;

        var header = new TextBlock
        {
            Text = title,
            FontWeight = FontWeights.Bold,
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(59, 130, 246)),
            Margin = new Thickness(0, 0, 0, 8)
        };
        stack.Children.Add(header);

        // Bitrate row
        stack.Children.Add(new TextBlock { Text = "Video Bitrate (kbps)", FontSize = 11, Margin = new Thickness(0, 4, 0, 2) });
        video.Text = child.VideoBitrateKbps.ToString();
        video.MinHeight = 26;
        stack.Children.Add(video);

        var presets = isHorizontal
            ? new[] { "Preset: Custom", "2500", "4000", "6000", "8000", "10000", "12000" }
            : new[] { "Preset: Custom", "2500", "3500", "4500", "6000" };

        var presetCombo = new ComboBox { ItemsSource = presets, SelectedIndex = 0, Margin = new Thickness(0, 3, 0, 6) };
        presetCombo.SelectionChanged += (_, _) =>
        {
            if (presetCombo.SelectedIndex > 0)
                video.Text = (string)presetCombo.SelectedItem;
        };
        stack.Children.Add(presetCombo);

        // FPS
        stack.Children.Add(new TextBlock { Text = "Frame Rate (FPS)", FontSize = 11, Margin = new Thickness(0, 4, 0, 2) });
        fps.ItemsSource = new[] { 30, 60 };
        fps.SelectedItem = child.FrameRate == 30 ? 30 : 60;
        fps.MinHeight = 26;
        stack.Children.Add(fps);

        // Encoder
        stack.Children.Add(new TextBlock { Text = "Encoder", FontSize = 11, Margin = new Thickness(0, 6, 0, 2) });
        encoder.ItemsSource = encoders;
        encoder.DisplayMemberPath = nameof(EncoderInfo.DisplayName);
        encoder.SelectedItem = encoders.FirstOrDefault(e => e.Id == child.EncoderId) ?? encoders[0];
        encoder.MinHeight = 26;
        stack.Children.Add(encoder);

        // Keyframe
        stack.Children.Add(new TextBlock { Text = "Keyframe Interval (sec)", FontSize = 11, Margin = new Thickness(0, 6, 0, 2) });
        keyframe.Text = child.KeyframeIntervalSeconds > 0 ? child.KeyframeIntervalSeconds.ToString() : "2";
        keyframe.MinHeight = 26;
        stack.Children.Add(keyframe);
    }

    private void UpdateRoutingPanels()
    {
        var mode = _routing.SelectedItem is RoutingMode rm ? rm : RoutingMode.Horizontal;
        _hPanelBorder.Visibility = mode is RoutingMode.Horizontal or RoutingMode.Both ? Visibility.Visible : Visibility.Collapsed;
        _vPanelBorder.Visibility = mode is RoutingMode.Vertical or RoutingMode.Both ? Visibility.Visible : Visibility.Collapsed;
        _linkSettings.Visibility = mode == RoutingMode.Both ? Visibility.Visible : Visibility.Collapsed;
        UpdateWarnings();
    }

    private void WireLinkEvents()
    {
        _hVideo.TextChanged += (_, _) =>
        {
            if (_isUpdatingLinked || _linkSettings.IsChecked != true) { UpdateWarnings(); return; }
            _isUpdatingLinked = true;
            _vVideo.Text = _hVideo.Text;
            _isUpdatingLinked = false;
            UpdateWarnings();
        };

        _vVideo.TextChanged += (_, _) =>
        {
            if (_isUpdatingLinked || _linkSettings.IsChecked != true) { UpdateWarnings(); return; }
            _isUpdatingLinked = true;
            _hVideo.Text = _vVideo.Text;
            _isUpdatingLinked = false;
            UpdateWarnings();
        };

        _hFps.SelectionChanged += (_, _) =>
        {
            if (_isUpdatingLinked || _linkSettings.IsChecked != true) return;
            _isUpdatingLinked = true;
            _vFps.SelectedItem = _hFps.SelectedItem;
            _isUpdatingLinked = false;
        };

        _vFps.SelectionChanged += (_, _) =>
        {
            if (_isUpdatingLinked || _linkSettings.IsChecked != true) return;
            _isUpdatingLinked = true;
            _hFps.SelectedItem = _vFps.SelectedItem;
            _isUpdatingLinked = false;
        };

        _hEncoder.SelectionChanged += (_, _) =>
        {
            if (_isUpdatingLinked || _linkSettings.IsChecked != true) return;
            _isUpdatingLinked = true;
            _vEncoder.SelectedItem = _hEncoder.SelectedItem;
            _isUpdatingLinked = false;
        };

        _vEncoder.SelectionChanged += (_, _) =>
        {
            if (_isUpdatingLinked || _linkSettings.IsChecked != true) return;
            _isUpdatingLinked = true;
            _hEncoder.SelectedItem = _vEncoder.SelectedItem;
            _isUpdatingLinked = false;
        };
    }

    private void SyncLinkedValues()
    {
        if (_linkSettings.IsChecked != true) return;
        _isUpdatingLinked = true;
        _vVideo.Text = _hVideo.Text;
        _vFps.SelectedItem = _hFps.SelectedItem;
        _vEncoder.SelectedItem = _hEncoder.SelectedItem;
        _vKeyframe.Text = _hKeyframe.Text;
        _isUpdatingLinked = false;
        UpdateWarnings();
    }

    private void UpdateWarnings()
    {
        var mode = _routing.SelectedItem is RoutingMode rm ? rm : RoutingMode.Horizontal;
        var warnList = new List<string>();

        if (mode is RoutingMode.Horizontal or RoutingMode.Both &&
            int.TryParse(_hVideo.Text, out var hBitrate) && hBitrate > _profile.MaxVideoBitrateKbps)
        {
            warnList.Add($"⚠ Horizontal bitrate ({hBitrate} kbps) exceeds {_profile.DisplayName} recommended limit of {_profile.MaxVideoBitrateKbps} kbps.");
        }

        if (mode is RoutingMode.Vertical or RoutingMode.Both &&
            int.TryParse(_vVideo.Text, out var vBitrate) && vBitrate > _profile.MaxVideoBitrateKbps)
        {
            warnList.Add($"⚠ Vertical bitrate ({vBitrate} kbps) exceeds {_profile.DisplayName} recommended limit of {_profile.MaxVideoBitrateKbps} kbps.");
        }

        _warning.Text = string.Join("\n", warnList);
        _warning.Visibility = warnList.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static void Add(Panel panel, string label, Control control)
    {
        panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 8, 0, 3), Foreground = Brushes.White });
        control.MinHeight = 27;
        panel.Children.Add(control);
    }

    private void Save()
    {
        Result.Name = _name.Text.Trim();
        Result.ServerUrl = _server.Text.Trim();
        Result.Routing = (RoutingMode)(_routing.SelectedItem ?? RoutingMode.Horizontal);
        Result.LinkSettings = _linkSettings.IsChecked == true;

        var proto = (string)(_protocol.SelectedItem ?? "RTMP");
        var autoRec = _reconnect.IsChecked == true;

        // Apply Horizontal
        Result.Horizontal.Name = $"{Result.Name} - H";
        Result.Horizontal.Protocol = proto;
        Result.Horizontal.StreamUrl = Result.ServerUrl;
        Result.Horizontal.OutputMode = OutputMode.Horizontal;
        Result.Horizontal.EncoderId = ((EncoderInfo)_hEncoder.SelectedItem).Id;
        Result.Horizontal.FrameRate = (int)(_hFps.SelectedItem ?? 60);
        Result.Horizontal.VideoBitrateKbps = int.TryParse(_hVideo.Text, out var hv) ? hv : 6000;
        Result.Horizontal.KeyframeIntervalSeconds = int.TryParse(_hKeyframe.Text, out var hk) ? hk : 2;
        Result.Horizontal.AutoReconnect = autoRec;

        // Apply Vertical
        Result.Vertical.Name = $"{Result.Name} - V";
        Result.Vertical.Protocol = proto;
        Result.Vertical.StreamUrl = Result.ServerUrl;
        Result.Vertical.OutputMode = OutputMode.Vertical;
        Result.Vertical.EncoderId = ((EncoderInfo)_vEncoder.SelectedItem).Id;
        Result.Vertical.FrameRate = (int)(_vFps.SelectedItem ?? 30);
        Result.Vertical.VideoBitrateKbps = int.TryParse(_vVideo.Text, out var vv) ? vv : 4000;
        Result.Vertical.KeyframeIntervalSeconds = int.TryParse(_vKeyframe.Text, out var vk) ? vk : 2;
        Result.Vertical.AutoReconnect = autoRec;

        var enteredKey = _visibleKey.Visibility == Visibility.Visible ? _visibleKey.Text : _key.Password;
        ReplacementKey = Result.PlatformType == PlatformType.Twitch && Result.ConfigurationMode == ConfigurationMode.NativeApi
            ? null : string.IsNullOrWhiteSpace(enteredKey) || enteredKey == _initialKey ? null : enteredKey;

        if (ClearKey && ReplacementKey == null)
        {
            Result.Enabled = false;
        }

        Result.EnsureChildDestinations();

        var hasKey = Result.ConfigurationMode == ConfigurationMode.NativeApi &&
            (Result.PlatformType == PlatformType.Twitch || Result.PlatformType == PlatformType.Facebook)
            || ClearKey || ReplacementKey != null || Result.StreamKeyReference != null;
        var error = DestinationValidation.ValidateGroup(Result, hasKey);
        if (error != null)
        {
            MessageBox.Show(this, error, "Invalid destination settings", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Result.UpdatedAt = DateTimeOffset.UtcNow;
        DialogResult = true;
    }
}
