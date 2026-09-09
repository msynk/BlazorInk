using System.Numerics;
using BlazorInk.Geometry;
using BlazorInk.Input;
using BlazorInk.Tools;
using Xunit;
using Xunit.Abstractions;

namespace BlazorInk.Tests.Core;

/// <summary>
/// Pointer routing regressions: a gesture belongs to the contact that started it, and nothing another pointer
/// does may corrupt or discard it.
/// </summary>
public class PointerRegressionTests(ITestOutputHelper output)
{
    private static InputSample Sample(int id, PointerPhase phase, float x, float y, double t) => new()
    {
        PointerId = id,
        Kind = PointerKind.Touch,
        Phase = phase,
        X = x,
        Y = y,
        Pressure = 0.5f,
        Timestamp = t,
        Buttons = phase is PointerPhase.Up or PointerPhase.Cancel ? 0 : 1,
    };

    [Fact]
    public void A_stroke_with_many_thousands_of_points_completes()
    {
        using var context = new TestToolContext();
        var tool = new DrawTool();
        tool.Activate(context);
        var random = new Random(3);
        var points = new (float, float, float)[20000];
        for (int i = 0; i < points.Length; i++)
        {
            float t = i / (float)points.Length;
            points[i] = (100 + t * 600 + random.NextSingle() * 3, 300 + MathF.Sin(t * 60) * 120, 0.2f + random.NextSingle() * 0.8f);
        }
        context.Gesture(tool, PointerKind.Pen, points);
        Stroke s = Assert.IsType<Stroke>(Assert.Single(context.Document.AllElements));
        output.WriteLine($"stored {s.Points.Count} of {points.Length} samples");
        Assert.True(s.Points.Count > 100);
        Assert.False(s.Geometry.IsEmpty);
    }

    [Fact]
    public void Interleaved_pointers_do_not_corrupt_a_stroke()
    {
        using var context = new TestToolContext();
        var tool = new DrawTool();
        tool.Activate(context);

        tool.OnPointerDown(context, Sample(1, PointerPhase.Down, 100, 100, 0));
        for (int i = 1; i <= 20; i++)
        {
            tool.OnPointerMove(context, Sample(1, PointerPhase.Move, 100 + i * 5, 100, i * 8));
            // A second, uninvited pointer must not contribute points to the first stroke.
            tool.OnPointerMove(context, Sample(2, PointerPhase.Move, 500, 500, i * 8));
        }
        tool.OnPointerUp(context, Sample(1, PointerPhase.Up, 200, 100, 200));

        Stroke s = Assert.IsType<Stroke>(Assert.Single(context.Document.AllElements));
        output.WriteLine($"bounds {s.Bounds}");
        Assert.True(s.Bounds.Right < 400, $"the second pointer leaked into the stroke: {s.Bounds}");
    }

    [Fact]
    public void A_cancel_for_another_pointer_does_not_kill_the_active_stroke()
    {
        // A resting palm, a contact the browser rejects, or a stray pointer leaving the element all raise a
        // cancel of their own. Acting on those would throw away the stroke the user is still drawing.
        using var context = new TestToolContext();
        var tool = new DrawTool();
        tool.Activate(context);

        tool.OnPointerDown(context, Sample(1, PointerPhase.Down, 100, 100, 0));
        for (int i = 1; i <= 10; i++) tool.OnPointerMove(context, Sample(1, PointerPhase.Move, 100 + i * 8, 100, i * 8));
        tool.OnPointerCancel(context, Sample(9, PointerPhase.Cancel, 0, 0, 90));
        tool.OnPointerUp(context, Sample(1, PointerPhase.Up, 180, 100, 100));
        output.WriteLine($"elements: {context.Document.ElementCount}");
        Assert.Equal(1, context.Document.ElementCount);
    }

    [Theory]
    [InlineData("draw", 1)]
    [InlineData("erase", -1)]
    [InlineData("shape", 1)]
    public void A_cancel_from_an_unrelated_pointer_is_ignored_by_every_tool(string toolId, int expectedDelta)
    {
        using var context = new TestToolContext();
        context.Document.AddElement(TestData.Stroke(new Vector2(80, 80), new Vector2(300, 300), count: 40));
        int before = context.Document.ElementCount;
        ITool tool = toolId switch
        {
            "erase" => new EraserTool { Mode = EraserMode.Stroke, Radius = 12 },
            "shape" => new ShapeTool(ShapeKind.Rectangle),
            _ => new DrawTool(),
        };
        tool.Activate(context);

        Assert.True(tool.OnPointerDown(context, Sample(1, PointerPhase.Down, 90, 90, 0)));
        for (int i = 1; i <= 6; i++) tool.OnPointerMove(context, Sample(1, PointerPhase.Move, 90 + i * 20, 90 + i * 20, i * 10));

        tool.OnPointerCancel(context, Sample(77, PointerPhase.Cancel, 0, 0, 70));
        tool.OnPointerUp(context, Sample(1, PointerPhase.Up, 240, 240, 100));

        // The gesture was still live when the pointer lifted, so it did what the user asked for.
        output.WriteLine($"{toolId}: {before} -> {context.Document.ElementCount} elements");
        Assert.Equal(before + expectedDelta, context.Document.ElementCount);
    }

    [Fact]
    public void A_cancel_from_an_unrelated_pointer_does_not_abandon_a_drag()
    {
        using var context = new TestToolContext();
        // Filled, so a press in the middle of it is a hit: an unfilled shape is only grabbable by its outline.
        var target = new RectangleShape(new Rect(100, 100, 120, 80))
        {
            Style = ShapeStyle.Default with { FillColor = InkColor.Parse("#dbeafe") },
        };
        context.Document.AddElement(target);
        var tool = new SelectTool();
        tool.Activate(context);

        Assert.True(tool.OnPointerDown(context, Sample(1, PointerPhase.Down, 160, 140, 0)));
        for (int i = 1; i <= 6; i++) tool.OnPointerMove(context, Sample(1, PointerPhase.Move, 160 + i * 10, 140 + i * 5, i * 10));
        tool.OnPointerCancel(context, Sample(77, PointerPhase.Cancel, 0, 0, 70));
        tool.OnPointerUp(context, Sample(1, PointerPhase.Up, 220, 170, 100));

        Rect moved = context.Document.GetElement(target.Id).Bounds;
        output.WriteLine($"moved to {moved}");
        Assert.True(moved.X > 140f, $"the drag was abandoned by an unrelated cancel: {moved}");
    }
}
