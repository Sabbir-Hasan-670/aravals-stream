namespace AravalsStream.Core.Platforms;

public static class PlatformRegistry
{
    private static readonly Dictionary<PlatformType, PlatformProfile> Profiles = new()
    {
        [PlatformType.YouTube] = new PlatformProfile
        {
            Id = "youtube",
            PlatformType = PlatformType.YouTube,
            DisplayName = "YouTube",
            Description = "Simultaneous Horizontal & Shorts/Vertical live streaming",
            BrandColor = "#FF0000",
            IconData = "M21.58,7.19C21.35,6.33 20.67,5.65 19.81,5.42C18.25,5 12,5 12,5C12,5 5.75,5 4.19,5.42C3.33,5.65 2.65,6.33 2.42,7.19C2,8.75 2,12 2,12C2,12 2,15.25 2.42,16.81C2.65,17.67 3.33,18.35 4.19,18.58C5.75,19 12,19 12,19C12,19 18.25,19 19.81,18.58C20.67,18.35 21.35,17.67 21.58,16.81C22,15.25 22,12 22,12C22,12 22,8.75 21.58,7.19Z M10,15.5L10,8.5L16,12L10,15.5Z",
            SupportsHorizontal = true,
            SupportsVertical = true,
            SupportsMultipleOutputs = true,
            DefaultVideoBitrateKbps = 6000,
            DefaultAudioBitrateKbps = 160,
            DefaultFps = 60,
            DefaultKeyframeIntervalSeconds = 2,
            DefaultProtocol = "RTMPS",
            DefaultServerUrl = "rtmps://a.rtmps.youtube.com/live2",
            DocumentationUrl = "https://support.google.com/youtube/answer/2853702",
            MaxVideoBitrateKbps = 12000
        },

        [PlatformType.Twitch] = new PlatformProfile
        {
            Id = "twitch",
            PlatformType = PlatformType.Twitch,
            DisplayName = "Twitch",
            Description = "Standard live broadcast (6000 kbps recommended cap)",
            BrandColor = "#9146FF",
            IconData = "M4.5,3L3,6.75v10.5h3.75V21h3.75l3.75-3.75H18L22.5,12.75V3H4.5z M20.25,12l-3,3h-3.75l-2.25,2.25V15H7.5V5.25h12.75V12z M15.75,8.25h1.5v4.5h-1.5V8.25z M11.25,8.25h1.5v4.5h-1.5V8.25z",
            SupportsHorizontal = true,
            SupportsVertical = true,
            SupportsMultipleOutputs = true,
            DefaultVideoBitrateKbps = 6000,
            DefaultAudioBitrateKbps = 160,
            DefaultFps = 60,
            DefaultKeyframeIntervalSeconds = 2,
            DefaultProtocol = "RTMP",
            DefaultServerUrl = "rtmp://live.twitch.tv/app",
            DocumentationUrl = "https://help.twitch.tv/s/article/broadcast-guidelines",
            MaxVideoBitrateKbps = 8000
        },

        [PlatformType.Kick] = new PlatformProfile
        {
            Id = "kick",
            PlatformType = PlatformType.Kick,
            DisplayName = "Kick",
            Description = "High-bitrate live streaming platform",
            BrandColor = "#53FC18",
            IconData = "M5,3h4v7l6-7h5l-7,8l7.5,10h-5l-5.5-7.5l-2,2.5v5h-4V3z",
            SupportsHorizontal = true,
            SupportsVertical = true,
            SupportsMultipleOutputs = true,
            DefaultVideoBitrateKbps = 6000,
            DefaultAudioBitrateKbps = 160,
            DefaultFps = 60,
            DefaultKeyframeIntervalSeconds = 2,
            DefaultProtocol = "RTMP",
            DefaultServerUrl = "rtmp://fa723fc1bfa2.global-contribute.live-video.net/app",
            DocumentationUrl = "https://help.kick.com",
            MaxVideoBitrateKbps = 10000
        },

        [PlatformType.Facebook] = new PlatformProfile
        {
            Id = "facebook",
            PlatformType = PlatformType.Facebook,
            DisplayName = "Facebook Live",
            Description = "Facebook Live video streaming via RTMPS",
            BrandColor = "#1877F2",
            IconData = "M12,2C6.477,2,2,6.477,2,12c0,4.991,3.657,9.128,8.438,9.879V14.89h-2.54V12h2.54V9.797c0-2.506,1.492-3.89,3.777-3.89c1.094,0,2.238,0.195,2.238,0.195v2.46h-1.26c-1.243,0-1.63,0.771-1.63,1.562V12h2.773l-0.443,2.89h-2.33v7C18.343,21.128,22,16.991,22,12C22,6.477,17.523,2,12,2z",
            SupportsHorizontal = true,
            SupportsVertical = true,
            SupportsMultipleOutputs = true,
            DefaultVideoBitrateKbps = 6000,
            DefaultAudioBitrateKbps = 160,
            DefaultFps = 60,
            DefaultKeyframeIntervalSeconds = 2,
            DefaultProtocol = "RTMPS",
            DefaultServerUrl = "rtmps://live-api-s.facebook.com:443/rtmp",
            DocumentationUrl = "https://www.facebook.com/help/live",
            MaxVideoBitrateKbps = 9000
        },

        [PlatformType.TikTok] = new PlatformProfile
        {
            Id = "tiktok",
            PlatformType = PlatformType.TikTok,
            DisplayName = "TikTok Live",
            Description = "Vertical video live stream for mobile audiences",
            BrandColor = "#FE2C55",
            IconData = "M16.6,5.82C15.65,5.17 15.05,4.07 15.01,2.83H11.5v12.28c0,1.38 -1.12,2.5 -2.5,2.5c-1.38,0 -2.5,-1.12 -2.5,-2.5c0,-1.38 1.12,-2.5 2.5,-2.5c0.27,0 0.52,0.04 0.76,0.12V9.06C9.52,9.02 9.26,9 9,9C5.69,9 3,11.69 3,15c0,3.31 2.69,6 6,6c3.31,0 6,-2.69 6,-6V8.65c1.45,1.04 3.23,1.66 5.16,1.68V6.85C18.84,6.84 17.58,6.44 16.6,5.82z",
            SupportsHorizontal = true,
            SupportsVertical = true,
            SupportsMultipleOutputs = false,
            DefaultVideoBitrateKbps = 4000,
            DefaultAudioBitrateKbps = 128,
            DefaultFps = 30,
            DefaultKeyframeIntervalSeconds = 2,
            DefaultProtocol = "RTMP",
            DefaultServerUrl = "",
            DocumentationUrl = "https://www.tiktok.com",
            MaxVideoBitrateKbps = 6000
        },

        [PlatformType.Custom] = new PlatformProfile
        {
            Id = "custom",
            PlatformType = PlatformType.Custom,
            DisplayName = "Custom RTMP",
            Description = "Direct RTMP / RTMPS stream to any server or relay",
            BrandColor = "#64748B",
            IconData = "M12,3c-4.97,0-9,4.03-9,9c0,2.12,0.74,4.07,1.97,5.61L6.4,16.18C5.53,14.97,5,13.54,5,12c0-3.87,3.13-7,7-7s7,3.13,7,7c0,1.54-0.53,2.97-1.4,4.18l1.43,1.43C20.26,16.07,21,14.12,21,12C21,7.03,16.97,3,12,3z M12,7c-2.76,0-5,2.24-5,5c0,1.21,0.43,2.32,1.15,3.19l1.44-1.44C9.23,13.23,9,12.64,9,12c0-1.66,1.34-3,3-3s3,1.34,3,3c0,0.64-0.23,1.23-0.59,1.75l1.44,1.44C16.57,14.32,17,13.21,17,12C17,9.24,14.76,7,12,7z M12,10.5c-0.83,0-1.5,0.67-1.5,1.5c0,0.53,0.28,0.99,0.7,1.26L9,19h6l-2.2-5.74C13.22,12.99,13.5,12.53,13.5,12C13.5,11.17,12.83,10.5,12,10.5z",
            SupportsHorizontal = true,
            SupportsVertical = true,
            SupportsMultipleOutputs = true,
            DefaultVideoBitrateKbps = 6000,
            DefaultAudioBitrateKbps = 160,
            DefaultFps = 60,
            DefaultKeyframeIntervalSeconds = 2,
            DefaultProtocol = "RTMP",
            DefaultServerUrl = "",
            DocumentationUrl = "",
            MaxVideoBitrateKbps = 20000
        }
    };

    public static IReadOnlyCollection<PlatformProfile> GetAll() => Profiles.Values;

    public static PlatformProfile Get(PlatformType type) =>
        Profiles.TryGetValue(type, out var profile) ? profile : Profiles[PlatformType.Custom];

    public static PlatformProfile Get(string? idOrName)
    {
        if (string.IsNullOrWhiteSpace(idOrName)) return Profiles[PlatformType.Custom];

        foreach (var profile in Profiles.Values)
        {
            if (string.Equals(profile.Id, idOrName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(profile.DisplayName, idOrName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(profile.PlatformType.ToString(), idOrName, StringComparison.OrdinalIgnoreCase))
            {
                return profile;
            }
        }
        return Profiles[PlatformType.Custom];
    }
}
