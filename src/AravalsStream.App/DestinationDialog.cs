using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AravalsStream.Core.Models;
using AravalsStream.Core.Recording.Models;

namespace AravalsStream.App.Streaming;

public sealed class DestinationDialog : Window
{
    private readonly TextBox _name = new();
    private readonly TextBox _server = new();
    private readonly PasswordBox _key = new();
    private readonly TextBox _visibleKey = new() { Visibility = Visibility.Collapsed };
    private readonly string? _initialKey;
    private readonly TextBox _video = new();
    private readonly TextBox _audio = new();
    private readonly TextBox _keyframe = new();
    private readonly ComboBox _mode = new();
    private readonly ComboBox _fps = new();
    private readonly ComboBox _encoder = new();
    private readonly ComboBox _protocol = new();
    private readonly CheckBox _reconnect = new() { Content = "Auto reconnect" };
    private readonly TextBlock _warning = new() { Foreground = Brushes.Orange, TextWrapping = TextWrapping.Wrap };
    public Destination Result { get; }
    public string? ReplacementKey { get; private set; }
    public bool ClearKey { get; private set; }
    public DestinationDialog(Window owner, Destination destination, IReadOnlyList<EncoderInfo> encoders, string? storedKey)
    {
        Owner = owner; Title = "Destination Settings"; Width = 480; Height = 760;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; Background = new SolidColorBrush(Color.FromRgb(27, 29, 36));
        Foreground = Brushes.White; Result = destination.Copy(); _initialKey = storedKey;
        var panel = new StackPanel { Margin = new Thickness(22) };
        var scroll = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Content = scroll;
        Add(panel, "Name", _name); _name.Text = Result.Name;
        Add(panel, "Protocol", _protocol); _protocol.ItemsSource = new[] { "RTMP", "RTMPS" }; _protocol.SelectedItem = Result.Protocol;
        Add(panel, "Server URL", _server); _server.Text = Result.StreamUrl;
        Add(panel, "Stream Key", _key); _key.Password = storedKey ?? "";
        _visibleKey.MinHeight = 27; panel.Children.Add(_visibleKey);
        var showKey = new Button { Content = "Show", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 0) };
        showKey.Click += (_, _) =>
        {
            if (_visibleKey.Visibility == Visibility.Collapsed)
            { _visibleKey.Text = _key.Password; _key.Visibility = Visibility.Collapsed; _visibleKey.Visibility = Visibility.Visible; showKey.Content = "Hide"; }
            else
            { _key.Password = _visibleKey.Text; _key.Visibility = Visibility.Visible; _visibleKey.Visibility = Visibility.Collapsed; showKey.Content = "Show"; }
        }; panel.Children.Add(showKey);
        var clear = new CheckBox { Content = "Clear saved key (disables destination)", Margin = new Thickness(0, 5, 0, 8) };
        clear.Checked += (_, _) => ClearKey = true; clear.Unchecked += (_, _) => ClearKey = false; panel.Children.Add(clear);
        Add(panel, "Output", _mode); _mode.ItemsSource = new[] { OutputMode.Horizontal, OutputMode.Vertical }; _mode.SelectedItem = Result.OutputMode == OutputMode.Vertical ? OutputMode.Vertical : OutputMode.Horizontal;
        Add(panel, "Encoder", _encoder);
        var choices = new List<EncoderInfo> { new("auto", "Auto", false, "", true, false) };
        choices.AddRange(encoders.Where(e => e.Available));
        _encoder.ItemsSource = choices; _encoder.DisplayMemberPath = nameof(EncoderInfo.DisplayName);
        _encoder.SelectedItem = choices.FirstOrDefault(e => e.Id == Result.EncoderId) ?? choices[0];
        Add(panel, "FPS", _fps); _fps.ItemsSource = new[] { 30, 60 }; _fps.SelectedItem = Result.FrameRate;
        Add(panel, "Video bitrate (kbps)", _video); _video.Text = Result.VideoBitrateKbps.ToString();
        var preset = new ComboBox { ItemsSource = new[] { "Custom", "2500", "4000", "6000", "8000", "10000", "12000" }, SelectedIndex = 0, Margin = new Thickness(0, 4, 0, 4) };
        preset.SelectionChanged += (_, _) => { if (preset.SelectedIndex > 0) _video.Text = (string)preset.SelectedItem; }; panel.Children.Add(preset);
        _video.TextChanged += (_, _) => _warning.Text = int.TryParse(_video.Text, out var value) && value > 12000 ? "⚠ High bitrate may exceed upload bandwidth or platform limits." : "";
        panel.Children.Add(_warning);
        Add(panel, "Audio bitrate (kbps)", _audio); _audio.Text = Result.AudioBitrateKbps.ToString();
        var audioPreset = new ComboBox { ItemsSource = new[] { "Custom", "96", "128", "160", "192", "256", "320" }, SelectedIndex = 0, Margin = new Thickness(0, 4, 0, 4) };
        audioPreset.SelectionChanged += (_, _) => { if (audioPreset.SelectedIndex > 0) _audio.Text = (string)audioPreset.SelectedItem; }; panel.Children.Add(audioPreset);
        Add(panel, "Keyframe interval (seconds)", _keyframe); _keyframe.Text = Result.KeyframeIntervalSeconds.ToString();
        _reconnect.IsChecked = Result.AutoReconnect; _reconnect.Margin = new Thickness(0, 12, 0, 12); panel.Children.Add(_reconnect);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Cancel", Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(0, 0, 8, 0) };
        cancel.Click += (_, _) => DialogResult = false;
        var save = new Button { Content = "Save Destination", Padding = new Thickness(12, 7, 12, 7) };
        save.Click += (_, _) => Save(); buttons.Children.Add(cancel); buttons.Children.Add(save); panel.Children.Add(buttons);
    }
    private static void Add(Panel panel, string label, Control control)
    {
        panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 8, 0, 3), Foreground = Brushes.White });
        control.MinHeight = 27; panel.Children.Add(control);
    }
    private void Save()
    {
        Result.Name = _name.Text.Trim(); Result.Protocol = (string)(_protocol.SelectedItem ?? "RTMP");
        Result.StreamUrl = _server.Text.Trim(); Result.OutputMode = (OutputMode)(_mode.SelectedItem ?? OutputMode.Horizontal);
        Result.EncoderId = ((EncoderInfo)_encoder.SelectedItem).Id;
        Result.FrameRate = (int)(_fps.SelectedItem ?? 60);
        Result.VideoBitrateKbps = int.TryParse(_video.Text, out var v) ? v : 0;
        Result.AudioBitrateKbps = int.TryParse(_audio.Text, out var a) ? a : 0;
        Result.KeyframeIntervalSeconds = int.TryParse(_keyframe.Text, out var k) ? k : 0;
        Result.AutoReconnect = _reconnect.IsChecked == true;
        var enteredKey = _visibleKey.Visibility == Visibility.Visible ? _visibleKey.Text : _key.Password;
        ReplacementKey = string.IsNullOrWhiteSpace(enteredKey) || enteredKey == _initialKey ? null : enteredKey;
        if (ClearKey && ReplacementKey == null) Result.Enabled = false;
        var error = DestinationValidation.Validate(Result, ClearKey || ReplacementKey != null || Result.StreamKeyReference != null);
        if (error != null) { MessageBox.Show(this, error, "Invalid destination"); return; }
        Result.UpdatedAt = DateTimeOffset.UtcNow; DialogResult = true;
    }
}




