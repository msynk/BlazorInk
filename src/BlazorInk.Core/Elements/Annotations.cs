using System.Numerics;
using BlazorInk.Geometry;
using BlazorInk.Rendering;

namespace BlazorInk;

/// <summary>
/// A text-highlight annotation: one or more rectangles (typically one per text line) drawn with multiply blending
/// so the underlying document content stays legible. Used for document/PDF review interfaces.
/// </summary>
public sealed record HighlightElement : InkElement
{
    private readonly Rect[] _rects;

    /// <summary>Creates a highlight over the given rectangles.</summary>
    public HighlightElement(IEnumerable<Rect> rects)
    {
        ArgumentNullException.ThrowIfNull(rects);
        _rects = rects.Select(static r => r.Normalize()).Where(static r => !r.IsEmpty).ToArray();
        LocalBounds = _rects.Length == 0 ? Rect.Empty : _rects.Aggregate(Rect.Empty, static (a, b) => a.Union(b));
    }

    /// <summary>Creates a highlight over one rectangle.</summary>
    public HighlightElement(Rect rect) : this([rect]) { }

    /// <summary>Highlighted rectangles in local coordinates.</summary>
    public IReadOnlyList<Rect> Rects => _rects;

    /// <summary>Highlight color (yellow by default).</summary>
    public InkColor Color { get; init; } = new(255, 235, 59);

    /// <summary>Opacity 0..1.</summary>
    public float Opacity { get; init; } = 0.5f;

    /// <inheritdoc />
    public override string TypeName => "Highlight";

    /// <inheritdoc />
    public override Rect LocalBounds { get; }

    /// <inheritdoc />
    protected override void DrawCore(IRenderContext context)
    {
        if (_rects.Length == 0) return;
        var b = new PathBuilder(_rects.Length * 5);
        foreach (Rect r in _rects) b.AddRect(r);
        context.FillPath(b.ToGeometry(), new FillPaint(Color, Opacity, BlendMode.Multiply));
    }

    /// <inheritdoc />
    protected override bool HitTestCore(Vector2 localPoint, float tolerance)
    {
        foreach (Rect r in _rects) if (r.Inflate(tolerance).Contains(localPoint)) return true;
        return false;
    }
}

/// <summary>
/// A callout: a text box with a pointer line to an anchor. Used for review comments and explanatory labels.
/// </summary>
public sealed record CalloutElement : InkElement
{
    /// <summary>Creates a callout.</summary>
    /// <param name="box">Text box rectangle in local coordinates.</param>
    /// <param name="anchor">Point the callout points to.</param>
    /// <param name="text">Text content.</param>
    public CalloutElement(Rect box, Vector2 anchor, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > TextElement.MaxLength) throw new ArgumentException("Text is too long.", nameof(text));
        Box = box.Normalize();
        Anchor = anchor;
        Text = text;
    }

    /// <summary>Text box rectangle.</summary>
    public Rect Box { get; init; }

    /// <summary>Anchor point the pointer leads to.</summary>
    public Vector2 Anchor { get; init; }

    /// <summary>Text content.</summary>
    public string Text { get; init; }

    /// <summary>Text style.</summary>
    public TextStyle TextStyle { get; init; } = TextStyle.Default with { FontSize = 14f };

    /// <summary>Box and pointer style. The fill defaults to a light yellow.</summary>
    public ShapeStyle Style { get; init; } = new() { StrokeColor = new InkColor(120, 90, 0), StrokeWidth = 1.5f, FillColor = new InkColor(255, 249, 196) };

    /// <summary>Padding between box and text.</summary>
    public float Padding { get; init; } = 6f;

    /// <inheritdoc />
    public override string TypeName => "Callout";

    /// <inheritdoc />
    public override Rect LocalBounds => Box.Union(Anchor).Inflate(Style.StrokeWidth + 1f);

    /// <inheritdoc />
    protected override void DrawCore(IRenderContext context)
    {
        // Pointer from the nearest box edge point to the anchor.
        Vector2 from = ClosestEdgePoint(Box, Anchor);
        var pointer = new PathBuilder(4).MoveTo(from).LineTo(Anchor).ToGeometry();
        context.StrokePath(pointer, Style.ToStrokePaint());

        var box = new PathBuilder(12).AddRoundedRect(Box, 4f).ToGeometry();
        if (Style.FillColor is { } fill) context.FillPath(box, new FillPaint(fill, Style.Opacity, Style.BlendMode));
        if (Style.StrokeWidth > 0) context.StrokePath(box, Style.ToStrokePaint());

        if (Text.Length > 0)
        {
            context.Save();
            context.ClipRect(Box);
            context.DrawText(Text, new Vector2(Box.Left + Padding, Box.Top + Padding), TextStyle, Style.Opacity);
            context.Restore();
        }
    }

    private static Vector2 ClosestEdgePoint(Rect r, Vector2 p)
    {
        Vector2 c = r.Center;
        Vector2 d = p - c;
        if (d == Vector2.Zero) return c;
        float sx = MathF.Abs(d.X) > GeometryMath.Epsilon ? (r.Width / 2f) / MathF.Abs(d.X) : float.MaxValue;
        float sy = MathF.Abs(d.Y) > GeometryMath.Epsilon ? (r.Height / 2f) / MathF.Abs(d.Y) : float.MaxValue;
        float s = MathF.Min(sx, sy);
        return s >= 1f ? p : c + d * s;
    }

    /// <inheritdoc />
    protected override bool HitTestCore(Vector2 localPoint, float tolerance)
    {
        if (Box.Inflate(tolerance).Contains(localPoint)) return true;
        float radius = MathF.Max(Style.StrokeWidth / 2f, 2f) + tolerance;
        return GeometryMath.DistanceToSegmentSquared(localPoint, ClosestEdgePoint(Box, Anchor), Anchor) <= radius * radius;
    }

    /// <inheritdoc />
    public override string GetAccessibleDescription() => Description ?? "Callout: " + Text;
}

/// <summary>
/// An element of a type this version of the library does not know. It is preserved verbatim through load/save so
/// documents created by newer versions or with custom element types are never silently damaged. It draws nothing.
/// </summary>
public sealed record UnknownElement : InkElement
{
    /// <summary>Creates an unknown element.</summary>
    /// <param name="typeName">The serialized type discriminator.</param>
    /// <param name="rawJson">The element's original JSON object.</param>
    /// <param name="bounds">Bounds recorded in the document, if any.</param>
    public UnknownElement(string typeName, string rawJson, Rect bounds)
    {
        ArgumentException.ThrowIfNullOrEmpty(typeName);
        ArgumentNullException.ThrowIfNull(rawJson);
        SerializedTypeName = typeName;
        RawJson = rawJson;
        LocalBounds = bounds;
    }

    /// <summary>The type discriminator found in the document.</summary>
    public string SerializedTypeName { get; }

    /// <summary>The original JSON.</summary>
    public string RawJson { get; }

    /// <inheritdoc />
    public override string TypeName => "Unknown (" + SerializedTypeName + ")";

    /// <inheritdoc />
    public override bool IsSelectable => false;

    /// <inheritdoc />
    public override Rect LocalBounds { get; }

    /// <inheritdoc />
    protected override void DrawCore(IRenderContext context) { }

    /// <inheritdoc />
    protected override bool HitTestCore(Vector2 localPoint, float tolerance) => false;
}
