using System.Collections.Immutable;

namespace Orleans.Durable.Storage.Engine.Ledgers.Control;

/// <summary>
/// A ledger map value under a batch's changes: the fold applies each change here and materializes one value at the end
/// (ledgers.md §2.6). It copies the value's ledgers only at its first change.
/// </summary>
internal sealed class LedgerMapBuilder
{
    private readonly ImmutableArray<LedgerMetadata> _source;
    private List<LedgerMetadata>? _ledgers;

    public LedgerMapBuilder(LedgerMapValue value)
    {
        _source = value.Ledgers;
        Epoch = value.Epoch;
        NextLedgerId = value.NextLedgerId;
        RetirementFloor = value.RetirementFloor;
        EncodedLength = value.EncodedLength;
        foreach (var ledger in _source)
        {
            OpenCount += ledger.IsOpen ? 1 : 0;
        }
    }

    public ulong Epoch { get; }

    public LedgerId NextLedgerId { get; private set; }

    public ulong RetirementFloor { get; private set; }

    /// <summary>Gets the length of the value's encoding.</summary>
    public int EncodedLength { get; private set; }

    /// <summary>Gets the number of ledgers listed open.</summary>
    public int OpenCount { get; private set; }

    public int Count => _ledgers?.Count ?? _source.Length;

    public LedgerMetadata this[int index] => _ledgers is null ? _source[index] : _ledgers[index];

    /// <summary>Returns the position of <paramref name="id"/>, in ID order, or −1 when it is not listed.</summary>
    public int IndexOf(LedgerId id) => LedgerMapValue.IndexOf(AsSpan(), id);

    /// <summary>Returns the listed ledgers, in ID order, until the next change.</summary>
    public ReadOnlySpan<LedgerMetadata> AsSpan() =>
        _ledgers is null ? _source.AsSpan() : System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_ledgers);

    public LedgerMetadata? Get(LedgerId id) => IndexOf(id) is >= 0 and var index ? this[index] : null;

    /// <summary>Closes the open ledger at <paramref name="index"/> at <paramref name="t"/>.</summary>
    public void Close(int index, Slot t)
    {
        var ledgers = Ledgers();
        ledgers[index] = ledgers[index].Close(t);
        EncodedLength += sizeof(ulong);
        OpenCount--;
    }

    /// <summary>Removes the closed ledger at <paramref name="index"/>.</summary>
    public void Remove(int index)
    {
        var ledgers = Ledgers();
        var ledger = ledgers[index];
        EncodedLength -= LedgerMapValue.EntryLength(ledger.Range, ledger.Location, closed: true);
        ledgers.RemoveAt(index);
    }

    /// <summary>Lists <paramref name="opening"/> open under <see cref="NextLedgerId"/>, which it advances (LG-I3).</summary>
    public void Open(LedgerOpening opening)
    {
        Ledgers().Add(new LedgerMetadata(NextLedgerId, opening.Range, opening.Location, opening.K, opening.Primary, LedgerStatus.Open.Instance));
        EncodedLength += LedgerMapValue.EntryLength(opening.Range, opening.Location, closed: false);
        NextLedgerId = new LedgerId(NextLedgerId.Value + 1);
        OpenCount++;
    }

    public void RaiseFloor(ulong revision) => RetirementFloor = Math.Max(RetirementFloor, revision);

    /// <summary>Materializes the value, at the epoch of the value the builder began from.</summary>
    public LedgerMapValue ToValue() => new(Epoch, NextLedgerId, RetirementFloor, _ledgers is null ? _source : [.. _ledgers]);

    private List<LedgerMetadata> Ledgers() => _ledgers ??= [.. _source];
}
