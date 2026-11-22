using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Ledgers.Tests;

#pragma warning disable CA2000 // The tests own the replicas a rejected quorum leaves them and dispose a quorum themselves to observe its disposal.

/// <summary>
/// Checks how a <see cref="LedgerQuorum"/> is composed (ledger-protocol.md §8.3): at least one reference-distinct replica,
/// any quorum <c>1 ≤ Q ≤ N</c> (LP-I4), a valid window, and ownership of its replicas, which it disposes once.
/// </summary>
public sealed class LedgerQuorumConstructionTests : IDisposable
{
    private static readonly LedgerReplicaOptions Window = new() { WindowSize = 4 };

    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    public void Dispose() => _leaks.Dispose();

    /// <summary>
    /// LP-I4: "A Ledger over N replicas has a quorum Q with 1 ≤ Q ≤ N", an even <c>N</c> included.
    /// </summary>
    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 1)]
    [InlineData(3, 2)]
    [InlineData(3, 3)]
    [InlineData(4, 2)]
    [InlineData(5, 3)]
    public async Task AnyQuorumFromOneToTheReplicaCountIsAccepted(int replicaCount, int q)
    {
        await using var quorum = LedgerQuorumTestData.Create(Replicas(replicaCount), q, Window);

        Assert.Equal(replicaCount, quorum.ReplicaCount);
        Assert.Equal(q, quorum.Quorum);
    }

    /// <summary>
    /// LP-I4: a quorum below 1 or above <c>N</c> is rejected, and the rejected composition takes no ownership: the
    /// caller still owns, and disposes, every replica.
    /// </summary>
    [Theory]
    [InlineData(3, 0)]
    [InlineData(3, 4)]
    [InlineData(1, -1)]
    [InlineData(2, 3)]
    public async Task AQuorumOutsideOneToTheReplicaCountIsRejected(int replicaCount, int quorum)
    {
        var replicas = Replicas(replicaCount);

        var error = Assert.Throws<ArgumentOutOfRangeException>(() => LedgerQuorumTestData.Create(replicas, quorum, Window));

        Assert.Equal("quorum", error.ParamName);
        await AssertUntouchedAsync(replicas);
    }

    [Fact]
    public async Task AnEmptyNullOrDuplicateReplicaSetIsRejected()
    {
        var first = new RecordingLedgerReplica();
        var second = new RecordingLedgerReplica();

        var nullCollection = Assert.Throws<ArgumentNullException>(() => LedgerQuorumTestData.Create(null!, 1, Window));
        var empty = Assert.Throws<ArgumentException>(() => LedgerQuorumTestData.Create([], 1, Window));
        var nullElement = Assert.Throws<ArgumentException>(() => LedgerQuorumTestData.Create([first, null!, second], 1, Window));
        var duplicate = Assert.Throws<ArgumentException>(() => LedgerQuorumTestData.Create([first, first, second], 1, Window));

        Assert.Equal("replicas", nullCollection.ParamName);
        Assert.Equal("replicas", empty.ParamName);
        Assert.Equal("replicas", nullElement.ParamName);
        Assert.Equal("replicas", duplicate.ParamName);
        await AssertUntouchedAsync([first, second]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task AnInvalidWindowOrNullOptionsAreRejected(int windowSize)
    {
        var replicas = Replicas(1);

        _ = Assert.Throws<ArgumentOutOfRangeException>(() => LedgerQuorumTestData.Create(replicas, 1, new LedgerReplicaOptions { WindowSize = windowSize }));
        _ = Assert.Throws<ArgumentNullException>(() => LedgerQuorumTestData.Create(replicas, 1, null!));

        await AssertUntouchedAsync(replicas);
    }

    /// <summary>
    /// The quorum owns its replicas: repeated disposal disposes each once, every replica's failure is reported, and a
    /// disposed quorum rejects every operation before it reaches a replica (ledger-protocol.md §6.3).
    /// </summary>
    [Fact]
    public async Task DisposingTheLedgerDisposesEveryReplicaOnce()
    {
        var failure = new InvalidOperationException("disposal failed");
        var failing = new RecordingLedgerReplica { DisposeAsyncException = failure };
        var replicas = new[] { new RecordingLedgerReplica(), failing, new RecordingLedgerReplica() };
        var quorum = LedgerQuorumTestData.Create(replicas, 2, Window);

        var error = await Assert.ThrowsAsync<AggregateException>(async () => await quorum.DisposeAsync());
        Assert.Same(failure, Assert.Single(error.InnerExceptions));
        _ = await Assert.ThrowsAsync<AggregateException>(async () => await quorum.DisposeAsync());
        Assert.All(replicas, static replica => Assert.Equal(1, replica.DisposeAsyncCount));

        using var payload = LedgerReplicaTestData.Buffer("value"u8);
        var token = TestContext.Current.CancellationToken;
        _ = await Assert.ThrowsAsync<ObjectDisposedException>(async () => await quorum.WriteAsync(new Slot(0), payload, token));
        _ = await Assert.ThrowsAsync<ObjectDisposedException>(async () => await quorum.ReadAsync(new Slot(0), token));
        _ = await Assert.ThrowsAsync<ObjectDisposedException>(async () => await quorum.CheckAsync(token));
        _ = await Assert.ThrowsAsync<ObjectDisposedException>(async () => await quorum.RecoverAsync(Slot.Zero, token));
        Assert.All(replicas, static replica =>
        {
            Assert.Empty(replica.WriteCalls);
            Assert.Empty(replica.ReadCalls);
            Assert.Empty(replica.ReadTailCalls);
        });
    }

    private static RecordingLedgerReplica[] Replicas(int count) => [.. Enumerable.Range(0, count).Select(static _ => new RecordingLedgerReplica())];

    /// <summary>
    /// Asserts that no operation and no disposal reached <paramref name="replicas"/>, then disposes them as their owner.
    /// </summary>
    private static async Task AssertUntouchedAsync(RecordingLedgerReplica[] replicas)
    {
        foreach (var replica in replicas)
        {
            Assert.Equal(0, replica.DisposeAsyncCount);
            Assert.Empty(replica.WriteCalls);
            Assert.Empty(replica.ReadCalls);
            Assert.Empty(replica.ReadTailCalls);
            await replica.DisposeAsync();
        }
    }
}
