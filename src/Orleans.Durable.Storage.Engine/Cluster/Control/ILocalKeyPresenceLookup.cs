using Orleans.Durable.Storage.Engine.Ledgers;

namespace Orleans.Durable.Storage.Engine.Cluster.Control;

/// <summary>
/// Non-blocking O(1) presence check over a node's caught-up local key index (journals.md §8.3), allowing
/// <see cref="LocalStreamDrainCoordinator"/> to prove a closed ledger has no <see cref="RowKind.LocalAppend"/> rows
/// for a registered stream without reading any batches or triggering a predecessor scan.
/// </summary>
public interface ILocalKeyPresenceLookup
{
    /// <summary>
    /// Checks whether the local node's caught-up key index holds any entry for <paramref name="localKey"/> in
    /// <paramref name="ledger"/> through <paramref name="tail"/>.
    /// </summary>
    /// <param name="ledger">The closed ledger metadata.</param>
    /// <param name="localKey">The encoded <see cref="LedgerLocalKey"/> bytes.</param>
    /// <param name="tail">The closed ledger's recovered tail <c>T</c>.</param>
    /// <param name="cancellationToken">Cancels the probe.</param>
    /// <returns>
    /// <see cref="LocalKeyPresenceRead.Absent"/> when proven absent in a caught-up local index,
    /// <see cref="LocalKeyPresenceRead.Present"/> when present, or
    /// <see cref="LocalKeyPresenceRead.Unindexed"/> when the caller must fall back to a sequential scan.
    /// </returns>
    ValueTask<LocalKeyPresenceRead> TryHasKeyEntryAsync(
        LedgerMetadata ledger,
        ReadOnlyMemory<byte> localKey,
        Slot tail,
        CancellationToken cancellationToken);
}
