using Orleans.Durable.Storage.Engine.Archive;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.Ledgers.Objects;
using Orleans.Durable.Storage.Engine.Ledgers.Store;

namespace Orleans.Durable.Storage;

/// <summary>
/// The silo's one node as its data plane reaches it (orleans.md §5.2, §5.14, OR-I2, OR-I9, OR-I14): a state machine,
/// opening, open, registered, stopping, and closed, and the one serving gate, <see cref="ServingAsync"/>, through which
/// the replica host, the transport's in-process path, and the node's other grains admit a call. A diskless silo holds no
/// <see cref="LedgerStore"/> and serves through <see cref="LocalNodeServing.Diskless"/>.
/// </summary>
/// <remarks>
/// The node's lifecycle participant drives the states: it opens the store (or mints a fresh node identity when diskless),
/// decides the registration, marks the node stopping when the silo's active stage stops, and closed once the store is
/// disposed. The states only advance.
/// </remarks>
internal sealed class LocalNode
{
    private readonly record struct OpenedNode(NodeId Node, LedgerStore? Store);

    private readonly TaskCompletionSource<OpenedNode> _opened = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<NodeRegistration> _registered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _state;

    /// <summary>The node's states, in the order they advance.</summary>
    public enum NodeState
    {
        /// <summary>The store is not open yet.</summary>
        Opening,

        /// <summary>The store is open (or diskless identity minted); the node is not registered yet.</summary>
        Open,

        /// <summary>The node is registered and serves (OR-I2).</summary>
        Registered,

        /// <summary>The silo stops: the node admits no new call.</summary>
        Stopping,

        /// <summary>The store is closed.</summary>
        Closed,
    }

    /// <summary>Gets the node's state.</summary>
    public NodeState State => (NodeState)Volatile.Read(ref _state);

    /// <summary>Gets a value indicating whether the silo opened as diskless (holding no LedgerStore).</summary>
    public bool IsDiskless => _opened.Task.IsCompletedSuccessfully && _opened.Task.Result.Store is null;

    /// <summary>Gets the node's LedgerStore, the one store a diskful silo serves (OR-I9).</summary>
    /// <exception cref="InvalidOperationException">
    /// The store is not open, the silo is diskless, or the node is closed.
    /// </exception>
    public LedgerStore Store => StoreIfOpen ?? throw new InvalidOperationException(
        State == NodeState.Closed
            ? "The node's LedgerStore is closed."
            : IsDiskless
                ? "The node is diskless and holds no LedgerStore."
                : "The node's LedgerStore is not open.");

    /// <summary>Gets the node's identity, which stays the node's once it is closed.</summary>
    /// <exception cref="InvalidOperationException">The node has not been opened.</exception>
    public NodeId Node => _opened.Task.IsCompletedSuccessfully ? _opened.Task.Result.Node : throw new InvalidOperationException("The node is not open.");

    /// <summary>
    /// Gets a task which completes with the node's registration, its registry entry and the cluster's store identities it
    /// registered with, once its registration answered <c>Registered</c>, and fails or is cancelled when the registration
    /// ends otherwise.
    /// </summary>
    public Task<NodeRegistration> Registered => _registered.Task;

    /// <summary>Gets the store once opened and until the node is closed, or <see langword="null"/> when diskless or closed.</summary>
    internal LedgerStore? StoreIfOpen => State < NodeState.Closed && _opened.Task.IsCompletedSuccessfully ? _opened.Task.Result.Store : null;

    /// <summary>
    /// Admits a call keyed by <paramref name="key"/>, in order: once the node is open, a key naming another node answers
    /// <see cref="NotThisNode"/> without waiting for the registration (OR-I14); then the call waits for the registration
    /// (OR-I2), and throws its failure; and a node which stops answers <see cref="LocalNodeServing.Stopping"/>, without
    /// effect. Otherwise it answers the store (diskful) or <see cref="LocalNodeServing.Diskless"/> (diskless).
    /// </summary>
    /// <param name="key">The node the call names.</param>
    /// <param name="cancellationToken">Ends the caller's wait.</param>
    public ValueTask<LocalNodeServing> ServingAsync(NodeId key, CancellationToken cancellationToken)
    {
        // Fast path: once open and registered, avoid an async state machine on every call.
        if (_opened.Task.IsCompletedSuccessfully)
        {
            var opened = _opened.Task.Result;
            if (opened.Node != key)
            {
                return new(NotThisNode.Instance);
            }

            if (_registered.Task.IsCompletedSuccessfully)
            {
                if (State >= NodeState.Stopping)
                {
                    return new(LocalNodeServing.Stopping.Instance);
                }

                return new(opened.Store is { } store ? store : LocalNodeServing.Diskless.Instance);
            }
        }

        return ServingSlowAsync(key, cancellationToken);
    }

    private async ValueTask<LocalNodeServing> ServingSlowAsync(NodeId key, CancellationToken cancellationToken)
    {
        // A closed node's store is answered nowhere: its key is still checked, and it answers Stopping.
        var opened = await _opened.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (opened.Node != key)
        {
            return NotThisNode.Instance;
        }

        if (!_registered.Task.IsCompletedSuccessfully)
        {
            _ = await _registered.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        if (State >= NodeState.Stopping)
        {
            return LocalNodeServing.Stopping.Instance;
        }

        return opened.Store is { } store ? store : LocalNodeServing.Diskless.Instance;
    }

    /// <summary>Records the node root's open LedgerStore.</summary>
    internal void Opened(LedgerStore store)
    {
        Advance(NodeState.Open);
        _ = _opened.TrySetResult(new OpenedNode(store.NodeId, store));
    }

    /// <summary>Records a diskless silo's fresh node identity, with no LedgerStore (orleans.md §5.14).</summary>
    internal void OpenedDiskless(NodeId node)
    {
        Advance(NodeState.Open);
        _ = _opened.TrySetResult(new OpenedNode(node, null));
    }

    /// <summary>
    /// Records the node's registration: its entry, the archive store the node registry records for the cluster, and the
    /// object-ledger store the node registry records when configured (OR-I20, OL-I1).
    /// </summary>
    internal void Register(NodeRegistryEntry entry, ArchiveStoreIdentity archiveStore, ObjectLedgerStoreId? objectLedgerStore = null)
    {
        Advance(NodeState.Registered);
        _ = _registered.TrySetResult(new NodeRegistration(entry, archiveStore, objectLedgerStore));
    }

    /// <summary>Records that the registration failed.</summary>
    internal void RegistrationFailed(Exception failure) => _registered.TrySetException(failure);

    /// <summary>Marks the node stopping: it admits no new call, and a registration not yet answered is cancelled.</summary>
    internal void Stop()
    {
        Advance(NodeState.Stopping);
        _ = _registered.TrySetCanceled(CancellationToken.None);
    }

    /// <summary>
    /// Marks the node closed before its store is disposed, or once its start failed before it opened one, so a call which
    /// passed the serving gate and which the disposed store refuses answers unavailable (OR-I2): that answer relies on the
    /// node being closed whenever its store refuses for its disposal.
    /// </summary>
    internal void Close()
    {
        Advance(NodeState.Closed);
        _ = _opened.TrySetCanceled(CancellationToken.None);
        _ = _registered.TrySetCanceled(CancellationToken.None);
    }

    private void Advance(NodeState state)
    {
        int current;
        do
        {
            current = Volatile.Read(ref _state);
            if (current >= (int)state)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref _state, (int)state, current) != current);
    }
}

/// <summary>
/// The answer of <see cref="LocalNode.ServingAsync"/> (conventions.md §15): the node's store, which serves the call;
/// <see cref="Diskless"/> when the silo serves without a LedgerStore; <see cref="NotThisNode"/>; or <see cref="Stopping"/>.
/// </summary>
internal readonly union LocalNodeServing(LedgerStore, LocalNodeServing.Diskless, NotThisNode, LocalNodeServing.Stopping)
{
    /// <summary>The node is registered and serves as a diskless silo holding no LedgerStore (orleans.md §5.14).</summary>
    public sealed record Diskless
    {
        private Diskless()
        {
        }

        /// <summary>Gets the one value.</summary>
        public static Diskless Instance { get; } = new();
    }

    /// <summary>The node stops and admits no new call; the call had no effect.</summary>
    public sealed record Stopping
    {
        private Stopping()
        {
        }

        /// <summary>Gets the one value.</summary>
        public static Stopping Instance { get; } = new();
    }
}

/// <summary>
/// A node's registration as its one write recorded it (orleans.md §5.3, §5.14, OR-I20, OL-I1): its registry entry, the
/// cluster's archive store, and the cluster's object-ledger store when registered, which the node registered with and its
/// record checks compare.
/// </summary>
/// <param name="Entry">The node's registry entry.</param>
/// <param name="ArchiveStore">The archive store the node registry records for the cluster.</param>
/// <param name="ObjectLedgerStore">The object-ledger store the node registry records for the cluster, or <see langword="null"/> when none is registered.</param>
internal sealed record NodeRegistration(NodeRegistryEntry Entry, ArchiveStoreIdentity ArchiveStore, ObjectLedgerStoreId? ObjectLedgerStore = null);
