using BlazorInk.Brushes;
using BlazorInk.Geometry;
using BlazorInk.Input;
using BlazorInk.Rendering;

namespace BlazorInk.Tools;

/// <summary>
/// The freehand drawing tool. Captures pointer input into a live stroke, draws it on the overlay while the pen is
/// down, and commits it to the document as one undoable operation when the pointer lifts.
/// </summary>
/// <remarks>
/// The live stroke is rendered on the overlay rather than the document surface, so nothing in the document has to be
/// redrawn while drawing. On commit the stroke moves to the document surface and its already-built geometry is reused.
/// </remarks>
public class DrawTool : ToolBase
{
    private readonly StrokeBuilder _builder = new();
    private readonly PathBuilder _pathBuilder = new(512);
    private StrokeGeometry? _liveGeometry;
    private Guid _targetLayerId;
    private int _lastGeometryCount = -1;
    private Rect _liveBounds = Rect.Empty;

    /// <inheritdoc />
    public override string Id => "draw";

    /// <inheritdoc />
    public override string DisplayName => "Draw";

    /// <inheritdoc />
    public override bool WantsCoalescedInput => true;

    /// <summary>True while a stroke is being drawn.</summary>
    public bool IsDrawing => _builder.IsActive;

    /// <summary>
    /// Simplification tolerance in document units applied when the stroke is committed. Removes points that do not
    /// change the shape, which shrinks documents noticeably. Zero disables simplification.
    /// </summary>
    public float SimplifyTolerance { get; set; } = 0.12f;

    /// <summary>
    /// Pressure tolerance used when simplifying, as a fraction of the 0..1 pressure range. Simplification treats
    /// pressure as its own channel so a straight stroke drawn with changing pressure keeps its width variation.
    /// </summary>
    public float SimplifyPressureTolerance { get; set; } = 0.04f;

    /// <summary>Whether a pen's eraser button switches this tool to erasing for the duration of the stroke.</summary>
    public bool HonorPenEraserButton { get; set; } = true;

    /// <summary>The brush used for the current stroke (the context brush, or the eraser when the pen is flipped).</summary>
    protected Brush? ActiveBrush { get; private set; }

    /// <inheritdoc />
    public override bool OnPointerDown(IToolContext context, in InputSample sample)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (_builder.IsActive) return false;
        if (context.Document.TryGetLayer(context.ActiveLayerId, out Layer? layer) && (layer.IsLocked || !layer.IsVisible)) return false;

        Brush brush = SelectBrush(context, sample);
        ActiveBrush = brush;
        _targetLayerId = context.ActiveLayerId;
        _builder.Begin(sample, brush, context.Viewport.ViewToDocument);
        _liveGeometry = null;
        _lastGeometryCount = -1;
        _liveBounds = Rect.Empty;
        context.CapturePointer(sample.PointerId);
        context.InvalidateOverlay();
        return true;
    }

    /// <summary>Chooses the brush for a stroke. Override to vary the brush by device or button.</summary>
    protected virtual Brush SelectBrush(IToolContext context, in InputSample sample)
    {
        if (HonorPenEraserButton && sample.Kind == PointerKind.Pen && sample.IsEraserButton)
        {
            return new PixelEraserBrush { Width = MathF.Max(context.Brush.Width * 4f, 16f) };
        }
        return context.Brush;
    }

    /// <inheritdoc />
    public override void OnPointerMove(IToolContext context, in InputSample sample)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!_builder.IsActive || sample.PointerId != _builder.PointerId) return;
        if (_builder.Add(sample)) context.InvalidateOverlay();
    }

    /// <inheritdoc />
    public override void OnPointerUp(IToolContext context, in InputSample sample)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!_builder.IsActive || sample.PointerId != _builder.PointerId) return;
        context.ReleasePointer(sample.PointerId);
        StrokePoints points = _builder.End(sample);
        Brush brush = ActiveBrush ?? context.Brush;
        ActiveBrush = null;
        _liveGeometry = null;
        _lastGeometryCount = -1;
        CommitStroke(context, points, brush);
        context.InvalidateOverlay();
    }

    /// <summary>Creates and adds the stroke element. Override to attach metadata or post-process points.</summary>
    protected virtual void CommitStroke(IToolContext context, StrokePoints points, Brush brush)
    {
        if (points.Count == 0) return;
        StrokePoints final = SimplifyTolerance > 0 ? points.Simplify(SimplifyTolerance, SimplifyPressureTolerance) : points;
        var stroke = new Stroke(final, brush) { Input = _builder.InputInfo };

        // The stroke belongs to the layer it started on. Re-reading the active layer here would silently move it
        // if the layer changed, or was locked, while the pen was down.
        Guid layerId = context.Document.TryGetLayer(_targetLayerId, out Layer? layer) && !layer.IsLocked && layer.IsVisible
            ? _targetLayerId
            : context.ActiveLayerId;

        if (!TryApply(context, () => context.Document.AddElement(stroke, layerId))) return;
        context.InvalidateDocument(stroke.Bounds);
    }

    /// <summary>
    /// Applies a document change, reporting a refusal instead of letting it escape. Document limits throw, and an
    /// exception here would unwind through the browser callback and strand the gesture.
    /// </summary>
    protected static bool TryApply(IToolContext context, Action change)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(change);
        try
        {
            change();
            return true;
        }
        catch (InvalidOperationException ex)
        {
            context.Announce("That change could not be applied: " + ex.Message);
            return false;
        }
    }

    /// <inheritdoc />
    public override void OnPointerCancel(IToolContext context, in InputSample sample)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!_builder.IsActive) return;
        context.ReleasePointer(sample.PointerId);
        _builder.Cancel();
        ActiveBrush = null;
        _liveGeometry = null;
        _lastGeometryCount = -1;
        context.InvalidateOverlay();
    }

    /// <inheritdoc />
    public override void Deactivate(IToolContext context)
    {
        // A tool that is swapped out mid-stroke must not leave a live builder behind: the next activation would
        // resume a stroke the user abandoned, and the overlay would keep showing it.
        if (_builder.IsActive)
        {
            if (_builder.PointerId >= 0) context?.ReleasePointer(_builder.PointerId);
            _builder.Cancel();
        }
        ActiveBrush = null;
        _liveGeometry = null;
        _lastGeometryCount = -1;
        context?.InvalidateOverlay();
    }

    /// <inheritdoc />
    public override void DrawOverlay(IToolContext context, IRenderContext render)
    {
        ArgumentNullException.ThrowIfNull(render);
        if (!_builder.IsActive || _builder.Count == 0) return;
        Brush brush = ActiveBrush ?? context.Brush;
        StrokeGeometry geometry = GetLiveGeometry(brush);
        _liveBounds = geometry.Bounds;
        if (brush.BlendMode == BlendMode.DestinationOut)
        {
            // An eraser cannot erase on the overlay (there is nothing under it), so show its outline instead.
            render.StrokePath(geometry.Path, new StrokePaint(new InkColor(120, 120, 120), 1f, 0.9f, BlendMode.Normal, LineCap.Round, LineJoin.Round));
            return;
        }
        geometry.Draw(render, brush.Color, brush.Opacity, brush.BlendMode);
    }

    private StrokeGeometry GetLiveGeometry(Brush brush)
    {
        // The live stroke changes every frame, so geometry is rebuilt into a reused path builder rather than cached.
        if (_liveGeometry is not null && _lastGeometryCount == _builder.Count) return _liveGeometry;
        _liveGeometry = brush.BuildGeometry(_builder.Points.AsView(), _pathBuilder);
        _lastGeometryCount = _builder.Count;
        return _liveGeometry;
    }

    /// <summary>Bounds of the live stroke in document units, used by the host to size overlay redraws.</summary>
    public Rect LiveBounds => _liveBounds;

    /// <inheritdoc />
    public override bool OnKeyDown(IToolContext context, ToolKeyEvent key)
    {
        if (key.Key == "Escape" && _builder.IsActive)
        {
            _builder.Cancel();
            ActiveBrush = null;
            _liveGeometry = null;
            context?.InvalidateOverlay();
            return true;
        }
        return false;
    }
}
