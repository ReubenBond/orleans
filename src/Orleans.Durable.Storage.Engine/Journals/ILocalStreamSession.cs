using Orleans.Durable.Storage.Engine.Ledgers;

namespace Orleans.Durable.Storage.Engine.Journals;

/// <summary>
/// Owns per-ledger runtime state for one local stream on its primary (journals.md §4.2, JS-I12).
/// <see cref="IDisposable.Dispose"/> runs once, strictly after the last possible <see cref="OnBatchDurable"/>.
/// </summary>
public interface ILocalStreamSession : IDisposable
{
    /// <summary>
    /// Invoked in strict slot order outside <see cref="LedgerAdmission"/>'s lock AFTER the batch is <c>Durable</c>,
    /// BEFORE warm-state <c>Apply</c> and BEFORE callers' write tasks complete (JS-I12). Always called with
    /// <paramref name="records"/> non-empty (skipped for streams with 0 records in the batch). Must perform no blocking I/O.
    /// </summary>
    void OnBatchDurable(Slot slot, ReadOnlySpan<LocalAppendRecord> records);
}
