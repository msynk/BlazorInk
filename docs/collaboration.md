# Collaboration

Collaboration is an optional package. Nothing in the core engine pays for it.

```bash
dotnet add package BlazorInk.Collaboration
```

## The design

Ink is append-mostly, and elements are identified by a `Guid`. That means the operations that matter commute
naturally: two people drawing at once produce two additions that can be applied in any order with the same result.
There is no conflict to resolve, so there is no CRDT.

The one genuine conflict is two people editing the *same* element, for example dragging the same shape. That is
settled last-writer-wins on the element's version, with the participant id breaking ties so every replica reaches
the same answer without coordination.

An application that needs stronger guarantees can layer a CRDT on top of these operations. Nobody pays for that
complexity by default.

## Wiring it up

```csharp
var session = new CollaborationSession(document, transport, participantId: "alex");

session.PresenceChanged += presence => { /* redraw their cursor */ };
session.ParticipantLeft += id => { /* remove them */ };
session.RemoteOperationRejected += reason => { /* log it */ };

await session.RequestSnapshotAsync();   // when joining an existing session
```

Everything the local user does is transmitted automatically: the session subscribes to the document and forwards
local changes, including undo and redo, which to everyone else are ordinary edits.

## The transport

```csharp
public interface ICollaborationTransport
{
    bool IsConnected { get; }
    Task SendAsync(CollaborationMessage message, CancellationToken cancellationToken = default);
    event Func<CollaborationMessage, Task>? MessageReceived;
    event Action<bool>? ConnectionChanged;
}
```

Implement it over SignalR, WebSockets, WebRTC or anything else. The transport must deliver messages and exclude
the sender from its own broadcasts. It does not need to guarantee ordering: the session tolerates reordering and
duplicates by sequence number.

A SignalR implementation is about thirty lines: forward `SendAsync` to a hub method, raise `MessageReceived` from
a hub callback, and raise `ConnectionChanged` from the connection's reconnect events.

## What travels on the wire

Drawing a stroke sends that stroke once, as one operation. Not the document, not the other strokes. A typical
handwriting stroke is a few hundred bytes because the points use the same compact binary encoding as the file
format.

```csharp
string json = OperationCodec.Serialize(operation);
DrawingOperation operation = OperationCodec.Deserialize(json);
```

Incoming operations are validated exactly like a loaded file, against `CollaborationOptions.Limits`. A participant
cannot make another client allocate unboundedly by sending a hostile operation.

## Live ink and cursors

A finished stroke is an operation. A stroke *in progress* is presence: transient, throttled, and never recorded in
history or saved with the document.

```csharp
await session.UpdatePresenceAsync(new PresenceState
{
    ParticipantId = "alex",
    Name = "Alex",
    Color = "#2563eb",
    Cursor = documentPoint,
    LiveStroke = pointsSoFar,
    LiveStrokeColor = "#2563eb",
    LiveStrokeWidth = 4f,
});
```

Presence is throttled to `PresenceInterval` (50 ms by default), and stale participants expire after
`PresenceTimeout`. Call `FlushPresenceAsync` on a timer so the last update is not held back by the throttle.

## Offline and reconnect

While the transport is disconnected, operations are queued, up to `MaxOfflineQueue`. Presence is not queued,
because stale presence is worse than none. When the transport reports a connection again, the queue is flushed in
order.

```csharp
if (!session.IsSynchronized)
{
    // session.PendingCount changes are waiting to be sent
}
```

## Joining

A new participant asks for a snapshot; any existing participant answers with the serialized document, and the
receiver applies it as a single batched change with origin `Remote`, so it does not land in their undo history.

Both sides must be editing the same document, including its layer ids. In practice a client joins by loading the
snapshot, which gives it exactly that.

## Limits worth knowing

- Two people editing the same element at the same moment: one edit wins, the other is dropped. Every replica
  agrees on which, but the losing edit is gone.
- Element ordering within a layer is index-based. Concurrent reordering by two participants converges to a
  consistent state but not necessarily the one either intended.
- There is no server-side authority in the box. If you need one, the same operations are what it would arbitrate.
