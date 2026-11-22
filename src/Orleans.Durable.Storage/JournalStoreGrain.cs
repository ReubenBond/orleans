using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Placement;

namespace Orleans.Durable.Storage;

/// <summary>
/// A node's <see cref="IJournalStoreGrain"/> activation (orleans.md §5.5, §7.2): placed only on the silo the registry names
/// for the node (<see cref="RegisteredNodePlacement"/>) and never moved, it runs each call on the thread pool, off the
/// activation's scheduler, through its silo's <see cref="JournalNodeHost"/>, and keeps no state of its own, so its calls
/// interleave. An activation which finds its silo serving another node answers <see cref="NotThisNode"/> and deactivates
/// (OR-I14).
/// </summary>
[RegisteredNodePlacement]
[Immovable]
internal sealed class JournalStoreGrain(JournalNodeHost host) : Grain, IJournalStoreGrain
{
    /// <inheritdoc/>
    public Task<JournalForwardedWriteOutcome> WriteAsync(JournalForwardedEntry entry, CancellationToken cancellationToken) =>
        host.WriteFromGrainAsync(this, Node, TaskScheduler.Current, entry, cancellationToken);

    /// <inheritdoc/>
    public Task<JournalForwardedRead> ReadAsync(string key, CancellationToken cancellationToken) =>
        host.ReadFromGrainAsync(this, Node, TaskScheduler.Current, key, cancellationToken);

    /// <inheritdoc/>
    public Task<JournalForwardedPage> ReadPageAsync(Guid snapshot, long offset, CancellationToken cancellationToken) =>
        host.ReadPageFromGrainAsync(this, Node, TaskScheduler.Current, snapshot, offset, cancellationToken);

    /// <inheritdoc/>
    public Task ReleaseSnapshotAsync(Guid snapshot) =>
        host.ReleaseSnapshotFromGrainAsync(this, Node, TaskScheduler.Current, snapshot);

    private NodeId Node => new(this.GetPrimaryKey());
}
