using System.Text.Json;
using AravalsStream.Core.Models;

namespace AravalsStream.Core.Kick;

public static class KickEventMapper
{
    public static ChatMessage? Map(string eventType, JsonElement payload)
    {
        var (actorField, messageType) = eventType switch
        {
            "chat.message.sent" => ("sender", ChatMessageType.StandardMessage),
            "channel.followed" => ("follower", ChatMessageType.Follow),
            "channel.subscription.new" or "channel.subscription.renewal" => ("subscriber", ChatMessageType.Subscription),
            "channel.subscription.gifts" => ("gifter", ChatMessageType.GiftSubscription),
            _ => ("", ChatMessageType.SystemAnnouncement)
        };
        if (actorField.Length == 0) return null;
        var actor = payload.TryGetProperty(actorField, out var a) && a.ValueKind == JsonValueKind.Object ? a : default;
        var identity = actor.ValueKind == JsonValueKind.Object && actor.TryGetProperty("identity", out var i) && i.ValueKind == JsonValueKind.Object ? i : default;
        var badges = identity.ValueKind == JsonValueKind.Object && identity.TryGetProperty("badges", out var b) && b.ValueKind == JsonValueKind.Array
            ? b.EnumerateArray().Select(x => String(x, "type")).Where(x => x.Length > 0).ToArray() : [];
        var text = eventType switch
        {
            "chat.message.sent" => String(payload, "content"),
            "channel.followed" => "Followed the channel",
            "channel.subscription.gifts" => "Gifted subscriptions",
            _ => "Subscribed to the channel"
        };
        DateTimeOffset timestamp = DateTimeOffset.UtcNow;
        if (DateTimeOffset.TryParse(String(payload, "created_at"), out var parsed)) timestamp = parsed;
        var reply = payload.TryGetProperty("replies_to", out var parent) && parent.ValueKind == JsonValueKind.Object
            ? String(parent, "message_id") : null;
        return new ChatMessage
        {
            Id = String(payload, "message_id") is { Length: > 0 } id ? id : Guid.NewGuid().ToString("N"),
            Platform = "Kick", AuthorId = Number(actor, "user_id")?.ToString() ?? "",
            AuthorName = String(actor, "username"), AuthorAvatar = String(actor, "profile_picture"),
            Text = text, Timestamp = timestamp, MessageType = messageType,
            Badges = badges, IsModerator = badges.Contains("moderator"), IsSubscriber = badges.Contains("subscriber"),
            IsOwner = badges.Contains("broadcaster"), IsVerified = Boolean(actor, "is_verified"),
            AuthorColor = String(identity, "username_color"), ReplyParentMessageId = reply
        };
    }

    private static string String(JsonElement item, string name) => item.ValueKind == JsonValueKind.Object &&
        item.TryGetProperty(name, out var x) && x.ValueKind == JsonValueKind.String ? x.GetString() ?? "" : "";
    private static long? Number(JsonElement item, string name) => item.ValueKind == JsonValueKind.Object &&
        item.TryGetProperty(name, out var x) && x.ValueKind == JsonValueKind.Number ? x.GetInt64() : null;
    private static bool Boolean(JsonElement item, string name) => item.ValueKind == JsonValueKind.Object &&
        item.TryGetProperty(name, out var x) && x.ValueKind == JsonValueKind.True;
}
