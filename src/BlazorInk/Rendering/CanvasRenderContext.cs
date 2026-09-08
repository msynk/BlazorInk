using System.Numerics;
using BlazorInk.Geometry;
using BlazorInk.Rendering;

namespace BlazorInk.Canvas;

/// <summary>Opcodes of the canvas command buffer. Kept in sync with the <c>OP</c> table in <c>blazorink.js</c>.</summary>
internal enum CanvasCommand : byte
{
    BeginPath = 0,
    MoveTo = 1,
    LineTo = 2,
    QuadTo = 3,
    CubicTo = 4,
    ClosePath = 5,
    Fill = 6,
    Stroke = 7,
    Save = 8,
    Restore = 9,
    Transform = 10,
    ClipRect = 11,
    DrawImage = 12,
    DrawText = 13,
    PushGroup = 14,
    PopGroup = 15,
    Clear = 16,
    SetTransform = 17,
    SetDash = 18,
    ClearRect = 19,
}

/// <summary>
/// An <see cref="IRenderContext"/> that records drawing operations into a flat buffer of doubles, which the
/// browser replays onto a 2D canvas in a single interop call.
/// </summary>
/// <remarks>
/// <para>
/// Recording is allocation-free after warm-up: the buffer is reused between frames and grows only when a frame
/// needs more room than any frame before it. Strings (text and font specifications) go into a side table and are
/// referenced by index, so the numeric buffer stays uniform and can be handed to JavaScript without copying in
/// WebAssembly.
/// </para>
/// <para>
/// This is the only rendering path the on-screen canvas uses. The engine never calls canvas APIs directly, which
/// is what allows the same drawing code to produce SVG and PNG output.
/// </para>
/// </remarks>
public sealed class CanvasRenderContext : IRenderContext, IRenderContextCapabilities
{
    private double[] _buffer;
    private int _count;
    private readonly List<string> _strings = new();
    private readonly Dictionary<string, int> _stringIndex = new(StringComparer.Ordinal);

    /// <summary>Creates a context with an initial capacity.</summary>
    public CanvasRenderContext(int capacity = 4096)
    {
        _buffer = new double[Math.Max(capacity, 64)];
    }

    /// <summary>The recorded commands.</summary>
    public ReadOnlySpan<double> Commands => _buffer.AsSpan(0, _count);

    /// <summary>The recorded commands as an array segment, for interop that needs an array.</summary>
    public ArraySegment<double> CommandSegment => new(_buffer, 0, _count);

    /// <summary>Number of recorded values.</summary>
    public int Count => _count;

    /// <summary>The string table referenced by text and image commands.</summary>
    public IReadOnlyList<string> Strings => _strings;

    /// <summary>True when nothing has been recorded.</summary>
    public bool IsEmpty => _count == 0;

    /// <inheritdoc />
    public bool SupportsText => true;

    /// <inheritdoc />
    public bool SupportsImages => true;

    /// <summary>Clears the buffer for the next frame while keeping its capacity.</summary>
    public void Reset()
    {
        _count = 0;
        _strings.Clear();
        _stringIndex.Clear();
    }

    /// <summary>Clears the whole surface to a color (transparent erases it).</summary>
    public void Clear(InkColor color)
    {
        Write(CanvasCommand.Clear);
        Write(color.ToArgb());
    }

    /// <summary>Clears a rectangle in the current coordinate space.</summary>
    public void ClearRect(Rect rect)
    {
        Write(CanvasCommand.ClearRect);
        Write(rect.X);
        Write(rect.Y);
        Write(rect.Width);
        Write(rect.Height);
    }

    /// <summary>Replaces the current transform (used to apply the device pixel ratio and viewport).</summary>
    public void SetTransform(Matrix3x2 matrix)
    {
        Write(CanvasCommand.SetTransform);
        Write(matrix.M11);
        Write(matrix.M12);
        Write(matrix.M21);
        Write(matrix.M22);
        Write(matrix.M31);
        Write(matrix.M32);
    }

    /// <inheritdoc />
    public void Save() => Write(CanvasCommand.Save);

    /// <inheritdoc />
    public void Restore() => Write(CanvasCommand.Restore);

    /// <inheritdoc />
    public void Transform(Matrix3x2 matrix)
    {
        if (matrix.IsIdentity) return;
        Write(CanvasCommand.Transform);
        Write(matrix.M11);
        Write(matrix.M12);
        Write(matrix.M21);
        Write(matrix.M22);
        Write(matrix.M31);
        Write(matrix.M32);
    }

    /// <inheritdoc />
    public void ClipRect(Rect rect)
    {
        Write(CanvasCommand.ClipRect);
        Write(rect.X);
        Write(rect.Y);
        Write(rect.Width);
        Write(rect.Height);
    }

    /// <inheritdoc />
    public void FillPath(PathGeometry path, in FillPaint paint)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.IsEmpty) return;
        float alpha = Math.Clamp(paint.Opacity, 0f, 1f);
        if (alpha <= 0 && paint.Blend != BlendMode.DestinationOut) return;
        WritePath(path);
        Write(CanvasCommand.Fill);
        Write(paint.Color.ToArgb());
        Write(alpha);
        Write((int)paint.Blend);
        Write(paint.Rule == FillRule.EvenOdd ? 1 : 0);
    }

    /// <inheritdoc />
    public void StrokePath(PathGeometry path, in StrokePaint paint)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.IsEmpty || paint.Width <= 0) return;
        float alpha = Math.Clamp(paint.Opacity, 0f, 1f);
        if (alpha <= 0 && paint.Blend != BlendMode.DestinationOut) return;
        if (paint.Dash is { Length: > 0 } dash)
        {
            Write(CanvasCommand.SetDash);
            Write(dash.Length);
            foreach (float d in dash) Write(MathF.Max(d, 0f));
        }
        WritePath(path);
        Write(CanvasCommand.Stroke);
        Write(paint.Color.ToArgb());
        Write(paint.Width);
        Write(alpha);
        Write((int)paint.Blend);
        Write((int)paint.Cap);
        Write((int)paint.Join);
    }

    /// <inheritdoc />
    public void DrawImage(ImageSource image, Rect destination, Rect? sourceRect, float opacity)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (destination.IsEmpty || opacity <= 0) return;
        Write(CanvasCommand.DrawImage);
        Write(InternString(image.Uri));
        Write(destination.X);
        Write(destination.Y);
        Write(destination.Width);
        Write(destination.Height);
        Rect crop = sourceRect ?? Rect.Empty;
        Write(sourceRect is not null ? 1 : 0);
        Write(crop.X);
        Write(crop.Y);
        Write(crop.Width);
        Write(crop.Height);
        Write(Math.Clamp(opacity, 0f, 1f));
    }

    /// <inheritdoc />
    public void DrawText(string text, Vector2 origin, in TextStyle style, float opacity)
    {
        if (string.IsNullOrEmpty(text) || opacity <= 0) return;
        Write(CanvasCommand.DrawText);
        Write(InternString(text));
        Write(InternString(ToCssFont(style)));
        Write(origin.X);
        Write(origin.Y);
        Write(style.FontSize);
        Write(style.FontSize * style.LineHeight);
        Write(style.Color.ToArgb());
        Write(Math.Clamp(opacity, 0f, 1f));
        Write((int)style.Align);
    }

    /// <inheritdoc />
    public void PushGroup(float opacity, BlendMode blend)
    {
        Write(CanvasCommand.PushGroup);
        Write(Math.Clamp(opacity, 0f, 1f));
        Write((int)blend);
    }

    /// <inheritdoc />
    public void PopGroup() => Write(CanvasCommand.PopGroup);

    /// <summary>Builds the CSS <c>font</c> shorthand for a text style.</summary>
    public static string ToCssFont(in TextStyle style)
    {
        string size = style.FontSize.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        string weight = style.Bold ? "bold " : string.Empty;
        string italic = style.Italic ? "italic " : string.Empty;
        return $"{italic}{weight}{size}px {style.FontFamily}";
    }

    private void WritePath(PathGeometry path)
    {
        Write(CanvasCommand.BeginPath);
        ReadOnlySpan<float> coords = path.Coordinates;
        int c = 0;
        foreach (PathVerb verb in path.Verbs)
        {
            switch (verb)
            {
                case PathVerb.MoveTo:
                    Write(CanvasCommand.MoveTo);
                    Write(coords[c++]);
                    Write(coords[c++]);
                    break;
                case PathVerb.LineTo:
                    Write(CanvasCommand.LineTo);
                    Write(coords[c++]);
                    Write(coords[c++]);
                    break;
                case PathVerb.QuadTo:
                    Write(CanvasCommand.QuadTo);
                    Write(coords[c++]);
                    Write(coords[c++]);
                    Write(coords[c++]);
                    Write(coords[c++]);
                    break;
                case PathVerb.CubicTo:
                    Write(CanvasCommand.CubicTo);
                    for (int i = 0; i < 6; i++) Write(coords[c++]);
                    break;
                case PathVerb.Close:
                    Write(CanvasCommand.ClosePath);
                    break;
            }
        }
    }

    private int InternString(string value)
    {
        if (_stringIndex.TryGetValue(value, out int index)) return index;
        index = _strings.Count;
        _strings.Add(value);
        _stringIndex[value] = index;
        return index;
    }

    private void Write(CanvasCommand command) => Write((double)(byte)command);

    private void Write(float value) => Write((double)value);

    private void Write(int value) => Write((double)value);

    private void Write(uint value) => Write((double)value);

    private void Write(double value)
    {
        if (_count == _buffer.Length) Array.Resize(ref _buffer, _buffer.Length * 2);
        _buffer[_count++] = value;
    }
}
