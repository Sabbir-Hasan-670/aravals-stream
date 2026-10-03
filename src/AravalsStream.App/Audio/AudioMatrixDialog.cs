using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AravalsStream.Core.Audio;
using AravalsStream.Core.Models;
using AravalsStream.Core.Settings;

namespace AravalsStream.App.Audio;

public sealed class AudioMatrixDialog : Window
{
    private readonly AudioMixer _mixer;
    private readonly AppSettings _settings;
    private readonly List<PlatformDestinationGroup> _groups;
    private readonly List<AudioChannel> _channels;
    private readonly ComboBox _monitorDeviceCombo;
    private readonly Border _feedbackWarning;

    public AudioMatrixDialog(
        Window owner,
        AudioMixer mixer,
        AppSettings settings,
        IEnumerable<PlatformDestinationGroup> groups)
    {
        Owner = owner;
        _mixer = mixer;
        _settings = settings;
        _groups = groups.ToList();
        _channels = mixer.Channels.ToList();

        Title = "Advanced Audio Matrix & Routing";
        Width = 850;
        Height = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (Brush)FindResource("BackgroundBrush");

        var root = new Grid { Margin = new Thickness(20) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Header
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Monitoring Bar
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Warning Bar
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // Matrix
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Buttons

        // Header
        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 15) };
        header.Children.Add(new TextBlock
        {
            Text = "ADVANCED AUDIO ROUTING MATRIX",
            FontSize = 18,
            FontWeight = FontWeights.Bold,
            Foreground = (Brush)FindResource("TextBrush")
        });
        header.Children.Add(new TextBlock
        {
            Text = "Configure which audio sources feed each destination, set per-output gains, audio sync offsets, and monitoring.",
            FontSize = 12,
            Foreground = (Brush)FindResource("QuietBrush"),
            Margin = new Thickness(0, 4, 0, 0)
        });
        root.Children.Add(header);

        // Monitoring Bar
        var monBar = new Border
        {
            Background = (Brush)FindResource("CardBrush"),
            BorderBrush = (Brush)FindResource("BorderBrushDark"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 8, 12, 8),
            Margin = new Thickness(0, 0, 0, 10)
        };
        var monGrid = new Grid();
        monGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        monGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var monLabel = new TextBlock
        {
            Text = "Monitoring Device (Headphones / Speakers):",
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)FindResource("TextBrush"),
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 15, 0)
        };
        Grid.SetColumn(monLabel, 0);
        monGrid.Children.Add(monLabel);

        _monitorDeviceCombo = new ComboBox
        {
            Height = 28,
            VerticalAlignment = VerticalAlignment.Center,
            Background = (Brush)FindResource("PanelBrush"),
            Foreground = (Brush)FindResource("TextBrush")
        };
        _monitorDeviceCombo.Items.Add("Default System Playback Device");
        _monitorDeviceCombo.SelectedIndex = 0;
        _monitorDeviceCombo.SelectionChanged += (_, _) => UpdateFeedbackWarning();
        Grid.SetColumn(_monitorDeviceCombo, 1);
        monGrid.Children.Add(_monitorDeviceCombo);
        monBar.Child = monGrid;
        Grid.SetRow(monBar, 1);
        root.Children.Add(monBar);

        // Feedback Warning Banner
        _feedbackWarning = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(50, 239, 68, 68)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(239, 68, 68)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 6, 12, 6),
            Margin = new Thickness(0, 0, 0, 10),
            Visibility = Visibility.Collapsed
        };
        var warnText = new TextBlock
        {
            Text = "⚠️ WARNING: Selected monitoring device matches an active Desktop Audio capture device! This may cause a severe audio feedback loop.",
            Foreground = new SolidColorBrush(Color.FromRgb(252, 165, 165)),
            FontWeight = FontWeights.SemiBold,
            FontSize = 11
        };
        _feedbackWarning.Child = warnText;
        Grid.SetRow(_feedbackWarning, 2);
        root.Children.Add(_feedbackWarning);

        // Matrix ScrollViewer
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
        var matrixGrid = BuildMatrixGrid();
        scroll.Content = matrixGrid;
        Grid.SetRow(scroll, 3);
        root.Children.Add(scroll);

        // Action Buttons
        var btnPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 15, 0, 0)
        };
        var closeBtn = new Button
        {
            Content = "CLOSE",
            Style = (Style)FindResource("AccentButton"),
            Width = 110,
            Height = 32
        };
        closeBtn.Click += (_, _) => Close();
        btnPanel.Children.Add(closeBtn);
        Grid.SetRow(btnPanel, 4);
        root.Children.Add(btnPanel);

        Content = root;
    }

    private Grid BuildMatrixGrid()
    {
        var grid = new Grid { Margin = new Thickness(0, 5, 0, 5) };

        // Columns: Source Name, Monitor Mode, Sync Offset, REC, and active outputs
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new ColumnDefinition().Width, MinWidth = 160 }); // Source
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // Monitoring Mode
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // Sync Offset
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // REC

        var outputTargets = new List<(string Key, string Name)>();
        outputTargets.Add(("Recording", "REC (Local)"));

        foreach (var g in _groups.Where(x => x.Enabled && x.Routing != RoutingMode.Off))
        {
            if (g.Routing is RoutingMode.Horizontal or RoutingMode.Both)
                outputTargets.Add((g.Horizontal.Id.ToString(), $"{g.Name} (H)"));
            if (g.Routing is RoutingMode.Vertical or RoutingMode.Both)
                outputTargets.Add((g.Vertical.Id.ToString(), $"{g.Name} (V)"));
        }

        for (int i = 1; i < outputTargets.Count; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        }

        // Header Row
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        AddHeaderCell(grid, "Audio Source", 0, 0);
        AddHeaderCell(grid, "Monitor Mode", 0, 1);
        AddHeaderCell(grid, "Sync Offset", 0, 2);
        for (int c = 0; c < outputTargets.Count; c++)
        {
            AddHeaderCell(grid, outputTargets[c].Name, 0, 3 + c);
        }

        // Channel Rows
        int row = 1;
        foreach (var ch in _channels)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // 1. Source Name
            var nameBlock = new TextBlock
            {
                Text = ch.Name,
                FontWeight = FontWeights.Bold,
                Foreground = (Brush)FindResource("TextBrush"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 6, 8, 6)
            };
            Grid.SetRow(nameBlock, row);
            Grid.SetColumn(nameBlock, 0);
            grid.Children.Add(nameBlock);

            // 2. Monitoring Mode Combo
            var monCombo = new ComboBox
            {
                Height = 26,
                Margin = new Thickness(6, 4, 6, 4),
                VerticalAlignment = VerticalAlignment.Center
            };
            monCombo.Items.Add("Monitor Off");
            monCombo.Items.Add("Monitor Only");
            monCombo.Items.Add("Monitor + Output");
            monCombo.SelectedIndex = ch.MonitoringMode switch
            {
                AudioMonitoringMode.MonitorOnly => 1,
                AudioMonitoringMode.MonitorAndOutput => 2,
                _ => 0
            };
            monCombo.SelectionChanged += (_, _) =>
            {
                ch.MonitoringMode = monCombo.SelectedIndex switch
                {
                    1 => AudioMonitoringMode.MonitorOnly,
                    2 => AudioMonitoringMode.MonitorAndOutput,
                    _ => AudioMonitoringMode.MonitorOff
                };
            };
            Grid.SetRow(monCombo, row);
            Grid.SetColumn(monCombo, 1);
            grid.Children.Add(monCombo);

            // 3. Sync Offset
            var syncPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 4, 6, 4), VerticalAlignment = VerticalAlignment.Center };
            var syncBox = new TextBox
            {
                Text = ch.SyncOffsetMs.ToString(),
                Width = 55,
                Height = 24,
                TextAlignment = TextAlignment.Center,
                Background = (Brush)FindResource("PanelBrush"),
                Foreground = (Brush)FindResource("TextBrush")
            };
            syncBox.LostFocus += (_, _) =>
            {
                if (int.TryParse(syncBox.Text, out var val))
                {
                    val = Math.Clamp(val, -500, 5000);
                    ch.SyncOffsetMs = val;
                    syncBox.Text = val.ToString();
                }
                else
                {
                    syncBox.Text = ch.SyncOffsetMs.ToString();
                }
            };
            syncPanel.Children.Add(syncBox);
            syncPanel.Children.Add(new TextBlock { Text = " ms", VerticalAlignment = VerticalAlignment.Center, Foreground = (Brush)FindResource("QuietBrush") });
            Grid.SetRow(syncPanel, row);
            Grid.SetColumn(syncPanel, 2);
            grid.Children.Add(syncPanel);

            // 4+. Output Toggles & Gains
            for (int c = 0; c < outputTargets.Count; c++)
            {
                var target = outputTargets[c];
                var cell = new StackPanel
                {
                    Orientation = Orientation.Vertical,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(10, 4, 10, 4)
                };

                bool isEnabled = _mixer.Matrix.IsRouteEnabled(ch.Id, target.Key);
                float gain = _mixer.Matrix.GetRouteGain(ch.Id, target.Key);

                var chk = new CheckBox
                {
                    IsChecked = isEnabled,
                    HorizontalAlignment = HorizontalAlignment.Center
                };
                var gainBox = new TextBox
                {
                    Text = $"{(int)(gain * 100)}%",
                    Width = 46,
                    Height = 20,
                    FontSize = 10,
                    TextAlignment = TextAlignment.Center,
                    Margin = new Thickness(0, 3, 0, 0),
                    Background = (Brush)FindResource("PanelBrush"),
                    Foreground = (Brush)FindResource("TextBrush")
                };

                chk.Checked += (_, _) => _mixer.Matrix.SetRoute(ch.Id, target.Key, true, gain);
                chk.Unchecked += (_, _) => _mixer.Matrix.SetRoute(ch.Id, target.Key, false, gain);

                gainBox.LostFocus += (_, _) =>
                {
                    var txt = gainBox.Text.TrimEnd('%');
                    if (float.TryParse(txt, out var p))
                    {
                        gain = Math.Clamp(p / 100.0f, 0f, 2.0f);
                        _mixer.Matrix.SetRoute(ch.Id, target.Key, chk.IsChecked == true, gain);
                        gainBox.Text = $"{(int)(gain * 100)}%";
                    }
                    else
                    {
                        gainBox.Text = $"{(int)(gain * 100)}%";
                    }
                };

                cell.Children.Add(chk);
                cell.Children.Add(gainBox);

                Grid.SetRow(cell, row);
                Grid.SetColumn(cell, 3 + c);
                grid.Children.Add(cell);
            }

            row++;
        }

        return grid;
    }

    private void AddHeaderCell(Grid grid, string text, int row, int col)
    {
        var border = new Border
        {
            Background = (Brush)FindResource("CardBrush"),
            BorderBrush = (Brush)FindResource("BorderBrushDark"),
            BorderThickness = new Thickness(0, 0, 1, 1),
            Padding = new Thickness(8, 6, 8, 6)
        };
        var tb = new TextBlock
        {
            Text = text,
            FontWeight = FontWeights.Bold,
            FontSize = 11,
            Foreground = (Brush)FindResource("QuietBrush"),
            HorizontalAlignment = HorizontalAlignment.Center
        };
        border.Child = tb;
        Grid.SetRow(border, row);
        Grid.SetColumn(border, col);
        grid.Children.Add(border);
    }

    private void UpdateFeedbackWarning()
    {
        // Check if selected monitoring device matches active desktop audio
        var activeCaptures = new[] { _settings.Audio?.DefaultDesktopAudioId };
        var isLoop = AudioMonitoringService.DetectFeedbackLoop(_monitorDeviceCombo.SelectedItem?.ToString(), activeCaptures);
        _feedbackWarning.Visibility = isLoop ? Visibility.Visible : Visibility.Collapsed;
    }
}
