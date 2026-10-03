# Aravals Stream 1.0.0 — stable release acceptance

Desktop, Remote Capture Agent and Relay use version **1.0.0**. This release updates versioning and packaging; it does not introduce features or redesign the architecture.

## Downloads and supported scope

| Product | Platform | Package |
| --- | --- | --- |
| Desktop workstation | Windows x64 | AravalsStream-Setup-1.0.0.exe |
| Remote Capture Agent | Windows x64 | AravalsRemoteCapture-Setup-1.0.0.exe |
| Optional event Relay | Linux x64 | AravalsStream-Relay-1.0.0-linux-x64.tar.gz |

Windows installers include FFmpeg. Relay is an event service, not a Linux streaming workstation. Linux/macOS desktop ports remain development software with incomplete feature parity and native acceptance. Earlier development downloads remain separate from this stable release. The workflow builds/tests those targets but excludes their packages from stable release assets.

## Regression gates

The release workflow freezes one source commit and requires all build/test/package jobs to succeed before publishing. Builds and publishes treat warnings as errors. Windows solution regression covers Core/Relay contracts, the WPF workstation and portable media contracts; native Linux/Mac runners exercise applicable portable tests. Platform-specific or opt-in hardware tests may be skipped when prerequisites are unavailable, and test reports retain those results.

Production WPF media regression uses actual FFmpeg recording and a local RTMP receiver, encrypted Remote PC SRT video/audio, decoded non-black video and non-silent audio, Relay-offline startup, outage/recovery continuity, event delivery after recovery, and process cleanup. Installed acceptance exercises the packaged Agent/Desktop assemblies with real display capture, pairing, protected credential persistence, restart, revocation, re-pairing and cleanup in isolated profiles.

Local acceptance logs and media remain in ignored `artifacts/1.0.0/acceptance/`. GitHub Actions retains test reports and release assets. SHA256SUMS.txt and BUILD-INFO.json identify the published packages and source commit. Automated localhost Remote PC and RTMP checks do not prove a physical second-PC network or a live Internet provider broadcast.

## Provider and platform limits

- Real Kick/Facebook authorization, callbacks and a production Relay deployment remain unverified. Fixture regression does not establish live provider acceptance.
- Facebook reactions, Stars, follows and shares remain unsupported by the current integration/API.
- Direct injected game capture is not implemented. Display/window capture availability depends on the application and OS restrictions; exclusive fullscreen/protected content is not guaranteed.
- macOS system audio and Linux Wayland desktop capture are not implemented. Mac development packages are unsigned and not notarized, and require separately installed FFmpeg and OS permissions.
- Remote PC requires documented pairing, firewall and network setup. Relay requires production credentials, HTTPS and persistence configuration; no infrastructure is deployed by release packaging.

## Publication

The stable tag is `v1.0.0`, with `prerelease: false` and GitHub latest status. Automatic change notes accompany supported downloads. Website metadata is prepared locally to consume the latest stable GitHub release; this task does not deploy the website or Relay.
