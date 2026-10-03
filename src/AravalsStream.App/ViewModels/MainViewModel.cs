using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using AravalsStream.Core.Models;
using AravalsStream.Core.Services;

namespace AravalsStream.App.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    public ObservableCollection<Scene> Scenes { get; } = new([
        new Scene { Name = "Gaming" },
        new Scene { Name = "Just Chatting" },
        new Scene { Name = "BRB" },
        new Scene { Name = "Starting Soon" },
        new Scene { Name = "Ending" }
    ]);

    public ObservableCollection<Destination> Destinations { get; } = [];
    public ObservableCollection<PlatformDestinationGroup> DestinationGroups { get; } = [];

    public ObservableCollection<ChatMessage> AllChatMessages { get; } = [];
    public ObservableCollection<ChatMessage> YouTubeChatMessages { get; } = [];
    public ObservableCollection<ChatMessage> TwitchChatMessages { get; } = [];
    public ObservableCollection<ChatMessage> KickChatMessages { get; } = [];
    public ObservableCollection<ChatMessage> FacebookChatMessages { get; } = [];
    private string _facebookBroadcastStatus = "OFFLINE";
    public string FacebookBroadcastStatus { get => _facebookBroadcastStatus; set => Set(ref _facebookBroadcastStatus, value); }
    private string _facebookPageName = "—";
    public string FacebookPageName { get => _facebookPageName; set => Set(ref _facebookPageName, value); }
    private string _facebookViewers = "—";
    public string FacebookViewers { get => _facebookViewers; set => Set(ref _facebookViewers, value); }
    private string _facebookCommentsStatus = "Offline";
    public string FacebookCommentsStatus { get => _facebookCommentsStatus; set => Set(ref _facebookCommentsStatus, value); }
    private string _tikTokBroadcastStatus = "OFFLINE";
    public string TikTokBroadcastStatus { get => _tikTokBroadcastStatus; set => Set(ref _tikTokBroadcastStatus, value); }
    private string _tikTokAccountName = "—";
    public string TikTokAccountName { get => _tikTokAccountName; set => Set(ref _tikTokAccountName, value); }
    private string _tikTokViewers = "—";
    public string TikTokViewers { get => _tikTokViewers; set => Set(ref _tikTokViewers, value); }
    private string _tikTokChatStatus = "Unavailable";
    public string TikTokChatStatus { get => _tikTokChatStatus; set => Set(ref _tikTokChatStatus, value); }
    private string _kickBroadcastStatus = "OFFLINE";
    public string KickBroadcastStatus { get => _kickBroadcastStatus; set => Set(ref _kickBroadcastStatus, value); }
    private string _kickViewers = "—";
    public string KickViewers { get => _kickViewers; set => Set(ref _kickViewers, value); }
    private string _kickFollowers = "—";
    public string KickFollowers { get => _kickFollowers; set => Set(ref _kickFollowers, value); }
    private string _kickCategory = "—";
    public string KickCategory { get => _kickCategory; set => Set(ref _kickCategory, value); }
    private string _kickChatStatus = "Offline";
    public string KickChatStatus { get => _kickChatStatus; set => Set(ref _kickChatStatus, value); }
    private string _twitchBroadcastStatus = "OFFLINE";
    public string TwitchBroadcastStatus { get => _twitchBroadcastStatus; set => Set(ref _twitchBroadcastStatus, value); }
    private string _twitchViewers = "—";
    public string TwitchViewers { get => _twitchViewers; set => Set(ref _twitchViewers, value); }
    private string _twitchFollowers = "—";
    public string TwitchFollowers { get => _twitchFollowers; set => Set(ref _twitchFollowers, value); }
    private string _twitchCategory = "—";
    public string TwitchCategory { get => _twitchCategory; set => Set(ref _twitchCategory, value); }
    private string _twitchChatStatus = "Offline";
    public string TwitchChatStatus { get => _twitchChatStatus; set => Set(ref _twitchChatStatus, value); }

    private string _youTubeBroadcastStatus = "OFFLINE";
    public string YouTubeBroadcastStatus
    {
        get => _youTubeBroadcastStatus;
        set => Set(ref _youTubeBroadcastStatus, value);
    }

    private string _youTubeConcurrentViewers = "—";
    public string YouTubeConcurrentViewers
    {
        get => _youTubeConcurrentViewers;
        set => Set(ref _youTubeConcurrentViewers, value);
    }

    private string _youTubeSubscribers = "—";
    public string YouTubeSubscribers
    {
        get => _youTubeSubscribers;
        set => Set(ref _youTubeSubscribers, value);
    }

    private string _youTubeChatStatus = "Offline";
    public string YouTubeChatStatus
    {
        get => _youTubeChatStatus;
        set => Set(ref _youTubeChatStatus, value);
    }

    private bool _isYouTubeLiveActive;
    public bool IsYouTubeLiveActive
    {
        get => _isYouTubeLiveActive;
        set => Set(ref _isYouTubeLiveActive, value);
    }

    private Scene? _selectedScene;
    public Scene? SelectedScene
    {
        get => _selectedScene;
        set
        {
            if (value is not null && !value.Enabled)
            {
                return;
            }
            if (Set(ref _selectedScene, value))
            {
                UpdateActiveSceneStates();
            }
        }
    }

    private BitmapSource? _previewFrame;
    public BitmapSource? PreviewFrame
    {
        get => _previewFrame;
        set => Set(ref _previewFrame, value);
    }

    private OutputMode _previewMode = OutputMode.Both;
    public OutputMode PreviewMode
    {
        get => _previewMode;
        set => Set(ref _previewMode, value);
    }

    public int EnabledOutputCount => DestinationGroups.Count > 0
        ? DestinationGroups.Where(g => g.Enabled && g.Routing != RoutingMode.Off).Sum(g => g.GetActiveDestinations().Count())
        : Destinations.Count(x => x.Enabled);

    public event Action? RequestCreateScene;
    public event Action? RequestAddDestination;
    public event Action? RequestToggleStream;

    public ICommand AddSceneCommand { get; }
    public ICommand AddDestinationCommand { get; }
    public ICommand SetHorizontalCommand { get; }
    public ICommand SetVerticalCommand { get; }
    public ICommand SetBothCommand { get; }
    public ICommand StartStreamCommand { get; }

    public MainViewModel()
    {
        SelectedScene = Scenes[0];
        UpdateActiveSceneStates();

        AddSceneCommand = new RelayCommand(() =>
        {
            if (RequestCreateScene is not null)
            {
                RequestCreateScene.Invoke();
            }
            else
            {
                var name = SceneManager.GenerateDuplicateName("Scene", Scenes.Select(s => s.Name));
                var scene = new Scene { Name = name, Enabled = true };
                Scenes.Add(scene);
                SelectedScene = scene;
            }
        });

        AddDestinationCommand = new RelayCommand(() => RequestAddDestination?.Invoke());
        SetHorizontalCommand = new RelayCommand(() => PreviewMode = OutputMode.Horizontal);
        SetVerticalCommand = new RelayCommand(() => PreviewMode = OutputMode.Vertical);
        SetBothCommand = new RelayCommand(() => PreviewMode = OutputMode.Both);
        StartStreamCommand = new RelayCommand(() => RequestToggleStream?.Invoke());
    }

    public void UpdateActiveSceneStates()
    {
        foreach (var scene in Scenes)
        {
            scene.IsActive = (scene == _selectedScene);
        }
    }
}

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

