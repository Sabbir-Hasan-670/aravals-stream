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
        FpsCombo.Text = _settings.Video.FrameRate.ToString();

        // 7. Video Bitrate
        VideoBitrateCombo.Text = _settings.Video.BitrateKbps.ToString();

        // 8. Audio Bitrate
        AudioBitrateCombo.Text = _settings.Audio.BitrateKbps.ToString();
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
        if (!int.TryParse(FpsCombo.Text, out var frameRate) || frameRate < 1 || frameRate > 60)
        { MessageBox.Show(this, "Frame rate must be from 1 to 60 FPS.", "Invalid frame rate"); return; }
        if (!int.TryParse(VideoBitrateCombo.Text, out var videoBitrate) || videoBitrate < 100 || videoBitrate > 200000 ||
            !int.TryParse(AudioBitrateCombo.Text, out var audioBitrate) || audioBitrate < 32 || audioBitrate > 512)
        { MessageBox.Show(this, "Video bitrate must be 100–200000 kbps; audio bitrate must be 32–512 kbps.", "Invalid bitrate"); return; }
        if (string.IsNullOrWhiteSpace(FolderInput.Text) || !Path.IsPathFullyQualified(FolderInput.Text.Trim()))
        { MessageBox.Show(this, "Choose a full recording folder path.", "Invalid recording folder"); return; }
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

        _settings.Video.FrameRate = frameRate;

        _settings.Video.BitrateKbps = videoBitrate;

        _settings.Audio.BitrateKbps = audioBitrate;

        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
