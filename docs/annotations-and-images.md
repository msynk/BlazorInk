# Annotations, text and images

## Shapes

```csharp
document.AddElement(new RectangleShape(new Rect(20, 20, 200, 120))
{
    Style = new ShapeStyle { StrokeColor = InkColor.Parse("#dc2626"), StrokeWidth = 3f },
    CornerRadius = 6f,
});

document.AddElement(new EllipseShape(new Rect(60, 60, 120, 120)));

document.AddElement(new LineShape(new Vector2(20, 20), new Vector2(200, 90))
{
    EndArrow = true,
    Style = new ShapeStyle { StrokeColor = InkColor.Parse("#2563eb"), StrokeWidth = 2f },
});
```

`ShapeStyle` carries stroke colour and width, an optional fill, opacity, a dash pattern and a blend mode.

Interactively, `ShapeTool` draws them:

```csharp
var tool = new ShapeTool(ShapeKind.Arrow);   // Rectangle, Ellipse, Line, Arrow, Highlight
```

Holding Shift while dragging constrains to a square, a circle or a 15-degree angle. The modifier travels with the
pointer samples, so it works with a pen and a touch keyboard as well as a mouse.

## Highlights

A highlight is one or more rectangles drawn with multiply blending, so whatever is underneath stays legible. It is
the right element for marking text, because a text line is a rectangle.

```csharp
document.AddElement(new HighlightElement(lineRects)
{
    Color = InkColor.Parse("#fde047"),
    Opacity = 0.5f,
    Description = "Highlighted: payment terms",
});
```

## Callouts

A callout is a text box with a pointer to an anchor. This is the shape a review comment takes.

```csharp
document.AddElement(new CalloutElement(
    box: new Rect(320, 100, 200, 70),
    anchor: new Vector2(280, 130),
    text: "Please confirm this clause.")
{
    Style = new ShapeStyle { FillColor = InkColor.Parse("#fff9c4"), StrokeColor = InkColor.Parse("#785a00") },
});
```

The pointer starts at whichever edge of the box faces the anchor, so it stays sensible when the callout is moved.

## Text

Text is laid out by the browser, so a text element records an explicit size. `TextTool` creates one and asks the
host to show a real HTML input over it, because an accessible, IME-capable text editor cannot be built on a canvas.

```razor
<InkCanvas Tool="_textTool" />

@code {
    private TextTool _textTool = new();

    protected override void OnInitialized()
    {
        _textTool.EditRequested += request => ShowEditor(request.Element, request.ViewRect);
        _textTool.EditFinished += HideEditor;
    }

    private void Commit(string text) => _textTool.CommitEdit(_context, text, measuredSize);
}
```

Committing empty text removes the element, so an abandoned text box never leaves an invisible object behind.

Created directly:

```csharp
document.AddElement(new TextElement(new Vector2(40, 40), "Approved")
{
    Style = TextStyle.Default with { FontSize = 20f, Bold = true, Color = InkColor.Parse("#15803d") },
    Background = InkColor.Parse("#dcfce7"),
    Padding = 6f,
});
```

When no measured size is supplied, the element estimates one from the font size. The estimate is only used for
bounds; the browser still lays the text out for real when drawing.

## Images

```csharp
var source = ImageSource.FromBytes(pngBytes, "image/png", width: 640, height: 400);
ImageElement? image = await canvas.AddImageAsync(source, position: null, maxWidth: 800f);
// null when the canvas is read-only; it is already in the document otherwise
```

`AddImageAsync` asks the browser for the natural size when it is unknown, scales the image to `maxWidth`, and
centres it in the view when no position is given.

Directly, without a canvas:

```csharp
document.AddElement(new ImageElement(source, new Rect(0, 0, 640, 400))
{
    Crop = new Rect(100, 50, 400, 300),   // in source pixels
    Opacity = 0.9f,
    Description = "Screenshot of the checkout page",
});
```

Position, size, rotation (through `Transform`) and cropping are all vector properties. Moving or resizing an image
never re-encodes pixels, and cropping is non-destructive: it can be changed or removed later.

### A pattern that works

Put the image on its own layer and lock it:

```csharp
Guid imageLayer = document.Layers[0].Id;
document.AddElement(new ImageElement(source, new Rect(0, 0, 640, 400)), imageLayer);
document.UpdateLayer(imageLayer, p => p with { IsLocked = true, Name = "Image" });

Guid annotations = document.AddLayer("Annotations").Id;
```

Annotating then cannot move the picture by accident, and hiding the image layer shows the annotations alone.

### Sources and safety

`ImageSource` accepts `data:`, `https:`, `http:` and `blob:` URIs. Data URIs must carry a raster media type; SVG
is rejected unless explicitly allowed, because an SVG image is a document that can reference other resources.

```csharp
if (!ImageSource.TryCreate(uri, width, height, allowSvg: false, out ImageSource? source))
{
    // refuse it
}
```

Embedded images are stored in the document as data URIs, which keeps it self-contained. For large assets, keep the
bytes elsewhere and reference them by URL, or save with `IncludeEmbeddedImages = false` and re-attach the payload
on load.

## What the framework deliberately is not

There is no filter stack, no colour adjustment, no compositing pipeline for pixels. Images are placed, cropped and
annotated. Turning the library into an image editor would cost every consumer size and complexity for something
few of them want.
