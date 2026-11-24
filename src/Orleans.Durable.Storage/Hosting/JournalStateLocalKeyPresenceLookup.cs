using Orleans.Durable.Storage.Engine.Cluster.Control;
using Orleans.Durable.Storage.Engine.Journals;
using Orleans.Durable.Storage.Engine.Ledgers;

namespace Orleans.Durable.Storage.Hosting;

/// <summary>
/// Adapts <see cref="JournalStateDatabase.TryHasKeyEntryAsync"/> to <see cref="ILocalKeyPresenceLookup"/> so
/// <see cref="LocalStreamDrainCoordinator"/> can prove closed ledgers have no local-stream rows in O(1) without
/// reading batches or starting a predecessor scan (journals.md §8.3, archive.md §2.3).
/// </summary>
internal sealed class JournalStateLocalKeyPresenceLookup(JournalStateDatabase stateDatabase) : ILocalKeyPresenceLookup
{
    private readonly JournalStateDatabase _stateDatabase = stateDatabase ?? throw new ArgumentNullException(nameof(stateDatabase));

    /// <inheritdoc />
    public async ValueTask<LocalKeyPresenceRead> TryHasKeyEntryAsync(
        LedgerMetadata ledger,
        ReadOnlyMemory<byte> localKey,
        Slot tail,
        CancellationToken cancellationToken)
    {
        var result = await _stateDatabase.TryHasKeyEntryAsync(ledger, localKey, tail, cancellationToken).ConfigureAwait(false);
        return result switch
        {
            false => LocalKeyPresenceRead.Absent.Instance,
            true => LocalKeyPresenceRead.Present.Instance,
            null => LocalKeyPresenceRead.Unindexed.Instance,
        };
    }
}
