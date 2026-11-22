using System.Globalization;
using Orleans.Testing.Simulation.Runtime.Decisions;
using Orleans.Testing.Simulation.Runtime.Scheduling.Resources;

namespace Orleans.Testing.Simulation.Runtime.Scheduling.Strategies;

/// <summary>
/// <para>
/// Reproduces a previously recorded schedule exactly by replaying the
/// <see cref="SimulationDecisionKind.SchedulingOrder"/> decisions captured during an earlier run
/// (typically produced by <see cref="SeededRandomSchedulingStrategy"/> with a decision log attached).
/// On each real choice it consumes the next recorded selection and grants the baton to the operation
/// whose id matches it.
/// </para>
/// <para>
/// It fails fast at the <em>first</em> divergence - if the recorded log is exhausted before the run
/// finishes making choices, or the recorded operation is not currently runnable - by throwing
/// <see cref="SimulationDecisionReplayMismatchException"/>, exactly as the runtime policy replay contract
/// requires. This is the scheduler-side counterpart of
/// <see cref="SimulationDecisionReplayValidator"/>: the validator checks decisions made <em>inside</em>
/// operations, while this drives the scheduling decisions themselves.
/// </para>
/// </summary>
internal sealed class ReplaySchedulingStrategy :
    ISimulationSchedulingStrategy,
    ISimulationSchedulingStrategyRuntimeHooks
{
    private readonly List<SimulationDecisionRecord> _scheduling;
    private readonly List<SimulationDecisionRecord> _resourceWinners;
    private int _schedulingIndex;
    private int _resourceWinnerIndex;
    private bool _mismatchAlreadyThrown;

    /// <summary>
    /// Initializes a new instance of the <see cref="ReplaySchedulingStrategy"/> class from a recorded
    /// decision sequence. Only <see cref="SimulationDecisionKind.SchedulingOrder"/> records are used;
    /// any other decisions (e.g. those recorded inside operation bodies) are ignored, so the same log
    /// can be passed here and to a <see cref="SimulationDecisionReplayValidator"/>.
    /// </summary>
    /// <param name="recordedDecisions">The decisions recorded during the original run, in order.</param>
    internal ReplaySchedulingStrategy(IReadOnlyList<SimulationDecisionRecord> recordedDecisions)
    {
        ArgumentNullException.ThrowIfNull(recordedDecisions);
        var scheduling = new List<SimulationDecisionRecord>(recordedDecisions.Count);
        var resourceWinners = new List<SimulationDecisionRecord>();
        foreach (var record in recordedDecisions)
        {
            if (record.Kind == SimulationDecisionKind.SchedulingOrder)
            {
                scheduling.Add(record);
            }
            else if (record.Kind == SimulationDecisionKind.ResourceWinner)
            {
                resourceWinners.Add(record);
            }
        }

        _scheduling = scheduling;
        _resourceWinners = resourceWinners;
    }

    /// <inheritdoc/>
    public string Name => "replay";

    /// <inheritdoc/>
    public bool RecordsNondeterministicChoices => false;

    /// <inheritdoc/>
    public SimulationOperation ChooseNext(SimulationSchedulingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Runnable.Count == 1)
        {
            // Single-candidate steps were never recorded, so they must not consume the replay stream.
            return context.Runnable[0];
        }

        if (_schedulingIndex >= _scheduling.Count)
        {
            _mismatchAlreadyThrown = true;
            throw new SimulationDecisionReplayMismatchException(
                "Replay diverged: the recorded scheduling log is exhausted, but the scheduler still has " +
                $"a choice among {context.Runnable.Count} runnable operations to make.");
        }

        var expected = _scheduling[_schedulingIndex++];
        LastDecisionSourceId = expected.SourceId;
        for (var index = 0; index < context.Runnable.Count; index++)
        {
            SimulationOperation operation = context.Runnable[index];
            if (string.Equals(FormatId(operation.Id), expected.SelectedResult, StringComparison.Ordinal))
            {
                return operation;
            }
        }

        throw new SimulationDecisionReplayMismatchException(
            $"Replay diverged at scheduling decision {expected.Id}: the recorded run selected operation " +
            $"'{expected.SelectedResult}', but it is not among the currently runnable operations " +
            $"[{FormatCandidates(context.Runnable)}].");
    }

    /// <summary>Gets the source identity of the decision most recently consumed by the strategy.</summary>
    internal string? LastDecisionSourceId { get; private set; }

    /// <summary>Chooses a resource waiter from a replayed resource-winner decision.</summary>
    internal int ChooseResourceWaiter(IReadOnlyList<SimulationResourceWaiterInfo> waiters)
    {
        ArgumentNullException.ThrowIfNull(waiters);
        if (waiters.Count == 1)
        {
            return 0;
        }

        if (_resourceWinnerIndex >= _resourceWinners.Count)
        {
            _mismatchAlreadyThrown = true;
            throw new SimulationDecisionReplayMismatchException(
                "Replay diverged: the recorded resource-winner stream is exhausted, but a signal must choose " +
                $"among {waiters.Count} pending waiters.");
        }

        SimulationDecisionRecord expected = _resourceWinners[_resourceWinnerIndex++];
        LastDecisionSourceId = expected.SourceId;
        for (var index = 0; index < waiters.Count; index++)
        {
            if (string.Equals(
                FormatId(waiters[index].OperationId),
                expected.SelectedResult,
                StringComparison.Ordinal))
            {
                return index;
            }
        }

        _mismatchAlreadyThrown = true;
        throw new SimulationDecisionReplayMismatchException(
            $"Replay diverged at resource-winner decision {expected.Id}: the recorded run selected operation " +
            $"'{expected.SelectedResult}', but it is not among the pending waiters [{FormatWaiters(waiters)}].");
    }

    /// <summary>
    /// Verifies that a successful replay consumed every recorded scheduling choice.
    /// </summary>
    /// <remarks>
    /// Partial or aborted runs must not call this method; unread choices are expected until the scheduler
    /// reaches a successful quiescent boundary.
    /// </remarks>
    /// <exception cref="SimulationDecisionReplayMismatchException">
    /// Thrown when one or more recorded scheduling choices remain unconsumed.
    /// </exception>
    internal void ValidateComplete()
    {
        if (_mismatchAlreadyThrown)
        {
            return;
        }

        if (_schedulingIndex < _scheduling.Count)
        {
            var expected = _scheduling[_schedulingIndex];
            var remaining = _scheduling.Count - _schedulingIndex;
            _mismatchAlreadyThrown = true;
            throw new SimulationDecisionReplayMismatchException(
                $"Replay diverged at end of run: {remaining} recorded scheduling decision(s) remain unconsumed; " +
                $"the next is {expected.Id}, which selected operation '{expected.SelectedResult}'.");
        }

        if (_resourceWinnerIndex < _resourceWinners.Count)
        {
            var expected = _resourceWinners[_resourceWinnerIndex];
            var remaining = _resourceWinners.Count - _resourceWinnerIndex;
            _mismatchAlreadyThrown = true;
            throw new SimulationDecisionReplayMismatchException(
                $"Replay diverged at end of run: {remaining} recorded resource-winner decision(s) remain unconsumed; " +
                $"the next is {expected.Id}, which selected operation '{expected.SelectedResult}'.");
        }
    }

    internal static string FormatId(SimulationOperationId id) =>
        id.Value.ToString(CultureInfo.InvariantCulture);

    int ISimulationSchedulingStrategyRuntimeHooks.ChooseResourceWaiter(
        IReadOnlyList<SimulationResourceWaiterInfo> waiters) =>
        ChooseResourceWaiter(waiters);

    string ISimulationSchedulingStrategyRuntimeHooks.DecisionSourceId =>
        LastDecisionSourceId ?? Name;

    void ISimulationSchedulingStrategyRuntimeHooks.ValidateComplete() =>
        ValidateComplete();

    private static string FormatCandidates(IReadOnlyList<SimulationOperation> runnable)
    {
        var ids = new string[runnable.Count];
        for (var i = 0; i < runnable.Count; i++)
        {
            ids[i] = FormatId(runnable[i].Id);
        }

        return string.Join(",", ids);
    }

    private static string FormatWaiters(IReadOnlyList<SimulationResourceWaiterInfo> waiters)
    {
        var ids = new string[waiters.Count];
        for (var index = 0; index < waiters.Count; index++)
        {
            ids[index] = FormatId(waiters[index].OperationId);
        }

        return string.Join(",", ids);
    }
}
