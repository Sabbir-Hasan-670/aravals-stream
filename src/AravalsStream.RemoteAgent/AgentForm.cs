using AravalsStream.Capture.Display;
using AravalsStream.Core.RemoteCapture;
using AravalsStream.Core.Recording;
using AravalsStream.Core.Services;

namespace AravalsStream.RemoteAgent;

internal sealed class AgentForm : Form
{
    private readonly ComboBox _display = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 330 };
    private readonly ComboBox _fps = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 100 };
    private readonly ComboBox _mode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150 };
    private readonly NumericUpDown _bitrate = new() { Minimum = 4000, Maximum = 50000, Increment = 1000, Value = 12000, Width = 130 };
    private readonly Label _status = new() { AutoSize = true, Text = "Ready" };
    private readonly Label _encoder = new() { AutoSize = true, Text = "Detecting encoder…" };
    private readonly Label _pairCode = new() { AutoSize = true, Text = "Preparing pairing service…" };
    private readonly Button _start = new() { Text = "Start", Width = 110, Height = 34 };
    private readonly Button _stop = new() { Text = "Stop", Width = 110, Height = 34, Enabled = false };
    private readonly NotifyIcon _tray = new() { Text = "Aravals Stream Remote Capture Agent", Icon = SystemIcons.Application, Visible = true };
    private readonly System.Drawing.Icon? _brandIcon;
    private readonly System.Drawing.Bitmap? _brandBitmap;
    private readonly DesktopDuplicationCaptureService _displays = new();
    private AgentLifecycleCoordinator? _runtime;
    private RemoteAgentControlPlane? _controlPlane;
    private bool _allowExit;
    private bool _restartingAfterRevocation;

    public AgentForm()
    {
        Text = "Aravals Stream — Remote Capture Agent";
        Width = 510;
        Height = 510;
        MinimumSize = new Size(510, 510);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(17, 25, 38);
        ForeColor = Color.WhiteSmoke;
        Font = new Font("Segoe UI", 10);
        _brandIcon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        if (_brandIcon is not null)
        {
            Icon = _brandIcon;
            _tray.Icon = _brandIcon;
            _brandBitmap = _brandIcon.ToBitmap();
        }
        BuildUi();
        LoadDisplays();
        DetectEncoder();
        Shown += async (_, _) => await StartControlPlaneAsync();
        _start.Click += async (_, _) => await StartAgentAsync();
        _stop.Click += async (_, _) => await StopAgentAsync();
        _tray.DoubleClick += (_, _) => { Show(); WindowState = FormWindowState.Normal; Activate(); };
        var trayMenu = new ContextMenuStrip();
        trayMenu.Items.Add("Open", null, (_, _) => { Show(); WindowState = FormWindowState.Normal; Activate(); });
        trayMenu.Items.Add("Exit", null, (_, _) => { _allowExit = true; Close(); });
        _tray.ContextMenuStrip = trayMenu;
        Resize += (_, _) => { if (WindowState == FormWindowState.Minimized) Hide(); };
        FormClosing += async (_, e) =>
        {
            if (e.CloseReason == CloseReason.UserClosing && !_allowExit)
            { e.Cancel = true; Hide(); return; }
            await StopAgentAsync();
            if (_runtime is not null) { await _runtime.DisposeAsync(); _runtime = null; _controlPlane = null; }
            _tray.Visible = false;
            _displays.Dispose();
            _brandBitmap?.Dispose();
            _brandIcon?.Dispose();
        };
    }

    private void BuildUi()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(22), ColumnCount = 2, RowCount = 8 };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 145));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        var brand = new FlowLayoutPanel { AutoSize = true, WrapContents = false, FlowDirection = FlowDirection.LeftToRight, Margin = Padding.Empty };
        if (_brandBitmap is not null)
            brand.Controls.Add(new PictureBox { Image = _brandBitmap, Width = 38, Height = 32, SizeMode = PictureBoxSizeMode.Zoom, Margin = new Padding(0, 0, 10, 0) });
        var brandText = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = Padding.Empty };
        brandText.Controls.Add(new Label { Text = "ARAVALS STREAM", Font = new Font(Font, FontStyle.Bold), AutoSize = true, Margin = Padding.Empty });
        brandText.Controls.Add(new Label { Text = $"Remote Capture Agent · v{GetType().Assembly.GetName().Version?.ToString(3)}", Font = new Font("Segoe UI", 8), ForeColor = Color.LightSteelBlue, AutoSize = true, Margin = Padding.Empty });
        brand.Controls.Add(brandText);
        root.Controls.Add(brand, 0, 0);
        root.SetColumnSpan(root.Controls[^1], 2);
        AddRow(root, 1, "Status", _status);
        AddRow(root, 2, "Display", _display);
        _fps.Items.AddRange([30, 60]); _fps.SelectedItem = 60;
        AddRow(root, 3, "Capture FPS", _fps);
        _mode.Items.AddRange(Enum.GetNames<RemoteAgentMode>()); _mode.SelectedItem = nameof(RemoteAgentMode.Auto);
        AddRow(root, 4, "Performance", _mode);
        AddRow(root, 5, "Video bitrate (kbps)", _bitrate);
        AddRow(root, 6, "Encoder", _encoder);
        AddRow(root, 7, "Pairing", _pairCode);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(135, 10, 0, 0) };
        buttons.Controls.Add(_start); buttons.Controls.Add(_stop);
        root.RowCount = 9;
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(buttons, 0, 8); root.SetColumnSpan(buttons, 2);
        Controls.Add(root);
    }

    private static void AddRow(TableLayoutPanel root, int row, string label, Control value)
    {
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        root.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = Color.LightSteelBlue }, 0, row);
        value.Anchor = AnchorStyles.Left;
        root.Controls.Add(value, 1, row);
    }

    private void LoadDisplays()
    {
        try
        {
            var displays = _displays.EnumerateDisplays();
            foreach (var display in displays)
                _display.Items.Add(new DisplayChoice(display, $"{display.Name} — {display.Width}×{display.Height}{(display.IsPrimary ? " (Primary)" : "")}"));
            if (_display.Items.Count > 0) _display.SelectedIndex = 0;
            else _status.Text = "No display is available.";
        }
        catch (Exception ex) { _status.Text = $"Display discovery failed: {ex.Message}"; }
    }

    private async void DetectEncoder()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "ffmpeg", "bin", "ffmpeg.exe");
            var encoders = await Task.Run(() => new EncoderDiscoveryService().DetectEncoders(path));
            var chosen = RemoteCaptureProtocol.SelectH264Encoder(encoders.Where(e => e.Available).Select(e => e.Id));
            var encoderName = encoders.FirstOrDefault(e => e.Id == chosen)?.DisplayName ?? chosen;
            _encoder.Text = encoderName;
        }
        catch (Exception ex) { _encoder.Text = $"Software fallback ({ex.Message})"; }
    }

    private async Task StartAgentAsync()
    {
        if (_runtime is null || _controlPlane is null) { _status.Text = "Pairing and discovery are not ready."; return; }
        try
        {
            _start.Enabled = false;
            await _runtime.StartMediaAsync();
            _status.Text = "Streaming — pairing and discovery active.";
            _stop.Enabled = true;
        }
        catch (Exception ex)
        {
            _status.Text = $"Could not start: {ex.Message}";
            _start.Enabled = true;
        }
    }

    private async Task StartControlPlaneAsync()
    {
        try
        {
            _runtime = new AgentLifecycleCoordinator(
                async () =>
                {
                    var display = (_display.SelectedItem as DisplayChoice)?.Display;
                    var plane = await RemoteAgentControlPlane.StartAsync(display?.Width ?? 1920, display?.Height ?? 1080,
                        (int)(_fps.SelectedItem ?? 60), ["libx264"]);
                    _controlPlane = plane;
                    plane.StatusChanged += value => { if (!IsDisposed) BeginInvoke(() => _status.Text = value); };
                    plane.CredentialRevoked += () => BeginInvoke(async () => await RestartAfterCredentialRevokedAsync());
                    return plane;
                },
                async () =>
                {
                    if (_display.SelectedItem is not DisplayChoice choice) throw new InvalidOperationException("Choose a display first.");
                    var path = Path.Combine(AppContext.BaseDirectory, "ffmpeg", "bin", "ffmpeg.exe");
                    var transmitter = await AgentMediaTransmitter.StartAsync(path, choice.Display, (int)(_fps.SelectedItem ?? 60),
                        (int)_bitrate.Value, (RemoteAgentMode)Enum.Parse(typeof(RemoteAgentMode), (string)(_mode.SelectedItem ?? nameof(RemoteAgentMode.Auto))),
                        RemoteAgentIdentity.LoadOrCreate().SrtPassphrase);
                    transmitter.StatusChanged += value => { if (!IsDisposed) BeginInvoke(() => _status.Text = value); };
                    _controlPlane!.SetStreaming(true);
                    return transmitter;
                });
            await _runtime.StartAsync();
            _pairCode.Text = $"Temporary pairing code: {_controlPlane!.PairingCode} (expires in 5 min)";
            _status.Text = "Ready — pairing and discovery active.";
            _start.Enabled = true;
        }
        catch (Exception ex)
        {
            _status.Text = $"Control plane failed: {ex.Message}";
            _pairCode.Text = "Pairing and discovery are unavailable.";
            _start.Enabled = false;
            AppLog.Write("RemoteControl", $"ControlPlaneStartFailed ({ex.GetType().Name}): {ex.Message}");
        }
    }

    private async Task RestartAfterCredentialRevokedAsync()
    {
        if (_restartingAfterRevocation || IsDisposed || _controlPlane is null || _runtime is null) return;
        _restartingAfterRevocation = true;
        try
        {
            var wasStreaming = _runtime.MediaPlaneRunning;
            await StopAgentAsync();
            await _controlPlane.RefreshPairingAsync();
            _pairCode.Text = $"Temporary pairing code: {_controlPlane.PairingCode} (expires in 5 min)";
            if (wasStreaming) await StartAgentAsync();
        }
        catch (Exception ex) { _status.Text = $"Could not refresh pairing service: {ex.Message}"; }
        finally { _restartingAfterRevocation = false; }
    }

    private async Task StopAgentAsync()
    {
        if (_runtime is not null) await _runtime.StopMediaAsync();
        _controlPlane?.SetStreaming(false);
        if (!IsDisposed) { _status.Text = "Ready — pairing and discovery active."; _start.Enabled = _controlPlane is not null; _stop.Enabled = false; }
    }

    private sealed record DisplayChoice(DisplayInfo Display, string Label)
    { public override string ToString() => Label; }
}

internal enum RemoteAgentMode { Auto, LowImpact, Balanced, Quality }
