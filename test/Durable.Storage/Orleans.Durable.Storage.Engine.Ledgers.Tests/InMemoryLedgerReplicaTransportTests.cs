using CsCheck;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Ledgers.Memory;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Ledgers.Tests;

/// <summary>
/// Checks <see cref="InMemoryLedgerReplicaTransport"/> (ledger-replicas.md §2.2, §2.3, §9.1): a node creates a replica with its first
/// write, recovery write, or seal unless it dropped the ledger, a seal creating it sealed, and a read or contiguous-tail
/// read of one it neither holds nor dropped answers as an empty one (LR-I7); the replica records the window of the replica
/// which created it and refuses another (LR-I6); a drop removes the replica, if held, and leaves the ledger's
/// tombstone, after which every facade refuses and nothing creates the replica again (LR-I3).
/// </summary>
public sealed class InMemoryLedgerReplicaTransportTests : IDisposable
{
    private static readonly LedgerId Ledger = LedgerReplicaTestData.Ledger;
    private static readonly LedgerReplicaOptions Options = new() { WindowSize = 4 };
    private static readonly NodeId NodeA = LedgerReplicaTestData.Node;
    private static readonly NodeId NodeB = new(Guid.ParseExact("22222222333344445555666666666666", "N"));
    private static readonly NodeId NodeC = new(Guid.ParseExact("33333333444455556666777777777777", "N"));

    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    public void Dispose() => _leaks.Dispose();

    /// <summary>
    /// LR-I7: "A read, a contiguous-tail read, and <c>Open</c> create nothing, and a replica the node neither holds nor
    /// dropped reads as empty"; its first write, recovery write, or seal creates it, a seal creating it sealed, and a replica
    /// opened before then finds it.
    /// </summary>
    [Fact]
    public async Task TheFirstMutationCreatesAReplicaAndReadsCreateNothing()
    {
        var token = TestContext.Current.CancellationToken;
        using var nodes = new InMemoryLedgerReplicaTransport();
        using var payload = Payload(Value(Ledger));
        var replica = nodes.Open(NodeA, Ledger, Options);

        Assert.Null(await LedgerReplicaTestData.ReadBytesAsync(replica, 0, token));
        Assert.Equal(ReplicaReadCase.Missing, (await replica.ReadAsync(Slot.Zero, 0, 1, token)).Case);
        Assert.Equal(new LedgerReplicaTail(new Slot(3), IsSealed: false), await replica.ReadTailAsync(new Slot(3), seal: false, token));
        Assert.Empty(await ListAsync(nodes, NodeA));

        // A rejected request creates nothing either.
        using (var empty = Payload([]))
        {
            await Assert.ThrowsAsync<ArgumentException>(async () => await replica.WriteAsync(0, empty, token));
        }

        Assert.Empty(await ListAsync(nodes, NodeA));

        Assert.Equal(ReplicaAnswers.Written, await replica.WriteAsync(0, payload, token));
        Assert.Equal([Ledger], await ListAsync(nodes, NodeA));
        Assert.Equal(Value(Ledger), await LedgerReplicaTestData.ReadBytesAsync(nodes.Open(NodeA, Ledger, Options), 0, token));

        // The contiguous-tail read of a replica which did not yet exist retained nothing.
        Assert.Equal(new LedgerReplicaTail(Slot.Zero, IsSealed: false), await replica.ReadTailAsync(token));

        // A recovery write and a seal create a replica too, the seal creating it sealed (LR-I8).
        Assert.Equal(ReplicaAnswers.Written, await nodes.Open(NodeA, new(Ledger.Value + 1), Options).RecoveryWriteAsync(0, payload, token));
        Assert.Equal(
            new LedgerReplicaTail(new Slot(2), IsSealed: true),
            await nodes.Open(NodeA, new(Ledger.Value + 2), Options).ReadTailAsync(new Slot(2), seal: true, token));
        Assert.Equal([Ledger, new LedgerId(Ledger.Value + 1), new LedgerId(Ledger.Value + 2)], await ListAsync(nodes, NodeA));
        var sealedAtCreation = nodes.Open(NodeA, new(Ledger.Value + 2), Options);
        Assert.Equal(new LedgerReplicaTail(new Slot(2), IsSealed: true), await sealedAtCreation.ReadTailAsync(token));
        Assert.Equal(ReplicaAnswers.Sealed, await sealedAtCreation.WriteAsync(0, payload, token));

        // Another node's replica of the same ledger is a distinct one, which the node does not yet hold.
        Assert.Empty(await ListAsync(nodes, NodeB));
        Assert.Null(await LedgerReplicaTestData.ReadBytesAsync(nodes.Open(NodeB, Ledger, Options), 0, token));

        // Disposing a replica leaves the replica.
        await replica.DisposeAsync();
        _ = await Assert.ThrowsAsync<ObjectDisposedException>(async () => await replica.ReadAsync(0, token));
        Assert.Equal(Value(Ledger), await LedgerReplicaTestData.ReadBytesAsync(nodes.Open(NodeA, Ledger, Options), 0, token));
    }

    /// <summary>
    /// ledger-replicas.md §2.1, §2.3: a request is checked before it reaches a replica, so a cancelled or invalid one, or one
    /// through a disposed facade, refuses and creates nothing, and a node handle refuses a cancelled listing and every
    /// listing once the nodes are disposed.
    /// </summary>
    [Fact]
    public async Task ARefusedRequestCreatesNothing()
    {
        var token = TestContext.Current.CancellationToken;
        using var nodes = new InMemoryLedgerReplicaTransport();
        using var payload = Payload(Value(Ledger));
        using var empty = Payload([]);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var replica = nodes.Open(NodeA, Ledger, Options);
        _ = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await replica.RecoveryWriteAsync(long.MaxValue, payload, token));
        _ = await Assert.ThrowsAsync<ArgumentException>(async () => await replica.RecoveryWriteAsync(0, empty, token));
        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await replica.WriteAsync(0, payload, cancelled.Token));
        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await replica.ReadAsync(0, cancelled.Token));
        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await replica.SealAsync(cancelled.Token));
        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await nodes.Node(NodeA).ListReplicasAsync(cancelled.Token));
        await replica.DisposeAsync();
        _ = await Assert.ThrowsAsync<ObjectDisposedException>(async () => await replica.WriteAsync(0, payload, token));
        Assert.Empty(await ListAsync(nodes, NodeA));

        nodes.Dispose();
        _ = await Assert.ThrowsAsync<ObjectDisposedException>(async () => await nodes.Node(NodeA).ListReplicasAsync(token));
    }

    /// <summary>
    /// LR-I3: a drop removes the replica with every value and its seal and leaves the ledger's tombstone in one step;
    /// every facade, one opened after the drop included, then refuses every operation, a write delayed past the drop
    /// creates nothing, and a second drop changes nothing.
    /// </summary>
    [Fact]
    public async Task ADropLeavesATombstoneAndEveryFacadeThenRefuses()
    {
        var token = TestContext.Current.CancellationToken;
        using var nodes = new InMemoryLedgerReplicaTransport();
        using var payload = Payload(Value(Ledger));
        var writer = nodes.Open(NodeA, Ledger, Options);
        var other = nodes.Open(NodeA, Ledger, new LedgerReplicaOptions { WindowSize = 64 });
        Assert.Equal(ReplicaAnswers.Written, await writer.WriteAsync(0, payload, token));
        _ = await writer.ReadTailAsync(new Slot(1), seal: true, token);

        await nodes.Node(NodeA).DropReplicaAsync(Ledger, token);
        await nodes.Node(NodeA).DropReplicaAsync(Ledger, token);
        Assert.Empty(await ListAsync(nodes, NodeA));
        foreach (var facade in new[] { writer, other, nodes.Open(NodeA, Ledger, Options) })
        {
            await AssertRegisterOperationsAnswerDroppedAsync(facade, payload);
        }

        Assert.Empty(await ListAsync(nodes, NodeA));
    }

    /// <summary>
    /// LR-I6: a replica records the window of the replica whose operation created it, and refuses every operation of a
    /// handle with another window before it has any effect: it neither writes, seals, nor retains a contiguous tail. A
    /// replica which does not exist has no window yet, so such a replica reads it as empty, and its first mutation decides
    /// the window.
    /// </summary>
    [Fact]
    public async Task AReplicaRefusesEveryOperationOfAnotherWindowWithoutEffect()
    {
        var token = TestContext.Current.CancellationToken;
        using var nodes = new InMemoryLedgerReplicaTransport();
        using var payload = Payload(Value(Ledger));
        var wider = new LedgerReplicaOptions { WindowSize = Options.WindowSize * 2 };
        var wide = nodes.Open(NodeA, Ledger, wider);
        var creator = nodes.Open(NodeA, Ledger, Options);
        Assert.Null(await LedgerReplicaTestData.ReadBytesAsync(wide, 0, token));
        Assert.Equal(ReplicaAnswers.Written, await creator.WriteAsync(Slot.Zero, payload, new Slot(1), recovery: false, token));

        var tail = new Slot(9);
        _ = await Assert.ThrowsAsync<ArgumentException>(async () => await wide.WriteAsync(new Slot(1), payload, tail, recovery: false, token));
        _ = await Assert.ThrowsAsync<ArgumentException>(async () => await wide.WriteAsync(new Slot(1), payload, tail, recovery: true, token));
        _ = await Assert.ThrowsAsync<ArgumentException>(async () => await wide.ReadAsync(Slot.Zero, tail, token));
        _ = await Assert.ThrowsAsync<ArgumentException>(async () => await wide.ReadTailAsync(tail, seal: false, token));
        _ = await Assert.ThrowsAsync<ArgumentException>(async () => await wide.ReadTailAsync(tail, seal: true, token));

        Assert.Null(await LedgerReplicaTestData.ReadBytesAsync(creator, 1, token));
        Assert.Equal(new LedgerReplicaTail(new Slot(1), IsSealed: false), await creator.ReadTailAsync(token));

        // Whichever operation creates the replica decides its window: here the wider request's seal.
        var other = new LedgerId(Ledger.Value + 1);
        Assert.True((await nodes.Open(NodeA, other, wider).SealAsync(token)).Tail.IsSealed);
        _ = await Assert.ThrowsAsync<ArgumentException>(async () => await nodes.Open(NodeA, other, Options).RecoveryWriteAsync(0, payload, token));
        Assert.Null(await LedgerReplicaTestData.ReadBytesAsync(nodes.Open(NodeA, other, wider), 0, token));
    }

    /// <summary>
    /// ledger-replicas.md §2.3: "A ledger the node does not hold gets a tombstone too": every operation on it then refuses, and
    /// nothing creates its replica.
    /// </summary>
    [Fact]
    public async Task ADropOfALedgerTheNodeDoesNotHoldLeavesATombstone()
    {
        var token = TestContext.Current.CancellationToken;
        using var nodes = new InMemoryLedgerReplicaTransport();
        using var payload = Payload(Value(Ledger));

        await nodes.Node(NodeB).DropReplicaAsync(Ledger, token);

        await AssertRegisterOperationsAnswerDroppedAsync(nodes.Open(NodeB, Ledger, Options), payload);
        Assert.Empty(await ListAsync(nodes, NodeB));

        // The tombstone is the node's own: another node creates the ledger's replica with its first write.
        await WriteValueAsync(nodes, NodeA, Ledger);
        Assert.Equal([Ledger], await ListAsync(nodes, NodeA));
    }

    /// <summary>
    /// A drop removes exactly its node's replica of its ledger: the node's other replicas and other nodes' replicas of
    /// the ledger keep serving, and a lower ledger's first write succeeds on the node which dropped a higher one.
    /// </summary>
    [Fact]
    public async Task ADropRemovesExactlyOneReplica()
    {
        var token = TestContext.Current.CancellationToken;
        using var nodes = new InMemoryLedgerReplicaTransport();
        LedgerId[] ledgers = [new(3), new(5), Ledger, new(9)];
        foreach (var ledger in ledgers)
        {
            await WriteValueAsync(nodes, NodeA, ledger);
        }

        await WriteValueAsync(nodes, NodeB, Ledger);
        await nodes.Node(NodeA).DropReplicaAsync(Ledger, token);

        Assert.Equal([new LedgerId(3), new LedgerId(5), new LedgerId(9)], await ListAsync(nodes, NodeA));
        foreach (var ledger in ledgers.Where(static ledger => ledger != Ledger))
        {
            var replica = nodes.Open(NodeA, ledger, Options);
            Assert.Equal(Value(ledger), await LedgerReplicaTestData.ReadBytesAsync(replica, 0, token));
        }

        Assert.Equal([Ledger], await ListAsync(nodes, NodeB));
        Assert.Equal(Value(Ledger), await LedgerReplicaTestData.ReadBytesAsync(nodes.Open(NodeB, Ledger, Options), 0, token));

        await WriteValueAsync(nodes, NodeA, new(2));
        Assert.Equal([new LedgerId(2), new LedgerId(3), new LedgerId(5), new LedgerId(9)], await ListAsync(nodes, NodeA));
    }

    /// <summary>
    /// LR-I7, LR-I3, LR-I9: for random sequences of writes, reads, and drops over three nodes, every outcome and every
    /// node's replicas match a model in which each node answers from its own replicas and tombstones, creates a replica
    /// with its first write unless it dropped the ledger, and keeps every tombstone.
    /// </summary>
    [Fact]
    public Task RandomWritesReadsAndDropsMatchAModelOfEachNode() =>
        Gen.Select(Gen.Int[0, 2], Gen.Int[0, 2], Gen.Int[1, 6]).Array[1, 40]
            .SampleAsync(
                operations => _leaks.InScopeAsync(() => CheckAgainstModelAsync(operations)),
                iter: 500);

    /// <summary>
    /// A ledger over node replicas writes, reads, and recovers; once the ledger leaves the map, the nodes which drop it
    /// refuse, a node which has not yet dropped it keeps its replica, and the quorum counts their dropped answers as
    /// sealed ones: a writer write reports sealed, while a read which reaches no quorum and a recovery report the drop
    /// (LP-I5).
    /// </summary>
    [Fact]
    public async Task AQuorumOverTransportReplicasWritesReadsSealsAndRefusesOnceDropped()
    {
        var token = TestContext.Current.CancellationToken;
        using var nodes = new InMemoryLedgerReplicaTransport();
        NodeId[] replicaNodes = [NodeA, NodeB, NodeC];
        await WriteValueAsync(nodes, NodeA, new(Ledger.Value - 1));
        await WriteValueAsync(nodes, NodeA, new(Ledger.Value + 1));

        await using var replica = LedgerTestData.Create([.. replicaNodes.Select(node => nodes.Open(node, Ledger, Options))], quorum: 2, Options);
        for (var address = 0L; address < 3; address++)
        {
            using var payload = Payload(Value((ulong)address + 1));
            Assert.Equal(
                ReplicaWriteCase.Written,
                (await replica.WriteAsync(new Slot(address), payload, new Slot(address), token)).Case);
        }

        Assert.Equal(Value(2), await LedgerTestData.ReadBytesAsync(replica, 1, token));
        var tail = new Slot(3);
        Assert.Equal(new LedgerTail(tail, IsSealed: false), (await replica.ReadTailAsync(tail, token)).Tail);
        Assert.Equal(tail, (await replica.RecoverAsync(token)).End);
        Assert.Equal(new LedgerTail(tail, IsSealed: true), (await replica.ReadTailAsync(token)).Tail);
        foreach (var node in replicaNodes)
        {
            Assert.Contains(Ledger, await ListAsync(nodes, node));
        }

        // Node C's reconciliation has not yet run.
        await nodes.Node(NodeA).DropReplicaAsync(Ledger, token);
        await nodes.Node(NodeB).DropReplicaAsync(Ledger, token);
        Assert.Equal([Ledger], await ListAsync(nodes, NodeC));
        using (var late = Payload(Value(4)))
        {
            Assert.Equal(ReplicaWriteCase.Sealed, (await replica.WriteAsync(4, late, token)).Case);
        }

        DroppedAnswers.AssertDropped(await replica.ReadAsync(4, token));
        DroppedAnswers.AssertDropped(await replica.RecoverAsync(tail, token));

        Assert.Equal([new LedgerId(Ledger.Value - 1), new LedgerId(Ledger.Value + 1)], await ListAsync(nodes, NodeA));
    }

    private static async Task CheckAgainstModelAsync((int Kind, int Node, int Ledger)[] operations)
    {
        var token = TestContext.Current.CancellationToken;
        NodeId[] modelNodes = [NodeA, NodeB, NodeC];
        var live = modelNodes.Select(static _ => new SortedSet<LedgerId>()).ToArray();
        var dropped = new HashSet<(int Node, LedgerId Ledger)>();
        using var nodes = new InMemoryLedgerReplicaTransport();
        using var payload = Payload(Value(1));
        foreach (var (kind, node, ledgerValue) in operations)
        {
            var ledger = new LedgerId((ulong)ledgerValue);
            var replica = nodes.Open(modelNodes[node], ledger, Options);
            var isDropped = dropped.Contains((node, ledger));
            switch (kind)
            {
                case 0:
                    await nodes.Node(modelNodes[node]).DropReplicaAsync(ledger, token);
                    _ = dropped.Add((node, ledger));
                    _ = live[node].Remove(ledger);
                    break;
                case 1 when !isDropped:
                    Assert.Equal(ReplicaAnswers.Written, await replica.WriteAsync(0, payload, token));
                    _ = live[node].Add(ledger);
                    break;
                case 2 when !isDropped:
                    Assert.Equal(
                        live[node].Contains(ledger) ? Value(1) : null,
                        await LedgerReplicaTestData.ReadBytesAsync(replica, 0, token));
                    break;
                default:
                    DroppedAnswers.AssertDropped(await replica.WriteAsync(0, payload, token));
                    DroppedAnswers.AssertDropped(await replica.ReadAsync(0, token));
                    break;
            }

            for (var index = 0; index < modelNodes.Length; index++)
            {
                Assert.Equal(live[index], await ListAsync(nodes, modelNodes[index]));
            }
        }
    }

    private static async Task<IReadOnlyList<LedgerId>> ListAsync(InMemoryLedgerReplicaTransport nodes, NodeId node) =>
        await nodes.Node(node).ListReplicasAsync(TestContext.Current.CancellationToken);

    private static async Task WriteValueAsync(InMemoryLedgerReplicaTransport nodes, NodeId node, LedgerId ledger)
    {
        using var payload = Payload(Value(ledger));
        Assert.Equal(ReplicaAnswers.Written, await nodes.Open(node, ledger, Options).WriteAsync(0, payload, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Asserts that every register operation of <paramref name="replica"/> answers <c>Dropped</c> (LR-I3).
    /// </summary>
    private static async Task AssertRegisterOperationsAnswerDroppedAsync(ILedgerReplica replica, ArcBuffer payload)
    {
        var token = TestContext.Current.CancellationToken;
        DroppedAnswers.AssertDropped(await replica.WriteAsync(0, payload, token));
        DroppedAnswers.AssertDropped(await replica.ReadAsync(0, token));
        DroppedAnswers.AssertDropped(await replica.ReadAsync(Slot.Zero, 0, 1, token));
        DroppedAnswers.AssertDropped(await replica.RecoveryWriteAsync(0, payload, token));
        DroppedAnswers.AssertDropped(await replica.ReadTailAsync(token));
        DroppedAnswers.AssertDropped(await replica.SealAsync(token));
    }

    private static byte[] Value(LedgerId ledger) => Value(ledger.Value);

    private static byte[] Value(ulong seed) => LedgerReplicaTestData.CreatePayload(4, seed: (int)seed);

    private static ArcBuffer Payload(byte[] value)
    {
        using var writer = new ArcBufferWriter();
        writer.Write(value);
        return writer.ConsumeSlice(writer.Length);
    }
}
