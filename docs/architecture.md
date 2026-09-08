# Architecture

## The shape of the system

```text
Input               browser pointer events, batched once per animation frame
  |
Stroke recognition  StrokeBuilder: spacing filter, streamline smoothing, pressure
  |
Stroke model        Stroke + Brush, immutable, structure-of-arrays points
  |
Commands            DrawingOperation, the unit of undo, diffing and collaboration
  |
Document            InkDocument: layers of elements, settings, metadata
  |
Renderer            IRenderContext: canvas commands, SVG, or the built-in rasterizer
```

Every layer is independent of the ones above it. `BlazorInk.Core` contains all of it except the browser bridge,
has no dependency on ASP.NET Core, and runs on a server, in a console application or in a unit test.

## The document model

```text
InkDocument
 |- Settings          page size and background
 |- Metadata          application key/value data, preserved by serialization
 |- Layers            ordered, each with a name, visibility, lock and opacity
      |- Elements     strokes, shapes, text, images, highlights, callouts
```

Elements are immutable records identified by a `Guid`. Editing one means creating a modified copy with `with` and
replacing it in the document. Point data and cached geometry are shared between copies, so a `with` on a
10,000-point stroke copies a handful of references, not the points.

This is what makes the rest of the system simple:

- **Undo** stores the inverse operation, never a copy of the document.
- **Collaboration** transmits the changed element, never the document.
- **Diffing** is a comparison of element ids and versions.

## Operations

Every mutation goes through `InkDocument.Apply`, which returns the inverse:

```csharp
DrawingOperation inverse = document.Apply(new AddElementOperation(layerId, stroke));
```

The operation types are `AddElement`, `RemoveElement`, `ReplaceElement`, `ReorderElement`, `AddLayer`,
`RemoveLayer`, `UpdateLayer`, `ReorderLayer`, `UpdateSettings`, `SetMetadata` and `Batch`. A batch applies
atomically: if one part fails, the already-applied parts are rolled back, so a document is never left half-changed.

`BeginBatch` groups a burst of changes into one event and one undo step:

```csharp
using (document.BeginBatch("Import"))
{
    foreach (Stroke stroke in strokes) document.AddElement(stroke);
}
```

The operation set is deliberately closed. Custom behaviour composes the existing operations rather than adding
new ones, which keeps undo, serialization and synchronization correct for every editor built on the library.

## Undo and redo

`HistoryManager` subscribes to the document and records the inverse of every local change as it happens. No
snapshots are taken. Memory is proportional to what changed, and the elements referenced by history share their
point data with the document.

Changes made with origin `Remote` or `Load` are not recorded: a collaborator's edit belongs to their history, not
yours. Undo and redo are re-broadcast to collaborators as ordinary edits, because that is what they are.

`BeginGroup` merges a gesture's many operations, such as every frame of a drag, into a single undo step.

## Rendering

`IRenderContext` is the only drawing surface the engine knows. Three implementations exist:

| Implementation | Used for |
| --- | --- |
| `CanvasRenderContext` | Records a flat command buffer that the browser replays onto a 2D canvas. |
| `SvgRenderContext` | Writes SVG markup. Paths stay paths. |
| `RasterRenderContext` | Draws into a pixel buffer with the built-in anti-aliased rasterizer. |

`DocumentRenderer.Render` walks the document once and calls the context. Because the screen, SVG export and PNG
export all go through it, an exported file cannot drift from what was drawn.

Layers that contain an eraser stroke, or that have opacity below 1, are drawn inside an isolated group so erasing
cannot reach the layers below. On canvas that is a scratch bitmap; in SVG it becomes a mask; in the rasterizer it
is a separate surface.

## The browser bridge

`blazorink.js` contains no drawing logic. It does three things:

1. Collects pointer samples, including coalesced ones, and hands them to .NET once per animation frame.
2. Replays a command buffer of numbers onto a 2D canvas.
3. Reports capabilities, sizes and encoded image data.

In WebAssembly the command buffer reaches JavaScript through `[JSImport]` as a view over managed memory, so a
redraw is one call with no serialization. Everywhere else the same buffer travels through `IJSRuntime`.

## Two canvases

The component owns a **document canvas** holding committed elements and an **overlay canvas** holding the live
stroke, selection handles and tool previews.

While a stroke is being drawn, only the overlay is redrawn. The thousands of strokes already in the document are
not touched until the pen lifts. This is the single most important reason the canvas stays responsive on a large
document, and it is why pointer input never triggers a Blazor render.

## Coordinate spaces

| Space | Meaning |
| --- | --- |
| Document | The units elements are stored in. Never changes when the user pans or zooms. |
| View | CSS pixels relative to the canvas element. `view = document * Zoom + Offset`. |
| Device | Physical canvas pixels, `view * devicePixelRatio`. Handled by the renderer. |

`Viewport` is an immutable value; panning and zooming produce a new one, so vector data is never rewritten by a
change of view.

## Extension points

- `Brush` is an abstract record. Derive from it, implement `BuildGeometry`, register the kind with `BrushRegistry`.
- `ITool` decides what pointer input does. Implement it for a custom interaction.
- `IRenderContext` lets you render a document to any surface.
- `ITextRasterizer` supplies a font engine for server-side PNG export.
- `ICollaborationTransport` moves operations between participants over any transport.

See [Extending the framework](extending.md).
