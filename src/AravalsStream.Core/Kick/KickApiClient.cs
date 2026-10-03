using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace AravalsStream.Core.Kick;

public sealed record KickUser(string UserId, string Username, string DisplayName, string? AvatarUrl);
public sealed record KickCategory(long Id, string Name);
public sealed record KickChannel(string UserId, string Slug, string Title, long? CategoryId, string? CategoryName,
    string? StreamUrl, string? StreamKey, bool IsLive, int? Viewers, IReadOnlyList<string> Tags);

public sealed class KickApiClient
{
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _scheduler = new(1, 1);
    public KickApiClient(HttpClient? http = null) => _http = http ?? new HttpClient();

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string token, object? body, CancellationToken ct)
    {
        await _scheduler.WaitAsync(ct);
        try
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                using var request = new HttpRequestMessage(method, "https://api.kick.com/public/v1/" + path);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                if (body != null) request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
                var response = await _http.SendAsync(request, ct);
                if (response.StatusCode != HttpStatusCode.TooManyRequests || attempt == 1) return response;
                var delay = response.Headers.RetryAfter?.Delta;
                response.Dispose();
                await Task.Delay(delay.HasValue && delay.Value > TimeSpan.Zero ? delay.Value : TimeSpan.FromSeconds(2), ct);
            }
            throw new InvalidOperationException("Kick API retry failed.");
        }
        finally { _scheduler.Release(); }
    }

    private async Task<JsonDocument> GetJsonAsync(HttpMethod method, string path, string token, object? body, CancellationToken ct)
    {
        using var response = await SendAsync(method, path, token, body, ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Kick API request failed: HTTP {(int)response.StatusCode}.", null, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
    }

    public async Task<KickUser> GetUserAsync(string token, CancellationToken ct = default)
    {
        using var json = await GetJsonAsync(HttpMethod.Get, "users", token, null, ct);
        var item = json.RootElement.GetProperty("data").EnumerateArray().FirstOrDefault();
        if (item.ValueKind == JsonValueKind.Undefined) throw new InvalidOperationException("Kick identity was not returned.");
        var name = String(item, "name");
        return new KickUser(Number(item, "user_id")?.ToString() ?? "", name, name, String(item, "profile_picture"));
    }

    public async Task<KickChannel> GetChannelAsync(string token, string userId, CancellationToken ct = default)
    {
        using var json = await GetJsonAsync(HttpMethod.Get, "channels?broadcaster_user_id=" + Uri.EscapeDataString(userId), token, null, ct);
        var item = json.RootElement.GetProperty("data").EnumerateArray().FirstOrDefault();
        if (item.ValueKind == JsonValueKind.Undefined) throw new InvalidOperationException("Kick channel was not returned.");
        var category = item.TryGetProperty("category", out var cat) && cat.ValueKind == JsonValueKind.Object ? cat : default;
        var stream = item.TryGetProperty("stream", out var st) && st.ValueKind == JsonValueKind.Object ? st : default;
        var tags = stream.ValueKind == JsonValueKind.Object && stream.TryGetProperty("custom_tags", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToArray() : [];
        return new KickChannel(Number(item, "broadcaster_user_id")?.ToString() ?? userId, String(item, "slug"),
            String(item, "stream_title"), Number(category, "id"), String(category, "name"),
            String(stream, "url"), String(stream, "key"), Boolean(stream, "is_live"),
            stream.ValueKind == JsonValueKind.Object && stream.TryGetProperty("viewer_count", out var viewers) && viewers.ValueKind == JsonValueKind.Number
                ? viewers.GetInt32() : null, tags);
    }

    public async Task<IReadOnlyList<KickCategory>> SearchCategoriesAsync(string token, string query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        using var json = await GetJsonAsync(HttpMethod.Get, "categories?q=" + Uri.EscapeDataString(query), token, null, ct);
        return json.RootElement.GetProperty("data").EnumerateArray().Select(x =>
            new KickCategory(Number(x, "id") ?? 0, String(x, "name"))).Where(x => x.Id > 0).ToArray();
    }

    public async Task UpdateChannelAsync(string token, string? title, long? categoryId, IReadOnlyList<string>? tags, CancellationToken ct = default)
    {
        var body = new Dictionary<string, object>();
        if (!string.IsNullOrWhiteSpace(title)) body["stream_title"] = title;
        if (categoryId.HasValue) body["category_id"] = categoryId.Value;
        if (tags != null) body["custom_tags"] = tags;
        if (body.Count == 0) return;
        using var response = await SendAsync(HttpMethod.Patch, "channels", token, body, ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Kick metadata update failed: HTTP {(int)response.StatusCode}.", null, response.StatusCode);
    }

    public async Task<bool> SendChatMessageAsync(string token, long broadcasterUserId, string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 500) return false;
        using var json = await GetJsonAsync(HttpMethod.Post, "chat", token,
            new { broadcaster_user_id = broadcasterUserId, content = text, type = "user" }, ct);
        return json.RootElement.TryGetProperty("data", out var data) && Boolean(data, "is_sent");
    }

    private static string String(JsonElement item, string name) => item.ValueKind == JsonValueKind.Object &&
        item.TryGetProperty(name, out var x) && x.ValueKind == JsonValueKind.String ? x.GetString() ?? "" : "";
    private static long? Number(JsonElement item, string name) => item.ValueKind == JsonValueKind.Object &&
        item.TryGetProperty(name, out var x) && x.ValueKind == JsonValueKind.Number ? x.GetInt64() : null;
    private static bool Boolean(JsonElement item, string name) => item.ValueKind == JsonValueKind.Object &&
        item.TryGetProperty(name, out var x) && x.ValueKind == JsonValueKind.True;
}
