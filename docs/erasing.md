# Erasing

Three erasing strategies exist because they are genuinely different, not three names for one behaviour. Pick the
one that matches the application.

```csharp
var eraser = new EraserTool
{
    Mode = EraserMode.Stroke,   // Stroke, Segment or Pixel
    Radius = 12f,               // document units
};
```

## Stroke eraser

`EraserMode.Stroke` deletes any stroke the eraser touches, whole.

This is what note-taking applications do, and what most users expect: a quick swipe removes a letter. It is cheap,
exactly reversible, and never increases the element count. It cannot remove part of a long stroke, which is
occasionally surprising if someone drew a whole line without lifting the pen.

## Segment eraser

`EraserMode.Segment` removes only the touched portion, splitting the stroke into fragments.

This feels like a real eraser and keeps the result fully vector. The first fragment keeps the original element id,
so undo and collaboration see an edit rather than a delete followed by two inserts. The cost is that erasing
through the middle of a stroke increases the element count.

```csharp
var fragments = new List<Stroke>();
bool changed = StrokeEditor.EraseSegments(stroke, eraserPath, radius, fragments);
```

`StrokeEditor` is public, so an application can implement its own erasing behaviour on the same geometry.

## Pixel eraser

`EraserMode.Pixel` draws an eraser stroke that masks out everything below it within the same layer.

Nothing is deleted. The erase is itself an object in the document: it can be undone, re-ordered, or removed later
to bring the ink back. It is the only mode that can erase part of an image, and it is the only mode that produces
the soft, partial erasure people expect from a raster tool. It exports as an SVG mask, so the result is preserved
in a vector file.

The trade-off is that an erased document keeps the erased ink, which matters if the point of erasing was to
remove information rather than to hide it.

## Which to choose

| Situation | Mode |
| --- | --- |
| Note taking, handwriting | `Stroke` |
| Drawing and sketching | `Segment` |
| Marking up an image or a photo | `Pixel` |
| The erased content must actually be gone | `Stroke` or `Segment` |

## The pen's eraser end

A stylus with an inverted eraser end reports pointer button 32. `DrawTool` honours it by default, switching to a
pixel eraser for the duration of that stroke:

```csharp
var tool = new DrawTool { HonorPenEraserButton = false };   // to opt out
```

## Erasing and undo

A whole erase gesture is one undo step, however many strokes it touched. In stroke and segment mode the tool
opens a history group on pointer down and closes it on pointer up, and erasing nothing creates no entry at all.
Pixel mode needs no group: the gesture produces a single eraser stroke, which is already one step.

## Layers

Erasing only affects unlocked, visible layers. A pixel eraser cannot reach past its own layer, which is why the
background and any locked image layer are safe from it.
