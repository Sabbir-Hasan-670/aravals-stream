namespace AravalsStream.Core.Accounts;

public sealed class YouTubeAccount : IPlatformAccount
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Platform => "YouTube";
    public string ChannelId { get; set; } = string.Empty;
    public string ChannelTitle { get; set; } = string.Empty;
    public string? ChannelThumbnailUrl { get; set; }
    public bool Connected { get; set; } = false;
    public DateTimeOffset LastAuthenticated { get; set; } = DateTimeOffset.UtcNow;
    public string? TokenReference { get; set; }

    public ulong? SubscriberCount { get; set; }
    public bool HiddenSubscriberCount { get; set; } = false;
    public DateTimeOffset? LastChecked { get; set; }
    public string? StatusMessage { get; set; }

    public string FormattedSubscribers => HiddenSubscriberCount || SubscriberCount == null
        ? "—"
        : FormatCount(SubscriberCount.Value);

    private static string FormatCount(ulong count)
    {
        if (count >= 1_000_000)
            return $"{count / 1_000_000.0:0.#}M";
        if (count >= 1_000)
            return $"{count / 1_000.0:0.#}K";
        return count.ToString("N0");
    }
}
