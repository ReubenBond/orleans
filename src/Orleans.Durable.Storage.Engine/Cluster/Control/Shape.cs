using System.Collections.Immutable;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.Ledgers.Control;

namespace Orleans.Durable.Storage.Engine.Cluster.Control;

/// <summary>
/// A transition before placement: the ledgers it closes and how many ranges it opens, from which, once placement chose its
/// replicas, the controller makes the transition.
/// </summary>
internal sealed class Shape
{
    private readonly Func<TransitionChoice, Transition> _make;

    private Shape(Func<TransitionChoice, Transition> make, ImmutableArray<LedgerId> closes, ImmutableArray<KeyRange> opens, bool isReplace = false)
    {
        _make = make;
        Closes = closes;
        Opens = opens;
        IsReplace = isReplace;
    }

    /// <summary>Gets the bootstrap's shape: it closes nothing and opens the whole keyspace.</summary>
    public static Shape Bootstrap { get; } = new(static choice => new Transition.Bootstrap(choice), [], [KeyRange.Root]);

    /// <summary>Gets the ledgers the transition closes.</summary>
    public ImmutableArray<LedgerId> Closes { get; }

    /// <summary>Gets the ranges the transition opens.</summary>
    public ImmutableArray<KeyRange> Opens { get; }

    /// <summary>Gets whether this shape builds a <see cref="Transition.Replace"/>.</summary>
    public bool IsReplace { get; }

    /// <summary>Returns the shape of a replace of <paramref name="ledger"/>: a takeover, a handoff, or a roll.</summary>
    public static Shape Replace(LedgerMetadata ledger, PredecessorReleaseHint predecessorHint = PredecessorReleaseHint.TakenOver) =>
        new(choice => new Transition.Replace(ledger.Id, choice, predecessorHint), [ledger.Id], [ledger.Range], isReplace: true);

    /// <summary>Returns the shape of a split of <paramref name="ledger"/> into its range's children.</summary>
    public static Shape Split(LedgerMetadata ledger)
    {
        var (lower, upper) = ledger.Range.Children;
        return new(choice => new Transition.Split(ledger.Id, choice), [ledger.Id], [lower, upper]);
    }

    /// <summary>Returns the shape of a merge of the sibling ledgers <paramref name="first"/> and <paramref name="second"/>.</summary>
    public static Shape Merge(LedgerMetadata first, LedgerMetadata second) =>
        new(choice => new Transition.Merge(first.Id, second.Id, choice), [first.Id, second.Id], [first.Range.Parent]);

    /// <summary>Returns the transition with <paramref name="choice"/>.</summary>
    public Transition Make(TransitionChoice choice) => _make(choice);
}
