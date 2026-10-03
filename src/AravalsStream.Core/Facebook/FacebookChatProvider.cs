using AravalsStream.Core.Chat;
using AravalsStream.Core.Models;

namespace AravalsStream.Core.Facebook;

public sealed class FacebookChatProvider : IChatProvider
{
    private readonly FacebookGraphClient _graph;
    private readonly Func<CancellationToken, Task<string>> _pageToken;
    private readonly TimeSpan _pollInterval;
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private CancellationTokenSource? _stop;
    private Task? _pollTask;
    private string? _liveId;
    public string Platform => "Facebook";
    public bool IsConnected { get; private set; }
    public string? StatusMessage { get; private set; } = "Offline";
    public event Action<ChatMessage>? MessageReceived;
    public event Action<string>? StatusChanged;

    public FacebookChatProvider(FacebookGraphClient graph, Func<CancellationToken, Task<string>> pageToken,
        TimeSpan? pollInterval = null)
    { _graph = graph; _pageToken = pageToken; _pollInterval = pollInterval ?? TimeSpan.FromSeconds(10); }

    public Task StartAsync(string liveChatId, CancellationToken ct = default)
    {
        if (_pollTask != null) return Task.CompletedTask;
        if (string.IsNullOrWhiteSpace(liveChatId)) throw new ArgumentException("Facebook live ID is required.");
        _liveId = liveChatId;
        _stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Status("Connecting");
        _pollTask = PollAsync(_stop.Token);
        return Task.CompletedTask;
    }

    private async Task PollAsync(CancellationToken ct)
    {
        var first = true;
        var delay = _pollInterval;
        string? terminalStatus = null;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var comments = await _graph.GetLiveCommentsAsync(_liveId!, await _pageToken(ct), ct);
                foreach (var message in comments.Reverse())
                {
                    if (!_seen.Add(message.Id) || first) continue;
                    MessageReceived?.Invoke(message);
                }
                first = false;
                if (_seen.Count > 2000) _seen.Clear();
                IsConnected = true;
                Status("Polling");
                delay = _pollInterval;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (FacebookGraphException ex) when (ex.GraphCode is 190 or 200)
            {
                IsConnected = false;
                terminalStatus = ex.GraphCode == 190 ? "Reconnect account" : "Permission missing";
                Status(terminalStatus);
                break;
            }
            catch (Exception)
            {
                IsConnected = false;
                Status("Reconnecting");
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 60));
            }
            try { await Task.Delay(delay, ct); }
            catch (OperationCanceledException) { break; }
        }
        IsConnected = false;
        Status(terminalStatus ?? "Offline");
    }

    public async Task<bool> SendMessageAsync(string text, CancellationToken ct = default)
    {
        if (_liveId == null || !IsConnected) return false;
        return await _graph.SendLiveCommentAsync(_liveId, await _pageToken(ct), text, ct);
    }

    public async Task StopAsync()
    {
        if (_stop == null) return;
        _stop.Cancel();
        if (_pollTask != null) await _pollTask;
        _stop.Dispose(); _stop = null; _pollTask = null;
        _seen.Clear(); _liveId = null; IsConnected = false;
        Status("Offline");
    }

    private void Status(string value) { StatusMessage = value; StatusChanged?.Invoke(value); }
    public async ValueTask DisposeAsync() => await StopAsync();
}
