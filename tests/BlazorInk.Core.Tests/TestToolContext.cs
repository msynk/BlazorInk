using System.Numerics;
using BlazorInk.Brushes;
using BlazorInk.Geometry;
using BlazorInk.History;
using BlazorInk.Input;
using BlazorInk.Tools;

namespace BlazorInk.Tests.Core;

/// <summary>A tool context for tests: a real document, history and selection with no browser involved.</summary>
internal sealed class TestToolContext : IToolContext, IDisposable
{
    public TestToolContext(InkDocument? document = null)
    {
        Document = document ?? new InkDocument(new DocumentSettings { Width = 800, Height = 600 });
        History = new HistoryManager(Document);
        Selection = new Selection(Document);
        ActiveLayerId = Document.Layers[0].Id;
    }

    public InkDocument Document { get; }

    public Selection Selection { get; }

    public HistoryManager History { get; }

    public Viewport Viewport { get; set; } = Viewport.Identity;

    public Guid ActiveLayerId { get; set; }

    public Brush Brush { get; set; } = new PenBrush();

    public ShapeStyle ShapeStyle { get; set; } = ShapeStyle.Default;

    public Vector2 ViewSize { get; set; } = new(800, 600);

    public int OverlayInvalidations { get; private set; }

    public int DocumentInvalidations { get; private set; }

    public List<string> Announcements { get; } = new();

    public string Cursor { get; private set; } = "default";

    public HashSet<int> CapturedPointers { get; } = new();

    public void InvalidateOverlay() => OverlayInvalidations++;

    public void InvalidateDocument(Rect? region = null) => DocumentInvalidations++;

    public void CapturePointer(int pointerId) => CapturedPointers.Add(pointerId);

    public void ReleasePointer(int pointerId) => CapturedPointers.Remove(pointerId);

    public void SetCursor(string cursor) => Cursor = cursor;

    public void Announce(string message) => Announcements.Add(message);

    /// <summary>Runs a complete gesture through a tool: down, a series of moves, then up.</summary>
    public void Gesture(ITool tool, PointerKind kind, params (float X, float Y, float Pressure)[] points)
    {
        ArgumentNullException.ThrowIfNull(tool);
        double time = 0;
        InputSample Make(int index, PointerPhase phase) => new()
        {
            PointerId = 1,
            Kind = kind,
            Phase = phase,
            X = points[index].X,
            Y = points[index].Y,
            Pressure = points[index].Pressure,
            Timestamp = time,
            Buttons = phase == PointerPhase.Up ? 0 : 1,
            IsPrimary = true,
        };

        tool.OnPointerDown(this, Make(0, PointerPhase.Down));
        for (int i = 1; i < points.Length - 1; i++)
        {
            time += 8;
            tool.OnPointerMove(this, Make(i, PointerPhase.Move));
        }
        time += 8;
        tool.OnPointerUp(this, Make(points.Length - 1, PointerPhase.Up));
    }

    /// <summary>Runs a straight-line gesture with a given number of samples.</summary>
    public void Line(ITool tool, Vector2 from, Vector2 to, int samples = 20, PointerKind kind = PointerKind.Mouse, float pressure = 0.5f)
    {
        var points = new (float, float, float)[samples];
        for (int i = 0; i < samples; i++)
        {
            Vector2 p = Vector2.Lerp(from, to, samples == 1 ? 0 : i / (float)(samples - 1));
            points[i] = (p.X, p.Y, pressure);
        }
        Gesture(tool, kind, points);
    }

    public void Dispose()
    {
        History.Dispose();
        Selection.Dispose();
    }
}
