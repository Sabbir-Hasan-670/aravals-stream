namespace AravalsStream.Core.Accounts;

public sealed class FacebookPageIdentity
{
    public string PageId { get; set; } = "";
    public string PageName { get; set; } = "";
    public string? PagePictureUrl { get; set; }
    public string? PageTokenReference { get; set; }
    public List<string> Tasks { get; set; } = [];
}

public sealed class FacebookAccount : IPlatformAccount
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Platform => "Facebook";
    public string FacebookUserId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string? AvatarUrl { get; set; }
    public bool Connected { get; set; }
    public string? TokenReference { get; set; }
    public DateTimeOffset LastAuthenticated { get; set; } = DateTimeOffset.UtcNow;
    public PlatformAccountState State { get; set; } = PlatformAccountState.Disconnected;
    public string? SelectedPageId { get; set; }
    public List<FacebookPageIdentity> Pages { get; set; } = [];
    public List<string> GrantedPermissions { get; set; } = [];
    public string ChannelId => SelectedPageId ?? FacebookUserId;
    public string ChannelTitle => Pages.FirstOrDefault(p => p.PageId == SelectedPageId)?.PageName ?? DisplayName;
    public string? ChannelThumbnailUrl => Pages.FirstOrDefault(p => p.PageId == SelectedPageId)?.PagePictureUrl ?? AvatarUrl;
}
