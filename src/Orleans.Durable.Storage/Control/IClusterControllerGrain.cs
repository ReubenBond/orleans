using System.Collections.Immutable;
using Orleans.Concurrency;
using Orleans.Durable.Storage.Engine.Archive;
using Orleans.Durable.Storage.Engine.Archive.Lsm.Control;
using Orleans.Durable.Storage.Engine.Cluster.Control;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.Ledgers.Control;
using Orleans.Durable.Storage.Engine.Ledgers.Objects;

namespace Orleans.Durable.Storage.Control;

/// <summary>
/// The controller grain, where the cluster's control plane decides (orleans.md §5.6, §7.2): it hosts the group committers
/// and runs the policies. One activation at a time serves the cluster, for performance and liveness only (OR-I12).
/// </summary>
/// <remarks>
/// The controller's state changes only in synchronous segments on the activation's scheduler, and no invariant spans an
/// await. The calls which await a committer interleave, so a registration or a commit never waits behind another; the
/// calls which only record a request run as ordinary turns.
/// </remarks>
internal interface IClusterControllerGrain : IGrainWithIntegerKey
{
    /// <summary>The controller grain's one key.</summary>
    const long Key = 0;

    /// <summary>
    /// Submits a silo's registration to the node registry committer and answers it (orleans.md §5.3). A call
    /// which fails, or whose reply is lost, is made again: a registration is idempotent.
    /// </summary>
    /// <param name="entry">The entry the silo would hold, active.</param>
    /// <param name="store">The archive store the node registry records, or, at a new cluster's first registration, the store's record's (OR-I20).</param>
    /// <param name="nodeToReplace">The node the operator names for replacement at the entry's ordinal, or <see langword="null"/>.</param>
    /// <param name="objectLedgerStore">The silo's object-ledger store ID, or <see langword="null"/> when none is configured (orleans.md §5.14).</param>
    [AlwaysInterleave]
    Task<NodeRegistrationOutcome> RegisterNodeAsync(NodeRegistryEntry entry, ArchiveStoreIdentity store, NodeId? nodeToReplace, ObjectLedgerStoreId? objectLedgerStore = null);

    /// <summary>
    /// Submits a ledger map change to the map's group committer and answers its verdict, with the deciding value's epoch
    /// for <c>Applied</c> and <c>Superseded</c> (orleans.md §5.6). A transition's executor submits its change once.
    /// </summary>
    /// <param name="change">The change.</param>
    [AlwaysInterleave]
    Task<LedgerMapCommitOutcome> CommitLedgerMapAsync(LedgerMapChange change);

    /// <summary>
    /// Submits a manifest change, a drain's or a compaction's, to the manifest's group committer and answers its verdict
    /// (orleans.md §5.6). A change which fails or whose reply is lost is submitted again; the committer merges it with an
    /// identical pending change. On archive logs it fails: the cluster keeps no manifest.
    /// </summary>
    /// <param name="change">The change.</param>
    [AlwaysInterleave]
    Task<ManifestCommitOutcome> CommitManifestAsync(ManifestChange change);

    /// <summary>
    /// Takes a node's report, each control interval, and answers the newest map epoch the controller knows durable, to
    /// which the node refreshes (OR-I19), and the closed ledgers whose drains the node duplicates (orleans.md §5.6).
    /// </summary>
    /// <param name="report">The node's report.</param>
    Task<NodeReportReply> ReportAsync(NodeReport report);

    /// <summary>Records a node's request for a WAL-release roll of <paramref name="ledger"/> (OR-I13) and returns.</summary>
    /// <param name="ledger">The ledger to roll.</param>
    Task RequestRollAsync(LedgerId ledger);

    /// <summary>
    /// Records a stopping or refused node's request, from its silo's <paramref name="generation"/>, to hand each open ledger
    /// it is primary of to another node, and returns (orleans.md §5.6, §7.2). The node repeats it each control interval
    /// while its runtime runs.
    /// </summary>
    /// <param name="node">The stopping node, or the node whose Journals are refused (OR-I8).</param>
    /// <param name="generation">The generation of the node's silo.</param>
    Task HandOffAsync(NodeId node, int generation);

    /// <summary>
    /// Records an operator's request to retire <paramref name="node"/> and returns before any register write (orleans.md
    /// §5.3): the grain submits the retirement to the node registry in the background, and its next pass raises the ledger
    /// map's retirement floor to the revision which retired the node. The caller repeats it until
    /// <see cref="GetRetirementAsync"/> answers the node retired or not registered, so a controller grain which moved
    /// learns of it again.
    /// </summary>
    /// <param name="node">The node to retire.</param>
    /// <exception cref="ArgumentException"><paramref name="node"/> is the default identity.</exception>
    Task RetireNodeAsync(NodeId node);

    /// <summary>
    /// For a StatefulSet scale-down to <paramref name="replicas"/>, records the retirement of every node active at an
    /// ordinal of <paramref name="replicas"/> or more, resolved from a registry value read after the call, and returns
    /// before any register write every node at those ordinals whose gate is not open, retired already or not, with its
    /// ordinal and standing: the nodes whose gates must open before the StatefulSet's replicas are reduced and the ordinals'
    /// volumes deleted, which the caller follows up by identity, through <see cref="GetRetirementAsync"/> and
    /// <see cref="RetireNodeAsync"/> (orleans.md §5.3, §8).
    /// </summary>
    /// <param name="replicas">The StatefulSet's replica count after the scale-down.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="replicas"/> is below <c>N</c>.</exception>
    [AlwaysInterleave]
    Task<ImmutableArray<OrdinalNode>> ScaleDownAsync(int replicas);

    /// <summary>
    /// For a deleted volume at <paramref name="ordinal"/>, returns before any register write every node a registry value
    /// read after the call holds at the ordinal whose gate is not open, with its standing, and records the retirement of
    /// each one active whose silo membership shows dead (orleans.md §5.3); a live occupant is returned active, for its
    /// caller to retire by identity once its pod has stopped. The caller follows up by identity alone, through
    /// <see cref="GetRetirementAsync"/> and <see cref="RetireNodeAsync"/>, never by repeating this call: a repeat would
    /// resolve the ordinal afresh, and could name a successor which registered there since.
    /// </summary>
    /// <param name="ordinal">The ordinal whose volume was deleted.</param>
    [AlwaysInterleave]
    Task<ImmutableArray<OrdinalNode>> RetireVolumeAsync(int ordinal);

    /// <summary>
    /// Answers <paramref name="node"/>'s standing and, once retired, its gate (orleans.md §5.8, OR-I4):
    /// <see cref="NodeStanding.NotRegistered"/>, from a registry value read after the call, which ends the caller's
    /// repetition; <see cref="NodeStanding.Active"/>, its retirement pending; or <see cref="NodeStanding.Retired"/>, from the
    /// newest ledger map value the grain's committer read, or wrote with a write which landed, reading the register first
    /// when that value does not show the gate open: whether the map's floor covers the retirement, whether the node's disk
    /// is releasable, which ends the caller's repetition, and the ledgers which still name the node.
    /// </summary>
    /// <param name="node">The node.</param>
    /// <exception cref="ArgumentException"><paramref name="node"/> is the default identity.</exception>
    [AlwaysInterleave]
    Task<NodeStanding> GetRetirementAsync(NodeId node);
    /// <summary>
    /// Starts a planned whole-cluster drain off node-backed ledgers into object-store ledgers before an instance-releasing
    /// stop (orleans.md §5.14, §8): fails closed when object-store ledgers are not configured or the controller's
    /// object-ledger gate has not passed; otherwise durably records <c>DrainAllRevision</c> in the node registry so every
    /// controller activation suppresses new node-backed placement, raises the ledger map's retirement floor to
    /// <c>DrainAllRevision</c> (LG-I11), rolls every open node-backed ledger onto the object store, drains and drops every
    /// closed node-backed ledger, and once the ledger map lists zero node-backed ledgers at or above that floor, atomically
    /// retires every active diskful node and clears <c>DrainAllRevision</c> in one registry write, then raises the map's
    /// retirement floor so silos restarted over empty roots register at their ordinals without <c>OrdinalOccupied</c>.
    /// Idempotent across retries and controller failovers (OR-I12).
    /// </summary>
    [AlwaysInterleave]
    Task<DrainAllStartOutcome> StartDrainAllAsync();

    /// <summary>
    /// Cancels an active drain-all by clearing <c>DrainAllRevision</c> in the node registry and restores
    /// <see cref="ControlOptions.LedgerPlacement"/> (orleans.md §5.14, §8); if Phase 2 already committed
    /// <c>CompleteDrainAll</c>, returns <see langword="false"/> and committed retirements remain retired (OR-I2).
    /// </summary>
    /// <returns>Whether drain-all was active and cleared by this call.</returns>
    [AlwaysInterleave]
    Task<bool> CancelDrainAllAsync();

    /// <summary>
    /// Answers the cluster's drain-all progress and stop readiness from a linearizable map-then-registry read of fresh
    /// durable values (orleans.md §5.14, §8, OR-I19): <see cref="DrainAllStatus.IsReady"/> holds once drain-all is no
    /// longer active (<c>DrainAllRevision == 0</c>), the durable map lists zero ledgers on nodes (open or closed), the
    /// durable registry lists zero active diskful nodes, and the map's retirement floor covers every diskful node's
    /// retirement revision (LG-I11, OR-I5).
    /// </summary>
    [AlwaysInterleave]
    Task<DrainAllStatus> GetDrainAllAsync();

    /// <summary>
    /// Releases <paramref name="partition"/>, which the grain holds in its activation since a compaction found it corrupt,
    /// once an operator repaired it (archive-lsm.md §8.8); the next assignment pass assigns it again while it is overfull.
    /// </summary>
    /// <param name="partition">The partition.</param>
    /// <returns>Whether the activation held the partition.</returns>
    Task<bool> ResolveCorruptPartitionAsync(KeyRange partition);
}
