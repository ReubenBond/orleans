using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Simulation.Tests;

/// <summary>
/// A scripted run in which one node's process answers from records whose own requests it never answered, and then
/// crashes: a writer write, a recovery write, and a seal each reach the node's disk while the node holds its answer, the
/// node serves other requests which show each record, and a crash discards the held answers before the node restarts
/// (test/Durable.Storage/docs/spec/verification.md §5.1). Its ledger trace names each such request at the serve which first shows it, so
/// trace validation takes each serve-time silent step (DeliverWrite, DeliverCopy, and DeliverSeal without a reply).
/// </summary>
/// <remarks>
/// The random corpora crash a node as a write leaves for its disk or as it completes, which rarely leaves the crashed
/// process time to serve another request first; this run fixes the order, so each step fires on every change.
/// <c>just ledger-traces</c> validates its trace with the corpora's.
/// </remarks>
public sealed class ServedLandingSimulationTests
{
    private const int Seed = 1;
    private const int Quorum = 2;
    private static readonly LedgerReplicaOptions Options = new() { WindowSize = 4 };
    private static readonly TimeSpan Deadline = TimeSpan.FromMilliseconds(40);

    /// <summary>
    /// Each serve-time silent step fires in the run's rendered trace: a read the crashed process served, the recovery's
    /// or the reader's, names the writer write and the recovery write whose DATA it returned (<c>landed</c>,
    /// <c>copied</c>), and its Sealed answer to a writer write names the seal it reports (<c>sealedBy</c>).
    /// </summary>
    [Fact]
    public void ACrashedProcessesAnswersNameTheRequestsWhichLandedWithoutAnswers()
    {
        string? module = null;
        _ = SeededSimulation.Run(Seed, async environment => module = await SimulateAsync(environment));
        var serves = module!.Split('\n').Select(static line => line.Trim()).Where(static line => line.StartsWith(".then(stepServe", StringComparison.Ordinal)).ToList();
        Assert.Contains(serves, static serve => Returns(serve) && serve.Contains("[(0, ", StringComparison.Ordinal));
        Assert.Contains(serves, static serve => Returns(serve) && serve.Contains("[(false, 0, 1, ", StringComparison.Ordinal));
        Assert.Contains(serves, static serve => serve.StartsWith(".then(stepServeWrite(", StringComparison.Ordinal) && serve.Contains("\"Sealed\"", StringComparison.Ordinal) && serve.Contains("[(0, ", StringComparison.Ordinal));

        // A read of the first node, the recovery's or the reader's, which returned DATA.
        static bool Returns(string serve) =>
            (serve.StartsWith(".then(stepServePeek(0, ", StringComparison.Ordinal) || serve.Contains(", 0, ", StringComparison.Ordinal) && serve.StartsWith(".then(stepServeRead(", StringComparison.Ordinal))
            && serve.Contains("\"Batch\"", StringComparison.Ordinal);
    }

    /// <summary>
    /// Writes position 0 everywhere and position 1 to the second node alone, recovers the ledger, which copies position 1,
    /// while the first node holds its answers to its position-0 write, the recovery's seals, and the recovery's copies;
    /// then reads both positions from the first node and writes past the end there, which answers Sealed, before the node
    /// crashes and restarts.
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
        var (holding, alone) = (nodes[0], nodes[1]);
        var replicas = new SimulatedLedgerReplicaTransport(environment, client, nodes, Deadline);
        var trace = replicas.LedgerTrace;
        trace.LogsRecoveredReplicas = true;
        _ = LedgerStoreInvariants.Register(environment, replicas);

        // The first node holds each answer it gives to these requests, every delivery of them, until its process crashes.
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = new Dictionary<LedgerOperation, TaskCompletionSource<ReplicaServing>>
        {
            [LedgerOperation.WriterWrite] = new(TaskCreationOptions.RunContinuationsAsynchronously),
            [LedgerOperation.Seal] = new(TaskCreationOptions.RunContinuationsAsynchronously),
            [LedgerOperation.RecoveryWrite] = new(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        replicas.OnServing(serving =>
        {
            if (serving.Node == holding && serving.Ledger == ledger
                && (serving is { Operation: LedgerOperation.WriterWrite, Address: 0 }
                    || serving is { Operation: LedgerOperation.Seal or LedgerOperation.RecoveryWrite, Client: "recovery" }))
            {
                serving.HoldAnswer(release.Task);
                _ = held[serving.Operation].TrySetResult(serving);
            }
        });

        // The store answers once the request's record is durable (conventions §13), so a held answer's record landed.
        async Task LandedAsync(LedgerOperation operation) => await (await held[operation].Task).Completed;

        try
        {
            await using var writer = replicas.OpenLedger(ledger, Options, Quorum, "client");
            Assert.Equal(ReplicaWriteCase.Written, (await WriteAsync(writer, 0)).Case);
            await LandedAsync(LedgerOperation.WriterWrite);
            await using (var only = Open(alone, "client"))
            {
                using var payload = ArcBuffer.Wrap(SimulatedStorage.Payload(1));
                Assert.Equal(ReplicaWriteCase.Written, (await only.WriteAsync(new Slot(1), payload, writer.KnownContiguousTail, recovery: false, cancellationToken)).Case);
            }

            // The recovery seals the other two nodes and copies position 1 to the third, so it ends at 2 without the
            // first node's answers.
            Slot end;
            await using (var recovery = replicas.OpenLedger(ledger, Options, Quorum, "recovery"))
            {
                end = (await trace.RecoverAsync(recovery, "recovery", ledger, cancellationToken)).End;
            }

            Assert.Equal(new Slot(2), end);
            await LandedAsync(LedgerOperation.Seal);
            await LandedAsync(LedgerOperation.RecoveryWrite);

            // The first node's process shows each record whose answer it holds.
            await using (var reader = Open(holding, "reader"))
            {
                foreach (var address in new long[] { 0, 1 })
                {
                    var read = await reader.ReadAsync(new Slot(address), Slot.Zero, cancellationToken);
                    var answer = read.Case;
                    read.Release();
                    Assert.Equal(ReplicaReadCase.Batch, answer);
                }
            }

            await using (var past = Open(holding, "client"))
            {
                using var payload = ArcBuffer.Wrap(SimulatedStorage.Payload(end.Value));
                Assert.Equal(ReplicaAnswers.Sealed, await past.WriteAsync(end, payload, writer.KnownContiguousTail, recovery: false, cancellationToken));
            }

            // The crash ends the process before it releases the held answers, which it then discards.
            var crash = holding.CrashAsync(operatingSystem: false);
            _ = release.TrySetResult();
            await crash;
            await holding.StartAsync(cancellationToken);
        }
        finally
        {
            _ = release.TrySetResult();
        }

        LedgerTraces.Export(environment, "serve", trace, ledger, nodes, Quorum, Options.WindowSize);
        return LedgerTraces.Render(trace, ledger, nodes, Quorum, Options.WindowSize);

        ILedgerReplica Open(LedgerStoreNode node, string name) =>
            trace.Traced(replicas.Open(node.NodeId, ledger, Options, name), name, node.Address, ledger);

        async Task<LedgerWriteOutcome> WriteAsync(Ledger writer, long address)
        {
            using var payload = ArcBuffer.Wrap(SimulatedStorage.Payload(address));
            return await trace.WriteAsync(writer, "client", ledger, new Slot(address), payload, cancellationToken);
        }
    }
}
