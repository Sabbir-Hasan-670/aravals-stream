namespace AravalsStream.Core.Composition;

public static class Yuv420FrameConverter
{
    public static int BufferSize(int width, int height)
    {
        if (width <= 0 || height <= 0 || (width & 1) != 0 || (height & 1) != 0)
            throw new ArgumentOutOfRangeException(nameof(width), "YUV420 requires positive even dimensions.");
        return checked(width * height * 3 / 2);
    }

    public static void Convert(byte[] bgra, byte[] yuv, int width, int height)
    {
        var pixels = checked(width * height);
        if (yuv.Length < BufferSize(width, height) || bgra.Length < checked(pixels * 4))
            throw new ArgumentException("Invalid BGRA or YUV420 buffer length.");
        var uvWidth = width / 2;
        var vOffset = pixels + pixels / 4;
        void ConvertRows(int firstRow, int lastRow)
        {
            for (var y = firstRow; y < lastRow; y += 2)
            {
                for (var x = 0; x < width; x += 2)
                {
                    var i0 = (y * width + x) * 4;
                    var i1 = i0 + 4;
                    var i2 = i0 + width * 4;
                    var i3 = i2 + 4;
                    var b = (bgra[i0] + bgra[i1] + bgra[i2] + bgra[i3] + 2) / 4;
                    var g = (bgra[i0 + 1] + bgra[i1 + 1] + bgra[i2 + 1] + bgra[i3 + 1] + 2) / 4;
                    var r = (bgra[i0 + 2] + bgra[i1 + 2] + bgra[i2 + 2] + bgra[i3 + 2] + 2) / 4;
                    yuv[y * width + x] = Luma(bgra[i0 + 2], bgra[i0 + 1], bgra[i0]);
                    yuv[y * width + x + 1] = Luma(bgra[i1 + 2], bgra[i1 + 1], bgra[i1]);
                    yuv[(y + 1) * width + x] = Luma(bgra[i2 + 2], bgra[i2 + 1], bgra[i2]);
                    yuv[(y + 1) * width + x + 1] = Luma(bgra[i3 + 2], bgra[i3 + 1], bgra[i3]);
                    var uv = y / 2 * uvWidth + x / 2;
                    yuv[pixels + uv] = (byte)Math.Clamp(((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128, 0, 255);
                    yuv[vOffset + uv] = (byte)Math.Clamp(((112 * r - 94 * g - 18 * b + 128) >> 8) + 128, 0, 255);
                }
            }
        }
        var rowPairs = height / 2;
        // Multiple canvases and encoders already run concurrently. Limit worker
        // fan-out so one conversion cannot occupy most cores and delay pumps.
        var workers = Math.Min(Math.Max(1, Environment.ProcessorCount / 2), Math.Min(4, Math.Max(1, rowPairs / 96)));
        if (workers < 2) ConvertRows(0, height);
        else Parallel.For(0, workers, worker =>
        {
            var first = 2 * (worker * rowPairs / workers);
            var last = 2 * ((worker + 1) * rowPairs / workers);
            ConvertRows(first, last);
        });
    }

    public static void ResizeNearest(ReadOnlySpan<byte> source, int sourceWidth, int sourceHeight,
        Span<byte> destination, int targetWidth, int targetHeight, string format = "yuv420p")
    {
        if (format == "nv12")
        {
            ResizeNv12(source, sourceWidth, sourceHeight, destination, targetWidth, targetHeight);
            return;
        }

        if (source.Length < BufferSize(sourceWidth, sourceHeight) || destination.Length < BufferSize(targetWidth, targetHeight))
            throw new ArgumentException("Invalid YUV420 buffer length.");
        ResizePlane(source, sourceWidth, sourceHeight, destination, targetWidth, targetHeight);
        var sourcePixels = sourceWidth * sourceHeight;
        var targetPixels = targetWidth * targetHeight;
        ResizePlane(source.Slice(sourcePixels), sourceWidth / 2, sourceHeight / 2,
            destination.Slice(targetPixels), targetWidth / 2, targetHeight / 2);
        ResizePlane(source.Slice(sourcePixels + sourcePixels / 4), sourceWidth / 2, sourceHeight / 2,
            destination.Slice(targetPixels + targetPixels / 4), targetWidth / 2, targetHeight / 2);
    }

    public static void ResizeNv12(ReadOnlySpan<byte> source, int sourceWidth, int sourceHeight,
        Span<byte> destination, int targetWidth, int targetHeight)
    {
        if (source.Length < BufferSize(sourceWidth, sourceHeight) || destination.Length < BufferSize(targetWidth, targetHeight))
            throw new ArgumentException("Invalid NV12 buffer length.");
        ResizePlane(source, sourceWidth, sourceHeight, destination, targetWidth, targetHeight);
        var sourcePixels = sourceWidth * sourceHeight;
        var targetPixels = targetWidth * targetHeight;
        var uvHeight = targetHeight / 2;
        var uvWidthPairs = targetWidth / 2;
        var srcUvWidthPairs = sourceWidth / 2;
        var srcUvHeight = sourceHeight / 2;

        var xMap = System.Buffers.ArrayPool<int>.Shared.Rent(uvWidthPairs);
        try
        {
            for (var x = 0; x < uvWidthPairs; x++)
                xMap[x] = (int)((long)x * srcUvWidthPairs / uvWidthPairs) * 2;

            var srcUv = source.Slice(sourcePixels);
            var destUv = destination.Slice(targetPixels);

            for (var y = 0; y < uvHeight; y++)
            {
                var srcRow = (int)((long)y * srcUvHeight / uvHeight) * sourceWidth;
                var destRow = y * targetWidth;
                for (var x = 0; x < uvWidthPairs; x++)
                {
                    var srcIdx = srcRow + xMap[x];
                    var destIdx = destRow + (x * 2);
                    destUv[destIdx] = srcUv[srcIdx];
                    destUv[destIdx + 1] = srcUv[srcIdx + 1];
                }
            }
        }
        finally
        {
            System.Buffers.ArrayPool<int>.Shared.Return(xMap);
        }
    }

    /// <summary>Re-packs planar YUV420 as NV12 while preserving the luma and chroma samples.</summary>
    public static void PlanarToNv12(ReadOnlySpan<byte> source, Span<byte> destination, int width, int height)
    {
        var size = BufferSize(width, height);
        if (source.Length < size || destination.Length < size)
            throw new ArgumentException("Invalid YUV420 or NV12 buffer length.");

        var pixels = checked(width * height);
        var chromaBytes = pixels / 4;
        source[..pixels].CopyTo(destination);
        var u = source.Slice(pixels, chromaBytes);
        var v = source.Slice(pixels + chromaBytes, chromaBytes);
        var uv = destination.Slice(pixels, chromaBytes * 2);
        for (var i = 0; i < chromaBytes; i++)
        {
            uv[i * 2] = u[i];
            uv[i * 2 + 1] = v[i];
        }
    }

    public static void FillBlack(Span<byte> frame, int width, int height)
    {
        var pixels = checked(width * height);
        if (frame.Length < BufferSize(width, height)) throw new ArgumentException("Invalid YUV420 buffer length.");
        frame.Slice(0, pixels).Fill(16);
        frame.Slice(pixels, pixels / 2).Fill(128);
    }

    private static byte Luma(int r, int g, int b) =>
        (byte)Math.Clamp(((66 * r + 129 * g + 25 * b + 128) >> 8) + 16, 0, 255);

    private static void ResizePlane(ReadOnlySpan<byte> source, int sourceWidth, int sourceHeight,
        Span<byte> destination, int targetWidth, int targetHeight)
    {
        var xMap = System.Buffers.ArrayPool<int>.Shared.Rent(targetWidth);
        try
        {
            for (var x = 0; x < targetWidth; x++)
                xMap[x] = (int)((long)x * sourceWidth / targetWidth);
            for (var y = 0; y < targetHeight; y++)
            {
                var sourceRow = (int)((long)y * sourceHeight / targetHeight) * sourceWidth;
                var targetRow = y * targetWidth;
                for (var x = 0; x < targetWidth; x++)
                    destination[targetRow + x] = source[sourceRow + xMap[x]];
            }
        }
        finally
        {
            System.Buffers.ArrayPool<int>.Shared.Return(xMap);
        }
    }
}
