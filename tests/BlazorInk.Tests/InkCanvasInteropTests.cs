using System.Numerics;
using BlazorInk.Brushes;
using BlazorInk.Export;
using BlazorInk.Geometry;
using BlazorInk.Input;
using BlazorInk.Interop;
using BlazorInk.Rendering;
using BlazorInk.Tools;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Xunit;

namespace BlazorInk.Tests.Components;

/// <summary>
/// The contract between the component and its browser layer: what reaches JavaScript, and what the component does
/// when the browser calls back or could not be reached at all.
/// </summary>
public class InkCanvasInteropTests : BunitContext
{
    private const string ModulePath = "./_content/BlazorInk/blazorink.js";

    public InkCanvasInteropTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    /// <summary>Makes the module's attach succeed, so the component reaches its attached state.</summary>
    private void SetupAttachedModule()
    {
        BunitJSModuleInterop module = JSInterop.SetupModule(ModulePath);
        module.Mode = JSRuntimeMode.Loose;
        module.Setup<AttachResult?>("attach", _ => true).SetResult(new AttachResult { Width = 400, Height = 300, DevicePixelRatio = 1 });
    }

    private static double[] Samples(params InputSample[] samples)
    {
        var data = new double[samples.Length * InputSample.PackedSize];
        for (int i = 0; i < samples.Length; i++) samples[i].WriteTo(data.AsSpan(i * InputSample.PackedSize, InputSample.PackedSize));
        return data;
    }

    private static InputSample Mouse(PointerPhase phase, float x, float y, double time) => new()
    {
        PointerId = 1,
        Kind = PointerKind.Mouse,
        Phase = phase,
        X = x,
        Y = y,
        Pressure = 0.5f,
        Timestamp = time,
        Buttons = phase == PointerPhase.Up ? 0 : 1,
        IsPrimary = true,
    };

    private static void Send(IRenderedComponent<InkCanvas> canvas, params InputSample[] samples)
    {
        double[] data = Samples(samples);
        canvas.InvokeAsync(() => ((IInkInteropClient)canvas.Instance).OnSamples(data, samples.Length));
    }

    [Fact]
    public async Task An_image_with_a_known_size_is_loaded_into_the_browser_when_added()
    {
        // The canvas only draws images the browser has decoded. Skipping the load because the size was already
        // known left the element in the document and the canvas showing nothing but the background.
        IRenderedComponent<InkCanvas> canvas = Render<InkCanvas>();
        ImageSource source = ImageSource.FromBytes([1, 2, 3], "image/png", 640, 400);

        ImageElement? element = await canvas.InvokeAsync(() => canvas.Instance.AddImageAsync(source, Vector2.Zero));

        Assert.NotNull(element);
        Assert.Equal(640, element!.Source.Width);
        Assert.Contains(JSInterop.Invocations, i => i.Identifier == "loadImage" && Equals(i.Arguments[1], source.Uri));
    }

    [Fact]
    public void A_redraw_request_from_the_browser_repaints_the_document_layer()
    {
        // Sent when an image the last frame referenced finishes decoding, or a discarded canvas is restored.
        SetupAttachedModule();
        IRenderedComponent<InkCanvas> canvas = Render<InkCanvas>();
        canvas.Instance.CurrentDocument.AddElement(new Stroke(StrokePoints.FromPositions([new Vector2(10, 10), new Vector2(90, 60)]), new PenBrush()));
        canvas.WaitForAssertion(() => Assert.Contains(JSInterop.Invocations, i => i.Identifier == "execute"));

        int before = JSInterop.Invocations.Count(i => i.Identifier == "execute" && Equals(i.Arguments[1], "document"));
        canvas.InvokeAsync(() => ((IInkInteropClient)canvas.Instance).OnRedrawRequested());

        canvas.WaitForAssertion(() => Assert.True(
            JSInterop.Invocations.Count(i => i.Identifier == "execute" && Equals(i.Arguments[1], "document")) > before,
            "the document layer was not redrawn"));
    }

    [Fact]
    public void Loading_a_document_mid_stroke_does_not_commit_the_stroke_into_the_new_document()
    {
        IRenderedComponent<InkCanvas> canvas = Render<InkCanvas>();
        InkDocument first = canvas.Instance.CurrentDocument;
        Send(canvas, Mouse(PointerPhase.Down, 10, 10, 0), Mouse(PointerPhase.Move, 40, 30, 8), Mouse(PointerPhase.Move, 80, 50, 16));

        var second = new InkDocument();
        canvas.InvokeAsync(() => canvas.Instance.LoadDocument(second));
        Send(canvas, Mouse(PointerPhase.Up, 120, 70, 24));

        Assert.Equal(0, first.ElementCount);
        Assert.Equal(0, second.ElementCount);
        Assert.False(canvas.Instance.History.CanUndo);
    }

    [Fact]
    public async Task Png_export_falls_back_to_the_managed_rasterizer_when_the_browser_layer_is_not_attached()
    {
        // Loose mode loads the module but never completes the attach. The browser has no surface to render into,
        // so the export used to come back empty instead of using the .NET rasterizer.
        IRenderedComponent<InkCanvas> canvas = Render<InkCanvas>();
        canvas.Instance.CurrentDocument.AddElement(new Stroke(StrokePoints.FromPositions([new Vector2(10, 10), new Vector2(90, 60)]), new PenBrush()));

        byte[] png = await canvas.InvokeAsync(() => canvas.Instance.ExportPngAsync(new PngExportOptions { Region = ExportRegion.Content }));

        Assert.NotNull(png);
        Assert.True(png.Length > 8, "the export returned no image");
        Assert.Equal(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G' }, png[..4]);
    }

    [Fact]
    public async Task Saving_reports_why_when_the_browser_module_failed_to_load()
    {
        // A blocked module used to make every save a silent no-op, which looks to the user like a broken button.
        Services.AddSingleton<IJSRuntime>(new BlockedModuleRuntime());
        IRenderedComponent<InkCanvas> canvas = Render<InkCanvas>();
        canvas.WaitForAssertion(() => Assert.NotNull(canvas.Instance.AttachFailed));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => canvas.InvokeAsync(() => canvas.Instance.SaveSvgAsync()));
        Assert.Contains("Blocked by policy", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_canvas_that_cannot_start_says_so_instead_of_waiting_silently()
    {
        // Ready is never raised when the module is blocked, so an application that showed the reason from Ready,
        // as the hosting documentation once did, never showed anything.
        Services.AddSingleton<IJSRuntime>(new BlockedModuleRuntime());
        string? reason = null;
        bool ready = false;
        IRenderedComponent<InkCanvas> canvas = Render<InkCanvas>(p => p
            .Add(c => c.Unavailable, (string r) => reason = r)
            .Add(c => c.Ready, (InputCapabilities _) => ready = true));

        canvas.WaitForAssertion(() => Assert.Equal("Blocked by policy", reason));
        Assert.False(ready);
        // A screen reader user focusing the canvas hears why it does nothing.
        Assert.Contains("Drawing is unavailable: Blocked by policy", canvas.Find($"#{canvas.Instance.ElementId}-desc").TextContent, StringComparison.Ordinal);
    }

    /// <summary>A runtime whose module import fails, as it does under a content security policy that blocks it.</summary>
    private sealed class BlockedModuleRuntime : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
            => InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
            => ValueTask.FromException<TValue>(new JSException("Blocked by policy"));
    }

    [Fact]
    public void The_container_carries_the_element_id()
    {
        IRenderedComponent<InkCanvas> canvas = Render<InkCanvas>();
        Assert.Equal(canvas.Instance.ElementId, canvas.Find(".blazorink-canvas").GetAttribute("id"));
        Assert.NotNull(canvas.Find($"#{canvas.Instance.ElementId}-desc"));
    }

    [Fact]
    public void Turning_keyboard_shortcuts_off_after_attach_reaches_the_browser_layer()
    {
        // The browser layer prevents the default action of the keys it owns synchronously, from the options it
        // was given. Without an update it kept swallowing Ctrl+Z and Ctrl+A after shortcuts were turned off.
        SetupAttachedModule();
        IRenderedComponent<InkCanvas> canvas = Render<InkCanvas>(p => p.Add(c => c.EnableKeyboardShortcuts, true));
        canvas.WaitForAssertion(() => Assert.Contains(JSInterop.Invocations, i => i.Identifier == "setCursor"));

        canvas.Render(p => p.Add(c => c.EnableKeyboardShortcuts, false));

        JSRuntimeInvocation update = Assert.Single(JSInterop.Invocations, i => i.Identifier == "setOptions");
        Assert.False(Assert.IsType<InteropOptions>(update.Arguments[1]).HandleKeyboard);
    }

    [Fact]
    public void Switching_tools_through_the_api_closes_the_inline_text_editor()
    {
        var document = new InkDocument();
        document.AddElement(new TextElement(new Vector2(20, 20), "Hello"));
        var text = new TextTool();
        IRenderedComponent<InkCanvas> canvas = Render<InkCanvas>(p => p.Add(c => c.Document, document).Add(c => c.Tool, text));

        Send(canvas, Mouse(PointerPhase.Down, 25, 25, 0));
        canvas.WaitForAssertion(() => Assert.NotEmpty(canvas.FindAll("textarea.blazorink-text-editor")));

        // No parameter changes, so nothing but the component itself can re-render its markup.
        canvas.InvokeAsync(() => canvas.Instance.SetTool(new DrawTool()));

        canvas.WaitForAssertion(() => Assert.Empty(canvas.FindAll("textarea.blazorink-text-editor")));
    }
}
