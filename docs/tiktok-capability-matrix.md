# TikTok Capability Audit (24 September 2026)

Source of truth: Current official TikTok for Developers documentation (`developers.tiktok.com` / `open.tiktokapis.com/v2/`).

This document records the official API audit and architectural decisions for **Aravals Stream Phase 15**.

| Feature / Edge | Current Official Status (`developers.tiktok.com`) | Aravals Stream Decision |
| --- | --- | --- |
| **Desktop Login Kit & OAuth** | Supported via Authorization Code Flow with PKCE (`code_challenge_method=S256`). Authorization URL: `https://www.tiktok.com/v2/auth/authorize/`, Token URL: `https://open.tiktokapis.com/v2/oauth/token/`. | `Supported`. Implemented with PKCE S256 (`code_verifier` 43–128 random chars) and loopback HTTP redirect listener (`http://127.0.0.1:19455/callback/`). |
| **Client Key & Secret Handling** | TikTok token exchange requires `client_key` and `client_secret` alongside PKCE `code_verifier`. | Because compiled desktop applications cannot securely protect an embedded client secret, users configure their own developer credentials. The `ClientSecret` is protected using Windows DPAPI. |
| **User Profile / Display API** | `GET /v2/user/info/?fields=open_id,union_id,avatar_url,display_name` with `user.info.basic` scope. | `Supported`. Displays creator Display Name, OpenID, and Avatar URL. |
| **LIVE Streaming API / Ingest API** | **Not Available in Official Public APIs**. TikTok for Developers does not provide general-purpose endpoints for creating live broadcasts, retrieving RTMP stream keys, or managing live stream lifecycles for third-party desktop tools. | `UnsupportedByCurrentPlatformApi`. Video streaming is supported via **Manual RTMP** (using creator RTMP credentials obtained from TikTok Live Studio or Creator Center). No reverse-engineered or private endpoints. |
| **LIVE Chat API (Read/Send)** | **Not Available in Official Public APIs**. Real-time live chat is not exposed via public API endpoints or webhooks. | `UnsupportedByCurrentPlatformApi`. Chat UI displays "Unavailable through current official developer API". No unofficial WebSocket wrappers or scraping. |
| **LIVE Viewer Statistics** | **Not Available in Official Public APIs**. Concurrent viewer count is not exposed over public developer edges. | `UnsupportedByCurrentPlatformApi`. Viewer count displays `—`. No fabricated numbers or page scraping. |
| **LIVE Gift / Reaction Events** | **Not Available in Official Public APIs**. Real-time gifts, likes, and follower events require private/internal APIs. | `UnsupportedByCurrentPlatformApi`. Not supported. |
| **Content Posting API** | Available under `video.upload` / `video.publish` scopes for pre-recorded video file uploads. | Kept separate from Live Video streaming. Foundation placeholder `ITikTokContentPublisher` provided for future clip export. |
| **App Review / Permissions** | Login Kit and Content Posting require TikTok Developer App Review for public production use. | Documented and reflected in capability model (`RequiresAppReview`). |

---

### Architectural Summary
1. **Account Integration**: Native OAuth Login Kit with PKCE S256 storing tokens in Windows DPAPI (`TokenReference`, `RefreshTokenReference`).
2. **Streaming Transport**: Secure Manual RTMP (`ManualRtmp`), routing by default to Vertical (`1080x1920`) using the existing shared compositor and `StreamingOutput` pipeline.
3. **No Unofficial Libraries**: Unofficial WebSocket scraping or private mobile protocols are completely avoided to ensure long-term stability, compliance, and user security.
