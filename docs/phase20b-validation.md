# Phase 20B local engineering acceptance — 0.20.1-beta

Validated on 2026-10-01. No production infrastructure was accessed or changed. Phase 21 was not started.

## Official contract and implementation

Reviewed current [Kick OpenAPI](https://api.kick.com/swagger/doc.yaml), [subscription operations](https://github.com/KickEngineering/KickDevDocs/blob/main/events/subscribe-to-events.md), [application webhook configuration](https://github.com/KickEngineering/KickDevDocs/blob/main/events/introduction.md), and [event types](https://github.com/KickEngineering/KickDevDocs/blob/main/events/event-types.md).

GET/POST/DELETE event subscriptions are implemented for the five existing chat/follow/subscription families. User tokens require `events:subscribe`; introspection verifies the application and scope, and the authenticated user must match the enrolled channel. Relay validates installation ownership before provider calls. Kick uses RSA signatures, not an invented subscription secret.

Callback configuration is application-level in Kick developer settings. The reviewed API does not expose callback URL readback, expiry fields, or a renewal endpoint. Relay requires a valid public HTTPS base URL and an explicit matching configured-callback confirmation. Discovery occurs before mutations, on Desktop reconnect, and periodically. Missing subscriptions are recreated; unknown POST outcomes persist intent and are reconciled rather than blindly retried. Explicit deletion clears unresolved intent. Active requires provider-confirmed discovery of every managed event.

Persisted schema 1 sessions/routes/enrollment data migrate to schema 2 with non-secret subscription metadata. Provider timeout/5xx/429 retries are bounded; malformed responses cannot mark Active. OAuth rejection suspends automatic authorization retries until explicit user retry/reconnection. Desktop displays state, successful check time, and fixed redacted errors independently of media.

Facebook challenge/HMAC verification and supported page fixtures pass. Live reactions, Stars, follows and shares remain **UNSUPPORTED BY CURRENT PROVIDER INTEGRATION/API**. Current Meta documentation requests returned HTTP 429; the existing Phase 14 capability matrix is the available reviewed integration contract. Generic feed events are not fabricated as share alerts.

## Build and deterministic tests

- Release clean and build: zero warnings, zero errors.
- Final full solution test run: **278/278 passed**, zero skipped (274 Core/Relay tests, 4 Desktop tests).
- Final TRX evidence: `artifacts/0.20.1-beta/acceptance/final-tests/`.
- Coverage includes creation, existing discovery, concurrent duplicate avoidance, uncertain POST outcomes, restart reconciliation, deletion, authorization errors, provider errors/timeouts/malformed responses, cross-installation ownership, revoked Relay sessions, metadata migration, production preflight, and Docker static structure.
- An older OAuth test was updated to require the newly needed subscription scope.

## Actual Desktop media acceptance

Evidence: `artifacts/0.20.1-beta/acceptance/media-20260930-224857/results.json` and its received `local-stream.mkv`.

An isolated actual WPF MainWindow started with Relay offline. Production Remote PC encrypted SRT capture supplied moving test video and audible sine audio. The existing compositor, streaming output, recording service, and local RTMP receiver were exercised. Relay was started, killed during active media, and restarted without restarting Desktop.

| Interval | Submitted video frames | Submitted audio chunks | Received video packets | Received audio packets |
| --- | ---: | ---: | ---: | ---: |
| Offline at Desktop startup | 60 | 201 | 61 | 191 |
| Before outage | 60 | 199 | 61 | 190 |
| During outage | 60 | 200 | 61 | 191 |
| After recovery | 59 | 200 | 46 | 142 |

Receiver packet counts reflect the available media time windows, including the shorter final window. Every interval decoded non-black video and nonzero audio (peak approximately 0.125–0.128). Streaming PID 15052 and recording PID 6896 remained unchanged; streaming reconnect count was zero. Recording frames continued and the resulting recording was validated. Relay moved through reconnecting to connected automatically.

A valid signed local Kick fixture after recovery reached the actual MainWindow mapper, Unified Event Bus/chat, and existing alert engine exactly once; duplicate delivery did not create a second alert.

The only media correction was a confirmed telemetry bug: FFmpeg's legacy `out_time_ms` field is microseconds, matching `out_time_us`. Three regression cases now verify duration conversion. No media transport architecture was changed.

## Production preparation and artifacts

Actual production `--preflight` process: valid random key/public callback configuration passed; missing signing key exited 1 with a redacted fixed diagnostic. Deterministic tests cover weak/default keys, invalid public URLs, enabled-provider configuration, and invalid persistence paths. No insecure fallback exists.

Desktop and Agent publish for self-contained Windows x64; Relay publishes for self-contained Linux x64. All three report product version 0.20.1-beta/file version 0.20.1.0. Desktop installer input includes the existing bundled FFmpeg tools; Agent includes its bundled FFmpeg executable.

The deployment ZIP contains 336 entries: Linux publish, source Dockerfile, standalone published-runtime Dockerfile, README, environment template, reverse proxy example, exclusions, and package validator. ZIP validation passed after correcting a validator pattern that falsely matched its own source. No real credentials, private certificates, state files, or user-specific paths are packaged. Linux entrypoint has verified ELF magic; runtime/deps files are present. Static Docker checks pass for copy paths, entrypoints, production environment, non-root execution, persistence ownership and secret exclusions.

**DOCKER RUNTIME VALIDATION — ENVIRONMENT BLOCKED**: Docker is unavailable. Container image execution is not claimed.

Real Kick callback: NOT VERIFIED. Real Facebook callback: NOT VERIFIED. Production deployment: NOT PERFORMED. Provider lifecycle API calls are exercised through deterministic official-contract fixtures, not a live authorized account.

PHASE 20 ENGINEERING COMPLETE — PRODUCTION RELAY DEPLOYMENT PENDING
