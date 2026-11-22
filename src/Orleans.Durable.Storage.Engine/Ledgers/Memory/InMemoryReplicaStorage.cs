using Orleans.Durable.Storage.Engine.Buffers;

namespace Orleans.Durable.Storage.Engine.Ledgers.Memory;

/// <summary>
/// Owns one process-local ledger replica: its DATA registers, its seal, its retained contiguous tail, and the window it
/// was created with, shared by every <see cref="InMemoryLedgerReplica"/> facade over it (ledger-replicas.md §9.1).
/// </summary>
/// <remarks>
/// <para>
/// Every operation applies ledger-replicas.md §8.1 atomically under the storage lock, so each answer is final when it is given:
/// the memory provider has no crash and no in-flight window, and is an oracle for answers at quiescence (conventions.md
/// §13). Its storage lasts for its lifetime.
/// </para>
/// <para>
/// The replica records the window of the operation which created it and refuses an operation of another window, which
/// would claim a different bound on its unwritten addresses (LR-I6).
/// </para>
/// </remarks>
internal sealed class InMemoryReplicaStorage : IDisposable
{
    private readonly Dictionary<long, ArcBuffer> _values = [];
    private readonly Lock _lock = new();
    private readonly int _windowSize;
    private readonly InMemoryNodeLease? _nodeLease;
    private bool _disposed;
    private bool _dropped;
    private bool _sealed;
    private long _retained;
    private long _grantedLeaseUntilTicks;

    internal InMemoryReplicaStorage(int windowSize, InMemoryNodeLease? nodeLease = null)
    {
        LedgerReplicaOperations.ValidateWindowSize(windowSize);
        _windowSize = windowSize;
        _nodeLease = nodeLease;
    }

    /// <summary>
    /// Applies a writer write or a recovery write (ledger-replicas.md §8.1): a writer write on a sealed replica stores nothing
    /// and answers <see cref="ReplicaWriteOutcome.Sealed"/> (LR-I8); otherwise the first write of an address decides it,
    /// and a later one leaves it (LR-I1, LR-I5).
    /// </summary>
    internal ReplicaWriteOutcome Write(long address, ArcBuffer payload, Slot contiguousTail, bool recovery, int windowSize)
    {
        var snapshot = Snapshot(payload);
        var stored = false;
        try
        {
            lock (_lock)
            {
                if (!Admit(windowSize, contiguousTail))
                {
                    return ReplicaWriteOutcome.Dropped.Instance;
                }

                if (!recovery && _sealed)
                {
                    return ReplicaWriteOutcome.Sealed.Instance;
                }

                // "A write WRITE(a, v) takes effect at its linearization point, setting S(t, a) = v."
                // The LogDrive paper, Appendix A.1.
                stored = _values.TryAdd(address, snapshot);
                if (!recovery && _nodeLease is { } lease)
                {
                    var leaseUntil = lease.ExtendLeaseTicks();
                    if (leaseUntil > _grantedLeaseUntilTicks)
                    {
                        _grantedLeaseUntilTicks = leaseUntil;
                    }

                    return lease.WrittenWithLease;
                }

                return ReplicaWriteOutcome.Written.Instance;
            }
        }
        finally
        {
            if (!stored)
            {
                snapshot.Dispose();
            }
        }
    }

    internal ReplicaRead Read(long address, Slot contiguousTail, int windowSize)
    {
        ArcBuffer retained;
        lock (_lock)
        {
            if (!Admit(windowSize, contiguousTail))
            {
                return ReplicaWriteOutcome.Dropped.Instance;
            }

            if (!_values.TryGetValue(address, out var value))
            {
                return ReplicaRead.Missing.Instance;
            }

            retained = value.Slice(0, value.Length);
        }

        using (retained)
        {
            return Snapshot(retained);
        }
    }

    /// <summary>
    /// Reports the retained contiguous tail and the seal, sealing first when <paramref name="seal"/> is set (LR-I6,
    /// LR-I8, LR-I10).
    /// </summary>
    internal ReplicaTailRead ReadTail(Slot contiguousTail, bool seal, int windowSize)
    {
        lock (_lock)
        {
            if (!Admit(windowSize, contiguousTail))
            {
                return ReplicaWriteOutcome.Dropped.Instance;
            }

            _sealed |= seal;
            var leaseRemaining = seal && _nodeLease is { } lease
                ? lease.ComputeLeaseRemaining(_grantedLeaseUntilTicks)
                : TimeSpan.Zero;
            return new LedgerReplicaTail(new Slot(_retained), _sealed, leaseRemaining);
        }
    }

    /// <summary>
    /// Extends the RAM-only lease when unsealed, or reports <see cref="ReplicaHeartbeatStatus.Sealed"/> /
    /// <see cref="ReplicaHeartbeatStatus.Dropped"/> (LR-I10).
    /// </summary>
    internal ReplicaHeartbeatStatus Heartbeat()
    {
        lock (_lock)
        {
            if (_dropped)
            {
                return ReplicaHeartbeatStatus.Dropped;
            }

            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_sealed)
            {
                return ReplicaHeartbeatStatus.Sealed;
            }

            if (_nodeLease is { } lease)
            {
                var leaseUntil = lease.ExtendLeaseTicks();
                if (leaseUntil > _grantedLeaseUntilTicks)
                {
                    _grantedLeaseUntilTicks = leaseUntil;
                }
            }

            return ReplicaHeartbeatStatus.Granted;
        }
    }

    /// <summary>
    /// Discards RAM-only lease state on simulated node restart while preserving durable slots and seal.
    /// </summary>
    internal void ResetLeaseOnRestart()
    {
        lock (_lock)
        {
            _grantedLeaseUntilTicks = 0;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Release();
        }
    }

    /// <summary>
    /// Removes every value and the seal, and refuses every later operation; the refusal and the removal are one step
    /// (LR-I3).
    /// </summary>
    internal void Drop()
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _dropped = true;
            _sealed = false;
            _retained = 0;
            Release();
        }
    }

    private static ArcBuffer Snapshot(ArcBuffer payload)
    {
        using var writer = new ArcBufferWriter(payload.Length);
        payload.CopyTo(writer);
        return writer.ConsumeSlice(writer.Length);
    }

    /// <summary>
    /// Admits an operation of window <paramref name="windowSize"/> carrying <paramref name="contiguousTail"/>, which the
    /// replica retains (LR-I6), or returns <see langword="false"/> once the node dropped the replica; the caller holds the
    /// lock.
    /// </summary>
    private bool Admit(int windowSize, Slot contiguousTail)
    {
        // A dropped replica answers as dropped through every facade, whose storage the drop also disposed.
        if (_dropped)
        {
            return false;
        }

        ObjectDisposedException.ThrowIf(_disposed, this);
        LedgerReplicaOperations.ValidateReplicaWindow(_windowSize, windowSize);
        _retained = Math.Max(_retained, contiguousTail.Value);
        return true;
    }

    private void Release()
    {
        foreach (var value in _values.Values)
        {
            var owned = value;
            owned.Dispose();
        }

        _values.Clear();
    }
}
