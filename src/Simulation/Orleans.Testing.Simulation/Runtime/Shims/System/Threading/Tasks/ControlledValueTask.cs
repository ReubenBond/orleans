using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;
using Orleans.Testing.Simulation.Runtime.Racing;
using Orleans.Testing.Simulation.Runtime.Shims;

namespace Orleans.Testing.Simulation.Shims.System.Threading.Tasks;

/// <summary>
/// <para>
/// Controlled <see cref="ValueTask.AsTask"/>, <see cref="ValueTask{TResult}.AsTask"/>, <see cref="ValueTask.Preserve"/>,
/// and <see cref="ValueTask{TResult}.Preserve"/>. For a value task backed by a pending
/// <see cref="IValueTaskSource"/>, the BCL registers a completion callback with the source through
/// <see cref="IValueTaskSource.OnCompleted"/>, and the source invokes it however it was configured: a source which runs
/// continuations asynchronously (such as <see cref="ManualResetValueTaskSourceCore{TResult}"/> with
/// <see cref="ManualResetValueTaskSourceCore{TResult}.RunContinuationsAsynchronously"/>, or a channel's pending read)
/// queues it to the real thread pool, so the returned task completes outside the coordinator.
/// </para>
/// <para>
/// These shims register nothing with the source. The coordinator polls the source's status through
/// <see cref="ValueTask.IsCompleted"/> (which calls <see cref="IValueTaskSource.GetStatus"/>), as the controlled
/// value-task awaiters do, and a controlled operation then completes the returned task. It consumes the source exactly
/// once, through the BCL's own conversion of a completed source (one <see cref="IValueTaskSource.GetStatus"/> and one
/// <see cref="IValueTaskSource.GetResult"/>), so the returned task's outcome, including the cancellation exception, is
/// the BCL's. A value task which wraps no source, wraps a task, or wraps a source that has already completed is
/// converted by the BCL itself, synchronously and without registering anything, so the result is the BCL's own
/// (including the wrapped task itself).
/// </para>
/// </summary>
public static class ControlledValueTask
{
    private const string AsTaskApi = "System.Threading.Tasks.ValueTask.AsTask";
    private const string PreserveApi = "System.Threading.Tasks.ValueTask.Preserve";
    private const string GenericAsTaskApi = "System.Threading.Tasks.ValueTask`1.AsTask";
    private const string GenericPreserveApi = "System.Threading.Tasks.ValueTask`1.Preserve";
    private const string GenericResultApi = "System.Threading.Tasks.ValueTask`1.get_Result";

    /// <summary>Controlled <c>ValueTask.AsTask()</c>.</summary>
    /// <param name="valueTask">The value task to convert; it is consumed.</param>
    /// <returns>A task representing the value task.</returns>
    public static Task AsTask(in ValueTask valueTask)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(AsTaskApi);
        return AsTaskCore(valueTask, AsTaskApi);
    }

    /// <summary>Controlled <c>ValueTask.Preserve()</c>.</summary>
    /// <param name="valueTask">The value task to preserve; it is consumed.</param>
    /// <returns>A value task which may be awaited any number of times.</returns>
    public static ValueTask Preserve(in ValueTask valueTask)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(PreserveApi);
        ValueTask copy = valueTask;
        return Fields.Object(ref copy) is null ? copy : new ValueTask(AsTaskCore(copy, PreserveApi));
    }

    /// <summary>Controlled <c>ValueTask&lt;TResult&gt;.AsTask()</c>.</summary>
    /// <typeparam name="TResult">The result type.</typeparam>
    /// <param name="valueTask">The value task to convert; it is consumed.</param>
    /// <returns>A task representing the value task.</returns>
    public static Task<TResult> AsTask<TResult>(in ValueTask<TResult> valueTask)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(GenericAsTaskApi);
        return AsTaskCore(valueTask, GenericAsTaskApi);
    }

    /// <summary>Controlled <c>ValueTask&lt;TResult&gt;.Preserve()</c>.</summary>
    /// <typeparam name="TResult">The result type.</typeparam>
    /// <param name="valueTask">The value task to preserve; it is consumed.</param>
    /// <returns>A value task which may be awaited any number of times.</returns>
    public static ValueTask<TResult> Preserve<TResult>(in ValueTask<TResult> valueTask)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(GenericPreserveApi);
        ValueTask<TResult> copy = valueTask;
        return Fields<TResult>.Object(ref copy) is null ? copy : new ValueTask<TResult>(AsTaskCore(copy, GenericPreserveApi));
    }

    /// <summary>
    /// Controlled <c>ValueTask&lt;TResult&gt;.Result</c>: pumps the coordinator until the value task completes, as a
    /// controlled synchronous wait does, instead of blocking the simulation's logical thread, then returns the BCL's
    /// result or throws its exception.
    /// </summary>
    /// <typeparam name="TResult">The result type.</typeparam>
    /// <param name="valueTask">The value task to read; it is consumed.</param>
    /// <returns>The value task's result.</returns>
    public static TResult Result<TResult>(in ValueTask<TResult> valueTask)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(GenericResultApi);
        if (!valueTask.IsCompleted)
        {
            SimulationTaskRuntime.DrainUntilCompleted(valueTask, GenericResultApi);
        }

        WaitCompletion(valueTask);
        return valueTask.Result;
    }

    private static Task AsTaskCore(ValueTask valueTask, string apiName)
    {
        if (Fields.Object(ref valueTask) is not IValueTaskSource || valueTask.IsCompleted)
        {
            return valueTask.AsTask();
        }

        var promise = new TaskCompletionSource();
        SimulationTaskRuntime.ScheduleWhenReady(
            () => valueTask.IsCompleted,
            () =>
            {
                SimulationCompletion.TrySetFromTask(promise, valueTask.AsTask(), AsTaskApi);
                RaceSynchronization.Signal(promise.Task);
            },
            apiName);
        return promise.Task;
    }

    private static Task<TResult> AsTaskCore<TResult>(ValueTask<TResult> valueTask, string apiName)
    {
        if (Fields<TResult>.Object(ref valueTask) is not IValueTaskSource<TResult> || valueTask.IsCompleted)
        {
            return valueTask.AsTask();
        }

        var promise = new TaskCompletionSource<TResult>();
        SimulationTaskRuntime.ScheduleWhenReady(
            () => valueTask.IsCompleted,
            () =>
            {
                SimulationCompletion.TrySetFromTask(promise, valueTask.AsTask(), GenericAsTaskApi);
                RaceSynchronization.Signal(promise.Task);
            },
            apiName);
        return promise.Task;
    }

    /// <summary>
    /// Publishes the happens-before edge from a completed value task to its awaiters, keyed on the task or source it
    /// wraps. A value task which completed synchronously wraps nothing and needs no edge: its awaiter runs after it in
    /// the same operation.
    /// </summary>
    /// <param name="valueTask">The completed value task.</param>
    internal static void SignalCompletion(ValueTask valueTask)
    {
        if (Fields.Object(ref valueTask) is { } completion)
        {
            RaceSynchronization.Signal(completion);
        }
    }

    /// <inheritdoc cref="SignalCompletion(ValueTask)"/>
    /// <typeparam name="TResult">The result type.</typeparam>
    internal static void SignalCompletion<TResult>(ValueTask<TResult> valueTask)
    {
        if (Fields<TResult>.Object(ref valueTask) is { } completion)
        {
            RaceSynchronization.Signal(completion);
        }
    }

    /// <summary>Consumes the happens-before edge <see cref="SignalCompletion(ValueTask)"/> published.</summary>
    /// <param name="valueTask">The completed value task being awaited.</param>
    internal static void WaitCompletion(ValueTask valueTask)
    {
        if (Fields.Object(ref valueTask) is { } completion)
        {
            RaceSynchronization.Wait(completion);
        }
    }

    /// <inheritdoc cref="WaitCompletion(ValueTask)"/>
    /// <typeparam name="TResult">The result type.</typeparam>
    internal static void WaitCompletion<TResult>(ValueTask<TResult> valueTask)
    {
        if (Fields<TResult>.Object(ref valueTask) is { } completion)
        {
            RaceSynchronization.Wait(completion);
        }
    }

    /// <summary>
    /// Reads the object a <see cref="ValueTask"/> wraps: <see langword="null"/> for a synchronously completed value
    /// task, a <see cref="Task"/>, or an <see cref="IValueTaskSource"/>. The BCL distinguishes these the same way, and
    /// exposes no public accessor for the distinction.
    /// </summary>
    private static class Fields
    {
        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_obj")]
        public static extern ref object? Object(ref ValueTask valueTask);
    }

    /// <summary>Reads the object a <see cref="ValueTask{TResult}"/> wraps; see <see cref="Fields"/>.</summary>
    /// <typeparam name="TResult">The result type.</typeparam>
    private static class Fields<TResult>
    {
        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_obj")]
        public static extern ref object? Object(ref ValueTask<TResult> valueTask);
    }
}
