# SVG and PNG export

PNG export walks the same `DocumentRenderer` path as the screen, and SVG export walks the document itself, but both draw every element through the same `IRenderContext`, so an exported file matches what was drawn.

## SVG

```csharp
string svg = SvgExporter.Export(document);
byte[] bytes = SvgExporter.ExportToUtf8Bytes(document);   // for writing a .svg file
SvgExporter.Export(document, stream);                     // streaming, for large drawings
```

From the component:

```csharp
string svg = await canvas.ExportSvgAsync();
await canvas.SaveSvgAsync("drawing.svg");
```

The output is genuinely vector. Strokes become filled outline paths, shapes become native SVG geometry, text
becomes `text` elements, and images become `image` elements. Nothing is rasterized.

Eraser strokes become SVG masks applied to exactly the content they covered, so an erased drawing exports the way
it looks on screen. Layer opacity becomes group opacity, and highlighter strokes carry `mix-blend-mode: multiply`.

### Options

```csharp
var options = new SvgExportOptions
{
    Region = ExportRegion.Content,        // Page, Content, Selection or Custom
    Padding = 12f,
    Background = InkColor.Transparent,    // null uses the document background
    Decimals = 2,                         // fewer decimals gives smaller files
    IncludeXmlDeclaration = true,         // for a standalone file
    Title = "Signature",                  // written as a title element, for accessibility
    IncludeElementDescriptions = true,    // per-element titles, navigable by screen readers
    SelectedIds = selection.Ids.ToArray(),
};
```

### Safety

The writer XML-escapes everything that reaches the output, including text content, descriptions and font family
names. It never emits script, event handler attributes or foreign objects, and colours are emitted as hex, which
cannot carry a payload. The export tests assert this against hostile input.

Images are the one external reference the output can contain: an `ImageElement` whose source is an `https:` URL
is written as an `image` element pointing at it. Embed images as data URIs, or refuse remote sources on load with
`AllowRemoteImages = false`, if the exported file must be self-contained.

If you import SVG from elsewhere, sanitize it yourself. BlazorInk does not import SVG, and injecting arbitrary
SVG into a page is a scripting risk regardless of this library.

### Large drawings

`Export(document)` returns a string, which means the whole file exists twice in memory while it is built. For a
large document prefer the streaming overloads, which write straight to a stream:

```csharp
using var file = File.Create("drawing.svg");
SvgExporter.Export(document, file);
```

## PNG

Two implementations exist, deliberately.

### In the browser

```csharp
byte[] png = await canvas.ExportPngAsync(new PngExportOptions { Scale = 3f });
await canvas.SavePngAsync("drawing.png");
```

Rendering happens on an offscreen canvas, so real fonts and every image format the browser can decode are
included. This is the right choice for anything a user downloads.

### On a server, or in tests

```csharp
byte[] png = PngExporter.Export(document, new PngExportOptions { Scale = 2f });
```

`PngExporter` uses the built-in anti-aliased rasterizer and PNG encoder. It has no native dependencies, runs
anywhere .NET runs, and produces byte-identical output for identical input, which is what makes visual regression
testing possible.

It ships no font engine and decodes only PNG images. Rather than silently producing an incomplete picture, the
result reports what it could not draw:

```csharp
PngExportResult result = PngExporter.ExportWithDetails(document);
if (!result.IsComplete)
{
    // result.SkippedTextElements, result.SkippedImages
}
```

To render text on a server, supply a font engine:

```csharp
var options = new PngExportOptions { TextRasterizer = new MySkiaTextRasterizer() };
```

`ITextRasterizer` has two methods: draw a string into a surface, and measure one.

### Options

```csharp
var options = new PngExportOptions
{
    Region = ExportRegion.Content,
    Padding = 8f,
    Scale = 3f,                       // output pixels per document unit
    Background = InkColor.Transparent,
    PixelsPerMeter = 11811,           // about 300 DPI, written into the file
    MaxPixels = 80_000_000,           // export fails rather than allocating more
    Compression = CompressionLevel.SmallestSize,
};
```

`Scale` is independent of the display the drawing was made on. A signature captured on a phone and one captured
on a desktop export at exactly the same resolution.

## Cropping

| Region | Meaning |
| --- | --- |
| `Page` | The document's page rectangle. |
| `Content` | The bounding box of the visible ink, plus `Padding`. |
| `Selection` | The bounding box of `SelectedIds`, plus `Padding`. |
| `Custom` | `CustomRegion`. |

Setting `SelectedIds` limits which elements are drawn whatever the region, so a selection can be exported in place
on the full page.
