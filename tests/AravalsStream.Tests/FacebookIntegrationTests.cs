using System.Net;
using System.Text;
using System.Text.Json;
using AravalsStream.Core.Accounts;
using AravalsStream.Core.Facebook;
using AravalsStream.Core.Interfaces;
using AravalsStream.Core.Models;
using AravalsStream.Core.Platforms;
using AravalsStream.Core.Services;
using AravalsStream.Core.Settings;
using Xunit;

namespace AravalsStream.Tests;

public sealed class FacebookIntegrationTests
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
    public void CurrentCapabilitiesDoNotPromiseDesktopLoginOrViewerStats()
    {
        Assert.Equal("v26.0", FacebookCapabilitySet.GraphVersion);
        Assert.Equal(FacebookCapabilityStatus.UnsupportedByCurrentMetaApi, FacebookCapabilitySet.Current.NativeDesktopLogin);
        Assert.Equal(FacebookCapabilityStatus.RequiresAppReview, FacebookCapabilitySet.Current.PageLiveCreation);
        Assert.Equal(FacebookCapabilityStatus.UnsupportedByCurrentMetaApi, FacebookCapabilitySet.Current.ViewerStats);
        Assert.True(FacebookCapabilitySet.Current.RequiresPageAccessToken);
    }

    [Fact]
    public async Task TokenImportSeparatesUserAndPageTokensAndDisconnectDeletesBoth()
    {
        var secrets = new Secrets();
        var graph = new FacebookGraphClient(new HttpClient(new Handler(request =>
        {
            Assert.Equal("v26.0", request.RequestUri!.Segments[1].TrimEnd('/'));
            Assert.Equal("user-token", request.Headers.Authorization?.Parameter);
            return request.RequestUri.AbsolutePath.EndsWith("/me")
                ? Json("{\"id\":\"u1\",\"name\":\"Alice\"}")
                : Json("{\"data\":[{\"id\":\"p1\",\"name\":\"Page One\",\"access_token\":\"page-token\",\"tasks\":[\"CREATE_CONTENT\"]}]}");
        })));
        var service = new FacebookAccountService(graph, secrets);
        var account = await service.ImportUserTokenAsync("user-token");
        Assert.Equal("Alice", account.DisplayName);
        Assert.Equal("p1", account.SelectedPageId);
        Assert.NotEqual(account.TokenReference, account.Pages[0].PageTokenReference);
        Assert.Equal("page-token", await service.GetPageTokenAsync(account, "p1"));
        Assert.Contains("CREATE_CONTENT", account.Pages[0].Tasks);
        Assert.Equal(FacebookPermissionStatus.Granted, FacebookPermissionDiagnostics.For(account)[0].Status);
        await service.DisconnectAsync(account);
        Assert.Empty(secrets.Items);
        Assert.False(account.Connected);
    }

    [Fact]
    public void LegacyManualFacebookDestinationAndReferencesSurviveMigration()
    {
        var group = PlatformDestinationGroup.CreateFromProfile(PlatformRegistry.Get(PlatformType.Facebook));
        group.ConfigurationMode = ConfigurationMode.ManualRtmp;
        group.StreamKeyReference = "manual-ref";
        var settings = new AppSettings { SettingsSchemaVersion = 5, DestinationGroups = [group] };
        JsonSettingsService.MigrateSettings(settings);
        Assert.Equal(9, settings.SettingsSchemaVersion);
        Assert.Equal(ConfigurationMode.ManualRtmp, group.ConfigurationMode);
        Assert.Equal("manual-ref", group.StreamKeyReference);
    }

    [Fact]
    public async Task SettingsPersistOnlyOpaqueFacebookSecretReferences()
    {
        var settings = new AppSettings { FacebookAccount = new FacebookAccount
        {
            FacebookUserId = "user1", Connected = true, TokenReference = "user-token-ref",
            Pages = [new FacebookPageIdentity { PageId = "page1", PageName = "Page",
                PageTokenReference = "page-token-ref" }]
        } };
        var path = Path.Combine(Path.GetTempPath(), $"aravals-facebook-{Guid.NewGuid():N}.json");
        try
        {
            await new JsonSettingsService(path).SaveAsync(settings);
            var raw = await File.ReadAllTextAsync(path);
            Assert.Contains("page-token-ref", raw);
            Assert.DoesNotContain("EAAsecret", raw);
            var loaded = await new JsonSettingsService(path).LoadAsync();
            Assert.Equal("page-token-ref", loaded.FacebookAccount?.Pages[0].PageTokenReference);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task OfficialPageLiveCreationUsesPageTokenAndNeverPlacesItInUrl()
    {
        var graph = new FacebookGraphClient(new HttpClient(new Handler(request =>
        {
            Assert.Equal("page-token", request.Headers.Authorization?.Parameter);
            Assert.DoesNotContain("page-token", request.RequestUri!.ToString());
            Assert.EndsWith("/p1/live_videos", request.RequestUri.AbsolutePath);
            Assert.Contains("status=LIVE_NOW", request.Content!.ReadAsStringAsync().Result);
            return Json("{\"id\":\"live1\",\"secure_stream_url\":\"rtmps://live-api-s.facebook.com:443/rtmp/privatekey\"}");
        })));
        var live = await graph.CreatePageLiveAsync("p1", "page-token", "Test", "Description");
        Assert.Equal("live1", live.Id);
        Assert.Contains("privatekey", live.SecureStreamUrl);
    }

    [Fact]
    public async Task CommentsMapAndPostingUsesOfficialVideoEdge()
    {
        var graph = new FacebookGraphClient(new HttpClient(new Handler(request =>
            request.Method == HttpMethod.Get
                ? Json("{\"data\":[{\"id\":\"c1\",\"message\":\"Hello\",\"from\":{\"id\":\"u1\",\"name\":\"Bob\"},\"created_time\":\"2026-09-01T00:00:00Z\"}]}")
                : Json("{\"id\":\"c2\"}"))));
        var comments = await graph.GetLiveCommentsAsync("live1", "page-token");
        Assert.Single(comments);
        Assert.Equal("Facebook", comments[0].Platform);
        Assert.Equal("Bob", comments[0].AuthorName);
        Assert.True(await graph.SendLiveCommentAsync("live1", "page-token", "Reply"));
    }

    [Fact]
    public async Task CommentsPollingDeduplicatesAndFailureDoesNotStopProvider()
    {
        var calls = 0;
        var graph = new FacebookGraphClient(new HttpClient(new Handler(_ =>
        {
            calls++;
            if (calls == 2) return Json("{\"error\":{\"code\":2}}", HttpStatusCode.ServiceUnavailable);
            return Json(calls == 1
                ? "{\"data\":[{\"id\":\"old\",\"message\":\"Old\"}]}"
                : "{\"data\":[{\"id\":\"new\",\"message\":\"New\"},{\"id\":\"old\",\"message\":\"Old\"}]}");
        })));
        await using var provider = new FacebookChatProvider(graph, _ => Task.FromResult("page-token"),
            TimeSpan.FromMilliseconds(20));
        var received = new TaskCompletionSource<ChatMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        provider.MessageReceived += message => received.TrySetResult(message);
        await provider.StartAsync("live1");
        var comment = await received.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("new", comment.Id);
        Assert.True(calls >= 3);
        Assert.True(provider.IsConnected);
        await provider.StopAsync();
    }

    [Fact]
    public async Task RateLimitRetriesAndErrorBodyIsNotExposed()
    {
        var calls = 0;
        var graph = new FacebookGraphClient(new HttpClient(new Handler(_ =>
        {
            calls++;
            if (calls == 1)
            {
                var limited = Json("{}", HttpStatusCode.TooManyRequests);
                limited.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMilliseconds(1));
                return limited;
            }
            return Json("{\"id\":\"u1\",\"name\":\"Alice\"}");
        })));
        Assert.Equal("u1", (await graph.GetUserAsync("token")).Id);
        Assert.Equal(2, calls);
        var denied = new FacebookGraphClient(new HttpClient(new Handler(_ =>
            Json("{\"error\":{\"code\":200,\"message\":\"private-token\"}}", HttpStatusCode.Forbidden))));
        var error = await Assert.ThrowsAsync<FacebookGraphException>(() => denied.GetUserAsync("token"));
        Assert.Contains("code 200", error.Message);
        Assert.DoesNotContain("private-token", error.Message);
    }

    [Fact]
    public void SanitizerRedactsMetaTokenAndIngestKey()
    {
        var secret = "EAA123456789012345678901234567890";
        var value = AppLog.Sanitize($"{secret} rtmps://live-api-s.facebook.com:443/rtmp/privatekey");
        Assert.DoesNotContain(secret, value);
        Assert.DoesNotContain("privatekey", value);
    }

    [Fact]
    public void FacebookIngestSeparatesCredentialFromPersistedServer()
    {
        var (server, key) = FacebookIngest.Split("rtmps://live-api-s.facebook.com:443/rtmp/privatekey");
        Assert.Equal("rtmps://live-api-s.facebook.com:443/rtmp", server);
        Assert.Equal("privatekey", key);
        Assert.DoesNotContain("privatekey", server);
        Assert.Throws<InvalidOperationException>(() => FacebookIngest.Split("rtmps://example.com/rtmp/privatekey"));
        Assert.Throws<InvalidOperationException>(() => FacebookIngest.Split("rtmps://live-api-s.facebook.com:443/rtmp/extra/privatekey"));
    }
}
