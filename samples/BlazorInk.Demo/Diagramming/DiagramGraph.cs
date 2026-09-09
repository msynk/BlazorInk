using System.Numerics;
using BlazorInk;
using BlazorInk.Geometry;

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
        => new(box.Normalize())
        {
            Style = style,
            CornerRadius = 8f,
            Description = label is null ? "Diagram node" : "Diagram node: " + label,
            Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [RoleKey] = NodeRole,
            },
        };

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
        // Remote and loaded changes are already consistent on the sender's side, and a reroute of our own must
        // not trigger another one.
        if (_disposed || _rerouting) return;
        if (e.Origin is ChangeOrigin.Remote or ChangeOrigin.Load) return;

        List<Guid>? moved = CollectMovedNodes(e.Operation);
        if (moved is null || moved.Count == 0) return;

        var updates = new List<InkElement>();
        var seen = new HashSet<Guid>();
        foreach (Guid nodeId in moved)
        {
            foreach (LineShape connector in ConnectorsOf(nodeId))
            {
                if (!seen.Add(connector.Id)) continue;
                if (Reroute(connector) is { } routed) updates.Add(routed);
            }
        }
        if (updates.Count == 0) return;

        _rerouting = true;
        try
        {
            // Applied inside the notification on purpose: the document reports it as a consequence of the change
            // being handled, and history folds it into the same undo step.
            _document.ReplaceElements(updates, "Route connectors");
            RerouteCount += updates.Count;
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

    /// <summary>Finds the nodes an operation moved, replaced or added.</summary>
    private List<Guid>? CollectMovedNodes(Operations.DrawingOperation operation)
    {
        List<Guid>? result = null;
        Collect(operation, ref result);
        return result;

        void Collect(Operations.DrawingOperation op, ref List<Guid>? into)
        {
            switch (op)
            {
                case Operations.ReplaceElementOperation replace when HasRole(replace.Element, NodeRole):
                    (into ??= []).Add(replace.Element.Id);
                    break;
                case Operations.AddElementOperation add when HasRole(add.Element, NodeRole):
                    (into ??= []).Add(add.Element.Id);
                    break;
                case Operations.BatchOperation batch:
                    foreach (Operations.DrawingOperation inner in batch.Operations) Collect(inner, ref into);
                    break;
            }
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
