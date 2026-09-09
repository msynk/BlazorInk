# Extending the framework

Six extension points cover the things applications actually need to change. Each is a small interface or an
abstract record, and none of them requires touching the engine.

## A custom brush

Derive from `Brush`, give it a unique `Kind`, and implement `BuildGeometry`.

```csharp
public sealed record DashedBrush : Brush
{
    public const string KindName = "dashed";

    public override string Kind => KindName;

    /// <summary>Length of each dash in document units.</summary>
    public float DashLength { get; init; } = 6f;

    public override StrokeGeometry BuildGeometry(StrokePointsView points, PathBuilder builder)
    {
        builder.Reset();
        // Emit whatever geometry you like: this walks the points and draws every other segment.
        for (int i = 1; i < points.Count; i++)
        {
            if (i % 2 == 0) continue;
            builder.MoveTo(points.PositionAt(i - 1));
            builder.LineTo(points.PositionAt(i));
        }
        return new StrokeGeometry(
            builder.ToGeometry(),
            StrokeGeometryMode.Centerline,
            Width,
            LineCap.Round,
            LineJoin.Round,
            points.Bounds.Inflate(Width));
    }
}
```

Register the kind so documents containing it can be loaded:

```csharp
BrushRegistry.Default.Register<DashedBrush>();
```

The registry supplies a default instance, and the serialized colour, width, opacity, blend mode, smoothing,
spacing, taper and pressure curve are applied on top of it. A client without the registration still renders the
stroke, as a pen with the stored appearance, rather than losing it.

`StrokePointsView` gives you positions, pressure and timing. Tilt and twist are on the stroke's `Points` if you
need them for a chisel nib or a calligraphic brush.

## A custom tool

Implement `ITool`, or derive from `ToolBase` and override only what you need.

For a worked example larger than the snippet below, read `samples/BlazorInk.Demo/Diagramming`. It builds a whole
node-and-connector diagram surface out of the public API alone: two tools, element metadata for the graph
structure, and a listener that re-routes connectors when a node moves. The framework has no idea what a diagram
is, and nothing in it needed changing.

```csharp
public sealed class DotTool : ToolBase
{
    public override string Id => "dot";
    public override string DisplayName => "Dot";
    public override string Cursor => "crosshair";

    public override bool OnPointerDown(IToolContext context, in InputSample sample)
    {
        Vector2 point = context.Viewport.ToDocument(sample.Position);
        context.Document.AddElement(
            new EllipseShape(Rect.FromCenter(point, 12f, 12f))
            {
                Style = new ShapeStyle { FillColor = context.ShapeStyle.StrokeColor, StrokeWidth = 0f },
            },
            context.ActiveLayerId);
        context.InvalidateDocument();
        return true;
    }
}
```

`IToolContext` gives a tool the document, selection, history, viewport, active layer, current brush and shape
style, plus the ability to invalidate a canvas layer, set the cursor and announce a message.

Tools mutate the document exclusively through operations, so anything a tool does is undoable and synchronizable
without extra work. Wrap a gesture in `context.History.BeginGroup()` to make it a single undo step.

Draw a live preview by overriding `DrawOverlay`; it is called on the overlay canvas, in document coordinates,
whenever you have called `InvalidateOverlay`.

## A custom render target

Implement `IRenderContext` to draw a document anywhere: a PDF library, a printer, a different graphics API.

```csharp
public sealed class PdfRenderContext : IRenderContext
{
    public void FillPath(PathGeometry path, in FillPaint paint) { /* walk path.Verbs and path.Coordinates */ }
    public void StrokePath(PathGeometry path, in StrokePaint paint) { }
    // Save, Restore, Transform, ClipRect, DrawImage, DrawText, PushGroup, PopGroup
}

DocumentRenderer.Render(document, new PdfRenderContext());
```

Two rules matter. `PushGroup` must genuinely isolate, because content inside it may erase and that must not reach
what was drawn before. And fills must honour `FillRule.NonZero`, because that is what keeps a self-overlapping
stroke outline solid.

## A text engine for server-side rendering

The built-in rasterizer has no font engine. Supply one to draw text in server-side PNG export.

```csharp
public sealed class SkiaTextRasterizer : ITextRasterizer
{
    public void DrawText(RasterSurface surface, string text, Vector2 origin, in TextStyle style,
                         float scale, float opacity, Rect clip) { }

    public Vector2? Measure(string text, in TextStyle style) => null;
}

PngExporter.Export(document, new PngExportOptions { TextRasterizer = new SkiaTextRasterizer() });
```

## A collaboration transport

Implement `ICollaborationTransport` over SignalR, WebSockets or anything else. See
[Collaboration](collaboration.md).

## A custom element type

`InkElement` is an abstract record you can derive from, and a custom element renders, hit tests and transforms
like any other. The serializer will refuse to write it, deliberately: an unknown type in a saved file cannot be
read back by anyone else. Convert custom elements to built-in ones before saving, or serialize them yourself
alongside the document and re-attach them on load.

## What is deliberately closed

The operation set. `DrawingOperation` cannot be subclassed from outside the assembly, because undo,
serialization and synchronization all depend on knowing every operation's inverse and wire format. Compose the
existing operations, including `BatchOperation`, instead.
