using System.Diagnostics.CodeAnalysis;
using BlazorInk.Geometry;
using BlazorInk.Operations;

namespace BlazorInk;

/// <summary>Raised after a document changed.</summary>
public sealed class DocumentChangedEventArgs : EventArgs
{
    internal DocumentChangedEventArgs(DrawingOperation operation, DrawingOperation inverse, ChangeOrigin origin, long revision)
    {
        Operation = operation;
        Inverse = inverse;
        Origin = origin;
        Revision = revision;
    }

    /// <summary>The operation that was applied.</summary>
    public DrawingOperation Operation { get; }

    /// <summary>The operation that undoes it.</summary>
    public DrawingOperation Inverse { get; }

    /// <summary>Who caused the change.</summary>
    public ChangeOrigin Origin { get; }

    /// <summary>The document revision after the change.</summary>
    public long Revision { get; }
}

/// <summary>
/// The vector drawing model: layers of elements plus settings and metadata. Independent of any UI; can be
/// created, edited, serialized and rendered on a server or in tests without a browser.
/// </summary>
/// <remarks>
/// <para>
/// Every mutation goes through <see cref="Apply(DrawingOperation, ChangeOrigin)"/> (the convenience methods such as
/// <see cref="AddElement"/> build the operation for you). The document raises <see cref="Changed"/> with the operation
/// and its inverse, which is what <see cref="History.HistoryManager"/> and collaboration sessions consume.
/// </para>
/// <para>
/// The document is not thread-safe; use it from one thread (the UI thread in Blazor).
/// </para>
/// </remarks>
public sealed class InkDocument
{
    private readonly List<Layer> _layers = new();
    private readonly Dictionary<Guid, Layer> _elementLayers = new();
    private readonly Dictionary<Guid, Layer> _layersById = new();
    private readonly Dictionary<string, string> _metadata = new(StringComparer.Ordinal);
    private List<DrawingOperation>? _batch;
    private List<DrawingOperation>? _batchInverses;
    private int _batchDepth;
    private ChangeOrigin _batchOrigin;
    private string? _batchLabel;
    private long _totalPoints;

    /// <summary>Creates a document with default settings and one layer named "Drawing".</summary>
    public InkDocument() : this(DocumentSettings.Default) { }

    /// <summary>Creates a document with the given settings and one layer named "Drawing".</summary>
    public InkDocument(DocumentSettings settings) : this(settings, DocumentLimits.Default) { }

    /// <summary>Creates a document with settings and limits and one layer named "Drawing".</summary>
    public InkDocument(DocumentSettings settings, DocumentLimits limits)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(limits);
        Settings = settings;
        Limits = limits;
        InsertLayerCore(Guid.NewGuid(), new LayerProperties { Name = "Drawing" }, null, null);
    }

    /// <summary>Creates an empty document without layers (used by deserialization).</summary>
    internal InkDocument(Guid id, DocumentSettings settings, DocumentLimits limits, bool withoutLayers)
    {
        Id = id;
        Settings = settings;
        Limits = limits;
        if (!withoutLayers) InsertLayerCore(Guid.NewGuid(), new LayerProperties { Name = "Drawing" }, null, null);
    }

    /// <summary>Document identifier.</summary>
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>Page settings.</summary>
    public DocumentSettings Settings { get; private set; }

    /// <summary>Resource limits enforced on mutation.</summary>
    public DocumentLimits Limits { get; }

    /// <summary>Layers from bottom to top.</summary>
    public IReadOnlyList<Layer> Layers => _layers;

    /// <summary>Application metadata.</summary>
    public IReadOnlyDictionary<string, string> Metadata => _metadata;

    /// <summary>Incremented on every applied operation (batches count once).</summary>
    public long Revision { get; private set; }

    /// <summary>Total number of elements across layers.</summary>
    public int ElementCount => _elementLayers.Count;

    /// <summary>Total number of stroke points in the document.</summary>
    public long TotalPointCount => _totalPoints;

    /// <summary>The top-most unlocked, visible layer, or the top-most layer when none qualifies.</summary>
    public Layer ActiveLayerCandidate
    {
        get
        {
            for (int i = _layers.Count - 1; i >= 0; i--) if (!_layers[i].IsLocked && _layers[i].IsVisible) return _layers[i];
            return _layers[^1];
        }
    }

    /// <summary>Raised after every applied operation (or once per batch).</summary>
    public event EventHandler<DocumentChangedEventArgs>? Changed;

    /// <summary>Union of the bounds of all elements on visible layers.</summary>
    public Rect ContentBounds
    {
        get
        {
            Rect bounds = Rect.Empty;
            foreach (Layer layer in _layers)
            {
                if (!layer.IsVisible) continue;
                foreach (InkElement e in layer.Elements) if (!(e is Stroke { IsEraser: true })) bounds = bounds.Union(e.Bounds);
            }
            return bounds;
        }
    }

    /// <summary>True when no layer contains elements.</summary>
    public bool IsEmpty => _elementLayers.Count == 0;

    /// <summary>All elements on all layers, bottom to top.</summary>
    public IEnumerable<InkElement> AllElements
    {
        get
        {
            foreach (Layer layer in _layers) foreach (InkElement e in layer.Elements) yield return e;
        }
    }

    // ---- Queries -------------------------------------------------------------------------------------------------

    /// <summary>Gets a layer by id.</summary>
    public bool TryGetLayer(Guid layerId, [NotNullWhen(true)] out Layer? layer) => _layersById.TryGetValue(layerId, out layer);

    /// <summary>Gets a layer by id or throws.</summary>
    public Layer GetLayer(Guid layerId) => TryGetLayer(layerId, out Layer? layer) ? layer : throw new KeyNotFoundException($"Layer {layerId} is not in the document.");

    /// <summary>True when an element with the id exists.</summary>
    public bool ContainsElement(Guid elementId) => _elementLayers.ContainsKey(elementId);

    /// <summary>Gets an element and its layer.</summary>
    public bool TryGetElement(Guid elementId, [NotNullWhen(true)] out InkElement? element, [NotNullWhen(true)] out Layer? layer)
    {
        element = null;
        if (!_elementLayers.TryGetValue(elementId, out layer)) return false;
        int index = layer.IndexOf(elementId);
        if (index < 0) return false;
        element = layer.Elements[index];
        return true;
    }

    /// <summary>Gets an element by id.</summary>
    public bool TryGetElement(Guid elementId, [NotNullWhen(true)] out InkElement? element) => TryGetElement(elementId, out element, out _);

    /// <summary>Gets an element by id or throws.</summary>
    public InkElement GetElement(Guid elementId) => TryGetElement(elementId, out InkElement? e) ? e : throw new KeyNotFoundException($"Element {elementId} is not in the document.");

    /// <summary>Gets the layer containing an element.</summary>
    public Layer? GetLayerOf(Guid elementId) => _elementLayers.GetValueOrDefault(elementId);

    // ---- Mutation API -----------------------------------------------------------------------------------------------

    /// <summary>Applies an operation and raises <see cref="Changed"/>. Returns the inverse operation.</summary>
    /// <param name="operation">The operation.</param>
    /// <param name="origin">Who caused it.</param>
    /// <exception cref="InvalidOperationException">The operation cannot be applied (missing element, limit exceeded, ...).</exception>
    public DrawingOperation Apply(DrawingOperation operation, ChangeOrigin origin = ChangeOrigin.Local)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (operation is BatchOperation { IsEmpty: true }) return operation;

        // Version numbers are decided before the operation is applied, so the operation reported to listeners
        // carries the same version that ends up in the document. A collaborator receiving an operation whose
        // version is lower than what the sender actually stored would discard it, and the two replicas would
        // never agree again.
        operation = AssignVersions(operation);
        DrawingOperation inverse = operation.ApplyCore(this);
        if (_batchDepth > 0)
        {
            _batch!.Add(operation);
            _batchInverses!.Insert(0, inverse);
            return inverse;
        }
        Revision++;
        Changed?.Invoke(this, new DocumentChangedEventArgs(operation, inverse, origin, Revision));
        return inverse;
    }

    /// <summary>
    /// Groups all operations applied until the returned scope is disposed into a single <see cref="BatchOperation"/>
    /// that raises one <see cref="Changed"/> event and undoes as one step. Scopes nest.
    /// </summary>
    /// <remarks>
    /// A scope collects operations that have already been applied, so an exception thrown inside it leaves the
    /// operations that succeeded in place. This differs from applying a <see cref="BatchOperation"/> directly,
    /// which rolls back on failure. Use the batch operation when the change must be all or nothing.
    /// </remarks>
    /// <param name="label">Label for the batch (used by history UIs).</param>
    /// <param name="origin">Origin reported for the batch.</param>
    public IDisposable BeginBatch(string? label = null, ChangeOrigin origin = ChangeOrigin.Local)
    {
        if (_batchDepth == 0)
        {
            _batch = new List<DrawingOperation>();
            _batchInverses = new List<DrawingOperation>();
            _batchOrigin = origin;
            _batchLabel = label;
        }
        _batchDepth++;
        return new BatchScope(this);
    }

    private void EndBatch()
    {
        if (--_batchDepth > 0) return;
        List<DrawingOperation> ops = _batch!;
        List<DrawingOperation> inverses = _batchInverses!;
        _batch = null;
        _batchInverses = null;
        if (ops.Count == 0) return;
        Revision++;
        DrawingOperation forward = ops.Count == 1 && _batchLabel is null ? ops[0] : new BatchOperation(ops, _batchLabel);
        DrawingOperation inverse = inverses.Count == 1 && _batchLabel is null ? inverses[0] : new BatchOperation(inverses, _batchLabel);
        Changed?.Invoke(this, new DocumentChangedEventArgs(forward, inverse, _batchOrigin, Revision));
    }

    private sealed class BatchScope(InkDocument document) : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            document.EndBatch();
        }
    }

    /// <summary>Adds an element to a layer (the top-most editable layer when <paramref name="layerId"/> is null).</summary>
    public void AddElement(InkElement element, Guid? layerId = null)
    {
        ArgumentNullException.ThrowIfNull(element);
        Apply(new AddElementOperation(layerId ?? ActiveLayerCandidate.Id, element));
    }

    /// <summary>Removes an element.</summary>
    public void RemoveElement(Guid elementId) => Apply(new RemoveElementOperation(elementId));

    /// <summary>Removes several elements as one step.</summary>
    public void RemoveElements(IEnumerable<Guid> elementIds)
    {
        ArgumentNullException.ThrowIfNull(elementIds);
        var ops = new List<DrawingOperation>();
        var seen = new HashSet<Guid>();
        // A repeated id would remove the element and then fail on the second attempt, rolling the batch back.
        foreach (Guid id in elementIds)
        {
            if (ContainsElement(id) && seen.Add(id)) ops.Add(new RemoveElementOperation(id));
        }
        if (ops.Count > 0) Apply(new BatchOperation(ops, "Delete"));
    }

    /// <summary>Replaces an element with a new version (same id).</summary>
    public void ReplaceElement(InkElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        Apply(new ReplaceElementOperation(element));
    }

    /// <summary>Replaces several elements as one step.</summary>
    public void ReplaceElements(IEnumerable<InkElement> elements, string? label = null)
    {
        ArgumentNullException.ThrowIfNull(elements);
        var ops = new List<DrawingOperation>();
        foreach (InkElement e in elements) ops.Add(new ReplaceElementOperation(e));
        if (ops.Count > 0) Apply(new BatchOperation(ops, label ?? "Edit"));
    }

    /// <summary>Adds a layer on top and returns it.</summary>
    public Layer AddLayer(string name) => AddLayer(new LayerProperties { Name = name });

    /// <summary>Adds a layer on top and returns it.</summary>
    public Layer AddLayer(LayerProperties properties, int? index = null)
    {
        ArgumentNullException.ThrowIfNull(properties);
        var id = Guid.NewGuid();
        Apply(new AddLayerOperation(id, properties, index));
        return GetLayer(id);
    }

    /// <summary>Removes a layer and its elements. A document always keeps at least one layer.</summary>
    public void RemoveLayer(Guid layerId) => Apply(new RemoveLayerOperation(layerId));

    /// <summary>Updates a layer's properties.</summary>
    public void UpdateLayer(Guid layerId, Func<LayerProperties, LayerProperties> update)
    {
        ArgumentNullException.ThrowIfNull(update);
        Apply(new UpdateLayerOperation(layerId, update(GetLayer(layerId).Properties)));
    }

    /// <summary>Moves a layer to a new index.</summary>
    public void ReorderLayer(Guid layerId, int index) => Apply(new ReorderLayerOperation(layerId, index));

    /// <summary>Changes the settings.</summary>
    public void UpdateSettings(DocumentSettings settings) => Apply(new UpdateSettingsOperation(settings));

    /// <summary>Sets or removes (null) a metadata entry.</summary>
    public void SetMetadata(string key, string? value) => Apply(new SetMetadataOperation(key, value));

    /// <summary>Removes all elements from all layers (layers are kept) as one undoable step.</summary>
    public void Clear()
    {
        var ops = new List<DrawingOperation>();
        foreach (Layer layer in _layers)
            for (int i = layer.Elements.Count - 1; i >= 0; i--) ops.Add(new RemoveElementOperation(layer.Elements[i].Id));
        if (ops.Count > 0) Apply(new BatchOperation(ops, "Clear"));
    }

    /// <summary>
    /// Returns the operation with element versions filled in from the document's current state. Only replacements
    /// need this: every other operation already carries whatever version its element should keep.
    /// </summary>
    private DrawingOperation AssignVersions(DrawingOperation operation)
    {
        switch (operation)
        {
            case ReplaceElementOperation replace when TryGetElement(replace.Element.Id, out InkElement? current):
            {
                if (replace.Element.Version > current.Version) return replace;
                return new ReplaceElementOperation(replace.Element with { Version = current.Version + 1 });
            }
            case BatchOperation batch:
            {
                List<DrawingOperation>? rewritten = null;
                for (int i = 0; i < batch.Operations.Count; i++)
                {
                    DrawingOperation assigned = AssignVersions(batch.Operations[i]);
                    if (!ReferenceEquals(assigned, batch.Operations[i]) && rewritten is null)
                    {
                        rewritten = new List<DrawingOperation>(batch.Operations.Count);
                        for (int j = 0; j < i; j++) rewritten.Add(batch.Operations[j]);
                    }
                    rewritten?.Add(assigned);
                }
                return rewritten is null ? batch : new BatchOperation(rewritten, batch.Label);
            }
            default:
                return operation;
        }
    }

    // ---- Core mutations (called by operations) ------------------------------------------------------------------------

    internal void InsertElementCore(Guid layerId, InkElement element, int? index)
    {
        Layer layer = GetLayer(layerId);
        if (_elementLayers.ContainsKey(element.Id)) throw new InvalidOperationException($"An element with id {element.Id} already exists in the document.");
        ValidateElement(element);
        if (_elementLayers.Count >= Limits.MaxElements) throw new InvalidOperationException($"The document limit of {Limits.MaxElements} elements was reached.");
        List<InkElement> list = layer.MutableElements;
        int at = index is null ? list.Count : Math.Clamp(index.Value, 0, list.Count);
        list.Insert(at, element);
        _elementLayers[element.Id] = layer;
        if (element is Stroke s) _totalPoints += s.Points.Count;
    }

    internal (Layer Layer, int Index, InkElement Element) RemoveElementCore(Guid elementId)
    {
        if (!_elementLayers.TryGetValue(elementId, out Layer? layer)) throw new InvalidOperationException($"Element {elementId} is not in the document.");
        int index = layer.IndexOf(elementId);
        InkElement element = layer.Elements[index];
        layer.MutableElements.RemoveAt(index);
        _elementLayers.Remove(elementId);
        if (element is Stroke s) _totalPoints -= s.Points.Count;
        return (layer, index, element);
    }

    internal InkElement ReplaceElementCore(InkElement element)
    {
        if (!_elementLayers.TryGetValue(element.Id, out Layer? layer)) throw new InvalidOperationException($"Element {element.Id} is not in the document.");
        int index = layer.IndexOf(element.Id);
        InkElement previous = layer.Elements[index];

        // The outgoing element's points no longer count towards the total, so remove them before validating.
        // Otherwise replacing a stroke with one of the same size could falsely trip the document's point limit.
        if (previous is Stroke outgoing) _totalPoints -= outgoing.Points.Count;
        try
        {
            ValidateElement(element);
        }
        catch
        {
            if (previous is Stroke restore) _totalPoints += restore.Points.Count;
            throw;
        }

        // Apply assigned the version already; anything reaching here with a stale one is a direct caller, and is
        // still bumped so two versions of an element are never confused.
        InkElement next = element.Version > previous.Version ? element : element with { Version = previous.Version + 1 };
        layer.MutableElements[index] = next;
        if (next is Stroke incoming) _totalPoints += incoming.Points.Count;
        return previous;
    }

    internal (Guid PreviousLayerId, int PreviousIndex) ReorderElementCore(Guid elementId, Guid targetLayerId, int? index)
    {
        Layer target = GetLayer(targetLayerId);
        (Layer source, int previousIndex, InkElement element) = RemoveElementCore(elementId);
        List<InkElement> list = target.MutableElements;
        int at = index is null ? list.Count : Math.Clamp(index.Value, 0, list.Count);
        list.Insert(at, element);
        _elementLayers[elementId] = target;
        if (element is Stroke s) _totalPoints += s.Points.Count;
        return (source.Id, previousIndex);
    }

    internal void InsertLayerCore(Guid layerId, LayerProperties properties, int? index, IReadOnlyList<InkElement>? elements)
    {
        ArgumentNullException.ThrowIfNull(properties);
        if (_layersById.ContainsKey(layerId)) throw new InvalidOperationException($"A layer with id {layerId} already exists.");
        if (_layers.Count >= Limits.MaxLayers) throw new InvalidOperationException($"The document limit of {Limits.MaxLayers} layers was reached.");
        if (elements is not null)
        {
            foreach (InkElement e in elements)
            {
                if (_elementLayers.ContainsKey(e.Id)) throw new InvalidOperationException($"An element with id {e.Id} already exists in the document.");
                ValidateElement(e);
            }
            if (_elementLayers.Count + elements.Count > Limits.MaxElements) throw new InvalidOperationException($"The document limit of {Limits.MaxElements} elements was reached.");
        }
        var layer = new Layer(layerId, properties, elements);
        int at = index is null ? _layers.Count : Math.Clamp(index.Value, 0, _layers.Count);
        _layers.Insert(at, layer);
        _layersById[layerId] = layer;
        foreach (InkElement e in layer.Elements)
        {
            _elementLayers[e.Id] = layer;
            if (e is Stroke s) _totalPoints += s.Points.Count;
        }
    }

    internal (int Index, Layer Layer) RemoveLayerCore(Guid layerId)
    {
        Layer layer = GetLayer(layerId);
        if (_layers.Count == 1) throw new InvalidOperationException("A document must keep at least one layer.");
        int index = _layers.IndexOf(layer);
        _layers.RemoveAt(index);
        _layersById.Remove(layerId);
        foreach (InkElement e in layer.Elements)
        {
            _elementLayers.Remove(e.Id);
            if (e is Stroke s) _totalPoints -= s.Points.Count;
        }
        return (index, layer);
    }

    internal LayerProperties UpdateLayerCore(Guid layerId, LayerProperties properties)
    {
        ArgumentNullException.ThrowIfNull(properties);
        Layer layer = GetLayer(layerId);
        LayerProperties previous = layer.Properties;
        layer.Properties = properties with { Opacity = Math.Clamp(properties.Opacity, 0f, 1f), Name = properties.Name ?? string.Empty };
        return previous;
    }

    internal int ReorderLayerCore(Guid layerId, int index)
    {
        Layer layer = GetLayer(layerId);
        int previous = _layers.IndexOf(layer);
        _layers.RemoveAt(previous);
        _layers.Insert(Math.Clamp(index, 0, _layers.Count), layer);
        return previous;
    }

    internal DocumentSettings UpdateSettingsCore(DocumentSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        DocumentSettings previous = Settings;
        Settings = settings with { Width = Math.Clamp(settings.Width, 0, Limits.MaxCoordinate), Height = Math.Clamp(settings.Height, 0, Limits.MaxCoordinate) };
        return previous;
    }

    internal string? SetMetadataCore(string key, string? value)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        _metadata.TryGetValue(key, out string? previous);
        if (value is null) _metadata.Remove(key);
        else _metadata[key] = value;
        return previous;
    }

    private void ValidateElement(InkElement element)
    {
        Rect b = element.LocalBounds;
        if (!float.IsFinite(b.X) || !float.IsFinite(b.Y) || !float.IsFinite(b.Width) || !float.IsFinite(b.Height))
            throw new InvalidOperationException($"{element.TypeName} has non-finite bounds.");
        float max = Limits.MaxCoordinate;
        if (MathF.Abs(b.Left) > max || MathF.Abs(b.Top) > max || MathF.Abs(b.Right) > max || MathF.Abs(b.Bottom) > max)
            throw new InvalidOperationException($"{element.TypeName} lies outside the allowed coordinate range (+/-{max}).");
        if (element is Stroke stroke)
        {
            if (stroke.Points.Count > Limits.MaxPointsPerStroke) throw new InvalidOperationException($"Stroke has {stroke.Points.Count} points; the limit is {Limits.MaxPointsPerStroke}.");
            if (_totalPoints + stroke.Points.Count > Limits.MaxTotalPoints) throw new InvalidOperationException($"The document limit of {Limits.MaxTotalPoints} stroke points was reached.");
            if (stroke.Brush.Width > Limits.MaxStrokeWidth) throw new InvalidOperationException($"Brush width {stroke.Brush.Width} exceeds the limit of {Limits.MaxStrokeWidth}.");
            stroke.Brush.Validate();
        }
    }

    /// <summary>
    /// Creates an independent copy. Elements are immutable and shared; layers, settings and metadata are copied.
    /// </summary>
    public InkDocument Clone()
    {
        var copy = new InkDocument(Id, Settings, Limits, withoutLayers: true);
        foreach (Layer layer in _layers) copy.InsertLayerCore(layer.Id, layer.Properties, null, layer.Elements.ToArray());
        foreach ((string k, string v) in _metadata) copy._metadata[k] = v;
        copy.Revision = Revision;
        return copy;
    }

    /// <inheritdoc />
    public override string ToString() => FormattableString.Invariant($"InkDocument {Id:N}: {_layers.Count} layers, {ElementCount} elements, revision {Revision}");
}
