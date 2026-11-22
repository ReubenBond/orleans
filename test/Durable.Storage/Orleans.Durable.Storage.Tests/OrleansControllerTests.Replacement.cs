using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Durable.Storage.Control;
using Orleans.Durable.Storage.Engine.Cluster.Control;
using Orleans.Durable.Storage.Engine.Journals;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.Ledgers.Control;
using Orleans.Durable.Journaling;
using Orleans.Runtime;
using Orleans.Storage;
using Orleans.TestingHost;

namespace Orleans.Durable.Storage.Tests;

/// <summary>
/// Re-placement of journaled grains (orleans.md §5.4): when a ledger's primary moves, each silo asks the
/// <c>[JournalPlacement]</c> activations whose journals' primary moved away to migrate, the director places them on the new
/// primary's silo, and each recovers its state by reading its journal there.
/// </summary>
public sealed partial class OrleansControllerTests
{
    /// <summary>
    /// orleans.md §5.4: a <c>[JournalPlacement]</c> grain on its journal's primary's silo migrates, with no call, to the
    /// silo of the node a <c>Replace</c> makes its ledger's primary, and its new activation reads the state its journal holds
    /// and writes on; a grain of the same journal range placed by Orleans' local placement stays where it is.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task AJournalPlacedGrainMigratesToItsJournalsNewPrimaryAndRecoversItsState()
    {
        var cluster = await DeployAsync(4, TestContext.Current.CancellationToken);
        var map = await BalancedAndPausedAsync(cluster);
        await JournalsServeAsync(cluster);
        var (placedKey, ledger) = PlacedKey(cluster, map, "migrating");
        var (_, _, old) = Nodes(cluster).Single(entry => entry.Node == ledger.Primary.Node);
        var (target, _, moved) = Nodes(cluster).First(entry => entry.Node != ledger.Primary.Node);
        var placed = cluster.Client.GetGrain<IPlacedJournaledChunksGrain>(placedKey);
        _ = await placed.AppendAsync([1, 2]);
        Assert.Equal(2, await placed.AppendAsync([3]));
        Assert.Equal(old.SiloAddress, await placed.SiloAsync());
        var activation = await placed.ActivationAsync();
        var localKey = KeyIn(ledger, "local", key => cluster.Client.GetGrain<IJournaledChunksGrain>(key).GetGrainId());
        var local = Services(old).GetRequiredService<IGrainFactory>().GetGrain<IJournaledChunksGrain>(localKey);
        Assert.Equal(1, await local.AppendAsync([9]));
        Assert.Equal(old.SiloAddress, await local.SiloAsync());

        await ReplaceAsync(cluster, ledger, target);
        await EventuallyAsync(async () => (await GrainSilosAsync(cluster, placed.GetGrainId())).SequenceEqual([moved.SiloAddress]), $"the placed grain to migrate to {moved.Name}");

        Assert.True(Services(old).GetRequiredService<JournalReplacement>().Migrations >= 1, "The old primary's silo asked no migration.");
        Assert.Equal(moved.SiloAddress, await placed.SiloAsync());
        Assert.NotEqual(activation, await placed.ActivationAsync());
        Assert.Equal([1, 2, 3], await placed.ReadAsync());
        Assert.Equal(3, await placed.AppendAsync([4]));
        Assert.Equal(old.SiloAddress, await local.SiloAsync());
        Assert.Equal([9], await local.ReadAsync());
    }

    /// <summary>
    /// orleans.md §5.4: a grain asked to migrate while an append runs in it, its entry durable and its reply held in the
    /// request, stays until the request ends, as Orleans drains running requests before it deactivates; the append is
    /// acknowledged, and the grain's journal holds it on the new primary's silo.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task AnAppendRunningWhenItsGrainIsAskedToMigrateIsInItsJournalAfterTheMove()
    {
        var cluster = await DeployAsync(4, TestContext.Current.CancellationToken);
        var map = await BalancedAndPausedAsync(cluster);
        await JournalsServeAsync(cluster);
        var (key, ledger) = PlacedKey(cluster, map, "running");
        var (_, _, old) = Nodes(cluster).Single(entry => entry.Node == ledger.Primary.Node);
        var (target, _, moved) = Nodes(cluster).First(entry => entry.Node != ledger.Primary.Node);
        var placed = cluster.Client.GetGrain<IPlacedJournaledChunksGrain>(key);
        Assert.Equal(1, await placed.AppendAsync([1]));
        Assert.Equal(old.SiloAddress, await placed.SiloAsync());

        var held = _state.AppendReplies.Hold(old.Name);
        var running = placed.AppendAsync([2]);
        await Bounded(held, "the append's reply to be held");
        await ReplaceAsync(cluster, ledger, target);
        var replacement = Services(old).GetRequiredService<JournalReplacement>();
        await EventuallyAsync(() => replacement.Migrations >= 1, "the old primary's silo to ask the grain to migrate");

        Assert.False(running.IsCompleted, "The held append completed.");
        Assert.Equal([old.SiloAddress], await GrainSilosAsync(cluster, placed.GetGrainId()));
        _state.AppendReplies.Release();
        Assert.Equal(2, await Bounded(running, "the held append"));
        await EventuallyAsync(async () => (await GrainSilosAsync(cluster, placed.GetGrainId())).SequenceEqual([moved.SiloAddress]), $"the grain to migrate to {moved.Name}");
        Assert.Equal([1, 2], await ReadJournalAsync(placed));
    }

    /// <summary>
    /// orleans.md §5.4, OR-I7 (#351): an append whose caller cancelled it while its write waited for replicas is still the
    /// old activation's write in flight, so it may land after the grain migrated and its new activation read; that
    /// activation's next write then meets changed content and fails with <c>InconsistentStateException</c>, and the
    /// activation after it reads the entry, once, and writes on. Constructed: the old primary's writer writes are held at
    /// the remote replicas and its replica takes no part in the <c>Replace</c>'s seal, so the successor's prefix lacks the
    /// entry and the old writer answers it <c>Fenced</c>; a call holds the old activation while its storage resends the
    /// entry to the new primary, where the successor's remote replicas hold it until the new activation has read.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task ACancelledAppendWhichLandsAfterItsGrainMigratedFailsTheNewActivationsWrite()
    {
        var cluster = await DeployAsync(4, TestContext.Current.CancellationToken);
        var map = await BalancedAndPausedAsync(cluster);
        await JournalsServeAsync(cluster);
        var (key, ledger) = PlacedKey(cluster, map, "cancelled");
        var (_, _, old) = Nodes(cluster).Single(entry => entry.Node == ledger.Primary.Node);

        // The executor holds no replica of the ledger, so each copy its recovery makes is a remote write.
        var (target, _, moved) = Nodes(cluster).Single(entry => !ledger.Replicas.Contains(entry.Node));
        var placed = cluster.Client.GetGrain<IPlacedJournaledChunksGrain>(key);
        Assert.Equal(1, await placed.AppendAsync([1]));
        Assert.Equal(old.SiloAddress, await placed.SiloAsync());

        // The cancelled append's own batch, which the JSON journal format carries as the chunk's base64.
        var chunk = "cancelled"u8.ToArray();
        var encoded = Encoding.ASCII.GetBytes(Convert.ToBase64String(chunk));
        bool Carries(object? call) => call is LedgerReplicaWriteRequest { Recovery: false } request && request.Payload.ToArray().AsSpan().IndexOf(encoded) >= 0;
        _state.HeldLedger = ledger.Id;
        var held = _state.ReplicaWrites.Hold(arrives: Carries, holds: static call => call is LedgerReplicaWriteRequest { Recovery: false });
        using var cancel = new CancellationTokenSource();
        var cancelled = placed.AppendUntilCancelledAsync(chunk, cancel.Token);
        await Bounded(held, "the append's replica writes to be held");
        await cancel.CancelAsync();
        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.WaitAsync(Patience, Token));

        // The old primary's replica takes no part in the Replace's seal, so the successor's prefix lacks the entry, and the
        // old writer answers it Fenced once the map records the ledger's end. A call holds the old activation, so its storage
        // resends the entry to the new primary, which holds it until the successor's replica writes are held too.
        var journal = JournalId.FromGrainId(placed.GetGrainId()).Value;
        _ = _state.TailReads.Hold(old.Name);
        _ = _state.ReplicaReads.Hold(old.Name);
        var forwarded = _state.ForwardedWrites.Hold(moved.Name, holds: call => call is JournalForwardedEntry entry && entry.Key == journal);
        var busy = _state.SiloReplies.Hold(old.Name);
        var staying = placed.SiloAsync();
        await Bounded(busy, "a call to hold the old activation");
        await ReplaceAsync(cluster, ledger, target);
        _state.TailReads.Release();
        _state.ReplicaReads.Release();
        await Bounded(forwarded, "the old activation's storage to resend the append to the new primary");
        var successor = (await ReadMapAsync()).Ledgers.Single(open => open.IsOpen && open.Range.Equals(ledger.Range));
        _state.HeldLedger = successor.Id;
        _state.ReplicaWrites.Release();
        var resent = _state.ReplicaWrites.Hold(arrives: Carries, holds: Carries);
        _state.ForwardedWrites.Release();
        await Bounded(resent, "the resent append to be admitted on the new primary");
        _state.SiloReplies.Release();
        Assert.Equal(old.SiloAddress, await Bounded(staying, "the held call"));
        await EventuallyAsync(async () => (await GrainSilosAsync(cluster, placed.GetGrainId())).SequenceEqual([moved.SiloAddress]), $"the grain to migrate to {moved.Name}");
        Assert.True(Services(old).GetRequiredService<JournalReplacement>().Migrations >= 1, "The old primary's silo asked no migration.");
        Assert.Equal([1], await placed.ReadAsync());

        // The resent entry lands after the new activation read, so that activation's write meets the changed content.
        _state.ReplicaWrites.Release();
        byte[] landed = [1, .. chunk];
        var router = Services(moved).GetRequiredService<JournalRouter>();
        await EventuallyAsync(async () => await ContentAsync(router, journal) is { } content && content.AsSpan().IndexOf(encoded) >= 0, "the resent append to land");
        var refused = await placed.ActivationAsync();
        _ = await Assert.ThrowsAsync<InconsistentStateException>(() => placed.AppendAsync([3]));

        // Orleans deactivates an activation whose call failed with InconsistentStateException, failing the calls which reach
        // it meanwhile with that exception; the next activation reads the journal.
        await EventuallyAsync(async () => await NewActivationAsync(placed, refused), "a new activation of the grain");
        Assert.Equal(landed, await placed.ReadAsync());
        Assert.Equal(3, await placed.AppendAsync([3]));
        Assert.Equal([.. landed, 3], await ReadJournalAsync(placed));
    }

    /// <summary>Returns whether a call to <paramref name="grain"/> reaches an activation other than <paramref name="activation"/>.</summary>
    private static async Task<bool> NewActivationAsync(IChunksGrain grain, Guid activation)
    {
        try
        {
            return await grain.ActivationAsync() != activation;
        }
        catch (InconsistentStateException)
        {
            return false;
        }
    }
    /// <summary>Returns the journal <paramref name="journal"/>'s raw content, read through <paramref name="router"/>.</summary>
    private static async Task<byte[]> ContentAsync(JournalRouter router, string journal)
    {
        var read = await router.ReadAsync(journal, JournalRowKey.Encode(journal), Token);
        using var content = read.Content;
        var slice = content.Reader.PeekSlice(content.Length);
        try
        {
            return slice.AsReadOnlySequence().ToArray();
        }
        finally
        {
            slice.Dispose();
        }
    }
    /// <summary>
    /// orleans.md §5.4, OR-I7, under load: clients append a distinct chunk to each of several <c>[JournalPlacement]</c> grains
    /// in turn, each call through another silo's grain factory, while a <c>Replace</c> moves their journals' ledger to another
    /// node. Every grain migrates to the new primary's silo, keeps being written, and its content holds each chunk whose
    /// append was acknowledged exactly once, in the order the acknowledgments came, and otherwise only chunks whose appends
    /// failed, each at most once: a write whose outcome is unknown may have landed. The old primary's silo asked each to
    /// migrate, and the content is read from each grain's journal by a new activation.
    /// </summary>
    [Fact(Explicit = true, Timeout = 180_000)]
    [Trait("Category", EndToEnd)]
    public async Task JournalPlacedGrainsFollowTheirPrimaryThroughAHandOffUnderLoad()
    {
        var cluster = await DeployAsync(4, TestContext.Current.CancellationToken);
        var map = await BalancedAndPausedAsync(cluster);
        await JournalsServeAsync(cluster);
        var ledger = map.Ledgers.First(open => open.IsOpen && open.Primary.Node != Nodes(cluster).Single(entry => entry.Handle == cluster.Primary).Node);
        string[] keys = [.. Enumerable.Range(0, 4).Select(index => PlacedKey(cluster, ledger, string.Create(CultureInfo.InvariantCulture, $"loaded-{index}")))];
        var (target, _, moved) = Nodes(cluster).First(entry => entry.Node != ledger.Primary.Node && entry.Handle != cluster.Primary);
        var factories = cluster.Silos.Select(static silo => Services(silo).GetRequiredService<IGrainFactory>()).ToArray();
        var acknowledged = keys.ToDictionary(static key => key, static _ => new ConcurrentQueue<byte[]>());
        var failed = keys.ToDictionary(static key => key, static _ => new ConcurrentQueue<byte[]>());
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var clients = keys.Select(key => Task.Run(async () =>
        {
            for (var operation = 0; !stop.IsCancellationRequested; operation++)
            {
                var chunk = Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{key}#{operation};"));
                try
                {
                    _ = await factories[operation % factories.Length].GetGrain<IPlacedJournaledChunksGrain>(key).AppendAsync(chunk);
                    acknowledged[key].Enqueue(chunk);
                }
                catch (Exception)
                {
                    failed[key].Enqueue(chunk);
                }

                await Task.Delay(TimeSpan.FromMilliseconds(10), TimeProvider.System, CancellationToken.None);
            }
        })).ToArray();
        await EventuallyAsync(() => keys.All(key => acknowledged[key].Count >= 3), "every placed grain to accept appends");

        await ReplaceAsync(cluster, ledger, target);
        await EventuallyAsync(async () => (await Task.WhenAll(keys.Select(key => GrainSilosAsync(cluster, cluster.Client.GetGrain<IPlacedJournaledChunksGrain>(key).GetGrainId())))).All(silos => silos.SequenceEqual([moved.SiloAddress])), $"every placed grain to migrate to {moved.Name}");
        var after = keys.ToDictionary(key => key, key => acknowledged[key].Count);
        await EventuallyAsync(() => keys.All(key => acknowledged[key].Count >= after[key] + 3), "every placed grain to accept appends on its new primary");
        await stop.CancelAsync();
        await Bounded(Task.WhenAll(clients), "the clients' stop");
        var (_, _, old) = Nodes(cluster).Single(entry => entry.Node == ledger.Primary.Node);
        Assert.True(Services(old).GetRequiredService<JournalReplacement>().Migrations >= 1, "The old primary's silo asked no migration.");

        foreach (var key in keys)
        {
            var grain = cluster.Client.GetGrain<IPlacedJournaledChunksGrain>(key);
            Assert.Equal(moved.SiloAddress, await grain.SiloAsync());
            var content = Encoding.UTF8.GetString(await ReadJournalAsync(grain));
            string[] chunks = [.. content.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(static chunk => chunk + ";")];
            Assert.Equal(chunks.Length, chunks.Distinct(StringComparer.Ordinal).Count());
            string[] expected = [.. acknowledged[key].Select(Encoding.UTF8.GetString)];
            Assert.Equal(expected, chunks.Where(expected.Contains));
            var unknown = failed[key].Select(Encoding.UTF8.GetString).ToHashSet(StringComparer.Ordinal);
            Assert.All(chunks.Where(chunk => !expected.Contains(chunk)), chunk => Assert.Contains(chunk, unknown));
        }
    }

    /// <summary>
    /// OR-I24, orleans.md §5.2: <see cref="INodeControlGrain.PrepareReleaseAsync"/> drains an in-flight grain append on the
    /// outgoing primary before stopping new admissions on that open ledger, answers repeated calls idempotently with the same
    /// <see cref="PrepareReleaseReply.Released"/> tail, and hands off cleanly to a cooperative
    /// <see cref="Transition.Replace"/> whose successor preserves every committed entry.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task CooperativePrepareReleaseFlushesInFlightAppendAndIsIdempotentBeforeReplace()
    {
        var cluster = await DeployAsync(4, TestContext.Current.CancellationToken);
        var map = await BalancedAndPausedAsync(cluster);
        await JournalsServeAsync(cluster);
        var (key, ledger) = PlacedKey(cluster, map, "cooperative");
        var (oldNode, _, old) = Nodes(cluster).Single(entry => entry.Node == ledger.Primary.Node);
        var (target, _, moved) = Nodes(cluster).First(entry => entry.Node != ledger.Primary.Node);
        var placed = cluster.Client.GetGrain<IPlacedJournaledChunksGrain>(key);
        Assert.Equal(1, await placed.AppendAsync([1]));
        Assert.Equal(old.SiloAddress, await placed.SiloAsync());

        // Hold the next append's replica writes so it is genuinely in flight when PrepareRelease arrives on the outgoing primary.
        var chunk = "inflight-coop"u8.ToArray();
        var encoded = Encoding.ASCII.GetBytes(Convert.ToBase64String(chunk));
        bool Carries(object? call) => call is LedgerReplicaWriteRequest { Recovery: false } request && request.Payload.ToArray().AsSpan().IndexOf(encoded) >= 0;
        _state.HeldLedger = ledger.Id;
        var held = _state.ReplicaWrites.Hold(arrives: Carries, holds: static call => call is LedgerReplicaWriteRequest { Recovery: false });
        var inFlight = placed.AppendAsync(chunk);
        await Bounded(held, "the in-flight append's replica writes to be held");

        _ = _state.ReportCalls.Hold();
        var prepareRequest = new PrepareReleaseRequest(ledger.Id, ledger.Range, TimeSpan.FromSeconds(10));
        var prepareTask = NodeControl(cluster, oldNode).PrepareReleaseAsync(prepareRequest);
        await Task.Delay(TimeSpan.FromMilliseconds(50), TimeProvider.System, Token);
        Assert.False(prepareTask.IsCompleted, "PrepareRelease should wait for the in-flight append to flush before releasing.");

        // Unblock the replica writes: the in-flight append flushes and completes, then PrepareRelease returns Released(tail).
        _state.ReplicaWrites.Release();
        Assert.Equal(2, await Bounded(inFlight, "the in-flight append to finish during cooperative drain"));
        var released = Assert.IsType<PrepareReleaseReply.Released>((await Bounded(prepareTask, "PrepareRelease")).Value);
        Assert.True(released.ContiguousTail > Slot.Zero, $"Expected positive contiguous tail after flushing in-flight append, got {released.ContiguousTail}.");

        // Repeated PrepareRelease on the same open ledger is idempotent and returns the same Released tail.
        var repeated = Assert.IsType<PrepareReleaseReply.Released>((await Bounded(NodeControl(cluster, oldNode).PrepareReleaseAsync(prepareRequest), "repeated PrepareRelease")).Value);
        Assert.Equal(released.ContiguousTail, repeated.ContiguousTail);

        // The outgoing primary no longer admits new writes on the released open ledger.
        var journal = JournalId.FromGrainId(placed.GetGrainId()).Value;
        var rejected = await Bounded(ForwardAsync(JournalNode(cluster, oldNode), journal, JournalVersion.Origin, [9]), "post-release write on outgoing primary");
        _ = Assert.IsType<JournalMoved>(rejected.Value);

        // Execute the cooperative Replace on the target node and verify the grain migrates with all entries intact.
        var choice = await ChoiceAsync(cluster, target);
        _ = Assert.IsType<LedgerMapCommitOutcome.Applied>((await Bounded(NodeControl(cluster, target).ExecuteAsync(new Transition.Replace(ledger.Id, choice, PredecessorReleaseHint.Cooperative)), "cooperative Replace")).Value);
        _state.ReportCalls.Release();
        _ = await EverySiloFollowsTheMapAsync(cluster);

        await EventuallyAsync(async () => (await GrainSilosAsync(cluster, placed.GetGrainId())).SequenceEqual([moved.SiloAddress]), $"the placed grain to migrate to {moved.Name}");
        Assert.Equal([1, .. chunk], await ReadJournalAsync(placed));
        Assert.Equal(3, await placed.AppendAsync([3]));
        Assert.Equal([1, .. chunk, 3], await ReadJournalAsync(placed));
    }

    /// <summary>
    /// Deactivates <paramref name="grain"/>'s activation and returns what a new one reads from its journal, so the content is
    /// the journal's, not an activation's memory.
    /// </summary>
    private static async Task<byte[]> ReadJournalAsync(IChunksGrain grain)
    {
        var activation = await grain.ActivationAsync();
        await grain.DeactivateAsync();
        await EventuallyAsync(async () => await grain.ActivationAsync() != activation, "a new activation of the grain");
        return await grain.ReadAsync();
    }

    /// <summary>Returns the silos which hold an activation of <paramref name="grain"/>.</summary>
    private static async Task<SiloAddress[]> GrainSilosAsync(TestCluster cluster, GrainId grain)
    {
        var statistics = await cluster.Client.GetGrain<IManagementGrain>(0).GetDetailedGrainStatistics();
        return [.. statistics.Where(statistic => statistic.GrainId == grain).Select(static statistic => statistic.SiloAddress).Distinct()];
    }

    /// <summary>
    /// Returns a key of a <see cref="IPlacedJournaledChunksGrain"/> whose journal lies in an open ledger of
    /// <paramref name="map"/> whose primary's silo is not the cluster's primary silo, and that ledger.
    /// </summary>
    private static (string Key, LedgerMetadata Ledger) PlacedKey(TestCluster cluster, LedgerMapValue map, string prefix)
    {
        var primary = Nodes(cluster).Single(entry => entry.Handle == cluster.Primary).Node;
        var ledger = map.Ledgers.First(open => open.IsOpen && open.Primary.Node != primary);
        return (PlacedKey(cluster, ledger, prefix), ledger);
    }

    /// <summary>Returns a key of a <see cref="IPlacedJournaledChunksGrain"/> named from <paramref name="prefix"/> whose journal <paramref name="ledger"/>'s range holds.</summary>
    private static string PlacedKey(TestCluster cluster, LedgerMetadata ledger, string prefix) =>
        KeyIn(ledger, prefix, key => cluster.Client.GetGrain<IPlacedJournaledChunksGrain>(key).GetGrainId());

    /// <summary>Returns a grain key named from <paramref name="prefix"/> whose grain, as <paramref name="grain"/> names it, has its journal in <paramref name="ledger"/>'s range.</summary>
    private static string KeyIn(LedgerMetadata ledger, string prefix, Func<string, GrainId> grain) =>
        Enumerable.Range(0, int.MaxValue)
            .Select(index => string.Create(CultureInfo.InvariantCulture, $"{prefix}-{index}"))
            .First(key => ledger.Range.Contains(JournalRowKey.Encode(JournalId.FromGrainId(grain(key)).Value).Span));

    /// <summary>
    /// Executes a <c>Replace</c> of <paramref name="ledger"/> by <paramref name="executor"/>, which becomes its successor's
    /// primary, while the controller grain hears no report, so it sends no takeover of its own; waits until every silo
    /// follows the map.
    /// </summary>
    private async Task ReplaceAsync(TestCluster cluster, LedgerMetadata ledger, NodeId executor)
    {
        _ = _state.ReportCalls.Hold();
        var choice = await ChoiceAsync(cluster, executor);
        _ = Assert.IsType<LedgerMapCommitOutcome.Applied>((await NodeControl(cluster, executor).ExecuteAsync(new Transition.Replace(ledger.Id, choice))).Value);
        _state.ReportCalls.Release();
        _ = await EverySiloFollowsTheMapAsync(cluster);
    }
}
