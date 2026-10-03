using System.Net.Http.Headers;
using System.Text.Json;
using AravalsStream.Core.Services;

namespace AravalsStream.Core.TikTok;

public sealed class TikTokApiException : Exception
{
    public string? ErrorCode { get; }

    public TikTokApiException(string? code, string message)
        : base(AppLog.Sanitize(message))
    {
        ErrorCode = code;
    }
}

public sealed class TikTokTokenResult
{
    public string AccessToken { get; set; } = "";
    public string RefreshToken { get; set; } = "";
    public int ExpiresIn { get; set; }
    public string OpenId { get; set; } = "";
    public string? Scope { get; set; }
}

public sealed class TikTokUserInfoResult
{
    public string OpenId { get; set; } = "";
    public string UnionId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string? AvatarUrl { get; set; }
}

public sealed class TikTokApiClient
{
    private readonly HttpClient _http;

    public TikTokApiClient(HttpClient? http = null)
    {
        _http = http ?? new HttpClient();
    }

    public string GenerateAuthorizeUrl(string clientKey, string redirectUri, string state, string codeChallenge, string scopes = "user.info.basic")
    {
        var escapedRedirect = Uri.EscapeDataString(redirectUri);
        var escapedScopes = Uri.EscapeDataString(scopes);
        var escapedChallenge = Uri.EscapeDataString(codeChallenge);
        var escapedState = Uri.EscapeDataString(state);

        return $"{TikTokCapabilitySet.DefaultAuthorizeBaseUrl}?client_key={Uri.EscapeDataString(clientKey)}&scope={escapedScopes}&response_type=code&redirect_uri={escapedRedirect}&state={escapedState}&code_challenge={escapedChallenge}&code_challenge_method=S256";
    }

    public Task<TikTokTokenResult> ExchangeCodeAsync(
        string clientKey,
        string clientSecret,
        string code,
        string redirectUri,
        string codeVerifier,
        CancellationToken ct = default) =>
        ExchangeCodeForTokenAsync(clientKey, clientSecret, code, codeVerifier, redirectUri, ct);

    public async Task<TikTokTokenResult> ExchangeCodeForTokenAsync(
        string clientKey,
        string clientSecret,
        string code,
        string codeVerifier,
        string redirectUri,
        CancellationToken ct = default)
    {
        var form = new Dictionary<string, string>
        {
            ["client_key"] = clientKey,
            ["client_secret"] = clientSecret,
            ["code"] = code,
            ["grant_type"] = "authorization_code",
            ["redirect_uri"] = redirectUri,
            ["code_verifier"] = codeVerifier
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, TikTokCapabilitySet.DefaultTokenUrl)
        {
            Content = new FormUrlEncodedContent(form)
        };

        using var response = await _http.SendAsync(request, ct);
        var json = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            ExtractAndThrowError(json, $"Token exchange failed with HTTP {(int)response.StatusCode}");
        }

        return ParseTokenResponse(json);
    }

    public async Task<TikTokTokenResult> RefreshTokenAsync(
        string clientKey,
        string clientSecret,
        string refreshToken,
        CancellationToken ct = default)
    {
        var form = new Dictionary<string, string>
        {
            ["client_key"] = clientKey,
            ["client_secret"] = clientSecret,
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, TikTokCapabilitySet.DefaultTokenUrl)
        {
            Content = new FormUrlEncodedContent(form)
        };

        using var response = await _http.SendAsync(request, ct);
        var json = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            ExtractAndThrowError(json, $"Token refresh failed with HTTP {(int)response.StatusCode}");
        }

        return ParseTokenResponse(json);
    }

    public async Task<TikTokUserInfoResult> GetUserInfoAsync(string accessToken, CancellationToken ct = default)
    {
        var url = $"{TikTokCapabilitySet.DefaultUserInfoUrl}?fields=open_id,union_id,avatar_url,display_name";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await _http.SendAsync(request, ct);
        var json = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            ExtractAndThrowError(json, $"User info query failed with HTTP {(int)response.StatusCode}");
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var err) && err.TryGetProperty("code", out var codeEl) && codeEl.GetString() != "ok" && codeEl.GetString() != "0")
            {
                var msg = err.TryGetProperty("message", out var msgEl) ? msgEl.GetString() : "Unknown error";
                throw new TikTokApiException(codeEl.GetString(), msg ?? "Unknown error");
            }

            var userEl = root.TryGetProperty("data", out var dataEl) && dataEl.TryGetProperty("user", out var u) ? u : dataEl;

            return new TikTokUserInfoResult
            {
                OpenId = userEl.TryGetProperty("open_id", out var oid) ? oid.GetString() ?? "" : "",
                UnionId = userEl.TryGetProperty("union_id", out var uid) ? uid.GetString() ?? "" : "",
                DisplayName = userEl.TryGetProperty("display_name", out var dn) ? dn.GetString() ?? "" : "",
                AvatarUrl = userEl.TryGetProperty("avatar_url", out var av) ? av.GetString() : null
            };
        }
        catch (JsonException ex)
        {
            throw new TikTokApiException("PARSE_ERROR", $"Failed to parse user info response: {ex.Message}");
        }
    }

    private static TikTokTokenResult ParseTokenResponse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var err) && err.TryGetProperty("code", out var codeEl) && codeEl.GetString() != "ok" && codeEl.GetString() != "0")
            {
                var msg = err.TryGetProperty("message", out var msgEl) ? msgEl.GetString() : "Unknown error";
                throw new TikTokApiException(codeEl.GetString(), msg ?? "Unknown error");
            }

            var data = root.TryGetProperty("data", out var d) ? d : root;

            var access = data.TryGetProperty("access_token", out var at) ? at.GetString() : null;
            if (string.IsNullOrEmpty(access))
            {
                throw new TikTokApiException("EMPTY_TOKEN", "Response did not contain an access_token.");
            }

            return new TikTokTokenResult
            {
                AccessToken = access,
                RefreshToken = data.TryGetProperty("refresh_token", out var rt) ? rt.GetString() ?? "" : "",
                ExpiresIn = data.TryGetProperty("expires_in", out var exp) ? exp.GetInt32() : 86400,
                OpenId = data.TryGetProperty("open_id", out var oid) ? oid.GetString() ?? "" : "",
                Scope = data.TryGetProperty("scope", out var sc) ? sc.GetString() : null
            };
        }
        catch (JsonException ex)
        {
            throw new TikTokApiException("PARSE_ERROR", $"Failed to parse token response: {ex.Message}");
        }
    }

    private static void ExtractAndThrowError(string json, string fallbackMessage)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var err))
            {
                var code = err.TryGetProperty("code", out var c) ? c.GetString() : null;
                var msg = err.TryGetProperty("message", out var m) ? m.GetString() : fallbackMessage;
                throw new TikTokApiException(code, msg ?? fallbackMessage);
            }
            if (root.TryGetProperty("message", out var mOnly))
            {
                throw new TikTokApiException(null, mOnly.GetString() ?? fallbackMessage);
            }
        }
        catch (JsonException) { }

        throw new TikTokApiException(null, fallbackMessage);
    }
}
