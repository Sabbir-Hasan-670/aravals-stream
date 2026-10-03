# Full workstation port — work in progress

User scope: full Aravals Stream on Windows, Linux and macOS. The existing Windows workstation remains available and unchanged in functionality. The portable workspace is a development target in the same solution, not a replacement repository or a completed full port.

## Project structure

- `AravalsStream.Core`: shared settings, scenes, destinations, provider clients, Relay client, event bus, alerts, recording/network models.
- `AravalsStream.Platform`: portable native-input planning and FFmpeg process lifecycle. Arguments use `ProcessStartInfo.ArgumentList`; shell interpretation is never used. Raw encoder diagnostics are drained without exposing keys.
- `AravalsStream.Desktop`: Avalonia desktop workspace targeting .NET 8. Separate development version `0.20.1-port.1` distinguishes it from the existing validated Windows release.
- Existing `AravalsStream.App` and `AravalsStream.RemoteAgent`: original Windows applications.

Official references: [Avalonia supported platforms](https://docs.avaloniaui.net/docs/supported-platforms), [Avalonia 11.3.12 package](https://www.nuget.org/packages/Avalonia/11.3.12), [FFmpeg device contracts](https://ffmpeg.org/ffmpeg-devices.html), [FFmpeg binary distribution links](https://ffmpeg.org/download.html).

## Current portable implementation

Actual preview, RTMP/RTMPS encoding, MKV recording, landscape/portrait canvas, image/video input and selected audio mixing are wired to FFmpeg. A moving test pattern is available for acceptance without capturing private desktop content. Stream keys remain in memory only in this workspace. Existing recordings are not intentionally overwritten by the UI.

Native command planning covers Windows GDI/DirectShow, Linux X11/V4L2/PulseAudio and macOS AVFoundation. Device identifiers must come from native enumeration; macOS camera 0 must not be guessed as the screen. FFmpeg and OS capture permissions are required. Synthetic tests verify JPEG preview and actual H.264/AAC recording.

## Required before full-port completion

| Area | Portable status / remaining work |
| --- | --- |
| Native Windows workstation | Existing full application remains the release target |
| Linux/macOS UI shell, preview and encoder | Development implementation; native acceptance pending |
| Multiple scenes/sources, transforms and live switching | Shared Core models exist; portable compositor and UI migration pending |
| Windows loopback audio | Existing Windows app supports it; portable native bridge pending |
| macOS system audio/window capture | ScreenCaptureKit adapter and permissions pending |
| Linux Wayland screen/window capture | ScreenCast portal/PipeWire adapter pending; never silently substitute X11 |
| Native device enumeration and hotplug recovery | Pending portable UI integration |
| Independent multistream destinations/reconnect/adaptive bitrate | Existing Windows behavior; portable migration pending |
| Camera/capture device/game capture | Backend planning exists for camera; native discovery and supported game/window capture pending |
| Media monitoring, routing, meters and sync | Shared mixer exists; native capture and portable UI integration pending |
| Persistent credentials | Windows DPAPI retained; macOS Keychain and Linux Secret Service bridges pending |
| OAuth/provider controls, chat, alerts and Relay UI | Existing Core integration retained; portable view/controller migration pending |
| Remote PC pairing/capture and cross-platform Agent | Original Windows implementation retained; native port pending |
| Hotkeys, tray/recovery, accessibility | Portable integration pending |
| Native installers, dependency bundling and signing | Windows release available; Linux/macOS final packaging pending |
| Full native capture/media acceptance | Required on each supported OS before release |

GitHub Actions artifacts named `desktop-development-*` are explicitly development builds. They must not be promoted or published on the download website as the completed Linux/macOS software. macOS Intel/Apple Silicon and Linux x64 are initial build targets; other architectures require separate acceptance.

The portable project avoids shared application profiles while persistent settings/credential migration is pending. Production Relay deployment is not part of this task and has not been performed.
