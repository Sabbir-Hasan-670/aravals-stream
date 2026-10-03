using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace AravalsStream.Core.Twitch;

public sealed record TwitchUser(string Id, string Login, string DisplayName, string? ProfileImageUrl, string? Description);
public sealed record TwitchCategory(string Id, string Name);
public sealed record TwitchChannelInfo(string Title, string? GameId, string? GameName, string? Language, IReadOnlyList<string> Tags);
public sealed record TwitchIngest(string Name, string ServerUrl, bool IsDefault);
public sealed record TwitchChannelStats(int? Viewers, long? Followers);

public sealed class TwitchApiClient
{
    private readonly string _clientId;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _scheduler = new(1, 1);
    public TwitchApiClient(string clientId, HttpClient? http = null)
    { _clientId = clientId; _http = http ?? new HttpClient(); }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? token, object? body, CancellationToken ct)
    {
        await _scheduler.WaitAsync(ct);
        try
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                using var request = new HttpRequestMessage(method, path.StartsWith("https://", StringComparison.Ordinal) ? path : "https://api.twitch.tv/helix/" + path);
                request.Headers.Add("Client-Id", _clientId);
                if (!string.IsNullOrEmpty(token)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                if (body != null) request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
                var response = await _http.SendAsync(request, ct);
                if (response.StatusCode != HttpStatusCode.TooManyRequests || attempt == 1) return response;
                var delay = response.Headers.RetryAfter?.Delta;
                if (delay == null && response.Headers.TryGetValues("Ratelimit-Reset", out var values) &&
                    long.TryParse(values.FirstOrDefault(), out var epoch))
                    delay = DateTimeOffset.FromUnixTimeSeconds(epoch) - DateTimeOffset.UtcNow;
                response.Dispose();
                await Task.Delay(delay.HasValue && delay.Value > TimeSpan.Zero ? delay.Value : TimeSpan.FromSeconds(2), ct);
            }
            throw new InvalidOperationException("Twitch API retry failed.");
        }
        finally { _scheduler.Release(); }
    }

    private async Task<JsonDocument> GetJsonAsync(HttpMethod method, string path, string? token, object? body, CancellationToken ct)
    {
        using var response = await SendAsync(method, path, token, body, ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Twitch API request failed: HTTP {(int)response.StatusCode}.", null, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
    }

    public async Task<TwitchUser> GetUserAsync(string token, CancellationToken ct = default)
    {
        using var json = await GetJsonAsync(HttpMethod.Get, "users", token, null, ct);
        var item = json.RootElement.GetProperty("data").EnumerateArray().FirstOrDefault();
        if (item.ValueKind == JsonValueKind.Undefined) throw new InvalidOperationException("Twitch account identity was not returned.");
        return new TwitchUser(item.GetProperty("id").GetString() ?? "", item.GetProperty("login").GetString() ?? "",
            item.GetProperty("display_name").GetString() ?? "", Value(item, "profile_image_url"), Value(item, "description"));
    }

    public async Task<TwitchChannelInfo> GetChannelAsync(string token, string userId, CancellationToken ct = default)
    {
        using var json = await GetJsonAsync(HttpMethod.Get, $"channels?broadcaster_id={Uri.EscapeDataString(userId)}", token, null, ct);
        var item = json.RootElement.GetProperty("data").EnumerateArray().FirstOrDefault();
        if (item.ValueKind == JsonValueKind.Undefined) throw new InvalidOperationException("Twitch channel was not returned.");
        var tags = item.TryGetProperty("tags", out var tagArray) && tagArray.ValueKind == JsonValueKind.Array
            ? tagArray.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToArray() : [];
        return new TwitchChannelInfo(Value(item, "title") ?? "", Value(item, "game_id"), Value(item, "game_name"), Value(item, "broadcaster_language"), tags);
    }

    public async Task UpdateChannelAsync(string token, string userId, string? title, string? gameId, string? language, IReadOnlyList<string>? tags, CancellationToken ct = default)
    {
        var body = new Dictionary<string, object>();
        if (title != null) body["title"] = title;
        if (gameId != null) body["game_id"] = gameId;
        if (language != null) body["broadcaster_language"] = language;
        if (tags != null) body["tags"] = tags;
        if (body.Count == 0) return;
        using var response = await SendAsync(HttpMethod.Patch, $"channels?broadcaster_id={Uri.EscapeDataString(userId)}", token, body, ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Twitch metadata update failed: HTTP {(int)response.StatusCode}.", null, response.StatusCode);
    }

    public async Task<IReadOnlyList<TwitchCategory>> SearchCategoriesAsync(string token, string query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        using var json = await GetJsonAsync(HttpMethod.Get, $"search/categories?query={Uri.EscapeDataString(query)}&first=20", token, null, ct);
        return json.RootElement.GetProperty("data").EnumerateArray()
            .Select(x => new TwitchCategory(Value(x, "id") ?? "", Value(x, "name") ?? "")).ToArray();
    }

    public async Task<string> GetStreamKeyAsync(string token, string userId, CancellationToken ct = default)
    {
        using var json = await GetJsonAsync(HttpMethod.Get, $"streams/key?broadcaster_id={Uri.EscapeDataString(userId)}", token, null, ct);
        var item = json.RootElement.GetProperty("data").EnumerateArray().FirstOrDefault();
        var key = item.ValueKind == JsonValueKind.Undefined ? null : Value(item, "stream_key");
        return !string.IsNullOrWhiteSpace(key) ? key : throw new InvalidOperationException("Twitch did not return a stream key.");
    }

    public async Task<IReadOnlyList<TwitchIngest>> GetIngestServersAsync(CancellationToken ct = default)
    {
        using var json = await GetJsonAsync(HttpMethod.Get, "https://ingest.twitch.tv/ingests", null, null, ct);
        return json.RootElement.GetProperty("ingests").EnumerateArray().Select(x =>
        {
            var template = Value(x, "url_template") ?? "";
            var server = template.Replace("{stream_key}", "", StringComparison.OrdinalIgnoreCase).TrimEnd('/');
            return new TwitchIngest(Value(x, "name") ?? server, server,
                x.TryGetProperty("default", out var flag) && flag.ValueKind == JsonValueKind.True);
        }).Where(x => Uri.TryCreate(x.ServerUrl, UriKind.Absolute, out var uri) &&
            (uri.Scheme == "rtmp" || uri.Scheme == "rtmps")).ToArray();
    }

    public async Task<TwitchChannelStats> GetStatsAsync(string token, string userId, CancellationToken ct = default)
    {
        int? viewers = null;
        long? followers = null;
        using (var json = await GetJsonAsync(HttpMethod.Get, $"streams?user_id={Uri.EscapeDataString(userId)}", token, null, ct))
        {
            var item = json.RootElement.GetProperty("data").EnumerateArray().FirstOrDefault();
            if (item.ValueKind != JsonValueKind.Undefined && item.TryGetProperty("viewer_count", out var count)) viewers = count.GetInt32();
        }
        try
        {
            using var json = await GetJsonAsync(HttpMethod.Get, $"channels/followers?broadcaster_id={Uri.EscapeDataString(userId)}", token, null, ct);
            if (json.RootElement.TryGetProperty("total", out var total)) followers = total.GetInt64();
        }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized) { }
        return new TwitchChannelStats(viewers, followers);
    }

    public async Task<bool> SendChatMessageAsync(string token, string broadcasterId, string senderId, string text, CancellationToken ct = default)
    {
        using var json = await GetJsonAsync(HttpMethod.Post, "chat/messages", token,
            new { broadcaster_id = broadcasterId, sender_id = senderId, message = text }, ct);
        var item = json.RootElement.GetProperty("data").EnumerateArray().FirstOrDefault();
        return item.ValueKind != JsonValueKind.Undefined && item.TryGetProperty("is_sent", out var sent) && sent.ValueKind == JsonValueKind.True;
    }

    public async Task CreateEventSubscriptionAsync(string token, string type, string version, object condition, string sessionId, CancellationToken ct = default)
    {
        using var response = await SendAsync(HttpMethod.Post, "eventsub/subscriptions", token,
            new { type, version, condition, transport = new { method = "websocket", session_id = sessionId } }, ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Twitch EventSub subscription failed: HTTP {(int)response.StatusCode}.", null, response.StatusCode);
    }

    private static string? Value(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
