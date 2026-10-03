using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AravalsStream.Core.Recording.Models;
using AravalsStream.Core.Settings;
using AravalsStream.Core.Versioning;

namespace AravalsStream.App.Views;

public sealed class FirstRunWizardDialog : Window
{
    private readonly AppSettings _settings;
    private readonly IReadOnlyList<EncoderInfo> _encoders;
    private int _step = 0;
    private readonly ContentControl _stepContainer;
    private readonly Button _backBtn;
    private readonly Button _nextBtn;

    public FirstRunWizardDialog(
        Window owner,
        AppSettings settings,
        IReadOnlyList<EncoderInfo> encoders)
    {
        Owner = owner;
        _settings = settings;
        _encoders = encoders;

        Title = $"Welcome to {AppVersion.Name} - Setup Wizard";
        Width = 620;
        Height = 440;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (Brush)FindResource("BackgroundBrush");

        var root = new Grid { Margin = new Thickness(24) };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        _stepContainer = new ContentControl();
        Grid.SetRow(_stepContainer, 0);
        root.Children.Add(_stepContainer);

        var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 15, 0, 0) };
        _backBtn = new Button { Content = "BACK", Width = 90, Height = 32, Margin = new Thickness(0, 0, 10, 0), Style = (Style)FindResource("GhostButton") };
        _backBtn.Click += (_, _) => NavigateStep(-1);

        _nextBtn = new Button { Content = "NEXT", Width = 110, Height = 32, Style = (Style)FindResource("AccentButton") };
        _nextBtn.Click += (_, _) => NavigateStep(1);

        btnPanel.Children.Add(_backBtn);
        btnPanel.Children.Add(_nextBtn);
        Grid.SetRow(btnPanel, 1);
        root.Children.Add(btnPanel);

        Content = root;
        RenderStep();
    }

    private void NavigateStep(int delta)
    {
        _step += delta;
        if (_step > 4)
        {
            _settings.FirstRunCompleted = true;
            DialogResult = true;
            Close();
            return;
        }
        RenderStep();
    }

    private void RenderStep()
    {
        _backBtn.IsEnabled = _step > 0;
        _nextBtn.Content = _step == 4 ? "START STREAMING" : "NEXT";

        _stepContainer.Content = _step switch
        {
            0 => BuildWelcomePage(),
            1 => BuildHardwarePage(),
            2 => BuildAudioPage(),
            3 => BuildRecordingPage(),
            4 => BuildReadyPage(),
            _ => null
        };
    }

    private UIElement BuildWelcomePage()
    {
        var panel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(new Image
        {
            Source = (ImageSource)Application.Current.FindResource("BrandLogo"),
            Width = 118,
            Height = 98,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 0, 0, 18)
        });
        panel.Children.Add(new TextBlock
        {
            Text = $"Welcome to {AppVersion.Name}",
            FontSize = 22,
            FontWeight = FontWeights.Bold,
            Foreground = (Brush)FindResource("TextBrush"),
            Margin = new Thickness(0, 0, 0, 10)
        });
        panel.Children.Add(new TextBlock
        {
            Text = "The next-generation live streaming and recording workstation.\nDual horizontal and vertical multi-output, high-performance NVENC/QSV encoding, and custom audio mixing.\n\nNo cloud account required. 100% local, private, and fast.",
            FontSize = 13,
            Foreground = (Brush)FindResource("QuietBrush"),
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 20
        });
        return panel;
    }

    private UIElement BuildHardwarePage()
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = "Video Hardware & Encoders",
            FontSize = 18,
            FontWeight = FontWeights.Bold,
            Foreground = (Brush)FindResource("TextBrush"),
            Margin = new Thickness(0, 0, 0, 10)
        });

        var recommended = _encoders.FirstOrDefault(e => e.Recommended && e.Available) ?? _encoders.FirstOrDefault(e => e.Available);
        panel.Children.Add(new TextBlock
        {
            Text = $"Detected recommended encoder: {recommended?.DisplayName ?? "x264 (Software)"}",
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("AccentBrush"),
            Margin = new Thickness(0, 0, 0, 15)
        });

        var list = new StackPanel();
        foreach (var enc in _encoders)
        {
            list.Children.Add(new TextBlock
            {
                Text = $"• {enc.DisplayName} — {(enc.Available ? "Ready" : "Not Detected")}",
                FontSize = 12,
                Foreground = enc.Available ? (Brush)FindResource("TextBrush") : (Brush)FindResource("QuietBrush"),
                Margin = new Thickness(0, 0, 0, 4)
            });
        }
        panel.Children.Add(list);
        return panel;
    }

    private UIElement BuildAudioPage()
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = "Audio Setup",
            FontSize = 18,
            FontWeight = FontWeights.Bold,
            Foreground = (Brush)FindResource("TextBrush"),
            Margin = new Thickness(0, 0, 0, 10)
        });
        panel.Children.Add(new TextBlock
        {
            Text = "Your default audio devices have been detected:\n• Microphone: System Default Input\n• Desktop Audio: WASAPI Loopback\n\nYou can fine-tune routing, volume, and monitoring at any time in the Audio Matrix.",
            FontSize = 13,
            Foreground = (Brush)FindResource("QuietBrush"),
            LineHeight = 22
        });
        return panel;
    }

    private UIElement BuildRecordingPage()
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = "Recording Folder",
            FontSize = 18,
            FontWeight = FontWeights.Bold,
            Foreground = (Brush)FindResource("TextBrush"),
            Margin = new Thickness(0, 0, 0, 10)
        });
        panel.Children.Add(new TextBlock
        {
            Text = $"Local recordings will be saved to:\n{_settings.Recording.OutputDirectory}\n\nYou can customize format (MKV / MP4) and bitrates in Settings.",
            FontSize = 13,
            Foreground = (Brush)FindResource("QuietBrush"),
            LineHeight = 22
        });
        return panel;
    }

    private UIElement BuildReadyPage()
    {
        var panel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(new TextBlock
        {
            Text = "You're Ready to Stream!",
            FontSize = 22,
            FontWeight = FontWeights.Bold,
            Foreground = (Brush)FindResource("AccentBrush"),
            Margin = new Thickness(0, 0, 0, 10)
        });
        panel.Children.Add(new TextBlock
        {
            Text = "Click 'START STREAMING' to launch your workstation.\nAdd destinations (YouTube, Twitch, Kick) and start broadcasting in Horizontal, Vertical, or Both!",
            FontSize = 13,
            Foreground = (Brush)FindResource("QuietBrush"),
            LineHeight = 20
        });
        return panel;
    }
}
