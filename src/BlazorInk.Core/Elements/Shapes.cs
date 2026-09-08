using System.Numerics;
using BlazorInk.Geometry;
using BlazorInk.Rendering;

namespace BlazorInk;

/// <summary>Visual style shared by shape annotations.</summary>
public sealed record ShapeStyle
{
    /// <summary>A 2-unit black outline with no fill.</summary>
    public static readonly ShapeStyle Default = new();

    /// <summary>Outline color.</summary>
    public InkColor StrokeColor { get; init; } = InkColor.Black;

    /// <summary>Outline width in local units; 0 disables the outline.</summary>
    public float StrokeWidth { get; init; } = 2f;

    /// <summary>Fill color, or null for no fill.</summary>
    public InkColor? FillColor { get; init; }

    /// <summary>Opacity 0..1 applied to the whole shape.</summary>
    public float Opacity { get; init; } = 1f;

    /// <summary>Dash pattern (on, off, ...) in local units, or null for a solid line.</summary>
    public float[]? Dash { get; init; }

    /// <summary>Blend mode.</summary>
    public BlendMode BlendMode { get; init; } = BlendMode.Normal;

    /// <summary>Paint for the outline.</summary>
    public StrokePaint ToStrokePaint() => new(StrokeColor, StrokeWidth, Opacity, BlendMode, LineCap.Round, LineJoin.Round, Dash);

    /// <summary>Paint for the fill.</summary>
    public FillPaint ToFillPaint() => new(FillColor ?? InkColor.Transparent, Opacity, BlendMode);

    /// <inheritdoc />
    public bool Equals(ShapeStyle? other)
        => other is not null && StrokeColor == other.StrokeColor && StrokeWidth == other.StrokeWidth && FillColor == other.FillColor
           && Opacity == other.Opacity && BlendMode == other.BlendMode && (Dash ?? []).AsSpan().SequenceEqual(other.Dash ?? []);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(StrokeColor, StrokeWidth, FillColor, Opacity, BlendMode);
}

/// <summary>Base for shape annotations that share a <see cref="ShapeStyle"/>.</summary>
public abstract record ShapeElement : InkElement
{
    /// <summary>Visual style.</summary>
    public ShapeStyle Style { get; init; } = ShapeStyle.Default;

    /// <summary>Builds the shape's path in local coordinates.</summary>
    public abstract PathGeometry GetPath();

    /// <summary>Bounds of the geometry without stroke width.</summary>
    protected abstract Rect GeometryBounds { get; }

    /// <inheritdoc />
    public override Rect LocalBounds => GeometryBounds.Inflate(Style.StrokeWidth / 2f + 1f);

    /// <inheritdoc />
    protected override void DrawCore(IRenderContext context)
    {
        PathGeometry path = GetPath();
        if (path.IsEmpty) return;
        if (Style.FillColor is { } fill && !fill.IsTransparent) context.FillPath(path, Style.ToFillPaint());
        if (Style.StrokeWidth > 0) context.StrokePath(path, Style.ToStrokePaint());
    }

    /// <summary>Distance-to-outline hit test shared by shapes; fill hit counts when the shape is filled.</summary>
    protected bool HitTestPath(Vector2 localPoint, float tolerance)
    {
        PathGeometry path = GetPath();
        float radius = MathF.Max(Style.StrokeWidth / 2f, 1.5f) + tolerance;
        float r2 = radius * radius;
        bool hit = false;
        bool filled = Style.FillColor is { IsTransparent: false };
        path.Flatten(0.5f, (pts, closed) =>
        {
            if (hit) return;
            ReadOnlySpan<Vector2> p = pts.Span;
            for (int i = 1; i < p.Length; i++)
            {
                if (GeometryMath.DistanceToSegmentSquared(localPoint, p[i - 1], p[i]) <= r2) { hit = true; return; }
            }
            if (closed && p.Length > 2)
            {
                if (GeometryMath.DistanceToSegmentSquared(localPoint, p[^1], p[0]) <= r2) { hit = true; return; }
                if (filled && GeometryMath.PointInPolygon(localPoint, p)) hit = true;
            }
        });
        return hit;
    }
}

/// <summary>A rectangle annotation, optionally with rounded corners.</summary>
public sealed record RectangleShape : ShapeElement
{
    /// <summary>Creates a rectangle.</summary>
    public RectangleShape(Rect rect) { Rect = rect.Normalize(); }

    /// <summary>Geometry in local coordinates.</summary>
    public Rect Rect { get; init; }

    /// <summary>Corner radius in local units.</summary>
    public float CornerRadius { get; init; }

    /// <inheritdoc />
    public override string TypeName => "Rectangle";

    /// <inheritdoc />
    protected override Rect GeometryBounds => Rect;

    /// <inheritdoc />
    public override PathGeometry GetPath() => CornerRadius > 0 ? new PathBuilder(12).AddRoundedRect(Rect, CornerRadius).ToGeometry() : new PathBuilder(6).AddRect(Rect).ToGeometry();

    /// <inheritdoc />
    protected override bool HitTestCore(Vector2 localPoint, float tolerance) => HitTestPath(localPoint, tolerance);
}

/// <summary>An ellipse annotation inscribed in a rectangle.</summary>
public sealed record EllipseShape : ShapeElement
{
    /// <summary>Creates an ellipse.</summary>
    public EllipseShape(Rect rect) { Rect = rect.Normalize(); }

    /// <summary>Bounding rectangle in local coordinates.</summary>
    public Rect Rect { get; init; }

    /// <inheritdoc />
    public override string TypeName => "Ellipse";

    /// <inheritdoc />
    protected override Rect GeometryBounds => Rect;

    /// <inheritdoc />
    public override PathGeometry GetPath() => new PathBuilder(8).AddEllipse(Rect).ToGeometry();

    /// <inheritdoc />
    protected override bool HitTestCore(Vector2 localPoint, float tolerance) => HitTestPath(localPoint, tolerance);
}

/// <summary>A straight line annotation with optional arrow heads at either end.</summary>
public sealed record LineShape : ShapeElement
{
    /// <summary>Creates a line.</summary>
    public LineShape(Vector2 start, Vector2 end)
    {
        Start = start;
        End = end;
    }

    /// <summary>Start point in local coordinates.</summary>
    public Vector2 Start { get; init; }

    /// <summary>End point in local coordinates.</summary>
    public Vector2 End { get; init; }

    /// <summary>Draw an arrow head at the start.</summary>
    public bool StartArrow { get; init; }

    /// <summary>Draw an arrow head at the end.</summary>
    public bool EndArrow { get; init; }

    /// <summary>Arrow head length as a multiple of the stroke width (default 5).</summary>
    public float ArrowSize { get; init; } = 5f;

    /// <inheritdoc />
    public override string TypeName => StartArrow || EndArrow ? "Arrow" : "Line";

    /// <inheritdoc />
    protected override Rect GeometryBounds => Rect.FromPoints(Start, End).Inflate(HeadLength);

    private float HeadLength => (StartArrow || EndArrow) ? MathF.Max(Style.StrokeWidth, 1f) * ArrowSize : 0f;

    /// <inheritdoc />
    public override PathGeometry GetPath()
    {
        var b = new PathBuilder(12);
        b.MoveTo(Start).LineTo(End);
        float len = HeadLength;
        Vector2 dir = GeometryMath.SafeNormalize(End - Start);
        if (dir != Vector2.Zero && len > 0)
        {
            if (EndArrow) AddHead(b, End, dir, len);
            if (StartArrow) AddHead(b, Start, -dir, len);
        }
        return b.ToGeometry();
    }

    private static void AddHead(PathBuilder b, Vector2 tip, Vector2 dir, float len)
    {
        const float halfAngle = 0.46f; // ~26 degrees
        Vector2 left = tip - GeometryMath.Rotate(dir, halfAngle) * len;
        Vector2 right = tip - GeometryMath.Rotate(dir, -halfAngle) * len;
        b.MoveTo(left).LineTo(tip).LineTo(right);
    }

    /// <inheritdoc />
    protected override void DrawCore(IRenderContext context)
    {
        // Lines are never filled; arrow heads are open strokes so they match the line weight.
        if (Style.StrokeWidth <= 0) return;
        context.StrokePath(GetPath(), Style.ToStrokePaint() with { Join = LineJoin.Round, Cap = LineCap.Round });
    }

    /// <inheritdoc />
    protected override bool HitTestCore(Vector2 localPoint, float tolerance)
    {
        float radius = MathF.Max(Style.StrokeWidth / 2f, 2f) + tolerance;
        return GeometryMath.DistanceToSegmentSquared(localPoint, Start, End) <= radius * radius;
    }

    /// <inheritdoc />
    public override bool IntersectsRect(Rect rect)
    {
        if (!Bounds.Intersects(rect)) return false;
        Vector2 s = Vector2.Transform(Start, Transform);
        Vector2 e = Vector2.Transform(End, Transform);
        return GeometryMath.SegmentIntersectsRect(s, e, rect.Inflate(Style.StrokeWidth / 2f));
    }
}
