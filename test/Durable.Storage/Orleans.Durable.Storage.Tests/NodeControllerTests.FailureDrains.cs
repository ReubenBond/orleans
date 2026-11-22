using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Durable.Storage.Engine.Archive.Lsm;
using Orleans.Durable.Storage.Engine.Archive.Lsm.Control;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Cluster.Control;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.Ledgers.Control;
using Orleans.Durable.Storage.Engine.Ledgers.Memory;
using Orleans.Durable.Storage.Hosting;
using Orleans.Durable.Storage.Testing;
using Orleans.Runtime;
using static Orleans.Durable.Storage.Tests.NodeRegistrySamples;

namespace Orleans.Durable.Storage.Tests;

/// <summary>
/// The failure-triggered drains (orleans.md §5.6, OR-I23): when they are due, the order of their starts, their marker, the
/// drain to it, and their backoff, on the LSM and on archive logs.
/// </summary>
public sealed partial class NodeControllerTests
{
    /// <summary>
    /// orleans.md §5.6: the failure-triggered drain of an open ledger the node is primary of is due once exactly <c>Q</c>
    /// of its replicas are live, and not below <c>Q</c>, when no marker can be decided; once it ended at its marker it is
    /// not due again for the same death, while the same replica returning and dying again, though no start saw it live,
    /// and another replica's death each trigger another, each drain ending exactly at its own marker; and the node forgets
    /// the drain once its replicas recover.
    /// </summary>
    [Fact]
    public Task AFailureTriggeredDrainIsDueOnceAtQLiveReplicasForEachDeath() => ReArmAsync(FailureDrainMutant.None);

    /// <summary>
    /// The re-arm test catches a drain which never records its end, a death which is its node alone, and a node which never
    /// forgets a recovered ledger's drain, each by its own message.
    /// </summary>
    /// <param name="mutant">The mutant.</param>
    /// <param name="message">A part of the message of the assertion which catches it.</param>
    [Theory]
    [InlineData(FailureDrainMutant.NeverEnds, "due again for the same death")]
    [InlineData(FailureDrainMutant.NodeOnly, "returned and died again")]
    [InlineData(FailureDrainMutant.NeverForgets, "replicas recovered")]
    internal async Task TheReArmTestCatchesEachMutant(FailureDrainMutant mutant, string message) =>
        Assert.Contains(message, (await Assert.ThrowsAnyAsync<Xunit.Sdk.XunitException>(() => ReArmAsync(mutant))).Message, StringComparison.Ordinal);

    /// <summary>
    /// orleans.md §5.6: a ledger of one replica, whose quorum is its one live replica, has no dead replica to drain for, so
    /// its failure-triggered drain is never due, whatever other nodes, or its own, are dead.
    /// </summary>
    [Fact]
    public async Task ALedgerOfOneReplicaIsNeverDueAFailureTriggeredDrain()
    {
        await using var node = new LsmNode(this);
        var ledger = await node.BootstrapAsync(Node(1));
        _ = await ProposeRowAsync(node.Writer(ledger), 1);

        _dead = Dead(Node(2), Node(3));
        Assert.Empty(node.Node.DrainsDue([]));
        _dead = Dead(Node(1), Node(2), Node(3));
        Assert.Empty(node.Node.DrainsDue([]));
    }

    /// <summary>
    /// orleans.md §5.6: with five replicas, a quorum of three, the drain is due only at exactly three live: not with one
    /// dead, nor with three dead, and a dead node which is no replica changes nothing.
    /// </summary>
    [Fact]
    public Task AFailureTriggeredDrainIsDueAtExactlyQLiveReplicasOfFive() => ExactlyQAsync(FailureDrainMutant.None);

    /// <summary>The exact-<c>Q</c> test catches a drain due at <c>Q</c> live replicas or more.</summary>
    [Fact]
    public async Task TheExactlyQTestCatchesADrainDueAboveQ() =>
        Assert.Contains("above Q", (await Assert.ThrowsAnyAsync<Xunit.Sdk.XunitException>(() => ExactlyQAsync(FailureDrainMutant.LiveAtLeastQ))).Message, StringComparison.Ordinal);

    /// <summary>
    /// orleans.md §5.6: a replica already dead when the primary's writer opened, as a degraded successor's are, makes no
    /// drain due, since every row the writer decides lies on its live replicas; the same replica returning and dying
    /// again does, and a second death, below <c>Q</c>, does not.
    /// </summary>
    [Fact]
    public async Task AReplicaDeadWhenTheWriterOpenedMakesNoDrainDue()
    {
        await using var node = new LsmNode(this);
        _dead = Dead(Node(3));
        node.Transport.Down(Node(3));
        var ledger = await node.BootstrapAsync();
        _ = await ProposeRowAsync(node.Writer(ledger), 1);

        Assert.Empty(node.Node.DrainsDue([]));
        _dead = Dead(Node(2), Node(3));
        Assert.Empty(node.Node.DrainsDue([]));
        _dead = DeadAt((Node(3), 2));
        Assert.Equal([ledger.Id], node.Node.DrainsDue([]).Select(static due => due.Id));
    }

    /// <summary>
    /// orleans.md §5.6: once its marker is decided a drain stays due until it reaches the marker, though the replicas fall
    /// below <c>Q</c> meanwhile: a start whose drain's commit failed reports the failure and backs off, and the start after
    /// the backoff resumes from the decided marker without writing another and ends exactly at it, recording the exposure
    /// from when the node first found the drain due.
    /// </summary>
    [Fact]
    public async Task ADecidedMarkersDrainRunsToItThoughTheReplicasFallBelowQ()
    {
        await using var node = new LsmNode(this) { FailCommits = 1 };
        var ledger = await node.BootstrapAsync();
        var writer = node.Writer(ledger);
        _ = await ProposeRowAsync(writer, 1);
        _dead = Dead(Node(3));
        node.Transport.Down(Node(3));

        var failure = Assert.Single(await node.DrainAsync());
        var marker = writer.KnownContiguousTail - 1;
        Assert.Equal(ledger.Id, failure.Ledger);
        Assert.True(node.Watermark(ledger) < marker);

        _dead = Dead(Node(2), Node(3));
        _time.Advance(node.Node.FirstExposedBackoff);
        Assert.Contains(ledger.Id, node.Node.DrainsDue([]).Select(static due => due.Id));
        Assert.Empty(await node.DrainAsync());

        Assert.Equal(marker, node.Watermark(ledger));
        Assert.Equal(marker + 1, writer.KnownContiguousTail);
        Assert.Equal([node.Node.FirstExposedBackoff.TotalSeconds], node.Exposures);
        Assert.Equal([6247, 6248, 6249], node.Log.Events.Where(static id => id is 6247 or 6248 or 6249));
    }

    /// <summary>
    /// orleans.md §5.6: a start whose drain made no progress, its reads answered backpressure, ends without a failure and
    /// backs the drain off, doubling, the first though it decided the marker, and the later ones writing no marker again;
    /// every third such start logs a warning; and once the reads answer, the start after the backoff resumes from the
    /// decided marker without writing another.
    /// </summary>
    [Fact]
    public async Task AStartWithoutProgressEndsWithoutAFailureAndBacksOff()
    {
        await using var node = new LsmNode(this);
        var ledger = await node.BootstrapAsync();
        var writer = node.Writer(ledger);
        _ = await ProposeRowAsync(writer, 1);
        _dead = Dead(Node(3));
        node.Transport.Backpressured = true;

        // The first start decides the marker and drains nothing, as each later one does; a decided marker is no progress.
        var backoff = node.Node.FirstExposedBackoff;
        for (var start = 0; start < NodeController.ExposedWarnStarts; start++)
        {
            Assert.Empty(await node.DrainAsync());
            _time.Advance(backoff - TimeSpan.FromTicks(1));
            Assert.Equal(start + 1 == NodeController.ExposedWarnStarts ? 1 : 0, node.Log.Events.Count(static id => id == 6250));
            _time.Advance(TimeSpan.FromTicks(1));
            backoff *= 2;
        }

        var marker = writer.KnownContiguousTail - 1;
        Assert.Equal(Slot.Zero, node.Watermark(ledger));
        Assert.Equal(1, node.Log.Events.Count(static id => id == 6248));
        node.Transport.Backpressured = false;
        Assert.Empty(await node.DrainAsync());
        Assert.Equal(marker, node.Watermark(ledger));
        Assert.Equal(marker + 1, writer.KnownContiguousTail);
    }

    /// <summary>
    /// orleans.md §5.6: a marker which is not decided ends the drain without a failure or a submission, the ledger closing:
    /// while its write is held, a takeover's seal reaches the live replicas and the map closes the ledger at the marker's
    /// slot, so the marker meets the seal and is answered <c>Fenced</c>, or <c>Ambiguous</c> once the map also dropped the
    /// ledger before the writer learned its end.
    /// </summary>
    /// <param name="dropped">Whether the ledger is also dropped, so the marker is <c>Ambiguous</c> rather than <c>Fenced</c>.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AMarkerWhichIsNotDecidedEndsTheDrainWithoutAFailure(bool dropped)
    {
        await using var node = new LsmNode(this);
        var ledger = await node.BootstrapAsync();
        var writer = node.Writer(ledger);
        _ = await ProposeRowAsync(writer, 1);
        var markerSlot = writer.KnownContiguousTail;
        _dead = Dead(Node(3));
        try
        {
            node.Transport.HoldWrites(markerSlot);
            Assert.Equal([ledger.Id], node.Start());
            await node.Transport.WriteHeld.WaitAsync(Patience, Token);
            foreach (var replica in new[] { Node(1), Node(2) })
            {
                await using var sealing = node.Transport.Open(replica, ledger.Id, new LedgerReplicaOptions { WindowSize = ledger.K });
                _ = await sealing.ReadTailAsync(markerSlot, seal: true, Token);
            }

            _ = await CommitMapAsync(new LedgerMapChange.Install([new LedgerClosing(ledger.Id, markerSlot)], [Opening(KeyRange.Root, Node(2), Node(1), Node(3))], 0));
            if (dropped)
            {
                _ = await CommitMapAsync(new LedgerMapChange.Drop(ledger.Id));
            }

            node.Transport.ReleaseWrites();
            await node.Node.DrainsEnded.WaitAsync(Patience, Token);
        }
        finally
        {
            node.Transport.Release();
            await node.Node.DrainsEnded.WaitAsync(Patience, Token);
        }

        Assert.Empty(node.Failures);
        Assert.Empty(node.Drains);
        Assert.Contains(6251, node.Log.Events);
        Assert.DoesNotContain(6248, node.Log.Events);
    }

    /// <summary>
    /// orleans.md §5.6: a writer disposed while its marker's write is held ends the drain's start without a failure, the
    /// ledger leaving the node: the marker, which landed, may still be decided, and then the start drains nothing past it.
    /// </summary>
    [Fact]
    public async Task AWriterDisposedWhileItsMarkerIsHeldEndsTheStartWithoutAFailure()
    {
        await using var node = new LsmNode(this);
        var ledger = await node.BootstrapAsync();
        var writer = node.Writer(ledger);
        _ = await ProposeRowAsync(writer, 1);
        var marker = writer.KnownContiguousTail;
        _dead = Dead(Node(3));
        try
        {
            node.Transport.HoldWrites(marker);
            Assert.Equal([ledger.Id], node.Start());
            await node.Transport.WriteHeld.WaitAsync(Patience, Token);
            var disposing = node.Registry.DisposeAsync().AsTask();

            // The write lands only once the disposal stopped the writer, so the marker is never decided.
            for (var waited = TimeSpan.Zero; node.Registry.TryGet(ledger.Id) is not null; waited += TimeSpan.FromMilliseconds(1))
            {
                Assert.True(waited < Patience, "The registry's disposal never stopped the writer.");
                await Task.Delay(TimeSpan.FromMilliseconds(1), TimeProvider.System, Token);
            }

            node.Transport.ReleaseWrites();
            await disposing.WaitAsync(Patience, Token);
            await node.Node.DrainsEnded.WaitAsync(Patience, Token);
        }
        finally
        {
            node.Transport.Release();
            await node.Node.DrainsEnded.WaitAsync(Patience, Token);
        }

        Assert.Empty(node.Failures);
        Assert.All(node.Drains, drain => Assert.True(drain.Below <= marker, $"A drain reached {drain.Below}, past the marker at {marker}."));
        Assert.Empty(node.Node.DrainsDue([]));
    }

    /// <summary>
    /// orleans.md §5.6: the ledgers a dead replica exposes, a closed ledger naming the dead node and an open one whose
    /// failure-triggered drain is due, are due first, closed before open, and start ahead of a closed ledger the controller
    /// named which was never started, though each of them started before.
    /// </summary>
    [Fact]
    public Task TheLedgersADeadReplicaExposesStartFirst() => ExposedFirstAsync(FailureDrainMutant.None);

    /// <summary>The ordering test catches starts which order every due ledger least recently started first.</summary>
    [Fact]
    public async Task TheOrderingTestCatchesStartsWhichPutNoExposedLedgerFirst() =>
        Assert.Contains("an exposed ledger first", (await Assert.ThrowsAnyAsync<Xunit.Sdk.XunitException>(() => ExposedFirstAsync(FailureDrainMutant.ExposedNotFirst))).Message, StringComparison.Ordinal);

    /// <summary>
    /// orleans.md §5.6, archive-lsm.md §8.1: a failure-triggered drain starts only once its marker is decided, reading and
    /// submitting nothing while the marker's write is held, then drains, in one start, every closed segment below the
    /// marker's slot <c>m</c> and ends exactly at <c>m</c>, reading no batch past it, though a later segment closed below
    /// the contiguous tail while its first read was held: on the LSM one drain a segment, to <c>m</c>, and on archive logs
    /// one drain to <c>m</c>. The dead replica refuses every call.
    /// </summary>
    /// <param name="logs">Whether the archive store is archive logs rather than the LSM.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task AFailureTriggeredDrainStartsOnceItsMarkerIsDecidedAndEndsAtItsSlot(bool logs) =>
        DrainToTheMarkerAsync(logs, FailureDrainMutant.None);

    /// <summary>
    /// The ordering test catches each mutant which reaches it, by its own message: a drain before the marker is decided, a
    /// bound at the marker's slot, below which its segment does not close, on either store; one drain a start on the LSM,
    /// whose segments below the marker take a drain each; and a bound past the marker on archive logs, whose one drain
    /// would take the later closed segment too.
    /// </summary>
    /// <param name="logs">Whether the archive store is archive logs rather than the LSM.</param>
    /// <param name="mutant">The mutant.</param>
    /// <param name="message">A part of the message of the assertion which catches it.</param>
    [Theory]
    [InlineData(false, FailureDrainMutant.DrainsBeforeTheMarker, "before its marker was decided")]
    [InlineData(true, FailureDrainMutant.DrainsBeforeTheMarker, "before its marker was decided")]
    [InlineData(false, FailureDrainMutant.BoundAtTheMarker, "below its marker")]
    [InlineData(true, FailureDrainMutant.BoundAtTheMarker, "below its marker")]
    [InlineData(false, FailureDrainMutant.OneDrainAStart, "below its marker")]
    [InlineData(true, FailureDrainMutant.DrainsPastTheMarker, "past its marker")]
    internal async Task TheMarkerOrderingTestCatchesEachMutant(bool logs, FailureDrainMutant mutant, string message) =>
        Assert.Contains(message, (await Assert.ThrowsAnyAsync<Xunit.Sdk.XunitException>(() => DrainToTheMarkerAsync(logs, mutant))).Message, StringComparison.Ordinal);

    /// <summary>
    /// orleans.md §5.7: the nodes the runtime counts dead are the registered ones whose silo membership shows dead, or no
    /// longer lists at a generation older than the observer's, each at its silo's generation; a node registered again at a
    /// newer generation of the same address is live, though membership shows its old silo dead.
    /// </summary>
    [Fact]
    public void TheDeadNodesAreThoseMembershipShowsDeadAtTheirRegisteredGeneration()
    {
        var registry = new NodeRegistryValue(4, [Entry(1, 1), Entry(2, 1), Entry(3, 2), Entry(4, 1)], StoreA);
        var members = new[] { (Silo(1, 1), SiloStatus.Active), (Silo(2, 1), SiloStatus.Dead), (Silo(3, 1), SiloStatus.Dead), (Silo(3, 2), SiloStatus.Active) }
            .ToImmutableDictionary(static member => member.Item1, static member => new ClusterMember(member.Item1, member.Item2, member.Item1.ToString()));

        var dead = NodeLedgers.DeadNodes(registry, members, observerGeneration: 5);

        Assert.Equal(new Dictionary<NodeId, int> { [Node(2)] = 1, [Node(4)] = 1 }, dead.ToDictionary());
        Assert.Empty(NodeLedgers.DeadNodes(null, members, observerGeneration: 5));
    }

    /// <summary>
    /// orleans.md §5.6: the deaths a writer opened with are those its own execution began with, keyed to its lifetime: a
    /// bootstrap whose commit lands but answers an unknown outcome is served late; a replica live at its opening then dies,
    /// and an unrelated execution, which began knowing the death, ends; the drain is due all the same.
    /// </summary>
    [Fact]
    public Task AWriterServedAfterAnUnknownOutcomeKeepsItsOwnExecutionsDeaths() => OpenedWithAsync(FailureDrainMutant.None);

    /// <summary>The opening-record test catches a record taken from whichever execution ends while the writer has none.</summary>
    [Fact]
    public async Task TheOpeningRecordTestCatchesARecordTakenAtAnyExecutionsEnd() =>
        Assert.Contains("opened with", (await Assert.ThrowsAnyAsync<Xunit.Sdk.XunitException>(() => OpenedWithAsync(FailureDrainMutant.RecordsAtAnyExecutionsEnd))).Message, StringComparison.Ordinal);

    /// <summary>
    /// orleans.md §5.6: exposed drains take a slot reserved beyond the concurrency: with the one slot held by an ordinary
    /// drain, a replica's death starts one exposed ledger's drain in the reserved slot, and the other exposed ledger, which
    /// finds both held, warns (6255) once it has waited five intervals without a start.
    /// </summary>
    [Fact]
    public Task AnExposedDrainTakesTheSlotReservedBeyondTheConcurrency() => ReservedSlotAsync(FailureDrainMutant.None);

    /// <summary>The reserved-slot test catches a node which reserves no slot for exposed drains.</summary>
    [Fact]
    public async Task TheReservedSlotTestCatchesANodeWhichReservesNone() =>
        Assert.Contains("reserved slot", (await Assert.ThrowsAnyAsync<Xunit.Sdk.XunitException>(() => ReservedSlotAsync(FailureDrainMutant.NoReservedSlot))).Message, StringComparison.Ordinal);

    /// <summary>
    /// orleans.md §5.6: on archive logs, the older overlapping ledger an exposed ledger's drain waits for joins the exposed
    /// tier: once the successor's failure-triggered drain answers <c>Waiting</c> on its predecessor, the predecessor starts
    /// ahead of a closed ledger started less recently.
    /// </summary>
    [Fact]
    public Task TheLedgerAnExposedDrainWaitsForStartsFirst() => WaitedOnAsync(FailureDrainMutant.None);

    /// <summary>The waited-on test catches a node which leaves the older ledger out of the exposed tier.</summary>
    [Fact]
    public async Task TheWaitedOnTestCatchesANodeWhichLeavesTheOlderLedgerOut() =>
        Assert.Contains("waits for", (await Assert.ThrowsAnyAsync<Xunit.Sdk.XunitException>(() => WaitedOnAsync(FailureDrainMutant.WaitedOnNotExposed))).Message, StringComparison.Ordinal);

    private async Task OpenedWithAsync(FailureDrainMutant mutant)
    {
        await using var node = new LsmNode(this);
        node.Node.FailureDrainMutant = mutant;
        async Task<LedgerMapCommitOutcome> LandsUnknownAsync(LedgerMapChange change, CancellationToken token)
        {
            _ = await CommitAsync(change, token);
            throw new IOException("The commit's outcome is unknown.");
        }

        _ = await Assert.ThrowsAnyAsync<IOException>(() => node.Node.ExecuteAsync(new Transition.Bootstrap(Choice(Node(1))), LandsUnknownAsync, Token));
        var ledger = Assert.Single((await _view.ReadAsync(Token)).Ledgers);
        for (var waited = TimeSpan.Zero; node.Registry.TryGet(ledger.Id) is null; waited += TimeSpan.FromMilliseconds(5))
        {
            Assert.True(waited < Patience, "The registry never served the writer whose bootstrap landed.");
            await Task.Delay(TimeSpan.FromMilliseconds(5), TimeProvider.System, Token);
            _ = await _view.ReadAsync(Token);
        }

        _ = await ProposeRowAsync(node.Writer(ledger), 1);
        _dead = Dead(Node(3));
        _ = await Record.ExceptionAsync(() => node.Node.ExecuteAsync(new Transition.Bootstrap(Choice(Node(1))), CommitAsync, Token));

        Assert.True(node.Node.DrainsDue([]).Any(due => due.Id == ledger.Id), "The writer opened with a death its own execution never knew of, so its drain is not due.");
    }

    private async Task ReservedSlotAsync(FailureDrainMutant mutant)
    {
        await using var node = new LsmNode(this);
        node.Node.FailureDrainMutant = mutant;
        var root = await node.BootstrapAsync();
        _ = Assert.IsType<LedgerMapCommitOutcome.Applied>((await node.Node.ExecuteAsync(new Transition.Split(root.Id, Choice(Node(1))), CommitAsync, Token)).Value);
        var lower = (await _view.ReadAsync(Token)).Ledgers.Single(ledger => ledger.IsOpen && ledger.Range == KeyRange.Root.Children.Lower);
        _ = Assert.IsType<LedgerMapCommitOutcome.Applied>((await node.Node.ExecuteAsync(new Transition.Split(lower.Id, Choice(Node(1)) with { }), CommitAsync, Token)).Value);
        _ = await _view.ReadAsync(Token);
        while (node.Node.DrainsDue([]).Any(static due => !due.IsOpen))
        {
            Assert.Empty(await node.DrainAsync());
        }

        var open = _view.Current.Ledgers.Where(static ledger => ledger.IsOpen).OrderBy(static ledger => ledger.Id.Value).ToList();
        Assert.Equal(3, open.Count);
        var busy = open[0];
        var writer = node.Writer(busy);
        static byte KeyIn(LedgerMetadata ledger) => Enumerable.Range(0, 256).Select(static key => (byte)key).First(key => ledger.Range.Contains([key]));
        _ = await ProposeRowAsync(writer, KeyIn(busy));
        _ = await writer.EndSegmentAsync(Token);
        _ = await ProposeRowAsync(writer, KeyIn(busy));

        // Each exposed ledger holds a row, so its drain holds a commit, and with it its slot.
        foreach (var exposed in open.Skip(1))
        {
            _ = await ProposeRowAsync(node.Writer(exposed), KeyIn(exposed));
        }
        try
        {
            // The one slot holds an ordinary drain when node 3 dies.
            node.HoldCommits();
            Assert.Equal([busy.Id], node.Start());
            await node.CommitHeld.WaitAsync(Patience, Token);
            Assert.Equal(0, node.OldestExposure());

            _dead = Dead(Node(3));
            var reserved = node.Start();
            Assert.True(reserved.Length == 1 && open.Skip(1).Any(ledger => ledger.Id == reserved[0]), "No exposed ledger's drain started in the reserved slot.");

            // The other exposed ledger finds both slots held, and warns once it has waited five intervals; the gauge reads
            // the time since the node found the drains due.
            for (var interval = 1; interval < NodeController.ExposedWaitWarnIntervals; interval++)
            {
                _time.Advance(TimeSpan.FromSeconds(1));
                Assert.Empty(node.Start());
            }

            Assert.Equal(1, node.Log.Events.Count(static id => id == 6255));
            Assert.Equal(NodeController.ExposedWaitWarnIntervals - 1, node.OldestExposure());
        }
        finally
        {
            node.ReleaseCommits();
            await node.Node.DrainsEnded.WaitAsync(Patience, Token);
        }
    }

    private async Task WaitedOnAsync(FailureDrainMutant mutant)
    {
        await using var logs = await StartLogsNodeAsync();
        logs.Node.FailureDrainMutant = mutant;
        await logs.ShareAsync(this);
        var root = await InstallAsync(Opening(KeyRange.Root, Node(1), Node(2), Node(3)));
        var (lowerRange, upperRange) = KeyRange.Root.Children;
        _ = await CommitMapAsync(new LedgerMapChange.Install([new LedgerClosing(root.Id, Slot.Zero)], [Opening(lowerRange, Node(1), Node(2), Node(4)), Opening(upperRange, Node(1), Node(2), Node(4))], 0));
        _ = await CommitMapAsync(new LedgerMapChange.Drop(root.Id));
        var lower = _view.Current.Ledgers.Single(ledger => ledger.Range == lowerRange);
        var upper = _view.Current.Ledgers.Single(ledger => ledger.Range == upperRange);

        // The node takes the lower ledger over onto nodes 1 to 3, so its successor's drain waits for the lower ledger's drop,
        // and the upper ledger closes; neither closed ledger names node 3, which dies.
        _ = Assert.IsType<LedgerMapCommitOutcome.Applied>((await logs.Node.ExecuteAsync(new Transition.Replace(lower.Id, new TransitionChoice(Node(1), [Node(1), Node(2), Node(3)], 4, 0, _view.Current.Epoch)), CommitAsync, Token)).Value);
        _ = await CommitMapAsync(new LedgerMapChange.Install([new LedgerClosing(upper.Id, Slot.Zero)], [Opening(upperRange, Node(2), Node(1), Node(4))], 0));
        var successor = _view.Current.Ledgers.Single(ledger => ledger.IsOpen && ledger.Range == lowerRange);
        static Task<LedgerMapCommitOutcome> RefusedAsync(LedgerMapChange change, CancellationToken token) =>
            Task.FromResult<LedgerMapCommitOutcome>(LedgerMapCommitOutcome.Refused.Instance);

        // The closed ledgers' drops are refused, so both stay due; the upper one starts before the lower one's last start.
        Assert.Equal([lower.Id], await StartAndWaitAsync(logs, RefusedAsync));
        Assert.Equal([upper.Id], await StartAndWaitAsync(logs, RefusedAsync));
        Assert.Equal([lower.Id], await StartAndWaitAsync(logs, RefusedAsync));
        _ = await ProposeRowAsync(logs.Writer(successor), 1);

        _dead = Dead(Node(3));
        Assert.Equal([successor.Id], await StartAndWaitAsync(logs, RefusedAsync));
        Assert.Equal(lower.Id, logs.Drains.WaitingFor(successor.Id));
        Assert.True((await StartAndWaitAsync(logs, RefusedAsync)).SequenceEqual([lower.Id]), "The ledger an exposed drain waits for did not start first.");

        static async Task<ImmutableArray<LedgerId>> StartAndWaitAsync(LogsNode logs, Func<LedgerMapChange, CancellationToken, Task<LedgerMapCommitOutcome>> commit)
        {
            var started = logs.Start([], commit);
            await logs.Node.DrainsEnded.WaitAsync(Patience, Token);
            return started;
        }
    }

    private static IReadOnlyDictionary<NodeId, int> Dead(params NodeId[] nodes) => nodes.ToImmutableDictionary(static node => node, static _ => 1);

    private static IReadOnlyDictionary<NodeId, int> DeadAt(params (NodeId Node, int Generation)[] nodes) => nodes.ToImmutableDictionary(static node => node.Node, static node => node.Generation);

    private async Task ReArmAsync(FailureDrainMutant mutant)
    {
        await using var node = new LsmNode(this);
        node.Node.FailureDrainMutant = mutant;
        var ledger = await node.BootstrapAsync();
        var writer = node.Writer(ledger);
        _ = await ProposeRowAsync(writer, 1);
        Assert.Empty(node.Node.DrainsDue([]));

        _dead = Dead(Node(2), Node(3));
        Assert.Empty(node.Node.DrainsDue([]));
        _dead = Dead(Node(3));
        Assert.Equal([ledger.Id], node.Node.DrainsDue([]).Select(static due => due.Id));
        Assert.Empty(await node.DrainAsync());
        Assert.Equal(writer.KnownContiguousTail - 1, node.Watermark(ledger));
        Assert.True(node.Node.DrainsDue([]).IsEmpty, "A drain which reached its marker is due again for the same death.");

        _ = await ProposeRowAsync(writer, 2);
        _dead = DeadAt((Node(3), 2));
        Assert.True(node.Node.DrainsDue([]).Any(due => due.Id == ledger.Id), "A replica which returned and died again, though no start saw it live, triggers no drain.");
        Assert.Empty(await node.DrainAsync());
        Assert.Equal(writer.KnownContiguousTail - 1, node.Watermark(ledger));

        _ = await ProposeRowAsync(writer, 3);
        _dead = Dead(Node(2));
        Assert.Equal([ledger.Id], node.Node.DrainsDue([]).Select(static due => due.Id));
        Assert.Empty(await node.DrainAsync());
        Assert.Equal(writer.KnownContiguousTail - 1, node.Watermark(ledger));
        Assert.Equal(3, node.Log.Events.Count(static id => id == 6248));

        _dead = Dead();
        Assert.Empty(node.Start());
        Assert.True(node.Node.TrackedFailureDrains == 0, "The node keeps a drain whose replicas recovered.");
    }

    private async Task ExactlyQAsync(FailureDrainMutant mutant)
    {
        await using var node = new LsmNode(this);
        node.Node.FailureDrainMutant = mutant;
        var ledger = await node.BootstrapAsync(Node(1), Node(2), Node(3), Node(4), Node(5));
        _ = await ProposeRowAsync(node.Writer(ledger), 1);

        _dead = Dead(Node(6));
        Assert.Empty(node.Node.DrainsDue([]));
        _dead = Dead(Node(5));
        Assert.True(node.Node.DrainsDue([]).IsEmpty, "The drain is due with one of five replicas dead, above Q live.");
        _dead = Dead(Node(4), Node(5), Node(6));
        Assert.Equal([ledger.Id], node.Node.DrainsDue([]).Select(static due => due.Id));
        _dead = Dead(Node(3), Node(4), Node(5));
        Assert.Empty(node.Node.DrainsDue([]));
    }

    private async Task ExposedFirstAsync(FailureDrainMutant mutant)
    {
        await using var node = new LsmNode(this) { FailCommits = int.MaxValue };
        node.Node.FailureDrainMutant = mutant;
        var root = await InstallAsync(Opening(KeyRange.Root, Node(1), Node(2), Node(3)));
        var (lowerRange, upperRange) = KeyRange.Root.Children;
        _ = await CommitMapAsync(new LedgerMapChange.Install([new LedgerClosing(root.Id, Slot.Zero)], [Opening(lowerRange, Node(1), Node(2), Node(4)), Opening(upperRange, Node(2), Node(1), Node(3))], 0));
        _ = await CommitMapAsync(new LedgerMapChange.Drop(root.Id));
        var lower = _view.Current.Ledgers.Single(ledger => ledger.Range == lowerRange);
        var upper = _view.Current.Ledgers.Single(ledger => ledger.Range == upperRange);

        // The node takes the lower ledger over onto the dead-to-be node 4: the closed ledger and its successor name it.
        var reply = await node.Node.ExecuteAsync(new Transition.Replace(lower.Id, new TransitionChoice(Node(1), [Node(1), Node(2), Node(4)], 4, 0, _view.Current.Epoch)), CommitAsync, Token);
        _ = Assert.IsType<LedgerMapCommitOutcome.Applied>(reply.Value);
        _ = await CommitMapAsync(new LedgerMapChange.Install([new LedgerClosing(upper.Id, Slot.Zero)], [Opening(upperRange, Node(2), Node(1), Node(3))], 0));
        var successor = _view.Current.Ledgers.Single(ledger => ledger.IsOpen && ledger.Range == lowerRange);
        lower = _view.Current.Get(lower.Id)!.Value;
        upper = _view.Current.Get(upper.Id)!.Value;

        // Before the death the closed lower ledger, then the successor, once a segment closed, start and fail, so each
        // started before the upper ledger, which only the controller's naming makes due.
        Assert.Equal([lower.Id], await node.StartAndWaitAsync([]));
        var writer = node.Writer(successor);
        _ = await ProposeRowAsync(writer, 1);
        _ = await writer.EndSegmentAsync(Token);
        _ = await ProposeRowAsync(writer, 2);
        Assert.Equal([successor.Id], await node.StartAndWaitAsync([]));

        _dead = Dead(Node(4));
        node.Transport.Down(Node(4));
        Assert.Equal([lower.Id, successor.Id, upper.Id], node.Node.DrainsDue([upper.Id]).Select(static due => due.Id));
        Assert.True((await node.StartAndWaitAsync([upper.Id])).SequenceEqual([lower.Id, successor.Id]), "The starts take an exposed ledger first.");
    }

    private async Task DrainToTheMarkerAsync(bool logs, FailureDrainMutant mutant)
    {
        using var transport = new HoldingTransport();
        await using var logsNode = logs ? await StartLogsNodeAsync(transport: transport) : null;
        await using var lsmNode = logs ? null : new LsmNode(this, transport);
        var node = logsNode?.Node ?? lsmNode!.Node;
        node.FailureDrainMutant = mutant;
        LedgerMetadata ledger;
        if (logsNode is not null)
        {
            ledger = await logsNode.BootstrapAsync(this);
            await logsNode.ShareAsync(this);
        }
        else
        {
            ledger = await lsmNode!.BootstrapAsync();
        }

        var writer = logsNode?.Writer(ledger) ?? lsmNode!.Writer(ledger);
        Slot Watermark() => logsNode?.Drains.DrainedBelow(ledger.Id) ?? lsmNode!.Watermark(ledger);
        _ = await ProposeRowAsync(writer, 1);
        _ = await ProposeRowAsync(writer, 2);
        var firstEnd = Assert.IsType<ProposalOutcome.Durable>((await writer.EndSegmentAsync(Token)).Value).Slot;
        _ = await ProposeRowAsync(writer, 3);
        var marker = await ProposeRowAsync(writer, 4) + 1;
        Assert.Equal(marker, writer.KnownContiguousTail);
        transport.ReadSlots.Clear();
        try
        {
            _dead = Dead(Node(3));
            transport.Down(Node(3));
            transport.HoldWrites(marker);
            Assert.Equal([ledger.Id], logsNode?.Start([], CommitAsync) ?? lsmNode!.Start());
            await transport.WriteHeld.WaitAsync(Patience, Token);
            Assert.True(transport.ReadSlots.IsEmpty && (lsmNode?.Drains.IsEmpty ?? true) && Watermark() == Slot.Zero, "The drain read or submitted before its marker was decided.");

            transport.HoldReads();
            transport.ReleaseWrites();
            await transport.ReadHeld.WaitAsync(Patience, Token);
            Assert.Equal(marker + 1, await ProposeRowAsync(writer, 5));
            _ = await ProposeRowAsync(writer, 6);
            var laterEnd = Assert.IsType<ProposalOutcome.Durable>((await writer.EndSegmentAsync(Token)).Value).Slot;
            _ = await ProposeRowAsync(writer, 7);
            Assert.True(writer.KnownContiguousTail > laterEnd, "A later segment is closed below the contiguous tail.");
            transport.ReleaseReads();
            await node.DrainsEnded.WaitAsync(Patience, Token);
        }
        finally
        {
            // A drain a failed assertion left running ends before the node's disposal, releasing what it holds.
            transport.Release();
            await node.DrainsEnded.WaitAsync(Patience, Token);
        }

        Assert.All(transport.ReadSlots, slot => Assert.True(slot <= marker, $"The drain read slot {slot}, past its marker's {marker}."));
        Assert.True(Watermark() >= marker, $"The drain's one start ended at watermark {Watermark()}, below its marker {marker}.");
        Assert.Equal(marker, Watermark());
        Assert.Empty(logsNode?.Failures ?? lsmNode!.Failures);
        if (lsmNode is not null)
        {
            Assert.Equal([firstEnd, marker], lsmNode.Drains.Select(static drain => drain.Below));
        }
    }

    /// <summary>Proposes one row of <paramref name="key"/> through <paramref name="writer"/>, and returns the slot it was decided at.</summary>
    private static async Task<Slot> ProposeRowAsync(LedgerWriter writer, byte key)
    {
        using var body = ArcBuffer.Wrap(new byte[8]);
        Row[] rows = [new Row([key], RowKind.Append, body)];
        try
        {
            return Assert.IsType<ProposalOutcome.Durable>((await writer.ProposeAsync(rows, Token)).Value).Slot;
        }
        finally
        {
            rows[0].Dispose();
        }
    }

    /// <summary>
    /// A node on the LSM under test, the primary of the ledgers it bootstraps, with its own process's reader and writer
    /// registry over a <see cref="HoldingTransport"/>, which drains into the test's object store and manifest; its drains'
    /// scans give up on backpressure at once, and its logger and meter record the failure-triggered drains' events and
    /// exposures.
    /// </summary>
    private sealed class LsmNode : IAsyncDisposable
    {
        private readonly NodeControllerTests _test;
        private readonly Meter _meter = new(DurableStorageMeter.Name);
        private readonly MeterListener _listener = new();
        private readonly ConcurrentQueue<double> _exposures = new();
        private double? _oldest;
        private int _failCommits;
        private bool _disposed;
        private TaskCompletionSource? _commitHold;
        private TaskCompletionSource _commitHeld = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public LsmNode(NodeControllerTests test, HoldingTransport? transport = null)
        {
            _test = test;
            Transport = transport ?? new HoldingTransport();
            Reader = new LedgerReader(test._view, Transport, test._time, Node(1));
            Registry = new LedgerWriterRegistry(Reader, 1 << 20, LedgerWriterOptions.Default, test._time, NullLoggerFactory.Instance);
            // The scans' one retry after backpressure waits a millisecond of real time, which the test's fake clock would never pass.
            var drains = new DrainWorker(test._objects, test._manifest, Reader, Archive, TimeProvider.System) { BackpressureRetries = 1, BackpressureDelay = TimeSpan.FromMilliseconds(1) };
            var lsm = new NodeArchive.Lsm(drains, new CompactionWorker(test._objects, new TailCache(test._objects, Archive), test._manifest, Archive), test._manifest);
            Node = test.Controller(Registry, Reader, lsm, drainConcurrency: 1, Log, _meter);
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (ReferenceEquals(instrument.Meter, _meter))
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<double>((instrument, value, _, _) =>
            {
                if (instrument.Name == "orleans.durable.storage.ledger.exposure")
                {
                    _exposures.Enqueue(value);
                }
                else
                {
                    _oldest = value;
                }
            });
            _listener.Start();
        }

        public HoldingTransport Transport { get; }

        public LedgerReader Reader { get; }

        public LedgerWriterRegistry Registry { get; }

        public NodeController Node { get; }

        public RecordingLogger Log { get; } = new();

        /// <summary>Gets the exposures the node recorded, in seconds.</summary>
        public IReadOnlyList<double> Exposures => [.. _exposures];

        /// <summary>Reads the gauge of the node's oldest unfinished exposure, in seconds.</summary>
        public double? OldestExposure()
        {
            _oldest = null;
            _listener.RecordObservableInstruments();
            return _oldest;
        }

        /// <summary>Gets or sets how many of the next drain commits throw, as an unknown outcome does.</summary>
        public int FailCommits
        {
            get => _failCommits;
            init => _failCommits = value;
        }

        /// <summary>Gets a task which completes once a commit arrives held, after the last <see cref="HoldCommits"/>.</summary>
        public Task CommitHeld => Volatile.Read(ref _commitHeld).Task;

        /// <summary>Holds every drain commit until <see cref="ReleaseCommits"/>.</summary>
        public void HoldCommits()
        {
            Volatile.Write(ref _commitHeld, new(TaskCreationOptions.RunContinuationsAsynchronously));
            Volatile.Write(ref _commitHold, new(TaskCreationOptions.RunContinuationsAsynchronously));
        }

        public void ReleaseCommits() => Interlocked.Exchange(ref _commitHold, null)?.TrySetResult();

        /// <summary>Gets the drain changes the node submitted, in order, those whose commit threw among them.</summary>
        public ConcurrentQueue<ManifestChange.Drain> Drains { get; } = new();

        /// <summary>Gets the failures the drains this node started reported, in the order they ended.</summary>
        public ConcurrentQueue<(LedgerId Ledger, Exception Failure)> Failures { get; } = new();

        public LedgerWriter Writer(LedgerMetadata ledger) => Registry.TryGet(ledger.Id) ?? throw new InvalidOperationException("The node serves no writer of the ledger.");

        /// <summary>Installs the first ledger through the node, its primary, with replicas on nodes 1, 2, and 3 unless given.</summary>
        public async Task<LedgerMetadata> BootstrapAsync(params NodeId[] replicas)
        {
            var choice = replicas.Length == 0 ? Choice(NodeRegistrySamples.Node(1)) : new TransitionChoice(NodeRegistrySamples.Node(1), [.. replicas], 4, 0, 0);
            _ = Assert.IsType<LedgerMapCommitOutcome.Applied>((await Node.ExecuteAsync(new Transition.Bootstrap(choice), _test.CommitAsync, Token)).Value);
            return Assert.Single((await _test._view.ReadAsync(Token)).Ledgers);
        }

        /// <summary>Returns the watermark of <paramref name="ledger"/> the manifest records.</summary>
        public Slot Watermark(LedgerMetadata ledger) => _test._manifest.Current?.Value.RecordedWatermark(ledger.Id) ?? Slot.Zero;

        /// <summary>Starts the node's drains due, recording their changes and failures.</summary>
        public ImmutableArray<LedgerId> Start(ImmutableArray<LedgerId> drainToo = default) =>
            Node.StartDrains(drainToo.IsDefault ? [] : drainToo, CommitAsync, (ledger, failure) => Failures.Enqueue((ledger, failure)), Token);

        /// <summary>Starts the node's drains due and waits for every drain to end, answering the ledgers it started.</summary>
        public async Task<ImmutableArray<LedgerId>> StartAndWaitAsync(ImmutableArray<LedgerId> drainToo)
        {
            var started = Start(drainToo);
            await Node.DrainsEnded.WaitAsync(Patience, Token);
            return started;
        }

        /// <summary>Starts the node's drains due and waits for every drain to end, answering the failures they reported.</summary>
        public async Task<ImmutableArray<(LedgerId Ledger, Exception Failure)>> DrainAsync()
        {
            Failures.Clear();
            _ = Start();
            await Node.DrainsEnded.WaitAsync(Patience, Token);
            return [.. Failures];
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            ReleaseCommits();
            Transport.Release();
            await Node.DrainsEnded.WaitAsync(Patience, Token);
            await Registry.DisposeAsync();
            await Reader.DisposeAsync();
            Transport.Dispose();
            _listener.Dispose();
            _meter.Dispose();
        }

        private async Task<ManifestCommitOutcome> CommitAsync(ManifestChange change, CancellationToken token)
        {
            if (change.Value is ManifestChange.Drain drain)
            {
                Drains.Enqueue(drain);
            }

            if (Volatile.Read(ref _commitHold) is { } hold)
            {
                _ = Volatile.Read(ref _commitHeld).TrySetResult();
                await hold.Task.WaitAsync(token);
            }

            return Interlocked.Decrement(ref _failCommits) >= 0
                ? throw new IOException("The manifest commit's outcome is unknown.")
                : await _test._manifestCommitter.CommitAsync(change, token);
        }
    }

    /// <summary>
    /// An in-memory replica transport which holds every replica write at or above a slot, or every read and tail read,
    /// while a test asks it to, completing a signal as the first call arrives held; answers every read with backpressure
    /// while asked; fails every call to a node it holds down; and records the slot of every read.
    /// </summary>
    private sealed class HoldingTransport : ILedgerReplicaTransport, IDisposable
    {
        private readonly InMemoryLedgerReplicaTransport _inner = new();
        private readonly Lock _lock = new();
        private readonly HashSet<NodeId> _down = [];
        private TaskCompletionSource? _writes;
        private Slot _writesFrom;
        private TaskCompletionSource? _reads;
        private TaskCompletionSource _writeHeld = NewSignal();
        private TaskCompletionSource _readHeld = NewSignal();
        private volatile bool _backpressured;

        /// <summary>Gets the slot of every read, in order.</summary>
        public ConcurrentQueue<Slot> ReadSlots { get; } = new();

        /// <summary>Gets or sets a value indicating whether every read answers backpressure.</summary>
        public bool Backpressured
        {
            get => _backpressured;
            set => _backpressured = value;
        }

        /// <summary>Gets a task which completes once a write at or above the held slot arrives, after the last <see cref="HoldWrites"/>.</summary>
        public Task WriteHeld
        {
            get
            {
                lock (_lock)
                {
                    return _writeHeld.Task;
                }
            }
        }

        /// <summary>Gets a task which completes once a read arrives held, after the last <see cref="HoldReads"/>.</summary>
        public Task ReadHeld
        {
            get
            {
                lock (_lock)
                {
                    return _readHeld.Task;
                }
            }
        }

        /// <summary>Fails every later call to <paramref name="node"/>, as a dead node's would.</summary>
        public void Down(NodeId node)
        {
            lock (_lock)
            {
                _ = _down.Add(node);
            }
        }

        public void HoldWrites(Slot from = default)
        {
            lock (_lock)
            {
                (_writes, _writesFrom, _writeHeld) = (NewSignal(), from, NewSignal());
            }
        }

        public void HoldReads()
        {
            lock (_lock)
            {
                (_reads, _readHeld) = (NewSignal(), NewSignal());
            }
        }

        public void ReleaseWrites()
        {
            TaskCompletionSource? writes;
            lock (_lock)
            {
                (writes, _writes) = (_writes, null);
            }

            _ = writes?.TrySetResult();
        }

        public void ReleaseReads()
        {
            TaskCompletionSource? reads;
            lock (_lock)
            {
                (reads, _reads) = (_reads, null);
            }

            _ = reads?.TrySetResult();
        }

        public void Release()
        {
            ReleaseWrites();
            ReleaseReads();
        }

        public ILedgerReplica Open(NodeId node, LedgerId ledger, LedgerReplicaOptions options) => new HeldReplica(this, node, _inner.Open(node, ledger, options));

        public ValueTask<ReplicaHeartbeatRead> HeartbeatAsync(NodeId node, ReadOnlyMemory<LedgerId> ledgers, CancellationToken cancellationToken = default)
        {
            lock (_lock)
            {
                if (_down.Contains(node))
                {
                    throw new TimeoutException($"Replica node {node} is unreachable.");
                }
            }

            return _inner.HeartbeatAsync(node, ledgers, cancellationToken);
        }

        public void Dispose()
        {
            Release();
            _inner.Dispose();
        }

        private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

        private void ThrowIfDown(NodeId node)
        {
            lock (_lock)
            {
                if (_down.Contains(node))
                {
                    throw new TimeoutException($"Node {node} is dead.");
                }
            }
        }

        private Task PassAsync(Slot? write)
        {
            TaskCompletionSource? gate;
            TaskCompletionSource held;
            lock (_lock)
            {
                (gate, held) = write is { } slot ? (slot >= _writesFrom ? _writes : null, _writeHeld) : (_reads, _readHeld);
            }

            if (gate is null)
            {
                return Task.CompletedTask;
            }

            _ = held.TrySetResult();
            return gate.Task;
        }

        private sealed class HeldReplica(HoldingTransport owner, NodeId node, ILedgerReplica inner) : ILedgerReplica
        {
            public async ValueTask<ReplicaWriteOutcome> WriteAsync(Slot slot, ArcBuffer payload, Slot contiguousTail, bool recovery, CancellationToken cancellationToken = default)
            {
                owner.ThrowIfDown(node);
                await owner.PassAsync(slot).WaitAsync(cancellationToken);
                return await inner.WriteAsync(slot, payload, contiguousTail, recovery, cancellationToken);
            }

            public async ValueTask<ReplicaRead> ReadAsync(Slot slot, Slot contiguousTail, CancellationToken cancellationToken = default)
            {
                owner.ThrowIfDown(node);
                owner.ReadSlots.Enqueue(slot);
                await owner.PassAsync(write: null).WaitAsync(cancellationToken);
                return owner.Backpressured ? new ReplicaRead(ReplicaWriteOutcome.Backpressure.Instance) : await inner.ReadAsync(slot, contiguousTail, cancellationToken);
            }

            public async ValueTask<ReplicaRead> ReadAsync(Slot slot, int offset, int length, Slot contiguousTail, CancellationToken cancellationToken = default)
            {
                owner.ThrowIfDown(node);
                owner.ReadSlots.Enqueue(slot);
                await owner.PassAsync(write: null).WaitAsync(cancellationToken);
                return owner.Backpressured ? new ReplicaRead(ReplicaWriteOutcome.Backpressure.Instance) : await inner.ReadAsync(slot, offset, length, contiguousTail, cancellationToken);
            }

            public async ValueTask<ReplicaTailRead> ReadTailAsync(Slot contiguousTail, bool seal, CancellationToken cancellationToken = default)
            {
                owner.ThrowIfDown(node);
                await owner.PassAsync(write: null).WaitAsync(cancellationToken);
                return await inner.ReadTailAsync(contiguousTail, seal, cancellationToken);
            }

            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }
}
