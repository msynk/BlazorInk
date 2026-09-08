using System.Numerics;
using System.Xml;
using BlazorInk.Brushes;
using BlazorInk.Export;
using BlazorInk.Geometry;
using BlazorInk.Raster;
using BlazorInk.Rendering;
using Xunit;

namespace BlazorInk.Tests.Core;

public class RasterizerTests
{
    private static RasterSurface Fill(int size, Action<RasterRenderContext> draw, InkColor? background = null)
    {
        var surface = new RasterSurface(size, size);
        surface.Clear(background ?? InkColor.Transparent);
        var context = new RasterRenderContext(surface, Matrix3x2.Identity);
        draw(context);
        return surface;
    }

    [Fact]
    public void Fills_a_rectangle_exactly()
    {
        RasterSurface surface = Fill(40, c => c.FillPath(new PathBuilder().AddRect(new Rect(10, 10, 20, 20)).ToGeometry(), new FillPaint(InkColor.Black)));

        Assert.Equal(255, surface.GetPixel(20, 20).A);
        Assert.Equal(0, surface.GetPixel(5, 20).A);
        Assert.Equal(0, surface.GetPixel(35, 20).A);
        Assert.Equal(255, surface.GetPixel(10, 10).A);
        Assert.Equal(0, surface.GetPixel(9, 9).A);
    }

    [Fact]
    public void Anti_aliases_a_diagonal_edge()
    {
        RasterSurface surface = Fill(40, c =>
        {
            Vector2[] triangle = [new(0, 0), new(40, 40), new(0, 40)];
            c.FillPath(new PathBuilder().AddPolygon(triangle).ToGeometry(), new FillPaint(InkColor.Black));
        });

        // Pixels straddling the diagonal must be partially covered, not all-or-nothing.
        int partial = 0;
        for (int i = 5; i < 35; i++)
        {
            byte a = surface.GetPixel(i, i).A;
            if (a is > 20 and < 235) partial++;
        }
        Assert.True(partial > 20, $"expected anti-aliased edge pixels, found {partial}");
    }

    [Fact]
    public void Respects_the_even_odd_fill_rule()
    {
        PathGeometry path = new PathBuilder()
            .AddRect(new Rect(5, 5, 30, 30))
            .AddRect(new Rect(13, 13, 14, 14))
            .ToGeometry();

        RasterSurface evenOdd = Fill(40, c => c.FillPath(path, new FillPaint(InkColor.Black, 1f, BlendMode.Normal, FillRule.EvenOdd)));
        RasterSurface nonZero = Fill(40, c => c.FillPath(path, new FillPaint(InkColor.Black, 1f, BlendMode.Normal, FillRule.NonZero)));

        Assert.Equal(0, evenOdd.GetPixel(20, 20).A);   // the inner rectangle is a hole
        Assert.Equal(255, nonZero.GetPixel(20, 20).A); // with non-zero it stays solid
        Assert.Equal(255, evenOdd.GetPixel(8, 20).A);
    }

    [Fact]
    public void Strokes_a_line_at_the_requested_width()
    {
        RasterSurface surface = Fill(60, c => c.StrokePath(
            new PathBuilder().MoveTo(10, 30).LineTo(50, 30).ToGeometry(),
            new StrokePaint(InkColor.Black, 8f, 1f, BlendMode.Normal, LineCap.Butt)));

        int covered = 0;
        for (int y = 0; y < 60; y++) if (surface.GetPixel(30, y).A > 128) covered++;
        Assert.Equal(8, covered);
    }

    [Fact]
    public void Round_caps_extend_past_the_endpoint_and_butt_caps_do_not()
    {
        RasterSurface round = Fill(60, c => c.StrokePath(new PathBuilder().MoveTo(20, 30).LineTo(40, 30).ToGeometry(), new StrokePaint(InkColor.Black, 10f, 1f, BlendMode.Normal, LineCap.Round)));
        RasterSurface butt = Fill(60, c => c.StrokePath(new PathBuilder().MoveTo(20, 30).LineTo(40, 30).ToGeometry(), new StrokePaint(InkColor.Black, 10f, 1f, BlendMode.Normal, LineCap.Butt)));

        Assert.True(round.GetPixel(17, 30).A > 200, "round caps should extend past the endpoint");
        Assert.Equal(0, butt.GetPixel(17, 30).A);
    }

    [Fact]
    public void Multiply_blending_darkens_the_overlap()
    {
        var surface = new RasterSurface(40, 40);
        surface.Clear(InkColor.White);
        var context = new RasterRenderContext(surface, Matrix3x2.Identity);
        var yellow = new InkColor(255, 235, 59);
        context.FillPath(new PathBuilder().AddRect(new Rect(0, 0, 40, 20)).ToGeometry(), new FillPaint(yellow, 0.5f, BlendMode.Multiply));
        context.FillPath(new PathBuilder().AddRect(new Rect(0, 10, 40, 20)).ToGeometry(), new FillPaint(yellow, 0.5f, BlendMode.Multiply));

        InkColor single = surface.GetPixel(20, 5);
        InkColor doubled = surface.GetPixel(20, 15);
        Assert.True(doubled.B < single.B, "overlapping multiply highlights should get darker");
        Assert.True(doubled.R > 200, "a yellow highlighter must not turn the paper dark");
    }

    [Fact]
    public void Destination_out_erases_what_is_beneath_it()
    {
        var surface = new RasterSurface(40, 40);
        surface.Clear(InkColor.Transparent);
        var context = new RasterRenderContext(surface, Matrix3x2.Identity);
        context.FillPath(new PathBuilder().AddRect(new Rect(0, 0, 40, 40)).ToGeometry(), new FillPaint(InkColor.Black));
        context.FillPath(new PathBuilder().AddCircle(new Vector2(20, 20), 10).ToGeometry(), new FillPaint(InkColor.Black, 1f, BlendMode.DestinationOut));

        Assert.Equal(0, surface.GetPixel(20, 20).A);
        Assert.Equal(255, surface.GetPixel(2, 2).A);
    }

    [Fact]
    public void Group_opacity_applies_to_the_group_as_a_whole()
    {
        var surface = new RasterSurface(40, 40);
        surface.Clear(InkColor.Transparent);
        var context = new RasterRenderContext(surface, Matrix3x2.Identity);
        context.PushGroup(0.5f, BlendMode.Normal);
        // Two overlapping opaque rectangles inside one group must not compound to more than 50%.
        context.FillPath(new PathBuilder().AddRect(new Rect(0, 0, 40, 40)).ToGeometry(), new FillPaint(InkColor.Black));
        context.FillPath(new PathBuilder().AddRect(new Rect(0, 0, 40, 40)).ToGeometry(), new FillPaint(InkColor.Black));
        context.PopGroup();

        Assert.InRange(surface.GetPixel(20, 20).A, 125, 131);
    }

    [Fact]
    public void The_clip_rectangle_is_honored()
    {
        RasterSurface surface = Fill(40, c =>
        {
            c.Save();
            c.ClipRect(new Rect(0, 0, 20, 40));
            c.FillPath(new PathBuilder().AddRect(new Rect(0, 0, 40, 40)).ToGeometry(), new FillPaint(InkColor.Black));
            c.Restore();
        });

        Assert.Equal(255, surface.GetPixel(10, 20).A);
        Assert.Equal(0, surface.GetPixel(30, 20).A);
    }

    [Fact]
    public void Transforms_scale_stroke_width_along_with_geometry()
    {
        var surface = new RasterSurface(80, 80);
        surface.Clear(InkColor.Transparent);
        var context = new RasterRenderContext(surface, Matrix3x2.CreateScale(2f));
        context.StrokePath(new PathBuilder().MoveTo(5, 20).LineTo(35, 20).ToGeometry(), new StrokePaint(InkColor.Black, 4f, 1f, BlendMode.Normal, LineCap.Butt));

        int covered = 0;
        for (int y = 0; y < 80; y++) if (surface.GetPixel(40, y).A > 128) covered++;
        Assert.Equal(8, covered);
    }

    [Fact]
    public void Drawing_far_outside_the_surface_is_safe()
    {
        RasterSurface surface = Fill(20, c =>
        {
            c.FillPath(new PathBuilder().AddRect(new Rect(-10000, -10000, 20000, 20000)).ToGeometry(), new FillPaint(InkColor.Black));
            c.FillPath(new PathBuilder().AddRect(new Rect(50000, 50000, 10, 10)).ToGeometry(), new FillPaint(InkColor.Black));
        });
        Assert.Equal(255, surface.GetPixel(10, 10).A);
    }
}

public class PngCodecTests
{
    [Fact]
    public void Round_trips_pixels_exactly()
    {
        const int size = 17;
        byte[] rgba = new byte[size * size * 4];
        var random = new Random(7);
        random.NextBytes(rgba);

        byte[] png = PngCodec.Encode(rgba, size, size);
        PngCodec.DecodedImage decoded = PngCodec.Decode(png);

        Assert.Equal(size, decoded.Width);
        Assert.Equal(size, decoded.Height);
        Assert.Equal(rgba, decoded.Rgba);
    }

    [Fact]
    public void Encoding_is_deterministic()
    {
        byte[] rgba = new byte[8 * 8 * 4];
        new Random(3).NextBytes(rgba);
        Assert.Equal(PngCodec.Encode(rgba, 8, 8), PngCodec.Encode(rgba, 8, 8));
    }

    [Fact]
    public void Produces_a_valid_png_signature()
    {
        byte[] png = TestData.CreatePng(4, 4, (_, _) => InkColor.Black);
        Assert.True(PngCodec.IsPng(png));
        Assert.False(PngCodec.IsPng([1, 2, 3, 4, 5, 6, 7, 8]));
    }

    [Fact]
    public void Compresses_a_flat_image_well()
    {
        byte[] png = TestData.CreatePng(200, 200, (_, _) => InkColor.White);
        Assert.True(png.Length < 2000, $"a flat 200x200 image encoded to {png.Length} bytes");
    }

    [Theory]
    [InlineData(new byte[] { 1, 2, 3 })]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })]
    public void Malformed_input_is_rejected(byte[] data)
    {
        Assert.Throws<FormatException>(() => PngCodec.Decode(data));
    }

    [Fact]
    public void An_oversized_declared_size_is_rejected()
    {
        byte[] png = TestData.CreatePng(4, 4, (_, _) => InkColor.Black);
        Assert.Throws<FormatException>(() => PngCodec.Decode(png, maxPixels: 4));
    }
}

public class PngExportTests
{
    [Fact]
    public void Exports_the_page_at_the_requested_scale()
    {
        InkDocument document = TestData.SampleDocument();
        PngExportResult result = PngExporter.ExportWithDetails(document, new PngExportOptions { Scale = 2f });

        Assert.Equal(800, result.Width);
        Assert.Equal(600, result.Height);
        Assert.True(PngCodec.IsPng(result.Bytes));
    }

    [Fact]
    public void The_exported_image_actually_contains_the_ink()
    {
        var document = new InkDocument(new DocumentSettings { Width = 100, Height = 100, Background = InkColor.White });
        document.AddElement(new Stroke(TestData.Line(new Vector2(10, 50), new Vector2(90, 50), 30), new PenBrush { Width = 6f, PressureCurve = PressureCurve.Constant, TaperEnd = 0 }));

        RasterSurface surface = PngExporter.Render(document, PngExportOptions.Default, out _, out _);
        Assert.True(surface.GetPixel(50, 50).R < 60, "the stroke should be dark at its center");
        Assert.Equal(255, surface.GetPixel(50, 10).R);
    }

    [Fact]
    public void A_transparent_background_is_preserved()
    {
        var document = new InkDocument(new DocumentSettings { Width = 50, Height = 50, Background = InkColor.Transparent });
        document.AddElement(new Stroke(TestData.Line(new Vector2(10, 25), new Vector2(40, 25), 10), new PenBrush { Width = 4f, PressureCurve = PressureCurve.Constant, TaperEnd = 0f }));

        RasterSurface surface = PngExporter.Render(document, PngExportOptions.Default, out _, out _);
        Assert.Equal(0, surface.GetPixel(25, 5).A);
        Assert.True(surface.GetPixel(25, 25).A > 200);
    }

    [Fact]
    public void Cropping_to_content_trims_the_empty_margins()
    {
        var document = new InkDocument(new DocumentSettings { Width = 500, Height = 500 });
        document.AddElement(new Stroke(TestData.Line(new Vector2(200, 200), new Vector2(240, 240), 10), new PenBrush { Width = 4f }));

        PngExportResult result = PngExporter.ExportWithDetails(document, new PngExportOptions { Region = ExportRegion.Content, Padding = 5f });
        Assert.True(result.Width < 100, $"expected a tight crop but got {result.Width}px");
        Assert.True(result.Width > 40);
    }

    [Fact]
    public void Exporting_a_selection_only_draws_the_selected_elements()
    {
        var document = new InkDocument(new DocumentSettings { Width = 200, Height = 100, Background = InkColor.White });
        var a = new Stroke(TestData.Line(new Vector2(10, 50), new Vector2(60, 50), 10), new PenBrush { Width = 6f });
        var b = new Stroke(TestData.Line(new Vector2(140, 50), new Vector2(190, 50), 10), new PenBrush { Width = 6f });
        document.AddElement(a);
        document.AddElement(b);

        var options = new PngExportOptions { Region = ExportRegion.Custom, CustomRegion = new Rect(0, 0, 200, 100), SelectedIds = [a.Id] };
        RasterSurface surface = PngExporter.Render(document, options, out _, out _);
        Assert.True(surface.GetPixel(35, 50).R < 100, "the selected stroke should be drawn");
        Assert.Equal(255, surface.GetPixel(165, 50).R);
    }

    [Fact]
    public void An_absurd_scale_is_refused_instead_of_exhausting_memory()
    {
        InkDocument document = TestData.SampleDocument();
        Assert.Throws<InvalidOperationException>(() => PngExporter.Export(document, new PngExportOptions { Scale = 5000f }));
    }

    [Fact]
    public void Text_is_reported_as_skipped_when_no_text_renderer_is_supplied()
    {
        var document = new InkDocument(new DocumentSettings { Width = 100, Height = 50 });
        document.AddElement(new TextElement(new Vector2(5, 5), "hello"));
        PngExportResult result = PngExporter.ExportWithDetails(document);
        Assert.Equal(1, result.SkippedTextElements);
        Assert.False(result.IsComplete);
    }

    [Fact]
    public void Embedded_png_images_are_drawn()
    {
        var document = new InkDocument(new DocumentSettings { Width = 40, Height = 40, Background = InkColor.White });
        byte[] png = TestData.CreatePng(8, 8, (x, y) => (x + y) % 2 == 0 ? new InkColor(200, 0, 0) : new InkColor(0, 0, 200));
        document.AddElement(new ImageElement(Rendering.ImageSource.FromBytes(png, "image/png", 8, 8), new Rect(8, 8, 24, 24)));

        RasterSurface surface = PngExporter.Render(document, PngExportOptions.Default, out _, out int skipped);
        Assert.Equal(0, skipped);
        InkColor middle = surface.GetPixel(20, 20);
        Assert.True(middle.R > 40 || middle.B > 40, "the image should have been drawn");
        Assert.Equal(255, surface.GetPixel(2, 2).R);
    }

    [Fact]
    public void Export_is_deterministic()
    {
        InkDocument document = TestData.SampleDocument();
        Assert.Equal(PngExporter.Export(document), PngExporter.Export(document));
    }

    [Fact]
    public void Eraser_strokes_remove_ink_within_their_layer_only()
    {
        var document = new InkDocument(new DocumentSettings { Width = 100, Height = 100, Background = InkColor.White });
        Guid bottom = document.Layers[0].Id;
        document.AddElement(new Stroke(TestData.Line(new Vector2(0, 30), new Vector2(100, 30), 20), new PenBrush { Width = 10f, PressureCurve = PressureCurve.Constant }), bottom);

        Layer top = document.AddLayer("Top");
        document.AddElement(new Stroke(TestData.Line(new Vector2(0, 70), new Vector2(100, 70), 20), new PenBrush { Width = 10f, PressureCurve = PressureCurve.Constant }), top.Id);
        document.AddElement(new Stroke(TestData.Line(new Vector2(40, 0), new Vector2(40, 100), 20), new PixelEraserBrush { Width = 24f }), top.Id);

        RasterSurface surface = PngExporter.Render(document, PngExportOptions.Default, out _, out _);
        Assert.Equal(255, surface.GetPixel(40, 70).R);          // erased on its own layer
        Assert.True(surface.GetPixel(40, 30).R < 100, "the eraser must not reach through to the layer below");
    }
}

public class SvgExportTests
{
    private static XmlDocument Parse(string svg)
    {
        var xml = new XmlDocument { XmlResolver = null };
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
        using var reader = XmlReader.Create(new StringReader(svg), settings);
        xml.Load(reader);
        return xml;
    }

    [Fact]
    public void Produces_well_formed_xml()
    {
        string svg = SvgExporter.Export(TestData.SampleDocument());
        XmlDocument xml = Parse(svg);
        Assert.Equal("svg", xml.DocumentElement!.LocalName);
        Assert.Equal("http://www.w3.org/2000/svg", xml.DocumentElement.NamespaceURI);
    }

    [Fact]
    public void Exports_strokes_as_paths_not_as_an_embedded_bitmap()
    {
        string svg = SvgExporter.Export(TestData.SampleDocument());
        XmlDocument xml = Parse(svg);
        XmlNodeList paths = xml.GetElementsByTagName("path");
        Assert.True(paths.Count >= 5, $"expected several vector paths, found {paths.Count}");
        Assert.DoesNotContain("data:image", svg, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Uses_the_document_size_as_the_view_box()
    {
        string svg = SvgExporter.Export(TestData.SampleDocument());
        XmlDocument xml = Parse(svg);
        Assert.Equal("0 0 400 300", xml.DocumentElement!.GetAttribute("viewBox"));
        Assert.Equal("400", xml.DocumentElement.GetAttribute("width"));
    }

    [Fact]
    public void Escapes_text_content_and_metadata()
    {
        var document = new InkDocument(new DocumentSettings { Width = 100, Height = 100 });
        document.AddElement(new TextElement(new Vector2(5, 5), "</text><script>alert('x')</script>"));
        string svg = SvgExporter.Export(document, new SvgExportOptions { Title = "<script>bad</script>" });

        Parse(svg); // must still be well formed
        Assert.DoesNotContain("<script>", svg, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("&lt;script&gt;", svg, StringComparison.Ordinal);
    }

    [Fact]
    public void Never_emits_script_or_event_handler_attributes()
    {
        InkDocument document = TestData.SampleDocument();
        document.AddElement(new TextElement(new Vector2(0, 0), "onload=alert(1)") { Description = "\" onclick=\"alert(1)" });
        string svg = SvgExporter.Export(document, new SvgExportOptions { IncludeElementDescriptions = true });

        XmlDocument xml = Parse(svg);
        foreach (XmlNode node in xml.GetElementsByTagName("*"))
        {
            Assert.NotEqual("script", node.LocalName, StringComparer.OrdinalIgnoreCase);
            Assert.NotEqual("foreignObject", node.LocalName, StringComparer.OrdinalIgnoreCase);
            if (node.Attributes is null) continue;
            foreach (XmlAttribute attribute in node.Attributes)
            {
                Assert.False(attribute.LocalName.StartsWith("on", StringComparison.OrdinalIgnoreCase), $"found event handler attribute {attribute.LocalName}");
            }
        }
    }

    [Fact]
    public void Eraser_strokes_become_masks()
    {
        var document = new InkDocument(new DocumentSettings { Width = 100, Height = 100 });
        document.AddElement(new Stroke(TestData.Line(new Vector2(0, 50), new Vector2(100, 50), 20), new PenBrush { Width = 10f }));
        document.AddElement(new Stroke(TestData.Line(new Vector2(50, 0), new Vector2(50, 100), 20), new PixelEraserBrush { Width = 20f }));

        string svg = SvgExporter.Export(document);
        XmlDocument xml = Parse(svg);
        Assert.Equal(1, xml.GetElementsByTagName("mask").Count);
        Assert.Contains("mask=\"url(#", svg, StringComparison.Ordinal);
    }

    [Fact]
    public void Highlighter_strokes_carry_the_multiply_blend_mode()
    {
        var document = new InkDocument(new DocumentSettings { Width = 100, Height = 100 });
        document.AddElement(new Stroke(TestData.Line(new Vector2(0, 50), new Vector2(100, 50), 10), new HighlighterBrush()));
        string svg = SvgExporter.Export(document);
        Assert.Contains("mix-blend-mode:multiply", svg, StringComparison.Ordinal);
    }

    [Fact]
    public void Layer_opacity_is_exported()
    {
        var document = new InkDocument(new DocumentSettings { Width = 100, Height = 100 });
        document.AddElement(new Stroke(TestData.Line(new Vector2(0, 0), new Vector2(50, 50), 10), new PenBrush()));
        document.UpdateLayer(document.Layers[0].Id, p => p with { Opacity = 0.25f });
        Assert.Contains("opacity=\"0.25\"", SvgExporter.Export(document), StringComparison.Ordinal);
    }

    [Fact]
    public void Element_transforms_are_exported_as_matrices()
    {
        var document = new InkDocument(new DocumentSettings { Width = 200, Height = 200 });
        document.AddElement(new RectangleShape(new Rect(0, 0, 20, 20)) { Transform = Matrix3x2.CreateRotation(0.5f) * Matrix3x2.CreateTranslation(50, 50) });
        Assert.Contains("transform=\"matrix(", SvgExporter.Export(document), StringComparison.Ordinal);
    }

    [Fact]
    public void Numbers_use_invariant_formatting()
    {
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("fr-FR");
            string svg = SvgExporter.Export(TestData.SampleDocument());
            Parse(svg);
            Assert.DoesNotContain("NaN", svg, StringComparison.Ordinal);
            Assert.Matches(@"viewBox=""[-0-9. ]+""", svg);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Export_is_deterministic()
    {
        InkDocument document = TestData.SampleDocument();
        Assert.Equal(SvgExporter.Export(document), SvgExporter.Export(document));
    }

    [Fact]
    public void Content_region_crops_to_the_drawing()
    {
        var document = new InkDocument(new DocumentSettings { Width = 1000, Height = 1000 });
        document.AddElement(new Stroke(TestData.Line(new Vector2(400, 400), new Vector2(440, 440), 10), new PenBrush { Width = 4f }));

        string svg = SvgExporter.Export(document, new SvgExportOptions { Region = ExportRegion.Content, Padding = 4f });
        XmlDocument xml = Parse(svg);
        string[] parts = xml.DocumentElement!.GetAttribute("viewBox").Split(' ');
        Assert.True(float.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture) < 100);
    }

    [Fact]
    public void Element_descriptions_are_exported_for_screen_readers()
    {
        var document = new InkDocument(new DocumentSettings { Width = 100, Height = 100 });
        document.AddElement(new Stroke(TestData.Line(new Vector2(0, 0), new Vector2(50, 50), 10), new PenBrush()) { Description = "Signature of Jane Doe" });

        string svg = SvgExporter.Export(document, new SvgExportOptions { IncludeElementDescriptions = true });
        Assert.Contains("<title>Signature of Jane Doe</title>", svg, StringComparison.Ordinal);
    }
}

public class RenderConsistencyTests
{
    [Fact]
    public void Svg_and_raster_render_the_same_number_of_elements()
    {
        InkDocument document = TestData.SampleDocument();
        var counting = new CountingContext();
        int drawn = DocumentRenderer.Render(document, counting);
        Assert.Equal(document.ElementCount, drawn);
    }

    [Fact]
    public void Culling_skips_elements_outside_the_clip_bounds()
    {
        var document = new InkDocument(new DocumentSettings { Width = 1000, Height = 1000 });
        for (int i = 0; i < 20; i++) document.AddElement(TestData.Stroke(new Vector2(i * 50, 0), new Vector2(i * 50 + 10, 10)));

        var counting = new CountingContext();
        int drawn = DocumentRenderer.Render(document, counting, new RenderOptions { ClipBounds = new Rect(0, 0, 120, 100) });
        Assert.True(drawn < 5, $"expected culling to skip most strokes but drew {drawn}");
    }

    private sealed class CountingContext : IRenderContext
    {
        public int Fills { get; private set; }
        public void Save() { }
        public void Restore() { }
        public void Transform(Matrix3x2 matrix) { }
        public void ClipRect(Rect rect) { }
        public void FillPath(PathGeometry path, in FillPaint paint) => Fills++;
        public void StrokePath(PathGeometry path, in StrokePaint paint) => Fills++;
        public void DrawImage(Rendering.ImageSource image, Rect destination, Rect? sourceRect, float opacity) { }
        public void DrawText(string text, Vector2 origin, in TextStyle style, float opacity) { }
        public void PushGroup(float opacity, BlendMode blend) { }
        public void PopGroup() { }
    }
}

public class ClosedPathStrokingTests
{
    [Fact]
    public void A_stroked_ellipse_has_no_gaps()
    {
        var surface = new RasterSurface(120, 120);
        surface.Clear(InkColor.White);
        var context = new RasterRenderContext(surface, Matrix3x2.Identity);
        context.StrokePath(new PathBuilder().AddEllipse(new Rect(20, 30, 80, 60)).ToGeometry(),
            new StrokePaint(InkColor.Black, 4f));

        // Walk the ellipse and check that ink is present all the way around.
        var center = new Vector2(60, 60);
        int gaps = 0;
        for (int i = 0; i < 360; i++)
        {
            float a = i * MathF.PI / 180f;
            var point = new Vector2(center.X + MathF.Cos(a) * 40f, center.Y + MathF.Sin(a) * 30f);
            bool ink = false;
            for (int dx = -3; dx <= 3 && !ink; dx++)
            {
                for (int dy = -3; dy <= 3 && !ink; dy++)
                {
                    if (surface.GetPixel((int)point.X + dx, (int)point.Y + dy).R < 128) ink = true;
                }
            }
            if (!ink) gaps++;
        }
        Assert.True(gaps == 0, $"the ellipse outline has {gaps} gaps out of 360 sampled positions");
    }

    [Fact]
    public void A_stroked_closed_rectangle_has_no_gaps()
    {
        var surface = new RasterSurface(120, 120);
        surface.Clear(InkColor.White);
        var context = new RasterRenderContext(surface, Matrix3x2.Identity);
        context.StrokePath(new PathBuilder().AddRect(new Rect(20, 20, 80, 80)).ToGeometry(),
            new StrokePaint(InkColor.Black, 4f));

        int gaps = 0;
        for (int x = 22; x < 98; x++)
        {
            if (surface.GetPixel(x, 20).R > 200) gaps++;
        }
        Assert.Equal(0, gaps);
    }

    [Fact]
    public void A_dash_pattern_does_not_leak_into_the_next_path()
    {
        var surface = new RasterSurface(120, 120);
        surface.Clear(InkColor.White);
        var context = new RasterRenderContext(surface, Matrix3x2.Identity);

        context.StrokePath(new PathBuilder().MoveTo(10, 30).LineTo(110, 30).ToGeometry(),
            new StrokePaint(InkColor.Black, 3f, 1f, BlendMode.Normal, LineCap.Butt, LineJoin.Miter, [6f, 4f]));
        context.StrokePath(new PathBuilder().MoveTo(10, 70).LineTo(110, 70).ToGeometry(),
            new StrokePaint(InkColor.Black, 3f));

        int gaps = 0;
        for (int x = 12; x < 108; x++) if (surface.GetPixel(x, 70).R > 200) gaps++;
        Assert.Equal(0, gaps);
    }
}
