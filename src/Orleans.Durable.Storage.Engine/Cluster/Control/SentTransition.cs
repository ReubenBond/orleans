using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.Ledgers.Control;

namespace Orleans.Durable.Storage.Engine.Cluster.Control;

/// <summary>Why the controller sent a transition, which decides the budget it counts against.</summary>
internal enum TransitionPurpose
{
    /// <summary>Installs the first ledger over the whole keyspace.</summary>
    Bootstrap,

    /// <summary>Replaces a ledger whose primary claim ended (orleans.md §5.7); never limited.</summary>
    Takeover,

    /// <summary>
    /// Replaces a ledger to change its replicas for the configuration's <c>N</c> and <c>K</c>, for failure domains, or to
    /// even the nodes' replica counts; it adds no node under disk pressure.
    /// </summary>
    Roll,

    /// <summary>
    /// Replaces a ledger to roll out a replica dead past <c>G</c> or retired, or to replace a replica its node lost (LR-I2)
    /// with a fresh one, on the same node, of the successor; its primary executes it, within <c>MaxConcurrentRepairs</c>.
    /// </summary>
    Repair,

    /// <summary>Replaces a ledger whose replica's node asked to release its WAL (OR-I13); never limited.</summary>
    WalReleaseRoll,

    /// <summary>Moves a ledger's primary: for balance, or off a retired primary (OR-I3).</summary>
    Handoff,

    /// <summary>Moves a ledger's primary off a departing node or one whose hand-off is pending; within <c>MaxConcurrentHandOffs</c>.</summary>
    Departure,

    /// <summary>Splits a ledger at its midpoint.</summary>
    Split,

    /// <summary>Merges two open siblings.</summary>
    Merge,
}

/// <summary>A transition the controller sends, and why.</summary>
/// <param name="transition">The transition, whose choice names its executor.</param>
/// <param name="purpose">Why it was chosen.</param>
/// <remarks>Its equality is reference equality: the controller matches a reply to the send it answers.</remarks>
internal sealed class SentTransition(Transition transition, TransitionPurpose purpose)
{
    /// <summary>Gets the transition, whose choice names its executor.</summary>
    public Transition Transition { get; } = transition;

    /// <summary>Gets why it was chosen.</summary>
    public TransitionPurpose Purpose { get; } = purpose;

    /// <summary>Gets the node which executes it.</summary>
    public NodeId Executor => Transition.Choice.Executor;

    /// <summary>
    /// Gets whether it counts against <c>MaxConcurrentTransitions</c>: every purpose but a takeover, a WAL-release roll, a
    /// repair, and a departure.
    /// </summary>
    public bool IsBalancing => Purpose is not (TransitionPurpose.Takeover or TransitionPurpose.WalReleaseRoll or TransitionPurpose.Repair or TransitionPurpose.Departure);

    /// <inheritdoc/>
    public override string ToString() => $"{Purpose} {Transition.Value} by {Executor}";
}
