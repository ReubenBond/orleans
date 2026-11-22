using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.ExceptionServices;
using Orleans.Durable.Storage.Engine.Buffers;
using Polly;

namespace Orleans.Durable.Storage.Engine.Ledgers;

internal sealed partial class LedgerQuorum
{
    /// <summary>
    /// The most DATA a recovery buffers by default: the values it found and has not yet copied to <c>Q</c> replicas
    /// (ledger-protocol.md §8.3).
    /// </summary>
    internal const long DefaultRecoveryByteBudget = 16L * 1024 * 1024;

    /// <summary>
    /// Gets the most DATA a recovery buffers: once the values it found and has not yet copied to <c>Q</c> replicas reach
    /// it, the recovery reads no further position until copies release some.
    /// </summary>
    internal long RecoveryByteBudget { get; init; } = DefaultRecoveryByteBudget;

    /// <summary>
    /// Seals the ledger's replicas and recovers its candidate end, BookKeeper's ledger recovery (LP-I6, LP-I7,
    /// ledger-protocol.md §8.3), calling the quorum's recovery observer, when it has one, with each replica answer it
    /// handles, in the order it handles them, before it acts on the answer; deterministic simulation logs them for trace
    /// validation (docs/spec/verification.md §5.1).
    /// </summary>
    /// <param name="contiguousTail">The ledger's known contiguous tail (LP-I11).</param>
    /// <param name="cancellationToken">Stops the recovery; the seals and recovery writes it sent may still land.</param>
    /// <returns>
    /// The recovery's <c>c</c> and its candidate end: every slot below the end holds a batch on <c>Q</c> replicas, and no
    /// writer write at it ever reaches <c>Q</c> written answers. The end is a candidate until the ledger map's conditional
    /// write records it as the ledger's <c>T</c>; concurrent recoveries may return different ends. Or
    /// <see cref="ReplicaWriteOutcome.Dropped"/> once a replica answers a seal, a read, or a recovery write <c>Dropped</c>:
    /// the ledger has left the map, so no close can apply (LP-I5).
    /// </returns>
    /// <remarks>
    /// <para>
    /// It seals every replica and takes <c>c</c>, the greatest contiguous tail among the first <c>N − Q + 1</c> seal
    /// answers and <paramref name="contiguousTail"/>, which it returns. It then reads slots from <c>c</c>,
    /// at most <c>K</c> slots ahead of the next undecided one and while the batches it holds stay within
    /// <see cref="RecoveryByteBudget"/>, deciding them in order: a slot some replica reports a batch at is found, and
    /// copied with recovery writes carrying <c>c</c> to the replicas which do not hold it until <c>Q</c> do; a slot which
    /// <c>N − Q + 1</c> replicas report missing, each to a read sent after the recovery held that replica's seal answer,
    /// is the candidate end.
    /// </para>
    /// <para>
    /// A replica which is down, slow, or saturated delays the recovery but never fails it: its seal, reads, and recovery
    /// writes are repeated with bounded, jittered backoff after a transient failure or a <c>Backpressure</c> answer until
    /// they answer otherwise, and a read which preceded the recovery's handling of its replica's seal answer is repeated
    /// once the recovery handles it, so the recovery ends once <c>max(Q, N − Q + 1)</c> replicas answer
    /// (ledger-protocol.md §6.5). A replica whose failure repetition cannot change takes no further part, and a corrupt
    /// batch counts as neither a batch nor absence at its slot (LP-I7). It fails with the
    /// <see cref="ArgumentException"/> a replica raised, such as a window it refuses; with
    /// <see cref="InvalidDataException"/> when it finds a batch at or above <c>c + 2K</c>, which breaks the window
    /// discipline; with <see cref="AggregateException"/>, holding the replicas' failures, when too few replicas remain able
    /// to decide a slot or hold its copies; and with <see cref="ObjectDisposedException"/> when the quorum is disposed,
    /// which stops it.
    /// </para>
    /// <para>
    /// "It then starts reading forward in the ledger, one entry at a time, replicating all entries it sees to the entire
    /// write quorum for that entry." (Apache BookKeeper, <i>The BookKeeper protocol</i>, "Closing a ledger as a reader".)
    /// </para>
    /// </remarks>
    public async ValueTask<LedgerRecoveryOutcome> RecoverAsync(Slot contiguousTail, CancellationToken cancellationToken = default)
    {
        using var admission = Admit();
        cancellationToken.ThrowIfCancellationRequested();
        using var recovery = new Recovery(this, contiguousTail, _recoveryObserver, cancellationToken, _lifetime.Token);
        return await recovery.RunAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Gets the retry pipelines of recovery's replica operations, which the quorum builds at its first recovery, since
    /// most ledgers a process opens never recover.
    /// </summary>
    private RecoveryRetries Retries => Volatile.Read(ref _recoveryRetries) ?? CreateRetries();

    private RecoveryRetries CreateRetries()
    {
        var created = new RecoveryRetries(_timeProvider);
        return Interlocked.CompareExchange(ref _recoveryRetries, created, null) ?? created;
    }

    /// <summary>
    /// The retry pipelines of a recovery's seals, reads, and recovery writes (<see cref="ReplicaRetry"/>), each of which
    /// repeats a <see cref="ReplicaWriteOutcome.Backpressure"/> answer.
    /// </summary>
    private sealed class RecoveryRetries(TimeProvider timeProvider)
    {
        public ResiliencePipeline<ReplicaTailRead> Seal { get; } =
            ReplicaRetry.Create<ReplicaTailRead>(timeProvider, static answer => answer.TryGetValue(out ReplicaWriteOutcome.Backpressure? _));

        public ResiliencePipeline<(ReplicaRead Answer, bool AfterSeal)> Read { get; } =
            ReplicaRetry.Create<(ReplicaRead Answer, bool AfterSeal)>(timeProvider, static read => read.Answer.TryGetValue(out ReplicaWriteOutcome.Backpressure? _));

        public ResiliencePipeline<ReplicaWriteOutcome> Copy { get; } =
            ReplicaRetry.Create<ReplicaWriteOutcome>(timeProvider, static answer => answer is ReplicaWriteOutcome.Backpressure);
    }

    /// <summary>
    /// One recovery's state, driven by one loop over the answers of the replica operations it sends.
    /// </summary>
    private sealed class Recovery(
        LedgerQuorum quorum, Slot contiguousTail, Action<RecoveryStep>? observer, CancellationToken cancellationToken, CancellationToken lifetime)
        : IDisposable
    {
        [SuppressMessage("Usage", "CA2213", Justification = "The recovery borrows the quorum which runs it; the quorum's owner disposes it.")]
        private readonly LedgerQuorum _quorum = quorum;
        private readonly RecoveryRetries _retries = quorum.Retries;
        private readonly CancellationTokenSource _stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime);
        private readonly HashSet<Task<Event>> _inFlight = [];
        private readonly SortedDictionary<long, Position> _positions = [];
        private readonly int[] _sealAnswered = new int[quorum._replicas.Length];
        private readonly bool[] _dead = new bool[quorum._replicas.Length];
        private readonly List<long>[] _awaitingSeal = [.. Enumerable.Range(0, quorum._replicas.Length).Select(static _ => new List<long>())];
        private readonly List<Exception> _errors = [];
        private readonly long _recoveryStartTimestamp = quorum._timeProvider.GetTimestamp();
        private long _leaseDeadlineTicks;
        private int _sealAnswers;
        private Slot _start;
        private bool _reading;
        private long _next;
        private long _issued;
        private long _bufferedBytes;
        private long? _end;

        internal abstract record Event(int Replica);

        internal sealed record SealEvent(int Replica, ReplicaTailRead Answer, Exception? Failure) : Event(Replica);

        /// <summary>
        /// A read's answer, its failure, or neither once the recovery stopped reading the position; a failed or stopped
        /// read holds no answer. A missing answer counts toward the end only when the read was sent after the replica's
        /// seal answer. <paramref name="VerificationFailed"/> distinguishes a batch the quorum's verifier rejected from
        /// a replica's own <see cref="LedgerReplicaCorruptException"/>: the replica held the slot as intact, so a
        /// recovery write would keep its copy (LR-I5) and must not count toward <c>Q</c>.
        /// </summary>
        internal sealed record ReadEvent(
            int Replica,
            long At,
            ReplicaRead Answer,
            bool AfterSeal,
            Exception? Failure,
            bool Stopped = false,
            bool VerificationFailed = false) : Event(Replica);

        /// <summary>
        /// A recovery write's answer, <see cref="ReplicaWriteOutcome.Written"/> or <see cref="ReplicaWriteOutcome.Dropped"/>,
        /// its failure, or neither once the recovery stopped copying the position; a failed or stopped write holds no
        /// answer.
        /// </summary>
        internal sealed record CopyEvent(int Replica, long At, ReplicaWriteOutcome Answer, Exception? Failure, bool Stopped = false) : Event(Replica);

        private int Replicas => _quorum._replicas.Length;

        /// <summary>
        /// Gets <c>c + 2K</c>: every writer write lies below it (LP-I7).
        /// </summary>
        private long Bound => checked(_start.Value + 2L * _quorum.WindowSize);

        public async ValueTask<LedgerRecoveryOutcome> RunAsync()
        {
            for (var replica = 0; replica < Replicas; replica++)
            {
                _ = _inFlight.Add(SealAsync(replica));
            }

            while (true)
            {
                if (_end is { } end && CopiesBelowEndDone(end))
                {
                    // LP-I7: the candidate end, once every position below it holds DATA on Q replicas.
                    Debug.Assert(end <= Bound, "LP-I7: T ≤ c + 2K.");
                    return new LedgerRecovery(_start, new Slot(end), ComputeRemainingLease());
                }

                if (_inFlight.Count == 0)
                {
                    throw new AggregateException("The recovery could not reach its coverage or quorum: too few replicas remain able to answer.", _errors);
                }

                Task<Event> completed;
                try
                {
                    completed = await Task.WhenAny(_inFlight).WaitAsync(_stop.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested)
                {
                    ThrowIfStopped();
                    throw;
                }

                // A replica operation which the caller's cancellation or the replica's disposal stopped is no failure of
                // its replica, so the recovery stops before it accounts for any answer which arrived since.
                ThrowIfStopped();
                _ = _inFlight.Remove(completed);
                var recovered = await completed.ConfigureAwait(false);
                observer?.Invoke(Describe(recovered));
                if (IsDropped(recovered))
                {
                    // LP-I5: a recovery which meets a dropped answer, at any position, answers Dropped: the ledger has left
                    // the map, so no close can apply.
                    return ReplicaWriteOutcome.Dropped.Instance;
                }

                Handle(recovered);
            }
        }

        /// <summary>
        /// Stops the recovery's replica operations, hands those still running to the replica, which waits for them before it
        /// disposes its replicas, and releases every value it holds.
        /// </summary>
        public void Dispose()
        {
            _stop.Cancel();
            _quorum.TrackRecoveryStragglers(_inFlight);
            _inFlight.Clear();
            foreach (var position in _positions.Values)
            {
                position.Dispose();
            }

            _positions.Clear();
            _stop.Dispose();
        }

        /// <summary>
        /// Throws once the caller cancelled the recovery or the replica's disposal stopped it.
        /// </summary>
        private void ThrowIfStopped()
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(lifetime.IsCancellationRequested, _quorum);
        }

        private static bool IsDropped(Event recovered) => recovered switch
        {
            SealEvent seal => seal.Answer.TryGetValue(out ReplicaWriteOutcome.Dropped? _),
            ReadEvent read => read.Answer.TryGetValue(out ReplicaWriteOutcome.Dropped? _),
            CopyEvent copy => copy.Answer is ReplicaWriteOutcome.Dropped,
            _ => throw new UnreachableException("A recovery event has no kind."),
        };

        private void Handle(Event recovered)
        {
            switch (recovered)
            {
                case SealEvent seal:
                    OnSeal(seal);
                    break;
                case ReadEvent read:
                    OnRead(read);
                    break;
                case CopyEvent copy:
                    OnCopy(copy);
                    break;
                default:
                    throw new UnreachableException("A recovery event has no kind.");
            }
        }

        private static RecoveryStep Describe(Event recovered) => recovered switch
        {
            SealEvent seal => new RecoveryStep(
                RecoveryStepKind.Seal,
                seal.Replica,
                Address: null,
                seal.Failure is null && seal.Answer.TryGetValue(out LedgerReplicaTail tail) ? tail : null,
                seal.Failure is null ? Answer(seal.Answer.Value) : null,
                AfterSeal: false,
                seal.Failure,
                Stopped: false),
            ReadEvent read => new RecoveryStep(
                RecoveryStepKind.Read,
                read.Replica,
                read.At,
                Tail: null,
                read.Failure is null && !read.Stopped ? Answer(read.Answer.Value) : null,
                read.AfterSeal,
                read.Failure,
                read.Stopped),
            CopyEvent copy => new RecoveryStep(
                RecoveryStepKind.RecoveryWrite,
                copy.Replica,
                copy.At,
                Tail: null,
                copy.Failure is null && !copy.Stopped ? Answer(copy.Answer.Value) : null,
                AfterSeal: false,
                copy.Failure,
                copy.Stopped),
            _ => throw new UnreachableException("A recovery event has no kind."),
        };

        /// <summary>
        /// Names the case a handled answer holds; its case types are shared across the answer unions, so one mapping
        /// serves seals, reads, and recovery writes. The observer's step needs no batch, so a read's is not boxed unless
        /// an observer runs.
        /// </summary>
        private static RecoveryAnswer Answer(object? answer) => answer switch
        {
            LedgerReplicaTail => RecoveryAnswer.Tail,
            ArcBuffer => RecoveryAnswer.Batch,
            ReplicaRead.Missing => RecoveryAnswer.Missing,
            ReplicaWriteOutcome.Written => RecoveryAnswer.Written,
            ReplicaWriteOutcome.Dropped => RecoveryAnswer.Dropped,
            _ => throw new UnreachableException(string.Create(
                CultureInfo.InvariantCulture,
                $"A recovery repeats a Backpressure answer, and a recovery write never answers Sealed, but it handled {answer}.")),
        };

        private void OnSeal(SealEvent seal)
        {
            if (seal.Failure is { } failure)
            {
                Fail(seal.Replica, failure);
                return;
            }

            var answer = seal.Answer switch
            {
                LedgerReplicaTail tail => tail,
                ReplicaWriteOutcome.Dropped => throw new UnreachableException("RunAsync ends at a dropped answer (LP-I5)."),
                ReplicaWriteOutcome.Backpressure => throw new UnreachableException("A recovery repeats a seal which answers Backpressure."),
                null => throw new UnreachableException("A seal which did not fail holds an answer."),
            };

            Debug.Assert(answer.IsSealed, "LR-I8: a replica answers a seal once it is sealed.");

            // LP-I18: record the monotonic lease expiry bound reported by each sealed replica in the sealing coverage.
            if (answer.LeaseRemaining > TimeSpan.Zero)
            {
                var frequency = _quorum._timeProvider.TimestampFrequency;
                var elapsed = Math.Max(0L, _quorum._timeProvider.GetTimestamp() - _recoveryStartTimestamp);
                var waitTicks = LedgerLeaseOptions.CeilingTimestampTicks(answer.LeaseRemaining, frequency);
                var sum = (UInt128)elapsed + (UInt128)waitTicks;
                var deadline = sum > (UInt128)long.MaxValue ? long.MaxValue : (long)sum;
                if (deadline > _leaseDeadlineTicks)
                {
                    _leaseDeadlineTicks = deadline;
                }
            }

            // LP-I7: the recovery holds the replica's seal answer, so a read it sends from here on follows the seal.
            Volatile.Write(ref _sealAnswered[seal.Replica], 1);

            // LP-I6: "It reads from the highest contiguous tail c among those answers and its own."
            if (!_reading)
            {
                _start = Max(_start, answer.ContiguousTail);
                if (++_sealAnswers >= _quorum.Coverage)
                {
                    _start = Max(_start, contiguousTail);
                    _reading = true;
                    _next = _start.Value;
                    _issued = _start.Value;
                    IssueReads();
                }
            }

            // A missing answer counts only from a read sent after the replica's seal answer (LP-I7), so a read which
            // preceded it is sent again.
            var awaiting = _awaitingSeal[seal.Replica];
            foreach (var at in awaiting.Where(at => at >= _next && _end is null && _positions.ContainsKey(at)))
            {
                Read(at, seal.Replica);
            }

            awaiting.Clear();
            Decide();
        }

        private void OnRead(ReadEvent read)
        {
            if (!_positions.TryGetValue(read.At, out var position))
            {
                if (read.Answer.TryGetValue(out ArcBuffer unused))
                {
                    unused.Dispose();
                }

                return;
            }

            position.Pending[read.Replica] = false;
            if (read.Stopped)
            {
                return;
            }

            if (read.Failure is { } failure)
            {
                if (failure is LedgerReplicaCorruptException)
                {
                    // LR-I2: a value which fails integrity is neither DATA nor absence. A replica's own corrupt slot is
                    // replaced by a recovery write (LR-I5), while a batch rejected by end-to-end verification came from
                    // a slot the replica considered intact, so a recovery write would keep it and must not count toward Q.
                    position.Corrupt[read.Replica] = true;
                    if (read.VerificationFailed)
                    {
                        position.Rejected[read.Replica] = true;
                    }

                    _errors.Add(failure);
                }
                else
                {
                    Fail(read.Replica, failure);
                }
            }
            else
            {
                switch (read.Answer)
                {
                    case ArcBuffer batch:
                        if (position.Hold(read.Replica, batch))
                        {
                            // The value stays buffered, within the byte budget, until Q replicas hold it.
                            _bufferedBytes += position.Value!.Value.Length;
                        }

                        position.Absent[read.Replica] = false;
                        break;
                    case ReplicaRead.Missing when read.AfterSeal:
                        position.Absent[read.Replica] = true;
                        break;
                    case ReplicaRead.Missing when Volatile.Read(ref _sealAnswered[read.Replica]) != 0:
                        Read(read.At, read.Replica);
                        break;
                    case ReplicaRead.Missing:
                        _awaitingSeal[read.Replica].Add(read.At);
                        break;
                    case ReplicaWriteOutcome.Dropped:
                        throw new UnreachableException("RunAsync ends at a dropped answer (LP-I5).");
                    case ReplicaWriteOutcome.Backpressure:
                        throw new UnreachableException("A recovery repeats a read which answers Backpressure.");
                    case null:
                    default:
                        throw new UnreachableException("A read which did not fail and was not stopped holds an answer.");
                }
            }

            // A position decided found copies to each replica which does not hold it; one not yet decided waits for its
            // decision, so recovery copies only positions below its end.
            if (position.Found && position.At < _next)
            {
                Copy(position);
            }

            Decide();
        }

        private void OnCopy(CopyEvent copy)
        {
            if (!_positions.TryGetValue(copy.At, out var position))
            {
                return;
            }

            position.Copying[copy.Replica] = false;
            if (copy.Stopped)
            {
                return;
            }

            if (copy.Failure is { } failure)
            {
                Fail(copy.Replica, failure);
            }
            else
            {
                position.Holds[copy.Replica] = copy.Answer switch
                {
                    ReplicaWriteOutcome.Written => !position.Rejected[copy.Replica],
                    ReplicaWriteOutcome.Dropped => throw new UnreachableException("RunAsync ends at a dropped answer (LP-I5)."),
                    ReplicaWriteOutcome.Backpressure => throw new UnreachableException("A recovery repeats a recovery write which answers Backpressure."),
                    ReplicaWriteOutcome.Sealed => throw new UnreachableException("CopyAsync fails a recovery write which answers Sealed (LR-I8)."),
                    null => throw new UnreachableException("A recovery write which did not fail and was not stopped holds an answer."),
                };
            }

            Complete(position);
            Decide();
        }

        /// <summary>
        /// Records a replica's failure. A rejected argument ends the recovery; any other failure which repetition cannot
        /// change leaves the replica out of it.
        /// </summary>
        private void Fail(int replica, Exception failure)
        {
            if (failure is ArgumentException)
            {
                // A rejected argument, such as a replica window the replica refuses, is the caller's error (§6.4).
                ExceptionDispatchInfo.Throw(failure);
            }

            _errors.Add(failure);
            _dead[replica] = true;
        }

        /// <summary>
        /// Decides positions in order from the next undecided one, reading further positions as the window and the byte
        /// budget allow.
        /// </summary>
        private void Decide()
        {
            while (_reading && _end is null && _positions.TryGetValue(_next, out var position))
            {
                if (position.Found)
                {
                    // LP-I7: every position the writer wrote lies below c + 2K.
                    if (_next >= Bound)
                    {
                        throw new InvalidDataException(string.Create(
                            CultureInfo.InvariantCulture,
                            $"Recovery found DATA at position {_next}, at or above c + 2K = {Bound}: the ledger's writer broke its window discipline."));
                    }

                    Copy(position);
                    position.CancelReads();
                    _next++;
                    continue;
                }

                if (position.AbsentCount >= _quorum.Coverage)
                {
                    // LP-I7: N - Q + 1 sealed replicas lack the position, so no writer write at it reaches Q.
                    _end = _next;
                    foreach (var later in _positions.Where(entry => entry.Key >= _next))
                    {
                        later.Value.CancelReads();
                    }

                    return;
                }

                if (!CanDecide(position))
                {
                    throw new AggregateException(
                        string.Create(CultureInfo.InvariantCulture, $"The recovery could not decide position {_next}: too few replicas remain able to answer."),
                        _errors);
                }

                break;
            }

            IssueReads();
        }

        /// <summary>
        /// Returns whether a position's answers can still decide it: a replica may yet report DATA, or enough may yet
        /// report it missing.
        /// </summary>
        private bool CanDecide(Position position)
        {
            var possible = position.AbsentCount;
            for (var replica = 0; replica < Replicas; replica++)
            {
                if (!_dead[replica] && !position.Absent[replica] && !position.Corrupt[replica])
                {
                    possible++;
                }
            }

            return possible >= _quorum.Coverage || position.Pending.Contains(true);
        }

        /// <summary>
        /// Reads every replica at the positions after the last one read, at most the window ahead of the next undecided
        /// position, no further than <c>c + 2K</c>, while the values the recovery holds stay within the byte budget.
        /// </summary>
        private void IssueReads()
        {
            if (!_reading || _end is not null)
            {
                return;
            }

            while (_issued <= Bound && _issued < _next + _quorum.WindowSize && _bufferedBytes < _quorum.RecoveryByteBudget)
            {
                var position = new Position(_issued, Replicas, _stop.Token);
                _positions.Add(_issued, position);
                for (var replica = 0; replica < Replicas; replica++)
                {
                    if (!_dead[replica])
                    {
                        Read(_issued, replica);
                    }
                }

                _issued++;
            }
        }

        /// <summary>
        /// Sends recovery writes of a found position's value to the replicas which do not hold it, while fewer than
        /// <c>Q</c> do (LR-I5, LP-I7).
        /// </summary>
        private void Copy(Position position)
        {
            if (position.Done || position.HolderCount >= _quorum.Quorum)
            {
                Complete(position);
                return;
            }

            for (var replica = 0; replica < Replicas; replica++)
            {
                if (!_dead[replica] && !position.Holds[replica] && !position.Rejected[replica] && !position.Copying[replica])
                {
                    position.Copying[replica] = true;
                    _ = _inFlight.Add(CopyAsync(position.At, replica, position.Value!.Value.Slice(0, position.Value.Value.Length), position.Copies.Token));
                }
            }

            Complete(position);
        }

        /// <summary>
        /// Releases a found position's value once <c>Q</c> replicas hold it, stopping the copies still running, or fails
        /// the recovery when too few replicas can hold it.
        /// </summary>
        private void Complete(Position position)
        {
            if (!position.Found || position.Done)
            {
                return;
            }

            if (position.HolderCount >= _quorum.Quorum)
            {
                _bufferedBytes -= position.Value!.Value.Length;
                position.Finish();
                return;
            }

            var possible = position.HolderCount;
            for (var replica = 0; replica < Replicas; replica++)
            {
                if (!_dead[replica] && !position.Holds[replica] && !position.Rejected[replica])
                {
                    possible++;
                }
            }

            if (possible < _quorum.Quorum)
            {
                throw new AggregateException(
                    string.Create(CultureInfo.InvariantCulture, $"The recovery could not copy position {position.At} to a quorum: too few replicas remain able to answer."),
                    _errors);
            }
        }

        private TimeSpan ComputeRemainingLease()
        {
            if (_leaseDeadlineTicks <= 0)
            {
                return TimeSpan.Zero;
            }

            var elapsed = Math.Max(0L, _quorum._timeProvider.GetTimestamp() - _recoveryStartTimestamp);
            var remaining = _leaseDeadlineTicks - elapsed;
            return remaining <= 0
                ? TimeSpan.Zero
                : LedgerLeaseOptions.CeilingTimestampTicksToTimeSpan(remaining, _quorum._timeProvider.TimestampFrequency);
        }

        private bool CopiesBelowEndDone(long end) =>
            _positions.Where(entry => entry.Key < end).All(static entry => entry.Value.Done);

        private void Read(long at, int replica)
        {
            var position = _positions[at];
            position.Pending[replica] = true;
            _ = _inFlight.Add(ReadAsync(at, replica, position.Reads.Token));
        }

        private async Task<Event> SealAsync(int replica)
        {
            try
            {
                var answer = await _retries.Seal.ExecuteAsync(
                    static async (state, token) => await state.quorum._replicas[state.replica].ReadTailAsync(state.contiguousTail, seal: true, token).ConfigureAwait(false),
                    (quorum: _quorum, contiguousTail, replica),
                    _stop.Token).ConfigureAwait(false);
                return new SealEvent(replica, answer, Failure: null);
            }
            catch (OperationCanceledException exception) when (_stop.IsCancellationRequested)
            {
                // The recovery stopped; it throws before it accounts for this event.
                return new SealEvent(replica, default, exception);
            }
#pragma warning disable CA1031 // The recovery accounts for every replica failure.
            catch (Exception exception)
#pragma warning restore CA1031
            {
                return new SealEvent(replica, default, exception);
            }
        }

        private async Task<Event> ReadAsync(long at, int replica, CancellationToken token)
        {
            try
            {
                // LP-I7: a missing answer counts only from a read sent after the replica's seal answer, so each attempt
                // notes whether the recovery held that answer when it was sent.
                var (answer, afterSeal) = await _retries.Read.ExecuteAsync(
                    static async (state, attempt) =>
                    {
                        var afterSeal = Volatile.Read(ref state.recovery._sealAnswered[state.replica]) != 0;
                        var answer = await state.recovery._quorum._replicas[state.replica]
                            .ReadAsync(new Slot(state.at), state.recovery._start, attempt)
                            .ConfigureAwait(false);
                        return (answer, afterSeal);
                    },
                    (recovery: this, at, replica),
                    token).ConfigureAwait(false);
                if (answer.TryGetValue(out ArcBuffer batch) && _quorum._verifyBatch is { } verify)
                {
                    bool intact;
                    try
                    {
                        intact = verify(batch);
                    }
                    catch
                    {
                        batch.Dispose();
                        throw;
                    }

                    if (!intact)
                    {
                        // LP-I7, LR-I2: a recovery write keeps whatever it stores for the ledger's life (LR-I5), so a
                        // batch that fails end-to-end verification counts as corrupt and is never copied.
                        batch.Dispose();
                        return new ReadEvent(
                            replica,
                            at,
                            default,
                            afterSeal,
                            new LedgerReplicaCorruptException(
                                new Slot(at),
                                string.Create(
                                    CultureInfo.InvariantCulture,
                                    $"Replica {replica} returned a batch at slot {at} which failed end-to-end verification.")),
                            VerificationFailed: true);
                    }
                }

                return new ReadEvent(replica, at, answer, afterSeal, Failure: null);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return new ReadEvent(replica, at, default, AfterSeal: false, Failure: null, Stopped: true);
            }
#pragma warning disable CA1031 // The recovery accounts for every replica failure.
            catch (Exception exception)
#pragma warning restore CA1031
            {
                return new ReadEvent(replica, at, default, AfterSeal: false, exception);
            }
        }

        private async Task<Event> CopyAsync(long at, int replica, ArcBuffer value, CancellationToken token)
        {
            try
            {
                // LP-I11: recovery's writes forward the contiguous tail its seal answers carried, never a position.
                var written = await _retries.Copy.ExecuteAsync(
                    static async (state, attempt) => await state.recovery._quorum._replicas[state.replica]
                        .WriteAsync(new Slot(state.at), state.value, state.recovery._start, recovery: true, attempt)
                        .ConfigureAwait(false),
                    (recovery: this, at, replica, value),
                    token).ConfigureAwait(false);
                if (written is ReplicaWriteOutcome.Sealed)
                {
                    // LR-I8: a sealed replica takes recovery writes, so this answer holds nothing toward Q.
                    throw new InvalidDataException(string.Create(
                        CultureInfo.InvariantCulture,
                        $"Replica {replica} answered a recovery write at position {at} {written}; a replica never answers a recovery write Sealed."));
                }

                return new CopyEvent(replica, at, written, Failure: null);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // Q replicas hold the position, or the recovery stopped.
                return new CopyEvent(replica, at, default, Failure: null, Stopped: true);
            }
#pragma warning disable CA1031 // The recovery accounts for every replica failure.
            catch (Exception exception)
#pragma warning restore CA1031
            {
                return new CopyEvent(replica, at, default, exception);
            }
            finally
            {
                value.Dispose();
            }
        }

        /// <summary>
        /// One position recovery reads: each replica's answer, and once some replica reports DATA, the value and the
        /// replicas which hold it.
        /// </summary>
        private sealed class Position(long at, int replicas, CancellationToken stop) : IDisposable
        {
            public long At { get; } = at;

            /// <summary>
            /// Gets the source which stops the position's reads once it is decided or the recovery ends.
            /// </summary>
            public CancellationTokenSource Reads { get; } = CancellationTokenSource.CreateLinkedTokenSource(stop);

            /// <summary>
            /// Gets the source which stops the position's recovery writes once <c>Q</c> replicas hold it or the recovery
            /// ends, so a slow replica holds neither the value nor the recovery.
            /// </summary>
            public CancellationTokenSource Copies { get; } = CancellationTokenSource.CreateLinkedTokenSource(stop);

            public bool[] Pending { get; } = new bool[replicas];

            public bool[] Absent { get; } = new bool[replicas];

            public bool[] Corrupt { get; } = new bool[replicas];

            public bool[] Rejected { get; } = new bool[replicas];

            public bool[] Holds { get; } = new bool[replicas];

            public bool[] Copying { get; } = new bool[replicas];

            /// <summary>
            /// Gets the position's value while copies of it may still be needed; released once <c>Q</c> replicas hold it.
            /// </summary>
            public ArcBuffer? Value { get; private set; }

            public bool Found { get; private set; }

            public bool Done { get; private set; }

            public int AbsentCount => Absent.Count(static absent => absent);

            public int HolderCount => Holds.Count(static holds => holds);

            /// <summary>
            /// Records that <paramref name="replica"/> holds the position, keeping the first value reported: single
            /// writers give the position one value (LR-I5).
            /// </summary>
            /// <returns>Whether the value is the first reported, which the position now holds.</returns>
            public bool Hold(int replica, ArcBuffer value)
            {
                Holds[replica] = true;
                if (Found)
                {
                    value.Dispose();
                    return false;
                }

                Found = true;
                Value = value;
                return true;
            }

            /// <summary>
            /// Records that <c>Q</c> replicas hold the position, stops its recovery writes still running, and releases its
            /// value; each running write releases its own slice of it.
            /// </summary>
            public void Finish()
            {
                Done = true;
                Copies.Cancel();
                Value?.Dispose();
                Value = null;
            }

            public void CancelReads()
            {
                if (!Reads.IsCancellationRequested)
                {
                    Reads.Cancel();
                }
            }

            public void Dispose()
            {
                // The recovery's stop may still be running its callbacks when the recovery ends, on this thread or another,
                // and disposing a linked source unregisters it from the stop unrun, which would leave every operation the
                // position sent running under a token nothing cancels, holding the quorum's disposal (ledger-protocol.md
                // §6.3); so each is cancelled before it is disposed.
                CancelReads();
                Copies.Cancel();
                Value?.Dispose();
                Value = null;
                Reads.Dispose();
                Copies.Dispose();
            }
        }
    }

    /// <summary>
    /// Tracks a recovery's replica operations still running when it ends, releasing a read's DATA when it answers.
    /// </summary>
    private void TrackRecoveryStragglers(IEnumerable<Task<Recovery.Event>> tasks)
    {
        foreach (var task in tasks)
        {
            TrackStraggler(task.ContinueWith(
                static completed =>
                {
                    if (completed.IsCompletedSuccessfully && completed.Result is Recovery.ReadEvent read && read.Answer.TryGetValue(out ArcBuffer batch))
                    {
                        batch.Dispose();
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default));
        }
    }
}
