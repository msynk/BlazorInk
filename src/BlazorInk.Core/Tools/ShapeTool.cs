using System.Numerics;
using BlazorInk.Geometry;
using BlazorInk.Input;
using BlazorInk.Rendering;

namespace BlazorInk.Tools;

/// <summary>The shape a <see cref="ShapeTool"/> draws.</summary>
public enum ShapeKind : byte
{
    /// <summary>A rectangle.</summary>
    Rectangle = 0,
    /// <summary>An ellipse inscribed in the dragged rectangle.</summary>
    Ellipse = 1,
    /// <summary>A straight line.</summary>
    Line = 2,
    /// <summary>A line with an arrow head at the end.</summary>
    Arrow = 3,
    /// <summary>A translucent highlight rectangle.</summary>
    Highlight = 4,
}

/// <summary>
/// Draws shape annotations by dragging: rectangle, ellipse, line, arrow and highlight. Holding Shift constrains
/// to a square/circle or to 15-degree angles for lines.
/// </summary>
public sealed class ShapeTool : ToolBase
{
    private bool _dragging;
    private int _pointerId = -1;
    private Vector2 _start;
    private Vector2 _current;
    private bool _constrain;

    /// <summary>Creates a shape tool.</summary>
    /// <param name="kind">Which shape to draw.</param>
    public ShapeTool(ShapeKind kind = ShapeKind.Rectangle) { Kind = kind; }

    /// <summary>Which shape is drawn.</summary>
    public ShapeKind Kind { get; set; }

    /// <summary>Corner radius for rectangles, in document units.</summary>
    public float CornerRadius { get; set; }

    /// <summary>Whether the tool stays active after drawing a shape (otherwise the host usually switches to select).</summary>
    public bool Sticky { get; set; } = true;

    /// <inheritdoc />
    public override string Id => "shape-" + Kind.ToString().ToLowerInvariant();

    /// <inheritdoc />
    public override string DisplayName => Kind.ToString();

    /// <summary>True while a shape is being dragged.</summary>
    public bool IsDrawing => _dragging;

    /// <inheritdoc />
    public override bool OnPointerDown(IToolContext context, in InputSample sample)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (_dragging) return false;
        if (context.Document.TryGetLayer(context.ActiveLayerId, out Layer? layer) && (layer.IsLocked || !layer.IsVisible)) return false;
        _dragging = true;
        _pointerId = sample.PointerId;
        _start = context.Viewport.ToDocument(sample.Position);
        _current = _start;
        _constrain = sample.Shift;
        context.CapturePointer(sample.PointerId);
        context.InvalidateOverlay();
        return true;
    }

    /// <inheritdoc />
    public override void OnPointerMove(IToolContext context, in InputSample sample)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!_dragging || sample.PointerId != _pointerId) return;
        _current = context.Viewport.ToDocument(sample.Position);
        _constrain = sample.Shift;
        context.InvalidateOverlay();
    }

    /// <inheritdoc />
    public override void OnPointerUp(IToolContext context, in InputSample sample)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!_dragging || sample.PointerId != _pointerId) return;
        context.ReleasePointer(sample.PointerId);
        _current = context.Viewport.ToDocument(sample.Position);
        _constrain = sample.Shift;
        _dragging = false;
        _pointerId = -1;

        InkElement? element = Build(context);
        if (element is not null)
        {
            context.Document.AddElement(element, context.ActiveLayerId);
            context.InvalidateDocument(element.Bounds);
            context.Announce($"{element.TypeName} added.");
        }
        context.InvalidateOverlay();
    }

    /// <inheritdoc />
    public override void OnPointerCancel(IToolContext context, in InputSample sample)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!_dragging) return;
        context.ReleasePointer(sample.PointerId);
        _dragging = false;
        _pointerId = -1;
        context.InvalidateOverlay();
    }

    private (Vector2 Start, Vector2 End) GetPoints()
    {
        Vector2 end = _current;
        if (_constrain)
        {
            Vector2 d = end - _start;
            if (Kind is ShapeKind.Line or ShapeKind.Arrow)
            {
                // Snap to 15 degree increments.
                float angle = MathF.Atan2(d.Y, d.X);
                float step = MathF.PI / 12f;
                angle = MathF.Round(angle / step) * step;
                float len = d.Length();
                end = _start + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * len;
            }
            else
            {
                float size = MathF.Max(MathF.Abs(d.X), MathF.Abs(d.Y));
                end = _start + new Vector2(MathF.CopySign(size, d.X == 0 ? 1 : d.X), MathF.CopySign(size, d.Y == 0 ? 1 : d.Y));
            }
        }
        return (_start, end);
    }

    private InkElement? Build(IToolContext context)
    {
        (Vector2 start, Vector2 end) = GetPoints();
        Rect rect = Rect.FromPoints(start, end);
        float minimum = 2f / MathF.Max(context.Viewport.Zoom, 0.001f);
        bool tiny = rect.Width < minimum && rect.Height < minimum;
        ShapeStyle style = context.ShapeStyle;

        switch (Kind)
        {
            case ShapeKind.Rectangle:
                return tiny ? null : new RectangleShape(rect) { Style = style, CornerRadius = CornerRadius };
            case ShapeKind.Ellipse:
                return tiny ? null : new EllipseShape(rect) { Style = style };
            case ShapeKind.Line:
                return tiny ? null : new LineShape(start, end) { Style = style };
            case ShapeKind.Arrow:
                return tiny ? null : new LineShape(start, end) { Style = style, EndArrow = true };
            case ShapeKind.Highlight:
                return tiny ? null : new HighlightElement(rect) { Color = style.StrokeColor, Opacity = 0.45f };
            default:
                return null;
        }
    }

    /// <inheritdoc />
    public override void DrawOverlay(IToolContext context, IRenderContext render)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!_dragging) return;
        InkElement? preview = Build(context);
        preview?.Draw(render);
    }

    /// <inheritdoc />
    public override bool OnKeyDown(IToolContext context, ToolKeyEvent key)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (key.Key == "Escape" && _dragging)
        {
            _dragging = false;
            _pointerId = -1;
            context.InvalidateOverlay();
            return true;
        }
        return false;
    }
}
