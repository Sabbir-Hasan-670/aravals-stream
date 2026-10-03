namespace AravalsStream.Core.Models;

public enum StreamEventType
{
    PaidMessage, Membership, MembershipMilestone, Subscription, GiftSubscription,
    Cheer, Raid, Follow, Donation, Like, Share, Custom
}

public sealed class StreamEvent
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Platform { get; set; } = "";
    public StreamEventType EventType { get; set; }
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
    public string ActorId { get; set; } = "";
    public string ActorName { get; set; } = "";
    public string? ActorAvatarUrl { get; set; }
    public string? Message { get; set; }
    public decimal? Amount { get; set; }
    public string? Currency { get; set; }
    public int? Quantity { get; set; }
    public string? Tier { get; set; }
    public Dictionary<string, string> Metadata { get; set; } = [];
    public bool IsSimulated { get; set; }
}
