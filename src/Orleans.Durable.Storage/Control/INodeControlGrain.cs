using Orleans.Concurrency;
using Orleans.Durable.Storage.Engine.Cluster.Control;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.Ledgers.Control;

namespace Orleans.Durable.Storage.Control;

/// <summary>
/// A node's control grain (orleans.md §5.6, §7.2), keyed by the node identity and placed on its registered silo: the
/// controller grain's way into the node's control runtime, which executes its transitions and compactions. Its calls
/// interleave, since the runtime runs them on its own exclusive scheduler, where they interleave at awaits.
/// </summary>
internal interface INodeControlGrain : IGrainWithGuidKey
{
    /// <summary>
    /// Executes <paramref name="transition"/> through the node's <c>TransitionExecutor</c>, submitting its change to the
    /// controller grain, and answers its verdict, or <c>Declined</c> when the node is retired, not the transition's
    /// executor, handing off or stopping, or not this silo's (OR-I3, §7.2).
    /// </summary>
    [AlwaysInterleave]
    Task<TransitionReply> ExecuteAsync(Transition transition);

    /// <summary>
    /// Cooperatively drains active journal writes on <paramref name="request"/>'s open ledger within its deadline and
    /// stops admitting new writes before a planned replacement handoff, answering <c>Released(contiguousTail)</c> on
    /// clean drain (and idempotently on retry), <c>Sealed</c> on concurrent seal, <c>TimedOut</c> if the deadline elapses,
    /// <c>NotServed</c> when the ledger is not held, or <c>Declined</c> when the node is stopped or not this silo's
    /// (orleans.md §5.6, §7.2, OR-I24).
    /// </summary>
    [AlwaysInterleave]
    Task<PrepareReleaseReply> PrepareReleaseAsync(PrepareReleaseRequest request);

    /// <summary>
    /// Compacts the assignment's partition and submits the compaction's change to the controller grain, answering the
    /// manifest's verdict, nothing to compact, or the partition's corruption (archive-lsm.md §8.8). It throws
    /// <see cref="NodeControlStoppedException"/> when the node stops or is not this silo's, which the controller treats as
    /// an unknown outcome.
    /// </summary>
    [AlwaysInterleave]
    Task<CompactionReply> CompactAsync(CompactionAssignment assignment);

    /// <summary>
    /// Records a pressure report from <paramref name="node"/>, which is under disk pressure and replicates an open ledger
    /// this node is primary of, in this node's pressure state, where it lasts five control intervals (orleans.md §5.9,
    /// OR-I10); an activation on a silo which does not serve the node records nothing.
    /// </summary>
    [AlwaysInterleave]
    Task ReportPressureAsync(NodeId node);
}
