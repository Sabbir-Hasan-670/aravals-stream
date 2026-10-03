using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AravalsStream.Core.Accounts;
using AravalsStream.Core.Chat;
using AravalsStream.Core.Interfaces;
using AravalsStream.Core.Models;
using AravalsStream.Core.Platforms;
using AravalsStream.Core.Services;
using AravalsStream.Core.Settings;
using AravalsStream.Core.YouTube;
using AravalsStream.Core.YouTube.Models;
using Xunit;

namespace AravalsStream.Tests;

public sealed class YouTubeIntegrationTests
{
    private sealed class MockHttpMessageHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Handler { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.OK);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(Handler(request));
        }
    }

    private sealed class InMemorySecretStorage : ISecretStorage
    {
        private readonly Dictionary<string, string> _store = new();
        public Task StoreAsync(string key, string secret, CancellationToken cancellationToken = default)
        {
            _store[key] = secret;
            return Task.CompletedTask;
        }
        public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
        {
            _store.TryGetValue(key, out var val);
            return Task.FromResult<string?>(val);
        }
        public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
        {
            _store.Remove(key);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public void OAuthStateModel_GeneratesPkceVerifierAndChallenge()
    {
        var verifier = GoogleOAuthClient.GenerateCodeVerifier();
        Assert.NotNull(verifier);
        Assert.True(verifier.Length >= 43 && verifier.Length <= 128);

        var challenge = GoogleOAuthClient.GenerateCodeChallenge(verifier);
        Assert.NotNull(challenge);
        Assert.NotEqual(verifier, challenge);

        var state = GoogleOAuthClient.GenerateState();
        Assert.NotNull(state);
        Assert.NotEmpty(state);
    }

    [Fact]
    public async Task TokenSecureReference_StoresAndRetrievesSecurely()
    {
        var storage = new InMemorySecretStorage();
        var tokenRef = Guid.NewGuid().ToString("N");
        var tokenData = new OAuthTokenData
        {
            AccessToken = "test-access-token-12345",
            RefreshToken = "test-refresh-token-67890",
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(1),
            Scope = "https://www.googleapis.com/auth/youtube"
        };

        var json = JsonSerializer.Serialize(tokenData);
        await storage.StoreAsync(tokenRef, json);

        var retrievedRaw = await storage.GetAsync(tokenRef);
        Assert.NotNull(retrievedRaw);
        var retrieved = JsonSerializer.Deserialize<OAuthTokenData>(retrievedRaw);
        Assert.NotNull(retrieved);
        Assert.Equal("test-access-token-12345", retrieved.AccessToken);
        Assert.Equal("test-refresh-token-67890", retrieved.RefreshToken);
        Assert.False(retrieved.IsExpired());

        // Test Disconnect/Removal
        await storage.RemoveAsync(tokenRef);
        Assert.Null(await storage.GetAsync(tokenRef));
    }

    [Fact]
    public async Task TokenRefresh_RefreshesExpiredAccessToken()
    {
        var storage = new InMemorySecretStorage();
        var tokenRef = "token-ref-1";
        var expiredToken = new OAuthTokenData
        {
            AccessToken = "expired-token",
            RefreshToken = "valid-refresh-token",
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-10) // Expired
        };
        await storage.StoreAsync(tokenRef, JsonSerializer.Serialize(expiredToken));

        var account = new YouTubeAccount
        {
            Id = Guid.NewGuid(),
            ChannelId = "UC12345",
            ChannelTitle = "Test Channel",
            Connected = true,
            TokenReference = tokenRef
        };

        var handler = new MockHttpMessageHandler
        {
            Handler = req =>
            {
                Assert.Equal("https://oauth2.googleapis.com/token", req.RequestUri?.ToString());
                var resp = new
                {
                    access_token = "new-fresh-access-token",
                    expires_in = 3600,
                    token_type = "Bearer",
                    scope = "https://www.googleapis.com/auth/youtube"
                };
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(resp), Encoding.UTF8, "application/json")
                };
            }
        };

        var settings = new GoogleOAuthSettings { ClientId = "client-id", ClientSecret = "client-secret" };
        var client = new GoogleOAuthClient(settings, storage, new HttpClient(handler));

        var token = await client.GetValidAccessTokenAsync(account);
        Assert.Equal("new-fresh-access-token", token);

        // Verify updated in storage
        var updatedRaw = await storage.GetAsync(tokenRef);
        var updated = JsonSerializer.Deserialize<OAuthTokenData>(updatedRaw!);
        Assert.NotNull(updated);
        Assert.Equal("new-fresh-access-token", updated.AccessToken);
        Assert.Equal("valid-refresh-token", updated.RefreshToken);
        Assert.False(updated.IsExpired());
    }

    [Fact]
    public async Task ChannelMetadataMapping_MapsChannelsAndSubscriberCounts()
    {
        var channelJson = """
        {
          "items": [
            {
              "id": "UC_x5XG1OV2P6uZZ5FSM9Ttw",
              "snippet": {
                "title": "Google Developers",
                "thumbnails": {
                  "default": { "url": "https://example.com/avatar.jpg" }
                }
              },
              "statistics": {
                "viewCount": "192300000",
                "subscriberCount": "2250000",
                "hiddenSubscriberCount": false
              }
            }
          ]
        }
        """;

        var handler = new MockHttpMessageHandler
        {
            Handler = req =>
            {
                Assert.Contains("/channels?part=snippet,statistics&mine=true", req.RequestUri?.ToString());
                Assert.Equal("Bearer", req.Headers.Authorization?.Scheme);
                Assert.Equal("valid-token", req.Headers.Authorization?.Parameter);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(channelJson, Encoding.UTF8, "application/json")
                };
            }
        };

        var client = new YouTubeApiClient(new HttpClient(handler));
        var (channelId, title, thumb, subs, hidden) = await client.GetChannelIdentityAsync("valid-token");

        Assert.Equal("UC_x5XG1OV2P6uZZ5FSM9Ttw", channelId);
        Assert.Equal("Google Developers", title);
        Assert.Equal("https://example.com/avatar.jpg", thumb);
        Assert.Equal(2250000UL, subs);
        Assert.False(hidden);

        var account = new YouTubeAccount
        {
            ChannelTitle = title,
            SubscriberCount = subs,
            HiddenSubscriberCount = hidden
        };
        Assert.Equal("2.3M", account.FormattedSubscribers);
    }

    [Fact]
    public async Task HiddenSubscriberHandling_DisplaysDashForHiddenSubs()
    {
        var channelJson = """
        {
          "items": [
            {
              "id": "UC_hidden",
              "snippet": { "title": "Private Creator" },
              "statistics": {
                "hiddenSubscriberCount": true
              }
            }
          ]
        }
        """;

        var handler = new MockHttpMessageHandler
        {
            Handler = _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(channelJson, Encoding.UTF8, "application/json")
            }
        };

        var client = new YouTubeApiClient(new HttpClient(handler));
        var (channelId, title, thumb, subs, hidden) = await client.GetChannelIdentityAsync("valid-token");

        Assert.Equal("UC_hidden", channelId);
        Assert.True(hidden);
        Assert.Null(subs);

        var account = new YouTubeAccount
        {
            SubscriberCount = subs,
            HiddenSubscriberCount = hidden
        };
        Assert.Equal("—", account.FormattedSubscribers);
    }

    [Fact]
    public async Task BroadcastCreateRequest_BuildsValidPayloadAndParsesResponse()
    {
        string? capturedBody = null;
        var responseJson = """
        {
          "id": "broadcast_abc123",
          "snippet": {
            "title": "My Phase 11 Stream",
            "description": "Native integration test",
            "scheduledStartTime": "2026-09-24T12:00:00Z",
            "liveChatId": "chat_xyz789"
          },
          "status": {
            "lifeCycleStatus": "ready",
            "privacyStatus": "unlisted",
            "madeForKids": false
          },
          "contentDetails": {
            "enableDvr": true,
            "enableAutoStart": true,
            "enableAutoStop": false
          }
        }
        """;

        var handler = new MockHttpMessageHandler
        {
            Handler = req =>
            {
                Assert.Contains("/liveBroadcasts?part=snippet,status,contentDetails", req.RequestUri?.ToString());
                capturedBody = req.Content?.ReadAsStringAsync().Result;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
                };
            }
        };

        var client = new YouTubeApiClient(new HttpClient(handler));
        var item = await client.CreateBroadcastAsync(
            "test-token",
            "My Phase 11 Stream",
            "Native integration test",
            "unlisted",
            DateTimeOffset.Parse("2026-09-24T12:00:00Z"),
            madeForKids: false,
            enableAutoStart: true,
            enableAutoStop: false,
            enableDvr: true);

        Assert.NotNull(item);
        Assert.Equal("broadcast_abc123", item.Id);
        Assert.Equal("ready", item.Status?.LifeCycleStatus);
        Assert.Equal("chat_xyz789", item.Snippet?.LiveChatId);
        Assert.NotNull(capturedBody);
        Assert.Contains("\"selfDeclaredMadeForKids\":false", capturedBody);
        Assert.Contains("\"enableAutoStart\":true", capturedBody);
    }

    [Fact]
    public async Task BroadcastSelection_ListsAndFiltersBroadcasts()
    {
        var responseJson = """
        {
          "items": [
            {
              "id": "b1",
              "snippet": { "title": "Upcoming Stream" },
              "status": { "lifeCycleStatus": "ready" }
            },
            {
              "id": "b2",
              "snippet": { "title": "Live Stream Now" },
              "status": { "lifeCycleStatus": "live" }
            }
          ]
        }
        """;

        var handler = new MockHttpMessageHandler
        {
            Handler = req =>
            {
                Assert.Contains("liveBroadcasts", req.RequestUri?.ToString());
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
                };
            }
        };

        var client = new YouTubeApiClient(new HttpClient(handler));
        var list = await client.ListBroadcastsAsync("token", "all");

        Assert.Equal(2, list.Count);
        Assert.Equal("Upcoming Stream", list[0].Snippet?.Title);
        Assert.Equal("Live Stream Now", list[1].Snippet?.Title);
    }

    [Fact]
    public async Task StreamCreationAndBinding_CreatesLiveStreamAndBinds()
    {
        var streamJson = """
        {
          "id": "stream_999",
          "snippet": { "title": "1080p Stream" },
          "cdn": {
            "ingestionType": "rtmp",
            "ingestionInfo": {
              "streamName": "secret-stream-key-xyz",
              "ingestionAddress": "rtmp://a.rtmp.youtube.com/live2"
            },
            "resolution": "1080p",
            "frameRate": "60fps"
          }
        }
        """;

        var bindJson = """
        {
          "id": "broadcast_abc123",
          "contentDetails": {
            "boundStreamId": "stream_999"
          }
        }
        """;

        var handler = new MockHttpMessageHandler
        {
            Handler = req =>
            {
                var uri = req.RequestUri?.ToString() ?? "";
                if (uri.Contains("/liveStreams"))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(streamJson, Encoding.UTF8, "application/json")
                    };
                }
                if (uri.Contains("/liveBroadcasts/bind"))
                {
                    Assert.Contains("id=broadcast_abc123", uri);
                    Assert.Contains("streamId=stream_999", uri);
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(bindJson, Encoding.UTF8, "application/json")
                    };
                }
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
        };

        var client = new YouTubeApiClient(new HttpClient(handler));
        var stream = await client.CreateLiveStreamAsync("token", "1080p Stream", "1080p", "60fps");

        Assert.NotNull(stream);
        Assert.Equal("stream_999", stream.Id);
        Assert.Equal("secret-stream-key-xyz", stream.Cdn?.IngestionInfo?.StreamName);
        Assert.Equal("rtmp://a.rtmp.youtube.com/live2", stream.Cdn?.IngestionInfo?.IngestionAddress);

        var bound = await client.BindBroadcastAsync("token", "broadcast_abc123", stream.Id);
        Assert.NotNull(bound);
        Assert.Equal("broadcast_abc123", bound.Id);
    }

    [Fact]
    public void IngestSecretSanitization_SanitizesStreamKeyAndSecretsInLogs()
    {
        var sensitiveLog = "Connecting to rtmp://a.rtmp.youtube.com/live2 with key abcde-1234-5678-fghi. Authorization: Bearer ya29.a0AfH6SM...";
        var sanitized = AppLog.Sanitize(sensitiveLog);

        Assert.DoesNotContain("ya29.a0AfH6SM", sanitized);
        Assert.Contains("Bearer [REDACTED]", sanitized);
    }

    [Fact]
    public void ConfigurationMode_SupportsBothManualAndNativeApi()
    {
        var profile = PlatformRegistry.Get(PlatformType.YouTube);

        // Manual RTMP configuration
        var manualGroup = PlatformDestinationGroup.CreateFromProfile(profile);
        manualGroup.ConfigurationMode = ConfigurationMode.ManualRtmp;
        manualGroup.ServerUrl = "rtmp://a.rtmp.youtube.com/live2";
        manualGroup.StreamKeyReference = "manual-key-ref";
        Assert.Equal(ConfigurationMode.ManualRtmp, manualGroup.ConfigurationMode);

        // Native API configuration
        var nativeGroup = PlatformDestinationGroup.CreateFromProfile(profile);
        nativeGroup.ConfigurationMode = ConfigurationMode.NativeApi;
        nativeGroup.BroadcastId = "bcast-id-123";
        nativeGroup.StreamId = "stream-id-456";
        nativeGroup.LiveChatId = "chat-id-789";
        Assert.Equal(ConfigurationMode.NativeApi, nativeGroup.ConfigurationMode);
        Assert.Equal("bcast-id-123", nativeGroup.BroadcastId);
    }

    [Fact]
    public void ChatMessageMapping_MapsStandardChatMessage()
    {
        var rawItem = new YouTubeLiveChatMessageItem
        {
            Id = "msg_1",
            Snippet = new YouTubeChatMessageSnippet
            {
              DisplayMessage = "Hello from chat!",
              PublishedAt = DateTimeOffset.Parse("2026-09-24T14:30:00Z"),
              Type = "textMessageEvent"
            },
            AuthorDetails = new YouTubeChatAuthorDetails
            {
              ChannelId = "channel_user1",
              DisplayName = "StreamerFan",
              ProfileImageUrl = "https://example.com/fan.png",
              IsChatModerator = true
            }
        };

        var mapped = YouTubeChatProvider.MapToChatMessage(rawItem);

        Assert.Equal("msg_1", mapped.Id);
        Assert.Equal("YouTube", mapped.Platform);
        Assert.Equal("StreamerFan", mapped.AuthorName);
        Assert.Equal("Hello from chat!", mapped.Text);
        Assert.True(mapped.IsModerator);
        Assert.False(mapped.IsOwner);
        Assert.Equal("MOD", mapped.DisplayBadge);
        Assert.Equal(ChatMessageType.StandardMessage, mapped.MessageType);
    }

    [Fact]
    public void SpecialChatEventMapping_MapsSuperChatSuperStickerAndMembership()
    {
        // 1. Super Chat
        var superChat = new YouTubeLiveChatMessageItem
        {
            Id = "sc_1",
            Snippet = new YouTubeChatMessageSnippet
            {
                Type = "superChatEvent",
                SuperChatDetails = new YouTubeSuperChatDetails
                {
                    AmountDisplayString = "$25.00",
                    UserComment = "Keep up the great stream!"
                }
            },
            AuthorDetails = new YouTubeChatAuthorDetails
            {
                DisplayName = "GenerousSupporter",
                IsChatSponsor = true
            }
        };

        var mappedSc = YouTubeChatProvider.MapToChatMessage(superChat);
        Assert.Equal(ChatMessageType.SuperChat, mappedSc.MessageType);
        Assert.Equal("$25.00", mappedSc.SuperChatAmount);
        Assert.Equal("Keep up the great stream!", mappedSc.SuperChatComment);
        Assert.Contains("$25.00", mappedSc.Text);
        Assert.True(mappedSc.IsMember);

        // 2. Super Sticker
        var sticker = new YouTubeLiveChatMessageItem
        {
            Id = "ss_1",
            Snippet = new YouTubeChatMessageSnippet { Type = "superStickerEvent" },
            AuthorDetails = new YouTubeChatAuthorDetails { DisplayName = "StickerSender" }
        };
        var mappedSticker = YouTubeChatProvider.MapToChatMessage(sticker);
        Assert.Equal(ChatMessageType.SuperSticker, mappedSticker.MessageType);

        // 3. New Sponsor (Membership)
        var sponsor = new YouTubeLiveChatMessageItem
        {
            Id = "sp_1",
            Snippet = new YouTubeChatMessageSnippet { Type = "newSponsorEvent" },
            AuthorDetails = new YouTubeChatAuthorDetails { DisplayName = "NewMember", IsChatSponsor = true }
        };
        var mappedSponsor = YouTubeChatProvider.MapToChatMessage(sponsor);
        Assert.Equal(ChatMessageType.Membership, mappedSponsor.MessageType);
    }

    [Fact]
    public async Task ViewerStatMapping_ExtractsConcurrentViewersFromLiveStreamingDetails()
    {
        var videoJson = """
        {
          "items": [
            {
              "id": "vid_live123",
              "liveStreamingDetails": {
                "actualStartTime": "2026-09-24T10:00:00Z",
                "concurrentViewers": "1482",
                "activeLiveChatId": "active_chat_xyz"
              }
            }
          ]
        }
        """;

        var handler = new MockHttpMessageHandler
        {
            Handler = req =>
            {
                Assert.Contains("/videos?part=liveStreamingDetails&id=vid_live123", req.RequestUri?.ToString());
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(videoJson, Encoding.UTF8, "application/json")
                };
            }
        };

        var client = new YouTubeApiClient(new HttpClient(handler));
        var (viewers, activeChatId) = await client.GetVideoLiveDetailsAsync("token", "vid_live123");

        Assert.Equal(1482UL, viewers);
        Assert.Equal("active_chat_xyz", activeChatId);
    }

    [Fact]
    public async Task ChatReconnectAndErrorIsolation_DoesNotThrowUnhandledException()
    {
        int callCount = 0;
        var handler = new MockHttpMessageHandler
        {
            Handler = _ =>
            {
                callCount++;
                if (callCount == 1)
                {
                    return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                }
                var chatJson = """
                {
                  "pollingIntervalMillis": 2000,
                  "items": [
                    {
                      "id": "m1",
                      "snippet": { "displayMessage": "After reconnect" },
                      "authorDetails": { "displayName": "Viewer" }
                    }
                  ]
                }
                """;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(chatJson, Encoding.UTF8, "application/json")
                };
            }
        };

        var storage = new InMemorySecretStorage();
        var tokenRef = "token-test";
        await storage.StoreAsync(tokenRef, JsonSerializer.Serialize(new OAuthTokenData
        {
            AccessToken = "tok",
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(1)
        }));

        var account = new YouTubeAccount { Id = Guid.NewGuid(), TokenReference = tokenRef, Connected = true };
        var oauth = new GoogleOAuthClient(new GoogleOAuthSettings { ClientId = "id" }, storage, new HttpClient(handler));
        var apiClient = new YouTubeApiClient(new HttpClient(handler));

        var provider = new YouTubeChatProvider(account, oauth, apiClient);

        var received = new List<ChatMessage>();
        provider.MessageReceived += msg => received.Add(msg);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await provider.StartAsync("test_chat_id", cts.Token);

        await Task.Delay(2500);
        await provider.StopAsync();

        Assert.True(callCount >= 1);
    }
}
