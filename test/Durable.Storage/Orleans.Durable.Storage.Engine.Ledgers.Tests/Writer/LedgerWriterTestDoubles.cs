using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Ledgers.Memory;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Ledgers.Tests.Writer;

/// <summary>
/// A replica whose operations a test can replace. By default a write answers <see cref="ReplicaAnswers.Written"/>, a read
/// <see cref="ReplicaReadCase.Missing"/>, and a contiguous-tail read its argument, sealed exactly when it seals (LR-I8), and
/// disposal does nothing.
/// </summary>
internal sealed class DelegatingLedgerReplica : ILedgerReplica
{
    internal Func<Slot, ArcBuffer, Slot, bool, CancellationToken, ValueTask<ReplicaWriteOutcome>>? Write { get; init; }

    internal Func<Slot, Slot, CancellationToken, ValueTask<ReplicaRead>>? Read { get; init; }

    internal Func<Slot, bool, CancellationToken, ValueTask<ReplicaTailRead>>? ReadTail { get; init; }

    internal Func<ValueTask>? Disposal { get; init; }

    public ValueTask<ReplicaWriteOutcome> WriteAsync(
        Slot slot,
        ArcBuffer payload,
        Slot contiguousTail,
        bool recovery,
        CancellationToken cancellationToken = default) =>
        Write?.Invoke(slot, payload, contiguousTail, recovery, cancellationToken) ?? ValueTask.FromResult(ReplicaAnswers.Written);

    public ValueTask<ReplicaRead> ReadAsync(
        Slot slot,
        int offset,
        int length,
        Slot contiguousTail,
        CancellationToken cancellationToken = default) =>
        LedgerReplicaOperations.ReadRangeOfValueAsync(this, slot, offset, length, contiguousTail, cancellationToken);

    public ValueTask<ReplicaRead> ReadAsync(
        Slot slot,
        Slot contiguousTail,
        CancellationToken cancellationToken = default) =>
        Read?.Invoke(slot, contiguousTail, cancellationToken) ?? ValueTask.FromResult(ReplicaAnswers.Missing);

    public ValueTask<ReplicaTailRead> ReadTailAsync(
        Slot contiguousTail,
        bool seal,
        CancellationToken cancellationToken = default) =>
        ReadTail?.Invoke(contiguousTail, seal, cancellationToken)
        ?? ValueTask.FromResult<ReplicaTailRead>(new LedgerReplicaTail(contiguousTail, IsSealed: seal));

    public ValueTask DisposeAsync() => Disposal?.Invoke() ?? ValueTask.CompletedTask;
}

/// <summary>
/// One replica operation a <see cref="TailRecordingLedgerReplica"/> saw: its kind, the address of a write or read, and the known
/// contiguous tail it carried (LR-I6).
/// </summary>
internal readonly record struct ReplicaOperation(string Kind, long? Address, long ContiguousTail)
{
    internal static ReplicaOperation WriteAt(long address, long contiguousTail) => new(TailRecordingLedgerReplica.Write, address, contiguousTail);

    internal static ReplicaOperation ReadAt(long address, long contiguousTail) => new(TailRecordingLedgerReplica.Read, address, contiguousTail);

    internal static ReplicaOperation TailRead(long contiguousTail) => new(TailRecordingLedgerReplica.ReadTail, null, contiguousTail);

    internal static ReplicaOperation SealWith(long contiguousTail) => new(TailRecordingLedgerReplica.Seal, null, contiguousTail);
}

/// <summary>
/// Wraps a replica and records each operation with the address and the known contiguous tail it carries. When
/// <see cref="HoldWrites"/> is set, each write waits until the test releases its address, which keeps its slot in
/// flight.
/// </summary>
/// <remarks>
/// Its disposal leaves the inner replica to the test, so several processes' Ledgers, each owning its own recording
/// replica, can share one inner replica as processes share a ledger's storage.
/// </remarks>
internal sealed class TailRecordingLedgerReplica(ILedgerReplica inner) : ILedgerReplica
{
    internal const string Write = "write";
    internal const string Read = "read";
    internal const string ReadTail = "readTail";
    internal const string Seal = "seal";

    private readonly ConcurrentDictionary<long, TaskCompletionSource> _released = new();
    private readonly ConcurrentDictionary<long, TaskCompletionSource> _started = new();
    private readonly ConcurrentDictionary<long, TaskCompletionSource> _landed = new();

    internal bool HoldWrites { get; init; }

    /// <summary>
    /// Gets each operation in arrival order.
    /// </summary>
    internal ConcurrentQueue<ReplicaOperation> Operations { get; } = new();

    /// <summary>
    /// Gets the contiguous-tail reads and seals, in arrival order.
    /// </summary>
    internal ReplicaOperation[] TailOperations() =>
        [.. Operations.Where(static operation => operation.Kind is ReadTail or Seal)];

    internal Task WriteStartedAsync(long address) =>
        Signal(_started, address).Task.WaitAsync(TestContext.Current.CancellationToken);

    internal Task WriteLandedAsync(long address) =>
        Signal(_landed, address).Task.WaitAsync(TestContext.Current.CancellationToken);

    internal void ReleaseWrite(long address) => _ = Signal(_released, address).TrySetResult();

    public async ValueTask<ReplicaWriteOutcome> WriteAsync(
        Slot slot,
        ArcBuffer payload,
        Slot contiguousTail,
        bool recovery,
        CancellationToken cancellationToken = default)
    {
        Operations.Enqueue(ReplicaOperation.WriteAt(slot.Value, contiguousTail.Value));
        _ = Signal(_started, slot.Value).TrySetResult();
        if (HoldWrites)
        {
            await Signal(_released, slot.Value).Task.WaitAsync(cancellationToken);
        }

        var result = await inner.WriteAsync(slot, payload, contiguousTail, recovery, cancellationToken);
        _ = Signal(_landed, slot.Value).TrySetResult();
        return result;
    }

    public ValueTask<ReplicaRead> ReadAsync(
        Slot slot,
        int offset,
        int length,
        Slot contiguousTail,
        CancellationToken cancellationToken = default) =>
        LedgerReplicaOperations.ReadRangeOfValueAsync(this, slot, offset, length, contiguousTail, cancellationToken);

    public ValueTask<ReplicaRead> ReadAsync(
        Slot slot,
        Slot contiguousTail,
        CancellationToken cancellationToken = default)
    {
        Operations.Enqueue(ReplicaOperation.ReadAt(slot.Value, contiguousTail.Value));
        return inner.ReadAsync(slot, contiguousTail, cancellationToken);
    }

    public ValueTask<ReplicaTailRead> ReadTailAsync(
        Slot contiguousTail,
        bool seal,
        CancellationToken cancellationToken = default)
    {
        Operations.Enqueue(seal ? ReplicaOperation.SealWith(contiguousTail.Value) : ReplicaOperation.TailRead(contiguousTail.Value));
        return inner.ReadTailAsync(contiguousTail, seal, cancellationToken);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static TaskCompletionSource Signal(ConcurrentDictionary<long, TaskCompletionSource> signals, long address) =>
        signals.GetOrAdd(address, static _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
}

/// <summary>
/// The primary's process for the tests: its <see cref="Ledger"/> of <see cref="WriterTestData.Id"/> and its
/// <see cref="LedgerWriter"/>, whose sequencer's stops and completions it records. Disposal disposes the writer and then
/// the ledger, which owns the replicas.
/// </summary>
internal sealed class PrimaryLedger : IAsyncDisposable
{
    internal PrimaryLedger(Ledger ledger, TimeProvider timeProvider)
    {
        Ledger = ledger;
        Writer = ledger.CreateWriter(timeProvider);
        Writer.Sequencer.Observer = step =>
        {
            switch (step.Kind)
            {
                case SequencerStepKind.Stopped:
                    StopReasons.Enqueue(step.Reason!.Value);
                    break;
                case SequencerStepKind.Completing:
                    CompletingSlots.Enqueue(step.Slot!.Value);
                    break;
                case SequencerStepKind.Acquired:
                case SequencerStepKind.Completed:
                case SequencerStepKind.Sealed:
                    break;
                default:
                    throw new UnreachableException();
            }
        };
    }

    internal Ledger Ledger { get; }

    internal LedgerWriter Writer { get; }

    internal Sequencer Sequencer => Writer.Sequencer;

    /// <summary>
    /// Gets every stop request the lifetime received, in order.
    /// </summary>
    internal ConcurrentQueue<SequencerStopReason> StopReasons { get; } = new();

    /// <summary>
    /// Gets every slot whose completion the writer called, in order: it calls one only once the slot's write was written.
    /// </summary>
    internal ConcurrentQueue<Slot> CompletingSlots { get; } = new();

    internal Slot KnownContiguousTail => Ledger.KnownContiguousTail;

    internal ValueTask<AppendOutcome> AppendAsync(ArcBuffer payload, CancellationToken cancellationToken = default) =>
        Writer.AppendAsync(payload, cancellationToken);

    /// <summary>
    /// The primary's open check, through its <see cref="Ledger"/>, which stops the writer's lifetime when it finds the
    /// ledger sealed (LP-I14).
    /// </summary>
    internal ValueTask<LedgerTailRead> ReadTailAsync(CancellationToken cancellationToken = default) => Ledger.ReadTailAsync(cancellationToken);

    internal ValueTask<ReplicaRead> ReadAsync(Slot slot, CancellationToken cancellationToken = default) =>
        Ledger.ReadAsync(slot, cancellationToken);

    internal ValueTask<ReplicaRead> ReadAsync(Slot slot, int offset, int length, CancellationToken cancellationToken = default) =>
        Ledger.ReadAsync(slot, offset, length, cancellationToken);

    /// <summary>
    /// The primary's recovery, through its <see cref="Ledger"/>, which stops the writer's lifetime first (LP-I14).
    /// </summary>
    internal ValueTask<RecoveryOutcome> RecoverAsync(CancellationToken cancellationToken = default) => Ledger.RecoverAsync(cancellationToken);

    internal void ObserveRecordedTail(Slot recordedTail) => Ledger.ObserveRecordedTail(recordedTail);

    public async ValueTask DisposeAsync()
    {
        await Writer.DisposeAsync();
        await Ledger.DisposeAsync();
    }
}

internal static class WriterTestData
{
    /// <summary>
    /// The ID of the ledger the tests build; any non-zero ID serves.
    /// </summary>
    internal static readonly LedgerId Id = new(7);

    /// <summary>Returns what a process lends its served writers, with the replica's payload limit and no log.</summary>
    internal static LedgerWriterHost Host(int maxBatchLength = int.MaxValue) => new(maxBatchLength, NullLogger.Instance);

    internal static ArcBuffer CreatePayload(ReadOnlySpan<byte> value)
    {
        using var writer = new ArcBufferWriter();
        writer.Write(value);
        return writer.ConsumeSlice(writer.Length);
    }

    /// <summary>
    /// Creates a memory replica with window <paramref name="windowSize"/>, which owns its storage.
    /// </summary>
    internal static InMemoryLedgerReplica CreateReplica(int windowSize = 4) => new(new LedgerReplicaOptions { WindowSize = windowSize });

    /// <summary>
    /// Builds a process's <see cref="Ledger"/> of <see cref="Id"/> over <paramref name="replicas"/>, in order, with quorum
    /// <paramref name="quorum"/> and window <paramref name="windowSize"/>, which owns them (ledger-protocol.md §2.5).
    /// </summary>
    internal static Ledger CreateLedger(IEnumerable<ILedgerReplica> replicas, int quorum, int windowSize, TimeProvider? timeProvider = null) =>
        LedgerTestData.OnNodes(Id, replicas, quorum, new LedgerReplicaOptions { WindowSize = windowSize }, timeProvider ?? new FakeTimeProvider());

    /// <summary>
    /// Builds the primary's process over a ledger of one replica, <paramref name="replica"/>: its <see cref="Ledger"/> with
    /// <c>N = 1</c>, <c>Q = 1</c>, which owns the replica, and its writer, whose writes and whose ledger's recovery back off
    /// on one clock.
    /// </summary>
    internal static PrimaryLedger CreateLog(ILedgerReplica replica, int windowSize = 4, TimeProvider? timeProvider = null) =>
        CreateLog([replica], quorum: 1, windowSize, timeProvider);

    /// <summary>
    /// Builds the primary's process over <paramref name="replicas"/>, in order, with quorum <paramref name="quorum"/>: its
    /// <see cref="Ledger"/>, which owns the replicas, and its writer, on one clock.
    /// </summary>
    internal static PrimaryLedger CreateLog(IEnumerable<ILedgerReplica> replicas, int quorum, int windowSize = 4, TimeProvider? timeProvider = null)
    {
        var time = timeProvider ?? new FakeTimeProvider();
#pragma warning disable CA2000 // The primary's process owns its Ledger and disposes it.
        return new PrimaryLedger(CreateLedger(replicas, quorum, windowSize, time), time);
#pragma warning restore CA2000
    }

    /// <summary>
    /// Returns the coverage <c>N − Q + 1</c>: the fewest replicas whose failure, seal, or drop leaves fewer than
    /// <c>Q</c> replicas able to answer a writer write <c>Written</c>, or a read <c>Missing</c> (ledger-protocol.md §8.4).
    /// </summary>
    internal static int Coverage(int replicas, int quorum) => replicas - quorum + 1;

    /// <summary>
    /// Appends <paramref name="value"/>, keeping its payload alive until the append ends.
    /// </summary>
    internal static async Task<AppendOutcome> AppendAsync(
        PrimaryLedger log,
        ReadOnlyMemory<byte> value,
        CancellationToken cancellationToken)
    {
        using var payload = CreatePayload(value.Span);
        return await log.AppendAsync(payload, cancellationToken);
    }

    internal static AppendOutcome Appended(long slot) => new(new AppendOutcome.Appended(new Slot(slot)));

    internal static AppendOutcome Sealed(long slot) => new(new AppendOutcome.Sealed(new Slot(slot)));

    internal static AppendOutcome Unresolved(long? slot) =>
        new(new AppendOutcome.Unresolved(slot is { } value ? new Slot(value) : null));

    /// <summary>
    /// Acquires and completes the first <paramref name="count"/> slots of <paramref name="lifetime"/>, a new lifetime,
    /// so that its next slot is <paramref name="count"/>: a lifetime grants slots from 0 and counts a slot complete only
    /// through its completion (ledger-protocol.md §2.6).
    /// </summary>
    internal static async Task AdvanceAsync(Sequencer lifetime, long count, CancellationToken cancellationToken)
    {
        for (var slot = 0L; slot < count; slot++)
        {
            var acquired = await lifetime.AcquireSlotAsync(cancellationToken);
            Assert.Equal(new Slot(slot), acquired.Slot);
            await lifetime.CompleteSlotAsync(acquired, cancellationToken);
        }
    }

    /// <summary>
    /// Asserts that <paramref name="operation"/> fails with the failure of the ledger's one replica, which the ledger
    /// reports inside the <see cref="AggregateException"/> of the quorum it could not reach (ledger-protocol.md §8.3).
    /// </summary>
    internal static async Task<T> ThrowsReplicaFailureAsync<T>(Func<Task> operation)
        where T : Exception =>
        Assert.Single(await ThrowsReplicaFailuresAsync<T>(operation, count: 1));

    /// <summary>
    /// Asserts that <paramref name="operation"/> fails with the failures of <paramref name="count"/> replicas, each a
    /// <typeparamref name="T"/>, which the ledger reports inside the <see cref="AggregateException"/> of the quorum it
    /// could not reach (ledger-protocol.md §8.3).
    /// </summary>
    internal static async Task<T[]> ThrowsReplicaFailuresAsync<T>(Func<Task> operation, int count)
        where T : Exception
    {
        var failure = await Assert.ThrowsAsync<AggregateException>(operation);
        Assert.Equal(count, failure.InnerExceptions.Count);
        return [.. failure.InnerExceptions.Select(static inner => Assert.IsType<T>(inner))];
    }

    /// <summary>
    /// Advances fake time past every pending retry delay until <paramref name="task"/> completes.
    /// </summary>
    internal static async Task<T> AdvanceUntilCompletedAsync<T>(FakeTimeProvider time, Task<T> task)
    {
        while (!task.IsCompleted)
        {
            time.Advance(StorageOutageRetry.MaximumDelay);
            _ = await Task.WhenAny(task, Task.Delay(TimeSpan.FromMilliseconds(1), TimeProvider.System, TestContext.Current.CancellationToken));
        }

        return await task;
    }

    /// <summary>
    /// Waits, in real time, until <paramref name="condition"/> holds.
    /// </summary>
    internal static async Task WaitUntilAsync(Func<bool> condition)
    {
        while (!condition())
        {
            await Task.Delay(TimeSpan.FromMilliseconds(1), TimeProvider.System, TestContext.Current.CancellationToken);
        }
    }
}

/// <summary>
/// Whether an injected write failure took effect before it was reported, as a timed-out request may.
/// </summary>
internal enum CreateFault
{
    /// <summary>
    /// The write failed without storing anything.
    /// </summary>
    NotLanded,

    /// <summary>
    /// The write stored its entry and then reported failure.
    /// </summary>
    Landed,
}

/// <summary>
/// Wraps a replica and fails the next writes as storage does during an outage, recording every attempted write with
/// the known contiguous tail and the recovery flag it carried.
/// </summary>
/// <remarks>
/// Its disposal leaves the inner replica to the test.
/// </remarks>
internal sealed class FlakyLedgerReplica(ILedgerReplica inner) : ILedgerReplica
{
    private readonly ConcurrentDictionary<Slot, ConcurrentQueue<CreateFault>> _faults = new();

    internal ConcurrentQueue<(Slot Address, byte[] Payload, Slot ContiguousTail, bool Recovery, CancellationToken Token)> Creates { get; } = new();

    /// <summary>
    /// Fails the next writes at <paramref name="slot"/>, in order.
    /// </summary>
    internal void FailNextCreates(Slot slot, params CreateFault[] faults)
    {
        var queue = _faults.GetOrAdd(slot, static _ => new ConcurrentQueue<CreateFault>());
        foreach (var fault in faults)
        {
            queue.Enqueue(fault);
        }
    }

    public async ValueTask<ReplicaWriteOutcome> WriteAsync(
        Slot slot,
        ArcBuffer payload,
        Slot contiguousTail,
        bool recovery,
        CancellationToken cancellationToken = default)
    {
        Creates.Enqueue((slot, payload.ToArray(), contiguousTail, recovery, cancellationToken));
        if (!_faults.TryGetValue(slot, out var faults) || !faults.TryDequeue(out var fault))
        {
            return await inner.WriteAsync(slot, payload, contiguousTail, recovery, cancellationToken);
        }

        if (fault == CreateFault.Landed)
        {
            _ = await inner.WriteAsync(slot, payload, contiguousTail, recovery, cancellationToken);
        }

        throw new TimeoutException("Injected storage timeout.");
    }

    public ValueTask<ReplicaRead> ReadAsync(
        Slot slot,
        int offset,
        int length,
        Slot contiguousTail,
        CancellationToken cancellationToken = default) =>
        LedgerReplicaOperations.ReadRangeOfValueAsync(this, slot, offset, length, contiguousTail, cancellationToken);

    public ValueTask<ReplicaRead> ReadAsync(
        Slot slot,
        Slot contiguousTail,
        CancellationToken cancellationToken = default) =>
        inner.ReadAsync(slot, contiguousTail, cancellationToken);

    public ValueTask<ReplicaTailRead> ReadTailAsync(
        Slot contiguousTail,
        bool seal,
        CancellationToken cancellationToken = default) =>
        inner.ReadTailAsync(contiguousTail, seal, cancellationToken);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>
/// A replica whose every operation fails with a new instance of <paramref name="failure"/>: one which repetition cannot
/// change, as a disposed replica's <see cref="ObjectDisposedException"/>, or a transient one.
/// </summary>
internal sealed class FailedLedgerReplica(Func<Exception> failure) : ILedgerReplica
{
    public ValueTask<ReplicaWriteOutcome> WriteAsync(
        Slot slot,
        ArcBuffer payload,
        Slot contiguousTail,
        bool recovery,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromException<ReplicaWriteOutcome>(failure());

    public ValueTask<ReplicaRead> ReadAsync(
        Slot slot,
        int offset,
        int length,
        Slot contiguousTail,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromException<ReplicaRead>(failure());

    public ValueTask<ReplicaRead> ReadAsync(
        Slot slot,
        Slot contiguousTail,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromException<ReplicaRead>(failure());

    public ValueTask<ReplicaTailRead> ReadTailAsync(
        Slot contiguousTail,
        bool seal,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromException<ReplicaTailRead>(failure());

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>
/// The replicas of <see cref="WriterTestData.Id"/> on <c>N</c> in-memory nodes. A test opens the replicas each
/// quorum owns over them, and others through which it seals or drops a replica directly, as another process or the
/// replica's node does; disposing it releases every replica's values.
/// </summary>
internal sealed class LedgerReplicas(int count, int windowSize) : IDisposable
{
    private readonly InMemoryLedgerReplicaTransport _nodes = new();
    private readonly NodeId[] _nodeIds = [.. Enumerable.Range(0, count).Select(static _ => NodeId.New())];
    private readonly LedgerReplicaOptions _options = new() { WindowSize = windowSize };

    /// <summary>
    /// Opens a handle over replica <paramref name="replica"/>, which answers as its node does (LR-I7); disposing it leaves
    /// the replica.
    /// </summary>
    internal ILedgerReplica Open(int replica) => _nodes.Open(_nodeIds[replica], WriterTestData.Id, _options);

    /// <summary>
    /// Opens a handle over each replica, in order.
    /// </summary>
    internal ILedgerReplica[] OpenEach() => [.. Enumerable.Range(0, _nodeIds.Length).Select(Open)];

    /// <summary>
    /// Seals replica <paramref name="index"/>, as another process's recovery does (LR-I8).
    /// </summary>
    internal async Task SealAsync(int index, CancellationToken cancellationToken)
    {
        await using var replica = Open(index);
        _ = await replica.ReadTailAsync(Slot.Zero, seal: true, cancellationToken);
    }

    /// <summary>
    /// Drops replica <paramref name="replica"/> on its node, which refuses its every later operation (LR-I3).
    /// </summary>
    internal ValueTask DropAsync(int replica, CancellationToken cancellationToken) =>
        _nodes.Node(_nodeIds[replica]).DropReplicaAsync(WriterTestData.Id, cancellationToken);

    public void Dispose() => _nodes.Dispose();
}

/// <summary>
/// How no request reaches an <see cref="UnreachableLedgerReplica"/>.
/// </summary>
internal enum Outage
{
    /// <summary>
    /// Each operation fails at once with an <see cref="IOException"/>, a failure repetition may change, as a refused
    /// connection does.
    /// </summary>
    Refused,

    /// <summary>
    /// Each operation waits, unanswered, until its token is cancelled, as one sent into a partition does.
    /// </summary>
    Partitioned,
}

/// <summary>
/// Wraps a replica which no request reaches until the test reconnects it, after which every operation, including those
/// still waiting, reaches the inner replica; it counts every operation sent to it.
/// </summary>
/// <remarks>
/// Its disposal leaves the inner replica to the test.
/// </remarks>
internal sealed class UnreachableLedgerReplica(ILedgerReplica inner, Outage outage) : ILedgerReplica
{
    private readonly TaskCompletionSource _reconnected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _attempts;

    /// <summary>
    /// Gets the number of operations sent to the replica.
    /// </summary>
    internal int Attempts => Volatile.Read(ref _attempts);

    /// <summary>
    /// Lets every later operation, and every one still waiting, reach the inner replica.
    /// </summary>
    internal void Reconnect() => _reconnected.TrySetResult();

    public async ValueTask<ReplicaWriteOutcome> WriteAsync(
        Slot slot,
        ArcBuffer payload,
        Slot contiguousTail,
        bool recovery,
        CancellationToken cancellationToken = default)
    {
        await ReachAsync(cancellationToken);
        return await inner.WriteAsync(slot, payload, contiguousTail, recovery, cancellationToken);
    }

    public ValueTask<ReplicaRead> ReadAsync(
        Slot slot,
        int offset,
        int length,
        Slot contiguousTail,
        CancellationToken cancellationToken = default) =>
        LedgerReplicaOperations.ReadRangeOfValueAsync(this, slot, offset, length, contiguousTail, cancellationToken);

    public async ValueTask<ReplicaRead> ReadAsync(
        Slot slot,
        Slot contiguousTail,
        CancellationToken cancellationToken = default)
    {
        await ReachAsync(cancellationToken);
        return await inner.ReadAsync(slot, contiguousTail, cancellationToken);
    }

    public async ValueTask<ReplicaTailRead> ReadTailAsync(
        Slot contiguousTail,
        bool seal,
        CancellationToken cancellationToken = default)
    {
        await ReachAsync(cancellationToken);
        return await inner.ReadTailAsync(contiguousTail, seal, cancellationToken);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private async ValueTask ReachAsync(CancellationToken cancellationToken)
    {
        _ = Interlocked.Increment(ref _attempts);
        if (_reconnected.Task.IsCompleted)
        {
            return;
        }

        if (outage == Outage.Refused)
        {
            throw new IOException("Injected refused connection.");
        }

        await _reconnected.Task.WaitAsync(cancellationToken);
    }
}
