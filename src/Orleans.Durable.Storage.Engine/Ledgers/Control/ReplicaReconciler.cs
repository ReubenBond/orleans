namespace Orleans.Durable.Storage.Engine.Ledgers.Control;

/// <summary>
/// One node's reconciliation of its replicas with its map view (ledgers.md §8.5): it drops each replica the view
/// revokes, through the node's own <see cref="ILedgerStore"/>, so it reconciles over every provider.
/// </summary>
/// <remarks>
/// <para>
/// The view revokes a replica when the ledger's ID is below the view's <c>NextLedgerId</c> and the view does not list
/// the ledger with the node (LG-I7). Those are replicas of ledgers which left the map, and stray replicas a delayed write,
/// recovery write, or seal created on a node the ledger never listed. IDs are assigned in installation order and never
/// reused (LG-I3), and a ledger's replicas are fixed for its life, so no later map lists a revoked replica with the node
/// again, and a view which predates a ledger's installation revokes nothing: a stale view drops less, never wrongly.
/// </para>
/// <para>
/// The composition calls <see cref="ReconcileAsync"/> whenever the node's view changes, which looks only at what the
/// view's change revokes: the ledgers which lost the node, and the ledgers installed since the view it last reconciled
/// with. It calls <see cref="SweepAsync"/> periodically, which judges every replica the node holds, so a stray replica a
/// write created after its ledger's installation reached the view is dropped too. Both decide from the view alone,
/// reading nothing; a failed map read only delays them.
/// </para>
/// </remarks>
public sealed class ReplicaReconciler
{
    /// <summary>The most drops a reconciliation has in flight at once.</summary>
    internal const int MaxConcurrentDrops = 8;

    private readonly ILedgerStore _store;
    private readonly LedgerMapView _map;
    private readonly Lock _lock = new();
    private LedgerMapValue? _reconciled;

    /// <summary>
    /// Initializes a new instance of the <see cref="ReplicaReconciler"/> class over the node's own store and its map view.
    /// </summary>
    /// <param name="store">The node's own replicas.</param>
    /// <param name="map">The node's map view.</param>
    public ReplicaReconciler(ILedgerStore store, LedgerMapView map)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(map);
        _store = store;
        _map = map;
    }

    /// <summary>
    /// Drops each replica the node holds which the view's <see cref="LedgerMapView.Current"/> revokes and the view it last
    /// reconciled with did not: the first reconciliation judges every replica, as <see cref="SweepAsync"/> does.
    /// </summary>
    /// <param name="cancellationToken">Stops the reconciliation; a drop it began may still complete.</param>
    /// <returns>The ledgers whose replicas it dropped, ascending.</returns>
    public async Task<IReadOnlyList<LedgerId>> ReconcileAsync(CancellationToken cancellationToken = default)
    {
        var view = _map.Current;
        LedgerMapValue? previous;
        lock (_lock)
        {
            previous = _reconciled;
        }

        if (previous is null || previous.Epoch >= view.Epoch)
        {
            return previous is null ? await SweepAsync(cancellationToken).ConfigureAwait(false) : [];
        }

        var held = await _store.ListReplicasAsync(cancellationToken).ConfigureAwait(false);
        var revoked = new List<LedgerId>();
        var node = _store.NodeId;

        // The ledgers which lost the node: a ledger's replicas are fixed for its life, so it left the map.
        foreach (var listed in previous.Ledgers)
        {
            if (listed.IsOnNode(node) && view.Get(listed.Id) is null)
            {
                revoked.Add(listed.Id);
            }
        }

        // The ledgers installed since: a replica of one the view does not list with the node is a stray.
        for (var index = LowerBound(held, previous.NextLedgerId); index < held.Count && held[index].Value < view.NextLedgerId.Value; index++)
        {
            if (Revokes(view, held[index], node))
            {
                revoked.Add(held[index]);
            }
        }

        revoked.Sort();
        await DropAsync(revoked, cancellationToken).ConfigureAwait(false);
        Advance(view);
        return revoked;
    }

    /// <summary>
    /// Drops each replica the node holds which the view's <see cref="LedgerMapView.Current"/> revokes.
    /// </summary>
    /// <param name="cancellationToken">Stops the sweep; a drop it began may still complete.</param>
    /// <returns>The ledgers whose replicas it dropped, ascending.</returns>
    public async Task<IReadOnlyList<LedgerId>> SweepAsync(CancellationToken cancellationToken = default)
    {
        var view = _map.Current;
        var held = await _store.ListReplicasAsync(cancellationToken).ConfigureAwait(false);
        var revoked = new List<LedgerId>();
        foreach (var ledger in held)
        {
            if (Revokes(view, ledger, _store.NodeId))
            {
                revoked.Add(ledger);
            }
        }

        await DropAsync(revoked, cancellationToken).ConfigureAwait(false);
        Advance(view);
        return revoked;
    }

    /// <summary>
    /// Returns whether <paramref name="map"/> revokes <paramref name="node"/>'s replica of <paramref name="ledger"/>: the
    /// ledger's ID is below the map's <c>NextLedgerId</c>, and the map does not list the ledger with the node (LG-I7).
    /// </summary>
    private static bool Revokes(LedgerMapValue map, LedgerId ledger, NodeId node) =>
        ledger.Value < map.NextLedgerId.Value && !(map.Get(ledger) is { } listed && listed.IsOnNode(node));

    /// <summary>Returns the position of the first ledger in <paramref name="held"/>, ascending, at or above <paramref name="id"/>.</summary>
    private static int LowerBound(IReadOnlyList<LedgerId> held, LedgerId id)
    {
        int lower = 0, upper = held.Count;
        while (lower < upper)
        {
            var middle = lower + (upper - lower) / 2;
            (lower, upper) = held[middle].Value < id.Value ? (middle + 1, upper) : (lower, middle);
        }

        return lower;
    }

    /// <summary>Drops <paramref name="revoked"/>, at most <see cref="MaxConcurrentDrops"/> at once.</summary>
    private async Task DropAsync(List<LedgerId> revoked, CancellationToken cancellationToken)
    {
        // LG-I7: a node drops a ledger only once its map view revokes it; a wrong drop would be permanent.
        for (var start = 0; start < revoked.Count; start += MaxConcurrentDrops)
        {
            var count = Math.Min(MaxConcurrentDrops, revoked.Count - start);
            var drops = new Task[count];
            for (var index = 0; index < count; index++)
            {
                drops[index] = _store.DropReplicaAsync(revoked[start + index], cancellationToken).AsTask();
            }

            await Task.WhenAll(drops).ConfigureAwait(false);
        }
    }

    /// <summary>Records <paramref name="view"/> as reconciled, unless a reconciliation with a later view already was.</summary>
    private void Advance(LedgerMapValue view)
    {
        lock (_lock)
        {
            if (_reconciled is null || _reconciled.Epoch < view.Epoch)
            {
                _reconciled = view;
            }
        }
    }
}
