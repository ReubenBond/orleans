using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Durable.Storage.Control;
using Orleans.Durable.Storage.Engine;
using Orleans.Durable.Storage.Engine.Archive;
using Orleans.Durable.Storage.Engine.Archive.Logs;
using Orleans.Durable.Storage.Engine.Archive.Lsm;
using Orleans.Durable.Storage.Engine.Cluster.Control;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.Ledgers.Objects;
using Orleans.Durable.Storage.Engine.Ledgers.Store;
using Orleans.Durable.Storage.Engine.MetaStore;
using Orleans.Durable.Storage.Engine.ObjectStore;
using Orleans.Durable.Storage.Engine.Wal;
using Orleans.Journaling;
using Orleans.Runtime;
using Polly;
using Polly.Retry;

namespace Orleans.Durable.Storage.Hosting;

/// <summary>
/// The silo lifecycle participant which runs the silo's one Clog node (orleans.md §5.2, OR-I9) and drives its
/// <see cref="LocalNode"/>: at <see cref="ServiceLifecycleStage.RuntimeInitialize"/> it opens the node root's LedgerStore,
/// which the replica endpoint serves; at <see cref="ServiceLifecycleStage.Active"/> it registers the node through the
/// controller grain, and the node serves once the registration answers <see cref="NodeRegistrationOutcome.Registered"/>
/// (OR-I2); when the active stage stops the node stops serving, and once the store is closed the node is closed.
/// </summary>
/// <remarks>
/// A retired node registers too, staying retired: its silo serves its replicas until its retirement gate opens, and
/// executes no transition (OR-I3). The node registers with the archive store the node registry records, never one its
/// configuration names, or at a new cluster's first registration with its store's record's (OR-I20). Every other answer
/// stops the silo: a later silo of the node registered, another new silo recorded another archive store first, a registry
/// at the register's size limit takes no new node, and an ordinal another
/// node holds active is taken only when <see cref="DurableStorageOptions.NodeToReplace"/> names that node, which a
/// critical event explains. A registration call whose outcome is unknown is made again, with bounded, jittered backoff,
/// until it is answered or the silo stops; any other failure stops the silo (conventions.md §6). A registration which
/// names a node for replacement reports that node's retirement from the registry once it is registered. The node's
/// registration completes on every path. The node's replica call limits are computed and validated when it is built, at the
/// silo's start, so a silo whose limits Orleans' messaging cannot meet does not start (orleans.md §5.12).
/// </remarks>
internal sealed partial class DurableStorageNode : ILifecycleParticipant<ISiloLifecycle>, IDisposable
{
    private static readonly TimeSpan FirstRetryDelay = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan LongestRetryDelay = TimeSpan.FromSeconds(5);

    private readonly DurableStorageOptions _options;
    private readonly ControlOptions _control;
    private readonly NodeId? _nodeToReplace;
    private readonly SiloArchive _archive;
    private readonly SiloObjectLedgers _objectLedgers;
    private readonly string _journalFormat;
    private readonly WalStorage _storage;
    private readonly ILocalSiloDetails _silo;
    private readonly IGrainFactory _grains;
    private readonly IMetaStore _metaStore;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;
    private readonly LocalNode _local;
    private readonly ReplicaCallLimits _limits;
    private readonly CancellationTokenSource _stopping = new();

    /// <summary>Initializes a new instance of the <see cref="DurableStorageNode"/> class.</summary>
    public DurableStorageNode(
        DurableStorageOptions options,
        ControlOptions control,
        LocalNode local,
        ReplicaCallLimits limits,
        SiloArchive archive,
        SiloObjectLedgers objectLedgers,
        IOptions<JournaledStateManagerOptions> journaling,
        WalStorage storage,
        ILocalSiloDetails silo,
        IGrainFactory grains,
        IMetaStore metaStore,
        TimeProvider timeProvider,
        ILogger<DurableStorageNode> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(control);
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentNullException.ThrowIfNull(objectLedgers);
        ArgumentNullException.ThrowIfNull(journaling);
        options.Validate();
        _options = options;
        _control = control;
        _nodeToReplace = options.NodeToReplace is { } named ? new NodeId(named) : null;
        _local = local;
        _limits = limits;
        _archive = archive;
        _objectLedgers = objectLedgers;
        _journalFormat = journaling.Value.JournalFormatKey;
        _storage = storage;
        _silo = silo;
        _grains = grains;
        _metaStore = metaStore;
        _timeProvider = timeProvider;
        _logger = logger;
    }


    /// <inheritdoc/>
    public void Dispose() => _stopping.Dispose();

    /// <inheritdoc/>
    public void Participate(ISiloLifecycle observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        _ = observer.Subscribe(nameof(DurableStorageNode), ServiceLifecycleStage.RuntimeInitialize, OpenAsync, CloseAsync);
        _ = observer.Subscribe(nameof(DurableStorageNode), ServiceLifecycleStage.Active, RegisterAsync, StopServingAsync);
    }

    /// <summary>
    /// Returns whether <paramref name="failure"/> leaves a registration's outcome unknown, so the call is made again: the
    /// node registry's own unknown outcome, an Orleans timeout or unavailability, a controller grain deactivating, or a
    /// cancellation the silo did not ask for. Every other failure is definite and stops the silo.
    /// </summary>
    internal static bool IsUnknownOutcome(Exception failure, CancellationToken cancellationToken) => failure switch
    {
        NodeRegistryUnavailableException or TimeoutException or SiloUnavailableException or OrleansMessageRejectionException or ObjectDisposedException => true,
        OperationCanceledException => !cancellationToken.IsCancellationRequested,
        _ => false,
    };

    /// <summary>Opens the node root's LedgerStore, or mints a fresh node identity for a diskless silo (orleans.md §5.14).</summary>
    private async Task OpenAsync(CancellationToken cancellationToken)
    {
        if (_options.IsDiskless)
        {
            var node = NodeId.New();
            _local.OpenedDiskless(node);
            LogOpenedDiskless(_logger, node, _objectLedgers.Redundancy, _limits.PayloadLimit, _limits.MaxReadReplyBytes, _limits.Deadline);
            return;
        }

        var store = await LedgerStore.OpenAsync(
            _storage,
            new LedgerStoreOptions
            {
                DirectoryPath = _options.Root,
                FileSize = _options.WalFileSize,
                PreallocateFiles = _options.PreallocateWalFiles,
                LeaseDuration = _options.Lease.LeaseDuration,
            },
            _options.Cluster,
            create: true,
            _options.Checkpoints,
            _timeProvider,
            cancellationToken).ConfigureAwait(false);
        _local.Opened(store);
        LogOpened(_logger, store.NodeId, _options.Root, _limits.PayloadLimit, _limits.MaxReadReplyBytes, _limits.Deadline);
        if (_objectLedgers.IsConfigured)
        {
            LogObjectLedgerRedundancy(_logger, store.NodeId, _objectLedgers.Redundancy);
        }
    }

    private async Task CloseAsync(CancellationToken cancellationToken)
    {
        // A start which failed or was cancelled before Active never registers.
        var store = _local.StoreIfOpen;
        _local.Close();
        if (store is not null)
        {
            // The transport answers a refusal of the disposed store unavailable only once the node is closed.
            Debug.Assert(_local.State == LocalNode.NodeState.Closed, "A node closes before its store is disposed.");

            // The final checkpoints take at most TransitionTimeout, and stop once the host's stop timeout passes: past the
            // bound the store skips the rest, which leaves the next open their replay, as a crash would (ledgerstore.md
            // §8.10), so a node's close does not grow with its replicas (orleans.md §5.2).
            using var deadline = new CancellationTokenSource(_control.TransitionTimeout, _timeProvider);
            using var bound = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
            LogStoreClosing(_logger, store.NodeId);
            var closed = await store.CloseAsync(bound.Token).ConfigureAwait(false);
            if (closed.SkippedFinalCheckpoints > 0)
            {
                LogFinalCheckpointsSkipped(_logger, store.NodeId, closed.FinalCheckpoints, closed.SkippedFinalCheckpoints, _control.TransitionTimeout);
            }
            else
            {
                LogStoreClosed(_logger, store.NodeId, closed.FinalCheckpoints);
            }
        }
    }

    /// <summary>Registers the node, calling again after each call whose outcome is unknown, until the registration is answered.</summary>
    private async Task RegisterAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopping.Token);
        var token = linked.Token;
        try
        {
            var entry = new NodeRegistryEntry(_local.Node, _silo.SiloAddress, _options.Ordinal, _options.FailureDomain, diskless: _options.IsDiskless);
            var (store, objectLedgerStore) = await ClusterStoresAsync(entry.Node, token).ConfigureAwait(false);
            var outcome = await RegisterOnceAnsweredAsync(entry, store, objectLedgerStore, token).ConfigureAwait(false);
            Exception? refusal = outcome switch
            {
                NodeRegistrationOutcome.Registered => null,
                NodeRegistrationOutcome.Stale => new InvalidOperationException($"Node {entry.Node} cannot register silo {entry.Silo}: the node registry holds it at a later silo. A wiped disk joins as a new node."),
                NodeRegistrationOutcome.WrongArchiveStore => new InvalidOperationException(
                    $"Node {entry.Node} cannot register: the node registry records another archive store than {store.Kind} store {store.Id}, the record of this silo's store, which another new silo's registration recorded first (OR-I20). "
                    + $"This silo's first registration left {_archive.FirstRecordObjects} in its store, created or adopted; once the store is confirmed not to be the cluster's, delete it by hand, or another cluster's first silo over the store adopts it."),
                NodeRegistrationOutcome.WrongObjectLedgerStore => new InvalidOperationException(
                    $"Node {entry.Node} cannot register: the node registry records another object-ledger store than {objectLedgerStore}, the record of this silo's store, which another silo's registration recorded first (OL-I1). "
                    + $"This silo's first registration left {_objectLedgers.FirstRecordObjects} in its store, created or adopted; once the store is confirmed not to be the cluster's, delete it by hand, or another cluster's first silo over the store adopts it."),
                NodeRegistrationOutcome.Refused => new InvalidOperationException($"Node {entry.Node} cannot register: its entry would take the node registry past the register's value limit of {NodeRegistryCodec.MaxValueLength} bytes."),
                NodeRegistrationOutcome.OrdinalOccupied occupied => RefuseOrdinalOccupied(entry, occupied),
                null => throw new UnreachableException(),
            };
            if (refusal is not null)
            {
                throw refusal;
            }

            if (outcome.Value is not NodeRegistrationOutcome.Registered registered)
            {
                throw new UnreachableException("Every answer but Registered is refused above.");
            }

            LogRegistered(_logger, entry.Node, entry.Silo, registered.Revision, store.Kind, store.Id);
            _local.Register(entry, store, objectLedgerStore);

            // The report follows Registered, so a slow MetaStore read never delays the node's serving.
            if (_nodeToReplace is { } nodeToReplace)
            {
                await ReportReplacementAsync(entry, nodeToReplace, registered.Revision, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // The silo stops before the node registered.
            _local.Stop();
            throw;
        }
        catch (Exception failure)
        {
            _local.RegistrationFailed(failure);
            throw;
        }
    }

    /// <summary>
    /// Returns the archive store the node registers with (OR-I20, orleans.md §5.3), reading again while an answer is unknown
    /// (<see cref="NodeLedgers.IsUnknownOutcome"/>): the identity the node registry records, so a silo joining its cluster
    /// registers at once, through an archive store outage, and never with its configuration's; or, at a new cluster's first
    /// registration, whose registry records none, the identity of the store's record, which the call creates or adopts on
    /// either kind of store; that the store belongs to this cluster alone is the operator's precondition (archive-lsm.md
    /// §9, archive-logs.md §10). Another store's fields, a corrupt record, or the store's definite refusal fails the
    /// silo's start.
    /// </summary>
    private async Task<(ArchiveStoreIdentity ArchiveStore, ObjectLedgerStoreId? ObjectLedgerStore)> ClusterStoresAsync(NodeId node, CancellationToken cancellationToken)
    {
        var failures = 0;
        var retry = new ResiliencePipelineBuilder { TimeProvider = _timeProvider }
            .AddRetry(new RetryStrategyOptions
            {
                ShouldHandle = arguments => ValueTask.FromResult(arguments.Outcome.Exception is { } failure && NodeLedgers.IsUnknownOutcome(failure, arguments.Context.CancellationToken)),
                MaxRetryAttempts = int.MaxValue,
                DelayGenerator = static arguments => ValueTask.FromResult<TimeSpan?>(Backoff.Delay(arguments.AttemptNumber, FirstRetryDelay, LongestRetryDelay, Random.Shared.NextDouble())),
                OnRetry = arguments =>
                {
                    if (NodeLedgers.LogsRetry(++failures))
                    {
                        LogArchiveStoreUnknown(_logger, node, failures, arguments.Outcome.Exception!);
                    }

                    return ValueTask.CompletedTask;
                },
            })
            .Build();
        try
        {
            return await retry.ExecuteAsync(static async (state, token) => await state.ClusterStoresOnceAsync(token).ConfigureAwait(false), this, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception refusal) when (refusal is ArchiveStoreMismatchException or ArchiveCorruptException or ObjectStoreRefusedException or ArchiveLogRefusedException)
        {
            LogFirstRegistrationRefused(_logger, node, _archive.Kind, refusal);
            throw new InvalidOperationException($"Node {node} cannot register its cluster's first silo over this store: {refusal.Message}", refusal);
        }
    }

    private async Task<(ArchiveStoreIdentity ArchiveStore, ObjectLedgerStoreId? ObjectLedgerStore)> ClusterStoresOnceAsync(CancellationToken cancellationToken)
    {
        var register = await _metaStore.GetRegisterAsync(_options.Cluster, NodeRegistryValue.RegisterName, cancellationToken).ConfigureAwait(false);
        var (registry, _) = await NodeRegistryCodec.ReadAsync(register, cancellationToken).ConfigureAwait(false);
        var archiveStore = registry.ArchiveStore ?? await _archive.FirstRecordAsync(NodeRuntime.RecordParametersOf(_options.Journals, _journalFormat), cancellationToken).ConfigureAwait(false);
        ObjectLedgerStoreId? objectLedgerStore;
        if (registry.ObjectLedgerStore is { } recorded)
        {
            objectLedgerStore = recorded;
        }
        else if (_objectLedgers.IsConfigured)
        {
            try
            {
                objectLedgerStore = await _objectLedgers.FirstRecordAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception refusal) when (refusal is ObjectLedgerStoreMismatchException or InvalidDataException or ObjectStoreRefusedException)
            {
                LogFirstObjectLedgerRegistrationRefused(_logger, _local.Node, refusal);
                throw new InvalidOperationException($"Node {_local.Node} cannot register its cluster's first silo over this object-ledger store: {refusal.Message}", refusal);
            }
        }
        else
        {
            objectLedgerStore = null;
        }

        return (archiveStore, objectLedgerStore);
    }

    /// <summary>Calls the controller grain until a call is answered, repeating each whose outcome is unknown.</summary>
    private async Task<NodeRegistrationOutcome> RegisterOnceAnsweredAsync(NodeRegistryEntry entry, ArchiveStoreIdentity store, ObjectLedgerStoreId? objectLedgerStore, CancellationToken cancellationToken)
    {
        var controller = _grains.GetGrain<IClusterControllerGrain>(IClusterControllerGrain.Key);
        var retry = new ResiliencePipelineBuilder { TimeProvider = _timeProvider }
            .AddRetry(new RetryStrategyOptions
            {
                ShouldHandle = arguments => ValueTask.FromResult(arguments.Outcome.Exception is { } failure && IsUnknownOutcome(failure, arguments.Context.CancellationToken)),
                MaxRetryAttempts = int.MaxValue,
                DelayGenerator = static arguments => ValueTask.FromResult<TimeSpan?>(Backoff.Delay(arguments.AttemptNumber, FirstRetryDelay, LongestRetryDelay, Random.Shared.NextDouble())),
                OnRetry = arguments =>
                {
                    LogRegistrationUnknown(_logger, entry.Node, arguments.Outcome.Exception!);
                    return ValueTask.CompletedTask;
                },
            })
            .Build();
        return await retry.ExecuteAsync(
            static async (state, _) => await state.controller.RegisterNodeAsync(state.entry, state.store, state.nodeToReplace, state.objectLedgerStore).ConfigureAwait(false),
            (controller, entry, store, nodeToReplace: _nodeToReplace, objectLedgerStore),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Logs the critical event for an <see cref="NodeRegistrationOutcome.OrdinalOccupied"/> answer and returns the
    /// exception which stops the silo, whose message gives the ways forward: for a new node, retiring the occupant by
    /// identity or naming it in <see cref="DurableStorageOptions.NodeToReplace"/> when its volume is lost; for a node the
    /// registry already holds, fixing the volume binding, since both nodes' volumes hold live nodes.
    /// </summary>
    private InvalidOperationException RefuseOrdinalOccupied(NodeRegistryEntry entry, NodeRegistrationOutcome.OrdinalOccupied occupied)
    {
        var configured = _nodeToReplace is { } nodeToReplace ? $"NodeToReplace names node {nodeToReplace}" : "NodeToReplace is not set";
        var message = occupied.HasEntry
            ? $"Node {entry.Node} stops without registering: ordinal {occupied.Ordinal} belongs to active node {occupied.Occupant}, and node {entry.Node} is already registered at {(occupied.EntryOrdinal is { } own ? $"ordinal {own}" : "no ordinal")}. "
                + $"This silo's root {_options.Root} holds a live node whose volume is bound to the wrong pod: bind it to the pod of its own ordinal, and retire neither node. {configured}."
            : $"Node {entry.Node} stops without registering: ordinal {occupied.Ordinal} belongs to active node {occupied.Occupant}, and node {entry.Node} is new to the registry. "
                + $"This silo's root {_options.Root} may be a volume which failed to mount, a deployment which dropped its volume mount, or a replaced disk; {configured}. "
                + $"If node {occupied.Occupant}'s volume is lost, either retire node {occupied.Occupant} through the controller grain (RetireNodeAsync({occupied.Occupant})) and start this silo again, which then registers at the freed ordinal, "
                + $"or set NodeToReplace to {occupied.Occupant} for this silo, which retires it as this node registers, and remove the setting once the silo has registered. "
                + "Otherwise restore the volume.";
        var refusal = new InvalidOperationException(message);
        LogOrdinalOccupied(_logger, entry.Node, occupied.Ordinal, occupied.Occupant, occupied.HasEntry, _options.Root, _nodeToReplace?.ToString() ?? "", refusal);
        return refusal;
    }

    /// <summary>
    /// Reports, from a read of the registry, what <see cref="DurableStorageOptions.NodeToReplace"/> did: the named node's
    /// retirement, which takes its replicas for lost, or that the setting took no effect. The committer logs each
    /// succession its writes make; this covers one whose write's answer was lost, which an earlier start of this silo may
    /// have made, so the named node counts as replaced whatever revision retired it. A failed read leaves only the report
    /// undone.
    /// </summary>
    private async Task ReportReplacementAsync(NodeRegistryEntry entry, NodeId nodeToReplace, ulong revision, CancellationToken cancellationToken)
    {
        NodeRegistryValue value;
        try
        {
            var register = await _metaStore.GetRegisterAsync(_options.Cluster, NodeRegistryValue.RegisterName, cancellationToken).ConfigureAwait(false);
            (value, _) = await NodeRegistryCodec.ReadAsync(register, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception failure) when (!cancellationToken.IsCancellationRequested)
        {
            LogReplacementUnread(_logger, entry.Node, nodeToReplace, failure);
            return;
        }

        if (value.Find(nodeToReplace) is { IsRetired: true } predecessor && predecessor.Ordinal == entry.Ordinal)
        {
            LogReplaced(_logger, entry.Node, nodeToReplace, entry.Ordinal, revision, predecessor.RetiredRevision);
        }
        else
        {
            LogReplacementIgnored(_logger, entry.Node, nodeToReplace, entry.Ordinal);
        }
    }

    private async Task StopServingAsync(CancellationToken cancellationToken)
    {
        _local.Stop();
        await _stopping.CancelAsync().ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Opened node {Node}'s LedgerStore at {Root}; replica payload limit {PayloadLimit} bytes, read reply reservation {MaxReadReplyBytes} bytes, call deadline {Deadline}.")]
    private static partial void LogOpened(ILogger logger, NodeId node, string root, int payloadLimit, int maxReadReplyBytes, TimeSpan deadline);

    [LoggerMessage(Level = LogLevel.Information, Message = "Opened diskless node {Node} (no LedgerStore; object-ledger store redundancy: {Redundancy}); replica payload limit {PayloadLimit} bytes, read reply reservation {MaxReadReplyBytes} bytes, call deadline {Deadline}.")]
    private static partial void LogOpenedDiskless(ILogger logger, NodeId node, ObjectLedgerRedundancy redundancy, int payloadLimit, int maxReadReplyBytes, TimeSpan deadline);

    [LoggerMessage(Level = LogLevel.Information, Message = "Node {Node} configured object-ledger store with operator-stated redundancy {Redundancy}.")]
    private static partial void LogObjectLedgerRedundancy(ILogger logger, NodeId node, ObjectLedgerRedundancy redundancy);

    [LoggerMessage(Level = LogLevel.Information, Message = "Registered node {Node} at silo {Silo}, registry revision {Revision}, with the cluster's {Kind} archive store {Store}.")]
    private static partial void LogRegistered(ILogger logger, NodeId node, SiloAddress silo, ulong revision, ArchiveStoreKind kind, ArchiveStoreId store);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Node {Node} could not learn its cluster's archive store from the node registry or, at its cluster's first registration, the store's record, {Failures} times so far; it reads again.")]
    private static partial void LogArchiveStoreUnknown(ILogger logger, NodeId node, int failures, Exception exception);

    [LoggerMessage(Level = LogLevel.Critical, Message = "Node {Node} stops without registering: its cluster's node registry records no archive store yet, and this silo's {Kind} store is another cluster's or not a store this silo can use, which no repetition changes. Fix the silo's archive store configuration.")]
    private static partial void LogFirstRegistrationRefused(ILogger logger, NodeId node, ArchiveStoreKind kind, Exception exception);

    [LoggerMessage(Level = LogLevel.Critical, Message = "Node {Node} stops without registering: its cluster's node registry records no object-ledger store yet, and this silo's object-ledger store record is corrupt, mismatched, or refused, which no repetition changes. Fix the silo's object-ledger store configuration.")]
    private static partial void LogFirstObjectLedgerRegistrationRefused(ILogger logger, NodeId node, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Registering node {Node} ended with an unknown outcome; registering again.")]
    private static partial void LogRegistrationUnknown(ILogger logger, NodeId node, Exception exception);

    [LoggerMessage(Level = LogLevel.Critical, Message = "Node {Node} stops without registering: ordinal {Ordinal} belongs to active node {Occupant} (node already registered: {HasEntry}; root {Root}; NodeToReplace '{NodeToReplace}'). The exception gives the way forward.")]
    private static partial void LogOrdinalOccupied(ILogger logger, NodeId node, int ordinal, NodeId occupant, bool hasEntry, string root, string nodeToReplace, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Node {Node} registered at ordinal {Ordinal}, registry revision {Revision}, in place of node {Predecessor}, which NodeToReplace names and the registry holds retired at revision {RetiredRevision}, by this start's registration or an earlier one's: its replicas are taken for lost. Remove NodeToReplace from this silo's configuration.")]
    private static partial void LogReplaced(ILogger logger, NodeId node, NodeId predecessor, int? ordinal, ulong revision, ulong retiredRevision);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Node {Node} registered at ordinal {Ordinal}; NodeToReplace names node {Named}, which the registry does not hold retired at that ordinal, so the setting took no effect. Remove it from this silo's configuration.")]
    private static partial void LogReplacementIgnored(ILogger logger, NodeId node, NodeId named, int? ordinal);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Node {Node} registered with NodeToReplace naming node {Named}, but reading the node registry to report that node's retirement failed.")]
    private static partial void LogReplacementUnread(ILogger logger, NodeId node, NodeId named, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Node {Node}'s LedgerStore closes: it drains its WAL and writes its final checkpoints (orleans.md §5.2).")]
    private static partial void LogStoreClosing(ILogger logger, NodeId node);

    [LoggerMessage(Level = LogLevel.Information, Message = "Node {Node}'s LedgerStore closed, with {Written} final checkpoints.")]
    private static partial void LogStoreClosed(ILogger logger, NodeId node, int written);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Node {Node}'s LedgerStore closed with {Written} final checkpoints, and skipped {Skipped} once its bound of {Bound} passed; its next open replays their replicas from their checkpoints in force.")]
    private static partial void LogFinalCheckpointsSkipped(ILogger logger, NodeId node, int written, int skipped, TimeSpan bound);
}
