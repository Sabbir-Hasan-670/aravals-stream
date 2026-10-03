namespace AravalsStream.Core.Models;

public enum ChatMessageType
{
    StandardMessage,
    SuperChat,
    SuperSticker,
    Membership,
    Gift,
    Poll,
    SystemAnnouncement
    ,Bits
    ,Subscription
    ,GiftSubscription
    ,Raid
    ,Follow
}

public sealed class ChatMessage
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Platform { get; set; } = "YouTube";
    public string? ChannelId { get; set; }
    public string? RawProviderMessageId { get; set; }
    public string AuthorId { get; set; } = string.Empty;
    public string AuthorName { get; set; } = string.Empty;
    public string? AuthorAvatar { get; set; }
    public string? AuthorAvatarUrl { get => AuthorAvatar; set => AuthorAvatar = value; }
    public string Text { get; set; } = string.Empty;
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
    public bool IsOwner { get; set; }
    public bool IsModerator { get; set; }
    public bool IsMember { get; set; }
    public bool IsSubscriber { get => IsMember; set => IsMember = value; }
    public bool IsVerified { get; set; }
    public bool IsHighlighted { get; set; }
    public ChatMessageType MessageType { get; set; } = ChatMessageType.StandardMessage;

    // Rich Event Details
    public string? SuperChatAmount { get; set; }
    public string? SuperChatComment { get; set; }
    public string? MembershipTier { get; set; }
    public string? BadgeText { get; set; }
    public string? BadgeColor { get; set; }
    public IReadOnlyList<string> Badges { get; set; } = [];
    public string? AuthorColor { get; set; }
    public int? Bits { get; set; }
    public int? EventQuantity { get; set; }
    public string? ReplyParentMessageId { get; set; }
    public string? ReplyToMessageId { get => ReplyParentMessageId; set => ReplyParentMessageId = value; }

    public string DisplayBadge => IsOwner ? "OWNER"
        : IsModerator ? "MOD"
        : IsMember ? "MEMBER"
        : IsVerified ? "VERIFIED"
        : string.Empty;

    public string BadgeBrushColor => IsOwner ? "#F59E0B" // Amber
        : IsModerator ? "#3B82F6" // Blue
        : IsMember ? "#10B981" // Green
        : IsVerified ? "#8B5CF6" // Purple
        : "#6B7280"; // Gray
}
