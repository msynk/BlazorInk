using System.Globalization;
using System.Numerics;
using BlazorInk.Geometry;
using BlazorInk.Rendering;

namespace BlazorInk.Export;

/// <summary>
/// An <see cref="IRenderContext"/> that writes SVG markup. Paths stay paths, so exported drawings remain
/// editable vector artwork rather than an embedded bitmap.
/// </summary>
/// <remarks>
/// All text that reaches the output (font families, text content, descriptions) is XML-escaped, and the context
/// never emits script, event handlers, external references or <c>foreignObject</c>, so the result is safe to serve.
/// </remarks>
public sealed class SvgRenderContext : IRenderContext, IRenderContextCapabilities
{
    private readonly TextWriter _writer;
    private readonly int _decimals;
    private readonly Stack<string> _closeTags = new();
    private int _clipCounter;

    /// <summary>Creates a context writing into <paramref name="writer"/>.</summary>
    /// <param name="writer">Destination for the SVG fragment.</param>
    /// <param name="decimals">Decimals kept for coordinates.</param>
    public SvgRenderContext(TextWriter writer, int decimals = 2)
    {
        ArgumentNullException.ThrowIfNull(writer);
        _writer = writer;
        _decimals = Math.Clamp(decimals, 0, 6);
    }

    /// <inheritdoc />
    public bool SupportsText => true;

    /// <inheritdoc />
    public bool SupportsImages => true;

    /// <summary>
    /// Prefix for generated ids, so fragments from several documents can be combined without collisions.
    /// Restricted to characters that are safe inside an attribute value.
    /// </summary>
    public string IdPrefix
    {
        get => _idPrefix;
        init => _idPrefix = IsSafeIdPrefix(value) ? value : "bi";
    }

    private readonly string _idPrefix = "bi";

    private static bool IsSafeIdPrefix(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 96 || !char.IsAsciiLetter(value[0])) return false;
        foreach (char c in value)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_') return false;
        }
        return true;
    }

    /// <inheritdoc />
    public void Save() => _closeTags.Push(string.Empty);

    /// <inheritdoc />
    public void Restore()
    {
        if (_closeTags.Count == 0) return;
        string tags = _closeTags.Pop();
        if (tags.Length > 0) _writer.Write(tags);
    }

    /// <inheritdoc />
    public void Transform(Matrix3x2 matrix)
    {
        if (matrix.IsIdentity) return;
        _writer.Write("<g transform=\"matrix(");
        WriteNumber(matrix.M11, 6);
        _writer.Write(' ');
        WriteNumber(matrix.M12, 6);
        _writer.Write(' ');
        WriteNumber(matrix.M21, 6);
        _writer.Write(' ');
        WriteNumber(matrix.M22, 6);
        _writer.Write(' ');
        WriteNumber(matrix.M31, _decimals);
        _writer.Write(' ');
        WriteNumber(matrix.M32, _decimals);
        _writer.Write(")\">");
        PushClose("</g>");
    }

    /// <inheritdoc />
    public void ClipRect(Rect rect)
    {
        string id = IdPrefix + "-clip-" + _clipCounter++.ToString(CultureInfo.InvariantCulture);
        _writer.Write("<clipPath id=\"");
        _writer.Write(id);
        _writer.Write("\"><rect x=\"");
        WriteNumber(rect.X, _decimals);
        _writer.Write("\" y=\"");
        WriteNumber(rect.Y, _decimals);
        _writer.Write("\" width=\"");
        WriteNumber(MathF.Max(rect.Width, 0), _decimals);
        _writer.Write("\" height=\"");
        WriteNumber(MathF.Max(rect.Height, 0), _decimals);
        _writer.Write("\"/></clipPath><g clip-path=\"url(#");
        _writer.Write(id);
        _writer.Write(")\">");
        PushClose("</g>");
    }

    /// <inheritdoc />
    public void FillPath(PathGeometry path, in FillPaint paint)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.IsEmpty || paint.EffectiveAlpha <= 0) return;
        _writer.Write("<path d=\"");
        path.WriteSvgPathData(_writer, _decimals);
        _writer.Write("\" fill=\"");
        _writer.Write(RgbOnly(paint.Color));
        _writer.Write('"');
        WriteOpacity("fill-opacity", paint.EffectiveAlpha);
        if (paint.Rule == FillRule.EvenOdd) _writer.Write(" fill-rule=\"evenodd\"");
        WriteBlend(paint.Blend);
        _writer.Write("/>");
    }

    /// <inheritdoc />
    public void StrokePath(PathGeometry path, in StrokePaint paint)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.IsEmpty || paint.EffectiveAlpha <= 0 || paint.Width <= 0) return;
        _writer.Write("<path d=\"");
        path.WriteSvgPathData(_writer, _decimals);
        _writer.Write("\" fill=\"none\" stroke=\"");
        _writer.Write(RgbOnly(paint.Color));
        _writer.Write("\" stroke-width=\"");
        WriteNumber(paint.Width, _decimals);
        _writer.Write('"');
        WriteOpacity("stroke-opacity", paint.EffectiveAlpha);
        if (paint.Cap != LineCap.Butt) _writer.Write(paint.Cap == LineCap.Round ? " stroke-linecap=\"round\"" : " stroke-linecap=\"square\"");
        if (paint.Join != LineJoin.Miter) _writer.Write(paint.Join == LineJoin.Round ? " stroke-linejoin=\"round\"" : " stroke-linejoin=\"bevel\"");
        if (paint.Dash is { Length: > 0 } dash)
        {
            _writer.Write(" stroke-dasharray=\"");
            for (int i = 0; i < dash.Length; i++)
            {
                if (i > 0) _writer.Write(' ');
                WriteNumber(MathF.Max(dash[i], 0), _decimals);
            }
            _writer.Write('"');
        }
        WriteBlend(paint.Blend);
        _writer.Write("/>");
    }

    /// <inheritdoc />
    public void DrawImage(ImageSource image, Rect destination, Rect? sourceRect, float opacity)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (destination.IsEmpty || opacity <= 0) return;

        // A crop is expressed as an oversized image behind a clip rectangle, which keeps the pixels untouched.
        if (sourceRect is { } crop && crop.Width > 0 && crop.Height > 0 && image.Width > 0 && image.Height > 0)
        {
            float scaleX = destination.Width / crop.Width;
            float scaleY = destination.Height / crop.Height;
            var full = new Rect(destination.X - crop.X * scaleX, destination.Y - crop.Y * scaleY, image.Width * scaleX, image.Height * scaleY);
            ClipRect(destination);
            WriteImageTag(image, full, opacity);
            Restore();
            return;
        }
        WriteImageTag(image, destination, opacity);
    }

    private void WriteImageTag(ImageSource image, Rect rect, float opacity)
    {
        _writer.Write("<image x=\"");
        WriteNumber(rect.X, _decimals);
        _writer.Write("\" y=\"");
        WriteNumber(rect.Y, _decimals);
        _writer.Write("\" width=\"");
        WriteNumber(MathF.Max(rect.Width, 0), _decimals);
        _writer.Write("\" height=\"");
        WriteNumber(MathF.Max(rect.Height, 0), _decimals);
        _writer.Write("\" preserveAspectRatio=\"none\"");
        WriteOpacity("opacity", opacity);
        _writer.Write(" href=\"");
        SvgText.WriteEscaped(_writer, image.Uri);
        _writer.Write("\"/>");
    }

    /// <inheritdoc />
    public void DrawText(string text, Vector2 origin, in TextStyle style, float opacity)
    {
        if (string.IsNullOrEmpty(text) || opacity <= 0) return;
        float lineHeight = style.FontSize * style.LineHeight;
        _writer.Write("<text x=\"");
        WriteNumber(origin.X, _decimals);
        _writer.Write("\" y=\"");
        // SVG positions text on its baseline; the element's origin is the top of the first line.
        WriteNumber(origin.Y + style.FontSize * 0.82f, _decimals);
        _writer.Write("\" font-family=\"");
        SvgText.WriteEscaped(_writer, style.FontFamily);
        _writer.Write("\" font-size=\"");
        WriteNumber(style.FontSize, _decimals);
        _writer.Write("\" fill=\"");
        _writer.Write(RgbOnly(style.Color));
        _writer.Write('"');
        WriteOpacity("fill-opacity", style.Color.Alpha * opacity);
        if (style.Bold) _writer.Write(" font-weight=\"bold\"");
        if (style.Italic) _writer.Write(" font-style=\"italic\"");
        if (style.Align != TextAlign.Left) _writer.Write(style.Align == TextAlign.Center ? " text-anchor=\"middle\"" : " text-anchor=\"end\"");
        _writer.Write(" xml:space=\"preserve\">");

        int line = 0;
        foreach (ReadOnlySpan<char> segment in text.AsSpan().EnumerateLines())
        {
            _writer.Write("<tspan x=\"");
            WriteNumber(origin.X, _decimals);
            _writer.Write("\" dy=\"");
            WriteNumber(line == 0 ? 0 : lineHeight, _decimals);
            _writer.Write("\">");
            SvgText.WriteEscaped(_writer, segment);
            _writer.Write("</tspan>");
            line++;
        }
        _writer.Write("</text>");
    }

    /// <inheritdoc />
    public void PushGroup(float opacity, BlendMode blend)
    {
        _writer.Write("<g");
        if (opacity < 1f) WriteOpacity("opacity", opacity);
        if (blend != BlendMode.Normal)
        {
            _writer.Write(" style=\"mix-blend-mode:");
            _writer.Write(BlendName(blend));
            _writer.Write(";isolation:isolate\"");
        }
        else
        {
            _writer.Write(" style=\"isolation:isolate\"");
        }
        _writer.Write('>');
        _closeTags.Push("</g>");
    }

    /// <inheritdoc />
    public void PopGroup()
    {
        if (_closeTags.Count == 0) return;
        _writer.Write(_closeTags.Pop());
    }

    /// <summary>Writes a raw group open tag with the given attributes. Used by the document exporter for layers and masks.</summary>
    internal void WriteRawOpen(string tag) => _writer.Write(tag);

    /// <summary>Writes raw markup. Callers are responsible for escaping.</summary>
    internal void WriteRaw(string markup) => _writer.Write(markup);

    private void PushClose(string tag)
    {
        // Attach to the innermost Save() scope so Restore() closes everything opened after it.
        if (_closeTags.Count > 0)
        {
            string existing = _closeTags.Pop();
            _closeTags.Push(tag + existing);
        }
        else
        {
            _closeTags.Push(tag);
        }
    }

    private void WriteOpacity(string attribute, float value)
    {
        if (value >= 1f) return;
        _writer.Write(' ');
        _writer.Write(attribute);
        _writer.Write("=\"");
        WriteNumber(Math.Clamp(value, 0f, 1f), 3);
        _writer.Write('"');
    }

    private void WriteBlend(BlendMode blend)
    {
        if (blend == BlendMode.Normal) return;
        _writer.Write(" style=\"mix-blend-mode:");
        _writer.Write(BlendName(blend));
        _writer.Write('"');
    }

    private static string BlendName(BlendMode blend) => blend switch
    {
        BlendMode.Multiply => "multiply",
        BlendMode.Screen => "screen",
        _ => "normal",
    };

    /// <summary>Returns <c>#rrggbb</c>; alpha is emitted separately as an opacity attribute for maximum viewer compatibility.</summary>
    private static string RgbOnly(InkColor color) => (color with { A = 255 }).ToCss();

    private void WriteNumber(float value, int decimals)
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
        _writer.Write(s);
    }
}

/// <summary>XML escaping helpers shared by the SVG writers.</summary>
internal static class SvgText
{
    /// <summary>Writes text with XML special characters escaped.</summary>
    public static void WriteEscaped(TextWriter writer, ReadOnlySpan<char> text)
    {
        foreach (char c in text)
        {
            switch (c)
            {
                case '<': writer.Write("&lt;"); break;
                case '>': writer.Write("&gt;"); break;
                case '&': writer.Write("&amp;"); break;
                case '"': writer.Write("&quot;"); break;
                case '\'': writer.Write("&#39;"); break;
                default:
                    // Control characters are not legal in XML 1.0 and would make the output unparseable.
                    if (c < 0x20 && c is not ('\t' or '\n' or '\r')) writer.Write(' ');
                    else writer.Write(c);
                    break;
            }
        }
    }

    /// <summary>Returns the text with XML special characters escaped.</summary>
    public static string Escape(string text)
    {
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        WriteEscaped(writer, text);
        return writer.ToString();
    }
}
