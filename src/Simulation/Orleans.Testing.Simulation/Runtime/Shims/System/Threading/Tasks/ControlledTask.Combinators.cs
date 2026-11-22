using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Orleans.Testing.Simulation.Runtime.Racing;
using Orleans.Testing.Simulation.Runtime.Shims;

namespace Orleans.Testing.Simulation.Shims.System.Threading.Tasks;

/// <summary>
/// Controlled task combinators. The BCL combinators register completion actions on their antecedents, and those
/// actions inherit the antecedent's dispatch: a task completed with
/// <see cref="TaskCreationOptions.RunContinuationsAsynchronously"/> (or a continuation the BCL queues) completes the
/// combinator's promise on the real thread pool, at a nondeterministic point. These combinators register nothing on
/// their antecedents. A promise whose antecedents are already complete when it is created is computed by the BCL on the
/// logical thread, synchronously, exactly as the BCL does. Otherwise the promise is completed through the coordinator:
/// a readiness wait polls the antecedents' <see cref="Task.IsCompleted"/>, the same mechanism the controlled awaiters
/// use, and a controlled operation completes the promise once the readiness predicate holds.
/// </summary>
public static partial class ControlledTask
{
    private const string WhenAllApi = "System.Threading.Tasks.Task.WhenAll";
    private const string WhenAnyApi = "System.Threading.Tasks.Task.WhenAny";
    private const string UnwrapApi = "System.Threading.Tasks.TaskExtensions.Unwrap";
    private const string TasksParameter = "tasks";

    /// <summary>Controlled <c>Task.WhenAll(Task[])</c>.</summary>
    /// <param name="tasks">The tasks to await.</param>
    /// <returns>A task that completes when all <paramref name="tasks"/> complete.</returns>
    public static Task WhenAll(params Task[] tasks)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(WhenAllApi);
        ArgumentNullException.ThrowIfNull(tasks);
        return WhenAllCore(tasks);
    }

    /// <summary>
    /// Controlled <c>Task.WhenAll(ReadOnlySpan&lt;Task&gt;)</c>: the .NET 9+ params-span overload that a
    /// two-or-more argument <c>Task.WhenAll(a, b, ...)</c> call binds to.
    /// </summary>
    /// <param name="tasks">The tasks to await.</param>
    /// <returns>A task that completes when all <paramref name="tasks"/> complete.</returns>
    public static Task WhenAll(params ReadOnlySpan<Task> tasks)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(WhenAllApi);
        return WhenAllCore(tasks);
    }

    /// <summary>Controlled <c>Task.WhenAll(IEnumerable&lt;Task&gt;)</c>.</summary>
    /// <param name="tasks">The tasks to await.</param>
    /// <returns>A task that completes when all <paramref name="tasks"/> complete.</returns>
    public static Task WhenAll(IEnumerable<Task> tasks)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(WhenAllApi);
        ArgumentNullException.ThrowIfNull(tasks);
        return tasks switch
        {
            Task[] array => WhenAllCore(array),
            List<Task> list => WhenAllCore(CollectionsMarshal.AsSpan(list)),
            _ => WhenAllCore(CollectionsMarshal.AsSpan(Enumerate(tasks))),
        };
    }

    /// <summary>Controlled <c>Task.WhenAll&lt;TResult&gt;(Task&lt;TResult&gt;[])</c>.</summary>
    /// <typeparam name="TResult">The task result type.</typeparam>
    /// <param name="tasks">The tasks to await.</param>
    /// <returns>A task with the ordered results once all complete.</returns>
    public static Task<TResult[]> WhenAll<TResult>(params Task<TResult>[] tasks)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(WhenAllApi);
        ArgumentNullException.ThrowIfNull(tasks);
        return WhenAllCore((ReadOnlySpan<Task<TResult>>)tasks);
    }

    /// <summary>
    /// Controlled <c>Task.WhenAll&lt;TResult&gt;(ReadOnlySpan&lt;Task&lt;TResult&gt;&gt;)</c>: the .NET 9+
    /// params-span overload that a two-or-more argument <c>Task.WhenAll(a, b, ...)</c> of typed tasks
    /// binds to.
    /// </summary>
    /// <typeparam name="TResult">The task result type.</typeparam>
    /// <param name="tasks">The tasks to await.</param>
    /// <returns>A task with the ordered results once all complete.</returns>
    public static Task<TResult[]> WhenAll<TResult>(params ReadOnlySpan<Task<TResult>> tasks)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(WhenAllApi);
        return WhenAllCore(tasks);
    }

    /// <summary>Controlled <c>Task.WhenAll&lt;TResult&gt;(IEnumerable&lt;Task&lt;TResult&gt;&gt;)</c>.</summary>
    /// <typeparam name="TResult">The task result type.</typeparam>
    /// <param name="tasks">The tasks to await.</param>
    /// <returns>A task with the ordered results once all complete.</returns>
    public static Task<TResult[]> WhenAll<TResult>(IEnumerable<Task<TResult>> tasks)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(WhenAllApi);

        // The BCL tests for an array before it tests for null, and enumerates any other sequence once, failing at
        // its first null element.
        if (tasks is Task<TResult>[] array)
        {
            return WhenAllCore((ReadOnlySpan<Task<TResult>>)array);
        }

        if (tasks is ICollection<Task<TResult>> collection)
        {
            var copy = new Task<TResult>[collection.Count];
            collection.CopyTo(copy, 0);
            return WhenAllCore((ReadOnlySpan<Task<TResult>>)copy);
        }

        ArgumentNullException.ThrowIfNull(tasks);
        return WhenAllCore(CollectionsMarshal.AsSpan(EnumerateNonNull(tasks)));
    }

    /// <summary>Controlled <c>Task.WhenAny(Task[])</c>.</summary>
    /// <param name="tasks">The candidate tasks.</param>
    /// <returns>A task whose result is the first task to complete.</returns>
    public static Task<Task> WhenAny(params Task[] tasks)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(WhenAnyApi);
        ArgumentNullException.ThrowIfNull(tasks);
        return WhenAnyCore((ReadOnlySpan<Task>)tasks);
    }

    /// <summary>
    /// Controlled <c>Task.WhenAny(ReadOnlySpan&lt;Task&gt;)</c>: the .NET 9+ params-span overload that a
    /// three-or-more argument <c>Task.WhenAny(a, b, c, ...)</c> call binds to.
    /// </summary>
    /// <param name="tasks">The candidate tasks.</param>
    /// <returns>A task whose result is the first task to complete.</returns>
    public static Task<Task> WhenAny(params ReadOnlySpan<Task> tasks)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(WhenAnyApi);
        return WhenAnyCore(tasks);
    }

    /// <summary>
    /// Controlled <c>Task.WhenAny(Task, Task)</c>: the two-argument overload that <c>Task.WhenAny(a, b)</c>
    /// binds to.
    /// </summary>
    /// <param name="task1">The first candidate task.</param>
    /// <param name="task2">The second candidate task.</param>
    /// <returns>A task whose result is the first task to complete.</returns>
    public static Task<Task> WhenAny(Task task1, Task task2)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(WhenAnyApi);
        return WhenAnyPair(task1, task2);
    }

    /// <summary>Controlled <c>Task.WhenAny(IEnumerable&lt;Task&gt;)</c>.</summary>
    /// <param name="tasks">The candidate tasks.</param>
    /// <returns>A task whose result is the first task to complete.</returns>
    public static Task<Task> WhenAny(IEnumerable<Task> tasks)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(WhenAnyApi);
        return WhenAnyEnumerable(tasks);
    }

    /// <summary>Controlled <c>Task.WhenAny&lt;TResult&gt;(Task&lt;TResult&gt;[])</c>.</summary>
    /// <typeparam name="TResult">The task result type.</typeparam>
    /// <param name="tasks">The candidate tasks.</param>
    /// <returns>A task whose result is the first task to complete.</returns>
    public static Task<Task<TResult>> WhenAny<TResult>(params Task<TResult>[] tasks)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(WhenAnyApi);
        ArgumentNullException.ThrowIfNull(tasks);
        return WhenAnyCore((ReadOnlySpan<Task<TResult>>)tasks);
    }

    /// <summary>
    /// Controlled <c>Task.WhenAny&lt;TResult&gt;(ReadOnlySpan&lt;Task&lt;TResult&gt;&gt;)</c>: the .NET 9+
    /// params-span overload that a three-or-more argument typed <c>Task.WhenAny(a, b, c, ...)</c> binds to.
    /// </summary>
    /// <typeparam name="TResult">The task result type.</typeparam>
    /// <param name="tasks">The candidate tasks.</param>
    /// <returns>A task whose result is the first task to complete.</returns>
    public static Task<Task<TResult>> WhenAny<TResult>(params ReadOnlySpan<Task<TResult>> tasks)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(WhenAnyApi);
        return WhenAnyCore(tasks);
    }

    /// <summary>
    /// Controlled <c>Task.WhenAny&lt;TResult&gt;(Task&lt;TResult&gt;, Task&lt;TResult&gt;)</c>: the
    /// two-argument overload that a typed <c>Task.WhenAny(a, b)</c> binds to.
    /// </summary>
    /// <typeparam name="TResult">The task result type.</typeparam>
    /// <param name="task1">The first candidate task.</param>
    /// <param name="task2">The second candidate task.</param>
    /// <returns>A task whose result is the first task to complete.</returns>
    public static Task<Task<TResult>> WhenAny<TResult>(Task<TResult> task1, Task<TResult> task2)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(WhenAnyApi);
        return WhenAnyPair(task1, task2);
    }

    /// <summary>Controlled <c>Task.WhenAny&lt;TResult&gt;(IEnumerable&lt;Task&lt;TResult&gt;&gt;)</c>.</summary>
    /// <typeparam name="TResult">The task result type.</typeparam>
    /// <param name="tasks">The candidate tasks.</param>
    /// <returns>A task whose result is the first task to complete.</returns>
    public static Task<Task<TResult>> WhenAny<TResult>(IEnumerable<Task<TResult>> tasks)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(WhenAnyApi);
        return WhenAnyEnumerable(tasks);
    }

    /// <summary>Controlled <c>TaskExtensions.Unwrap(this Task&lt;Task&gt;)</c>.</summary>
    /// <param name="task">The nested task to unwrap.</param>
    /// <returns>A task representing the completion of the inner task.</returns>
    public static Task Unwrap(Task<Task> task)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(UnwrapApi);
        ArgumentNullException.ThrowIfNull(task);

        // Once the outer task has completed the BCL completes or returns the proxy synchronously; when it completed
        // successfully, the proxy is the inner task itself. That task carries only its own edges, so the outer task's
        // edge is acquired here, as the outer task is observed complete.
        if (task.IsCompleted)
        {
            Task unwrapped = task.Unwrap();
            if (task.IsCompletedSuccessfully && ReferenceEquals(unwrapped, task.Result))
            {
                RaceSynchronization.Wait(task);
                return unwrapped;
            }

            return TrackUnwrap(unwrapped, task, () => task.Result);
        }

        var promise = new TaskCompletionSource(task.CreationOptions & TaskCreationOptions.AttachedToParent);
        CompleteWhenReady(
            () => IsUnwrapReady(task),
            () =>
            {
                if (task.Status != TaskStatus.RanToCompletion)
                {
                    SimulationCompletion.TrySetFromTask(promise, task.Unwrap(), UnwrapApi);
                }
                else if (task.Result is Task inner)
                {
                    SimulationCompletion.TrySetFromTask(promise, inner, UnwrapApi);
                }
                else
                {
                    // The BCL's unwrap promise cancels with the default token when the inner task is null.
                    SimulationCompletion.TrySetCanceled(promise, default, UnwrapApi);
                }
            },
            UnwrapApi);
        return TrackUnwrap(promise.Task, task, () => task.Result);
    }

    /// <summary>Controlled <c>TaskExtensions.Unwrap&lt;TResult&gt;(this Task&lt;Task&lt;TResult&gt;&gt;)</c>.</summary>
    /// <typeparam name="TResult">The inner task result type.</typeparam>
    /// <param name="task">The nested task to unwrap.</param>
    /// <returns>A task representing the completion (and result) of the inner task.</returns>
    public static Task<TResult> Unwrap<TResult>(Task<Task<TResult>> task)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(UnwrapApi);
        ArgumentNullException.ThrowIfNull(task);
        if (task.IsCompleted)
        {
            Task<TResult> unwrapped = task.Unwrap();
            if (task.IsCompletedSuccessfully && ReferenceEquals(unwrapped, task.Result))
            {
                RaceSynchronization.Wait(task);
                return unwrapped;
            }

            return TrackUnwrap(unwrapped, task, () => task.Result);
        }

        var promise = new TaskCompletionSource<TResult>(task.CreationOptions & TaskCreationOptions.AttachedToParent);
        CompleteWhenReady(
            () => IsUnwrapReady(task),
            () =>
            {
                if (task.Status != TaskStatus.RanToCompletion)
                {
                    // The outer task has completed, so the BCL proxy transfers its fault or cancellation synchronously.
                    SimulationCompletion.TrySetFromTask(promise, task.Unwrap(), UnwrapApi);
                }
                else if (task.Result is Task<TResult> inner)
                {
                    SimulationCompletion.TrySetFromTask(promise, inner, UnwrapApi);
                }
                else
                {
                    SimulationCompletion.TrySetCanceled(promise, default, UnwrapApi);
                }
            },
            UnwrapApi);
        return TrackUnwrap(promise.Task, task, () => task.Result);
    }

    private static Task WhenAllCore(ReadOnlySpan<Task> tasks)
    {
        // The BCL returns the completed singleton for no tasks and the task itself for one.
        switch (tasks.Length)
        {
            case 0:
                return Task.CompletedTask;
            case 1:
                return tasks[0] ?? throw NullTask();
        }

        Task[] copy = tasks.ToArray();
        ThrowIfAnyNull(copy);

        // The BCL's non-generic WhenAll aggregates faults, and picks the canceled task, in the order the antecedents
        // complete. The coordinator observes completions between scheduling steps, so antecedents observed complete
        // in the same step are ordered by argument position.
        var order = new CompletionOrder<Task>(copy);
        if (order.Observe())
        {
            return Track(Task.WhenAll(order.Completed), copy);
        }

        var promise = new TaskCompletionSource();
        CompleteWhenReady(order.Observe, () => SimulationCompletion.TrySetFromTask(promise, Task.WhenAll(order.Completed), WhenAllApi), WhenAllApi);
        return Track(promise.Task, copy);
    }

    private static Task<TResult[]> WhenAllCore<TResult>(ReadOnlySpan<Task<TResult>> tasks)
    {
        if (tasks.IsEmpty)
        {
            return Task.FromResult(Array.Empty<TResult>());
        }

        Task<TResult>[] copy = tasks.ToArray();
        ThrowIfAnyNull(copy);

        // The generic WhenAll aggregates faults, results, and the canceled task in argument order, so the BCL computes
        // the exact outcome from the completed antecedents.
        var progress = new CompletionProgress(copy);
        if (progress.Observe())
        {
            return Track(Task.WhenAll(copy), copy);
        }

        var promise = new TaskCompletionSource<TResult[]>();
        CompleteWhenReady(progress.Observe, () => SimulationCompletion.TrySetFromTask(promise, Task.WhenAll(copy), WhenAllApi), WhenAllApi);
        return Track(promise.Task, copy);
    }

    private static Task<TTask> WhenAnyCore<TTask>(ReadOnlySpan<TTask> tasks)
        where TTask : Task
    {
        // The BCL routes exactly two tasks through the pair overload, which validates each argument by name.
        if (tasks.Length == 2)
        {
            return WhenAnyPair(tasks[0], tasks[1]);
        }

        if (tasks.IsEmpty)
        {
            throw new ArgumentException("The tasks argument contains no tasks.", TasksParameter);
        }

        TTask[] copy = tasks.ToArray();
        ThrowIfAnyNull(copy);
        return WhenAnyPromise(copy);
    }

    private static Task<TTask> WhenAnyPair<TTask>(TTask task1, TTask task2)
        where TTask : Task
    {
        ArgumentNullException.ThrowIfNull(task1);
        ArgumentNullException.ThrowIfNull(task2);
        return WhenAnyPromise([task1, task2]);
    }

    private static Task<TTask> WhenAnyEnumerable<TTask>(IEnumerable<TTask> tasks)
        where TTask : Task
    {
        if (tasks is ICollection<TTask> collection)
        {
            if (tasks.GetType() == typeof(List<TTask>))
            {
                return WhenAnyCore((ReadOnlySpan<TTask>)CollectionsMarshal.AsSpan((List<TTask>)tasks));
            }

            if (tasks is TTask[] array)
            {
                return WhenAnyCore((ReadOnlySpan<TTask>)array);
            }

            if (collection.Count <= 0)
            {
                throw new ArgumentException("The tasks argument contains no tasks.", TasksParameter);
            }

            var copy = new TTask[collection.Count];
            collection.CopyTo(copy, 0);
            ThrowIfAnyNull(copy);
            return WhenAnyPromise(copy);
        }

        ArgumentNullException.ThrowIfNull(tasks);
        List<TTask> list = EnumerateNonNull(tasks);
        if (list.Count == 0)
        {
            throw new ArgumentException("The tasks argument contains no tasks.", TasksParameter);
        }

        return WhenAnyPromise([.. list]);
    }

    /// <summary>
    /// Completes with the first antecedent, in argument order, among those the coordinator observes complete. The BCL
    /// picks the first antecedent to complete, which for antecedents already complete at the call is the first in
    /// argument order; the coordinator observes completions between scheduling steps, so the winner among antecedents
    /// completing in the same step is the first in argument order. Every antecedent is pending until the winner is
    /// observed, so each readiness check costs O(pending).
    /// </summary>
    private static Task<TTask> WhenAnyPromise<TTask>(TTask[] tasks)
        where TTask : Task
    {
        if (FirstCompleted(tasks) is { } completed)
        {
            return TrackWinner(Task.FromResult(completed));
        }

        TTask? winner = null;
        var promise = new TaskCompletionSource<TTask>();
        CompleteWhenReady(
            () => (winner = FirstCompleted(tasks)) is not null,
            () => SimulationCompletion.TrySetResult(promise, winner!, WhenAnyApi),
            WhenAnyApi);
        return TrackWinner(promise.Task);
    }

    /// <summary>
    /// Schedules <paramref name="complete"/> as a controlled operation once <paramref name="isReady"/> holds; see
    /// <see cref="SimulationTaskRuntime.ScheduleWhenReady(Func{bool}, Action, string)"/> for the predicate's contract.
    /// </summary>
    private static void CompleteWhenReady(Func<bool> isReady, Action complete, string apiName) =>
        SimulationTaskRuntime.ScheduleWhenReady(isReady, complete, apiName);

    private static bool IsUnwrapReady(Task<Task> task) =>
        task.IsCompleted && (task.Status != TaskStatus.RanToCompletion || task.Result is not { IsCompleted: false });

    private static bool IsUnwrapReady<TResult>(Task<Task<TResult>> task) =>
        task.IsCompleted && (task.Status != TaskStatus.RanToCompletion || task.Result is not { IsCompleted: false });

    private static TTask TrackUnwrap<TTask>(TTask proxy, Task outer, Func<Task?> inner)
        where TTask : Task =>
        RaceTaskDependencies.Register(
            proxy,
            () => outer.Status == TaskStatus.RanToCompletion && inner() is { } completed
                ? [outer, completed]
                : [outer]);

    private static TTask Track<TTask>(TTask proxy, Task[] dependencies)
        where TTask : Task =>
        RaceTaskDependencies.Register(proxy, () => dependencies);

    private static Task<TTask> TrackWinner<TTask>(Task<TTask> proxy)
        where TTask : Task =>
        RaceTaskDependencies.Register(
            proxy,
            () => proxy.IsCompletedSuccessfully ? [proxy.Result] : []);

    private static bool AllCompleted(Task[] tasks)
    {
        foreach (Task task in tasks)
        {
            if (!task.IsCompleted)
            {
                return false;
            }
        }

        return true;
    }

    private static TTask? FirstCompleted<TTask>(TTask[] tasks)
        where TTask : Task
    {
        foreach (TTask task in tasks)
        {
            if (task.IsCompleted)
            {
                return task;
            }
        }

        return null;
    }

    private static void ThrowIfAnyNull<TTask>(TTask[] tasks)
        where TTask : Task
    {
        foreach (TTask task in tasks)
        {
            if (task is null)
            {
                throw NullTask();
            }
        }
    }

    private static ArgumentException NullTask() =>
        new("The tasks argument included a null value.", TasksParameter);

    private static List<Task> Enumerate(IEnumerable<Task> tasks)
    {
        var list = new List<Task>();
        foreach (Task task in tasks)
        {
            list.Add(task);
        }

        return list;
    }

    private static List<TTask> EnumerateNonNull<TTask>(IEnumerable<TTask> tasks)
        where TTask : Task
    {
        var list = new List<TTask>();
        foreach (TTask task in tasks)
        {
            if (task is null)
            {
                throw NullTask();
            }

            list.Add(task);
        }

        return list;
    }

    /// <summary>
    /// Records the order in which the coordinator observes antecedents complete; antecedents observed complete in the
    /// same scheduling step are recorded in argument order. The antecedents not yet observed complete are kept compacted
    /// in place, in argument order, so an observation costs O(pending).
    /// </summary>
    /// <typeparam name="TTask">The antecedent task type.</typeparam>
    private sealed class CompletionOrder<TTask>
        where TTask : Task
    {
        private readonly TTask[] _tasks;
        private readonly int[] _pending;
        private readonly List<TTask> _observed;
        private int _pendingCount;

        public CompletionOrder(TTask[] tasks)
        {
            _tasks = tasks;
            _pending = new int[tasks.Length];
            for (int i = 0; i < tasks.Length; i++)
            {
                _pending[i] = i;
            }

            _pendingCount = tasks.Length;
            _observed = new List<TTask>(tasks.Length);
        }

        /// <summary>Gets the antecedents observed complete, in the order observed.</summary>
        public IReadOnlyList<TTask> Observed => _observed;

        /// <summary>Gets the antecedents observed complete, in the order observed, as an array.</summary>
        public TTask[] Completed => [.. _observed];

        /// <summary>Records the antecedents which have completed since the last observation.</summary>
        /// <returns>Whether every antecedent has been observed complete.</returns>
        public bool Observe()
        {
            int kept = 0;
            for (int i = 0; i < _pendingCount; i++)
            {
                int index = _pending[i];
                if (_tasks[index].IsCompleted)
                {
                    _observed.Add(_tasks[index]);
                }
                else
                {
                    _pending[kept++] = index;
                }
            }

            _pendingCount = kept;
            return kept == 0;
        }
    }

    /// <summary>
    /// Observes whether every antecedent has completed. Antecedents only ever become complete, so each observation
    /// resumes from the first antecedent not yet observed complete, at an amortized O(1) per scheduling step.
    /// </summary>
    private sealed class CompletionProgress(Task[] tasks)
    {
        private int _next;

        /// <summary>Advances past the antecedents which have completed, in argument order.</summary>
        /// <returns>Whether every antecedent has completed.</returns>
        public bool Observe()
        {
            while (_next < tasks.Length && tasks[_next].IsCompleted)
            {
                _next++;
            }

            return _next == tasks.Length;
        }
    }
}
