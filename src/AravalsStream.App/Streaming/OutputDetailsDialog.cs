using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AravalsStream.Core.Models;
using AravalsStream.Core.Recording.Models;

namespace AravalsStream.App.Streaming;

public sealed class OutputDetailsDialog : Window
{
    public OutputDetailsDialog(Window owner, Destination destination, IStreamingOutput? activeOutput)
    {
        Owner = owner;
        Title = $"{destination.Name} - Output Details";
        Width = 480;
        Height = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(27, 29, 36));
        Foreground = Brushes.White;

        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var panel = new StackPanel { Margin = new Thickness(22) };
        scroll.Content = panel;
        Content = scroll;

        var title = new TextBlock
        {
            Text = destination.Name,
            FontSize = 16,
            FontWeight = FontWeights.Bold,
            Foreground = Brushes.White,
            Margin = new Thickness(0, 0, 0, 14)
        };
        panel.Children.Add(title);

        var telemetry = activeOutput?.Telemetry;
        var status = activeOutput?.Status ?? destination.Status;

        var res = destination.OutputMode == OutputMode.Vertical ? "1080 x 1920 (Vertical)" : "1920 x 1080 (Horizontal)";
        var encoderName = telemetry?.ActiveEncoder ?? destination.EncoderId;
        var targetBitrate = $"{destination.VideoBitrateKbps} kbps (Audio: {destination.AudioBitrateKbps} kbps)";
        var measuredBitrate = telemetry?.MeasuredBitrateKbps != null
            ? $"{telemetry.MeasuredBitrateKbps:0} kbps (~{telemetry.MeasuredBitrateKbps.Value / 1000.0:0.1} Mbps)"
            : "N/A (Not transmitting)";
        var fps = telemetry != null ? $"{telemetry.CurrentFps:0.0} FPS (Target: {destination.FrameRate} FPS)" : $"{destination.FrameRate} FPS (Target)";
        var dropped = telemetry != null ? $"{telemetry.FramesDropped}" : "0";
        var reconnects = activeOutput?.ReconnectCount.ToString() ?? "0";
        var duration = telemetry != null ? telemetry.ElapsedTime.ToString(@"hh\:mm\:ss") : "00:00:00";
        var error = activeOutput?.LastError ?? "None";

        AddRow(panel, "Platform", destination.Platform);
        AddRow(panel, "Format", destination.OutputMode.ToString());
        AddRow(panel, "Resolution", res);
        AddRow(panel, "State", status.ToString().ToUpperInvariant(), GetStatusBrush(status));
        AddRow(panel, "Active Encoder", encoderName);
        AddRow(panel, "Target Bitrate", targetBitrate);
        AddRow(panel, "Measured Bitrate", measuredBitrate);
        AddRow(panel, "FPS", fps);
        AddRow(panel, "Dropped Frames", dropped);
        AddRow(panel, "Reconnect Count", reconnects);
        AddRow(panel, "Session Duration", duration);
        AddRow(panel, "Server URL", destination.StreamUrl);
        AddRow(panel, "Last Error", error, error != "None" ? Brushes.OrangeRed : Brushes.LightGray);

        var closeBtn = new Button
        {
            Content = "Close",
            HorizontalAlignment = HorizontalAlignment.Right,
            Padding = new Thickness(16, 7, 16, 7),
            Margin = new Thickness(0, 16, 0, 0)
        };
        closeBtn.Click += (_, _) => Close();
        panel.Children.Add(closeBtn);
    }

    private static void AddRow(Panel panel, string label, string value, Brush? valueBrush = null)
    {
        var border = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(45, 51, 62)),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(0, 6, 0, 6)
        };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var lbl = new TextBlock { Text = label, Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184)), FontSize = 12 };
        Grid.SetColumn(lbl, 0);
        grid.Children.Add(lbl);

        var val = new TextBlock
        {
            Text = value,
            Foreground = valueBrush ?? Brushes.White,
            FontWeight = FontWeights.SemiBold,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap
        };
        Grid.SetColumn(val, 1);
        grid.Children.Add(val);

        border.Child = grid;
        panel.Children.Add(border);
    }

    private static Brush GetStatusBrush(DestinationStatus s) => s switch
    {
        DestinationStatus.Live => new SolidColorBrush(Color.FromRgb(16, 185, 129)),
        DestinationStatus.Connecting or DestinationStatus.FallingBehind or DestinationStatus.Reconnecting => new SolidColorBrush(Color.FromRgb(245, 158, 11)),
        DestinationStatus.Error => new SolidColorBrush(Color.FromRgb(239, 68, 68)),
        _ => new SolidColorBrush(Color.FromRgb(100, 116, 139))
    };
}
