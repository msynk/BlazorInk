using System.Numerics;
using BlazorInk.Geometry;
using BlazorInk.Rendering;

namespace BlazorInk.Raster;

/// <summary>
/// Converts a stroked path into the polygons that cover it, so a rasterizer only needs to fill polygons.
/// Supports butt/round/square caps and miter/round/bevel joins.
/// </summary>
public static class PathStroker
{
    private const float DefaultMiterLimit = 4f;

    /// <summary>
    /// Produces the outline polygons of a stroked path. Each polygon is closed and should be filled with the
    /// non-zero rule; overlapping polygons therefore merge into a single solid stroke.
    /// </summary>
    /// <param name="path">The path to stroke.</param>
    /// <param name="width">Line width.</param>
    /// <param name="cap">Cap style.</param>
    /// <param name="join">Join style.</param>
    /// <param name="tolerance">Flattening tolerance.</param>
    /// <param name="dash">Optional dash pattern.</param>
    /// <param name="output">Receives the polygons. Cleared first.</param>
    public static void Stroke(PathGeometry path, float width, LineCap cap, LineJoin join, float tolerance, float[]? dash, List<Vector2[]> output)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(output);
        output.Clear();
        if (path.IsEmpty || width <= 0) return;
        float radius = width / 2f;

        var scratch = new List<Vector2>(64);
        path.Flatten(tolerance, (memory, closed) =>
        {
            ReadOnlySpan<Vector2> points = memory.Span;
            if (dash is { Length: > 0 })
            {
                foreach (Vector2[] piece in ApplyDash(points, closed, dash))
                {
                    StrokePolyline(piece, false, radius, cap, join, output, scratch);
                }
                return;
            }
            StrokePolyline(points, closed, radius, cap, join, output, scratch);
        });
    }

    private static void StrokePolyline(ReadOnlySpan<Vector2> raw, bool closed, float radius, LineCap cap, LineJoin join, List<Vector2[]> output, List<Vector2> scratch)
    {
        // Drop repeated points: zero-length segments have no direction and would produce NaN normals.
        scratch.Clear();
        foreach (Vector2 p in raw)
        {
            if (scratch.Count == 0 || Vector2.DistanceSquared(scratch[^1], p) > GeometryMath.Epsilon * GeometryMath.Epsilon) scratch.Add(p);
        }
        if (closed && scratch.Count > 1 && Vector2.DistanceSquared(scratch[0], scratch[^1]) <= GeometryMath.Epsilon * GeometryMath.Epsilon) scratch.RemoveAt(scratch.Count - 1);

        int n = scratch.Count;
        if (n == 0) return;
        if (n == 1)
        {
            // A lone point renders as a dot for round and square caps, and as nothing for butt caps.
            if (cap == LineCap.Round) output.Add(CreateCircle(scratch[0], radius));
            else if (cap == LineCap.Square) output.Add(CreateSquare(scratch[0], radius));
            return;
        }

        // Each segment contributes a quad; each join and cap contributes its own polygon. Filling them all with
        // the non-zero rule merges them into one shape, which avoids the self-intersection problems of a single
        // offset outline on tight curves.
        for (int i = 0; i < n - 1; i++) output.Add(CreateSegmentQuad(scratch[i], scratch[i + 1], radius));
        if (closed) output.Add(CreateSegmentQuad(scratch[n - 1], scratch[0], radius));

        int jointStart = closed ? 0 : 1;
        int jointEnd = closed ? n : n - 1;
        for (int i = jointStart; i < jointEnd; i++)
        {
            Vector2 previous = scratch[(i - 1 + n) % n];
            Vector2 current = scratch[i];
            Vector2 next = scratch[(i + 1) % n];
            AddJoin(output, previous, current, next, radius, join);
        }

        if (!closed)
        {
            AddCap(output, scratch[0], GeometryMath.SafeNormalize(scratch[0] - scratch[1]), radius, cap);
            AddCap(output, scratch[n - 1], GeometryMath.SafeNormalize(scratch[n - 1] - scratch[n - 2]), radius, cap);
        }
    }

    private static Vector2[] CreateSegmentQuad(Vector2 a, Vector2 b, float radius)
    {
        Vector2 normal = GeometryMath.Perpendicular(GeometryMath.SafeNormalize(b - a)) * radius;
        return [a + normal, b + normal, b - normal, a - normal];
    }

    private static void AddJoin(List<Vector2[]> output, Vector2 previous, Vector2 current, Vector2 next, float radius, LineJoin join)
    {
        Vector2 dirIn = GeometryMath.SafeNormalize(current - previous);
        Vector2 dirOut = GeometryMath.SafeNormalize(next - current);
        if (dirIn == Vector2.Zero || dirOut == Vector2.Zero) return;
        float cross = GeometryMath.Cross(dirIn, dirOut);
        if (MathF.Abs(cross) < 1e-6f) return; // collinear: the segment quads already meet flush

        switch (join)
        {
            case LineJoin.Round:
                output.Add(CreateCircle(current, radius));
                break;
            case LineJoin.Bevel:
                AddBevel(output, current, dirIn, dirOut, radius, cross);
                break;
            default:
            {
                // Miter: extend to the intersection unless the corner is so sharp that the spike exceeds the limit.
                float sign = cross > 0 ? -1f : 1f;
                Vector2 nIn = GeometryMath.Perpendicular(dirIn) * (radius * sign);
                Vector2 nOut = GeometryMath.Perpendicular(dirOut) * (radius * sign);
                Vector2 bisector = GeometryMath.SafeNormalize(nIn + nOut);
                float cosHalf = Vector2.Dot(bisector, GeometryMath.SafeNormalize(nIn));
                if (bisector == Vector2.Zero || cosHalf < 1e-4f || 1f / cosHalf > DefaultMiterLimit)
                {
                    AddBevel(output, current, dirIn, dirOut, radius, cross);
                    return;
                }
                Vector2 tip = current + bisector * (radius / cosHalf);
                output.Add([current, current + nIn, tip, current + nOut]);
                break;
            }
        }
    }

    private static void AddBevel(List<Vector2[]> output, Vector2 current, Vector2 dirIn, Vector2 dirOut, float radius, float cross)
    {
        float sign = cross > 0 ? -1f : 1f;
        Vector2 nIn = GeometryMath.Perpendicular(dirIn) * (radius * sign);
        Vector2 nOut = GeometryMath.Perpendicular(dirOut) * (radius * sign);
        output.Add([current, current + nIn, current + nOut]);
    }

    private static void AddCap(List<Vector2[]> output, Vector2 point, Vector2 outward, float radius, LineCap cap)
    {
        switch (cap)
        {
            case LineCap.Round:
                output.Add(CreateCircle(point, radius));
                break;
            case LineCap.Square:
            {
                if (outward == Vector2.Zero) { output.Add(CreateSquare(point, radius)); return; }
                Vector2 normal = GeometryMath.Perpendicular(outward) * radius;
                Vector2 extend = outward * radius;
                output.Add([point + normal, point + normal + extend, point - normal + extend, point - normal]);
                break;
            }
        }
    }

    private static Vector2[] CreateCircle(Vector2 center, float radius)
    {
        int steps = Math.Clamp((int)MathF.Ceiling(MathF.PI / MathF.Acos(MathF.Max(1f - 0.2f / MathF.Max(radius, 0.2f), -1f + 1e-6f))), 8, 64);
        var points = new Vector2[steps];
        for (int i = 0; i < steps; i++)
        {
            float a = i / (float)steps * MathF.Tau;
            points[i] = center + new Vector2(MathF.Cos(a), MathF.Sin(a)) * radius;
        }
        return points;
    }

    private static Vector2[] CreateSquare(Vector2 center, float radius)
        => [new(center.X - radius, center.Y - radius), new(center.X + radius, center.Y - radius), new(center.X + radius, center.Y + radius), new(center.X - radius, center.Y + radius)];

    /// <summary>Splits a polyline into the "on" pieces of a dash pattern.</summary>
    private static List<Vector2[]> ApplyDash(ReadOnlySpan<Vector2> points, bool closed, float[] dash)
    {
        var result = new List<Vector2[]>();
        float total = 0f;
        foreach (float d in dash) total += MathF.Max(d, 0f);
        if (total <= GeometryMath.Epsilon || points.Length < 2) return result;

        var current = new List<Vector2>(16);
        int index = 0;
        float remaining = dash[0];
        bool on = true;
        current.Add(points[0]);

        int count = closed ? points.Length : points.Length - 1;
        for (int i = 0; i < count; i++)
        {
            Vector2 a = points[i];
            Vector2 b = points[(i + 1) % points.Length];
            float length = Vector2.Distance(a, b);
            float position = 0f;
            while (length - position > remaining)
            {
                position += remaining;
                Vector2 split = Vector2.Lerp(a, b, position / length);
                if (on)
                {
                    current.Add(split);
                    if (current.Count > 1) result.Add(current.ToArray());
                    current.Clear();
                }
                else
                {
                    current.Clear();
                    current.Add(split);
                }
                on = !on;
                index = (index + 1) % dash.Length;
                remaining = MathF.Max(dash[index], 0.01f);
            }
            remaining -= length - position;
            if (on) current.Add(b);
        }
        if (on && current.Count > 1) result.Add(current.ToArray());
        return result;
    }
}
