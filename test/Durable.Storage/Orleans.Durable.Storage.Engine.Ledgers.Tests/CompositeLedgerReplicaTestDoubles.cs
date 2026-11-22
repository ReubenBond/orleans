using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Ledgers.Tests;

internal readonly record struct LedgerReplicaWriteCall(
    Slot Address,
    byte[] Payload,
    byte[][] BackingArrays,
    Slot ContiguousTail,
    bool Recovery,
    CancellationToken CancellationToken);

internal readonly record struct LedgerReplicaReadCall(
    Slot Address,
    Slot ContiguousTail,
    CancellationToken CancellationToken);

internal readonly record struct LedgerReplicaRangeReadCall(Slot Address, int Offset, int Length, Slot ContiguousTail);

internal readonly record struct LedgerReplicaReadTailCall(Slot ContiguousTail, bool Seal, CancellationToken CancellationToken);

/// <summary>
/// A replica double which records every call and answers through its handlers: by default every write is written, every
/// read missing, and every contiguous-tail read reports its argument, sealed when it seals.
/// </summary>
internal sealed class RecordingLedgerReplica : ILedgerReplica
{
    private readonly Lock _lock = new();
    private readonly List<LedgerReplicaWriteCall> _writeCalls = [];
    private readonly List<LedgerReplicaReadCall> _readCalls = [];
    private readonly List<LedgerReplicaRangeReadCall> _rangeReadCalls = [];
    private readonly List<LedgerReplicaReadTailCall> _readTailCalls = [];
    private int _disposeAsyncCount;

    internal Func<Slot, ArcBuffer, bool, CancellationToken, ValueTask<ReplicaWriteOutcome>>? WriteAsyncHandler { get; set; }

    internal Func<Slot, CancellationToken, ValueTask<ReplicaRead>>? ReadAsyncHandler { get; set; }

    internal Func<Slot, bool, CancellationToken, ValueTask<ReplicaTailRead>>? ReadTailAsyncHandler { get; set; }

    internal Func<ValueTask>? DisposeAsyncHandler { get; init; }

    internal Exception? DisposeAsyncException { get; init; }

    internal int DisposeAsyncCount => Volatile.Read(ref _disposeAsyncCount);

    internal IReadOnlyList<LedgerReplicaWriteCall> WriteCalls
    {
        get
        {
            lock (_lock)
            {
                return [.. _writeCalls];
            }
        }
    }

    internal IReadOnlyList<LedgerReplicaReadCall> ReadCalls
    {
        get
        {
            lock (_lock)
            {
                return [.. _readCalls];
            }
        }
    }

    /// <summary>Gets every ranged read the replica received, which it serves by reading the whole value and slicing it.</summary>
    internal IReadOnlyList<LedgerReplicaRangeReadCall> RangeReadCalls
    {
        get
        {
            lock (_lock)
            {
                return [.. _rangeReadCalls];
            }
        }
    }

    internal IReadOnlyList<LedgerReplicaReadTailCall> ReadTailCalls
    {
        get
        {
            lock (_lock)
            {
                return [.. _readTailCalls];
            }
        }
    }

    public ValueTask<ReplicaWriteOutcome> WriteAsync(
        Slot slot,
        ArcBuffer payload,
        Slot contiguousTail,
        bool recovery,
        CancellationToken cancellationToken = default)
    {
        var call = new LedgerReplicaWriteCall(
            slot,
            payload.ToArray(),
            [.. payload.ArraySegments.Select(static ledger => ledger.Array!)],
            contiguousTail,
            recovery,
            cancellationToken);
        lock (_lock)
        {
            _writeCalls.Add(call);
        }

        return WriteAsyncHandler?.Invoke(slot, payload, recovery, cancellationToken)
            ?? ValueTask.FromResult(ReplicaAnswers.Written);
    }

    public ValueTask<ReplicaRead> ReadAsync(
        Slot slot, int offset, int length, Slot contiguousTail, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            _rangeReadCalls.Add(new LedgerReplicaRangeReadCall(slot, offset, length, contiguousTail));
        }

        return LedgerReplicaOperations.ReadRangeOfValueAsync(this, slot, offset, length, contiguousTail, cancellationToken);
    }

    public ValueTask<ReplicaRead> ReadAsync(
        Slot slot,
        Slot contiguousTail,
        CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            _readCalls.Add(new LedgerReplicaReadCall(slot, contiguousTail, cancellationToken));
        }

        return ReadAsyncHandler?.Invoke(slot, cancellationToken)
            ?? ValueTask.FromResult(ReplicaAnswers.Missing);
    }

    public ValueTask<ReplicaTailRead> ReadTailAsync(
        Slot contiguousTail,
        bool seal,
        CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            _readTailCalls.Add(new LedgerReplicaReadTailCall(contiguousTail, seal, cancellationToken));
        }

        return ReadTailAsyncHandler?.Invoke(contiguousTail, seal, cancellationToken)
            ?? ValueTask.FromResult<ReplicaTailRead>(new LedgerReplicaTail(contiguousTail, seal));
    }

    public ValueTask DisposeAsync()
    {
        _ = Interlocked.Increment(ref _disposeAsyncCount);
        if (DisposeAsyncHandler is { } handler)
        {
            return handler();
        }

        return DisposeAsyncException is null
            ? ValueTask.CompletedTask
            : ValueTask.FromException(DisposeAsyncException);
    }
}

/// <summary>
/// A replica which forwards to the replica it wraps, and which a test makes unreachable, fails some of its reads,
/// writes, or seals, holds its seals or writes until released, or answers its recovery writes sealed, as a down,
/// damaged, slow, or misbehaving replica does; it records the reads and writes it receives and counts its seals.
/// </summary>
/// <param name="inner">The replica it wraps, which it owns.</param>
internal sealed class ControlledLedgerReplica(ILedgerReplica inner) : ILedgerReplica
{
    private readonly Lock _lock = new();
    private readonly List<LedgerReplicaWriteCall> _writes = [];
    private readonly List<LedgerReplicaReadCall> _readCalls = [];
    private readonly Dictionary<long, int> _reads = [];
    private readonly Dictionary<long, int> _answeredReads = [];
    private TaskCompletionSource _sealGate = Released();
    private TaskCompletionSource _readGate = Released();
    private TaskCompletionSource _writeGate = Released();
    private bool _writesObserveCancellation;
    private int _sealFailures;
    private int _sealCalls;
    private int _heldWrites;
    private int _unreachable;

    /// <summary>
    /// Gets or sets a value indicating whether every operation fails with an <see cref="IOException"/>, which recovery
    /// repeats (ledger-protocol.md §6.5).
    /// </summary>
    internal bool Unreachable
    {
        get => Volatile.Read(ref _unreachable) != 0;
        set => Volatile.Write(ref _unreachable, value ? 1 : 0);
    }

    /// <summary>
    /// Gets or sets the failure of a read of an address, or <see langword="null"/> to forward it.
    /// </summary>
    internal Func<Slot, Exception?>? ReadFailure { get; set; }

    /// <summary>
    /// Gets or sets the answer to a read of an address, which the replica gives in place of forwarding it, or
    /// <see langword="null"/> to forward it.
    /// </summary>
    internal Func<Slot, ReplicaRead?>? ReadAnswer { get; set; }

    /// <summary>
    /// Gets or sets the failure of a write of an address, a recovery write when its flag is set, which the replica raises
    /// once the write passes its hold, without applying it; or <see langword="null"/> to forward it.
    /// </summary>
    internal Func<Slot, bool, Exception?>? WriteFailure { get; set; }

    /// <summary>
    /// Gets or sets the failure of a seal, which the replica raises once the seal passes its hold, without applying it; or
    /// <see langword="null"/> to forward it.
    /// </summary>
    internal Func<Exception?>? SealFailure { get; set; }

    /// <summary>
    /// Gets or sets the answer to a write of an address, a recovery write when its flag is set, which the replica gives
    /// once the write passes its hold, without applying it; or <see langword="null"/> to forward it.
    /// </summary>
    internal Func<Slot, bool, ReplicaWriteOutcome?>? WriteAnswer { get; set; }

    /// <summary>
    /// Gets or sets the answer to a seal, which the replica gives once the seal passes its hold, without applying it; or
    /// <see langword="null"/> to forward it.
    /// </summary>
    internal Func<ReplicaTailRead?>? SealAnswer { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the replica misbehaves by answering every recovery write
    /// <see cref="ReplicaAnswers.Sealed"/> without applying it, which no replica may (LR-I8).
    /// </summary>
    internal bool AnswersRecoveryWritesSealed { get; set; }

    /// <summary>
    /// Gets the number of seals the replica received, failed or held ones included.
    /// </summary>
    internal int SealCalls => Volatile.Read(ref _sealCalls);

    /// <summary>
    /// Gets the number of writes the replica holds.
    /// </summary>
    internal int HeldWrites => Volatile.Read(ref _heldWrites);

    /// <summary>
    /// Gets every write the replica received, in order, held ones included.
    /// </summary>
    internal IReadOnlyList<LedgerReplicaWriteCall> Writes
    {
        get
        {
            lock (_lock)
            {
                return [.. _writes];
            }
        }
    }

    /// <summary>
    /// Gets every read the replica received, in order.
    /// </summary>
    internal IReadOnlyList<LedgerReplicaReadCall> Reads
    {
        get
        {
            lock (_lock)
            {
                return [.. _readCalls];
            }
        }
    }

    /// <summary>
    /// Fails the next <paramref name="count"/> seals with an <see cref="IOException"/> before they reach the replica.
    /// </summary>
    internal void FailSeals(int count) => Volatile.Write(ref _sealFailures, count);

    /// <summary>
    /// Holds every seal before it reaches the replica until <see cref="ReleaseSeals"/>.
    /// </summary>
    internal void HoldSeals() => Volatile.Write(ref _sealGate, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

    internal void ReleaseSeals() => _ = Volatile.Read(ref _sealGate).TrySetResult();

    /// <summary>
    /// Holds every read before it reaches the replica until <see cref="ReleaseReads"/>.
    /// </summary>
    internal void HoldReads() => Volatile.Write(ref _readGate, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

    internal void ReleaseReads() => _ = Volatile.Read(ref _readGate).TrySetResult();

    /// <summary>
    /// Holds every write before it reaches the replica until <see cref="ReleaseWrites"/>: a held write ignores its
    /// cancellation, as a slow replica already serving it does, unless <paramref name="observeCancellation"/> is set, when
    /// its cancellation fails it with <see cref="OperationCanceledException"/>.
    /// </summary>
    internal void HoldWrites(bool observeCancellation = false)
    {
        Volatile.Write(ref _writesObserveCancellation, observeCancellation);
        Volatile.Write(ref _writeGate, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
    }

    internal void ReleaseWrites() => _ = Volatile.Read(ref _writeGate).TrySetResult();

    /// <summary>
    /// Returns how many reads of <paramref name="slot"/> the replica received.
    /// </summary>
    internal int ReadsOf(long slot)
    {
        lock (_lock)
        {
            return _reads.GetValueOrDefault(slot);
        }
    }

    /// <summary>
    /// Returns how many reads of <paramref name="slot"/> the replica answered.
    /// </summary>
    internal int AnsweredReadsOf(long slot)
    {
        lock (_lock)
        {
            return _answeredReads.GetValueOrDefault(slot);
        }
    }

    public async ValueTask<ReplicaWriteOutcome> WriteAsync(
        Slot slot, ArcBuffer payload, Slot contiguousTail, bool recovery, CancellationToken cancellationToken = default)
    {
        ThrowIfUnreachable();
        lock (_lock)
        {
            _writes.Add(new LedgerReplicaWriteCall(slot, payload.ToArray(), [], contiguousTail, recovery, cancellationToken));
        }

        var gate = Volatile.Read(ref _writeGate).Task;
        if (!gate.IsCompleted)
        {
            _ = Interlocked.Increment(ref _heldWrites);
            try
            {
                await (Volatile.Read(ref _writesObserveCancellation) ? gate.WaitAsync(cancellationToken) : gate);
            }
            finally
            {
                _ = Interlocked.Decrement(ref _heldWrites);
            }
        }

        if (WriteFailure?.Invoke(slot, recovery) is { } failure)
        {
            throw failure;
        }

        if (WriteAnswer?.Invoke(slot, recovery) is { } answer)
        {
            return answer;
        }

        if (recovery && AnswersRecoveryWritesSealed)
        {
            return ReplicaAnswers.Sealed;
        }

        return await inner.WriteAsync(slot, payload, contiguousTail, recovery, cancellationToken);
    }

    public async ValueTask<ReplicaRead> ReadAsync(Slot slot, Slot contiguousTail, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            _reads[slot.Value] = _reads.GetValueOrDefault(slot.Value) + 1;
            _readCalls.Add(new LedgerReplicaReadCall(slot, contiguousTail, cancellationToken));
        }

        ThrowIfUnreachable();
        await Volatile.Read(ref _readGate).Task.WaitAsync(cancellationToken);
        if (ReadFailure?.Invoke(slot) is { } failure)
        {
            throw failure;
        }

        if (ReadAnswer?.Invoke(slot) is { } answer)
        {
            return answer;
        }

        var result = await inner.ReadAsync(slot, contiguousTail, cancellationToken);
        lock (_lock)
        {
            _answeredReads[slot.Value] = _answeredReads.GetValueOrDefault(slot.Value) + 1;
        }

        return result;
    }

    public ValueTask<ReplicaRead> ReadAsync(
        Slot slot, int offset, int length, Slot contiguousTail, CancellationToken cancellationToken = default) =>
        LedgerReplicaOperations.ReadRangeOfValueAsync(this, slot, offset, length, contiguousTail, cancellationToken);

    public async ValueTask<ReplicaTailRead> ReadTailAsync(Slot contiguousTail, bool seal, CancellationToken cancellationToken = default)
    {
        if (seal)
        {
            _ = Interlocked.Increment(ref _sealCalls);
            await Volatile.Read(ref _sealGate).Task;
            if (SealFailure?.Invoke() is { } failure)
            {
                throw failure;
            }

            if (Interlocked.Decrement(ref _sealFailures) >= 0)
            {
                throw new IOException("An injected seal failure.");
            }

            if (SealAnswer?.Invoke() is { } answer)
            {
                return answer;
            }
        }

        ThrowIfUnreachable();
        return await inner.ReadTailAsync(contiguousTail, seal, cancellationToken);
    }

    public ValueTask DisposeAsync() => inner.DisposeAsync();

    private static TaskCompletionSource Released()
    {
        var released = new TaskCompletionSource();
        released.SetResult();
        return released;
    }

    private void ThrowIfUnreachable()
    {
        if (Unreachable)
        {
            throw new IOException("The replica is unreachable.");
        }
    }
}

internal sealed class TransferredArcBuffer : IDisposable
{
    private readonly ArcBufferWriter _writer = new();
    private readonly ArcBuffer _buffer;
    private bool _disposed;
    private bool _transferred;

    internal TransferredArcBuffer(ReadOnlySpan<byte> value)
    {
        _writer.Write(value);
        _buffer = _writer.PeekSlice(_writer.Length);
        Pages = [.. _buffer.Pages.Select(static page => page)];
    }

    internal IReadOnlyList<ArcBufferPage> Pages { get; }

    internal ArcBuffer Take()
    {
        if (_transferred)
        {
            throw new InvalidOperationException("The buffer has already been transferred.");
        }

        _transferred = true;
        return _buffer;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (!_transferred)
        {
            var buffer = _buffer;
            buffer.Dispose();
        }

        _writer.Dispose();
    }
}

internal static class CompositeLedgerReplicaTestAssertions
{
    internal static void ReferenceCounts(
        IReadOnlyList<ArcBufferPage> pages,
        int expected) =>
        Assert.All(pages, page => Assert.Equal(expected, page.ReferenceCount));
}
