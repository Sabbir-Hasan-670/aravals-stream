using System.Text.RegularExpressions;
using AravalsStream.Core.Models;

namespace AravalsStream.Core.Alerts;

public static class ChatOverlayFilter
{
    private static readonly Regex Link = new(@"\b(?:https?://|www\.)\S+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool ShouldShow(ChatMessage message, ChatOverlaySettings settings, DateTimeOffset now)
    {
        if (!settings.Enabled || !settings.Platforms.Contains(message.Platform, StringComparer.OrdinalIgnoreCase)) return false;
        if (settings.BlockedUserIds.Contains(message.AuthorId, StringComparer.OrdinalIgnoreCase)) return false;
        if (settings.HideLinks && Link.IsMatch(message.Text)) return false;
        if (settings.BlockedWords.Any(word => !string.IsNullOrWhiteSpace(word) &&
            message.Text.Contains(word, StringComparison.OrdinalIgnoreCase))) return false;
        return now - message.Timestamp <= TimeSpan.FromSeconds(Math.Clamp(settings.MessageDurationSeconds, 1, 120));
    }
}
