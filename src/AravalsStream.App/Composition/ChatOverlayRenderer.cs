using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AravalsStream.Core.Alerts;
using AravalsStream.Core.Composition;
using AravalsStream.Core.Models;

namespace AravalsStream.App.Composition;

public static class ChatOverlayRenderer
{
    public static RawVideoFrame Render(IEnumerable<ChatMessage> history, ChatOverlaySettings settings, DateTimeOffset now)
    {
        const int width = 650, height = 420;
        var pixels = new byte[width * height * 4];
        return RenderInto(history, settings, now, pixels);
    }

    public static RawVideoFrame RenderInto(IEnumerable<ChatMessage> history, ChatOverlaySettings settings,
        DateTimeOffset now, byte[] pixels)
    {
        const int width = 650, height = 420;
        if (pixels.Length < width * height * 4) throw new ArgumentException("Chat frame buffer is too small.", nameof(pixels));
        Array.Clear(pixels, 0, width * height * 4);
        var messages = history.Where(m => ChatOverlayFilter.ShouldShow(m, settings, now))
            .TakeLast(Math.Clamp(settings.MaxMessages, 1, 10)).ToArray();
        if (messages.Length == 0) return new(width, height, width * 4, pixels);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            int fontSize = Math.Clamp(settings.FontSize, 12, 44);
            int rowHeight = Math.Max(55, fontSize * 2 + 12);
            int y = height - messages.Length * rowHeight;
            foreach (var message in messages)
            {
                var age = Math.Max(0, (now - message.Timestamp).TotalSeconds);
                var remaining = Math.Clamp((settings.MessageDurationSeconds - age) / 1.0, 0, 1);
                var rowOpacity = settings.Animation == AlertAnimation.Fade ? remaining : 1;
                dc.PushOpacity(rowOpacity);
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(
                    (byte)(255 * Math.Clamp(settings.BackgroundOpacity, 0, 1)), 17, 29, 44)),
                    null, new Rect(0, y + 2, width, rowHeight - 4), 10, 10);
                var badge = settings.ShowBadges && message.Badges.Count > 0
                    ? " [" + string.Join(",", message.Badges.Take(3)) + "]" : "";
                var label = (settings.ShowPlatformIcon ? $"[{message.Platform}] " : "") +
                    message.AuthorName + badge + ": " + message.Text;
                if (settings.ShowTimestamps) label = message.Timestamp.ToLocalTime().ToString("HH:mm") + " " + label;
                var text = new FormattedText(label.Length > 240 ? label[..240] : label,
                    CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                    new Typeface("Segoe UI"), fontSize, Brushes.White, 1)
                { MaxTextWidth = width - 26, MaxTextHeight = rowHeight - 10, Trimming = TextTrimming.CharacterEllipsis };
                var x = 12;
                if (settings.ShowAvatars)
                {
                    dc.DrawEllipse(Brushes.Teal, null, new Point(23, y + rowHeight / 2.0), 16, 16);
                    var initial = new FormattedText(message.AuthorName.FirstOrDefault().ToString(),
                        CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 18,
                        Brushes.White, 1);
                    dc.DrawText(initial, new Point(23 - initial.Width / 2, y + rowHeight / 2.0 - initial.Height / 2));
                    x = 45;
                }
                text.MaxTextWidth = width - x - 12;
                dc.DrawText(text, new Point(x, y + 6));
                dc.Pop();
                y += rowHeight;
            }
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual); bitmap.CopyPixels(pixels, width * 4, 0);
        for (int i = 0; i < pixels.Length; i += 4)
        {
            int a = pixels[i + 3];
            if (a is 0 or 255) continue;
            pixels[i] = (byte)Math.Min(255, pixels[i] * 255 / a);
            pixels[i + 1] = (byte)Math.Min(255, pixels[i + 1] * 255 / a);
            pixels[i + 2] = (byte)Math.Min(255, pixels[i + 2] * 255 / a);
        }
        return new(width, height, width * 4, pixels);
    }
}
