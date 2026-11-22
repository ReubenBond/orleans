using Orleans.Durable.Storage.Engine.Buffers;
using Orleans.Durable.Storage.Engine.Ledgers.Memory;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Ledgers.Tests;

/// <summary>
/// A replica's byte-range reads (ledger-replicas.md §2.1).
/// </summary>
public sealed class LedgerReplicaRangedReadTests : IDisposable
{
    private static readonly LedgerReplicaOptions Options = new() { WindowSize = 4 };

    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    public void Dispose() => _leaks.Dispose();

    [Fact]
    public async Task AByteRangeReadReturnsTheRangePassesThroughMissingAndAnswersARangePastTheBatch()
    {
        var token = TestContext.Current.CancellationToken;
        await using var replica = new InMemoryLedgerReplica(Options);
        using var payload = Payload("0123456789");
        _ = await replica.WriteAsync(0, payload, token);
        _ = await replica.SealAsync(token);

        var range = await replica.ReadAsync(Slot.Zero, 3, 4, token);
        Assert.Equal(ReplicaReadCase.Batch, range.Case);
        using (var bytes = range.Batch)
        {
            Assert.Equal("3456"u8.ToArray(), bytes.ToArray());
        }

        Assert.Equal(ReplicaReadCase.Missing, (await replica.ReadAsync(new Slot(1), 0, 1, token)).Case);
        Assert.Equal(new ReplicaRead.OutsideBatch(10), (await replica.ReadAsync(Slot.Zero, 8, 3, token)).Value);
        _ = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await replica.ReadAsync(Slot.Zero, 0, 0, token));
    }

    private static ArcBuffer Payload(string value) => LedgerReplicaTestData.Buffer(System.Text.Encoding.UTF8.GetBytes(value));
}
