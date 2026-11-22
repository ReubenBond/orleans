using System.Collections.Concurrent;
using Microsoft.Extensions.Time.Testing;

namespace Orleans.Durable.Storage.Engine.Ledgers.Tests;

/// <summary>
/// A fake clock which counts the timers its users create and records their due times, so a test waits for the timer a
/// hedge or a backoff arms and advances the clock exactly past it, without polling real time.
/// </summary>
internal sealed class TimerSignallingTimeProvider : FakeTimeProvider
{
    private readonly ConcurrentQueue<TimeSpan> _dueTimes = new();
    private TaskCompletionSource _created = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _timers;

    /// <summary>Gets the number of timers created so far.</summary>
    internal int Timers => Volatile.Read(ref _timers);

    /// <summary>Gets the due time of every timer created so far, in order.</summary>
    internal IReadOnlyList<TimeSpan> DueTimes => [.. _dueTimes];

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = base.CreateTimer(callback, state, dueTime, period);
        _dueTimes.Enqueue(dueTime);
        _ = Interlocked.Increment(ref _timers);
        Interlocked.Exchange(ref _created, new(TaskCreationOptions.RunContinuationsAsynchronously)).SetResult();
        return timer;
    }

    /// <summary>Waits until at least <paramref name="count"/> timers have been created.</summary>
    internal async Task WaitForTimersAsync(int count, CancellationToken cancellationToken)
    {
        while (true)
        {
            var created = Volatile.Read(ref _created).Task;
            if (Timers >= count)
            {
                return;
            }

            await created.WaitAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Runs <paramref name="operation"/> to completion, advancing the clock past every timer created while it runs: each
    /// time timers appear, by the longest due time among them, at least <paramref name="step"/>. It returns the
    /// operation's result.
    /// </summary>
    internal async Task<T> AdvanceUntilCompletedAsync<T>(Task<T> operation, TimeSpan step, int timersBefore, CancellationToken cancellationToken)
    {
        var seen = timersBefore;
        while (!operation.IsCompleted)
        {
            var timer = WaitForTimersAsync(seen + 1, cancellationToken);
            if (await Task.WhenAny(operation, timer) == timer)
            {
                await timer;
                var created = DueTimes;
                var longest = created.Skip(seen).Where(static due => due != Timeout.InfiniteTimeSpan).DefaultIfEmpty(step).Max();
                seen = created.Count;
                Advance(longest > step ? longest : step);
            }
        }

        return await operation;
    }
}
