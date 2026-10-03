using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using AravalsStream.Core.Accounts;
using AravalsStream.Core.Interfaces;
using AravalsStream.Core.Services;
using AravalsStream.Core.Settings;

namespace AravalsStream.Core.TikTok;

public sealed class TikTokAccountService
{
    private readonly TikTokApiClient _api;
    private readonly ISecretStorage _secrets;

    public TikTokAccountService(TikTokApiClient api, ISecretStorage secrets)
    {
        _api = api;
        _secrets = secrets;
    }

    public static string GenerateCodeVerifier(int length = 64)
    {
        if (length < 43) length = 43;
        if (length > 128) length = 128;

        const string unreserved = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-._~";
        var bytes = new byte[length];
        RandomNumberGenerator.Fill(bytes);
        var sb = new StringBuilder(length);
        for (int i = 0; i < length; i++)
        {
            sb.Append(unreserved[bytes[i] % unreserved.Length]);
        }
        return sb.ToString();
    }

    public static string GenerateCodeChallenge(string codeVerifier)
    {
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public async Task<TikTokAccount> AuthorizeAsync(
        TikTokOAuthSettings settings,
        CancellationToken ct = default)
    {
        if (!settings.IsConfigured)
            throw new InvalidOperationException("TikTok Client Key and Client Secret must be configured in Settings -> Accounts before connecting.");

        var clientSecret = await _secrets.GetAsync(settings.ClientSecretReference!, ct)
            ?? throw new InvalidOperationException("Stored TikTok Client Secret could not be retrieved from secure storage.");

        var redirectUri = settings.RedirectUri;
        if (!redirectUri.EndsWith('/')) redirectUri += "/";

        var state = Guid.NewGuid().ToString("N");
        var codeVerifier = GenerateCodeVerifier();
        var codeChallenge = GenerateCodeChallenge(codeVerifier);

        using var listener = new HttpListener();
        listener.Prefixes.Add(redirectUri);
        try
        {
            listener.Start();
        }
        catch (HttpListenerException ex)
        {
            throw new InvalidOperationException($"Failed to bind local loopback listener to {redirectUri}. Check permissions or port: {ex.Message}", ex);
        }

        var authUrl = _api.GenerateAuthorizeUrl(settings.ClientKey, redirectUri, state, codeChallenge, settings.Scopes);

        AppLog.Write("TikTokOAuth", "Launching browser for TikTok authorization...");
        try
        {
            Process.Start(new ProcessStartInfo(authUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            listener.Stop();
            throw new InvalidOperationException($"Failed to launch system browser for authorization: {ex.Message}", ex);
        }

        HttpListenerContext context;
        using (ct.Register(() => { try { listener.Stop(); } catch { } }))
        {
            try
            {
                context = await listener.GetContextAsync();
            }
            catch (Exception ex) when (ct.IsCancellationRequested)
            {
                throw new OperationCanceledException("TikTok authorization flow was canceled.", ex, ct);
            }
        }

        var query = context.Request.QueryString;
        var receivedState = query["state"];
        var code = query["code"];
        var error = query["error"];
        var errorDesc = query["error_description"];

        var responseHtml = @"<!DOCTYPE html><html><head><title>Aravals Stream</title><style>body{background:#1E2028;color:#FFFFFF;font-family:sans-serif;text-align:center;padding:50px;}</style></head><body><h2>{0}</h2><p>{1}</p></body></html>";

        if (!string.IsNullOrEmpty(error))
        {
            var body = string.Format(responseHtml, "Authorization Failed", WebUtility.HtmlEncode(errorDesc ?? error));
            var buf = Encoding.UTF8.GetBytes(body);
            context.Response.ContentType = "text/html";
            context.Response.ContentLength64 = buf.Length;
            await context.Response.OutputStream.WriteAsync(buf, ct);
            context.Response.Close();
            throw new TikTokApiException(error, errorDesc ?? "Authorization was denied by user or TikTok.");
        }

        if (receivedState != state)
        {
            var body = string.Format(responseHtml, "Security Error", "State parameter mismatch (possible CSRF attack).");
            var buf = Encoding.UTF8.GetBytes(body);
            context.Response.ContentType = "text/html";
            context.Response.ContentLength64 = buf.Length;
            await context.Response.OutputStream.WriteAsync(buf, ct);
            context.Response.Close();
            throw new InvalidOperationException("OAuth state parameter mismatch.");
        }

        if (string.IsNullOrEmpty(code))
        {
            var body = string.Format(responseHtml, "Authorization Error", "No authorization code returned.");
            var buf = Encoding.UTF8.GetBytes(body);
            context.Response.ContentType = "text/html";
            context.Response.ContentLength64 = buf.Length;
            await context.Response.OutputStream.WriteAsync(buf, ct);
            context.Response.Close();
            throw new InvalidOperationException("No authorization code was received.");
        }

        var successBody = string.Format(responseHtml, "Connected to TikTok!", "You can now close this tab and return to Aravals Stream.");
        var successBuf = Encoding.UTF8.GetBytes(successBody);
        context.Response.ContentType = "text/html";
        context.Response.ContentLength64 = successBuf.Length;
        await context.Response.OutputStream.WriteAsync(successBuf, ct);
        context.Response.Close();
        listener.Stop();

        AppLog.Write("TikTokOAuth", "Exchanging authorization code for access token...");
        var tokens = await _api.ExchangeCodeForTokenAsync(settings.ClientKey, clientSecret, code, codeVerifier, redirectUri, ct);

        var tokenRef = Guid.NewGuid().ToString("N");
        var refreshRef = Guid.NewGuid().ToString("N");

        await _secrets.StoreAsync(tokenRef, tokens.AccessToken, ct);
        if (!string.IsNullOrEmpty(tokens.RefreshToken))
        {
            await _secrets.StoreAsync(refreshRef, tokens.RefreshToken, ct);
        }

        AppLog.Write("TikTokOAuth", "Fetching creator profile info...");
        var user = await _api.GetUserInfoAsync(tokens.AccessToken, ct);

        var account = new TikTokAccount
        {
            OpenId = user.OpenId,
            UnionId = user.UnionId,
            DisplayName = string.IsNullOrWhiteSpace(user.DisplayName) ? "TikTok Creator" : user.DisplayName,
            AvatarUrl = user.AvatarUrl,
            Connected = true,
            TokenReference = tokenRef,
            RefreshTokenReference = refreshRef,
            LastAuthenticated = DateTimeOffset.UtcNow,
            TokenExpiryUtc = DateTimeOffset.UtcNow.AddSeconds(tokens.ExpiresIn),
            State = PlatformAccountState.Connected
        };

        AppLog.Write("TikTokOAuth", $"Successfully authorized TikTok account: {account.DisplayName} (OpenId: {account.OpenId})");
        return account;
    }

    public async Task<string> GetAccessTokenAsync(
        TikTokAccount account,
        TikTokOAuthSettings settings,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(account.TokenReference))
            throw new InvalidOperationException("Account does not have a valid token reference.");

        if (account.TokenExpiryUtc.HasValue && account.TokenExpiryUtc.Value <= DateTimeOffset.UtcNow.AddMinutes(5))
        {
            await RefreshTokensAsync(account, settings, ct);
        }

        var token = await _secrets.GetAsync(account.TokenReference, ct);
        if (string.IsNullOrEmpty(token))
            throw new InvalidOperationException("Access token could not be retrieved from secure storage.");

        return token;
    }

    public async Task RefreshTokensAsync(
        TikTokAccount account,
        TikTokOAuthSettings settings,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(account.RefreshTokenReference))
            throw new InvalidOperationException("No refresh token reference available for this account.");

        var clientSecret = await _secrets.GetAsync(settings.ClientSecretReference ?? "", ct)
            ?? throw new InvalidOperationException("Client secret not available for token refresh.");

        var currentRefresh = await _secrets.GetAsync(account.RefreshTokenReference, ct);
        if (string.IsNullOrEmpty(currentRefresh))
            throw new InvalidOperationException("Stored refresh token is missing or expired.");

        account.State = PlatformAccountState.Refreshing;
        try
        {
            var result = await _api.RefreshTokenAsync(settings.ClientKey, clientSecret, currentRefresh, ct);

            account.TokenReference ??= Guid.NewGuid().ToString("N");
            await _secrets.StoreAsync(account.TokenReference, result.AccessToken, ct);

            if (!string.IsNullOrEmpty(result.RefreshToken))
            {
                await _secrets.StoreAsync(account.RefreshTokenReference, result.RefreshToken, ct);
            }

            account.TokenExpiryUtc = DateTimeOffset.UtcNow.AddSeconds(result.ExpiresIn);
            account.LastAuthenticated = DateTimeOffset.UtcNow;
            account.State = PlatformAccountState.Connected;
            AppLog.Write("TikTokOAuth", $"Successfully refreshed TikTok token for {account.DisplayName}");
        }
        catch (TikTokApiException ex) when (ex.ErrorCode is "access_token_invalid" or "refresh_token_invalid" or "10008")
        {
            account.State = PlatformAccountState.NeedsReauthentication;
            AppLog.Write("TikTokOAuth", $"TikTok token refresh requires reauthentication: {ex.Message}");
            throw;
        }
        catch (Exception ex)
        {
            account.State = PlatformAccountState.Error;
            AppLog.Write("TikTokOAuth", $"TikTok token refresh failed: {ex.Message}");
            throw;
        }
    }

    public async Task DisconnectAsync(TikTokAccount account, CancellationToken ct = default)
    {
        if (account.TokenReference != null)
        {
            await _secrets.RemoveAsync(account.TokenReference, ct);
            account.TokenReference = null;
        }
        if (account.RefreshTokenReference != null)
        {
            await _secrets.RemoveAsync(account.RefreshTokenReference, ct);
            account.RefreshTokenReference = null;
        }

        account.Connected = false;
        account.State = PlatformAccountState.Disconnected;
        AppLog.Write("TikTokOAuth", $"Disconnected TikTok account: {account.DisplayName}");
    }
}
