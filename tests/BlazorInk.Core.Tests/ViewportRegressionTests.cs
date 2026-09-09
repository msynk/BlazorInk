using System.Numerics;
using BlazorInk.Brushes;
using BlazorInk.Geometry;
using BlazorInk.Input;
using BlazorInk.Tools;
using Xunit;
using Xunit.Abstractions;

namespace BlazorInk.Tests.Core;

/// <summary>
/// Coordinate and viewport regressions: drawing, hit testing and zooming have to agree at every zoom level,
/// and a viewport built with an object initializer must not be able to divide by zero.
/// </summary>
public class ViewportRegressionTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(0.05f)]
    [InlineData(0.5f)]
    [InlineData(1f)]
    [InlineData(1.5f)]
    [InlineData(2f)]
    [InlineData(3.7f)]
    [InlineData(16f)]
    [InlineData(64f)]
    public void Drawing_at_any_zoom_lands_at_the_same_document_position(float zoom)
    {
        using var context = new TestToolContext();
        context.Viewport = Viewport.Identity.ZoomTo(zoom, new Vector2(400, 300)).Pan(new Vector2(37, -19));
        var tool = new DrawTool();
        tool.Activate(context);

        Vector2 docFrom = new(120, 90), docTo = new(260, 210);
        Vector2 viewFrom = context.Viewport.ToView(docFrom), viewTo = context.Viewport.ToView(docTo);
        context.Line(tool, viewFrom, viewTo, 40);

        Stroke stroke = Assert.IsType<Stroke>(Assert.Single(context.Document.AllElements));
        Vector2 first = stroke.Points.PositionAt(0);
        Vector2 last = stroke.Points.PositionAt(stroke.Points.Count - 1);
        output.WriteLine($"zoom {zoom}: first={first} last={last} n={stroke.Points.Count}");
        Assert.True(Vector2.Distance(first, docFrom) < 1.0f, $"start off by {Vector2.Distance(first, docFrom)} at zoom {zoom}");
        Assert.True(Vector2.Distance(last, docTo) < 1.0f, $"end off by {Vector2.Distance(last, docTo)} at zoom {zoom}");
    }

    [Theory]
    [InlineData(0.05f)]
    [InlineData(1f)]
    [InlineData(2.5f)]
    [InlineData(64f)]
    public void Hit_testing_finds_a_stroke_at_any_zoom(float zoom)
    {
        using var context = new TestToolContext();
        context.Document.AddElement(TestData.Stroke(new Vector2(100, 100), new Vector2(300, 100), new PenBrush { Width = 4 }));
        context.Viewport = Viewport.Identity.ZoomTo(zoom, new Vector2(400, 300));
        var select = new SelectTool();
        select.Activate(context);
        Vector2 view = context.Viewport.ToView(new Vector2(200, 100));
        context.Gesture(select, PointerKind.Mouse, (view.X, view.Y, 0.5f), (view.X, view.Y, 0.5f));
        Assert.Equal(1, context.Selection.Count);
    }

    [Fact]
    public void Zooming_keeps_the_anchor_point_fixed()
    {
        var vp = new Viewport(1f, new Vector2(10, -5));
        var anchor = new Vector2(321, 177);
        Vector2 before = vp.ToDocument(anchor);
        for (int i = 0; i < 40; i++)
        {
            vp = vp.ZoomBy(1.19f, anchor);
            Vector2 after = vp.ToDocument(anchor);
            Assert.True(Vector2.Distance(before, after) < 0.01f, $"drifted {Vector2.Distance(before, after)} at step {i} zoom {vp.Zoom}");
        }
    }

    [Fact]
    public void A_degenerate_viewport_cannot_be_built_with_an_object_initializer()
    {
        // A record struct's init accessors and its default value both bypass the constructor, so the guard has to
        // live on the property. A zoom of zero would turn every document coordinate into infinity.
        Viewport bad = Viewport.Identity with { Zoom = 0f };
        Vector2 p = bad.ToDocument(new Vector2(10, 10));
        output.WriteLine($"zoom=0 -> {p}");
        Assert.True(float.IsFinite(p.X) && float.IsFinite(p.Y), "zoom 0 produced non-finite document coordinates");

        Vector2 fromDefault = default(Viewport).ToDocument(new Vector2(10, 10));
        Assert.True(float.IsFinite(fromDefault.X) && float.IsFinite(fromDefault.Y), "default(Viewport) produced non-finite coordinates");
    }

    [Fact]
    public void Negative_zoom_does_not_mirror_the_document()
    {
        Viewport bad = Viewport.Identity with { Zoom = -2f };
        output.WriteLine($"negative zoom read back as: {bad.Zoom}");
        Assert.True(bad.Zoom > 0, "a negative zoom was accepted through a with-expression");
    }

    [Fact]
    public void A_non_finite_offset_is_refused()
    {
        Viewport bad = Viewport.Identity with { Offset = new Vector2(float.NaN, float.PositiveInfinity) };
        Assert.True(float.IsFinite(bad.Offset.X) && float.IsFinite(bad.Offset.Y));
    }

    [Fact]
    public void A_transform_cannot_push_an_element_beyond_the_coordinate_limit()
    {
        // Validating only the local box would let an imported document scale modest coordinates millions of units
        // away, which makes zoom-to-fit and export meaningless.
        var document = new InkDocument();
        Stroke stroke = TestData.Stroke(new Vector2(0, 0), new Vector2(10, 10));
        var scaled = (Stroke)stroke.TransformBy(Matrix3x2.CreateScale(1_000_000f));
        output.WriteLine($"local={scaled.LocalBounds} document={scaled.Bounds}");
        Assert.Throws<InvalidOperationException>(() => document.AddElement(scaled));
    }
}
