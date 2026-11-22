using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Ledgers.Memory;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Ledgers.Tests;

/// <summary>
/// Checks one exclusively owned memory replica (ledger-replicas.md §8.1, §9.1): create-once DATA registers (LR-I1), the
/// positionless seal a writer write meets and a recovery write passes (LR-I5, LR-I8), and the retained contiguous tail
/// (LR-I6).
/// </summary>
public sealed class InMemoryLedgerReplicaTests : IDisposable
{
    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    public void Dispose() => _leaks.Dispose();

    private static LedgerReplicaOptions Options(int windowSize = 8) => new() { WindowSize = windowSize };

    /// <summary>
    /// An empty replica reads missing, and its contiguous-tail read reports 0, unsealed (LR-I6).
    /// </summary>
    [Fact]
    public async Task AnEmptyReplicaReadsMissingWithAZeroUnsealedTail()
    {
        await using var replica = new InMemoryLedgerReplica(Options());

        var read = await replica.ReadAsync(new Slot(0), TestContext.Current.CancellationToken);
        Assert.Equal(ReplicaReadCase.Missing, read.Case);
        Assert.Equal(new LedgerReplicaTail(Slot.Zero, IsSealed: false), await replica.ReadTailAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// ledger-replicas.md §3.2: an empty payload is never DATA, so a writer write and a recovery write reject it.
    /// </summary>
    [Fact]
    public async Task AnEmptyPayloadIsRejectedByEveryWrite()
    {
        await using var replica = new InMemoryLedgerReplica(Options());
        using var writer = new ArcBufferWriter(0);
        using var payload = writer.PeekSlice(0);

        await Assert.ThrowsAsync<ArgumentException>(
            async () => await replica.WriteAsync(new Slot(0), payload, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(
            async () => await replica.RecoveryWriteAsync(0, payload, TestContext.Current.CancellationToken));
        Assert.Equal(ReplicaReadCase.Missing, (await replica.ReadAsync(0, TestContext.Current.CancellationToken)).Case);
    }

    [Fact]
    public async Task AWrittenValueReadsAsData()
    {
        await using var replica = new InMemoryLedgerReplica(Options());
        using var payload = LedgerReplicaTestData.Buffer("value"u8);

        var result = await replica.WriteAsync(new Slot(3), payload, TestContext.Current.CancellationToken);
        Assert.Equal(ReplicaAnswers.Written, result);

        Assert.Equal("value"u8.ToArray(), await LedgerReplicaTestData.ReadBytesAsync(replica, 3, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// LR-I1: the first write decides an address, and a repeat reports written and leaves it.
    /// </summary>
    [Fact]
    public async Task ARepeatedWriteReportsWrittenAndKeepsTheFirstValue()
    {
        await using var replica = new InMemoryLedgerReplica(Options());
        using var first = LedgerReplicaTestData.Buffer("stable"u8);
        using var second = LedgerReplicaTestData.Buffer("other"u8);

        Assert.Equal(ReplicaAnswers.Written, await replica.WriteAsync(new Slot(4), first, TestContext.Current.CancellationToken));
        Assert.Equal(ReplicaAnswers.Written, await replica.WriteAsync(new Slot(4), second, TestContext.Current.CancellationToken));
        Assert.Equal(ReplicaAnswers.Written, await replica.RecoveryWriteAsync(4, second, TestContext.Current.CancellationToken));

        Assert.Equal("stable"u8.ToArray(), await LedgerReplicaTestData.ReadBytesAsync(replica, 4, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// LR-I8: a writer write on a sealed replica stores nothing and answers sealed, at every address.
    /// </summary>
    [Fact]
    public async Task AWriterWriteOnASealedReplicaAnswersSealedAndStoresNothing()
    {
        await using var replica = new InMemoryLedgerReplica(Options());
        using var payload = LedgerReplicaTestData.Buffer("late"u8);
        await replica.SealAsync(TestContext.Current.CancellationToken);

        foreach (var address in new long[] { 0, 5 })
        {
            Assert.Equal(ReplicaAnswers.Sealed, await replica.WriteAsync(new Slot(address), payload, TestContext.Current.CancellationToken));
            Assert.Equal(ReplicaReadCase.Missing, (await replica.ReadAsync(address, TestContext.Current.CancellationToken)).Case);
        }
    }

    /// <summary>
    /// LR-I8: a seal is positionless and idempotent: it leaves the registers and reports the retained contiguous tail,
    /// sealed, as every later contiguous-tail read does.
    /// </summary>
    [Fact]
    public async Task ASealIsPositionlessAndIdempotent()
    {
        var token = TestContext.Current.CancellationToken;
        await using var replica = new InMemoryLedgerReplica(Options());
        using var payload = LedgerReplicaTestData.Buffer("value"u8);
        foreach (var address in new long[] { 0, 1, 2 })
        {
            Assert.Equal(ReplicaAnswers.Written, await replica.WriteAsync(address, payload, token));
        }

        // The retained contiguous tail is what operations carried, not the DATA the replica holds (LR-I6).
        Assert.Equal(new LedgerReplicaTail(Slot.Zero, IsSealed: true), await replica.SealAsync(token));
        Assert.Equal(new LedgerReplicaTail(new Slot(2), IsSealed: true), await replica.ReadTailAsync(new Slot(2), seal: true, token));
        Assert.Equal(new LedgerReplicaTail(new Slot(2), IsSealed: true), await replica.ReadTailAsync(token));
        foreach (var address in new long[] { 0, 1, 2 })
        {
            Assert.Equal("value"u8.ToArray(), await LedgerReplicaTestData.ReadBytesAsync(replica, address, token));
        }
    }

    [Fact]
    public async Task ConcurrentSealsAllAnswerSealed()
    {
        await using var replica = new InMemoryLedgerReplica(Options());

        var answers = await Task.WhenAll(Enumerable.Range(0, 16).Select(
            _ => replica.SealAsync(TestContext.Current.CancellationToken).AsTask()));

        Assert.All(answers, static answer => Assert.Equal(new LedgerReplicaTail(Slot.Zero, IsSealed: true), answer));
    }

    /// <summary>
    /// LR-I8: a writer write racing a seal either precedes it, written and held, or follows it, sealed and absent.
    /// </summary>
    [Fact]
    public async Task AWriterWriteRacingASealIsEitherWrittenOrSealedAndAbsent()
    {
        var token = TestContext.Current.CancellationToken;
        await using var replica = new InMemoryLedgerReplica(Options());
        using var payload = LedgerReplicaTestData.Buffer("race"u8);

        var seal = Task.Run(() => replica.SealAsync(token).AsTask(), token);
        var written = await replica.WriteAsync(new Slot(0), payload, token);
        Assert.True((await seal).Tail.IsSealed);

        Assert.Equal(written is ReplicaWriteOutcome.Written ? "race"u8.ToArray() : null, await LedgerReplicaTestData.ReadBytesAsync(replica, 0, token));
        Assert.Equal(ReplicaAnswers.Sealed, await replica.WriteAsync(new Slot(1), payload, token));
    }

    /// <summary>
    /// LR-I5, LR-I8: a recovery write on a sealed replica creates missing DATA and answers written; the replica stays
    /// sealed.
    /// </summary>
    [Fact]
    public async Task ARecoveryWriteOnASealedReplicaStoresDataAndLeavesItSealed()
    {
        var token = TestContext.Current.CancellationToken;
        await using var replica = new InMemoryLedgerReplica(Options());
        using var payload = LedgerReplicaTestData.Buffer("recovered"u8);
        await replica.SealAsync(token);

        Assert.Equal(ReplicaAnswers.Written, await replica.RecoveryWriteAsync(7, payload, token));

        Assert.Equal("recovered"u8.ToArray(), await LedgerReplicaTestData.ReadBytesAsync(replica, 7, token));
        Assert.True((await replica.ReadTailAsync(token)).Tail.IsSealed);
        Assert.Equal(ReplicaAnswers.Sealed, await replica.WriteAsync(8, payload, token));
    }

    /// <summary>
    /// LR-I1, LR-I5: concurrent recovery writes of one value on a sealed replica converge on it.
    /// </summary>
    [Fact]
    public async Task ConcurrentRecoveryWritesOfOneValueConverge()
    {
        await using var replica = new InMemoryLedgerReplica(Options());
        await replica.SealAsync(TestContext.Current.CancellationToken);
        using var payload = LedgerReplicaTestData.Buffer("converge"u8);

        var answers = await Task.WhenAll(Enumerable.Range(0, 16).Select(
            _ => replica.RecoveryWriteAsync(0, payload, TestContext.Current.CancellationToken).AsTask()));

        Assert.All(answers, static answer => Assert.Equal(ReplicaAnswers.Written, answer));
        Assert.Equal("converge"u8.ToArray(), await LedgerReplicaTestData.ReadBytesAsync(replica, 0, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// LR-I6: every operation carries a known contiguous tail, and the replica retains the greatest; a contiguous-tail
    /// read reports the greater of it and its argument.
    /// </summary>
    [Fact]
    public async Task TheReplicaRetainsTheGreatestContiguousTailAnyOperationCarried()
    {
        var token = TestContext.Current.CancellationToken;
        await using var replica = new InMemoryLedgerReplica(Options());
        using var payload = LedgerReplicaTestData.Buffer("value"u8);

        Assert.Equal(ReplicaAnswers.Written, await replica.WriteAsync(new Slot(3), payload, new Slot(3), recovery: false, token));
        Assert.Equal(new LedgerReplicaTail(new Slot(3), IsSealed: false), await replica.ReadTailAsync(token));
        (await replica.ReadAsync(new Slot(9), new Slot(5), token)).Release();
        Assert.Equal(new LedgerReplicaTail(new Slot(5), IsSealed: false), await replica.ReadTailAsync(new Slot(2), seal: false, token));
        Assert.Equal(ReplicaAnswers.Written, await replica.WriteAsync(new Slot(1), payload, new Slot(1), recovery: true, token));
        Assert.Equal(new LedgerReplicaTail(new Slot(7), IsSealed: false), await replica.ReadTailAsync(new Slot(7), seal: false, token));
        Assert.Equal(new LedgerReplicaTail(new Slot(7), IsSealed: true), await replica.SealAsync(token));

        // A writer write the seal refuses still carries its contiguous tail.
        Assert.Equal(ReplicaAnswers.Sealed, await replica.WriteAsync(new Slot(4), payload, new Slot(8), recovery: false, token));
        Assert.Equal(new LedgerReplicaTail(new Slot(8), IsSealed: true), await replica.ReadTailAsync(token));
    }

    [Fact]
    public async Task AMultiPageWriteSnapshotsItsPayload()
    {
        await using var replica = new InMemoryLedgerReplica(Options());
        var writer = new ArcBufferWriter();
        var expected = LedgerReplicaTestData.CreatePayload(ArcBufferWriter.MinimumPageSize * 3 + 29);
        writer.Write(expected);
        var payload = writer.PeekSlice(writer.Length);
        Assert.False(payload.AsReadOnlySequence().IsSingleSegment);

        await replica.WriteAsync(new Slot(11), payload, TestContext.Current.CancellationToken);
        writer.WriteAt(0, "changed"u8);
        payload.Dispose();
        writer.Dispose();

        Assert.Equal(expected, await LedgerReplicaTestData.ReadBytesAsync(replica, 11, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AReadReturnsAnIndependentSnapshot()
    {
        await using var replica = new InMemoryLedgerReplica(Options());
        var expected = LedgerReplicaTestData.CreatePayload(ArcBufferWriter.MinimumPageSize + 19);
        using var payload = LedgerReplicaTestData.Buffer(expected);
        await replica.WriteAsync(new Slot(12), payload, TestContext.Current.CancellationToken);

        var firstResult = await replica.ReadAsync(new Slot(12), TestContext.Current.CancellationToken);
        Assert.Equal(ReplicaReadCase.Batch, firstResult.Case);
        using var first = firstResult.Batch;
        var firstArrays = first.ArraySegments.Select(static ledger => ledger.Array).ToArray();
        first.First.Array[first.Offset] ^= byte.MaxValue;

        var secondResult = await replica.ReadAsync(new Slot(12), TestContext.Current.CancellationToken);
        Assert.Equal(ReplicaReadCase.Batch, secondResult.Case);
        using var second = secondResult.Batch;
        Assert.Equal(expected, second.ToArray());
        Assert.All(
            firstArrays.Zip(second.ArraySegments.Select(static ledger => ledger.Array)),
            pair => Assert.NotSame(pair.First, pair.Second));
    }

    [Fact]
    public async Task TheMaximumValidAddressCanBeWrittenAndRead()
    {
        await using var replica = new InMemoryLedgerReplica(Options(windowSize: 1));
        using var payload = LedgerReplicaTestData.Buffer("maximum"u8);

        await replica.WriteAsync(new Slot(long.MaxValue - 1), payload, TestContext.Current.CancellationToken);

        Assert.Equal("maximum"u8.ToArray(), await LedgerReplicaTestData.ReadBytesAsync(replica, long.MaxValue - 1, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ConcurrentWritesToDistinctAddressesRemainVisible()
    {
        await using var replica = new InMemoryLedgerReplica(Options(windowSize: 64));
        using var payload = LedgerReplicaTestData.Buffer("parallel"u8);
        var addresses = Enumerable.Range(0, 64).Select(static value => (long)value).ToArray();

        await Task.WhenAll(addresses.Select(address => replica.WriteAsync(address, payload, TestContext.Current.CancellationToken).AsTask()));

        var reads = await Task.WhenAll(addresses.Select(address => LedgerReplicaTestData.ReadBytesAsync(replica, address, TestContext.Current.CancellationToken)));
        Assert.All(reads, value => Assert.Equal("parallel"u8.ToArray(), value));
    }

    /// <summary>
    /// A cancelled operation has no effect: it writes nothing, seals nothing, and retains no contiguous tail (LR-I6).
    /// </summary>
    [Fact]
    public async Task ACancelledOperationHasNoEffect()
    {
        await using var replica = new InMemoryLedgerReplica(Options(windowSize: 4));
        using var payload = LedgerReplicaTestData.Buffer("ignored"u8);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var tail = new Slot(5);

        await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await replica.WriteAsync(new Slot(1), payload, tail, recovery: false, cancellation.Token));
        await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await replica.WriteAsync(new Slot(1), payload, tail, recovery: true, cancellation.Token));
        await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await replica.ReadAsync(new Slot(0), tail, cancellation.Token));
        await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await replica.ReadTailAsync(tail, seal: false, cancellation.Token));
        await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await replica.ReadTailAsync(tail, seal: true, cancellation.Token));

        Assert.Equal(ReplicaReadCase.Missing, (await replica.ReadAsync(new Slot(1), TestContext.Current.CancellationToken)).Case);
        Assert.Equal(new LedgerReplicaTail(Slot.Zero, IsSealed: false), await replica.ReadTailAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(long.MaxValue)]
    public async Task AnInvalidAddressIsRejectedByReadsAndWrites(long address)
    {
        await using var replica = new InMemoryLedgerReplica(Options());
        using var payload = LedgerReplicaTestData.Buffer("value"u8);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            async () => await replica.WriteAsync(address, payload, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            async () => await replica.RecoveryWriteAsync(address, payload, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            async () => await replica.ReadAsync(address, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    public void AnInvalidWindowIsRejectedAtConstruction(int windowSize) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new InMemoryLedgerReplica(Options(windowSize)));

    [Fact]
    public void NullOptionsAreRejectedAtConstruction() =>
        Assert.Throws<ArgumentNullException>(() => new InMemoryLedgerReplica(null!));

    /// <summary>
    /// ledger-replicas.md §6.3: a returned buffer outlives the replica, whose operations then fail as disposed; disposal is
    /// idempotent.
    /// </summary>
    [Fact]
    public async Task AReturnedBufferOutlivesTheReplica()
    {
        var replica = new InMemoryLedgerReplica(Options());
        var expected = LedgerReplicaTestData.CreatePayload(ArcBufferWriter.MinimumPageSize + 9);
        using var payload = LedgerReplicaTestData.Buffer(expected);
        Assert.False(payload.AsReadOnlySequence().IsSingleSegment);
        await replica.WriteAsync(new Slot(2), payload, TestContext.Current.CancellationToken);
        var result = await replica.ReadAsync(new Slot(2), TestContext.Current.CancellationToken);
        Assert.Equal(ReplicaReadCase.Batch, result.Case);
        var returned = result.Batch;

        await replica.DisposeAsync();
        await replica.DisposeAsync();

        using (returned)
        {
            Assert.Equal(expected, returned.ToArray());
        }

        await Assert.ThrowsAsync<ObjectDisposedException>(
            async () => await replica.ReadAsync(new Slot(2), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ObjectDisposedException>(
            async () => await replica.WriteAsync(new Slot(3), payload, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ObjectDisposedException>(
            async () => await replica.ReadTailAsync(TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ObjectDisposedException>(
            async () => await replica.SealAsync(TestContext.Current.CancellationToken));
    }
}
