using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Configuration;
using Orleans.Core.Internal;
using Orleans.Durable.Journaling;
using Orleans.Durable.Storage.AzureStorage.Archive;
using Orleans.Durable.Storage.Control;
using Orleans.Durable.Storage.Engine.Archive;
using Orleans.Durable.Storage.Engine.Archive.Logs;
using Orleans.Durable.Storage.Engine.Archive.Lsm;
using Orleans.Durable.Storage.Engine.Archive.Lsm.Control;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Cluster.Control;
using Orleans.Durable.Storage.Engine.Journals;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.Ledgers.Control;
using Orleans.Durable.Storage.Engine.Ledgers.Store;
using Orleans.Durable.Storage.Engine.MetaStore;
using Orleans.Durable.Storage.Engine.ObjectStore;
using Orleans.Durable.Storage.Engine.ObjectStore.Memory;
using Orleans.Durable.Storage.Engine.Wal;
using Orleans.Durable.Storage.Engine.Wal.Portable;
using Orleans.Durable.Storage.Hosting;
using Orleans.Durable.Storage.Testing;
using Orleans.Hosting;
using Orleans.Runtime;
using Orleans.Runtime.Placement;
using Orleans.Storage;
using Orleans.TestingHost;
using static Orleans.Durable.Storage.Tests.NodeRegistrySamples;

namespace Orleans.Durable.Storage.Tests;

/// <summary>
/// The controller grain, the node control grains, and each silo's Ledgers runtime over silos with their own node roots,
/// one MetaStore, and one object store (orleans.md §5.2, §5.6, §5.7, §7.2): bootstrap, splits, and a requested roll; a
/// stopping silo's hand-off while membership still shows it active, its stop order against held and late work, and its
/// declines while it hands off; takeovers after a kill, with a sweep and a manifest replace held, and with the object
/// store refusing every call; every survivor following the map through its reports; the controller's committers in no
/// caller's context; interleaved node calls; a misplaced or retired node's declines; drains one round at a time; a silo
/// whose runtime cannot start; a decode failure of an applied reply; and the sweep of a stray replica.
/// </summary>
/// <remarks>
/// Every hold is a gate the test opens, and teardown opens every gate before it stops the silos, each stop bounded; a stop
/// past its bound writes each silo's stop, its lifecycle stages and Ledgers runtime step with their times and its
/// warnings, to the results directory (#252). Only the tests which kill a silo shorten membership's probes; the others
/// keep Orleans' defaults.
/// </remarks>
[Collection(TestingHostCollection.Name)]
public sealed partial class OrleansControllerTests : IAsyncLifetime
{
    private const string TestKey = "DurableStorageControllerTest";
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(60);
    private static readonly ConcurrentDictionary<string, TestState> States = new(StringComparer.Ordinal);

    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();
    private readonly TestState _state = new();
    private TestCluster? _cluster;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync()
    {
        States[_state.Id] = _state;
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _state.ReleaseAll();
        if (_cluster is not null)
        {
            // A stopping silo hands its ledgers off while another active silo can take them, then stops as a crash would.
            foreach (var silo in _cluster.Silos.Reverse().ToList())
            {
                _state.RecordStop(silo.Name, "teardown stop begins");
                try
                {
                    await _cluster.StopSiloAsync(silo).WaitAsync(Patience, CancellationToken.None);
                }
                catch (TimeoutException timeout)
                {
                    var report = _state.WriteStopReport(silo.Name, Patience);
                    throw new TimeoutException($"Silo {silo.Name}'s stop outlasted {Patience}; its stop report is {report}.", timeout);
                }

                _state.RecordStop(silo.Name, "teardown stop ended");
            }

            await _cluster.DisposeAsync();
        }

        _ = States.TryRemove(_state.Id, out _);
        Assert.True(_state.DropViolations.IsEmpty, string.Join(Environment.NewLine, _state.DropViolations));
        if (Directory.Exists(_state.Roots))
        {
            Directory.Delete(_state.Roots, recursive: true);
        }

        _leaks.Dispose();
    }

    /// <summary>
    /// orleans.md §5.6: with three silos registered, the controller bootstraps the first ledger and splits toward one open
    /// ledger per live active node, each on three distinct registered nodes, its primary first (OR-I5), and each primary's
    /// silo serves its ledger through its writer registry; a roll a node requests (OR-I13) closes its ledger.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task TheControllerBootstrapsSplitsAndRollsOnRequestOverThreeSilos()
    {
        var cluster = await DeployAsync(3, TestContext.Current.CancellationToken);

        var map = await ServedAsync(cluster, ranges: 3);

        var nodes = Nodes(cluster).Select(static node => node.Node).ToHashSet();
        Assert.All(map.Ledgers.Where(static ledger => ledger.IsOpen), ledger =>
        {
            Assert.Equal(3, ledger.Replicas.Distinct().Count());
            Assert.Subset(nodes, ledger.Replicas.ToHashSet());
            Assert.Equal(ledger.Primary.Node, ledger.Replicas[0]);
        });

        // The silo's WAL retention repeats the request (row 4.4); one request, which the controller records, rolls it.
        var rolled = map.Ledgers.First(static ledger => ledger.IsOpen).Id;
        await Controller(cluster).RequestRollAsync(rolled);
        _ = await ServedAsync(cluster, ranges: 3, done: value => value.Get(rolled) is not { IsOpen: true });
    }

    /// <summary>
    /// orleans.md §5.6: each primary's node reports its ledgers' proposed bytes per second over <c>LoadWindow</c>, and the
    /// controller splits a ledger whose load exceeds <c>SplitBytesPerSecond</c>. A 64 KiB batch each control interval
    /// forces the split by construction: every window of two control intervals then measures at least 64 KiB over three
    /// intervals, far above 16 KiB/s, while an idle ledger's markers stay far below it, so every report carries the load
    /// until a pass, whichever it is, splits the ledger.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task ABatchEachIntervalAboveTheSplitThresholdSplitsItsLedger()
    {
        _state.Control = static control =>
        {
            control.SplitBytesPerSecond = 16 << 10;
            control.LoadWindow = 2 * control.ControlInterval;
        };
        var cluster = await DeployAsync(3, TestContext.Current.CancellationToken);
        var map = await ServedAsync(cluster, ranges: 3, done: value => Balanced(cluster, value));
        var ledger = map.Ledgers.First(static ledger => ledger.IsOpen);
        var handle = Nodes(cluster).Single(entry => entry.Node == ledger.Primary.Node).Handle;
        LedgerWriter? writer = null;
        await EventuallyAsync(() => (writer = Runtime(handle)!.Writers.TryGet(ledger.Id)) is not null, $"s{ledger.Id}'s primary to serve it");

        byte[] key = [Enumerable.Range(0, 256).Select(static value => (byte)value).First(value => ledger.Range.Contains([value]))];
        var (lower, upper) = ledger.Range.Children;
        var interval = Services(handle).GetRequiredService<ControlOptions>().ControlInterval;
        var writing = Stopwatch.StartNew();
        while (!Split() && await ProposeAsync())
        {
            Assert.True(writing.Elapsed < Patience, $"Waited {Patience} for s{ledger.Id} to split.");
            await Task.Delay(interval, TimeProvider.System, Token);
        }

        // A proposal which is not durable met the split's close of the ledger, whose install the filter then records.
        await EventuallyAsync(Split, $"s{ledger.Id} to split");

        bool Split() => _state.AppliedInstalls.Any(install => install.Closes.Any(closing => closing.Id == ledger.Id)
            && install.Opens.Select(static opening => opening.Range).ToHashSet().SetEquals([lower, upper]));

        async Task<bool> ProposeAsync()
        {
            using var payload = Payload(new byte[64 << 10]);
            using var row = new Row(key, RowKind.Append, payload);
            try
            {
                return (await writer!.ProposeAsync([row], Token)).Value is ProposalOutcome.Durable;
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// orleans.md §5.9, OR-I10: a node under disk pressure throttles the primary of every open ledger it replicates, on
    /// that primary's own silo through its pressure reports, and its own primaries through its pressure, while the primary
    /// of a ledger it does not replicate is not throttled; the meter publishes the pressure, the throttles, and the map's
    /// ledgers. Once its pressure ends, every throttle ends within the reports' lifetime, and each start and end, of the
    /// pressure and of each throttle, is logged once. A write forwarded to a throttled ledger's primary, and a write on the
    /// primary's own silo, are answered <c>Throttled</c> before its Journals, counted, and land once the throttle ends.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task ADiskPressureThrottlesThePrimariesOfTheLedgersItsNodeReplicatesUntilItEnds()
    {
        var gauges = new ConcurrentDictionary<(string Instrument, string? Node, string? Tag), long>();
        using var listener = Listen(gauges);
        var cluster = await DeployAsync(4, TestContext.Current.CancellationToken);
        var map = await ServedAsync(cluster, ranges: 4, done: value => Balanced(cluster, value));
        var open = map.Ledgers.Where(static ledger => ledger.IsOpen).ToArray();

        // The pressed node replicates a ledger another node is primary of, so its reports throttle that primary, and is
        // no replica of another ledger, whose primary stays unthrottled.
        var (pressed, _, handle) = Assert.Single(
            Nodes(cluster).Where(entry => open.Any(ledger => ledger.Replicas.Contains(entry.Node) && ledger.Primary.Node != entry.Node)
                && open.Any(ledger => !ledger.Replicas.Contains(entry.Node))).Take(1));
        bool Throttles(LedgerMetadata ledger) => ledger.Replicas.Contains(pressed) || ledger.Primary.Node == pressed;

        _state.ExtraWalBytes[handle.Name] = 1L << 50;
        await EventuallyAsync(
            async () => (map = await ReadMapAsync()).Ledgers.Where(static ledger => ledger.IsOpen)
                .All(ledger => Pressure(cluster, ledger.Primary.Node).IsThrottled(ledger) == Throttles(ledger)),
            "the primary of exactly each ledger the pressed node replicates to be throttled");
        Assert.True(Pressure(cluster, pressed).UnderPressure);
        Assert.All(Nodes(cluster).Where(entry => entry.Node != pressed), entry => Assert.False(Pressure(cluster, entry.Node).UnderPressure));
        var throttled = map.Ledgers.Where(ledger => ledger.IsOpen && Throttles(ledger)).ToArray();
        await EventuallyAsync(() => throttled.All(ledger => Logged(6204, ledger) == 1), "each throttle's start to be logged");

        // A write forwarded to a throttled ledger's primary is answered Throttled before its Journals, without effect.
        var written = throttled[0];
        var key = KeyIn(written);
        var primaryJournals = _state.Journals[Nodes(cluster).Single(entry => entry.Node == written.Primary.Node).Handle.Name];
        primaryJournals.BeforeCall = static () => throw new InvalidOperationException("A throttled ledger's write reaches no Journals.");
        Assert.Equal(LocalJournalWriteOutcome.Throttled.Instance, (await ForwardAsync(JournalNode(cluster, written.Primary.Node), key, JournalVersion.Origin, [1, 2, 3])).Value);

        // A local write on the throttled primary is answered Throttled before its Journals too, and resent until it lands.
        var primarySilo = Nodes(cluster).Single(entry => entry.Node == written.Primary.Node).Handle;
        var localKey = Enumerable.Range(0, int.MaxValue).Select(static index => string.Create(CultureInfo.InvariantCulture, $"local-{index}")).First(candidate => written.Range.Contains(JournalRowKey.Encode(candidate).Span));
        using var localPayload = Payload([4, 5, 6]);
        using var localRecord = JournalEntryRecord.Append(JournalRowKey.Encode(localKey), JournalVersion.Origin, localPayload);
        var localWrite = Services(primarySilo).GetRequiredService<JournalRouter>().WriteAsync(localKey, JournalRowKey.Encode(localKey), localRecord, Token);
        await EventuallyAsync(() => Pressure(cluster, written.Primary.Node).ThrottledWrites.GetValueOrDefault(written.Id) > 2, "the local write to be answered Throttled and resent");
        Assert.False(localWrite.IsCompleted);
        primaryJournals.BeforeCall = null;

        // Only the throttled ledger's primary answers Throttled: a replica node of it answers Moved naming the primary.
        var replica = written.Replicas.First(node => node != written.Primary.Node);
        Assert.Equal(new JournalMoved(written.Primary.Node), (await ForwardAsync(JournalNode(cluster, replica), key, JournalVersion.Origin, [1, 2, 3])).Value);

        // The meter publishes the node's pressure, each primary's last observed throttle, and the map's ledgers.
        await EventuallyAsync(
            () =>
            {
                listener.RecordObservableInstruments();
                return throttled.All(ledger => gauges.GetValueOrDefault(("orleans.durable.storage.ledger.throttled", ledger.Primary.Node.ToString(), ledger.Id.ToString())) == 1)
                    && gauges.GetValueOrDefault(("orleans.durable.storage.cluster.ledgers", null, "open")) == map.Ledgers.Count(static ledger => ledger.IsOpen);
            },
            "the meter to publish each throttle and the map's open ledgers");
        Assert.All(Nodes(cluster), entry => Assert.Equal(entry.Node == pressed ? 1 : 0, gauges[("orleans.durable.storage.node.disk.pressure", entry.Node.ToString(), null)]));
        Assert.True(gauges[("orleans.durable.storage.node.disk.used", pressed.ToString(), null)] > 1L << 50);

        _state.ExtraWalBytes[handle.Name] = 0;
        await EventuallyAsync(
            async () => (await ReadMapAsync()).Ledgers.Where(static ledger => ledger.IsOpen).All(ledger => !Pressure(cluster, ledger.Primary.Node).IsThrottled(ledger)),
            "every throttle to end");
        await EventuallyAsync(() => throttled.All(ledger => Logged(6205, ledger) == 1), "each throttle's end to be logged");

        // Once the throttle ends, the write lands, at the version the throttled one left unchanged.
        _ = Assert.IsType<JournalWriteOutcome.Accepted>((await ForwardAsync(JournalNode(cluster, written.Primary.Node), key, JournalVersion.Origin, [1, 2, 3])).Value);
        _ = Assert.IsType<JournalWriteOutcome.Accepted>((await localWrite.WaitAsync(Patience, Token)).Value);
        Assert.All(throttled, ledger => Assert.Equal(1, Logged(6204, ledger)));
        Assert.Equal([6237, 6238], _state.PressureEvents.Where(entry => entry.Silo == handle.Name && entry.EventId is 6237 or 6238).Select(static entry => entry.EventId));

        int Logged(int eventId, LedgerMetadata ledger) =>
            _state.PressureEvents.Count(entry => entry.EventId == eventId && Equals(entry.Ledger, ledger.Id));
    }

    /// <summary>
    /// orleans.md §5.9, OR-I13: once a node's WAL passes <c>F</c> beyond its oldest replica's first file, it asks for that
    /// replica's WAL-release roll, and the controller grain rolls the ledger once. With drains stalled, the rolled ledger
    /// stays closed and live and pins the WAL, so the node asks for no second roll however far its WAL grows, and its disk
    /// budget then puts it under pressure. Once drains resume, the closed ledger drains and drops, and the pressure ends.
    /// Ledgers which hold no replica pin no WAL, and are not rolled.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task WithDrainsStalledANodeRollsItsOldestReplicaOnceAndThenComesUnderPressure()
    {
        // T = 2 MiB over 1 MiB files: F = 1, so the second WAL file starts the rolls; a budget of 8 MiB, above T and three
        // files, which the stalled WAL soon passes; checkpoints each second, so a drop releases its WAL soon after.
        _state.Node = static (_, options) =>
        {
            options.WalRetentionBytes = 2 << 20;
            options.DiskBudgetBytes = 8 << 20;
            options.Checkpoints = new LedgerStoreCheckpointOptions { CheckpointInterval = TimeSpan.FromSeconds(1) };
        };
        var cluster = await DeployAsync(3, TestContext.Current.CancellationToken);

        // Every closed ledger the bootstrap's splits left drains and drops first: one still live at the written ledger's
        // first file would rightly hold the roll back until its own drain.
        var map = await ServedAsync(cluster, ranges: 3, done: value => Balanced(cluster, value) && value.Ledgers.All(static ledger => ledger.IsOpen));
        var initial = map.Ledgers.Select(static ledger => ledger.Id).ToHashSet();
        var written = map.Ledgers.First();
        _ = _state.ManifestReplaces.Hold();

        await WriteAsync(cluster, written, batches: 24);
        await EventuallyAsync(() => Rolls(written.Id) == 1, $"s{written.Id} to be rolled for WAL release");
        LedgerMetadata successor = default;
        await EventuallyAsync(
            async () => (await ReadMapAsync()).Ledgers.FirstOrDefault(ledger => ledger.IsOpen && ledger.Range == written.Range) is { Id.Value: var id } open
                && id > written.Id.Value && (successor = open).IsOpen,
            $"s{written.Id}'s successor to open");

        await WriteAsync(cluster, successor, batches: 160);
        await EventuallyAsync(() => successor.Replicas.All(node => Pressure(cluster, node).UnderPressure), "the stalled WAL to put the successor's nodes under disk pressure");
        Assert.Equal((1, 0), (Rolls(written.Id), Rolls(successor.Id)));
        Assert.NotNull((await ReadMapAsync()).Get(written.Id));

        _state.ManifestReplaces.Release();
        await EventuallyAsync(
            async () => (await ReadMapAsync()).Get(written.Id) is null && successor.Replicas.All(node => !Pressure(cluster, node).UnderPressure),
            $"s{written.Id} to drain and drop, and the pressure to end");
        var last = await ReadMapAsync();
        Assert.All(initial.Where(ledger => ledger != written.Id), ledger => Assert.True(last.Get(ledger) is { IsOpen: true }, $"Ledger {ledger}, which pins no WAL, was rolled."));

        int Rolls(LedgerId ledger) => _state.AppliedInstalls.Count(install => install.Closes.Any(closing => closing.Id == ledger));
    }

    /// <summary>
    /// orleans.md §5.6, archive-lsm.md §8.7: on the LSM each silo preloads the ranges of the open ledgers its node is
    /// primary of, and follows that set as it changes: once the cluster is deployed and balanced, and again once a silo's
    /// stop has handed its ledgers to the others.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task EachSiloPreloadsTheRangesItsNodeIsPrimaryOf()
    {
        var cluster = await DeployAsync(3, TestContext.Current.CancellationToken);
        _ = await ServedAsync(cluster, ranges: 3, done: value => Balanced(cluster, value));
        await PreloadsFollowThePrimariesAsync(cluster);

        var (_, handle) = await PrimaryAsync(cluster, silo => silo != cluster.Primary);
        await cluster.StopSiloAsync(handle, TestContext.Current.CancellationToken).WaitAsync(Patience, Token);
        await PreloadsFollowThePrimariesAsync(cluster);
    }

    /// <summary>
    /// orleans.md §5.2, §5.6: a stopping silo hands each ledger its node is primary of to another node before it stops:
    /// while a lifecycle participant below the runtime's stage holds the stop, membership still shows the silo active, so
    /// no takeover can have moved them, and the map already names the node primary of none. Its Journals have stopped by
    /// then, and with them every snapshot its forwarded reads pinned is released (§5.5), before the silo's disposal.
    /// Balancing is paused once the map is balanced, so only the stop's hand-off (a departure, outside the balancing
    /// budget) moves the node's ledger, and the writes before the stop meet no other seal.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task AStoppingSiloHandsItsLedgersOffWhileMembershipShowsItActive()
    {
        var cluster = await DeployAsync(4, TestContext.Current.CancellationToken);
        var map = await ServedAsync(cluster, ranges: 4, done: value => Balanced(cluster, value));
        foreach (var silo in cluster.Silos)
        {
            Services(silo).GetRequiredService<ControlOptions>().MaxConcurrentTransitions = 0;
        }

        var (node, _, handle) = Nodes(cluster).First(entry => entry.Handle != cluster.Primary && map.Ledgers.Any(ledger => ledger.IsOpen && ledger.Primary.Node == entry.Node));
        await EventuallyAsync(() => _state.Journals[handle.Name].State == LocalJournals.JournalsState.Serving, $"{handle.Name}'s Journals to serve");
        var key = KeyIn((await ReadMapAsync()).Ledgers.First(ledger => ledger.IsOpen && ledger.Primary.Node == node));
        var grain = JournalNode(cluster, node);
        var version = JournalVersion.Origin;
        foreach (var body in new[] { new byte[700 << 10], new byte[700 << 10] })
        {
            version = Assert.IsType<JournalWriteOutcome.Accepted>((await ForwardAsync(grain, key, version, body)).Value).Version;
        }

        var found = Assert.IsType<JournalForwardedRead.Found>((await grain.ReadAsync(key, Token)).Value);
        found.FirstPage.Dispose();
        var reads = Services(handle).GetRequiredService<ForwardedReads>();
        Assert.Equal(1, reads.Pinned);
        var held = _state.StopGate.Hold(handle.Name);

        var stop = cluster.StopSiloAsync(handle, Token);
        await held.WaitAsync(Patience, Token);

        var members = Services(cluster.Silos[0]).GetRequiredService<IClusterMembershipService>().CurrentSnapshot.Members;
        Assert.Equal(SiloStatus.Active, members[handle.SiloAddress].Status);
        Assert.DoesNotContain((await ReadMapAsync()).Ledgers, ledger => ledger.IsOpen && ledger.Primary.Node == node);
        Assert.Equal((0, 0L), (reads.Pinned, reads.ReservedBytes));
        _state.StopGate.Release();
        await stop.WaitAsync(Patience, Token);
        _ = await ServedAsync(cluster, ranges: 3);
    }

    /// <summary>
    /// orleans.md §5.2, §5.7, OR-I5: in a three-silo cluster (<c>N = 3, Q = 2</c>), a gracefully stopping silo waits
    /// for degraded quorum hand-off and hands its open ledgers off cooperatively before its stop completes and while
    /// membership still shows it <c>Active</c>; writes continue on the remaining two silos with zero lost acknowledged
    /// entries.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task AStoppingSiloOnAThreeSiloClusterHandsOffCooperativelyBeforeDeathDeclaration()
    {
        var cluster = await DeployAsync(3, TestContext.Current.CancellationToken);
        var map = await ServedAsync(cluster, ranges: 3, done: value => Balanced(cluster, value));
        var (stoppingNode, _, handle) = Nodes(cluster).Last(entry => entry.Handle != cluster.Primary && map.Ledgers.Any(ledger => ledger.IsOpen && ledger.Primary.Node == entry.Node));
        await EventuallyAsync(() => Nodes(cluster).All(entry => _state.Journals[entry.Handle.Name].State == LocalJournals.JournalsState.Serving), "all three silos' Journals to serve");

        var stoppingLedger = (await ReadMapAsync()).Ledgers.First(ledger => ledger.IsOpen && ledger.Primary.Node == stoppingNode);
        var key = KeyIn(stoppingLedger);
        var rowKey = JournalRowKey.Encode(key);
        var survivingHandle = Nodes(cluster).First(entry => entry.Node != stoppingNode).Handle;
        var router = Services(survivingHandle).GetRequiredService<JournalRouter>();
        using (var firstBody = Payload([1, 2, 3]))
        using (var firstRecord = JournalEntryRecord.Append(rowKey, JournalVersion.Origin, firstBody))
        {
            var firstOutcome = await router.WriteAsync(key, rowKey, firstRecord, Token);
            _ = Assert.IsType<JournalWriteOutcome.Accepted>(firstOutcome.Value);
        }

        // Hold right after NodeLedgers.StopAsync finishes (before silo teardown / membership death detection).
        var held = _state.StopGate.Hold(handle.Name);
        var stop = cluster.StopSiloAsync(handle, Token);
        await held.WaitAsync(Patience, Token);

        var members = Services(survivingHandle).GetRequiredService<IClusterMembershipService>().CurrentSnapshot.Members;
        Assert.Equal(SiloStatus.Active, members[handle.SiloAddress].Status);
        var afterHandOff = await ReadMapAsync();
        Assert.DoesNotContain(afterHandOff.Ledgers, ledger => ledger.IsOpen && ledger.Primary.Node == stoppingNode);
        var successor = Assert.NotNull(afterHandOff.FindOpen(rowKey.Span));
        Assert.NotEqual(stoppingNode, successor.Primary.Node);
        Assert.Contains(stoppingNode, successor.Replicas);

        _state.StopGate.Release();
        await stop.WaitAsync(Patience, Token);

        // Surviving two silos keep serving reads and writes on the handed-off range at Q-of-N quorum!
        var readBefore = await router.ReadAsync(key, rowKey, Token);
        JournalVersion versionBefore;
        using (readBefore.Content)
        {
            versionBefore = readBefore.Version;
            var slice = readBefore.Content.Reader.PeekSlice(readBefore.Content.Length);
            try
            {
                Assert.Equal([1, 2, 3], slice.ToArray());
            }
            finally
            {
                slice.Dispose();
            }
        }

        using (var secondBody = Payload([4, 5, 6]))
        using (var secondRecord = JournalEntryRecord.Append(rowKey, versionBefore, secondBody))
        {
            var secondOutcome = await router.WriteAsync(key, rowKey, secondRecord, Token);
            _ = Assert.IsType<JournalWriteOutcome.Accepted>(secondOutcome.Value);
        }

        var readAfter = await router.ReadAsync(key, rowKey, Token);
        using (readAfter.Content)
        {
            var slice = readAfter.Content.Reader.PeekSlice(readAfter.Content.Length);
            try
            {
                Assert.Equal([1, 2, 3, 4, 5, 6], slice.ToArray());
            }
            finally
            {
                slice.Dispose();
            }
        }
    }

    /// <summary>
    /// orleans.md §5.2, §5.6: a stopping node asks to hand off before its runtime checks its view, so a node primary of
    /// nothing is pending too: held below its runtime's stage, where membership still shows its silo active, it is no
    /// replica of a ledger the controller places meanwhile, here a roll of a ledger which names it. Balancing is paused
    /// once the cluster serves, so a hand-off made here leaves the node primary of nothing.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task AStoppingSiloPrimaryOfNothingIsNoReplicaOfALedgerPlacedWhileItStops()
    {
        var cluster = await DeployAsync(4, TestContext.Current.CancellationToken);
        var map = await ServedAsync(cluster, ranges: 4, done: value => Balanced(cluster, value));
        foreach (var silo in cluster.Silos)
        {
            Services(silo).GetRequiredService<ControlOptions>().MaxConcurrentTransitions = 0;
        }

        var (stopping, _, handle) = Nodes(cluster).First(entry => entry.Handle != cluster.Primary && map.Ledgers.Any(ledger => ledger.IsOpen && ledger.Primary.Node == entry.Node));
        NodeId[] others = [.. Nodes(cluster).Select(static entry => entry.Node).Where(node => node != stopping)];
        var moved = map.Ledgers.Single(ledger => ledger.IsOpen && ledger.Primary.Node == stopping);
        var choice = await ChoiceAsync(cluster, others[0]);
        choice = new TransitionChoice(others[0], [others[0], stopping, others[1]], choice.K, choice.ChosenAt, choice.Epoch);
        _ = Assert.IsType<LedgerMapCommitOutcome.Applied>((await NodeControl(cluster, others[0]).ExecuteAsync(new Transition.Replace(moved.Id, choice))).Value);
        var gate = _state.StopGate.Hold(handle.Name);

        var stop = cluster.StopSiloAsync(handle, Token);
        await gate.WaitAsync(Patience, Token);
        var named = (await ReadMapAsync()).Ledgers.Single(ledger => ledger.IsOpen && ledger.Range == moved.Range);
        Assert.Contains(stopping, named.Replicas);
        await Controller(cluster).RequestRollAsync(named.Id);
        var after = await ServedAsync(cluster, ranges: 4, done: value => value.Get(named.Id) is not { IsOpen: true });

        Assert.Equal(SiloStatus.Active, Services(cluster.Silos[0]).GetRequiredService<IClusterMembershipService>().CurrentSnapshot.Members[handle.SiloAddress].Status);
        Assert.DoesNotContain(stopping, after.Ledgers.Single(ledger => ledger.IsOpen && ledger.Range == moved.Range).Replicas);
        _state.StopGate.Release();
        await stop.WaitAsync(Patience, Token);
    }

    /// <summary>
    /// orleans.md §5.2: a kill's stop token is cancelled from the start, and the node's first hand-off request fails; the
    /// stop still cancels the runtime's work and disposes it (steps 4 and 5), which run however steps 1 to 3 end.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task AKillWhoseHandOffRequestFailsStillStopsAndDisposesTheRuntime()
    {
        var cluster = await DeployAsync(4, TestContext.Current.CancellationToken);
        _ = await ServedAsync(cluster, ranges: 4);
        var (_, _, handle) = Nodes(cluster).First(entry => entry.Handle != cluster.Primary);
        var ledgers = Services(handle).GetRequiredService<NodeLedgers>();
        _state.FaultHandOffsFrom = handle.Name;

        await cluster.KillSiloAsync(handle, TestContext.Current.CancellationToken).WaitAsync(Patience, TestContext.Current.CancellationToken);

        Assert.True(_state.HandOffFaults > 0, "The killed silo's hand-off request never failed.");
        Assert.Equal(LocalNodeControl.ControlState.Stopped, Control(handle).State);
        Assert.Null(ledgers.Runtime);

        // Teardown's stops start from a membership which shows the killed silo dead, so none counts it a hand-off target
        // (#252).
        await EventuallyAsync(
            () => cluster.Silos.All(silo => Services(silo).GetRequiredService<IClusterMembershipService>().CurrentSnapshot.GetSiloStatus(handle.SiloAddress) == SiloStatus.Dead),
            "every survivor's membership to show the killed silo dead");
    }

    /// <summary>
    /// orleans.md §5.2: a silo stops in a bounded time while a transition it executes waits at a replica read: it waits
    /// for that admitted work within <c>TransitionTimeout</c>, then cancels it; meanwhile, handing off, it declines a new
    /// transition, touching nothing it is about to dispose.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task AStopEndsHeldWorkWithinItsTimeoutAndDeclinesTransitionsWhileItHandsOff()
    {
        var cluster = await DeployAsync(4, TestContext.Current.CancellationToken);
        var (executor, handle, heldExecution, other) = await HoldAnExecutionAsync(cluster);

        var stop = cluster.StopSiloAsync(handle, Token);
        await EventuallyAsync(() => Control(handle).State == LocalNodeControl.ControlState.HandingOff, "the stopping silo's runtime hands off");
        var declined = await NodeControl(cluster, executor).ExecuteAsync(new Transition.Replace(other.Id, await ChoiceAsync(cluster, executor)));

        Assert.Equal(TransitionReply.Declined.Instance, declined);
        await stop.WaitAsync(Patience, Token);
        Assert.Equal(LocalNodeControl.ControlState.Stopped, Control(handle).State);
        _ = await Record.ExceptionAsync(() => heldExecution.WaitAsync(Patience, Token));
    }

    /// <summary>
    /// orleans.md §5.2, ledgers.md §2.5: steps 4 and 5 of a stop take at most <c>TransitionTimeout</c> from the
    /// cancellation, and the reader's disposal stops every leased read: a tail read through the node's reader, made under
    /// no token of the runtime's, as a caller beside its control work makes it, holds its ledger's lease while its
    /// replicas' answers are held, and the runtime's disposal ends it with <see cref="ObjectDisposedException"/> and
    /// completes within the bound, leaving nothing for the silo's disposal. The silo's stop is held just below the
    /// runtime's stage, so the read is judged there, before the silo's messaging stops and fails it.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task AStopEndsALeasedReadAndDisposesItsRuntimeWithinItsBound()
    {
        var cluster = await DeployAsync(4, TestContext.Current.CancellationToken);
        var map = await ServedAsync(cluster, ranges: 4, done: value => Balanced(cluster, value));
        var (node, _, handle) = Nodes(cluster).First(entry => entry.Handle != cluster.Primary);
        var held = map.Ledgers.First(ledger => ledger.IsOpen && ledger.Primary.Node != node);
        _state.HeldLedger = held.Id;
        var reads = _state.TailReads.Hold();
        var ledgers = Services(handle).GetRequiredService<NodeLedgers>();
        var read = Assert.IsType<NodeRuntime>(ledgers.Runtime).Reader.ReadTailAsync(held, CancellationToken.None).AsTask();
        await reads.WaitAsync(Patience, Token);
        var bound = Services(handle).GetRequiredService<ControlOptions>().TransitionTimeout;
        var gate = _state.StopGate.Hold(handle.Name);

        var stop = cluster.StopSiloAsync(handle, Token);
        await EventuallyAsync(() => Control(handle).State == LocalNodeControl.ControlState.Stopped, "the stopping runtime to cancel its work");
        var cancelled = TimeProvider.System.GetTimestamp();
        await gate.WaitAsync(Patience, Token);

        Assert.InRange(TimeProvider.System.GetElapsedTime(cancelled), TimeSpan.Zero, bound + TimeSpan.FromSeconds(5));
        Assert.Null(ledgers.LateDisposal);
        _ = await Assert.ThrowsAsync<ObjectDisposedException>(() => read.WaitAsync(TimeSpan.Zero, TimeProvider.System, Token));
        _state.StopGate.Release();
        await stop.WaitAsync(Patience, Token);
        _state.TailReads.Release();
    }

    /// <summary>
    /// orleans.md §5.2: a transition admitted before its silo's hand-off began, which installs once the node's view names
    /// it primary of none, makes it primary again; the stop checks its view once its admitted work ended (step 3), and
    /// hands the late install's ledger off before it goes on. The late ledger's departure is held at the controller grain,
    /// and the stop below the runtime's stage, where membership still shows the silo active: a stop which skipped step 3
    /// would reach that gate while the map still names the node primary, and no takeover could stand in for the hand-off.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task ALateInstallWhileASiloHandsOffIsHandedOffAgain()
    {
        var cluster = await DeployAsync(4, TestContext.Current.CancellationToken);
        var (executor, handle, heldExecution, _) = await HoldAnExecutionAsync(cluster);
        var gate = _state.StopGate.Hold(handle.Name);

        var stop = cluster.StopSiloAsync(handle, Token);
        await EventuallyAsync(
            () => Control(handle).State == LocalNodeControl.ControlState.HandingOff && Runtime(handle) is { } runtime && !runtime.View.Current.Ledgers.Any(ledger => ledger.IsOpen && ledger.Primary.Node == executor),
            "the stopping silo's view names its node primary of none");
        var departure = _state.Departures.Hold();
        _state.TailReads.Release();

        var late = await heldExecution.WaitAsync(Patience, Token);
        var applied = Assert.IsType<LedgerMapCommitOutcome.Applied>(late.Value);
        await departure.WaitAsync(Patience, Token);

        // Step 3 waits for the departure, so the stop reaches its gate only once the departure is released.
        if (await Task.WhenAny(gate, Task.Delay(TimeSpan.FromSeconds(3), TimeProvider.System, Token)) != gate)
        {
            _state.Departures.Release();
            await gate.WaitAsync(Patience, Token);
        }

        var map = await ReadMapAsync();
        Assert.Equal(SiloStatus.Active, Services(cluster.Silos[0]).GetRequiredService<IClusterMembershipService>().CurrentSnapshot.Members[handle.SiloAddress].Status);
        Assert.True(map.Epoch > applied.Epoch);
        Assert.DoesNotContain(map.Ledgers, ledger => ledger.IsOpen && ledger.Primary.Node == executor);
        _state.Departures.Release();
        _state.StopGate.Release();
        await stop.WaitAsync(Patience, Token);
    }

    /// <summary>
    /// orleans.md §5.7, R2, OR-I19: once membership declares a killed silo dead, the controller takes each ledger its node
    /// was primary of over through a live node, the remaining silos serve every open range, and every surviving silo's
    /// view, which reads the map only once an hour, reaches the register's epoch through its reports.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task AKilledSilosLedgersAreTakenOverAndEverySurvivorFollowsTheMap()
    {
        var cluster = await DeployAsync(4, fastMembership: true, TestContext.Current.CancellationToken);
        var (node, handle) = await PrimaryAsync(cluster, silo => silo != cluster.Primary);

        await cluster.KillSiloAsync(handle, Token);

        var after = await ServedAsync(cluster, ranges: 3, done: value => !value.Ledgers.Any(ledger => ledger.IsOpen && ledger.Primary.Node == node));
        Assert.DoesNotContain(after.Ledgers, ledger => ledger.IsOpen && ledger.Primary.Node == node);
        _ = await EverySiloFollowsTheMapAsync(cluster);
    }

    /// <summary>
    /// orleans.md §5.6, R3, OR-I19: while a sweep is held in its listing and a manifest replace is held at the store, a
    /// killed silo's ledgers are taken over and every open range is served; the replace then applies and the sweep
    /// completes, and every survivor's view reaches the register's epoch through its reports.
    /// </summary>
    /// <remarks>
    /// The held drain is shown held at the store and in the manifest, not by the test's call: Orleans ends a call whose
    /// response does not arrive within its response timeout, however long its grain works on it, and the kill and the
    /// takeover may outlast it (#245). The test's client gets a short response timeout, and the replace stays held until
    /// the call has timed out, so every run holds the commit past its caller's wait.
    /// </remarks>
    [Fact(Timeout = 180_000)]
    public async Task ATakeoverInstallsWhileASweepAndAManifestReplaceAreHeld()
    {
        _state.ClientResponseTimeout = TimeSpan.FromSeconds(10);
        var cluster = await DeployAsync(4, fastMembership: true, TestContext.Current.CancellationToken);
        _ = await ServedAsync(cluster, ranges: 4);
        var listing = _state.Listings.Hold();
        var replace = _state.ManifestReplaces.Hold();
        await listing.WaitAsync(Patience, Token);
        var sweeper = _state.Listings.HeldAt;
        var sweeps = Services(cluster.Silos.Single(silo => silo.Name == sweeper)).GetRequiredService<SweepHost>();
        var swept = sweeps.Sweeps;

        // A ledger the map never names, so no node's drain decides the change first.
        var ledger = new LedgerId(1_000_000);
        var tag = new Tag(ledger, Slot.Zero, 0);
        var section = new SectionRef(new ObjectName("d/1000000/0/held"), 0, 10, KeyRange.Root, tag, tag, new ObjectRange(10, 1));
        var drained = Controller(cluster).CommitManifestAsync(new ManifestChange.Drain(ledger, Slot.Zero, new Slot(1), [section], complete: false));
        await replace.WaitAsync(Patience, Token);

        // Neither the sweep's silo nor the controller grain's, whose stop would end the held commit.
        var controller = await cluster.Client.GetGrain<IManagementGrain>(0).GetActivationAddress(Controller(cluster), Token);
        var (node, handle) = await PrimaryAsync(cluster, silo => silo != cluster.Primary && silo.Name != sweeper && silo.SiloAddress != controller);
        await cluster.KillSiloAsync(handle, Token);
        _ = await ServedAsync(cluster, ranges: 3, done: value => !value.Ledgers.Any(ledger => ledger.IsOpen && ledger.Primary.Node == node));

        var timedOut = await Assert.ThrowsAsync<TimeoutException>(async () => await drained.WaitAsync(Patience, Token));
        Assert.Contains("Response did not arrive on time", timedOut.Message, StringComparison.Ordinal);
        Assert.Equal(Slot.Zero, (await _state.StoredManifestAsync())?.RecordedWatermark(ledger) ?? Slot.Zero);
        Assert.Equal(controller, await cluster.Client.GetGrain<IManagementGrain>(0).GetActivationAddress(Controller(cluster), Token));

        Assert.Equal(swept, sweeps.Sweeps);
        _state.ManifestReplaces.Release();
        _state.Listings.Release();
        await EventuallyAsync(async () => await _state.StoredManifestAsync() is { } stored && stored.RecordedWatermark(ledger) == new Slot(1), "the held drain to apply");
        await EventuallyAsync(() => sweeps.Sweeps > swept, "the held sweep completes");
        _ = await EverySiloFollowsTheMapAsync(cluster);
    }

    /// <summary>
    /// journals.md §2.1, §8.3, JS-I1, #352: a grain whose journal's ledger rolls, drains, and drops writes on at the version
    /// its activation holds, read before the drop by the activation which wrote it or by a new one, and every read gives
    /// the version the next write is accepted at. No write intervenes, so the version a read answers names the journal's
    /// current state for the next conditional write.
    /// </summary>
    /// <param name="reactivateBeforeTheDrop">Whether a new activation reads the journal after its drain and before its drop, as a grain recovering after a takeover does.</param>
    /// <param name="archiveStore">The cluster's archive store, into which the ledger drains before it drops.</param>
    [Theory(Timeout = 180_000)]
    [InlineData(false, ArchiveStoreKind.Lsm)]
    [InlineData(true, ArchiveStoreKind.Lsm)]
    [InlineData(false, ArchiveStoreKind.Logs)]
    [InlineData(true, ArchiveStoreKind.Logs)]
    public async Task AGrainWritesOnAtTheVersionItReadAcrossItsLedgersDrop(bool reactivateBeforeTheDrop, ArchiveStoreKind archiveStore)
    {
        _state.ArchiveStore = archiveStore;
        var (_, grain, _) = await DropTheLedgerOfAGrainAsync(reactivateBeforeTheDrop, TestContext.Current.CancellationToken);

        Assert.Equal(2, await grain.AppendAsync([2]));
        Assert.Equal(3, await grain.AppendAsync([3]));
        Assert.Equal([1, 2, 3], await ReadJournalAsync(grain));
    }

    /// <summary>
    /// OR-I7, #352: a grain whose journal another writer changed after its ledger dropped is refused its write with
    /// <see cref="InconsistentStateException"/>: its storage reads the journal once after the rejection, finds content other
    /// than its own, and sends nothing more.
    /// </summary>
    /// <param name="archiveStore">The cluster's archive store, into which the ledger drains before it drops.</param>
    [Theory(Timeout = 180_000)]
    [InlineData(ArchiveStoreKind.Lsm)]
    [InlineData(ArchiveStoreKind.Logs)]
    public async Task AGrainWhoseJournalAnotherWriterChangedAfterItsLedgersDropIsRefused(ArchiveStoreKind archiveStore)
    {
        _state.ArchiveStore = archiveStore;
        var (_, grain, primary) = await DropTheLedgerOfAGrainAsync(reactivateBeforeTheDrop: false, TestContext.Current.CancellationToken);
        var key = JournalId.FromGrainId(grain.GetGrainId()).Value;
        var rowKey = JournalRowKey.Encode(key);
        var router = Services(primary).GetRequiredService<JournalRouter>();
        var read = await router.ReadAsync(key, rowKey, Token);
        read.Content.Dispose();
        using (var payload = Payload([7]))
        using (var record = JournalEntryRecord.Append(rowKey, read.Version, payload))
        {
            _ = Assert.IsType<JournalWriteOutcome.Accepted>((await router.WriteAsync(key, rowKey, record, Token)).Value);
        }

        _ = await Assert.ThrowsAsync<InconsistentStateException>(() => grain.AppendAsync([2]));
    }

    /// <summary>
    /// journals.md §8.6, JS-I7, orleans.md §5.6: a grain's journal deleted on its primary is expired by the primary's expiry
    /// scan, which the node's control loop starts each <c>ExpiryInterval</c>, once its tombstone's retention has passed: a
    /// read then answers it absent at the version after its <c>Delete</c>, which only an <c>Expired</c> gives, and a new
    /// activation of the grain reads it empty and writes on from it.
    /// </summary>
    /// <param name="archiveStore">The cluster's archive store.</param>
    [Theory(Timeout = 180_000)]
    [InlineData(ArchiveStoreKind.Lsm)]
    [InlineData(ArchiveStoreKind.Logs)]
    public async Task APrimaryExpiresADeletedGrainJournalOnceItsRetentionHasPassed(ArchiveStoreKind archiveStore)
    {
        _state.ArchiveStore = archiveStore;
        _state.Node = static (_, node) =>
        {
            node.Journals.TombstoneRetention = TimeSpan.FromSeconds(1);
            node.Journals.ExpiryInterval = TimeSpan.FromMilliseconds(100);
        };
        var cluster = await DeployAsync(3, TestContext.Current.CancellationToken);
        _ = await ServedAsync(cluster, ranges: 3, done: value => Balanced(cluster, value));
        await EventuallyAsync(() => Nodes(cluster).All(entry => _state.Journals[entry.Handle.Name].State == LocalJournals.JournalsState.Serving), "every silo's Journals to serve");
        var silo = cluster.Silos[0];
        var grain = Services(silo).GetRequiredService<IGrainFactory>().GetGrain<IJournaledChunksGrain>("expired");
        Assert.Equal(1, await grain.AppendAsync([1]));
        var key = JournalId.FromGrainId(grain.GetGrainId()).Value;
        var rowKey = JournalRowKey.Encode(key);
        var router = Services(silo).GetRequiredService<JournalRouter>();
        JournalVersion deleted;
        using (var record = JournalEntryRecord.Delete(rowKey, await ReadVersionAsync(), TimeProvider.System.GetUtcNow()))
        {
            deleted = Assert.IsType<JournalWriteOutcome.Accepted>((await router.WriteAsync(key, rowKey, record, Token)).Value).Version;
        }

        await EventuallyAsync(async () => await ReadVersionAsync() == deleted.Next(), "the primary's expiry scan to expire the deleted journal");
        var activation = await grain.ActivationAsync();
        await grain.DeactivateAsync();
        await EventuallyAsync(async () => await grain.ActivationAsync() != activation, "a new activation of the grain");
        Assert.Empty(await grain.ReadAsync());
        Assert.Equal(1, await grain.AppendAsync([2]));
        Assert.Equal([2], await ReadJournalAsync(grain));

        async Task<JournalVersion> ReadVersionAsync()
        {
            var read = await router.ReadAsync(key, rowKey, Token);
            read.Content.Dispose();
            return read.Version;
        }
    }

    /// <summary>
    /// journals.md §8.6, orleans.md §5.6, §5.2: a node's control loop starts an expiry scan at most once each
    /// <c>ExpiryInterval</c> (none a tick before it, one once it elapses, and none on later control passes before another
    /// interval elapses); starts none while one runs, here held as it starts while the clock advances past several
    /// intervals; and its stop awaits the held scan with the loop, so the cancelled work outlasts the stop's bound and logs
    /// its steps and warning in order, which a loop which left the scan behind would not.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task ANodeRunsOneExpiryScanAtATimeAtMostOncePerIntervalAndItsStopAwaitsIt()
    {
        var expiryInterval = TimeSpan.FromSeconds(1);
        var clock = new SteppableUtcTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        _state.Node = (_, node) => node.Journals.ExpiryInterval = expiryInterval;
        _state.Silo = (silo, builder) =>
        {
            if (silo == "Secondary_2")
            {
                _ = builder.Services.AddSingleton<TimeProvider>(clock);
            }
        };
        var cluster = await DeployAsync(3, TestContext.Current.CancellationToken);
        _ = await ServedAsync(cluster, ranges: 3, done: value => Balanced(cluster, value));
        await EventuallyAsync(() => Nodes(cluster).All(entry => _state.Journals[entry.Handle.Name].State == LocalJournals.JournalsState.Serving), "every silo's Journals to serve");
        var handle = cluster.Silos.Single(static silo => silo.Name == "Secondary_2");
        var node = _state.LocalNodes[handle.Name].Node;
        var journals = Assert.IsType<NodeRuntime>(Runtime(handle)).Journals;

        // No scan starts before ExpiryInterval elapses, even across several control passes and a tick short of the interval.
        clock.Advance(expiryInterval - TimeSpan.FromTicks(1));
        await WaitForControlPassesAsync(3);
        Assert.Equal(0, journals.ExpiryScans);

        // Reaching ExpiryInterval starts one scan; further control passes before another interval elapses start none.
        clock.Advance(TimeSpan.FromTicks(1));
        await EventuallyAsync(() => journals.ExpiryScans == 1, $"{handle.Name}'s first expiry scan");
        await WaitForControlPassesAsync(3);
        Assert.Equal(1, journals.ExpiryScans);

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        journals.BeforeExpiryScan = () =>
        {
            _ = started.TrySetResult();
            return hold.Task;
        };
        try
        {
            clock.Advance(expiryInterval);
            await started.Task.WaitAsync(Patience, Token);
            var held = journals.ExpiryScans;
            Assert.Equal(2, held);

            clock.Advance(expiryInterval * 5);
            await WaitForControlPassesAsync(3);
            Assert.Equal(held, journals.ExpiryScans);

            await cluster.StopSiloAsync(handle, TestContext.Current.CancellationToken).WaitAsync(Patience, Token);
            var events = _state.StopEvents.Where(line => line.Contains($"{handle.Name}:", StringComparison.Ordinal)).ToList();
            var cancelBegins = events.FindIndex(static line => line.Contains("stop: step Cancel Begins", StringComparison.Ordinal));
            var outlasted = events.FindIndex(static line => line.Contains("Journals' outstanding writes outlasted", StringComparison.Ordinal));
            var cancelEnds = events.FindIndex(static line => line.Contains("stop: step Cancel Ends", StringComparison.Ordinal));
            var disposeBegins = events.FindIndex(static line => line.Contains("stop: step Dispose Begins", StringComparison.Ordinal));
            Assert.True(cancelBegins >= 0, $"Missing Cancel Begins in {handle.Name}'s stop events:{Environment.NewLine}{string.Join(Environment.NewLine, events)}");
            Assert.True(outlasted > cancelBegins, $"Expected cancelled-work outlasted warning after Cancel Begins in {handle.Name}'s stop events:{Environment.NewLine}{string.Join(Environment.NewLine, events)}");
            Assert.True(cancelEnds > outlasted, $"Expected Cancel Ends after outlasted warning in {handle.Name}'s stop events:{Environment.NewLine}{string.Join(Environment.NewLine, events)}");
            Assert.True(disposeBegins > cancelEnds, $"Expected Dispose Begins after Cancel Ends in {handle.Name}'s stop events:{Environment.NewLine}{string.Join(Environment.NewLine, events)}");
            foreach (var step in new[] { "HandOff", "AdmittedWork", "HandOffAgain" })
            {
                foreach (var phase in new[] { "Begins", "Ends" })
                {
                    Assert.Contains(events, line => line.Contains($"stop: step {step} {phase}", StringComparison.Ordinal));
                }
            }
        }
        finally
        {
            _ = hold.TrySetResult();
        }

        async Task WaitForControlPassesAsync(int passes)
        {
            var target = _state.ReportCounts.GetValueOrDefault(node) + passes;
            await EventuallyAsync(() => _state.ReportCounts.GetValueOrDefault(node) >= target, $"{handle.Name} to run {passes} control passes");
        }
    }

    /// <summary>
    /// Writes one chunk through a grain on the primary's silo of an open ledger, rolls that ledger, and drops it once it has
    /// drained, a new activation reading the journal between its drain and its drop when <paramref name="reactivateBeforeTheDrop"/>.
    /// </summary>
    private async Task<(TestCluster Cluster, IJournaledChunksGrain Grain, SiloHandle Primary)> DropTheLedgerOfAGrainAsync(bool reactivateBeforeTheDrop, CancellationToken cancellationToken)
    {
        var cluster = await DeployAsync(3, cancellationToken);
        _ = await ServedAsync(cluster, ranges: 3, done: value => Balanced(cluster, value));
        var map = await EverySiloFollowsTheMapAsync(cluster);
        await EventuallyAsync(() => Nodes(cluster).All(entry => _state.Journals[entry.Handle.Name].State == LocalJournals.JournalsState.Serving), "every silo's Journals to serve");
        var ledger = map.Ledgers.First(static ledger => ledger.IsOpen);
        var primary = Nodes(cluster).Single(entry => entry.Node == ledger.Primary.Node).Handle;
        var factory = Services(primary).GetRequiredService<IGrainFactory>();
        var grainKey = Enumerable.Range(0, int.MaxValue)
            .Select(static index => string.Create(CultureInfo.InvariantCulture, $"drop-{index}"))
            .First(key => ledger.Range.Contains(JournalRowKey.Encode(JournalId.FromGrainId(factory.GetGrain<IJournaledChunksGrain>(key).GetGrainId()).Value).Span));
        var grain = factory.GetGrain<IJournaledChunksGrain>(grainKey);
        Assert.Equal(1, await grain.AppendAsync([1]));

        _state.KeptLedgers = [ledger.Id];
        await Controller(cluster).RequestRollAsync(ledger.Id).WaitAsync(Patience, Token);
        await EventuallyAsync(async () => (await ReadMapAsync()).Get(ledger.Id) is { } closed && await _state.DrainedAsync(closed, Token), $"s{ledger.Id}'s drain to complete");
        if (reactivateBeforeTheDrop)
        {
            var activation = await grain.ActivationAsync();
            await grain.DeactivateAsync();
            await EventuallyAsync(async () => await grain.ActivationAsync() != activation, "a new activation of the grain");
            Assert.Equal([1], await grain.ReadAsync());
        }

        _state.KeptLedgers = [];
        await EventuallyAsync(async () => (await ReadMapAsync()).Get(ledger.Id) is null, $"s{ledger.Id} to drop");
        _ = await EverySiloFollowsTheMapAsync(cluster);
        return (cluster, grain, primary);
    }

    /// <summary>
    /// orleans.md §5.5, §5.9: a read of a journal whose rows lie in the archive waits for the object store while the store
    /// refuses every call, on the primary's silo and forwarded from another alike, and completes with the journal's content
    /// once the store answers. The journal's ledger rolls and drains, and its drop is kept back until the store refuses, so
    /// once the drop lands every silo's history read must read the manifest, which the store refuses.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task AReadOfAJournalInTheArchiveWaitsForTheStoreAndCompletesOnceItAnswers()
    {
        var cluster = await DeployAsync(3, TestContext.Current.CancellationToken);
        _ = await ServedAsync(cluster, ranges: 3, done: value => Balanced(cluster, value));
        var map = await EverySiloFollowsTheMapAsync(cluster);
        await EventuallyAsync(() => Nodes(cluster).All(entry => _state.Journals[entry.Handle.Name].State == LocalJournals.JournalsState.Serving), "every silo's Journals to serve");
        var ledger = map.Ledgers.First(static ledger => ledger.IsOpen);
        var primary = Nodes(cluster).Single(entry => entry.Node == ledger.Primary.Node).Handle;
        var other = cluster.Silos.First(silo => silo != primary);
        var key = KeyIn(ledger);
        var rowKey = JournalRowKey.Encode(key);
        using (var payload = Payload([1, 2, 3]))
        using (var record = JournalEntryRecord.Append(rowKey, JournalVersion.Origin, payload))
        {
            _ = Assert.IsType<JournalWriteOutcome.Accepted>((await Services(primary).GetRequiredService<JournalRouter>().WriteAsync(key, rowKey, record, Token)).Value);
        }

        _state.KeptLedgers = [ledger.Id];
        await Controller(cluster).RequestRollAsync(ledger.Id).WaitAsync(Patience, Token);
        await EventuallyAsync(async () => await _state.StoredManifestAsync() is { } stored && stored.IsComplete(ledger.Id), $"s{ledger.Id}'s drain to complete");
        _state.RefuseObjects = true;
        _state.KeptLedgers = [];
        await EventuallyAsync(async () => (await ReadMapAsync()).Get(ledger.Id) is null, $"s{ledger.Id} to drop");
        _ = await EverySiloFollowsTheMapAsync(cluster);

        var local = Services(primary).GetRequiredService<JournalRouter>().ReadAsync(key, rowKey, Token);
        var forwarded = Services(other).GetRequiredService<JournalRouter>().ReadAsync(key, rowKey, Token);
        await Task.Delay(TimeSpan.FromSeconds(1), TimeProvider.System, Token);
        Assert.False(local.IsCompleted, $"The local read completed while the store refused every call: {local.Status}.");
        Assert.False(forwarded.IsCompleted, $"The forwarded read completed while the store refused every call: {forwarded.Status}.");

        _state.RefuseObjects = false;

        foreach (var read in new[] { local, forwarded })
        {
            var answered = await read.WaitAsync(Patience, Token);
            using (answered.Content)
            {
                var content = answered.Content.Reader.PeekSlice(answered.Content.Length);
                try
                {
                    Assert.Equal([1, 2, 3], content.ToArray());
                }
                finally
                {
                    content.Dispose();
                }
            }
        }
    }

    /// <summary>
    /// orleans.md §5.6, §5.9: the controller grain's activation reads only the map, so while the object store refuses every
    /// call, silos joining the cluster register at once with the identity its registry records, the controller bootstraps
    /// and splits, and a killed silo's ledgers are taken over. Only a new cluster's first registration waits for the store.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task TheControllerServesWhileTheObjectStoreRefusesEveryCall()
    {
        var cluster = await DeployAsync(1, fastMembership: true, TestContext.Current.CancellationToken);
        _state.RefuseObjects = true;
        _ = await cluster.StartAdditionalSilosAsync(3, startAdditionalSiloOnNewPort: false, Token);
        await EveryResolverListsEveryNodeAsync(cluster);
        var (node, handle) = await PrimaryAsync(cluster, silo => silo != cluster.Primary);

        await cluster.KillSiloAsync(handle, Token);

        _ = await ServedAsync(cluster, ranges: 3, done: value => !value.Ledgers.Any(ledger => ledger.IsOpen && ledger.Primary.Node == node));
    }

    /// <summary>
    /// conventions.md §12: the controller grain builds its node registry, ledger map, and manifest committers inside the
    /// call which activates it, under that call's <see cref="RequestContext"/> and Orleans' activation Activity; their
    /// batches serve every node, so the registry's and the map's writes and the manifest's replaces run under neither.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task TheControllersCommittersRunInNoCallersContext()
    {
        var cluster = await DeployAsync(1, TestContext.Current.CancellationToken);
        _ = await Nodes(cluster)[0].Local.Registered.WaitAsync(Patience, Token);
        using var source = new ActivitySource("Orleans.Durable.Storage.Tests.CommittingCaller");
        using var listener = new ActivityListener
        {
            ShouldListenTo = static _ => true,
            Sample = static (ref _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);
        _state.RecordContexts = true;

        // Another key's activation, whose activating call is this registration.
        var controller = cluster.Client.GetGrain<IClusterControllerGrain>(IClusterControllerGrain.Key + 1);
        RequestContext.Set(TestState.ProbeKey, "committing caller");
        try
        {
            using (source.StartActivity("committing caller"))
            {
                var registered = Nodes(cluster)[0].Handle.SiloAddress;
                _ = Assert.IsType<NodeRegistrationOutcome.Registered>((await controller.RegisterNodeAsync(new NodeRegistryEntry(Node(77), registered), await RegisteredStoreAsync(), nodeToReplace: null)).Value);
                _ = Assert.IsType<LedgerMapCommitOutcome.Applied>((await controller.CommitLedgerMapAsync(new LedgerMapChange.RaiseFloor(1))).Value);
                var tag = new Tag(new LedgerId(1), Slot.Zero, 0);
                var section = new SectionRef(new ObjectName("d/1/0/probe"), 0, 10, KeyRange.Root, tag, tag, new ObjectRange(10, 1));
                _ = Assert.IsType<ManifestCommitOutcome.Applied>((await controller.CommitManifestAsync(new ManifestChange.Drain(new LedgerId(1), Slot.Zero, new Slot(1), [section], complete: false))).Value);
            }
        }
        finally
        {
            RequestContext.Clear();
        }

        var writes = _state.Writes;
        Assert.Contains(writes, static write => write.Target == NodeRegistryValue.RegisterName);
        Assert.Contains(writes, static write => write.Target == LedgerMapRegister.Name);
        Assert.Contains(writes, static write => write.Target == ObjectName.Manifest.Value);
        Assert.All(writes, static write => Assert.Equal((null, null), (write.Probe, write.Activity)));
    }

    /// <summary>
    /// The node's control runtime interleaves its calls at awaits on its exclusive scheduler (orleans.md §7.2): while a
    /// compaction waits at its manifest read, a transition sent to the same node runs and is answered, and the compaction
    /// then completes.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task ATransitionRunsWhileACompactionOfTheSameNodeIsHeld()
    {
        var cluster = await DeployAsync(1, TestContext.Current.CancellationToken);
        var node = Nodes(cluster)[0].Node;
        _ = await Nodes(cluster)[0].Local.Registered.WaitAsync(Patience, Token);
        var tag = new Tag(new LedgerId(1), Slot.Zero, 0);
        var section = new SectionRef(new ObjectName("d/1/0/drained"), 0, 10, KeyRange.Root, tag, tag, new ObjectRange(10, 1));
        var drained = await Controller(cluster).CommitManifestAsync(new ManifestChange.Drain(new LedgerId(1), Slot.Zero, new Slot(1), [section], complete: false));
        var epoch = Assert.IsType<ManifestCommitOutcome.Applied>(drained.Value).Epoch;
        var grain = NodeControl(cluster, node);
        var resolver = Services(cluster.Silos[0]).GetRequiredService<NodeRegistryResolver>();
        await EventuallyAsync(() => resolver.Resolve(node) is not null, "the silo's resolver names its node's silo");

        var held = _state.ManifestReads.Hold();
        Task<CompactionReply> compaction;
        RequestContext.Set(TestState.ProbeKey, TestState.HeldRead);
        try
        {
            compaction = grain.CompactAsync(new CompactionAssignment(KeyRange.Root, node, epoch));
        }
        finally
        {
            RequestContext.Clear();
        }

        await held.WaitAsync(Patience, Token);
        var other = Node(99);
        var choice = new TransitionChoice(other, [other, node, Node(98)], 4, 1, 0);
        var reply = await grain.ExecuteAsync(new Transition.Bootstrap(choice)).WaitAsync(Patience, Token);

        Assert.False(compaction.IsCompleted);
        Assert.Equal(TransitionReply.Declined.Instance, reply);
        _state.ManifestReads.Release();
        Assert.Equal(CompactionReply.NothingToCompact.Instance, await compaction.WaitAsync(Patience, Token));
    }

    /// <summary>
    /// orleans.md §5.5, §7.2, OR-I17: a node's journal grain writes forwarded entries through its Journals, and reads a
    /// journal longer than one page as one snapshot: its first page, then each later page, from any offset, in order, with
    /// a write landing between pages changing none of them, until the last page releases the snapshot, after which a page
    /// answers <c>Expired</c> and a new read sees the write. A node which is not the key's primary answers a read and a
    /// write <c>Moved</c> naming the primary, and the reader's release of an abandoned read's snapshot releases it.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task AForwardedReadOfMoreThanOnePageReadsOneSnapshotWhileWritesLand()
    {
        var cluster = await DeployAsync(3, TestContext.Current.CancellationToken);
        _ = await ServedAsync(cluster, ranges: 3, done: value => Balanced(cluster, value));
        const string Key = "a journal of three pages";

        // A node which is not the primary answers from its own view, which a map read here may be ahead of.
        var map = await EverySiloFollowsTheMapAsync(cluster);
        var node = Assert.NotNull(map.FindOpen(JournalRowKey.Encode(Key).Span)).Primary.Node;
        var handle = Nodes(cluster).Single(entry => entry.Node == node).Handle;
        await EventuallyAsync(() => _state.Journals[handle.Name].State == LocalJournals.JournalsState.Serving, "the primary's Journals to serve");
        var grain = JournalNode(cluster, node);
        byte[][] bodies = [.. Enumerable.Range(1, 3).Select(static index => Enumerable.Repeat((byte)index, 700 << 10).ToArray())];
        var version = JournalVersion.Origin;
        foreach (var body in bodies)
        {
            version = Assert.IsType<JournalWriteOutcome.Accepted>((await ForwardAsync(grain, Key, version, body)).Value).Version;
        }

        var other = JournalNode(cluster, Nodes(cluster).First(entry => entry.Node != node).Node);
        Assert.Equal(new JournalMoved(node), (await other.ReadAsync(Key, Token)).Value);
        Assert.Equal(new JournalMoved(node), (await ForwardAsync(other, Key, version, [7])).Value);

        var found = Assert.IsType<JournalForwardedRead.Found>((await grain.ReadAsync(Key, Token)).Value);
        Assert.Equal((version, true, 3L * (700 << 10)), (found.Version, found.Exists, found.ContentLength));
        var snapshot = Assert.NotNull(found.Snapshot);
        List<byte> content = [.. Take(found.FirstPage)];
        var after = Assert.IsType<JournalWriteOutcome.Accepted>((await ForwardAsync(grain, Key, version, [9])).Value).Version;
        while (content.Count < found.ContentLength)
        {
            content.AddRange(Take(Assert.IsType<JournalForwardedPage.Page>((await grain.ReadPageAsync(snapshot, content.Count, Token)).Value).Bytes));
        }

        Assert.Equal(bodies.SelectMany(static body => body), content);
        Assert.Equal(JournalForwardedPage.Expired.Instance, (await grain.ReadPageAsync(snapshot, JournalForwardedRead.PageBytes, Token)).Value);
        var again = Assert.IsType<JournalForwardedRead.Found>((await grain.ReadAsync(Key, Token)).Value);
        Assert.Equal((after, found.ContentLength + 1), (again.Version, again.ContentLength));
        var read = Take(again.FirstPage).Length;
        byte[] last = [];
        while (read < again.ContentLength)
        {
            last = Take(Assert.IsType<JournalForwardedPage.Page>((await grain.ReadPageAsync(Assert.NotNull(again.Snapshot), read, Token)).Value).Bytes);
            read += last.Length;
        }

        Assert.Equal(9, last[^1]);
        Assert.Equal(0, Services(handle).GetRequiredService<ForwardedReads>().Pinned);

        var abandoned = Assert.IsType<JournalForwardedRead.Found>((await grain.ReadAsync(Key, Token)).Value);
        abandoned.FirstPage.Dispose();
        await grain.ReleaseSnapshotAsync(Assert.NotNull(abandoned.Snapshot));

        // Well within the snapshot's window, so only the release, not its expiry, meets this bound.
        var released = TimeProvider.System.GetTimestamp();
        while (Services(handle).GetRequiredService<ForwardedReads>().Pinned != 0)
        {
            Assert.True(TimeProvider.System.GetElapsedTime(released) < TimeSpan.FromSeconds(5), "The abandoned read's snapshot was not released within 5 seconds.");
            await Task.Delay(TimeSpan.FromMilliseconds(20), TimeProvider.System, Token);
        }
        Assert.Equal(JournalForwardedPage.Expired.Instance, (await grain.ReadPageAsync(abandoned.Snapshot.Value, JournalForwardedRead.PageBytes, Token)).Value);

        static byte[] Take(OrleansBuffer bytes)
        {
            try
            {
                return bytes.ToArray();
            }
            finally
            {
                bytes.Dispose();
            }
        }
    }

    /// <summary>
    /// orleans.md §5.5, R4: a journal written through a silo whose node is not its primary lands on the primary, forwarded
    /// through its node grain, and reads back whole through another silo, forwarded in pages of one snapshot, and on the
    /// primary itself from its own Journals, each read reporting the version the last write was accepted at.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task AJournalWrittenThroughAnySiloReadsBackThroughEverySilo()
    {
        var cluster = await DeployAsync(3, TestContext.Current.CancellationToken);
        var map = await ServedAsync(cluster, ranges: 3, done: value => Balanced(cluster, value));
        const string Key = "a routed journal";
        var primary = Assert.NotNull(map.FindOpen(JournalRowKey.Encode(Key).Span)).Primary.Node;
        var nodes = Nodes(cluster);
        var writer = nodes.First(entry => entry.Node != primary).Handle;
        var reader = nodes.Single(entry => entry.Node != primary && entry.Handle != writer).Handle;
        var owner = nodes.Single(entry => entry.Node == primary).Handle;
        await EventuallyAsync(() => nodes.All(entry => _state.Journals[entry.Handle.Name].State == LocalJournals.JournalsState.Serving), "every silo's Journals to serve");
        byte[][] bodies = [[.. Enumerable.Repeat((byte)1, 900 << 10)], [.. Enumerable.Repeat((byte)2, 900 << 10)]];
        var version = JournalVersion.Origin;
        foreach (var body in bodies)
        {
            using var payload = Payload(body);
            using var record = JournalEntryRecord.Append(JournalRowKey.Encode(Key), version, payload);
            version = Assert.IsType<JournalWriteOutcome.Accepted>((await Services(writer).GetRequiredService<JournalRouter>().WriteAsync(Key, JournalRowKey.Encode(Key), record, Token)).Value).Version;
        }

        foreach (var handle in new[] { reader, owner, writer })
        {
            var read = await Services(handle).GetRequiredService<JournalRouter>().ReadAsync(Key, JournalRowKey.Encode(Key), Token);
            using (read.Content)
            {
                var content = read.Content.Reader.PeekSlice(read.Content.Length);
                try
                {
                    Assert.Equal(bodies.SelectMany(static body => body), content.ToArray());
                }
                finally
                {
                    content.Dispose();
                }

                Assert.Equal((version, true), (read.Version, read.Exists));
            }
        }
    }

    /// <summary>
    /// orleans.md §5.5, §8.1: forwarded reads of a 64 MiB journal through Orleans journal storage on a silo whose node is
    /// not the journal's primary, one reader and then four at once, each for eight seconds over three runs. It reports each
    /// read's rate, the bytes allocated per byte read on each heap, the CPU per GiB, the collections of each generation a
    /// second and the generation-2 collections' causes, and the owner's peak reserved bytes against
    /// <c>MaxForwardedReadBytes</c>. Each run counts every read which completed, the last ones after its eight seconds, and
    /// one read before the runs warms the path. The consumer skips the bytes, parsing no entries, so Orleans' apply cost is
    /// excluded. Every silo shares the process, so the allocations are the cluster's, the owner's history reads and page
    /// copies among them.
    /// </summary>
    [Fact(Explicit = true)]
    public async Task ForwardedReadsOfALargeJournal()
    {
        const int EntryBytes = 4 << 20;
        const int Entries = 16;
        const string Key = "a large journal";
        _state.Node = static (_, node) => node.WalFileSize = 1L << 30;
        var cluster = await DeployAsync(3, Token);
        var map = await ServedAsync(cluster, ranges: 3, done: value => Balanced(cluster, value));
        var primary = Assert.NotNull(map.FindOpen(JournalRowKey.Encode(Key).Span)).Primary.Node;
        var nodes = Nodes(cluster);
        var owner = nodes.Single(entry => entry.Node == primary).Handle;
        var reader = nodes.First(entry => entry.Node != primary).Handle;
        await EventuallyAsync(() => nodes.All(entry => _state.Journals[entry.Handle.Name].State == LocalJournals.JournalsState.Serving), "every silo's Journals to serve");
        var version = JournalVersion.Origin;
        var body = new byte[EntryBytes];
        for (var entry = 0; entry < Entries; entry++)
        {
            using var payload = Payload(body);
            using var record = JournalEntryRecord.Append(JournalRowKey.Encode(Key), version, payload);
            version = Assert.IsType<JournalWriteOutcome.Accepted>((await Services(owner).GetRequiredService<JournalRouter>().WriteAsync(Key, JournalRowKey.Encode(Key), record, Token)).Value).Version;
        }

        var storage = Services(reader).GetRequiredService<DurableJournalStorageProvider>();
        var reads = Services(owner).GetRequiredService<ForwardedReads>();
        await storage.CreateStorage(new JournalId(Key)).ReadAsync(new SkippingConsumer(), Token);
        MeasurementSample.Report($"GC: server {GCSettings.IsServerGC}, latency mode {GCSettings.LatencyMode}; {Environment.ProcessorCount} cores; {await ArchivedBytesAsync(owner, Key) / MeasurementSample.Mebibyte} MiB of the journal in the archive, the rest in its open ledger's live rows");
        foreach (var readers in (int[])[1, 4])
        {
            var samples = new List<MeasurementSample>();
            for (var run = 0; run < 3; run++)
            {
                samples.Add(await ForwardedReadsAsync(storage, reads, Key, (long)EntryBytes * Entries, readers, TimeSpan.FromSeconds(8)));
            }

            MeasurementSample.Report(string.Create(
                CultureInfo.InvariantCulture,
                $"{readers} forwarded reader(s) of a {EntryBytes * Entries / MeasurementSample.Mebibyte} MiB journal, n = {samples.Count}: each read {MeasurementSample.Spread(samples.Select(sample => sample.MebibytesPerSecond / readers))} MiB/s; per byte read, allocated {MeasurementSample.Spread(samples.Select(static sample => sample.TotalBytesPerByte), "F2")} bytes, large-object-heap {MeasurementSample.Spread(samples.Select(static sample => sample.LargeObjectBytesPerByte), "F3")}, pinned-object-heap {MeasurementSample.Spread(samples.Select(static sample => sample.PinnedObjectBytesPerByte), "F3")}; CPU {MeasurementSample.Spread(samples.Select(static sample => sample.CpuSecondsPerGibibyte), "F1")} s per GiB; collections per s, generation 0 {MeasurementSample.Spread(samples.Select(static sample => sample.Gen0PerSecond), "F1")}, 1 {MeasurementSample.Spread(samples.Select(static sample => sample.Gen1PerSecond), "F1")}, 2 {MeasurementSample.Spread(samples.Select(static sample => sample.Gen2PerSecond), "F2")}; working set peak {MeasurementSample.Spread(samples.Select(static sample => (double)sample.PeakWorkingSet / MeasurementSample.Mebibyte))} MiB; owner's reserved bytes peak {MeasurementSample.Spread(samples.Select(static sample => (double)sample.PeakBoundedBytes / MeasurementSample.Mebibyte))} MiB of {samples[0].Bound / MeasurementSample.Mebibyte} MiB; generation-2 causes {string.Join(", ", samples.SelectMany(static sample => sample.Gen2Causes).GroupBy(static cause => cause.Key).Select(static cause => $"{cause.Key} {cause.Sum(static count => count.Value)}"))}"));
        }

        MeasurementSample.Report($"{await ArchivedBytesAsync(owner, Key) / MeasurementSample.Mebibyte} MiB of the journal in the archive after the runs");
    }

    /// <summary>Returns the bytes of the journal <paramref name="key"/> its owner's archive store holds.</summary>
    private async Task<long> ArchivedBytesAsync(SiloHandle owner, string key)
    {
        var archive = (Services(owner).GetRequiredService<LocalRouting>().Current ?? throw new InvalidOperationException("The owner's runtime published no routing.")).Archive;
        var read = await archive.ReadAsync(JournalRowKey.Encode(key).Memory, await ReadMapAsync(), Token);
        if (read.Value is ArchiveStoreRead.Read found)
        {
            using (found)
            {
                return found.Prefix.Content.Length;
            }
        }

        return 0;
    }

    /// <summary>
    /// orleans.md §5.5, OR-I1: a node which holds no claim on a key's open ledger answers a write forwarded to it
    /// <c>Moved</c>, naming the primary in its map, without effect: the write then lands on the primary at the same version.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task ANodeWithoutTheClaimAnswersMovedNamingThePrimary()
    {
        var cluster = await DeployAsync(3, TestContext.Current.CancellationToken);
        _ = await ServedAsync(cluster, ranges: 3, done: value => Balanced(cluster, value));
        const string Key = "a journal sent to the wrong node";

        // The claimless node answers from its own view, which a map read here may be ahead of.
        var map = await EverySiloFollowsTheMapAsync(cluster);
        var primary = Assert.NotNull(map.FindOpen(JournalRowKey.Encode(Key).Span)).Primary.Node;
        var claimless = Nodes(cluster).First(entry => entry.Node != primary);
        await EventuallyAsync(() => _state.Journals[claimless.Handle.Name].State == LocalJournals.JournalsState.Serving, "the claimless silo's Journals to serve");

        Assert.Equal(new JournalMoved(primary), (await ForwardAsync(JournalNode(cluster, claimless.Node), Key, JournalVersion.Origin, [1])).Value);
        _ = Assert.IsType<JournalWriteOutcome.Accepted>((await ForwardAsync(JournalNode(cluster, primary), Key, JournalVersion.Origin, [1])).Value);
    }

    /// <summary>
    /// OR-I1, OR-I7, journals.md §2.2: a write in its primary's writer while the primary hands its ledger off, before any
    /// quorum holds its entry, is answered <c>Fenced</c>, without effect, and the router resends it to the new primary,
    /// where it lands exactly once. The write is the only one in the old ledger's writer when it seals, which answers it
    /// <c>Fenced</c>: its replica writes to other silos are held, and the seal and reads which reach the old primary's
    /// silo, whose own replica the write reached in process, are held there too, so the new primary's recovery decides
    /// the tail from the two replicas which lack the entry, and that tail excludes it. The recovery needs only <c>Q</c>
    /// seals and stops the rest once it ends, so the old primary's seal may never arrive (#326): the test waits for none,
    /// and the successor's install, which follows the recovery, orders the release. With <paramref name="lateSeal"/>,
    /// the seal to the old primary is held at its caller until the successor is installed, as a loaded silo delivers it.
    /// Without those holds, a recovery which found the entry on the old primary's replica would copy it forward, and the
    /// write would be <c>Accepted</c> on its one send. The router logs that one <c>Fenced</c> resend, so the write cannot
    /// pass on a single send; an <c>Ambiguous</c> answer would settle the write unaccepted. Four silos with <c>N = 3</c>
    /// let the handed-off node's ledgers move to the others.
    /// </summary>
    [Theory(Timeout = 180_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AWriteFencedByAHandOffIsResentToTheNewPrimaryAndLandsOnce(bool lateSeal)
    {
        var cluster = await DeployAsync(4, TestContext.Current.CancellationToken);
        _ = await ServedAsync(cluster, ranges: 3, done: value => Balanced(cluster, value));

        // The writing silo routes by its own view, which a map read here may be ahead of.
        var map = await EverySiloFollowsTheMapAsync(cluster);

        // The last silo, which the teardown stops first: the handed-off node stays pending until its silo stops, so a silo
        // stopped before it could place no hand-off of its own and would wait out the host's stop timeout (§5.2).
        var (node, _, handle) = Nodes(cluster).Last(entry => entry.Handle != cluster.Primary && map.Ledgers.Any(ledger => ledger.IsOpen && ledger.Primary.Node == entry.Node));
        var ledger = map.Ledgers.First(ledger => ledger.IsOpen && ledger.Primary.Node == node);
        var key = Enumerable.Range(0, int.MaxValue).Select(static index => string.Create(CultureInfo.InvariantCulture, $"fenced-{index}")).First(candidate => ledger.Range.Contains(JournalRowKey.Encode(candidate).Span));
        await EventuallyAsync(() => _state.Journals[handle.Name].State == LocalJournals.JournalsState.Serving, $"{handle.Name}'s Journals to serve");
        _state.HeldLedger = ledger.Id;
        var held = _state.ReplicaWrites.Hold();
        _ = _state.TailReads.Hold(handle.Name);
        _ = _state.ReplicaReads.Hold(handle.Name);
        if (lateSeal)
        {
            _ = _state.SealSends.Hold(holds: target => target is Guid key && key == node.Value);
        }

        using var payload = Payload([1, 2, 3]);
        using var record = JournalEntryRecord.Append(JournalRowKey.Encode(key), JournalVersion.Origin, payload);
        var write = Services(handle).GetRequiredService<JournalRouter>().WriteAsync(key, JournalRowKey.Encode(key), record, Token);
        await held.WaitAsync(Patience, Token);

        await Controller(cluster).HandOffAsync(node, handle.SiloAddress.Generation);
        await EventuallyAsync(async () => (await ReadMapAsync()).FindOpen(JournalRowKey.Encode(key).Span) is { } open && open.Id != ledger.Id && open.Primary.Node != node, "the key's ledger to move off the handed-off node");
        _state.SealSends.Release();
        _state.ReplicaWrites.Release();
        _state.TailReads.Release();
        _state.ReplicaReads.Release();

        var version = Assert.IsType<JournalWriteOutcome.Accepted>((await write.WaitAsync(Patience, Token)).Value).Version;
        Assert.Single(_state.RouterResends, resend => resend.Silo == handle.Name && resend.Message.Contains($"'{key}' after Fenced", StringComparison.Ordinal));
        var read = await Services(handle).GetRequiredService<JournalRouter>().ReadAsync(key, JournalRowKey.Encode(key), Token);
        using (read.Content)
        {
            var content = read.Content.Reader.PeekSlice(read.Content.Length);
            try
            {
                Assert.Equal([1, 2, 3], content.ToArray());
            }
            finally
            {
                content.Dispose();
            }

            Assert.Equal((version, true), (read.Version, read.Exists));
        }
    }
    /// <summary>
    /// orleans.md §2, §5.5: Orleans Journaling's default journal storage is Clog's: a journaled grain's state, written
    /// through <c>DurableJournalStorage</c> and longer than one forwarded page, recovers whole in a new activation. The grain
    /// runs on a silo whose node is not its journal's primary, so its recovery reads the journal in forwarded pages.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public Task AJournaledGrainsStateRecoversThroughTheDurableJournalStorage() =>
        RecoverChunksAsync<IJournaledChunksGrain>(providerName: null, TestContext.Current.CancellationToken);

    /// <summary>
    /// orleans.md §2: a silo which registers Clog under a provider name serves the grain types which select that name
    /// (<c>JournalStorageProvider</c>), whose state recovers through Clog's storage as the default's does.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public Task AGrainOverANamedProviderRecoversThroughTheDurableJournalStorage() =>
        RecoverChunksAsync<INamedJournaledChunksGrain>(NamedJournaledChunksGrain.Provider, TestContext.Current.CancellationToken);

    /// <summary>
    /// orleans.md §5.4, §5.9: a grain marked <c>[JournalPlacement]</c>, called from a silo whose node is not its journal's
    /// primary, activates on the primary's silo, so its writes and reads take the local path; and that silo's status
    /// shows its Journals serving and the ledger's applied slot, contiguous tail, watermark, and undrained bytes and age
    /// once its governance loop records them.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task AJournalPlacedGrainActivatesOnItsPrimaryWhoseStatusShowsItsLedger()
    {
        var cluster = await DeployAsync(3, TestContext.Current.CancellationToken);
        _ = await ServedAsync(cluster, ranges: 3, done: value => Balanced(cluster, value));
        var map = await EverySiloFollowsTheMapAsync(cluster);
        await EventuallyAsync(() => Nodes(cluster).All(entry => _state.Journals[entry.Handle.Name].State == LocalJournals.JournalsState.Serving), "every silo's Journals to serve");
        var key = JournalId.FromGrainId(cluster.Client.GetGrain<IPlacedJournaledChunksGrain>("placed").GetGrainId()).Value;
        var ledger = Assert.NotNull(map.FindOpen(JournalRowKey.Encode(key).Span));
        var primary = Nodes(cluster).Single(entry => entry.Node == ledger.Primary.Node).Handle;
        var caller = Nodes(cluster).First(entry => entry.Node != ledger.Primary.Node).Handle;
        var grain = Services(caller).GetRequiredService<IGrainFactory>().GetGrain<IPlacedJournaledChunksGrain>("placed");

        _ = await grain.AppendAsync([1, 2]);
        Assert.Equal(2, await grain.AppendAsync([3]));
        Assert.Equal(primary.SiloAddress, await grain.SiloAsync());

        var status = _state.Statuses[primary.Name];
        Assert.Equal(LocalJournals.JournalsState.Serving, status.Journals);
        await EventuallyAsync(
            () => status.NodeStatus?.Primaries.SingleOrDefault(observed => observed.Ledger == ledger.Id) is { AppliedBelow.Value: >= 2, Undrained: { Bytes: > 0, Age: not null } } observed
                && observed.ContiguousTail >= observed.AppliedBelow!.Value
                && observed.Watermark == Slot.Zero,
            $"{primary.Name}'s status to show the grain's writes in ledger {ledger.Id}");
        Assert.Equal(_state.LocalNodes[primary.Name].Node, status.NodeStatus!.Node);
    }

    /// <summary>
    /// orleans.md §5.9: the governance loop records the node's status every control interval, whatever its disk
    /// measurement does: while the measurement fails, each record keeps the last measured resources and observes the
    /// node's primaries afresh, so a ledger handed off leaves the status.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task TheStatusIsRecordedEachIntervalWhileTheDiskMeasurementFails()
    {
        var cluster = await DeployAsync(4, TestContext.Current.CancellationToken);
        var map = await ServedAsync(cluster, ranges: 3, done: value => Balanced(cluster, value));
        var (node, _, handle) = Nodes(cluster).First(entry => entry.Handle != cluster.Primary && map.Ledgers.Any(ledger => ledger.IsOpen && ledger.Primary.Node == entry.Node));
        var status = _state.Statuses[handle.Name];
        await EventuallyAsync(() => status.NodeStatus is { Resources: not null, Primaries.IsEmpty: false }, $"{handle.Name}'s status to record its resources and primaries");

        _state.FailedDiskMeasurements[handle.Name] = true;
        var failing = TimeProvider.System.GetUtcNow();
        await EventuallyAsync(() => status.NodeStatus is { } recorded && recorded.ObservedAt > failing && recorded.Resources!.MeasuredAt < failing, $"{handle.Name}'s status to be recorded after its disk measurement fails");
        var measured = status.NodeStatus!.Resources;

        await Controller(cluster).HandOffAsync(node, handle.SiloAddress.Generation);
        await EventuallyAsync(() => status.NodeStatus is { Primaries.IsEmpty: true }, $"{handle.Name}'s status to leave its handed-off ledgers");
        Assert.Same(measured, status.NodeStatus!.Resources);
        _state.FailedDiskMeasurements.Clear();
    }

    /// <summary>
    /// OR-I14, orleans.md §7.2: a journal grain the registry places beside another node's store answers a write, a read,
    /// and a page <c>NotThisNode</c>, touching nothing, and deactivates after each.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task AMisplacedJournalGrainAnswersNotThisNodeAndDeactivates()
    {
        var cluster = await DeployAsync(1, TestContext.Current.CancellationToken);
        _ = await Nodes(cluster)[0].Local.Registered.WaitAsync(Patience, Token);
        var misplaced = new NodeId(Guid.NewGuid());
        var silo = cluster.Silos[0];
        _ = Assert.IsType<NodeRegistrationOutcome.Registered>((await Controller(cluster).RegisterNodeAsync(new NodeRegistryEntry(misplaced, silo.SiloAddress), await RegisteredStoreAsync(), nodeToReplace: null)).Value);
        var resolver = Services(silo).GetRequiredService<NodeRegistryResolver>();
        resolver.RequestReread();
        await EventuallyAsync(() => silo.SiloAddress.Equals(resolver.Resolve(misplaced)), "the silo's resolver places the misplaced node beside its own");
        var grain = JournalNode(cluster, misplaced);
        _state.Journals[silo.Name].BeforeCall = static () => throw new InvalidOperationException("A misplaced grain reaches no Journals.");

        var management = cluster.Client.GetGrain<IManagementGrain>(0);
        Assert.Equal(NotThisNode.Instance, (await ForwardAsync(grain, "k", JournalVersion.Origin, [1])).Value);
        await DeactivatedAsync("a write");
        Assert.Equal(NotThisNode.Instance, (await grain.ReadAsync("k", Token)).Value);
        await DeactivatedAsync("a read");
        Assert.Equal(NotThisNode.Instance, (await grain.ReadPageAsync(Guid.NewGuid(), 0, Token)).Value);
        await DeactivatedAsync("a page");

        Task DeactivatedAsync(string call) => EventuallyAsync(
            async () => !(await management.GetDetailedGrainStatistics()).Any(statistic => statistic.GrainId.Key.ToString() == misplaced.Value.ToString("N")),
            $"the misplaced activation to deactivate after {call}");
    }
    /// <summary>
    /// OR-I14, orleans.md §7.2: a node control grain the registry places beside another node's store, as a defect or a
    /// directory race would, declines a transition, refuses a compaction, and records no pressure report, touching
    /// nothing, and deactivates.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task AMisplacedNodeControlGrainDeclinesRefusesAndDeactivates()
    {
        var cluster = await DeployAsync(1, TestContext.Current.CancellationToken);
        _ = await Nodes(cluster)[0].Local.Registered.WaitAsync(Patience, Token);
        var misplaced = new NodeId(Guid.NewGuid());
        var silo = cluster.Silos[0];
        _ = Assert.IsType<NodeRegistrationOutcome.Registered>((await Controller(cluster).RegisterNodeAsync(new NodeRegistryEntry(misplaced, silo.SiloAddress), await RegisteredStoreAsync(), nodeToReplace: null)).Value);
        var resolver = Services(silo).GetRequiredService<NodeRegistryResolver>();
        resolver.RequestReread();
        await EventuallyAsync(() => silo.SiloAddress.Equals(resolver.Resolve(misplaced)), "the silo's resolver places the misplaced node beside its own");
        var grain = NodeControl(cluster, misplaced);

        Assert.Equal(TransitionReply.Declined.Instance, await grain.ExecuteAsync(new Transition.Bootstrap(new TransitionChoice(misplaced, [misplaced], 1, 1, 0))));
        _ = await Assert.ThrowsAsync<NodeControlStoppedException>(() => grain.CompactAsync(new CompactionAssignment(KeyRange.Root, misplaced, 1)));
        var reporter = new NodeId(Guid.NewGuid());
        await grain.ReportPressureAsync(reporter);
        var replicated = new LedgerMetadata(new LedgerId(1), KeyRange.Root, [reporter], 1, new PrimaryClaim(misplaced, SequencerInstanceId.New()), LedgerStatus.Open.Instance);
        Assert.False(Services(silo).GetRequiredService<NodePressure>().IsThrottled(replicated));
        var management = cluster.Client.GetGrain<IManagementGrain>(0);
        await EventuallyAsync(
            async () => !(await management.GetDetailedGrainStatistics()).Any(statistic => statistic.GrainId.Key.ToString() == misplaced.Value.ToString("N")),
            "the misplaced activation deactivates");
    }

    /// <summary>
    /// OR-I3: once its silo's registry view shows its node retired, a node control grain declines every transition, so the
    /// node submits no map change.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task ARetiredNodeDeclinesATransitionAndWritesNothing()
    {
        var cluster = await DeployAsync(3, TestContext.Current.CancellationToken);

        // Balanced, so no balancing hand-off races the retirement.
        var map = await ServedAsync(cluster, ranges: 3, done: value => Balanced(cluster, value));
        var (node, _, handle) = Nodes(cluster)[1];
        var register = await _state.MetaStore.GetRegisterAsync(_state.Cluster, NodeRegistryValue.RegisterName, Token);
        var (registry, version) = await NodeRegistryCodec.ReadAsync(register, Token);
        var retired = new NodeRegistryValue(registry.Revision + 1, [.. registry.Nodes.Select(entry => entry.Node == node ? entry.Retire(registry.Revision + 1) : entry)], registry.ArchiveStore);
        _ = Assert.IsType<RegisterWriteOutcome.Applied>((await register.WriteAsync(version, new(NodeRegistryCodec.Encode(retired)), Token)).Value);
        var resolver = Services(handle).GetRequiredService<NodeRegistryResolver>();
        resolver.RequestReread();
        await EventuallyAsync(() => resolver.Current?.Revision >= retired.Revision, "the retired node's silo reads the retirement");
        var epoch = (await ReadMapAsync()).Epoch;
        var ledger = map.Ledgers.First(static ledger => ledger.IsOpen);
        var choice = new TransitionChoice(node, [node, .. ledger.Replicas.Where(replica => replica != node).Take(2)], 4, retired.Revision, epoch);

        var reply = await NodeControl(cluster, node).ExecuteAsync(new Transition.Replace(ledger.Id, choice));

        Assert.Equal(TransitionReply.Declined.Instance, reply);
        Assert.Equal(TransitionReply.Declined.Instance, await NodeControl(cluster, node).ExecuteAsync(new Transition.Split(ledger.Id, choice)));
        Assert.DoesNotContain((await ReadMapAsync()).Ledgers, listed => listed.Primary.Node == node && listed.Id.Value > map.Ledgers.Max(static ledger => ledger.Id.Value));
    }

    /// <summary>
    /// orleans.md §5.3, §5.8, OR-I3, OR-I4: a retirement call returns while the registry write it causes is held, and the
    /// node stands active until it lands; then the controller grain raises the map's floor to its revision and hands off
    /// and repairs every ledger which names the node, which drain and drop. While the map keeps a closed ledger naming it, one answer shows the floor covering the retirement, that ledger alone naming the node, and the gate closed, and
    /// the retired node serves that ledger's replica; once the drop lands, the gate opens and the map names it nowhere.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task ARetiredNodeLeavesEveryLedgerAndItsGateOpensOnceTheyDrop()
    {
        var cluster = await DeployAsync(4, TestContext.Current.CancellationToken);
        var map = await ServedAsync(cluster, ranges: 4, done: value => Balanced(cluster, value));
        var (retired, _, handle) = Nodes(cluster).First(entry => entry.Handle != cluster.Primary);
        var naming = map.Ledgers.First(ledger => ledger.IsOpen && ledger.Replicas.Contains(retired));
        _state.HeldDrains = [naming.Id];
        var held = _state.RegistryWrites.Hold();

        await Controller(cluster).RetireNodeAsync(retired).WaitAsync(Patience, Token);

        await held.WaitAsync(Patience, Token);
        Assert.Equal(NodeStanding.Active.Instance, await Controller(cluster).GetRetirementAsync(retired));
        _state.RegistryWrites.Release();
        NodeStanding.Retired standing = null!;
        // The kept ledger's own repair closes it, possibly after every other ledger naming the node has dropped.
        await EventuallyAsync(
            async () => (await ReadMapAsync()).Get(naming.Id) is { IsOpen: false }
                && (await Controller(cluster).GetRetirementAsync(retired)).Value is NodeStanding.Retired { FloorCovers: true } covered
                && (standing = covered).Ledgers.SequenceEqual([naming.Id]),
            "the floor to cover the retirement, the kept ledger to close, and only it to name the node");
        Assert.False(standing.IsReleasable);

        // The retired node's silo still serves the replica of the ledger which names it: a call from another silo reaches its store.
        var other = cluster.Silos.First(silo => silo != handle);
        await using (var replica = Services(other).GetRequiredService<ILedgerReplicaTransport>().Open(retired, naming.Id, new LedgerReplicaOptions { WindowSize = naming.K }))
        {
            _ = await replica.ReadTailAsync(Slot.Zero, seal: false, Token);
        }

        _state.HeldDrains = [];
        await EventuallyAsync(async () => (await Controller(cluster).GetRetirementAsync(retired)).IsReleasable, "the retired node's gate to open");
        Assert.DoesNotContain((await ReadMapAsync()).Ledgers, ledger => ledger.Primary.Node == retired || ledger.Replicas.Contains(retired));
        _ = await ServedAsync(cluster, ranges: 3);
    }

    /// <summary>
    /// orleans.md §5.8, OR-I4 (#381): a ledger naming a retired node drops while another which names it is kept, though
    /// both are closed and the drops of a dropper's pass share a ledger map batch. Two ledgers name the node, and the drains
    /// of both are held until the retirement has closed them; then one is released, so it becomes droppable while the
    /// other is still kept, and it drops, leaving the kept ledger alone naming the node, with the gate closed; no ledger map
    /// write ever dropped the kept ledger, so no batch carrying the released one's drop could fail on its account. Keeping a
    /// ledger by failing the map write which drops it failed every change of that write's batch, the released one's drop
    /// among them, whenever a pass submitted both.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task ALedgerOfARetiredNodeDropsWhileAnotherNamingItIsKept()
    {
        var cluster = await DeployAsync(4, TestContext.Current.CancellationToken);
        var map = await ServedAsync(cluster, ranges: 4, done: value => Balanced(cluster, value));
        var (retired, _, _) = Nodes(cluster).First(entry => entry.Handle != cluster.Primary && map.Ledgers.Count(ledger => ledger.IsOpen && ledger.Replicas.Contains(entry.Node)) >= 2);
        var naming = map.Ledgers.Where(ledger => ledger.IsOpen && ledger.Replicas.Contains(retired)).Take(2).ToArray();
        var (kept, released) = (naming[0], naming[1]);
        _state.HeldDrains = [kept.Id, released.Id];

        await Controller(cluster).RetireNodeAsync(retired).WaitAsync(Patience, Token);
        await EventuallyAsync(
            async () => await ReadMapAsync() is var current && current.Get(kept.Id) is { IsOpen: false } && current.Get(released.Id) is { IsOpen: false }
                && (await Controller(cluster).GetRetirementAsync(retired)).Value is NodeStanding.Retired { FloorCovers: true } covered
                && covered.Ledgers.Order().SequenceEqual(new[] { kept.Id, released.Id }.Order()),
            "both held ledgers to close and alone to name the node");

        _state.HeldDrains = [kept.Id];
        await EventuallyAsync(
            async () => (await ReadMapAsync()).Get(released.Id) is null
                && (await Controller(cluster).GetRetirementAsync(retired)).Value is NodeStanding.Retired { FloorCovers: true, IsReleasable: false } covered
                && covered.Ledgers.SequenceEqual([kept.Id]),
            "the released ledger to drop while the kept one alone names the node");

        // No map write dropped the kept ledger, so none of its batches could fail the released ledger's drop.
        Assert.DoesNotContain(kept.Id, _state.MapWriteDrops);

        _state.HeldDrains = [];
        await EventuallyAsync(async () => (await Controller(cluster).GetRetirementAsync(retired)).IsReleasable, "the retired node's gate to open");
    }
    /// <summary>
    /// orleans.md §5.3, §5.8, §5.10: a retired node's silo which restarts before its gate opens registers again, its entry
    /// at the new silo's generation and still retired at its revision, and serves the replicas which still name it, so a
    /// restart loses none of them; it is never placed again, and its gate opens once its ledgers drop.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task ARetiredNodesSiloWhichRestartsServesItsReplicasUntilItsGateOpens()
    {
        var cluster = await DeployAsync(4, TestContext.Current.CancellationToken);
        var map = await ServedAsync(cluster, ranges: 4, done: value => Balanced(cluster, value));
        var (retired, _, handle) = Nodes(cluster).First(entry => entry.Handle != cluster.Primary);
        var naming = map.Ledgers.First(ledger => ledger.IsOpen && ledger.Replicas.Contains(retired));
        _state.HeldDrains = [naming.Id];
        await Controller(cluster).RetireNodeAsync(retired);
        await EventuallyAsync(
            async () => (await Controller(cluster).GetRetirementAsync(retired)).Value is NodeStanding.Retired { FloorCovers: true } covered && covered.Ledgers.SequenceEqual([naming.Id]),
            "the retired node's ledgers to leave it but the held one");
        var before = (await ReadRegistryAsync()).Find(retired)!;

        var restarted = Assert.IsType<SiloHandle>(await cluster.RestartSiloAsync(handle).WaitAsync(Patience, Token), exactMatch: false);

        Assert.Equal(retired, Services(restarted).GetRequiredService<LocalNode>().Node);
        var after = (await ReadRegistryAsync()).Find(retired)!;
        Assert.Equal(before.RetiredRevision, after.RetiredRevision);
        Assert.True(after.Silo.Generation > before.Silo.Generation);
        var other = cluster.Silos.First(silo => silo != restarted);
        await EventuallyAsync(
            async () =>
            {
                try
                {
                    await using var replica = Services(other).GetRequiredService<ILedgerReplicaTransport>().Open(retired, naming.Id, new LedgerReplicaOptions { WindowSize = naming.K });
                    _ = await replica.ReadTailAsync(Slot.Zero, seal: false, Token);
                    return true;
                }
                catch (LedgerReplicaUnavailableException)
                {
                    return false;
                }
            },
            "the restarted silo to serve the retired node's replica");

        _state.HeldDrains = [];
        await EventuallyAsync(async () => (await Controller(cluster).GetRetirementAsync(retired)).IsReleasable, "the retired node's gate to open");
        Assert.DoesNotContain((await ReadMapAsync()).Ledgers, ledger => ledger.Primary.Node == retired || ledger.Replicas.Contains(retired));
    }

    /// <summary>
    /// orleans.md §5.3: a deleted volume's retirement resolves its ordinal from a registry value read after the call: a
    /// running silo's occupant is answered active and not retired, and once its silo is dead, the call answers that node,
    /// records its retirement, and returns, and its caller follows it by identity alone until the gate opens. A silo over
    /// a new disk at that ordinal then registers a new node, and a repeat of the retirement by the old identity leaves the
    /// new node active.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task AVolumeRetirementResolvesItsOrdinalToTheStoppedNodeAndIsFollowedByIdentity()
    {
        _state.UseOrdinals = true;
        var cluster = await DeployAsync(4, fastMembership: true, TestContext.Current.CancellationToken);
        _ = await ServedAsync(cluster, ranges: 4);
        var (stopped, _, handle) = Nodes(cluster).First(entry => entry.Handle != cluster.Primary);
        var ordinal = OrdinalOf(handle.Name);
        var (running, runningOrdinal) = Nodes(cluster).Where(entry => entry.Handle != handle && entry.Handle != cluster.Primary).Select(static entry => (entry.Node, OrdinalOf(entry.Handle.Name))).First();

        Assert.Equal([new OrdinalNode(running, runningOrdinal, NodeStanding.Active.Instance)], await Controller(cluster).RetireVolumeAsync(runningOrdinal));
        await cluster.StopSiloAsync(handle, Token);
        await EventuallyAsync(
            () => cluster.Silos.All(silo => Services(silo).GetRequiredService<IClusterMembershipService>().CurrentSnapshot.GetSiloStatus(handle.SiloAddress) == SiloStatus.Dead),
            "every silo's membership to show the stopped silo dead");

        Assert.Equal([stopped], (await Controller(cluster).RetireVolumeAsync(ordinal)).Select(static found => found.Node));
        await EventuallyAsync(
            async () =>
            {
                await Controller(cluster).RetireNodeAsync(stopped);
                return (await Controller(cluster).GetRetirementAsync(stopped)).IsReleasable;
            },
            "the stopped node's gate to open");
        Assert.DoesNotContain((await ReadMapAsync()).Ledgers, ledger => ledger.Primary.Node == stopped || ledger.Replicas.Contains(stopped));
        Assert.Equal(NodeStanding.Active.Instance, await Controller(cluster).GetRetirementAsync(running));

        Directory.Delete(Path.Combine(_state.Roots, handle.Name), recursive: true);
        _ = Directory.CreateDirectory(Path.Combine(_state.Roots, handle.Name));

        // The silo at that ordinal starts again under its name, outside the cluster's list of silos, so the test stops it.
        var successorSilo = await cluster.StartSiloAsync(ordinal, cluster.Options, configurationOverrides: null, startSiloOnNewPort: false, cancellationToken: Token).WaitAsync(Patience, Token);
        try
        {
            var successor = (await Services(successorSilo).GetRequiredService<LocalNode>().Registered.WaitAsync(Patience, Token)).Entry;
            Assert.Equal((ordinal, false), (successor.Ordinal, successor.Node == stopped));

            await Controller(cluster).RetireNodeAsync(stopped);
            await Task.Delay(TimeSpan.FromSeconds(1), TimeProvider.System, Token);
            Assert.Equal(NodeStanding.Active.Instance, await Controller(cluster).GetRetirementAsync(successor.Node));
            Assert.False((await ReadRegistryAsync()).Find(successor.Node)!.IsRetired);
        }
        finally
        {
            await cluster.StopSiloAsync(successorSilo, CancellationToken.None);
        }
    }

    /// <summary>
    /// orleans.md §5.3: a scale-down records the retirement of the node at the scaled-away ordinal and answers it, active,
    /// before any register write, while the write it causes is held; it refuses a count below <c>N</c>. A retirement of an
    /// identity the registry never held is answered <c>NotRegistered</c>, from a value read after the call, which ends its
    /// caller's repetition.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task AScaleDownAnswersBeforeAnyRegisterWriteAndAnUnknownNodeIsNotRegistered()
    {
        _state.UseOrdinals = true;
        var cluster = await DeployAsync(4, TestContext.Current.CancellationToken);
        _ = await ServedAsync(cluster, ranges: 4);
        var last = Nodes(cluster).Single(entry => OrdinalOf(entry.Handle.Name) == 3).Node;
        var held = _state.RegistryWrites.Hold();

        var answered = await Controller(cluster).ScaleDownAsync(3).WaitAsync(Patience, Token);

        Assert.Equal([new OrdinalNode(last, 3, NodeStanding.Active.Instance)], answered);
        await held.WaitAsync(Patience, Token);
        Assert.Equal(NodeStanding.Active.Instance, await Controller(cluster).GetRetirementAsync(last));
        _ = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Controller(cluster).ScaleDownAsync(2));
        _state.RegistryWrites.Release();
        await EventuallyAsync(async () => (await Controller(cluster).GetRetirementAsync(last)).Value is NodeStanding.Retired, "the scaled-away node to retire");

        var unknown = new NodeId(Guid.NewGuid());
        await Controller(cluster).RetireNodeAsync(unknown);
        Assert.Equal(NodeStanding.NotRegistered.Instance, await Controller(cluster).GetRetirementAsync(unknown));
        _ = await Assert.ThrowsAsync<ArgumentException>(() => Controller(cluster).RetireNodeAsync(default));
        Assert.False(await Controller(cluster).ResolveCorruptPartitionAsync(KeyRange.Root));
    }

    /// <summary>
    /// orleans.md §5.3, §5.6: a retirement whose registry write fails unapplied is submitted again by the next pass with no
    /// repeat, and its gate opens; one whose write fails while the controller grain moves is lost with its activation, and
    /// the node stands active until its caller repeats the call, as the procedure has it, when it retires and its gate
    /// opens. Five silos leave <c>N</c> active nodes after both retirements; then a silo whose registry view predates both
    /// stops at once once it reads the registry again, as too few others remain.
    /// </summary>
    /// <remarks>
    /// The stopping silo's view predates the retirements by construction (#248): the controller grain, whose reads would
    /// raise its silo's view, moves only between other silos, and the stopping silo's resolver reads are held from before
    /// the retirements until its stop has asked twice for a hand-off no one can place.
    /// </remarks>
    [Fact(Timeout = 180_000)]
    public async Task ARetirementWhoseWriteFailedIsSubmittedAgainOrRepeatedAfterAMove()
    {
        var cluster = await DeployAsync(5, TestContext.Current.CancellationToken);
        _ = await ServedAsync(cluster, ranges: 4, done: value => Balanced(cluster, value));
        var stopping = cluster.Silos[^1];
        var stoppingNode = Services(stopping).GetRequiredService<LocalNode>().Node;
        var view = Services(stopping).GetRequiredService<NodeRegistryResolver>();
        await MoveControllerAsync(cluster, cluster.Primary!);
        _ = _state.ResolverReads.Hold(stopping.Name);
        var viewed = view.Current!.Revision;
        var nodes = Nodes(cluster).Where(entry => entry.Handle != cluster.Primary && entry.Handle != stopping).Select(static entry => entry.Node).ToArray();

        _state.FailRegistryWrites = 1;
        await Controller(cluster).RetireNodeAsync(nodes[0]);
        await EventuallyAsync(async () => (await Controller(cluster).GetRetirementAsync(nodes[0])).Value is NodeStanding.Retired, "the failed retirement to be submitted again");
        Assert.Equal(0, _state.FailRegistryWrites);

        var held = _state.RegistryWrites.Hold();
        await Controller(cluster).RetireNodeAsync(nodes[1]);
        await held.WaitAsync(Patience, Token);
        _state.FailRegistryWrites = 1;
        await RequestControllerMoveAsync(cluster, cluster.Silos[1]);
        _state.RegistryWrites.Release();
        for (var interval = 0; interval < 5; interval++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(200), TimeProvider.System, Token);
            Assert.Equal(NodeStanding.Active.Instance, await Controller(cluster).GetRetirementAsync(nodes[1]));
        }

        await Controller(cluster).RetireNodeAsync(nodes[1]);
        await EventuallyAsync(async () => (await Controller(cluster).GetRetirementAsync(nodes[1])).IsReleasable, "the repeated retirement's gate to open");
        Assert.True((await Controller(cluster).GetRetirementAsync(nodes[0])).IsReleasable);

        // Retiring the third non-primary node leaves only one other active node (< Q = 2), so no degraded hand-off can be
        // placed for the stopping silo once its registry view catches up.
        await Controller(cluster).RetireNodeAsync(nodes[2]);
        await EventuallyAsync(async () => (await Controller(cluster).GetRetirementAsync(nodes[2])).Value is NodeStanding.Retired, "the third node to retire");

        // orleans.md §5.2 step 1: a stopping primary whose registry view predates the retirements, which changed no
        // membership, re-reads it, so it counts one other active node, below quorum Q for a hand-off, and stops at once
        // rather than waiting for a hand-off no one can place until the host's 30-second stop timeout. Until its reads are
        // released, its view counts the retired nodes as targets, so it waits, asking each control interval.
        await EventuallyAsync(
            async () => (await ReadMapAsync()).Ledgers.Any(ledger => ledger.IsOpen && ledger.Primary.Node == stoppingNode),
            "the stopping silo's node to be primary of an open ledger");
        Assert.Equal(viewed, view.Current!.Revision);
        Assert.True(viewed < (await ReadRegistryAsync()).Revision);
        var stop = cluster.StopSiloAsync(stopping, Token);
        await EventuallyAsync(() => _state.HandOffRequests.GetValueOrDefault(stoppingNode) >= 2, "the stopping node to ask again for its hand-off");
        Assert.False(stop.IsCompleted);
        _state.ResolverReads.Release();
        await stop.WaitAsync(TimeSpan.FromSeconds(15), Token);
    }

    /// <summary>
    /// orleans.md §5.3, §5.6, OR-I4: a retirement whose floor raise fails, unapplied, while the controller grain moves is
    /// carried to the map by the new activation's passes, with no repeat of the call, since its registry view holds the
    /// retirement above the map's floor; the gate opens, and after the controller grain's silo crashes, its activation on
    /// another silo answers it releasable from its own reads.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task AFloorRaiseLostWithItsControllerIsRaisedByTheNextActivation()
    {
        var cluster = await DeployAsync(4, fastMembership: true, TestContext.Current.CancellationToken);
        _ = await ServedAsync(cluster, ranges: 4, done: value => Balanced(cluster, value));
        var retired = Nodes(cluster).First(entry => entry.Handle != cluster.Primary).Node;
        _state.FailFloorRaises = true;

        await Controller(cluster).RetireNodeAsync(retired);
        await EventuallyAsync(async () => (await Controller(cluster).GetRetirementAsync(retired)).Value is NodeStanding.Retired { FloorCovers: false }, "the registry to show the node retired");
        await Task.Delay(TimeSpan.FromSeconds(1), TimeProvider.System, Token);
        Assert.True((await ReadMapAsync()).RetirementFloor < Assert.IsType<NodeStanding.Retired>((await Controller(cluster).GetRetirementAsync(retired)).Value).RetiredRevision);
        await Controller(cluster).AsReference<IGrainManagementExtension>().DeactivateOnIdle(Token);
        _state.FailFloorRaises = false;

        await EventuallyAsync(async () => (await Controller(cluster).GetRetirementAsync(retired)).IsReleasable, "the next activation to raise the floor and the gate to open");

        // The test cluster's membership lives on its primary silo, so the controller grain migrates to another before its crash.
        var host = cluster.Silos.First(silo => silo != cluster.Primary);
        await MoveControllerAsync(cluster, host);
        await cluster.KillSiloAsync(host, Token);
        await EventuallyAsync(
            () => cluster.Silos.All(silo => Services(silo).GetRequiredService<IClusterMembershipService>().CurrentSnapshot.GetSiloStatus(host.SiloAddress) == SiloStatus.Dead),
            "every silo's membership to show the controller grain's silo dead");

        Assert.True((await Controller(cluster).GetRetirementAsync(retired)).IsReleasable);
        Assert.DoesNotContain(host.SiloAddress, await ControllerSilosAsync(cluster));
    }

    /// <summary>
    /// orleans.md §5.8, OR-I19: a controller grain which moves to a silo whose registry view predates a retirement answers
    /// the retired node from a registry value read after the call, never active from the view, so the move takes back no
    /// gate an earlier activation answered open (#248). The silo's resolver reads are held, so its view predates the
    /// retirement by construction, and the moved activation's passes, whose re-reads go through the resolver, cannot raise it.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task AControllerWhichMovesToAStaleSiloAnswersARetiredNodeFromAValueReadAfterTheCall()
    {
        var cluster = await DeployAsync(4, TestContext.Current.CancellationToken);
        _ = await ServedAsync(cluster, ranges: 4, done: value => Balanced(cluster, value));
        var hosting = Assert.Single(await ControllerSilosAsync(cluster));
        var stale = cluster.Silos.First(silo => silo != cluster.Primary && silo.SiloAddress != hosting);
        var view = Services(stale).GetRequiredService<NodeRegistryResolver>();
        var retired = Nodes(cluster).First(entry => entry.Handle != cluster.Primary && entry.Handle != stale).Node;
        _ = _state.ResolverReads.Hold(stale.Name);

        await Controller(cluster).RetireNodeAsync(retired);
        await EventuallyAsync(async () => (await Controller(cluster).GetRetirementAsync(retired)).IsReleasable, "the retired node's gate to open");

        await MoveControllerAsync(cluster, stale);
        Assert.False(view.Current!.Find(retired)!.IsRetired);

        var standing = await Controller(cluster).GetRetirementAsync(retired);
        Assert.True(standing.IsReleasable, $"The moved controller answered {standing.Value}.");
        Assert.True(view.Current!.Find(retired)!.IsRetired);
        _state.ResolverReads.Release();
    }

    /// <summary>
    /// Moves the controller grain's activation to <paramref name="target"/>, unless it is there alone, and waits until it is.
    /// </summary>
    private static async Task MoveControllerAsync(TestCluster cluster, SiloHandle target)
    {
        if (!(await ControllerSilosAsync(cluster)).SequenceEqual([target.SiloAddress]))
        {
            await RequestControllerMoveAsync(cluster, target);
        }

        await EventuallyAsync(async () => (await ControllerSilosAsync(cluster)).SequenceEqual([target.SiloAddress]), $"the controller grain to move to {target.Name}");
    }

    /// <summary>Asks the controller grain to migrate to <paramref name="target"/> once idle, and returns once it is asked.</summary>
    private static async Task RequestControllerMoveAsync(TestCluster cluster, SiloHandle target)
    {
        RequestContext.Set(IPlacementDirector.PlacementHintKey, target.SiloAddress);
        try
        {
            await Controller(cluster).AsReference<IGrainManagementExtension>().MigrateOnIdle(Token);
        }
        finally
        {
            RequestContext.Clear();
        }
    }

    /// <summary>Returns the silos of the controller grain's activations: two while one migrates, and one otherwise.</summary>
    private static async Task<SiloAddress[]> ControllerSilosAsync(TestCluster cluster)
    {
        var id = Controller(cluster).GetGrainId();
        var statistics = await cluster.Client.GetGrain<IManagementGrain>(0).GetDetailedGrainStatistics();
        return [.. statistics.Where(statistic => statistic.GrainId == id).Select(static statistic => statistic.SiloAddress).Distinct()];
    }

    /// <summary>
    /// orleans.md §5.10, §5.7: after every silo stops, silos started over the same roots open the same nodes and register
    /// them at newer generations, and the controller grain takes every ledger open before the restart, the ledgers the map
    /// holds open once every silo stopped, over exactly once, on one of its replicas; each entry appended before the stop,
    /// its ledger kept from its drop, lies below its ledger's recorded tail and reads back from a replica after the
    /// restart. Until a node registers, its claims have ended, so the controller takes its ledgers over at once on their
    /// other replicas and waits for no node (step 3): with <paramref name="lateRegistration"/>, one primary's registration
    /// is held at the controller grain until a takeover of one of its ledgers applies, and none of its ledgers keeps it
    /// as primary, though it then registers at its newer generation.
    /// </summary>
    [Theory(Timeout = 180_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AWholeClusterRestartTakesEveryLedgerOverOnceOnOneOfItsReplicas(bool lateRegistration)
    {
        var first = await DeployAsync(3, TestContext.Current.CancellationToken);
        var before = await ServedAsync(first, ranges: 3, done: value => Balanced(first, value));
        var nodes = Nodes(first).Select(static entry => entry.Node).ToHashSet();
        var silos = Nodes(first).ToDictionary(static entry => entry.Node, static entry => entry.Handle.Name);
        var primarySilo = Assert.IsType<SiloHandle>(first.Primary, exactMatch: false).Name;
        var generations = (await ReadRegistryAsync()).Nodes.ToDictionary(static entry => entry.Node, static entry => entry.Silo.Generation);
        var appended = new List<(LedgerMetadata Ledger, Slot Slot, byte[] Body)>();
        foreach (var ledger in before.Ledgers.Where(static ledger => ledger.IsOpen))
        {
            var handle = Nodes(first).Single(entry => entry.Node == ledger.Primary.Node).Handle;
            LedgerWriter? writer = null;
            await EventuallyAsync(() => (writer = Runtime(handle)!.Writers.TryGet(ledger.Id)) is not null, $"s{ledger.Id}'s primary to serve it");
            var body = Encoding.ASCII.GetBytes(string.Create(CultureInfo.InvariantCulture, $"appended to s{ledger.Id} before the restart"));
            byte[] key = [Enumerable.Range(0, 256).Select(static value => (byte)value).First(value => ledger.Range.Contains([value]))];
            using var payload = Payload(body);
            using var row = new Row(key, RowKind.Append, payload);
            var outcome = await writer!.ProposeAsync([row], Token);
            Assert.True(outcome.TryGetValue(out ProposalOutcome.Durable durable), $"s{ledger.Id}'s append answered {outcome}.");
            appended.Add((ledger, durable.Slot, body));
        }

        // Each appended ledger is kept, so its entries read back from its replicas however soon its drain completes.
        _state.KeptLedgers = appended.Select(static entry => entry.Ledger.Id).ToFrozenSet();
        _cluster = null;
        foreach (var silo in first.Silos.Reverse().ToList())
        {
            await first.StopSiloAsync(silo, Token).WaitAsync(Patience, Token);
        }

        await first.DisposeAsync();
        var stopped = await ReadMapAsync();
        var predecessors = stopped.Ledgers.Where(static ledger => ledger.IsOpen).ToArray();

        // The late node is a primary whose silo is not the test cluster's primary silo, whose registration starts the
        // controller grain; its registration is held until another node's takeover of one of its ledgers applies.
        NodeId? late = lateRegistration ? predecessors.Select(static ledger => ledger.Primary.Node).First(node => silos[node] != primarySilo) : null;
        var released = Task.CompletedTask;
        if (late is { } held)
        {
            _state.HeldRegistrationOf = held;
            var arrived = _state.Registrations.Hold();
            released = Task.Run(
                async () =>
                {
                    await arrived.WaitAsync(Patience, Token);
                    await EventuallyAsync(
                        () => _state.AppliedInstalls.Any(install => install.Closes.Any(closing => predecessors.Any(ledger => ledger.Id == closing.Id && ledger.Primary.Node == held))),
                        $"a takeover of a ledger of node {held}, whose registration is held");
                    _state.Registrations.Release();
                },
                Token);
        }

        var cluster = await DeployAsync(3, TestContext.Current.CancellationToken);
        await released.WaitAsync(Patience, Token);

        Assert.True(nodes.SetEquals(Nodes(cluster).Select(static entry => entry.Node)));
        var after = await ServedAsync(cluster, ranges: 3, done: value => predecessors.All(ledger => value.Get(ledger.Id) is not { IsOpen: true }));
        Assert.All((await ReadRegistryAsync()).Nodes, entry => Assert.True(entry.Silo.Generation > generations[entry.Node]));
        var map = string.Create(CultureInfo.InvariantCulture, $"stopped {string.Join(", ", stopped.Ledgers.Select(static ledger => $"s{ledger.Id} {ledger.Range} {ledger.Primary.Node} {ledger.IsOpen}"))}; after {string.Join(", ", after.Ledgers.Select(static ledger => $"s{ledger.Id} {ledger.Range} {ledger.Primary.Node} {ledger.IsOpen}"))}");
        foreach (var predecessor in predecessors)
        {
            _ = Assert.Single(_state.MapWrites, write => write.Closed.Contains(predecessor.Id));
            var successor = after.Ledgers.Where(candidate => candidate.Range == predecessor.Range && candidate.Id.Value > predecessor.Id.Value).MinBy(static candidate => candidate.Id.Value);
            Assert.True(predecessor.Replicas.Contains(successor.Primary.Node), $"s{predecessor.Id}'s successor s{successor.Id} has primary {successor.Primary.Node}, none of its replicas: {map}");
            Assert.True(predecessor.Primary.Node != late || successor.Primary.Node != late, $"s{predecessor.Id}'s successor s{successor.Id} kept node {late}, whose registration was held, as primary: {map}");
        }

        var transport = Services(cluster.Silos[0]).GetRequiredService<ILedgerReplicaTransport>();
        foreach (var (ledger, slot, body) in appended)
        {
            var tail = Assert.IsType<LedgerStatus.Closed>(after.Get(ledger.Id)!.Value.Status.Value).T;
            Assert.True(slot < tail, $"s{ledger.Id}'s entry at {slot} lies at or above its recorded tail {tail}.");
            await using var replica = transport.Open(ledger.Replicas[0], ledger.Id, new LedgerReplicaOptions { WindowSize = ledger.K });
            var read = await replica.ReadAsync(slot, tail, Token);
            Assert.True(read.TryGetValue(out ArcBuffer entry), $"s{ledger.Id}'s entry at {slot} read {read}.");
            using (entry)
            {
                Assert.True(entry.ToArray().AsSpan().IndexOf(body) >= 0, $"s{ledger.Id}'s entry at {slot} lacks its appended row.");
            }
        }
    }

    /// <summary>
    /// orleans.md §5.6: a node drains one round at a time, so while manifest replaces are held over many control
    /// intervals, each closed ledger it drains reaches the controller grain's manifest committer once.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task ANodeDrainsEachLedgerOneRoundAtATime()
    {
        var replace = _state.ManifestReplaces.Hold();
        var cluster = await DeployAsync(3, TestContext.Current.CancellationToken);
        _ = await ServedAsync(cluster, ranges: 3);
        await replace.WaitAsync(Patience, Token);

        // Many control intervals of 200 ms pass while the first drain's replace is held.
        await Task.Delay(TimeSpan.FromSeconds(2), TimeProvider.System, Token);

        Assert.NotEmpty(_state.DrainCommits);
        Assert.All(_state.DrainCommits, static commits => Assert.Equal(1, commits.Value));
        _state.ManifestReplaces.Release();
    }

    /// <summary>
    /// orleans.md §5.6, OR-I22: a report call which hangs, as one to an unreachable controller does, is abandoned below the
    /// <c>ReplyIntervals</c> control intervals after which a node's archive-log share falls to its floor, and the node
    /// reports again, rather than waiting out Orleans' response timeout with one report in flight.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task AHungReportIsAbandonedBelowTheShareDeadlineAndTheNodeReportsAgain()
    {
        var cluster = await DeployAsync(1, TestContext.Current.CancellationToken);
        var (_, _, handle) = Nodes(cluster).Single();
        var interval = Services(handle).GetRequiredService<ControlOptions>().ControlInterval;
        await EventuallyAsync(() => _state.ReportsSent.GetValueOrDefault(handle.Name) > 0, "the node to report");
        var held = _state.ReportCalls.Hold();
        await held.WaitAsync(Patience, Token);
        var first = _state.ReportsSent[handle.Name];

        await Task.Delay(interval * (2 * ArchiveLogRequestShare.ReplyIntervals), TimeProvider.System, Token);
        Assert.True(_state.ReportsSent[handle.Name] > first, "The node sent no other report while its first was held.");
        _state.ReportCalls.Release();
    }

    /// <summary>
    /// orleans.md §5.2: a silo whose Ledgers runtime cannot open the map fails to start, and its node's control runtime,
    /// which never served, is stopped, so a call declines.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task ASiloWhoseRuntimeCannotOpenTheMapDoesNotStart()
    {
        _state.FailMapAfterRegistration = true;

        _ = await Assert.ThrowsAnyAsync<Exception>(() => DeployAsync(1, TestContext.Current.CancellationToken));

        var control = Assert.Single(_state.Controls.Values);
        Assert.Equal(LocalNodeControl.ControlState.Stopped, control.State);
        Assert.Equal(TransitionReply.Declined.Instance, await control.ExecuteAsync(Node(1), new Transition.Bootstrap(new TransitionChoice(Node(1), [Node(1)], 1, 1, 0)), static (_, _) => throw new InvalidOperationException("A stopped runtime commits nothing.")));
    }

    /// <summary>
    /// journals.md §8.4, OR-I17: a silo whose Journals fail a start check, constructing the node's <c>JournalStore</c>, does
    /// not start, the check naming the bound which fails; its Journals, which never served, are stopped and answer every
    /// call as stopped, its control plane declines, and the runtime's partial build is disposed, its meter among it. Three
    /// of the four checks never fail first in the composition: on the LSM, whose appends are unbounded, <c>MaxBodyBytes</c>
    /// is always within the archive store's <c>MaxAppendBytes</c>; and the silo's options validation refuses a
    /// <c>MaxQueuedBytes</c> or a <c>MaxProposalBytes</c> below the largest batch before the silo starts
    /// (<c>DurableStorageConfigurationTests</c>). <c>JournalStoreTests</c> checks all four.
    /// </summary>
    [Theory(Timeout = 120_000)]
    [InlineData(JournalStoreLimit.PayloadLimit)]
    public async Task ASiloWhoseJournalsFailAStartCheckDoesNotStart(JournalStoreLimit limit)
    {
        // The default replica payload limit is 32 MiB, which a body of that length and its row pass.
        _state.Node = (_, options) => options.Journals.MaxBodyBytes = limit switch
        {
            JournalStoreLimit.PayloadLimit => 32 << 20,
            JournalStoreLimit.MaxQueuedBytes or JournalStoreLimit.MaxAppendBytes or JournalStoreLimit.MaxProposalBytes => throw new UnreachableException(),
            _ => throw new UnreachableException(),
        };
        var instruments = new ConcurrentDictionary<Instrument, bool>();
        using var listener = ListenForCompletion(instruments);
        using var other = AnotherSilosRuntimeMetrics();

        var failure = await Assert.ThrowsAnyAsync<Exception>(() => DeployAsync(1, TestContext.Current.CancellationToken));

        Assert.Contains(Chain(failure), exception => exception is JournalStoreLimitException refusal && refusal.Limit == limit);
        await AssertNeverStartedAsync(instruments);
    }

    /// <summary>
    /// ledgers.md §8.7, OR-I17: a silo whose writers' <c>MaxProposalBytes</c> holds the largest batch the Journals build, as
    /// its options validation requires, but less than the replica payload limit does not start: its writer registry refuses
    /// the option before the Journals are constructed, and the runtime's partial build is disposed.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task ASiloWhoseWritersHoldLessThanThePayloadLimitDoesNotStart()
    {
        var proposals = new JournalStoreOptions().LargestBatch();
        _state.Node = (_, options) => options.MaxProposalBytes = proposals;
        var instruments = new ConcurrentDictionary<Instrument, bool>();
        using var listener = ListenForCompletion(instruments);
        using var other = AnotherSilosRuntimeMetrics();

        var failure = await Assert.ThrowsAnyAsync<Exception>(() => DeployAsync(1, TestContext.Current.CancellationToken));

        Assert.Contains(Chain(failure), exception => exception is ArgumentOutOfRangeException { ParamName: "options" } refusal && Equals(refusal.ActualValue, proposals));
        await AssertNeverStartedAsync(instruments);
    }

    /// <summary>
    /// OR-I8: a silo whose journal format the archive store's record cannot hold, one holding a line feed, does not start,
    /// the refusal naming the parameter: a new cluster's first registration, which would create the record, fails, so the
    /// registry records no store and the Journals never serve.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task ASiloWhoseJournalFormatTheRecordCannotHoldDoesNotStart()
    {
        _state.Silo = static (_, builder) => builder.Configure<JournaledStateManagerOptions>(static options => options.JournalFormatKey = "json\nlines");

        var failure = await Assert.ThrowsAnyAsync<Exception>(() => DeployAsync(1, TestContext.Current.CancellationToken));

        Assert.Contains(Chain(failure), static exception => exception is ArgumentException { ParamName: "parameters" } refusal
            && refusal.Message.Contains($"'{NodeRuntime.JournalFormatParameter}'", StringComparison.Ordinal));
        // The registration fails before the node's runtime starts, so its Journals were never started.
        Assert.Equal(LocalJournals.JournalsState.Checking, Assert.Single(_state.Journals.Values).State);
        Assert.Null((await ReadRegistryAsync()).ArchiveStore);
    }

    /// <summary>
    /// OR-I8, journals.md §8.4: the first silo records the cluster's journal format and <c>MaxBodyBytes</c> with the archive
    /// store; a silo configured with another of either starts, serves its control plane, and asks to hand its ledgers off,
    /// but its Journals never serve, answering every call as stopped, and it logs event 6239 naming the record's value and
    /// its own. Over the LSM its gate passes, since the parameters are the Journals' alone; over archive logs, whose layout
    /// writes the format into the logs, its gate is refused too, so it writes no log and lists or deletes nothing.
    /// </summary>
    [Theory(Timeout = 120_000)]
    [InlineData(NodeRuntime.JournalFormatParameter, ArchiveStoreKind.Lsm)]
    [InlineData("max-body-bytes", ArchiveStoreKind.Lsm)]
    [InlineData(NodeRuntime.JournalFormatParameter, ArchiveStoreKind.Logs)]
    [InlineData("max-body-bytes", ArchiveStoreKind.Logs)]
    public async Task ASiloWithAnotherJournalFormatOrBodyLimitNeverServesJournals(string parameter, ArchiveStoreKind kind)
    {
        _state.ArchiveStore = kind;
        var cluster = await DeployAsync(1, TestContext.Current.CancellationToken);
        await EventuallyAsync(() => _state.Journals["Primary"].State == LocalJournals.JournalsState.Serving, "the first silo's Journals to serve");
        var body = new JournalStoreOptions().MaxBodyBytes;
        var (stored, configured) = parameter == NodeRuntime.JournalFormatParameter
            ? (new JournaledStateManagerOptions().JournalFormatKey, "orleans-binary")
            : (body.ToString(CultureInfo.InvariantCulture), (body / 2).ToString(CultureInfo.InvariantCulture));
        if (parameter == NodeRuntime.JournalFormatParameter)
        {
            _state.Silo = (silo, builder) => builder.Configure<JournaledStateManagerOptions>(options => options.JournalFormatKey = silo == "Primary" ? options.JournalFormatKey : configured);
        }
        else
        {
            _state.Node = (silo, options) => options.Journals.MaxBodyBytes = silo == "Primary" ? body : body / 2;
        }

        var added = await cluster.StartAdditionalSiloAsync();

        await AssertRefusedAsync(added.Name, 6239);
        Assert.Contains(_state.StopEvents, line => line.Contains($"{added.Name}: Critical 6239:", StringComparison.Ordinal)
            && line.Contains($"{parameter} as {stored}, and this silo is configured with {configured}", StringComparison.Ordinal));
        var gate = Archive(added).Gate.Passed;
        if (kind == ArchiveStoreKind.Lsm)
        {
            Assert.True(gate.IsCompletedSuccessfully);
        }
        else
        {
            Assert.True(gate.IsFaulted);
            Assert.Equal((0, 0), (_state.LogWrites.GetValueOrDefault(added.Name), _state.StoreSweepCalls.GetValueOrDefault(added.Name)));
        }
    }

    /// <summary>
    /// OR-I8, orleans.md §5.6: a refused silo is pending: its node hands each ledger it is primary of off by departure and is
    /// given none back. Its record read is held while the node is checking, which leaves it eligible, until balancing makes
    /// it a primary; once released, the check finds another journal format, and the ledger moves to another node. A new
    /// controller activation gives it none either: with its hand-off requests failed before they are sent, the new
    /// activation learns it is pending from its first report, before which it is no balance target.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task ARefusedSilosLedgersMoveToOtherNodesAndNoneComeBack()
    {
        const string Refused = "Secondary_3";
        _state.Silo = static (silo, builder) => builder.Configure<JournaledStateManagerOptions>(options => options.JournalFormatKey = silo == Refused ? "orleans-binary" : options.JournalFormatKey);
        var reads = _state.RecordReads.Hold(Refused);
        var cluster = await DeployAsync(4, TestContext.Current.CancellationToken);
        await reads.WaitAsync(Patience, Token);
        var node = _state.LocalNodes[Refused].Node;
        _ = await ServedAsync(cluster, ranges: 4, done: value => value.Ledgers.Any(ledger => ledger.IsOpen && ledger.Primary.Node == node));
        Assert.Equal(LocalJournals.JournalsState.Checking, _state.Journals[Refused].State);

        _state.RecordReads.Release();

        await AssertRefusedAsync(Refused, 6239);
        var handedOff = await ServedAsync(cluster, ranges: 4, done: value => !value.Ledgers.Any(ledger => ledger.IsOpen && ledger.Primary.Node == node));

        // Ten balance intervals: no ledger comes back, and none opened since names the node.
        await Task.Delay(TimeSpan.FromSeconds(2), TimeProvider.System, Token);
        var later = await ReadMapAsync();
        Assert.DoesNotContain(later.Ledgers, ledger => ledger.IsOpen && ledger.Primary.Node == node);
        Assert.DoesNotContain(later.Ledgers, ledger => ledger.Id.Value >= handedOff.NextLedgerId.Value && ledger.Replicas.Contains(node));

        // A new activation, which hears no hand-off request, learns from the node's reports alone.
        _state.FaultHandOffsFrom = Refused;
        var faults = _state.HandOffFaults;
        await Controller(cluster).AsReference<IGrainManagementExtension>().DeactivateOnIdle(Token);
        await EventuallyAsync(() => _state.HandOffFaults > faults + 1, $"{Refused}'s hand-off requests to fail after the controller grain's move");
        await Task.Delay(TimeSpan.FromSeconds(2), TimeProvider.System, Token);
        var moved = await ReadMapAsync();
        Assert.DoesNotContain(moved.Ledgers, ledger => ledger.IsOpen && ledger.Primary.Node == node);
        Assert.DoesNotContain(moved.Ledgers, ledger => ledger.Id.Value >= handedOff.NextLedgerId.Value && ledger.Replicas.Contains(node));
        _state.FaultHandOffsFrom = null;
    }

    /// <summary>
    /// OR-I8, OR-I20: a silo joining over a store whose record names another store, here another layout sharing the
    /// store's object, is refused with its own critical event, 6241, naming the field and both values, and stays up.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task ASiloWhoseRecordNamesAnotherStoreIsRefused()
    {
        var cluster = await DeployAsync(1, TestContext.Current.CancellationToken);
        var other = OtherStore("Secondary_1");
        using var record = ArcBuffer.Wrap(ArchiveStoreRecord.Create((await RegisteredStoreAsync()).Id, "logs", new Dictionary<string, string>(), new Dictionary<string, string>()).Encode());
        _ = await other.CreateAsync(ObjectName.Store, record, null, TestContext.Current.CancellationToken);

        var added = await JoinAsync(cluster);

        await AssertRefusedAsync(added.Name, 6241);
        Assert.Contains(_state.StopEvents, line => line.Contains($"{added.Name}: Critical 6241:", StringComparison.Ordinal)
            && line.Contains("holds layout as logs, where lsm is expected", StringComparison.Ordinal));
    }

    /// <summary>OR-I8: a silo joining over a store whose record breaks its format is refused with event 6240, and stays up.</summary>
    [Fact(Timeout = 120_000)]
    public async Task ASiloWhoseRecordIsCorruptIsRefused()
    {
        var cluster = await DeployAsync(1, TestContext.Current.CancellationToken);
        using var record = ArcBuffer.Wrap("not a store's record"u8.ToArray());
        _ = await OtherStore("Secondary_1").CreateAsync(ObjectName.Store, record, null, TestContext.Current.CancellationToken);

        var added = await JoinAsync(cluster);

        await AssertRefusedAsync(added.Name, 6240);
    }

    /// <summary>
    /// OR-I8: a defect the record check meets, one which no repetition changes, is not retried: the Journals are refused
    /// after one read of the record, with event 6242. The archive store raises <see cref="UnreachableException"/> itself
    /// for a state its code holds unreachable.
    /// </summary>
    [Theory(Timeout = 120_000)]
    [InlineData(nameof(InvalidOperationException))]
    [InlineData(nameof(UnreachableException))]
    [InlineData(nameof(InvalidCastException))]
    [InlineData(nameof(IndexOutOfRangeException))]
    [InlineData(nameof(KeyNotFoundException))]
    public async Task ADefectOfTheRecordCheckIsRefusedNotRetried(string defect)
    {
        const string Message = "The test's object store meets a defect reading the store's record.";
        var cluster = await DeployAsync(1, TestContext.Current.CancellationToken);
        _state.RecordReadFault = defect switch
        {
            nameof(InvalidOperationException) => new InvalidOperationException(Message),
            nameof(UnreachableException) => new UnreachableException(Message),
            nameof(InvalidCastException) => new InvalidCastException(Message),
            nameof(IndexOutOfRangeException) => IndexPastTheEnd(),
            nameof(KeyNotFoundException) => new KeyNotFoundException(Message),
            _ => throw new ArgumentOutOfRangeException(nameof(defect), defect, "No such defect."),
        };

        var added = await JoinAsync(cluster);

        await AssertRefusedAsync(added.Name, 6242);
        Assert.Equal(1, _state.RecordReadCounts[added.Name]);
    }

    /// <summary>
    /// OR-I20, orleans.md §5.13: a joining silo's record check takes the store's definite refusal of a request, here a 400,
    /// as definite, after one read, with its own event, 6246, not a defect's; and takes a refusal of its credential or
    /// access, a 401 or 403, as an answer still unknown, which a grant may change: it reads again, its gate pending, and
    /// passes once the store answers.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task ARefusedRequestIsDefiniteAndARefusedAuthorizationIsReadAgain()
    {
        var cluster = await DeployAsync(1, TestContext.Current.CancellationToken);
        _state.RecordReadFault = new ObjectStoreRefusedException("The test's store refuses the request with 400 InvalidQueryParameterValue.", new IOException("400"), isAuthorization: false);
        var refused = await JoinAsync(cluster);

        await AssertRefusedAsync(refused.Name, 6246);
        Assert.Equal(1, _state.RecordReadCounts[refused.Name]);
        Assert.DoesNotContain(_state.StopEvents, line => line.Contains($"{refused.Name}: Critical 6242:", StringComparison.Ordinal));

        _state.RecordReadFault = new ObjectStoreRefusedException("The test's store refuses the credential with 403 AuthorizationPermissionMismatch.", new IOException("403"), isAuthorization: true);
        var waiting = await JoinAsync(cluster);
        await EventuallyAsync(() => _state.RecordReadCounts.GetValueOrDefault(waiting.Name) > 1, "the refused authorization's check to read again");
        Assert.False(Archive(waiting).Gate.Passed.IsCompleted);

        _state.RecordReadFault = null;
        await Archive(waiting).Gate.Passed.WaitAsync(Patience, Token);
        await EventuallyAsync(() => _state.Journals[waiting.Name].State == LocalJournals.JournalsState.Serving, "the Journals to serve once the store answers");
    }

    /// <summary>Returns the <see cref="IndexOutOfRangeException"/> the runtime raises for an index past an array's end; only the runtime raises its type.</summary>
    private static IndexOutOfRangeException IndexPastTheEnd()
    {
        int[] empty = [];
        try
        {
            _ = empty[empty.Length];
        }
        catch (IndexOutOfRangeException exception)
        {
            return exception;
        }

        throw new UnreachableException("An index past an array's end raises IndexOutOfRangeException.");
    }

    /// <summary>
    /// OR-I8: a cancellation the runtime did not request, as a service SDK raises for its own timeout, leaves the record's
    /// answer unknown: the check reads the record again, the Journals answering every call as stopped meanwhile, and they
    /// serve once the store answers.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task ACancellationTheRuntimeDidNotRequestIsRetried()
    {
        var cluster = await DeployAsync(1, TestContext.Current.CancellationToken);
        _state.RecordReadFault = new TaskCanceledException("The test's object store client times out reading the store's record.");
        var silo = (await JoinAsync(cluster)).Name;
        var journals = _state.Journals[silo];

        await EventuallyAsync(() => _state.RecordReadCounts.GetValueOrDefault(silo) > 1, "a cancelled check of the record, checked again");
        Assert.Equal(LocalJournals.JournalsState.Checking, journals.State);
        await AssertStoppedAnswerAsync(journals);

        _state.RecordReadFault = null;
        await EventuallyAsync(() => journals.State == LocalJournals.JournalsState.Serving, "the Journals to serve once the store answers");
    }

    /// <summary>
    /// orleans.md §5.2: the runtime stops its Journals once, and every caller, the lifecycle's stop, the runtime's disposal,
    /// and the silo's disposal, waits for that one disposal, which the store's own second disposal would not wait for.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task EveryStopOfTheJournalsWaitsForTheOneDisposal()
    {
        var cluster = await DeployAsync(1, TestContext.Current.CancellationToken);
        var runtime = Runtime(cluster.Primary!)!;

        var stopped = runtime.StopJournalsAsync();

        Assert.Same(stopped, runtime.StopJournalsAsync());
        await stopped.WaitAsync(Patience, Token);
    }

    /// <summary>
    /// OR-I8, OR-I20, orleans.md §5.9, the two-phase start: a silo joining while the archive store refuses every call
    /// registers at once, with the identity the node registry records, so a silo which waited for its store's record to
    /// learn an identity would not; its archive services then wait for the background check of the record: its gate is
    /// shut, so its node refuses a compaction and drains nothing, and its Journals, which cannot check the record, answer
    /// every call as stopped. Once the store answers, the gate passes and the Journals serve. A joining silo creates nothing,
    /// on either kind of store: not the record, and it writes no log.
    /// </summary>
    [Theory(Timeout = 120_000)]
    [InlineData(ArchiveStoreKind.Lsm)]
    [InlineData(ArchiveStoreKind.Logs)]
    public async Task ASiloJoiningDuringAnArchiveStoreOutageRegistersAndGatesItsArchiveServices(ArchiveStoreKind kind)
    {
        _state.ArchiveStore = kind;
        var cluster = await DeployAsync(1, TestContext.Current.CancellationToken);
        _state.RefuseObjects = true;

        var added = await JoinAsync(cluster);

        var node = _state.LocalNodes[added.Name];
        Assert.True(node.Registered.IsCompletedSuccessfully);
        Assert.Equal(await RegisteredStoreAsync(), RegisteredWith(added));
        var journals = _state.Journals[added.Name];
        await EventuallyAsync(() => _state.RecordReadCounts.GetValueOrDefault(added.Name) > 1, "a failed check of the record, checked again");
        Assert.Equal(LocalJournals.JournalsState.Checking, journals.State);
        Assert.False(Archive(added).Gate.Passed.IsCompleted);
        await AssertStoppedAnswerAsync(journals);
        var assignment = new CompactionAssignment(KeyRange.Root, node.Node, ManifestEpoch: 0);
        _ = await Assert.ThrowsAsync<NodeControlStoppedException>(() => NodeControl(cluster, node.Node).CompactAsync(assignment));
        Assert.Equal((0, 0), (_state.StoreWrites.GetValueOrDefault(added.Name), _state.LogWrites.GetValueOrDefault(added.Name)));

        _state.RefuseObjects = false;
        await EventuallyAsync(() => journals.State == LocalJournals.JournalsState.Serving, "the Journals to serve once the store answers");
        Assert.True(Archive(added).Gate.IsPassed);
        Assert.Equal(kind, Archive(added).Gate.PassedKind);
    }

    /// <summary>
    /// OR-I8, orleans.md §5.2: a silo stopped while its record check waits for the object store stops at once: the check is
    /// cancelled, its Journals stop without having served, its archive gate is cancelled, and the runtime's stop takes far
    /// less than its bound.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task AStopWhileTheRecordCheckWaitsEndsTheCheck()
    {
        var cluster = await DeployAsync(1, TestContext.Current.CancellationToken);
        _state.RefuseObjects = true;
        var added = await JoinAsync(cluster);
        var silo = added.Name;
        var gate = Archive(added).Gate;
        await EventuallyAsync(() => _state.RecordReadCounts.GetValueOrDefault(silo) > 0, "a check of the record");

        await cluster.StopSiloAsync(added, TestContext.Current.CancellationToken).WaitAsync(Patience, Token);
        Assert.True(gate.Passed.IsCanceled);

        Assert.Equal(LocalJournals.JournalsState.Stopped, _state.Journals[silo].State);
        Assert.InRange(StopTime(silo, nameof(NodeLedgers), nameof(ServiceLifecycleStage.Active)), TimeSpan.Zero, TimeSpan.FromSeconds(5));
        Assert.DoesNotContain(_state.StopEvents, line => line.Contains($"{silo}:", StringComparison.Ordinal) && line.Contains("outlasted", StringComparison.Ordinal));
    }

    /// <summary>
    /// orleans.md §5.2, journals.md §9: a stopping silo stops its Journals before it disposes their writer registry, so a
    /// write whose batch its writer has yet to decide is answered as the writer decides it, here <c>Accepted</c> once the
    /// held replica writes reach their quorum, not <c>Ambiguous</c>. The silo is one of three with <c>N = 3</c>, so no
    /// hand-off can be placed and its ledger stays its own through the stop; its bound is the test's patience, so the
    /// outcome rests on the order alone.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task AWriteItsWriterDecidesWhileTheSiloStopsIsAnsweredAsDecided()
    {
        _state.Control = static options => options.TransitionTimeout = Patience;
        var (handle, journals, write) = await HoldAJournalWriteAsync(TestContext.Current.CancellationToken);
        var stop = _cluster!.StopSiloAsync(handle, TestContext.Current.CancellationToken);

        await EventuallyAsync(() => journals.State == LocalJournals.JournalsState.Stopped, "the stopping silo's Journals to stop");
        Assert.False(write.IsCompleted);
        _state.ReplicaWrites.Release();

        _ = Assert.IsType<JournalWriteOutcome.Accepted>((await write.WaitAsync(Patience, Token)).Value);
        await stop.WaitAsync(Patience, Token);
    }

    /// <summary>
    /// orleans.md §5.2: the node's runtime disposed outside the lifecycle's stop, as a silo's disposal does, keeps the order:
    /// its Journals stop before its writer registry is disposed, so a write whose replica writes are released once they
    /// stopped is <c>Accepted</c>.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task ARuntimeDisposedOutsideItsStopStopsItsJournalsBeforeItsWriters()
    {
        _state.Control = static options => options.TransitionTimeout = Patience;
        var (handle, journals, write) = await HoldAJournalWriteAsync(TestContext.Current.CancellationToken);
        var disposal = Services(handle).GetRequiredService<NodeLedgers>().DisposeAsync().AsTask();

        await EventuallyAsync(() => journals.State == LocalJournals.JournalsState.Stopped, "the disposed runtime's Journals to stop");
        Assert.False(write.IsCompleted);
        _state.ReplicaWrites.Release();

        _ = Assert.IsType<JournalWriteOutcome.Accepted>((await write.WaitAsync(Patience, Token)).Value);
        await disposal.WaitAsync(Patience, Token);
        Assert.Null(Runtime(handle));
    }

    /// <summary>
    /// orleans.md §5.2, #404: steps 4 and 5 take at most <c>TransitionTimeout</c> from the cancellation, its callbacks
    /// included: a callback on the runtime's stopping token which runs until the test releases it, as one which waits on a
    /// call Orleans cannot address does, holds no step of the stop, which logs that its cancelled work outlasted the bound
    /// and goes on. Each step's start and end is in the stop report, the step the cancellation's callbacks hold among them.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task ACancellationCallbackWhichRunsLongHoldsNoStepOfTheStop()
    {
        var cluster = await DeployAsync(3, TestContext.Current.CancellationToken);
        _ = await ServedAsync(cluster, ranges: 3, done: value => Balanced(cluster, value));
        var handle = cluster.Silos.First(silo => silo != cluster.Primary);
        var bound = Services(handle).GetRequiredService<ControlOptions>().TransitionTimeout;
        using var released = new ManualResetEventSlim();
        using var registration = Runtime(handle)!.Stopping.Register(() => released.Wait(Patience * 2));
        Task stop;
        try
        {
            stop = cluster.StopSiloAsync(handle, Token);
            var deadline = TimeProvider.System.GetTimestamp();
            while (!_state.StopEvents.Any(line => line.Contains($"{handle.Name}: stop reaches stage {nameof(ServiceLifecycleStage.Active)}", StringComparison.Ordinal)))
            {
                Assert.True(TimeProvider.System.GetElapsedTime(deadline) < bound * 3, $"{handle.Name}'s stop did not leave its Ledgers runtime's stage within {bound * 3} while a cancellation callback ran.");
                await Task.Delay(TimeSpan.FromMilliseconds(50), TimeProvider.System, Token);
            }
        }
        finally
        {
            released.Set();
        }

        await stop.WaitAsync(Patience, Token);
        Assert.InRange(StopTime(handle.Name, nameof(NodeLedgers), nameof(ServiceLifecycleStage.Active)), TimeSpan.Zero, bound + bound / 2);
        Assert.Contains(_state.StopEvents, line => line.Contains($"{handle.Name}:", StringComparison.Ordinal) && line.Contains("Journals' outstanding writes outlasted", StringComparison.Ordinal));
        foreach (var step in new[] { "HandOff", "AdmittedWork", "HandOffAgain", "Cancel" })
        {
            foreach (var phase in new[] { "Begins", "Ends" })
            {
                Assert.Contains(_state.StopEvents, line => line.Contains($"{handle.Name}:", StringComparison.Ordinal) && line.Contains($"stop: step {step} {phase}", StringComparison.Ordinal));
            }
        }
    }

    /// <summary>
    /// orleans.md §5.2 steps 4 and 5: a stopping silo waits for its Journals' disposal at most <c>TransitionTimeout</c>, and a
    /// write whose replicas never answer holds that disposal through the whole bound. A lease the test holds on the node's
    /// reader, as a caller which kept its lease past its work's stop does, holds the runtime's disposal until the stop has
    /// judged it, so the disposal is still running then whatever the thread pool does (#409), and the stop defers it to the
    /// silo's disposal rather than wait again: the runtime's stop takes the bound,
    /// not two, logs the deferral and no defect, and the silo's disposal awaits the Journals' disposal, which answers the
    /// write <c>Ambiguous</c> once its writer is disposed. A call the silo places while it stops, to a node with no active
    /// silo, is refused, which Orleans logs as an error; the test makes one, so the stop always holds that event, and every
    /// other error fails it.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task AJournalsDisposalWhichOutlastsTheStopsBoundIsDeferredAndHoldsNoStop()
    {
        var (handle, _, write) = await HoldAJournalWriteAsync(TestContext.Current.CancellationToken);
        var bound = Services(handle).GetRequiredService<ControlOptions>().TransitionTimeout;
        var runtime = Runtime(handle)!;
        var lease = LeaseALedger(handle);
        var leased = true;
        try
        {
            var stopping = _cluster!.StopSiloAsync(handle, TestContext.Current.CancellationToken);

            // A call the stopping silo places while it stops, to a node with no registered active silo, is refused by the
            // node placement director, which Orleans logs as an error (100071): made here, the stop always holds one.
            await EventuallyAsync(() => Control(handle).State == LocalNodeControl.ControlState.Stopped, $"{handle.Name}'s runtime to cancel its work");
            var unregistered = Services(handle).GetRequiredService<IGrainFactory>().GetGrain<ILedgerStoreGrain>(Guid.NewGuid());
            _ = await Assert.ThrowsAsync<NodePlacementRefusedException>(() => unregistered.ReadTailAsync(new LedgerReplicaTailRequest { Version = LedgerReplicaProtocol.Version, Ledger = 1, WindowSize = 1 }, Token));

            // The lease holds the runtime's disposal until the stop has judged it, then lets the silo's disposal find it ended.
            await EventuallyAsync(() => Logged(handle.Name, "spent the stop's whole bound") || Logged(handle.Name, "left of the stop's bound"), $"{handle.Name}'s stop to judge its runtime's disposal");
            leased = false;
            lease.Dispose();
            await stopping.WaitAsync(Patience, Token);
        }
        finally
        {
            if (leased)
            {
                lease.Dispose();
            }
        }

        Assert.InRange(StopTime(handle.Name, nameof(NodeLedgers), nameof(ServiceLifecycleStage.Active)), bound, bound + bound / 2);
        Assert.Contains(_state.StopEvents, line => line.Contains($"{handle.Name}:", StringComparison.Ordinal) && line.Contains("Journals' outstanding writes outlasted", StringComparison.Ordinal));
        Assert.Contains(_state.StopEvents, line => line.Contains($"{handle.Name}:", StringComparison.Ordinal) && line.Contains("spent the stop's whole bound", StringComparison.Ordinal));
        Assert.Contains(_state.StopEvents, line => line.Contains($"{handle.Name}: Error ", StringComparison.Ordinal) && IsRefusedPlacement(line));
        var errors = _state.StopEvents.Where(line => line.Contains($"{handle.Name}: Error ", StringComparison.Ordinal) && !IsRefusedPlacement(line)).ToList();
        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));
        Assert.True(runtime.StopJournalsAsync().IsCompletedSuccessfully);
        _ = Assert.IsType<JournalWriteOutcome.Ambiguous>((await write.WaitAsync(Patience, Token)).Value);
    }

    /// <summary>
    /// orleans.md §5.2 step 5, #315: step 4's work may end with almost none of the stop's bound left, so a runtime disposal
    /// which outlasts the rest is deferred with a warning and is no defect. A write whose replica writes are held holds the
    /// Journals' disposal until they are released, four fifths of the bound after the runtime's work was cancelled, so step
    /// 4 ends with at most a fifth of it left; a lease the test holds on the node's reader, as a caller which kept its lease
    /// past its work's stop does, holds the runtime's disposal past that rest, and ending it once the deferral is logged
    /// lets the silo's disposal find the disposal ended.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task ARuntimeDisposalWhichOutlastsWhatStepFourLeftOfTheBoundIsDeferredAsNoDefect()
    {
        var (test, since) = (TestContext.Current.Test, DebugAssertions.RaisedCount);
        var (handle, _, write) = await HoldAJournalWriteAsync(TestContext.Current.CancellationToken);
        var bound = Services(handle).GetRequiredService<ControlOptions>().TransitionTimeout;
        var lease = LeaseALedger(handle);
        var leased = true;
        try
        {
            var stopping = _cluster!.StopSiloAsync(handle, TestContext.Current.CancellationToken);
            await EventuallyAsync(() => Control(handle).State == LocalNodeControl.ControlState.Stopped, $"{handle.Name}'s runtime to cancel its work");

            // The stop's bound began before its control plane stopped, so step 4 ends with at most a fifth of it left.
            await Task.Delay(bound - bound / 5, TimeProvider.System, Token);
            _state.ReplicaWrites.Release();
            await EventuallyAsync(() => Logged(handle.Name, "left of the stop's bound") || Logged(handle.Name, "spent the stop's whole bound"), $"{handle.Name}'s stop to wait for its runtime's disposal");

            Assert.True(Logged(handle.Name, "outlasted what its cancelled work left of the stop's bound"));
            Assert.False(Logged(handle.Name, "spent the stop's whole bound"));
            Assert.False(Logged(handle.Name, "a defect"));
            leased = false;
            lease.Dispose();
            await stopping.WaitAsync(Patience, Token);
        }
        finally
        {
            if (leased)
            {
                lease.Dispose();
            }
        }

        Assert.False(Logged(handle.Name, "a defect"));
        Assert.False(Logged(handle.Name, "did not end within"));
        Assert.Empty(DebugAssertions.TakeRaisedBy(test, since));
        _ = Assert.IsType<JournalWriteOutcome.Accepted>((await write.WaitAsync(Patience, Token)).Value);
    }

    /// <summary>
    /// orleans.md §5.2 step 5, #315: a runtime disposal which began once step 4's work ended within the stop's bound, and
    /// still runs a whole <c>TransitionTimeout</c> into the silo's disposal, is the defect. Step 4 ends at once, with no
    /// write outstanding, and a lease the test holds on the node's reader, as a caller which kept its lease past its work's
    /// stop does, holds the runtime's disposal through both waits: the stop defers it with a warning and no defect, and the
    /// silo's disposal logs and asserts the defect.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task ARuntimeDisposalWhichOutlastsAWholeBoundOfTheSilosDisposalIsADefect()
    {
        var (test, since) = (TestContext.Current.Test, DebugAssertions.RaisedCount);
        var cluster = await DeployAsync(3, TestContext.Current.CancellationToken);
        _ = await ServedAsync(cluster, ranges: 3, done: value => Balanced(cluster, value));
        var handle = cluster.Silos.First(silo => silo != cluster.Primary);
        var lease = LeaseALedger(handle);
        try
        {
            var stopping = cluster.StopSiloAsync(handle, TestContext.Current.CancellationToken);
            await EventuallyAsync(() => Logged(handle.Name, "left of the stop's bound") || Logged(handle.Name, "spent the stop's whole bound"), $"{handle.Name}'s stop to wait for its runtime's disposal");

            Assert.True(Logged(handle.Name, "outlasted what its cancelled work left of the stop's bound"));
            Assert.False(Logged(handle.Name, "a defect"));
            Assert.Null(DebugAssertions.FirstRaisedBy(test, since));
            var stopped = await Record.ExceptionAsync(() => stopping.WaitAsync(Patience, Token));

            // The silo's disposal of its services logs and absorbs the assertion's exception, so the stop completes.
            Assert.Null(stopped);
            Assert.True(Logged(handle.Name, "did not end within"), string.Join(Environment.NewLine, _state.StopEvents));
            Assert.Contains(_state.StopEvents, line => line.Contains($"{handle.Name}: Error ", StringComparison.Ordinal) && line.Contains("of the silo's disposal though its cancelled work", StringComparison.Ordinal) && line.Contains("a defect", StringComparison.Ordinal));
#if DEBUG
            var assertion = Assert.Single(DebugAssertions.TakeRaisedBy(test, since));
            Assert.Contains("outlasted a whole bound", assertion.Message, StringComparison.Ordinal);
#endif
        }
        finally
        {
            lease.Dispose();
        }
    }

    /// <summary>
    /// Leases an open ledger through <paramref name="handle"/>'s node reader, whose disposal, the runtime's among it, waits
    /// for every lease to end (ledgers.md §2.5): the lease a caller holds past its work's stop, held until it is disposed.
    /// </summary>
    private static LedgerReader.LedgerLease LeaseALedger(SiloHandle handle)
    {
        var runtime = Assert.IsType<NodeRuntime>(Services(handle).GetRequiredService<NodeLedgers>().Runtime);
        return runtime.Reader.LeaseLedger(runtime.View.Current.Ledgers.First(static ledger => ledger.IsOpen));
    }

    /// <summary>Returns whether <paramref name="silo"/> logged a warning or worse containing <paramref name="text"/>.</summary>
    private bool Logged(string silo, string text) =>
        _state.StopEvents.Any(line => line.Contains($"{silo}:", StringComparison.Ordinal) && line.Contains(text, StringComparison.Ordinal));

    /// <summary>
    /// Returns whether a recorded event is Orleans' log (100071) of a placement the node placement director refused, its
    /// answer for a node with no registered active silo (orleans.md §5.3), which the caller resends: no defect.
    /// </summary>
    private static bool IsRefusedPlacement(string line) =>
        line.Contains(" Error 100071: ", StringComparison.Ordinal) && line.Contains($"({nameof(NodePlacementRefusedException)}: ", StringComparison.Ordinal);

    /// <summary>
    /// Asserts that the silo's start built no runtime which outlived it: its Journals are stopped and answer every call as
    /// stopped, its control plane declines a transition, and every node instrument the partial runtime's build published,
    /// on its meter, which carries its node's tag, completed, as its disposed meter's do. Another test's silos in the
    /// process publish on meters of their own, tagged with their nodes.
    /// </summary>
    private async Task AssertNeverStartedAsync(ConcurrentDictionary<Instrument, bool> instruments)
    {
        var journals = Assert.Single(_state.Journals.Values);
        Assert.Equal(LocalJournals.JournalsState.Stopped, journals.State);
        await AssertStoppedAnswerAsync(journals);
        var control = Assert.Single(_state.Controls.Values);
        Assert.Equal(TransitionReply.Declined.Instance, await control.ExecuteAsync(Node(1), new Transition.Bootstrap(new TransitionChoice(Node(1), [Node(1)], 1, 1, 0)), static (_, _) => throw new InvalidOperationException("A stopped runtime commits nothing.")));
        var node = Assert.Single(_state.LocalNodes.Values).Node.ToString();
        var built = instruments.Where(instrument => instrument.Key.Meter.Tags?.Contains(new("node", node)) == true).ToList();
        Assert.NotEmpty(built);
        Assert.All(built, static instrument => Assert.True(instrument.Value, $"{instrument.Key.Name} outlived the failed start."));
    }

    /// <summary>
    /// orleans.md §5.6, §7.2: when the reply to an applied install cannot be decoded, the executor's commit call relays an
    /// unknown outcome, never a defect which abandons the opened ledger's lifetime, so the node serves the ledger it
    /// installed, with no takeover.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task ADecodeFailureOfAnAppliedReplyAbandonsNoLifetime()
    {
        _state.Control = static options => options.Replicas = 1;
        _state.FailNextMapReply = 1;
        var cluster = await DeployAsync(1, TestContext.Current.CancellationToken);

        var map = await ServedAsync(cluster, ranges: 1);
        await Task.Delay(TimeSpan.FromSeconds(1), TimeProvider.System, Token);

        Assert.Equal(0, _state.FailNextMapReply);
        var root = Assert.Single(map.Ledgers);
        Assert.Equal(root.Id, Assert.Single((await ReadMapAsync()).Ledgers).Id);
    }

    /// <summary>
    /// ledgers.md §8.5: a replica a delayed write created on a node its ledger never listed, after the node reconciled the
    /// ledger's installation, is dropped by the runtime's periodic sweep.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task AStrayReplicaIsSweptAway()
    {
        var cluster = await DeployAsync(4, TestContext.Current.CancellationToken);
        // An open ledger, which no drain or drop removes, of a balanced map, which moves no ledger meanwhile.
        var map = await ServedAsync(cluster, ranges: 4, done: value => Balanced(cluster, value));
        var root = map.Ledgers.First(static ledger => ledger.IsOpen);
        var (stray, local, handle) = Nodes(cluster).First(entry => !root.Replicas.Contains(entry.Node));
        await Runtime(handle)!.Reconciler!.ReconcileAsync(Token);
        await using (var replica = Services(handle).GetRequiredService<ILedgerReplicaTransport>().Open(stray, root.Id, new LedgerReplicaOptions { WindowSize = root.K }))
        {
            using var writer = new ArcBufferWriter();
            writer.Write([1]);
            using var payload = writer.ConsumeSlice(1);
            // The write created the stray, which a sweep may already have dropped by the time the store is listed.
            _ = Assert.IsType<ReplicaWriteOutcome.Written>((await replica.WriteAsync(Slot.Zero, payload, Slot.Zero, recovery: false, Token)).Value);
        }

        await EventuallyAsync(async () => !(await local.Store.ListReplicasAsync(Token)).Contains(root.Id), "the sweep drops the stray replica");
    }

    private static List<(NodeId Node, LocalNode Local, SiloHandle Handle)> Nodes(TestCluster cluster) =>
        [.. cluster.Silos.Select(static silo => (Services(silo).GetRequiredService<LocalNode>().Node, Services(silo).GetRequiredService<LocalNode>(), silo))];

    private static IServiceProvider Services(SiloHandle silo) => ((InProcessSiloHandle)silo).SiloHost.Services;

    private static ArcBuffer Payload(ReadOnlySpan<byte> body)
    {
        using var writer = new ArcBufferWriter();
        writer.Write(body);
        return writer.ConsumeSlice(body.Length);
    }

    private static NodeRuntime? Runtime(SiloHandle silo) => Services(silo).GetRequiredService<NodeLedgers>().Runtime;

    /// <summary>Waits until each running silo has preloaded exactly the ranges of the open ledgers its node is primary of.</summary>
    private async Task PreloadsFollowThePrimariesAsync(TestCluster cluster) =>
        await EventuallyAsync(
            async () =>
            {
                var map = await ReadMapAsync();
                return Nodes(cluster).All(entry => Runtime(entry.Handle)?.LsmStore?.Tails.Preloaded.ToHashSet().SetEquals(NodeLedgers.PrimaryRanges(map, entry.Node)) == true);
            },
            "each silo to preload the ranges its node is primary of");

    private static IJournalStoreGrain JournalNode(TestCluster cluster, NodeId node) => cluster.Client.GetGrain<IJournalStoreGrain>(node.Value);

    /// <summary>Returns a journal key whose row key <paramref name="ledger"/>'s range holds.</summary>
    private static string KeyIn(LedgerMetadata ledger) =>
        Enumerable.Range(0, int.MaxValue).Select(static index => string.Create(CultureInfo.InvariantCulture, $"journal-{index}")).First(key => ledger.Range.Contains(JournalRowKey.Encode(key).Span));

    /// <summary>Forwards an <c>Append</c> of <paramref name="body"/> to the journal <paramref name="key"/> at <paramref name="expected"/> through <paramref name="grain"/>, as the router sends one.</summary>
    private static async Task<JournalForwardedWriteOutcome> ForwardAsync(IJournalStoreGrain grain, string key, JournalVersion expected, byte[] body)
    {
        using var payload = Payload(body);
        using var record = JournalEntryRecord.Append(JournalRowKey.Encode(key), expected, payload);
        using var bytes = record.ToArcBuffer();
        var sent = WireBuffers.CopyToOrleans(bytes);
        try
        {
            return await grain.WriteAsync(new JournalForwardedEntry { Key = key, Record = sent }, Token);
        }
        finally
        {
            sent.Dispose();
        }
    }

    /// <summary>The answer a test gives a journal write the Journals do not take: <c>Moved</c> naming no owner, no effect.</summary>
    private static LocalJournalWriteOutcome StoppedAnswer { get; } = new(new JournalMoved(Owner: null));

    /// <summary>Returns <paramref name="exception"/> and every exception it wraps, depth first.</summary>
    private static IEnumerable<Exception> Chain(Exception exception)
    {
        yield return exception;
        var inner = exception is AggregateException aggregate ? aggregate.InnerExceptions : (IEnumerable<Exception>)(exception.InnerException is { } one ? [one] : []);
        foreach (var wrapped in inner.SelectMany(Chain))
        {
            yield return wrapped;
        }
    }

    /// <summary>
    /// Deploys three silos with <c>N = 3</c> and retires one of the other silos so only one other active node
    /// (<c>&lt; Q = 2</c>) remains and the stopping silo can hand none of its ledgers off, then writes an <c>Append</c>
    /// through the Journals of a silo other than the test cluster's primary, to a key of a ledger its node is primary of,
    /// while that ledger's replica writes are held at their callees, so the batch waits for its quorum; answers the silo,
    /// its Journals, and the write, once a replica write is held.
    /// </summary>
    private async Task<(SiloHandle Handle, LocalJournals Journals, Task<LocalJournalWriteOutcome> Write)> HoldAJournalWriteAsync(CancellationToken cancellationToken)
    {
        var cluster = await DeployAsync(3, cancellationToken);
        var map = await ServedAsync(cluster, ranges: 3, done: value => Balanced(cluster, value));
        var (node, _, handle) = Nodes(cluster).First(entry => entry.Handle != cluster.Primary && map.Ledgers.Any(ledger => ledger.IsOpen && ledger.Primary.Node == entry.Node));
        var retired = Nodes(cluster).First(entry => entry.Handle != cluster.Primary && entry.Handle != handle).Node;
        await Controller(cluster).RetireNodeAsync(retired);
        await EventuallyAsync(async () => (await Controller(cluster).GetRetirementAsync(retired)).Value is NodeStanding.Retired, "the third silo to retire so hand-off has fewer than Q targets");
        var resolver = Services(handle).GetRequiredService<NodeRegistryResolver>();
        resolver.RequestReread();
        await EventuallyAsync(() => resolver.Current?.Find(retired) is { IsRetired: true }, $"{handle.Name}'s registry view to observe the retirement");
        var ledger = map.Ledgers.First(ledger => ledger.IsOpen && ledger.Primary.Node == node);
        var key = Enumerable.Range(0, int.MaxValue).Select(static index => JournalRowKey.Encode(string.Create(CultureInfo.InvariantCulture, $"journal-{index}"))).First(key => ledger.Range.Contains(key.Span));
        var journals = _state.Journals[handle.Name];
        await EventuallyAsync(() => journals.State == LocalJournals.JournalsState.Serving, $"{handle.Name}'s Journals to serve");
        _state.HeldLedger = ledger.Id;
        var held = _state.ReplicaWrites.Hold();
        var write = WriteAsync();
        await held.WaitAsync(Patience, Token);
        return (handle, journals, write);

        async Task<LocalJournalWriteOutcome> WriteAsync()
        {
            using var payload = Payload([1, 2, 3]);
            using var record = JournalEntryRecord.Append(key, JournalVersion.Origin, payload);
            return await journals.WriteAsync(record, Token);
        }
    }

    /// <summary>Asserts that <paramref name="journals"/> answer a write and a read <c>Moved</c> naming no owner, reaching no store.</summary>
    private static async Task AssertStoppedAnswerAsync(LocalJournals journals)
    {
        journals.BeforeCall = static () => throw new InvalidOperationException("Journals which do not serve take no call.");
        try
        {
            using var record = JournalEntryRecord.Delete(JournalRowKey.Encode("k"), JournalVersion.Origin, DateTimeOffset.UnixEpoch);
            Assert.Equal(StoppedAnswer, await journals.WriteAsync(record, Token));
            Assert.Equal(new JournalReadOutcome(new JournalMoved(Owner: null)), await journals.ReadAsync(JournalRowKey.Encode("k"), Token));
        }
        finally
        {
            journals.BeforeCall = null;
        }
    }

    /// <summary>Returns the time between two stages a silo's stop reached, by the system clock.</summary>
    private TimeSpan StopTime(string silo, string from, string to) =>
        TimeProvider.System.GetElapsedTime(_state.StopStages[(silo, from)], _state.StopStages[(silo, to)]);

    /// <summary>
    /// Waits until a refused silo's Journals are refused, its critical event <paramref name="eventId"/> logged before, and its
    /// node has asked the controller grain to hand its ledgers off more than once, as it does each control interval; and
    /// checks that it still serves its control plane and answers journal calls as stopped.
    /// </summary>
    private async Task AssertRefusedAsync(string silo, int eventId)
    {
        var journals = _state.Journals[silo];
        await EventuallyAsync(() => journals.State == LocalJournals.JournalsState.Refused, $"{silo}'s Journals to be refused");
        Assert.Equal(LocalJournals.JournalsState.Refused, _state.Statuses[silo].Journals);
        Assert.Contains(_state.StopEvents, line => line.Contains($"{silo}: Critical {eventId}:", StringComparison.Ordinal));
        var node = _state.LocalNodes[silo].Node;
        await EventuallyAsync(() => _state.HandOffRequests.GetValueOrDefault(node) > 1, $"{silo}'s node to keep asking to hand off");
        Assert.Equal(LocalNodeControl.ControlState.Serving, _state.Controls[silo].State);
        await AssertStoppedAnswerAsync(journals);
    }

    /// <summary>
    /// Listens to the node's instruments on the composition's meter, recording each published and whether its measurements
    /// completed, as a disposed meter's do, from every silo the process runs; each runtime's meter carries its node's tag.
    /// </summary>
    private static MeterListener ListenForCompletion(ConcurrentDictionary<Instrument, bool> instruments)
    {
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == DurableStorageMeter.Name && instrument.Name.StartsWith("orleans.durable.storage.node.", StringComparison.Ordinal))
                {
                    instruments[instrument] = false;
                    listener.EnableMeasurementEvents(instrument);
                }
            },
            MeasurementsCompleted = (instrument, _) => instruments[instrument] = true,
        };
        listener.Start();
        return listener;
    }

    /// <summary>
    /// Returns the instruments of a live runtime of another node, as another test's silo in the process publishes them for
    /// as long as it runs (#309): a listener which judged them with the failed silo's would find them never completed.
    /// </summary>
    private static NodeMetrics AnotherSilosRuntimeMetrics() =>
        new(new NodeId(Guid.NewGuid()), new NodePressure(TimeSpan.FromSeconds(1), TimeProvider.System, NullLogger<NodePressure>.Instance));

    private LocalNodeControl Control(SiloHandle silo) => _state.Controls[silo.Name];

    /// <summary>
    /// <paramref name="readers"/> readers each read the journal <paramref name="key"/> again and again through a new
    /// storage instance of <paramref name="storage"/> for <paramref name="duration"/>, watched by a probe of the owner's
    /// reserved bytes.
    /// </summary>
    private static async Task<MeasurementSample> ForwardedReadsAsync(DurableJournalStorageProvider storage, ForwardedReads owner, string key, long length, int readers, TimeSpan duration)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var total = 0L;
        using var probe = new MeasurementProbe(() => owner.ReservedBytes, new DurableStorageOptions().MaxForwardedReadBytes);
        stop.CancelAfter(duration);
        var tasks = Enumerable.Range(0, readers).Select(reader => Task.Run(
            async () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    var consumer = new SkippingConsumer();
                    await storage.CreateStorage(new JournalId(key)).ReadAsync(consumer, Token);
                    Assert.Equal(length, consumer.Bytes);
                    _ = Interlocked.Add(ref total, length);
                }
            },
            Token)).ToList();

        await probe.WatchAsync(stop.Token);
        await Task.WhenAll(tasks);
        return probe.Sample(Interlocked.Read(ref total));
    }

    /// <summary>Writes <paramref name="batches"/> batches of one 64 KiB row each to <paramref name="ledger"/> through its primary's writer.</summary>
    private static async Task WriteAsync(TestCluster cluster, LedgerMetadata ledger, int batches)
    {
        var handle = Nodes(cluster).Single(entry => entry.Node == ledger.Primary.Node).Handle;
        LedgerWriter? writer = null;
        await EventuallyAsync(() => (writer = Runtime(handle)!.Writers.TryGet(ledger.Id)) is not null, $"s{ledger.Id}'s primary to serve it");
        byte[] key = [Enumerable.Range(0, 256).Select(static value => (byte)value).First(value => ledger.Range.Contains([value]))];
        for (var batch = 0; batch < batches; batch++)
        {
            using var payload = Payload(new byte[64 << 10]);
            using var row = new Row(key, RowKind.Append, payload);
            _ = Assert.IsType<ProposalOutcome.Durable>((await writer!.ProposeAsync([row], Token)).Value);
        }
    }

    /// <summary>
    /// Listens to the composition's meter, recording each gauge's last measurement by its instrument, its <c>node</c> tag,
    /// and its <c>ledger</c> or <c>status</c> tag, from every silo the process runs.
    /// </summary>
    private static MeterListener Listen(ConcurrentDictionary<(string Instrument, string? Node, string? Tag), long> gauges)
    {
        var listener = new MeterListener
        {
            InstrumentPublished = static (instrument, listener) =>
            {
                if (instrument.Meter.Name == DurableStorageMeter.Name)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            },
        };

        void Record<T>(Instrument instrument, T value, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
            where T : struct
        {
            var (node, tag, cause) = ((string?)null, (string?)null, (string?)null);
            foreach (var pair in tags)
            {
                node = pair.Key == "node" ? pair.Value as string : node;
                tag = pair.Key is "ledger" or "status" ? pair.Value as string : tag;
                cause = pair.Key == "cause" ? pair.Value as string : cause;
            }

            // A blocked drain's gauge carries its cause beside its ledger.
            tag = cause is null ? tag : $"{tag}:{cause}";
            gauges[(instrument.Name, node, tag)] = Convert.ToInt64(value, CultureInfo.InvariantCulture);
        }

        listener.SetMeasurementEventCallback<long>(Record);
        listener.SetMeasurementEventCallback<int>(Record);
        listener.Start();
        return listener;
    }

    private static NodePressure Pressure(TestCluster cluster, NodeId node) =>
        Services(Nodes(cluster).Single(entry => entry.Node == node).Handle).GetRequiredService<NodePressure>();

    private static IClusterControllerGrain Controller(TestCluster cluster) => cluster.Client.GetGrain<IClusterControllerGrain>(IClusterControllerGrain.Key);

    private static INodeControlGrain NodeControl(TestCluster cluster, NodeId node) => cluster.Client.GetGrain<INodeControlGrain>(node.Value);

    private static async Task EventuallyAsync(Func<bool> condition, string what) => await EventuallyAsync(() => Task.FromResult(condition()), what);

    private static async Task EventuallyAsync(Func<Task<bool>> condition, string what)
    {
        var deadline = TimeProvider.System.GetUtcNow() + Patience;
        while (!await condition())
        {
            Assert.True(TimeProvider.System.GetUtcNow() < deadline, $"Waited {Patience} for {what}.");
            await Task.Delay(TimeSpan.FromMilliseconds(50), TimeProvider.System, Token);
        }
    }

    /// <summary>
    /// Holds every tail read of one open ledger, and sends a <c>Replace</c> of it to another node, not the primary silo,
    /// whose recovery then waits at the held reads; answers that node, its silo, the held execution, and another open
    /// ledger. It waits for a balanced map first, so no balancing transition of the controller's changes the ledger before
    /// the held one reads its tail.
    /// </summary>
    /// <summary>
    /// Writes a chunks grain's state of three 500 KiB chunks through a silo whose node is not the grain's journal's primary,
    /// where the grain activates, deactivates it, and reads the state back from a new activation on that silo.
    /// </summary>
    private async Task RecoverChunksAsync<TGrain>(string? providerName, CancellationToken cancellationToken)
        where TGrain : IChunksGrain
    {
        _state.ProviderName = providerName;
        var cluster = await DeployAsync(3, cancellationToken);
        _ = await ServedAsync(cluster, ranges: 3, done: value => Balanced(cluster, value));
        var map = await EverySiloFollowsTheMapAsync(cluster);
        await EventuallyAsync(() => Nodes(cluster).All(entry => _state.Journals[entry.Handle.Name].State == LocalJournals.JournalsState.Serving), "every silo's Journals to serve");
        var key = JournalId.FromGrainId(cluster.Client.GetGrain<TGrain>("chunks").GetGrainId()).Value;
        var primary = Assert.NotNull(map.FindOpen(JournalRowKey.Encode(key).Span)).Primary.Node;
        var reader = Nodes(cluster).First(entry => entry.Node != primary).Handle;
        var grain = Services(reader).GetRequiredService<IGrainFactory>().GetGrain<TGrain>("chunks");
        byte[][] chunks = [.. Enumerable.Range(1, 3).Select(static index => Enumerable.Repeat((byte)index, 500 << 10).ToArray())];
        foreach (var chunk in chunks)
        {
            _ = await grain.AppendAsync(chunk);
        }

        Assert.Equal(reader.SiloAddress, await grain.SiloAsync());
        var first = await grain.ActivationAsync();
        await grain.DeactivateAsync();
        await EventuallyAsync(async () => await grain.ActivationAsync() != first, "a new activation of the grain");

        Assert.Equal(reader.SiloAddress, await grain.SiloAsync());
        Assert.Equal(chunks.SelectMany(static chunk => chunk), await grain.ReadAsync());
    }

    private async Task<(NodeId Executor, SiloHandle Handle, Task<TransitionReply> Execution, LedgerMetadata Other)> HoldAnExecutionAsync(TestCluster cluster)
    {
        var map = await ServedAsync(cluster, ranges: 4, done: value => Balanced(cluster, value));
        var (executor, handle) = Nodes(cluster).Where(entry => entry.Handle != cluster.Primary).Select(static entry => (entry.Node, entry.Handle)).First();
        var open = map.Ledgers.Where(ledger => ledger.IsOpen && ledger.Primary.Node != executor).ToArray();
        var (held, other) = (open[0], open[1]);
        _state.HeldLedger = held.Id;
        var reads = _state.TailReads.Hold();
        var execution = NodeControl(cluster, executor).ExecuteAsync(new Transition.Replace(held.Id, await ChoiceAsync(cluster, executor)));
        await reads.WaitAsync(Patience, Token);
        return (executor, handle, execution, other);
    }

    /// <summary>Returns a choice of three active nodes, <paramref name="executor"/> first, at the registry's and the map's current revision and epoch.</summary>
    private async Task<TransitionChoice> ChoiceAsync(TestCluster cluster, NodeId executor)
    {
        var register = await _state.MetaStore.GetRegisterAsync(_state.Cluster, NodeRegistryValue.RegisterName, Token);
        var (registry, _) = await NodeRegistryCodec.ReadAsync(register, Token);
        NodeId[] others = [.. Nodes(cluster).Select(static entry => entry.Node).Where(node => node != executor).Take(2)];
        return new TransitionChoice(executor, [executor, .. others], 4, registry.Revision, (await ReadMapAsync()).Epoch);
    }

    /// <summary>
    /// Waits until every silo's map view reaches the register's epoch, which only report replies and executions bring, and
    /// returns the register's value at that epoch.
    /// </summary>
    private async Task<LedgerMapValue> EverySiloFollowsTheMapAsync(TestCluster cluster)
    {
        var map = await ReadMapAsync();
        await EventuallyAsync(
            () => cluster.Silos.All(silo => Runtime(silo) is { } runtime && runtime.View.Current.Epoch >= map.Epoch),
            $"every silo's view to reach epoch {map.Epoch}");
        return map;
    }

    /// <summary>
    /// Waits until every range is served and a node on a silo which <paramref name="eligible"/> accepts is the primary of an
    /// open ledger, and returns that node and its silo. A test never stops the primary silo, which hosts the test cluster's
    /// membership table.
    /// </summary>
    private async Task<(NodeId Node, SiloHandle Handle)> PrimaryAsync(TestCluster cluster, Func<SiloHandle, bool> eligible)
    {
        var map = await ServedAsync(cluster, ranges: cluster.Silos.Count, done: value => Primaries(cluster, value, eligible).Any());
        return Primaries(cluster, map, eligible).First();
    }

    /// <summary>Returns whether every node's primaries in <paramref name="map"/> are within one of each other's, where primary balance leaves them.</summary>
    private static bool Balanced(TestCluster cluster, LedgerMapValue map)
    {
        var primaries = Nodes(cluster).Select(entry => map.Ledgers.Count(ledger => ledger.IsOpen && ledger.Primary.Node == entry.Node)).ToArray();
        return primaries.Max() - primaries.Min() <= 1;
    }

    private static IEnumerable<(NodeId Node, SiloHandle Handle)> Primaries(TestCluster cluster, LedgerMapValue map, Func<SiloHandle, bool> eligible) =>
        Nodes(cluster)
            .Where(entry => eligible(entry.Handle) && map.Ledgers.Any(ledger => ledger.IsOpen && ledger.Primary.Node == entry.Node))
            .Select(static entry => (entry.Node, entry.Handle));

    /// <summary>
    /// Waits until the map lists at least <paramref name="ranges"/> open ledgers, each served by its primary's silo, and
    /// <paramref name="done"/> holds, and returns that map.
    /// </summary>
    private async Task<LedgerMapValue> ServedAsync(TestCluster cluster, int ranges, Func<LedgerMapValue, bool>? done = null)
    {
        var deadline = TimeProvider.System.GetUtcNow() + Patience;
        while (true)
        {
            var map = await ReadMapAsync();
            var runtimes = cluster.Silos.Select(Runtime).OfType<NodeRuntime>().ToDictionary(static runtime => runtime.Controller.Node);
            var open = map.Ledgers.Where(static ledger => ledger.IsOpen).ToArray();
            var served = open.Count(ledger => runtimes.TryGetValue(ledger.Primary.Node, out var runtime) && runtime.Writers.TryGet(ledger.Id) is not null);
            var finished = done?.Invoke(map) ?? true;
            if (open.Length >= ranges && served == open.Length && finished)
            {
                return map;
            }

            Assert.True(
                TimeProvider.System.GetUtcNow() < deadline,
                $"The map at epoch {map.Epoch} lists {open.Length} open ledgers, {served} served, wanting {ranges}; the test's condition {(finished ? "holds" : "does not hold")}.");
            await Task.Delay(TimeSpan.FromMilliseconds(200), TimeProvider.System, Token);
        }
    }

    /// <summary>Returns the archive store the node registry records, which the cluster's first registration recorded.</summary>
    private async Task<ArchiveStoreIdentity> RegisteredStoreAsync() => Assert.NotNull((await ReadRegistryAsync()).ArchiveStore);

    private async Task<NodeRegistryValue> ReadRegistryAsync() =>
        (await NodeRegistryCodec.ReadAsync(await _state.MetaStore.GetRegisterAsync(_state.Cluster, NodeRegistryValue.RegisterName, Token), Token)).Value;

    private Task<LedgerMapValue> ReadMapAsync() => ReadMapAsync(_state);

    private static async Task<LedgerMapValue> ReadMapAsync(TestState state)
    {
        var register = await state.MetaStore.GetRegisterAsync(state.Cluster, LedgerMapRegister.Name, Token);
        if (await register.ReadAsync(Token) is not { } entry)
        {
            return LedgerMapValue.Empty;
        }

        using (entry)
        {
            return Assert.IsType<LedgerMapValue>(LedgerMapValue.Decode(entry.Value.ToArray()).Value);
        }
    }

    private Task<TestCluster> DeployAsync(short silos, CancellationToken cancellationToken) => DeployAsync(silos, fastMembership: false, cancellationToken);

    /// <summary>Starts one more silo, which joins the cluster, and waits until every silo's registry view lists every node.</summary>
    private static async Task<SiloHandle> JoinAsync(TestCluster cluster)
    {
        var added = await cluster.StartAdditionalSiloAsync().WaitAsync(Patience, Token);
        await EveryResolverListsEveryNodeAsync(cluster);
        return added;
    }

    /// <summary>
    /// Returns a store of the silo named <paramref name="silo"/> of its own, apart from the cluster's, which it runs over once
    /// it starts: an object store, and on archive logs archive logs in the same container.
    /// </summary>
    private InMemoryObjectStore OtherStore(string silo)
    {
        var objects = new InMemoryObjectStore();
        _state.OtherStores[silo] = (objects, new MemoryArchiveLogStore(_state.Logs.Layout));
        return objects;
    }

    /// <summary>Returns the silo's archive store and gate.</summary>
    private static SiloArchive Archive(SiloHandle silo) => Services(silo).GetRequiredService<SiloArchive>();

    /// <summary>Returns the archive store <paramref name="silo"/>'s node registered with, which its registration recorded (OR-I20).</summary>
    private static ArchiveStoreIdentity RegisteredWith(SiloHandle silo) =>
        Assert.IsType<NodeRegistration>(Services(silo).GetRequiredService<LocalNode>().Registered.Result).ArchiveStore;

    /// <summary>
    /// Waits until every silo's registry view names every running silo: a stopping silo counts the active nodes its view
    /// shows to decide whether a hand-off can be placed (orleans.md §5.2), and a node's grains are placed only on the silo
    /// the caller's view names (OR-I14). After a whole-cluster restart the register still lists every node at its previous
    /// silo, so the views must name the silos running now, not merely as many nodes.
    /// </summary>
    private static async Task EveryResolverListsEveryNodeAsync(TestCluster cluster)
    {
        var resolvers = cluster.Silos.Select(static silo => Services(silo).GetRequiredService<NodeRegistryResolver>()).ToArray();
        foreach (var resolver in resolvers)
        {
            resolver.RequestReread();
        }

        SiloAddress[] running = [.. cluster.Silos.Select(static silo => silo.SiloAddress)];
        await EventuallyAsync(() => resolvers.All(resolver => running.All(silo => resolver.Current?.FindBySilo(silo) is not null)), "every silo's registry view to name every running silo");
    }

    private async Task<TestCluster> DeployAsync(short silos, bool fastMembership, CancellationToken cancellationToken)
    {
        _state.FastMembership = fastMembership;
        var builder = new TestClusterBuilder(silos);
        builder.Properties[TestKey] = _state.Id;
        if (_state.ServiceId is { } serviceId)
        {
            builder.Options.ServiceId = serviceId;
        }

        _ = builder.AddSiloBuilderConfigurator<NodeConfigurator>();
        _ = builder.AddClientBuilderConfigurator<ClientConfigurator>();
        _cluster = builder.Build();
        await _cluster.DeployAsync(cancellationToken).WaitAsync(Patience, cancellationToken);

        // Every silo's registry view names every running silo before a test acts.
        await EveryResolverListsEveryNodeAsync(_cluster);
        return _cluster;
    }

    /// <summary>
    /// One test's MetaStore and object store, recording their writers' contexts, the silos' roots, the faults a test
    /// injects, and its gates.
    /// </summary>
    private sealed class TestState
    {
        public const string ProbeKey = "clog-controller-probe";
        public const string HeldRead = "held read";

        private readonly ConcurrentQueue<(string Target, object? Probe, string? Activity)> _writes = new();
        private int _failNextMapReply;
        private int _handOffFaults;
        private int _failRegistryWrites;
        private int _loseRecordCreates;
        private IObjectStore? _azureObjects;
        private IObjectStore? _sharedObjects;
        private IArchiveLogStore? _azureLogs;
        private string? _writeRepliesAt;
        private long _writesBegun;
        private Tuple<string, byte[]>? _heldReply;
        private bool _holdEveryWriteReply;

        public string Id { get; } = Guid.NewGuid().ToString("N");

        public Guid Cluster { get; } = Guid.NewGuid();

        public InMemoryMetaStore MetaStore { get; } = new();

        public InMemoryObjectStore Objects { get; } = new();

        /// <summary>
        /// Gets or sets the cluster's store prefix, which its silos are configured with; the test's stores hold each object
        /// under its name without it, and refuse a name outside it. <see langword="null"/> derives the prefix from the
        /// cluster's <see cref="ServiceId"/>, over <see cref="Shared"/>'s stores, which hold every name whole.
        /// </summary>
        public string? Prefix { get; set; } = "t";

        /// <summary>Gets or sets the Orleans service ID of the test's cluster, or <see langword="null"/> for the test cluster's own.</summary>
        public string? ServiceId { get; set; }

        /// <summary>
        /// Gets or sets the bucket and container a cluster whose <see cref="Prefix"/> is derived shares with others: its LSM's
        /// object store and its archive-log container, used as they are.
        /// </summary>
        public (IObjectStore Objects, IArchiveLogContainer Container)? Shared { get; set; }

        /// <summary>Gets or sets the kind of archive store the silos run over: the LSM, over <see cref="Objects"/>, or archive logs.</summary>
        public ArchiveStoreKind ArchiveStore { get; set; } = ArchiveStoreKind.Lsm;

        /// <summary>
        /// Gets or sets the archive logs of a cluster on archive logs, in one container with <see cref="Objects"/>, which
        /// holds durable storage's objects under <c>durable-storage/</c>.
        /// </summary>
        public MemoryArchiveLogStore Logs { get; set; } = new();

        /// <summary>Gets the silos over another store than the cluster's, by name: each one's own object store and archive logs.</summary>
        public ConcurrentDictionary<string, (IObjectStore Objects, IArchiveLogStore Logs)> OtherStores { get; } = new(StringComparer.Ordinal);

        /// <summary>Gets the archive-log writes, appends and openers, each silo made.</summary>
        public ConcurrentDictionary<string, int> LogWrites { get; } = new(StringComparer.Ordinal);

        /// <summary>Gets the logs whose appends and openers the test's archive-log store refuses, as a damaged blob's would be.</summary>
        public ConcurrentDictionary<string, bool> RefusedLogs { get; } = new(StringComparer.Ordinal);

        /// <summary>
        /// Gets or sets the fake Azure Blob service whose one container holds the cluster's archive logs and durable storage's objects,
        /// in place of <see cref="Objects"/> and <see cref="Logs"/>, or <see langword="null"/>.
        /// </summary>
        public FakeAzureBlobService? Azure { get; set; }

        /// <summary>Gets or sets how many of the next creates of a store's record land and then lose their answer.</summary>
        public int LoseRecordCreates
        {
            get => Volatile.Read(ref _loseRecordCreates);
            set => Volatile.Write(ref _loseRecordCreates, value);
        }

        /// <summary>
        /// Gets the object store of the cluster's archive store: <see cref="Objects"/>, the fake Azure container's, or, for a
        /// cluster whose prefix derives from its <see cref="ServiceId"/>, <see cref="Shared"/>'s bucket under that prefix.
        /// </summary>
        public IObjectStore ClusterObjects => Azure is { } azure
            ? _azureObjects ??= new AzureBlobObjectStore(azure.Client())
            : Prefix is null && Shared is { } shared && ServiceId is { } serviceId
                ? _sharedObjects ??= new PrefixedObjectStore(shared.Objects, ArchiveStorePrefix.Of(serviceId).Value + "/")
                : Objects;

        /// <summary>Gets the archive logs of the cluster's archive store: <see cref="Logs"/>, or the fake Azure container's.</summary>
        public IArchiveLogStore ClusterLogs => Azure is { } azure
            ? _azureLogs ??= new AzureArchiveLogStore(azure.Client(), new JournaledStateManagerOptions().JournalFormatKey)
            : Logs;

        /// <summary>Returns the object store the silo named <paramref name="silo"/> runs over.</summary>
        public IObjectStore ObjectsOf(string silo) => OtherStores.TryGetValue(silo, out var other) ? other.Objects : ClusterObjects;

        /// <summary>Returns the archive logs the silo named <paramref name="silo"/> runs over.</summary>
        public IArchiveLogStore LogsOf(string silo) => OtherStores.TryGetValue(silo, out var other) ? other.Logs : ClusterLogs;

        /// <summary>Takes one pending lost answer of a record's create, if any.</summary>
        public bool TakeLostRecordCreate()
        {
            int pending;
            do
            {
                pending = Volatile.Read(ref _loseRecordCreates);
                if (pending == 0)
                {
                    return false;
                }
            }
            while (Interlocked.CompareExchange(ref _loseRecordCreates, pending - 1, pending) != pending);

            return true;
        }

        public string Roots { get; } = Path.Combine(Path.GetTempPath(), "clog-controller-tests", Guid.NewGuid().ToString("N"));

        public bool RecordContexts { get; set; }

        public bool FastMembership { get; set; }

        /// <summary>Gets or sets the response timeout of the test's client, or Orleans' default.</summary>
        public TimeSpan? ClientResponseTimeout { get; set; }

        public bool RefuseObjects { get; set; }

        public bool FailMapAfterRegistration { get; set; }

        /// <summary>Gets or sets a change to the test's control options.</summary>
        public Action<ControlOptions>? Control { get; set; }

        /// <summary>Gets or sets a change to the node options of the silo it names.</summary>
        public Action<string, DurableStorageOptions>? Node { get; set; }

        /// <summary>Gets or sets the Orleans Journaling provider name each silo registers Clog under, or the default name.</summary>
        public string? ProviderName { get; set; }

        /// <summary>Gets each resend the silos' journal routers logged, its silo and message.</summary>
        public ConcurrentQueue<(string Silo, string Message)> RouterResends { get; } = new();

        /// <summary>Gets or sets whether the next ledger map commit's reply fails to decode, once it applied: 1, then 0.</summary>
        public int FailNextMapReply
        {
            get => Volatile.Read(ref _failNextMapReply);
            set => Volatile.Write(ref _failNextMapReply, value);
        }

        /// <summary>Gets or sets the silo whose hand-off requests fail at once, before they are sent.</summary>
        public string? FaultHandOffsFrom { get; set; }

        /// <summary>Gets how many hand-off requests failed at once.</summary>
        public int HandOffFaults => Volatile.Read(ref _handOffFaults);

        /// <summary>Gets or sets the ledger whose replica tail reads, reads, and writes the gates hold.</summary>
        public LedgerId? HeldLedger { get; set; }

        /// <summary>Gets the drain commits each closed ledger's drains submitted to the controller grain.</summary>
        public ConcurrentDictionary<LedgerId, int> DrainCommits { get; } = new();

        /// <summary>Gets the ledger of each drain commit the controller grain answered <c>Applied</c>, in order.</summary>
        public ConcurrentQueue<LedgerId> AppliedDrains { get; } = new();

        /// <summary>Gets the system clock's timestamps of each manifest replace's start, once any hold released it, and end.</summary>
        public ConcurrentQueue<(long Started, long Ended)> ManifestReplaceTimes { get; } = new();

        /// <summary>Gets each applied ledger map write which closed or dropped a ledger, in order: the ledgers it closed and dropped.</summary>
        public ConcurrentQueue<(LedgerId[] Closed, LedgerId[] Dropped)> MapWrites { get; } = new();

        /// <summary>Gets the ledgers each applied manifest replace advanced: its recorded watermark or completeness changed.</summary>
        public ConcurrentQueue<LedgerId[]> ManifestAdvances { get; } = new();

        /// <summary>Gets or sets the ledgers whose first drain commit the controller grain's silo holds until each has arrived.</summary>
        public DrainBarrier? DrainCommitBarrier { get; set; }

        /// <summary>
        /// Gets each drop an applied ledger map write made against LG-I10, or before the archive held the ledger: before the
        /// stored manifest showed it complete (archive-lsm.md §2.6), or before its drain markers covered each of its slots
        /// below its <c>T</c> which holds rows (archive-logs.md §2.4).
        /// </summary>
        public ConcurrentQueue<string> DropViolations { get; } = new();

        /// <summary>
        /// Checks each ledger an applied map write from <paramref name="before"/> to <paramref name="after"/> dropped: no
        /// listed ledger with a lower ID overlaps it (LG-I10), and <paramref name="manifest"/>, the manifest the store held
        /// before the write, shows it complete. Completeness only grows (LSM-I3), so a drop resting on a manifest value
        /// which was read or applied passes; the rule that a value whose replace has an unknown outcome is no evidence is
        /// beyond this check, since a value which landed is complete too (archive-lsm.md §2.6). It records the write's
        /// closes and drops in order, and any failure of its own as a violation, so the product's write path never sees one.
        /// On archive logs a drop rests on its drain markers instead: <paramref name="chainBreaks"/>, read before the write
        /// from the value it replaces, names each dropped ledger whose markers did not cover its rows.
        /// </summary>
        public void CheckMapWrite(LedgerMapValue before, LedgerMapValue after, ManifestValue? manifest, IReadOnlyDictionary<LedgerId, string>? chainBreaks)
        {
            try
            {
                var closed = before.Ledgers.Where(ledger => ledger.IsOpen && after.Get(ledger.Id) is not { IsOpen: true }).Select(static ledger => ledger.Id).ToArray();
                var dropped = before.Ledgers.Where(ledger => after.Get(ledger.Id) is null).ToArray();
                if (closed.Length > 0 || dropped.Length > 0)
                {
                    MapWrites.Enqueue((closed, [.. dropped.Select(static ledger => ledger.Id)]));
                }

                foreach (var ledger in dropped)
                {
                    _ = _drops.GetOrAdd(ledger.Id, static _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
                }

                foreach (var ledger in dropped)
                {
                    foreach (var blocker in after.Ledgers.Where(listed => listed.Id.Value < ledger.Id.Value && listed.Range.Overlaps(ledger.Range)))
                    {
                        DropViolations.Enqueue(string.Create(CultureInfo.InvariantCulture, $"s{ledger.Id} dropped at epoch {after.Epoch} while s{blocker.Id}, which overlaps it, is listed (LG-I10)."));
                    }

                    if (ArchiveStore == ArchiveStoreKind.Lsm && manifest?.IsComplete(ledger.Id) != true)
                    {
                        DropViolations.Enqueue(string.Create(CultureInfo.InvariantCulture, $"s{ledger.Id} dropped at epoch {after.Epoch} while the stored manifest did not show it complete."));
                    }

                    // On archive logs a drop rests on its drain worker's markers, not on a manifest (archive-logs.md §2.4).
                    if (ArchiveStore != ArchiveStoreKind.Lsm && chainBreaks is not null && chainBreaks.TryGetValue(ledger.Id, out var chainBreak))
                    {
                        DropViolations.Enqueue(string.Create(CultureInfo.InvariantCulture, $"s{ledger.Id} dropped at epoch {after.Epoch} while {chainBreak}."));
                    }
                }
            }
            catch (Exception failure)
            {
                DropViolations.Enqueue($"The drop check failed: {failure}");
            }
        }

        /// <summary>
        /// Gets, on archive logs, the slots of each ledger whose replica writes carried a batch with rows, each a slot a drain
        /// must write before the ledger drops: a segment of markers alone gets no drain marker (archive-logs.md §2.3).
        /// </summary>
        public ConcurrentDictionary<LedgerId, ConcurrentDictionary<long, bool>> RowSlots { get; } = new();

        /// <summary>Records the slot of a replica write whose batch carries rows, on archive logs.</summary>
        public void RecordReplicaWrite(LedgerReplicaWriteRequest request)
        {
            if (ArchiveStore == ArchiveStoreKind.Lsm)
            {
                return;
            }

            using var batch = ArcBuffer.Wrap(request.Payload.ToArray());
            if (BatchView.Decode(batch, out var view) is null && view.Count > 0)
            {
                _ = RowSlots.GetOrAdd(new LedgerId(request.Ledger), static _ => new()).TryAdd(request.Slot, true);
            }
        }

        /// <summary>
        /// Returns, for each closed ledger <paramref name="written"/> drops from <paramref name="before"/>, why the drain
        /// markers the cluster's store holds do not cover it: a slot below its <c>T</c> whose batch carries rows lies in no
        /// marker's segment, from its start to the end its content names (archive-logs.md §2.3, §2.4, §7.5). Only a sweep of
        /// a dropped ledger deletes its markers (LOG-I6), so a value which still lists the ledger shows every marker its
        /// drains wrote. A dropped ledger which is open has no end its markers could reach.
        /// </summary>
        public async ValueTask<Dictionary<LedgerId, string>> DrainChainBreaksAsync(LedgerMapValue before, LedgerMapValue written, CancellationToken cancellationToken)
        {
            var breaks = new Dictionary<LedgerId, string>();
            foreach (var ledger in before.Ledgers.Where(ledger => written.Get(ledger.Id) is null))
            {
                if (await ChainBreakAsync(ledger, cancellationToken).ConfigureAwait(false) is { } chainBreak)
                {
                    breaks[ledger.Id] = chainBreak;
                }
            }

            return breaks;
        }

        /// <summary>
        /// Returns whether the cluster's store shows <paramref name="ledger"/> drained whole: on the LSM, the stored manifest
        /// shows it complete; on archive logs, it is closed and its drain markers cover every slot below its <c>T</c> whose
        /// batch carries rows (archive-logs.md §2.3).
        /// </summary>
        public async ValueTask<bool> DrainedAsync(LedgerMetadata ledger, CancellationToken cancellationToken) =>
            ArchiveStore == ArchiveStoreKind.Lsm
                ? await StoredManifestAsync().ConfigureAwait(false) is { } stored && stored.IsComplete(ledger.Id)
                : await ChainBreakAsync(ledger, cancellationToken).ConfigureAwait(false) is null;

        /// <summary>Returns why the drain markers do not cover the closed <paramref name="ledger"/>'s rows, or null when they do.</summary>
        private async ValueTask<string?> ChainBreakAsync(LedgerMetadata ledger, CancellationToken cancellationToken)
        {
            if (ledger.Status.Value is not LedgerStatus.Closed closed)
            {
                return "it was open, with no end its drain markers could reach";
            }

            var segments = new List<(Slot Start, Slot End)>();
            await foreach (var listed in ClusterObjects.ListAsync(ArchiveLogNames.DrainMarkersOf(ledger.Id), cancellationToken: cancellationToken).ConfigureAwait(false))
            {
                if (ArchiveLogNames.ParseDrainMarker(listed.Name.Value) is { } parsed && parsed.Ledger == ledger.Id
                    && await ClusterObjects.ReadVersionedAsync(listed.Name, cancellationToken).ConfigureAwait(false) is { } stored)
                {
                    using var content = stored.Content;
                    if (ArchiveLogNames.ParseDrainMarkerContent(content.ToArray()) is { } end)
                    {
                        segments.Add((parsed.Start, end));
                    }
                }
            }

            long[] rows = RowSlots.TryGetValue(ledger.Id, out var slots) ? [.. slots.Keys.Where(slot => slot < closed.T.Value).Order()] : [];
            return rows.FirstOrDefault(slot => !segments.Any(segment => segment.Start.Value <= slot && slot < segment.End.Value), -1) is var uncovered and >= 0
                ? string.Create(CultureInfo.InvariantCulture, $"its slot {uncovered}, below its T {closed.T}, holds rows in no drain marker's segment")
                : null;
        }

        /// <summary>Returns the manifest the store holds, read past any refusal the test injects, or null when it holds none.</summary>
        public async ValueTask<ManifestValue?> StoredManifestAsync()
        {
            if (await ClusterObjects.ReadVersionedAsync(ObjectName.Manifest).ConfigureAwait(false) is not { } stored)
            {
                return null;
            }

            using var content = stored.Content;
            return ManifestFormat.Decode(content.AsReadOnlySequence()).Value as ManifestValue
                ?? throw new InvalidDataException("The stored manifest does not decode.");
        }

        /// <summary>Gets each silo's node control runtime, captured as the silo starts.</summary>
        public ConcurrentDictionary<string, LocalNodeControl> Controls { get; } = new(StringComparer.Ordinal);

        /// <summary>Gets each silo's status, captured as the silo starts.</summary>
        public ConcurrentDictionary<string, DurableStorageStatus> Statuses { get; } = new(StringComparer.Ordinal);

        /// <summary>Gets each silo's node, captured as the silo starts.</summary>
        public ConcurrentDictionary<string, LocalNode> LocalNodes { get; } = new(StringComparer.Ordinal);

        /// <summary>Gets each silo's lifecycle and Ledgers runtime, captured as the silo starts, for the stop report.</summary>
        public ConcurrentDictionary<string, (ISiloLifecycle Lifecycle, NodeLedgers Ledgers)> Silos { get; } = new(StringComparer.Ordinal);

        /// <summary>Gets the silos' stop events and warnings, timed, in order, for the stop report.</summary>
        public ConcurrentQueue<string> StopEvents { get; } = new();

        /// <summary>Holds the manifest reads made under the <see cref="HeldRead"/> probe.</summary>
        public Gate ManifestReads { get; } = new();

        /// <summary>Holds the manifest's replaces.</summary>
        public Gate ManifestReplaces { get; } = new();

        /// <summary>Holds the object store's listings, which only the sweeper makes.</summary>
        public Gate Listings { get; } = new();

        /// <summary>Gets each drain commit and drop a silo sent the controller grain, with the ledger it names, in order.</summary>
        public ConcurrentQueue<(string Silo, LedgerId Ledger)> ArchiveSubmissions { get; } = new();

        /// <summary>Holds the archive-log store's appends and openers, which only a drain makes.</summary>
        public Gate LogAppends { get; } = new();

        /// <summary>Holds the replica tail reads of <see cref="HeldLedger"/>.</summary>
        public Gate TailReads { get; } = new();

        /// <summary>Holds the replica reads of <see cref="HeldLedger"/> at their callees.</summary>
        public Gate ReplicaReads { get; } = new();

        /// <summary>Holds the replica writes of <see cref="HeldLedger"/> at their callees.</summary>
        public Gate ReplicaWrites { get; } = new();

        /// <summary>Holds the replica writes of every ledger at their callers, before they leave the silo.</summary>
        public Gate ReplicaSends { get; } = new();

        /// <summary>Holds the seals of <see cref="HeldLedger"/> at their callers, before they leave the silo, each the target node's key.</summary>
        public Gate SealSends { get; } = new();

        /// <summary>
        /// Holds, at the silo <see cref="HoldWriteReplies"/> names, the reply of the first forwarded journal write its owner
        /// answers <c>Accepted</c>, and of every one once <see cref="HoldEveryWriteReply"/> is called.
        /// </summary>
        public Gate WriteReplies { get; } = new();

        /// <summary>
        /// Gets each forwarded journal write a silo whose replies <see cref="HoldWriteReplies"/> armed is running: its silo,
        /// journal, payload, and place in the order the silos began them (<see cref="WritesBegun"/>).
        /// </summary>
        public ConcurrentDictionary<object, (string Silo, string Key, byte[] Payload, long Sequence)> RunningWrites { get; } = new();

        /// <summary>Gets how many forwarded journal writes the silos have begun.</summary>
        public long WritesBegun => Volatile.Read(ref _writesBegun);

        /// <summary>Counts a forwarded journal write begun, and returns its place in the order.</summary>
        public long BeginWrite() => Interlocked.Increment(ref _writesBegun);

        /// <summary>Gets the journal and record of the first forwarded write whose reply <see cref="WriteReplies"/> held.</summary>
        public (string Key, byte[] Record)? HeldReply => Volatile.Read(ref _heldReply) is { } held ? (held.Item1, held.Item2) : null;

        /// <summary>Holds the reads of the archive store's record, <c>store</c>.</summary>
        public Gate RecordReads { get; } = new();

        /// <summary>Gets or sets the failure each read of the archive store's record throws once it passes <see cref="RecordReads"/>, or null.</summary>
        public Exception? RecordReadFault { get; set; }

        /// <summary>Holds the reads of the archive store's record which a silo makes once registered: its background check's.</summary>
        public Gate CheckRecordReads { get; } = new();

        /// <summary>Gets the failure each read a silo's background check makes of the store's record throws, by silo, once it passes <see cref="CheckRecordReads"/>.</summary>
        public ConcurrentDictionary<string, Exception> CheckRecordReadFaults { get; } = new(StringComparer.Ordinal);

        /// <summary>Gets each node's latest report the controller grain received.</summary>
        public ConcurrentDictionary<NodeId, NodeReport> LatestReports { get; } = new();

        /// <summary>Gets how many reports the controller grain has received from each node.</summary>
        public ConcurrentDictionary<NodeId, int> ReportCounts { get; } = new();

        /// <summary>Gets each silo's listings and deletions in the object store and the archive-log store, which only sweeps and drains make.</summary>
        public ConcurrentDictionary<string, int> StoreSweepCalls { get; } = new(StringComparer.Ordinal);

        public void CountSweepCall(string silo) => _ = StoreSweepCalls.AddOrUpdate(silo, 1, static (_, calls) => calls + 1);

        /// <summary>Gets or sets the activation of the controller grain which took the latest call; calls set it concurrently.</summary>
        public ActivationId? ControllerActivation
        {
            get
            {
                lock (_controllerActivationLock)
                {
                    return field;
                }
            }

            set
            {
                lock (_controllerActivationLock)
                {
                    field = value;
                }
            }
        }

        private readonly Lock _controllerActivationLock = new();

        /// <summary>Gets each silo's object creates and replaces.</summary>
        public ConcurrentDictionary<string, int> StoreWrites { get; } = new(StringComparer.Ordinal);

        public void CountStoreWrite(string silo) => _ = StoreWrites.AddOrUpdate(silo, 1, static (_, writes) => writes + 1);

        /// <summary>Gets the reads of the archive store's record each silo made.</summary>
        public ConcurrentDictionary<string, int> RecordReadCounts { get; } = new(StringComparer.Ordinal);

        /// <summary>Gets the reports each silo sent the controller grain, counted as they leave it.</summary>
        public ConcurrentDictionary<string, int> ReportsSent { get; } = new(StringComparer.Ordinal);

        /// <summary>Holds the reports the controller grain receives, as a call to an unreachable controller hangs.</summary>
        public Gate ReportCalls { get; } = new();

        /// <summary>Gets the hand-off requests the controller grain received for each node.</summary>
        public ConcurrentDictionary<NodeId, int> HandOffRequests { get; } = new();

        /// <summary>
        /// Gets the gate of the silos' registry views: it holds the node registry reads of a silo's
        /// <see cref="NodeRegistryResolver"/>, and no other reader's, such as the controller grain's committer.
        /// </summary>
        public Gate ResolverReads { get; } = new();

        /// <summary>Gets when each silo's stop reached each timed stage, by the system clock's timestamp.</summary>
        public ConcurrentDictionary<(string Silo, string Stage), long> StopStages { get; } = new();

        /// <summary>Gets each silo's Journals, captured as the silo starts.</summary>
        public ConcurrentDictionary<string, LocalJournals> Journals { get; } = new(StringComparer.Ordinal);

        /// <summary>Gets or sets a change to the silo builder of the silo it names.</summary>
        public Action<string, ISiloBuilder>? Silo { get; set; }

        /// <summary>Holds the stop of one silo below its Ledgers runtime's stage.</summary>
        public Gate StopGate { get; } = new();

        /// <summary>Holds the controller grain's ledger map commits, except the install which closes <see cref="HeldLedger"/>.</summary>
        public Gate Departures { get; } = new();

        /// <summary>Holds the writes of the node registry, which only the controller grain's committer makes.</summary>
        public Gate RegistryWrites { get; } = new();

        /// <summary>Holds, at the controller grain, the ledger map installs which close <see cref="HeldLedger"/>.</summary>
        public Gate Installs { get; } = new();

        /// <summary>Gets or sets the node whose registrations <see cref="Registrations"/> holds at the controller grain.</summary>
        public NodeId? HeldRegistrationOf { get; set; }

        /// <summary>Holds, at the controller grain, the registrations of <see cref="HeldRegistrationOf"/>.</summary>
        public Gate Registrations { get; } = new();

        /// <summary>
        /// Holds the reply of each append to a <see cref="IPlacedJournaledChunksGrain"/>, once the grain answered it, while the
        /// request still runs in its activation.
        /// </summary>
        public Gate AppendReplies { get; } = new();

        /// <summary>
        /// Holds the reply of each <see cref="IChunksGrain.SiloAsync"/> call to a <see cref="IPlacedJournaledChunksGrain"/>, once the
        /// grain answered it, while the request still runs in its activation, so the activation stays.
        /// </summary>
        public Gate SiloReplies { get; } = new();

        /// <summary>Holds, at its owner's silo, each forwarded journal write the hold accepts, before the owner takes it.</summary>
        public Gate ForwardedWrites { get; } = new();

        /// <summary>Holds the reads of the ledger map register a silo's MetaStore makes, as its view's refreshes do.</summary>
        public Gate MapReads { get; } = new();

        /// <summary>
        /// Gets or sets the two silos, with their nodes, between which every call to a node's grain is lost, a loss
        /// membership does not see: a forwarded journal write's reply, once its owner answered it, and every other call's
        /// request, before it leaves its silo.
        /// </summary>
        public (string Silo, NodeId Node, string Other, NodeId OtherNode)? Partition { get; set; }

        /// <summary>Gets the calls the partition lost before they left their silos.</summary>
        public ConcurrentQueue<string> LostRequests { get; } = new();

        /// <summary>
        /// Gets the forwarded journal writes whose replies the partition lost: each sending silo, journal, payload, and
        /// whether its owner accepted it.
        /// </summary>
        public ConcurrentQueue<(string Silo, string Key, byte[] Payload, bool Accepted)> LostReplies { get; } = new();

        /// <summary>
        /// Gets or sets the ledgers the map keeps once drained: each ledger map write which drops one fails unapplied, and the
        /// controller drops it again at a later pass. The write fails whole, with every change the committer folded into its
        /// batch, the drops of other ledgers which the same dropper pass submitted among them, so a test which waits for
        /// another ledger to drop meanwhile holds its ledger's drain instead (<see cref="HeldDrains"/>).
        /// </summary>
        public FrozenSet<LedgerId> KeptLedgers { get; set; } = [];

        /// <summary>
        /// Gets or sets the ledgers whose drains never complete: the controller grain refuses each drain commit of one, before
        /// its committer sees it, so the manifest never shows the ledger complete, no dropper submits its drop, and no other
        /// change shares a batch with it (#381). A node's next control pass drains it again once released.
        /// </summary>
        public FrozenSet<LedgerId> HeldDrains { get; set; } = [];

        /// <summary>
        /// Gets each ledger a ledger map write drops, from the value it replaces, in the order the writes were sent, whether
        /// or not they landed.
        /// </summary>
        public ConcurrentQueue<LedgerId> MapWriteDrops { get; } = new();

        /// <summary>Gets the bytes each silo, by name, adds to its LedgerStore's measured WAL, which puts it under disk pressure.</summary>
        public ConcurrentDictionary<string, long> ExtraWalBytes { get; } = new();

        /// <summary>Gets the silos whose disk measurements fail, as an unreadable volume's would.</summary>
        public ConcurrentDictionary<string, bool> FailedDiskMeasurements { get; } = new(StringComparer.Ordinal);

        /// <summary>Gets the disk pressure and throttle events each silo logged (events 6204, 6205, 6237, 6238), in order.</summary>
        public ConcurrentQueue<(string Silo, int EventId, object? Ledger)> PressureEvents { get; } = new();

        /// <summary>Records a stop event or a warning of <paramref name="silo"/>, timed.</summary>
        public void RecordStop(string silo, string what)
        {
            var line = string.Create(CultureInfo.InvariantCulture, $"{TimeProvider.System.GetUtcNow():HH:mm:ss.fff} {silo}: {what}");
            List<TaskCompletionSource>? seen = null;
            lock (_eventWaiters)
            {
                StopEvents.Enqueue(line);
                foreach (var waiter in _eventWaiters.Where(waiter => waiter.Match(line)).ToList())
                {
                    if (--waiter.Remaining == 0)
                    {
                        _ = _eventWaiters.Remove(waiter);
                        (seen ??= []).Add(waiter.Seen);
                    }
                }
            }

            foreach (var waiter in seen ?? [])
            {
                _ = waiter.TrySetResult();
            }
        }

        /// <summary>
        /// Returns a task which completes once <paramref name="count"/> of the recorded stop events and warnings match
        /// <paramref name="match"/>, those recorded already among them: a wait on the events themselves, which no clock
        /// bounds.
        /// </summary>
        public Task EventsAsync(Func<string, bool> match, int count = 1)
        {
            lock (_eventWaiters)
            {
                var remaining = count - StopEvents.Count(match);
                if (remaining <= 0)
                {
                    return Task.CompletedTask;
                }

                var waiter = new EventWaiter(match, remaining);
                _eventWaiters.Add(waiter);
                return waiter.Seen.Task;
            }
        }

        /// <summary>Returns a task which completes once an applied ledger map write drops <paramref name="ledger"/>.</summary>
        public Task DroppedAsync(LedgerId ledger) => _drops.GetOrAdd(ledger, static _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;

        private readonly List<EventWaiter> _eventWaiters = [];

        private readonly ConcurrentDictionary<LedgerId, TaskCompletionSource> _drops = new();

        /// <summary>A wait for <see cref="Remaining"/> more events which match.</summary>
        private sealed class EventWaiter(Func<string, bool> match, int remaining)
        {
            public Func<string, bool> Match { get; } = match;

            public int Remaining { get; set; } = remaining;

            public TaskCompletionSource Seen { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        /// <summary>
        /// Writes, for a stop which outlasted <paramref name="patience"/>, each silo's lifecycle stages, its Ledgers runtime's
        /// stop step (orleans.md §5.2), and the timed stop events and warnings, to the results directory; returns its path.
        /// </summary>
        public string WriteStopReport(string stopping, TimeSpan patience)
        {
            var report = new StringBuilder();
            _ = report.AppendLine(CultureInfo.InvariantCulture, $"Silo {stopping}'s stop outlasted {patience}.");
            foreach (var (silo, (lifecycle, _)) in Silos.OrderBy(static entry => entry.Key, StringComparer.Ordinal))
            {
                _ = report.AppendLine(CultureInfo.InvariantCulture, $"{silo}: highest stage started {lifecycle.HighestCompletedStage}, lowest stage stopped {lifecycle.LowestStoppedStage}; Ledgers runtime {LedgersStep(lifecycle, Controls[silo])}.");
            }

            _ = report.AppendLine("Stop events and warnings, in order:");
            foreach (var line in StopEvents)
            {
                _ = report.AppendLine(line);
            }

            var path = TestDiagnostics.PathFor(TestContext.Current.Test?.TestDisplayName ?? nameof(OrleansControllerTests), ".stop.log");
            File.WriteAllText(path, report.ToString());
            return path;
        }

        /// <summary>Returns the step of its stop (orleans.md §5.2) the Ledgers runtime of a silo is in, from its state.</summary>
        private static string LedgersStep(ISiloLifecycle lifecycle, LocalNodeControl control) =>
            lifecycle.LowestStoppedStage <= NodeLedgers.Stage ? "stopped" : control.State switch
            {
                LocalNodeControl.ControlState.Starting => "starting",
                LocalNodeControl.ControlState.Serving => "serving, its stop not begun",
                LocalNodeControl.ControlState.HandingOff => "in steps 1 to 3, handing off or waiting for its admitted work",
                LocalNodeControl.ControlState.Stopped => "in step 4 or 5, waiting for its cancelled work or disposing its runtime, or started none",
                _ => throw new UnreachableException(),
            };

        /// <summary>Gets the installs the controller grain applied, in order.</summary>
        public ConcurrentQueue<LedgerMapChange.Install> AppliedInstalls { get; } = new();

        /// <summary>Gets or sets how many of the next node registry writes fail unapplied, after any hold.</summary>
        public int FailRegistryWrites
        {
            get => Volatile.Read(ref _failRegistryWrites);
            set => Volatile.Write(ref _failRegistryWrites, value);
        }

        /// <summary>Gets or sets whether every ledger map write which raises the retirement floor fails unapplied.</summary>
        public bool FailFloorRaises { get; set; }

        /// <summary>Takes one pending registry write failure, if any.</summary>
        public bool TakeRegistryWriteFailure()
        {
            int pending;
            do
            {
                pending = Volatile.Read(ref _failRegistryWrites);
                if (pending == 0)
                {
                    return false;
                }
            }
            while (Interlocked.CompareExchange(ref _failRegistryWrites, pending - 1, pending) != pending);

            return true;
        }

        /// <summary>Gets or sets whether each silo runs at an ordinal, its index in the test cluster: 0 for the primary silo.</summary>
        public bool UseOrdinals { get; set; }

        public IReadOnlyList<(string Target, object? Probe, string? Activity)> Writes => [.. _writes];

        public void Record(string target)
        {
            if (RecordContexts)
            {
                _writes.Enqueue((target, RequestContext.Get(ProbeKey), Activity.Current?.OperationName));
            }
        }

        /// <summary>
        /// Arms <see cref="WriteReplies"/> to hold, at <paramref name="silo"/>, the reply of the next forwarded journal write
        /// its owner answers <c>Accepted</c>; the task completes once it is held.
        /// </summary>
        public Task HoldWriteReplies(string silo)
        {
            var held = WriteReplies.Hold(silo);
            Volatile.Write(ref _writeRepliesAt, silo);
            return held;
        }

        /// <summary>Returns whether a forwarded write's reply at <paramref name="silo"/> may be held.</summary>
        public bool HoldsWriteRepliesAt(string silo) => Volatile.Read(ref _writeRepliesAt) == silo;

        /// <summary>Makes <see cref="WriteReplies"/> hold the reply of every forwarded journal write at its silo from now on.</summary>
        public void HoldEveryWriteReply() => Volatile.Write(ref _holdEveryWriteReply, true);

        /// <summary>
        /// Returns whether the reply of a forwarded write of <paramref name="key"/>, whose record is <paramref name="record"/>
        /// and whose owner answered it <paramref name="accepted"/> or not, is held: the first accepted one, and every one
        /// once <see cref="HoldEveryWriteReply"/> was called.
        /// </summary>
        public bool HoldsWriteReply(string key, byte[] record, bool accepted) =>
            accepted && Interlocked.CompareExchange(ref _heldReply, Tuple.Create(key, record), null) is null || Volatile.Read(ref _holdEveryWriteReply);

        /// <summary>Counts a hand-off request failed at once.</summary>
        public void CountHandOffFault() => _ = Interlocked.Increment(ref _handOffFaults);

        /// <summary>Takes the one injected decode failure, if it is still pending.</summary>
        public bool TakeMapReplyFailure() => Interlocked.Exchange(ref _failNextMapReply, 0) == 1;

        public void ReleaseAll()
        {
            ManifestReads.Release();
            ManifestReplaces.Release();
            Listings.Release();
            TailReads.Release();
            ReplicaReads.Release();
            ReplicaWrites.Release();
            ReplicaSends.Release();
            SealSends.Release();
            WriteReplies.Release();
            RecordReads.Release();
            StopGate.Release();
            Departures.Release();
            RegistryWrites.Release();
            ResolverReads.Release();
            ReportCalls.Release();
            Installs.Release();
            Registrations.Release();
            AppendReplies.Release();
            SiloReplies.Release();
            ForwardedWrites.Release();
            MapReads.Release();
            Partition = null;
            KeptLedgers = [];
            HeldDrains = [];
        }
    }

    /// <summary>
    /// Holds the first drain commit of each of its ledgers until the first of every one has arrived, then releases them
    /// together, so the manifest committer sees them at once; it holds no call past the last arrival.
    /// </summary>
    private sealed class DrainBarrier(IReadOnlyCollection<LedgerId> ledgers)
    {
        private readonly ConcurrentDictionary<LedgerId, bool> _arrived = new();
        private readonly TaskCompletionSource _all = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Gets a task which completes once a commit of every ledger arrived.</summary>
        public Task All => _all.Task;

        public async ValueTask ArriveAsync(LedgerId ledger)
        {
            if (!ledgers.Contains(ledger) || !_arrived.TryAdd(ledger, true))
            {
                return;
            }

            if (_arrived.Count == ledgers.Count)
            {
                _all.TrySetResult();
            }

            await _all.Task.WaitAsync(Patience).ConfigureAwait(false);
        }
    }

    /// <summary>Holds the calls which reach it from <see cref="Hold"/> until <see cref="Release()"/>.</summary>
    private sealed class Gate
    {
        private Holding? _holding;
        private string? _heldAt;

        /// <summary>Gets the silo whose call reached the gate first while it held.</summary>
        public string? HeldAt => Volatile.Read(ref _heldAt);

        /// <summary>Gets whether the gate holds calls.</summary>
        public bool IsHolding => Volatile.Read(ref _holding) is { Released.Task.IsCompleted: false };

        /// <summary>Gets a task which completes once a call the gate holds past its cancellation saw its caller cancel it.</summary>
        public Task Cancelled => Volatile.Read(ref _holding)?.Cancelled.Task ?? Task.CompletedTask;

        /// <summary>
        /// Holds every call, or only <paramref name="silo"/>'s, from now until released, or, given <paramref name="holds"/>,
        /// only the calls it accepts; the task completes once a held call arrives, or, given <paramref name="arrives"/>, once
        /// a held call it accepts arrives. Given <paramref name="outlivesCancellation"/>, a held call stays held when its
        /// caller cancels it, as a request no cancellation reaches does, until released (<see cref="Cancelled"/>).
        /// </summary>
        public Task Hold(string? silo = null, Func<object?, bool>? arrives = null, Func<object?, bool>? holds = null, bool outlivesCancellation = false)
        {
            var holding = new Holding(silo, arrives, holds, outlivesCancellation);
            Volatile.Write(ref _holding, holding);
            return holding.Arrived.Task;
        }

        public void Release() => Volatile.Read(ref _holding)?.Released.TrySetResult();

        /// <summary>Releases <paramref name="silo"/>'s calls alone, held and later, while the gate holds the other silos'.</summary>
        public void Release(string silo) => Volatile.Read(ref _holding)?.ReleasedFor(silo).TrySetResult();

        /// <summary>Holds <paramref name="call"/>, from <paramref name="silo"/>, while the gate holds its silo's calls of its kind.</summary>
        public async ValueTask WaitAsync(string silo, CancellationToken cancellationToken, object? call = null)
        {
            if (Volatile.Read(ref _holding) is { Released.Task.IsCompleted: false } holding && (holding.Silo is null || holding.Silo == silo)
                && !holding.ReleasedFor(silo).Task.IsCompleted && (holding.Holds?.Invoke(call) ?? true))
            {
                if (holding.Arrives?.Invoke(call) ?? true)
                {
                    _ = Interlocked.CompareExchange(ref _heldAt, silo, null);
                    _ = holding.Arrived.TrySetResult();
                }

                if (holding.OutlivesCancellation)
                {
                    using var cancelled = cancellationToken.Register(static state => ((TaskCompletionSource)state!).TrySetResult(), holding.Cancelled);
                    _ = await Task.WhenAny(holding.Released.Task, holding.ReleasedFor(silo).Task).WaitAsync(Patience, CancellationToken.None).ConfigureAwait(false);
                    return;
                }

                _ = await Task.WhenAny(holding.Released.Task, holding.ReleasedFor(silo).Task).WaitAsync(Patience, cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>One hold: published whole, so a waiter never sees half of it.</summary>
        private sealed class Holding(string? silo, Func<object?, bool>? arrives, Func<object?, bool>? holds, bool outlivesCancellation)
        {
            public string? Silo { get; } = silo;

            public bool OutlivesCancellation { get; } = outlivesCancellation;

            public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public Func<object?, bool>? Arrives { get; } = arrives;

            public Func<object?, bool>? Holds { get; } = holds;

            public TaskCompletionSource Arrived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public TaskCompletionSource Released { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            private ConcurrentDictionary<string, TaskCompletionSource> Silos { get; } = new(StringComparer.Ordinal);

            /// <summary>Returns the release of <paramref name="silo"/>'s calls alone.</summary>
            public TaskCompletionSource ReleasedFor(string silo) => Silos.GetOrAdd(silo, static _ => new(TaskCreationOptions.RunContinuationsAsynchronously));
        }
    }

    private sealed class RecordingMetaStore(TestState state, string silo) : IMetaStore
    {
        public async ValueTask<IConditionalRegister> GetRegisterAsync(Guid cluster, string name, CancellationToken cancellationToken = default)
        {
            if (state.FailMapAfterRegistration && name == LedgerMapRegister.Name && state.LocalNodes.TryGetValue(silo, out var node) && node.Registered.IsCompletedSuccessfully)
            {
                throw new IOException("The test's MetaStore refuses the ledger map to the silo's runtime.");
            }

            return new RecordingRegister(state, silo, name, await state.MetaStore.GetRegisterAsync(cluster, name, cancellationToken).ConfigureAwait(false));
        }

        private sealed class RecordingRegister(TestState state, string silo, string name, IConditionalRegister register) : IConditionalRegister
        {
            public async ValueTask<MetaStoreEntry?> ReadAsync(CancellationToken cancellationToken = default)
            {
                if (name == LedgerMapRegister.Name)
                {
                    await state.MapReads.WaitAsync(silo, cancellationToken).ConfigureAwait(false);
                }

                if (name == NodeRegistryValue.RegisterName && state.ResolverReads.IsHolding && ReadsForTheResolver())
                {
                    await state.ResolverReads.WaitAsync(silo, cancellationToken).ConfigureAwait(false);
                }

                return await register.ReadAsync(cancellationToken).ConfigureAwait(false);
            }

            /// <summary>
            /// Returns whether the silo's <see cref="NodeRegistryResolver"/> makes the read. The resolver reads in no caller's
            /// execution context (conventions.md §12), so no <c>RequestContext</c> marks its reads; its own frames on the
            /// stack do, since the read starts synchronously from its re-read.
            /// </summary>
            private static bool ReadsForTheResolver() =>
                new StackTrace().GetFrames().Any(static frame => frame.GetMethod()?.DeclaringType is { } type
                    && (type == typeof(NodeRegistryResolver) || type.DeclaringType == typeof(NodeRegistryResolver)));

            public async ValueTask<RegisterWriteOutcome> WriteAsync(MetaStoreVersion? expectedVersion, System.Buffers.ReadOnlySequence<byte> value, CancellationToken cancellationToken = default)
            {
                state.Record(name);
                if (name == NodeRegistryValue.RegisterName)
                {
                    await state.RegistryWrites.WaitAsync(silo, cancellationToken).ConfigureAwait(false);
                    if (state.TakeRegistryWriteFailure())
                    {
                        throw new IOException("The test's MetaStore failed the node registry write, unapplied.");
                    }
                }
                LedgerMapValue? before = null;
                ManifestValue? manifest = null;
                Dictionary<LedgerId, string>? chainBreaks = null;
                if (name == LedgerMapRegister.Name)
                {
                    try
                    {
                        using var current = await register.ReadAsync(cancellationToken).ConfigureAwait(false);
                        before = current is null ? LedgerMapValue.Empty : LedgerMapValue.Decode(current.Value.ToArray()).Value as LedgerMapValue;
                        manifest = await state.StoredManifestAsync().ConfigureAwait(false);

                        // Only the value this write replaces shows its drops' markers: one an intervening write dropped may be swept.
                        if (state.ArchiveStore != ArchiveStoreKind.Lsm && before is not null && current?.Version == expectedVersion
                            && LedgerMapValue.Decode(System.Buffers.BuffersExtensions.ToArray(value)).Value is LedgerMapValue replacing)
                        {
                            chainBreaks = await state.DrainChainBreaksAsync(before, replacing, cancellationToken).ConfigureAwait(false);
                        }
                    }
                    catch (Exception failure) when (failure is not OperationCanceledException)
                    {
                        state.DropViolations.Enqueue($"The drop check's reads failed: {failure}");
                    }
                }

                if (name == LedgerMapRegister.Name && before is not null
                    && LedgerMapValue.Decode(System.Buffers.BuffersExtensions.ToArray(value)).Value is LedgerMapValue dropping)
                {
                    foreach (var listed in before.Ledgers.Where(listed => listed.Id.Value < dropping.NextLedgerId.Value && dropping.Get(listed.Id) is null))
                    {
                        state.MapWriteDrops.Enqueue(listed.Id);
                    }
                }

                if (name == LedgerMapRegister.Name && (state.KeptLedgers.Count > 0 || state.FailFloorRaises)
                    && LedgerMapValue.Decode(System.Buffers.BuffersExtensions.ToArray(value)).Value is LedgerMapValue written)
                {
                    if (state.KeptLedgers.Any(kept => kept.Value < written.NextLedgerId.Value && written.Get(kept) is null))
                    {
                        throw new IOException("The test's MetaStore failed the ledger map write which drops a kept ledger, unapplied.");
                    }

                    if (state.FailFloorRaises && written.RetirementFloor > await FloorAsync(cancellationToken).ConfigureAwait(false))
                    {
                        throw new IOException("The test's MetaStore failed the ledger map write which raises the floor, unapplied.");
                    }
                }

                var outcome = await register.WriteAsync(expectedVersion, value, cancellationToken).ConfigureAwait(false);
                if (before is not null && outcome.Value is RegisterWriteOutcome.Applied
                    && LedgerMapValue.Decode(System.Buffers.BuffersExtensions.ToArray(value)).Value is LedgerMapValue after)
                {
                    state.CheckMapWrite(before, after, manifest, chainBreaks);
                }

                return outcome;
            }

            private async ValueTask<ulong> FloorAsync(CancellationToken cancellationToken)
            {
                using var current = await register.ReadAsync(cancellationToken).ConfigureAwait(false);
                return current is null ? 0 : Assert.IsType<LedgerMapValue>(LedgerMapValue.Decode(current.Value.ToArray()).Value).RetirementFloor;
            }
        }
    }

    /// <summary>
    /// The test's object store, as one silo sees it: it records its replaces' contexts, holds manifest reads, manifest
    /// replaces, and listings while a test asks, and refuses every call while a test asks.
    /// </summary>
    private sealed class RecordingObjectStore(TestState state, string silo) : IObjectStore
    {
        private IObjectStore Inner => state.RefuseObjects ? throw new IOException("The test's object store refuses every call.") : state.ObjectsOf(silo);

        public async ValueTask<ObjectCreateOutcome> CreateAsync(ObjectName name, ArcBuffer content, uint? crc32c, CancellationToken cancellationToken = default)
        {
            state.CountStoreWrite(silo);
            var outcome = await Inner.CreateAsync(name, content, crc32c, cancellationToken).ConfigureAwait(false);
            if ((name == ObjectName.Store || name.Value == ArchiveLogNames.StoreRecord) && state.TakeLostRecordCreate())
            {
                throw new IOException("The test's object store lost the answer of the record's create, which landed.");
            }

            return outcome;
        }

        public ValueTask<ArcBuffer?> ReadAsync(ObjectName name, long offset, int length, CancellationToken cancellationToken = default) =>
            Inner.ReadAsync(name, offset, length, cancellationToken);

        public ValueTask<ObjectProperties?> GetPropertiesAsync(ObjectName name, CancellationToken cancellationToken = default) =>
            Inner.GetPropertiesAsync(name, cancellationToken);

        public async IAsyncEnumerable<ListedObject> ListAsync(string prefix, string? startAfter = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            state.CountSweepCall(silo);
            await state.Listings.WaitAsync(silo, cancellationToken).ConfigureAwait(false);
            await foreach (var listed in Inner.ListAsync(prefix, startAfter, cancellationToken).ConfigureAwait(false))
            {
                yield return listed;
            }
        }

        public ValueTask DeleteAsync(ObjectName name, CancellationToken cancellationToken = default)
        {
            state.CountSweepCall(silo);
            return Inner.DeleteAsync(name, cancellationToken);
        }

        public async ValueTask<VersionedObject?> ReadVersionedAsync(ObjectName name, CancellationToken cancellationToken = default)
        {
            if (name == ObjectName.Store || name.Value == ArchiveLogNames.StoreRecord)
            {
                _ = state.RecordReadCounts.AddOrUpdate(silo, 1, static (_, reads) => reads + 1);
                await state.RecordReads.WaitAsync(silo, cancellationToken).ConfigureAwait(false);
                if (state.RecordReadFault is { } fault)
                {
                    throw fault;
                }

                if (state.LocalNodes.TryGetValue(silo, out var local) && local.Registered.IsCompletedSuccessfully)
                {
                    await state.CheckRecordReads.WaitAsync(silo, cancellationToken).ConfigureAwait(false);
                    if (state.CheckRecordReadFaults.TryGetValue(silo, out var checkFault))
                    {
                        throw checkFault;
                    }
                }
            }

            if (name == ObjectName.Manifest && RequestContext.Get(TestState.ProbeKey) is TestState.HeldRead)
            {
                await state.ManifestReads.WaitAsync(silo, cancellationToken).ConfigureAwait(false);
            }

            return await Inner.ReadVersionedAsync(name, cancellationToken).ConfigureAwait(false);
        }

        public async ValueTask<ObjectReplaceOutcome> ReplaceAsync(ObjectName name, ObjectVersion? expectedVersion, ArcBuffer content, uint? crc32c, CancellationToken cancellationToken = default)
        {
            state.CountStoreWrite(silo);
            state.Record(name.Value);
            if (name != ObjectName.Manifest)
            {
                return await Inner.ReplaceAsync(name, expectedVersion, content, crc32c, cancellationToken).ConfigureAwait(false);
            }

            await state.ManifestReplaces.WaitAsync(silo, cancellationToken).ConfigureAwait(false);
            var replaced = state.RefuseObjects ? null : await state.StoredManifestAsync().ConfigureAwait(false);
            var written = ManifestFormat.Decode(content.AsReadOnlySequence()).Value as ManifestValue;
            var started = TimeProvider.System.GetTimestamp();
            try
            {
                var outcome = await Inner.ReplaceAsync(name, expectedVersion, content, crc32c, cancellationToken).ConfigureAwait(false);
                if (outcome.Value is ObjectReplaceOutcome.Applied && replaced is not null && written is not null)
                {
                    // The replace is conditional on the version read, so the value it replaced is the one read before it.
                    // A ledger the replace advanced was incomplete before it, and it recorded a new watermark or completed it;
                    // a complete ledger's entry leaves the manifest once DrainedBelow passes it, which advances nothing.
                    state.ManifestAdvances.Enqueue([.. written.Watermarks.Keys.Concat(replaced.Watermarks.Keys)
                        .Concat(Enumerable.Range((int)replaced.DrainedBelow.Value, (int)(written.DrainedBelow.Value - replaced.DrainedBelow.Value)).Select(static id => new LedgerId((ulong)id)))
                        .Distinct()
                        .Where(ledger => !replaced.IsComplete(ledger) && (written.IsComplete(ledger) || written.RecordedWatermark(ledger) != replaced.RecordedWatermark(ledger)))]);
                }

                return outcome;
            }
            finally
            {
                state.ManifestReplaceTimes.Enqueue((started, TimeProvider.System.GetTimestamp()));
            }
        }
    }

    /// <summary>
    /// The test's archive-log store, as one silo sees it: it counts the silo's appends and openers, refuses every call while
    /// the test refuses the object store, as one container's outage would, and refuses the writes of the logs a test names.
    /// </summary>
    private sealed class RecordingArchiveLogStore(TestState state, string silo) : IArchiveLogStore
    {
        private readonly IArchiveLogStore _layoutOf = state.LogsOf(silo);

        public ArchiveLogLayout Layout => _layoutOf.Layout;

        private IArchiveLogStore Inner => state.RefuseObjects ? throw new IOException("The test's archive-log store refuses every call.") : state.LogsOf(silo);

        public bool AdmitsName(string name) => _layoutOf.AdmitsName(name);

        public ValueTask<ArchiveLogRead> ReadAsync(ArchiveLogName name, CancellationToken cancellationToken) => Inner.ReadAsync(name, cancellationToken);

        public ValueTask<ArchiveLogProperties?> GetPropertiesAsync(ArchiveLogName name, CancellationToken cancellationToken) => Inner.GetPropertiesAsync(name, cancellationToken);

        public async ValueTask<ArchiveLogWriteOutcome> AppendAsync(ArchiveLogName name, ArchiveLogPosition from, IReadOnlyList<System.Buffers.ReadOnlySequence<byte>> run, CancellationToken cancellationToken)
        {
            await state.LogAppends.WaitAsync(silo, cancellationToken, name.Value).ConfigureAwait(false);
            return await Write(name).AppendAsync(name, from, run, cancellationToken).ConfigureAwait(false);
        }

        public async ValueTask<ArchiveLogWriteOutcome> OpenAsync(ArchiveLogName name, ArchiveLogPosition from, ArchiveLogOpener opener, System.Buffers.ReadOnlySequence<byte> content, CancellationToken cancellationToken)
        {
            await state.LogAppends.WaitAsync(silo, cancellationToken, name.Value).ConfigureAwait(false);
            return await Write(name).OpenAsync(name, from, opener, content, cancellationToken).ConfigureAwait(false);
        }

        public IAsyncEnumerable<ArchiveLogGenerationPage> ListGenerationsAsync(string? continuation, CancellationToken cancellationToken)
        {
            state.CountSweepCall(silo);
            return Inner.ListGenerationsAsync(continuation, cancellationToken);
        }

        public ValueTask DeleteGenerationAsync(string log, ulong generation, CancellationToken cancellationToken)
        {
            state.CountSweepCall(silo);
            return Inner.DeleteGenerationAsync(log, generation, cancellationToken);
        }

        private IArchiveLogStore Write(ArchiveLogName name)
        {
            // Counted before the outage refuses it: a write a silo attempts is one it made.
            _ = state.LogWrites.AddOrUpdate(silo, 1, static (_, writes) => writes + 1);
            var inner = Inner;
            return state.RefusedLogs.ContainsKey(name.Value)
                ? throw new ArchiveLogRefusedException($"The test's archive-log store refuses the writes of '{name}'.")
                : inner;
        }
    }

    /// <summary>
    /// Counts the drains submitted to the controller grain, holds the replica tail reads, reads, and writes of the held
    /// ledger and, while a test
    /// asks, the ledger map commits other than the held ledger's install, and fails the
    /// reply of one applied ledger map commit as a reply which cannot be decoded.
    /// </summary>
    private sealed class ControlFilter(IConfiguration configuration, ILocalSiloDetails silo) : IIncomingGrainCallFilter
    {
        public async Task Invoke(IIncomingGrainCallContext context)
        {
            var state = States[configuration[TestKey]!];
            var method = context.InterfaceMethod.Name;
            if (context.Grain is ILedgerStoreGrain && method == nameof(ILedgerStoreGrain.ReadTailAsync)
                && state.HeldLedger is { } held && context.Request.GetArgument(0) is LedgerReplicaTailRequest request && request.Ledger == held.Value)
            {
                await state.TailReads.WaitAsync(silo.Name, CancellationToken.None);
            }

            if (context.Grain is ILedgerStoreGrain && method == nameof(ILedgerStoreGrain.ReadAsync)
                && state.HeldLedger is { } read && context.Request.GetArgument(0) is LedgerReplicaReadRequest readRequest && readRequest.Ledger == read.Value)
            {
                await state.ReplicaReads.WaitAsync(silo.Name, CancellationToken.None);
            }

            if (context.Grain is ILedgerStoreGrain && method == nameof(ILedgerStoreGrain.WriteAsync)
                && state.HeldLedger is { } written && context.Request.GetArgument(0) is LedgerReplicaWriteRequest write && write.Ledger == written.Value)
            {
                await state.ReplicaWrites.WaitAsync(silo.Name, CancellationToken.None, write);
            }

            if (context.Grain is IClusterControllerGrain && method == nameof(IClusterControllerGrain.CommitLedgerMapAsync)
                && context.Request.GetArgument(0) is LedgerMapChange { Value: LedgerMapChange.Install install })
            {
                if (install.Closes.Any(closing => closing.Id == state.HeldLedger))
                {
                    await state.Installs.WaitAsync(silo.Name, CancellationToken.None, install);
                }
                else
                {
                    await state.Departures.WaitAsync(silo.Name, CancellationToken.None);
                }
            }

            if (context.Grain is IClusterControllerGrain && method == nameof(IClusterControllerGrain.RegisterNodeAsync)
                && context.Request.GetArgument(0) is NodeRegistryEntry { Node: var registering } && registering == state.HeldRegistrationOf)
            {
                await state.Registrations.WaitAsync(silo.Name, CancellationToken.None);
            }

            if (context.Grain is IClusterControllerGrain && method == nameof(IClusterControllerGrain.ReportAsync) && context.Request.GetArgument(0) is NodeReport report)
            {
                state.LatestReports[report.Node] = report;
                _ = state.ReportCounts.AddOrUpdate(report.Node, 1, static (_, count) => count + 1);
                await state.ReportCalls.WaitAsync(silo.Name, CancellationToken.None);
            }

            if (context.Grain is ILedgerStoreGrain && method == nameof(ILedgerStoreGrain.WriteAsync) && context.Request.GetArgument(0) is LedgerReplicaWriteRequest replicaWrite)
            {
                state.RecordReplicaWrite(replicaWrite);
            }

            if (context.Grain is IClusterControllerGrain)
            {
                state.ControllerActivation = context.TargetContext.ActivationId;
            }

            if (context.Grain is IClusterControllerGrain && method == nameof(IClusterControllerGrain.HandOffAsync) && context.Request.GetArgument(0) is NodeId handingOff)
            {
                _ = state.HandOffRequests.AddOrUpdate(handingOff, 1, static (_, requests) => requests + 1);
            }

            if (context.Grain is IClusterControllerGrain && method == nameof(IClusterControllerGrain.CommitManifestAsync) && context.Request.GetArgument(0) is ManifestChange { Value: ManifestChange.Drain drain })
            {
                if (state.HeldDrains.Contains(drain.Ledger))
                {
                    throw new IOException("The test's controller grain refused the drain commit of a held ledger, unapplied.");
                }

                _ = state.DrainCommits.AddOrUpdate(drain.Ledger, 1, static (_, commits) => commits + 1);
                if (state.DrainCommitBarrier is { } barrier)
                {
                    await barrier.ArriveAsync(drain.Ledger);
                }
            }

            // The callee releases the forwarded record once it is invoked, so a write's record is copied first.
            (string Key, byte[] Record)? forwarded = null;
            var running = new object();
            if (context.Grain is IJournalStoreGrain && method == nameof(IJournalStoreGrain.WriteAsync) && state.HoldsWriteRepliesAt(silo.Name)
                && context.Request.GetArgument(0) is JournalForwardedEntry entry)
            {
                var copy = entry.Record.ToArray();
                forwarded = (entry.Key, copy);
                if (Orleans.Durable.Storage.Engine.Journals.JournalEntry.Decode(JournalRowKey.Encode(entry.Key).Span, new System.Buffers.ReadOnlySequence<byte>(copy), out var decoded) is null)
                {
                    state.RunningWrites[running] = (silo.Name, entry.Key, copy[^(int)decoded.PayloadLength..], state.BeginWrite());
                }
            }

            if (context.Grain is IJournalStoreGrain && method == nameof(IJournalStoreGrain.WriteAsync) && context.Request.GetArgument(0) is JournalForwardedEntry arriving)
            {
                await state.ForwardedWrites.WaitAsync(silo.Name, CancellationToken.None, arriving);
            }

            try
            {
                await context.Invoke();
            }
            finally
            {
                _ = state.RunningWrites.TryRemove(running, out _);
            }

            if (forwarded is var (key, record)
                && state.HoldsWriteReply(key, record, accepted: context.Result is JournalForwardedWriteOutcome { Value: JournalWriteOutcome.Accepted }))
            {
                await state.WriteReplies.WaitAsync(silo.Name, CancellationToken.None);
            }

            if (context.Grain is IPlacedJournaledChunksGrain && method == nameof(IChunksGrain.AppendAsync))
            {
                await state.AppendReplies.WaitAsync(silo.Name, CancellationToken.None);
            }

            if (context.Grain is IPlacedJournaledChunksGrain && method == nameof(IChunksGrain.SiloAsync))
            {
                await state.SiloReplies.WaitAsync(silo.Name, CancellationToken.None);
            }

            if (context.Grain is IClusterControllerGrain && method == nameof(IClusterControllerGrain.CommitLedgerMapAsync)
                && context.Result is LedgerMapCommitOutcome { Value: LedgerMapCommitOutcome.Applied }
                && context.Request.GetArgument(0) is LedgerMapChange { Value: LedgerMapChange.Install applied })
            {
                state.AppliedInstalls.Enqueue(applied);
            }

            if (context.Grain is IClusterControllerGrain && method == nameof(IClusterControllerGrain.CommitManifestAsync)
                && context.Result is ManifestCommitOutcome { Value: ManifestCommitOutcome.Applied }
                && context.Request.GetArgument(0) is ManifestChange { Value: ManifestChange.Drain appliedDrain })
            {
                state.AppliedDrains.Enqueue(appliedDrain.Ledger);
            }

            if (context.Grain is IClusterControllerGrain && method == nameof(IClusterControllerGrain.CommitLedgerMapAsync)
                && context.Result is LedgerMapCommitOutcome { Value: LedgerMapCommitOutcome.Applied } && state.TakeMapReplyFailure())
            {
                throw new InvalidDataException("The test fails the decoding of an applied commit's reply.");
            }
        }
    }

    /// <summary>
    /// Counts the reports each silo sends, fails a silo's hand-off requests at once, synchronously, while a test asks, as
    /// a call does which cannot be sent, holds the replica writes a silo sends while a test asks, and loses the calls
    /// across the test's partition.
    /// </summary>
    private sealed class HandOffFault(IConfiguration configuration, ILocalSiloDetails silo) : IOutgoingGrainCallFilter
    {
        public Task Invoke(IOutgoingGrainCallContext context)
        {
            var state = States[configuration[TestKey]!];
            if (state.Partition is { } partition && Across(partition, context))
            {
                return LoseAsync(state, context);
            }

            if (context.InterfaceMethod.DeclaringType == typeof(ILedgerStoreGrain) && context.InterfaceMethod.Name == nameof(ILedgerStoreGrain.WriteAsync))
            {
                return SendAsync(state, context);
            }

            if (context.InterfaceMethod.DeclaringType == typeof(ILedgerStoreGrain) && context.InterfaceMethod.Name == nameof(ILedgerStoreGrain.ReadTailAsync)
                && state.HeldLedger is { } sealing && context.Request.GetArgument(0) is LedgerReplicaTailRequest { Seal: true } seal && seal.Ledger == sealing.Value)
            {
                return SealAsync(state, context);
            }

            if (context.InterfaceMethod.Name == nameof(IClusterControllerGrain.ReportAsync))
            {
                _ = state.ReportsSent.AddOrUpdate(silo.Name, 1, static (_, reports) => reports + 1);
            }

            if (context.InterfaceMethod.DeclaringType == typeof(IClusterControllerGrain))
            {
                switch (context.Request.GetArgument(0))
                {
                    case ManifestChange { Value: ManifestChange.Drain drain }:
                        state.ArchiveSubmissions.Enqueue((silo.Name, drain.Ledger));
                        break;
                    case LedgerMapChange { Value: LedgerMapChange.Drop drop }:
                        state.ArchiveSubmissions.Enqueue((silo.Name, drop.Ledger));
                        break;
                    default:
                        break;
                }
            }

            if (state.FaultHandOffsFrom == silo.Name && context.InterfaceMethod.Name == nameof(IClusterControllerGrain.HandOffAsync))
            {
                state.CountHandOffFault();
                throw new InvalidOperationException("The test fails the silo's hand-off request before it is sent.");
            }

            return context.Invoke();
        }

        private async Task SendAsync(TestState state, IOutgoingGrainCallContext context)
        {
            await state.ReplicaSends.WaitAsync(silo.Name, CancellationToken.None, context.Request.GetArgument(0));
            await context.Invoke();
        }

        private async Task SealAsync(TestState state, IOutgoingGrainCallContext context)
        {
            await state.SealSends.WaitAsync(silo.Name, CancellationToken.None, context.TargetId.GetGuidKey());
            await context.Invoke();
        }

        /// <summary>Returns whether <paramref name="context"/> calls, from one side of <paramref name="partition"/>, a grain of the other side's node.</summary>
        private bool Across((string Silo, NodeId Node, string Other, NodeId OtherNode) partition, IOutgoingGrainCallContext context)
        {
            var type = context.InterfaceMethod.DeclaringType;
            if (type != typeof(IJournalStoreGrain) && type != typeof(ILedgerStoreGrain) && type != typeof(INodeControlGrain))
            {
                return false;
            }

            var target = context.TargetId.GetGuidKey();
            return silo.Name == partition.Silo && target == partition.OtherNode.Value || silo.Name == partition.Other && target == partition.Node.Value;
        }

        /// <summary>
        /// Loses a call across the partition as the network would, failing it as a lost message's response timeout does: a
        /// forwarded journal write's reply once its owner answered it, so its effect may stand; any other call's request.
        /// </summary>
        private async Task LoseAsync(TestState state, IOutgoingGrainCallContext context)
        {
            if (context.InterfaceMethod.DeclaringType == typeof(IJournalStoreGrain) && context.InterfaceMethod.Name == nameof(IJournalStoreGrain.WriteAsync)
                && context.Request.GetArgument(0) is JournalForwardedEntry entry)
            {
                // The callee releases the forwarded record, so its payload is copied before the call.
                var record = entry.Record.ToArray();
                var payload = Orleans.Durable.Storage.Engine.Journals.JournalEntry.Decode(JournalRowKey.Encode(entry.Key).Span, new System.Buffers.ReadOnlySequence<byte>(record), out var decoded) is null
                    ? record[^(int)decoded.PayloadLength..]
                    : throw new InvalidDataException($"A forwarded write of '{entry.Key}' does not decode.");
                await context.Invoke();
                state.LostReplies.Enqueue((silo.Name, entry.Key, payload, context.Result is JournalForwardedWriteOutcome { Value: JournalWriteOutcome.Accepted }));
                throw new TimeoutException($"The test's partition lost the reply of a forwarded write of '{entry.Key}'.");
            }

            state.LostRequests.Enqueue(context.InterfaceMethod.Name);
            throw new TimeoutException($"The test's partition lost a call of {context.InterfaceMethod.DeclaringType?.Name}.{context.InterfaceMethod.Name}.");
        }
    }

    /// <summary>
    /// Records each silo's node, node control runtime, Ledgers runtime, and lifecycle, and when its stop reaches each stage;
    /// and holds one silo's stop below its Ledgers runtime's stage.
    /// </summary>
    private sealed class SiloCapture(LocalNode node, LocalNodeControl control, LocalJournals journals, NodeLedgers ledgers, DurableStorageStatus status, ILocalSiloDetails silo, IConfiguration configuration) : ILifecycleParticipant<ISiloLifecycle>
    {
        /// <summary>The stages whose stop the stop report times, from the first a stop reaches.</summary>
        private static readonly (int Stage, string Name)[] TimedStages =
        [
            (ServiceLifecycleStage.Last, nameof(ServiceLifecycleStage.Last)),
            (NodeLedgers.Stage, nameof(NodeLedgers)),
            (ServiceLifecycleStage.Active, nameof(ServiceLifecycleStage.Active)),
            (ServiceLifecycleStage.BecomeActive, nameof(ServiceLifecycleStage.BecomeActive)),
            (ServiceLifecycleStage.ApplicationServices, nameof(ServiceLifecycleStage.ApplicationServices)),
            (ServiceLifecycleStage.RuntimeGrainServices, nameof(ServiceLifecycleStage.RuntimeGrainServices)),
            (ServiceLifecycleStage.RuntimeServices, nameof(ServiceLifecycleStage.RuntimeServices)),
            (ServiceLifecycleStage.RuntimeInitialize, nameof(ServiceLifecycleStage.RuntimeInitialize)),
            (ServiceLifecycleStage.First, nameof(ServiceLifecycleStage.First)),
        ];

        public void Participate(ISiloLifecycle observer)
        {
            var state = States[configuration[TestKey]!];
            state.LocalNodes[silo.Name] = node;
            state.Controls[silo.Name] = control;
            state.Journals[silo.Name] = journals;
            state.Statuses[silo.Name] = status;
            state.Silos[silo.Name] = (observer, ledgers);
            foreach (var (stage, name) in TimedStages)
            {
                _ = observer.Subscribe(
                    nameof(SiloCapture),
                    stage,
                    static _ => Task.CompletedTask,
                    _ =>
                    {
                        state.StopStages[(silo.Name, name)] = TimeProvider.System.GetTimestamp();
                        state.RecordStop(silo.Name, $"stop reaches stage {name} ({stage})");
                        return Task.CompletedTask;
                    });
            }

            _ = observer.Subscribe(
                nameof(SiloCapture),
                ServiceLifecycleStage.Active,
                static _ => Task.CompletedTask,
                async _ => await state.StopGate.WaitAsync(silo.Name, CancellationToken.None));
        }
    }

    /// <summary>Returns the ordinal of the test cluster's silo named <paramref name="silo"/>: 0 for the primary, n for <c>Secondary_n</c>.</summary>
    private static int OrdinalOf(string silo) =>
        silo == "Primary" ? 0 : int.Parse(silo["Secondary_".Length..], System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// The operating system's WAL storage, whose measurement of a silo's WAL directory adds the bytes the test names for
    /// the silo (<see cref="TestState.ExtraWalBytes"/>).
    /// </summary>
    private sealed class PressingWalStorage(TestState state, string silo) : WalStorage
    {
        private readonly PortableWalStorage _inner = new();

        public override ValueTask CreateDirectoryAsync(string path, CancellationToken cancellationToken) => _inner.CreateDirectoryAsync(path, cancellationToken);

        public override ValueTask<WalDirectory> OpenDirectoryAsync(string path, CancellationToken cancellationToken) => _inner.OpenDirectoryAsync(path, cancellationToken);

        public override ValueTask DeleteDirectoryAsync(string path, CancellationToken cancellationToken) => _inner.DeleteDirectoryAsync(path, cancellationToken);

        public override ValueTask<IReadOnlyList<string>> ListDirectoriesAsync(string path, CancellationToken cancellationToken) => _inner.ListDirectoriesAsync(path, cancellationToken);

        public override async ValueTask<WalDirectoryUsage> MeasureDirectoryAsync(string path, CancellationToken cancellationToken)
        {
            if (state.FailedDiskMeasurements.ContainsKey(silo))
            {
                throw new IOException("The test's volume refuses its measurement.");
            }

            var usage = await _inner.MeasureDirectoryAsync(path, cancellationToken);
            return Path.GetFileName(Path.TrimEndingDirectorySeparator(path)) == "wal"
                ? usage with { UsedBytes = usage.UsedBytes + state.ExtraWalBytes.GetValueOrDefault(silo) }
                : usage;
        }
    }

    /// <summary>Records a silo's disk pressure and throttle events, with the ledger each names.</summary>
    private sealed class PressureEvents(TestState state, string silo) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) =>
            categoryName == typeof(NodeDisk).FullName || categoryName == typeof(NodePressure).FullName ? new Logger(state, silo) : NullLogger.Instance;

        public void Dispose()
        {
        }

        private sealed class Logger(TestState state, string silo) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState scope)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState log, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                var ledger = log is IReadOnlyList<KeyValuePair<string, object?>> fields ? fields.FirstOrDefault(static field => field.Key == "Ledger").Value : null;
                state.PressureEvents.Enqueue((silo, eventId.Id, ledger));
            }
        }
    }

    /// <summary>
    /// Records each warning a silo logs, from Orleans' lifecycle and membership and from Clog, for the stop report, and the
    /// start and end of each of its stop's steps, which <see cref="NodeLedgers"/> and <see cref="DurableStorageNode"/> log at
    /// Information, so a report names the step a stop waited in.
    /// </summary>
    private sealed class StopWarnings(TestState state, string silo) : ILoggerProvider
    {
        /// <summary>The categories whose Information events name a stop's steps.</summary>
        public static readonly string[] StepCategories = [typeof(NodeLedgers).FullName!, typeof(DurableStorageNode).FullName!];

        public ILogger CreateLogger(string categoryName) => new Logger(state, silo, StepCategories.Contains(categoryName) ? LogLevel.Information : LogLevel.Warning);

        public void Dispose()
        {
        }

        private sealed class Logger(TestState state, string silo, LogLevel least) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState scope)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= least;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState log, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (IsEnabled(logLevel))
                {
                    state.RecordStop(silo, $"{logLevel} {eventId.Id}: {formatter(log, exception)}{(exception is null ? string.Empty : $" ({exception.GetType().Name}: {exception.Message})")}");
                }
            }
        }
    }

    /// <summary>Records each resend a silo's journal router logs, at Debug.</summary>
    private sealed class RouterResends(TestState state, string silo) : ILoggerProvider, ILogger
    {
        public ILogger CreateLogger(string categoryName) => categoryName == typeof(JournalRouter).FullName ? this : NullLogger.Instance;

        public void Dispose()
        {
        }

        public IDisposable? BeginScope<TState>(TState scope)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState log, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var message = formatter(log, exception);
            if (message.StartsWith("Resending", StringComparison.Ordinal))
            {
                state.RouterResends.Enqueue((silo, message));
            }
        }
    }

    /// <summary>A journal storage consumer which skips every byte it is given, counting them.</summary>
    private sealed class SkippingConsumer : IJournalStorageConsumer
    {
        public long Bytes { get; private set; }

        public void Read(JournalBufferReader reader, IJournalMetadata? metadata)
        {
            Bytes += reader.Length;
            reader.Skip(reader.Length);
        }
    }

    private sealed class NodeConfigurator : ISiloConfigurator
    {
        public void Configure(ISiloBuilder siloBuilder)
        {
            var state = States[siloBuilder.Configuration[TestKey]!];
            var silo = siloBuilder.Configuration["Orleans:Name"]!;
            var root = Path.Combine(state.Roots, silo);
            _ = Directory.CreateDirectory(root);
            _ = siloBuilder.Services.AddSingleton<IMetaStore>(new RecordingMetaStore(state, silo));

            _ = siloBuilder.Services.AddSingleton<ILifecycleParticipant<ISiloLifecycle>, SiloCapture>();
            _ = siloBuilder.Services.AddSingleton<WalStorage>(new PressingWalStorage(state, silo));
            _ = siloBuilder.Services.AddSingleton<ILoggerProvider>(_ => new PressureEvents(state, silo));
            _ = siloBuilder.Services.AddSingleton<ILoggerProvider>(_ => new StopWarnings(state, silo));
            _ = siloBuilder.Services.AddSingleton<ILoggerProvider>(_ => new RouterResends(state, silo));
            _ = siloBuilder.Services.AddLogging(static logging => logging.AddFilter<RouterResends>(typeof(JournalRouter).FullName, LogLevel.Debug).AddFilter<StopWarnings>(typeof(NodeLedgers).FullName, LogLevel.Information).AddFilter<StopWarnings>(typeof(DurableStorageNode).FullName, LogLevel.Information));
            _ = siloBuilder.AddIncomingGrainCallFilter<ControlFilter>();
            _ = siloBuilder.AddOutgoingGrainCallFilter<HandOffFault>();
            if (state.FastMembership)
            {
                _ = siloBuilder.Configure<ClusterMembershipOptions>(static membership =>
                {
                    membership.ProbeTimeout = TimeSpan.FromSeconds(1);
                    membership.NumMissedProbesLimit = 2;
                    membership.NumVotesForDeathDeclaration = 1;
                });
            }

            var control = new ControlOptions
            {
                LedgersPerNode = 1,
                SequencerWindow = 4,
                TransitionTimeout = TimeSpan.FromSeconds(10),
                ControlInterval = TimeSpan.FromMilliseconds(200),
                BalanceInterval = TimeSpan.FromMilliseconds(200),

                // Map changes reach the silos through report replies and executions alone (orleans.md §5.6).
                MapRefreshInterval = TimeSpan.FromHours(1),
                ReplicaSweepInterval = TimeSpan.FromSeconds(1),
                ManifestCommitInterval = TimeSpan.Zero,
                SweepInterval = TimeSpan.FromMilliseconds(200),
            };
            state.Control?.Invoke(control);
            var node = new DurableStorageOptions
            {
                Cluster = state.Cluster,
                Root = root,
                Ordinal = state.UseOrdinals ? OrdinalOf(silo) : null,
                WalFileSize = 1 << 20,
                PreallocateWalFiles = false,
                ArchiveStore = state.ArchiveStore,
                ArchiveStorePrefix = state.Prefix,

                // A test machine's free space puts no node under disk pressure; a test adds WAL bytes for that.
                MinimumFreeDiskBytes = 0,
            };
            state.Node?.Invoke(silo, node);

            // The silo's stores under the prefix it is configured with: the test's own, which hold each object under its
            // name without the prefix and refuse a name outside it, so a silo configured with another prefix sees the
            // cluster's objects, and a composition which bypassed the prefix fails at once; the archive-log container places
            // durable storage's objects under the prefix in that bucket, as a real container does. For a prefix derived from the service
            // ID, the stores the cluster shares with others, which hold every name whole.
            if (node.ArchiveStorePrefix is { } prefix)
            {
                _ = siloBuilder.Services.AddSingleton<IObjectStore>(new PrefixStrippingObjectStore(new RecordingObjectStore(state, silo), prefix + "/"));
                _ = siloBuilder.Services.AddSingleton<IArchiveLogContainer>(new TestArchiveLogContainer(
                    prefix,
                    () => new RecordingArchiveLogStore(state, silo),
                    () => new PrefixStrippingObjectStore(new RecordingObjectStore(state, silo), prefix + "/")));
            }
            else
            {
                var (objects, container) = state.Shared ?? throw new InvalidOperationException("A cluster whose prefix is derived runs over shared stores.");
                _ = siloBuilder.Services.AddSingleton(objects);
                _ = siloBuilder.Services.AddSingleton(container);
            }

            var siloOptions = new DurableStorageSiloOptions { Node = node, Control = control };
            _ = siloBuilder.AddDurableJournalStorageWith(siloOptions, state.ProviderName);
            _ = siloBuilder.Services.Configure<global::Orleans.Durable.Journaling.Json.JsonJournalOptions>(static options => options.AddTypeInfoResolver(JournaledChunksJsonContext.Default));
            state.Silo?.Invoke(silo, siloBuilder);
        }
    }

    /// <summary>Gives the test's client the response timeout its test chooses.</summary>
    private sealed class ClientConfigurator : IClientBuilderConfigurator
    {
        public void Configure(IConfiguration configuration, IClientBuilder clientBuilder)
        {
            if (States[configuration[TestKey]!].ClientResponseTimeout is { } timeout)
            {
                _ = clientBuilder.Configure<ClientMessagingOptions>(options => options.ResponseTimeout = timeout);
            }
        }
    }

    /// <summary>
    /// A <see cref="TimeProvider"/> whose UTC wall clock advances only when the test calls <see cref="Advance"/>, while its
    /// timers and high-resolution timestamps delegate to <see cref="TimeProvider.System"/> so the silo's loops and stop bounds
    /// run normally.
    /// </summary>
    private sealed class SteppableUtcTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private long _utcTicks = start.UtcTicks;

        public override DateTimeOffset GetUtcNow() => new(Volatile.Read(ref _utcTicks), TimeSpan.Zero);

        public void Advance(TimeSpan delta) => _ = Interlocked.Add(ref _utcTicks, delta.Ticks);

        public override long GetTimestamp() => System.GetTimestamp();

        public override long TimestampFrequency => System.TimestampFrequency;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            System.CreateTimer(callback, state, dueTime, period);
    }
}
