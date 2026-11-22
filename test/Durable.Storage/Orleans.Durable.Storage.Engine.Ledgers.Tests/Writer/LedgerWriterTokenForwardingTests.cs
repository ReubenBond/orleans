using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Ledgers.Tests.Writer;

#pragma warning disable CA2000 // Each replica double passes to the primary's Ledger, which owns and disposes it.

public sealed class LedgerWriterTokenForwardingTests : IDisposable
{
    private readonly ArcBufferLeakCheck _leaks = ArcBufferLeakCheck.Start();

    public void Dispose() => _leaks.Dispose();

    [Fact]
    public async Task Append_AwaitsEachStepAndForwardsTheRequiredTokensWhenNotSealed()
    {
        var writeGate = new AsyncGate();
        var writeCompleted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var steps = new SequencerGates();
        var writeTokens = new List<CancellationToken>();
        var writeAddresses = new List<long>();
        var dataReplica = new DelegatingLedgerReplica
        {
            Write = async (address, payload, contiguousTail, recovery, cancellationToken) =>
            {
                Assert.True(steps.AcquireCompleted);
                Assert.Equal(Slot.Zero, contiguousTail);
                Assert.False(recovery);
                writeAddresses.Add(address.Value);
                writeTokens.Add(cancellationToken);
                Assert.Equal("value"u8.ToArray(), payload.ToArray());
                await writeGate.EnterAndWaitAsync();
                writeCompleted.SetResult();
                return ReplicaAnswers.Written;
            },
        };
        await using var log = WriterTestData.CreateLog(dataReplica, windowSize: 1);
        steps.Install(log.Sequencer);
        using var caller = new CancellationTokenSource();
        using var payload = WriterTestData.CreatePayload("value"u8);

        var append = log.AppendAsync(payload, caller.Token).AsTask();

        await steps.AcquireGate.WaitUntilEnteredAsync();
        Assert.False(writeGate.IsEntered);
        steps.AcquireGate.Release();

        await writeGate.WaitUntilEnteredAsync();
        Assert.True(steps.AcquireCompleted);
        Assert.False(steps.CompleteGate.IsEntered);
        writeGate.Release();

        await steps.CompleteGate.WaitUntilEnteredAsync();
        Assert.True(writeCompleted.Task.IsCompleted);
        steps.CompleteGate.Release();

        var result = await append;

        Assert.Equal(WriterTestData.Appended(0), result);
        Assert.Equal([caller.Token], steps.AcquireTokens);

        // Cancelling stops the caller waiting, never the write (ledger-protocol.md §2.4), so the write and the completion
        // at which the append linearizes use the writer's lifetime token, which only disposal cancels: the write's replica
        // call runs under a token linked to it, which the caller's cancellation does not reach.
        var writeToken = Assert.Single(writeTokens);
        Assert.NotEqual(caller.Token, writeToken);
        Assert.True(writeToken.CanBeCanceled);
        Assert.Equal([Slot.Zero.Value], writeAddresses);
        var completeToken = Assert.Single(steps.CompleteTokens);
        Assert.NotEqual(caller.Token, completeToken);
        Assert.True(completeToken.CanBeCanceled);
        await caller.CancelAsync();
        Assert.False(writeToken.IsCancellationRequested);
        Assert.False(completeToken.IsCancellationRequested);
    }

    [Fact]
    public async Task Append_WhenWriteReturnsSealed_StopsTheLifetimeAndNeverCompletesTheSlot()
    {
        var steps = new SequencerGates();
        var dataReplica = new DelegatingLedgerReplica
        {
            Write = (_, _, _, _, _) => ValueTask.FromResult(ReplicaAnswers.Sealed),
        };
        await using var log = WriterTestData.CreateLog(dataReplica, windowSize: 1);
        steps.Install(log.Sequencer);
        using var caller = new CancellationTokenSource();
        using var payload = WriterTestData.CreatePayload("value"u8);

        var append = log.AppendAsync(payload, caller.Token).AsTask();
        await steps.AcquireGate.WaitUntilEnteredAsync();
        steps.AcquireGate.Release();

        var result = await append;

        Assert.Equal(WriterTestData.Sealed(0), result);
        Assert.Equal([SequencerStopReason.Sealing], log.StopReasons);
        Assert.Empty(steps.CompleteTokens);
    }

    /// <summary>
    /// A coalesced open check (LP-I9, ledgers.md §8.4) cancels a waiting caller when that caller's token fires without
    /// cancelling the shared replica check for other callers, while disposing the ledger cancels the replica's token.
    /// </summary>
    [Fact]
    public async Task ReadTail_CancelsCallerWaitWithoutCancellingSharedReplicaCheck()
    {
        using var caller = new CancellationTokenSource();
        var replicaEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var replicaRelease = new TaskCompletionSource<ReplicaTailRead>(TaskCreationOptions.RunContinuationsAsynchronously);
        var replicaTokenObserved = CancellationToken.None;
        var dataReplica = new DelegatingLedgerReplica
        {
            ReadTail = (_, _, cancellationToken) =>
            {
                replicaTokenObserved = cancellationToken;
                _ = replicaEntered.TrySetResult();
                return new ValueTask<ReplicaTailRead>(replicaRelease.Task);
            },
        };
        await using var log = WriterTestData.CreateLog(dataReplica, windowSize: 4);

        var waiting = log.ReadTailAsync(caller.Token).AsTask();
        await replicaEntered.Task.WaitAsync(TestContext.Current.CancellationToken);
        await caller.CancelAsync();

        var cancelled = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.Equal(caller.Token, cancelled.CancellationToken);
        Assert.False(replicaTokenObserved.IsCancellationRequested);

        _ = replicaRelease.TrySetResult(new LedgerReplicaTail(new Slot(6), IsSealed: true));
        var result = await log.ReadTailAsync(TestContext.Current.CancellationToken);
        Assert.Equal(new LedgerTail(new Slot(6), IsSealed: true), result);
    }

    /// <summary>
    /// LP-I11, LR-I6: a contiguous-tail read and a seal carry the known contiguous tail to the replica, which the
    /// contiguous-tail read raises from the replica's report.
    /// </summary>
    [Fact]
    public async Task ReadTailAndSeal_ForwardTheKnownContiguousTailToTheReplica()
    {
        var contiguousTails = new List<(string Operation, Slot ContiguousTail)>();
        var dataReplica = new DelegatingLedgerReplica
        {
            ReadTail = (contiguousTail, seal, _) =>
            {
                contiguousTails.Add((seal ? "seal" : "readTail", contiguousTail));
                return ValueTask.FromResult<ReplicaTailRead>(seal ? new LedgerReplicaTail(contiguousTail, IsSealed: true) : new LedgerReplicaTail(new Slot(5), IsSealed: false));
            },
        };
        await using var log = WriterTestData.CreateLog(dataReplica, windowSize: 4);

        var tail = await log.ReadTailAsync(TestContext.Current.CancellationToken);
        var end = (await log.RecoverAsync(TestContext.Current.CancellationToken)).End;

        Assert.Equal(new LedgerTail(new Slot(5), IsSealed: false), tail);
        Assert.Equal(new Slot(5), end);
        Assert.Equal([("readTail", Slot.Zero), ("seal", new Slot(5))], contiguousTails);
    }

    /// <summary>A read issues one replica read at the slot, under a token the caller's cancels.</summary>
    [Fact]
    public async Task Read_IssuesOneReplicaReadAtTheSlotAndForwardsCallerToken()
    {
        using var caller = new CancellationTokenSource();
        var reads = new List<(long Address, long ContiguousTail, bool CancelledWithTheCaller)>();
        var dataReplica = new DelegatingLedgerReplica
        {
            Read = (address, contiguousTail, cancellationToken) =>
            {
                caller.Cancel();
                reads.Add((address.Value, contiguousTail.Value, cancellationToken.IsCancellationRequested));
                return ValueTask.FromResult(ReplicaAnswers.Missing);
            },
        };
        await using var log = WriterTestData.CreateLog(dataReplica, windowSize: 4);
        _ = log.Ledger.RaiseKnownContiguousTail(new Slot(2));

        var result = await log.ReadAsync(new Slot(3), caller.Token);

        Assert.Equal(ReplicaReadCase.Missing, result.Case);
        Assert.Equal([(3L, 2L, true)], reads);
    }

    /// <summary>
    /// A recovery stops when its caller cancels: the caller's cancellation reaches the replica's seal, which the recovery
    /// sends with a token linked to the caller's (LP-I7). The writer stopped its lifetime before the seal (LP-I14).
    /// </summary>
    [Fact]
    public async Task Recover_ForwardsCallerCancellationToTheReplicaAfterStoppingTheLifetime()
    {
        var sealStarted = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        PrimaryLedger? primary = null;
        var dataReplica = new DelegatingLedgerReplica
        {
            ReadTail = async (contiguousTail, seal, cancellationToken) =>
            {
                Assert.True(seal);
                Assert.Equal([SequencerStopReason.Sealing], primary!.StopReasons);
                _ = sealStarted.TrySetResult(cancellationToken);
                await Task.Delay(Timeout.InfiniteTimeSpan, TimeProvider.System, cancellationToken);
                return new LedgerReplicaTail(contiguousTail, IsSealed: true);
            },
        };
        await using var log = WriterTestData.CreateLog(dataReplica, windowSize: 1);
        primary = log;
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        var recovery = log.RecoverAsync(caller.Token).AsTask();
        var sealToken = await sealStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.False(sealToken.IsCancellationRequested);
        await caller.CancelAsync();

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => recovery);
        Assert.True(sealToken.IsCancellationRequested);
        Assert.Equal([SequencerStopReason.Sealing], log.StopReasons);
    }

    private sealed class AsyncGate
    {
        private readonly TaskCompletionSource _entered = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource _release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal bool IsEntered => _entered.Task.IsCompleted;

        internal async ValueTask EnterAndWaitAsync()
        {
            _entered.SetResult();
            await _release.Task.WaitAsync(TestContext.Current.CancellationToken);
        }

        internal Task WaitUntilEnteredAsync() =>
            _entered.Task.WaitAsync(TestContext.Current.CancellationToken);

        internal void Release() => _release.SetResult();
    }

    /// <summary>
    /// Holds a lifetime's acquisition and completion at gates through its test seams, recording the tokens each receives.
    /// </summary>
    private sealed class SequencerGates
    {
        private int _acquireCompleted;

        internal AsyncGate AcquireGate { get; } = new();

        internal bool AcquireCompleted => Volatile.Read(ref _acquireCompleted) != 0;

        internal List<CancellationToken> AcquireTokens { get; } = [];

        internal AsyncGate CompleteGate { get; } = new();

        internal List<CancellationToken> CompleteTokens { get; } = [];

        internal void Install(Sequencer sequencer)
        {
            sequencer.BeforeAcquire = async cancellationToken =>
            {
                AcquireTokens.Add(cancellationToken);
                await AcquireGate.EnterAndWaitAsync();
            };
            var observer = sequencer.Observer;
            sequencer.Observer = step =>
            {
                observer?.Invoke(step);
                if (step.Kind == SequencerStepKind.Acquired)
                {
                    Volatile.Write(ref _acquireCompleted, 1);
                }
            };
            sequencer.BeforeComplete = async (_, cancellationToken) =>
            {
                CompleteTokens.Add(cancellationToken);
                await CompleteGate.EnterAndWaitAsync();
            };
        }
    }
}
