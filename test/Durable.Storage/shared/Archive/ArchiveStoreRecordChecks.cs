using Orleans.Durable.Storage.Engine.Archive;

namespace Orleans.Durable.Storage.Testing;

/// <summary>
/// The two checks of a store's record together, as the stores' tests make them (archive.md §2.4): the store's fields, which
/// <see cref="IArchiveStore.StoreRecordAsync"/> checks, then the caller's parameters, which
/// <see cref="ArchiveStoreRecord.CheckParametersMatch"/> checks.
/// </summary>
internal static class ArchiveStoreRecordChecks
{
    /// <summary>Returns the ID of the store's record, which the call creates when absent, once both halves of it match.</summary>
    /// <exception cref="ArchiveStoreMismatchException">A store field or a caller parameter differs, which the exception names.</exception>
    public static async ValueTask<ArchiveStoreId> CheckedIdAsync(this IArchiveStore store, IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        var record = await store.StoreRecordAsync(parameters, cancellationToken).ConfigureAwait(false);
        record.CheckParametersMatch(parameters);
        return record.Id;
    }
}
