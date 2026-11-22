using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Durable.Storage.Engine.Cluster.Control;
using Orleans.Durable.Storage.Engine.Journals;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.Ledgers.Control;
using Orleans.Durable.Storage.Hosting;
using Orleans.TestingHost;

namespace Orleans.Durable.Storage.Tests;

/// <summary>
/// The primary's lifecycle under load (OR-I1, orleans.md §5.7, verification §10): racing takers of one ledger, a primary
/// falsely suspected while it keeps running, and a move of a ledger across a partition which membership does not see.
/// Each runs a <see cref="ClusterWorkload"/> and checks its history as the other multi-silo scenarios do; balancing is
/// paused once the map is balanced, so only the scenario's own transitions move ledgers. The controller grain sends every
/// transition in a cluster and takes over no ledger it is replacing, so while a scenario's own transition runs, the
/// controller hears no report: it learns of no claim that transition's recovery ends and sends no takeover of its own.
/// </summary>
public sealed partial class OrleansControllerTests
{
    /// <summary>
    /// OR-I1, orleans.md §5.7, under load: two nodes take one ledger over at once, each with a choice made at the same map
    /// epoch which excludes the ledger's primary. Both recoveries seal the ledger, and the controller grain holds both
    /// installs until one from each taker has arrived, then lets them race, which the controller grain's ledger map
    /// committer decides: exactly one applies, the other is <c>Superseded</c>, one register write closes the ledger, and
    /// the map opens one successor over the range, whose writer the winner holds and for which the loser keeps no
    /// lifetime, while the old primary keeps running. Every journal progresses, and the history is linearizable. Two
    /// committers racing on one register, as two controller activations would, are
    /// <c>LedgerMapCommitterConformanceTests</c>'.
    /// </summary>
    [Fact(Explicit = true, Timeout = 180_000)]
    [Trait("Category", EndToEnd)]
    public async Task RacingTakersOfOneLedgerInstallExactlyOneSuccessor()
    {
        var cluster = await DeployAsync(4, TestContext.Current.CancellationToken);
        var map = await BalancedAndPausedAsync(cluster);
        var (node, _, _) = Nodes(cluster).Last(entry => entry.Handle != cluster.Primary && map.Ledgers.Any(ledger => ledger.IsOpen && ledger.Primary.Node == entry.Node));
        var ledger = map.Ledgers.First(ledger => ledger.IsOpen && ledger.Primary.Node == node);
        NodeId[] takers = [.. Nodes(cluster).Select(static entry => entry.Node).Where(other => other != node)];
        await JournalsServeAsync(cluster);
        var (keys, contended) = WorkloadKeys("racing", ledger, focused: 3, total: 8);
        await using var workload = NewWorkload(keys, contended);
        workload.Start(Routers(cluster));
        await workload.ProgressesAsync(2, Patience);

        _state.HeldLedger = ledger.Id;
        _ = _state.ReportCalls.Hold();
        var arrived = new ConcurrentDictionary<NodeId, bool>();
        var both = _state.Installs.Hold(arrives: call => call is LedgerMapChange.Install install && arrived.TryAdd(install.Opens[0].Primary.Node, true) && arrived.Count == 2);
        var choice = await ChoiceAsync(cluster, takers[0]);
        Task<TransitionReply> Take(NodeId taker) =>
            NodeControl(cluster, taker).ExecuteAsync(new Transition.Replace(ledger.Id, new TransitionChoice(taker, [taker, .. takers.Where(other => other != taker)], choice.K, choice.ChosenAt, choice.Epoch)));
        Task<TransitionReply>[] racing = [Take(takers[0]), Take(takers[1])];
        await Bounded(both, "both takers' installs to reach the controller grain");
        Assert.DoesNotContain(racing, static execution => execution.IsCompleted);
        _state.Installs.Release();
        var replies = await Bounded(Task.WhenAll(racing), "the racing takers' executions");
        _state.ReportCalls.Release();

        var won = Assert.Single(Enumerable.Range(0, 2), index => replies[index].Value is LedgerMapCommitOutcome.Applied);
        _ = Assert.IsType<LedgerMapCommitOutcome.Superseded>(replies[1 - won].Value);
        var (winner, loser) = (takers[won], takers[1 - won]);
        var after = await ReadMapAsync();
        var successor = Assert.Single(after.Ledgers, open => open.IsOpen && open.Range.Overlaps(ledger.Range));
        Assert.Equal((ledger.Range, winner), (successor.Range, successor.Primary.Node));
        _ = Assert.Single(_state.MapWrites, write => write.Closed.Contains(ledger.Id));
        var losing = Assert.IsType<NodeRuntime>(Runtime(Nodes(cluster).Single(entry => entry.Node == loser).Handle));
        await EventuallyAsync(() => losing.Writers.PendingLifetimes == 0, $"the losing taker to abandon its lifetime of s{ledger.Id}'s successor");

        await workload.ProgressesAsync(3, Patience);
        await AssertWorkloadAsync(cluster, workload);
    }

    /// <summary>
    /// OR-I1, LP-I4, orleans.md §5.7's false <c>Dead</c> declaration, under load: other nodes take over two ledgers while
    /// their primary keeps running, as the controller's takeovers after a false suspicion do, and the old primary learns
    /// nothing of it: its silo's reads of the map are held, so its view still names its node primary of both, and it holds
    /// both writers, neither fenced, since the workload's clients of their journals are paused and no call reaches them.
    /// The first contact of one stale writer is a write of a journal it holds warm, admitted without a load and proposed
    /// to the sealed replicas: the seal fences the writer, and the write waits for the ledger's <c>T</c>, which only the
    /// map gives it (LP-I16), so once the map is readable it is answered <c>Fenced</c>, never accepted, and is absent from
    /// its journal, whose slow path folds the closed ledger below its recorded <c>T</c> (LG-I5). The first contact of the other is a read of a journal the new primary has written: its
    /// seal check finds the ledger sealed and fences the writer, and the read answers <c>Moved</c>, never the stale
    /// absence; naming no owner is accepted, since the stale view cannot name the new one. So the old primary is never a
    /// second writer. Once its view follows the map, its writers are gone, every journal progresses, and the history is
    /// linearizable.
    /// </summary>
    [Fact(Explicit = true, Timeout = 180_000)]
    [Trait("Category", EndToEnd)]
    public async Task AFalselySuspectedPrimaryKeepsRunningAndIsFencedNeverASecondWriter()
    {
        var cluster = await DeployAsync(4, TestContext.Current.CancellationToken);
        var map = await BalancedAndPausedAsync(cluster);
        // The suspected silo's map reads are held, so it hosts no controller grain, whose committer reads the map.
        var controllers = await ControllerSilosAsync(cluster);
        var (node, _, handle) = Nodes(cluster).Last(entry => entry.Handle != cluster.Primary && !controllers.Contains(entry.Handle.SiloAddress)
            && map.Ledgers.Any(ledger => ledger.IsOpen && ledger.Primary.Node == entry.Node));
        var read = map.Ledgers.First(ledger => ledger.IsOpen && ledger.Primary.Node == node);
        NodeId[] takers = [.. Nodes(cluster).Select(static entry => entry.Node).Where(other => other != node)];

        // The node becomes the primary of a second ledger, so one stale writer meets a write and the other a read.
        var taken = map.Ledgers.First(ledger => ledger.IsOpen && ledger.Primary.Node != node);
        _ = _state.ReportCalls.Hold();
        _ = Assert.IsType<LedgerMapCommitOutcome.Applied>((await NodeControl(cluster, node).ExecuteAsync(new Transition.Replace(taken.Id, await ChoiceAsync(cluster, node)))).Value);
        _state.ReportCalls.Release();
        map = await EverySiloFollowsTheMapAsync(cluster);
        var written = map.Ledgers.Single(ledger => ledger.IsOpen && ledger.Range == taken.Range);
        Assert.Equal(node, written.Primary.Node);
        await JournalsServeAsync(cluster);
        var (keys, contended) = WorkloadKeys("suspected", read, focused: 3, total: 8);
        await using var workload = NewWorkload(keys, contended);
        workload.Start(Routers(cluster, except: handle));
        await workload.ProgressesAsync(2, Patience);

        // A journal the old primary holds warm admits a write without a load, whose open check would find the seal first.
        var grain = JournalNode(cluster, node);
        var fencedKey = KeyIn(written);
        var warm = Assert.IsType<JournalWriteOutcome.Accepted>((await Bounded(ForwardAsync(grain, fencedKey, JournalVersion.Origin, [1]), "the write which warms the journal")).Value).Version;
        await workload.PauseAsync(keys.Where(key => read.Range.Contains(JournalRowKey.Encode(key).Span) || written.Range.Contains(JournalRowKey.Encode(key).Span)), Patience);
        _ = _state.MapReads.Hold(handle.Name);
        _ = _state.ReportCalls.Hold();
        var successors = new Dictionary<LedgerId, NodeId>();
        foreach (var (ledger, taker) in new[] { (read, takers[0]), (written, takers[1]) })
        {
            var choice = await ChoiceAsync(cluster, taker);
            var replicas = new[] { taker }.Concat(takers.Where(other => other != taker));
            _ = Assert.IsType<LedgerMapCommitOutcome.Applied>((await NodeControl(cluster, taker).ExecuteAsync(new Transition.Replace(ledger.Id, new TransitionChoice(taker, [.. replicas], choice.K, choice.ChosenAt, choice.Epoch)))).Value);
            successors[ledger.Id] = taker;
        }

        _state.ReportCalls.Release();
        var suspected = Assert.IsType<NodeRuntime>(Runtime(handle));
        var writers = new Dictionary<LedgerId, LedgerWriter>();
        foreach (var ledger in new[] { read, written })
        {
            Assert.True(suspected.View.Current.Get(ledger.Id) is { IsOpen: true } stale && stale.Primary.Node == node, $"The suspected primary's view learned s{ledger.Id}'s takeover.");
            writers[ledger.Id] = Assert.IsType<LedgerWriter>(suspected.Writers.TryGet(ledger.Id));
            Assert.False(writers[ledger.Id].Fenced.IsCompleted, $"The suspected primary's writer of s{ledger.Id} was fenced before its first contact.");
        }

        // A write's proposal is the stale writer's first contact with the sealed replicas. Its seal stops the writer, and
        // the write waits for the ledger's T from the map, which decides whether it landed (LP-I16).
        var writing = ForwardAsync(grain, fencedKey, warm, [2, 3]);
        _ = await Bounded(Task.WhenAny(writing, writers[written.Id].Fenced), "the suspected primary's write to meet the seal");
        if (writing.IsCompletedSuccessfully)
        {
            _ = Assert.IsType<JournalWriteOutcome.Fenced>((await writing).Value);
        }

        Assert.True(writers[written.Id].Fenced.IsCompleted, $"The write's proposal met s{written.Id}'s seal without fencing its writer.");

        // A read of a journal the new primary wrote is the other stale writer's first contact: its seal check.
        var readKey = Enumerable.Range(0, int.MaxValue).Select(static index => string.Create(CultureInfo.InvariantCulture, $"stale-read-{index}")).First(key => read.Range.Contains(JournalRowKey.Encode(key).Span));
        var owner = JournalNode(cluster, successors[read.Id]);
        _ = Assert.IsType<JournalWriteOutcome.Accepted>((await Bounded(ForwardAsync(owner, readKey, JournalVersion.Origin, [4, 5, 6]), "the new primary's write")).Value);
        Assert.False(writers[read.Id].Fenced.IsCompleted, $"The suspected primary's writer of s{read.Id} was fenced before the read.");
        var reading = grain.ReadAsync(readKey, Token);
        _ = await Bounded(Task.WhenAny(reading, writers[read.Id].Fenced), "the suspected primary's read to reach its seal check");
        if (reading.IsCompletedSuccessfully && (await reading).Value is JournalForwardedRead.Found early)
        {
            early.FirstPage.Dispose();
            Assert.Fail($"The suspected primary answered a read of s{read.Id}, taken from it, from its stale state.");
        }

        Assert.True(suspected.View.Current.Get(read.Id) is { IsOpen: true } && suspected.View.Current.Get(written.Id) is { IsOpen: true }, "The suspected primary's view learned a takeover while its map reads were held.");
        _state.MapReads.Release();
        _ = Assert.IsType<JournalWriteOutcome.Fenced>((await Bounded(writing, "the suspected primary's write")).Value);
        var answered = (await Bounded(reading, "the suspected primary's read")).Value;
        if (answered is JournalForwardedRead.Found found)
        {
            found.FirstPage.Dispose();
        }

        Assert.True(answered is JournalMoved { Owner: var mover } && mover != node, $"The suspected primary answered its read {answered}.");
        Assert.True(writers[read.Id].Fenced.IsCompleted, $"The read's seal check did not fence s{read.Id}'s stale writer.");
        await EventuallyAsync(() => suspected.Writers.TryGet(read.Id) is null && suspected.Writers.TryGet(written.Id) is null, "the suspected primary to drop its stale writers");
        workload.Resume();
        await workload.ProgressesAsync(3, Patience);
        await AssertWorkloadAsync(cluster, workload);
        await AssertJournalAsync(cluster, fencedKey, [1]);
        await AssertJournalAsync(cluster, readKey, [4, 5, 6]);
    }

    /// <summary>
    /// verification §10, OR-I1, OR-I7, under load: every call between two silos' nodes is lost, a partition membership
    /// does not see, while a <c>Replace</c> moves the first silo's node's ledger to the second's, with the old primary
    /// among its successor's replicas. A forwarded write's reply is lost once its owner answered, so its router answers
    /// <c>Ambiguous</c>; every other call is lost before it leaves its silo, so the successor's recovery decides without
    /// the old primary's replica, its writes reach a quorum without it, and reads across the partition are resent until it
    /// heals. On each side of the move, a write of a new journal sent through the router across the partition is accepted
    /// with its reply lost; each lands exactly once: its journal reads as that write alone, and the same write resent at
    /// its version is rejected. Once the partition heals, a write from the old primary's silo to the moved ledger is
    /// accepted, no call is lost any more, every journal progresses, the history is linearizable, and each workload write
    /// whose accepted reply was lost, at least one, is among its client's <c>Ambiguous</c> writes and in its journal's
    /// final content once.
    /// </summary>
    [Fact(Explicit = true, Timeout = 180_000)]
    [Trait("Category", EndToEnd)]
    public async Task AWorkloadStaysLinearizableWhileALedgerMovesAcrossAPartition()
    {
        var cluster = await DeployAsync(4, TestContext.Current.CancellationToken);
        var map = await BalancedAndPausedAsync(cluster);
        var (node, _, handle) = Nodes(cluster).Last(entry => entry.Handle != cluster.Primary && map.Ledgers.Any(ledger => ledger.IsOpen && ledger.Primary.Node == entry.Node));
        var (other, _, otherHandle) = Nodes(cluster).First(entry => entry.Node != node && entry.Handle != cluster.Primary);
        var third = Nodes(cluster).First(entry => entry.Node != node && entry.Node != other).Node;
        var ledger = map.Ledgers.First(ledger => ledger.IsOpen && ledger.Primary.Node == node);
        await JournalsServeAsync(cluster);
        var (keys, contended) = WorkloadKeys("partition", ledger, focused: 3, total: 8);
        await using var workload = NewWorkload(keys, contended);
        workload.Start(Routers(cluster));
        await workload.ProgressesAsync(2, Patience);

        _state.Partition = (handle.Name, node, otherHandle.Name, other);
        var before = await WriteAcrossAsync(otherHandle, ledger, "lost-before");
        _ = _state.ReportCalls.Hold();
        var choice = await ChoiceAsync(cluster, other);
        var moved = await NodeControl(cluster, other).ExecuteAsync(new Transition.Replace(ledger.Id, new TransitionChoice(other, [other, node, third], choice.K, choice.ChosenAt, choice.Epoch)));
        _ = Assert.IsType<LedgerMapCommitOutcome.Applied>(moved.Value);
        _state.ReportCalls.Release();
        var after = await WriteAcrossAsync(handle, ledger, "lost-after");
        Assert.Contains(_state.LostRequests, static request => request == nameof(ILedgerStoreGrain.ReadTailAsync));
        await EventuallyAsync(() => _state.LostReplies.Any(lost => lost.Accepted && keys.Contains(lost.Key)), "a workload write accepted across the partition with its reply lost");
        _state.Partition = null;

        // Healed: a new write from the old primary's silo reaches the moved ledger's new primary and is accepted.
        var healed = Enumerable.Range(0, int.MaxValue).Select(static index => string.Create(CultureInfo.InvariantCulture, $"healed-{index}")).First(key => ledger.Range.Contains(JournalRowKey.Encode(key).Span));
        using (var body = Payload("healed;"u8))
        using (var record = JournalEntryRecord.Append(JournalRowKey.Encode(healed), JournalVersion.Origin, body))
        {
            var outcome = await Bounded(Services(handle).GetRequiredService<JournalRouter>().WriteAsync(healed, JournalRowKey.Encode(healed), record, Token), $"The write of '{healed}' after the heal");
            _ = Assert.IsType<JournalWriteOutcome.Accepted>(outcome.Value);
        }

        var lost = (_state.LostRequests.Count, _state.LostReplies.Count);

        foreach (var (key, payload) in new[] { before, after })
        {
            await AssertJournalAsync(cluster, key, payload);
            var router = Services(cluster.Silos[0]).GetRequiredService<JournalRouter>();
            using var body = Payload(payload);
            using var record = JournalEntryRecord.Append(JournalRowKey.Encode(key), JournalVersion.Origin, body);
            _ = Assert.IsType<JournalWriteOutcome.Rejected>((await Bounded(router.WriteAsync(key, JournalRowKey.Encode(key), record, Token), $"The resend of '{key}'")).Value);
            await AssertJournalAsync(cluster, key, payload);
        }

        await workload.ProgressesAsync(3, Patience);
        Assert.Equal(lost, (_state.LostRequests.Count, _state.LostReplies.Count));
        await AssertWorkloadAsync(cluster, workload);
        await AssertJournalAsync(cluster, healed, "healed;"u8.ToArray());
        var ambiguous = workload.AmbiguousWrites;
        var landed = _state.LostReplies.Where(lost => lost.Accepted && keys.Contains(lost.Key)).ToList();
        Assert.NotEmpty(landed);
        Assert.All(landed, lost =>
        {
            Assert.Contains(ambiguous, write => write.Key == lost.Key && write.Payload.AsSpan().SequenceEqual(lost.Payload));
            var content = workload.FinalContent[lost.Key];
            var at = content.AsSpan().IndexOf(lost.Payload);
            Assert.True(at >= 0 && content.AsSpan(at + 1).IndexOf(lost.Payload) < 0, $"The accepted write to '{lost.Key}' whose reply the partition lost is not in its final content exactly once.");
        });
    }

    /// <summary>
    /// Appends to a new journal of <paramref name="ledger"/> at its origin through <paramref name="from"/>'s router, whose
    /// owner is across the partition: its owner accepts it and the partition loses the reply, so the router answers
    /// <c>Ambiguous</c>. Returns the journal and its payload.
    /// </summary>
    private async Task<(string Key, byte[] Payload)> WriteAcrossAsync(SiloHandle from, LedgerMetadata ledger, string prefix)
    {
        var key = Enumerable.Range(0, int.MaxValue).Select(index => string.Create(CultureInfo.InvariantCulture, $"{prefix}-{index}")).First(candidate => ledger.Range.Contains(JournalRowKey.Encode(candidate).Span));
        var payload = Encoding.UTF8.GetBytes($"{key};");
        var rowKey = JournalRowKey.Encode(key);
        using var body = Payload(payload);
        using var record = JournalEntryRecord.Append(rowKey, JournalVersion.Origin, body);
        var outcome = await Bounded(Services(from).GetRequiredService<JournalRouter>().WriteAsync(key, rowKey, record, Token), $"The write of '{key}' through {from.Name}");
        Assert.True(outcome.Value is JournalWriteOutcome.Ambiguous, $"The write of '{key}' through {from.Name} answered {outcome.Value}.");
        Assert.Contains(_state.LostReplies, lost => lost.Silo == from.Name && lost.Key == key && lost.Accepted);
        return (key, payload);
    }

    /// <summary>Waits for a balanced map which every silo's view follows, then pauses balancing; returns the map.</summary>
    private async Task<LedgerMapValue> BalancedAndPausedAsync(TestCluster cluster)
    {
        _ = await ServedAsync(cluster, ranges: 4, done: value => Balanced(cluster, value));
        foreach (var silo in cluster.Silos)
        {
            Services(silo).GetRequiredService<ControlOptions>().MaxConcurrentTransitions = 0;
        }

        return await EverySiloFollowsTheMapAsync(cluster);
    }
}
