using System.Collections.Immutable;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Orleans.Durable.Storage.Engine.Ledgers.Control;

/// <summary>
/// Executes transitions in a process which can become a primary (ledgers.md §2.6, §8.1): it recovers the ledgers a
/// transition closes, creates a sequencer lifetime for each ledger it opens, which the process's
/// <see cref="LedgerWriterRegistry"/> holds, submits the change once through its caller's <c>commit</c> delegate, and
/// reconciles from the map view.
/// </summary>
/// <remarks>
/// <para>
/// Whether a transition applied is decided by the map, never by whether a reply arrived: the registry adopts each
/// lifetime into its ledger's writer once its view lists the ledger under the lifetime's claim, and stops it once the
/// change can no longer apply. Only the <see cref="LedgerMapChange"/> and its <see cref="LedgerMapCommitOutcome"/> cross
/// to another process, as data; the <c>commit</c> delegate never leaves this one.
/// </para>
/// <para>
/// A recovery's seal is final, so a transition interrupted after its recoveries leaves the sealed ledgers open in the map
/// until a later transition recovers and closes them.
/// </para>
/// </remarks>
public sealed partial class TransitionExecutor
{
    private readonly LedgerWriterRegistry _registry;
    private readonly LedgerReader _reader;
    private readonly LedgerMapView _map;
    private readonly ILogger _logger;
    private readonly Lock _lock = new();

    // The transition each lifetime this executor created was created for, while the registry holds it or has it pending:
    // an earlier submission applied a transition only when the map lists a claim created for that transition.
    private readonly Dictionary<SequencerInstanceId, Transition> _origins = [];

    /// <summary>
    /// Initializes a new instance of the <see cref="TransitionExecutor"/> class over the process's registry, whose reader
    /// and map view it uses.
    /// </summary>
    /// <param name="registry">The process's registry, which holds the lifetimes the executor creates.</param>
    /// <param name="logger">Receives the defects a committer reports.</param>
    public TransitionExecutor(LedgerWriterRegistry registry, ILogger<TransitionExecutor> logger)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(logger);
        _registry = registry;
        _reader = registry.Reader;
        _map = _reader.Map;
        _logger = logger;
    }

    /// <summary>
    /// Executes <paramref name="transition"/> (ledgers.md §8.1).
    /// </summary>
    /// <param name="transition">The transition, which this process's node executes.</param>
    /// <param name="commit">Submits the change once to the map committer, as its <c>CommitAsync</c> does.</param>
    /// <param name="cancellationToken">
    /// Before <paramref name="commit"/> is called, stops the transition: the lifetimes it created stop, and the ledgers it
    /// sealed stay open in the map. After, stops the caller waiting, and the map view decides the lifetimes.
    /// </param>
    /// <param name="created">
    /// Told of each lifetime the transition creates, on the executor's thread, once the registry holds it and before the
    /// change which names it is submitted, so the caller can key what it knew as the transition began to the lifetime,
    /// whatever the transition's outcome.
    /// </param>
    /// <returns>
    /// The outcome <paramref name="commit"/> returned, once the process's registry has reconciled with a view at its
    /// epoch: <see cref="LedgerMapCommitOutcome.Applied"/>, and the process serves the ledgers its view lists under its
    /// claims; <see cref="LedgerMapCommitOutcome.Superseded"/>, also without a submission when the view shows the change
    /// can no longer apply, and the lifetimes it created are stopped; or <see cref="LedgerMapCommitOutcome.Refused"/> or
    /// <see cref="LedgerMapCommitOutcome.Rejected"/>, and they are stopped. A transition whose view lists every range it
    /// opens under a claim of a lifetime this process created for the same transition and still holds or has pending, as
    /// after a retry of a transition whose reply was lost, answers <see cref="LedgerMapCommitOutcome.Applied"/> at the
    /// view's epoch.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// Another node executes the transition, it closes a ledger no map lists, or its ledgers cannot take it: a split at the
    /// deepest range, or a merge of ledgers which are not siblings.
    /// </exception>
    /// <remarks>
    /// A recovery which fails throws before any change is submitted. An <see cref="OverflowException"/>,
    /// <see cref="InvalidDataException"/>, or <see cref="InvalidOperationException"/> other than
    /// <see cref="ObjectDisposedException"/> from <paramref name="commit"/> means the change never applies, and the
    /// lifetimes stop; any other exception leaves the outcome unknown and the lifetimes to the map view, which the
    /// registry re-reads while they are pending.
    /// </remarks>
    public async Task<LedgerMapCommitOutcome> ExecuteAsync(
        Transition transition,
        Func<LedgerMapChange, CancellationToken, Task<LedgerMapCommitOutcome>> commit,
        CancellationToken cancellationToken = default,
        Action<SequencerInstanceId>? created = null)
    {
        ArgumentNullException.ThrowIfNull(commit);
        if (transition.Value is null)
        {
            throw new ArgumentException("A transition holds a case.", nameof(transition));
        }

        var (choice, closedIds) = (transition.Choice, transition.Closes);
        if (choice.Executor != _registry.Node)
        {
            throw new ArgumentException("A transition is executed by the node it names, which becomes the primary of the ledgers it opens.", nameof(transition));
        }

        // 1. A view at least as new as the map the controller chose from, which lists every ledger the transition closes.
        var view = await ReadChosenAsync(choice, closedIds, cancellationToken).ConfigureAwait(false);
        if (!TryListClosing(view, choice, closedIds, out var closedLedgers))
        {
            return await DecidedAsync(view, transition, cancellationToken).ConfigureAwait(false);
        }

        var opens = Opens(transition, closedLedgers);
        using var closings = _registry.BeginClosing(closedIds);
        var leases = new List<LedgerReader.LedgerLease>(closedLedgers.Length);
        try
        {
            foreach (var ledger in closedLedgers)
            {
                leases.Add(_reader.LeaseLedger(ledger));
            }

            // 2. Recover each ledger it closes, learning its candidate end and any remaining predecessor lease.
            if (await RecoverAllAsync(closedLedgers, leases, view, cancellationToken).ConfigureAwait(false) is not { } recovered)
            {
                return await DroppedAsync(cancellationToken).ConfigureAwait(false);
            }

            // 2b. LP-I18, LG-I14: before installing any successor in LedgerMap, wait out the maximum remaining lease
            // reported by the closed ledgers' sealing coverages, unless this is a cooperative Replace whose outgoing
            // primary already permanently surrendered its lease in PrepareRelease (OR-I24), or every closed ledger's
            // lease was surrendered locally in this process's StopAdmittingAsync.
            var skipLeaseWait = transition.Value is Transition.Replace { PredecessorHint: PredecessorReleaseHint.Cooperative };
            if (!skipLeaseWait && recovered.MaxLeaseRemaining > TimeSpan.Zero)
            {
                await Task.Delay(recovered.MaxLeaseRemaining, _registry.TimeProvider, cancellationToken).ConfigureAwait(false);
            }

            // 3. Create each opened ledger's lifetime, which the registry holds and the view decides.
            var lifetimes = CreateLifetimes(transition, opens, created);
            LedgerMapChange.Install change;
            try
            {
                change = new LedgerMapChange.Install(recovered.Ends, Openings(choice, opens, lifetimes), choice.ChosenAt);
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch
            {
                // Nothing was submitted, so nothing of the change can apply.
                Abandon(lifetimes);
                throw;
            }

            // 4. Submit the change once. From the call on, the change may apply however commit fails, synchronously or
            // not, unless the failure is one under which it never applies.
            LedgerMapCommitOutcome outcome;
            try
            {
                outcome = await commit(change, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (NeverApplies(exception))
            {
                Abandon(lifetimes);
                throw;
            }

            // 5. Reconcile from the view.
            return await SettleAsync(outcome, transition, opens, closedLedgers, leases, lifetimes, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            foreach (var lease in leases)
            {
                lease.Dispose();
            }
        }
    }

    /// <summary>
    /// Returns whether a failure of the <c>commit</c> delegate means the change never applies: it would overflow the IDs,
    /// the register holds no ledger map, or the committer found a defect before it wrote. A disposed committer may have
    /// written it.
    /// </summary>
    private static bool NeverApplies(Exception exception) =>
        exception is OverflowException or InvalidDataException or InvalidOperationException and not ObjectDisposedException;

    /// <summary>
    /// Lists the ledgers the transition closes from <paramref name="view"/> when the change's rule holds there
    /// (ledgers.md §8.1): every ledger it closes listed open, no ledger open for a bootstrap, and its <c>ChosenAt</c> at
    /// least the view's retirement floor. A rule which fails in the view fails in every later map.
    /// </summary>
    private static bool TryListClosing(LedgerMapValue view, TransitionChoice choice, ImmutableArray<LedgerId> closedIds, out ImmutableArray<LedgerMetadata> closedLedgers)
    {
        closedLedgers = [];
        ReadOnlySpan<KeyRange> root = [KeyRange.Root];
        if (!view.InstallRuleHolds(closedIds.AsSpan(), closedIds.IsEmpty ? root : [], choice.ChosenAt))
        {
            return false;
        }

        var listed = ImmutableArray.CreateBuilder<LedgerMetadata>(closedIds.Length);
        foreach (var id in closedIds)
        {
            listed.Add(view.Get(id)!.Value);
        }

        closedLedgers = listed.MoveToImmutable();
        return true;
    }

    private static ImmutableArray<LedgerOpening> Openings(TransitionChoice choice, ImmutableArray<KeyRange> opens, ImmutableArray<Sequencer> lifetimes)
    {
        var openings = ImmutableArray.CreateBuilder<LedgerOpening>(opens.Length);
        for (var index = 0; index < opens.Length; index++)
        {
            openings.Add(new LedgerOpening(opens[index], choice.Location, choice.K, new PrimaryClaim(choice.Executor, lifetimes[index].InstanceId)));
        }

        return openings.MoveToImmutable();
    }

    /// <summary>
    /// Returns a view at least as new as the map the controller chose from; a ledger the transition closes which the view
    /// does not yet list makes it read the register.
    /// </summary>
    private async Task<LedgerMapValue> ReadChosenAsync(TransitionChoice choice, ImmutableArray<LedgerId> closedIds, CancellationToken cancellationToken)
    {
        var view = await _map.RefreshAsync(choice.Epoch, cancellationToken).ConfigureAwait(false);
        if (!closedIds.Any(id => id.Value >= view.NextLedgerId.Value))
        {
            return view;
        }

        view = await _map.ReadAsync(cancellationToken).ConfigureAwait(false);
        return closedIds.Any(id => id.Value >= view.NextLedgerId.Value)
            ? throw new ArgumentException("A transition closes ledgers the map installed.", "transition")
            : view;
    }

    /// <summary>
    /// Answers a transition whose rule fails in <paramref name="view"/>: <see cref="LedgerMapCommitOutcome.Applied"/> when
    /// an earlier submission of the same transition by this process applied it, as after a lost reply, and otherwise
    /// <see cref="LedgerMapCommitOutcome.Superseded"/>.
    /// </summary>
    private async Task<LedgerMapCommitOutcome> DecidedAsync(LedgerMapValue view, Transition transition, CancellationToken cancellationToken)
    {
        var closedLedgers = ImmutableArray.CreateBuilder<LedgerMetadata>(transition.Closes.Length);
        foreach (var id in transition.Closes)
        {
            if (view.Get(id) is not { } listed)
            {
                // A ledger it closes has left the map, so this process's ledgers which replaced it are decided.
                return new LedgerMapCommitOutcome.Superseded(view.Epoch);
            }

            closedLedgers.Add(listed);
        }

        if (!AppliedHere(view, transition, Opens(transition, closedLedgers.MoveToImmutable())))
        {
            return new LedgerMapCommitOutcome.Superseded(view.Epoch);
        }

        await _registry.ReconciledAsync(view.Epoch, cancellationToken).ConfigureAwait(false);
        return new LedgerMapCommitOutcome.Applied(view.Epoch);
    }

    /// <summary>
    /// Returns whether <paramref name="view"/> lists every range of <paramref name="opens"/> under a claim of a lifetime
    /// this process created for <paramref name="transition"/> and still holds or has pending: only that transition's change
    /// names such a claim, so it applied. A claim created for another transition, by this process too, shows nothing of
    /// this one: a primary's own roll chosen below the floor, say, whose range the view lists under the primary's claim of
    /// an earlier change.
    /// </summary>
    private bool AppliedHere(LedgerMapValue view, Transition transition, ImmutableArray<KeyRange> opens)
    {
        foreach (var range in opens)
        {
            if (!view.Ledgers.Any(listed => listed.Range == range && CreatedFor(listed.Primary, transition)))
            {
                return false;
            }
        }

        Debug.Assert(!transition.Closes.Any(id => view.Get(id) is { IsOpen: true }), "A change which applied closed every ledger it closes.");
        return true;
    }

    /// <summary>
    /// Returns whether the process's registry still holds <paramref name="lifetime"/>, pending its view's decision or
    /// serving its writer, so a caller which keys what it knows to the lifetimes an execution reports created forgets it
    /// once this answers false.
    /// </summary>
    public bool Holds(SequencerInstanceId lifetime) => _registry.Knows(new PrimaryClaim(_registry.Node, lifetime));

    /// <summary>Returns whether <paramref name="claim"/> names a lifetime this executor created for <paramref name="transition"/>.</summary>
    private bool CreatedFor(PrimaryClaim claim, Transition transition)
    {
        if (claim.Node != _registry.Node)
        {
            return false;
        }

        lock (_lock)
        {
            return _origins.TryGetValue(claim.Sequencer, out var origin) && Same(origin, transition);
        }
    }

    /// <summary>
    /// Returns whether two transitions are the same: the same case, closing the same ledgers, with the same choice. A
    /// controller sends a transition again only as itself; a choice made again is another transition.
    /// </summary>
    private static bool Same(Transition first, Transition second)
    {
        var (one, other) = (first.Choice, second.Choice);
        return first.Value?.GetType() == second.Value?.GetType()
            && first.Closes.SequenceEqual(second.Closes)
            && (one.Executor, one.K, one.ChosenAt, one.Epoch) == (other.Executor, other.K, other.ChosenAt, other.Epoch)
            && one.Location.Equals(other.Location);
    }

    /// <summary>A replica answered dropped: the ledger has left the map, so the change can no longer apply.</summary>
    private async Task<LedgerMapCommitOutcome> DroppedAsync(CancellationToken cancellationToken)
    {
        var read = await _map.ReadAsync(cancellationToken).ConfigureAwait(false);
        return new LedgerMapCommitOutcome.Superseded(read.Epoch);
    }

    /// <summary>
    /// Recovers every ledger the transition closes, concurrently, and returns their candidate ends and the maximum
    /// remaining predecessor lease across them, or <see langword="null"/> once a replica of any answered dropped.
    /// </summary>
    private async Task<(ImmutableArray<LedgerClosing> Ends, TimeSpan MaxLeaseRemaining)?> RecoverAllAsync(
        ImmutableArray<LedgerMetadata> closedLedgers,
        List<LedgerReader.LedgerLease> leases,
        LedgerMapValue view,
        CancellationToken cancellationToken)
    {
        var recoveries = new Task<(Slot End, TimeSpan LeaseRemaining)?>[closedLedgers.Length];
        for (var index = 0; index < closedLedgers.Length; index++)
        {
            recoveries[index] = RecoverAsync(leases[index].Ledger, view, cancellationToken);
        }

        var results = await Task.WhenAll(recoveries).ConfigureAwait(false);
        var closings = ImmutableArray.CreateBuilder<LedgerClosing>(results.Length);
        var maxLeaseRemaining = TimeSpan.Zero;
        for (var index = 0; index < results.Length; index++)
        {
            if (results[index] is not { } result)
            {
                return null;
            }

            closings.Add(new LedgerClosing(closedLedgers[index].Id, result.End));
            if (result.LeaseRemaining > maxLeaseRemaining)
            {
                maxLeaseRemaining = result.LeaseRemaining;
            }
        }

        return (closings.MoveToImmutable(), maxLeaseRemaining);
    }

    /// <summary>
    /// Gets or sets a test seam, run under the executor's lock between recording a lifetime's origin and handing the
    /// lifetime to the registry, so a test can race another transition's prune against that step.
    /// </summary>
    internal Action? OriginRecorded { get; set; }

    /// <summary>
    /// Creates one lifetime per opened range, each held by the registry until the view decides it, and records the
    /// transition each was created for, forgetting the origins of lifetimes the registry no longer holds. A lifetime's
    /// origin is recorded and the lifetime held under one lock, which the prune takes too, so a prune never sees an origin
    /// whose lifetime the registry does not yet know. <paramref name="created"/> learns of each once the registry holds it.
    /// </summary>
    private ImmutableArray<Sequencer> CreateLifetimes(Transition transition, ImmutableArray<KeyRange> opens, Action<SequencerInstanceId>? created)
    {
        var choice = transition.Choice;
        lock (_lock)
        {
            foreach (var instance in _origins.Keys.Where(instance => !_registry.Knows(new PrimaryClaim(_registry.Node, instance))).ToList())
            {
                _ = _origins.Remove(instance);
            }
        }

        var lifetimes = ImmutableArray.CreateBuilder<Sequencer>(opens.Length);
        try
        {
            foreach (var _ in opens)
            {
                var lifetime = new Sequencer(choice.K);
                lifetimes.Add(lifetime);
                lock (_lock)
                {
                    _origins.Add(lifetime.InstanceId, transition);
                    OriginRecorded?.Invoke();
                    _registry.Hold(lifetime, transition.Closes, opens, choice.ChosenAt);
                }

                created?.Invoke(lifetime.InstanceId);
            }
        }
        catch
        {
            Abandon(lifetimes.ToImmutable());
            throw;
        }

        return lifetimes.MoveToImmutable();
    }

    /// <summary>
    /// Reconciles from the view at the outcome's epoch, or stops the lifetimes when nothing of the change is pending
    /// (ledgers.md §8.1 step 5).
    /// </summary>
    private async Task<LedgerMapCommitOutcome> SettleAsync(
        LedgerMapCommitOutcome outcome,
        Transition transition,
        ImmutableArray<KeyRange> opens,
        ImmutableArray<LedgerMetadata> closedLedgers,
        List<LedgerReader.LedgerLease> leases,
        ImmutableArray<Sequencer> lifetimes,
        CancellationToken cancellationToken)
    {
        if (outcome.Value is LedgerMapCommitOutcome.Applied applied)
        {
            _ = await ReconcileAsync(applied.Epoch, closedLedgers, leases, cancellationToken).ConfigureAwait(false);
            return outcome;
        }

        if (outcome.Value is LedgerMapCommitOutcome.Superseded superseded)
        {
            // An earlier submission of the transition by this process may have applied its change, as after a retry.
            var view = await ReconcileAsync(superseded.Epoch, closedLedgers, leases, cancellationToken).ConfigureAwait(false);
            return AppliedHere(view, transition, opens) ? new LedgerMapCommitOutcome.Applied(view.Epoch) : outcome;
        }

        if (outcome.Value is LedgerMapCommitOutcome.Refused or LedgerMapCommitOutcome.Rejected)
        {
            // Nothing of the change is pending: the seals stand, and the controller chooses again.
            Abandon(lifetimes);
            if (outcome.Value is LedgerMapCommitOutcome.Rejected rejected)
            {
                LogRejected(_logger, rejected.Reason);
                Debug.Fail($"The map committer rejected a transition's change as a defect: {rejected.Reason}.");
            }

            return outcome;
        }

        throw new UnreachableException("conventions.md §15: a commit answers a union which holds a case.");
    }

    /// <summary>Returns the ranges the transition opens, from the ranges of the ledgers it closes.</summary>
    internal static ImmutableArray<KeyRange> Opens(Transition transition, ImmutableArray<LedgerMetadata> closing) => transition switch
    {
        Transition.Bootstrap => [KeyRange.Root],
        Transition.Replace => [closing[0].Range],
        Transition.Split => Children(closing[0].Range),
        Transition.Merge => [Parent(closing[0].Range, closing[1].Range)],
        null => throw new UnreachableException(),
    };

    private static ImmutableArray<KeyRange> Children(KeyRange range)
    {
        if (range.Depth >= KeyRange.MaxDepth)
        {
            throw new ArgumentException("A split's ledger has a range above the deepest.", nameof(range));
        }

        var (lower, upper) = range.Children;
        return [lower, upper];
    }

    private static KeyRange Parent(KeyRange first, KeyRange second) =>
        first.Depth > 0 && first.Sibling == second
            ? first.Parent
            : throw new ArgumentException("A merge's ledgers own sibling ranges.", nameof(second));

    [LoggerMessage(Level = LogLevel.Error, Message = "The ledger map committer rejected a transition's change as a defect: {Reason}.")]
    private static partial void LogRejected(ILogger logger, LedgerMapRejection reason);

    /// <summary>
    /// Recovers the ledger of <paramref name="process"/>, which <paramref name="view"/> lists open: when this process holds
    /// its writer, it first stops admitting (permanently surrendering this primary's lease), so the recovery starts at the
    /// primary's exact contiguous tail (ledgers.md §8.1).
    /// </summary>
    /// <returns>
    /// The candidate end and the remaining lease to wait before committing (zero when this process surrendered the
    /// ledger's sole primary lease locally), or <see langword="null"/> when a replica answered dropped.
    /// </returns>
    private async Task<(Slot End, TimeSpan LeaseRemaining)?> RecoverAsync(Ledger process, LedgerMapValue view, CancellationToken cancellationToken)
    {
        var localSurrendered = false;
        if (_registry.HeldWriter(process) is { } writer)
        {
            await writer.StopAdmittingAsync(cancellationToken).ConfigureAwait(false);
            localSurrendered = true;
        }

        // LG-I7: the executor's seals and recovery writes reach the replicas its view lists.
        process.AssertSendsFrom(view);
        return await process.RecoverAsync(cancellationToken).ConfigureAwait(false) switch
        {
            RecoveryOutcome.Recovered recovered => (recovered.End, localSurrendered ? TimeSpan.Zero : recovered.LeaseRemaining),
            ReplicaWriteOutcome.Dropped => null,
            null => throw new UnreachableException(),
        };
    }

    /// <summary>
    /// Refreshes the view to <paramref name="epoch"/> and waits for the registry to reconcile from it (ledgers.md §8.1
    /// step 5): it serves the ledgers the view lists under its claims and stops the lifetimes it does not, and each
    /// recovered ledger the view lists closed learns its recorded <c>T</c>, never a losing recovery's candidate end
    /// (LP-I11).
    /// </summary>
    /// <returns>The view it reconciled with.</returns>
    private async Task<LedgerMapValue> ReconcileAsync(ulong epoch, ImmutableArray<LedgerMetadata> closedLedgers, List<LedgerReader.LedgerLease> leases, CancellationToken cancellationToken)
    {
        var view = await _map.RefreshAsync(epoch, cancellationToken).ConfigureAwait(false);
        await _registry.ReconciledAsync(view.Epoch, cancellationToken).ConfigureAwait(false);
        for (var index = 0; index < closedLedgers.Length; index++)
        {
            if (view.Get(closedLedgers[index].Id)?.Status.Value is LedgerStatus.Closed closed)
            {
                leases[index].Ledger.ObserveRecordedTail(closed.T);
            }
        }

        return view;
    }

    private void Abandon(ImmutableArray<Sequencer> lifetimes)
    {
        foreach (var lifetime in lifetimes)
        {
            _registry.Abandon(lifetime);
        }
    }
}
