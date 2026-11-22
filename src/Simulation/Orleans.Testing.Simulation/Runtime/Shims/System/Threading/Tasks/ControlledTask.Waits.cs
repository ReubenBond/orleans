using System.Threading;
using System.Threading.Tasks;
using Orleans.Testing.Simulation.Runtime.Racing;
using Orleans.Testing.Simulation.Runtime.Shims;
using Orleans.Testing.Simulation.Runtime.Tasks;

namespace Orleans.Testing.Simulation.Shims.System.Threading.Tasks;

public static partial class ControlledTask
{
    private const string WaitApi = "System.Threading.Tasks.Task.Wait";
    private const string WaitAllApi = "System.Threading.Tasks.Task.WaitAll";
    private const string WaitAnyApi = "System.Threading.Tasks.Task.WaitAny";

    /// <summary>
    /// Controlled <c>task.Wait(int)</c>. The timed and cancellable waits pump the coordinator until the task completes,
    /// the token is cancelled, or the timeout elapses in virtual time (which advances only when nothing else can run),
    /// then let the BCL compute the outcome from the completed task, so they return and throw what the BCL does.
    /// </summary>
    /// <param name="task">The task to wait for.</param>
    /// <param name="millisecondsTimeout">The timeout in milliseconds, or -1 to wait indefinitely.</param>
    /// <returns><see langword="true"/> if the task completed within the timeout.</returns>
    public static bool Wait(Task task, int millisecondsTimeout)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(WaitApi);
        return WaitCore(task, millisecondsTimeout, CancellationToken.None);
    }

    /// <summary>Controlled <c>task.Wait(int, CancellationToken)</c>; see <see cref="Wait(Task, int)"/>.</summary>
    /// <param name="task">The task to wait for.</param>
    /// <param name="millisecondsTimeout">The timeout in milliseconds, or -1 to wait indefinitely.</param>
    /// <param name="cancellationToken">The token that cancels the wait.</param>
    /// <returns><see langword="true"/> if the task completed within the timeout.</returns>
    public static bool Wait(Task task, int millisecondsTimeout, CancellationToken cancellationToken)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(WaitApi);
        return WaitCore(task, millisecondsTimeout, cancellationToken);
    }

    /// <summary>Controlled <c>task.Wait(CancellationToken)</c>; see <see cref="Wait(Task, int)"/>.</summary>
    /// <param name="task">The task to wait for.</param>
    /// <param name="cancellationToken">The token that cancels the wait.</param>
    public static void Wait(Task task, CancellationToken cancellationToken)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(WaitApi);
        WaitCore(task, Timeout.Infinite, cancellationToken);
    }

    /// <summary>Controlled <c>task.Wait(TimeSpan)</c>; see <see cref="Wait(Task, int)"/>.</summary>
    /// <param name="task">The task to wait for.</param>
    /// <param name="timeout">The timeout, or <see cref="Timeout.InfiniteTimeSpan"/> to wait indefinitely.</param>
    /// <returns><see langword="true"/> if the task completed within the timeout.</returns>
    public static bool Wait(Task task, TimeSpan timeout)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(WaitApi);
        return WaitCore(task, ToMilliseconds(timeout), CancellationToken.None);
    }

    /// <summary>Controlled <c>task.Wait(TimeSpan, CancellationToken)</c>; see <see cref="Wait(Task, int)"/>.</summary>
    /// <param name="task">The task to wait for.</param>
    /// <param name="timeout">The timeout, or <see cref="Timeout.InfiniteTimeSpan"/> to wait indefinitely.</param>
    /// <param name="cancellationToken">The token that cancels the wait.</param>
    /// <returns><see langword="true"/> if the task completed within the timeout.</returns>
    public static bool Wait(Task task, TimeSpan timeout, CancellationToken cancellationToken)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(WaitApi);
        int millisecondsTimeout = ToMilliseconds(timeout);

        // As in the BCL, the TimeSpan overload observes the token first, even with a zero timeout or a completed task;
        // the int overload does not.
        cancellationToken.ThrowIfCancellationRequested();
        return WaitCore(task, millisecondsTimeout, cancellationToken);
    }

    /// <summary>Controlled <c>Task.WaitAll(Task[], int)</c>; see <see cref="Wait(Task, int)"/>.</summary>
    /// <param name="tasks">The tasks to wait for.</param>
    /// <param name="millisecondsTimeout">The timeout in milliseconds, or -1 to wait indefinitely.</param>
    /// <returns><see langword="true"/> if every task completed within the timeout.</returns>
    public static bool WaitAll(Task[] tasks, int millisecondsTimeout)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(WaitAllApi);
        return WaitAllCore(tasks, millisecondsTimeout, WaitAllApi, CancellationToken.None);
    }

    /// <summary>Controlled <c>Task.WaitAll(Task[], int, CancellationToken)</c>; see <see cref="Wait(Task, int)"/>.</summary>
    /// <param name="tasks">The tasks to wait for.</param>
    /// <param name="millisecondsTimeout">The timeout in milliseconds, or -1 to wait indefinitely.</param>
    /// <param name="cancellationToken">The token that cancels the wait.</param>
    /// <returns><see langword="true"/> if every task completed within the timeout.</returns>
    public static bool WaitAll(Task[] tasks, int millisecondsTimeout, CancellationToken cancellationToken)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(WaitAllApi);
        return WaitAllCore(tasks, millisecondsTimeout, WaitAllApi, cancellationToken);
    }

    /// <summary>Controlled <c>Task.WaitAll(Task[], CancellationToken)</c>; see <see cref="Wait(Task, int)"/>.</summary>
    /// <param name="tasks">The tasks to wait for.</param>
    /// <param name="cancellationToken">The token that cancels the wait.</param>
    public static void WaitAll(Task[] tasks, CancellationToken cancellationToken)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(WaitAllApi);
        WaitAllCore(tasks, Timeout.Infinite, WaitAllApi, cancellationToken);
    }

    /// <summary>Controlled <c>Task.WaitAll(Task[], TimeSpan)</c>; see <see cref="Wait(Task, int)"/>.</summary>
    /// <param name="tasks">The tasks to wait for.</param>
    /// <param name="timeout">The timeout, or <see cref="Timeout.InfiniteTimeSpan"/> to wait indefinitely.</param>
    /// <returns><see langword="true"/> if every task completed within the timeout.</returns>
    public static bool WaitAll(Task[] tasks, TimeSpan timeout)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(WaitAllApi);
        return WaitAllCore(tasks, ToMilliseconds(timeout), WaitAllApi, CancellationToken.None);
    }

    /// <summary>Controlled <c>Task.WaitAll(IEnumerable&lt;Task&gt;, CancellationToken)</c>; see <see cref="Wait(Task, int)"/>.</summary>
    /// <param name="tasks">The tasks to wait for, enumerated once.</param>
    /// <param name="cancellationToken">The token that cancels the wait.</param>
    public static void WaitAll(IEnumerable<Task> tasks, CancellationToken cancellationToken)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(WaitAllApi);
        ArgumentNullException.ThrowIfNull(tasks);
        WaitAllCore([.. tasks], Timeout.Infinite, WaitAllApi, cancellationToken);
    }

    /// <summary>Controlled <c>Task.WaitAll(ReadOnlySpan&lt;Task&gt;)</c>; see <see cref="Wait(Task, int)"/>.</summary>
    /// <param name="tasks">The tasks to wait for.</param>
    public static void WaitAll(ReadOnlySpan<Task> tasks)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(WaitAllApi);
        WaitAllCore(tasks.ToArray(), Timeout.Infinite, WaitAllApi, CancellationToken.None);
    }

    /// <summary>Controlled <c>Task.WaitAny(Task[], int)</c>; see <see cref="Wait(Task, int)"/>.</summary>
    /// <param name="tasks">The candidate tasks.</param>
    /// <param name="millisecondsTimeout">The timeout in milliseconds, or -1 to wait indefinitely.</param>
    /// <returns>The index of the completed task with the lowest index, or -1 if none completed within the timeout.</returns>
    public static int WaitAny(Task[] tasks, int millisecondsTimeout)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(WaitAnyApi);
        return WaitAnyCore(tasks, millisecondsTimeout, CancellationToken.None);
    }

    /// <summary>Controlled <c>Task.WaitAny(Task[], int, CancellationToken)</c>; see <see cref="WaitAny(Task[], int)"/>.</summary>
    /// <param name="tasks">The candidate tasks.</param>
    /// <param name="millisecondsTimeout">The timeout in milliseconds, or -1 to wait indefinitely.</param>
    /// <param name="cancellationToken">The token that cancels the wait.</param>
    /// <returns>The index of the completed task with the lowest index, or -1 if none completed within the timeout.</returns>
    public static int WaitAny(Task[] tasks, int millisecondsTimeout, CancellationToken cancellationToken)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(WaitAnyApi);
        return WaitAnyCore(tasks, millisecondsTimeout, cancellationToken);
    }

    /// <summary>Controlled <c>Task.WaitAny(Task[], CancellationToken)</c>; see <see cref="WaitAny(Task[], int)"/>.</summary>
    /// <param name="tasks">The candidate tasks.</param>
    /// <param name="cancellationToken">The token that cancels the wait.</param>
    /// <returns>The index of the completed task with the lowest index.</returns>
    public static int WaitAny(Task[] tasks, CancellationToken cancellationToken)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(WaitAnyApi);
        return WaitAnyCore(tasks, Timeout.Infinite, cancellationToken);
    }

    /// <summary>Controlled <c>Task.WaitAny(Task[], TimeSpan)</c>; see <see cref="WaitAny(Task[], int)"/>.</summary>
    /// <param name="tasks">The candidate tasks.</param>
    /// <param name="timeout">The timeout, or <see cref="Timeout.InfiniteTimeSpan"/> to wait indefinitely.</param>
    /// <returns>The index of the completed task with the lowest index, or -1 if none completed within the timeout.</returns>
    public static int WaitAny(Task[] tasks, TimeSpan timeout)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(WaitAnyApi);
        return WaitAnyCore(tasks, ToMilliseconds(timeout), CancellationToken.None);
    }

    // Each wait checks completion before it calls a Drain* method, which builds the predicate's closure, so a wait on
    // completed tasks allocates nothing.
    private static bool WaitCore(Task task, int millisecondsTimeout, CancellationToken cancellationToken)
    {
        // Task.Wait is an instance method: a null receiver fails the call itself, with the runtime's
        // NullReferenceException, before any argument is validated.
        _ = task.IsCompleted;
        ArgumentOutOfRangeException.ThrowIfLessThan(millisecondsTimeout, Timeout.Infinite, nameof(millisecondsTimeout));
        if (!task.IsCompleted && !DrainUntilCompleted(task, millisecondsTimeout, cancellationToken))
        {
            // As in the BCL, a zero timeout returns without observing the token.
            if (millisecondsTimeout != 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            return false;
        }

        RaceSynchronization.Wait(task);
        // The coordinator has completed the wait in virtual time; this zero-timeout BCL call never blocks and supplies the exact BCL result and exception semantics.
        return task.Wait(0, cancellationToken);
    }

    /// <summary>
    /// Pumps the coordinator until every task completes, the token is cancelled, or the timeout elapses in virtual
    /// time, then returns what <c>Task.WaitAll(Task[], int, CancellationToken)</c> returns or throws.
    /// </summary>
    /// <param name="tasks">The tasks to wait for.</param>
    /// <param name="millisecondsTimeout">The timeout in milliseconds, or -1 to wait indefinitely.</param>
    /// <param name="apiName">The API reported if the wait cannot make progress.</param>
    /// <param name="cancellationToken">The token that cancels the wait.</param>
    /// <returns><see langword="true"/> if every task completed within the timeout.</returns>
    internal static bool WaitAllCore(Task[] tasks, int millisecondsTimeout, string apiName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        ArgumentOutOfRangeException.ThrowIfLessThan(millisecondsTimeout, Timeout.Infinite, nameof(millisecondsTimeout));
        cancellationToken.ThrowIfCancellationRequested();
        ValidateNoNullTasks(tasks);
        int completedPrefix = CompletedPrefix(tasks, 0);
        if (completedPrefix < tasks.Length
            && !DrainUntilAllCompleted(tasks, completedPrefix, millisecondsTimeout, apiName, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            return false;
        }

        foreach (Task task in tasks)
        {
            RaceSynchronization.Wait(task);
        }

        // The coordinator has completed the wait in virtual time; this zero-timeout BCL call never blocks and supplies the exact BCL result and exception semantics.
        return Task.WaitAll(tasks, 0, cancellationToken);
    }

    private static int WaitAnyCore(Task[] tasks, int millisecondsTimeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        ArgumentOutOfRangeException.ThrowIfLessThan(millisecondsTimeout, Timeout.Infinite, nameof(millisecondsTimeout));
        cancellationToken.ThrowIfCancellationRequested();
        ValidateNoNullTasks(tasks);
        if (tasks.Length == 0)
        {
            return -1;
        }

        if (!AnyCompleted(tasks) && !DrainUntilAnyCompleted(tasks, millisecondsTimeout, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            return -1;
        }

        // The coordinator has completed the wait in virtual time; this zero-timeout BCL call never blocks and supplies the exact BCL result and exception semantics.
        int winner = Task.WaitAny(tasks, 0, CancellationToken.None);
        if (winner >= 0)
        {
            RaceSynchronization.Wait(tasks[winner]);
        }

        return winner;
    }

    private static bool DrainUntilCompleted(Task task, int millisecondsTimeout, CancellationToken cancellationToken) =>
        DrainUntilOrTimeout(() => task.IsCompleted, millisecondsTimeout, WaitApi, cancellationToken);

    // A task never leaves the completed state, so each check resumes the scan where the previous one stopped.
    private static bool DrainUntilAllCompleted(
        Task[] tasks,
        int completedPrefix,
        int millisecondsTimeout,
        string apiName,
        CancellationToken cancellationToken) =>
        DrainUntilOrTimeout(
            () => (completedPrefix = CompletedPrefix(tasks, completedPrefix)) == tasks.Length,
            millisecondsTimeout,
            apiName,
            cancellationToken);

    private static bool DrainUntilAnyCompleted(Task[] tasks, int millisecondsTimeout, CancellationToken cancellationToken) =>
        DrainUntilOrTimeout(() => AnyCompleted(tasks), millisecondsTimeout, WaitAnyApi, cancellationToken);

    // The length of the longest prefix of `tasks` whose tasks have completed, given that the first `start` have.
    private static int CompletedPrefix(Task[] tasks, int start)
    {
        while (start < tasks.Length && tasks[start].IsCompleted)
        {
            start++;
        }

        return start;
    }

    // Pumps the coordinator until `completed` holds, the token is cancelled, or the timeout elapses in virtual time.
    private static bool DrainUntilOrTimeout(
        Func<bool> completed,
        int millisecondsTimeout,
        string apiName,
        CancellationToken cancellationToken)
    {
        if (completed())
        {
            return true;
        }

        if (millisecondsTimeout == 0)
        {
            return false;
        }

        ISimulationTimer? deadline = millisecondsTimeout == Timeout.Infinite
            ? null
            : SimulationTaskRuntime.RegisterTimeout(TimeSpan.FromMilliseconds(millisecondsTimeout), onElapsed: null, apiName);
        try
        {
            SimulationTaskRuntime.DrainUntil(
                () => completed() || cancellationToken.IsCancellationRequested || deadline?.IsElapsed == true,
                apiName,
                CancellationToken.None);
        }
        finally
        {
            deadline?.Cancel();
        }

        return completed();
    }

    // The BCL's conversion of a TimeSpan timeout to milliseconds.
    private static int ToMilliseconds(TimeSpan timeout)
    {
        long totalMilliseconds = (long)timeout.TotalMilliseconds;
        if (totalMilliseconds is < -1 or > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        return (int)totalMilliseconds;
    }
}
