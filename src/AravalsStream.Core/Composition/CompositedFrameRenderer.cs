using AravalsStream.Core.Models;

namespace AravalsStream.Core.Composition;

public sealed record RawVideoFrame(int Width, int Height, int Stride, byte[] Pixels);

public sealed class CanvasRenderCache
{
    internal readonly List<(int X, int Y, int Width, int Height)> DirtyRects = [];
    internal int Width, Height;
    internal bool Initialized;
}

public static class CompositedFrameRenderer
{
    public static void Render(
        int targetWidth,
        int targetHeight,
        OutputMode mode,
        Scene? scene,
        Func<SceneSource, RawVideoFrame?> frameProvider,
        byte[] destination,
        int? logicalWidth = null,
        int? logicalHeight = null,
        Action<SourceType, long>? onSourceRendered = null,
        Action<long>? onCanvasCleared = null,
        CanvasRenderCache? renderCache = null)
    {
        int requiredSize = targetWidth * targetHeight * 4;
        if (destination.Length < requiredSize)
        {
            throw new ArgumentException($"Destination buffer too small. Required: {requiredSize}, Actual: {destination.Length}");
        }

        var firstVisible = scene?.Sources.FirstOrDefault(s => s.Visible);
        var firstFrame = firstVisible is null ? null : frameProvider(firstVisible);
        var firstTransform = mode == OutputMode.Vertical ? firstVisible?.VerticalTransform : firstVisible?.HorizontalTransform;
        double scaleX = (double)targetWidth / (logicalWidth ?? targetWidth);
        double scaleY = (double)targetHeight / (logicalHeight ?? targetHeight);
        var coversCanvas = firstVisible?.Type == SourceType.DisplayCapture && firstFrame is not null &&
            firstFrame.Width > 0 && firstFrame.Height > 0 &&
            firstFrame.Stride >= (long)firstFrame.Width * 4 &&
            firstFrame.Pixels.Length >= (long)firstFrame.Stride * firstFrame.Height &&
            firstTransform is not null && firstTransform.Opacity >= 0.999 &&
            Math.Round(firstTransform.X * scaleX) <= 0 && Math.Round(firstTransform.Y * scaleY) <= 0 &&
            Math.Round((firstTransform.X + firstTransform.Width) * scaleX) >= targetWidth &&
            Math.Round((firstTransform.Y + firstTransform.Height) * scaleY) >= targetHeight;
        if (renderCache is not null && (renderCache.Width != targetWidth || renderCache.Height != targetHeight))
        {
            renderCache.Width = targetWidth;
            renderCache.Height = targetHeight;
            renderCache.Initialized = false;
            renderCache.DirtyRects.Clear();
        }
        if (!coversCanvas)
        {
            var clearStart = onCanvasCleared is null ? 0 : System.Diagnostics.Stopwatch.GetTimestamp();
            if (renderCache?.Initialized == true)
            {
                foreach (var (x, y, width, height) in renderCache.DirtyRects)
                    for (var row = y; row < y + height; row++)
                        System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(
                            destination.AsSpan((row * targetWidth + x) * 4, width * 4)).Fill(0xFF000000U);
            }
            else
            {
                var span64 = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ulong>(destination.AsSpan(0, requiredSize));
                span64.Fill(0xFF000000FF000000UL);
            }
            if (onCanvasCleared is not null)
                onCanvasCleared(System.Diagnostics.Stopwatch.GetTimestamp() - clearStart);
        }
        if (renderCache is not null)
        {
            renderCache.DirtyRects.Clear();
            renderCache.Initialized = true;
        }

        if (scene is null || scene.Sources.Count == 0) return;
        double layoutScaleX = (double)targetWidth / (logicalWidth ?? targetWidth);
        double layoutScaleY = (double)targetHeight / (logicalHeight ?? targetHeight);

        // Render sources in order (bottom to top)
        foreach (var source in scene.Sources)
        {
            if (!source.Visible) continue;

            var frame = ReferenceEquals(source, firstVisible) ? firstFrame : frameProvider(source);
            if (frame is null || frame.Width <= 0 || frame.Height <= 0 ||
                frame.Stride < (long)frame.Width * 4 || frame.Pixels.Length < (long)frame.Stride * frame.Height)
                continue;

            var transform = mode == OutputMode.Vertical ? source.VerticalTransform : source.HorizontalTransform;
            if (transform.Width <= 0 || transform.Height <= 0) continue;
            var sourceStart = onSourceRendered is null ? 0 : System.Diagnostics.Stopwatch.GetTimestamp();

            // Handle Smart Vertical enlarged background
            if (mode == OutputMode.Vertical && transform.BackgroundEnlarged)
            {
                RenderEnlargedBackground(frame, targetWidth, targetHeight, destination);
            }

            RenderSource(frame, source.Type, transform, targetWidth, targetHeight, destination, layoutScaleX, layoutScaleY);
            if (renderCache is not null && transform.Opacity > 0.001 && transform.Width > 0 && transform.Height > 0)
            {
                var left = Math.Clamp((int)Math.Round(transform.X * layoutScaleX), 0, targetWidth);
                var top = Math.Clamp((int)Math.Round(transform.Y * layoutScaleY), 0, targetHeight);
                var right = Math.Clamp((int)Math.Round((transform.X + transform.Width) * layoutScaleX), 0, targetWidth);
                var bottom = Math.Clamp((int)Math.Round((transform.Y + transform.Height) * layoutScaleY), 0, targetHeight);
                if (right > left && bottom > top) renderCache.DirtyRects.Add((left, top, right - left, bottom - top));
            }
            if (renderCache is not null && mode == OutputMode.Vertical && transform.BackgroundEnlarged)
                renderCache.DirtyRects.Add((0, 0, targetWidth, targetHeight));
            if (onSourceRendered is not null)
                onSourceRendered(source.Type, System.Diagnostics.Stopwatch.GetTimestamp() - sourceStart);
        }
    }

    private static void RenderEnlargedBackground(
        RawVideoFrame frame,
        int targetWidth,
        int targetHeight,
        byte[] destination)
    {
        // Aspect Fill 1080x1920 with dim opacity (0.35)
        double scaleX = (double)targetWidth / frame.Width;
        double scaleY = (double)targetHeight / frame.Height;
        double scale = Math.Max(scaleX, scaleY);

        double renderW = frame.Width * scale;
        double renderH = frame.Height * scale;
        double renderX = (targetWidth - renderW) / 2.0;
        double renderY = (targetHeight - renderH) / 2.0;

        BlitScaled(
            frame.Pixels,
            frame.Width,
            frame.Height,
            frame.Stride,
            0, 0, frame.Width, frame.Height,
            destination,
            targetWidth,
            targetHeight,
            (int)renderX,
            (int)renderY,
            (int)renderW,
            (int)renderH,
            0.35f);
    }

    private static void RenderSource(
        RawVideoFrame frame,
        SourceType sourceType,
        SourceTransform transform,
        int targetWidth,
        int targetHeight,
        byte[] destination,
        double layoutScaleX,
        double layoutScaleY)
    {
        // Calculate cropped source bounds
        int cropL = Math.Clamp((int)transform.CropLeft, 0, frame.Width - 1);
        int cropT = Math.Clamp((int)transform.CropTop, 0, frame.Height - 1);
        int cropR = Math.Clamp((int)transform.CropRight, 0, frame.Width - cropL - 1);
        int cropB = Math.Clamp((int)transform.CropBottom, 0, frame.Height - cropT - 1);

        int srcW = frame.Width - cropL - cropR;
        int srcH = frame.Height - cropT - cropB;
        if (srcW <= 0 || srcH <= 0) return;

        int destX = (int)Math.Round(transform.X * layoutScaleX);
        int destY = (int)Math.Round(transform.Y * layoutScaleY);
        int destW = (int)Math.Round(transform.Width * layoutScaleX);
        int destH = (int)Math.Round(transform.Height * layoutScaleY);
        float opacity = Math.Clamp((float)transform.Opacity, 0f, 1f);

        if (opacity <= 0.001f) return;

        BlitScaled(
            frame.Pixels,
            frame.Width,
            frame.Height,
            frame.Stride,
            cropL,
            cropT,
            srcW,
            srcH,
            destination,
            targetWidth,
            targetHeight,
            destX,
            destY,
            destW,
            destH,
            opacity,
            sourceType == SourceType.DisplayCapture);
    }

    private static void BlitScaled(
        byte[] srcPixels,
        int srcTotalW,
        int srcTotalH,
        int srcStride,
        int srcCropX,
        int srcCropY,
        int srcCropW,
        int srcCropH,
        byte[] destPixels,
        int destCanvasW,
        int destCanvasH,
        int destX,
        int destY,
        int destW,
        int destH,
        float opacity,
        bool opaqueSource = false)
    {
        if (destW <= 0 || destH <= 0 || srcCropW <= 0 || srcCropH <= 0) return;

        // Clip destination rectangle against canvas bounds
        int clipDestStartX = Math.Max(0, destX);
        int clipDestStartY = Math.Max(0, destY);
        int clipDestEndX = Math.Min(destCanvasW, destX + destW);
        int clipDestEndY = Math.Min(destCanvasH, destY + destH);

        if (clipDestStartX >= clipDestEndX || clipDestStartY >= clipDestEndY) return;

        if (opaqueSource && opacity >= 0.999f && srcCropW == destW && srcCropH == destH)
        {
            var rowBytes = (clipDestEndX - clipDestStartX) * 4;
            for (var row = clipDestStartY; row < clipDestEndY; row++)
            {
                var srcRow = srcCropY + row - destY;
                var srcCol = srcCropX + clipDestStartX - destX;
                Buffer.BlockCopy(srcPixels, srcRow * srcStride + srcCol * 4,
                    destPixels, (row * destCanvasW + clipDestStartX) * 4, rowBytes);
            }
            return;
        }

        double scaleX = (double)srcCropW / destW;
        double scaleY = (double)srcCropH / destH;

        bool isOpaque = opacity >= 0.999f;
        int alphaInt = (int)(opacity * 256);

        int destStride = destCanvasW * 4;

        var clippedWidth = clipDestEndX - clipDestStartX;
        var xMap = System.Buffers.ArrayPool<int>.Shared.Rent(clippedWidth);
        for (var col = 0; col < clippedWidth; col++)
            xMap[col] = srcCropX + Math.Clamp((int)((clipDestStartX + col - destX) * scaleX), 0, srcCropW - 1);

        void BlitRows(int firstRow, int lastRow)
        {
            for (int destRow = firstRow; destRow < lastRow; destRow++)
            {
                int relY = destRow - destY;
                int srcY = srcCropY + Math.Clamp((int)(relY * scaleY), 0, srcCropH - 1);
                int srcRowOffset = srcY * srcStride;
                int destRowOffset = destRow * destStride;
                for (int destCol = clipDestStartX; destCol < clipDestEndX; destCol++)
                {
                    int srcX = xMap[destCol - clipDestStartX];
                    int srcIdx = srcRowOffset + (srcX * 4);
                    int destIdx = destRowOffset + (destCol * 4);

                    byte b = srcPixels[srcIdx];
                    byte g = srcPixels[srcIdx + 1];
                    byte r = srcPixels[srcIdx + 2];
                    byte a = srcPixels[srcIdx + 3];

                    if (opaqueSource || isOpaque && a >= 250)
                    {
                        destPixels[destIdx] = b;
                        destPixels[destIdx + 1] = g;
                        destPixels[destIdx + 2] = r;
                        destPixels[destIdx + 3] = 255;
                    }
                    else
                    {
                        int effectiveAlpha = (a * alphaInt) >> 8;
                        int invAlpha = 256 - effectiveAlpha;

                        destPixels[destIdx] = (byte)((b * effectiveAlpha + destPixels[destIdx] * invAlpha) >> 8);
                        destPixels[destIdx + 1] = (byte)((g * effectiveAlpha + destPixels[destIdx + 1] * invAlpha) >> 8);
                        destPixels[destIdx + 2] = (byte)((r * effectiveAlpha + destPixels[destIdx + 2] * invAlpha) >> 8);
                        destPixels[destIdx + 3] = 255;
                    }
                }
            }
        }

        try
        {
            var rows = clipDestEndY - clipDestStartY;
            var pixels = (long)rows * clippedWidth;
            if (pixels < 500_000 || Environment.ProcessorCount < 4)
                BlitRows(clipDestStartY, clipDestEndY);
            else
            {
                var workers = Math.Min(Environment.ProcessorCount, Math.Max(1, rows / 128));
                Parallel.For(0, workers, worker =>
                {
                    var first = clipDestStartY + worker * rows / workers;
                    var last = clipDestStartY + (worker + 1) * rows / workers;
                    BlitRows(first, last);
                });
            }
        }
        finally { System.Buffers.ArrayPool<int>.Shared.Return(xMap); }
    }
}
