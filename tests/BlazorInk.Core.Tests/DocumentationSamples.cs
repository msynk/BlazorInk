using System.IO.Compression;
using System.Numerics;
using BlazorInk.Brushes;
using BlazorInk.Collaboration;
using BlazorInk.Export;
using BlazorInk.Geometry;
using BlazorInk.Input;
using BlazorInk.Operations;
using BlazorInk.Raster;
using BlazorInk.Rendering;
using BlazorInk.Serialization;
using BlazorInk.Tools;
using Xunit;

namespace BlazorInk.Tests.Core;

/// <summary>
/// The code the documentation tells people to write, compiled and run. Documentation that no longer matches the
/// API is a bug, and the only way to keep it from drifting silently is to make the compiler read it too. Each
/// method mirrors one section of one document; when an API changes, this file fails to build and says which page
/// has to be updated.
/// </summary>
public class DocumentationSamples
{
    private static InkDocument Sample() => TestData.SampleDocument();

    [Fact]
    public void Export_document_svg_and_png()
    {
        InkDocument document = Sample();

        // docs/export.md - SVG
        string svg = SvgExporter.Export(document);
        byte[] bytes = SvgExporter.ExportToUtf8Bytes(document);
        using var stream = new MemoryStream();
        SvgExporter.Export(document, stream);

        Assert.StartsWith("<svg", svg, StringComparison.Ordinal);
        Assert.NotEmpty(bytes);
        Assert.True(stream.Length > 0);

        // docs/export.md - SVG options
        var svgOptions = new SvgExportOptions
        {
            Region = ExportRegion.Content,
            Padding = 12f,
            Background = InkColor.Transparent,
            Decimals = 2,
            IncludeXmlDeclaration = true,
            Title = "Signature",
            IncludeElementDescriptions = true,
            SelectedIds = document.AllElements.Select(e => e.Id).ToArray(),
        };
        Assert.StartsWith("<?xml", SvgExporter.Export(document, svgOptions), StringComparison.Ordinal);

        // docs/export.md - PNG
        byte[] png = PngExporter.Export(document, new PngExportOptions { Scale = 2f });
        Assert.NotEmpty(png);

        PngExportResult result = PngExporter.ExportWithDetails(document);
        if (!result.IsComplete)
        {
            Assert.True(result.SkippedTextElements >= 0 && result.SkippedImages >= 0);
        }

        // docs/export.md - PNG options
        var pngOptions = new PngExportOptions
        {
            Region = ExportRegion.Content,
            Padding = 8f,
            Scale = 3f,
            Background = InkColor.Transparent,
            PixelsPerMeter = 11811,
            MaxPixels = 80_000_000,
            Compression = CompressionLevel.SmallestSize,
        };
        Assert.NotEmpty(PngExporter.Export(document, pngOptions));
    }

    [Fact]
    public async Task Serialization_round_trip_and_limits()
    {
        InkDocument document = Sample();

        // docs/serialization.md
        string json = InkSerializer.Serialize(document);
        InkDocument reloaded = InkSerializer.Deserialize(json);

        byte[] utf8 = InkSerializer.SerializeToUtf8Bytes(document);
        using var stream = new MemoryStream();
        await InkSerializer.SerializeAsync(document, stream);
        stream.Position = 0;
        InkDocument loaded = await InkSerializer.DeserializeAsync(stream);

        Assert.True(InkSerializer.TryDeserialize(json, out InkDocument? d, out string? error));
        Assert.NotNull(d);
        Assert.Null(error);
        Assert.NotEmpty(utf8);
        Assert.Equal(document.ElementCount, reloaded.ElementCount);
        Assert.Equal(document.ElementCount, loaded.ElementCount);

        string readable = InkSerializer.Serialize(document, InkSerializerOptions.Readable);
        Assert.Contains("\n", readable, StringComparison.Ordinal);

        var options = new InkDeserializerOptions
        {
            Limits = new DocumentLimits { MaxElements = 5_000, MaxPointsPerStroke = 20_000 },
            AllowRemoteImages = false,
            AllowSvgImages = false,
        };
        Assert.NotNull(InkSerializer.Deserialize(json, options));
    }

    [Fact]
    public void Metadata_and_descriptions()
    {
        InkDocument document = Sample();
        Stroke stroke = document.AllElements.OfType<Stroke>().First();

        // docs/serialization.md and docs/accessibility.md
        document.SetMetadata("case-id", "2026-114");
        document.ReplaceElement(stroke with
        {
            Description = "Signature of Jane Doe",
            Metadata = new Dictionary<string, string> { ["signer"] = "jane" },
        });
        document.AddElement(new LineShape(new Vector2(0, 0), new Vector2(10, 10)) { EndArrow = true, Description = "Arrow pointing at the total" });

        Assert.Equal("2026-114", document.Metadata["case-id"]);
        Assert.Equal("Signature of Jane Doe", document.GetElement(stroke.Id).GetAccessibleDescription());
    }

    [Fact]
    public void Selection_queries_and_layers()
    {
        InkDocument document = Sample();
        using var selection = new Selection(document);
        Guid elementId = document.AllElements.First().Id;
        Guid otherId = document.AllElements.Skip(1).First().Id;
        Guid thirdId = document.AllElements.Skip(2).First().Id;

        // docs/selection-and-layers.md
        selection.Set(elementId);
        selection.Add(otherId);
        selection.Toggle(thirdId);
        Rect bounds = selection.Bounds;
        foreach (InkElement element in selection.Elements) Assert.NotNull(element);
        selection.SelectAll();
        selection.Clear();
        Assert.False(bounds.IsUnset);

        var point = new Vector2(30, 30);
        var rect = new Rect(0, 0, 400, 300);
        InkElement? hit = DocumentQuery.HitTest(document, point, tolerance: 4f);
        List<InkElement> all = DocumentQuery.HitTestAll(document, point);
        List<InkElement> inside = DocumentQuery.FindInRect(document, rect, RectSelectionMode.Contain);
        Assert.NotNull(all);
        Assert.NotNull(inside);
        _ = hit;

        Layer annotations = document.AddLayer("Annotations");
        document.UpdateLayer(annotations.Id, p => p with { IsVisible = false });
        document.UpdateLayer(annotations.Id, p => p with { IsLocked = true });
        document.UpdateLayer(annotations.Id, p => p with { Opacity = 0.5f });
        document.UpdateLayer(annotations.Id, p => p with { Name = "Review notes" });
        document.ReorderLayer(annotations.Id, index: 0);
        document.RemoveLayer(annotations.Id);
        Assert.False(document.TryGetLayer(annotations.Id, out _));
    }

    [Fact]
    public void Brushes_tools_and_erasing()
    {
        // docs/drawing-and-brushes.md
        var pen = new PenBrush
        {
            Color = InkColor.Parse("#1d4ed8"),
            Width = 6f,
            Opacity = 1f,
            Smoothing = 0.5f,
            Spacing = 0.25f,
            SimulatePressure = true,
            TaperStart = 0f,
            TaperEnd = 6f,
            PressureCurve = PressureCurve.Pen,
        };
        Brush brush = pen with { Color = InkColor.Parse("#dc2626") };
        var writing = new PenBrush { Width = 3f, Smoothing = 0.35f, Spacing = 0.12f, TaperEnd = 5f };
        Assert.Equal(InkColor.Parse("#dc2626"), brush.Color);
        Assert.Equal(3f, writing.Width);

        Brush[] builtIn = [new PenBrush(), new PencilBrush(), new MarkerBrush(), new HighlighterBrush(), new PixelEraserBrush()];
        Assert.Equal(5, builtIn.Length);

        _ = PressureCurve.Pen;
        _ = PressureCurve.Linear;
        _ = PressureCurve.Constant;
        _ = new PressureCurve(PressureCurveKind.EaseIn, minFactor: 0.1f, maxFactor: 1f);
        _ = PressureCurve.FromSamples([0f, 0.1f, 0.45f, 0.8f, 1f]);

        // docs/getting-started.md and docs/erasing.md
        ITool tool = new DrawTool { SimplifyTolerance = 0.12f };
        tool = new EraserTool { Mode = EraserMode.Stroke, Radius = 12f };
        tool = new SelectTool();
        tool = new ShapeTool(ShapeKind.Arrow);
        Assert.Equal("shape-arrow", tool.Id);
        _ = new DrawTool { HonorPenEraserButton = false };

        // docs/erasing.md - the geometric primitive
        var stroke = TestData.Stroke(new Vector2(0, 0), new Vector2(100, 0));
        var fragments = new List<Stroke>();
        bool changed = StrokeEditor.EraseSegments(stroke, [new Vector2(50, 0)], 10f, fragments);
        Assert.True(changed);
        Assert.Equal(2, fragments.Count);

        // docs/mobile-and-stylus.md
        var pan = new PanTool { MinZoom = 0.1f, MaxZoom = 32f, EnablePinchZoom = true };
        Assert.Equal(0.1f, pan.MinZoom);
    }

    [Fact]
    public void Annotations_text_and_images()
    {
        var document = new InkDocument();

        // docs/annotations-and-images.md
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

        Rect[] lineRects = [new Rect(10, 10, 80, 14), new Rect(10, 26, 60, 14)];
        document.AddElement(new HighlightElement(lineRects)
        {
            Color = InkColor.Parse("#fde047"),
            Opacity = 0.5f,
            Description = "Highlighted: payment terms",
        });

        document.AddElement(new CalloutElement(
            box: new Rect(320, 100, 200, 70),
            anchor: new Vector2(280, 130),
            text: "Please confirm this clause.")
        {
            Style = new ShapeStyle { FillColor = InkColor.Parse("#fff9c4"), StrokeColor = InkColor.Parse("#785a00") },
        });

        document.AddElement(new TextElement(new Vector2(40, 40), "Approved")
        {
            Style = TextStyle.Default with { FontSize = 20f, Bold = true, Color = InkColor.Parse("#15803d") },
            Background = InkColor.Parse("#dcfce7"),
            Padding = 6f,
        });

        byte[] pngBytes = PngExporter.Export(new InkDocument(new DocumentSettings { Width = 4, Height = 4 }));
        ImageSource source = ImageSource.FromBytes(pngBytes, "image/png", width: 640, height: 400);
        document.AddElement(new ImageElement(source, new Rect(0, 0, 640, 400))
        {
            Crop = new Rect(100, 50, 400, 300),
            Opacity = 0.9f,
            Description = "Screenshot of the checkout page",
        });

        Guid imageLayer = document.Layers[0].Id;
        document.UpdateLayer(imageLayer, p => p with { IsLocked = true, Name = "Image" });
        Guid annotations = document.AddLayer("Annotations").Id;
        Assert.True(document.TryGetLayer(annotations, out _));

        Assert.True(ImageSource.TryCreate(source.Uri, 640, 400, allowSvg: false, out ImageSource? parsed));
        Assert.NotNull(parsed);
        Assert.False(ImageSource.TryCreate("javascript:alert(1)", 1, 1, allowSvg: false, out _));
    }

    /// <summary>docs/extending.md - a custom brush registered with the registry.</summary>
    public sealed record DashedBrush : Brush
    {
        public const string KindName = "dashed";

        public override string Kind => KindName;

        /// <summary>Length of each dash in document units.</summary>
        public float DashLength { get; init; } = 6f;

        public override StrokeGeometry BuildGeometry(StrokePointsView points, PathBuilder builder)
        {
            ArgumentNullException.ThrowIfNull(builder);
            builder.Reset();
            for (int i = 1; i < points.Count; i++)
            {
                if (i % 2 == 0) continue;
                builder.MoveTo(points.PositionAt(i - 1));
                builder.LineTo(points.PositionAt(i));
            }
            return new StrokeGeometry(
                builder.ToGeometry(),
                StrokeGeometryMode.Centerline,
                Width,
                LineCap.Round,
                LineJoin.Round,
                points.Bounds.Inflate(Width));
        }
    }

    /// <summary>docs/extending.md - a custom tool.</summary>
    public sealed class DotTool : ToolBase
    {
        public override string Id => "dot";

        public override string DisplayName => "Dot";

        public override string Cursor => "crosshair";

        public override bool OnPointerDown(IToolContext context, in InputSample sample)
        {
            ArgumentNullException.ThrowIfNull(context);
            Vector2 point = context.Viewport.ToDocument(sample.Position);
            context.Document.AddElement(
                new EllipseShape(Rect.FromCenter(point, 12f, 12f))
                {
                    Style = new ShapeStyle { FillColor = context.ShapeStyle.StrokeColor, StrokeWidth = 0f },
                },
                context.ActiveLayerId);
            context.InvalidateDocument();
            return true;
        }
    }

    /// <summary>docs/extending.md - a text rasterizer for the .NET PNG exporter.</summary>
    public sealed class NullTextRasterizer : ITextRasterizer
    {
        public void DrawText(RasterSurface surface, string text, Vector2 origin, in TextStyle style, float scale, float opacity, Rect clip)
        {
        }

        public Vector2? Measure(string text, in TextStyle style) => null;
    }

    [Fact]
    public void Extending_the_framework()
    {
        var registry = new BrushRegistry();
        registry.Register<DashedBrush>();
        Assert.True(registry.TryCreate(DashedBrush.KindName, out Brush? created));
        Assert.IsType<DashedBrush>(created);

        var stroke = new Stroke(TestData.Line(new Vector2(0, 0), new Vector2(60, 60), 8), new DashedBrush { Width = 3f });
        Assert.False(stroke.Geometry.IsEmpty);

        using var context = new TestToolContext();
        var tool = new DotTool();
        tool.Activate(context);
        Assert.True(tool.OnPointerDown(context, new InputSample { X = 50, Y = 50, Pressure = 0.5f, Buttons = 1 }));
        Assert.Equal(1, context.Document.ElementCount);

        Assert.NotEmpty(PngExporter.Export(context.Document, new PngExportOptions { TextRasterizer = new NullTextRasterizer() }));
        DocumentRenderer.Render(context.Document, new SvgRenderContext(TextWriter.Null));
    }

    [Fact]
    public void Collaboration_surface()
    {
        // docs/collaboration.md - the shapes the page documents, without a transport.
        var presence = new PresenceState
        {
            ParticipantId = "alex",
            Name = "Alex",
            Color = "#2563eb",
            Cursor = new Vector2(10, 20),
            LiveStroke = [new Vector2(0, 0), new Vector2(5, 5)],
            LiveStrokeColor = "#2563eb",
            LiveStrokeWidth = 4f,
        };
        Assert.Equal("alex", presence.ParticipantId);

        var document = new InkDocument();
        var stroke = TestData.Stroke(new Vector2(0, 0), new Vector2(10, 10));
        DrawingOperation operation = new AddElementOperation(document.Layers[0].Id, stroke);
        string json = OperationCodec.Serialize(operation);
        DrawingOperation received = OperationCodec.Deserialize(json);
        Assert.IsType<AddElementOperation>(received);
    }
}
