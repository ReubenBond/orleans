using Orleans.Durable.Storage.Engine.Ledgers;

namespace Orleans.Durable.Storage.Engine.Archive.Control;

/// <summary>
/// Control-plane processor for one <see cref="MessageChannel"/> invoked during single-pass segment drains
/// (archive.md §8.1, LG-I15, LG-I16).
/// </summary>
public interface IMessageProcessor
{
    /// <summary>Gets the message channel owned by this processor.</summary>
    MessageChannel Channel { get; }

    /// <summary>
    /// Opens a fresh segment-scoped fold for <paramref name="segment"/> of <paramref name="ledger"/>.
    /// </summary>
    IMessageFold BeginSegmentFold(LedgerMetadata ledger, LedgerSegmentId segment);
}
