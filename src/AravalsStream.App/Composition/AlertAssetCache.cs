using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AravalsStream.Core.Services;

namespace AravalsStream.App.Composition;

public sealed class AlertAssetCache
{
    private sealed record Asset(BitmapSource[] Frames, DateTimeOffset Loaded);
    private readonly Dictionary<string, Asset?> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _order = new();

    public BitmapSource? Get(string? path, TimeSpan elapsed)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (!_cache.TryGetValue(path, out var asset))
        {
            asset = Load(path);
            _cache[path] = asset;
            _order.Enqueue(path);
            while (_order.Count > 4) _cache.Remove(_order.Dequeue());
        }
        if (asset?.Frames.Length is not > 0) return null;
        int index = (int)(Math.Max(0, elapsed.TotalMilliseconds) / 100) % asset.Frames.Length;
        return asset.Frames[index];
    }

    private static Asset? Load(string path)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 20 * 1024 * 1024) return null;
            if (!new[] { ".png", ".jpg", ".jpeg", ".gif" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)) return null;
            using var stream = File.OpenRead(path);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var frames = new List<BitmapSource>();
            long pixels = 0;
            foreach (var frame in decoder.Frames.Take(60))
            {
                if (frame.PixelWidth > 4096 || frame.PixelHeight > 4096) break;
                var scale = Math.Min(1.0, 480.0 / Math.Max(frame.PixelWidth, frame.PixelHeight));
                var resized = new TransformedBitmap(frame, new ScaleTransform(scale, scale));
                resized.Freeze();
                pixels += (long)resized.PixelWidth * resized.PixelHeight;
                if (pixels > 4_000_000) break;
                frames.Add(resized);
            }
            return frames.Count == 0 ? null : new Asset(frames.ToArray(), DateTimeOffset.UtcNow);
        }
        catch (Exception ex) { AppLog.Write("AlertAsset", $"Asset skipped: {ex.Message}"); return null; }
    }
}
