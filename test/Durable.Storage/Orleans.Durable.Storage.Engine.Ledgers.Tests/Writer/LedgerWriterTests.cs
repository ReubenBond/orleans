using System.Collections.Concurrent;
using Microsoft.Extensions.Time.Testing;
using Orleans.Durable.Storage.Testing;
using Polly;

namespace Orleans.Durable.Storage.Engine.Ledgers.Tests.Writer;

#pragma warning disable CA2000 // Each replica double passes to the primary's Ledger, which owns and disposes it.

public sealed class LedgerWriterTests : IDisposable
{
    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    public void Dispose() => _leaks.Dispose();

    /// <summary>
    /// ledger-protocol.md §2.1: a position read classifies storage only. It reports a hole as missing rather than passing over
    /// it, reads backwards as well as forwards, and returns payloads its caller owns independently.
    /// </summary>
    [Fact]
    public async Task Read_NeverSkipsAHoleAndReturnsIndependentlyOwnedPayloads()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var replica = WriterTestData.CreateReplica(windowSize: 64);
        await using var log = WriterTestData.CreateLog(replica, windowSize: 64);
        using (var first = WriterTestData.CreatePayload("first"u8))
        {
            _ = await replica.WriteAsync(Slot.Zero, first, Slot.Zero, recovery: false, cancellationToken);
        }

        using (var third = WriterTestData.CreatePayload("third"u8))
        {
            _ = await replica.WriteAsync(new Slot(2), third, Slot.Zero, recovery: false, cancellationToken);
        }

        var hole = await log.ReadAsync(new Slot(1), cancellationToken);
        Assert.Equal(ReplicaReadCase.Missing, hole.Case);
        var later = await log.ReadAsync(new Slot(2), cancellationToken);
        Assert.Equal(ReplicaReadCase.Batch, later.Case);
        using var retained = later.Batch;
        var backwards = await log.ReadAsync(Slot.Zero, cancellationToken);
        Assert.Equal(ReplicaReadCase.Batch, backwards.Case);
        using var earlier = backwards.Batch;
        Assert.Equal("first"u8.ToArray(), earlier.ToArray());
        Assert.Equal("third"u8.ToArray(), retained.ToArray());
    }

    /// <summary>
    /// ledger-protocol.md §2.1: a byte-range read returns bytes <c>[offset, offset + length)</c> of the DATA, passes a
    /// missing position through, answers a range past the value with <see cref="ReplicaRead.OutsideBatch"/>, and rejects
    /// an invalid range.
    /// </summary>
    [Fact]
    public async Task ByteRangeRead_ReturnsTheRangeOfDataPassesThroughMissingAndAnswersARangeOutsideTheValue()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var log = WriterTestData.CreateLog(WriterTestData.CreateReplica(windowSize: 64), windowSize: 64);
        using var payload = WriterTestData.CreatePayload("0123456789"u8);
        Assert.Equal(
            WriterTestData.Appended(0),
            await log.AppendAsync(payload, cancellationToken));
        Assert.Equal(new Slot(1), (await log.RecoverAsync(cancellationToken)).End);

        var range = await log.ReadAsync(Slot.Zero, 2, 5, cancellationToken);
        Assert.Equal(ReplicaReadCase.Batch, range.Case);
        using (var bytes = range.Batch)
        {
            Assert.Equal("23456"u8.ToArray(), bytes.ToArray());
        }

        var whole = await log.ReadAsync(Slot.Zero, 0, 10, cancellationToken);
        using (var bytes = whole.Batch)
        {
            Assert.Equal("0123456789"u8.ToArray(), bytes.ToArray());
        }

        Assert.Equal(
            ReplicaReadCase.Missing,
            (await log.ReadAsync(new Slot(1), 0, 1, cancellationToken)).Case);
        Assert.Equal(
            ReplicaReadCase.Missing,
            (await log.ReadAsync(new Slot(2), 0, 1, cancellationToken)).Case);

        // A range which extends past the value answers the value's length; an invalid range throws.
        Assert.Equal(new ReplicaRead.OutsideBatch(10), (await log.ReadAsync(Slot.Zero, 8, 3, cancellationToken)).Value);
        Assert.Equal(new ReplicaRead.OutsideBatch(10), (await log.ReadAsync(Slot.Zero, 10, 1, cancellationToken)).Value);
        _ = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            async () => await log.ReadAsync(Slot.Zero, -1, 1, cancellationToken));
        _ = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            async () => await log.ReadAsync(Slot.Zero, 0, 0, cancellationToken));
    }

    /// <summary>
    /// LP-I1: each append writes its own position, a contiguous-tail read reports the completed prefix the appends carried,
    /// and a read returns the appended payload.
    /// </summary>
    [Fact]
    public async Task AppendReadTailAndReadNext_PreservePayloadsAndPositions()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var log = WriterTestData.CreateLog(WriterTestData.CreateReplica());
        using var firstPayload = WriterTestData.CreatePayload("first"u8);
        using var secondPayload = WriterTestData.CreatePayload("second"u8);

        Assert.Equal(
            WriterTestData.Appended(0),
            await log.AppendAsync(firstPayload, cancellationToken));
        Assert.Equal(
            WriterTestData.Appended(1),
            await log.AppendAsync(secondPayload, cancellationToken));
        Assert.Equal(
            new LedgerTail(new Slot(2), IsSealed: false),
            (await log.ReadTailAsync(cancellationToken)).Tail);

        var read = await log.ReadAsync(new Slot(1), cancellationToken);
        Assert.Equal(ReplicaReadCase.Batch, read.Case);
        var readPayload = read.Batch;
        using (readPayload)
        {
            Assert.Equal("second"u8.ToArray(), readPayload.ToArray());
        }
    }

    [Fact]
    public async Task ZeroLengthPayload_IsRejected()
    {
        await using var log = WriterTestData.CreateLog(WriterTestData.CreateReplica());
        using var payload = WriterTestData.CreatePayload([]);

        await Assert.ThrowsAsync<ArgumentException>(
            async () => await log.AppendAsync(payload, TestContext.Current.CancellationToken));
        Assert.Equal(
            new LedgerTail(Slot.Zero, IsSealed: false),
            (await log.ReadTailAsync(TestContext.Current.CancellationToken)).Tail);
    }

    [Fact]
    public async Task CreateWriter_RejectsAMissingTimeProvider()
    {
        await using var ledger = WriterTestData.CreateLedger([new DelegatingLedgerReplica()], quorum: 1, windowSize: 1);

        Assert.Equal("timeProvider", Assert.Throws<ArgumentNullException>(() => ledger.CreateWriter(null!)).ParamName);
    }

    /// <summary>
    /// LP-I1: one writer gives each slot one batch, so a <see cref="Ledger"/> creates one writer for its life: a second
    /// <see cref="Ledger.CreateWriter(TimeProvider)"/> throws, while the first writer appends and after it is disposed, and the first
    /// writer's appends are unaffected.
    /// </summary>
    [Fact]
    public async Task ALedgerRefusesASecondWriter()
    {
        await using var primary = WriterTestData.CreateLog(WriterTestData.CreateReplica());

        _ = Assert.Throws<InvalidOperationException>(() => primary.Ledger.CreateWriter(TimeProvider.System));
        Assert.Equal(
            WriterTestData.Appended(0),
            await WriterTestData.AppendAsync(primary, "value"u8.ToArray(), TestContext.Current.CancellationToken));
        await primary.Writer.DisposeAsync();
        _ = Assert.Throws<InvalidOperationException>(() => primary.Ledger.CreateWriter(TimeProvider.System));
    }

    /// <summary>
    /// LP-I15, ledger-protocol.md §2.1: a <see cref="Ledger"/> creates its writer's lifetime, with its window <c>K</c>,
    /// which bounds recovery's reads and each replica's rebuilt contiguous tail, under a fresh instance ID: two writers
    /// over the same replicas, as two executors racing to open one range, hold distinct lifetimes.
    /// </summary>
    [Theory]
    [InlineData(4)]
    [InlineData(8)]
    public async Task AWriterCreatesALifetimeOfItsLedgersWindowUnderAFreshInstanceId(int window)
    {
        using var storage = new LedgerReplicas(count: 3, window);
        await using var first = WriterTestData.CreateLog(storage.OpenEach(), quorum: 2, window);
        await using var second = WriterTestData.CreateLog(storage.OpenEach(), quorum: 2, window);

        Assert.Equal(window, first.Sequencer.WindowSize);
        Assert.NotEqual(first.Sequencer.InstanceId, second.Sequencer.InstanceId);
        Assert.Equal(
            WriterTestData.Appended(0),
            await WriterTestData.AppendAsync(first, "value"u8.ToArray(), TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// ledger-protocol.md §2.1: a ranged read with a negative offset or a non-positive length is rejected before it reaches
    /// the replica.
    /// </summary>
    [Theory]
    [InlineData(-1, 1)]
    [InlineData(0, 0)]
    [InlineData(0, -1)]
    public async Task ByteRangeRead_RejectsAnInvalidRangeBeforeReadingTheReplicas(int offset, int length)
    {
        var replica = new TailRecordingLedgerReplica(new DelegatingLedgerReplica());
        await using var log = WriterTestData.CreateLog(replica);

        _ = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            async () => await log.ReadAsync(Slot.Zero, offset, length, TestContext.Current.CancellationToken));
        Assert.Empty(replica.Operations);
    }

    /// <summary>
    /// LR-I6, LP-I11: a contiguous tail is never inferred from landed DATA. A contiguous-tail read reports the greatest
    /// contiguous tail an operation carried, so DATA at 0, 1, and 2, whose writes carried at most 1, leaves it at 1. A
    /// position read reports a hole rather than skipping past it (ledger-protocol.md §2.1).
    /// </summary>
    [Fact]
    public async Task ReadTail_ReportsACarriedContiguousTailRatherThanTheLandedPrefix()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var dataReplica = WriterTestData.CreateReplica(windowSize: 3);
        await using var log = WriterTestData.CreateLog(dataReplica, windowSize: 3);
        using var payload = WriterTestData.CreatePayload("value"u8);
        _ = await dataReplica.WriteAsync(new Slot(0), payload, Slot.Zero, recovery: false, cancellationToken);
        _ = await dataReplica.WriteAsync(new Slot(2), payload, Slot.Zero, recovery: false, cancellationToken);

        Assert.Equal(
            new LedgerTail(Slot.Zero, IsSealed: false),
            (await log.ReadTailAsync(cancellationToken)).Tail);
        var hole = await log.ReadAsync(new Slot(1), cancellationToken);
        Assert.Equal(ReplicaReadCase.Missing, hole.Case);
        var afterHole = await log.ReadAsync(new Slot(2), cancellationToken);
        Assert.Equal(ReplicaReadCase.Batch, afterHole.Case);
        afterHole.Batch.Dispose();

        _ = await dataReplica.WriteAsync(new Slot(1), payload, new Slot(1), recovery: false, cancellationToken);
        Assert.Equal(
            new LedgerTail(new Slot(1), IsSealed: false),
            (await log.ReadTailAsync(cancellationToken)).Tail);
    }

    /// <summary>
    /// ledger-protocol.md §2.1: a read reports DATA or missing; the seal is positionless (LR-I8), so no read reports a seal,
    /// at the candidate end or above it.
    /// </summary>
    [Theory]
    [InlineData(1, 1)]
    [InlineData(3, 2)]
    public async Task Read_ClassifiesDataAndMissingPositionsAndNeverReportsASeal(int replicas, int quorum)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var storage = new LedgerReplicas(replicas, windowSize: 4);
        await using var log = WriterTestData.CreateLog(storage.OpenEach(), quorum, windowSize: 4);
        using var payload = WriterTestData.CreatePayload("value"u8);
        _ = await log.AppendAsync(payload, cancellationToken);
        Assert.Equal(new Slot(1), (await log.RecoverAsync(cancellationToken)).End);

        var read = await log.ReadAsync(Slot.Zero, cancellationToken);

        Assert.Equal(ReplicaReadCase.Batch, read.Case);
        read.Batch.Dispose();

        var atEnd = await log.ReadAsync(new Slot(1), cancellationToken);
        Assert.Equal(ReplicaReadCase.Missing, atEnd.Case);

        var missing = await log.ReadAsync(new Slot(2), cancellationToken);
        Assert.Equal(ReplicaReadCase.Missing, missing.Case);
    }

    /// <summary>
    /// LR-I2: a read never reports corruption as missing. When the replicas which answer corruption leave fewer than
    /// <c>Q</c> able to answer, <c>N − Q + 1</c> of them, the read cannot reach its quorum of missing answers, though
    /// every other replica answers missing, and the quorum reports each replica's <see cref="LedgerReplicaCorruptException"/>
    /// inside the failure of the read it could not decide.
    /// </summary>
    [Theory]
    [InlineData(1, 1)]
    [InlineData(3, 2)]
    public async Task Read_ReportsAReplicasCorruptionRatherThanMissing(int replicas, int quorum)
    {
        var corrupt = WriterTestData.Coverage(replicas, quorum);
        await using var log = WriterTestData.CreateLog(
            [
                .. Enumerable.Range(0, replicas).Select(replica => replica < corrupt
                    ? new DelegatingLedgerReplica
                    {
                        Read = (address, _, _) => ValueTask.FromException<ReplicaRead>(
                            new LedgerReplicaCorruptException(address, "The value fails its integrity check.")),
                    }
                    : new DelegatingLedgerReplica()),
            ],
            quorum, windowSize: 4);

        _ = await WriterTestData.ThrowsReplicaFailuresAsync<LedgerReplicaCorruptException>(
            async () => await log.ReadAsync(Slot.Zero, TestContext.Current.CancellationToken),
            corrupt);
    }

    /// <summary>
    /// LP-I8, ledger-protocol.md §8.1: an append whose write the replica answers <see cref="ReplicaAnswers.Sealed"/>, as it does once
    /// a replica answers sealed while fewer than <c>Q</c> answered written (LP-I4), here <c>N − Q + 1</c> sealed
    /// replicas, stops the sequencer with <see cref="SequencerStopReason.Sealing"/>, best effort, and never completes its
    /// slot.
    /// </summary>
    [Theory]
    [InlineData(1, 1)]
    [InlineData(3, 2)]
    public async Task Append_WriteResultSealed_StopsTheLifetimeAndDoesNotCompleteTheSlot(int replicas, int quorum)
    {
        var sealedReplicas = WriterTestData.Coverage(replicas, quorum);
        await using var log = WriterTestData.CreateLog(
            [
                .. Enumerable.Range(0, replicas).Select(replica => replica < sealedReplicas
                    ? new DelegatingLedgerReplica { Write = (_, _, _, _, _) => ValueTask.FromResult(ReplicaAnswers.Sealed) }
                    : new DelegatingLedgerReplica()),
            ],
            quorum,
            windowSize: 2);
        using var payload = WriterTestData.CreatePayload("zombie"u8);

        var result = await log.AppendAsync(payload, TestContext.Current.CancellationToken);

        Assert.Equal(WriterTestData.Sealed(0), result);
        Assert.Equal([SequencerStopReason.Sealing], log.StopReasons);
        Assert.Empty(log.CompletingSlots);
    }

    /// <summary>
    /// LP-I8: an append whose write reaches the replicas after another process's seal reports <c>IsSealed</c> at its
    /// position, stops the sequencer with <see cref="SequencerStopReason.Sealing"/>, and its slot never completes; the
    /// sealed replicas stored nothing for it (LR-I8).
    /// </summary>
    [Theory]
    [InlineData(1, 1)]
    [InlineData(3, 2)]
    public async Task Append_WhoseWriteMeetsASeal_IsSealedStopsTheLifetimeAndNeverCompletes(int replicas, int quorum)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var storage = new LedgerReplicas(replicas, windowSize: 4);
        TailRecordingLedgerReplica[] primaryReplicas = [.. storage.OpenEach().Select(static replica => new TailRecordingLedgerReplica(replica) { HoldWrites = true })];
        await using var primary = WriterTestData.CreateLog(primaryReplicas, quorum, windowSize: 4);
        await using var sealer = WriterTestData.CreateLedger(storage.OpenEach(), quorum, windowSize: 4);
        var first = WriterTestData.AppendAsync(primary, "first"u8.ToArray(), cancellationToken);
        foreach (var replica in primaryReplicas)
        {
            replica.ReleaseWrite(0);
            await replica.WriteLandedAsync(0);
        }

        Assert.Equal(WriterTestData.Appended(0), await first);
        var fenced = WriterTestData.AppendAsync(primary, "fenced"u8.ToArray(), cancellationToken);
        foreach (var replica in primaryReplicas)
        {
            await replica.WriteStartedAsync(1);
        }

        Assert.Equal(new Slot(1), (await sealer.RecoverAsync(cancellationToken)).End);
        foreach (var replica in primaryReplicas)
        {
            replica.ReleaseWrite(1);
        }

        Assert.Equal(WriterTestData.Sealed(1), await fenced);
        Assert.Equal([SequencerStopReason.Sealing], primary.StopReasons);
        Assert.Equal([Slot.Zero], primary.CompletingSlots);
        Assert.Equal(
            ReplicaReadCase.Missing,
            (await sealer.ReadAsync(new Slot(1), cancellationToken)).Case);
        _ = await Assert.ThrowsAsync<SequencerStoppedException>(
            () => primary.Sequencer.AcquireSlotAsync(cancellationToken).AsTask());
    }

    /// <summary>
    /// LP-I8, LP-I7: a seal returns the candidate end <c>T</c>. Every position below it holds DATA, a repeated seal in
    /// any process returns the same end, and an append at <c>T</c> from a lifetime the seal could not stop, another
    /// process's deposed primary (LP-I16), meets the seal and never completes.
    /// </summary>
    [Theory]
    [InlineData(1, 1)]
    [InlineData(3, 2)]
    public async Task Recover_ReturnsTheCandidateEndBelowWhichEverySlotHoldsABatchAndAtWhichNoAppendCompletes(int replicas, int quorum)
    {
        const int Appended = 3;
        var cancellationToken = TestContext.Current.CancellationToken;
        using var storage = new LedgerReplicas(replicas, windowSize: 4);
        await using var primary = WriterTestData.CreateLog(storage.OpenEach(), quorum, windowSize: 4);
        for (var position = 0; position < Appended; position++)
        {
            byte[] value = [(byte)position];
            Assert.Equal(WriterTestData.Appended(position), await WriterTestData.AppendAsync(primary, value, cancellationToken));
        }

        // The deposed primary's lifetime has granted and completed the slots the recovery recovers.
        await using var deposed = WriterTestData.CreateLog(storage.OpenEach(), quorum, windowSize: 4);
        await WriterTestData.AdvanceAsync(deposed.Sequencer, Appended, cancellationToken);

        var end = (await primary.RecoverAsync(cancellationToken)).End;

        Assert.Equal(new Slot(Appended), end);
        Assert.Equal(end, (await primary.RecoverAsync(cancellationToken)).End);
        for (var position = 0; position < end.Value; position++)
        {
            var read = await primary.ReadAsync(new Slot(position), cancellationToken);
            Assert.Equal(ReplicaReadCase.Batch, read.Case);
            using var bytes = read.Batch;
            byte[] value = [(byte)position];
            Assert.Equal(value, bytes.ToArray());
        }

        Assert.Equal(
            WriterTestData.Sealed(end.Value),
            await WriterTestData.AppendAsync(deposed, "fenced"u8.ToArray(), cancellationToken));
        Assert.Equal(end, deposed.Sequencer.CompletedTail);
        Assert.Equal(ReplicaReadCase.Missing, (await primary.ReadAsync(end, cancellationToken)).Case);
        Assert.Equal(end, (await deposed.RecoverAsync(cancellationToken)).End);
    }

    /// <summary>
    /// LP-I14: a recovery called on the <see cref="Ledger"/> directly, not through its writer, stops the writer's lifetime
    /// before the recovery sends its first seal.
    /// </summary>
    [Fact]
    public async Task ADirectRecoveryOfTheLedgerStopsItsWriterBeforeItsFirstSeal()
    {
        var operations = new ConcurrentQueue<string>();
        var dataReplica = new DelegatingLedgerReplica
        {
            ReadTail = (contiguousTail, seal, _) =>
            {
                operations.Enqueue(seal ? "seal" : "readTail");
                return ValueTask.FromResult<ReplicaTailRead>(new LedgerReplicaTail(contiguousTail, IsSealed: seal));
            },
        };
        await using var log = WriterTestData.CreateLog(dataReplica, windowSize: 1);
        var observer = log.Sequencer.Observer;
        log.Sequencer.Observer = step =>
        {
            observer!(step);
            if (step.Kind == SequencerStepKind.Stopped)
            {
                operations.Enqueue("stop");
            }
        };

        Assert.Equal(Slot.Zero, (await log.Ledger.RecoverAsync(TestContext.Current.CancellationToken)).End);

        Assert.Equal(["stop", "seal"], operations);
        Assert.Equal([SequencerStopReason.Sealing], log.StopReasons);
    }

    /// <summary>
    /// LP-I8: a seal is idempotent, returning the same candidate end each time, and the append which follows it is never
    /// acknowledged: the seal's best-effort stop stopped the sequencer, so the append acquires nothing and writes nothing.
    /// </summary>
    [Theory]
    [InlineData(1, 1)]
    [InlineData(3, 2)]
    public async Task Recover_IsIdempotentAndASubsequentAppendIsUnresolved(int replicas, int quorum)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var storage = new LedgerReplicas(replicas, windowSize: 4);
        await using var log = WriterTestData.CreateLog(storage.OpenEach(), quorum, windowSize: 4);
        using var payload = WriterTestData.CreatePayload("zombie"u8);

        Assert.Equal(Slot.Zero, (await log.RecoverAsync(cancellationToken)).End);
        Assert.Equal(Slot.Zero, (await log.RecoverAsync(cancellationToken)).End);

        Assert.Equal(WriterTestData.Unresolved(null), await log.AppendAsync(payload, cancellationToken));
        Assert.Equal(
            new LedgerTail(Slot.Zero, IsSealed: true),
            (await log.ReadTailAsync(cancellationToken)).Tail);
    }

    /// <summary>
    /// LP-I9: a contiguous-tail read reports the replica's contiguous tail and seal, which its quorum of replicas
    /// reported.
    /// </summary>
    [Theory]
    [InlineData(1, 1)]
    [InlineData(3, 2)]
    public async Task ReadTail_ReportsTheReplicasContiguousTailAndSeal(int replicas, int quorum)
    {
        await using var log = WriterTestData.CreateLog(
            [
                .. Enumerable.Range(0, replicas).Select(static _ => new DelegatingLedgerReplica
                {
                    ReadTail = (_, _, _) => ValueTask.FromResult<ReplicaTailRead>(new LedgerReplicaTail(new Slot(5), IsSealed: true)),
                }),
            ],
            quorum, windowSize: 1);

        var result = await log.ReadTailAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new LedgerTail(new Slot(5), IsSealed: true), result);
    }

    /// <summary>
    /// LP-I12: "A slot holder repeats the identical write, with bounded, jittered backoff, until it lands
    /// (<c>Written</c>) or meets a seal or a drop. Storage outages delay an append and never end its ledger." A write
    /// which landed before failing is answered <c>Written</c> by the next (LR-I1); one which did not is written again.
    /// Every attempt is a writer write. The outage reaches <c>N − Q + 1</c> replicas, so no attempt before it ends
    /// reaches <c>Q</c>.
    /// </summary>
    [Theory]
    [InlineData("L", 1, 1)]
    [InlineData("N", 1, 1)]
    [InlineData("NNLN", 1, 1)]
    [InlineData("NNNNNNNN", 1, 1)]
    [InlineData("L", 3, 2)]
    [InlineData("N", 3, 2)]
    [InlineData("NNLN", 3, 2)]
    [InlineData("NNNNNNNN", 3, 2)]
    public async Task StorageFailures_RetryIdenticalBytesUntilTheCreateLands(string faults, int replicas, int quorum)
    {
        ArgumentException.ThrowIfNullOrEmpty(faults);
        var time = new FakeTimeProvider();
        using var storage = new LedgerReplicas(replicas, windowSize: 2);
        FlakyLedgerReplica[] dataReplicas = [.. storage.OpenEach().Select(static replica => new FlakyLedgerReplica(replica))];
        var outage = dataReplicas[..WriterTestData.Coverage(replicas, quorum)];
        foreach (var dataReplica in outage)
        {
            dataReplica.FailNextCreates(Slot.Zero, [.. faults.Select(static fault => fault == 'L' ? CreateFault.Landed : CreateFault.NotLanded)]);
        }

        await using var log = WriterTestData.CreateLog(dataReplicas, quorum, windowSize: 2, time);

        var first = WriterTestData.AppendAsync(log, "first"u8.ToArray(), TestContext.Current.CancellationToken);
        var second = WriterTestData.AppendAsync(log, "second"u8.ToArray(), TestContext.Current.CancellationToken);

        Assert.Equal(
            WriterTestData.Appended(0),
            await WriterTestData.AdvanceUntilCompletedAsync(time, first));
        Assert.Equal(WriterTestData.Appended(1), await second);
        Assert.All(outage, dataReplica =>
        {
            var firstCreates = dataReplica.Creates.Where(static create => create.Address == Slot.Zero).ToArray();
            Assert.Equal(faults.Length + 1, firstCreates.Length);
            Assert.All(firstCreates, static create => Assert.Equal("first"u8.ToArray(), create.Payload));
        });
        Assert.All(dataReplicas.SelectMany(static dataReplica => dataReplica.Creates), static create => Assert.False(create.Recovery));
        Assert.Empty(log.StopReasons);
        Assert.Equal(
            new LedgerTail(new Slot(2), IsSealed: false),
            (await log.ReadTailAsync(TestContext.Current.CancellationToken)).Tail);
        var stored = await log.ReadAsync(Slot.Zero, TestContext.Current.CancellationToken);
        using var storedPayload = stored.Batch;
        Assert.Equal("first"u8.ToArray(), storedPayload.ToArray());
    }

    /// <summary>
    /// LP-I12: a write which is repeating after an outage ends once it meets a seal, reporting <c>IsSealed</c>. At
    /// <c>N = 3</c> the replica the outage spared holds the DATA, which the seal does not change.
    /// </summary>
    [Theory]
    [InlineData(1, 1)]
    [InlineData(3, 2)]
    public async Task SealWhileRetrying_SealsTheAppend(int replicas, int quorum)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var time = new FakeTimeProvider();
        using var storage = new LedgerReplicas(replicas, windowSize: 2);
        FlakyLedgerReplica[] dataReplicas = [.. storage.OpenEach().Select(static replica => new FlakyLedgerReplica(replica))];
        foreach (var dataReplica in dataReplicas[..WriterTestData.Coverage(replicas, quorum)])
        {
            dataReplica.FailNextCreates(Slot.Zero, CreateFault.NotLanded);
        }

        await using var log = WriterTestData.CreateLog(dataReplicas, quorum, windowSize: 2, time);

        var append = WriterTestData.AppendAsync(log, "sealed"u8.ToArray(), cancellationToken);
        await WriterTestData.WaitUntilAsync(() => dataReplicas.All(static dataReplica => !dataReplica.Creates.IsEmpty));
        for (var replica = 0; replica < replicas; replica++)
        {
            await storage.SealAsync(replica, cancellationToken);
        }

        Assert.Equal(
            WriterTestData.Sealed(0),
            await WriterTestData.AdvanceUntilCompletedAsync(time, append));
        Assert.Equal([SequencerStopReason.Sealing], log.StopReasons);
        Assert.Equal(
            new LedgerTail(Slot.Zero, IsSealed: true),
            (await log.ReadTailAsync(cancellationToken)).Tail);
    }

    /// <summary>
    /// LP-I13, LP-I12: a write failure which repetition cannot change, here the corruption which <c>N − Q + 1</c> replicas
    /// answer, so that no quorum can form, ends the append without a retry and stops the sequencer; the write was sent, so
    /// the append answers <c>Unresolved(0)</c> (ledger-protocol.md §2.4).
    /// </summary>
    [Theory]
    [InlineData(1, 1)]
    [InlineData(3, 2)]
    public async Task NonStorageWriteFailure_StopsTheLifetimeAndAnswersUnresolvedWithoutRetrying(int replicas, int quorum)
    {
        var writeCounts = new int[replicas];
        var corrupt = WriterTestData.Coverage(replicas, quorum);
        await using var log = WriterTestData.CreateLog(
            [
                .. Enumerable.Range(0, replicas).Select(replica => new DelegatingLedgerReplica
                {
                    Write = (address, _, _, _, _) =>
                    {
                        _ = Interlocked.Increment(ref writeCounts[replica]);
                        return replica < corrupt
                            ? ValueTask.FromException<ReplicaWriteOutcome>(new LedgerReplicaCorruptException(address, "Different bytes occupy the address."))
                            : ValueTask.FromResult(ReplicaAnswers.Written);
                    },
                }),
            ],
            quorum,
            windowSize: 2);

        Assert.Equal(
            WriterTestData.Unresolved(0),
            await WriterTestData.AppendAsync(log, "value"u8.ToArray(), TestContext.Current.CancellationToken));

        Assert.All(writeCounts, static count => Assert.Equal(1, count));
        Assert.Equal([SequencerStopReason.PayloadWriteFailed], log.StopReasons);
        Assert.Equal(
            WriterTestData.Unresolved(null),
            await WriterTestData.AppendAsync(log, "value"u8.ToArray(), TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// LP-I12, ledger-protocol.md §2.4, §6: replicas whose failure repetition cannot change, here disposed replicas, can
    /// neither land the write nor take a seal; once <c>N − Q + 1</c> have failed so, no quorum can form, so the append
    /// answers <c>Unresolved(0)</c> promptly, since the other replicas may hold its batch, and stops the sequencer instead
    /// of retrying forever.
    /// </summary>
    [Theory]
    [InlineData(1, 1)]
    [InlineData(3, 2)]
    public async Task TerminalLedgerReplicaFailure_AnswersUnresolvedPromptlyAndStopsTheLifetime(int replicas, int quorum)
    {
        var failed = WriterTestData.Coverage(replicas, quorum);
        using var storage = new LedgerReplicas(replicas, windowSize: 4);
        var time = new FakeTimeProvider();
        await using var log = WriterTestData.CreateLog(
            [
                .. Enumerable.Range(0, replicas).Select(replica => replica < failed
                    ? new FailedLedgerReplica(static () => new ObjectDisposedException("replica", "injected disposed replica"))
                    : storage.Open(replica)),
            ],
            quorum,
            windowSize: 4,
            time);

        var append = WriterTestData.AppendAsync(log, "value"u8.ToArray(), TestContext.Current.CancellationToken);

        Assert.Equal(WriterTestData.Unresolved(0), await WriterTestData.AdvanceUntilCompletedAsync(time, append));
        Assert.Equal([SequencerStopReason.PayloadWriteFailed], log.StopReasons);
    }

    /// <summary>
    /// Disposal ends a write waiting out an outage of <c>N − Q + 1</c> replicas, even for a caller which cannot cancel:
    /// the write began, so the append answers <c>Unresolved(0)</c>, which the recovered <c>T</c> decides, and the lifetime
    /// stops with <see cref="SequencerStopReason.Requested"/>, so the abandoned slot holds back no later completion
    /// (ledger-protocol.md §2.4, §6.3).
    /// </summary>
    [Theory]
    [InlineData(1, 1)]
    [InlineData(3, 2)]
    public async Task Dispose_EndsAWriteWaitingOutAnOutage(int replicas, int quorum)
    {
        var time = new FakeTimeProvider();
        using var storage = new LedgerReplicas(replicas, windowSize: 2);
        FlakyLedgerReplica[] dataReplicas = [.. storage.OpenEach().Select(static replica => new FlakyLedgerReplica(replica))];
        foreach (var dataReplica in dataReplicas[..WriterTestData.Coverage(replicas, quorum)])
        {
            dataReplica.FailNextCreates(Slot.Zero, [.. Enumerable.Repeat(CreateFault.NotLanded, 1_000)]);
        }

        await using var log = WriterTestData.CreateLog(dataReplicas, quorum, windowSize: 2, time);

        var append = WriterTestData.AppendAsync(log, "value"u8.ToArray(), CancellationToken.None);
        await WriterTestData.WaitUntilAsync(() => dataReplicas.All(static dataReplica => !dataReplica.Creates.IsEmpty));
        await log.DisposeAsync().AsTask().WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(WriterTestData.Unresolved(0), await append);
        Assert.Equal([SequencerStopReason.Requested, SequencerStopReason.Requested], log.StopReasons);
        _ = await Assert.ThrowsAsync<ObjectDisposedException>(
            () => WriterTestData.AppendAsync(log, "value"u8.ToArray(), TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// The writer reports its own steps through its lifetime's observer: a completion which returned once the lifetime's
    /// completion step, and a write which met a seal before the stop it makes (ledger-protocol.md §2.3).
    /// </summary>
    [Fact]
    public async Task TheObserverReceivesTheWritersSteps()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var log = WriterTestData.CreateLog(
            new DelegatingLedgerReplica
            {
                Write = static (slot, _, _, _, _) => ValueTask.FromResult(slot == Slot.Zero ? ReplicaAnswers.Written : ReplicaAnswers.Sealed),
            },
            windowSize: 1);
        var steps = new ConcurrentQueue<SequencerStep>();
        var observer = log.Sequencer.Observer;
        log.Sequencer.Observer = step =>
        {
            observer?.Invoke(step);
            steps.Enqueue(step);
        };

        Assert.Equal(WriterTestData.Appended(0), await WriterTestData.AppendAsync(log, "first"u8.ToArray(), cancellationToken));
        Assert.Equal(
            new AppendOutcome(new AppendOutcome.Sealed(new Slot(1))),
            await WriterTestData.AppendAsync(log, "second"u8.ToArray(), cancellationToken));

        Assert.Equal(
            [
                SequencerStep.Acquired(Slot.Zero),
                SequencerStep.Completing(Slot.Zero),
                SequencerStep.Completed(Slot.Zero),
                SequencerStep.Acquired(new Slot(1)),
                SequencerStep.Sealed(new Slot(1)),
                SequencerStep.Stopped(SequencerStopReason.Sealing),
            ],
            steps);
    }

    /// <summary>
    /// A write failure which repetition cannot change, which a replica reports only once the writer's disposal cancelled
    /// the write, is the disposal's: the quorum checks its caller's cancellation before it classifies a replica's failure,
    /// so the append answers <c>Unresolved(0)</c>, and the lifetime stops with
    /// <see cref="SequencerStopReason.Requested"/>, never <see cref="SequencerStopReason.PayloadWriteFailed"/>
    /// (ledger-protocol.md §2.4, §6.3).
    /// </summary>
    [Fact]
    public async Task TerminalWriteFailureDuringDisposal_StopsTheLifetimeAsTheDisposal()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var writing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var log = WriterTestData.CreateLog(
            new DelegatingLedgerReplica
            {
                Write = async (address, _, _, _, writeToken) =>
                {
                    writing.SetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, TimeProvider.System, writeToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                    throw new LedgerReplicaCorruptException(address, "Different bytes occupy the address.");
                },
            },
            windowSize: 1);
        var append = WriterTestData.AppendAsync(log, "value"u8.ToArray(), CancellationToken.None);
        await writing.Task.WaitAsync(cancellationToken);

        await log.Writer.DisposeAsync().AsTask().WaitAsync(cancellationToken);

        Assert.Equal(WriterTestData.Unresolved(0), await append.WaitAsync(cancellationToken));
        Assert.Equal([SequencerStopReason.Requested, SequencerStopReason.Requested], log.StopReasons);
    }

    /// <summary>
    /// A slot acquired once disposal has begun is never admitted for writing: the append writes nothing and is unresolved
    /// without a slot, as an append which acquired none is, and disposal, which stopped the lifetime, does not wait for it
    /// (ledger-protocol.md §6.3).
    /// </summary>
    [Fact]
    public async Task ASlotAcquiredDuringDisposalIsNeverWritten()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var storage = WriterTestData.CreateReplica(windowSize: 2);
        var dataReplica = new FlakyLedgerReplica(storage);
        await using var log = WriterTestData.CreateLog(dataReplica, windowSize: 2);
        using var disposed = new ManualResetEventSlim();
        var acquired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observer = log.Sequencer.Observer;
        log.Sequencer.Observer = step =>
        {
            observer?.Invoke(step);
            if (step.Kind == SequencerStepKind.Acquired)
            {
                acquired.SetResult();
                disposed.Wait(cancellationToken);
            }
        };

        var append = Task.Run(() => WriterTestData.AppendAsync(log, "value"u8.ToArray(), CancellationToken.None), cancellationToken);
        await acquired.Task.WaitAsync(cancellationToken);
        await log.Writer.DisposeAsync().AsTask().WaitAsync(cancellationToken);
        disposed.Set();

        Assert.Equal(WriterTestData.Unresolved(null), await append.WaitAsync(cancellationToken));
        Assert.Empty(dataReplica.Creates);
        Assert.Equal([SequencerStopReason.Requested, SequencerStopReason.Requested], log.StopReasons);
    }

    /// <summary>
    /// An append still waiting to acquire a slot when the writer's disposal stops the lifetime acquires none, writes
    /// nothing, and is unresolved without a slot (LP-I13).
    /// </summary>
    [Fact]
    public async Task AnAppendWaitingToAcquireWhenDisposalStopsTheLifetimeIsUnresolved()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var storage = WriterTestData.CreateReplica(windowSize: 2);
        var dataReplica = new FlakyLedgerReplica(storage);
        var acquiring = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var log = WriterTestData.CreateLog(dataReplica, windowSize: 2);
        log.Sequencer.BeforeAcquire = async token =>
        {
            acquiring.SetResult();
            await released.Task.WaitAsync(token);
        };

        var append = WriterTestData.AppendAsync(log, "value"u8.ToArray(), CancellationToken.None);
        await acquiring.Task.WaitAsync(cancellationToken);
        await log.Writer.DisposeAsync().AsTask().WaitAsync(cancellationToken);
        released.SetResult();

        Assert.Equal(WriterTestData.Unresolved(null), await append.WaitAsync(cancellationToken));
        Assert.Empty(dataReplica.Creates);
        Assert.Equal([SequencerStopReason.Requested], log.StopReasons);
    }

    /// <summary>
    /// LP-I12: the backoff between repeated writes is timed by the writer's time provider, so a write which failed
    /// is repeated only once that clock passes the delay.
    /// </summary>
    [Fact]
    public async Task StorageOutageDelays_ElapseOnTheInjectedTimeProvider()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var time = new FakeTimeProvider();
        await using var storage = WriterTestData.CreateReplica(windowSize: 2);
        var dataReplica = new FlakyLedgerReplica(storage);
        dataReplica.FailNextCreates(Slot.Zero, CreateFault.NotLanded);
        await using var log = WriterTestData.CreateLog(dataReplica, windowSize: 2, time);

        var append = WriterTestData.AppendAsync(log, "value"u8.ToArray(), cancellationToken);
        await WriterTestData.WaitUntilAsync(() => !dataReplica.Creates.IsEmpty);

        // Real time passes well beyond the largest first delay while the injected clock stands still.
        _ = await Task.WhenAny(append, Task.Delay(TimeSpan.FromMilliseconds(500), TimeProvider.System, cancellationToken));
        Assert.False(append.IsCompleted);
        Assert.Single(dataReplica.Creates);

        Assert.Equal(
            WriterTestData.Appended(0),
            await WriterTestData.AdvanceUntilCompletedAsync(time, append));
    }

    [Fact]
    public void StorageOutageRetry_RejectsAMissingTimeProvider() =>
        _ = Assert.Throws<ArgumentNullException>(() => StorageOutageRetry.Create(null!));

    /// <summary>
    /// LP-I12, ledger-replicas.md §6.4: a failure repetition cannot change ends the run at its first attempt. An
    /// <see cref="InvalidDataException"/> is one, from any source, so the run makes exactly one attempt, though a
    /// repetition would have succeeded.
    /// </summary>
    [Fact]
    public async Task StorageOutageRetry_MakesOneAttemptAtAnInvalidDataException()
    {
        var time = new FakeTimeProvider();
        var attempts = 0;
        var run = StorageOutageRetry.Create(time)
            .ExecuteAsync(
                _ => Interlocked.Increment(ref attempts) == 1
                    ? ValueTask.FromException<LedgerWriteOutcome>(new InvalidDataException("A read below the known contiguous tail found every replica missing."))
                    : ValueTask.FromResult<LedgerWriteOutcome>(ReplicaWriteOutcome.Written.Instance),
                TestContext.Current.CancellationToken)
            .AsTask();

        _ = await Assert.ThrowsAsync<InvalidDataException>(() => WriterTestData.AdvanceUntilCompletedAsync(time, run));
        Assert.Equal(1, Volatile.Read(ref attempts));
    }

    /// <summary>
    /// LP-I12, outcomes.md §1: a <see cref="ReplicaWriteOutcome.Backpressure"/> answer asserts nothing, so the run repeats
    /// the write until it answers otherwise.
    /// </summary>
    [Fact]
    public async Task StorageOutageRetry_RepeatsABackpressureAnswer()
    {
        const int Answers = 3;
        var time = new FakeTimeProvider();
        var attempts = 0;
        var run = StorageOutageRetry.Create(time)
            .ExecuteAsync(
                _ => ValueTask.FromResult<LedgerWriteOutcome>(Interlocked.Increment(ref attempts) <= Answers ? ReplicaWriteOutcome.Backpressure.Instance : ReplicaWriteOutcome.Written.Instance),
                TestContext.Current.CancellationToken)
            .AsTask();

        Assert.Equal(ReplicaWriteCase.Written, (await WriterTestData.AdvanceUntilCompletedAsync(time, run)).Case);
        Assert.Equal(Answers + 1, Volatile.Read(ref attempts));
    }

    /// <summary>
    /// LP-I12: "A slot holder repeats the identical write, with bounded, jittered backoff". Every delay between
    /// repetitions is at least <see cref="StorageOutageRetry.BaseDelay"/> and at most
    /// <see cref="StorageOutageRetry.MaximumDelay"/>, and two outages of the same length wait out different delays, where a
    /// backoff without jitter would repeat one sequence.
    /// </summary>
    [Fact]
    public async Task StorageOutageRetry_DelaysAreBoundedAndJittered()
    {
        var first = await RecordOutageDelaysAsync();
        var second = await RecordOutageDelaysAsync();

        Assert.All(first.Concat(second), delay => Assert.InRange(delay, StorageOutageRetry.BaseDelay, StorageOutageRetry.MaximumDelay));
        Assert.NotEqual(first, second);

        static async Task<TimeSpan[]> RecordOutageDelaysAsync()
        {
            const int Outages = 6;
            var fake = new FakeTimeProvider();
            var time = new DelayRecordingTimeProvider(fake);
            var attempts = 0;
            var run = StorageOutageRetry.Create(time)
                .ExecuteAsync(
                    _ => Interlocked.Increment(ref attempts) <= Outages
                        ? ValueTask.FromException<LedgerWriteOutcome>(new IOException("outage"))
                        : ValueTask.FromResult<LedgerWriteOutcome>(ReplicaWriteOutcome.Written.Instance),
                    TestContext.Current.CancellationToken)
                .AsTask();

            Assert.Equal(ReplicaWriteCase.Written, (await WriterTestData.AdvanceUntilCompletedAsync(fake, run)).Case);
            Assert.Equal(Outages + 1, Volatile.Read(ref attempts));
            Assert.Equal(Outages, time.Delays.Count);
            return [.. time.Delays];
        }
    }

    /// <summary>
    /// Regression test, #335 (conventions.md §6): the replica, storage-outage, and map-read retries wait at least their
    /// first step before every first repetition. Polly's own jittered backoff drew a wait of zero about once in 840 first
    /// repetitions, which repeated at once without arming a timer.
    /// </summary>
    [Fact]
    public async Task EveryFirstRepetitionWaitsAtLeastItsFirstStep()
    {
        await AssertFirstRepetitionsWaitAsync(time => ReplicaRetry.Create<string?>(time, static answer => answer is null), null, "answered", ReplicaRetry.BaseDelay);
        await AssertFirstRepetitionsWaitAsync<LedgerWriteOutcome>(StorageOutageRetry.Create, ReplicaWriteOutcome.Backpressure.Instance, ReplicaWriteOutcome.Written.Instance, StorageOutageRetry.BaseDelay);
        await AssertFirstRepetitionsWaitAsync<bool>(LedgerWriter.CreateMapReadRetry, false, true, LedgerWriter.MapReadBaseDelay);

        static async Task AssertFirstRepetitionsWaitAsync<T>(Func<TimeProvider, ResiliencePipeline<T>> create, T repeated, T answered, TimeSpan first)
        {
            var fake = new FakeTimeProvider();
            var time = new DelayRecordingTimeProvider(fake);
            var retry = create(time);
            const int Repetitions = 10_000;
            for (var run = 0; run < Repetitions; run++)
            {
                var attempts = 0;
                var delays = time.Delays.Count;
                var repeating = retry.ExecuteAsync(_ => ValueTask.FromResult(++attempts == 1 ? repeated : answered), TestContext.Current.CancellationToken).AsTask();
                Assert.True(attempts == 1 && time.Delays.Count == delays + 1, $"Repetition {run} began without waiting on a timer.");
                fake.Advance(time.Delays.Last());
                Assert.Equal(answered, await repeating.WaitAsync(TimeSpan.FromSeconds(30), TimeProvider.System, TestContext.Current.CancellationToken));
            }

            Assert.All(time.Delays, delay => Assert.True(delay >= first, $"A first repetition waited {delay}, below {first}."));
        }
    }

    /// <summary>
    /// Cancelling stops the caller observing the outcome, not the write (ledger-protocol.md §6). The write lands after the
    /// caller released its payload, and the later append completes after it, in position order.
    /// </summary>
    [Fact]
    public async Task CancellationDuringRetry_StopsWaitingWhileTheWriteLandsAndLaterAppendsCompleteInOrder()
    {
        var time = new FakeTimeProvider();
        await using var storage = WriterTestData.CreateReplica(windowSize: 2);
        var dataReplica = new FlakyLedgerReplica(storage);
        dataReplica.FailNextCreates(Slot.Zero, CreateFault.NotLanded, CreateFault.NotLanded);
        await using var log = WriterTestData.CreateLog(dataReplica, windowSize: 2, time);
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        var cancelled = WriterTestData.AppendAsync(log, "first"u8.ToArray(), caller.Token);
        await WriterTestData.WaitUntilAsync(() => !dataReplica.Creates.IsEmpty);
        await caller.CancelAsync();
        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await cancelled);
        var later = WriterTestData.AppendAsync(log, "second"u8.ToArray(), TestContext.Current.CancellationToken);

        Assert.Equal(
            WriterTestData.Appended(1),
            await WriterTestData.AdvanceUntilCompletedAsync(time, later));
        Assert.Empty(log.StopReasons);
        Assert.All(dataReplica.Creates, create => Assert.NotEqual(caller.Token, create.Token));
        var stored = await log.ReadAsync(Slot.Zero, TestContext.Current.CancellationToken);
        using var storedPayload = stored.Batch;
        Assert.Equal("first"u8.ToArray(), storedPayload.ToArray());
        Assert.Equal(
            new LedgerTail(new Slot(2), IsSealed: false),
            (await log.ReadTailAsync(TestContext.Current.CancellationToken)).Tail);
    }

    /// <summary>
    /// LP-I13: a completion failure after a durable batch is unresolved at the batch's slot. The writer stops the lifetime,
    /// so a later append is unresolved without a slot, and the primary resolves the append from the recovered end.
    /// </summary>
    [Fact]
    public async Task FailedCompletion_StopsTheLifetimeAndIsUnresolvedAtTheDurableSlot()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var dataReplica = WriterTestData.CreateReplica(windowSize: 2);
        await using var log = WriterTestData.CreateLog(dataReplica, windowSize: 2);
        log.Sequencer.BeforeComplete = static (_, _) => ValueTask.FromException(new IOException("completion failed"));
        using var payload = WriterTestData.CreatePayload("zombie"u8);

        Assert.Equal(WriterTestData.Unresolved(0), await log.AppendAsync(payload, cancellationToken));

        Assert.Equal([SequencerStopReason.CompletionFailed], log.StopReasons);
        Assert.Equal("zombie"u8.ToArray(), payload.ToArray());
        var durable = await dataReplica.ReadAsync(Slot.Zero, Slot.Zero, cancellationToken);
        Assert.Equal(ReplicaReadCase.Batch, durable.Case);
        durable.Batch.Dispose();
        var later = log.AppendAsync(payload, cancellationToken).AsTask();
        Assert.True(later.IsCompleted);
        Assert.Equal(WriterTestData.Unresolved(null), await later);
    }

    [Fact]
    public async Task PreCancelledAppend_DoesNotWrite()
    {
        var writeCount = 0;
        var dataReplica = new DelegatingLedgerReplica
        {
            Write = (_, _, _, _, _) =>
            {
                writeCount++;
                return ValueTask.FromResult(ReplicaAnswers.Written);
            },
        };
        await using var log = WriterTestData.CreateLog(
            dataReplica, windowSize: 1);
        using var payload = WriterTestData.CreatePayload("value"u8);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await log.AppendAsync(payload, cancellation.Token));
        Assert.Equal(0, writeCount);
    }

    /// <summary>
    /// ledger-protocol.md §6.3: the writer's disposal cancels its writes and waits for them to end; the
    /// <see cref="Ledger"/>'s disposal then disposes the replicas, which it owns, and reports their failures. The replica's
    /// write ends before the replica is disposed.
    /// </summary>
    [Fact]
    public async Task Dispose_DisposesTheReplicasOnceTheWritesEndAndReportsTheirFailure()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var events = new ConcurrentQueue<string>();
        var replicaFailure = new IOException("The replica could not be disposed.");
        var writeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var replica = new DelegatingLedgerReplica
        {
            Write = async (_, _, _, _, writeToken) =>
            {
                writeStarted.SetResult();
                try
                {
                    await new TaskCompletionSource().Task.WaitAsync(writeToken);
                }
                finally
                {
                    events.Enqueue("write ended");
                }

                return ReplicaAnswers.Written;
            },
            Disposal = () =>
            {
                events.Enqueue("replica disposed");
                return ValueTask.FromException(replicaFailure);
            },
        };
        var log = WriterTestData.CreateLog(replica, windowSize: 1);
        var append = WriterTestData.AppendAsync(log, "value"u8.ToArray(), CancellationToken.None);
        await writeStarted.Task.WaitAsync(cancellationToken);

        var failure = await Assert.ThrowsAsync<AggregateException>(() => log.DisposeAsync().AsTask());

        Assert.Same(replicaFailure, Assert.Single(failure.InnerExceptions));
        Assert.Equal(["write ended", "replica disposed"], events);
        Assert.Equal(WriterTestData.Unresolved(0), await append.WaitAsync(cancellationToken));
    }

    /// <summary>
    /// ledger-protocol.md §2.4, §6.3: the writer's disposal cancels an admitted write, and the <see cref="Ledger"/> stops
    /// waiting for it; the write was sent, so the append answers <c>Unresolved(0)</c>. A replica write which outlasts its
    /// cancellation still holds the replica's disposal, so no write uses the replica once it is disposed.
    /// </summary>
    [Fact]
    public async Task Dispose_DisposesTheReplicaOnlyOnceAReplicaWriteWhichOutlastsItsCancellationEnds()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var events = new ConcurrentQueue<string>();
        var writeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writeReleased = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var replica = new DelegatingLedgerReplica
        {
            Write = async (_, _, _, _, _) =>
            {
                writeStarted.SetResult();
                await writeReleased.Task;
                events.Enqueue("write ended");
                return ReplicaAnswers.Written;
            },
            Disposal = () =>
            {
                events.Enqueue("replica disposed");
                return ValueTask.CompletedTask;
            },
        };
        await using var log = WriterTestData.CreateLog(replica, windowSize: 1);
        try
        {
            var append = WriterTestData.AppendAsync(log, "value"u8.ToArray(), CancellationToken.None);
            await writeStarted.Task.WaitAsync(cancellationToken);

            var disposal = log.DisposeAsync().AsTask();
            Assert.Equal(WriterTestData.Unresolved(0), await append.WaitAsync(cancellationToken));

            // The replica's write ignores the cancellation, so the replica's disposal cannot end while it holds.
            Assert.False(disposal.IsCompleted);
            Assert.Empty(events);

            writeReleased.SetResult();
            await disposal.WaitAsync(cancellationToken);

            Assert.Equal(["write ended", "replica disposed"], events);
        }
        finally
        {
            _ = writeReleased.TrySetResult();
        }
    }

    /// <summary>
    /// ledger-protocol.md §2.3, §6.3: the writer's disposal stops its lifetime with
    /// <see cref="SequencerStopReason.Requested"/>; an append after its disposal throws
    /// <see cref="ObjectDisposedException"/> without touching the replicas, and a repeated disposal does nothing.
    /// </summary>
    [Fact]
    public async Task DisposalStopsTheLifetimeAndLaterCallsTouchNothing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var replica = new TailRecordingLedgerReplica(new DelegatingLedgerReplica());
        await using var log = WriterTestData.CreateLog(replica, windowSize: 1);

        await log.Writer.DisposeAsync();
        await log.Writer.DisposeAsync();
        _ = await Assert.ThrowsAsync<ObjectDisposedException>(
            () => WriterTestData.AppendAsync(log, "value"u8.ToArray(), cancellationToken));

        Assert.Equal([SequencerStopReason.Requested], log.StopReasons);
        Assert.Empty(replica.Operations);
    }

    /// <summary>
    /// ledgers.md §8.3, ledger-protocol.md §2.3: <c>ReadTailAsync</c> is the primary's open check. While the ledger is open
    /// it leaves the lifetime running, as another process's recovery does. Once that recovery has sealed the ledger, the
    /// check reports the seal and stops the lifetime with <see cref="SequencerStopReason.Sealing"/>, as a write which meets
    /// a seal does, so the primary's next append acquires no slot and writes nothing (LP-I14).
    /// </summary>
    [Theory]
    [InlineData(1, 1)]
    [InlineData(3, 2)]
    public async Task ASealCheckWhichFindsTheLedgerSealedStopsTheLifetimeInThisProcess(int replicas, int quorum)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var storage = new LedgerReplicas(replicas, windowSize: 4);
        await using var primary = WriterTestData.CreateLog(storage.OpenEach(), quorum, windowSize: 4);
        Assert.Equal(WriterTestData.Appended(0), await WriterTestData.AppendAsync(primary, "first"u8.ToArray(), cancellationToken));

        Assert.Equal(new LedgerTail(new Slot(1), IsSealed: false), (await primary.ReadTailAsync(cancellationToken)).Tail);
        Assert.Empty(primary.StopReasons);

        await using var executor = WriterTestData.CreateLedger(storage.OpenEach(), quorum, windowSize: 4);
        Assert.Equal(new Slot(1), (await executor.RecoverAsync(cancellationToken)).End);
        Assert.Empty(primary.StopReasons);

        Assert.Equal(new LedgerTail(new Slot(1), IsSealed: true), (await primary.ReadTailAsync(cancellationToken)).Tail);
        Assert.Equal([SequencerStopReason.Sealing], primary.StopReasons);
        Assert.Equal(
            WriterTestData.Unresolved(null),
            await WriterTestData.AppendAsync(primary, "second"u8.ToArray(), cancellationToken));
    }

    /// <summary>
    /// LP-I12, ledger-protocol.md §6.4: attempt 0 executes directly without arming retry timers on success or permanent
    /// failure, and enters bounded jittered backoff starting from the first retry step when attempt 0 meets
    /// <see cref="ReplicaWriteOutcome.Backpressure"/> or a transient failure.
    /// </summary>
    [Fact]
    public async Task WriteFastPath_BypassesRetryOnHappyPathAndPermanentFailureAndUsesExactBackoffOnFallback()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        // 1. Happy path: completes immediately in 1 attempt without advancing the fake clock (no retry timer armed).
        var fake = new FakeTimeProvider();
        var happyAttempts = 0;
        var happyReplica = new DelegatingLedgerReplica
        {
            Write = (_, _, _, _, _) =>
            {
                Interlocked.Increment(ref happyAttempts);
                return ValueTask.FromResult(ReplicaAnswers.Written);
            },
        };
        await using (var happy = WriterTestData.CreateLog(happyReplica, windowSize: 4, fake))
        {
            Assert.Equal(WriterTestData.Appended(0), await WriterTestData.AppendAsync(happy, "ok"u8.ToArray(), cancellationToken));
            Assert.Equal(1, Volatile.Read(ref happyAttempts));
        }

        // 2. Fallback on Backpressure then transient IOException: exact first-step backoff and completion on attempt 3.
        var fallbackFake = new FakeTimeProvider();
        var fallbackTime = new DelayRecordingTimeProvider(fallbackFake);
        var attempts = 0;
        var flakyReplica = new DelegatingLedgerReplica
        {
            Write = (_, _, _, _, _) => Interlocked.Increment(ref attempts) switch
            {
                1 => ValueTask.FromResult(ReplicaAnswers.Backpressure),
                2 => ValueTask.FromException<ReplicaWriteOutcome>(new IOException("transient outage")),
                _ => ValueTask.FromResult(ReplicaAnswers.Written),
            },
        };
        await using (var retryLog = WriterTestData.CreateLog(flakyReplica, windowSize: 4, fallbackTime))
        {
            var append = WriterTestData.AppendAsync(retryLog, "retry-me"u8.ToArray(), cancellationToken);
            Assert.Equal(
                WriterTestData.Appended(0),
                await WriterTestData.AdvanceUntilCompletedAsync(fallbackFake, append));
            Assert.Equal(3, Volatile.Read(ref attempts));
            // Two retry delays precede completion, plus the writer's contiguous-tail publish timer after completion.
            var retryDelays = fallbackTime.Delays.Take(2).ToArray();
            Assert.Equal(2, retryDelays.Length);
            Assert.All(retryDelays, delay => Assert.InRange(delay, StorageOutageRetry.BaseDelay, StorageOutageRetry.MaximumDelay));
        }

        // 3. Permanent failure on attempt 0: zero retry timers, immediate stop with PayloadWriteFailed.
        var permanentFake = new FakeTimeProvider();
        var permanentTime = new DelayRecordingTimeProvider(permanentFake);
        var permanentAttempts = 0;
        var permanentReplica = new DelegatingLedgerReplica
        {
            Write = (_, _, _, _, _) =>
            {
                Interlocked.Increment(ref permanentAttempts);
                return ValueTask.FromException<ReplicaWriteOutcome>(new InvalidDataException("permanent"));
            },
        };
        await using var permLog = WriterTestData.CreateLog(permanentReplica, windowSize: 4, permanentTime);
        Assert.Equal(WriterTestData.Unresolved(0), await WriterTestData.AppendAsync(permLog, "bad"u8.ToArray(), cancellationToken));
        Assert.Equal(1, Volatile.Read(ref permanentAttempts));
        Assert.Empty(permanentTime.Delays);
        Assert.Equal([SequencerStopReason.PayloadWriteFailed], permLog.StopReasons);
    }

    /// <summary>
    /// Records the delay of every one-shot timer created through it, which is how a retry pipeline waits.
    /// </summary>
    private sealed class DelayRecordingTimeProvider(FakeTimeProvider inner) : TimeProvider
    {
        private readonly ConcurrentQueue<TimeSpan> _delays = [];

        internal IReadOnlyCollection<TimeSpan> Delays => _delays;

        public override TimeZoneInfo LocalTimeZone => inner.LocalTimeZone;

        public override long TimestampFrequency => inner.TimestampFrequency;

        public override DateTimeOffset GetUtcNow() => inner.GetUtcNow();

        public override long GetTimestamp() => inner.GetTimestamp();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            if (period == Timeout.InfiniteTimeSpan)
            {
                _delays.Enqueue(dueTime);
            }

            return inner.CreateTimer(callback, state, dueTime, period);
        }
    }
}
