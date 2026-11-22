using System.Collections.Immutable;
using System.Diagnostics;

namespace Orleans.Durable.Storage.Engine.Ledgers.Control;

/// <summary>
/// A transition a controller chooses (ledgers.md §2.6, §4): it closes open ledgers and opens new ones in one change of
/// the map. Each case carries its <see cref="TransitionChoice"/>: the node which executes it and becomes the primary of
/// every ledger it opens, the replicas and <c>K</c> of those ledgers, <c>ChosenAt</c>, and the epoch of the map its
/// controller chose it from.
/// </summary>
public readonly union Transition(
    Transition.Bootstrap,
    Transition.Replace,
    Transition.Split,
    Transition.Merge) : IEquatable<Transition>
{
    /// <inheritdoc/>
    public bool Equals(Transition other) => Equals(Value, other.Value);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is Transition other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => Value?.GetHashCode() ?? 0;

    /// <summary>Gets the transition's executor, placement, and provenance.</summary>
    /// <exception cref="InvalidOperationException">The transition holds no case.</exception>
    public TransitionChoice Choice => this switch
    {
        Bootstrap bootstrap => bootstrap.Choice,
        Replace replace => replace.Choice,
        Split split => split.Choice,
        Merge merge => merge.Choice,
        null => throw new InvalidOperationException("conventions.md §15: a transition holds a case."),
    };

    /// <summary>Gets the open ledgers the transition closes, in its case's order; none for a bootstrap.</summary>
    /// <exception cref="InvalidOperationException">The transition holds no case.</exception>
    public ImmutableArray<LedgerId> Closes => this switch
    {
        Bootstrap => [],
        Replace replace => [replace.Ledger],
        Split split => [split.Ledger],
        Merge merge => [merge.First, merge.Second],
        null => throw new InvalidOperationException("conventions.md §15: a transition holds a case."),
    };

    /// <summary>Opens one ledger over the whole keyspace, in a map which lists no ledger.</summary>
    /// <param name="Choice">The transition's executor, placement, and provenance.</param>
    public sealed record Bootstrap(TransitionChoice Choice);

    /// <summary>
    /// Closes <see cref="Ledger"/> at its recovered tail and opens a successor over its range: a takeover, a handoff,
    /// or a roll.
    /// </summary>
    public sealed record Replace
    {
        /// <summary>Initializes a new instance of the <see cref="Replace"/> class.</summary>
        /// <exception cref="ArgumentException"><paramref name="ledger"/> is the default.</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="predecessorHint"/> is not a defined hint.</exception>
        public Replace(LedgerId ledger, TransitionChoice choice, PredecessorReleaseHint predecessorHint = PredecessorReleaseHint.TakenOver)
        {
            if (predecessorHint is not (PredecessorReleaseHint.TakenOver or PredecessorReleaseHint.Cooperative))
            {
                throw new ArgumentOutOfRangeException(nameof(predecessorHint), predecessorHint, "A replace transition carries a defined predecessor release hint.");
            }

            Ledger = CheckLedger(ledger, nameof(ledger));
            Choice = choice;
            PredecessorHint = predecessorHint;
        }

        /// <summary>Gets the open ledger it closes.</summary>
        public LedgerId Ledger { get; }

        /// <summary>Gets the transition's executor, placement, and provenance.</summary>
        public TransitionChoice Choice { get; }

        /// <summary>
        /// Gets whether the controller observed the predecessor cooperatively drain and stop admitting before sending
        /// this replacement (<see cref="PredecessorReleaseHint.Cooperative"/>) or fell back to / started a non-cooperative
        /// takeover (<see cref="PredecessorReleaseHint.TakenOver"/>). Recovery always seals and recovers the predecessor
        /// regardless of this hint.
        /// </summary>
        public PredecessorReleaseHint PredecessorHint { get; }
    }

    /// <summary>Closes <see cref="Ledger"/> at its recovered tail and opens its range's two children.</summary>
    public sealed record Split
    {
        /// <summary>Initializes a new instance of the <see cref="Split"/> class.</summary>
        /// <exception cref="ArgumentException"><paramref name="ledger"/> is the default.</exception>
        public Split(LedgerId ledger, TransitionChoice choice)
        {
            Ledger = CheckLedger(ledger, nameof(ledger));
            Choice = choice;
        }

        /// <summary>Gets the open ledger it closes.</summary>
        public LedgerId Ledger { get; }

        /// <summary>Gets the transition's executor, placement, and provenance.</summary>
        public TransitionChoice Choice { get; }
    }

    /// <summary>Closes two open sibling ledgers at their recovered tails and opens their parent range.</summary>
    public sealed record Merge
    {
        /// <summary>Initializes a new instance of the <see cref="Merge"/> class.</summary>
        /// <exception cref="ArgumentException">A ledger is the default, or the two are one ledger.</exception>
        public Merge(LedgerId first, LedgerId second, TransitionChoice choice)
        {
            First = CheckLedger(first, nameof(first));
            Second = CheckLedger(second, nameof(second));
            if (first == second)
            {
                throw new ArgumentException("A merge closes two ledgers.", nameof(second));
            }

            Choice = choice;
        }

        /// <summary>Gets the first ledger it closes.</summary>
        public LedgerId First { get; }

        /// <summary>Gets the second ledger it closes, the first's sibling.</summary>
        public LedgerId Second { get; }

        /// <summary>Gets the transition's executor, placement, and provenance.</summary>
        public TransitionChoice Choice { get; }
    }

    private static LedgerId CheckLedger(LedgerId ledger, string parameterName) =>
        ledger == default ? throw new ArgumentException("A transition closes ledgers by ID.", parameterName) : ledger;
}

/// <summary>
/// Distinguishes whether a <see cref="Transition.Replace"/> followed a cooperative <c>PrepareRelease</c> drain on the
/// outgoing primary or a non-cooperative takeover (orleans.md §5.6, §5.7).
/// </summary>
/// <remarks>
/// This hint is informational for telemetry, logging, and verification: the successor's <see cref="TransitionExecutor"/>
/// still seals and recovers the predecessor ledger before installing the replacement (LP-I4, OR-I24).
/// </remarks>
public enum PredecessorReleaseHint
{
    /// <summary>
    /// Non-cooperative replacement: a failure takeover, same-primary roll/repair, or fallback after a timed-out, failed,
    /// declined, or concurrently sealed <c>PrepareRelease</c>.
    /// </summary>
    TakenOver = 0,

    /// <summary>
    /// Cooperative planned handoff: the outgoing primary answered <c>PrepareRelease</c> with <c>Released</c> after
    /// draining in-flight writes and stopping admission.
    /// </summary>
    Cooperative = 1,
}

/// <summary>
/// What every transition carries (ledgers.md §2.6): the node which executes it and becomes the primary of every ledger
/// it opens, the location and window of those ledgers, the revision of the composition's node view those nodes were
/// chosen from, and the epoch of the map the controller chose the transition from.
/// </summary>
/// <remarks>Equality compares locations, whose replicas compare element by element, in order.</remarks>
public readonly record struct TransitionChoice
{
    /// <summary>Initializes a new instance of the <see cref="TransitionChoice"/> struct.</summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="executor"/> or <paramref name="location"/> is the default, or <paramref name="location"/> is on
    /// nodes and <paramref name="executor"/> is not among its replicas.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="k"/> is below 1.</exception>
    public TransitionChoice(NodeId executor, LedgerLocation location, int k, ulong chosenAt, ulong epoch)
    {
        if (executor == default)
        {
            throw new ArgumentException("A transition names the node which executes it.", nameof(executor));
        }

        if (location.Value is null)
        {
            throw new ArgumentException("A transition names the location of the ledgers it opens.", nameof(location));
        }

        if (location.Value is LedgerLocation.Nodes nodes && !nodes.Replicas.Contains(executor))
        {
            throw new ArgumentException(
                "A transition's executor, the primary of every ledger it opens, is among their replicas when placed on nodes (ledgers.md §2.6).",
                nameof(executor));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(k, 1);
        Executor = executor;
        Location = location;
        K = k;
        ChosenAt = chosenAt;
        Epoch = epoch;
    }

    /// <summary>Initializes a new instance of the <see cref="TransitionChoice"/> struct for ledgers on <paramref name="replicas"/>.</summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="executor"/> is the default or not among <paramref name="replicas"/>, or <paramref name="replicas"/>
    /// is not an odd number, at most <see cref="LedgerLocation.MaxReplicas"/>, of distinct node identities.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="k"/> is below 1.</exception>
    public TransitionChoice(NodeId executor, ImmutableArray<NodeId> replicas, int k, ulong chosenAt, ulong epoch)
        : this(executor, new LedgerLocation.Nodes(replicas), k, chosenAt, epoch)
    {
    }

    /// <summary>Gets the node whose process executes the transition and becomes the primary of every ledger it opens.</summary>
    public NodeId Executor { get; }

    /// <summary>Gets where the storage of every ledger the transition opens is: its replicas' nodes, or the object store.</summary>
    public LedgerLocation Location { get; }

    /// <summary>Gets the replicas of every ledger the transition opens on nodes, in the map's order.</summary>
    /// <exception cref="InvalidOperationException">
    /// The choice places ledgers in the object store, which has no replicas: a caller which may meet one decides on
    /// <see cref="Location"/> first (ledgers.md §4).
    /// </exception>
    public ImmutableArray<NodeId> Replicas => Location switch
    {
        LedgerLocation.Nodes nodes => nodes.Replicas,
        LedgerLocation.ObjectStore => throw new InvalidOperationException("An object-store transition choice has no replicas (ledgers.md §4)."),
        null => throw new UnreachableException("conventions.md §15: a transition choice's location holds a case."),
    };

    /// <summary>Gets the window of every ledger the transition opens.</summary>
    public int K { get; }

    /// <summary>Gets the revision of the composition's node view the nodes were chosen from (LG-I11).</summary>
    public ulong ChosenAt { get; }

    /// <summary>Gets the epoch of the map the controller chose the transition from.</summary>
    public ulong Epoch { get; }

    /// <inheritdoc/>
    public bool Equals(TransitionChoice other) =>
        Executor == other.Executor && Location.Equals(other.Location) && K == other.K
        && ChosenAt == other.ChosenAt && Epoch == other.Epoch;

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Executor, Location, K, ChosenAt, Epoch);
}
