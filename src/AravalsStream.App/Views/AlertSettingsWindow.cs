using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.IO;
using AravalsStream.Core.Alerts;
using AravalsStream.Core.Models;

namespace AravalsStream.App.Views;

public sealed class AlertSettingsWindow : Window
{
    private readonly AlertSettings _settings;
    private readonly Action<StreamEvent> _test;
    private readonly ListBox _types = new();
    private readonly TextBox _title = new(), _message = new(), _duration = new();
    private readonly TextBox _font = new(), _fontSize = new(), _enter = new(), _exit = new(),
        _background = new(), _padding = new(), _corner = new(), _volume = new(), _sound = new(), _image = new();
    private readonly TextBox _hX = new(), _hY = new(), _hWidth = new(),
        _vX = new(), _vY = new(), _vWidth = new(), _queueMax = new(), _queueGap = new(),
        _cooldown = new(), _groupWindow = new();
    private readonly CheckBox _group = new() { Content = "Group follower bursts" };
    private readonly ComboBox _animation = new(), _position = new(), _alignment = new();
    private readonly CheckBox _enabled = new() { Content = "Enabled" },
        _horizontal = new() { Content = "Horizontal" }, _vertical = new() { Content = "Vertical" },
        _monitor = new() { Content = "Monitor sound" }, _record = new() { Content = "Record sound" };
    private AlertDefinition? _current;
    private readonly Dictionary<string, CheckBox> _soundPlatforms = new(StringComparer.OrdinalIgnoreCase);

    public AlertSettingsWindow(AlertSettings settings, Action<StreamEvent> test)
    {
        _settings = settings; _test = test;
        Title = "Alert Settings"; Width = 900; Height = 600; MinWidth = 720; MinHeight = 480;
        Background = new SolidColorBrush(Color.FromRgb(18, 26, 38)); Foreground = Brushes.White;
        AravalsStream.App.Controls.DarkWindowChrome.Apply(this);
        var root = new Grid { Margin = new Thickness(18) };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(270) });
        Content = root;
        _types.ItemsSource = settings.Definitions;
        _types.DisplayMemberPath = "EventType";
        _types.SelectionChanged += (_, _) => { if (_current != null) SaveSelected(_current); _current = _types.SelectedItem as AlertDefinition; LoadSelected(); };
        Grid.SetColumn(_types, 0); root.Children.Add(_types);

        var preview = new StackPanel { Margin = new Thickness(18) };
        Grid.SetColumn(preview, 1); root.Children.Add(preview);
        preview.Children.Add(new TextBlock { Text = "PREVIEW", Foreground = Brushes.LightCyan, FontSize = 15 });
        var sample = new Border { Background = new SolidColorBrush(Color.FromRgb(27, 48, 63)),
            CornerRadius = new CornerRadius(18), Padding = new Thickness(25), Margin = new Thickness(0, 45, 0, 20) };
        var sampleText = new TextBlock { Text = "Sabbir\njust subscribed!", FontSize = 30,
            TextAlignment = TextAlignment.Center, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap };
        sample.Child = sampleText; preview.Children.Add(sample);
        _types.SelectionChanged += (_, _) => sampleText.Text = _types.SelectedItem is AlertDefinition d
            ? AlertTemplate.Render(d.TitleTemplate, TestEvent(d.EventType)) + "\n" +
              AlertTemplate.Render(d.MessageTemplate, TestEvent(d.EventType)) : "";
        var testButton = new Button { Content = "TEST ALERT", Padding = new Thickness(12), Margin = new Thickness(0, 8, 0, 0) };
        testButton.Click += (_, _) => { SaveSelected(); if (_types.SelectedItem is AlertDefinition d) _test(TestEvent(d.EventType)); };
        preview.Children.Add(testButton);

        var form = new StackPanel { Margin = new Thickness(12, 0, 0, 0) };
        var scroll = new ScrollViewer { Content = form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetColumn(scroll, 2); root.Children.Add(scroll);
        form.Children.Add(new TextBlock { Text = "SETTINGS", Foreground = Brushes.LightCyan, FontSize = 15 });
        form.Children.Add(_enabled);
        AddField(form, "Title template", _title);
        AddField(form, "Message template", _message);
        AddField(form, "Display duration (ms)", _duration);
        AddField(form, "Enter duration (ms)", _enter);
        AddField(form, "Exit duration (ms)", _exit);
        AddField(form, "Font family", _font);
        AddField(form, "Font size", _fontSize);
        form.Children.Add(new TextBlock { Text = "Text alignment", Margin = new Thickness(0, 14, 0, 4) });
        _alignment.ItemsSource = new[] { "Left", "Center", "Right" }; form.Children.Add(_alignment);
        AddField(form, "Background opacity (0-1)", _background);
        AddField(form, "Padding", _padding);
        AddField(form, "Corner radius", _corner);
        form.Children.Add(new TextBlock { Text = "Animation", Margin = new Thickness(0, 14, 0, 4) });
        _animation.ItemsSource = Enum.GetValues<AlertAnimation>(); form.Children.Add(_animation);
        form.Children.Add(new TextBlock { Text = "Position", Margin = new Thickness(0, 14, 0, 4) });
        _position.ItemsSource = Enum.GetValues<AlertPosition>(); form.Children.Add(_position);
        form.Children.Add(_horizontal); form.Children.Add(_vertical);
        AddField(form, "Horizontal X (custom)", _hX);
        AddField(form, "Horizontal Y (custom)", _hY);
        AddField(form, "Horizontal width", _hWidth);
        AddField(form, "Vertical X (custom)", _vX);
        AddField(form, "Vertical Y (custom)", _vY);
        AddField(form, "Vertical width", _vWidth);
        AddField(form, "Sound volume (0-1)", _volume);
        AddField(form, "Sound file", _sound);
        var chooseSound = new Button { Content = "Choose WAV/MP3" };
        chooseSound.Click += (_, _) => ChooseAsset(_sound, "Audio|*.wav;*.mp3"); form.Children.Add(chooseSound);
        form.Children.Add(_monitor); form.Children.Add(_record);
        form.Children.Add(new TextBlock { Text = "Send sound to", Margin = new Thickness(0, 12, 0, 4) });
        foreach (var platform in new[] { "YouTube", "Twitch", "Kick", "Facebook", "TikTok", "Custom" })
        {
            var check = new CheckBox { Content = platform, Margin = new Thickness(0, 2, 0, 2) };
            _soundPlatforms[platform] = check;
            form.Children.Add(check);
        }
        AddField(form, "Image/GIF", _image);
        var chooseImage = new Button { Content = "Choose PNG/JPG/GIF" };
        chooseImage.Click += (_, _) => ChooseAsset(_image, "Images|*.png;*.jpg;*.jpeg;*.gif"); form.Children.Add(chooseImage);
        var chatButton = new Button { Content = "Chat Overlay Settings", Margin = new Thickness(0, 14, 0, 0) };
        chatButton.Click += (_, _) => new ChatOverlaySettingsWindow(_settings) { Owner = this }.ShowDialog();
        form.Children.Add(chatButton);
        form.Children.Add(new TextBlock { Text = "QUEUE", Foreground = Brushes.LightCyan, Margin = new Thickness(0, 18, 0, 0) });
        AddField(form, "Maximum queued alerts", _queueMax);
        AddField(form, "Gap between alerts (ms)", _queueGap);
        AddField(form, "Cooldown (ms)", _cooldown);
        form.Children.Add(_group);
        AddField(form, "Group window (ms)", _groupWindow);
        var save = new Button { Content = "Save", Padding = new Thickness(10), Margin = new Thickness(0, 20, 0, 0) };
        save.Click += (_, _) => { SaveSelected(); DialogResult = true; };
        form.Children.Add(save);
        _queueMax.Text = _settings.MaxQueueLength.ToString();
        _queueGap.Text = _settings.GapMilliseconds.ToString();
        _cooldown.Text = _settings.CooldownMilliseconds.ToString();
        _group.IsChecked = _settings.GroupRepeatedEvents;
        _groupWindow.Text = _settings.GroupWindowMilliseconds.ToString();
        _types.SelectedIndex = 0;
    }

    private static void AddField(Panel parent, string label, TextBox box)
    {
        parent.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 14, 0, 4), Foreground = Brushes.LightGray });
        box.Padding = new Thickness(6); parent.Children.Add(box);
    }

    private void LoadSelected()
    {
        if (_types.SelectedItem is not AlertDefinition d) return;
        _enabled.IsChecked = d.Enabled; _title.Text = d.TitleTemplate; _message.Text = d.MessageTemplate;
        _duration.Text = d.DisplayMilliseconds.ToString(); _horizontal.IsChecked = d.ShowHorizontal;
        _vertical.IsChecked = d.ShowVertical;
        _enter.Text = d.EnterMilliseconds.ToString(); _exit.Text = d.ExitMilliseconds.ToString();
        _font.Text = d.FontFamily; _fontSize.Text = d.FontSize.ToString();
        _alignment.SelectedItem = d.TextAlignment;
        _background.Text = d.BackgroundOpacity.ToString("0.00");
        _padding.Text = d.Padding.ToString("0"); _corner.Text = d.CornerRadius.ToString("0");
        _animation.SelectedItem = d.Animation; _position.SelectedItem = d.Position;
        _volume.Text = d.SoundVolume.ToString("0.00"); _sound.Text = d.SoundPath ?? "";
        _image.Text = d.ImagePath ?? ""; _monitor.IsChecked = d.MonitorSound; _record.IsChecked = d.RecordSound;
        _hX.Text = d.HorizontalX.ToString(); _hY.Text = d.HorizontalY.ToString();
        _hWidth.Text = d.HorizontalWidth.ToString(); _vX.Text = d.VerticalX.ToString();
        _vY.Text = d.VerticalY.ToString(); _vWidth.Text = d.VerticalWidth.ToString();
        foreach (var (platform, check) in _soundPlatforms)
            check.IsChecked = d.SoundPlatforms.Contains(platform, StringComparer.OrdinalIgnoreCase);
    }

    private void SaveSelected()
    {
        if (_current is not { } d) return;
        SaveSelected(d);
    }

    private void SaveSelected(AlertDefinition d)
    {
        d.Enabled = _enabled.IsChecked == true; d.TitleTemplate = _title.Text; d.MessageTemplate = _message.Text;
        if (int.TryParse(_duration.Text, out var ms)) d.DisplayMilliseconds = Math.Clamp(ms, 250, 30000);
        d.ShowHorizontal = _horizontal.IsChecked == true; d.ShowVertical = _vertical.IsChecked == true;
        if (int.TryParse(_enter.Text, out var enter)) d.EnterMilliseconds = Math.Clamp(enter, 0, 3000);
        if (int.TryParse(_exit.Text, out var exit)) d.ExitMilliseconds = Math.Clamp(exit, 0, 3000);
        d.FontFamily = string.IsNullOrWhiteSpace(_font.Text) ? "Segoe UI" : _font.Text;
        if (int.TryParse(_fontSize.Text, out var fontSize)) d.FontSize = Math.Clamp(fontSize, 14, 72);
        d.TextAlignment = _alignment.SelectedItem?.ToString() ?? "Center";
        if (double.TryParse(_background.Text, out var bg)) d.BackgroundOpacity = Math.Clamp(bg, 0, 1);
        if (double.TryParse(_padding.Text, out var pad)) d.Padding = Math.Clamp(pad, 0, 100);
        if (double.TryParse(_corner.Text, out var corner)) d.CornerRadius = Math.Clamp(corner, 0, 100);
        if (_animation.SelectedItem is AlertAnimation animation) d.Animation = animation;
        if (_position.SelectedItem is AlertPosition position) d.Position = position;
        if (double.TryParse(_volume.Text, out var volume)) d.SoundVolume = Math.Clamp(volume, 0, 1);
        d.SoundPath = string.IsNullOrWhiteSpace(_sound.Text) ? null : _sound.Text;
        d.ImagePath = string.IsNullOrWhiteSpace(_image.Text) ? null : _image.Text;
        d.MonitorSound = _monitor.IsChecked == true; d.RecordSound = _record.IsChecked == true;
        d.SoundPlatforms = _soundPlatforms.Where(x => x.Value.IsChecked == true).Select(x => x.Key).ToList();
        if (int.TryParse(_hX.Text, out var hX)) d.HorizontalX = Math.Clamp(hX, -1800, 1800);
        if (int.TryParse(_hY.Text, out var hY)) d.HorizontalY = Math.Clamp(hY, -900, 900);
        if (int.TryParse(_hWidth.Text, out var hW)) d.HorizontalWidth = Math.Clamp(hW, 200, 1800);
        if (int.TryParse(_vX.Text, out var vX)) d.VerticalX = Math.Clamp(vX, -900, 900);
        if (int.TryParse(_vY.Text, out var vY)) d.VerticalY = Math.Clamp(vY, -1800, 1800);
        if (int.TryParse(_vWidth.Text, out var vW)) d.VerticalWidth = Math.Clamp(vW, 200, 1000);
        if (int.TryParse(_queueMax.Text, out var max)) _settings.MaxQueueLength = Math.Clamp(max, 1, 100);
        if (int.TryParse(_queueGap.Text, out var gap)) _settings.GapMilliseconds = Math.Clamp(gap, 0, 10000);
        if (int.TryParse(_cooldown.Text, out var cooldown)) _settings.CooldownMilliseconds = Math.Clamp(cooldown, 0, 60000);
        _settings.GroupRepeatedEvents = _group.IsChecked == true;
        if (int.TryParse(_groupWindow.Text, out var window)) _settings.GroupWindowMilliseconds = Math.Clamp(window, 100, 60000);
    }

    private void ChooseAsset(TextBox box, string filter)
    {
        var picker = new Microsoft.Win32.OpenFileDialog { Filter = filter, CheckFileExists = true };
        if (picker.ShowDialog(this) != true) return;
        if (new FileInfo(picker.FileName).Length > 20 * 1024 * 1024)
        { MessageBox.Show(this, "Choose an asset smaller than 20 MB."); return; }
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AravalsStream", "alert-assets");
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, Guid.NewGuid().ToString("N") + Path.GetExtension(picker.FileName).ToLowerInvariant());
        File.Copy(picker.FileName, destination);
        box.Text = destination;
    }

    private static StreamEvent TestEvent(StreamEventType type) => new()
    {
        Platform = type == StreamEventType.PaidMessage ? "YouTube" : "Twitch",
        EventType = type, ActorId = "simulated", ActorName = "Sabbir", Quantity = 5,
        Amount = 5, Currency = "USD", IsSimulated = true, Message = "Test alert"
    };
}
