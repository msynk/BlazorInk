using System.Numerics;
using BlazorInk.Geometry;
using Xunit;

namespace BlazorInk.Tests.Core;

public class RectTests
{
    [Fact]
    public void FromPoints_normalizes_any_corner_order()
    {
        Rect a = Rect.FromPoints(new Vector2(10, 20), new Vector2(4, 5));
        Assert.Equal(new Rect(4, 5, 6, 15), a);
    }

    [Fact]
    public void Union_ignores_empty_rectangles()
    {
        var r = new Rect(1, 2, 3, 4);
        Assert.Equal(r, Rect.Empty.Union(r));
        Assert.Equal(r, r.Union(Rect.Empty));
    }

    [Fact]
    public void Union_with_point_grows_a_degenerate_rectangle()
    {
        Rect r = Rect.Empty.Union(new Vector2(5, 5)).Union(new Vector2(10, 2));
        Assert.Equal(Rect.FromLTRB(5, 2, 10, 5), r);
    }

    [Fact]
    public void Intersect_returns_empty_when_disjoint()
    {
        Assert.True(new Rect(0, 0, 5, 5).Intersect(new Rect(10, 10, 5, 5)).IsEmpty);
    }

    [Fact]
    public void Transform_returns_the_bounding_box_of_a_rotated_rectangle()
    {
        var r = new Rect(0, 0, 10, 10);
        Rect rotated = r.Transform(Matrix3x2.CreateRotation(MathF.PI / 4f, new Vector2(5, 5)));
        // A square rotated 45 degrees around its center has a bounding box of side length 10*sqrt(2).
        Assert.Equal(10 * MathF.Sqrt(2), rotated.Width, 3);
        Assert.Equal(10 * MathF.Sqrt(2), rotated.Height, 3);
        Assert.Equal(5f, rotated.Center.X, 3);
    }

    [Fact]
    public void Contains_includes_the_edges()
    {
        var r = new Rect(0, 0, 10, 10);
        Assert.True(r.Contains(new Vector2(0, 0)));
        Assert.True(r.Contains(new Vector2(10, 10)));
        Assert.False(r.Contains(new Vector2(10.01f, 5)));
    }
}

public class InkColorTests
{
    [Theory]
    [InlineData("#000", 0, 0, 0, 255)]
    [InlineData("#fff", 255, 255, 255, 255)]
    [InlineData("#ff0000", 255, 0, 0, 255)]
    [InlineData("#00ff0080", 0, 255, 0, 128)]
    [InlineData("#1234", 17, 34, 51, 68)]
    [InlineData("rgb(10, 20, 30)", 10, 20, 30, 255)]
    [InlineData("rgba(10, 20, 30, 0.5)", 10, 20, 30, 128)]
    [InlineData("black", 0, 0, 0, 255)]
    public void Parses_supported_syntaxes(string input, int r, int g, int b, int a)
    {
        InkColor color = InkColor.Parse(input);
        Assert.Equal(new InkColor((byte)r, (byte)g, (byte)b, (byte)a), color);
    }

    [Theory]
    [InlineData("")]
    [InlineData("#12345")]
    [InlineData("#gg0000")]
    [InlineData("not-a-color")]
    [InlineData("rgb(1,2)")]
    public void Rejects_invalid_input(string input)
    {
        Assert.False(InkColor.TryParse(input, out _));
    }

    [Fact]
    public void ToCss_omits_alpha_when_opaque()
    {
        Assert.Equal("#ff8000", new InkColor(255, 128, 0).ToCss());
        Assert.Equal("#ff800080", new InkColor(255, 128, 0, 128).ToCss());
    }

    [Fact]
    public void ToCss_round_trips_through_Parse()
    {
        var original = new InkColor(12, 34, 56, 78);
        Assert.Equal(original, InkColor.Parse(original.ToCss()));
    }

    [Fact]
    public void ToCss_only_emits_characters_that_are_safe_in_css_and_svg()
    {
        for (int i = 0; i < 256; i += 17)
        {
            string css = new InkColor((byte)i, (byte)(255 - i), (byte)i, (byte)i).ToCss();
            Assert.Matches("^#[0-9a-f]{6,8}$", css);
        }
    }
}

public class GeometryMathTests
{
    [Fact]
    public void DistanceToSegment_handles_degenerate_segments()
    {
        float d = GeometryMath.DistanceToSegment(new Vector2(3, 4), Vector2.Zero, Vector2.Zero);
        Assert.Equal(5f, d, 4);
    }

    [Fact]
    public void DistanceToSegment_clamps_to_the_endpoints()
    {
        var a = new Vector2(0, 0);
        var b = new Vector2(10, 0);
        Assert.Equal(5f, GeometryMath.DistanceToSegment(new Vector2(-5, 0), a, b), 4);
        Assert.Equal(3f, GeometryMath.DistanceToSegment(new Vector2(5, 3), a, b), 4);
    }

    [Fact]
    public void SegmentsIntersect_detects_crossing_and_touching()
    {
        Assert.True(GeometryMath.SegmentsIntersect(new Vector2(0, 0), new Vector2(10, 10), new Vector2(0, 10), new Vector2(10, 0)));
        Assert.True(GeometryMath.SegmentsIntersect(new Vector2(0, 0), new Vector2(10, 0), new Vector2(10, 0), new Vector2(10, 10)));
        Assert.False(GeometryMath.SegmentsIntersect(new Vector2(0, 0), new Vector2(1, 0), new Vector2(5, 5), new Vector2(6, 6)));
    }

    [Fact]
    public void PointInPolygon_uses_the_nonzero_rule()
    {
        Vector2[] square = [new(0, 0), new(10, 0), new(10, 10), new(0, 10)];
        Assert.True(GeometryMath.PointInPolygon(new Vector2(5, 5), square));
        Assert.False(GeometryMath.PointInPolygon(new Vector2(15, 5), square));
    }

    [Fact]
    public void Simplify_keeps_the_endpoints_and_removes_collinear_points()
    {
        float[] xs = [0, 1, 2, 3, 4, 5];
        float[] ys = [0, 0, 0, 0, 0, 0];
        var keep = new List<int>();
        GeometryMath.Simplify(xs, ys, 0.01f, keep);
        Assert.Equal([0, 5], keep);
    }

    [Fact]
    public void Simplify_preserves_a_significant_deviation()
    {
        float[] xs = [0, 5, 10];
        float[] ys = [0, 8, 0];
        var keep = new List<int>();
        GeometryMath.Simplify(xs, ys, 1f, keep);
        Assert.Equal([0, 1, 2], keep);
    }

    [Fact]
    public void Simplify_handles_very_long_inputs_without_stack_overflow()
    {
        const int n = 200_000;
        float[] xs = new float[n];
        float[] ys = new float[n];
        for (int i = 0; i < n; i++)
        {
            xs[i] = i * 0.1f;
            ys[i] = MathF.Sin(i * 0.01f) * 50f;
        }
        var keep = new List<int>();
        GeometryMath.Simplify(xs, ys, 0.5f, keep);
        Assert.True(keep.Count > 2 && keep.Count < n);
        Assert.Equal(0, keep[0]);
        Assert.Equal(n - 1, keep[^1]);
    }
}

public class PathGeometryTests
{
    [Fact]
    public void Builder_produces_svg_path_data_in_invariant_culture()
    {
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            PathGeometry path = new PathBuilder().MoveTo(1.5f, 2.25f).LineTo(3f, 4f).ToGeometry();
            Assert.Equal("M1.5,2.25 L3,4", path.ToSvgPathData());
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Flatten_produces_a_polyline_per_subpath()
    {
        PathGeometry path = new PathBuilder()
            .MoveTo(0, 0).LineTo(10, 0)
            .MoveTo(0, 10).LineTo(10, 10)
            .ToGeometry();

        var subPaths = new List<int>();
        path.Flatten(0.1f, (points, _) => subPaths.Add(points.Length));
        Assert.Equal([2, 2], subPaths);
    }

    [Fact]
    public void Flatten_stays_within_the_requested_tolerance_of_the_true_curve()
    {
        Vector2 p0 = new(0, 0), p1 = new(0, 100), p2 = new(100, 100), p3 = new(100, 0);
        PathGeometry path = new PathBuilder().MoveTo(p0).CubicTo(p1, p2, p3).ToGeometry();
        const float tolerance = 0.1f;
        var points = new List<Vector2>();
        path.Flatten(tolerance, (p, _) => points.AddRange(p.Span));

        // Every densely sampled point of the analytic curve must lie close to the polyline.
        for (int i = 0; i <= 1000; i++)
        {
            float t = i / 1000f, mt = 1 - t;
            Vector2 exact = p0 * (mt * mt * mt) + p1 * (3 * mt * mt * t) + p2 * (3 * mt * t * t) + p3 * (t * t * t);
            float best = float.MaxValue;
            for (int s = 1; s < points.Count; s++) best = MathF.Min(best, GeometryMath.DistanceToSegment(exact, points[s - 1], points[s]));
            Assert.True(best <= tolerance, $"point at t={t} was {best} away from the flattened polyline");
        }
        Assert.Equal(p3, points[^1]);
    }

    [Fact]
    public void Transform_updates_coordinates_and_bounds()
    {
        PathGeometry path = new PathBuilder().AddRect(new Rect(0, 0, 10, 10)).ToGeometry();
        PathGeometry moved = path.Transform(Matrix3x2.CreateTranslation(5, 5));
        Assert.Equal(new Rect(5, 5, 10, 10), moved.Bounds);
        Assert.Equal(new Rect(0, 0, 10, 10), path.Bounds);
    }

    [Fact]
    public void AddEllipse_approximates_a_circle_closely()
    {
        PathGeometry path = new PathBuilder().AddCircle(new Vector2(0, 0), 100f).ToGeometry();
        var points = new List<Vector2>();
        path.Flatten(0.05f, (p, _) => points.AddRange(p.Span));
        foreach (Vector2 p in points)
        {
            Assert.Equal(100f, p.Length(), 0);
        }
    }

    [Fact]
    public void Svg_path_data_never_contains_scientific_notation()
    {
        PathGeometry path = new PathBuilder().MoveTo(0.0000001f, 123456.789f).LineTo(1e-9f, 1f).ToGeometry();
        string data = path.ToSvgPathData(3);
        Assert.DoesNotContain("E", data, StringComparison.OrdinalIgnoreCase);
    }
}
