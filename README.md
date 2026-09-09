# BlazorInk

A native drawing and ink engine for Blazor.

The stroke model, smoothing, pressure response, hit testing, undo, serialization and export are written in C#.
JavaScript does only what the browser alone can do: collect pointer and keyboard input, and replay a command
buffer onto a canvas. There is no JavaScript drawing library underneath. The one drawing JavaScript does is a
provisional trail under Blazor Server, described in [hosting models](https://github.com/msynk/BlazorInk/blob/main/docs/hosting-models.md), which exists to
hide network latency and is replaced by the engine's own geometry.

```razor
<InkCanvas @ref="canvas" Height="200px" AriaLabel="Signature" />

@code {
    private InkCanvas? canvas;

    private void Clear() => canvas!.Clear();
    private Task<string> SaveAsync() => canvas!.ExportSvgAsync();
}
```

## What it is for

Signature fields, handwriting input, whiteboards, image markup, document review, diagram surfaces, and anything
else that needs ink. The engine is a vector document model, so a drawing can be saved, versioned, transmitted,
re-rendered at any size and edited later, rather than being a picture of a drawing.

## Packages

| Package | Contents |
| --- | --- |
| `BlazorInk.Core` | The engine: document model, strokes, brushes, geometry, tools, undo, serialization, SVG and PNG export. No UI, no JavaScript, runs anywhere .NET runs. |
| `BlazorInk` | The `InkCanvas` component and the browser layer. Depends on Core. |
| `BlazorInk.Collaboration` | Transport-agnostic operation sync, presence and offline queueing. Optional. |

A signature pad needs `BlazorInk`. Nothing else is required.

## Installation

```bash
dotnet add package BlazorInk
```

Add the namespace to `_Imports.razor`:

```razor
@using BlazorInk
@using BlazorInk.Brushes
@using BlazorInk.Geometry
@using BlazorInk.Rendering
@using BlazorInk.Tools
```

The stylesheet the component needs is injected automatically by the package's Blazor initializer. To add it
yourself instead, reference `_content/BlazorInk/blazorink.css`.

## Architecture

```text
Pointer events (browser)
  -> batched once per animation frame
  -> StrokeBuilder      spacing filter, streamline smoothing, pressure
  -> Stroke + Brush     immutable vector model
  -> DrawingOperation   the unit of undo, diffing and collaboration
  -> InkDocument        layers of elements
  -> IRenderContext     canvas commands, SVG, or the built-in rasterizer
```

Every renderer draws the same elements through the same `IRenderContext` interface, so an exported file matches
what was drawn. Pointer input never triggers a Blazor render: samples arrive once per frame, the active tool
processes them, and only the affected canvas layer is redrawn.

## Documentation

- [Getting started](https://github.com/msynk/BlazorInk/blob/main/docs/getting-started.md)
- [Architecture](https://github.com/msynk/BlazorInk/blob/main/docs/architecture.md)
- [Drawing, brushes and pressure](https://github.com/msynk/BlazorInk/blob/main/docs/drawing-and-brushes.md)
- [Erasing](https://github.com/msynk/BlazorInk/blob/main/docs/erasing.md)
- [Selection, transforms and layers](https://github.com/msynk/BlazorInk/blob/main/docs/selection-and-layers.md)
- [Annotations, text and images](https://github.com/msynk/BlazorInk/blob/main/docs/annotations-and-images.md)
- [Serialization](https://github.com/msynk/BlazorInk/blob/main/docs/serialization.md)
- [SVG and PNG export](https://github.com/msynk/BlazorInk/blob/main/docs/export.md)
- [Hosting models: WebAssembly, Server and Hybrid](https://github.com/msynk/BlazorInk/blob/main/docs/hosting-models.md)
- [Performance](https://github.com/msynk/BlazorInk/blob/main/docs/performance.md)
- [Accessibility](https://github.com/msynk/BlazorInk/blob/main/docs/accessibility.md)
- [Mobile, touch and stylus](https://github.com/msynk/BlazorInk/blob/main/docs/mobile-and-stylus.md)
- [Collaboration](https://github.com/msynk/BlazorInk/blob/main/docs/collaboration.md)
- [Extending the framework](https://github.com/msynk/BlazorInk/blob/main/docs/extending.md)

## Demo

```bash
dotnet run --project samples/BlazorInk.Demo
```

The demo covers a signature field, a handwriting surface, a full whiteboard, a diagram surface with connectors
that follow the nodes they join, image annotation, document review, two-way collaboration, the serialization
format and a performance page that measures the engine in your browser.

## Requirements

.NET 8 or .NET 10. Works in Blazor WebAssembly, Blazor Server, Blazor Web App and Blazor Hybrid.

## Licence

MIT.
