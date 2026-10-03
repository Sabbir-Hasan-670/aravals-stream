using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AravalsStream.Core.Accounts;
using AravalsStream.Core.Interfaces;
using AravalsStream.Core.Services;
using AravalsStream.Core.Settings;

namespace AravalsStream.Core.YouTube;

public sealed class GoogleOAuthClient
{
    private const string AuthEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";
    private const string TokenEndpoint = "https://oauth2.googleapis.com/token";
    private const string RevokeEndpoint = "https://oauth2.googleapis.com/revoke";

    private readonly GoogleOAuthSettings _settings;
    private readonly ISecretStorage _secretStorage;
    private readonly HttpClient _http;

    public GoogleOAuthClient(
        GoogleOAuthSettings settings,
        ISecretStorage secretStorage,
        HttpClient? httpClient = null)
    {
        _settings = settings;
        _secretStorage = secretStorage;
        _http = httpClient ?? new HttpClient();
    }

    public static string GenerateCodeVerifier()
    {
        var bytes = new byte[64];
        RandomNumberGenerator.Fill(bytes);
        return Base64UrlEncode(bytes);
    }

    public static string GenerateCodeChallenge(string codeVerifier)
    {
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier));
        return Base64UrlEncode(hash);
    }

    public static string GenerateState()
    {
        var bytes = new byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Base64UrlEncode(bytes);
    }

    public static string Base64UrlEncode(byte[] input)
    {
        return Convert.ToBase64String(input)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    public string BuildAuthorizationUrl(string codeChallenge, string state, string redirectUri)
    {
        var scope = Uri.EscapeDataString(_settings.Scopes);
        var encRedirect = Uri.EscapeDataString(redirectUri);
        var encChallenge = Uri.EscapeDataString(codeChallenge);
        var encState = Uri.EscapeDataString(state);
        var encClientId = Uri.EscapeDataString(_settings.ClientId);

        return $"{AuthEndpoint}?client_id={encClientId}&redirect_uri={encRedirect}&response_type=code&scope={scope}&code_challenge={encChallenge}&code_challenge_method=S256&state={encState}&access_type=offline&prompt=consent";
    }

    public static int FindAvailablePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public async Task<(string Code, string State)> ListenForCallbackAsync(int port, CancellationToken ct)
    {
        var prefix = $"http://127.0.0.1:{port}/";
        using var listener = new HttpListener();
        listener.Prefixes.Add(prefix);
        listener.Start();

        try
        {
            using var reg = ct.Register(() => { try { listener.Stop(); } catch { } });
            var context = await listener.GetContextAsync();

            var req = context.Request;
            var code = req.QueryString["code"];
            var state = req.QueryString["state"];
            var error = req.QueryString["error"];

            var successHtml = "<!DOCTYPE html><html><head><meta charset='utf-8'><title>Aravals Stream - Authorized</title><style>body{background:#1B1D24;color:#FFFFFF;font-family:-apple-system,BlinkMacSystemFont,Segoe UI,Roboto,sans-serif;display:flex;align-items:center;justify-content:center;height:100vh;margin:0;} .card{background:#232630;padding:40px;border-radius:12px;box-shadow:0 8px 24px rgba(0,0,0,0.5);text-align:center;max-width:440px;} h1{color:#10B981;margin-bottom:12px;font-size:24px;} p{color:#9CA3AF;line-height:1.5;}</style></head><body><div class='card'><h1>Authorization Successful!</h1><p>Your YouTube account is now connected to <strong>Aravals Stream</strong>.</p><p>You may safely close this browser window and return to the application.</p></div></body></html>";
            var errorHtml = string.IsNullOrEmpty(error) ? "" : $"<!DOCTYPE html><html><head><meta charset='utf-8'><title>Aravals Stream - Authorization Error</title><style>body{{background:#1B1D24;color:#FFFFFF;font-family:-apple-system,BlinkMacSystemFont,Segoe UI,Roboto,sans-serif;display:flex;align-items:center;justify-content:center;height:100vh;margin:0;}} .card{{background:#232630;padding:40px;border-radius:12px;text-align:center;max-width:440px;}} h1{{color:#EF4444;font-size:24px;}}</style></head><body><div class='card'><h1>Authorization Failed</h1><p>{System.Web.HttpUtility.HtmlEncode(error)}</p></div></body></html>";

            var responseHtml = string.IsNullOrEmpty(error) ? successHtml : errorHtml;

            var buf = Encoding.UTF8.GetBytes(responseHtml);
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.ContentLength64 = buf.Length;
            context.Response.StatusCode = string.IsNullOrEmpty(error) ? 200 : 400;
            await context.Response.OutputStream.WriteAsync(buf, ct);
            context.Response.Close();

            if (!string.IsNullOrEmpty(error))
                throw new InvalidOperationException($"Google OAuth returned error: {error}");

            if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state))
                throw new InvalidOperationException("OAuth callback missing code or state parameter.");

            return (code, state);
        }
        finally
        {
            try { listener.Stop(); } catch { }
        }
    }

    public async Task<OAuthTokenData> ExchangeCodeForTokensAsync(
        string code,
        string codeVerifier,
        string redirectUri,
        CancellationToken ct = default)
    {
        var parameters = new Dictionary<string, string>
        {
            ["client_id"] = _settings.ClientId,
            ["client_secret"] = _settings.ClientSecret,
            ["code"] = code,
            ["code_verifier"] = codeVerifier,
            ["grant_type"] = "authorization_code",
            ["redirect_uri"] = redirectUri
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(parameters)
        };

        using var res = await _http.SendAsync(req, ct);
        var json = await res.Content.ReadAsStringAsync(ct);

        if (!res.IsSuccessStatusCode)
        {
            AppLog.Write("OAuth", $"Token exchange failed with HTTP {res.StatusCode}: {AppLog.Sanitize(json)}");
            throw new InvalidOperationException($"Failed to exchange authorization code for tokens: {res.StatusCode}");
        }

        var tokenResponse = JsonSerializer.Deserialize<GoogleTokenResponse>(json);
        if (tokenResponse == null || string.IsNullOrEmpty(tokenResponse.AccessToken))
            throw new InvalidOperationException("Google token endpoint returned empty or invalid token payload.");

        return new OAuthTokenData
        {
            AccessToken = tokenResponse.AccessToken,
            RefreshToken = tokenResponse.RefreshToken,
            TokenType = tokenResponse.TokenType ?? "Bearer",
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(tokenResponse.ExpiresIn > 0 ? tokenResponse.ExpiresIn : 3600),
            Scope = tokenResponse.Scope
        };
    }

    public async Task<OAuthTokenData> RefreshTokenAsync(string refreshToken, CancellationToken ct = default)
    {
        var parameters = new Dictionary<string, string>
        {
            ["client_id"] = _settings.ClientId,
            ["client_secret"] = _settings.ClientSecret,
            ["refresh_token"] = refreshToken,
            ["grant_type"] = "refresh_token"
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(parameters)
        };

        using var res = await _http.SendAsync(req, ct);
        var json = await res.Content.ReadAsStringAsync(ct);

        if (!res.IsSuccessStatusCode)
        {
            AppLog.Write("OAuth", $"Token refresh failed with HTTP {res.StatusCode}: {AppLog.Sanitize(json)}");
            throw new InvalidOperationException($"Failed to refresh OAuth token: {res.StatusCode}");
        }

        var tokenResponse = JsonSerializer.Deserialize<GoogleTokenResponse>(json);
        if (tokenResponse == null || string.IsNullOrEmpty(tokenResponse.AccessToken))
            throw new InvalidOperationException("Google token refresh returned empty payload.");

        return new OAuthTokenData
        {
            AccessToken = tokenResponse.AccessToken,
            RefreshToken = !string.IsNullOrEmpty(tokenResponse.RefreshToken) ? tokenResponse.RefreshToken : refreshToken,
            TokenType = tokenResponse.TokenType ?? "Bearer",
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(tokenResponse.ExpiresIn > 0 ? tokenResponse.ExpiresIn : 3600),
            Scope = tokenResponse.Scope
        };
    }

    public async Task RevokeTokenAsync(string token, CancellationToken ct = default)
    {
        try
        {
            var url = $"{RevokeEndpoint}?token={Uri.EscapeDataString(token)}";
            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            await _http.SendAsync(req, ct);
        }
        catch (Exception ex)
        {
            AppLog.Write("OAuth", $"Token revocation error: {ex.Message}");
        }
    }

    public async Task<string> GetValidAccessTokenAsync(YouTubeAccount account, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(account.TokenReference))
            throw new InvalidOperationException("Account has no token reference configured.");

        var raw = await _secretStorage.GetAsync(account.TokenReference, ct);
        if (string.IsNullOrEmpty(raw))
            throw new InvalidOperationException("Stored OAuth credentials not found in secure storage.");

        var tokenData = JsonSerializer.Deserialize<OAuthTokenData>(raw);
        if (tokenData == null)
            throw new InvalidOperationException("Invalid OAuth token structure in secure storage.");

        if (tokenData.IsExpired())
        {
            if (string.IsNullOrEmpty(tokenData.RefreshToken))
                throw new InvalidOperationException("Access token expired and no refresh token is available. Please reauthenticate.");

            AppLog.Write("OAuth", $"Access token for channel '{account.ChannelTitle}' is expired. Refreshing token...");
            var refreshed = await RefreshTokenAsync(tokenData.RefreshToken, ct);

            // Store back in DPAPI
            await _secretStorage.StoreAsync(account.TokenReference, JsonSerializer.Serialize(refreshed), ct);
            account.LastAuthenticated = DateTimeOffset.UtcNow;
            return refreshed.AccessToken;
        }

        return tokenData.AccessToken;
    }

    public async Task<YouTubeAccount> AuthorizeAsync(
        Func<string, Task>? launchBrowser = null,
        CancellationToken ct = default)
    {
        if (!_settings.IsConfigured)
            throw new InvalidOperationException("Google Client ID is not configured. Please configure it in Settings -> Accounts.");

        var verifier = GenerateCodeVerifier();
        var challenge = GenerateCodeChallenge(verifier);
        var state = GenerateState();
        var port = FindAvailablePort();
        var redirectUri = $"http://127.0.0.1:{port}/oauth2redirect";

        var authUrl = BuildAuthorizationUrl(challenge, state, redirectUri);

        // 1. Launch system browser
        if (launchBrowser != null)
        {
            await launchBrowser(authUrl);
        }
        else
        {
            Process.Start(new ProcessStartInfo(authUrl) { UseShellExecute = true });
        }

        // 2. Listen for callback
        var (code, returnedState) = await ListenForCallbackAsync(port, ct);
        if (!string.Equals(state, returnedState, StringComparison.Ordinal))
            throw new InvalidOperationException("OAuth state mismatch. Possible cross-site request forgery.");

        // 3. Exchange code for tokens
        var tokens = await ExchangeCodeForTokensAsync(code, verifier, redirectUri, ct);

        // 4. Save tokens securely in DPAPI
        var tokenRef = Guid.NewGuid().ToString("N");
        await _secretStorage.StoreAsync(tokenRef, JsonSerializer.Serialize(tokens), ct);

        // 5. Fetch channel identity using access token
        var api = new YouTubeApiClient(_http);
        var identity = await api.GetChannelIdentityAsync(tokens.AccessToken, ct);

        var account = new YouTubeAccount
        {
            Id = Guid.NewGuid(),
            ChannelId = identity.ChannelId,
            ChannelTitle = identity.ChannelTitle,
            ChannelThumbnailUrl = identity.ThumbnailUrl,
            SubscriberCount = identity.SubscriberCount,
            HiddenSubscriberCount = identity.HiddenSubscriberCount,
            Connected = true,
            LastAuthenticated = DateTimeOffset.UtcNow,
            TokenReference = tokenRef
        };

        AppLog.Write("OAuth", $"Successfully authenticated YouTube channel '{account.ChannelTitle}' ({account.ChannelId}).");
        return account;
    }

    private sealed class GoogleTokenResponse
    {
        [JsonPropertyName("access_token")]
        public string? AccessToken { get; set; }

        [JsonPropertyName("refresh_token")]
        public string? RefreshToken { get; set; }

        [JsonPropertyName("expires_in")]
        public int ExpiresIn { get; set; }

        [JsonPropertyName("token_type")]
        public string? TokenType { get; set; }

        [JsonPropertyName("scope")]
        public string? Scope { get; set; }
    }
}
