using Orleans.Durable.Storage.Engine.Archive;
using Orleans.Durable.Storage.Engine.Ledgers.Control;
using Orleans.Durable.Storage.Engine.Ledgers.Objects;
using Orleans.Durable.Storage.Engine.ObjectStore;
using Polly;

namespace Orleans.Durable.Storage.Control;

/// <summary>
/// The silo's object-ledger store as its configuration selects it (orleans.md §5.14, §6, OL-I1): either an unconfigured
/// instance (<see cref="CreateUnconfigured"/>) when <see cref="DurableStorageOptions.ObjectLedgers"/> is unset, or a
/// configured store over one provider instance under the cluster's store prefix, with a startup gate (<see cref="Gate"/>)
/// that passes only once the store's record matches the ID the node registry records (and the configured layout).
/// </summary>
internal sealed class SiloObjectLedgers
{
    private int _controllerRefusalReported;

    private SiloObjectLedgers()
    {
    }

    /// <summary>Creates a fresh unconfigured instance for a silo that configures no object-ledger store.</summary>
    public static SiloObjectLedgers CreateUnconfigured() => new();

    /// <summary>Creates a configured object-ledger store holder under <paramref name="prefix"/>.</summary>
    /// <param name="store">The silo's object-ledger store.</param>
    /// <param name="prefix">The cluster's store prefix under which the store lies.</param>
    /// <param name="pacer">The per-node request pacer decorating the store's object store, if any.</param>
    public SiloObjectLedgers(ObjectLedgerStore store, ArchiveStorePrefix prefix, ObjectStoreRequestPacer? pacer = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (prefix.Value is null)
        {
            throw new ArgumentException("The default ArchiveStorePrefix is no store prefix.", nameof(prefix));
        }

        Store = store;
        Prefix = prefix;
        Pacer = pacer;
    }

    /// <summary>Gets the object-ledger store, or <see langword="null"/> when the silo configures none.</summary>
    public ObjectLedgerStore? Store { get; }

    /// <summary>Gets the per-node request pacer decorating the object-ledger store, or <see langword="null"/> when none is configured.</summary>
    public ObjectStoreRequestPacer? Pacer { get; }

    /// <summary>Gets the cluster's store prefix, or <see langword="null"/> when the silo configures no object-ledger store.</summary>
    public ArchiveStorePrefix? Prefix { get; }

    /// <summary>Gets the operator-stated redundancy of the object-ledger store.</summary>
    public ObjectLedgerRedundancy Redundancy => Store?.Redundancy ?? default;

    /// <summary>Gets a value indicating whether the silo configures an object-ledger store.</summary>
    public bool IsConfigured => Store is not null;

    /// <summary>
    /// Gets the objects a first registration's record call creates or adopts in the store, which stay there if the
    /// registration then loses to another store's (orleans.md §5.14).
    /// </summary>
    public string FirstRecordObjects => $"the object-ledger store's record '{Prefix}/{ObjectLedgerStore.StoreRecordName}'";

    /// <summary>Gets the silo's object-ledger gate, passed once the store's record matches the node registry's ID.</summary>
    public ObjectLedgerGate Gate { get; } = new();

    /// <summary>
    /// Returns whether this call is the silo's first report that a controller grain activation found the object-ledger gate
    /// refused.
    /// </summary>
    public bool ReportsControllerRefusal() => Interlocked.Exchange(ref _controllerRefusalReported, 1) == 0;

    /// <summary>
    /// Answers the ID of the object-ledger store's record at a cluster's first registration that introduces object-store
    /// ledgers, creating or adopting the record (orleans.md §5.14, OL-I1).
    /// </summary>
    public async ValueTask<ObjectLedgerStoreId> FirstRecordAsync(CancellationToken cancellationToken)
    {
        var store = Store ?? throw new InvalidOperationException("The silo configures no object-ledger store.");
        var record = await store.StoreRecordAsync(cancellationToken).ConfigureAwait(false);
        return record.Id;
    }

    /// <summary>
    /// Checks the object-ledger store's record against <paramref name="expected"/>, the ID the node registry records, and
    /// passes <see cref="Gate"/> once the store's record exists, matches <paramref name="expected"/>, and matches the
    /// configured layout (orleans.md §5.14, OL-I1).
    /// </summary>
    /// <exception cref="ObjectLedgerStoreMismatchException">The store holds no record, a different ID, or a different layout.</exception>
    /// <exception cref="InvalidDataException">The store record is corrupt.</exception>
    public async Task CheckAsync(ObjectLedgerStoreId expected, ResiliencePipeline retry, CancellationToken cancellationToken)
    {
        var store = Store ?? throw new InvalidOperationException("The silo configures no object-ledger store.");
        var record = await retry.ExecuteAsync(
            static async (state, token) => await state.ReadStoreRecordAsync(token).ConfigureAwait(false),
            store,
            cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            throw new ObjectLedgerStoreMismatchException(
                $"The object-ledger store holds no record '{Prefix}/{ObjectLedgerStore.StoreRecordName}', where the node registry records {expected} (OL-I1).");
        }

        if (record.Id != expected)
        {
            throw new ObjectLedgerStoreMismatchException(
                $"The object-ledger store's record '{Prefix}/{ObjectLedgerStore.StoreRecordName}' holds ID {record.Id}, where the node registry records {expected} (OL-I1).");
        }

        Gate.Pass(store);
    }
}

/// <summary>
/// The silo's object-ledger gate (orleans.md §5.14, OL-I1): every service that adopts object-store ledgers or runs
/// <see cref="ObjectLedgerSweeper"/> waits for <see cref="Passed"/>. It transitions once: passed with the verified
/// <see cref="ObjectLedgerStore"/>, refused with the refusal exception, or cancelled when the silo stops.
/// </summary>
internal sealed class ObjectLedgerGate
{
    private readonly TaskCompletionSource<ObjectLedgerStore> _passed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Gets the gate's task: completes with the verified <see cref="ObjectLedgerStore"/> once the store's record matches the
    /// node registry, faults only with its refusal, and is cancelled when the silo stops.
    /// </summary>
    public Task<ObjectLedgerStore> Passed => _passed.Task;

    /// <summary>Gets a value indicating whether the gate has passed.</summary>
    public bool IsPassed => _passed.Task.IsCompletedSuccessfully;

    /// <summary>
    /// Returns the verified <see cref="ObjectLedgerStore"/> once the gate has passed; otherwise throws, failing closed
    /// while the gate is pending, refused, or cancelled (OL-I1).
    /// </summary>
    public ObjectLedgerStore RequirePassed()
    {
        var task = _passed.Task;
        if (task.IsCompletedSuccessfully)
        {
            return task.Result;
        }

        if (task.IsFaulted)
        {
            throw new InvalidOperationException("The silo's object-ledger gate was refused (OL-I1).", task.Exception.InnerException);
        }

        throw new InvalidOperationException(task.IsCanceled
            ? "The silo's object-ledger gate was cancelled because the silo is stopping (OL-I1)."
            : "The silo's object-ledger gate has not passed yet (OL-I1).");
    }

    /// <summary>Passes the gate with <paramref name="store"/>.</summary>
    public void Pass(ObjectLedgerStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _ = _passed.TrySetResult(store);
    }

    /// <summary>Refuses the gate with <paramref name="refusal"/>.</summary>
    public void Refuse(Exception refusal)
    {
        ArgumentNullException.ThrowIfNull(refusal);
        _ = _passed.TrySetException(refusal);
        _ = _passed.Task.Exception;
    }

    /// <summary>Cancels the gate as the silo stops.</summary>
    public void Cancel() => _ = _passed.TrySetCanceled(CancellationToken.None);
}
