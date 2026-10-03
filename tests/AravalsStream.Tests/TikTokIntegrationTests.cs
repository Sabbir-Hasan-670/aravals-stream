using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AravalsStream.Core.Accounts;
using AravalsStream.Core.Interfaces;
using AravalsStream.Core.Models;
using AravalsStream.Core.Platforms;
using AravalsStream.Core.Services;
using AravalsStream.Core.Settings;
using AravalsStream.Core.TikTok;
using Xunit;

namespace AravalsStream.Tests;

public sealed class TikTokIntegrationTests
{
    private sealed class MockSecretStorage : ISecretStorage
    {
        public Dictionary<string, string> Items { get; } = [];
        public Task StoreAsync(string key, string secret, CancellationToken cancellationToken = default)
        {
            Items[key] = secret;
            return Task.CompletedTask;
        }
        public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult(Items.GetValueOrDefault(key));
        public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
        {
            Items.Remove(key);
            return Task.CompletedTask;
        }
    }

    private sealed class MockHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(handler(request));
    }

    private static HttpResponseMessage JsonResponse(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    [Fact]
    public void TikTokCapabilityMatrix_AccuratelyReflectsOfficialApis()
    {
        var caps = TikTokCapabilitySet.Current;
        Assert.True(caps.SupportsNativeLogin);
        Assert.True(caps.SupportsProfileRead);
        Assert.False(caps.SupportsLiveCreation);
        Assert.False(caps.SupportsLiveIngest);
        Assert.False(caps.SupportsLiveChatRead);
        Assert.False(caps.SupportsLiveChatSend);
        Assert.False(caps.SupportsLiveViewerStats);
        Assert.False(caps.SupportsLiveEvents);
        Assert.True(caps.RequiresAppReview);
        Assert.True(caps.RequiresServerSecret);
        Assert.Equal(TikTokAppApprovalStatus.Development, caps.AppApprovalStatus);
    }

    [Fact]
    public void PkceVerifierAndChallenge_FollowRfc7636AndTikTokSpecs()
    {
        var verifier = TikTokAccountService.GenerateCodeVerifier(64);
        Assert.Equal(64, verifier.Length);
        Assert.Matches(@"^[a-zA-Z0-9\-._~]+$", verifier);

        var challenge = TikTokAccountService.GenerateCodeChallenge(verifier);
        Assert.Equal(64, challenge.Length);
        Assert.Matches(@"^[0-9a-f]+$", challenge); // lowercase hex

        // Verify SHA-256 calculation explicitly
        var expectedHash = Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).ToLowerInvariant();
        Assert.Equal(expectedHash, challenge);
    }

    [Fact]
    public void AuthorizeUrlGeneration_ContainsRequiredParametersAndScopes()
    {
        var client = new TikTokApiClient();
        var url = client.GenerateAuthorizeUrl(
            clientKey: "aw12345678",
            redirectUri: "http://127.0.0.1:19455/callback/",
            state: "test-state-123",
            codeChallenge: "test-challenge-hex",
            scopes: "user.info.basic");

        Assert.StartsWith("https://www.tiktok.com/v2/auth/authorize/", url);
        Assert.Contains("client_key=aw12345678", url);
        Assert.Contains("response_type=code", url);
        Assert.Contains("scope=user.info.basic", url);
        Assert.Contains("state=test-state-123", url);
        Assert.Contains("code_challenge=test-challenge-hex", url);
        Assert.Contains("code_challenge_method=S256", url);
    }

    [Fact]
    public async Task TokenExchangeAndProfileMapping_SucceedsWithMock()
    {
        var secrets = new MockSecretStorage();
        var http = new HttpClient(new MockHttpMessageHandler(req =>
        {
            if (req.RequestUri!.AbsolutePath.Contains("/oauth/token/"))
            {
                return JsonResponse(@"{
                    ""access_token"": ""act.test_access_token_12345"",
                    ""expires_in"": 86400,
                    ""open_id"": ""_000_open_id_xyz"",
                    ""refresh_token"": ""rft.test_refresh_token_67890"",
                    ""refresh_expires_in"": 31536000,
                    ""scope"": ""user.info.basic"",
                    ""token_type"": ""Bearer""
                }");
            }
            if (req.RequestUri.AbsolutePath.Contains("/user/info/"))
            {
                Assert.Equal("Bearer", req.Headers.Authorization?.Scheme);
                Assert.Equal("act.test_access_token_12345", req.Headers.Authorization?.Parameter);
                return JsonResponse(@"{
                    ""data"": {
                        ""user"": {
                            ""open_id"": ""_000_open_id_xyz"",
                            ""union_id"": ""_000_union_id_xyz"",
                            ""avatar_url"": ""https://p16-tiktok.example.com/avatar.jpg"",
                            ""display_name"": ""AravalsCreator""
                        }
                    },
                    ""error"": {
                        ""code"": ""ok"",
                        ""message"": """"
                    }
                }");
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }));

        var apiClient = new TikTokApiClient(http);
        var tokenRes = await apiClient.ExchangeCodeAsync("aw123", "sec123", "code123", "http://127.0.0.1:19455/callback/", "verifier123");
        Assert.Equal("act.test_access_token_12345", tokenRes.AccessToken);
        Assert.Equal("_000_open_id_xyz", tokenRes.OpenId);

        var user = await apiClient.GetUserInfoAsync(tokenRes.AccessToken);
        Assert.Equal("_000_open_id_xyz", user.OpenId);
        Assert.Equal("AravalsCreator", user.DisplayName);
        Assert.Equal("https://p16-tiktok.example.com/avatar.jpg", user.AvatarUrl);
    }

    [Fact]
    public async Task TokenRefresh_RotatesTokensAndUpdatesAccountState()
    {
        var secrets = new MockSecretStorage();
        secrets.Items["sec-ref"] = "my_client_secret";
        secrets.Items["rft-ref"] = "rft.old_refresh_token";

        var account = new TikTokAccount
        {
            Id = Guid.NewGuid(),
            DisplayName = "Streamer",
            OpenId = "open-123",
            Connected = true,
            State = PlatformAccountState.Connected,
            TokenReference = "act-ref",
            RefreshTokenReference = "rft-ref"
        };

        var settings = new TikTokOAuthSettings
        {
            ClientKey = "key123",
            ClientSecretReference = "sec-ref"
        };

        var http = new HttpClient(new MockHttpMessageHandler(req =>
        {
            return JsonResponse(@"{
                ""access_token"": ""act.new_access_token_99999"",
                ""expires_in"": 86400,
                ""refresh_token"": ""rft.new_refresh_token_88888"",
                ""refresh_expires_in"": 31536000,
                ""scope"": ""user.info.basic"",
                ""token_type"": ""Bearer""
            }");
        }));

        var service = new TikTokAccountService(new TikTokApiClient(http), secrets);
        await service.RefreshTokensAsync(account, settings);

        Assert.Equal("act.new_access_token_99999", secrets.Items["act-ref"]);
        Assert.Equal("rft.new_refresh_token_88888", secrets.Items["rft-ref"]);
        Assert.Equal(PlatformAccountState.Connected, account.State);
        Assert.True(account.TokenExpiryUtc > DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task DisconnectAccount_PurgesTokensFromSecureStorageAndSetsDisconnected()
    {
        var secrets = new MockSecretStorage();
        secrets.Items["act-ref"] = "act.token_value";
        secrets.Items["rft-ref"] = "rft.token_value";

        var account = new TikTokAccount
        {
            Id = Guid.NewGuid(),
            DisplayName = "Streamer",
            OpenId = "open-123",
            Connected = true,
            State = PlatformAccountState.Connected,
            TokenReference = "act-ref",
            RefreshTokenReference = "rft-ref"
        };

        var service = new TikTokAccountService(new TikTokApiClient(), secrets);
        await service.DisconnectAsync(account);

        Assert.False(account.Connected);
        Assert.Equal(PlatformAccountState.Disconnected, account.State);
        Assert.Null(account.TokenReference);
        Assert.Null(account.RefreshTokenReference);
        Assert.DoesNotContain("act-ref", secrets.Items.Keys);
        Assert.DoesNotContain("rft-ref", secrets.Items.Keys);
    }

    [Fact]
    public void AppLogSanitization_RedactsTikTokTokensSecretsAndCredentialedUrls()
    {
        var rawLog1 = "Connecting with access token act.A1B2C3D4E5F6G7H8I9J0K1L2M3 and refresh token rft.Z9Y8X7W6V5U4T3S2R1Q0P9O8N7";
        var sanitized1 = AppLog.Sanitize(rawLog1);
        Assert.DoesNotContain("act.A1B2C3D4E5F6G7H8I9J0K1L2M3", sanitized1);
        Assert.DoesNotContain("rft.Z9Y8X7W6V5U4T3S2R1Q0P9O8N7", sanitized1);
        Assert.Contains("[REDACTED]", sanitized1);

        var rawLog2 = "Sending request with client_secret=very_secret_tiktok_key_123456789 and code_verifier=abcdef1234567890abcdef1234567890";
        var sanitized2 = AppLog.Sanitize(rawLog2);
        Assert.DoesNotContain("very_secret_tiktok_key_123456789", sanitized2);
        Assert.DoesNotContain("abcdef1234567890abcdef1234567890", sanitized2);

        var rawLog3 = "ffmpeg -i pipe:0 -f flv rtmp://live.tiktok.com/app/stream_key_secret_12345";
        var sanitized3 = AppLog.Sanitize(rawLog3);
        Assert.DoesNotContain("stream_key_secret_12345", sanitized3);
        Assert.Contains("rtmp://live.tiktok.com/app/[REDACTED]", sanitized3);
    }

    [Fact]
    public void TikTokVerticalPreset_DefaultsToVertical1080x1920()
    {
        var profile = PlatformRegistry.Get(PlatformType.TikTok);
        Assert.NotNull(profile);
        Assert.True(profile.SupportsVertical);
        Assert.True(profile.SupportsHorizontal);
        Assert.False(profile.SupportsMultipleOutputs);

        var group = PlatformDestinationGroup.CreateFromProfile(profile);
        Assert.Equal(RoutingMode.Vertical, group.Routing);
        Assert.Equal(OutputMode.Vertical, group.Vertical.OutputMode);
        Assert.True(group.Vertical.FrameRate is 30 or 60);
    }

    [Fact]
    public void SettingsMigrationV6ToV7_InitializesTikTokAndPreservesManualRtmp()
    {
        var group = PlatformDestinationGroup.CreateFromProfile(PlatformRegistry.Get(PlatformType.TikTok));
        group.ConfigurationMode = ConfigurationMode.ManualRtmp;
        group.StreamKeyReference = "tiktok-manual-key-ref";
        group.ServerUrl = "rtmp://live.tiktok.com/app/";

        var settings = new AppSettings
        {
            SettingsSchemaVersion = 6,
            TikTokOAuth = null!,
            TikTokAccount = null,
            DestinationGroups = [group]
        };

        JsonSettingsService.MigrateSettings(settings);

        Assert.Equal(9, settings.SettingsSchemaVersion);
        Assert.NotNull(settings.TikTokOAuth);
        Assert.Equal("http://127.0.0.1:19455/callback/", settings.TikTokOAuth.RedirectUri);
        Assert.Equal(ConfigurationMode.ManualRtmp, group.ConfigurationMode);
        Assert.Equal("tiktok-manual-key-ref", group.StreamKeyReference);
        Assert.Equal("rtmp://live.tiktok.com/app/", group.ServerUrl);
    }

    [Fact]
    public async Task SettingsPersistence_StoresOnlyOpaqueReferences()
    {
        var settings = new AppSettings
        {
            SettingsSchemaVersion = 7,
            TikTokOAuth = new TikTokOAuthSettings
            {
                ClientKey = "key_pub",
                ClientSecretReference = "dpapi_sec_ref"
            },
            TikTokAccount = new TikTokAccount
            {
                Id = Guid.NewGuid(),
                OpenId = "open_id_123",
                DisplayName = "Creator",
                Connected = true,
                TokenReference = "dpapi_act_ref",
                RefreshTokenReference = "dpapi_rft_ref"
            }
        };

        var tempPath = Path.Combine(Path.GetTempPath(), $"aravals-tiktok-{Guid.NewGuid():N}.json");
        try
        {
            var service = new JsonSettingsService(tempPath);
            await service.SaveAsync(settings);

            var json = await File.ReadAllTextAsync(tempPath);
            Assert.Contains("dpapi_sec_ref", json);
            Assert.Contains("dpapi_act_ref", json);
            Assert.Contains("dpapi_rft_ref", json);
            Assert.DoesNotContain("act.", json);
            Assert.DoesNotContain("rft.", json);

            var loaded = await service.LoadAsync();
            Assert.Equal("key_pub", loaded.TikTokOAuth.ClientKey);
            Assert.Equal("dpapi_sec_ref", loaded.TikTokOAuth.ClientSecretReference);
            Assert.Equal("dpapi_act_ref", loaded.TikTokAccount?.TokenReference);
            Assert.Equal("dpapi_rft_ref", loaded.TikTokAccount?.RefreshTokenReference);
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    [Fact]
    public void ApiFailureIsolation_AccountErrorDoesNotAffectManualRtmpTransportConfig()
    {
        var account = new TikTokAccount
        {
            Id = Guid.NewGuid(),
            DisplayName = "Creator",
            Connected = true,
            State = PlatformAccountState.NeedsReauthentication
        };

        var group = PlatformDestinationGroup.CreateFromProfile(PlatformRegistry.Get(PlatformType.TikTok));
        group.ConfigurationMode = ConfigurationMode.ManualRtmp;
        group.ServerUrl = "rtmp://live.tiktok.com/app/";
        group.Vertical.StreamUrl = "rtmp://live.tiktok.com/app/";
        group.StreamKeyReference = "manual-key";

        // Even though API account state is NeedsReauthentication,
        // the RTMP stream configuration is completely intact and valid for transport.
        var validationError = DestinationValidation.Validate(group.Vertical, hasKey: true);
        Assert.Null(validationError);
        Assert.Equal(ConfigurationMode.ManualRtmp, group.ConfigurationMode);
    }
}
