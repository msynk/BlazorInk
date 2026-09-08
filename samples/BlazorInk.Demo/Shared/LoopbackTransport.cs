using BlazorInk.Collaboration;

namespace BlazorInk.Demo.Shared;

/// <summary>
/// An in-memory transport that connects several sessions in the same browser tab, so the collaboration demo runs
/// without a server. A real application swaps this for SignalR or WebSockets; nothing else changes.
/// </summary>
public sealed class LoopbackHub
{
    private readonly List<LoopbackTransport> _clients = new();

    /// <summary>Extra delay applied to every message, used to show what latency does to the experience.</summary>
    public int LatencyMilliseconds { get; set; }

    /// <summary>Creates a transport endpoint attached to this hub.</summary>
    public LoopbackTransport CreateClient()
    {
        var client = new LoopbackTransport(this);
        _clients.Add(client);
        return client;
    }

    internal async Task BroadcastAsync(LoopbackTransport sender, CollaborationMessage message)
    {
        if (LatencyMilliseconds > 0) await Task.Delay(LatencyMilliseconds).ConfigureAwait(false);
        foreach (LoopbackTransport client in _clients.ToArray())
        {
            if (ReferenceEquals(client, sender)) continue;
            await client.ReceiveAsync(message).ConfigureAwait(false);
        }
    }
}

/// <summary>One endpoint of a <see cref="LoopbackHub"/>.</summary>
public sealed class LoopbackTransport(LoopbackHub hub) : ICollaborationTransport
{
    private bool _connected = true;

    /// <inheritdoc />
    public bool IsConnected => _connected;

    /// <inheritdoc />
    public event Func<CollaborationMessage, Task>? MessageReceived;

    /// <inheritdoc />
    public event Action<bool>? ConnectionChanged;

    /// <summary>Number of messages this endpoint has sent, shown in the demo.</summary>
    public int SentCount { get; private set; }

    /// <summary>Number of bytes this endpoint has sent, so the cost of an edit is visible.</summary>
    public long SentBytes { get; private set; }

    /// <summary>Simulates going offline and coming back, to show the queue and reconnect behaviour.</summary>
    public void SetConnected(bool connected)
    {
        if (_connected == connected) return;
        _connected = connected;
        ConnectionChanged?.Invoke(connected);
    }

    /// <inheritdoc />
    public async Task SendAsync(CollaborationMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (!_connected) throw new InvalidOperationException("The transport is offline.");
        SentCount++;
        SentBytes += (message.Operation?.Length ?? 0) + (message.Snapshot?.Length ?? 0) + 64;
        await hub.BroadcastAsync(this, message).ConfigureAwait(false);
    }

    internal Task ReceiveAsync(CollaborationMessage message)
        => _connected && MessageReceived is not null ? MessageReceived(message) : Task.CompletedTask;
}
