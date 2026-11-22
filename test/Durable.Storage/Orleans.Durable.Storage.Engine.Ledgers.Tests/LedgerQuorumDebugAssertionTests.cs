using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Ledgers.Memory;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Ledgers.Tests;

/// <summary>
/// A debug assertion which fails under a quorum's replica is recorded even though the quorum counts its exception as
/// that replica's failure and acknowledges the write, so it fails its test (docs/spec/verification.md §11.2). The
/// replica double defines <c>DEBUG</c>, so this runs in every configuration.
/// </summary>
public sealed class LedgerQuorumDebugAssertionTests : IDisposable
{
    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    public void Dispose() => _leaks.Dispose();

    [Fact]
    public async Task AnAssertionUnderAReplicaIsRecordedThoughTheQuorumAcknowledgesTheWrite()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var test = TestContext.Current.Test;
        var since = DebugAssertions.RaisedCount;
        var options = new LedgerReplicaOptions { WindowSize = 8 };
        var replicas = Enumerable.Range(0, 3)
            .Select(index =>
            {
                var replica = new InMemoryLedgerReplica(options);
                return index == 0 ? (ILedgerReplica)new AssertingLedgerReplica(replica) : replica;
            })
            .ToArray();
        await using (var quorum = LedgerQuorumTestData.Create(replicas, 2, options))
        {
            using var writer = new ArcBufferWriter();
            writer.Write("value"u8);
            using var payload = writer.ConsumeSlice(writer.Length);

            Assert.Equal(ReplicaWriteCase.Written, (await quorum.WriteAsync(Slot.Zero, payload, cancellationToken)).Case);
        }

        var assertion = Assert.Single(DebugAssertions.TakeRaisedBy(test, since));
        Assert.Equal($"Debug assertion failed: {AssertingLedgerReplica.Message}", assertion.Message);
        Assert.StartsWith($"{typeof(AssertingLedgerReplica).FullName}.{nameof(AssertingLedgerReplica.WriteAsync)}+IL_", assertion.SiteKey, StringComparison.Ordinal);
        Assert.Empty(DebugAssertions.TakeRaisedBy(test));
    }
}
