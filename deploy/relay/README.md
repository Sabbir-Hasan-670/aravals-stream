# Aravals Relay 0.20.1-beta

This package runs the optional event relay only. It never handles video, encoded media, audio, or recordings. Keep the Desktop's RTMP/RTMPS and Remote PC LAN SRT paths unchanged.

## Local development

Run from the repository root in PowerShell with Development mode and local-only HTTP:

```powershell
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:ASPNETCORE_URLS = 'http://localhost:5080'
$env:RELAY_TOKEN_SIGNING_KEY = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
$enrollCode = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
$env:RELAY_ENROLLMENT_CREDENTIALS_JSON = ConvertTo-Json -Compress @(@{ code = $enrollCode; channels = @('kick:YOUR_KICK_USER_ID', 'facebook:YOUR_PAGE_ID') })
$env:RELAY_STORAGE_PATH = Join-Path $env:TEMP 'aravals-relay-state.json'
$env:KICK_WEBHOOK_PUBLIC_KEY_PEM = '<Kick-published public key PEM>'
$env:FACEBOOK_WEBHOOK_VERIFY_TOKEN = '<random verification token>'
$env:FACEBOOK_APP_SECRET = '<Meta app secret>'
dotnet run --project src/AravalsStream.Relay
```

The Desktop accepts `http://localhost`/loopback only for development. Production requires HTTPS at the public edge. Set `RELAY_ALLOWED_HOSTS` and `RELAY_TRUSTED_PROXY` to the exact public host and proxy IP. Never expose the container's HTTP listener directly to the Internet.

For containers, supply the provider's public RSA PEM as a read-only mount at the path configured by `KICK_WEBHOOK_PUBLIC_KEY_FILE`. Process environments may alternatively provide `KICK_WEBHOOK_PUBLIC_KEY_PEM`, with real newlines or escaped `\n` separators. The public key is verification material, not a subscription secret. Docker `--env-file` expects enrollment JSON without surrounding shell quotes; the template uses that format.

## Container deployment preparation

Build from the repository root with `docker build -f deploy/relay/Dockerfile -t aravals-relay:0.20.1-beta .`. The deployment ZIP also includes the self-contained `linux-x64/` publish and `Dockerfile.published`; after extracting the ZIP, `docker build -f Dockerfile.published -t aravals-relay:0.20.1-beta .` needs no repository checkout. Mount persistent storage at `/data` (for example `-v aravals-relay-state:/data`) so sessions and channel ownership survive process restarts. Terminate TLS at a trusted reverse proxy and forward the original HTTPS scheme. Set `RELAY_TRUSTED_PROXY` to the proxy address as seen inside the container (a Docker bridge gateway may differ from localhost). Copy `.env.example` to a protected environment file and replace every placeholder. Store the environment file outside source control with restrictive permissions. `nginx-relay.conf.example` shows HTTPS and WebSocket proxy settings. Production deployment is a separate explicit step.

## Startup preflight

Run `./linux-x64/AravalsStream.Relay --preflight` with the deployment environment configured, or `dotnet run --project src/AravalsStream.Relay -- --preflight` in the repository. This returns nonzero on an invalid signing key, missing production host allowlist, unwritable state location, invalid public URL or missing enabled-provider configuration. Normal service startup runs the same checks before accepting requests. The signing key must be Base64 representing at least 32 cryptographically random bytes; repeated, sequential, default and plain text key material is rejected. No insecure fallback key is generated.

## Kick subscription lifecycle

Official sources checked for this version: [Kick OpenAPI](https://api.kick.com/swagger/doc.yaml), [event subscriptions](https://github.com/KickEngineering/KickDevDocs/blob/main/events/subscribe-to-events.md), [webhook application setup](https://github.com/KickEngineering/KickDevDocs/blob/main/events/introduction.md), and [event formats](https://github.com/KickEngineering/KickDevDocs/blob/main/events/event-types.md).

The API supports GET, POST and DELETE `/public/v1/events/subscriptions`. POST accepts event name/version and `method=webhook`; a user access token determines the broadcaster. Callback URLs and per-subscription secrets are not fields in that protocol. Configure the application's webhook URL in Kick's developer settings to `RELAY_PUBLIC_BASE_URL` plus `/webhooks/kick`, then confirm that exact URL in `KICK_CONFIGURED_WEBHOOK_URL`. Set `KICK_APP_ID` to the same application's client ID and enable `KICK_SUBSCRIPTIONS_ENABLED`. The API cannot read back the application's configured callback, so the administrator's configuration is the explicit prerequisite, not a claimed provider verification.

Enable subscription management in Desktop's Webhook Relay settings. The existing Kick OAuth flow now requests `events:subscribe`; accounts authorized before this version may need reconnecting to grant that scope. Desktop sends its current access token only in an authenticated HTTPS operation. Relay checks the installation's enrolled channel, the provider token's active user type/application/scope, and the authenticated Kick user ID before provider mutations. Kick access tokens and client secrets are never persisted by Relay. The Relay needs no Kick client secret; the existing Desktop secret storage and OAuth refresh flow remain responsible for that credential.

Only the five event types consumed by the existing mapper are requested: chat, follow, new subscription, subscription renewal, and subscription gifts. Each operation lists subscriptions and matches app ID, broadcaster, event/version and webhook method before creating only missing entries. An operation gate prevents simultaneous duplicate mutations. IDs, callback configuration, status and last successful discovery are persisted as non-secret metadata; schema 1 state is loaded without removing sessions/routes and is upgraded to schema 2 on the next write. Relay restart and Desktop reconnect always discover first. A disconnected Desktop does not delete persistent subscriptions.

The current contract supplies no webhook-subscription expiry or renewal endpoint. The `channel.subscription.renewal` event describes a viewer's paid subscription, not the webhook subscription's lifetime. Desktop performs discovery on connection and every 15 minutes; missing entries are recreated, without a fabricated expiry timer. GET timeout/5xx/429 retries are capped at three attempts with backoff. Mutating POST is never blindly retried. Unknown create outcomes remain Pending/Error with persisted intent, and subsequent discovery can resolve them. If the provider definitively never created them, use **Delete managed subscriptions** to clear that uncertain intent, then **Check Kick subscriptions** to explicitly recreate. DELETE acts only on matching managed or uncertain IDs discovered from the provider. OAuth rejection suspends automatic authorization retries until the user reconnects/retries. Errors expose fixed summaries, not provider bodies.

Desktop shows NotConfigured, Checking, Pending, Active, Error or ReauthorizationRequired, last successful provider check, and a redacted error summary. Active requires provider-confirmed discovery of every required event; socket connection alone never means subscriptions are Active. A local Relay cannot be used as a real provider callback. `KICK_SUBSCRIPTIONS_ENABLED=false` remains the safe local default.

## Facebook scope

Facebook challenge verification, HMAC signature verification and supported `page` feed/live_videos fixture normalization remain available. Facebook Live reactions, Stars, followers and share alerts are **UNSUPPORTED BY CURRENT PROVIDER INTEGRATION/API** in the existing Aravals integration. This is a capability boundary, not a product bug or a claim that all Meta APIs lack webhooks. Page callback tests do not establish a reviewed live Meta subscription. The current Meta documentation endpoints returned HTTP 429 during this pass; the repository's Phase 14 capability audit remains the available integration contract.

## Security and current operating limits

- Kick requests are checked against the configured provider RSA public key, signed raw body, message ID, and a five-minute timestamp window. Only the existing chat/follow/subscription event families are normalized.
- Facebook supports the Graph Webhooks GET challenge and HMAC signature verification for signed `page` feed and `live_videos` fixtures. Existing app capability metadata still marks desktop Facebook live webhook integration unsupported; this is not a claim of a live Meta subscription.
- Each high-entropy enrollment code is one-time and server-configured for an exact provider/channel allowlist. Create one credential entry per trusted Desktop in `RELAY_ENROLLMENT_CREDENTIALS_JSON`. Relay reserves those routes at enrollment; WebSocket clients may request only their pre-assigned channels. Refresh credentials are stored on Windows with current-user DPAPI; Relay stores only SHA-256 refresh-token and used-enrollment-code digests. Access tokens are signed, five-minute credentials. WebSocket auth is sent in the first TLS-protected frame, not a URL.
- Channel ownership, used-code hashes, and refresh-token hashes persist in an atomically replaced JSON file. Replay entries and live connections are in memory. Events are not queued while a Desktop is offline. One bounded 128-event WebSocket send queue exists per connected installation; overload evicts the oldest queued event. A Relay process restart keeps Desktop sessions and channel claims, and Desktop reconnects with its saved DPAPI refresh credential.
- Real public Kick and Facebook callbacks remain NOT VERIFIED until production HTTPS deployment and platform application configuration are explicitly authorized.
- The public status endpoints expose only service version, uptime, and active connection count. Logs contain metadata only, never request bodies or credential values.
