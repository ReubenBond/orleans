using System.Diagnostics;
using Orleans.Durable.Storage.Engine.Cluster.Control;
using Orleans.Durable.Storage.Engine.GroupCommit;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.Ledgers.Control;
using Orleans.Durable.Storage.Engine.Ledgers.Store;
using Orleans.Placement;

namespace Orleans.Durable.Storage.Control;

/// <summary>
/// A node's <see cref="INodeControlGrain"/> activation (orleans.md §5.6, §7.2): placed only on the silo the registry names
/// for the node (<see cref="RegisteredNodePlacement"/>) and never moved, it admits each call through the node's serving
/// gate and forwards it to the node's control runtime, <see cref="LocalNodeControl"/>, which runs the node controller on
/// its own exclusive scheduler. An activation which finds its silo serving another node answers as a node which does not
/// serve, and deactivates; a node which stops declines a transition and refuses a compaction.
/// </summary>
/// <remarks>
/// The grain holds no state: the runtime owns the node controller, its scheduler, and its lifetime, so a new activation
/// of the grain shares the runtime's one state with the old one. Each commit delegate it gives the controller calls the
/// controller grain and relays a failure as an unknown outcome, apart from the controller grain's definite refusal, so a
/// change which may have applied never abandons its lifetimes.
/// </remarks>
[RegisteredNodePlacement]
[Immovable]
internal sealed class NodeControlGrain(LocalNode local, LocalNodeControl control, NodePressure pressure, SiloArchive archive, SiloObjectLedgers objectLedgers) : Grain, INodeControlGrain
{
    private NodeId Node => new(this.GetPrimaryKey());

    private IClusterControllerGrain ClusterController => GrainFactory.GetGrain<IClusterControllerGrain>(IClusterControllerGrain.Key);

    /// <inheritdoc/>
    public async Task<TransitionReply> ExecuteAsync(Transition transition)
    {
        if (!await ServingAsync())
        {
            return TransitionReply.Declined.Instance;
        }

        if (transition.Choice.Location.Value is LedgerLocation.ObjectStore && !objectLedgers.Gate.IsPassed)
        {
            // OL-I1: the node executes object-store transitions only once its silo's object-ledger gate has passed.
            return TransitionReply.Declined.Instance;
        }

        // The delegate is created here, in the activation, and never crosses a grain call (orleans.md §5.6).
        var clusterController = ClusterController;
        return await control.ExecuteAsync(Node, transition, (change, token) => CommitAsync(() => clusterController.CommitLedgerMapAsync(change), token));
    }

    /// <inheritdoc/>
    public async Task<PrepareReleaseReply> PrepareReleaseAsync(PrepareReleaseRequest request)
    {
        if (!await ServingAsync())
        {
            return PrepareReleaseReply.Declined.Instance;
        }

        return await control.PrepareReleaseAsync(Node, request);
    }

    /// <inheritdoc/>
    public async Task<CompactionReply> CompactAsync(CompactionAssignment assignment)
    {
        if (!await ServingAsync())
        {
            throw new NodeControlStoppedException($"Node {Node} does not serve on this silo; the compaction of {assignment.Partition} did not start.");
        }

        if (!archive.Gate.IsPassed)
        {
            // OR-I20: the node reads and writes the archive only once its silo's check of the store's record passed.
            throw new NodeControlStoppedException($"Node {Node}'s archive gate has not passed; the compaction of {assignment.Partition} did not start.");
        }

        var clusterController = ClusterController;
        return await control.CompactAsync(Node, assignment, (change, token) => CommitAsync(() => clusterController.CommitManifestAsync(change), token));
    }

    /// <inheritdoc/>
    public async Task ReportPressureAsync(NodeId node)
    {
        if (await ServingAsync())
        {
            pressure.Report(node);
        }
    }

    /// <summary>
    /// Submits a change through the controller grain's commit call: its verdict, the controller grain's definite refusal as
    /// it is, and every other failure, among them a reply the call could not decode, as an unknown outcome.
    /// </summary>
    private static async Task<T> CommitAsync<T>(Func<Task<T>> call, CancellationToken cancellationToken)
    {
        try
        {
            return await call().WaitAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (ControllerInactiveException)
        {
            throw;
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            throw new GroupCommitOutcomeUnknownException("The controller grain's commit call failed; the change may have applied.", failure);
        }
    }

    /// <summary>
    /// Admits a call through the node's serving gate: <see langword="true"/> when the silo serves the grain's node;
    /// <see langword="false"/> when it stops, or serves another node, which deactivates this activation (§7.2).
    /// </summary>
    private async Task<bool> ServingAsync()
    {
        return await local.ServingAsync(Node, CancellationToken.None) switch
        {
            LedgerStore or LocalNodeServing.Diskless => true,
            NotThisNode => DeactivateAndDecline(),
            LocalNodeServing.Stopping => false,
            null => throw new UnreachableException("conventions.md §15: a serving answer holds a case."),
        };
    }

    private bool DeactivateAndDecline()
    {
        this.DeactivateOnIdle();
        return false;
    }
}
