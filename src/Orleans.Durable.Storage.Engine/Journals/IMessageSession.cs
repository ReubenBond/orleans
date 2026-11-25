using Orleans.Durable.Storage.Engine.Ledgers;

namespace Orleans.Durable.Storage.Engine.Journals;

/// <summary>
/// Per-open-ledger primary session for one <see cref="MessageChannel"/> (journals.md §2.1, JS-I12).
/// </summary>
public interface IMessageSession : IDisposable
{
    /// <summary>
    /// Receives the non-empty message records for this session's channel from a batch decided at
    /// <paramref name="slot"/>, strictly in slot order, after quorum durability and before warm-state apply or
    /// caller <c>Accepted</c> (JS-I12).
    /// </summary>
    /// <param name="slot">The decided ledger slot of the batch.</param>
    /// <param name="records">One or more message records for this session's channel in batch order.</param>
    void OnBatchDurable(Slot slot, ReadOnlySpan<MessageRecord> records);
}
