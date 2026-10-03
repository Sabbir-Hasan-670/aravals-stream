using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AravalsStream.Core.Accounts;

namespace AravalsStream.App.Views;

public sealed class YouTubeModeDialog : Window
{
    public bool UseNativeAccount { get; private set; }
    public bool UseManualRtmp { get; private set; }

    public YouTubeModeDialog(Window owner, YouTubeAccount? account)
    {
        Owner = owner;
        Title = "Configure YouTube Destination";
        Width = 460;
        Height = 310;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(27, 29, 36));
        Foreground = Brushes.White;
        ResizeMode = ResizeMode.NoResize;

        var root = new Grid { Margin = new Thickness(24) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Header
        var titleText = new TextBlock
        {
            Text = "YouTube Configuration",
            FontSize = 18,
            FontWeight = FontWeights.Bold,
            Foreground = Brushes.White
        };
        Grid.SetRow(titleText, 0);
        root.Children.Add(titleText);

        // Body
        var body = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
        Grid.SetRow(body, 1);

        var descText = new TextBlock
        {
            Text = "How would you like to stream to YouTube?",
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.FromRgb(156, 163, 175)),
            Margin = new Thickness(0, 0, 0, 16)
        };
        body.Children.Add(descText);

        // Option 1: Native Account
        var nativeBtn = new Button
        {
            Height = 44,
            Background = new SolidColorBrush(Color.FromRgb(239, 68, 68)), // Red accent
            Foreground = Brushes.White,
            FontWeight = FontWeights.SemiBold,
            Cursor = System.Windows.Input.Cursors.Hand,
            Margin = new Thickness(0, 0, 0, 10)
        };

        if (account != null && account.Connected)
        {
            nativeBtn.Content = $"Use Connected Account ({account.ChannelTitle})";
        }
        else
        {
            nativeBtn.Content = "Connect YouTube Account (Native Integration)";
        }

        nativeBtn.Click += (_, _) =>
        {
            UseNativeAccount = true;
            DialogResult = true;
            Close();
        };
        body.Children.Add(nativeBtn);

        // Option 2: Manual RTMP
        var manualBtn = new Button
        {
            Height = 40,
            Content = "Manual RTMP (Standard Stream Key)",
            Background = new SolidColorBrush(Color.FromRgb(45, 48, 58)),
            Foreground = Brushes.White,
            Cursor = System.Windows.Input.Cursors.Hand
        };
        manualBtn.Click += (_, _) =>
        {
            UseManualRtmp = true;
            DialogResult = true;
            Close();
        };
        body.Children.Add(manualBtn);

        root.Children.Add(body);

        // Cancel
        var cancelBtn = new Button
        {
            Content = "Cancel",
            Width = 80,
            Height = 32,
            HorizontalAlignment = HorizontalAlignment.Right,
            Background = Brushes.Transparent,
            Foreground = new SolidColorBrush(Color.FromRgb(156, 163, 175)),
            Cursor = System.Windows.Input.Cursors.Hand
        };
        cancelBtn.Click += (_, _) =>
        {
            DialogResult = false;
            Close();
        };
        Grid.SetRow(cancelBtn, 2);
        root.Children.Add(cancelBtn);

        Content = root;
    }
}
