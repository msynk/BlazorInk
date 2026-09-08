using System.Numerics;
using BenchmarkDotNet.Attributes;
using BlazorInk.Brushes;
using BlazorInk.Canvas;
using BlazorInk.Export;
using BlazorInk.Geometry;
using BlazorInk.History;
using BlazorInk.Input;
using BlazorInk.Rendering;
using BlazorInk.Serialization;

namespace BlazorInk.Benchmarks;

/// <summary>
/// The input path: what happens between a pointer event arriving and a point being stored. This runs on every
/// animation frame while drawing, so it must not allocate.
/// </summary>
[MemoryDiagnoser]
public class InputBenchmarks
{
    private InputSample[] _frame = null!;
    private readonly StrokeBuilder _builder = new();
    private readonly Brush _brush = new PenBrush { Width = 4f };

    /// <summary>Samples in one animation frame. 4 is a mouse at 60 Hz; 16 is a 240 Hz pen with coalescing.</summary>
    [Params(4, 16, 64)]
    public int SamplesPerFrame { get; set; }

    [GlobalSetup]
    public void Setup() => _frame = Workloads.Frame(SamplesPerFrame);

    /// <summary>Processing one frame of pointer samples into stored points.</summary>
    [Benchmark]
    public int ProcessFrame()
    {
        _builder.Begin(_frame[0], _brush, Matrix3x2.Identity);
        for (int i = 1; i < _frame.Length; i++) _builder.Add(_frame[i]);
        int count = _builder.Count;
        _builder.Cancel();
        return count;
    }

    /// <summary>Decoding the packed samples that arrive from the browser.</summary>
    [Benchmark]
    public float DecodeSamples()
    {
        Span<double> packed = stackalloc double[InputSample.PackedSize];
        float sum = 0;
        foreach (InputSample sample in _frame)
        {
            sample.WriteTo(packed);
            sum += InputSample.ReadFrom(packed).X;
        }
        return sum;
    }
}

/// <summary>
/// Turning stored points into renderable geometry. This is the most expensive step per stroke, and it is cached,
/// so it happens once per stroke rather than once per frame.
/// </summary>
[MemoryDiagnoser]
public class StrokeGenerationBenchmarks
{
    private StrokePoints _handwriting = null!;
    private StrokePoints _long = null!;
    private readonly PathBuilder _builder = new(4096);
    private readonly Brush _pen = new PenBrush { Width = 4f };
    private readonly Brush _marker = new MarkerBrush { Width = 8f };

    [GlobalSetup]
    public void Setup()
    {
        _handwriting = Workloads.HandwritingStroke(new Random(1));
        _long = Workloads.LongStroke(PointsInLongStroke);
    }

    /// <summary>Points in the long-stroke case: a continuous scribble that never lifts.</summary>
    [Params(1_000, 10_000)]
    public int PointsInLongStroke { get; set; }

    /// <summary>A typical handwriting stroke (60 points) as a variable-width outline.</summary>
    [Benchmark]
    public int VariableWidthOutline() => _pen.BuildGeometry(_handwriting.AsView(), _builder).Path.VerbCount;

    /// <summary>A constant-width centerline stroke, which produces far less geometry.</summary>
    [Benchmark]
    public int Centerline() => _marker.BuildGeometry(_handwriting.AsView(), _builder).Path.VerbCount;

    /// <summary>A very long stroke, the worst case for outline generation.</summary>
    [Benchmark]
    public int LongStrokeOutline() => _pen.BuildGeometry(_long.AsView(), _builder).Path.VerbCount;

    /// <summary>Simplification, which runs once when a stroke is committed.</summary>
    [Benchmark]
    public int Simplify() => _long.Simplify(0.1f).Count;
}

/// <summary>Recording a redraw: this runs whenever the document or the viewport changes.</summary>
[MemoryDiagnoser]
public class RenderBenchmarks
{
    private InkDocument _document = null!;
    private CanvasRenderContext _context = null!;
    private Rect _visible;

    /// <summary>Strokes in the document.</summary>
    [Params(500, 5_000)]
    public int Strokes { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _document = Workloads.Document(Strokes);
        _context = new CanvasRenderContext(1 << 20);
        _visible = new Rect(0, 0, 900, 600);

        // Warm the geometry cache: the first render of a document pays for geometry, later ones do not.
        foreach (InkElement element in _document.AllElements)
        {
            if (element is Stroke stroke) _ = stroke.Geometry;
        }
    }

    /// <summary>Recording every element, which is what a full-page export does.</summary>
    [Benchmark]
    public int RecordEverything()
    {
        _context.Reset();
        DocumentRenderer.Render(_document, _context);
        return _context.Count;
    }

    /// <summary>Recording only what is on screen, which is what an interactive redraw does.</summary>
    [Benchmark(Baseline = true)]
    public int RecordVisibleRegion()
    {
        _context.Reset();
        DocumentRenderer.Render(_document, _context, new RenderOptions { ClipBounds = _visible });
        return _context.Count;
    }
}

/// <summary>Hit testing and rectangle selection over a full document.</summary>
[MemoryDiagnoser]
public class QueryBenchmarks
{
    private InkDocument _document = null!;

    /// <summary>Strokes in the document.</summary>
    [Params(1_000, 10_000)]
    public int Strokes { get; set; }

    [GlobalSetup]
    public void Setup() => _document = Workloads.Document(Strokes);

    /// <summary>One click with the selection tool.</summary>
    [Benchmark]
    public bool HitTest() => DocumentQuery.HitTest(_document, new Vector2(760f, 480f), 4f) is not null;

    /// <summary>A marquee selection over a quarter of the page.</summary>
    [Benchmark]
    public int MarqueeSelect() => DocumentQuery.FindInRect(_document, new Rect(200, 200, 600, 400)).Count;
}

/// <summary>Saving, loading and exporting.</summary>
[MemoryDiagnoser]
public class SerializationBenchmarks
{
    private InkDocument _document = null!;
    private byte[] _saved = null!;
    private StrokePoints _points = null!;
    private byte[] _encodedPoints = null!;

    /// <summary>Strokes in the document.</summary>
    [Params(1_000, 10_000)]
    public int Strokes { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _document = Workloads.Document(Strokes);
        _saved = InkSerializer.SerializeToUtf8Bytes(_document);
        _points = Workloads.LongStroke(10_000);
        _encodedPoints = PointCodec.Encode(_points);
    }

    /// <summary>Writing the whole document.</summary>
    [Benchmark]
    public int Save() => InkSerializer.SerializeToUtf8Bytes(_document).Length;

    /// <summary>Reading the whole document back.</summary>
    [Benchmark]
    public int Load() => InkSerializer.Deserialize(_saved).ElementCount;

    /// <summary>Encoding 10,000 points, the hot inner loop of saving.</summary>
    [Benchmark]
    public int EncodePoints() => PointCodec.Encode(_points).Length;

    /// <summary>Decoding 10,000 points.</summary>
    [Benchmark]
    public int DecodePoints() => PointCodec.Decode(_encodedPoints).Count;

    /// <summary>Exporting the document as SVG.</summary>
    [Benchmark]
    public int ExportSvg() => SvgExporter.Export(_document).Length;
}

/// <summary>Raster export with the built-in rasterizer, used on servers and in tests.</summary>
[MemoryDiagnoser]
public class RasterBenchmarks
{
    private InkDocument _document = null!;

    /// <summary>Strokes in the document.</summary>
    [Params(200, 2_000)]
    public int Strokes { get; set; }

    [GlobalSetup]
    public void Setup() => _document = Workloads.Document(Strokes);

    /// <summary>Rendering to pixels without encoding, which isolates the rasterizer.</summary>
    [Benchmark]
    public int Rasterize() => PngExporter.Render(_document, new PngExportOptions { Scale = 1f }, out _, out _).Width;

    /// <summary>Full PNG export including compression.</summary>
    [Benchmark]
    public int ExportPng() => PngExporter.Export(_document, new PngExportOptions { Scale = 1f }).Length;
}

/// <summary>Document mutation and undo, which must not copy the document.</summary>
[MemoryDiagnoser]
public class HistoryBenchmarks : IDisposable
{
    private InkDocument _document = null!;
    private HistoryManager _history = null!;
    private Stroke[] _strokes = null!;

    [GlobalSetup]
    public void Setup()
    {
        _document = Workloads.Document(1_000);
        _history = new HistoryManager(_document, limit: 10_000);
        var random = new Random(7);
        _strokes = new Stroke[100];
        for (int i = 0; i < _strokes.Length; i++)
        {
            _strokes[i] = new Stroke(Workloads.HandwritingStroke(random), new PenBrush());
        }
    }

    [GlobalCleanup]
    public void Cleanup() => _history.Dispose();

    /// <summary>Releases the history manager.</summary>
    public void Dispose()
    {
        _history?.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>Adding 100 strokes and undoing all of them, the cost of a burst of edits.</summary>
    [Benchmark]
    public int AddAndUndo()
    {
        Guid layer = _document.Layers[0].Id;
        foreach (Stroke stroke in _strokes)
        {
            _document.AddElement(stroke with { Id = Guid.NewGuid() }, layer);
        }
        int count = 0;
        while (_history.CanUndo && count < _strokes.Length)
        {
            _history.Undo();
            count++;
        }
        return count;
    }
}
