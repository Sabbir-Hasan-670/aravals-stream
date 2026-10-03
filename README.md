# Aravals Stream

Windows desktop livestreaming application built with C#, .NET 8, WPF, and MVVM.

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
