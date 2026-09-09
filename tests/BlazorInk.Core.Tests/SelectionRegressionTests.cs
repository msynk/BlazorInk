using System.Numerics;
using BlazorInk.Geometry;
using BlazorInk.Rendering;
using Xunit;
using Xunit.Abstractions;

namespace BlazorInk.Tests.Core;

/// <summary>
/// Numerical stability of repeated transforms, and rendering with an element filter.
/// </summary>
public class SelectionRegressionTests(ITestOutputHelper output)
{
    [Fact]
    public void Repeated_rotation_returns_to_the_original_bounds()
    {
        using var context = new TestToolContext();
        var rect = new RectangleShape(new Rect(100, 100, 120, 60));
        context.Document.AddElement(rect);
        context.Selection.Set(rect.Id);
        Rect before = context.Document.GetElement(rect.Id).Bounds;

        for (int i = 0; i < 4; i++) TransformSelection(context, Matrix3x2.CreateRotation(MathF.PI / 2f, before.Center));
        Rect after = context.Document.GetElement(rect.Id).Bounds;
        output.WriteLine($"before={before} after={after}");
        Assert.True(Vector2.Distance(before.TopLeft, after.TopLeft) < 0.01f, $"{before} != {after}");
    }

    [Fact]
    public void Many_small_transforms_do_not_degrade_the_element()
    {
        using var context = new TestToolContext();
        Stroke stroke = TestData.Stroke(new Vector2(100, 100), new Vector2(200, 150));
        context.Document.AddElement(stroke);
        context.Selection.Set(stroke.Id);
        Rect before = context.Document.GetElement(stroke.Id).Bounds;
        for (int i = 0; i < 200; i++)
        {
            TransformSelection(context, Matrix3x2.CreateScale(1.01f, before.Center));
            TransformSelection(context, Matrix3x2.CreateScale(1f / 1.01f, before.Center));
        }
        Rect after = context.Document.GetElement(stroke.Id).Bounds;
        output.WriteLine($"before={before} after={after}");
        Assert.True(MathF.Abs(before.Width - after.Width) < 1f, $"width drifted {before.Width} -> {after.Width}");
    }

    [Fact]
    public void Rendering_honours_an_element_filter_supplied_as_a_plain_array()
    {
        // Selection.Ids hands out an array, and probing an array once per element makes a filtered render
        // quadratic in the document size.
        var document = new InkDocument();
        var ids = new List<Guid>();
        for (int i = 0; i < 4000; i++)
        {
            Stroke stroke = TestData.Stroke(new Vector2(i % 200, i / 200f), new Vector2(i % 200 + 5, i / 200f + 5), count: 3);
            document.AddElement(stroke);
            if (i % 2 == 0) ids.Add(stroke.Id);
        }

        var options = new RenderOptions { ElementFilter = ids.ToArray() };
        var context = new CountingRenderContext();

        var clock = System.Diagnostics.Stopwatch.StartNew();
        int drawn = DocumentRenderer.Render(document, context, options);
        clock.Stop();

        output.WriteLine($"drew {drawn} of {document.ElementCount} elements in {clock.Elapsed.TotalMilliseconds:0.00} ms");
        Assert.Equal(ids.Count, drawn);
        // Generous, but it fails outright if the filter is ever probed with something worse than a hash lookup.
        Assert.True(clock.Elapsed.TotalMilliseconds < 2000, $"filtered rendering took {clock.Elapsed.TotalMilliseconds:0} ms");
    }

    private static void TransformSelection(TestToolContext context, Matrix3x2 matrix)
    {
        List<InkElement> moved = [.. context.Selection.Elements.Select(e => e.TransformBy(matrix))];
        context.Document.ReplaceElements(moved, "Transform");
    }

    private sealed class CountingRenderContext : IRenderContext
    {
        public void Save() { }

        public void Restore() { }

        public void Transform(Matrix3x2 matrix) { }

        public void ClipRect(Rect rect) { }

        public void FillPath(PathGeometry path, in FillPaint paint) { }

        public void StrokePath(PathGeometry path, in StrokePaint paint) { }

        public void DrawImage(ImageSource image, Rect destination, Rect? sourceRect, float opacity) { }

        public void DrawText(string text, Vector2 origin, in TextStyle style, float opacity) { }

        public void PushGroup(float opacity, BlendMode blend) { }

        public void PopGroup() { }
    }
}
