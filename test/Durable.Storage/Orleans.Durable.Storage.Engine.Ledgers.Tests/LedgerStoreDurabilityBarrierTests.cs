using System.Reflection;
using Orleans.Durable.Storage.Diagnostics;
using Orleans.Durable.Storage.Engine.Ledgers.Store;
using Orleans.Durable.Storage.Engine.Wal;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Ledgers.Tests;

/// <summary>
/// Checks the LedgerStore and LiveReplica type-enforced durability barriers (conventions.md §13, LS-I1, LS-I2, LS-I11):
/// default proof tokens are rejected, no proof type exposes a public constructor, and a failed WAL sync for a slot write
/// or drop never acknowledges a durable outcome.
/// </summary>
public sealed class LedgerStoreDurabilityBarrierTests : IDisposable
{
    private static readonly LedgerReplicaOptions Window = new() { WindowSize = 64 };

    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    public void Dispose() => _leaks.Dispose();

    [Fact]
    public void DefaultDurabilityProofTokensAreRejectedAndHaveNoPublicConstructors()
    {
        Type[] proofTypes =
        [
            typeof(LiveReplica.DurableSlotWrite),
            typeof(LiveReplica.DurableSeal),
            typeof(LedgerStore.DurableCreate),
            typeof(LedgerStore.DurableDrop),
        ];

        foreach (var proofType in proofTypes)
        {
            Assert.Empty(proofType.GetConstructors(BindingFlags.Public | BindingFlags.Instance));
        }

        var defaultSlot = default(LiveReplica.DurableSlotWrite);
        Assert.False(defaultSlot.IsValid);
        _ = Assert.Throws<InvalidOperationException>(() => defaultSlot.EnsureDurable());
        _ = Assert.Throws<InvalidOperationException>(() => defaultSlot.ToWrittenOutcome(ReplicaWriteOutcome.Written.Instance));

        var defaultSeal = default(LiveReplica.DurableSeal);
        Assert.False(defaultSeal.IsValid);
        _ = Assert.Throws<InvalidOperationException>(() => defaultSeal.EnsureDurable());
        _ = Assert.Throws<InvalidOperationException>(() => defaultSeal.ToSealedOutcome());
        _ = Assert.Throws<InvalidOperationException>(() => defaultSeal.ToTail(Slot.Zero));

        var defaultCreate = default(LedgerStore.DurableCreate);
        Assert.False(defaultCreate.IsValid);
        _ = Assert.Throws<InvalidOperationException>(() => defaultCreate.EnsureDurable());
        _ = Assert.Throws<InvalidOperationException>(() => LedgerStore.DurableCreate.FromWal(default));

        var defaultDrop = default(LedgerStore.DurableDrop);
        Assert.False(defaultDrop.IsValid);
        _ = Assert.Throws<InvalidOperationException>(() => defaultDrop.EnsureDurable());
        _ = Assert.Throws<InvalidOperationException>(() => LedgerStore.DurableDrop.FromWal(default));
    }

    [Fact(Timeout = 30_000)]
    public async Task FailedSlotWriteNeverAnswersWrittenAndStopsTheStore()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = LedgerStoreReplicaTests.Simulated();
        var replica = await root.CreateReplicaAsync(Window);
        var since = FatalErrors.ReportedCount;

        root.Storage.WriteFault = static (_, _, _, _) => new IOException("injected slot write failure");
        var failure = await Assert.ThrowsAnyAsync<IOException>(() => LedgerStoreReplicaTests.WriteAsync(replica, 0, [1, 2, 3]));
        Assert.True(FatalErrorHandler.IsReported(failure));
        Assert.NotEmpty(FatalErrors.TakeMine(since));

        await root.CrashAsync(replica, operatingSystem: true);
        await using var reopened = await root.OpenReplicaAsync(Window);
        Assert.Null(await LedgerReplicaTestData.ReadBytesAsync(reopened, 0, cancellationToken));
    }

    [Fact(Timeout = 30_000)]
    public async Task FailedDropNeverAnswersDroppedAndLeavesReplicaUndroppedOnRestart()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = LedgerStoreReplicaTests.Simulated();
        var replica = await root.CreateReplicaAsync(Window);
        Assert.Equal(ReplicaWriteCase.Written, (await LedgerStoreReplicaTests.WriteAsync(replica, 0, [7])).Case);

        var since = FatalErrors.ReportedCount;
        root.Storage.WriteFault = static (_, _, _, _) => new IOException("injected drop record failure");

        var dropFailure = await Assert.ThrowsAnyAsync<IOException>(() => replica.Store.DropReplicaAsync(replica.LedgerId, cancellationToken).AsTask());
        Assert.True(FatalErrorHandler.IsReported(dropFailure));
        Assert.NotEmpty(FatalErrors.TakeMine(since));

        await root.CrashAsync(replica, operatingSystem: true);
        await using var reopened = await root.OpenReplicaAsync(Window);
        Assert.Equal([7], await LedgerReplicaTestData.ReadBytesAsync(reopened, 0, cancellationToken));
        Assert.Equal(ReplicaWriteCase.Written, (await LedgerStoreReplicaTests.WriteAsync(reopened, 1, [8])).Case);
    }
}
