using AravalsStream.Platform;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using AravalsStream.Core.Models;
using System.Globalization;

namespace AravalsStream.Desktop;

// Development workspace: native capture/encode is real; feature parity is tracked separately.
public sealed class WorkspaceWindow : Window
{
    private readonly ComboBox _source = new() { ItemsSource = new[] { "Display", "Camera", "Image", "Video", "Test pattern" }, SelectedIndex = 0 };
    private readonly TextBox _device = new();
    private readonly TextBox _ffmpeg = new() { Watermark = "FFmpeg executable path" };
    private readonly TextBox _microphone = new() { Watermark = "Microphone device name/index (optional)" };
    private readonly TextBox _desktopAudio = new() { Watermark = "PulseAudio monitor source (Linux only)" };
    private readonly ComboBox _canvas = new() { ItemsSource = new[] { "Horizontal · 1920×1080", "Vertical · 1080×1920" }, SelectedIndex = 0 };
    private readonly TextBox _server = new() { Watermark = "rtmps://server/application" };
    private readonly TextBox _key = new() { Watermark = "Stream key (kept in memory only)", PasswordChar = '●' };
    private readonly TextBox _recordPath = new() { Watermark = "Recording .mkv file path" };
    private readonly TextBlock _status = new() { Text = "Ready", TextWrapping = TextWrapping.Wrap };
    private readonly ComboBox _scenes = new();
    private readonly TextBox _sceneName = new() { Watermark = "Scene name" };
    private readonly ListBox _layers = new() { Height = 100 };
    private readonly TextBox _x = new() { Text = "0" }, _y = new() { Text = "0" }, _width = new() { Text = "1920" }, _height = new() { Text = "1080" }, _opacity = new() { Text = "1" }, _rotation = new() { Text = "0" };
    private readonly CheckBox _visible = new() { Content = "Visible", IsChecked = true };
    private SceneWorkspace _workspace = SceneWorkspace.Create();
    private readonly string _workspacePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AravalsStream", "portable", "scenes.json");
    private bool _refreshing;
    private bool _loading = true;
    private readonly Image _image = new() { Stretch = Stretch.Uniform };
    private FfmpegProcess? _preview;
    private FfmpegProcess? _stream;
    private FfmpegProcess? _record;
    private CancellationTokenSource? _previewToken;
    private Task? _previewTask;
    private bool _busy;
    private bool _closing;
    private readonly DispatcherTimer _timer;

    public WorkspaceWindow()
    {
        Title = "Aravals Stream — Cross-platform development"; Width = 1240; Height = 820; MinWidth = 820; MinHeight = 650;
        Background = Brush.Parse("#0B121C");
        _device.Text = PlatformCapture.Current switch { DesktopPlatform.Windows => "desktop", DesktopPlatform.LinuxX11 => Environment.GetEnvironmentVariable("DISPLAY") ?? ":0.0", _ => "" };
        try { _ffmpeg.Text = FfmpegProcess.FindExecutable(); } catch (FileNotFoundException ex) { _status.Text = ex.Message; }
        _recordPath.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Aravals-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".mkv");
        var root = new Grid { RowDefinitions = new("Auto,*,Auto"), Margin = new Thickness(22), RowSpacing = 18 };
        var heading = new StackPanel { Spacing = 5 };
        heading.Children.Add(new TextBlock { Text = "ARAVALS STREAM", FontSize = 26, FontWeight = FontWeight.Bold, Foreground = Brush.Parse("#25D2AA") });
        heading.Children.Add(new TextBlock { Text = $"{PlatformCapture.Current} · Development port · Feature parity and native-platform acceptance pending", Foreground = Brushes.LightGray });
        root.Children.Add(heading);
        var body = new Grid { ColumnDefinitions = new("330,*"), ColumnSpacing = 22 }; Grid.SetRow(body, 1);
        var tools = new StackPanel { Spacing = 10 };
        AddField(tools, "SCENES", _scenes); tools.Children.Add(_sceneName);
        tools.Children.Add(Button("Add scene", AddScene)); tools.Children.Add(Button("Rename scene", RenameScene));
        AddField(tools, "SOURCES", _layers);
        AddField(tools, "VIDEO SOURCE", _source); AddField(tools, "Device name/index or file path", _device);
        tools.Children.Add(Button("Add source to scene", AddSource)); tools.Children.Add(Button("Remove selected source", RemoveSource));
        AddField(tools, "CANVAS", _canvas);
        var geometry = new Grid { ColumnDefinitions = new("*,*"), RowDefinitions = new("Auto,Auto,Auto"), ColumnSpacing = 8, RowSpacing = 8 };
        AddGeometry(geometry, "X", _x, 0, 0); AddGeometry(geometry, "Y", _y, 1, 0);
        AddGeometry(geometry, "Width", _width, 0, 1); AddGeometry(geometry, "Height", _height, 1, 1);
        AddGeometry(geometry, "Opacity · 0–1", _opacity, 0, 2); AddGeometry(geometry, "Rotation", _rotation, 1, 2);
        tools.Children.Add(geometry); tools.Children.Add(_visible); tools.Children.Add(Button("Apply source transform", ApplyTransform));
        AddField(tools, "MICROPHONE", _microphone); AddField(tools, "DESKTOP AUDIO", _desktopAudio);
        AddField(tools, "FFMPEG", _ffmpeg); AddField(tools, "STREAM SERVER", _server); tools.Children.Add(_key);
        var preview = Button("Start preview", TogglePreview); var stream = Button("Start / stop stream", ToggleStream);
        tools.Children.Add(preview); tools.Children.Add(stream); AddField(tools, "RECORDING", _recordPath); tools.Children.Add(Button("Start / stop recording", ToggleRecording));
        body.Children.Add(new ScrollViewer { Content = tools });
        var previewArea = new Grid { RowDefinitions = new("*,Auto"), RowSpacing = 12 }; Grid.SetColumn(previewArea, 1);
        previewArea.Children.Add(new Border { Background = Brushes.Black, CornerRadius = new CornerRadius(10), Child = _image });
        var note = new TextBlock { Text = "Preview and outputs use actual FFmpeg capture. macOS display capture requires its enumerated screen device index and screen-recording permission. Wayland portal capture, native loopback and the remaining workstation features are still being ported.", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.LightGray };
        Grid.SetRow(note, 1); previewArea.Children.Add(note); body.Children.Add(previewArea); root.Children.Add(body);
        Grid.SetRow(_status, 2); root.Children.Add(_status); Content = root;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) =>
        {
            if (_stream?.HasExited == true || _record?.HasExited == true) _status.Text = "An output stopped. Check source permissions and server/device availability.";
            else if (_preview?.HasExited == true) _status.Text = "Preview capture stopped. Check the selected device and native capture permissions.";
        };
        _timer.Start();
        RefreshScenes();
        _scenes.SelectionChanged += async (_, _) =>
        {
            if (_refreshing) return;
            if (_stream is not null || _record is not null) { _status.Text = "Stop outputs before switching scenes in this development workspace."; RefreshScenes(); return; }
            if (_scenes.SelectedItem is NativeScene scene) { _workspace.SelectedSceneId = scene.Id; RefreshLayers(); await SaveWorkspace(); await RestartPreview(); }
        };
        _layers.SelectionChanged += (_, _) => LoadTransform();
        _canvas.SelectionChanged += (_, _) => LoadTransform();
        Opened += async (_, _) =>
        {
            try { _workspace = await SceneWorkspace.LoadAsync(_workspacePath); RefreshScenes(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { _status.Text = "Saved scene workspace could not be loaded; it will not be overwritten."; _workspaceWritable = false; }
            finally { _loading = false; }
        };
        Closing += async (_, args) =>
        {
            if (_closing) return;
            args.Cancel = true; _closing = true; _timer.Stop();
            try { await StopPreview(); if (_stream is not null) await _stream.DisposeAsync(); if (_record is not null) await _record.DisposeAsync(); }
            finally { _key.Text = ""; (_image.Source as IDisposable)?.Dispose(); Close(); }
        };
    }

    private Button Button(string text, Func<Task> action)
    {
        var button = new Button { Content = text, HorizontalAlignment = HorizontalAlignment.Stretch };
        button.Click += async (_, _) =>
        {
            if (_busy) return;
            _busy = true; button.IsEnabled = false;
            try { await action(); }
            catch (Exception ex) when (ex is ArgumentException or IOException or PlatformNotSupportedException or System.ComponentModel.Win32Exception or InvalidOperationException)
            { _status.Text = ex is System.ComponentModel.Win32Exception ? "FFmpeg could not start. Verify its executable path." : ex.Message; }
            finally { _busy = false; button.IsEnabled = true; }
        };
        return button;
    }

    private static void AddField(StackPanel parent, string label, Control input)
    { parent.Children.Add(new TextBlock { Text = label, FontSize = 12, Foreground = Brushes.LightGray }); parent.Children.Add(input); }
    private static void AddGeometry(Grid grid, string label, Control input, int column, int row)
    { var stack = new StackPanel { Spacing = 4 }; AddField(stack, label, input); Grid.SetColumn(stack, column); Grid.SetRow(stack, row); grid.Children.Add(stack); }
    private bool _workspaceWritable = true;
    private NativeScene Scene => _workspace.Scenes.First(s => s.Id == _workspace.SelectedSceneId);
    private void RefreshScenes()
    {
        _refreshing = true; _scenes.ItemsSource = _workspace.Scenes.ToArray(); _scenes.SelectedItem = Scene; _refreshing = false; RefreshLayers();
    }
    private void RefreshLayers()
    { var selected = _layers.SelectedItem as NativeSceneSource; _layers.ItemsSource = Scene.Sources.ToArray(); _layers.SelectedItem = selected is not null && Scene.Sources.Contains(selected) ? selected : Scene.Sources.FirstOrDefault(); }
    private Task SaveWorkspace() => _workspaceWritable ? _workspace.SaveAsync(_workspacePath) : throw new IOException("Saved workspace could not be loaded. These edits cannot be saved without replacing that file.");
    private void RequireEditable()
    { if (_loading) throw new InvalidOperationException("Scene workspace is loading."); if (_stream is not null || _record is not null) throw new InvalidOperationException("Stop outputs before editing scenes in this development workspace."); }
    private async Task AddScene()
    {
        RequireEditable(); var scene = new NativeScene { Name = string.IsNullOrWhiteSpace(_sceneName.Text) ? "Scene " + (_workspace.Scenes.Count + 1) : _sceneName.Text.Trim() };
        _workspace.Scenes.Add(scene); _workspace.SelectedSceneId = scene.Id; RefreshScenes(); await SaveWorkspace(); await RestartPreview();
    }
    private async Task RenameScene()
    { if (string.IsNullOrWhiteSpace(_sceneName.Text)) throw new ArgumentException("Enter a scene name."); Scene.Name = _sceneName.Text.Trim(); RefreshScenes(); await SaveWorkspace(); }
    private async Task AddSource()
    {
        RequireEditable(); var input = Input(); PlatformCapture.Arguments(PlatformCapture.Current, input, 30);
        var first = Scene.Sources.Count == 0;
        var source = new NativeSceneSource { Name = input.Kind + " " + (Scene.Sources.Count + 1), Input = input,
            Horizontal = new() { Width = first ? 1920 : 640, Height = first ? 1080 : 360 },
            Vertical = new() { Width = first ? 1080 : 640, Height = first ? 1920 : 360 } };
        Scene.Sources.Add(source); RefreshLayers(); _layers.SelectedItem = source; await SaveWorkspace(); await RestartPreview();
    }
    private async Task RemoveSource()
    { RequireEditable(); if (_layers.SelectedItem is NativeSceneSource source) { Scene.Sources.Remove(source); RefreshLayers(); await SaveWorkspace(); await RestartPreview(); } }
    private SourceTransform? CurrentTransform => _layers.SelectedItem is NativeSceneSource source ? (_canvas.SelectedIndex == 1 ? source.Vertical : source.Horizontal) : null;
    private void LoadTransform()
    {
        if (CurrentTransform is not { } t) return;
        _x.Text = t.X.ToString(CultureInfo.InvariantCulture); _y.Text = t.Y.ToString(CultureInfo.InvariantCulture);
        _width.Text = t.Width.ToString(CultureInfo.InvariantCulture); _height.Text = t.Height.ToString(CultureInfo.InvariantCulture);
        _opacity.Text = t.Opacity.ToString(CultureInfo.InvariantCulture); _rotation.Text = t.Rotation.ToString(CultureInfo.InvariantCulture);
        _visible.IsChecked = (_layers.SelectedItem as NativeSceneSource)?.Visible;
    }
    private async Task ApplyTransform()
    {
        RequireEditable(); if (CurrentTransform is not { } t) throw new ArgumentException("Choose a scene source.");
        static double Number(TextBox box) => double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && double.IsFinite(n) ? n : throw new ArgumentException("Enter valid numeric transform values.");
        var next = t.Clone(); next.X = Number(_x); next.Y = Number(_y); next.Width = Number(_width); next.Height = Number(_height); next.Opacity = Number(_opacity); next.Rotation = Number(_rotation);
        var source = (NativeSceneSource)_layers.SelectedItem!;
        var validation = Plan() with { Layers = [new(source.Input, next)] }; MediaArguments.Preview(validation);
        if (_canvas.SelectedIndex == 1) source.Vertical = next; else source.Horizontal = next;
        source.Visible = _visible.IsChecked == true; RefreshLayers(); _layers.SelectedItem = source; await SaveWorkspace(); await RestartPreview();
    }
    private async Task RestartPreview() { if (_preview is not null) { await StopPreview(); await TogglePreview(); } }
    private CaptureInput Input()
    {
        var kind = _source.SelectedIndex switch { 1 => CaptureKind.Camera, 2 => CaptureKind.Image, 3 => CaptureKind.Video, 4 => CaptureKind.TestVideo, _ => CaptureKind.Display };
        return new(kind, _device.Text ?? "");
    }

    private MediaPlan Plan()
    {
        var audio = new List<CaptureInput>();
        if (!string.IsNullOrWhiteSpace(_microphone.Text)) audio.Add(new(CaptureKind.Microphone, _microphone.Text));
        if (!string.IsNullOrWhiteSpace(_desktopAudio.Text)) audio.Add(new(CaptureKind.DesktopAudio, _desktopAudio.Text));
        return _workspace.Plan(Scene, PlatformCapture.Current, _canvas.SelectedIndex == 1, audio);
    }

    private async Task ToggleStream()
    {
        if (_stream is not null) { await _stream.DisposeAsync(); _stream = null; _status.Text = "Stream stopped"; return; }
        var url = (_server.Text ?? "").TrimEnd('/') + (string.IsNullOrWhiteSpace(_key.Text) ? "" : "/" + _key.Text);
        _stream = new(_ffmpeg.Text ?? "", MediaArguments.Output(Plan(), url, false));
        _status.Text = "Streaming process started; connection is being established.";
    }
    private async Task ToggleRecording()
    {
        if (_record is not null) { await _record.DisposeAsync(); _record = null; _status.Text = "Recording saved"; return; }
        if (string.IsNullOrWhiteSpace(_recordPath.Text)) throw new ArgumentException("Choose a recording file path.");
        var path = Path.GetFullPath(_recordPath.Text);
        if (File.Exists(path)) throw new ArgumentException("Choose a new recording filename; existing recordings will not be overwritten.");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _record = new(_ffmpeg.Text ?? "", MediaArguments.Output(Plan(), path, true)); _status.Text = "Recording process started";
    }
    private async Task TogglePreview()
    {
        if (_preview is not null) { await StopPreview(); _status.Text = "Preview stopped"; return; }
        _previewToken = new(); _preview = new(_ffmpeg.Text ?? "", MediaArguments.Preview(Plan()));
        _previewTask = ReadPreview(_preview.Video, _previewToken.Token); _status.Text = "Preview capture started";
    }
    private async Task StopPreview()
    {
        _previewToken?.Cancel();
        if (_preview is not null) await _preview.DisposeAsync();
        if (_previewTask is not null) await _previewTask;
        _preview = null; _previewTask = null; _previewToken?.Dispose(); _previewToken = null;
    }
    private async Task ReadPreview(Stream stream, CancellationToken token)
    {
        var buffer = new byte[16384]; using var frame = new MemoryStream(); var previous = -1; var inside = false;
        try
        {
            int length;
            while ((length = await stream.ReadAsync(buffer, token)) > 0)
                for (var i = 0; i < length; i++)
                {
                    var b = buffer[i];
                    if (!inside && previous == 0xff && b == 0xd8) { frame.SetLength(0); frame.WriteByte(0xff); inside = true; }
                    if (inside) frame.WriteByte(b);
                    if (inside && previous == 0xff && b == 0xd9)
                    {
                        var bytes = frame.ToArray(); inside = false;
                        await Dispatcher.UIThread.InvokeAsync(() => { if (_closing || token.IsCancellationRequested) return; var old = _image.Source; _image.Source = new Bitmap(new MemoryStream(bytes)); (old as IDisposable)?.Dispose(); });
                    }
                    if (frame.Length > 4 * 1024 * 1024) { inside = false; frame.SetLength(0); }
                    previous = b;
                }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (ArgumentException) { await Dispatcher.UIThread.InvokeAsync(() => _status.Text = "Preview frame decoding failed."); }
    }
}
