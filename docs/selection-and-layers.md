# Selection, transforms and layers

## The selection tool

```csharp
var tool = new SelectTool();
```

Click to select, drag inside the selection to move it, drag a corner or edge handle to resize, drag the circle
above it to rotate. Dragging on empty space draws a marquee. Right-click adds to the selection.

Keyboard, when the canvas has focus:

| Key | Action |
| --- | --- |
| Arrow keys | Nudge by one document unit |
| Shift + arrows | Nudge by ten |
| Delete or Backspace | Delete the selection |
| Ctrl+A | Select everything on unlocked, visible layers |
| Escape | Cancel the drag, then clear the selection |

## Why manipulation is cheap

Moving, resizing and rotating change only each element's `Transform`. Stroke points, text and image pixels are
never rewritten. That has three consequences worth knowing:

- A drag of 10,000 selected strokes costs one matrix per stroke, not a rewrite of every point.
- Undo restores the exact original transform, with no accumulated floating-point drift.
- A stroke's width does not change when you scale it, unless you flatten it deliberately with `Stroke.Flatten()`.

The whole gesture is a single undo step.

## Working with the selection in code

```csharp
Selection selection = canvas.Selection;

selection.Set(elementId);
selection.Add(otherId);
selection.Toggle(thirdId);
selection.SelectAll();
selection.Clear();

Rect bounds = selection.Bounds;
foreach (InkElement element in selection.Elements) { }
```

The selection drops elements automatically when they are removed from the document, including by a collaborator.

## Querying the document

```csharp
InkElement? hit = DocumentQuery.HitTest(document, point, tolerance: 4f);
List<InkElement> all = DocumentQuery.HitTestAll(document, point);
List<InkElement> inside = DocumentQuery.FindInRect(document, rect, RectSelectionMode.Contain);
```

Hit testing is geometric, not bounding-box based: a diagonal stroke is not selected by a marquee that only
overlaps its bounding box.

## Layers

Layers are part of the document model, not separate HTML canvases. That means they survive saving, take part in
undo, and are honoured identically by the screen, SVG export and PNG export.

```csharp
Layer annotations = document.AddLayer("Annotations");

document.UpdateLayer(annotations.Id, p => p with { IsVisible = false });
document.UpdateLayer(annotations.Id, p => p with { IsLocked = true });
document.UpdateLayer(annotations.Id, p => p with { Opacity = 0.5f });
document.UpdateLayer(annotations.Id, p => p with { Name = "Review notes" });

document.ReorderLayer(annotations.Id, index: 0);   // 0 is the bottom
document.RemoveLayer(annotations.Id);              // a document always keeps at least one layer
```

Tell the canvas where new elements go:

```razor
<InkCanvas ActiveLayerId="_annotationLayer" />
```

A locked or hidden layer rejects drawing, erasing and selection. That is how the image annotation sample keeps a
picture from being moved by accident: the image sits on its own locked layer.

## A typical structure

```text
Foreground     stickers, watermarks
Annotations    arrows, highlights, callouts
Drawing        freehand ink
Images         the photo or screenshot being marked up  (locked)
Background     paper colour, grid
```
