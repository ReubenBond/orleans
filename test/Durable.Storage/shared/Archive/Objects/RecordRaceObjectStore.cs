using Orleans.Durable.Storage.Engine.Archive;
using Orleans.Durable.Storage.Engine.Archive.Lsm;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.ObjectStore;

namespace Orleans.Durable.Storage.Testing;

/// <summary>
/// An <see cref="IObjectStore"/> over another which can make the next create of a store's record race another process's:
/// the competing record lands first, so the create answers <c>Exists</c>, or the create answers <c>Exists</c> while the
/// record stays absent, as a store which contradicts itself would, or the create lands and answers <c>Exists</c>, as a
/// client which retried its own landed attempt does; every other call passes through.
/// </summary>
/// <param name="inner">The object store.</param>
/// <param name="record">The name of the store's record.</param>
internal sealed class RecordRaceObjectStore(IObjectStore inner, ObjectName record) : IObjectStore
{
    private (bool Armed, ArchiveStoreRecord? Winner, bool OwnAttempt) _race;

    /// <summary>
    /// Makes the next create of the record find <paramref name="winner"/> created first, or, when it is
    /// <see langword="null"/>, answer <c>Exists</c> without creating anything.
    /// </summary>
    public void RaceNextCreate(ArchiveStoreRecord? winner) => _race = (true, winner, false);

    /// <summary>Makes the next create of the record land and then answer <c>Exists</c>, as a retried landed attempt does.</summary>
    public void LandNextCreateAsExisting() => _race = (true, null, true);

    public async ValueTask<ObjectCreateOutcome> CreateAsync(ObjectName name, ArcBuffer content, uint? crc32c, CancellationToken cancellationToken = default)
    {
        if (name != record || !_race.Armed)
        {
            return await inner.CreateAsync(name, content, crc32c, cancellationToken);
        }

        var (_, winner, ownAttempt) = _race;
        _race = default;
        if (ownAttempt)
        {
            _ = await inner.CreateAsync(name, content, crc32c, cancellationToken);
            return ObjectCreateOutcome.Exists.Instance;
        }

        if (winner is null)
        {
            return ObjectCreateOutcome.Exists.Instance;
        }

        using (var competing = ArcBuffer.Wrap(winner.Encode()))
        {
            _ = await inner.CreateAsync(name, competing, null, cancellationToken);
        }

        return await inner.CreateAsync(name, content, crc32c, cancellationToken);
    }

    public ValueTask<ArcBuffer?> ReadAsync(ObjectName name, long offset, int length, CancellationToken cancellationToken = default) =>
        inner.ReadAsync(name, offset, length, cancellationToken);

    public ValueTask<ObjectProperties?> GetPropertiesAsync(ObjectName name, CancellationToken cancellationToken = default) =>
        inner.GetPropertiesAsync(name, cancellationToken);

    public IAsyncEnumerable<ListedObject> ListAsync(string prefix, string? startAfter = null, CancellationToken cancellationToken = default) => inner.ListAsync(prefix, startAfter, cancellationToken);

    public ValueTask DeleteAsync(ObjectName name, CancellationToken cancellationToken = default) => inner.DeleteAsync(name, cancellationToken);

    public ValueTask<VersionedObject?> ReadVersionedAsync(ObjectName name, CancellationToken cancellationToken = default) =>
        inner.ReadVersionedAsync(name, cancellationToken);

    public ValueTask<ObjectReplaceOutcome> ReplaceAsync(ObjectName name, ObjectVersion? expectedVersion, ArcBuffer content, uint? crc32c, CancellationToken cancellationToken = default) =>
        inner.ReplaceAsync(name, expectedVersion, content, crc32c, cancellationToken);
}
