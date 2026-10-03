namespace AravalsStream.Core.Settings;

public sealed class GeneralSettings
{
    public bool ConfirmExitWhileLive { get; set; } = true;
    public bool MinimizeToTray { get; set; } = false;
    public bool StartMinimized { get; set; } = false;
    public bool CheckForUpdates { get; set; } = false;
    public string Language { get; set; } = "en-US";
}
