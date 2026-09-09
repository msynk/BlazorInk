using BlazorInk.Brushes;
using BlazorInk.Export;
using BlazorInk.Geometry;
using Xunit;
using Xunit.Abstractions;

namespace BlazorInk.Tests.Core;

/// <summary>
/// Regressions in the outline the brushes generate: the shape of a committed stroke must not depend on how many
/// points simplification happened to keep.
/// </summary>
public class StrokeGeometryRegressionTests(ITestOutputHelper output)
{
    /// <summary>
    /// Simplification is what makes this a regression rather than a curiosity: a straight stroke is committed as
    /// two points hundreds of units apart, and the taper was evaluated only at those. A taper meant for the last
    /// few units of the stroke became a wedge down its whole length, so a line looked right while it was being
    /// drawn and changed shape the moment it was committed.
    /// </summary>
    [Theory]
    [InlineData(2)]
    [InlineData(6)]
    [InlineData(60)]
    public void A_taper_covers_the_same_length_however_few_points_the_stroke_kept(int count)
    {
        var buffer = new StrokePointBuffer(count);
        for (int i = 0; i < count; i++)
        {
            float t = i / (float)(count - 1);
            buffer.Add(new InkPoint(80 + t * 480, 200, 0.8f, i * 8f));
        }
        var stroke = new Stroke(buffer.ToStrokePoints(), new PenBrush { Width = 6f, TaperEnd = 5f });

        var document = new InkDocument(new DocumentSettings { Width = 700, Height = 400, Background = InkColor.Transparent });
        document.AddElement(stroke);
        Raster.RasterSurface surface = PngExporter.Render(document,
            new PngExportOptions { Region = ExportRegion.Custom, CustomRegion = new Rect(0, 0, 700, 400), Background = InkColor.Transparent },
            out _, out _);
        byte[] rgba = surface.ToRgba32();

        float Coverage(int x)
        {
            long alpha = 0;
            for (int y = 0; y < surface.Height; y++) alpha += rgba[(y * surface.Width + x) * 4 + 3];
            return alpha / 255f;
        }

        // The taper zone is the last 5 units, so everything from well after the start to well before the end must
        // be at full width whatever the point count is.
        float[] samples = [Coverage(120), Coverage(300), Coverage(500), Coverage(550)];
        output.WriteLine($"{count} points -> " + string.Join(" ", samples.Select(v => v.ToString("0.00"))));
        foreach (float coverage in samples)
        {
            Assert.True(coverage > 4.5f, $"a {count}-point stroke tapered outside its taper zone: {string.Join(" ", samples)}");
        }
    }
}
