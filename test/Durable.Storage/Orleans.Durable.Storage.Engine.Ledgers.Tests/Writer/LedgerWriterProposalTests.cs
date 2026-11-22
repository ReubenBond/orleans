using System.Buffers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Orleans.Durable.Storage.Fuzz.Ledgers;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Ledgers.Tests.Writer;

#pragma warning disable CA2000 // Each replica double passes to a Ledger, which owns and disposes it.

/// <summary>
/// The primary's proposals and open check (ledgers.md §2.4, §8.2, §8.3), over a writer created through the reader's
/// lease of its ledger: proposals encoded within the ledger's range and the replica's payload limit, decided from the
/// ledger map when they meet a seal or cannot complete, and coalesced open checks.
/// </summary>
public sealed class LedgerWriterProposalTests : IDisposable
{
    private const int Window = 4;
    private static readonly NodeId Node = new(Guid.Parse("00000000-0000-0000-0000-0000000000a1"));

    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    public void Dispose() => _leaks.Dispose();

    /// <summary>
    /// ledgers.md §2.4, §7: the writer's batch checks each row as it begins against the replica's payload limit, so a row
    /// which would take its batch a byte over the limit is refused, writing nothing, before any replica sees a write; the
    /// batch then holds a row which reaches exactly the limit, takes slot 0, and its written batch is the limit long.
    /// </summary>
    [Fact]
    public async Task ARowAByteOverThePayloadLimitIsRefusedAndABatchAtTheLimitIsProposed()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        // The body is long enough that the batch at the limit holds the longest header a batch of one row may need.
        var body = new byte[40];
        var limit = LedgerSegmentRules.Encode(new RuleBatch(IsStart: true, 0, 0, [new RuleRow(0, IsDefining: true, 0, [0x10], [], 0, [], body)])).Length;
        await using var inner = WriterTestData.CreateReplica(Window);
        var written = new List<int>();
        var replica = new DelegatingLedgerReplica
        {
            Write = (slot, payload, tail, recovery, token) =>
            {
                lock (written)
                {
                    written.Add(payload.Length);
                }

                return inner.WriteAsync(slot, payload, tail, recovery, token);
            },
        };
        await using var primary = await Primary.StartAsync(() => replica, cancellationToken, maxBatchLength: limit);
        using var batch = await primary.Writer.CreateBatchAsync(rowCount: 1, limit, cancellationToken);

        var refused = Assert.Throws<ArgumentException>(() => batch.AddRow([0x10], RowKind.Append, default, default, new ReadOnlySequence<byte>(new byte[body.Length + 1])));

        Assert.Contains("holds", refused.Message, StringComparison.Ordinal);
        Assert.Equal(0, batch.RowsBegun);
        Assert.Empty(written);
        _ = batch.AddRow([0x10], RowKind.Append, default, default, new ReadOnlySequence<byte>(body));
        Assert.Equal(Durable(0), await primary.Writer.ProposeAsync(batch, cancellationToken));
        Assert.Equal([limit], written);
    }

    /// <summary>
    /// ledgers.md §2.4: a batch with an unwritten declared row is refused with <see cref="ArgumentException"/> and keeps
    /// its bytes, so its caller completes and proposes it.
    /// </summary>
    [Fact]
    public async Task AnIncompleteBatchIsRefusedAndKeepsItsBytes()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var inner = WriterTestData.CreateReplica(Window);
        var replica = new TailRecordingLedgerReplica(inner);
        await using var primary = await Primary.StartAsync(() => replica, cancellationToken);
        using var batch = await primary.Writer.CreateBatchAsync(rowCount: 2, 256, cancellationToken);
        _ = batch.AddRow([0x10], RowKind.Append, default, default, new ReadOnlySequence<byte>("one"u8.ToArray()));

        _ = Assert.Throws<ArgumentException>(() => { _ = primary.Writer.ProposeAsync(batch, cancellationToken).AsTask(); });

        Assert.Empty(replica.Operations);
        _ = batch.AddRow([0x11], RowKind.Append, default, default, new ReadOnlySequence<byte>("two"u8.ToArray()));
        Assert.Equal(Durable(0), await primary.Writer.ProposeAsync(batch, cancellationToken));
    }

    /// <summary>ledgers.md §2.4: a writer refuses a batch another writer created, which was checked against another range and limit.</summary>
    [Fact]
    public async Task AWriterRefusesABatchAnotherWriterCreated()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var inner = WriterTestData.CreateReplica(Window);
        var replica = new TailRecordingLedgerReplica(inner);
        await using var primary = await Primary.StartAsync(() => replica, cancellationToken);
        await using var otherInner = WriterTestData.CreateReplica(Window);
        await using var other = await Primary.StartAsync(() => otherInner, cancellationToken);
        using var foreign = await other.Writer.CreateBatchAsync(rowCount: 1, 256, cancellationToken);
        _ = foreign.AddRow([0x10], RowKind.Append, default, default, new ReadOnlySequence<byte>("foreign"u8.ToArray()));

        _ = Assert.Throws<ArgumentException>(() => { _ = primary.Writer.ProposeAsync(foreign, cancellationToken).AsTask(); });

        Assert.Empty(replica.Operations);
    }

    /// <summary>
    /// ledgers.md §2.4: a call with a complete batch of this writer's takes the batch's bytes and appends them at the
    /// batch's slot whatever follows: a cancelled caller stops waiting, the append is decided as usual, and the batch is
    /// spent.
    /// </summary>
    [Fact]
    public async Task ACancelledProposalStillAppendsItsBatch()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var inner = WriterTestData.CreateReplica(Window);
        var replica = new TailRecordingLedgerReplica(inner) { HoldWrites = true };
        await using var primary = await Primary.StartAsync(() => replica, cancellationToken);
        using var batch = await primary.Writer.CreateBatchAsync(rowCount: 1, 256, cancellationToken);
        _ = batch.AddRow([0x10], RowKind.Append, default, default, new ReadOnlySequence<byte>("row"u8.ToArray()));
        using var precancelled = new CancellationTokenSource();
        await precancelled.CancelAsync();

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await primary.Writer.ProposeAsync(batch, precancelled.Token));

        await replica.WriteStartedAsync(0);
        replica.ReleaseWrite(0);
        await WriterTestData.WaitUntilAsync(() => primary.Ledger.KnownContiguousTail == new Slot(1));
        Assert.Contains(replica.Operations, static operation => operation.Kind == TailRecordingLedgerReplica.Write);
        _ = Assert.Throws<InvalidOperationException>(() => { _ = primary.Writer.ProposeAsync(batch, cancellationToken).AsTask(); });
        _ = Assert.Throws<InvalidOperationException>(() => batch.WriteBody([]));
    }
    /// <summary>ledgers.md §2.4: a writer takes a batch's bytes once, so a second proposal of the batch throws.</summary>
    [Fact]
    public async Task AProposedBatchCannotBeProposedAgain()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var inner = WriterTestData.CreateReplica(Window);
        await using var primary = await Primary.StartAsync(() => inner, cancellationToken);
        using var batch = await primary.Writer.CreateBatchAsync(rowCount: 1, 256, cancellationToken);
        _ = batch.AddRow([0x10], RowKind.Append, default, default, new ReadOnlySequence<byte>("row"u8.ToArray()));

        Assert.Equal(Durable(0), await primary.Writer.ProposeAsync(batch, cancellationToken));

        _ = Assert.Throws<InvalidOperationException>(() => { _ = primary.Writer.ProposeAsync(batch, cancellationToken).AsTask(); });
    }

    /// <summary>ledgers.md §2.4: only a writer created through the reader's lease of its ledger proposes, so only it creates a batch.</summary>
    [Fact]
    public async Task AnAppendOnlyWriterCreatesNoBatch()
    {
        await using var appendOnly = WriterTestData.CreateLog(WriterTestData.CreateReplica());

        _ = Assert.Throws<InvalidOperationException>(() => { _ = appendOnly.Writer.CreateBatchAsync(1, 64, TestContext.Current.CancellationToken).AsTask(); });
    }

    /// <summary>
    /// ledgers.md §2.4: a writer its user abandons after a fault of its own is <see cref="LedgerWriter.Fenced"/>, and every
    /// later proposal is <see cref="ProposalOutcome.Fenced"/> without a slot; abandoning again changes nothing.
    /// </summary>
    [Fact]
    public async Task AnAbandonedWriterIsFencedAndProposesNothingMore()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var inner = WriterTestData.CreateReplica(Window);
        var replica = new TailRecordingLedgerReplica(inner);
        await using var primary = await Primary.StartAsync(() => replica, cancellationToken);
        using var row = CreateRow([0x10], "row"u8);
        Assert.Equal(Durable(0), await primary.Writer.ProposeAsync([row], cancellationToken));

        primary.Writer.Abandon();
        primary.Writer.Abandon();

        await primary.Writer.Fenced.WaitAsync(TimeSpan.FromSeconds(30), TimeProvider.System, cancellationToken);
        Assert.Equal(new ProposalOutcome(default(ProposalOutcome.Fenced)), await primary.Writer.ProposeAsync([row], cancellationToken));
    }

    /// <summary>
    /// orleans.md §5.6: a writer's proposed bytes, its ledger's load, count each batch proposed at a slot, a marker
    /// included, by its length; a proposal which took no slot counts nothing.
    /// </summary>
    [Fact]
    public async Task ProposedBytesCountEveryBatchProposedAtASlot()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var inner = WriterTestData.CreateReplica(Window);
        await using var primary = await Primary.StartAsync(() => inner, cancellationToken);
        Assert.Equal(0, primary.Writer.ProposedBytes);

        long expected = 0;
        byte[][] bodies = ["one"u8.ToArray(), new byte[100]];
        for (var slot = 0; slot < bodies.Length; slot++)
        {
            using var batch = await primary.Writer.CreateBatchAsync(rowCount: 1, 256, cancellationToken);
            byte[] key = [0x10];
            _ = batch.AddRow(key, RowKind.Append, batch.HeldVersion(key), default, new ReadOnlySequence<byte>(bodies[slot]));
            expected += batch.Length;
            Assert.Equal(Durable(slot), await primary.Writer.ProposeAsync(batch, cancellationToken));
            Assert.Equal(expected, primary.Writer.ProposedBytes);
        }

        Assert.Equal(Durable(2), await primary.Writer.EndSegmentAsync(cancellationToken));
        var marker = await primary.Ledger.ReadAsync(new Slot(2), cancellationToken);
        expected += marker.Batch.Length;
        marker.Batch.Dispose();
        Assert.Equal(expected, primary.Writer.ProposedBytes);

        primary.Writer.Abandon();
        using var row = CreateRow([0x10], "row"u8);
        Assert.Equal(Fenced, await primary.Writer.ProposeAsync([row], cancellationToken));
        Assert.Equal(expected, primary.Writer.ProposedBytes);
    }

    /// <summary>
    /// ledgers.md §2.2, §2.4: a row whose key lies outside the ledger's range is refused with
    /// <see cref="ArgumentException"/>, writing nothing, and nothing is appended until the batch, completed with rows in
    /// the range, is proposed.
    /// </summary>
    [Fact]
    public async Task ARowOutsideTheRangeIsRefusedWithoutAppending()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var inner = WriterTestData.CreateReplica(Window);
        var replica = new TailRecordingLedgerReplica(inner);
        await using var primary = await Primary.StartAsync(() => replica, cancellationToken, range: KeyRange.Containing([0x00], 1));
        using var batch = await primary.Writer.CreateBatchAsync(rowCount: 2, 256, cancellationToken);
        _ = batch.AddRow([0x10], RowKind.Append, default, default, new ReadOnlySequence<byte>("in"u8.ToArray()));

        var refused = Assert.Throws<ArgumentException>(() => batch.AddRow([0x80], RowKind.Append, default, default, new ReadOnlySequence<byte>("out"u8.ToArray())));
        Assert.Contains("range", refused.Message, StringComparison.Ordinal);

        Assert.Empty(replica.Operations);
        _ = batch.AddRow([0x11], RowKind.Append, default, default, new ReadOnlySequence<byte>("in"u8.ToArray()));
        Assert.Equal(Durable(0), await primary.Writer.ProposeAsync(batch, cancellationToken));
    }
    /// <summary>
    /// ledgers.md §8.2: a proposal whose write met a seal is decided from the map's recorded <c>T</c>. Here its batch
    /// landed before the replica answered sealed, so the recovery ends above it and the proposal is
    /// <see cref="ProposalOutcome.Durable"/>; a proposal after the lifetime stopped took no slot and is
    /// <see cref="ProposalOutcome.Fenced"/> at once, and the writer's <see cref="Ledger"/> learns <c>T</c>.
    /// </summary>
    [Fact]
    public async Task AProposalWhoseWriteMetASealIsDurableBelowTheRecordedTail()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var inner = WriterTestData.CreateReplica(Window);
        var replica = new DelegatingLedgerReplica
        {
            Write = async (slot, payload, tail, recovery, token) =>
            {
                var written = await inner.WriteAsync(slot, payload, tail, recovery, token);
                if (slot.Value < 2)
                {
                    return written;
                }

                _ = await inner.ReadTailAsync(tail, seal: true, token);
                return ReplicaWriteOutcome.Sealed.Instance;
            },
            Read = inner.ReadAsync,
            ReadTail = inner.ReadTailAsync,
        };
        await using var primary = await Primary.StartAsync(() => replica, cancellationToken);
        using var row = CreateRow([0x10], "row"u8);

        Assert.Equal(Durable(0), await primary.Writer.ProposeAsync([row], cancellationToken));
        Assert.Equal(Durable(1), await primary.Writer.ProposeAsync([row], cancellationToken));
        var sealedProposal = primary.Writer.ProposeAsync([row], cancellationToken).AsTask();
        await WriterTestData.WaitUntilAsync(() => primary.Writer.Sequencer.IsStopped);
        Assert.Equal(Fenced, await primary.Writer.ProposeAsync([row], cancellationToken));
        Assert.False(sealedProposal.IsCompleted);

        var end = await RecoverElsewhereAsync(inner, cancellationToken);
        Assert.Equal(new Slot(3), end);
        await primary.PublishAsync(epoch: 2, new LedgerStatus.Closed(end), cancellationToken);

        Assert.Equal(Durable(2), await sealedProposal);
        Assert.Equal(new Slot(3), primary.Ledger.KnownContiguousTail);
    }

    /// <summary>
    /// ledgers.md §8.2, §8.4: a primary whose map view missed the close re-reads the map when its write meets a seal, and
    /// its proposal at the recorded <c>T</c> is <see cref="ProposalOutcome.Fenced"/> after exactly one read of the map.
    /// </summary>
    [Fact]
    public async Task APrimaryWhoseViewMissedTheCloseReReadsTheMapAndIsFenced()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var inner = WriterTestData.CreateReplica(Window);
        await using var primary = await Primary.StartAsync(() => new TailRecordingLedgerReplica(inner), cancellationToken);
        using var row = CreateRow([0x10], "row"u8);
        Assert.Equal(Durable(0), await primary.Writer.ProposeAsync([row], cancellationToken));

        var end = await RecoverElsewhereAsync(inner, cancellationToken);
        primary.Map.Write(primary.Value(epoch: 2, new LedgerStatus.Closed(end)));
        Assert.True(primary.Map.View.Current.Get(WriterTestData.Id)!.Value.IsOpen);
        var reads = primary.Map.Reads;

        Assert.Equal(Fenced, await primary.Writer.ProposeAsync([row], cancellationToken));
        Assert.False(primary.Map.View.Current.Get(WriterTestData.Id)!.Value.IsOpen);
        Assert.Equal(reads + 1, primary.Map.Reads);
    }

    /// <summary>
    /// ledgers.md §8.3: a proposal which met a seal, whose ledger leaves the map before the primary's view learns its
    /// <c>T</c>, is <see cref="ProposalOutcome.Ambiguous"/>.
    /// </summary>
    [Fact]
    public async Task AProposalWhoseLedgerLeftTheMapBeforeItsViewLearnedTIsAmbiguous()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var inner = WriterTestData.CreateReplica(Window);
        await using var primary = await Primary.StartAsync(() => new TailRecordingLedgerReplica(inner), cancellationToken);
        _ = await inner.ReadTailAsync(Slot.Zero, seal: true, cancellationToken);
        using var row = CreateRow([0x10], "row"u8);

        var proposal = primary.Writer.ProposeAsync([row], cancellationToken).AsTask();
        await WriterTestData.WaitUntilAsync(() => primary.Writer.Sequencer.IsStopped);
        Assert.False(proposal.IsCompleted);
        await primary.Map.PublishAsync(new LedgerMapValue(3, new LedgerId(WriterTestData.Id.Value + 1), 0, []), cancellationToken);

        Assert.Equal(new ProposalOutcome(default(ProposalOutcome.Ambiguous)), await proposal);
    }

    /// <summary>
    /// ledgers.md §2.4: <see cref="LedgerWriter.Fenced"/> completes once the lifetime stops for any reason but the
    /// writer's disposal, since the writer then makes no later proposal durable, and not on a requested stop alone.
    /// </summary>
    [Theory]
    [InlineData(StopCause.Sealing, true)]
    [InlineData(StopCause.PayloadWriteFailed, true)]
    [InlineData(StopCause.CompletionFailed, true)]
    [InlineData(StopCause.Requested, false)]
    public async Task TheWriterIsFencedByEveryStopButARequestedOne(StopCause cause, bool fenced)
    {
        var reason = cause switch
        {
            StopCause.Sealing => SequencerStopReason.Sealing,
            StopCause.PayloadWriteFailed => SequencerStopReason.PayloadWriteFailed,
            StopCause.CompletionFailed => SequencerStopReason.CompletionFailed,
            StopCause.Requested => SequencerStopReason.Requested,
            _ => throw new ArgumentOutOfRangeException(nameof(cause)),
        };
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var inner = WriterTestData.CreateReplica(Window);
        await using var primary = await Primary.StartAsync(() => new TailRecordingLedgerReplica(inner), cancellationToken);
        Assert.False(primary.Writer.Fenced.IsCompleted);

        primary.Writer.Sequencer.Stop(reason);

        // The stop completes Fenced synchronously, within the stop.
        Assert.Equal(fenced, primary.Writer.Fenced.IsCompleted);
        using var row = CreateRow([0x10], "row"u8);
        Assert.Equal(Fenced, await primary.Writer.ProposeAsync([row], cancellationToken));
    }

    /// <summary>
    /// ledgers.md §2.4: a batch whose slot was granted once the writer's disposal began keeps the slot, and its proposal,
    /// a call after the disposal, throws <see cref="ObjectDisposedException"/> and writes nothing: the slot holds no
    /// batch, so a recovery ends the ledger at or below it.
    /// </summary>
    [Fact]
    public async Task ABatchGrantedASlotDuringDisposalIsNeverWritten()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var inner = WriterTestData.CreateReplica(Window);
        var replica = new TailRecordingLedgerReplica(inner);
        await using var primary = await Primary.StartAsync(() => replica, cancellationToken);
        using var disposing = new ManualResetEventSlim();
        var acquired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observer = primary.Writer.Sequencer.Observer;
        primary.Writer.Sequencer.Observer = step =>
        {
            observer?.Invoke(step);
            if (step.Kind == SequencerStepKind.Acquired)
            {
                acquired.SetResult();
                disposing.Wait(cancellationToken);
            }
        };
        using var row = CreateRow([0x10], "row"u8);

        var proposal = Task.Run(async () => await primary.Writer.ProposeAsync([row], CancellationToken.None), cancellationToken);
        await acquired.Task.WaitAsync(cancellationToken);
        var disposal = primary.Writer.DisposeAsync().AsTask();
        disposing.Set();

        _ = await Assert.ThrowsAsync<ObjectDisposedException>(() => proposal.WaitAsync(cancellationToken));
        await disposal.WaitAsync(cancellationToken);
        Assert.DoesNotContain(replica.Operations, operation => operation.Kind == TailRecordingLedgerReplica.Write);
    }
    /// <summary>
    /// ledgers.md §8.2, LP-I12: a write which stored its batch and then failed in a way repetition cannot change is
    /// <c>Unresolved</c> at its slot, which the map's recorded <c>T</c> decides: <see cref="ProposalOutcome.Durable"/>
    /// when <c>T</c> lies above the slot, and <see cref="ProposalOutcome.Fenced"/> when it lies at it.
    /// </summary>
    [Theory]
    [InlineData(1, true)]
    [InlineData(0, false)]
    public async Task AFailedWriteIsDecidedFromTheRecordedTail(long recordedTail, bool durable)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var inner = WriterTestData.CreateReplica(Window);
        var replica = new DelegatingLedgerReplica
        {
            Write = async (slot, payload, tail, recovery, token) =>
            {
                _ = await inner.WriteAsync(slot, payload, tail, recovery, token);
                throw new NotSupportedException("The replica failed after storing the batch.");
            },
        };
        await using var primary = await Primary.StartAsync(() => replica, cancellationToken);
        using var row = CreateRow([0x10], "row"u8);

        var proposal = primary.Writer.ProposeAsync([row], cancellationToken).AsTask();
        await WriterTestData.WaitUntilAsync(() => primary.Writer.Sequencer.IsStopped);
        Assert.False(proposal.IsCompleted);
        await primary.PublishAsync(epoch: 2, new LedgerStatus.Closed(new Slot(recordedTail)), cancellationToken);

        Assert.Equal(durable ? Durable(0) : Fenced, await proposal);
    }

    /// <summary>
    /// ledgers.md §2.4: the writer's disposal fails a proposal with <see cref="ObjectDisposedException"/>, which asserts
    /// nothing, only while its map view still lists the ledger open.
    /// </summary>
    [Fact]
    public async Task DisposalFailsAProposalWhileItsViewListsTheLedgerOpen()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var inner = WriterTestData.CreateReplica(Window);
        await using var primary = await Primary.StartAsync(() => new TailRecordingLedgerReplica(inner), cancellationToken);
        _ = await inner.ReadTailAsync(Slot.Zero, seal: true, cancellationToken);
        using var row = CreateRow([0x10], "row"u8);
        var proposal = primary.Writer.ProposeAsync([row], cancellationToken).AsTask();
        await WriterTestData.WaitUntilAsync(() => primary.Writer.Sequencer.IsStopped);

        await primary.Writer.DisposeAsync();

        _ = await Assert.ThrowsAsync<ObjectDisposedException>(() => proposal);
    }

    /// <summary>
    /// ledgers.md §2.4: a proposal whose write waits out an outage when the writer is disposed is decided from the view
    /// as it stands: the view lists the ledger closed above the slot, so it is <see cref="ProposalOutcome.Durable"/>.
    /// </summary>
    [Fact]
    public async Task DisposalDecidesAProposalFromAViewWhichListsTheLedgerClosed()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var inner = WriterTestData.CreateReplica(Window);
        var attempts = 0;
        var replica = new DelegatingLedgerReplica
        {
            Write = async (slot, payload, tail, recovery, token) =>
            {
                _ = await inner.WriteAsync(slot, payload, tail, recovery, token);
                _ = Interlocked.Increment(ref attempts);
                throw new IOException("The replica is unreachable.");
            },
        };
        await using var primary = await Primary.StartAsync(() => replica, cancellationToken);
        using var row = CreateRow([0x10], "row"u8);
        var proposal = primary.Writer.ProposeAsync([row], cancellationToken).AsTask();
        await WriterTestData.WaitUntilAsync(() => Volatile.Read(ref attempts) >= 1);
        await primary.PublishAsync(epoch: 2, new LedgerStatus.Closed(new Slot(1)), cancellationToken);

        await primary.Writer.DisposeAsync();

        Assert.Equal(Durable(0), await proposal);
    }

    /// <summary>
    /// ledger-protocol.md §2.6, ledgers.md §2.4: proposals take slots in the order of their calls, also when the window
    /// is full and they wait for it, so a primary which proposes its batches in order has them decided in that order.
    /// </summary>
    [Fact]
    public async Task ProposalsTakeSlotsInTheOrderOfTheirCalls()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var inner = WriterTestData.CreateReplica(windowSize: 1);
        var replica = new TailRecordingLedgerReplica(inner) { HoldWrites = true };
        await using var primary = await Primary.StartAsync(() => replica, cancellationToken, windowSize: 1);
        var rows = Enumerable.Range(0, 6).Select(index => CreateRow([0x10], [(byte)index])).ToArray();
        try
        {
            var proposals = rows.Select(row => primary.Writer.ProposeAsync([row], cancellationToken).AsTask()).ToArray();
            for (var slot = 0L; slot < proposals.Length; slot++)
            {
                await replica.WriteStartedAsync(slot);
                replica.ReleaseWrite(slot);
            }

            for (var index = 0; index < proposals.Length; index++)
            {
                Assert.Equal(Durable(index), await proposals[index]);
                var read = await primary.Ledger.ReadAsync(new Slot(index), cancellationToken);
                using var batch = read.Batch;
                Assert.Null(BatchView.Decode(batch, out var view));
                foreach (var decoded in view)
                {
                    Assert.Equal([(byte)index], decoded.Body.ToArray());
                }
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

    /// <summary>
    /// ledgers.md §2.4: cancelling a creation while it waits withdraws it: it throws
    /// <see cref="OperationCanceledException"/>, creates nothing, and takes no slot, so the next creation takes the slot it
    /// would have; once a batch is created and proposed, cancellation only stops the caller waiting.
    /// </summary>
    [Fact]
    public async Task CancellingACreationWhichWaitsWithdrawsIt()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var inner = WriterTestData.CreateReplica(windowSize: 1);
        var replica = new TailRecordingLedgerReplica(inner) { HoldWrites = true };
        await using var primary = await Primary.StartAsync(() => replica, cancellationToken, windowSize: 1);
        using var row = CreateRow([0x10], "row"u8);
        using var precancelled = new CancellationTokenSource();
        await precancelled.CancelAsync();
        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await primary.Writer.CreateBatchAsync(1, 64, precancelled.Token));

        using var afterSlot = new CancellationTokenSource();
        var first = primary.Writer.ProposeAsync([row], afterSlot.Token).AsTask();
        await replica.WriteStartedAsync(0);
        using var queued = new CancellationTokenSource();
        var withdrawn = primary.Writer.CreateBatchAsync(1, 64, queued.Token).AsTask();
        var next = primary.Writer.ProposeAsync([row], cancellationToken).AsTask();
        await queued.CancelAsync();
        await afterSlot.CancelAsync();
        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => withdrawn);
        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);

        replica.ReleaseWrite(0);
        await replica.WriteStartedAsync(1);
        replica.ReleaseWrite(1);
        Assert.Equal(Durable(1), await next);
        Assert.Equal(2, replica.Operations.Count(static operation => operation.Kind == TailRecordingLedgerReplica.Write));
    }
    /// <summary>
    /// ledgers.md §8.3, LP-I9, LP-I14: the open check reports the primary's contiguous tail while no replica is sealed,
    /// covering every proposal it reported durable, and <see cref="OpenCheckOutcome.Sealed"/> once a recovery sealed the
    /// ledger, after which the lifetime is stopped, a proposal takes no slot, and the writer follows its map view, so it
    /// learns <c>T</c> once the map records it.
    /// </summary>
    [Fact]
    public async Task TheOpenCheckReportsTheContiguousTailUntilTheLedgerIsSealed()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var inner = WriterTestData.CreateReplica(Window);
        await using var primary = await Primary.StartAsync(() => new TailRecordingLedgerReplica(inner), cancellationToken);
        using var row = CreateRow([0x10], "row"u8);
        Assert.Equal(Durable(0), await primary.Writer.ProposeAsync([row], cancellationToken));
        Assert.Equal(Durable(1), await primary.Writer.ProposeAsync([row], cancellationToken));

        Assert.Equal(new OpenCheckOutcome.Open(new Slot(2)), (await primary.Writer.ConfirmOpenAsync(cancellationToken)).Value);

        var end = await RecoverElsewhereAsync(inner, cancellationToken);
        var reads = primary.Map.Reads;
        Assert.Same(OpenCheckOutcome.Sealed.Instance, (await primary.Writer.ConfirmOpenAsync(cancellationToken)).Value);
        Assert.True(primary.Writer.Sequencer.IsStopped);
        Assert.Equal(Fenced, await primary.Writer.ProposeAsync([row], cancellationToken));
        await WriterTestData.WaitUntilAsync(() => primary.Map.Reads > reads);
        primary.Map.Write(primary.Value(epoch: 2, new LedgerStatus.Closed(end)));
        _ = await primary.Map.View.ReadAsync(cancellationToken);
        await WriterTestData.WaitUntilAsync(() => primary.Ledger.KnownContiguousTail == end);
    }

    /// <summary>
    /// ledgers.md §8.3: concurrent open checks share checks. Calls made while a check is in flight share the next one,
    /// which begins after each of them, and a caller's cancellation ends only its own wait.
    /// </summary>
    [Fact]
    public async Task ConcurrentOpenChecksShareTheNextCheck()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var checks = 0;
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var replica = new DelegatingLedgerReplica
        {
            ReadTail = async (tail, seal, token) =>
            {
                if (Interlocked.Increment(ref checks) == 1)
                {
                    await held.Task.WaitAsync(token);
                }

                return new LedgerReplicaTail(tail, IsSealed: false);
            },
        };
        await using var primary = await Primary.StartAsync(() => replica, cancellationToken);

        var first = primary.Writer.ConfirmOpenAsync(cancellationToken);
        await WriterTestData.WaitUntilAsync(() => Volatile.Read(ref checks) == 1);
        using var cancelled = new CancellationTokenSource();
        var abandoned = primary.Writer.ConfirmOpenAsync(cancelled.Token);
        var shared = new[] { primary.Writer.ConfirmOpenAsync(cancellationToken), primary.Writer.ConfirmOpenAsync(cancellationToken) };
        await cancelled.CancelAsync();
        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned);
        held.SetResult();

        var open = new OpenCheckOutcome(new OpenCheckOutcome.Open(Slot.Zero));
        Assert.Equal(open, await first);
        Assert.All(await Task.WhenAll(shared), outcome => Assert.Equal(open, outcome));
        Assert.Equal(2, Volatile.Read(ref checks));
    }

    /// <summary>
    /// ledgers.md §2.4: an open check which backpressure keeps from deciding answers
    /// <see cref="ReplicaWriteOutcome.Backpressure"/>, which asserts nothing and leaves the lifetime running.
    /// </summary>
    [Fact]
    public async Task AnOpenCheckWhichBackpressureKeepsFromDecidingAnswersBackpressure()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var replica = new DelegatingLedgerReplica
        {
            ReadTail = static (_, _, _) => ValueTask.FromResult<ReplicaTailRead>(ReplicaWriteOutcome.Backpressure.Instance),
        };
        await using var primary = await Primary.StartAsync(() => replica, cancellationToken);

        Assert.Same(ReplicaWriteOutcome.Backpressure.Instance, (await primary.Writer.ConfirmOpenAsync(cancellationToken)).Value);
        Assert.False(primary.Writer.Sequencer.IsStopped);
    }

    /// <summary>
    /// ledgers.md §2.4, §2.5: disposing the writer while an open check is held refuses later checks with
    /// <see cref="ObjectDisposedException"/>, lets the held one answer, and releases the writer's lease, so the reader
    /// disposes every replica it opened.
    /// </summary>
    [Fact]
    public async Task DisposingTheWriterWhileAnOpenCheckIsHeldReleasesItsLease()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var opened = 0;
        var disposed = 0;
        var primary = await Primary.StartAsync(
            () =>
            {
                _ = Interlocked.Increment(ref opened);
                return new DelegatingLedgerReplica
                {
                    ReadTail = async (tail, seal, token) =>
                    {
                        await held.Task.WaitAsync(token);
                        return new LedgerReplicaTail(tail, IsSealed: false);
                    },
                    Disposal = () =>
                    {
                        _ = Interlocked.Increment(ref disposed);
                        return ValueTask.CompletedTask;
                    },
                };
            },
            cancellationToken);
        var check = primary.Writer.ConfirmOpenAsync(cancellationToken);

        await primary.Writer.DisposeAsync();
        _ = Assert.Throws<ObjectDisposedException>(() => { _ = primary.Writer.ConfirmOpenAsync(cancellationToken); });
        held.SetResult();
        _ = await check;
        await primary.DisposeAsync();

        Assert.Equal(1, opened);
        Assert.Equal(opened, disposed);
    }

    /// <summary>
    /// LP-I14, ledgers.md §2.4: a <see cref="Ledger"/> adopts one writer's lifetime, which it stops at once when this
    /// process already recovered the ledger, so the writer reports itself fenced.
    /// </summary>
    [Fact]
    public async Task ALifetimeAdoptedAfterARecoveryInThisProcessIsStoppedAtOnce()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var inner = WriterTestData.CreateReplica(Window);
        var map = await TestLedgerMap.OpenAsync(cancellationToken);
        await using var reader = new LedgerReader(map.View, new Transport(() => new TailRecordingLedgerReplica(inner)), new FakeTimeProvider(), localNode: null);
        var lifetime = new Sequencer(Window);
        using var lease = reader.LeaseLedger(Metadata(KeyRange.Root, Window, lifetime.InstanceId, LedgerStatus.Open.Instance));
        _ = (await lease.Ledger.RecoverAsync(cancellationToken)).End;

        await using var writer = lease.CreateWriter(lifetime, map.View, WriterTestData.Host(), new FakeTimeProvider());

        Assert.Equal(SequencerStopReason.Sealing, await lifetime.Stopped);
        await writer.Fenced.WaitAsync(cancellationToken);
        Assert.Equal(lifetime.InstanceId, writer.InstanceId);
        using var row = CreateRow([0x10], "row"u8);
        Assert.Equal(Fenced, await writer.ProposeAsync([row], cancellationToken));
    }

    /// <summary>The reasons a lifetime stops, which <see cref="SequencerStopReason"/> names inside the assembly.</summary>
    public enum StopCause
    {
        Sealing,
        PayloadWriteFailed,
        CompletionFailed,
        Requested,
    }

    public enum RefusedLifetime
    {
        /// <summary>The leased ledger's primary claim names another lifetime.</summary>
        Unclaimed,

        /// <summary>The claim names the lifetime, whose window is not the ledger's.</summary>
        OtherWindow,
    }

    /// <summary>
    /// LP-I15, ledgers.md §2.4: a lease creates a writer only over the lifetime the leased ledger's primary claim names,
    /// with the ledger's window: a writer exists only for a claim a map lists. A refused lifetime is not adopted, so the
    /// claimed one still is.
    /// </summary>
    [Theory]
    [InlineData(RefusedLifetime.Unclaimed)]
    [InlineData(RefusedLifetime.OtherWindow)]
    public async Task AWriterIsCreatedOnlyOverTheLifetimeTheLeasedClaimNames(RefusedLifetime refused)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var inner = WriterTestData.CreateReplica(Window);
        var map = await TestLedgerMap.OpenAsync(cancellationToken);
        await using var reader = new LedgerReader(map.View, new Transport(() => new TailRecordingLedgerReplica(inner)), new FakeTimeProvider(), localNode: null);
        var claimed = new Sequencer(Window);
        var other = refused == RefusedLifetime.Unclaimed ? new Sequencer(Window) : new Sequencer(Window + 1);
        var named = refused == RefusedLifetime.Unclaimed ? claimed : other;
        using var lease = reader.LeaseLedger(Metadata(KeyRange.Root, Window, named.InstanceId, LedgerStatus.Open.Instance));

        if (refused == RefusedLifetime.Unclaimed)
        {
            _ = Assert.Throws<InvalidOperationException>(() => lease.CreateWriter(other, map.View, WriterTestData.Host(), new FakeTimeProvider()));
            await using var writer = lease.CreateWriter(claimed, map.View, WriterTestData.Host(), new FakeTimeProvider());
            Assert.Equal(claimed.InstanceId, writer.InstanceId);
        }
        else
        {
            _ = Assert.Throws<ArgumentException>(() => lease.CreateWriter(other, map.View, WriterTestData.Host(), new FakeTimeProvider()));
        }

        Assert.False(other.IsStopped);
    }

    /// <summary>
    /// OR-I24: <see cref="LedgerWriter.PrepareReleaseAsync"/> waits for in-flight proposals to become durable, stops
    /// admitting with <see cref="SequencerStopReason.Released"/>, answers <see cref="LedgerPrepareReleaseOutcome.Released"/>
    /// with the contiguous tail, answers repeated calls idempotently, and fences later proposals.
    /// </summary>
    [Fact]
    public async Task PrepareReleaseDrainsInFlightProposalsStopsAdmittingAndIsIdempotent()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var inner = WriterTestData.CreateReplica(Window);
        var replica = new TailRecordingLedgerReplica(inner) { HoldWrites = true };
        await using var primary = await Primary.StartAsync(() => replica, cancellationToken);
        using var row = CreateRow([0x10], "in-flight"u8);

        var inflight = primary.Writer.ProposeAsync([row], cancellationToken).AsTask();
        await replica.WriteStartedAsync(0);

        var prepare = primary.Writer.PrepareReleaseAsync(TimeSpan.FromSeconds(30), cancellationToken);
        Assert.False(prepare.IsCompleted);

        replica.ReleaseWrite(0);
        Assert.Equal(Durable(0), await inflight);

        var outcome = await prepare;
        var released = Assert.IsType<LedgerPrepareReleaseOutcome.Released>(outcome.Value);
        Assert.Equal(new Slot(1), released.ContiguousTail);
        Assert.Equal(SequencerStopReason.Released, primary.Writer.Sequencer.StopReason);
        Assert.Equal(Fenced, await primary.Writer.ProposeAsync([row], cancellationToken));

        // Repeated PrepareReleaseAsync is idempotent and returns the same contiguous tail.
        var repeat = await primary.Writer.PrepareReleaseAsync(TimeSpan.FromSeconds(30), cancellationToken);
        Assert.Equal(released, Assert.IsType<LedgerPrepareReleaseOutcome.Released>(repeat.Value));
    }

    /// <summary>
    /// OR-I24: if another node seals the ledger while <see cref="LedgerWriter.PrepareReleaseAsync"/> is draining, the
    /// writer observes the seal and aborts immediately with <see cref="LedgerPrepareReleaseOutcome.Sealed"/> instead of
    /// hanging until the deadline.
    /// </summary>
    [Fact]
    public async Task PrepareReleaseAbortsImmediatelyOnConcurrentSeal()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var time = new FakeTimeProvider();
        await using var inner = WriterTestData.CreateReplica(Window);
        var replica = new TailRecordingLedgerReplica(inner) { HoldWrites = true };
        await using var primary = await Primary.StartAsync(() => replica, cancellationToken, time: time);
        using var row = CreateRow([0x10], "in-flight"u8);

        var inflight = primary.Writer.ProposeAsync([row], cancellationToken).AsTask();
        await replica.WriteStartedAsync(0);

        var prepare = primary.Writer.PrepareReleaseAsync(TimeSpan.FromHours(1), cancellationToken);
        Assert.False(prepare.IsCompleted);

        // Another node seals the ledger; releasing the held write observes the seal immediately without advancing the clock.
        var end = await RecoverElsewhereAsync(inner, cancellationToken);
        replica.ReleaseWrite(0);

        var outcome = await prepare;
        Assert.Same(LedgerPrepareReleaseOutcome.Sealed.Instance, outcome.Value);
        Assert.NotEqual(SequencerStopReason.Released, primary.Writer.Sequencer.StopReason);

        primary.Map.Write(primary.Value(epoch: 2, new LedgerStatus.Closed(end)));
        _ = await primary.Map.View.ReadAsync(cancellationToken);
        _ = await inflight;
    }

    /// <summary>
    /// OR-I24: if an in-flight proposal does not finish before the deadline, <see cref="LedgerWriter.PrepareReleaseAsync"/>
    /// returns <see cref="LedgerPrepareReleaseOutcome.TimedOut"/> and stops the lifetime as
    /// <see cref="SequencerStopReason.Sealing"/> rather than <see cref="SequencerStopReason.Released"/>.
    /// </summary>
    [Fact]
    public async Task PrepareReleaseReturnsTimedOutWhenDeadlineExpires()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var time = new FakeTimeProvider();
        await using var inner = WriterTestData.CreateReplica(Window);
        var replica = new TailRecordingLedgerReplica(inner) { HoldWrites = true };
        await using var primary = await Primary.StartAsync(() => replica, cancellationToken, time: time);
        using var row = CreateRow([0x10], "stuck"u8);

        var inflight = primary.Writer.ProposeAsync([row], cancellationToken).AsTask();
        await replica.WriteStartedAsync(0);

        var prepare = primary.Writer.PrepareReleaseAsync(TimeSpan.FromSeconds(5), cancellationToken);
        Assert.False(prepare.IsCompleted);

        time.Advance(TimeSpan.FromSeconds(5));
        var outcome = await prepare;
        Assert.Same(LedgerPrepareReleaseOutcome.TimedOut.Instance, outcome.Value);
        Assert.Equal(SequencerStopReason.Sealing, primary.Writer.Sequencer.StopReason);

        replica.ReleaseWrite(0);
        var end = await RecoverElsewhereAsync(inner, cancellationToken);
        primary.Map.Write(primary.Value(epoch: 2, new LedgerStatus.Closed(end)));
        _ = await primary.Map.View.ReadAsync(cancellationToken);
        _ = await inflight;
    }

    private static ProposalOutcome Fenced => new(default(ProposalOutcome.Fenced));

    private static ProposalOutcome Durable(long slot) => new(new ProposalOutcome.Durable(new Slot(slot)));

    private static Row CreateRow(byte[] key, ReadOnlySpan<byte> payload)
    {
        using var buffer = WriterTestData.CreatePayload(payload);
        return new Row(key, RowKind.Append, buffer);
    }

    private static LedgerMetadata Metadata(KeyRange range, int windowSize, SequencerInstanceId sequencer, LedgerStatus status) =>
        new(WriterTestData.Id, range, [Node], windowSize, new PrimaryClaim(Node, sequencer), status);

    /// <summary>Recovers the ledger through a <see cref="Ledger"/> of another process, over the same storage.</summary>
    internal static async Task<Slot> RecoverElsewhereAsync(ILedgerReplica storage, CancellationToken cancellationToken)
    {
        await using var executor = WriterTestData.CreateLedger([new TailRecordingLedgerReplica(storage)], quorum: 1, Window);
        return (await executor.RecoverAsync(cancellationToken)).End;
    }

    /// <summary>A transport whose every replica <paramref name="open"/> makes.</summary>
    private sealed class Transport(Func<ILedgerReplica> open) : ILedgerReplicaTransport
    {
        public ILedgerReplica Open(NodeId node, LedgerId ledger, LedgerReplicaOptions options) => open();

        public ValueTask<ReplicaHeartbeatRead> HeartbeatAsync(NodeId node, ReadOnlyMemory<LedgerId> ledgers, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new ReplicaHeartbeatRead(new ReplicaHeartbeatReply(TimeSpan.Zero, new ReplicaHeartbeatStatus[ledgers.Length])));
    }

    /// <summary>
    /// The primary's process: a map register and view, a reader over one replica, and the writer it creates through its
    /// lease of the ledger, whose open entry the map lists. Disposal disposes the writer and then the reader.
    /// </summary>
    internal sealed class Primary : IAsyncDisposable
    {
        private readonly LedgerReader.LedgerLease _lease;
        private readonly KeyRange _range;
        private readonly int _windowSize;

        private Primary(TestLedgerMap map, LedgerReader reader, LedgerReader.LedgerLease lease, LedgerWriter writer, KeyRange range, int windowSize)
        {
            Map = map;
            Reader = reader;
            _lease = lease;
            Writer = writer;
            _range = range;
            _windowSize = windowSize;
        }

        public TestLedgerMap Map { get; }

        public LedgerWriter Writer { get; }

        public Ledger Ledger => _lease.Ledger;

        public LedgerReader Reader { get; }

        public LedgerMetadata OpenMetadata => Metadata(_range, _windowSize, Writer.InstanceId, LedgerStatus.Open.Instance);

        public static async Task<Primary> StartAsync(
            Func<ILedgerReplica> replica,
            CancellationToken cancellationToken,
            int windowSize = Window,
            KeyRange? range = null,
            int maxBatchLength = int.MaxValue,
            LedgerWriterOptions? options = null,
            Random? random = null,
            TimeProvider? time = null,
            Action<LedgerSegmentKeys>? segmentEnded = null)
        {
            var map = await TestLedgerMap.OpenAsync(cancellationToken);
            var reader = new LedgerReader(map.View, new Transport(replica), new FakeTimeProvider(), localNode: null);
            var keys = range ?? KeyRange.Root;
            var lifetime = new Sequencer(windowSize);
            var lease = reader.LeaseLedger(Metadata(keys, windowSize, lifetime.InstanceId, LedgerStatus.Open.Instance));
            var host = new LedgerWriterHost(maxBatchLength, NullLogger.Instance, options, random, segmentEnded);
            var writer = lease.CreateWriter(lifetime, map.View, host, time ?? new FakeTimeProvider());
            var primary = new Primary(map, reader, lease, writer, keys, windowSize);
            await primary.PublishAsync(epoch: 1, LedgerStatus.Open.Instance, cancellationToken);
            return primary;
        }

        /// <summary>
        /// Returns a map value which lists the ledger, under the writer's claim, with <paramref name="status"/>, beside an
        /// open ledger over each sibling range on its path from the root, so the open ranges partition the keyspace (LG-I1).
        /// </summary>
        public LedgerMapValue Value(ulong epoch, LedgerStatus status)
        {
            var ledgers = new List<LedgerMetadata>();
            var siblings = 0UL;
            for (var range = _range; range.Depth > 0; range = range.Parent)
            {
                siblings++;
                ledgers.Add(new LedgerMetadata(
                    new LedgerId(WriterTestData.Id.Value - siblings),
                    range.Sibling,
                    [Node],
                    _windowSize,
                    new PrimaryClaim(Node, new SequencerInstanceId(Guid.NewGuid())),
                    LedgerStatus.Open.Instance));
            }

            ledgers.Reverse();
            ledgers.Add(Metadata(_range, _windowSize, Writer.InstanceId, status));
            return new(epoch, new LedgerId(WriterTestData.Id.Value + 1), 0, [.. ledgers]);
        }

        public Task PublishAsync(ulong epoch, LedgerStatus status, CancellationToken cancellationToken) =>
            Map.PublishAsync(Value(epoch, status), cancellationToken);

        public async ValueTask DisposeAsync()
        {
            await Writer.DisposeAsync();
            _lease.Dispose();
            await Reader.DisposeAsync();
        }
    }
}
