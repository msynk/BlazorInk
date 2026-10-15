using System.Globalization;
using System.Numerics;
using BlazorInk.Brushes;
using BlazorInk.Geometry;
using BlazorInk.Raster;
using BlazorInk.Rendering;
using Xunit;

namespace BlazorInk.Tests.Core;

/// <summary>
/// Regressions in path flattening, stroking, stroke outlines, bounds and hit testing.
/// </summary>
public class GeometryRegressionTests
{
    private static RasterSurface Draw(int width, int height, Action<RasterRenderContext> draw)
    {
        var surface = new RasterSurface(width, height);
        var context = new RasterRenderContext(surface, Matrix3x2.Identity);
        draw(context);
        return surface;
    }

    /// <summary>
    /// A Close leaves the sub-path start behind so a following LineTo can continue from it. When a MoveTo followed
    /// instead, that lone point was emitted as a sub-path of its own, and the stroker drew a cap there.
    /// </summary>
    [Fact]
    public void Flatten_does_not_emit_the_point_a_close_leaves_behind()
    {
        PathGeometry path = new PathBuilder().AddRect(new Rect(10, 10, 30, 30)).AddRect(new Rect(60, 60, 30, 30)).ToGeometry();

        var subPaths = new List<(int Count, bool Closed)>();
        path.Flatten(0.25f, (points, closed) => subPaths.Add((points.Length, closed)));

        Assert.Equal(new[] { (4, true), (4, true) }, subPaths);
    }

    [Fact]
    public void Stroking_several_closed_sub_paths_draws_no_cap_at_their_starts()
    {
        PathGeometry path = new PathBuilder().AddRect(new Rect(10, 10, 30, 30)).AddRect(new Rect(60, 60, 30, 30)).ToGeometry();

        RasterSurface surface = Draw(100, 100, c => c.StrokePath(path, new StrokePaint(InkColor.Black, 10f, Cap: LineCap.Square, Join: LineJoin.Round)));

        // (5.5, 5.5) is 6.4 units from the first rectangle's corner: outside its round join, inside a square cap.
        Assert.Equal(0, surface.GetPixel(5, 5).A);
        Assert.Equal(0, surface.GetPixel(55, 55).A);
        Assert.Equal(255, surface.GetPixel(10, 25).A);
    }

    /// <summary>
    /// A path that doubles back on itself is collinear at the turn, which the stroker treated as needing no join.
    /// The round join canvas and SVG draw there was missing, leaving a flat end in the middle of the stroke.
    /// </summary>
    [Fact]
    public void A_round_join_is_drawn_where_a_path_doubles_back()
    {
        PathGeometry path = new PathBuilder().MoveTo(5, 10).LineTo(30, 10).LineTo(10, 10).ToGeometry();

        RasterSurface surface = Draw(60, 20, c => c.StrokePath(path, new StrokePaint(InkColor.Black, 8f, Cap: LineCap.Butt, Join: LineJoin.Round)));

        // (32.5, 10.5) is 2.5 units past the turn: inside the round join of radius 4.
        Assert.True(surface.GetPixel(32, 10).A > 200, $"alpha was {surface.GetPixel(32, 10).A}");
        Assert.Equal(0, surface.GetPixel(36, 10).A);
    }

    /// <summary>The full-circle arc of a dot skipped its first vertex, flattening one side of every dot.</summary>
    [Fact]
    public void A_dot_outline_is_a_complete_circle()
    {
        var center = new Vector2(50, 50);
        var points = StrokePoints.FromPositions([center], pressure: 1f);
        var polygon = new List<Vector2>();
        StrokeOutliner.BuildPolygon(points.AsView(), new OutlineOptions(20f) { PressureCurve = PressureCurve.Constant }, polygon);

        Assert.Equal(16, polygon.Count);
        Rect bounds = GeometryMath.Bounds(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(polygon));
        Assert.Equal(center.X - bounds.Left, bounds.Right - center.X, 3);
        Assert.Equal(10f, bounds.Right - center.X, 3);
    }

    /// <summary>
    /// Marker and highlighter strokes are drawn as a Catmull-Rom spline, which overshoots its points at a hairpin
    /// turn. Their bounds came from the points alone, so the overshoot was clipped and culled.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Centerline_stroke_bounds_cover_the_spline_overshoot(bool highlighter)
    {
        Brush brush = highlighter ? new HighlighterBrush { Width = 8f } : new MarkerBrush { Width = 8f };
        var points = StrokePoints.FromPositions([new(0, 0), new(100, 0), new(100, 1), new(0, 1)]);

        // Before the geometry exists the bounds are the brush's estimate; afterwards they are the geometry's.
        var stroke = new Stroke(points, brush);
        Rect estimate = stroke.LocalBounds;
        Assert.False(stroke.HasGeometry);
        StrokeGeometry geometry = stroke.Geometry;
        Rect exact = stroke.LocalBounds;

        float maxX = float.MinValue;
        geometry.Path.Flatten(0.05f, (pts, _) =>
        {
            foreach (Vector2 p in pts.Span) maxX = MathF.Max(maxX, p.X);
        });
        float paintedRight = maxX + brush.Width / 2f;

        Assert.True(paintedRight > 105f, $"the test no longer exercises the overshoot (curve reaches {maxX})");
        Assert.True(exact.Right >= paintedRight, $"geometry bounds end at {exact.Right}, ink reaches {paintedRight}");
        Assert.True(estimate.Right >= paintedRight, $"estimated bounds end at {estimate.Right}, ink reaches {paintedRight}");
    }

    /// <summary>
    /// Under rotation, mapping the selection rectangle into local space and taking its bounding box covers much
    /// more than the rectangle, so a marquee selected strokes it did not touch.
    /// </summary>
    [Fact]
    public void A_rotated_stroke_is_not_selected_by_a_rectangle_that_misses_it()
    {
        var points = StrokePoints.FromPositions([new(0, 0), new(100, 0), new(100, 100), new(0, 100)]);
        var stroke = new Stroke(points, new PenBrush { Width = 2f }) { Transform = Matrix3x2.CreateRotation(MathF.PI / 4f) };

        // 8.5 units left of where the stroke starts, but inside the stroke's bounding box.
        var miss = new Rect(-36.77f, -14.14f, 28.28f, 28.28f);
        Assert.True(stroke.Bounds.Intersects(miss));
        Assert.False(stroke.IntersectsRect(miss));

        // The first segment runs along y = x in document space.
        Assert.True(stroke.IntersectsRect(new Rect(30, 30, 10, 10)));
    }

    /// <summary>Path data must stay well formed for any finite coordinate; large ones overflowed the format buffer.</summary>
    [Fact]
    public void Svg_path_data_keeps_large_coordinates()
    {
        PathGeometry path = new PathBuilder().MoveTo(-3e38f, 5).LineTo(1e30f, 5).ToGeometry();

        string data = path.ToSvgPathData(6);

        string[] parts = data.Split(' ');
        Assert.Equal(2, parts.Length);
        Assert.Equal(-3e38f, float.Parse(parts[0][1..].Split(',')[0], CultureInfo.InvariantCulture), 1e33f);
        Assert.Equal(1e30f, float.Parse(parts[1][1..].Split(',')[0], CultureInfo.InvariantCulture), 1e25f);
    }

    /// <summary>
    /// The fast fixed-point formatter must produce exactly what the general "F" formatter (plus trimming) did, so
    /// SVG output is byte-identical. Midpoints of the binary value are the case most likely to differ.
    /// </summary>
    [Fact]
    public void Svg_number_formatting_matches_the_general_formatter()
    {
        static string Reference(float value, int decimals)
        {
            if (!float.IsFinite(value)) value = 0;
            string s = value.ToString("F" + decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
            if (s.Contains('.')) s = s.TrimEnd('0').TrimEnd('.');
            return s is "" or "-0" ? "0" : s;
        }

        var values = new List<float>
        {
            0f, -0f, 1f, -1f, 0.5f, -0.5f, 2.5f, -2.5f, 0.125f, -0.125f, 0.375f, 1.005f, 2.675f, 0.0049999f, -0.0049999f,
            1e-7f, -1e-7f, 1e-30f, float.Epsilon, -float.Epsilon, 123456.789f, -98765.4321f, 999999.9999f,
            8388607.5f, 16777215f, 999_999_900f, -999_999_900f, 1e9f, -1e9f, 3e12f, float.MaxValue, float.MinValue,
            float.NaN, float.PositiveInfinity, float.NegativeInfinity,
        };
        // Every dyadic fraction with a small denominator hits exact decimal midpoints for some precision.
        for (int k = -4096; k <= 4096; k++) values.Add(k / 4096f * 37f);
        for (int k = -2000; k <= 2000; k++) values.Add(k / 128f);
        var random = new Random(1234);
        for (int i = 0; i < 200_000; i++)
        {
            float v = BitConverter.Int32BitsToSingle(random.Next(int.MinValue, int.MaxValue));
            if (float.IsFinite(v) && MathF.Abs(v) < 2e9f) values.Add(v);
            values.Add((float)((random.NextDouble() - 0.5) * Math.Pow(10, random.Next(-8, 10))));
        }

        Span<char> buffer = stackalloc char[SvgNumber.MaxLength];
        foreach (float value in values)
        {
            for (int decimals = 0; decimals <= 6; decimals++)
            {
                string actual = buffer[..SvgNumber.Format(value, decimals, buffer)].ToString();
                string expected = Reference(value, decimals);
                if (actual != expected)
                {
                    Assert.Fail($"{value.ToString("R", CultureInfo.InvariantCulture)} with {decimals} decimals: expected {expected}, got {actual}");
                }
            }
        }
    }

    /// <summary>Refining the last sample (what spacing does) dropped tilt the buffer had not allocated yet.</summary>
    [Fact]
    public void Replacing_the_last_point_keeps_its_tilt_and_twist()
    {
        var buffer = new StrokePointBuffer();
        buffer.Add(new InkPoint(1, 2));
        buffer.ReplaceLast(new InkPoint(1, 2) { TiltX = 30, TiltY = -10, Twist = 90 });

        StrokePoints points = buffer.ToStrokePoints();
        Assert.Equal(PointChannels.Tilt | PointChannels.Twist, points.Channels);
        Assert.Equal(30f, points[0].TiltX);
        Assert.Equal(-10f, points[0].TiltY);
        Assert.Equal(90f, points[0].Twist);
    }

    [Fact]
    public void Tilt_must_be_supplied_for_both_axes()
    {
        float[] xs = [0, 1];
        Assert.Throws<ArgumentException>(() => StrokePoints.FromArrays(xs, xs, tiltY: xs));
    }

    /// <summary>
    /// Dashing cost one polygon per dash along the whole path, so a huge shape with a tiny dash (clamped only to
    /// 0.05 device pixels) could exhaust memory during a server-side export. Only the visible part is cut now.
    /// </summary>
    [Fact]
    public void A_tiny_dash_along_a_huge_path_is_only_cut_where_it_can_be_seen()
    {
        PathGeometry path = new PathBuilder().MoveTo(-1e7f, 50).LineTo(1e7f, 50).ToGeometry();
        var output = new List<Vector2[]>();

        PathStroker.Stroke(path, 2f, LineCap.Butt, LineJoin.Miter, 0.25f, [0.5f, 0.5f], output, new Rect(0, 0, 100, 100));

        // About a hundred dashes cross the visible 100 pixels; the twenty million elsewhere are never made.
        Assert.InRange(output.Count, 90, 130);
    }

    [Fact]
    public void A_dash_too_dense_to_cut_is_stroked_solid()
    {
        PathGeometry path = new PathBuilder().MoveTo(-1e7f, 50).LineTo(1e7f, 50).ToGeometry();
        var output = new List<Vector2[]>();

        // Without bounds every dash would be needed: far past the limit, so the line is drawn solid instead.
        PathStroker.Stroke(path, 2f, LineCap.Butt, LineJoin.Miter, 0.25f, [0.5f, 0.5f], output);

        Assert.Single(output);
    }

    [Theory]
    [InlineData(new float[] { 7f, 5f })]
    [InlineData(new float[] { 7f, 5f, 3f })]
    public void Skipping_the_unseen_part_of_a_path_keeps_the_dash_phase(float[] dash)
    {
        // The same line drawn twice: once with most of it outside a small surface, where the pattern is advanced
        // without being cut, and once on a surface wide enough to see all of it, where every dash is cut. The
        // part both can see must be dashed identically.
        PathGeometry path = new PathBuilder().MoveTo(-1000.5f, 50).LineTo(100, 50).ToGeometry();
        var paint = new StrokePaint(InkColor.Black, 3f, Cap: LineCap.Butt, Dash: dash);

        RasterSurface small = Draw(100, 100, c => c.StrokePath(path, paint));
        var large = new RasterSurface(1200, 100);
        new RasterRenderContext(large, Matrix3x2.CreateTranslation(1000, 0)).StrokePath(path, paint);

        int inked = 0;
        for (int x = 0; x < 100; x++)
        {
            int a = small.GetPixel(x, 50).A;
            int b = large.GetPixel(x + 1000, 50).A;
            Assert.True(Math.Abs(a - b) <= 4, $"pixel {x}: {a} with the unseen part skipped, {b} with it cut");
            if (a > 128) inked++;
        }
        // And it is still a dashed line, not a solid one.
        Assert.InRange(inked, 30, 75);
    }
}
