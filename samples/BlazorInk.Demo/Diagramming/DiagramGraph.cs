using System.Numerics;
using BlazorInk;
using BlazorInk.Geometry;
using BlazorInk.Rendering;

namespace BlazorInk.Demo.Diagramming;

/// <summary>
/// Keeps a diagram's connectors attached to the nodes they join.
/// </summary>
/// <remarks>
/// <para>
/// This is the demo's answer to "can an application build derived geometry on top of the document model without
/// the library knowing anything about diagrams?". Nothing here is part of BlazorInk: a node is an ordinary
/// <see cref="RectangleShape"/> and a connector is an ordinary <see cref="LineShape"/>. What makes them a
/// diagram is the element metadata, which the file format preserves, and this listener.
/// </para>
/// <para>
/// When a node moves the graph reroutes every connector touching it, applying those edits while it is still
/// handling the change that caused them. The document reports such an edit with a greater
/// <see cref="DocumentChangedEventArgs.Depth"/>, and history places it in the same undo step as its cause, so
/// dragging a node and undoing puts both the node and its connectors back in one press.
/// </para>
/// <para>
/// A node's label is part of the node: its text is stored in the node's metadata. What is drawn is an ordinary
/// <see cref="TextElement"/> derived from it and kept centred on the node, on the layer named by
/// <see cref="LabelLayerId"/>. Deleting a node deletes its label and its connectors in the same undo step.
/// </para>
/// </remarks>
public sealed class DiagramGraph : IDisposable
{
    /// <summary>Metadata key marking an element's part in the diagram.</summary>
    public const string RoleKey = "diagram-role";

    /// <summary>Metadata key on a connector naming the node it starts at.</summary>
    public const string FromKey = "diagram-from";

    /// <summary>Metadata key on a connector naming the node it ends at.</summary>
    public const string ToKey = "diagram-to";

    /// <summary>Role value for a node.</summary>
    public const string NodeRole = "node";

    /// <summary>Role value for a connector.</summary>
    public const string ConnectorRole = "connector";

    /// <summary>Role value for the text drawn on a node.</summary>
    public const string LabelRole = "label";

    /// <summary>Metadata key on a node holding its label text.</summary>
    public const string LabelKey = "diagram-label";

    /// <summary>Metadata key on a label naming the node it belongs to.</summary>
    public const string NodeKey = "diagram-node";

    /// <summary>Font size of node labels, in document units.</summary>
    public const float LabelFontSize = 14f;

    private readonly InkDocument _document;
    private bool _rerouting;
    private bool _disposed;

    /// <summary>Attaches to a document and starts keeping its connectors routed.</summary>
    public DiagramGraph(InkDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        _document = document;
        _document.Changed += OnDocumentChanged;
    }

    /// <summary>Number of connectors rerouted since the graph was created. Used by the demo's status line.</summary>
    public int RerouteCount { get; private set; }

    /// <summary>
    /// The layer labels are drawn on, or null to draw them on the active layer. A locked layer above the nodes
    /// keeps a click on the text selecting the node rather than the label.
    /// </summary>
    public Guid? LabelLayerId { get; set; }

    /// <summary>True when an element carries the given diagram role.</summary>
    public static bool HasRole(InkElement element, string role)
    {
        ArgumentNullException.ThrowIfNull(element);
        return element.Metadata is { } metadata
            && metadata.TryGetValue(RoleKey, out string? value)
            && string.Equals(value, role, StringComparison.Ordinal);
    }

    /// <summary>Creates a node: a rounded rectangle carrying the metadata that makes it one.</summary>
    public static RectangleShape CreateNode(Rect box, ShapeStyle style, string? label = null)
        => (RectangleShape)WithLabel(new RectangleShape(box.Normalize())
        {
            Style = style,
            CornerRadius = 8f,
            Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [RoleKey] = NodeRole,
            },
        }, label);

    /// <summary>The label text stored on a node, or null when it has none.</summary>
    public static string? LabelOf(InkElement node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return node.Metadata is { } metadata && metadata.TryGetValue(LabelKey, out string? value) && value.Length > 0 ? value : null;
    }

    /// <summary>Returns the node with a new label. The drawn text follows when the change is applied.</summary>
    public static InkElement WithLabel(InkElement node, string? label)
    {
        ArgumentNullException.ThrowIfNull(node);
        label = string.IsNullOrWhiteSpace(label) ? null : label.Trim();
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
        if (node.Metadata is { } existing)
        {
            foreach ((string key, string value) in existing) metadata[key] = value;
        }
        if (label is null) metadata.Remove(LabelKey);
        else metadata[LabelKey] = label;
        return node with
        {
            Metadata = metadata,
            Description = label is null ? "Diagram node" : "Diagram node: " + label,
        };
    }

    /// <summary>The text element drawn on a node, if there is one.</summary>
    public TextElement? LabelElementOf(Guid nodeId)
    {
        string id = nodeId.ToString("N");
        foreach (InkElement element in _document.AllElements)
        {
            if (element is TextElement text && HasRole(text, LabelRole) && Endpoint(text, NodeKey) == id) return text;
        }
        return null;
    }

    /// <summary>Creates or updates the text drawn on a node so it shows the node's label, centred on it.</summary>
    private static TextElement LayoutLabel(InkElement node, string text, TextElement? existing)
    {
        Rect box = node.Bounds;
        TextStyle style = TextStyle.Default with
        {
            FontSize = LabelFontSize,
            Align = TextAlign.Center,
            Color = node is ShapeElement shape ? shape.Style.StrokeColor : InkColor.Black,
        };
        // Centre-aligned text is positioned by the middle of its top edge.
        var position = new Vector2(box.Center.X, box.Center.Y - LabelFontSize * style.LineHeight / 2f);
        TextElement label = existing ?? new TextElement(position, text);
        return label with
        {
            Position = position,
            Text = text,
            Transform = Matrix3x2.Identity,
            Style = style,
            Size = null,
            Padding = 0f,
            Description = "Label: " + text,
            Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [RoleKey] = LabelRole,
                [NodeKey] = node.Id.ToString("N"),
            },
        };
    }

    /// <summary>Creates a connector between two nodes, already routed to their edges.</summary>
    public static LineShape CreateConnector(InkElement from, InkElement to, ShapeStyle style)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        (Vector2 start, Vector2 end) = Route(from.Bounds, to.Bounds);
        return new LineShape(start, end)
        {
            Style = style,
            EndArrow = true,
            Description = "Connector",
            Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [RoleKey] = ConnectorRole,
                [FromKey] = from.Id.ToString("N"),
                [ToKey] = to.Id.ToString("N"),
            },
        };
    }

    /// <summary>
    /// Picks where a connector meets two boxes: the point on each box's edge along the line joining their
    /// centres. Simple, stable under rotation of the drag, and never leaves the line starting inside a node.
    /// </summary>
    public static (Vector2 Start, Vector2 End) Route(Rect from, Rect to)
    {
        Vector2 a = from.Center;
        Vector2 b = to.Center;
        Vector2 direction = b - a;
        if (direction.LengthSquared() < 1e-6f) return (a, b);
        return (EdgePoint(from, direction), EdgePoint(to, -direction));
    }

    /// <summary>The point where a ray leaving the rectangle's centre crosses its edge.</summary>
    private static Vector2 EdgePoint(Rect box, Vector2 direction)
    {
        Vector2 centre = box.Center;
        float halfWidth = MathF.Max(box.Width / 2f, 0.001f);
        float halfHeight = MathF.Max(box.Height / 2f, 0.001f);
        float dx = MathF.Abs(direction.X) < 1e-6f ? 1e-6f : direction.X;
        float dy = MathF.Abs(direction.Y) < 1e-6f ? 1e-6f : direction.Y;
        // Scale the ray until it touches whichever pair of edges it reaches first.
        float scale = MathF.Min(halfWidth / MathF.Abs(dx), halfHeight / MathF.Abs(dy));
        return centre + new Vector2(dx, dy) * scale;
    }

    /// <summary>The connectors currently attached to a node.</summary>
    public IEnumerable<LineShape> ConnectorsOf(Guid nodeId)
    {
        string id = nodeId.ToString("N");
        foreach (InkElement element in _document.AllElements)
        {
            if (element is not LineShape line || !HasRole(line, ConnectorRole)) continue;
            if (Endpoint(line, FromKey) == id || Endpoint(line, ToKey) == id) yield return line;
        }
    }

    private static string? Endpoint(InkElement connector, string key)
        => connector.Metadata is { } metadata && metadata.TryGetValue(key, out string? value) ? value : null;

    private void OnDocumentChanged(object? sender, DocumentChangedEventArgs e)
    {
        // Remote and loaded changes are already consistent on the sender's side, and an edit of our own must
        // not trigger another one.
        if (_disposed || _rerouting) return;
        if (e.Origin is ChangeOrigin.Remote or ChangeOrigin.Load) return;

        List<Guid> moved = [];
        List<Guid> removed = [];
        Collect(e.Operation, moved, removed);
        // Undo and redo restore a state that was already consistent, including whatever left with a node.
        if (e.Origin is ChangeOrigin.Undo or ChangeOrigin.Redo) removed.Clear();
        if (moved.Count == 0 && removed.Count == 0) return;

        var updates = new List<InkElement>();
        var additions = new List<InkElement>();
        var deletions = new HashSet<Guid>();
        var seen = new HashSet<Guid>();
        foreach (Guid nodeId in moved)
        {
            foreach (LineShape connector in ConnectorsOf(nodeId))
            {
                if (!seen.Add(connector.Id)) continue;
                if (Reroute(connector) is { } routed) updates.Add(routed);
            }
            if (_document.TryGetElement(nodeId, out InkElement? node)) SyncLabel(node, updates, additions, deletions);
        }
        foreach (Guid nodeId in removed)
        {
            // Whatever was attached to a deleted node goes with it: a connector to nothing is not a diagram.
            foreach (LineShape connector in ConnectorsOf(nodeId)) deletions.Add(connector.Id);
            if (LabelElementOf(nodeId) is { } label) deletions.Add(label.Id);
        }
        if (updates.Count == 0 && additions.Count == 0 && deletions.Count == 0) return;

        _rerouting = true;
        try
        {
            // Applied inside the notification on purpose: the document reports it as a consequence of the change
            // being handled, and history folds it into the same undo step.
            using (_document.BeginBatch("Update diagram"))
            {
                if (updates.Count > 0) _document.ReplaceElements(updates, "Route connectors");
                foreach (InkElement label in additions) _document.AddElement(label, LabelLayerId);
                if (deletions.Count > 0) _document.RemoveElements(deletions);
            }
            RerouteCount += updates.Count(u => u is LineShape);
        }
        catch (InvalidOperationException)
        {
            // A connector whose node was deleted in the same batch. The connector is left where it was rather
            // than failing the edit that caused this.
        }
        finally
        {
            _rerouting = false;
        }
    }

    /// <summary>Brings a node's drawn label in line with the label stored on the node.</summary>
    private void SyncLabel(InkElement node, List<InkElement> updates, List<InkElement> additions, HashSet<Guid> deletions)
    {
        if (!HasRole(node, NodeRole)) return;
        string? text = LabelOf(node);
        TextElement? existing = LabelElementOf(node.Id);
        if (text is null)
        {
            if (existing is not null) deletions.Add(existing.Id);
            return;
        }
        TextElement laidOut = LayoutLabel(node, text, existing);
        if (existing is null) additions.Add(laidOut);
        else if (existing.Position != laidOut.Position || existing.Text != laidOut.Text
                 || existing.Style != laidOut.Style || !existing.Transform.IsIdentity)
        {
            updates.Add(laidOut);
        }
    }

    /// <summary>Returns the connector with its endpoints moved back onto its nodes, or null when nothing changed.</summary>
    private LineShape? Reroute(LineShape connector)
    {
        string? fromId = Endpoint(connector, FromKey);
        string? toId = Endpoint(connector, ToKey);
        if (fromId is null || toId is null) return null;
        if (!Guid.TryParseExact(fromId, "N", out Guid from) || !Guid.TryParseExact(toId, "N", out Guid to)) return null;
        if (!_document.TryGetElement(from, out InkElement? fromNode) || !_document.TryGetElement(to, out InkElement? toNode)) return null;

        (Vector2 start, Vector2 end) = Route(fromNode.Bounds, toNode.Bounds);
        if (Close(connector.Start, start) && Close(connector.End, end)) return null;
        // The connector's own transform is folded away: its geometry is the route, so a stale transform from an
        // earlier drag would move it off the nodes again.
        return connector with { Start = start, End = end, Transform = Matrix3x2.Identity };
    }

    private static bool Close(Vector2 a, Vector2 b) => Vector2.DistanceSquared(a, b) < 0.01f;

    /// <summary>Finds the nodes an operation moved, replaced or added, and the elements it removed.</summary>
    private static void Collect(Operations.DrawingOperation operation, List<Guid> moved, List<Guid> removed)
    {
        switch (operation)
        {
            case Operations.ReplaceElementOperation replace when HasRole(replace.Element, NodeRole):
                moved.Add(replace.Element.Id);
                break;
            case Operations.AddElementOperation add when HasRole(add.Element, NodeRole):
                moved.Add(add.Element.Id);
                break;
            case Operations.RemoveElementOperation remove:
                // The element is already gone, so its role cannot be checked; anything attached to it is enough.
                removed.Add(remove.ElementId);
                break;
            case Operations.BatchOperation batch:
                foreach (Operations.DrawingOperation inner in batch.Operations) Collect(inner, moved, removed);
                break;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _document.Changed -= OnDocumentChanged;
    }
}
