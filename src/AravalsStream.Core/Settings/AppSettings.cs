using AravalsStream.Core.Accounts;
using AravalsStream.Core.Alerts;
using AravalsStream.Core.Models;
using AravalsStream.Core.Recording.Models;

namespace AravalsStream.Core.Settings;

public sealed class AppSettings
{
    public int SettingsSchemaVersion { get; set; } = 8;
    public bool FirstRunCompleted { get; set; } = false;
    public GeneralSettings General { get; set; } = new();
    public PerformanceSettings Performance { get; set; } = new();
    public AudioSettings Audio { get; set; } = new();
    public CanvasSettings Canvas { get; set; } = new();
    public StreamingSettings Streaming { get; set; } = new();
    public List<HotkeyBinding> Hotkeys { get; set; } = [];
    public List<AudioRouteSetting> AudioRoutes { get; set; } = [];
    public List<Scene> Scenes { get; set; } = [];
    public List<DisplayCaptureSource> CaptureSources { get; set; } = [];
    public List<CaptureResource> CaptureResources { get; set; } = [];
    public List<Destination> Destinations { get; set; } = [];
    public List<PlatformDestinationGroup> DestinationGroups { get; set; } = [];
    public string? SelectedSceneId { get; set; }
    public OutputMode PreviewMode { get; set; } = OutputMode.Both;
    public RecordingSettings Recording { get; set; } = new();
    public GoogleOAuthSettings GoogleOAuth { get; set; } = new();
    public YouTubeAccount? YouTubeAccount { get; set; }
    public List<YouTubeAccount> YouTubeAccounts { get; set; } = [];
    public TwitchOAuthSettings TwitchOAuth { get; set; } = new();
    public TwitchAccount? TwitchAccount { get; set; }
    public KickOAuthSettings KickOAuth { get; set; } = new();
    public KickAccount? KickAccount { get; set; }
    public FacebookAccount? FacebookAccount { get; set; }
    public TikTokOAuthSettings TikTokOAuth { get; set; } = new();
    public TikTokAccount? TikTokAccount { get; set; }
    public AlertSettings Alerts { get; set; } = new();
    public RelaySettings Relay { get; set; } = new();
}
