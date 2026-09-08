using BlazorInk.Operations;

namespace BlazorInk.History;

/// <summary>One undoable step.</summary>
public sealed record HistoryEntry(DrawingOperation Forward, DrawingOperation Inverse, string Label, DateTimeOffset Timestamp);

/// <summary>
/// Undo/redo for an <see cref="InkDocument"/>. Records the inverse of every local change as it happens, so no
/// document snapshots are ever taken: memory usage is proportional to what changed, and elements referenced by
/// history share their point data with the document.
/// </summary>
/// <remarks>
/// Attach one manager per document. Changes with origin <see cref="ChangeOrigin.Remote"/> or <see cref="ChangeOrigin.Load"/>
/// are not recorded (remote changes belong to the other user's history). Use <see cref="BeginGroup"/> to merge a
/// gesture's many operations (e.g. every frame of a drag) into a single undo step.
/// </remarks>
public sealed class HistoryManager : IDisposable
{
    private readonly InkDocument _document;
    private readonly List<HistoryEntry> _undo = new();
    private readonly List<HistoryEntry> _redo = new();
    private List<HistoryEntry>? _group;
    private string? _groupLabel;
    private int _groupDepth;
    private bool _applying;
    private bool _disposed;

    /// <summary>Creates a manager and starts recording the document's changes.</summary>
    public HistoryManager(InkDocument document, int limit = 200)
    {
        ArgumentNullException.ThrowIfNull(document);
        _document = document;
        Limit = limit;
        document.Changed += OnDocumentChanged;
    }

    /// <summary>Maximum number of undo steps kept; older steps are dropped. Zero disables history.</summary>
    public int Limit { get; set; }

    /// <summary>True when there is something to undo.</summary>
    public bool CanUndo => _undo.Count > 0;

    /// <summary>True when there is something to redo.</summary>
    public bool CanRedo => _redo.Count > 0;

    /// <summary>Number of undo steps.</summary>
    public int UndoCount => _undo.Count;

    /// <summary>Number of redo steps.</summary>
    public int RedoCount => _redo.Count;

    /// <summary>Label of the next undo step, or null.</summary>
    public string? UndoLabel => _undo.Count > 0 ? _undo[^1].Label : null;

    /// <summary>Label of the next redo step, or null.</summary>
    public string? RedoLabel => _redo.Count > 0 ? _redo[^1].Label : null;

    /// <summary>Undo steps, oldest first.</summary>
    public IReadOnlyList<HistoryEntry> UndoStack => _undo;

    /// <summary>Redo steps, oldest first.</summary>
    public IReadOnlyList<HistoryEntry> RedoStack => _redo;

    /// <summary>Raised whenever the undo or redo stack changes.</summary>
    public event EventHandler? Changed;

    /// <summary>Undoes the most recent step. Returns false when there is nothing to undo.</summary>
    public bool Undo()
    {
        if (_undo.Count == 0 || _groupDepth > 0) return false;
        HistoryEntry entry = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        ApplyEntry(entry.Inverse, ChangeOrigin.Undo);
        _redo.Add(entry);
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>Redoes the most recently undone step. Returns false when there is nothing to redo.</summary>
    public bool Redo()
    {
        if (_redo.Count == 0 || _groupDepth > 0) return false;
        HistoryEntry entry = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        ApplyEntry(entry.Forward, ChangeOrigin.Redo);
        _undo.Add(entry);
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private void ApplyEntry(DrawingOperation operation, ChangeOrigin origin)
    {
        _applying = true;
        try
        {
            _document.Apply(operation, origin);
        }
        finally
        {
            _applying = false;
        }
    }

    /// <summary>Clears both stacks.</summary>
    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Starts a group: all local changes until the returned scope is disposed become one undo step.
    /// Groups nest; the outermost one wins.
    /// </summary>
    public IDisposable BeginGroup(string? label = null)
    {
        if (_groupDepth == 0)
        {
            _group = new List<HistoryEntry>();
            _groupLabel = label;
        }
        _groupDepth++;
        return new GroupScope(this);
    }

    private void EndGroup()
    {
        if (--_groupDepth > 0) return;
        List<HistoryEntry> entries = _group!;
        _group = null;
        if (entries.Count == 0) return;
        HistoryEntry merged = Merge(entries, _groupLabel);
        Push(merged);
    }

    private static HistoryEntry Merge(List<HistoryEntry> entries, string? label)
    {
        if (entries.Count == 1 && label is null) return entries[0];
        var forward = new List<DrawingOperation>(entries.Count);
        var inverse = new List<DrawingOperation>(entries.Count);
        foreach (HistoryEntry e in entries) forward.Add(e.Forward);
        for (int i = entries.Count - 1; i >= 0; i--) inverse.Add(entries[i].Inverse);
        string l = label ?? entries[0].Label;
        return new HistoryEntry(new BatchOperation(forward, l), new BatchOperation(inverse, l), l, entries[^1].Timestamp);
    }

    private void OnDocumentChanged(object? sender, DocumentChangedEventArgs e)
    {
        if (_applying || e.Origin != ChangeOrigin.Local || Limit <= 0) return;
        var entry = new HistoryEntry(e.Operation, e.Inverse, e.Operation.Label, DateTimeOffset.UtcNow);
        if (_groupDepth > 0)
        {
            _group!.Add(entry);
            return;
        }
        Push(entry);
    }

    private void Push(HistoryEntry entry)
    {
        _redo.Clear();
        _undo.Add(entry);
        if (_undo.Count > Limit) _undo.RemoveRange(0, _undo.Count - Limit);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private sealed class GroupScope(HistoryManager owner) : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            owner.EndGroup();
        }
    }

    /// <summary>Stops recording.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _document.Changed -= OnDocumentChanged;
    }
}
