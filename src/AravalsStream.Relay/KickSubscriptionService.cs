using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AravalsStream.Core.Settings;

namespace AravalsStream.Relay;

public sealed class KickSubscriptionService
{
    public static readonly string[] EventNames = ["chat.message.sent", "channel.followed", "channel.subscription.new", "channel.subscription.renewal", "channel.subscription.gifts"];
    private readonly RelayState _state;
    private readonly IConfiguration _config;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    public KickSubscriptionService(RelayState state, IConfiguration config, HttpClient http,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    { _state = state; _config = config; _http = http; _delay = delay ?? Task.Delay; }

    public async Task<KickRelaySubscriptionStatus> ReconcileAsync(string relayAccess, string channel, string kickAccess,
        bool delete = false, CancellationToken ct = default)
    {
        // One mutation at a time; provider GET is authoritative on every reconnect/restart.
        _ = _state.GetKickSubscription(relayAccess, channel);
        await _gate.WaitAsync(ct);
        try
        {
            var previous = _state.GetKickSubscription(relayAccess, channel);
            var callback = "";
            KickRelaySubscriptionStatus Save(RelaySubscriptionState value, string? error, KickRelaySubscription[]? subscriptions = null,
                string[]? uncertain = null, bool checkedOk = false)
            {
                var result = new KickRelaySubscriptionStatus(channel, value, checkedOk ? DateTimeOffset.UtcNow : previous?.LastCheckedUtc,
                    error, callback, subscriptions ?? previous?.Subscriptions ?? [], uncertain ?? previous?.UncertainEvents ?? []);
                _state.SaveKickSubscription(relayAccess, result); previous = result; return result;
            }
            if (!_config.GetValue<bool>("KICK_SUBSCRIPTIONS_ENABLED") || !RelayPreflight.PublicHttpsUrl(_config["RELAY_PUBLIC_BASE_URL"], out var baseUrl))
                return Save(RelaySubscriptionState.NotConfigured, "Public Relay URL required for provider subscription.");
            callback = new Uri(baseUrl!, "webhooks/kick").AbsoluteUri;
            var appId = _config["KICK_APP_ID"];
            if (string.IsNullOrWhiteSpace(appId) || _config["KICK_CONFIGURED_WEBHOOK_URL"] != callback)
                return Save(RelaySubscriptionState.NotConfigured, "Configure the Kick application webhook URL to the Relay callback.");
            if (!long.TryParse(channel, out var userId) || userId <= 0) throw new UnauthorizedAccessException("Valid Kick channel ownership required.");
            Save(RelaySubscriptionState.Checking, null);
            try
            {
                using (var identity = await ReadAsync(HttpMethod.Post, "https://id.kick.com/oauth/token/introspect", kickAccess, null, ct))
                {
                    var data = identity.RootElement.GetProperty("data");
                    if (!data.GetProperty("active").GetBoolean() || Text(data, "client_id") != appId || Text(data, "token_type") != "user" ||
                        !Text(data, "scope").Split(' ').Contains("events:subscribe"))
                        return Save(RelaySubscriptionState.ReauthorizationRequired, "Reconnect Kick with events:subscribe permission for the configured application.");
                }
                using (var identity = await ReadAsync(HttpMethod.Get, "https://api.kick.com/public/v1/users", kickAccess, null, ct))
                {
                    var users = identity.RootElement.GetProperty("data");
                    if (users.ValueKind != JsonValueKind.Array || users.GetArrayLength() != 1 || users[0].GetProperty("user_id").GetInt64() != userId)
                        throw new UnauthorizedAccessException("Kick token does not belong to the enrolled channel.");
                }
                var existing = await ListAsync(channel, appId, kickAccess, ct);
                _ = _state.GetKickSubscription(relayAccess, channel);
                if (delete)
                {
                    var ownedIds = (previous?.Subscriptions ?? []).Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
                    var remove = existing.Where(s => ownedIds.Contains(s.Id) || (previous?.UncertainEvents ?? []).Contains(s.Event)).ToArray();
                    foreach (var item in remove) ownedIds.Add(item.Id);
                    if (remove.Length > 0)
                    {
                        _ = _state.GetKickSubscription(relayAccess, channel);
                        using var response = await ReadAsync(HttpMethod.Delete, "https://api.kick.com/public/v1/events/subscriptions?" +
                            string.Join('&', remove.Select(x => "id=" + Uri.EscapeDataString(x.Id))), kickAccess, null, ct);
                    }
                    var remaining = await ListAsync(channel, appId, kickAccess, ct);
                    if (remaining.Any(s => ownedIds.Contains(s.Id))) return Save(RelaySubscriptionState.Error, "Kick did not confirm subscription deletion.", remaining, [], true);
                    return Save(RelaySubscriptionState.NotConfigured, null, [], [], true);
                }
                var missing = EventNames.Where(e => !existing.Any(s => s.Event == e && s.Version == 1)).ToArray();
                var uncertain = (previous?.UncertainEvents ?? []).Where(e => missing.Contains(e)).ToArray();
                if (uncertain.Length > 0)
                    return Save(RelaySubscriptionState.Pending, "A prior create outcome is unknown. Discovery will continue; no duplicate creation is sent.", existing, uncertain, true);
                if (missing.Length > 0)
                {
                    // Persist intent before a mutation. An ambiguous HTTP outcome must never trigger a blind POST retry.
                    Save(RelaySubscriptionState.Pending, null, existing, missing, true);
                    _ = _state.GetKickSubscription(relayAccess, channel);
                    using var created = await ReadAsync(HttpMethod.Post, "https://api.kick.com/public/v1/events/subscriptions", kickAccess,
                        new { broadcaster_user_id = userId, events = missing.Select(e => new { name = e, version = 1 }), method = "webhook" }, ct);
                    var results = created.RootElement.GetProperty("data");
                    if (results.ValueKind != JsonValueKind.Array || results.GetArrayLength() != missing.Length) throw new JsonException();
                    var reported = new HashSet<string>(); var confirmed = new List<KickRelaySubscription>(existing); var failed = false;
                    foreach (var item in results.EnumerateArray())
                    {
                        var name = Text(item, "name"); var id = Text(item, "subscription_id");
                        if (!missing.Contains(name) || !reported.Add(name) || item.GetProperty("version").GetInt32() != 1) throw new JsonException();
                        if (Text(item, "error").Length > 0) { failed = true; continue; }
                        if (id.Length is 0 or > 200) throw new JsonException();
                        confirmed.Add(new(id, name, 1, appId));
                    }
                    var awaitingDiscovery = confirmed.Where(s => missing.Contains(s.Event)).Select(s => s.Event).ToArray();
                    Save(RelaySubscriptionState.Pending, failed ? "Kick rejected one or more event subscriptions." : null, confirmed.ToArray(), awaitingDiscovery, true);
                    existing = await ListAsync(channel, appId, kickAccess, ct);
                    missing = EventNames.Where(e => !existing.Any(s => s.Event == e && s.Version == 1)).ToArray();
                    if (missing.Length > 0) return Save(RelaySubscriptionState.Error, "Kick has not confirmed all required event subscriptions.", existing, awaitingDiscovery.Where(missing.Contains).ToArray(), true);
                }
                return Save(RelaySubscriptionState.Active, null, existing, [], true);
            }
            catch (ProviderAuthException) { return Save(RelaySubscriptionState.ReauthorizationRequired, "Kick authorization was rejected. Reconnect the Kick account."); }
            catch (UnauthorizedAccessException)
            {
                // Keep status useful for a mismatched provider identity; revoked Relay sessions cannot save it.
                Save(RelaySubscriptionState.ReauthorizationRequired, "Kick token does not match the enrolled broadcaster.");
                throw;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            { return Save(RelaySubscriptionState.Error, "Kick subscription API timed out. Creation is not blindly retried."); }
            catch (HttpRequestException) { return Save(RelaySubscriptionState.Error, "Kick subscription API is unavailable. Retry after connectivity recovers."); }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
            { return Save(RelaySubscriptionState.Error, "Kick returned an invalid subscription response; no Active status was inferred."); }
        }
        finally { _gate.Release(); }
    }

    private async Task<KickRelaySubscription[]> ListAsync(string channel, string appId, string token, CancellationToken ct)
    {
        using var document = await ReadAsync(HttpMethod.Get, "https://api.kick.com/public/v1/events/subscriptions?broadcaster_user_id=" + Uri.EscapeDataString(channel), token, null, ct);
        var items = document.RootElement.GetProperty("data");
        if (items.ValueKind != JsonValueKind.Array || items.GetArrayLength() > 1000) throw new JsonException();
        var subscriptions = new List<KickRelaySubscription>();
        foreach (var item in items.EnumerateArray())
        {
            var id = Text(item, "id"); var name = Text(item, "event"); var application = Text(item, "app_id");
            if (id.Length is 0 or > 200 || application.Length is 0 or > 200 || name.Length is 0 or > 100) throw new JsonException();
            if (item.GetProperty("broadcaster_user_id").GetInt64().ToString(System.Globalization.CultureInfo.InvariantCulture) == channel &&
                application == appId && Text(item, "method") == "webhook" && EventNames.Contains(name) && item.GetProperty("version").GetInt32() == 1)
                subscriptions.Add(new(id, name, 1, application));
        }
        return subscriptions.ToArray();
    }

    private async Task<JsonDocument> ReadAsync(HttpMethod method, string url, string token, object? body, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 8192) throw new ProviderAuthException();
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(method, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (body is not null) request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(8));
            try
            {
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) throw new ProviderAuthException();
                if ((response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500) && method == HttpMethod.Get && attempt < 2)
                { await _delay(TimeSpan.FromMilliseconds(500 * (1 << attempt) + Random.Shared.Next(100)), ct); continue; }
                response.EnsureSuccessStatusCode();
                if (response.StatusCode == HttpStatusCode.NoContent) return JsonDocument.Parse("{}");
                using var stream = await response.Content.ReadAsStreamAsync(timeout.Token); using var bytes = new MemoryStream();
                var buffer = new byte[4096]; int count;
                while ((count = await stream.ReadAsync(buffer, timeout.Token)) != 0)
                { if (bytes.Length + count > 65536) throw new JsonException(); bytes.Write(buffer, 0, count); }
                return JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions { MaxDepth = 12 });
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && method == HttpMethod.Get && attempt < 2)
            { await _delay(TimeSpan.FromMilliseconds(500 * (1 << attempt)), ct); }
        }
    }
    private static string Text(JsonElement value, string name) => value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() ?? "" : "";
    private sealed class ProviderAuthException : Exception { }
}
