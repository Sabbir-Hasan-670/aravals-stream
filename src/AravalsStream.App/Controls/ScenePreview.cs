using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AravalsStream.App.Composition;
using AravalsStream.Core.Models;
using AravalsStream.Core.Services;

namespace AravalsStream.App.Controls;

public sealed class ScenePreview : UserControl
{
    private sealed record ItemVisual(SceneSource Source, Border Border, ImageBrush Brush, Border? BgBorder = null, ImageBrush? BgBrush = null);
    
    private readonly Canvas _videoCanvas = new() { Background = Brushes.Black, ClipToBounds = true };
    private readonly Viewbox _viewbox = new() { Stretch = Stretch.Uniform };
    private readonly Canvas _adornerCanvas = new() { Background = null, ClipToBounds = false };
    private readonly List<ItemVisual> _items = [];
    private readonly Dictionary<string, FrameworkElement> _handleElements = [];
    
    private Border? _selectionBorder;
    private Border? _lockBadge;
    private OutputMode _mode;
    private ISceneCompositor? _compositor;
    private Scene? _scene;
    private SceneSource? _selected;
    private SceneSource? _dragged;
    private string? _handle;
    private Point _startLogicalPoint;
    private (double X, double Y, double Width, double Height) _start;
    private bool _showSafeArea;

    // Handles are constant DIP size: 12 DIP visual box with 20 DIP hit target
    private const double HandleVisualSize = 12;
    private const double HandleHitSize = 22;
    private const double HalfHit = HandleHitSize / 2;

    public bool ShowSafeArea
    {
        get => _showSafeArea;
        set { _showSafeArea = value; Refresh(); }
    }

    public void ToggleSafeArea() => ShowSafeArea = !ShowSafeArea;

    public event Action<SceneSource>? SourceSelected;
    public event Action? TransformChanged;
    public event Action<SceneSource, Point>? ContextMenuRequested;
    public event Action? DoubleClicked;

    public ScenePreview() : this(OutputMode.Horizontal) { }

    public ScenePreview(OutputMode mode)
    {
        _mode = mode;
        var (width, height) = CanvasLayout.Size(mode);
        _videoCanvas.Width = width;
        _videoCanvas.Height = height;
        _viewbox.Child = _videoCanvas;

        var rootGrid = new Grid();
        rootGrid.Children.Add(_viewbox);
        rootGrid.Children.Add(_adornerCanvas);
        Content = rootGrid;

        _adornerCanvas.MouseLeftButtonDown += AdornerCanvas_MouseLeftButtonDown;
        _adornerCanvas.MouseMove += AdornerCanvas_MouseMove;
        _adornerCanvas.MouseLeftButtonUp += AdornerCanvas_MouseLeftButtonUp;
        _adornerCanvas.LostMouseCapture += (_, _) => _dragged = null;

        SizeChanged += (_, _) => Dispatcher.BeginInvoke(UpdateAdornerPositions, System.Windows.Threading.DispatcherPriority.Loaded);
        Loaded += (_, _) => Dispatcher.BeginInvoke(UpdateAdornerPositions, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    protected override Size MeasureOverride(Size constraint)
    {
        base.MeasureOverride(constraint);
        return new Size(0, 0);
    }

    public void SetMode(OutputMode mode)
    {
        _mode = mode;
        var (width, height) = CanvasLayout.Size(mode);
        _videoCanvas.Width = width;
        _videoCanvas.Height = height;
        Refresh();
    }

    public void Bind(ISceneCompositor compositor)
    {
        if (_compositor is not null) _compositor.FrameReady -= OnFrameReady;
        _compositor = compositor;
        _compositor.FrameReady += OnFrameReady;
        Refresh();
    }

    public void SetScene(Scene? scene) { _scene = scene; Refresh(); }
    
    public void SetSelected(SceneSource? source)
    {
        _selected = source?.CanTransform == true ? source : null;
        UpdateAdornerPositions();
    }

    public void Refresh()
    {
        _videoCanvas.Children.Clear();
        _items.Clear();

        if (_scene is null || _compositor is null)
        {
            _adornerCanvas.Children.Clear();
            _handleElements.Clear();
            return;
        }

        foreach (var source in _scene.Sources)
        {
            if (!source.HasVideo || source.Type is not (SourceType.DisplayCapture or SourceType.WindowCapture or SourceType.Camera or SourceType.CaptureDevice or SourceType.RemotePc or SourceType.Alerts or SourceType.ChatOverlay) || !source.Visible) continue;
            var transform = Transform(source);
            if (!transform.Visible) continue;

            Border? bgBorder = null;
            ImageBrush? bgBrush = null;

            if (_mode == OutputMode.Vertical && transform.BackgroundEnlarged)
            {
                bgBrush = new ImageBrush
                {
                    Stretch = Stretch.UniformToFill,
                    AlignmentX = AlignmentX.Center,
                    AlignmentY = AlignmentY.Center
                };
                bgBorder = new Border
                {
                    Width = _videoCanvas.Width,
                    Height = _videoCanvas.Height,
                    Background = bgBrush,
                    Opacity = 0.35,
                    IsHitTestVisible = false,
                    Effect = new System.Windows.Media.Effects.BlurEffect
                    {
                        Radius = 24,
                        RenderingBias = System.Windows.Media.Effects.RenderingBias.Performance
                    }
                };
                Canvas.SetLeft(bgBorder, 0);
                Canvas.SetTop(bgBorder, 0);
                _videoCanvas.Children.Add(bgBorder);
            }

            var brush = new ImageBrush { Stretch = Stretch.Fill, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top };
            var border = new Border
            {
                Background = brush,
                BorderBrush = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Opacity = Math.Clamp(transform.Opacity, 0, 1),
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new RotateTransform(transform.Rotation),
                Tag = source
            };

            border.MouseLeftButtonDown += Source_MouseLeftButtonDown;
            border.MouseRightButtonUp += (s, e) =>
            {
                SourceSelected?.Invoke(source);
                ContextMenuRequested?.Invoke(source, e.GetPosition(this));
                e.Handled = true;
            };

            var visual = new ItemVisual(source, border, brush, bgBorder, bgBrush);
            _items.Add(visual);
            UpdateFrame(visual);
            Position(visual);
            _videoCanvas.Children.Add(border);
        }

        RenderSafeAreaOverlay();
        Dispatcher.BeginInvoke(UpdateAdornerPositions, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void RenderSafeAreaOverlay()
    {
        if (_mode != OutputMode.Vertical || !_showSafeArea) return;

        var safeAreaCanvas = new Canvas
        {
            Width = _videoCanvas.Width,
            Height = _videoCanvas.Height,
            IsHitTestVisible = false
        };

        const double topMargin = 160;
        const double bottomMargin = 360;
        const double rightMargin = 130;
        const double leftMargin = 40;

        double safeW = _videoCanvas.Width - leftMargin - rightMargin;
        double safeH = _videoCanvas.Height - topMargin - bottomMargin;

        var topShade = new Border { Width = _videoCanvas.Width, Height = topMargin, Background = new SolidColorBrush(Color.FromArgb(45, 255, 60, 60)) };
        Canvas.SetLeft(topShade, 0); Canvas.SetTop(topShade, 0);
        safeAreaCanvas.Children.Add(topShade);

        var bottomShade = new Border { Width = _videoCanvas.Width, Height = bottomMargin, Background = new SolidColorBrush(Color.FromArgb(45, 255, 60, 60)) };
        Canvas.SetLeft(bottomShade, 0); Canvas.SetTop(bottomShade, _videoCanvas.Height - bottomMargin);
        safeAreaCanvas.Children.Add(bottomShade);

        var rightShade = new Border { Width = rightMargin, Height = safeH, Background = new SolidColorBrush(Color.FromArgb(35, 255, 140, 0)) };
        Canvas.SetLeft(rightShade, _videoCanvas.Width - rightMargin); Canvas.SetTop(rightShade, topMargin);
        safeAreaCanvas.Children.Add(rightShade);

        var safeRect = new Border { Width = safeW, Height = safeH, BorderBrush = new SolidColorBrush(Color.FromArgb(200, 0, 229, 255)), BorderThickness = new Thickness(2), Background = Brushes.Transparent };
        Canvas.SetLeft(safeRect, leftMargin); Canvas.SetTop(safeRect, topMargin);
        safeAreaCanvas.Children.Add(safeRect);

        _videoCanvas.Children.Add(safeAreaCanvas);
    }

    private SourceTransform Transform(SceneSource source) => _mode == OutputMode.Vertical ? source.VerticalTransform : source.HorizontalTransform;

    private void UpdateFrame(ItemVisual visual)
    {
        var bitmap = visual.Source.Type is SourceType.Alerts or SourceType.ChatOverlay && _compositor is SceneCompositor sceneCompositor
            ? sceneCompositor.OverlayFrameFor(visual.Source.Type, _mode) : _compositor?.FrameFor(visual.Source);
        if (bitmap is null) return;
        visual.Brush.ImageSource = bitmap;
        if (visual.BgBrush is not null) visual.BgBrush.ImageSource = bitmap;

        var t = Transform(visual.Source);
        var left = Math.Clamp(t.CropLeft, 0, bitmap.PixelWidth - 1);
        var top = Math.Clamp(t.CropTop, 0, bitmap.PixelHeight - 1);
        var width = Math.Max(1, bitmap.PixelWidth - left - Math.Max(0, t.CropRight));
        var height = Math.Max(1, bitmap.PixelHeight - top - Math.Max(0, t.CropBottom));
        visual.Brush.ViewboxUnits = BrushMappingMode.Absolute;
        visual.Brush.Viewbox = new Rect(left, top, width, height);
    }

    private void OnFrameReady(string id)
    {
        foreach (var visual in _items.Where(i => _compositor?.KeyFor(i.Source) == id && (i.Brush.ImageSource is null || (i.BgBrush is not null && i.BgBrush.ImageSource is null))))
            UpdateFrame(visual);
    }

    private void Position(ItemVisual visual)
    {
        var t = Transform(visual.Source);
        visual.Border.Width = Math.Max(1, t.Width * Math.Max(0.01, t.ScaleX));
        visual.Border.Height = Math.Max(1, t.Height * Math.Max(0.01, t.ScaleY));
        visual.Border.Opacity = Math.Clamp(t.Opacity, 0, 1);
        visual.Border.RenderTransform = new RotateTransform(t.Rotation);
        Canvas.SetLeft(visual.Border, t.X);
        Canvas.SetTop(visual.Border, t.Y);
    }

    private Rect CalculateScreenRect(SourceTransform t)
    {
        try
        {
            if (!_videoCanvas.IsVisible || _adornerCanvas.ActualWidth <= 0 || _adornerCanvas.ActualHeight <= 0)
                return Rect.Empty;

            var p1 = _videoCanvas.TranslatePoint(new Point(t.X, t.Y), _adornerCanvas);
            var p2 = _videoCanvas.TranslatePoint(new Point(t.X + t.Width, t.Y + t.Height), _adornerCanvas);
            return new Rect(p1, p2);
        }
        catch
        {
            return Rect.Empty;
        }
    }

    public void UpdateAdornerPositions()
    {
        _adornerCanvas.Children.Clear();
        _handleElements.Clear();
        _selectionBorder = null;
        _lockBadge = null;

        if (_selected?.CanTransform != true) return;
        var t = Transform(_selected);
        if (!t.Visible) return;

        var screenRect = CalculateScreenRect(t);
        if (screenRect.IsEmpty || screenRect.Width <= 0 || screenRect.Height <= 0) return;

        bool isLocked = _selected.Locked || t.Locked;

        // Selection rectangle on the adorner layer (in screen DIPs)
        _selectionBorder = new Border
        {
            Width = Math.Max(1, screenRect.Width),
            Height = Math.Max(1, screenRect.Height),
            BorderBrush = isLocked
                ? new SolidColorBrush(Color.FromRgb(255, 179, 0)) // Amber warning
                : new SolidColorBrush(Color.FromRgb(0, 229, 255)), // Teal accent
            BorderThickness = new Thickness(2.5),
            Background = Brushes.Transparent,
            Cursor = isLocked ? Cursors.Arrow : Cursors.SizeAll
        };

        Canvas.SetLeft(_selectionBorder, screenRect.Left);
        Canvas.SetTop(_selectionBorder, screenRect.Top);
        _selectionBorder.MouseLeftButtonDown += (s, e) =>
        {
            if (!isLocked) BeginDrag(_selected, "MOVE", e);
        };
        _selectionBorder.MouseRightButtonUp += (s, e) =>
        {
            ContextMenuRequested?.Invoke(_selected, e.GetPosition(this));
            e.Handled = true;
        };

        _adornerCanvas.Children.Add(_selectionBorder);

        if (isLocked)
        {
            // Lock badge at top-right
            _lockBadge = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(220, 20, 26, 36)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(255, 179, 0)),
                BorderThickness = new Thickness(1.5),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(7, 3, 7, 3),
                IsHitTestVisible = false,
                Child = new TextBlock
                {
                    Text = "🔒 LOCKED",
                    Foreground = new SolidColorBrush(Color.FromRgb(255, 179, 0)),
                    FontSize = 11,
                    FontWeight = FontWeights.Bold
                }
            };
            Canvas.SetLeft(_lockBadge, screenRect.Left + 8);
            Canvas.SetTop(_lockBadge, screenRect.Top + 8);
            _adornerCanvas.Children.Add(_lockBadge);
            return;
        }

        // Add 8 handles in constant DIP size
        var handleDefs = new (string Name, Cursor Cursor)[]
        {
            ("TL", Cursors.SizeNWSE),
            ("T",  Cursors.SizeNS),
            ("TR", Cursors.SizeNESW),
            ("R",  Cursors.SizeWE),
            ("BR", Cursors.SizeNWSE),
            ("B",  Cursors.SizeNS),
            ("BL", Cursors.SizeNESW),
            ("L",  Cursors.SizeWE)
        };

        foreach (var (name, cursor) in handleDefs)
        {
            // Container grid with larger hit area (22x22 DIP)
            var hitContainer = new Grid
            {
                Width = HandleHitSize,
                Height = HandleHitSize,
                Background = Brushes.Transparent,
                Cursor = cursor,
                Tag = name
            };

            // Visual handle (12x12 DIP white box with 2px teal border)
            var visualBox = new Border
            {
                Width = HandleVisualSize,
                Height = HandleVisualSize,
                Background = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(0, 229, 255)),
                BorderThickness = new Thickness(2),
                CornerRadius = new CornerRadius(2),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false
            };

            hitContainer.Children.Add(visualBox);
            hitContainer.MouseLeftButtonDown += (_, e) => BeginDrag(_selected, name, e);

            _handleElements[name] = hitContainer;
            _adornerCanvas.Children.Add(hitContainer);
        }

        RepositionHandles(screenRect);
    }

    private void RepositionHandles(Rect screenRect)
    {
        double left = screenRect.Left;
        double top = screenRect.Top;
        double right = screenRect.Right;
        double bottom = screenRect.Bottom;
        double midX = screenRect.Left + screenRect.Width / 2;
        double midY = screenRect.Top + screenRect.Height / 2;

        void Place(string name, double x, double y)
        {
            if (_handleElements.TryGetValue(name, out var handle))
            {
                Canvas.SetLeft(handle, x - HalfHit);
                Canvas.SetTop(handle, y - HalfHit);
            }
        }

        Place("TL", left, top);
        Place("T",  midX, top);
        Place("TR", right, top);
        Place("R",  right, midY);
        Place("BR", right, bottom);
        Place("B",  midX, bottom);
        Place("BL", left, bottom);
        Place("L",  left, midY);

        if (_selectionBorder is not null)
        {
            _selectionBorder.Width = Math.Max(1, screenRect.Width);
            _selectionBorder.Height = Math.Max(1, screenRect.Height);
            Canvas.SetLeft(_selectionBorder, screenRect.Left);
            Canvas.SetTop(_selectionBorder, screenRect.Top);
        }

        if (_lockBadge is not null)
        {
            Canvas.SetLeft(_lockBadge, screenRect.Left + 8);
            Canvas.SetTop(_lockBadge, screenRect.Top + 8);
        }
    }

    private void AdornerCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            DoubleClicked?.Invoke();
            e.Handled = true;
        }
    }

    private void Source_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { Tag: SceneSource source }) return;
        SourceSelected?.Invoke(source);

        bool isLocked = source.Locked || Transform(source).Locked;
        if (!isLocked)
        {
            BeginDrag(source, "MOVE", e);
        }
    }

    private void BeginDrag(SceneSource source, string handle, MouseButtonEventArgs e)
    {
        if (!source.CanTransform || source.Locked || Transform(source).Locked) return;
        _selected = source;
        _dragged = source;
        _handle = handle;
        
        // Translate start point into logical canvas coordinates
        _startLogicalPoint = _adornerCanvas.TranslatePoint(e.GetPosition(_adornerCanvas), _videoCanvas);
        var t = Transform(source);
        _start = (t.X, t.Y, t.Width, t.Height);
        _adornerCanvas.CaptureMouse();
        e.Handled = true;
    }

    private void AdornerCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragged is null || e.LeftButton != MouseButtonState.Pressed) return;
        if (_dragged.Locked || Transform(_dragged).Locked)
        {
            _dragged = null;
            _adornerCanvas.ReleaseMouseCapture();
            return;
        }

        var currentLogical = _adornerCanvas.TranslatePoint(e.GetPosition(_adornerCanvas), _videoCanvas);
        var dx = currentLogical.X - _startLogicalPoint.X;
        var dy = currentLogical.Y - _startLogicalPoint.Y;
        var t = Transform(_dragged);

        if (_handle == "MOVE")
        {
            t.X = _start.X + dx;
            t.Y = _start.Y + dy;
            t.LayoutMode = VerticalLayoutMode.Manual;
            if (_mode == OutputMode.Vertical)
            {
                t.BackgroundEnlarged = false;
                t.FocusX = CanvasLayout.CalculateFocusX(t.X, t.Width, 1080);
            }
        }
        else if (_handle is "R" or "L" or "T" or "B")
        {
            t.LayoutMode = VerticalLayoutMode.Manual;
            if (_mode == OutputMode.Vertical)
            {
                t.BackgroundEnlarged = false;
            }

            switch (_handle)
            {
                case "R":
                    t.Width = Math.Max(16, _start.Width + dx);
                    t.X = _start.X;
                    break;
                case "L":
                    var newWidth = Math.Max(16, _start.Width - dx);
                    t.X = _start.X + _start.Width - newWidth;
                    t.Width = newWidth;
                    break;
                case "B":
                    t.Height = Math.Max(16, _start.Height + dy);
                    t.Y = _start.Y;
                    break;
                case "T":
                    var newHeight = Math.Max(16, _start.Height - dy);
                    t.Y = _start.Y + _start.Height - newHeight;
                    t.Height = newHeight;
                    break;
            }

            if (_mode == OutputMode.Vertical)
            {
                t.FocusX = CanvasLayout.CalculateFocusX(t.X, t.Width, 1080);
            }
        }
        else // Corner handles: "TL", "TR", "BL", "BR"
        {
            t.LayoutMode = VerticalLayoutMode.Manual;
            if (_mode == OutputMode.Vertical)
            {
                t.BackgroundEnlarged = false;
            }

            bool leftSide = _handle is "TL" or "BL";
            bool topSide = _handle is "TL" or "TR";

            double desiredWidth = Math.Max(16, _start.Width + (leftSide ? -dx : dx));
            double desiredHeight = Math.Max(16, _start.Height + (topSide ? -dy : dy));

            if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            {
                double ratio = _start.Width / Math.Max(1, _start.Height);
                double scaleX = desiredWidth / Math.Max(1, _start.Width);
                double scaleY = desiredHeight / Math.Max(1, _start.Height);
                double scale = Math.Abs(dx) > Math.Abs(dy) ? scaleX : scaleY;

                desiredWidth = Math.Max(16, _start.Width * scale);
                desiredHeight = Math.Max(16, desiredWidth / ratio);
            }

            t.Width = desiredWidth;
            t.Height = desiredHeight;
            t.X = leftSide ? _start.X + _start.Width - desiredWidth : _start.X;
            t.Y = topSide ? _start.Y + _start.Height - desiredHeight : _start.Y;

            if (_mode == OutputMode.Vertical)
            {
                t.FocusX = CanvasLayout.CalculateFocusX(t.X, t.Width, 1080);
            }
        }

        // Live update video element position
        var visual = _items.FirstOrDefault(i => ReferenceEquals(i.Source, _dragged));
        if (visual is not null)
        {
            Position(visual);
        }

        // Live update screen handles position
        var screenRect = CalculateScreenRect(t);
        if (!screenRect.IsEmpty)
        {
            RepositionHandles(screenRect);
        }

        TransformChanged?.Invoke();
    }

    private void AdornerCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragged is null) return;
        _dragged = null;
        _handle = null;
        _adornerCanvas.ReleaseMouseCapture();
        UpdateAdornerPositions();
        TransformChanged?.Invoke();
    }
}


