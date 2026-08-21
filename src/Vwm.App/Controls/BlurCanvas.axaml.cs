using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Vwm.App.ViewModels;
using Vwm.Core.Render;

namespace Vwm.App.Controls;

/// <summary>
/// The blur editor's canvas: a frame of the recording with one grey box over it that the
/// user drags to move, pulls by a handle to resize, or draws from scratch on empty frame.
///
/// The box is kept in the region's own coordinates — fractions of the frame, 0-1 — and only
/// converted to screen pixels for display. That is what makes the placement survive the
/// difference between this scaled-down still and the full-size render.
/// </summary>
public partial class BlurCanvas : UserControl
{
    /// <summary>The still frame under the box. Null shows an empty player background.</summary>
    public static readonly StyledProperty<Bitmap?> FrameProperty =
        AvaloniaProperty.Register<BlurCanvas, Bitmap?>(nameof(Frame));

    /// <summary>The area being placed. Edited in place, so the inspector's sliders and
    /// labels track the drag live.</summary>
    public static readonly StyledProperty<BlurAreaItem?> RegionProperty =
        AvaloniaProperty.Register<BlurCanvas, BlurAreaItem?>(nameof(Region));

    /// <summary>Hides the box and turns off editing — used while the canvas is showing the
    /// real blurred frame, where a placement box on top would only be in the way.</summary>
    public static readonly StyledProperty<bool> ShowBoxProperty =
        AvaloniaProperty.Register<BlurCanvas, bool>(nameof(ShowBox), defaultValue: true);

    public Bitmap? Frame
    {
        get => GetValue(FrameProperty);
        set => SetValue(FrameProperty, value);
    }

    public BlurAreaItem? Region
    {
        get => GetValue(RegionProperty);
        set => SetValue(RegionProperty, value);
    }

    public bool ShowBox
    {
        get => GetValue(ShowBoxProperty);
        set => SetValue(ShowBoxProperty, value);
    }

    private enum DragMode { None, Move, Resize, Draw }

    private readonly (Rectangle Handle, int Dx, int Dy)[] _handles;
    private DragMode _mode;
    private int _handleDx, _handleDy;
    private Point _pressNormalized;
    private (double X, double Y, double W, double H) _pressRegion;
    private BlurAreaItem? _watched;

    public BlurCanvas()
    {
        InitializeComponent();

        _handles =
        [
            (HandleNW, -1, -1), (HandleN, 0, -1), (HandleNE, 1, -1),
            (HandleW, -1, 0), (HandleE, 1, 0),
            (HandleSW, -1, 1), (HandleS, 0, 1), (HandleSE, 1, 1),
        ];
        foreach (var (handle, dx, dy) in _handles)
            handle.Cursor = new Cursor(CursorFor(dx, dy));
        Box.Cursor = new Cursor(StandardCursorType.SizeAll);

        Overlay.PointerPressed += OnPointerPressed;
        Overlay.PointerMoved += OnPointerMoved;
        Overlay.PointerReleased += OnPointerReleased;
        Overlay.PointerCaptureLost += (_, _) => _mode = DragMode.None;
        Root.SizeChanged += (_, _) => Sync();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == FrameProperty)
        {
            FrameImage.Source = Frame;
            Sync();
        }
        else if (change.Property == RegionProperty)
        {
            // Follow the selected area so edits made with the sliders redraw the box too.
            if (_watched is not null)
                _watched.PropertyChanged -= OnRegionChanged;
            _watched = Region;
            if (_watched is not null)
                _watched.PropertyChanged += OnRegionChanged;
            _mode = DragMode.None;
            Sync();
        }
        else if (change.Property == ShowBoxProperty)
        {
            _mode = DragMode.None;
            Sync();
        }
    }

    private void OnRegionChanged(object? sender, PropertyChangedEventArgs e) => Sync();

    /// <summary>Where the frame actually lands inside the control. The image is drawn
    /// <c>Stretch="Uniform"</c>, so it is letterboxed; the box has to use the same rectangle
    /// or it would drift away from the content it covers.</summary>
    private Rect FrameRect()
    {
        var bounds = Root.Bounds;
        if (Frame is not { } bitmap || bounds.Width <= 0 || bounds.Height <= 0)
            return default;
        var size = bitmap.Size;
        if (size.Width <= 0 || size.Height <= 0)
            return default;

        var scale = Math.Min(bounds.Width / size.Width, bounds.Height / size.Height);
        var width = size.Width * scale;
        var height = size.Height * scale;
        return new Rect((bounds.Width - width) / 2, (bounds.Height - height) / 2, width, height);
    }

    private void Sync()
    {
        var frame = FrameRect();
        var visible = ShowBox && Region is not null && frame.Width > 0 && frame.Height > 0;
        Box.IsVisible = visible;
        foreach (var (handle, _, _) in _handles)
            handle.IsVisible = visible;
        EmptyHint.IsVisible = Frame is not null && ShowBox && Region is null;
        if (!visible)
            return;

        var region = Region!;
        var x = frame.X + region.X * frame.Width;
        var y = frame.Y + region.Y * frame.Height;
        var w = region.Width * frame.Width;
        var h = region.Height * frame.Height;

        Canvas.SetLeft(Box, x);
        Canvas.SetTop(Box, y);
        Box.Width = w;
        Box.Height = h;

        foreach (var (handle, dx, dy) in _handles)
        {
            Canvas.SetLeft(handle, x + (dx + 1) * w / 2 - handle.Width / 2);
            Canvas.SetTop(handle, y + (dy + 1) * h / 2 - handle.Height / 2);
        }
    }

    private Point? Normalize(Point p)
    {
        var frame = FrameRect();
        if (frame.Width <= 0 || frame.Height <= 0)
            return null;
        return new Point((p.X - frame.X) / frame.Width, (p.Y - frame.Y) / frame.Height);
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!ShowBox || Region is null || Normalize(e.GetPosition(Overlay)) is not { } n)
            return;

        _pressNormalized = n;
        _pressRegion = (Region.X, Region.Y, Region.Width, Region.Height);

        if (e.Source is Rectangle handle && _handles.FirstOrDefault(h => h.Handle == handle) is { Handle: not null } hit)
        {
            _mode = DragMode.Resize;
            (_handleDx, _handleDy) = (hit.Dx, hit.Dy);
        }
        else if (e.Source == Box || Box.IsPointerOver)
        {
            _mode = DragMode.Move;
        }
        else
        {
            // Dragging on bare frame draws a fresh rectangle from that corner.
            _mode = DragMode.Draw;
        }
        e.Pointer.Capture(Overlay);
        e.Handled = true;
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_mode == DragMode.None || Region is null || Normalize(e.GetPosition(Overlay)) is not { } n)
            return;

        switch (_mode)
        {
            case DragMode.Move:
                Apply(
                    Math.Clamp(_pressRegion.X + (n.X - _pressNormalized.X), 0, 1 - _pressRegion.W),
                    Math.Clamp(_pressRegion.Y + (n.Y - _pressNormalized.Y), 0, 1 - _pressRegion.H),
                    _pressRegion.W, _pressRegion.H);
                break;

            case DragMode.Resize:
                var left = _pressRegion.X;
                var top = _pressRegion.Y;
                var right = _pressRegion.X + _pressRegion.W;
                var bottom = _pressRegion.Y + _pressRegion.H;
                if (_handleDx < 0)
                    left = Math.Clamp(n.X, 0, right - BlurRegion.MinSize);
                else if (_handleDx > 0)
                    right = Math.Clamp(n.X, left + BlurRegion.MinSize, 1);
                if (_handleDy < 0)
                    top = Math.Clamp(n.Y, 0, bottom - BlurRegion.MinSize);
                else if (_handleDy > 0)
                    bottom = Math.Clamp(n.Y, top + BlurRegion.MinSize, 1);
                Apply(left, top, right - left, bottom - top);
                break;

            case DragMode.Draw:
                var x0 = Math.Clamp(Math.Min(_pressNormalized.X, n.X), 0, 1);
                var y0 = Math.Clamp(Math.Min(_pressNormalized.Y, n.Y), 0, 1);
                var x1 = Math.Clamp(Math.Max(_pressNormalized.X, n.X), 0, 1);
                var y1 = Math.Clamp(Math.Max(_pressNormalized.Y, n.Y), 0, 1);
                // A click that never really moved would make a zero-size area the renderer
                // would reject, so every drawn box gets at least the minimum side.
                var w = Math.Max(x1 - x0, BlurRegion.MinSize);
                var h = Math.Max(y1 - y0, BlurRegion.MinSize);
                Apply(Math.Min(x0, 1 - w), Math.Min(y0, 1 - h), w, h);
                break;
        }
        e.Handled = true;
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _mode = DragMode.None;
        e.Pointer.Capture(null);
    }

    private void Apply(double x, double y, double width, double height)
    {
        if (Region is not { } region)
            return;
        region.Width = Math.Clamp(width, BlurRegion.MinSize, 1);
        region.Height = Math.Clamp(height, BlurRegion.MinSize, 1);
        region.X = Math.Clamp(x, 0, 1 - region.Width);
        region.Y = Math.Clamp(y, 0, 1 - region.Height);
        Sync();
    }

    private static StandardCursorType CursorFor(int dx, int dy) => (dx, dy) switch
    {
        (-1, -1) => StandardCursorType.TopLeftCorner,
        (0, -1) => StandardCursorType.TopSide,
        (1, -1) => StandardCursorType.TopRightCorner,
        (-1, 0) => StandardCursorType.LeftSide,
        (1, 0) => StandardCursorType.RightSide,
        (-1, 1) => StandardCursorType.BottomLeftCorner,
        (0, 1) => StandardCursorType.BottomSide,
        _ => StandardCursorType.BottomRightCorner,
    };
}
