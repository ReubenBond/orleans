using Orleans.Durable.Storage.Engine.Archive;
using Orleans.Durable.Storage.Engine.Ledgers;

namespace Orleans.Durable.Storage;

/// <summary>
/// What the silo's journal router reads to route each journal's entries and reads (orleans.md §5.5): the silo's node, its
/// map view, and the cluster's archive store, whose pure <c>AdmitsKey</c> the router's early refusals ask. The silo's
/// Ledgers runtime publishes them once it starts; a call before then waits for them within its deadline.
/// </summary>
internal sealed class LocalRouting
{
    private readonly TaskCompletionSource<Routing> _published = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Gets what the runtime published, or <see langword="null"/> before it started.</summary>
    public Routing? Current => _published.Task.IsCompletedSuccessfully ? _published.Task.Result : null;

    /// <summary>Publishes the node's routing, once: the first runtime's, which lives as long as the silo.</summary>
    public void Publish(NodeId node, LedgerMapView view, IArchiveStore archive)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(archive);
        _ = _published.TrySetResult(new Routing(node, view, archive));
    }

    /// <summary>Waits until the runtime published the node's routing.</summary>
    public Task<Routing> ReadyAsync(CancellationToken cancellationToken) => _published.Task.WaitAsync(cancellationToken);

    /// <summary>The node, its map view, and the cluster's archive store.</summary>
    /// <param name="Node">The silo's node.</param>
    /// <param name="View">The node's view of <c>ledger-map</c>.</param>
    /// <param name="Archive">The cluster's archive store.</param>
    public sealed record Routing(NodeId Node, LedgerMapView View, IArchiveStore Archive);
}
