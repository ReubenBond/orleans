using System.Collections.Immutable;
using Orleans.Durable.Storage.Engine.Ledgers;

namespace Orleans.Durable.Storage.Engine.Cluster.Control;

/// <summary>
/// What a node reports to the controller each control interval, and at once when a ledger becomes <c>Unserved</c>
/// (orleans.md §5.6, §7.2).
/// </summary>
/// <param name="Node">The reporting node.</param>
/// <param name="Generation">The generation of the reporting node's silo: the controller ignores a report from a silo older
/// than the newest it knows for the node, a process the node's restart replaced.</param>
/// <param name="Sequence">The report's number in its process, from 1, which only grows: the controller ignores a report of
/// the generation it last heard which is not newer than the last it applied, as a report the node gave up on and which
/// arrives after a newer one is (orleans.md §5.6, OR-I21).</param>
/// <param name="Unserved">The open ledgers whose claims name the node under a lifetime its process does not hold
/// (ledgers.md §2.4).</param>
/// <param name="ProposedBytesPerSecond">The load of each ledger the node is primary of and serves: the bytes its writer
/// proposed per second over <c>LoadWindow</c>, only of the ledgers whose writer was measured for a whole window.</param>
/// <param name="DiskPressure">Whether the node is under disk pressure (orleans.md §5.9).</param>
/// <param name="ClosedDrainProgress">For each closed ledger the node drains, the time of its drain's last completed
/// progress.</param>
/// <param name="LostReplicas">The ledgers whose replica on the node its store found damaged, which the node lost
/// (orleans.md §5.6, LR-I2).</param>
/// <param name="UndrainedKeys">On archive logs, the keys of the closed segments of the ledgers the node owes a drain: each
/// segment's key count, its writer's or its scan's, and, for a ledger no count covers, one segment of the running average
/// of the counts the node learned, <see cref="ArchiveLogNodeDrains.DefaultSegmentKeys"/> before the first, at most
/// <c>MaxSegmentKeys</c>, and at least 1; a ledger whose drain answered
/// <c>Waiting</c> counts nothing (orleans.md §5.6); 0 on the LSM.</param>
/// <param name="ArchiveLogRequestsPerSecond">On archive logs, the rate the node's drains run at, from its controller's last
/// share, which the controller reserves; <see langword="null"/> before the node's first share, and on the LSM.</param>
/// <param name="HandingOff">Whether the node hands its ledgers off, as its silo stops or its Journals are refused: the
/// controller records it as the node's hand-off request, so a new activation knows it pending from its first report
/// (orleans.md §5.6).</param>
/// <param name="ArchiveGatePassed">Whether the node's silo passed its archive gate (orleans.md §5.13, OR-I20): a controller
/// grain on a silo whose gate has not passed moves to the silo of a node which reports it passed.</param>
/// <param name="Draining">Whether the node drains: it serves, its hand-off not begun, and its silo's archive gate
/// passed, the one condition under which its control loop starts drains; the controller names the next replica which
/// drains for the closed ledgers of a node which does not, at once (orleans.md §5.6).</param>
/// <param name="ObjectStoreDemand">The node's demand on the shared object-store request budget (orleans.md §5.14); 0 when
/// the node holds no object-store work.</param>
/// <param name="ObjectStoreRequestsPerSecond">The rate the node's object-store budget currently runs at, from its
/// controller's last share, which the controller reserves; <see langword="null"/> before the node's first share or when no
/// object-ledger store is configured.</param>
/// <param name="ObjectLedgersReady">Whether the node has an object-ledger store configured and its object-ledger gate
/// passed (OL-I1, orleans.md §5.14): the controller restricts object-store placement, takeovers, range targets, and
/// object-store drains to ready nodes so mixed clusters during rolling migration never target unconfigured or pending
/// silos.</param>
/// <param name="ReleasedLedgers">Open ledgers whose writers this node cooperatively drained and stopped admitting via
/// <c>PrepareRelease</c>, mapped to their final contiguous tail, so a replacement or a new controller activation after
/// failover observes their released state idempotently (orleans.md §5.6, OR-I24).</param>
/// <param name="CatchUpReady">Whether the node's local apply loop has finished initial catch-up for the ledgers present
/// when it started (orleans.md §5.6): routine primary balancing waits until this is <see langword="true"/>, while urgent
/// departures, takeovers, splits, rolls, and repairs remain immediately eligible.</param>
internal sealed record NodeReport(
    NodeId Node,
    int Generation,
    long Sequence,
    ImmutableArray<LedgerId> Unserved,
    ImmutableDictionary<LedgerId, long> ProposedBytesPerSecond,
    bool DiskPressure,
    ImmutableDictionary<LedgerId, DateTimeOffset> ClosedDrainProgress,
    ImmutableArray<LedgerId> LostReplicas,
    long UndrainedKeys,
    double? ArchiveLogRequestsPerSecond,
    bool HandingOff,
    bool ArchiveGatePassed,
    bool Draining,
    long ObjectStoreDemand = 0,
    double? ObjectStoreRequestsPerSecond = null,
    bool ObjectLedgersReady = false,
    ImmutableDictionary<LedgerId, Slot>? ReleasedLedgers = null,
    bool CatchUpReady = true)
{
    /// <summary>Gets the released ledgers map, never <see langword="null"/>.</summary>
    public ImmutableDictionary<LedgerId, Slot> ReleasedLedgersOrEmpty => ReleasedLedgers ?? [];

    /// <summary>
    /// Returns the report numbered <paramref name="sequence"/> of a node's silo of <paramref name="generation"/> which
    /// serves every ledger it holds, carries no load, drains nothing, and lost no replica.
    /// </summary>
    public static NodeReport Idle(NodeId node, int generation, long sequence) => new(node, generation, sequence, [], [], DiskPressure: false, [], [], UndrainedKeys: 0, ArchiveLogRequestsPerSecond: null, HandingOff: false, ArchiveGatePassed: false, Draining: false, ReleasedLedgers: []);
}
