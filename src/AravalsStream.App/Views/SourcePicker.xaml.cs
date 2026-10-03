using System.Windows;
using System.Windows.Controls;
using AravalsStream.Capture.Audio;
using AravalsStream.Capture.Camera;
using AravalsStream.Capture.Display;
using AravalsStream.Capture.Window;
using AravalsStream.App.Controls;
using AravalsStream.Core.Models;

namespace AravalsStream.App.Views;

public partial class SourcePicker : Window
{
    public sealed record SourceChoice(SourceType Type, string Label);
    public SourceType SelectedType => (TypeList.SelectedItem as SourceChoice)?.Type ?? SourceType.DisplayCapture;
    public object? SelectedDevice => DeviceList.SelectedItem;
    public bool IsSynthetic => SelectedType is SourceType.Alerts or SourceType.ChatOverlay;
    public bool IsRemote => SelectedType == SourceType.RemotePc;
    public CameraFormat? PreferredCameraFormat => FormatList.SelectedItem as CameraFormat;
    public CameraFormat? PreferredCaptureDeviceFormat => FormatList.SelectedItem as CameraFormat;
    private readonly SourceType? _initialType;
    private readonly CameraFormat? _initialFormat;

    public SourcePicker(SourceType? initialType = null, CameraFormat? initialFormat = null)
    {
        InitializeComponent();
        DarkWindowChrome.Apply(this);
        _initialType = initialType;
        _initialFormat = initialFormat;
        TypeList.ItemsSource = new[]
        {
            new SourceChoice(SourceType.DisplayCapture, "Display Capture"),
            new SourceChoice(SourceType.WindowCapture, "Window Capture"),
            new SourceChoice(SourceType.Camera, "Camera"),
            new SourceChoice(SourceType.CaptureDevice, "Capture Device (HDMI / UVC)"),
            new SourceChoice(SourceType.RemotePc, "Remote PC"),
            new SourceChoice(SourceType.AudioInput, "Audio Input Capture"),
            new SourceChoice(SourceType.AudioOutput, "Audio Output Capture"),
            new SourceChoice(SourceType.Alerts, "Alerts"),
            new SourceChoice(SourceType.ChatOverlay, "Chat Overlay")
        };
        Loaded += (_, _) => TypeList.SelectedItem = _initialType is null
            ? TypeList.Items[0]
            : TypeList.Items.Cast<SourceChoice>().FirstOrDefault(choice => choice.Type == _initialType) ?? TypeList.Items[0];
    }

    private async void TypeList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        CameraOptions.Visibility = SelectedType is SourceType.Camera or SourceType.CaptureDevice ? Visibility.Visible : Visibility.Collapsed;
        var isCaptureDevice = SelectedType == SourceType.CaptureDevice;
        FormatLabel.Text = isCaptureDevice ? "Device format:" : "Camera format:";
        DeviceHelpText.Text = IsRemote
            ? "Discover a paired Remote Agent on your local network, or connect by computer name/IP."
            : isCaptureDevice
            ? "Connect the card to this PC; the gaming PC needs no Aravals software. Add card audio separately as Audio Input Capture."
            : "Choose a device or window";
        await RefreshDevicesAsync();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshDevicesAsync();

    private async Task RefreshDevicesAsync()
    {
        if (!IsLoaded || TypeList.SelectedItem is null) return;
        AddButton.IsEnabled = false;
        DeviceList.ItemsSource = null;
        DeviceList.Visibility = IsSynthetic ? Visibility.Collapsed : Visibility.Visible;
        if (IsRemote) { DeviceList.Visibility = Visibility.Collapsed; AddButton.IsEnabled = true; return; }
        if (IsSynthetic) { AddButton.IsEnabled = true; return; }
        try
        {
            object[] devices = SelectedType switch
            {
                SourceType.DisplayCapture => await Task.Run(() => new DesktopDuplicationCaptureService().EnumerateDisplays().Cast<object>().ToArray()),
                SourceType.WindowCapture => await Task.Run(() => new WindowCaptureService().EnumerateWindows().Cast<object>().ToArray()),
                SourceType.Camera => (await new CameraCaptureService().EnumerateAsync()).Cast<object>().ToArray(),
                SourceType.CaptureDevice => (await new CameraCaptureService().EnumerateAsync()).Cast<object>().ToArray(),
                SourceType.AudioInput => await Task.Run(() => new WasapiAudioCaptureService().Enumerate(true).Cast<object>().ToArray()),
                SourceType.AudioOutput => await Task.Run(() => new WasapiAudioCaptureService().Enumerate(false).Cast<object>().ToArray()),
                _ => []
            };
            DeviceList.ItemsSource = devices;
            if (devices.Length > 0) DeviceList.SelectedIndex = 0;
        }
        catch (Exception ex) { AravalsStream.Core.Services.AppLog.Write("SourcePicker", $"Enumeration failed for {SelectedType}: {ex}"); MessageBox.Show(this, ex.Message, "Device enumeration failed"); }
    }

    private async void DeviceList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        AddButton.IsEnabled = SelectedDevice is not null;
        FormatList.ItemsSource = null;
        if (SelectedType is not (SourceType.Camera or SourceType.CaptureDevice) || SelectedDevice is not CameraInfo camera) return;
        try
        {
            var formats = await new CameraCaptureService().EnumerateFormatsAsync(camera);
            if (!ReferenceEquals(SelectedDevice, camera)) return;
            FormatList.ItemsSource = new object[] { "Auto / Recommended" }.Concat(formats.Cast<object>()).ToArray();
            FormatList.SelectedItem = formats.FirstOrDefault(f => f == _initialFormat) ?? FormatList.Items[0];
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Camera formats unavailable"); }
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedDevice is null && !IsSynthetic && !IsRemote) return;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}



