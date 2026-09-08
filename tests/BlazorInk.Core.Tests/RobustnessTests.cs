using System.Numerics;
using BlazorInk.Brushes;
using BlazorInk.Export;
using BlazorInk.Geometry;
using BlazorInk.History;
using BlazorInk.Input;
using BlazorInk.Operations;
using BlazorInk.Raster;
using BlazorInk.Serialization;
using BlazorInk.Tools;
using Xunit;

namespace BlazorInk.Tests.Core;

/// <summary>
/// Regression tests for defects found in review. Each one describes the failure it prevents, because a test
/// whose purpose is forgotten is a test that gets deleted.
/// </summary>
public class GestureRobustnessTests
{
    [Fact]
    public void Switching_tools_mid_drag_does_not_disable_undo()
    {
        // A tool that opens a history group and is swapped out before closing it leaves the group open forever,
        // which silently swallows every later change and makes undo do nothing.
        using var context = new TestToolContext();
        var shape = new RectangleShape(new Rect(50, 50, 80, 40)) { Style = new ShapeStyle { FillColor = InkColor.Black } };
        context.Document.AddElement(shape);
        context.History.Clear();

        var select = new SelectTool();
        context.Selection.Set(shape.Id);
        select.OnPointerDown(context, Down(70, 70));
        select.OnPointerMove(context, Move(140, 120));

        select.Deactivate(context);

        var draw = new DrawTool();
        draw.Activate(context);
        context.Line(draw, new Vector2(10, 10), new Vector2(60, 60), 12);

        Assert.True(context.History.CanUndo);
        Assert.True(context.History.Undo());
    }

    [Fact]
    public void Switching_tools_mid_stroke_discards_the_stroke()
    {
        using var context = new TestToolContext();
        var draw = new DrawTool();
        draw.OnPointerDown(context, Down(10, 10));
        draw.OnPointerMove(context, Move(60, 40));
        Assert.True(draw.IsDrawing);

        draw.Deactivate(context);

        Assert.False(draw.IsDrawing);
        Assert.Equal(0, context.Document.ElementCount);
    }

    [Fact]
    public void An_eraser_gesture_interrupted_by_deactivation_leaves_history_usable()
    {
        using var context = new TestToolContext();
        for (int i = 0; i < 3; i++)
        {
            context.Document.AddElement(new Stroke(TestData.Line(new Vector2(0, 20 + i * 30), new Vector2(300, 20 + i * 30), 40), new PenBrush()));
        }
        context.History.Clear();

        var eraser = new EraserTool { Mode = EraserMode.Stroke, Radius = 15f };
        eraser.OnPointerDown(context, Down(100, 20));
        eraser.OnPointerMove(context, Move(150, 50));
        eraser.Deactivate(context);

        context.Document.AddElement(TestData.Stroke(new Vector2(0, 200), new Vector2(50, 220)));
        Assert.True(context.History.CanUndo);
    }

    [Fact]
    public void Changing_the_eraser_mode_mid_gesture_does_not_strand_it()
    {
        using var context = new TestToolContext();
        context.Document.AddElement(new Stroke(TestData.Line(new Vector2(0, 50), new Vector2(300, 50), 40), new PenBrush()));
        context.History.Clear();

        var eraser = new EraserTool { Mode = EraserMode.Stroke, Radius = 15f };
        eraser.OnPointerDown(context, Down(100, 50));
        eraser.Mode = EraserMode.Pixel;      // the application changes the setting while the pointer is down
        eraser.OnPointerUp(context, Up(120, 50));

        context.Document.AddElement(TestData.Stroke(new Vector2(0, 200), new Vector2(50, 220)));
        Assert.True(context.History.CanUndo);
    }

    [Fact]
    public void A_stroke_stays_on_the_layer_it_started_on()
    {
        using var context = new TestToolContext();
        Layer second = context.Document.AddLayer("Second");
        context.ActiveLayerId = second.Id;

        var draw = new DrawTool();
        draw.OnPointerDown(context, Down(10, 10));
        draw.OnPointerMove(context, Move(60, 40));

        // The application switches the active layer while the pen is still down.
        context.ActiveLayerId = context.Document.Layers[0].Id;
        draw.OnPointerUp(context, Up(100, 70));

        Assert.Single(second.Elements);
        Assert.Empty(context.Document.Layers[0].Elements);
    }

    [Fact]
    public void Shift_adds_to_the_selection_so_multi_select_works_without_a_mouse()
    {
        using var context = new TestToolContext();
        var first = new RectangleShape(new Rect(20, 20, 40, 40)) { Style = new ShapeStyle { FillColor = InkColor.Black } };
        var second = new RectangleShape(new Rect(120, 20, 40, 40)) { Style = new ShapeStyle { FillColor = InkColor.Black } };
        context.Document.AddElement(first);
        context.Document.AddElement(second);

        var tool = new SelectTool();
        context.Gesture(tool, PointerKind.Pen, (40, 40, 0.5f), (40, 40, 0.5f));
        Assert.Equal(1, context.Selection.Count);

        tool.OnPointerDown(context, Down(140, 40) with { Shift = true, Kind = PointerKind.Pen });
        tool.OnPointerUp(context, Up(140, 40) with { Shift = true, Kind = PointerKind.Pen });

        Assert.Equal(2, context.Selection.Count);
    }

    [Fact]
    public void Cancelling_a_drag_leaves_no_history_step()
    {
        using var context = new TestToolContext();
        var shape = new RectangleShape(new Rect(50, 50, 60, 40)) { Style = new ShapeStyle { FillColor = InkColor.Black } };
        context.Document.AddElement(shape);
        context.History.Clear();
        context.Selection.Set(shape.Id);

        var tool = new SelectTool();
        tool.OnPointerDown(context, Down(70, 70));
        tool.OnPointerMove(context, Move(180, 160));
        tool.OnPointerCancel(context, new InputSample { PointerId = 1, Phase = PointerPhase.Cancel, X = 180, Y = 160, Timestamp = 32 });

        Assert.Equal(50f, context.Document.GetElement(shape.Id).Bounds.X, 2f);
        Assert.False(context.History.CanUndo);
    }

    [Fact]
    public void A_refused_change_does_not_escape_the_tool()
    {
        // Document limits throw. An exception here would unwind through the browser callback and strand the
        // gesture, so tools report the refusal instead.
        var document = new InkDocument(DocumentSettings.Default, DocumentLimits.Default with { MaxElements = 1 });
        using var context = new TestToolContext(document);
        context.Document.AddElement(TestData.Stroke(new Vector2(0, 0), new Vector2(10, 10)));

        var draw = new DrawTool();
        context.Line(draw, new Vector2(20, 20), new Vector2(80, 80), 12);

        Assert.Equal(1, context.Document.ElementCount);
        Assert.Contains(context.Announcements, m => m.Contains("could not be applied", StringComparison.Ordinal));
    }

    [Fact]
    public void Text_placement_and_typing_are_one_undo_step()
    {
        using var context = new TestToolContext();
        var tool = new TextTool();

        tool.OnPointerDown(context, Down(40, 60));
        tool.CommitEdit(context, "Reviewed", new Vector2(80, 20));

        Assert.Equal(1, context.Document.ElementCount);
        Assert.Equal(1, context.History.UndoCount);

        context.History.Undo();
        Assert.Equal(0, context.Document.ElementCount);
    }

    [Fact]
    public void The_text_tool_refuses_a_locked_layer()
    {
        using var context = new TestToolContext();
        context.Document.UpdateLayer(context.ActiveLayerId, p => p with { IsLocked = true });

        var tool = new TextTool();
        Assert.False(tool.OnPointerDown(context, Down(40, 60)));
        Assert.Equal(0, context.Document.ElementCount);
    }

    [Fact]
    public void Panning_does_not_modify_the_document()
    {
        Assert.False(new PanTool().ModifiesDocument);
        Assert.True(new DrawTool().ModifiesDocument);
        Assert.True(new SelectTool().ModifiesDocument);
    }

    private static InputSample Down(float x, float y) => new() { PointerId = 1, Phase = PointerPhase.Down, X = x, Y = y, Pressure = 0.5f, Buttons = 1, IsPrimary = true };

    private static InputSample Move(float x, float y) => new() { PointerId = 1, Phase = PointerPhase.Move, X = x, Y = y, Pressure = 0.5f, Buttons = 1, Timestamp = 16, IsPrimary = true };

    private static InputSample Up(float x, float y) => new() { PointerId = 1, Phase = PointerPhase.Up, X = x, Y = y, Timestamp = 32, IsPrimary = true };
}

public class HistoryRobustnessTests
{
    [Fact]
    public void An_undo_that_cannot_be_applied_keeps_the_step()
    {
        // A collaborator can delete the element a local undo step refers to. Losing the step silently would be
        // worse than refusing: the user's earlier work would become unreachable.
        var document = new InkDocument();
        using var history = new HistoryManager(document);
        Stroke first = TestData.Stroke(new Vector2(0, 0), new Vector2(10, 10));
        Stroke second = TestData.Stroke(new Vector2(20, 20), new Vector2(30, 30));
        document.AddElement(first);
        document.AddElement(second);

        // Remove the newest element as a remote change, so the local undo step for it can no longer apply.
        document.Apply(new RemoveElementOperation(second.Id), ChangeOrigin.Remote);

        Assert.Equal(2, history.UndoCount);
        Assert.False(history.Undo());
        Assert.Equal(2, history.UndoCount);
    }

    [Fact]
    public void Disposing_closes_a_group_left_open()
    {
        var document = new InkDocument();
        var history = new HistoryManager(document);
        IDisposable group = history.BeginGroup("Interrupted");
        document.AddElement(TestData.Stroke(new Vector2(0, 0), new Vector2(10, 10)));

        history.Dispose();
        GC.KeepAlive(group);

        // The group was closed, so the change it captured became a real step rather than vanishing.
        Assert.Equal(1, history.UndoCount);
    }

    [Fact]
    public void EndOpenGroups_restores_a_usable_history()
    {
        var document = new InkDocument();
        using var history = new HistoryManager(document);
        history.BeginGroup("Abandoned");
        document.AddElement(TestData.Stroke(new Vector2(0, 0), new Vector2(10, 10)));
        Assert.False(history.CanUndo);

        history.EndOpenGroups();

        Assert.True(history.CanUndo);
        document.AddElement(TestData.Stroke(new Vector2(20, 20), new Vector2(30, 30)));
        Assert.Equal(2, history.UndoCount);
    }
}

public class DocumentRobustnessTests
{
    [Fact]
    public void The_selection_can_be_iterated_while_deleting()
    {
        var document = new InkDocument();
        using var selection = new Selection(document);
        for (int i = 0; i < 5; i++) document.AddElement(TestData.Stroke(new Vector2(i * 20, 0), new Vector2(i * 20 + 10, 10)));
        selection.SelectAll();

        // The obvious consumer idiom. Handing out the live list would throw here.
        foreach (Guid id in selection.Ids) document.RemoveElement(id);

        Assert.True(document.IsEmpty);
    }

    [Fact]
    public void RemoveElements_tolerates_a_repeated_id()
    {
        var document = new InkDocument();
        Stroke stroke = TestData.Stroke(new Vector2(0, 0), new Vector2(10, 10));
        document.AddElement(stroke);

        document.RemoveElements([stroke.Id, stroke.Id]);
        Assert.True(document.IsEmpty);
    }

    [Fact]
    public void Replacing_a_stroke_with_one_of_the_same_size_does_not_trip_the_point_limit()
    {
        var document = new InkDocument(DocumentSettings.Default, DocumentLimits.Default with { MaxTotalPoints = 30 });
        var stroke = new Stroke(TestData.Line(new Vector2(0, 0), new Vector2(100, 100), 30), new PenBrush());
        document.AddElement(stroke);

        // Recolouring is a replace of identical size and must be accepted, not refused for exceeding the total.
        document.ReplaceElement(stroke with { Brush = stroke.Brush with { Color = InkColor.Parse("#f00") } });
        Assert.Equal(30, document.TotalPointCount);
    }

    [Fact]
    public void A_failed_replace_leaves_the_point_count_intact()
    {
        var document = new InkDocument(DocumentSettings.Default, DocumentLimits.Default with { MaxPointsPerStroke = 40 });
        var stroke = new Stroke(TestData.Line(new Vector2(0, 0), new Vector2(50, 50), 20), new PenBrush());
        document.AddElement(stroke);
        long before = document.TotalPointCount;

        Assert.Throws<InvalidOperationException>(() =>
            document.ReplaceElement(stroke with { Points = TestData.Line(new Vector2(0, 0), new Vector2(50, 50), 100) }));

        Assert.Equal(before, document.TotalPointCount);
    }

    [Fact]
    public void A_zero_height_rectangle_still_intersects()
    {
        // A flat rectangle is a line, not "no rectangle". Treating it as empty would cull such elements from
        // rendering and hit testing.
        var line = new Rect(10, 50, 100, 0);
        Assert.True(line.Intersects(new Rect(0, 40, 200, 20)));
        Assert.False(Rect.Empty.Intersects(new Rect(0, 0, 10, 10)));
    }
}

public class UntrustedInputTests
{
    [Fact]
    public void A_document_full_of_eraser_strokes_exports_without_exhausting_the_stack()
    {
        // Eraser strokes nest masks in SVG. A recursive implementation nested once per eraser, which a document
        // controls, and a stack overflow cannot be caught.
        var document = new InkDocument(new DocumentSettings { Width = 200, Height = 200 });
        Guid layer = document.Layers[0].Id;
        document.AddElement(new Stroke(TestData.Line(new Vector2(0, 100), new Vector2(200, 100), 10), new PenBrush()), layer);
        using (document.BeginBatch("Erasers"))
        {
            for (int i = 0; i < 5000; i++)
            {
                document.AddElement(new Stroke(TestData.Line(new Vector2(i % 200, 0), new Vector2(i % 200, 200), 4), new PixelEraserBrush()), layer);
            }
        }

        string svg = SvgExporter.Export(document);
        Assert.StartsWith("<svg", svg, StringComparison.Ordinal);
        Assert.EndsWith("</svg>", svg, StringComparison.Ordinal);
    }

    [Fact]
    public void A_hostile_id_prefix_cannot_break_out_of_an_attribute()
    {
        var document = new InkDocument(new DocumentSettings { Width = 100, Height = 100 });
        document.AddElement(new Stroke(TestData.Line(new Vector2(0, 50), new Vector2(100, 50), 10), new PenBrush()));
        document.AddElement(new Stroke(TestData.Line(new Vector2(50, 0), new Vector2(50, 100), 10), new PixelEraserBrush()));

        string svg = SvgExporter.Export(document, new SvgExportOptions { IdPrefix = "a\" onload=\"alert(1)" });
        Assert.DoesNotContain("onload", svg, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_document_beyond_its_limits_is_reported_as_malformed()
    {
        // TryDeserialize promises not to throw. Limit violations used to surface as InvalidOperationException.
        var document = new InkDocument();
        for (int i = 0; i < 20; i++) document.AddElement(TestData.Stroke(new Vector2(i * 10, 0), new Vector2(i * 10 + 5, 5)));
        string json = InkSerializer.Serialize(document);

        var options = new InkDeserializerOptions { Limits = DocumentLimits.Default with { MaxElements = 5 } };
        Assert.Throws<InkFormatException>(() => InkSerializer.Deserialize(json, options));
        Assert.False(InkSerializer.TryDeserialize(json, out _, out string? error, options));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void A_highlight_with_too_many_rectangles_is_rejected()
    {
        var rects = string.Join(',', Enumerable.Range(0, 200_000).Select(i => $"[{i},0,4,10]"));
        string json = $$"""
        {"format":"blazorink","version":1,"layers":[{"id":"11111111-1111-4111-8111-111111111111","name":"L","elements":[
          {"type":"highlight","id":"22222222-2222-4222-8222-222222222222","rects":[{{rects}}]}
        ]}]}
        """;
        Assert.Throws<InkFormatException>(() => InkSerializer.Deserialize(json));
    }

    [Fact]
    public void A_highlight_with_non_finite_coordinates_is_sanitized()
    {
        string json = """
        {"format":"blazorink","version":1,"layers":[{"id":"11111111-1111-4111-8111-111111111111","name":"L","elements":[
          {"type":"highlight","id":"22222222-2222-4222-8222-222222222222","rects":[[1e39,1e39,1e39,1e39]]}
        ]}]}
        """;
        InkDocument document = InkSerializer.Deserialize(json);
        foreach (InkElement element in document.AllElements)
        {
            Rect bounds = element.Bounds;
            Assert.True(float.IsFinite(bounds.X) && float.IsFinite(bounds.Width));
        }
    }

    [Fact]
    public void Point_data_that_claims_more_points_than_it_contains_is_rejected()
    {
        // Version 1, no flags, quantization 64, a large count and almost no payload.
        var bytes = new List<byte> { 1, 0, 64 };
        uint count = 100_000;
        while (count >= 0x80) { bytes.Add((byte)(count | 0x80)); count >>= 7; }
        bytes.Add((byte)count);
        bytes.AddRange(Enumerable.Repeat((byte)1, 10));

        Assert.Throws<FormatException>(() => PointCodec.Decode(bytes.ToArray()));
    }

    [Theory]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x7F, 0xFF, 0xFF, 0xF8, 0x49, 0x48, 0x44, 0x52 })]
    public void A_png_with_a_hostile_chunk_length_is_rejected(byte[] data)
    {
        // The length is close to int.MaxValue; the bounds check must not overflow into a passing comparison.
        Assert.Throws<FormatException>(() => PngCodec.Decode(data));
    }

    [Fact]
    public void A_png_with_corrupt_compressed_data_is_reported_as_a_format_error()
    {
        byte[] valid = TestData.CreatePng(8, 8, (_, _) => InkColor.Black);
        byte[] corrupt = valid.ToArray();
        // Damage the middle of the file, which is inside the compressed image data.
        for (int i = valid.Length / 2; i < valid.Length / 2 + 8 && i < corrupt.Length; i++) corrupt[i] ^= 0xFF;

        Assert.Throws<FormatException>(() => PngCodec.Decode(corrupt));
    }

    [Fact]
    public void An_undecodable_embedded_image_is_reported_rather_than_failing_the_export()
    {
        var document = new InkDocument(new DocumentSettings { Width = 50, Height = 50 });
        byte[] valid = TestData.CreatePng(8, 8, (_, _) => InkColor.Black);
        byte[] corrupt = valid.ToArray();
        for (int i = valid.Length / 2; i < valid.Length / 2 + 8 && i < corrupt.Length; i++) corrupt[i] ^= 0xFF;
        document.AddElement(new ImageElement(Rendering.ImageSource.FromBytes(corrupt, "image/png", 8, 8), new Rect(0, 0, 50, 50)));

        PngExportResult result = PngExporter.ExportWithDetails(document);
        Assert.Equal(1, result.SkippedImages);
        Assert.True(result.Bytes.Length > 0);
    }
}
