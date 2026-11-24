using Orleans.Durable.Storage.Engine.Ledgers;

namespace Orleans.Durable.Storage.Engine.Cluster.Control;

/// <summary>
/// Control-plane processor that folds and durably commits a ledger-local stream when a closed ledger drains
/// (archive.md §2.3, orleans.md §5.6, LG-I16).
/// </summary>
public interface ILocalStreamProcessor
{
    /// <summary>Gets the canonical ledger-local stream name owned by this processor.</summary>
    LocalStreamName Stream { get; }

    /// <summary>Begins a fresh fold session for closed ledger <paramref name="ledger"/> through <paramref name="tail"/>.</summary>
    /// <param name="ledger">The closed ledger metadata.</param>
    /// <param name="tail">The closed ledger's recovered tail <c>T</c>.</param>
    /// <returns>A disposable fold session receiving <see cref="RowKind.LocalAppend"/> payloads in slot and row order.</returns>
    ILocalStreamFold BeginClosedLedgerFold(LedgerMetadata ledger, Slot tail);
}
