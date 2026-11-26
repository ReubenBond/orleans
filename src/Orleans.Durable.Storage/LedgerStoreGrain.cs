using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Placement;

namespace Orleans.Durable.Storage;

/// <summary>
/// A node's <see cref="ILedgerStoreGrain"/> activation (orleans.md §5.12, §7.2): placed only on the silo the registry
/// names for the node (<see cref="RegisteredNodePlacement"/>) and never moved, it runs each call's store work on the thread
/// pool, off the activation's scheduler, through its silo's <see cref="LedgerReplicaHost"/>, and keeps no state of its
/// own. An activation which finds its silo serving another node answers <see cref="NotThisNode"/> and deactivates (OR-I14).
/// </summary>
[RegisteredNodePlacement]
[Immovable]
internal sealed class LedgerStoreGrain(LedgerReplicaHost host) : Grain, ILedgerStoreGrain
{
    /// <inheritdoc/>
    public Task<RemoteReplicaWriteOutcome> WriteAsync(LedgerReplicaWriteRequest request, CancellationToken cancellationToken) =>
        host.WriteFromGrainAsync(this, Node, TaskScheduler.Current, request, cancellationToken);

    /// <inheritdoc/>
    public Task<RemoteReplicaRead> ReadAsync(LedgerReplicaReadRequest request, CancellationToken cancellationToken) =>
        host.ReadFromGrainAsync(this, Node, TaskScheduler.Current, request, cancellationToken);

    /// <inheritdoc/>
    public Task<RemoteReplicaRangeRead> ReadRangeAsync(LedgerReplicaReadRangeRequest request, CancellationToken cancellationToken) =>
        host.ReadRangeFromGrainAsync(this, Node, TaskScheduler.Current, request, cancellationToken);

    /// <inheritdoc/>
    public Task<RemoteReplicaSlotsRead> ReadSlotsAsync(LedgerReplicaReadSlotsRequest request, CancellationToken cancellationToken) =>
        host.ReadSlotsFromGrainAsync(this, Node, TaskScheduler.Current, request, cancellationToken);

    /// <inheritdoc/>
    public Task<RemoteReplicaTailRead> ReadTailAsync(LedgerReplicaTailRequest request, CancellationToken cancellationToken) =>
        host.ReadTailFromGrainAsync(this, Node, TaskScheduler.Current, request, cancellationToken);

    /// <inheritdoc/>
    public Task<RemoteReplicaHeartbeatRead> HeartbeatAsync(LedgerReplicaHeartbeatRequest request, CancellationToken cancellationToken) =>
        host.HeartbeatFromGrainAsync(this, Node, TaskScheduler.Current, request, cancellationToken);

    private NodeId Node => new(this.GetPrimaryKey());
}
