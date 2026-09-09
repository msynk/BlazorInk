# Accessibility

## What is honestly possible

A canvas is pixels. A screen reader cannot describe handwriting, and no amount of ARIA changes that. Claiming
otherwise would be worse than useless, because it would stop developers from doing the work that actually helps.

What the library does provide:

- A properly labelled, focusable, keyboard-operable canvas.
- A live text summary of what the drawing contains, built from the descriptions attached to elements.
- Announcements of changes through a polite live region.
- Real HTML controls for everything except the drawing itself.
- An export path that carries the same descriptions into the SVG.

What your application must still do: give elements meaningful descriptions, and provide a non-drawing route to
any information that only exists as a drawing.

## The canvas element

```razor
<InkCanvas AriaLabel="Signature for the tenancy agreement" />
```

Rendered as:

```html
<div role="application" aria-roledescription="drawing canvas"
     aria-label="Signature for the tenancy agreement"
     aria-describedby="bi-...-desc" tabindex="0">
  <canvas aria-hidden="true"></canvas>   <!-- document -->
  <canvas aria-hidden="true"></canvas>   <!-- overlay -->
  <div aria-hidden="true"></div>         <!-- input surface -->
  <div class="blazorink-visually-hidden">Drawing with 12 elements on 2 layers. Signature of Jane Doe.</div>
  <div role="status" aria-live="polite">Erased 3 strokes.</div>
</div>
```

The canvases are hidden from assistive technology because their content is unreadable. The description element
beside them is not, and it updates as the document changes.

## Describing content

```csharp
document.ReplaceElement(stroke with { Description = "Signature of Jane Doe" });
document.AddElement(new LineShape(a, b) { EndArrow = true, Description = "Arrow pointing at the total" });
```

Elements without a description fall back to something structural, such as "Stroke, 142 points, pen brush, color
#111827", and text elements report their text. Exported SVG carries the same strings as `title` elements when
`IncludeElementDescriptions` is set.

## Keyboard

The canvas takes focus when it is drawn on, so shortcuts are reachable for someone who just used a mouse or pen.

| Key | Action |
| --- | --- |
| Ctrl+Z | Undo |
| Ctrl+Shift+Z, Ctrl+Y | Redo |
| Ctrl+A | Select all |
| Enter | Select the first element, which is how you start navigating without a pointer |
| Tab, Shift+Tab | Step to the next or previous element, announcing each one |
| Delete, Backspace | Delete the selection |
| Escape | Cancel the gesture, then clear the selection |
| Arrow keys | Nudge the selection; Shift for larger steps |
| Ctrl+arrow keys | Resize the selection; Shift for larger steps |
| R, Shift+R | Rotate the selection by 15 degrees |
| Enter, with the text tool active | Open a text box in the middle of the view |
| Ctrl+0 | Reset zoom |
| Ctrl+9 | Zoom to fit |
| Ctrl+plus, Ctrl+minus | Zoom |

Every element can therefore be reached, moved, resized, rotated and deleted without a pointer, and each step
announces what is now selected. Text is reachable too: the text tool opens its real HTML editor from the keyboard,
so dictation, an input method and a screen reader all work on it.

The canvas is not a keyboard trap. Tab belongs to it only while an element is selected: pressing Escape clears
the selection, and the next Tab moves focus out of the canvas as usual. Delete, Backspace and the arrow keys are
likewise left to the page when nothing is selected, so a keyboard user can still scroll past a focused canvas.
Keys pressed inside `ChildContent`, or in any input, button or link within the canvas, are never intercepted.

Turn them off with `EnableKeyboardShortcuts="false"` if the surrounding application owns those keys. That
genuinely releases the keys: the browser layer stops preventing their default behaviour as well.

Keyboard input travels the same path as pointer input, so it does not depend on a framework event and does not
re-render the component per keystroke.

## Toolbars

Build them from real HTML. The demo's toolbar is buttons and inputs with `aria-pressed`, `aria-label` and
`role="toolbar"`, and it is fully keyboard operable without the canvas being involved at all. Never put controls
inside the canvas.

## Live announcements

```csharp
canvas.Announce("Signature captured.");
```

Tools already announce meaningful events: how many elements a marquee selected, how many strokes an erase
removed, that an image was added. Announcements are coalesced to at most one per frame.

## Colour, contrast and motion

- The library never relies on colour alone to convey state; selection is shown with an outline and handles.
- Under forced colours the container sets `forced-color-adjust: none`, because a drawing whose colours are
  replaced by the system palette is no longer the user's drawing. Style the surrounding UI to respect the
  forced-colours palette.
- There are no animations, so there is nothing to disable for reduced motion.

## What WCAG asks of a drawing surface

Freehand drawing is a path-based gesture, and 2.5.1 Pointer Gestures exempts input where the path itself is
essential: you cannot draw a signature with arrow keys. Everything *around* the drawing is not exempt, which is
why tools, colours, undo, layer controls and export must be ordinary controls. That is the split the library
takes: the ink is a gesture, everything else is a button.

Under 1.1.1 Non-text Content, a drawing needs a text alternative. `Description` is how you supply one; the
canvas surfaces it, and so does the exported SVG.
