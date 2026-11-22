using Microsoft.Extensions.Time.Testing;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Journals;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Tests;

/// <summary>
/// The snapshots a node pins for forwarded reads (orleans.md §5.5, §5.12, OR-I17), each path forced by construction on a
/// fake clock: content which fits one page is answered whole and pins nothing; longer content is reserved, in arrival order,
/// before its first page, and served from one snapshot in pages until its last page releases it; a snapshot longer than the
/// budget waits until it runs alone; a snapshot's first window is what remains of its read's deadline, which a page renews,
/// and one no page renews is released at its end; a read waits for a forwarded-read slot until an earlier read's
/// reservation; one deadline from the read's receipt bounds the slot, the history, and the reservation, any of which
/// answers <c>Busy</c> once it passes, pinning nothing; the reader's release and the node's stop release snapshots; and
/// <c>Moved</c>, a failed read, and cancellation return the slot. Every chunk a read hands over is released on every path.
/// </summary>
public sealed class ForwardedReadsTests : IDisposable
{
    private const int Page = JournalForwardedRead.PageBytes;

    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();
    private readonly FakeTimeProvider _time = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _leaks.Dispose();

    /// <summary>Content of at most one page is answered whole in the first page, and pins and reserves nothing.</summary>
    [Fact]
    public async Task ContentWhichFitsOnePageIsAnsweredWholeAndPinsNothing()
    {
        using var reads = Reads();

        var found = Found(await ReadAsync(reads, Content(Page, chunk: 1000)));

        Assert.Null(found.Snapshot);
        Assert.Equal(Bytes(0, Page), found.FirstPage.ToArray());
        Assert.Equal((0, 0L, 0), (reads.Pinned, reads.ReservedBytes, reads.SlotsHeld));
        found.FirstPage.Dispose();
    }

    /// <summary>
    /// Longer content reserves its length, answers its first page, and serves its later pages, at any offset, from the one
    /// snapshot until the last page releases it; an offset outside the content is refused as an argument, and a page after
    /// the release answers <c>Expired</c>.
    /// </summary>
    [Fact]
    public async Task LongerContentIsServedInPagesFromOneSnapshotUntilItsLastPage()
    {
        using var reads = Reads();
        const long Length = 2 * Page + 5;

        var found = Found(await ReadAsync(reads, Content(Length, chunk: 300_000)));
        var snapshot = Assert.NotNull(found.Snapshot);
        Assert.Equal((1, Length, 0), (reads.Pinned, reads.ReservedBytes, reads.SlotsHeld));
        Assert.Equal(Bytes(0, Page), Take(found.FirstPage));

        Assert.Equal(Bytes(Page, Page), PageAt(reads, snapshot, Page));
        Assert.Equal(Bytes(10, Page), PageAt(reads, snapshot, 10));
        Assert.Equal(JournalRefusal.Argument, Assert.IsType<JournalCallRefused>(reads.Page(snapshot, -1).Value).Kind);
        Assert.Equal(JournalRefusal.Argument, Assert.IsType<JournalCallRefused>(reads.Page(snapshot, Length).Value).Kind);
        Assert.Equal(Bytes(2 * Page, 5), PageAt(reads, snapshot, 2 * Page));

        Assert.Equal((0, 0L), (reads.Pinned, reads.ReservedBytes));
        Assert.Equal(JournalForwardedPage.Expired.Instance, reads.Page(snapshot, Page).Value);
    }

    /// <summary>
    /// Reservations wait in arrival order: with a snapshot pinned, a read which does not fit waits, and a later one which
    /// would fit waits behind it; releasing the first lets both in.
    /// </summary>
    [Fact]
    public async Task ReservationsWaitInArrivalOrder()
    {
        using var reads = Reads(budget: 4 * Page, slots: 3);
        var first = Assert.NotNull(Found(await ReadAsync(reads, Content(5 * Page / 2))).Snapshot);

        var second = ReadAsync(reads, Content(2 * Page));
        await UntilAsync(() => reads.Reserving == 1, "the second read to wait to reserve");
        var third = ReadAsync(reads, Content(Page + 1));
        await UntilAsync(() => reads.Reserving == 2, "the third read to wait to reserve");
        Assert.False(second.IsCompleted || third.IsCompleted);

        _ = PageAt(reads, first, Page);
        _ = PageAt(reads, first, 2 * Page);
        Drain(reads, Found(await second.WaitAsync(Patience, Token)));
        Drain(reads, Found(await third.WaitAsync(Patience, Token)));

        Assert.Equal(0L, reads.ReservedBytes);
    }

    /// <summary>A snapshot longer than the whole budget reserves all of it, so it waits until no other snapshot is pinned.</summary>
    [Fact]
    public async Task ASnapshotLongerThanTheBudgetWaitsUntilItRunsAlone()
    {
        using var reads = Reads(budget: 2 * Page, slots: 2);
        var first = Assert.NotNull(Found(await ReadAsync(reads, Content(Page + 1))).Snapshot);

        var large = ReadAsync(reads, Content(3 * Page));
        await UntilAsync(() => reads.Reserving == 1, "the large read to wait to reserve");

        _ = PageAt(reads, first, Page);
        var found = Found(await large.WaitAsync(Patience, Token));
        Assert.Equal(2L * Page, reads.ReservedBytes);
        Drain(reads, found);
    }

    /// <summary>
    /// A snapshot's window is a call's deadline from its read's receipt and from each later page: a page just before it ends
    /// renews it by a whole deadline, and a snapshot no page asks for by then is released, its next page <c>Expired</c>.
    /// </summary>
    [Fact]
    public async Task ASnapshotNoPageRenewsIsReleasedAtTheEndOfItsWindow()
    {
        using var reads = Reads();
        var snapshot = Assert.NotNull(Found(await ReadAsync(reads, Content(3 * Page))).Snapshot);

        _time.Advance(Deadline - TimeSpan.FromTicks(1));
        _ = PageAt(reads, snapshot, Page);
        _time.Advance(Deadline - TimeSpan.FromTicks(1));
        Assert.Equal(1, reads.Pinned);
        _time.Advance(TimeSpan.FromTicks(1));

        Assert.Equal((0, 0L), (reads.Pinned, reads.ReservedBytes));
        Assert.Equal(JournalForwardedPage.Expired.Instance, reads.Page(snapshot, 2 * Page).Value);
    }

    /// <summary>
    /// A snapshot's first window is what remains of its read's deadline, which counted the history read: a read whose
    /// history took half the deadline keeps its snapshot only for the other half.
    /// </summary>
    [Fact]
    public async Task ASnapshotsFirstWindowIsWhatRemainsOfItsReadsDeadline()
    {
        using var reads = Reads();
        var snapshot = Assert.NotNull(Found(await reads.ReadAsync(
            _ =>
            {
                _time.Advance(Deadline / 2);
                return Task.FromResult(Content(3 * Page));
            },
            reads.DeadlineFromNow(),
            Token)).Snapshot);

        _time.Advance(Deadline / 2 - TimeSpan.FromTicks(1));
        Assert.Equal(1, reads.Pinned);
        _time.Advance(TimeSpan.FromTicks(1));

        Assert.Equal((0, 0L), (reads.Pinned, reads.ReservedBytes));
        Assert.Equal(JournalForwardedPage.Expired.Instance, reads.Page(snapshot, Page).Value);
    }

    /// <summary>
    /// A read whose history arrives after its deadline answers <c>Busy</c> and pins nothing, its content released: its caller
    /// has stopped waiting.
    /// </summary>
    [Fact]
    public async Task AReadWhoseHistoryArrivesPastItsDeadlineIsBusyAndPinsNothing()
    {
        using var reads = Reads();

        var read = await reads.ReadAsync(
            _ =>
            {
                _time.Advance(Deadline);
                return Task.FromResult(Content(2 * Page));
            },
            reads.DeadlineFromNow(),
            Token);

        Assert.Equal(JournalForwardedRead.Busy.Instance, read.Value);
        Assert.Equal((0, 0L, 0), (reads.Pinned, reads.ReservedBytes, reads.SlotsHeld));
    }

    /// <summary>
    /// A read holds a forwarded-read slot from before its history read until its reservation: with one slot, and a read
    /// waiting to reserve, the next read does not read the journal until the first is reserved.
    /// </summary>
    [Fact]
    public async Task AReadWaitsForASlotUntilAnEarlierReadIsReserved()
    {
        using var reads = Reads(budget: 2 * Page, slots: 1);
        var pinned = Found(await ReadAsync(reads, Content(2 * Page)));
        var reserving = ReadAsync(reads, Content(Page + 1));
        await UntilAsync(() => reads.Reserving == 1, "the second read to wait to reserve");
        var readsMade = 0;

        var next = reads.ReadAsync(
            _ =>
            {
                readsMade++;
                return Task.FromResult(Content(10));
            },
            reads.DeadlineFromNow(),
            Token);
        await UntilAsync(() => reads.WaitingForSlot == 1, "the third read to wait for a slot");
        Assert.Equal(0, readsMade);

        Drain(reads, pinned);
        Drain(reads, Found(await reserving.WaitAsync(Patience, Token)));
        Found(await next.WaitAsync(Patience, Token)).FirstPage.Dispose();
        Assert.Equal(1, readsMade);
    }

    /// <summary>
    /// A read still waiting for a slot at its deadline answers <c>Busy</c> without reading the journal; the slot's holder,
    /// here a read whose history read is held, is unaffected.
    /// </summary>
    [Fact]
    public async Task AReadWhichWaitsForASlotPastItsDeadlineIsBusyWithoutReading()
    {
        using var reads = Reads(slots: 1);
        var history = new TaskCompletionSource<ForwardedReads.Source>(TaskCreationOptions.RunContinuationsAsynchronously);
        var holding = reads.ReadAsync(_ => history.Task, reads.DeadlineFromNow(), Token);
        await UntilAsync(() => reads.SlotsHeld == 1, "the first read to hold the slot");
        var readsMade = 0;
        var waiting = reads.ReadAsync(
            _ =>
            {
                readsMade++;
                return Task.FromResult(Content(10));
            },
            reads.DeadlineFromNow(),
            Token);
        await UntilAsync(() => reads.WaitingForSlot == 1, "the third read to wait for a slot");

        _time.Advance(Deadline);

        Assert.Equal(JournalForwardedRead.Busy.Instance, (await waiting.WaitAsync(Patience, Token)).Value);
        Assert.Equal(0, readsMade);
        history.SetResult(Content(10));
        Found(await holding.WaitAsync(Patience, Token)).FirstPage.Dispose();
        Assert.Equal(0, reads.SlotsHeld);
    }

    /// <summary>
    /// One deadline from the read's receipt bounds both its waits: a read which waited half of it for a slot and then waits
    /// to reserve answers <c>Busy</c> at that deadline, not a whole deadline after its slot. A snapshot renewed by a page
    /// keeps the budget full meanwhile.
    /// </summary>
    [Fact]
    public async Task OneDeadlineBoundsTheWaitForASlotAndTheReservation()
    {
        using var reads = Reads(budget: 4 * Page, slots: 1);
        var whole = Found(await ReadAsync(reads, Content(4 * Page)));
        var first = ReadAsync(reads, Content(2 * Page + 1));
        await UntilAsync(() => reads.Reserving == 1, "the first read to wait to reserve");
        var second = ReadAsync(reads, Content(3 * Page));
        await UntilAsync(() => reads.WaitingForSlot == 1, "the second read to wait for a slot");

        _time.Advance(Deadline / 2);
        Drain(reads, whole);
        var pinned = Found(await first.WaitAsync(Patience, Token));
        await UntilAsync(() => reads.Reserving == 1, "the second read to wait to reserve");
        _ = PageAt(reads, Assert.NotNull(pinned.Snapshot), Page);
        _time.Advance(Deadline / 2 - TimeSpan.FromTicks(1));
        Assert.False(second.IsCompleted);
        _time.Advance(TimeSpan.FromTicks(1));

        Assert.Equal(JournalForwardedRead.Busy.Instance, (await second.WaitAsync(Patience, Token)).Value);
        Assert.Equal(1, reads.Pinned);
        Drain(reads, pinned, from: 2 * Page);
    }

    /// <summary>
    /// The read's deadline bounds its history read too: a history read still running at the deadline is cancelled, which it
    /// may be since it has no effect, and the read answers <c>Busy</c>, returning its slot; a read whose content of one page
    /// arrives after its deadline pins nothing, so it is answered whole.
    /// </summary>
    [Fact]
    public async Task TheDeadlineEndsAHistoryReadStillRunningAndALateOnePageReadIsAnswered()
    {
        using var reads = Reads(slots: 1);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = reads.ReadAsync(
            async token =>
            {
                started.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, _time, token);
                return Content(10);
            },
            reads.DeadlineFromNow(),
            Token);
        await started.Task.WaitAsync(Patience, Token);

        _time.Advance(Deadline);

        Assert.Equal(JournalForwardedRead.Busy.Instance, (await running.WaitAsync(Patience, Token)).Value);
        Assert.Equal(0, reads.SlotsHeld);

        var late = await reads.ReadAsync(
            _ =>
            {
                _time.Advance(Deadline);
                return Task.FromResult(Content(10));
            },
            reads.DeadlineFromNow(),
            Token);
        Found(late).FirstPage.Dispose();
    }

    /// <summary>
    /// A read whose caller cancels as its reservation is granted, before it pins its snapshot, releases the reservation and
    /// its content. The cancellation's own callback, registered after the read's, runs first and releases the snapshot
    /// ahead, which grants the reservation before the read sees the cancellation.
    /// </summary>
    [Fact]
    public async Task AReadCancelledAsItsReservationIsGrantedPinsNothing()
    {
        using var reads = Reads(budget: 2 * Page, slots: 2);
        var ahead = Found(await ReadAsync(reads, Content(2 * Page)));
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var waiting = reads.ReadAsync(_ => Task.FromResult(Content(Page + 1)), reads.DeadlineFromNow(), cancel.Token);
        await UntilAsync(() => reads.Reserving == 1, "the read to wait to reserve");
        using var granting = cancel.Token.Register(() => Drain(reads, ahead));

        await cancel.CancelAsync();

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.WaitAsync(Patience, Token));
        Assert.Equal((0, 0L, 0), (reads.Pinned, reads.ReservedBytes, reads.Reserving));
    }

    /// <summary>
    /// A renewal never loses to a timer which fired as it ran: on a clock whose timers fire only when the test says, a
    /// snapshot renewed by a page after its window passed survives the stale callback, which re-arms for the renewal's
    /// remainder, and is released only when the callback runs once that remainder has passed.
    /// </summary>
    [Fact]
    public async Task AStaleTimerCallbackReArmsUntilTheLastRenewalPasses()
    {
        var clock = new RecordingTimers(_time);
        using var reads = new ForwardedReads(TestReplicaLimits.Options(), TestReplicaLimits.Of(deadline: Deadline), clock);
        var snapshot = Assert.NotNull(Found(await ReadAsync(reads, Content(3 * Page))).Snapshot);
        // The history read's expiry made a timer of its own first; the snapshot's is the last.
        var timer = clock.Timers[^1];

        _time.Advance(Deadline);
        _ = PageAt(reads, snapshot, Page);
        timer.Fire();

        Assert.Equal(1, reads.Pinned);
        Assert.Equal(Deadline, timer.Due);
        _time.Advance(Deadline);
        timer.Fire();

        Assert.Equal((0, 0L), (reads.Pinned, reads.ReservedBytes));
        Assert.Equal(JournalForwardedPage.Expired.Instance, reads.Page(snapshot, 2 * Page).Value);
    }

    /// <summary>A read the Journals answer <c>Moved</c> is answered as it is, and pins nothing and returns its slot.</summary>
    [Fact]
    public async Task AMovedReadIsAnsweredAsItIsAndReturnsItsSlot()
    {
        using var reads = Reads(slots: 1);
        var owner = new NodeId(Guid.NewGuid());

        var read = await ReadAsync(reads, new JournalMoved(owner));

        Assert.Equal(new JournalMoved(owner), read.Value);
        Assert.Equal((0, 0, 0), (reads.Pinned, reads.SlotsHeld, reads.WaitingForSlot));
    }

    /// <summary>A read whose history read fails throws its failure and returns its slot.</summary>
    [Fact]
    public async Task AFailedReadReturnsItsSlot()
    {
        using var reads = Reads(slots: 1);

        _ = await Assert.ThrowsAsync<JournalStoreCorruptException>(() => reads.ReadAsync(_ => throw new JournalStoreCorruptException("A test's corruption."), reads.DeadlineFromNow(), Token));

        Assert.Equal(0, reads.SlotsHeld);
        Found(await ReadAsync(reads, Content(10))).FirstPage.Dispose();
    }

    /// <summary>
    /// A read cancelled while it waits for a slot reads nothing, and one cancelled while it waits to reserve releases its
    /// content; each leaves the slots and the budget as they were.
    /// </summary>
    [Fact]
    public async Task ACancelledReadReleasesItsContentAndHoldsNothing()
    {
        using var reads = Reads(budget: 2 * Page, slots: 1);
        var pinned = Found(await ReadAsync(reads, Content(2 * Page)));
        using var reservingCancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var reserving = reads.ReadAsync(_ => Task.FromResult(Content(Page + 1)), reads.DeadlineFromNow(), reservingCancel.Token);
        await UntilAsync(() => reads.Reserving == 1, "the second read to wait to reserve");
        using var slotCancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var readsMade = 0;
        var waiting = reads.ReadAsync(
            _ =>
            {
                readsMade++;
                return Task.FromResult(Content(10));
            },
            reads.DeadlineFromNow(),
            slotCancel.Token);
        await UntilAsync(() => reads.WaitingForSlot == 1, "the third read to wait for a slot");

        await slotCancel.CancelAsync();
        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.WaitAsync(Patience, Token));
        await reservingCancel.CancelAsync();
        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reserving.WaitAsync(Patience, Token));

        Assert.Equal(0, readsMade);
        Assert.Equal((0, 0, 0, 1), (reads.Reserving, reads.WaitingForSlot, reads.SlotsHeld, reads.Pinned));
        Drain(reads, pinned);
    }

    /// <summary>
    /// A snapshot whose first page cannot be copied, here content whose chunks hold less than its length, is released: its
    /// reservation returns and its chunks are released.
    /// </summary>
    [Fact]
    public async Task ASnapshotWhoseFirstPageFailsIsReleased()
    {
        using var reads = Reads();
        ForwardedReads.Source shortOfItsLength = new ForwardedReads.Content(JournalVersion.Origin, Exists: true, 2 * Page, ReplaceRequested: false, [ArcBuffer.Wrap(Bytes(0, 10))]);

        _ = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => ReadAsync(reads, shortOfItsLength));

        Assert.Equal((0, 0L, 0), (reads.Pinned, reads.ReservedBytes, reads.SlotsHeld));
    }

    /// <summary>The reader's release of an abandoned read's snapshot releases it; its next page answers <c>Expired</c>.</summary>
    [Fact]
    public async Task TheReadersReleaseReleasesTheSnapshot()
    {
        using var reads = Reads();
        var found = Found(await ReadAsync(reads, Content(2 * Page)));
        found.FirstPage.Dispose();

        reads.Release(Assert.NotNull(found.Snapshot));
        reads.Release(found.Snapshot.Value);

        Assert.Equal((0, 0L), (reads.Pinned, reads.ReservedBytes));
        Assert.Equal(JournalForwardedPage.Expired.Instance, reads.Page(found.Snapshot.Value, Page).Value);
    }

    /// <summary>The node's stop releases every pinned snapshot, whose next page answers <c>Expired</c>.</summary>
    [Fact]
    public async Task TheNodesStopReleasesEveryPinnedSnapshot()
    {
        using var reads = Reads();
        var found = Found(await ReadAsync(reads, Content(2 * Page)));
        found.FirstPage.Dispose();

        reads.Stop();

        Assert.Equal((0, 0L), (reads.Pinned, reads.ReservedBytes));
        Assert.Equal(JournalForwardedPage.Expired.Instance, reads.Page(Assert.NotNull(found.Snapshot), Page).Value);
    }

    /// <summary>
    /// A stop which runs while a read assembles its content, before it pins its snapshot, still releases that snapshot: the
    /// read answers its first page, and its next page <c>Expired</c>.
    /// </summary>
    [Fact]
    public async Task AStopWhileAReadAssemblesItsContentReleasesTheSnapshotItPins()
    {
        using var reads = Reads();

        var found = Found(await reads.ReadAsync(
            _ =>
            {
                reads.Stop();
                return Task.FromResult(Content(2 * Page));
            },
            reads.DeadlineFromNow(),
            Token));

        Assert.Equal(Bytes(0, Page), Take(found.FirstPage));
        Assert.Equal((0, 0L), (reads.Pinned, reads.ReservedBytes));
        Assert.Equal(JournalForwardedPage.Expired.Instance, reads.Page(Assert.NotNull(found.Snapshot), Page).Value);
    }

    private static Task<JournalForwardedRead> ReadAsync(ForwardedReads reads, ForwardedReads.Source source) =>
        reads.ReadAsync(_ => Task.FromResult(source), reads.DeadlineFromNow(), Token);

    private static byte[] Bytes(long offset, int length) => [.. Enumerable.Range(0, length).Select(index => (byte)((offset + index) % 251))];

    /// <summary>Returns content of <paramref name="length"/> bytes, byte <c>i</c> being <c>i mod 251</c>, in chunks of <paramref name="chunk"/>.</summary>
    private static ForwardedReads.Source Content(long length, int chunk = Page / 3)
    {
        var chunks = new List<ArcBuffer>();
        for (long offset = 0; offset < length; offset += chunk)
        {
            chunks.Add(ArcBuffer.Wrap(Bytes(offset, (int)Math.Min(chunk, length - offset))));
        }

        return new ForwardedReads.Content(JournalVersion.Origin, Exists: true, length, ReplaceRequested: false, [.. chunks]);
    }

    private static JournalForwardedRead.Found Found(JournalForwardedRead read) => Assert.IsType<JournalForwardedRead.Found>(read.Value);

    /// <summary>
    /// Releases <paramref name="found"/>'s first page, and reads its snapshot's pages from <paramref name="from"/> to the last,
    /// which releases it.
    /// </summary>
    private static void Drain(ForwardedReads reads, JournalForwardedRead.Found found, long from = Page)
    {
        found.FirstPage.Dispose();
        var snapshot = Assert.NotNull(found.Snapshot);
        for (var offset = from; offset < found.ContentLength; offset += Page)
        {
            _ = PageAt(reads, snapshot, offset);
        }
    }

    private static byte[] Take(OrleansBuffer bytes)
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

    private static byte[] PageAt(ForwardedReads reads, Guid snapshot, long offset) => Take(Assert.IsType<JournalForwardedPage.Page>(reads.Page(snapshot, offset).Value).Bytes);

    /// <summary>Waits until <paramref name="condition"/> holds, failing with <paramref name="what"/> once the test's patience passes.</summary>
    private static async Task UntilAsync(Func<bool> condition, string what)
    {
        var started = TimeProvider.System.GetTimestamp();
        while (!condition())
        {
            Assert.True(TimeProvider.System.GetElapsedTime(started) < Patience, $"Waited {Patience} for {what}.");
            await Task.Delay(TimeSpan.FromMilliseconds(1), TimeProvider.System, Token);
        }
    }

    /// <summary>A clock whose timers fire only when the test fires them, keeping the fake clock's time.</summary>
    private sealed class RecordingTimers(FakeTimeProvider time) : TimeProvider
    {
        public List<ManualTimer> Timers { get; } = [];

        public override long TimestampFrequency => time.TimestampFrequency;

        public override long GetTimestamp() => time.GetTimestamp();

        public override DateTimeOffset GetUtcNow() => time.GetUtcNow();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(callback, state) { Due = dueTime };
            Timers.Add(timer);
            return timer;
        }
    }

    /// <summary>A timer which records its due time and runs its callback only when <see cref="Fire"/> is called.</summary>
    private sealed class ManualTimer(TimerCallback callback, object? state) : ITimer
    {
        public TimeSpan Due { get; set; }

        public void Fire() => callback(state);

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            Due = dueTime;
            return true;
        }

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private ForwardedReads Reads(long budget = 256L << 20, int slots = 4) =>
        new(TestReplicaLimits.Options(options =>
        {
            options.MaxForwardedReadBytes = budget;
            options.Journals.MaxConcurrentHistoryReads = slots;
        }), TestReplicaLimits.Of(deadline: Deadline), _time);
}
