namespace Orleans.Durable.Storage.Simulation;

/// <summary>
/// The constructs Clog's rewritten code relies on, each written as Clog's code writes it, so a test runs it under
/// Clockwork's rewrite and checks what it does (test/Durable.Storage/docs/spec/verification.md §7.1).
/// </summary>
public static partial class ClockworkConformance
{
    /// <summary>
    /// Waits for a task which has not completed for <paramref name="grace"/>, suppressing the timeout, as a bounded wait
    /// which must not throw is written, and then completes it (ReubenBond/Clockwork#38).
    /// </summary>
    /// <param name="grace">The grace.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The virtual time the wait took.</returns>
    public static async Task<TimeSpan> WaitOutGraceAsync(TimeSpan grace, CancellationToken cancellationToken)
    {
        var started = TimeProvider.System.GetUtcNow();
        var never = new TaskCompletionSource<int>();
        await ((Task)Task.WhenAll(never.Task))
            .WaitAsync(grace, TimeProvider.System, cancellationToken)
            .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        var waited = TimeProvider.System.GetUtcNow() - started;

        // The straggler answers after the wait, so nothing outlives the run.
        never.SetResult(0);
        return waited;
    }

    /// <summary>
    /// Enumerates an asynchronous sequence inside an asynchronous disposable, both configured not to capture a context
    /// (ReubenBond/Clockwork#39).
    /// </summary>
    /// <param name="count">The number of items the sequence yields.</param>
    /// <returns>The items enumerated, and whether the sequence and the disposable were both disposed.</returns>
    public static async Task<(List<int> Items, bool Disposed)> EnumerateInsideADisposableAsync(int count)
    {
        var resource = new Resource();
        var sequence = new Sequence(count);
        var items = new List<int>();
        await using (resource.ConfigureAwait(false))
        {
            await foreach (var item in sequence.ProduceAsync().ConfigureAwait(false))
            {
                items.Add(item);
            }
        }

        return (items, resource.Disposed && sequence.Disposed);
    }

    /// <summary>
    /// Races two task completion sources which tasks complete, and continues one of them, as a quorum gathers its
    /// replicas' answers (ReubenBond/Clockwork#40).
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The winner's value, then the continuation's.</returns>
    public static async Task<(int Winner, int Continued)> RaceCompletionSourcesAsync(CancellationToken cancellationToken)
    {
        var first = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var continued = first.Task.ContinueWith(static task => task.Result + 10, cancellationToken, TaskContinuationOptions.None, TaskScheduler.Default);
        var completions = Task.WhenAll(
            Task.Run(() => first.SetResult(1), cancellationToken),
            Task.Run(() => second.SetResult(2), cancellationToken));
        var winner = await Task.WhenAny(first.Task, second.Task).ConfigureAwait(false);
        await completions.ConfigureAwait(false);
        return (await winner.ConfigureAwait(false), await continued.ConfigureAwait(false));
    }

    /// <summary>
    /// Waits for a task which never completes until <paramref name="timeout"/> passes or a cancellation after
    /// <paramref name="cancelAfter"/> ends the wait (ReubenBond/Clockwork#44).
    /// </summary>
    /// <param name="timeout">The wait's timeout.</param>
    /// <param name="cancelAfter">When the wait's token is cancelled.</param>
    /// <returns>The exception which ended the wait, and the virtual time the wait took.</returns>
    public static async Task<(Exception Ended, TimeSpan Waited)> WaitForNothingAsync(TimeSpan timeout, TimeSpan cancelAfter)
    {
        var started = TimeProvider.System.GetUtcNow();
        var never = new TaskCompletionSource();
        using var source = new CancellationTokenSource();
        var cancel = CancelAfterAsync(source, cancelAfter);
        try
        {
            await never.Task.WaitAsync(timeout, TimeProvider.System, source.Token).ConfigureAwait(false);
            throw new InvalidOperationException("A task which never completes completed.");
        }
        catch (TimeoutException exception)
        {
            return (exception, TimeProvider.System.GetUtcNow() - started);
        }
        catch (OperationCanceledException exception)
        {
            return (exception, TimeProvider.System.GetUtcNow() - started);
        }
        finally
        {
            await cancel.ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Returns a task which a callback registered on <paramref name="cancellationToken"/> completes, as a caller's
    /// cancellation ends its wait: the callback completes a task completion source, a controlled API, on whichever
    /// thread cancels the token (ReubenBond/Clockwork#48).
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The task the callback completes.</returns>
    public static Task CompleteOnCancellation(CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = cancellationToken.Register(() => completion.TrySetResult());
        return completion.Task;
    }

    private static async Task CancelAfterAsync(CancellationTokenSource source, TimeSpan delay)
    {
        await Task.Delay(delay, TimeProvider.System, CancellationToken.None).ConfigureAwait(false);
        await source.CancelAsync().ConfigureAwait(false);
    }

    private sealed class Resource : IAsyncDisposable
    {
        public bool Disposed { get; private set; }

        public async ValueTask DisposeAsync()
        {
            await Task.Yield();
            Disposed = true;
        }
    }

    private sealed class Sequence(int count)
    {
        public bool Disposed { get; private set; }

        public async IAsyncEnumerable<int> ProduceAsync()
        {
            try
            {
                for (var item = 0; item < count; item++)
                {
                    await Task.Yield();
                    yield return item;
                }
            }
            finally
            {
                Disposed = true;
            }
        }
    }
}
