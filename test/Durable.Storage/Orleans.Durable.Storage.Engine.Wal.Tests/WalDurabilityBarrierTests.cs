using System.Reflection;
using Orleans.Durable.Storage.Diagnostics;
using Orleans.Durable.Storage.Engine.Wal.Simulation;
using Orleans.Durable.Storage.Testing;
using static Orleans.Durable.Storage.Engine.Wal.Tests.WalTestHelpers;

namespace Orleans.Durable.Storage.Engine.Wal.Tests;

/// <summary>
/// Checks the WAL's type-enforced durability barrier (conventions.md §13, WAL-I1, WAL-I8): a default or invalid
/// <see cref="WalDurableRecord"/> is rejected, callers cannot publicly construct one, and a write whose storage sync
/// fails never invokes <see cref="WalWriteRequest.OnDurable"/> and never advances <see cref="WriteAheadLog.DurableFrontier"/>.
/// </summary>
public sealed class WalDurabilityBarrierTests : IDisposable
{
    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    public void Dispose() => _leaks.Dispose();

    [Fact]
    public void DefaultAndInvalidWalDurableRecordsAreRejectedAndHaveNoPublicConstructor()
    {
        Assert.Empty(typeof(WalDurableRecord).GetConstructors(BindingFlags.Public | BindingFlags.Instance));

        var uninitialized = default(WalDurableRecord);
        Assert.False(uninitialized.IsValid);
        _ = Assert.Throws<InvalidOperationException>(() => uninitialized.EnsureDurable());
        _ = Assert.Throws<InvalidOperationException>(() => uninitialized.Position);
        _ = Assert.Throws<InvalidOperationException>(() => uninitialized.Page);

        _ = Assert.Throws<ArgumentOutOfRangeException>(() => new WalDurableRecord(default, new WalPage(1, 0)));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => new WalDurableRecord(new WalPosition(1, 4096), default));

        var valid = new WalDurableRecord(new WalPosition(1, 4096), new WalPage(1, 0));
        Assert.True(valid.IsValid);
        valid.EnsureDurable();
        Assert.Equal(new WalPosition(1, 4096), valid.Position);
        Assert.Equal(new WalPage(1, 0), valid.Page);
    }

    [Fact(Timeout = 30_000)]
    public async Task FailedWriteSyncNeverCallsOnDurableAndNeverAdvancesDurableFrontier()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var storage = new SimulatedWalStorage { HoldWrites = true };
        var wal = await WriteAheadLog.CreateAsync(storage, LogPath, Options(), cancellationToken);
        var initialFrontier = wal.DurableFrontier;

        using var first = new BarrierTrackingRequest(Buffer([1, 2, 3, 4]));
        await wal.WriteAsync(first, cancellationToken);
        await WalFormatTests.WaitForPendingAsync(storage, 1);

        Assert.Equal(0, first.DurableCalls);
        Assert.Equal(0, first.FailedCalls);
        Assert.Equal(initialFrontier, wal.DurableFrontier);

        storage.HoldWrites = false;
        Assert.True(storage.CompleteWrite());
        var firstDurable = await first.Completion;
        Assert.True(firstDurable.IsValid);
        Assert.Equal(1, first.DurableCalls);
        Assert.Equal(0, first.FailedCalls);

        var frontierAfterFirst = wal.DurableFrontier;
        Assert.True(frontierAfterFirst > initialFrontier);

        var since = FatalErrors.ReportedCount;
        var injected = new IOException("injected WAL sync failure");
        storage.WriteFault = (_, _, _, _) => injected;

        using var second = new BarrierTrackingRequest(Buffer([5, 6, 7, 8]));
        await wal.WriteAsync(second, cancellationToken);

        var failure = await Assert.ThrowsAnyAsync<IOException>(() => second.Completion);
        Assert.Equal(0, second.DurableCalls);
        Assert.Equal(1, second.FailedCalls);
        Assert.Same(injected, AssertReported(since, FatalErrorSite.HandleWriteGather, LogFile(1)).Failure.Exception);
        Assert.True(FatalErrorHandler.IsReported(failure));
        await AssertAnswersNothingAsync(wal);
        await wal.DisposeAsync();
    }

    private sealed class BarrierTrackingRequest : WalWriteRequest, IDisposable
    {
        private readonly TaskCompletionSource<WalDurableRecord> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public BarrierTrackingRequest(Engine.Buffers.ArcBuffer payload) => Payload = payload;

        public Task<WalDurableRecord> Completion => _completion.Task;

        public int DurableCalls { get; private set; }

        public int FailedCalls { get; private set; }

        public void Dispose() => Payload.Dispose();

        protected internal override void OnDurable(WalDurableRecord record)
        {
            record.EnsureDurable();
            DurableCalls++;
            _ = _completion.TrySetResult(record);
        }

        protected internal override void OnFailed(Exception error)
        {
            FailedCalls++;
            _ = _completion.TrySetException(error);
        }
    }
}
