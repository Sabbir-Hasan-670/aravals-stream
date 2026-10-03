namespace AravalsStream.Core.Accounts;

public enum PlatformAccountState { Disconnected, Connected, Refreshing, NeedsReauthentication, Error, Connecting, PermissionMissing }

public sealed class TwitchAccount : IPlatformAccount
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Platform => "Twitch";
    public string UserId { get; set; } = string.Empty;
    public string Login { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? ProfileImageUrl { get; set; }
    public string? Description { get; set; }
    public bool Connected { get; set; }
    public string? TokenReference { get; set; }
    public DateTimeOffset LastAuthenticated { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastValidatedUtc { get; set; }
    public PlatformAccountState State { get; set; } = PlatformAccountState.Disconnected;
    public string ChannelId => UserId;
    public string ChannelTitle => DisplayName;
    public string? ChannelThumbnailUrl => ProfileImageUrl;
    public long? FollowerCount { get; set; }
    public int? ViewerCount { get; set; }
}
