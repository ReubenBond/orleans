using System.Diagnostics;
using Orleans.Durable.Storage.Engine.Threading;

namespace Orleans.Durable.Storage.Engine.GroupCommit;

/// <summary>
/// Writes one group-committed value, the ledger map, the node registry, or the manifest: it batches concurrent
/// submissions, merges identical ones, folds each batch in submission order by its value's rules, and writes the result
/// with one conditional write (conventions.md §6).
/// </summary>
/// <typeparam name="TValue">The decoded value.</typeparam>
/// <typeparam name="TVersion">The store's version of the value; <see langword="null"/> while the value is absent.</typeparam>
/// <typeparam name="TChange">A submitted change.</typeparam>
/// <typeparam name="TOutcome">A change's answer.</typeparam>
/// <typeparam name="TReply">The store's answer to a conditional write.</typeparam>
/// <remarks>
/// <para>
/// It answers a change only from a value read or written after the change was submitted: a value is from after a
/// submission when the read or write which produced it was dispatched after it. It keeps the value it last read or wrote
/// with that value's version, names the version its last applied write returned, and re-reads only after a mismatch, an
/// unknown outcome, or a batch which carries nothing against a value from before its submissions. A mismatch asserts only
/// that the write can no longer land, since a backend-SDK retry may have applied an earlier attempt, so the batch is
/// decided again against the value read.
/// </para>
/// <para>
/// A write whose outcome is unknown ends its batch with one read. A change the write carried is answered applied when the
/// value read holds its goal, and otherwise fails with the write's exception, its outcome unknown, since the write may
/// still land; every other change is answered as the rules decide it from the value read, or fails with the write's
/// exception. A read which fails then fails the batch with the write's exception. A read which finds the value corrupt
/// fails each change a write of the batch carried with an unknown outcome around the corruption, and every other change
/// with the corruption; so does the read after a mismatch. The write lands at most once, and only while the store holds
/// the version it was conditioned on (MS-I3), so the rules learn whether it may still land over a value read, and fence
/// the value before an answer it could revoke. The store client's own retries are the only retries.
/// </para>
/// <para>
/// A failed read fails its batch with the read's failure, and the next batch reads first; after a mismatch, a change a
/// write of the batch carried fails instead with an unknown outcome around the read's failure, since that write may have
/// landed. A defect of the rules, or a
/// value which breaks their invariants or does not decode as itself, fails each change no dispatched write carried with
/// <see cref="InvalidOperationException"/>, nothing written for it, and each change a dispatched write carried with an
/// unknown outcome. One batch is decided at a time, the next accumulating meanwhile, and each write starts at least the
/// write interval after the previous one completed. The batches run without any caller's token, in the execution context
/// of the committer's owner, which created it, rather than any submitter's, and each submission is answered exactly once
/// (conventions.md §3, §11, §12). An owner which creates the committer inside a caller's flow, such as a grain activated
/// by a submitter's call, creates it under <see cref="ExecutionContext.SuppressFlow"/>, so the batches run in no context.
/// </para>
/// </remarks>
public sealed class GroupCommitter<TValue, TVersion, TChange, TOutcome, TReply> : IAsyncDisposable
    where TValue : class
    where TVersion : class
    where TChange : notnull
{
    private readonly IGroupCommitRules<TValue, TVersion, TChange, TOutcome, TReply> _rules;
    private readonly string _owner;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _writeInterval;
    private readonly AdmissionGate _gate = new();
    private readonly CancellationTokenSource _disposing = new();
    private readonly CancellationToken _stopping;
    private readonly Lock _lock = new();
    private readonly List<Submission> _pending = [];
    private readonly ExecutionContext? _ownerContext = ExecutionContext.Capture();
    private long _sequence;
    private bool _running;
    private Task _loop = Task.CompletedTask;
    private int _disposeStarted;

    // Read and written only by the batch loop, one batch at a time.
    private KnownValue? _known;
    private HeldWrite? _held;
    private long? _lastWriteCompleted;

    /// <summary>
    /// Initializes a new instance of the <see cref="GroupCommitter{TValue, TVersion, TChange, TOutcome, TReply}"/> class,
    /// whose writes follow one another without an interval.
    /// </summary>
    /// <param name="rules">The value's rules.</param>
    /// <param name="owner">The name of the committer which owns this one, for its disposal's and defects' messages.</param>
    public GroupCommitter(IGroupCommitRules<TValue, TVersion, TChange, TOutcome, TReply> rules, string owner)
        : this(rules, owner, TimeProvider.System, TimeSpan.Zero)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="GroupCommitter{TValue, TVersion, TChange, TOutcome, TReply}"/> class.
    /// </summary>
    /// <param name="rules">The value's rules.</param>
    /// <param name="owner">The name of the committer which owns this one, for its disposal's and defects' messages.</param>
    /// <param name="timeProvider">Spaces the writes.</param>
    /// <param name="writeInterval">The least time from one write's completion to the next write's start.</param>
    public GroupCommitter(IGroupCommitRules<TValue, TVersion, TChange, TOutcome, TReply> rules, string owner, TimeProvider timeProvider, TimeSpan writeInterval)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentException.ThrowIfNullOrEmpty(owner);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentOutOfRangeException.ThrowIfLessThan(writeInterval, TimeSpan.Zero);
        _rules = rules;
        _owner = owner;
        _timeProvider = timeProvider;
        _writeInterval = writeInterval;
        _stopping = _disposing.Token;
    }

    /// <summary>
    /// Gets a value indicating whether the committer is the <c>records-sequence-after-io</c> mutant, which snapshots
    /// <see cref="_sequence"/> after a read or write completes instead of before it was dispatched; for tests.
    /// </summary>
    internal bool RecordsSequenceAfterIoMutant { get; init; }

    /// <summary>
    /// Submits <paramref name="change"/> and returns its answer, decided from a value read or written after the
    /// submission.
    /// </summary>
    /// <param name="change">The change.</param>
    /// <param name="cancellationToken">Ends the caller's wait only: the change stays in its batch and may apply.</param>
    /// <returns>The change's answer, as the rules settle it.</returns>
    /// <exception cref="ObjectDisposedException">The committer was disposed before the change was decided.</exception>
    /// <exception cref="InvalidOperationException">The rules failed, or a value the committer was about to write broke
    /// their invariants: a defect, and no write carried the change.</exception>
    /// <remarks>Any other exception is the rules' answer, a read's failure or corruption, or an unknown outcome, the
    /// rules' <see cref="IGroupCommitRules{TValue, TVersion, TChange, TOutcome, TReply}.OutcomeUnknown"/> exception or
    /// a write's failure; submitting the same change again decides it.</remarks>
    public Task<TOutcome> CommitAsync(TChange change, CancellationToken cancellationToken = default)
    {
        if (change is null)
        {
            throw new ArgumentNullException(nameof(change));
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!_gate.TryEnterUnscoped())
        {
            throw Disposed();
        }

        lock (_lock)
        {
            var sequence = ++_sequence;
            var merged = Pending(change);
            if (merged is null)
            {
                merged = new Submission(change);
                _pending.Add(merged);
            }

            merged.Join(sequence);
            if (!_running)
            {
                _running = true;
                _loop = Start();
            }

            return merged.Outcome.Task.WaitAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Closes admission, answers queued changes with <see cref="ObjectDisposedException"/>, cancels the read or write in
    /// flight, whose batch's outcome stays undecided, and waits until every admitted change has its answer and the batch
    /// loop has ended.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        var drained = _gate.CloseAsync();
        var first = Interlocked.Exchange(ref _disposeStarted, 1) == 0;
        if (first)
        {
            Submission[] queued;
            lock (_lock)
            {
                queued = [.. _pending];
                _pending.Clear();
            }

            foreach (var submission in queued)
            {
                Release(submission, Failed(Disposed()));
            }

            await _disposing.CancelAsync().ConfigureAwait(false);
        }

        await drained.ConfigureAwait(false);
        Task loop;
        lock (_lock)
        {
            loop = _loop;
        }

        await loop.ConfigureAwait(false);
        if (first)
        {
            _disposing.Dispose();
            _ownerContext?.Dispose();
        }
    }

    private static GroupCommitAnswer<TOutcome> Failed(Exception failure) => new GroupCommitAnswer<TOutcome>.Failed(failure);

    private static GroupCommitAnswer<TOutcome>[] Failures(int count, Exception failure) => [.. Enumerable.Repeat(Failed(failure), count)];

    /// <summary>
    /// Starts the batch loop in its owner's execution context, which the constructor captured, rather than the submitter's
    /// which starts it: the loop serves every submitter, and one owner creates and owns it (conventions.md §12). An owner
    /// which created it under suppressed flow captured none, and the loop runs in none.
    /// </summary>
    private Task Start()
    {
        if (_ownerContext is { } owner)
        {
            Task? loop = null;
            ExecutionContext.Run(owner, _ => loop = Task.Run(RunAsync, CancellationToken.None), null);
            return loop!;
        }

        // The owner created the committer with flow suppressed, so the loop runs in no context.
        if (ExecutionContext.IsFlowSuppressed())
        {
            return Task.Run(RunAsync, CancellationToken.None);
        }

        using (ExecutionContext.SuppressFlow())
        {
            return Task.Run(RunAsync, CancellationToken.None);
        }
    }

    /// <summary>Returns the pending submission of <paramref name="change"/>, or <see langword="null"/>. A batch holds few changes.</summary>
    private Submission? Pending(TChange change)
    {
        for (var index = 0; index < _pending.Count; index++)
        {
            if (EqualityComparer<TChange>.Default.Equals(_pending[index].Change, change))
            {
                return _pending[index];
            }
        }

        return null;
    }

    private ObjectDisposedException Disposed() => new(_owner, "The committer was disposed before the change was decided.");

    /// <summary>Answers every caller of <paramref name="submission"/> as <paramref name="answer"/> holds, and ends their admissions.</summary>
    private void Release(Submission submission, GroupCommitAnswer<TOutcome> answer)
    {
        var released = answer switch
        {
            GroupCommitAnswer<TOutcome>.Answered answered => submission.Outcome.TrySetResult(answered.Outcome),
            GroupCommitAnswer<TOutcome>.Failed failed => submission.Outcome.TrySetException(failed.Failure),
            null => throw new UnreachableException("The loop checks every answer holds a case before it releases it."),
        };
        Debug.Assert(released, "Each submission is answered once.");
        submission.Exit(_gate);
    }

    /// <summary>
    /// Decides batches, one at a time, until none is pending, releasing each submission once. A defect of the loop itself
    /// fails every submission it has not released, and stops the loop, so the next submission starts another.
    /// </summary>
    private async Task RunAsync()
    {
        Submission[] batch = [];
        var released = 0;
        try
        {
            await DecideBatchesAsync().ConfigureAwait(false);
        }
        catch (Exception defect)
        {
            var failure = new InvalidOperationException($"{_owner}'s batch loop failed.", defect);
            Submission[] pending;
            lock (_lock)
            {
                pending = [.. _pending];
                _pending.Clear();
                _running = false;
            }

            foreach (var submission in batch.Skip(released).Concat(pending))
            {
                if (submission.Outcome.TrySetException(failure))
                {
                    submission.Exit(_gate);
                }
            }
        }

        async Task DecideBatchesAsync()
        {
            while (true)
            {
                try
                {
                    // Changes submitted while the interval passes join the batch its write carries.
                    await WaitForWriteIntervalAsync().ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
                {
                    // Disposal answered the queue; anything admitted since is decided below, as disposed.
                }

                lock (_lock)
                {
                    if (_pending.Count == 0)
                    {
                        _running = false;
                        return;
                    }

                    batch = [.. _pending];
                    released = 0;
                    _pending.Clear();
                }

                var dispatched = new bool[batch.Length];
                GroupCommitAnswer<TOutcome>[] answers;
                try
                {
                    answers = await DecideAsync(batch, dispatched).ConfigureAwait(false);
                    if (Array.Exists(answers, static answer => answer.Value is null or GroupCommitAnswer<TOutcome>.Failed { Failure: null }))
                    {
                        throw new InvalidOperationException("The rules gave an answer which holds no case, or a failure without its exception.");
                    }
                }
                catch (Exception defect)
                {
                    // DecideAsync answers every failure of the store; anything else is a defect of the rules, and the next
                    // batch reads first.
                    _known = null;
                    answers = Defect(dispatched, new InvalidOperationException($"{_owner} failed to decide a batch.", defect));
                }

                for (; released < batch.Length; released++)
                {
                    Release(batch[released], answers[released]);
                }
            }
        }
    }

    /// <summary>
    /// Answers a batch a defect ended: each change a dispatched write carried with an unknown outcome around it, since the
    /// write may land; each other change with <paramref name="defect"/>, no write carrying it.
    /// </summary>
    private GroupCommitAnswer<TOutcome>[] Defect(bool[] dispatched, InvalidOperationException defect) =>
        [.. dispatched.Select(carried => carried ? Failed(_rules.OutcomeUnknown("A write of the change's batch may have applied before a defect ended the batch.", defect)) : Failed(defect))];

    /// <summary>Decides one batch and returns each submission's answer, in batch order.</summary>
    /// <param name="batch">The batch.</param>
    /// <param name="dispatched">For each change, whether a write of the batch which carried it was dispatched.</param>
    private async Task<GroupCommitAnswer<TOutcome>[]> DecideAsync(Submission[] batch, bool[] dispatched)
    {
        TChange[] changes = new TChange[batch.Length];
        long latest = 0;
        for (var index = 0; index < batch.Length; index++)
        {
            changes[index] = batch[index].Change;
            latest = Math.Max(latest, batch[index].Sequence);
        }

        var known = _known;
        var retry = false;
        while (true)
        {
            if (known is null)
            {
                var step = await ReadAsync().ConfigureAwait(false);
                if (step.Value is ReadStep.Corrupt corrupt)
                {
                    return AfterRead(dispatched, corrupt.Failure, "the read which followed found the value corrupt.");
                }

                if (step.Value is ReadStep.Failed failed)
                {
                    return AfterRead(dispatched, failed.Failure, "the read which followed failed.");
                }

                known = step.Value is ReadStep.Known read ? read.Value : throw new UnreachableException();
            }

            var decision = _rules.Fold(known.Value, known.HeldWriteMayLand, changes);
            var carries = Carries(decision, batch.Length);

            // A batch which carries nothing is answered only from a durable value from after its submissions
            // (conventions.md §6, §13).
            if (!carries && !known.TryConfirmFresh(latest, out var fresh))
            {
                known = null;
                continue;
            }

            if (decision.Written is not { } written)
            {
                if (!known.TryConfirmFresh(latest, out fresh))
                {
                    known = null;
                    continue;
                }

                return SettleReadOnly(decision, batch.Length, in fresh);
            }

            // A fence which applied leaves no held write which may land, so this also refuses a second fence in a batch.
            if (!carries && !known.HeldWriteMayLand)
            {
                return Defect(dispatched, new InvalidOperationException($"{_owner}'s rules wrote without carrying a change while no held write may land: a fence only."));
            }

            var wrote = await WriteAsync(known, decision, written, dispatched, retry).ConfigureAwait(false);
            if (wrote.Value is WriteStep.Decided decided)
            {
                return decided.Answers;
            }

            if (wrote.Value is WriteStep.Mismatched)
            {
                known = null;
                retry = true;
                continue;
            }

            var applied = wrote.Value as WriteStep.Applied ?? throw new UnreachableException();
            known = applied.Value;
            Debug.Assert(!known.HeldWriteMayLand, "No held write may land over a value a later write applied.");
            if (!carries)
            {
                continue;
            }

            return SettleApplied(decision, batch.Length, applied.Commit);
        }
    }

    private static bool Carries(IGroupCommitBatch<TValue, TVersion, TOutcome> decision, int count)
    {
        for (var index = 0; index < count; index++)
        {
            if (decision.Carries(index))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Answers a read-only batch from a fresh, post-submission durable state (conventions.md §6, §13).</summary>
    private static GroupCommitAnswer<TOutcome>[] SettleReadOnly(
        IGroupCommitBatch<TValue, TVersion, TOutcome> decision,
        int count,
        in FreshDurableState state)
    {
        state.EnsureValid();
        return SettleCore(decision, count, state.Value);
    }

    /// <summary>Answers a write batch strictly from a confirmed CAS-applied commit and notifies the batch of the applied version (conventions.md §6, §13).</summary>
    private static GroupCommitAnswer<TOutcome>[] SettleApplied(
        IGroupCommitBatch<TValue, TVersion, TOutcome> decision,
        int count,
        in DurableCommit commit)
    {
        commit.EnsureDurable();
        var answers = SettleCore(decision, count, commit.Value);
        decision.OnApplied(commit.Version);
        return answers;
    }

    /// <summary>Answers every change from <paramref name="value"/>, which a durability barrier verified.</summary>
    private static GroupCommitAnswer<TOutcome>[] SettleCore(IGroupCommitBatch<TValue, TVersion, TOutcome> decision, int count, TValue value)
    {
        var answers = new GroupCommitAnswer<TOutcome>[count];
        for (var index = 0; index < count; index++)
        {
            answers[index] = decision.Settle(index, value);
        }

        return answers;
    }

    /// <summary>
    /// Answers a batch whose read failed, or found the value corrupt: each change a dispatched write carried, as before a
    /// mismatch, with an unknown outcome around <paramref name="failure"/>, since the write may have landed before the
    /// read; each other change with <paramref name="failure"/>.
    /// </summary>
    private GroupCommitAnswer<TOutcome>[] AfterRead(bool[] dispatched, Exception failure, string what) =>
        _stopping.IsCancellationRequested
            ? Failures(dispatched.Length, Disposed())
            : [.. dispatched.Select(carried => carried ? Failed(_rules.OutcomeUnknown("A write of the change's batch may have applied; " + what, failure)) : Failed(failure))];

    /// <summary>
    /// Writes the batch's value over <paramref name="known"/>'s version, after checking it encodes as itself and keeps the
    /// rules' invariants, and, for a retry after a mismatch, after the write interval.
    /// </summary>
    private async Task<WriteStep> WriteAsync(KnownValue known, IGroupCommitBatch<TValue, TVersion, TOutcome> decision, TValue written, bool[] dispatched, bool retry)
    {
        long sequenceBefore;
        TReply? reply = default;
        Exception? unknown = null;
        // A change an earlier attempt carried stays unknown if this attempt is refused, since only this one wrote nothing.
        var earlier = retry ? (bool[])dispatched.Clone() : null;
        var encoded = _rules.Encode(written);
        using (encoded.Content)
        {
            if (!_rules.DecodesAs(encoded.Content.AsReadOnlySequence(), written))
            {
                return new WriteStep.Decided(Defect(dispatched, new InvalidOperationException($"{_owner} would write a value which does not decode as itself.")));
            }

            if (decision.Violation(known.Value) is { } violation)
            {
                return new WriteStep.Decided(Defect(dispatched, new InvalidOperationException(violation)));
            }

            if (retry)
            {
                try
                {
                    await WaitForWriteIntervalAsync().ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
                {
                    return new WriteStep.Decided(Failures(dispatched.Length, Disposed()));
                }
            }

            for (var index = 0; index < dispatched.Length; index++)
            {
                dispatched[index] |= decision.Carries(index);
            }

            // Test-only mutant escape hatch: RecordsSequenceAfterIoMutant intentionally delays the sequence snapshot
            // past the I/O boundary so linearizability/freshness tests prove the sequence-before-IO barrier dynamically.
            sequenceBefore = RecordsSequenceAfterIoMutant ? 0 : CurrentSequence();
            try
            {
                reply = await _rules.WriteAsync(known.Version, encoded, _stopping).ConfigureAwait(false);
            }
            catch (Exception) when (_stopping.IsCancellationRequested)
            {
                return new WriteStep.Decided(Failures(dispatched.Length, Disposed()));
            }
            catch (Exception failure)
            {
                unknown = failure;
            }
            finally
            {
                _lastWriteCompleted = _timeProvider.GetTimestamp();
            }

            if (RecordsSequenceAfterIoMutant)
            {
                sequenceBefore = CurrentSequence();
            }
        }

        if (unknown is not null)
        {
            // The encoding is released before the read which ends the batch.
            return new WriteStep.Decided(await EndAfterUnknownOutcomeAsync(decision, dispatched, unknown, known.Version).ConfigureAwait(false));
        }

        var outcome = _rules.WriteOutcome(reply!);
        if (outcome.Value is GroupCommitWrite<TVersion>.Refused refused)
        {
            // The store wrote nothing, so no read follows, and the value it holds is still the one the batch was decided
            // against.
            return refused.Failure is null
                ? throw new InvalidOperationException("The rules read a write's refusal as holding no exception.")
                : new WriteStep.Decided(AfterRead(earlier ?? new bool[dispatched.Length], refused.Failure, "the store refused a later attempt, which wrote nothing."));
        }

        if (outcome.Value is GroupCommitWrite<TVersion>.Applied applied)
        {
            // The store moved past every version a held write was conditioned on.
            _held = null;
            var commit = DurableCommit.FromApplied(written, applied);
            _known = KnownValue.FromCommit(commit, sequenceBefore);
            return new WriteStep.Applied(_known, commit);
        }

        // A mismatch: the write can no longer land, but an earlier attempt of it may have, so the batch is decided again
        // from a read.
        _known = null;
        return outcome.Value is GroupCommitWrite<TVersion>.Mismatch
            ? WriteStep.Mismatched.Instance
            : throw new InvalidOperationException("The rules read a write's reply as holding no case.");
    }

    /// <summary>
    /// Ends a batch whose write's outcome is unknown with one read (conventions.md §6, §13): a change the write carried
    /// is answered only applied, and every other as the rules decide it from the post-IO <see cref="DurableRead"/>; the
    /// rest fail with <paramref name="unknown"/>, as does the whole batch when the read fails.
    /// </summary>
    private async Task<GroupCommitAnswer<TOutcome>[]> EndAfterUnknownOutcomeAsync(
        IGroupCommitBatch<TValue, TVersion, TOutcome> decision,
        bool[] dispatched,
        Exception unknown,
        TVersion? conditionedOn)
    {
        // The write lands at most once, and only while the store holds the version it was conditioned on (MS-I3).
        _held = new HeldWrite(conditionedOn);
        var step = await ReadAsync().ConfigureAwait(false);
        if (step.Value is ReadStep.Corrupt corrupt)
        {
            return AfterRead(dispatched, corrupt.Failure, "the read which followed found the value corrupt.");
        }

        if (step.Value is ReadStep.Failed)
        {
            return Failures(dispatched.Length, _stopping.IsCancellationRequested ? Disposed() : unknown);
        }

        var known = step.Value as ReadStep.Known ?? throw new UnreachableException();
        return SettleAfterUnknownOutcome(decision, dispatched.Length, known.Read, unknown);
    }

    /// <summary>
    /// Answers a batch after an unknown write outcome strictly from a fresh post-IO <see cref="DurableRead"/>
    /// (conventions.md §6, §13).
    /// </summary>
    private static GroupCommitAnswer<TOutcome>[] SettleAfterUnknownOutcome(
        IGroupCommitBatch<TValue, TVersion, TOutcome> decision,
        int count,
        in DurableRead read,
        Exception unknown)
    {
        read.EnsureDurable();
        var answers = new GroupCommitAnswer<TOutcome>[count];
        for (var index = 0; index < answers.Length; index++)
        {
            answers[index] = (decision.Carries(index)
                ? decision.Applied(index, read.Value)
                : decision.AfterUnknownOutcome(index, read.Value, read.HeldWriteMayLand)) ?? Failed(unknown);
        }

        return answers;
    }

    /// <summary>Returns the sequence number of the latest submission.</summary>
    private long CurrentSequence()
    {
        lock (_lock)
        {
            return _sequence;
        }
    }

    /// <summary>
    /// Reads the value, recording the latest submission made before the read was dispatched, and keeps it for the next
    /// batch; a failed or corrupt read keeps none, so the next batch reads first.
    /// </summary>
    private async Task<ReadStep> ReadAsync()
    {
        // Test-only mutant escape hatch: RecordsSequenceAfterIoMutant intentionally delays the sequence snapshot
        // past the I/O boundary so linearizability/freshness tests prove the sequence-before-IO barrier dynamically.
        var sequenceBefore = RecordsSequenceAfterIoMutant ? 0 : CurrentSequence();
        GroupCommitRead<TValue, TVersion> read;
        try
        {
            read = await _rules.ReadAsync(_stopping).ConfigureAwait(false);
        }
        catch (Exception failure)
        {
            _known = null;
            return new ReadStep.Failed(failure);
        }

        if (RecordsSequenceAfterIoMutant)
        {
            sequenceBefore = CurrentSequence();
        }

        if (read.Value is GroupCommitRead<TValue, TVersion>.Corrupt corrupt)
        {
            _known = null;
            return new ReadStep.Corrupt(corrupt.Failure);
        }

        var found = read.Value as GroupCommitRead<TValue, TVersion>.Found ?? throw new InvalidOperationException("The rules' read holds no case.");

        // A held write may land only while the store holds the version it was conditioned on, which never recurs.
        var mayLand = _held is { } held && EqualityComparer<TVersion?>.Default.Equals(held.Version, found.Version);
        if (!mayLand)
        {
            _held = null;
        }

        var durableRead = DurableRead.FromFound(found, mayLand);
        _known = KnownValue.FromRead(durableRead, sequenceBefore);
        return new ReadStep.Known(_known, durableRead);
    }

    /// <summary>
    /// Waits until the write interval has passed since the last write completed. <see cref="Task.Delay(TimeSpan, TimeProvider, CancellationToken)"/>
    /// waits whole milliseconds, and a timer may fire a tick early, so the wait rounds up and measures again.
    /// </summary>
    private async Task WaitForWriteIntervalAsync()
    {
        while (_lastWriteCompleted is { } completed)
        {
            var remaining = _writeInterval - _timeProvider.GetElapsedTime(completed);
            if (remaining <= TimeSpan.Zero)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(Math.Ceiling(remaining.TotalMilliseconds)), _timeProvider, _stopping).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Zero-allocation proof that a conditional write completed with <see cref="GroupCommitWrite{TVersion}.Applied"/>
    /// (conventions.md §6, §13). Only minted from a post-CAS <see cref="GroupCommitWrite{TVersion}.Applied"/> outcome.
    /// </summary>
    internal readonly struct DurableCommit
    {
        private readonly TValue _value;
        private readonly TVersion _version;
        private readonly bool _durable;

        private DurableCommit(TValue value, TVersion version)
        {
            _value = value;
            _version = version;
            _durable = true;
        }

        internal bool IsValid => _durable;

        internal TValue Value => _durable ? _value : throw Uninitialized();

        internal TVersion Version => _durable ? _version : throw Uninitialized();

        internal static DurableCommit FromApplied(TValue written, GroupCommitWrite<TVersion>.Applied applied)
        {
            ArgumentNullException.ThrowIfNull(written);
            ArgumentNullException.ThrowIfNull(applied);
            if (applied.Version is null)
            {
                throw new InvalidOperationException("The rules read an applied write as holding no version.");
            }

            return new DurableCommit(written, applied.Version);
        }

        internal void EnsureDurable()
        {
            if (!_durable)
            {
                throw Uninitialized();
            }
        }

        private static InvalidOperationException Uninitialized() =>
            new("A default DurableCommit does not witness an applied conditional write (conventions.md §6, §13).");
    }

    /// <summary>
    /// Zero-allocation proof that a store read completed with <see cref="GroupCommitRead{TValue, TVersion}.Found"/>
    /// (conventions.md §6, §13). Only minted from a post-IO <see cref="GroupCommitRead{TValue, TVersion}.Found"/> result.
    /// </summary>
    internal readonly struct DurableRead
    {
        private readonly TValue _value;
        private readonly TVersion? _version;
        private readonly bool _heldWriteMayLand;
        private readonly bool _durable;

        private DurableRead(TValue value, TVersion? version, bool heldWriteMayLand)
        {
            _value = value;
            _version = version;
            _heldWriteMayLand = heldWriteMayLand;
            _durable = true;
        }

        internal bool IsValid => _durable;

        internal TValue Value => _durable ? _value : throw Uninitialized();

        internal TVersion? Version => _durable ? _version : throw Uninitialized();

        internal bool HeldWriteMayLand => _durable ? _heldWriteMayLand : throw Uninitialized();

        internal static DurableRead FromFound(GroupCommitRead<TValue, TVersion>.Found found, bool heldWriteMayLand)
        {
            ArgumentNullException.ThrowIfNull(found);
            ArgumentNullException.ThrowIfNull(found.Current);
            return new DurableRead(found.Current, found.Version, heldWriteMayLand);
        }

        internal void EnsureDurable()
        {
            if (!_durable)
            {
                throw Uninitialized();
            }
        }

        private static InvalidOperationException Uninitialized() =>
            new("A default DurableRead does not witness a completed store read (conventions.md §6, §13).");
    }

    /// <summary>
    /// Zero-allocation proof that a durable state was observed by a read or write dispatched after every submission in
    /// the read-only batch it answers (conventions.md §6, §13).
    /// </summary>
    internal readonly struct FreshDurableState
    {
        private readonly TValue _value;
        private readonly bool _valid;

        private FreshDurableState(TValue value)
        {
            _value = value;
            _valid = true;
        }

        internal bool IsValid => _valid;

        internal TValue Value => _valid ? _value : throw Uninitialized();

        internal static bool TryFromCommit(in DurableCommit commit, long sequenceBefore, long latestSubmissionSequence, out FreshDurableState state)
        {
            commit.EnsureDurable();
            if (latestSubmissionSequence <= sequenceBefore)
            {
                state = new FreshDurableState(commit.Value);
                return true;
            }

            state = default;
            return false;
        }

        internal static bool TryFromRead(in DurableRead read, long sequenceBefore, long latestSubmissionSequence, out FreshDurableState state)
        {
            read.EnsureDurable();
            if (latestSubmissionSequence <= sequenceBefore)
            {
                state = new FreshDurableState(read.Value);
                return true;
            }

            state = default;
            return false;
        }

        internal void EnsureValid()
        {
            if (!_valid)
            {
                throw Uninitialized();
            }
        }

        private static InvalidOperationException Uninitialized() =>
            new("A default FreshDurableState does not witness a fresh durable store state (conventions.md §6, §13).");
    }

    /// <summary>
    /// The value the committer last read or wrote; its version, <see langword="null"/> while the value is absent; the
    /// sequence number of the latest submission made before the read or write which produced it was dispatched; and
    /// whether a write whose outcome is unknown, conditioned on that version, may still land.
    /// </summary>
    private sealed class KnownValue
    {
        private readonly bool _fromCommit;
        private readonly DurableCommit _commit;
        private readonly DurableRead _read;

        private KnownValue(DurableCommit commit, long sequenceBefore)
        {
            commit.EnsureDurable();
            _fromCommit = true;
            _commit = commit;
            Value = commit.Value;
            Version = commit.Version;
            SequenceBefore = sequenceBefore;
            HeldWriteMayLand = false;
        }

        private KnownValue(DurableRead read, long sequenceBefore)
        {
            read.EnsureDurable();
            _fromCommit = false;
            _read = read;
            Value = read.Value;
            Version = read.Version;
            SequenceBefore = sequenceBefore;
            HeldWriteMayLand = read.HeldWriteMayLand;
        }

        public TValue Value { get; }

        public TVersion? Version { get; }

        public long SequenceBefore { get; }

        public bool HeldWriteMayLand { get; }

        public static KnownValue FromCommit(DurableCommit commit, long sequenceBefore) => new(commit, sequenceBefore);

        public static KnownValue FromRead(DurableRead read, long sequenceBefore) => new(read, sequenceBefore);

        public bool TryConfirmFresh(long latestSubmissionSequence, out FreshDurableState state) =>
            _fromCommit
                ? FreshDurableState.TryFromCommit(in _commit, SequenceBefore, latestSubmissionSequence, out state)
                : FreshDurableState.TryFromRead(in _read, SequenceBefore, latestSubmissionSequence, out state);
    }

    /// <summary>The version a write whose outcome is unknown was conditioned on: it may land while the store holds it.</summary>
    private sealed record HeldWrite(TVersion? Version);

    /// <summary>A read's outcome: the value with its durability proof, its corruption, or the read's failure.</summary>
    private readonly union ReadStep(ReadStep.Known, ReadStep.Corrupt, ReadStep.Failed)
    {
        public sealed record Known(KnownValue Value, DurableRead Read);

        public sealed record Corrupt(Exception Failure);

        public sealed record Failed(Exception Failure);
    }

    /// <summary>A write's outcome: applied with its durability proof, mismatched, or the batch's answers.</summary>
    private readonly union WriteStep(WriteStep.Applied, WriteStep.Mismatched, WriteStep.Decided)
    {
        public sealed record Applied(KnownValue Value, DurableCommit Commit);

        public sealed record Mismatched
        {
            public static Mismatched Instance { get; } = new();
        }

        public sealed record Decided(GroupCommitAnswer<TOutcome>[] Answers);
    }

    /// <summary>One change and every caller which submitted it while it was pending.</summary>
    private sealed class Submission(TChange change)
    {
        private int _callers;

        public TChange Change { get; } = change;

        public TaskCompletionSource<TOutcome> Outcome { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Gets the latest sequence number among its callers', so its answer comes from a value from after every one.</summary>
        public long Sequence { get; private set; }

        public void Join(long sequence)
        {
            Sequence = sequence;
            _callers++;
        }

        /// <summary>Ends every caller's admission, once its answer is released.</summary>
        public void Exit(AdmissionGate gate)
        {
            for (; _callers > 0; _callers--)
            {
                gate.Exit();
            }
        }
    }
}
