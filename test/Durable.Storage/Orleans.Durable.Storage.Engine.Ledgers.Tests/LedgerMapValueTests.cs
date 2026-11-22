using System.Collections.Immutable;
using CsCheck;
using Orleans.Durable.Storage.Testing;
using static Orleans.Durable.Storage.Engine.Ledgers.Tests.LedgerMapSamples;

namespace Orleans.Durable.Storage.Engine.Ledgers.Tests;

/// <summary>
/// The map value's types (ledgers.md §2.3, §4): construction rejects every shape the map cannot hold, <c>Get</c> finds
/// a ledger by ID, and <c>Find</c> answers what a scan of the value's ledgers answers, without allocating when no closed
/// ledger contains the key.
/// </summary>
public sealed class LedgerMapValueTests : IDisposable
{
    private static readonly PrimaryClaim Claim = new(Node(1), new SequencerInstanceId(Guid.AllBitsSet));

    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    /// <summary>Gets replica sets as node numbers, 0 for the default identity: none, an even count, a repeat, and a default.</summary>
    public static TheoryData<string> BadReplicaSets => ["", "1 2", "1 2 1", "1 0 2"];

    public void Dispose() => _leaks.Dispose();

    /// <summary>
    /// LG-I10: <c>OlderOverlapping</c> names, for each listed ledger, the listed ledger with the lowest ID below its own
    /// whose range overlaps it, as a scan of the value's ledgers does, over ranges of depth 0 to 20 which nest, repeat, and
    /// leave gaps, and IDs with gaps; and nothing for an ID the value does not list.
    /// </summary>
    [Fact]
    public void OlderOverlappingNamesEachLedgersLowestOlderOverlappingLedgerAsAScanDoes() =>
        Gen.Select(Gen.Int[0, 20], Gen.Byte[0, 3].Array[3], Gen.Int[1, 3]).Array[0, 24].Sample(static entries =>
        {
            var id = 0UL;
            var ledgers = ImmutableArray.CreateBuilder<LedgerMetadata>();
            var unlisted = new List<LedgerId>();
            foreach (var (depth, bits, gap) in entries)
            {
                if (gap > 1)
                {
                    unlisted.Add(new LedgerId(id + 1));
                }

                id += (ulong)gap;
                byte[] key = [.. bits.Select(static bit => (byte)(bit << 6))];
                ledgers.Add(new LedgerMetadata(new LedgerId(id), KeyRange.Containing(key, depth), [Node(1)], 1, Claim, LedgerStatus.Open.Instance));
            }

            var value = new LedgerMapValue(1, new LedgerId(id + 1), 0, ledgers.ToImmutable());
            foreach (var ledger in value.Ledgers)
            {
                LedgerId? scanned = value.Ledgers.Where(other => other.Id < ledger.Id && other.Range.Overlaps(ledger.Range)).Select(static other => (LedgerId?)other.Id).FirstOrDefault();
                Assert.Equal(scanned, value.OlderOverlapping(ledger.Id));
            }

            Assert.All(unlisted.Append(new LedgerId(id + 1)), ledger => Assert.Null(value.OlderOverlapping(ledger)));
        });

    [Theory]
    [MemberData(nameof(BadReplicaSets))]
    public void ALedgerHasAnOddNumberOfDistinctReplicas(string nodes) =>
        _ = Assert.Throws<ArgumentException>(() => new LedgerMetadata(
            new LedgerId(1),
            KeyRange.Root,
            [.. nodes.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).Select(static node => node == 0 ? default : Node(node))],
            1,
            Claim,
            LedgerStatus.Open.Instance));

    [Fact]
    public void ALedgerHasAtMost255Replicas()
    {
        ImmutableArray<NodeId> replicas = [.. Enumerable.Range(1, LedgerLocation.MaxReplicas + 2).Select(Node)];
        _ = Assert.Throws<ArgumentException>(() => new LedgerMetadata(new LedgerId(1), KeyRange.Root, replicas, 1, Claim, LedgerStatus.Open.Instance));
        var most = new LedgerMetadata(new LedgerId(1), KeyRange.Root, replicas[..LedgerLocation.MaxReplicas], 1, Claim, LedgerStatus.Open.Instance);
        Assert.Equal(128, Assert.IsType<LedgerLocation.Nodes>(most.Location.Value).Quorum);
    }

    [Fact]
    public void ALedgerHasAnIdAWindowAPrimaryAndAStatus()
    {
        ImmutableArray<NodeId> replicas = [Node(1)];
        _ = Assert.Throws<ArgumentException>(() => new LedgerMetadata(default, KeyRange.Root, replicas, 1, Claim, LedgerStatus.Open.Instance));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => new LedgerMetadata(new LedgerId(1), KeyRange.Root, replicas, 0, Claim, LedgerStatus.Open.Instance));
        _ = Assert.Throws<ArgumentException>(() => new LedgerMetadata(new LedgerId(1), KeyRange.Root, replicas, 1, default, LedgerStatus.Open.Instance));
        _ = Assert.Throws<ArgumentException>(() => new LedgerMetadata(new LedgerId(1), KeyRange.Root, replicas, 1, Claim, default));
        _ = Assert.Throws<ArgumentException>(() => new PrimaryClaim(default, new SequencerInstanceId(Guid.AllBitsSet)));
        _ = Assert.Throws<ArgumentException>(() => new PrimaryClaim(Node(1), default));
    }

    /// <summary>Ledgers compare their replicas element by element, not by reference.</summary>
    [Fact]
    public void LedgersWithEqualReplicasInDistinctArraysAreEqual()
    {
        var left = new LedgerMetadata(new LedgerId(1), KeyRange.Root, [Node(1), Node(2), Node(3)], 1, Claim, LedgerStatus.Open.Instance);
        var right = new LedgerMetadata(new LedgerId(1), KeyRange.Root, [Node(1), Node(2), Node(3)], 1, Claim, LedgerStatus.Open.Instance);
        Assert.Equal(left, right);
        Assert.Equal(left.GetHashCode(), right.GetHashCode());
        Assert.NotEqual(left, new LedgerMetadata(new LedgerId(1), KeyRange.Root, [Node(1), Node(3), Node(2)], 1, Claim, LedgerStatus.Open.Instance));
        Assert.NotEqual(left, left.Close(new Slot(0)));
    }

    /// <summary>
    /// ledgers.md §4: a ledger in the object store has no replica, so no node holds it, asking for its replicas is a
    /// defect, and it differs from every ledger on nodes; a ledger on nodes is on exactly its replicas' nodes.
    /// </summary>
    [Fact]
    public void ALedgersLocationNamesTheNodesItIsOn()
    {
        var onNodes = new LedgerMetadata(new LedgerId(1), KeyRange.Root, [Node(1), Node(2), Node(3)], 1, Claim, LedgerStatus.Open.Instance);
        var inObjectStore = new LedgerMetadata(new LedgerId(1), KeyRange.Root, LedgerLocation.ObjectStore.Instance, 1, Claim, LedgerStatus.Open.Instance);

        Assert.Equal([true, true, true, false], Enumerable.Range(1, 4).Select(node => onNodes.IsOnNode(Node(node))));
        _ = Assert.Throws<InvalidOperationException>(() => inObjectStore.Replicas);
        Assert.DoesNotContain(true, Enumerable.Range(1, 4).Select(node => inObjectStore.IsOnNode(Node(node))));
        Assert.NotEqual(onNodes, inObjectStore);
        _ = Assert.Throws<ArgumentException>(() => new LedgerMetadata(new LedgerId(1), KeyRange.Root, default(LedgerLocation), 1, Claim, LedgerStatus.Open.Instance));
    }

    [Fact]
    public void AValueListsItsLedgersInAscendingIdOrderBelowItsNextId()
    {
        var one = Ledger(1, KeyRange.Root, ([Node(1)], 1, Claim), LedgerStatus.Open.Instance);
        var two = Ledger(2, KeyRange.Root, ([Node(1)], 1, Claim), LedgerStatus.Open.Instance);
        _ = Assert.Throws<ArgumentException>(() => new LedgerMapValue(1, new LedgerId(3), 0, [two, one]));
        _ = Assert.Throws<ArgumentException>(() => new LedgerMapValue(1, new LedgerId(3), 0, [one, one]));
        _ = Assert.Throws<ArgumentException>(() => new LedgerMapValue(1, new LedgerId(2), 0, [one, two]));
        _ = Assert.Throws<ArgumentException>(() => new LedgerMapValue(1, default, 0, []));
    }

    [Fact]
    public void AnAbsentRegisterReadsAsEpochZeroWithNoLedgers()
    {
        Assert.Equal((0UL, 1UL, 0UL, 0), (LedgerMapValue.Empty.Epoch, LedgerMapValue.Empty.NextLedgerId.Value, LedgerMapValue.Empty.RetirementFloor, LedgerMapValue.Empty.Ledgers.Length));
        Assert.Null(LedgerMapValue.Empty.Find([0]));
    }

    [Fact]
    public void GetFindsEachListedLedgerAndNoOther() =>
        Values.Sample(static value =>
        {
            foreach (var ledger in value.Ledgers)
            {
                Assert.Equal(ledger, value.Get(ledger.Id));
            }

            for (var id = 1UL; id < Math.Min(value.NextLedgerId.Value + 2, 200); id++)
            {
                Assert.Equal(value.Ledgers.Any(ledger => ledger.Id.Value == id), value.Get(new LedgerId(id)) is not null);
            }
        });

    /// <summary>
    /// <c>Find</c> answers the open ledger and the closed ledgers, in ID order, whose ranges a scan of the value finds
    /// containing the key, for keys of 1 to 9 bytes, so keys shorter than a range's prefix are probed too.
    /// </summary>
    [Fact]
    public void FindAnswersWhatAScanOfTheLedgersAnswers() =>
        Gen.Select(Values, Gen.Byte.Array[1, 9]).Sample(static (value, key) =>
        {
            var containing = value.Ledgers.Where(ledger => ledger.Range.Contains(key)).ToArray();
            var lookup = value.Find(key);
            Assert.NotNull(lookup);
            Assert.Equal(containing.Single(static ledger => ledger.IsOpen), lookup.Value.Open);
            Assert.Equal(containing.Where(static ledger => !ledger.IsOpen), lookup.Value.Closed);
            Assert.Equal(lookup.Value.Open, value.FindOpen(key));
        });

    /// <summary>
    /// <c>Find</c> allocates nothing when no closed ledger contains the key, and <c>FindOpen</c> nothing when closed ledgers
    /// do.
    /// </summary>
    [Fact]
    public void FindAllocatesNothingWhenNoClosedLedgerContainsTheKey()
    {
        var shape = (ImmutableArray.Create(Node(1)), 1, Claim);
        var (lower, upper) = KeyRange.Root.Children;
        var value = new LedgerMapValue(
            2,
            new LedgerId(4),
            0,
            [Ledger(1, lower, shape, new LedgerStatus.Closed(new Slot(5))), Ledger(2, lower, shape, LedgerStatus.Open.Instance), Ledger(3, upper, shape, LedgerStatus.Open.Instance)]);
        byte[] key = [0xff, 1];
        _ = value.Find(key);
        var before = GC.GetAllocatedBytesForCurrentThread();
        var lookup = value.Find(key);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.Equal(3UL, lookup!.Value.Open.Id.Value);
        Assert.Equal([1UL], value.Find([0x01])!.Value.Closed.Select(static ledger => ledger.Id.Value));
        before = GC.GetAllocatedBytesForCurrentThread();
        var open = value.FindOpen([0x01]);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.Equal(2UL, open!.Value.Id.Value);
        Assert.Null(LedgerMapValue.Empty.FindOpen([0]));
    }
}
