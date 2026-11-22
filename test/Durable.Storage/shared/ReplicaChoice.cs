using System.Collections.Immutable;
using Orleans.Durable.Storage.Engine.Cluster.Control;
using Orleans.Durable.Storage.Engine.Ledgers;

namespace Orleans.Durable.Storage.Testing;

/// <summary>
/// The standing at <c>ChosenAt</c> of the primary of the ledger a transition closes, from which OR-I5 decides whether the
/// transition may place degraded.
/// </summary>
internal enum PredecessorPrimary
{
    /// <summary>Its claim stands and it is not leaving: no transition of its ledger may degrade.</summary>
    Serving,

    /// <summary>Its claim ended: membership declared it <c>Dead</c>, or it reported the ledger <c>Unserved</c>. A takeover may degrade.</summary>
    Ended,

    /// <summary>It is retired, departing, or has its hand-off pending: the hand-off of its ledger may degrade.</summary>
    Leaving,
}

/// <summary>
/// OR-I5 as orleans.md §5.1 and §5.6 state it, the one oracle of a sent transition's replicas: <c>N</c> distinct nodes, the
/// executor first, each of which may be given work (active in the registry value at <c>ChosenAt</c>, live in the
/// controller's membership view, with no hand-off pending); or, for a takeover of a ledger whose primary's claim ended or
/// a hand-off of a ledger whose primary is leaving, chosen while fewer than <c>N</c> nodes may be given work, a degraded
/// choice: every node which may, at least <c>Q = (N + 1) / 2</c>, then the closed ledger's replicas which membership shows
/// dead and the registry active, in their order, as many as fill <c>N</c>.
/// </summary>
internal static class ReplicaChoice
{
    /// <summary>Returns the violation <paramref name="sent"/>'s replicas show, or <see langword="null"/> when they satisfy OR-I5.</summary>
    /// <param name="sent">The transition the controller sent.</param>
    /// <param name="replicas">The configuration's <c>N</c>.</param>
    /// <param name="predecessor">The ledger a <c>Replace</c> closes, as the controller's map listed it; otherwise <see langword="null"/>.</param>
    /// <param name="primary">The standing of <paramref name="predecessor"/>'s primary at <c>ChosenAt</c>.</param>
    /// <param name="givenWork">The nodes which may be given work at <c>ChosenAt</c>: active, live, and with no hand-off pending.</param>
    /// <param name="isDeadActive">Whether membership shows a node dead and the registry at <c>ChosenAt</c> holds it active.</param>
    public static string? Violation(SentTransition sent, int replicas, LedgerMetadata? predecessor, PredecessorPrimary primary, IReadOnlySet<NodeId> givenWork, Func<NodeId, bool> isDeadActive)
    {
        var choice = sent.Transition.Choice;
        var chosen = choice.Replicas;
        if (chosen.Length != replicas || chosen.Distinct().Count() != replicas || chosen[0] != choice.Executor)
        {
            return $"OR-I5: {sent} names {string.Join(", ", chosen)}, not {replicas} distinct nodes with its executor first.";
        }

        var live = chosen.TakeWhile(givenWork.Contains).Count();
        if (live == replicas)
        {
            return null;
        }

        ImmutableArray<NodeId> kept = [.. chosen.Skip(live)];
        if (predecessor is not { } replaced || primary == PredecessorPrimary.Serving)
        {
            return $"OR-I5: {sent} keeps {string.Join(", ", kept)}, which may be given no work, though its ledger's primary neither ended its claim nor is leaving.";
        }

        var agrees = primary == PredecessorPrimary.Ended
            ? sent.Purpose == TransitionPurpose.Takeover
            : sent.Purpose is TransitionPurpose.Handoff or TransitionPurpose.Departure;
        if (!agrees)
        {
            return $"OR-I5: {sent} places degraded as {sent.Purpose}, though its ledger's primary is {primary}.";
        }

        if (live < (replicas + 1) / 2)
        {
            return $"OR-I5: {sent} names {live} nodes which may be given work, fewer than its quorum.";
        }

        if (live != givenWork.Count)
        {
            return $"OR-I5: {sent} keeps {string.Join(", ", kept)} after {live} of the {givenWork.Count} nodes which may be given work.";
        }

        ImmutableArray<NodeId> expected = [.. replaced.Replicas.Where(isDeadActive).Take(replicas - live)];
        return kept.SequenceEqual(expected)
            ? null
            : $"OR-I5: {sent} keeps {string.Join(", ", kept)}, not s{replaced.Id}'s replicas dead in membership and active in the registry, in their order: {string.Join(", ", expected)}.";
    }
}
