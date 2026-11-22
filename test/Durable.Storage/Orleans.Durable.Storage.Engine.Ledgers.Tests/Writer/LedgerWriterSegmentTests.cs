using System.Buffers;
using CsCheck;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Ledgers.Memory;
using Orleans.Durable.Storage.Testing;
using Primary = Orleans.Durable.Storage.Engine.Ledgers.Tests.Writer.LedgerWriterProposalTests.Primary;

namespace Orleans.Durable.Storage.Engine.Ledgers.Tests.Writer;

#pragma warning disable CA2000 // Each replica double passes to a Ledger, which owns and disposes it.

/// <summary>
/// The writer's segments and proposal budget (ledgers.md §2.4, §8.2, §8.7), judged by the segment decoder over the
/// decided slots: a batch starts a segment at slot 0, after a marker, and once the open segment holding a batch with rows
/// reached <see cref="LedgerWriterOptions.MaxSegmentBytes"/>, defined <see cref="LedgerWriterOptions.MaxSegmentKeys"/>,
/// or aged past the writer's drawn age; a marker starts a segment which ends by neither age nor size while it holds only
/// the marker; the batches created and not yet decided hold at most <see cref="LedgerWriterOptions.MaxProposalBytes"/>.
/// </summary>
public sealed class LedgerWriterSegmentTests : IDisposable
{
    private const int Window = 4;
    private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(10);

    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    public void Dispose() => _leaks.Dispose();

    /// <summary>
    /// ledgers.md §8.2: a batch starts a segment once the open segment's batches total at least
    /// <see cref="LedgerWriterOptions.MaxSegmentBytes"/>: with the bound at exactly the first two batches' bytes the third
    /// batch starts one, and with it a byte above them the third batch does not.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ASegmentEndsOnceItsBatchesReachMaxSegmentBytes(bool atThreshold)
    {
        var token = TestContext.Current.CancellationToken;
        long firstTwo;
        await using (var trial = await StartAsync(LedgerWriterOptions.Default, token))
        {
            await ProposeKeysAsync(trial.Primary, token, [1], [2]);
            var decided = await ReadDecidedAsync(trial.Primary, 2, token);
            firstTwo = decided[0].Length + decided[1].Length;
        }

        var options = Options(maxSegmentBytes: firstTwo + (atThreshold ? 0 : 1));
        await using var primary = await StartAsync(options, token);
        byte[][] keys = [.. Enumerable.Range(1, 12).Select(static key => new[] { (byte)key })];
        await ProposeKeysAsync(primary.Primary, token, keys);

        var segments = await ReadDecidedAsync(primary.Primary, keys.Length, token);
        Judge(segments, options);
        Assert.Equal(atThreshold, segments[2].IsStart);
        Assert.False(segments[1].IsStart);
    }

    /// <summary>
    /// ledgers.md §8.2: a batch starts a segment when its rows, each counted as a key it may define, would take the open
    /// segment past <see cref="LedgerWriterOptions.MaxSegmentKeys"/>, so the writer's dictionary never holds more, and
    /// holds only the new segment's keys after it starts; a batch of more rows than the bound is refused. The writer gives
    /// its host's sink each segment it ends, with the keys it defined, and its open segment once at its disposal.
    /// </summary>
    [Fact]
    public async Task ASegmentEndsOnceItDefinesMaxSegmentKeys()
    {
        var token = TestContext.Current.CancellationToken;
        var options = Options(maxSegmentKeys: 3);
        List<LedgerSegmentKeys> ended = [];
        await using var primary = await StartAsync(options, token, segmentEnded: ended.Add);
        byte[][][] batches = [[[1]], [[2]], [[1], [2]], [[3], [4]], [[1]], [[5]], [[5], [6]], [[7]], [[7]], [[8], [9], [10]], [[1]]];
        foreach (var keys in batches)
        {
            await ProposeRowsAsync(primary.Primary, keys, token);
            Assert.InRange(primary.Primary.Writer.DictionaryKeys, 1, options.MaxSegmentKeys);
        }

        var segments = await ReadDecidedAsync(primary.Primary, batches.Length, token);
        Judge(segments, options);
        Assert.Equal([0L, 2, 3, 5, 8, 9, 10], segments.Where(static batch => batch.IsStart).Select(static batch => batch.Slot));
        Assert.Equal(1, primary.Primary.Writer.DictionaryKeys);
        Assert.Equal([(0L, 2), (2, 2), (3, 3), (5, 3), (8, 1), (9, 3)], ended.Select(static segment => (segment.Segment.Start.Value, segment.KeyCount)));
        await primary.Primary.Writer.DisposeAsync();
        await primary.Primary.Writer.DisposeAsync();
        Assert.Equal((10L, 1), (ended[^1].Segment.Start.Value, ended[^1].KeyCount));
        Assert.Equal(7, ended.Count);
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => { _ = primary.Primary.Writer.CreateBatchAsync(rowCount: 4, 1024, token).AsTask(); });
    }

    /// <summary>
    /// ledgers.md §2.4, §8.2: <see cref="LedgerWriter.EndSegmentAsync"/> proposes a marker, a batch of no rows which starts
    /// a segment; a segment which holds only its marker ends by neither size nor age, so the next batch with rows continues
    /// it, defining its keys anew at offset 1, even with <see cref="LedgerWriterOptions.MaxSegmentBytes"/> at one byte.
    /// A segment which defined no key, a marker's alone, is not among the segments the writer records ended.
    /// </summary>
    [Fact]
    public async Task AMarkerStartsASegmentWhichEndsByNeitherSizeNorAgeWhileItHoldsOnlyTheMarker()
    {
        var token = TestContext.Current.CancellationToken;
        var options = Options(maxSegmentBytes: 1, maxSegmentAge: MaxAge);
        List<LedgerSegmentKeys> ended = [];
        await using var primary = await StartAsync(options, token, segmentEnded: ended.Add);
        var writer = primary.Primary.Writer;
        await ProposeRowsAsync(primary.Primary, [[1]], token);
        await ProposeRowsAsync(primary.Primary, [[1]], token);
        Assert.Equal(Durable(2), await writer.EndSegmentAsync(token));
        primary.Time.Advance(MaxAge * 2);
        await ProposeRowsAsync(primary.Primary, [[1]], token);
        await ProposeRowsAsync(primary.Primary, [[1]], token);
        Assert.Equal(Durable(5), await writer.EndSegmentAsync(token));
        Assert.Equal(Durable(6), await writer.EndSegmentAsync(token));
        await ProposeRowsAsync(primary.Primary, [[1]], token);

        var segments = await ReadDecidedAsync(primary.Primary, 8, token);
        Judge(segments, options);
        Assert.Equal([true, true, true, false, true, true, true, false], segments.Select(static batch => batch.IsStart));
        Assert.Equal(0, segments[2].Rows);
        Assert.Equal((1UL, 0UL, true), (segments[3].Offset, segments[3].OrdinalBase, segments[3].Defines));
        await writer.DisposeAsync();
        Assert.Equal([(0L, 1), (1, 1), (2, 1), (4, 1), (6, 1)], ended.Select(static segment => (segment.Segment.Start.Value, segment.KeyCount)));
    }

    /// <summary>
    /// ledgers.md §8.2: the writer ends its open segment at its drawn age, counted from the segment's first batch with rows,
    /// with a marker it proposes itself when no batch is being created; a segment holding only that marker never ages, so
    /// the next batch with rows continues it, and the age counts again from that batch.
    /// </summary>
    [Fact]
    public async Task ASegmentEndsAtItsDrawnAgeWithAMarkerCountedFromItsFirstBatchWithRows()
    {
        var token = TestContext.Current.CancellationToken;
        var options = Options(maxSegmentAge: MaxAge);
        await using var primary = await StartAsync(options, token, new Random(7));
        var writer = primary.Primary.Writer;
        var age = writer.DrawnSegmentAge;
        Assert.InRange(age, MaxAge * 0.9, MaxAge);

        await ProposeRowsAsync(primary.Primary, [[1]], token);
        primary.Time.Advance(age - TimeSpan.FromTicks(1));
        await ProposeRowsAsync(primary.Primary, [[2]], token, expected: 1);
        primary.Time.Advance(TimeSpan.FromTicks(1));
        await WriterTestData.WaitUntilAsync(() => primary.Primary.Ledger.KnownContiguousTail == new Slot(3));

        primary.Time.Advance(age * 3);
        await ProposeRowsAsync(primary.Primary, [[1]], token, expected: 3);
        primary.Time.Advance(age);
        await WriterTestData.WaitUntilAsync(() => primary.Primary.Ledger.KnownContiguousTail == new Slot(5));

        var segments = await ReadDecidedAsync(primary.Primary, 5, token);
        Judge(segments, options);
        Assert.Equal([true, false, true, false, true], segments.Select(static batch => batch.IsStart));
        Assert.Equal([1, 1, 0, 1, 0], segments.Select(static batch => batch.Rows));
    }

    /// <summary>
    /// orleans.md §5.9, §8: the writer answers the bytes it proposed in the segments at or above a watermark, markers
    /// included, and the age of the oldest of those batches with rows, the open segment's included, changing nothing; a
    /// segment which holds only its marker has no batch with rows, so it has no age. It holds an entry per ended segment
    /// until a drain's watermark passes it, and a lower watermark restores none.
    /// </summary>
    [Fact]
    public async Task TheWriterAnswersTheBytesAndTheOldestBatchsAgeAboveAWatermark()
    {
        var token = TestContext.Current.CancellationToken;
        await using var primary = await StartAsync(Options(maxSegmentAge: TimeSpan.FromHours(1)), token);
        var writer = primary.Primary.Writer;
        Assert.Equal(new LedgerUndrained(0, Age: null), writer.ObserveUndrained(Slot.Zero));

        // Segments: [0] with rows, [1, 2] a marker then rows, [3] a marker alone, and the open [4], a marker alone.
        await ProposeRowsAsync(primary.Primary, [[1]], token, expected: 0);
        primary.Time.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(Durable(1), await writer.EndSegmentAsync(token));
        await ProposeRowsAsync(primary.Primary, [[2]], token, expected: 2);
        primary.Time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(Durable(3), await writer.EndSegmentAsync(token));
        Assert.Equal(Durable(4), await writer.EndSegmentAsync(token));
        var lengths = (await ReadDecidedAsync(primary.Primary, 5, token)).Select(static batch => (long)batch.Length).ToArray();

        Assert.Equal(new LedgerUndrained(lengths.Sum(), TimeSpan.FromMinutes(6)), writer.ObserveUndrained(Slot.Zero));
        Assert.Equal(new LedgerUndrained(lengths[1..].Sum(), TimeSpan.FromMinutes(1)), writer.ObserveUndrained(new Slot(1)));
        Assert.Equal(new LedgerUndrained(lengths[3] + lengths[4], Age: null), writer.ObserveUndrained(new Slot(3)));
        Assert.Equal(3, writer.EndedSegments);

        writer.ForgetDrainedBelow(new Slot(1));
        writer.ForgetDrainedBelow(Slot.Zero);
        Assert.Equal(2, writer.EndedSegments);
        Assert.Equal(new LedgerUndrained(lengths[1..].Sum(), TimeSpan.FromMinutes(1)), writer.ObserveUndrained(Slot.Zero));
        writer.ForgetDrainedBelow(new Slot(4));
        Assert.Equal(0, writer.EndedSegments);
        Assert.Equal(new LedgerUndrained(lengths[4], Age: null), writer.ObserveUndrained(new Slot(4)));

        _ = Assert.Throws<ArgumentOutOfRangeException>(() => writer.ObserveUndrained(new Slot(5)));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => writer.ForgetDrainedBelow(new Slot(5)));
    }

    /// <summary>
    /// ledgers.md §8.2: each writer draws its segment age once, from its process's seeded random source, between nine
    /// tenths of <see cref="LedgerWriterOptions.MaxSegmentAge"/> and it, so writers of one process draw different ages and
    /// a process with the same seed draws them again.
    /// </summary>
    [Fact]
    public void TheSegmentAgeIsDrawnFromTheSeededSourceBetweenNineTenthsOfTheMaximumAndIt()
    {
        var options = Options(maxSegmentAge: MaxAge);
        var host = new LedgerWriterHost(int.MaxValue, NullLogger.Instance, options, new Random(11));
        var replay = new LedgerWriterHost(int.MaxValue, NullLogger.Instance, options, new Random(11));

        var ages = Enumerable.Range(0, 200).Select(_ => host.DrawSegmentAge()).ToArray();

        Assert.Equal(ages, Enumerable.Range(0, 200).Select(_ => replay.DrawSegmentAge()));
        Assert.All(ages, static age => Assert.InRange(age, MaxAge * 0.9, MaxAge));
        Assert.True(ages.Distinct().Count() > 100);
    }

    /// <summary>
    /// ledgers.md §2.4, LG-I12: a created batch disposed unproposed, whole or after a refused row, abandons its writer,
    /// since no batch after its slot can then be decided; every later proposal is <see cref="ProposalOutcome.Fenced"/>.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ABatchDisposedUnproposedAbandonsTheWriter(bool refusedRow)
    {
        var token = TestContext.Current.CancellationToken;
        await using var primary = await StartAsync(LedgerWriterOptions.Default, token);
        var writer = primary.Primary.Writer;
        using (var batch = await writer.CreateBatchAsync(rowCount: 1, 64, token))
        {
            Assert.Equal(Slot.Zero, batch.Slot);
            if (refusedRow)
            {
                _ = Assert.Throws<ArgumentException>(() => batch.AddRow([1], RowKind.Append, default, default, new ReadOnlySequence<byte>(new byte[64])));
            }
        }

        await writer.Fenced.WaitAsync(TimeSpan.FromSeconds(30), TimeProvider.System, token);
        using var row = CreateRow([1]);
        Assert.Equal(Fenced, await writer.ProposeAsync([row], token));
    }

    /// <summary>
    /// ledgers.md §2.2, §2.4, LG-I13: <see cref="LedgerBatch.BeginRow"/> returns each row's version, computed from the
    /// version before it the defining row carries and the writer's dictionary, and the segment decoder resolves each
    /// decided row to that version and to its key's token, which only the defining row carries.
    /// </summary>
    [Fact]
    public async Task BeginRowReturnsTheVersionsTheSegmentDecoderResolves()
    {
        var token = TestContext.Current.CancellationToken;
        await using var primary = await StartAsync(LedgerWriterOptions.Default, token);
        var writer = primary.Primary.Writer;
        var tokenA = new RowToken([0xa1, 0xa2]);
        var versionBeforeA = new RowVersion(new RowToken([9]), 5);
        var written = new List<(byte Key, RowVersion Version, RowToken Token)>();
        using (var batch = await writer.CreateBatchAsync(rowCount: 2, 256, token))
        {
            written.Add((1, batch.AddRow([1], RowKind.Append, versionBeforeA, tokenA, Body), tokenA));
            written.Add((2, batch.AddRow([2], RowKind.Append, default, default, Body), default));
            Assert.Equal(Durable(0), await writer.ProposeAsync(batch, token));
        }

        using (var batch = await writer.CreateBatchAsync(rowCount: 3, 256, token))
        {
            written.Add((2, batch.AddRow([2], RowKind.Delete, written[1].Version, default, ReadOnlySequence<byte>.Empty), default));
            written.Add((1, batch.AddRow([1], RowKind.Append, written[0].Version, default, Body), tokenA));
            written.Add((2, batch.AddRow([2], RowKind.Append, written[2].Version, default, Body), default));
            Assert.Equal(Durable(1), await writer.ProposeAsync(batch, token));
        }

        Assert.Equal([new RowVersion(new RowToken([9]), 6), new RowVersion(default, 1), new RowVersion(default, 2), new RowVersion(new RowToken([9]), 7), new RowVersion(default, 3)], written.Select(static row => row.Version));
        var resolved = new List<(byte Key, RowVersion Version, RowToken Token)>();
        using var segments = new LedgerSegmentView(WriterTestData.Id);
        for (var slot = 0L; slot < 2; slot++)
        {
            var read = await primary.Primary.Ledger.ReadAsync(new Slot(slot), token);
            using var buffer = read.Batch;
            Resolve(segments, slot, buffer, resolved);
        }

        Assert.Equal(written, resolved);
    }

    /// <summary>
    /// ledgers.md §2.4: a creation which waits for the window or for the proposal budget ends when the lifetime stops: it
    /// creates a batch which takes no slot, whose proposal is <see cref="ProposalOutcome.Fenced"/> at once.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StoppingTheLifetimeEndsACreationWhichWaits(bool forBudget)
    {
        var token = TestContext.Current.CancellationToken;
        await using var inner = WriterTestData.CreateReplica(windowSize: forBudget ? Window : 1);
        var replica = new TailRecordingLedgerReplica(inner) { HoldWrites = true };
        var options = Options(maxProposalBytes: 256);
        var primary = await Primary.StartAsync(() => replica, token, windowSize: forBudget ? Window : 1, options: options);
        using var row = CreateRow([1]);
        Task<ProposalOutcome> first;
        try
        {
            first = primary.Writer.ProposeAsync([row], token).AsTask();
            await replica.WriteStartedAsync(0);
            var creating = primary.Writer.CreateBatchAsync(rowCount: 1, forBudget ? 256 : 64, token).AsTask();
            Assert.False(creating.IsCompleted);

            primary.Writer.Abandon();

            using var batch = await creating;
            Assert.Null(batch.Slot);
            _ = batch.AddRow([1], RowKind.Append, default, default, Body);
            Assert.Equal(Fenced, await primary.Writer.ProposeAsync(batch, token));
            replica.ReleaseWrite(0);
        }
        finally
        {
            // The abandoned writer's first proposal is decided only once the map records the ledger's end, or fails at
            // the writer's disposal while its view lists the ledger open.
            await primary.DisposeAsync();
        }

        _ = await Record.ExceptionAsync(() => first);
    }

    /// <summary>
    /// ledgers.md §2.4, §8.7: a creation whose maxLength would take the batches created and not yet decided past
    /// <see cref="LedgerWriterOptions.MaxProposalBytes"/> waits until a decision frees room, and a smaller creation behind
    /// it waits its turn; a maxLength above the budget is refused; the reserved bytes never pass the budget.
    /// </summary>
    [Fact]
    public async Task ACreationWhichWouldPassTheProposalBudgetWaitsAndTheCreationsBehindItWaitTheirTurn()
    {
        var token = TestContext.Current.CancellationToken;
        await using var inner = WriterTestData.CreateReplica(Window);
        var replica = new TailRecordingLedgerReplica(inner) { HoldWrites = true };
        var options = Options(maxProposalBytes: 400);
        await using var primary = await Primary.StartAsync(() => replica, token, options: options);
        var writer = primary.Writer;
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => { _ = writer.CreateBatchAsync(rowCount: 1, 401, token).AsTask(); });

        var (first, firstLength) = await ProposeHeldAsync(writer, maxLength: 300, token);
        await replica.WriteStartedAsync(0);
        var large = writer.CreateBatchAsync(rowCount: 1, 380, token).AsTask();
        var small = writer.CreateBatchAsync(rowCount: 1, 40, token).AsTask();
        Assert.Equal(2, writer.WaitingCreations);
        Assert.Equal(firstLength, writer.ReservedBytes);

        replica.ReleaseWrite(0);
        Assert.Equal(Durable(0), await first);
        using (var batch = await large)
        {
            Assert.Equal(new Slot(1), batch.Slot);
            Assert.Equal(1, writer.WaitingCreations);
            Assert.Equal(380, writer.ReservedBytes);
            _ = batch.AddRow([2], RowKind.Append, default, default, Body);
            var secondLength = batch.Length;
            var second = writer.ProposeAsync(batch, token).AsTask();
            using (var last = await small)
            {
                Assert.Equal(new Slot(2), last.Slot);
                Assert.Equal(secondLength + 40, writer.ReservedBytes);
                _ = last.AddRow([3], RowKind.Append, default, default, Body);
                var third = writer.ProposeAsync(last, token).AsTask();
                for (var slot = 1L; slot <= 2; slot++)
                {
                    await replica.WriteStartedAsync(slot);
                    replica.ReleaseWrite(slot);
                }

                Assert.Equal(Durable(2), await third);
            }

            Assert.Equal(Durable(1), await second);
        }

        Assert.Equal(0, writer.ReservedBytes);
    }

    /// <summary>
    /// ledgers.md §8.7: small batches within the proposal budget fill the window: four batches are written at once.
    /// </summary>
    [Fact]
    public async Task SmallBatchesWithinTheBudgetFillTheWindow()
    {
        var token = TestContext.Current.CancellationToken;
        await using var inner = WriterTestData.CreateReplica(Window);
        var replica = new TailRecordingLedgerReplica(inner) { HoldWrites = true };
        var options = Options(maxProposalBytes: 400);
        await using var primary = await Primary.StartAsync(() => replica, token, options: options);
        var proposals = new List<Task<ProposalOutcome>>();
        for (var index = 0; index < Window; index++)
        {
            proposals.Add((await ProposeHeldAsync(primary.Writer, maxLength: 100, token)).Outcome);
        }

        for (var slot = 0L; slot < Window; slot++)
        {
            await replica.WriteStartedAsync(slot);
        }

        for (var slot = 0L; slot < Window; slot++)
        {
            replica.ReleaseWrite(slot);
            Assert.Equal(Durable(slot), await proposals[(int)slot]);
        }
    }

    /// <summary>
    /// ledgers.md §2.4: a creation whose <c>maxLength</c> cannot hold its batch's longest header is refused with
    /// <see cref="ArgumentOutOfRangeException"/> before it is queued: it takes no slot, and the writer creates and proposes
    /// its next batch as before.
    /// </summary>
    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public async Task ACreationWhoseMaxLengthCannotHoldItsHeaderIsRefusedBeforeItTakesASlot(int shortBy)
    {
        var token = TestContext.Current.CancellationToken;
        await using var primary = await StartAsync(LedgerWriterOptions.Default, token);
        var writer = primary.Primary.Writer;
        var maxLength = shortBy < 0 ? -1 : LedgerBatch.MaxHeaderLength(1) - shortBy;
        if (shortBy == 0)
        {
            using var exact = await writer.CreateBatchAsync(rowCount: 1, maxLength, token);
            Assert.Equal(Slot.Zero, exact.Slot);
            _ = exact.AddRow([1], RowKind.Append, default, default, Body);
            Assert.Equal(Durable(0), await writer.ProposeAsync(exact, token));
            return;
        }

        _ = Assert.Throws<ArgumentOutOfRangeException>(() => { _ = writer.CreateBatchAsync(rowCount: 1, maxLength, token).AsTask(); });
        Assert.Equal(0, writer.WaitingCreations);
        Assert.Equal(0, writer.ReservedBytes);
        await ProposeRowsAsync(primary.Primary, [[1]], token, expected: 0);
    }

    /// <summary>
    /// ledgers.md §2.4: <c>StopAdmittingAsync</c> gives a creation waiting for the window, and every one after it, no slot,
    /// so its proposal is <see cref="ProposalOutcome.Fenced"/> at once; nothing is written past the proposals admitted
    /// before the call, and the stop completes.
    /// </summary>
    [Fact]
    public async Task StopAdmittingGivesWaitingCreationsNoSlot()
    {
        var token = TestContext.Current.CancellationToken;
        await using var inner = WriterTestData.CreateReplica(windowSize: 1);
        var replica = new TailRecordingLedgerReplica(inner) { HoldWrites = true };
        var time = new FakeTimeProvider();
        await using var primary = await Primary.StartAsync(() => replica, token, windowSize: 1, time: time);
        var writer = primary.Writer;
        var (first, _) = await ProposeHeldAsync(writer, maxLength: 100, token);
        await replica.WriteStartedAsync(0);
        var waiting = writer.CreateBatchAsync(rowCount: 1, 100, token).AsTask();
        Assert.False(waiting.IsCompleted);

        var stopping = writer.StopAdmittingAsync(token);
        using (var late = await writer.CreateBatchAsync(rowCount: 1, 100, token))
        {
            Assert.Null(late.Slot);
            Assert.Null(late.Segment);
        }

        time.Advance(LedgerWriter.StopAdmittingDrainLimit);
        await stopping;
        using (var batch = await waiting)
        {
            Assert.Null(batch.Slot);
            _ = batch.AddRow([2], RowKind.Append, default, default, Body);
            Assert.Equal(Fenced, await writer.ProposeAsync(batch, token));
        }

        replica.ReleaseWrite(0);
        _ = await Record.ExceptionAsync(() => first.WaitAsync(TimeSpan.FromMilliseconds(100), TimeProvider.System, token));
        Assert.Equal(1, replica.Operations.Count(static operation => operation.Kind == TailRecordingLedgerReplica.Write));
    }

    /// <summary>
    /// ledgers.md §2.4, LG-I5: a batch which took its slot before <c>StopAdmittingAsync</c> and is proposed after it is
    /// never written, and its proposal completes <see cref="ProposalOutcome.Fenced"/> only once the proposals admitted
    /// before it have ended their appends, so proposals complete in slot order.
    /// </summary>
    [Fact]
    public async Task AGrantedBatchProposedAfterStopAdmittingIsFencedInSlotOrder()
    {
        var token = TestContext.Current.CancellationToken;
        await using var inner = WriterTestData.CreateReplica(windowSize: 2);
        var replica = new TailRecordingLedgerReplica(inner) { HoldWrites = true };
        var time = new FakeTimeProvider();
        await using var primary = await Primary.StartAsync(() => replica, token, windowSize: 2, time: time);
        var writer = primary.Writer;
        var (first, _) = await ProposeHeldAsync(writer, maxLength: 100, token);
        await replica.WriteStartedAsync(0);
        using var granted = await writer.CreateBatchAsync(rowCount: 1, 100, token);
        Assert.Equal(new Slot(1), granted.Slot);
        _ = granted.AddRow([2], RowKind.Append, default, default, Body);

        var stopping = writer.StopAdmittingAsync(token);
        var second = writer.ProposeAsync(granted, token).AsTask();
        time.Advance(LedgerWriter.StopAdmittingDrainLimit);
        await stopping;
        Assert.False(second.IsCompleted);

        // The first proposal's append ends once its write does; its outcome waits for the map, which no executor closes.
        replica.ReleaseWrite(0);
        Assert.Equal(Fenced, await second.WaitAsync(TimeSpan.FromSeconds(30), TimeProvider.System, token));
        Assert.False(first.IsCompletedSuccessfully);
        Assert.Equal(1, replica.Operations.Count(static operation => operation.Kind == TailRecordingLedgerReplica.Write));
    }

    /// <summary>
    /// ledgers.md §8.2: a segment whose age passes while a batch is created and not yet proposed, and while a creation
    /// waits behind it and is then withdrawn, still ends: once the batch is proposed, which leaves the writer idle, the
    /// writer proposes the marker the segment owes, at the next slot, and only one.
    /// </summary>
    [Fact]
    public async Task ASegmentWhoseAgePassesWhileABatchIsBuiltEndsOnceTheWriterIsIdle()
    {
        var token = TestContext.Current.CancellationToken;
        await using var primary = await StartAsync(Options(maxSegmentAge: MaxAge), token, new Random(3));
        var writer = primary.Primary.Writer;
        await ProposeRowsAsync(primary.Primary, [[1]], token, expected: 0);
        using (var building = await writer.CreateBatchAsync(rowCount: 1, 1024, token))
        {
            using var withdrawn = new CancellationTokenSource();
            var waiting = writer.CreateBatchAsync(rowCount: 1, 1024, withdrawn.Token).AsTask();
            primary.Time.Advance(writer.DrawnSegmentAge);
            await withdrawn.CancelAsync();
            _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
            Assert.Equal(0, writer.WaitingCreations);

            _ = building.AddRow([2], RowKind.Append, building.HeldVersion([2]), default, Body);
            Assert.Equal(Durable(1), await writer.ProposeAsync(building, token));
        }

        await WriterTestData.WaitUntilAsync(() => primary.Primary.Ledger.KnownContiguousTail == new Slot(3));
        await ProposeRowsAsync(primary.Primary, [[1]], token, expected: 3);
        var segments = await ReadDecidedAsync(primary.Primary, 4, token);
        Assert.Equal([true, false, true, false], segments.Select(static batch => batch.IsStart));
        Assert.Equal([1, 1, 0, 1], segments.Select(static batch => batch.Rows));
    }

    /// <summary>
    /// ledgers.md §8.2, property: random scripts of proposals of one to three keys, markers, and the clock's advance, over
    /// a writer of four keys a segment whose age timer may also fire late, form the segments an independent reading of
    /// §8.2 gives: a batch starts a segment at slot 0, as a marker, when its rows could pass the key bound, or once the
    /// segment's first batch with rows is the writer's age old; and when the age passes with the writer idle, the writer
    /// proposes a marker, once.
    /// </summary>
    [Fact]
    public Task WriterScriptsFormSegmentsAsTheEndRulesGive() =>
        Gen.Select(Gen.Int[0, 3].Select(static lag => lag * 30), Gen.Select(Gen.Int[0, 5], Gen.Int[0, 31], Gen.Int[0, 150]).Array[1, 14])
            .SampleAsync((lag, script) => _leaks.InScopeAsync(() => CheckScriptAsync(TimeSpan.FromSeconds(lag), script)), iter: 60, threads: 1);

    /// <summary>
    /// Runs one script, each step <c>(kind, keys, percent)</c>: kinds 0 to 3 propose the keys whose bits <c>keys</c> sets
    /// (at least one, at most three), 4 ends the segment, and 5 advances the clock by <c>percent</c> of the writer's age.
    /// </summary>
    private static async Task CheckScriptAsync(TimeSpan lag, (int Kind, int Keys, int Percent)[] script)
    {
        var token = TestContext.Current.CancellationToken;
        var options = Options(maxSegmentKeys: 4, maxSegmentAge: MaxAge);
        var inner = WriterTestData.CreateReplica(Window);
        var time = new LateTimerTimeProvider(lag);
        await using var primary = new Started(await Primary.StartAsync(() => inner, token, options: options, random: new Random(5), time: time), inner, time);
        var writer = primary.Primary.Writer;
        var age = writer.DrawnSegmentAge;
        var expected = new List<(bool Start, int Rows)>();
        var defined = new HashSet<int>();
        TimeSpan? firstRows = null;
        var now = TimeSpan.Zero;

        void Start()
        {
            defined.Clear();
            firstRows = null;
        }

        foreach (var (kind, keyBits, percent) in script)
        {
            if (kind <= 3)
            {
                byte[][] keys = [.. Enumerable.Range(0, 5).Where(bit => (keyBits >> bit & 1) != 0).Take(3).Select(static bit => new[] { (byte)(bit + 1) })];
                if (keys.Length == 0)
                {
                    keys = [[1]];
                }

                var starts = expected.Count == 0
                    || defined.Count > 0 && defined.Count + keys.Length > options.MaxSegmentKeys
                    || firstRows is { } since && now - since >= age;
                if (starts)
                {
                    Start();
                }

                defined.UnionWith(keys.Select(static key => (int)key[0]));
                firstRows ??= now;
                expected.Add((starts, keys.Length));
                await ProposeRowsAsync(primary.Primary, keys, token, expected: expected.Count - 1);
            }
            else if (kind == 4)
            {
                Start();
                expected.Add((true, 0));
                Assert.Equal(Durable(expected.Count - 1), await writer.EndSegmentAsync(token));
            }
            else
            {
                var step = age * percent / 100;
                now += step;
                time.Advance(step);
                if (firstRows is { } since && now >= since + age + lag)
                {
                    // The timer fired with the writer idle: the marker the aged segment owes.
                    Start();
                    expected.Add((true, 0));
                }

                await WriterTestData.WaitUntilAsync(() => primary.Primary.Ledger.KnownContiguousTail == new Slot(expected.Count));
            }
        }

        var segments = await ReadDecidedAsync(primary.Primary, expected.Count, token);
        Assert.Equal(expected, segments.Select(static batch => (batch.IsStart, batch.Rows)));
    }

    /// <summary>
    /// ledgers.md §8.2, LG-I12: a seal during a batch's build, by another process's recovery, leaves the batch unwritten:
    /// its proposal is decided from the map's <c>T</c>, <see cref="ProposalOutcome.Fenced"/> since <c>T</c> lies at its slot,
    /// a batch created after the stop takes no slot, nothing lies at or above <c>T</c>, and every slot below <c>T</c>
    /// resolves from every rewind.
    /// </summary>
    [Fact]
    public async Task ASealDuringABatchsBuildDecidesItFromTAndLeavesTheSegmentResolvable()
    {
        var token = TestContext.Current.CancellationToken;
        await using var inner = WriterTestData.CreateReplica(Window);
        var replica = new TailRecordingLedgerReplica(inner);
        await using var primary = await Primary.StartAsync(() => replica, token);
        await ProposeRowsAsync(primary, [[1]], token, expected: 0);
        await ProposeRowsAsync(primary, [[1], [2]], token, expected: 1);
        using var building = await primary.Writer.CreateBatchAsync(rowCount: 1, 1024, token);
        Assert.Equal(new Slot(2), building.Slot);

        var end = await LedgerWriterProposalTests.RecoverElsewhereAsync(inner, token);
        Assert.Equal(new Slot(2), end);
        await primary.PublishAsync(epoch: 2, new LedgerStatus.Closed(end), token);
        _ = building.AddRow([2], RowKind.Append, building.HeldVersion([2]), default, Body);
        Assert.Equal(Fenced, await primary.Writer.ProposeAsync(building, token));
        using (var after = await primary.Writer.CreateBatchAsync(rowCount: 1, 1024, token))
        {
            Assert.Null(after.Slot);
        }

        var stored = await inner.ReadAsync(end, Slot.Zero, token);
        stored.Release();
        Assert.Equal(ReplicaReadCase.Missing, stored.Case);
        for (var from = 0L; from < end.Value; from++)
        {
            using var segments = new LedgerSegmentView(WriterTestData.Id);
            var start = from;
            while (start > 0)
            {
                var probe = await primary.Ledger.ReadAsync(new Slot(start), token);
                using var bytes = probe.Batch;
                Assert.Null(BatchView.Decode(bytes, out var view));
                if (view.IsSegmentStart)
                {
                    break;
                }

                start -= (long)view.LedgerSegmentOffset;
            }

            for (var slot = start; slot < end.Value; slot++)
            {
                var read = await primary.Ledger.ReadAsync(new Slot(slot), token);
                using var buffer = read.Batch;
                _ = Advance(segments, slot, buffer);
            }
        }
    }

    private static ReadOnlySequence<byte> Body => new("body"u8.ToArray());

    private static LedgerWriterOptions Options(long? maxSegmentBytes = null, int? maxSegmentKeys = null, TimeSpan? maxSegmentAge = null, long? maxProposalBytes = null)
    {
        var defaults = LedgerWriterOptions.Default;
        return new(
            maxSegmentBytes ?? defaults.MaxSegmentBytes,
            maxSegmentKeys ?? defaults.MaxSegmentKeys,
            maxSegmentAge ?? defaults.MaxSegmentAge,
            maxProposalBytes ?? defaults.MaxProposalBytes);
    }

    private static ProposalOutcome Fenced => new(default(ProposalOutcome.Fenced));

    private static ProposalOutcome Durable(long slot) => new(new ProposalOutcome.Durable(new Slot(slot)));

    private static Row CreateRow(byte[] key)
    {
        using var buffer = WriterTestData.CreatePayload("body"u8);
        return new Row(key, RowKind.Append, buffer);
    }

    private static async Task<Started> StartAsync(LedgerWriterOptions options, CancellationToken token, Random? random = null, Action<LedgerSegmentKeys>? segmentEnded = null)
    {
        var inner = WriterTestData.CreateReplica(Window);
        var time = new FakeTimeProvider();
        var primary = await Primary.StartAsync(() => inner, token, options: options, random: random, time: time, segmentEnded: segmentEnded);
        return new Started(primary, inner, time);
    }

    /// <summary>
    /// Creates a one-row batch of at most <paramref name="maxLength"/> bytes and proposes it, not waiting for its outcome;
    /// returns the outcome and the batch's length.
    /// </summary>
    private static async Task<(Task<ProposalOutcome> Outcome, int Length)> ProposeHeldAsync(LedgerWriter writer, int maxLength, CancellationToken token)
    {
        var batch = await writer.CreateBatchAsync(rowCount: 1, maxLength, token);
        _ = batch.AddRow([1], RowKind.Append, batch.HeldVersion([1]), default, Body);
        return (ProposeAndDisposeAsync(writer, batch, token), batch.Length);

        static async Task<ProposalOutcome> ProposeAndDisposeAsync(LedgerWriter writer, LedgerBatch batch, CancellationToken token)
        {
            using (batch)
            {
                return await writer.ProposeAsync(batch, token);
            }
        }
    }

    private static async Task ProposeKeysAsync(Primary primary, CancellationToken token, params byte[][] keys)
    {
        foreach (var key in keys)
        {
            await ProposeRowsAsync(primary, [key], token);
        }
    }

    private static async Task ProposeRowsAsync(Primary primary, byte[][] keys, CancellationToken token, long? expected = null)
    {
        var rows = keys.Select(CreateRow).ToArray();
        try
        {
            var outcome = await primary.Writer.ProposeAsync(rows, token);
            Assert.True(outcome.Value is ProposalOutcome.Durable, $"The proposal was {outcome}.");
            if (expected is { } slot)
            {
                Assert.Equal(Durable(slot), outcome);
            }
        }
        finally
        {
            foreach (var row in rows)
            {
                row.Dispose();
            }
        }
    }

    /// <summary>Reads the decided slots below <paramref name="count"/> and advances one segment decoder over them, which accepts each.</summary>
    private static async Task<List<Decided>> ReadDecidedAsync(Primary primary, long count, CancellationToken token)
    {
        var decided = new List<Decided>();
        using var segments = new LedgerSegmentView(WriterTestData.Id);
        for (var slot = 0L; slot < count; slot++)
        {
            var read = await primary.Ledger.ReadAsync(new Slot(slot), token);
            using var buffer = read.Batch;
            decided.Add(Advance(segments, slot, buffer));
        }

        return decided;
    }

    private static Decided Advance(LedgerSegmentView segments, long slot, ArcBuffer buffer)
    {
        Assert.Null(BatchView.Decode(buffer, out var view));
        var definedBefore = segments.DefinedKeys;
        Assert.Null(segments.Advance(new Slot(slot), view));
        var defines = false;
        foreach (var row in view)
        {
            defines |= row.IsDefining;
        }

        return new Decided(slot, view.IsSegmentStart, view.LedgerSegmentOffset, view.OrdinalBase, view.Count, view.Length, definedBefore, defines);
    }

    private static void Resolve(LedgerSegmentView segments, long slot, ArcBuffer buffer, List<(byte Key, RowVersion Version, RowToken Token)> resolved)
    {
        Assert.Null(BatchView.Decode(buffer, out var view));
        Assert.Null(segments.Advance(new Slot(slot), view));
        foreach (var row in segments.Rows)
        {
            resolved.Add((row.Key[0], row.Version, row.Token));
        }
    }

    /// <summary>
    /// ledgers.md §8.2's segment ends, as the decided batches show them: each batch after slot 0 starts a segment exactly
    /// when it is a marker, or the open segment holds a batch with rows and its batches total at least
    /// <see cref="LedgerWriterOptions.MaxSegmentBytes"/>, or the batch's rows could take the keys its segment defined past
    /// <see cref="LedgerWriterOptions.MaxSegmentKeys"/>; markers aside, an aged segment's end is a marker too.
    /// </summary>
    private static void Judge(IReadOnlyList<Decided> decided, LedgerWriterOptions options)
    {
        long bytes = 0;
        var hasRows = false;
        foreach (var batch in decided)
        {
            var starts = batch.Slot == 0
                || batch.Rows == 0
                || hasRows && bytes >= options.MaxSegmentBytes
                || batch.DefinedBefore > 0 && batch.DefinedBefore + (ulong)batch.Rows > (ulong)options.MaxSegmentKeys;
            Assert.True(starts == batch.IsStart, $"The batch at slot {batch.Slot} {(batch.IsStart ? "starts" : "continues")} its segment.");
            if (batch.IsStart)
            {
                bytes = 0;
                hasRows = false;
            }

            bytes += batch.Length;
            hasRows |= batch.Rows > 0;
        }
    }

    /// <summary>One decided batch's header, as the segment decoder accepted it.</summary>
    private readonly record struct Decided(long Slot, bool IsStart, ulong Offset, ulong OrdinalBase, int Rows, int Length, ulong DefinedBefore, bool Defines);

    /// <summary>A fake clock whose timers fire <paramref name="lag"/> after they are due, as a busy timer queue's may.</summary>
    private sealed class LateTimerTimeProvider(TimeSpan lag) : FakeTimeProvider
    {
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            base.CreateTimer(callback, state, dueTime == Timeout.InfiniteTimeSpan ? dueTime : dueTime + lag, period);
    }

    /// <summary>A primary over one memory replica, on a fake clock, which disposes the replica after the primary.</summary>
    private sealed class Started(Primary primary, InMemoryLedgerReplica replica, FakeTimeProvider time) : IAsyncDisposable
    {
        public Primary Primary { get; } = primary;

        public FakeTimeProvider Time { get; } = time;

        public async ValueTask DisposeAsync()
        {
            await Primary.DisposeAsync();
            await replica.DisposeAsync();
        }
    }
}
