using System.Buffers;
using System.Collections.Immutable;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Engine.Ledgers.Memory;
using Orleans.Durable.Storage.Engine.MetaStore;
using Orleans.Durable.Storage.Testing;
using static Orleans.Durable.Storage.Engine.Journals.Tests.JournalSamples;

namespace Orleans.Durable.Storage.Engine.Journals.Tests;

/// <summary>
/// <see cref="LedgerAdmission.For"/> over a real <see cref="LedgerWriter"/> (journals.md §8.1, ledgers.md §2.4): the
/// admission takes its ledger, range, window, and payload limit from the writer, proposes batches the writer creates on
/// in-memory replicas, and is fenced when the writer is disposed or stops admitting. Writes run on the thread pool.
/// </summary>
public sealed class LedgerAdmissionWriterTests : IDisposable
{
    private static readonly JournalWriteOutcome Fenced = new(JournalWriteOutcome.Fenced.Instance);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    public void Dispose() => _leaks.Dispose();

    private static JournalWriteOutcome Accepted(JournalVersion version, bool replaceRequested = false) => new(new JournalWriteOutcome.Accepted(version, ReplaceRequested: replaceRequested));

    /// <summary>Writes to many keys at once are each accepted once their batches are durable, and applied in slot order.</summary>
    [Fact]
    public async Task ConcurrentWritesThroughARealWriterAreAccepted()
    {
        await using var primary = await RealPrimary.StartAsync(KeyRange.Root);
        await using var admission = primary.Admission();

        var writes = await Task.WhenAll(Enumerable.Range(0, 32).Select(index => Task.Run(
            async () =>
            {
                using var first = Append(V(0), key: JournalRowKey.Encode($"k{index}"));
                using var second = Append(V(1), key: JournalRowKey.Encode($"k{index}"));
                var one = admission.AdmitAsync(first, TestContext.Current.CancellationToken);
                var two = admission.AdmitAsync(second, TestContext.Current.CancellationToken);
                return await Task.WhenAll(one, two).WaitAsync(Timeout, TimeProvider.System, TestContext.Current.CancellationToken);
            },
            TestContext.Current.CancellationToken)));

        Assert.All(writes, static outcomes => Assert.Equal([Accepted(V(1)), Accepted(V(2))], outcomes));
        Assert.True(admission.AppliedBelow.Value >= 1);
    }

    /// <summary>The admission takes the writer's range, and answers a key outside it <c>Moved</c> without an owner.</summary>
    [Fact]
    public async Task AKeyOutsideTheWritersRangeIsMoved()
    {
        var range = KeyRange.Containing(Key.Span, 1);
        var outside = Enumerable.Range(0, 64).Select(static index => JournalRowKey.Encode($"k{index}")).First(key => !range.Contains(key.Span));
        await using var primary = await RealPrimary.StartAsync(range);
        await using var admission = primary.Admission();
        using var foreign = Append(V(0), key: outside);
        using var own = Append(V(0));

        Assert.Equal(new JournalWriteOutcome(new JournalMoved(Owner: null)), await admission.AdmitAsync(foreign, TestContext.Current.CancellationToken).WaitAsync(Timeout, TimeProvider.System, TestContext.Current.CancellationToken));
        Assert.Equal(Accepted(V(1)), await admission.AdmitAsync(own, TestContext.Current.CancellationToken).WaitAsync(Timeout, TimeProvider.System, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// A writer disposed before the admission refuses its next proposal, which appended nothing: the write is
    /// <c>Fenced</c>, and so is every later write.
    /// </summary>
    [Fact]
    public async Task AWriterDisposedBeforeItsAdmissionFencesIt()
    {
        await using var primary = await RealPrimary.StartAsync(KeyRange.Root);
        await using var admission = primary.Admission();
        using var first = Append(V(0));
        Assert.Equal(Accepted(V(1)), await admission.AdmitAsync(first, TestContext.Current.CancellationToken).WaitAsync(Timeout, TimeProvider.System, TestContext.Current.CancellationToken));

        await primary.Writer.DisposeAsync();

        using var second = Append(V(1));
        using var third = Append(V(0), key: JournalRowKey.Encode("other"));
        Assert.Equal(Fenced, await admission.AdmitAsync(second, TestContext.Current.CancellationToken).WaitAsync(Timeout, TimeProvider.System, TestContext.Current.CancellationToken));
        Assert.Equal(Fenced, await admission.AdmitAsync(third, TestContext.Current.CancellationToken).WaitAsync(Timeout, TimeProvider.System, TestContext.Current.CancellationToken));
    }

    /// <summary>A writer which stopped admitting, as an executor's transition stops it, answers its next proposal <c>Fenced</c>, which fences the admission.</summary>
    [Fact]
    public async Task AWriterWhichStoppedAdmittingFencesItsAdmission()
    {
        await using var primary = await RealPrimary.StartAsync(KeyRange.Root);
        await using var admission = primary.Admission();

        await primary.Writer.StopAdmittingAsync(TestContext.Current.CancellationToken);

        using var first = Append(V(0));
        using var second = Append(V(0), key: JournalRowKey.Encode("other"));
        Assert.Equal(Fenced, await admission.AdmitAsync(first, TestContext.Current.CancellationToken).WaitAsync(Timeout, TimeProvider.System, TestContext.Current.CancellationToken));
        Assert.Equal(Fenced, await admission.AdmitAsync(second, TestContext.Current.CancellationToken).WaitAsync(Timeout, TimeProvider.System, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// OR-I24: <see cref="LedgerAdmission.PrepareReleaseAsync"/> flushes staged and in-flight writes cleanly, answers
    /// <see cref="LedgerAdmission.LedgerAdmissionDrainResult.Drained"/>, and refuses subsequent writes with
    /// <see cref="JournalMoved"/> instead of fencing in-flight callers; the underlying writer then releases cleanly with
    /// its final contiguous tail.
    /// </summary>
    [Fact]
    public async Task PrepareReleaseFlushesInFlightWritesAndMovesSubsequentWrites()
    {
        await using var primary = await RealPrimary.StartAsync(KeyRange.Root);
        await using var admission = primary.Admission();
        using var first = Append(V(0));
        using var second = Append(V(1));
        var w1 = admission.AdmitAsync(first, TestContext.Current.CancellationToken);
        var w2 = admission.AdmitAsync(second, TestContext.Current.CancellationToken);

        var drain = await admission.PrepareReleaseAsync(Timeout, primary.Writer.Fenced, TestContext.Current.CancellationToken);
        Assert.Equal(LedgerAdmission.LedgerAdmissionDrainResult.Drained, drain);
        Assert.Equal([Accepted(V(1)), Accepted(V(2))], await Task.WhenAll(w1, w2).WaitAsync(Timeout, TimeProvider.System, TestContext.Current.CancellationToken));

        // Subsequent writes are refused while draining/released, and repeated admission drain while open is idempotent.
        using var after = Append(V(2));
        Assert.Equal(Fenced, await admission.AdmitAsync(after, TestContext.Current.CancellationToken).WaitAsync(Timeout, TimeProvider.System, TestContext.Current.CancellationToken));
        Assert.Equal(LedgerAdmission.LedgerAdmissionDrainResult.Drained, await admission.PrepareReleaseAsync(Timeout, primary.Writer.Fenced, TestContext.Current.CancellationToken));

        var released = Assert.IsType<LedgerPrepareReleaseOutcome.Released>((await primary.Writer.PrepareReleaseAsync(Timeout, TestContext.Current.CancellationToken)).Value);
        Assert.True(released.ContiguousTail.Value >= 1);
        Assert.Equal(released, Assert.IsType<LedgerPrepareReleaseOutcome.Released>((await primary.Writer.PrepareReleaseAsync(Timeout, TestContext.Current.CancellationToken)).Value));
    }

    /// <summary>
    /// Adaptive coalescing in <see cref="LedgerAdmission"/> (journals.md §8.1): when idle (<c>_outstanding == 0</c>),
    /// the first small write proposes immediately; subsequent small writes arriving while that batch is in flight
    /// coalesce into one multi-row batch once it completes instead of opening separate 1-row proposals; large writes
    /// reaching <see cref="LedgerAdmission.CoalesceThresholdBytes"/> pipeline concurrently up to the window; and
    /// <see cref="LedgerAdmission.PrepareReleaseAsync"/> flushes coalescing small entries immediately.
    /// </summary>
    [Fact]
    public async Task AdaptiveCoalescingCombinesSmallWritesBehindInFlightBatchAndPipelinesLargeBatches()
    {
        var proposals = new List<(int RowCount, TaskCompletionSource<ProposalOutcome> Gate)>();
        var gateLock = new Lock();
        var firstProposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondProposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thirdProposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var nextSlot = 0L;

        ValueTask<LedgerBatch> CreateBatch(int rowCount, int maxLength, CancellationToken ct)
        {
            var slot = new Slot(Interlocked.Increment(ref nextSlot) - 1);
            var segment = new LedgerBatchSegment(IsStart: true, Offset: 0, OrdinalBase: 0, new LedgerSegmentDictionary());
            return ValueTask.FromResult(new LedgerBatch(KeyRange.Root, maxLength, rowCount, segment, segmentId: new LedgerSegmentId(new LedgerId(10), slot), slot: slot));
        }

        ValueTask<ProposalOutcome> Propose(LedgerBatch batch, CancellationToken ct)
        {
            var tcs = new TaskCompletionSource<ProposalOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
            int count;
            lock (gateLock)
            {
                proposals.Add((batch.DeclaredRowCount, tcs));
                count = proposals.Count;
            }

            if (count == 1)
            {
                _ = firstProposed.TrySetResult();
            }
            else if (count == 2)
            {
                _ = secondProposed.TrySetResult();
            }
            else if (count == 3)
            {
                _ = thirdProposed.TrySetResult();
            }

            return new ValueTask<ProposalOutcome>(tcs.Task);
        }

        await using var admission = new LedgerAdmission(
            new LedgerId(10),
            KeyRange.Root,
            window: 8,
            maxBatchLength: 1 << 25,
            new JournalStoreOptions(),
            JournalSamples.Store,
            TimeProvider.System,
            static (_, _) => ValueTask.FromResult(new JournalLoadOutcome(new JournalLoadOutcome.Loaded(new JournalLoad(null, JournalPlanning.Unrecorded(JournalSamples.Store, default))))),
            CreateBatch,
            Propose,
            static () => { },
            runInline: false);

        // 1. Idle: first small write proposes immediately as batch 0 (1 row).
        using var r0 = Append(V(0), "a", JournalRowKey.Encode("k0"));
        var w0 = admission.AdmitAsync(r0, TestContext.Current.CancellationToken);
        await firstProposed.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);

        // 2. While batch 0 is in flight, 5 small writes arrive -> must coalesce in _unbatched (still only 1 proposal!).
        var smallRecords = Enumerable.Range(1, 5).Select(i => Append(V(0), "s", JournalRowKey.Encode($"k{i}"))).ToArray();
        Task<JournalWriteOutcome>[] smallWrites;
        try
        {
            smallWrites = [.. smallRecords.Select(r => admission.AdmitAsync(r, TestContext.Current.CancellationToken))];
            await Task.Delay(50, TestContext.Current.CancellationToken);
            lock (gateLock)
            {
                Assert.Single(proposals);
            }

            // Completing batch 0 immediately sweeps all 5 coalesced small writes into ONE 5-row batch!
            lock (gateLock)
            {
                _ = proposals[0].Gate.TrySetResult(new ProposalOutcome.Durable(new Slot(0)));
            }

            await secondProposed.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
            Assert.Equal(Accepted(V(1)), await w0.WaitAsync(Timeout, TestContext.Current.CancellationToken));
            lock (gateLock)
            {
                Assert.Equal(2, proposals.Count);
                Assert.Equal(5, proposals[1].RowCount);
            }

            // 3. While batch 1 is in flight, a large write (>= CoalesceThresholdBytes) pipelines immediately as batch 2!
            using var largeRecord = Append(V(0), new string('L', LedgerAdmission.CoalesceThresholdBytes), JournalRowKey.Encode("klarge"));
            var largeWrite = admission.AdmitAsync(largeRecord, TestContext.Current.CancellationToken);
            await thirdProposed.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
            lock (gateLock)
            {
                Assert.Equal(3, proposals.Count);
                Assert.Equal(1, proposals[2].RowCount);
            }

            // 4. While batch 1 and batch 2 are in flight, a small write coalesces, and PrepareReleaseAsync flushes it!
            using var drainRecord = Append(V(0), "d", JournalRowKey.Encode("kdrain"));
            var drainWrite = admission.AdmitAsync(drainRecord, TestContext.Current.CancellationToken);
            var writerFenced = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var drainTask = admission.PrepareReleaseAsync(Timeout, writerFenced.Task, TestContext.Current.CancellationToken);

            // Wait until PrepareReleaseAsync flushes batch 3 without waiting for batches 1 & 2 to complete first!
            var deadline = DateTime.UtcNow + Timeout;
            while (DateTime.UtcNow < deadline)
            {
                lock (gateLock)
                {
                    if (proposals.Count == 4)
                    {
                        break;
                    }
                }

                await Task.Delay(5, TestContext.Current.CancellationToken);
            }

            lock (gateLock)
            {
                Assert.Equal(4, proposals.Count);
                _ = proposals[1].Gate.TrySetResult(new ProposalOutcome.Durable(new Slot(1)));
                _ = proposals[2].Gate.TrySetResult(new ProposalOutcome.Durable(new Slot(2)));
                _ = proposals[3].Gate.TrySetResult(new ProposalOutcome.Durable(new Slot(3)));
            }

            Assert.Equal(LedgerAdmission.LedgerAdmissionDrainResult.Drained, await drainTask.WaitAsync(Timeout, TestContext.Current.CancellationToken));
            Assert.All(await Task.WhenAll(smallWrites).WaitAsync(Timeout, TestContext.Current.CancellationToken), outcome => Assert.Equal(Accepted(V(1)), outcome));
            Assert.Equal(Accepted(V(1)), await largeWrite.WaitAsync(Timeout, TestContext.Current.CancellationToken));
            Assert.Equal(Accepted(V(1)), await drainWrite.WaitAsync(Timeout, TestContext.Current.CancellationToken));
        }
        finally
        {
            foreach (var r in smallRecords)
            {
                r.Dispose();
            }
        }
    }

    /// <summary>
    /// The primary's process: the ledger map in an in-memory register listing the ledger open over its range, beside an
    /// open sibling, a reader over in-memory replicas, and the writer created through the reader's lease of the ledger.
    /// Disposal disposes the writer, then the lease, the reader, and the replicas.
    /// </summary>
    private sealed class RealPrimary : IAsyncDisposable
    {
        private const int Window = 4;
        private static readonly NodeId Node = new(Guid.Parse("00000000-0000-0000-0000-0000000000c1"));
        private static readonly LedgerId Id = new(10);

        private readonly InMemoryLedgerReplicaTransport _replicas;
        private readonly LedgerReader _reader;
        private readonly LedgerReader.LedgerLease _lease;

        private RealPrimary(InMemoryLedgerReplicaTransport replicas, LedgerReader reader, LedgerReader.LedgerLease lease, LedgerWriter writer)
        {
            _replicas = replicas;
            _reader = reader;
            _lease = lease;
            Writer = writer;
        }

        public LedgerWriter Writer { get; }

        public static async Task<RealPrimary> StartAsync(KeyRange range)
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var lifetime = new Sequencer(Window);
            var ledger = new LedgerMetadata(Id, range, [Node], Window, new PrimaryClaim(Node, lifetime.InstanceId), LedgerStatus.Open.Instance);
            var ledgers = ImmutableArray.CreateBuilder<LedgerMetadata>();
            if (range.Depth > 0)
            {
                ledgers.Add(new LedgerMetadata(new LedgerId(Id.Value - 1), range.Sibling, [Node], Window, new PrimaryClaim(Node, new SequencerInstanceId(Guid.NewGuid())), LedgerStatus.Open.Instance));
            }

            ledgers.Add(ledger);
            var value = new LedgerMapValue(1, new LedgerId(Id.Value + 1), 0, ledgers.ToImmutable());
            var register = new InMemoryConditionalRegister();
            var bytes = new byte[value.EncodedLength];
            value.Write(bytes);
            _ = await register.WriteAsync(expectedVersion: null, new ReadOnlySequence<byte>(bytes), cancellationToken);
            var view = await LedgerMapView.OpenAsync(register, cancellationToken);
            var replicas = new InMemoryLedgerReplicaTransport();
            var reader = new LedgerReader(view, replicas, TimeProvider.System, localNode: null);
            var lease = reader.LeaseLedger(ledger);
            var writer = lease.CreateWriter(lifetime, view, new LedgerWriterHost(1 << 25, NullLogger.Instance), TimeProvider.System);
            return new RealPrimary(replicas, reader, lease, writer);
        }

        /// <summary>Creates the admission of the writer's ledger, over a loader which finds every journal absent.</summary>
        public LedgerAdmission Admission() =>
            LedgerAdmission.For(Writer, new JournalStoreOptions(), JournalSamples.Store, TimeProvider.System, static (_, _) => ValueTask.FromResult(new JournalLoadOutcome(new JournalLoadOutcome.Loaded(new JournalLoad(null, JournalPlanning.Unrecorded(JournalSamples.Store, default))))));

        public async ValueTask DisposeAsync()
        {
            await Writer.DisposeAsync();
            _lease.Dispose();
            await _reader.DisposeAsync();
            _replicas.Dispose();
        }
    }
}
