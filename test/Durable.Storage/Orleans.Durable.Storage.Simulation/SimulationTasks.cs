namespace Orleans.Durable.Storage.Simulation;

/// <summary>
/// The task combinators a simulation test's own code awaits, run under Clockwork's rewrite (test/Durable.Storage/docs/spec/verification.md
/// §7.1). The build rewrites the kit, not the test assembly, so a combinator the test assembly called would be the BCL's:
/// its promise runs on the real thread pool whenever a task it waits for runs its continuations asynchronously, as a
/// task completion source created so does, and it then posts its waiter's continuation to the simulation from outside
/// it, in no recorded order and possibly after the driver found nothing runnable. Each combinator here is Clockwork's
/// controlled one, so it completes inside the simulation. test/Durable.Storage/eng/BannedSymbols.Simulation.txt bans the BCL's in simulation
/// test projects.
/// </summary>
public static class SimulationTasks
{
    /// <summary>Completes once every task in <paramref name="tasks"/> has completed, as <see cref="Task.WhenAll(ReadOnlySpan{Task})"/>.</summary>
    /// <param name="tasks">The tasks.</param>
    /// <returns>A task which completes once every task has.</returns>
    public static Task WhenAll(params ReadOnlySpan<Task> tasks) => Task.WhenAll(tasks);

    /// <summary>Completes once every task in <paramref name="tasks"/> has completed, as <see cref="Task.WhenAll(IEnumerable{Task})"/>.</summary>
    /// <param name="tasks">The tasks.</param>
    /// <returns>A task which completes once every task has.</returns>
    public static Task WhenAll(IEnumerable<Task> tasks) => Task.WhenAll(tasks);

    /// <summary>Completes with every result once every task in <paramref name="tasks"/> has completed, as <see cref="Task.WhenAll{TResult}(IEnumerable{Task{TResult}})"/>.</summary>
    /// <typeparam name="TResult">The tasks' result.</typeparam>
    /// <param name="tasks">The tasks.</param>
    /// <returns>A task whose result is each task's, in order.</returns>
    public static Task<TResult[]> WhenAll<TResult>(IEnumerable<Task<TResult>> tasks) => Task.WhenAll(tasks);

    /// <summary>Completes with the first of <paramref name="first"/> and <paramref name="second"/> to complete, as <see cref="Task.WhenAny(Task, Task)"/>.</summary>
    /// <param name="first">The first task.</param>
    /// <param name="second">The second task.</param>
    /// <returns>A task whose result is the task which completed first.</returns>
    public static Task<Task> WhenAny(Task first, Task second) => Task.WhenAny(first, second);

    /// <summary>Completes with the first task in <paramref name="tasks"/> to complete, as <see cref="Task.WhenAny(IEnumerable{Task})"/>.</summary>
    /// <param name="tasks">The tasks.</param>
    /// <returns>A task whose result is the task which completed first.</returns>
    public static Task<Task> WhenAny(IEnumerable<Task> tasks) => Task.WhenAny(tasks);

    /// <summary>Completes with the first task in <paramref name="tasks"/> to complete, as <see cref="Task.WhenAny{TResult}(IEnumerable{Task{TResult}})"/>.</summary>
    /// <typeparam name="TResult">The tasks' result.</typeparam>
    /// <param name="tasks">The tasks.</param>
    /// <returns>A task whose result is the task which completed first.</returns>
    public static Task<Task<TResult>> WhenAny<TResult>(IEnumerable<Task<TResult>> tasks) => Task.WhenAny(tasks);

    /// <summary>Yields to the scheduler, as <see cref="Task.Yield"/>: the rest of the caller runs as an operation of its own.</summary>
    /// <returns>A task which completes once the scheduler has run the caller's continuation's turn.</returns>
    public static async Task Yield() => await Task.Yield();

    /// <summary>Completes once <paramref name="delay"/> passes on <paramref name="timeProvider"/>, as <see cref="Task.Delay(TimeSpan, TimeProvider, CancellationToken)"/>.</summary>
    /// <param name="delay">How long to wait.</param>
    /// <param name="timeProvider">The clock which times the wait.</param>
    /// <param name="cancellationToken">Ends the wait early, cancelled.</param>
    /// <returns>A task which completes once the delay passes.</returns>
    public static Task Delay(TimeSpan delay, TimeProvider timeProvider, CancellationToken cancellationToken) =>
        Task.Delay(delay, timeProvider, cancellationToken);

    /// <summary>
    /// Waits for <paramref name="task"/> until <paramref name="timeout"/> passes on <paramref name="timeProvider"/>, as
    /// <see cref="Task.WaitAsync(TimeSpan, TimeProvider)"/> does, and returns whether the task completed first.
    /// </summary>
    /// <param name="task">The task.</param>
    /// <param name="timeout">How long to wait.</param>
    /// <param name="timeProvider">The clock which times the wait.</param>
    /// <returns><see langword="true"/> when the task completed before the timeout.</returns>
    public static async Task<bool> CompletesWithinAsync(Task task, TimeSpan timeout, TimeProvider timeProvider)
    {
        await task.WaitAsync(timeout, timeProvider).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        return task.IsCompleted;
    }
}
