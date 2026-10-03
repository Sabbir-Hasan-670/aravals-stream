# Phase 19 status

Phase 19 engineering acceptance is complete for candidate 0.19.8-beta. The
final isolated pairing lifecycle verified Forget revocation, rejection of the
old credential across Agent restart and after re-pair, new credential media
authorization, and new credential persistence. The registered Windows
pairing profile remained unchanged. See the final acceptance section in
[phase-19-validation.md](phase-19-validation.md).

Manual visual QA, two-PC operation, and physical HDMI capture remain pending.
Same-PC combined cadence remains classified as ONE-PC COMBINED CAPACITY
EXCEEDED; it is not a two-PC result.

## Capture device

The Add Source dialog has a distinct **Capture Device (HDMI / UVC)** choice.
It uses Windows Media Foundation `MediaFrameSourceGroup` / `MediaFrameReader`
and the format advertised by the selected device. The current machine has not
been tested with a physical HDMI capture card or UVC device. Capture-card video
and audio are not automatically associated; audio can be added through Audio
Input Capture and routed through the existing audio matrix.

## Remote capture implementation

The app includes a Windows Remote Capture Agent, temporary-code pairing,
protected stored transport keys, LAN discovery/manual connection, encrypted
SRT transport, H.264/AAC MPEG-TS sender and receiver, remote video/audio source
integration, and a bounded latest-frame handoff. A disconnected Agent is
handled through the existing capture-failure/retry path. The agent exposes
performance, frame-rate, and bitrate controls.

The Agent's `LowImpact` choice captures at native display size and scales the
encoded output to 1280×720 with aspect ratio preserved and padding as needed.
A same-PC 30-second loopback verified this 720p30 profile; see the validation
record for measured rates and resource use.

## Isolated performance results

See [phase-19-validation.md](phase-19-validation.md) for test setup and
measurements. The 1080p30 sender-only and receiver-only tests both sustained
about 30 FPS. The earlier one-PC loopback fell to roughly 11–15 receiver FPS
while the sender ran at about 30 FPS, so that result is classified as
**one-PC combined capacity exceeded**, not a standalone receiver bottleneck.
Receiver code was not changed based on the combined-only result.

## Branding

The supplied transparent official logo is included as the desktop and Agent
brand asset. Desktop and Agent executable icons, app header, splash resource,
first-run wizard, About view, Settings → About, Agent window/tray, and both
Inno Setup scripts reference the brand assets. Product metadata is aligned
between the app and Agent. The source logo was copied without modification;
the multi-size ICO and 512-pixel splash image were derived from it.

The earlier 0.19.4-beta self-contained Desktop and Agent publishes used
matching EXE metadata. The Agent silence timeline emits a 10 ms / 960-float
stereo block on every tick, zero-filling when captured samples are unavailable.
Deterministic tests cover silence, real-to-silence and silence-to-real
transitions, and continuous sample counts. See the historical 0.19.4 results
and the final 0.19.8 acceptance in [phase-19-validation.md](phase-19-validation.md).

## Final acceptance

- Remote-PC production model/binding, media start/stop, restart, reconnect,
  silent-audio behavior, horizontal/vertical rendering, recording, local stream
  output, sync-offset control, tray Exit, and static branding passed technical
  acceptance. Build and all 235 tests passed.
- Isolated credential acceptance passed: Credential A authenticated and
  persisted; production Forget removed local metadata and protected secret and
  revoked A remotely; A remained rejected after Agent restart and after
  Credential B was issued. B differed from A, authorized video/audio media,
  and persisted through restart.
- Agent DeviceId and certificate identity remained stable across Forget and
  re-pair; the credential changed. The registered Windows pairing files were
  unchanged.
- Visual WPF inspection remains manual. Two-PC operation and physical HDMI
  capture were not verified.

## Build and tests

Final `dotnet clean`, Release build, and Release test completed for 0.19.8-beta
with 0 build warnings, 0 errors, and all 235 tests passing. Self-contained
win-x64 Desktop and Agent publishes and both 0.19.8-beta installers were
produced.
