using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Orleans.Durable.Storage.Control;
using Orleans.Durable.Storage.Engine.Archive;
using Orleans.Durable.Storage.Engine.Archive.Logs;
using Orleans.Durable.Storage.Engine.Archive.Logs.Control;
using Orleans.Durable.Storage.Engine.Archive.Lsm;
using Orleans.Durable.Storage.Engine.Archive.Lsm.Control;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Cluster.Control;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.Ledgers.Control;
using Orleans.Durable.Storage.Engine.Ledgers.Memory;
using Orleans.Durable.Storage.Engine.ObjectStore;
using Orleans.Durable.Storage.Engine.ObjectStore.Memory;
using Orleans.Durable.Storage.Testing;
using static Orleans.Durable.Storage.Tests.NodeRegistrySamples;

namespace Orleans.Durable.Storage.Tests;

/// <summary>
/// A node's control work (orleans.md §5.6): it declines a transition another node executes or which reaches it retired,
/// submits a change only while its node is active (OR-I3), brings its view to the epoch a report reply names (OR-I19),
/// reports a closed ledger's drain progress until the ledger leaves the map, and answers a compaction assignment with a
/// reply: nothing to compact, stale after a fresh read, or the partition's corruption, none of which submits a change,
/// while the manifest's own corruption fails the call.
/// </summary>
public sealed partial class NodeControllerTests : IAsyncLifetime
{
    private const int Generation = 3;

    private static readonly TimeSpan LoadWindow = TimeSpan.FromMinutes(1);

    private static readonly ArchiveOptions Archive = new() { GridBits = 1, MaxSectionsPerPartition = 2 };
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();
    private readonly ScriptedConditionalRegister _register = new();
    private readonly InMemoryObjectStore _objects = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    private LedgerMapCommitter _committer = null!;
    private LedgerMapView _view = null!;
    private LedgerReader _reader = null!;
    private LedgerWriterRegistry _registry = null!;
    private ManifestView _manifest = null!;
    private ManifestCommitter _manifestCommitter = null!;
    private int _commits;
    private bool _active = true;
    private int _activeChecks;
    private int _inactiveAfter = int.MaxValue;
    private IReadOnlyDictionary<NodeId, int> _dead = ImmutableDictionary<NodeId, int>.Empty;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _committer = new LedgerMapCommitter(_register, 1 << 20);
        _view = await LedgerMapView.OpenAsync(_register, Token);
        _reader = new LedgerReader(_view, new UnopenedTransport(), _time, Node(1));
        _registry = new LedgerWriterRegistry(_reader, 1 << 20, LedgerWriterOptions.Default, _time, NullLoggerFactory.Instance);
        _manifest = new ManifestView(_objects);
        _manifestCommitter = new ManifestCommitter(_objects, _manifest, _time, TimeSpan.Zero);
    }

    public async ValueTask DisposeAsync()
    {
        await _registry.DisposeAsync();
        await _reader.DisposeAsync();
        await _committer.DisposeAsync();
        await _manifestCommitter.DisposeAsync();
        _leaks.Dispose();
    }

    /// <summary>A transition another node executes, or one which reaches a retired node, is declined with nothing submitted.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ATransitionForAnotherNodeOrARetiredOneIsDeclined(bool retired)
    {
        _active = !retired;
        var executor = retired ? Node(1) : Node(2);

        var reply = await Controller().ExecuteAsync(new Transition.Bootstrap(Choice(executor)), CommitAsync, Token);

        Assert.Equal(TransitionReply.Declined.Instance, reply.Value);
        Assert.Equal(0, _commits);
    }

    /// <summary>
    /// orleans.md §5.2: once its runtime begins to hand off, a node declines a transition it would otherwise execute, while
    /// its admission is still open, and submits nothing.
    /// </summary>
    [Fact]
    public async Task ANodeHandingOffDeclinesATransitionWhileItsAdmissionIsOpen()
    {
        var control = new LocalNodeControl();
        control.Serve(Controller(), TaskScheduler.Default, Token);
        control.BeginHandOff();

        var reply = await control.ExecuteAsync(Node(1), new Transition.Bootstrap(Choice(Node(1))), CommitAsync);

        Assert.Equal(TransitionReply.Declined.Instance, reply);
        Assert.Equal(0, _commits);
        Assert.NotNull(await control.StartDrainsAsync([], CommitManifestAsync, static (_, _) => { }));
    }

    /// <summary>
    /// OR-I3: a node retired while it executes a transition, after the transition reached it active, submits nothing and
    /// answers <c>Refused</c>, abandoning the lifetimes it created.
    /// </summary>
    [Fact]
    public async Task ANodeRetiredWhileItExecutesSubmitsNothing()
    {
        _inactiveAfter = 1;

        var reply = await Controller().ExecuteAsync(new Transition.Bootstrap(Choice(Node(1))), CommitAsync, Token);

        Assert.Equal(LedgerMapCommitOutcome.Refused.Instance, reply.Value);
        Assert.Equal(0, _commits);
        Assert.Equal(2, _activeChecks);
    }

    /// <summary>OR-I19: a report reply's epoch brings the node's view to it; an older one reads nothing.</summary>
    [Fact]
    public async Task AReplysEpochBringsTheViewToIt()
    {
        var node = Controller();
        _ = await InstallAsync(Opening(KeyRange.Root, Node(1), Node(2), Node(3)));
        Assert.Equal(0ul, _view.Current.Epoch);

        await node.ObserveAsync(new NodeReportReply(1, [], ArchiveLogShare: null), Token);
        Assert.Equal(1ul, _view.Current.Epoch);
        var reads = _register.Reads;
        await node.ObserveAsync(new NodeReportReply(0, [], ArchiveLogShare: null), Token);
        Assert.Equal(reads, _register.Reads);
    }

    /// <summary>
    /// orleans.md §5.6: an applied drain of a closed ledger records its progress, which the node reports until the map
    /// drops the ledger; each report names the node's silo generation, and carries the disk pressure and the lost replicas
    /// its host gives it.
    /// </summary>
    [Fact]
    public async Task AClosedLedgersDrainProgressIsReportedUntilItIsDropped()
    {
        var node = Controller(lsm: true);
        var root = await InstallAsync(Opening(KeyRange.Root, Node(1), Node(2), Node(3)));
        _ = await CommitMapAsync(new LedgerMapChange.Install([new LedgerClosing(root.Id, Slot.Zero)], [Opening(KeyRange.Root, Node(2), Node(1), Node(3))], 0));
        var closed = Assert.Single(_view.Current.Ledgers, static ledger => !ledger.IsOpen);

        var outcome = await node.DrainAsync(closed, CommitManifestAsync, Token);

        _ = Assert.IsType<DrainOutcome.Drained>(outcome.Value);
        var report = node.Report(diskPressure: true, [closed.Id], handingOff: false, refused: false, archiveGatePassed: true, draining: true);
        Assert.Equal((Generation, _time.GetUtcNow()), (report.Generation, report.ClosedDrainProgress[closed.Id]));
        Assert.True(report.DiskPressure);
        Assert.Equal([closed.Id], report.LostReplicas);
        _ = await CommitMapAsync(new LedgerMapChange.Drop(closed.Id));
        Assert.Empty(node.Report(diskPressure: false, [], handingOff: false, refused: false, archiveGatePassed: true, draining: true).ClosedDrainProgress);
    }

    /// <summary>
    /// orleans.md §5.6: the drains due take closed ledgers first, each whose first replica is the node or which the
    /// controller names, until the node's manifest view shows it complete; and an open ledger the node is primary of only
    /// once its writer here closed a segment below the tail it knows, so one the node does not serve is not due.
    /// </summary>
    [Fact]
    public async Task DrainsDueTakeClosedLedgersFirstAndOpenOnesOnlyWithAClosedSegment()
    {
        var node = Controller(lsm: true);
        var root = await InstallAsync(Opening(KeyRange.Root, Node(1), Node(2), Node(3)));
        var (lowerRange, upperRange) = KeyRange.Root.Children;
        _ = await CommitMapAsync(new LedgerMapChange.Install([new LedgerClosing(root.Id, Slot.Zero)], [Opening(lowerRange, Node(1), Node(2), Node(3)), Opening(upperRange, Node(2), Node(1), Node(3))], 0));
        var upper = _view.Current.Ledgers.Single(ledger => ledger.IsOpen && ledger.Range == upperRange);
        _ = await CommitMapAsync(new LedgerMapChange.Install([new LedgerClosing(upper.Id, Slot.Zero)], [Opening(upperRange, Node(3), Node(1), Node(2))], 0));

        Assert.Equal([root.Id], node.DrainsDue([]).Select(static ledger => ledger.Id));
        Assert.Equal([root.Id, upper.Id], node.DrainsDue([upper.Id]).Select(static ledger => ledger.Id));

        _ = await node.DrainAsync(Assert.Single(_view.Current.Ledgers, ledger => ledger.Id == root.Id), CommitManifestAsync, Token);
        Assert.Equal([upper.Id], node.DrainsDue([upper.Id]).Select(static ledger => ledger.Id));
    }

    /// <summary>
    /// orleans.md §5.6: an open segment's start closes the segment before it for a drain only once it lies below the
    /// contiguous tail, so a writer whose tail reaches the start exactly, the start batch not yet known decided, owes no
    /// drain; nor does one whose open segment starts at the watermark.
    /// </summary>
    [Theory]
    [InlineData(4, 4, false)]
    [InlineData(4, 5, true)]
    [InlineData(0, 5, false)]
    public void AnOpenSegmentIsNotDueAgainUntilItsStartIsBelowTheTail(long openSegmentStart, long tail, bool due) =>
        Assert.Equal(due, NodeController.ClosesASegment(Slot.Zero, new Slot(openSegmentStart), new Slot(tail)));

    /// <summary>
    /// orleans.md §5.9: the node observes each open ledger it is primary of and serves: the applied slot the Journals
    /// give, the contiguous tail its writer knows, the watermark its drains reached, and the bytes and oldest
    /// batch's age its writer proposed at or above that watermark, which a drain of the closed segment lowers; the drain
    /// round, not the observation, lets the writer forget the drained segment.
    /// </summary>
    [Fact]
    public async Task APrimaryIsObservedWithItsWatermarkAndWhatItsWriterProposedAboveIt()
    {
        await using var logs = await StartLogsNodeAsync();
        var ledger = await logs.BootstrapAsync(this);
        await logs.ShareAsync(this);
        var writer = logs.Writer(ledger);
        await logs.ProposeAsync(ledger, 0x10);
        var first = writer.ProposedBytes;
        _time.Advance(TimeSpan.FromMinutes(2));
        _ = Assert.IsType<ProposalOutcome.Durable>((await writer.EndSegmentAsync(Token)).Value);
        await logs.ProposeAsync(ledger, 0x11);
        _time.Advance(TimeSpan.FromMinutes(1));

        Slot? AppliedBelow(LedgerId observed)
        {
            Assert.Equal(ledger.Id, observed);
            return new Slot(1);
        }

        var before = Assert.Single(logs.Node.ObservePrimaries(AppliedBelow));
        Assert.Equal(
            new PrimaryObservation(ledger.Id, new Slot(1), writer.KnownContiguousTail, Slot.Zero, new LedgerUndrained(writer.ProposedBytes, TimeSpan.FromMinutes(3))),
            before);
        _ = logs.Node.ObservePrimaries(AppliedBelow);
        Assert.Equal(1, writer.EndedSegments);

        Assert.Empty(await logs.DrainAsync([], CommitAsync));
        Assert.Equal(0, writer.EndedSegments);
        Assert.Equal(
            new PrimaryObservation(ledger.Id, new Slot(1), writer.KnownContiguousTail, new Slot(1), new LedgerUndrained(writer.ProposedBytes - first, TimeSpan.FromMinutes(1))),
            Assert.Single(logs.Node.ObservePrimaries(AppliedBelow)));
    }

    /// <summary>
    /// orleans.md §5.6: a report carries the load of each ledger the node is primary of and serves once its writer was
    /// measured for a whole <c>LoadWindow</c>: the bytes the writer proposed since the window's first sample, per second.
    /// </summary>
    [Fact]
    public async Task AReportCarriesTheProposedBytesPerSecondOfEachServedPrimaryLedger()
    {
        using var transport = new InMemoryLedgerReplicaTransport();
        await using var reader = new LedgerReader(_view, transport, _time, Node(1));
        await using var registry = new LedgerWriterRegistry(reader, 1 << 20, LedgerWriterOptions.Default, _time, NullLoggerFactory.Instance);
        var node = Controller(registry: registry);
        _ = Assert.IsType<LedgerMapCommitOutcome.Applied>((await node.ExecuteAsync(new Transition.Bootstrap(Choice(Node(1))), CommitAsync, Token)).Value);
        _ = await _view.ReadAsync(Token);
        var ledger = Assert.Single(_view.Current.Ledgers);
        var writer = registry.TryGet(ledger.Id);
        Assert.NotNull(writer);
        Assert.Empty(node.Report(diskPressure: false, [], handingOff: false, refused: false, archiveGatePassed: true, draining: true).ProposedBytesPerSecond);

        var before = writer.ProposedBytes;
        for (var batch = 0; batch < 3; batch++)
        {
            using var body = ArcBuffer.Wrap(new byte[30_000]);
            using var row = new Row([0x10], RowKind.Append, body);
            _ = Assert.IsType<ProposalOutcome.Durable>((await writer.ProposeAsync([row], Token)).Value);
        }

        _time.Advance(LoadWindow);
        var load = node.Report(diskPressure: false, [], handingOff: false, refused: false, archiveGatePassed: true, draining: true).ProposedBytesPerSecond;

        Assert.Equal(ledger.Id, Assert.Single(load).Key);
        Assert.Equal((long)((writer.ProposedBytes - before) / LoadWindow.TotalSeconds), load[ledger.Id]);
        Assert.InRange(load[ledger.Id], 1_500, long.MaxValue);
    }

    /// <summary>
    /// orleans.md §5.6: on archive logs, a node drains nothing before a reply assigned it a share, whatever it owes; its
    /// first round builds its drain worker over the gate; a reply's share sets the worker's rate, a reply without one
    /// keeping it; and the node's drains run over the one gate its silo passed.
    /// </summary>
    [Fact]
    public async Task ANodeDrainsOnlyAtTheShareAReplyAssigned()
    {
        await using var logs = await StartLogsNodeAsync();
        var ledger = await logs.BootstrapAsync(this);
        await logs.ProposeAsync(ledger, 0x10);
        _ = Assert.IsType<ProposalOutcome.Durable>((await logs.Writer(ledger).EndSegmentAsync(Token)).Value);
        await logs.ProposeAsync(ledger, 0x11);

        Assert.Empty(await logs.DrainAsync([], CommitAsync));
        Assert.Equal<(long, double?)>((1, null), Archived(logs.Node.Report(diskPressure: false, [], handingOff: false, refused: false, archiveGatePassed: true, draining: true)));

        await logs.Node.ObserveAsync(new NodeReportReply(0, [], ArchiveLogShare: 4_000), Token);
        await logs.Node.ObserveAsync(new NodeReportReply(0, [], ArchiveLogShare: null), Token);
        Assert.Equal(4_000, logs.Drains.Worker!.RequestsPerSecond);
        Assert.Empty(await logs.DrainAsync([], CommitAsync));
        Assert.Equal<(long, double?)>((0, 4_000), Archived(logs.Node.Report(diskPressure: false, [], handingOff: false, refused: false, archiveGatePassed: true, draining: true)));
        _ = Assert.Throws<ArgumentException>(() => logs.Drains.Open(logs.OtherGate, _view, logs.Reader));
    }

    /// <summary>
    /// orleans.md §5.6: a start starts the drain of each ledger due whose drain is not in flight, while fewer than the
    /// node's concurrency are in flight: a drain held at its drop's submission holds no other ledger's drain and drop at a
    /// later start, which starts that drain only while a slot is free, and is not started again while it runs.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ASlowDrainHoldsNoOtherLedgersDrainWithinTheConcurrency(int concurrency)
    {
        await using var logs = await StartLogsNodeAsync(drainConcurrency: concurrency);
        await logs.ShareAsync(this);
        var (lower, upper) = await TwoLedgersAsync();
        _ = await CommitMapAsync(new LedgerMapChange.Install([new LedgerClosing(lower.Id, Slot.Zero)], [Opening(lower.Range, Node(2), Node(1), Node(3))], 0));

        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var heldSubmissions = 0;
        async Task<LedgerMapCommitOutcome> HeldAsync(LedgerMapChange change, CancellationToken token)
        {
            _ = Interlocked.Increment(ref heldSubmissions);
            _ = held.TrySetResult();
            await release.Task.WaitAsync(token);
            return await CommitAsync(change, token);
        }

        Assert.Equal([lower.Id], logs.Start([], HeldAsync));
        await held.Task.WaitAsync(Patience, Token);
        _ = await CommitMapAsync(new LedgerMapChange.Install([new LedgerClosing(upper.Id, Slot.Zero)], [Opening(upper.Range, Node(2), Node(1), Node(3))], 0));
        Assert.Equal([lower.Id, upper.Id], logs.Node.DrainsDue([]).Select(static ledger => ledger.Id));

        var upperDropped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<LedgerMapCommitOutcome> DroppingAsync(LedgerMapChange change, CancellationToken token)
        {
            var outcome = await CommitAsync(change, token);
            _ = upperDropped.TrySetResult();
            return outcome;
        }

        var commits = _commits;
        var started = logs.Start([], DroppingAsync);
        if (concurrency == 1)
        {
            Assert.Empty(started);
        }
        else
        {
            Assert.Equal([upper.Id], started);
            await upperDropped.Task.WaitAsync(Patience, Token);
        }

        _ = await _view.ReadAsync(Token);
        Assert.Equal((concurrency - 1, concurrency == 1), (_commits - commits, _view.Current.Get(upper.Id) is not null));
        Assert.NotNull(_view.Current.Get(lower.Id));
        Assert.False(logs.Node.DrainsEnded.IsCompleted);

        release.SetResult();
        await logs.Node.DrainsEnded.WaitAsync(Patience, Token);
        Assert.Empty(await logs.DrainAsync([], CommitAsync));
        _ = await _view.ReadAsync(Token);
        Assert.Equal(1, heldSubmissions);
        Assert.Null(_view.Current.Get(lower.Id));
        Assert.Null(_view.Current.Get(upper.Id));
        Assert.Empty(logs.Failures);
    }

    /// <summary>
    /// orleans.md §5.6: a start leaves a ledger whose drain is in flight: at a concurrency of 2, with one ledger due and its
    /// drain held at its drop's submission, a second start starts nothing, submits nothing, and lists no marker.
    /// </summary>
    [Fact]
    public async Task AStartLeavesALedgerWhoseDrainIsInFlight()
    {
        var objects = new FlakyObjectStore(new InMemoryObjectStore());
        await using var logs = await StartLogsNodeAsync(objects: objects, drainConcurrency: 2);
        await logs.ShareAsync(this);
        var (lower, _) = await TwoLedgersAsync();
        _ = await CommitMapAsync(new LedgerMapChange.Install([new LedgerClosing(lower.Id, Slot.Zero)], [Opening(lower.Range, Node(2), Node(1), Node(3))], 0));
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<LedgerMapCommitOutcome> HeldAsync(LedgerMapChange change, CancellationToken token)
        {
            _ = held.TrySetResult();
            await release.Task.WaitAsync(token);
            return await CommitAsync(change, token);
        }

        Assert.Equal([lower.Id], logs.Start([], HeldAsync));
        await held.Task.WaitAsync(Patience, Token);
        var (commits, lists) = (_commits, objects.Lists);

        Assert.Empty(logs.Start([], CommitAsync));
        Assert.Equal((commits, lists), (_commits, objects.Lists));

        release.SetResult();
        await logs.Node.DrainsEnded.WaitAsync(Patience, Token);
        Assert.Equal(commits + 1, _commits);
    }

    /// <summary>
    /// orleans.md §5.2, §5.6: the node's drains each end on their own, and <c>DrainsEnded</c>, which the stop's closing of
    /// the node's admission awaits, completes only once every drain the node started has ended: a drain the stop's
    /// cancellation ends at once ends neither while another, which outlives the cancellation, still runs; and a drain the
    /// cancellation ended reports no failure.
    /// </summary>
    [Fact]
    public async Task TheStopWaitsForEveryDrainTheNodeStarted()
    {
        using var stopping = new CancellationTokenSource();
        await using var logs = await StartLogsNodeAsync(drainConcurrency: 2);
        await logs.ShareAsync(this);
        var (lower, upper) = await TwoLedgersAsync();
        _ = await CommitMapAsync(new LedgerMapChange.Install([new LedgerClosing(lower.Id, Slot.Zero), new LedgerClosing(upper.Id, Slot.Zero)], [Opening(lower.Range, Node(2), Node(1), Node(3)), Opening(upper.Range, Node(2), Node(1), Node(3))], 0));
        var (lowerHeld, lowerEnded, upperHeld, release) = (NewSignal(), NewSignal(), NewSignal(), NewSignal());
        async Task<LedgerMapCommitOutcome> HeldAsync(LedgerMapChange change, CancellationToken token)
        {
            if (change.Value is LedgerMapChange.Drop { Ledger: var ledger } && ledger == lower.Id)
            {
                try
                {
                    _ = lowerHeld.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, TimeProvider.System, token);
                }
                finally
                {
                    _ = lowerEnded.TrySetResult();
                }
            }

            _ = upperHeld.TrySetResult();
            await release.Task;
            return await CommitAsync(change, CancellationToken.None);
        }

        var failures = new ConcurrentQueue<(LedgerId, Exception)>();
        var control = new LocalNodeControl();
        control.Serve(logs.Node, TaskScheduler.Default, stopping.Token);
        Assert.Equal(2, (await control.StartLogDrainsAsync(logs.Gate, [], HeldAsync, (ledger, failure) => failures.Enqueue((ledger, failure))))?.Length);
        await Task.WhenAll(lowerHeld.Task, upperHeld.Task).WaitAsync(Patience, Token);

        await stopping.CancelAsync();
        control.Stop();
        var closing = control.CloseAdmissionAsync();
        await lowerEnded.Task.WaitAsync(Patience, Token);
        Assert.False(closing.IsCompleted);
        Assert.False(logs.Node.DrainsEnded.IsCompleted);

        release.SetResult();
        await closing.WaitAsync(Patience, Token);
        Assert.True(logs.Node.DrainsEnded.IsCompleted);
        Assert.Empty(failures);

        static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>
    /// orleans.md §5.6: a drain which ends starts again at a later start only after the due ledgers started less recently:
    /// at a concurrency of 1, two closed ledgers whose drops are refused, so both stay due, take turns, and the node forgets
    /// a ledger's last start once its report finds the ledger dropped.
    /// </summary>
    [Fact]
    public async Task DueLedgersTakeTurnsLeastRecentlyStartedFirst()
    {
        await using var logs = await StartLogsNodeAsync();
        await logs.ShareAsync(this);
        var (lower, upper) = await TwoLedgersAsync();
        _ = await CommitMapAsync(new LedgerMapChange.Install([new LedgerClosing(lower.Id, Slot.Zero), new LedgerClosing(upper.Id, Slot.Zero)], [Opening(lower.Range, Node(2), Node(1), Node(3)), Opening(upper.Range, Node(2), Node(1), Node(3))], 0));
        var submitted = new ConcurrentQueue<LedgerId>();
        Task<LedgerMapCommitOutcome> RefusedAsync(LedgerMapChange change, CancellationToken token)
        {
            submitted.Enqueue(Assert.IsType<LedgerMapChange.Drop>(change.Value).Ledger);
            return Task.FromResult<LedgerMapCommitOutcome>(LedgerMapCommitOutcome.Refused.Instance);
        }

        var started = new List<LedgerId>();
        for (var start = 0; start < 4; start++)
        {
            started.Add(Assert.Single(logs.Start([], RefusedAsync)));
            await logs.Node.DrainsEnded.WaitAsync(Patience, Token);
        }

        Assert.Equal([lower.Id, upper.Id, lower.Id, upper.Id], started);
        Assert.Equal(started, submitted);
        Assert.Equal([lower.Id, upper.Id], logs.Node.StartedLedgers.Order());

        Assert.Equal([lower.Id], logs.Start([], CommitAsync));
        await logs.Node.DrainsEnded.WaitAsync(Patience, Token);
        _ = await _view.ReadAsync(Token);
        Assert.Null(_view.Current.Get(lower.Id));
        _ = logs.Node.Report(diskPressure: false, [], handingOff: false, refused: false, archiveGatePassed: true, draining: true);
        Assert.Equal([upper.Id], logs.Node.StartedLedgers);
        Assert.Empty(logs.Failures);
    }

    /// <summary>
    /// orleans.md §5.6: a node counts the closed segments of the ledgers it owes a drain by the keys its writer recorded,
    /// the last segment once its writer gives it at the close, and its drains lower the count; the closed ledger's complete
    /// drain submits its drop, and the ledger counts nothing while the drop is pending, its submission refused or its writer
    /// gave a segment late.
    /// </summary>
    [Fact]
    public async Task ANodeCountsItsWritersSegmentsUntilItsDrainsCompleteThemAndNothingWhileTheDropIsPending()
    {
        await using var logs = await StartLogsNodeAsync();
        var ledger = await logs.BootstrapAsync(this);
        await logs.ShareAsync(this);
        await logs.ProposeAsync(ledger, 0x10, 0x11);
        _ = Assert.IsType<ProposalOutcome.Durable>((await logs.Writer(ledger).EndSegmentAsync(Token)).Value);
        await logs.ProposeAsync(ledger, 0x12);
        Assert.Equal(2, logs.Node.Report(diskPressure: false, [], handingOff: false, refused: false, archiveGatePassed: true, draining: true).UndrainedKeys);

        Assert.Empty(await logs.DrainAsync([], CommitAsync));
        Assert.Equal(0, logs.Node.Report(diskPressure: false, [], handingOff: false, refused: false, archiveGatePassed: true, draining: true).UndrainedKeys);

        // The ledger closes at its tail, and its last segment, which its writer gives at its disposal, counts.
        var writer = logs.Writer(ledger);
        var t = writer.KnownContiguousTail;
        _ = await CommitMapAsync(new LedgerMapChange.Install([new LedgerClosing(ledger.Id, t)], [Opening(KeyRange.Root, Node(2), Node(1), Node(3))], 0));
        await writer.DisposeAsync();
        Assert.Equal(1, logs.Node.Report(diskPressure: false, [], handingOff: false, refused: false, archiveGatePassed: true, draining: true).UndrainedKeys);

        Assert.Empty(await logs.DrainAsync([], static (_, _) => Task.FromResult<LedgerMapCommitOutcome>(LedgerMapCommitOutcome.Refused.Instance)));
        logs.Drains.OnSegmentEnded(new LedgerSegmentKeys(new LedgerSegmentId(ledger.Id, new Slot(1)), 1));
        Assert.Equal(0, logs.Node.Report(diskPressure: false, [], handingOff: false, refused: false, archiveGatePassed: true, draining: true).UndrainedKeys);

        var commits = _commits;
        Assert.Empty(await logs.DrainAsync([], CommitAsync));
        Assert.Equal(commits + 1, _commits);

        // The view still lists the dropped ledger: the node neither drains nor drops it again, and counts nothing for it.
        Assert.NotNull(_view.Current.Get(ledger.Id));
        Assert.Empty(await logs.DrainAsync([], CommitAsync));
        Assert.Equal(commits + 1, _commits);
        Assert.Equal(0, logs.Node.Report(diskPressure: false, [], handingOff: false, refused: false, archiveGatePassed: true, draining: true).UndrainedKeys);
        _ = await _view.ReadAsync(Token);
        Assert.DoesNotContain(_view.Current.Ledgers, other => other.Id == ledger.Id);
        var report = logs.Node.Report(diskPressure: false, [], handingOff: false, refused: false, archiveGatePassed: true, draining: true);
        Assert.Equal((0L, 0), (report.UndrainedKeys, report.ClosedDrainProgress.Count));
    }

    /// <summary>
    /// orleans.md §5.6: a drop whose submission throws is the drain's failure, logged and backed off as any other: the
    /// ledger is not drained again until its backoff passes.
    /// </summary>
    [Fact]
    public async Task AFailedDropSubmissionIsLoggedAndBackedOffAsADrainsFailure()
    {
        await using var logs = await StartLogsNodeAsync();
        await logs.ShareAsync(this);
        var (lower, _) = await TwoLedgersAsync();
        _ = await CommitMapAsync(new LedgerMapChange.Install([new LedgerClosing(lower.Id, Slot.Zero)], [Opening(lower.Range, Node(2), Node(1), Node(3))], 0));

        _ = Assert.IsType<IOException>(Assert.Single(await logs.DrainAsync([], static (_, _) => throw new IOException("The drop's submission failed."))).Failure);

        Assert.False(logs.Drains.Drains(lower.Id));
        Assert.Contains(logs.Log.Entries, entry => entry.Level == LogLevel.Warning && entry.Message.Contains($"drain of ledger {lower.Id} failed;", StringComparison.Ordinal));
    }

    /// <summary>
    /// orleans.md §5.6: a ledger counts no undrained keys only while its last drain answered <c>Waiting</c>: once the older
    /// ledger dropped and a drain of it failed, it counts its segment again.
    /// </summary>
    [Fact]
    public async Task AWaitingLedgerCountsAgainOnceItsDrainAnswersOtherwise()
    {
        var objects = new FlakyObjectStore(new InMemoryObjectStore());
        await using var logs = await StartLogsNodeAsync(objects: objects);
        await logs.ShareAsync(this);
        var older = await InstallAsync(Opening(KeyRange.Root, Node(2), Node(1), Node(3)));
        _ = await CommitMapAsync(new LedgerMapChange.Install([new LedgerClosing(older.Id, Slot.Zero)], [Opening(KeyRange.Root, Node(1), Node(2), Node(3))], 0));
        var waiting = Assert.Single(_view.Current.Ledgers, static ledger => ledger.IsOpen);
        _ = await CommitMapAsync(new LedgerMapChange.Install([new LedgerClosing(waiting.Id, Slot.Zero)], [Opening(KeyRange.Root, Node(2), Node(1), Node(3))], 0));
        Assert.Empty(await logs.DrainAsync([], CommitAsync));
        Assert.Equal(0, Undrained());

        _ = await CommitMapAsync(new LedgerMapChange.Drop(older.Id));
        objects.FailsLists = true;
        _ = Assert.IsType<IOException>(Assert.Single(await logs.DrainAsync([], CommitAsync)).Failure);
        Assert.Equal(ArchiveLogNodeDrains.DefaultSegmentKeys, Undrained());

        long Undrained() => logs.Node.Report(diskPressure: false, [], handingOff: false, refused: false, archiveGatePassed: true, draining: true).UndrainedKeys;
    }

    /// <summary>
    /// orleans.md §5.6: a node counts a closed ledger it drains too, which another node wrote, as one segment of the
    /// default count, having learned none, from the reply which names it until its drain completes; before, it owes it
    /// nothing.
    /// </summary>
    [Fact]
    public async Task ASecondDrainerCountsTheLedgerItDrainsTooAsOneSegmentUntilItsDrainCompletes()
    {
        await using var logs = await StartLogsNodeAsync();
        await logs.ShareAsync(this);
        var root = await InstallAsync(Opening(KeyRange.Root, Node(2), Node(1), Node(3)));
        _ = await CommitMapAsync(new LedgerMapChange.Install([new LedgerClosing(root.Id, Slot.Zero)], [Opening(KeyRange.Root, Node(2), Node(1), Node(3))], 0));
        Assert.Equal(0, logs.Node.Report(diskPressure: false, [], handingOff: false, refused: false, archiveGatePassed: true, draining: true).UndrainedKeys);

        await logs.Node.ObserveAsync(new NodeReportReply(_view.Current.Epoch, [root.Id], ArchiveLogShare: null), Token);
        Assert.Equal(ArchiveLogNodeDrains.DefaultSegmentKeys, logs.Node.Report(diskPressure: false, [], handingOff: false, refused: false, archiveGatePassed: true, draining: true).UndrainedKeys);

        Assert.Empty(await logs.DrainAsync([root.Id], CommitAsync));
        Assert.Equal(0, logs.Node.Report(diskPressure: false, [], handingOff: false, refused: false, archiveGatePassed: true, draining: true).UndrainedKeys);
    }

    /// <summary>
    /// orleans.md §5.6: a node restarted over a closed ledger it owes, whose writer's counts its process lost, counts it as
    /// one segment of the default count, having learned none, until its drain's scan learns the segment's keys.
    /// </summary>
    [Fact]
    public async Task ARestartedNodeCountsItsLedgerAsOneSegmentUntilItsScanLearnsIt()
    {
        await using var logs = await StartLogsNodeAsync();
        var ledger = await logs.BootstrapAsync(this);
        await logs.ProposeAsync(ledger, 0x10, 0x11, 0x12);
        _ = Assert.IsType<ProposalOutcome.Durable>((await logs.Writer(ledger).EndSegmentAsync(Token)).Value);
        await logs.ProposeAsync(ledger, 0x13);

        var t = logs.Writer(ledger).KnownContiguousTail;
        _ = await CommitMapAsync(new LedgerMapChange.Install([new LedgerClosing(ledger.Id, t)], [Opening(KeyRange.Root, Node(2), Node(1), Node(3))], 0));
        var restarted = logs.Restart();
        await restarted.ShareAsync(this);
        Assert.Equal(ArchiveLogNodeDrains.DefaultSegmentKeys, restarted.Node.Report(diskPressure: false, [], handingOff: false, refused: false, archiveGatePassed: true, draining: true).UndrainedKeys);

        logs.Store.Hold();
        var round = restarted.DrainAsync([], CommitAsync);
        await logs.Store.HeldAsync.WaitAsync(Patience, Token);
        Assert.Equal(3, restarted.Node.Report(diskPressure: false, [], handingOff: false, refused: false, archiveGatePassed: true, draining: true).UndrainedKeys);
        logs.Store.Release();
        Assert.Empty(await round);
        Assert.Equal(0, restarted.Node.Report(diskPressure: false, [], handingOff: false, refused: false, archiveGatePassed: true, draining: true).UndrainedKeys);
    }

    /// <summary>
    /// orleans.md §5.9, §8: a drain a refused write blocks is counted in <c>orleans.durable.storage.archive.logs.drain.blocked</c> and logged
    /// once (6244) when it starts to block at a segment, cause, and key; it is retried only once its retry interval passed on
    /// the drains' clock, not a tick before (#384), and logged again when it blocks at another segment; a drain which passes the
    /// block clears the gauge.
    /// </summary>
    [Fact]
    public async Task ABlockedDrainIsCountedAndLoggedOncePerBlockUntilADrainPassesIt()
    {
        await using var logs = await StartLogsNodeAsync(drainsTime: new ImmediateWaits(_time));
        var ledger = await logs.BootstrapAsync(this);
        await logs.ShareAsync(this);
        await logs.ProposeAsync(ledger, 0x10);
        _ = Assert.IsType<ProposalOutcome.Durable>((await logs.Writer(ledger).EndSegmentAsync(Token)).Value);
        await logs.ProposeAsync(ledger, 0x11);
        _ = Assert.IsType<ProposalOutcome.Durable>((await logs.Writer(ledger).EndSegmentAsync(Token)).Value);
        await logs.ProposeAsync(ledger, 0x12);

        logs.Store.Refuse(0x10);
        Assert.Equal(ArchiveLogDrainBlockedCause.Refused, Blocked(await logs.DrainAsync([], CommitAsync)).Cause);
        Assert.Empty(await logs.DrainAsync([], CommitAsync));
        _time.Advance(ArchiveLogNodeDrains.BlockedRetryInterval - TimeSpan.FromTicks(1));
        Assert.Empty(await logs.DrainAsync([], CommitAsync));
        _time.Advance(TimeSpan.FromTicks(1));
        _ = Blocked(await logs.DrainAsync([], CommitAsync));
        Assert.Equal(1, logs.Criticals);
        Assert.Equal(new Dictionary<(string, string), int> { [(ledger.Id.ToString(), "refused")] = 1 }, logs.Gauge());

        // The block moves to the next segment: logged again.
        logs.Store.Refuse(0x11);
        _time.Advance(ArchiveLogNodeDrains.BlockedRetryInterval);
        Assert.Equal(new Slot(1), Blocked(await logs.DrainAsync([], CommitAsync)).Segment.Start);
        Assert.Equal(2, logs.Criticals);

        logs.Store.Refuse();
        _time.Advance(ArchiveLogNodeDrains.BlockedRetryInterval);
        Assert.Empty(await logs.DrainAsync([], CommitAsync));
        Assert.Empty(logs.Drains.Blocked);
        Assert.Empty(logs.Gauge());
        Assert.Equal(2, logs.Criticals);
    }

    /// <summary>
    /// orleans.md §5.6: a drain which fails without a block, here its listing of the ledger's drain markers, backs its
    /// ledger off, from a second after its first failure, doubling with each failure after it, each wait jittered up to
    /// twice its step: the ledger is not drained again until its backoff passed. The first failure is logged with its exception and each later one with the count since the
    /// drain last answered; a drain which answers starts the backoff over.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task AFailingDrainBacksItsLedgerOffUntilItAnswers()
    {
        var objects = new FlakyObjectStore(new InMemoryObjectStore());
        await using var logs = await StartLogsNodeAsync(drainsTime: _time, objects: objects);
        var ledger = await logs.BootstrapAsync(this);
        await logs.Node.ObserveAsync(new NodeReportReply(_view.Current.Epoch, [], ArchiveLogShare: 1_000_000), Token);
        await logs.ProposeAsync(ledger, 0x10);
        _ = Assert.IsType<ProposalOutcome.Durable>((await logs.Writer(ledger).EndSegmentAsync(Token)).Value);
        await logs.ProposeAsync(ledger, 0x11);
        objects.FailsLists = true;

        _ = Assert.IsType<IOException>(Assert.Single(await logs.DrainAsync([], CommitAsync)).Failure);
        Assert.False(logs.Drains.Drains(ledger.Id));
        var first = ArchiveLogNodeDrains.FirstFailureBackoff;
        _time.Advance(first - TimeSpan.FromTicks(1));
        Assert.False(logs.Drains.Drains(ledger.Id));
        Assert.Empty(await logs.DrainAsync([], CommitAsync));
        _time.Advance(first + TimeSpan.FromTicks(1));
        Assert.True(logs.Drains.Drains(ledger.Id));

        _ = Assert.IsType<IOException>(Assert.Single(await logs.DrainAsync([], CommitAsync)).Failure);
        _time.Advance(2 * first - TimeSpan.FromTicks(1));
        Assert.False(logs.Drains.Drains(ledger.Id));
        _time.Advance(2 * first + TimeSpan.FromTicks(1));

        // The drain answers, so the next failure backs off from a second again, and is logged as a first.
        objects.FailsLists = false;
        var round = logs.DrainAsync([], CommitAsync);
        var started = TimeProvider.System.GetTimestamp();
        while (!round.IsCompleted)
        {
            Assert.True(TimeProvider.System.GetElapsedTime(started) < Patience, $"The drain round did not answer within {Patience}.");

            // The drain's pacer waits on the test's clock between its requests.
            _time.Advance(TimeSpan.FromMilliseconds(1));
            await Task.Delay(TimeSpan.FromMilliseconds(1), TimeProvider.System, TestContext.Current.CancellationToken);
        }

        Assert.Empty(await round);
        Assert.Equal(new Slot(1), logs.Drains.DrainedBelow(ledger.Id));
        logs.Drains.Fail(ledger.Id, new IOException("The test's store fails again."));
        _time.Advance(2 * first);
        Assert.True(logs.Drains.Drains(ledger.Id));
        Assert.Equal(
            ["failed;", "failed again, 2 times", "failed;"],
            logs.Log.Entries.Where(static entry => entry.Level == LogLevel.Warning).Select(static entry => entry.Message.Contains("failed again, 2 times", StringComparison.Ordinal) ? "failed again, 2 times" : entry.Message.Contains("failed;", StringComparison.Ordinal) ? "failed;" : entry.Message));
    }

    /// <summary>
    /// orleans.md §5.9: a node blocked on a closed ledger clears its gauge once another node's drain dropped the ledger and
    /// its map view no longer lists it.
    /// </summary>
    [Fact]
    public async Task TheBlockedGaugeClearsOnceAnotherNodeDropsTheLedger()
    {
        await using var logs = await StartLogsNodeAsync();
        var ledger = await logs.BootstrapAsync(this);
        await logs.ShareAsync(this);
        await logs.ProposeAsync(ledger, 0x10);
        var t = logs.Writer(ledger).KnownContiguousTail;
        _ = await CommitMapAsync(new LedgerMapChange.Install([new LedgerClosing(ledger.Id, t)], [Opening(KeyRange.Root, Node(2), Node(1), Node(3))], 0));
        logs.Store.Refuse(0x10);
        _ = Blocked(await logs.DrainAsync([], CommitAsync));
        _ = Assert.Single(logs.Gauge());

        // Another drainer completed the ledger, whose drop the map now shows.
        _ = await CommitMapAsync(new LedgerMapChange.Drop(ledger.Id));
        _ = logs.Node.Report(diskPressure: false, [], handingOff: false, refused: false, archiveGatePassed: true, draining: true);
        Assert.Empty(logs.Drains.Blocked);
        Assert.Empty(logs.Gauge());
    }

    /// <summary>
    /// orleans.md §5.6, OR-I22: a node whose reports hang, so no reply reaches it and it sends no other report, falls to
    /// its floor <c>ReplyIntervals</c> control intervals after its last share, where its drains read the rate, before each
    /// request, and not where a report is built; a reply which assigns no share does not renew the deadline; the floor is
    /// over the node's current registry view; and the next share replaces it.
    /// </summary>
    [Fact]
    public async Task ANodeWhoseRepliesStopFallsToItsFloorWhereItsDrainsReadTheRate()
    {
        await using var logs = await StartLogsNodeAsync(drainsTime: _time);
        _ = await logs.BootstrapAsync(this);
        await logs.Node.ObserveAsync(new NodeReportReply(_view.Current.Epoch, [], ArchiveLogShare: 4_000), Token);
        Assert.Empty(await logs.DrainAsync([], CommitAsync));
        Assert.Equal(4_000, logs.Drains.Worker!.RequestsPerSecond);

        _time.Advance(ControlInterval * ArchiveLogRequestShare.ReplyIntervals - TimeSpan.FromTicks(1));
        await logs.Node.ObserveAsync(new NodeReportReply(_view.Current.Epoch, [], ArchiveLogShare: null), Token);
        Assert.Equal(4_000, logs.Drains.Worker.RequestsPerSecond);
        _time.Advance(TimeSpan.FromTicks(1));
        Assert.Equal(ArchiveLogRequestShare.Floor(10_000, 3), logs.Drains.Worker.RequestsPerSecond);

        logs.NodeCount = 200;
        Assert.Equal(ArchiveLogRequestShare.Floor(10_000, 200), logs.Drains.Worker.RequestsPerSecond);
        Assert.Equal(ArchiveLogRequestShare.Floor(10_000, 200), Archived(logs.Node.Report(diskPressure: false, [], handingOff: false, refused: false, archiveGatePassed: true, draining: true)).RequestsPerSecond);

        await logs.Node.ObserveAsync(new NodeReportReply(_view.Current.Epoch, [], ArchiveLogShare: 300), Token);
        Assert.Equal(300, logs.Drains.Worker.RequestsPerSecond);
    }

    /// <summary>
    /// orleans.md §5.6, OR-I21: a node whose silo refused its archive store or its Journals reports no undrained keys and no
    /// rate, though its writers counted keys it owes, so the controller's shares leave it out.
    /// </summary>
    [Fact]
    public async Task ARefusedNodeReportsNoUndrainedKeysAndNoRate()
    {
        await using var logs = await StartLogsNodeAsync();
        var ledger = await logs.BootstrapAsync(this);
        await logs.ShareAsync(this);
        await logs.ProposeAsync(ledger, 0x10, 0x11);
        _ = Assert.IsType<ProposalOutcome.Durable>((await logs.Writer(ledger).EndSegmentAsync(Token)).Value);
        await logs.ProposeAsync(ledger, 0x12);

        var (keys, rate) = Archived(logs.Node.Report(diskPressure: false, [], handingOff: false, refused: false, archiveGatePassed: true, draining: true));
        Assert.Equal(2, keys);
        Assert.NotNull(rate);
        Assert.Equal((0, null), Archived(logs.Node.Report(diskPressure: false, [], handingOff: true, refused: true, archiveGatePassed: true, draining: true)));
    }

    /// <summary>
    /// orleans.md §5.6: the segments of a ledger the node owes between its drained watermark and the lowest segment a count
    /// covers, as a takeover's or a restarted node's writer leaves them, count as one more segment of the average count the
    /// node learned until a count covers them.
    /// </summary>
    [Fact]
    public async Task SegmentsNoCountCoversBelowTheCountedOnesCountAsOneSegment()
    {
        await using var logs = await StartLogsNodeAsync();
        var ledger = await logs.BootstrapAsync(this);
        logs.Drains.OnSegmentEnded(new LedgerSegmentKeys(new LedgerSegmentId(ledger.Id, new Slot(5)), 2));
        Assert.Equal(2 + 2, logs.Drains.Report(_view.Current, [ledger]).UndrainedKeys);

        logs.Drains.OnSegmentEnded(new LedgerSegmentKeys(new LedgerSegmentId(ledger.Id, Slot.Zero), 3));
        Assert.Equal(5, logs.Drains.Report(_view.Current, [ledger]).UndrainedKeys);
    }

    /// <summary>
    /// orleans.md §5.6: a ledger no count covers counts as one segment of the running average of the counts the node
    /// learned, of any ledger, each moving it a sixteenth of the way to the count, <c>DefaultSegmentKeys</c> before the
    /// first, rounded, at most <c>MaxSegmentKeys</c>, and at least 1, so a restarted node's estimate never takes a segment's
    /// most keys for each ledger it owes.
    /// </summary>
    [Fact]
    public async Task AnUncountedSegmentCountsAsTheAverageOfTheLearnedCountsWithinMaxSegmentKeys()
    {
        await using var logs = await StartLogsNodeAsync();
        var ledger = await logs.BootstrapAsync(this);
        Assert.Equal(ArchiveLogNodeDrains.DefaultSegmentKeys, logs.Drains.Report(_view.Current, [ledger]).UndrainedKeys);

        // The first count starts the average, and each later one moves it a sixteenth of the way: 100, then
        // 100 + (1,700 - 100) / 16 = 200, then 200 + (520 - 200) / 16 = 220.
        var other = new LedgerId(ledger.Id.Value + 100);
        foreach (var (segment, keys, average) in new[] { (0, 100, 100), (1, 1_700, 200), (2, 520, 220) })
        {
            logs.Drains.OnSegmentEnded(new LedgerSegmentKeys(new LedgerSegmentId(other, new Slot(segment)), keys));
            Assert.Equal(average, logs.Drains.Report(_view.Current, [ledger]).UndrainedKeys);
        }

        using var meter = new Meter(DurableStorageMeter.Name);
        var capped = new ArchiveLogNodeDrains(Node(1), budget: 10_000, nodes: static () => 3, maxSegmentKeys: 50, ControlInterval, new ArchiveLogOptions(), _time, meter, NullLogger.Instance);
        Assert.Equal(50, capped.Report(_view.Current, [ledger]).UndrainedKeys);

        // A learned count of 0 enters the average, which counts at least one key.
        capped.OnSegmentEnded(new LedgerSegmentKeys(new LedgerSegmentId(other, Slot.Zero), 0));
        Assert.Equal(1, capped.Report(_view.Current, [ledger]).UndrainedKeys);
    }

    /// <summary>
    /// orleans.md §5.6: a closed ledger the node owes whose drain answers <c>Waiting</c>, an older overlapping ledger still
    /// listed, counts no undrained keys, since they wait for that ledger's drop, and is not drained again while the node's
    /// view lists the older ledger; once the older ledger drops, its drain completes and drops it.
    /// </summary>
    [Fact]
    public async Task ALedgerWhoseDrainWaitsCountsNoUndrainedKeys()
    {
        await using var logs = await StartLogsNodeAsync();
        await logs.ShareAsync(this);
        var older = await InstallAsync(Opening(KeyRange.Root, Node(2), Node(1), Node(3)));
        _ = await CommitMapAsync(new LedgerMapChange.Install([new LedgerClosing(older.Id, Slot.Zero)], [Opening(KeyRange.Root, Node(1), Node(2), Node(3))], 0));
        var waiting = Assert.Single(_view.Current.Ledgers, static ledger => ledger.IsOpen);
        _ = await CommitMapAsync(new LedgerMapChange.Install([new LedgerClosing(waiting.Id, Slot.Zero)], [Opening(KeyRange.Root, Node(2), Node(1), Node(3))], 0));
        Assert.Equal(ArchiveLogNodeDrains.DefaultSegmentKeys, logs.Node.Report(diskPressure: false, [], handingOff: false, refused: false, archiveGatePassed: true, draining: true).UndrainedKeys);

        Assert.Empty(await logs.DrainAsync([], CommitAsync));
        Assert.Equal(0, logs.Node.Report(diskPressure: false, [], handingOff: false, refused: false, archiveGatePassed: true, draining: true).UndrainedKeys);
        Assert.NotNull(_view.Current.Get(waiting.Id));

        // The ledger is not drained again while the older ledger it waits for is listed.
        Assert.False(logs.Drains.Drains(waiting.Id));
        Assert.Empty(logs.Start([], CommitAsync));

        _ = await CommitMapAsync(new LedgerMapChange.Drop(older.Id));
        Assert.True(logs.Drains.Drains(waiting.Id));
        Assert.Empty(await logs.DrainAsync([], CommitAsync));
        _ = await _view.ReadAsync(Token);
        Assert.Null(_view.Current.Get(waiting.Id));
    }

    /// <summary>
    /// orleans.md §5.9: a blocked drain is the node's to report only while it owes the ledger's drain: once it no longer
    /// does, as when the controller withdraws a drain-too or the ledger moves to another drainer, its report clears the
    /// block and the gauge.
    /// </summary>
    [Fact]
    public async Task TheBlockedGaugeClearsOnceTheNodeNoLongerOwesTheLedger()
    {
        await using var logs = await StartLogsNodeAsync();
        var ledger = await logs.BootstrapAsync(this);
        await logs.ShareAsync(this);
        await logs.ProposeAsync(ledger, 0x10);
        _ = Assert.IsType<ProposalOutcome.Durable>((await logs.Writer(ledger).EndSegmentAsync(Token)).Value);
        await logs.ProposeAsync(ledger, 0x11);
        logs.Store.Refuse(0x10);
        _ = Blocked(await logs.DrainAsync([], CommitAsync));
        _ = Assert.Single(logs.Gauge());

        _ = logs.Drains.Report(_view.Current, [ledger]);
        _ = Assert.Single(logs.Gauge());
        _ = logs.Drains.Report(_view.Current, []);
        Assert.Empty(logs.Drains.Blocked);
        Assert.Empty(logs.Gauge());
    }

    /// <summary>The archive-log drains' control interval in these tests, at which a node reports.</summary>
    private static readonly TimeSpan ControlInterval = TimeSpan.FromSeconds(1);

    private static (long UndrainedKeys, double? RequestsPerSecond) Archived(NodeReport report) => (report.UndrainedKeys, report.ArchiveLogRequestsPerSecond);

    private static ArchiveLogDrainBlockedException Blocked(ImmutableArray<(LedgerId Ledger, Exception Failure)> failures) =>
        Assert.IsType<ArchiveLogDrainBlockedException>(Assert.Single(failures).Failure);

    /// <summary>Starts a node of its own process on archive logs, over an in-memory store whose writes it can refuse or hold.</summary>
    /// <summary>
    /// The drains' clock in a test which times their retries: it reads <paramref name="clock"/>, which only the test moves,
    /// and completes every one-shot wait at once, so the drain pacer, whose waits are the drains' only timers, admits each
    /// request at once while the clock stands still.
    /// </summary>
    private sealed class ImmediateWaits(TimeProvider clock) : TimeProvider
    {
        public override long TimestampFrequency => clock.TimestampFrequency;

        public override TimeZoneInfo LocalTimeZone => clock.LocalTimeZone;

        public override DateTimeOffset GetUtcNow() => clock.GetUtcNow();

        public override long GetTimestamp() => clock.GetTimestamp();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            period == Timeout.InfiniteTimeSpan
                ? System.CreateTimer(callback, state, TimeSpan.Zero, Timeout.InfiniteTimeSpan)
                : throw new NotSupportedException("The drains create no periodic timer.");
    }
    private async Task<LogsNode> StartLogsNodeAsync(TimeProvider? drainsTime = null, IObjectStore? objects = null, int drainConcurrency = 1, HoldingTransport? transport = null)
    {
        var store = new RefusingArchiveLogStore(new MemoryArchiveLogStore());
        return new LogsNode(
            this,
            transport ?? new HoldingTransport(),
            store,
            await PassGateAsync(store, objects ?? new InMemoryObjectStore()),
            await PassGateAsync(store, new InMemoryObjectStore()),
            drainsTime ?? TimeProvider.System,
            drainConcurrency);

        static async Task<ArchiveLogGate> PassGateAsync(RefusingArchiveLogStore store, IObjectStore objects)
        {
            var archive = new LogArchiveStore(store, objects, ArchiveLogNames.Hexadecimal);
            var record = await archive.StoreRecordAsync(ImmutableDictionary<string, string>.Empty, Token);
            return await ArchiveLogGate.PassAsync(archive, new ArchiveStoreIdentity(ArchiveStoreKind.Logs, new ArchiveStorePrefix("t"), record.Id), ImmutableDictionary<string, string>.Empty, Token);
        }
    }

    /// <summary>
    /// A node on archive logs under test, with its own process's reader, and a writer registry, drains, and controller of
    /// the process; <see cref="Restart"/> gives another process's over the same replicas and store.
    /// </summary>
    private sealed class LogsNode : IAsyncDisposable
    {
        private readonly NodeControllerTests _test;
        private readonly HoldingTransport _transport;
        private readonly Meter _meter = new(DurableStorageMeter.Name);
        private readonly RecordingLogger _logger = new();
        private readonly TimeProvider _drainsTime;
        private readonly ConcurrentDictionary<(string Ledger, string Cause), int> _gauge = new();
        private readonly MeterListener _listener;
        private readonly List<LedgerWriterRegistry> _registries = [];
        private readonly bool _ownsProcess = true;
        private readonly int _drainConcurrency;

        public LogsNode(NodeControllerTests test, HoldingTransport transport, RefusingArchiveLogStore store, ArchiveLogGate gate, ArchiveLogGate otherGate, TimeProvider drainsTime, int drainConcurrency)
        {
            _test = test;
            _drainConcurrency = drainConcurrency;
            _transport = transport;
            _drainsTime = drainsTime;
            Store = store;
            Gate = gate;
            OtherGate = otherGate;
            Reader = new LedgerReader(test._view, transport, test._time, Node(1));
            _listener = ListenBlocked(_meter);
            (Drains, Registry, Node) = Start();
        }

        private LogsNode(LogsNode restarted)
        {
            _test = restarted._test;
            _transport = restarted._transport;
            _meter = restarted._meter;
            _logger = restarted._logger;
            _drainsTime = restarted._drainsTime;
            _drainConcurrency = restarted._drainConcurrency;
            _listener = restarted._listener;
            _registries = restarted._registries;
            _ownsProcess = false;
            Store = restarted.Store;
            Gate = restarted.Gate;
            OtherGate = restarted.OtherGate;
            Reader = restarted.Reader;
            (Drains, Registry, Node) = Start();
        }

        public LedgerReader Reader { get; }

        public RefusingArchiveLogStore Store { get; }

        public ArchiveLogGate Gate { get; }

        public ArchiveLogGate OtherGate { get; }

        public ArchiveLogNodeDrains Drains { get; }

        public LedgerWriterRegistry Registry { get; }

        public NodeController Node { get; }

        /// <summary>Gets or sets the active nodes the node's registry view lists now, which its floor divides the budget by.</summary>
        public int NodeCount { get; set; } = 3;

        public int Criticals => _logger.Events.Count(static id => id == 6244);

        public RecordingLogger Log => _logger;

        public LedgerWriter Writer(LedgerMetadata ledger) => Registry.TryGet(ledger.Id) ?? throw new InvalidOperationException("The node serves no writer of the ledger.");

        /// <summary>Returns the node of a new process over the same replicas and store, whose drains learned nothing of this one's writers.</summary>
        public LogsNode Restart() => new(this);

        /// <summary>Installs the first ledger through the node, its primary.</summary>
        public async Task<LedgerMetadata> BootstrapAsync(NodeControllerTests owner)
        {
            _ = Assert.IsType<LedgerMapCommitOutcome.Applied>((await Node.ExecuteAsync(new Transition.Bootstrap(Choice(NodeRegistrySamples.Node(1))), owner.CommitAsync, Token)).Value);
            return Assert.Single((await owner._view.ReadAsync(Token)).Ledgers);
        }

        /// <summary>Gets the failures the drains this node started reported, in the order they ended.</summary>
        public ConcurrentQueue<(LedgerId Ledger, Exception Failure)> Failures { get; } = new();

        /// <summary>Starts the node's drains due over its gate, recording their failures in <see cref="Failures"/>.</summary>
        public ImmutableArray<LedgerId> Start(ImmutableArray<LedgerId> drainToo, Func<LedgerMapChange, CancellationToken, Task<LedgerMapCommitOutcome>> commit) =>
            Node.StartLogDrains(Gate, drainToo, commit, (ledger, failure) => Failures.Enqueue((ledger, failure)), Token);

        /// <summary>Starts the node's drains due and waits for every drain to end, answering the failures they reported.</summary>
        public async Task<ImmutableArray<(LedgerId Ledger, Exception Failure)>> DrainAsync(ImmutableArray<LedgerId> drainToo, Func<LedgerMapChange, CancellationToken, Task<LedgerMapCommitOutcome>> commit)
        {
            Failures.Clear();
            _ = Start(drainToo, commit);
            await Node.DrainsEnded.WaitAsync(Patience, Token);
            return [.. Failures];
        }

        /// <summary>Gives the node a share, as its controller's reply does.</summary>
        public Task ShareAsync(NodeControllerTests owner) => Node.ObserveAsync(new NodeReportReply(owner._view.Current.Epoch, [], ArchiveLogShare: 1_000_000), Token);

        public async Task ProposeAsync(LedgerMetadata ledger, params byte[] keys)
        {
            using var body = ArcBuffer.Wrap(new byte[8]);
            Row[] rows = [.. keys.Select(key => new Row([key], RowKind.Append, body))];
            try
            {
                _ = Assert.IsType<ProposalOutcome.Durable>((await Writer(ledger).ProposeAsync(rows, Token)).Value);
            }
            finally
            {
                foreach (var row in rows)
                {
                    row.Dispose();
                }
            }
        }

        /// <summary>Returns the blocked drains' gauge as it stands.</summary>
        public Dictionary<(string, string), int> Gauge()
        {
            _gauge.Clear();
            _listener.RecordObservableInstruments();
            return _gauge.ToDictionary();
        }

        public async ValueTask DisposeAsync()
        {
            _listener.Dispose();
            foreach (var registry in _registries)
            {
                await registry.DisposeAsync();
            }

            _registries.Clear();
            if (_ownsProcess)
            {
                await Reader.DisposeAsync();
                _transport.Dispose();
                _meter.Dispose();
            }
        }

        private (ArchiveLogNodeDrains Drains, LedgerWriterRegistry Registry, NodeController Node) Start()
        {
            var drains = new ArchiveLogNodeDrains(NodeRegistrySamples.Node(1), budget: 10_000, nodes: () => NodeCount, LedgerWriterOptions.Default.MaxSegmentKeys, ControlInterval, new ArchiveLogOptions(), _drainsTime, _meter, _logger);
            var registry = new LedgerWriterRegistry(Reader, 1 << 20, LedgerWriterOptions.Default, _test._time, NullLoggerFactory.Instance, drains.OnSegmentEnded);
            _registries.Add(registry);
            return (drains, registry, _test.Controller(registry: registry, logs: drains, reader: Reader, drainConcurrency: _drainConcurrency));
        }

        /// <summary>Listens to <paramref name="source"/> alone, recording each blocked drain by ledger and cause.</summary>
        private MeterListener ListenBlocked(Meter source)
        {
            var listener = new MeterListener
            {
                InstrumentPublished = (instrument, listener) =>
                {
                    if (ReferenceEquals(instrument.Meter, source) && instrument.Name == ArchiveLogNodeDrains.BlockedName)
                    {
                        listener.EnableMeasurementEvents(instrument);
                    }
                },
            };

            listener.SetMeasurementEventCallback<int>((_, value, tags, _) =>
            {
                var (ledger, cause) = ((string?)null, (string?)null);
                foreach (var tag in tags)
                {
                    (ledger, cause) = tag.Key switch
                    {
                        "ledger" => (tag.Value as string, cause),
                        "cause" => (ledger, tag.Value as string),
                        _ => (ledger, cause),
                    };
                }

                _gauge[(ledger!, cause!)] = value;
            });
            listener.Start();
            return listener;
        }
    }

    /// <summary>
    /// archive-lsm.md §2.4, §8.8: a partition with nothing to compact answers so; one whose sections' objects are gone
    /// answers <see cref="CompactionReply.Corrupt"/>, the worker's fresh read listing them still; one whose section is corrupt, on a
    /// read and on the fresh read past the caches, answers <see cref="CompactionReply.Corrupt"/>, naming the object. None
    /// submits a change.
    /// </summary>
    [Theory]
    [InlineData("nothing")]
    [InlineData("gone")]
    [InlineData("corrupt")]
    public async Task ACompactionWhichSubmitsNothingAnswersWhy(string sections)
    {
        var node = Controller(lsm: true);
        var epoch = sections == "nothing" ? await DrainAsync(1, write: false) : await DrainAsync(3, write: sections == "corrupt");
        var reply = await node.CompactAsync(new CompactionAssignment(KeyRange.Root, Node(1), epoch), CommitManifestAsync, Token);

        switch (sections)
        {
            case "nothing":
                Assert.Equal(CompactionReply.NothingToCompact.Instance, reply.Value);
                break;
            case "gone":
                Assert.Contains("listed object deleted", Assert.IsType<CompactionReply.Corrupt>(reply.Value).Reason, StringComparison.Ordinal);
                break;
            default:
                var corrupt = Assert.IsType<CompactionReply.Corrupt>(reply.Value);
                Assert.Equal(KeyRange.Root, corrupt.Partition);
                Assert.Contains("d/1/", corrupt.Reason, StringComparison.Ordinal);
                break;
        }

        Assert.Equal(0, _commits);
    }

    /// <summary>The manifest's own corruption, met refreshing the view, fails the call: it is never the partition's.</summary>
    [Fact]
    public async Task TheManifestsCorruptionFailsTheCompaction()
    {
        var node = Controller(lsm: true);
        using (var garbage = ArcBuffer.Wrap(new byte[] { 1, 2, 3, 4 }))
        {
            _ = await _objects.ReplaceAsync(ObjectName.Manifest, null, garbage, null, Token);
        }

        _ = await Assert.ThrowsAsync<ArchiveCorruptException>(() => node.CompactAsync(new CompactionAssignment(KeyRange.Root, Node(1), 1), CommitManifestAsync, Token));
        Assert.Equal(0, _commits);
    }

    private static TransitionChoice Choice(NodeId executor) =>
        new(executor, [executor, .. new[] { Node(1), Node(2), Node(3) }.Where(node => node != executor).Take(2)], 4, 0, 0);

    private static LedgerOpening Opening(KeyRange range, params NodeId[] replicas) =>
        new(range, [.. replicas], 4, new PrimaryClaim(replicas[0], SequencerInstanceId.New()));

    /// <summary>
    /// orleans.md §5.14, OL-I1: a node reports its object-store demand (open object-store primaries plus closed
    /// object-store ledgers in <c>DrainToo</c>), its <c>ObjectLedgersReady</c> flag, and its object-store request share
    /// (falling back to the floor after <c>ReplyIntervals</c> without a share), clears them when refused, and declines any
    /// object-store transition while <c>objectLedgersReady</c> is false.
    /// </summary>
    [Fact]
    public async Task ObjectStoreDemandShareAndGateReadinessFollowSection514()
    {
        var ready = false;
        var share = new ArchiveLogNodeShare(static () => ArchiveLogRequestShare.Floor(1_000, 2), ControlInterval);
        var controller = new NodeController(
            Node(1),
            Generation,
            _view,
            _registry,
            _reader,
            new TransitionExecutor(_registry, NullLogger<TransitionExecutor>.Instance),
            () => _active,
            () => _dead,
            new NodeArchive.Lsm(new DrainWorker(_objects, _manifest, _reader, Archive, _time), new CompactionWorker(_objects, new TailCache(_objects, Archive), _manifest, Archive), _manifest),
            _time,
            LoadWindow,
            drainConcurrency: 1,
            objectStoreShare: share,
            objectLedgersReady: () => ready);

        // Before ObjectLedgersReady, an object-store transition is declined and ObjectLedgersReady is reported false.
        var objectChoice = new TransitionChoice(Node(1), LedgerLocation.ObjectStore.Instance, k: 2, 0, _view.Current.Epoch);
        Assert.Equal(TransitionReply.Declined.Instance, (await controller.ExecuteAsync(new Transition.Bootstrap(objectChoice), CommitAsync, Token)).Value);
        Assert.Equal(0, _commits);

        var beforeReady = controller.Report(diskPressure: false, [], handingOff: false, refused: false, archiveGatePassed: true, draining: true);
        Assert.False(beforeReady.ObjectLedgersReady);
        Assert.Equal(0, beforeReady.ObjectStoreDemand);
        Assert.Null(beforeReady.ObjectStoreRequestsPerSecond);

        // Install an open object-store ledger and a closed object-store ledger in DrainToo; mark ObjectLedgersReady.
        var closed = await InstallAsync(new LedgerOpening(KeyRange.Root, LedgerLocation.ObjectStore.Instance, k: 2, new PrimaryClaim(Node(1), SequencerInstanceId.New())));
        _ = await CommitMapAsync(new LedgerMapChange.Install(
            [new LedgerClosing(closed.Id, Slot.Zero)],
            [new LedgerOpening(KeyRange.Root, LedgerLocation.ObjectStore.Instance, k: 2, new PrimaryClaim(Node(1), SequencerInstanceId.New()))],
            0));
        ready = true;

        await controller.ObserveAsync(new NodeReportReply(_view.Current.Epoch, [closed.Id], ArchiveLogShare: null, ObjectStoreShare: 650), Token);
        var active = controller.Report(diskPressure: false, [], handingOff: false, refused: false, archiveGatePassed: true, draining: true);
        Assert.True(active.ObjectLedgersReady);
        Assert.Equal(2, active.ObjectStoreDemand);
        Assert.Equal(650, active.ObjectStoreRequestsPerSecond);

        // True store refusal (refused && !draining) clears ObjectLedgersReady, ObjectStoreDemand, and ObjectStoreRequestsPerSecond.
        var refused = controller.Report(diskPressure: false, [], handingOff: true, refused: true, archiveGatePassed: false, draining: false);
        Assert.False(refused.ObjectLedgersReady);
        Assert.Equal(0, refused.ObjectStoreDemand);
        Assert.Null(refused.ObjectStoreRequestsPerSecond);

        // LSM parameter mismatch (refused && draining) still drains closed ledgers, so it keeps reporting ObjectLedgersReady,
        // its closed DrainToo object-store demand, and its current object-store request rate (Audit #3).
        var paramMismatch = controller.Report(diskPressure: false, [], handingOff: true, refused: true, archiveGatePassed: true, draining: true);
        Assert.True(paramMismatch.ObjectLedgersReady);
        Assert.Equal(1, paramMismatch.ObjectStoreDemand);
        Assert.Equal(650, paramMismatch.ObjectStoreRequestsPerSecond);

        // After ReplyIntervals without a fresh share, the node falls back to its floor.
        _time.Advance(ControlInterval * ArchiveLogRequestShare.ReplyIntervals);
        var expired = controller.Report(diskPressure: false, [], handingOff: false, refused: false, archiveGatePassed: true, draining: true);
        Assert.Equal(ArchiveLogRequestShare.Floor(1_000, 2), expired.ObjectStoreRequestsPerSecond);
    }

    /// <summary>
    /// Audit #10: when <see cref="NodeController.ExecuteAsync"/> receives a <c>Nodes</c>-targeted transition whose closed
    /// ledger is an <c>ObjectStore</c> ledger not yet in the node's cached map view, it refreshes its map to
    /// <c>transition.Choice.Epoch</c> before checking <c>TouchesObjectStore</c> and returns
    /// <see cref="TransitionReply.Declined"/> cleanly while <c>objectLedgersReady</c> is false.
    /// </summary>
    [Fact]
    public async Task ExecuteAsyncRefreshesStaleMapAndDeclinesObjectStoreClosureWhenNotReady()
    {
        var staleView = await LedgerMapView.OpenAsync(_register, Token);
        var controller = new NodeController(
            Node(1),
            Generation,
            staleView,
            _registry,
            _reader,
            new TransitionExecutor(_registry, NullLogger<TransitionExecutor>.Instance),
            () => true,
            () => _dead,
            new NodeArchive.Lsm(new DrainWorker(_objects, _manifest, _reader, Archive, _time), new CompactionWorker(_objects, new TailCache(_objects, Archive), _manifest, Archive), _manifest),
            _time,
            LoadWindow,
            drainConcurrency: 1,
            objectLedgersReady: static () => false);

        // Commit an ObjectStore ledger to the MetaStore without refreshing staleView.
        var outcome = await _committer.CommitAsync(
            new LedgerMapChange.Install([], [new LedgerOpening(KeyRange.Root, LedgerLocation.ObjectStore.Instance, k: 2, new PrimaryClaim(Node(2), SequencerInstanceId.New()))], 0),
            Token);
        var applied = Assert.IsType<LedgerMapCommitOutcome.Applied>(outcome.Value);
        var fresh = await _view.ReadAsync(Token);
        var objectLedger = Assert.Single(fresh.Ledgers);
        Assert.Equal(0ul, staleView.Current.Epoch);

        // A Replace targeting Nodes (ObjectStore -> Nodes) must refresh staleView, detect the ObjectStore closure, and Decline.
        var transition = new Transition.Replace(
            objectLedger.Id,
            new TransitionChoice(Node(1), new LedgerLocation.Nodes([Node(1), Node(2), Node(3)]), k: 4, 0, applied.Epoch));
        var reply = await controller.ExecuteAsync(transition, CommitAsync, Token);
        Assert.IsType<TransitionReply.Declined>(reply.Value);
        Assert.Equal(applied.Epoch, staleView.Current.Epoch);
    }

    private NodeController Controller(bool lsm = false, LedgerWriterRegistry? registry = null, ArchiveLogNodeDrains? logs = null, LedgerReader? reader = null, int drainConcurrency = 1, Func<LedgerId, KeyRange, TimeSpan, CancellationToken, Task<LedgerPrepareReleaseOutcome>>? prepareRelease = null) =>
        Controller(registry, reader, logs is not null ? new NodeArchive.Logs(logs) : new NodeArchive.Lsm(new DrainWorker(_objects, _manifest, _reader, Archive, _time), new CompactionWorker(_objects, new TailCache(_objects, Archive), _manifest, Archive), _manifest), drainConcurrency, prepareRelease: prepareRelease);

    private NodeController Controller(LedgerWriterRegistry? registry, LedgerReader? reader, NodeArchive archive, int drainConcurrency, ILogger? logger = null, Meter? meter = null, Func<LedgerId, KeyRange, TimeSpan, CancellationToken, Task<LedgerPrepareReleaseOutcome>>? prepareRelease = null) =>
        new(
            Node(1),
            Generation,
            _view,
            registry ?? _registry,
            reader ?? _reader,
            new TransitionExecutor(registry ?? _registry, NullLogger<TransitionExecutor>.Instance),
            () => _active && ++_activeChecks <= _inactiveAfter,
            () => _dead,
            archive,
            _time,
            LoadWindow,
            drainConcurrency,
            logger,
            meter,
            prepareRelease: prepareRelease);

    /// <summary>
    /// OR-I24: <see cref="NodeController.PrepareReleaseAsync"/> drains the served ledger, records it in
    /// <see cref="NodeReport.ReleasedLedgers"/> until the map drops or closes it, answers repeated requests idempotently,
    /// declines mismatched ranges or stopped <see cref="LocalNodeControl"/>, answers <see cref="PrepareReleaseReply.NotServed"/>
    /// for unknown ledgers, and propagates <see cref="PrepareReleaseReply.Sealed"/> / <see cref="PrepareReleaseReply.TimedOut"/>
    /// without recording the ledger as released.
    /// </summary>
    [Fact]
    public async Task PrepareReleaseRecordsReleasedLedgerIdempotentlyAndHandlesEdgeCases()
    {
        using var transport = new InMemoryLedgerReplicaTransport();
        await using var reader = new LedgerReader(_view, transport, _time, Node(1));
        await using var registry = new LedgerWriterRegistry(reader, 1 << 20, LedgerWriterOptions.Default, _time, NullLoggerFactory.Instance);
        var node = Controller(registry: registry, reader: reader);
        _ = Assert.IsType<LedgerMapCommitOutcome.Applied>((await node.ExecuteAsync(new Transition.Bootstrap(Choice(Node(1))), CommitAsync, Token)).Value);
        _ = await _view.ReadAsync(Token);
        var ledger = Assert.Single(_view.Current.Ledgers);

        // Wrong ledger or wrong range -> NotServed.
        Assert.Same(PrepareReleaseReply.NotServed.Instance, (await node.PrepareReleaseAsync(new PrepareReleaseRequest(new LedgerId(999), KeyRange.Root, TimeSpan.FromSeconds(5)), Token)).Value);
        Assert.Same(PrepareReleaseReply.NotServed.Instance, (await node.PrepareReleaseAsync(new PrepareReleaseRequest(ledger.Id, KeyRange.Root.Children.Lower, TimeSpan.FromSeconds(5)), Token)).Value);

        // Propose one batch, then PrepareRelease -> Released(1), reported in NodeReport.ReleasedLedgers, and idempotent.
        var writer = registry.TryGet(ledger.Id)!;
        using (var body = ArcBuffer.Wrap("hi"u8.ToArray()))
        using (var row = new Row([0x10], RowKind.Append, body))
        {
            _ = Assert.IsType<ProposalOutcome.Durable>((await writer.ProposeAsync([row], Token)).Value);
        }

        var released = Assert.IsType<PrepareReleaseReply.Released>((await node.PrepareReleaseAsync(new PrepareReleaseRequest(ledger.Id, ledger.Range, TimeSpan.FromSeconds(5)), Token)).Value);
        Assert.Equal(new Slot(1), released.ContiguousTail);
        var report = node.Report(diskPressure: false, [], handingOff: false, refused: false, archiveGatePassed: true, draining: true);
        Assert.NotNull(report.ReleasedLedgers);
        Assert.Equal(new Slot(1), Assert.Single(report.ReleasedLedgers, kv => kv.Key == ledger.Id).Value);
        Assert.Equal(released, Assert.IsType<PrepareReleaseReply.Released>((await node.PrepareReleaseAsync(new PrepareReleaseRequest(ledger.Id, ledger.Range, TimeSpan.FromSeconds(5)), Token)).Value));

        // Closing the ledger in the map prunes it from NodeReport.ReleasedLedgers.
        _ = await CommitMapAsync(new LedgerMapChange.Install([new LedgerClosing(ledger.Id, new Slot(1))], [Opening(KeyRange.Root, Node(2), Node(1), Node(3))], 0));
        Assert.Empty(node.Report(diskPressure: false, [], handingOff: false, refused: false, archiveGatePassed: true, draining: true).ReleasedLedgers ?? []);

        // LocalNodeControl rejects another node's ID, keeps forwarding PrepareRelease during BeginHandOff, and declines once stopped.
        var control = new LocalNodeControl();
        control.Serve(node, TaskScheduler.Default, Token);
        _ = await Assert.ThrowsAsync<InvalidOperationException>(() => control.PrepareReleaseAsync(Node(2), new PrepareReleaseRequest(ledger.Id, ledger.Range, TimeSpan.FromSeconds(5))));
        control.BeginHandOff();
        Assert.Same(PrepareReleaseReply.NotServed.Instance, (await control.PrepareReleaseAsync(Node(1), new PrepareReleaseRequest(ledger.Id, ledger.Range, TimeSpan.FromSeconds(5)))).Value);
        control.Stop();
        Assert.Same(PrepareReleaseReply.Declined.Instance, (await control.PrepareReleaseAsync(Node(1), new PrepareReleaseRequest(ledger.Id, ledger.Range, TimeSpan.FromSeconds(5)))).Value);
    }

    /// <summary>
    /// OR-I24: when the journal drain delegate reports <see cref="LedgerPrepareReleaseOutcome.Sealed"/> or
    /// <see cref="LedgerPrepareReleaseOutcome.TimedOut"/>, <see cref="NodeController.PrepareReleaseAsync"/> returns the
    /// matching reply and does not record the ledger in <see cref="NodeReport.ReleasedLedgers"/>.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PrepareReleasePropagatesSealedAndTimedOutWithoutRecordingReleased(bool sealedOutcome)
    {
        using var transport = new InMemoryLedgerReplicaTransport();
        await using var reader = new LedgerReader(_view, transport, _time, Node(1));
        await using var registry = new LedgerWriterRegistry(reader, 1 << 20, LedgerWriterOptions.Default, _time, NullLoggerFactory.Instance);
        var node = Controller(
            registry: registry,
            reader: reader,
            prepareRelease: (_, _, _, _) => Task.FromResult(sealedOutcome
                ? new LedgerPrepareReleaseOutcome(LedgerPrepareReleaseOutcome.Sealed.Instance)
                : new LedgerPrepareReleaseOutcome(LedgerPrepareReleaseOutcome.TimedOut.Instance)));
        _ = Assert.IsType<LedgerMapCommitOutcome.Applied>((await node.ExecuteAsync(new Transition.Bootstrap(Choice(Node(1))), CommitAsync, Token)).Value);
        _ = await _view.ReadAsync(Token);
        var ledger = Assert.Single(_view.Current.Ledgers);

        var reply = await node.PrepareReleaseAsync(new PrepareReleaseRequest(ledger.Id, ledger.Range, TimeSpan.FromSeconds(5)), Token);

        if (sealedOutcome)
        {
            Assert.Same(PrepareReleaseReply.Sealed.Instance, reply.Value);
        }
        else
        {
            Assert.Same(PrepareReleaseReply.TimedOut.Instance, reply.Value);
        }

        Assert.Empty(node.Report(diskPressure: false, [], handingOff: false, refused: false, archiveGatePassed: true, draining: true).ReleasedLedgers ?? []);
    }

    private Task<LedgerMapCommitOutcome> CommitAsync(LedgerMapChange change, CancellationToken token)
    {
        _commits++;
        return _committer.CommitAsync(change, token);
    }

    private Task<ManifestCommitOutcome> CommitManifestAsync(ManifestChange change, CancellationToken token)
    {
        if (change.Value is ManifestChange.Compaction)
        {
            _commits++;
        }

        return _manifestCommitter.CommitAsync(change, token);
    }

    private async Task<LedgerMapCommitOutcome> CommitMapAsync(LedgerMapChange change)
    {
        var outcome = await _committer.CommitAsync(change, Token);
        _ = await _view.ReadAsync(Token);
        return outcome;
    }

    /// <summary>
    /// Installs the root, splits it into two open ledgers of node 1, and drops the root, so neither waits for an older one;
    /// answers the lower and upper ledgers.
    /// </summary>
    private async Task<(LedgerMetadata Lower, LedgerMetadata Upper)> TwoLedgersAsync()
    {
        var root = await InstallAsync(Opening(KeyRange.Root, Node(1), Node(2), Node(3)));
        var (lowerRange, upperRange) = KeyRange.Root.Children;
        _ = await CommitMapAsync(new LedgerMapChange.Install([new LedgerClosing(root.Id, Slot.Zero)], [Opening(lowerRange, Node(1), Node(2), Node(3)), Opening(upperRange, Node(1), Node(2), Node(3))], 0));
        _ = await CommitMapAsync(new LedgerMapChange.Drop(root.Id));
        return (_view.Current.Ledgers.Single(ledger => ledger.Range == lowerRange), _view.Current.Ledgers.Single(ledger => ledger.Range == upperRange));
    }

    private async Task<LedgerMetadata> InstallAsync(LedgerOpening opening)
    {
        var outcome = await _committer.CommitAsync(new LedgerMapChange.Install([], [opening], 0), Token);
        _ = Assert.IsType<LedgerMapCommitOutcome.Applied>(outcome.Value);
        return Assert.Single((await LedgerMapView.OpenAsync(_register, Token)).Current.Ledgers);
    }

    /// <summary>
    /// Commits <paramref name="count"/> drains of ledger 1 into sections of the root partition, writing each section's
    /// object as bytes no run object holds when <paramref name="write"/>, and returns the manifest's epoch.
    /// </summary>
    private async Task<ulong> DrainAsync(int count, bool write)
    {
        ManifestCommitOutcome outcome = default;
        for (var slot = 0; slot < count; slot++)
        {
            var name = new ObjectName($"d/1/{slot}/s");
            if (write)
            {
                using var garbage = ArcBuffer.Wrap(new byte[64]);
                _ = await _objects.CreateAsync(name, garbage, null, Token);
            }

            var tag = new Tag(new LedgerId(1), new Slot(slot), 0);
            var section = new SectionRef(name, 0, 10, KeyRange.Root, tag, tag, new ObjectRange(10, 54));
            outcome = await _manifestCommitter.CommitAsync(new ManifestChange.Drain(new LedgerId(1), new Slot(slot), new Slot(slot + 1), [section], complete: false), Token);
        }

        return Assert.IsType<ManifestCommitOutcome.Applied>(outcome.Value).Epoch;
    }

    /// <summary>An object store whose listings fail, as an unknown outcome does, while <see cref="FailsLists"/> is set.</summary>
    private sealed class FlakyObjectStore(IObjectStore inner) : IObjectStore
    {
        private int _lists;

        public bool FailsLists { get; set; }

        /// <summary>Gets the listings asked of the store.</summary>
        public int Lists => Volatile.Read(ref _lists);

        public ValueTask<ObjectCreateOutcome> CreateAsync(ObjectName name, ArcBuffer content, uint? crc32c, CancellationToken cancellationToken = default) =>
            inner.CreateAsync(name, content, crc32c, cancellationToken);

        public ValueTask<ArcBuffer?> ReadAsync(ObjectName name, long offset, int length, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(name, offset, length, cancellationToken);

        public ValueTask<ObjectProperties?> GetPropertiesAsync(ObjectName name, CancellationToken cancellationToken = default) =>
            inner.GetPropertiesAsync(name, cancellationToken);

        public IAsyncEnumerable<ListedObject> ListAsync(string prefix, string? startAfter = null, CancellationToken cancellationToken = default)
        {
            _ = Interlocked.Increment(ref _lists);
            return FailsLists ? throw new IOException("The test's object store fails the listing.") : inner.ListAsync(prefix, startAfter, cancellationToken);
        }

        public ValueTask DeleteAsync(ObjectName name, CancellationToken cancellationToken = default) => inner.DeleteAsync(name, cancellationToken);

        public ValueTask<VersionedObject?> ReadVersionedAsync(ObjectName name, CancellationToken cancellationToken = default) =>
            inner.ReadVersionedAsync(name, cancellationToken);

        public ValueTask<ObjectReplaceOutcome> ReplaceAsync(ObjectName name, ObjectVersion? expectedVersion, ArcBuffer content, uint? crc32c, CancellationToken cancellationToken = default) =>
            inner.ReplaceAsync(name, expectedVersion, content, crc32c, cancellationToken);
    }

    /// <summary>
    /// An archive-log store which refuses the writes of the keys <see cref="Refuse"/> names, as a store refuses a write no
    /// retry changes, and holds every write while <see cref="Hold"/> holds them.
    /// </summary>
    private sealed class RefusingArchiveLogStore(MemoryArchiveLogStore inner) : IArchiveLogStore
    {
        private readonly Lock _sync = new();
        private HashSet<string> _refused = [with(StringComparer.Ordinal)];
        private TaskCompletionSource? _release;
        private TaskCompletionSource _held = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Gets a task which completes once a write is held.</summary>
        public Task HeldAsync => _held.Task;

        public ArchiveLogLayout Layout => inner.Layout;

        /// <summary>Refuses the writes of <paramref name="keys"/>' logs from now on, and of no other.</summary>
        public void Refuse(params byte[] keys)
        {
            lock (_sync)
            {
                _refused = [.. keys.Select(static key => ArchiveLogNames.Hexadecimal.Of([key]).Value)];
            }
        }

        public void Hold()
        {
            lock (_sync)
            {
                _release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        public void Release()
        {
            TaskCompletionSource? release;
            lock (_sync)
            {
                (release, _release) = (_release, null);
            }

            _ = release?.TrySetResult();
        }

        public bool AdmitsName(string name) => inner.AdmitsName(name);

        public ValueTask<ArchiveLogRead> ReadAsync(ArchiveLogName name, CancellationToken cancellationToken) => inner.ReadAsync(name, cancellationToken);

        public ValueTask<ArchiveLogProperties?> GetPropertiesAsync(ArchiveLogName name, CancellationToken cancellationToken) => inner.GetPropertiesAsync(name, cancellationToken);

        public async ValueTask<ArchiveLogWriteOutcome> AppendAsync(ArchiveLogName name, ArchiveLogPosition from, IReadOnlyList<ReadOnlySequence<byte>> run, CancellationToken cancellationToken)
        {
            await AdmitAsync(name);
            return await inner.AppendAsync(name, from, run, cancellationToken);
        }

        public async ValueTask<ArchiveLogWriteOutcome> OpenAsync(ArchiveLogName name, ArchiveLogPosition from, ArchiveLogOpener opener, ReadOnlySequence<byte> content, CancellationToken cancellationToken)
        {
            await AdmitAsync(name);
            return await inner.OpenAsync(name, from, opener, content, cancellationToken);
        }

        public IAsyncEnumerable<ArchiveLogGenerationPage> ListGenerationsAsync(string? continuation, CancellationToken cancellationToken) => inner.ListGenerationsAsync(continuation, cancellationToken);

        public ValueTask DeleteGenerationAsync(string log, ulong generation, CancellationToken cancellationToken) => inner.DeleteGenerationAsync(log, generation, cancellationToken);

        private async Task AdmitAsync(ArchiveLogName name)
        {
            Task? release;
            lock (_sync)
            {
                if (_refused.Contains(name.Value))
                {
                    throw new ArchiveLogRefusedException($"The store refuses '{name}'.");
                }

                release = _release?.Task;
            }

            if (release is not null)
            {
                _ = _held.TrySetResult();
                await release;
            }
        }
    }
    /// <summary>A transport these tests never open: every case runs without replica I/O.</summary>
    private sealed class UnopenedTransport : ILedgerReplicaTransport
    {
        public ILedgerReplica Open(NodeId node, LedgerId ledger, LedgerReplicaOptions options) =>
            throw new InvalidOperationException("The node's control work under test opens no replica.");

        public ValueTask<ReplicaHeartbeatRead> HeartbeatAsync(NodeId node, ReadOnlyMemory<LedgerId> ledgers, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The node's control work under test opens no replica.");
    }
}
