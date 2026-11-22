#define DEBUG

// Debug's calls compile only where DEBUG is defined, so this file defines it: the double fails its assertion in every
// configuration, the Release tests' among them.
using System.Diagnostics;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Ledgers;

namespace Orleans.Durable.Storage.Testing;

/// <summary>
/// A replica which fails a debug assertion on every write and forwards every other operation to the replica it wraps:
/// a mutant on product code's path, whose exception a quorum counts as one replica's failure
/// (docs/spec/verification.md §11.2).
/// </summary>
/// <param name="inner">The replica it wraps, which it owns.</param>
internal sealed class AssertingLedgerReplica(ILedgerReplica inner) : ILedgerReplica
{
    internal const string Message = "a replica refused a write (the mutant of verification §11.2)";

    public ValueTask<ReplicaWriteOutcome> WriteAsync(
        Slot slot, ArcBuffer payload, Slot contiguousTail, bool recovery, CancellationToken cancellationToken = default)
    {
        Debug.Fail(Message);
        return inner.WriteAsync(slot, payload, contiguousTail, recovery, cancellationToken);
    }

    public ValueTask<ReplicaRead> ReadAsync(Slot slot, Slot contiguousTail, CancellationToken cancellationToken = default) =>
        inner.ReadAsync(slot, contiguousTail, cancellationToken);

    public ValueTask<ReplicaRead> ReadAsync(
        Slot slot, int offset, int length, Slot contiguousTail, CancellationToken cancellationToken = default) =>
        inner.ReadAsync(slot, offset, length, contiguousTail, cancellationToken);

    public ValueTask<ReplicaTailRead> ReadTailAsync(Slot contiguousTail, bool seal, CancellationToken cancellationToken = default) =>
        inner.ReadTailAsync(contiguousTail, seal, cancellationToken);

    public ValueTask DisposeAsync() => inner.DisposeAsync();
}
