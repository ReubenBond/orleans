using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;
using Orleans.Testing.Simulation.Runtime.Racing;
using Orleans.Testing.Simulation.Runtime.Shims;
using Orleans.Testing.Simulation.Runtime.Tasks;

namespace Orleans.Testing.Simulation.Shims.System.Threading.Tasks;

/// <summary>
/// <para>
/// Controlled <c>Task.WhenEach</c>, every .NET 11 overload. The BCL registers a completion action on each task when
/// <c>WhenEach</c> is called and yields the tasks in the order those actions run, through an async iterator which
/// awaits a <see cref="ManualResetValueTaskSourceCore{TResult}"/> that runs its continuations asynchronously, on the
/// thread pool. Here the coordinator observes the tasks complete from the call on, as the other controlled combinators
/// do, and records the order it observes; tasks observed complete in the same scheduling step are ordered by argument
/// position. <see cref="IAsyncEnumerator{T}.MoveNextAsync"/> returns a value task which a controlled operation completes
/// once a task is available, and which a controlled await observes through the coordinator.
/// </para>
/// <para>
/// Argument validation, the order of tasks already complete at the call (argument order), the single enumeration (the
/// first enumerator to start yields every task, and any other yields none), cancellation through the enumerator's token
/// (<see cref="OperationCanceledException"/> when a task is available, <see cref="TaskCanceledException"/> while
/// waiting for one), and <see cref="IAsyncEnumerator{T}.Current"/> are the BCL's. An enumerator acquires each yielded
/// task's race-detection edge.
/// </para>
/// </summary>
public static partial class ControlledTask
{
    private const string WhenEachApi = "System.Threading.Tasks.Task.WhenEach";

    /// <summary>Controlled <c>Task.WhenEach(Task[])</c>.</summary>
    /// <param name="tasks">The tasks to yield as they complete.</param>
    /// <returns>An asynchronous sequence of the tasks, in the order they complete.</returns>
    public static IAsyncEnumerable<Task> WhenEach(params Task[] tasks)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(WhenEachApi);
        ArgumentNullException.ThrowIfNull(tasks);
        return WhenEachCore((ReadOnlySpan<Task>)tasks);
    }

    /// <summary>Controlled <c>Task.WhenEach(ReadOnlySpan&lt;Task&gt;)</c>.</summary>
    /// <param name="tasks">The tasks to yield as they complete.</param>
    /// <returns>An asynchronous sequence of the tasks, in the order they complete.</returns>
    public static IAsyncEnumerable<Task> WhenEach(params ReadOnlySpan<Task> tasks)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(WhenEachApi);
        return WhenEachCore(tasks);
    }

    /// <summary>Controlled <c>Task.WhenEach(IEnumerable&lt;Task&gt;)</c>.</summary>
    /// <param name="tasks">The tasks to yield as they complete.</param>
    /// <returns>An asynchronous sequence of the tasks, in the order they complete.</returns>
    public static IAsyncEnumerable<Task> WhenEach(IEnumerable<Task> tasks)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(WhenEachApi);
        ArgumentNullException.ThrowIfNull(tasks);
        return new WhenEachSequence<Task>([.. EnumerateNonNull(tasks)]);
    }

    /// <summary>Controlled <c>Task.WhenEach&lt;TResult&gt;(Task&lt;TResult&gt;[])</c>.</summary>
    /// <typeparam name="TResult">The task result type.</typeparam>
    /// <param name="tasks">The tasks to yield as they complete.</param>
    /// <returns>An asynchronous sequence of the tasks, in the order they complete.</returns>
    public static IAsyncEnumerable<Task<TResult>> WhenEach<TResult>(params Task<TResult>[] tasks)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(WhenEachApi);
        ArgumentNullException.ThrowIfNull(tasks);
        return WhenEachCore((ReadOnlySpan<Task<TResult>>)tasks);
    }

    /// <summary>Controlled <c>Task.WhenEach&lt;TResult&gt;(ReadOnlySpan&lt;Task&lt;TResult&gt;&gt;)</c>.</summary>
    /// <typeparam name="TResult">The task result type.</typeparam>
    /// <param name="tasks">The tasks to yield as they complete.</param>
    /// <returns>An asynchronous sequence of the tasks, in the order they complete.</returns>
    public static IAsyncEnumerable<Task<TResult>> WhenEach<TResult>(params ReadOnlySpan<Task<TResult>> tasks)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(WhenEachApi);
        return WhenEachCore(tasks);
    }

    /// <summary>Controlled <c>Task.WhenEach&lt;TResult&gt;(IEnumerable&lt;Task&lt;TResult&gt;&gt;)</c>.</summary>
    /// <typeparam name="TResult">The task result type.</typeparam>
    /// <param name="tasks">The tasks to yield as they complete.</param>
    /// <returns>An asynchronous sequence of the tasks, in the order they complete.</returns>
    public static IAsyncEnumerable<Task<TResult>> WhenEach<TResult>(IEnumerable<Task<TResult>> tasks)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(WhenEachApi);
        ArgumentNullException.ThrowIfNull(tasks);
        return new WhenEachSequence<Task<TResult>>([.. EnumerateNonNull(tasks)]);
    }

    private static WhenEachSequence<TTask> WhenEachCore<TTask>(ReadOnlySpan<TTask> tasks)
        where TTask : Task
    {
        TTask[] copy = tasks.ToArray();
        ThrowIfAnyNull(copy);
        return new WhenEachSequence<TTask>(copy);
    }

    /// <summary>
    /// The sequence <c>Task.WhenEach</c> returns, and the state its enumerators share: the BCL yields each task once, to
    /// the first enumerator to start.
    /// </summary>
    /// <typeparam name="TTask">The task type.</typeparam>
    private sealed class WhenEachSequence<TTask> : IAsyncEnumerable<TTask>
        where TTask : Task
    {
        private readonly int _count;
        private readonly CompletionOrder<TTask>? _order;
        private int _started;
        private int _yielded;
        private bool _abandoned;

        public WhenEachSequence(TTask[] tasks)
        {
            _count = tasks.Length;
            if (tasks.Length == 0)
            {
                return;
            }

            // The BCL orders the tasks as their completion actions run, whether or not an enumerator is waiting, so the
            // coordinator observes them from the call on, until each has completed or the enumeration has ended.
            _order = new CompletionOrder<TTask>(tasks);
            if (!_order.Observe())
            {
                SimulationTaskRuntime.ScheduleWhenReady(() => _abandoned || _order.Observe(), static () => { }, WhenEachApi);
            }
        }

        /// <summary>Gets the number of tasks not yet yielded.</summary>
        public int Remaining => _count - _yielded;

        public IAsyncEnumerator<TTask> GetAsyncEnumerator(CancellationToken cancellationToken = default) =>
            new WhenEachEnumerator<TTask>(this, cancellationToken);

        /// <summary>Claims the tasks for the first enumerator to start.</summary>
        public bool TryStart() => _order is not null && Interlocked.Exchange(ref _started, 1) == 0;

        /// <summary>Stops observing the tasks once the enumerator which claimed them has ended.</summary>
        public void Abandon() => _abandoned = true;

        /// <summary>Gets whether a task observed complete has not yet been yielded.</summary>
        public bool HasNext()
        {
            _ = _order!.Observe();
            return _yielded < _order.Observed.Count;
        }

        /// <summary>Takes the next task observed complete, if any.</summary>
        public bool TryTakeNext(out TTask next)
        {
            if (!HasNext())
            {
                next = null!;
                return false;
            }

            next = _order!.Observed[_yielded++];
            return true;
        }
    }

    /// <summary>
    /// An enumerator of <see cref="WhenEachSequence{TTask}"/>, with the BCL async iterator's semantics. It is its own
    /// value-task source, as the iterator is, so a cancellation completes <see cref="MoveNextAsync"/> as canceled.
    /// </summary>
    /// <typeparam name="TTask">The task type.</typeparam>
    private sealed class WhenEachEnumerator<TTask>(WhenEachSequence<TTask> sequence, CancellationToken cancellationToken)
        : IAsyncEnumerator<TTask>, IValueTaskSource<bool>
        where TTask : Task
    {
        private ManualResetValueTaskSourceCore<bool> _promise;
        private bool _started;
        private bool _owner;
        private bool _finished;
        private bool _waiting;

        public TTask Current { get; private set; } = null!;

        public ValueTask<bool> MoveNextAsync()
        {
            if (_finished)
            {
                return new ValueTask<bool>(false);
            }

            if (!_started)
            {
                _started = true;
                _owner = sequence.TryStart();
                if (!_owner)
                {
                    return Finish();
                }
            }

            if (sequence.Remaining == 0)
            {
                return Finish();
            }

            if (sequence.TryTakeNext(out TTask next))
            {
                return Yield(next);
            }

            // The BCL waits through WaitAsync(cancellationToken), which fails at once for a canceled token.
            if (cancellationToken.IsCancellationRequested)
            {
                return Fail(WaitCanceled());
            }

            _promise.Reset();
            _waiting = true;
            SimulationTaskRuntime.ScheduleWhenReady(
                () => sequence.HasNext() || cancellationToken.IsCancellationRequested,
                () =>
                {
                    _waiting = false;
                    if (!sequence.TryTakeNext(out TTask available))
                    {
                        Complete(WaitCanceled());
                    }
                    else if (cancellationToken.IsCancellationRequested)
                    {
                        Complete(new OperationCanceledException(cancellationToken));
                    }
                    else
                    {
                        Current = available;
                        SimulationCompletion.Run(
                            ref _promise,
                            true,
                            static (ref ManualResetValueTaskSourceCore<bool> promise, bool result) => promise.SetResult(result),
                            WhenEachApi);
                    }
                },
                WhenEachApi);
            return new ValueTask<bool>(this, _promise.Version);
        }

        public ValueTask DisposeAsync()
        {
            // The BCL's async iterator cannot be disposed while a MoveNextAsync is pending.
            if (_waiting)
            {
                throw new NotSupportedException();
            }

            End();
            return default;
        }

        public bool GetResult(short token)
        {
            bool available = _promise.GetResult(token);
            if (available)
            {
                RaceSynchronization.Wait(Current);
            }

            return available;
        }

        public ValueTaskSourceStatus GetStatus(short token) => _promise.GetStatus(token);

        public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags) =>
            _promise.OnCompleted(continuation, state, token, flags);

        private ValueTask<bool> Yield(TTask next)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return Fail(new OperationCanceledException(cancellationToken));
            }

            Current = next;
            RaceSynchronization.Wait(next);
            return new ValueTask<bool>(true);
        }

        private ValueTask<bool> Finish()
        {
            End();
            return new ValueTask<bool>(false);
        }

        private ValueTask<bool> Fail(Exception exception)
        {
            _promise.Reset();
            Complete(exception);
            return new ValueTask<bool>(this, _promise.Version);
        }

        private void Complete(Exception exception)
        {
            End();
            SimulationCompletion.Run(
                ref _promise,
                exception,
                static (ref ManualResetValueTaskSourceCore<bool> promise, Exception exception) => promise.SetException(exception),
                WhenEachApi);
        }

        // An ended iterator's Current is default, and the tasks it claimed need no further observation.
        private void End()
        {
            _finished = true;
            Current = null!;
            if (_owner)
            {
                sequence.Abandon();
            }
        }

        // Awaiting the canceled WaitAsync task throws the TaskCanceledException the BCL's iterator propagates.
        private TaskCanceledException WaitCanceled() => new(Task.FromCanceled(cancellationToken));
    }
}
