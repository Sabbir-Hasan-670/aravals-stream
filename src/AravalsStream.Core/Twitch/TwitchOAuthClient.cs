using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using AravalsStream.Core.Accounts;
using AravalsStream.Core.Interfaces;
using AravalsStream.Core.Settings;

namespace AravalsStream.Core.Twitch;

public sealed record TwitchDeviceAuthorization(string DeviceCode, string UserCode, string VerificationUri, int ExpiresIn, int Interval);

public sealed class TwitchOAuthClient
{
    private static readonly SemaphoreSlim TokenGate = new(1, 1);
    public static readonly string[] RequiredScopes =
    [
        "channel:manage:broadcast", "channel:read:stream_key", "user:read:chat", "user:write:chat",
        "moderator:read:followers", "channel:read:subscriptions", "bits:read"
    ];

    private readonly TwitchOAuthSettings _settings;
    private readonly ISecretStorage _secrets;
    private readonly HttpClient _http;

    public TwitchOAuthClient(TwitchOAuthSettings settings, ISecretStorage secrets, HttpClient? http = null)
    { _settings = settings; _secrets = secrets; _http = http ?? new HttpClient(); }

    public async Task<TwitchDeviceAuthorization> BeginDeviceAuthorizationAsync(CancellationToken ct = default)
    {
        if (!_settings.IsConfigured) throw new InvalidOperationException("Twitch Client ID is not configured.");
        using var response = await _http.PostAsync("https://id.twitch.tv/oauth2/device",
            new FormUrlEncodedContent(new Dictionary<string, string>
            { ["client_id"] = _settings.ClientId, ["scopes"] = string.Join(' ', RequiredScopes) }), ct);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Twitch authorization setup failed: HTTP {(int)response.StatusCode}.");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var root = json.RootElement;
        return new TwitchDeviceAuthorization(
            root.GetProperty("device_code").GetString() ?? throw new InvalidOperationException("Missing device code."),
            root.GetProperty("user_code").GetString() ?? "",
            root.GetProperty("verification_uri").GetString() ?? throw new InvalidOperationException("Missing verification URL."),
            root.GetProperty("expires_in").GetInt32(), root.GetProperty("interval").GetInt32());
    }

    public async Task<OAuthTokenData> WaitForAuthorizationAsync(TwitchDeviceAuthorization device, CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(device.ExpiresIn));
        var interval = Math.Max(1, device.Interval);
        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(interval), timeout.Token);
            using var response = await _http.PostAsync("https://id.twitch.tv/oauth2/token",
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = _settings.ClientId,
                    ["scopes"] = string.Join(' ', RequiredScopes),
                    ["device_code"] = device.DeviceCode,
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code"
                }), timeout.Token);
            var body = await response.Content.ReadAsStringAsync(timeout.Token);
            if (response.IsSuccessStatusCode) return ParseToken(body);
            var code = ParseOAuthError(body);
            if (code == "authorization_pending") continue;
            if (code == "slow_down") { interval += 5; continue; }
            throw new InvalidOperationException($"Twitch authorization failed: {code ?? $"HTTP {(int)response.StatusCode}"}.");
        }
    }

    public async Task<TwitchAccount> AuthorizeAsync(Func<TwitchDeviceAuthorization, Task>? launchBrowser = null, CancellationToken ct = default)
    {
        var device = await BeginDeviceAuthorizationAsync(ct);
        if (launchBrowser != null) await launchBrowser(device);
        else Process.Start(new ProcessStartInfo(device.VerificationUri) { UseShellExecute = true });
        var tokens = await WaitForAuthorizationAsync(device, ct);
        var api = new TwitchApiClient(_settings.ClientId, _http);
        var identity = await api.GetUserAsync(tokens.AccessToken, ct);
        var reference = Guid.NewGuid().ToString();
        await _secrets.StoreAsync(reference, JsonSerializer.Serialize(tokens), ct);
        return new TwitchAccount
        {
            UserId = identity.Id, Login = identity.Login, DisplayName = identity.DisplayName,
            ProfileImageUrl = identity.ProfileImageUrl, Description = identity.Description,
            Connected = true, State = PlatformAccountState.Connected,
            TokenReference = reference, LastAuthenticated = DateTimeOffset.UtcNow,
            LastValidatedUtc = DateTimeOffset.UtcNow
        };
    }

    public async Task<string> GetValidAccessTokenAsync(TwitchAccount account, CancellationToken ct = default)
    {
        await TokenGate.WaitAsync(ct);
        try { return await GetValidAccessTokenCoreAsync(account, ct); }
        finally { TokenGate.Release(); }
    }

    private async Task<string> GetValidAccessTokenCoreAsync(TwitchAccount account, CancellationToken ct)
    {
        if (!account.Connected || string.IsNullOrEmpty(account.TokenReference))
            throw new InvalidOperationException("Twitch account is not connected.");
        var raw = await _secrets.GetAsync(account.TokenReference, ct);
        var token = string.IsNullOrEmpty(raw) ? null : JsonSerializer.Deserialize<OAuthTokenData>(raw);
        if (token == null) { account.State = PlatformAccountState.NeedsReauthentication; throw new InvalidOperationException("Twitch credentials are unavailable."); }
        if (token.IsExpired() || account.LastValidatedUtc == null || DateTimeOffset.UtcNow - account.LastValidatedUtc >= TimeSpan.FromHours(1))
        {
            if (!token.IsExpired() && await ValidateAsync(token.AccessToken, ct))
            {
                account.LastValidatedUtc = DateTimeOffset.UtcNow;
                account.State = PlatformAccountState.Connected;
                return token.AccessToken;
            }
            if (string.IsNullOrEmpty(token.RefreshToken))
            { account.State = PlatformAccountState.NeedsReauthentication; throw new InvalidOperationException("Reconnect Twitch account."); }
            account.State = PlatformAccountState.Refreshing;
            try
            {
                var refreshed = await RefreshAsync(token.RefreshToken, ct);
                await _secrets.StoreAsync(account.TokenReference, JsonSerializer.Serialize(refreshed), ct);
                account.LastAuthenticated = DateTimeOffset.UtcNow;
                account.LastValidatedUtc = DateTimeOffset.UtcNow;
                account.State = PlatformAccountState.Connected;
                return refreshed.AccessToken;
            }
            catch { account.State = PlatformAccountState.NeedsReauthentication; throw; }
        }
        return token.AccessToken;
    }

    public async Task<bool> ValidateAsync(string accessToken, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://id.twitch.tv/oauth2/validate");
        request.Headers.Authorization = new AuthenticationHeaderValue("OAuth", accessToken);
        using var response = await _http.SendAsync(request, ct);
        return response.StatusCode == HttpStatusCode.OK;
    }

    public async Task<OAuthTokenData> RefreshAsync(string refreshToken, CancellationToken ct = default)
    {
        using var response = await _http.PostAsync("https://id.twitch.tv/oauth2/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            { ["client_id"] = _settings.ClientId, ["grant_type"] = "refresh_token", ["refresh_token"] = refreshToken }), ct);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Twitch token refresh failed: HTTP {(int)response.StatusCode}.");
        var token = ParseToken(await response.Content.ReadAsStringAsync(ct));
        if (string.IsNullOrEmpty(token.RefreshToken)) token.RefreshToken = refreshToken;
        return token;
    }

    public async Task DisconnectAsync(TwitchAccount account, CancellationToken ct = default)
    {
        if (!string.IsNullOrEmpty(account.TokenReference))
        {
            var raw = await _secrets.GetAsync(account.TokenReference, ct);
            var token = string.IsNullOrEmpty(raw) ? null : JsonSerializer.Deserialize<OAuthTokenData>(raw);
            if (token != null)
            {
                try
                {
                    await _http.PostAsync("https://id.twitch.tv/oauth2/revoke",
                        new FormUrlEncodedContent(new Dictionary<string, string>
                        { ["client_id"] = _settings.ClientId, ["token"] = token.AccessToken }), ct);
                }
                catch (HttpRequestException) { }
            }
            await _secrets.RemoveAsync(account.TokenReference, ct);
        }
        account.TokenReference = null;
        account.Connected = false;
        account.State = PlatformAccountState.Disconnected;
    }

    private static OAuthTokenData ParseToken(string body)
    {
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        return new OAuthTokenData
        {
            AccessToken = root.GetProperty("access_token").GetString() ?? throw new InvalidOperationException("Missing access token."),
            RefreshToken = root.TryGetProperty("refresh_token", out var refresh) ? refresh.GetString() : null,
            TokenType = root.TryGetProperty("token_type", out var type) ? type.GetString() ?? "Bearer" : "Bearer",
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(root.GetProperty("expires_in").GetInt32()),
            Scope = root.TryGetProperty("scope", out var scope) ? scope.ValueKind == JsonValueKind.Array
                ? string.Join(' ', scope.EnumerateArray().Select(x => x.GetString())) : scope.GetString() : null
        };
    }

    private static string? ParseOAuthError(string body)
    {
        try
        {
            using var json = JsonDocument.Parse(body);
            var root = json.RootElement;
            return root.TryGetProperty("message", out var message) ? message.GetString()
                : root.TryGetProperty("error", out var error) ? error.GetString() : null;
        }
        catch (JsonException) { return null; }
    }
}
