using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AravalsStream.Core.Services;
using AravalsStream.Core.YouTube.Models;

namespace AravalsStream.Core.YouTube;

public sealed class YouTubeApiClient
{
    private const string BaseUrl = "https://www.googleapis.com/youtube/v3";
    private readonly HttpClient _http;

    public YouTubeApiClient(HttpClient? httpClient = null)
    {
        _http = httpClient ?? new HttpClient();
    }

    private void SetAuthHeader(HttpRequestMessage req, string accessToken)
    {
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
    }

    public async Task<(string ChannelId, string ChannelTitle, string? ThumbnailUrl, ulong? SubscriberCount, bool HiddenSubscriberCount)>
        GetChannelIdentityAsync(string accessToken, CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/channels?part=snippet,statistics&mine=true";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        SetAuthHeader(req, accessToken);

        using var res = await _http.SendAsync(req, ct);
        var json = await res.Content.ReadAsStringAsync(ct);

        if (!res.IsSuccessStatusCode)
        {
            AppLog.Write("YouTubeApi", $"GetChannelIdentity failed with HTTP {res.StatusCode}: {AppLog.Sanitize(json)}");
            throw new InvalidOperationException($"YouTube API channel request failed: {res.StatusCode}");
        }

        var response = JsonSerializer.Deserialize<YouTubeChannelListResponse>(json);
        var item = response?.Items?.FirstOrDefault()
            ?? throw new InvalidOperationException("No YouTube channel found for the authenticated account.");

        ulong? subs = null;
        if (!item.Statistics?.HiddenSubscriberCount == true && ulong.TryParse(item.Statistics?.SubscriberCount, out var s))
        {
            subs = s;
        }

        return (
            item.Id,
            item.Snippet?.Title ?? "My Channel",
            item.Snippet?.Thumbnails?.Default?.Url ?? item.Snippet?.Thumbnails?.Medium?.Url,
            subs,
            item.Statistics?.HiddenSubscriberCount ?? false
        );
    }

    public async Task<IReadOnlyList<YouTubeBroadcastItem>> ListBroadcastsAsync(
        string accessToken,
        string broadcastStatus = "all",
        CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/liveBroadcasts?part=snippet,status,contentDetails&broadcastStatus={broadcastStatus}&broadcastType=all&maxResults=25";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        SetAuthHeader(req, accessToken);

        using var res = await _http.SendAsync(req, ct);
        var json = await res.Content.ReadAsStringAsync(ct);

        if (!res.IsSuccessStatusCode)
        {
            AppLog.Write("YouTubeApi", $"ListBroadcasts failed with HTTP {res.StatusCode}: {AppLog.Sanitize(json)}");
            throw new InvalidOperationException($"YouTube API broadcast list request failed: {res.StatusCode}");
        }

        var response = JsonSerializer.Deserialize<YouTubeBroadcastListResponse>(json);
        return response?.Items ?? (IReadOnlyList<YouTubeBroadcastItem>)Array.Empty<YouTubeBroadcastItem>();
    }

    public async Task<YouTubeBroadcastItem> CreateBroadcastAsync(
        string accessToken,
        string title,
        string description,
        string privacyStatus,
        DateTimeOffset scheduledStartTime,
        bool madeForKids,
        bool enableAutoStart = true,
        bool enableAutoStop = false,
        bool enableDvr = true,
        CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/liveBroadcasts?part=snippet,status,contentDetails";
        var payload = new
        {
            snippet = new
            {
                title = title,
                description = description,
                scheduledStartTime = scheduledStartTime.ToString("o")
            },
            status = new
            {
                privacyStatus = privacyStatus.ToLowerInvariant(),
                selfDeclaredMadeForKids = madeForKids
            },
            contentDetails = new
            {
                enableAutoStart = enableAutoStart,
                enableAutoStop = enableAutoStop,
                enableDvr = enableDvr
            }
        };

        var jsonBody = JsonSerializer.Serialize(payload);
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(jsonBody, Encoding.UTF8, "application/json")
        };
        SetAuthHeader(req, accessToken);

        using var res = await _http.SendAsync(req, ct);
        var resJson = await res.Content.ReadAsStringAsync(ct);

        if (!res.IsSuccessStatusCode)
        {
            AppLog.Write("YouTubeApi", $"CreateBroadcast failed with HTTP {res.StatusCode}: {AppLog.Sanitize(resJson)}");
            throw new InvalidOperationException($"YouTube API broadcast creation failed: {res.StatusCode}");
        }

        var item = JsonSerializer.Deserialize<YouTubeBroadcastItem>(resJson)
            ?? throw new InvalidOperationException("YouTube API returned empty broadcast item.");

        return item;
    }

    public async Task<IReadOnlyList<YouTubeLiveStreamItem>> ListStreamsAsync(
        string accessToken,
        CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/liveStreams?part=snippet,cdn,status&mine=true";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        SetAuthHeader(req, accessToken);

        using var res = await _http.SendAsync(req, ct);
        var json = await res.Content.ReadAsStringAsync(ct);

        if (!res.IsSuccessStatusCode)
        {
            AppLog.Write("YouTubeApi", $"ListStreams failed with HTTP {res.StatusCode}: {AppLog.Sanitize(json)}");
            throw new InvalidOperationException($"YouTube API live streams request failed: {res.StatusCode}");
        }

        var response = JsonSerializer.Deserialize<YouTubeLiveStreamListResponse>(json);
        return response?.Items ?? (IReadOnlyList<YouTubeLiveStreamItem>)Array.Empty<YouTubeLiveStreamItem>();
    }

    public async Task<YouTubeLiveStreamItem> CreateLiveStreamAsync(
        string accessToken,
        string title,
        string resolution = "1080p",
        string frameRate = "60fps",
        CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/liveStreams?part=snippet,cdn";
        var payload = new
        {
            snippet = new { title = title },
            cdn = new
            {
                ingestionType = "rtmp",
                resolution = resolution,
                frameRate = frameRate
            }
        };

        var jsonBody = JsonSerializer.Serialize(payload);
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(jsonBody, Encoding.UTF8, "application/json")
        };
        SetAuthHeader(req, accessToken);

        using var res = await _http.SendAsync(req, ct);
        var resJson = await res.Content.ReadAsStringAsync(ct);

        if (!res.IsSuccessStatusCode)
        {
            AppLog.Write("YouTubeApi", $"CreateLiveStream failed with HTTP {res.StatusCode}: {AppLog.Sanitize(resJson)}");
            throw new InvalidOperationException($"YouTube API stream creation failed: {res.StatusCode}");
        }

        var item = JsonSerializer.Deserialize<YouTubeLiveStreamItem>(resJson)
            ?? throw new InvalidOperationException("YouTube API returned empty live stream item.");

        return item;
    }

    public async Task<YouTubeBroadcastItem> BindBroadcastAsync(
        string accessToken,
        string broadcastId,
        string streamId,
        CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/liveBroadcasts/bind?id={Uri.EscapeDataString(broadcastId)}&part=id,contentDetails&streamId={Uri.EscapeDataString(streamId)}";
        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        SetAuthHeader(req, accessToken);

        using var res = await _http.SendAsync(req, ct);
        var json = await res.Content.ReadAsStringAsync(ct);

        if (!res.IsSuccessStatusCode)
        {
            AppLog.Write("YouTubeApi", $"BindBroadcast failed with HTTP {res.StatusCode}: {AppLog.Sanitize(json)}");
            throw new InvalidOperationException($"YouTube API broadcast bind failed: {res.StatusCode}");
        }

        var item = JsonSerializer.Deserialize<YouTubeBroadcastItem>(json)
            ?? throw new InvalidOperationException("YouTube API returned empty bound broadcast item.");

        return item;
    }

    public async Task<(ulong? ConcurrentViewers, string? ActiveLiveChatId)> GetVideoLiveDetailsAsync(
        string accessToken,
        string videoId,
        CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/videos?part=liveStreamingDetails&id={Uri.EscapeDataString(videoId)}";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        SetAuthHeader(req, accessToken);

        using var res = await _http.SendAsync(req, ct);
        var json = await res.Content.ReadAsStringAsync(ct);

        if (!res.IsSuccessStatusCode)
        {
            AppLog.Write("YouTubeApi", $"GetVideoLiveDetails failed with HTTP {res.StatusCode}: {AppLog.Sanitize(json)}");
            return (null, null);
        }

        var response = JsonSerializer.Deserialize<YouTubeVideoListResponse>(json);
        var item = response?.Items?.FirstOrDefault();

        ulong? viewers = null;
        if (ulong.TryParse(item?.LiveStreamingDetails?.ConcurrentViewers, out var v))
            viewers = v;

        return (viewers, item?.LiveStreamingDetails?.ActiveLiveChatId);
    }

    public async Task<YouTubeLiveChatMessageListResponse?> GetLiveChatMessagesAsync(
        string accessToken,
        string liveChatId,
        string? pageToken = null,
        CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/liveChat/messages?liveChatId={Uri.EscapeDataString(liveChatId)}&part=id,snippet,authorDetails";
        if (!string.IsNullOrEmpty(pageToken))
            url += $"&pageToken={Uri.EscapeDataString(pageToken)}";

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        SetAuthHeader(req, accessToken);

        using var res = await _http.SendAsync(req, ct);
        var json = await res.Content.ReadAsStringAsync(ct);

        if (!res.IsSuccessStatusCode)
        {
            AppLog.Write("YouTubeApi", $"GetLiveChatMessages returned HTTP {res.StatusCode}: {AppLog.Sanitize(json)}");
            return null;
        }

        return JsonSerializer.Deserialize<YouTubeLiveChatMessageListResponse>(json);
    }

    public async Task<bool> SendLiveChatMessageAsync(
        string accessToken,
        string liveChatId,
        string messageText,
        CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/liveChat/messages?part=snippet";
        var payload = new
        {
            snippet = new
            {
                liveChatId = liveChatId,
                type = "textMessageEvent",
                textMessageDetails = new
                {
                    messageText = messageText
                }
            }
        };

        var jsonBody = JsonSerializer.Serialize(payload);
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(jsonBody, Encoding.UTF8, "application/json")
        };
        SetAuthHeader(req, accessToken);

        using var res = await _http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode)
        {
            var err = await res.Content.ReadAsStringAsync(ct);
            AppLog.Write("YouTubeApi", $"SendLiveChatMessage failed with HTTP {res.StatusCode}: {AppLog.Sanitize(err)}");
            return false;
        }

        return true;
    }

    public async Task<bool> TransitionBroadcastAsync(
        string accessToken,
        string broadcastId,
        string transitionStatus,
        CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/liveBroadcasts/transition?broadcastStatus={Uri.EscapeDataString(transitionStatus)}&id={Uri.EscapeDataString(broadcastId)}&part=status";
        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        SetAuthHeader(req, accessToken);

        using var res = await _http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode)
        {
            var err = await res.Content.ReadAsStringAsync(ct);
            AppLog.Write("YouTubeApi", $"TransitionBroadcast failed with HTTP {res.StatusCode}: {AppLog.Sanitize(err)}");
            return false;
        }

        return true;
    }
}
