using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using AravalsStream.Core.Platforms;

namespace AravalsStream.Core.Models;

public sealed class PlatformDestinationGroup : INotifyPropertyChanged
{
    private Guid _id = Guid.NewGuid();
    private PlatformType _platformType = PlatformType.Custom;
    private string _platform = "Custom RTMP";
    private string _name = "Custom RTMP";
    private bool _enabled = true;
    private RoutingMode _routing = RoutingMode.Horizontal;
    private int _order = 0;
    private bool _linkSettings = false;
    private string _serverUrl = "";
    private string? _streamKeyReference;
    private Destination _horizontal = new() { OutputMode = OutputMode.Horizontal };
    private Destination _vertical = new() { OutputMode = OutputMode.Vertical };
    private DateTimeOffset _createdAt = DateTimeOffset.UtcNow;
    private DateTimeOffset _updatedAt = DateTimeOffset.UtcNow;

    private ConfigurationMode _configurationMode = ConfigurationMode.ManualRtmp;
    private string? _broadcastId;
    private string? _streamId;
    private string? _liveChatId;
    private string? _boundAccountId;
    private string? _broadcastTitle;
    private string? _broadcastStatus;
    public string? TwitchGameId { get; set; }
    public string? TwitchGameName { get; set; }
    public string? TwitchLanguage { get; set; }
    public List<string> TwitchTags { get; set; } = [];
    public string? TwitchIngestName { get; set; }
    public long? KickCategoryId { get; set; }
    public string? KickCategoryName { get; set; }
    public List<string> KickTags { get; set; } = [];
    public string? FacebookPageId { get; set; }
    public string? FacebookDescription { get; set; }

    public ConfigurationMode ConfigurationMode
    {
        get => _configurationMode;
        set { _configurationMode = value; OnPropertyChanged(); OnPropertyChanged(nameof(ModeBadgeText)); }
    }

    public string? BroadcastId
    {
        get => _broadcastId;
        set { _broadcastId = value; OnPropertyChanged(); }
    }

    public string? StreamId
    {
        get => _streamId;
        set { _streamId = value; OnPropertyChanged(); }
    }

    public string? LiveChatId
    {
        get => _liveChatId;
        set { _liveChatId = value; OnPropertyChanged(); }
    }

    public string? BoundAccountId
    {
        get => _boundAccountId;
        set { _boundAccountId = value; OnPropertyChanged(); }
    }

    public string? BroadcastTitle
    {
        get => _broadcastTitle;
        set { _broadcastTitle = value; OnPropertyChanged(); }
    }

    public string? BroadcastStatus
    {
        get => _broadcastStatus;
        set { _broadcastStatus = value; OnPropertyChanged(); }
    }

    private DestinationGroupStatus _status = DestinationGroupStatus.Offline;
    private string _statusSummary = "OFFLINE";
    private string _horizontalDetailsText = "";
    private string _verticalDetailsText = "";

    public Guid Id
    {
        get => _id;
        set { _id = value; OnPropertyChanged(); }
    }

    public PlatformType PlatformType
    {
        get => _platformType;
        set { _platformType = value; OnPropertyChanged(); OnPropertyChanged(nameof(IconData)); OnPropertyChanged(nameof(BrandColor)); }
    }

    public string Platform
    {
        get => _platform;
        set { _platform = value; OnPropertyChanged(); }
    }

    public string Name
    {
        get => _name;
        set { _name = value; OnPropertyChanged(); }
    }

    public bool Enabled
    {
        get => _enabled;
        set { _enabled = value; OnPropertyChanged(); OnPropertyChanged(nameof(Status)); }
    }

    public RoutingMode Routing
    {
        get => _routing;
        set
        {
            _routing = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(RoutingText));
            OnPropertyChanged(nameof(ModeBadgeText));
            OnPropertyChanged(nameof(IsHorizontalVisible));
            OnPropertyChanged(nameof(IsVerticalVisible));
            OnPropertyChanged(nameof(IsHSelected));
            OnPropertyChanged(nameof(IsVSelected));
            OnPropertyChanged(nameof(IsBothSelected));
            OnPropertyChanged(nameof(IsOffSelected));
        }
    }

    public int Order
    {
        get => _order;
        set { _order = value; OnPropertyChanged(); }
    }

    public bool LinkSettings
    {
        get => _linkSettings;
        set { _linkSettings = value; OnPropertyChanged(); }
    }

    public string ServerUrl
    {
        get => _serverUrl;
        set { _serverUrl = value; OnPropertyChanged(); }
    }

    public string? StreamKeyReference
    {
        get => _streamKeyReference;
        set { _streamKeyReference = value; OnPropertyChanged(); }
    }

    public Destination Horizontal
    {
        get => _horizontal;
        set { _horizontal = value; OnPropertyChanged(); }
    }

    public Destination Vertical
    {
        get => _vertical;
        set { _vertical = value; OnPropertyChanged(); }
    }

    public DateTimeOffset CreatedAt
    {
        get => _createdAt;
        set { _createdAt = value; OnPropertyChanged(); }
    }

    public DateTimeOffset UpdatedAt
    {
        get => _updatedAt;
        set { _updatedAt = value; OnPropertyChanged(); }
    }

    [JsonIgnore]
    public DestinationGroupStatus Status
    {
        get => _status;
        set { _status = value; OnPropertyChanged(); }
    }

    [JsonIgnore]
    public string StatusSummary
    {
        get => _statusSummary;
        set { _statusSummary = value; OnPropertyChanged(); }
    }

    [JsonIgnore]
    public string RoutingText => Routing switch
    {
        RoutingMode.Both => "BOTH",
        RoutingMode.Horizontal => "HORIZONTAL",
        RoutingMode.Vertical => "VERTICAL",
        _ => "OFF"
    };

    [JsonIgnore]
    public string ModeBadgeText => $"{(ConfigurationMode == ConfigurationMode.NativeApi ? "NATIVE ACCOUNT" : "MANUAL RTMP")} • {RoutingText}";

    [JsonIgnore]
    public string IconData => PlatformRegistry.Get(PlatformType).IconData;

    [JsonIgnore]
    public string BrandColor => PlatformRegistry.Get(PlatformType).BrandColor;

    [JsonIgnore]
    public bool IsHorizontalVisible => Routing is RoutingMode.Horizontal or RoutingMode.Both;

    [JsonIgnore]
    public bool IsVerticalVisible => Routing is RoutingMode.Vertical or RoutingMode.Both;

    [JsonIgnore]
    public bool IsHSelected => Routing == RoutingMode.Horizontal;

    [JsonIgnore]
    public bool IsVSelected => Routing == RoutingMode.Vertical;

    [JsonIgnore]
    public bool IsBothSelected => Routing == RoutingMode.Both;

    [JsonIgnore]
    public bool IsOffSelected => Routing == RoutingMode.Off;

    [JsonIgnore]
    public string HorizontalStatusText => Horizontal.Status.ToString().ToUpperInvariant();

    [JsonIgnore]
    public string VerticalStatusText => Vertical.Status.ToString().ToUpperInvariant();

    [JsonIgnore]
    public string HorizontalStatusDisplay => $"● H {HorizontalStatusText}";

    [JsonIgnore]
    public string VerticalStatusDisplay => $"● V {VerticalStatusText}";

    [JsonIgnore]
    public string HorizontalDetailsText
    {
        get => string.IsNullOrWhiteSpace(_horizontalDetailsText)
            ? $"{Horizontal.VideoBitrateKbps} kbps • {Horizontal.FrameRate} FPS"
            : _horizontalDetailsText;
        set { _horizontalDetailsText = value; OnPropertyChanged(); }
    }

    [JsonIgnore]
    public string VerticalDetailsText
    {
        get => string.IsNullOrWhiteSpace(_verticalDetailsText)
            ? $"{Vertical.VideoBitrateKbps} kbps • {Vertical.FrameRate} FPS"
            : _verticalDetailsText;
        set { _verticalDetailsText = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    public void NotifyAllPropertiesChanged() =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));

    public IEnumerable<Destination> GetActiveDestinations()
    {
        if (!Enabled || Routing == RoutingMode.Off)
            yield break;

        if (Routing is RoutingMode.Horizontal or RoutingMode.Both)
            yield return Horizontal;

        if (Routing is RoutingMode.Vertical or RoutingMode.Both)
            yield return Vertical;
    }

    public void EnsureChildDestinations()
    {
        Horizontal ??= new Destination { OutputMode = OutputMode.Horizontal };
        Vertical ??= new Destination { OutputMode = OutputMode.Vertical };

        Horizontal.OutputMode = OutputMode.Horizontal;
        Vertical.OutputMode = OutputMode.Vertical;

        Horizontal.Platform = Platform;
        Vertical.Platform = Platform;

        Horizontal.Name = $"{Name} - H";
        Vertical.Name = $"{Name} - V";

        if (string.IsNullOrWhiteSpace(Horizontal.StreamUrl))
            Horizontal.StreamUrl = ServerUrl;
        if (string.IsNullOrWhiteSpace(Vertical.StreamUrl))
            Vertical.StreamUrl = ServerUrl;

        if (string.IsNullOrWhiteSpace(Horizontal.StreamKeyReference))
            Horizontal.StreamKeyReference = StreamKeyReference;
        if (string.IsNullOrWhiteSpace(Vertical.StreamKeyReference))
            Vertical.StreamKeyReference = StreamKeyReference;

        Horizontal.Enabled = Enabled && Routing is RoutingMode.Horizontal or RoutingMode.Both;
        Vertical.Enabled = Enabled && Routing is RoutingMode.Vertical or RoutingMode.Both;
    }

    public void UpdateAggregation()
    {
        EnsureChildDestinations();

        if (!Enabled)
        {
            Status = DestinationGroupStatus.Disabled;
            StatusSummary = "DISABLED";
            NotifyAllPropertiesChanged();
            return;
        }

        if (Routing == RoutingMode.Off)
        {
            Status = DestinationGroupStatus.Offline;
            StatusSummary = "ROUTING OFF";
            NotifyAllPropertiesChanged();
            return;
        }

        if (Routing == RoutingMode.Horizontal)
        {
            Status = MapChildStatus(Horizontal.Status);
            StatusSummary = $"H {Horizontal.Status.ToString().ToUpperInvariant()}";
            NotifyAllPropertiesChanged();
            return;
        }

        if (Routing == RoutingMode.Vertical)
        {
            Status = MapChildStatus(Vertical.Status);
            StatusSummary = $"V {Vertical.Status.ToString().ToUpperInvariant()}";
            NotifyAllPropertiesChanged();
            return;
        }

        // Both mode
        var h = Horizontal.Status;
        var v = Vertical.Status;

        if (h == DestinationStatus.Live && v == DestinationStatus.Live)
        {
            Status = DestinationGroupStatus.Live;
            StatusSummary = "LIVE";
        }
        else if (h == DestinationStatus.Live || v == DestinationStatus.Live)
        {
            Status = DestinationGroupStatus.Partial;
            StatusSummary = $"H {h.ToString().ToUpperInvariant()} • V {v.ToString().ToUpperInvariant()}";
        }
        else if (h is DestinationStatus.FallingBehind or DestinationStatus.Reconnecting || v is DestinationStatus.FallingBehind or DestinationStatus.Reconnecting)
        {
            Status = DestinationGroupStatus.Reconnecting;
            StatusSummary = $"H {h.ToString().ToUpperInvariant()} • V {v.ToString().ToUpperInvariant()}";
        }
        else if (h == DestinationStatus.Connecting || v == DestinationStatus.Connecting)
        {
            Status = DestinationGroupStatus.Connecting;
            StatusSummary = $"H {h.ToString().ToUpperInvariant()} • V {v.ToString().ToUpperInvariant()}";
        }
        else if (h == DestinationStatus.Error && v == DestinationStatus.Error)
        {
            Status = DestinationGroupStatus.Error;
            StatusSummary = "ERROR";
        }
        else if (h == DestinationStatus.Error || v == DestinationStatus.Error)
        {
            Status = DestinationGroupStatus.Error;
            StatusSummary = $"H {h.ToString().ToUpperInvariant()} • V {v.ToString().ToUpperInvariant()}";
        }
        else if (h == DestinationStatus.Stopping || v == DestinationStatus.Stopping)
        {
            Status = DestinationGroupStatus.Stopping;
            StatusSummary = "STOPPING";
        }
        else
        {
            Status = DestinationGroupStatus.Offline;
            StatusSummary = "OFFLINE";
        }

        NotifyAllPropertiesChanged();
    }

    private static DestinationGroupStatus MapChildStatus(DestinationStatus s) => s switch
    {
        DestinationStatus.Live => DestinationGroupStatus.Live,
        DestinationStatus.Connecting => DestinationGroupStatus.Connecting,
        DestinationStatus.FallingBehind or DestinationStatus.Reconnecting => DestinationGroupStatus.Reconnecting,
        DestinationStatus.Stopping => DestinationGroupStatus.Stopping,
        DestinationStatus.Error => DestinationGroupStatus.Error,
        DestinationStatus.Disabled => DestinationGroupStatus.Disabled,
        _ => DestinationGroupStatus.Offline
    };

    public PlatformDestinationGroup Copy()
    {
        return new PlatformDestinationGroup
        {
            Id = Id,
            PlatformType = PlatformType,
            Platform = Platform,
            Name = Name,
            Enabled = Enabled,
            Routing = Routing,
            Order = Order,
            LinkSettings = LinkSettings,
            ServerUrl = ServerUrl,
            StreamKeyReference = StreamKeyReference,
            ConfigurationMode = ConfigurationMode,
            BroadcastId = BroadcastId,
            StreamId = StreamId,
            LiveChatId = LiveChatId,
            BoundAccountId = BoundAccountId,
            BroadcastTitle = BroadcastTitle,
            BroadcastStatus = BroadcastStatus,
            TwitchGameId = TwitchGameId,
            TwitchGameName = TwitchGameName,
            TwitchLanguage = TwitchLanguage,
            TwitchTags = [.. TwitchTags],
            TwitchIngestName = TwitchIngestName,
            KickCategoryId = KickCategoryId,
            KickCategoryName = KickCategoryName,
            KickTags = [.. KickTags],
            FacebookPageId = FacebookPageId,
            FacebookDescription = FacebookDescription,
            Horizontal = Horizontal.Copy(),
            Vertical = Vertical.Copy(),
            CreatedAt = CreatedAt,
            UpdatedAt = UpdatedAt
        };
    }

    public PlatformDestinationGroup Duplicate(bool copySecrets = false)
    {
        var dup = Copy();
        dup.Id = Guid.NewGuid();
        dup.Name = $"{Name} Copy";
        dup.Enabled = false;
        dup.Status = DestinationGroupStatus.Disabled;
        dup.CreatedAt = DateTimeOffset.UtcNow;
        dup.UpdatedAt = dup.CreatedAt;

        dup.Horizontal.Id = Guid.NewGuid();
        dup.Horizontal.Name = $"{dup.Name} - H";
        dup.Horizontal.Enabled = false;
        dup.Horizontal.Status = DestinationStatus.Disabled;

        dup.Vertical.Id = Guid.NewGuid();
        dup.Vertical.Name = $"{dup.Name} - V";
        dup.Vertical.Enabled = false;
        dup.Vertical.Status = DestinationStatus.Disabled;

        if (!copySecrets)
        {
            dup.StreamKeyReference = null;
            dup.Horizontal.StreamKeyReference = null;
            dup.Vertical.StreamKeyReference = null;
        }

        return dup;
    }

    public static PlatformDestinationGroup CreateFromProfile(PlatformProfile profile, string? name = null)
    {
        var groupName = !string.IsNullOrWhiteSpace(name) ? name : profile.DisplayName;
        var routing = profile.PlatformType == PlatformType.TikTok
            ? RoutingMode.Vertical
            : profile.SupportsHorizontal && profile.SupportsVertical
                ? RoutingMode.Horizontal
                : profile.SupportsVertical
                    ? RoutingMode.Vertical
                    : RoutingMode.Horizontal;

        var group = new PlatformDestinationGroup
        {
            PlatformType = profile.PlatformType,
            Platform = profile.DisplayName,
            Name = groupName,
            ServerUrl = profile.DefaultServerUrl,
            Routing = routing,
            Horizontal = new Destination
            {
                Platform = profile.DisplayName,
                Name = $"{groupName} - H",
                OutputMode = OutputMode.Horizontal,
                StreamUrl = profile.DefaultServerUrl,
                VideoBitrateKbps = profile.DefaultVideoBitrateKbps,
                AudioBitrateKbps = profile.DefaultAudioBitrateKbps,
                FrameRate = profile.DefaultFps,
                KeyframeIntervalSeconds = profile.DefaultKeyframeIntervalSeconds,
                Protocol = profile.DefaultProtocol
            },
            Vertical = new Destination
            {
                Platform = profile.DisplayName,
                Name = $"{groupName} - V",
                OutputMode = OutputMode.Vertical,
                StreamUrl = profile.DefaultServerUrl,
                VideoBitrateKbps = Math.Min(profile.DefaultVideoBitrateKbps, 5000),
                AudioBitrateKbps = profile.DefaultAudioBitrateKbps,
                FrameRate = profile.PlatformType == PlatformType.TikTok ? 30 : profile.DefaultFps,
                KeyframeIntervalSeconds = profile.DefaultKeyframeIntervalSeconds,
                Protocol = profile.DefaultProtocol
            }
        };

        group.EnsureChildDestinations();
        return group;
    }
}
