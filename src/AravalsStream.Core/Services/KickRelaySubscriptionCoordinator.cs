using System.Threading.Channels;
using AravalsStream.Core.Settings;

namespace AravalsStream.Core.Services;

public sealed class KickRelaySubscriptionCoordinator : IAsyncDisposable
{
    private readonly WebhookRelayClient _relay;
    private readonly string _channel;
    private readonly Func<CancellationToken, Task<string>> _token;
    private readonly CancellationTokenSource _stop = new();
    private readonly Channel<bool> _requests = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly Task _worker;
    private readonly Task _timer;
    private bool _authorizationRejected;
    public event Action<KickRelaySubscriptionStatus>? StatusChanged;
    public KickRelaySubscriptionCoordinator(WebhookRelayClient relay, string channel, Func<CancellationToken, Task<string>> token)
    {
        _relay = relay; _channel = channel; _token = token; relay.StateChanged += OnRelayState;
        _worker = Task.Run(RunAsync); _timer = Task.Run(TickAsync);
        if (relay.State == RelayConnectionState.Connected) RequestCheck();
    }
    public void RequestCheck() { _authorizationRejected = false; _requests.Writer.TryWrite(false); }
    public void RequestDelete() { _requests.Writer.TryWrite(true); }
    private void OnRelayState(RelayConnectionState value)
    { if (value == RelayConnectionState.Connected && !_authorizationRejected) _requests.Writer.TryWrite(false); }
    private async Task TickAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(15));
        try { while (await timer.WaitForNextTickAsync(_stop.Token)) if (!_authorizationRejected) _requests.Writer.TryWrite(false); }
        catch (OperationCanceledException) { }
    }
    private async Task RunAsync()
    {
        try
        {
            await foreach (var delete in _requests.Reader.ReadAllAsync(_stop.Token))
            {
                if (_relay.State != RelayConnectionState.Connected) continue;
                try
                {
                    StatusChanged?.Invoke(new(_channel, RelaySubscriptionState.Checking, null, null, "", [], []));
                    string token;
                    try { token = await _token(_stop.Token); }
                    catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
                    catch
                    {
                        _authorizationRejected = true;
                        StatusChanged?.Invoke(new(_channel, RelaySubscriptionState.ReauthorizationRequired, null,
                            "Kick authorization is unavailable. Reconnect Kick, then retry.", "", [], []));
                        continue;
                    }
                    var status = await _relay.ManageKickSubscriptionsAsync(_channel, token, delete, _stop.Token);
                    _authorizationRejected = status.State == RelaySubscriptionState.ReauthorizationRequired;
                    StatusChanged?.Invoke(status);
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
                catch
                {
                    // Transport failure remains independent of media; reconnect or the bounded check interval retries it.
                    StatusChanged?.Invoke(new(_channel, RelaySubscriptionState.Error, null,
                        "Subscription check failed. Check Relay connectivity and reconnect Kick, then retry.", "", [], []));
                }
            }
        }
        catch (OperationCanceledException) { }
    }
    public async ValueTask DisposeAsync()
    { _relay.StateChanged -= OnRelayState; _stop.Cancel(); try { await Task.WhenAll(_worker, _timer); } catch (OperationCanceledException) { } _stop.Dispose(); }
}
