using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Ledgers;

namespace Orleans.Durable.Storage.Testing;

/// <summary>
/// A replica transport over another whose replicas answer a whole-slot read <see cref="ReplicaWriteOutcome.Backpressure"/>
/// while <see cref="Saturated"/> says so, before reading anything, as a saturated replica does (ledger-replicas.md §2.1),
/// and count each such answer. A test may also hold a replica's opening, as a ledger's first lease does it, the answer
/// of a write the replica has stored, as a slow acknowledgement does, or a recovery's seal, or make every replica
/// unreachable, as a partition from all of them does.
/// </summary>
internal sealed class BackpressureTransport(ILedgerReplicaTransport inner) : ILedgerReplicaTransport
{
    private int _answered;

    /// <summary>Gets or sets which reads, by ledger and slot, answer backpressure; none by default.</summary>
    public Func<LedgerId, Slot, bool> Saturated { get; set; } = static (_, _) => false;

    /// <summary>Gets or sets a step each replica's opening runs first, which a test blocks to hold a ledger's first lease; none by default.</summary>
    public Action<LedgerId>? BeforeOpen { get; set; }

    /// <summary>
    /// Gets or sets a step each replica write awaits once the replica has stored it, before it answers, which a test holds
    /// to delay a batch's acknowledgement; none by default.
    /// </summary>
    public Func<LedgerId, Slot, Task>? BeforeWriteAnswer { get; set; }

    /// <summary>
    /// Gets or sets a step each sealing tail read awaits before it reaches the replica, which a test holds to delay a
    /// recovery's seal while seal checks proceed; none by default.
    /// </summary>
    public Func<LedgerId, Task>? BeforeSeal { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether every replica call fails, before it reaches its replica, with the
    /// <see cref="TimeoutException"/> of a call whose deadline passed, as one to a partitioned node does; false by default.
    /// </summary>
    public bool Unreachable { get; set; }

    /// <summary>Gets the number of reads which answered backpressure.</summary>
    public int Answered => Volatile.Read(ref _answered);

    public ILedgerReplica Open(NodeId node, LedgerId ledger, LedgerReplicaOptions options)
    {
        BeforeOpen?.Invoke(ledger);
        return new Replica(this, ledger, inner.Open(node, ledger, options));
    }

    public ValueTask<ReplicaHeartbeatRead> HeartbeatAsync(NodeId node, ReadOnlyMemory<LedgerId> ledgers, CancellationToken cancellationToken = default) =>
        Unreachable
            ? throw new TimeoutException($"Replica node {node} is unreachable.")
            : inner.HeartbeatAsync(node, ledgers, cancellationToken);

    private sealed class Replica(BackpressureTransport transport, LedgerId ledger, ILedgerReplica inner) : ILedgerReplica
    {
        public ValueTask<ReplicaWriteOutcome> WriteAsync(Slot slot, ArcBuffer payload, Slot contiguousTail, bool recovery, CancellationToken cancellationToken = default) =>
            transport.Unreachable ? ValueTask.FromException<ReplicaWriteOutcome>(Partitioned())
            : transport.BeforeWriteAnswer is { } hold
                ? HeldAsync(inner.WriteAsync(slot, payload, contiguousTail, recovery, cancellationToken), hold(ledger, slot), cancellationToken)
                : inner.WriteAsync(slot, payload, contiguousTail, recovery, cancellationToken);

        public ValueTask<ReplicaRead> ReadAsync(Slot slot, Slot contiguousTail, CancellationToken cancellationToken = default)
        {
            if (transport.Unreachable)
            {
                return ValueTask.FromException<ReplicaRead>(Partitioned());
            }

            if (transport.Saturated(ledger, slot))
            {
                _ = Interlocked.Increment(ref transport._answered);
                return ValueTask.FromResult(new ReplicaRead(ReplicaWriteOutcome.Backpressure.Instance));
            }

            return inner.ReadAsync(slot, contiguousTail, cancellationToken);
        }

        public ValueTask<ReplicaRead> ReadAsync(Slot slot, int offset, int length, Slot contiguousTail, CancellationToken cancellationToken = default) =>
            transport.Unreachable ? ValueTask.FromException<ReplicaRead>(Partitioned()) : inner.ReadAsync(slot, offset, length, contiguousTail, cancellationToken);

        public ValueTask<ReplicaTailRead> ReadTailAsync(Slot contiguousTail, bool seal, CancellationToken cancellationToken = default) =>
            transport.Unreachable ? ValueTask.FromException<ReplicaTailRead>(Partitioned())
            : seal && transport.BeforeSeal is { } hold
                ? SealHeldAsync(hold(ledger), contiguousTail, cancellationToken)
                : inner.ReadTailAsync(contiguousTail, seal, cancellationToken);

        public ValueTask DisposeAsync() => inner.DisposeAsync();

        private static TimeoutException Partitioned() => new("The test's transport reaches no replica: the call's deadline passed.");

        private async ValueTask<ReplicaTailRead> SealHeldAsync(Task hold, Slot contiguousTail, CancellationToken cancellationToken)
        {
            await hold.WaitAsync(cancellationToken).ConfigureAwait(false);
            return await inner.ReadTailAsync(contiguousTail, seal: true, cancellationToken).ConfigureAwait(false);
        }

        private static async ValueTask<ReplicaWriteOutcome> HeldAsync(ValueTask<ReplicaWriteOutcome> writing, Task hold, CancellationToken cancellationToken)
        {
            var outcome = await writing.ConfigureAwait(false);
            await hold.WaitAsync(cancellationToken).ConfigureAwait(false);
            return outcome;
        }
    }
}
