using System.Diagnostics;
using System.Globalization;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.ObjectStore;
using Polly;

namespace Orleans.Durable.Storage.Engine.Ledgers.Objects;

/// <summary>
/// A process's object-ledger store (object-ledgers.md §2.2): the object store, under the cluster's store prefix, and its
/// layout. It opens each object-store ledger's storage without I/O, and answers the store's record (OL-I1).
/// </summary>
public sealed class ObjectLedgerStore
{
    /// <summary>The longest record a reader accepts, far above the record's 66 bytes.</summary>
    private const int MaxRecordLength = 4096;

    /// <summary>The name of the object-ledger store's record under the cluster's store prefix (object-ledgers.md §7).</summary>
    public static ObjectName StoreRecordName => ObjectLedgerNames.Store;

    private readonly ObjectLedgerStoreOptions _options;

    private ObjectLedgerStore(IObjectStore objects, IAppendableObjectStore? appendable, ObjectLedgerStoreOptions options, TimeProvider timeProvider)
    {
        Objects = objects;
        Appendable = appendable;
        _options = options;
        Cache = new ObjectLedgerCache(options.CacheBytes, options.CacheDirectory);

        // A recovery's every step is idempotent, so a transient failure repeats it from its start (object-ledgers.md §6).
        RecoveryRetry = ReplicaRetry.Create<LedgerRecoveryOutcome>(timeProvider, static _ => false);
    }

    /// <summary>Gets the store's layout.</summary>
    public ObjectLedgerLayout Layout => _options.Layout;

    /// <summary>Gets the operator-stated redundancy of the store, recorded in the startup log (orleans.md §5.14).</summary>
    public ObjectLedgerRedundancy Redundancy => _options.Redundancy;

    /// <summary>
    /// Gets the object store, which the ledgers' storage reads and writes and the sweeper reclaims (object-ledgers.md §2.3).
    /// </summary>
    internal IObjectStore Objects { get; }

    /// <summary>
    /// Gets the same object store's appendable interface, which the append layout writes (row 7.4), or
    /// <see langword="null"/> for a store of the per-slot layout over a provider without appendable objects.
    /// </summary>
    internal IAppendableObjectStore? Appendable { get; }

    /// <summary>Gets the process's derived local cache of verified batches (OL-I11).</summary>
    internal ObjectLedgerCache Cache { get; }

    /// <summary>Gets the retry pipeline every recovery of the store's ledgers shares.</summary>
    internal ResiliencePipeline<LedgerRecoveryOutcome> RecoveryRetry { get; }

    /// <summary>Evicts every cached batch belonging to <paramref name="id"/> from the local cache.</summary>
    internal void EvictCachedLedger(LedgerId id) => Cache.EvictLedger(id);

    /// <summary>
    /// Returns a store of the per-slot layout over any object store (object-ledgers.md §3.1), whose names lie below the
    /// cluster's store prefix, as <see cref="PrefixedObjectStore"/> places them.
    /// </summary>
    /// <param name="store">The object store.</param>
    /// <param name="options">The store's options, whose layout is per-slot.</param>
    /// <param name="timeProvider">The clock of the recoveries' backoff.</param>
    /// <exception cref="ArgumentException">The options' layout is not per-slot.</exception>
    public static ObjectLedgerStore Over(IObjectStore store, ObjectLedgerStoreOptions options, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        options.Validate();
        if (options.Layout != ObjectLedgerLayout.PerSlot)
        {
            throw new ArgumentException("A store without appendable objects holds the per-slot layout only.", nameof(options));
        }

        return new ObjectLedgerStore(store, appendable: null, options, timeProvider);
    }

    /// <summary>
    /// Returns a store of either layout over one object store of both interfaces (object-ledgers.md §3.1), whose names lie
    /// below the cluster's store prefix, as <see cref="PrefixedAppendableObjectStore"/> places them. For the
    /// append layout, validates at startup that the configured payload limit framed with its header and <c>CLOE</c> trailer
    /// fits <see cref="IAppendableObjectStore.MaxAppendLength"/> (object-ledgers.md §8.1).
    /// </summary>
    /// <typeparam name="TStore">The object store's type.</typeparam>
    /// <param name="store">The object store.</param>
    /// <param name="options">The store's options.</param>
    /// <param name="timeProvider">The clock of the recoveries' backoff.</param>
    public static ObjectLedgerStore OverAppendable<TStore>(TStore store, ObjectLedgerStoreOptions options, TimeProvider timeProvider)
        where TStore : IObjectStore, IAppendableObjectStore
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        options.Validate();
        return new ObjectLedgerStore(store, store, options, timeProvider);
    }

    /// <summary>
    /// Answers the store's record (OL-I1): the call which finds none creates it with a new random ID and the configured
    /// layout, and a call which finds one, or whose create meets one, answers the record the store holds.
    /// </summary>
    /// <param name="cancellationToken">Stops the caller's wait; a create already sent may still land.</param>
    /// <returns>The store's record.</returns>
    /// <exception cref="ObjectLedgerStoreMismatchException">The record names another layout.</exception>
    /// <exception cref="InvalidDataException">
    /// The record breaks a rule of its format (object-ledgers.md §7), fails its CRC-32C, or vanished after it existed.
    /// </exception>
    public async ValueTask<ObjectLedgerStoreRecord> StoreRecordAsync(CancellationToken cancellationToken = default)
    {
        if (await ReadRecordAsync(cancellationToken).ConfigureAwait(false) is not { } stored)
        {
            var created = ObjectLedgerStoreRecord.NewRandom(Layout);
            using var content = ArcBuffer.Wrap(created.Encode());
            switch (await CreateRecordAsync(content, cancellationToken).ConfigureAwait(false))
            {
                case ObjectCreateOutcome.Created:
                    return created;
                case ObjectCreateOutcome.Exists:
                    // Another process created it first, or this call's own earlier attempt did: read the one the store holds.
                    stored = await ReadRecordAsync(cancellationToken).ConfigureAwait(false)
                        ?? throw new InvalidDataException($"{ObjectLedgerNames.Store} exists but reads as absent; no process deletes it (OL-I1).");
                    break;
                case null:
                default:
                    throw new UnreachableException();
            }
        }

        return CheckLayout(stored);
    }

    /// <summary>
    /// Reads the store's record without creating it (OL-I1): the check of a process which must find the record its
    /// cluster's node registry records (row 7.5).
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The store's record, or <see langword="null"/> when the store holds none.</returns>
    /// <exception cref="ObjectLedgerStoreMismatchException">The record names another layout.</exception>
    /// <exception cref="InvalidDataException">The record breaks a rule of its format, or fails its CRC-32C.</exception>
    public async ValueTask<ObjectLedgerStoreRecord?> ReadStoreRecordAsync(CancellationToken cancellationToken = default) =>
        await ReadRecordAsync(cancellationToken).ConfigureAwait(false) is { } stored ? CheckLayout(stored) : null;

    /// <summary>
    /// Opens the storage of an object-store ledger, without I/O (object-ledgers.md §2.1).
    /// </summary>
    /// <param name="id">The ledger.</param>
    /// <param name="windowSize">The ledger's window <c>K</c>, from its map entry.</param>
    internal ILedgerStorage Open(LedgerId id, int windowSize)
    {
        LedgerReplicaOperations.ValidateWindowSize(windowSize);
        return Layout switch
        {
            ObjectLedgerLayout.PerSlot => new ObjectLedgerStorage(Objects, id, windowSize, _options.MaxBatchBytes, RecoveryRetry, Cache),
            ObjectLedgerLayout.Append => new AppendObjectLedgerStorage(Objects, Appendable!, id, windowSize, _options, RecoveryRetry, Cache),
            _ => throw new UnreachableException(),
        };
    }

    /// <summary>
    /// Creates the store's record with its CRC-32C, reporting content which changed after the checksum was computed as the
    /// defect it is (conventions.md §14, outcomes.md §8).
    /// </summary>
    private async ValueTask<ObjectCreateOutcome> CreateRecordAsync(ArcBuffer content, CancellationToken cancellationToken)
    {
        try
        {
            return await Objects.CreateAsync(ObjectLedgerNames.Store, content, ObjectStoreChecksums.Crc32C(content), cancellationToken).ConfigureAwait(false);
        }
        catch (ObjectContentChangedException exception)
        {
            ObjectContentChangedDefect.Stop(exception);
            throw;
        }
    }

    private ObjectLedgerStoreRecord CheckLayout(ObjectLedgerStoreRecord stored) =>
        stored.Layout == Layout
            ? stored
            : throw new ObjectLedgerStoreMismatchException(string.Create(
                CultureInfo.InvariantCulture,
                $"The object-ledger store's record names the {ObjectLedgerStoreRecord.LayoutName(stored.Layout)} layout, not the process's {ObjectLedgerStoreRecord.LayoutName(Layout)}."));

    private async ValueTask<ObjectLedgerStoreRecord?> ReadRecordAsync(CancellationToken cancellationToken)
    {
        if (await Objects.GetPropertiesAsync(ObjectLedgerNames.Store, cancellationToken).ConfigureAwait(false) is not { } properties)
        {
            return null;
        }

        if (properties.Length is 0 or > MaxRecordLength)
        {
            throw new InvalidDataException(string.Create(CultureInfo.InvariantCulture, $"The object-ledger store's record is {properties.Length} bytes, not a record."));
        }

        ArcBuffer? read;
        try
        {
            read = await Objects.ReadAsync(ObjectLedgerNames.Store, 0, (int)properties.Length, cancellationToken).ConfigureAwait(false);
        }
        catch (EndOfStreamException exception)
        {
            throw new InvalidDataException("The object-ledger store's record is shorter than its properties' length; no process replaces it (OL-I1).", exception);
        }

        using var bytes = read ?? throw new InvalidDataException("The object-ledger store's record was deleted, which no process does (OL-I1).");
        var sequence = bytes.AsReadOnlySequence();
        if (properties.Crc32c is { } crc32c && ObjectStoreChecksums.Crc32C(sequence) != crc32c)
        {
            throw new InvalidDataException("The object-ledger store's record fails the CRC-32C its create recorded.");
        }

        var decoding = sequence.IsSingleSegment ? ObjectLedgerStoreRecord.Decode(sequence.FirstSpan) : ObjectLedgerStoreRecord.Decode(bytes.ToArray());
        return decoding switch
        {
            ObjectLedgerStoreRecord record => record,
            InvalidDataException rejection => throw rejection,
            null => throw new UnreachableException(),
        };
    }
}
