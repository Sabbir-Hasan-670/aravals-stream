using System.Net;
using System.Text;
using System.Text.Json;
using AravalsStream.Core.Accounts;
using AravalsStream.Core.Interfaces;
using AravalsStream.Core.Models;
using AravalsStream.Core.Platforms;
using AravalsStream.Core.Services;
using AravalsStream.Core.Settings;
using AravalsStream.Core.Twitch;
using Xunit;

namespace AravalsStream.Tests;

public sealed class TwitchIntegrationTests
{
    private sealed class Secrets : ISecretStorage
    {
        public Dictionary<string, string> Items { get; } = [];
        public Task StoreAsync(string key, string secret, CancellationToken cancellationToken = default)
        { Items[key] = secret; return Task.CompletedTask; }
        public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult(Items.GetValueOrDefault(key));
        public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
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
    public async Task ManualTwitchDestinationSurvivesMigration()
    {
        var group = PlatformDestinationGroup.CreateFromProfile(PlatformRegistry.Get(PlatformType.Twitch));
        group.ConfigurationMode = ConfigurationMode.ManualRtmp;
        group.StreamKeyReference = "manual-secret-ref";
        var settings = new AppSettings { DestinationGroups = [group] };
        JsonSettingsService.MigrateSettings(settings);
        Assert.Equal(ConfigurationMode.ManualRtmp, settings.DestinationGroups[0].ConfigurationMode);
        Assert.Equal("manual-secret-ref", settings.DestinationGroups[0].StreamKeyReference);
        var path = Path.Combine(Path.GetTempPath(), $"aravals-twitch-test-{Guid.NewGuid():N}.json");
        try
        {
            var service = new JsonSettingsService(path);
            await service.SaveAsync(settings);
            var loaded = await service.LoadAsync();
            Assert.Equal(ConfigurationMode.ManualRtmp, loaded.DestinationGroups[0].ConfigurationMode);
            Assert.DoesNotContain("access_token", await File.ReadAllTextAsync(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void NativeMetadataAndAccountReferenceCopyWithoutTokens()
    {
        var group = new PlatformDestinationGroup { ConfigurationMode = ConfigurationMode.NativeApi,
            TwitchGameId = "509658", TwitchGameName = "Just Chatting", TwitchTags = ["English"],
            TwitchIngestName = "Auto", BoundAccountId = "account-id" };
        var copy = group.Copy();
        copy.TwitchTags.Add("Extra");
        Assert.Single(group.TwitchTags);
        Assert.Equal("509658", copy.TwitchGameId);
        Assert.Equal("account-id", copy.BoundAccountId);
        Assert.DoesNotContain("AccessToken", JsonSerializer.Serialize(new AppSettings { TwitchAccount = new TwitchAccount { TokenReference = "opaque-ref" } }));
    }

    [Fact]
    public async Task OAuthDeviceStartAndRefreshUsePublicClientAndSecretReference()
    {
        var calls = new List<string>();
        var http = new HttpClient(new Handler(request =>
        {
            calls.Add(request.RequestUri!.AbsolutePath);
            return request.RequestUri.AbsolutePath.EndsWith("/device")
                ? Json("{\"device_code\":\"device\",\"user_code\":\"ABCD\",\"verification_uri\":\"https://www.twitch.tv/activate\",\"expires_in\":300,\"interval\":1}")
                : request.RequestUri.AbsolutePath.EndsWith("/validate")
                    ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
                    : Json("{\"access_token\":\"new-access\",\"refresh_token\":\"new-refresh\",\"expires_in\":3600}");
        }));
        var secrets = new Secrets();
        var oauth = new TwitchOAuthClient(new TwitchOAuthSettings { ClientId = "public-client" }, secrets, http);
        var device = await oauth.BeginDeviceAuthorizationAsync();
        Assert.Equal("ABCD", device.UserCode);
        await secrets.StoreAsync("ref", JsonSerializer.Serialize(new OAuthTokenData
        { AccessToken = "old-access", RefreshToken = "old-refresh", ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1) }));
        var account = new TwitchAccount { Connected = true, TokenReference = "ref" };
        Assert.Equal("new-access", await oauth.GetValidAccessTokenAsync(account));
        Assert.Contains("new-refresh", secrets.Items["ref"]);
        Assert.Equal(PlatformAccountState.Connected, account.State);
        Assert.Contains("/oauth2/token", calls);
    }

    [Fact]
    public async Task ApiMapsIdentityCategoryIngestStatsAndChat()
    {
        var http = new HttpClient(new Handler(request => request.RequestUri!.AbsolutePath switch
        {
            "/helix/users" => Json("{\"data\":[{\"id\":\"1\",\"login\":\"abc\",\"display_name\":\"ABC\",\"profile_image_url\":\"image\"}]}"),
            "/helix/channels" => request.Method == HttpMethod.Patch ? Json("{}", HttpStatusCode.NoContent)
                : Json("{\"data\":[{\"title\":\"Live\",\"game_id\":\"2\",\"game_name\":\"Game\",\"broadcaster_language\":\"en\",\"tags\":[\"test\"]}]}"),
            "/helix/search/categories" => Json("{\"data\":[{\"id\":\"2\",\"name\":\"Game\"}]}"),
            "/helix/streams/key" => Json("{\"data\":[{\"stream_key\":\"sensitive-key\"}]}"),
            "/ingests" => Json("{\"ingests\":[{\"name\":\"Auto\",\"default\":true,\"url_template\":\"rtmp://example/app/{stream_key}\"}]}"),
            "/helix/streams" => Json("{\"data\":[{\"viewer_count\":48}]}"),
            "/helix/channels/followers" => Json("{}", HttpStatusCode.Forbidden),
            "/helix/chat/messages" => Json("{\"data\":[{\"is_sent\":true}]}"),
            _ => throw new InvalidOperationException(request.RequestUri.ToString())
        }));
        var api = new TwitchApiClient("client", http);
        Assert.Equal("ABC", (await api.GetUserAsync("token")).DisplayName);
        Assert.Equal("Game", (await api.GetChannelAsync("token", "1")).GameName);
        Assert.Equal("Game", (await api.SearchCategoriesAsync("token", "gam"))[0].Name);
        Assert.Equal("sensitive-key", await api.GetStreamKeyAsync("token", "1"));
        Assert.Equal("rtmp://example/app", (await api.GetIngestServersAsync())[0].ServerUrl);
        await api.UpdateChannelAsync("token", "1", "Live", "2", "en", ["test"]);
        var stats = await api.GetStatsAsync("token", "1");
        Assert.Equal(48, stats.Viewers);
        Assert.Null(stats.Followers);
        Assert.True(await api.SendChatMessageAsync("token", "1", "1", "hello"));
    }

    [Theory]
    [InlineData("channel.chat.message", "{\"message_id\":\"m\",\"chatter_user_name\":\"Alice\",\"chatter_user_id\":\"1\",\"message\":{\"text\":\"hi\"},\"badges\":[{\"set_id\":\"moderator\"}]}", ChatMessageType.StandardMessage)]
    [InlineData("channel.cheer", "{\"user_name\":\"Alice\",\"bits\":100,\"message\":\"yay\"}", ChatMessageType.Bits)]
    [InlineData("channel.subscribe", "{\"user_name\":\"Alice\",\"tier\":\"1000\"}", ChatMessageType.Subscription)]
    [InlineData("channel.subscription.gift", "{\"user_name\":\"Alice\",\"total\":2}", ChatMessageType.GiftSubscription)]
    [InlineData("channel.raid", "{\"from_broadcaster_user_name\":\"Alice\",\"viewers\":10}", ChatMessageType.Raid)]
    public void EventMappingUsesCommonChatModel(string type, string eventJson, ChatMessageType expected)
    {
        using var document = JsonDocument.Parse($"{{\"payload\":{{\"subscription\":{{\"type\":\"{type}\"}},\"event\":{eventJson}}}}}");
        var mapped = TwitchEventMapper.Map(document.RootElement);
        Assert.NotNull(mapped);
        Assert.Equal("Twitch", mapped.Platform);
        Assert.Equal(expected, mapped.MessageType);
        if (type == "channel.chat.message") Assert.True(mapped.IsModerator);
        if (type == "channel.cheer") Assert.Equal(100, mapped.Bits);
    }

    [Fact]
    public void LogSanitizerRedactsTwitchKeyAndBearer()
    {
        var text = AppLog.Sanitize("rtmp://example/app/privateKey token: privateToken bearer abc123");
        Assert.DoesNotContain("privateKey", text);
        Assert.DoesNotContain("privateToken", text);
        Assert.DoesNotContain("abc123", text);
    }

    [Fact]
    public async Task ApiRateLimitRetriesWithoutExposingResponseBody()
    {
        var calls = 0;
        var http = new HttpClient(new Handler(_ =>
        {
            calls++;
            if (calls == 1)
            {
                var limited = Json("{\"token\":\"must-stay-private\"}", HttpStatusCode.TooManyRequests);
                limited.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMilliseconds(1));
                return limited;
            }
            return Json("{\"data\":[{\"id\":\"1\",\"login\":\"a\",\"display_name\":\"A\"}]}");
        }));
        var user = await new TwitchApiClient("client", http).GetUserAsync("secret-token");
        Assert.Equal("A", user.DisplayName);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task ApiErrorDoesNotContainServerBodyOrBearer()
    {
        var http = new HttpClient(new Handler(_ => Json("{\"access_token\":\"sensitive-value\"}", HttpStatusCode.Forbidden)));
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => new TwitchApiClient("client", http).GetUserAsync("secret-token"));
        Assert.DoesNotContain("sensitive-value", error.Message);
        Assert.DoesNotContain("secret-token", error.Message);
    }
}
