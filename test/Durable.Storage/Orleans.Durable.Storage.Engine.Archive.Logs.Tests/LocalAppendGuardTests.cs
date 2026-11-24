using Orleans.Durable.Storage.Engine.Archive.Logs.Control;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Archive.Logs.Tests;

/// <summary>
/// LG-I15 guard in Archive Logs: <see cref="ArchiveLogDrainWorker"/> fails closed with
/// <see cref="ArchiveLogDrainBlockedException"/> (<see cref="ArchiveLogDrainBlockedCause.Corrupt"/>) on
/// <see cref="RowKind.LocalAppend"/> without a drain barrier (until J2).
/// </summary>
public sealed class LocalAppendGuardTests : IDisposable
{
    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _leaks.Dispose();

    [Fact]
    public async Task ArchiveLogDrainWorkerFailsClosedOnLocalAppendWithoutBarrier()
    {
        await using var world = await ArchiveLogWorld.StartAsync(Token);
        var ledger = world.Ledgers.OpenContaining([0]).Id;
        var localKey = LedgerLocalKey.Encode(ledger, KeyRange.Root, LocalStreamName.Parse("orleans.wakeup"));
        var writer = world.Ledgers.Registry.TryGet(ledger)!;
        using (var batch = await writer.CreateBatchAsync(1, 1024, Token))
        {
            _ = batch.BeginRow(localKey, RowKind.LocalAppend, default, default, 2, "wk"u8);
            Assert.IsType<ProposalOutcome.Durable>((await writer.ProposeAsync(batch, Token)).Value);
        }

        var t = await world.CloseAsync(ledger, Token);

        var ex = await Assert.ThrowsAsync<ArchiveLogDrainBlockedException>(() =>
            world.Worker().DrainAsync(world.Ledgers.Ledger(ledger), t, Token));
        Assert.Equal(ArchiveLogDrainBlockedCause.Corrupt, ex.Cause);
        Assert.IsType<ArchiveCorruptException>(ex.InnerException);
    }
}
