namespace AravalsStream.Core.RemoteCapture;

/// <summary>Converts limited-range BT.601 NV12 frames into the compositor's BGRA8 frame contract.</summary>
public static class Nv12BgraConverter
{
    public static void Convert(ReadOnlySpan<byte> nv12, Span<byte> bgra, int width, int height)
    {
        if (width <= 0 || height <= 0 || (width & 1) != 0 || (height & 1) != 0)
            throw new ArgumentOutOfRangeException(nameof(width), "NV12 dimensions must be positive and even.");
        var pixels = checked(width * height);
        if (nv12.Length < checked(pixels * 3 / 2)) throw new ArgumentException("NV12 frame is truncated.");
        if (bgra.Length < checked(pixels * 4)) throw new ArgumentException("BGRA output buffer is too small.");

        var uvPlane = nv12.Slice(pixels);
        for (var y = 0; y < height; y++)
        {
            var yRow = nv12.Slice(y * width, width);
            var uvRow = uvPlane.Slice((y >> 1) * width, width);
            var output = bgra.Slice(y * width * 4, width * 4);
            for (var x = 0; x < width; x += 2)
            {
                var d = uvRow[x] - 128;
                var e = uvRow[x + 1] - 128;
                WritePixel(yRow[x], d, e, output, x * 4);
                WritePixel(yRow[x + 1], d, e, output, (x + 1) * 4);
            }
        }
    }

    private static void WritePixel(int luma, int d, int e, Span<byte> output, int offset)
    {
        var c = Math.Max(0, luma - 16);
        output[offset] = Clip((298 * c + 516 * d + 128) >> 8);
        output[offset + 1] = Clip((298 * c - 100 * d - 208 * e + 128) >> 8);
        output[offset + 2] = Clip((298 * c + 409 * e + 128) >> 8);
        output[offset + 3] = 255;
    }

    private static byte Clip(int value) => (byte)Math.Clamp(value, 0, 255);
}
