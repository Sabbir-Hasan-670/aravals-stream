namespace AravalsStream.Core.TikTok;

public interface ITikTokContentPublisher
{
    Task<string> InitializeVideoUploadAsync(string filePath, string title, CancellationToken ct = default);
    Task<bool> CheckPublishStatusAsync(string publishId, CancellationToken ct = default);
}
