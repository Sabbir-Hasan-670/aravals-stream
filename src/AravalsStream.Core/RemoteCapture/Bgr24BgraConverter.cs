namespace AravalsStream.Core.RemoteCapture;

/// <summary>Expands packed BGR24 pipe frames into the compositor's BGRA8 frame contract.</summary>
public static class Bgr24BgraConverter
{
    public static void Convert(ReadOnlySpan<byte> bgr24, Span<byte> bgra, int width, int height)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        var pixels = checked(width * height);
        if (bgr24.Length < checked(pixels * 3)) throw new ArgumentException("BGR24 frame is truncated.", nameof(bgr24));
        if (bgra.Length < checked(pixels * 4)) throw new ArgumentException("BGRA output buffer is too small.", nameof(bgra));
        for (var pixel = 0; pixel < pixels; pixel++)
        {
            var input = pixel * 3;
            var output = pixel * 4;
            bgra[output] = bgr24[input];
            bgra[output + 1] = bgr24[input + 1];
            bgra[output + 2] = bgr24[input + 2];
            bgra[output + 3] = 255;
        }
    }
}
