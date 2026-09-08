using System.Numerics;
using BlazorInk.Brushes;
using BlazorInk.Geometry;
using BlazorInk.History;
using BlazorInk.Operations;
using Xunit;

namespace BlazorInk.Tests.Core;

public class DocumentTests
{
    [Fact]
    public void New_document_has_one_layer_and_no_elements()
    {
        var document = new InkDocument();
        Assert.Single(document.Layers);
        Assert.True(document.IsEmpty);
        Assert.Equal(0, document.ElementCount);
    }

    [Fact]
    public void AddElement_places_the_element_on_the_active_layer()
    {
        var document = new InkDocument();
        Stroke stroke = TestData.Stroke(new Vector2(0, 0), new Vector2(10, 10));
        document.AddElement(stroke);
        Assert.Equal(1, document.ElementCount);
        Assert.Same(stroke, document.Layers[0].Elements[0]);
        Assert.Equal(document.Layers[0].Id, document.GetLayerOf(stroke.Id)!.Id);
    }

    [Fact]
    public void AddElement_rejects_a_duplicate_id()
    {
        var document = new InkDocument();
        Stroke stroke = TestData.Stroke(new Vector2(0, 0), new Vector2(10, 10));
        document.AddElement(stroke);
        Assert.Throws<InvalidOperationException>(() => document.AddElement(stroke));
    }

    [Fact]
    public void ReplaceElement_keeps_position_and_bumps_the_version()
    {
        var document = new InkDocument();
        Stroke a = TestData.Stroke(new Vector2(0, 0), new Vector2(10, 10));
        Stroke b = TestData.Stroke(new Vector2(50, 50), new Vector2(60, 60));
        document.AddElement(a);
        document.AddElement(b);

        document.ReplaceElement(a with { Description = "edited" });

        InkElement updated = document.Layers[0].Elements[0];
        Assert.Equal(a.Id, updated.Id);
        Assert.Equal("edited", updated.Description);
        Assert.True(updated.Version > a.Version);
        Assert.Equal(b.Id, document.Layers[0].Elements[1].Id);
    }

    [Fact]
    public void Elements_track_their_layer_after_reordering()
    {
        var document = new InkDocument();
        Layer second = document.AddLayer("Second");
        Stroke stroke = TestData.Stroke(new Vector2(0, 0), new Vector2(10, 10));
        document.AddElement(stroke, document.Layers[0].Id);

        document.Apply(new ReorderElementOperation(stroke.Id, second.Id, null));

        Assert.Equal(second.Id, document.GetLayerOf(stroke.Id)!.Id);
        Assert.Empty(document.Layers[0].Elements);
        Assert.Single(second.Elements);
    }

    [Fact]
    public void RemoveLayer_refuses_to_remove_the_last_layer()
    {
        var document = new InkDocument();
        Assert.Throws<InvalidOperationException>(() => document.RemoveLayer(document.Layers[0].Id));
    }

    [Fact]
    public void ContentBounds_covers_the_visible_elements()
    {
        var document = new InkDocument();
        document.AddElement(new RectangleShape(new Rect(10, 10, 20, 20)) { Style = new ShapeStyle { StrokeWidth = 0 } });
        document.AddElement(new RectangleShape(new Rect(100, 50, 20, 20)) { Style = new ShapeStyle { StrokeWidth = 0 } });
        Rect bounds = document.ContentBounds;
        Assert.Equal(9f, bounds.Left, 0);
        Assert.Equal(121f, bounds.Right, 0);
    }

    [Fact]
    public void ContentBounds_ignores_hidden_layers()
    {
        var document = new InkDocument();
        document.AddElement(new RectangleShape(new Rect(10, 10, 20, 20)));
        Layer hidden = document.AddLayer("Hidden");
        document.AddElement(new RectangleShape(new Rect(500, 500, 20, 20)), hidden.Id);
        document.UpdateLayer(hidden.Id, p => p with { IsVisible = false });
        Assert.True(document.ContentBounds.Right < 100);
    }

    [Fact]
    public void Clear_removes_everything_in_one_undoable_step()
    {
        var document = new InkDocument();
        using var history = new HistoryManager(document);
        for (int i = 0; i < 5; i++) document.AddElement(TestData.Stroke(new Vector2(i, 0), new Vector2(i + 5, 10)));

        document.Clear();
        Assert.True(document.IsEmpty);

        history.Undo();
        Assert.Equal(5, document.ElementCount);
    }

    [Fact]
    public void Batch_raises_one_change_event()
    {
        var document = new InkDocument();
        int events = 0;
        document.Changed += (_, _) => events++;

        using (document.BeginBatch("Add many"))
        {
            for (int i = 0; i < 10; i++) document.AddElement(TestData.Stroke(new Vector2(i, 0), new Vector2(i + 1, 1)));
        }

        Assert.Equal(1, events);
        Assert.Equal(10, document.ElementCount);
    }

    [Fact]
    public void A_failing_operation_inside_a_batch_leaves_the_document_unchanged()
    {
        var document = new InkDocument();
        Stroke existing = TestData.Stroke(new Vector2(0, 0), new Vector2(1, 1));
        document.AddElement(existing);

        var batch = new BatchOperation(
        [
            new AddElementOperation(document.Layers[0].Id, TestData.Stroke(new Vector2(2, 2), new Vector2(3, 3))),
            new AddElementOperation(document.Layers[0].Id, existing), // duplicate id: must fail
        ]);

        Assert.Throws<InvalidOperationException>(() => document.Apply(batch));
        Assert.Equal(1, document.ElementCount);
    }

    [Fact]
    public void TotalPointCount_tracks_additions_and_removals()
    {
        var document = new InkDocument();
        Stroke stroke = TestData.Stroke(new Vector2(0, 0), new Vector2(10, 10), count: 25);
        document.AddElement(stroke);
        Assert.Equal(25, document.TotalPointCount);
        document.RemoveElement(stroke.Id);
        Assert.Equal(0, document.TotalPointCount);
    }

    [Fact]
    public void Limits_reject_a_stroke_with_too_many_points()
    {
        var document = new InkDocument(DocumentSettings.Default, DocumentLimits.Default with { MaxPointsPerStroke = 10 });
        Assert.Throws<InvalidOperationException>(() => document.AddElement(TestData.Stroke(new Vector2(0, 0), new Vector2(1, 1), count: 50)));
    }

    [Fact]
    public void Limits_reject_coordinates_far_outside_the_allowed_range()
    {
        var document = new InkDocument(DocumentSettings.Default, DocumentLimits.Default with { MaxCoordinate = 1000 });
        Assert.Throws<InvalidOperationException>(() => document.AddElement(TestData.Stroke(new Vector2(0, 0), new Vector2(50_000, 0))));
    }

    [Fact]
    public void Clone_shares_elements_but_not_layer_lists()
    {
        InkDocument document = TestData.SampleDocument();
        InkDocument copy = document.Clone();

        Assert.Equal(document.ElementCount, copy.ElementCount);
        Assert.Same(document.Layers[0].Elements[0], copy.Layers[0].Elements[0]);

        copy.AddElement(TestData.Stroke(new Vector2(0, 0), new Vector2(1, 1)));
        Assert.NotEqual(document.ElementCount, copy.ElementCount);
    }
}

public class HistoryTests
{
    [Fact]
    public void Undo_and_redo_restore_the_element()
    {
        var document = new InkDocument();
        using var history = new HistoryManager(document);
        Stroke stroke = TestData.Stroke(new Vector2(0, 0), new Vector2(10, 10));

        document.AddElement(stroke);
        Assert.True(history.CanUndo);

        Assert.True(history.Undo());
        Assert.True(document.IsEmpty);
        Assert.True(history.CanRedo);

        Assert.True(history.Redo());
        Assert.Equal(1, document.ElementCount);
        Assert.Equal(stroke.Id, document.Layers[0].Elements[0].Id);
    }

    [Fact]
    public void Undo_restores_the_z_order_position()
    {
        var document = new InkDocument();
        using var history = new HistoryManager(document);
        Stroke a = TestData.Stroke(new Vector2(0, 0), new Vector2(1, 1));
        Stroke b = TestData.Stroke(new Vector2(2, 2), new Vector2(3, 3));
        Stroke c = TestData.Stroke(new Vector2(4, 4), new Vector2(5, 5));
        document.AddElement(a);
        document.AddElement(b);
        document.AddElement(c);

        document.RemoveElement(b.Id);
        history.Undo();

        Assert.Equal([a.Id, b.Id, c.Id], document.Layers[0].Elements.Select(e => e.Id));
    }

    [Fact]
    public void A_new_change_clears_the_redo_stack()
    {
        var document = new InkDocument();
        using var history = new HistoryManager(document);
        document.AddElement(TestData.Stroke(new Vector2(0, 0), new Vector2(1, 1)));
        history.Undo();
        Assert.True(history.CanRedo);

        document.AddElement(TestData.Stroke(new Vector2(5, 5), new Vector2(6, 6)));
        Assert.False(history.CanRedo);
    }

    [Fact]
    public void Groups_collapse_many_changes_into_one_step()
    {
        var document = new InkDocument();
        using var history = new HistoryManager(document);
        Stroke stroke = TestData.Stroke(new Vector2(0, 0), new Vector2(10, 10));
        document.AddElement(stroke);

        using (history.BeginGroup("Drag"))
        {
            for (int i = 1; i <= 20; i++)
            {
                document.ReplaceElement(document.GetElement(stroke.Id) with { Transform = Matrix3x2.CreateTranslation(i, 0) });
            }
        }

        Assert.Equal(2, history.UndoCount);
        history.Undo();
        Assert.Equal(Matrix3x2.Identity, document.GetElement(stroke.Id).Transform);
    }

    [Fact]
    public void Remote_and_load_changes_are_not_recorded()
    {
        var document = new InkDocument();
        using var history = new HistoryManager(document);
        document.Apply(new AddElementOperation(document.Layers[0].Id, TestData.Stroke(new Vector2(0, 0), new Vector2(1, 1))), ChangeOrigin.Remote);
        Assert.False(history.CanUndo);
    }

    [Fact]
    public void The_limit_drops_the_oldest_steps()
    {
        var document = new InkDocument();
        using var history = new HistoryManager(document, limit: 5);
        for (int i = 0; i < 20; i++) document.AddElement(TestData.Stroke(new Vector2(i, 0), new Vector2(i + 1, 1)));
        Assert.Equal(5, history.UndoCount);
    }

    [Fact]
    public void Undoing_everything_returns_to_the_original_document()
    {
        var document = new InkDocument();
        using var history = new HistoryManager(document);
        Guid layerId = document.Layers[0].Id;

        document.AddElement(TestData.Stroke(new Vector2(0, 0), new Vector2(10, 10)));
        Layer layer = document.AddLayer("Second");
        document.AddElement(TestData.Stroke(new Vector2(20, 20), new Vector2(30, 30)), layer.Id);
        document.UpdateLayer(layerId, p => p with { Opacity = 0.5f });
        document.SetMetadata("author", "test");

        while (history.CanUndo) history.Undo();

        Assert.Single(document.Layers);
        Assert.True(document.IsEmpty);
        Assert.Equal(1f, document.Layers[0].Opacity);
        Assert.Empty(document.Metadata);
    }

    [Fact]
    public void History_does_not_copy_stroke_point_data()
    {
        var document = new InkDocument();
        using var history = new HistoryManager(document);
        Stroke stroke = TestData.Stroke(new Vector2(0, 0), new Vector2(100, 100), count: 500);
        document.AddElement(stroke);
        document.RemoveElement(stroke.Id);

        // The undo entry references the same points instance, so undo history costs no per-point memory.
        var operation = (AddElementOperation)history.UndoStack[^1].Inverse;
        Assert.Same(stroke.Points, ((Stroke)operation.Element).Points);
    }
}

public class SelectionTests
{
    [Fact]
    public void Selection_drops_elements_that_are_removed()
    {
        var document = new InkDocument();
        using var selection = new Selection(document);
        Stroke stroke = TestData.Stroke(new Vector2(0, 0), new Vector2(10, 10));
        document.AddElement(stroke);
        selection.Set(stroke.Id);
        Assert.Equal(1, selection.Count);

        document.RemoveElement(stroke.Id);
        Assert.True(selection.IsEmpty);
    }

    [Fact]
    public void Eraser_strokes_cannot_be_selected()
    {
        var document = new InkDocument();
        using var selection = new Selection(document);
        var eraser = new Stroke(TestData.Line(new Vector2(0, 0), new Vector2(10, 10)), new PixelEraserBrush());
        document.AddElement(eraser);
        selection.Add(eraser.Id);
        Assert.True(selection.IsEmpty);
    }

    [Fact]
    public void SelectAll_skips_locked_and_hidden_layers()
    {
        var document = new InkDocument();
        using var selection = new Selection(document);
        document.AddElement(TestData.Stroke(new Vector2(0, 0), new Vector2(10, 10)));
        Layer locked = document.AddLayer("Locked");
        document.AddElement(TestData.Stroke(new Vector2(20, 20), new Vector2(30, 30)), locked.Id);
        document.UpdateLayer(locked.Id, p => p with { IsLocked = true });

        selection.SelectAll();
        Assert.Equal(1, selection.Count);
    }

    [Fact]
    public void HitTest_returns_the_topmost_element()
    {
        var document = new InkDocument();
        var bottom = new RectangleShape(new Rect(0, 0, 100, 100)) { Style = new ShapeStyle { FillColor = InkColor.White } };
        var top = new RectangleShape(new Rect(10, 10, 50, 50)) { Style = new ShapeStyle { FillColor = InkColor.Black } };
        document.AddElement(bottom);
        document.AddElement(top);

        Assert.Equal(top.Id, DocumentQuery.HitTest(document, new Vector2(30, 30))!.Id);
        Assert.Equal(bottom.Id, DocumentQuery.HitTest(document, new Vector2(90, 90))!.Id);
    }

    [Fact]
    public void HitTest_respects_the_element_transform()
    {
        var document = new InkDocument();
        var rect = new RectangleShape(new Rect(0, 0, 20, 20)) { Style = new ShapeStyle { FillColor = InkColor.Black }, Transform = Matrix3x2.CreateTranslation(100, 100) };
        document.AddElement(rect);

        Assert.Null(DocumentQuery.HitTest(document, new Vector2(10, 10), tolerance: 0));
        Assert.NotNull(DocumentQuery.HitTest(document, new Vector2(110, 110), tolerance: 0));
    }

    [Fact]
    public void FindInRect_distinguishes_intersect_from_contain()
    {
        var document = new InkDocument();
        document.AddElement(TestData.Stroke(new Vector2(0, 0), new Vector2(100, 0)));
        Assert.Single(DocumentQuery.FindInRect(document, new Rect(50, -10, 100, 20), RectSelectionMode.Intersect));
        Assert.Empty(DocumentQuery.FindInRect(document, new Rect(50, -10, 100, 20), RectSelectionMode.Contain));
        Assert.Single(DocumentQuery.FindInRect(document, new Rect(-20, -20, 200, 40), RectSelectionMode.Contain));
    }

    [Fact]
    public void Rect_selection_does_not_match_a_stroke_that_only_passes_nearby()
    {
        var document = new InkDocument();
        // A large diagonal stroke whose bounding box covers the query rectangle but whose ink does not.
        document.AddElement(TestData.Stroke(new Vector2(0, 0), new Vector2(200, 200), count: 50));
        Assert.Empty(DocumentQuery.FindInRect(document, new Rect(150, 20, 20, 20)));
        Assert.Single(DocumentQuery.FindInRect(document, new Rect(90, 90, 20, 20)));
    }
}
