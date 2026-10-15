namespace BlazorInk.Operations;

/// <summary>
/// An atomic, invertible change to an <see cref="InkDocument"/>. Operations are the unit of undo/redo, of
/// document diffing and of collaboration: every mutation of a document is expressed as one.
/// </summary>
/// <remarks>
/// Operations reference elements by id and carry the new element instances (which share their point data), so
/// applying, inverting and transmitting them never copies stroke points.
/// </remarks>
public abstract record DrawingOperation
{
    /// <summary>Short label for history UIs, e.g. "Add stroke".</summary>
    public abstract string Label { get; }

    /// <summary>Applies the operation to a document and returns its inverse. Throws when the operation cannot be applied.</summary>
    internal abstract DrawingOperation ApplyCore(InkDocument document);

    /// <summary>
    /// Whether the operation can be applied to the document in its current state (e.g. the referenced element exists).
    /// Used to skip stale remote operations instead of throwing.
    /// </summary>
    public abstract bool CanApply(InkDocument document);
}

/// <summary>Adds an element to a layer.</summary>
/// <param name="LayerId">Target layer.</param>
/// <param name="Element">The element to add. Its id must not already exist in the document.</param>
/// <param name="Index">Insertion index within the layer, or null to append on top.</param>
public sealed record AddElementOperation(Guid LayerId, InkElement Element, int? Index = null) : DrawingOperation
{
    /// <inheritdoc />
    public override string Label => "Add " + Element.TypeName.ToLowerInvariant();

    /// <inheritdoc />
    public override bool CanApply(InkDocument document) => document.TryGetLayer(LayerId, out _) && !document.ContainsElement(Element.Id);

    internal override DrawingOperation ApplyCore(InkDocument document)
    {
        document.InsertElementCore(LayerId, Element, Index);
        return new RemoveElementOperation(Element.Id);
    }
}

/// <summary>Removes an element.</summary>
/// <param name="ElementId">The element to remove.</param>
public sealed record RemoveElementOperation(Guid ElementId) : DrawingOperation
{
    /// <inheritdoc />
    public override string Label => "Delete";

    /// <inheritdoc />
    public override bool CanApply(InkDocument document) => document.ContainsElement(ElementId);

    internal override DrawingOperation ApplyCore(InkDocument document)
    {
        (Layer layer, int index, InkElement removed) = document.RemoveElementCore(ElementId);
        return new AddElementOperation(layer.Id, removed, index);
    }
}

/// <summary>Replaces an element with a new version of itself (same id). Covers move, transform, style changes and edits.</summary>
/// <param name="Element">The new element. The document assigns it the next version number.</param>
public sealed record ReplaceElementOperation(InkElement Element) : DrawingOperation
{
    /// <inheritdoc />
    public override string Label => "Edit " + Element.TypeName.ToLowerInvariant();

    /// <inheritdoc />
    public override bool CanApply(InkDocument document) => document.ContainsElement(Element.Id);

    internal override DrawingOperation ApplyCore(InkDocument document)
    {
        InkElement previous = document.ReplaceElementCore(Element);
        return new ReplaceElementOperation(previous);
    }
}

/// <summary>Moves an element to a new z-order index and/or a different layer.</summary>
/// <param name="ElementId">The element.</param>
/// <param name="TargetLayerId">Destination layer.</param>
/// <param name="Index">Destination index within the layer (clamped), or null for the top.</param>
public sealed record ReorderElementOperation(Guid ElementId, Guid TargetLayerId, int? Index) : DrawingOperation
{
    /// <inheritdoc />
    public override string Label => "Reorder";

    /// <inheritdoc />
    public override bool CanApply(InkDocument document) => document.ContainsElement(ElementId) && document.TryGetLayer(TargetLayerId, out _);

    internal override DrawingOperation ApplyCore(InkDocument document)
    {
        (Guid previousLayer, int previousIndex) = document.ReorderElementCore(ElementId, TargetLayerId, Index);
        return new ReorderElementOperation(ElementId, previousLayer, previousIndex);
    }
}

/// <summary>Adds a layer, optionally with elements (used to undo a layer removal).</summary>
/// <param name="LayerId">The layer id.</param>
/// <param name="Properties">Layer properties.</param>
/// <param name="Index">Insertion index (bottom = 0), or null for the top.</param>
/// <param name="Elements">Elements to place in the layer, or null.</param>
public sealed record AddLayerOperation(Guid LayerId, LayerProperties Properties, int? Index = null, IReadOnlyList<InkElement>? Elements = null) : DrawingOperation
{
    /// <inheritdoc />
    public override string Label => "Add layer";

    /// <inheritdoc />
    public override bool CanApply(InkDocument document) => !document.TryGetLayer(LayerId, out _);

    internal override DrawingOperation ApplyCore(InkDocument document)
    {
        document.InsertLayerCore(LayerId, Properties, Index, Elements);
        return new RemoveLayerOperation(LayerId);
    }
}

/// <summary>Removes a layer and everything in it.</summary>
/// <param name="LayerId">The layer.</param>
public sealed record RemoveLayerOperation(Guid LayerId) : DrawingOperation
{
    /// <inheritdoc />
    public override string Label => "Remove layer";

    /// <inheritdoc />
    public override bool CanApply(InkDocument document) => document.TryGetLayer(LayerId, out _) && document.Layers.Count > 1;

    internal override DrawingOperation ApplyCore(InkDocument document)
    {
        (int index, Layer removed) = document.RemoveLayerCore(LayerId);
        return new AddLayerOperation(removed.Id, removed.Properties, index, removed.Elements.ToArray());
    }
}

/// <summary>Changes a layer's properties (name, visibility, lock, opacity).</summary>
/// <param name="LayerId">The layer.</param>
/// <param name="Properties">New properties.</param>
public sealed record UpdateLayerOperation(Guid LayerId, LayerProperties Properties) : DrawingOperation
{
    /// <inheritdoc />
    public override string Label => "Change layer";

    /// <inheritdoc />
    public override bool CanApply(InkDocument document) => document.TryGetLayer(LayerId, out _);

    internal override DrawingOperation ApplyCore(InkDocument document)
    {
        LayerProperties previous = document.UpdateLayerCore(LayerId, Properties);
        return new UpdateLayerOperation(LayerId, previous);
    }
}

/// <summary>Moves a layer to a new index in the layer stack.</summary>
/// <param name="LayerId">The layer.</param>
/// <param name="Index">New index (0 = bottom).</param>
public sealed record ReorderLayerOperation(Guid LayerId, int Index) : DrawingOperation
{
    /// <inheritdoc />
    public override string Label => "Reorder layer";

    /// <inheritdoc />
    public override bool CanApply(InkDocument document) => document.TryGetLayer(LayerId, out _);

    internal override DrawingOperation ApplyCore(InkDocument document)
    {
        int previous = document.ReorderLayerCore(LayerId, Index);
        return new ReorderLayerOperation(LayerId, previous);
    }
}

/// <summary>Changes the document settings (size, background).</summary>
/// <param name="Settings">New settings.</param>
public sealed record UpdateSettingsOperation(DocumentSettings Settings) : DrawingOperation
{
    /// <inheritdoc />
    public override string Label => "Change document";

    /// <inheritdoc />
    public override bool CanApply(InkDocument document) => true;

    internal override DrawingOperation ApplyCore(InkDocument document)
    {
        DocumentSettings previous = document.UpdateSettingsCore(Settings);
        return new UpdateSettingsOperation(previous);
    }
}

/// <summary>Sets or removes a document metadata entry.</summary>
/// <param name="Key">Metadata key.</param>
/// <param name="Value">New value, or null to remove.</param>
public sealed record SetMetadataOperation(string Key, string? Value) : DrawingOperation
{
    /// <inheritdoc />
    public override string Label => "Change metadata";

    /// <inheritdoc />
    public override bool CanApply(InkDocument document) => !string.IsNullOrEmpty(Key);

    internal override DrawingOperation ApplyCore(InkDocument document)
    {
        string? previous = document.SetMetadataCore(Key, Value);
        return new SetMetadataOperation(Key, previous);
    }
}

/// <summary>A group of operations applied atomically and undone as one step.</summary>
/// <param name="Operations">Operations in application order.</param>
/// <param name="Label">Optional label; defaults to the first operation's label.</param>
public sealed record BatchOperation(IReadOnlyList<DrawingOperation> Operations, string? Label = null) : DrawingOperation
{
    /// <inheritdoc />
    public override string Label { get; } = Label ?? (Operations.Count > 0 ? Operations[0].Label : "Batch");

    /// <summary>True when the batch contains nothing.</summary>
    public bool IsEmpty => Operations.Count == 0;

    /// <inheritdoc />
    public override bool CanApply(InkDocument document) => Operations.Count == 0 || Operations[0].CanApply(document);

    internal override DrawingOperation ApplyCore(InkDocument document)
    {
        var inverses = new DrawingOperation[Operations.Count];
        int applied = 0;
        try
        {
            for (int i = 0; i < Operations.Count; i++)
            {
                inverses[Operations.Count - 1 - i] = Operations[i].ApplyCore(document);
                applied++;
            }
        }
        catch
        {
            // Roll back the partially applied batch so the document is never left half-changed. The rollback is
            // never reported, so it has to restore elements exactly: a replacement undone the ordinary way gets a
            // newer version, and this replica would then out-rank every collaborator's next edit of the element.
            bool restoring = document.Restoring;
            document.Restoring = true;
            try
            {
                for (int i = 0; i < applied; i++) inverses[Operations.Count - applied + i].ApplyCore(document);
            }
            finally
            {
                document.Restoring = restoring;
            }
            throw;
        }
        return new BatchOperation(inverses, Label);
    }
}
