namespace Orleans.Durable.Storage.Engine.Ledgers;

/// <summary>
/// One batch of a scan (ledgers.md §2.5): its <see cref="LedgerRead"/>, which its caller disposes, and, for a
/// <see cref="LedgerRead.Data"/>, the batch's resolved rows, which the scan's <see cref="LedgerSegmentView"/> lends until
/// the scan advances. The scan owns the view and disposes it when the enumeration ends.
/// </summary>
/// <remarks>
/// The rows are lent over the batch's bytes, which the item's <see cref="LedgerRead.Data"/> owns: its caller reads them
/// before it disposes the read, and before the scan advances.
/// </remarks>
public readonly struct LedgerScanItem
{
    private readonly LedgerSegmentView? _view;
    private readonly long _advance;

    internal LedgerScanItem(LedgerRead read, LedgerSegmentView? view)
    {
        Read = read;
        _view = view;
        _advance = view?.Advances ?? 0;
    }

    /// <summary>Gets the read of the batch's slot, which its caller disposes.</summary>
    public LedgerRead Read { get; }

    /// <summary>Gets the batch's resolved rows, which are valid until the scan advances and its read is disposed.</summary>
    /// <exception cref="InvalidOperationException">
    /// The item holds no batch, a <c>Dropped</c> or <c>Backpressure</c>; or the scan has advanced past it or ended.
    /// </exception>
    public LedgerSegmentView.RowEnumerable Rows =>
        (_view ?? throw new InvalidOperationException("The scan's item holds no batch, so it lends no rows.")).RowsAt(_advance);
}
