using Orleans.Testing.Simulation.Runtime.Execution;
using Orleans.Testing.Simulation.Runtime.Scheduling;
using Orleans.Testing.Simulation.Runtime.Shims;
using Orleans.Testing.Simulation.Runtime.Racing;
using Orleans.Testing.Simulation.Runtime.Threading;

namespace Orleans.Testing.Simulation.Runtime.Tasks;

/// <summary>Bridges controlled async/task rewrite targets to the active simulation coordinator.</summary>
public static class SimulationTaskRuntime
{
    /// <summary>
    /// Gets a value indicating whether a simulation is active on the calling logical thread.
    /// </summary>
    public static bool IsSimulationActive => SimulationExecutionContext.IsActive;

    /// <summary>Resolves the scheduler for the active ambient simulation runtime.</summary>
    public static (SimulationScheduler Scheduler, SimulationNodeIdentity? Node) RequireScheduler(
        string apiName)
    {
        var snapshot = SimulationRuntimeDispatch.RequireActiveSimulation(apiName);
        return (snapshot.Runtime.Scheduler, snapshot.Node);
    }

    /// <summary>
    /// Registers <paramref name="continuation"/> so it runs on the simulation's logical thread once
    /// <paramref name="antecedent"/> completes, routed through the ambient coordinator's readiness queue.
    /// This is the core of "await pauses the logical operation and completion makes it runnable": the
    /// continuation is never invoked inline, never captured onto a captured
    /// <see cref="System.Threading.SynchronizationContext"/>, and never handed to the physical thread
    /// pool - it always lands on the coordinator, which is exactly why <c>ConfigureAwait(false)</c> still
    /// stays controlled.
    /// </summary>
    /// <param name="antecedent">The task whose completion makes the continuation runnable.</param>
    /// <param name="continuation">The continuation to schedule.</param>
    /// <param name="apiName">The controlled API, for diagnostics.</param>
    /// <param name="flowExecutionContext">Whether to capture the caller's user execution context.</param>
    public static void ScheduleContinuation(
        System.Threading.Tasks.Task antecedent,
        Action continuation,
        string apiName,
        bool flowExecutionContext)
    {
        var (scheduler, node) = RequireScheduler(apiName);
        ArgumentNullException.ThrowIfNull(antecedent);
        ArgumentNullException.ThrowIfNull(continuation);
        var snapshot = SimulationRuntimeDispatch.RequireActiveSimulation(apiName);
        ExecutionContext? context = flowExecutionContext ? ExecutionContext.Capture() : null;
        var predecessor = new object();
        RaceSynchronization.Signal(predecessor);
        _ = scheduler.ScheduleWhenReady(
            node,
            () => antecedent.IsCompleted,
            () =>
            {
                RaceSynchronization.Wait(predecessor);
                RaceSynchronization.Wait(antecedent);
                RunScheduledWork(snapshot, context, continuation);
            });
    }

    internal static ISimulationWorkRegistration ScheduleCancelableContinuation(
        System.Threading.Tasks.Task antecedent,
        Action continuation,
        string apiName)
    {
        var (scheduler, node) = RequireScheduler(apiName);
        ArgumentNullException.ThrowIfNull(antecedent);
        ArgumentNullException.ThrowIfNull(continuation);
        SimulationExecutionSnapshot snapshot = SimulationRuntimeDispatch.RequireActiveSimulation(apiName);
        var predecessor = new object();
        RaceSynchronization.Signal(predecessor);
        return scheduler.ScheduleWhenReady(
            node,
            () => antecedent.IsCompleted,
            () =>
            {
                RaceSynchronization.Wait(predecessor);
                RaceSynchronization.Wait(antecedent);
                RunScheduledWork(snapshot, null, continuation);
            });
    }

    /// <summary>
    /// Schedules <paramref name="work"/> as a controlled operation once <paramref name="isReady"/> holds; the work runs
    /// on the logical thread without the caller's user execution context.
    /// </summary>
    /// <remarks>
    /// The coordinator calls the predicate on the thread driving the scheduler, once per scheduling step until it first
    /// returns <see langword="true"/>, and never again after that. It may update its own observation state (which
    /// antecedents it has observed complete, say), since antecedents only ever become complete, but must not otherwise
    /// change simulation state. It runs every step, so its cost should be O(pending antecedents).
    /// </remarks>
    /// <param name="isReady">The readiness predicate.</param>
    /// <param name="work">The work to run once ready.</param>
    /// <param name="apiName">The controlled API, for diagnostics.</param>
    internal static void ScheduleWhenReady(Func<bool> isReady, Action work, string apiName) =>
        ScheduleWhenReady(SimulationRuntimeDispatch.RequireActiveSimulation(apiName), isReady, work, apiName);

    /// <summary>
    /// Schedules <paramref name="work"/> as a controlled operation under <paramref name="snapshot"/>, and so on its
    /// node, once <paramref name="isReady"/> holds; see <see cref="ScheduleWhenReady(Func{bool}, Action, string)"/>.
    /// </summary>
    /// <param name="snapshot">The snapshot the work runs under.</param>
    /// <param name="isReady">The readiness predicate.</param>
    /// <param name="work">The work to run once ready.</param>
    /// <param name="apiName">The controlled API, for diagnostics.</param>
    internal static void ScheduleWhenReady(
        SimulationExecutionSnapshot snapshot,
        Func<bool> isReady,
        Action work,
        string apiName)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(isReady);
        ArgumentNullException.ThrowIfNull(work);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiName);
        var predecessor = new object();
        RaceSynchronization.Signal(predecessor);
        _ = snapshot.Runtime.Scheduler.ScheduleWhenReady(
            snapshot.Node,
            isReady,
            () =>
            {
                RaceSynchronization.Wait(predecessor);
                RunScheduledWork(snapshot, null, work);
            });
    }

    /// <summary>
    /// Schedules <paramref name="work"/> as a new, immediately-runnable unit of controlled work on the
    /// ambient coordinator's ready queue. This is the shared primitive behind every controlled concurrency surface that
    /// spawns a fresh logical operation - <c>Task.Run</c>, <c>TaskFactory.StartNew</c>,
    /// <c>ThreadPool.QueueUserWorkItem</c>, <c>Thread.Start</c>, and the branches of <c>Parallel</c> - so
    /// the body runs on the simulation's single logical thread interleaved with all other controlled work
    /// instead of on an uncontrolled physical thread-pool thread. The body runs to its next explicit yield
    /// point (an <c>await</c>, <c>Task.Yield</c>, <c>Thread.Yield</c>/<c>Sleep</c>, or completion) as one
    /// cooperative scheduling unit.
    /// </summary>
    /// <param name="work">The work item to enqueue. Runs exactly once.</param>
    /// <param name="apiName">The controlled API queuing the work, for diagnostics.</param>
    /// <param name="flowExecutionContext">Whether to capture the caller's user execution context.</param>
    public static void QueueWork(Action work, string apiName, bool flowExecutionContext = true)
    {
        var (scheduler, node) = RequireScheduler(apiName);
        ArgumentNullException.ThrowIfNull(work);
        var snapshot = SimulationRuntimeDispatch.RequireActiveSimulation(apiName);
        ExecutionContext? context = flowExecutionContext ? ExecutionContext.Capture() : null;
        scheduler.Schedule(
            node,
            () => Orleans.Testing.Simulation.Runtime.Threading.SimulationSynchronizationFlow.RunAsNewStrand(
                () => RunScheduledWork(snapshot, context, work)));
    }

    internal static void RunWithCapturedExecutionContext(ExecutionContext? context, Action work)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (context is null)
        {
            work();
            return;
        }

        SimulationExecutionSnapshot snapshot = SimulationExecutionContext.Current
            ?? throw new InvalidOperationException("A controlled execution context requires an active simulation.");
        long strandId = SimulationSynchronizationFlow.CurrentId;
        ExecutionContext.Run(
            context,
            static state =>
            {
                var flowed = ((SimulationExecutionSnapshot Snapshot, long StrandId, Action Work))state!;
                RunInSimulationSnapshot(flowed.Snapshot, flowed.StrandId, flowed.Work);
            },
            (snapshot, strandId, work));
    }

    internal static void RunWithCapturedExecutionContextAsNewStrand(ExecutionContext? context, Action work) =>
        SimulationSynchronizationFlow.RunAsNewStrand(() => RunWithCapturedExecutionContext(context, work));

    internal static void RunWithoutUserExecutionContext(SimulationExecutionSnapshot snapshot, Action work)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(work);

        long strandId = SimulationSynchronizationFlow.CurrentId;
        ExecutionContext.Run(
            (ExecutionContext)Activator.CreateInstance(typeof(ExecutionContext), nonPublic: true)!,
            static state =>
            {
                var invocation = ((SimulationExecutionSnapshot Snapshot, long StrandId, Action Work))state!;
                RunInSimulationSnapshot(invocation.Snapshot, invocation.StrandId, invocation.Work);
            },
            (snapshot, strandId, work));
    }

    /// <summary>
    /// Schedules <paramref name="continuation"/> to run as immediately-runnable controlled work (the
    /// backing for <c>Task.Yield</c>).
    /// </summary>
    /// <param name="continuation">The continuation to schedule.</param>
    /// <param name="apiName">The controlled API, for diagnostics.</param>
    /// <param name="flowExecutionContext">Whether to capture the caller's user execution context.</param>
    public static void ScheduleYield(Action continuation, string apiName, bool flowExecutionContext)
    {
        var (scheduler, node) = RequireScheduler(apiName);
        ArgumentNullException.ThrowIfNull(continuation);
        var snapshot = SimulationRuntimeDispatch.RequireActiveSimulation(apiName);
        ExecutionContext? context = flowExecutionContext ? ExecutionContext.Capture() : null;
        scheduler.Schedule(node, () => RunScheduledWork(snapshot, context, continuation));
    }

    /// <summary>
    /// Deterministically pumps ready simulation work on the calling logical thread until
    /// <paramref name="task"/> completes, backing controlled synchronous waits. Assumes a simulation is
    /// active.
    /// </summary>
    /// <param name="task">The task to wait for.</param>
    /// <param name="apiName">The controlled synchronous-wait API, for diagnostics.</param>
    /// <param name="cancellationToken">A token that can cancel the pump between simulation dispatches.</param>
    public static void DrainUntilCompleted(
        System.Threading.Tasks.Task task,
        string apiName,
        CancellationToken cancellationToken)
    {
        var (scheduler, _) = RequireScheduler(apiName);
        ArgumentNullException.ThrowIfNull(task);
        scheduler.DrainUntil(() => task.IsCompleted, cancellationToken);
    }

    /// <summary>
    /// Deterministically pumps ready simulation work until <paramref name="valueTask"/> completes, backing a
    /// synchronous <c>GetResult</c> on a controlled value-task awaiter. Assumes a simulation is active.
    /// </summary>
    /// <param name="valueTask">The value task to wait for.</param>
    /// <param name="apiName">The controlled API, for diagnostics.</param>
    internal static void DrainUntilCompleted(System.Threading.Tasks.ValueTask valueTask, string apiName) =>
        DrainUntil(() => valueTask.IsCompleted, apiName, CancellationToken.None);

    /// <summary>
    /// Deterministically pumps ready simulation work until <paramref name="valueTask"/> completes, backing a
    /// synchronous <c>GetResult</c> on a controlled value-task awaiter. Assumes a simulation is active.
    /// </summary>
    /// <typeparam name="TResult">The value task's result type.</typeparam>
    /// <param name="valueTask">The value task to wait for.</param>
    /// <param name="apiName">The controlled API, for diagnostics.</param>
    internal static void DrainUntilCompleted<TResult>(System.Threading.Tasks.ValueTask<TResult> valueTask, string apiName) =>
        DrainUntil(() => valueTask.IsCompleted, apiName, CancellationToken.None);

    /// <summary>
    /// Deterministically pumps ready simulation work until <paramref name="completed"/> holds. Used by
    /// multi-task synchronous waits (<c>WaitAll</c>/<c>WaitAny</c>). Assumes a simulation is active.
    /// </summary>
    /// <param name="completed">The predicate that ends the wait.</param>
    /// <param name="apiName">The controlled synchronous-wait API, for diagnostics.</param>
    /// <param name="cancellationToken">A token that can cancel the pump between simulation dispatches.</param>
    public static void DrainUntil(
        Func<bool> completed,
        string apiName,
        CancellationToken cancellationToken)
    {
        var (scheduler, _) = RequireScheduler(apiName);
        ArgumentNullException.ThrowIfNull(completed);
        scheduler.DrainUntil(completed, cancellationToken);
    }

    internal static bool RunOne(string apiName, CancellationToken cancellationToken)
    {
        var (scheduler, _) = RequireScheduler(apiName);
        return scheduler.RunOne(cancellationToken);
    }

    internal static void ParkIndefinitely(string apiName)
    {
        var (scheduler, node) = RequireScheduler(apiName);
        _ = scheduler.ScheduleWhenReady(node, static () => false, static () => { });
    }

    /// <summary>
    /// Registers a deterministic virtual-time deadline on the ambient coordinator, backing the finite
    /// timeout of a controlled synchronization wait. Requires an active simulation.
    /// </summary>
    /// <param name="delay">The strictly positive modelled delay before the deadline elapses.</param>
    /// <param name="onElapsed">An optional callback invoked once, on the logical thread, when the deadline elapses.</param>
    /// <param name="apiName">The controlled API registering the timeout, for diagnostics.</param>
    /// <returns>A handle used to observe elapse or cancel the deadline.</returns>
    public static ISimulationTimer RegisterTimeout(TimeSpan delay, Action? onElapsed, string apiName)
    {
        var (scheduler, node) = RequireScheduler(apiName);
        return scheduler.RegisterTimer(node, delay, onElapsed);
    }

    internal static ISimulationTimer RegisterTimeout(
        SimulationExecutionSnapshot snapshot,
        TimeSpan delay,
        Action? onElapsed,
        string apiName)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return snapshot.Runtime.Scheduler.RegisterTimer(
            snapshot.Node,
            delay,
            onElapsed);
    }

    internal static void QueueCapturedWork(
        SimulationExecutionSnapshot snapshot,
        ExecutionContext? context,
        Action work,
        string apiName)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(work);
        snapshot.Runtime.Scheduler.Schedule(
            snapshot.Node,
            () => SimulationSynchronizationFlow.RunAsNewStrand(
                () => RunScheduledWork(snapshot, context, work)));
    }

    private static void RunScheduledWork(
        SimulationExecutionSnapshot snapshot,
        ExecutionContext? context,
        Action work)
    {
        if (context is null)
        {
            RunWithoutUserExecutionContext(snapshot, work);
            return;
        }

        RunWithCapturedExecutionContext(context, work);
    }

    internal static void RunInSimulationSnapshot(
        SimulationExecutionSnapshot snapshot,
        long strandId,
        Action work)
    {
        using var runtimeScope = SimulationExecutionContext.EnterRuntime(snapshot.Runtime);
        using var nodeScope = snapshot.Node is null
            ? null
            : SimulationExecutionContext.EnterNode(snapshot.Node);
        using var executionScope = snapshot.LogicalExecutionId.IsNone
            ? null
            : SimulationExecutionContext.EnterLogicalExecution(snapshot.LogicalExecutionId);
        SimulationSynchronizationFlow.RunAsStrand(strandId, work);
    }
}
