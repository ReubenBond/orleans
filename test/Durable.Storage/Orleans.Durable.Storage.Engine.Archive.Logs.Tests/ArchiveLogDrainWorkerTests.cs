using System.Buffers;
using System.Text;
using Microsoft.Extensions.Time.Testing;
using Orleans.Durable.Storage.Engine.Archive.Control;
using Orleans.Durable.Storage.Engine.Archive.Logs.Control;
using Orleans.Durable.Storage.Engine.Archive.Lsm;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.Ledgers.Control;
using Orleans.Durable.Storage.Engine.ObjectStore;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Archive.Logs.Tests;

/// <summary>
/// Examples of <see cref="ArchiveLogDrainWorker"/> (archive-logs.md §2.3, §2.4, §6, §8.1, §10): a closed ledger's segments
/// drained in order, each key's write once and each non-empty segment's marker <c>durable-storage/d/{S:D20}/{start:D20}</c>, to
/// <c>Complete</c>, after which <c>Drop(S)</c> applies and the logs hold the fold; a segment still open at the bound not
/// drained; a marker-only segment passed without a write; markers resuming a restarted drain; a crash between
/// <c>Complete</c> and the drop answering <c>Complete</c> again; <c>Waiting</c> on a listed older ledger and
/// <c>Dropped</c> after the drop; a failed segment left unmarked, whose landed writes the next drain finds held; corrupt,
/// refused, and predecessor-less keys blocking their ledger with their cause; and the drain concurrency, request rate, and
/// memory.
/// </summary>
public sealed class ArchiveLogDrainWorkerTests : IDisposable
{
    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _leaks.Dispose();

    /// <summary>
    /// archive-logs.md §7.1, orleans.md §5.13: a drain names each key's log by the archive gate's names, the scheme the
    /// store's record pins, and by no other: through a store whose scheme is not the hexadecimal one, every write lands
    /// under that scheme's names.
    /// </summary>
    [Fact]
    public async Task ADrainNamesEachKeysLogByTheGatesNames()
    {
        var names = new ArchiveLogNames("test-1", static key => "t/" + Convert.ToHexStringLower(key));
        await using var world = await ArchiveLogWorld.StartAsync(Token, names: names);
        Assert.Same(names, world.Gate.Names);
        var ledger = world.Ledgers.OpenContaining([0]).Id;
        await world.ProposeAsync(ledger, ("a", RowKind.Append, "a0"), ("b", RowKind.Replace, "b0"));
        var t = await world.CloseAsync(ledger, Token);
        var mark = world.RequestMark;

        Assert.Same(ArchiveLogDrainOutcome.Complete.Instance, (await world.Worker().DrainAsync(world.Ledgers.Ledger(ledger), t, Token)).Value);

        string[] expected = [names.Of("a"u8).Value, names.Of("b"u8).Value];
        Assert.Equal(expected.Order(StringComparer.Ordinal), world.WritesSince(mark).Select(static request => request.Log).Distinct().Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// LOG-I3: a closed ledger drains each segment in order, every key's write once, and marks each segment which holds rows
    /// with its end, answering <c>Complete</c> at <c>T</c>; its drop then applies, a later drain answers <c>Dropped</c>, and
    /// every key's log reads as the fold of its commands, through an opener, an append run, and a tombstoned <c>Delete</c>.
    /// </summary>
    [Fact]
    public async Task AClosedLedgerDrainsEverySegmentInOrderAndCompletes()
    {
        await using var world = await ArchiveLogWorld.StartAsync(Token);
        var ledger = world.Ledgers.OpenContaining([0]).Id;
        await world.ProposeAsync(ledger, ("a", RowKind.Append, "a0"), ("b", RowKind.Replace, "b0"));
        await world.ProposeAsync(ledger, ("a", RowKind.Append, "a1"));
        var second = await world.Ledgers.EndSegmentAsync(ledger, Token);
        await world.ProposeAsync(ledger, ("b", RowKind.Append, "b1"), ("c", RowKind.Delete, "tombston"));
        await world.ProposeAsync(ledger, ("a", RowKind.Replace, "a2"), ("a", RowKind.Append, "a3"));
        var t = await world.CloseAsync(ledger, Token);
        var metadata = world.Ledgers.Ledger(ledger);
        var worker = world.Worker();
        var mark = world.RequestMark;

        var outcome = await worker.DrainAsync(metadata, t, Token);

        Assert.Same(ArchiveLogDrainOutcome.Complete.Instance, outcome.Value);
        Assert.Equal([(ledger, Slot.Zero, second), (ledger, second, t)], await world.MarkersAsync(Token));

        // Segment 1: a's and b's first writes, two requests each; segment 2: b's append, one block, and c's and a's
        // openers, two requests each.
        Assert.Equal(9, world.WritesSince(mark).Length);
        Assert.IsType<LedgerMapCommitOutcome.Applied>((await worker.SubmitDropAsync(ledger, world.Ledgers.CommitAsync, Token)).Value);
        _ = await world.Ledgers.Map.ReadAsync(Token);
        Assert.Same(ArchiveLogDrainOutcome.Dropped.Instance, (await worker.DrainAsync(metadata, t, Token)).Value);
        foreach (var key in world.Keys)
        {
            Assert.True((await world.ReadAsync(key, Token)).SameAs(world.Fold(key)), key);
        }
    }

    /// <summary>
    /// archive-logs.md §2.3, V5: a closed ledger whose replicas answer <c>Backpressure</c> past the scan's retries drains
    /// nothing: the drain answers <c>Drained</c> at its start, writes no log and no marker, and the ledger is not complete,
    /// so its drop is refused; once the replicas answer, it drains every segment, its markers cover every row, and it
    /// answers <c>Complete</c> at <c>T</c>.
    /// </summary>
    [Fact]
    public async Task AClosedLedgerUnderBackpressurePastItsRetriesDrainsNothingUntilItsReplicasAnswer()
    {
        await using var world = await ArchiveLogWorld.StartAsync(Token);
        var ledger = world.Ledgers.OpenContaining([0]).Id;
        await world.ProposeAsync(ledger, ("a", RowKind.Append, "a0"), ("b", RowKind.Replace, "b0"));
        var second = await world.Ledgers.EndSegmentAsync(ledger, Token);
        await world.ProposeAsync(ledger, ("a", RowKind.Append, "a1"));
        var t = await world.CloseAsync(ledger, Token);
        var metadata = world.Ledgers.Ledger(ledger);
        var worker = world.Worker();
        var mark = world.RequestMark;
        world.Ledgers.Transport.Saturated = static (_, _) => true;

        Assert.Equal(new ArchiveLogDrainOutcome.Drained(Slot.Zero), (await worker.DrainAsync(metadata, t, Token)).Value);

        Assert.Empty(world.WritesSince(mark));
        Assert.Empty(await world.MarkersAsync(Token));
        _ = await Assert.ThrowsAsync<InvalidOperationException>(async () => await worker.SubmitDropAsync(ledger, world.Ledgers.CommitAsync, Token));
        Assert.NotNull((await world.Ledgers.Map.ReadAsync(Token)).Get(ledger));

        world.Ledgers.Transport.Saturated = static (_, _) => false;
        Assert.Same(ArchiveLogDrainOutcome.Complete.Instance, (await worker.DrainAsync(metadata, t, Token)).Value);
        Assert.Equal([(ledger, Slot.Zero, second), (ledger, second, t)], await world.MarkersAsync(Token));
        foreach (var key in world.Keys)
        {
            Assert.True((await world.ReadAsync(key, Token)).SameAs(world.Fold(key)), key);
        }
    }

    /// <summary>A drain of a ledger the map shows dropped answers <c>Dropped</c> and writes nothing.</summary>
    [Fact]
    public async Task ADrainOfADroppedLedgerAnswersDropped()
    {
        await using var world = await ArchiveLogWorld.StartAsync(Token);
        var ledger = world.Ledgers.OpenContaining([0]).Id;
        await world.ProposeAsync(ledger, ("a", RowKind.Append, "a0"));
        var t = await world.CloseAsync(ledger, Token);
        var metadata = world.Ledgers.Ledger(ledger);
        var worker = world.Worker();
        Assert.Same(ArchiveLogDrainOutcome.Complete.Instance, (await worker.DrainAsync(metadata, t, Token)).Value);
        Assert.IsType<LedgerMapCommitOutcome.Applied>((await worker.SubmitDropAsync(ledger, world.Ledgers.CommitAsync, Token)).Value);
        _ = await world.Ledgers.Map.ReadAsync(Token);
        var mark = world.RequestMark;

        Assert.Same(ArchiveLogDrainOutcome.Dropped.Instance, (await world.Worker().DrainAsync(metadata, t, Token)).Value);
        Assert.Same(ArchiveLogDrainOutcome.Dropped.Instance, (await worker.DrainAsync(metadata, t, Token)).Value);
        Assert.Empty(world.WritesSince(mark));
        await Assert.ThrowsAsync<InvalidOperationException>(() => worker.SubmitDropAsync(ledger, world.Ledgers.CommitAsync, Token));
    }

    /// <summary>
    /// A segment still open at the drain's bound is not drained: the drain answers <c>OpenSegment</c> at its start, writing
    /// nothing and leaving no marker; once a marker batch closes it below the tail, it drains.
    /// </summary>
    [Fact]
    public async Task ASegmentStillOpenAtTheBoundIsNotDrained()
    {
        await using var world = await ArchiveLogWorld.StartAsync(Token);
        var ledger = world.Ledgers.OpenContaining([0]).Id;
        await world.ProposeAsync(ledger, ("a", RowKind.Append, "a0"));
        await world.ProposeAsync(ledger, ("a", RowKind.Append, "a1"));
        var worker = world.Worker();
        var mark = world.RequestMark;

        var open = await world.DrainAsync(worker, ledger, await world.Ledgers.ConfirmOpenAsync(ledger, Token), Token);

        Assert.Equal(new ArchiveLogDrainOutcome.OpenSegment(Slot.Zero), open.Value);
        Assert.Empty(world.WritesSince(mark));
        Assert.Empty(await world.MarkersAsync(Token));

        var end = await world.Ledgers.EndSegmentAsync(ledger, Token);
        var drained = await world.DrainAsync(worker, ledger, await world.Ledgers.ConfirmOpenAsync(ledger, Token), Token);

        Assert.Equal(new ArchiveLogDrainOutcome.OpenSegment(end), drained.Value);
        Assert.True((await world.ReadAsync("a", Token)).SameAs(world.Fold("a")));
    }

    /// <summary>
    /// A segment which holds only a marker batch holds no write: the drain passes it without a request and without a
    /// marker of its own.
    /// </summary>
    [Fact]
    public async Task AMarkerOnlySegmentIsPassedWithoutAWrite()
    {
        await using var world = await ArchiveLogWorld.StartAsync(Token);
        var ledger = world.Ledgers.OpenContaining([0]).Id;
        await world.ProposeAsync(ledger, ("a", RowKind.Append, "a0"));
        var first = await world.Ledgers.EndSegmentAsync(ledger, Token);
        var second = await world.Ledgers.EndSegmentAsync(ledger, Token);
        var worker = world.Worker();
        Assert.Equal(new ArchiveLogDrainOutcome.OpenSegment(first), (await world.DrainAsync(worker, ledger, first + 1, Token)).Value);
        var mark = world.RequestMark;

        var passed = await world.DrainAsync(worker, ledger, await world.Ledgers.ConfirmOpenAsync(ledger, Token), Token);

        Assert.Equal(new ArchiveLogDrainOutcome.OpenSegment(second), passed.Value);
        Assert.Empty(world.WritesSince(mark));
        Assert.Equal([(ledger, Slot.Zero, first)], await world.MarkersAsync(Token));
    }

    /// <summary>
    /// A restarted drain resumes after the highest marker of its ledger: a fresh worker writes nothing for the marked
    /// segments, and drains the next from the marker's end.
    /// </summary>
    [Fact]
    public async Task MarkersResumeARestartedDrain()
    {
        await using var world = await ArchiveLogWorld.StartAsync(Token);
        var ledger = world.Ledgers.OpenContaining([0]).Id;
        await world.ProposeAsync(ledger, ("a", RowKind.Append, "a0"));
        _ = await world.Ledgers.EndSegmentAsync(ledger, Token);
        await world.ProposeAsync(ledger, ("a", RowKind.Append, "a1"), ("b", RowKind.Append, "b0"));
        var end = await world.Ledgers.EndSegmentAsync(ledger, Token);
        Assert.Equal(new ArchiveLogDrainOutcome.OpenSegment(end), (await world.DrainAsync(world.Worker(), ledger, end + 1, Token)).Value);
        await world.ProposeAsync(ledger, ("b", RowKind.Append, "b1"));
        var last = await world.Ledgers.EndSegmentAsync(ledger, Token);
        var mark = world.RequestMark;

        var resumed = await world.DrainAsync(world.Worker(), ledger, last + 1, Token);

        Assert.Equal(new ArchiveLogDrainOutcome.OpenSegment(last), resumed.Value);
        Assert.Equal([ArchiveLogWorld.NameOf("b").Value], world.WritesSince(mark).Select(static request => request.Log).Distinct());
        Assert.True((await world.ReadAsync("a", Token)).SameAs(world.Fold("a")));
        Assert.True((await world.ReadAsync("b", Token)).SameAs(world.Fold("b")));
    }

    /// <summary>
    /// A crash between <c>Complete</c> and the drop costs a second drain, which finds every segment marked, answers
    /// <c>Complete</c> again without a write, and submits the drop.
    /// </summary>
    [Fact]
    public async Task ACrashBetweenCompleteAndTheDropAnswersCompleteAgain()
    {
        await using var world = await ArchiveLogWorld.StartAsync(Token);
        var ledger = world.Ledgers.OpenContaining([0]).Id;
        await world.ProposeAsync(ledger, ("a", RowKind.Append, "a0"));
        _ = await world.Ledgers.EndSegmentAsync(ledger, Token);
        await world.ProposeAsync(ledger, ("a", RowKind.Append, "a1"));
        var t = await world.CloseAsync(ledger, Token);
        Assert.Same(ArchiveLogDrainOutcome.Complete.Instance, (await world.DrainAsync(world.Worker(), ledger, t, Token)).Value);
        var mark = world.RequestMark;
        var restarted = world.Worker();

        Assert.Same(ArchiveLogDrainOutcome.Complete.Instance, (await world.DrainAsync(restarted, ledger, t, Token)).Value);
        Assert.Empty(world.WritesSince(mark));
        Assert.IsType<LedgerMapCommitOutcome.Applied>((await restarted.SubmitDropAsync(ledger, world.Ledgers.CommitAsync, Token)).Value);
    }

    /// <summary>
    /// A drain of a ledger whose older overlapping ledger is still listed answers <c>Waiting</c> with it and writes nothing;
    /// the <c>ignores-lineage</c> mutant writes the successor's segment first, whose key's write then finds its predecessor
    /// missing and blocks.
    /// </summary>
    [Fact]
    public async Task ADrainWaitsForAListedOlderOverlappingLedger()
    {
        await using var world = await ArchiveLogWorld.StartAsync(Token);
        var older = world.Ledgers.OpenContaining([0]).Id;
        await world.ProposeAsync(older, ("a", RowKind.Append, "a0"));
        _ = await world.CloseAsync(older, Token);
        var successor = world.Ledgers.OpenContaining([0]).Id;
        await world.ProposeAsync(successor, ("a", RowKind.Append, "a1"));
        var end = await world.Ledgers.EndSegmentAsync(successor, Token);
        var mark = world.RequestMark;

        var waiting = await world.DrainAsync(world.Worker(), successor, end + 1, Token);

        Assert.Equal(new ArchiveLogDrainOutcome.Waiting(older), waiting.Value);
        Assert.Empty(world.WritesSince(mark));
        var blocked = await Assert.ThrowsAsync<ArchiveLogDrainBlockedException>(() => world.DrainAsync(world.Worker(ignoresLineage: true), successor, end + 1, Token));
        Assert.Equal(ArchiveLogDrainBlockedCause.Corrupt, blocked.Cause);
    }

    /// <summary>
    /// A segment with a write which fails on every attempt is left unmarked: the drain lets its other writes settle and
    /// throws the failure, and the next drain writes the segment again, finds the landed write held without a second
    /// landing, and completes it.
    /// </summary>
    [Fact]
    public async Task AFailedSegmentIsLeftUnmarkedAndTheNextDrainFindsItsLandedWritesHeld()
    {
        await using var world = await ArchiveLogWorld.StartAsync(Token);
        var ledger = world.Ledgers.OpenContaining([0]).Id;
        await world.ProposeAsync(ledger, ("a", RowKind.Replace, "a0"), ("b", RowKind.Append, "b0"));
        var t = await world.CloseAsync(ledger, Token);
        var failing = ArchiveLogWorld.NameOf("b").Value;
        world.Faults = request => request.Log == failing && request.Kind == ArchiveLogRequestKind.AppendUnit ? ArchiveLogFault.FailBefore : ArchiveLogFault.None;

        _ = await Assert.ThrowsAsync<IOException>(() => world.DrainAsync(world.Worker(), ledger, t, Token));

        Assert.Empty(await world.MarkersAsync(Token));
        Assert.True((await world.ReadAsync("a", Token)).SameAs(world.Fold("a")));
        world.Faults = null;
        Assert.Same(ArchiveLogDrainOutcome.Complete.Instance, (await world.DrainAsync(world.Worker(), ledger, t, Token)).Value);
        Assert.Equal(1, world.Memory.Creations(ArchiveLogWorld.NameOf("a").Value, 1));
        Assert.True((await world.ReadAsync("b", Token)).SameAs(world.Fold("b")));
    }

    /// <summary>
    /// A key whose log contradicts its chain blocks its ledger's drain at the segment, with cause <c>Corrupt</c>, the key, its log, the
    /// ledger, and the segment's start; the segment admits no more writes, and gets no marker.
    /// </summary>
    [Fact]
    public async Task ACorruptLogBlocksItsLedgerWithItsKey()
    {
        await using var world = await ArchiveLogWorld.StartAsync(Token);
        var ledger = world.Ledgers.OpenContaining([0]).Id;
        await world.ProposeAsync(ledger, ("a", RowKind.Append, "a0"));
        var start = await world.Ledgers.EndSegmentAsync(ledger, Token);
        Assert.Equal(new ArchiveLogDrainOutcome.OpenSegment(start), (await world.DrainAsync(world.Worker(), ledger, start + 1, Token)).Value);
        await world.ProposeAsync(ledger, ("a", RowKind.Append, "a1"), ("b", RowKind.Append, "b0"));
        var t = await world.CloseAsync(ledger, Token);
        // Another writer appended to a's log past its chain: its position lies past the write's end, on too few blocks.
        _ = await world.Memory.AppendAsync(ArchiveLogWorld.NameOf("a"), new ArchiveLogPosition(1, 0, 2, 1), [new ReadOnlySequence<byte>("xyz"u8.ToArray())], Token);

        var blocked = await Assert.ThrowsAsync<ArchiveLogDrainBlockedException>(() => world.DrainAsync(world.Worker(), ledger, t, Token));

        Assert.Equal((ArchiveLogDrainBlockedCause.Corrupt, "a", ArchiveLogWorld.NameOf("a"), ledger, start), (blocked.Cause, Encoding.UTF8.GetString(blocked.Key!.Value.Span), blocked.Log, blocked.Segment.Ledger, blocked.Segment.Start));
        Assert.Single(await world.MarkersAsync(Token));
    }

    /// <summary>A write the store refuses for a reason no retry removes blocks the ledger with cause <c>Refused</c>.</summary>
    [Fact]
    public async Task ARefusedWriteBlocksItsLedger()
    {
        await using var world = await ArchiveLogWorld.StartAsync(Token);
        var ledger = world.Ledgers.OpenContaining([0]).Id;
        await world.ProposeAsync(ledger, ("a", RowKind.Append, "a0"));
        var t = await world.CloseAsync(ledger, Token);
        _ = world.Logs.Refused.Add(ArchiveLogWorld.NameOf("a").Value);

        var blocked = await Assert.ThrowsAsync<ArchiveLogDrainBlockedException>(() => world.DrainAsync(world.Worker(), ledger, t, Token));

        Assert.Equal(ArchiveLogDrainBlockedCause.Refused, blocked.Cause);
        Assert.IsType<ArchiveLogRefusedException>(blocked.InnerException);
    }

    /// <summary>
    /// A write which the store classifies <c>PredecessorMissing</c>, once every older ledger dropped and the earlier
    /// segments completed, is a log which lost a write it held: it blocks with cause <c>Corrupt</c>.
    /// </summary>
    [Fact]
    public async Task AWriteWhosePredecessorIsMissingBlocksWithCauseCorrupt()
    {
        await using var world = await ArchiveLogWorld.StartAsync(Token);
        var ledger = world.Ledgers.OpenContaining([0]).Id;
        await world.Ledgers.ProposeWithTokenAsync(ledger, LiveRow.Of("a", RowKind.Append, "a0"), ArchiveLogTokens.Encode(new ArchiveLogPosition(1, 0, 5, 1)), Token);
        var t = await world.CloseAsync(ledger, Token);

        var blocked = await Assert.ThrowsAsync<ArchiveLogDrainBlockedException>(() => world.DrainAsync(world.Worker(), ledger, t, Token));

        Assert.Equal(ArchiveLogDrainBlockedCause.Corrupt, blocked.Cause);
        Assert.Empty(await world.MarkersAsync(Token));
    }

    /// <summary>The <c>marks-unheld</c> mutant marks a segment whose write threw, so a later drain never writes it again.</summary>
    [Fact]
    public async Task TheMarksUnheldMutantLosesAFailedWrite()
    {
        await using var world = await ArchiveLogWorld.StartAsync(Token);
        var ledger = world.Ledgers.OpenContaining([0]).Id;
        await world.ProposeAsync(ledger, ("a", RowKind.Append, "a0"));
        var t = await world.CloseAsync(ledger, Token);
        world.Faults = static request => request.Kind == ArchiveLogRequestKind.AppendUnit ? ArchiveLogFault.FailBefore : ArchiveLogFault.None;
        Assert.Same(ArchiveLogDrainOutcome.Complete.Instance, (await world.DrainAsync(world.Worker(marksUnheld: true), ledger, t, Token)).Value);
        world.Faults = null;

        Assert.Same(ArchiveLogDrainOutcome.Complete.Instance, (await world.DrainAsync(world.Worker(), ledger, t, Token)).Value);
        Assert.False((await world.ReadAsync("a", Token)).SameAs(world.Fold("a")));
    }

    /// <summary>A worker writes <c>DrainConcurrency</c> keys at once, and no more: each write the probe holds briefly, so they overlap.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task AWorkerWritesAtMostItsConcurrencyAtOnce(int concurrency)
    {
        await using var world = await ArchiveLogWorld.StartAsync(Token);
        var ledger = world.Ledgers.OpenContaining([0]).Id;
        await world.ProposeAsync(ledger, [.. Enumerable.Range(0, 8).Select(static key => ($"k{key}", RowKind.Append, "x"))]);
        var t = await world.CloseAsync(ledger, Token);

        world.Logs.WriteDelay = TimeSpan.FromMilliseconds(20);
        Assert.Same(ArchiveLogDrainOutcome.Complete.Instance, (await world.DrainAsync(world.Worker(new ArchiveLogOptions { DrainConcurrency = concurrency }), ledger, t, Token)).Value);

        Assert.Equal(concurrency, world.Logs.MostInFlight);
    }

    /// <summary>
    /// A worker admits its writes' requests within its rate, the node's share of the cluster's budget, which may be
    /// reassigned: three keys' first writes, two requests each, and the segment's marker are admitted at 0, 2, 4, and 6
    /// seconds at one request a second, so the drain ends six seconds in, within a second.
    /// </summary>
    [Fact]
    public async Task AWorkerKeepsWithinItsRequestRate()
    {
        await using var world = await ArchiveLogWorld.StartAsync(Token);
        var ledger = world.Ledgers.OpenContaining([0]).Id;
        await world.ProposeAsync(ledger, ("a", RowKind.Append, "a"), ("b", RowKind.Append, "b"), ("c", RowKind.Append, "c"));
        var t = await world.CloseAsync(ledger, Token);
        var time = new FakeTimeProvider();
        var worker = world.Worker(new ArchiveLogOptions { DrainRequestsPerSecond = 100 }, time);
        worker.RequestsPerSecond = 1;
        var started = time.GetUtcNow();

        var draining = world.DrainAsync(worker, ledger, t, Token);
        while (!draining.IsCompleted)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(5), TimeProvider.System, Token);
            time.Advance(TimeSpan.FromMilliseconds(250));
        }

        Assert.Same(ArchiveLogDrainOutcome.Complete.Instance, (await draining).Value);
        Assert.InRange(time.GetUtcNow() - started, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(7));
        Assert.Throws<ArgumentOutOfRangeException>(() => worker.RequestsPerSecond = 0);
    }

    /// <summary>
    /// <c>DrainMemoryBytes</c> is a hard bound (archive-logs.md §8.1): a segment whose rows charge exactly the budget drains,
    /// and one whose rows pass it by a byte fails with <see cref="DrainMemoryExceededException"/>, naming the ledger and the
    /// segment, writing nothing; either way the memory returns whole. A key of one byte with one row of one byte charges
    /// the key, its entry, the command's entry, and the body.
    /// </summary>
    [Theory]
    [InlineData(0, true)]
    [InlineData(-1, false)]
    public async Task ASegmentDrainsWithinTheDrainMemoryAndFailsPastIt(long slack, bool drains)
    {
        await using var world = await ArchiveLogWorld.StartAsync(Token);
        var ledger = world.Ledgers.OpenContaining([0]).Id;
        await world.ProposeAsync(ledger, ("a", RowKind.Append, "x"));
        var t = await world.CloseAsync(ledger, Token);
        var budget = 1 + KeyEntryBytes + CommandEntryBytes + 1 + slack;
        var worker = world.Worker(new ArchiveLogOptions { DrainMemoryBytes = budget });
        var mark = world.RequestMark;

        if (drains)
        {
            Assert.Same(ArchiveLogDrainOutcome.Complete.Instance, (await world.DrainAsync(worker, ledger, t, Token)).Value);
            Assert.True((await world.ReadAsync("a", Token)).SameAs(world.Fold("a")));
        }
        else
        {
            var exceeded = await Assert.ThrowsAsync<DrainMemoryExceededException>(() => world.DrainAsync(worker, ledger, t, Token));
            Assert.Equal((new LedgerSegmentId(ledger, Slot.Zero), budget), (exceeded.Segment, exceeded.Budget));
            Assert.Empty(world.WritesSince(mark));
            Assert.Equal(1, worker.SegmentsDrainedAlone);
        }

        Assert.Equal(budget, worker.AvailableMemoryBytes);
    }

    /// <summary>
    /// Two drains whose segments each fit the drain memory but not together take turns: the second waits for the first's
    /// memory, and both complete, the memory returning whole.
    /// </summary>
    [Fact]
    public async Task TwoDrainsTakeTurnsWithinTheDrainMemory()
    {
        await using var world = await ArchiveLogWorld.StartAsync(Token);
        var ledger = world.Ledgers.OpenContaining([0]).Id;
        await world.ProposeAsync(ledger, ("a", RowKind.Append, "x"));
        var t = await world.CloseAsync(ledger, Token);
        var budget = 3 * (1 + KeyEntryBytes + CommandEntryBytes + 1) / 2;
        var worker = world.Worker(new ArchiveLogOptions { DrainMemoryBytes = budget });

        var outcomes = await Task.WhenAll(world.DrainAsync(worker, ledger, t, Token), world.DrainAsync(worker, ledger, t, Token));

        Assert.All(outcomes, static outcome => Assert.Same(ArchiveLogDrainOutcome.Complete.Instance, outcome.Value));
        Assert.Equal((budget, 0), (worker.AvailableMemoryBytes, worker.SegmentsDrainedAlone));
        Assert.True((await world.ReadAsync("a", Token)).SameAs(world.Fold("a")));
    }

    /// <summary>
    /// A defining row whose stored token lies behind its key's chain, which the store would classify held since its log is
    /// already past it, fails the drain at its segment with cause <c>Corrupt</c> before any write, rather than letting the
    /// segment be marked and the ledger drop with the key's commands lost (HI-I4): the drain checks each token against
    /// <c>Plan</c> of its key's write in the segment it drained before.
    /// </summary>
    [Fact]
    public async Task ADefiningRowWhoseTokenLiesBehindTheLogFailsTheDrain()
    {
        await using var world = await ArchiveLogWorld.StartAsync(Token);
        var ledger = world.Ledgers.OpenContaining([0]).Id;
        await world.ProposeAsync(ledger, ("a", RowKind.Append, "a0"));
        var second = await world.Ledgers.EndSegmentAsync(ledger, Token);
        await world.Ledgers.ProposeWithTokenAsync(ledger, LiveRow.Of("a", RowKind.Append, "a1"), default, Token);
        var t = await world.CloseAsync(ledger, Token);

        var blocked = await Assert.ThrowsAsync<ArchiveLogDrainBlockedException>(() => world.DrainAsync(world.Worker(), ledger, t, Token));

        Assert.Equal((ArchiveLogDrainBlockedCause.Corrupt, new LedgerSegmentId(ledger, second)), (blocked.Cause, blocked.Segment));
        Assert.Contains("HI-I4", blocked.Message, StringComparison.Ordinal);
        Assert.Single(await world.MarkersAsync(Token));
    }

    /// <summary>
    /// A ledger's highest drain marker which breaks its format, names an end not past its start or past <c>T</c>, names a
    /// slot which starts no segment, or is listed and then reads as absent while its ledger is still listed, blocks the
    /// drain with cause <c>CorruptMarker</c>, naming the marker's segment, and writes nothing.
    /// </summary>
    [Theory]
    [InlineData("malformed")]
    [InlineData("end-not-past-start")]
    [InlineData("end-past-t")]
    [InlineData("not-a-segment-start")]
    [InlineData("listed-then-absent")]
    public async Task ACorruptMarkerBlocksTheDrain(string corruption)
    {
        await using var world = await ArchiveLogWorld.StartAsync(Token);
        var ledger = world.Ledgers.OpenContaining([0]).Id;
        await world.ProposeAsync(ledger, ("a", RowKind.Append, "a0"));
        await world.ProposeAsync(ledger, ("a", RowKind.Append, "a1"));
        var second = await world.Ledgers.EndSegmentAsync(ledger, Token);
        await world.ProposeAsync(ledger, ("a", RowKind.Append, "a2"));
        var t = await world.CloseAsync(ledger, Token);
        var name = ArchiveLogNames.DrainMarker(ledger, Slot.Zero);
        IObjectStore objects = world.Objects;
        if (corruption == "listed-then-absent")
        {
            objects = new PhantomListingStore(world.Objects, new ObjectName(name));
        }
        else
        {
            byte[] content = corruption switch
            {
                "malformed" => "slot two"u8.ToArray(),
                "end-not-past-start" => ArchiveLogNames.DrainMarkerContent(Slot.Zero),
                "end-past-t" => ArchiveLogNames.DrainMarkerContent(t + 1),
                _ => ArchiveLogNames.DrainMarkerContent(second - 1),
            };
            using var buffer = ArcBuffer.Wrap(content);
            _ = await world.Objects.CreateAsync(new ObjectName(name), buffer, null, Token);
        }

        var gate = await ArchiveLogWorld.PassGateAsync(new LogArchiveStore(world.Logs, objects, ArchiveLogNames.Hexadecimal), Token);
        var mark = world.RequestMark;
        var blocked = await Assert.ThrowsAsync<ArchiveLogDrainBlockedException>(() => world.DrainAsync(world.Worker(gate: gate), ledger, t, Token));

        Assert.Equal((ArchiveLogDrainBlockedCause.CorruptMarker, new LedgerSegmentId(ledger, Slot.Zero), null), (blocked.Cause, blocked.Segment, blocked.Key));
        Assert.Empty(world.WritesSince(mark));
    }

    /// <summary>
    /// A segment whose write fails stops admitting its writes at the failure, and the worker remembers the writes it saw
    /// held, so the segment's next attempt writes only the keys not yet held; every attempt reports its failure.
    /// </summary>
    [Fact]
    public async Task ARetryOfAFailedSegmentWritesOnlyItsKeysNotYetHeld()
    {
        await using var world = await ArchiveLogWorld.StartAsync(Token);
        var ledger = world.Ledgers.OpenContaining([0]).Id;
        await world.ProposeAsync(ledger, [.. Enumerable.Range(0, 6).Select(static key => ($"k{key}", RowKind.Append, "x"))]);
        var t = await world.CloseAsync(ledger, Token);
        var refused = ArchiveLogWorld.NameOf("k2").Value;
        _ = world.Logs.Refused.Add(refused);
        var worker = world.Worker(new ArchiveLogOptions { DrainConcurrency = 1 });

        var first = await Assert.ThrowsAsync<ArchiveLogDrainBlockedException>(() => world.DrainAsync(worker, ledger, t, Token));
        Assert.Equal(ArchiveLogDrainBlockedCause.Refused, first.Cause);
        Assert.Equal([ArchiveLogWorld.NameOf("k0").Value, ArchiveLogWorld.NameOf("k1").Value], world.WritesSince(0).Select(static request => request.Log).Distinct());
        var mark = world.RequestMark;

        var again = await Assert.ThrowsAsync<ArchiveLogDrainBlockedException>(() => world.DrainAsync(worker, ledger, t, Token));
        Assert.Equal(ArchiveLogDrainBlockedCause.Refused, again.Cause);
        Assert.Empty(world.WritesSince(mark));

        _ = world.Logs.Refused.Remove(refused);
        Assert.Same(ArchiveLogDrainOutcome.Complete.Instance, (await world.DrainAsync(worker, ledger, t, Token)).Value);
        Assert.DoesNotContain(world.WritesSince(mark), static request => request.Log is var log && (log == ArchiveLogWorld.NameOf("k0").Value || log == ArchiveLogWorld.NameOf("k1").Value));
        foreach (var key in world.Keys)
        {
            Assert.True((await world.ReadAsync(key, Token)).SameAs(world.Fold(key)));
        }
    }

    /// <summary>
    /// A worker forgets a ledger's drains once its drop applies, and remembers the drop until a map value no longer lists
    /// the ledger, so a drain of it while the process's view still lists it answers <c>Dropped</c> rather than draining and
    /// dropping it again; and it forgets every ledger a map read shows dropped, so what it remembers is bounded by the
    /// ledgers it drains.
    /// </summary>
    [Fact]
    public async Task AWorkerForgetsADroppedLedger()
    {
        await using var world = await ArchiveLogWorld.StartAsync(Token);
        var ledger = world.Ledgers.OpenContaining([0]).Id;
        await world.ProposeAsync(ledger, ("a", RowKind.Append, "a0"));
        var t = await world.CloseAsync(ledger, Token);
        var worker = world.Worker();
        var other = world.Worker();
        var metadata = world.Ledgers.Ledger(ledger);

        Assert.Same(ArchiveLogDrainOutcome.Complete.Instance, (await world.DrainAsync(worker, ledger, t, Token)).Value);
        Assert.Same(ArchiveLogDrainOutcome.Complete.Instance, (await world.DrainAsync(other, ledger, t, Token)).Value);
        Assert.Equal((1, 1, 0, 0), worker.Remembered);
        Assert.IsType<LedgerMapCommitOutcome.Applied>((await worker.SubmitDropAsync(ledger, world.Ledgers.CommitAsync, Token)).Value);

        Assert.Equal((0, 0, 0, 1), worker.Remembered);
        Assert.NotNull(world.Ledgers.Map.Current.Get(ledger));
        Assert.Same(ArchiveLogDrainOutcome.Dropped.Instance, (await worker.DrainAsync(metadata, t, Token)).Value);
        Assert.Equal((0, 0, 0, 1), worker.Remembered);

        _ = await world.Ledgers.Map.ReadAsync(Token);
        Assert.Same(ArchiveLogDrainOutcome.Dropped.Instance, (await other.DrainAsync(metadata, t, Token)).Value);
        Assert.Equal((0, 0, 0, 0), other.Remembered);
        Assert.Same(ArchiveLogDrainOutcome.Dropped.Instance, (await worker.DrainAsync(metadata, t, Token)).Value);

        // The worker answers a drop it remembers before it reads the map, and forgets it at its first drain of a newer value.
        Assert.Equal((0, 0, 0, 1), worker.Remembered);
        var successor = world.Ledgers.Ledger(world.Ledgers.OpenContaining([0]).Id);
        _ = Assert.IsType<ArchiveLogDrainOutcome.Drained>((await worker.DrainAsync(successor, Slot.Zero, Token)).Value);
        Assert.Equal((0, 0, 0, 0), worker.Remembered);
    }

    /// <summary>
    /// archive-logs.md §2.4: a worker remembers only a drop which applied: one the committer refused, or whose submission
    /// threw, leaves the ledger complete, so the next drain answers <c>Complete</c> again and the drop is submitted again.
    /// </summary>
    [Fact]
    public async Task ADropWhichDidNotApplyIsNotRemembered()
    {
        await using var world = await ArchiveLogWorld.StartAsync(Token);
        var ledger = world.Ledgers.OpenContaining([0]).Id;
        await world.ProposeAsync(ledger, ("a", RowKind.Append, "a0"));
        var t = await world.CloseAsync(ledger, Token);
        var worker = world.Worker();
        var metadata = world.Ledgers.Ledger(ledger);
        Assert.Same(ArchiveLogDrainOutcome.Complete.Instance, (await world.DrainAsync(worker, ledger, t, Token)).Value);

        Assert.IsType<LedgerMapCommitOutcome.Refused>((await worker.SubmitDropAsync(ledger, static (_, _) => Task.FromResult<LedgerMapCommitOutcome>(LedgerMapCommitOutcome.Refused.Instance), Token)).Value);
        Assert.Equal(0, worker.Remembered.Dropped);
        Assert.Same(ArchiveLogDrainOutcome.Complete.Instance, (await worker.DrainAsync(metadata, t, Token)).Value);

        _ = await Assert.ThrowsAsync<IOException>(() => worker.SubmitDropAsync(ledger, static (_, _) => throw new IOException("The submission failed."), Token));
        Assert.Equal(0, worker.Remembered.Dropped);
        Assert.Same(ArchiveLogDrainOutcome.Complete.Instance, (await worker.DrainAsync(metadata, t, Token)).Value);
        Assert.IsType<LedgerMapCommitOutcome.Applied>((await worker.SubmitDropAsync(ledger, world.Ledgers.CommitAsync, Token)).Value);
        Assert.Equal(1, worker.Remembered.Dropped);
    }

    // The drain worker's charges, which the memory tests count (ArchiveLogDrainWorker).
    private const long KeyEntryBytes = 512;
    private const long CommandEntryBytes = 160;

    /// <summary>An object store which lists, beside its own objects, one which it does not hold: a marker listed and then gone.</summary>
    private sealed class PhantomListingStore(IObjectStore inner, ObjectName phantom) : IObjectStore
    {
        public ValueTask<ObjectCreateOutcome> CreateAsync(ObjectName name, ArcBuffer content, uint? crc32c, CancellationToken cancellationToken = default) =>
            inner.CreateAsync(name, content, crc32c, cancellationToken);

        public ValueTask<ArcBuffer?> ReadAsync(ObjectName name, long offset, int length, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(name, offset, length, cancellationToken);

        public ValueTask<ObjectProperties?> GetPropertiesAsync(ObjectName name, CancellationToken cancellationToken = default) =>
            inner.GetPropertiesAsync(name, cancellationToken);

        public async IAsyncEnumerable<ListedObject> ListAsync(string prefix, string? startAfter = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var listed in inner.ListAsync(prefix, startAfter, cancellationToken))
            {
                yield return listed;
            }

            if (phantom.Value.StartsWith(prefix, StringComparison.Ordinal))
            {
                yield return new ListedObject(phantom, 20);
            }
        }

        public ValueTask DeleteAsync(ObjectName name, CancellationToken cancellationToken = default) => inner.DeleteAsync(name, cancellationToken);

        public ValueTask<VersionedObject?> ReadVersionedAsync(ObjectName name, CancellationToken cancellationToken = default) =>
            inner.ReadVersionedAsync(name, cancellationToken);

        public ValueTask<ObjectReplaceOutcome> ReplaceAsync(ObjectName name, ObjectVersion? expectedVersion, ArcBuffer content, uint? crc32c, CancellationToken cancellationToken = default) =>
            inner.ReplaceAsync(name, expectedVersion, content, crc32c, cancellationToken);
    }
}
