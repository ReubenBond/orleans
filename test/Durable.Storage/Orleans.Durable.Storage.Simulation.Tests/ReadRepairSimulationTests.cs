using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Simulation.Tests;

/// <summary>
/// A scripted run in which a reader repairs its preferred replica (ledger-protocol.md §8.3, LP-I10): the ledger is written
/// and closed while the first node is partitioned, so that node holds none of it, and a reader on that node, which
/// learned the recorded <c>T</c>, reads a slot below it, finds it on another replica, and copies it to the first node with
/// a recovery write (docs/spec/verification.md §5.1). Its ledger trace maps the repair to the model's fetcher, which reads
/// below its tail and sends the repair in one step (<c>ReadBelowTail</c>), so trace validation checks a repair against
/// the model.
/// </summary>
/// <remarks>
/// The random corpora's readers have no preferred replica, so no read of theirs repairs; this run fixes one which does.
/// <c>just ledger-traces</c> validates its trace with the corpora's.
/// </remarks>
public sealed class ReadRepairSimulationTests
{
    private const int Seed = 1;
    private const int Quorum = 2;
    private const string Reader = "reader";
    private static readonly LedgerReplicaOptions Options = new() { WindowSize = 4 };
    private static readonly TimeSpan Deadline = TimeSpan.FromMilliseconds(40);

    /// <summary>
    /// The reader's read of its preferred replica, which lacked the slot, names the reader's fetcher as the repair's
    /// sender, the repair's call and serve are that fetcher's copy, and the repaired node then holds the slot.
    /// </summary>
    [Fact]
    public void AReadersRepairOfItsPreferredReplicaIsItsFetchersCopy()
    {
        string? module = null;
        _ = SeededSimulation.Run(Seed, async environment => module = await SimulateAsync(environment));
        var steps = module!.Split('\n').Select(static line => line.Trim()).ToList();
        Assert.Contains(steps, static step =>
            step.StartsWith(".then(stepServePeek(0, ", StringComparison.Ordinal)
            && step.Contains("\"Missing\", 0, ", StringComparison.Ordinal));
        Assert.Contains(steps, static step =>
            step.StartsWith(".then(stepServeCopy(true, 0, 0, 0, ", StringComparison.Ordinal)
            && step.Contains("\"Written\"", StringComparison.Ordinal));
    }

    /// <summary>
    /// Writes positions 0 and 1 while the first node is partitioned, recovers and closes the ledger at 2 without it, then
    /// reconnects it and reads position 0 through a reader whose preferred replica is the first node's.
    /// </summary>
    /// <returns>The run's rendered ledger trace.</returns>
    private static async Task<string> SimulateAsync(SimulationEnvironment environment)
    {
        // The scenario's calls carry a cancellable token, as production callers' do, which nothing cancels.
        using var cancellation = new CancellationTokenSource();
        var cancellationToken = cancellation.Token;
        var ledger = SimulatedStorage.Ledger;
        environment.Network.MessageDropRate = 0;
        environment.Rpc.DuplicationRate = 0;
        var client = environment.Cluster.AddNode("client");
        var nodes = await LedgerStoreNode.StartNodesAsync(environment, SimulatedStorage.Cluster, 3, cancellationToken);
        var lacking = nodes[0];
        var replicas = new SimulatedLedgerReplicaTransport(environment, client, nodes, Deadline);
        var trace = replicas.LedgerTrace;
        trace.LogsRecoveredReplicas = true;
        _ = LedgerStoreInvariants.Register(environment, replicas);
        var repair = new TaskCompletionSource<ReplicaServing>(TaskCreationOptions.RunContinuationsAsynchronously);
        replicas.OnServing(serving =>
        {
            if (serving is { Operation: LedgerOperation.RecoveryWrite, Client: Reader })
            {
                _ = repair.TrySetResult(serving);
            }
        });

        lacking.Isolate();
        await using (var writer = replicas.OpenLedger(ledger, Options, Quorum, "client"))
        {
            foreach (var address in new long[] { 0, 1 })
            {
                using var payload = ArcBuffer.Wrap(SimulatedStorage.Payload(address));
                var written = await trace.WriteAsync(writer, "client", ledger, new Slot(address), payload, cancellationToken);
                Assert.Equal(ReplicaWriteCase.Written, written.Case);
            }
        }

        Slot end;
        await using (var recovery = replicas.OpenLedger(ledger, Options, Quorum, "recovery"))
        {
            end = (await trace.RecoverAsync(recovery, "recovery", ledger, cancellationToken)).End;
        }

        Assert.Equal(new Slot(2), end);
        trace.Closed(ledger, "recovery", end.Value);
        lacking.Reconnect();

        // The reader runs on the first node, whose replica it prefers (ledger-protocol.md §8.4). Its Ledger owns the
        // quorum, whose disposal is idempotent.
        await using var quorum = new LedgerQuorum(
            replicas.OpenTraced(ledger, Options, Reader),
            Quorum,
            Options,
            SimulationEnvironment.Clock,
            preferredReplica: 0,
            recoveryObserver: LedgerTrace.RecoveryObserver,
            verifyBatch: static _ => true);
        await using (var reader = replicas.OpenLedger(ledger, quorum))
        {
            trace.ObserveRecordedTail(reader, Reader, ledger, end);
            var read = await reader.ReadAsync(Slot.Zero, cancellationToken);
            var answer = read.Case;
            read.Release();
            Assert.Equal(ReplicaReadCase.Batch, answer);

            // The read answers without waiting for its repair, which the reader's disposal would cancel.
            await (await repair.Task).Completed;
        }

        // The first node now holds the repaired slot.
        await using (var repaired = trace.Traced(replicas.Open(lacking.NodeId, ledger, Options, "check"), "check", lacking.Address, ledger))
        {
            var read = await repaired.ReadAsync(Slot.Zero, Slot.Zero, cancellationToken);
            var answer = read.Case;
            read.Release();
            Assert.Equal(ReplicaReadCase.Batch, answer);
        }

        LedgerTraces.Export(environment, "repair", trace, ledger, nodes, Quorum, Options.WindowSize);
        return LedgerTraces.Render(trace, ledger, nodes, Quorum, Options.WindowSize);
    }
}
