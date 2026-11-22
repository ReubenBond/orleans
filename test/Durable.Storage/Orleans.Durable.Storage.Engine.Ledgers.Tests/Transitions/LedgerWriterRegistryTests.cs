using Microsoft.Extensions.Logging;
using Orleans.Durable.Storage.Engine.Ledgers.Control;
using Orleans.Durable.Storage.Testing;
using static Orleans.Durable.Storage.Engine.Ledgers.Tests.Transitions.TransitionCluster;

namespace Orleans.Durable.Storage.Engine.Ledgers.Tests.Transitions;

/// <summary>
/// The registry (ledgers.md §2.4, §8.3, LP-I16): a stopped lifetime held unserved until the view decides its ledger, a
/// writer leaving once the view lists the ledger closed or not at all, a close racing the writer's disposal in either
/// order, the unserved ledgers it lists and signals, the lifetimes it stops once their change can no longer apply, and
/// a writer it cannot create or whose lifetime already stopped, which leaves the follower going.
/// </summary>
public sealed class LedgerWriterRegistryTests : IDisposable
{
    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    public enum CloseOrder
    {
        /// <summary>The writer's own map follower decides its stopped append before the registry disposes it.</summary>
        FollowerFirst,

        /// <summary>The registry disposes the writer before its append meets the seal.</summary>
        DisposalFirst,
    }

    public enum Precluded
    {
        /// <summary>The view lists a ledger the change closes already closed.</summary>
        ClosesClosed,

        /// <summary>The change is a bootstrap, and the view lists an open ledger.</summary>
        BootstrapWithAnOpenLedger,

        /// <summary>The view's retirement floor lies above the change's <c>ChosenAt</c>.</summary>
        FloorAboveChosenAt,

        /// <summary>The view lists the lifetime's claim already closed.</summary>
        ClaimListedClosed,
    }

    public void Dispose() => _leaks.Dispose();

    /// <summary>
    /// A writer whose append met a seal stays held while the view lists its ledger open: unserved, with its stopped
    /// append waiting for <c>T</c>. Once the view lists the ledger closed, the append is decided and the writer leaves.
    /// </summary>
    [Fact]
    public async Task AStoppedLifetimeIsHeldUnservedUntilTheViewListsTheLedgerClosed()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var cluster = new TransitionCluster();
        var a = await cluster.BootstrapAsync(Nodes[0], cancellationToken);
        var root = a.Open(KeyRange.Root);
        var writer = a.Registry.TryGet(root.Id)!;
        var b = await cluster.StartAsync(Nodes[1], cancellationToken);
        using (var lease = b.Reader.LeaseLedger(root))
        {
            _ = await lease.Ledger.RecoverAsync(cancellationToken);
        }

        var stopped = ProposeAsync(writer, 0x10, cancellationToken);
        await writer.Fenced.WaitAsync(Bound, cancellationToken);

        Assert.Equal([root.Id], await a.Registry.WaitForUnservedAsync(NoneKnown, cancellationToken).WaitAsync(Bound, cancellationToken));
        Assert.Null(a.Registry.TryGet(root.Id));
        Assert.Same(writer, a.Registry.HeldWriter(writer.Ledger));
        Assert.False(stopped.IsCompleted);

        Assert.Equal(Applied(2), await b.ExecuteAsync(cluster, new Transition.Replace(root.Id, Choice(Nodes[1], 1)), cancellationToken));
        _ = await a.View.ReadAsync(cancellationToken);

        Assert.Equal(Fenced, await stopped.WaitAsync(Bound, cancellationToken));
        await WaitUntilAsync(() => a.Registry.HeldWriter(writer.Ledger) is null, "the writer leaves");
        Assert.Empty(a.Registry.Unserved);
    }

    /// <summary>
    /// <c>TryGet</c>'s guarantee per ledger: <see langword="null"/> until the registry adopts the ledger's one lifetime,
    /// then that writer, then <see langword="null"/> forever once the view lists the ledger closed or the lifetime stops;
    /// never a second writer. A watcher reads both ledgers of a replace, and then the successor's stop, throughout.
    /// </summary>
    [Fact]
    public async Task TryGetAnswersOneWriterPerLedgerAndThenNullForever()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var cluster = new TransitionCluster();
        var a = await cluster.BootstrapAsync(Nodes[0], cancellationToken);
        var root = a.Open(KeyRange.Root);
        var successor = a.View.Current.NextLedgerId;
        var observed = new List<(LedgerWriter? Root, LedgerWriter? Successor)>();
        var observing = new Lock();
        void Observe()
        {
            lock (observing)
            {
                observed.Add((a.Registry.TryGet(root.Id), a.Registry.TryGet(successor)));
            }
        }

        using var watching = new CancellationTokenSource();
        Observe();
        var watcher = Task.Run(
            async () =>
            {
                while (!watching.IsCancellationRequested)
                {
                    Observe();
                    await Task.Yield();
                }
            },
            cancellationToken);

        Assert.Equal(Applied(2), await a.ExecuteAsync(cluster, new Transition.Replace(root.Id, Choice(Nodes[0], 1)), cancellationToken));
        await WaitUntilAsync(() => a.Registry.TryGet(successor) is not null, "the registry adopts the successor");
        Observe();
        a.Registry.TryGet(successor)!.Abandon();
        await WaitUntilAsync(() => a.Registry.TryGet(successor) is null, "the registry stops serving the stopped lifetime");
        await watching.CancelAsync();
        await watcher;
        Observe();

        AssertOneWriterThenNull([.. observed.Select(static pair => pair.Root)], initiallyServed: true);
        AssertOneWriterThenNull([.. observed.Select(static pair => pair.Successor)], initiallyServed: false);
        Assert.Null(observed[^1].Root);
        Assert.Null(observed[^1].Successor);
    }

    /// <summary>
    /// The registry disposes a writer once its view lists the ledger closed, while the writer's own follower decides its
    /// stopped appends from the same view. Each order is forced: the registry's follower is held while it opens a ledger
    /// the same view lists, so the writer's follower decides first; or the append's write is held until the registry
    /// disposes the writer, so the disposal decides it. Either way the append is decided from <c>T</c>, never failed by
    /// the disposal.
    /// </summary>
    [Theory]
    [InlineData(CloseOrder.FollowerFirst)]
    [InlineData(CloseOrder.DisposalFirst)]
    public async Task ACloseRacingTheWritersDisposalIsDecidedFromTheView(CloseOrder order)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var cluster = new TransitionCluster();
        var a = await cluster.BootstrapAsync(Nodes[0], cancellationToken);
        var root = a.Open(KeyRange.Root);
        var writer = a.Registry.TryGet(root.Id)!;
        Assert.Equal(DurableAt(0), await ProposeAsync(writer, 0x10, cancellationToken));
        var b = await cluster.StartAsync(Nodes[1], cancellationToken);
        Task<ProposalOutcome> stopped;
        if (order == CloseOrder.FollowerFirst)
        {
            Slot end;
            using (var lease = b.Reader.LeaseLedger(root))
            {
                end = (await lease.Ledger.RecoverAsync(cancellationToken)).End;
            }

            stopped = ProposeAsync(writer, 0x10, cancellationToken);
            await writer.Fenced.WaitAsync(Bound, cancellationToken);

            // The view which closes the root also lists a successor under a lifetime this process holds, whose opening the
            // test holds, with the registry's follower inside its reconciliation.
            var successor = a.View.Current.NextLedgerId;
            using var opened = new ManualResetEventSlim();
            var opening = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            a.Hooks.Open = (node, ledger) =>
            {
                if (ledger == successor)
                {
                    _ = opening.TrySetResult();
                    opened.Wait(cancellationToken);
                }
            };
            var lifetime = new Sequencer(Window);
            a.Registry.Hold(lifetime, [root.Id], [KeyRange.Root], 0);
            var install = new LedgerMapChange.Install([new LedgerClosing(root.Id, end)], [new LedgerOpening(KeyRange.Root, Nodes, Window, new PrimaryClaim(Nodes[0], lifetime.InstanceId))], 0);
            Assert.Equal(Applied(2), await cluster.Committer.CommitAsync(install, cancellationToken));
            _ = await a.View.ReadAsync(cancellationToken);
            await opening.Task.WaitAsync(Bound, cancellationToken);

            Assert.Equal(Fenced, await stopped.WaitAsync(Bound, cancellationToken));
            Assert.Same(writer, a.Registry.HeldWriter(writer.Ledger));
            opened.Set();
            a.Hooks.Open = null;
            await WaitUntilAsync(() => a.Registry.TryGet(successor) is not null, "the registry adopts the successor");
        }
        else
        {
            var writing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            a.Hooks.Write = async (call, inner) =>
            {
                if (call.Slot == new Slot(1) && !call.Recovery)
                {
                    _ = writing.TrySetResult();
                    await released.Task.WaitAsync(call.CancellationToken);
                }

                return await inner();
            };
            stopped = ProposeAsync(writer, 0x10, cancellationToken);
            await writing.Task.WaitAsync(Bound, cancellationToken);
            Assert.Equal(Applied(2), await b.ExecuteAsync(cluster, new Transition.Replace(root.Id, Choice(Nodes[1], 1)), cancellationToken));
            Assert.False(writer.Fenced.IsCompleted);

            _ = await a.View.ReadAsync(cancellationToken);
            await WaitUntilAsync(() => a.Registry.HeldWriter(writer.Ledger) is null, "the registry lets the writer go");
            released.SetResult();

            Assert.Equal(Fenced, await stopped.WaitAsync(Bound, cancellationToken));
        }

        await WaitUntilAsync(() => a.Registry.HeldWriter(writer.Ledger) is null, "the writer leaves");
        Assert.Equal(new Slot(1), writer.Ledger.KnownContiguousTail);
        Assert.True(writer.Fenced.IsCompleted);
    }

    /// <summary>
    /// ledger-protocol.md §6.3, #295: a disposal of the writer which races the registry's, begun once its view lists the
    /// ledger closed, completes only once that one disposal has ended, so its caller sees the writer's last segment given.
    /// The registry's disposal is held inside the segment's sink, which it calls under the writer's lock.
    /// </summary>
    [Fact]
    public async Task ADisposalWhichRacesTheRegistrysCompletesOnceThatDisposalEnds()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var cluster = new TransitionCluster();
        var a = await cluster.BootstrapAsync(Nodes[0], cancellationToken);
        var root = a.Open(KeyRange.Root);
        var writer = a.Registry.TryGet(root.Id)!;
        Assert.Equal(DurableAt(0), await ProposeAsync(writer, 0x10, cancellationToken));
        var given = new List<LedgerSegmentKeys>();
        using var giving = new ManualResetEventSlim();
        using var released = new ManualResetEventSlim();
        a.OnSegmentEnded = keys =>
        {
            lock (given)
            {
                given.Add(keys);
            }

            giving.Set();
            released.Wait(cancellationToken);
        };
        var b = await cluster.StartAsync(Nodes[1], cancellationToken);
        Assert.Equal(Applied(2), await b.ExecuteAsync(cluster, new Transition.Replace(root.Id, Choice(Nodes[1], 1)), cancellationToken));
        _ = await a.View.ReadAsync(cancellationToken);
        Assert.True(giving.Wait(Bound, cancellationToken), "the registry's disposal gives the writer's segment");

        var racing = writer.DisposeAsync().AsTask();
        try
        {
            Assert.False(racing.IsCompleted);
        }
        finally
        {
            released.Set();
        }

        await racing.WaitAsync(Bound, cancellationToken);
        lock (given)
        {
            Assert.Equal([new LedgerSegmentKeys(new LedgerSegmentId(root.Id, Slot.Zero), 1)], given);
        }
    }

    /// <summary>
    /// ledgers.md §2.4: an idle primary, which made no proposal and no open check after its last durable one, learns that
    /// another process closed its ledger once its registry's view lists it closed: its writer leaves, reporting
    /// <see cref="LedgerWriter.Fenced"/>, and its <see cref="Ledger"/> learns the recorded <c>T</c>.
    /// </summary>
    [Fact]
    public async Task AnIdlePrimaryWhoseLedgerAnotherProcessClosesIsFenced()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var cluster = new TransitionCluster();
        var a = await cluster.BootstrapAsync(Nodes[0], cancellationToken);
        var root = a.Open(KeyRange.Root);
        var writer = a.Registry.TryGet(root.Id)!;
        Assert.Equal(DurableAt(0), await ProposeAsync(writer, 0x10, cancellationToken));
        var b = await cluster.StartAsync(Nodes[1], cancellationToken);
        Assert.Equal(Applied(2), await b.ExecuteAsync(cluster, new Transition.Replace(root.Id, Choice(Nodes[1], 1)), cancellationToken));
        Assert.False(writer.Fenced.IsCompleted);

        _ = await a.View.ReadAsync(cancellationToken);

        await writer.Fenced.WaitAsync(Bound, cancellationToken);
        await WaitUntilAsync(() => a.Registry.HeldWriter(writer.Ledger) is null, "the writer leaves");
        Assert.Equal(new Slot(1), writer.Ledger.KnownContiguousTail);
    }

    /// <summary>
    /// The registry lists an open ledger the view names this node's primary of under a claim of an earlier process of the
    /// node, and signals the addition; a wait which already knows it waits for another.
    /// </summary>
    [Fact]
    public async Task UnservedListsAnEarlierProcesssClaimAndSignalsIt()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var cluster = new TransitionCluster();
        var a = await cluster.StartAsync(Nodes[0], cancellationToken);
        var waiting = a.Registry.WaitForUnservedAsync(NoneKnown, cancellationToken);
        var earlier = new LedgerMetadata(new LedgerId(1), KeyRange.Root, Nodes, Window, new PrimaryClaim(Nodes[0], new SequencerInstanceId(Guid.CreateVersion7())), LedgerStatus.Open.Instance);
        cluster.Write(new LedgerMapValue(1, new LedgerId(2), 0, [earlier]));
        _ = await a.View.ReadAsync(cancellationToken);

        Assert.Equal([earlier.Id], await waiting.WaitAsync(Bound, cancellationToken));
        Assert.Null(a.Registry.TryGet(earlier.Id));
        var known = a.Registry.WaitForUnservedAsync(Known(earlier.Id), cancellationToken);
        Assert.False(known.IsCompleted);
    }

    /// <summary>
    /// ledgers.md §2.6: a held lifetime whose change can no longer apply stops without serving: a view which no longer
    /// lists open a ledger the change closes, which lists an open ledger for a bootstrap, or whose floor exceeds the
    /// change's <c>ChosenAt</c>, and one which lists the lifetime's claim already closed.
    /// </summary>
    [Theory]
    [InlineData(Precluded.ClosesClosed)]
    [InlineData(Precluded.BootstrapWithAnOpenLedger)]
    [InlineData(Precluded.FloorAboveChosenAt)]
    [InlineData(Precluded.ClaimListedClosed)]
    public async Task AHeldLifetimeStopsOnceItsChangeCanNoLongerApply(Precluded precluded)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var cluster = new TransitionCluster();
        var a = await cluster.BootstrapAsync(Nodes[0], cancellationToken);
        var root = a.Open(KeyRange.Root);
        var b = await cluster.StartAsync(Nodes[1], cancellationToken);
        var lifetime = new Sequencer(Window);
        b.Registry.Hold(lifetime, precluded == Precluded.BootstrapWithAnOpenLedger ? [] : [root.Id], [KeyRange.Root], chosenAt: 1);

        switch (precluded)
        {
            case Precluded.ClosesClosed:
                Assert.Equal(Applied(2), await a.ExecuteAsync(cluster, new Transition.Replace(root.Id, Choice(Nodes[0], 1)), cancellationToken));
                break;
            case Precluded.FloorAboveChosenAt:
                _ = await cluster.Committer.CommitAsync(new LedgerMapChange.RaiseFloor(2), cancellationToken);
                break;
            case Precluded.ClaimListedClosed:
                var opened = new LedgerOpening(KeyRange.Root, Nodes, Window, new PrimaryClaim(Nodes[1], lifetime.InstanceId));
                _ = await cluster.Committer.CommitAsync(new LedgerMapChange.Install([new LedgerClosing(root.Id, Slot.Zero)], [opened], 1), cancellationToken);
                _ = await cluster.Committer.CommitAsync(new LedgerMapChange.Install([new LedgerClosing(new LedgerId(2), Slot.Zero)], [new LedgerOpening(KeyRange.Root, Nodes, Window, new PrimaryClaim(Nodes[2], new SequencerInstanceId(Guid.CreateVersion7())))], 1), cancellationToken);
                break;
            case Precluded.BootstrapWithAnOpenLedger:
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(precluded));
        }

        _ = await b.View.ReadAsync(cancellationToken);
        await WaitUntilAsync(() => b.Registry.PendingLifetimes == 0, "the registry stops the lifetime");
        Assert.Equal(SequencerStopReason.Requested, await lifetime.Stopped.WaitAsync(Bound, cancellationToken));
        Assert.Empty(b.Registry.Unserved);
        Assert.All(b.View.Current.Ledgers, ledger => Assert.Null(b.HeldWriter(ledger.Id)));
    }

    /// <summary>
    /// ledgers.md §8.3: a writer the registry cannot create, here over a lifetime whose window is not its ledger's, is
    /// contained to its ledger: the lifetime stops, the ledger is unserved, the failure is logged as an error, and the
    /// follower goes on, serving the other ledger the same view lists, and adopting a later claim. Disposal then disposes
    /// every writer.
    /// </summary>
    [Fact]
    public async Task AWriterWhichCannotBeCreatedLeavesItsLedgerUnservedAndTheFollowerGoesOn()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var cluster = new TransitionCluster();
        var b = await cluster.BootstrapAsync(Nodes[1], cancellationToken);
        var root = b.Open(KeyRange.Root);
        Slot end;
        using (var lease = b.Reader.LeaseLedger(root))
        {
            end = (await lease.Ledger.RecoverAsync(cancellationToken)).End;
        }

        var (lower, upper) = KeyRange.Root.Children;
        var refused = new Sequencer(Window + 1);
        var served = new Sequencer(Window);
        b.Registry.Hold(refused, [root.Id], [lower, upper], 0);
        b.Registry.Hold(served, [root.Id], [lower, upper], 0);
        var split = new LedgerMapChange.Install(
            [new LedgerClosing(root.Id, end)],
            [new LedgerOpening(lower, Nodes, Window, new PrimaryClaim(Nodes[1], refused.InstanceId)), new LedgerOpening(upper, Nodes, Window, new PrimaryClaim(Nodes[1], served.InstanceId))],
            0);
        Assert.Equal(Applied(2), await cluster.Committer.CommitAsync(split, cancellationToken));

        _ = await b.View.ReadAsync(cancellationToken);

        var lowerLedger = b.Open(lower);
        Assert.Equal([lowerLedger.Id], await b.Registry.WaitForUnservedAsync(Known(root.Id), cancellationToken).WaitAsync(Bound, cancellationToken));
        Assert.Equal(SequencerStopReason.Requested, await refused.Stopped.WaitAsync(Bound, cancellationToken));
        var upperWriter = b.Registry.TryGet(b.Open(upper).Id);
        Assert.NotNull(upperWriter);
        Assert.Equal(DurableAt(0), await ProposeAsync(upperWriter, 0x90, cancellationToken));

        // The follower logs the failure once it leaves its lock, after it lists the ledger unserved.
        await WaitUntilAsync(() => b.Logs.From<LedgerWriterRegistry>().Count > 0, "the registry logs the failure");
        var failure = Assert.Single(b.Logs.From<LedgerWriterRegistry>());
        Assert.Equal(LogLevel.Error, failure.Level);
        Assert.IsType<ArgumentException>(failure.Exception);
        Assert.Contains(lowerLedger.Id.ToString(), failure.Message, StringComparison.Ordinal);

        Assert.Equal(Applied(3), await b.ExecuteAsync(cluster, new Transition.Replace(lowerLedger.Id, Choice(Nodes[1], 2)), cancellationToken));
        Assert.NotNull(b.Registry.TryGet(b.Open(lower).Id));
        Assert.Empty(b.Registry.Unserved);

        await b.Registry.DisposeAsync();
        Assert.True(upperWriter.Sequencer.IsStopped);
        _ = await Assert.ThrowsAsync<ObjectDisposedException>(() => ProposeAsync(upperWriter, 0x90, cancellationToken));
    }

    /// <summary>
    /// LP-I14: a lifetime adopted once this process already recovered its ledger, before the view listed the claim, is
    /// stopped at adoption without failing the reconciliation: its writer is held, unserved, and the follower goes on to
    /// adopt a later claim, after which the stopped writer leaves.
    /// </summary>
    [Fact]
    public async Task ALifetimeStoppedAtItsAdoptionIsHeldUnserved()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var cluster = new TransitionCluster();
        var a = await cluster.BootstrapAsync(Nodes[0], cancellationToken);
        var root = a.Open(KeyRange.Root);
        var b = await cluster.StartAsync(Nodes[1], cancellationToken);
        var lifetime = new Sequencer(Window);
        b.Registry.Hold(lifetime, [root.Id], [KeyRange.Root], 0);
        var claimed = new LedgerMetadata(b.View.Current.NextLedgerId, KeyRange.Root, Nodes, Window, new PrimaryClaim(Nodes[1], lifetime.InstanceId), LedgerStatus.Open.Instance);
        using var early = b.Reader.LeaseLedger(claimed);
        _ = await early.Ledger.RecoverAsync(cancellationToken);
        Slot end;
        using (var lease = b.Reader.LeaseLedger(root))
        {
            end = (await lease.Ledger.RecoverAsync(cancellationToken)).End;
        }

        var replace = new LedgerMapChange.Install([new LedgerClosing(root.Id, end)], [new LedgerOpening(KeyRange.Root, Nodes, Window, claimed.Primary)], 0);
        Assert.Equal(Applied(2), await cluster.Committer.CommitAsync(replace, cancellationToken));
        _ = await b.View.ReadAsync(cancellationToken);

        Assert.Equal([claimed.Id], await b.Registry.WaitForUnservedAsync(NoneKnown, cancellationToken).WaitAsync(Bound, cancellationToken));
        Assert.Equal(SequencerStopReason.Sealing, await lifetime.Stopped.WaitAsync(Bound, cancellationToken));
        var stopped = b.Registry.HeldWriter(early.Ledger);
        Assert.NotNull(stopped);
        Assert.Equal(lifetime.InstanceId, stopped.InstanceId);
        Assert.Null(b.Registry.TryGet(claimed.Id));
        Assert.Equal(0, b.Registry.PendingLifetimes);
        Assert.Empty(b.Logs.From<LedgerWriterRegistry>());

        Assert.Equal(Applied(3), await b.ExecuteAsync(cluster, new Transition.Replace(claimed.Id, Choice(Nodes[1], 2)), cancellationToken));
        Assert.NotNull(b.Registry.TryGet(b.Open(KeyRange.Root).Id));
        await WaitUntilAsync(() => b.Registry.HeldWriter(early.Ledger) is null, "the stopped writer leaves");
        Assert.Empty(b.Registry.Unserved);
    }

    /// <summary>
    /// A registry's disposal stops the lifetimes it holds and disposes its writers, whose stopped appends fail with
    /// <see cref="ObjectDisposedException"/> while the view decides nothing.
    /// </summary>
    [Fact]
    public async Task DisposalStopsHeldLifetimesAndFailsUndecidedAppends()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var cluster = new TransitionCluster();
        var a = await cluster.BootstrapAsync(Nodes[0], cancellationToken);
        var root = a.Open(KeyRange.Root);
        var writer = a.Registry.TryGet(root.Id)!;
        var b = await cluster.StartAsync(Nodes[1], cancellationToken);
        using (var lease = b.Reader.LeaseLedger(root))
        {
            _ = await lease.Ledger.RecoverAsync(cancellationToken);
        }

        var stopped = ProposeAsync(writer, 0x10, cancellationToken);
        await writer.Fenced.WaitAsync(Bound, cancellationToken);
        var pending = new Sequencer(Window);
        a.Registry.Hold(pending, [root.Id], [KeyRange.Root], chosenAt: 0);

        await a.Registry.DisposeAsync();

        _ = await Assert.ThrowsAsync<ObjectDisposedException>(() => stopped);
        Assert.True(pending.IsStopped);
        _ = Assert.Throws<ObjectDisposedException>(() => a.Registry.Hold(new Sequencer(Window), [], [KeyRange.Root], 0));
    }

    /// <summary>
    /// Checks a sequence of one ledger's <c>TryGet</c> answers: <see langword="null"/>s, then one writer, then
    /// <see langword="null"/>s, with the writer present from the start when <paramref name="initiallyServed"/>.
    /// </summary>
    private static void AssertOneWriterThenNull(LedgerWriter?[] answers, bool initiallyServed)
    {
        var first = Array.FindIndex(answers, static answer => answer is not null);
        Assert.True(first >= 0, "The registry served the ledger's writer.");
        Assert.Equal(initiallyServed, first == 0);
        var writer = answers[first];
        var last = Array.FindLastIndex(answers, static answer => answer is not null);
        for (var index = first; index <= last; index++)
        {
            Assert.True(ReferenceEquals(writer, answers[index]), $"TryGet answered {(answers[index] is null ? "null" : "another writer")} at observation {index}, between answers of one writer.");
        }
    }
}
