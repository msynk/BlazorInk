using System.Numerics;
using BlazorInk.Brushes;
using BlazorInk.Canvas;
using BlazorInk.Export;
using BlazorInk.Geometry;
using BlazorInk.History;
using BlazorInk.Input;
using BlazorInk.Interop;
using BlazorInk.Operations;
using BlazorInk.Rendering;
using BlazorInk.Tools;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace BlazorInk;

/// <summary>
/// A drawing surface: pointer, touch and stylus input, high-quality ink rendering, tools, undo/redo and export.
/// </summary>
/// <remarks>
/// <para>
/// The component is a thin shell around the engine in <c>BlazorInk.Core</c>. It owns two canvases: the document
/// surface holding committed elements, and an overlay holding the live stroke and tool previews. Pointer input
/// never triggers a Blazor render: samples arrive from the browser once per animation frame, the active tool
/// processes them, and only the affected canvas layer is redrawn.
/// </para>
/// <example>
/// A signature pad:
/// <code>
/// &lt;InkCanvas @ref="canvas" Width="400px" Height="180px" AriaLabel="Signature" /&gt;
/// @code {
///     private InkCanvas? canvas;
///     private Task ClearAsync() =&gt; canvas!.ClearAsync();
///     private Task&lt;string&gt; SaveAsync() =&gt; canvas!.ExportSvgAsync();
/// }
/// </code>
/// </example>
/// </remarks>
public sealed partial class InkCanvas : ComponentBase, IAsyncDisposable, IInkInteropClient
{
    private readonly CanvasRenderContext _documentContext = new(8192);
    private readonly CanvasRenderContext _overlayContext = new(2048);
    private readonly ToolContext _toolContext;
    private readonly string _elementId = "bi-" + Guid.NewGuid().ToString("N")[..12];

    private ElementReference _container;
    private InkInterop? _interop;
    private InkDocument _document = null!;
    private HistoryManager _history = null!;
    private Selection _selection = null!;
    private ITool _activeTool = null!;
    private Brush _brush = new PenBrush();
    private ShapeStyle _shapeStyle = ShapeStyle.Default;
    private Viewport _viewport = Viewport.Identity;
    private Guid _activeLayerId;
    private InputCapabilities _capabilities = InputCapabilities.None;

    private double _cssWidth;
    private double _cssHeight;
    private double _devicePixelRatio = 1;
    private bool _documentDirty = true;
    private bool _overlayDirty = true;
    private bool _attached;
    private bool _renderScheduled;
    private bool _uiDirty;
    private bool _disposed;
    private string _announcement = string.Empty;
    private string _cursor = "crosshair";
    private ITool? _previousToolParameter;
    private InkDocument? _previousDocumentParameter;
    private Viewport _previousViewportParameter = Viewport.Identity;
    private TextEditorState? _textEditor;
    private Stroke? _lastAddedStroke;
    private bool _watchForAddedStroke;
    private ElementReference _textEditorElement;
    private bool _focusTextEditor;

    /// <summary>Creates the component.</summary>
    public InkCanvas()
    {
        _toolContext = new ToolContext(this);
    }

    [Inject]
    private IJSRuntime JSRuntime { get; set; } = null!;

    [Inject]
    private NavigationManager Navigation { get; set; } = null!;

    // ---- Parameters -------------------------------------------------------------------------------------------

    /// <summary>
    /// The document to edit. When null the component creates and owns one, which is what a signature pad or a
    /// simple drawing field wants.
    /// </summary>
    [Parameter]
    public InkDocument? Document { get; set; }

    /// <summary>The active tool. Defaults to freehand drawing.</summary>
    [Parameter]
    public ITool? Tool { get; set; }

    /// <summary>The brush used by drawing tools.</summary>
    [Parameter]
    public Brush? Brush { get; set; }

    /// <summary>The style applied to newly created shapes and annotations.</summary>
    [Parameter]
    public ShapeStyle? ShapeStyle { get; set; }

    /// <summary>The layer new elements are added to. Defaults to the top-most editable layer.</summary>
    [Parameter]
    public Guid? ActiveLayerId { get; set; }

    /// <summary>The viewport (pan and zoom). Supports two-way binding.</summary>
    [Parameter]
    public Viewport Viewport { get; set; } = Viewport.Identity;

    /// <summary>Raised when the viewport changes, for two-way binding.</summary>
    [Parameter]
    public EventCallback<Viewport> ViewportChanged { get; set; }

    /// <summary>When true, input is ignored and the drawing is display-only.</summary>
    [Parameter]
    public bool ReadOnly { get; set; }

    /// <summary>Maximum number of undo steps. Zero disables history.</summary>
    [Parameter]
    public int HistoryLimit { get; set; } = 200;

    /// <summary>CSS width of the canvas element, for example <c>"100%"</c> or <c>"400px"</c>.</summary>
    [Parameter]
    public string Width { get; set; } = "100%";

    /// <summary>CSS height of the canvas element.</summary>
    [Parameter]
    public string Height { get; set; } = "300px";

    /// <summary>Extra CSS classes for the container.</summary>
    [Parameter]
    public string? Class { get; set; }

    /// <summary>Extra inline styles for the container.</summary>
    [Parameter]
    public string? Style { get; set; }

    /// <summary>Accessible name of the canvas.</summary>
    [Parameter]
    public string AriaLabel { get; set; } = "Drawing canvas";

    /// <summary>Whether the canvas can receive keyboard focus. Required for keyboard shortcuts.</summary>
    [Parameter]
    public bool Focusable { get; set; } = true;

    /// <summary>Whether the built-in keyboard shortcuts (undo, redo, delete, select all, escape) are handled.</summary>
    [Parameter]
    public bool EnableKeyboardShortcuts { get; set; } = true;

    /// <summary>
    /// Whether two-finger pan and pinch zoom are handled by the canvas. When false, touch gestures scroll the page
    /// instead, which is usually what a small signature field in a form wants.
    /// </summary>
    [Parameter]
    public bool EnableTouchGestures { get; set; }

    /// <summary>Whether a finger touch is ignored while a pen is in contact, so a resting palm does not draw.</summary>
    [Parameter]
    public bool RejectTouchWhilePen { get; set; } = true;

    /// <summary>
    /// Whether a provisional trail is drawn in the browser while a sample batch travels to the server. Ignored in
    /// WebAssembly, where the engine already runs locally. Defaults to on for interactive server rendering.
    /// </summary>
    [Parameter]
    public bool? WetInk { get; set; }

    /// <summary>Content rendered above the canvas, for toolbars or overlays positioned by the application.</summary>
    [Parameter]
    public RenderFragment? ChildContent { get; set; }

    /// <summary>Additional attributes applied to the container element.</summary>
    [Parameter(CaptureUnmatchedValues = true)]
    public IReadOnlyDictionary<string, object>? AdditionalAttributes { get; set; }

    // ---- Events -----------------------------------------------------------------------------------------------

    /// <summary>Raised when a stroke begins. Not raised for tools that do not draw strokes.</summary>
    [Parameter]
    public EventCallback<StrokeEventArgs> StrokeStarted { get; set; }

    /// <summary>Raised when a stroke is committed to the document.</summary>
    [Parameter]
    public EventCallback<StrokeEventArgs> StrokeCompleted { get; set; }

    /// <summary>Raised after any change to the document.</summary>
    [Parameter]
    public EventCallback<DocumentChangedEventArgs> DocumentChanged { get; set; }

    /// <summary>Raised when the selection changes.</summary>
    [Parameter]
    public EventCallback SelectionChanged { get; set; }

    /// <summary>Raised when the undo or redo stack changes.</summary>
    [Parameter]
    public EventCallback HistoryChanged { get; set; }

    /// <summary>Raised once the canvas is attached and the device capabilities are known.</summary>
    [Parameter]
    public EventCallback<InputCapabilities> Ready { get; set; }

    // ---- Public API -------------------------------------------------------------------------------------------

    /// <summary>The document being edited.</summary>
    public InkDocument CurrentDocument => _document;

    /// <summary>Undo and redo for this canvas.</summary>
    public HistoryManager History => _history;

    /// <summary>The current selection.</summary>
    public Selection Selection => _selection;

    /// <summary>What the current device and browser support. Valid once <see cref="Ready"/> has been raised.</summary>
    public InputCapabilities Capabilities => _capabilities;

    /// <summary>The active tool.</summary>
    public ITool ActiveTool => _activeTool;

    /// <summary>The current viewport.</summary>
    public Viewport CurrentViewport => _viewport;

    /// <summary>The canvas size in CSS pixels.</summary>
    public Vector2 ViewSize => new((float)_cssWidth, (float)_cssHeight);

    /// <summary>True when the zero-copy WebAssembly rendering path is in use.</summary>
    public bool UsesFastRenderPath => _interop?.UsesFastPath ?? false;

    /// <summary>The id of the container element, useful for labelling and testing.</summary>
    public string ElementId => _elementId;

    /// <summary>Undoes the last change. Returns false when there is nothing to undo.</summary>
    public bool Undo()
    {
        if (ReadOnly || !_history.Undo()) return false;
        Announce(_history.CanUndo ? "Undone." : "Undone. Nothing left to undo.");
        InvalidateAll();
        return true;
    }

    /// <summary>Redoes the last undone change. Returns false when there is nothing to redo.</summary>
    public bool Redo()
    {
        if (ReadOnly || !_history.Redo()) return false;
        Announce("Redone.");
        InvalidateAll();
        return true;
    }

    /// <summary>Removes every element, as one undoable step.</summary>
    public void Clear()
    {
        if (ReadOnly || _document.IsEmpty) return;
        _document.Clear();
        _selection.Clear();
        Announce("Canvas cleared.");
        InvalidateAll();
    }

    /// <summary>Deletes the selected elements.</summary>
    public void DeleteSelection()
    {
        if (ReadOnly || _selection.IsEmpty) return;
        int count = _selection.Count;
        _document.RemoveElements(_selection.Ids);
        Announce($"Deleted {count} elements.");
        InvalidateAll();
    }

    /// <summary>Replaces the document being edited and resets history and selection.</summary>
    public void LoadDocument(InkDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        AttachDocument(document);
        InvalidateAll();
    }

    /// <summary>Sets the viewport, notifying <see cref="ViewportChanged"/>.</summary>
    public void SetViewport(Viewport viewport)
    {
        if (_viewport == viewport) return;
        _viewport = viewport;
        InvalidateAll();
        if (ViewportChanged.HasDelegate) _ = ViewportChanged.InvokeAsync(viewport);
    }

    /// <summary>Fits the drawing into the visible area.</summary>
    public void ZoomToFit(float padding = 24f)
    {
        Rect bounds = _document.ContentBounds;
        if (bounds.IsEmpty) bounds = _document.Settings.PageRect;
        if (bounds.IsEmpty || _cssWidth <= 0) return;
        SetViewport(Viewport.Fit(bounds, ViewSize, padding));
        Announce("Zoomed to fit.");
    }

    /// <summary>Resets pan and zoom.</summary>
    public void ResetViewport() => SetViewport(Viewport.Identity);

    /// <summary>Zooms around the center of the view.</summary>
    public void ZoomBy(float factor) => SetViewport(_viewport.ZoomBy(factor, ViewSize / 2f));

    /// <summary>
    /// Exports the drawing as SVG. This is synchronous work: SVG generation happens entirely in .NET and needs
    /// nothing from the browser.
    /// </summary>
    public string ExportSvg(SvgExportOptions? options = null)
        => SvgExporter.Export(_document, ApplySelection(options ?? SvgExportOptions.Default));

    /// <summary>
    /// Exports the drawing as SVG. Provided for symmetry with <see cref="ExportPngAsync"/>; the work itself is
    /// synchronous, so <see cref="ExportSvg"/> is equivalent and avoids a needless state machine.
    /// </summary>
    public Task<string> ExportSvgAsync(SvgExportOptions? options = null) => Task.FromResult(ExportSvg(options));

    private SvgExportOptions ApplySelection(SvgExportOptions options)
        => options.Region == ExportRegion.Selection && options.SelectedIds is null
            ? options with { SelectedIds = _selection.Ids }
            : options;

    /// <summary>
    /// Exports the drawing as a PNG. Rendering happens in the browser, so real fonts and every image format the
    /// browser supports are included.
    /// </summary>
    /// <param name="options">Region, scale and background.</param>
    /// <param name="cancellationToken">Cancels the export.</param>
    public async Task<byte[]> ExportPngAsync(PngExportOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= PngExportOptions.Default;
        if (_interop is null) return PngExporter.Export(_document, options);

        Rect region = ResolveExportRegion(options);
        float scale = options.Scale > 0 ? options.Scale : 1f;
        int width = Math.Max((int)MathF.Ceiling(region.Width * scale), 1);
        int height = Math.Max((int)MathF.Ceiling(region.Height * scale), 1);
        if ((long)width * height > options.MaxPixels)
            throw new InvalidOperationException($"Exporting {width} by {height} pixels exceeds the limit of {options.MaxPixels} pixels. Lower the scale or export a smaller region.");

        var context = new CanvasRenderContext(Math.Max(4096, _document.ElementCount * 64));
        InkColor background = options.Background ?? _document.Settings.Background;
        if (!background.IsTransparent) context.Clear(background);
        context.SetTransform(Matrix3x2.CreateTranslation(-region.X, -region.Y) * Matrix3x2.CreateScale(scale));
        DocumentRenderer.Render(_document, context, new RenderOptions
        {
            ClipBounds = region,
            IncludeHiddenLayers = options.IncludeHiddenLayers,
            ElementFilter = options.Region == ExportRegion.Selection && options.SelectedIds is null ? _selection.Ids : options.SelectedIds,
        });

        await EnsureImagesLoadedAsync(cancellationToken).ConfigureAwait(false);
        return await _interop.RenderToImageAsync(width, height, context, "image/png", 1d, cancellationToken).ConfigureAwait(false);
    }

    private Rect ResolveExportRegion(PngExportOptions options)
    {
        if (options.Region == ExportRegion.Selection && options.SelectedIds is null)
        {
            Rect bounds = _selection.Bounds;
            return bounds.IsEmpty ? options.ResolveRegion(_document) : bounds.Inflate(options.Padding);
        }
        return options.ResolveRegion(_document);
    }

    /// <summary>Exports the drawing as a PNG and offers it to the user as a file download.</summary>
    /// <exception cref="InvalidOperationException">The browser layer is not available, so nothing can be downloaded.</exception>
    public async Task SavePngAsync(string fileName = "drawing.png", PngExportOptions? options = null, CancellationToken cancellationToken = default)
    {
        RequireBrowser();
        byte[] bytes = await ExportPngAsync(options, cancellationToken).ConfigureAwait(false);
        if (bytes.Length > 0) await _interop!.SaveFileAsync(fileName, bytes, "image/png").ConfigureAwait(false);
    }

    /// <summary>Exports the drawing as SVG and offers it to the user as a file download.</summary>
    /// <exception cref="InvalidOperationException">The browser layer is not available, so nothing can be downloaded.</exception>
    public async Task SaveSvgAsync(string fileName = "drawing.svg", SvgExportOptions? options = null)
    {
        RequireBrowser();
        string svg = ExportSvg((options ?? SvgExportOptions.Default) with { IncludeXmlDeclaration = true });
        await _interop!.SaveFileAsync(fileName, System.Text.Encoding.UTF8.GetBytes(svg), "image/svg+xml").ConfigureAwait(false);
    }

    /// <summary>
    /// Throws when the browser layer is unavailable. Downloading is the one operation that cannot degrade
    /// quietly: silently doing nothing would look to the user like a broken button.
    /// </summary>
    private void RequireBrowser()
    {
        if (_interop is null || _disposed)
        {
            throw new InvalidOperationException(
                AttachFailed is { } reason
                    ? "The canvas could not attach to the browser, so the file cannot be saved: " + reason
                    : "The canvas is not attached to a browser yet. Wait for the Ready event before saving a file.");
        }
    }

    /// <summary>Adds an image to the document, sized to its natural dimensions.</summary>
    /// <param name="source">The image.</param>
    /// <param name="position">Top-left position in document units, or null to center it in the view.</param>
    /// <param name="maxWidth">Maximum width in document units; larger images are scaled down.</param>
    public async Task<ImageElement?> AddImageAsync(Rendering.ImageSource source, Vector2? position = null, float maxWidth = 600f)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (ReadOnly) return null;

        Rendering.ImageSource resolved = source;
        if (_interop is not null && (source.Width == 0 || source.Height == 0))
        {
            ImageMetrics? metrics = await _interop.LoadImageAsync(source.Uri).ConfigureAwait(false);
            if (metrics is not null) resolved = source.WithSize(metrics.Width, metrics.Height);
        }

        Vector2 topLeft = position ?? _viewport.ToDocument(ViewSize / 2f);
        ImageElement element = ImageElement.AtNaturalSize(resolved, topLeft, maxWidth);
        if (position is null) element = element with { Rect = element.Rect.Offset(-element.Rect.Size / 2f) };

        _document.AddElement(element, ActiveLayer);
        Announce("Image added.");
        InvalidateAll();
        return element;
    }

    /// <summary>Selects every element on unlocked, visible layers.</summary>
    public void SelectAll()
    {
        _selection.SelectAll();
        Announce($"{_selection.Count} elements selected.");
    }

    /// <summary>Requests a redraw of the whole canvas.</summary>
    public void Refresh() => InvalidateAll();

    /// <summary>Announces a message to screen readers through the canvas live region.</summary>
    public void Announce(string message)
    {
        if (string.IsNullOrWhiteSpace(message) || _announcement == message) return;
        _announcement = message;
        _uiDirty = true;
        ScheduleRender();
    }

    // ---- Lifecycle --------------------------------------------------------------------------------------------

    /// <inheritdoc />
    protected override void OnInitialized()
    {
        AttachDocument(Document ?? new InkDocument());
        _activeTool = Tool ?? new DrawTool();
        _previousToolParameter = Tool;
        AttachTextTool();
        _activeTool.Activate(_toolContext);
        _cursor = _activeTool.Cursor;
    }

    /// <inheritdoc />
    protected override void OnParametersSet()
    {
        if (Document is not null && !ReferenceEquals(Document, _previousDocumentParameter))
        {
            _previousDocumentParameter = Document;
            if (!ReferenceEquals(Document, _document))
            {
                // A gesture in flight refers to the old document's elements and history; finish it first.
                CancelActiveGesture();
                AttachDocument(Document);
            }
            InvalidateAll();
        }

        if (Tool is not null && !ReferenceEquals(Tool, _previousToolParameter))
        {
            _previousToolParameter = Tool;
            SetTool(Tool);
        }

        if (Brush is not null && !Equals(Brush, _brush)) _brush = Brush;
        if (ShapeStyle is not null) _shapeStyle = ShapeStyle;
        if (ActiveLayerId is { } layerId && _document.TryGetLayer(layerId, out _)) _activeLayerId = layerId;
        // Only follow the parameter when the parent actually changed it. Comparing against the current viewport
        // instead would revert the user's own panning every time the parent re-rendered.
        if (Viewport != _previousViewportParameter)
        {
            _previousViewportParameter = Viewport;
            if (Viewport != default && Viewport != _viewport)
            {
                _viewport = Viewport;
                InvalidateAll();
            }
        }
        if (_history.Limit != HistoryLimit) _history.Limit = HistoryLimit;
    }

    /// <inheritdoc />
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender) await AttachAsync().ConfigureAwait(false);
        if (_focusTextEditor && _textEditor is not null)
        {
            _focusTextEditor = false;
            try
            {
                await _textEditorElement.FocusAsync().ConfigureAwait(false);
            }
            catch (JSDisconnectedException)
            {
            }
            catch (JSException)
            {
            }
        }
    }

    private async Task AttachAsync()
    {
        if (_disposed) return;
        _interop = new InkInterop(JSRuntime, this, _elementId, Navigation.BaseUri);
        try
        {
            var options = new InteropOptions
            {
                RejectTouchWhilePen = RejectTouchWhilePen,
                TrackHover = false,
                WetInk = WetInk ?? !OperatingSystem.IsBrowser(),
            };
            AttachResult? result = await _interop.AttachAsync(_container, options).ConfigureAwait(false);
            if (_disposed) return;

            // A host that cannot run the module (a blocked script, a prerender pass, a test double) reports
            // nothing. The canvas then stays inert rather than throwing and taking the page down with it.
            if (result is null)
            {
                _capabilities = InputCapabilities.None;
                return;
            }

            _capabilities = result.ToCapabilities();
            _cssWidth = result.Width;
            _cssHeight = result.Height;
            _devicePixelRatio = result.DevicePixelRatio <= 0 ? 1 : result.DevicePixelRatio;
            _attached = true;

            await _interop.SetSizeAsync(_cssWidth, _cssHeight, _devicePixelRatio).ConfigureAwait(false);
            if (_disposed) return;
            await _interop.SetCursorAsync(_cursor).ConfigureAwait(false);
            await UpdateWetInkStyleAsync().ConfigureAwait(false);
            if (_disposed) return;
            InvalidateAll();
            await RenderDirtyAsync().ConfigureAwait(false);

            if (Ready.HasDelegate) await Ready.InvokeAsync(_capabilities).ConfigureAwait(false);
        }
        catch (JSDisconnectedException)
        {
            // The circuit closed while attaching; nothing to do.
        }
        catch (TaskCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (JSException ex)
        {
            // The module failed to load or threw. Drawing is unavailable, but the application keeps running.
            AttachFailed = ex.Message;
            _capabilities = InputCapabilities.None;
        }
    }

    /// <summary>
    /// Ends whatever the active tool is doing, without committing it. Called whenever the canvas is about to
    /// change underneath a gesture: a stranded gesture would hold pointer capture and an open history group,
    /// which silently disables undo.
    /// </summary>
    private void CancelActiveGesture()
    {
        if (_activeTool is null) return;
        _activeTool.Deactivate(_toolContext);
        _activeTool.Activate(_toolContext);
        _history?.EndOpenGroups();
        _overlayDirty = true;
    }

    /// <summary>
    /// The reason the browser layer could not be attached, or null when everything is working. Applications can
    /// surface this to explain why the canvas is not responding.
    /// </summary>
    public string? AttachFailed { get; private set; }

    private void AttachDocument(InkDocument document)
    {
        if (_document is not null) _document.Changed -= OnDocumentChanged;
        _history?.Dispose();
        _selection?.Dispose();

        _document = document;
        // Order matters: history and selection must observe the document before this component does, so that by
        // the time the host is notified of a change, undo state and the selection already reflect it. Subscribing
        // the component first would make every host re-render one step stale.
        _history = new HistoryManager(document, HistoryLimit);
        _history.Changed += OnHistoryChanged;
        _selection = new Selection(document);
        _selection.Changed += OnSelectionChanged;
        _document.Changed += OnDocumentChanged;
        _activeLayerId = ActiveLayerId is { } id && document.TryGetLayer(id, out _) ? id : document.ActiveLayerCandidate.Id;
    }

    private void OnDocumentChanged(object? sender, DocumentChangedEventArgs e)
    {
        if (_watchForAddedStroke) TrackAddedStroke(e.Operation);
        _documentDirty = true;
        _uiDirty = true;
        ScheduleRender();
        RaiseAsync(() => DocumentChanged.InvokeAsync(e), DocumentChanged.HasDelegate);
    }

    private void OnHistoryChanged(object? sender, EventArgs e)
    {
        _uiDirty = true;
        ScheduleRender();
        RaiseAsync(() => HistoryChanged.InvokeAsync(), HistoryChanged.HasDelegate);
    }

    private void OnSelectionChanged(object? sender, EventArgs e)
    {
        _overlayDirty = true;
        _uiDirty = true;
        ScheduleRender();
        RaiseAsync(() => SelectionChanged.InvokeAsync(), SelectionChanged.HasDelegate);
    }

    /// <summary>
    /// Raises an event callback on the renderer's dispatcher. Engine events can originate from a JavaScript
    /// callback or a background continuation, and invoking a callback off the dispatcher throws silently.
    /// </summary>
    private void RaiseAsync(Func<Task> raise, bool hasDelegate)
    {
        if (!hasDelegate || _disposed) return;
        _ = InvokeAsync(async () =>
        {
            try
            {
                await raise().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
            }
            catch (JSDisconnectedException)
            {
            }
        });
    }

    /// <summary>Replaces the active tool.</summary>
    public void SetTool(ITool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        if (ReferenceEquals(tool, _activeTool)) return;
        DetachTextTool();
        _activeTool.Deactivate(_toolContext);
        _activeTool = tool;
        AttachTextTool();
        _activeTool.Activate(_toolContext);
        _overlayDirty = true;
        _ = SetCursorAsync(tool.Cursor);
        ScheduleRender();
    }

    private async Task SetCursorAsync(string cursor)
    {
        if (_cursor == cursor) return;
        _cursor = cursor;
        if (_interop is not null) await _interop.SetCursorAsync(cursor).ConfigureAwait(false);
    }

    private Guid ActiveLayer
    {
        get
        {
            if (_document.TryGetLayer(_activeLayerId, out Layer? layer) && !layer.IsLocked && layer.IsVisible) return _activeLayerId;
            return _document.ActiveLayerCandidate.Id;
        }
    }

    // ---- Input ------------------------------------------------------------------------------------------------

    void IInkInteropClient.OnSamples(ReadOnlySpan<double> data, int count)
    {
        if (_disposed) return;

        // A read-only canvas still pans and zooms: only tools that change the document are blocked.
        if (ReadOnly && _activeTool.ModifiesDocument)
        {
            CancelActiveGesture();
            return;
        }
        int stride = InputSample.PackedSize;
        bool wantsAll = _activeTool.WantsCoalescedInput;

        for (int i = 0; i < count; i++)
        {
            int offset = i * stride;
            if (offset + stride > data.Length) break;
            InputSample sample = InputSample.ReadFrom(data.Slice(offset, stride));
            _capabilities = _capabilities.Observe(sample);
            // A tool that does not want intermediate samples still needs the last one before an up event,
            // so only interior coalesced move samples are dropped.
            if (!wantsAll && sample.IsCoalesced && sample.Phase == PointerPhase.Move && i < count - 1) continue;
            Dispatch(sample);
        }

        _ = RenderDirtyAsync();
    }

    private void Dispatch(in InputSample sample)
    {
        switch (sample.Phase)
        {
            case PointerPhase.Down:
                if (_activeTool.OnPointerDown(_toolContext, sample))
                {
                    StrokeEventArgs started = new(null, sample.Kind);
                    RaiseAsync(() => StrokeStarted.InvokeAsync(started), StrokeStarted.HasDelegate);
                }
                break;
            case PointerPhase.Move:
            case PointerPhase.Hover:
                _activeTool.OnPointerMove(_toolContext, sample);
                break;
            case PointerPhase.Up:
                HandlePointerUp(sample);
                break;
            case PointerPhase.Cancel:
                _activeTool.OnPointerCancel(_toolContext, sample);
                break;
        }
    }

    private void HandlePointerUp(in InputSample sample)
    {
        // The document reports exactly which element an operation added, so the event carries the right stroke
        // rather than whichever element happens to be last in some layer.
        _lastAddedStroke = null;
        _watchForAddedStroke = StrokeCompleted.HasDelegate;
        try
        {
            _activeTool.OnPointerUp(_toolContext, sample);
        }
        finally
        {
            _watchForAddedStroke = false;
        }

        if (_lastAddedStroke is { } stroke)
        {
            StrokeEventArgs completed = new(stroke, sample.Kind);
            _lastAddedStroke = null;
            RaiseAsync(() => StrokeCompleted.InvokeAsync(completed), StrokeCompleted.HasDelegate);
        }
    }

    /// <summary>Records the stroke an operation added, so <see cref="StrokeCompleted"/> reports the real element.</summary>
    private void TrackAddedStroke(DrawingOperation operation)
    {
        switch (operation)
        {
            case AddElementOperation { Element: Stroke { IsEraser: false } stroke }:
                _lastAddedStroke = stroke;
                break;
            case BatchOperation batch:
                foreach (DrawingOperation inner in batch.Operations) TrackAddedStroke(inner);
                break;
        }
    }

    void IInkInteropClient.OnResized(double width, double height, double devicePixelRatio)
    {
        if (_disposed) return;
        bool changed = Math.Abs(width - _cssWidth) > 0.5 || Math.Abs(height - _cssHeight) > 0.5 || Math.Abs(devicePixelRatio - _devicePixelRatio) > 0.001;
        if (!changed) return;
        _cssWidth = width;
        _cssHeight = height;
        _devicePixelRatio = devicePixelRatio <= 0 ? 1 : devicePixelRatio;
        _capabilities = _capabilities with { DevicePixelRatio = _devicePixelRatio };
        _ = ResizeAsync();
    }

    private async Task ResizeAsync()
    {
        if (_interop is null || _disposed) return;
        await _interop.SetSizeAsync(_cssWidth, _cssHeight, _devicePixelRatio).ConfigureAwait(false);
        InvalidateAll();
        await RenderDirtyAsync().ConfigureAwait(false);
    }

    void IInkInteropClient.OnKey(string key, bool ctrl, bool shift, bool alt)
    {
        if (_disposed || !EnableKeyboardShortcuts || ReadOnly) return;

        var keyEvent = new ToolKeyEvent(key, ctrl, shift, alt);
        if (_activeTool.OnKeyDown(_toolContext, keyEvent))
        {
            _ = RenderDirtyAsync();
            return;
        }

        switch (key)
        {
            case "z" or "Z" when ctrl && !shift:
                Undo();
                break;
            case "z" or "Z" when ctrl && shift:
            case "y" or "Y" when ctrl:
                Redo();
                break;
            case "a" or "A" when ctrl:
                SelectAll();
                break;
            case "Delete" or "Backspace":
                DeleteSelection();
                break;
            case "Escape":
                _selection.Clear();
                break;
            case "0" when ctrl:
                ResetViewport();
                break;
            case "9" when ctrl:
                ZoomToFit();
                break;
            case "+" or "=" when ctrl:
                ZoomBy(1.25f);
                break;
            case "-" when ctrl:
                ZoomBy(0.8f);
                break;
            default:
                return;
        }

        _ = RenderDirtyAsync();
    }

    // ---- Text editing --------------------------------------------------------------------------------------------

    /// <summary>State of the inline text editor while a text element is being edited.</summary>
    private sealed record TextEditorState(Guid ElementId, string Text, string Style);

    /// <summary>
    /// Text editing is the one interaction a canvas cannot own. A real text control is needed for selection,
    /// input methods, autocorrect, dictation and screen readers, so <see cref="TextTool"/> asks the component to
    /// place one over the element and the component reports the result back.
    /// </summary>
    private void AttachTextTool()
    {
        if (_activeTool is not TextTool tool) return;
        tool.EditRequested += OnTextEditRequested;
        tool.EditFinished += OnTextEditFinished;
    }

    private void DetachTextTool()
    {
        if (_activeTool is not TextTool tool) return;
        tool.EditRequested -= OnTextEditRequested;
        tool.EditFinished -= OnTextEditFinished;
        _textEditor = null;
    }

    private void OnTextEditRequested(TextEditRequest request)
    {
        _textEditor = new TextEditorState(request.Element.Id, request.Element.Text, BuildEditorStyle(request));
        _focusTextEditor = true;
        _uiDirty = true;
        _ = InvokeAsync(StateHasChanged);
    }

    private string BuildEditorStyle(TextEditRequest request)
    {
        TextStyle style = request.Element.Style;
        Rect box = request.ViewRect;
        float fontSize = style.FontSize * _viewport.Zoom;
        string family = style.FontFamily.Replace("\"", string.Empty, StringComparison.Ordinal);
        string weight = style.Bold ? "bold " : string.Empty;
        string italic = style.Italic ? "italic " : string.Empty;
        var culture = System.Globalization.CultureInfo.InvariantCulture;

        return string.Create(culture,
            $"left:{box.X:0.##}px;top:{box.Y:0.##}px;width:{MathF.Max(box.Width, fontSize * 4):0.##}px;height:{MathF.Max(box.Height, fontSize * style.LineHeight):0.##}px;font:{italic}{weight}{fontSize:0.##}px {family};line-height:{fontSize * style.LineHeight:0.##}px;color:{style.Color.ToCss()};");
    }

    private void OnTextEditFinished()
    {
        _textEditor = null;
        _uiDirty = true;
        _ = InvokeAsync(StateHasChanged);
    }

    /// <summary>
    /// Handles a commit, cancel or blur from the inline editor. The value comes from the browser rather than
    /// from a framework binding, so what is saved is exactly what the user sees in the field.
    /// </summary>
    void IInkInteropClient.OnTextEditorKey(string key, string value)
    {
        if (_disposed || _textEditor is null || _activeTool is not TextTool tool) return;

        if (key == "Escape")
        {
            tool.CancelEdit(_toolContext);
            _textEditor = null;
            _uiDirty = true;
            ScheduleRender();
            return;
        }

        _textEditor = _textEditor with { Text = value };
        _ = InvokeAsync(() => CommitTextAsync(tool, value));
    }

    private async Task CommitTextAsync(TextTool tool, string value)
    {
        Vector2? measured = await MeasureTextAsync(value, tool).ConfigureAwait(false);
        tool.CommitEdit(_toolContext, value, measured);
        _textEditor = null;
        _uiDirty = true;
        await RenderDirtyAsync().ConfigureAwait(false);
    }

    private async Task<Vector2?> MeasureTextAsync(string text, TextTool tool)
    {
        if (_interop is null || string.IsNullOrEmpty(text) || tool.Editing is null) return null;
        TextStyle style = tool.Editing.Style;
        TextMetrics? metrics = await _interop.MeasureTextAsync(text, CanvasRenderContext.ToCssFont(style)).ConfigureAwait(false);
        if (metrics is null) return null;
        return new Vector2((float)metrics.Width, Math.Max(metrics.Lines, 1) * style.FontSize * style.LineHeight);
    }

    // ---- Rendering --------------------------------------------------------------------------------------------

    private void InvalidateAll()
    {
        _documentDirty = true;
        _overlayDirty = true;
        ScheduleRender();
    }

    private void ScheduleRender()
    {
        if (_renderScheduled || !_attached || _disposed) return;
        _renderScheduled = true;
        // Coalesces bursts of programmatic changes into one redraw without touching Blazor's render cycle.
        _ = InvokeAsync(async () =>
        {
            _renderScheduled = false;
            await RenderDirtyAsync().ConfigureAwait(false);
        });
    }

    private async Task RenderDirtyAsync()
    {
        if (_interop is null || _disposed || !_attached) return;
        if (!_documentDirty && !_overlayDirty && !_uiDirty) return;

        try
        {
            if (_documentDirty)
            {
                _documentDirty = false;
                RecordDocument();
                await _interop.ExecuteAsync(CanvasLayer.Document, _documentContext).ConfigureAwait(false);
            }
            if (_overlayDirty)
            {
                _overlayDirty = false;
                RecordOverlay();
                await _interop.ExecuteAsync(CanvasLayer.Overlay, _overlayContext).ConfigureAwait(false);
            }
            if (_uiDirty)
            {
                // The accessible description and anything bound to the document must follow the model. This
                // happens at most once per frame, and never for a plain pointer move.
                _uiDirty = false;
                StateHasChanged();
            }
        }
        catch (JSDisconnectedException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (TaskCanceledException)
        {
        }
    }

    private Matrix3x2 DeviceTransform => _viewport.DocumentToView * Matrix3x2.CreateScale((float)_devicePixelRatio);

    private void RecordDocument()
    {
        CanvasRenderContext context = _documentContext;
        context.Reset();
        context.Clear(_document.Settings.Background);
        context.SetTransform(DeviceTransform);

        Rect visible = _viewport.VisibleRect(ViewSize);
        DocumentRenderer.Render(_document, context, new RenderOptions { ClipBounds = visible });
    }

    private void RecordOverlay()
    {
        CanvasRenderContext context = _overlayContext;
        context.Reset();
        context.Clear(InkColor.Transparent);
        context.SetTransform(DeviceTransform);
        _activeTool.DrawOverlay(_toolContext, context);
    }

    private async Task UpdateWetInkStyleAsync()
    {
        if (_interop is null) return;
        bool enabled = WetInk ?? !OperatingSystem.IsBrowser();
        if (!enabled)
        {
            await _interop.SetWetInkStyleAsync(null, 0, 0).ConfigureAwait(false);
            return;
        }
        float width = MathF.Max(_brush.Width * _viewport.Zoom * 0.8f, 1f);
        await _interop.SetWetInkStyleAsync(_brush.Color, width, _brush.Opacity).ConfigureAwait(false);
    }

    private async Task EnsureImagesLoadedAsync(CancellationToken cancellationToken)
    {
        if (_interop is null) return;
        foreach (InkElement element in _document.AllElements)
        {
            if (element is ImageElement image) await _interop.LoadImageAsync(image.Source.Uri, cancellationToken).ConfigureAwait(false);
        }
    }

    private string ContainerClass => string.Join(' ', new[] { "blazorink-canvas", Class }.Where(static c => !string.IsNullOrWhiteSpace(c)));

    private string ContainerStyle
    {
        get
        {
            string touchAction = EnableTouchGestures ? "none" : "pan-x pan-y";
            return $"width:{Width};height:{Height};touch-action:{touchAction};{Style}";
        }
    }

    /// <summary>
    /// Builds the text exposed to assistive technology. Handwriting itself cannot be described automatically, so
    /// this reports the structure and any descriptions the application attached to elements.
    /// </summary>
    private string GetContentDescription()
    {
        if (_document is null) return string.Empty;
        if (_document.IsEmpty) return "Empty drawing canvas.";

        var described = new List<string>();
        int total = _document.ElementCount;
        const int maxDescribed = 25;
        foreach (InkElement element in _document.AllElements)
        {
            if (described.Count >= maxDescribed) break;
            if (element.Description is { Length: > 0 } description) described.Add(description);
            else if (element is TextElement text && text.Text.Length > 0) described.Add("Text: " + text.Text);
        }

        string summary = $"Drawing with {total} elements on {_document.Layers.Count} layers.";
        return described.Count == 0 ? summary : summary + " " + string.Join(". ", described) + ".";
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        DetachTextTool();
        _activeTool?.Deactivate(_toolContext);
        if (_document is not null) _document.Changed -= OnDocumentChanged;
        if (_history is not null)
        {
            _history.Changed -= OnHistoryChanged;
            _history.Dispose();
        }
        if (_selection is not null)
        {
            _selection.Changed -= OnSelectionChanged;
            _selection.Dispose();
        }
        if (_interop is not null) await _interop.DisposeAsync().ConfigureAwait(false);
        _interop = null;
    }

    /// <summary>The tool context handed to tools. Kept private so tools cannot reach the component itself.</summary>
    private sealed class ToolContext(InkCanvas canvas) : IToolContext
    {
        public InkDocument Document => canvas._document;

        public Selection Selection => canvas._selection;

        public HistoryManager History => canvas._history;

        public Viewport Viewport
        {
            get => canvas._viewport;
            set => canvas.SetViewport(value);
        }

        public Guid ActiveLayerId => canvas.ActiveLayer;

        public Brush Brush => canvas._brush;

        public ShapeStyle ShapeStyle => canvas._shapeStyle;

        public Vector2 ViewSize => canvas.ViewSize;

        public void InvalidateOverlay()
        {
            canvas._overlayDirty = true;
        }

        public void InvalidateDocument(Rect? region = null)
        {
            canvas._documentDirty = true;
        }

        public void CapturePointer(int pointerId)
        {
            // The browser layer captures on pointer down already; tools call this to express intent.
        }

        public void ReleasePointer(int pointerId)
        {
        }

        public void SetCursor(string cursor) => _ = canvas.SetCursorAsync(cursor);

        public void Announce(string message) => canvas.Announce(message);
    }
}

/// <summary>Information about a stroke event.</summary>
/// <param name="Stroke">The stroke, once it exists. Null when a stroke has only just started.</param>
/// <param name="Device">The device that drew it.</param>
public readonly record struct StrokeEventArgs(Stroke? Stroke, PointerKind Device);
