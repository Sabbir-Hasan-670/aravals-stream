# Aravals Stream

Livestreaming desktop software with a Windows workstation and a Linux/macOS desktop port in development, built with C# and .NET 8.

## Downloads and platform status

Use [Build & Release — Windows, Linux, macOS](https://github.com/Sabbir-Hasan-670/aravals-stream/actions/workflows/release.yml) to create all platform downloads, checksums and automatic release notes in one run. See [release instructions](docs/releases.md).

The [combined 0.20.1-port.1 release](https://github.com/Sabbir-Hasan-670/aravals-stream/releases/tag/v0.20.1-port.1) contains Windows installers and Linux/Mac Intel/Mac Apple Silicon desktop development packages, plus release notes, checksums and build information. All were built and published by one successful workflow run.

The [0.20.1-beta release](https://github.com/Sabbir-Hasan-670/aravals-stream/releases/tag/v0.20.1-beta) contains the existing **Windows Desktop and Remote Capture Agent installers**, plus the optional Linux event Relay. The Relay is not the streaming desktop application.

The complete Linux/macOS workstation port is **in development** in this solution. `src/AravalsStream.Desktop` is an Avalonia development workspace with real FFmpeg preview, RTMP output and MKV recording. It is not feature-equivalent to the Windows workstation yet. See [port acceptance status](docs/cross-platform-port.md) before using development artifacts.

Building the Windows solution from a fresh checkout requires .NET 8 and FFmpeg:

```powershell
./scripts/get-windows-ffmpeg.ps1
dotnet build AravalsStream.sln -c Release
dotnet test AravalsStream.sln -c Release
```

Windows installer scripts also require Inno Setup 6. The GitHub Actions workflow prepares these dependencies and tests the Windows workstation. Native Linux/macOS jobs compile the portable desktop and exercise synthetic media; this does not establish native screen/audio acceptance or complete feature parity.

## Build and run

```powershell
dotnet build AravalsStream.sln
dotnet run --project src/AravalsStream.App/AravalsStream.App.csproj
```

## Phase 1 scope

The project has a responsive dark WPF shell, scene and source models with independent horizontal and vertical transforms, destination routing models, JSON settings abstraction, secure secret-storage abstraction, retry policy, and an honest streaming-state shell.

## Display capture

Choose a scene, select **Add Source**, pick **Display Capture**, select a connected display, and click **Add Display**. The selected desktop appears in the horizontal and vertical preview areas. Select a source to show or hide, rename, or delete it. The capture session stops when hidden, deleted, switched away from, or when the app closes.

Display capture uses Windows Desktop Duplication through Direct3D 11. A scene can contain several display items. Each display has one shared capture feed; WPF composes those feeds in ordered 1920×1080 and 1080×1920 logical canvases. Select a source to drag or resize it with corner handles. Shift allows free aspect ratio resizing. Use **Transform…** for numeric position, size, rotation, opacity, and crop values. The source list context menu contains fit, stretch, center, original size, reset, and canvas layout copy actions. Source order determines which item appears on top. Layout is saved in the local settings file on exit.

Display mode changes and secure desktop transitions may stop capture and require reselecting the source. The preview uploads only frames required by its selected performance mode and pauses while minimized. A direct GPU texture sharing path remains a future improvement.

### Manual scene check

1. Run the app and select Gaming. Add Display 1 and, if present, Display 2. Both appear in the scene; the second starts in a corner.
2. Select a source and drag it in Horizontal. Drag a corner handle to resize it. Hold Shift for free aspect ratio sizing.
3. Click Vertical and give that same source a different layout. Return to Horizontal and check that its layout remains unchanged.
4. Use **Transform…** for numeric edits and crop values. Right click a source for fit, stretch, center, original size, reset, and copy layout actions.
5. Show or hide, lock or unlock, and move the source up or down. Switch scenes and return.
6. Close and reopen the app; the scene layout and source order should restore.

Encoding, recording, RTMP outputs, platform APIs, chat connectors, and audio meters are implemented in later phases. See [Phase 17 performance notes](docs/phase17-performance.md) for the current media pipeline, benchmark results, and limits.
