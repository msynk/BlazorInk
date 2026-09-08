using BlazorInk.Geometry;
using BlazorInk.Rendering;

namespace BlazorInk.Brushes;

/// <summary>How a stroke's path should be painted.</summary>
public enum StrokeGeometryMode : byte
{
    /// <summary>The path is a closed outline polygon to be filled (variable width strokes).</summary>
    Fill = 0,
    /// <summary>The path is a centerline to be stroked with a constant width.</summary>
    Centerline = 1,
}

/// <summary>
/// The renderable vector geometry of a stroke, produced by a <see cref="Brush"/> from stroke points. Immutable and
/// cached by strokes, so it is only computed once per stroke version regardless of how many times it is drawn.
/// </summary>
public sealed class StrokeGeometry
{
    /// <summary>Geometry of an empty stroke.</summary>
    public static readonly StrokeGeometry Empty = new(PathGeometry.Empty, StrokeGeometryMode.Fill, 0, LineCap.Round, LineJoin.Round, Rect.Empty);

    /// <summary>Creates a stroke geometry.</summary>
    public StrokeGeometry(PathGeometry path, StrokeGeometryMode mode, float lineWidth, LineCap cap, LineJoin join, Rect bounds)
    {
        Path = path;
        Mode = mode;
        LineWidth = lineWidth;
        Cap = cap;
        Join = join;
        Bounds = bounds;
    }

    /// <summary>The vector path.</summary>
    public PathGeometry Path { get; }

    /// <summary>Whether to fill or stroke the path.</summary>
    public StrokeGeometryMode Mode { get; }

    /// <summary>Line width for <see cref="StrokeGeometryMode.Centerline"/> geometry.</summary>
    public float LineWidth { get; }

    /// <summary>Line cap for centerline geometry.</summary>
    public LineCap Cap { get; }

    /// <summary>Line join for centerline geometry.</summary>
    public LineJoin Join { get; }

    /// <summary>Bounds of the painted area including line width.</summary>
    public Rect Bounds { get; }

    /// <summary>True when there is nothing to draw.</summary>
    public bool IsEmpty => Path.IsEmpty;

    /// <summary>Draws the geometry with the given color, opacity and blend mode.</summary>
    public void Draw(IRenderContext context, InkColor color, float opacity, BlendMode blend)
    {
        if (IsEmpty) return;
        if (Mode == StrokeGeometryMode.Fill)
        {
            context.FillPath(Path, new FillPaint(color, opacity, blend, FillRule.NonZero));
        }
        else
        {
            context.StrokePath(Path, new StrokePaint(color, LineWidth, opacity, blend, Cap, Join));
        }
    }
}
