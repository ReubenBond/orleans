using System.Collections.Immutable;
using CsCheck;
using Microsoft.Extensions.Time.Testing;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Testing;
using static Orleans.Durable.Storage.Tests.NodeRegistrySamples;

namespace Orleans.Durable.Storage.Tests;

/// <summary>
/// A node's pressure state (orleans.md §5.9, OR-I10): a primary on the node is throttled while the node is under disk
/// pressure, whether or not it is among its ledger's replicas, or while a replica node's report lasts, five control
/// intervals on the monotonic clock; the write path's answer and the control interval's observation agree, each
/// throttle's start and end is logged once (events 6204, 6205), and each throttled write is counted.
/// </summary>
[NoPageLeakCheck("The pressure state rents no pages.")]
public sealed class NodePressureTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan Lifetime = NodePressure.ReportLifetimeIntervals * Interval;
    private static readonly KeyRange[] Ranges = [.. Enumerable.Range(0, 3).Aggregate(
        (IEnumerable<KeyRange>)[KeyRange.Root],
        static (level, _) => level.SelectMany(static range => new[] { range.Children.Lower, range.Children.Upper }))];

    // The node is node 1. It is primary of three open ledgers, one of which does not list it among its replicas, and of
    // a closed one; another node is primary of the last.
    private static readonly LedgerMetadata[] Ledgers =
    [
        Ledger(1, primary: 1, replicas: [1, 2, 3]),
        Ledger(2, primary: 1, replicas: [1, 3, 4]),
        Ledger(3, primary: 1, replicas: [2, 3, 4]),
        Ledger(4, primary: 1, replicas: [1, 2, 3], new LedgerStatus.Closed(Slot.Zero)),
        Ledger(5, primary: 2, replicas: [2, 1, 3]),
    ];

    private static readonly LedgerMapValue Map = new(1, new LedgerId(10), 0, [.. Ledgers]);

    /// <summary>
    /// A report lasts exactly five control intervals: it throttles one tick before, and not at, five intervals after it;
    /// a later report extends it.
    /// </summary>
    [Fact]
    public void AReportLastsExactlyFiveControlIntervals()
    {
        var time = new FakeTimeProvider();
        var pressure = new NodePressure(Interval, time, new RecordingLogger<NodePressure>());
        var ledger = Ledgers[0];

        pressure.Report(Node(2));
        time.Advance(5 * Interval - TimeSpan.FromTicks(1));
        Assert.True(pressure.IsThrottled(ledger));
        time.Advance(TimeSpan.FromTicks(1));
        Assert.False(pressure.IsThrottled(ledger));

        pressure.Report(Node(2));
        time.Advance(4 * Interval);
        pressure.Report(Node(2));
        time.Advance(4 * Interval);
        Assert.True(pressure.IsThrottled(ledger));
    }

    /// <summary>
    /// The node's own pressure throttles every ledger it is primary of, the one which does not list it among its replicas
    /// included, and names the node among the causes; a report from a node which replicates none of a ledger throttles
    /// nothing of it.
    /// </summary>
    [Fact]
    public void TheNodesOwnPressureThrottlesItsPrimariesWhetherOrNotTheyListIt()
    {
        var pressure = new NodePressure(Interval, new FakeTimeProvider(), new RecordingLogger<NodePressure>());
        pressure.Report(Node(4));
        pressure.Observe(Node(1), Map);
        Assert.Equal([false, true, true], Ledgers[..3].Select(pressure.IsThrottled));

        pressure.SetUnderPressure(true);
        pressure.Observe(Node(1), Map);

        Assert.All(Ledgers[..3], ledger => Assert.True(pressure.IsThrottled(ledger)));
        Assert.Equal([Node(1)], pressure.ThrottleCauses[Ledgers[0].Id]);
        Assert.Equal([Node(1), Node(4)], pressure.ThrottleCauses[Ledgers[1].Id]);
        Assert.Equal([Node(1), Node(4)], pressure.ThrottleCauses[Ledgers[2].Id]);
    }

    /// <summary>
    /// orleans.md §5.14: a ledger in the object store writes no node's disk, so neither its primary's pressure nor any
    /// node's report throttles it, and its observation names no cause.
    /// </summary>
    [Fact]
    public void DiskPressureNeverThrottlesALedgerInTheObjectStore()
    {
        var pressure = new NodePressure(Interval, new FakeTimeProvider(), new RecordingLogger<NodePressure>());
        var inObjectStore = new LedgerMetadata(new LedgerId(9), Ranges[0], LedgerLocation.ObjectStore.Instance, 4, new PrimaryClaim(Node(1), SequencerInstanceId.New()), LedgerStatus.Open.Instance);
        pressure.Report(Node(4));
        pressure.SetUnderPressure(true);
        pressure.Observe(Node(1), new LedgerMapValue(1, new LedgerId(10), 0, [inObjectStore]));

        Assert.False(pressure.IsThrottled(inObjectStore));
        Assert.DoesNotContain(inObjectStore.Id, pressure.ThrottleCauses.Keys);

        // Throttled-write counts on an open object-store primary (e.g. request-budget exhaustion) survive Observe until
        // the ledger closes, on both diskful and diskless silos (orleans.md §5.14).
        pressure.CountThrottledWrite(inObjectStore.Id);
        pressure.CountThrottledWrite(inObjectStore.Id);
        pressure.Observe(Node(1), new LedgerMapValue(1, new LedgerId(10), 0, [inObjectStore]));
        Assert.Equal(new Dictionary<LedgerId, long> { [inObjectStore.Id] = 2 }, pressure.ThrottledWrites);

        var closedObjectStore = new LedgerMetadata(inObjectStore.Id, Ranges[0], LedgerLocation.ObjectStore.Instance, 4, inObjectStore.Primary, new LedgerStatus.Closed(Slot.Zero));
        pressure.Observe(Node(1), new LedgerMapValue(2, new LedgerId(10), 0, [closedObjectStore]));
        Assert.Empty(pressure.ThrottledWrites);
    }

    /// <summary>
    /// orleans.md §5.9, §8: each write counted <c>Throttled</c> is counted for its ledger; an observation ends the count of
    /// a ledger which closed, and keeps every other.
    /// </summary>
    [Fact]
    public void EachThrottledWriteIsCountedUntilItsLedgerCloses()
    {
        var pressure = new NodePressure(Interval, new FakeTimeProvider(), new RecordingLogger<NodePressure>());
        pressure.SetUnderPressure(true);
        pressure.CountThrottledWrite(Ledgers[0].Id);
        pressure.CountThrottledWrite(Ledgers[0].Id);
        pressure.CountThrottledWrite(Ledgers[1].Id);
        pressure.Observe(Node(1), Map);
        Assert.Equal(new Dictionary<LedgerId, long> { [Ledgers[0].Id] = 2, [Ledgers[1].Id] = 1 }, pressure.ThrottledWrites);

        pressure.Observe(Node(1), new LedgerMapValue(2, new LedgerId(10), 0, [Ledger(1, primary: 1, replicas: [1, 2, 3], new LedgerStatus.Closed(Slot.Zero)), .. Ledgers[1..]]));

        Assert.Equal(new Dictionary<LedgerId, long> { [Ledgers[1].Id] = 1 }, pressure.ThrottledWrites);
    }

    /// <summary>
    /// Over any sequence of reports, clock advances biased to a report's lifetime, changes of the node's own pressure, and
    /// observations, the write path's answer for each open ledger the node is primary of and the last observation's causes
    /// both equal a model of each node's last report time; the observation names exactly those ledgers, never a closed one
    /// or another node's, and each throttle's start and end is logged once.
    /// </summary>
    [Fact]
    public void TheThrottleFollowsTheLastReportOfEachNodeAndTheNodesOwnPressure()
    {
        var steps = Gen.OneOf(
            Gen.Int[2, 4].Select(static node => new Step(Report: node, null, null)),
            Gen.OneOf(Gen.Const(Lifetime - TimeSpan.FromTicks(1)), Gen.Const(Lifetime), Gen.Const(TimeSpan.FromTicks(1)), Gen.Long[0, Lifetime.Ticks * 2].Select(TimeSpan.FromTicks))
                .Select(static advance => new Step(null, advance, null)),
            Gen.Bool.Select(static under => new Step(null, null, under)),
            Gen.Const(new Step(null, null, null)));

        steps.Array[1, 40].Sample(
            sequence =>
            {
                var time = new FakeTimeProvider();
                var logger = new RecordingLogger<NodePressure>();
                var pressure = new NodePressure(Interval, time, logger);
                var reported = new Dictionary<NodeId, DateTimeOffset>();
                var under = false;
                var observed = ImmutableDictionary<LedgerId, bool>.Empty;
                var (starts, ends) = (0, 0);
                foreach (var step in sequence)
                {
                    if (step.Report is { } node)
                    {
                        pressure.Report(Node(node));
                        reported[Node(node)] = time.GetUtcNow();
                    }
                    else if (step.Advance is { } advance)
                    {
                        time.Advance(advance);
                    }
                    else if (step.UnderPressure is { } underPressure)
                    {
                        pressure.SetUnderPressure(underPressure);
                        under = underPressure;
                    }
                    else
                    {
                        pressure.Observe(Node(1), Map);
                        var now = observed;
                        foreach (var ledger in Ledgers.Where(static ledger => ledger.IsOpen && ledger.Primary.Node == Node(1)))
                        {
                            var throttled = Causes(ledger).Any();
                            var was = now.GetValueOrDefault(ledger.Id);
                            (starts, ends) = (starts + (throttled && !was ? 1 : 0), ends + (!throttled && was ? 1 : 0));
                            now = now.SetItem(ledger.Id, throttled);
                        }

                        observed = now;
                        Assert.Equal(observed.Keys.Order(), pressure.ThrottleCauses.Keys.Order());
                        Assert.All(observed.Keys, ledger => Assert.Equal(Causes(Ledgers.Single(candidate => candidate.Id == ledger)), pressure.ThrottleCauses[ledger]));
                    }

                    Assert.All(Ledgers.Where(static ledger => ledger.Primary.Node == Node(1)), ledger => Assert.Equal(Causes(ledger).Any(), pressure.IsThrottled(ledger)));
                }

                Assert.Equal(starts, logger.Events.Count(static id => id == 6204));
                Assert.Equal(ends, logger.Events.Count(static id => id == 6205));

                IEnumerable<NodeId> Causes(LedgerMetadata ledger) =>
                    ledger.Replicas.Prepend(Node(1)).Distinct().Where(node => node == Node(1)
                        ? under
                        : reported.TryGetValue(node, out var at) && time.GetUtcNow() - at < Lifetime);
            },
            iter: 500);
    }

    private static LedgerMetadata Ledger(int id, int primary, int[] replicas, LedgerStatus? status = null) =>
        new(new LedgerId((ulong)id), Ranges[id], [.. replicas.Select(Node)], 4, new PrimaryClaim(Node(primary), SequencerInstanceId.New()), status ?? LedgerStatus.Open.Instance);

    /// <summary>One step: a report from a node, a clock advance, a change of the node's own pressure, or an observation.</summary>
    private sealed record Step(int? Report, TimeSpan? Advance, bool? UnderPressure);
}
