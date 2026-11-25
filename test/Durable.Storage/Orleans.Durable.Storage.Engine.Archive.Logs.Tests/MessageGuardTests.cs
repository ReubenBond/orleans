using Orleans.Durable.Storage.Engine.Archive.Logs.Control;
using Orleans.Durable.Storage.Engine.Ledgers;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Archive.Logs.Tests;

/// <summary>
/// LG-I15 guard in Archive Logs: <see cref="ArchiveLogDrainWorker"/> fails closed with
/// <see cref="ArchiveLogDrainBlockedException"/> (<see cref="ArchiveLogDrainBlockedCause.Corrupt"/>) on
/// unregistered <see cref="RowKind.Message"/> channels.
/// </summary>
public sealed class MessageGuardTests : IDisposable
{
    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _leaks.Dispose();

    [Fact]
    public async Task ArchiveLogDrainWorkerFailsClosedOnUnregisteredMessageChannel()
    {
        await using var world = await ArchiveLogWorld.StartAsync(Token);
        var ledger = world.Ledgers.OpenContaining([0]).Id;
        var channel = MessageChannel.Parse("app.events");
        var writer = world.Ledgers.Registry.TryGet(ledger)!;
        using (var batch = await writer.CreateBatchAsync(1, 1024, Token))
        {
            batch.BeginMessage(channel, 2, "ev"u8);
            Assert.IsType<ProposalOutcome.Durable>((await writer.ProposeAsync(batch, Token)).Value);
        }

        var t = await world.CloseAsync(ledger, Token);

        var ex = await Assert.ThrowsAsync<ArchiveLogDrainBlockedException>(() =>
            world.Worker().DrainAsync(world.Ledgers.Ledger(ledger), t, Token));
        Assert.Equal(ArchiveLogDrainBlockedCause.Corrupt, ex.Cause);
        Assert.IsType<ArchiveCorruptException>(ex.InnerException);
    }
}
