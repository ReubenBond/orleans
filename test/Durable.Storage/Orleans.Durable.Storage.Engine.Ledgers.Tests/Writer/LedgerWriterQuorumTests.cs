using System.Collections.Concurrent;
using Microsoft.Extensions.Time.Testing;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Ledgers.Tests.Writer;

#pragma warning disable CA2000 // Each replica double passes to the primary's Ledger, which owns and disposes it.

/// <summary>
/// Checks the writer's outcomes over a ledger of <c>N = 3</c> replicas with <c>Q = 2</c> where they differ from a
/// ledger of one replica (ledger-protocol.md §2.4, §6): the quorum masks a minority of failing replicas, and a minority of
/// sealed or dropped ones whose answers arrive after <c>Q</c> others wrote; a mixed transient-and-terminal failure is
/// waited out; a seal recovers once <c>max(Q, N − Q + 1)</c> replicas answer; and disposal stops a seal which cannot
/// (ledger-protocol.md §6.3, §8.3).
/// </summary>
public sealed class LedgerWriterQuorumTests : IDisposable
{
    private const int Replicas = 3;
    private const int Quorum = 2;
    private const int WindowSize = 4;

    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    public void Dispose() => _leaks.Dispose();

    /// <summary>
    /// LP-I4, LP-I12: a write is written once <c>Q</c> replicas answered written, so one replica whose writes fail,
    /// transiently, terminally, or with corruption, neither delays an append nor ends it. Each append writes that replica
    /// once, without a retry, completes in order, and stops nothing (ledger-protocol.md §2.4, §6).
    /// </summary>
    [Theory(Timeout = 30_000)]
    [InlineData("transient")]
    [InlineData("terminal")]
    [InlineData("corrupt")]
    public async Task AWriteWhichOneReplicaFailsIsWrittenWithoutARetry(string failure)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var storage = new LedgerReplicas(Replicas, WindowSize);
        var failedWrites = 0;
        var failing = new DelegatingLedgerReplica
        {
            Write = (address, _, _, _, _) =>
            {
                _ = Interlocked.Increment(ref failedWrites);
                return ValueTask.FromException<ReplicaWriteOutcome>(failure switch
                {
                    "transient" => new IOException("Injected storage outage."),
                    "terminal" => new ObjectDisposedException("replica", "Injected disposed replica."),
                    _ => new LedgerReplicaCorruptException(address, "Injected corruption."),
                });
            },
        };
        await using var log = WriterTestData.CreateLog([failing, storage.Open(1), storage.Open(2)], Quorum, windowSize: WindowSize);

        // The clock never advances, so an append which waited out a retry would never end.
        Assert.Equal(
            WriterTestData.Appended(0),
            await WriterTestData.AppendAsync(log, "first"u8.ToArray(), cancellationToken));
        Assert.Equal(
            WriterTestData.Appended(1),
            await WriterTestData.AppendAsync(log, "second"u8.ToArray(), cancellationToken));

        Assert.Equal(2, Volatile.Read(ref failedWrites));
        Assert.Empty(log.StopReasons);
        var read = await log.ReadAsync(Slot.Zero, cancellationToken);
        using var value = read.Batch;
        Assert.Equal("first"u8.ToArray(), value.ToArray());
    }

    /// <summary>
    /// outcomes.md §2, ledger-protocol.md §6.4, LP-I12: a write which cannot decide answers <c>Backpressure</c> when a
    /// replica answered it, which asserts nothing, and the writer repeats the identical write at the same slot rather than
    /// stopping its sequencer. Two of three replicas answer their first two writes with backpressure; the third write
    /// reaches <c>Q</c>, the append completes, and nothing stops.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task AWriterRepeatsAWriteWhichBackpressureAloneKeepsFromQuorum()
    {
        const int Answers = 2;
        var cancellationToken = TestContext.Current.CancellationToken;
        var time = new FakeTimeProvider();
        using var storage = new LedgerReplicas(Replicas, WindowSize);
        var writes = new int[Replicas];
        ILedgerReplica Backpressured(int replica)
        {
            var inner = storage.Open(replica);
            return new DelegatingLedgerReplica
            {
                Write = (slot, payload, tail, recovery, token) => Interlocked.Increment(ref writes[replica]) <= Answers
                    ? ValueTask.FromResult(ReplicaAnswers.Backpressure)
                    : inner.WriteAsync(slot, payload, tail, recovery, token),
                Read = inner.ReadAsync,
                ReadTail = inner.ReadTailAsync,
                Disposal = inner.DisposeAsync,
            };
        }

        await using var log = WriterTestData.CreateLog([storage.Open(0), Backpressured(1), Backpressured(2)], Quorum, WindowSize, time);

        var append = WriterTestData.AppendAsync(log, "first"u8.ToArray(), cancellationToken);

        Assert.Equal(WriterTestData.Appended(0), await WriterTestData.AdvanceUntilCompletedAsync(time, append));
        Assert.Equal([Answers + 1, Answers + 1], writes[1..]);
        Assert.Empty(log.StopReasons);
        Assert.False(log.Sequencer.IsStopped);
        var read = await log.ReadAsync(Slot.Zero, cancellationToken);
        using var value = read.Batch;
        Assert.Equal("first"u8.ToArray(), value.ToArray());
    }

    /// <summary>
    /// LP-I4, LP-I5, LR-I8: a writer write is sealed at the first sealed or dropped answer while fewer than <c>Q</c>
    /// replicas answered written, so a single sealed or dropped replica whose answer arrives first seals the append, which
    /// stops the sequencer with <see cref="SequencerStopReason.Sealing"/> and never completes its slot (LP-I8,
    /// ledger-protocol.md §2.4).
    /// </summary>
    [Theory(Timeout = 30_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnAppendWhichAMinorityReplicaAnswersSealedFirstIsSealed(bool dropped)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var storage = new LedgerReplicas(Replicas, WindowSize);
        await CloseAsync(storage, replica: 0, dropped, cancellationToken);
        TailRecordingLedgerReplica[] replicas = [.. storage.OpenEach().Select(static (replica, index) => new TailRecordingLedgerReplica(replica) { HoldWrites = index > 0 })];
        await using var log = WriterTestData.CreateLog(replicas, Quorum, windowSize: WindowSize);

        var append = WriterTestData.AppendAsync(log, "value"u8.ToArray(), cancellationToken);

        Assert.Equal(WriterTestData.Sealed(0), await append);
        Assert.Equal([SequencerStopReason.Sealing], log.StopReasons);
        Assert.Empty(log.CompletingSlots);
        replicas[1].ReleaseWrite(0);
        replicas[2].ReleaseWrite(0);
    }

    /// <summary>
    /// LP-I4, LP-I5: a writer write is written once <c>Q</c> replicas answered written, before any sealed or dropped
    /// answer arrives, so a single sealed or dropped replica whose answer arrives later leaves the append durable and its
    /// slot completed (ledger-protocol.md §2.4).
    /// </summary>
    [Theory(Timeout = 30_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnAppendWhichQReplicasWroteBeforeAMinorityReplicaAnsweredSealedIsDurable(bool dropped)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var storage = new LedgerReplicas(Replicas, WindowSize);
        await CloseAsync(storage, replica: 0, dropped, cancellationToken);
        TailRecordingLedgerReplica[] replicas = [.. storage.OpenEach().Select(static (replica, index) => new TailRecordingLedgerReplica(replica) { HoldWrites = index == 0 })];
        await using var log = WriterTestData.CreateLog(replicas, Quorum, windowSize: WindowSize);

        var append = WriterTestData.AppendAsync(log, "value"u8.ToArray(), cancellationToken);

        Assert.Equal(WriterTestData.Appended(0), await append);
        Assert.Empty(log.StopReasons);
        Assert.Equal([Slot.Zero], log.CompletingSlots);
        replicas[0].ReleaseWrite(0);
    }

    /// <summary>
    /// ledger-protocol.md §2.4: an append reported <c>IsSealed</c> "may still be on <c>Q</c> replicas", so its caller learns
    /// <c>T</c> from the close rather than taking it as lost. One sealed replica answers first; the writes it outran then
    /// land on the other two, and the seal's recovery finds them and ends above the position (LP-I8, LP-I7).
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task AnAppendReportedSealedMayStillBeOnQReplicasAndTheSealDecidesIt()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var storage = new LedgerReplicas(Replicas, WindowSize);
        await storage.SealAsync(0, cancellationToken);
        TailRecordingLedgerReplica[] replicas = [.. storage.OpenEach().Select(static (replica, index) => new TailRecordingLedgerReplica(replica) { HoldWrites = index > 0 })];
        await using var log = WriterTestData.CreateLog(replicas, Quorum, windowSize: WindowSize);
        Assert.Equal(
            WriterTestData.Sealed(0),
            await WriterTestData.AppendAsync(log, "value"u8.ToArray(), cancellationToken));

        replicas[1].ReleaseWrite(0);
        replicas[2].ReleaseWrite(0);
        await replicas[1].WriteLandedAsync(0);
        await replicas[2].WriteLandedAsync(0);

        Assert.Equal(new Slot(1), (await log.RecoverAsync(cancellationToken)).End);
        var read = await log.ReadAsync(Slot.Zero, cancellationToken);
        using var value = read.Batch;
        Assert.Equal("value"u8.ToArray(), value.ToArray());
    }

    /// <summary>
    /// LP-I12, ledger-protocol.md §6: "an <see cref="AggregateException"/> is transient when any inner failure is, as a quorum
    /// may form once one replica recovers". One replica's write times out, as a partitioned replica's does, while another
    /// fails in a way repetition cannot change, as a disposed replica does, so the quorum fails with both, fewer than
    /// <c>N − Q + 1</c> of them terminal; the append waits the outage out, identical bytes and all, and is written once
    /// the timed-out replica answers, the failed replica's failure notwithstanding. It stops nothing. A rule which retried
    /// only when every inner failure is transient would end this append.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task AMixedTransientAndTerminalFailureIsWaitedOutUntilAQuorumForms()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var time = new FakeTimeProvider();
        using var storage = new LedgerReplicas(Replicas, WindowSize);
        FlakyLedgerReplica[] replicas =
        [
            new(storage.Open(0)),
            new(new FailedLedgerReplica(static () => new ObjectDisposedException("replica", "Injected disposed replica."))),
            new(storage.Open(2)),
        ];
        replicas[0].FailNextCreates(Slot.Zero, CreateFault.NotLanded);
        await using var log = WriterTestData.CreateLog(replicas, Quorum, windowSize: WindowSize, time);

        var append = WriterTestData.AppendAsync(log, "value"u8.ToArray(), cancellationToken);

        Assert.Equal(
            WriterTestData.Appended(0),
            await WriterTestData.AdvanceUntilCompletedAsync(time, append));
        Assert.All(replicas, static replica =>
        {
            Assert.Equal(2, replica.Creates.Count);
            Assert.All(replica.Creates, static create => Assert.Equal("value"u8.ToArray(), create.Payload));
        });
        Assert.Empty(log.StopReasons);
    }

    /// <summary>
    /// LP-I12: "Only a failure which repetition cannot change ends the repetition, and it stops the lifetime (LP-I13)";
    /// ledger-protocol.md §6.4, Replica failures repetition cannot change: "When <c>N − Q + 1</c> replicas failed in ways
    /// repetition cannot change, so no write quorum can form, the quorum's write fails with an
    /// <see cref="AggregateException"/> carrying only those failures; stop with <c>PayloadWriteFailed</c> and answer
    /// <c>Unresolved(p)</c>". Two of three replicas failed so, as disposed replicas do, so the quorum fails with only their
    /// failures (ledger-protocol.md §8.3, Write), wherever the third replica's transient failure arrives among them, and
    /// the append ends without a retry. Before the fix, a transient failure among the first <c>N − Q + 1</c> answers kept
    /// the append retrying forever.
    /// </summary>
    [Theory(Timeout = 30_000)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task TerminalFailuresWhichLeaveFewerThanQReplicasEndTheAppendWithoutARetry(int transientReplica)
    {
        var transientWrites = 0;
        await using var log = WriterTestData.CreateLog(
            [
                .. Enumerable.Range(0, Replicas).Select(replica => replica == transientReplica
                    ? new DelegatingLedgerReplica
                    {
                        Write = (_, _, _, _, _) =>
                        {
                            _ = Interlocked.Increment(ref transientWrites);
                            return ValueTask.FromException<ReplicaWriteOutcome>(new IOException("Injected storage outage."));
                        },
                    }
                    : (ILedgerReplica)new FailedLedgerReplica(static () => new ObjectDisposedException("replica", "Injected disposed replica."))),
            ],
            Quorum,
            windowSize: WindowSize);

        // The clock never advances, so an append which waited out a retry would never end.
        Assert.Equal(
            WriterTestData.Unresolved(0),
            await WriterTestData.AppendAsync(log, "value"u8.ToArray(), TestContext.Current.CancellationToken));

        Assert.Equal(1, Volatile.Read(ref transientWrites));
        Assert.Equal([SequencerStopReason.PayloadWriteFailed], log.StopReasons);
    }

    /// <summary>
    /// LP-I10, ledger-protocol.md §8.3: a read returns DATA any replica reports, so one replica's corruption is masked where
    /// another holds the value.
    /// </summary>
    [Fact]
    public async Task AReadMasksOneCorruptReplicaWhereAnotherHoldsTheValue()
    {
        await using var log = ReadLogOverOneCorruptReplica(
            static () => ValueTask.FromResult(new ReplicaRead(WriterTestData.CreatePayload("value"u8))));

        var read = await log.ReadAsync(Slot.Zero, TestContext.Current.CancellationToken);

        Assert.Equal(ReplicaReadCase.Batch, read.Case);
        using var value = read.Batch;
        Assert.Equal("value"u8.ToArray(), value.ToArray());
    }

    /// <summary>
    /// LR-I2, LP-I10: "a replica's <see cref="LedgerReplicaCorruptException"/> is that replica's failure, and a read
    /// which finds no DATA on another replica fails". Below the caller's contiguous tail the position is decided, so the
    /// read reports the corruption although <c>Q</c> other replicas answer missing.
    /// </summary>
    [Fact]
    public async Task AReadBelowTheContiguousTailReportsOneReplicasCorruptionRatherThanMissing()
    {
        await using var log = ReadLogOverOneCorruptReplica(static () => ValueTask.FromResult(ReplicaAnswers.Missing));
        _ = log.Ledger.RaiseKnownContiguousTail(new Slot(1));

        _ = await WriterTestData.ThrowsReplicaFailureAsync<LedgerReplicaCorruptException>(
            async () => await log.ReadAsync(Slot.Zero, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// ledger-protocol.md §2.1, ledger-protocol.md §8.3: at or above every contiguous tail a read reports missing once <c>Q</c>
    /// replicas do, which decides nothing, so one replica's corruption there neither fails the read nor turns into
    /// DATA; the caller decides what the result means.
    /// </summary>
    [Fact]
    public async Task AReadAtTheContiguousTailReportsMissingOnceQReplicasDo()
    {
        await using var log = ReadLogOverOneCorruptReplica(static () => ValueTask.FromResult(ReplicaAnswers.Missing));

        var read = await log.ReadAsync(Slot.Zero, TestContext.Current.CancellationToken);

        Assert.Equal(ReplicaReadCase.Missing, read.Case);
    }

    /// <summary>
    /// LP-I5: a dropped answer counts as sealed and decides nothing else. With one replica of three dropped, reads below
    /// the contiguous tail over a whole rotation answer the DATA where they first ask a live replica and <c>Dropped</c>
    /// where they first ask the dropped one, since a drop is definite (ledger-protocol.md §6.4); a read at the tail reports
    /// missing once <c>Q</c> do, since it needs no dropped answer; and a seal whose recovery meets the dropped answer, here
    /// before any other replica answers, answers <c>Dropped</c>, since the ledger has left the map (ledger-protocol.md
    /// §3.1, §3.3).
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task AMinorityDroppedReplicaLeavesReadsDecidableButASealWhichMeetsItAnswersDropped()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var storage = new LedgerReplicas(Replicas, WindowSize);
        await using var primary = WriterTestData.CreateLog(storage.OpenEach(), Quorum, windowSize: WindowSize);
        Assert.Equal(WriterTestData.Appended(0), await WriterTestData.AppendAsync(primary, "value"u8.ToArray(), cancellationToken));
        await storage.DropAsync(0, cancellationToken);

        var dropped = 0;
        for (var read = 0; read < Replicas; read++)
        {
            var decided = await primary.ReadAsync(Slot.Zero, cancellationToken);
            if (decided.TryGetValue(out ReplicaWriteOutcome.Dropped? _))
            {
                dropped++;
                continue;
            }

            using var value = decided.Batch;
            Assert.Equal("value"u8.ToArray(), value.ToArray());
        }

        Assert.Equal(1, dropped);

        Assert.Equal(
            ReplicaReadCase.Missing,
            (await primary.ReadAsync(new Slot(1), cancellationToken)).Case);

        await using var sealer = WriterTestData.CreateLog(
            [
                storage.Open(0),
                new UnreachableLedgerReplica(storage.Open(1), Outage.Partitioned),
                new UnreachableLedgerReplica(storage.Open(2), Outage.Partitioned),
            ],
            Quorum, windowSize: WindowSize);
        DroppedAnswers.AssertDropped(await sealer.RecoverAsync(cancellationToken));
    }

    /// <summary>
    /// LP-I6, LP-I7, LP-I8: a seal returns its candidate end once <c>max(Q, N − Q + 1)</c> replicas answer, so one
    /// replica which no request reaches, refused or partitioned, delays it but never holds it (ledger-protocol.md §6.5). Every
    /// position below the end holds DATA, which a read hedges past the partitioned replica to reach (LP-I10), and the
    /// ledger reads as sealed.
    /// </summary>
    [Theory(Timeout = 30_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ASealEndsWhileOneReplicaIsUnreachable(bool partitioned)
    {
        const int Appended = 3;
        var cancellationToken = TestContext.Current.CancellationToken;
        using var storage = new LedgerReplicas(Replicas, WindowSize);
        var unreachable = new UnreachableLedgerReplica(storage.Open(2), partitioned ? Outage.Partitioned : Outage.Refused);
        var time = new TimerSignallingTimeProvider();
        await using var log = WriterTestData.CreateLog(
            [storage.Open(0), storage.Open(1), unreachable],
            Quorum, windowSize: WindowSize, timeProvider: time);
        try
        {
            for (var position = 0; position < Appended; position++)
            {
                byte[] value = [(byte)position];
                Assert.Equal(
                    WriterTestData.Appended(position),
                    await WriterTestData.AppendAsync(log, value, cancellationToken));
            }

            var attempts = unreachable.Attempts;

            Assert.Equal(new Slot(Appended), (await log.RecoverAsync(cancellationToken)).End);

            Assert.True(unreachable.Attempts > attempts);
            for (var position = 0; position < Appended; position++)
            {
                var timers = time.Timers;
                var read = await time.AdvanceUntilCompletedAsync(
                    log.ReadAsync(new Slot(position), cancellationToken).AsTask(), LedgerQuorum.ReadHedgeDelay, timers, cancellationToken)
                    .WaitAsync(TimeSpan.FromSeconds(10), TimeProvider.System, cancellationToken);
                using var bytes = read.Batch;
                byte[] value = [(byte)position];
                Assert.Equal(value, bytes.ToArray());
            }

            Assert.Equal(
                new LedgerTail(new Slot(Appended), IsSealed: true),
                (await log.ReadTailAsync(cancellationToken)).Tail);
        }
        finally
        {
            // A partitioned replica's reads outlast their operations, and the replica's disposal waits for them.
            unreachable.Reconnect();
        }
    }

    /// <summary>
    /// LP-I11, ledger-protocol.md §8.2: recoveries of one ledger may end at different candidate ends, and the map records one
    /// of them. A deposed writer's write of position 0 reached replica 2 alone. One executor's recovery cannot reach
    /// replica 2 and ends at 0; another's reaches it, copies position 0 to <c>Q</c> replicas, and ends at 1, which the
    /// map records. The first executor's Ledger learns the recorded <c>T</c>, above its own candidate end, and carries
    /// it, so position 0 lies below its known contiguous tail (ledger model mutant <c>learn-own-end</c>).
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task AnExecutorWhoseRecoveryLostLearnsTheRecordedTailAboveItsCandidateEnd()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var storage = new LedgerReplicas(Replicas, WindowSize);
        await using (var replica = storage.Open(2))
        using (var payload = WriterTestData.CreatePayload("held"u8))
        {
            Assert.Equal(
                ReplicaAnswers.Written,
                await replica.WriteAsync(Slot.Zero, payload, Slot.Zero, recovery: false, cancellationToken));
        }

        var hidden = new UnreachableLedgerReplica(storage.Open(2), Outage.Refused);
        var away = new UnreachableLedgerReplica(storage.Open(0), Outage.Refused);
        var loserReplica = new TailRecordingLedgerReplica(storage.Open(1));
        await using var loser = WriterTestData.CreateLog(
            [storage.Open(0), loserReplica, hidden],
            Quorum, windowSize: WindowSize);
        await using var winner = WriterTestData.CreateLog(
            [away, storage.Open(1), storage.Open(2)],
            Quorum, windowSize: WindowSize);
        try
        {
            Assert.Equal(Slot.Zero, (await loser.RecoverAsync(cancellationToken)).End);
            var recorded = (await winner.RecoverAsync(cancellationToken)).End;
            Assert.Equal(new Slot(1), recorded);
            Assert.Equal(Slot.Zero, loser.KnownContiguousTail);

            loser.ObserveRecordedTail(recorded);

            Assert.Equal(recorded, loser.KnownContiguousTail);
            Assert.Equal(new LedgerTail(recorded, IsSealed: true), (await loser.ReadTailAsync(cancellationToken)).Tail);
            var read = await loser.ReadAsync(Slot.Zero, cancellationToken);
            using var bytes = read.Batch;
            Assert.Equal("held"u8.ToArray(), bytes.ToArray());
            Assert.Contains(ReplicaOperation.ReadAt(0, recorded.Value), loserReplica.Operations);
        }
        finally
        {
            hidden.Reconnect();
            away.Reconnect();
        }
    }

    /// <summary>
    /// ledger-protocol.md §6.3: a recovery whose recovery cannot reach its coverage, <c>N − Q + 1</c> replicas
    /// being unreachable, waits; disposing the primary's process disposes its Ledger, which stops the recovery, so the disposal
    /// completes and the seal fails with <see cref="ObjectDisposedException"/>.
    /// </summary>
    [Theory(Timeout = 30_000)]
    [InlineData(1, 1, false)]
    [InlineData(1, 1, true)]
    [InlineData(3, 2, false)]
    [InlineData(3, 2, true)]
    public async Task Dispose_EndsASealStuckInARecoveryOverUnreachableReplicas(int replicas, int quorum, bool partitioned)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var storage = new LedgerReplicas(replicas, WindowSize);
        UnreachableLedgerReplica[] unreachable =
            [.. Enumerable.Range(0, WriterTestData.Coverage(replicas, quorum)).Select(replica => new UnreachableLedgerReplica(storage.Open(replica), partitioned ? Outage.Partitioned : Outage.Refused))];
        await using var log = WriterTestData.CreateLog(
            [.. unreachable, .. Enumerable.Range(unreachable.Length, replicas - unreachable.Length).Select(storage.Open)],
            quorum, windowSize: WindowSize);

        var seal = SealInBackground(log, cancellationToken);
        await WriterTestData.WaitUntilAsync(() => unreachable.All(static replica => replica.Attempts > 0));
        Assert.False(seal.IsCompleted);

        await log.DisposeAsync().AsTask().WaitAsync(cancellationToken);

        _ = await Assert.ThrowsAsync<ObjectDisposedException>(async () => await seal);
    }

    private static Task<RecoveryOutcome> SealInBackground(PrimaryLedger log, CancellationToken cancellationToken) =>
        log.RecoverAsync(cancellationToken).AsTask();

    private static Task CloseAsync(LedgerReplicas storage, int replica, bool dropped, CancellationToken cancellationToken) =>
        dropped ? storage.DropAsync(replica, cancellationToken).AsTask() : storage.SealAsync(replica, cancellationToken);

    /// <summary>
    /// Builds the primary's process over three replicas: the first answers every read with corruption, and the others with
    /// <paramref name="read"/>.
    /// </summary>
    private static PrimaryLedger ReadLogOverOneCorruptReplica(Func<ValueTask<ReplicaRead>> read) =>
        WriterTestData.CreateLog(
            [
                new DelegatingLedgerReplica
                {
                    Read = (address, _, _) => ValueTask.FromException<ReplicaRead>(
                        new LedgerReplicaCorruptException(address, "The value fails its integrity check.")),
                },
                new DelegatingLedgerReplica { Read = (_, _, _) => read() },
                new DelegatingLedgerReplica { Read = (_, _, _) => read() },
            ],
            Quorum, windowSize: WindowSize);
}
