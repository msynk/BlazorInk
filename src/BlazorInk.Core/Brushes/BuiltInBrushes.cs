using BlazorInk.Geometry;
using BlazorInk.Rendering;

namespace BlazorInk.Brushes;

/// <summary>
/// A pressure-sensitive pen: variable width outline with round caps and a subtle taper at the end.
/// The default brush for handwriting and signatures.
/// </summary>
public sealed record PenBrush : Brush
{
    /// <summary>The serialized kind: <c>pen</c>.</summary>
    public const string KindName = "pen";

    /// <summary>Creates a pen with sensible defaults (black, 4 units wide, ease-in pressure curve).</summary>
    public PenBrush()
    {
        Width = 4f;
        PressureCurve = PressureCurve.Pen;
        TaperEnd = 6f;
    }

    /// <inheritdoc />
    public override string Kind => KindName;

    /// <inheritdoc />
    public override StrokeGeometry BuildGeometry(StrokePointsView points, PathBuilder builder)
        => VariableWidthBrushes.Build(points, builder, this, roundCaps: true);
}

/// <summary>
/// A pencil: thin, slightly translucent, with strong pressure response. Renders as a variable-width outline.
/// </summary>
public sealed record PencilBrush : Brush
{
    /// <summary>The serialized kind: <c>pencil</c>.</summary>
    public const string KindName = "pencil";

    /// <summary>Creates a pencil with defaults (2 units wide, 85% opacity).</summary>
    public PencilBrush()
    {
        Width = 2f;
        Opacity = 0.85f;
        PressureCurve = new PressureCurve(PressureCurveKind.EaseIn, 0.35f, 1f);
        Smoothing = 0.35f;
        Spacing = 0.15f;
    }

    /// <inheritdoc />
    public override string Kind => KindName;

    /// <inheritdoc />
    public override StrokeGeometry BuildGeometry(StrokePointsView points, PathBuilder builder)
        => VariableWidthBrushes.Build(points, builder, this, roundCaps: true);
}

/// <summary>
/// A marker: constant width, round caps, opaque. Renders as a stroked smooth centerline, which keeps geometry small.
/// </summary>
public sealed record MarkerBrush : Brush
{
    /// <summary>The serialized kind: <c>marker</c>.</summary>
    public const string KindName = "marker";

    /// <summary>Creates a marker with defaults (8 units wide, no pressure).</summary>
    public MarkerBrush()
    {
        Width = 8f;
        PressureCurve = PressureCurve.Constant;
        SimulatePressure = false;
    }

    /// <inheritdoc />
    public override string Kind => KindName;

    /// <inheritdoc />
    public override StrokeGeometry BuildGeometry(StrokePointsView points, PathBuilder builder)
        => CenterlineBrushes.Build(points, builder, Width, LineCap.Round, LineJoin.Round);
}

/// <summary>
/// A highlighter: wide, translucent, flat caps, multiply blend so text underneath stays legible and
/// overlapping highlights do not turn opaque.
/// </summary>
public sealed record HighlighterBrush : Brush
{
    /// <summary>The serialized kind: <c>highlighter</c>.</summary>
    public const string KindName = "highlighter";

    /// <summary>Creates a yellow highlighter (20 units wide, 45% opacity, multiply).</summary>
    public HighlighterBrush()
    {
        Color = new InkColor(255, 235, 59);
        Width = 20f;
        Opacity = 0.45f;
        BlendMode = BlendMode.Multiply;
        PressureCurve = PressureCurve.Constant;
        SimulatePressure = false;
        Smoothing = 0.6f;
        Spacing = 0.2f;
    }

    /// <inheritdoc />
    public override string Kind => KindName;

    /// <inheritdoc />
    public override StrokeGeometry BuildGeometry(StrokePointsView points, PathBuilder builder)
        => CenterlineBrushes.Build(points, builder, Width, LineCap.Butt, LineJoin.Round);
}

/// <summary>
/// A pixel eraser. Strokes drawn with it are stored as vector elements that erase whatever is beneath them
/// within the same layer (destination-out compositing). Unlike the stroke and segment erasers this does not
/// modify other elements, so it can be undone and re-rendered exactly, and exports as an SVG mask.
/// </summary>
public sealed record PixelEraserBrush : Brush
{
    /// <summary>The serialized kind: <c>pixel-eraser</c>.</summary>
    public const string KindName = "pixel-eraser";

    /// <summary>Creates a 20-unit eraser.</summary>
    public PixelEraserBrush()
    {
        Width = 20f;
        BlendMode = BlendMode.DestinationOut;
        PressureCurve = PressureCurve.Constant;
        SimulatePressure = false;
        Smoothing = 0.3f;
    }

    /// <inheritdoc />
    public override string Kind => KindName;

    /// <inheritdoc />
    public override StrokeGeometry BuildGeometry(StrokePointsView points, PathBuilder builder)
        => VariableWidthBrushes.Build(points, builder, this, roundCaps: true);
}

/// <summary>Shared geometry helpers for brushes that render variable-width outlines.</summary>
public static class VariableWidthBrushes
{
    /// <summary>Builds outline geometry for a brush.</summary>
    public static StrokeGeometry Build(StrokePointsView points, PathBuilder builder, Brush brush, bool roundCaps)
    {
        var options = new OutlineOptions(brush.Width)
        {
            PressureCurve = brush.PressureCurve,
            TaperStart = brush.TaperStart,
            TaperEnd = brush.TaperEnd,
            RoundCaps = roundCaps,
            Smooth = true,
        };
        Rect bounds = StrokeOutliner.Build(points, options, builder);
        if (builder.IsEmpty) return StrokeGeometry.Empty;
        return new StrokeGeometry(builder.ToGeometry(), StrokeGeometryMode.Fill, 0f, LineCap.Round, LineJoin.Round, bounds.Inflate(1f));
    }
}

/// <summary>Shared geometry helpers for brushes that render a constant-width centerline.</summary>
public static class CenterlineBrushes
{
    /// <summary>Builds a smooth centerline path (Catmull-Rom) to be stroked at <paramref name="width"/>.</summary>
    public static StrokeGeometry Build(StrokePointsView points, PathBuilder builder, float width, LineCap cap, LineJoin join)
    {
        builder.Reset();
        int n = points.Count;
        if (n == 0) return StrokeGeometry.Empty;
        if (n == 1)
        {
            // A single tap: draw a dot of the line width so it is visible.
            builder.AddCircle(points.PositionAt(0), width / 2f);
            return new StrokeGeometry(builder.ToGeometry(), StrokeGeometryMode.Fill, 0f, cap, join, points.Bounds.Inflate(width / 2f + 1f));
        }
        Span<System.Numerics.Vector2> pts = n <= 1024 ? stackalloc System.Numerics.Vector2[n] : new System.Numerics.Vector2[n];
        for (int i = 0; i < n; i++) pts[i] = points.PositionAt(i);
        builder.AddCatmullRom(pts);
        float pad = width / 2f + 1f;
        return new StrokeGeometry(builder.ToGeometry(), StrokeGeometryMode.Centerline, width, cap, join, points.Bounds.Inflate(pad));
    }
}
