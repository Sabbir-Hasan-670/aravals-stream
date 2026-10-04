using AravalsStream.Core.Models;

namespace AravalsStream.Core.Services;

public static class CanvasLayout
{
    public const int HorizontalWidth = 1920, HorizontalHeight = 1080;
    public const int VerticalWidth = 1080, VerticalHeight = 1920;

    private static AravalsStream.Core.Settings.CanvasSettings _settings = new();

    public static void Configure(AravalsStream.Core.Settings.CanvasSettings settings)
    {
        if (!settings.IsValid) throw new ArgumentException("Canvas dimensions must be even numbers from 64 to 4096.", nameof(settings));
        Volatile.Write(ref _settings, settings with { });
    }

    public static (int Width, int Height) Size(OutputMode mode)
    {
        var settings = Volatile.Read(ref _settings);
        return mode == OutputMode.Vertical
            ? (settings.VerticalWidth, settings.VerticalHeight) : (settings.HorizontalWidth, settings.HorizontalHeight);
    }

    public static (double X, double Y) PreviewToCanvas(double previewX, double previewY, double previewWidth, double previewHeight, OutputMode mode)
    {
        var (width, height) = Size(mode);
        return (previewX * width / previewWidth, previewY * height / previewHeight);
    }

    public static void Fit(SourceTransform transform, double sourceWidth, double sourceHeight, OutputMode mode)
    {
        var (width, height) = Size(mode);
        var scale = Math.Min(width / sourceWidth, height / sourceHeight);
        transform.Width = sourceWidth * scale;
        transform.Height = sourceHeight * scale;
        Center(transform, mode);
    }

    public static void FitEntire(SourceTransform transform, double sourceWidth, double sourceHeight, OutputMode mode)
    {
        Fit(transform, sourceWidth, sourceHeight, mode);
        transform.LayoutMode = VerticalLayoutMode.FitEntire;
        transform.BackgroundEnlarged = false;
    }

    public static void FillCanvas(SourceTransform transform, double sourceWidth, double sourceHeight, OutputMode mode, double focusX = 0.5)
    {
        var (width, height) = Size(mode);
        var scale = Math.Max(width / sourceWidth, height / sourceHeight);
        transform.Width = sourceWidth * scale;
        transform.Height = sourceHeight * scale;
        transform.Y = (height - transform.Height) / 2;

        var overflowX = transform.Width - width;
        transform.FocusX = Math.Clamp(focusX, 0.0, 1.0);
        transform.X = overflowX > 0 ? -overflowX * transform.FocusX : (width - transform.Width) / 2;

        transform.LayoutMode = VerticalLayoutMode.FillCanvas;
        transform.BackgroundEnlarged = false;
    }

    public static void SmartVertical(SourceTransform transform, double sourceWidth, double sourceHeight, SmartVerticalTemplate template = SmartVerticalTemplate.FullscreenCrop, double focusX = 0.5)
    {
        transform.VerticalTemplate = template;
        if (template == SmartVerticalTemplate.BackgroundAndFullSource)
        {
            FitEntire(transform, sourceWidth, sourceHeight, OutputMode.Vertical);
            transform.LayoutMode = VerticalLayoutMode.SmartVertical;
            transform.BackgroundEnlarged = true;
        }
        else
        {
            FillCanvas(transform, sourceWidth, sourceHeight, OutputMode.Vertical, focusX);
            transform.LayoutMode = VerticalLayoutMode.SmartVertical;
            transform.BackgroundEnlarged = false;
        }
    }

    public static double CalculateFocusX(double currentX, double scaledWidth, double canvasWidth)
    {
        var overflow = scaledWidth - canvasWidth;
        if (overflow <= 0) return 0.5;
        return Math.Clamp(-currentX / overflow, 0.0, 1.0);
    }

    public static void ApplyFocusX(SourceTransform transform, OutputMode mode)
    {
        var (width, _) = Size(mode);
        var overflow = transform.Width - width;
        if (overflow > 0)
        {
            transform.X = -overflow * Math.Clamp(transform.FocusX, 0.0, 1.0);
        }
    }

    public static void Stretch(SourceTransform transform, OutputMode mode)
    {
        var (width, height) = Size(mode);
        transform.X = 0; transform.Y = 0;
        transform.Width = width; transform.Height = height;
        transform.LayoutMode = VerticalLayoutMode.Manual;
        transform.BackgroundEnlarged = false;
    }

    public static void Center(SourceTransform transform, OutputMode mode)
    {
        var (width, height) = Size(mode);
        transform.X = (width - transform.Width) / 2;
        transform.Y = (height - transform.Height) / 2;
    }

    public static void CopyLayout(SourceTransform from, SourceTransform to, OutputMode fromMode, OutputMode toMode)
    {
        var (fw, fh) = Size(fromMode);
        var (tw, th) = Size(toMode);
        to.X = from.X * tw / fw;
        to.Y = from.Y * th / fh;
        to.Width = from.Width * tw / fw;
        to.Height = from.Height * th / fh;
        to.ScaleX = from.ScaleX;
        to.ScaleY = from.ScaleY;
        to.Rotation = from.Rotation;
        to.Opacity = from.Opacity;
        to.CropLeft = from.CropLeft;
        to.CropTop = from.CropTop;
        to.CropRight = from.CropRight;
        to.CropBottom = from.CropBottom;
        to.Visible = from.Visible;
        to.Locked = from.Locked;
        to.LayoutMode = from.LayoutMode;
        to.VerticalTemplate = from.VerticalTemplate;
        to.FocusX = from.FocusX;
        to.BackgroundEnlarged = from.BackgroundEnlarged;
    }

    public static void SmartCopyLayout(SourceTransform from, SourceTransform to, OutputMode fromMode, OutputMode toMode)
    {
        var (tw, th) = Size(toMode);
        if (fromMode == toMode)
        {
            to.X = from.X;
            to.Y = from.Y;
            to.Width = from.Width;
            to.Height = from.Height;
        }
        else if (from.Width > 0 && from.Height > 0)
        {
            var scale = Math.Min(tw / from.Width, th / from.Height);
            to.Width = from.Width * scale;
            to.Height = from.Height * scale;
            to.X = (tw - to.Width) / 2;
            to.Y = (th - to.Height) / 2;
        }
        to.ScaleX = from.ScaleX;
        to.ScaleY = from.ScaleY;
        to.Rotation = from.Rotation;
        to.Opacity = from.Opacity;
        to.CropLeft = from.CropLeft;
        to.CropTop = from.CropTop;
        to.CropRight = from.CropRight;
        to.CropBottom = from.CropBottom;
        to.Visible = from.Visible;
        to.Locked = from.Locked;
        to.LayoutMode = from.LayoutMode;
        to.VerticalTemplate = from.VerticalTemplate;
        to.FocusX = from.FocusX;
        to.BackgroundEnlarged = from.BackgroundEnlarged;
    }
}
