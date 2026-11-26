using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks.Sources;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Threading;
using Polly;

namespace Orleans.Durable.Storage.Engine.Ledgers;

/// <summary>
/// The quorum operations over one ledger's <c>N</c> replicas with quorum <c>Q</c>, following Apache BookKeeper's ledger
/// protocol: the writer's writes, reads, the seal check, and recovery (ledger-protocol.md §2.1). It is the ledger storage
/// of a ledger on nodes (<see cref="ILedgerStorage"/>, ledger-protocol.md §2.7): a <see cref="Ledger"/> composes one,
/// carrying its known contiguous tail into each operation and learning from each answer.
/// </summary>
/// <remarks>
/// <para>
/// One writer gives each slot one batch, so the quorum arbitrates nothing: <c>Q</c> replicas holding a writer write
/// decide it, any copy of a decided slot is its batch, and sealing <c>N − Q + 1</c> replicas ends the writer (LP-I4,
/// LP-I6). It counts only its replicas' answers, each of which reflects durable state (LP-I17), a replica's
/// <c>Dropped</c> as a sealed answer (LP-I5), and its <c>Backpressure</c> as no answer. An operation which cannot decide
/// follows one rule (ledger-protocol.md §6.4): a read answers <c>Dropped</c> when a replica answered it; then every
/// operation fails with the failures repetition cannot change once <c>N − Q + 1</c> replicas failed so, answers
/// <c>Backpressure</c> when a replica answered it, and otherwise fails with every failure.
/// </para>
/// <para>
/// A read below the known contiguous tail asks one replica at a time, by the policy of ledger-protocol.md §8.3 and §8.4;
/// every other operation goes to every replica (LedgerQuorum.Reads.cs).
/// </para>
/// <para>
/// It keeps no contiguous tail: each operation carries the one its caller passes to every replica it asks, and the seal
/// check and recovery return the greatest the replicas answered (LP-I11).
/// </para>
/// <para>
/// An operation returns once its decision is reached; the replica operations still running past it, its stragglers,
/// finish tracked, so that disposal waits for them and releases their buffers (ledger-protocol.md §6.3). Every replica
/// operation runs under a token which the quorum's disposal cancels, so disposal ends them rather than waiting for
/// whatever bounds them at their transport.
/// </para>
/// </remarks>
internal sealed partial class LedgerQuorum : ILedgerStorage
{
    private const int StragglersClosed = int.MinValue;

    private readonly ILedgerReplica[] _replicas;
    private readonly bool _supportsReadRange;
    private readonly bool _supportsReadSlots;
    private readonly TimeProvider _timeProvider;
    private readonly Action<RecoveryStep>? _recoveryObserver;
    private readonly Func<ArcBuffer, bool>? _verifyBatch;
    private readonly AdmissionGate _operations = new();
    private readonly CancellationTokenSource _lifetime = new();

    // Cancelled once disposal begins: every write's replica calls run under it, so disposal ends them however long their
    // caller would wait, as the cancelled lifetime ends the reads' (ledger-protocol.md §6.3).
    private readonly CancellationTokenSource _disposing = new();
    private readonly Lock _disposeLock = new();
    private readonly TaskCompletionSource _stragglersDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CachedWriteToken? _cachedWriteToken;
    private ReplicaWriteOutcome.Written _cachedWrittenGrant = ReplicaWriteOutcome.Written.Instance;
    private Task? _disposalTask;
    private int _writesCancelled;
    private int _stragglerState;
    private Exception? _stragglerFailure;
    private RecoveryRetries? _recoveryRetries;
    private long _trackedStragglers;

    /// <summary>
    /// Initializes a new instance of the <see cref="LedgerQuorum"/> class over a ledger's <c>N</c> replicas, in the ledger
    /// map's order, which it owns and disposes. Construction performs no I/O.
    /// </summary>
    /// <param name="replicas">
    /// The reference-distinct replicas, at least one, each opened with <paramref name="options"/>, so that every replica
    /// records the ledger's window <c>K</c>.
    /// </param>
    /// <param name="quorum">The quorum <c>Q</c>, with <c>1 ≤ Q ≤ N</c> (LP-I4).</param>
    /// <param name="options">The ledger's runtime options, whose window bounds recovery's reads (LP-I7).</param>
    /// <param name="timeProvider">The clock of recovery's backoff and of the hedges of reads below the tail.</param>
    /// <param name="preferredReplica">
    /// The index among <paramref name="replicas"/> of the replica a read below the known contiguous tail asks first, the
    /// one on the caller's own node, or <see langword="null"/> when the caller's node holds none (ledger-protocol.md §8.4).
    /// </param>
    /// <param name="readRotation">
    /// Where the rotation of the first replica a read below the tail asks starts, when the quorum has no preferred replica.
    /// </param>
    /// <param name="recoveryObserver">
    /// The observer of every recovery's steps, which receives each replica answer a recovery handles, serially and before the
    /// recovery acts on it, or <see langword="null"/>; deterministic simulation logs them for trace validation
    /// (test/Durable.Storage/docs/spec/verification.md §5.1).
    /// </param>
    /// <param name="verifyBatch">
    /// Whether a batch's bytes pass the integrity check of the format above the protocol, which read repair and recovery
    /// require of any batch they copy (ledger-protocol.md §8.3); or <see langword="null"/>, when read repair repairs
    /// nothing and recovery treats batches as opaque.
    /// </param>
    public LedgerQuorum(
        IEnumerable<ILedgerReplica> replicas,
        int quorum,
        LedgerReplicaOptions options,
        TimeProvider timeProvider,
        int? preferredReplica = null,
        uint readRotation = 0,
        Action<RecoveryStep>? recoveryObserver = null,
        Func<ArcBuffer, bool>? verifyBatch = null)
    {
        ArgumentNullException.ThrowIfNull(replicas);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        LedgerReplicaOperations.ValidateWindowSize(options.WindowSize);
        _replicas = [.. replicas];
        _supportsReadRange = _replicas.Length > 0 && _replicas.All(static r => r.SupportsReadRange);
        _supportsReadSlots = _replicas.Length > 0 && _replicas.All(static r => r.SupportsReadSlots);
        if (_replicas.Length == 0)
        {
            throw new ArgumentException("A ledger has at least one replica.", nameof(replicas));
        }

        if (_replicas.Any(static replica => replica is null))
        {
            throw new ArgumentException("Replicas cannot contain null values.", nameof(replicas));
        }

        if (_replicas.Distinct(ReferenceEqualityComparer.Instance).Count() != _replicas.Length)
        {
            throw new ArgumentException("Replicas must be reference-distinct instances.", nameof(replicas));
        }

        // LP-I4: "A Ledger over N replicas has a quorum Q with 1 ≤ Q ≤ N."
        ArgumentOutOfRangeException.ThrowIfLessThan(quorum, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(quorum, _replicas.Length);
        if (preferredReplica is { } preferred)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(preferred, nameof(preferredReplica));
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(preferred, _replicas.Length, nameof(preferredReplica));
        }

        Quorum = quorum;
        WindowSize = options.WindowSize;
        _timeProvider = timeProvider;
        _preferredReplica = preferredReplica;
        _readRotation = readRotation % (uint)_replicas.Length;
        _recoveryObserver = recoveryObserver;
        _verifyBatch = verifyBatch;
    }

    /// <summary>
    /// Creates the quorum over the replicas of a ledger a map lists on <paramref name="nodes"/>, in the map's order, so
    /// that <c>replicas[i]</c> is the replica on <c>nodes[i]</c>, which it owns and disposes. The replica on
    /// <paramref name="localNode"/>, when the map lists one there, is the preferred replica of its reads below the known
    /// contiguous tail (ledger-protocol.md §8.4); otherwise the first replica of such a read rotates from a start the
    /// ledger's ID derives, so first reads of different ledgers spread across their replicas. Every recovery calls
    /// <paramref name="recoveryObserver"/>, when given, with each replica answer it handles (test/Durable.Storage/docs/spec/verification.md
    /// §5.1). Read repair and recovery copy only a batch <paramref name="verifyBatch"/> accepts.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="nodes"/> is empty, or does not pair one node with each replica.
    /// </exception>
    internal static LedgerQuorum ForNodes(
        LedgerId ledger,
        ImmutableArray<NodeId> nodes,
        IEnumerable<ILedgerReplica> replicas,
        int quorum,
        LedgerReplicaOptions options,
        TimeProvider timeProvider,
        NodeId? localNode,
        Action<RecoveryStep>? recoveryObserver = null,
        Func<ArcBuffer, bool>? verifyBatch = null)
    {
        if (nodes.IsDefaultOrEmpty)
        {
            throw new ArgumentException("A ledger on nodes names the node of each replica.", nameof(nodes));
        }

        ILedgerReplica[] opened = [.. replicas];
        if (opened.Length != nodes.Length)
        {
            throw new ArgumentException("A ledger's nodes and replicas pair up in the map's order.", nameof(replicas));
        }

        return new LedgerQuorum(
            opened,
            quorum,
            options,
            timeProvider,
            localNode is { } node && nodes.IndexOf(node) is >= 0 and var preferred ? preferred : null,
            ReadRotation(ledger),
            recoveryObserver,
            verifyBatch);
    }

    /// <summary>
    /// Gets the number of replicas, <c>N</c>.
    /// </summary>
    public int ReplicaCount => _replicas.Length;

    /// <summary>
    /// Gets the ledger's window <c>K</c>, which bounds recovery's reads (LP-I7) and which its writer's sequencer and its
    /// replicas share.
    /// </summary>
    public int WindowSize { get; }

    /// <summary>
    /// Gets the quorum <c>Q</c>: a writer write is written once <c>Q</c> replicas answered written (LP-I4).
    /// </summary>
    public int Quorum { get; }

    /// <summary>
    /// Gets the number of replica operations left running past an operation's decision, whose completion the quorum
    /// tracks and whose buffers it releases (ledger-protocol.md §6.3), which a test reads to show that its run left
    /// stragglers.
    /// </summary>
    public long TrackedStragglers => Interlocked.Read(ref _trackedStragglers);

    /// <summary>
    /// Returns where the rotation of a ledger's first replica to read starts: a Fibonacci hash of its ID, which is
    /// deterministic and spreads consecutive IDs across replicas.
    /// </summary>
    private static uint ReadRotation(LedgerId id) => (uint)(unchecked(id.Value * 0x9E3779B97F4A7C15UL) >> 32);

    /// <summary>
    /// Gets the coverage <c>N − Q + 1</c>: that many replicas hold a property exactly when no <c>Q</c> replicas lack it.
    /// </summary>
    private int Coverage => _replicas.Length - Quorum + 1;

    /// <summary>
    /// Checks the seal over <c>Q</c> replicas, carrying <paramref name="contiguousTail"/> (LP-I9, LP-I11).
    /// </summary>
    /// <param name="contiguousTail">The ledger's known contiguous tail (LP-I11).</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>
    /// A <see cref="LedgerTail"/>: the greatest of <paramref name="contiguousTail"/> and the contiguous tails the answering
    /// replicas reported, and whether a replica answered sealed or dropped; its <see cref="LedgerTail.IsSealed"/> false
    /// proves no recovery of the ledger completed before the call began. Or, when it cannot decide,
    /// <see cref="ReplicaWriteOutcome.Backpressure"/> when a replica answered it (ledger-protocol.md §6.4).
    /// </returns>
    /// <remarks>
    /// One sealed or dropped answer decides it sealed: the replica's seal or drop is durable (LR-I4, LP-I5), and the tails
    /// answered so far are known contiguous tails. Otherwise it answers unsealed once <c>Q</c> replicas answered unsealed.
    /// The remaining replicas' operations finish as stragglers, so the call carries the contiguous tail to every replica.
    /// </remarks>
    public async ValueTask<LedgerTailRead> CheckAsync(Slot contiguousTail, CancellationToken cancellationToken = default)
    {
        using var admission = Admit();
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfReadsStopped();
        var pending = new List<Task<ReplicaTailRead>>(_replicas.Length);
        var unsealed = 0;
        var backpressure = false;
        var tail = contiguousTail;
        var errors = new List<Exception>();
        List<Exception>? permanentErrors = null;
        using var scope = new CallScope(cancellationToken, _lifetime.Token);
        var token = scope.Token;
        try
        {
            foreach (var replica in _replicas)
            {
                pending.Add(Invoke(() => replica.ReadTailAsync(contiguousTail, seal: false, token)));
            }

            while (pending.Count > 0)
            {
                var completed = await Task.WhenAny(pending).WaitAsync(token).ConfigureAwait(false);
                _ = pending.Remove(completed);
                if (!completed.IsCompletedSuccessfully)
                {
                    token.ThrowIfCancellationRequested();
                    AddFailure(completed, errors, ref permanentErrors);
                    continue;
                }

                switch (await completed.ConfigureAwait(false))
                {
                    case LedgerReplicaTail { IsSealed: true } answer:
                        return new LedgerTail(Max(tail, answer.ContiguousTail), IsSealed: true);
                    case LedgerReplicaTail answer:
                        tail = Max(tail, answer.ContiguousTail);
                        if (++unsealed >= Quorum)
                        {
                            return new LedgerTail(tail, IsSealed: false);
                        }

                        break;
                    case ReplicaWriteOutcome.Dropped:
                        // LP-I5: a dropped answer is a sealed one, which carries no contiguous tail.
                        return new LedgerTail(tail, IsSealed: true);
                    case ReplicaWriteOutcome.Backpressure:
                        backpressure = true;
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
            scope.Outlive(TrackStragglers(pending));
        }

        _ = NoQuorum("seal check", permanentErrors, pending: 0);
        return backpressure ? ReplicaWriteOutcome.Backpressure.Instance : throw Unavailable("seal check", errors);
    }

    /// <summary>
    /// Sends <paramref name="contiguousTail"/> to every replica as a contiguous-tail read which does not seal, which each
    /// replica that holds the ledger retains (LR-I6), so a reader in another process learns it from any one replica and
    /// reads the ledger's last batches from one replica (LP-I10, ledger-protocol.md §8.2). Its calls run as stragglers
    /// under the quorum's lifetime, which a stop of its reads or its disposal ends, and their answers assert nothing: a
    /// replica which missed one learns the tail from the ledger's next operation. Once the quorum's reads stopped, it
    /// sends nothing.
    /// </summary>
    /// <param name="contiguousTail">The ledger's known contiguous tail (LP-I11).</param>
    public void WriteContiguousTail(Slot contiguousTail)
    {
        using var admission = _operations.TryEnter();
        if (!admission.Entered || _lifetime.IsCancellationRequested)
        {
            return;
        }

        var token = _lifetime.Token;
        var sent = new Task<ReplicaTailRead>[_replicas.Length];
        for (var replica = 0; replica < _replicas.Length; replica++)
        {
            var target = _replicas[replica];
            sent[replica] = Invoke(() => target.ReadTailAsync(contiguousTail, seal: false, token));
        }

        _ = TrackStragglers(sent);
    }

    /// <summary>
    /// Learns a contiguous tail from the preferred replica, the one on the caller's node, with a contiguous-tail read which
    /// does not seal and carries <paramref name="contiguousTail"/>: the replica retains the greater of the two and answers
    /// it, which is at most the ledger's contiguous tail (LR-I6, LP-I11), so a replica node learns the tail its ledger's
    /// writes and explicit contiguous-tail writes carried without a seal check (ledger-protocol.md §8.2, the ledger model's
    /// <c>LearnTail</c>). A quorum with no preferred replica sends nothing.
    /// </summary>
    /// <param name="contiguousTail">The ledger's known contiguous tail (LP-I11).</param>
    /// <param name="cancellationToken">Stops the learn.</param>
    /// <returns>
    /// The greater of <paramref name="contiguousTail"/> and the replica's retained contiguous tail; or
    /// <paramref name="contiguousTail"/> when the replica answered <c>Dropped</c> or <c>Backpressure</c>, or failed, which
    /// teaches nothing, or when there is no preferred replica.
    /// </returns>
    /// <exception cref="ObjectDisposedException">The quorum's reads stopped, or it was disposed.</exception>
    public async ValueTask<Slot> LearnTailAsync(Slot contiguousTail, CancellationToken cancellationToken = default)
    {
        using var admission = Admit();
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfReadsStopped();
        if (_preferredReplica is not { } preferred)
        {
            return contiguousTail;
        }

        using var scope = new CallScope(cancellationToken, _lifetime.Token);
        var token = scope.Token;
        var replica = _replicas[preferred];
        try
        {
            return await Invoke(() => replica.ReadTailAsync(contiguousTail, seal: false, token)).ConfigureAwait(false) is LedgerReplicaTail answer
                ? Max(contiguousTail, answer.ContiguousTail)
                : contiguousTail;
        }
        catch (OperationCanceledException) when (ReadsStopped(cancellationToken))
        {
            throw ReadsStoppedException();
        }
        catch (Exception exception) when (exception is not (OperationCanceledException or OutOfMemoryException))
        {
            // A failed replica teaches nothing; the caller learns again later.
            return contiguousTail;
        }
    }

    /// <summary>
    /// Stops the quorum's reads: every read, seal check, and recovery, admitted or later, fails with
    /// <see cref="ObjectDisposedException"/> once the replica calls it awaits end under the cancelled lifetime, while
    /// writes go on until the quorum's disposal ends their replica calls (ledger-protocol.md §6.3). Its owner's disposal
    /// calls it for a quorum another caller may still lease, so a lease held across a slow replica bounds no disposal.
    /// </summary>
    /// <returns>A task which completes once the cancellation's callbacks ran.</returns>
    public Task StopReadsAsync()
    {
        lock (_disposeLock)
        {
            // Disposal cancels the lifetime itself, and disposes it once the operations drained.
            return _disposalTask is null ? _lifetime.CancelAsync() : Task.CompletedTask;
        }
    }

    /// <summary>
    /// Disposes the quorum and the replicas it owns.
    /// </summary>
    /// <remarks>
    /// Disposal closes admission, stops the reads, seal checks, and recoveries in progress, which fail with
    /// <see cref="ObjectDisposedException"/> (<see cref="StopReadsAsync"/>), and cancels the replica calls of every write,
    /// so an admitted write fails with <see cref="ObjectDisposedException"/> once its calls end before its replicas decide
    /// it; it then waits for the admitted operations and the replica operations they left running, releases their
    /// buffers, and only then disposes the replicas, reporting their failures (ledger-protocol.md §6.3). A cancelled call
    /// may still land, so its outcome is unknown, and a write it ends reports nothing written. A recovery repeats a
    /// replica's operations until they answer, so without the stop a recovery over unreachable replicas would hold
    /// disposal forever; and a replica call which disposal did not cancel would hold it for as long as its transport
    /// waits for an answer which may never come.
    /// </remarks>
    /// <returns>A task which completes once the replicas are disposed.</returns>
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _writesCancelled, 1) == 0)
        {
            // Off the lock, before the disposal which releases the source begins: the cancellation's callbacks end the
            // writes' replica calls, whose continuations may run inline.
            _disposing.Cancel();
        }

        lock (_disposeLock)
        {
            _disposalTask ??= CompleteDisposalAsync(_operations.CloseAsync());
            return new ValueTask(_disposalTask);
        }
    }

    /// <summary>
    /// Writes <paramref name="payload"/> at <paramref name="slot"/> as the writer, carrying
    /// <paramref name="contiguousTail"/> (LP-I4).
    /// </summary>
    /// <param name="slot">The slot.</param>
    /// <param name="payload">The batch, which the caller keeps; the write retains a zero-copy lease until every replica call ends.</param>
    /// <param name="contiguousTail">The ledger's known contiguous tail (LP-I11).</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>
    /// <see cref="ReplicaWriteOutcome.Written"/> at <c>Q</c> written answers; <see cref="ReplicaWriteOutcome.Sealed"/> at
    /// the first sealed or dropped answer while fewer than <c>Q</c> answered written (LP-I4, LP-I5); or, when it can no
    /// longer reach <c>Q</c>, <see cref="ReplicaWriteOutcome.Backpressure"/> when a replica answered it, which asserts
    /// nothing: the write may have left a batch on fewer than <c>Q</c> replicas, as a failed write may, and the writer
    /// repeats it (LP-I12, ledger-protocol.md §6.4).
    /// </returns>
    /// <remarks>
    /// "On a write, the QuorumDrive forwards the command to all replicas." (research/papers/logdrive/paper.md, §3.4.) A
    /// write which can no longer reach <c>Q</c> fails with an <see cref="AggregateException"/>: of only the failures which
    /// repetition cannot change once <c>N − Q + 1</c> replicas failed so, since no <c>Q</c> replicas can ever take the
    /// write; otherwise it answers <see cref="ReplicaWriteOutcome.Backpressure"/> when a replica answered backpressure, and
    /// fails with every failure, a transient one among them; it waits for the replicas still running only while their
    /// answers decide which it reports (ledger-protocol.md §8.3; LP-I12). Its replica calls run under a token which its
    /// caller's token and the quorum's disposal both cancel, and which its stragglers keep to their end; a write whose
    /// replica call the disposal ended fails with <see cref="ObjectDisposedException"/>, which asserts nothing.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">
    /// The quorum was disposed before the write was admitted, which then reached no replica; or its disposal ended the
    /// write's replica calls before their answers decided it. The write then asserts nothing: it may have landed on any
    /// of its replicas, as a failed write may, so its caller treats it as unknown, never as absent (LP-I12, LP-I13).
    /// </exception>
    public ValueTask<LedgerWriteOutcome> WriteAsync(
        Slot slot,
        ArcBuffer payload,
        Slot contiguousTail,
        CancellationToken cancellationToken = default)
    {
        if (!_operations.TryEnterUnscoped())
        {
            return ValueTask.FromException<LedgerWriteOutcome>(new ObjectDisposedException(GetType().FullName));
        }

        try
        {
            LedgerReplicaOperations.ValidateSlot(slot);
            LedgerReplicaOperations.ValidateBatch(payload);
            cancellationToken.ThrowIfCancellationRequested();
        }
#pragma warning disable CA1031 // Return validation and cancellation failures as faulted ValueTasks, matching async ValueTask semantics.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            _operations.Exit();
            return ValueTask.FromException<LedgerWriteOutcome>(exception);
        }

        return WriteAdmittedAsync(slot, payload, contiguousTail, cancellationToken);
    }

    private ValueTask<LedgerWriteOutcome> WriteAdmittedAsync(
        Slot slot,
        ArcBuffer payload,
        Slot contiguousTail,
        CancellationToken cancellationToken)
    {
        // Retain one zero-copy lease shared by every replica call (conventions.md §1, BUF-I4), preserving identical
        // (First, Offset, Length) across replicas while keeping pages pinned until the last straggler finishes.
        ArcBuffer retained = default;
        CancellationTokenSource? perCallLinked = null;
        CancellationToken token;
        try
        {
            retained = payload.Retain();
            token = AcquireWriteToken(cancellationToken, out perCallLinked);
        }
#pragma warning disable CA1031 // Return sync failures as faulted ValueTasks, matching async ValueTask semantics.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            perCallLinked?.Dispose();
            retained.Dispose();
            _operations.Exit();
            return ValueTask.FromException<LedgerWriteOutcome>(exception);
        }

        var replicas = _replicas;
        var sharedPayload = retained;
        WriteCoordinator coordinator;
        try
        {
            ValueTask<ReplicaWriteOutcome> firstAttempt;
            try
            {
                firstAttempt = replicas[0].WriteAsync(slot, sharedPayload, contiguousTail, recovery: false, token);
            }
#pragma warning disable CA1031 // Any synchronous replica failure participates in quorum accounting.
            catch (Exception exception)
#pragma warning restore CA1031
            {
                var transferredPayload = retained;
                var transferredLinked = perCallLinked;
                retained = default;
                perCallLinked = null;
                return WriteAdmittedSyncStartAsync(
                    slot,
                    transferredPayload,
                    contiguousTail,
                    token,
                    transferredLinked,
                    cancellationToken,
                    firstAttempt: default,
                    firstFailure: exception);
            }

            if (firstAttempt.IsCompleted)
            {
                var transferredPayload = retained;
                var transferredLinked = perCallLinked;
                retained = default;
                perCallLinked = null;
                return WriteAdmittedSyncStartAsync(
                    slot,
                    transferredPayload,
                    contiguousTail,
                    token,
                    transferredLinked,
                    cancellationToken,
                    firstAttempt,
                    firstFailure: null);
            }

            // Async fast path (WAL/network replicas): return WriteCoordinator's ValueTask directly without allocating
            // an async state-machine box for WriteAdmittedAsync, while every ValueTask await stays inside an
            // IAsyncStateMachine (WriteCoordinator.ObserveAsync) for Clockwork compatibility (verification.md §7.1).
            coordinator = new WriteCoordinator(
                this,
                retained,
                perCallLinked,
                cancellationToken,
                remaining: replicas.Length,
                written: 0,
                minGrantTicks: long.MaxValue,
                backpressure: false,
                decided: false,
                decidedOutcome: null,
                decidedException: null,
                errors: null,
                permanentErrors: null);
            retained = default;
            perCallLinked = null;
            coordinator.Attach(firstAttempt);
        }
        finally
        {
            perCallLinked?.Dispose();
            retained.Dispose();
        }

        for (var i = 1; i < replicas.Length; i++)
        {
            ValueTask<ReplicaWriteOutcome> attempt;
            try
            {
                attempt = replicas[i].WriteAsync(slot, sharedPayload, contiguousTail, recovery: false, token);
            }
#pragma warning disable CA1031 // Any synchronous replica failure participates in quorum accounting.
            catch (Exception exception)
#pragma warning restore CA1031
            {
                coordinator.OnSyncFailure(exception);
                continue;
            }

            coordinator.Attach(attempt);
        }

        return coordinator.CompleteFanOut();
    }

    private async ValueTask<LedgerWriteOutcome> WriteAdmittedSyncStartAsync(
        Slot slot,
        ArcBuffer retained,
        Slot contiguousTail,
        CancellationToken token,
        CancellationTokenSource? perCallLinked,
        CancellationToken cancellationToken,
        ValueTask<ReplicaWriteOutcome> firstAttempt,
        Exception? firstFailure)
    {
        var sharedPayload = retained;
        var replicas = _replicas;
        var remaining = replicas.Length;
        var written = 0;
        var minGrantTicks = long.MaxValue;
        var backpressure = false;
        var decided = false;
        LedgerWriteOutcome? decidedOutcome = null;
        Exception? decidedException = null;
        List<Exception>? errors = null;
        List<Exception>? permanentErrors = null;
        WriteCoordinator? coordinator = null;

        try
        {
            remaining--;
            if (firstFailure is not null)
            {
                RecordWriteFailure(
                    firstFailure,
                    cancellationToken,
                    remaining,
                    written,
                    backpressure,
                    ref errors,
                    ref permanentErrors,
                    ref decided,
                    ref decidedOutcome,
                    ref decidedException);
            }
            else
            {
                ReplicaWriteOutcome firstOutcome = default;
                Exception? firstAwaitFailure = null;
                try
                {
                    // Inside an async ValueTask method, awaiting an already-completed ValueTask completes inline
                    // without allocating a state machine box and stays Clockwork-compatible (verification.md §7.1).
                    firstOutcome = await firstAttempt.ConfigureAwait(false);
                }
#pragma warning disable CA1031 // Faulted or canceled ValueTasks participate in quorum accounting.
                catch (Exception exception)
#pragma warning restore CA1031
                {
                    firstAwaitFailure = exception;
                }

                if (firstAwaitFailure is not null)
                {
                    RecordWriteFailure(
                        firstAwaitFailure,
                        cancellationToken,
                        remaining,
                        written,
                        backpressure,
                        ref errors,
                        ref permanentErrors,
                        ref decided,
                        ref decidedOutcome,
                        ref decidedException);
                }
                else
                {
                    RecordWriteOutcome(
                        firstOutcome,
                        remaining,
                        ref written,
                        ref minGrantTicks,
                        ref backpressure,
                        errors,
                        permanentErrors,
                        ref decided,
                        ref decidedOutcome,
                        ref decidedException);
                }
            }

            for (var i = 1; i < replicas.Length; i++)
            {
                ValueTask<ReplicaWriteOutcome> attempt;
                try
                {
                    attempt = replicas[i].WriteAsync(slot, sharedPayload, contiguousTail, recovery: false, token);
                }
#pragma warning disable CA1031 // Any synchronous replica failure participates in quorum accounting.
                catch (Exception exception)
#pragma warning restore CA1031
                {
                    remaining--;
                    if (coordinator is null)
                    {
                        if (!decided)
                        {
                            RecordWriteFailure(
                                exception,
                                cancellationToken,
                                remaining,
                                written,
                                backpressure,
                                ref errors,
                                ref permanentErrors,
                                ref decided,
                                ref decidedOutcome,
                                ref decidedException);
                        }
                    }
                    else
                    {
                        coordinator.OnSyncFailure(exception);
                    }

                    continue;
                }

                if (attempt.IsCompleted)
                {
                    remaining--;
                    ReplicaWriteOutcome outcome;
                    try
                    {
                        outcome = await attempt.ConfigureAwait(false);
                    }
#pragma warning disable CA1031 // Faulted or canceled ValueTasks participate in quorum accounting.
                    catch (Exception exception)
#pragma warning restore CA1031
                    {
                        if (coordinator is null)
                        {
                            if (!decided)
                            {
                                RecordWriteFailure(
                                    exception,
                                    cancellationToken,
                                    remaining,
                                    written,
                                    backpressure,
                                    ref errors,
                                    ref permanentErrors,
                                    ref decided,
                                    ref decidedOutcome,
                                    ref decidedException);
                            }
                        }
                        else
                        {
                            coordinator.OnSyncFailure(exception);
                        }

                        continue;
                    }

                    if (coordinator is null)
                    {
                        if (!decided)
                        {
                            RecordWriteOutcome(
                                outcome,
                                remaining,
                                ref written,
                                ref minGrantTicks,
                                ref backpressure,
                                errors,
                                permanentErrors,
                                ref decided,
                                ref decidedOutcome,
                                ref decidedException);
                        }
                    }
                    else
                    {
                        coordinator.OnSyncOutcome(outcome);
                    }
                }
                else
                {
                    if (coordinator is null)
                    {
                        coordinator = new WriteCoordinator(
                            this,
                            retained,
                            perCallLinked,
                            cancellationToken,
                            remaining,
                            written,
                            minGrantTicks,
                            backpressure,
                            decided,
                            decidedOutcome,
                            decidedException,
                            errors,
                            permanentErrors);
                        retained = default;
                        perCallLinked = null;
                    }

                    coordinator.Attach(attempt);
                }
            }
        }
        finally
        {
            perCallLinked?.Dispose();
            retained.Dispose();
        }

        if (coordinator is null)
        {
            _operations.Exit();
            if (decidedException is not null)
            {
                ExceptionDispatchInfo.Throw(decidedException);
            }

            Debug.Assert(decidedOutcome is not null, "A completed write must have an outcome or an exception.");
            return decidedOutcome.Value;
        }

        return await coordinator.CompleteFanOut().ConfigureAwait(false);
    }

    private CancellationToken AcquireWriteToken(CancellationToken cancellationToken, out CancellationTokenSource? perCallLinked)
    {
        perCallLinked = null;
        if (!cancellationToken.CanBeCanceled || cancellationToken == _disposing.Token)
        {
            return _disposing.Token;
        }

        var cached = Volatile.Read(ref _cachedWriteToken);
        if (cached is not null)
        {
            if (cached.Caller == cancellationToken)
            {
                return cached.Linked.Token;
            }

            perCallLinked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposing.Token);
            return perCallLinked.Token;
        }

#pragma warning disable CA2000 // Ownership transfers to _cachedWriteToken (disposed in CompleteDisposalAsync) or to perCallLinked.
        var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposing.Token);
#pragma warning restore CA2000
        var created = new CachedWriteToken(cancellationToken, linked);
        var prior = Interlocked.CompareExchange(ref _cachedWriteToken, created, null);
        if (prior is null)
        {
            // Ownership of linked transferred to _cachedWriteToken, disposed in CompleteDisposalAsync.
            return linked.Token;
        }

        if (prior.Caller == cancellationToken)
        {
            linked.Dispose();
            return prior.Linked.Token;
        }

        perCallLinked = linked;
        return perCallLinked.Token;
    }

    private static ValueTask<LedgerWriteOutcome> CompletedWriteValueTask(
        LedgerWriteOutcome? decidedOutcome,
        Exception? decidedException)
    {
        if (decidedException is not null)
        {
            return ValueTask.FromException<LedgerWriteOutcome>(decidedException);
        }

        Debug.Assert(decidedOutcome is not null, "A completed write must have an outcome or an exception.");
        return new ValueTask<LedgerWriteOutcome>(decidedOutcome.Value);
    }

    private ReplicaWriteOutcome.Written GetWrittenOutcome(long minGrantTicks)
    {
        if (minGrantTicks is <= 0 or long.MaxValue)
        {
            return ReplicaWriteOutcome.Written.Instance;
        }

        var grant = TimeSpan.FromTicks(minGrantTicks);
        var cached = Volatile.Read(ref _cachedWrittenGrant);
        if (cached.LeaseGrant == grant)
        {
            return cached;
        }

        var created = new ReplicaWriteOutcome.Written(grant);
        Volatile.Write(ref _cachedWrittenGrant, created);
        return created;
    }

    private void RecordWriteOutcome(
        ReplicaWriteOutcome outcome,
        int remaining,
        ref int written,
        ref long minGrantTicks,
        ref bool backpressure,
        List<Exception>? errors,
        List<Exception>? permanentErrors,
        ref bool decided,
        ref LedgerWriteOutcome? decidedOutcome,
        ref Exception? decidedException)
    {
        if (outcome is ReplicaWriteOutcome.Written writtenOutcome)
        {
            // LP-I18: a quorum lease grant is the minimum grant returned by the Q replicas that decided the write.
            var grantTicks = writtenOutcome.LeaseGrant.Ticks;
            if (grantTicks < minGrantTicks)
            {
                minGrantTicks = grantTicks;
            }

            if (++written >= Quorum)
            {
                decided = true;
                decidedOutcome = GetWrittenOutcome(minGrantTicks);
            }
            else
            {
                TryDecideWriteNoQuorum(remaining, written, backpressure, errors, permanentErrors, ref decided, ref decidedOutcome, ref decidedException);
            }

            return;
        }

        if (outcome is ReplicaWriteOutcome.Sealed or ReplicaWriteOutcome.Dropped)
        {
            // LP-I4: "A Sealed answer never counts toward the quorum." LP-I5: a dropped answer is a sealed one,
            // never a written one; the ledger has left the map.
            decided = true;
            decidedOutcome = ReplicaWriteOutcome.Sealed.Instance;
            return;
        }

        if (outcome is ReplicaWriteOutcome.Backpressure)
        {
            backpressure = true;
            TryDecideWriteNoQuorum(remaining, written, backpressure, errors, permanentErrors, ref decided, ref decidedOutcome, ref decidedException);
            return;
        }

        throw new UnreachableException("conventions.md §15: a replica answers a union which holds a case.");
    }

    private void RecordWriteFailure(
        Exception exception,
        CancellationToken cancellationToken,
        int remaining,
        int written,
        bool backpressure,
        ref List<Exception>? errors,
        ref List<Exception>? permanentErrors,
        ref bool decided,
        ref LedgerWriteOutcome? decidedOutcome,
        ref Exception? decidedException)
    {
        // Checked before the failure is classified, so a cancellation recorded below is the replica's own, such as a
        // transport's timeout: an unknown outcome, which repeating may change. A call the disposal ended is no replica's
        // failure.
        if (cancellationToken.IsCancellationRequested)
        {
            decided = true;
            decidedException = new OperationCanceledException(cancellationToken);
            return;
        }

        if (_disposing.IsCancellationRequested)
        {
            decided = true;
            decidedException = new ObjectDisposedException(nameof(LedgerQuorum), "The quorum's disposal ended the write's replica calls.");
            return;
        }

        (errors ??= []).Add(exception);
        if (!LedgerReplicaFailures.IsTransient(exception, stop: CancellationToken.None))
        {
            (permanentErrors ??= []).Add(exception);
        }

        TryDecideWriteNoQuorum(remaining, written, backpressure, errors, permanentErrors, ref decided, ref decidedOutcome, ref decidedException);
    }

    private void TryDecideWriteNoQuorum(
        int remaining,
        int written,
        bool backpressure,
        List<Exception>? errors,
        List<Exception>? permanentErrors,
        ref bool decided,
        ref LedgerWriteOutcome? decidedOutcome,
        ref Exception? decidedException)
    {
        if (written + remaining >= Quorum)
        {
            return;
        }

        var permanent = permanentErrors?.Count ?? 0;
        if (permanent >= Coverage)
        {
            decided = true;
            decidedException = Unavailable("write", permanentErrors!);
            return;
        }

        if (permanent + remaining < Coverage)
        {
            decided = true;
            if (backpressure)
            {
                decidedOutcome = ReplicaWriteOutcome.Backpressure.Instance;
            }
            else
            {
                decidedException = Unavailable("write", errors!);
            }
        }
    }

    private static Slot Max(Slot left, Slot right) => left >= right ? left : right;

    /// <summary>
    /// The rule of an operation which cannot decide from its answers (ledger-protocol.md §6.4): throws the failures
    /// repetition cannot change once <c>N − Q + 1</c> replicas failed so, since no <c>Q</c> replicas can ever answer it, so
    /// a caller which repeats while an inner failure may change stops (LP-I12); otherwise returns whether the replicas still
    /// pending can no longer bring those to <c>N − Q + 1</c>. The operation then answers <c>Backpressure</c> when a replica
    /// answered it, and otherwise fails with every failure, a transient one among them.
    /// </summary>
    /// <param name="operation">The operation, which names the failure.</param>
    /// <param name="permanentErrors">The failures repetition cannot change, one per replica, or <see langword="null"/>.</param>
    /// <param name="pending">The number of replicas which have not answered.</param>
    private bool NoQuorum(string operation, List<Exception>? permanentErrors, int pending)
    {
        var permanent = permanentErrors?.Count ?? 0;
        if (permanent >= Coverage)
        {
            throw Unavailable(operation, permanentErrors!);
        }

        return permanent + pending < Coverage;
    }

    /// <summary>
    /// Records a failed replica operation's one failure, and among the failures repetition cannot change when it is one.
    /// </summary>
    private static void AddFailure(Task task, List<Exception> errors, ref List<Exception>? permanentErrors)
    {
        var first = errors.Count;
        AddErrors(task, errors);

        // Invoke faults its task with the one exception or cancellation, so these count replicas.
        Debug.Assert(errors.Count == first + 1, "A failed replica operation records one error.");
        var error = errors[^1];
        if (!LedgerReplicaFailures.IsTransient(error, stop: CancellationToken.None))
        {
            (permanentErrors ??= []).Add(error);
        }
    }

    /// <summary>
    /// Reports an operation which can no longer reach its decision, with its replicas' failures.
    /// </summary>
    private static AggregateException Unavailable(string operation, List<Exception> errors) =>
        new(string.Create(CultureInfo.InvariantCulture, $"The {operation} could not reach its required quorum."), errors);

    /// <summary>
    /// Records a failed replica operation's errors: a faulted task's exceptions, or the cancellation a cancelled task
    /// stored, which awaiting it rethrows unchanged, so a replica's own cancellation keeps its cause, such as a
    /// transport's timeout.
    /// </summary>
    private static void AddErrors(Task task, List<Exception> errors)
    {
        if (task.Exception is { } exception)
        {
            errors.AddRange(exception.InnerExceptions);
            return;
        }

        Debug.Assert(task.IsCanceled, "A replica operation which did not succeed faulted or was cancelled.");
        try
        {
            task.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException cancellation)
        {
            errors.Add(cancellation);
        }
    }

    /// <summary>
    /// Runs a replica operation so that a synchronous failure, like an asynchronous one, faults the returned task and
    /// counts in the operation's accounting.
    /// </summary>
    private static Task<T> Invoke<T>(Func<ValueTask<T>> operation)
    {
        try
        {
            return operation().AsTask();
        }
#pragma warning disable CA1031 // Any synchronous replica failure must participate in quorum accounting.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            return Task.FromException<T>(exception);
        }
    }

    /// <summary>
    /// Disposes each replica once, continuing past failures and reporting them together.
    /// </summary>
    private static async ValueTask DisposeReplicasAsync(ILedgerReplica[] replicas)
    {
        List<Exception>? exceptions = null;
        foreach (var replica in replicas)
        {
            try
            {
                await replica.DisposeAsync().ConfigureAwait(false);
            }
#pragma warning disable CA1031 // Disposal must continue after any replica's failure and report every failure.
            catch (Exception exception)
#pragma warning restore CA1031
            {
                (exceptions ??= []).Add(exception);
            }
        }

        if (exceptions is { Count: > 0 })
        {
            throw new AggregateException("One or more of the ledger's replicas failed to dispose.", exceptions);
        }
    }

    private async Task CompleteDisposalAsync(Task drained)
    {
        // ledger-protocol.md §6.3: the operations admitted before disposal finish with the replicas first, a recovery
        // stopped by the lifetime's cancellation, whose callbacks run off the caller's lock, and a write's replica calls
        // by DisposeAsync's cancellation. Only an operation tracks a straggler, so once every one has exited, the tracked
        // set is final.
        var stopped = _lifetime.CancelAsync();
        await drained.ConfigureAwait(false);
        await stopped.ConfigureAwait(false);
        await CloseStragglersAsync().ConfigureAwait(false);
        var stragglerFailure = Volatile.Read(ref _stragglerFailure);

        try
        {
            await DisposeReplicasAsync(_replicas).ConfigureAwait(false);
        }
        catch (Exception exception) when (stragglerFailure is not null)
        {
            throw new AggregateException(stragglerFailure, exception);
        }
        finally
        {
            _lifetime.Dispose();
            _disposing.Dispose();
            Volatile.Read(ref _cachedWriteToken)?.Linked.Dispose();
        }

        if (stragglerFailure is not null)
        {
            throw stragglerFailure;
        }
    }

    private void EnterStragglers(int count)
    {
        Debug.Assert(count > 0, "A straggler group enters with at least one running replica call.");
        var state = Interlocked.Add(ref _stragglerState, count);
        Debug.Assert((state & StragglersClosed) == 0, "Stragglers are tracked only while an operation is admitted, before disposal closes them.");
        _ = Interlocked.Add(ref _trackedStragglers, count);
    }

    private void ExitStragglers(int count, Exception? failure = null)
    {
        Debug.Assert(count > 0, "A straggler group exits with at least one completed replica call.");
        if (failure is not null)
        {
            _ = Interlocked.CompareExchange(ref _stragglerFailure, failure, null);
        }

        if (Interlocked.Add(ref _stragglerState, -count) == StragglersClosed)
        {
            _ = _stragglersDrained.TrySetResult();
        }
    }

    private Task CloseStragglersAsync()
    {
        if (Interlocked.Or(ref _stragglerState, StragglersClosed) == 0)
        {
            _ = _stragglersDrained.TrySetResult();
        }

        return _stragglersDrained.Task;
    }

    private void ThrowIfReadsStopped()
    {
        if (_lifetime.IsCancellationRequested)
        {
            throw ReadsStoppedException();
        }
    }

    /// <summary>Returns whether a read's cancellation came from its stopped lifetime, not from its caller.</summary>
    private bool ReadsStopped(CancellationToken cancellationToken) => _lifetime.IsCancellationRequested && !cancellationToken.IsCancellationRequested;

    private ObjectDisposedException ReadsStoppedException() => new(nameof(LedgerQuorum), "The quorum's reads stopped: its owner is disposed.");

    /// <summary>Returns a source linked to the caller's token and to the quorum's lifetime, which stops its reads.</summary>
    private CancellationTokenSource LinkToLifetime(CancellationToken cancellationToken) =>
        CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);

    /// <summary>
    /// Tracks replica operations still running past their operation's decision, releasing a read's batch when it answers;
    /// returns a task which completes once they all ended, or <see langword="null"/> when there are none.
    /// </summary>
    private Task? TrackStragglers<T>(IEnumerable<Task<T>> tasks)
    {
        List<Task>? running = null;
        foreach (var task in tasks)
        {
            var completion = task.ContinueWith(
                static completed =>
                {
                    if (!completed.IsCompletedSuccessfully)
                    {
                        _ = completed.Exception;
                    }
                    else if (completed.Result is ReplicaRead read && read.TryGetValue(out ArcBuffer batch))
                    {
                        batch.Dispose();
                    }
                    else if (completed.Result is ReplicaRangeRead rangeRead)
                    {
                        rangeRead.Release();
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            TrackStraggler(completion);
            (running ??= []).Add(completion);
        }

        return running is null ? null : Task.WhenAll(running);
    }

    private void TrackStraggler(Task completion)
    {
        EnterStragglers(1);
        if (completion.IsCompleted)
        {
            ExitStragglers(1, completion.Exception);
            return;
        }

        _ = completion.ContinueWith(
            static (completed, state) => ((LedgerQuorum)state!).ExitStragglers(1, completed.Exception),
            this,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>
    /// Admits an operation, which runs to its end before disposal releases the replicas it uses (ledger-protocol.md §6.3).
    /// </summary>
    private AdmissionGate.Admission Admit()
    {
        var admission = _operations.TryEnter();
        ObjectDisposedException.ThrowIf(!admission.Entered, this);
        return admission;
    }

    private sealed class CachedWriteToken(CancellationToken caller, CancellationTokenSource linked)
    {
        public CancellationToken Caller { get; } = caller;

        public CancellationTokenSource Linked { get; } = linked;
    }

    /// <summary>
    /// Coordinates one asynchronous quorum write across its replicas without <see cref="Task.WhenAny(IEnumerable{Task})"/>
    /// or per-straggler continuations: owns the retained zero-copy <see cref="ArcBuffer"/> lease until all <c>N</c>
    /// replicas finish, settles the caller's <see cref="ValueTask{LedgerWriteOutcome}"/> as soon as the write decides, and
    /// registers any remaining replicas with the quorum's atomic straggler counter before exiting admission.
    /// </summary>
    private sealed class WriteCoordinator(
        LedgerQuorum quorum,
        ArcBuffer retained,
        CancellationTokenSource? perCallLinked,
        CancellationToken cancellationToken,
        int remaining,
        int written,
        long minGrantTicks,
        bool backpressure,
        bool decided,
        LedgerWriteOutcome? decidedOutcome,
        Exception? decidedException,
        List<Exception>? errors,
        List<Exception>? permanentErrors) : IValueTaskSource<LedgerWriteOutcome>
    {
        private readonly LedgerQuorum _quorum = quorum;
        private readonly ArcBuffer _retained = retained;
        private readonly CancellationTokenSource? _perCallLinked = perCallLinked;
        private readonly CancellationToken _cancellationToken = cancellationToken;
        private readonly Lock _lock = new();
        private ManualResetValueTaskSourceCore<LedgerWriteOutcome> _core = new() { RunContinuationsAsynchronously = true };
        private CancellationTokenRegistration _cancellationRegistration;
        private int _pendingReplicas = remaining;
        private int _written = written;
        private long _minGrantTicks = minGrantTicks;
        private bool _backpressure = backpressure;
        private bool _decided = decided;
        private bool _fanOutDone;
        private bool _stragglerTracked;
        private LedgerWriteOutcome? _decidedOutcome = decidedOutcome;
        private Exception? _decidedException = decidedException;
        private List<Exception>? _errors = errors;
        private List<Exception>? _permanentErrors = permanentErrors;

        public void Attach(ValueTask<ReplicaWriteOutcome> attempt) => _ = ObserveAsync(attempt);

        private async Task ObserveAsync(ValueTask<ReplicaWriteOutcome> attempt)
        {
            ReplicaWriteOutcome outcome = default;
            Exception? failure = null;
            try
            {
                outcome = await attempt.ConfigureAwait(false);
            }
#pragma warning disable CA1031 // Observe every replica failure cleanly, including stragglers.
            catch (Exception exception)
#pragma warning restore CA1031
            {
                failure = exception;
            }

            OnAsyncCompleted(outcome, failure);
        }

        public void OnSyncOutcome(ReplicaWriteOutcome outcome)
        {
            lock (_lock)
            {
                _pendingReplicas--;
                if (!_decided)
                {
                    _quorum.RecordWriteOutcome(
                        outcome,
                        _pendingReplicas,
                        ref _written,
                        ref _minGrantTicks,
                        ref _backpressure,
                        _errors,
                        _permanentErrors,
                        ref _decided,
                        ref _decidedOutcome,
                        ref _decidedException);
                }
            }
        }

        public void OnSyncFailure(Exception exception)
        {
            lock (_lock)
            {
                _pendingReplicas--;
                if (!_decided)
                {
                    _quorum.RecordWriteFailure(
                        exception,
                        _cancellationToken,
                        _pendingReplicas,
                        _written,
                        _backpressure,
                        ref _errors,
                        ref _permanentErrors,
                        ref _decided,
                        ref _decidedOutcome,
                        ref _decidedException);
                }
            }
        }

        public ValueTask<LedgerWriteOutcome> CompleteFanOut()
        {
            bool decidedNow;
            bool cleanupNow;
            LedgerWriteOutcome? outcome;
            Exception? exception;
            lock (_lock)
            {
                _fanOutDone = true;
                if (!_decided)
                {
                    if (_cancellationToken.IsCancellationRequested)
                    {
                        _decided = true;
                        _decidedException = new OperationCanceledException(_cancellationToken);
                    }
                    else
                    {
                        _quorum.TryDecideWriteNoQuorum(
                            _pendingReplicas,
                            _written,
                            _backpressure,
                            _errors,
                            _permanentErrors,
                            ref _decided,
                            ref _decidedOutcome,
                            ref _decidedException);
                    }
                }

                decidedNow = _decided;
                cleanupNow = _pendingReplicas == 0;
                if (decidedNow && !cleanupNow)
                {
                    _quorum.EnterStragglers(_pendingReplicas);
                    _stragglerTracked = true;
                }

                outcome = _decidedOutcome;
                exception = _decidedException;
            }

            if (decidedNow)
            {
                if (cleanupNow)
                {
                    _retained.Dispose();
                    _perCallLinked?.Dispose();
                }

                _quorum._operations.Exit();
                return CompletedWriteValueTask(outcome, exception);
            }

            if (_cancellationToken.CanBeCanceled)
            {
                var registration = _cancellationToken.UnsafeRegister(
                    static state => ((WriteCoordinator)state!).OnCallerCanceled(),
                    this);
                var disposeRegistration = false;
                lock (_lock)
                {
                    if (_decided)
                    {
                        disposeRegistration = true;
                    }
                    else
                    {
                        _cancellationRegistration = registration;
                    }
                }

                if (disposeRegistration)
                {
                    _ = registration.Unregister();
                }
            }

            return new ValueTask<LedgerWriteOutcome>(this, _core.Version);
        }

        public LedgerWriteOutcome GetResult(short token) => _core.GetResult(token);

        public ValueTaskSourceStatus GetStatus(short token) => _core.GetStatus(token);

        public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags) =>
            _core.OnCompleted(continuation, state, token, flags);

        private void OnAsyncCompleted(ReplicaWriteOutcome outcome, Exception? failure)
        {
            var settleNow = false;
            var exitStraggler = false;
            var cleanupNow = false;
            LedgerWriteOutcome? decidedOutcome = null;
            Exception? decidedException = null;
            CancellationTokenRegistration registration = default;

            lock (_lock)
            {
                _pendingReplicas--;
                if (!_decided)
                {
                    if (failure is not null)
                    {
                        _quorum.RecordWriteFailure(
                            failure,
                            _cancellationToken,
                            _pendingReplicas,
                            _written,
                            _backpressure,
                            ref _errors,
                            ref _permanentErrors,
                            ref _decided,
                            ref _decidedOutcome,
                            ref _decidedException);
                    }
                    else
                    {
                        _quorum.RecordWriteOutcome(
                            outcome,
                            _pendingReplicas,
                            ref _written,
                            ref _minGrantTicks,
                            ref _backpressure,
                            _errors,
                            _permanentErrors,
                            ref _decided,
                            ref _decidedOutcome,
                            ref _decidedException);
                    }

                    if (_fanOutDone && _decided)
                    {
                        settleNow = true;
                        cleanupNow = _pendingReplicas == 0;
                        if (!cleanupNow)
                        {
                            _quorum.EnterStragglers(_pendingReplicas);
                            _stragglerTracked = true;
                        }

                        decidedOutcome = _decidedOutcome;
                        decidedException = _decidedException;
                        registration = _cancellationRegistration;
                        _cancellationRegistration = default;
                    }
                }
                else if (_fanOutDone)
                {
                    Debug.Assert(_stragglerTracked, "A replica completing after a decided fan-out must be tracked as a straggler.");
                    exitStraggler = true;
                    cleanupNow = _pendingReplicas == 0;
                }
            }

            if (settleNow)
            {
                _ = registration.Unregister();
                if (cleanupNow)
                {
                    _retained.Dispose();
                    _perCallLinked?.Dispose();
                }

                _quorum._operations.Exit();
                SettleCore(decidedOutcome, decidedException);
            }
            else if (exitStraggler)
            {
                if (cleanupNow)
                {
                    _retained.Dispose();
                    _perCallLinked?.Dispose();
                }

                _quorum.ExitStragglers(1);
            }
        }

        private void OnCallerCanceled()
        {
            var cleanupNow = false;
            Exception? decidedException;
            CancellationTokenRegistration registration;
            lock (_lock)
            {
                if (_decided)
                {
                    return;
                }

                _decided = true;
                decidedException = new OperationCanceledException(_cancellationToken);
                _decidedException = decidedException;
                cleanupNow = _pendingReplicas == 0;
                if (!cleanupNow)
                {
                    _quorum.EnterStragglers(_pendingReplicas);
                    _stragglerTracked = true;
                }

                registration = _cancellationRegistration;
                _cancellationRegistration = default;
            }

            _ = registration.Unregister();
            if (cleanupNow)
            {
                _retained.Dispose();
                _perCallLinked?.Dispose();
            }

            _quorum._operations.Exit();
            SettleCore(decidedOutcome: null, decidedException);
        }

        private void SettleCore(LedgerWriteOutcome? decidedOutcome, Exception? decidedException)
        {
            if (decidedException is not null)
            {
                _core.SetException(decidedException);
                return;
            }

            Debug.Assert(decidedOutcome is not null, "A settled write must have an outcome or an exception.");
            _core.SetResult(decidedOutcome.Value);
        }
    }

    /// <summary>
    /// The token under which one operation's replica calls run: the caller's, which a token of the quorum's also cancels,
    /// the lifetime's once its reads stop (<see cref="StopReadsAsync"/>) for a read and the disposal's for a write; or the
    /// quorum's alone when the caller's cannot be cancelled. Disposal releases the source linking the two once the
    /// operation's stragglers ended, so each straggler observes the quorum's token to its end.
    /// </summary>
    private struct CallScope : IDisposable
    {
        private readonly CancellationTokenSource? _linked;
        private Task? _stragglers;

        public CallScope(CancellationToken cancellationToken, CancellationToken quorum)
        {
            if (cancellationToken.CanBeCanceled)
            {
                _linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, quorum);
                Token = _linked.Token;
            }
            else
            {
                Token = quorum;
            }
        }

        public readonly CancellationToken Token { get; }

        /// <summary>Records the operation's stragglers, which the token serves past the operation.</summary>
        public void Outlive(Task? stragglers) => _stragglers = stragglers;

        public readonly void Dispose()
        {
            if (_stragglers is { IsCompleted: false } stragglers && _linked is not null)
            {
                _ = stragglers.ContinueWith(
                    static (_, state) => ((CancellationTokenSource)state!).Dispose(),
                    _linked,
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
                return;
            }

            _linked?.Dispose();
        }
    }
}
