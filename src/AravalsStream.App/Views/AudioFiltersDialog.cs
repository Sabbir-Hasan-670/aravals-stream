using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AravalsStream.Core.Audio;

namespace AravalsStream.App.Views;

public sealed class AudioFiltersDialog : Window
{
    private readonly StackPanel _panel = new();
    private readonly List<Func<bool>> _validators = [];
    public AudioFilterSettings Result { get; private set; }

    public AudioFiltersDialog(Window owner, string sourceName, AudioFilterSettings filters)
    {
        Owner = owner; Title = $"Audio filters — {sourceName}";
        Width = 620; Height = 650; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (Brush)FindResource("BackgroundBrush");
        Foreground = (Brush)FindResource("TextBrush");
        Result = filters.Copy();
        var root = new DockPanel { Margin = new Thickness(20) };
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Cancel", Margin = new Thickness(8), Padding = new Thickness(16, 6, 16, 6) };
        cancel.Click += (_, _) => DialogResult = false;
        var save = new Button { Content = "Apply filters", Margin = new Thickness(8), Padding = new Thickness(16, 6, 16, 6) };
        save.Click += (_, _) =>
        {
            if (_validators.Any(validate => !validate())) return;
            if (Result.GateCloseDb > Result.GateOpenDb)
            { MessageBox.Show(this, "Gate close threshold must be at or below the open threshold."); return; }
            DialogResult = true;
        };
        footer.Children.Add(cancel); footer.Children.Add(save);
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        root.Children.Add(new ScrollViewer { Content = _panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = root; Build();
        AravalsStream.App.Controls.DarkWindowChrome.Apply(this);
    }

    private void Build()
    {
        _panel.Children.Clear(); _validators.Clear();
        _panel.Children.Add(new TextBlock { Text = "Filters apply to recording, streaming and monitoring. Enable the filters you need.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
        var preset = new Button { Content = "Voice cleanup preset", HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(12, 6, 12, 6) };
        preset.Click += (_, _) => { Result = AudioFilterSettings.VoiceCleanup(); Build(); };
        _panel.Children.Add(preset);
        Toggle("Noise suppression (spectral)", Result.NoiseSuppression, v => Result.NoiseSuppression = v);
        Number("Noise floor (dB) • lower values preserve quieter speech", Result.NoiseFloorDb, -80, -20, v => Result.NoiseFloorDb = v);
        Toggle("High-pass filter • remove low rumble", Result.HighPass, v => Result.HighPass = v);
        Number("Cutoff (Hz)", Result.HighPassHz, 20, 1000, v => Result.HighPassHz = v);
        Toggle("Noise gate • silence background noise between words", Result.NoiseGate, v => Result.NoiseGate = v);
        Number("Open threshold (dB)", Result.GateOpenDb, -80, 0, v => Result.GateOpenDb = v);
        Number("Close threshold (dB)", Result.GateCloseDb, -80, 0, v => Result.GateCloseDb = v);
        Number("Release (ms)", Result.GateReleaseMs, 10, 2000, v => Result.GateReleaseMs = v);
        Toggle("Compressor • control loud speech", Result.Compressor, v => Result.Compressor = v);
        Number("Threshold (dB)", Result.CompressorThresholdDb, -60, 0, v => Result.CompressorThresholdDb = v);
        Number("Ratio", Result.CompressorRatio, 1, 20, v => Result.CompressorRatio = v);
        Toggle("Gain", Result.Gain, v => Result.Gain = v);
        Number("Gain (dB)", Result.GainDb, -60, 30, v => Result.GainDb = v);
        Toggle("Limiter • prevent clipping", Result.Limiter, v => Result.Limiter = v);
        Number("Ceiling (dB)", Result.LimiterDb, -30, 0, v => Result.LimiterDb = v);
    }

    private void Toggle(string label, bool enabled, Action<bool> set)
    {
        var checkbox = new CheckBox { Content = label, IsChecked = enabled, Margin = new Thickness(0, 16, 0, 6) };
        checkbox.Checked += (_, _) => set(true); checkbox.Unchecked += (_, _) => set(false);
        _panel.Children.Add(checkbox);
    }
    private void Number(string label, double value, double min, double max, Action<double> set)
    {
        var row = new DockPanel { Margin = new Thickness(20, 3, 0, 3) };
        var input = new TextBox { Text = value.ToString(CultureInfo.InvariantCulture), Width = 90, Margin = new Thickness(8, 0, 0, 0) };
        DockPanel.SetDock(input, Dock.Right); row.Children.Add(input);
        row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap });
        _panel.Children.Add(row);
        _validators.Add(() =>
        {
            if (double.TryParse(input.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) && number >= min && number <= max)
            { set(number); return true; }
            MessageBox.Show(this, $"{label}: enter a number from {min} to {max}."); input.Focus(); return false;
        });
    }
}
