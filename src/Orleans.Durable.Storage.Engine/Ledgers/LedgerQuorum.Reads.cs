using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Orleans.Durable.Storage.Engine.Buffers;

namespace Orleans.Durable.Storage.Engine.Ledgers;

/// <summary>
/// The quorum's reads (ledger-protocol.md §8.3): a read below the known contiguous tail asks one replica at a time and
/// hedges a slow one within a budget; a read at or above it asks every replica.
/// </summary>
internal sealed partial class LedgerQuorum
{
    /// <summary>
    /// How long a read below the known contiguous tail waits on the replicas it asked before it hedges them with one more
    /// (ledger-protocol.md §8.4). The wait spans the whole replica call, its transport's queue included, and the hedge
    /// budget bounds what the hedges cost.
    /// </summary>
    internal static readonly TimeSpan ReadHedgeDelay = TimeSpan.FromMilliseconds(50);

    // The hedge budget counts tenths of a hedge (ledger-protocol.md §8.4): each read below the tail deposits one tenth, a
    // hedge spends a whole one, and the bucket holds at most ten hedges, so hedges stay near a tenth of those reads.
    private const int HedgeCost = 10;
    private const int HedgeBudgetCapacity = 10 * HedgeCost;

    // The index of the replica a read below the known contiguous tail asks first, or null when the quorum has none.
    private readonly int? _preferredReplica;

    // The slots whose repairs are in flight, at most K (ledger-protocol.md §8.3).
    private readonly ConcurrentDictionary<long, byte> _repairing = new();
    private int _repairsInFlight;
    private int _hedgeBudget = HedgeBudgetCapacity;
    private uint _readRotation;
    private long _readHedges;
    private long _readHedgeBudgetExhaustions;

    /// <summary>
    /// Gets the number of hedges the quorum's reads below the known contiguous tail sent, which a test reads to show that
    /// its run hedged.
    /// </summary>
    internal long ReadHedges => Interlocked.Read(ref _readHedges);

    /// <summary>
    /// Gets the number of reads below the known contiguous tail which met an empty hedge budget and waited instead.
    /// </summary>
    internal long ReadHedgeBudgetExhaustions => Interlocked.Read(ref _readHedgeBudgetExhaustions);

    /// <summary>
    /// Gets or sets a test seam told, each time a read's hedge delay passes with no answer, whether the read hedged or
    /// met an empty budget.
    /// </summary>
    internal Action<bool>? HedgeDecided { get; set; }

    /// <summary>
    /// Reads one slot, reporting whether it holds a batch, carrying <paramref name="contiguousTail"/>.
    /// </summary>
    /// <param name="slot">The slot.</param>
    /// <param name="contiguousTail">The ledger's known contiguous tail (LP-I11).</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>
    /// The batch, which the caller owns and disposes; <see cref="ReplicaRead.Missing"/>, only at or above
    /// <paramref name="contiguousTail"/>; or, when it cannot decide, <see cref="ReplicaWriteOutcome.Dropped"/> when a
    /// replica answered it (LP-I5) or <see cref="ReplicaWriteOutcome.Backpressure"/> when a replica answered it
    /// (ledger-protocol.md §6.4).
    /// </returns>
    /// <remarks>
    /// Below the contiguous tail the slot is decided, so one replica's batch answers it and the read never answers
    /// <see cref="ReplicaRead.Missing"/> (LP-I10); a batch found past a preferred replica which lacked it is copied there
    /// (read repair). At or above it, <see cref="ReplicaRead.Missing"/> takes <c>Q</c> answers, and neither result decides
    /// the slot (ledger-protocol.md §8.3).
    /// </remarks>
    public ValueTask<ReplicaRead> ReadAsync(Slot slot, Slot contiguousTail, CancellationToken cancellationToken = default) =>
        ReadAsync(new ReadTarget(slot, Offset: 0, Length: 0), contiguousTail, cancellationToken);

    /// <summary>
    /// Reads bytes <c>[offset, offset + length)</c> of the batch at one slot, a byte-range read, or reports its other
    /// answer, as <see cref="ReadAsync(Slot, Slot, CancellationToken)"/> does, forwarding the range to each replica it asks
    /// so only the range crosses the network; it repairs nothing (ledger-protocol.md §8.3).
    /// </summary>
    /// <param name="slot">The slot.</param>
    /// <param name="offset">The first byte of the range; at least zero.</param>
    /// <param name="length">The number of bytes in the range; positive.</param>
    /// <param name="contiguousTail">The ledger's known contiguous tail (LP-I11).</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>
    /// The range, which the caller owns and disposes; <see cref="ReplicaRead.OutsideBatch"/> once a replica reports that the
    /// range extends past the batch, which is the same on every replica (LP-I1, LP-I10); or the read's other answer.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="offset"/> is negative or <paramref name="length"/> is not positive.
    /// </exception>
    public ValueTask<ReplicaRead> ReadAsync(
        Slot slot,
        int offset,
        int length,
        Slot contiguousTail,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        return ReadAsync(new ReadTarget(slot, offset, length), contiguousTail, cancellationToken);
    }

    /// <inheritdoc/>
    public IAsyncEnumerable<ReplicaRead> ReadRangeAsync(
        Slot from,
        Slot below,
        Slot acknowledged,
        SemaphoreSlim? window = null,
        CancellationToken cancellationToken = default) =>
        LedgerRangeReads.ReadPointWiseAsync(this, from, below, acknowledged, LedgerReader.DefaultMaxReadAheadBytes, window, onReadIssued: null, cancellationToken);

    private ValueTask<ReplicaRead> ReadAsync(ReadTarget target, Slot contiguousTail, CancellationToken cancellationToken) =>
        target.Slot < contiguousTail
            ? ReadDecidedAsync(target, contiguousTail, cancellationToken)
            : ReadUndecidedAsync(target, contiguousTail, cancellationToken);

    /// <summary>
    /// Reads a decided slot one replica at a time (LP-I10, ledger-protocol.md §8.3). While one replica is pending it waits
    /// on that replica alone, up to <see cref="ReadHedgeDelay"/>, and asks the next replica at once on an answer without
    /// a batch; a replica which outlasts the delay is hedged (<see cref="ReadHedgedAsync"/>) when the budget allows. It
    /// asks every replica but the preferred one under a token linked to the caller's and to the quorum's lifetime, created
    /// at its first such ask, and cancels it once it decides, so a remote read which lost leaves its transport's queue
    /// and a stop of the quorum's reads (<see cref="StopReadsAsync"/>) ends it. The local, preferred replica is read under
    /// the lifetime's own token, which a stop cancels and which the caller's cancellation would only have ended the wait
    /// of (orleans.md §5.12), while the read waits on it under the caller's token: so a stop ends the preferred call
    /// itself, and a read which the preferred replica answers creates no token source; one it answers at once registers on
    /// no token, and one it answers later registers only its wait, on the caller's token. A whole read which takes its
    /// batch from another replica after the preferred one answered missing or corrupt repairs the preferred one
    /// (<see cref="Repair"/>).
    /// </summary>
    private async ValueTask<ReplicaRead> ReadDecidedAsync(ReadTarget target, Slot contiguousTail, CancellationToken cancellationToken)
    {
        using var admission = Admit();
        LedgerReplicaOperations.ValidateSlot(target.Slot);
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfReadsStopped();
        DepositHedgeBudget();
        var answers = default(NoBatchAnswers);
        var replica = FirstReplicaToRead();
        var hedgeBudgetExhausted = false;
        var preferredLacks = false;
        CancellationTokenSource? losers = null;
        try
        {
            for (var asked = 1; ; asked++)
            {
                var asking = replica;
                var preferred = asking == _preferredReplica;
                var reading = ReadReplica(asking, target, contiguousTail, preferred ? _lifetime.Token : (losers ??= LinkToLifetime(cancellationToken)).Token);
                replica = NextReplica(replica);
                ReplicaRead answer;
                if (reading.IsCompletedSuccessfully)
                {
                    answer = await reading.ConfigureAwait(false);
                }
                else
                {
                    var read = reading.AsTask();
                    var unasked = asked < _replicas.Length;
                    if (!read.IsCompleted && unasked && !hedgeBudgetExhausted)
                    {
                        // A wait on the preferred replica alone takes the caller's token: a stop ends the replica's call.
                        await ((Task)read).WaitAsync(ReadHedgeDelay, _timeProvider, losers?.Token ?? cancellationToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                        if (!read.IsCompleted && !cancellationToken.IsCancellationRequested && !_lifetime.IsCancellationRequested)
                        {
                            if (TrySpendHedge())
                            {
                                losers ??= LinkToLifetime(cancellationToken);
                                var pendingPreferred = preferred ? read : null;
                                return await ReadHedgedAsync(read, pendingPreferred, preferredLacks, replica, asked, answers, losers, target, contiguousTail).ConfigureAwait(false);
                            }

                            hedgeBudgetExhausted = true;
                            RecordHedgeBudgetExhausted();
                        }
                    }

                    var token = losers?.Token ?? cancellationToken;
                    if (!read.IsCompleted)
                    {
                        await ((Task)read).WaitAsync(token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                    }

                    if (!read.IsCompleted)
                    {
                        _ = TrackStragglers([read]);
                        token.ThrowIfCancellationRequested();
                    }

                    ThrowIfStoppedCall(read);
                    preferredLacks |= preferred && IsCorrupt(read);
                    if (!answers.TryTake(read, token, out answer))
                    {
                        if (asked == _replicas.Length)
                        {
                            return answers.Decide(this, target.Slot, contiguousTail, decided: true);
                        }

                        continue;
                    }
                }

                switch (answer)
                {
                    case ArcBuffer batch:
                        if (preferredLacks)
                        {
                            Repair(target, batch, contiguousTail);
                        }

                        return answer;
                    case ReplicaWriteOutcome.Dropped:
                        // LP-I5: a drop is definite and permanent, and §6.4 answers it first.
                        return answer;
                    case ReplicaRead.OutsideBatch:
                        // The slot holds one batch, the same on every replica (LP-I1, LP-I10), which the range passes.
                        return answer;
                    case ReplicaRead.Missing or ReplicaWriteOutcome.Backpressure:
                        // A replica which missed the write, or was too busy, may lack a decided slot's batch: ask the next.
                        preferredLacks |= preferred && answer is ReplicaRead.Missing;
                        answers.Record(answer);
                        if (asked == _replicas.Length)
                        {
                            return answers.Decide(this, target.Slot, contiguousTail, decided: true);
                        }

                        break;
                    case null:
                    default:
                        throw new UnreachableException("conventions.md §15: a replica answers a union which holds a case.");
                }
            }
        }
        catch (OperationCanceledException) when (ReadsStopped(cancellationToken))
        {
            throw ReadsStoppedException();
        }
        finally
        {
            // Disposed once cancelled: a cancelled source keeps answering its token (a registration runs at once).
            losers?.Cancel();
            losers?.Dispose();
        }
    }

    /// <summary>
    /// Continues a decided slot's read once <paramref name="slow"/> outlasted the hedge delay: asks one more replica, the
    /// hedge, and from then on asks the next replica on each answer without a batch, and hedges again only while no
    /// hedge it sent is pending, the delay has passed since its last ask, and the budget allows (ledger-protocol.md §8.4).
    /// Every replica it asks runs under <paramref name="losers"/>, linked to the caller's token and to the quorum's
    /// lifetime, on which it also waits, and which it cancels once the read decides, so the reads which lost stop where
    /// their transport can stop them. It repairs the preferred replica, whose read is <paramref name="preferredRead"/>
    /// when still pending, once that replica answered missing or corrupt before another's batch decided the read.
    /// </summary>
    private async ValueTask<ReplicaRead> ReadHedgedAsync(
        Task<ReplicaRead> slow,
        Task<ReplicaRead>? preferredRead,
        bool preferredLacks,
        int replica,
        int asked,
        NoBatchAnswers answers,
        CancellationTokenSource losers,
        ReadTarget target,
        Slot contiguousTail)
    {
        var cancellationToken = losers.Token;
        var pending = new List<Task<ReplicaRead>>(_replicas.Length) { slow };
        var hedgeBudgetExhausted = false;
        long lastAsk = 0;
        RecordHedge();
        HedgeDecided?.Invoke(true);
        var hedge = AskNextReplica();
        try
        {
            while (true)
            {
                if (pending.Count == 0)
                {
                    return answers.Decide(this, target.Slot, contiguousTail, decided: true);
                }

                var first = Task.WhenAny(pending);
                if (hedge is not null || asked == _replicas.Length || hedgeBudgetExhausted)
                {
                    await ((Task)first).WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                }
                else if (ReadHedgeDelay - _timeProvider.GetElapsedTime(lastAsk) is var remaining && remaining > TimeSpan.Zero)
                {
                    await ((Task)first).WaitAsync(remaining, _timeProvider, cancellationToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                }

                cancellationToken.ThrowIfCancellationRequested();
                if (!first.IsCompleted)
                {
                    if (TrySpendHedge())
                    {
                        RecordHedge();
                        HedgeDecided?.Invoke(true);
                        hedge = AskNextReplica();
                    }
                    else
                    {
                        hedgeBudgetExhausted = true;
                        RecordHedgeBudgetExhausted();
                    }

                    continue;
                }

                var completed = await first.ConfigureAwait(false);
                _ = pending.Remove(completed);
                if (completed == hedge)
                {
                    hedge = null;
                }

                ThrowIfStoppedCall(completed);
                var fromPreferred = completed == preferredRead;
                preferredLacks |= fromPreferred && IsCorrupt(completed);
                if (answers.TryTake(completed, cancellationToken, out var answer))
                {
                    switch (answer)
                    {
                        case ArcBuffer batch:
                            if (preferredLacks)
                            {
                                Repair(target, batch, contiguousTail);
                            }

                            return answer;
                        case ReplicaWriteOutcome.Dropped or ReplicaRead.OutsideBatch:
                            return answer;
                        case ReplicaRead.Missing or ReplicaWriteOutcome.Backpressure:
                            preferredLacks |= fromPreferred && answer is ReplicaRead.Missing;
                            answers.Record(answer);
                            break;
                        case null:
                        default:
                            throw new UnreachableException("conventions.md §15: a replica answers a union which holds a case.");
                    }
                }

                if (asked < _replicas.Length)
                {
                    _ = AskNextReplica();
                }
            }
        }
        finally
        {
            losers.Cancel();
            _ = TrackStragglers(pending);
        }

        Task<ReplicaRead> AskNextReplica()
        {
            var read = ReadReplica(replica, target, contiguousTail, losers.Token).AsTask();
            replica = NextReplica(replica);
            asked++;
            lastAsk = _timeProvider.GetTimestamp();
            pending.Add(read);
            return read;
        }
    }

    /// <summary>
    /// Reads a slot at or above the known contiguous tail from every replica at once: the first batch any reports, or
    /// <see cref="ReplicaRead.Missing"/> once <c>Q</c> replicas report it (ledger-protocol.md §8.3).
    /// </summary>
    private async ValueTask<ReplicaRead> ReadUndecidedAsync(ReadTarget target, Slot contiguousTail, CancellationToken cancellationToken)
    {
        using var admission = Admit();
        LedgerReplicaOperations.ValidateSlot(target.Slot);
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfReadsStopped();
        var pending = new List<Task<ReplicaRead>>(_replicas.Length);
        var answers = default(NoBatchAnswers);
        using var scope = new CallScope(cancellationToken, _lifetime.Token);
        var token = scope.Token;
        try
        {
            for (var replica = 0; replica < _replicas.Length; replica++)
            {
                pending.Add(ReadReplica(replica, target, contiguousTail, token).AsTask());
            }

            while (pending.Count > 0)
            {
                var completed = await Task.WhenAny(pending).WaitAsync(token).ConfigureAwait(false);
                _ = pending.Remove(completed);
                if (!answers.TryTake(completed, token, out var read))
                {
                    continue;
                }

                if (read.TryGetValue(out ArcBuffer _) || read.TryGetValue(out ReplicaRead.OutsideBatch? _))
                {
                    return read;
                }

                answers.Record(read);
                if (answers.Missing >= Quorum)
                {
                    return ReplicaRead.Missing.Instance;
                }
            }
        }
        catch (OperationCanceledException) when (ReadsStopped(cancellationToken))
        {
            throw ReadsStoppedException();
        }
        finally
        {
            scope.Outlive(TrackStragglers(pending));
        }

        return answers.Decide(this, target.Slot, contiguousTail, decided: false);
    }

    /// <summary>
    /// Returns the index of the replica a read below the known contiguous tail asks first: the preferred replica, or the
    /// next in a rotation over the replicas (ledger-protocol.md §8.4). The rotation holds a replica's index, so it never
    /// wraps unevenly as a counter would.
    /// </summary>
    private int FirstReplicaToRead()
    {
        if (_preferredReplica is { } preferred)
        {
            return preferred;
        }

        var current = Volatile.Read(ref _readRotation);
        while (true)
        {
            var next = (uint)NextReplica((int)current);
            var seen = Interlocked.CompareExchange(ref _readRotation, next, current);
            if (seen == current)
            {
                return (int)next;
            }

            current = seen;
        }
    }

    /// <summary>
    /// Throws <see cref="OperationCanceledException"/> for a replica call which did not answer once the quorum's reads
    /// stopped: the stop may have ended it, and a call the stop ended is no replica's failure. The decided read's catch
    /// answers <see cref="ObjectDisposedException"/> while the caller's token stands. Checked before the call's failure is
    /// recorded on both of the read's paths, since the lifetime may end the preferred call before it cancels the read's
    /// linked token, whose registration on the lifetime may be older (callbacks run newest first).
    /// </summary>
    private void ThrowIfStoppedCall(Task completed)
    {
        if (!completed.IsCompletedSuccessfully)
        {
            _lifetime.Token.ThrowIfCancellationRequested();
        }
    }

    /// <summary>Returns the replica after <paramref name="replica"/> in the map's order, wrapping around.</summary>
    private int NextReplica(int replica) => replica + 1 == _replicas.Length ? 0 : replica + 1;

    /// <summary>
    /// Reads one replica, so that a synchronous failure, like an asynchronous one, faults the returned operation; a ranged
    /// read forwards its range, so only the range crosses the network.
    /// </summary>
    private ValueTask<ReplicaRead> ReadReplica(int replica, ReadTarget target, Slot contiguousTail, CancellationToken cancellationToken)
    {
        try
        {
            return target.IsByteRange
                ? _replicas[replica].ReadAsync(target.Slot, target.Offset, target.Length, contiguousTail, cancellationToken)
                : _replicas[replica].ReadAsync(target.Slot, contiguousTail, cancellationToken);
        }
#pragma warning disable CA1031 // Any synchronous replica failure must participate in the read's accounting.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            return ValueTask.FromException<ReplicaRead>(exception);
        }
    }

    /// <summary>Returns whether a replica's read failed because the replica's copy fails its integrity check (LR-I2).</summary>
    private static bool IsCorrupt(Task<ReplicaRead> completed) => completed.Exception?.InnerException is LedgerReplicaCorruptException;

    /// <summary>
    /// Copies the batch a whole read below the known contiguous tail found to its preferred replica, which answered missing
    /// or corrupt, with a recovery write carrying <paramref name="contiguousTail"/> (read repair, LP-I7, LP-I10). The slot
    /// is decided, so the batch is its one value, which a recovery write keeps where it exists and writes where it is
    /// missing or corrupt (LR-I5): the repair restores the slot's redundancy on the replica the reader's node holds. A
    /// recovery write keeps whatever it stores for the ledger's life, so the repair sends only bytes the quorum's batch
    /// verifier accepts, and with no verifier it repairs nothing: bytes damaged after the replica which sent them checked
    /// them would otherwise become a lasting bad copy. It keeps at most <c>K</c> repairs in flight and one per slot, since
    /// skipping a repair is always safe. It runs as a straggler under the quorum's lifetime, retaining its own view of the
    /// batch, and its answer asserts nothing: a preferred replica it misses lacks the slot until a later read repairs it.
    /// A byte-range read holds only its range, so it repairs nothing.
    /// </summary>
    private void Repair(ReadTarget target, ArcBuffer batch, Slot contiguousTail)
    {
        if (target.IsByteRange || _preferredReplica is not { } preferred || _verifyBatch is not { } verify || !verify(batch))
        {
            return;
        }

        if (Interlocked.Increment(ref _repairsInFlight) > WindowSize || !_repairing.TryAdd(target.Slot.Value, 0))
        {
            _ = Interlocked.Decrement(ref _repairsInFlight);
            return;
        }

        _ = TrackStragglers([RepairAsync(_replicas[preferred], target.Slot, batch.Retain(), contiguousTail, _lifetime.Token)]);

        async Task<ReplicaWriteOutcome> RepairAsync(ILedgerReplica replica, Slot slot, ArcBuffer copy, Slot tail, CancellationToken cancellationToken)
        {
            try
            {
                using (copy)
                {
                    return await replica.WriteAsync(slot, copy, tail, recovery: true, cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                _ = _repairing.TryRemove(slot.Value, out _);
                _ = Interlocked.Decrement(ref _repairsInFlight);
            }
        }
    }

    /// <summary>Deposits a read's tenth of a hedge into the budget, up to its capacity.</summary>
    private void DepositHedgeBudget()
    {
        var budget = Volatile.Read(ref _hedgeBudget);
        while (budget < HedgeBudgetCapacity)
        {
            var seen = Interlocked.CompareExchange(ref _hedgeBudget, budget + 1, budget);
            if (seen == budget)
            {
                return;
            }

            budget = seen;
        }
    }

    /// <summary>Spends a hedge from the budget, returning whether it held one.</summary>
    private bool TrySpendHedge()
    {
        var budget = Volatile.Read(ref _hedgeBudget);
        while (budget >= HedgeCost)
        {
            var seen = Interlocked.CompareExchange(ref _hedgeBudget, budget - HedgeCost, budget);
            if (seen == budget)
            {
                return true;
            }

            budget = seen;
        }

        return false;
    }

    private void RecordHedge()
    {
        _ = Interlocked.Increment(ref _readHedges);
        LedgerReadMetrics.RecordHedge();
    }

    private void RecordHedgeBudgetExhausted()
    {
        _ = Interlocked.Increment(ref _readHedgeBudgetExhaustions);
        LedgerReadMetrics.RecordHedgeBudgetExhausted();
        HedgeDecided?.Invoke(false);
    }

    /// <summary>
    /// The answers of a read's replicas which hold no batch, and their failures, which ledger-protocol.md §6.4's rule
    /// decides once no replica is left to ask.
    /// </summary>
    private struct NoBatchAnswers
    {
        private bool _dropped;
        private bool _backpressure;
        private List<Exception>? _errors;
        private List<Exception>? _permanentErrors;

        /// <summary>Gets the number of replicas which answered <see cref="ReplicaRead.Missing"/>.</summary>
        internal int Missing { get; private set; }

        /// <summary>
        /// Takes a completed replica read's answer, or records its failure and returns <see langword="false"/>. A failure
        /// the caller's own cancellation caused rethrows it.
        /// </summary>
        internal bool TryTake(Task<ReplicaRead> completed, CancellationToken cancellationToken, out ReplicaRead answer)
        {
            if (completed.IsCompletedSuccessfully)
            {
                answer = completed.Result;
                return true;
            }

            // Checked before the failure is recorded, so a cancellation recorded is the replica's own, such as a
            // transport's timeout.
            cancellationToken.ThrowIfCancellationRequested();
            AddFailure(completed, _errors ??= [], ref _permanentErrors);
            answer = default;
            return false;
        }

        /// <summary>Records an answer which holds no batch.</summary>
        internal void Record(ReplicaRead answer)
        {
            switch (answer)
            {
                case ReplicaRead.Missing:
                    Missing++;
                    break;
                case ReplicaWriteOutcome.Dropped:
                    _dropped = true;
                    break;
                case ReplicaWriteOutcome.Backpressure:
                    _backpressure = true;
                    break;
                case ArcBuffer or ReplicaRead.OutsideBatch:
                case null:
                default:
                    throw new UnreachableException("A batch, or a range past it, is the read's answer, never an answer without one.");
            }
        }

        /// <summary>
        /// Decides a read which asked every replica and found no batch, by ledger-protocol.md §6.4's rule: <c>Dropped</c>
        /// when a replica answered it (LP-I5); the failures repetition cannot change once <c>N − Q + 1</c> replicas failed
        /// so; <c>Backpressure</c> when a replica answered it; below the tail, with every replica answering
        /// <c>Missing</c>, <see cref="InvalidDataException"/> (LP-I10); and otherwise every failure.
        /// </summary>
        internal readonly ReplicaRead Decide(LedgerQuorum quorum, Slot slot, Slot contiguousTail, bool decided)
        {
            if (_dropped)
            {
                return ReplicaWriteOutcome.Dropped.Instance;
            }

            _ = quorum.NoQuorum("read", _permanentErrors, pending: 0);
            if (_backpressure)
            {
                return ReplicaWriteOutcome.Backpressure.Instance;
            }

            if (decided && _errors is null)
            {
                // LP-I10: a decided slot was written on Q replicas, at most Q - 1 of which a failure may lose.
                throw new InvalidDataException(string.Create(
                    CultureInfo.InvariantCulture,
                    $"Every replica reports slot {slot} missing, below the contiguous tail {contiguousTail}: the caller carried a tail above the ledger's, or more than Q - 1 replicas lost the slot."));
            }

            throw Unavailable("read", _errors ?? []);
        }
    }

    /// <summary>A read's slot and, for a byte-range read, its range: a whole read's <see cref="Length"/> is zero.</summary>
    private readonly record struct ReadTarget(Slot Slot, int Offset, int Length)
    {
        internal bool IsByteRange => Length > 0;
    }
}
