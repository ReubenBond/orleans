using System.Globalization;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.Ledgers.Memory;
#if !DURABLE_STORAGE_SIMULATION_TESTS
using Orleans.Durable.Storage.Engine.Ledgers.Tests;
#endif

namespace Orleans.Durable.Storage.Testing;

/// <summary>
/// A defect the harness builds into the primary's process from real components, to show the fold oracle fails it.
/// </summary>
internal enum LedgerMutation
{
    /// <summary>The ledger as it is.</summary>
    None,

    /// <summary>
    /// A recovery is judged as raising its process's known contiguous tail to its end (LP-I11). The harness builds the
    /// defect into the recorded <c>KnownAfter</c> alone, without raising the <see cref="Ledger"/>'s known contiguous tail
    /// past a recorded <c>T</c>.
    /// </summary>
    RaiseTailToEnd,

    /// <summary>A recovery in the writer's process stops the writer's lifetime only after it sealed (LP-I14).</summary>
    StopAfterSeal,

    /// <summary>The writer appends at a slot it already appended at (ledger-protocol.md §2.3).</summary>
    AcceptSlotTwice,

    /// <summary>
    /// Each read of the primary's process is judged as carrying one above the tail its requests carried (LP-I11). The
    /// harness builds the defect into the judgment alone: a replica which heard the higher tail would hold a contiguous
    /// tail the writer never reached, and the writer's later writes would wait on it rather than meet the oracle.
    /// </summary>
    ReadsCarryAboveTail,

    /// <summary>
    /// A learn is judged as answering one past the tail its replica answered (LP-I11). The harness builds the defect into
    /// the outcome alone, without raising the <see cref="Ledger"/>'s known contiguous tail past a recorded <c>T</c>.
    /// </summary>
    LearnPastTail,
}

/// <summary>
/// What the fold oracle's runs reached.
/// </summary>
internal sealed class LedgerCoverage
{
    private int _observedPastCompleted;
    private long _hedges;
    private long _missingFallbacks;

    /// <summary>
    /// Gets how many times the primary's process learned a recorded <c>T</c> above its writer's completed prefix.
    /// </summary>
    internal int ObservedPastCompleted => Volatile.Read(ref _observedPastCompleted);

    /// <summary>Gets how many hedges the primary's reads below the tail sent.</summary>
    internal long Hedges => Interlocked.Read(ref _hedges);

    /// <summary>Gets how many times a replica answered a read below the tail missing, so the read fell back past it.</summary>
    internal long MissingFallbacks => Interlocked.Read(ref _missingFallbacks);

    internal void RecordObservedPastCompleted() => Interlocked.Increment(ref _observedPastCompleted);

    internal void RecordHedges(long hedges) => Interlocked.Add(ref _hedges, hedges);

    internal void RecordMissingFallback() => Interlocked.Increment(ref _missingFallbacks);
}

/// <summary>
/// A memory replica whose answers the fold oracle skews, so that its checks discriminate.
/// </summary>
/// <param name="inner">The replica, which the wrapper disposes.</param>
/// <param name="leads">
/// Whether this replica answers seals after the others and alone answers a read below the tail the read carries with a
/// batch. A read below its tail is decided, so the first batch any replica returns answers it (LP-I10); once the leading
/// replica holds the slot, the other replicas answer it without reaching the store, so the read always finds the batch
/// on the leader and this replica's contiguous tail can lead theirs: missing at every third slot,
/// as a replica which missed the write does; failing at the next, as a timed-out call does; and failing at the third
/// after twice the hedge delay on <paramref name="time"/>, as a slow call does, so a read which asked it first hedges.
/// </param>
/// <param name="time">The quorum's clock, which the harness advances.</param>
/// <param name="answersLost">
/// Whether every answer to a writer write which lands is lost now: the transport cannot decode it, an unknown outcome
/// which repetition cannot change (ledger-replicas.md §2.2), so the append's write fails after it was sent.
/// </param>
/// <param name="requestsLost">
/// Whether every writer write is lost before it lands now, and its answer cannot be decoded: an unknown outcome which
/// repetition cannot change, so the append's write fails and its batch never lands.
/// </param>
/// <param name="dropped">
/// Whether the replica's node dropped the ledger, after which it answers every read honestly, as a dropped node does.
/// </param>
/// <param name="coverage">Counts the reads below the tail this replica answers missing.</param>
/// <param name="leader">The slots the leading replica holds, which its writes add once it answers them written.</param>
/// <param name="leaderHolds">
/// Whether the leading replica held the slot when the current read began, so a leader straggler completing mid-read
/// after the leader answered missing cannot turn the remaining replicas dishonest.
/// </param>
internal sealed class SkewedLedgerReplica(ILedgerReplica inner, bool leads, TimeProvider time, Func<bool> answersLost, Func<bool> requestsLost, Func<bool> dropped, LedgerCoverage? coverage, LeaderSlots leader, Func<Slot, bool> leaderHolds) : ILedgerReplica
{
    public async ValueTask<ReplicaWriteOutcome> WriteAsync(
        Slot slot, ArcBuffer payload, Slot contiguousTail, bool recovery, CancellationToken cancellationToken = default)
    {
        if (!recovery && requestsLost())
        {
            throw new InvalidDataException("The write was lost before it landed, and its answer could not be decoded.");
        }

        var lost = !recovery && answersLost();
        var result = await inner.WriteAsync(slot, payload, contiguousTail, recovery, cancellationToken);
        if (leads && result is ReplicaWriteOutcome.Written)
        {
            leader.Add(slot);
        }

        return lost && result is ReplicaWriteOutcome.Written
            ? throw new InvalidDataException("The replica's answer to the write could not be decoded.")
            : result;
    }

    public ValueTask<ReplicaRead> ReadAsync(Slot slot, Slot contiguousTail, CancellationToken cancellationToken = default) =>
        Honest(slot, contiguousTail) ? inner.ReadAsync(slot, contiguousTail, cancellationToken) : AnswerWithoutABatch(slot, cancellationToken);

    public ValueTask<ReplicaRead> ReadAsync(
        Slot slot, int offset, int length, Slot contiguousTail, CancellationToken cancellationToken = default) =>
        Honest(slot, contiguousTail) ? inner.ReadAsync(slot, offset, length, contiguousTail, cancellationToken) : AnswerWithoutABatch(slot, cancellationToken);

    public async ValueTask<ReplicaTailRead> ReadTailAsync(Slot contiguousTail, bool seal, CancellationToken cancellationToken = default)
    {
        var tail = await inner.ReadTailAsync(contiguousTail, seal, cancellationToken);
        if (seal && leads)
        {
            // The seal lands at once, and its answer arrives after the others'.
            await YieldAsync();
        }

        return tail;
    }

    public ValueTask DisposeAsync() => inner.DisposeAsync();

    private static ValueTask<ReplicaRead> TimedOut() => ValueTask.FromException<ReplicaRead>(new TimeoutException("The read timed out."));

    private bool Honest(Slot slot, Slot contiguousTail) => dropped() || leads || slot >= contiguousTail || !leaderHolds(slot);

    private ValueTask<ReplicaRead> AnswerWithoutABatch(Slot slot, CancellationToken cancellationToken)
    {
        switch (slot.Value % 3)
        {
            case 0:
                coverage?.RecordMissingFallback();
                return ValueTask.FromResult(new ReplicaRead(ReplicaRead.Missing.Instance));
            case 1:
                return TimedOut();
            default:
                return SlowlyTimedOutAsync(cancellationToken);
        }
    }

    private async ValueTask<ReplicaRead> SlowlyTimedOutAsync(CancellationToken cancellationToken)
    {
        await DelayAsync(2 * LedgerQuorum.ReadHedgeDelay, time, cancellationToken);
        return await TimedOut();
    }

    // In a simulation the test assembly is not rewritten, so the replica yields and waits through the simulation kit,
    // whose continuations the simulation schedules (verification.md §7.1).
#if DURABLE_STORAGE_SIMULATION_TESTS
    private static Task YieldAsync() => Orleans.Durable.Storage.Simulation.SimulationTasks.Yield();

    private static Task DelayAsync(TimeSpan delay, TimeProvider timeProvider, CancellationToken cancellationToken) =>
        Orleans.Durable.Storage.Simulation.SimulationTasks.Delay(delay, timeProvider, cancellationToken);
#else
    private static async Task YieldAsync() => await Task.Yield();

    private static Task DelayAsync(TimeSpan delay, TimeProvider timeProvider, CancellationToken cancellationToken) =>
        Task.Delay(delay, timeProvider, cancellationToken);
#endif
}

/// <summary>
/// The slots the fold oracle's leading replica holds, so the other replicas report a decided slot lost only once the
/// leader holds it.
/// </summary>
internal sealed class LeaderSlots
{
    private readonly Lock _lock = new();
    private readonly HashSet<long> _slots = [];

    internal void Add(Slot slot)
    {
        lock (_lock)
        {
            _ = _slots.Add(slot.Value);
        }
    }

    internal bool Holds(Slot slot)
    {
        lock (_lock)
        {
            return _slots.Contains(slot.Value);
        }
    }
}

/// <summary>
/// Orders a recovery's seal answers, as the fold oracle's scripted cases do: the replica beneath the recording, whose
/// seal answers it holds, and the replica above it, whose recorded answers it delays and notes, and each step the
/// recovery handled.
/// </summary>
internal interface ISealOrder
{
    /// <summary>Wraps the replica below the recording.</summary>
    ILedgerReplica Answering(ILedgerReplica inner, int replica);

    /// <summary>Wraps the replica above the recording.</summary>
    ILedgerReplica Delivering(ILedgerReplica inner, int replica);

    /// <summary>Observes a step the recovery handled.</summary>
    void OnStep(RecoveryStep step);
}

/// <summary>
/// The system the fold oracle drives: a ledger's replicas on <c>N</c> nodes, memory nodes or another
/// <see cref="ILedgerSystemNodes"/>; the primary's process, its <see cref="Ledger"/> and its <see cref="LedgerWriter"/>;
/// and other processes' <see cref="Ledger"/>s, which check and recover the ledger. Every replica request any process
/// sends is recorded, with the moment the writer's lifetime stopped.
/// </summary>
/// <remarks>
/// On the thread pool the quorum runs on a fake clock, which a read advances past each timer it arms, and the writer and
/// the recording on the system clock. In a simulation (<c>DURABLE_STORAGE_SIMULATION_TESTS</c>) all three run on the virtual clock, which
/// advances once nothing else can run, and the system uses no cancellation.
/// </remarks>
internal sealed class LedgerSystem : ILedgerConformanceSystem, IAsyncDisposable
{
    private const string Primary = "primary";

    private static readonly LedgerId Id = new(7);

    /// <summary>The harness call whose replica requests flow from this execution context, if any.</summary>
    private static readonly AsyncLocal<FlowedRequests?> CurrentCall = new();

    /// <summary>
    /// Whether the leading replica already held the read's slot when the current primary read began, snapshotted so a
    /// leader straggler write completing mid-read cannot make the remaining replicas dishonest (#444).
    /// </summary>
    private static readonly AsyncLocal<bool?> CurrentReadLeaderHeld = new();

    // The observer of the recovery the current call runs, which every quorum's observer forwards to, so concurrent
    // recoveries through one Ledger each see their own steps.
    private static readonly AsyncLocal<Action<RecoveryStep>?> CurrentRecovery = new();

    /// <summary>The weight of a check or a recovery among the operations in flight, above any count of the others.</summary>
    private const int Alone = 1 << 16;

    private readonly ILedgerSystemNodes _nodes;
#if DURABLE_STORAGE_SIMULATION_TESTS
    private readonly TimeProvider _time = SystemClock;
#else
    private readonly TimerSignallingTimeProvider _time = new();
#endif
    private readonly NodeId[] _nodeIds;
    private readonly bool[] _dropped;
    private readonly LedgerReplicaOptions _options;
    private readonly int _quorum;
    private readonly LedgerMutation _mutation;
    private readonly LedgerCoverage? _coverage;
    private readonly int _leader;
    private readonly ISealOrder? _sealOrder;
    private readonly HistoryRecorder<ReplicaCall, ReplicaAnswer> _recording = new(SystemClock);
    private readonly List<long[]> _appends = [];
    private readonly Func<ILedgerReplica, ILedgerReplica>? _wrap;
    private readonly Func<Task>? _settle;
    private readonly LeaderSlots _leaderSlots = new();
    private readonly Ledger _primary;
#pragma warning disable CA2213 // The primary's ledger owns and disposes its quorum; the system keeps it for its hedge count.
    private readonly LedgerQuorum _primaryQuorum;
#pragma warning restore CA2213
    private readonly LedgerWriter _writer;
    private long _stoppedAt = long.MaxValue;
    private long _end = -1;
    private int _processes;
    private int _running;
    private bool _answersLost;
    private bool _requestsLost;

#if DURABLE_STORAGE_SIMULATION_TESTS
    private static TimeProvider SystemClock => Orleans.Durable.Storage.Simulation.SimulationEnvironment.Clock;

    private static CancellationToken Cancellation => CancellationToken.None;
#else
    private static TimeProvider SystemClock => TimeProvider.System;

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;
#endif

    internal LedgerSystem(int replicas, int quorum, int window, LedgerMutation mutation, LedgerCoverage? coverage, int leader, ISealOrder? sealOrder = null, Func<ILedgerReplica, ILedgerReplica>? wrap = null, Func<Task>? settle = null, ILedgerSystemNodes? nodes = null, TimeProvider? writerClock = null)
    {
        _wrap = wrap;
        _settle = settle;
        _leader = leader;
        _sealOrder = sealOrder;
        _nodes = nodes ?? new InMemoryLedgerSystemNodes();
        _nodeIds = NodeIds(replicas);
        _dropped = new bool[replicas];
        _options = new LedgerReplicaOptions { WindowSize = window };
        _quorum = quorum;
        _mutation = mutation;
        _coverage = coverage;
        _primary = Open(Primary, out _primaryQuorum, _nodeIds[0]);
        _writer = _primary.CreateWriter(writerClock ?? SystemClock);
        _writer.AcceptsSlotTwiceMutant = mutation == LedgerMutation.AcceptSlotTwice;
        _writer.Sequencer.Observer = step =>
        {
            if (step.Kind == SequencerStepKind.Stopped)
            {
                _ = Interlocked.CompareExchange(ref _stoppedAt, _recording.Now(), long.MaxValue);
            }
        };
    }

    /// <summary>
    /// Returns the system's node IDs, fixed, so a simulated case's quorum orders its replicas the same on every run.
    /// </summary>
    internal static NodeId[] NodeIds(int replicas) =>
        [.. Enumerable.Range(0, replicas).Select(static index => new NodeId(new Guid(index + 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0)))];

    public Task<LedgerOutcome> AppendAsync(LedgerAppend request) => AppendAsync(request.Value, twice: false);

    public async Task<LedgerOutcome> AcquireAsync(LedgerAcquire request)
    {
        var cancellationToken = Cancellation;
        switch (request.Use)
        {
            case AcquireUse.AppendTwice:
                return await AppendAsync(request.Value, twice: true);
            case AcquireUse.AppendForeign:
                {
                    var running = Enter(alone: true);
                    var flowed = new FlowedRequests();
                    CurrentCall.Value = flowed;
                    using var payload = ArcBuffer.Wrap(LedgerStorageSpec.Payload(request.Value));
                    try
                    {
                        _ = await _writer.AppendAsync(new Slot(1_000), payload, cancellationToken);
                        return new LedgerOutcome(LedgerOutcomeKind.AppendedTwice, 1_000);
                    }
                    catch (ArgumentException)
                    {
                        return new LedgerOutcome(LedgerOutcomeKind.Refused, Recording: new(MaxCarried(flowed)));
                    }
                    finally
                    {
                        Leave(running);
                    }
                }

            case AcquireUse.Cancelled:
                {
                    var running = Enter(alone: true);
                    var flowed = new FlowedRequests();
                    CurrentCall.Value = flowed;
                    using var cancelled = new CancellationTokenSource();
                    await cancelled.CancelAsync();
                    try
                    {
                        return await _writer.AcquireAsync(cancelled.Token) is { } slot
                            ? new LedgerOutcome(LedgerOutcomeKind.Appended, slot.Value, Recording: new())
                            : new LedgerOutcome(LedgerOutcomeKind.Unresolved, -1, Recording: new());
                    }
                    catch (OperationCanceledException)
                    {
                        return new LedgerOutcome(LedgerOutcomeKind.Cancelled, Recording: new(MaxCarried(flowed)));
                    }
                    finally
                    {
                        Leave(running);
                    }
                }

            default:
                throw new ArgumentOutOfRangeException(nameof(request));
        }
    }

    public async Task<LedgerOutcome> AppendUnansweredAsync(LedgerAppendUnanswered request)
    {
        LoseWrites(request.Landed, lost: true);
        try
        {
            return await AppendAsync(request.Value, twice: false);
        }
        finally
        {
            LoseWrites(request.Landed, lost: false);
        }
    }

    /// <summary>
    /// Loses every writer write's answers once it lands, when <paramref name="landed"/>, or the write before it lands.
    /// </summary>
    private void LoseWrites(bool landed, bool lost)
    {
        if (landed)
        {
            Volatile.Write(ref _answersLost, lost);
        }
        else
        {
            Volatile.Write(ref _requestsLost, lost);
        }
    }

    public Task<LedgerOutcome> ReadAsync(LedgerSlotRead request) =>
        ReadAsync(new Slot(request.Slot), (ledger, cancellationToken) => ledger.ReadAsync(new Slot(request.Slot), cancellationToken), refusesRange: false);

    public Task<LedgerOutcome> ReadRangeAsync(LedgerRangeRead request) =>
        ReadAsync(new Slot(request.Slot), (ledger, cancellationToken) => ledger.ReadAsync(new Slot(request.Slot), request.Offset, request.Length, cancellationToken), refusesRange: true);

    /// <summary>
    /// Reads every slot from the scan''s first below the primary''s known contiguous tail when it begins, in slot order, as
    /// a drain does: each answers its batch, or <c>Dropped</c> once the ledger''s storage is reclaimed (LL-I6, LL-I9).
    /// </summary>
    public async Task<LedgerOutcome> ScanAsync(LedgerScan request)
    {
        var running = Enter(alone: false);
        try
        {
            var flowed = new FlowedRequests();
            CurrentCall.Value = flowed;
            var below = _primary.KnownContiguousTail.Value;
            List<byte[]?> batches = [];
            for (var slot = request.From; slot < below; slot++)
            {
                var target = new Slot(slot);
                var read = await ReadPrimaryAsync(target, (ledger, cancellationToken) => ledger.ReadAsync(target, cancellationToken));
                if (read.TryGetValue(out ArcBuffer payload))
                {
                    using (payload)
                    {
                        batches.Add(payload.ToArray());
                    }
                }
                else if (read.Case == ReplicaReadCase.Dropped)
                {
                    batches.Add(null);
                }
                else
                {
                    // LL-I6: a read below the bound never answers Missing.
                    return new LedgerOutcome(LedgerOutcomeKind.Missing, slot, Recording: new(MaxCarried(flowed)));
                }
            }

            return new LedgerOutcome(LedgerOutcomeKind.Scanned, below, Batches: batches, Recording: new(MaxCarried(flowed)));
        }
        finally
        {
            Leave(running);
        }
    }

    /// <summary>
    /// Runs one read of the primary''s process and judges it by the requests which flow from it: concurrent reads of one
    /// slot overlap in time, so a read''s interval would also hold another read''s requests of the slot. A byte-range read
    /// which names a range past its batch answers <see cref="LedgerOutcomeKind.RangeRefused"/>.
    /// </summary>
    private async Task<LedgerOutcome> ReadAsync(Slot slot, Func<Ledger, CancellationToken, ValueTask<ReplicaRead>> readAsync, bool refusesRange)
    {
        var running = Enter(alone: false);
        try
        {
            var flowed = new FlowedRequests();
            CurrentCall.Value = flowed;
            var start = _recording.Now();
            var read = await ReadPrimaryAsync(slot, readAsync);
            if (refusesRange && read.TryGetValue(out ReplicaRead.OutsideBatch? _))
            {
                return new LedgerOutcome(LedgerOutcomeKind.RangeRefused, Recording: new(MaxCarried(flowed)));
            }

            var end = _recording.Now();
            byte[]? bytes = null;
            if (read.TryGetValue(out ArcBuffer payload))
            {
                using (payload)
                {
                    bytes = payload.ToArray();
                }
            }

            // The read carried the one tail its Ledger knew, to each replica it asked at most once, so its judgment rests
            // on its own requests; its interval's requests bound them.
            var requests = flowed.Snapshot();
            var reads = requests.Where(static sent => sent.Kind == ReplicaCallKind.Read).ToList();
            Assert.True(reads.Count > 0, "The read asked no replica.");
            Assert.True(reads.DistinctBy(static sent => sent.Replica).Count() == reads.Count, "The read asked a replica twice.");
            Assert.True(reads.Min(static sent => sent.Tail) == reads.Max(static sent => sent.Tail), "The read's replica reads carried different tails.");

            var carried = requests.Max(static sent => sent.Tail);
            Assert.True(
                carried <= MaxCarried(start, end, static call => call.Client == Primary),
                "The read's requests carried a tail above every primary request in its interval.");
            if (_mutation == LedgerMutation.ReadsCarryAboveTail)
            {
                carried++;
            }

            var kind = read.Case switch
            {
                ReplicaReadCase.Batch => LedgerOutcomeKind.Batch,
                ReplicaReadCase.Missing => LedgerOutcomeKind.Missing,
                ReplicaReadCase.Dropped => LedgerOutcomeKind.Dropped,
                ReplicaReadCase.Backpressure => throw new InvalidOperationException("A memory replica answered Backpressure."),
                ReplicaReadCase.OutsideBatch => throw new InvalidOperationException("A whole read answered a range past its batch."),
                _ => throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"The read answered {read.Case}.")),
            };
            return new LedgerOutcome(kind, Bytes: bytes, Recording: new(carried));
        }
        finally
        {
            Leave(running);
        }
    }

    /// <summary>
    /// Runs a read of the primary''s <see cref="Ledger"/>: on the thread pool, advancing the quorum''s fake clock past each
    /// hedge delay it arms; in a simulation, on the virtual clock, which passes each hedge delay once nothing else can run.
    /// </summary>
    private async Task<ReplicaRead> ReadPrimaryAsync(Slot slot, Func<Ledger, CancellationToken, ValueTask<ReplicaRead>> readAsync)
    {
        CurrentReadLeaderHeld.Value = _leaderSlots.Holds(slot);
#if DURABLE_STORAGE_SIMULATION_TESTS
        return await readAsync(_primary, Cancellation);
#else
        var timers = _time.Timers;
        return await _time.AdvanceUntilCompletedAsync(readAsync(_primary, Cancellation).AsTask(), LedgerQuorum.ReadHedgeDelay, timers, Cancellation);
#endif
    }

    private static long MaxCarried(FlowedRequests flowed) => flowed.Snapshot().Select(static sent => sent.Tail).DefaultIfEmpty(-1).Max();
    /// <summary>
    /// Checks the seal through the primary's <see cref="Ledger"/>, or through a fresh <see cref="Ledger"/> of another
    /// process, which knows no tail, as a reader's check does.
    /// </summary>
    public async Task<LedgerOutcome> CheckAsync(LedgerCheck request)
    {
        var running = Enter(alone: true);
        var client = request.Elsewhere ? string.Create(CultureInfo.InvariantCulture, $"process-{Interlocked.Increment(ref _processes)}") : Primary;
        var ledger = request.Elsewhere ? Open(client) : _primary;
        try
        {
            var flowed = new FlowedRequests();
            CurrentCall.Value = flowed;
            var tail = await ledger.ReadTailAsync(Cancellation);
            return new LedgerOutcome(
                LedgerOutcomeKind.Tail,
                tail.Tail.ContiguousTail.Value,
                tail.Tail.IsSealed,
                LifetimeStopped: _writer.Sequencer.IsStopped,
                Recording: new(MaxCarried(flowed)));
        }
        finally
        {
            if (request.Elsewhere)
            {
                await ledger.DisposeAsync();
            }

            Leave(running);
        }
    }

    /// <summary>
    /// Learns a contiguous tail through the primary's <see cref="Ledger"/>, or through a fresh <see cref="Ledger"/> of
    /// another process on the second replica's node, which knows no tail, as a replica node's apply loop does
    /// (ledgers.md §8.5).
    /// </summary>
    public async Task<LedgerOutcome> LearnAsync(LedgerLearn request)
    {
        var running = Enter(alone: true);
        var client = request.Elsewhere ? string.Create(CultureInfo.InvariantCulture, $"process-{Interlocked.Increment(ref _processes)}") : Primary;
        var local = request.Elsewhere ? 1 : 0;
        var ledger = request.Elsewhere ? Open(client, out _, _nodeIds[local]) : _primary;
        try
        {
            var flowed = new FlowedRequests();
            CurrentCall.Value = flowed;
            var tail = await ledger.LearnTailAsync(Cancellation);
            if (_mutation == LedgerMutation.LearnPastTail)
            {
                tail += 1;
            }

            var requests = flowed.Snapshot();
            return new LedgerOutcome(
                LedgerOutcomeKind.Learned,
                tail.Value,
                LifetimeStopped: _writer.Sequencer.IsStopped,
                Recording: new(
                    requests.Select(static sent => sent.Tail).DefaultIfEmpty(-1).Max(),
                    AskedOtherNodes: requests.Exists(sent => sent.Replica != local)));
        }
        finally
        {
            if (request.Elsewhere)
            {
                await ledger.DisposeAsync();
            }

            Leave(running);
        }
    }

    public async Task<LedgerOutcome> RecoverAsync(LedgerRecover request)
    {
        var running = Enter(alone: true);
        try
        {
            return await RecoverAloneAsync(request);
        }
        finally
        {
            Leave(running);
        }
    }

    private async Task<LedgerOutcome> RecoverAloneAsync(LedgerRecover request)
    {
        var cancellationToken = Cancellation;
        var client = request.Elsewhere ? string.Create(CultureInfo.InvariantCulture, $"process-{Interlocked.Increment(ref _processes)}") : Primary;

        // The defect recovers the writer's process through a second Ledger there, which holds no writer, and only then
        // stops the writer's lifetime.
        var stopsAfterSeal = !request.Elsewhere && _mutation == LedgerMutation.StopAfterSeal;
        var ledger = request.Elsewhere || stopsAfterSeal ? Open(client) : _primary;
        try
        {
            var knownBefore = ledger.KnownContiguousTail.Value;
            var flowed = new FlowedRequests();
            CurrentCall.Value = flowed;
            var start = _recording.Now();
            // The recovery calls its observer serially, and every call returns before RecoverAsync does.
            List<(int Replica, long Tail)> handledSeals = [];
            CurrentRecovery.Value = step =>
            {
                if (step is { Kind: RecoveryStepKind.Seal, Tail: { } tail })
                {
                    handledSeals.Add((step.Replica, tail.ContiguousTail.Value));
                }

                _sealOrder?.OnStep(step);
            };
            var outcome = await ledger.RecoverAsync(cancellationToken);
            if (stopsAfterSeal)
            {
                _writer.Sequencer.Stop(SequencerStopReason.Sealing);
            }

            var end = _recording.Now();
            var seals = Calls(start, end, call => call.Client == client && call.Kind == ReplicaCallKind.Seal).ToList();
            var firstSeal = seals.Select(static seal => seal.Call).DefaultIfEmpty(long.MaxValue).Min();
            var stoppedFirst = Interlocked.Read(ref _stoppedAt) < firstSeal;
            if (outcome.Value is not RecoveryOutcome.Recovered { End: var recovered })
            {
                // LL-I9: a recovery which meets the reclamation answers Dropped.
                return new LedgerOutcome(
                    LedgerOutcomeKind.Dropped,
                    LifetimeStopped: _writer.Sequencer.IsStopped,
                    Recording: new(MaxCarried(flowed), StoppedFirst: stoppedFirst));
            }

            var knownAfter = _mutation == LedgerMutation.RaiseTailToEnd
                ? Math.Max(ledger.KnownContiguousTail.Value, recovered.Value)
                : ledger.KnownContiguousTail.Value;
            _ = Interlocked.CompareExchange(ref _end, recovered.Value, -1);

            // LP-I6: c is the greatest of the recovery's own tail and the tails of the first N - Q + 1 seal answers it
            // handled. It may handle them in another order than the one the recording shows: the recording stamps an
            // answer before the layers above it deliver it, Task.WhenAny picks among answers already complete in its set's
            // order, and a transport may reorder them too. So the order is the observer's, and the answers it reports are
            // N - Q + 1 distinct replicas' answers, each a different recorded seal answer.
            var coverage = _nodeIds.Length - _quorum + 1;
            var received = handledSeals.Take(coverage).ToList();
            Assert.True(
                received.Count == coverage,
                string.Create(CultureInfo.InvariantCulture, $"The recovery reported {received.Count} seal answers with a tail, fewer than N - Q + 1 = {coverage}."));
            var unmatched = seals.Where(static seal => seal.Output.Tail is not null).ToList();
            foreach (var (replica, tail) in received)
            {
                Assert.True(
                    received.Count(seal => seal.Replica == replica) == 1,
                    string.Create(CultureInfo.InvariantCulture, $"The recovery reported replica {replica}'s seal answer more than once among its first {coverage}."));
                var match = unmatched.FindIndex(seal => seal.Input.Replica == replica && seal.Output.Tail?.ContiguousTail.Value == tail);
                Assert.True(
                    match >= 0,
                    string.Create(CultureInfo.InvariantCulture, $"The recovery reported a seal answer of replica {replica} with tail {tail}, which the recording holds no unmatched answer for."));
                unmatched.RemoveAt(match);
            }

            var c = Math.Max(knownBefore, received.Select(static seal => seal.Tail).DefaultIfEmpty(0).Max());
            return new LedgerOutcome(
                LedgerOutcomeKind.End,
                recovered.Value,
                LifetimeStopped: _writer.Sequencer.IsStopped,
                Recording: new(MaxCarried(flowed), knownBefore, knownAfter, c, stoppedFirst));
        }
        finally
        {
            if (!ReferenceEquals(ledger, _primary))
            {
                await ledger.DisposeAsync();
            }
        }
    }

    public LedgerOutcome Observe()
    {
        var recorded = Interlocked.Read(ref _end);
        _primary.ObserveRecordedTail(new Slot(recorded));
        if (recorded > _writer.Sequencer.CompletedTail.Value)
        {
            _coverage?.RecordObservedPastCompleted();
        }

        return new LedgerOutcome(LedgerOutcomeKind.Observed, Recording: new(KnownAfter: _primary.KnownContiguousTail.Value));
    }

    /// <summary>
    /// Reclaims the ledger's storage once the map revoked the closed ledger, as its nodes' reconciliation does: its
    /// first step, the drop on one node, which keeps the leading replica, or the drop on every node (LL-I9, LR-I7).
    /// </summary>
    public async Task<LedgerOutcome> DropAsync(LedgerDrop request)
    {
        var running = Enter(alone: true);
        try
        {
            var first = (_leader + 1) % _nodeIds.Length;
            for (var replica = 0; replica < _nodeIds.Length; replica++)
            {
                if ((request.Every || replica == first) && !_dropped[replica])
                {
                    // The skewed replica answers honestly from now on, as a dropped node does.
                    Volatile.Write(ref _dropped[replica], true);
                    await _nodes.DropAsync(_nodeIds[replica], Id);
                }
            }

            return new LedgerOutcome(LedgerOutcomeKind.Reclaimed);
        }
        finally
        {
            Leave(running);
        }
    }

    public async ValueTask DisposeAsync()
    {
        // The wrapped replicas finish their work first, so the quorum's disposal of them waits on nothing.
        if (_settle is { } settle)
        {
            await settle();
        }

        // The slow replicas' reads which lost answer once their delay passes, so the quorum's disposal does not wait on them.
#if !DURABLE_STORAGE_SIMULATION_TESTS
        _time.Advance(TimeSpan.FromHours(1));
#endif
        _coverage?.RecordHedges(_primaryQuorum.ReadHedges);
        await _writer.DisposeAsync();
        await _primary.DisposeAsync();
        await _nodes.DisposeAsync();
    }

    private async Task<LedgerOutcome> AppendAsync(byte value, bool twice)
    {
        var running = Enter(alone: false);
        try
        {
            return await AppendRunningAsync(value, twice);
        }
        finally
        {
            Leave(running);
        }
    }

    private async Task<LedgerOutcome> AppendRunningAsync(byte value, bool twice)
    {
        var flowed = new FlowedRequests();
        CurrentCall.Value = flowed;
        var start = _recording.Now();
        long[] interval = [start, long.MaxValue];
        lock (_appends)
        {
            _appends.Add(interval);
        }

        AppendOutcome outcome;
        using (var payload = ArcBuffer.Wrap(LedgerStorageSpec.Payload(value)))
        {
            if (!twice)
            {
                outcome = await _writer.AppendAsync(payload, Cancellation);
            }
            else if (await _writer.AcquireAsync(Cancellation) is { } acquired)
            {
                outcome = await _writer.AppendAsync(acquired, payload, Cancellation);
                try
                {
                    _ = await _writer.AppendAsync(acquired, payload, Cancellation);
                    return new LedgerOutcome(LedgerOutcomeKind.AppendedTwice, acquired.Value);
                }
                catch (ArgumentException)
                {
                    // ledger-protocol.md §2.3: the slot was already appended.
                }
            }
            else
            {
                outcome = new AppendOutcome(new AppendOutcome.Unresolved(Slot: null));
            }
        }

        var end = _recording.Now();
        bool overlapped;
        lock (_appends)
        {
            interval[1] = end;
            overlapped = _appends.Exists(other => !ReferenceEquals(other, interval) && other[0] < end && other[1] > start);
        }

        var (kind, slot) = outcome.Value switch
        {
            AppendOutcome.Appended appended => (LedgerOutcomeKind.Appended, appended.Slot.Value),
            AppendOutcome.Sealed sealedAt => (LedgerOutcomeKind.Sealed, sealedAt.Slot.Value),
            AppendOutcome.Unresolved unresolved => (LedgerOutcomeKind.Unresolved, unresolved.Slot?.Value ?? -1),
            _ => throw new InvalidOperationException("An append outcome holds no case."),
        };

        return new LedgerOutcome(
            kind,
            slot,
            LifetimeStopped: _writer.Sequencer.IsStopped,
            OverlappedAppend: overlapped,
            StoppedBySeal: _writer.Sequencer.Stopped is { IsCompletedSuccessfully: true, Result: SequencerStopReason.Sealing },
            Recording: new(MaxCarried(flowed)));
    }

    private Ledger Open(string client) => Open(client, out _, client == Primary ? _nodeIds[0] : null);

    /// <summary>
    /// Opens <paramref name="client"/>'s <see cref="Ledger"/> over the system's nodes, with the quorum it composes, whose
    /// every recovery forwards its steps to the observer of the recovery the current call runs, and whose preferred
    /// replica is <paramref name="localNode"/>'s.
    /// </summary>
    private Ledger Open(string client, out LedgerQuorum quorum, NodeId? localNode)
    {
        quorum = LedgerQuorum.ForNodes(
            Id,
            [.. _nodeIds],
            _nodeIds.Select((node, replica) => new AttributingReplica(
                Delivering(
                    new CallRecordingLedgerReplica(
                        Answering(
                            new SkewedLedgerReplica(Wrap(_nodes.Open(node, Id, _options)), leads: replica == _leader, _time, () => Volatile.Read(ref _answersLost), () => Volatile.Read(ref _requestsLost), () => Volatile.Read(ref _dropped[replica]), _coverage, _leaderSlots, slot => CurrentReadLeaderHeld.Value ?? _leaderSlots.Holds(slot)),
                            replica),
                        client,
                        replica,
                        _recording),
                    replica),
                replica)),
            _quorum,
            _options,
            _time,
            localNode,
            static step => CurrentRecovery.Value?.Invoke(step));
        return new Ledger(Id, quorum, new LedgerLocation.Nodes([.. _nodeIds]));
    }

    /// <summary>
    /// Wraps a memory replica in the wrapper the system was given, which the quorum disposes with its replicas.
    /// </summary>
    private ILedgerReplica Wrap(ILedgerReplica replica) => _wrap?.Invoke(replica) ?? replica;

    private ILedgerReplica Answering(ILedgerReplica inner, int replica) => _sealOrder?.Answering(inner, replica) ?? inner;

    private ILedgerReplica Delivering(ILedgerReplica inner, int replica) => _sealOrder?.Delivering(inner, replica) ?? inner;

    /// <summary>
    /// Counts a harness operation in flight, and fails when an exclusive operation (a check, a learn, a recovery, a drop,
    /// or an acquisition) overlaps another operation.
    /// </summary>
    private int Enter(bool alone)
    {
        var weight = alone ? Alone : 1;
        var before = Interlocked.Add(ref _running, weight) - weight;
        Assert.True(alone ? before == 0 : before < Alone, "An exclusive harness operation overlapped another operation.");
        return weight;
    }

    private void Leave(int weight) => Interlocked.Add(ref _running, -weight);

    private IEnumerable<HistoryOperation<ReplicaCall, ReplicaAnswer>> Calls(long start, long end, Func<ReplicaCall, bool> select) =>
        _recording.History(static _ => ReplicaAnswer.Pending).Where(operation => operation.Call > start && operation.Call < end && select(operation.Input));

    private long MaxCarried(long start, long end, Func<ReplicaCall, bool> select) =>
        Calls(start, end, select).Select(static operation => operation.Input.CarriedTail).DefaultIfEmpty(-1).Max();

    /// <summary>The replica requests which flowed from one harness call: each one's kind, replica, and carried tail.</summary>
    private sealed class FlowedRequests
    {
        private readonly Lock _lock = new();
        private readonly List<(ReplicaCallKind Kind, int Replica, long Tail)> _requests = [];

        public void Add(ReplicaCallKind kind, int replica, Slot tail)
        {
            lock (_lock)
            {
                _requests.Add((kind, replica, tail.Value));
            }
        }

        public List<(ReplicaCallKind Kind, int Replica, long Tail)> Snapshot()
        {
            lock (_lock)
            {
                return [.. _requests];
            }
        }
    }

    /// <summary>A replica which records each request in the <see cref="CurrentCall"/> it flows from, if any.</summary>
    private sealed class AttributingReplica(ILedgerReplica inner, int replica) : ILedgerReplica
    {
        public ValueTask<ReplicaWriteOutcome> WriteAsync(
            Slot slot, ArcBuffer payload, Slot contiguousTail, bool recovery, CancellationToken cancellationToken = default)
        {
            CurrentCall.Value?.Add(recovery ? ReplicaCallKind.RecoveryWrite : ReplicaCallKind.Write, replica, contiguousTail);
            return inner.WriteAsync(slot, payload, contiguousTail, recovery, cancellationToken);
        }

        public ValueTask<ReplicaRead> ReadAsync(Slot slot, Slot contiguousTail, CancellationToken cancellationToken = default)
        {
            CurrentCall.Value?.Add(ReplicaCallKind.Read, replica, contiguousTail);
            return inner.ReadAsync(slot, contiguousTail, cancellationToken);
        }

        public ValueTask<ReplicaRead> ReadAsync(
            Slot slot, int offset, int length, Slot contiguousTail, CancellationToken cancellationToken = default)
        {
            CurrentCall.Value?.Add(ReplicaCallKind.Read, replica, contiguousTail);
            return inner.ReadAsync(slot, offset, length, contiguousTail, cancellationToken);
        }

        public ValueTask<ReplicaTailRead> ReadTailAsync(Slot contiguousTail, bool seal, CancellationToken cancellationToken = default)
        {
            CurrentCall.Value?.Add(seal ? ReplicaCallKind.Seal : ReplicaCallKind.ReadTail, replica, contiguousTail);
            return inner.ReadTailAsync(contiguousTail, seal, cancellationToken);
        }

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
