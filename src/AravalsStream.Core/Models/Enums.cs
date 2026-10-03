namespace AravalsStream.Core.Models;

public enum CaptureState { Initializing, Active, Unavailable, Disconnected, Recovering, Error, Stopped }
public enum OutputMode { Horizontal, Vertical, Both }
public enum DestinationStatus { Disabled, Offline, Connecting, Live, FallingBehind, Reconnecting, Restarting, Stopping, Error }
// Keep existing numeric values stable because settings store enum values as JSON numbers.
public enum SourceType { DisplayCapture, WindowCapture, GameCapture, Camera, Image, Video, Text, Browser, Alerts, AudioInput, AudioOutput, Color, ChatOverlay, CaptureDevice, RemotePc }
public enum StreamSessionState { Idle, Starting, Live, Stopping, Error }
public enum VerticalLayoutMode { FitEntire, FillCanvas, SmartVertical, OriginalSize, Center, Manual }
public enum SmartVerticalTemplate { FullscreenCrop, BackgroundAndFullSource }

