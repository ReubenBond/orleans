using Orleans.Durable.Storage.Engine.Ledgers;

namespace Orleans.Durable.Storage.Simulation;

/// <summary>
/// One event of a <see cref="LedgerMapTrace"/>: a ledger map value the register applied, or a proposal answered durable.
/// </summary>
/// <param name="Value">The applied value, or <see langword="null"/> for a durable answer.</param>
/// <param name="Ledger">The durable answer's ledger.</param>
/// <param name="Slot">The durable answer's slot.</param>
/// <param name="Key">The first byte of the durable answer's key.</param>
public readonly record struct LedgerMapTraceEvent(LedgerMapValue? Value, LedgerId Ledger, long Slot, byte Key)
{
    /// <summary>Returns the event of a value the register applied.</summary>
    public static LedgerMapTraceEvent Applied(LedgerMapValue value) => new(value, default, 0, 0);

    /// <summary>Returns the event of a proposal answered durable at <paramref name="slot"/> of <paramref name="ledger"/>.</summary>
    public static LedgerMapTraceEvent Durable(LedgerId ledger, long slot, byte key) => new(null, ledger, slot, key);
}

/// <summary>
/// One decided batch of a ledger, as a scan read it: a row of one key, defining or referencing it in its segment, or a
/// marker, a batch of no rows; and whether it starts a segment (ledgers.md §2.2).
/// </summary>
/// <param name="Slot">The batch's slot.</param>
/// <param name="Key">The first byte of its row's key, or <see langword="null"/> for a marker.</param>
/// <param name="StartsSegment">Whether the batch starts a segment.</param>
/// <param name="Defining">Whether its row defines its key in the segment.</param>
public readonly record struct LedgerMapTraceBatch(long Slot, byte? Key, bool StartsSegment, bool Defining);

/// <summary>
/// A simulation's ledger map trace, which <see cref="LedgerMapTraceExport"/> renders for validation against
/// <c>specs/ledgers/LedgerMap.qnt</c> (docs/spec/verification.md §5.1): every value the register applied, in order,
/// interleaved with the proposals answered durable, and each traced ledger's decided batches. Recording takes no step of
/// the simulation, so it leaves a run's schedule as it is.
/// </summary>
public sealed class LedgerMapTrace
{
    private readonly List<LedgerMapTraceEvent> _events = [];
    private readonly Dictionary<LedgerId, SortedDictionary<long, LedgerMapTraceBatch>> _batches = [];
    private int _observed;

    /// <summary>Gets the events, in the order the run produced them.</summary>
    public IReadOnlyList<LedgerMapTraceEvent> Events => _events;

    /// <summary>Gets each traced ledger's decided batches, by slot.</summary>
    public IReadOnlyDictionary<LedgerId, SortedDictionary<long, LedgerMapTraceBatch>> Batches => _batches;

    /// <summary>Records a value the register applied.</summary>
    public void Applied(LedgerMapValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        _events.Add(LedgerMapTraceEvent.Applied(value));
    }

    /// <summary>
    /// Records each value of <paramref name="appliedWrites"/>, a register's applied writes in order, which an earlier
    /// observation did not record.
    /// </summary>
    /// <exception cref="InvalidOperationException">A write is not a ledger map value.</exception>
    public void Observe(IReadOnlyList<byte[]> appliedWrites)
    {
        ArgumentNullException.ThrowIfNull(appliedWrites);
        for (; _observed < appliedWrites.Count; _observed++)
        {
            Applied(LedgerMapValue.Decode(appliedWrites[_observed].AsSpan()).Value as LedgerMapValue
                ?? throw new InvalidOperationException($"The register's write {_observed} is not a ledger map value."));
        }
    }

    /// <summary>Records a proposal of <paramref name="key"/> answered durable at <paramref name="slot"/> of <paramref name="ledger"/>.</summary>
    public void Durable(LedgerId ledger, Slot slot, byte key) => _events.Add(LedgerMapTraceEvent.Durable(ledger, slot.Value, key));

    /// <summary>Records a decided batch of <paramref name="ledger"/>, replacing an earlier record of its slot.</summary>
    public void Batch(LedgerId ledger, LedgerMapTraceBatch batch)
    {
        if (!_batches.TryGetValue(ledger, out var batches))
        {
            _batches[ledger] = batches = [];
        }

        batches[batch.Slot] = batch;
    }

    /// <summary>Records the batch a scan of <paramref name="ledger"/> read at <paramref name="slot"/>.</summary>
    /// <exception cref="InvalidOperationException">The batch holds more than one row, which the model does not express.</exception>
    public void Batch(LedgerId ledger, long slot, LedgerScanItem item, LedgerRead.Data data)
    {
        var view = data.View;
        if (view.Count == 0)
        {
            Batch(ledger, new LedgerMapTraceBatch(slot, null, view.IsSegmentStart, Defining: false));
            return;
        }

        if (view.Count > 1)
        {
            throw new InvalidOperationException($"s{ledger}'s slot {slot} holds {view.Count} rows; the model's batches hold one.");
        }

        var defining = false;
        foreach (var row in view)
        {
            defining = row.IsDefining;
        }

        byte key = 0;
        foreach (var row in item.Rows)
        {
            key = row.Key[0];
        }

        Batch(ledger, new LedgerMapTraceBatch(slot, key, view.IsSegmentStart, defining));
    }
}
