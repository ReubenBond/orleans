using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Durable.Storage.Engine.Archive;
using Orleans.Durable.Storage.Engine.Journals;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.Ledgers.Control;
using Orleans.Durable.Storage.Hosting;
using Orleans.TestingHost;

namespace Orleans.Durable.Storage.Tests;

/// <summary>
/// The multi-silo scenarios (orleans.md §9, verification §10): each runs a <see cref="ClusterWorkload"/> through a
/// transition, its disrupted ledger's journals contended by several clients, then checks the workload's history against
/// the journal model and each journal's final read against the fold of its history read through the archive's slow path.
/// Each runs over the LSM and over archive logs, but for the manifest's own scenario. The
/// smoke set runs with the default tests; the rest are explicit, and <c>just e2e</c> runs the whole set (verification §12).
/// </summary>
public sealed partial class OrleansControllerTests
{
    private const string EndToEnd = "EndToEnd";

    /// <summary>
    /// OR-I1, OR-I7, journals.md §2.2, under load: while clients write every journal, a primary hands its ledger off with
    /// writes of the workload in its writer, which keeps appending until its seal. The gates of
    /// <see cref="AWriteFencedByAHandOffIsResentToTheNewPrimaryAndLandsOnce"/> keep every replica write of that ledger
    /// from a quorum and the seal and reads at the old primary's silo from the new primary's recovery, so the writes in
    /// the writer are answered <c>Fenced</c> and resent, at least one of them, which the routers' log shows; every
    /// journal then progresses, and the history is linearizable.
    /// </summary>
    [Theory(Timeout = 180_000)]
    [Trait("Category", EndToEnd)]
    [InlineData(ArchiveStoreKind.Lsm)]
    [InlineData(ArchiveStoreKind.Logs)]
    public async Task AWorkloadStaysLinearizableWhileAPrimaryKeepsAppendingThroughItsHandOff(ArchiveStoreKind archiveStore)
    {
        _state.ArchiveStore = archiveStore;
        var cluster = await DeployAsync(4, TestContext.Current.CancellationToken);
        _ = await ServedAsync(cluster, ranges: 3, done: value => Balanced(cluster, value));
        var map = await EverySiloFollowsTheMapAsync(cluster);
        // The last silo, which the teardown stops first: the handed-off node stays pending until its silo stops, so a silo
        // stopped before it could place no hand-off of its own and would wait out the host's stop timeout.
        var (node, _, handle) = Nodes(cluster).Last(entry => entry.Handle != cluster.Primary && map.Ledgers.Any(ledger => ledger.IsOpen && ledger.Primary.Node == entry.Node));
        var ledger = map.Ledgers.First(ledger => ledger.IsOpen && ledger.Primary.Node == node);
        await JournalsServeAsync(cluster);
        var (keys, contended) = WorkloadKeys("handoff", ledger, focused: 3, total: 8);
        await using var workload = NewWorkload(keys, contended);
        workload.Start(Routers(cluster));
        await workload.ProgressesAsync(2, Patience);

        _state.HeldLedger = ledger.Id;
        var held = _state.ReplicaWrites.Hold();
        var seal = _state.TailReads.Hold(handle.Name);
        _ = _state.ReplicaReads.Hold(handle.Name);
        await held.WaitAsync(Patience, Token);
        await Controller(cluster).HandOffAsync(node, handle.SiloAddress.Generation);
        await EventuallyAsync(async () => (await ReadMapAsync()).Get(ledger.Id) is not { IsOpen: true }, $"s{ledger.Id} to close");
        await seal.WaitAsync(Patience, Token);
        _state.ReplicaWrites.Release();
        _state.TailReads.Release();
        _state.ReplicaReads.Release();

        await workload.ProgressesAsync(3, Patience);
        Assert.Contains(_state.RouterResends, resend => contended.Any(key => resend.Message.Contains($"'{key}' after Fenced", StringComparison.Ordinal)));
        await AssertWorkloadAsync(cluster, workload);
    }

    /// <summary>
    /// orleans.md §5.7, R2, OR-I19, journals.md §2.2, under load: a silo whose node is a primary is killed while clients
    /// write every journal through the other silos, with the killed node's writes in flight. The first write its quorum
    /// accepts once the holds begin keeps its reply at the killed silo, so its client's router answers <c>Ambiguous</c>
    /// and the takeover's recovery keeps the entry, which the journal's final read shows. The killed silo then holds every
    /// replica write before it leaves the silo, and once one is held, every forwarded write's reply, as a crash would: a
    /// write it admits after that reaches no quorum, its router answers <c>Ambiguous</c>, and the recovery fences the entry,
    /// which no read shows. A kill runs the silo's stop, which answers its admitted writes once it ends them, so the held
    /// replies keep those answers from their routers.
    /// The controller takes the killed node's ledgers over, every journal progresses, and the history is linearizable. The
    /// killed silo's router serves no client, so each answer comes from a surviving silo's router.
    /// </summary>
    [Theory(Timeout = 180_000)]
    [Trait("Category", EndToEnd)]
    [InlineData(ArchiveStoreKind.Lsm)]
    [InlineData(ArchiveStoreKind.Logs)]
    public async Task AWorkloadStaysLinearizableThroughAKillAndTakeover(ArchiveStoreKind archiveStore)
    {
        _state.ArchiveStore = archiveStore;
        // The killed silo's stop waits for its held proposals within the transition timeout, so a short one ends it sooner.
        _state.Control = static options => options.TransitionTimeout = TimeSpan.FromSeconds(3);
        var cluster = await DeployAsync(4, fastMembership: true, TestContext.Current.CancellationToken);
        _ = await ServedAsync(cluster, ranges: 4, done: value => Balanced(cluster, value));
        var map = await EverySiloFollowsTheMapAsync(cluster);
        var (node, _, handle) = Nodes(cluster).First(entry => entry.Handle != cluster.Primary && map.Ledgers.Any(ledger => ledger.IsOpen && ledger.Primary.Node == entry.Node));
        var ledger = map.Ledgers.First(ledger => ledger.IsOpen && ledger.Primary.Node == node);
        await JournalsServeAsync(cluster);
        var (keys, contended) = WorkloadKeys("kill", ledger, focused: 3, total: 8);
        await using var workload = NewWorkload(keys, contended);
        workload.Disrupted[handle.Name] = true;
        workload.Start(Routers(cluster));
        await workload.ProgressesAsync(2, Patience);

        // A kill runs the silo's stop, which may hand its ledgers off while the silo still answers; the seal and reads of the
        // ledger whose journals the hold below pins reach the killed silo's own replica, which holds the pinned entry, so
        // they are held there and its successor's recovery decides the tail from the other two replicas, as a crash's would.
        _state.HeldLedger = ledger.Id;
        _ = _state.TailReads.Hold(handle.Name);
        _ = _state.ReplicaReads.Hold(handle.Name);
        await _state.HoldWriteReplies(handle.Name).WaitAsync(Patience, Token);

        // Every replica write the silo sends is held from here, and the hold arrives once one carries a forwarded write the
        // silo began after the hold took effect, whose every send it therefore holds: with the silo's own replica, one of
        // three, the entry reaches no quorum. Markers and writes begun earlier are held without arriving. The order of
        // writes begun is read once the hold holds, so a write which began before then, one of whose sends may have left
        // the silo, never arrives.
        Tuple<string, byte[]>? fenced = null;
        var holding = long.MaxValue;
        var arrived = _state.ReplicaSends.Hold(handle.Name, call =>
            call is LedgerReplicaWriteRequest { Recovery: false } request
            && request.Payload.ToArray() is var batch
            && _state.RunningWrites.Values.FirstOrDefault(write => write.Silo == handle.Name && write.Sequence > Volatile.Read(ref holding)
                && ledger.Range.Contains(JournalRowKey.Encode(write.Key).Span) && batch.AsSpan().IndexOf(write.Payload) >= 0) is { Payload: not null } carried
            && Interlocked.CompareExchange(ref fenced, Tuple.Create(carried.Key, carried.Payload), null) is null);
        Volatile.Write(ref holding, _state.WritesBegun);
        await arrived.WaitAsync(Patience, Token);
        _state.HoldEveryWriteReply();
        await cluster.KillSiloAsync(handle, Token).WaitAsync(Patience, Token);

        _ = await ServedAsync(cluster, ranges: 3, done: value => !value.Ledgers.Any(ledger => ledger.IsOpen && ledger.Primary.Node == node));
        _state.ReplicaSends.Release();
        _state.WriteReplies.Release();
        _state.TailReads.Release();
        _state.ReplicaReads.Release();
        await workload.ProgressesAsync(3, Patience);
        await AssertWorkloadAsync(cluster, workload);

        var (heldKey, heldRecord) = Assert.NotNull(_state.HeldReply);
        var ambiguous = workload.AmbiguousWrites;
        var landed = Assert.Single(ambiguous, write => write.Key == heldKey && heldRecord.AsSpan().EndsWith(write.Payload));
        Assert.True(workload.FinalContent[heldKey].AsSpan().IndexOf(landed.Payload) >= 0, $"The accepted write to '{heldKey}' whose reply the killed silo held is not in its final content.");
        Assert.NotNull(fenced);
        var (fencedKey, fencedPayload) = fenced;
        Assert.Contains(ambiguous, write => write.Key == fencedKey && write.Payload.AsSpan().SequenceEqual(fencedPayload));
        Assert.True(workload.FinalContent[fencedKey].AsSpan().IndexOf(fencedPayload) < 0, $"The write to '{fencedKey}' whose replica writes the killed silo held is in its final content.");
    }

    /// <summary>
    /// orleans.md §5.7, OR-I5, R2, under load: in a cluster of exactly <c>N</c> = 3 silos, a silo whose node is a primary
    /// is killed while clients write every journal through the other silos. The two survivors are fewer than <c>N</c> and
    /// a quorum, so the controller takes each of the killed node's ledgers over at once onto a degraded successor which
    /// keeps the killed node among its replicas, and every journal progresses on the remaining quorum. The killed silo
    /// then restarts over its disk, within the grace, and serves its replicas again, each missing every write made while
    /// it was dead, and balancing hands it a primary; then the third silo is killed, so its ledgers' seals and recoveries,
    /// on two of three replicas, count the returned node's holey replicas, and their successors are degraded in turn.
    /// Every journal progresses, the history is linearizable, and every closed ledger drains completely and drops, though
    /// its drains read replicas with holes.
    /// </summary>
    [Theory(Timeout = 240_000)]
    [Trait("Category", EndToEnd)]
    [InlineData(ArchiveStoreKind.Lsm)]
    [InlineData(ArchiveStoreKind.Logs)]
    public async Task AWorkloadStaysLinearizableThroughAKillAndTakeoverOnThreeSilos(ArchiveStoreKind archiveStore)
    {
        _state.ArchiveStore = archiveStore;
        // The killed silo's stop waits for its proposals within the transition timeout, so a short one ends it sooner.
        _state.Control = static options => options.TransitionTimeout = TimeSpan.FromSeconds(3);
        var cluster = await DeployAsync(3, fastMembership: true, TestContext.Current.CancellationToken);
        _ = await ServedAsync(cluster, ranges: 3, done: value => Balanced(cluster, value));
        var map = await EverySiloFollowsTheMapAsync(cluster);
        var (node, _, handle) = Nodes(cluster).First(entry => entry.Handle != cluster.Primary && map.Ledgers.Any(ledger => ledger.IsOpen && ledger.Primary.Node == entry.Node));
        var (third, _, thirdHandle) = Nodes(cluster).Single(entry => entry.Handle != cluster.Primary && entry.Node != node);
        var ranges = map.Ledgers.Where(ledger => ledger.IsOpen && ledger.Primary.Node == node).Select(static ledger => ledger.Range).ToHashSet();
        await JournalsServeAsync(cluster);
        var (keys, contended) = WorkloadKeys("kill-three", map.Ledgers.First(ledger => ledger.IsOpen && ledger.Primary.Node == node), focused: 3, total: 8);
        await using var workload = NewWorkload(keys, contended);
        workload.Disrupted[handle.Name] = true;
        workload.Disrupted[thirdHandle.Name] = true;

        // A grain on the primary silo whose journal lies in the killed node's ledger, which no client writes, so the
        // successor holds its key cold until the grain writes again after the predecessor drops (#352).
        var killedLedger = map.Ledgers.First(ledger => ledger.IsOpen && ledger.Primary.Node == node);
        var grainFactory = Services(cluster.Primary!).GetRequiredService<IGrainFactory>();
        var grain = grainFactory.GetGrain<IJournaledChunksGrain>(KeyIn(killedLedger, "kill-three-grain", key => grainFactory.GetGrain<IJournaledChunksGrain>(key).GetGrainId()));
        Assert.Equal(1, await grain.AppendAsync([1]));
        workload.Start(Routers(cluster));
        await workload.ProgressesAsync(2, Patience);

        await cluster.KillSiloAsync(handle, Token).WaitAsync(Patience, Token);

        var taken = await ServedAsync(cluster, ranges: 3, done: value => !value.Ledgers.Any(ledger => ledger.IsOpen && ledger.Primary.Node == node));
        var successors = taken.Ledgers.Where(ledger => ledger.IsOpen && ranges.Contains(ledger.Range)).ToArray();
        Assert.Equal(ranges.Count, successors.Length);
        Assert.All(successors, successor => Assert.Equal(node, successor.Replicas[^1]));
        await workload.ProgressesAsync(3, Patience);

        // The killed silo returns over its disk as the same node, its replicas missing every write made while it was dead.
        _ = await cluster.RestartSiloAsync(handle).WaitAsync(Patience, Token);
        await JournalsServeAsync(cluster);
        _ = await ServedAsync(cluster, ranges: 3, done: value => value.Ledgers.Any(ledger => ledger.IsOpen && ledger.Primary.Node == node));
        await workload.ProgressesAsync(2, Patience);

        var before = await ReadMapAsync();
        var thirds = before.Ledgers.Where(ledger => ledger.IsOpen && ledger.Primary.Node == third).Select(static ledger => ledger.Range).ToHashSet();
        Assert.NotEmpty(thirds);
        Assert.All(before.Ledgers.Where(ledger => ledger.IsOpen && ledger.Primary.Node == third), ledger => Assert.Contains(node, ledger.Replicas));
        await cluster.KillSiloAsync(thirdHandle, Token).WaitAsync(Patience, Token);

        var again = await ServedAsync(cluster, ranges: 3, done: value => !value.Ledgers.Any(ledger => ledger.IsOpen && ledger.Primary.Node == third));
        Assert.All(again.Ledgers.Where(ledger => ledger.IsOpen && thirds.Contains(ledger.Range)), successor => Assert.Equal(third, successor.Replicas[^1]));
        await workload.ProgressesAsync(3, Patience);
        await AssertWorkloadAsync(cluster, workload);

        await EventuallyAsync(async () => (await ReadMapAsync()).Ledgers.All(static ledger => ledger.IsOpen), "every closed ledger to drain completely and drop");

        // The grain writes on at the version its activation holds, after its ledger dropped, through a cold successor.
        Assert.Equal(2, await grain.AppendAsync([2]));
        Assert.Equal([1, 2], await ReadJournalAsync(grain));
    }

    /// <summary>
    /// orleans.md §5.5, §5.7, under load: while a <c>Replace</c> executed by one node is held in its recovery, clients write
    /// that node's journals only through other silos' routers, so every call to it is forwarded, and those journals
    /// progress while the execution is held; once it completes, every journal progresses, including those of the replaced
    /// ledger, and the history is linearizable.
    /// </summary>
    [Theory(Timeout = 180_000)]
    [Trait("Category", EndToEnd)]
    [InlineData(ArchiveStoreKind.Lsm)]
    [InlineData(ArchiveStoreKind.Logs)]
    public async Task ForwardedCallsStayLinearizableWhileTheirNodeExecutesATransition(ArchiveStoreKind archiveStore)
    {
        _state.ArchiveStore = archiveStore;
        var cluster = await DeployAsync(4, TestContext.Current.CancellationToken);
        var (executor, handle, execution, _) = await HoldAnExecutionAsync(cluster);
        var map = await ReadMapAsync();
        var executors = map.Ledgers.First(ledger => ledger.IsOpen && ledger.Primary.Node == executor);
        var replaced = map.Get(Assert.NotNull(_state.HeldLedger))!.Value;
        await JournalsServeAsync(cluster);
        var (forwarded, contended) = WorkloadKeys("forwarded", executors, focused: 3, total: 3);
        var (others, _) = WorkloadKeys("replaced", replaced, focused: 2, total: 5);
        await using var workload = NewWorkload([.. forwarded, .. others], contended);
        workload.Start(Routers(cluster, except: handle));

        await workload.ProgressesAsync(3, Patience, forwarded);
        Assert.False(execution.IsCompleted, "The held execution completed before its tail reads were released.");
        _state.TailReads.Release();
        _ = await execution.WaitAsync(Patience, Token);

        await workload.ProgressesAsync(3, Patience);
        await AssertWorkloadAsync(cluster, workload);
    }

    /// <summary>
    /// orleans.md §5.2, under load: a primary's silo stops gracefully while clients write every journal; it asks the
    /// controller to hand its node off, its ledgers move to the others, every journal progresses through the others, and
    /// the history is linearizable.
    /// </summary>
    [Theory(Explicit = true, Timeout = 180_000)]
    [Trait("Category", EndToEnd)]
    [InlineData(ArchiveStoreKind.Lsm)]
    [InlineData(ArchiveStoreKind.Logs)]
    public async Task AWorkloadStaysLinearizableThroughAGracefulStop(ArchiveStoreKind archiveStore)
    {
        _state.ArchiveStore = archiveStore;
        var cluster = await DeployAsync(4, TestContext.Current.CancellationToken);
        var (node, handle) = await PrimaryAsync(cluster, silo => silo != cluster.Primary);
        var map = await EverySiloFollowsTheMapAsync(cluster);
        await JournalsServeAsync(cluster);
        var (keys, contended) = WorkloadKeys("stop", map.Ledgers.First(ledger => ledger.IsOpen && ledger.Primary.Node == node), focused: 3, total: 8);
        await using var workload = NewWorkload(keys, contended);
        workload.Start(Routers(cluster));
        await workload.ProgressesAsync(2, Patience);

        workload.Disrupted[handle.Name] = true;
        await cluster.StopSiloAsync(handle, Token).WaitAsync(Patience, Token);

        Assert.True(_state.HandOffRequests.GetValueOrDefault(node) > 0, "The stopping silo asked for no hand-off of its node.");
        _ = await ServedAsync(cluster, ranges: 3, done: value => !value.Ledgers.Any(ledger => ledger.IsOpen && ledger.Primary.Node == node));
        await workload.ProgressesAsync(3, Patience);
        await AssertWorkloadAsync(cluster, workload);
    }

    /// <summary>
    /// orleans.md §5.6, under load: clients write every journal, every silo stops, and the cluster restarts over the same
    /// stores and roots; new clients bind each journal's version by reading it, every journal progresses, and the history
    /// across the restart is linearizable.
    /// </summary>
    [Theory(Explicit = true, Timeout = 300_000)]
    [Trait("Category", EndToEnd)]
    [InlineData(ArchiveStoreKind.Lsm)]
    [InlineData(ArchiveStoreKind.Logs)]
    public async Task AWorkloadStaysLinearizableThroughAWholeClusterRestart(ArchiveStoreKind archiveStore)
    {
        _state.ArchiveStore = archiveStore;
        var first = await DeployAsync(3, TestContext.Current.CancellationToken);
        var map = await ServedAsync(first, ranges: 3, done: value => Balanced(first, value));
        await JournalsServeAsync(first);
        var (keys, contended) = WorkloadKeys("restart", map.Ledgers.First(static ledger => ledger.IsOpen), focused: 2, total: 8);
        await using var workload = NewWorkload(keys, contended);
        workload.Start(Routers(first));
        await workload.ProgressesAsync(3, Patience);
        await workload.StopAsync(Patience);

        _cluster = null;
        foreach (var silo in first.Silos.Reverse().ToList())
        {
            await first.StopSiloAsync(silo, Token).WaitAsync(Patience, Token);
        }

        await first.DisposeAsync();
        var predecessors = (await ReadMapAsync()).Ledgers.Where(static ledger => ledger.IsOpen).ToArray();
        var cluster = await DeployAsync(3, TestContext.Current.CancellationToken);
        _ = await ServedAsync(cluster, ranges: 3, done: value => predecessors.All(ledger => value.Get(ledger.Id) is not { IsOpen: true }));
        _ = await EverySiloFollowsTheMapAsync(cluster);
        await JournalsServeAsync(cluster);

        workload.Start(Routers(cluster));
        await workload.ProgressesAsync(3, Patience);
        await AssertWorkloadAsync(cluster, workload);
    }

    /// <summary>
    /// orleans.md §5.6, under load: a fourth silo joins a balanced cluster of three while clients write every journal, so
    /// the controller splits a ledger toward one open range per node, its primary executing, and hands a primary off onto
    /// the new node to balance them; then new clients write every journal through every silo's router, the new silo's
    /// among them. Every journal progresses, and the history is linearizable.
    /// </summary>
    [Theory(Explicit = true, Timeout = 300_000)]
    [Trait("Category", EndToEnd)]
    [InlineData(ArchiveStoreKind.Lsm)]
    [InlineData(ArchiveStoreKind.Logs)]
    public async Task AWorkloadStaysLinearizableWhileASiloJoinsAndTheControllerSplitsAndBalancesOntoIt(ArchiveStoreKind archiveStore)
    {
        _state.ArchiveStore = archiveStore;
        var cluster = await DeployAsync(3, TestContext.Current.CancellationToken);
        var map = await ServedAsync(cluster, ranges: 3, done: value => Balanced(cluster, value));
        _ = await EverySiloFollowsTheMapAsync(cluster);
        await JournalsServeAsync(cluster);
        var (keys, contended) = WorkloadKeys("join", map.Ledgers.First(static ledger => ledger.IsOpen), focused: 3, total: 8);
        await using var workload = NewWorkload(keys, contended);
        workload.Start(Routers(cluster));
        await workload.ProgressesAsync(2, Patience);
        var open = map.Ledgers.Where(static ledger => ledger.IsOpen).Select(static ledger => ledger.Range).ToHashSet();

        var added = await Bounded(cluster.StartAdditionalSiloAsync(), "the fourth silo's start");

        var joined = Services(added).GetRequiredService<LocalNode>().Node;
        _ = await ServedAsync(cluster, ranges: 4, done: value => Balanced(cluster, value) && value.Ledgers.Any(ledger => ledger.IsOpen && ledger.Primary.Node == joined));
        Assert.Contains(_state.AppliedInstalls, install => install.Opens.Length == 2 && install.Opens[0].Range.Sibling == install.Opens[1].Range
            && open.Contains(install.Opens[0].Range.Parent) && install.Opens.All(opening => opening.Primary.Node != joined));
        Assert.Contains(_state.AppliedInstalls, install => install.Closes.Length == 1 && install.Opens is [{ } handedOff] && handedOff.Primary.Node == joined);
        await JournalsServeAsync(cluster);
        _ = await EverySiloFollowsTheMapAsync(cluster);
        await workload.StopAsync(Patience);
        workload.Start(Routers(cluster));
        await workload.ProgressesAsync(3, Patience);
        await AssertWorkloadAsync(cluster, workload);
    }

    /// <summary>
    /// orleans.md §5.6, under load: one journal's replaces of 64 KiB make its ledger hot, far above
    /// <c>SplitBytesPerSecond</c>, while the workload's own load stays far below a quarter of it; the controller splits the
    /// hot ledger, its primary executing, so its children share that primary; once the replaces stop, both children are
    /// cold and the open ranges exceed the target, so the controller merges them back. Every journal progresses, the
    /// history is linearizable, and the hot journal, whose replaces crossed the split and the merge, reads its last
    /// snapshot and matches the fold of its history read through the slow path.
    /// </summary>
    [Theory(Explicit = true, Timeout = 300_000)]
    [Trait("Category", EndToEnd)]
    [InlineData(ArchiveStoreKind.Lsm)]
    [InlineData(ArchiveStoreKind.Logs)]
    public async Task AWorkloadStaysLinearizableThroughAHotSplitAndAColdMerge(ArchiveStoreKind archiveStore)
    {
        _state.ArchiveStore = archiveStore;
        _state.Control = static control =>
        {
            control.SplitBytesPerSecond = 1 << 20;
            control.LoadWindow = 2 * control.ControlInterval;
        };
        var cluster = await DeployAsync(3, TestContext.Current.CancellationToken);
        var map = await ServedAsync(cluster, ranges: 3, done: value => Balanced(cluster, value));
        _ = await EverySiloFollowsTheMapAsync(cluster);
        await JournalsServeAsync(cluster);
        var hot = map.Ledgers.First(static ledger => ledger.IsOpen);
        var (keys, contended) = WorkloadKeys("splits", hot, focused: 3, total: 8);
        await using var workload = NewWorkload(keys, contended);
        workload.Start(Routers(cluster));
        await workload.ProgressesAsync(2, Patience);

        var (lower, upper) = hot.Range.Children;
        bool Split() => _state.AppliedInstalls.Any(install => install.Opens.Select(static opening => opening.Range).ToHashSet().SetEquals([lower, upper]));
        var load = await LoadAsync(cluster, hot, Split);
        await EventuallyAsync(
            () => _state.AppliedInstalls.Any(install => install.Opens.Length == 1 && install.Opens[0].Range == hot.Range && install.Closes.Length == 2),
            $"the cold children of {hot.Range} to merge");

        await workload.ProgressesAsync(3, Patience);
        await AssertWorkloadAsync(cluster, workload);
        await AssertJournalAsync(cluster, load, new byte[64 << 10]);
    }

    /// <summary>
    /// orleans.md §5.3, §5.8, OR-I3, OR-I4, under load: a node which is a primary is retired while clients write every
    /// journal, some of them in its ledger; the controller hands off and repairs every ledger which names it, which drain
    /// and drop in lineage order, until its gate opens; every journal progresses, and the history is linearizable.
    /// </summary>
    [Theory(Explicit = true, Timeout = 300_000)]
    [Trait("Category", EndToEnd)]
    [InlineData(ArchiveStoreKind.Lsm)]
    [InlineData(ArchiveStoreKind.Logs)]
    public async Task AWorkloadStaysLinearizableWhileANodeIsRetired(ArchiveStoreKind archiveStore)
    {
        _state.ArchiveStore = archiveStore;
        var cluster = await DeployAsync(4, TestContext.Current.CancellationToken);
        var map = await ServedAsync(cluster, ranges: 4, done: value => Balanced(cluster, value));
        _ = await EverySiloFollowsTheMapAsync(cluster);
        await JournalsServeAsync(cluster);
        var (retired, _, _) = Nodes(cluster).First(entry => entry.Handle != cluster.Primary && map.Ledgers.Any(ledger => ledger.IsOpen && ledger.Primary.Node == entry.Node));
        var (keys, contended) = WorkloadKeys("retire", map.Ledgers.First(ledger => ledger.IsOpen && ledger.Primary.Node == retired), focused: 3, total: 8);
        await using var workload = NewWorkload(keys, contended);
        workload.Start(Routers(cluster));
        await workload.ProgressesAsync(2, Patience);

        await Controller(cluster).RetireNodeAsync(retired).WaitAsync(Patience, Token);

        // The gate opens only once no ledger names the node, so every ledger which named it dropped.
        await EventuallyAsync(async () => (await Controller(cluster).GetRetirementAsync(retired)).IsReleasable, "the retired node's gate to open");
        Assert.DoesNotContain((await ReadMapAsync()).Ledgers, ledger => ledger.Primary.Node == retired || ledger.Replicas.Contains(retired));
        await workload.ProgressesAsync(3, Patience);
        await AssertWorkloadAsync(cluster, workload);
    }

    /// <summary>
    /// orleans.md §5.6 (map size), §5.9, under load: while the object store refuses every call nothing drains, so the rolls
    /// the test requests of the ledgers outside the workload's grow the map. Each roll whose growth fits the room the value
    /// limit leaves beyond the takeover reserve, by the spec's rule, applies; the first which does not fit is held while
    /// the store refuses, and the map then still lies within the reserve. The workload's journals, in a ledger the test
    /// never rolls, each written by one client which reads only when an answer binds no version, keep accepting writes,
    /// which their warm state decides; a read of a journal with rows in the archive would wait for the store. Once the
    /// store answers again, drains complete, closed ledgers drop in lineage order, the held roll applies after a drop, and
    /// the clients read after their acceptances again. Every journal progresses, and the history is linearizable.
    /// </summary>
    [Theory(Explicit = true, Timeout = 300_000)]
    [Trait("Category", EndToEnd)]
    [InlineData(ArchiveStoreKind.Lsm)]
    [InlineData(ArchiveStoreKind.Logs)]
    public async Task AWorkloadStaysLinearizableThroughAnObjectStoreOutageWhichFillsTheMap(ArchiveStoreKind archiveStore)
    {
        _state.ArchiveStore = archiveStore;
        const int MapBytes = 2 << 10;
        _state.Node = static (_, node) => node.LedgerMapMaxBytes = MapBytes;
        var cluster = await DeployAsync(3, TestContext.Current.CancellationToken);
        var map = await ServedAsync(cluster, ranges: 3, done: value => Balanced(cluster, value));
        _ = await EverySiloFollowsTheMapAsync(cluster);
        await JournalsServeAsync(cluster);
        var warm = map.Ledgers.First(static ledger => ledger.IsOpen);
        var (keys, _) = WorkloadKeys("outage", warm, focused: 3, total: 3);
        await using var workload = NewWorkload(keys, contended: []);
        workload.Start(Routers(cluster));
        await workload.ProgressesAsync(2, Patience);

        // Each client finishes any read it is in and then only writes, so none waits for the store in a read.
        workload.ReadsAfterAcceptances = false;
        await workload.ProgressesAsync(2, Patience);
        _state.RefuseObjects = true;
        LedgerMetadata? held = null;
        for (var rolls = 0; held is null; rolls++)
        {
            Assert.True(rolls < 64, "The map did not fill.");
            var current = await ReadMapAsync();
            var roll = current.Ledgers.First(ledger => ledger.IsOpen && !ledger.Range.Overlaps(warm.Range));
            var reserve = TakeoverReserve(current);
            var fits = current.EncodedLength + GrowthOfRoll(current, roll) <= MapBytes - reserve;
            await Controller(cluster).RequestRollAsync(roll.Id).WaitAsync(Patience, Token);
            if (fits)
            {
                await EventuallyAsync(async () => (await ReadMapAsync()).Get(roll.Id) is not { IsOpen: true }, $"the roll of s{roll.Id}, which fits beyond the reserve, to apply");
            }
            else
            {
                Assert.True(current.EncodedLength <= MapBytes - reserve, $"The map of {current.EncodedLength} bytes holds part of the {reserve}-byte takeover reserve of its {MapBytes}-byte limit.");
                held = roll;
            }
        }

        // An absence: the held roll stays open over many balance intervals while the store refuses.
        await Task.Delay(TimeSpan.FromSeconds(3), TimeProvider.System, Token);
        Assert.True((await ReadMapAsync()).Get(held.Value.Id) is { IsOpen: true }, $"s{held.Value.Id}'s roll, which does not fit beyond the reserve, applied while the store refused every call.");
        await workload.ProgressesAsync(2, Patience);

        var writes = _state.MapWrites.Count;
        _state.RefuseObjects = false;

        await EventuallyAsync(async () => (await ReadMapAsync()).Get(held.Value.Id) is not { IsOpen: true }, $"the held roll of s{held.Value.Id} to apply");
        var after = _state.MapWrites.Skip(writes).ToArray();
        var closing = Array.FindIndex(after, write => write.Closed.Contains(held.Value.Id));
        Assert.True(closing > 0 && after[..closing].Any(static write => write.Dropped.Length > 0), $"s{held.Value.Id}'s roll applied before any drop freed room.");
        workload.ReadsAfterAcceptances = true;
        await workload.ProgressesAsync(3, Patience);
        await AssertWorkloadAsync(cluster, workload);
    }

    /// <summary>
    /// orleans.md §5.6 (controller grain), under load: every open ledger is rolled at once, and the controller grain's
    /// silo holds each rolled ledger's first drain commit until every one's has arrived, so the manifest committer receives
    /// them together and one manifest replace advances at least two of the rolled ledgers, which the replaced values show;
    /// each replace starts at least <c>ManifestCommitInterval</c> after the previous one completed. Then the workload
    /// stops, every open ledger rolls again, and every ledger which held a workload row drops, so each journal's fold
    /// through the slow path rebases while its owner may still name its state by its earlier version (journals.md §8.3).
    /// Every journal progresses, and the history is linearizable.
    /// </summary>
    [Fact(Explicit = true, Timeout = 300_000)]
    [Trait("Category", EndToEnd)]
    public async Task ConcurrentDrainCommitsShareManifestReplacesAnIntervalApartUnderLoad()
    {
        var interval = TimeSpan.FromSeconds(1);
        _state.Control = control => control.ManifestCommitInterval = interval;
        var cluster = await DeployAsync(3, TestContext.Current.CancellationToken);
        var map = await ServedAsync(cluster, ranges: 3, done: value => Balanced(cluster, value));
        _ = await EverySiloFollowsTheMapAsync(cluster);
        await JournalsServeAsync(cluster);
        var (keys, contended) = WorkloadKeys("manifest", map.Ledgers.First(static ledger => ledger.IsOpen), focused: 3, total: 8);
        await using var workload = NewWorkload(keys, contended);
        workload.Start(Routers(cluster));
        await workload.ProgressesAsync(2, Patience);

        LedgerId[] rolled = [.. map.Ledgers.Where(static ledger => ledger.IsOpen).Select(static ledger => ledger.Id)];
        var barrier = new DrainBarrier(rolled);
        _state.DrainCommitBarrier = barrier;
        var advances = _state.ManifestAdvances.Count;
        await Bounded(Task.WhenAll(rolled.Select(ledger => Controller(cluster).RequestRollAsync(ledger))), "the roll requests");
        await barrier.All.WaitAsync(Patience, Token);
        await EventuallyAsync(async () => await _state.StoredManifestAsync() is { } stored && rolled.All(stored.IsComplete), "every rolled ledger's drain to complete");

        Assert.True(
            _state.ManifestAdvances.Skip(advances).Any(advanced => advanced.Count(rolled.Contains) >= 2),
            $"No manifest replace advanced two of the rolled ledgers {string.Join(", ", rolled)}: {string.Join("; ", _state.ManifestAdvances.Skip(advances).Select(static advanced => string.Join(", ", advanced)))}.");
        var times = _state.ManifestReplaceTimes.OrderBy(static times => times.Started).ToArray();
        for (var index = 1; index < times.Length; index++)
        {
            var gap = TimeProvider.System.GetElapsedTime(times[index - 1].Ended, times[index].Started);
            Assert.True(gap >= interval * 0.9, $"Manifest replace {index} started {gap} after the previous one completed.");
        }

        await workload.ProgressesAsync(2, Patience);
        await workload.StopAsync(Patience);
        var stopped = await ReadMapAsync();
        await Bounded(Task.WhenAll(stopped.Ledgers.Where(static ledger => ledger.IsOpen).Select(ledger => Controller(cluster).RequestRollAsync(ledger.Id))), "the second roll requests");
        await EventuallyAsync(async () => !(await ReadMapAsync()).Ledgers.Any(ledger => stopped.Get(ledger.Id) is not null), "every ledger listed when the workload stopped to drop");
        await AssertWorkloadAsync(cluster, workload);
        Assert.Equal(keys.Count, workload.RebasedFinalReads);
    }

    /// <summary>
    /// Replaces a journal in <paramref name="ledger"/>'s range with 64 KiB snapshots through its primary's silo, each at the
    /// version the last answer gave, until <paramref name="done"/> holds, so the ledger's proposed bytes far exceed the
    /// workload's; returns the journal, which is not one of the workload's.
    /// </summary>
    private static async Task<string> LoadAsync(TestCluster cluster, LedgerMetadata ledger, Func<bool> done)
    {
        var key = Enumerable.Range(0, int.MaxValue).Select(static index => string.Create(CultureInfo.InvariantCulture, $"load-{index}")).First(candidate => ledger.Range.Contains(JournalRowKey.Encode(candidate).Span));
        var rowKey = JournalRowKey.Encode(key);
        var router = Services(Nodes(cluster).Single(entry => entry.Node == ledger.Primary.Node).Handle).GetRequiredService<JournalRouter>();
        var started = TimeProvider.System.GetTimestamp();
        var expected = JournalVersion.Origin;
        while (!done())
        {
            Assert.True(TimeProvider.System.GetElapsedTime(started) < Patience, $"Waited {Patience} for the load on {ledger.Range} to end.");
            using var snapshot = Payload(new byte[64 << 10]);
            using var record = JournalEntryRecord.Replace(rowKey, expected, snapshot);
            expected = (await router.WriteAsync(key, rowKey, record, Token)).Value switch
            {
                JournalWriteOutcome.Accepted accepted => accepted.Version,
                JournalWriteOutcome.Rejected rejected => rejected.CurrentVersion,
                var other => throw new InvalidOperationException($"A load replace of '{key}' answered {other}."),
            };
            await Task.Delay(TimeSpan.FromMilliseconds(20), TimeProvider.System, Token);
        }

        return key;
    }

    /// <summary>
    /// Returns the room the controller grain keeps in <paramref name="map"/> for the takeovers of the node with the most
    /// primaries: its primaries times the most a takeover of an open ledger adds (orleans.md §5.6).
    /// </summary>
    private static long TakeoverReserve(LedgerMapValue map)
    {
        var open = map.Ledgers.Where(static ledger => ledger.IsOpen).ToArray();
        return (long)open.GroupBy(static ledger => ledger.Primary.Node).Max(static primaries => primaries.Count()) * open.Max(ledger => GrowthOfRoll(map, ledger));
    }

    /// <summary>Returns what a roll or takeover of <paramref name="ledger"/> with its current replicas adds to <paramref name="map"/>.</summary>
    private static int GrowthOfRoll(LedgerMapValue map, LedgerMetadata ledger) =>
        map.GrowthOf(new Transition.Replace(ledger.Id, new TransitionChoice(ledger.Replicas[0], ledger.Replicas, ledger.K, 0, map.Epoch)));

    /// <summary>Returns a workload over <paramref name="keys"/> which also fails on any drop the test's MetaStore flagged.</summary>
    private ClusterWorkload NewWorkload(IReadOnlyList<string> keys, IReadOnlyCollection<string> contended) =>
        new(keys, contended) { Faults = () => _state.DropViolations };

    /// <summary>
    /// Reads <paramref name="key"/> through the primary silo's router and through the slow path, and asserts that both show
    /// <paramref name="content"/> and agree as the workload's final reads must (<see cref="ClusterWorkload.AssertFinalRead"/>).
    /// </summary>
    private static async Task AssertJournalAsync(TestCluster cluster, string key, byte[] content)
    {
        var primary = Assert.IsType<SiloHandle>(cluster.Primary, exactMatch: false);
        var rowKey = JournalRowKey.Encode(key);
        var read = await Bounded(Services(primary).GetRequiredService<JournalRouter>().ReadAsync(key, rowKey, Token), $"The read of '{key}'");
        using (read.Content)
        {
            var bytes = read.Content.Reader.PeekSlice(read.Content.Length);
            byte[] answered;
            try
            {
                answered = bytes.ToArray();
            }
            finally
            {
                bytes.Dispose();
            }

            using var history = await Bounded(ReadSlowPathAsync(cluster, rowKey), $"The slow-path read of '{key}'");
            _ = ClusterWorkload.AssertFinalRead(key, read.Version, read.Exists, answered, history.Prefix, history.Rows);
            Assert.Equal(content, answered);
        }
    }

    /// <summary>
    /// Reads <paramref name="rowKey"/>'s history through the archive's slow path on the silo whose node owns its open
    /// ledger (archive.md §2.2: only the open ledger's primary process guarantees <c>ServedHere</c> / linearizable
    /// open-ledger tails; another process's contiguous tail may lag the primary's last write until explicit tail
    /// publication).
    /// </summary>
    private static async Task<ArchiveRead> ReadSlowPathAsync(TestCluster cluster, JournalRowKey rowKey)
    {
        var fallback = Assert.IsType<SiloHandle>(cluster.Primary, exactMatch: false);
        var fallbackRuntime = Assert.IsType<NodeRuntime>(Runtime(fallback));
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var map = await fallbackRuntime.View.ReadAsync(Token);
            var openPrimary = map.Find(rowKey.Span)?.Open.Primary.Node;
            var ownerHandle = openPrimary is { } owner
                ? Nodes(cluster).FirstOrDefault(entry => entry.Node == owner).Handle
                : null;
            if (ownerHandle is not null)
            {
                var ownerRuntime = Assert.IsType<NodeRuntime>(Runtime(ownerHandle));
                _ = await ownerRuntime.View.ReadAsync(Token);
                var read = await new ArchiveReader(ownerRuntime.Reader, ownerRuntime.Archive, TimeProvider.System).ReadAsync(rowKey.Memory, Token);
                if (read.Linearizable)
                {
                    return read;
                }

                read.Dispose();
            }
        }

        _ = await fallbackRuntime.View.ReadAsync(Token);
        return await new ArchiveReader(fallbackRuntime.Reader, fallbackRuntime.Archive, TimeProvider.System).ReadAsync(rowKey.Memory, Token);
    }

    /// <summary>Awaits <paramref name="task"/> within <see cref="Patience"/>, naming <paramref name="what"/> if it does not complete.</summary>
    private static async Task<T> Bounded<T>(Task<T> task, string what)
    {
        try
        {
            return await task.WaitAsync(Patience, Token);
        }
        catch (TimeoutException timeout)
        {
            throw new TimeoutException($"{what} did not complete within {Patience}.", timeout);
        }
    }

    /// <inheritdoc cref="Bounded{T}(Task{T}, string)"/>
    private static async Task Bounded(Task task, string what)
    {
        try
        {
            await task.WaitAsync(Patience, Token);
        }
        catch (TimeoutException timeout)
        {
            throw new TimeoutException($"{what} did not complete within {Patience}.", timeout);
        }
    }

    /// <summary>
    /// Returns <paramref name="total"/> journal keys named from <paramref name="prefix"/>, the first <paramref name="focused"/>
    /// in <paramref name="ledger"/>'s range and the rest outside it, and those first, which several clients contend.
    /// </summary>
    private static (IReadOnlyList<string> Keys, IReadOnlyCollection<string> Contended) WorkloadKeys(string prefix, LedgerMetadata ledger, int focused, int total)
    {
        var candidates = Enumerable.Range(0, int.MaxValue).Select(index => string.Create(CultureInfo.InvariantCulture, $"{prefix}-{index}"));
        string[] inside = [.. candidates.Where(key => ledger.Range.Contains(JournalRowKey.Encode(key).Span)).Take(focused)];
        var outside = candidates.Where(key => !ledger.Range.Contains(JournalRowKey.Encode(key).Span)).Take(total - focused);
        return ([.. inside, .. outside], inside);
    }

    /// <summary>Returns the routers of <paramref name="cluster"/>'s silos as they are now, but <paramref name="except"/>'s.</summary>
    private static Func<IReadOnlyList<(string Silo, JournalRouter Router)>> Routers(TestCluster cluster, SiloHandle? except = null)
    {
        (string Silo, JournalRouter Router)[] routers = [.. cluster.Silos.Where(silo => silo != except).Select(static silo => (silo.Name, Services(silo).GetRequiredService<JournalRouter>()))];
        return () => routers;
    }

    private async Task JournalsServeAsync(TestCluster cluster) =>
        await EventuallyAsync(() => Nodes(cluster).All(entry => _state.Journals[entry.Handle.Name].State == LocalJournals.JournalsState.Serving), "every silo's Journals to serve");

    /// <summary>
    /// Stops the workload's clients and checks its history through a silo it did not disrupt, the cluster's primary silo,
    /// once that silo's view follows the map: its router answers the final reads, and its ledger reader and archive store
    /// read each journal's history through the slow path, each after a read of the map, so the fold is never older than
    /// the state the read's owner answered from. Every ledger the map dropped meanwhile was dropped in lineage
    /// order (LG-I10) and once the stored manifest showed it complete (archive-lsm.md §2.6).
    /// </summary>
    private async Task AssertWorkloadAsync(TestCluster cluster, ClusterWorkload workload)
    {
        await workload.StopAsync(Patience);
        _ = await EverySiloFollowsTheMapAsync(cluster);
        var primary = Assert.IsType<SiloHandle>(cluster.Primary, exactMatch: false);
        await workload.AssertLinearizableAsync(
            Services(primary).GetRequiredService<JournalRouter>(),
            rowKey => new ValueTask<ArchiveRead>(ReadSlowPathAsync(cluster, rowKey)),
            Patience,
            Token);
        Assert.True(_state.DropViolations.IsEmpty, string.Join(Environment.NewLine, _state.DropViolations));
    }
}
