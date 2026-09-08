using System.Numerics;
using BlazorInk.Geometry;

namespace BlazorInk;

/// <summary>
/// Geometric editing operations on stroke point data: splitting a stroke where an eraser crossed it and
/// trimming its ends. Used by the segment eraser and by tools that cut strokes.
/// </summary>
public static class StrokeEditor
{
    /// <summary>
    /// Splits a stroke's points into the runs that survive erasing with a circular eraser swept along a path.
    /// </summary>
    /// <param name="points">The stroke's points in local coordinates.</param>
    /// <param name="eraserPath">The eraser positions in the same coordinates (at least one).</param>
    /// <param name="eraserRadius">Eraser radius, already including half the stroke width.</param>
    /// <param name="runs">Receives the surviving index ranges. Cleared first. Empty means the whole stroke is erased.</param>
    /// <returns>True when anything was erased.</returns>
    public static bool SplitByEraser(StrokePoints points, ReadOnlySpan<Vector2> eraserPath, float eraserRadius, List<(int Start, int Length)> runs)
    {
        ArgumentNullException.ThrowIfNull(points);
        ArgumentNullException.ThrowIfNull(runs);
        runs.Clear();
        int n = points.Count;
        if (n == 0 || eraserPath.IsEmpty) return false;

        float r2 = eraserRadius * eraserRadius;
        ReadOnlySpan<float> xs = points.X;
        ReadOnlySpan<float> ys = points.Y;

        Span<bool> erased = n <= 1024 ? stackalloc bool[n] : new bool[n];
        bool any = false;
        for (int i = 0; i < n; i++)
        {
            var p = new Vector2(xs[i], ys[i]);
            bool hit = false;
            if (eraserPath.Length == 1)
            {
                hit = (p - eraserPath[0]).LengthSquared() <= r2;
            }
            else
            {
                for (int e = 1; e < eraserPath.Length; e++)
                {
                    if (GeometryMath.DistanceToSegmentSquared(p, eraserPath[e - 1], eraserPath[e]) <= r2) { hit = true; break; }
                }
            }
            erased[i] = hit;
            any |= hit;
        }

        if (!any)
        {
            runs.Add((0, n));
            return false;
        }

        int start = -1;
        for (int i = 0; i < n; i++)
        {
            if (!erased[i])
            {
                if (start < 0) start = i;
            }
            else if (start >= 0)
            {
                AddRun(runs, start, i - start);
                start = -1;
            }
        }
        if (start >= 0) AddRun(runs, start, n - start);
        return true;
    }

    private static void AddRun(List<(int Start, int Length)> runs, int start, int length)
    {
        // A single surviving point between two erased regions would render as a dot; drop it.
        if (length >= 2) runs.Add((start, length));
    }

    /// <summary>
    /// Erases part of a stroke and returns the strokes that remain (0, 1 or more). The originals' brush,
    /// transform, description and metadata are preserved; each fragment gets a new id except the first, which
    /// keeps the original id so undo and collaboration see an edit rather than a delete plus two adds.
    /// </summary>
    /// <param name="stroke">The stroke.</param>
    /// <param name="eraserPathDocument">Eraser path in document coordinates.</param>
    /// <param name="eraserRadiusDocument">Eraser radius in document units.</param>
    /// <param name="fragments">Receives the surviving fragments. Cleared first.</param>
    /// <returns>True when the stroke was modified.</returns>
    public static bool EraseSegments(Stroke stroke, ReadOnlySpan<Vector2> eraserPathDocument, float eraserRadiusDocument, List<Stroke> fragments)
    {
        ArgumentNullException.ThrowIfNull(stroke);
        ArgumentNullException.ThrowIfNull(fragments);
        fragments.Clear();
        if (stroke.Points.Count == 0) return false;

        // Work in the stroke's local coordinates.
        Vector2[]? rented = null;
        ReadOnlySpan<Vector2> path = eraserPathDocument;
        float radius = eraserRadiusDocument;
        if (!stroke.Transform.IsIdentity)
        {
            if (!Matrix3x2.Invert(stroke.Transform, out Matrix3x2 inverse)) return false;
            rented = new Vector2[eraserPathDocument.Length];
            for (int i = 0; i < eraserPathDocument.Length; i++) rented[i] = Vector2.Transform(eraserPathDocument[i], inverse);
            path = rented;
            float scale = MathF.Sqrt(MathF.Abs(stroke.Transform.GetDeterminant()));
            if (scale > GeometryMath.Epsilon) radius /= scale;
        }

        float effectiveRadius = radius + stroke.Brush.HitTestWidth / 2f;
        var runs = new List<(int Start, int Length)>();
        bool erased = SplitByEraser(stroke.Points, path, effectiveRadius, runs);
        GC.KeepAlive(rented);
        if (!erased) return false;

        for (int i = 0; i < runs.Count; i++)
        {
            (int start, int length) = runs[i];
            StrokePoints slice = stroke.Points.Slice(start, length);
            Stroke fragment = i == 0
                ? stroke with { Points = slice }
                : stroke with { Points = slice, Id = Guid.NewGuid(), Version = 0 };
            fragments.Add(fragment);
        }
        return true;
    }

    /// <summary>Returns the stroke with the first <paramref name="count"/> points removed, or null when nothing remains.</summary>
    public static Stroke? TrimStart(Stroke stroke, int count)
    {
        ArgumentNullException.ThrowIfNull(stroke);
        int remaining = stroke.Points.Count - count;
        if (remaining < 2) return null;
        return stroke with { Points = stroke.Points.Slice(count, remaining) };
    }

    /// <summary>Returns the stroke with the last <paramref name="count"/> points removed, or null when nothing remains.</summary>
    public static Stroke? TrimEnd(Stroke stroke, int count)
    {
        ArgumentNullException.ThrowIfNull(stroke);
        int remaining = stroke.Points.Count - count;
        if (remaining < 2) return null;
        return stroke with { Points = stroke.Points.Slice(0, remaining) };
    }
}
