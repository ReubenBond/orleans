using Orleans.Durable.Storage.Engine.Ledgers;

namespace Orleans.Durable.Storage.Engine.Archive.Control;

/// <summary>
/// Barrier invoked by archive drain workers before completing or dropping a closed ledger (LG-I16),
/// and during segment scans to validate skipped <see cref="RowKind.LocalAppend"/> keys fail-closed (LG-I15).
/// </summary>
public interface ILedgerLocalDrainBarrier
{
    /// <summary>
    /// Validates that <paramref name="rowKey"/> of a <see cref="RowKind.LocalAppend"/> row in <paramref name="ledger"/>
    /// is a valid <see cref="LedgerLocalKey"/> belonging to a registered local stream; throws
    /// <see cref="ArchiveCorruptException"/> otherwise.
    /// </summary>
    /// <param name="ledger">The ledger being scanned.</param>
    /// <param name="rowKey">The raw key bytes of the <see cref="RowKind.LocalAppend"/> row.</param>
    /// <exception cref="ArchiveCorruptException">The key is malformed, belongs to another ledger or range, or names an unregistered stream.</exception>
    void ValidateLocalAppendKey(LedgerMetadata ledger, ReadOnlySpan<byte> rowKey);

    /// <summary>
    /// Durably drains every registered ledger-local stream of closed ledger <paramref name="ledger"/> through
    /// <paramref name="tail"/>. Idempotent across retries and racing drainers.
    /// </summary>
    /// <param name="ledger">The closed ledger metadata.</param>
    /// <param name="tail">The closed ledger's recovered tail <c>T</c>.</param>
    /// <param name="cancellationToken">Cancels the drain attempt.</param>
    /// <returns>
    /// <see cref="LocalDrainBarrierOutcome.Drained"/> once all registered streams are durably drained,
    /// <see cref="LocalDrainBarrierOutcome.Dropped"/> once confirmed unlisted in the ledger map, or
    /// <see cref="LocalDrainBarrierOutcome.Backpressure"/> when a replica is saturated.
    /// </returns>
    ValueTask<LocalDrainBarrierOutcome> EnsureLocalStreamsDrainedAsync(
        LedgerMetadata ledger,
        Slot tail,
        CancellationToken cancellationToken);
}
