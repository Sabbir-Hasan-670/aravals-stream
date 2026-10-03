using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AravalsStream.Core.Alerts;
using AravalsStream.Core.Composition;
using AravalsStream.Core.Models;

namespace AravalsStream.App.Composition;

// WPF creates a small transparent bitmap on the UI thread. The encoder only reads immutable pixels.
public static class AlertOverlayRenderer
{
    private static readonly AlertAssetCache Assets = new();
    public static RawVideoFrame Render(AlertInstance? alert, OutputMode mode, DateTimeOffset now)
    {
        int width = mode == OutputMode.Vertical ? 900 : 700;
        int height = mode == OutputMode.Vertical ? 240 : 180;
        var pixels = new byte[width * height * 4];
        return RenderInto(alert, mode, now, pixels);
    }

    public static RawVideoFrame RenderInto(AlertInstance? alert, OutputMode mode, DateTimeOffset now, byte[] pixels)
    {
        int width = mode == OutputMode.Vertical ? 900 : 700;
        int height = mode == OutputMode.Vertical ? 240 : 180;
        if (pixels.Length < width * height * 4) throw new ArgumentException("Alert frame buffer is too small.", nameof(pixels));
        Array.Clear(pixels, 0, width * height * 4);
        if (alert == null || (mode == OutputMode.Vertical && !alert.Definition.ShowVertical) ||
            (mode == OutputMode.Horizontal && !alert.Definition.ShowHorizontal))
            return new RawVideoFrame(width, height, width * 4, pixels);

        var opacity = alert.OpacityAt(now);
        if (opacity <= 0) return new RawVideoFrame(width, height, width * 4, pixels);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushOpacity(opacity);
            bool transformed = false;
            var offset = 1 - opacity;
            switch (alert.Definition.Animation)
            {
                case AlertAnimation.SlideDown:
                    dc.PushTransform(new TranslateTransform(0, -height * offset)); transformed = true; break;
                case AlertAnimation.SlideUp:
                    dc.PushTransform(new TranslateTransform(0, height * offset)); transformed = true; break;
                case AlertAnimation.Zoom:
                case AlertAnimation.Pop:
                    var scale = alert.Definition.Animation == AlertAnimation.Pop
                        ? 1 - .35 * Math.Sqrt(offset) : 1 - .15 * offset;
                    dc.PushTransform(new ScaleTransform(scale, scale, width / 2.0, height / 2.0)); transformed = true; break;
            }
            var background = new SolidColorBrush(Color.FromArgb(
                (byte)(255 * Math.Clamp(alert.Definition.BackgroundOpacity, 0, 1)), 17, 29, 44));
            dc.DrawRoundedRectangle(background, new Pen(Brushes.MediumTurquoise, 3),
                new Rect(2, 2, width - 4, height - 4), alert.Definition.CornerRadius, alert.Definition.CornerRadius);
            var asset = Assets.Get(alert.Definition.ImagePath, now - alert.StartedAt);
            if (asset != null)
            {
                var size = Math.Min(height - 32, 140);
                dc.DrawImage(asset, new Rect(18, (height - size) / 2.0, size, size));
            }
            var font = new FontFamily(alert.Definition.FontFamily);
            DrawText(dc, alert.Title, font, Math.Clamp(alert.Definition.FontSize, 14, 72),
                Brushes.White, width, height * .24, asset != null ? 160 : 0,
                alert.Definition.TextAlignment, alert.Definition.Padding);
            DrawText(dc, alert.Message, font, Math.Clamp(alert.Definition.FontSize * .7, 12, 54),
                Brushes.LightCyan, width, height * .57, asset != null ? 160 : 0,
                alert.Definition.TextAlignment, alert.Definition.Padding);
            if (transformed) dc.Pop();
            dc.Pop();
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.CopyPixels(pixels, width * 4, 0);
        // The software compositor expects straight alpha; WPF returns premultiplied BGRA.
        for (int i = 0; i < pixels.Length; i += 4)
        {
            int a = pixels[i + 3];
            if (a is 0 or 255) continue;
            pixels[i] = (byte)Math.Min(255, pixels[i] * 255 / a);
            pixels[i + 1] = (byte)Math.Min(255, pixels[i + 1] * 255 / a);
            pixels[i + 2] = (byte)Math.Min(255, pixels[i + 2] * 255 / a);
        }
        return new RawVideoFrame(width, height, width * 4, pixels);
    }

    private static void DrawText(DrawingContext dc, string value, FontFamily font, double size,
        Brush brush, int width, double centerY, int inset, string alignment, double padding)
    {
        var text = new FormattedText(value.Length > 160 ? value[..160] : value, CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight, new Typeface(font, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
            size, brush, 1);
        var side = Math.Clamp(padding, 4, 100);
        text.MaxTextWidth = width - inset - side * 2;
        text.MaxTextHeight = size * 2.4;
        text.Trimming = TextTrimming.CharacterEllipsis;
        text.TextAlignment = Enum.TryParse<TextAlignment>(alignment, true, out var parsed)
            ? parsed : TextAlignment.Center;
        dc.DrawText(text, new Point(side + inset, centerY - text.Height / 2));
    }
}
