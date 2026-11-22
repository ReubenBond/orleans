// Copyright (c) .NET Foundation and Contributors.
// Licensed under the MIT license.
// Adapted from Orleans' AdmissionGateTests, dotnet/orleans test/Orleans.Core.Tests/Utils/AdmissionGateTests.cs at
// commit 8c4887aba920b9250ad2914a039777a43781603f.

namespace Orleans.Durable.Storage.Engine.Threading.Tests;

public sealed class AdmissionGateTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Fact]
    public void TryEnter_AfterAllTokensAreDisposed_AdmitsUntilClosed()
    {
        var gate = new AdmissionGate();
        using (var admission = gate.TryEnter())
        {
            Assert.True(admission.Entered);
        }

        Task drained;
        using (var admission = gate.TryEnter())
        {
            Assert.True(admission.Entered);
            drained = gate.CloseAsync();
            Assert.False(drained.IsCompleted);
        }

        Assert.True(drained.IsCompletedSuccessfully);
        using var rejected = gate.TryEnter();
        Assert.False(rejected.Entered);
    }

    [Fact]
    public void TryEnterUnscoped_AfterExit_AdmitsUntilClosed()
    {
        var gate = new AdmissionGate();
        Assert.True(gate.TryEnterUnscoped());
        gate.Exit();

        Assert.True(gate.TryEnterUnscoped());
        var drained = gate.CloseAsync();
        Assert.False(gate.TryEnterUnscoped());
        Assert.False(drained.IsCompleted);

        gate.Exit();
        Assert.True(drained.IsCompletedSuccessfully);
        Assert.False(gate.TryEnterUnscoped());
        Assert.Same(drained, gate.CloseAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ScopedAndUnscopedAdmissions_DrainTogether(bool exitUnscopedFirst)
    {
        var gate = new AdmissionGate();
        Assert.True(gate.TryEnterUnscoped());
        Task drained;
        using (var scoped = gate.TryEnter())
        {
            Assert.True(scoped.Entered);
            drained = gate.CloseAsync();
            Assert.False(gate.TryEnterUnscoped());
            using var rejected = gate.TryEnter();
            Assert.False(rejected.Entered);

            if (exitUnscopedFirst)
            {
                gate.Exit();
            }

            Assert.False(drained.IsCompleted);
        }

        if (!exitUnscopedFirst)
        {
            Assert.False(drained.IsCompleted);
            gate.Exit();
        }

        Assert.True(drained.IsCompletedSuccessfully);
        Assert.Same(drained, gate.CloseAsync());
    }

    [Fact]
    public void CloseAsync_WhenEmpty_CompletesAndRemainsClosed()
    {
        var gate = new AdmissionGate();

        var drained = gate.CloseAsync();

        Assert.True(drained.IsCompletedSuccessfully);
        for (var i = 0; i < 3; i++)
        {
            using var rejected = gate.TryEnter();
            Assert.False(rejected.Entered);
            Assert.Same(drained, gate.CloseAsync());
            Assert.True(drained.IsCompletedSuccessfully);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    public void CloseAsync_WithOutstandingTokens_CompletesAfterFinalDisposal(int count)
    {
        var gate = new AdmissionGate();
        var admissions = new AdmissionGate.Admission[count];
        for (var i = 0; i < count; i++)
        {
            admissions[i] = gate.TryEnter();
            Assert.True(admissions[i].Entered);
        }

        var drained = gate.CloseAsync();
        for (var i = 0; i < count; i++)
        {
            using (var rejected = gate.TryEnter())
            {
                Assert.False(rejected.Entered);
            }

            Assert.Same(drained, gate.CloseAsync());
            Assert.False(drained.IsCompleted);
            admissions[i].Dispose();
        }

        Assert.True(drained.IsCompletedSuccessfully);
        using var afterDrain = gate.TryEnter();
        Assert.False(afterDrain.Entered);
        Assert.Same(drained, gate.CloseAsync());
    }

    [Fact]
    public async Task CloseAsync_WithConcurrentCallers_ReturnsSharedDrainTask()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var gate = new AdmissionGate();
        Task drained;
        using (var admission = gate.TryEnter())
        {
            Assert.True(admission.Entered);
            var callers = Enumerable.Range(0, 8).Select(_ => Task.Factory.StartNew(
                gate.CloseAsync, cancellationToken, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default)).ToArray();
            var results = await Task.WhenAll(callers).WaitAsync(Timeout, cancellationToken);
            drained = results[0];
            Assert.All(results, result => Assert.Same(drained, result));
            Assert.False(drained.IsCompleted);
        }

        await drained.WaitAsync(Timeout, cancellationToken);
        using var rejected = gate.TryEnter();
        Assert.False(rejected.Entered);
        Assert.Same(drained, gate.CloseAsync());
    }

    [Fact]
    public void DefaultToken_RepresentsRejectionAndIsSafeToDispose()
    {
        var admission = default(AdmissionGate.Admission);

        Assert.False(admission.Entered);
        admission.Dispose();
        admission.Dispose();
        Assert.False(admission.Entered);
    }

    [Fact]
    public void IsClosed_BecomesTrueWhenClosedAndStaysTrue()
    {
        var gate = new AdmissionGate();
        Assert.False(gate.IsClosed);

        Assert.True(gate.TryEnterUnscoped());
        Assert.False(gate.IsClosed);

        var drained = gate.CloseAsync();
        Assert.True(gate.IsClosed);
        Assert.False(gate.TryEnterUnscoped());
        Assert.True(gate.IsClosed);

        gate.Exit();
        Assert.True(drained.IsCompletedSuccessfully);
        Assert.True(gate.IsClosed);
    }

    [Theory]
    [InlineData("return", false)]
    [InlineData("exception", false)]
    [InlineData("cancellation", false)]
    [InlineData("return", true)]
    [InlineData("exception", true)]
    [InlineData("cancellation", true)]
    public async Task Admission_AcrossAwait_DrainsWhenOperationEnds(string outcome, bool unscoped)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var gate = new AdmissionGate();
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failure = new InvalidOperationException("Admitted operation failed.");
        var canceledToken = new CancellationToken(canceled: true);
        var operation = RunAsync();
        var drained = gate.CloseAsync();
        try
        {
            Assert.False(operation.IsCompleted);
            Assert.False(drained.IsCompleted);
        }
        finally
        {
            _ = resume.TrySetResult();
        }

        switch (outcome)
        {
            case "return":
                await operation.WaitAsync(Timeout, cancellationToken);
                break;
            case "exception":
                Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(
                    () => operation.WaitAsync(Timeout, cancellationToken)));
                break;
            case "cancellation":
                var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                    () => operation.WaitAsync(Timeout, cancellationToken));
                Assert.Equal(canceledToken, exception.CancellationToken);
                break;
            default:
                throw new InvalidOperationException($"Unexpected outcome: {outcome}");
        }

        await drained.WaitAsync(Timeout, cancellationToken);
        using var rejected = gate.TryEnter();
        Assert.False(rejected.Entered);
        Assert.Same(drained, gate.CloseAsync());

        async Task RunAsync()
        {
            if (unscoped)
            {
                Assert.True(gate.TryEnterUnscoped());
                try
                {
                    await RunBodyAsync();
                }
                finally
                {
                    gate.Exit();
                }
            }
            else
            {
                using var admission = gate.TryEnter();
                Assert.True(admission.Entered);
                await RunBodyAsync();
            }
        }

        async Task RunBodyAsync()
        {
            await resume.Task;
            if (string.Equals(outcome, "exception", StringComparison.Ordinal))
            {
                throw failure;
            }

            if (string.Equals(outcome, "cancellation", StringComparison.Ordinal))
            {
                canceledToken.ThrowIfCancellationRequested();
            }
        }
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(8, false)]
    [InlineData(1, true)]
    [InlineData(8, true)]
    public async Task CloseAsync_RacingWithEntry_DrainsExactlyTheAdmittedOperations(int count, bool unscoped)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        for (var iteration = 0; iteration < 100; iteration++)
        {
            var gate = new AdmissionGate();
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var decisions = Enumerable.Range(0, count)
                .Select(_ => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
            var workers = decisions.Select(decision => Task.Run(
                async () =>
                {
                    await start.Task;
                    if (unscoped)
                    {
                        var entered = gate.TryEnterUnscoped();
                        decision.SetResult(entered);
                        if (entered)
                        {
                            try
                            {
                                await release.Task;
                            }
                            finally
                            {
                                gate.Exit();
                            }
                        }
                    }
                    else
                    {
                        using var admission = gate.TryEnter();
                        decision.SetResult(admission.Entered);
                        if (admission.Entered)
                        {
                            await release.Task;
                        }
                    }
                },
                cancellationToken)).ToArray();
            var closing = CloseWhenStartedAsync(gate, start.Task);


            start.SetResult();
            try
            {
                var admitted = await Task.WhenAll(decisions.Select(decision => decision.Task))
                    .WaitAsync(Timeout, cancellationToken);
                var drained = (await closing.WaitAsync(Timeout, cancellationToken)).Drained;
                Assert.Equal(admitted.Any(value => value), !drained.IsCompleted);
                using var rejected = gate.TryEnter();
                Assert.False(rejected.Entered);
            }
            finally
            {
                _ = release.TrySetResult();
                await Task.WhenAll(workers).WaitAsync(Timeout, cancellationToken);
            }

            var completion = (await closing.WaitAsync(Timeout, cancellationToken)).Drained;
            await completion.WaitAsync(Timeout, cancellationToken);
            Assert.Same(completion, gate.CloseAsync());
            using var afterDrain = gate.TryEnter();
            Assert.False(afterDrain.Entered);
        }
    }

    [Fact]
    public async Task CloseAsync_RacingWithDisposalAndRejectedEntries_CompletesDraining()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        for (var iteration = 0; iteration < 100; iteration++)
        {
            var gate = new AdmissionGate();
            var admission = gate.TryEnter();
            Assert.True(admission.Entered);
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var exiting = Task.Run(
                async () =>
                {
                    await start.Task;
                    admission.Dispose();
                },
                cancellationToken);
            var entering = Task.Run(
                async () =>
                {
                    await start.Task;
                    for (var i = 0; i < 8; i++)
                    {
                        using var attempt = gate.TryEnter();
                    }
                },
                cancellationToken);
            var closing = CloseWhenStartedAsync(gate, start.Task);


            start.SetResult();
            await Task.WhenAll(entering, exiting, closing).WaitAsync(Timeout, cancellationToken);
            var drained = (await closing).Drained;
            await drained.WaitAsync(Timeout, cancellationToken);

            using var rejected = gate.TryEnter();
            Assert.False(rejected.Entered);
            Assert.Same(drained, gate.CloseAsync());
            Assert.True(drained.IsCompletedSuccessfully);
        }
    }

    /// <summary>
    /// Closes the gate on a thread-pool thread once <paramref name="start"/> completes, returning the drain task.
    /// </summary>
    private static async Task<Closing> CloseWhenStartedAsync(AdmissionGate gate, Task start)
    {
        await start;
        return new Closing(gate.CloseAsync());
    }

    private sealed record Closing(Task Drained);
}
