using System.Globalization;
using Orleans.Durable.Storage.Engine.ObjectStore;

namespace Orleans.Durable.Storage.Engine.Ledgers.Objects;

/// <summary>
/// The options of a process's <see cref="ObjectLedgerStore"/> (object-ledgers.md §4), over an object store whose names
/// lie below the cluster's store prefix (§7).
/// </summary>
public sealed class ObjectLedgerStoreOptions
{
    /// <summary>The default memory budget of the local batch cache, 1 GiB (object-ledgers.md §4).</summary>
    public const long DefaultCacheBytes = 1L << 30;

    /// <summary>The default target size of an append-layout extent, 128 MiB (object-ledgers.md §4, §8.6).</summary>
    public const long DefaultExtentTargetBytes = 128L * 1024 * 1024;

    /// <summary>The default append limit of an append-layout extent, 40,000 appends (object-ledgers.md §4, §8.6).</summary>
    public const int DefaultExtentMaxAppends = 40_000;

    /// <summary>
    /// The greatest target byte size allowed for an extent: <see cref="int.MaxValue"/> minus the 57-byte <c>ROLL</c>
    /// append, so a rolled extent stays within <see cref="IAppendableObjectStore"/>'s <c>2³¹ − 1</c> byte limit
    /// (object-ledgers.md §3.1, §8.1).
    /// </summary>
    public const long MaxExtentTargetBytes = int.MaxValue - ObjectLedgerExtentFormat.RollAppendLength;

    /// <summary>
    /// The greatest append limit allowed for an extent, 49,999: leaves room for the final <c>ROLL</c> append within
    /// Azure Blob Storage's 50,000-block limit per append blob (object-ledgers.md §3.1, §8.1).
    /// </summary>
    public const int MaxExtentAppends = 49_999;

    /// <summary>
    /// The largest batch that fits one <c>CLOE</c> append framed with its header and trailer inside
    /// <see cref="IAppendableObjectStore.MaxAppendLength"/> (object-ledgers.md §8.1).
    /// </summary>
    public const int MaxAppendBatchLength =
        IAppendableObjectStore.MaxAppendLength - ObjectLedgerExtentFormat.FrameHeaderLength - ObjectLedgerExtentFormat.TrailerLength;

    /// <summary>Gets or sets the layout the process uses, which the store's record must name (OL-I1).</summary>
    public ObjectLedgerLayout Layout { get; set; } = ObjectLedgerLayout.PerSlot;

    /// <summary>
    /// Gets or sets the target byte size after which an append-layout extent rolls to its successor (object-ledgers.md §4, §8.1).
    /// </summary>
    public long ExtentTargetBytes { get; set; } = DefaultExtentTargetBytes;

    /// <summary>
    /// Gets or sets the append count after which an append-layout extent rolls to its successor (object-ledgers.md §4, §8.1).
    /// </summary>
    public int ExtentMaxAppends { get; set; } = DefaultExtentMaxAppends;

    /// <summary>
    /// Gets or sets the largest batch payload the process admits; in the append layout, the store checks at startup that this
    /// batch framed with its <c>DATA</c> header and <c>CLOE</c> trailer fits <see cref="IAppendableObjectStore.MaxAppendLength"/>
    /// (object-ledgers.md §4, §8.1).
    /// </summary>
    public int MaxBatchBytes { get; set; } = MaxAppendBatchLength;

    /// <summary>
    /// Gets or sets the memory budget of the local batch cache in bytes; 0 disables the cache (object-ledgers.md §4, OL-I11).
    /// </summary>
    public long CacheBytes { get; set; } = DefaultCacheBytes;

    /// <summary>
    /// Gets or sets an optional directory on volatile local disk for caching verified batches alongside memory (OL-I11).
    /// </summary>
    public string? CacheDirectory { get; set; }

    /// <summary>
    /// Gets or sets the object-ledger store's redundancy as the operator states it, recorded in each silo's startup log
    /// (orleans.md §5.14).
    /// </summary>
    public ObjectLedgerRedundancy Redundancy { get; set; } = ObjectLedgerRedundancy.SingleZone;

    /// <summary>Validates the options for the configured layout.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A setting falls outside its contract.</exception>
    /// <exception cref="ArgumentException">The cache directory is empty or whitespace.</exception>
    public void Validate()
    {
        _ = ObjectLedgerStoreRecord.LayoutName(Layout);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxBatchBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ExtentTargetBytes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(ExtentTargetBytes, MaxExtentTargetBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ExtentMaxAppends);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(ExtentMaxAppends, MaxExtentAppends);
        ArgumentOutOfRangeException.ThrowIfNegative(CacheBytes);
        if (Redundancy is not (ObjectLedgerRedundancy.SingleZone or ObjectLedgerRedundancy.ZoneRedundant))
        {
            throw new ArgumentOutOfRangeException(
                nameof(Redundancy),
                Redundancy,
                string.Create(CultureInfo.InvariantCulture, $"ObjectLedgerRedundancy {Redundancy} is not defined."));
        }

        if (CacheDirectory is not null && string.IsNullOrWhiteSpace(CacheDirectory))
        {
            throw new ArgumentException("CacheDirectory, when set, cannot be empty or whitespace.", nameof(CacheDirectory));
        }

        if (Layout != ObjectLedgerLayout.Append)
        {
            return;
        }

        var singleFramedAppend = (long)MaxBatchBytes + ObjectLedgerExtentFormat.FrameHeaderLength + ObjectLedgerExtentFormat.TrailerLength;
        if (singleFramedAppend > IAppendableObjectStore.MaxAppendLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxBatchBytes),
                MaxBatchBytes,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"A batch of {MaxBatchBytes} bytes framed with its header ({ObjectLedgerExtentFormat.FrameHeaderLength}) and trailer ({ObjectLedgerExtentFormat.TrailerLength}) takes {singleFramedAppend} bytes, past the append limit of {IAppendableObjectStore.MaxAppendLength}."));
        }
    }
}
