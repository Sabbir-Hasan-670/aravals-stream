namespace AravalsStream.Core.Accounts;

public sealed class TikTokAccount : IPlatformAccount
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Platform => "TikTok";
    public string OpenId { get; set; } = "";
    public string UnionId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string? AvatarUrl { get; set; }
    public bool Connected { get; set; }
    public string? TokenReference { get; set; }
    public string? RefreshTokenReference { get; set; }
    public DateTimeOffset LastAuthenticated { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? TokenExpiryUtc { get; set; }
    public PlatformAccountState State { get; set; } = PlatformAccountState.Disconnected;

    public string ChannelId => OpenId;
    public string ChannelTitle => DisplayName;
    public string? ChannelThumbnailUrl => AvatarUrl;
}
