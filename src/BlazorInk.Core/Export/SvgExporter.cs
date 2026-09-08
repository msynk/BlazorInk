using System.Globalization;
using System.Text;
using BlazorInk.Geometry;
using BlazorInk.Rendering;

namespace BlazorInk.Export;

/// <summary>What area of the document an export covers.</summary>
public enum ExportRegion : byte
{
    /// <summary>The page rectangle from the document settings, or the content bounds when the page is unbounded.</summary>
    Page = 0,
    /// <summary>The bounding box of the visible content, with padding.</summary>
    Content = 1,
    /// <summary>An explicit rectangle supplied by the caller.</summary>
    Custom = 2,
    /// <summary>The bounding box of the current selection.</summary>
    Selection = 3,
}

/// <summary>Options shared by the SVG and PNG exporters.</summary>
public record ExportOptions
{
    /// <summary>Which part of the document to export.</summary>
    public ExportRegion Region { get; init; } = ExportRegion.Page;

    /// <summary>The rectangle used when <see cref="Region"/> is <see cref="ExportRegion.Custom"/>.</summary>
    public Rect CustomRegion { get; init; }

    /// <summary>
    /// When set, only these elements are drawn (eraser strokes are always kept so erasing still applies). With
    /// <see cref="ExportRegion.Selection"/> the output is also cropped to them.
    /// </summary>
    public IReadOnlyCollection<Guid>? SelectedIds { get; init; }

    /// <summary>Padding added around the content in document units (used by content and selection regions).</summary>
    public float Padding { get; init; } = 8f;

    /// <summary>
    /// Background color. Null uses the document's background; use <see cref="InkColor.Transparent"/> to export
    /// with no background at all.
    /// </summary>
    public InkColor? Background { get; init; }

    /// <summary>Whether hidden layers are included.</summary>
    public bool IncludeHiddenLayers { get; init; }

    /// <summary>Computes the exported region for a document.</summary>
    public Rect ResolveRegion(InkDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        switch (Region)
        {
            case ExportRegion.Custom:
                return CustomRegion.Normalize();
            case ExportRegion.Content:
            {
                Rect bounds = document.ContentBounds;
                return bounds.IsEmpty ? document.Settings.PageRect : bounds.Inflate(Padding);
            }
            case ExportRegion.Selection:
            {
                Rect bounds = Rect.Empty;
                if (SelectedIds is not null)
                {
                    foreach (Guid id in SelectedIds) if (document.TryGetElement(id, out InkElement? e)) bounds = bounds.Union(e.Bounds);
                }
                return bounds.IsEmpty ? document.Settings.PageRect : bounds.Inflate(Padding);
            }
            default:
            {
                Rect page = document.Settings.PageRect;
                if (!page.IsEmpty) return page;
                Rect content = document.ContentBounds;
                return content.IsEmpty ? new Rect(0, 0, 1, 1) : content.Inflate(Padding);
            }
        }
    }
}

/// <summary>Options specific to SVG export.</summary>
public sealed record SvgExportOptions : ExportOptions
{
    /// <summary>Default options.</summary>
    public static readonly SvgExportOptions Default = new();

    /// <summary>Decimals kept for coordinates. Fewer decimals give smaller files.</summary>
    public int Decimals { get; init; } = 2;

    /// <summary>Whether to include the XML declaration (needed when writing a standalone <c>.svg</c> file).</summary>
    public bool IncludeXmlDeclaration { get; init; }

    /// <summary>Optional document title, written as <c>&lt;title&gt;</c> for accessibility.</summary>
    public string? Title { get; init; }

    /// <summary>Optional description, written as <c>&lt;desc&gt;</c> for accessibility.</summary>
    public string? Description { get; init; }

    /// <summary>
    /// Whether each element's accessible description is written as a <c>&lt;title&gt;</c> inside its group. Adds
    /// bytes but makes exported drawings navigable by screen readers.
    /// </summary>
    public bool IncludeElementDescriptions { get; init; }

    /// <summary>Prefix for generated element ids.</summary>
    public string IdPrefix { get; init; } = "bi";

    /// <summary>Scale factor applied through the <c>width</c>/<c>height</c> attributes; the view box is unchanged.</summary>
    public float Scale { get; init; } = 1f;
}

/// <summary>
/// Exports documents as SVG. Strokes are exported as filled outline paths, shapes as native SVG shapes, text as
/// <c>&lt;text&gt;</c> and images as <c>&lt;image&gt;</c>: the output stays fully vector and editable in any SVG tool.
/// </summary>
/// <remarks>
/// Eraser strokes become SVG masks that apply to exactly the content they covered, so an erased drawing exports
/// the way it looks on screen without flattening anything to pixels.
/// </remarks>
public static class SvgExporter
{
    /// <summary>Exports a document to an SVG string.</summary>
    public static string Export(InkDocument document, SvgExportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        // Every stroke point becomes about a dozen characters of path data; guessing low makes the builder
        // repeatedly copy itself, which is what dominates the cost of exporting a large drawing.
        var builder = new StringBuilder((int)Math.Clamp(1024 + document.ElementCount * 200L + document.TotalPointCount * 26L, 1024, 512L * 1024 * 1024));
        using (var writer = new StringWriter(builder, CultureInfo.InvariantCulture))
        {
            Export(document, writer, options);
        }
        return builder.ToString();
    }

    /// <summary>Exports a document as UTF-8 bytes, suitable for writing a <c>.svg</c> file.</summary>
    public static byte[] ExportToUtf8Bytes(InkDocument document, SvgExportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        using var stream = new MemoryStream((int)Math.Clamp(1024 + document.TotalPointCount * 26L, 1024, int.MaxValue));
        Export(document, stream, options);
        return stream.ToArray();
    }

    /// <summary>Exports a document into a stream as UTF-8.</summary>
    public static void Export(InkDocument document, Stream stream, SvgExportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false), 16384, leaveOpen: true);
        Export(document, writer, options ?? SvgExportOptions.Default with { IncludeXmlDeclaration = true });
    }

    /// <summary>Exports a document into a text writer.</summary>
    public static void Export(InkDocument document, TextWriter writer, SvgExportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(writer);
        options ??= SvgExportOptions.Default;

        Rect region = options.ResolveRegion(document);
        if (region.IsEmpty) region = new Rect(region.X, region.Y, MathF.Max(region.Width, 1), MathF.Max(region.Height, 1));
        int decimals = Math.Clamp(options.Decimals, 0, 6);

        if (options.IncludeXmlDeclaration) writer.Write("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");

        float scale = options.Scale > 0 && float.IsFinite(options.Scale) ? options.Scale : 1f;
        writer.Write("<svg xmlns=\"http://www.w3.org/2000/svg\" version=\"1.1\" width=\"");
        WriteNumber(writer, region.Width * scale, decimals);
        writer.Write("\" height=\"");
        WriteNumber(writer, region.Height * scale, decimals);
        writer.Write("\" viewBox=\"");
        WriteNumber(writer, region.X, decimals);
        writer.Write(' ');
        WriteNumber(writer, region.Y, decimals);
        writer.Write(' ');
        WriteNumber(writer, region.Width, decimals);
        writer.Write(' ');
        WriteNumber(writer, region.Height, decimals);
        writer.Write("\">");

        if (options.Title is { Length: > 0 } title)
        {
            writer.Write("<title>");
            SvgText.WriteEscaped(writer, title);
            writer.Write("</title>");
        }
        if (options.Description is { Length: > 0 } description)
        {
            writer.Write("<desc>");
            SvgText.WriteEscaped(writer, description);
            writer.Write("</desc>");
        }

        InkColor background = options.Background ?? document.Settings.Background;
        if (!background.IsTransparent)
        {
            writer.Write("<rect x=\"");
            WriteNumber(writer, region.X, decimals);
            writer.Write("\" y=\"");
            WriteNumber(writer, region.Y, decimals);
            writer.Write("\" width=\"");
            WriteNumber(writer, region.Width, decimals);
            writer.Write("\" height=\"");
            WriteNumber(writer, region.Height, decimals);
            writer.Write("\" fill=\"");
            writer.Write((background with { A = 255 }).ToCss());
            writer.Write('"');
            if (background.A < 255)
            {
                writer.Write(" fill-opacity=\"");
                WriteNumber(writer, background.Alpha, 3);
                writer.Write('"');
            }
            writer.Write("/>");
        }

        var context = new SvgRenderContext(writer, decimals) { IdPrefix = options.IdPrefix };
        var state = new ExportState(options, region, decimals);
        foreach (Layer layer in document.Layers)
        {
            if (!layer.IsVisible && !options.IncludeHiddenLayers) continue;
            WriteLayer(writer, context, layer, state);
        }

        writer.Write("</svg>");
    }

    private sealed class ExportState(SvgExportOptions options, Rect region, int decimals)
    {
        public SvgExportOptions Options => options;
        public Rect Region => region;
        public int Decimals => decimals;
        public int MaskCounter;
    }

    private static void WriteLayer(TextWriter writer, SvgRenderContext context, Layer layer, ExportState state)
    {
        List<InkElement> elements = FilterElements(layer, state.Options);
        if (elements.Count == 0) return;

        writer.Write("<g");
        if (layer.Name is { Length: > 0 })
        {
            writer.Write(" data-layer=\"");
            SvgText.WriteEscaped(writer, layer.Name);
            writer.Write('"');
        }
        if (layer.Opacity < 1f)
        {
            writer.Write(" opacity=\"");
            WriteNumber(writer, layer.Opacity, 3);
            writer.Write('"');
        }
        writer.Write(" style=\"isolation:isolate\">");

        WriteRun(writer, context, elements, elements.Count, state);

        writer.Write("</g>");
    }

    /// <summary>
    /// Writes the first <paramref name="count"/> elements of a layer. Eraser strokes become masks that apply to
    /// everything drawn before them, which is exactly the sequential destination-out semantics used on canvas.
    /// </summary>
    private static void WriteRun(TextWriter writer, SvgRenderContext context, List<InkElement> elements, int count, ExportState state)
    {
        int lastEraser = -1;
        for (int i = count - 1; i >= 0; i--)
        {
            if (elements[i] is Stroke { IsEraser: true }) { lastEraser = i; break; }
        }

        if (lastEraser < 0)
        {
            for (int i = 0; i < count; i++) WriteElement(writer, context, elements[i], state);
            return;
        }

        var eraser = (Stroke)elements[lastEraser];
        string maskId = state.Options.IdPrefix + "-mask-" + state.MaskCounter++.ToString(CultureInfo.InvariantCulture);

        writer.Write("<mask id=\"");
        writer.Write(maskId);
        writer.Write("\" maskUnits=\"userSpaceOnUse\" x=\"");
        WriteNumber(writer, state.Region.X, state.Decimals);
        writer.Write("\" y=\"");
        WriteNumber(writer, state.Region.Y, state.Decimals);
        writer.Write("\" width=\"");
        WriteNumber(writer, state.Region.Width, state.Decimals);
        writer.Write("\" height=\"");
        WriteNumber(writer, state.Region.Height, state.Decimals);
        writer.Write("\"><rect x=\"");
        WriteNumber(writer, state.Region.X, state.Decimals);
        writer.Write("\" y=\"");
        WriteNumber(writer, state.Region.Y, state.Decimals);
        writer.Write("\" width=\"");
        WriteNumber(writer, state.Region.Width, state.Decimals);
        writer.Write("\" height=\"");
        WriteNumber(writer, state.Region.Height, state.Decimals);
        writer.Write("\" fill=\"#fff\"/>");

        // Black where the eraser passed: luminance 0 makes the masked content transparent there.
        var maskContext = new SvgRenderContext(writer, state.Decimals) { IdPrefix = state.Options.IdPrefix + "-m" + state.MaskCounter.ToString(CultureInfo.InvariantCulture) };
        maskContext.Save();
        if (!eraser.Transform.IsIdentity) maskContext.Transform(eraser.Transform);
        StrokeGeometryToMask(maskContext, eraser);
        maskContext.Restore();
        writer.Write("</mask>");

        writer.Write("<g mask=\"url(#");
        writer.Write(maskId);
        writer.Write(")\">");
        WriteRun(writer, context, elements, lastEraser, state);
        writer.Write("</g>");

        for (int i = lastEraser + 1; i < count; i++) WriteElement(writer, context, elements[i], state);
    }

    private static void StrokeGeometryToMask(SvgRenderContext context, Stroke eraser)
    {
        Brushes.StrokeGeometry geometry = eraser.Geometry;
        if (geometry.IsEmpty) return;
        var black = new InkColor(0, 0, 0);
        if (geometry.Mode == Brushes.StrokeGeometryMode.Fill) context.FillPath(geometry.Path, new FillPaint(black));
        else context.StrokePath(geometry.Path, new StrokePaint(black, geometry.LineWidth, 1f, BlendMode.Normal, geometry.Cap, geometry.Join));
    }

    private static void WriteElement(TextWriter writer, SvgRenderContext context, InkElement element, ExportState state)
    {
        if (element is Stroke { IsEraser: true }) return;
        bool describe = state.Options.IncludeElementDescriptions;
        if (describe)
        {
            writer.Write("<g role=\"img\"><title>");
            SvgText.WriteEscaped(writer, element.GetAccessibleDescription());
            writer.Write("</title>");
        }
        element.Draw(context);
        if (describe) writer.Write("</g>");
    }

    private static List<InkElement> FilterElements(Layer layer, SvgExportOptions options)
    {
        var result = new List<InkElement>(layer.Count);
        HashSet<Guid>? selection = options.SelectedIds is { Count: > 0 } ? [.. options.SelectedIds] : null;
        foreach (InkElement element in layer.Elements)
        {
            if (selection is not null && !selection.Contains(element.Id) && element is not Stroke { IsEraser: true }) continue;
            result.Add(element);
        }
        // Trailing erasers with nothing left to erase only bloat the output.
        while (result.Count > 0 && result[0] is Stroke { IsEraser: true }) result.RemoveAt(0);
        return result;
    }

    private static void WriteNumber(TextWriter writer, float value, int decimals)
    {
        if (!float.IsFinite(value)) value = 0;
        Span<char> buffer = stackalloc char[32];
        value.TryFormat(buffer, out int written, "F" + decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        ReadOnlySpan<char> s = buffer[..written];
        if (s.IndexOf('.') >= 0)
        {
            s = s.TrimEnd('0');
            if (s.Length > 0 && s[^1] == '.') s = s[..^1];
        }
        if (s.IsEmpty || s.SequenceEqual("-0")) s = "0";
        writer.Write(s);
    }
}
