using System.Globalization;
using Orleans.Durable.Storage.Engine.Archive;
using Orleans.Durable.Storage.Engine.Archive.Logs;
using Orleans.Durable.Storage.Engine.Archive.Lsm;
using Orleans.Durable.Storage.Engine.Journals;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.Ledgers.Objects;
using Orleans.Durable.Storage.Engine.Ledgers.Store;
using Orleans.Durable.Storage.Engine.ObjectStore;
using Orleans.Journaling;
using Orleans.Runtime;
using StorePrefix = Orleans.Durable.Storage.Engine.Archive.ArchiveStorePrefix;

namespace Orleans.Durable.Storage;

/// <summary>
/// Configures a silo's durable storage node (orleans.md §6): the cluster, the node root and its LedgerStore, the node's registry
/// entry, its archive store, and the replica calls' limits. Every option a deployment sets is a settable property whose
/// type the configuration binding generator parses from text, or a section of such properties, so the options bind from
/// configuration within the trim and AOT analyzers.
/// </summary>
public sealed class DurableStorageOptions
{
    /// <summary>Gets or sets the durable storage cluster, which scopes every register and claims the node root; equal on every silo.</summary>
    public Guid Cluster { get; set; }

    /// <summary>
    /// Gets or sets the node root: a directory on the node's persistent volume, which the deployment creates, durably,
    /// before the silo starts, and without which it does not start a diskful silo. The silo creates only what lies beneath
    /// it (ledgerstore.md §3.3). Leaving <see cref="Root"/> empty configures a diskless silo which holds no LedgerStore,
    /// registers a fresh node identity on every start, and requires <see cref="ObjectLedgers"/> (orleans.md §5.14).
    /// </summary>
    public string Root { get; set; } = "";

    /// <summary>Gets a value indicating whether the silo is configured as diskless (no <see cref="Root"/>).</summary>
    internal bool IsDiskless => string.IsNullOrWhiteSpace(Root);

    /// <summary>
    /// The keyed DI service key under which a deployment may register a dedicated <see cref="IObjectStore"/> /
    /// <see cref="IAppendableObjectStore"/> for object-store ledgers apart from the LSM's unkeyed store (orleans.md §5.14).
    /// When no service is registered under this key, the silo falls back to the unkeyed store registration.
    /// </summary>
    public const string ObjectLedgersStoreServiceKey = "Orleans.Durable.Storage.ObjectLedgers";

    /// <summary>
    /// Gets or sets the object-ledger store's options, or <see langword="null"/> when the silo places no ledgers in object
    /// storage (orleans.md §5.14, §6). When set, the silo resolves one provider instance of both <see cref="IObjectStore"/>
    /// and <see cref="IAppendableObjectStore"/> (first under <see cref="ObjectLedgersStoreServiceKey"/>, otherwise unkeyed;
    /// or <see cref="IObjectStore"/> alone for per-slot), places it under the cluster's store prefix via
    /// <see cref="PrefixedAppendableObjectStore"/>, and checks its store record against the node registry.
    /// </summary>
    public ObjectLedgerStoreOptions? ObjectLedgers { get; set; }

    /// <summary>
    /// Gets or sets the silo's StatefulSet ordinal, or <see langword="null"/> when it has none. At most one running silo
    /// holds each ordinal, as a StatefulSet guarantees (orleans.md §5.2).
    /// </summary>
    public int? Ordinal { get; set; }

    /// <summary>
    /// Gets or sets the node the operator names for replacement by this silo's node, or <see langword="null"/>: the node
    /// whose active entry holds this silo's ordinal, which the registration retires by ordinal succession (orleans.md
    /// §5.3). A registration at an ordinal another node's active entry holds applies only when that node is named here, or
    /// once it is retired. The setting matters only while the named node is active at the ordinal, so it acts once: after
    /// the replacement the named node is retired, and a later start finds the ordinal held by this node. It requires
    /// <see cref="Ordinal"/>. The node is named by its identity's UUID, whose text form, <c>{node:N}</c>, a log shows; a
    /// configuration binder reads that form.
    /// </summary>
    public Guid? NodeToReplace { get; set; }

    /// <summary>Gets or sets the node's failure domain, for example its zone, or <see langword="null"/> when none is configured.</summary>
    public string? FailureDomain { get; set; }

    /// <summary>Gets or sets the size of each of the LedgerStore's WAL files.</summary>
    public long WalFileSize { get; set; } = 1L << 30;

    /// <summary>
    /// Gets or sets the node's disk budget: the bytes of the node's LedgerStore files beyond which the node is under disk
    /// pressure, or <see langword="null"/> for 80% of the volume holding the WAL (orleans.md §5.9).
    /// </summary>
    public long? DiskBudgetBytes { get; set; }

    /// <summary>
    /// Gets or sets the free space below which a volume holding the node's LedgerStore puts the node under disk pressure
    /// (orleans.md §5.9).
    /// </summary>
    public long MinimumFreeDiskBytes { get; set; } = 1L << 30;

    /// <summary>
    /// Gets or sets <c>T</c>, the WAL retention target near which the node keeps its WAL, or <see langword="null"/> for the
    /// smaller of 50 GiB and half the node's disk budget (orleans.md §5.9).
    /// </summary>
    public long? WalRetentionBytes { get; set; }

    /// <summary>Gets or sets a value indicating whether the LedgerStore zero-fills each new WAL file at its full size.</summary>
    public bool PreallocateWalFiles { get; set; } = true;

    /// <summary>Gets or sets when the LedgerStore checkpoints its catalog and each replica its slot table.</summary>
    public LedgerStoreCheckpointOptions Checkpoints { get; set; } = new();

    /// <summary>
    /// Gets or sets the L2 ledger-replica quorum read-lease policy (ledger-protocol.md §4.1, system-model.md §2.1):
    /// <c>LeaseDuration</c> (0 disables leases), <c>ClockDriftFactor</c>, and <c>LeaseGuardBand</c>.
    /// </summary>
    public LedgerLeaseOptions Lease { get; set; } = new();

    /// <summary>
    /// Gets or sets the Journals' policy (journals.md §8.8): <c>MaxBodyBytes</c>, fixed for the cluster's life and
    /// recorded with its archive store, batching, the bytes each ledger's admission queues, warm state, and tombstone
    /// retention. Constructing the node's Journals checks it against the replica payload limit, the archive store, and
    /// <see cref="MaxProposalBytes"/>, and a silo which fails a check does not start (journals.md §8.4, OR-I17).
    /// </summary>
    public JournalStoreOptions Journals { get; set; } = new();

    /// <summary>
    /// Gets or sets the bytes of batches past which the segments of the ledgers the node is primary of end (ledgers.md §4,
    /// §8.2); per silo.
    /// </summary>
    public long MaxSegmentBytes { get; set; } = LedgerWriterOptions.DefaultMaxSegmentBytes;

    /// <summary>
    /// Gets or sets the keys a segment of a ledger the node is primary of defines before it ends (ledgers.md §8.2); per
    /// silo.
    /// </summary>
    public int MaxSegmentKeys { get; set; } = LedgerWriterOptions.DefaultMaxSegmentKeys;

    /// <summary>
    /// Gets or sets the longest a segment of a ledger the node is primary of stays open, from its first batch with rows, less
    /// up to a tenth drawn per ledger; at most <see cref="LedgerWriterOptions.LongestSegmentAge"/> (ledgers.md §8.2); per silo.
    /// </summary>
    public TimeSpan MaxSegmentAge { get; set; } = LedgerWriterOptions.Default.MaxSegmentAge;

    /// <summary>
    /// Gets or sets the bytes of the batches each writer of a ledger the node is primary of holds created and undecided,
    /// at least the largest batch the Journals build (ledgers.md §2.4, §8.7); per silo.
    /// </summary>
    public long MaxProposalBytes { get; set; } = LedgerWriterOptions.DefaultMaxProposalBytes;

    /// <summary>
    /// Returns the writers' options, which <see cref="MaxSegmentBytes"/> and its siblings make. A method, not a property,
    /// so a configuration binder sees no key for it.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">A writer option is out of range.</exception>
    internal LedgerWriterOptions CreateWriterOptions() => new(MaxSegmentBytes, MaxSegmentKeys, MaxSegmentAge, MaxProposalBytes);

    /// <summary>
    /// Gets or sets the kind of the cluster's archive store, fixed for its life and required (orleans.md §6, OR-I20): the
    /// LSM, over the <c>IObjectStore</c> the silo's services supply and with <see cref="Lsm"/>'s options, or archive logs,
    /// over the <c>IArchiveLogContainer</c> the silo's services supply, as <c>Orleans.Durable.Storage.AzureStorage</c>'s
    /// <c>AddAzureArchiveLogs</c> registers it, and with <see cref="ArchiveLogs"/>' options. The memory store, which
    /// only tests run, is no silo's. The store's ID is not an option: a silo takes it from the node registry, or, at a new
    /// cluster's first registration, from the store's record.
    /// </summary>
    public ArchiveStoreKind ArchiveStore { get; set; }

    /// <summary>
    /// Gets or sets the cluster's store prefix, under which its archive store lies in the bucket or container its services
    /// supply (orleans.md §5.13, §6): 1 to 16 characters of <c>a</c>–<c>z</c>, <c>0</c>–<c>9</c>, <c>_</c>, and <c>-</c>,
    /// or <see langword="null"/> for the default, 10 characters derived from Orleans' <c>ClusterOptions.ServiceId</c>
    /// (<see cref="StorePrefix.Of"/>). Each cluster's prefix is unique within its bucket or container, and equal on
    /// every silo of the cluster, whose node registry records it. A silo which leaves it unset while its service ID is one
    /// Orleans gives by default, <c>default</c> or localhost clustering's <c>dev</c>, does not start, since unrelated
    /// deployments share those.
    /// </summary>
    public string? ArchiveStorePrefix { get; set; }

    /// <summary>Gets or sets the archive logs' options, on a cluster whose archive store is archive logs (orleans.md §5.13, §6).</summary>
    public ArchiveLogStoreOptions ArchiveLogs { get; set; } = new();

    /// <summary>
    /// Gets or sets the most replica calls the silo's replica host admits at once through <c>ILedgerStoreGrain</c>, beyond
    /// which it answers <c>Backpressure</c> before the store (orleans.md §5.12, OR-I17).
    /// </summary>
    public int MaxReplicaCalls { get; set; } = 128;

    /// <summary>
    /// Gets or sets the request payload and DATA reply bytes the silo's replica host holds, each admitted read's largest
    /// reply reserved at admission, below which it admits another call (orleans.md §5.12, OR-I17).
    /// </summary>
    public long MaxReplicaCallBytes { get; set; } = 128L << 20;

    /// <summary>
    /// Gets or sets the most replica calls the silo keeps outstanding to each node, its own included, beyond which a call
    /// waits in the node's replica FIFO queue (orleans.md §5.12, OR-I17).
    /// </summary>
    public int MaxNodeCalls { get; set; } = 32;

    /// <summary>
    /// Gets or sets the request payload and reply bytes the silo keeps outstanding to each node, its own included, each
    /// read reserving its largest reply, beyond which a call waits in the node's replica FIFO queue (orleans.md §5.12,
    /// OR-I17).
    /// </summary>
    public long MaxNodeCallBytes { get; set; } = 64L << 20;

    /// <summary>
    /// Gets or sets the most contiguous-tail reads and seals the silo keeps outstanding to each node, in a lane of their
    /// own beside the byte FIFO queue, so a seal on the failover path never waits behind large writes (orleans.md §5.12).
    /// </summary>
    public int MaxNodeControlCalls { get; set; } = 8;

    /// <summary>
    /// Gets or sets the reply a remote whole read reserves, at the caller and at the host: about the typical batch. A read
    /// of a larger batch is answered <c>TooLarge</c> with its length, and repeated reserving exactly that (orleans.md
    /// §5.12, OR-I17). At most the payload limit.
    /// </summary>
    public int MaxReadReplyBytes { get; set; } = 1 << 20;

    /// <summary>
    /// Gets or sets the bytes of forwarded writes the silo keeps outstanding to each node, in a FIFO forwarding lane of the
    /// node's own, apart from its replica calls and its first read pages (orleans.md §5.5, §5.12, OR-I17): a forwarded write
    /// beyond it waits there, and one whose deadline passes there is never sent and is answered <c>ForwardQueueTimeout</c>.
    /// </summary>
    public long MaxForwardCallBytes { get; set; } = 64L << 20;

    /// <summary>
    /// Gets or sets the first pages of forwarded reads the silo keeps outstanding to each node, at least 1 and 16 by default,
    /// in a FIFO lane of the node's own, apart from its forwarded writes (orleans.md §5.5, §5.12, OR-I17). A read's request
    /// carries no payload and its reply is at most one 1 MiB page, so the lane bounds calls; a first page beyond it waits
    /// there, and one whose deadline passes there is never sent and is answered <c>ForwardQueueTimeout</c>.
    /// </summary>
    public int MaxForwardReadCalls { get; set; } = 16;

    /// <summary>
    /// Gets or sets the bytes of the read snapshots the node pins for forwarded reads (orleans.md §5.5, §5.12, OR-I17): the
    /// owner of a forwarded read whose content passes one page reserves its content's length, or the whole budget for a
    /// larger one, before it answers the first page, waiting in order while earlier snapshots hold the budget, and releases
    /// it at the last page or the snapshot's deadline.
    /// </summary>
    public long MaxForwardedReadBytes { get; set; } = 256L << 20;

    /// <summary>
    /// Gets or sets the largest value the MetaStore provider stores in <c>ledger-map</c>, which bounds the map: a change
    /// which would pass it is <c>Refused</c> (orleans.md §5.6, §6).
    /// </summary>
    public int LedgerMapMaxBytes { get; set; } = 64 << 10;

    /// <summary>
    /// Gets or sets the time a blocked archive-log drain waits between its attempts (orleans.md §5.9): about a minute, the
    /// drains' <c>BlockedRetryInterval</c>, and shorter only in tests.
    /// </summary>
    internal TimeSpan BlockedDrainRetryInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Gets or sets the LSM's options, on a cluster whose archive store is the LSM (archive-lsm.md §8.7, §8.8).</summary>
    public ArchiveOptions Lsm { get; set; } = new();

    /// <summary>
    /// Returns the archive-log drains' options, on a cluster whose archive store is archive logs (archive-logs.md §8.6). A
    /// method, not a property, so a configuration binder sees no key for it.
    /// </summary>
    internal ArchiveLogOptions CreateArchiveLogOptions() => ArchiveLogs.CreateDrainOptions();

    /// <summary>Throws unless the options can start a node.</summary>
    /// <exception cref="ArgumentException">An option is missing or out of range.</exception>
    internal void Validate()
    {
        if (Cluster == Guid.Empty)
        {
            throw new ArgumentException("A durable storage node names its cluster.", nameof(Cluster));
        }

        if (IsDiskless)
        {
            if (ObjectLedgers is null)
            {
                throw new ArgumentException("A diskless silo (Root empty) requires ObjectLedgers to be configured (orleans.md §5.14).", nameof(Root));
            }

            if (Ordinal is not null)
            {
                throw new ArgumentException("A diskless silo holds no persistent volume and cannot carry a StatefulSet ordinal (orleans.md §5.14).", nameof(Ordinal));
            }

            if (NodeToReplace is not null)
            {
                throw new ArgumentException("A diskless silo holds no persistent volume and cannot replace another node (orleans.md §5.14).", nameof(NodeToReplace));
            }
        }
        else if (!Path.IsPathFullyQualified(Root))
        {
            throw new ArgumentException("The node root is a fully qualified path, whose volumes the node measures.", nameof(Root));
        }

        if (ObjectLedgers is not null)
        {
            Section(nameof(ObjectLedgers), ObjectLedgers.Validate);
        }

        if (Ordinal is { } ordinal)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(ordinal, nameof(Ordinal));
        }

        if (NodeToReplace is { } nodeToReplace)
        {
            if (nodeToReplace == default)
            {
                throw new ArgumentException("A node named for replacement is a node identity.", nameof(NodeToReplace));
            }

            if (Ordinal is null)
            {
                throw new ArgumentException("A node is replaced only at an ordinal: naming one requires the silo's ordinal.", nameof(NodeToReplace));
            }
        }

        if (FailureDomain is { Length: 0 } || FailureDomain is { } domain && System.Text.Encoding.UTF8.GetByteCount(domain) > NodeRegistryEntry.MaxFailureDomainBytes)
        {
            throw new ArgumentException(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"A configured failure domain is non-empty and at most {NodeRegistryEntry.MaxFailureDomainBytes} bytes in UTF-8."), nameof(FailureDomain));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(WalFileSize, nameof(WalFileSize));
        if (DiskBudgetBytes is { } budget)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(budget, nameof(DiskBudgetBytes));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(MinimumFreeDiskBytes, nameof(MinimumFreeDiskBytes));
        if (WalRetentionBytes is { } retention)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(retention, nameof(WalRetentionBytes));
        }

        ArgumentNullException.ThrowIfNull(Checkpoints, nameof(Checkpoints));
        Section(nameof(Checkpoints), Checkpoints.Validate);
        ArgumentNullException.ThrowIfNull(Lease, nameof(Lease));
        Section(nameof(Lease), Lease.Validate);
        ArgumentNullException.ThrowIfNull(Journals, nameof(Journals));
        Section(nameof(Journals), Journals.Validate);
        CheckWriters();
        var largest = Journals.LargestBatch();
        if (Journals.MaxQueuedBytes < largest)
        {
            throw new ArgumentOutOfRangeException(
                $"{nameof(Journals)}:{nameof(JournalStoreOptions.MaxQueuedBytes)}",
                Journals.MaxQueuedBytes,
                string.Create(CultureInfo.InvariantCulture, $"A ledger's admission queue holds at least the largest batch the Journals build, {largest} bytes (journals.md §8.4)."));
        }

        if (MaxProposalBytes < largest)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxProposalBytes),
                MaxProposalBytes,
                string.Create(CultureInfo.InvariantCulture, $"A writer's proposals hold at least the largest batch the Journals build, {largest} bytes (ledgers.md §8.7)."));
        }

        if (ObjectLedgers is not null && largest > ObjectLedgers.MaxBatchBytes)
        {
            throw new ArgumentOutOfRangeException(
                $"{nameof(ObjectLedgers)}:{nameof(ObjectLedgerStoreOptions.MaxBatchBytes)}",
                ObjectLedgers.MaxBatchBytes,
                string.Create(CultureInfo.InvariantCulture, $"An object-ledger store's MaxBatchBytes holds at least the largest batch the Journals build, {largest} bytes (object-ledgers.md §8.2)."));
        }

        if (ArchiveStore is not (ArchiveStoreKind.Lsm or ArchiveStoreKind.Logs))
        {
            throw new ArgumentException(
                ArchiveStore == default
                    ? "A durable storage node names its cluster's archive store: ArchiveStore is required, Lsm or Logs."
                    : "A durable storage node's archive store is the LSM or archive logs; the memory store runs only in tests.",
                nameof(ArchiveStore));
        }

        ArgumentNullException.ThrowIfNull(ArchiveLogs, nameof(ArchiveLogs));
        Section(nameof(ArchiveLogs), ArchiveLogs.Validate);

        if (ArchiveStorePrefix is not null && !StorePrefix.IsValid(ArchiveStorePrefix))
        {
            throw new ArgumentException(
                $"A durable storage cluster's store prefix is 1 to {StorePrefix.MaxLength} characters of a-z, 0-9, '_', and '-'.", nameof(ArchiveStorePrefix));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxReplicaCalls, nameof(MaxReplicaCalls));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxReplicaCallBytes, nameof(MaxReplicaCallBytes));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxNodeCalls, nameof(MaxNodeCalls));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxNodeCallBytes, nameof(MaxNodeCallBytes));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxNodeControlCalls, nameof(MaxNodeControlCalls));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxReadReplyBytes, nameof(MaxReadReplyBytes));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxForwardCallBytes, nameof(MaxForwardCallBytes));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxForwardReadCalls, nameof(MaxForwardReadCalls));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxForwardedReadBytes, nameof(MaxForwardedReadBytes));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(LedgerMapMaxBytes, nameof(LedgerMapMaxBytes));
        ArgumentNullException.ThrowIfNull(Lsm, nameof(Lsm));
        Section(nameof(Lsm), Lsm.Validate);
    }

    /// <summary>
    /// Checks the writers' options through the Ledgers' own constructor, so a constraint it adds holds at start, naming the
    /// node's option.
    /// </summary>
    private void CheckWriters()
    {
        try
        {
            _ = CreateWriterOptions();
        }
        catch (ArgumentOutOfRangeException invalid)
        {
            var option = invalid.ParamName is { Length: > 0 } parameter ? char.ToUpperInvariant(parameter[0]) + parameter[1..] : null;
            throw new ArgumentOutOfRangeException(option, invalid.ActualValue, Unqualified(invalid));
        }
    }

    /// <summary>Validates a section, naming its failing option by its key below this section, as <c>Section:Option</c>.</summary>
    private static void Section(string section, Action validate)
    {
        try
        {
            validate();
        }
        catch (ArgumentOutOfRangeException invalid)
        {
            throw new ArgumentOutOfRangeException($"{section}:{invalid.ParamName}", invalid.ActualValue, Unqualified(invalid));
        }
        catch (ArgumentException invalid) when (invalid.GetType() == typeof(ArgumentException))
        {
            throw new ArgumentException(Unqualified(invalid), $"{section}:{invalid.ParamName}", invalid);
        }
    }

    /// <summary>
    /// Returns an argument exception's message without the parameter and value the runtime appends, which the exception
    /// that names the option appends again.
    /// </summary>
    private static string Unqualified(ArgumentException invalid)
    {
        var message = invalid.Message;
        var actual = invalid is ArgumentOutOfRangeException { ActualValue: { } value } ? Environment.NewLine + $"Actual value was {value}." : null;
        if (actual is not null && message.EndsWith(actual, StringComparison.Ordinal))
        {
            message = message[..^actual.Length];
        }

        var parameter = invalid.ParamName is { } name ? $" (Parameter '{name}')" : null;
        return parameter is not null && message.EndsWith(parameter, StringComparison.Ordinal) ? message[..^parameter.Length] : message;
    }
}
