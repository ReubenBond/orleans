using System.Net;
using CsCheck;
using Orleans.Durable.Storage.Control;
using Orleans.Durable.Storage.Engine.Ledgers.Objects;
using Orleans.Durable.Storage.Testing;
using Orleans.Runtime;
using static Orleans.Durable.Storage.Tests.NodeRegistrySamples;

namespace Orleans.Durable.Storage.Tests;

/// <summary>
/// The committer's write check, <see cref="NodeRegistryRules.Violation"/>, against the one OR-I2 step oracle,
/// <see cref="NodeRegistryHistoryOracle.Step"/> (orleans.md §5.1, §5.3): they agree on generated steps of registrations and
/// retirements, the folds the committer makes and mutations of them, and each clause has an example the check refuses; a
/// retirement retires an active entry and is answered from a retired one; a retired node's registration keeps it retired;
/// and every registration keeps room for every retirement and every restart.
/// </summary>
[NoPageLeakCheck("The rules and the codec rent no pages.")]
public sealed class NodeRegistryRulesTests
{
    private static readonly ObjectLedgerStoreId ObjectStoreA = new(new UInt128(0, 0xAA01));
    private static readonly ObjectLedgerStoreId ObjectStoreB = new(new UInt128(0, 0xBB02));

    private enum Mutation
    {
        None,
        SkipRevision,
        ChangeStore,
        ChangeObjectLedgerStore,
        FlipDiskless,
        RemoveEntry,
        LowerGeneration,
        RetireWithoutSuccession,
        RetireUnnamed,
        RetireAtAnotherRevision,
        UnretireEntry,
        MoveRetirement,
        JoinRetiredEarlier,
        ShareActiveOrdinal,
    }

    [Fact]
    public void TheWriteCheckAgreesWithTheStepOracle()
    {
        var objectStores = Gen.Frequency((2, Gen.Const(default(ObjectLedgerStoreId?))), (4, Gen.Const<ObjectLedgerStoreId?>(ObjectStoreA)), (1, Gen.Const<ObjectLedgerStoreId?>(ObjectStoreB)));
        var registrations = Gen.Select(Gen.Int[1, 4], Gen.Int[1, 4], Gen.Int[1, 4], Gen.Int[-1, 1], Gen.Int[0, 4], Gen.Bool, objectStores)
            .Select(static (node, generation, host, ordinal, nodeToReplace, diskless, objectStore) =>
                (NodeRegistryChange)new NodeRegistrationRequest(
                    new NodeRegistryEntry(Node(node), Silo(host, generation), diskless ? null : ordinal < 0 ? null : ordinal, failureDomain: null, retiredRevision: 0, diskless: diskless),
                    StoreA,
                    nodeToReplace == 0 ? null : Node(nodeToReplace),
                    objectStore));
        var retirements = Gen.Int[1, 5].Select(static node => (NodeRegistryChange)new NodeRetirementRequest(Node(node)));
        var requests = Gen.Frequency((4, registrations), (1, retirements)).Array[0, 8];
        Gen.Select(requests, requests, Gen.Enum<Mutation>(), Gen.Int[0, 100]).Sample(
            static sample =>
            {
                var (history, step, mutation, choice) = sample;
                var before = NodeRegistryRules.Fold(NodeRegistryValue.Empty, history, int.MaxValue).Written ?? NodeRegistryValue.Empty;
                var (written, verdicts, _) = NodeRegistryRules.Fold(before, step, int.MaxValue);
                if (written is not { } after)
                {
                    return;
                }

                NodeRegistryChange[] carried = [.. step.Where((_, index) => verdicts[index] == NodeRegistryVerdict.Applies)];
                var mutated = Mutate(before, after, carried, mutation, choice);
                var oracle = NodeRegistryHistoryOracle.Step(
                    before,
                    mutated,
                    [.. carried.Select(static change => change.Value).OfType<NodeRegistrationRequest>().Select(static request => (request.Entry, request.NodeToReplace))],
                    [.. carried.Select(static change => change.Value).OfType<NodeRetirementRequest>().Select(static retirement => retirement.Node)]);
                var check = NodeRegistryRules.Violation(before, mutated, carried, int.MaxValue);
                Assert.True(oracle is null == check is null, $"{mutation}: the oracle says '{oracle ?? "valid"}', the check '{check ?? "valid"}'.");
                if (mutation == Mutation.None)
                {
                    Assert.Null(check);
                }
            },
            iter: 2000);
    }

    [Fact]
    public void EachClauseOfTheWriteCheckRefusesItsBreach()
    {
        var before = new NodeRegistryValue(2, [Entry(1, 2, ordinal: 0), Entry(2, 1, ordinal: 1), Entry(3, 1).Retire(1)], StoreA);
        NodeRegistryValue After(params NodeRegistryEntry[] nodes) => new(3, [.. nodes], StoreA);
        string? Check(NodeRegistryValue after, params NodeRegistryChange[] carried) => NodeRegistryRules.Violation(before, after, carried, int.MaxValue);

        Assert.Null(Check(After(Entry(1, 3, ordinal: 0), Entry(2, 1, ordinal: 1), Entry(3, 1).Retire(1))));
        Assert.NotNull(Check(new NodeRegistryValue(4, before.Nodes, StoreA)));
        Assert.NotNull(Check(new NodeRegistryValue(3, before.Nodes, StoreB)));
        Assert.NotNull(Check(After(Entry(1, 2, ordinal: 0), Entry(2, 1, ordinal: 1))));
        Assert.NotNull(Check(After(Entry(1, 1, ordinal: 0), Entry(2, 1, ordinal: 1), Entry(3, 1).Retire(1))));
        Assert.NotNull(Check(After(Entry(1, 2, ordinal: 0), Entry(2, 1, ordinal: 1), Entry(3, 2))));

        // A retired entry advances only to a greater generation, retired at its own revision.
        Assert.Null(Check(After(Entry(1, 2, ordinal: 0), Entry(2, 1, ordinal: 1), Entry(3, 2).Retire(1))));
        Assert.NotNull(Check(After(Entry(1, 2, ordinal: 0), Entry(2, 1, ordinal: 1), Entry(3, 2).Retire(3))));
        Assert.NotNull(Check(After(Entry(1, 2, ordinal: 0), Entry(2, 1, ordinal: 1), Entry(3, 1).Retire(3)), new NodeRetirementRequest(Node(3))));

        // The value's reservation fits the register's limit.
        var fits = After(Entry(1, 3, ordinal: 0), Entry(2, 1, ordinal: 1), Entry(3, 1).Retire(1));
        Assert.Null(NodeRegistryRules.Violation(before, fits, [], NodeRegistryRules.ReservedLength(fits)));
        Assert.NotNull(NodeRegistryRules.Violation(before, fits, [], NodeRegistryRules.ReservedLength(fits) - 1));
        Assert.NotNull(Check(After(Entry(1, 2, ordinal: 0).Retire(3), Entry(2, 1, ordinal: 1), Entry(3, 1).Retire(1))));
        Assert.NotNull(Check(After(Entry(1, 2, ordinal: 0), Entry(2, 1, ordinal: 1), Entry(3, 1).Retire(1), Entry(4, 1).Retire(2))));
        Assert.NotNull(Check(After(Entry(1, 2, ordinal: 0), Entry(2, 1, ordinal: 1), Entry(3, 1).Retire(1), Entry(4, 1, ordinal: 0))));

        // An ordinal succession: node 4 registers at node 1's ordinal and retires it, only when its registration names node
        // 1 and node 4 holds the ordinal.
        var succession = After(Entry(1, 2, ordinal: 0).Retire(3), Entry(2, 1, ordinal: 1), Entry(3, 1).Retire(1), Entry(4, 1, ordinal: 0));
        Assert.Null(Check(succession, new NodeRegistrationRequest(Entry(4, 1, ordinal: 0), StoreA, Node(1))));
        Assert.NotNull(Check(succession, new NodeRegistrationRequest(Entry(4, 1, ordinal: 0), StoreA, null)));
        Assert.NotNull(Check(succession, new NodeRegistrationRequest(Entry(4, 1, ordinal: 0), StoreA, Node(2))));
        Assert.NotNull(Check(succession, new NodeRegistrationRequest(Entry(2, 2, ordinal: 1), StoreA, Node(1))));

        // An operator's retirement: node 2 retired at this write's revision only when a carried retirement names it.
        var retirement = After(Entry(1, 2, ordinal: 0), Entry(2, 1, ordinal: 1).Retire(3), Entry(3, 1).Retire(1));
        Assert.Null(Check(retirement, new NodeRetirementRequest(Node(2))));
        Assert.NotNull(Check(retirement));
        Assert.NotNull(Check(retirement, new NodeRetirementRequest(Node(1))));
    }

    /// <summary>
    /// orleans.md §5.3: a retirement retires an active entry at the write's revision, is answered from an entry already
    /// retired, and finds no entry of a node the value does not hold; a registration of the node after it in the same batch
    /// advances its entry, retired at the write's revision, and its ordinal is free.
    /// </summary>
    [Fact]
    public void ARetirementRetiresAnActiveEntryAtItsWritesRevision()
    {
        var before = new NodeRegistryValue(2, [Entry(1, 1, ordinal: 0), Entry(2, 1, ordinal: 1).Retire(2)], StoreA);
        Assert.Equal(NodeRegistryVerdict.Applies, NodeRegistryRules.Decide(before, new NodeRetirementRequest(Node(1)), int.MaxValue));
        Assert.Equal(NodeRegistryVerdict.GoalPresent, NodeRegistryRules.Decide(before, new NodeRetirementRequest(Node(2)), int.MaxValue));
        Assert.Equal(NodeRegistryVerdict.NotRegistered, NodeRegistryRules.Decide(before, new NodeRetirementRequest(Node(3)), int.MaxValue));
        NodeRegistryChange[] batch =
        [
            new NodeRetirementRequest(Node(1)),
            new NodeRegistrationRequest(Entry(1, 2, ordinal: 0), StoreA, null),
            new NodeRegistrationRequest(Entry(4, 1, ordinal: 0), StoreA, null),
        ];

        var (written, verdicts, successions) = NodeRegistryRules.Fold(before, batch, int.MaxValue);

        Assert.Equal([NodeRegistryVerdict.Applies, NodeRegistryVerdict.Applies, NodeRegistryVerdict.Applies], verdicts);
        Assert.True(new NodeRegistryValue(3, [Entry(1, 2, ordinal: 0).Retire(3), Entry(2, 1, ordinal: 1).Retire(2), Entry(4, 1, ordinal: 0)], StoreA).ContentEquals(written!));
        Assert.Empty(successions);
        Assert.Null(NodeRegistryRules.Violation(before, written!, batch, int.MaxValue));
    }

    /// <summary>
    /// orleans.md §5.3: a retired node's silo which restarts registers a greater generation at another address, and its
    /// entry stays retired at its revision, so the silo serves its replicas until its gate opens and is never active again.
    /// Its registration holds no ordinal: another node active at its ordinal neither refuses it nor is retired by it,
    /// whatever it names. The same silo again is the goal, and an older one stale.
    /// </summary>
    [Fact]
    public void ARetiredNodesRegistrationAdvancesItsEntryAndKeepsItRetired()
    {
        var before = new NodeRegistryValue(3, [Entry(1, 1, ordinal: 0).Retire(2), Entry(2, 1, ordinal: 0)], StoreA);
        var restart = new NodeRegistrationRequest(Entry(1, 2, ordinal: 0, host: 7), StoreA, Node(2));

        var (written, verdicts, successions) = NodeRegistryRules.Fold(before, [restart], int.MaxValue);

        Assert.Equal([NodeRegistryVerdict.Applies], verdicts);
        Assert.Empty(successions);
        Assert.True(new NodeRegistryValue(4, [Entry(1, 2, ordinal: 0, host: 7).Retire(2), Entry(2, 1, ordinal: 0)], StoreA).ContentEquals(written!));
        Assert.Null(NodeRegistryRules.Violation(before, written!, [restart], int.MaxValue));
        Assert.Equal(NodeRegistryVerdict.GoalPresent, NodeRegistryRules.Decide(written!, restart, int.MaxValue));
        Assert.Equal(NodeRegistryVerdict.Stale, NodeRegistryRules.Decide(written!, new NodeRegistrationRequest(Entry(1, 1, ordinal: 0, host: 8), StoreA, null), int.MaxValue));
    }

    /// <summary>
    /// orleans.md §5.3: a registration applies only while the value it leaves fits the value limit with every entry at its
    /// longest, so every retirement of a written value fits it too, and so does every restart of its nodes, however long
    /// its silo's generation and endpoint: an active node's at any ordinal and with any failure domain the format holds,
    /// and a retired node's, which keeps its ordinal and failure domain however long the domain it asks for; only a new
    /// identity is refused.
    /// </summary>
    [Fact]
    public void ARegistrationKeepsRoomForEveryRetirement()
    {
        var before = new NodeRegistryValue(1, [Entry(1, 1, ordinal: 0)], StoreA);
        var request = new NodeRegistrationRequest(Entry(2, 1, ordinal: 1), StoreA, null);
        var reserved = NodeRegistryRules.ReservedLength(new NodeRegistryValue(2, [Entry(1, 1, ordinal: 0), Entry(2, 1, ordinal: 1)], StoreA));

        Assert.Equal(NodeRegistryVerdict.TooLarge, NodeRegistryRules.Decide(before, request, reserved - 1));
        var (written, verdicts, _) = NodeRegistryRules.Fold(before, [request], reserved);
        Assert.Equal([NodeRegistryVerdict.Applies], verdicts);

        var (retired, _, _) = NodeRegistryRules.Fold(written!, [new NodeRetirementRequest(Node(1)), new NodeRetirementRequest(Node(2))], reserved);
        Assert.True(NodeRegistryCodec.Encode(retired!).Length <= reserved);
        Assert.True(NodeRegistryCodec.Encode(written!).Length < reserved);

        var longest = SiloAddress.New(new IPEndPoint(IPAddress.Parse("ffff:ffff:ffff:ffff:ffff:ffff:ffff:ffff"), IPEndPoint.MaxPort), int.MaxValue);
        var domain = new string('z', NodeRegistryEntry.MaxFailureDomainBytes);
        NodeRegistryChange[] activeRestarts = [new NodeRegistrationRequest(new NodeRegistryEntry(Node(1), longest, int.MaxValue, domain), StoreA, null), new NodeRegistrationRequest(new NodeRegistryEntry(Node(2), longest, int.MaxValue - 1, domain), StoreA, null)];
        var (grown, grownVerdicts, _) = NodeRegistryRules.Fold(written!, activeRestarts, reserved);
        Assert.Equal([NodeRegistryVerdict.Applies, NodeRegistryVerdict.Applies], grownVerdicts);
        Assert.True(NodeRegistryCodec.Encode(grown!).Length <= reserved);

        NodeRegistryChange[] restarts = [new NodeRegistrationRequest(new NodeRegistryEntry(Node(1), longest, 5, domain), StoreA, null), new NodeRegistrationRequest(new NodeRegistryEntry(Node(2), longest, 6, domain), StoreA, null)];
        var (restarted, restartVerdicts, _) = NodeRegistryRules.Fold(retired!, restarts, NodeRegistryRules.ReservedLength(retired!));
        Assert.Equal([NodeRegistryVerdict.Applies, NodeRegistryVerdict.Applies], restartVerdicts);
        Assert.Equal([(0, null), (1, null)], restarted!.Nodes.Select(static entry => (entry.Ordinal!.Value, entry.FailureDomain)));
        Assert.All(restarted.Nodes, entry => Assert.Equal(longest, entry.Silo));
        Assert.Equal(NodeRegistryVerdict.TooLarge, NodeRegistryRules.Decide(restarted, new NodeRegistrationRequest(Entry(3, 1), StoreA, null), NodeRegistryRules.ReservedLength(restarted)));
    }

    /// <summary>
    /// orleans.md §7.3: a failure domain is at most 64 bytes in UTF-8, so an entry, and a silo's options, refuse a longer
    /// one before any registration reserves it; the limit counts bytes, not characters.
    /// </summary>
    [Fact]
    public void AFailureDomainOverItsLimitIsRefusedAtValidation()
    {
        _ = new NodeRegistryEntry(Node(1), Silo(1, 1), failureDomain: new string('z', 62) + "\u00e9");
        _ = Assert.Throws<ArgumentException>(() => new NodeRegistryEntry(Node(1), Silo(1, 1), failureDomain: new string('z', 65)));
        _ = Assert.Throws<ArgumentException>(() => new NodeRegistryEntry(Node(1), Silo(1, 1), failureDomain: new string('z', 63) + "\u00e9"));
    }

    /// <summary>
    /// An ordinal another node holds active is taken only by a registration which names that node for replacement; a free
    /// or retired ordinal, a registration at no ordinal, and a restart of the ordinal's own node apply whatever they name
    /// (orleans.md §5.3).
    /// </summary>
    [Fact]
    public void AnActiveOrdinalIsTakenOnlyByARegistrationWhichNamesItsNode()
    {
        var value = new NodeRegistryValue(3, [Entry(1, 1, ordinal: 0).Retire(2), Entry(2, 1, ordinal: 0), Entry(3, 1, ordinal: 1)], StoreA);
        NodeRegistryVerdict Decide(NodeRegistryEntry entry, int? nodeToReplace = null) =>
            NodeRegistryRules.Decide(value, new NodeRegistrationRequest(entry, StoreA, nodeToReplace is { } node ? Node(node) : null), int.MaxValue);

        // A free ordinal, with or without a named node.
        Assert.Equal(NodeRegistryVerdict.Applies, Decide(Entry(4, 1, ordinal: 2)));
        Assert.Equal(NodeRegistryVerdict.Applies, Decide(Entry(4, 1, ordinal: 2), nodeToReplace: 2));

        // An active ordinal: refused unless the registration names its node, here node 2.
        Assert.Equal(NodeRegistryVerdict.OrdinalOccupied, Decide(Entry(4, 1, ordinal: 0)));
        Assert.Equal(NodeRegistryVerdict.OrdinalOccupied, Decide(Entry(4, 1, ordinal: 0), nodeToReplace: 1));
        Assert.Equal(NodeRegistryVerdict.OrdinalOccupied, Decide(Entry(4, 1, ordinal: 0), nodeToReplace: 3));
        Assert.Equal(NodeRegistryVerdict.Applies, Decide(Entry(4, 1, ordinal: 0), nodeToReplace: 2));

        // A known node moving onto another node's active ordinal needs the same intent.
        Assert.Equal(NodeRegistryVerdict.OrdinalOccupied, Decide(Entry(3, 2, ordinal: 0)));
        Assert.Equal(NodeRegistryVerdict.Applies, Decide(Entry(3, 2, ordinal: 0), nodeToReplace: 2));

        // An ordinal whose entry is retired is free.
        var retired = new NodeRegistryValue(3, [Entry(1, 1, ordinal: 0).Retire(2)], StoreA);
        Assert.Equal(NodeRegistryVerdict.Applies, NodeRegistryRules.Decide(retired, new NodeRegistrationRequest(Entry(4, 1, ordinal: 0), StoreA, null), int.MaxValue));

        // The ordinal's own node restarting, and a registration at no ordinal, take nothing.
        Assert.Equal(NodeRegistryVerdict.Applies, Decide(Entry(2, 2, ordinal: 0)));
        Assert.Equal(NodeRegistryVerdict.Applies, Decide(Entry(2, 2, ordinal: 0), nodeToReplace: 3));
        Assert.Equal(NodeRegistryVerdict.GoalPresent, Decide(Entry(2, 1, ordinal: 0)));
        Assert.Equal(NodeRegistryVerdict.Applies, Decide(Entry(4, 1), nodeToReplace: 2));

        // A retired node's silo restarts retired, holding no ordinal: it takes none, whatever it names.
        Assert.Equal(NodeRegistryVerdict.Applies, Decide(Entry(1, 5, ordinal: 0), nodeToReplace: 2));
    }

    /// <summary>
    /// A batch which registers node 4 at node 1's ordinal, naming node 1, so retiring it, and then node 4 again at no ordinal
    /// would write a retirement with no successor; the fold defers the second registration, so the write is one OR-I2 step.
    /// </summary>
    [Fact]
    public void TheFoldDefersARegistrationWhichWouldBreakTheStep()
    {
        var before = new NodeRegistryValue(1, [Entry(1, 1, ordinal: 0)], StoreA);
        NodeRegistryChange[] batch = [new NodeRegistrationRequest(Entry(4, 1, ordinal: 0), StoreA, Node(1)), new NodeRegistrationRequest(Entry(4, 2), StoreA, null)];

        var (written, verdicts, successions) = NodeRegistryRules.Fold(before, batch, int.MaxValue);

        Assert.Equal([NodeRegistryVerdict.Applies, NodeRegistryVerdict.Deferred], verdicts);
        Assert.True(new NodeRegistryValue(2, [Entry(1, 1, ordinal: 0).Retire(2), Entry(4, 1, ordinal: 0)], StoreA).ContentEquals(written!));
        Assert.Null(NodeRegistryRules.Violation(before, written!, batch[..1], int.MaxValue));
        Assert.Equal([new NodeRegistrySuccession(Node(4), Node(1), 0)], successions);
    }

    /// <summary>A known node which moves onto another node's ordinal, naming it, retires it, and the fold reports the succession.</summary>
    [Fact]
    public void AKnownNodeWhichNamesTheOccupantOfItsNewOrdinalRetiresIt()
    {
        var before = new NodeRegistryValue(1, [Entry(2, 1, ordinal: 0), Entry(3, 1, ordinal: 1)], StoreA);
        NodeRegistryChange[] batch = [new NodeRegistrationRequest(Entry(3, 2, ordinal: 0), StoreA, Node(2))];

        var (written, verdicts, successions) = NodeRegistryRules.Fold(before, batch, int.MaxValue);

        Assert.Equal([NodeRegistryVerdict.Applies], verdicts);
        Assert.True(new NodeRegistryValue(2, [Entry(2, 1, ordinal: 0).Retire(2), Entry(3, 2, ordinal: 0)], StoreA).ContentEquals(written!));
        Assert.Equal([new NodeRegistrySuccession(Node(3), Node(2), 0)], successions);
    }

    /// <summary>
    /// orleans.md §5.3, §5.14, OL-I1: <see cref="ObjectLedgerStoreId"/> adoption, mismatch refusal, same-silo adoption
    /// skipping ordinal occupancy checks, <see cref="NodeRegistryRules.ReservedLength"/> boundary, diskless retired
    /// restarts, diskless immutability, <c>diskless-ordinal</c> rejection, and <c>default(ObjectLedgerStoreId)</c>
    /// rejection.
    /// </summary>
    [Fact]
    public async Task ObjectLedgerStoreIdAndDisklessRulesHold()
    {
        // Reject default(ObjectLedgerStoreId) in registry values and registration submissions.
        _ = Assert.Throws<ArgumentException>(() => new NodeRegistryValue(1, [Entry(1, 1)], StoreA, default(ObjectLedgerStoreId)));
        await using var committer = new NodeRegistryCommitter(new ScriptedConditionalRegister(), NodeRegistryCodec.MaxValueLength, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        _ = await Assert.ThrowsAsync<ArgumentException>(() => committer.RegisterAsync(Entry(1, 1), StoreA, null, default(ObjectLedgerStoreId), TestContext.Current.CancellationToken));

        // Reject Diskless with an ordinal at validation; valid diskless entries round-trip through CLNR.
        _ = Assert.Throws<ArgumentException>(() => new NodeRegistryEntry(Node(1), Silo(1, 1), ordinal: 0, failureDomain: null, retiredRevision: 0, diskless: true));
        var validDiskless = new NodeRegistryValue(1, [new NodeRegistryEntry(Node(1), Silo(1, 1), ordinal: null, failureDomain: null, retiredRevision: 0, diskless: true)], StoreA, ObjectStoreA);
        var decodedValid = NodeRegistryCodec.Decode(NodeRegistryCodec.Encode(validDiskless));
        Assert.True(Assert.IsType<NodeRegistryValue>(decodedValid.Value).ContentEquals(validDiskless));

        // Same-silo ObjectLedgerStore adoption keeps the entry unchanged and skips FindOccupant even when another node
        // occupies the ordinal requested by the adopting registration.
        var beforeAdopt = new NodeRegistryValue(1, [Entry(1, 1, ordinal: 0), Entry(2, 1, ordinal: 1)], StoreA, objectLedgerStore: null);
        var adopt = new NodeRegistrationRequest(Entry(1, 1, ordinal: 1), StoreA, NodeToReplace: null, ObjectStoreA);
        var (adopted, adoptVerdicts, adoptSuccessions) = NodeRegistryRules.Fold(beforeAdopt, [adopt], int.MaxValue);
        Assert.Equal([NodeRegistryVerdict.Applies], adoptVerdicts);
        Assert.Empty(adoptSuccessions);
        Assert.Equal(ObjectStoreA, adopted!.ObjectLedgerStore);
        Assert.Equal(0, adopted.Find(Node(1))!.Ordinal);
        Assert.False(adopted.Find(Node(2))!.IsRetired);
        Assert.Null(NodeRegistryRules.Violation(beforeAdopt, adopted, [adopt], int.MaxValue));

        // WrongObjectLedgerStore is refused once an ID is recorded; changing it violates OL-I1.
        Assert.Equal(
            NodeRegistryVerdict.WrongObjectLedgerStore,
            NodeRegistryRules.Decide(adopted, new NodeRegistrationRequest(Entry(3, 1), StoreA, null, ObjectStoreB), int.MaxValue));
        var changedStore = new NodeRegistryValue(3, adopted.Nodes, StoreA, ObjectStoreB);
        var violation = NodeRegistryRules.Violation(adopted, changedStore, [], int.MaxValue);
        Assert.NotNull(violation);
        Assert.Contains("OL-I1", violation, StringComparison.Ordinal);

        // Diskless flip (false -> true and true -> false) is refused by Decide and Violation.
        var disklessBefore = new NodeRegistryValue(
            2,
            [Entry(1, 1, ordinal: 0), new NodeRegistryEntry(Node(2), Silo(2, 1), ordinal: null, failureDomain: null, retiredRevision: 0, diskless: true)],
            StoreA,
            ObjectStoreA);
        Assert.Equal(
            NodeRegistryVerdict.Stale,
            NodeRegistryRules.Decide(
                disklessBefore,
                new NodeRegistrationRequest(new NodeRegistryEntry(Node(1), Silo(1, 2), ordinal: null, failureDomain: null, retiredRevision: 0, diskless: true), StoreA, null, ObjectStoreA),
                int.MaxValue));
        Assert.Equal(
            NodeRegistryVerdict.Stale,
            NodeRegistryRules.Decide(
                disklessBefore,
                new NodeRegistrationRequest(new NodeRegistryEntry(Node(2), Silo(2, 2), ordinal: null, failureDomain: null, retiredRevision: 0, diskless: false), StoreA, null, ObjectStoreA),
                int.MaxValue));
        var flipped = new NodeRegistryValue(
            3,
            [Entry(1, 1, ordinal: 0), new NodeRegistryEntry(Node(2), Silo(2, 2), ordinal: null, failureDomain: null, retiredRevision: 0, diskless: false)],
            StoreA,
            ObjectStoreA);
        Assert.NotNull(NodeRegistryRules.Violation(disklessBefore, flipped, [], int.MaxValue));

        // Retired diskless node restarts retired with greater generation, keeping Diskless = true.
        var retiredDiskless = new NodeRegistryValue(
            3,
            [new NodeRegistryEntry(Node(2), Silo(2, 1), ordinal: null, failureDomain: "az1", retiredRevision: 2, diskless: true)],
            StoreA,
            ObjectStoreA);
        var restartDiskless = new NodeRegistrationRequest(
            new NodeRegistryEntry(Node(2), Silo(9, 2), ordinal: null, failureDomain: "az2", retiredRevision: 0, diskless: true),
            StoreA,
            null,
            ObjectStoreA);
        var (restarted, restartVerdicts, _) = NodeRegistryRules.Fold(retiredDiskless, [restartDiskless], int.MaxValue);
        Assert.Equal([NodeRegistryVerdict.Applies], restartVerdicts);
        var restartedEntry = restarted!.Find(Node(2))!;
        Assert.True(restartedEntry.IsRetired);
        Assert.Equal(2UL, restartedEntry.RetiredRevision);
        Assert.True(restartedEntry.Diskless);
        Assert.Equal("az1", restartedEntry.FailureDomain);

        // ReservedLength boundary: a diskless entry reserves room for its retirement, longest silo/domain, and future ObjectLedgerStoreId.
        var beforeReserve = new NodeRegistryValue(1, [Entry(1, 1, ordinal: 0)], StoreA, objectLedgerStore: null);
        var disklessJoin = new NodeRegistrationRequest(
            new NodeRegistryEntry(Node(2), Silo(2, 1), ordinal: null, failureDomain: null, retiredRevision: 0, diskless: true),
            StoreA,
            NodeToReplace: null,
            ObjectLedgerStore: null);
        var withDiskless = new NodeRegistryValue(2, [Entry(1, 1, ordinal: 0), disklessJoin.Entry], StoreA, objectLedgerStore: null);
        var reserved = NodeRegistryRules.ReservedLength(withDiskless);
        Assert.Equal(NodeRegistryVerdict.TooLarge, NodeRegistryRules.Decide(beforeReserve, disklessJoin, reserved - 1));
        Assert.Equal(NodeRegistryVerdict.Applies, NodeRegistryRules.Decide(beforeReserve, disklessJoin, reserved));
        // Adopting ObjectLedgerStoreId afterwards fits within the already-reserved length.
        var adoptLater = new NodeRegistrationRequest(disklessJoin.Entry, StoreA, null, ObjectStoreA);
        Assert.Equal(NodeRegistryVerdict.Applies, NodeRegistryRules.Decide(withDiskless, adoptLater, reserved));
    }

    /// <summary>
    /// orleans.md §5.3, §5.14, §7.3, OR-I2: <see cref="NodeRegistryValue.DrainAllRevision"/> invariants,
    /// <see cref="NodeDrainAllStartRequest"/>, <see cref="NodeDrainAllCancelRequest"/>,
    /// <see cref="NodeDrainAllCompleteRequest"/>, <see cref="NodeRegistryRules.ReservedLength"/> room for
    /// <c>DrainAllRevision</c>, and <see cref="NodeRegistryRules.Violation"/> enforcement.
    /// </summary>
    [Fact]
    public void DrainAllRevisionRulesAndCompleteDrainAllInvariantsHold()
    {
        // Constructor invariants: DrainAllRevision <= Revision and requires ObjectLedgerStore.
        _ = Assert.Throws<ArgumentException>(() => new NodeRegistryValue(2, [Entry(1, 1)], StoreA, ObjectStoreA, drainAllRevision: 3));
        _ = Assert.Throws<ArgumentException>(() => new NodeRegistryValue(2, [Entry(1, 1)], StoreA, objectLedgerStore: null, drainAllRevision: 2));

        // StartDrainAll refuses a registry without ObjectLedgerStore, applies once configured, and is GoalPresent once active.
        var withoutObjectStore = new NodeRegistryValue(1, [Entry(1, 1, ordinal: 0)], StoreA);
        Assert.Equal(NodeRegistryVerdict.ObjectLedgersNotConfigured, NodeRegistryRules.Decide(withoutObjectStore, NodeDrainAllStartRequest.Instance, int.MaxValue));

        var diskless = new NodeRegistryEntry(Node(2), Silo(2, 1), ordinal: null, failureDomain: null, retiredRevision: 0, diskless: true);
        var before = new NodeRegistryValue(2, [Entry(1, 1, ordinal: 0), diskless, Entry(3, 1, ordinal: 1)], StoreA, ObjectStoreA);
        var reserved = NodeRegistryRules.ReservedLength(before);

        // StartDrainAll fits within the ReservedLength reserved at registration time (no TooLarge).
        var (started, startVerdicts, _) = NodeRegistryRules.Fold(before, [NodeDrainAllStartRequest.Instance], reserved);
        Assert.Equal([NodeRegistryVerdict.Applies], startVerdicts);
        Assert.Equal(3UL, started!.Revision);
        Assert.Equal(3UL, started.DrainAllRevision);
        Assert.True(NodeRegistryCodec.Encode(started).Length <= reserved);
        Assert.Null(NodeRegistryRules.Violation(before, started, [NodeDrainAllStartRequest.Instance], reserved));
        Assert.Equal(NodeRegistryVerdict.GoalPresent, NodeRegistryRules.Decide(started, NodeDrainAllStartRequest.Instance, reserved));

        // CompleteDrainAll with mismatched revision is DrainAllSuperseded; with matching revision it atomically retires active diskful
        // nodes only (leaving diskless active) and clears DrainAllRevision = 0 within the reserved budget.
        Assert.Equal(NodeRegistryVerdict.DrainAllSuperseded, NodeRegistryRules.Decide(started, new NodeDrainAllCompleteRequest(2), reserved));
        var complete = new NodeDrainAllCompleteRequest(3);
        var (completed, completeVerdicts, _) = NodeRegistryRules.Fold(started, [complete], reserved);
        Assert.Equal([NodeRegistryVerdict.Applies], completeVerdicts);
        Assert.Equal(4UL, completed!.Revision);
        Assert.Equal(0UL, completed.DrainAllRevision);
        Assert.Equal(4UL, completed.Find(Node(1))!.RetiredRevision);
        Assert.False(completed.Find(Node(2))!.IsRetired);
        Assert.Equal(4UL, completed.Find(Node(3))!.RetiredRevision);
        Assert.True(NodeRegistryCodec.Encode(completed).Length <= reserved);
        Assert.Null(NodeRegistryRules.Violation(started, completed, [complete], reserved));

        // Violation refuses retiring diskful nodes without CompleteDrainAll or retiring a diskless node on CompleteDrainAll.
        Assert.NotNull(NodeRegistryRules.Violation(started, completed, [], reserved));
        var retiredDisklessToo = new NodeRegistryValue(4, [Entry(1, 1, ordinal: 0).Retire(4), diskless.Retire(4), Entry(3, 1, ordinal: 1).Retire(4)], StoreA, ObjectStoreA, drainAllRevision: 0);
        Assert.NotNull(NodeRegistryRules.Violation(started, retiredDisklessToo, [complete], reserved));

        // CancelDrainAll clears DrainAllRevision = 0 when active and is GoalPresent when inactive.
        var (cancelled, cancelVerdicts, _) = NodeRegistryRules.Fold(started, [NodeDrainAllCancelRequest.Instance], reserved);
        Assert.Equal([NodeRegistryVerdict.Applies], cancelVerdicts);
        Assert.Equal(0UL, cancelled!.DrainAllRevision);
        Assert.False(cancelled.Find(Node(1))!.IsRetired);
        Assert.Null(NodeRegistryRules.Violation(started, cancelled, [NodeDrainAllCancelRequest.Instance], reserved));
        Assert.Equal(NodeRegistryVerdict.GoalPresent, NodeRegistryRules.Decide(cancelled, NodeDrainAllCancelRequest.Instance, reserved));
    }

    private static NodeRegistryValue Mutate(NodeRegistryValue before, NodeRegistryValue after, NodeRegistryChange[] carried, Mutation mutation, int choice)
    {
        var nodes = after.Nodes.ToList();
        bool Named(NodeRegistryEntry entry) => carried.Any(change => change.Value is NodeRetirementRequest retirement && retirement.Node == entry.Node
            || change.Value is NodeRegistrationRequest request && request.NodeToReplace == entry.Node);
        NodeRegistryEntry? Pick(Func<NodeRegistryEntry, bool> where) => nodes.Where(where).ElementAtOrDefault(nodes.Count(where) is > 0 and var count ? choice % count : 0);
        void Replace(NodeRegistryEntry old, NodeRegistryEntry next) => nodes[nodes.IndexOf(old)] = next;
        switch (mutation)
        {
            case Mutation.SkipRevision:
                return new NodeRegistryValue(after.Revision + 1, after.Nodes, after.ArchiveStore, after.ObjectLedgerStore);
            case Mutation.ChangeStore when before.ArchiveStore is not null:
                return new NodeRegistryValue(after.Revision, after.Nodes, StoreB, after.ObjectLedgerStore);
            case Mutation.ChangeObjectLedgerStore when before.ObjectLedgerStore is not null:
                return new NodeRegistryValue(after.Revision, after.Nodes, after.ArchiveStore, before.ObjectLedgerStore == ObjectStoreA ? ObjectStoreB : ObjectStoreA);
            case Mutation.FlipDiskless when Pick(static entry => !entry.IsRetired && entry.Ordinal is null) is { } flipped:
                Replace(flipped, new NodeRegistryEntry(flipped.Node, Silo(9, flipped.Silo.Generation + 1), ordinal: null, flipped.FailureDomain, retiredRevision: 0, diskless: !flipped.Diskless));
                break;
            case Mutation.RemoveEntry when Pick(_ => true) is { } removed:
                _ = nodes.Remove(removed);
                break;
            case Mutation.LowerGeneration when Pick(static entry => !entry.IsRetired && entry.Silo.Generation > 1) is { } lowered:
                Replace(lowered, new NodeRegistryEntry(lowered.Node, Silo(9, lowered.Silo.Generation - 1), lowered.Ordinal, lowered.FailureDomain, retiredRevision: 0, diskless: lowered.Diskless));
                break;
            case Mutation.RetireWithoutSuccession when Pick(entry => !entry.IsRetired && entry.Ordinal is null && !Named(entry)) is { } retired:
                Replace(retired, retired.Retire(after.Revision));
                break;
            case Mutation.RetireUnnamed when Pick(entry => !entry.IsRetired && entry.Ordinal is not null && !Named(entry)) is { } retired:
                Replace(retired, retired.Retire(after.Revision));
                break;
            case Mutation.RetireAtAnotherRevision when after.Revision > 1 && Pick(static entry => !entry.IsRetired) is { } retired:
                Replace(retired, retired.Retire(after.Revision - 1));
                break;
            case Mutation.UnretireEntry when Pick(static entry => entry.IsRetired) is { } unretired:
                Replace(unretired, new NodeRegistryEntry(unretired.Node, unretired.Silo, unretired.Ordinal, unretired.FailureDomain, retiredRevision: 0, diskless: unretired.Diskless));
                break;
            case Mutation.MoveRetirement when Pick(entry => entry.IsRetired && entry.RetiredRevision < after.Revision) is { } moved:
                Replace(moved, new NodeRegistryEntry(moved.Node, moved.Silo, moved.Ordinal, moved.FailureDomain, moved.RetiredRevision + 1, diskless: moved.Diskless));
                break;
            case Mutation.JoinRetiredEarlier when after.Revision > 1:
                nodes.Add(Entry(50, 1).Retire(after.Revision - 1));
                break;
            case Mutation.ShareActiveOrdinal when Pick(static entry => !entry.IsRetired && entry.Ordinal is not null) is { } holder:
                nodes.Add(Entry(51, 1, holder.Ordinal));
                break;

            // A mutation the step has nothing to apply to leaves it as the committer folded it.
            case Mutation.None:
            case Mutation.ChangeStore:
            case Mutation.ChangeObjectLedgerStore:
            case Mutation.FlipDiskless:
            case Mutation.RemoveEntry:
            case Mutation.LowerGeneration:
            case Mutation.RetireWithoutSuccession:
            case Mutation.RetireUnnamed:
            case Mutation.RetireAtAnotherRevision:
            case Mutation.UnretireEntry:
            case Mutation.MoveRetirement:
            case Mutation.JoinRetiredEarlier:
            case Mutation.ShareActiveOrdinal:
            default:
                return after;
        }

        nodes.Sort(static (left, right) => NodeRegistryValue.CompareNodes(left.Node, right.Node));
        return new NodeRegistryValue(after.Revision, [.. nodes], after.ArchiveStore);
    }
}
