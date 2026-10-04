using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using AravalsStream.App.Services;
using AravalsStream.Core.Recording.Models;
using AravalsStream.Core.Services;
using AravalsStream.Core.Versioning;

namespace AravalsStream.App.Views;

public partial class MainWindow
{
    private readonly AppUpdateService _updateService = new();
    private readonly DispatcherTimer _updateTimer = new() { Interval = TimeSpan.FromHours(6) };
    private AvailableAppUpdate? _availableUpdate;
    private string? _dismissedUpdateVersion;
    private bool _updateCheckInProgress;
    private bool _updateInstallInProgress;

    private void InitializeUpdateChecks()
    {
        _updateTimer.Tick += async (_, _) => await CheckForUpdatesAsync(false);
        ConfigureUpdateChecks();
        if (_loadedSettings.General.CheckForUpdates) _ = CheckForUpdatesAsync(false);
    }

    private void ConfigureUpdateChecks()
    {
        if (_loadedSettings.General.CheckForUpdates) _updateTimer.Start();
        else _updateTimer.Stop();
    }

    private async Task CheckForUpdatesAsync(bool manual)
    {
        if (_updateCheckInProgress || _updateInstallInProgress) return;
        _updateCheckInProgress = true;
        UpdateButton.IsEnabled = false;
        UpdateButton.Content = "CHECKING…";
        try
        {
            var includeDevelopment = AppVersion.ReleaseChannel == "Development" ||
                _loadedSettings.General.IncludeDevelopmentUpdates;
            _availableUpdate = await _updateService.CheckAsync(AppVersion.Version, includeDevelopment);
            if (_availableUpdate is null)
            {
                UpdateNotice.Visibility = Visibility.Collapsed;
                UpdateButton.Content = "CHECK UPDATES";
                if (manual) MessageBox.Show(this, "Aravals Stream is up to date.", "Updates",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                UpdateButton.Content = $"UPDATE {_availableUpdate.Version}";
                UpdateButton.Foreground = (Brush)FindResource("AccentBrush");
                UpdateButton.ToolTip = $"Download and install Aravals Stream {_availableUpdate.Version}";
                if (manual || _dismissedUpdateVersion != _availableUpdate.Version)
                {
                    UpdateNoticeText.Text = $"Aravals Stream {_availableUpdate.Version} is available.";
                    InstallUpdateButton.Content = "Download and install";
                    UpdateNotice.Visibility = Visibility.Visible;
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Write("Update", $"Check failed: {ex.Message}");
            UpdateButton.Content = "CHECK UPDATES";
            if (manual) MessageBox.Show(this, $"Could not check for updates: {ex.Message}", "Updates",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _updateCheckInProgress = false;
            UpdateButton.IsEnabled = true;
        }
    }

    private async void UpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_availableUpdate is not null) await InstallUpdateAsync();
        else await CheckForUpdatesAsync(true);
    }

    private async void InstallUpdateButton_Click(object sender, RoutedEventArgs e) => await InstallUpdateAsync();

    private void DismissUpdateNotice_Click(object sender, RoutedEventArgs e)
    {
        _dismissedUpdateVersion = _availableUpdate?.Version;
        UpdateNotice.Visibility = Visibility.Collapsed;
    }

    private async Task InstallUpdateAsync()
    {
        if (_availableUpdate is not { } update || _updateInstallInProgress) return;
        if (_streaming || _outputs.Count > 0 || _recording.State is RecordingState.Recording or RecordingState.Paused or RecordingState.Starting)
        {
            var answer = MessageBox.Show(this,
                "Installing the update will stop active streams and recording, then close Aravals Stream. Continue?",
                "Install update", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.OK) return;
        }
        _updateInstallInProgress = true;
        UpdateNotice.Visibility = Visibility.Visible;
        InstallUpdateButton.IsEnabled = false;
        UpdateButton.IsEnabled = false;
        try
        {
            var progress = new Progress<double>(value =>
                UpdateNoticeText.Text = $"Downloading {_availableUpdate?.Version}: {value:P0}");
            var installer = await _updateService.DownloadVerifiedInstallerAsync(update, progress);
            UpdateNoticeText.Text = "Installer verified. Starting update…";
            _ = Process.Start(new ProcessStartInfo
            {
                FileName = installer,
                Arguments = "/SILENT /CLOSEAPPLICATIONS /RESTARTAPPLICATIONS /NORESTART",
                UseShellExecute = true
            }) ?? throw new InvalidOperationException("The installer did not start.");
            _forceExit = true;
            Close();
        }
        catch (Exception ex)
        {
            AppLog.Write("Update", $"Install failed: {ex}");
            UpdateNoticeText.Text = "Update failed. Try again from the Updates button.";
            MessageBox.Show(this, $"The update was not installed: {ex.Message}", "Updates",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _updateInstallInProgress = false;
            InstallUpdateButton.IsEnabled = true;
            UpdateButton.IsEnabled = true;
        }
    }
}
