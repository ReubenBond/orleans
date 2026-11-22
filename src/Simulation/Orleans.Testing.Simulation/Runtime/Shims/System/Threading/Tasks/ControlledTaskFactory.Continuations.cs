using System.Threading;
using System.Threading.Tasks;
using Orleans.Testing.Simulation.Runtime;
using Orleans.Testing.Simulation.Runtime.Execution;
using Orleans.Testing.Simulation.Runtime.Racing;
using Orleans.Testing.Simulation.Runtime.Shims;

namespace Orleans.Testing.Simulation.Shims.System.Threading.Tasks;

#pragma warning disable CA1068 // CancellationToken parameters mirror the BCL ContinueWhenAll/ContinueWhenAny overload order.

// Controlled TaskFactory and TaskFactory<TResult> ContinueWhenAll and ContinueWhenAny, and the FromAsync rejection.
public static partial class ControlledTaskFactory
{
    private const string ContinueWhenAllApi = "System.Threading.Tasks.TaskFactory.ContinueWhenAll";
    private const string ContinueWhenAnyApi = "System.Threading.Tasks.TaskFactory.ContinueWhenAny";
    private const string GenericContinueWhenAllApi = "System.Threading.Tasks.TaskFactory`1.ContinueWhenAll";
    private const string GenericContinueWhenAnyApi = "System.Threading.Tasks.TaskFactory`1.ContinueWhenAny";
    private const string TasksParameter = "tasks";

    // ---- TaskFactory.ContinueWhenAll(Task[], Action<Task[]>) ----

    /// <summary>Controlled <c>TaskFactory.ContinueWhenAll(Task[], Action&lt;Task[]&gt;)</c>.</summary>
    public static Task ContinueWhenAll(TaskFactory factory, Task[] tasks, Action<Task[]> continuationAction) =>
        ContinueWhenAll(RequiredReceiver(factory, ContinueWhenAllApi), tasks, continuationAction, factory.CancellationToken, factory.ContinuationOptions, DefaultScheduler(factory));

    /// <summary>Controlled <c>TaskFactory.ContinueWhenAll(Task[], Action&lt;Task[]&gt;, CancellationToken)</c>.</summary>
    public static Task ContinueWhenAll(
        TaskFactory factory,
        Task[] tasks,
        Action<Task[]> continuationAction,
        CancellationToken cancellationToken) =>
        ContinueWhenAll(RequiredReceiver(factory, ContinueWhenAllApi), tasks, continuationAction, cancellationToken, factory.ContinuationOptions, DefaultScheduler(factory));

    /// <summary>Controlled <c>TaskFactory.ContinueWhenAll(Task[], Action&lt;Task[]&gt;, TaskContinuationOptions)</c>.</summary>
    public static Task ContinueWhenAll(
        TaskFactory factory,
        Task[] tasks,
        Action<Task[]> continuationAction,
        TaskContinuationOptions continuationOptions) =>
        ContinueWhenAll(RequiredReceiver(factory, ContinueWhenAllApi), tasks, continuationAction, factory.CancellationToken, continuationOptions, DefaultScheduler(factory));

    /// <summary>Controlled <c>TaskFactory.ContinueWhenAll(Task[], Action&lt;Task[]&gt;, CancellationToken, TaskContinuationOptions, TaskScheduler)</c>.</summary>
    public static Task ContinueWhenAll(
        TaskFactory factory,
        Task[] tasks,
        Action<Task[]> continuationAction,
        CancellationToken cancellationToken,
        TaskContinuationOptions continuationOptions,
        TaskScheduler scheduler)
    {
        RequireReceiver(factory, ContinueWhenAllApi);
        ArgumentNullException.ThrowIfNull(continuationAction);
        return WhenAll(tasks, continuationAction, cancellationToken, continuationOptions, scheduler, ContinueWhenAllApi, ContinueAll<Task>, CanceledTask);

    }

    // ---- TaskFactory.ContinueWhenAll<TAntecedentResult>(Task<TAntecedentResult>[], Action<Task<TAntecedentResult>[]>) ----

    /// <summary>Controlled <c>TaskFactory.ContinueWhenAll&lt;TAntecedentResult&gt;(Task&lt;TAntecedentResult&gt;[], Action&lt;Task&lt;TAntecedentResult&gt;[]&gt;)</c>.</summary>
    public static Task ContinueWhenAll<TAntecedentResult>(
        TaskFactory factory,
        Task<TAntecedentResult>[] tasks,
        Action<Task<TAntecedentResult>[]> continuationAction) =>
        ContinueWhenAll(RequiredReceiver(factory, ContinueWhenAllApi), tasks, continuationAction, factory.CancellationToken, factory.ContinuationOptions, DefaultScheduler(factory));

    /// <summary>Controlled <c>TaskFactory.ContinueWhenAll&lt;TAntecedentResult&gt;(Task&lt;TAntecedentResult&gt;[], Action&lt;Task&lt;TAntecedentResult&gt;[]&gt;, CancellationToken)</c>.</summary>
    public static Task ContinueWhenAll<TAntecedentResult>(
        TaskFactory factory,
        Task<TAntecedentResult>[] tasks,
        Action<Task<TAntecedentResult>[]> continuationAction,
        CancellationToken cancellationToken) =>
        ContinueWhenAll(RequiredReceiver(factory, ContinueWhenAllApi), tasks, continuationAction, cancellationToken, factory.ContinuationOptions, DefaultScheduler(factory));

    /// <summary>Controlled <c>TaskFactory.ContinueWhenAll&lt;TAntecedentResult&gt;(Task&lt;TAntecedentResult&gt;[], Action&lt;Task&lt;TAntecedentResult&gt;[]&gt;, TaskContinuationOptions)</c>.</summary>
    public static Task ContinueWhenAll<TAntecedentResult>(
        TaskFactory factory,
        Task<TAntecedentResult>[] tasks,
        Action<Task<TAntecedentResult>[]> continuationAction,
        TaskContinuationOptions continuationOptions) =>
        ContinueWhenAll(RequiredReceiver(factory, ContinueWhenAllApi), tasks, continuationAction, factory.CancellationToken, continuationOptions, DefaultScheduler(factory));

    /// <summary>Controlled <c>TaskFactory.ContinueWhenAll&lt;TAntecedentResult&gt;(Task&lt;TAntecedentResult&gt;[], Action&lt;Task&lt;TAntecedentResult&gt;[]&gt;, CancellationToken, TaskContinuationOptions, TaskScheduler)</c>.</summary>
    public static Task ContinueWhenAll<TAntecedentResult>(
        TaskFactory factory,
        Task<TAntecedentResult>[] tasks,
        Action<Task<TAntecedentResult>[]> continuationAction,
        CancellationToken cancellationToken,
        TaskContinuationOptions continuationOptions,
        TaskScheduler scheduler)
    {
        RequireReceiver(factory, ContinueWhenAllApi);
        ArgumentNullException.ThrowIfNull(continuationAction);
        return WhenAll(
            tasks,
            continuationAction,
            cancellationToken,
            continuationOptions,
            scheduler,
            ContinueWhenAllApi,
            ContinueAll<Task<TAntecedentResult>>,
            CanceledTask);
    }

    // ---- TaskFactory.ContinueWhenAll<TResult>(Task[], Func<Task[], TResult>) ----

    /// <summary>Controlled <c>TaskFactory.ContinueWhenAll&lt;TResult&gt;(Task[], Func&lt;Task[], TResult&gt;)</c>.</summary>
    public static Task<TResult> ContinueWhenAll<TResult>(TaskFactory factory, Task[] tasks, Func<Task[], TResult> continuationFunction) =>
        ContinueWhenAll(RequiredReceiver(factory, ContinueWhenAllApi), tasks, continuationFunction, factory.CancellationToken, factory.ContinuationOptions, DefaultScheduler(factory));

    /// <summary>Controlled <c>TaskFactory.ContinueWhenAll&lt;TResult&gt;(Task[], Func&lt;Task[], TResult&gt;, CancellationToken)</c>.</summary>
    public static Task<TResult> ContinueWhenAll<TResult>(
        TaskFactory factory,
        Task[] tasks,
        Func<Task[], TResult> continuationFunction,
        CancellationToken cancellationToken) =>
        ContinueWhenAll(RequiredReceiver(factory, ContinueWhenAllApi), tasks, continuationFunction, cancellationToken, factory.ContinuationOptions, DefaultScheduler(factory));

    /// <summary>Controlled <c>TaskFactory.ContinueWhenAll&lt;TResult&gt;(Task[], Func&lt;Task[], TResult&gt;, TaskContinuationOptions)</c>.</summary>
    public static Task<TResult> ContinueWhenAll<TResult>(
        TaskFactory factory,
        Task[] tasks,
        Func<Task[], TResult> continuationFunction,
        TaskContinuationOptions continuationOptions) =>
        ContinueWhenAll(RequiredReceiver(factory, ContinueWhenAllApi), tasks, continuationFunction, factory.CancellationToken, continuationOptions, DefaultScheduler(factory));

    /// <summary>Controlled <c>TaskFactory.ContinueWhenAll&lt;TResult&gt;(Task[], Func&lt;Task[], TResult&gt;, CancellationToken, TaskContinuationOptions, TaskScheduler)</c>.</summary>
    public static Task<TResult> ContinueWhenAll<TResult>(
        TaskFactory factory,
        Task[] tasks,
        Func<Task[], TResult> continuationFunction,
        CancellationToken cancellationToken,
        TaskContinuationOptions continuationOptions,
        TaskScheduler scheduler)
    {
        RequireReceiver(factory, ContinueWhenAllApi);
        ArgumentNullException.ThrowIfNull(continuationFunction);
        return WhenAll(tasks, continuationFunction, cancellationToken, continuationOptions, scheduler, ContinueWhenAllApi, ContinueAll<Task, TResult>, CanceledTask<TResult>);
    }

    // ---- TaskFactory.ContinueWhenAll<TAntecedentResult, TResult>(Task<TAntecedentResult>[], Func<Task<TAntecedentResult>[], TResult>) ----

    /// <summary>Controlled <c>TaskFactory.ContinueWhenAll&lt;TAntecedentResult, TResult&gt;(Task&lt;TAntecedentResult&gt;[], Func&lt;Task&lt;TAntecedentResult&gt;[], TResult&gt;)</c>.</summary>
    public static Task<TResult> ContinueWhenAll<TAntecedentResult, TResult>(
        TaskFactory factory,
        Task<TAntecedentResult>[] tasks,
        Func<Task<TAntecedentResult>[], TResult> continuationFunction) =>
        ContinueWhenAll(RequiredReceiver(factory, ContinueWhenAllApi), tasks, continuationFunction, factory.CancellationToken, factory.ContinuationOptions, DefaultScheduler(factory));

    /// <summary>Controlled <c>TaskFactory.ContinueWhenAll&lt;TAntecedentResult, TResult&gt;(Task&lt;TAntecedentResult&gt;[], Func&lt;Task&lt;TAntecedentResult&gt;[], TResult&gt;, CancellationToken)</c>.</summary>
    public static Task<TResult> ContinueWhenAll<TAntecedentResult, TResult>(
        TaskFactory factory,
        Task<TAntecedentResult>[] tasks,
        Func<Task<TAntecedentResult>[], TResult> continuationFunction,
        CancellationToken cancellationToken) =>
        ContinueWhenAll(RequiredReceiver(factory, ContinueWhenAllApi), tasks, continuationFunction, cancellationToken, factory.ContinuationOptions, DefaultScheduler(factory));

    /// <summary>Controlled <c>TaskFactory.ContinueWhenAll&lt;TAntecedentResult, TResult&gt;(Task&lt;TAntecedentResult&gt;[], Func&lt;Task&lt;TAntecedentResult&gt;[], TResult&gt;, TaskContinuationOptions)</c>.</summary>
    public static Task<TResult> ContinueWhenAll<TAntecedentResult, TResult>(
        TaskFactory factory,
        Task<TAntecedentResult>[] tasks,
        Func<Task<TAntecedentResult>[], TResult> continuationFunction,
        TaskContinuationOptions continuationOptions) =>
        ContinueWhenAll(RequiredReceiver(factory, ContinueWhenAllApi), tasks, continuationFunction, factory.CancellationToken, continuationOptions, DefaultScheduler(factory));

    /// <summary>Controlled <c>TaskFactory.ContinueWhenAll&lt;TAntecedentResult, TResult&gt;(Task&lt;TAntecedentResult&gt;[], Func&lt;Task&lt;TAntecedentResult&gt;[], TResult&gt;, CancellationToken, TaskContinuationOptions, TaskScheduler)</c>.</summary>
    public static Task<TResult> ContinueWhenAll<TAntecedentResult, TResult>(
        TaskFactory factory,
        Task<TAntecedentResult>[] tasks,
        Func<Task<TAntecedentResult>[], TResult> continuationFunction,
        CancellationToken cancellationToken,
        TaskContinuationOptions continuationOptions,
        TaskScheduler scheduler)
    {
        RequireReceiver(factory, ContinueWhenAllApi);
        ArgumentNullException.ThrowIfNull(continuationFunction);
        return WhenAll(
            tasks,
            continuationFunction,
            cancellationToken,
            continuationOptions,
            scheduler,
            ContinueWhenAllApi,
            ContinueAll<Task<TAntecedentResult>, TResult>,
            CanceledTask<TResult>);
    }

    // ---- TaskFactory.ContinueWhenAny(Task[], Action<Task>) ----

    /// <summary>Controlled <c>TaskFactory.ContinueWhenAny(Task[], Action&lt;Task&gt;)</c>.</summary>
    public static Task ContinueWhenAny(TaskFactory factory, Task[] tasks, Action<Task> continuationAction) =>
        ContinueWhenAny(RequiredReceiver(factory, ContinueWhenAnyApi), tasks, continuationAction, factory.CancellationToken, factory.ContinuationOptions, DefaultScheduler(factory));

    /// <summary>Controlled <c>TaskFactory.ContinueWhenAny(Task[], Action&lt;Task&gt;, CancellationToken)</c>.</summary>
    public static Task ContinueWhenAny(
        TaskFactory factory,
        Task[] tasks,
        Action<Task> continuationAction,
        CancellationToken cancellationToken) =>
        ContinueWhenAny(RequiredReceiver(factory, ContinueWhenAnyApi), tasks, continuationAction, cancellationToken, factory.ContinuationOptions, DefaultScheduler(factory));

    /// <summary>Controlled <c>TaskFactory.ContinueWhenAny(Task[], Action&lt;Task&gt;, TaskContinuationOptions)</c>.</summary>
    public static Task ContinueWhenAny(
        TaskFactory factory,
        Task[] tasks,
        Action<Task> continuationAction,
        TaskContinuationOptions continuationOptions) =>
        ContinueWhenAny(RequiredReceiver(factory, ContinueWhenAnyApi), tasks, continuationAction, factory.CancellationToken, continuationOptions, DefaultScheduler(factory));

    /// <summary>Controlled <c>TaskFactory.ContinueWhenAny(Task[], Action&lt;Task&gt;, CancellationToken, TaskContinuationOptions, TaskScheduler)</c>.</summary>
    public static Task ContinueWhenAny(
        TaskFactory factory,
        Task[] tasks,
        Action<Task> continuationAction,
        CancellationToken cancellationToken,
        TaskContinuationOptions continuationOptions,
        TaskScheduler scheduler)
    {
        RequireReceiver(factory, ContinueWhenAnyApi);
        ArgumentNullException.ThrowIfNull(continuationAction);
        return WhenAny(tasks, continuationAction, cancellationToken, continuationOptions, scheduler, ContinueWhenAnyApi, ContinueAny<Task>, CanceledTask);
    }

    // ---- TaskFactory.ContinueWhenAny<TResult>(Task[], Func<Task, TResult>) ----

    /// <summary>Controlled <c>TaskFactory.ContinueWhenAny&lt;TResult&gt;(Task[], Func&lt;Task, TResult&gt;)</c>.</summary>
    public static Task<TResult> ContinueWhenAny<TResult>(TaskFactory factory, Task[] tasks, Func<Task, TResult> continuationFunction) =>
        ContinueWhenAny(RequiredReceiver(factory, ContinueWhenAnyApi), tasks, continuationFunction, factory.CancellationToken, factory.ContinuationOptions, DefaultScheduler(factory));

    /// <summary>Controlled <c>TaskFactory.ContinueWhenAny&lt;TResult&gt;(Task[], Func&lt;Task, TResult&gt;, CancellationToken)</c>.</summary>
    public static Task<TResult> ContinueWhenAny<TResult>(
        TaskFactory factory,
        Task[] tasks,
        Func<Task, TResult> continuationFunction,
        CancellationToken cancellationToken) =>
        ContinueWhenAny(RequiredReceiver(factory, ContinueWhenAnyApi), tasks, continuationFunction, cancellationToken, factory.ContinuationOptions, DefaultScheduler(factory));

    /// <summary>Controlled <c>TaskFactory.ContinueWhenAny&lt;TResult&gt;(Task[], Func&lt;Task, TResult&gt;, TaskContinuationOptions)</c>.</summary>
    public static Task<TResult> ContinueWhenAny<TResult>(
        TaskFactory factory,
        Task[] tasks,
        Func<Task, TResult> continuationFunction,
        TaskContinuationOptions continuationOptions) =>
        ContinueWhenAny(RequiredReceiver(factory, ContinueWhenAnyApi), tasks, continuationFunction, factory.CancellationToken, continuationOptions, DefaultScheduler(factory));

    /// <summary>Controlled <c>TaskFactory.ContinueWhenAny&lt;TResult&gt;(Task[], Func&lt;Task, TResult&gt;, CancellationToken, TaskContinuationOptions, TaskScheduler)</c>.</summary>
    public static Task<TResult> ContinueWhenAny<TResult>(
        TaskFactory factory,
        Task[] tasks,
        Func<Task, TResult> continuationFunction,
        CancellationToken cancellationToken,
        TaskContinuationOptions continuationOptions,
        TaskScheduler scheduler)
    {
        RequireReceiver(factory, ContinueWhenAnyApi);
        ArgumentNullException.ThrowIfNull(continuationFunction);
        return WhenAny(tasks, continuationFunction, cancellationToken, continuationOptions, scheduler, ContinueWhenAnyApi, ContinueAny<Task, TResult>, CanceledTask<TResult>);
    }

    // ---- TaskFactory.ContinueWhenAny<TAntecedentResult, TResult>(Task<TAntecedentResult>[], Func<Task<TAntecedentResult>, TResult>) ----

    /// <summary>Controlled <c>TaskFactory.ContinueWhenAny&lt;TAntecedentResult, TResult&gt;(Task&lt;TAntecedentResult&gt;[], Func&lt;Task&lt;TAntecedentResult&gt;, TResult&gt;)</c>.</summary>
    public static Task<TResult> ContinueWhenAny<TAntecedentResult, TResult>(
        TaskFactory factory,
        Task<TAntecedentResult>[] tasks,
        Func<Task<TAntecedentResult>, TResult> continuationFunction) =>
        ContinueWhenAny(RequiredReceiver(factory, ContinueWhenAnyApi), tasks, continuationFunction, factory.CancellationToken, factory.ContinuationOptions, DefaultScheduler(factory));

    /// <summary>Controlled <c>TaskFactory.ContinueWhenAny&lt;TAntecedentResult, TResult&gt;(Task&lt;TAntecedentResult&gt;[], Func&lt;Task&lt;TAntecedentResult&gt;, TResult&gt;, CancellationToken)</c>.</summary>
    public static Task<TResult> ContinueWhenAny<TAntecedentResult, TResult>(
        TaskFactory factory,
        Task<TAntecedentResult>[] tasks,
        Func<Task<TAntecedentResult>, TResult> continuationFunction,
        CancellationToken cancellationToken) =>
        ContinueWhenAny(RequiredReceiver(factory, ContinueWhenAnyApi), tasks, continuationFunction, cancellationToken, factory.ContinuationOptions, DefaultScheduler(factory));

    /// <summary>Controlled <c>TaskFactory.ContinueWhenAny&lt;TAntecedentResult, TResult&gt;(Task&lt;TAntecedentResult&gt;[], Func&lt;Task&lt;TAntecedentResult&gt;, TResult&gt;, TaskContinuationOptions)</c>.</summary>
    public static Task<TResult> ContinueWhenAny<TAntecedentResult, TResult>(
        TaskFactory factory,
        Task<TAntecedentResult>[] tasks,
        Func<Task<TAntecedentResult>, TResult> continuationFunction,
        TaskContinuationOptions continuationOptions) =>
        ContinueWhenAny(RequiredReceiver(factory, ContinueWhenAnyApi), tasks, continuationFunction, factory.CancellationToken, continuationOptions, DefaultScheduler(factory));

    /// <summary>Controlled <c>TaskFactory.ContinueWhenAny&lt;TAntecedentResult, TResult&gt;(Task&lt;TAntecedentResult&gt;[], Func&lt;Task&lt;TAntecedentResult&gt;, TResult&gt;, CancellationToken, TaskContinuationOptions, TaskScheduler)</c>.</summary>
    public static Task<TResult> ContinueWhenAny<TAntecedentResult, TResult>(
        TaskFactory factory,
        Task<TAntecedentResult>[] tasks,
        Func<Task<TAntecedentResult>, TResult> continuationFunction,
        CancellationToken cancellationToken,
        TaskContinuationOptions continuationOptions,
        TaskScheduler scheduler)
    {
        RequireReceiver(factory, ContinueWhenAnyApi);
        ArgumentNullException.ThrowIfNull(continuationFunction);
        return WhenAny(
            tasks,
            continuationFunction,
            cancellationToken,
            continuationOptions,
            scheduler,
            ContinueWhenAnyApi,
            ContinueAny<Task<TAntecedentResult>, TResult>,
            CanceledTask<TResult>);
    }

    // ---- TaskFactory.ContinueWhenAny<TAntecedentResult>(Task<TAntecedentResult>[], Action<Task<TAntecedentResult>>) ----

    /// <summary>Controlled <c>TaskFactory.ContinueWhenAny&lt;TAntecedentResult&gt;(Task&lt;TAntecedentResult&gt;[], Action&lt;Task&lt;TAntecedentResult&gt;&gt;)</c>.</summary>
    public static Task ContinueWhenAny<TAntecedentResult>(
        TaskFactory factory,
        Task<TAntecedentResult>[] tasks,
        Action<Task<TAntecedentResult>> continuationAction) =>
        ContinueWhenAny(RequiredReceiver(factory, ContinueWhenAnyApi), tasks, continuationAction, factory.CancellationToken, factory.ContinuationOptions, DefaultScheduler(factory));

    /// <summary>Controlled <c>TaskFactory.ContinueWhenAny&lt;TAntecedentResult&gt;(Task&lt;TAntecedentResult&gt;[], Action&lt;Task&lt;TAntecedentResult&gt;&gt;, CancellationToken)</c>.</summary>
    public static Task ContinueWhenAny<TAntecedentResult>(
        TaskFactory factory,
        Task<TAntecedentResult>[] tasks,
        Action<Task<TAntecedentResult>> continuationAction,
        CancellationToken cancellationToken) =>
        ContinueWhenAny(RequiredReceiver(factory, ContinueWhenAnyApi), tasks, continuationAction, cancellationToken, factory.ContinuationOptions, DefaultScheduler(factory));

    /// <summary>Controlled <c>TaskFactory.ContinueWhenAny&lt;TAntecedentResult&gt;(Task&lt;TAntecedentResult&gt;[], Action&lt;Task&lt;TAntecedentResult&gt;&gt;, TaskContinuationOptions)</c>.</summary>
    public static Task ContinueWhenAny<TAntecedentResult>(
        TaskFactory factory,
        Task<TAntecedentResult>[] tasks,
        Action<Task<TAntecedentResult>> continuationAction,
        TaskContinuationOptions continuationOptions) =>
        ContinueWhenAny(RequiredReceiver(factory, ContinueWhenAnyApi), tasks, continuationAction, factory.CancellationToken, continuationOptions, DefaultScheduler(factory));

    /// <summary>Controlled <c>TaskFactory.ContinueWhenAny&lt;TAntecedentResult&gt;(Task&lt;TAntecedentResult&gt;[], Action&lt;Task&lt;TAntecedentResult&gt;&gt;, CancellationToken, TaskContinuationOptions, TaskScheduler)</c>.</summary>
    public static Task ContinueWhenAny<TAntecedentResult>(
        TaskFactory factory,
        Task<TAntecedentResult>[] tasks,
        Action<Task<TAntecedentResult>> continuationAction,
        CancellationToken cancellationToken,
        TaskContinuationOptions continuationOptions,
        TaskScheduler scheduler)
    {
        RequireReceiver(factory, ContinueWhenAnyApi);
        ArgumentNullException.ThrowIfNull(continuationAction);
        return WhenAny(
            tasks,
            continuationAction,
            cancellationToken,
            continuationOptions,
            scheduler,
            ContinueWhenAnyApi,
            ContinueAny<Task<TAntecedentResult>>,
            CanceledTask);
    }

    // ---- TaskFactory<TResult>.ContinueWhenAll(Task[], Func<Task[], TResult>) ----

    /// <summary>Controlled <c>TaskFactory&lt;TResult&gt;.ContinueWhenAll(Task[], Func&lt;Task[], TResult&gt;)</c>.</summary>
    public static Task<TResult> ContinueWhenAll<TResult>(TaskFactory<TResult> factory, Task[] tasks, Func<Task[], TResult> continuationFunction) =>
        ContinueWhenAll(RequiredReceiver(factory, GenericContinueWhenAllApi), tasks, continuationFunction, factory.CancellationToken, factory.ContinuationOptions, DefaultScheduler(factory));

    /// <summary>Controlled <c>TaskFactory&lt;TResult&gt;.ContinueWhenAll(Task[], Func&lt;Task[], TResult&gt;, CancellationToken)</c>.</summary>
    public static Task<TResult> ContinueWhenAll<TResult>(
        TaskFactory<TResult> factory,
        Task[] tasks,
        Func<Task[], TResult> continuationFunction,
        CancellationToken cancellationToken) =>
        ContinueWhenAll(RequiredReceiver(factory, GenericContinueWhenAllApi), tasks, continuationFunction, cancellationToken, factory.ContinuationOptions, DefaultScheduler(factory));

    /// <summary>Controlled <c>TaskFactory&lt;TResult&gt;.ContinueWhenAll(Task[], Func&lt;Task[], TResult&gt;, TaskContinuationOptions)</c>.</summary>
    public static Task<TResult> ContinueWhenAll<TResult>(
        TaskFactory<TResult> factory,
        Task[] tasks,
        Func<Task[], TResult> continuationFunction,
        TaskContinuationOptions continuationOptions) =>
        ContinueWhenAll(RequiredReceiver(factory, GenericContinueWhenAllApi), tasks, continuationFunction, factory.CancellationToken, continuationOptions, DefaultScheduler(factory));

    /// <summary>Controlled <c>TaskFactory&lt;TResult&gt;.ContinueWhenAll(Task[], Func&lt;Task[], TResult&gt;, CancellationToken, TaskContinuationOptions, TaskScheduler)</c>.</summary>
    public static Task<TResult> ContinueWhenAll<TResult>(
        TaskFactory<TResult> factory,
        Task[] tasks,
        Func<Task[], TResult> continuationFunction,
        CancellationToken cancellationToken,
        TaskContinuationOptions continuationOptions,
        TaskScheduler scheduler)
    {
        RequireReceiver(factory, GenericContinueWhenAllApi);
        ArgumentNullException.ThrowIfNull(continuationFunction);
        return WhenAll(tasks, continuationFunction, cancellationToken, continuationOptions, scheduler, GenericContinueWhenAllApi, ContinueAll<Task, TResult>, CanceledTask<TResult>);
    }

    // ---- TaskFactory<TResult>.ContinueWhenAll<TAntecedentResult>(Task<TAntecedentResult>[], Func<Task<TAntecedentResult>[], TResult>) ----

    /// <summary>
    /// Controlled <c>TaskFactory&lt;TResult&gt;.ContinueWhenAll&lt;TAntecedentResult&gt;(Task&lt;TAntecedentResult&gt;[], Func&lt;Task&lt;TAntecedentResult&gt;[], TResult&gt;)</c>.
    /// The call-site pass binds the type parameters declaring-type-first (<typeparamref name="TResult"/>) then
    /// method-argument (<typeparamref name="TAntecedentResult"/>).
    /// </summary>
    public static Task<TResult> ContinueWhenAll<TResult, TAntecedentResult>(
        TaskFactory<TResult> factory,
        Task<TAntecedentResult>[] tasks,
        Func<Task<TAntecedentResult>[], TResult> continuationFunction) =>
        ContinueWhenAll(RequiredReceiver(factory, GenericContinueWhenAllApi), tasks, continuationFunction, factory.CancellationToken, factory.ContinuationOptions, DefaultScheduler(factory));

    /// <summary>Controlled <c>TaskFactory&lt;TResult&gt;.ContinueWhenAll&lt;TAntecedentResult&gt;(Task&lt;TAntecedentResult&gt;[], Func&lt;Task&lt;TAntecedentResult&gt;[], TResult&gt;, CancellationToken)</c>.</summary>
    public static Task<TResult> ContinueWhenAll<TResult, TAntecedentResult>(
        TaskFactory<TResult> factory,
        Task<TAntecedentResult>[] tasks,
        Func<Task<TAntecedentResult>[], TResult> continuationFunction,
        CancellationToken cancellationToken) =>
        ContinueWhenAll(RequiredReceiver(factory, GenericContinueWhenAllApi), tasks, continuationFunction, cancellationToken, factory.ContinuationOptions, DefaultScheduler(factory));

    /// <summary>Controlled <c>TaskFactory&lt;TResult&gt;.ContinueWhenAll&lt;TAntecedentResult&gt;(Task&lt;TAntecedentResult&gt;[], Func&lt;Task&lt;TAntecedentResult&gt;[], TResult&gt;, TaskContinuationOptions)</c>.</summary>
    public static Task<TResult> ContinueWhenAll<TResult, TAntecedentResult>(
        TaskFactory<TResult> factory,
        Task<TAntecedentResult>[] tasks,
        Func<Task<TAntecedentResult>[], TResult> continuationFunction,
        TaskContinuationOptions continuationOptions) =>
        ContinueWhenAll(RequiredReceiver(factory, GenericContinueWhenAllApi), tasks, continuationFunction, factory.CancellationToken, continuationOptions, DefaultScheduler(factory));

    /// <summary>Controlled <c>TaskFactory&lt;TResult&gt;.ContinueWhenAll&lt;TAntecedentResult&gt;(Task&lt;TAntecedentResult&gt;[], Func&lt;Task&lt;TAntecedentResult&gt;[], TResult&gt;, CancellationToken, TaskContinuationOptions, TaskScheduler)</c>.</summary>
    public static Task<TResult> ContinueWhenAll<TResult, TAntecedentResult>(
        TaskFactory<TResult> factory,
        Task<TAntecedentResult>[] tasks,
        Func<Task<TAntecedentResult>[], TResult> continuationFunction,
        CancellationToken cancellationToken,
        TaskContinuationOptions continuationOptions,
        TaskScheduler scheduler)
    {
        RequireReceiver(factory, GenericContinueWhenAllApi);
        ArgumentNullException.ThrowIfNull(continuationFunction);
        return WhenAll(
            tasks,
            continuationFunction,
            cancellationToken,
            continuationOptions,
            scheduler,
            GenericContinueWhenAllApi,
            ContinueAll<Task<TAntecedentResult>, TResult>,
            CanceledTask<TResult>);
    }

    // ---- TaskFactory<TResult>.ContinueWhenAny(Task[], Func<Task, TResult>) ----

    /// <summary>Controlled <c>TaskFactory&lt;TResult&gt;.ContinueWhenAny(Task[], Func&lt;Task, TResult&gt;)</c>.</summary>
    public static Task<TResult> ContinueWhenAny<TResult>(TaskFactory<TResult> factory, Task[] tasks, Func<Task, TResult> continuationFunction) =>
        ContinueWhenAny(RequiredReceiver(factory, GenericContinueWhenAnyApi), tasks, continuationFunction, factory.CancellationToken, factory.ContinuationOptions, DefaultScheduler(factory));

    /// <summary>Controlled <c>TaskFactory&lt;TResult&gt;.ContinueWhenAny(Task[], Func&lt;Task, TResult&gt;, CancellationToken)</c>.</summary>
    public static Task<TResult> ContinueWhenAny<TResult>(
        TaskFactory<TResult> factory,
        Task[] tasks,
        Func<Task, TResult> continuationFunction,
        CancellationToken cancellationToken) =>
        ContinueWhenAny(RequiredReceiver(factory, GenericContinueWhenAnyApi), tasks, continuationFunction, cancellationToken, factory.ContinuationOptions, DefaultScheduler(factory));

    /// <summary>Controlled <c>TaskFactory&lt;TResult&gt;.ContinueWhenAny(Task[], Func&lt;Task, TResult&gt;, TaskContinuationOptions)</c>.</summary>
    public static Task<TResult> ContinueWhenAny<TResult>(
        TaskFactory<TResult> factory,
        Task[] tasks,
        Func<Task, TResult> continuationFunction,
        TaskContinuationOptions continuationOptions) =>
        ContinueWhenAny(RequiredReceiver(factory, GenericContinueWhenAnyApi), tasks, continuationFunction, factory.CancellationToken, continuationOptions, DefaultScheduler(factory));

    /// <summary>Controlled <c>TaskFactory&lt;TResult&gt;.ContinueWhenAny(Task[], Func&lt;Task, TResult&gt;, CancellationToken, TaskContinuationOptions, TaskScheduler)</c>.</summary>
    public static Task<TResult> ContinueWhenAny<TResult>(
        TaskFactory<TResult> factory,
        Task[] tasks,
        Func<Task, TResult> continuationFunction,
        CancellationToken cancellationToken,
        TaskContinuationOptions continuationOptions,
        TaskScheduler scheduler)
    {
        RequireReceiver(factory, GenericContinueWhenAnyApi);
        ArgumentNullException.ThrowIfNull(continuationFunction);
        return WhenAny(tasks, continuationFunction, cancellationToken, continuationOptions, scheduler, GenericContinueWhenAnyApi, ContinueAny<Task, TResult>, CanceledTask<TResult>);
    }

    // ---- TaskFactory<TResult>.ContinueWhenAny<TAntecedentResult>(Task<TAntecedentResult>[], Func<Task<TAntecedentResult>, TResult>) ----

    /// <summary>
    /// Controlled <c>TaskFactory&lt;TResult&gt;.ContinueWhenAny&lt;TAntecedentResult&gt;(Task&lt;TAntecedentResult&gt;[], Func&lt;Task&lt;TAntecedentResult&gt;, TResult&gt;)</c>.
    /// The call-site pass binds the type parameters declaring-type-first (<typeparamref name="TResult"/>) then
    /// method-argument (<typeparamref name="TAntecedentResult"/>).
    /// </summary>
    public static Task<TResult> ContinueWhenAny<TResult, TAntecedentResult>(
        TaskFactory<TResult> factory,
        Task<TAntecedentResult>[] tasks,
        Func<Task<TAntecedentResult>, TResult> continuationFunction) =>
        ContinueWhenAny(RequiredReceiver(factory, GenericContinueWhenAnyApi), tasks, continuationFunction, factory.CancellationToken, factory.ContinuationOptions, DefaultScheduler(factory));

    /// <summary>Controlled <c>TaskFactory&lt;TResult&gt;.ContinueWhenAny&lt;TAntecedentResult&gt;(Task&lt;TAntecedentResult&gt;[], Func&lt;Task&lt;TAntecedentResult&gt;, TResult&gt;, CancellationToken)</c>.</summary>
    public static Task<TResult> ContinueWhenAny<TResult, TAntecedentResult>(
        TaskFactory<TResult> factory,
        Task<TAntecedentResult>[] tasks,
        Func<Task<TAntecedentResult>, TResult> continuationFunction,
        CancellationToken cancellationToken) =>
        ContinueWhenAny(RequiredReceiver(factory, GenericContinueWhenAnyApi), tasks, continuationFunction, cancellationToken, factory.ContinuationOptions, DefaultScheduler(factory));

    /// <summary>Controlled <c>TaskFactory&lt;TResult&gt;.ContinueWhenAny&lt;TAntecedentResult&gt;(Task&lt;TAntecedentResult&gt;[], Func&lt;Task&lt;TAntecedentResult&gt;, TResult&gt;, TaskContinuationOptions)</c>.</summary>
    public static Task<TResult> ContinueWhenAny<TResult, TAntecedentResult>(
        TaskFactory<TResult> factory,
        Task<TAntecedentResult>[] tasks,
        Func<Task<TAntecedentResult>, TResult> continuationFunction,
        TaskContinuationOptions continuationOptions) =>
        ContinueWhenAny(RequiredReceiver(factory, GenericContinueWhenAnyApi), tasks, continuationFunction, factory.CancellationToken, continuationOptions, DefaultScheduler(factory));

    /// <summary>Controlled <c>TaskFactory&lt;TResult&gt;.ContinueWhenAny&lt;TAntecedentResult&gt;(Task&lt;TAntecedentResult&gt;[], Func&lt;Task&lt;TAntecedentResult&gt;, TResult&gt;, CancellationToken, TaskContinuationOptions, TaskScheduler)</c>.</summary>
    public static Task<TResult> ContinueWhenAny<TResult, TAntecedentResult>(
        TaskFactory<TResult> factory,
        Task<TAntecedentResult>[] tasks,
        Func<Task<TAntecedentResult>, TResult> continuationFunction,
        CancellationToken cancellationToken,
        TaskContinuationOptions continuationOptions,
        TaskScheduler scheduler)
    {
        RequireReceiver(factory, GenericContinueWhenAnyApi);
        ArgumentNullException.ThrowIfNull(continuationFunction);
        return WhenAny(
            tasks,
            continuationFunction,
            cancellationToken,
            continuationOptions,
            scheduler,
            GenericContinueWhenAnyApi,
            ContinueAny<Task<TAntecedentResult>, TResult>,
            CanceledTask<TResult>);
    }

    /// <summary>
    /// Rejects <c>TaskFactory.FromAsync</c> and <c>TaskFactory&lt;TResult&gt;.FromAsync</c>, every overload, at the
    /// rewritten call site. The returned task completes when the <see cref="IAsyncResult"/> completes, and an
    /// <see cref="IAsyncResult"/> completes on whatever thread its implementation chooses, outside Clockwork's control.
    /// </summary>
    /// <param name="apiName">The unsupported API, supplied by the rewriter.</param>
    public static void RejectFromAsync(string apiName)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(apiName);
        throw new SimulationApiException(
            SimulationApiCategory.Task,
            apiName,
            "the task it returns completes when an IAsyncResult completes, and an IAsyncResult completes on a thread " +
            "of its implementation's choosing, outside Clockwork's control. Await a task-returning API, or a " +
            "TaskCompletionSource which the simulation completes, instead.");
    }

    /// <summary>
    /// Validates the arguments as the BCL's <c>ContinueWhenAll</c> does, then continues once the coordinator observes
    /// every antecedent complete. The continuation receives a defensive copy of the antecedents, and the user's delegate
    /// is its <see cref="Task.AsyncState"/>, as in the BCL.
    /// </summary>
    private static TContinuation WhenAll<TAntecedent, TContinuation>(
        TAntecedent[] tasks,
        Delegate continuation,
        CancellationToken cancellationToken,
        TaskContinuationOptions continuationOptions,
        TaskScheduler scheduler,
        string apiName,
        Func<Task, TAntecedent[], Delegate, CancellationToken, TaskContinuationOptions, TContinuation> continueGate,
        Func<TaskCreationOptions, CancellationToken, TContinuation> canceledTask)
        where TAntecedent : Task
        where TContinuation : Task
    {
        CheckMultiTaskContinuationOptions(continuationOptions);
        ArgumentNullException.ThrowIfNull(tasks);
        ArgumentNullException.ThrowIfNull(scheduler);
        TAntecedent[] copy = CopyNonEmptyNonNull(tasks);
        SimulationExecutionSnapshot target = ControlledTask.RejectUnpreservableContinuation(
            continuationOptions,
            scheduler,
            SimulationRuntimeDispatch.RequireActiveSimulation(apiName),
            apiName);
        if (IsCanceledEagerly(cancellationToken, continuationOptions))
        {
            return canceledTask(CreationOptions(continuationOptions), cancellationToken);
        }

        int next = 0;
        return ControlledTask.ContinueGate(
            target,
            () =>
            {
                // Antecedents only ever become complete, so each check resumes from the first not yet observed complete.
                while (next < copy.Length && copy[next].IsCompleted)
                {
                    next++;
                }

                return next == copy.Length;
            },
            () =>
            {
                foreach (TAntecedent task in copy)
                {
                    RaceSynchronization.Wait(task);
                }

                return TaskStatus.RanToCompletion;
            },
            cancellationToken,
            continuationOptions,
            apiName,
            (gate, token, options) => continueGate(gate, copy, continuation, token, options));
    }

    /// <summary>
    /// Validates the arguments as the BCL's <c>ContinueWhenAny</c> does, then continues once the coordinator observes an
    /// antecedent complete, with the first antecedent, in argument order, among those observed complete. The BCL
    /// continues with the first antecedent to complete; the coordinator observes completions between scheduling steps,
    /// so among antecedents which complete in the same step the first in argument order wins, as in
    /// <see cref="ControlledTask.WhenAny(Task[])"/>.
    /// </summary>
    private static TContinuation WhenAny<TAntecedent, TContinuation>(
        TAntecedent[] tasks,
        Delegate continuation,
        CancellationToken cancellationToken,
        TaskContinuationOptions continuationOptions,
        TaskScheduler scheduler,
        string apiName,
        Func<Task, Func<TAntecedent>, Delegate, CancellationToken, TaskContinuationOptions, TContinuation> continueGate,
        Func<TaskCreationOptions, CancellationToken, TContinuation> canceledTask)
        where TAntecedent : Task
        where TContinuation : Task
    {
        CheckMultiTaskContinuationOptions(continuationOptions);
        ArgumentNullException.ThrowIfNull(tasks);
        if (tasks.Length == 0)
        {
            throw new ArgumentException("The tasks argument contains no tasks.", TasksParameter);
        }

        ArgumentNullException.ThrowIfNull(scheduler);
        TAntecedent[] copy = CopyNonEmptyNonNull(tasks);
        SimulationExecutionSnapshot target = ControlledTask.RejectUnpreservableContinuation(
            continuationOptions,
            scheduler,
            SimulationRuntimeDispatch.RequireActiveSimulation(apiName),
            apiName);
        if (IsCanceledEagerly(cancellationToken, continuationOptions))
        {
            return canceledTask(CreationOptions(continuationOptions), cancellationToken);
        }

        // Every antecedent is pending until the winner is observed, so each check costs O(pending).
        TAntecedent? winner = null;
        return ControlledTask.ContinueGate(
            target,
            () => (winner ??= FirstCompleted(copy)) is not null,
            () =>
            {
                RaceSynchronization.Wait(winner!);
                return TaskStatus.RanToCompletion;
            },
            cancellationToken,
            continuationOptions,
            apiName,
            (gate, token, options) => continueGate(gate, () => winner!, continuation, token, options));
    }

    private static Task ContinueAll<TAntecedent>(
        Task gate,
        TAntecedent[] completed,
        Delegate state,
        CancellationToken cancellationToken,
        TaskContinuationOptions continuationOptions)
        where TAntecedent : Task =>
        gate.ContinueWith(
            (_, action) => ((Action<TAntecedent[]>)action!)(completed),
            state,
            cancellationToken,
            continuationOptions,
            TaskScheduler.Default);

    private static Task<TResult> ContinueAll<TAntecedent, TResult>(
        Task gate,
        TAntecedent[] completed,
        Delegate state,
        CancellationToken cancellationToken,
        TaskContinuationOptions continuationOptions)
        where TAntecedent : Task =>
        gate.ContinueWith(
            (_, function) => ((Func<TAntecedent[], TResult>)function!)(completed),
            state,
            cancellationToken,
            continuationOptions,
            TaskScheduler.Default);

    private static Task ContinueAny<TAntecedent>(
        Task gate,
        Func<TAntecedent> winner,
        Delegate state,
        CancellationToken cancellationToken,
        TaskContinuationOptions continuationOptions)
        where TAntecedent : Task =>
        gate.ContinueWith(
            (_, action) => ((Action<TAntecedent>)action!)(winner()),
            state,
            cancellationToken,
            continuationOptions,
            TaskScheduler.Default);

    private static Task<TResult> ContinueAny<TAntecedent, TResult>(
        Task gate,
        Func<TAntecedent> winner,
        Delegate state,
        CancellationToken cancellationToken,
        TaskContinuationOptions continuationOptions)
        where TAntecedent : Task =>
        gate.ContinueWith(
            (_, function) => ((Func<TAntecedent, TResult>)function!)(winner()),
            state,
            cancellationToken,
            continuationOptions,
            TaskScheduler.Default);

    // The BCL returns a task canceled at creation, with the continuation's creation options and no state, when the token
    // is canceled and LazyCancellation is not requested. A task constructed with a canceled token is created canceled.
    private static Task CanceledTask(TaskCreationOptions creationOptions, CancellationToken cancellationToken) =>
        new(static () => { }, cancellationToken, creationOptions);

    private static Task<TResult> CanceledTask<TResult>(TaskCreationOptions creationOptions, CancellationToken cancellationToken) =>
        new(static () => default!, cancellationToken, creationOptions);

    private static bool IsCanceledEagerly(CancellationToken cancellationToken, TaskContinuationOptions continuationOptions) =>
        cancellationToken.IsCancellationRequested && (continuationOptions & TaskContinuationOptions.LazyCancellation) == 0;

    // The creation options the BCL derives from continuation options.
    private static TaskCreationOptions CreationOptions(TaskContinuationOptions continuationOptions) =>
        (TaskCreationOptions)(continuationOptions & (TaskContinuationOptions.PreferFairness | TaskContinuationOptions.LongRunning |
            TaskContinuationOptions.DenyChildAttach | TaskContinuationOptions.HideScheduler | TaskContinuationOptions.AttachedToParent |
            TaskContinuationOptions.RunContinuationsAsynchronously));

    /// <summary>Validates multi-task continuation options exactly as the BCL's <c>TaskFactory</c> does.</summary>
    private static void CheckMultiTaskContinuationOptions(TaskContinuationOptions continuationOptions)
    {
        const TaskContinuationOptions NotOnAny =
            TaskContinuationOptions.NotOnCanceled | TaskContinuationOptions.NotOnFaulted | TaskContinuationOptions.NotOnRanToCompletion;
        const TaskContinuationOptions IllegalMask = TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.LongRunning;

        if ((continuationOptions & IllegalMask) == IllegalMask)
        {
            throw new ArgumentOutOfRangeException(
                nameof(continuationOptions),
                "The specified TaskContinuationOptions combined LongRunning and ExecuteSynchronously.  Synchronous continuations should not be long running.");
        }

        if ((continuationOptions & ~(TaskContinuationOptions.LongRunning | TaskContinuationOptions.PreferFairness |
            TaskContinuationOptions.AttachedToParent | TaskContinuationOptions.DenyChildAttach | TaskContinuationOptions.HideScheduler |
            TaskContinuationOptions.LazyCancellation | NotOnAny | TaskContinuationOptions.ExecuteSynchronously)) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(continuationOptions));
        }

        if ((continuationOptions & NotOnAny) != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(continuationOptions),
                "It is invalid to exclude specific continuation kinds for continuations off of multiple tasks.");
        }
    }

    private static TAntecedent[] CopyNonEmptyNonNull<TAntecedent>(TAntecedent[] tasks)
        where TAntecedent : Task
    {
        if (tasks.Length == 0)
        {
            throw new ArgumentException("The tasks argument contains no tasks.", TasksParameter);
        }

        var copy = new TAntecedent[tasks.Length];
        for (int i = 0; i < tasks.Length; i++)
        {
            copy[i] = tasks[i] ?? throw new ArgumentException("The tasks argument included a null value.", TasksParameter);
        }

        return copy;
    }

    private static TAntecedent? FirstCompleted<TAntecedent>(TAntecedent[] tasks)
        where TAntecedent : Task
    {
        foreach (TAntecedent task in tasks)
        {
            if (task.IsCompleted)
            {
                return task;
            }
        }

        return null;
    }

    // The overloads without a scheduler use the factory's, or TaskScheduler.Default rather than TaskScheduler.Current,
    // as the controlled ContinueWith and StartNew do.
    private static TaskScheduler DefaultScheduler(TaskFactory factory) => factory.Scheduler ?? TaskScheduler.Default;

    private static TaskScheduler DefaultScheduler<TResult>(TaskFactory<TResult> factory) => factory.Scheduler ?? TaskScheduler.Default;

    // The BCL's members are instance methods: a null factory fails the call itself, with the runtime's
    // NullReferenceException, before any argument is validated.
    private static void RequireReceiver(object factory, string apiName)
    {
        SimulationRuntimeDispatch.RequireActiveSimulation(apiName);
        _ = factory.GetType();
    }

    // The overloads which take the factory's defaults pass the receiver through this first: C# evaluates arguments left to
    // right, so the call is checked before the later arguments read the factory.
    private static TFactory RequiredReceiver<TFactory>(TFactory factory, string apiName)
        where TFactory : class
    {
        RequireReceiver(factory, apiName);
        return factory;
    }
}

#pragma warning restore CA1068
