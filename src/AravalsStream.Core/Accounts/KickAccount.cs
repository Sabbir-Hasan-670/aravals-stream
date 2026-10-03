namespace AravalsStream.Core.Accounts;

public sealed class KickAccount : IPlatformAccount
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Platform => "Kick";
    public string KickUserId { get; set; } = "";
    public string Username { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string? AvatarUrl { get; set; }
    public bool Connected { get; set; }
    public string? TokenReference { get; set; }
    public DateTimeOffset LastAuthenticated { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastValidatedUtc { get; set; }
    public PlatformAccountState State { get; set; } = PlatformAccountState.Disconnected;
    public string ChannelId => KickUserId;
    public string ChannelTitle => DisplayName;
    public string? ChannelThumbnailUrl => AvatarUrl;
}
