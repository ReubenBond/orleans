using System.Collections.Immutable;
using Orleans.Durable.Storage.Engine.Ledgers.Control;
using Orleans.Durable.Storage.Testing;
using static Orleans.Durable.Storage.Engine.Ledgers.Tests.LedgerMapRulesTests;

namespace Orleans.Durable.Storage.Engine.Ledgers.Tests;

/// <summary>
/// What a transition adds to the map's encoding (<see cref="LedgerMapGrowth.GrowthOf"/>) and how many ranges a value
/// limit holds (<see cref="LedgerMapCommitter.RangeCapacity"/>), each judged by encoding the value it describes, so a
/// controller counts the map's size without knowing the <c>CLLM</c> layout (orleans.md §5.6).
/// </summary>
public sealed class LedgerMapGrowthTests : IDisposable
{
    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    private static readonly ImmutableArray<NodeId> Three = [LedgerMapSamples.Node(1), LedgerMapSamples.Node(2), LedgerMapSamples.Node(3)];

    private static TransitionChoice Choice => new(Three[0], Three, k: 2, chosenAt: 0, epoch: 0);

    public void Dispose() => _leaks.Dispose();

    /// <summary>Each case's growth is the difference between the encodings of the map before and after its install.</summary>
    [Fact]
    public void EachTransitionsGrowthIsWhatItsInstallAddsToTheEncoding()
    {
        var bootstrapped = Applies(LedgerMapValue.Empty, Install([], [KeyRange.Root]));
        Assert.Equal(Added(LedgerMapValue.Empty, bootstrapped), LedgerMapValue.Empty.GrowthOf(new Transition.Bootstrap(Choice)));

        var root = Assert.Single(bootstrapped.Ledgers);
        var replaced = Applies(bootstrapped, Install([new(root.Id, new Slot(1))], [KeyRange.Root]));
        Assert.Equal(Added(bootstrapped, replaced), bootstrapped.GrowthOf(new Transition.Replace(root.Id, Choice)));

        var (lower, upper) = KeyRange.Root.Children;
        var split = Applies(bootstrapped, Install([new(root.Id, new Slot(1))], [lower, upper]));
        Assert.Equal(Added(bootstrapped, split), bootstrapped.GrowthOf(new Transition.Split(root.Id, Choice)));

        var (first, second) = (split.Ledgers[^2], split.Ledgers[^1]);
        var merged = Applies(split, Install([new(first.Id, new Slot(1)), new(second.Id, new Slot(1))], [KeyRange.Root]));
        Assert.Equal(Added(split, merged), split.GrowthOf(new Transition.Merge(first.Id, second.Id, Choice)));
    }

    /// <summary>A transition which closes a ledger the map does not list open has no growth in it.</summary>
    [Fact]
    public void AGrowthClosesOnlyLedgersTheMapListsOpen()
    {
        var bootstrapped = Applies(LedgerMapValue.Empty, Install([], [KeyRange.Root]));
        var replaced = Applies(bootstrapped, Install([new(bootstrapped.Ledgers[0].Id, new Slot(1))], [KeyRange.Root]));
        _ = Assert.Throws<ArgumentException>(() => replaced.GrowthOf(new Transition.Replace(bootstrapped.Ledgers[0].Id, Choice)));
        _ = Assert.Throws<ArgumentException>(() => replaced.GrowthOf(new Transition.Replace(new LedgerId(99), Choice)));
    }

    /// <summary>
    /// The capacity is the most ranges, split evenly, whose map with one closed predecessor per range fits the limit:
    /// that map fits, and the map of one range more does not.
    /// </summary>
    [Theory]
    [InlineData(1_000)]
    [InlineData(64 << 10)]
    [InlineData(400 << 10)]
    public async Task TheRangeCapacityIsTheLargestEvenlySplitMapWhichFits(int limit)
    {
        await using var committer = new LedgerMapCommitter(new UnusedRegister(), limit);
        var capacity = committer.RangeCapacity(Three.Length);
        Assert.True(capacity > 0);
        Assert.True(Saturated(capacity).EncodedLength <= limit);
        Assert.True(Saturated(capacity + 1).EncodedLength > limit);
    }

    private static int Added(LedgerMapValue before, LedgerMapValue after) => after.EncodedLength - before.EncodedLength;

    /// <summary>Returns a map of <paramref name="ranges"/> open ranges at the depth which holds them, each with one closed predecessor.</summary>
    private static LedgerMapValue Saturated(int ranges)
    {
        var depth = ranges <= 1 ? 0 : 64 - ulong.LeadingZeroCount((ulong)ranges - 1);
        var claim = new PrimaryClaim(Three[0], new SequencerInstanceId(Guid.Parse("00000000-0000-0000-0000-000000000001")));
        var ledgers = ImmutableArray.CreateBuilder<LedgerMetadata>(2 * ranges);
        for (var status = 0; status < 2; status++)
        {
            for (var index = 0; index < ranges; index++)
            {
                var range = depth == 0 ? KeyRange.Root : KeyRange.Containing(LedgerMapSamples.Key((ulong)index << (int)(64 - depth)), (int)depth);
                var id = (ulong)(status * ranges + index + 1);
                ledgers.Add(new LedgerMetadata(new LedgerId(id), range, Three, 2, claim, status == 0 ? new LedgerStatus.Closed(new Slot(1)) : LedgerStatus.Open.Instance));
            }
        }

        return new LedgerMapValue(1, new LedgerId((ulong)(2 * ranges) + 1), 0, ledgers.MoveToImmutable());
    }

    /// <summary>A register the committer is built over and never touches.</summary>
    private sealed class UnusedRegister : MetaStore.IConditionalRegister
    {
        public ValueTask<MetaStore.MetaStoreEntry?> ReadAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<MetaStore.RegisterWriteOutcome> WriteAsync(MetaStore.MetaStoreVersion? expectedVersion, System.Buffers.ReadOnlySequence<byte> value, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
