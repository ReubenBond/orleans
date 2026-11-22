using Orleans.Durable.Storage.Engine.Ledgers.Tests.Writer;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Ledgers.Tests;

#pragma warning disable CA2000 // The Ledger under test disposes its replicas.

/// <summary>
/// Checks how a <see cref="Ledger"/>'s disposal disposes its replicas (ledger-protocol.md §6.3).
/// </summary>
public sealed class LedgerDisposalTests : IDisposable
{
    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    public void Dispose() => _leaks.Dispose();

    /// <summary>
    /// Disposal disposes every replica once, past failures, and reports the failures together.
    /// </summary>
    [Fact]
    public async Task DisposeAsyncDisposesEveryReplicaOnceAndAggregatesTheirFailures()
    {
        var firstFailure = new InvalidOperationException("first failure");
        var secondFailure = new NotSupportedException("second failure");
        var first = new RecordingLedgerReplica { DisposeAsyncException = firstFailure };
        var second = new RecordingLedgerReplica { DisposeAsyncHandler = () => ValueTask.FromException(secondFailure) };
        var last = new RecordingLedgerReplica();
        var ledger = WriterTestData.CreateLedger([first, second, last], quorum: 2, windowSize: 4);

        var error = await Assert.ThrowsAsync<AggregateException>(async () => await ledger.DisposeAsync());

        Assert.Equal(new Exception[] { firstFailure, secondFailure }, error.InnerExceptions);
        Assert.Equal(1, first.DisposeAsyncCount);
        Assert.Equal(1, second.DisposeAsyncCount);
        Assert.Equal(1, last.DisposeAsyncCount);
    }

    /// <summary>
    /// Disposal resumes off the caller's synchronization context, so a caller whose context never runs a continuation,
    /// as a blocked owner's does, still sees every replica disposed.
    /// </summary>
    [Fact]
    public async Task DisposeAsyncDoesNotResumeOnTheCallingSynchronizationContext()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slow = new RecordingLedgerReplica { DisposeAsyncHandler = () => new ValueTask(gate.Task) };
        var next = new RecordingLedgerReplica();
        var last = new RecordingLedgerReplica();
        var ledger = WriterTestData.CreateLedger([slow, next, last], quorum: 2, windowSize: 4);
        Task? disposal = null;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(
            () =>
            {
                SynchronizationContext.SetSynchronizationContext(new NonPumpingSynchronizationContext());
                disposal = ledger.DisposeAsync().AsTask();
                started.SetResult();
            })
        {
            IsBackground = true,
        };

        thread.Start();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        gate.SetResult();

        await disposal!.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(1, next.DisposeAsyncCount);
        Assert.Equal(1, last.DisposeAsyncCount);
    }

    private sealed class NonPumpingSynchronizationContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state)
        {
        }
    }
}
