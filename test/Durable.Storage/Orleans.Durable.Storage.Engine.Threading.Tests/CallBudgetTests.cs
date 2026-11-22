using CsCheck;
using Microsoft.Extensions.Time.Testing;
using Orleans.Durable.Storage.Testing;

namespace Orleans.Durable.Storage.Engine.Threading.Tests;

/// <summary>
/// <see cref="CallBudget"/> (ledger-replicas.md §2.2): a call which lacks budget, in bytes or calls, waits in the FIFO
/// queue, so a large call at the head is not overtaken by smaller ones behind it; it fails with its owner's timeout only
/// once the deadline fixed when the call was made passes, and never at once; its caller's cancellation withdraws it; and
/// the bounds and deadlines are validated. A model-based property holds the budget to a FIFO queue with byte and call
/// capacity, deadlines, and cancellation after every step, and a budget of calls alone to a FIFO semaphore whose calls may
/// wait with no deadline; mutants which overtake, fail fast, or time out early are each caught by the oracle named for them.
/// </summary>
public sealed class CallBudgetTests
{
    private const long Budget = 100;
    private static readonly TimeSpan Deadline = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task ALargeCallAtTheHeadIsNotOvertakenBySmallerOnesBehindIt() =>
        Assert.Null(await OrderViolationAsync(CallBudgetMutant.None));

    [Fact]
    public async Task ACallWaitsUntilItsDeadlineAndThenFailsWithItsOwnersTimeout() =>
        Assert.Null(await DeadlineViolationAsync(CallBudgetMutant.None));

    /// <summary>Each mutant is caught by the oracle which names it: order catches overtaking, the deadline failing fast or early.</summary>
    [Theory]
    [InlineData(CallBudgetMutant.Overtake, "order")]
    [InlineData(CallBudgetMutant.FailFast, "deadline")]
    [InlineData(CallBudgetMutant.EarlyDeadline, "deadline")]
    internal async Task EachMutantIsCaughtByItsOracle(CallBudgetMutant mutant, string oracle) =>
        Assert.NotNull(oracle == "order" ? await OrderViolationAsync(mutant) : await DeadlineViolationAsync(mutant));

    /// <summary>
    /// A deadline is fixed when the call is made: a call which reaches the queue after waiting elsewhere (a node's
    /// registration, say) times out at that deadline, and one whose deadline passed before it lacked budget fails at once.
    /// </summary>
    [Fact]
    public async Task TheDeadlineRunsFromWhenTheCallWasMade()
    {
        var time = new FakeTimeProvider();
        var budget = Create(time);
        using var held = await budget.EnterAsync(Budget, budget.DeadlineAfter(Deadline), TestContext.Current.CancellationToken);
        var deadline = budget.DeadlineAfter(Deadline);
        time.Advance(Deadline / 2);

        var waiting = budget.EnterAsync(1, deadline, TestContext.Current.CancellationToken).AsTask();
        time.Advance(Deadline / 2 - TimeSpan.FromTicks(1));
        await Task.Yield();
        Assert.False(waiting.IsCompleted);
        time.Advance(TimeSpan.FromTicks(1));
        _ = await Assert.ThrowsAsync<TimeoutException>(() => waiting.WaitAsync(Bound, TimeProvider.System, TestContext.Current.CancellationToken));

        _ = await Assert.ThrowsAsync<TimeoutException>(() => budget.EnterAsync(1, deadline, TestContext.Current.CancellationToken).AsTask());
        Assert.Equal((Budget, 1, 0), (budget.OutstandingBytes, budget.OutstandingCalls, budget.Waiting));
    }

    [Fact]
    public async Task ACallBeyondTheCallLimitWaitsInOrderThoughItsBytesFit()
    {
        var token = TestContext.Current.CancellationToken;
        var budget = new CallBudget(Budget, maxCalls: 2, TimeProvider.System, static waited => new TimeoutException());
        var first = await budget.EnterAsync(1, budget.DeadlineAfter(Deadline), token);
        var second = await budget.EnterAsync(1, budget.DeadlineAfter(Deadline), token);
        var third = budget.EnterAsync(1, budget.DeadlineAfter(Deadline), token).AsTask();
        var fourth = budget.EnterAsync(0, budget.DeadlineAfter(Deadline), token).AsTask();
        Assert.False(third.IsCompleted);
        Assert.False(fourth.IsCompleted);
        Assert.Equal((2, 2), (budget.OutstandingCalls, budget.Waiting));

        first.Dispose();
        using var granted = await third.WaitAsync(Bound, TimeProvider.System, token);
        Assert.False(fourth.IsCompleted);
        second.Dispose();
        (await fourth.WaitAsync(Bound, TimeProvider.System, token)).Dispose();
        granted.Dispose();
        Assert.Equal((0, 0L), (budget.OutstandingCalls, budget.OutstandingBytes));
    }

    [Fact]
    public async Task ABudgetRefusesCallsLargerThanItAndUnboundedOrInvalidLimits()
    {
        var budget = Create(TimeProvider.System);
        _ = await Assert.ThrowsAsync<ArgumentException>(() => budget.EnterAsync(Budget + 1, budget.DeadlineAfter(Deadline), TestContext.Current.CancellationToken).AsTask());
        Assert.Equal(0, budget.OutstandingBytes);
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => new CallBudget(0, 1, TimeProvider.System, static _ => new TimeoutException()));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => new CallBudget(1, 0, TimeProvider.System, static _ => new TimeoutException()));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => budget.DeadlineAfter(TimeSpan.Zero));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => budget.DeadlineAfter(Timeout.InfiniteTimeSpan));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => budget.DeadlineAfter(TimeSpan.MaxValue));
    }

    /// <summary>
    /// Model-based: random sequences of calls entering with up to the whole budget, releases, cancellations, and the clock's
    /// advance, under a random call limit, against a model of a FIFO queue with byte and call capacity. After every step each
    /// call is granted, waiting, timed out, or cancelled as the model says, and the bytes and calls outstanding equal the
    /// model's.
    /// </summary>
    [Fact]
    public Task TheBudgetBehavesAsAFifoQueueWithCapacityDeadlinesAndCancellation() =>
        Gen.Select(Gen.Int[1, 5], Gen.Select(Gen.Int[0, 3], Gen.Long[0, Budget], Gen.Int[0, 7], Gen.Int[1, 40]).Array[1, 40])
            .SampleAsync((maxCalls, steps) => CheckAgainstFifoModelAsync(maxCalls, steps, callsAlone: false), iter: 300, threads: 1);

    /// <summary>
    /// Model-based, for a budget of calls alone (<see cref="CallBudget.ForCalls"/>): random sequences of calls entering with
    /// and without a deadline, releases, cancellations, and the clock's advance, against a model of a FIFO semaphore. After
    /// every step each call is granted, waiting, timed out, or cancelled as the model says, a call without a deadline never
    /// times out, and the calls outstanding equal the model's.
    /// </summary>
    [Fact]
    public Task ABudgetOfCallsAloneBehavesAsAFifoSemaphore() =>
        Gen.Select(Gen.Int[1, 5], Gen.Select(Gen.Int[0, 3], Gen.Long[0, 1], Gen.Int[0, 7], Gen.Int[1, 40]).Array[1, 40])
            .SampleAsync((maxCalls, steps) => CheckAgainstFifoModelAsync(maxCalls, steps, callsAlone: true), iter: 300, threads: 1);

    /// <summary>
    /// A cancellation which arrives once the call was granted has nothing to withdraw: the call keeps its grant, and the
    /// budget counts it once.
    /// </summary>
    [Fact]
    public async Task ACancellationAfterTheGrantLeavesTheCallGranted()
    {
        var token = TestContext.Current.CancellationToken;
        var budget = CallBudget.ForCalls(1, TimeProvider.System, static waited => new TimeoutException());
        var held = await budget.EnterAsync(deadline: null, token);
        using var cancel = new CancellationTokenSource();
        var waiting = budget.EnterAsync(deadline: null, cancel.Token).AsTask();
        Assert.False(waiting.IsCompleted);

        held.Dispose();
        await cancel.CancelAsync();

        using var grant = await waiting.WaitAsync(Bound, TimeProvider.System, token);
        Assert.Equal((1, 0, 0L), (budget.OutstandingCalls, budget.Waiting, budget.OutstandingBytes));
    }

    /// <summary>A budget of calls alone refuses a call which would hold bytes, whose sum it does not bound.</summary>
    [Fact]
    public async Task ABudgetOfCallsAloneRefusesACallWhichHoldsBytes()
    {
        var budget = CallBudget.ForCalls(1, TimeProvider.System, static waited => new TimeoutException());
        _ = await Assert.ThrowsAsync<InvalidOperationException>(() => budget.EnterAsync(1, budget.DeadlineAfter(Deadline), TestContext.Current.CancellationToken).AsTask());
        (await budget.EnterAsync(0, budget.DeadlineAfter(Deadline), TestContext.Current.CancellationToken)).Dispose();
        Assert.Equal(0, budget.OutstandingCalls);
    }

    /// <summary>
    /// <see cref="CallBudget.TryEnter"/> grants uncontended zero-byte calls up to the call limit, returns
    /// <see langword="null"/> when the limit is full or any caller is already queued, and never overtakes FIFO waiters.
    /// </summary>
    [Fact]
    public async Task TryEnterGrantsUncontendedCallsAndPreservesFifoBehindWaiters()
    {
        var token = TestContext.Current.CancellationToken;
        var callsOnly = CallBudget.ForCalls(2, TimeProvider.System, static _ => new TimeoutException());
        using (var first = callsOnly.TryEnter())
        using (var second = callsOnly.TryEnter())
        {
            Assert.NotNull(first);
            Assert.NotNull(second);
            Assert.Equal(2, callsOnly.OutstandingCalls);
            Assert.Null(callsOnly.TryEnter());

            var waiter = callsOnly.EnterAsync(deadline: null, token).AsTask();
            Assert.False(waiter.IsCompleted);
            Assert.Null(callsOnly.TryEnter());

            first.Dispose();
            using var granted = await waiter.WaitAsync(Bound, TimeProvider.System, token);
            Assert.Null(callsOnly.TryEnter());
        }

        Assert.Equal(0, callsOnly.OutstandingCalls);
        using (var after = callsOnly.TryEnter())
        {
            Assert.NotNull(after);
        }

        // Byte+call budget: a waiter blocked on bytes at the head blocks TryEnter even when call slots are free.
        var sized = new CallBudget(Budget, maxCalls: 3, TimeProvider.System, static _ => new TimeoutException());
        using (var held = await sized.EnterAsync(Budget, sized.DeadlineAfter(Deadline), token))
        {
            using (var zeroByte = sized.TryEnter())
            {
                Assert.NotNull(zeroByte);
            }

            var waiting = sized.EnterAsync(1, sized.DeadlineAfter(Deadline), token).AsTask();
            Assert.False(waiting.IsCompleted);
            Assert.Null(sized.TryEnter());
            held.Dispose();
            (await waiting.WaitAsync(Bound, TimeProvider.System, token)).Dispose();
        }

        Assert.Equal((0, 0L), (sized.OutstandingCalls, sized.OutstandingBytes));
    }

    /// <summary>
    /// <see cref="CallBudget.EnterLeaseAsync"/> completes synchronously when uncontended, preserves FIFO order, timeout,
    /// and cancellation when contended, refuses non-zero bytes on a calls-alone budget, and makes repeated
    /// <see cref="CallBudget.Lease.Dispose"/> calls idempotent.
    /// </summary>
    [Fact]
    public async Task EnterLeaseAsyncGrantsUncontendedSynchronouslyPreservesFifoAndSupportsIdempotentDispose()
    {
        var token = TestContext.Current.CancellationToken;
        var time = new FakeTimeProvider();
        var budget = Create(time);

        var immediate = budget.EnterLeaseAsync(60, budget.DeadlineAfter(Deadline), token);
        Assert.True(immediate.IsCompletedSuccessfully);
        var first = await immediate;
        Assert.Equal((1, 60L, 0), (budget.OutstandingCalls, budget.OutstandingBytes, budget.Waiting));

        var waiting = budget.EnterLeaseAsync(50, budget.DeadlineAfter(Deadline), token).AsTask();
        Assert.False(waiting.IsCompleted);
        Assert.Equal(1, budget.Waiting);

        var behind = budget.EnterLeaseAsync(10, budget.DeadlineAfter(Deadline), token).AsTask();
        Assert.False(behind.IsCompleted);
        Assert.Equal(2, budget.Waiting);

        // Disposing the same lease twice must release its budget only once.
        first.Dispose();
        first.Dispose();

        var second = await waiting.WaitAsync(Bound, TimeProvider.System, token);
        var third = await behind.WaitAsync(Bound, TimeProvider.System, token);
        Assert.Equal((2, 60L, 0), (budget.OutstandingCalls, budget.OutstandingBytes, budget.Waiting));

        var expiring = budget.EnterLeaseAsync(50, budget.DeadlineAfter(Deadline), token).AsTask();
        time.Advance(Deadline);
        _ = await Assert.ThrowsAsync<TimeoutException>(() => expiring.WaitAsync(Bound, TimeProvider.System, token));

        using var cancel = new CancellationTokenSource();
        var cancelled = budget.EnterLeaseAsync(50, budget.DeadlineAfter(Deadline), cancel.Token).AsTask();
        await cancel.CancelAsync();
        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.WaitAsync(Bound, TimeProvider.System, token));

        second.Dispose();
        third.Dispose();
        Assert.Equal((0, 0L, 0), (budget.OutstandingCalls, budget.OutstandingBytes, budget.Waiting));

        var callsOnly = CallBudget.ForCalls(1, time, static _ => new TimeoutException());
        _ = await Assert.ThrowsAsync<InvalidOperationException>(() => callsOnly.EnterLeaseAsync(1, callsOnly.DeadlineAfter(Deadline), token).AsTask());
        var callLease = callsOnly.EnterLeaseAsync(0, callsOnly.DeadlineAfter(Deadline), token);
        Assert.True(callLease.IsCompletedSuccessfully);
        (await callLease).Dispose();
        Assert.Equal(0, callsOnly.OutstandingCalls);
    }

    private static CallBudget Create(TimeProvider time, CallBudgetMutant mutant = CallBudgetMutant.None) =>
        new MutantCallBudget(Budget, int.MaxValue, time, static waited => new TimeoutException(), mutant);

    /// <summary>Returns the first violation of FIFO order and the budget, or null.</summary>
    private static async Task<string?> OrderViolationAsync(CallBudgetMutant mutant)
    {
        var budget = Create(TimeProvider.System, mutant);
        var token = TestContext.Current.CancellationToken;
        using var first = await budget.EnterAsync(60, budget.DeadlineAfter(Deadline), token);
        var large = budget.EnterAsync(80, budget.DeadlineAfter(Deadline), token).AsTask();
        var small = budget.EnterAsync(10, budget.DeadlineAfter(Deadline), token).AsTask();
        if (small.IsCompleted)
        {
            if (small.IsCompletedSuccessfully)
            {
                small.Result.Dispose();
            }

            first.Dispose();
            _ = await Record.ExceptionAsync(() => large.WaitAsync(Bound, TimeProvider.System));
            return "A 10-byte call was answered while an 80-byte call waited ahead of it.";
        }

        first.Dispose();
        (await large.WaitAsync(Bound, TimeProvider.System)).Dispose();
        (await small.WaitAsync(Bound, TimeProvider.System)).Dispose();
        return null;
    }

    /// <summary>Returns the first violation of the deadline's contract, or null.</summary>
    private static async Task<string?> DeadlineViolationAsync(CallBudgetMutant mutant)
    {
        var time = new FakeTimeProvider();
        var budget = Create(time, mutant);
        var token = TestContext.Current.CancellationToken;
        using var first = await budget.EnterAsync(60, budget.DeadlineAfter(Deadline), token);
        var waiting = budget.EnterAsync(80, budget.DeadlineAfter(Deadline), token).AsTask();
        await Task.Yield();
        if (waiting.IsCompleted)
        {
            return "A call which lacked budget ended at once.";
        }

        time.Advance(Deadline - TimeSpan.FromTicks(1));
        await Task.Delay(TimeSpan.FromMilliseconds(20), TimeProvider.System, token);
        if (waiting.IsCompleted)
        {
            return "A call ended before its deadline passed.";
        }

        time.Advance(TimeSpan.FromTicks(1));
        var failure = await Record.ExceptionAsync(() => waiting.WaitAsync(Bound, TimeProvider.System));
        return failure is TimeoutException { InnerException: null } ? null : $"A call whose deadline passed ended with {failure?.GetType().Name ?? "no failure"}.";
    }

    /// <summary>
    /// Runs one sequence of steps, each <c>(kind, bytes, pick, seconds)</c>: 0 enters a call of <c>bytes</c>, 1 releases
    /// the <c>pick</c>-th granted call, 2 cancels the <c>pick</c>-th waiting call, and 3 advances the clock. For a budget of
    /// calls alone, a call holds no bytes, and <c>bytes</c> 1 enters it with no deadline.
    /// </summary>
    private static async Task CheckAgainstFifoModelAsync(int maxCalls, (int Kind, long Bytes, int Pick, int Seconds)[] steps, bool callsAlone)
    {
        var time = new FakeTimeProvider();
        var deadline = TimeSpan.FromSeconds(30);
        var budget = callsAlone
            ? CallBudget.ForCalls(maxCalls, time, static waited => new TimeoutException())
            : new CallBudget(Budget, maxCalls, time, static waited => new TimeoutException());
        var calls = new List<ModelCall>();
        var queue = new List<ModelCall>();
        var outstanding = 0L;
        var granted = 0;
        var now = TimeSpan.Zero;

        bool Fits(long bytes) => granted < maxCalls && outstanding + bytes <= Budget;

        void GrantHeads()
        {
            while (queue.Count > 0 && Fits(queue[0].Bytes))
            {
                outstanding += queue[0].Bytes;
                granted++;
                queue[0].State = ModelState.Granted;
                queue.RemoveAt(0);
            }
        }

        foreach (var (kind, bytes, pick, seconds) in steps)
        {
            switch (kind)
            {
                case 0:
                    {
                        // Each call enters a millisecond after the last, so no two deadlines tie.
                        now += TimeSpan.FromMilliseconds(1);
                        time.Advance(TimeSpan.FromMilliseconds(1));
#pragma warning disable CA2000 // The model call owns its cancellation, which the run disposes once it ends.
                        var cancel = new CancellationTokenSource();
#pragma warning restore CA2000
                        var call = !callsAlone
                            ? new ModelCall(bytes, now + deadline, cancel, budget.EnterAsync(bytes, budget.DeadlineAfter(deadline), cancel.Token).AsTask())
                            : bytes == 0
                                ? new ModelCall(0, now + deadline, cancel, budget.EnterAsync(budget.DeadlineAfter(deadline), cancel.Token).AsTask())
                                : new ModelCall(0, null, cancel, budget.EnterAsync(deadline: null, cancel.Token).AsTask());
                        calls.Add(call);
                        if (queue.Count == 0 && Fits(call.Bytes))
                        {
                            outstanding += call.Bytes;
                            granted++;
                            call.State = ModelState.Granted;
                        }
                        else
                        {
                            call.State = ModelState.Waiting;
                            queue.Add(call);
                        }

                        break;
                    }

                case 1 when calls.Where(static call => call.State == ModelState.Granted).ElementAtOrDefault(pick) is { } grant:
                    (await grant.Task).Dispose();
                    grant.State = ModelState.Released;
                    outstanding -= grant.Bytes;
                    granted--;
                    GrantHeads();
                    break;
                case 2 when queue.ElementAtOrDefault(pick) is { } waiting:
                    await waiting.Cancel.CancelAsync();
                    waiting.State = ModelState.Cancelled;
                    _ = queue.Remove(waiting);
                    GrantHeads();
                    break;
                case 3:
                    now += TimeSpan.FromSeconds(seconds);
                    time.Advance(TimeSpan.FromSeconds(seconds));
                    while (queue.FirstOrDefault(call => call.Deadline <= now) is { } expired)
                    {
                        expired.State = ModelState.TimedOut;
                        _ = queue.Remove(expired);
                        GrantHeads();
                    }

                    break;
                default:
                    break;
            }

            Assert.Equal((outstanding, granted, queue.Count), (budget.OutstandingBytes, budget.OutstandingCalls, budget.Waiting));
            foreach (var call in calls)
            {
                switch (call.State)
                {
                    case ModelState.Granted or ModelState.Released:
                        Assert.Equal(call.Bytes, (await call.Task.WaitAsync(Bound, TimeProvider.System)).Bytes);
                        break;
                    case ModelState.TimedOut:
                        _ = await Assert.ThrowsAsync<TimeoutException>(() => call.Task.WaitAsync(Bound, TimeProvider.System));
                        break;
                    case ModelState.Cancelled:
                        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call.Task.WaitAsync(Bound, TimeProvider.System));
                        break;
                    case ModelState.Waiting:
                        Assert.False(call.Task.IsCompleted, "A call the model holds in the queue was answered.");
                        break;
                    default:
                        throw new InvalidOperationException("A model call holds no state.");
                }
            }
        }

        foreach (var call in calls)
        {
            if (call.State == ModelState.Granted)
            {
                (await call.Task).Dispose();
            }

            call.Cancel.Dispose();
        }
    }

    private enum ModelState
    {
        Waiting,
        Granted,
        Released,
        TimedOut,
        Cancelled,
    }

    private sealed class ModelCall(long bytes, TimeSpan? deadline, CancellationTokenSource cancel, Task<CallBudget.Grant> task)
    {
        public long Bytes { get; } = bytes;

        public TimeSpan? Deadline { get; } = deadline;

        public CancellationTokenSource Cancel { get; } = cancel;

        public Task<CallBudget.Grant> Task { get; } = task;

        public ModelState State { get; set; }
    }
}
