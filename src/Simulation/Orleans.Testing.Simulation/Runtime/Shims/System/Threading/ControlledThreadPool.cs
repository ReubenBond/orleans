using System.Threading;
using Orleans.Testing.Simulation.Runtime.Execution;
using Orleans.Testing.Simulation.Runtime.Shims;
using Orleans.Testing.Simulation.Runtime.Tasks;

namespace Orleans.Testing.Simulation.Shims.System.Threading;

/// <summary>
/// <para>
/// Static shims for the <see cref="ThreadPool"/> queueing surface. The rewriter redirects the supported
/// call sites here: <see cref="ThreadPool.QueueUserWorkItem(WaitCallback)"/> and its overloads queue the
/// callback as a fresh controlled operation on the simulation coordinator instead of dispatching it to a
/// physical thread-pool thread, so it runs deterministically on the single logical thread.
/// </para>
/// <para>
/// <b>ExecutionContext flow.</b> The BCL draws a deliberate distinction between the <em>safe</em>
/// <c>QueueUserWorkItem</c> family, which captures the caller's <see cref="ExecutionContext"/> and runs
/// the callback under it (so ambient <see cref="AsyncLocal{T}"/> values flow to the callback), and the
/// <em>unsafe</em> <c>UnsafeQueueUserWorkItem</c> family, which does not capture the caller's context.
/// The shims preserve that distinction: the safe variants snapshot the context at enqueue time with
/// <see cref="ExecutionContext.Capture"/> and run the callback via <see cref="ExecutionContext.Run"/>,
/// while the unsafe variants queue the callback directly, so it observes only the ambient context at run
/// time rather than the caller's captured snapshot.
/// </para>
/// <para>
/// <b>Registered waits.</b> The <c>RegisterWaitForSingleObject</c> family and its unsafe sibling
/// bind a <see cref="WaitOrTimerCallback"/> to a <see cref="WaitHandle"/>. Now that controlled wait handles
/// exist, each factory returns a <see cref="ControlledRegisteredWaitHandle"/> whose wait loop runs as a
/// controlled operation on the coordinator: it fires the callback with <c>timedOut: false</c> when the handle
/// is signalled and <c>timedOut: true</c> when the virtual-time deadline elapses, honours
/// <c>executeOnlyOnce</c> versus repeating registrations, and stops on <c>Unregister</c>. The safe variants
/// flow the caller's <see cref="ExecutionContext"/>; the unsafe variants do not. No physical thread-pool wait
/// thread is ever used.
/// </para>
/// <para>
/// This goes beyond Microsoft Coyote, which routes thread-pool work through its controlled
/// <c>Task</c>/<c>TaskFactory</c> types and does not intercept <c>ThreadPool.QueueUserWorkItem</c> or the
/// registered-wait surface directly. The native-I/O surface (<c>UnsafeQueueNativeOverlapped</c>) cannot be
/// modelled faithfully and is rejected precisely - see <see cref="SimulationApiException"/>.
/// </para>
/// </summary>
public static class ControlledThreadPool
{
    private const string QueueApi = "System.Threading.ThreadPool.QueueUserWorkItem";
    private const string UnsafeQueueApi = "System.Threading.ThreadPool.UnsafeQueueUserWorkItem";

    /// <summary>Controlled <see cref="ThreadPool.QueueUserWorkItem(WaitCallback)"/> (flows ExecutionContext).</summary>
    /// <param name="callBack">The work item to queue.</param>
    /// <returns><see langword="true"/> - the work item is always accepted.</returns>
    public static bool QueueUserWorkItem(WaitCallback callBack)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(QueueApi);
        ArgumentNullException.ThrowIfNull(callBack);
        return QueueUserWorkItem(callBack, state: null);
    }

    /// <summary>Controlled <see cref="ThreadPool.QueueUserWorkItem(WaitCallback, object)"/> (flows ExecutionContext).</summary>
    /// <param name="callBack">The work item to queue.</param>
    /// <param name="state">The object passed to the callback.</param>
    /// <returns><see langword="true"/> - the work item is always accepted.</returns>
    public static bool QueueUserWorkItem(WaitCallback callBack, object? state)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(QueueApi);
        ArgumentNullException.ThrowIfNull(callBack);
        SimulationTaskRuntime.QueueWork(
            () => callBack(state),
            QueueApi,
            flowExecutionContext: true);
        return true;
    }

    /// <summary>Controlled generic <see cref="ThreadPool.QueueUserWorkItem{TState}(Action{TState}, TState, bool)"/> (flows ExecutionContext).</summary>
    /// <typeparam name="TState">The state type.</typeparam>
    /// <param name="callBack">The work item to queue.</param>
    /// <param name="state">The strongly-typed state passed to the callback.</param>
    /// <param name="preferLocal">Ignored because the controlled scheduler has a single queue.</param>
    /// <returns><see langword="true"/> - the work item is always accepted.</returns>
    public static bool QueueUserWorkItem<TState>(Action<TState> callBack, TState state, bool preferLocal)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(QueueApi);
        ArgumentNullException.ThrowIfNull(callBack);
        SimulationTaskRuntime.QueueWork(
            () => callBack(state),
            QueueApi,
            flowExecutionContext: true);
        return true;
    }

    /// <summary>Controlled <see cref="ThreadPool.UnsafeQueueUserWorkItem(WaitCallback, object)"/> (does not flow ExecutionContext).</summary>
    /// <param name="callBack">The work item to queue.</param>
    /// <param name="state">The object passed to the callback.</param>
    /// <returns><see langword="true"/> - the work item is always accepted.</returns>
    public static bool UnsafeQueueUserWorkItem(WaitCallback callBack, object? state)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(UnsafeQueueApi);
        ArgumentNullException.ThrowIfNull(callBack);
        // Unsafe variants do not capture the caller's ExecutionContext, so the callback observes only the
        // ambient run-time context, not the caller's enqueue-time snapshot.
        SimulationTaskRuntime.QueueWork(
            () => callBack(state),
            UnsafeQueueApi,
            flowExecutionContext: false);
        return true;
    }

    /// <summary>Controlled <see cref="ThreadPool.UnsafeQueueUserWorkItem(IThreadPoolWorkItem, bool)"/> (does not flow ExecutionContext).</summary>
    /// <param name="callBack">The work item to queue.</param>
    /// <param name="preferLocal">Ignored because the controlled scheduler has a single queue.</param>
    /// <returns><see langword="true"/> - the work item is always accepted.</returns>
    public static bool UnsafeQueueUserWorkItem(IThreadPoolWorkItem callBack, bool preferLocal)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(UnsafeQueueApi);
        ArgumentNullException.ThrowIfNull(callBack);
        SimulationTaskRuntime.QueueWork(
            callBack.Execute,
            UnsafeQueueApi,
            flowExecutionContext: false);
        return true;
    }

    /// <summary>Controlled generic <see cref="ThreadPool.UnsafeQueueUserWorkItem{TState}(Action{TState}, TState, bool)"/> (does not flow ExecutionContext).</summary>
    /// <typeparam name="TState">The state type.</typeparam>
    /// <param name="callBack">The work item to queue.</param>
    /// <param name="state">The strongly-typed state passed to the callback.</param>
    /// <param name="preferLocal">Ignored because the controlled scheduler has a single queue.</param>
    /// <returns><see langword="true"/> - the work item is always accepted.</returns>
    public static bool UnsafeQueueUserWorkItem<TState>(Action<TState> callBack, TState state, bool preferLocal)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(UnsafeQueueApi);
        ArgumentNullException.ThrowIfNull(callBack);
        SimulationTaskRuntime.QueueWork(
            () => callBack(state),
            UnsafeQueueApi,
            flowExecutionContext: false);
        return true;
    }

    /// <summary>
    /// Rejection injected before an unsupported native-I/O thread-pool call site
    /// (<c>UnsafeQueueNativeOverlapped</c>), which cannot be modelled by the deterministic scheduler.
    /// </summary>
    /// <param name="apiName">The unsupported API, supplied by the rewriter.</param>
    public static void RejectNativeOverlapped(string apiName)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(apiName);
        throw new SimulationApiException(
            SimulationApiCategory.ThreadPool,
            apiName,
            "native overlapped I/O cannot be modelled by the deterministic scheduler; the controlled " +
            "thread pool has no OS I/O completion port.");
    }

    // ---- Registered waits: RegisterWaitForSingleObject / UnsafeRegisterWaitForSingleObject
    // bind a WaitOrTimerCallback to a controlled event. Each of the two families has four timeout overloads
    // (UInt32/Int32/Int64/TimeSpan). The safe family flows the caller's ExecutionContext; the unsafe family
    // does not. The registration runs as a controlled operation. ----

    /// <summary>Controlled <see cref="ThreadPool.RegisterWaitForSingleObject(WaitHandle, WaitOrTimerCallback, object, uint, bool)"/> (flows ExecutionContext).</summary>
    public static ControlledRegisteredWaitHandle RegisterWaitForSingleObject(WaitHandle waitObject, WaitOrTimerCallback callBack, object? state, uint millisecondsTimeOutInterval, bool executeOnlyOnce)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation("System.Threading.ThreadPool.RegisterWaitForSingleObject");
        ArgumentNullException.ThrowIfNull(waitObject);
        ArgumentNullException.ThrowIfNull(callBack);
        return RegisterControlled(waitObject, callBack, state, NormalizeTimeout(millisecondsTimeOutInterval), executeOnlyOnce, flow: true);
    }

    /// <summary>Controlled <see cref="ThreadPool.RegisterWaitForSingleObject(WaitHandle, WaitOrTimerCallback, object, int, bool)"/> (flows ExecutionContext).</summary>
    public static ControlledRegisteredWaitHandle RegisterWaitForSingleObject(WaitHandle waitObject, WaitOrTimerCallback callBack, object? state, int millisecondsTimeOutInterval, bool executeOnlyOnce)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation("System.Threading.ThreadPool.RegisterWaitForSingleObject");
        ArgumentNullException.ThrowIfNull(waitObject);
        ArgumentNullException.ThrowIfNull(callBack);
        return RegisterControlled(waitObject, callBack, state, NormalizeTimeout(millisecondsTimeOutInterval), executeOnlyOnce, flow: true);
    }

    /// <summary>Controlled <see cref="ThreadPool.RegisterWaitForSingleObject(WaitHandle, WaitOrTimerCallback, object, long, bool)"/> (flows ExecutionContext).</summary>
    public static ControlledRegisteredWaitHandle RegisterWaitForSingleObject(WaitHandle waitObject, WaitOrTimerCallback callBack, object? state, long millisecondsTimeOutInterval, bool executeOnlyOnce)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation("System.Threading.ThreadPool.RegisterWaitForSingleObject");
        ArgumentNullException.ThrowIfNull(waitObject);
        ArgumentNullException.ThrowIfNull(callBack);
        return RegisterControlled(waitObject, callBack, state, NormalizeTimeout(millisecondsTimeOutInterval), executeOnlyOnce, flow: true);
    }

    /// <summary>Controlled <see cref="ThreadPool.RegisterWaitForSingleObject(WaitHandle, WaitOrTimerCallback, object, TimeSpan, bool)"/> (flows ExecutionContext).</summary>
    public static ControlledRegisteredWaitHandle RegisterWaitForSingleObject(WaitHandle waitObject, WaitOrTimerCallback callBack, object? state, TimeSpan timeout, bool executeOnlyOnce)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation("System.Threading.ThreadPool.RegisterWaitForSingleObject");
        ArgumentNullException.ThrowIfNull(waitObject);
        ArgumentNullException.ThrowIfNull(callBack);
        return RegisterControlled(waitObject, callBack, state, NormalizeTimeout(timeout), executeOnlyOnce, flow: true);
    }

    /// <summary>Controlled <see cref="ThreadPool.UnsafeRegisterWaitForSingleObject(WaitHandle, WaitOrTimerCallback, object, uint, bool)"/> (does not flow ExecutionContext).</summary>
    public static ControlledRegisteredWaitHandle UnsafeRegisterWaitForSingleObject(WaitHandle waitObject, WaitOrTimerCallback callBack, object? state, uint millisecondsTimeOutInterval, bool executeOnlyOnce)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation("System.Threading.ThreadPool.UnsafeRegisterWaitForSingleObject");
        ArgumentNullException.ThrowIfNull(waitObject);
        ArgumentNullException.ThrowIfNull(callBack);
        return RegisterControlled(waitObject, callBack, state, NormalizeTimeout(millisecondsTimeOutInterval), executeOnlyOnce, flow: false);
    }

    /// <summary>Controlled <see cref="ThreadPool.UnsafeRegisterWaitForSingleObject(WaitHandle, WaitOrTimerCallback, object, int, bool)"/> (does not flow ExecutionContext).</summary>
    public static ControlledRegisteredWaitHandle UnsafeRegisterWaitForSingleObject(WaitHandle waitObject, WaitOrTimerCallback callBack, object? state, int millisecondsTimeOutInterval, bool executeOnlyOnce)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation("System.Threading.ThreadPool.UnsafeRegisterWaitForSingleObject");
        ArgumentNullException.ThrowIfNull(waitObject);
        ArgumentNullException.ThrowIfNull(callBack);
        return RegisterControlled(waitObject, callBack, state, NormalizeTimeout(millisecondsTimeOutInterval), executeOnlyOnce, flow: false);
    }

    /// <summary>Controlled <see cref="ThreadPool.UnsafeRegisterWaitForSingleObject(WaitHandle, WaitOrTimerCallback, object, long, bool)"/> (does not flow ExecutionContext).</summary>
    public static ControlledRegisteredWaitHandle UnsafeRegisterWaitForSingleObject(WaitHandle waitObject, WaitOrTimerCallback callBack, object? state, long millisecondsTimeOutInterval, bool executeOnlyOnce)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation("System.Threading.ThreadPool.UnsafeRegisterWaitForSingleObject");
        ArgumentNullException.ThrowIfNull(waitObject);
        ArgumentNullException.ThrowIfNull(callBack);
        return RegisterControlled(waitObject, callBack, state, NormalizeTimeout(millisecondsTimeOutInterval), executeOnlyOnce, flow: false);
    }

    /// <summary>Controlled <see cref="ThreadPool.UnsafeRegisterWaitForSingleObject(WaitHandle, WaitOrTimerCallback, object, TimeSpan, bool)"/> (does not flow ExecutionContext).</summary>
    public static ControlledRegisteredWaitHandle UnsafeRegisterWaitForSingleObject(WaitHandle waitObject, WaitOrTimerCallback callBack, object? state, TimeSpan timeout, bool executeOnlyOnce)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation("System.Threading.ThreadPool.UnsafeRegisterWaitForSingleObject");
        ArgumentNullException.ThrowIfNull(waitObject);
        ArgumentNullException.ThrowIfNull(callBack);
        return RegisterControlled(waitObject, callBack, state, NormalizeTimeout(timeout), executeOnlyOnce, flow: false);
    }

    private static ControlledRegisteredWaitHandle RegisterControlled(
        WaitHandle waitObject, WaitOrTimerCallback callBack, object? state, int timeoutMs, bool executeOnlyOnce, bool flow)
    {
        ExecutionContext? context = flow ? ExecutionContext.Capture() : null;
        return new ControlledRegisteredWaitHandle(waitObject, callBack, state, timeoutMs, executeOnlyOnce, context);
    }

    // A uint timeout of 0xFFFFFFFF is the BCL's "infinite" sentinel. Every other accepted value must fit
    // the controlled timer's Int32 millisecond range.
    private static int NormalizeTimeout(uint millisecondsTimeOutInterval)
    {
        if (millisecondsTimeOutInterval == uint.MaxValue)
        {
            return Timeout.Infinite;
        }

        if (millisecondsTimeOutInterval > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(millisecondsTimeOutInterval),
                millisecondsTimeOutInterval,
                "The timeout must be UInt32.MaxValue (infinite) or fit in Int32 milliseconds.");
        }

        return (int)millisecondsTimeOutInterval;
    }

    private static int NormalizeTimeout(int millisecondsTimeOutInterval)
    {
        if (millisecondsTimeOutInterval < Timeout.Infinite)
        {
            throw new ArgumentOutOfRangeException(nameof(millisecondsTimeOutInterval), millisecondsTimeOutInterval, "The timeout must be -1 (infinite) or a non-negative value.");
        }

        return millisecondsTimeOutInterval;
    }

    private static int NormalizeTimeout(long millisecondsTimeOutInterval)
    {
        if (millisecondsTimeOutInterval < Timeout.Infinite || millisecondsTimeOutInterval > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(millisecondsTimeOutInterval), millisecondsTimeOutInterval, "The timeout must be between -1 and Int32.MaxValue milliseconds.");
        }

        return (int)millisecondsTimeOutInterval;
    }

    private static int NormalizeTimeout(TimeSpan timeout)
    {
        long total = (long)timeout.TotalMilliseconds;
        if (total < Timeout.Infinite || total > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "The timeout must be between -1 and Int32.MaxValue milliseconds.");
        }

        return (int)total;
    }

}
