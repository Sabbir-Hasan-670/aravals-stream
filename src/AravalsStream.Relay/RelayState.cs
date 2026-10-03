using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using AravalsStream.Core.Models;
using System.Net.WebSockets;
using AravalsStream.Core.Settings;

namespace AravalsStream.Relay;

public sealed class RelayState
{
    private readonly byte[] _signingKey;
    private readonly Dictionary<string, string[]> _enrollmentClaims = new(StringComparer.Ordinal);
    private readonly string _storagePath;
    private readonly object _storageGate = new();
    private readonly ConcurrentDictionary<string, Session> _sessions = new();
    private readonly ConcurrentDictionary<string, string> _routes = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _usedEnrollmentCodes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _dedupe = new();
    private readonly ConcurrentDictionary<string, Channel<RelayEvent>> _listeners = new();
    private readonly ConcurrentDictionary<string, WebSocket> _sockets = new();
    private readonly ConcurrentDictionary<string, KickRelaySubscriptionStatus> _kickSubscriptions = new();
    private int _connections;
    private long _dropped;

    public RelayState(IConfiguration configuration)
    {
        _signingKey = ReadKey(configuration["RELAY_TOKEN_SIGNING_KEY"]);
        LoadEnrollmentClaims(configuration["RELAY_ENROLLMENT_CREDENTIALS_JSON"]);
        _storagePath = configuration["RELAY_STORAGE_PATH"] ?? Path.Combine(AppContext.BaseDirectory, "relay-state.json");
        try
        {
            if (!File.Exists(_storagePath)) return;
            var stored = JsonSerializer.Deserialize<PersistedState>(File.ReadAllBytes(_storagePath));
            if (stored is null || stored.SchemaVersion is not (1 or 2)) throw new InvalidOperationException("Unsupported Relay persistence schema; state has not been changed.");
            foreach (var pair in stored.Sessions) _sessions[pair.Key] = new Session(pair.Value.RefreshHash, pair.Value.Generation);
            foreach (var pair in stored.Routes) _routes[pair.Key] = pair.Value;
            foreach (var hash in stored.UsedEnrollmentCodes ?? []) _usedEnrollmentCodes[hash] = 0;
            foreach (var pair in stored.KickSubscriptions) _kickSubscriptions[pair.Key] = pair.Value;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { throw new InvalidOperationException("Relay state storage could not be read.", ex); }
    }

    public int ConnectionCount => Volatile.Read(ref _connections);
    public long DroppedEvents => Interlocked.Read(ref _dropped);
    public string? EnrollmentError => _signingKey.Length < 32 || _enrollmentClaims.Count == 0
        ? "Token signing and channel-scoped enrollment credentials must be configured securely." : null;
    private static byte[] ReadKey(string? value)
    {
        if (RelayPreflight.SigningKeyError(value) is not null) return [];
        try { var bytes = Convert.FromBase64String(value!); return bytes.Length >= 32 ? bytes : []; }
        catch { return []; }
    }

    public (string InstallationId, string RefreshToken, string AccessToken)? Enroll(string? code, string[]? requestedChannels = null)
    {
        if (EnrollmentError is not null || string.IsNullOrEmpty(code)) return null;
        var codeHash = Hash(code);
        var claims = _enrollmentClaims.FirstOrDefault(x => FixedTimeHashEquals(x.Key, codeHash));
        if (claims.Key is null || (requestedChannels is not null && !SameChannels(claims.Value, requestedChannels)) ||
            !_usedEnrollmentCodes.TryAdd(codeHash, 0)) return null;
        var id = Guid.NewGuid().ToString("N"); var refresh = Token();
        var addedRoutes = new List<string>();
        foreach (var channel in claims.Value)
        {
            if (_routes.TryAdd(channel, id)) addedRoutes.Add(channel);
            else
            {
                foreach (var added in addedRoutes) _routes.TryRemove(added, out _);
                return null;
            }
        }
        _sessions[id] = new Session(Hash(refresh), 1);
        Persist();
        return (id, refresh, MakeAccess(id, 1));
    }

    public (string AccessToken, DateTimeOffset ExpiresAt)? Refresh(string id, string? refresh)
    {
        if (refresh is null || !_sessions.TryGetValue(id, out var session) ||
            !FixedTimeHashEquals(session.RefreshHash, Hash(refresh))) return null;
        var expiry = DateTimeOffset.UtcNow.AddMinutes(5);
        return (MakeAccess(id, session.Generation, expiry), expiry);
    }

    public string? ValidateAccess(string? token)
    {
        if (string.IsNullOrEmpty(token) || _signingKey.Length < 32) return null;
        var parts = token.Split('.'); if (parts.Length != 2) return null;
        byte[] supplied; byte[] body;
        try { body = Convert.FromBase64String(parts[0]); supplied = Convert.FromBase64String(parts[1]); }
        catch (FormatException) { return null; }
        var expected = HMACSHA256.HashData(_signingKey, body);
        if (supplied.Length != expected.Length || !CryptographicOperations.FixedTimeEquals(supplied, expected)) return null;
        try
        {
            var claim = JsonSerializer.Deserialize<AccessClaim>(body);
            if (claim is null || claim.Expires <= DateTimeOffset.UtcNow.ToUnixTimeSeconds() ||
                !_sessions.TryGetValue(claim.InstallationId, out var s) || s.Generation != claim.Generation) return null;
            return claim.InstallationId;
        }
        catch (JsonException) { return null; }
    }

    public bool Revoke(string installationId, string? refresh)
    {
        if (refresh is null || !_sessions.TryGetValue(installationId, out var session) ||
            !FixedTimeHashEquals(session.RefreshHash, Hash(refresh))) return false;
        _sessions.TryRemove(installationId, out _);
        foreach (var key in _routes.Where(x => x.Value == installationId).Select(x => x.Key).ToArray()) _routes.TryRemove(key, out _);
        if (_listeners.TryRemove(installationId, out var channel)) channel.Writer.TryComplete();
        if (_sockets.TryRemove(installationId, out var socket)) socket.Abort();
        Persist();
        return true;
    }

    public bool TryOwnChannel(string installationId, string provider, string channelId)
    {
        if (provider is not ("kick" or "facebook") || channelId.Length is 0 or > 200) return false;
        var route = provider + ":" + channelId;
        return _routes.TryGetValue(route, out var owner) && owner == installationId;
    }

    public KickRelaySubscriptionStatus? GetKickSubscription(string accessToken, string channelId)
    {
        var id = ValidateAccess(accessToken);
        if (id is null || !TryOwnChannel(id, "kick", channelId)) throw new UnauthorizedAccessException("Relay channel authorization required.");
        return _kickSubscriptions.TryGetValue(id + ":" + channelId, out var result) ? result : null;
    }

    public void SaveKickSubscription(string accessToken, KickRelaySubscriptionStatus status)
    {
        lock (_storageGate)
        {
            var id = ValidateAccess(accessToken);
            if (id is null || !TryOwnChannel(id, "kick", status.ChannelId)) throw new UnauthorizedAccessException("Relay channel authorization required.");
            _kickSubscriptions[id + ":" + status.ChannelId] = status;
            Persist();
        }
    }

    public bool IsDuplicate(string provider, string eventId, DateTimeOffset now)
    {
        foreach (var old in _dedupe.Where(x => now - x.Value > TimeSpan.FromHours(24)).Select(x => x.Key).ToArray()) _dedupe.TryRemove(old, out _);
        if (_dedupe.Count > 10000) foreach (var old in _dedupe.OrderBy(x => x.Value).Take(_dedupe.Count - 10000).Select(x => x.Key)) _dedupe.TryRemove(old, out _);
        return !_dedupe.TryAdd(provider + ":" + eventId, now);
    }

    public bool Deliver(RelayEvent item)
    {
        var route = item.Provider + ":" + item.ChannelId;
        if (!_routes.TryGetValue(route, out var owner) || !_listeners.TryGetValue(owner, out var queue)) return false;
        if (queue.Reader.Count >= 128) Interlocked.Increment(ref _dropped);
        if (queue.Writer.TryWrite(item with { InstallationId = owner })) return true;
        Interlocked.Increment(ref _dropped); return false;
    }

    public bool TryAttach(string installationId, out ChannelReader<RelayEvent>? reader) => TryAttach(installationId, null, out reader);

    public bool TryAttach(string installationId, WebSocket? socket, out ChannelReader<RelayEvent>? reader)
    {
        reader = null;
        while (true)
        {
            var count = Volatile.Read(ref _connections); if (count >= 100) return false;
            if (Interlocked.CompareExchange(ref _connections, count + 1, count) == count) break;
        }
        var queue = Channel.CreateBounded<RelayEvent>(new BoundedChannelOptions(128) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = false });
        if (!_listeners.TryAdd(installationId, queue)) { Interlocked.Decrement(ref _connections); return false; }
        if (socket is not null) _sockets[installationId] = socket;
        reader = queue.Reader; return true;
    }
    public void Detach(string installationId, WebSocket? socket = null)
    {
        if (socket is null || (_sockets.TryGetValue(installationId, out var current) && ReferenceEquals(current, socket)))
            _sockets.TryRemove(installationId, out _);
        _listeners.TryRemove(installationId, out _); Interlocked.Decrement(ref _connections);
    }

    private string MakeAccess(string id, int generation, DateTimeOffset? expiry = null)
    {
        var claim = JsonSerializer.SerializeToUtf8Bytes(new AccessClaim(id, generation, (expiry ?? DateTimeOffset.UtcNow.AddMinutes(5)).ToUnixTimeSeconds()));
        return Convert.ToBase64String(claim) + "." + Convert.ToBase64String(HMACSHA256.HashData(_signingKey, claim));
    }
    private static string Token() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    private static string Hash(string value) => Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static bool FixedTimeHashEquals(string left, string right) => CryptographicOperations.FixedTimeEquals(Convert.FromBase64String(left), Convert.FromBase64String(right));
    private sealed record Session(string RefreshHash, int Generation);
    private sealed record AccessClaim(string InstallationId, int Generation, long Expires);
    private void Persist()
    {
        lock (_storageGate)
        {
            var directory = Path.GetDirectoryName(_storagePath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            var saved = new PersistedState(2,
                _sessions.ToDictionary(x => x.Key, x => new PersistedSession(x.Value.RefreshHash, x.Value.Generation)),
                _routes.ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase))
            { UsedEnrollmentCodes = _usedEnrollmentCodes.Keys.ToArray(), KickSubscriptions = _kickSubscriptions.ToDictionary() };
            var temp = _storagePath + ".tmp";
            File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(saved));
            File.Move(temp, _storagePath, true);
        }
    }
    private void LoadEnrollmentClaims(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return;
        try
        {
            var entries = JsonSerializer.Deserialize<EnrollmentCredential[]>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
            foreach (var entry in entries)
            {
                if (string.IsNullOrEmpty(entry.Code) || entry.Code.Length < 24 || entry.Channels is null || entry.Channels.Length is 0 or > 32 ||
                    entry.Channels.Any(c => !IsValidRoute(c)) || entry.Channels.Distinct(StringComparer.OrdinalIgnoreCase).Count() != entry.Channels.Length) continue;
                _enrollmentClaims[Hash(entry.Code)] = entry.Channels;
            }
        }
        catch (JsonException) { }
    }
    private static bool IsValidRoute(string route)
    {
        var parts = route.Split(':', 2);
        return parts.Length == 2 && parts[0] is "kick" or "facebook" && parts[1].Length is > 0 and <= 200;
    }
    private static bool SameChannels(IEnumerable<string> expected, IEnumerable<string> requested) =>
        expected.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .SequenceEqual(requested.OrderBy(x => x, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
    private sealed record EnrollmentCredential(string Code, string[] Channels);
    private sealed record PersistedState(int SchemaVersion, Dictionary<string, PersistedSession> Sessions, Dictionary<string, string> Routes)
    {
        public string[] UsedEnrollmentCodes { get; init; } = [];
        public Dictionary<string, KickRelaySubscriptionStatus> KickSubscriptions { get; init; } = [];
    }
    private sealed record PersistedSession(string RefreshHash, int Generation);
}
