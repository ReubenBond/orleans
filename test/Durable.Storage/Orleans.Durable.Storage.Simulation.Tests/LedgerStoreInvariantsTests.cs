using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Simulation.Tests;

/// <summary>
/// Each check of <see cref="LedgerStoreInvariants"/> fires when the catalog it projects, or a node's ledger, shows what
/// its invariant forbids.
/// </summary>
public sealed class LedgerStoreInvariantsTests
{
    private const string SealPersists = "a replica which answered sealed answers sealed until its drop, across restarts (LS-I11, LR-I8)";
    private static readonly LedgerReplicaOptions Options = new() { WindowSize = 64 };
    private static readonly LedgerId Other = new(2);

    /// <summary>
    /// A catalog without a replica whose creation its store acknowledged violates LS-I1.
    /// </summary>
    [Fact]
    public void ACatalogWhichLosesAnAcknowledgedReplicaFails() =>
        AssertFires("every acknowledged replica stays listed until its drop (LS-I1)", static async (node, replicas, invariants) =>
        {
            invariants.CatalogFilter = static catalog => catalog with { Replicas = [] };
            await WriteAsync(replicas, node, Other);
        });

    /// <summary>
    /// A catalog which lists a replica nobody requested violates LS-I1.
    /// </summary>
    [Fact]
    public void ACatalogWhichListsAnUnrequestedReplicaFails() =>
        AssertFires("every listed replica was requested and not dropped (LS-I1)", static async (node, replicas, invariants) =>
        {
            invariants.CatalogFilter = static catalog => catalog with { Replicas = [.. catalog.Replicas, new LedgerId(9)] };
            await WriteAsync(replicas, node, Other);
        });

    /// <summary>
    /// A catalog which lists a replica whose drop its store acknowledged violates LS-I1.
    /// </summary>
    [Fact]
    public void ACatalogWhichListsADroppedReplicaFails() =>
        AssertFires("every listed replica was requested and not dropped (LS-I1)", static async (node, replicas, invariants) =>
        {
            await SimulatedStorage.DropEverywhereAsync(replicas, SimulatedStorage.Ledger);
            invariants.CatalogFilter = static catalog => catalog with { Replicas = [SimulatedStorage.Ledger, .. catalog.Replicas] };
            await WriteAsync(replicas, node, Other);
        });

    /// <summary>
    /// A catalog without the tombstone of a drop its store acknowledged violates LS-I2, though no catalog showed the
    /// tombstone before.
    /// </summary>
    [Fact]
    public void ACatalogWithoutTheTombstoneOfAnAcknowledgedDropFails() =>
        AssertFires("every acknowledged drop leaves a tombstone (LS-I2)", static async (_, replicas, invariants) =>
        {
            invariants.CatalogFilter = static catalog => catalog with { Tombstones = [] };
            await SimulatedStorage.DropEverywhereAsync(replicas, SimulatedStorage.Ledger);
        });

    /// <summary>
    /// A catalog which loses a tombstone an earlier catalog showed violates LS-I2, though no client dropped its ledger.
    /// </summary>
    [Fact]
    public void ACatalogWhichLosesATombstoneFails() =>
        AssertFires("no tombstone is ever lost (LS-I2)", static async (node, replicas, invariants) =>
        {
            invariants.CatalogFilter = static catalog => catalog with { Tombstones = [.. catalog.Tombstones, new LedgerId(9)] };
            await WriteAsync(replicas, node, Other);
            invariants.CatalogFilter = null;
            await WriteAsync(replicas, node, Other);
        });

    /// <summary>
    /// A catalog whose next handle falls below an earlier one violates LS-I1.
    /// </summary>
    [Fact]
    public void ANextHandleWhichDecreasesFails() =>
        AssertFires("the next handle never decreases (LS-I1)", static async (node, replicas, invariants) =>
        {
            invariants.CatalogFilter = static catalog => catalog with { NextHandle = 0 };
            await WriteAsync(replicas, node, Other);
        });

    /// <summary>
    /// A catalog whose node identity changes violates LS-I6.
    /// </summary>
    [Fact]
    public void ANodeIdentityWhichChangesFails() =>
        AssertFires("the node identity survives restarts (LS-I6)", static async (node, replicas, invariants) =>
        {
            invariants.CatalogFilter = static catalog => catalog with { NodeId = new NodeId(Guid.Parse("0d6c3f0e-2c3a-4f39-9a51-0b1c2d3e4f50")) };
            await WriteAsync(replicas, node, Other);
        });

    /// <summary>
    /// An acknowledged entry which a restarted store reads as missing violates LS-I5 and LR-I2.
    /// </summary>
    [Fact]
    public void AnAcknowledgedEntryWhichARestartLosesFails() =>
        AssertFires("every acknowledged entry survives restarts, as itself or corrupt (LS-I5, LR-I2)", static async (node, replicas, invariants) =>
        {
            _ = replicas.Open(node.NodeId, SimulatedStorage.Ledger, Options);
            replicas.Audit(node).Acknowledge(SimulatedStorage.Ledger, new Slot(5));
            await node.CrashAsync(operatingSystem: false);
            await node.StartAsync();
        });

    /// <summary>
    /// A replica which answers a writer write written after it answered sealed violates LS-I11 and LR-I8.
    /// </summary>
    [Fact]
    public void AWrittenAnswerAfterASealedOneFails() =>
        AssertFires(SealPersists, static async (node, replicas, _) =>
        {
            replicas.Audit(node).AcknowledgeSealAnswer(SimulatedStorage.Ledger, isSealed: true, "seal");
            await WriteAsync(replicas, node, SimulatedStorage.Ledger);
        });

    /// <summary>
    /// A replica which answered sealed and which a restart recovers unsealed violates LS-I11, though it answers nothing
    /// unsealed before the restart.
    /// </summary>
    [Fact]
    public void ASealWhichARestartLosesFails() =>
        AssertFires(SealPersists, static async (node, replicas, _) =>
        {
            replicas.Audit(node).AcknowledgeSealAnswer(SimulatedStorage.Ledger, isSealed: true, "seal");
            await node.CrashAsync(operatingSystem: false);
            await node.StartAsync();
        });

    /// <summary>
    /// Writes the next address of <paramref name="ledger"/> on <paramref name="node"/>, whose first write creates its
    /// replica, so the node publishes its store.
    /// </summary>
    private static async Task WriteAsync(SimulatedLedgerReplicaTransport replicas, LedgerStoreNode node, LedgerId ledger)
    {
        await using var replica = (SimulatedLedgerReplica)replicas.Open(node.NodeId, ledger, Options);
        var address = replicas.Audit(node).AcknowledgedEntries().Count(entry => entry.Ledger == ledger);
        using var payload = ArcBuffer.Wrap(SimulatedStorage.Payload(address));
        _ = await replica.WriteAsync(new Slot(address), payload, Slot.Zero, recovery: false);
    }

    private static void AssertFires(string invariant, Func<LedgerStoreNode, SimulatedLedgerReplicaTransport, LedgerStoreInvariants, Task> breakIt)
    {
        var failure = Assert.Throws<SimulationFailedException>(() => SeededSimulation.Run(7, async environment =>
        {
            var client = environment.Cluster.AddNode("client");
            var node = (await LedgerStoreNode.StartNodesAsync(environment, SimulatedStorage.Cluster, 1))[0];
            var replicas = new SimulatedLedgerReplicaTransport(environment, client, [node], TimeSpan.FromMilliseconds(40));
            var invariants = LedgerStoreInvariants.Register(environment, replicas);
            await SimulatedStorage.WriteEverywhereAsync(replicas, SimulatedStorage.Ledger, Options);
            await breakIt(node, replicas, invariants);
        }));

        var violation = Assert.IsType<InvariantViolationException>(failure.InnerException);
        Assert.Equal("replica-0: " + invariant, violation.Violation!.Invariant);
    }
}
