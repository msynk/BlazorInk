using System.Numerics;
using BlazorInk.Brushes;
using BlazorInk.Geometry;

namespace BlazorInk.Tests.Core;

/// <summary>Builders for the drawings used across the test suite.</summary>
internal static class TestData
{
    public static StrokePoints Line(Vector2 from, Vector2 to, int count = 20, float pressure = 0.5f)
    {
        var buffer = new StrokePointBuffer(count);
        for (int i = 0; i < count; i++)
        {
            float t = count == 1 ? 0 : i / (float)(count - 1);
            Vector2 p = Vector2.Lerp(from, to, t);
            buffer.Add(new InkPoint(p.X, p.Y, pressure, i * 8f));
        }
        return buffer.ToStrokePoints();
    }

    public static StrokePoints Circle(Vector2 center, float radius, int count = 64)
    {
        var buffer = new StrokePointBuffer(count);
        for (int i = 0; i < count; i++)
        {
            float a = i / (float)count * MathF.Tau;
            buffer.Add(new InkPoint(center.X + MathF.Cos(a) * radius, center.Y + MathF.Sin(a) * radius, 0.5f, i * 8f));
        }
        return buffer.ToStrokePoints();
    }

    /// <summary>Points with a pressure ramp from light to heavy and back, as a real signature has.</summary>
    public static StrokePoints PressureRamp(Vector2 from, Vector2 to, int count = 40)
    {
        var buffer = new StrokePointBuffer(count);
        for (int i = 0; i < count; i++)
        {
            float t = i / (float)(count - 1);
            Vector2 p = Vector2.Lerp(from, to, t);
            float pressure = 0.1f + 0.9f * MathF.Sin(t * MathF.PI);
            buffer.Add(new InkPoint(p.X, p.Y, pressure, i * 6f));
        }
        return buffer.ToStrokePoints();
    }

    public static Stroke Stroke(Vector2 from, Vector2 to, Brush? brush = null, int count = 20)
        => new(Line(from, to, count), brush ?? new PenBrush());

    /// <summary>A document with a handful of strokes, a shape, text and two layers.</summary>
    public static InkDocument SampleDocument()
    {
        var document = new InkDocument(new DocumentSettings { Width = 400, Height = 300, Background = InkColor.White });
        Guid drawing = document.Layers[0].Id;
        document.AddElement(new Stroke(Line(new Vector2(20, 20), new Vector2(180, 120), 30), new PenBrush { Width = 5 }), drawing);
        document.AddElement(new Stroke(Circle(new Vector2(100, 200), 40), new MarkerBrush { Color = InkColor.Parse("#c62828") }), drawing);
        document.AddElement(new Stroke(Line(new Vector2(200, 40), new Vector2(380, 60), 12), new HighlighterBrush()), drawing);

        Layer annotations = document.AddLayer("Annotations");
        document.AddElement(new RectangleShape(new Rect(220, 150, 120, 80)) { Style = new ShapeStyle { StrokeColor = InkColor.Parse("#1565c0"), StrokeWidth = 3 } }, annotations.Id);
        document.AddElement(new LineShape(new Vector2(210, 260), new Vector2(330, 250)) { EndArrow = true, Style = new ShapeStyle { StrokeColor = InkColor.Parse("#2e7d32"), StrokeWidth = 2 } }, annotations.Id);
        document.AddElement(new TextElement(new Vector2(230, 165), "Review") { Style = Rendering.TextStyle.Default with { FontSize = 18 } }, annotations.Id);
        return document;
    }

    /// <summary>An in-memory PNG usable as an <see cref="Rendering.ImageSource"/>.</summary>
    public static byte[] CreatePng(int width, int height, Func<int, int, InkColor> shader)
    {
        byte[] rgba = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                InkColor c = shader(x, y);
                int o = (y * width + x) * 4;
                rgba[o] = c.R;
                rgba[o + 1] = c.G;
                rgba[o + 2] = c.B;
                rgba[o + 3] = c.A;
            }
        }
        return Raster.PngCodec.Encode(rgba, width, height);
    }
}
