using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AravalsStream.Core.Accounts;
using AravalsStream.Core.Interfaces;
using AravalsStream.Core.Settings;

namespace AravalsStream.Core.Kick;

public sealed class KickOAuthClient
{
    public const string RequestedScopes = "user:read channel:read channel:write chat:write streamkey:read events:subscribe";
    private static readonly SemaphoreSlim TokenGate = new(1, 1);
    private readonly KickOAuthSettings _settings;
    private readonly ISecretStorage _secrets;
    private readonly HttpClient _http;

    public KickOAuthClient(KickOAuthSettings settings, ISecretStorage secrets, HttpClient? http = null)
    { _settings = settings; _secrets = secrets; _http = http ?? new HttpClient(); }

    public static string NewRandomValue() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(48))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    public static string CodeChallenge(string verifier) => Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public string BuildAuthorizationUrl(string challenge, string state)
    {
        ValidateRedirect();
        return "https://id.kick.com/oauth/authorize?response_type=code" +
            $"&client_id={Uri.EscapeDataString(_settings.ClientId)}" +
            $"&redirect_uri={Uri.EscapeDataString(_settings.RedirectUri)}" +
            $"&scope={Uri.EscapeDataString(RequestedScopes)}" +
            $"&code_challenge={Uri.EscapeDataString(challenge)}&code_challenge_method=S256" +
            $"&state={Uri.EscapeDataString(state)}";
    }

    private void ValidateRedirect()
    {
        if (!Uri.TryCreate(_settings.RedirectUri, UriKind.Absolute, out var uri) ||
            uri.Scheme != "http" || uri.Host != "localhost" || uri.Port < 1024 ||
            !uri.AbsolutePath.EndsWith('/'))
            throw new InvalidOperationException("Kick redirect URI must be an HTTP localhost URL with a fixed port and trailing slash.");
    }

    public async Task<KickAccount> AuthorizeAsync(CancellationToken ct = default)
    {
        if (!_settings.IsConfigured) throw new InvalidOperationException("Kick Client ID, secret, and redirect URI are required.");
        ValidateRedirect();
        var verifier = NewRandomValue();
        var state = NewRandomValue();
        using var listener = new HttpListener();
        listener.Prefixes.Add(_settings.RedirectUri);
        listener.Start();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        Process.Start(new ProcessStartInfo(BuildAuthorizationUrl(CodeChallenge(verifier), state)) { UseShellExecute = true });
        HttpListenerContext callback;
        try { callback = await listener.GetContextAsync().WaitAsync(timeout.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new TimeoutException("Kick authorization timed out."); }
        var query = callback.Request.QueryString;
        var code = query["code"];
        var returnedState = query["state"];
        var error = query["error"];
        var success = !string.IsNullOrEmpty(code) && string.IsNullOrEmpty(error) &&
            CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(state), Encoding.UTF8.GetBytes(returnedState ?? ""));
        var body = success ? "Kick authorization received. Return to Aravals Stream." : "Kick authorization failed. Return to Aravals Stream.";
        var bytes = Encoding.UTF8.GetBytes(body);
        callback.Response.ContentType = "text/plain; charset=utf-8";
        callback.Response.ContentLength64 = bytes.Length;
        await callback.Response.OutputStream.WriteAsync(bytes, ct);
        callback.Response.Close();
        listener.Stop();
        if (!success) throw new InvalidOperationException(!string.IsNullOrEmpty(error) ? "Kick authorization was denied." : "Kick OAuth callback was invalid.");
        var token = await ExchangeCodeAsync(code!, verifier, ct);
        var api = new KickApiClient(_http);
        var user = await api.GetUserAsync(token.AccessToken, ct);
        var channel = await api.GetChannelAsync(token.AccessToken, user.UserId, ct);
        var reference = Guid.NewGuid().ToString();
        await _secrets.StoreAsync(reference, JsonSerializer.Serialize(token), ct);
        return new KickAccount { KickUserId = user.UserId, Username = channel.Slug.Length > 0 ? channel.Slug : user.Username,
            DisplayName = user.DisplayName, AvatarUrl = user.AvatarUrl,
            Connected = true, State = PlatformAccountState.Connected, TokenReference = reference,
            LastAuthenticated = DateTimeOffset.UtcNow, LastValidatedUtc = DateTimeOffset.UtcNow };
    }

    public async Task<OAuthTokenData> ExchangeCodeAsync(string code, string verifier, CancellationToken ct = default)
    {
        var secret = await GetClientSecretAsync(ct);
        using var response = await _http.PostAsync("https://id.kick.com/oauth/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code", ["client_id"] = _settings.ClientId,
            ["client_secret"] = secret, ["redirect_uri"] = _settings.RedirectUri,
            ["code_verifier"] = verifier, ["code"] = code
        }), ct);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Kick token exchange failed: HTTP {(int)response.StatusCode}.");
        return ParseToken(await response.Content.ReadAsStringAsync(ct));
    }

    public async Task<string> GetValidAccessTokenAsync(KickAccount account, CancellationToken ct = default)
    {
        await TokenGate.WaitAsync(ct);
        try
        {
            if (!account.Connected || string.IsNullOrEmpty(account.TokenReference))
                throw new InvalidOperationException("Kick account is not connected.");
            var raw = await _secrets.GetAsync(account.TokenReference, ct);
            var token = string.IsNullOrEmpty(raw) ? null : JsonSerializer.Deserialize<OAuthTokenData>(raw);
            if (token == null) { account.State = PlatformAccountState.NeedsReauthentication; throw new InvalidOperationException("Kick credentials are unavailable."); }
            if (!token.IsExpired() && account.LastValidatedUtc is { } last && DateTimeOffset.UtcNow - last < TimeSpan.FromHours(1))
                return token.AccessToken;
            if (!token.IsExpired() && await IsActiveAsync(token.AccessToken, ct))
            { account.LastValidatedUtc = DateTimeOffset.UtcNow; account.State = PlatformAccountState.Connected; return token.AccessToken; }
            if (string.IsNullOrEmpty(token.RefreshToken))
            { account.State = PlatformAccountState.NeedsReauthentication; throw new InvalidOperationException("Reconnect Kick account."); }
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
        finally { TokenGate.Release(); }
    }

    public async Task<bool> IsActiveAsync(string accessToken, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://id.kick.com/oauth/token/introspect");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) return false;
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return json.RootElement.TryGetProperty("data", out var data) &&
            data.TryGetProperty("active", out var active) && active.ValueKind == JsonValueKind.True;
    }

    public async Task<OAuthTokenData> RefreshAsync(string refreshToken, CancellationToken ct = default)
    {
        var secret = await GetClientSecretAsync(ct);
        using var response = await _http.PostAsync("https://id.kick.com/oauth/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token", ["client_id"] = _settings.ClientId,
            ["client_secret"] = secret, ["refresh_token"] = refreshToken
        }), ct);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Kick token refresh failed: HTTP {(int)response.StatusCode}.");
        var token = ParseToken(await response.Content.ReadAsStringAsync(ct));
        if (string.IsNullOrEmpty(token.RefreshToken)) token.RefreshToken = refreshToken;
        return token;
    }

    public async Task DisconnectAsync(KickAccount account, CancellationToken ct = default)
    {
        if (!string.IsNullOrEmpty(account.TokenReference))
        {
            var raw = await _secrets.GetAsync(account.TokenReference, ct);
            var token = string.IsNullOrEmpty(raw) ? null : JsonSerializer.Deserialize<OAuthTokenData>(raw);
            if (token != null)
            {
                try
                {
                    if (!string.IsNullOrEmpty(token.RefreshToken))
                    {
                        using var refreshResponse = await _http.PostAsync("https://id.kick.com/oauth/revoke?token=" +
                            Uri.EscapeDataString(token.RefreshToken) + "&token_hint_type=refresh_token", null, ct);
                    }
                    using var accessResponse = await _http.PostAsync("https://id.kick.com/oauth/revoke?token=" +
                        Uri.EscapeDataString(token.AccessToken) + "&token_hint_type=access_token", null, ct);
                }
                catch (HttpRequestException) { }
            }
            await _secrets.RemoveAsync(account.TokenReference, ct);
        }
        account.TokenReference = null;
        account.Connected = false;
        account.State = PlatformAccountState.Disconnected;
    }

    private async Task<string> GetClientSecretAsync(CancellationToken ct)
    {
        var value = await _secrets.GetAsync(_settings.ClientSecretReference, ct);
        return !string.IsNullOrWhiteSpace(value) ? value : throw new InvalidOperationException("Kick client secret is unavailable.");
    }

    private static OAuthTokenData ParseToken(string body)
    {
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        return new OAuthTokenData
        {
            AccessToken = root.GetProperty("access_token").GetString() ?? throw new InvalidOperationException("Missing Kick access token."),
            RefreshToken = root.TryGetProperty("refresh_token", out var refresh) ? refresh.GetString() : null,
            TokenType = root.TryGetProperty("token_type", out var type) ? type.GetString() ?? "Bearer" : "Bearer",
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(root.GetProperty("expires_in").GetInt32()),
            Scope = root.TryGetProperty("scope", out var scope) ? scope.GetString() : null
        };
    }
}
