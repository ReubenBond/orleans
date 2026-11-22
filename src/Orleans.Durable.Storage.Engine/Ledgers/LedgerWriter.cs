using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Threading;
using Polly;
using Polly.Retry;

namespace Orleans.Durable.Storage.Engine.Ledgers;

/// <summary>
/// The primary's writer of one ledger (ledgers.md §2.4, ledger-protocol.md §2.3): its ordered proposals, decided from the
/// ledger map when they meet a seal, and its open check, over the process's <see cref="Ledger"/> of the ledger and the
/// sequencer lifetime that <see cref="Ledger"/> holds for its one writer.
/// </summary>
/// <remarks>
/// <para>
/// A proposal's caller creates a <see cref="LedgerBatch"/>, which takes its slot at creation, one creation at a time, with
/// its segment state (ledgers.md §8.2), writes its rows into it, and proposes it; the proposal appends the batch at its
/// slot (ledger-protocol.md §8.1). Every created batch is proposed or the writer is abandoned, so batches are decided in
/// slot order (LG-I12). An append which completes is
/// <see cref="ProposalOutcome.Durable"/>; one which met a seal or could not complete is decided by this process's map
/// view, which the writer re-reads with bounded backoff until it lists the ledger closed or no longer lists it
/// (ledgers.md §8.2).
/// </para>
/// <para>
/// Each acquired slot's resolution is admitted through an admission gate (conventions.md §11). Disposal closes it,
/// cancels every write still waiting out an outage and every completion, waits for the admitted resolutions to end,
/// stops the lifetime, and releases the writer's lease on its <see cref="Ledger"/>, which it never disposes itself.
/// </para>
/// <para>
/// The <see cref="Ledger"/> stops the lifetime before any recovery in the writer's process and once a seal check there
/// finds the ledger sealed (LP-I14), so the process appends nothing to a ledger it is sealing.
/// </para>
/// </remarks>
public sealed partial class LedgerWriter : IAsyncDisposable
{
    private static readonly ProposalOutcome FencedOutcome = new(default(ProposalOutcome.Fenced));

    private readonly CancellationTokenSource _disposing = new();
    private readonly Serving? _serving;
    private readonly AdmissionGate _admissions = new();

    // Admits proposals until StopAdmittingAsync closes it; each admission lasts until its append's resolution ends, whether
    // or not its caller still waits.
    private readonly AdmissionGate _proposals = new();
    private readonly TaskCompletionSource _fenced = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TimeProvider _timeProvider;
    private readonly ResiliencePipeline<LedgerWriteOutcome> _writeRetry;
    private readonly ResiliencePipeline<bool> _mapReadRetry;
    private readonly Lock _lock = new();
    private TaskCompletionSource<Slot?>? _recordedTail;
    private Task? _mapFollowing;
    private TaskCompletionSource<OpenCheckOutcome>? _runningCheck;
    private TaskCompletionSource<OpenCheckOutcome>? _queuedCheck;
    private Task? _helping;
    private int _disposed;

    // Completes once the writer's one disposal has ended, which every later DisposeAsync awaits.
    private readonly TaskCompletionSource _disposalEnded = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Initializes a new instance of the <see cref="LedgerWriter"/> class over <paramref name="ledger"/> and its writer's
    /// lifetime; <see cref="Ledger.CreateWriter(TimeProvider)"/> creates the one writer of a ledger. Construction
    /// performs no I/O.
    /// </summary>
    /// <param name="ledger">This process's <see cref="Ledger"/> of the ledger, which the writer never disposes.</param>
    /// <param name="sequencer">The lifetime the <see cref="Ledger"/> holds for its writer, with the ledger's window.</param>
    /// <param name="timeProvider">Times the delays between repeated writes and map reads.</param>
    /// <param name="serving">What its proposals need, or <see langword="null"/> for a writer which only appends.</param>
    internal LedgerWriter(Ledger ledger, Sequencer sequencer, TimeProvider timeProvider, Serving? serving)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(sequencer);
        ArgumentNullException.ThrowIfNull(timeProvider);
        Ledger = ledger;
        _serving = serving;
        _timeProvider = timeProvider;
        _writeRetry = StorageOutageRetry.Create(timeProvider, firstAttemptNumber: 1);
        _mapReadRetry = CreateMapReadRetry(timeProvider);
        Sequencer = sequencer;
        if (serving is not null)
        {
            InitializeBatches(serving.Host);
        }

        // A lifetime its Ledger stopped before the writer existed, after a recovery in this process, is handled here.
        if (!sequencer.TrySetOnStop(OnStopped, out var stoppedWith))
        {
            OnStopped(stoppedWith);
        }
    }

    /// <summary>
    /// Gets the delay before the writer re-reads the map a second time while its view decides no proposal it holds.
    /// </summary>
    internal static TimeSpan MapReadBaseDelay { get; } = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// Gets the longest delay between the writer's map reads while its view decides no proposal it holds.
    /// </summary>
    internal static TimeSpan MapReadMaximumDelay { get; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Gets the longest <see cref="StopAdmittingAsync"/> waits for the appends it admitted before it stops the lifetime: a
    /// few write round trips. The primary's own writes may wait out backpressure which only the executor's seal ends, so
    /// the wait is bounded, and the seal decides the appends still in flight.
    /// </summary>
    internal static TimeSpan StopAdmittingDrainLimit { get; } = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Gets the instance ID of the writer's sequencer lifetime, which the ledger's primary claim names (LP-I15).
    /// </summary>
    public SequencerInstanceId InstanceId => Sequencer.InstanceId;

    /// <summary>
    /// Gets the writer's sequencer lifetime.
    /// </summary>
    internal Sequencer Sequencer { get; }

    /// <summary>
    /// Gets the process's <see cref="Ledgers.Ledger"/> of the ledger, which the writer leases.
    /// </summary>
    internal Ledger Ledger { get; }

    /// <summary>Gets what the writer's proposals need, which only a writer created through the reader's lease holds.</summary>
    /// <exception cref="InvalidOperationException">The writer only appends.</exception>
    private Serving Proposing => _serving ?? throw new InvalidOperationException(
        "Only a writer created through the reader's lease of its ledger proposes (ledgers.md §2.4); this one only appends.");

    /// <summary>Gets the replica's payload limit, which every batch of the writer stays within (ledgers.md §7).</summary>
    /// <exception cref="InvalidOperationException">The writer only appends.</exception>
    public int MaxBatchLength => Proposing.Host.MaxBatchLength;

    /// <summary>Gets the ID of the writer's ledger.</summary>
    public LedgerId LedgerId => Ledger.Id;

    /// <summary>Gets a value indicating whether the writer's disposal has begun, after which every proposal throws.</summary>
    internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    /// <summary>Gets the range of the writer's ledger, which every key it proposes lies in.</summary>
    /// <exception cref="InvalidOperationException">The writer only appends.</exception>
    public KeyRange Range => Proposing.Range;

    /// <summary>Gets the ledger's window <c>K</c>: the most slots in flight at once (ledger-protocol.md §2.6).</summary>
    public int Window => Sequencer.WindowSize;

    /// <summary>
    /// Gets a task which completes once this writer will make no later proposal <see cref="ProposalOutcome.Durable"/>
    /// (ledgers.md §2.4): its lifetime stopped for any reason but its disposal (a write met a seal, a write or completion
    /// failed, an executor in this process stopped admitting, or a recovery or open check in this process sealed the
    /// ledger or found it sealed), or the map view lists the ledger closed or no longer lists it. Every proposal afterwards
    /// is <see cref="ProposalOutcome.Fenced"/> or <see cref="ProposalOutcome.Ambiguous"/>.
    /// </summary>
    public Task Fenced => _fenced.Task;

    /// <summary>
    /// Proposes <paramref name="batch"/> and returns its outcome once it is decided (ledgers.md §2.4): a call with a
    /// complete batch this writer created takes the batch's bytes at once and appends them at the batch's slot, whatever
    /// follows, a cancellation or a disposal included. The batch is then proposed, and immutable. Proposals complete in
    /// slot order, and one whose batch took no slot completes <see cref="ProposalOutcome.Fenced"/> at once. The caller
    /// disposes the batch, which then holds nothing.
    /// </summary>
    /// <param name="batch">A complete batch this writer created, every declared row written.</param>
    /// <param name="cancellationToken">
    /// Stops only the caller waiting: the append is decided as usual, and later proposals still complete in slot order.
    /// </param>
    /// <returns>
    /// <see cref="ProposalOutcome.Durable"/> at the batch's slot once every lower slot is decided;
    /// <see cref="ProposalOutcome.Fenced"/> when the ledger's recorded <c>T</c> is at or below its slot, or it took none;
    /// or <see cref="ProposalOutcome.Ambiguous"/> when the ledger left the map before this process's view learned <c>T</c>.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="batch"/> was created by another writer, or a declared row or body byte is unwritten. Nothing was
    /// appended, and the batch keeps its bytes.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The batch was already proposed or disposed, or left unusable by a failed write, or the writer only appends.
    /// Nothing was appended.
    /// </exception>
    /// <exception cref="ObjectDisposedException">
    /// The writer was disposed before the call, or after the batch's write began while its map view listed the ledger
    /// open.
    /// </exception>
    public ValueTask<ProposalOutcome> ProposeAsync(LedgerBatch batch, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (!ReferenceEquals(batch.Owner, this))
        {
            throw new ArgumentException("A batch is proposed by the writer which created it, whose range, payload limit, and segment it was checked against.", nameof(batch));
        }

        if (!batch.IsComplete)
        {
            throw new ArgumentException(
                string.Create(CultureInfo.InvariantCulture, $"The batch declares {batch.DeclaredRowCount} rows and has begun {batch.RowsBegun}, or its last body is incomplete."),
                nameof(batch));
        }

        // The proposal takes the batch's bytes now, whatever follows.
        var encoded = batch.Complete();
        var reserved = OnProposed(batch);
        if (Volatile.Read(ref _disposed) != 0)
        {
            // A slot the batch took is never written; the disposal's stop ends the ledger at or below it.
            encoded.Dispose();
            ReleaseReservation(reserved);
            throw new ObjectDisposedException(GetType().FullName);
        }

        if (batch.Grant is not { } grant)
        {
            // The batch took no slot, since the lifetime had stopped: it is not in the ledger and never will be.
            encoded.Dispose();
            return ValueTask.FromResult(FencedOutcome);
        }

        if (!_proposals.TryEnterUnscoped())
        {
            // An executor in this process stopped admitting (StopAdmittingAsync) after the batch took its slot: the
            // proposal is not in the ledger and never will be, and the slot nobody wrote ends the ledger at or below it. It
            // completes once the earlier proposals' appends have ended, so proposals complete in slot order (LG-I5).
            encoded.Dispose();
            ReleaseReservation(reserved);
            return FencedAfterAdmittedAsync(cancellationToken);
        }

        return ProposeGrantedAsync(grant, encoded, reserved, cancellationToken);
    }

    /// <summary>
    /// Stops the writer's lifetime for its user, which met a fault of its own and proposes nothing more (ledgers.md §8.2):
    /// the writer is <see cref="Fenced"/>, helps the recovery a stopped lifetime means, and its ledger is
    /// <c>Unserved</c> until a takeover replaces it (§8.6). Every later proposal is <see cref="ProposalOutcome.Fenced"/>,
    /// and proposals already made are decided as usual. Idempotent.
    /// </summary>
    public void Abandon() => Sequencer.Stop(SequencerStopReason.Abandoned);

    /// <summary>
    /// For an executor in this process which closes the ledger (ledgers.md §8.1): every proposal after the call is
    /// <see cref="ProposalOutcome.Fenced"/> without a slot; once every earlier proposal's append has ended, or
    /// <see cref="StopAdmittingDrainLimit"/> has passed, it stops the lifetime with <see cref="SequencerStopReason.Sealing"/>,
    /// so the executor's recovery starts at the primary's contiguous tail and its seal decides the appends still in
    /// flight. Irrevocable and idempotent: the caller then seals the ledger, and a caller which exits before the seal
    /// leaves the lifetime stopped and the ledger unserved.
    /// </summary>
    /// <param name="cancellationToken">
    /// Stops the wait for the earlier appends; the lifetime is stopped all the same, and the recorded <c>T</c> decides them.
    /// </param>
    /// <returns>A task which completes once the lifetime is stopped.</returns>
    internal async Task StopAdmittingAsync(CancellationToken cancellationToken = default)
    {
        Task drained;
        lock (_lock)
        {
            // Under the lock, so a stop the lifetime makes meanwhile sees the gate closed and leaves the recovery to the
            // executor (OnStopped). Every batch created from now on takes no slot. Surrender the primary lease
            // permanently so no local 0-RTT read can be served once closing begins (LP-I18, LG-I14).
            Ledger.SurrenderLease();
            drained = _proposals.CloseAsync();
            _admittingBatches = false;
            PumpCreations();
        }

        try
        {
            await drained.WaitAsync(StopAdmittingDrainLimit, _timeProvider, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // An append still in flight waits out backpressure or an outage; the executor's seal decides it.
        }
        finally
        {
            // LP-I14: the executor's recovery follows, and the lifetime is stopped before its first seal.
            Sequencer.Stop(SequencerStopReason.Sealing);
        }
    }

    /// <summary>
    /// Cooperatively drains in-flight proposals and stops admitting new batches before a planned handoff within
    /// <paramref name="deadline"/> (orleans.md §5.6, OR-I24).
    /// </summary>
    /// <remarks>
    /// Repeating this call on a writer that already stopped with <see cref="SequencerStopReason.Released"/> returns
    /// <see cref="LedgerPrepareReleaseOutcome.Released"/> idempotently with its final contiguous tail. If another node
    /// concurrently seals the ledger while proposals are draining or before release completes, this method aborts
    /// immediately and returns <see cref="LedgerPrepareReleaseOutcome.Sealed"/> without waiting for
    /// <paramref name="deadline"/>.
    /// </remarks>
    /// <param name="deadline">Maximum time to wait for in-flight proposals and replica confirmation.</param>
    /// <param name="cancellationToken">Cancels the caller's wait.</param>
    /// <returns>
    /// <see cref="LedgerPrepareReleaseOutcome.Released"/> once drained and stopped admitting,
    /// <see cref="LedgerPrepareReleaseOutcome.Sealed"/> on concurrent seal or prior stop, or
    /// <see cref="LedgerPrepareReleaseOutcome.TimedOut"/> when <paramref name="deadline"/> expires first.
    /// </returns>
    public async Task<LedgerPrepareReleaseOutcome> PrepareReleaseAsync(TimeSpan deadline, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(deadline, TimeSpan.Zero);
        cancellationToken.ThrowIfCancellationRequested();
        var started = _timeProvider.GetTimestamp();

        if (Sequencer.StopReason == SequencerStopReason.Released)
        {
            Ledger.SurrenderLease();
            return new LedgerPrepareReleaseOutcome.Released(KnownContiguousTail);
        }

        if (Sequencer.IsStopped || Volatile.Read(ref _disposed) != 0 || _serving?.Map.Current.Get(Ledger.Id) is not { IsOpen: true })
        {
            return LedgerPrepareReleaseOutcome.Sealed.Instance;
        }

        Task drained;
        lock (_lock)
        {
            if (Sequencer.StopReason == SequencerStopReason.Released)
            {
                Ledger.SurrenderLease();
                return new LedgerPrepareReleaseOutcome.Released(KnownContiguousTail);
            }

            if (Sequencer.IsStopped || Volatile.Read(ref _disposed) != 0)
            {
                return LedgerPrepareReleaseOutcome.Sealed.Instance;
            }

            // Under the lock, so a concurrent stop sees the gate closed and leaves recovery to the transition executor.
            // Permanently surrender the primary lease before confirming open or returning Released so the successor's
            // TransitionExecutor can skip the pre-CAS lease wait safely (OR-I24, LP-I18, LG-I14).
            Ledger.SurrenderLease();
            drained = _proposals.CloseAsync();
            _admittingBatches = false;
            PumpCreations();
        }

        try
        {
            if (!drained.IsCompleted)
            {
                var remaining = deadline - _timeProvider.GetElapsedTime(started);
                if (remaining <= TimeSpan.Zero)
                {
                    return StopAfterPrepareReleaseTimeout();
                }

                using var waiting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var delay = Task.Delay(remaining, _timeProvider, waiting.Token);
                var winner = await Task.WhenAny(drained, Fenced, delay).ConfigureAwait(false);
                await waiting.CancelAsync().ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (winner == Fenced || Sequencer.IsStopped)
                {
                    return Sequencer.StopReason == SequencerStopReason.Released
                        ? new LedgerPrepareReleaseOutcome.Released(KnownContiguousTail)
                        : LedgerPrepareReleaseOutcome.Sealed.Instance;
                }

                if (winner != drained)
                {
                    return StopAfterPrepareReleaseTimeout();
                }
            }

            if (Sequencer.StopReason == SequencerStopReason.Released)
            {
                return new LedgerPrepareReleaseOutcome.Released(KnownContiguousTail);
            }

            if (Sequencer.IsStopped || Volatile.Read(ref _disposed) != 0 || _serving?.Map.Current.Get(Ledger.Id) is not { IsOpen: true })
            {
                return LedgerPrepareReleaseOutcome.Sealed.Instance;
            }

            // Confirm against Q replicas that no other node concurrently sealed this ledger while we drained
            // (including when there were no in-flight writes to hit a sealed replica directly).
            var checkRemaining = deadline - _timeProvider.GetElapsedTime(started);
            if (checkRemaining <= TimeSpan.Zero)
            {
                return StopAfterPrepareReleaseTimeout();
            }

            OpenCheckOutcome openCheck;
            try
            {
                openCheck = await ConfirmOpenAsync(cancellationToken).WaitAsync(checkRemaining, _timeProvider, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                return StopAfterPrepareReleaseTimeout();
            }
            catch (ObjectDisposedException)
            {
                return LedgerPrepareReleaseOutcome.Sealed.Instance;
            }
            catch (Exception exception) when (!IsProcessCritical(exception) && !(exception is OperationCanceledException && cancellationToken.IsCancellationRequested))
            {
                return StopAfterPrepareReleaseTimeout();
            }

            if (openCheck.Value is OpenCheckOutcome.Sealed)
            {
                return LedgerPrepareReleaseOutcome.Sealed.Instance;
            }

            if (openCheck.Value is ReplicaWriteOutcome.Backpressure)
            {
                return StopAfterPrepareReleaseTimeout();
            }

            if (Sequencer.StopReason == SequencerStopReason.Released)
            {
                return new LedgerPrepareReleaseOutcome.Released(KnownContiguousTail);
            }

            if (Sequencer.IsStopped || Volatile.Read(ref _disposed) != 0 || _serving?.Map.Current.Get(Ledger.Id) is not { IsOpen: true })
            {
                return LedgerPrepareReleaseOutcome.Sealed.Instance;
            }

            // Push the final contiguous tail to replicas before stopping admission so recovery starts at the primary's tail.
            lock (_contiguousTailLock)
            {
                if (!_contiguousTailClosed && !Sequencer.IsStopped && Volatile.Read(ref _disposed) == 0)
                {
                    _ = Ledger.WriteContiguousTail();
                }
            }

            Sequencer.Stop(SequencerStopReason.Released);
            return Sequencer.StopReason == SequencerStopReason.Released
                ? new LedgerPrepareReleaseOutcome.Released(KnownContiguousTail)
                : LedgerPrepareReleaseOutcome.Sealed.Instance;
        }
        finally
        {
            // Once proposals have been closed, the writer cannot resume admitting; ensure the lifetime is stopped so
            // cancellation or any abnormal exit never leaves a half-closed writer with Sequencer.IsStopped == false.
            if (!Sequencer.IsStopped)
            {
                Sequencer.Stop(SequencerStopReason.Sealing);
            }
        }

        LedgerPrepareReleaseOutcome StopAfterPrepareReleaseTimeout()
        {
            // Proposals are already closed; stop the lifetime as Sealing so IsUnserved surfaces it if the controller fails over.
            Sequencer.Stop(SequencerStopReason.Sealing);
            if (Sequencer.StopReason == SequencerStopReason.Released)
            {
                return new LedgerPrepareReleaseOutcome.Released(KnownContiguousTail);
            }

            return Sequencer.StopReason == SequencerStopReason.Sealing
                ? LedgerPrepareReleaseOutcome.TimedOut.Instance
                : LedgerPrepareReleaseOutcome.Sealed.Instance;
        }
    }

    /// <summary>
    /// Checks that the ledger is open with a seal check of <c>Q</c> replicas begun after the call (ledgers.md §8.3,
    /// LP-I9). Concurrent calls share checks: a call made while a check is in flight shares the next one, which runs with
    /// no caller's token, so one caller's cancellation ends only its own wait.
    /// </summary>
    /// <returns>
    /// <see cref="OpenCheckOutcome.Open"/> with the primary's contiguous tail when no replica answered sealed or dropped;
    /// <see cref="OpenCheckOutcome.Sealed"/>, once the <see cref="Ledger"/> has stopped the writer's lifetime (LP-I14),
    /// after which the writer follows its map view until it learns <c>T</c>; or
    /// <see cref="ReplicaWriteOutcome.Backpressure"/>, when backpressure kept the check from deciding, which asserts
    /// nothing.
    /// </returns>
    /// <remarks>The check fails as <see cref="Ledger.ReadTailAsync"/> does when no quorum answers.</remarks>
    /// <exception cref="ObjectDisposedException">The writer was disposed; a check already in flight still answers.</exception>
    public Task<OpenCheckOutcome> ConfirmOpenAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        TaskCompletionSource<OpenCheckOutcome> check;
        var start = false;
        lock (_lock)
        {
            if (_runningCheck is null)
            {
                check = _runningCheck = NewCheck();
                start = true;
            }
            else
            {
                check = _queuedCheck ??= NewCheck();
            }
        }

        if (start)
        {
            // The checks serve every caller, so they run in none's execution context (conventions.md §12).
            using (ExecutionContext.SuppressFlow())
            {
                _ = Task.Run(() => RunChecksAsync(check), CancellationToken.None);
            }
        }

        return check.Task.WaitAsync(cancellationToken);
    }

    /// <summary>
    /// Appends a batch at <paramref name="slot"/>, which this writer acquired (<see cref="AcquireAsync"/>) and has not
    /// appended, and returns its outcome (ledger-protocol.md §2.3, §2.4).
    /// </summary>
    /// <param name="slot">A slot acquired from this writer and not yet appended.</param>
    /// <param name="payload">The batch; non-empty. The caller owns it until the call returns.</param>
    /// <param name="cancellationToken">
    /// Stops the caller waiting. It neither cancels the write nor changes its outcome, and later appends still complete in
    /// slot order.
    /// </param>
    /// <returns>
    /// <see cref="AppendOutcome.Appended"/> once the slot completed; <see cref="AppendOutcome.Sealed"/> when the write met
    /// a seal or a drop; or <see cref="AppendOutcome.Unresolved"/> when the writer's disposal began before the write, the
    /// write failed or was abandoned once it began, or the completion failed, which the recovered <c>T</c> decides.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// The payload is empty, or the slot was not acquired from this writer or was already appended. Nothing was written.
    /// </exception>
    internal async ValueTask<AppendOutcome> AppendAsync(Slot slot, ArcBuffer payload, CancellationToken cancellationToken = default)
    {
        ThrowIfProposing();
        if (payload.Length == 0)
        {
            throw new ArgumentException("A zero-length batch cannot be appended.", nameof(payload));
        }

        var grant = TakeAcquired(slot);

        // An abandoned slot would hold back every later completion, so a slot whose caller may stop waiting resolves on
        // its own with a retained view of the payload, which the caller may release once it stops waiting.
        var owned = cancellationToken.CanBeCanceled;
        var resolved = owned ? payload.Retain() : payload;
        return await WaitAsync(BeginResolution(grant, resolved, owned, reserved: 0, proposals: null), cancellationToken).ConfigureAwait(false);
    }
    /// <summary>
    /// Cancels every write still waiting out an outage and every completion, waits for the appends in progress to end,
    /// stops the lifetime with <see cref="SequencerStopReason.Requested"/>, and releases the writer's lease on its
    /// <see cref="Ledger"/>: no append outlives the writer (ledger-protocol.md §6.3). A proposal which took no slot, or
    /// whose slot was granted during the disposal and never written, is <see cref="ProposalOutcome.Fenced"/>; one whose
    /// write began and was not decided is decided from the map view as it stands, or fails with
    /// <see cref="ObjectDisposedException"/> while the view still lists the ledger open.
    /// </summary>
    /// <returns>
    /// A task which completes once the admitted appends have ended and the lifetime is stopped: every call returns once the
    /// one disposal has ended, so a caller which races the registry's disposal of the writer awaits it too.
    /// </returns>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            // The first call observes the disposal's failure, a defect; a later one awaits its end.
            await _disposalEnded.Task.ConfigureAwait(false);
            return;
        }

        try
        {
            var drained = _admissions.CloseAsync();
            await _disposing.CancelAsync().ConfigureAwait(false);
            await drained.ConfigureAwait(false);
            Sequencer.Stop(SequencerStopReason.Requested);
            CloseContiguousTail();
            if (_serving is not null)
            {
                // A writer its registry disposes once the view decided the ledger learns the decision, idle or not.
                _ = Decide(_serving.Map.Current, out _);
            }

            Task? mapFollowing;
            Task? helping;
            Task? agedMarkers;
            lock (_lock)
            {
                mapFollowing = _mapFollowing;
                helping = _helping;
                agedMarkers = _agedMarkers;
                _ageTimer?.Dispose();
                _ageTimer = null;
                PumpCreations();

                // The writer creates no batch after its disposal, so its open segment ends here.
                ReportSegmentEndedLocked();
            }

            try
            {
                if (mapFollowing is not null)
                {
                    await mapFollowing.ConfigureAwait(false);
                }

                if (agedMarkers is not null)
                {
                    // An aged marker's unexpected failure is a defect, which surfaces here.
                    await agedMarkers.ConfigureAwait(false);
                }

                if (helping is not null)
                {
                    // A helper's unexpected failure is a defect, which surfaces here once the lease is released.
                    await helping.ConfigureAwait(false);
                }
            }
            finally
            {
                _serving?.Lease.Dispose();
                _disposing.Dispose();
            }
        }
        finally
        {
            _ = _disposalEnded.TrySetResult();
        }
    }

    // A stack overflow or an access violation ends the process before any catch runs, so running out of memory is the one
    // process-critical failure a catch can meet.
    private static bool IsProcessCritical(Exception exception) => exception is OutOfMemoryException;

    private static TaskCompletionSource<OpenCheckOutcome> NewCheck() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static ProposalOutcome OutcomeAt(Slot slot, Slot? recordedTail) => recordedTail switch
    {
        { } end when slot < end => new ProposalOutcome(new ProposalOutcome.Durable(slot)),
        not null => FencedOutcome,
        null => new ProposalOutcome(default(ProposalOutcome.Ambiguous)),
    };

    private static void Release(ArcBuffer payload, bool owned)
    {
        if (owned)
        {
            payload.Dispose();
        }
    }

    /// <summary>
    /// Re-reads the map with bounded, jittered, exponential backoff while the view decides no proposal the writer holds;
    /// a failed read asserts nothing, so it is repeated too.
    /// </summary>
    internal static ResiliencePipeline<bool> CreateMapReadRetry(TimeProvider timeProvider) =>
        new ResiliencePipelineBuilder<bool> { TimeProvider = timeProvider }
            .AddRetry(new RetryStrategyOptions<bool>
            {
                ShouldHandle = static arguments => ValueTask.FromResult(
                    arguments.Outcome.Exception is { } exception
                        ? !IsProcessCritical(exception) && !arguments.Context.CancellationToken.IsCancellationRequested
                        : !arguments.Outcome.Result),
                MaxRetryAttempts = int.MaxValue,
                DelayGenerator = static arguments => ValueTask.FromResult<TimeSpan?>(Backoff.Delay(arguments.AttemptNumber, MapReadBaseDelay, MapReadMaximumDelay, Random.Shared.NextDouble())),
            })
            .Build();


    /// <summary>
    /// Awaits <paramref name="resolution"/>, or stops the caller waiting once <paramref name="cancellationToken"/> fires;
    /// the resolution continues and decides its slot as usual.
    /// </summary>
    private static ValueTask<AppendOutcome> WaitAsync(ValueTask<AppendOutcome> resolution, CancellationToken cancellationToken) =>
        !cancellationToken.CanBeCanceled || resolution.IsCompleted
            ? resolution
            : WaitCancelableAsync(resolution, cancellationToken);

    private static async ValueTask<AppendOutcome> WaitCancelableAsync(ValueTask<AppendOutcome> resolution, CancellationToken cancellationToken)
    {
        var pending = resolution.AsTask();
        try
        {
            return await pending.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _ = pending.ContinueWith(
                static task => _ = task.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            throw;
        }
    }

    /// <summary>
    /// Answers <see cref="ProposalOutcome.Fenced"/> for a proposal whose slot is never written once every proposal the
    /// writer admitted has ended its append, so it completes after the earlier slots (LG-I5).
    /// </summary>
    private async ValueTask<ProposalOutcome> FencedAfterAdmittedAsync(CancellationToken cancellationToken)
    {
        await _proposals.CloseAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        return FencedOutcome;
    }

    /// <summary>
    /// Appends an encoded batch, whose ownership passes to the slot's resolution, at its granted slot, and decides its
    /// outcome (ledgers.md §8.2).
    /// </summary>
    private async ValueTask<ProposalOutcome> ProposeGrantedAsync(SlotGrant grant, ArcBuffer batch, long reserved, CancellationToken cancellationToken)
    {
        var appended = await WaitAsync(BeginResolution(grant, batch, owned: true, reserved, _proposals), cancellationToken).ConfigureAwait(false);
        if (appended.TryGetValue(out AppendOutcome.Appended durable))
        {
            return new ProposalOutcome(new ProposalOutcome.Durable(durable.Slot));
        }

        // A write which met a seal, or an append which could not complete, stopped the lifetime: the ledger map's recorded
        // T decides the slot, Durable below it and Fenced at or above it (ledgers.md §8.2).
        Slot slot;
        if (appended.TryGetValue(out AppendOutcome.Sealed met))
        {
            slot = met.Slot;
        }
        else if (appended.TryGetValue(out AppendOutcome.Unresolved unresolved) && unresolved.Slot is { } written)
        {
            slot = written;
        }
        else
        {
            return FencedOutcome;
        }

        var recordedTail = await RecordedTailAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        return OutcomeAt(slot, recordedTail);
    }

    /// <summary>
    /// Starts resolving the acquired slot under the writer's admission; the resolution disposes
    /// <paramref name="payload"/> when <paramref name="owned"/>, releases a proposal's <paramref name="reserved"/> bytes
    /// of its budget, and exits the proposal's admission of <paramref name="proposals"/>, which passes to it, when it ends.
    /// </summary>
    private ValueTask<AppendOutcome> BeginResolution(SlotGrant grant, ArcBuffer payload, bool owned, long reserved, AdmissionGate? proposals)
    {
        if (!_admissions.TryEnterUnscoped())
        {
            // Disposal began after the slot was acquired, so the slot is never written, and a recovery ends the ledger at
            // or below it; the disposal's stop keeps it from holding back every later completion.
            Release(payload, owned);
            ReleaseReservation(reserved);
            proposals?.Exit();
            Sequencer.Stop(SequencerStopReason.Requested);
            return UnwrittenAsync();
        }

        return ResolveSlotAsync(grant, payload, owned, reserved, proposals);
    }

    /// <summary>
    /// Answers a slot granted during the disposal and never written once the earlier slots' resolutions have ended, so
    /// the append which wrote nothing completes after them (LG-I5).
    /// </summary>
    private async ValueTask<AppendOutcome> UnwrittenAsync()
    {
        await _admissions.CloseAsync().ConfigureAwait(false);
        return new AppendOutcome(new AppendOutcome.Unresolved(Slot: null));
    }

    /// <summary>
    /// Returns the ledger's recorded <c>T</c> once this process's map view lists the ledger closed, or
    /// <see langword="null"/> once it no longer lists it, following the view from the first call on. After disposal it
    /// decides from the view as it stands, and fails with <see cref="ObjectDisposedException"/> while it lists the ledger
    /// open or does not list it yet.
    /// </summary>
    private Task<Slot?> RecordedTailAsync()
    {
        lock (_lock)
        {
            if (_recordedTail is { } started)
            {
                return started.Task;
            }

            var recordedTail = new TaskCompletionSource<Slot?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _recordedTail = recordedTail;
            if (Volatile.Read(ref _disposed) != 0)
            {
                // Every later proposal shares this one decision of the view (LG-I5).
                SettleFromView(recordedTail);
                return recordedTail.Task;
            }

            // The view serves every proposal, so it is followed in none's execution context (conventions.md §12).
            using (ExecutionContext.SuppressFlow())
            {
                _mapFollowing = Task.Run(() => FollowMapAsync(recordedTail), CancellationToken.None);
            }

            return recordedTail.Task;
        }
    }

    /// <summary>
    /// Starts following the map view after an open check found the ledger sealed, so the writer learns <c>T</c>.
    /// </summary>
    private void FollowMapAfterSeal()
    {
        if (_serving is not null && Volatile.Read(ref _disposed) == 0)
        {
            _ = RecordedTailAsync();
        }
    }

    /// <summary>
    /// Settles <paramref name="recordedTail"/> when <paramref name="value"/> lists the ledger closed, passing its
    /// <c>T</c> to the <see cref="Ledger"/> (LP-I11), or no longer lists it.
    /// </summary>
    /// <returns>Whether <paramref name="value"/> decides it.</returns>
    private bool TrySettle(LedgerMapValue value, TaskCompletionSource<Slot?> recordedTail)
    {
        if (!Decide(value, out var decided))
        {
            return false;
        }

        _ = recordedTail.TrySetResult(decided);
        return true;
    }

    /// <summary>
    /// Learns the ledger's decision from <paramref name="value"/> when it holds one (<see cref="Classify"/>): the
    /// <see cref="Ledger"/> learns a recorded <c>T</c> (LP-I11), and <see cref="Fenced"/> completes.
    /// </summary>
    /// <returns>Whether <paramref name="value"/> decides the ledger.</returns>
    private bool Decide(LedgerMapValue value, out Slot? recordedTail)
    {
        if (!Classify(value, Ledger.Id, out recordedTail))
        {
            return false;
        }

        if (recordedTail is { } t)
        {
            Ledger.ObserveRecordedTail(t);
        }

        _ = _fenced.TrySetResult();
        return true;
    }

    /// <summary>
    /// Returns whether <paramref name="value"/> decides <paramref name="ledger"/>: it lists it closed, with its recorded
    /// <c>T</c>, or has revoked it, before this view learned <c>T</c>; the view only advances, so it then never will.
    /// </summary>
    private static bool Classify(LedgerMapValue value, LedgerId ledger, out Slot? recordedTail)
    {
        recordedTail = null;
        if (value.Get(ledger)?.Status.Value is LedgerStatus.Closed closed)
        {
            recordedTail = closed.T;
            return true;
        }

        return value.NoLongerLists(ledger);
    }

    /// <summary>
    /// Once the lifetime stops for any reason but the writer's disposal, completes <see cref="Fenced"/> and, unless the
    /// writer only appends, is being disposed, or an executor in this process stopped admitting to recover the ledger
    /// itself, helps the recovery the stop means (ledgers.md §8.2). Runs synchronously within the stop.
    /// </summary>
    private void OnStopped(SequencerStopReason reason)
    {
        // The stop ends every creation's wait (ledgers.md §2.4).
        if (_serving is not null)
        {
            OnStoppedCreations();
        }

        if (reason == SequencerStopReason.Requested)
        {
            return;
        }

        _ = _fenced.TrySetResult();
        if (_serving is null)
        {
            return;
        }

        lock (_lock)
        {
            if (_proposals.IsClosed || Volatile.Read(ref _disposed) != 0 || _helping is not null)
            {
                return;
            }

            // The recovery serves the process, so it runs in no caller's execution context (conventions.md §12).
            using (ExecutionContext.SuppressFlow())
            {
                _helping = Task.Run(HelpRecoverAsync, CancellationToken.None);
            }
        }
    }

    /// <summary>
    /// Helps the recovery a stopped lifetime means: while the map view lists the ledger open, recovers it through the
    /// <see cref="Ledger"/> from its known contiguous tail, so a recovery whose seal round begins after this one's seal
    /// completes starts at that tail (ledgers.md §8.2). Its candidate end decides nothing: only the map's <c>T</c> is
    /// passed. The process runs at most <see cref="LedgerWriterHost.MaxHelperRecoveries"/> at once, and a helper beyond
    /// them leaves the recovery to the transition which closes the ledger.
    /// </summary>
    private async Task HelpRecoverAsync()
    {
        var serving = _serving!;
        if (!serving.Host.TryBeginHelping())
        {
            LogHelperSkipped(serving.Host.Logger, Ledger.Id);
            return;
        }

        try
        {
            var view = serving.Map.Current;
            if (view.Get(Ledger.Id) is not { IsOpen: true })
            {
                return;
            }

            // LG-I7: the helper's seals and recovery writes reach the replicas its view lists.
            Ledger.AssertSendsFrom(view);
            _ = await Ledger.RecoverAsync(_disposing.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_disposing.IsCancellationRequested)
        {
            // The writer's disposal ended the helper; the transition which closes the ledger recovers it.
        }
        catch (AggregateException exception)
        {
            // Too few replicas remain able to decide a slot: the transition which closes the ledger recovers it later.
            LogHelperFailed(serving.Host.Logger, Ledger.Id, exception);
        }
        catch (Exception exception) when (!IsProcessCritical(exception))
        {
            Debug.Fail($"A helper's recovery of ledger {Ledger.Id} failed unexpectedly: {exception}");
            throw;
        }
        finally
        {
            serving.Host.EndHelping();
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The primary of ledger {Ledger} could not help its recovery: too few replicas answered.")]
    private static partial void LogHelperFailed(ILogger logger, LedgerId ledger, Exception exception);

    [LoggerMessage(Level = LogLevel.Debug, Message = "The primary of ledger {Ledger} left its recovery to the transition which closes it: the process's helpers are all busy.")]
    private static partial void LogHelperSkipped(ILogger logger, LedgerId ledger);

    private void SettleFromView(TaskCompletionSource<Slot?> recordedTail)
    {
        if (!TrySettle(_serving!.Map.Current, recordedTail))
        {
            _ = recordedTail.TrySetException(new ObjectDisposedException(
                nameof(LedgerWriter),
                "The ledger writer was disposed while its map view listed its ledger open, before the map decided its proposals."));
        }
    }

    /// <summary>
    /// Follows the map view until it decides the writer's undecided proposals: one task waits for the view's changes,
    /// whoever reads the map, and another re-reads the map with bounded, jittered backoff; the first which finds the
    /// ledger closed or revoked ends both. Disposal ends both, and the view as it stands decides.
    /// </summary>
    private async Task FollowMapAsync(TaskCompletionSource<Slot?> recordedTail)
    {
        using var decided = CancellationTokenSource.CreateLinkedTokenSource(_disposing.Token);
        await Task.WhenAll(WatchViewAsync(recordedTail, decided), ReadMapAsync(recordedTail, decided)).ConfigureAwait(false);
        if (!recordedTail.Task.IsCompleted)
        {
            SettleFromView(recordedTail);
        }
    }

    private async Task WatchViewAsync(TaskCompletionSource<Slot?> recordedTail, CancellationTokenSource decided)
    {
        var map = _serving!.Map;
        try
        {
            var value = map.Current;
            while (!TrySettle(value, recordedTail))
            {
                value = await map.WaitForChangeAsync(value.Epoch, decided.Token).ConfigureAwait(false);
            }

            await decided.CancelAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (decided.IsCancellationRequested)
        {
            // The other task decided, or the writer was disposed.
        }
    }

    private async Task ReadMapAsync(TaskCompletionSource<Slot?> recordedTail, CancellationTokenSource decided)
    {
        try
        {
            _ = await _mapReadRetry.ExecuteAsync(
                static async (state, cancellationToken) =>
                {
                    var map = state.Writer._serving!.Map;
                    _ = await map.ReadAsync(cancellationToken).ConfigureAwait(false);
                    return state.Writer.TrySettle(map.Current, state.RecordedTail);
                },
                new MapRead(this, recordedTail),
                decided.Token).ConfigureAwait(false);
            await decided.CancelAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (decided.IsCancellationRequested && !IsProcessCritical(exception))
        {
            // The other task decided, or the writer was disposed, including a transient read failure that raced cancellation.
        }
    }

    /// <summary>
    /// Runs <paramref name="check"/>, then each check queued meanwhile, one at a time, until none is queued.
    /// </summary>
    private async Task RunChecksAsync(TaskCompletionSource<OpenCheckOutcome> check)
    {
        while (true)
        {
            try
            {
                OpenCheckOutcome outcome = (await Ledger.ReadTailQuorumAsync(CancellationToken.None).ConfigureAwait(false)).Value switch
                {
                    LedgerTail { IsSealed: true } => OpenCheckOutcome.Sealed.Instance,
                    LedgerTail open => new OpenCheckOutcome.Open(open.ContiguousTail),
                    ReplicaWriteOutcome.Backpressure backpressure => backpressure,
                    _ => throw new InvalidOperationException("A seal check holds no case."),
                };
                if (outcome.Value is OpenCheckOutcome.Sealed)
                {
                    FollowMapAfterSeal();
                }

                check.SetResult(outcome);
            }
            catch (Exception exception) when (!IsProcessCritical(exception))
            {
                check.SetException(exception);
            }

            lock (_lock)
            {
                _runningCheck = _queuedCheck;
                _queuedCheck = null;
                if (_runningCheck is null)
                {
                    return;
                }

                check = _runningCheck;
            }
        }
    }

    /// <summary>
    /// Writes and completes an acquired slot under the admission its caller took, and exits it, releasing
    /// <paramref name="payload"/> when it owns it.
    /// </summary>
    private async ValueTask<AppendOutcome> ResolveSlotAsync(SlotGrant grant, ArcBuffer payload, bool owned, long reserved, AdmissionGate? proposals)
    {
        LedgerWriterMetrics.RecordOccupancy(delta: 1);
        try
        {
            bool metSeal;
            try
            {
                metSeal = await WriteUntilDecidedAsync(grant.Slot, payload).ConfigureAwait(false);
            }
            catch (Exception exception) when (!IsProcessCritical(exception))
            {
                // LP-I13: the write began, so its batch may be on replicas which a recovery copies below T, and this writer
                // never fills its slot; it stops the lifetime rather than hold back every later completion, and the
                // recovered T decides the append. A failure repetition cannot change stops it with PayloadWriteFailed, and
                // the writer's disposal, which ended a write waiting out an outage, with Requested.
                Sequencer.Stop(
                    exception is OperationCanceledException && _disposing.IsCancellationRequested
                        ? SequencerStopReason.Requested
                        : SequencerStopReason.PayloadWriteFailed);
                return new AppendOutcome(new AppendOutcome.Unresolved(grant.Slot));
            }

            if (metSeal)
            {
                // The write met a sealed or dropped replica, so this slot never completes (LP-I8). Stopping the lifetime
                // makes the writer's other appends observe the seal at once.
                Sequencer.Observer?.Invoke(SequencerStep.Sealed(grant.Slot));
                Sequencer.Stop(SequencerStopReason.Sealing);
                return new AppendOutcome(new AppendOutcome.Sealed(grant.Slot));
            }

            // "In this protocol, the linearization point of an append is determined by the completeSlot."
            // (research/papers/logdrive/paper.md, §4.)
            var completionStarted = _timeProvider.GetTimestamp();
            try
            {
                // Only disposal cancels completion; the caller's cancellation never does.
                await Sequencer.CompleteSlotAsync(grant, _disposing.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (!IsProcessCritical(exception))
            {
                // LP-I13: a writer which cannot establish whether a slot holding a durable batch completed stops the
                // lifetime before it reports. The primary recovers and resolves the append from the recovered end.
                Sequencer.Stop(SequencerStopReason.CompletionFailed);
                return new AppendOutcome(new AppendOutcome.Unresolved(grant.Slot));
            }
            finally
            {
                LedgerWriterMetrics.RecordCompletion(_timeProvider.GetElapsedTime(completionStarted));
            }

            Sequencer.Observer?.Invoke(SequencerStep.Completed(grant.Slot));

            // LP-I11: completion is in order (LP-I3), and the slot completed only after its write was written.
            _ = Ledger.RaiseKnownContiguousTail(new Slot(checked(grant.Slot.Value + 1)));
            ScheduleContiguousTail();
            return new AppendOutcome(new AppendOutcome.Appended(grant.Slot));
        }
        finally
        {
            // A proposed batch is immutable from its proposal on, so its bytes still match their CRC-32C.
            Debug.Assert(proposals is null || BatchView.CrcMatches(payload), "A proposed batch is immutable.");
            Release(payload, owned);
            ReleaseReservation(reserved);
            LedgerWriterMetrics.RecordOccupancy(delta: -1);
            _admissions.Exit();
            proposals?.Exit();
        }
    }

    /// <summary>
    /// Writes this slot's batch until the write is written or meets a seal or a drop.
    /// </summary>
    /// <remarks>
    /// LP-I12: a slot holder repeats the identical write, with bounded, jittered backoff, until it is written or meets a
    /// seal or a drop; storage outages and backpressure delay an append and never end its ledger. A
    /// <see cref="ReplicaWriteOutcome.Backpressure"/> answer asserts nothing, and the repeat at the same slot covers a
    /// batch it may have left on fewer than <c>Q</c> replicas (outcomes.md §1). The retry belongs to the slot's holder
    /// rather than to the ledger: holding the slot makes this writer its only writer, so repeating the identical write is
    /// harmless (LR-I1), while the ledger's write fans out one write per replica. Each attempt carries the known
    /// contiguous tail when it starts (LP-I11). It fails with a failure repetition cannot change
    /// (<see cref="LedgerReplicaFailures.IsTransient"/>), or with the cancellation of the writer's disposal.
    /// </remarks>
    /// <returns>Whether the write met a seal or a drop: <see langword="false"/> once it is written.</returns>
    private async ValueTask<bool> WriteUntilDecidedAsync(Slot slot, ArcBuffer payload)
    {
        var writeStarted = _timeProvider.GetTimestamp();
        if (_serving is not null)
        {
            // LG-I7: the primary writes only to the replicas its view lists with the ledger (ledgers.md §8.1, §8.5).
            Ledger.AssertSendsFrom(_serving.Map.Current);
        }

        var cancellationToken = _disposing.Token;
        try
        {
            // Fast path: run attempt 0 directly without entering Polly; enter _writeRetry only when attempt 0 answers
            // Backpressure or throws a repeatable transient failure (LP-I12).
            cancellationToken.ThrowIfCancellationRequested();
            NoteWrite();
            LedgerWriteOutcome outcome;
            try
            {
                outcome = await Ledger.WriteAsync(slot, payload, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (StorageOutageRetry.IsRepeatable(exception, cancellationToken))
            {
                return await RetryWriteUntilDecidedAsync(slot, payload, cancellationToken).ConfigureAwait(false);
            }

            if (outcome is ReplicaWriteOutcome.Written)
            {
                return false;
            }

            if (outcome is ReplicaWriteOutcome.Sealed)
            {
                return true;
            }

            if (outcome is ReplicaWriteOutcome.Backpressure)
            {
                return await RetryWriteUntilDecidedAsync(slot, payload, cancellationToken).ConfigureAwait(false);
            }

            throw new UnreachableException("conventions.md §15: a write answers a union which holds a case.");
        }
        finally
        {
            LedgerWriterMetrics.RecordWrite(_timeProvider.GetElapsedTime(writeStarted));
        }
    }

    private async ValueTask<bool> RetryWriteUntilDecidedAsync(
        Slot slot,
        ArcBuffer payload,
        CancellationToken cancellationToken)
    {
        var delay = Backoff.Delay(0, StorageOutageRetry.BaseDelay, StorageOutageRetry.MaximumDelay, Random.Shared.NextDouble());
        await Task.Delay(delay, _timeProvider, cancellationToken).ConfigureAwait(false);
        var outcome = await _writeRetry.ExecuteAsync(
            static (state, ct) =>
            {
                state.Writer.NoteWrite();
                return state.Writer.Ledger.WriteAsync(state.Slot, state.Payload, ct);
            },
            new WriteAttempt(this, slot, payload),
            cancellationToken).ConfigureAwait(false);
        return outcome switch
        {
            ReplicaWriteOutcome.Written => false,
            ReplicaWriteOutcome.Sealed => true,
            ReplicaWriteOutcome.Backpressure => throw new UnreachableException("LP-I12: the writer repeats a write which answers Backpressure."),
            null => throw new UnreachableException("conventions.md §15: a write answers a union which holds a case."),
        };
    }

    private readonly record struct WriteAttempt(LedgerWriter Writer, Slot Slot, ArcBuffer Payload);

    private readonly record struct MapRead(LedgerWriter Writer, TaskCompletionSource<Slot?> RecordedTail);

    /// <summary>
    /// What a writer's proposals need, which the reader's lease of its ledger supplies (ledgers.md §2.4, §2.5): the
    /// ledger's range, the process's map view, what the process lends every served writer, and the lease the writer
    /// holds on its <see cref="Ledger"/> until it is disposed.
    /// </summary>
    internal sealed record Serving(KeyRange Range, LedgerMapView Map, LedgerWriterHost Host, LedgerReader.LedgerLease Lease);
}

/// <summary>
/// What a process lends each writer it serves (ledgers.md §2.4): the replica's payload limit, the log of the writers'
/// helper recoveries, and the process-wide bound on the helper recoveries running at once (ledgers.md §8.2).
/// </summary>
/// <param name="maxBatchLength">The replica's payload limit, which the composition takes from its replica transport.</param>
/// <param name="logger">Receives the helper recoveries which fail.</param>
/// <param name="options">The cluster's writer options, or the defaults.</param>
/// <param name="random">The process's seeded random source of its writers' segment ages, or an unseeded one.</param>
/// <param name="segmentEnded">The sink of each segment a writer ends, or <see langword="null"/>.</param>
internal sealed class LedgerWriterHost(int maxBatchLength, ILogger logger, LedgerWriterOptions? options = null, Random? random = null, Action<LedgerSegmentKeys>? segmentEnded = null)
{
    /// <summary>
    /// Gets the sink each writer gives every segment it ends, with the keys it defined, at the start of the next segment and
    /// at its disposal (ledgers.md §2.4), or <see langword="null"/>. Called under the writer's lock: it must not block or
    /// call the writer.
    /// </summary>
    public Action<LedgerSegmentKeys>? OnSegmentEnded { get; } = segmentEnded;

    /// <summary>The most helper recoveries a process runs at once.</summary>
    internal const int MaxHelperRecoveries = 32;

    private readonly Random _random = random ?? new Random();
    private readonly Lock _lock = new();
    private int _helping;

    /// <summary>Gets the replica's payload limit.</summary>
    public int MaxBatchLength { get; } = maxBatchLength > 0 ? maxBatchLength : throw new ArgumentOutOfRangeException(nameof(maxBatchLength));

    /// <summary>Gets the writer options of the cluster, which every writer the process serves follows.</summary>
    public LedgerWriterOptions Options { get; } = options ?? LedgerWriterOptions.Default;

    /// <summary>
    /// Draws a writer's segment age from the process's seeded random source, uniformly between nine tenths of
    /// <see cref="LedgerWriterOptions.MaxSegmentAge"/> and it, so the segments of ledgers created together do not end
    /// together (ledgers.md §8.2).
    /// </summary>
    public TimeSpan DrawSegmentAge()
    {
        double fraction;
        lock (_lock)
        {
            fraction = _random.NextDouble();
        }

        var maximum = Options.MaxSegmentAge;
        return maximum - maximum / 10 * fraction;
    }

    /// <summary>Gets the log of the writers' helper recoveries.</summary>
    public ILogger Logger { get; } = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>Begins a helper recovery unless <see cref="MaxHelperRecoveries"/> are running.</summary>
    public bool TryBeginHelping()
    {
        if (Interlocked.Increment(ref _helping) <= MaxHelperRecoveries)
        {
            return true;
        }

        EndHelping();
        return false;
    }

    /// <summary>Ends a helper recovery <see cref="TryBeginHelping"/> began.</summary>
    public void EndHelping() => _ = Interlocked.Decrement(ref _helping);
}
