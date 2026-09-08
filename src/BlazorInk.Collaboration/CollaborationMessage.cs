using System.Numerics;
using BlazorInk.Geometry;

namespace BlazorInk.Collaboration;

/// <summary>The kinds of message a session exchanges.</summary>
public enum CollaborationMessageKind : byte
{
    /// <summary>A document change, carrying one operation.</summary>
    Operation = 0,
    /// <summary>A participant's cursor, selection or in-progress stroke.</summary>
    Presence = 1,
    /// <summary>A full document, sent when a participant joins or resynchronizes.</summary>
    Snapshot = 2,
    /// <summary>A request for a snapshot.</summary>
    SnapshotRequest = 3,
    /// <summary>A participant left.</summary>
    Leave = 4,
}

/// <summary>
/// One message exchanged between participants. Messages are self-contained, and <see cref="Sequence"/> makes
/// them idempotent: a retried message is applied once. A message that arrives after a later one from the same
/// sender is discarded, so a transport must deliver each sender's messages in order.
/// </summary>
public sealed record CollaborationMessage
{
    /// <summary>What the message carries.</summary>
    public required CollaborationMessageKind Kind { get; init; }

    /// <summary>Who sent it.</summary>
    public required string SenderId { get; init; }

    /// <summary>
    /// The sender's own message counter. A message whose sequence is not greater than the last one seen from
    /// that sender is discarded as a duplicate or a late arrival.
    /// </summary>
    public long Sequence { get; init; }

    /// <summary>The document the message applies to.</summary>
    public Guid DocumentId { get; init; }

    /// <summary>The serialized operation, for <see cref="CollaborationMessageKind.Operation"/>.</summary>
    public string? Operation { get; init; }

    /// <summary>The serialized document, for <see cref="CollaborationMessageKind.Snapshot"/>.</summary>
    public string? Snapshot { get; init; }

    /// <summary>The sender's presence, for <see cref="CollaborationMessageKind.Presence"/>.</summary>
    public PresenceState? Presence { get; init; }
}

/// <summary>
/// What another participant is doing right now: where their pointer is, what they have selected, and the stroke
/// they are drawing. Presence is transient and never recorded in history or persisted with the document.
/// </summary>
public sealed record PresenceState
{
    /// <summary>Stable identifier of the participant.</summary>
    public required string ParticipantId { get; init; }

    /// <summary>Display name.</summary>
    public string? Name { get; init; }

    /// <summary>Colour used for their cursor and selection outline, as a CSS colour.</summary>
    public string? Color { get; init; }

    /// <summary>Pointer position in document coordinates, or null when the pointer is away.</summary>
    public Vector2? Cursor { get; init; }

    /// <summary>Ids of the elements they have selected.</summary>
    public IReadOnlyList<Guid>? Selection { get; init; }

    /// <summary>
    /// The points of the stroke they are currently drawing, in document coordinates, so other participants can
    /// see ink appear as it is drawn rather than only when the pen lifts.
    /// </summary>
    public IReadOnlyList<Vector2>? LiveStroke { get; init; }

    /// <summary>Colour of the live stroke, as a CSS colour.</summary>
    public string? LiveStrokeColor { get; init; }

    /// <summary>Width of the live stroke in document units.</summary>
    public float LiveStrokeWidth { get; init; }

    /// <summary>When the state was produced, used to expire stale participants.</summary>
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Bounding box of the live stroke, used to limit redraws.</summary>
    public Rect LiveStrokeBounds
    {
        get
        {
            if (LiveStroke is not { Count: > 0 }) return Rect.Empty;
            Rect bounds = Rect.Empty;
            foreach (Vector2 p in LiveStroke) bounds = bounds.Union(p);
            return bounds.Inflate(LiveStrokeWidth);
        }
    }
}

/// <summary>
/// Moves messages between participants. Implement this over SignalR, WebSockets, WebRTC or anything else; the
/// session makes no assumptions beyond eventual delivery.
/// </summary>
/// <remarks>
/// The transport is responsible for delivery and for excluding the sender from its own broadcasts. It must
/// deliver each sender's messages in order; a message that arrives after a later one from the same sender is
/// discarded. Duplicates are harmless. Ordering between different senders does not matter.
/// </remarks>
public interface ICollaborationTransport
{
    /// <summary>True when the transport is connected and messages will be delivered.</summary>
    bool IsConnected { get; }

    /// <summary>Sends a message to the other participants.</summary>
    Task SendAsync(CollaborationMessage message, CancellationToken cancellationToken = default);

    /// <summary>Raised for each message received from another participant.</summary>
    event Func<CollaborationMessage, Task>? MessageReceived;

    /// <summary>Raised when the connection state changes. The session flushes its queue when it becomes true.</summary>
    event Action<bool>? ConnectionChanged;
}
