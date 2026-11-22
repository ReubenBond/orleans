using System.Runtime.Versioning;

namespace Orleans.Durable.Storage.Architecture.Tests.SimulationFixtures.ClockworkCompatibility;

// Planted fixtures for ClockworkCompatibilityTests: one compatible type which uses the allowed forms, and one type per
// Clockwork compatibility rule documented in docs/spec/verification.md §7.1.

/// <summary>Uses the allowed forms: awaiting a ValueTask, TaskScheduler.Default, and unnamed synchronization primitives.</summary>
public static class FixtureCompatible
{
    public static async Task<int> AwaitValueTasksAsync(ValueTask work, ValueTask<int> value)
    {
        await work;
        await work.ConfigureAwait(false);
        var first = await value;
        var second = await value.ConfigureAwait(false);
        return first + second;
    }

    public static Task ContinueOnDefaultScheduler(Task task) =>
        task.ContinueWith(static _ => { }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);

    public static void UseUnnamedPrimitives()
    {
        using var mutex = new Mutex();
        using var semaphore = new Semaphore(0, 1);
        using var waitHandle = new EventWaitHandle(false, EventResetMode.ManualReset);
        _ = (mutex, semaphore, waitHandle);
    }
}

/// <summary>Reads <see cref="ValueTask{TResult}.Result"/> synchronously, both directly and inside an async method.</summary>
public static class FixtureValueTaskResult
{
    public static int ReadSync(ValueTask<int> value) => value.Result;

    public static async Task<int> ReadInsideAsync(ValueTask<int> value)
    {
        await Task.Yield();
        return value.Result;
    }
}

/// <summary>Blocks on <see cref="ValueTask"/> and <see cref="ValueTask{TResult}"/> awaiters outside an async state machine.</summary>
public static class FixtureValueTaskGetResult
{
    public static void BlockVoid(ValueTask work) => work.GetAwaiter().GetResult();

    public static int BlockGeneric(ValueTask<int> value) => value.GetAwaiter().GetResult();

    public static void BlockConfiguredVoid(ValueTask work) => work.ConfigureAwait(false).GetAwaiter().GetResult();

    public static int BlockConfiguredGeneric(ValueTask<int> value) => value.ConfigureAwait(false).GetAwaiter().GetResult();
}

/// <summary>Derives a custom <see cref="TaskScheduler"/>.</summary>
public sealed class FixtureCustomTaskScheduler : TaskScheduler
{
    protected override IEnumerable<Task>? GetScheduledTasks() => null;

    protected override void QueueTask(Task task) => _ = TryExecuteTask(task);

    protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => false;
}

/// <summary>Creates a <see cref="ConcurrentExclusiveSchedulerPair"/>.</summary>
public static class FixtureSchedulerPair
{
    public static TaskScheduler Create() => new ConcurrentExclusiveSchedulerPair().ExclusiveScheduler;
}

/// <summary>Uses a non-default <see cref="TaskScheduler"/> member and constructs a <see cref="TaskFactory"/> with a scheduler.</summary>
public static class FixtureNonDefaultScheduler
{
    public static TaskScheduler Current() => TaskScheduler.Current;

    public static TaskScheduler FromContext() => TaskScheduler.FromCurrentSynchronizationContext();

    public static TaskFactory FactoryWithScheduler() => new(TaskScheduler.Default);
}

/// <summary>Uses named cross-process <see cref="Mutex"/>, <see cref="Semaphore"/>, and <see cref="EventWaitHandle"/> APIs.</summary>
public static class FixtureNamedPrimitives
{
    public static void NamedConstructors()
    {
        using var mutex = new Mutex(false, "fixture-mutex");
        using var semaphore = new Semaphore(0, 1, "fixture-semaphore");
        using var waitHandle = new EventWaitHandle(false, EventResetMode.ManualReset, "fixture-event");
        _ = (mutex, semaphore, waitHandle);
    }

    [SupportedOSPlatform("windows")]
    public static void OpenExisting()
    {
        using var mutex = Mutex.OpenExisting("fixture-mutex");
        _ = Semaphore.TryOpenExisting("fixture-semaphore", out var semaphore);
        semaphore?.Dispose();
        using var waitHandle = EventWaitHandle.OpenExisting("fixture-event");
        _ = (mutex, waitHandle);
    }
}

/// <summary>Creates tasks with <c>TaskFactory.FromAsync</c> and <c>TaskFactory&lt;TResult&gt;.FromAsync</c>.</summary>
public static class FixtureFromAsync
{
    public static Task NonGeneric(Func<AsyncCallback, object?, IAsyncResult> begin, Action<IAsyncResult> end) =>
        Task.Factory.FromAsync(begin, end, state: null);

    public static Task<int> Generic(Func<AsyncCallback, object?, IAsyncResult> begin, Func<IAsyncResult, int> end) =>
        Task<int>.Factory.FromAsync(begin, end, state: null);
}

/// <summary>Calls APIs which Clockwork rejects with <c>SimulationApiException</c>.</summary>
public static class FixtureRejectedMembers
{
    public static object? RawHandle(WaitHandle handle) => handle.SafeWaitHandle;

    public static void Interrupt(Thread thread) => thread.Interrupt();

    public static long ContentionCount() => Monitor.LockContentionCount;
}
