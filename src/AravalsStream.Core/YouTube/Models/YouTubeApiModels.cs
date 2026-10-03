using System.Text.Json.Serialization;

namespace AravalsStream.Core.YouTube.Models;

// ================= Channel Models =================
public sealed class YouTubeChannelListResponse
{
    [JsonPropertyName("items")]
    public List<YouTubeChannelItem>? Items { get; set; }
}

public sealed class YouTubeChannelItem
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("snippet")]
    public YouTubeChannelSnippet? Snippet { get; set; }

    [JsonPropertyName("statistics")]
    public YouTubeChannelStatistics? Statistics { get; set; }
}

public sealed class YouTubeChannelSnippet
{
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("thumbnails")]
    public YouTubeThumbnails? Thumbnails { get; set; }
}

public sealed class YouTubeThumbnails
{
    [JsonPropertyName("default")]
    public YouTubeThumbnailItem? Default { get; set; }

    [JsonPropertyName("medium")]
    public YouTubeThumbnailItem? Medium { get; set; }
}

public sealed class YouTubeThumbnailItem
{
    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;
}

public sealed class YouTubeChannelStatistics
{
    [JsonPropertyName("subscriberCount")]
    public string? SubscriberCount { get; set; }

    [JsonPropertyName("hiddenSubscriberCount")]
    public bool HiddenSubscriberCount { get; set; }
}

// ================= Broadcast Models =================
public sealed class YouTubeBroadcastListResponse
{
    [JsonPropertyName("items")]
    public List<YouTubeBroadcastItem>? Items { get; set; }
}

public sealed class YouTubeBroadcastItem
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("snippet")]
    public YouTubeBroadcastSnippet? Snippet { get; set; }

    [JsonPropertyName("status")]
    public YouTubeBroadcastStatus? Status { get; set; }

    [JsonPropertyName("contentDetails")]
    public YouTubeBroadcastContentDetails? ContentDetails { get; set; }
}

public sealed class YouTubeBroadcastSnippet
{
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("scheduledStartTime")]
    public DateTimeOffset? ScheduledStartTime { get; set; }

    [JsonPropertyName("actualStartTime")]
    public DateTimeOffset? ActualStartTime { get; set; }

    [JsonPropertyName("liveChatId")]
    public string? LiveChatId { get; set; }
}

public sealed class YouTubeBroadcastStatus
{
    [JsonPropertyName("lifeCycleStatus")]
    public string LifeCycleStatus { get; set; } = "created"; // created, ready, testing, live, complete

    [JsonPropertyName("privacyStatus")]
    public string PrivacyStatus { get; set; } = "private"; // public, unlisted, private

    [JsonPropertyName("recordingStatus")]
    public string? RecordingStatus { get; set; }

    [JsonPropertyName("selfDeclaredMadeForKids")]
    public bool SelfDeclaredMadeForKids { get; set; } = false;
}

public sealed class YouTubeBroadcastContentDetails
{
    [JsonPropertyName("boundStreamId")]
    public string? BoundStreamId { get; set; }

    [JsonPropertyName("enableAutoStart")]
    public bool EnableAutoStart { get; set; } = false;

    [JsonPropertyName("enableAutoStop")]
    public bool EnableAutoStop { get; set; } = false;

    [JsonPropertyName("enableDvr")]
    public bool EnableDvr { get; set; } = true;
}

// ================= Live Stream Models =================
public sealed class YouTubeLiveStreamListResponse
{
    [JsonPropertyName("items")]
    public List<YouTubeLiveStreamItem>? Items { get; set; }
}

public sealed class YouTubeLiveStreamItem
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("snippet")]
    public YouTubeLiveStreamSnippet? Snippet { get; set; }

    [JsonPropertyName("cdn")]
    public YouTubeCdnSettings? Cdn { get; set; }

    [JsonPropertyName("status")]
    public YouTubeLiveStreamStatus? Status { get; set; }
}

public sealed class YouTubeLiveStreamSnippet
{
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;
}

public sealed class YouTubeCdnSettings
{
    [JsonPropertyName("ingestionType")]
    public string IngestionType { get; set; } = "rtmp";

    [JsonPropertyName("ingestionInfo")]
    public YouTubeIngestionInfo? IngestionInfo { get; set; }

    [JsonPropertyName("resolution")]
    public string Resolution { get; set; } = "1080p";

    [JsonPropertyName("frameRate")]
    public string FrameRate { get; set; } = "60fps";
}

public sealed class YouTubeIngestionInfo
{
    [JsonPropertyName("ingestionAddress")]
    public string IngestionAddress { get; set; } = string.Empty;

    [JsonPropertyName("streamName")]
    public string StreamName { get; set; } = string.Empty; // sensitive stream key

    [JsonPropertyName("rtmpsIngestionAddress")]
    public string? RtmpsIngestionAddress { get; set; }
}

public sealed class YouTubeLiveStreamStatus
{
    [JsonPropertyName("streamStatus")]
    public string StreamStatus { get; set; } = "ready"; // active, created, error, inactive, ready
}

// ================= Video & Viewers Models =================
public sealed class YouTubeVideoListResponse
{
    [JsonPropertyName("items")]
    public List<YouTubeVideoItem>? Items { get; set; }
}

public sealed class YouTubeVideoItem
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("liveStreamingDetails")]
    public YouTubeLiveStreamingDetails? LiveStreamingDetails { get; set; }
}

public sealed class YouTubeLiveStreamingDetails
{
    [JsonPropertyName("concurrentViewers")]
    public string? ConcurrentViewers { get; set; }

    [JsonPropertyName("activeLiveChatId")]
    public string? ActiveLiveChatId { get; set; }
}

// ================= Live Chat Models =================
public sealed class YouTubeLiveChatMessageListResponse
{
    [JsonPropertyName("pollingIntervalMillis")]
    public ulong PollingIntervalMillis { get; set; } = 3000;

    [JsonPropertyName("nextPageToken")]
    public string? NextPageToken { get; set; }

    [JsonPropertyName("items")]
    public List<YouTubeLiveChatMessageItem>? Items { get; set; }
}

public sealed class YouTubeLiveChatMessageItem
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("snippet")]
    public YouTubeChatMessageSnippet? Snippet { get; set; }

    [JsonPropertyName("authorDetails")]
    public YouTubeChatAuthorDetails? AuthorDetails { get; set; }
}

public sealed class YouTubeChatMessageSnippet
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "textMessageEvent"; // textMessageEvent, superChatEvent, superStickerEvent, newSponsorEvent

    [JsonPropertyName("liveChatId")]
    public string LiveChatId { get; set; } = string.Empty;

    [JsonPropertyName("displayMessage")]
    public string? DisplayMessage { get; set; }

    [JsonPropertyName("publishedAt")]
    public DateTimeOffset PublishedAt { get; set; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("textMessageDetails")]
    public YouTubeTextMessageDetails? TextMessageDetails { get; set; }

    [JsonPropertyName("superChatDetails")]
    public YouTubeSuperChatDetails? SuperChatDetails { get; set; }
}

public sealed class YouTubeTextMessageDetails
{
    [JsonPropertyName("messageText")]
    public string MessageText { get; set; } = string.Empty;
}

public sealed class YouTubeSuperChatDetails
{
    [JsonPropertyName("amountDisplayString")]
    public string AmountDisplayString { get; set; } = string.Empty;

    [JsonPropertyName("userComment")]
    public string? UserComment { get; set; }
}

public sealed class YouTubeChatAuthorDetails
{
    [JsonPropertyName("channelId")]
    public string ChannelId { get; set; } = string.Empty;

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = string.Empty;

    [JsonPropertyName("profileImageUrl")]
    public string? ProfileImageUrl { get; set; }

    [JsonPropertyName("isChatOwner")]
    public bool IsChatOwner { get; set; }

    [JsonPropertyName("isChatModerator")]
    public bool IsChatModerator { get; set; }

    [JsonPropertyName("isChatSponsor")]
    public bool IsChatSponsor { get; set; }

    [JsonPropertyName("isVerified")]
    public bool IsVerified { get; set; }
}
