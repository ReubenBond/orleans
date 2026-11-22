using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Time.Testing;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Ledgers.Tests.Writer;

#pragma warning disable CA2000 // Each replica double passes to a process's Ledger, which owns and disposes it.

/// <summary>
/// Checks the known contiguous tail (LP-I11, ledger-protocol.md §8.2): a process's <see cref="Ledger"/> raises it from
/// its writer's acquisitions and completions, its contiguous-tail reads, its recoveries' <c>c</c>, and the recorded
/// <c>T</c>, never lowers it, never raises it past the ledger's contiguous tail, and carries it with every write, read,
/// contiguous-tail read, and seal (LR-I6).
/// </summary>
public sealed class LedgerKnownTailTests : IDisposable
{
    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    public void Dispose() => _leaks.Dispose();

    private const int WindowSize = 4;

    /// <summary>
    /// "Acquiring slot <c>p</c>: <c>max(0, p + 1 − K)</c>. At most <c>K</c> slots are in flight (LP-I2), so every
    /// position below <c>p + 1 − K</c> has completed" (ledger-protocol.md §8.2). Every write is held, so no completion raises
    /// the value; below <c>K − 1</c> the bound is clamped at zero rather than negative. The next contiguous-tail read
    /// carries it.
    /// </summary>
    [Theory]
    [InlineData(0L)]
    [InlineData(10L)]
    public async Task AcquiringSlotPRaisesTheKnownContiguousTailToPPlusOneMinusK(long firstPosition)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var storage = WriterTestData.CreateReplica(WindowSize);

        // The positions below firstPosition are the ledger's completed prefix: an earlier writer's DATA, whose slots the
        // lifetime granted and completed.
        for (var position = 0L; position < firstPosition; position++)
        {
            using var payload = WriterTestData.CreatePayload(Value(position));
            Assert.Equal(
                ReplicaAnswers.Written,
                await storage.WriteAsync(new Slot(position), payload, new Slot(position), recovery: false, cancellationToken));
        }

        var replica = new TailRecordingLedgerReplica(storage) { HoldWrites = true };
        await using var log = WriterTestData.CreateLog(replica, windowSize: WindowSize);
        await WriterTestData.AdvanceAsync(log.Sequencer, firstPosition, cancellationToken);
        var appends = new List<Task<AppendOutcome>>();

        for (var position = firstPosition; position < firstPosition + WindowSize; position++)
        {
            var append = WriterTestData.AppendAsync(log, Value(position), cancellationToken);
            appends.Add(append);
            await replica.WriteStartedAsync(position);
            Assert.Equal(new Slot(Math.Max(0, position + 1 - WindowSize)), log.KnownContiguousTail);
        }

        Assert.Equal(
            new LedgerTail(new Slot(firstPosition), IsSealed: false),
            (await log.ReadTailAsync(cancellationToken)).Tail);
        Assert.Equal([ReplicaOperation.TailRead(firstPosition)], replica.TailOperations());

        for (var position = firstPosition; position < firstPosition + WindowSize; position++)
        {
            replica.ReleaseWrite(position);
        }

        var results = new List<AppendOutcome>();
        foreach (var append in appends)
        {
            results.Add(await append);
        }

        Assert.Equal(
            Enumerable.Range(0, WindowSize).Select(index => WriterTestData.Appended(firstPosition + index)),
            results);
        Assert.Equal(new Slot(firstPosition + WindowSize), log.KnownContiguousTail);
    }

    /// <summary>
    /// "Completing slot <c>p</c>: <c>p + 1</c>. Completion is in order (LP-I3), and a slot completes only after its
    /// create reported <c>Written</c>" (ledger-protocol.md §8.2). DATA which lands above a hole raises nothing until the hole
    /// completes.
    /// </summary>
    [Fact]
    public async Task CompletingSlotPRaisesTheKnownContiguousTailToPPlusOne()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var storage = WriterTestData.CreateReplica(WindowSize);
        var replica = new TailRecordingLedgerReplica(storage) { HoldWrites = true };
        await using var log = WriterTestData.CreateLog(replica, windowSize: WindowSize);
        var first = WriterTestData.AppendAsync(log, Value(0), cancellationToken);
        await replica.WriteStartedAsync(0);
        var second = WriterTestData.AppendAsync(log, Value(1), cancellationToken);
        await replica.WriteStartedAsync(1);
        var third = WriterTestData.AppendAsync(log, Value(2), cancellationToken);
        await replica.WriteStartedAsync(2);
        Assert.Equal(Slot.Zero, log.KnownContiguousTail);

        replica.ReleaseWrite(0);
        Assert.Equal(WriterTestData.Appended(0), await first);
        Assert.Equal(new Slot(1), log.KnownContiguousTail);

        // Slot 2's DATA lands above the hole at 1, so its completion waits for slot 1.
        replica.ReleaseWrite(2);
        await replica.WriteLandedAsync(2);
        Assert.False(third.IsCompleted);
        Assert.Equal(new Slot(1), log.KnownContiguousTail);

        replica.ReleaseWrite(1);
        Assert.Equal(WriterTestData.Appended(1), await second);
        Assert.Equal(WriterTestData.Appended(2), await third);
        Assert.Equal(new Slot(3), log.KnownContiguousTail);
    }

    /// <summary>
    /// LP-I12, LP-I11: "Each attempt carries the known contiguous tail when it starts; only the payload is identical"
    /// (ledger-protocol.md §8.1). A write which fails while a lower append completes is repeated with the tail that completion
    /// raised, and every attempt is a writer write.
    /// </summary>
    [Fact]
    public async Task EachRepeatedWriteCarriesTheKnownContiguousTailCurrentWhenItStarts()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var time = new FakeTimeProvider();
        await using var storage = WriterTestData.CreateReplica(windowSize: 2);
        var flaky = new FlakyLedgerReplica(storage);
        flaky.FailNextCreates(new Slot(1), CreateFault.NotLanded);
        var replica = new TailRecordingLedgerReplica(flaky) { HoldWrites = true };
        await using var log = WriterTestData.CreateLog(replica, windowSize: 2, time);
        var first = WriterTestData.AppendAsync(log, Value(0), cancellationToken);
        await replica.WriteStartedAsync(0);
        var second = WriterTestData.AppendAsync(log, Value(1), cancellationToken);
        await replica.WriteStartedAsync(1);

        // Slot 1's first attempt fails, and it waits out the outage on the injected clock while slot 0 completes.
        replica.ReleaseWrite(1);
        await WriterTestData.WaitUntilAsync(() => flaky.Creates.Any(static create => create.Address.Value == 1));
        replica.ReleaseWrite(0);
        Assert.Equal(WriterTestData.Appended(0), await first);
        Assert.Equal(new Slot(1), log.KnownContiguousTail);

        Assert.Equal(
            WriterTestData.Appended(1),
            await WriterTestData.AdvanceUntilCompletedAsync(time, second));
        Assert.Equal(
            [0L, 1L],
            flaky.Creates.Where(static create => create.Address.Value == 1).Select(static create => create.ContiguousTail.Value));
        Assert.All(flaky.Creates, static create => Assert.False(create.Recovery));
    }

    /// <summary>
    /// LP-I11, LR-I6: "A replica's <c>ReadTailAsync</c> contiguous tail ... Retained values are known contiguous tails"
    /// (ledger-protocol.md §8.2). Each contiguous-tail read raises the known contiguous tail to the replica's report, and the
    /// next contiguous-tail read or seal carries it.
    /// </summary>
    [Fact]
    public async Task ReadTailRaisesTheKnownContiguousTailAndCarriesIt()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var answers = new ConcurrentQueue<LedgerReplicaTail>(
        [
            new LedgerReplicaTail(new Slot(5), IsSealed: false),
            new LedgerReplicaTail(new Slot(9), IsSealed: true),
        ]);
        var replica = new TailRecordingLedgerReplica(new DelegatingLedgerReplica
        {
            ReadTail = (contiguousTail, seal, _) =>
            {
                if (seal)
                {
                    return ValueTask.FromResult<ReplicaTailRead>(new LedgerReplicaTail(contiguousTail, IsSealed: true));
                }

                return answers.TryDequeue(out var answer)
                    ? ValueTask.FromResult<ReplicaTailRead>(answer)
                    : throw new InvalidOperationException("No answer remains.");
            },
        });
        await using var log = WriterTestData.CreateLog(replica, windowSize: WindowSize);

        Assert.Equal(new LedgerTail(new Slot(5), IsSealed: false), (await log.ReadTailAsync(cancellationToken)).Tail);
        Assert.Equal(new Slot(5), log.KnownContiguousTail);
        Assert.Equal(new LedgerTail(new Slot(9), IsSealed: true), (await log.ReadTailAsync(cancellationToken)).Tail);
        Assert.Equal(new Slot(9), log.KnownContiguousTail);
        Assert.Equal(new Slot(9), (await log.RecoverAsync(cancellationToken)).End);

        Assert.Equal(
            [ReplicaOperation.TailRead(0), ReplicaOperation.TailRead(5), ReplicaOperation.SealWith(9)],
            replica.TailOperations());
    }

    /// <summary>
    /// ledger-protocol.md §2.1: a read, ranged or not, carries its process's known contiguous tail (LP-I11), and no read
    /// raises it: the primary's reads carry its completed prefix, while a reader which has learned nothing carries zero.
    /// </summary>
    [Fact]
    public async Task ReadsCarryTheKnownContiguousTail()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var storage = WriterTestData.CreateReplica(WindowSize);
        var primaryReplica = new TailRecordingLedgerReplica(storage);
        await using var primary = WriterTestData.CreateLog(primaryReplica, windowSize: WindowSize);
        for (var position = 0; position < 3; position++)
        {
            Assert.Equal(WriterTestData.Appended(position), await WriterTestData.AppendAsync(primary, Value(position), cancellationToken));
        }

        var readerReplica = new TailRecordingLedgerReplica(storage);
        await using var reader = WriterTestData.CreateLedger([readerReplica], quorum: 1, WindowSize);

        var known = await primary.ReadAsync(new Slot(1), cancellationToken);
        known.Batch.Dispose();
        var range = await primary.ReadAsync(new Slot(2), 0, 1, cancellationToken);
        range.Batch.Dispose();
        var unknown = await reader.ReadAsync(new Slot(1), cancellationToken);
        unknown.Batch.Dispose();

        Assert.Equal(
            [ReplicaOperation.ReadAt(1, 3), ReplicaOperation.ReadAt(2, 3)],
            primaryReplica.Operations.Where(static operation => operation.Kind == TailRecordingLedgerReplica.Read));
        Assert.Equal([ReplicaOperation.ReadAt(1, 0)], readerReplica.Operations);
        Assert.Equal(Slot.Zero, reader.KnownContiguousTail);
    }

    /// <summary>
    /// LP-I11 under contention: contiguous-tail reads which run at once each raise the known contiguous tail to the tail
    /// they learn, so once they finish it holds the highest of them. A raise which loses a race to a lower one retries
    /// rather than leaving the lower value.
    /// </summary>
    [Fact]
    public async Task ConcurrentTailReadsLeaveTheHighestTailTheyLearn()
    {
        const int Rounds = 1_000;
        var cancellationToken = TestContext.Current.CancellationToken;
        var checkers = Math.Clamp(Environment.ProcessorCount, 2, 8);
        using var learned = new ThreadLocal<long>();
        var logs = new PrimaryLedger[Rounds];
        var node = NodeId.New();
        var nodes = new LedgerLocation.Nodes([node]);
        for (var round = 0; round < Rounds; round++)
        {
            var replica = new DelegatingLedgerReplica
            {
                ReadTail = (_, _, _) => ValueTask.FromResult<ReplicaTailRead>(
                    new LedgerReplicaTail(new Slot(learned.Value), IsSealed: false)),
            };
            var ledger = new Ledger(
                WriterTestData.Id,
                LedgerQuorum.ForNodes(WriterTestData.Id, nodes.Replicas, [replica], quorum: 1, new LedgerReplicaOptions { WindowSize = WindowSize }, TimeProvider.System, localNode: node),
                nodes,
                TimeProvider.System);
            logs[round] = new PrimaryLedger(ledger, TimeProvider.System);
        }

        try
        {
            var checks = new ConcurrentQueue<Task<Slot>>();
            var startAt = 0L;
            using var ready = new Barrier(checkers + 1);
            var threads = Enumerable.Range(1, checkers)
                .Select(checker => new Thread(() =>
                {
                    // Checker c learns tail c, so the highest any learns is the number of checkers.
                    learned.Value = checker;
                    foreach (var log in logs)
                    {
                        ready.SignalAndWait();
                        var start = Volatile.Read(ref startAt);
                        while (Stopwatch.GetTimestamp() < start)
                        {
                            Thread.SpinWait(1);
                        }

                        checks.Enqueue(log.Ledger.LearnTailAsync(cancellationToken).AsTask());
                    }
                }))
                .ToArray();
            foreach (var thread in threads)
            {
                thread.Start();
            }

            // Every checker starts a round's contiguous-tail read at the same instant, so their raises race.
            for (var round = 0; round < Rounds; round++)
            {
                Volatile.Write(ref startAt, Stopwatch.GetTimestamp() + Stopwatch.Frequency / 10_000);
                ready.SignalAndWait(cancellationToken);
            }

            foreach (var thread in threads)
            {
                thread.Join();
            }

            _ = await Task.WhenAll(checks);
            foreach (var log in logs)
            {
                Assert.Equal(new Slot(checkers), log.KnownContiguousTail);
            }
        }
        finally
        {
            foreach (var log in logs)
            {
                await log.DisposeAsync();
            }
        }
    }

    /// <summary>
    /// "A primary which completes every append before it seals its own ledger recovers with the exact contiguous tail,
    /// and recovery ends at its first read" (ledger-protocol.md §8.2): its completions alone raise the value to the number of
    /// appends, which its seal carries and which is the candidate end.
    /// </summary>
    [Fact]
    public async Task APrimaryWhichCompletedEveryAppendSealsWithItsExactContiguousTail()
    {
        const int appendCount = 10;
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var storage = WriterTestData.CreateReplica(WindowSize);
        var replica = new TailRecordingLedgerReplica(storage);
        await using var log = WriterTestData.CreateLog(replica, windowSize: WindowSize);

        var results = await Task.WhenAll(
            Enumerable.Range(0, appendCount).Select(index => WriterTestData.AppendAsync(log, Value(index), cancellationToken)));
        Assert.Equal(new Slot(appendCount), (await log.RecoverAsync(cancellationToken)).End);

        Assert.True(Enumerable.Range(0, appendCount).Select(static index => WriterTestData.Appended(index)).ToHashSet().SetEquals(results));
        Assert.Equal([ReplicaOperation.SealWith(appendCount)], replica.TailOperations());
        Assert.Equal(
            ReplicaOperation.ReadAt(appendCount, appendCount),
            replica.Operations.First(static operation => operation.Kind == TailRecordingLedgerReplica.Read));
        Assert.Equal(
            new LedgerTail(new Slot(appendCount), IsSealed: true),
            (await log.ReadTailAsync(cancellationToken)).Tail);
        Assert.Equal(new Slot(appendCount), log.KnownContiguousTail);
    }

    /// <summary>
    /// LP-I11: a recovery raises the known contiguous tail to its <c>c</c>, the greatest contiguous tail among its first
    /// <c>N − Q + 1</c> seal answers and its own, below which every slot holds a batch (LR-I6, LP-I7), and never to its
    /// candidate end <c>T</c>, which no process knows to be the ledger's end until the map records it. A recovery in a
    /// process whose own value is higher leaves it unchanged.
    /// </summary>
    [Fact]
    public async Task ARecoveryRaisesTheKnownContiguousTailToItsCAndNeverToItsEnd()
    {
        const int Appended = 5;
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var storage = WriterTestData.CreateReplica(WindowSize);
        await using var primary = WriterTestData.CreateLog(new TailRecordingLedgerReplica(storage), windowSize: WindowSize);
        for (var position = 0; position < Appended; position++)
        {
            Assert.Equal(WriterTestData.Appended(position), await WriterTestData.AppendAsync(primary, Value(position), cancellationToken));
        }

        // The write of slot p carried p, so the replica retains Appended - 1, which recovery takes as c.
        await using var reader = WriterTestData.CreateLedger([new TailRecordingLedgerReplica(storage)], quorum: 1, WindowSize);

        Assert.Equal(new Slot(Appended), (await reader.RecoverAsync(cancellationToken)).End);

        Assert.Equal(new Slot(Appended - 1), reader.KnownContiguousTail);
        Assert.Equal(
            new LedgerTail(new Slot(Appended - 1), IsSealed: true),
            (await reader.ReadTailAsync(cancellationToken)).Tail);

        var primaryKnown = primary.KnownContiguousTail;
        Assert.Equal(new Slot(Appended), primaryKnown);
        Assert.Equal(new Slot(Appended), (await primary.RecoverAsync(cancellationToken)).End);
        Assert.Equal(primaryKnown, primary.KnownContiguousTail);
    }

    /// <summary>
    /// "The ledger map's recorded <c>T</c>: <c>T</c>. Every position below it holds DATA on <c>Q</c> replicas, and the
    /// map records one candidate end for the ledger's life" (ledger-protocol.md §8.2). An executor in another process which
    /// sealed the ledger knows only its recovery's <c>c</c>; once it observes the recorded <c>T</c>, its known contiguous
    /// tail is <c>T</c>, and every later contiguous-tail read, read, and recovery carries it. Observing it again changes
    /// nothing.
    /// </summary>
    [Fact]
    public async Task ObservingTheRecordedTailRaisesTheKnownContiguousTailAndLaterOperationsCarryIt()
    {
        const int Appended = 5;
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var storage = WriterTestData.CreateReplica(WindowSize);
        await using var primary = WriterTestData.CreateLog(new TailRecordingLedgerReplica(storage), windowSize: WindowSize);
        for (var position = 0; position < Appended; position++)
        {
            Assert.Equal(WriterTestData.Appended(position), await WriterTestData.AppendAsync(primary, Value(position), cancellationToken));
        }

        var executorReplica = new TailRecordingLedgerReplica(storage);
        await using var executor = WriterTestData.CreateLedger([executorReplica], quorum: 1, WindowSize);
        var recorded = (await executor.RecoverAsync(cancellationToken)).End;
        Assert.Equal(new Slot(Appended), recorded);
        Assert.Equal(new Slot(Appended - 1), executor.KnownContiguousTail);

        executor.ObserveRecordedTail(recorded);
        executor.ObserveRecordedTail(recorded);

        Assert.Equal(recorded, executor.KnownContiguousTail);
        var before = executorReplica.Operations.Count;
        Assert.Equal(new LedgerTail(recorded, IsSealed: true), (await executor.ReadTailAsync(cancellationToken)).Tail);
        var read = await executor.ReadAsync(new Slot(2), cancellationToken);
        using (var bytes = read.Batch)
        {
            Assert.Equal(Value(2), bytes.ToArray());
        }

        Assert.Equal(recorded, (await executor.RecoverAsync(cancellationToken)).End);
        var after = executorReplica.Operations.Skip(before).ToArray();
        Assert.Equal(
            [ReplicaOperation.TailRead(Appended), ReplicaOperation.ReadAt(2, Appended), ReplicaOperation.SealWith(Appended), ReplicaOperation.ReadAt(Appended, Appended)],
            after[..4]);

        // Recovery reads ahead within its window from the recorded T, and every read carries it.
        Assert.All(after[4..], static operation => Assert.Equal((TailRecordingLedgerReplica.Read, Appended), (operation.Kind, operation.ContiguousTail)));
    }

    /// <summary>
    /// LP-I11: "Every position below it holds DATA its replica decided, so it never exceeds the recorded <c>T</c>." A
    /// sealer in another process recovers the end <c>T</c> while two of the primary's slots are in flight, the higher of
    /// which already landed. Its DATA lies outside the ledger and never completes, the slot at <c>T</c> meets the seal
    /// (LP-I8), and neither process's known contiguous tail nor any value it carries passes <c>T</c>.
    /// </summary>
    [Fact]
    public async Task TheKnownContiguousTailNeverExceedsTheRecoveredEnd()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var storage = WriterTestData.CreateReplica(WindowSize);
        var primaryReplica = new TailRecordingLedgerReplica(storage) { HoldWrites = true };
        await using var primary = WriterTestData.CreateLog(primaryReplica, windowSize: WindowSize);
        var sealerReplica = new TailRecordingLedgerReplica(storage);
        await using var sealer = WriterTestData.CreateLedger([sealerReplica], quorum: 1, WindowSize);
        for (var position = 0; position < 3; position++)
        {
            var append = WriterTestData.AppendAsync(primary, Value(position), cancellationToken);
            primaryReplica.ReleaseWrite(position);
            Assert.Equal(WriterTestData.Appended(position), await append);
        }

        var atEnd = WriterTestData.AppendAsync(primary, Value(3), cancellationToken);
        var aboveEnd = WriterTestData.AppendAsync(primary, Value(4), cancellationToken);
        await primaryReplica.WriteStartedAsync(3);
        await primaryReplica.WriteStartedAsync(4);
        primaryReplica.ReleaseWrite(4);
        await primaryReplica.WriteLandedAsync(4);

        var end = (await sealer.RecoverAsync(cancellationToken)).End;
        Assert.Equal(new Slot(3), end);
        Assert.InRange(sealer.KnownContiguousTail.Value, 0, end.Value);
        Assert.Equal(new LedgerTail(end, IsSealed: true), (await sealer.ReadTailAsync(cancellationToken)).Tail);
        Assert.Equal(end, sealer.KnownContiguousTail);

        primaryReplica.ReleaseWrite(3);

        Assert.Equal(WriterTestData.Sealed(end.Value), await atEnd);
        Assert.Equal(WriterTestData.Unresolved(4), await aboveEnd);
        Assert.Equal(end, primary.KnownContiguousTail);
        Assert.Equal(new LedgerTail(end, IsSealed: true), (await primary.ReadTailAsync(cancellationToken)).Tail);
        Assert.Equal(end, (await primary.RecoverAsync(cancellationToken)).End);
        Assert.Equal(end, primary.KnownContiguousTail);
        Assert.All(
            primaryReplica.Operations.Concat(sealerReplica.Operations),
            operation => Assert.InRange(operation.ContiguousTail, 0, end.Value));
    }

    /// <summary>
    /// LP-I11: the known contiguous tail only rises and never passes the ledger's contiguous tail, over seeded
    /// interleavings of appends, writes which land out of order, and contiguous-tail reads whose answers arrive after
    /// completions have raised the value past them. Each contiguous-tail read answers a tail its replica could retain: at
    /// least the value it carried and at most the contiguous prefix of landed DATA (LR-I6). Every step waits until the
    /// appends it decides have returned, so each seed replays identically.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public Task TheKnownContiguousTailNeverDecreases(int seed) =>
        SeededSimulation.RunAndReplayAsync(seed, SimulateInterleavingsAsync);

    private static async Task SimulateInterleavingsAsync(SimulationRun run)
    {
        const int steps = 200;
        var cancellationToken = TestContext.Current.CancellationToken;
        var landed = new ConcurrentDictionary<long, bool>();
        var pendingTailReads = new ConcurrentQueue<PendingTailRead>();
        var replica = new TailRecordingLedgerReplica(new DelegatingLedgerReplica
        {
            Write = (address, _, _, _, _) =>
            {
                landed[address.Value] = true;
                return ValueTask.FromResult(ReplicaAnswers.Written);
            },
            ReadTail = (contiguousTail, _, _) =>
            {
                var pending = new PendingTailRead(contiguousTail.Value);
                pendingTailReads.Enqueue(pending);
                return new ValueTask<ReplicaTailRead>(pending.Answer.Task);
            },
        })
        {
            HoldWrites = true,
        };
        await using var log = WriterTestData.CreateLog(replica, windowSize: WindowSize);
        var appends = new List<Task<AppendOutcome>>();
        var completed = 0;
        var held = new List<long>();
        var tailReads = new List<(Task<LedgerTailRead> Read, PendingTailRead Pending)>();
        var known = 0L;
        var staleAnswers = 0;

        long LandedPrefix()
        {
            var prefix = 0L;
            while (landed.ContainsKey(prefix))
            {
                prefix++;
            }

            return prefix;
        }

        async Task CompleteLandedPrefixAsync()
        {
            for (var prefix = LandedPrefix(); completed < prefix; completed++)
            {
                Assert.Equal(WriterTestData.Appended(completed), await appends[completed]);
            }
        }

        // A contiguous-tail read reports the known contiguous tail after it learns from the answer, which an answer
        // below the value raised since the read began cannot lower.
        async Task AnswerAsync(int index, long answer)
        {
            var (read, pending) = tailReads[index];
            tailReads.RemoveAt(index);
            var expected = Math.Max(answer, log.KnownContiguousTail.Value);
            _ = pending.Answer.TrySetResult(new LedgerReplicaTail(new Slot(answer), IsSealed: false));
            Assert.Equal(new LedgerTail(new Slot(expected), IsSealed: false), (await read).Tail);
        }

        for (var step = 0; step < steps; step++)
        {
            // The sequencer's completed prefix is the landed prefix once every step's completions have returned.
            List<string> enabled = [];
            if (appends.Count - completed < WindowSize)
            {
                enabled.Add("append");
            }

            if (held.Count > 0)
            {
                enabled.Add("land");
            }

            if (tailReads.Count == 0)
            {
                enabled.Add("readTail");
            }

            if (tailReads.Count > 0)
            {
                enabled.Add("answer");
            }

            string entry;
            switch (enabled[run.Random.Next(enabled.Count)])
            {
                case "append":
                    {
                        var position = (long)appends.Count;
                        var append = WriterTestData.AppendAsync(log, Value(position), cancellationToken);
                        appends.Add(append);
                        await replica.WriteStartedAsync(position);
                        held.Add(position);
                        entry = string.Create(CultureInfo.InvariantCulture, $"append {position}");
                        break;
                    }

                case "land":
                    {
                        var index = run.Random.Next(held.Count);
                        var position = held[index];
                        held.RemoveAt(index);
                        replica.ReleaseWrite(position);
                        await replica.WriteLandedAsync(position);
                        await CompleteLandedPrefixAsync();
                        entry = string.Create(CultureInfo.InvariantCulture, $"land {position}");
                        break;
                    }

                case "readTail":
                    {
                        var read = ReadTailInBackground(log, cancellationToken);
                        await WriterTestData.WaitUntilAsync(() => !pendingTailReads.IsEmpty);
                        Assert.True(pendingTailReads.TryDequeue(out var pending));
                        Assert.Equal(known, pending.ContiguousTail);
                        tailReads.Add((read, pending));
                        entry = string.Create(CultureInfo.InvariantCulture, $"readTail carries {pending.ContiguousTail}");
                        break;
                    }

                default:
                    {
                        var index = run.Random.Next(tailReads.Count);
                        var bound = tailReads[index].Pending.ContiguousTail;
                        var answer = bound + run.Random.NextInt64(LandedPrefix() - bound + 1);
                        if (answer < known)
                        {
                            staleAnswers++;
                        }

                        await AnswerAsync(index, answer);
                        entry = string.Create(CultureInfo.InvariantCulture, $"answer {answer} to the read which carried {bound}");
                        break;
                    }
            }

            var current = log.KnownContiguousTail.Value;
            Assert.True(current >= known, string.Create(CultureInfo.InvariantCulture, $"After '{entry}', the known contiguous tail fell from {known} to {current}."));
            Assert.True(current <= LandedPrefix(), string.Create(CultureInfo.InvariantCulture, $"After '{entry}', the known contiguous tail {current} passed the landed prefix {LandedPrefix()}."));
            known = current;
            run.Record(string.Create(CultureInfo.InvariantCulture, $"{entry}; known {known}"));
        }

        foreach (var position in held.Order())
        {
            replica.ReleaseWrite(position);
        }

        var slot = 0L;
        foreach (var append in appends)
        {
            Assert.Equal(WriterTestData.Appended(slot++), await append);
        }

        while (tailReads.Count > 0)
        {
            await AnswerAsync(0, tailReads[0].Pending.ContiguousTail);
        }

        Assert.Equal(new Slot(appends.Count), log.KnownContiguousTail);

        // The run exercised the race the property guards: a contiguous-tail read answered below a value raised since it
        // started.
        Assert.NotEqual(0, staleAnswers);
    }

    private static byte[] Value(long position) => BitConverter.GetBytes(position);

    private static Task<LedgerTailRead> ReadTailInBackground(PrimaryLedger log, CancellationToken cancellationToken) =>
        log.ReadTailAsync(cancellationToken).AsTask();

    private sealed class PendingTailRead(long contiguousTail)
    {
        internal long ContiguousTail { get; } = contiguousTail;

        internal TaskCompletionSource<ReplicaTailRead> Answer { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
