using System.Collections.Immutable;

namespace Orleans.Durable.Storage.Engine.Ledgers.Control;

/// <summary>What a transition's install adds to the ledger map's encoding, which a controller counts against the register's value limit (orleans.md §5.6).</summary>
public static class LedgerMapGrowth
{
    /// <summary>
    /// Returns the bytes the install of <paramref name="transition"/> adds to <paramref name="map"/>'s encoding: the entry
    /// of each ledger it opens, with its choice's replicas, and the tail each ledger it closes records (ledgers.md §7).
    /// </summary>
    /// <exception cref="ArgumentException">The transition holds no case, a ledger it closes is not listed open, a split's
    /// range is at <see cref="KeyRange.MaxDepth"/>, or a merge's ledgers own no sibling ranges.</exception>
    public static int GrowthOf(this LedgerMapValue map, Transition transition)
    {
        ArgumentNullException.ThrowIfNull(map);
        if (transition.Value is null)
        {
            throw new ArgumentException("A transition holds a case.", nameof(transition));
        }

        var closes = transition.Closes;
        var closing = ImmutableArray.CreateBuilder<LedgerMetadata>(closes.Length);
        foreach (var id in closes)
        {
            closing.Add(map.Get(id) is { IsOpen: true } ledger ? ledger : throw new ArgumentException("A transition closes ledgers the map lists open.", nameof(transition)));
        }

        var location = transition.Choice.Location;
        var growth = closes.Length * sizeof(ulong);
        foreach (var range in TransitionExecutor.Opens(transition, closing.MoveToImmutable()))
        {
            growth += LedgerMapValue.EntryLength(range, location, closed: false);
        }

        return growth;
    }
}
