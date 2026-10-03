using AravalsStream.Platform;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

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
        AddField(tools, "VIDEO SOURCE", _source); AddField(tools, "Device name/index or file path", _device);
        AddField(tools, "CANVAS", _canvas); AddField(tools, "MICROPHONE", _microphone); AddField(tools, "DESKTOP AUDIO", _desktopAudio);
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
        _timer.Tick += (_, _) => { if (_stream?.HasExited == true || _record?.HasExited == true) _status.Text = "An output stopped. Check source permissions and server/device availability."; };
        _timer.Start();
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

    private MediaPlan Plan()
    {
        var kind = _source.SelectedIndex switch { 1 => CaptureKind.Camera, 2 => CaptureKind.Image, 3 => CaptureKind.Video, 4 => CaptureKind.TestVideo, _ => CaptureKind.Display };
        var audio = new List<CaptureInput>();
        if (!string.IsNullOrWhiteSpace(_microphone.Text)) audio.Add(new(CaptureKind.Microphone, _microphone.Text));
        if (!string.IsNullOrWhiteSpace(_desktopAudio.Text)) audio.Add(new(CaptureKind.DesktopAudio, _desktopAudio.Text));
        return new(PlatformCapture.Current, new(kind, _device.Text ?? ""), audio, _canvas.SelectedIndex == 1 ? CanvasSize.Vertical : CanvasSize.Horizontal);
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
