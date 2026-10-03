using System.Text.Json;
using AravalsStream.Core.Models;

namespace AravalsStream.Core.Twitch;

public static class TwitchEventMapper
{
    public static ChatMessage? Map(JsonElement notification)
    {
        if (!notification.TryGetProperty("payload", out var payload) ||
            !payload.TryGetProperty("subscription", out var subscription) ||
            !payload.TryGetProperty("event", out var evt)) return null;
        var type = String(subscription, "type");
        var badges = evt.TryGetProperty("badges", out var badgesElement) && badgesElement.ValueKind == JsonValueKind.Array
            ? badgesElement.EnumerateArray().Select(x => String(x, "set_id")).Where(x => x.Length > 0).ToArray() : [];
        var author = String(evt, "chatter_user_name");
        var authorId = String(evt, "chatter_user_id");
        var messageType = ChatMessageType.StandardMessage;
        var text = evt.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object
            ? String(message, "text") : String(evt, "message");
        int? bits = null;
        switch (type)
        {
            case "channel.chat.message":
                if (evt.TryGetProperty("cheer", out var cheer) && cheer.ValueKind == JsonValueKind.Object && cheer.TryGetProperty("bits", out var cheerBits))
                { messageType = ChatMessageType.Bits; bits = cheerBits.GetInt32(); }
                break;
            case "channel.cheer":
                messageType = ChatMessageType.Bits;
                author = String(evt, "user_name"); authorId = String(evt, "user_id");
                if (evt.TryGetProperty("bits", out var directBits)) bits = directBits.GetInt32();
                text = $"Cheered {bits ?? 0} Bits: {text}";
                break;
            case "channel.subscribe":
                messageType = ChatMessageType.Subscription;
                author = String(evt, "user_name"); authorId = String(evt, "user_id");
                text = $"Subscribed ({String(evt, "tier")})";
                break;
            case "channel.subscription.gift":
                messageType = ChatMessageType.GiftSubscription;
                author = String(evt, "user_name"); authorId = String(evt, "user_id");
                text = $"Gifted {Number(evt, "total")} subscriptions";
                break;
            case "channel.raid":
                messageType = ChatMessageType.Raid;
                author = String(evt, "from_broadcaster_user_name"); authorId = String(evt, "from_broadcaster_user_id");
                text = $"Raided with {Number(evt, "viewers")} viewers";
                break;
            default: return null;
        }
        return new ChatMessage
        {
            Id = String(evt, "message_id") is { Length: > 0 } id ? id : Guid.NewGuid().ToString("N"),
            Platform = "Twitch", AuthorId = authorId, AuthorName = author, Text = text,
            Timestamp = DateTimeOffset.UtcNow, MessageType = messageType, Badges = badges,
            IsOwner = badges.Contains("broadcaster"), IsModerator = badges.Contains("moderator"),
            IsSubscriber = badges.Contains("subscriber"), IsVerified = badges.Contains("verified"),
            AuthorColor = String(evt, "color"), Bits = bits,
            EventQuantity = messageType == ChatMessageType.GiftSubscription ? Number(evt, "total")
                : messageType == ChatMessageType.Raid ? Number(evt, "viewers") : bits,
            ReplyParentMessageId = evt.TryGetProperty("reply", out var reply) && reply.ValueKind == JsonValueKind.Object
                ? String(reply, "parent_message_id") : null
        };
    }

    private static string String(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    private static int Number(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : 0;
}
