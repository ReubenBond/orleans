namespace Orleans.Durable.Storage.Engine.Ledgers;

/// <summary>
/// The drop order of one ledger map value (LG-I10): for each ledger it lists, the listed ledger with the lowest ID below
/// its own whose range overlaps it, whose drop must come first. <see cref="LedgerMapValue"/> builds it once, on first use,
/// so a caller asking for many ledgers scans the value once. Immutable.
/// </summary>
/// <remarks>
/// Ranges are dyadic, so two overlap only when one contains the other. The build probes, for each listed ledger, its
/// range's ancestor at each shallower depth the value lists, one lookup each, and records the overlap in both directions:
/// <c>O(n·d)</c> lookups for <c>n</c> ledgers over <c>d</c> distinct depths, where a scan per ledger costs <c>O(n²)</c>.
/// </remarks>
internal sealed class LedgerMapLineage
{
    private readonly Dictionary<LedgerId, LedgerId> _older = [];

    /// <summary>Initializes a new instance of the <see cref="LedgerMapLineage"/> class over <paramref name="ledgers"/>.</summary>
    /// <param name="ledgers">The value's ledgers, in ascending ID order.</param>
    public LedgerMapLineage(IReadOnlyList<LedgerMetadata> ledgers)
    {
        // The ledgers come in ascending ID order, so the first ledger of a range has its lowest ID.
        var lowestOf = new Dictionary<KeyRange, LedgerId>();
        foreach (var ledger in ledgers)
        {
            _ = lowestOf.TryAdd(ledger.Range, ledger.Id);
        }

        int[] depths = [.. ledgers.Select(static ledger => ledger.Range.Depth).Distinct().Order()];
        var lowestAbove = new Dictionary<LedgerId, LedgerId>();
        var lowestWithin = new Dictionary<KeyRange, LedgerId>();
        foreach (var ledger in ledgers)
        {
            foreach (var depth in depths)
            {
                if (depth >= ledger.Range.Depth)
                {
                    break;
                }

                // A shallower depth leaves the range at least one bit, so its prefix holds a byte to probe with.
                var ancestor = KeyRange.Containing(ledger.Range.Prefix, depth);
                if (lowestOf.TryGetValue(ancestor, out var containing))
                {
                    lowestAbove[ledger.Id] = Lowest(lowestAbove, ledger.Id, containing);
                    lowestWithin[ancestor] = Lowest(lowestWithin, ancestor, ledger.Id);
                }
            }
        }

        foreach (var ledger in ledgers)
        {
            var lowest = lowestOf[ledger.Range];
            lowest = lowestAbove.TryGetValue(ledger.Id, out var above) && above < lowest ? above : lowest;
            lowest = lowestWithin.TryGetValue(ledger.Range, out var within) && within < lowest ? within : lowest;
            if (lowest < ledger.Id)
            {
                _older[ledger.Id] = lowest;
            }
        }
    }

    /// <summary>
    /// Returns the listed ledger with the lowest ID below <paramref name="ledger"/>'s whose range overlaps it;
    /// <see langword="null"/> when none does, or the value does not list <paramref name="ledger"/>.
    /// </summary>
    public LedgerId? OlderOverlapping(LedgerId ledger) => _older.TryGetValue(ledger, out var older) ? older : null;

    private static LedgerId Lowest<TKey>(Dictionary<TKey, LedgerId> lowest, TKey key, LedgerId candidate)
        where TKey : notnull =>
        lowest.TryGetValue(key, out var known) && known < candidate ? known : candidate;
}
