using Orleans.Durable.Storage.Engine.Threading;

namespace Orleans.Durable.Storage.Testing;

/// <summary>A defect a <see cref="MutantCallBudget"/> carries, which a budget test's oracle must catch.</summary>
internal enum CallBudgetMutant
{
    /// <summary>None: the budget grants in arrival order and times a call out only at its deadline.</summary>
    None,

    /// <summary>A call which fits is granted ahead of an earlier call which does not; the order oracle catches it.</summary>
    Overtake,

    /// <summary>A call which lacks budget fails at once rather than waiting; the deadline oracle catches it.</summary>
    FailFast,

    /// <summary>A waiting call times out at half the time left before its deadline; the deadline oracle catches it.</summary>
    EarlyDeadline,
}

/// <summary>
/// A <see cref="CallBudget"/> which breaks one of its rules through the budget's policy seams, so a test shows that its
/// oracle catches that defect (ledger-replicas.md §2.2).
/// </summary>
internal sealed class MutantCallBudget(long maxBytes, int maxCalls, TimeProvider timeProvider, Func<TimeSpan, Exception> timeout, CallBudgetMutant mutant)
    : CallBudget(maxBytes, maxCalls, timeProvider, timeout)
{
    /// <summary>Gets the defect the budget carries.</summary>
    public CallBudgetMutant Mutant => mutant;

    protected override bool GrantsInArrivalOrder => mutant != CallBudgetMutant.Overtake;

    protected override bool WaitsForBudget => mutant != CallBudgetMutant.FailFast;

    protected override TimeSpan WaitFor(TimeSpan remaining) => mutant == CallBudgetMutant.EarlyDeadline ? remaining / 2 : remaining;
}
