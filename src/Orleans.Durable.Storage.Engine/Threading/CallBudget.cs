using System.Diagnostics;
using System.Globalization;

namespace Orleans.Durable.Storage.Engine.Threading;

/// <summary>
/// A budget of bytes and calls which grants each call its share in arrival order (ledger-replicas.md §2.2): a call which
/// lacks budget waits in the budget's FIFO queue, so no call overtakes another and a large call at the head delays the
/// smaller ones behind it rather than starving; a call fails with its owner's timeout only once its deadline passes while
/// it waits, and never at once for lack of budget.
/// </summary>
/// <remarks>
/// <para>
/// A call's deadline is an absolute timestamp of the budget's <see cref="TimeProvider"/>, fixed when the call was made, so
/// every wait the call makes counts against one deadline. The owner supplies the failure a timed-out call reports. The
/// protected members are policy seams through which tests derive budgets which break one of these rules; a budget built
/// directly keeps them all.
/// </para>
/// <para>
/// A budget of calls alone (<see cref="ForCalls"/>) is a FIFO slot gate: its calls hold no bytes, and a call may wait with
/// no deadline. It serves where <see cref="SemaphoreSlim"/> would otherwise, since the arrival order of
/// <see cref="SemaphoreSlim.WaitAsync()"/>'s waiters is not part of its contract.
/// </para>
/// </remarks>
public class CallBudget
{
    private readonly Lock _lock = new();
    private readonly LinkedList<Waiter> _queue = [];
    private readonly Func<TimeSpan, Exception> _timeout;
    private long _bytes;
    private int _calls;

    /// <summary>Initializes a new instance of the <see cref="CallBudget"/> class.</summary>
    /// <param name="maxBytes">The bytes the granted calls may hold at once, positive.</param>
    /// <param name="maxCalls">The calls which may hold grants at once, positive.</param>
    /// <param name="timeProvider">Times the deadlines.</param>
    /// <param name="timeout">Creates the failure of a call whose deadline passed while it waited, from how long it waited.</param>
    public CallBudget(long maxBytes, int maxCalls, TimeProvider timeProvider, Func<TimeSpan, Exception> timeout)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxCalls);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(timeout);
        MaxBytes = maxBytes;
        MaxCalls = maxCalls;
        TimeProvider = timeProvider;
        _timeout = timeout;
    }

    /// <summary>
    /// Returns a budget of <paramref name="maxCalls"/> calls alone, whose calls hold no bytes and enter through
    /// <see cref="EnterAsync(long?, CancellationToken)"/>.
    /// </summary>
    /// <param name="maxCalls">The calls which may hold grants at once, positive.</param>
    /// <param name="timeProvider">Times the deadlines.</param>
    /// <param name="timeout">Creates the failure of a call whose deadline passed while it waited, from how long it waited.</param>
    public static CallBudget ForCalls(int maxCalls, TimeProvider timeProvider, Func<TimeSpan, Exception> timeout) =>
        new(long.MaxValue, maxCalls, timeProvider, timeout) { CallsAlone = true };

    /// <summary>Gets the bytes the granted calls may hold at once.</summary>
    public long MaxBytes { get; }

    /// <summary>Gets a value indicating whether the budget counts calls alone (<see cref="ForCalls"/>), whose calls hold no bytes.</summary>
    public bool CallsAlone { get; private init; }

    /// <summary>Gets the calls which may hold grants at once.</summary>
    public int MaxCalls { get; }

    /// <summary>Gets the clock of the budget's deadlines.</summary>
    public TimeProvider TimeProvider { get; }

    /// <summary>Gets the bytes the granted calls hold.</summary>
    public long OutstandingBytes
    {
        get
        {
            lock (_lock)
            {
                return _bytes;
            }
        }
    }

    /// <summary>Gets the number of granted calls.</summary>
    public int OutstandingCalls
    {
        get
        {
            lock (_lock)
            {
                return _calls;
            }
        }
    }

    /// <summary>Gets the number of calls waiting for budget.</summary>
    public int Waiting
    {
        get
        {
            lock (_lock)
            {
                return _queue.Count;
            }
        }
    }

    /// <summary>
    /// Gets a value indicating whether a call which fits is granted only once every earlier call was: true, the policy
    /// which keeps arrival order. A test's budget overrides it to show that its oracle catches a budget which overtakes.
    /// </summary>
    protected virtual bool GrantsInArrivalOrder => true;

    /// <summary>
    /// Gets a value indicating whether a call which lacks budget waits for it: true. A test's budget overrides it to show
    /// that its oracle catches a budget which fails a call at once.
    /// </summary>
    protected virtual bool WaitsForBudget => true;

    /// <summary>
    /// Returns how long a call waits before it times out, given the time left before its deadline: all of it. A test's
    /// budget overrides it to show that its oracle catches a budget which times a call out early.
    /// </summary>
    protected virtual TimeSpan WaitFor(TimeSpan remaining) => remaining;

    /// <summary>Returns the deadline <paramref name="after"/> from now, an absolute timestamp of the budget's clock.</summary>
    /// <param name="after">The time a call may take, positive and finite.</param>
    public long DeadlineAfter(TimeSpan after)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(after, TimeSpan.Zero);
        if (after == Timeout.InfiniteTimeSpan || after == TimeSpan.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(after), "A call's deadline is finite.");
        }

        return checked(TimeProvider.GetTimestamp() + (long)(after.TotalSeconds * TimeProvider.TimestampFrequency));
    }

    /// <summary>Returns the time left before <paramref name="deadline"/>, zero once it passed.</summary>
    public TimeSpan Remaining(long deadline)
    {
        var remaining = TimeProvider.GetElapsedTime(TimeProvider.GetTimestamp(), deadline);
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    /// <summary>
    /// Waits in the queue for <paramref name="bytes"/> of budget and one call, and returns the grant, which the call
    /// disposes once it ends.
    /// </summary>
    /// <param name="bytes">The bytes the call holds, at most <see cref="MaxBytes"/>.</param>
    /// <param name="deadline">The call's deadline, from <see cref="DeadlineAfter"/>.</param>
    /// <param name="cancellationToken">The caller's cancellation, which withdraws a waiting call.</param>
    /// <returns>
    /// The grant; or a failure: <see cref="ArgumentException"/> for a call which needs more than the whole budget, so it
    /// could never be granted, with no effect; the owner's timeout failure once the deadline passes while the call waits,
    /// or at once when it passed before the call lacked budget; or <see cref="OperationCanceledException"/> for the
    /// caller's cancellation, which removes the waiting call.
    /// </returns>
    /// <exception cref="InvalidOperationException">The budget counts calls alone (<see cref="CallsAlone"/>), whose calls hold no bytes.</exception>
    public ValueTask<Grant> EnterAsync(long bytes, long deadline, CancellationToken cancellationToken)
    {
        if (CallsAlone && bytes != 0)
        {
            throw new InvalidOperationException("A budget of calls alone takes calls which hold no bytes.");
        }

        return EnterAsync(bytes, (long?)deadline, cancellationToken);
    }

    /// <summary>
    /// Waits in the queue for one call, holding no bytes, and returns the grant, which the call disposes once it ends: the
    /// entry of a budget of calls alone (<see cref="ForCalls"/>).
    /// </summary>
    /// <param name="deadline">The call's deadline, from <see cref="DeadlineAfter"/>, or none, so it waits until it is granted or cancelled.</param>
    /// <param name="cancellationToken">The caller's cancellation, which withdraws a waiting call.</param>
    /// <returns>
    /// The grant; or a failure: the owner's timeout failure once the deadline passes while the call waits, or at once when
    /// it passed before the call lacked a call; or <see cref="OperationCanceledException"/> for the caller's cancellation,
    /// which removes the waiting call.
    /// </returns>
    public ValueTask<Grant> EnterAsync(long? deadline, CancellationToken cancellationToken) => EnterAsync(0, deadline, cancellationToken);

    /// <summary>
    /// Waits in the queue for <paramref name="bytes"/> of budget and one call without allocating a <see cref="Grant"/>
    /// handle on the uncontended path, and returns a value-type <see cref="Lease"/> which the call disposes once it ends.
    /// </summary>
    /// <param name="bytes">The bytes the call holds, at most <see cref="MaxBytes"/>.</param>
    /// <param name="deadline">The call's deadline, from <see cref="DeadlineAfter"/>.</param>
    /// <param name="cancellationToken">The caller's cancellation, which withdraws a waiting call.</param>
    /// <returns>The value-type lease, or the same failures as <see cref="EnterAsync(long, long, CancellationToken)"/>.</returns>
    /// <exception cref="InvalidOperationException">The budget counts calls alone (<see cref="CallsAlone"/>), whose calls hold no bytes.</exception>
    public ValueTask<Lease> EnterLeaseAsync(long bytes, long deadline, CancellationToken cancellationToken)
    {
        if (CallsAlone && bytes != 0)
        {
            return ValueTask.FromException<Lease>(new InvalidOperationException("A budget of calls alone takes calls which hold no bytes."));
        }

        return EnterLeaseCoreAsync(bytes, deadline, cancellationToken);
    }

    /// <summary>
    /// Grants one call holding no bytes immediately if the budget has room and no earlier call is waiting in arrival order;
    /// otherwise returns <see langword="null"/> without waiting.
    /// </summary>
    public Grant? TryEnter()
    {
        lock (_lock)
        {
            if ((_queue.Count == 0 || !GrantsInArrivalOrder) && FitsLocked(0))
            {
                _calls++;
                return NewGrant(0);
            }
        }

        return null;
    }

    private async ValueTask<Grant> EnterAsync(long bytes, long? deadline, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        if (bytes > MaxBytes)
        {
            throw new ArgumentException(
                string.Create(CultureInfo.InvariantCulture, $"A call of {bytes} bytes passes the budget of {MaxBytes} bytes."),
                nameof(bytes));
        }

        cancellationToken.ThrowIfCancellationRequested();
        Waiter waiter;
        lock (_lock)
        {
            if ((_queue.Count == 0 || !GrantsInArrivalOrder) && FitsLocked(bytes))
            {
                _bytes += bytes;
                _calls++;
                return NewGrant(bytes);
            }

            var entered = TimeProvider.GetTimestamp();
            if (!WaitsForBudget || entered >= deadline)
            {
                throw _timeout(TimeProvider.GetElapsedTime(entered));
            }

            waiter = new Waiter(this, bytes, entered);
            waiter.Node = _queue.AddLast(waiter);
        }

        await waiter.WaitAsync(deadline is { } due ? Remaining(due) : null, cancellationToken).ConfigureAwait(false);
        return NewGrant(bytes);
    }

    private ValueTask<Lease> EnterLeaseCoreAsync(long bytes, long? deadline, CancellationToken cancellationToken)
    {
        if (bytes < 0)
        {
            return ValueTask.FromException<Lease>(new ArgumentOutOfRangeException(nameof(bytes), bytes, "bytes ('" + bytes.ToString(CultureInfo.InvariantCulture) + "') must be a non-negative value."));
        }

        if (bytes > MaxBytes)
        {
            return ValueTask.FromException<Lease>(new ArgumentException(
                string.Create(CultureInfo.InvariantCulture, $"A call of {bytes} bytes passes the budget of {MaxBytes} bytes."),
                nameof(bytes)));
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromCanceled<Lease>(cancellationToken);
        }

        Waiter waiter;
        lock (_lock)
        {
            if ((_queue.Count == 0 || !GrantsInArrivalOrder) && FitsLocked(bytes))
            {
                _bytes += bytes;
                _calls++;
                return new(new Lease(this, bytes));
            }

            var entered = TimeProvider.GetTimestamp();
            if (!WaitsForBudget || entered >= deadline)
            {
                return ValueTask.FromException<Lease>(_timeout(TimeProvider.GetElapsedTime(entered)));
            }

            waiter = new Waiter(this, bytes, entered);
            waiter.Node = _queue.AddLast(waiter);
        }

        return WaitLeaseAsync(waiter, bytes, deadline, cancellationToken);
    }

    private async ValueTask<Lease> WaitLeaseAsync(Waiter waiter, long bytes, long? deadline, CancellationToken cancellationToken)
    {
        await waiter.WaitAsync(deadline is { } due ? Remaining(due) : null, cancellationToken).ConfigureAwait(false);
        return new Lease(this, bytes);
    }

    private void Release(long bytes)
    {
        List<Waiter>? granted;
        lock (_lock)
        {
            _bytes -= bytes;
            _calls--;
            Debug.Assert(_bytes >= 0 && _calls >= 0, "A budget releases no more bytes or calls than it granted.");
            granted = GrantWaitersLocked();
            AssertLocked();
        }

        Complete(granted);
    }

    /// <summary>Removes a waiting call, which then fails with <paramref name="failure"/>.</summary>
    private void Withdraw(Waiter waiter, Exception failure)
    {
        List<Waiter>? granted;
        lock (_lock)
        {
            if (waiter.Node?.List is null)
            {
                return;
            }

            _queue.Remove(waiter.Node);
            waiter.Node = null;

            // A call removed from the head may let the calls behind it in.
            granted = GrantWaitersLocked();
            AssertLocked();
        }

        _ = waiter.TrySetException(failure);
        Complete(granted);
    }

    /// <summary>Grants budget to the calls at the head of the queue, in arrival order, while each fits. Under the lock.</summary>
    private List<Waiter>? GrantWaitersLocked()
    {
        List<Waiter>? granted = null;
        for (var next = _queue.First; next is { } waiting;)
        {
            next = waiting.Next;
            if (!FitsLocked(waiting.Value.Bytes))
            {
                if (GrantsInArrivalOrder)
                {
                    break;
                }

                continue;
            }

            _queue.Remove(waiting);
            waiting.Value.Node = null;
            _bytes += waiting.Value.Bytes;
            _calls++;
            (granted ??= []).Add(waiting.Value);
        }

        return granted;
    }

    /// <summary>
    /// Asserts the budget's invariants, under the lock: it never grants beyond its bytes or calls, and, in arrival order, a
    /// call waits only while the call at the head does not fit.
    /// </summary>
    [Conditional("DEBUG")]
    private void AssertLocked()
    {
        Debug.Assert(_bytes <= MaxBytes && _calls <= MaxCalls, "A budget grants no more bytes or calls than it holds.");
        Debug.Assert(!GrantsInArrivalOrder || _queue.First is not { } head || !FitsLocked(head.Value.Bytes), "A call waits only while the head of the queue does not fit.");
    }

    private Grant NewGrant(long bytes) => new(this, bytes);

    /// <summary>Returns whether a call of <paramref name="bytes"/> fits the budget's bytes and calls. Under the lock.</summary>
    private bool FitsLocked(long bytes) => _calls < MaxCalls && _bytes + bytes <= MaxBytes;

    private static void Complete(List<Waiter>? granted)
    {
        if (granted is null)
        {
            return;
        }

        foreach (var waiter in granted)
        {
            // A granted call has left the queue, so its withdrawal finds nothing to remove and never completes it.
            var set = waiter.TrySetResult();
            Debug.Assert(set, "A granted call is completed only by its grant.");
        }
    }

    /// <summary>
    /// A value-type hold on a call's bytes and call of the budget, which returns them once when disposed. Intended for
    /// single-owner <c>using</c> scopes on hot paths where allocating a <see cref="Grant"/> handle is unnecessary; do not
    /// copy an active lease.
    /// </summary>
    public struct Lease : IDisposable
    {
        private CallBudget? _budget;

        internal Lease(CallBudget budget, long bytes)
        {
            _budget = budget;
            Bytes = bytes;
        }

        /// <summary>Gets the bytes the call holds.</summary>
        public readonly long Bytes { get; }

        /// <summary>Returns the call's bytes and call to the budget, once.</summary>
        public void Dispose()
        {
            if (_budget is { } budget)
            {
                _budget = null;
                budget.Release(Bytes);
            }
        }
    }

    /// <summary>A call's bytes and call of the budget, which it returns once, when it is disposed.</summary>
    public sealed class Grant : IDisposable
    {
        private readonly CallBudget _budget;
        private int _released;

        internal Grant(CallBudget budget, long bytes)
        {
            _budget = budget;
            Bytes = bytes;
        }

        /// <summary>Gets the bytes the call holds.</summary>
        public long Bytes { get; }

        /// <summary>Returns the call's bytes and call to the budget, once.</summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                _budget.Release(Bytes);
            }
        }
    }

    /// <summary>A call waiting for budget, failed by its deadline or its caller's cancellation.</summary>
    private sealed class Waiter(CallBudget budget, long bytes, long entered) : TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
    {
        public long Bytes { get; } = bytes;

        private CallBudget Budget => budget;

        private long Entered => entered;

        /// <summary>Gets or sets its place in the queue, which it leaves once it is granted or withdrawn.</summary>
        public LinkedListNode<Waiter>? Node { get; set; }

        public async Task WaitAsync(TimeSpan? remaining, CancellationToken cancellationToken)
        {
            using var registration = cancellationToken.UnsafeRegister(
                static (state, token) =>
                {
                    var waiter = (Waiter)state!;
                    waiter.Budget.Withdraw(waiter, new OperationCanceledException(token));
                },
                this);
            ITimer timer;
            using (ExecutionContext.SuppressFlow())
            {
                timer = budget.TimeProvider.CreateTimer(
                    static state =>
                    {
                        var waiter = (Waiter)state!;
                        waiter.Budget.Withdraw(waiter, waiter.Budget._timeout(waiter.Budget.TimeProvider.GetElapsedTime(waiter.Entered)));
                    },
                    this,
                    remaining is { } left ? budget.WaitFor(left) : Timeout.InfiniteTimeSpan,
                    Timeout.InfiniteTimeSpan);
            }

            await using (timer.ConfigureAwait(false))
            {
                await Task.ConfigureAwait(false);
            }
        }
    }
}
