using System.Numerics;
using BlazorInk.Geometry;
using BlazorInk.Rendering;

namespace BlazorInk;

/// <summary>
/// A raster image placed in the document. Position, size, rotation (via <see cref="InkElement.Transform"/>) and an
/// optional crop are vector properties, so images can be annotated, moved and exported without re-encoding pixels.
/// </summary>
public sealed record ImageElement : InkElement
{
    /// <summary>Creates an image element.</summary>
    /// <param name="source">Image source.</param>
    /// <param name="rect">Destination rectangle in local coordinates.</param>
    public ImageElement(ImageSource source, Rect rect)
    {
        ArgumentNullException.ThrowIfNull(source);
        Source = source;
        Rect = rect.Normalize();
    }

    /// <summary>The image.</summary>
    public ImageSource Source { get; init; }

    /// <summary>Destination rectangle in local coordinates.</summary>
    public Rect Rect { get; init; }

    /// <summary>Optional crop rectangle in source pixels. Null draws the whole image.</summary>
    public Rect? Crop { get; init; }

    /// <summary>Opacity 0..1.</summary>
    public float Opacity { get; init; } = 1f;

    /// <inheritdoc />
    public override string TypeName => "Image";

    /// <inheritdoc />
    public override Rect LocalBounds => Rect;

    /// <summary>Creates an image element at its natural size (or a fallback size when unknown) positioned at <paramref name="position"/>.</summary>
    public static ImageElement AtNaturalSize(ImageSource source, Vector2 position, float maxWidth = float.PositiveInfinity)
    {
        ArgumentNullException.ThrowIfNull(source);
        float w = source.Width > 0 ? source.Width : 200;
        float h = source.Height > 0 ? source.Height : 200;
        if (w > maxWidth && maxWidth > 0 && float.IsFinite(maxWidth))
        {
            float s = maxWidth / w;
            w *= s;
            h *= s;
        }
        return new ImageElement(source, new Rect(position.X, position.Y, w, h));
    }

    /// <inheritdoc />
    protected override void DrawCore(IRenderContext context) => context.DrawImage(Source, Rect, Crop, Opacity);

    /// <inheritdoc />
    protected override bool HitTestCore(Vector2 localPoint, float tolerance) => Rect.Inflate(tolerance).Contains(localPoint);

    /// <inheritdoc />
    public override string GetAccessibleDescription() => Description ?? (Source.IsEmbedded ? "Embedded image" : "Image");
}
