namespace AravalsStream.App.Recording;

public interface IHardwareEncoderBackend
{
    string Id { get; }
    string DisplayName { get; }
    bool IsHardwareAccelerated { get; }
    string DefaultPreset { get; }
    string PixelFormat { get; }
    string BuildEncoderArguments(int bitrateKbps, int keyframeIntervalSeconds, string? preset = null);
}

public sealed class NvencBackend : IHardwareEncoderBackend
{
    public string Id => "h264_nvenc";
    public string DisplayName => "NVIDIA NVENC (D3D11 / NV12)";
    public bool IsHardwareAccelerated => true;
    public string DefaultPreset => "p4";
    public string PixelFormat => "nv12";

    public string BuildEncoderArguments(int bitrateKbps, int keyframeIntervalSeconds, string? preset = null)
    {
        var p = preset ?? DefaultPreset;
        return $"-c:v h264_nvenc -b:v {bitrateKbps}k -preset {p} -tune ll -pix_fmt yuv420p";
    }
}

public sealed class QuickSyncBackend : IHardwareEncoderBackend
{
    public string Id => "h264_qsv";
    public string DisplayName => "Intel QuickSync (D3D11 / NV12)";
    public bool IsHardwareAccelerated => true;
    public string DefaultPreset => "medium";
    public string PixelFormat => "nv12";

    public string BuildEncoderArguments(int bitrateKbps, int keyframeIntervalSeconds, string? preset = null)
    {
        var p = preset ?? DefaultPreset;
        return $"-c:v h264_qsv -b:v {bitrateKbps}k -preset {p} -pix_fmt yuv420p";
    }
}

public sealed class AmfBackend : IHardwareEncoderBackend
{
    public string Id => "h264_amf";
    public string DisplayName => "AMD AMF (D3D11 / NV12)";
    public bool IsHardwareAccelerated => true;
    public string DefaultPreset => "balanced";
    public string PixelFormat => "nv12";

    public string BuildEncoderArguments(int bitrateKbps, int keyframeIntervalSeconds, string? preset = null)
    {
        var p = preset ?? DefaultPreset;
        return $"-c:v h264_amf -b:v {bitrateKbps}k -quality {p} -pix_fmt yuv420p";
    }
}

public sealed class SoftwareX264Backend : IHardwareEncoderBackend
{
    public string Id => "libx264";
    public string DisplayName => "Software x264 (CPU Fallback)";
    public bool IsHardwareAccelerated => false;
    public string DefaultPreset => "veryfast";
    public string PixelFormat => "yuv420p";

    public string BuildEncoderArguments(int bitrateKbps, int keyframeIntervalSeconds, string? preset = null)
    {
        var p = preset ?? DefaultPreset;
        return $"-c:v libx264 -b:v {bitrateKbps}k -preset {p} -tune zerolatency -pix_fmt yuv420p";
    }
}

public static class HardwareEncoderRegistry
{
    public static readonly IReadOnlyList<IHardwareEncoderBackend> All =
    [
        new NvencBackend(),
        new QuickSyncBackend(),
        new AmfBackend(),
        new SoftwareX264Backend()
    ];

    public static IHardwareEncoderBackend Resolve(string encoderId) =>
        All.FirstOrDefault(b => b.Id.Equals(encoderId, StringComparison.OrdinalIgnoreCase)) ?? new SoftwareX264Backend();
}
