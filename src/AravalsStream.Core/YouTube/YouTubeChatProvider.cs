using System.Collections.Concurrent;
using AravalsStream.Core.Accounts;
using AravalsStream.Core.Chat;
using AravalsStream.Core.Models;
using AravalsStream.Core.Services;
using AravalsStream.Core.YouTube.Models;

namespace AravalsStream.Core.YouTube;

public sealed class YouTubeChatProvider : IChatProvider
{
    private readonly YouTubeAccount _account;
    private readonly GoogleOAuthClient _oauthClient;
    private readonly YouTubeApiClient _apiClient;

    private readonly object _gate = new();
    private readonly HashSet<string> _seenMessageIds = new();
    private readonly Queue<string> _messageIdQueue = new();
    private const int MaxSeenHistory = 1000;

    private CancellationTokenSource? _cts;
    private Task? _pollTask;
    private bool _isConnected;
    private string? _liveChatId;
    private string? _nextPageToken;
    private DateTimeOffset _lastSendTime = DateTimeOffset.MinValue;
    private readonly TimeSpan _minSendInterval = TimeSpan.FromSeconds(1);

    public string Platform => "YouTube";
    public bool IsConnected
    {
        get { lock (_gate) return _isConnected; }
        private set
        {
            lock (_gate) _isConnected = value;
            StatusChanged?.Invoke(value ? "Connected" : "Disconnected");
        }
    }

    public string? StatusMessage { get; private set; }

    public event Action<ChatMessage>? MessageReceived;
    public event Action<string>? StatusChanged;

    public YouTubeChatProvider(
        YouTubeAccount account,
        GoogleOAuthClient oauthClient,
        YouTubeApiClient? apiClient = null)
    {
        _account = account;
        _oauthClient = oauthClient;
        _apiClient = apiClient ?? new YouTubeApiClient();
    }

    public Task StartAsync(string liveChatId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_pollTask != null && !_pollTask.IsCompleted)
                return Task.CompletedTask;

            _liveChatId = liveChatId;
            _cts = new CancellationTokenSource();
            _pollTask = Task.Run(() => PollLoopAsync(_cts.Token), _cts.Token);
        }
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        CancellationTokenSource? cts;
        Task? task;
        lock (_gate)
        {
            cts = _cts;
            task = _pollTask;
            _cts = null;
            _pollTask = null;
        }

        if (cts != null)
        {
            cts.Cancel();
            if (task != null)
            {
                try { await task; } catch (OperationCanceledException) { } catch { }
            }
            cts.Dispose();
        }

        IsConnected = false;
        StatusMessage = "Chat stopped";
    }

    public async Task<bool> SendMessageAsync(string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrEmpty(_liveChatId))
            return false;

        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            if (now - _lastSendTime < _minSendInterval)
                return false; // Rate limit guard
            _lastSendTime = now;
        }

        try
        {
            var token = await _oauthClient.GetValidAccessTokenAsync(_account, ct);
            var success = await _apiClient.SendLiveChatMessageAsync(token, _liveChatId, text.Trim(), ct);
            if (success)
            {
                AppLog.Write("YouTubeChat", $"Sent chat message to {_liveChatId}: {text.Trim()}");
            }
            return success;
        }
        catch (Exception ex)
        {
            AppLog.Write("YouTubeChat", $"Failed to send chat message: {ex.Message}");
            return false;
        }
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        AppLog.Write("YouTubeChat", $"Starting YouTube chat poller for chat ID: {_liveChatId}");
        var backoffDelay = TimeSpan.FromSeconds(3);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var token = await _oauthClient.GetValidAccessTokenAsync(_account, ct);
                var response = await _apiClient.GetLiveChatMessagesAsync(token, _liveChatId!, _nextPageToken, ct);

                if (response != null)
                {
                    IsConnected = true;
                    StatusMessage = "Connected";
                    _nextPageToken = response.NextPageToken;

                    if (response.Items != null && response.Items.Count > 0)
                    {
                        foreach (var item in response.Items)
                        {
                            if (RecordMessageId(item.Id))
                            {
                                var msg = MapToChatMessage(item);
                                MessageReceived?.Invoke(msg);
                            }
                        }
                    }

                    // Respect API rate limiting interval
                    var intervalMs = Math.Clamp(response.PollingIntervalMillis, 2000, 15000);
                    backoffDelay = TimeSpan.FromMilliseconds(intervalMs);
                }
                else
                {
                    // Response null: possible transient error
                    IsConnected = false;
                    StatusMessage = "Reconnecting...";
                    backoffDelay = TimeSpan.FromSeconds(Math.Min(backoffDelay.TotalSeconds * 1.5, 30));
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                AppLog.Write("YouTubeChat", $"Chat poll error (isolated from stream): {ex.Message}");
                IsConnected = false;
                StatusMessage = "Connection error. Retrying...";
                backoffDelay = TimeSpan.FromSeconds(Math.Min(backoffDelay.TotalSeconds * 1.5, 30));
            }

            try
            {
                await Task.Delay(backoffDelay, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        IsConnected = false;
        StatusMessage = "Offline";
    }

    private bool RecordMessageId(string id)
    {
        lock (_gate)
        {
            if (_seenMessageIds.Contains(id)) return false;
            _seenMessageIds.Add(id);
            _messageIdQueue.Enqueue(id);
            if (_messageIdQueue.Count > MaxSeenHistory)
            {
                var oldest = _messageIdQueue.Dequeue();
                _seenMessageIds.Remove(oldest);
            }
            return true;
        }
    }

    public static ChatMessage MapToChatMessage(YouTubeLiveChatMessageItem item)
    {
        var type = ChatMessageType.StandardMessage;
        string text = item.Snippet?.DisplayMessage ?? item.Snippet?.TextMessageDetails?.MessageText ?? string.Empty;
        string? superAmt = null;
        string? superComment = null;

        if (item.Snippet?.Type == "superChatEvent")
        {
            type = ChatMessageType.SuperChat;
            superAmt = item.Snippet.SuperChatDetails?.AmountDisplayString;
            superComment = item.Snippet.SuperChatDetails?.UserComment;
            text = $"[Super Chat {superAmt}] {superComment ?? text}";
        }
        else if (item.Snippet?.Type == "superStickerEvent")
        {
            type = ChatMessageType.SuperSticker;
            text = "[Super Sticker]";
        }
        else if (item.Snippet?.Type == "newSponsorEvent")
        {
            type = ChatMessageType.Membership;
            text = "[New Member Joined!]";
        }

        return new ChatMessage
        {
            Id = item.Id,
            Platform = "YouTube",
            AuthorId = item.AuthorDetails?.ChannelId ?? string.Empty,
            AuthorName = item.AuthorDetails?.DisplayName ?? "Anonymous",
            AuthorAvatar = item.AuthorDetails?.ProfileImageUrl,
            Text = text,
            Timestamp = item.Snippet?.PublishedAt ?? DateTimeOffset.UtcNow,
            IsOwner = item.AuthorDetails?.IsChatOwner ?? false,
            IsModerator = item.AuthorDetails?.IsChatModerator ?? false,
            IsMember = item.AuthorDetails?.IsChatSponsor ?? false,
            IsVerified = item.AuthorDetails?.IsVerified ?? false,
            MessageType = type,
            SuperChatAmount = superAmt,
            SuperChatComment = superComment
        };
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }
}
