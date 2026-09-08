using System.Numerics;
using BlazorInk.Brushes;
using BlazorInk.Geometry;
using AngleSharp.Dom;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BlazorInk.Tests.Components;

/// <summary>
/// Component-level tests. They render the real <see cref="InkCanvas"/> with a stubbed JavaScript runtime, so the
/// wiring between parameters, the document, history and selection is covered without a browser.
/// </summary>
public class InkCanvasTests : BunitContext
{
    public InkCanvasTests()
    {
        // The canvas talks to a JavaScript module; loose mode answers those calls with defaults.
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [Fact]
    public void Renders_the_two_canvas_layers_and_the_input_surface()
    {
        IRenderedComponent<InkCanvas> canvas = Render<InkCanvas>();

        Assert.NotNull(canvas.Find("canvas[data-bi-layer='document']"));
        Assert.NotNull(canvas.Find("canvas[data-bi-layer='overlay']"));
        Assert.NotNull(canvas.Find(".blazorink-input"));
    }

    [Fact]
    public void Exposes_an_accessible_role_name_and_description()
    {
        IRenderedComponent<InkCanvas> canvas = Render<InkCanvas>(p => p.Add(c => c.AriaLabel, "Signature"));

        IElement container = canvas.Find(".blazorink-canvas");
        Assert.Equal("application", container.GetAttribute("role"));
        Assert.Equal("Signature", container.GetAttribute("aria-label"));
        Assert.Contains("Empty drawing canvas", canvas.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void Creates_its_own_document_when_none_is_supplied()
    {
        IRenderedComponent<InkCanvas> canvas = Render<InkCanvas>();
        Assert.NotNull(canvas.Instance.CurrentDocument);
        Assert.Single(canvas.Instance.CurrentDocument.Layers);
    }

    [Fact]
    public void Uses_the_supplied_document()
    {
        var document = new InkDocument();
        IRenderedComponent<InkCanvas> canvas = Render<InkCanvas>(p => p.Add(c => c.Document, document));
        Assert.Same(document, canvas.Instance.CurrentDocument);
    }

    [Fact]
    public void Records_history_for_changes_made_through_the_document()
    {
        IRenderedComponent<InkCanvas> canvas = Render<InkCanvas>();
        InkDocument document = canvas.Instance.CurrentDocument;

        document.AddElement(new Stroke(TestPoints(), new PenBrush()));

        Assert.Equal(1, canvas.Instance.History.UndoCount);
        Assert.True(canvas.Instance.History.CanUndo);
    }

    [Fact]
    public void Undo_and_redo_work_through_the_component_api()
    {
        IRenderedComponent<InkCanvas> canvas = Render<InkCanvas>();
        InkDocument document = canvas.Instance.CurrentDocument;
        document.AddElement(new Stroke(TestPoints(), new PenBrush()));

        Assert.True(canvas.Instance.Undo());
        Assert.Equal(0, document.ElementCount);

        Assert.True(canvas.Instance.Redo());
        Assert.Equal(1, document.ElementCount);
    }

    [Fact]
    public void The_history_limit_parameter_is_applied()
    {
        IRenderedComponent<InkCanvas> canvas = Render<InkCanvas>(p => p.Add(c => c.HistoryLimit, 3));
        InkDocument document = canvas.Instance.CurrentDocument;

        for (int i = 0; i < 10; i++) document.AddElement(new Stroke(TestPoints(i * 10), new PenBrush()));

        Assert.Equal(3, canvas.Instance.History.UndoCount);
    }

    [Fact]
    public void The_accessible_description_follows_the_document()
    {
        IRenderedComponent<InkCanvas> canvas = Render<InkCanvas>();
        canvas.Instance.CurrentDocument.AddElement(new Stroke(TestPoints(), new PenBrush()) { Description = "A test stroke" });
        canvas.Render(p => { });

        Assert.Contains("Drawing with 1 elements", canvas.Markup, StringComparison.Ordinal);
        Assert.Contains("A test stroke", canvas.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void Clear_empties_the_document_in_one_undoable_step()
    {
        IRenderedComponent<InkCanvas> canvas = Render<InkCanvas>();
        InkDocument document = canvas.Instance.CurrentDocument;
        for (int i = 0; i < 3; i++) document.AddElement(new Stroke(TestPoints(i * 20), new PenBrush()));

        canvas.Instance.Clear();
        Assert.True(document.IsEmpty);

        Assert.True(canvas.Instance.Undo());
        Assert.Equal(3, document.ElementCount);
    }

    [Fact]
    public void ReadOnly_blocks_editing_commands()
    {
        IRenderedComponent<InkCanvas> canvas = Render<InkCanvas>(p => p.Add(c => c.ReadOnly, true));
        canvas.Instance.CurrentDocument.AddElement(new Stroke(TestPoints(), new PenBrush()));

        canvas.Instance.Clear();
        Assert.Equal(1, canvas.Instance.CurrentDocument.ElementCount);
        Assert.False(canvas.Instance.Undo());
    }

    [Fact]
    public void Raises_DocumentChanged_for_the_host()
    {
        var changes = new List<DocumentChangedEventArgs>();
        IRenderedComponent<InkCanvas> canvas = Render<InkCanvas>(p => p
            .Add(c => c.DocumentChanged, args => changes.Add(args)));

        canvas.Instance.CurrentDocument.AddElement(new Stroke(TestPoints(), new PenBrush()));

        Assert.Single(changes);
        Assert.Equal(ChangeOrigin.Local, changes[0].Origin);
    }

    [Fact]
    public void History_is_already_updated_when_DocumentChanged_is_raised()
    {
        // A host that re-renders from this callback must see the new undo state, not the previous one.
        int undoCountAtNotification = -1;
        IRenderedComponent<InkCanvas> canvas = null!;
        canvas = Render<InkCanvas>(p => p
            .Add(c => c.DocumentChanged, _ => undoCountAtNotification = canvas.Instance.History.UndoCount));

        canvas.Instance.CurrentDocument.AddElement(new Stroke(TestPoints(), new PenBrush()));

        Assert.Equal(1, undoCountAtNotification);
    }

    [Fact]
    public void Selection_is_already_updated_when_DocumentChanged_is_raised()
    {
        int selectionAtNotification = -1;
        IRenderedComponent<InkCanvas> canvas = null!;
        canvas = Render<InkCanvas>(p => p
            .Add(c => c.DocumentChanged, _ => selectionAtNotification = canvas.Instance.Selection.Count));

        var stroke = new Stroke(TestPoints(), new PenBrush());
        canvas.Instance.CurrentDocument.AddElement(stroke);
        canvas.Instance.Selection.Set(stroke.Id);

        canvas.Instance.CurrentDocument.RemoveElement(stroke.Id);
        Assert.Equal(0, selectionAtNotification);
    }

    [Fact]
    public void Replacing_the_document_parameter_rebinds_history_and_selection()
    {
        var first = new InkDocument();
        IRenderedComponent<InkCanvas> canvas = Render<InkCanvas>(p => p.Add(c => c.Document, first));
        first.AddElement(new Stroke(TestPoints(), new PenBrush()));
        Assert.Equal(1, canvas.Instance.History.UndoCount);

        var second = new InkDocument();
        canvas.Render(p => p.Add(c => c.Document, second));

        Assert.Same(second, canvas.Instance.CurrentDocument);
        Assert.Equal(0, canvas.Instance.History.UndoCount);

        second.AddElement(new Stroke(TestPoints(), new PenBrush()));
        Assert.Equal(1, canvas.Instance.History.UndoCount);

        // The old document must no longer drive the component.
        first.AddElement(new Stroke(TestPoints(50), new PenBrush()));
        Assert.Equal(1, canvas.Instance.History.UndoCount);
    }

    [Fact]
    public void The_viewport_parameter_is_applied_and_reported()
    {
        Viewport? reported = null;
        IRenderedComponent<InkCanvas> canvas = Render<InkCanvas>(p => p
            .Add(c => c.Viewport, new Viewport(2f, new Vector2(10, 20)))
            .Add(c => c.ViewportChanged, v => reported = v));

        Assert.Equal(2f, canvas.Instance.CurrentViewport.Zoom);

        canvas.Instance.SetViewport(new Viewport(3f, Vector2.Zero));
        Assert.Equal(3f, canvas.Instance.CurrentViewport.Zoom);
        Assert.Equal(3f, reported!.Value.Zoom);
    }

    [Fact]
    public async Task Exports_svg_from_the_component()
    {
        IRenderedComponent<InkCanvas> canvas = Render<InkCanvas>();
        canvas.Instance.CurrentDocument.AddElement(new Stroke(TestPoints(), new PenBrush()));

        string svg = await canvas.Instance.ExportSvgAsync();
        Assert.StartsWith("<svg", svg, StringComparison.Ordinal);
        Assert.Contains("<path", svg, StringComparison.Ordinal);
    }

    [Fact]
    public void The_touch_action_style_follows_the_gesture_setting()
    {
        IRenderedComponent<InkCanvas> withGestures = Render<InkCanvas>(p => p.Add(c => c.EnableTouchGestures, true));
        Assert.Contains("touch-action:none", withGestures.Find(".blazorink-canvas").GetAttribute("style")!, StringComparison.Ordinal);

        IRenderedComponent<InkCanvas> withScrolling = Render<InkCanvas>(p => p.Add(c => c.EnableTouchGestures, false));
        Assert.Contains("touch-action:pan-x pan-y", withScrolling.Find(".blazorink-canvas").GetAttribute("style")!, StringComparison.Ordinal);
    }

    private static StrokePoints TestPoints(float offset = 0)
    {
        var buffer = new StrokePointBuffer();
        for (int i = 0; i < 12; i++) buffer.Add(new InkPoint(offset + i * 5f, 20f + i * 3f, 0.5f, i * 8f));
        return buffer.ToStrokePoints();
    }
}
