using BlazorInk.Geometry;

namespace BlazorInk.Rendering;

/// <summary>Controls which parts of a document are drawn.</summary>
public sealed record RenderOptions
{
    /// <summary>Draw everything on visible layers.</summary>
    public static readonly RenderOptions Default = new();

    /// <summary>Only draw elements whose bounds intersect this rectangle. Empty means no culling.</summary>
    public Rect ClipBounds { get; init; } = Rect.Empty;

    /// <summary>Whether hidden layers are drawn.</summary>
    public bool IncludeHiddenLayers { get; init; }

    /// <summary>When set, only these elements are drawn (eraser strokes are always kept so erasing still applies).</summary>
    public IReadOnlyCollection<Guid>? ElementFilter { get; init; }

    /// <summary>When set, this element is skipped. Used to hide an element that a tool is drawing on the overlay instead.</summary>
    public Guid? SuppressedElement { get; init; }
}

/// <summary>
/// Draws a document into any <see cref="IRenderContext"/>. Canvas rendering, SVG export and PNG export all go
/// through this one path, which is what keeps the screen and the exported file identical.
/// </summary>
public static class DocumentRenderer
{
    /// <summary>Draws the document's layers and their elements.</summary>
    /// <param name="document">The document.</param>
    /// <param name="context">Where to draw.</param>
    /// <param name="options">What to include.</param>
    /// <returns>The number of elements drawn.</returns>
    public static int Render(InkDocument document, IRenderContext context, RenderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(context);
        options ??= RenderOptions.Default;
        options = WithIndexedFilter(options);
        int drawn = 0;
        foreach (Layer layer in document.Layers)
        {
            if (!layer.IsVisible && !options.IncludeHiddenLayers) continue;
            drawn += RenderLayer(layer, context, options);
        }
        return drawn;
    }

    /// <summary>Draws a single layer, isolating it so eraser strokes cannot erase content on the layers below.</summary>
    public static int RenderLayer(Layer layer, IRenderContext context, RenderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(layer);
        ArgumentNullException.ThrowIfNull(context);
        options = WithIndexedFilter(options ?? RenderOptions.Default);
        if (layer.Count == 0) return 0;

        bool isolate = layer.Opacity < 1f || LayerHasEraser(layer);
        if (isolate) context.PushGroup(layer.Opacity, BlendMode.Normal);

        int drawn = 0;
        Rect clip = options.ClipBounds;
        bool cull = !clip.IsEmpty;
        IReadOnlyCollection<Guid>? filter = options.ElementFilter;

        foreach (InkElement element in layer.Elements)
        {
            if (options.SuppressedElement == element.Id) continue;
            bool isEraser = element is Stroke { IsEraser: true };
            if (filter is not null && !isEraser && !filter.Contains(element.Id)) continue;
            if (cull && !element.Bounds.Intersects(clip)) continue;
            element.Draw(context);
            drawn++;
        }

        if (isolate) context.PopGroup();
        return drawn;
    }

    /// <summary>
    /// Ensures the element filter can be probed in constant time. The filter arrives as a collection because
    /// that is what callers have (the selection hands out an array), and probing an array once per element turns
    /// a redraw into a quadratic scan: exporting a thousand selected elements out of ten thousand would compare
    /// ids ten million times.
    /// </summary>
    private static RenderOptions WithIndexedFilter(RenderOptions options)
    {
        IReadOnlyCollection<Guid>? filter = options.ElementFilter;
        // A handful of ids is faster to scan than to hash, and the common sets are already hash based.
        if (filter is null || filter.Count <= 8 || filter is HashSet<Guid> or IReadOnlySet<Guid>) return options;
        return options with { ElementFilter = new HashSet<Guid>(filter) };
    }

    private static bool LayerHasEraser(Layer layer)
    {
        foreach (InkElement element in layer.Elements)
        {
            if (element is Stroke { IsEraser: true }) return true;
        }
        return false;
    }

    /// <summary>Draws the document background over a region.</summary>
    public static void RenderBackground(IRenderContext context, InkColor color, Rect region)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (color.IsTransparent || region.IsEmpty) return;
        context.FillPath(new PathBuilder(6).AddRect(region).ToGeometry(), new FillPaint(color));
    }
}
