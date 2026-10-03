using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AravalsStream.Core.Models;

public sealed class SourceTransform : INotifyPropertyChanged
{
    private double _x, _y, _rotation, _cropLeft, _cropTop, _cropRight, _cropBottom;
    private double _width = 400, _height = 225, _scaleX = 1, _scaleY = 1, _opacity = 1;
    private bool _visible = true, _locked;
    private VerticalLayoutMode _layoutMode = VerticalLayoutMode.Manual;
    private SmartVerticalTemplate _verticalTemplate = SmartVerticalTemplate.FullscreenCrop;
    private double _focusX = 0.5;
    private bool _backgroundEnlarged;

    public double X { get => _x; set => Set(ref _x, value); }
    public double Y { get => _y; set => Set(ref _y, value); }
    public double Width { get => _width; set => Set(ref _width, value); }
    public double Height { get => _height; set => Set(ref _height, value); }
    public double ScaleX { get => _scaleX; set => Set(ref _scaleX, value); }
    public double ScaleY { get => _scaleY; set => Set(ref _scaleY, value); }
    public double Rotation { get => _rotation; set => Set(ref _rotation, value); }
    public double CropLeft { get => _cropLeft; set => Set(ref _cropLeft, value); }
    public double CropTop { get => _cropTop; set => Set(ref _cropTop, value); }
    public double CropRight { get => _cropRight; set => Set(ref _cropRight, value); }
    public double CropBottom { get => _cropBottom; set => Set(ref _cropBottom, value); }
    public double Opacity { get => _opacity; set => Set(ref _opacity, value); }
    public bool Visible { get => _visible; set => Set(ref _visible, value); }
    public bool Locked { get => _locked; set => Set(ref _locked, value); }

    public VerticalLayoutMode LayoutMode { get => _layoutMode; set => Set(ref _layoutMode, value); }
    public SmartVerticalTemplate VerticalTemplate { get => _verticalTemplate; set => Set(ref _verticalTemplate, value); }
    public double FocusX { get => _focusX; set => Set(ref _focusX, value); }
    public bool BackgroundEnlarged { get => _backgroundEnlarged; set => Set(ref _backgroundEnlarged, value); }

    // Kept for compatibility with Phase 1 settings files.
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public double? Scale { get => null; set { if (value.HasValue) { ScaleX = value.Value; ScaleY = value.Value; } } }
    public event PropertyChangedEventHandler? PropertyChanged;

    public SourceTransform Clone() => new()
    {
        X = X,
        Y = Y,
        Width = Width,
        Height = Height,
        ScaleX = ScaleX,
        ScaleY = ScaleY,
        Rotation = Rotation,
        CropLeft = CropLeft,
        CropTop = CropTop,
        CropRight = CropRight,
        CropBottom = CropBottom,
        Opacity = Opacity,
        Visible = Visible,
        Locked = Locked,
        LayoutMode = LayoutMode,
        VerticalTemplate = VerticalTemplate,
        FocusX = FocusX,
        BackgroundEnlarged = BackgroundEnlarged
    };

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
