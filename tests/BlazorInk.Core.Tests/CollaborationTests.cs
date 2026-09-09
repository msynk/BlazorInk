using System.Numerics;
using BlazorInk.Brushes;
using BlazorInk.Collaboration;
using BlazorInk.Geometry;
using BlazorInk.Operations;
using BlazorInk.Serialization;
using Xunit;

namespace BlazorInk.Tests.Core;

/// <summary>An in-memory transport that connects sessions directly, so the tests exercise real synchronization.</summary>
internal sealed class TestHub
{
    private readonly List<TestTransport> _clients = new();

    public TestTransport CreateClient()
    {
        var client = new TestTransport(this);
        _clients.Add(client);
        return client;
    }

    internal async Task BroadcastAsync(TestTransport sender, CollaborationMessage message)
    {
        foreach (TestTransport client in _clients.ToArray())
        {
            if (!ReferenceEquals(client, sender)) await client.ReceiveAsync(message).ConfigureAwait(false);
        }
    }
}

internal sealed class TestTransport(TestHub hub) : ICollaborationTransport
{
    private bool _connected = true;

    public bool IsConnected => _connected;

    public int SentCount { get; private set; }

    public event Func<CollaborationMessage, Task>? MessageReceived;

    public event Action<bool>? ConnectionChanged;

    public void SetConnected(bool connected)
    {
        if (_connected == connected) return;
        _connected = connected;
        ConnectionChanged?.Invoke(connected);
    }

    public async Task SendAsync(CollaborationMessage message, CancellationToken cancellationToken = default)
    {
        if (!_connected) throw new InvalidOperationException("Offline.");
        SentCount++;
        await hub.BroadcastAsync(this, message).ConfigureAwait(false);
    }

    internal Task ReceiveAsync(CollaborationMessage message)
        => _connected && MessageReceived is not null ? MessageReceived(message) : Task.CompletedTask;
}

public class OperationCodecTests
{
    [Fact]
    public void Round_trips_every_operation_kind()
    {
        var document = new InkDocument();
        Guid layer = document.Layers[0].Id;
        Stroke stroke = TestData.Stroke(new Vector2(0, 0), new Vector2(50, 50));

        DrawingOperation[] operations =
        [
            new AddElementOperation(layer, stroke, 0),
            new RemoveElementOperation(stroke.Id),
            new ReplaceElementOperation(stroke with { Description = "edited" }),
            new ReorderElementOperation(stroke.Id, layer, 2),
            new AddLayerOperation(Guid.NewGuid(), new LayerProperties { Name = "New", Opacity = 0.4f }, 1, [stroke]),
            new RemoveLayerOperation(layer),
            new UpdateLayerOperation(layer, new LayerProperties { Name = "Renamed", IsLocked = true }),
            new ReorderLayerOperation(layer, 0),
            new UpdateSettingsOperation(new DocumentSettings { Width = 123, Height = 456, Background = InkColor.Parse("#abcdef") }),
            new SetMetadataOperation("key", "value"),
            new SetMetadataOperation("key", null),
            new BatchOperation([new RemoveElementOperation(stroke.Id), new SetMetadataOperation("a", "b")], "Group"),
        ];

        foreach (DrawingOperation operation in operations)
        {
            string json = OperationCodec.Serialize(operation);
            DrawingOperation decoded = OperationCodec.Deserialize(json);
            Assert.Equal(operation.GetType(), decoded.GetType());
            Assert.Equal(json, OperationCodec.Serialize(decoded));
        }
    }

    [Fact]
    public void An_add_carries_the_stroke_points()
    {
        var document = new InkDocument();
        Stroke stroke = new(TestData.PressureRamp(new Vector2(5, 5), new Vector2(120, 90), 40), new PenBrush { Width = 7f });
        string json = OperationCodec.Serialize(new AddElementOperation(document.Layers[0].Id, stroke));

        var decoded = (AddElementOperation)OperationCodec.Deserialize(json);
        var received = (Stroke)decoded.Element;
        Assert.Equal(stroke.Id, received.Id);
        Assert.Equal(stroke.Points.Count, received.Points.Count);
        Assert.Equal(7f, received.Brush.Width, 3);
    }

    [Fact]
    public void A_stroke_operation_is_small()
    {
        var document = new InkDocument();
        Stroke stroke = new(TestData.PressureRamp(new Vector2(0, 0), new Vector2(200, 150), 120), new PenBrush());
        string json = OperationCodec.Serialize(new AddElementOperation(document.Layers[0].Id, stroke));

        // Roughly ten bytes per point on the wire, including base64 expansion and all the JSON around it.
        double perPoint = json.Length / 120.0;
        Assert.True(perPoint < 14, $"a stroke cost {perPoint:0.0} bytes per point on the wire ({json.Length} bytes total)");
    }

    [Theory]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("not json")]
    [InlineData("{\"op\":\"nonsense\"}")]
    [InlineData("{\"op\":\"add\"}")]
    [InlineData("{\"op\":\"remove\",\"element\":\"not-a-guid\"}")]
    public void Malformed_operations_are_rejected(string json)
    {
        Assert.Throws<InkFormatException>(() => OperationCodec.Deserialize(json));
    }
}

public class CollaborationSessionTests
{
    private static (InkDocument A, InkDocument B, CollaborationSession SessionA, CollaborationSession SessionB, TestTransport TransportA, TestTransport TransportB) CreatePair()
    {
        var hub = new TestHub();
        var a = new InkDocument(new DocumentSettings { Width = 400, Height = 300 });
        InkDocument b = InkSerializer.Deserialize(InkSerializer.Serialize(a));

        TestTransport transportA = hub.CreateClient();
        TestTransport transportB = hub.CreateClient();
        return (a, b, new CollaborationSession(a, transportA, "alex"), new CollaborationSession(b, transportB, "sam"), transportA, transportB);
    }

    [Fact]
    public async Task A_stroke_drawn_by_one_participant_appears_for_the_other()
    {
        (InkDocument a, InkDocument b, CollaborationSession sa, CollaborationSession sb, _, _) = CreatePair();
        await using (sa)
        await using (sb)
        {
            Stroke stroke = TestData.Stroke(new Vector2(10, 10), new Vector2(90, 60));
            a.AddElement(stroke);
            await Task.Yield();

            Assert.Equal(1, b.ElementCount);
            Assert.Equal(stroke.Id, b.Layers[0].Elements[0].Id);
        }
    }

    [Fact]
    public async Task Remote_changes_do_not_enter_the_local_undo_history()
    {
        (InkDocument a, InkDocument b, CollaborationSession sa, CollaborationSession sb, _, _) = CreatePair();
        await using (sa)
        await using (sb)
        {
            using var historyB = new History.HistoryManager(b);
            a.AddElement(TestData.Stroke(new Vector2(0, 0), new Vector2(50, 50)));
            await Task.Yield();

            Assert.Equal(1, b.ElementCount);
            Assert.False(historyB.CanUndo);
        }
    }

    [Fact]
    public async Task Undo_is_broadcast_as_an_ordinary_edit()
    {
        (InkDocument a, InkDocument b, CollaborationSession sa, CollaborationSession sb, _, _) = CreatePair();
        await using (sa)
        await using (sb)
        {
            using var historyA = new History.HistoryManager(a);
            a.AddElement(TestData.Stroke(new Vector2(0, 0), new Vector2(50, 50)));
            await Task.Yield();
            Assert.Equal(1, b.ElementCount);

            historyA.Undo();
            await Task.Yield();

            Assert.Equal(0, b.ElementCount);
        }
    }

    [Fact]
    public async Task Two_participants_drawing_at_once_keep_both_strokes()
    {
        (InkDocument a, InkDocument b, CollaborationSession sa, CollaborationSession sb, _, _) = CreatePair();
        await using (sa)
        await using (sb)
        {
            a.AddElement(TestData.Stroke(new Vector2(0, 0), new Vector2(40, 40)));
            b.AddElement(TestData.Stroke(new Vector2(100, 100), new Vector2(140, 140)));
            await Task.Yield();

            Assert.Equal(2, a.ElementCount);
            Assert.Equal(2, b.ElementCount);
        }
    }

    [Fact]
    public async Task Editing_the_same_element_resolves_the_same_way_on_both_sides()
    {
        (InkDocument a, InkDocument b, CollaborationSession sa, CollaborationSession sb, TestTransport ta, TestTransport tb) = CreatePair();
        await using (sa)
        await using (sb)
        {
            Stroke stroke = TestData.Stroke(new Vector2(0, 0), new Vector2(50, 50));
            a.AddElement(stroke);
            await Task.Yield();

            // Both edit the same element while disconnected, then reconnect.
            ta.SetConnected(false);
            tb.SetConnected(false);
            a.ReplaceElement(a.GetElement(stroke.Id) with { Description = "from alex" });
            b.ReplaceElement(b.GetElement(stroke.Id) with { Description = "from sam" });

            ta.SetConnected(true);
            tb.SetConnected(true);
            await sa.FlushAsync();
            await sb.FlushAsync();
            await Task.Yield();

            // Whatever the outcome, the replicas must agree.
            Assert.Equal(a.GetElement(stroke.Id).Description, b.GetElement(stroke.Id).Description);
        }
    }

    [Fact]
    public async Task Changes_made_offline_are_queued_and_delivered_on_reconnect()
    {
        (InkDocument a, InkDocument b, CollaborationSession sa, CollaborationSession sb, TestTransport ta, _) = CreatePair();
        await using (sa)
        await using (sb)
        {
            ta.SetConnected(false);
            for (int i = 0; i < 5; i++) a.AddElement(TestData.Stroke(new Vector2(i * 10, 0), new Vector2(i * 10 + 5, 20)));
            Assert.Equal(0, b.ElementCount);
            Assert.Equal(5, sa.PendingCount);

            ta.SetConnected(true);
            await sa.FlushAsync();
            await Task.Yield();

            Assert.Equal(5, b.ElementCount);
            Assert.True(sa.IsSynchronized);
        }
    }

    [Fact]
    public async Task A_snapshot_brings_a_joining_participant_up_to_date()
    {
        (InkDocument a, InkDocument b, CollaborationSession sa, CollaborationSession sb, TestTransport ta, _) = CreatePair();
        await using (sa)
        await using (sb)
        {
            ta.SetConnected(false);
            for (int i = 0; i < 4; i++) a.AddElement(TestData.Stroke(new Vector2(i * 20, 0), new Vector2(i * 20 + 10, 30)));
            ta.SetConnected(true);
            sa.ClearPending();

            await sb.RequestSnapshotAsync();
            await Task.Yield();

            Assert.Equal(4, b.ElementCount);
        }
    }

    [Fact]
    public async Task A_snapshot_that_cannot_be_applied_leaves_the_document_intact()
    {
        var hub = new TestHub();
        var local = new InkDocument(new DocumentSettings { Width = 200, Height = 200 }, DocumentLimits.Default with { MaxElements = 3 });
        for (int i = 0; i < 3; i++) local.AddElement(TestData.Stroke(new Vector2(i * 10, 0), new Vector2(i * 10 + 5, 20)));

        TestTransport transport = hub.CreateClient();
        await using var session = new CollaborationSession(local, transport, "local");

        // A peer sends a document with more elements than this participant allows.
        var remote = InkSerializer.Deserialize(InkSerializer.Serialize(local));
        var oversized = new InkDocument(remote.Settings) { Id = remote.Id };
        for (int i = 0; i < 20; i++) oversized.AddElement(TestData.Stroke(new Vector2(i, 0), new Vector2(i + 1, 1)));

        TestTransport peer = hub.CreateClient();
        await peer.SendAsync(new CollaborationMessage
        {
            Kind = CollaborationMessageKind.Snapshot,
            SenderId = "peer",
            DocumentId = local.Id,
            Sequence = 1,
            Snapshot = InkSerializer.Serialize(oversized),
        });
        await Task.Yield();

        // The local drawing survives: a refused snapshot must not destroy it.
        Assert.Equal(3, local.ElementCount);
    }

    [Fact]
    public async Task Presence_is_reported_and_never_touches_the_document()
    {
        (InkDocument a, InkDocument b, CollaborationSession sa, CollaborationSession sb, _, _) = CreatePair();
        await using (sa)
        await using (sb)
        {
            PresenceState? received = null;
            sb.PresenceChanged += p => received = p;

            await sa.UpdatePresenceAsync(new PresenceState
            {
                ParticipantId = "alex",
                Name = "Alex",
                Cursor = new Vector2(30, 40),
                LiveStroke = [new Vector2(0, 0), new Vector2(10, 10)],
                LiveStrokeWidth = 4f,
            });
            await Task.Yield();

            Assert.NotNull(received);
            Assert.Equal("Alex", received!.Name);
            Assert.Equal(0, b.ElementCount);
            Assert.Single(sb.Participants);
        }
    }

    [Fact]
    public async Task A_hostile_operation_is_rejected_without_disturbing_the_document()
    {
        var hub = new TestHub();
        var document = new InkDocument();
        document.AddElement(TestData.Stroke(new Vector2(0, 0), new Vector2(10, 10)));

        TestTransport transport = hub.CreateClient();
        await using var session = new CollaborationSession(document, transport, "local");
        string? rejection = null;
        session.RemoteOperationRejected += reason => rejection = reason;

        TestTransport peer = hub.CreateClient();
        await peer.SendAsync(new CollaborationMessage
        {
            Kind = CollaborationMessageKind.Operation,
            SenderId = "peer",
            DocumentId = document.Id,
            Sequence = 1,
            Operation = "{\"op\":\"add\",\"layer\":\"not-a-guid\"}",
        });
        await Task.Yield();

        Assert.Equal(1, document.ElementCount);
        Assert.NotNull(rejection);
    }

    [Fact]
    public async Task A_duplicate_message_is_ignored()
    {
        var hub = new TestHub();
        var document = new InkDocument();
        TestTransport transport = hub.CreateClient();
        await using var session = new CollaborationSession(document, transport, "local");

        TestTransport peer = hub.CreateClient();
        Stroke stroke = TestData.Stroke(new Vector2(0, 0), new Vector2(20, 20));
        var message = new CollaborationMessage
        {
            Kind = CollaborationMessageKind.Operation,
            SenderId = "peer",
            DocumentId = document.Id,
            Sequence = 1,
            Operation = OperationCodec.Serialize(new AddElementOperation(document.Layers[0].Id, stroke)),
        };

        await peer.SendAsync(message);
        await peer.SendAsync(message);
        await Task.Yield();

        Assert.Equal(1, document.ElementCount);
    }

    [Fact]
    public async Task A_participant_that_leaves_is_forgotten_and_can_rejoin_with_the_same_id()
    {
        var hub = new TestHub();
        var document = new InkDocument();
        TestTransport transport = hub.CreateClient();
        await using var session = new CollaborationSession(document, transport, "local");
        TestTransport peer = hub.CreateClient();

        await peer.SendAsync(new CollaborationMessage
        {
            Kind = CollaborationMessageKind.Presence,
            SenderId = "peer",
            DocumentId = document.Id,
            Sequence = 7,
            Presence = new PresenceState { ParticipantId = "peer", Name = "Peer" },
        });
        await Task.Yield();
        Assert.Single(session.Participants);

        await peer.SendAsync(new CollaborationMessage
        {
            Kind = CollaborationMessageKind.Leave,
            SenderId = "peer",
            DocumentId = document.Id,
            Sequence = 8,
        });
        await Task.Yield();
        Assert.Empty(session.Participants);

        // Rejoining restarts at sequence 1. If the de-duplication cursor had survived the leave, every message
        // from the returning participant would be discarded as a retransmission and they would appear frozen.
        Stroke stroke = TestData.Stroke(new Vector2(0, 0), new Vector2(20, 20));
        await peer.SendAsync(new CollaborationMessage
        {
            Kind = CollaborationMessageKind.Operation,
            SenderId = "peer",
            DocumentId = document.Id,
            Sequence = 1,
            Operation = OperationCodec.Serialize(new AddElementOperation(document.Layers[0].Id, stroke)),
        });
        await Task.Yield();

        Assert.Equal(1, document.ElementCount);
    }

    [Fact]
    public async Task A_change_made_while_the_queue_is_draining_does_not_overtake_it()
    {
        var hub = new TestHub();
        var document = new InkDocument();
        TestTransport transport = hub.CreateClient();
        var received = new List<CollaborationMessage>();

        TestTransport peer = hub.CreateClient();
        peer.MessageReceived += m => { received.Add(m); return Task.CompletedTask; };

        await using var session = new CollaborationSession(document, transport, "local");

        transport.SetConnected(false);
        document.AddElement(TestData.Stroke(new Vector2(0, 0), new Vector2(10, 10)));
        document.AddElement(TestData.Stroke(new Vector2(20, 0), new Vector2(30, 10)));
        Assert.Equal(2, session.PendingCount);

        transport.SetConnected(true);
        document.AddElement(TestData.Stroke(new Vector2(40, 0), new Vector2(50, 10)));
        await session.FlushAsync();
        await Task.Yield();

        Assert.Equal(0, session.PendingCount);
        long[] sequences = received
            .Where(m => m.Kind == CollaborationMessageKind.Operation)
            .Select(m => m.Sequence)
            .ToArray();
        Assert.Equal(3, sequences.Length);
        Assert.Equal(sequences.OrderBy(x => x).ToArray(), sequences);
    }
}
