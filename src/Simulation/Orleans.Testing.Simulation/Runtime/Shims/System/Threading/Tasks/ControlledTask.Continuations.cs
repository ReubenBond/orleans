using System.Threading;
using System.Threading.Tasks;
using Orleans.Testing.Simulation.Runtime;
using Orleans.Testing.Simulation.Runtime.Execution;
using Orleans.Testing.Simulation.Runtime.Racing;
using Orleans.Testing.Simulation.Runtime.Shims;

namespace Orleans.Testing.Simulation.Shims.System.Threading.Tasks;

#pragma warning disable CA1068 // CancellationToken parameters mirror the BCL ContinueWith overload order.

/// <summary>
/// <para>
/// Controlled <see cref="Task.ContinueWith(Action{Task})"/> and <see cref="Task{TResult}.ContinueWith(Action{Task{TResult}})"/>:
/// every .NET 11 overload of both types, with and without state, a cancellation token, continuation options, and a
/// scheduler. The returned task is a genuine BCL continuation task, so its status, <see cref="Task.CreationOptions"/>,
/// <see cref="Task.AsyncState"/>, cancellation (including <see cref="TaskContinuationOptions.LazyCancellation"/>),
/// <c>NotOn*</c>/<c>OnlyOn*</c> filtering, and exception handling are the BCL's own. It continues a private gate
/// rather than the antecedent, so the BCL registers nothing on the antecedent: the coordinator observes the antecedent
/// complete, as the controlled awaiters do, and a controlled operation then completes the gate with the antecedent's
/// status, which runs the continuation inline on that operation. The gate copies the status alone, never the
/// antecedent's exceptions, so a fault the continuation ignores stays unobserved, as in the BCL.
/// </para>
/// <para>
/// <see cref="TaskContinuationOptions.ExecuteSynchronously"/> is a hint in the BCL and is always applied to the gate, so
/// the continuation runs on the logical thread rather than being queued to the thread pool; the continuation therefore
/// never runs inside the call that completes the antecedent, and never inside <c>ContinueWith</c> itself.
/// </para>
/// <para>
/// The overloads without a scheduler use <see cref="TaskScheduler.Default"/> rather than <see cref="TaskScheduler.Current"/>,
/// as the controlled <c>TaskFactory.StartNew</c> treats a factory without a scheduler: inside a simulation
/// <see cref="TaskScheduler.Current"/> is <see cref="TaskScheduler.Default"/> or the simulation's own
/// <see cref="SimulationTaskScheduler"/> (in the root task of <see cref="SimulationCluster.RunToCompletion(Func{Task}, CancellationToken, int)"/>
/// before its first await, for example), and the continuation runs on the coordinator either way. An explicit
/// <see cref="TaskScheduler.Default"/> or <see cref="SimulationTaskScheduler"/> is accepted on the same terms.
/// <see cref="TaskContinuationOptions.LongRunning"/> (which requests a dedicated thread),
/// <see cref="TaskContinuationOptions.AttachedToParent"/> (Clockwork's controlled tasks have no parent), and any other
/// explicit scheduler cannot be preserved and throw <see cref="SimulationApiException"/>.
/// </para>
/// </summary>
public static partial class ControlledTask
{
    private const string ContinueWithApi = "System.Threading.Tasks.Task.ContinueWith";
    private const string GenericContinueWithApi = "System.Threading.Tasks.Task`1.ContinueWith";

    // The fault a gate carries for a faulted antecedent. It never escapes: the continuation receives the antecedent.
    private static readonly InvalidOperationException GateFault = new("The antecedent faulted.");

    /// <summary>Controlled <c>Task.ContinueWith(Action&lt;Task&gt;)</c>.</summary>
    public static Task ContinueWith(Task antecedent, Action<Task> continuationAction) =>
        ContinueWith(antecedent, continuationAction, default, TaskContinuationOptions.None, TaskScheduler.Default);

    /// <summary>Controlled <c>Task.ContinueWith(Action&lt;Task&gt;, CancellationToken)</c>.</summary>
    public static Task ContinueWith(Task antecedent, Action<Task> continuationAction, CancellationToken cancellationToken) =>
        ContinueWith(antecedent, continuationAction, cancellationToken, TaskContinuationOptions.None, TaskScheduler.Default);

    /// <summary>Controlled <c>Task.ContinueWith(Action&lt;Task&gt;, TaskScheduler)</c>.</summary>
    public static Task ContinueWith(Task antecedent, Action<Task> continuationAction, TaskScheduler scheduler) =>
        ContinueWith(antecedent, continuationAction, default, TaskContinuationOptions.None, scheduler);

    /// <summary>Controlled <c>Task.ContinueWith(Action&lt;Task&gt;, TaskContinuationOptions)</c>.</summary>
    public static Task ContinueWith(Task antecedent, Action<Task> continuationAction, TaskContinuationOptions continuationOptions) =>
        ContinueWith(antecedent, continuationAction, default, continuationOptions, TaskScheduler.Default);

    /// <summary>Controlled <c>Task.ContinueWith(Action&lt;Task&gt;, CancellationToken, TaskContinuationOptions, TaskScheduler)</c>.</summary>
    public static Task ContinueWith(
        Task antecedent,
        Action<Task> continuationAction,
        CancellationToken cancellationToken,
        TaskContinuationOptions continuationOptions,
        TaskScheduler scheduler) =>
        Continue(
            antecedent,
            continuationAction,
            nameof(continuationAction),
            cancellationToken,
            continuationOptions,
            scheduler,
            ContinueWithApi,
            (gate, token, options) => gate.ContinueWith(_ => continuationAction(antecedent), token, options, TaskScheduler.Default));

    /// <summary>Controlled <c>Task.ContinueWith(Action&lt;Task, object&gt;, object)</c>.</summary>
    public static Task ContinueWith(Task antecedent, Action<Task, object?> continuationAction, object? state) =>
        ContinueWith(antecedent, continuationAction, state, default, TaskContinuationOptions.None, TaskScheduler.Default);

    /// <summary>Controlled <c>Task.ContinueWith(Action&lt;Task, object&gt;, object, CancellationToken)</c>.</summary>
    public static Task ContinueWith(
        Task antecedent,
        Action<Task, object?> continuationAction,
        object? state,
        CancellationToken cancellationToken) =>
        ContinueWith(antecedent, continuationAction, state, cancellationToken, TaskContinuationOptions.None, TaskScheduler.Default);

    /// <summary>Controlled <c>Task.ContinueWith(Action&lt;Task, object&gt;, object, TaskScheduler)</c>.</summary>
    public static Task ContinueWith(
        Task antecedent,
        Action<Task, object?> continuationAction,
        object? state,
        TaskScheduler scheduler) =>
        ContinueWith(antecedent, continuationAction, state, default, TaskContinuationOptions.None, scheduler);

    /// <summary>Controlled <c>Task.ContinueWith(Action&lt;Task, object&gt;, object, TaskContinuationOptions)</c>.</summary>
    public static Task ContinueWith(
        Task antecedent,
        Action<Task, object?> continuationAction,
        object? state,
        TaskContinuationOptions continuationOptions) =>
        ContinueWith(antecedent, continuationAction, state, default, continuationOptions, TaskScheduler.Default);

    /// <summary>Controlled <c>Task.ContinueWith(Action&lt;Task, object&gt;, object, CancellationToken, TaskContinuationOptions, TaskScheduler)</c>.</summary>
    public static Task ContinueWith(
        Task antecedent,
        Action<Task, object?> continuationAction,
        object? state,
        CancellationToken cancellationToken,
        TaskContinuationOptions continuationOptions,
        TaskScheduler scheduler) =>
        Continue(
            antecedent,
            continuationAction,
            nameof(continuationAction),
            cancellationToken,
            continuationOptions,
            scheduler,
            ContinueWithApi,
            (gate, token, options) => gate.ContinueWith(
                (_, value) => continuationAction(antecedent, value),
                state,
                token,
                options,
                TaskScheduler.Default));

    /// <summary>Controlled <c>Task.ContinueWith&lt;TResult&gt;(Func&lt;Task, TResult&gt;)</c>.</summary>
    public static Task<TResult> ContinueWith<TResult>(Task antecedent, Func<Task, TResult> continuationFunction) =>
        ContinueWith(antecedent, continuationFunction, default, TaskContinuationOptions.None, TaskScheduler.Default);

    /// <summary>Controlled <c>Task.ContinueWith&lt;TResult&gt;(Func&lt;Task, TResult&gt;, CancellationToken)</c>.</summary>
    public static Task<TResult> ContinueWith<TResult>(
        Task antecedent,
        Func<Task, TResult> continuationFunction,
        CancellationToken cancellationToken) =>
        ContinueWith(antecedent, continuationFunction, cancellationToken, TaskContinuationOptions.None, TaskScheduler.Default);

    /// <summary>Controlled <c>Task.ContinueWith&lt;TResult&gt;(Func&lt;Task, TResult&gt;, TaskScheduler)</c>.</summary>
    public static Task<TResult> ContinueWith<TResult>(
        Task antecedent,
        Func<Task, TResult> continuationFunction,
        TaskScheduler scheduler) =>
        ContinueWith(antecedent, continuationFunction, default, TaskContinuationOptions.None, scheduler);

    /// <summary>Controlled <c>Task.ContinueWith&lt;TResult&gt;(Func&lt;Task, TResult&gt;, TaskContinuationOptions)</c>.</summary>
    public static Task<TResult> ContinueWith<TResult>(
        Task antecedent,
        Func<Task, TResult> continuationFunction,
        TaskContinuationOptions continuationOptions) =>
        ContinueWith(antecedent, continuationFunction, default, continuationOptions, TaskScheduler.Default);

    /// <summary>Controlled <c>Task.ContinueWith&lt;TResult&gt;(Func&lt;Task, TResult&gt;, CancellationToken, TaskContinuationOptions, TaskScheduler)</c>.</summary>
    public static Task<TResult> ContinueWith<TResult>(
        Task antecedent,
        Func<Task, TResult> continuationFunction,
        CancellationToken cancellationToken,
        TaskContinuationOptions continuationOptions,
        TaskScheduler scheduler) =>
        Continue(
            antecedent,
            continuationFunction,
            nameof(continuationFunction),
            cancellationToken,
            continuationOptions,
            scheduler,
            ContinueWithApi,
            (gate, token, options) => gate.ContinueWith(_ => continuationFunction(antecedent), token, options, TaskScheduler.Default));

    /// <summary>Controlled <c>Task.ContinueWith&lt;TResult&gt;(Func&lt;Task, object, TResult&gt;, object)</c>.</summary>
    public static Task<TResult> ContinueWith<TResult>(
        Task antecedent,
        Func<Task, object?, TResult> continuationFunction,
        object? state) =>
        ContinueWith(antecedent, continuationFunction, state, default, TaskContinuationOptions.None, TaskScheduler.Default);

    /// <summary>Controlled <c>Task.ContinueWith&lt;TResult&gt;(Func&lt;Task, object, TResult&gt;, object, CancellationToken)</c>.</summary>
    public static Task<TResult> ContinueWith<TResult>(
        Task antecedent,
        Func<Task, object?, TResult> continuationFunction,
        object? state,
        CancellationToken cancellationToken) =>
        ContinueWith(antecedent, continuationFunction, state, cancellationToken, TaskContinuationOptions.None, TaskScheduler.Default);

    /// <summary>Controlled <c>Task.ContinueWith&lt;TResult&gt;(Func&lt;Task, object, TResult&gt;, object, TaskScheduler)</c>.</summary>
    public static Task<TResult> ContinueWith<TResult>(
        Task antecedent,
        Func<Task, object?, TResult> continuationFunction,
        object? state,
        TaskScheduler scheduler) =>
        ContinueWith(antecedent, continuationFunction, state, default, TaskContinuationOptions.None, scheduler);

    /// <summary>Controlled <c>Task.ContinueWith&lt;TResult&gt;(Func&lt;Task, object, TResult&gt;, object, TaskContinuationOptions)</c>.</summary>
    public static Task<TResult> ContinueWith<TResult>(
        Task antecedent,
        Func<Task, object?, TResult> continuationFunction,
        object? state,
        TaskContinuationOptions continuationOptions) =>
        ContinueWith(antecedent, continuationFunction, state, default, continuationOptions, TaskScheduler.Default);

    /// <summary>Controlled <c>Task.ContinueWith&lt;TResult&gt;(Func&lt;Task, object, TResult&gt;, object, CancellationToken, TaskContinuationOptions, TaskScheduler)</c>.</summary>
    public static Task<TResult> ContinueWith<TResult>(
        Task antecedent,
        Func<Task, object?, TResult> continuationFunction,
        object? state,
        CancellationToken cancellationToken,
        TaskContinuationOptions continuationOptions,
        TaskScheduler scheduler) =>
        Continue(
            antecedent,
            continuationFunction,
            nameof(continuationFunction),
            cancellationToken,
            continuationOptions,
            scheduler,
            ContinueWithApi,
            (gate, token, options) => gate.ContinueWith(
                (_, value) => continuationFunction(antecedent, value),
                state,
                token,
                options,
                TaskScheduler.Default));

    /// <summary>Controlled <c>Task&lt;TResult&gt;.ContinueWith(Action&lt;Task&lt;TResult&gt;&gt;)</c>.</summary>
    public static Task ContinueWith<TResult>(Task<TResult> antecedent, Action<Task<TResult>> continuationAction) =>
        ContinueWith(antecedent, continuationAction, default, TaskContinuationOptions.None, TaskScheduler.Default);

    /// <summary>Controlled <c>Task&lt;TResult&gt;.ContinueWith(Action&lt;Task&lt;TResult&gt;&gt;, CancellationToken)</c>.</summary>
    public static Task ContinueWith<TResult>(
        Task<TResult> antecedent,
        Action<Task<TResult>> continuationAction,
        CancellationToken cancellationToken) =>
        ContinueWith(antecedent, continuationAction, cancellationToken, TaskContinuationOptions.None, TaskScheduler.Default);

    /// <summary>Controlled <c>Task&lt;TResult&gt;.ContinueWith(Action&lt;Task&lt;TResult&gt;&gt;, TaskScheduler)</c>.</summary>
    public static Task ContinueWith<TResult>(
        Task<TResult> antecedent,
        Action<Task<TResult>> continuationAction,
        TaskScheduler scheduler) =>
        ContinueWith(antecedent, continuationAction, default, TaskContinuationOptions.None, scheduler);

    /// <summary>Controlled <c>Task&lt;TResult&gt;.ContinueWith(Action&lt;Task&lt;TResult&gt;&gt;, TaskContinuationOptions)</c>.</summary>
    public static Task ContinueWith<TResult>(
        Task<TResult> antecedent,
        Action<Task<TResult>> continuationAction,
        TaskContinuationOptions continuationOptions) =>
        ContinueWith(antecedent, continuationAction, default, continuationOptions, TaskScheduler.Default);

    /// <summary>Controlled <c>Task&lt;TResult&gt;.ContinueWith(Action&lt;Task&lt;TResult&gt;&gt;, CancellationToken, TaskContinuationOptions, TaskScheduler)</c>.</summary>
    public static Task ContinueWith<TResult>(
        Task<TResult> antecedent,
        Action<Task<TResult>> continuationAction,
        CancellationToken cancellationToken,
        TaskContinuationOptions continuationOptions,
        TaskScheduler scheduler) =>
        Continue(
            antecedent,
            continuationAction,
            nameof(continuationAction),
            cancellationToken,
            continuationOptions,
            scheduler,
            GenericContinueWithApi,
            (gate, token, options) => gate.ContinueWith(_ => continuationAction(antecedent), token, options, TaskScheduler.Default));

    /// <summary>Controlled <c>Task&lt;TResult&gt;.ContinueWith(Action&lt;Task&lt;TResult&gt;, object&gt;, object)</c>.</summary>
    public static Task ContinueWith<TResult>(
        Task<TResult> antecedent,
        Action<Task<TResult>, object?> continuationAction,
        object? state) =>
        ContinueWith(antecedent, continuationAction, state, default, TaskContinuationOptions.None, TaskScheduler.Default);

    /// <summary>Controlled <c>Task&lt;TResult&gt;.ContinueWith(Action&lt;Task&lt;TResult&gt;, object&gt;, object, CancellationToken)</c>.</summary>
    public static Task ContinueWith<TResult>(
        Task<TResult> antecedent,
        Action<Task<TResult>, object?> continuationAction,
        object? state,
        CancellationToken cancellationToken) =>
        ContinueWith(antecedent, continuationAction, state, cancellationToken, TaskContinuationOptions.None, TaskScheduler.Default);

    /// <summary>Controlled <c>Task&lt;TResult&gt;.ContinueWith(Action&lt;Task&lt;TResult&gt;, object&gt;, object, TaskScheduler)</c>.</summary>
    public static Task ContinueWith<TResult>(
        Task<TResult> antecedent,
        Action<Task<TResult>, object?> continuationAction,
        object? state,
        TaskScheduler scheduler) =>
        ContinueWith(antecedent, continuationAction, state, default, TaskContinuationOptions.None, scheduler);

    /// <summary>Controlled <c>Task&lt;TResult&gt;.ContinueWith(Action&lt;Task&lt;TResult&gt;, object&gt;, object, TaskContinuationOptions)</c>.</summary>
    public static Task ContinueWith<TResult>(
        Task<TResult> antecedent,
        Action<Task<TResult>, object?> continuationAction,
        object? state,
        TaskContinuationOptions continuationOptions) =>
        ContinueWith(antecedent, continuationAction, state, default, continuationOptions, TaskScheduler.Default);

    /// <summary>Controlled <c>Task&lt;TResult&gt;.ContinueWith(Action&lt;Task&lt;TResult&gt;, object&gt;, object, CancellationToken, TaskContinuationOptions, TaskScheduler)</c>.</summary>
    public static Task ContinueWith<TResult>(
        Task<TResult> antecedent,
        Action<Task<TResult>, object?> continuationAction,
        object? state,
        CancellationToken cancellationToken,
        TaskContinuationOptions continuationOptions,
        TaskScheduler scheduler) =>
        Continue(
            antecedent,
            continuationAction,
            nameof(continuationAction),
            cancellationToken,
            continuationOptions,
            scheduler,
            GenericContinueWithApi,
            (gate, token, options) => gate.ContinueWith(
                (_, value) => continuationAction(antecedent, value),
                state,
                token,
                options,
                TaskScheduler.Default));

    /// <summary>Controlled <c>Task&lt;TResult&gt;.ContinueWith&lt;TNewResult&gt;(Func&lt;Task&lt;TResult&gt;, TNewResult&gt;)</c>.</summary>
    public static Task<TNewResult> ContinueWith<TResult, TNewResult>(
        Task<TResult> antecedent,
        Func<Task<TResult>, TNewResult> continuationFunction) =>
        ContinueWith(antecedent, continuationFunction, default, TaskContinuationOptions.None, TaskScheduler.Default);

    /// <summary>Controlled <c>Task&lt;TResult&gt;.ContinueWith&lt;TNewResult&gt;(Func&lt;Task&lt;TResult&gt;, TNewResult&gt;, CancellationToken)</c>.</summary>
    public static Task<TNewResult> ContinueWith<TResult, TNewResult>(
        Task<TResult> antecedent,
        Func<Task<TResult>, TNewResult> continuationFunction,
        CancellationToken cancellationToken) =>
        ContinueWith(antecedent, continuationFunction, cancellationToken, TaskContinuationOptions.None, TaskScheduler.Default);

    /// <summary>Controlled <c>Task&lt;TResult&gt;.ContinueWith&lt;TNewResult&gt;(Func&lt;Task&lt;TResult&gt;, TNewResult&gt;, TaskScheduler)</c>.</summary>
    public static Task<TNewResult> ContinueWith<TResult, TNewResult>(
        Task<TResult> antecedent,
        Func<Task<TResult>, TNewResult> continuationFunction,
        TaskScheduler scheduler) =>
        ContinueWith(antecedent, continuationFunction, default, TaskContinuationOptions.None, scheduler);

    /// <summary>Controlled <c>Task&lt;TResult&gt;.ContinueWith&lt;TNewResult&gt;(Func&lt;Task&lt;TResult&gt;, TNewResult&gt;, TaskContinuationOptions)</c>.</summary>
    public static Task<TNewResult> ContinueWith<TResult, TNewResult>(
        Task<TResult> antecedent,
        Func<Task<TResult>, TNewResult> continuationFunction,
        TaskContinuationOptions continuationOptions) =>
        ContinueWith(antecedent, continuationFunction, default, continuationOptions, TaskScheduler.Default);

    /// <summary>
    /// Controlled <c>Task&lt;TResult&gt;.ContinueWith&lt;TNewResult&gt;(Func&lt;Task&lt;TResult&gt;, TNewResult&gt;, CancellationToken, TaskContinuationOptions, TaskScheduler)</c>.
    /// The call-site pass binds the type parameters declaring-type-first (<typeparamref name="TResult"/>) then
    /// method-argument (<typeparamref name="TNewResult"/>).
    /// </summary>
    public static Task<TNewResult> ContinueWith<TResult, TNewResult>(
        Task<TResult> antecedent,
        Func<Task<TResult>, TNewResult> continuationFunction,
        CancellationToken cancellationToken,
        TaskContinuationOptions continuationOptions,
        TaskScheduler scheduler) =>
        Continue(
            antecedent,
            continuationFunction,
            nameof(continuationFunction),
            cancellationToken,
            continuationOptions,
            scheduler,
            GenericContinueWithApi,
            (gate, token, options) => gate.ContinueWith(_ => continuationFunction(antecedent), token, options, TaskScheduler.Default));

    /// <summary>Controlled <c>Task&lt;TResult&gt;.ContinueWith&lt;TNewResult&gt;(Func&lt;Task&lt;TResult&gt;, object, TNewResult&gt;, object)</c>.</summary>
    public static Task<TNewResult> ContinueWith<TResult, TNewResult>(
        Task<TResult> antecedent,
        Func<Task<TResult>, object?, TNewResult> continuationFunction,
        object? state) =>
        ContinueWith(antecedent, continuationFunction, state, default, TaskContinuationOptions.None, TaskScheduler.Default);

    /// <summary>Controlled <c>Task&lt;TResult&gt;.ContinueWith&lt;TNewResult&gt;(Func&lt;Task&lt;TResult&gt;, object, TNewResult&gt;, object, CancellationToken)</c>.</summary>
    public static Task<TNewResult> ContinueWith<TResult, TNewResult>(
        Task<TResult> antecedent,
        Func<Task<TResult>, object?, TNewResult> continuationFunction,
        object? state,
        CancellationToken cancellationToken) =>
        ContinueWith(antecedent, continuationFunction, state, cancellationToken, TaskContinuationOptions.None, TaskScheduler.Default);

    /// <summary>Controlled <c>Task&lt;TResult&gt;.ContinueWith&lt;TNewResult&gt;(Func&lt;Task&lt;TResult&gt;, object, TNewResult&gt;, object, TaskScheduler)</c>.</summary>
    public static Task<TNewResult> ContinueWith<TResult, TNewResult>(
        Task<TResult> antecedent,
        Func<Task<TResult>, object?, TNewResult> continuationFunction,
        object? state,
        TaskScheduler scheduler) =>
        ContinueWith(antecedent, continuationFunction, state, default, TaskContinuationOptions.None, scheduler);

    /// <summary>Controlled <c>Task&lt;TResult&gt;.ContinueWith&lt;TNewResult&gt;(Func&lt;Task&lt;TResult&gt;, object, TNewResult&gt;, object, TaskContinuationOptions)</c>.</summary>
    public static Task<TNewResult> ContinueWith<TResult, TNewResult>(
        Task<TResult> antecedent,
        Func<Task<TResult>, object?, TNewResult> continuationFunction,
        object? state,
        TaskContinuationOptions continuationOptions) =>
        ContinueWith(antecedent, continuationFunction, state, default, continuationOptions, TaskScheduler.Default);

    /// <summary>Controlled <c>Task&lt;TResult&gt;.ContinueWith&lt;TNewResult&gt;(Func&lt;Task&lt;TResult&gt;, object, TNewResult&gt;, object, CancellationToken, TaskContinuationOptions, TaskScheduler)</c>.</summary>
    public static Task<TNewResult> ContinueWith<TResult, TNewResult>(
        Task<TResult> antecedent,
        Func<Task<TResult>, object?, TNewResult> continuationFunction,
        object? state,
        CancellationToken cancellationToken,
        TaskContinuationOptions continuationOptions,
        TaskScheduler scheduler) =>
        Continue(
            antecedent,
            continuationFunction,
            nameof(continuationFunction),
            cancellationToken,
            continuationOptions,
            scheduler,
            GenericContinueWithApi,
            (gate, token, options) => gate.ContinueWith(
                (_, value) => continuationFunction(antecedent, value),
                state,
                token,
                options,
                TaskScheduler.Default));

    private static TContinuation Continue<TContinuation>(
        Task antecedent,
        Delegate continuation,
        string continuationParameterName,
        CancellationToken cancellationToken,
        TaskContinuationOptions continuationOptions,
        TaskScheduler scheduler,
        string apiName,
        Func<Task, CancellationToken, TaskContinuationOptions, TContinuation> continueGate)
        where TContinuation : Task
    {
        SimulationExecutionSnapshot caller = SimulationRuntimeDispatch.RequireActiveSimulation(apiName);

        // The BCL's ContinueWith is an instance method: a null antecedent fails the call itself, with the runtime's
        // NullReferenceException, before any argument is validated.
        _ = antecedent.IsCompleted;

        ArgumentNullException.ThrowIfNull(continuation, continuationParameterName);
        ArgumentNullException.ThrowIfNull(scheduler);
        ValidateContinuationOptions(continuationOptions);
        SimulationExecutionSnapshot target = RejectUnpreservableContinuation(continuationOptions, scheduler, caller, apiName);

        return ContinueGate(
            target,
            () => antecedent.IsCompleted,
            () =>
            {
                RaceSynchronization.Wait(antecedent);
                return antecedent.Status;
            },
            cancellationToken,
            continuationOptions,
            apiName,
            continueGate);
    }

    /// <summary>
    /// Creates a genuine BCL continuation of a private gate, and completes the gate on a controlled operation, in the
    /// caller's execution context and on a fresh logical strand, once the coordinator observes the antecedents ready.
    /// The BCL registers nothing on the antecedents.
    /// </summary>
    /// <param name="target">The snapshot, and so the node, the continuation runs under.</param>
    /// <param name="isReady">
    /// Whether the antecedents are ready: a readiness predicate (see <see cref="SimulationTaskRuntime.ScheduleWhenReady(Func{bool}, Action, string)"/>)
    /// which, once it has held, holds again when the controlled operation evaluates it.
    /// </param>
    /// <param name="observe">
    /// Runs on the controlled operation once the antecedents are ready: acquires their race-detection edges and returns
    /// the status the gate takes.
    /// </param>
    /// <param name="cancellationToken">The continuation's cancellation token.</param>
    /// <param name="continuationOptions">The continuation's validated options.</param>
    /// <param name="apiName">The controlled API, for diagnostics.</param>
    /// <param name="continueGate">Creates the continuation of the gate task, with the given token and options.</param>
    internal static TContinuation ContinueGate<TContinuation>(
        SimulationExecutionSnapshot target,
        Func<bool> isReady,
        Func<TaskStatus> observe,
        CancellationToken cancellationToken,
        TaskContinuationOptions continuationOptions,
        string apiName,
        Func<Task, CancellationToken, TaskContinuationOptions, TContinuation> continueGate)
        where TContinuation : Task
    {
        ExecutionContext? context = ExecutionContext.Capture();
        var gate = new TaskCompletionSource();

        // The continuation runs inline on a controlled operation, in the execution context captured here and on a
        // fresh logical strand, so the gate continuation must not capture and restore a context of its own.
        TContinuation continuationTask;
        if (ExecutionContext.IsFlowSuppressed())
        {
            continuationTask = continueGate(gate.Task, cancellationToken, continuationOptions | TaskContinuationOptions.ExecuteSynchronously);
        }
        else
        {
            using (ExecutionContext.SuppressFlow())
            {
                continuationTask = continueGate(gate.Task, cancellationToken, continuationOptions | TaskContinuationOptions.ExecuteSynchronously);
            }
        }

        SimulationTaskRuntime.ScheduleWhenReady(
            target,
            () => isReady() || continuationTask.IsCompleted,
            () => SimulationTaskRuntime.RunWithCapturedExecutionContextAsNewStrand(
                context,
                () =>
                {
                    // A continuation canceled through its token before its antecedents are ready is removed from the
                    // gate by the BCL, so nothing waits for them any longer.
                    if (!isReady())
                    {
                        return;
                    }

                    CompleteGate(gate, observe(), continuationTask, apiName);
                }),
            apiName);
        return continuationTask;
    }

    /// <summary>
    /// <para>
    /// Completes a continuation's private gate, on the controlled operation which observed the antecedent complete, from
    /// the antecedent's status alone, then publishes the continuation's completion. The continuation receives the real
    /// antecedent, so the gate's outcome only selects whether, and with which status, the BCL runs it. Copying the
    /// antecedent's exceptions (as <see cref="TaskCompletionSource.TrySetFromTask(Task)"/> does) would mark them
    /// observed, and suppress the <see cref="TaskScheduler.UnobservedTaskException"/> the BCL raises for a fault which
    /// no continuation observes.
    /// </para>
    /// <para>
    /// The gate's continuation is <see cref="TaskContinuationOptions.ExecuteSynchronously"/> on
    /// <see cref="TaskScheduler.Default"/>, so the BCL runs it inside the gate's completion unless it must queue it
    /// instead, as it does when the stack is nearly exhausted. A queued continuation would run on the thread pool,
    /// outside the simulation, so that fails the operation rather than going unnoticed.
    /// </para>
    /// </summary>
    internal static void CompleteGate(TaskCompletionSource gate, TaskStatus antecedentStatus, Task continuationTask, string apiName)
    {
        switch (antecedentStatus)
        {
            case TaskStatus.RanToCompletion:
                SimulationCompletion.TrySetResult(gate, ContinueWithApi);
                break;
            case TaskStatus.Canceled:
                SimulationCompletion.TrySetCanceled(gate, default, ContinueWithApi);
                break;
            default:
                SimulationCompletion.TrySetException(gate, GateFault, ContinueWithApi);

                // The sentinel fault is Clockwork's own: observing it keeps the gate from reporting it as unobserved.
                _ = gate.Task.Exception;
                break;
        }

        if (!continuationTask.IsCompleted)
        {
            throw new SimulationApiException(
                SimulationApiCategory.Task,
                apiName,
                "the runtime queued the continuation instead of running it inline on the controlled operation which " +
                "observed its antecedent complete (for example, because the stack was nearly exhausted), so it would run " +
                "outside the simulation.");
        }

        RaceSynchronization.Signal(continuationTask);
    }

    /// <summary>Validates continuation options exactly as the BCL's <c>ContinueWith</c> does.</summary>
    private static void ValidateContinuationOptions(TaskContinuationOptions continuationOptions)
    {
        const TaskContinuationOptions NotOnAnything =
            TaskContinuationOptions.NotOnCanceled | TaskContinuationOptions.NotOnFaulted | TaskContinuationOptions.NotOnRanToCompletion;
        const TaskContinuationOptions CreationOptionsMask =
            TaskContinuationOptions.PreferFairness | TaskContinuationOptions.LongRunning | TaskContinuationOptions.DenyChildAttach |
            TaskContinuationOptions.HideScheduler | TaskContinuationOptions.AttachedToParent |
            TaskContinuationOptions.RunContinuationsAsynchronously;
        const TaskContinuationOptions IllegalMask = TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.LongRunning;

        if ((continuationOptions & IllegalMask) == IllegalMask)
        {
            throw new ArgumentOutOfRangeException(
                nameof(continuationOptions),
                "The specified TaskContinuationOptions combined LongRunning and ExecuteSynchronously.  Synchronous continuations should not be long running.");
        }

        if ((continuationOptions & ~(CreationOptionsMask | NotOnAnything | TaskContinuationOptions.LazyCancellation |
            TaskContinuationOptions.ExecuteSynchronously)) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(continuationOptions));
        }

        if ((continuationOptions & NotOnAnything) == NotOnAnything)
        {
            throw new ArgumentOutOfRangeException(
                nameof(continuationOptions),
                "The specified TaskContinuationOptions excluded all continuation kinds.");
        }
    }

    /// <summary>
    /// Rejects the continuation options a continuation on the coordinator cannot preserve, and resolves the scheduler to
    /// the snapshot the continuation runs under (see <see cref="ControlledTaskScheduler.ResolveTarget"/>).
    /// </summary>
    /// <param name="continuationOptions">The continuation's validated options.</param>
    /// <param name="scheduler">The continuation's scheduler.</param>
    /// <param name="caller">The caller's simulation snapshot.</param>
    /// <param name="apiName">The controlled API, for diagnostics.</param>
    /// <returns>The snapshot the continuation runs under.</returns>
    internal static SimulationExecutionSnapshot RejectUnpreservableContinuation(
        TaskContinuationOptions continuationOptions,
        TaskScheduler scheduler,
        SimulationExecutionSnapshot caller,
        string apiName)
    {
        SimulationExecutionSnapshot target =
            ControlledTaskScheduler.ResolveTarget(scheduler, caller, SimulationApiCategory.Task, apiName);

        if ((continuationOptions & TaskContinuationOptions.LongRunning) != 0)
        {
            throw new SimulationApiException(
                SimulationApiCategory.Task,
                apiName,
                "TaskContinuationOptions.LongRunning requests a dedicated thread, which a continuation on the " +
                "simulation's logical thread cannot preserve.");
        }

        if ((continuationOptions & TaskContinuationOptions.AttachedToParent) != 0)
        {
            throw new SimulationApiException(
                SimulationApiCategory.Task,
                apiName,
                "TaskContinuationOptions.AttachedToParent attaches the continuation to a parent task, and Clockwork's " +
                "controlled tasks do not model parent and child tasks.");
        }

        return target;
    }
}

#pragma warning restore CA1068
