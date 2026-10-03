namespace AravalsStream.Core.Accounts;

public interface IPlatformAccount
{
    Guid Id { get; }
    string Platform { get; }
    string ChannelId { get; }
    string ChannelTitle { get; }
    string? ChannelThumbnailUrl { get; }
    bool Connected { get; }
    DateTimeOffset LastAuthenticated { get; }
    string? TokenReference { get; }
}
