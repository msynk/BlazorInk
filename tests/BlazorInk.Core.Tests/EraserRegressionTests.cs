using System.Numerics;
using BlazorInk.Brushes;
using BlazorInk.Geometry;
using BlazorInk.Input;
using BlazorInk.Tools;
using Xunit;
using Xunit.Abstractions;

namespace BlazorInk.Tests.Core;

/// <summary>
/// Eraser regressions. The segment eraser cuts a stroke's shape, not the points that happen to be stored, so a
/// simplified straight line is cut where the eraser actually crosses it.
/// </summary>
public class EraserRegressionTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(EraserMode.Stroke)]
    [InlineData(EraserMode.Segment)]
    [InlineData(EraserMode.Pixel)]
    public void An_eraser_gesture_is_exactly_one_undo_step(EraserMode mode)
    {
        using var context = new TestToolContext();
        for (int i = 0; i < 6; i++)
            context.Document.AddElement(TestData.Stroke(new Vector2(50, 40 + i * 12), new Vector2(350, 40 + i * 12), new PenBrush { Width = 3 }, 40));
        int steps = context.History.UndoCount;

        var eraser = new EraserTool { Mode = mode, Radius = 14 };
        eraser.Activate(context);
        context.Line(eraser, new Vector2(200, 20), new Vector2(200, 140), 30);

        output.WriteLine($"{mode}: undo steps added = {context.History.UndoCount - steps}, elements = {context.Document.ElementCount}");
        Assert.Equal(steps + 1, context.History.UndoCount);
        Assert.True(context.History.Undo());
        Assert.Equal(6, context.Document.ElementCount);
        Assert.Equal(steps, context.History.UndoCount);
    }

    [Fact]
    public void Segment_erasing_a_stroke_repeatedly_stays_finite()
    {
        using var context = new TestToolContext();
        context.Document.AddElement(TestData.Stroke(new Vector2(20, 100), new Vector2(700, 100), new PenBrush { Width = 4 }, 400));
        var eraser = new EraserTool { Mode = EraserMode.Segment, Radius = 3 };
        eraser.Activate(context);
        for (int i = 0; i < 40; i++)
        {
            float x = 40 + i * 16;
            context.Line(eraser, new Vector2(x, 80), new Vector2(x, 120), 6);
        }
        output.WriteLine($"fragments: {context.Document.ElementCount}, points {context.Document.TotalPointCount}");
        Assert.True(context.Document.ElementCount < 200, $"{context.Document.ElementCount} fragments");
        foreach (InkElement e in context.Document.AllElements)
        {
            Stroke s = Assert.IsType<Stroke>(e);
            Assert.True(s.Points.Count > 0, "an empty fragment survived erasing");
        }
    }

    [Fact]
    public void Segment_erasing_cuts_a_stroke_whose_points_are_far_apart()
    {
        // Simplification leaves a straight stroke with only its two endpoints. Classifying stored points alone
        // would let the eraser pass straight through the gap between them.
        var points = StrokePoints.FromPositions([new Vector2(0, 100), new Vector2(400, 100)]);
        var stroke = new Stroke(points, new PenBrush { Width = 4 });
        var fragments = new List<Stroke>();

        bool changed = StrokeEditor.EraseSegments(stroke, [new Vector2(200, 100)], 12f, fragments);
        output.WriteLine($"changed={changed} fragments={fragments.Count} " + string.Join(" | ", fragments.Select(f => f.Bounds.ToString())));
        Assert.True(changed, "erasing the middle of a two-point stroke did nothing");
        Assert.Equal(2, fragments.Count);
    }

    [Fact]
    public void Segment_erasing_one_end_of_a_two_point_stroke_keeps_the_rest()
    {
        // Touching one endpoint used to leave a single surviving point, which is dropped as a dot, deleting the
        // whole stroke.
        var points = StrokePoints.FromPositions([new Vector2(0, 100), new Vector2(400, 100)]);
        var stroke = new Stroke(points, new PenBrush { Width = 4 });
        var fragments = new List<Stroke>();

        bool changed = StrokeEditor.EraseSegments(stroke, [new Vector2(0, 100)], 12f, fragments);
        output.WriteLine($"changed={changed} fragments={fragments.Count} " + string.Join(" | ", fragments.Select(f => f.Bounds.ToString())));
        Assert.True(changed);
        Assert.Single(fragments);
        Assert.True(fragments[0].Bounds.Width > 300, $"most of the stroke should survive but only {fragments[0].Bounds.Width} units remain");
    }

    [Fact]
    public void Erasing_the_middle_of_a_drawn_straight_line_leaves_a_real_gap()
    {
        using var context = new TestToolContext();
        var pen = new DrawTool();
        pen.Activate(context);
        context.Line(pen, new Vector2(40, 200), new Vector2(560, 200), 60);
        Stroke drawn = Assert.IsType<Stroke>(Assert.Single(context.Document.AllElements));
        output.WriteLine($"the committed line kept {drawn.Points.Count} points after simplification");
        pen.Deactivate(context);

        var eraser = new EraserTool { Mode = EraserMode.Segment, Radius = 20 };
        eraser.Activate(context);
        context.Line(eraser, new Vector2(300, 170), new Vector2(300, 230), 10);

        var pieces = context.Document.AllElements.Cast<Stroke>().OrderBy(e => e.Bounds.Left).ToList();
        output.WriteLine("pieces: " + string.Join(" | ", pieces.Select(f => $"{f.Bounds.Left:0}..{f.Bounds.Right:0}")));
        Assert.Equal(2, pieces.Count);
        float gap = pieces[1].Bounds.Left - pieces[0].Bounds.Right;
        Assert.True(gap > 10f, $"the eraser left a gap of only {gap} units");
        Assert.True(pieces[0].Bounds.Left < 60f && pieces[1].Bounds.Right > 540f, "the ends of the line were lost");
    }

    [Fact]
    public void A_second_pointer_after_a_mode_change_does_not_strand_the_gesture()
    {
        using var context = new TestToolContext();
        context.Document.AddElement(new Stroke(TestData.Line(new Vector2(0, 100), new Vector2(400, 100), 40), new PenBrush { Width = 4f }));
        context.History.Clear();
        var eraser = new EraserTool { Mode = EraserMode.Stroke, Radius = 10f };

        static InputSample S(int id, PointerPhase phase, float x, float y) => new()
        {
            PointerId = id, Kind = PointerKind.Touch, Phase = phase, X = x, Y = y, Pressure = 0.5f,
            Buttons = phase == PointerPhase.Up ? 0 : 1, IsPrimary = id == 1,
        };

        Assert.True(eraser.OnPointerDown(context, S(1, PointerPhase.Down, 200, 100)));
        eraser.Mode = EraserMode.Pixel;
        // A second finger lands while the first is still erasing. Latching the new mode for it used to route the
        // first finger's release to the pixel path, which ignored it and left the history group open.
        Assert.False(eraser.OnPointerDown(context, S(2, PointerPhase.Down, 300, 300)));
        eraser.OnPointerUp(context, S(1, PointerPhase.Up, 200, 100));

        Assert.False(eraser.IsErasing);
        Assert.False(eraser.IsDrawing);
        Assert.Equal(0, context.Document.ElementCount);
        Assert.True(context.History.Undo(), "the erase was not recorded, or history was left disabled");
        Assert.Equal(1, context.Document.ElementCount);
    }

    [Fact]
    public void The_segment_eraser_does_not_cut_pixel_eraser_masks()
    {
        // Cutting a mask brings back the ink it hides, which is the opposite of erasing.
        using var context = new TestToolContext();
        var mask = new Stroke(TestData.Line(new Vector2(100, 0), new Vector2(100, 200), 40), new PixelEraserBrush { Width = 20f });
        context.Document.AddElement(mask);
        var eraser = new EraserTool { Mode = EraserMode.Segment, Radius = 10f };

        context.Line(eraser, new Vector2(50, 100), new Vector2(150, 100), 10);

        Assert.Equal(1, context.Document.ElementCount);
        Assert.Same(mask, context.Document.GetElement(mask.Id));
    }
}
