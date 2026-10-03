using System.Net;
using System.Text;
using System.Text.Json;
using AravalsStream.Core.Accounts;
using AravalsStream.Core.Interfaces;
using AravalsStream.Core.Kick;
using AravalsStream.Core.Models;
using AravalsStream.Core.Platforms;
using AravalsStream.Core.Services;
using AravalsStream.Core.Settings;
using Xunit;

namespace AravalsStream.Tests;

public sealed class KickIntegrationTests
{
    private sealed class Secrets : ISecretStorage
    {
        public Dictionary<string, string> Items { get; } = [];
        public Task StoreAsync(string key, string value, CancellationToken ct = default)
        { Items[key] = value; return Task.CompletedTask; }
        public Task<string?> GetAsync(string key, CancellationToken ct = default) => Task.FromResult(Items.GetValueOrDefault(key));
        public Task RemoveAsync(string key, CancellationToken ct = default)
        { Items.Remove(key); return Task.CompletedTask; }
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(reply(request));
    }
    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task LegacyKickManualRtmpPersistsAndSecretsStayReferenced()
    {
        var group = PlatformDestinationGroup.CreateFromProfile(PlatformRegistry.Get(PlatformType.Kick));
        group.ConfigurationMode = ConfigurationMode.ManualRtmp;
        group.StreamKeyReference = "opaque-key-ref";
        var settings = new AppSettings { DestinationGroups = [group], KickAccount = new KickAccount
        { KickUserId = "123", Connected = true, TokenReference = "opaque-token-ref" } };
        JsonSettingsService.MigrateSettings(settings);
        Assert.Equal(ConfigurationMode.ManualRtmp, settings.DestinationGroups[0].ConfigurationMode);
        var path = Path.Combine(Path.GetTempPath(), $"aravals-kick-{Guid.NewGuid():N}.json");
        try
        {
            var service = new JsonSettingsService(path);
            await service.SaveAsync(settings);
            var loaded = await service.LoadAsync();
            Assert.Equal("opaque-key-ref", loaded.DestinationGroups[0].StreamKeyReference);
            Assert.Equal("opaque-token-ref", loaded.KickAccount?.TokenReference);
            Assert.DoesNotContain("access_token", await File.ReadAllTextAsync(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void NativeMetadataCopiesWithoutSharingTags()
    {
        var group = new PlatformDestinationGroup { ConfigurationMode = ConfigurationMode.NativeApi,
            KickCategoryId = 123, KickCategoryName = "Games", KickTags = ["one"], BoundAccountId = "account" };
        var copy = group.Copy();
        copy.KickTags.Add("two");
        Assert.Single(group.KickTags);
        Assert.Equal(123, copy.KickCategoryId);
        Assert.Equal("account", copy.BoundAccountId);
    }

    [Fact]
    public void OAuthUrlUsesPkceStateRegisteredLocalhostAndExactScopes()
    {
        var oauth = new KickOAuthClient(new KickOAuthSettings { ClientId = "client", ClientSecretReference = "ref" }, new Secrets());
        var verifier = KickOAuthClient.NewRandomValue();
        var url = oauth.BuildAuthorizationUrl(KickOAuthClient.CodeChallenge(verifier), "state-value");
        Assert.Contains("code_challenge_method=S256", url);
        Assert.Contains("state=state-value", url);
        Assert.Contains("localhost%3A8765", url);
        Assert.Contains("streamkey%3Aread", url);
        Assert.Contains("events%3Asubscribe", url);
    }

    [Fact]
    public async Task OAuthRefreshReplacesStoredTokenAndDisconnectDeletesIt()
    {
        var secrets = new Secrets();
        await secrets.StoreAsync("client-secret-ref", "developer-secret");
        await secrets.StoreAsync("token-ref", JsonSerializer.Serialize(new OAuthTokenData
        { AccessToken = "old-access", RefreshToken = "old-refresh", ExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(-10) }));
        var calls = 0;
        var http = new HttpClient(new Handler(request =>
        {
            calls++;
            return request.RequestUri!.AbsolutePath.EndsWith("/revoke") ? Json("OK") :
                Json("{\"access_token\":\"new-access\",\"refresh_token\":\"new-refresh\",\"expires_in\":3600}");
        }));
        var oauth = new KickOAuthClient(new KickOAuthSettings { ClientId = "client", ClientSecretReference = "client-secret-ref" }, secrets, http);
        var account = new KickAccount { Connected = true, TokenReference = "token-ref" };
        Assert.Equal("new-access", await oauth.GetValidAccessTokenAsync(account));
        Assert.Contains("new-refresh", secrets.Items["token-ref"]);
        Assert.DoesNotContain("old-refresh", secrets.Items["token-ref"]);
        Assert.Equal(PlatformAccountState.Connected, account.State);
        await oauth.DisconnectAsync(account);
        Assert.False(secrets.Items.ContainsKey("token-ref"));
        Assert.False(account.Connected);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task ApiMapsIdentityChannelCredentialCategoryViewersAndChat()
    {
        var http = new HttpClient(new Handler(request => request.RequestUri!.AbsolutePath switch
        {
            "/public/v1/users" => Json("{\"data\":[{\"user_id\":123,\"name\":\"Alice\",\"profile_picture\":\"image\"}]}"),
            "/public/v1/channels" => request.Method == HttpMethod.Patch ? Json("", HttpStatusCode.NoContent) :
                Json("{\"data\":[{\"broadcaster_user_id\":123,\"slug\":\"alice\",\"stream_title\":\"Live\",\"category\":{\"id\":7,\"name\":\"Games\"},\"stream\":{\"is_live\":true,\"viewer_count\":42,\"url\":\"rtmps://stream.kick.com/app\",\"key\":\"sensitive-key\",\"custom_tags\":[\"test\"]}}]}"),
            "/public/v1/categories" => Json("{\"data\":[{\"id\":7,\"name\":\"Games\"}]}"),
            "/public/v1/chat" => Json("{\"data\":{\"is_sent\":true,\"message_id\":\"abc\"}}"),
            _ => throw new InvalidOperationException(request.RequestUri.ToString())
        }));
        var api = new KickApiClient(http);
        Assert.Equal("123", (await api.GetUserAsync("token")).UserId);
        var channel = await api.GetChannelAsync("token", "123");
        Assert.Equal("sensitive-key", channel.StreamKey);
        Assert.Equal(42, channel.Viewers);
        Assert.Equal("Games", channel.CategoryName);
        Assert.Equal("Games", (await api.SearchCategoriesAsync("token", "Gam"))[0].Name);
        await api.UpdateChannelAsync("token", "Live", 7, ["test"]);
        Assert.True(await api.SendChatMessageAsync("token", 123, "hello"));
    }

    [Fact]
    public async Task ApiRateLimitRetriesAndErrorExcludesBody()
    {
        var calls = 0;
        var http = new HttpClient(new Handler(_ =>
        {
            calls++;
            if (calls == 1)
            {
                var response = Json("{\"secret\":\"hidden\"}", HttpStatusCode.TooManyRequests);
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMilliseconds(1));
                return response;
            }
            return Json("{\"data\":[{\"user_id\":1,\"name\":\"A\"}]}");
        }));
        Assert.Equal("A", (await new KickApiClient(http).GetUserAsync("token")).DisplayName);
        Assert.Equal(2, calls);
        var forbidden = new KickApiClient(new HttpClient(new Handler(_ => Json("{\"access_token\":\"hidden\"}", HttpStatusCode.Forbidden))));
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => forbidden.GetUserAsync("token"));
        Assert.DoesNotContain("hidden", error.Message);
        Assert.DoesNotContain("token", error.Message);
    }

    [Theory]
    [InlineData("chat.message.sent", ChatMessageType.StandardMessage)]
    [InlineData("channel.followed", ChatMessageType.Follow)]
    [InlineData("channel.subscription.new", ChatMessageType.Subscription)]
    [InlineData("channel.subscription.gifts", ChatMessageType.GiftSubscription)]
    public void OfficialWebhookEventsMapToCommonChatModel(string eventType, ChatMessageType type)
    {
        using var json = JsonDocument.Parse("{\"message_id\":\"m\",\"content\":\"hi\",\"created_at\":\"2026-01-01T00:00:00Z\",\"sender\":{\"user_id\":1,\"username\":\"Alice\",\"identity\":{\"badges\":[{\"type\":\"moderator\"}]}},\"follower\":{\"user_id\":2,\"username\":\"Bob\"},\"subscriber\":{\"user_id\":3,\"username\":\"Sue\"},\"gifter\":{\"user_id\":4,\"username\":\"Gif\"}}");
        var message = KickEventMapper.Map(eventType, json.RootElement);
        Assert.NotNull(message);
        Assert.Equal("Kick", message.Platform);
        Assert.Equal(type, message.MessageType);
        if (type == ChatMessageType.StandardMessage) Assert.True(message.IsModerator);
    }

    [Fact]
    public void SanitizerRedactsKickIngestAndClientSecret()
    {
        var text = AppLog.Sanitize("rtmps://stream.kick.com/app/privatekey secret: private-client-value");
        Assert.DoesNotContain("privatekey", text);
        Assert.DoesNotContain("private-client-value", text);
    }

    [Fact]
    public async Task ChatProviderSendsOfficiallyButDoesNotClaimReceiveConnection()
    {
        var secrets = new Secrets();
        await secrets.StoreAsync("token-ref", JsonSerializer.Serialize(new OAuthTokenData
        { AccessToken = "access", ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(1) }));
        var account = new KickAccount { KickUserId = "123", Connected = true, TokenReference = "token-ref",
            LastValidatedUtc = DateTimeOffset.UtcNow };
        var api = new KickApiClient(new HttpClient(new Handler(request => request.RequestUri!.AbsolutePath == "/public/v1/chat"
            ? Json("{\"data\":{\"is_sent\":true}}") : throw new InvalidOperationException())));
        var provider = new KickChatProvider(account,
            new KickOAuthClient(new KickOAuthSettings { ClientId = "client", ClientSecretReference = "ref" }, secrets), api);
        await provider.StartAsync("123");
        Assert.True(provider.IsConnected);
        Assert.Contains("receive requires public webhook", provider.StatusMessage);
        Assert.True(await provider.SendMessageAsync("hello"));
        await provider.StopAsync();
        Assert.False(provider.IsConnected);
    }
}
