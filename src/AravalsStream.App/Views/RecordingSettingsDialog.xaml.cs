using System.IO;
using System.Windows;
using System.Windows.Media;
using AravalsStream.Core.Models;
using AravalsStream.Core.Recording.Models;

namespace AravalsStream.App.Views;

public partial class RecordingSettingsDialog : Window
{
    private readonly RecordingSettings _settings;
    private readonly FfmpegResolution _ffmpegStatus;
    private readonly IReadOnlyList<EncoderInfo> _encoders;

    public RecordingSettings Settings => _settings;

    public RecordingSettingsDialog(
        RecordingSettings settings,
        FfmpegResolution ffmpegStatus,
        IReadOnlyList<EncoderInfo> encoders)
        : this(null, settings, ffmpegStatus, encoders)
    {
    }

    public RecordingSettingsDialog(
        Window? owner,
        RecordingSettings settings,
        FfmpegResolution ffmpegStatus,
        IReadOnlyList<EncoderInfo> encoders)
    {
        if (owner is not null) Owner = owner;
        _settings = settings;
        _ffmpegStatus = ffmpegStatus;
        _encoders = encoders;

        InitializeComponent();
        AravalsStream.App.Controls.DarkWindowChrome.Apply(this);
        PopulateControls();
    }

    private void PopulateControls()
    {
        // 1. FFmpeg status badge
        if (_ffmpegStatus.Status == FfmpegStatus.Available)
        {
            FfmpegStatusText.Text = $"FFmpeg: {_ffmpegStatus.Version ?? "Available"}";
            FfmpegStatusText.Foreground = (Brush)Application.Current.FindResource("AccentBrush");
            FfmpegStatusBadge.Background = new SolidColorBrush(Color.FromArgb(50, 0, 229, 255));
            FfmpegStatusBadge.BorderBrush = (Brush)Application.Current.FindResource("AccentBrush");
        }
        else
        {
            FfmpegStatusText.Text = "FFmpeg: Missing";
            FfmpegStatusText.Foreground = (Brush)Application.Current.FindResource("DangerBrush");
            FfmpegStatusBadge.Background = new SolidColorBrush(Color.FromArgb(50, 255, 60, 60));
            FfmpegStatusBadge.BorderBrush = (Brush)Application.Current.FindResource("DangerBrush");
        }

        // 2. Folder input
        FolderInput.Text = _settings.OutputDirectory;

        // 3. Output mode
        ModeCombo.SelectedIndex = _settings.Mode switch
        {
            OutputMode.Horizontal => 0,
            OutputMode.Vertical => 1,
            OutputMode.Both => 2,
            _ => 0
        };

        // 4. Container
        ContainerCombo.SelectedIndex = string.Equals(_settings.Container, "mp4", StringComparison.OrdinalIgnoreCase) ? 1 : 0;

        // 5. Encoders
        EncoderCombo.Items.Clear();
        var autoItem = "Auto (Recommended)";
        var recommended = _encoders.FirstOrDefault(e => e.Recommended);
        if (recommended is not null)
        {
            autoItem = $"Auto (Recommended: {recommended.DisplayName})";
        }
        EncoderCombo.Items.Add(autoItem);

        int selectedEncoderIdx = 0;
        int currentIdx = 1;
        foreach (var enc in _encoders)
        {
            var text = enc.DisplayName + (enc.HardwareAccelerated ? " [HW]" : " [SW]");
            EncoderCombo.Items.Add(text);
            if (string.Equals(_settings.Video.EncoderId, enc.Id, StringComparison.OrdinalIgnoreCase))
            {
                selectedEncoderIdx = currentIdx;
            }
            currentIdx++;
        }
        EncoderCombo.SelectedIndex = selectedEncoderIdx;

        // 6. FPS
        FpsCombo.SelectedIndex = _settings.Video.FrameRate <= 30 ? 1 : 0;

        // 7. Video Bitrate
        VideoBitrateCombo.SelectedIndex = _settings.Video.BitrateKbps switch
        {
            >= 8000 => 0,
            >= 6000 => 1,
            >= 4500 => 2,
            _ => 3
        };

        // 8. Audio Bitrate
        AudioBitrateCombo.SelectedIndex = _settings.Audio.BitrateKbps switch
        {
            >= 320 => 0,
            >= 192 => 1,
            >= 160 => 2,
            _ => 3
        };
    }

    private void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Select Recording Output Directory",
            UseDescriptionForTitle = true,
            SelectedPath = Directory.Exists(FolderInput.Text) ? FolderInput.Text : Environment.GetFolderPath(Environment.SpecialFolder.MyVideos)
        };

        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            FolderInput.Text = dialog.SelectedPath;
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        _settings.OutputDirectory = FolderInput.Text.Trim();

        _settings.Mode = ModeCombo.SelectedIndex switch
        {
            0 => OutputMode.Horizontal,
            1 => OutputMode.Vertical,
            2 => OutputMode.Both,
            _ => OutputMode.Horizontal
        };

        _settings.Container = ContainerCombo.SelectedIndex == 1 ? "mp4" : "mkv";

        if (EncoderCombo.SelectedIndex == 0 || EncoderCombo.SelectedIndex < 0)
        {
            _settings.Video.EncoderId = "auto";
        }
        else
        {
            int encIdx = EncoderCombo.SelectedIndex - 1;
            if (encIdx >= 0 && encIdx < _encoders.Count)
            {
                _settings.Video.EncoderId = _encoders[encIdx].Id;
            }
        }

        _settings.Video.FrameRate = FpsCombo.SelectedIndex == 1 ? 30 : 60;

        _settings.Video.BitrateKbps = VideoBitrateCombo.SelectedIndex switch
        {
            0 => 8000,
            1 => 6000,
            2 => 4500,
            _ => 3000
        };

        _settings.Audio.BitrateKbps = AudioBitrateCombo.SelectedIndex switch
        {
            0 => 320,
            1 => 192,
            2 => 160,
            _ => 128
        };

        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
