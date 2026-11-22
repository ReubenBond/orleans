using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Simulation.Tests;

/// <summary>
/// A scripted run in which a reader on a replica's node learns the open ledger's contiguous tail from its own replica,
/// without a seal check, and reads below it (ledger-protocol.md §8.2, LP-I11, ledgers.md §8.5): a
/// <see cref="LedgerWriter"/> appends three batches and, once idle, writes its contiguous tail explicitly to every
/// replica; the reader on the second node learns that tail from the replica there and reads the last batch from it. Its
/// ledger trace maps the learn to the model's fetcher, which learns a replica's retained tail (<c>LearnTail</c>), so trace
/// validation checks the learned tail against the model (test/Durable.Storage/docs/spec/verification.md §5.1).
/// </summary>
/// <remarks>
/// The random corpora's readers learn no tail from one replica; this run fixes one which does. <c>just ledger-traces</c>
/// validates its trace with the corpora's.
/// </remarks>
public sealed class LearnTailSimulationTests
{
    private const int Seed = 1;
    private const int Quorum = 2;
    private const int Appends = 3;
    private const string Writer = "writer";
    private const string Learner = "learner";
    private static readonly LedgerReplicaOptions Options = new() { WindowSize = 4 };
    private static readonly TimeSpan Deadline = TimeSpan.FromMilliseconds(40);

    /// <summary>
    /// The learner's tail read of its own replica is its fetcher's learn, which the replica answers with the writer's
    /// explicit contiguous tail, and the learner then reads the last batch below it from that replica.
    /// </summary>
    [Fact]
    public void AReadersLearnOfItsOwnReplicaIsItsFetchersLearnTail()
    {
        string? module = null;
        _ = SeededSimulation.Run(Seed, async environment => module = await SimulateAsync(environment));
        var steps = module!.Split('\n').Select(static line => line.Trim()).ToList();
        Assert.Contains(steps, static step =>
            step.StartsWith(".then(stepServeLearn(0, 1, ", StringComparison.Ordinal)
            && step.Contains("\"Tail\", 3, ", StringComparison.Ordinal));
        Assert.DoesNotContain(steps, static step =>
            step.StartsWith(".then(stepServeLearn(", StringComparison.Ordinal)
            && !step.StartsWith(".then(stepServeLearn(0, 1, ", StringComparison.Ordinal));
        Assert.Contains(steps, static step =>
            step.StartsWith(".then(stepServePeek(1, 2, ", StringComparison.Ordinal)
            && step.Contains("\"Batch\"", StringComparison.Ordinal));
    }

    /// <summary>
    /// Appends three batches through a <see cref="LedgerWriter"/>, waits until its explicit contiguous-tail write has
    /// reached the second node, then learns the tail through a reader whose preferred replica is that node's, and reads
    /// the last batch below it.
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
        var learnerNode = nodes[1];
        var replicas = new SimulatedLedgerReplicaTransport(environment, client, nodes, Deadline);
        var trace = replicas.LedgerTrace;
        trace.LogsRecoveredReplicas = true;
        _ = LedgerStoreInvariants.Register(environment, replicas);

        // The writer's tail reads are its explicit contiguous-tail writes alone: it runs no check. Once its appends
        // completed, the first which reaches the learner's node carries the completed prefix.
        var appended = false;
        var explicitTail = new TaskCompletionSource<ReplicaServing>(TaskCreationOptions.RunContinuationsAsynchronously);
        replicas.OnServing(serving =>
        {
            if (appended && serving is { Operation: LedgerOperation.TailRead, Client: Writer } && serving.Node == learnerNode)
            {
                _ = explicitTail.TrySetResult(serving);
            }
        });

        var writerQuorum = replicas.OpenQuorum(ledger, Options, Quorum, Writer);
        await using var writerLedger = replicas.OpenLedger(ledger, writerQuorum);
        await using var writer = writerLedger.CreateWriter(SimulationEnvironment.Clock);
        trace.TraceWriter(writer, Writer, ledger);
        for (var address = 0L; address < Appends; address++)
        {
            using var payload = ArcBuffer.Wrap(SimulatedStorage.Payload(address));
            var outcome = await writer.AppendAsync(payload, cancellationToken);
            Assert.IsType<AppendOutcome.Appended>(outcome.Value);
        }

        appended = true;
        await (await explicitTail.Task).Completed;

        // The learner runs on the second node, whose replica it prefers (ledger-protocol.md §8.4). Its Ledger owns the
        // quorum, whose disposal is idempotent.
        await using var quorum = new LedgerQuorum(
            replicas.OpenTraced(ledger, Options, Learner),
            Quorum,
            Options,
            SimulationEnvironment.Clock,
            preferredReplica: 1,
            recoveryObserver: LedgerTrace.RecoveryObserver,
            verifyBatch: static _ => true);
        await using (var learner = replicas.OpenLedger(ledger, quorum))
        {
            var learned = await learner.LearnTailAsync(cancellationToken);
            Assert.Equal(new Slot(Appends), learned);
            Assert.Equal(learned, learner.KnownContiguousTail);

            var read = await learner.ReadAsync(new Slot(Appends - 1), cancellationToken);
            var answer = read.Case;
            read.Release();
            Assert.Equal(ReplicaReadCase.Batch, answer);
        }

        LedgerTraces.Export(environment, "learn", trace, ledger, nodes, Quorum, Options.WindowSize);
        return LedgerTraces.Render(trace, ledger, nodes, Quorum, Options.WindowSize);
    }
}
