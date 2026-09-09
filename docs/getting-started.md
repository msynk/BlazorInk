# Getting started

## Install

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

The component's stylesheet is injected automatically when the app starts. If you prefer to control it, add the
link yourself and the automatic injection stands down:

```html
<link rel="stylesheet" href="_content/BlazorInk/blazorink.css" />
```

## A drawing surface

```razor
<InkCanvas Height="400px" />
```

That is a working canvas: pointer and stylus input, and touch as soon as you set `EnableTouchGestures`, pressure-aware ink, undo and redo on the keyboard.

## A signature field

```razor
<InkCanvas @ref="_canvas" Height="180px" AriaLabel="Sign here"
           DocumentChanged="() => StateHasChanged()" />

<button @onclick="Clear" disabled="@IsEmpty">Clear</button>
<button @onclick="AcceptAsync" disabled="@IsEmpty">Accept</button>

@code {
    private InkCanvas? _canvas;

    private bool IsEmpty => _canvas?.CurrentDocument.IsEmpty ?? true;

    private void Clear() => _canvas!.Clear();

    private async Task AcceptAsync()
    {
        string svg = await _canvas!.ExportSvgAsync(new SvgExportOptions
        {
            Region = ExportRegion.Content,   // crop to the ink
            Background = InkColor.Transparent,
        });
        await SaveToServerAsync(svg);
    }
}
```

`DocumentChanged` is what keeps your own UI in step. A child component cannot re-render its parent on its own, so
bind this event whenever your markup reads canvas state such as `CurrentDocument.IsEmpty` or `History.CanUndo`.
Drawing itself never re-renders anything.

## Choosing a brush

```razor
<InkCanvas Brush="_brush" Height="400px" />

@code {
    private Brush _brush = new PenBrush
    {
        Color = InkColor.Parse("#1d4ed8"),
        Width = 5f,
    };
}
```

Brushes are immutable records, so derive variants with `with`:

```csharp
_brush = _brush with { Color = InkColor.Parse("#dc2626") };
```

The built-in brushes are `PenBrush`, `PencilBrush`, `MarkerBrush`, `HighlighterBrush` and `PixelEraserBrush`.

## Choosing a tool

The `Tool` parameter decides what pointer input does.

```razor
<InkCanvas Tool="_tool" Brush="_brush" Height="400px" />

@code {
    private ITool _tool = new DrawTool();

    private void UseEraser() => _tool = new EraserTool { Mode = EraserMode.Stroke, Radius = 12f };
    private void UseSelect() => _tool = new SelectTool();
    private void UseArrow()  => _tool = new ShapeTool(ShapeKind.Arrow);
}
```

## Undo, redo and clearing

```csharp
_canvas.Undo();
_canvas.Redo();
_canvas.Clear();
_canvas.DeleteSelection();
```

`History` exposes `CanUndo`, `CanRedo`, `UndoLabel` and the stacks themselves if you want to show a history list.

Keyboard shortcuts work when the canvas has focus, which happens automatically when it is drawn on: Ctrl+Z,
Ctrl+Shift+Z or Ctrl+Y, Ctrl+A, Delete, Escape, and Ctrl with `0`, `9`, `+` or `-` for the viewport.

## Saving and loading

```csharp
string json = InkSerializer.Serialize(_canvas.CurrentDocument);
// ... store it anywhere: a database column, a file, a blob ...

InkDocument document = InkSerializer.Deserialize(json);
_canvas.LoadDocument(document);
```

For untrusted input use `TryDeserialize`, which reports a message instead of throwing.

## Exporting

```csharp
string svg = await _canvas.ExportSvgAsync();
byte[] png = await _canvas.ExportPngAsync(new PngExportOptions { Scale = 3f });

await _canvas.SaveSvgAsync("drawing.svg");
await _canvas.SavePngAsync("drawing.png");
```

## Working with the document directly

The document is a plain object. You can build, inspect and edit a drawing with no UI at all, which is what makes
the engine testable and usable on a server.

```csharp
var document = new InkDocument(new DocumentSettings { Width = 400, Height = 300 });

var points = StrokePoints.FromPositions([new Vector2(10, 10), new Vector2(200, 120)]);
document.AddElement(new Stroke(points, new PenBrush { Width = 6f }));

Console.WriteLine(document.ContentBounds);
File.WriteAllText("drawing.svg", SvgExporter.Export(document));
```

## Next

- [Architecture](architecture.md) explains how the pieces fit together.
- [Drawing, brushes and pressure](drawing-and-brushes.md) covers stroke quality and stylus support.
- [Hosting models](hosting-models.md) covers Blazor Server and Hybrid.
