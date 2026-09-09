# Mobile, touch and stylus

## Scrolling versus drawing

The single most important decision on a touch device is whether a finger drag draws or scrolls the page. There is
no setting that gets both, so the component asks you which one this canvas is.

```razor
<InkCanvas EnableTouchGestures="false" />   <!-- default: a finger scrolls the page -->
<InkCanvas EnableTouchGestures="true" />    <!-- a finger draws, and pinch zooms the canvas -->
```

With gestures off the canvas sets `touch-action: pan-x pan-y`, so a signature field embedded in a long form does
not trap the page. A stylus still draws, because a pen pointer is not a touch pointer.

With gestures on the canvas sets `touch-action: none` and owns every touch. Use this for a full-screen whiteboard.

## Palm rejection

While a pen is in contact, touch contacts are ignored. A hand usually lands before the nib does, so a touch
gesture that is already in progress when the pen arrives is cancelled rather than left to commit a mark:

```razor
<InkCanvas RejectTouchWhilePen="true" />   <!-- default -->
```

This is what stops a hand resting on a tablet from drawing while you write. It is a heuristic, not a hardware
feature: the browser exposes pen and touch as separate pointer types, and the library uses that. Turn it off for a
finger-first surface where someone might legitimately use both at once.

## Pinch zoom and panning

With `EnableTouchGestures="true"` a second finger takes over the gesture whatever the active tool is: the
in-flight tool gesture is cancelled and both contacts drive pan and pinch zoom until the last finger lifts. One
finger still draws. `PanTool` does the same thing when it is the active tool:

```csharp
var pan = new PanTool { MinZoom = 0.1f, MaxZoom = 32f, EnablePinchZoom = true };
```

Panning and zooming change only the viewport, so they produce no operations, no history entries and no change to
the vector data.

## High-DPI displays

The canvas backing store is sized to the device pixel ratio, so ink is sharp on a phone or a Retina display. The
ratio is watched at runtime: moving the window to a different display, or changing browser zoom, resizes the
backing store and redraws.

Nothing about a drawing depends on the display it was made on. A signature captured on a 3x phone and one captured
on a 1x monitor are the same vector document, and export at the same resolution.

## Pointer capture

The browser layer captures the pointer on contact, so a gesture continues when the finger or pen leaves the canvas
and completes properly when it is lifted outside. A cancelled gesture, for example when the system takes over,
arrives as a cancel and rolls the interaction back rather than leaving a half-drawn stroke.

## Coalesced samples

A stylus samples far faster than the display refreshes. The browser buffers those samples and hands them over with
the frame's pointer event; the library reads them all, so fast handwriting keeps its shape instead of turning into
straight lines between frame positions.

`Capabilities.CoalescedEvents` reports whether the browser supports this.

## Orientation and resizing

The canvas observes its own element. Rotating a phone or resizing a split view resizes the backing store and
redraws at the new size. The document is unaffected: it has its own coordinate space.

## Low-latency canvas

The canvas is requested with the `desynchronized` hint, which shortens the path from drawing command to pixels on
browsers that support it. `Capabilities.LowLatencyCanvas` reports whether the browser honoured it.

## Practical advice

- A signature field: fixed height, `EnableTouchGestures="false"`, a clear button, and a validation rule on ink
  length rather than on whether anything was drawn at all.
- A note-taking surface: `EnableTouchGestures="true"`, palm rejection on, a pen brush with low smoothing and tight
  spacing, and a stroke or segment eraser.
- A form annotation over a document: gestures off so the page still scrolls, and a highlight tool rather than
  freehand for text.
- Test with a real stylus. Mouse input exercises none of the pressure, tilt or palm-rejection paths.

## Hit targets

Selection handles are drawn small, but what they respond to is not. A contact reported as touch grows the handle
and element hit areas by `SelectTool.TouchTargetScale`, which is 2.8 by default, so a fingertip lands on a target
close to the 24 by 24 pixel minimum WCAG 2.5.8 asks for without the handles looking clumsy under a mouse.
