using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using AravalsStream.Core.Models;

namespace AravalsStream.Core.Facebook;

public sealed record FacebookUser(string Id, string Name, string? PictureUrl);
public sealed record FacebookPage(string Id, string Name, string? PictureUrl, string AccessToken, IReadOnlyList<string> Tasks);
public sealed record FacebookLive(string Id, string SecureStreamUrl, string Status);
public sealed class FacebookGraphException : HttpRequestException
{
    public int? GraphCode { get; }
    public FacebookGraphException(HttpStatusCode status, int? graphCode)
        : base($"Facebook Graph API failed: HTTP {(int)status}" +
            (graphCode.HasValue ? $", code {graphCode.Value}" : "") + ".", null, status)
        => GraphCode = graphCode;
}

// Tokens are sent in headers, never in URLs or exception messages.
public sealed class FacebookGraphClient
{
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(1, 1);
    public FacebookGraphClient(HttpClient? http = null) => _http = http ?? new HttpClient();

    private async Task<JsonDocument> RequestAsync(HttpMethod method, string path, string token,
        Dictionary<string, string>? form = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token)) throw new InvalidOperationException("Facebook authorization is unavailable.");
        await _gate.WaitAsync(ct);
        try
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                using var request = new HttpRequestMessage(method,
                    $"https://graph.facebook.com/{FacebookCapabilitySet.GraphVersion}/{path}");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                if (form != null) request.Content = new FormUrlEncodedContent(form);
                using var response = await _http.SendAsync(request, ct);
                var errorCode = response.IsSuccessStatusCode ? null : await SafeErrorCodeAsync(response, ct);
                if ((response.StatusCode == HttpStatusCode.TooManyRequests || errorCode is 613 or 80001) && attempt == 0)
                {
                    var delay = response.Headers.RetryAfter?.Delta;
                    await Task.Delay(delay is { } d && d > TimeSpan.Zero ? d : TimeSpan.FromSeconds(3), ct);
                    continue;
                }
                if (!response.IsSuccessStatusCode)
                {
                    // Graph API response bodies may contain private metadata. Only expose status and error code.
                    throw new FacebookGraphException(response.StatusCode, errorCode);
                }
                return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            }
            throw new InvalidOperationException("Facebook Graph API rate limit retry failed.");
        }
        finally { _gate.Release(); }
    }

    private static async Task<int?> SafeErrorCodeAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            return json.RootElement.TryGetProperty("error", out var error) &&
                error.TryGetProperty("code", out var code) && code.TryGetInt32(out var n) ? n : null;
        }
        catch (JsonException) { return null; }
    }

    public async Task<FacebookUser> GetUserAsync(string userToken, CancellationToken ct = default)
    {
        using var json = await RequestAsync(HttpMethod.Get, "me?fields=id,name,picture", userToken, ct: ct);
        var root = json.RootElement;
        var picture = root.TryGetProperty("picture", out var p) && p.TryGetProperty("data", out var d)
            ? String(d, "url") : null;
        return new FacebookUser(String(root, "id"), String(root, "name"), picture);
    }

    public async Task<IReadOnlyList<FacebookPage>> GetPagesAsync(string userToken, CancellationToken ct = default)
    {
        using var json = await RequestAsync(HttpMethod.Get,
            "me/accounts?fields=id,name,access_token,tasks,picture&limit=100", userToken, ct: ct);
        if (!json.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return [];
        return data.EnumerateArray().Select(item =>
        {
            var picture = item.TryGetProperty("picture", out var p) && p.TryGetProperty("data", out var d)
                ? String(d, "url") : null;
            var tasks = item.TryGetProperty("tasks", out var t) && t.ValueKind == JsonValueKind.Array
                ? t.EnumerateArray().Select(x => x.GetString() ?? "").ToArray() : [];
            return new FacebookPage(String(item, "id"), String(item, "name"), picture,
                String(item, "access_token"), tasks);
        }).Where(p => p.Id.Length > 0 && p.AccessToken.Length > 0).ToArray();
    }

    public async Task<IReadOnlySet<string>> GetGrantedPermissionsAsync(string userToken, CancellationToken ct = default)
    {
        using var json = await RequestAsync(HttpMethod.Get, "me/permissions", userToken, ct: ct);
        if (!json.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return new HashSet<string>(StringComparer.Ordinal);
        return data.EnumerateArray()
            .Where(item => String(item, "status") == "granted")
            .Select(item => String(item, "permission"))
            .Where(value => value.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
    }

    public async Task<FacebookLive> CreatePageLiveAsync(string pageId, string pageToken, string title,
        string? description, CancellationToken ct = default)
    {
        var form = new Dictionary<string, string> { ["status"] = "LIVE_NOW", ["title"] = title };
        if (!string.IsNullOrWhiteSpace(description)) form["description"] = description;
        using var json = await RequestAsync(HttpMethod.Post, Uri.EscapeDataString(pageId) + "/live_videos",
            pageToken, form, ct);
        var root = json.RootElement;
        var ingest = String(root, "secure_stream_url");
        if (ingest.Length == 0) throw new InvalidOperationException("Meta did not return a secure ingest URL.");
        return new FacebookLive(String(root, "id"), ingest, "Created");
    }

    public async Task<IReadOnlyList<ChatMessage>> GetLiveCommentsAsync(string liveId, string pageToken,
        CancellationToken ct = default)
    {
        var path = Uri.EscapeDataString(liveId) +
            "/comments?fields=id,message,from,created_time&order=reverse_chronological&limit=50";
        using var json = await RequestAsync(HttpMethod.Get, path, pageToken, ct: ct);
        return json.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array
            ? data.EnumerateArray().Select(MapComment).Where(x => x.Id.Length > 0).ToArray() : [];
    }

    public async Task<bool> SendLiveCommentAsync(string liveId, string pageToken, string message,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(message)) return false;
        using var json = await RequestAsync(HttpMethod.Post, Uri.EscapeDataString(liveId) + "/comments",
            pageToken, new Dictionary<string, string> { ["message"] = message }, ct);
        return String(json.RootElement, "id").Length > 0;
    }

    public static ChatMessage MapComment(JsonElement item)
    {
        var author = item.TryGetProperty("from", out var from) ? from : default;
        var when = DateTimeOffset.TryParse(String(item, "created_time"), out var parsed) ? parsed : DateTimeOffset.UtcNow;
        return new ChatMessage
        {
            Id = String(item, "id"), Platform = "Facebook", AuthorId = String(author, "id"),
            AuthorName = String(author, "name"), Text = String(item, "message"), Timestamp = when
        };
    }

    private static string String(JsonElement item, string name) => item.ValueKind == JsonValueKind.Object &&
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
}
