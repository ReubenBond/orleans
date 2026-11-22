using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Ledgers.Memory;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Ledgers.Tests;

/// <summary>
/// Checks the <see cref="LedgerQuorum"/>'s writes, reads, seal checks, and recoveries over replica doubles and memory
/// replicas (ledger-protocol.md §8.3): writes at <c>Q</c> (LP-I4), dropped answers as sealed ones (LP-I5), the contiguous
/// tail each operation carries (LP-I11), the seal over <c>N − Q + 1</c> (LP-I6), and the seal check over <c>Q</c> (LP-I9).
/// </summary>
public sealed class LedgerQuorumTests : IDisposable
{
    private static readonly LedgerReplicaOptions Window = new() { WindowSize = 8 };

    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    public void Dispose() => _leaks.Dispose();

    /// <summary>
    /// ledger-replicas.md §3.2: an empty payload is the caller's error, which the composition rejects as an argument before any
    /// replica sees it.
    /// </summary>
    [Fact]
    public async Task AnEmptyPayloadIsRejectedWithoutReachingAReplica()
    {
        var replicas = Replicas(3);
        await using var quorum = LedgerQuorumTestData.Create(replicas, 2, Window);
        using var writer = new ArcBufferWriter(0);
        using var empty = writer.PeekSlice(0);
        var token = TestContext.Current.CancellationToken;

        _ = await Assert.ThrowsAsync<ArgumentException>(async () => await quorum.WriteAsync(Slot.Zero, empty, token));

        Assert.All(replicas, static replica => Assert.Empty(replica.WriteCalls));
    }

    /// <summary>
    /// LP-I4: "On a write, the QuorumDrive forwards the command to all replicas" sharing one zero-copy retained lease
    /// of the payload (conventions.md §1, BUF-I4), and the write is written once <c>Q</c> replicas answer written,
    /// keeping the payload pinned until every straggler ends.
    /// </summary>
    [Fact]
    public async Task AWriteFansOutAStableSnapshotAndIsWrittenAtQWrittenAnswers()
    {
        var gates = Enumerable.Range(0, 3)
            .Select(static _ => new TaskCompletionSource<ReplicaWriteOutcome>(TaskCreationOptions.RunContinuationsAsynchronously))
            .ToArray();
        var replicas = Replicas(3);
        ArcBuffer stragglerBuffer = default;
        for (var index = 0; index < replicas.Length; index++)
        {
            var replicaIndex = index;
            var gate = gates[index];
            replicas[index].WriteAsyncHandler = (_, buffer, _, _) =>
            {
                if (replicaIndex == 2)
                {
                    stragglerBuffer = buffer;
                }

                return new ValueTask<ReplicaWriteOutcome>(gate.Task);
            };
        }

        var quorum = LedgerQuorumTestData.Create(replicas, 2, Window);
        var writer = new ArcBufferWriter();
        var expected = LedgerReplicaTestData.CreatePayload(ArcBufferWriter.MinimumPageSize * 3 + 47);
        writer.Write(expected);
        var payload = writer.PeekSlice(writer.Length);
        var pages = payload.Pages.ToArray();
        var sourceArrays = payload.ArraySegments.Select(static ledger => ledger.Array).ToArray();
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        var operation = quorum.WriteAsync(new Slot(41), payload, caller.Token).AsTask();

        Assert.False(operation.IsCompleted);
        var callToken = replicas[0].WriteCalls[0].CancellationToken;
        Assert.All(
            replicas,
            replica =>
            {
                var call = Assert.Single(replica.WriteCalls);
                Assert.Equal(41, call.Address.Value);
                Assert.Equal(expected, call.Payload);
                Assert.False(call.Recovery);

                // ledger-protocol.md §6.3: one token, which the caller's cancellation and the quorum's disposal both reach.
                Assert.Equal(callToken, call.CancellationToken);

                // conventions.md §1, ledger-protocol.md §8.3: zero-copy sharing of one retained lease across replicas.
                Assert.All(
                    sourceArrays.Zip(call.BackingArrays),
                    pair => Assert.Same(pair.First, pair.Second));
            });

        _ = gates[0].TrySetResult(ReplicaAnswers.Written);
        Assert.False(operation.IsCompleted);
        _ = gates[1].TrySetResult(ReplicaAnswers.Written);

        Assert.Equal(ReplicaWriteCase.Written, (await operation).Case);

        // Even after the caller disposes payload and writer, the quorum's retained lease keeps the pages pinned and
        // unpoisoned until the straggler finishes (BUF-I4).
        payload.Dispose();
        writer.Dispose();
        CompositeLedgerReplicaTestAssertions.ReferenceCounts(pages, 1);
        Assert.Equal(expected, stragglerBuffer.ToArray());

        // The straggler keeps the token, which the caller's cancellation reaches after the write answered.
        Assert.False(callToken.IsCancellationRequested);
        await caller.CancelAsync();
        Assert.True(callToken.IsCancellationRequested);

        _ = gates[2].TrySetResult(ReplicaAnswers.Written);
        await quorum.DisposeAsync();
        CompositeLedgerReplicaTestAssertions.ReferenceCounts(pages, 0);
    }

    /// <summary>
    /// LP-I4: a writer write reports sealed at the first sealed answer while fewer than <c>Q</c> replicas answered
    /// written, so a sealed answer never counts toward the quorum; once <c>Q</c> answered written first, it is written.
    /// </summary>
    [Theory]
    [InlineData(new[] { ReplicaWriteCase.Sealed, ReplicaWriteCase.Written, ReplicaWriteCase.Written }, ReplicaWriteCase.Sealed)]
    [InlineData(new[] { ReplicaWriteCase.Written, ReplicaWriteCase.Sealed, ReplicaWriteCase.Written }, ReplicaWriteCase.Sealed)]
    [InlineData(new[] { ReplicaWriteCase.Written, ReplicaWriteCase.Written, ReplicaWriteCase.Sealed }, ReplicaWriteCase.Written)]
    public async Task AWriterWriteIsSealedAtTheFirstSealedAnswerBeforeQWritten(ReplicaWriteCase[] answers, ReplicaWriteCase expected)
    {
        // The replicas answer one at a time, in order, so the quorum meets the answers in that order.
        var replicas = Replicas(3);
        var gates = answers.Select(static _ => new TaskCompletionSource<ReplicaWriteOutcome>(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        for (var index = 0; index < replicas.Length; index++)
        {
            var gate = gates[index];
            replicas[index].WriteAsyncHandler = (_, _, _, _) => new ValueTask<ReplicaWriteOutcome>(gate.Task);
        }

        await using var quorum = LedgerQuorumTestData.Create(replicas, 2, Window);
        using var payload = LedgerReplicaTestData.Buffer("value"u8);
        var write = quorum.WriteAsync(new Slot(3), payload, TestContext.Current.CancellationToken).AsTask();
        for (var index = 0; index < answers.Length && !write.IsCompleted; index++)
        {
            _ = gates[index].TrySetResult(answers[index].ToOutcome());
            _ = await Task.WhenAny(write, Task.Delay(TimeSpan.FromMilliseconds(20), TimeProvider.System, TestContext.Current.CancellationToken));
        }

        Assert.Equal(expected, (await write).Case);
        foreach (var gate in gates)
        {
            _ = gate.TrySetResult(ReplicaAnswers.Written);
        }
    }

    /// <summary>
    /// LP-I5: a writer write counts a dropped answer as a sealed one.
    /// </summary>
    [Fact]
    public async Task AWriterWriteCountsADroppedAnswerAsSealed()
    {
        var token = TestContext.Current.CancellationToken;
        using var payload = LedgerReplicaTestData.Buffer("value"u8);
        var writer = Replicas(3);
        writer[0].WriteAsyncHandler = (_, _, _, _) => ValueTask.FromResult(ReplicaAnswers.Dropped);
        await using var quorum = LedgerQuorumTestData.Create(writer, 2, Window);

        Assert.Equal(ReplicaWriteCase.Sealed, (await quorum.WriteAsync(new Slot(0), payload, token)).Case);
    }

    /// <summary>
    /// LP-I4: a write which can no longer reach <c>Q</c> written answers fails with the replicas' failures.
    /// </summary>
    [Fact]
    public async Task AWriteWhichCannotReachQFailsWithTheReplicaFailures()
    {
        var first = new InvalidOperationException("durability lost");
        var second = new IOException("unreachable");
        var replicas = Replicas(3);
        replicas[0].WriteAsyncHandler = (_, _, _, _) => ValueTask.FromException<ReplicaWriteOutcome>(first);
        replicas[2].WriteAsyncHandler = (_, _, _, _) => ValueTask.FromException<ReplicaWriteOutcome>(second);
        await using var quorum = LedgerQuorumTestData.Create(replicas, 2, Window);
        using var payload = LedgerReplicaTestData.Buffer("payload"u8);

        var error = await Assert.ThrowsAsync<AggregateException>(
            async () => await quorum.WriteAsync(new Slot(3), payload, TestContext.Current.CancellationToken));

        Assert.Equal(new Exception[] { first, second }, error.InnerExceptions);
    }

    /// <summary>
    /// ledger-protocol.md §6.4, LP-I4, LP-I9: a replica's backpressure is no answer, so a write, a read, and a seal check
    /// which cannot reach <c>Q</c> answers answer <c>Backpressure</c> when a replica answered it, while ones which reach
    /// <c>Q</c> beside a replica's backpressure decide without it.
    /// </summary>
    [Fact]
    public async Task BackpressureAloneKeepsTheQuorumFromDeciding()
    {
        var token = TestContext.Current.CancellationToken;
        using var payload = LedgerReplicaTestData.Buffer("payload"u8);
        var blocked = Replicas(3);
        foreach (var replica in blocked.Take(2))
        {
            Backpressure(replica);
        }

        await using (var quorum = LedgerQuorumTestData.Create(blocked, 2, Window))
        {
            Assert.Equal(ReplicaWriteCase.Backpressure, (await quorum.WriteAsync(new Slot(3), payload, token)).Case);
            Assert.Equal(ReplicaReadCase.Backpressure, (await quorum.ReadAsync(new Slot(3), token)).Case);
            Assert.Equal(new LedgerTailRead(ReplicaWriteOutcome.Backpressure.Instance), await quorum.CheckAsync(token));
        }

        var decided = Replicas(3);
        Backpressure(decided[0]);
        await using (var quorum = LedgerQuorumTestData.Create(decided, 2, Window))
        {
            Assert.Equal(ReplicaWriteCase.Written, (await quorum.WriteAsync(new Slot(3), payload, token)).Case);
            Assert.Equal(ReplicaReadCase.Missing, (await quorum.ReadAsync(new Slot(3), token)).Case);
            Assert.Equal(new LedgerTail(Slot.Zero, IsSealed: false), (await quorum.CheckAsync(token)).Tail);
        }

        static void Backpressure(RecordingLedgerReplica replica)
        {
            replica.WriteAsyncHandler = static (_, _, _, _) => ValueTask.FromResult(ReplicaAnswers.Backpressure);
            replica.ReadAsyncHandler = static (_, _) => ValueTask.FromResult<ReplicaRead>(ReplicaWriteOutcome.Backpressure.Instance);
            replica.ReadTailAsyncHandler = static (_, _, _) => ValueTask.FromResult<ReplicaTailRead>(ReplicaWriteOutcome.Backpressure.Instance);
        }
    }

    /// <summary>
    /// LP-I4, ledger-replicas.md §6.4 and §9.2: a write which can no longer reach <c>Q</c> fails with only the failures
    /// repetition cannot change once <c>N − Q + 1</c> replicas failed so, since no <c>Q</c> replicas can ever take the
    /// write, and otherwise with every failure, a transient one among them. It waits for a replica still running only
    /// while that replica's answer decides which failures it reports, and returns <see cref="ReplicaAnswers.Sealed"/> if the
    /// replica answers so meanwhile. Short of <c>N − Q + 1</c> such failures, it answers <c>Backpressure</c> when a replica
    /// answered it (ledger-protocol.md §6.4). Each replica answers written (<c>W</c>), sealed (<c>S</c>), backpressure
    /// (<c>B</c>), with a disposed replica's
    /// <see cref="ObjectDisposedException"/>, which repetition cannot change (<c>F</c>), with a transient
    /// <see cref="IOException"/> (<c>t</c>), or with a cancellation it raised itself, such as a transport's timeout, which
    /// is transient (<c>c</c>), in replica order as the write fans out, except the held one, which answers last, once the
    /// test releases it. <paramref name="reported"/> lists the replicas whose failures the write reports, in order, or is
    /// <c>S</c> when it returns sealed and <c>B</c> when it returns backpressure.
    /// </summary>
    [Theory(Timeout = 30_000)]
    [InlineData("tFF", 2, 2, true, "12")]
    [InlineData("FtF", 2, 2, true, "02")]
    [InlineData("FFt", 2, 2, false, "01")]
    [InlineData("Ftt", 2, 2, true, "012")]
    [InlineData("FcF", 2, 2, true, "02")]
    [InlineData("Fct", 2, 2, true, "012")]
    [InlineData("ttS", 2, 2, false, "01")]
    [InlineData("tFS", 2, 2, true, "S")]
    [InlineData("ttFFF", -1, 3, false, "234")]
    [InlineData("FFtWW", 4, 3, true, "012")]
    [InlineData("BFF", -1, 2, false, "12")]
    [InlineData("BtF", 2, 2, false, "B")]
    [InlineData("BFt", 2, 2, true, "B")]
    [InlineData("tBt", -1, 2, false, "B")]
    public async Task AWriteWhichCannotReachQWaitsOnlyWhilePendingAnswersDecideWhichFailuresItReports(
        string answers, int held, int q, bool waits, string reported)
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failures = answers.Select(Failure).ToArray();
        var replicas = Replicas(answers.Length);
        for (var index = 0; index < replicas.Length; index++)
        {
            var answer = Answer(answers[index], failures[index]);
            if (index == held)
            {
                replicas[index].WriteAsyncHandler = async (_, _, _, _) =>
                {
                    await release.Task;
                    return await answer;
                };
            }
            else
            {
                replicas[index].WriteAsyncHandler = (_, _, _, _) => new ValueTask<ReplicaWriteOutcome>(answer);
            }
        }

        await using var quorum = LedgerQuorumTestData.Create(replicas, q, Window);
        using var payload = LedgerReplicaTestData.Buffer("payload"u8);

        // The replicas which answer at once do so as the write fans out, so the write has met their answers on return.
        var write = quorum.WriteAsync(new Slot(3), payload, TestContext.Current.CancellationToken).AsTask();
        var waited = !write.IsCompleted;
        release.SetResult();
        Assert.Equal(waits, waited);
        if (reported is "S" or "B")
        {
            Assert.Equal(reported == "S" ? ReplicaWriteCase.Sealed : ReplicaWriteCase.Backpressure, (await write).Case);
            return;
        }

        var error = await Assert.ThrowsAsync<AggregateException>(() => write);

        // Each failure is the replica's own exception, a cancellation the replica raised included.
        Exception?[] expected = [.. reported.Select(replica => failures[replica - '0'])];
        Assert.Equal(expected, error.InnerExceptions);

        static Exception? Failure(char answer) => answer switch
        {
            'F' => new ObjectDisposedException("replica", "The replica's quorum was disposed."),
            't' => new IOException("The replica is unreachable."),
            'c' => new OperationCanceledException("The replica's transport timed out."),
            _ => null,
        };

        static Task<ReplicaWriteOutcome> Answer(char answer, Exception? failure)
        {
            if (failure is not null)
            {
                return Task.FromException<ReplicaWriteOutcome>(failure);
            }

            return Task.FromResult(answer switch
            {
                'S' => ReplicaAnswers.Sealed,
                'B' => ReplicaAnswers.Backpressure,
                _ => ReplicaAnswers.Written,
            });
        }
    }

    /// <summary>
    /// LP-I4: a write whose caller's token is cancelled fails with <see cref="OperationCanceledException"/>, even when
    /// replicas fail with the same cancellation before it observes the token, so the caller's cancellation is never
    /// reported as replica failures.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task AWriteCancelledByItsCallerFailsWithTheCancellationNotTheReplicaFailures()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var release = new TaskCompletionSource<ReplicaWriteOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var replicas = Replicas(3);
        replicas[0].WriteAsyncHandler = (_, _, _, token) =>
        {
            cancellation.Cancel();
            return ValueTask.FromCanceled<ReplicaWriteOutcome>(token);
        };
        replicas[1].WriteAsyncHandler = static (_, _, _, token) => ValueTask.FromCanceled<ReplicaWriteOutcome>(token);
        replicas[2].WriteAsyncHandler = (_, _, _, _) => new ValueTask<ReplicaWriteOutcome>(release.Task);
        await using var quorum = LedgerQuorumTestData.Create(replicas, 2, Window);
        using var payload = LedgerReplicaTestData.Buffer("payload"u8);

        var write = quorum.WriteAsync(new Slot(3), payload, cancellation.Token).AsTask();
        try
        {
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => write);
            Assert.Equal(cancellation.Token, error.CancellationToken);
        }
        finally
        {
            release.SetResult(ReplicaAnswers.Written);
        }
    }

    /// <summary>
    /// LP-I11: every operation sends every replica the contiguous tail it carries, and a seal check answers the greatest
    /// of it and the tails the answering replicas report. The quorum keeps no tail between operations.
    /// </summary>
    [Fact]
    public async Task EveryOperationCarriesItsTailToEveryReplica()
    {
        var token = TestContext.Current.CancellationToken;
        var replicas = Replicas(3);
        await using var quorum = LedgerQuorumTestData.Create(replicas, 2, Window);
        using var payload = LedgerReplicaTestData.Buffer("value"u8);

        _ = await quorum.WriteAsync(new Slot(5), payload, new Slot(5), token);
        _ = await quorum.WriteAsync(new Slot(1), payload, new Slot(2), token);
        (await quorum.ReadAsync(new Slot(9), new Slot(3), token)).Release();
        Assert.Equal(new LedgerTail(new Slot(4), IsSealed: false), (await quorum.CheckAsync(new Slot(4), token)).Tail);

        // The replica doubles answer at once, so every replica received every operation before it returned.
        foreach (var replica in replicas)
        {
            Assert.Equal([(5L, false), (1L, false)], replica.WriteCalls.Select(static call => (call.Address.Value, call.Recovery)));
            Assert.Equal([5L, 2L], replica.WriteCalls.Select(static call => call.ContiguousTail.Value));
            Assert.Equal(new Slot(3), Assert.Single(replica.ReadCalls).ContiguousTail);
            Assert.Equal(new Slot(4), Assert.Single(replica.ReadTailCalls).ContiguousTail);
        }
    }

    /// <summary>
    /// ledger-protocol.md §8.3: at or above the contiguous tail it carries, a read asks every replica and returns the first
    /// DATA any reports, without waiting for the others; the DATA a straggler reports afterwards is released.
    /// </summary>
    [Fact]
    public async Task AReadReturnsTheFirstDataAnyReplicaReports()
    {
        var replicas = Replicas(3);
        var content = LedgerReplicaTestData.CreatePayload(ArcBufferWriter.MinimumPageSize * 2 + 19);
        using var first = new TransferredArcBuffer(content);
        using var late = new TransferredArcBuffer(content);
        var gate = new TaskCompletionSource<ReplicaRead>(TaskCreationOptions.RunContinuationsAsynchronously);
        replicas[0].ReadAsyncHandler = (_, _) => ValueTask.FromResult(ReplicaAnswers.Missing);
        replicas[1].ReadAsyncHandler = (_, _) => ValueTask.FromResult(new ReplicaRead(first.Take()));
        replicas[2].ReadAsyncHandler = (_, _) => new ValueTask<ReplicaRead>(gate.Task);
        await using var quorum = LedgerQuorumTestData.Create(replicas, 2, Window);

        var result = await quorum.ReadAsync(new Slot(7), TestContext.Current.CancellationToken);

        Assert.Equal(ReplicaReadCase.Batch, result.Case);
        using (var returned = result.Batch)
        {
            Assert.Equal(content, returned.ToArray());
        }

        _ = gate.TrySetResult(new ReplicaRead(late.Take()));
    }

    /// <summary>
    /// ledger-protocol.md §8.3: at or above the contiguous tail it carries, a read reports missing once <c>Q</c> replicas do, without
    /// waiting for the others.
    /// </summary>
    [Fact]
    public async Task AReadAtOrAboveTheCarriedTailIsMissingAtQMissingAnswers()
    {
        var replicas = Replicas(3);
        var gate = new TaskCompletionSource<ReplicaRead>(TaskCreationOptions.RunContinuationsAsynchronously);
        replicas[2].ReadAsyncHandler = (_, _) => new ValueTask<ReplicaRead>(gate.Task);
        await using var quorum = LedgerQuorumTestData.Create(replicas, 2, Window);

        var result = await quorum.ReadAsync(new Slot(17), new Slot(17), TestContext.Current.CancellationToken);

        Assert.Equal(ReplicaReadCase.Missing, result.Case);
        _ = gate.TrySetResult(ReplicaAnswers.Missing);
    }

    /// <summary>
    /// Below the contiguous tail it carries, an address is decided, so a read which every replica answers missing fails with
    /// <see cref="InvalidDataException"/>, while one which some replica failed is unavailable (ledger-protocol.md §8.3).
    /// </summary>
    [Fact]
    public async Task AReadBelowTheCarriedTailWhichNoReplicaHoldsFails()
    {
        var token = TestContext.Current.CancellationToken;
        await using (var quorum = LedgerQuorumTestData.Create(Replicas(3), 2, Window))
        {
            _ = await Assert.ThrowsAsync<InvalidDataException>(
                async () => await quorum.ReadAsync(new Slot(3), new Slot(5), token));
        }

        var replicas = Replicas(3);
        var failure = new IOException("unreachable");
        replicas[1].ReadAsyncHandler = (_, _) => ValueTask.FromException<ReplicaRead>(failure);
        await using (var quorum = LedgerQuorumTestData.Create(replicas, 2, Window))
        {
            var error = await Assert.ThrowsAsync<AggregateException>(
                async () => await quorum.ReadAsync(new Slot(3), new Slot(5), token));
            Assert.Same(failure, Assert.Single(error.InnerExceptions));
        }
    }

    /// <summary>
    /// A read which neither finds DATA nor reaches <c>Q</c> missing answers fails with the replicas' failures, and reports
    /// the drop when a replica answered dropped (LP-I5).
    /// </summary>
    [Fact]
    public async Task AReadWhichCannotDecideFailsWithTheReplicaFailures()
    {
        var token = TestContext.Current.CancellationToken;
        var first = new InvalidOperationException("read one");
        var second = new NotSupportedException("read two");
        var replicas = Replicas(3);
        replicas[0].ReadAsyncHandler = (_, _) => throw first;
        replicas[1].ReadAsyncHandler = (_, _) => throw second;
        await using (var quorum = LedgerQuorumTestData.Create(replicas, 2, Window))
        {
            var error = await Assert.ThrowsAsync<AggregateException>(async () => await quorum.ReadAsync(new Slot(23), token));
            Assert.Contains(first, error.InnerExceptions);
            Assert.Contains(second, error.InnerExceptions);
        }

        var dropped = Replicas(3);
        dropped[0].ReadAsyncHandler = (_, _) => ValueTask.FromResult<ReplicaRead>(ReplicaWriteOutcome.Dropped.Instance);
        dropped[1].ReadAsyncHandler = (_, _) => ValueTask.FromException<ReplicaRead>(new IOException("unreachable"));
        await using (var quorum = LedgerQuorumTestData.Create(dropped, 2, Window))
        {
            DroppedAnswers.AssertDropped(await quorum.ReadAsync(new Slot(23), token));
        }
    }

    /// <summary>
    /// ledger-protocol.md §6.4, LP-I5: a read which cannot decide answers <see cref="ReplicaWriteOutcome.Dropped"/> when a
    /// replica answered it, since a drop is definite and permanent, even over failures repetition cannot change; then
    /// fails with those failures once <c>N − Q + 1</c> replicas failed so; then answers <c>Backpressure</c> when a replica
    /// answered it; and otherwise fails with every failure. Each replica answers dropped (<c>D</c>), backpressure
    /// (<c>B</c>), missing (<c>M</c>), with a disposed replica's <see cref="ObjectDisposedException"/>, which repetition
    /// cannot change (<c>F</c>), or with a transient <see cref="IOException"/> (<c>t</c>). <paramref name="reported"/> is
    /// <c>D</c> or <c>B</c> for those answers, or lists the replicas whose failures the read reports, in order.
    /// </summary>
    [Theory]
    [InlineData("DBM", "D")]
    [InlineData("FFD", "D")]
    [InlineData("FFB", "01")]
    [InlineData("BtM", "B")]
    [InlineData("tFM", "01")]
    public async Task AReadWhichCannotDecideAnswersByThePrecedenceOfItsAnswers(string answers, string reported)
    {
        var failures = answers.Select(Failure).ToArray();
        var replicas = Replicas(answers.Length);
        for (var index = 0; index < replicas.Length; index++)
        {
            var answer = answers[index];
            var failure = failures[index];
            replicas[index].ReadAsyncHandler = (_, _) => failure is not null
                ? ValueTask.FromException<ReplicaRead>(failure)
                : ValueTask.FromResult(answer switch
                {
                    'D' => new ReplicaRead(ReplicaWriteOutcome.Dropped.Instance),
                    'B' => new ReplicaRead(ReplicaWriteOutcome.Backpressure.Instance),
                    _ => ReplicaAnswers.Missing,
                });
        }

        await using var quorum = LedgerQuorumTestData.Create(replicas, 2, Window);

        var read = quorum.ReadAsync(new Slot(3), TestContext.Current.CancellationToken).AsTask();
        switch (reported)
        {
            case "D":
                Assert.Equal(ReplicaReadCase.Dropped, (await read).Case);
                break;
            case "B":
                Assert.Equal(ReplicaReadCase.Backpressure, (await read).Case);
                break;
            default:
                var error = await Assert.ThrowsAsync<AggregateException>(() => read);
                Exception?[] expected = [.. reported.Select(replica => failures[replica - '0'])];
                Assert.Equal(expected, error.InnerExceptions);
                break;
        }
    }

    /// <summary>
    /// ledger-protocol.md §6.4, LP-I5, LP-I9: a seal check answers sealed at once when one replica answered sealed or
    /// dropped, without waiting for <c>Q</c>, since the replica's seal is durable and the tails answered so far are known
    /// contiguous tails; one which cannot decide fails with the failures repetition cannot change once
    /// <c>N − Q + 1</c> replicas failed so, answers <c>Backpressure</c> when a replica answered it, and otherwise fails with
    /// every failure.
    /// </summary>
    [Fact]
    public async Task ASealCheckWhichCannotDecideAnswersByThePrecedenceOfItsAnswers()
    {
        var token = TestContext.Current.CancellationToken;
        var held = new TaskCompletionSource<ReplicaTailRead>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sealedOne = Replicas(3);
        sealedOne[0].ReadTailAsyncHandler = (_, _, _) => ValueTask.FromResult<ReplicaTailRead>(new LedgerReplicaTail(new Slot(5), IsSealed: true));
        sealedOne[1].ReadTailAsyncHandler = (_, _, _) => new ValueTask<ReplicaTailRead>(held.Task);
        sealedOne[2].ReadTailAsyncHandler = (_, _, _) => new ValueTask<ReplicaTailRead>(held.Task);
        await using (var quorum = LedgerQuorumTestData.Create(sealedOne, 2, Window))
        {
            Assert.Equal(new LedgerTail(new Slot(5), IsSealed: true), (await quorum.CheckAsync(new Slot(2), token)).Tail);
            _ = held.TrySetResult(new LedgerReplicaTail(Slot.Zero, IsSealed: false));
        }

        var transient = Replicas(3);
        transient[0].ReadTailAsyncHandler = (_, _, _) => ValueTask.FromResult<ReplicaTailRead>(ReplicaWriteOutcome.Backpressure.Instance);
        transient[1].ReadTailAsyncHandler = (_, _, _) => ValueTask.FromException<ReplicaTailRead>(new IOException("unreachable"));
        transient[2].ReadTailAsyncHandler = (_, _, _) => ValueTask.FromException<ReplicaTailRead>(new IOException("unreachable"));
        await using (var quorum = LedgerQuorumTestData.Create(transient, 2, Window))
        {
            Assert.Equal(new LedgerTailRead(ReplicaWriteOutcome.Backpressure.Instance), await quorum.CheckAsync(token));
        }

        var first = new ObjectDisposedException("replica", "The replica's quorum was disposed.");
        var second = new ObjectDisposedException("replica", "The replica's quorum was disposed.");
        var permanent = Replicas(3);
        permanent[0].ReadTailAsyncHandler = (_, _, _) => ValueTask.FromException<ReplicaTailRead>(first);
        permanent[1].ReadTailAsyncHandler = (_, _, _) => ValueTask.FromException<ReplicaTailRead>(second);
        permanent[2].ReadTailAsyncHandler = (_, _, _) => ValueTask.FromResult<ReplicaTailRead>(ReplicaWriteOutcome.Backpressure.Instance);
        await using (var quorum = LedgerQuorumTestData.Create(permanent, 2, Window))
        {
            var error = await Assert.ThrowsAsync<AggregateException>(async () => await quorum.CheckAsync(token));
            Assert.Equal(new Exception[] { first, second }, error.InnerExceptions);
        }
    }

    /// <summary>
    /// LP-I9: a seal check answers sealed at the first sealed answer, and otherwise unsealed once <c>Q</c> replicas
    /// answer, with the greatest contiguous tail among the answers it took and the one it carries; it does not wait for
    /// the others.
    /// </summary>
    [Fact]
    public async Task ASealCheckAnswersAtASealOrQWithTheGreatestTail()
    {
        var replicas = Replicas(3);
        var gate = new TaskCompletionSource<ReplicaTailRead>(TaskCreationOptions.RunContinuationsAsynchronously);
        replicas[0].ReadTailAsyncHandler = (_, _, _) => ValueTask.FromResult<ReplicaTailRead>(new LedgerReplicaTail(new Slot(4), IsSealed: false));
        replicas[1].ReadTailAsyncHandler = (_, _, _) => ValueTask.FromResult<ReplicaTailRead>(new LedgerReplicaTail(new Slot(6), IsSealed: true));
        replicas[2].ReadTailAsyncHandler = (_, _, _) => new ValueTask<ReplicaTailRead>(gate.Task);
        await using var quorum = LedgerQuorumTestData.Create(replicas, 2, Window);

        var tail = await quorum.CheckAsync(new Slot(2), TestContext.Current.CancellationToken);

        Assert.Equal(new LedgerTail(new Slot(6), IsSealed: true), tail);
        Assert.All(replicas, static replica => Assert.False(Assert.Single(replica.ReadTailCalls).Seal));
        _ = gate.TrySetResult(new LedgerReplicaTail(new Slot(9), IsSealed: false));
    }

    /// <summary>
    /// LP-I5, LP-I9: a seal check counts a dropped answer as a sealed one, which carries no contiguous tail; one which
    /// cannot reach <c>Q</c> answers fails.
    /// </summary>
    [Fact]
    public async Task ASealCheckCountsADroppedAnswerAsSealed()
    {
        var token = TestContext.Current.CancellationToken;
        var replicas = Replicas(3);
        replicas[0].ReadTailAsyncHandler = (_, _, _) => ValueTask.FromResult<ReplicaTailRead>(ReplicaWriteOutcome.Dropped.Instance);
        replicas[1].ReadTailAsyncHandler = (_, _, _) => ValueTask.FromException<ReplicaTailRead>(new IOException("unreachable"));
        await using (var quorum = LedgerQuorumTestData.Create(replicas, 2, Window))
        {
            Assert.Equal(new LedgerTail(new Slot(3), IsSealed: true), (await quorum.CheckAsync(new Slot(3), token)).Tail);
        }

        var failing = Replicas(3);
        failing[0].ReadTailAsyncHandler = (_, _, _) => ValueTask.FromException<ReplicaTailRead>(new IOException("unreachable"));
        failing[1].ReadTailAsyncHandler = (_, _, _) => ValueTask.FromException<ReplicaTailRead>(new IOException("unreachable"));
        await using (var quorum = LedgerQuorumTestData.Create(failing, 2, Window))
        {
            _ = await Assert.ThrowsAsync<AggregateException>(async () => await quorum.CheckAsync(Slot.Zero, token));
        }
    }

    /// <summary>
    /// LP-I6: a recovery seals every replica and reads from <c>c</c> once <c>N − Q + 1</c> answered sealed, with the
    /// greatest contiguous tail among them, without waiting for the others; one which cannot reach <c>N − Q + 1</c>
    /// sealed answers fails.
    /// </summary>
    [Theory]
    [InlineData(3, 2)]
    [InlineData(3, 1)]
    [InlineData(5, 3)]
    [InlineData(4, 2)]
    public async Task ARecoveryReadsFromTheGreatestTailOfItsFirstNMinusQPlusOneSealAnswers(int replicaCount, int q)
    {
        var token = TestContext.Current.CancellationToken;
        var coverage = replicaCount - q + 1;
        var replicas = Replicas(replicaCount);
        var gate = new TaskCompletionSource<ReplicaTailRead>(TaskCreationOptions.RunContinuationsAsynchronously);
        for (var index = 0; index < replicaCount; index++)
        {
            var tail = new Slot(index);
            replicas[index].ReadTailAsyncHandler = index >= coverage
                ? (_, _, _) => new ValueTask<ReplicaTailRead>(gate.Task)
                : (_, _, _) => ValueTask.FromResult<ReplicaTailRead>(new LedgerReplicaTail(tail, IsSealed: true));
        }

        await using (var quorum = LedgerQuorumTestData.Create(replicas, q, Window))
        {
            // Every replica reports each slot missing, so the recovery ends at c, the first slot it reads.
            Assert.Equal(
                new LedgerRecovery(new Slot(coverage - 1), new Slot(coverage - 1)),
                await quorum.RecoverAsync(Slot.Zero, token));
            Assert.All(replicas, static replica => Assert.True(Assert.Single(replica.ReadTailCalls).Seal));
            _ = gate.TrySetResult(new LedgerReplicaTail(Slot.Zero, IsSealed: true));
        }

        var failing = Replicas(replicaCount);
        foreach (var replica in failing.Take(q))
        {
            replica.ReadTailAsyncHandler = (_, _, _) => ValueTask.FromException<ReplicaTailRead>(new NotSupportedException("unsupported"));
        }

        await using (var quorum = LedgerQuorumTestData.Create(failing, q, Window))
        {
            _ = await Assert.ThrowsAsync<AggregateException>(async () => await quorum.RecoverEndAsync(token));
        }
    }

    /// <summary>
    /// LP-I6, LP-I9: after a recovery, a seal check reports sealed with any <c>N − Q</c> replicas unavailable, even when
    /// the recovery never sealed the replicas a held seal kept from it: its <c>N − Q + 1</c> sealed replicas meet every
    /// <c>Q</c> the check hears.
    /// </summary>
    [Theory]
    [InlineData(3, 2)]
    [InlineData(5, 3)]
    [InlineData(4, 2)]
    [InlineData(4, 3)]
    public async Task ASealCheckAfterARecoveryReportsSealedWithAnyNMinusQReplicasUnavailable(int replicaCount, int q)
    {
        var token = TestContext.Current.CancellationToken;
        var coverage = replicaCount - q + 1;
        using var nodes = new InMemoryLedgerReplicaTransport();
        var replicas = Enumerable.Range(0, replicaCount)
            .Select(index => new ControlledLedgerReplica(nodes.Open(new NodeId(Guid.NewGuid()), LedgerReplicaTestData.Ledger, Window)))
            .ToArray();
        foreach (var held in replicas.Skip(Math.Max(coverage, q)))
        {
            held.HoldSeals();
        }

        await using var quorum = LedgerQuorumTestData.Create(replicas, q, Window);
        _ = await quorum.RecoverAsync(Slot.Zero, token);

        foreach (var unavailable in Subsets(replicaCount, replicaCount - q))
        {
            for (var index = 0; index < replicaCount; index++)
            {
                replicas[index].Unreachable = unavailable.Contains(index);
            }

            Assert.True((await quorum.CheckAsync(Slot.Zero, token)).Tail.IsSealed);
        }

        foreach (var replica in replicas)
        {
            replica.Unreachable = false;
            replica.ReleaseSeals();
        }
    }

    /// <summary>
    /// A cancelled call, or one with an invalid address, never reaches a replica.
    /// </summary>
    [Fact]
    public async Task InvalidInputsAndPreCancellationDoNotReachReplicas()
    {
        var replicas = Replicas(3);
        await using var quorum = LedgerQuorumTestData.Create(replicas, 2, Window);
        using var payload = LedgerReplicaTestData.Buffer([1]);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var token = TestContext.Current.CancellationToken;

        foreach (var address in new long[] { -1, long.MaxValue })
        {
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await quorum.WriteAsync(address, payload, token));
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await quorum.ReadAsync(address, token));
        }

        await Assert.ThrowsAsync<OperationCanceledException>(async () => await quorum.WriteAsync(new Slot(0), payload, cancellation.Token));
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await quorum.ReadAsync(new Slot(0), cancellation.Token));
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await quorum.CheckAsync(cancellation.Token));
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await quorum.RecoverAsync(Slot.Zero, cancellation.Token));
        Assert.All(replicas, static replica => Assert.Empty(replica.WriteCalls));
        Assert.All(replicas, static replica => Assert.Empty(replica.ReadCalls));
        Assert.All(replicas, static replica => Assert.Empty(replica.ReadTailCalls));
    }

    /// <summary>
    /// ledger-protocol.md §8.3, LP-I4, BUF-I4: synchronous, asynchronous, and mixed replica completions each decide at Q
    /// (or at the first sealed answer), track only pending stragglers, keep zero-copy payload pages pinned and unpoisoned
    /// until the last straggler completes, and release every page with zero leaks.
    /// </summary>
    [Fact]
    public async Task WriteSyncAndMixedCompletionsDecideCleanlyAndPinPayloadForStragglers()
    {
        var token = TestContext.Current.CancellationToken;

        // Pure synchronous completion: zero stragglers, immediate page release.
        var syncReplicas = Replicas(3);
        await using (var quorum = LedgerQuorumTestData.Create(syncReplicas, 2, Window))
        {
            using var syncPayload = LedgerReplicaTestData.Buffer("sync-payload"u8);
            Assert.Equal(ReplicaWriteCase.Written, (await quorum.WriteAsync(new Slot(0), syncPayload, token)).Case);
            Assert.Equal(0, quorum.TrackedStragglers);
        }

        // Mixed sync + async: replica 0 and 2 complete synchronously (Written), replica 1 is pending (straggler).
        // Even after caller disposes payload and writer, replica 1 reads uncorrupted pages under Debug page poisoning.
        var mixedGate = new TaskCompletionSource<ReplicaWriteOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        ArcBuffer stragglerBuffer = default;
        var mixedReplicas = Replicas(3);
        mixedReplicas[1].WriteAsyncHandler = (_, buffer, _, _) =>
        {
            stragglerBuffer = buffer;
            return new ValueTask<ReplicaWriteOutcome>(mixedGate.Task);
        };

        var expectedBytes = LedgerReplicaTestData.CreatePayload(ArcBufferWriter.MinimumPageSize * 2 + 19);
        var mixedQuorum = LedgerQuorumTestData.Create(mixedReplicas, 2, Window);
        var writer = new ArcBufferWriter();
        writer.Write(expectedBytes);
        var mixedPayload = writer.PeekSlice(writer.Length);
        var pages = mixedPayload.Pages.ToArray();

        Assert.Equal(ReplicaWriteCase.Written, (await mixedQuorum.WriteAsync(new Slot(1), mixedPayload, token)).Case);
        Assert.Equal(1, mixedQuorum.TrackedStragglers);

        mixedPayload.Dispose();
        writer.Dispose();
        CompositeLedgerReplicaTestAssertions.ReferenceCounts(pages, 1);
        Assert.Equal(expectedBytes, stragglerBuffer.ToArray());

        _ = mixedGate.TrySetResult(ReplicaAnswers.Written);
        await mixedQuorum.DisposeAsync();
        Assert.Equal(1, mixedQuorum.TrackedStragglers);
        CompositeLedgerReplicaTestAssertions.ReferenceCounts(pages, 0);

        // Mixed pending-then-sync-sealed: replica 0 pending, replica 1 sync Sealed, replica 2 sync Written.
        var sealedGate = new TaskCompletionSource<ReplicaWriteOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sealedReplicas = Replicas(3);
        sealedReplicas[0].WriteAsyncHandler = (_, _, _, _) => new ValueTask<ReplicaWriteOutcome>(sealedGate.Task);
        sealedReplicas[1].WriteAsyncHandler = (_, _, _, _) => ValueTask.FromResult(ReplicaAnswers.Sealed);
        var sealedQuorum = LedgerQuorumTestData.Create(sealedReplicas, 2, Window);
        using (var payload = LedgerReplicaTestData.Buffer("sealed-mixed"u8))
        {
            Assert.Equal(ReplicaWriteCase.Sealed, (await sealedQuorum.WriteAsync(new Slot(2), payload, token)).Case);
            Assert.Equal(1, sealedQuorum.TrackedStragglers);
        }

        _ = sealedGate.TrySetResult(ReplicaAnswers.Written);
        await sealedQuorum.DisposeAsync();
        Assert.Equal(1, sealedQuorum.TrackedStragglers);
    }

    /// <summary>
    /// ledger-protocol.md §6.3, §8.3: <see cref="LedgerQuorum.DisposeAsync"/> waits for tracked write stragglers to
    /// finish before disposing replicas, observes straggler exceptions without faulting disposal, and releases retained
    /// payload pages once the straggler exits.
    /// </summary>
    [Fact]
    public async Task WriteStragglerFaultIsObservedAndDrainedBeforeReplicasAreDisposed()
    {
        var token = TestContext.Current.CancellationToken;
        var stragglerGate = new TaskCompletionSource<ReplicaWriteOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var replicas = Replicas(3);
        replicas[2].WriteAsyncHandler = (_, _, _, _) => new ValueTask<ReplicaWriteOutcome>(stragglerGate.Task);
        var quorum = LedgerQuorumTestData.Create(replicas, 2, Window);

        var writer = new ArcBufferWriter();
        writer.Write("straggler-drain"u8);
        var payload = writer.PeekSlice(writer.Length);
        var pages = payload.Pages.ToArray();

        Assert.Equal(ReplicaWriteCase.Written, (await quorum.WriteAsync(new Slot(0), payload, token)).Case);
        Assert.Equal(1, quorum.TrackedStragglers);
        payload.Dispose();
        writer.Dispose();
        CompositeLedgerReplicaTestAssertions.ReferenceCounts(pages, 1);

        var disposal = quorum.DisposeAsync().AsTask();
        _ = await Task.WhenAny(disposal, Task.Delay(TimeSpan.FromMilliseconds(20), TimeProvider.System, token));
        Assert.False(disposal.IsCompleted);
        Assert.All(replicas, static replica => Assert.Equal(0, replica.DisposeAsyncCount));

        _ = stragglerGate.TrySetException(new IOException("late straggler failure"));
        await disposal.WaitAsync(TimeSpan.FromSeconds(5), TimeProvider.System, token);

        Assert.Equal(1, quorum.TrackedStragglers);
        Assert.All(replicas, static replica => Assert.Equal(1, replica.DisposeAsyncCount));
        CompositeLedgerReplicaTestAssertions.ReferenceCounts(pages, 0);
    }

    /// <summary>
    /// ledger-protocol.md §6.3: sequential writes with the same cancelable token reuse the cached linked token cleanly,
    /// concurrent writes with distinct tokens fall back to a per-call linked token without leaking, caller cancellation
    /// takes precedence over concurrent quorum disposal, and disposal alone throws <see cref="ObjectDisposedException"/>.
    /// </summary>
    [Fact]
    public async Task WriteTokenCachingAndCancellationVsDisposalPrecedenceArePreserved()
    {
        var token = TestContext.Current.CancellationToken;

        // Reuse the cached linked token across sequential writes with the same caller token, and overlap a second token.
        var gate = new TaskCompletionSource<ReplicaWriteOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var replicas = Replicas(3);
        var callCount = 0;
        replicas[0].WriteAsyncHandler = (_, _, _, _) =>
            Interlocked.Increment(ref callCount) == 2
                ? new ValueTask<ReplicaWriteOutcome>(gate.Task)
                : ValueTask.FromResult(ReplicaAnswers.Written);
        replicas[1].WriteAsyncHandler = (_, _, _, _) =>
            Volatile.Read(ref callCount) == 2
                ? new ValueTask<ReplicaWriteOutcome>(gate.Task)
                : ValueTask.FromResult(ReplicaAnswers.Written);
        await using (var quorum = LedgerQuorumTestData.Create(replicas, 2, Window))
        {
            using var firstCaller = CancellationTokenSource.CreateLinkedTokenSource(token);
            using var secondCaller = CancellationTokenSource.CreateLinkedTokenSource(token);
            using var payload = LedgerReplicaTestData.Buffer("tokens"u8);

            // Two sequential writes with firstCaller.Token hit the 1-slot cached write token.
            Assert.Equal(ReplicaWriteCase.Written, (await quorum.WriteAsync(new Slot(0), payload, firstCaller.Token)).Case);
            var inFlight = quorum.WriteAsync(new Slot(1), payload, firstCaller.Token).AsTask();
            Assert.False(inFlight.IsCompleted);

            // Concurrent write with a different caller token uses a per-call linked CTS while firstCaller is in flight.
            Assert.Equal(ReplicaWriteCase.Written, (await quorum.WriteAsync(new Slot(2), payload, secondCaller.Token)).Case);
            Assert.Equal(replicas[0].WriteCalls[0].CancellationToken, replicas[0].WriteCalls[1].CancellationToken);
            Assert.NotEqual(replicas[0].WriteCalls[1].CancellationToken, replicas[0].WriteCalls[2].CancellationToken);

            _ = gate.TrySetResult(ReplicaAnswers.Written);
            Assert.Equal(ReplicaWriteCase.Written, (await inFlight).Case);
        }

        // Caller cancellation takes precedence over disposal when both fire while a write is pending.
        var cancelGate = new TaskCompletionSource<ReplicaWriteOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelReplicas = Replicas(3);
        foreach (var replica in cancelReplicas)
        {
            replica.WriteAsyncHandler = async (_, _, _, ct) =>
            {
                await cancelGate.Task.WaitAsync(ct).ConfigureAwait(false);
                return ReplicaAnswers.Written;
            };
        }

        var cancelQuorum = LedgerQuorumTestData.Create(cancelReplicas, 2, Window);
        using (var caller = new CancellationTokenSource())
        using (var payload = LedgerReplicaTestData.Buffer("cancel-wins"u8))
        {
            var write = cancelQuorum.WriteAsync(new Slot(0), payload, caller.Token).AsTask();
            await caller.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => write);
        }

        await cancelQuorum.DisposeAsync();

        // Disposal alone (with non-canceled caller token) throws ObjectDisposedException.
        var disposeGate = new TaskCompletionSource<ReplicaWriteOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposeReplicas = Replicas(3);
        foreach (var replica in disposeReplicas)
        {
            replica.WriteAsyncHandler = async (_, _, _, ct) =>
            {
                await disposeGate.Task.WaitAsync(ct).ConfigureAwait(false);
                return ReplicaAnswers.Written;
            };
        }

        var disposeQuorum = LedgerQuorumTestData.Create(disposeReplicas, 2, Window);
        using (var payload = LedgerReplicaTestData.Buffer("dispose-wins"u8))
        {
            var write = disposeQuorum.WriteAsync(new Slot(0), payload, token).AsTask();
            var disposal = disposeQuorum.DisposeAsync().AsTask();
            _ = await Assert.ThrowsAsync<ObjectDisposedException>(() => write);
            await disposal;
        }
    }

    /// <summary>
    /// LP-I4, ledger-protocol.md §6.4: permanent failures reaching <c>N - Q + 1</c> fail immediately even while other
    /// replicas remain pending, while fewer permanent failures alongside <c>Backpressure</c> return <c>Backpressure</c>,
    /// consistently across both synchronous and asynchronous replica completions.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WriteNoQuorumClassifiesPermanentAndBackpressureConsistentlyAcrossSyncAndAsync(bool asynchronous)
    {
        var token = TestContext.Current.CancellationToken;
        using var payload = LedgerReplicaTestData.Buffer("no-quorum"u8);

        // 1 permanent + 1 Backpressure + 1 Written (N=3, Q=2) -> Backpressure (only 1 permanent < N-Q+1 = 2).
        var bpReplicas = Replicas(3);
        bpReplicas[0].WriteAsyncHandler = (_, _, _, _) => FailAsync(new InvalidDataException("corrupt"), asynchronous);
        bpReplicas[1].WriteAsyncHandler = (_, _, _, _) => AnswerAsync(ReplicaAnswers.Backpressure, asynchronous);
        bpReplicas[2].WriteAsyncHandler = (_, _, _, _) => AnswerAsync(ReplicaAnswers.Written, asynchronous);
        await using (var quorum = LedgerQuorumTestData.Create(bpReplicas, 2, Window))
        {
            Assert.Equal(ReplicaWriteCase.Backpressure, (await quorum.WriteAsync(new Slot(0), payload, token)).Case);
        }

        // 2 permanent (N-Q+1) + 1 pending straggler -> throws AggregateException immediately without waiting for replica 2.
        var stragglerGate = new TaskCompletionSource<ReplicaWriteOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var perm1 = new InvalidDataException("corrupt-0");
        var perm2 = new NotSupportedException("unsupported-1");
        var permReplicas = Replicas(3);
        permReplicas[0].WriteAsyncHandler = (_, _, _, _) => FailAsync(perm1, asynchronous);
        permReplicas[1].WriteAsyncHandler = (_, _, _, _) => FailAsync(perm2, asynchronous);
        permReplicas[2].WriteAsyncHandler = (_, _, _, _) => new ValueTask<ReplicaWriteOutcome>(stragglerGate.Task);
        var permQuorum = LedgerQuorumTestData.Create(permReplicas, 2, Window);
        var aggregate = await Assert.ThrowsAsync<AggregateException>(
            async () => await permQuorum.WriteAsync(new Slot(1), payload, token));
        Assert.Equal(2, aggregate.InnerExceptions.Count);
        Assert.Contains(perm1, aggregate.InnerExceptions);
        Assert.Contains(perm2, aggregate.InnerExceptions);
        Assert.Equal(1, permQuorum.TrackedStragglers);
        _ = stragglerGate.TrySetResult(ReplicaAnswers.Written);
        await permQuorum.DisposeAsync();
        Assert.Equal(1, permQuorum.TrackedStragglers);

        static ValueTask<ReplicaWriteOutcome> AnswerAsync(ReplicaWriteOutcome outcome, bool asyncCompletion) =>
            asyncCompletion ? CompleteLaterAsync(outcome) : ValueTask.FromResult(outcome);

        static ValueTask<ReplicaWriteOutcome> FailAsync(Exception exception, bool asyncCompletion) =>
            asyncCompletion ? ThrowLaterAsync(exception) : ValueTask.FromException<ReplicaWriteOutcome>(exception);

        static async ValueTask<ReplicaWriteOutcome> CompleteLaterAsync(ReplicaWriteOutcome outcome)
        {
            await Task.Yield();
            return outcome;
        }

        static async ValueTask<ReplicaWriteOutcome> ThrowLaterAsync(Exception exception)
        {
            await Task.Yield();
            throw exception;
        }
    }

    private static Exception? Failure(char answer) => answer switch
    {
        'F' => new ObjectDisposedException("replica", "The replica's quorum was disposed."),
        't' => new IOException("The replica is unreachable."),
        _ => null,
    };

    private static RecordingLedgerReplica[] Replicas(int count) => [.. Enumerable.Range(0, count).Select(static _ => new RecordingLedgerReplica())];

    /// <summary>
    /// Returns every subset of <c>{0, ..., <paramref name="count"/> - 1}</c> with <paramref name="size"/> members.
    /// </summary>
    private static IEnumerable<HashSet<int>> Subsets(int count, int size) =>
        Enumerable.Range(0, 1 << count)
            .Where(mask => int.PopCount(mask) == size)
            .Select(mask => Enumerable.Range(0, count).Where(index => (mask & (1 << index)) != 0).ToHashSet());
}
