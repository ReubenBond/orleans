using Orleans.Durable.Storage.Engine.Archive.Lsm;
using Orleans.Durable.Storage.Engine.ObjectStore;

namespace Orleans.Durable.Storage.Engine.Archive.Logs;

/// <summary>
/// The silo's archive gate on archive logs (orleans.md §5.13, OR-I8, OR-I20): the evidence that the store is the one the
/// node registry names, kind and record ID, with the store's own store fields, its log-naming scheme among them; and that
/// the record's caller parameters are the process's. Only
/// <see cref="PassAsync"/> makes one, and <see cref="Control.ArchiveLogDrainWorker"/> and
/// <see cref="Control.ArchiveLogSweeper"/> take one, and their stores and names only from it, so no drain writes and no
/// sweep deletes before every check passed: the Azure layout writes the process's journal format into each blob, so a
/// parameter which differs stops drains and sweeps too.
/// </summary>
/// <remarks>
/// The keys' logs and durable storage's objects under <c>durable-storage/</c> share one container, so the gate binds both stores: it takes them
/// from the <see cref="LogArchiveStore"/> whose record it checks, whose construction contract puts both in one container.
/// That the container belongs to one cluster is an operator precondition, which nothing checks (archive-logs.md §9).
/// </remarks>
public sealed class ArchiveLogGate
{
    private ArchiveLogGate(LogArchiveStore store, ArchiveStoreRecord record)
    {
        Logs = store.Logs;
        Objects = store.Objects;
        Names = store.Names;
        Record = record;
    }

    /// <summary>Gets the archive-log store of the keys' logs.</summary>
    public IArchiveLogStore Logs { get; }

    /// <summary>Gets the object store of durable storage's objects under <c>durable-storage/</c>, in the same container.</summary>
    public IObjectStore Objects { get; }

    /// <summary>Gets the names of the keys' logs, whose scheme the record pins: the only names a drain writes.</summary>
    public ArchiveLogNames Names { get; }

    /// <summary>Gets the store's record, which the gate checked.</summary>
    public ArchiveStoreRecord Record { get; }

    /// <summary>
    /// Passes the gate of a silo whose node registry records <paramref name="identity"/>: checks the store's kind, then
    /// reads the store's record without creating it and checks its ID, then checks the record's store fields against the
    /// store's own, and its caller parameters against <paramref name="parameters"/>. A silo pointed at a container which
    /// holds no record, or another store's, creates nothing there. A new cluster's first silo creates or adopts the record
    /// before it registers its identity, as on the LSM (orleans.md §5.13), so its gate then finds it.
    /// </summary>
    /// <param name="store">The archive store over the cluster's container, whose two stores the gate binds.</param>
    /// <param name="identity">The archive store the node registry records for the cluster.</param>
    /// <param name="parameters">The caller parameters of the process: its journal format and body limit.</param>
    /// <param name="cancellationToken">Stops the checks.</param>
    /// <returns>The gate's evidence.</returns>
    /// <exception cref="ArchiveStoreMismatchException">
    /// The store's kind or the record's ID is not <paramref name="identity"/>'s, or the record is absent; or the record
    /// holds other store fields or other caller parameters. No retry changes it.
    /// </exception>
    /// <exception cref="ArchiveCorruptException">The record is corrupt.</exception>
    public static async ValueTask<ArchiveLogGate> PassAsync(LogArchiveStore store, ArchiveStoreIdentity identity, IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(parameters);
        // Checked before any request: the registry names another kind of store, which this process must not write.
        identity.CheckKind(store.Kind);

        // Read only: a container which holds no record, or another store's, is refused before anything is created in it.
        var stored = await store.ReadRecordAsync(cancellationToken).ConfigureAwait(false);
        identity.Check(store.Kind, stored?.Id);

        // The record exists, and is never replaced, so this reads it again and checks its store fields; it creates nothing.
        var record = await store.StoreRecordAsync(parameters, cancellationToken).ConfigureAwait(false);
        identity.Check(store.Kind, record.Id);
        record.CheckParametersMatch(parameters);
        return new ArchiveLogGate(store, record);
    }
}
