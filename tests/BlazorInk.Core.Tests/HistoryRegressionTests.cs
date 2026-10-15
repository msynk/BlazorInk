using System.Numerics;
using BlazorInk.Brushes;
using BlazorInk.Export;
using BlazorInk.Geometry;
using BlazorInk.History;
using BlazorInk.Input;
using BlazorInk.Tools;
using Xunit;
using Xunit.Abstractions;

namespace BlazorInk.Tests.Core;

/// <summary>
/// Undo and redo regressions, including gestures a host interrupts part way through.
/// </summary>
public class HistoryRegressionTests(ITestOutputHelper output)
{
    [Fact]
    public void A_scope_disposed_after_the_group_was_force_closed_is_a_no_op()
    {
        // A tool opens a group at pointer down and disposes it at pointer up. If the host closes the group in
        // between - LoadDocument, a document swap, disposing the manager - the tool's own dispose still runs, and
        // closing an already closed group must not throw.
        var document = new InkDocument();
        using var history = new HistoryManager(document);
        IDisposable group = history.BeginGroup("Interrupted");
        document.AddElement(TestData.Stroke(new Vector2(0, 0), new Vector2(10, 10)));
        history.EndOpenGroups();
        group.Dispose();
        document.AddElement(TestData.Stroke(new Vector2(1, 1), new Vector2(9, 9)));
        Assert.Equal(2, history.UndoCount);
    }

    [Fact]
    public void A_scope_disposed_after_the_manager_was_disposed_is_a_no_op()
    {
        var document = new InkDocument();
        var history = new HistoryManager(document);
        IDisposable group = history.BeginGroup("Interrupted");
        document.AddElement(TestData.Stroke(new Vector2(0, 0), new Vector2(10, 10)));
        history.Dispose();
        group.Dispose();
    }

    [Fact]
    public void A_long_random_edit_sequence_stays_consistent()
    {
        var random = new Random(20260909);
        var document = new InkDocument();
        using var history = new HistoryManager(document, 20000);
        using var selection = new Selection(document);
        var log = new List<string>();

        for (int step = 0; step < 4000; step++)
        {
            int action = random.Next(6);
            switch (action)
            {
                case 0:
                case 1:
                    document.AddElement(TestData.Stroke(new Vector2(random.Next(500), random.Next(500)), new Vector2(random.Next(500), random.Next(500)), count: 4));
                    log.Add("add");
                    break;
                case 2:
                    if (!document.IsEmpty)
                    {
                        InkElement victim = document.AllElements.ElementAt(random.Next(document.ElementCount));
                        document.RemoveElement(victim.Id);
                        log.Add("remove");
                    }
                    break;
                case 3:
                    if (!document.IsEmpty)
                    {
                        InkElement e = document.AllElements.ElementAt(random.Next(document.ElementCount));
                        document.ReplaceElement(e.Translate(new Vector2(1, 1)));
                        log.Add("move");
                    }
                    break;
                case 4:
                    log.Add(history.Undo() ? "undo" : "undo-noop");
                    break;
                case 5:
                    log.Add(history.Redo() ? "redo" : "redo-noop");
                    break;
            }

            // The element index and the layer lists must never disagree, whatever the sequence was.
            Assert.Equal(document.ElementCount, document.AllElements.Count());
            foreach (InkElement e in document.AllElements)
            {
                Assert.True(document.ContainsElement(e.Id));
                Assert.NotNull(document.GetLayerOf(e.Id));
            }
        }

        int guard = 0;
        while (history.CanUndo && guard++ < 100_000) history.Undo();
        output.WriteLine($"after full undo: {document.ElementCount} elements, {document.TotalPointCount} points, log tail {string.Join(",", log.TakeLast(10))}");
        Assert.True(document.IsEmpty, $"{document.ElementCount} elements survived a full undo");
        Assert.Equal(0, document.TotalPointCount);
    }

    [Theory]
    [InlineData("draw")]
    [InlineData("erase-stroke")]
    [InlineData("erase-segment")]
    [InlineData("select")]
    [InlineData("shape")]
    [InlineData("text")]
    public void A_gesture_interrupted_by_a_document_swap_leaves_history_usable(string toolId)
    {
        using var context = new TestToolContext();
        context.Document.AddElement(TestData.Stroke(new Vector2(50, 50), new Vector2(250, 250), new PenBrush { Width = 6 }));
        ITool tool = toolId switch
        {
            "erase-stroke" => new EraserTool { Mode = EraserMode.Stroke, Radius = 20 },
            "erase-segment" => new EraserTool { Mode = EraserMode.Segment, Radius = 20 },
            "select" => new SelectTool(),
            "shape" => new ShapeTool(ShapeKind.Rectangle),
            "text" => new TextTool(),
            _ => new DrawTool(),
        };
        tool.Activate(context);

        InputSample S(PointerPhase phase, float x, float y, double t) => new()
        { PointerId = 3, Kind = PointerKind.Mouse, Phase = phase, X = x, Y = y, Pressure = 0.5f, Timestamp = t, Buttons = phase == PointerPhase.Up ? 0 : 1 };

        tool.OnPointerDown(context, S(PointerPhase.Down, 60, 60, 0));
        for (int i = 1; i <= 6; i++) tool.OnPointerMove(context, S(PointerPhase.Move, 60 + i * 20, 60 + i * 20, i * 10));

        // Halfway through the gesture the host tears the session down, exactly as LoadDocument does.
        context.History.EndOpenGroups();
        context.History.Dispose();

        // The tool still finishes its gesture, and nothing it does may throw.
        tool.OnPointerUp(context, S(PointerPhase.Up, 220, 220, 100));
        tool.Deactivate(context);
        output.WriteLine($"{toolId}: {context.Document.ElementCount} elements after the interrupted gesture");
    }

    [Fact]
    public void A_gesture_interrupted_by_a_tool_change_leaves_history_usable()
    {
        using var context = new TestToolContext();
        var eraser = new EraserTool { Mode = EraserMode.Segment, Radius = 15 };
        context.Document.AddElement(TestData.Stroke(new Vector2(50, 100), new Vector2(450, 100), new PenBrush { Width = 5 }, 60));
        eraser.Activate(context);

        InputSample S(PointerPhase phase, float x, float y) => new()
        { PointerId = 1, Kind = PointerKind.Mouse, Phase = phase, X = x, Y = y, Pressure = 0.5f, Buttons = phase == PointerPhase.Up ? 0 : 1 };

        eraser.OnPointerDown(context, S(PointerPhase.Down, 200, 100));
        eraser.OnPointerMove(context, S(PointerPhase.Move, 220, 100));
        eraser.Deactivate(context);

        var pen = new DrawTool();
        pen.Activate(context);
        context.Line(pen, new Vector2(10, 300), new Vector2(200, 320), 20);

        output.WriteLine($"undo steps: {context.History.UndoCount}");
        Assert.True(context.History.CanUndo, "history was left disabled by the interrupted erase");
        Assert.True(context.History.Undo());
        Assert.True(context.History.Undo());
    }

    [Fact]
    public void Exporting_a_document_that_is_then_cleared_produces_valid_output()
    {
        var document = new InkDocument();
        for (int i = 0; i < 300; i++) document.AddElement(TestData.Stroke(new Vector2(i, 0), new Vector2(i, 50), count: 4));

        string svg = SvgExporter.Export(document);
        var xml = new System.Xml.XmlDocument();
        xml.LoadXml(svg);
        Assert.NotNull(xml.DocumentElement);

        int before = document.ElementCount;
        document.Clear();
        Assert.Equal(0, document.ElementCount);
        Assert.Equal(300, before);
    }

    [Fact]
    public void Redo_into_a_layer_a_collaborator_removed_is_refused_not_thrown()
    {
        // The missing layer used to surface as KeyNotFoundException, which the history manager does not catch:
        // the redo crashed instead of being refused like any other step the document moved past.
        var document = new InkDocument();
        using var history = new HistoryManager(document);
        Layer notes = document.AddLayer("Notes");
        document.AddElement(TestData.Stroke(new Vector2(0, 0), new Vector2(10, 10)), notes.Id);
        Assert.True(history.Undo());

        document.Apply(new Operations.RemoveLayerOperation(notes.Id), ChangeOrigin.Remote);

        Assert.False(history.Redo());
        Assert.Equal(1, history.RedoCount);
    }

    [Fact]
    public void A_tool_working_on_a_layer_that_no_longer_exists_does_not_strand_history()
    {
        using var context = new TestToolContext();
        context.ActiveLayerId = Guid.NewGuid();

        // The pen reports the refusal instead of throwing out of the pointer callback.
        context.Line(new DrawTool(), new Vector2(10, 10), new Vector2(100, 60));
        Assert.Equal(0, context.Document.ElementCount);

        // The text tool opens its undo group before adding the box; a throw there used to leave it open for good.
        Assert.False(new TextTool().CreateAt(context, new Vector2(20, 20)));

        context.ActiveLayerId = context.Document.Layers[0].Id;
        context.Line(new DrawTool(), new Vector2(10, 10), new Vector2(100, 60));
        Assert.True(context.History.Undo(), "history was left disabled by the refused text box");
    }

    [Fact]
    public void A_text_box_left_blank_leaves_no_undo_step()
    {
        using var context = new TestToolContext();
        var tool = new TextTool();
        Assert.True(tool.CreateAt(context, new Vector2(20, 20)));
        tool.CommitEdit(context, "   ");

        Assert.Equal(0, context.Document.ElementCount);
        Assert.False(context.History.CanUndo, "an undo step that adds and removes the same empty box does nothing");
    }

    [Fact]
    public void A_failed_batch_puts_elements_back_at_their_original_version()
    {
        // The rollback is never reported to collaborators, so a version bump here would leave this replica ahead
        // of everyone else and make it discard their next edit of the element.
        var document = new InkDocument();
        Stroke stroke = TestData.Stroke(new Vector2(0, 0), new Vector2(10, 10));
        document.AddElement(stroke);
        InkElement before = document.GetElement(stroke.Id);

        var batch = new Operations.BatchOperation(
        [
            new Operations.ReplaceElementOperation(before with { Description = "edited" }),
            new Operations.RemoveElementOperation(Guid.NewGuid()),
        ]);
        Assert.Throws<InvalidOperationException>(() => document.Apply(batch));

        InkElement after = document.GetElement(stroke.Id);
        Assert.Null(after.Description);
        Assert.Equal(before.Version, after.Version);
    }

    [Fact]
    public void Redoing_a_drag_reports_the_versions_it_stores()
    {
        // Redo replays every frame of the drag in one batch. Each frame was reported with the version the document
        // had before the batch, while the document stored a higher one per frame, so what collaborators received
        // did not match what the sender kept.
        var document = new InkDocument();
        using var history = new HistoryManager(document);
        Stroke stroke = TestData.Stroke(new Vector2(0, 0), new Vector2(10, 10));
        document.AddElement(stroke);
        using (history.BeginGroup("Move"))
        {
            for (int i = 1; i <= 3; i++) document.ReplaceElement(document.GetElement(stroke.Id) with { Transform = Matrix3x2.CreateTranslation(i * 10, 0) });
        }
        Assert.True(history.Undo());

        Operations.DrawingOperation? reported = null;
        document.Changed += (_, e) => reported = e.Operation;
        Assert.True(history.Redo());

        var versions = new List<int>();
        void Collect(Operations.DrawingOperation operation)
        {
            if (operation is Operations.ReplaceElementOperation replace) versions.Add(replace.Element.Version);
            else if (operation is Operations.BatchOperation batch) foreach (Operations.DrawingOperation inner in batch.Operations) Collect(inner);
        }
        Collect(reported!);

        Assert.Equal(3, versions.Count);
        Assert.Equal(versions.Distinct().Count(), versions.Count);
        Assert.Equal(document.GetElement(stroke.Id).Version, versions[^1]);
    }

    [Fact]
    public void An_element_at_the_highest_version_does_not_wrap_to_a_negative_one()
    {
        var document = new InkDocument();
        Stroke stroke = TestData.Stroke(new Vector2(0, 0), new Vector2(10, 10)) with { Version = int.MaxValue };
        document.AddElement(stroke);

        document.ReplaceElement(document.GetElement(stroke.Id) with { Description = "edited" });

        Assert.Equal(int.MaxValue, document.GetElement(stroke.Id).Version);
    }
}
