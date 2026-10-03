using AravalsStream.Core.Models;

namespace AravalsStream.Core.Chat;

public static class StreamEventMapper
{
    public static StreamEvent? FromChat(ChatMessage message)
    {
        StreamEventType? type = message.MessageType switch
        {
            ChatMessageType.SuperChat or ChatMessageType.SuperSticker => StreamEventType.PaidMessage,
            ChatMessageType.Membership => StreamEventType.Membership,
            ChatMessageType.Subscription => StreamEventType.Subscription,
            ChatMessageType.GiftSubscription or ChatMessageType.Gift => StreamEventType.GiftSubscription,
            ChatMessageType.Bits => StreamEventType.Cheer,
            ChatMessageType.Raid => StreamEventType.Raid,
            ChatMessageType.Follow => StreamEventType.Follow,
            _ => null
        };
        if (type is null) return null;
        var capabilities = PlatformEventCapabilities.For(message.Platform);
        var supported = type.Value switch
        {
            StreamEventType.PaidMessage => capabilities.SupportsPaidMessage,
            StreamEventType.Membership => capabilities.SupportsMembership,
            StreamEventType.Subscription => capabilities.SupportsSubscription,
            StreamEventType.GiftSubscription => capabilities.SupportsGiftSubscription,
            StreamEventType.Cheer => capabilities.SupportsCheer,
            StreamEventType.Raid => capabilities.SupportsRaid,
            StreamEventType.Follow => capabilities.SupportsFollow,
            _ => false
        };
        if (!supported) return null;
        return new StreamEvent
        {
            Id = $"{message.Platform}:{message.RawProviderMessageId ?? message.Id}:event",
            Platform = message.Platform, EventType = type.Value, Timestamp = message.Timestamp,
            ActorId = message.AuthorId, ActorName = message.AuthorName,
            ActorAvatarUrl = message.AuthorAvatarUrl,
            Quantity = message.EventQuantity ?? message.Bits, Tier = message.MembershipTier,
            Message = message.SuperChatComment ?? message.Text,
            Metadata = message.SuperChatAmount is { Length: > 0 } amount
                ? new Dictionary<string, string> { ["displayAmount"] = amount } : []
        };
    }
}
