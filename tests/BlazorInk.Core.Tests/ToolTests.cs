using System.Numerics;
using BlazorInk.Brushes;
using BlazorInk.Geometry;
using BlazorInk.Input;
using BlazorInk.Tools;
using Xunit;

namespace BlazorInk.Tests.Core;

public class DrawToolTests
{
    [Fact]
    public void A_gesture_commits_exactly_one_stroke()
    {
        using var context = new TestToolContext();
        var tool = new DrawTool();
        context.Line(tool, new Vector2(10, 10), new Vector2(200, 120), 30);

        Assert.Equal(1, context.Document.ElementCount);
        Assert.IsType<Stroke>(context.Document.Layers[0].Elements[0]);
    }

    [Fact]
    public void A_committed_stroke_is_recorded_in_history()
    {
        using var context = new TestToolContext();
        var tool = new DrawTool();
        context.Line(tool, new Vector2(10, 10), new Vector2(200, 120), 30);

        Assert.True(context.History.CanUndo);
        Assert.Equal(1, context.History.UndoCount);

        context.History.Undo();
        Assert.Equal(0, context.Document.ElementCount);
    }

    [Fact]
    public void The_stroke_lands_on_the_active_layer()
    {
        using var context = new TestToolContext();
        Layer second = context.Document.AddLayer("Second");
        context.ActiveLayerId = second.Id;

        context.Line(new DrawTool(), new Vector2(0, 0), new Vector2(100, 100), 20);

        Assert.Empty(context.Document.Layers[0].Elements);
        Assert.Single(second.Elements);
    }

    [Fact]
    public void Drawing_is_refused_on_a_locked_layer()
    {
        using var context = new TestToolContext();
        context.Document.UpdateLayer(context.ActiveLayerId, p => p with { IsLocked = true });

        context.Line(new DrawTool(), new Vector2(0, 0), new Vector2(100, 100), 20);
        Assert.Equal(0, context.Document.ElementCount);
    }

    [Fact]
    public void A_single_tap_still_leaves_a_mark()
    {
        using var context = new TestToolContext();
        context.Gesture(new DrawTool(), PointerKind.Mouse, (50, 50, 0.5f), (50, 50, 0.5f));

        Assert.Equal(1, context.Document.ElementCount);
        var stroke = (Stroke)context.Document.Layers[0].Elements[0];
        Assert.False(stroke.Geometry.IsEmpty);
    }

    [Fact]
    public void Escape_cancels_the_stroke_in_progress()
    {
        using var context = new TestToolContext();
        var tool = new DrawTool();
        tool.OnPointerDown(context, new InputSample { PointerId = 1, Phase = PointerPhase.Down, X = 10, Y = 10, Pressure = 0.5f, Buttons = 1 });
        tool.OnPointerMove(context, new InputSample { PointerId = 1, Phase = PointerPhase.Move, X = 60, Y = 60, Pressure = 0.5f, Buttons = 1, Timestamp = 16 });
        Assert.True(tool.IsDrawing);

        Assert.True(tool.OnKeyDown(context, new ToolKeyEvent("Escape", false, false, false)));
        Assert.False(tool.IsDrawing);
        Assert.Equal(0, context.Document.ElementCount);
    }

    [Fact]
    public void The_view_transform_is_applied_so_strokes_land_in_document_space()
    {
        using var context = new TestToolContext { Viewport = new Viewport(2f, new Vector2(100, 50)) };
        context.Line(new DrawTool(), new Vector2(100, 50), new Vector2(300, 250), 20);

        var stroke = (Stroke)context.Document.Layers[0].Elements[0];
        Assert.Equal(0f, stroke.Points.X[0], 1f);
        Assert.Equal(0f, stroke.Points.Y[0], 1f);
        Assert.Equal(100f, stroke.Points.X[^1], 2f);
    }

    [Fact]
    public void The_pen_eraser_button_erases_instead_of_drawing()
    {
        using var context = new TestToolContext();
        var tool = new DrawTool();
        // Pen barrel button 32 is the eraser end of a stylus.
        tool.OnPointerDown(context, new InputSample { PointerId = 1, Kind = PointerKind.Pen, Phase = PointerPhase.Down, X = 10, Y = 10, Pressure = 0.5f, Buttons = 32 });
        tool.OnPointerMove(context, new InputSample { PointerId = 1, Kind = PointerKind.Pen, Phase = PointerPhase.Move, X = 80, Y = 40, Pressure = 0.5f, Buttons = 32, Timestamp = 16 });
        tool.OnPointerUp(context, new InputSample { PointerId = 1, Kind = PointerKind.Pen, Phase = PointerPhase.Up, X = 120, Y = 60, Pressure = 0f, Timestamp = 32 });

        var stroke = (Stroke)context.Document.Layers[0].Elements[0];
        Assert.True(stroke.IsEraser);
    }
}

public class EraserToolTests
{
    private static TestToolContext WithStrokes(int count = 3)
    {
        var context = new TestToolContext();
        for (int i = 0; i < count; i++)
        {
            context.Document.AddElement(new Stroke(TestData.Line(new Vector2(0, 20 + i * 40), new Vector2(400, 20 + i * 40), 60), new PenBrush { Width = 4f }));
        }
        // The fixture's own setup is not user history.
        context.History.Clear();
        return context;
    }

    [Fact]
    public void Stroke_mode_deletes_the_whole_stroke()
    {
        using TestToolContext context = WithStrokes();
        var tool = new EraserTool { Mode = EraserMode.Stroke, Radius = 10f };
        context.Line(tool, new Vector2(200, 20), new Vector2(210, 20), 4);

        Assert.Equal(2, context.Document.ElementCount);
    }

    [Fact]
    public void Segment_mode_splits_a_stroke_and_keeps_the_rest()
    {
        using TestToolContext context = WithStrokes(1);
        var tool = new EraserTool { Mode = EraserMode.Segment, Radius = 12f };
        context.Line(tool, new Vector2(200, 20), new Vector2(205, 20), 3);

        Assert.Equal(2, context.Document.ElementCount);
        Rect first = context.Document.Layers[0].Elements[0].Bounds;
        Rect second = context.Document.Layers[0].Elements[1].Bounds;
        Assert.True(first.Right < second.Left, "the two fragments should sit on either side of the erased gap");
    }

    [Fact]
    public void Pixel_mode_adds_an_eraser_stroke_instead_of_deleting()
    {
        using TestToolContext context = WithStrokes(1);
        var tool = new EraserTool { Mode = EraserMode.Pixel, Radius = 12f };
        context.Line(tool, new Vector2(100, 20), new Vector2(300, 20), 20);

        Assert.Equal(2, context.Document.ElementCount);
        Assert.True(((Stroke)context.Document.Layers[0].Elements[1]).IsEraser);
    }

    [Fact]
    public void A_whole_erase_gesture_is_a_single_undo_step()
    {
        using TestToolContext context = WithStrokes();
        var tool = new EraserTool { Mode = EraserMode.Stroke, Radius = 30f };
        context.Line(tool, new Vector2(0, 20), new Vector2(400, 140), 40);

        Assert.True(context.Document.ElementCount < 3);
        Assert.Equal(1, context.History.UndoCount);

        context.History.Undo();
        Assert.Equal(3, context.Document.ElementCount);
    }

    [Fact]
    public void Erasing_nothing_creates_no_history_entry()
    {
        using TestToolContext context = WithStrokes();
        var tool = new EraserTool { Mode = EraserMode.Stroke, Radius = 5f };
        context.Line(tool, new Vector2(0, 300), new Vector2(400, 320), 20);

        Assert.Equal(3, context.Document.ElementCount);
        Assert.Equal(0, context.History.UndoCount);
    }

    [Fact]
    public void A_fast_drag_does_not_skip_over_thin_strokes()
    {
        using var context = new TestToolContext();
        // Vertical hairlines, one every 20 units.
        for (int i = 0; i < 10; i++)
        {
            context.Document.AddElement(new Stroke(TestData.Line(new Vector2(20 + i * 20, 0), new Vector2(20 + i * 20, 200), 30), new PenBrush { Width = 1f }));
        }

        // Two samples 200 units apart, as a fast flick would produce.
        var tool = new EraserTool { Mode = EraserMode.Stroke, Radius = 6f };
        context.Gesture(tool, PointerKind.Mouse, (10, 100, 0.5f), (210, 100, 0.5f), (210, 100, 0.5f));

        Assert.True(context.Document.ElementCount <= 1, $"the sweep should have erased nearly every line but {context.Document.ElementCount} remain");
    }
}

public class SelectToolTests
{
    private static TestToolContext WithShape(out RectangleShape shape)
    {
        var context = new TestToolContext();
        shape = new RectangleShape(new Rect(100, 100, 100, 60)) { Style = new ShapeStyle { FillColor = InkColor.White, StrokeWidth = 2f } };
        context.Document.AddElement(shape);
        context.History.Clear();
        return context;
    }

    [Fact]
    public void Clicking_an_element_selects_it()
    {
        using TestToolContext context = WithShape(out RectangleShape shape);
        var tool = new SelectTool();
        context.Gesture(tool, PointerKind.Mouse, (150, 130, 0.5f), (150, 130, 0.5f));

        Assert.Equal(1, context.Selection.Count);
        Assert.Equal(shape.Id, context.Selection.Ids[0]);
    }

    [Fact]
    public void Clicking_empty_space_clears_the_selection()
    {
        using TestToolContext context = WithShape(out RectangleShape shape);
        var tool = new SelectTool();
        context.Selection.Set(shape.Id);

        context.Gesture(tool, PointerKind.Mouse, (500, 400, 0.5f), (500, 400, 0.5f));
        Assert.True(context.Selection.IsEmpty);
    }

    [Fact]
    public void Dragging_a_selected_element_moves_it_as_one_undo_step()
    {
        using TestToolContext context = WithShape(out RectangleShape shape);
        var tool = new SelectTool();
        context.Selection.Set(shape.Id);

        context.Line(tool, new Vector2(150, 130), new Vector2(250, 190), 20);

        InkElement moved = context.Document.GetElement(shape.Id);
        Assert.Equal(200f, moved.Bounds.X, 3f);
        Assert.Equal(160f, moved.Bounds.Y, 3f);
        Assert.Equal(1, context.History.UndoCount);

        context.History.Undo();
        Assert.Equal(99f, context.Document.GetElement(shape.Id).Bounds.X, 2f);
    }

    [Fact]
    public void A_marquee_selects_everything_it_covers()
    {
        using var context = new TestToolContext();
        for (int i = 0; i < 4; i++)
        {
            context.Document.AddElement(new RectangleShape(new Rect(i * 100, 50, 50, 50)));
        }

        var tool = new SelectTool();
        context.Line(tool, new Vector2(0, 0), new Vector2(260, 200), 20);

        Assert.Equal(3, context.Selection.Count);
    }

    [Fact]
    public void Arrow_keys_nudge_the_selection()
    {
        using TestToolContext context = WithShape(out RectangleShape shape);
        var tool = new SelectTool();
        context.Selection.Set(shape.Id);

        Assert.True(tool.OnKeyDown(context, new ToolKeyEvent("ArrowRight", false, false, false)));
        Assert.Equal(100f, context.Document.GetElement(shape.Id).Bounds.X, 1f);

        Assert.True(tool.OnKeyDown(context, new ToolKeyEvent("ArrowRight", false, true, false)));
        Assert.Equal(110f, context.Document.GetElement(shape.Id).Bounds.X, 1f);
    }

    [Fact]
    public void Delete_removes_the_selection()
    {
        using TestToolContext context = WithShape(out RectangleShape shape);
        var tool = new SelectTool();
        context.Selection.Set(shape.Id);

        Assert.True(tool.OnKeyDown(context, new ToolKeyEvent("Delete", false, false, false)));
        Assert.Equal(0, context.Document.ElementCount);
    }

    [Fact]
    public void Resizing_from_a_corner_scales_the_element()
    {
        using TestToolContext context = WithShape(out RectangleShape shape);
        var tool = new SelectTool();
        context.Selection.Set(shape.Id);

        // The selection box is inflated by the stroke width, so the corner handle sits slightly outside.
        Rect bounds = context.Selection.Bounds;
        var corner = new Vector2(bounds.Right, bounds.Bottom);
        context.Line(tool, corner, corner + new Vector2(100, 60), 20);

        Rect after = context.Document.GetElement(shape.Id).Bounds;
        Assert.True(after.Width > bounds.Width * 1.5f, $"expected the element to grow from {bounds.Width} but it is {after.Width}");
    }

    [Fact]
    public void A_cancelled_drag_restores_the_original_position()
    {
        using TestToolContext context = WithShape(out RectangleShape shape);
        var tool = new SelectTool();
        context.Selection.Set(shape.Id);
        float originalX = context.Document.GetElement(shape.Id).Bounds.X;

        tool.OnPointerDown(context, new InputSample { PointerId = 1, Phase = PointerPhase.Down, X = 150, Y = 130, Buttons = 1 });
        tool.OnPointerMove(context, new InputSample { PointerId = 1, Phase = PointerPhase.Move, X = 300, Y = 300, Buttons = 1, Timestamp = 16 });
        tool.OnPointerCancel(context, new InputSample { PointerId = 1, Phase = PointerPhase.Cancel, X = 300, Y = 300, Timestamp = 32 });

        Assert.Equal(originalX, context.Document.GetElement(shape.Id).Bounds.X, 1f);
    }
}

public class ShapeToolTests
{
    [Fact]
    public void Dragging_creates_a_rectangle()
    {
        using var context = new TestToolContext();
        context.Line(new ShapeTool(ShapeKind.Rectangle), new Vector2(50, 50), new Vector2(200, 150), 10);

        var shape = Assert.IsType<RectangleShape>(context.Document.Layers[0].Elements[0]);
        Assert.Equal(new Rect(50, 50, 150, 100), shape.Rect);
    }

    [Fact]
    public void Dragging_creates_an_arrow_with_a_head()
    {
        using var context = new TestToolContext();
        context.Line(new ShapeTool(ShapeKind.Arrow), new Vector2(10, 10), new Vector2(200, 90), 10);

        var line = Assert.IsType<LineShape>(context.Document.Layers[0].Elements[0]);
        Assert.True(line.EndArrow);
    }

    [Fact]
    public void A_click_without_a_drag_creates_nothing()
    {
        using var context = new TestToolContext();
        context.Gesture(new ShapeTool(ShapeKind.Ellipse), PointerKind.Mouse, (50, 50, 0.5f), (50, 50, 0.5f));
        Assert.Equal(0, context.Document.ElementCount);
    }

    [Fact]
    public void The_shape_uses_the_context_style()
    {
        using var context = new TestToolContext { ShapeStyle = new ShapeStyle { StrokeColor = InkColor.Parse("#ff0000"), StrokeWidth = 5f } };
        context.Line(new ShapeTool(ShapeKind.Rectangle), new Vector2(0, 0), new Vector2(100, 100), 10);

        var shape = (RectangleShape)context.Document.Layers[0].Elements[0];
        Assert.Equal(InkColor.Parse("#ff0000"), shape.Style.StrokeColor);
        Assert.Equal(5f, shape.Style.StrokeWidth);
    }
}

public class PanToolTests
{
    [Fact]
    public void Dragging_pans_the_viewport_without_changing_the_document()
    {
        using var context = new TestToolContext();
        context.Document.AddElement(TestData.Stroke(new Vector2(0, 0), new Vector2(50, 50)));
        context.History.Clear();
        long revision = context.Document.Revision;

        context.Line(new PanTool(), new Vector2(100, 100), new Vector2(160, 130), 10);

        Assert.Equal(60f, context.Viewport.Offset.X, 1f);
        Assert.Equal(30f, context.Viewport.Offset.Y, 1f);
        Assert.Equal(revision, context.Document.Revision);
        Assert.Equal(0, context.History.UndoCount);
    }

    [Fact]
    public void Two_pointers_pinch_to_zoom()
    {
        using var context = new TestToolContext();
        var tool = new PanTool();

        tool.OnPointerDown(context, new InputSample { PointerId = 1, Kind = PointerKind.Touch, Phase = PointerPhase.Down, X = 200, Y = 200, Buttons = 1 });
        tool.OnPointerDown(context, new InputSample { PointerId = 2, Kind = PointerKind.Touch, Phase = PointerPhase.Down, X = 300, Y = 200, Buttons = 1 });
        // Spread the fingers apart around the same centre.
        tool.OnPointerMove(context, new InputSample { PointerId = 1, Kind = PointerKind.Touch, Phase = PointerPhase.Move, X = 150, Y = 200, Buttons = 1, Timestamp = 16 });
        tool.OnPointerMove(context, new InputSample { PointerId = 2, Kind = PointerKind.Touch, Phase = PointerPhase.Move, X = 350, Y = 200, Buttons = 1, Timestamp = 16 });

        Assert.True(context.Viewport.Zoom > 1.5f, $"pinching apart should zoom in but zoom is {context.Viewport.Zoom}");
    }
}

public class TextToolTests
{
    [Fact]
    public void Clicking_creates_a_text_element_and_asks_for_an_editor()
    {
        using var context = new TestToolContext();
        var tool = new TextTool();
        TextEditRequest? request = null;
        tool.EditRequested += r => request = r;

        tool.OnPointerDown(context, new InputSample { PointerId = 1, Phase = PointerPhase.Down, X = 40, Y = 60, Buttons = 1 });

        Assert.NotNull(request);
        Assert.Equal(1, context.Document.ElementCount);
        Assert.NotNull(tool.Editing);
    }

    [Fact]
    public void Committing_empty_text_removes_the_element()
    {
        using var context = new TestToolContext();
        var tool = new TextTool();
        tool.OnPointerDown(context, new InputSample { PointerId = 1, Phase = PointerPhase.Down, X = 40, Y = 60, Buttons = 1 });

        tool.CommitEdit(context, "   ");
        Assert.Equal(0, context.Document.ElementCount);
    }

    [Fact]
    public void Committing_text_updates_the_element()
    {
        using var context = new TestToolContext();
        var tool = new TextTool();
        tool.OnPointerDown(context, new InputSample { PointerId = 1, Phase = PointerPhase.Down, X = 40, Y = 60, Buttons = 1 });

        tool.CommitEdit(context, "Hello", new Vector2(60, 20));
        var text = (TextElement)context.Document.Layers[0].Elements[0];
        Assert.Equal("Hello", text.Text);
        Assert.Equal(new Vector2(60, 20), text.Size);
    }

    [Fact]
    public void Clicking_existing_text_edits_it_rather_than_creating_another()
    {
        using var context = new TestToolContext();
        context.Document.AddElement(new TextElement(new Vector2(40, 60), "Existing") { Size = new Vector2(120, 20) });
        var tool = new TextTool();

        tool.OnPointerDown(context, new InputSample { PointerId = 1, Phase = PointerPhase.Down, X = 60, Y = 66, Buttons = 1 });

        Assert.Equal(1, context.Document.ElementCount);
        Assert.Equal("Existing", tool.Editing!.Text);
    }
}
