using System.Buffers;
using Orleans.Serialization.Buffers;
using Xunit;

namespace Orleans.Journaling.Tests;

public partial class StateManagerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetainedStorage_ManagerUsesCapabilityAndPreservesReplayAfterManyCaptures(bool snapshot)
    {
        var storage = new RetainedProbeStorage { IsCompactionRequested = snapshot };
        var manager = CreateTestSystem(storage).Manager;
        var value = new DurableValue<int>("value", manager, CreateValueCodec<int>());
        await manager.InitializeAsync(TestContext.Current.CancellationToken);
        for (var i = 1; i <= 500; i++)
        {
            value.Value = i;
            await manager.WriteStateAsync(TestContext.Current.CancellationToken);
            Assert.Equal(0, manager.PendingWriteByteCount);
        }
        Assert.Equal(500, storage.RetainedCalls);
        Assert.Same(storage.Observation.First, storage.Inner.Storage.Segments[^1].First);
        Assert.Equal(snapshot ? 1 : 500, storage.Inner.GetMemoryStatistics().Segments);
        // A large number of captures must not imply a new 16KiB page per capture.
        var retainedPayload = storage.Inner.Segments.Sum(static segment => segment.Length);
        Assert.Equal((retainedPayload + ArcBufferWriter.MinimumPageSize - 1) / ArcBufferWriter.MinimumPageSize,
            storage.Inner.GetMemoryStatistics().RetainedPages);
        await manager.DisposeAsync();
        await using (var recovered = CreateTestSystem(storage.Inner).Manager)
        {
            var recoveredValue = new DurableValue<int>("value", recovered, CreateValueCodec<int>());
            await recovered.InitializeAsync(TestContext.Current.CancellationToken);
            Assert.Equal(500, recoveredValue.Value);
        }
        await storage.Inner.DeleteAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, Assert.IsType<ArcBufferPage>(storage.Observation.First).ReferenceCount); // only the probe's independently owned pin
        Assert.Equal(0, storage.Inner.GetMemoryStatistics().RetainedCapacity);
        storage.ReleaseObservation();
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task RetainedStorage_FailedAndAmbiguousWritesReleaseManagerPinAndReplayActualOutcome(bool snapshot, bool committed, bool canceled)
    {
        var storage = new RetainedProbeStorage();
        var manager = CreateTestSystem(storage).Manager;
        var value = new DurableValue<int>("value", manager, CreateValueCodec<int>());
        var lifecycle = new StorageOwnershipAckState();
        manager.RegisterStateMachine("ack", lifecycle);
        await manager.InitializeAsync(TestContext.Current.CancellationToken);
        value.Value = 1;
        await manager.WriteStateAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, lifecycle.WriteCompletedCount);
        value.Value = 2;
        storage.IsCompactionRequested = snapshot;
        Exception expected = canceled
            ? new OperationCanceledException(new CancellationToken(canceled: true))
            : new IOException("Retained write acknowledgement failed.");
        storage.NextFailure = expected;
        storage.CommitBeforeFailure = committed;
        Assert.Same(expected, await Record.ExceptionAsync(() => manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask()));
        Assert.Equal(1, lifecycle.WriteCompletedCount);
        Assert.Equal(snapshot ? 0 : storage.Observation.Length, manager.PendingWriteByteCount);
        await manager.DisposeAsync();
        await using (var recovered = CreateTestSystem(storage.Inner).Manager)
        {
            var recoveredValue = new DurableValue<int>("value", recovered, CreateValueCodec<int>());
            // Bind the emitted ACK stream using the same test state so its raw entry is replayable.
            recovered.RegisterStateMachine("ack", new StorageOwnershipAckState());
            await recovered.InitializeAsync(TestContext.Current.CancellationToken);
            Assert.Equal(committed ? 2 : 1, recoveredValue.Value);
        }
        await storage.Inner.DeleteAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, Assert.IsType<ArcBufferPage>(storage.Observation.First).ReferenceCount);
        Assert.Equal(0, storage.Inner.GetMemoryStatistics().RetainedPages);
        storage.ReleaseObservation();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetainedStorage_OwnerDisposalDrainsActualIoAndCanceledWaitDoesNotReleaseBuffer(bool snapshot)
    {
        var storage = new RetainedProbeStorage { IsCompactionRequested = snapshot, Block = true };
        var manager = CreateTestSystem(storage).Manager;
        var value = new DurableValue<int>("value", manager, CreateValueCodec<int>());
        await manager.InitializeAsync(TestContext.Current.CancellationToken);
        value.Value = 42;
        using var caller = new CancellationTokenSource();
        var write = manager.WriteStateAsync(caller.Token).AsTask();
        await WaitFor(storage.Entered.Task);
        var sourceRefCount = Assert.IsType<ArcBufferPage>(storage.Observation.First).ReferenceCount;
        var expectedBytes = storage.Observation.ToArray();
        caller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => write);
        Assert.Equal(sourceRefCount, Assert.IsType<ArcBufferPage>(storage.Observation.First).ReferenceCount);
        var disposal = manager.DisposeAsync().AsTask();
        Assert.False(disposal.IsCompleted);
        Assert.Equal(sourceRefCount, Assert.IsType<ArcBufferPage>(storage.Observation.First).ReferenceCount);
        Assert.Equal(expectedBytes, storage.Observation.ToArray());
        storage.Release.SetResult();
        await WaitFor(disposal);
        Assert.Equal(expectedBytes, Assert.Single(storage.Inner.Segments));
        Assert.Equal(2, Assert.IsType<ArcBufferPage>(storage.Observation.First).ReferenceCount); // storage + observation; all manager/writer pins drained
        await using (var recovered = CreateTestSystem(storage.Inner).Manager)
        {
            var recoveredValue = new DurableValue<int>("value", recovered, CreateValueCodec<int>());
            await recovered.InitializeAsync(TestContext.Current.CancellationToken);
            Assert.Equal(42, recoveredValue.Value);
        }
        await storage.Inner.DeleteAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, Assert.IsType<ArcBufferPage>(storage.Observation.First).ReferenceCount);
        storage.ReleaseObservation();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task RetainedStorage_ThrowingShutdownCallbackDrainsIoAndPreservesOriginalFailure(bool snapshot, bool failStorage)
    {
        var expected = new IOException("Storage cancellation callback failed.");
        var storageFailure = new IOException("Storage acknowledgement failed.");
        var storage = new RetainedProbeStorage
        {
            IsCompactionRequested = snapshot,
            Block = true,
            CancellationFailure = expected,
            NextFailure = failStorage ? storageFailure : null,
            CommitBeforeFailure = true
        };
        var manager = CreateTestSystem(storage).Manager;
        var value = new DurableValue<int>("value", manager, CreateValueCodec<int>());
        await manager.InitializeAsync(TestContext.Current.CancellationToken);
        value.Value = 42;
        var write = manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask();
        await WaitFor(storage.Entered.Task);
        var baseline = Assert.IsType<ArcBufferPage>(storage.Observation.First).ReferenceCount;
        var bytes = storage.Observation.ToArray();

        var disposal = manager.DisposeAsync().AsTask();
        try
        {
            Assert.False(disposal.IsCompleted);
            Assert.Equal(baseline, Assert.IsType<ArcBufferPage>(storage.Observation.First).ReferenceCount);
            Assert.Equal(bytes, storage.Observation.ToArray());
        }
        finally
        {
            storage.Release.TrySetResult();
        }

        if (failStorage)
        {
            Assert.Same(storageFailure, await Record.ExceptionAsync(() => WaitFor(write)));
        }
        else
        {
            await WaitFor(write);
        }

        var failure = await Assert.ThrowsAsync<AggregateException>(() => WaitFor(disposal));
        Assert.Same(expected, Assert.Single(failure.InnerExceptions));
        Assert.Equal(2, Assert.IsType<ArcBufferPage>(storage.Observation.First).ReferenceCount);
        Assert.Equal(bytes, Assert.Single(storage.Inner.Segments));
        await storage.Inner.DeleteAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, Assert.IsType<ArcBufferPage>(storage.Observation.First).ReferenceCount);
        storage.ReleaseObservation();
    }

    private sealed class StorageOwnershipAckState : IStateMachine
    {
        public int WriteCompletedCount { get; private set; }
        public void Reset(JournalStreamWriter writer) { }
        public void WritePendingEntries(JournalStreamWriter writer)
        {
            using var entry = writer.BeginEntry();
            entry.Writer.GetSpan(1)[0] = 1;
            entry.Writer.Advance(1);
            entry.Commit();
        }
        public void WriteSnapshot(JournalStreamWriter writer) => WritePendingEntries(writer);
        public void OnWriteCompleted() => WriteCompletedCount++;
        public void ReplayEntry(JournalEntry entry, JournalReplayContext context)
        {
            Assert.Equal(new byte[] { 1 }, entry.Reader.ToArray());
            entry.Reader.Skip(entry.Reader.Length);
        }
    }

    private sealed class RetainedProbeStorage : IJournalStorage
    {
        public VolatileJournalStorage Inner { get; } = new(OrleansBinaryJournalFormat.JournalFormatKey);
        public bool IsCompactionRequested { get; set; }
        public bool Block { get; set; }
        public bool CommitBeforeFailure { get; set; }
        public Exception? NextFailure { get; set; }
        public Exception? CancellationFailure { get; set; }
        public TaskCompletionSource Entered { get; } = NewSignal();
        public TaskCompletionSource Release { get; } = NewSignal();
        public ArcBuffer Observation;
        public int RetainedCalls { get; private set; }
        private bool _hasObservation;

        public void ReleaseObservation()
        {
            if (_hasObservation) Observation.Dispose();
            _hasObservation = false;
        }

        public ValueTask ReadAsync(IJournalStorageConsumer consumer, CancellationToken cancellationToken) => Inner.ReadAsync(consumer, cancellationToken);
        public ValueTask DeleteAsync(CancellationToken cancellationToken) => Inner.DeleteAsync(cancellationToken);
        public ValueTask AppendAsync(ArcBuffer value, CancellationToken cancellationToken) => Write(value, replace: false, cancellationToken);
        public ValueTask ReplaceAsync(ArcBuffer value, CancellationToken cancellationToken) => Write(value, replace: true, cancellationToken);

        private async ValueTask Write(ArcBuffer value, bool replace, CancellationToken cancellationToken)
        {
            using var registration = CancellationFailure is { } cancellationFailure
                ? cancellationToken.Register(() => throw cancellationFailure)
                : default;
            ReleaseObservation();
            Observation = value.Slice(0);
            _hasObservation = true;
            RetainedCalls++;
            Entered.TrySetResult();
            // Deliberately ignore cancellation after entry, as a real provider can complete/commit
            // after owner cancellation. Actual completion, not the cancellation request, is authoritative.
            if (Block) await Release.Task;
            var failure = NextFailure;
            NextFailure = null;
            if (failure is not null && !CommitBeforeFailure) throw failure;
            if (replace) await Inner.ReplaceAsync(value, CancellationToken.None);
            else await Inner.AppendAsync(value, CancellationToken.None);
            if (failure is not null) throw failure;
        }
    }
}
