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
    // Values of the wrong JSON kind used to escape as InvalidOperationException, which the session does not catch.
    [InlineData("{\"op\":5}")]
    [InlineData("{\"op\":\"metadata\",\"key\":5}")]
    [InlineData("{\"op\":\"batch\",\"label\":5,\"operations\":[]}")]
    [InlineData("{\"op\":\"settings\",\"background\":5}")]
    [InlineData("{\"op\":\"updateLayer\",\"layer\":\"00000000-0000-0000-0000-000000000001\",\"properties\":{\"name\":5}}")]
    [InlineData("{\"op\":\"updateLayer\",\"layer\":\"00000000-0000-0000-0000-000000000001\",\"properties\":{\"opacity\":\"x\"}}")]
    public void Malformed_operations_are_rejected(string json)
    {
        Assert.Throws<InkFormatException>(() => OperationCodec.Deserialize(json));
    }

    [Fact]
    public void Layer_metadata_travels_with_layer_operations()
    {
        // An update replaces the whole property set, so metadata missing from the wire was erased on every other
        // replica by something as small as renaming the layer.
        var properties = new LayerProperties { Name = "Notes", Metadata = new Dictionary<string, string> { ["role"] = "review" } };
        var decoded = (UpdateLayerOperation)OperationCodec.Deserialize(OperationCodec.Serialize(new UpdateLayerOperation(Guid.NewGuid(), properties)));
        Assert.Equal("review", decoded.Properties.Metadata?["role"]);

        var added = (AddLayerOperation)OperationCodec.Deserialize(OperationCodec.Serialize(new AddLayerOperation(Guid.NewGuid(), properties)));
        Assert.Equal("review", added.Properties.Metadata?["role"]);
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

/// <summary>A transport whose messages the test delivers by hand, so concurrent edits can be interleaved exactly.</summary>
internal sealed class ManualTransport : ICollaborationTransport
{
    public bool IsConnected { get; set; } = true;

    /// <summary>Makes the next operation send fail, as a dropped connection would.</summary>
    public bool FailNextOperation { get; set; }

    public List<CollaborationMessage> Sent { get; } = new();

    public event Func<CollaborationMessage, Task>? MessageReceived;

    public event Action<bool>? ConnectionChanged;

    public Task SendAsync(CollaborationMessage message, CancellationToken cancellationToken = default)
    {
        if (FailNextOperation && message.Kind == CollaborationMessageKind.Operation)
        {
            FailNextOperation = false;
            throw new InvalidOperationException("Connection dropped.");
        }
        Sent.Add(message);
        return Task.CompletedTask;
    }

    public Task DeliverAsync(CollaborationMessage message) => MessageReceived?.Invoke(message) ?? Task.CompletedTask;

    public void RaiseConnectionChanged() => ConnectionChanged?.Invoke(IsConnected);
}

public class CollaborationRegressionTests
{
    /// <summary>
    /// Three participants: alex and sam already share two strokes, sam has one more that alex has not seen, and zoe
    /// joins empty.
    /// </summary>
    private static (InkDocument Alex, InkDocument Sam, InkDocument Zoe, TestHub Hub) CreateJoiningTrio()
    {
        var alex = new InkDocument(new DocumentSettings { Width = 400, Height = 300 });
        InkDocument zoe = InkSerializer.Deserialize(InkSerializer.Serialize(alex));
        alex.AddElement(TestData.Stroke(new Vector2(0, 0), new Vector2(20, 20)));
        alex.AddElement(TestData.Stroke(new Vector2(40, 0), new Vector2(60, 20)));
        InkDocument sam = InkSerializer.Deserialize(InkSerializer.Serialize(alex));
        sam.AddElement(TestData.Stroke(new Vector2(80, 0), new Vector2(100, 20)));
        return (alex, sam, zoe, new TestHub());
    }

    [Fact]
    public async Task A_snapshot_answer_is_applied_only_by_the_participant_that_asked_and_only_once()
    {
        (InkDocument alex, InkDocument sam, InkDocument zoe, TestHub hub) = CreateJoiningTrio();
        await using var sa = new CollaborationSession(alex, hub.CreateClient(), "alex");
        await using var ss = new CollaborationSession(sam, hub.CreateClient(), "sam");
        await using var sz = new CollaborationSession(zoe, hub.CreateClient(), "zoe");

        // Both alex and sam answer. Every participant used to apply every answer: sam took alex's copy and lost
        // the stroke only sam had, and zoe applied both answers, the second overwriting the first.
        await sz.RequestSnapshotAsync();
        await Task.Yield();

        Assert.Equal(2, alex.ElementCount);
        Assert.Equal(3, sam.ElementCount);
        // Alex answered first; that answer is the one zoe kept.
        Assert.Equal(alex.AllElements.Select(e => e.Id), zoe.AllElements.Select(e => e.Id));
    }

    [Fact]
    public async Task A_snapshot_sent_to_everyone_still_resets_every_participant()
    {
        (InkDocument alex, InkDocument sam, InkDocument zoe, TestHub hub) = CreateJoiningTrio();
        await using var sa = new CollaborationSession(alex, hub.CreateClient(), "alex");
        await using var ss = new CollaborationSession(sam, hub.CreateClient(), "sam");
        await using var sz = new CollaborationSession(zoe, hub.CreateClient(), "zoe");

        await sa.SendSnapshotAsync();
        await Task.Yield();

        Assert.Equal(alex.AllElements.Select(e => e.Id), sam.AllElements.Select(e => e.Id));
        Assert.Equal(alex.AllElements.Select(e => e.Id), zoe.AllElements.Select(e => e.Id));
    }

    [Fact]
    public async Task An_answer_nobody_asked_for_is_ignored()
    {
        var local = new InkDocument(new DocumentSettings { Width = 200, Height = 200 });
        local.AddElement(TestData.Stroke(new Vector2(0, 0), new Vector2(20, 20)));
        var other = new InkDocument(local.Settings) { Id = local.Id };
        var transport = new ManualTransport();
        await using var session = new CollaborationSession(local, transport, "local");

        await transport.DeliverAsync(new CollaborationMessage
        {
            Kind = CollaborationMessageKind.Snapshot,
            SenderId = "peer",
            RecipientId = "local",
            DocumentId = local.Id,
            Sequence = 1,
            Snapshot = InkSerializer.Serialize(other),
        });

        Assert.Equal(1, local.ElementCount);
    }

    [Fact]
    public async Task Three_participants_agree_on_the_winner_of_a_tied_edit()
    {
        // The tie used to be settled between the receiving replica's own id and the sender's. A bystander holding
        // alex's version then judged sam's version against "zoe", kept alex's and disagreed with alex and sam,
        // who had both settled on sam's.
        var a = new InkDocument();
        Stroke stroke = TestData.Stroke(new Vector2(0, 0), new Vector2(50, 50));
        a.AddElement(stroke);
        InkDocument b = InkSerializer.Deserialize(InkSerializer.Serialize(a));
        InkDocument c = InkSerializer.Deserialize(InkSerializer.Serialize(a));

        ManualTransport ta = new(), tb = new(), tc = new();
        await using var sa = new CollaborationSession(a, ta, "alex");
        await using var sb = new CollaborationSession(b, tb, "sam");
        await using var sc = new CollaborationSession(c, tc, "zoe");

        a.ReplaceElement(a.GetElement(stroke.Id) with { Description = "from alex" });
        b.ReplaceElement(b.GetElement(stroke.Id) with { Description = "from sam" });
        CollaborationMessage fromAlex = Assert.Single(ta.Sent);
        CollaborationMessage fromSam = Assert.Single(tb.Sent);

        await tb.DeliverAsync(fromAlex);
        await tc.DeliverAsync(fromAlex);
        await ta.DeliverAsync(fromSam);
        await tc.DeliverAsync(fromSam);

        string? winner = a.GetElement(stroke.Id).Description;
        Assert.Equal(winner, b.GetElement(stroke.Id).Description);
        Assert.Equal(winner, c.GetElement(stroke.Id).Description);

        // The replica that took the winning edit stores it at the version it was sent with. Bumping it left that
        // replica a version ahead of the others, so the next edit from anyone was ordered differently there.
        Assert.Equal(a.GetElement(stroke.Id).Version, b.GetElement(stroke.Id).Version);
        Assert.Equal(a.GetElement(stroke.Id).Version, c.GetElement(stroke.Id).Version);
    }

    [Fact]
    public async Task Presence_does_not_overtake_queued_operations()
    {
        // Presence and operations share the sender's sequence. A presence update sent past a queued operation
        // made the receiver discard that operation as a late arrival once the queue drained.
        var local = new InkDocument();
        InkDocument remote = InkSerializer.Deserialize(InkSerializer.Serialize(local));
        var transport = new ManualTransport { FailNextOperation = true };
        var peer = new ManualTransport();
        await using var session = new CollaborationSession(local, transport, "local");
        await using var receiver = new CollaborationSession(remote, peer, "remote");

        local.AddElement(TestData.Stroke(new Vector2(0, 0), new Vector2(20, 20)));
        Assert.Equal(1, session.PendingCount);

        await session.UpdatePresenceAsync(new PresenceState { ParticipantId = "local", Cursor = new Vector2(5, 5) });
        await session.FlushAsync();
        Assert.Equal(0, session.PendingCount);

        foreach (CollaborationMessage message in transport.Sent.ToArray()) await peer.DeliverAsync(message);

        Assert.Equal(1, remote.ElementCount);
    }

    [Fact]
    public async Task A_snapshot_brings_settings_metadata_and_layer_order_along()
    {
        // Only layers and elements were synchronized: a joining participant kept its own page and metadata, and
        // layers it already had stayed in its own order.
        var a = new InkDocument(new DocumentSettings { Width = 400, Height = 300 });
        Layer top = a.AddLayer("Top");
        InkDocument b = InkSerializer.Deserialize(InkSerializer.Serialize(a));
        a.ReorderLayer(top.Id, 0);
        a.UpdateSettings(new DocumentSettings { Width = 1000, Height = 700, Background = InkColor.Parse("#123456") });
        a.SetMetadata("case", "42");
        b.SetMetadata("stale", "x");

        var hub = new TestHub();
        await using var sa = new CollaborationSession(a, hub.CreateClient(), "alex");
        await using var sb = new CollaborationSession(b, hub.CreateClient(), "sam");
        await sb.RequestSnapshotAsync();
        await Task.Yield();

        Assert.Equal(a.Layers.Select(l => l.Id), b.Layers.Select(l => l.Id));
        Assert.Equal(a.Settings, b.Settings);
        Assert.Equal("42", b.Metadata["case"]);
        Assert.False(b.Metadata.ContainsKey("stale"));
    }
}
