using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Orleans.Durable.Storage.Engine.Ledgers.Control;
using Orleans.Durable.Storage.Testing;
using static Orleans.Durable.Storage.Engine.Ledgers.Tests.Transitions.TransitionCluster;

namespace Orleans.Durable.Storage.Engine.Ledgers.Tests.Transitions;

/// <summary>
/// The transition executor (ledgers.md §2.6, §8.1): every transition through a real committer, each row of its
/// <c>commit</c> outcome table through a scripted delegate, its preconditions against a refreshed view, retries after a
/// lost reply, and the primary which stops admitting, helps a recovery, or is decided from the map.
/// </summary>
public sealed class TransitionExecutorTests : IDisposable
{
    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    public enum InvalidTransition
    {
        /// <summary>Another node than the process's executes it.</summary>
        AnotherExecutor,

        /// <summary>It closes a ledger at or above the register's <c>NextLedgerId</c>, which no map lists.</summary>
        ClosesAnUninstalledLedger,


        /// <summary>It merges open ledgers whose ranges are not siblings.</summary>
        MergeOfNonSiblings,
    }

    public enum BootstrapMap
    {
        /// <summary>The map lists no ledger.</summary>
        Empty,

        /// <summary>The map lists only closed ledgers.</summary>
        ClosedLedgersOnly,

        /// <summary>The map lists an open ledger, under an earlier process's claim.</summary>
        OpenLedger,
    }

    public enum CommitFailure
    {
        /// <summary>The change would overflow the ledger IDs: it never applies.</summary>
        Overflow,

        /// <summary>The register holds no ledger map: it never applies.</summary>
        InvalidData,

        /// <summary>The committer found a defect before it wrote: it never applies.</summary>
        InvalidOperation,

        /// <summary>The committer was disposed, possibly after it wrote: the outcome is unknown.</summary>
        Disposed,
    }

    public void Dispose() => _leaks.Dispose();

    /// <summary>
    /// An applied bootstrap opens the whole keyspace under the executor's claim, which names the lifetime its registry
    /// adopted into the ledger's writer (LP-I15), and the process serves it.
    /// </summary>
    [Fact]
    public async Task AnAppliedBootstrapIsServedUnderTheExecutorsClaim()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var cluster = new TransitionCluster();
        var a = await cluster.StartAsync(Nodes[0], cancellationToken);

        Assert.Equal(Applied(1), await a.ExecuteAsync(cluster, new Transition.Bootstrap(Choice(Nodes[0], 0)), cancellationToken));

        var root = a.Open(KeyRange.Root);
        var writer = a.Registry.TryGet(root.Id);
        Assert.NotNull(writer);
        Assert.Equal(new PrimaryClaim(Nodes[0], writer.InstanceId), root.Primary);
        Assert.Equal(DurableAt(0), await ProposeAsync(writer, 0x10, cancellationToken));
        Assert.Empty(a.Registry.Unserved);
        Assert.Equal(0, a.Registry.PendingLifetimes);
    }

    /// <summary>
    /// ledgers.md §2.6: a bootstrap's rule is the shared install rule, so it is <c>Superseded</c> exactly when the map
    /// lists an open ledger, and applies over a map which lists none or only closed ones.
    /// </summary>
    [Theory]
    [InlineData(BootstrapMap.Empty)]
    [InlineData(BootstrapMap.ClosedLedgersOnly)]
    [InlineData(BootstrapMap.OpenLedger)]
    public async Task ABootstrapIsSupersededExactlyWhenTheMapListsAnOpenLedger(BootstrapMap map)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var cluster = new TransitionCluster();
        if (map != BootstrapMap.Empty)
        {
            LedgerStatus status = map == BootstrapMap.OpenLedger ? LedgerStatus.Open.Instance : new LedgerStatus.Closed(Slot.Zero);
            var earlier = new LedgerMetadata(new LedgerId(1), KeyRange.Root, Nodes, Window, new PrimaryClaim(Nodes[1], new SequencerInstanceId(Guid.CreateVersion7())), status);
            cluster.Write(new LedgerMapValue(1, new LedgerId(2), 0, [earlier]));
        }

        var a = await cluster.StartAsync(Nodes[0], cancellationToken);
        var epoch = a.View.Current.Epoch;

        var outcome = await a.ExecuteAsync(cluster, new Transition.Bootstrap(Choice(Nodes[0], epoch)), cancellationToken);

        Assert.Equal(map == BootstrapMap.OpenLedger ? Superseded(epoch) : Applied(epoch + 1), outcome);
        Assert.Equal(0, a.Registry.PendingLifetimes);
        Assert.Equal(map != BootstrapMap.OpenLedger, a.View.Current.Ledgers.Any(ledger => ledger.IsOpen && a.Registry.TryGet(ledger.Id) is not null));
    }

    /// <summary>
    /// A split by the ledger's own primary stops admitting, recovers from the primary's exact contiguous tail, closes the
    /// ledger there, and serves both children; the parent's writer leaves the registry once the view lists it closed,
    /// and its <see cref="Ledger"/> learns the recorded <c>T</c>.
    /// </summary>
    [Fact]
    public async Task ASplitByThePrimaryClosesTheLedgerAtItsTailAndServesBothChildren()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var cluster = new TransitionCluster();
        var a = await cluster.BootstrapAsync(Nodes[0], cancellationToken);
        var root = a.Open(KeyRange.Root);
        var parent = a.Registry.TryGet(root.Id)!;
        for (var slot = 0; slot < 3; slot++)
        {
            Assert.Equal(DurableAt(slot), await ProposeAsync(parent, 0x10, cancellationToken));
        }

        Assert.Equal(Applied(2), await a.ExecuteAsync(cluster, new Transition.Split(root.Id, Choice(Nodes[0], a.View.Current.Epoch)), cancellationToken));

        Assert.Equal(new LedgerStatus.Closed(new Slot(3)), a.View.Current.Get(root.Id)!.Value.Status);
        Assert.Null(a.HeldWriter(root.Id));
        await parent.Fenced.WaitAsync(Bound, cancellationToken);

        // The registry starts the disposal of a writer it no longer holds on the thread pool, so it may not have begun yet.
        await WaitUntilAsync(() => parent.IsDisposed, "the registry disposes the parent's writer");
        _ = await Assert.ThrowsAsync<ObjectDisposedException>(() => ProposeAsync(parent, 0x10, cancellationToken));
        var (lower, upper) = KeyRange.Root.Children;
        Assert.Equal(DurableAt(0), await ProposeAsync(a.Registry.TryGet(a.Open(lower).Id)!, 0x10, cancellationToken));
        Assert.Equal(DurableAt(0), await ProposeAsync(a.Registry.TryGet(a.Open(upper).Id)!, 0x90, cancellationToken));
        Assert.Equal(new Slot(3), parent.Ledger.KnownContiguousTail);
    }

    /// <summary>
    /// A merge recovers two sibling ledgers, whose tails differ, closes each at its own recovered tail, and opens their
    /// parent, which its executor serves.
    /// </summary>
    [Fact]
    public async Task AMergeOfSiblingsOpensTheirParent()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var cluster = new TransitionCluster();
        var a = await cluster.BootstrapAsync(Nodes[0], cancellationToken);
        _ = await a.ExecuteAsync(cluster, new Transition.Split(a.Open(KeyRange.Root).Id, Choice(Nodes[0], a.View.Current.Epoch)), cancellationToken);
        var (lower, upper) = KeyRange.Root.Children;
        var lowerLedger = a.Open(lower);
        var upperLedger = a.Open(upper);
        for (var slot = 0; slot < 2; slot++)
        {
            Assert.Equal(DurableAt(slot), await ProposeAsync(a.Registry.TryGet(lowerLedger.Id)!, 0x10, cancellationToken));
        }

        Assert.Equal(DurableAt(0), await ProposeAsync(a.Registry.TryGet(upperLedger.Id)!, 0x90, cancellationToken));
        var b = await cluster.StartAsync(Nodes[1], cancellationToken);
        var merge = new Transition.Merge(lowerLedger.Id, upperLedger.Id, Choice(Nodes[1], a.View.Current.Epoch));

        Assert.Equal(Applied(3), await b.ExecuteAsync(cluster, merge, cancellationToken));

        Assert.Equal(new LedgerStatus.Closed(new Slot(2)), b.View.Current.Get(lowerLedger.Id)!.Value.Status);
        Assert.Equal(new LedgerStatus.Closed(new Slot(1)), b.View.Current.Get(upperLedger.Id)!.Value.Status);
        var merged = b.Open(KeyRange.Root);
        Assert.Equal(Nodes[1], merged.Primary.Node);
        Assert.Equal(DurableAt(0), await ProposeAsync(b.Registry.TryGet(merged.Id)!, 0x10, cancellationToken));

        // The composition's own map reads advance a view; the old primary's writers then leave its registry.
        _ = await a.View.ReadAsync(cancellationToken);
        await WaitUntilAsync(() => a.HeldWriter(lowerLedger.Id) is null && a.HeldWriter(upperLedger.Id) is null, "the old primary's writers leave");
    }

    /// <summary>
    /// ledgers.md §8.1: a transition which another node executes, which closes a ledger no map lists after a read of the
    /// register, or whose ledgers cannot take it, as a merge of ledgers which are not siblings, throws
    /// <see cref="ArgumentException"/>, sealing nothing and leaving no lifetime. (A split at the deepest range needs a
    /// map whose open ledgers partition the keyspace down to <see cref="KeyRange.MaxDepth"/>, one ledger per level.)
    /// </summary>
    [Theory]
    [InlineData(InvalidTransition.AnotherExecutor)]
    [InlineData(InvalidTransition.ClosesAnUninstalledLedger)]

    [InlineData(InvalidTransition.MergeOfNonSiblings)]
    public async Task AnInvalidTransitionThrowsWithoutSealing(InvalidTransition invalid)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var cluster = new TransitionCluster();
        TransitionProcess a;
        Transition transition;
        LedgerMetadata untouched;
        switch (invalid)
        {
            case InvalidTransition.AnotherExecutor:
                a = await cluster.BootstrapAsync(Nodes[0], cancellationToken);
                untouched = a.Open(KeyRange.Root);
                transition = new Transition.Replace(untouched.Id, Choice(Nodes[1], 1));
                break;
            case InvalidTransition.ClosesAnUninstalledLedger:
                a = await cluster.BootstrapAsync(Nodes[0], cancellationToken);
                untouched = a.Open(KeyRange.Root);
                transition = new Transition.Replace(a.View.Current.NextLedgerId, Choice(Nodes[0], 1));
                break;

            case InvalidTransition.MergeOfNonSiblings:
                a = await cluster.BootstrapAsync(Nodes[0], cancellationToken);
                _ = await a.ExecuteAsync(cluster, new Transition.Split(a.Open(KeyRange.Root).Id, Choice(Nodes[0], 1)), cancellationToken);
                var (lower, upper) = KeyRange.Root.Children;
                _ = await a.ExecuteAsync(cluster, new Transition.Split(a.Open(lower).Id, Choice(Nodes[0], 2)), cancellationToken);
                untouched = a.Open(lower.Children.Lower);
                transition = new Transition.Merge(untouched.Id, a.Open(upper).Id, Choice(Nodes[0], 3));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(invalid));
        }

        var reads = cluster.Register.Reads;

        _ = await Assert.ThrowsAsync<ArgumentException>(() => a.ExecuteAsync(cluster, transition, cancellationToken));

        Assert.Equal(0, a.Registry.PendingLifetimes);
        Assert.Equal(invalid == InvalidTransition.ClosesAnUninstalledLedger ? reads + 1 : reads, cluster.Register.Reads);
        await using var checker = new LedgerReader(a.View, cluster.Transport, new FakeTimeProvider(), localNode: null);
        Assert.False((await checker.ReadTailAsync(untouched, cancellationToken)).Tail.IsSealed);
    }

    /// <summary>ledgers.md §2.6: the executor, the primary of every ledger a transition opens, is one of their replicas.</summary>
    [Fact]
    public void AChoiceWhoseExecutorIsNotAmongItsReplicasIsRefused() =>
        _ = Assert.Throws<ArgumentException>(() => new TransitionChoice(LedgerMapSamples.Node(9), Nodes, Window, 0, 0));

    /// <summary>
    /// A replace by another process, whose view predates the transition's epoch and is refreshed first, seals the ledger,
    /// so the old primary's next proposal meets the seal, is decided from the recorded <c>T</c>, and its writer leaves its
    /// registry once its view lists the ledger closed.
    /// </summary>
    [Fact]
    public async Task AReplaceByAnotherProcessFencesTheOldPrimary()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var cluster = new TransitionCluster();
        var b = await cluster.StartAsync(Nodes[1], cancellationToken);
        var a = await cluster.BootstrapAsync(Nodes[0], cancellationToken);
        var root = a.Open(KeyRange.Root);
        var old = a.Registry.TryGet(root.Id)!;
        Assert.Equal(DurableAt(0), await ProposeAsync(old, 0x10, cancellationToken));
        Assert.Equal(0ul, b.View.Current.Epoch);

        Assert.Equal(Applied(2), await b.ExecuteAsync(cluster, new Transition.Replace(root.Id, Choice(Nodes[1], 1)), cancellationToken));

        Assert.Equal(Fenced, await ProposeAsync(old, 0x10, cancellationToken));
        await old.Fenced.WaitAsync(Bound, cancellationToken);
        await WaitUntilAsync(() => a.HeldWriter(root.Id) is null, "the old primary's writer leaves");
        var successor = b.Open(KeyRange.Root);
        Assert.Equal(DurableAt(0), await ProposeAsync(b.Registry.TryGet(successor.Id)!, 0x10, cancellationToken));
        Assert.Empty(a.Registry.Unserved);
    }

    /// <summary>
    /// A refused change stops the lifetimes the executor created, so a view which later lists their claims finds no
    /// lifetime to serve; the seals stand, and the closed ledger stays open in the map.
    /// </summary>
    [Fact]
    public async Task ARefusedChangeStopsTheLifetimes()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var cluster = new TransitionCluster();
        var a = await cluster.BootstrapAsync(Nodes[0], cancellationToken);
        var root = a.Open(KeyRange.Root);
        LedgerMapChange? submitted = null;

        var outcome = await a.Executor.ExecuteAsync(
            new Transition.Replace(root.Id, Choice(Nodes[0], 1)),
            (change, _) =>
            {
                submitted = change;
                return Task.FromResult<LedgerMapCommitOutcome>(LedgerMapCommitOutcome.Refused.Instance);
            },
            cancellationToken);

        Assert.Same(LedgerMapCommitOutcome.Refused.Instance, outcome.Value);
        Assert.Equal(0, a.Registry.PendingLifetimes);
        Assert.True(a.View.Current.Get(root.Id)!.Value.IsOpen);
        await cluster.PublishAppliedAsync(a, submitted!.Value, cancellationToken);
        var opened = a.Open(KeyRange.Root);

        // The sealed root was unserved while the map listed it open; the published change closes it.
        Assert.Equal([opened.Id], await a.Registry.WaitForUnservedAsync(Known(root.Id), cancellationToken).WaitAsync(Bound, cancellationToken));
        Assert.Null(a.Registry.TryGet(opened.Id));
    }

    /// <summary>
    /// A rejected change is a defect: the executor stops the lifetimes it created, logs the reason as an error, and fails
    /// a debug assertion naming it.
    /// </summary>
    [Fact]
    public async Task ARejectedChangeStopsTheLifetimesAndReportsTheDefect()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var cluster = new TransitionCluster();
        var a = await cluster.StartAsync(Nodes[0], cancellationToken);
        var since = DebugAssertions.RaisedCount;
        var rejected = new LedgerMapCommitOutcome(new LedgerMapCommitOutcome.Rejected(LedgerMapRejection.OpensLeaveGap));

        var execution = a.Executor.ExecuteAsync(new Transition.Bootstrap(Choice(Nodes[0], 0)), (_, _) => Task.FromResult(rejected), cancellationToken);

        if (TestDiagnostics.Configuration == "Debug")
        {
            _ = await Assert.ThrowsAsync<DebugAssertionException>(() => execution);
            var assertion = Assert.Single(DebugAssertions.TakeRaisedBy(TestContext.Current.Test, since));
            Assert.Contains(nameof(LedgerMapRejection.OpensLeaveGap), assertion.Message, StringComparison.Ordinal);
        }
        else
        {
            Assert.Equal(rejected, await execution);
        }

        Assert.Equal(0, a.Registry.PendingLifetimes);
        var (level, message) = Assert.Single(a.Logger.Entries);
        Assert.Equal(LogLevel.Error, level);
        Assert.Contains(nameof(LedgerMapRejection.OpensLeaveGap), message, StringComparison.Ordinal);
    }

    /// <summary>
    /// ledgers.md §8.1: cancellation once a replace's seals have returned, before <c>commit</c> is called, stops the
    /// transition with no lifetime left and nothing submitted; the seals stand, so the old primary's next proposal is
    /// <see cref="ProposalOutcome.Fenced"/> once a later replace closes the ledger, which the map still lists open.
    /// </summary>
    [Fact]
    public async Task CancellationBeforeCommitStopsTheLifetimes()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var cluster = new TransitionCluster();
        var a = await cluster.BootstrapAsync(Nodes[0], cancellationToken);
        var root = a.Open(KeyRange.Root);
        var old = a.Registry.TryGet(root.Id)!;
        var b = await cluster.StartAsync(Nodes[1], cancellationToken);
        using var interrupt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var seals = 0;
        b.Hooks.ReadTail = async (call, inner) =>
        {
            var answer = await inner();
            if (call.Seal && Interlocked.Increment(ref seals) == Nodes.Length)
            {
                await interrupt.CancelAsync();
            }

            return answer;
        };
        var calls = 0;

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => b.Executor.ExecuteAsync(
            new Transition.Replace(root.Id, Choice(Nodes[1], 1)),
            (change, token) =>
            {
                calls++;
                return cluster.Committer.CommitAsync(change, token);
            },
            interrupt.Token));

        b.Hooks.ReadTail = null;
        Assert.Equal(0, calls);
        Assert.Equal(0, b.Registry.PendingLifetimes);
        Assert.True((await b.View.ReadAsync(cancellationToken)).Get(root.Id)!.Value.IsOpen);
        var proposal = ProposeAsync(old, 0x10, cancellationToken);
        await WaitUntilAsync(() => old.Sequencer.IsStopped, "the old primary's write meets the seal");
        Assert.False(proposal.IsCompleted);

        var c = await cluster.StartAsync(Nodes[2], cancellationToken);
        Assert.Equal(Applied(2), await c.ExecuteAsync(cluster, new Transition.Replace(root.Id, Choice(Nodes[2], 1)), cancellationToken));
        _ = await a.View.ReadAsync(cancellationToken);
        Assert.Equal(Fenced, await proposal.WaitAsync(Bound, cancellationToken));
    }

    /// <summary>
    /// ledgers.md §8.1: cancellation after <c>commit</c> was called only stops the caller waiting: the change applied, so
    /// its lifetime stays pending and the registry adopts it once the view lists it.
    /// </summary>
    [Fact]
    public async Task CancellationAfterCommitLeavesTheLifetimesToTheView()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var cluster = new TransitionCluster();
        var a = await cluster.BootstrapAsync(Nodes[0], cancellationToken);
        var root = a.Open(KeyRange.Root);
        var b = await cluster.StartAsync(Nodes[1], cancellationToken);
        using var interrupt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => b.Executor.ExecuteAsync(
            new Transition.Replace(root.Id, Choice(Nodes[1], 1)),
            async (change, token) =>
            {
                Assert.Equal(Applied(2), await cluster.Committer.CommitAsync(change, CancellationToken.None));
                await interrupt.CancelAsync();
                token.ThrowIfCancellationRequested();
                throw new UnreachableException();
            },
            interrupt.Token));

        Assert.Equal(1, b.Registry.PendingLifetimes);
        Assert.True(b.View.Current.Get(root.Id)!.Value.IsOpen);
        _ = await b.View.ReadAsync(cancellationToken);
        var successor = b.Open(KeyRange.Root);
        await WaitUntilAsync(() => b.Registry.TryGet(successor.Id) is not null, "the registry adopts the lifetime");
        Assert.Equal(0, b.Registry.PendingLifetimes);
    }

    /// <summary>
    /// An unknown outcome leaves the lifetimes to the map view, whether <c>commit</c> throws synchronously after it
    /// dispatched the change or returns a faulted task: once a view lists the change applied, the registry adopts its
    /// lifetime and serves the ledger.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnUnknownOutcomeLeavesTheLifetimesToTheView(bool throwsSynchronously)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var cluster = new TransitionCluster();
        var a = await cluster.BootstrapAsync(Nodes[0], cancellationToken);
        var root = a.Open(KeyRange.Root);
        var b = await cluster.StartAsync(Nodes[1], cancellationToken);
        LedgerMapChange? submitted = null;

        _ = await Assert.ThrowsAsync<IOException>(() => b.Executor.ExecuteAsync(
            new Transition.Replace(root.Id, Choice(Nodes[1], 1)),
            (change, _) =>
            {
                submitted = change;
                var unknown = new IOException("The write's outcome is unknown.");
                return throwsSynchronously ? throw unknown : Task.FromException<LedgerMapCommitOutcome>(unknown);
            },
            cancellationToken));

        Assert.Equal(1, b.Registry.PendingLifetimes);
        await cluster.PublishAppliedAsync(b, submitted!.Value, cancellationToken);
        var successor = b.Open(KeyRange.Root);
        await WaitUntilAsync(() => b.Registry.TryGet(successor.Id) is not null, "the registry adopts the lifetime");
        Assert.Equal(DurableAt(0), await ProposeAsync(b.Registry.TryGet(successor.Id)!, 0x10, cancellationToken));
        Assert.Equal(0, b.Registry.PendingLifetimes);
    }

    /// <summary>
    /// ledgers.md §8.1: while lifetimes are pending, the registry re-reads the register itself, with backoff, so a change
    /// which applied though its <c>commit</c> failed with an unknown outcome is adopted with no other read of the map.
    /// </summary>
    [Fact]
    public async Task AnUnknownOutcomeIsAdoptedFromTheRegistrysOwnReread()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var cluster = new TransitionCluster();
        var a = await cluster.BootstrapAsync(Nodes[0], cancellationToken);
        var root = a.Open(KeyRange.Root);
        var b = await cluster.StartAsync(Nodes[1], cancellationToken);

        _ = await Assert.ThrowsAsync<TimeoutException>(() => b.Executor.ExecuteAsync(
            new Transition.Replace(root.Id, Choice(Nodes[1], 1)),
            async (change, token) =>
            {
                Assert.Equal(Applied(2), await cluster.Committer.CommitAsync(change, token));
                throw new TimeoutException("The reply was lost.");
            },
            cancellationToken));

        Assert.Equal(1, b.Registry.PendingLifetimes);
        Assert.Equal(1ul, b.View.Current.Epoch);
        var started = TimeProvider.System.GetTimestamp();
        while (b.Registry.PendingLifetimes > 0)
        {
            Assert.True(TimeProvider.System.GetElapsedTime(started) < Bound, "The registry did not re-read the register.");
            b.Time.Advance(LedgerWriter.MapReadMaximumDelay);
            await Task.Delay(TimeSpan.FromMilliseconds(1), TimeProvider.System, cancellationToken);
        }

        Assert.Equal(2ul, b.View.Current.Epoch);
        Assert.NotNull(b.Registry.TryGet(b.Open(KeyRange.Root).Id));
    }

    /// <summary>
    /// ledgers.md §8.1: a <c>commit</c> failure under which the change never applies stops the lifetimes at once; a
    /// disposed committer may have written it, so its lifetime stays pending for the view.
    /// </summary>
    [Theory]
    [InlineData(CommitFailure.Overflow)]
    [InlineData(CommitFailure.InvalidData)]
    [InlineData(CommitFailure.InvalidOperation)]
    [InlineData(CommitFailure.Disposed)]
    public async Task ACommitFailureStopsTheLifetimesOnlyWhenTheChangeNeverApplies(CommitFailure failure)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var cluster = new TransitionCluster();
        var a = await cluster.BootstrapAsync(Nodes[0], cancellationToken);
        var root = a.Open(KeyRange.Root);
        var b = await cluster.StartAsync(Nodes[1], cancellationToken);
        Exception thrown = failure switch
        {
            CommitFailure.Overflow => new OverflowException("The IDs would overflow."),
            CommitFailure.InvalidData => new InvalidDataException("The register holds no ledger map."),
            CommitFailure.InvalidOperation => new InvalidOperationException("The committer found a defect."),
            CommitFailure.Disposed => new ObjectDisposedException(nameof(LedgerMapCommitter)),
            _ => throw new ArgumentOutOfRangeException(nameof(failure)),
        };

        var raised = await Assert.ThrowsAnyAsync<Exception>(() => b.Executor.ExecuteAsync(
            new Transition.Replace(root.Id, Choice(Nodes[1], 1)),
            (_, _) => Task.FromException<LedgerMapCommitOutcome>(thrown),
            cancellationToken));

        Assert.Same(thrown, raised);
        Assert.Equal(failure == CommitFailure.Disposed ? 1 : 0, b.Registry.PendingLifetimes);
    }

    /// <summary>
    /// After an unknown outcome, a view whose retirement floor passes the change's <c>ChosenAt</c> while every ledger it
    /// closes is still open stops the lifetime, since the change can no longer apply (LG-I11).
    /// </summary>
    [Fact]
    public async Task AnUnknownOutcomeWhoseFloorPassesChosenAtStopsTheLifetime()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var cluster = new TransitionCluster();
        var a = await cluster.BootstrapAsync(Nodes[0], cancellationToken);
        var root = a.Open(KeyRange.Root);
        var b = await cluster.StartAsync(Nodes[1], cancellationToken);
        _ = await Assert.ThrowsAsync<IOException>(() => b.Executor.ExecuteAsync(
            new Transition.Replace(root.Id, Choice(Nodes[1], 1, chosenAt: 2)),
            (_, _) => Task.FromException<LedgerMapCommitOutcome>(new IOException("The write's outcome is unknown.")),
            cancellationToken));
        Assert.Equal(1, b.Registry.PendingLifetimes);

        Assert.Equal(new LedgerMapCommitOutcome.Applied(2), (await cluster.Committer.CommitAsync(new LedgerMapChange.RaiseFloor(3), cancellationToken)).Value);
        _ = await b.View.ReadAsync(cancellationToken);

        await WaitUntilAsync(() => b.Registry.PendingLifetimes == 0, "the registry stops the lifetime");
        Assert.True(b.View.Current.Get(root.Id)!.Value.IsOpen);
    }

    /// <summary>
    /// ledgers.md §8.1: a retry of a transition whose reply was lost answers <c>Applied</c>, not <c>Superseded</c>: its
    /// view lists the range under the first attempt's claim, which the registry holds, and serves that attempt's writer;
    /// the retry's own lifetime stops. The retry's view may already list the change, or may still predate it.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ARetryAfterALostReplyAnswersApplied(bool viewListsTheChange)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var cluster = new TransitionCluster();
        var a = await cluster.BootstrapAsync(Nodes[0], cancellationToken);
        var root = a.Open(KeyRange.Root);
        var b = await cluster.StartAsync(Nodes[1], cancellationToken);
        var transition = new Transition.Replace(root.Id, Choice(Nodes[1], 1));
        LedgerMapChange? first = null;
        _ = await Assert.ThrowsAsync<IOException>(() => b.Executor.ExecuteAsync(
            transition,
            async (change, token) =>
            {
                first = change;
                Assert.Equal(Applied(2), await cluster.Committer.CommitAsync(change, token));
                throw new IOException("The reply was lost.");
            },
            cancellationToken));
        var claim = Assert.IsType<LedgerMapChange.Install>(first!.Value.Value).Opens[0].Primary;
        if (viewListsTheChange)
        {
            _ = await b.View.ReadAsync(cancellationToken);
        }

        Assert.Equal(Applied(2), await b.ExecuteAsync(cluster, transition, cancellationToken));

        var successor = b.Open(KeyRange.Root);
        Assert.Equal(claim, successor.Primary);
        Assert.Equal(claim.Sequencer, b.Registry.TryGet(successor.Id)!.InstanceId);
        Assert.Equal(0, b.Registry.PendingLifetimes);
    }

    /// <summary>
    /// A lifetime's origin and its hold are one step: another transition's execution, started between them, cannot prune
    /// the origin as a lifetime its registry does not know, so after a lost reply the resend of the transition which
    /// applied still answers <c>Applied</c>. The other execution is started from the seam between the two and given a
    /// second to prune; with the steps apart, it prunes and reaches the seam itself well within it.
    /// </summary>
    [Fact]
    public async Task AConcurrentPruneKeepsTheOriginOfALifetimeBeingHeld()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var cluster = new TransitionCluster();
        var a = await cluster.BootstrapAsync(Nodes[0], cancellationToken);
        var root = a.Open(KeyRange.Root);
        var b = await cluster.StartAsync(Nodes[1], cancellationToken);
        var transition = new Transition.Replace(root.Id, Choice(Nodes[1], 1));
        var other = new Transition.Replace(root.Id, Choice(Nodes[1], 1, chosenAt: 1));
        var pruned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<LedgerMapCommitOutcome>? concurrent = null;
        var seams = 0;
        b.Executor.OriginRecorded = () =>
        {
            if (Interlocked.Increment(ref seams) == 1)
            {
                concurrent = Task.Run(() => b.Executor.ExecuteAsync(other, static (_, _) => Task.FromResult<LedgerMapCommitOutcome>(LedgerMapCommitOutcome.Refused.Instance), CancellationToken.None), CancellationToken.None);
                _ = pruned.Task.Wait(TimeSpan.FromSeconds(1), CancellationToken.None);
            }
            else
            {
                pruned.TrySetResult();
            }
        };

        _ = await Assert.ThrowsAsync<IOException>(() => b.Executor.ExecuteAsync(
            transition,
            async (change, token) =>
            {
                Assert.Equal(Applied(2), await cluster.Committer.CommitAsync(change, token));
                throw new IOException("The reply was lost.");
            },
            cancellationToken));
        Assert.Equal(LedgerMapCommitOutcome.Refused.Instance, (await concurrent!).Value);
        b.Executor.OriginRecorded = null;

        Assert.Equal(Applied(2), await b.ExecuteAsync(cluster, transition, cancellationToken));
    }
    /// <summary>
    /// A transition chosen below the refreshed view's retirement floor is <c>Superseded</c> at once, before it seals
    /// anything: the primary's open check still finds the ledger open.
    /// </summary>
    [Fact]
    public async Task ATransitionChosenBelowTheFloorIsSupersededWithoutSealing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var cluster = new TransitionCluster();
        var a = await cluster.BootstrapAsync(Nodes[0], cancellationToken);
        var root = a.Open(KeyRange.Root);
        _ = await cluster.Committer.CommitAsync(new LedgerMapChange.RaiseFloor(5), cancellationToken);
        var b = await cluster.StartAsync(Nodes[1], cancellationToken);

        Assert.Equal(Superseded(2), await b.ExecuteAsync(cluster, new Transition.Replace(root.Id, Choice(Nodes[1], 1, chosenAt: 4)), cancellationToken));

        Assert.Equal(new OpenCheckOutcome.Open(Slot.Zero), (await a.Registry.TryGet(root.Id)!.ConfirmOpenAsync(cancellationToken)).Value);
        Assert.Equal(0, b.Registry.PendingLifetimes);
    }

    /// <summary>
    /// A roll its own primary executes, chosen below the floor, is <c>Superseded</c>, not <c>Applied</c>: the view lists the
    /// range under the primary's own claim, as it would after a retry whose first attempt applied, but the ledger the roll
    /// closes is still open, so the change never applied.
    /// </summary>
    [Fact]
    public async Task APrimarysOwnRollChosenBelowTheFloorIsSuperseded()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var cluster = new TransitionCluster();
        var a = await cluster.BootstrapAsync(Nodes[0], cancellationToken);
        var root = a.Open(KeyRange.Root);
        _ = await cluster.Committer.CommitAsync(new LedgerMapChange.RaiseFloor(5), cancellationToken);

        Assert.Equal(Superseded(2), await a.ExecuteAsync(cluster, new Transition.Replace(root.Id, Choice(Nodes[0], 1, chosenAt: 4)), cancellationToken));

        Assert.True(a.View.Current.Get(root.Id)!.Value.IsOpen);
        Assert.Equal(0, a.Registry.PendingLifetimes);
    }

    /// <summary>
    /// The same roll, with the executor's view already showing the raised floor: the rule fails in its own view, so it
    /// answers <c>Superseded</c> from that view, writing nothing, sealing nothing, and creating no lifetime.
    /// </summary>
    [Fact]
    public async Task APrimarysOwnRollWhoseViewShowsTheFloorIsSupersededWithoutAWrite()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var cluster = new TransitionCluster();
        var a = await cluster.BootstrapAsync(Nodes[0], cancellationToken);
        var root = a.Open(KeyRange.Root);
        _ = await cluster.Committer.CommitAsync(new LedgerMapChange.RaiseFloor(5), cancellationToken);
        _ = await a.View.ReadAsync(cancellationToken);
        var writes = cluster.Register.Writes;

        Assert.Equal(Superseded(2), await a.ExecuteAsync(cluster, new Transition.Replace(root.Id, Choice(Nodes[0], 1, chosenAt: 4)), cancellationToken));

        Assert.Equal(writes, cluster.Register.Writes);
        Assert.Equal(new OpenCheckOutcome.Open(Slot.Zero), (await a.Registry.TryGet(root.Id)!.ConfirmOpenAsync(cancellationToken)).Value);
        Assert.Equal(0, a.Registry.PendingLifetimes);
    }

    /// <summary>
    /// ledgers.md §2.6: <c>Applied</c> means this transition's change applied. Once another roll of the ledger by the same
    /// primary applied, a roll chosen otherwise finds the range listed under a claim this process holds, created for the
    /// other roll, and answers <c>Superseded</c>; the roll which applied, sent again, answers <c>Applied</c>.
    /// </summary>
    [Fact]
    public async Task AnotherRollOfTheSamePrimaryWhichAppliedSupersedesThisOne()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var cluster = new TransitionCluster();
        var a = await cluster.BootstrapAsync(Nodes[0], cancellationToken);
        var root = a.Open(KeyRange.Root);
        var applied = new Transition.Replace(root.Id, Choice(Nodes[0], 1, chosenAt: 2));
        Assert.Equal(Applied(2), await a.ExecuteAsync(cluster, applied, cancellationToken));

        var other = new Transition.Replace(root.Id, Choice(Nodes[0], 1, chosenAt: 1));
        Assert.Equal(Superseded(2), await a.ExecuteAsync(cluster, other, cancellationToken));

        Assert.Equal(Applied(2), await a.ExecuteAsync(cluster, applied, cancellationToken));
        Assert.Equal(0, a.Registry.PendingLifetimes);
    }

    /// <summary>
    /// An executor whose view does not yet list a ledger the transition closes, its ID at or above the view's
    /// <c>NextLedgerId</c>, reads the register rather than answering <c>Superseded</c>.
    /// </summary>
    [Fact]
    public async Task AViewWhichDoesNotYetListAClosedLedgerReadsTheRegister()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var cluster = new TransitionCluster();
        var a = await cluster.BootstrapAsync(Nodes[0], cancellationToken);
        var b = await cluster.StartAsync(Nodes[1], cancellationToken);
        _ = await a.ExecuteAsync(cluster, new Transition.Split(a.Open(KeyRange.Root).Id, Choice(Nodes[0], 1)), cancellationToken);
        Assert.Equal(1ul, b.View.Current.Epoch);
        var lower = a.Open(KeyRange.Root.Children.Lower);

        Assert.Equal(Applied(3), await b.ExecuteAsync(cluster, new Transition.Replace(lower.Id, Choice(Nodes[1], 1)), cancellationToken));

        Assert.NotNull(b.Registry.TryGet(b.Open(KeyRange.Root.Children.Lower).Id));
    }

    /// <summary>
    /// LP-I11: a losing executor's change is <c>Superseded</c>, its lifetime stops, and its <see cref="Ledger"/> of the
    /// ledger both recovered, leased before the transition, learns the map's recorded <c>T</c>, not its own candidate end,
    /// which lies above <c>T</c>: a write of the old primary reached one replica, which the winner's recovery did not hear
    /// from and the loser's did.
    /// </summary>
    [Fact]
    public async Task ALosingExecutorStopsItsLifetimeAndLearnsTheRecordedTail()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var cluster = new TransitionCluster();
        var a = await cluster.BootstrapAsync(Nodes[0], cancellationToken);
        var root = a.Open(KeyRange.Root);
        var writer = a.Registry.TryGet(root.Id)!;
        Assert.Equal(DurableAt(0), await ProposeAsync(writer, 0x10, cancellationToken));
        Assert.Equal(DurableAt(1), await ProposeAsync(writer, 0x10, cancellationToken));
        var b = await cluster.StartAsync(Nodes[1], cancellationToken);
        var c = await cluster.StartAsync(Nodes[2], cancellationToken);
        using var loser = b.Reader.LeaseLedger(root);

        // The old primary's write at slot 2 reaches the third node alone.
        var landed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        a.Hooks.Write = async (call, inner) =>
        {
            if (call.Slot != new Slot(2))
            {
                return await inner();
            }

            if (call.Node != Nodes[2])
            {
                return ReplicaWriteOutcome.Backpressure.Instance;
            }

            var answer = await inner();
            _ = landed.TrySetResult();
            return answer;
        };
        var stranded = ProposeAsync(writer, 0x10, cancellationToken);
        await landed.Task.WaitAsync(Bound, cancellationToken);

        // The winner's recovery never hears from the third node, so it ends at 2.
        c.Hooks.ReadTail = (call, inner) => call.Ledger == root.Id && call.Node == Nodes[2] ? ValueTask.FromResult(new ReplicaTailRead(ReplicaWriteOutcome.Backpressure.Instance)) : inner();
        c.Hooks.Read = (call, inner) => call.Ledger == root.Id && call.Node == Nodes[2] ? ValueTask.FromResult(new ReplicaRead(ReplicaWriteOutcome.Backpressure.Instance)) : inner();
        Assert.Equal(Applied(2), await c.ExecuteAsync(cluster, new Transition.Replace(root.Id, Choice(Nodes[2], 1)), cancellationToken));
        Assert.Equal(new LedgerStatus.Closed(new Slot(2)), c.View.Current.Get(root.Id)!.Value.Status);

        // The loser's recovery hears the third node's batch at slot 2 before the others' absence, so it ends at 3: the
        // others' reads at slot 2 answer only once the recovery copies the batch, which it does once it decides slot 2
        // found. Their absence, handled before the batch, would decide slot 2 the end. The test owns the gate, and opens
        // it to end the gated reads when the recovery never decides.
        var decided = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        b.Hooks.Write = (call, inner) =>
        {
            if (call.Ledger == root.Id && call.Recovery && call.Slot == new Slot(2))
            {
                _ = decided.TrySetResult();
            }

            return inner();
        };
        b.Hooks.Read = async (call, inner) =>
        {
            if (call.Ledger == root.Id && call.Slot == new Slot(2) && call.Node != Nodes[2])
            {
                await decided.Task.WaitAsync(call.CancellationToken);
            }

            return await inner();
        };
        LedgerMapChange? submitted = null;

        var execution = b.Executor.ExecuteAsync(
            new Transition.Replace(root.Id, Choice(Nodes[1], 1)),
            (change, token) =>
            {
                submitted = change;
                return cluster.Committer.CommitAsync(change, token);
            },
            cancellationToken);
        LedgerMapCommitOutcome outcome;
        try
        {
            await decided.Task.WaitAsync(Bound, cancellationToken);
            outcome = await execution.WaitAsync(Bound, cancellationToken);
        }
        catch (TimeoutException) when (!decided.Task.IsCompleted)
        {
            throw new Xunit.Sdk.XunitException(
                $"The loser's recovery copied no batch at slot 2 within {Bound}, so it never decided slot 2 found; the execution {(execution.IsCompleted ? "ended" : "still runs")}.");
        }
        finally
        {
            _ = decided.TrySetCanceled(cancellationToken);
        }

        Assert.Equal(Superseded(2), outcome);
        Assert.Equal(new Slot(3), Assert.IsType<LedgerMapChange.Install>(submitted!.Value.Value).Closes[0].T);
        Assert.Equal(new Slot(2), loser.Ledger.KnownContiguousTail);
        Assert.Equal(0, b.Registry.PendingLifetimes);
        Assert.Empty(b.Registry.Unserved);
        _ = await a.View.ReadAsync(cancellationToken);
        Assert.Equal(Fenced, await stranded.WaitAsync(Bound, cancellationToken));
    }

    /// <summary>
    /// LP-I5: an executor whose recovery meets a replica which dropped the ledger answers <c>Superseded</c> at the
    /// register's latest epoch, which its view then holds, and leaves no lifetime: the ledger has left the map.
    /// </summary>
    [Fact]
    public async Task ARecoveryWhichMeetsADroppedReplicaIsSuperseded()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var cluster = new TransitionCluster();
        var a = await cluster.BootstrapAsync(Nodes[0], cancellationToken);
        var root = a.Open(KeyRange.Root);
        var b = await cluster.StartAsync(Nodes[1], cancellationToken);
        var c = await cluster.StartAsync(Nodes[2], cancellationToken);
        Assert.Equal(Applied(2), await c.ExecuteAsync(cluster, new Transition.Replace(root.Id, Choice(Nodes[2], 1)), cancellationToken));
        Assert.Equal(Applied(3), await cluster.Committer.CommitAsync(new LedgerMapChange.Drop(root.Id), cancellationToken));
        _ = await c.View.ReadAsync(cancellationToken);
        foreach (var node in Nodes)
        {
            Assert.Equal([root.Id], await new ReplicaReconciler(cluster.Transport.Node(node), c.View).ReconcileAsync(cancellationToken));
        }

        Assert.Equal(1ul, b.View.Current.Epoch);

        Assert.Equal(Superseded(3), await b.ExecuteAsync(cluster, new Transition.Replace(root.Id, Choice(Nodes[1], 1)), cancellationToken));

        Assert.Equal(3ul, b.View.Current.Epoch);
        Assert.Equal(0, b.Registry.PendingLifetimes);
        Assert.Empty(b.Registry.Unserved);
    }

    /// <summary>
    /// ledgers.md §8.1: while an executor in this process closes a ledger whose primary this process is, the ledger is not
    /// <see cref="LedgerWriterRegistry.Unserved"/>, though its lifetime stopped, so no controller replaces it meanwhile;
    /// an executor interrupted before its commit leaves it unserved.
    /// </summary>
    [Fact]
    public async Task ALedgerBeingClosedHereIsNotUnservedUntilItsExecutorEnds()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var cluster = new TransitionCluster();
        var a = await cluster.BootstrapAsync(Nodes[0], cancellationToken);
        var root = a.Open(KeyRange.Root);
        var writer = a.Registry.TryGet(root.Id)!;
        var sealing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        a.Hooks.ReadTail = async (call, inner) =>
        {
            if (call.Seal)
            {
                _ = sealing.TrySetResult();
                await released.Task.WaitAsync(call.CancellationToken);
            }

            return await inner();
        };
        using var interrupt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var split = a.ExecuteAsync(cluster, new Transition.Split(root.Id, Choice(Nodes[0], 1)), interrupt.Token);
        await sealing.Task.WaitAsync(Bound, cancellationToken);
        Assert.True(writer.Sequencer.IsStopped);
        await a.Registry.ReconciledAsync(a.View.Current.Epoch, cancellationToken).WaitAsync(Bound, cancellationToken);

        Assert.Empty(a.Registry.Unserved);
        Assert.Null(a.Registry.TryGet(root.Id));
        await interrupt.CancelAsync();
        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => split);

        Assert.Equal([root.Id], await a.Registry.WaitForUnservedAsync(NoneKnown, cancellationToken).WaitAsync(Bound, cancellationToken));
        Assert.True(a.View.Current.Get(root.Id)!.Value.IsOpen);
    }

    /// <summary>
    /// A split by the primary's own process which exits before its seal leaves the lifetime stopped and the ledger
    /// <see cref="LedgerWriterRegistry.Unserved"/>, unsealed, since the writer leaves the recovery to the executor which
    /// stopped admitting; the proposal it was waiting for is decided once the map records <c>T</c>.
    /// </summary>
    [Fact]
    public async Task ASplitByThePrimaryWhichExitsBeforeItsSealLeavesTheLedgerUnserved()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var cluster = new TransitionCluster();
        var a = await cluster.BootstrapAsync(Nodes[0], cancellationToken);
        var root = a.Open(KeyRange.Root);
        var writer = a.Registry.TryGet(root.Id)!;
        var completing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        writer.Sequencer.BeforeComplete = (_, token) => new ValueTask(completing.Task.WaitAsync(token));
        var held = ProposeAsync(writer, 0x10, cancellationToken);
        using var exit = new CancellationTokenSource();

        // The executor runs up to its wait for the held proposal before the call returns.
        var split = a.ExecuteAsync(cluster, new Transition.Split(root.Id, Choice(Nodes[0], 1)), exit.Token);
        Assert.False(split.IsCompleted);
        await exit.CancelAsync();

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => split);
        completing.SetResult();
        Assert.True(writer.Sequencer.IsStopped);
        Assert.Equal([root.Id], await a.Registry.WaitForUnservedAsync(NoneKnown, cancellationToken).WaitAsync(Bound, cancellationToken));
        Assert.Equal(Fenced, await ProposeAsync(writer, 0x10, cancellationToken));
        Assert.True(a.View.Current.Get(root.Id)!.Value.IsOpen);
        await using (var checker = new LedgerReader(a.View, cluster.Transport, new FakeTimeProvider(), localNode: null))
        {
            Assert.False((await checker.ReadTailAsync(root, cancellationToken)).Tail.IsSealed);
        }

        var b = await cluster.StartAsync(Nodes[1], cancellationToken);
        Assert.Equal(Applied(2), await b.ExecuteAsync(cluster, new Transition.Replace(root.Id, Choice(Nodes[1], 1)), cancellationToken));
        _ = await a.View.ReadAsync(cancellationToken);
        Assert.Equal(DurableAt(0), await held.WaitAsync(Bound, cancellationToken));
    }

    /// <summary>
    /// After <see cref="LedgerWriter.StopAdmittingAsync"/>, which stops the lifetime at the primary's contiguous tail,
    /// every proposal completes <see cref="ProposalOutcome.Fenced"/> without a slot.
    /// </summary>
    [Fact]
    public async Task ProposalsAfterStopAdmittingAreFenced()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var cluster = new TransitionCluster();
        var a = await cluster.BootstrapAsync(Nodes[0], cancellationToken);
        var writer = a.Registry.TryGet(a.Open(KeyRange.Root).Id)!;
        Assert.Equal(DurableAt(0), await ProposeAsync(writer, 0x10, cancellationToken));
        Assert.Equal(DurableAt(1), await ProposeAsync(writer, 0x10, cancellationToken));

        await writer.StopAdmittingAsync(cancellationToken);

        Assert.Equal(new Slot(2), writer.Ledger.KnownContiguousTail);
        Assert.Equal(Fenced, await ProposeAsync(writer, 0x10, cancellationToken));
        Assert.Equal(SequencerStopReason.Sealing, await writer.Sequencer.Stopped);
        await writer.Fenced.WaitAsync(Bound, cancellationToken);
    }

    /// <summary>
    /// ledgers.md §2.4: <see cref="LedgerWriter.StopAdmittingAsync"/> waits for every earlier proposal's append to end,
    /// also one whose caller stopped waiting, so the tail that append completed is known; the lifetime stops only then.
    /// </summary>
    [Fact]
    public async Task StopAdmittingWaitsForTheAppendOfAProposalWhoseCallerStoppedWaiting()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var cluster = new TransitionCluster();
        var a = await cluster.BootstrapAsync(Nodes[0], cancellationToken);
        var writer = a.Registry.TryGet(a.Open(KeyRange.Root).Id)!;
        var completing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        writer.Sequencer.BeforeComplete = async (grant, token) =>
        {
            _ = held.TrySetResult();
            await completing.Task.WaitAsync(token);
        };
        using var abandoned = new CancellationTokenSource();
        var proposal = ProposeAsync(writer, 0x10, abandoned.Token);
        await held.Task.WaitAsync(Bound, cancellationToken);
        await abandoned.CancelAsync();
        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => proposal);

        var stopping = writer.StopAdmittingAsync(cancellationToken);
        Assert.Equal(Fenced, await ProposeAsync(writer, 0x10, cancellationToken));
        Assert.False(stopping.IsCompleted);
        Assert.False(writer.Sequencer.IsStopped);
        completing.SetResult();

        await stopping.WaitAsync(Bound, cancellationToken);
        Assert.Equal(new Slot(1), writer.Ledger.KnownContiguousTail);
        Assert.True(writer.Sequencer.IsStopped);
    }

    /// <summary>
    /// ledgers.md §8.1: <see cref="LedgerWriter.StopAdmittingAsync"/> waits for an admitted append at most
    /// <see cref="LedgerWriter.StopAdmittingDrainLimit"/> on the writer's clock: an append which waits out backpressure
    /// forever does not hold it, the lifetime stops with <see cref="SequencerStopReason.Sealing"/>, a second call ends
    /// too, and the executor's seal decides the append.
    /// </summary>
    [Fact]
    public async Task StopAdmittingIsBoundedByItsDrainLimit()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var cluster = new TransitionCluster();
        var a = await cluster.BootstrapAsync(Nodes[0], cancellationToken);
        var root = a.Open(KeyRange.Root);
        var writer = a.Registry.TryGet(root.Id)!;
        Assert.Equal(DurableAt(0), await ProposeAsync(writer, 0x10, cancellationToken));
        var writing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        a.Hooks.Write = (call, inner) =>
        {
            _ = writing.TrySetResult();
            return ValueTask.FromResult<ReplicaWriteOutcome>(ReplicaWriteOutcome.Backpressure.Instance);
        };
        var stuck = ProposeAsync(writer, 0x10, cancellationToken);
        await writing.Task.WaitAsync(Bound, cancellationToken);

        var stopping = writer.StopAdmittingAsync(cancellationToken);
        Assert.False(stopping.IsCompleted);
        Assert.False(writer.Sequencer.IsStopped);
        a.Time.Advance(LedgerWriter.StopAdmittingDrainLimit);
        await stopping.WaitAsync(Bound, cancellationToken);

        Assert.Equal(SequencerStopReason.Sealing, await writer.Sequencer.Stopped);
        var again = writer.StopAdmittingAsync(cancellationToken);
        a.Time.Advance(LedgerWriter.StopAdmittingDrainLimit);
        await again.WaitAsync(Bound, cancellationToken);
        Assert.False(stuck.IsCompleted);

        var b = await cluster.StartAsync(Nodes[1], cancellationToken);
        Assert.Equal(Applied(2), await b.ExecuteAsync(cluster, new Transition.Replace(root.Id, Choice(Nodes[1], 1)), cancellationToken));
        Assert.Equal(new LedgerStatus.Closed(new Slot(1)), b.View.Current.Get(root.Id)!.Value.Status);
        _ = await a.View.ReadAsync(cancellationToken);
        Assert.Equal(Fenced, await stuck.WaitAsync(Bound, cancellationToken));
    }

    /// <summary>
    /// ledgers.md §8.2: a primary whose append meets a seal helps the recovery from its known contiguous tail, so a seal
    /// check begun afterwards reads that tail from the replicas, and lists the ledger unserved.
    /// </summary>
    [Fact]
    public async Task APrimaryWhoseAppendMeetsASealHelpsTheRecoveryFromItsTail()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var cluster = new TransitionCluster();
        var a = await cluster.BootstrapAsync(Nodes[0], cancellationToken);
        var root = a.Open(KeyRange.Root);
        var writer = a.Registry.TryGet(root.Id)!;
        Assert.Equal(DurableAt(0), await ProposeAsync(writer, 0x10, cancellationToken));
        Assert.Equal(DurableAt(1), await ProposeAsync(writer, 0x10, cancellationToken));
        var b = await cluster.StartAsync(Nodes[1], cancellationToken);
        using (var lease = b.Reader.LeaseLedger(root))
        {
            Assert.Equal(new Slot(2), (await lease.Ledger.RecoverAsync(cancellationToken)).End);
        }

        var sealedProposal = ProposeAsync(writer, 0x10, cancellationToken);
        await writer.Fenced.WaitAsync(Bound, cancellationToken);

        Assert.Equal([root.Id], await a.Registry.WaitForUnservedAsync(NoneKnown, cancellationToken).WaitAsync(Bound, cancellationToken));
        await using var checker = new LedgerReader(b.View, cluster.Transport, new FakeTimeProvider(), localNode: null);
        var started = TimeProvider.System.GetTimestamp();
        while ((await checker.ReadTailAsync(root, cancellationToken)).Tail.ContiguousTail != new Slot(2))
        {
            Assert.True(TimeProvider.System.GetElapsedTime(started) < Bound, "The primary did not help the recovery.");
            await Task.Delay(TimeSpan.FromMilliseconds(1), TimeProvider.System, cancellationToken);
        }

        Assert.False(sealedProposal.IsCompleted);
        Assert.Equal(Applied(2), await b.ExecuteAsync(cluster, new Transition.Replace(root.Id, Choice(Nodes[1], 1)), cancellationToken));
        _ = await a.View.ReadAsync(cancellationToken);
        Assert.Equal(Fenced, await sealedProposal.WaitAsync(Bound, cancellationToken));
    }

    /// <summary>
    /// ledgers.md §8.3: a primary whose view skips its ledger's close, seeing it no longer listed, answers its in-flight
    /// proposals <see cref="ProposalOutcome.Ambiguous"/>, and its writer leaves the registry.
    /// </summary>
    [Fact]
    public async Task APrimaryWhoseViewSkipsTheCloseAnswersAmbiguous()
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

        var sealedProposal = ProposeAsync(writer, 0x10, cancellationToken);
        await writer.Fenced.WaitAsync(Bound, cancellationToken);
        cluster.Write(new LedgerMapValue(
            3,
            new LedgerId(3),
            0,
            [new LedgerMetadata(new LedgerId(2), KeyRange.Root, Nodes, Window, new PrimaryClaim(Nodes[1], new SequencerInstanceId(Guid.CreateVersion7())), LedgerStatus.Open.Instance)]));
        _ = await a.View.ReadAsync(cancellationToken);

        Assert.Equal(new ProposalOutcome(default(ProposalOutcome.Ambiguous)), await sealedProposal.WaitAsync(Bound, cancellationToken));
        await WaitUntilAsync(() => a.Registry.HeldWriter(writer.Ledger) is null, "the writer leaves");
    }
}
