using System.Globalization;

namespace AravalsStream.Platform;

public enum DesktopPlatform { Windows, LinuxX11, LinuxWayland, MacOS }
public enum CaptureKind { Display, Camera, Image, Video, Microphone, DesktopAudio, TestVideo, TestAudio }
public sealed record CaptureInput(CaptureKind Kind, string Device, float Gain = 1, bool Muted = false);

public static class PlatformCapture
{
    public static DesktopPlatform Current => OperatingSystem.IsWindows() ? DesktopPlatform.Windows :
        OperatingSystem.IsMacOS() ? DesktopPlatform.MacOS :
        Environment.GetEnvironmentVariable("XDG_SESSION_TYPE") == "wayland" ? DesktopPlatform.LinuxWayland : DesktopPlatform.LinuxX11;

    // Arguments are individual tokens passed to ProcessStartInfo.ArgumentList, never shell commands.
    public static string[] Arguments(DesktopPlatform platform, CaptureInput input, int fps)
    {
        if (fps is < 1 or > 120) throw new ArgumentOutOfRangeException(nameof(fps));
        if (!float.IsFinite(input.Gain) || input.Gain is < 0 or > 2) throw new ArgumentOutOfRangeException(nameof(input));
        var rate = fps.ToString(CultureInfo.InvariantCulture);
        var device = input.Device;
        if (input.Kind is not (CaptureKind.TestVideo or CaptureKind.TestAudio) && string.IsNullOrWhiteSpace(device))
            throw new ArgumentException("Choose a capture device or file.");
        if (device.Contains('\0')) throw new ArgumentException("Invalid capture device.");
        return input.Kind switch
        {
            CaptureKind.TestVideo => ["-re", "-f", "lavfi", "-i", $"testsrc2=size=640x360:rate={rate}"],
            CaptureKind.TestAudio => ["-re", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000"],
            CaptureKind.Image => ["-loop", "1", "-framerate", rate, "-i", Path.GetFullPath(device)],
            CaptureKind.Video => ["-re", "-stream_loop", "-1", "-i", Path.GetFullPath(device)],
            CaptureKind.Display when platform == DesktopPlatform.Windows => ["-f", "gdigrab", "-framerate", rate, "-i", device],
            CaptureKind.Display when platform == DesktopPlatform.LinuxX11 => ["-f", "x11grab", "-framerate", rate, "-i", device],
            CaptureKind.Display when platform == DesktopPlatform.MacOS => ["-f", "avfoundation", "-framerate", rate, "-capture_cursor", "1", "-i", device + ":none"],
            CaptureKind.Display => throw new PlatformNotSupportedException("Wayland requires the ScreenCast portal and PipeWire capture adapter; X11 capture must not be used as a substitute."),
            CaptureKind.Camera when platform == DesktopPlatform.Windows => ["-f", "dshow", "-framerate", rate, "-i", "video=" + device],
            CaptureKind.Camera when platform is DesktopPlatform.LinuxX11 or DesktopPlatform.LinuxWayland => ["-f", "v4l2", "-framerate", rate, "-i", device],
            CaptureKind.Camera => ["-f", "avfoundation", "-framerate", rate, "-i", device + ":none"],
            CaptureKind.Microphone when platform == DesktopPlatform.Windows => ["-f", "dshow", "-i", "audio=" + device],
            CaptureKind.Microphone when platform == DesktopPlatform.MacOS => ["-f", "avfoundation", "-i", "none:" + device],
            CaptureKind.Microphone => ["-f", "pulse", "-i", device],
            CaptureKind.DesktopAudio when platform is DesktopPlatform.LinuxX11 or DesktopPlatform.LinuxWayland => ["-f", "pulse", "-i", device],
            CaptureKind.DesktopAudio => throw new PlatformNotSupportedException("Native loopback adapter required; a microphone must not be presented as desktop audio."),
            _ => throw new ArgumentOutOfRangeException(nameof(input))
        };
    }
}
