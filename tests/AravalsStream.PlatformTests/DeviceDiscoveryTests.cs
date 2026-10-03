using AravalsStream.Platform;
using Xunit;

namespace AravalsStream.PlatformTests;

public sealed class DeviceDiscoveryTests
{
    [Fact]
    public void MacScreenIndexIsKeptSeparateFromCameraAndAudioIndices()
    {
        var devices = DeviceDiscovery.ParseAvFoundation("""
            [AVFoundation indev @ 0x123] AVFoundation video devices:
            [AVFoundation indev @ 0x123] [0] Built-in Camera
            [AVFoundation indev @ 0x123] [1] Capture screen 0
            [AVFoundation indev @ 0x123] AVFoundation audio devices:
            [AVFoundation indev @ 0x123] [0] Built-in Microphone
            """);
        Assert.Equal("1", Assert.Single(devices, d => d.Kind == CaptureKind.Display).Identifier);
        Assert.Equal("0", Assert.Single(devices, d => d.Kind == CaptureKind.Camera).Identifier);
        Assert.Equal("0", Assert.Single(devices, d => d.Kind == CaptureKind.Microphone).Identifier);
        Assert.DoesNotContain(devices, d => d.Kind == CaptureKind.DesktopAudio);
    }

    [Fact]
    public void DirectShowUsesUniqueIdentifierAndIgnoresDiagnosticLines()
    {
        var devices = DeviceDiscovery.ParseDirectShow("""
            [dshow @ 0123] "USB Camera" (video)
            [dshow @ 0123]   Alternative name "@device_pnp_123"
            [dshow @ 0123] "USB Microphone" (audio)
            [in#0 @ 0123] Error opening input: Immediate exit requested
            """);
        Assert.Equal(2, devices.Count);
        Assert.Equal("@device_pnp_123", Assert.Single(devices, d => d.Kind == CaptureKind.Camera).Identifier);
        Assert.Equal("USB Camera", Assert.Single(devices, d => d.Kind == CaptureKind.Camera).Name);
        Assert.Equal("USB Microphone", Assert.Single(devices, d => d.Kind == CaptureKind.Microphone).Identifier);
    }

    [Fact]
    public void PulseUsesMonitorRelationshipRatherThanDeviceNameToIdentifyLoopback()
    {
        var devices = DeviceDiscovery.ParsePulseSources("""
            [
              {"name":"usb.monitor.microphone","description":"Mic","monitor_of_sink":4294967295},
              {"name":"native-loopback","description":"Speaker monitor","monitor_of_sink":0},
              {"name":"other-microphone","monitor_of_sink":"n/a"}
            ]
            """);
        Assert.Equal(CaptureKind.Microphone, devices[0].Kind);
        Assert.Equal(CaptureKind.DesktopAudio, devices[1].Kind);
        Assert.Equal(CaptureKind.Microphone, devices[2].Kind);
    }
}
