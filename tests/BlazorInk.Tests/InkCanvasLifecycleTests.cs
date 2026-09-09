using System.Numerics;
using BlazorInk.Brushes;
using BlazorInk.Geometry;
using Bunit;
using Microsoft.JSInterop;
using Xunit;
using Xunit.Abstractions;

namespace BlazorInk.Tests.Components;

/// <summary>
/// Component lifecycle: repeated creation and disposal, several canvases on one page, and the paths that have to
/// keep working when the browser module never loaded.
/// </summary>
public class InkCanvasLifecycleTests : BunitContext
{
    private readonly ITestOutputHelper _output;

    public InkCanvasLifecycleTests(ITestOutputHelper output)
    {
        _output = output;
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private static StrokePoints Points(float offset = 0)
    {
        var buffer = new StrokePointBuffer();
        for (int i = 0; i < 12; i++) buffer.Add(new InkPoint(offset + i * 5f, 20f + i * 3f, 0.5f, i * 8f));
        return buffer.ToStrokePoints();
    }

    [Fact]
    public async Task Repeated_create_draw_dispose_cycles_leave_nothing_behind()
    {
        for (int cycle = 0; cycle < 25; cycle++)
        {
            IRenderedComponent<InkCanvas> canvas = Render<InkCanvas>();
            canvas.Instance.CurrentDocument.AddElement(new Stroke(Points(cycle), new PenBrush()));
            Assert.Equal(1, canvas.Instance.History.UndoCount);
            await canvas.Instance.DisposeAsync();
            // Disposing twice is what a host does when a circuit tears down after an explicit dispose.
            await canvas.Instance.DisposeAsync();
        }
    }

    [Fact]
    public async Task A_disposed_canvas_ignores_further_document_changes()
    {
        IRenderedComponent<InkCanvas> canvas = Render<InkCanvas>();
        InkDocument document = canvas.Instance.CurrentDocument;
        await canvas.Instance.DisposeAsync();

        document.AddElement(new Stroke(Points(), new PenBrush()));
        _output.WriteLine($"undo count after disposal: {canvas.Instance.History.UndoCount}");
        Assert.False(canvas.Instance.Undo());
    }

    [Fact]
    public void Twenty_canvases_on_one_page_have_distinct_ids_and_isolated_state()
    {
        var canvases = new List<IRenderedComponent<InkCanvas>>();
        for (int i = 0; i < 20; i++) canvases.Add(Render<InkCanvas>());

        var ids = canvases.Select(c => c.Instance.ElementId).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());

        canvases[3].Instance.CurrentDocument.AddElement(new Stroke(Points(), new PenBrush()));
        Assert.Equal(1, canvases[3].Instance.CurrentDocument.ElementCount);
        for (int i = 0; i < canvases.Count; i++)
        {
            if (i == 3) continue;
            Assert.Equal(0, canvases[i].Instance.CurrentDocument.ElementCount);
            Assert.Equal(0, canvases[i].Instance.History.UndoCount);
        }
    }

    [Fact]
    public void Changing_the_brush_updates_the_wet_ink_style_sent_to_the_browser()
    {
        // The provisional trail is drawn by JavaScript with whatever style it was last told. Sending it only once
        // at attach left it drawing the previous colour and width until the round trip to the server replaced it.
        IRenderedComponent<InkCanvas> canvas = Render<InkCanvas>(p => p
            .Add(c => c.WetInk, true)
            .Add(c => c.Brush, new PenBrush { Color = InkColor.Parse("#ff0000"), Width = 3 }));

        int before = JSInterop.Invocations.Count(i => i.Identifier == "setWetInkStyle");

        canvas.Render(p => p
            .Add(c => c.WetInk, true)
            .Add(c => c.Brush, new PenBrush { Color = InkColor.Parse("#0000ff"), Width = 12 }));

        var after = JSInterop.Invocations.Where(i => i.Identifier == "setWetInkStyle").ToList();
        foreach (var call in after) _output.WriteLine("  " + string.Join(", ", call.Arguments));
        Assert.True(after.Count > before, "changing the brush did not update the wet-ink style");
    }

    [Fact]
    public void An_announcement_reaches_the_live_region_when_the_browser_module_is_unavailable()
    {
        // Loose mode never completes the attach, so this exercises the canvas whose module was blocked. Its own
        // markup - the live region, the accessible description, the inline editor - still has to update.
        IRenderedComponent<InkCanvas> canvas = Render<InkCanvas>();
        canvas.Instance.Announce("Canvas cleared.");
        canvas.WaitForState(() => canvas.Markup.Contains("Canvas cleared.", StringComparison.Ordinal), TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void The_accessible_description_is_not_rebuilt_for_every_element_of_a_large_document()
    {
        var document = new InkDocument();
        for (int i = 0; i < 2000; i++) document.AddElement(new Stroke(Points(i), new PenBrush()));
        IRenderedComponent<InkCanvas> canvas = Render<InkCanvas>(p => p.Add(c => c.Document, document));
        string markup = canvas.Markup;
        _output.WriteLine($"description length: {markup.Length}");
        Assert.Contains("2000 elements", markup, StringComparison.Ordinal);
        Assert.True(markup.Length < 20000, $"the accessible description grew to {markup.Length} characters");
    }

    [Fact]
    public async Task Exporting_while_the_document_is_being_edited_does_not_throw()
    {
        IRenderedComponent<InkCanvas> canvas = Render<InkCanvas>();
        InkDocument document = canvas.Instance.CurrentDocument;
        for (int i = 0; i < 50; i++) document.AddElement(new Stroke(Points(i), new PenBrush()));

        string svg = await Task.Run(() => canvas.Instance.ExportSvg(null));
        Assert.StartsWith("<svg", svg, StringComparison.Ordinal);
    }
}
