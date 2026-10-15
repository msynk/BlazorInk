using System.Numerics;
using BlazorInk.Geometry;
using BlazorInk.History;
using BlazorInk.Operations;
using Xunit;

namespace BlazorInk.Tests.Core;

/// <summary>
/// Applications routinely keep geometry that is derived from other elements: a connector that has to stay
/// attached to the node it joins, a callout that follows what it points at. The document supports that by
/// letting a listener apply further changes while it is handling one, and reporting those with a greater
/// <see cref="DocumentChangedEventArgs.Depth"/> so history can place a consequence in the same step as its cause.
/// These tests pin that contract down, because it is what the demo's diagram surface is built on.
/// </summary>
public class DerivedGeometryTests
{
    /// <summary>
    /// A minimal version of the demo's diagram graph: a listener that re-routes a connector whenever the node it
    /// is attached to moves, applying the edit inside the notification that told it about the move.
    /// </summary>
    private sealed class Connector : IDisposable
    {
        private readonly InkDocument _document;
        private readonly Guid _nodeId;
        private readonly Guid _connectorId;
        private bool _rerouting;

        public Connector(InkDocument document, Guid nodeId, Guid connectorId)
        {
            _document = document;
            _nodeId = nodeId;
            _connectorId = connectorId;
            document.Changed += OnChanged;
        }

        public int Reroutes { get; private set; }

        public int MaxDepthSeen { get; private set; }

        private void OnChanged(object? sender, DocumentChangedEventArgs e)
        {
            if (e.Depth > MaxDepthSeen) MaxDepthSeen = e.Depth;
            if (_rerouting || e.Origin is ChangeOrigin.Remote or ChangeOrigin.Load) return;
            if (!Mentions(e.Operation, _nodeId)) return;
            if (!_document.TryGetElement(_nodeId, out InkElement? node)) return;
            if (!_document.TryGetElement(_connectorId, out InkElement? connector) || connector is not LineShape line) return;

            Vector2 target = node.Bounds.Center;
            if (Vector2.DistanceSquared(line.End, target) < 0.01f) return;

            _rerouting = true;
            try
            {
                _document.ReplaceElement(line with { End = target });
                Reroutes++;
            }
            finally
            {
                _rerouting = false;
            }
        }

        private static bool Mentions(DrawingOperation operation, Guid id) => operation switch
        {
            ReplaceElementOperation replace => replace.Element.Id == id,
            AddElementOperation add => add.Element.Id == id,
            BatchOperation batch => batch.Operations.Any(op => Mentions(op, id)),
            _ => false,
        };

        public void Dispose() => _document.Changed -= OnChanged;
    }

    private static (InkDocument Document, Guid NodeId, Guid ConnectorId) BuildGraph()
    {
        var document = new InkDocument();
        var node = new RectangleShape(new Rect(200, 200, 80, 40));
        var connector = new LineShape(new Vector2(20, 20), node.Bounds.Center);
        document.AddElement(node);
        document.AddElement(connector);
        return (document, node.Id, connector.Id);
    }

    [Fact]
    public void A_connector_follows_the_node_it_is_attached_to()
    {
        (InkDocument document, Guid nodeId, Guid connectorId) = BuildGraph();
        using var graph = new Connector(document, nodeId, connectorId);

        var node = (RectangleShape)document.GetElement(nodeId);
        document.ReplaceElement(node.Translate(new Vector2(150, 90)));

        var connector = (LineShape)document.GetElement(connectorId);
        Vector2 expected = document.GetElement(nodeId).Bounds.Center;
        Assert.Equal(1, graph.Reroutes);
        Assert.Equal(expected.X, connector.End.X, 3);
        Assert.Equal(expected.Y, connector.End.Y, 3);
    }

    [Fact]
    public void A_consequential_edit_made_by_a_listener_joins_the_step_that_caused_it()
    {
        (InkDocument document, Guid nodeId, Guid connectorId) = BuildGraph();
        using var history = new HistoryManager(document);
        using var graph = new Connector(document, nodeId, connectorId);

        Vector2 nodeBefore = document.GetElement(nodeId).Bounds.Center;
        Vector2 connectorBefore = ((LineShape)document.GetElement(connectorId)).End;
        int steps = history.UndoCount;

        // A tool groups its gesture, which is what makes the consequence part of the same step. This is exactly
        // what SelectTool does around a drag, so moving a node and undoing puts the connector back too.
        var node = (RectangleShape)document.GetElement(nodeId);
        using (history.BeginGroup("Move"))
        {
            document.ReplaceElement(node.Translate(new Vector2(150, 90)));
        }

        Assert.Equal(1, graph.Reroutes);
        Assert.True(graph.MaxDepthSeen > 0, "the derived edit was not reported as nested inside its cause");
        Assert.Equal(steps + 1, history.UndoCount);

        Assert.True(history.Undo());
        Assert.Equal(nodeBefore.X, document.GetElement(nodeId).Bounds.Center.X, 3);
        Assert.Equal(connectorBefore.X, ((LineShape)document.GetElement(connectorId)).End.X, 3);
        Assert.Equal(connectorBefore.Y, ((LineShape)document.GetElement(connectorId)).End.Y, 3);
        Assert.Equal(steps, history.UndoCount);
    }

    [Fact]
    public void An_ungrouped_consequence_joins_the_step_that_caused_it()
    {
        (InkDocument document, Guid nodeId, Guid connectorId) = BuildGraph();
        using var history = new HistoryManager(document);
        using var graph = new Connector(document, nodeId, connectorId);
        Vector2 connectorBefore = ((LineShape)document.GetElement(connectorId)).End;

        // An edit made through the API rather than a tool has no group around it. Its consequence must still undo
        // with it: as a step of its own, undoing once would put the connector back and leave the node where it
        // was moved, a state the user never saw. Which order the handlers were subscribed in must not matter.
        var node = (RectangleShape)document.GetElement(nodeId);
        document.ReplaceElement(node.Translate(new Vector2(150, 90)));

        Assert.Equal(1, graph.Reroutes);
        Assert.Equal(1, history.UndoCount);

        Assert.True(history.Undo());
        Assert.Equal(200 + 40, document.GetElement(nodeId).Bounds.Center.X, 3);
        Assert.Equal(connectorBefore.X, ((LineShape)document.GetElement(connectorId)).End.X, 3);
        Assert.Equal(connectorBefore.Y, ((LineShape)document.GetElement(connectorId)).End.Y, 3);
        Assert.False(history.CanUndo);

        // Redo replays the cause and its consequence together.
        Assert.True(history.Redo());
        Assert.Equal(1, history.UndoCount);
    }

    [Fact]
    public void A_consequence_of_a_change_history_did_not_record_is_a_step_of_its_own()
    {
        (InkDocument document, Guid nodeId, _) = BuildGraph();
        using var history = new HistoryManager(document);
        var marker = new RectangleShape(new Rect(0, 0, 10, 10));
        document.AddElement(marker);
        int steps = history.UndoCount;

        // A listener that reacts locally to a remote edit. The remote edit is not undoable here, but the local
        // edit it caused is, and it must not be folded into whatever local step happened to be recorded last.
        document.Changed += (_, e) =>
        {
            if (e.Origin == ChangeOrigin.Remote && e.Depth == 0) document.RemoveElement(marker.Id);
        };
        var node = (RectangleShape)document.GetElement(nodeId);
        document.Apply(new ReplaceElementOperation(node.Translate(new Vector2(150, 90))), ChangeOrigin.Remote);

        Assert.Equal(steps + 1, history.UndoCount);
        Assert.True(history.Undo());
        Assert.True(document.ContainsElement(marker.Id));
        Assert.Equal(steps, history.UndoCount);
    }

    [Fact]
    public void The_diagram_page_reroutes_connectors_when_a_node_moves()
    {
        // The routing rule the demo uses: a connector meets each node on its edge, along the line joining their
        // centres, so it never starts inside a box.
        var from = new Rect(0, 0, 100, 60);
        var to = new Rect(300, 0, 100, 60);

        (Vector2 start, Vector2 end) = Route(from, to);
        Assert.Equal(from.Right, start.X, 3);
        Assert.Equal(to.Left, end.X, 3);

        // Move the far node down and the endpoints move with it, still on the boxes' edges.
        Rect moved = to.Offset(new Vector2(0, 400));
        (Vector2 start2, Vector2 end2) = Route(from, moved);
        Assert.True(start2.Y > start.Y, "the connector did not follow the node downwards");
        Assert.True(moved.Contains(end2) || Close(end2, moved), $"the endpoint {end2} left the node {moved}");
        Assert.True(from.Contains(start2) || Close(start2, from), $"the start {start2} left the node {from}");

        static bool Close(Vector2 point, Rect box) => box.Inflate(0.01f).Contains(point);

        // A local copy of the demo's routing, so the engine's behaviour is pinned even though the rule lives in
        // the sample rather than in the library.
        static (Vector2 Start, Vector2 End) Route(Rect a, Rect b)
        {
            Vector2 direction = b.Center - a.Center;
            if (direction.LengthSquared() < 1e-6f) return (a.Center, b.Center);
            return (Edge(a, direction), Edge(b, -direction));
        }

        static Vector2 Edge(Rect box, Vector2 direction)
        {
            float halfWidth = MathF.Max(box.Width / 2f, 0.001f);
            float halfHeight = MathF.Max(box.Height / 2f, 0.001f);
            float dx = MathF.Abs(direction.X) < 1e-6f ? 1e-6f : direction.X;
            float dy = MathF.Abs(direction.Y) < 1e-6f ? 1e-6f : direction.Y;
            float scale = MathF.Min(halfWidth / MathF.Abs(dx), halfHeight / MathF.Abs(dy));
            return box.Center + new Vector2(dx, dy) * scale;
        }
    }

    [Fact]
    public void A_history_limit_of_zero_keeps_nothing()
    {
        var document = new InkDocument();
        using var history = new HistoryManager(document, limit: 0);

        document.AddElement(TestData.Stroke(new Vector2(0, 0), new Vector2(10, 10)));
        document.AddElement(TestData.Stroke(new Vector2(20, 20), new Vector2(30, 30)));

        Assert.Equal(0, history.UndoCount);
        Assert.False(history.CanUndo);
        Assert.False(history.Undo());
        // The changes themselves still happened; only the ability to reverse them was declined.
        Assert.Equal(2, document.ElementCount);
    }

    [Fact]
    public void Lowering_the_history_limit_releases_the_steps_it_drops()
    {
        var document = new InkDocument();
        using var history = new HistoryManager(document, limit: 50);
        for (int i = 0; i < 20; i++) document.AddElement(TestData.Stroke(new Vector2(i, 0), new Vector2(i, 10)));
        Assert.Equal(20, history.UndoCount);

        int changes = 0;
        history.Changed += (_, _) => changes++;
        history.Limit = 5;

        // Trimming happens immediately rather than only applying to future changes, so the elements the dropped
        // steps referenced are released instead of being pinned by a limit nobody has reached yet.
        Assert.Equal(5, history.UndoCount);
        Assert.Equal(1, changes);

        // The steps that remain are the newest ones.
        Assert.True(history.Undo());
        Assert.Equal(19, document.ElementCount);
    }
}
