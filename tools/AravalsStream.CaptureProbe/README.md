# Background capture regression probe

Run on an unlocked Windows desktop with .NET 8:

```powershell
dotnet run --project tools/AravalsStream.CaptureProbe -c Release
```

The probe owns two animated test windows. It verifies actual Windows Graphics
Capture frames, unique pixel colors, DXGI display frames, and the production
compositor's latest frame versions while foreground, covered for 8 seconds,
minimized for 16 seconds, and restored with repeated resizing. It also verifies
that restoring preview publishes the latest background frame. A failed stage
returns exit code 1. It does not load or modify user settings or accounts.

This tests the capture engine with an animated target. A browser tab that stops
rendering its own content still cannot supply new pixels to window capture.
